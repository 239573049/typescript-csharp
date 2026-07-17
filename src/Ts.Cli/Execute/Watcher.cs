// Ported from tsgo internal/execute/watcher.go — the plain-tsc watch session over
// watchmanager.WatchManager.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections;
using Ts.Core;
using Ts.Execute.WatchManagement;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Execute;

using PathT = global::Ts.TsPath.Path;
using TsPathApi = global::Ts.TsPath.TsPath;

// watcher.go:24-27
internal sealed class CachedSourceFile
{
    internal global::Ts.Ast.SourceFile file = null!;
    internal DateTime modTime;
}

// watcher.go:29-55 — mtime-validated source-file cache over the inner compiler host.
internal sealed class WatchCompilerHost : global::Ts.Compiler.CompilerHost
{
    internal global::Ts.Compiler.CompilerHost inner = null!;
    internal SyncMap<PathT, CachedSourceFile> cache = null!;

    public global::Ts.Vfs.IFileSystem FS() => inner.FS();

    public string DefaultLibraryPath() => inner.DefaultLibraryPath();

    public string GetCurrentDirectory() => inner.GetCurrentDirectory();

    public void Trace(global::Ts.Diagnostics.Message msg, params object?[] args) => inner.Trace(msg, args);

    public global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(string fileName, PathT path) => inner.GetResolvedProjectReference(fileName, path);

    // watcher.go:34-55
    public global::Ts.Ast.SourceFile? GetSourceFile(global::Ts.Ast.SourceFileParseOptions opts)
    {
        var info = inner.FS().Stat(opts.FileName);

        if (cache.TryLoad(opts.Path, out var cached))
        {
            if (info != null && info.ModTime() == cached!.modTime)
            {
                return cached.file;
            }
        }

        var file = inner.GetSourceFile(opts);
        if (file != null)
        {
            if (info != null)
            {
                cache.Store(opts.Path, new CachedSourceFile
                {
                    file = file,
                    modTime = info.ModTime(),
                });
            }
        }
        else
        {
            cache.Delete(opts.Path);
        }
        return file;
    }
}

// watcher.go:57-80
internal sealed class Watcher : IExecuteWatcher
{
    internal ISystem sys = null!;
    internal string configFileName = "";
    internal global::Ts.TsOptions.ParsedCommandLine config = null!;
    internal CompilerOptions? compilerOptionsFromCommandLine;
    internal global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw;
    internal DiagnosticReporter reportDiagnostic = null!;
    internal DiagnosticsReporter reportErrorSummary = null!;
    internal DiagnosticReporter reportWatchStatus = null!;
    internal ICommandLineTesting? testing;

    internal global::Ts.Execute.Incremental.Program? program;
    internal TscExtendedConfigCache? extendedConfigCache;
    internal bool configModified;
    internal bool configHasErrors;
    internal List<string> configFilePaths = new();

    internal SyncMap<PathT, CachedSourceFile> sourceFileCache = new();

    internal WatchManager wm = null!;
    internal Set<PathT> seenFiles = new(); // all build dependencies (for event filtering)
    internal Dictionary<string, DateTime> configMtimes = new(StringComparer.Ordinal);

    // watcher.go:84-112
    internal static Watcher CreateWatcher(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine configParseResult,
        CompilerOptions? compilerOptionsFromCommandLine,
        global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary,
        ICommandLineTesting? testing)
    {
        var wm = new WatchManager(sys.Writer(), sys.FS().DirectoryExists);
        if (testing is ICommandLineTestingWithWatchBackend t)
        {
            wm.SetBackend(t.WatchBackend());
        }
        var w = new Watcher
        {
            sys = sys,
            config = configParseResult,
            compilerOptionsFromCommandLine = compilerOptionsFromCommandLine,
            commandLineRaw = commandLineRaw,
            reportDiagnostic = reportDiagnostic,
            reportErrorSummary = reportErrorSummary,
            reportWatchStatus = DiagnosticReporters.CreateWatchStatusReporter(sys, global::Ts.Locale.Locale.Default, configParseResult.CompilerOptions()!, testing),
            testing = testing,
            wm = wm,
        };
        if (configParseResult.ConfigFile != null)
        {
            w.configFileName = configParseResult.ConfigFile.SourceFile.FileName();
        }
        return w;
    }

    // watcher.go:114-141
    internal void Start(CancellationToken ctx)
    {
        wm.Lock();
        extendedConfigCache = new TscExtendedConfigCache();
        var host = global::Ts.Compiler.CompilerHosts.NewCompilerHost(
            sys.GetCurrentDirectory(), sys.FS(), sys.DefaultLibraryPath(), extendedConfigCache,
            EmitAndReport.GetTraceWithWriterFromSys(sys.Writer(), global::Ts.Locale.Locale.Default, testing));
        program = global::Ts.Execute.Incremental.BuildInfoReaderHelpers.ReadBuildInfoProgram(
            config, global::Ts.Execute.Incremental.BuildInfoReaderHelpers.NewBuildInfoReader(host), host);

        if (configFileName.Length > 0)
        {
            configFilePaths = new List<string> { configFileName };
            configFilePaths.AddRange(config.ExtendedSourceFiles() ?? []);
        }

        if (sys.GetEnvironmentVariable("TS_WATCH_DEBUG").Length > 0)
        {
            wm.DebugLog = sys.Writer();
        }

        if (testing == null)
        {
            wm.EnsureDefaultBackend();
        }

        reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Starting_compilation_in_watch_mode));
        if (!DoBuild())
        {
            wm.ForceOverflow();
        }
        wm.Unlock();

        if (testing == null)
        {
            wm.RunLoop(ctx, DoCycle);
        }
    }

    // watcher.go:143-199
    internal Dictionary<string, bool> ComputeDesiredWatches(IReadOnlyList<string> seenFilePaths)
    {
        var cwd = sys.GetCurrentDirectory();

        var desiredDirs = new Dictionary<string, bool>(StringComparer.Ordinal); // dir → recursive

        // Wildcard directories from tsconfig (recursive or non-recursive)
        if (config.ConfigFile != null)
        {
            foreach (var (dir, recursive) in config.WildcardDirectories() ?? new Dictionary<string, bool>()) // map-iter: 序无关 (dir set)
            {
                var realDir = sys.FS().Realpath(dir);
                desiredDirs[realDir] = recursive;
            }
        }

        // For no-config CLI mode, ensure CWD is watched
        if (config.ConfigFile == null && desiredDirs.Count == 0)
        {
            var dir = sys.FS().Realpath(cwd);
            desiredDirs[dir] = false;
        }

        // Config file parent directories as non-recursive watches
        foreach (var cfgPath in configFilePaths)
        {
            var realPath = sys.FS().Realpath(cfgPath);
            var dir = TsPathApi.GetDirectoryPath(realPath);
            desiredDirs.TryAdd(dir, false);
        }

        // For no-config CLI mode, also watch the CLI-specified files' directories
        if (config.ConfigFile == null)
        {
            foreach (var fileName in config.FileNames() ?? [])
            {
                var absPath = TsPathApi.GetNormalizedAbsolutePath(fileName, cwd);
                var realPath = sys.FS().Realpath(absPath);
                var dir = TsPathApi.GetDirectoryPath(realPath);
                desiredDirs.TryAdd(dir, false);
            }
        }

        // Add parent directories for seen files not covered by existing dir watches.
        // Resolve ancestor fallbacks first so coverage checks use final dirs.
        var resolvedDirs = wm.ResolveDesiredDirs(desiredDirs);

        var opts = ComparePathsOptions();
        foreach (var filePath in seenFilePaths)
        {
            var dir = TsPathApi.GetDirectoryPath(filePath);
            if (!WatchManagerUtil.IsDirCoveredByWatch(resolvedDirs, dir, opts))
            {
                if (WatchPathUtil.CanWatchDirectory(dir))
                {
                    resolvedDirs[dir] = false;
                }
            }
        }

        // Re-resolve in case newly added dirs don't exist
        return wm.ResolveDesiredDirs(resolvedDirs);
    }

    // watcher.go:201-204
    private void ReconcileWatches(IReadOnlyList<string> seenFilePaths)
    {
        var desiredDirs = ComputeDesiredWatches(seenFilePaths);
        wm.ReconcileWatches(desiredDirs);
    }

    // watcher.go:206-211
    private global::Ts.TsPath.ComparePathsOptions ComparePathsOptions() => new()
    {
        UseCaseSensitiveFileNames = sys.FS().UseCaseSensitiveFileNames(),
        CurrentDirectory = sys.GetCurrentDirectory(),
    };

    // watcher.go:213-256
    public void DoCycle()
    {
        wm.Lock();
        try
        {
            var (changedPaths, overflow) = wm.DrainEvents();
            var hasEvents = (changedPaths?.Count ?? 0) > 0 || overflow;

            if (RecheckTsConfig())
            {
                return;
            }

            if (hasEvents && !overflow && !configModified)
            {
                // Filter fswatch events against known dependencies
                if (IsRelevantChange(changedPaths!))
                {
                    EvictChangedSourceFiles(changedPaths!);
                }
                else
                {
                    wm.DebugLog?.Write($"[watch] DoCycle: {changedPaths!.Count} event(s) not relevant to compilation, skipping rebuild\n");
                    testing?.OnProgram(program);
                    return;
                }
            }
            else if (overflow)
            {
                // Overflow: evict the entire source file cache to force re-build
                sourceFileCache = new SyncMap<PathT, CachedSourceFile>();
            }
            else if (!hasEvents && !configModified)
            {
                // No events and no config change
                wm.DebugLog?.Write("[watch] DoCycle: no events, skipping\n");
                testing?.OnProgram(program);
                return;
            }

            reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.File_change_detected_Starting_incremental_compilation));
            if (!DoBuild())
            {
                // Mid-cycle watch failure; force a full rebuild on the next event
                wm.ForceOverflow();
            }
        }
        finally
        {
            wm.Unlock();
        }
    }

    // watcher.go:258-280
    private bool IsRelevantChange(Dictionary<string, global::Ts.FsWatch.EventKind> changedPaths)
    {
        var caseSensitive = sys.FS().UseCaseSensitiveFileNames();
        var cwd = sys.GetCurrentDirectory();
        var opts = ComparePathsOptions();
        foreach (var eventPath in changedPaths.Keys) // map-iter: 序无关 (existence check)
        {
            var p = TsPathApi.ToPath(eventPath, cwd, caseSensitive);
            if (seenFiles.Has(p))
            {
                return true;
            }
            if (config.ConfigFile != null && config.PossiblyMatchesFileName(eventPath))
            {
                return true;
            }
            if (config.ConfigFile != null && config.PossiblyMatchesDirectoryName(p))
            {
                return true;
            }
            if (sys.FS().DirectoryExists(eventPath))
            {
                if (wm.IsPathUnderWatch(eventPath, opts))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // watcher.go:282-353 — Go returns error; C# returns false on watch-reconcile failure.
    private bool DoBuild()
    {
        if (configModified)
        {
            sourceFileCache = new SyncMap<PathT, CachedSourceFile>();
        }

        var cached = global::Ts.Vfs.CachedVfs.CachedFs.From(sys.FS());
        var tfs = new global::Ts.Vfs.TrackingVfs.TrackingFs { Inner = cached };
        var innerHost = global::Ts.Compiler.CompilerHosts.NewCompilerHost(
            sys.GetCurrentDirectory(), tfs, sys.DefaultLibraryPath(), extendedConfigCache,
            EmitAndReport.GetTraceWithWriterFromSys(sys.Writer(), global::Ts.Locale.Locale.Default, testing));
        var host = new WatchCompilerHost { inner = innerHost, cache = sourceFileCache };

        if (config.ConfigFile != null)
        {
            var wildcardDirs = config.WildcardDirectories();
            foreach (var dir in (wildcardDirs ?? new Dictionary<string, bool>()).Keys) // map-iter: 序无关 (seen-file set)
            {
                tfs.SeenFiles.Add(dir);
            }
            if (wildcardDirs is { Count: > 0 })
            {
                config = config.ReloadFileNamesOfParsedCommandLine(sys.FS());
            }
        }
        foreach (var path in configFilePaths)
        {
            tfs.SeenFiles.Add(path);
        }

        program = global::Ts.Execute.Incremental.Program.NewProgram(
            new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
            {
                Config = config,
                Host = host,
            }),
            program,
            null!,
            testing != null);

        var result = CompileAndEmit();
        cached.DisableAndClearCache();

        var caseSensitive = sys.FS().UseCaseSensitiveFileNames();
        var cwd = sys.GetCurrentDirectory();
        var seenSlice = tfs.SeenFiles.ToSlice();
        seenFiles = new Set<PathT>(seenSlice.Length);
        foreach (var p in seenSlice)
        {
            seenFiles.Add(TsPathApi.ToPath(p, cwd, caseSensitive));
        }

        configMtimes = new Dictionary<string, DateTime>(configFilePaths.Count, StringComparer.Ordinal);
        foreach (var cfgPath in configFilePaths)
        {
            if (sys.FS().Stat(cfgPath) is { } s)
            {
                configMtimes[cfgPath] = s.ModTime();
            }
        }

        try
        {
            ReconcileWatches(seenSlice);
        }
        catch (Exception e)
        {
            sys.Writer().Write(e.Message + "\n");
            return false;
        }
        configModified = false;

        var programFiles = program.GetProgram().FilesByPath();
        sourceFileCache.Range((path, _) =>
        {
            if (!programFiles.ContainsKey(path))
            {
                sourceFileCache.Delete(path);
            }
            return true;
        });

        var errorCount = result.Diagnostics.Length;
        if (errorCount == 1)
        {
            reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Found_1_error_Watching_for_file_changes));
        }
        else
        {
            reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Found_0_errors_Watching_for_file_changes, errorCount));
        }

        testing?.OnProgram(program);
        return true;
    }

    // watcher.go:355-368
    private void EvictChangedSourceFiles(Dictionary<string, global::Ts.FsWatch.EventKind> changedPaths)
    {
        var caseSensitive = sys.FS().UseCaseSensitiveFileNames();
        var cwd = sys.GetCurrentDirectory();
        foreach (var eventPath in changedPaths.Keys) // map-iter: 序无关 (cache eviction)
        {
            var p = TsPathApi.ToPath(eventPath, cwd, caseSensitive);
            if (sourceFileCache.TryLoad(p, out _))
            {
                wm.DebugLog?.Write($"[watch] evicting cached source file: {p}\n");
                sourceFileCache.Delete(p);
            }
        }
    }

    // watcher.go:370-383
    private CompileAndEmitResult CompileAndEmit()
    {
        return EmitAndReport.EmitFilesAndReportErrors(new EmitInput
        {
            Sys = sys,
            ProgramLike = program!,
            Program = program!.GetProgram(),
            Config = config,
            ReportDiagnostic = reportDiagnostic,
            ReportErrorSummary = reportErrorSummary,
            Writer = sys.Writer(),
            CompileTimes = new CompileTimes(),
            Testing = testing,
        });
    }

    // watcher.go:385-424
    private bool RecheckTsConfig()
    {
        if (configFileName.Length == 0)
        {
            return false;
        }

        if (!configHasErrors && configFilePaths.Count > 0)
        {
            var changed = false;
            foreach (var path in configFilePaths)
            {
                var hasOld = configMtimes.TryGetValue(path, out var oldMtime);
                var s = sys.FS().Stat(path);
                if (!hasOld)
                {
                    if (s != null)
                    {
                        changed = true;
                        break;
                    }
                }
                else if (s == null || s.ModTime() != oldMtime)
                {
                    changed = true;
                    break;
                }
            }
            if (!changed)
            {
                return false;
            }
        }

        var configParseResult = ParseConfigFile();
        if (configParseResult == null)
        {
            return true;
        }
        if (configHasErrors)
        {
            configModified = true;
        }
        configHasErrors = false;
        configFilePaths = new List<string> { configFileName };
        configFilePaths.AddRange(configParseResult.ExtendedSourceFiles() ?? []);
        if (!global::Ts.TsOptions.ParsedOptionsDeepEqual.DeepEqual(config.ParsedConfig, configParseResult.ParsedConfig))
        {
            configModified = true;
        }
        config = configParseResult;
        return false;
    }

    // watcher.go:426-443
    private global::Ts.TsOptions.ParsedCommandLine? ParseConfigFile()
    {
        var newExtendedConfigCache = new TscExtendedConfigCache();
        var (configParseResult, errors) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFile(
            configFileName, compilerOptionsFromCommandLine, commandLineRaw, sys, newExtendedConfigCache);
        if (errors is { Length: > 0 })
        {
            foreach (var e in errors)
            {
                reportDiagnostic(e);
            }
            configHasErrors = true;
            var errorCount = errors.Length;
            if (errorCount == 1)
            {
                reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Found_1_error_Watching_for_file_changes));
            }
            else
            {
                reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(Diag.Found_0_errors_Watching_for_file_changes, errorCount));
            }
            return null;
        }
        extendedConfigCache = newExtendedConfigCache;
        return configParseResult;
    }

    public void Dispose()
    {
        wm.CloseAllWatches();
    }
}
