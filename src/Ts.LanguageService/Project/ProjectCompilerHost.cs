// Ported from tsgo internal/project/compilerhost.go (whole file).
// PORT-NOTE: the class keeps the landed C# name `ProjectCompilerHost` (Go: compilerHost)
// and retains the earlier foundation constructor (`(currentDirectory, fileSystem,
// defaultLibraryPath)`) because ProjectService still drives projects through it. The
// Go-shaped construction path (NewCompilerHost) is what project.go's CreateProgram uses;
// members that differ between the two paths branch on `builder != null` exactly like Go's
// `c.builder == nil` checks.

using System;

namespace Ts.Project;

internal sealed class ProjectCompilerHost :
    global::Ts.Compiler.CompilerHost,
    global::Ts.TsOptions.ParseConfigHost
{
    // Foundation-only state (pre-Go-shape ProjectService path).
    private readonly string foundationDefaultLibraryPath;
    private readonly OverlayFileSystem? foundationFileSystem;

    internal readonly global::Ts.TsPath.Path configFilePath;
    internal readonly string currentDirectory;
    internal readonly SessionOptions sessionOptions;

    internal SourceFS sourceFS = null!;
    internal ConfigFileRegistry? configFileRegistry;

    internal Project? project;
    internal ProjectCollectionBuilder? builder;
    internal global::Ts.Project.Logging.LogTree? logger;

    // compilerhost.go:29-46 — newCompilerHost.
    internal static ProjectCompilerHost NewCompilerHost(
        string currentDirectory,
        Project project,
        ProjectCollectionBuilder builder,
        global::Ts.Project.Logging.LogTree? logger)
    {
        return new ProjectCompilerHost(currentDirectory, project, builder, logger);
    }

    private ProjectCompilerHost(string currentDirectory, Project project, ProjectCollectionBuilder builder, global::Ts.Project.Logging.LogTree? logger)
    {
        configFilePath = project.configFilePath;
        this.currentDirectory = currentDirectory;
        sessionOptions = builder.sessionOptions;

        sourceFS = SourceFS.NewSourceFS(true, builder.fs, builder.toPath);

        this.project = project;
        this.builder = builder;
        this.logger = logger;
        foundationDefaultLibraryPath = sessionOptions.DefaultLibraryPath;
    }

    // Foundation constructor (ProjectService path; not part of compilerhost.go).
    internal ProjectCompilerHost(string currentDirectory, OverlayFileSystem fileSystem, string defaultLibraryPath)
    {
        this.currentDirectory = currentDirectory;
        foundationFileSystem = fileSystem;
        foundationDefaultLibraryPath = defaultLibraryPath;
        sessionOptions = null!;
    }

    // compilerhost.go:48-60 — freeze clears references to mutable state to make the
    // compilerHost safe for use after the snapshot has been finalized. See the usage in
    // snapshot.go for more details.
    internal void Freeze(SnapshotFS snapshotFS, ConfigFileRegistry configFileRegistry)
    {
        if (builder is null)
        {
            throw new InvalidOperationException("freeze can only be called once");
        }
        sourceFS.source = snapshotFS;
        sourceFS.DisableTracking();
        this.configFileRegistry = configFileRegistry;
        builder = null;
        project = null;
        logger = null;
    }

    // compilerhost.go:62-66 — ensureAlive.
    private void EnsureAlive()
    {
        if (builder is null || project is null)
        {
            throw new InvalidOperationException("method must not be called after snapshot initialization");
        }
    }

    // DefaultLibraryPath implements compiler.CompilerHost.
    public string DefaultLibraryPath() => foundationDefaultLibraryPath;

    // FS implements compiler.CompilerHost.
    public global::Ts.Vfs.IFileSystem FS()
    {
        if (foundationFileSystem is not null)
        {
            return foundationFileSystem;
        }
        return sourceFS;
    }

    // GetCurrentDirectory implements compiler.CompilerHost.
    public string GetCurrentDirectory() => currentDirectory;

    // GetResolvedProjectReference implements compiler.CompilerHost.
    public global::Ts.TsOptions.ParsedCommandLine? GetResolvedProjectReference(string fileName, global::Ts.TsPath.Path path)
    {
        if (foundationFileSystem is not null)
        {
            // Foundation (ProjectService) path, not part of compilerhost.go: there is no config
            // file registry, so the referenced config is parsed directly, as the CLI host does
            // (compiler/host.go:86-89).
            return global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFilePath(
                fileName, path, null, null, this, null).Item1;
        }
        if (builder is null)
        {
            return configFileRegistry!.GetConfig(path);
        }
        else
        {
            // acquireConfigForProject will bypass sourceFS, so track the file here.
            sourceFS.Track(fileName);
            return builder.ConfigFileRegistryBuilder.AcquireConfigForProject(fileName, path, project!, logger!);
        }
    }

    // GetSourceFile implements compiler.CompilerHost. Files are cached in parseCache
    // and acquired immediately for the in-progress program.
    public global::Ts.Ast.SourceFile? GetSourceFile(global::Ts.Ast.SourceFileParseOptions opts)
    {
        if (builder is not null)
        {
            EnsureAlive();
            FileHandle? fh = sourceFS.GetFileByPath(opts.FileName, opts.Path);
            if (fh is not null)
            {
                ParseCacheKey key = ParseCache.NewParseCacheKey(opts, fh.Hash(), fh.Kind());
                return builder.ParseCache.Acquire(key, fh);
            }
            return null;
        }

        // Foundation path (ProjectService): parse without the project-system caches.
        if (foundationFileSystem is null || !foundationFileSystem.TryReadFile(opts.FileName, out string text))
        {
            return null;
        }
        global::Ts.Core.ScriptKind scriptKind = foundationFileSystem.TryGetOpenFile(opts.Path, out OpenFileSnapshot openFile)
            ? openFile.ScriptKind
            : global::Ts.Core.CoreUtil.GetScriptKindFromFileName(opts.FileName);
        if (global::Ts.Bundled.Bundled.IsBundled(opts.FileName))
        {
            // HOSTING-NOTE: the session path shares parsed files through ParseCache
            // (compilerhost.go:105-117, lsp.ServerOptions.ParseCache); the foundation path had no
            // cache and re-parsed every bundled lib on each reload. Bundled libs are immutable,
            // so they are shared process-wide keyed by the parse options, as ParseCache keys
            // them by (options, content hash, kind).
            var key = (opts.FileName, opts.ExternalModuleIndicatorOptions.Jsx, opts.ExternalModuleIndicatorOptions.Force, scriptKind);
            return bundledSourceFiles.GetOrAdd(key, _ => global::Ts.Parser.Parser.ParseSourceFile(opts, text, scriptKind));
        }
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, scriptKind);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string FileName, bool Jsx, bool Force, global::Ts.Core.ScriptKind Kind), global::Ts.Ast.SourceFile> bundledSourceFiles = new();

    // Trace implements compiler.CompilerHost.
    public void Trace(global::Ts.Diagnostics.Message msg, params object?[] args)
    {
        if (logger is not null)
        {
            // PORT-NOTE(project): Go calls msg.Localize(locale.Default, args...). The C#
            // Diagnostics localization/formatting surface (Localize/StringifyArgs) is
            // deferred to the phase-9 runtime wiring (Ts.Compiler/Diagnostics/Diagnostics.cs
            // header, plan/06 §2.2), so Trace emits the base text followed by the raw args —
            // same log content modulo Go-verb expansion.
            global::Ts.Project.Logging.LogTreeExtensions.Log(logger, [msg.Text, .. args]);
        }
    }
}

