// Ported from tsgo internal/execute/watcher.go.
//
// The Go implementation layers incremental.Program and watchmanager over fswatch. This wave keeps
// the externally observable watch-session flow (initial build, watched config/input/wildcard
// paths, debounced change cycles, config reload, diagnostics, and exact watch status messages)
// while consuming the already-landed global::Ts.FsWatch public API directly.

using System;
using System.Collections.Generic;
using System.Threading.Channels;

namespace Ts.Execute;

internal sealed class WatchSession : IExecuteWatcher
{
    private readonly object gate = new();
    private readonly ISystem sys;
    private readonly global::Ts.Core.CompilerOptions compilerOptionsFromCommandLine;
    private readonly global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw;
    private readonly DiagnosticReporter reportDiagnostic;
    private readonly DiagnosticsReporter reportErrorSummary;
    private readonly DiagnosticReporter reportWatchStatus;
    private readonly ICommandLineTesting? testing;
    private readonly global::Ts.FsWatch.IWatcher watcher;
    private readonly Channel<bool> eventSignal;
    private readonly List<global::Ts.FsWatch.IWatch> watches = new();
    private readonly HashSet<string> pendingPaths;
    private readonly HashSet<string> configFilePaths;
    private readonly StringComparer pathComparer;

    private global::Ts.TsOptions.ParsedCommandLine config;
    private HashSet<string> seenFiles;
    private bool overflow;
    private bool configModified;
    private bool disposed;

    internal WatchSession(
        ISystem sys,
        global::Ts.TsOptions.ParsedCommandLine config,
        global::Ts.Core.CompilerOptions compilerOptionsFromCommandLine,
        global::Ts.Collections.OrderedMap<string, object?>? commandLineRaw,
        DiagnosticReporter reportDiagnostic,
        DiagnosticsReporter reportErrorSummary,
        ICommandLineTesting? testing)
    {
        this.sys = sys;
        this.config = config;
        this.compilerOptionsFromCommandLine = compilerOptionsFromCommandLine;
        this.commandLineRaw = commandLineRaw;
        this.reportDiagnostic = reportDiagnostic;
        this.reportErrorSummary = reportErrorSummary;
        this.testing = testing;
        pathComparer = sys.FS().UseCaseSensitiveFileNames() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        pendingPaths = new HashSet<string>(pathComparer);
        configFilePaths = new HashSet<string>(pathComparer);
        seenFiles = new HashSet<string>(pathComparer);
        watcher = testing?.Watcher() ?? global::Ts.FsWatch.Watcher.Default();
        eventSignal = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        reportWatchStatus = DiagnosticReporters.CreateWatchStatusReporter(
            sys,
            global::Ts.Locale.Locale.Default,
            config.CompilerOptions()!,
            testing);
        RefreshConfigFilePaths();
    }

    internal void Start()
    {
        reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
            global::Ts.Diagnostics.Diagnostics.Starting_compilation_in_watch_mode));
        DoBuild();

        // Go keeps the caller in watchmanager.RunLoop outside test mode. The channel transfers
        // FileSystemWatcher/debounce callbacks back to this large-stack command-line thread.
        if (testing is null)
        {
            RunLoop();
        }
    }

    public void DoCycle()
    {
        global::Ts.Core.LargeStackExecutor.Run(DoCycleCore);
    }

    private void DoCycleCore()
    {
        HashSet<string> changedPaths;
        bool hadOverflow;
        bool configChanged;
        lock (gate)
        {
            changedPaths = new HashSet<string>(pendingPaths, pathComparer);
            pendingPaths.Clear();
            hadOverflow = overflow;
            overflow = false;
            configChanged = configModified;
            configModified = false;
        }

        if (changedPaths.Count == 0 && !hadOverflow && !configChanged)
        {
            return;
        }

        if (!hadOverflow && !configChanged && !IsRelevantChange(changedPaths))
        {
            return;
        }

        if (configChanged && !ReloadConfig())
        {
            return;
        }

        reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
            global::Ts.Diagnostics.Diagnostics.File_change_detected_Starting_incremental_compilation));
        DoBuild();
    }

    private void RunLoop()
    {
        var reader = eventSignal.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out _))
            {
                // Drain the wakeups. Paths and overflow state are coalesced under gate.
            }
            DoCycleCore();
        }
    }

    private bool ReloadConfig()
    {
        string configFileName = config.ConfigName();
        if (configFileName == "")
        {
            return true;
        }

        var (parsed, errors) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFile(
            configFileName,
            compilerOptionsFromCommandLine,
            commandLineRaw,
            sys,
            extendedConfigCache: null);
        if (errors is not null && errors.Length > 0)
        {
            foreach (var error in errors)
            {
                reportDiagnostic(error);
            }
            ReportFoundErrors(errors.Length);
            return false;
        }

        // Go reaches this dereference only when GetParsedCommandLineOfConfigFile returned no
        // unrecoverable errors. Preserve that invariant rather than adding a speculative guard.
        config = parsed!;
        RefreshConfigFilePaths();
        return true;
    }

    private void DoBuild()
    {
        if (config.ConfigFile is not null && config.WildcardDirectories() is { Count: > 0 })
        {
            config = config.ReloadFileNamesOfParsedCommandLine(sys.FS());
        }

        // PORT-NOTE: Go constructs incremental.Program with the previous program and build-info
        // snapshot here. The incremental/.tsbuildinfo package has not landed in C# yet, so this
        // wave deliberately performs a fresh Program build while preserving watch invalidation,
        // diagnostics, emit, and status behaviour. Program reuse arrives with the later
        // incremental/.tsbuildinfo wave.
        var outcome = Tsc.PerformCompilationOutcome(
            sys,
            config,
            reportDiagnostic,
            reportErrorSummary,
            testing);

        ReconcileWatches(outcome.Program);
        ReportFoundErrors(outcome.Diagnostics.Length);
    }

    private void ReportFoundErrors(int errorCount)
    {
        if (errorCount == 1)
        {
            reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                global::Ts.Diagnostics.Diagnostics.Found_1_error_Watching_for_file_changes));
        }
        else
        {
            reportWatchStatus(global::Ts.Ast.Diagnostic.NewCompilerDiagnostic(
                global::Ts.Diagnostics.Diagnostics.Found_0_errors_Watching_for_file_changes,
                errorCount));
        }
    }

    private bool IsRelevantChange(HashSet<string> changedPaths)
    {
        foreach (string eventPath in changedPaths)
        {
            string normalized = NormalizeAbsolutePath(eventPath);
            if (seenFiles.Contains(normalized) || configFilePaths.Contains(normalized))
            {
                return true;
            }
            if (config.ConfigFile is not null && config.PossiblyMatchesFileName(normalized))
            {
                return true;
            }
            if (config.ConfigFile is not null && sys.FS().DirectoryExists(normalized))
            {
                var path = global::Ts.TsPath.TsPath.ToPath(
                    normalized,
                    sys.GetCurrentDirectory(),
                    sys.FS().UseCaseSensitiveFileNames());
                if (config.PossiblyMatchesDirectoryName(path))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void ReconcileWatches(global::Ts.Compiler.Program program)
    {
        foreach (var activeWatch in watches)
        {
            activeWatch.Close();
        }
        watches.Clear();

        var desiredFiles = new HashSet<string>(pathComparer);
        foreach (string configPath in configFilePaths)
        {
            desiredFiles.Add(configPath);
        }
        foreach (string fileName in config.FileNames() ?? Array.Empty<string>())
        {
            desiredFiles.Add(NormalizeAbsolutePath(fileName));
        }
        foreach (var sourceFile in program.SourceFiles())
        {
            string fileName = NormalizeAbsolutePath(sourceFile.FileName());
            if (sys.FS().FileExists(fileName))
            {
                desiredFiles.Add(fileName);
            }
        }
        seenFiles = desiredFiles;

        foreach (string fileName in desiredFiles)
        {
            string parent = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName);
            if (parent != fileName && sys.FS().DirectoryExists(parent))
            {
                watches.Add(watcher.WatchFile(fileName, OnFsEvents));
            }
        }

        Dictionary<string, bool>? wildcardDirectories =
            config.ConfigFile is null ? null : config.WildcardDirectories();
        // map-iter: 序无关 — each wildcard directory creates an independent subscription; callback
        // behaviour and rebuild decisions do not depend on subscription creation order.
        foreach (var wildcard in wildcardDirectories ?? new Dictionary<string, bool>())
        {
            string directory = ResolveWatchableDirectory(NormalizeAbsolutePath(wildcard.Key));
            if (directory == "")
            {
                continue;
            }
            if (wildcard.Value)
            {
                watches.Add(watcher.WatchDirectory(
                    directory,
                    OnFsEvents,
                    global::Ts.FsWatch.Watcher.WithRecursive()));
            }
            else
            {
                watches.Add(watcher.WatchDirectory(directory, OnFsEvents));
            }
        }
    }

    private string ResolveWatchableDirectory(string directory)
    {
        while (directory != "" && !sys.FS().DirectoryExists(directory))
        {
            string parent = global::Ts.TsPath.TsPath.GetDirectoryPath(directory);
            if (parent == directory)
            {
                return "";
            }
            directory = parent;
        }
        return directory;
    }

    private void RefreshConfigFilePaths()
    {
        configFilePaths.Clear();
        string configName = config.ConfigName();
        if (configName != "")
        {
            configFilePaths.Add(NormalizeAbsolutePath(configName));
        }
        foreach (string extendedSourceFile in config.ExtendedSourceFiles() ?? Array.Empty<string>())
        {
            configFilePaths.Add(NormalizeAbsolutePath(extendedSourceFile));
        }
    }

    private string NormalizeAbsolutePath(string path) =>
        global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(path, sys.GetCurrentDirectory());

    private void OnFsEvents(IReadOnlyList<global::Ts.FsWatch.Event> events, Exception? error)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (error is not null)
            {
                overflow = true;
            }
            foreach (var fsEvent in events)
            {
                string path = NormalizeAbsolutePath(fsEvent.Path);
                pendingPaths.Add(path);
                if (configFilePaths.Contains(path))
                {
                    configModified = true;
                }
            }
        }
        _ = eventSignal.Writer.TryWrite(true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        foreach (var activeWatch in watches)
        {
            activeWatch.Close();
        }
        watches.Clear();
        eventSignal.Writer.TryComplete();
    }
}
