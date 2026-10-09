// Ported from tsgo internal/project/project.go (whole file), merged with the earlier
// landed foundation subset. The foundation members (Reload/SetInferredRoots/watches and
// the private (kind, name, cwd, caseSensitive) constructor) are kept because
// ProjectService still drives them; every Go member missing from that subset is ported
// below in Go's declaration order. Registered deviation: compiler.Program.UpdateProgram
// is not ported yet (see IncludeProcessor.cs header), so CreateProgram always takes the
// full-rebuild path and never produces ProgramUpdateKind.Cloned.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Collections;
using Ts.Core;
using Ts.Project.Logging;
using static global::Ts.Compiler.ProgramLsExports;

using WatchKind = global::Ts.LsProto.WatchKind;

namespace Ts.Project;

public enum Kind
{
    Inferred,
    Configured,
}

public enum ProgramUpdateKind
{
    None,
    Cloned,
    SameFileNames,
    NewFiles,
}

// project.go:46-52 — PendingReload.
public enum PendingReload
{
    None,
    FileNames,
    Full,
}

public sealed class Project : IDisposable, LsProject, global::Ts.Project.Dirty.Cloneable<Project>
{
    internal const string InferredProjectName = "/dev/null/inferred"; // lowercase so toPath is a no-op regardless of settings

    // project.go:24 — hr.
    internal const string Hr = "-----------------------------------------------";

    private readonly string currentDirectory;
    private readonly string configFileName;
    // PORT-NOTE(project.go:246): Clone copies configFilePath verbatim (no recompute), so
    // this cannot be readonly.
    internal global::Ts.TsPath.Path configFilePath;
    private readonly List<global::Ts.FsWatch.IWatch> watches = [];
    private bool disposed;

    // project.go:62-63 — dirty/dirtyFilePath. (Go `dirty` is exposed as IsDirty.)
    internal global::Ts.TsPath.Path dirtyFilePath;

    // project.go:65-88 — Project fields (Go: host/CommandLine/commandLineWithTypingsFiles/
    // Program/ProgramUpdateKind/ProgramLastUpdate/potentialProjectReferences/
    // programFilesWatch/typingsWatch/checkerPool/installedTypingsInfo/typingsFiles).
    internal ProjectCompilerHost? host;
    internal global::Ts.TsOptions.ParsedCommandLine? commandLineWithTypingsFiles;
    internal object commandLineWithTypingsFilesOnce = new();
    internal Set<global::Ts.TsPath.Path>? potentialProjectReferences;

    internal WatchedFiles<SyncSet<global::Ts.TsPath.Path>>? programFilesWatch;
    internal WatchedFiles<PatternsAndIgnored>? typingsWatch;

    internal CheckerPool? checkerPool;

    // installedTypingsInfo is the value of `project.ComputeTypingsInfo()` that was
    // used during the most recently completed typings installation.
    internal global::Ts.Project.Ata.TypingsInfo? installedTypingsInfo;

    // typingsFiles are the root files added by the typings installer.
    internal string[] typingsFiles = [];

    private Project(Kind kind, string name, string currentDirectory, bool useCaseSensitiveFileNames)
    {
        Kind = kind;
        configFileName = name;
        this.currentDirectory = currentDirectory;
        configFilePath = global::Ts.TsPath.TsPath.ToPath(name, currentDirectory, useCaseSensitiveFileNames);
        IsDirty = true;
    }

    public Kind Kind { get; }

    public global::Ts.TsOptions.ParsedCommandLine? CommandLine { get; internal set; }

    public global::Ts.Compiler.Program? Program { get; internal set; }

    public ProgramUpdateKind ProgramUpdateKind { get; internal set; }

    public ulong ProgramLastUpdate { get; internal set; }

    public bool IsDirty { get; internal set; }

    public string CurrentDirectory => currentDirectory;

    public string[] RootFileNames => CommandLine?.FileNames() ?? [];

    public string Name() => configFileName;

    public global::Ts.TsPath.Path Id() => configFilePath;

    public global::Ts.Compiler.Program? GetProgram() => Program;

    public global::Ts.Ls.LanguageService? GetLanguageService() => LanguageService;

    internal global::Ts.Ls.LanguageService? LanguageService { get; private set; }

    public string ConfigFileName()
    {
        if (Kind != Kind.Configured)
        {
            throw new InvalidOperationException("ConfigFileName called on non-configured project");
        }

        return configFileName;
    }

    public global::Ts.TsPath.Path ConfigFilePath()
    {
        if (Kind != Kind.Configured)
        {
            throw new InvalidOperationException("ConfigFilePath called on non-configured project");
        }

        return configFilePath;
    }

    // project.go:215-228 — GetProjectDiagnostics returns program diagnostics combined with
    // any global diagnostics discovered during checking. These are the diagnostics reported
    // on the tsconfig.json file.
    public global::Ts.Ast.Diagnostic[] GetProjectDiagnostics(global::Ts.Core.RequestContext ctx)
    {
        _ = ctx;
        global::Ts.Ast.Diagnostic[]? globalDiags = null;
        if (checkerPool is not null)
        {
            globalDiags = checkerPool.GetGlobalDiagnostics();
        }
        return global::Ts.Compiler.Program.SortAndDeduplicateDiagnostics(
            (Program!.GetConfigFileParsingDiagnostics() ?? [])
                .Concat(Program.GetProgramDiagnostics() ?? [])
                .Concat(globalDiags ?? []).ToArray());
    }

    // project.go:230-232 — HasFile.
    public bool HasFile(string fileName)
    {
        return ContainsFile(ToPath(fileName));
    }

    // project.go:234-236 — containsFile.
    internal bool ContainsFile(global::Ts.TsPath.Path path)
    {
        return Program is not null && Program.GetSourceFileByPath(path) is not null;
    }

    // project.go:238-240 — IsSourceFromProjectReference.
    public bool IsSourceFromProjectReference(global::Ts.TsPath.Path path)
    {
        return Program is not null && Program.IsSourceFromProjectReferenceForLs(path);
    }

    // project.go:242-268 — Clone.
    public Project Clone()
    {
        var clone = new Project(Kind, configFileName, currentDirectory, true)
        {
            IsDirty = IsDirty,
            dirtyFilePath = dirtyFilePath,
            host = host,
            CommandLine = CommandLine,
            commandLineWithTypingsFiles = commandLineWithTypingsFiles,
            Program = Program,
            ProgramUpdateKind = ProgramUpdateKind.None,
            ProgramLastUpdate = ProgramLastUpdate,
            potentialProjectReferences = potentialProjectReferences,
            programFilesWatch = programFilesWatch,
            typingsWatch = typingsWatch,
            checkerPool = checkerPool,
            installedTypingsInfo = installedTypingsInfo,
            typingsFiles = typingsFiles,
            LanguageService = LanguageService,
        };
        // Go copies configFilePath into the clone literally (project.go:246). Recomputing
        // it here would re-case the path (the ctor parameter above is a stand-in for Go's
        // omitted useCaseSensitiveFileNames field), so assign the original.
        clone.configFilePath = configFilePath;
        return clone;
    }

    // project.go:270-284 — SetCommandLine reassigns the project's command line and resets
    // all state derived from it. Changing the command line always requires a full program
    // rebuild, so the project is marked fully dirty. It also resets:
    //   - the memoized command line augmented with typings files (and its sync.Once, so
    //     the augmented command line is rebuilt from the new command line on next access);
    //   - potentialProjectReferences, the pre-load placeholder derived from the old
    //     command line (always nil for inferred projects, which have no project references).
    internal void SetCommandLine(global::Ts.TsOptions.ParsedCommandLine commandLine)
    {
        CommandLine = commandLine;
        commandLineWithTypingsFiles = null;
        commandLineWithTypingsFilesOnce = new object();
        potentialProjectReferences = null;
        IsDirty = true;
        dirtyFilePath = default;
    }

    // project.go:286-318 — getCommandLineWithTypingsFiles returns the command line augmented
    // with typing files if ATA is enabled.
    internal global::Ts.TsOptions.ParsedCommandLine? GetCommandLineWithTypingsFiles()
    {
        if (typingsFiles.Length == 0)
        {
            return CommandLine;
        }

        // Check if ATA is enabled for this project
        global::Ts.Core.TypeAcquisition? typeAcquisition = GetTypeAcquisition();
        if (typeAcquisition is null || !typeAcquisition.Enable.IsTrue())
        {
            return CommandLine;
        }

        // Go: commandLineWithTypingsFilesOnce.Do → double-checked lazy init.
        lock (commandLineWithTypingsFilesOnce)
        {
            if (commandLineWithTypingsFiles is null)
            {
                // Create an augmented command line that includes typing files
                string[] originalRootNames = CommandLine!.FileNames() ?? [];
                var newRootNames = new List<string>(originalRootNames.Length + typingsFiles.Length);
                newRootNames.AddRange(originalRootNames);
                newRootNames.AddRange(typingsFiles);

                // Create a new ParsedCommandLine with the augmented root file names
                commandLineWithTypingsFiles = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
                    CommandLine.CompilerOptions()!,
                    newRootNames.ToArray(),
                    new global::Ts.TsPath.ComparePathsOptions
                    {
                        UseCaseSensitiveFileNames = host!.FS().UseCaseSensitiveFileNames(),
                        CurrentDirectory = currentDirectory,
                    });
            }
        }
        return commandLineWithTypingsFiles;
    }

    // project.go:320-327 — setPotentialProjectReference.
    internal void SetPotentialProjectReference(global::Ts.TsPath.Path configFilePath)
    {
        if (potentialProjectReferences is null)
        {
            potentialProjectReferences = new Set<global::Ts.TsPath.Path>();
        }
        else
        {
            potentialProjectReferences = potentialProjectReferences.Clone();
        }
        potentialProjectReferences!.Add(configFilePath);
    }

    // project.go:329-344 — hasPotentialProjectReference.
    internal bool HasPotentialProjectReference(Snapshot.ProjectTreeRequest projectTreeRequest)
    {
        global::Ts.TsOptions.ParsedCommandLine? commandLine = CommandLine;
        if (commandLine is not null)
        {
            // map-iter: 序无关 — membership probe per reference path.
            foreach (string path in commandLine.ResolvedProjectReferencePaths() ?? [])
            {
                if (projectTreeRequest.IsProjectReferenced(ToPath(path)))
                {
                    return true;
                }
            }
        }
        else if (potentialProjectReferences is not null)
        {
            // map-iter: 序无关 — membership probe per reference.
            foreach (global::Ts.TsPath.Path path in potentialProjectReferences.Keys())
            {
                if (projectTreeRequest.IsProjectReferenced(path))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // project.go:346-349 — CreateProgramResult.
    internal readonly record struct CreateProgramResult(global::Ts.Compiler.Program Program, ProgramUpdateKind UpdateKind);

    // project.go:351-415 — CreateProgram.
    internal CreateProgramResult CreateProgram()
    {
        ProgramUpdateKind updateKind = ProgramUpdateKind.NewFiles;
        bool programCloned = false;
        global::Ts.Compiler.Program newProgram;

        // project.go:356-362 — Define a fresh CreateCheckerPool closure for this call. Each
        // invocation of CreateProgram must use its own closure so that concurrent goroutines
        // cloning the same project never share a captured variable through a stale closure
        // stored in the old program's options.
        global::Ts.Checker.ICheckerPool CreateCheckerPool(global::Ts.Compiler.Program program)
        {
            return CheckerPool.NewCheckerPool(host!.sessionOptions.CheckerPoolOptions, program, Log);
        }

        // Create the command line, potentially augmented with typing files
        global::Ts.TsOptions.ParsedCommandLine? commandLine = GetCommandLineWithTypingsFiles();

        // PORT-NOTE (deviation): Go's dirty-file path (`p.Program.UpdateProgram`) is not
        // available in the C# compiler yet; this always rebuilds (programCloned stays false).
        {
            string typingsLocation = "";
            if (GetTypeAcquisition()!.Enable.IsTrue())
            {
                typingsLocation = host!.sessionOptions.TypingsLocation;
            }
            newProgram = global::Ts.Core.LargeStackExecutor.Run(() =>
            {
                var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
                {
                    Host = host!,
                    // Go: Config: commandLine (nil-able *ParsedCommandLine). ProgramOptions.Config
                    // is non-nullable in C#, and the compiler wave owns that shape, so assert —
                    // Project invariant: the command line is set before CreateProgram runs.
                    Config = commandLine!,
                    UseSourceOfProjectReference = true,
                    TypingsLocation = typingsLocation,
                    CreateCheckerPool = CreateCheckerPool,
                });
                return program;
            });
        }

        if (!programCloned && Program is not null && HaveSameFileNames(
                FileNamesOf(Program),
                FileNamesOf(newProgram),
                host!.FS()))
        {
            updateKind = ProgramUpdateKind.SameFileNames;
        }

        newProgram.BindSourceFiles();

        return new CreateProgramResult(newProgram, updateKind);
    }

    // Go: program file-name comparison inputs — SourceFiles() before BindSourceFiles.
    private static string[] FileNamesOf(global::Ts.Compiler.Program program)
    {
        var names = new string[program.SourceFiles().Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = program.SourceFiles()[i].FileName();
        }
        return names;
    }

    // project.go:417-419 — CloneWatchers.
    internal WatchedFiles<SyncSet<global::Ts.TsPath.Path>>? CloneWatchers()
    {
        return programFilesWatch!.Clone(host!.sourceFS.seenFiles!);
    }

    // project.go:421-423 — log.
    private void Log(string msg)
    {
        _ = msg;
        // !!!
    }

    // project.go:425-427 — toPath.
    // PORT-NOTE(project): Go's p.host is always the ProjectCompilerHost the Program was
    // built over. The C#-only foundation ProjectService builds Programs via Reload without a
    // ProjectCompilerHost, so the Program's own compiler host (the same FS in Go) stands in.
    internal global::Ts.TsPath.Path ToPath(string fileName)
    {
        var fs = host is not null ? host.FS() : Program!.Host().FS();
        return global::Ts.TsPath.TsPath.ToPath(fileName, currentDirectory, fs.UseCaseSensitiveFileNames());
    }

    // project.go:429-448 — print.
    internal string Print(bool writeFileNames, bool writeFileExplanation, System.Text.StringBuilder builder)
    {
        _ = writeFileExplanation;
        builder.Append("\nProject '").Append(Name()).Append("'\n");
        if (Program is null)
        {
            builder.Append("\tFiles (0) NoProgram\n");
        }
        else
        {
            global::Ts.Ast.SourceFile[] sourceFiles = Program.SourceFiles();
            builder.Append("\tFiles (").Append(sourceFiles.Length).Append(")\n");
            if (writeFileNames)
            {
                foreach (global::Ts.Ast.SourceFile sourceFile in sourceFiles)
                {
                    builder.Append("\t\t");
                    builder.Append(sourceFile.FileName());
                    builder.Append('\n');
                }
                // !!!
                // if writeFileExplanation {}
            }
        }
        builder.Append(Hr);
        return builder.ToString();
    }

    // project.go:450-467 — GetTypeAcquisition returns the type acquisition settings for
    // this project.
    public global::Ts.Core.TypeAcquisition? GetTypeAcquisition()
    {
        if (Kind == Kind.Inferred)
        {
            // For inferred projects, use default settings
            return new global::Ts.Core.TypeAcquisition
            {
                Enable = global::Ts.Core.Tristate.True,
                Include = null,
                Exclude = null,
                DisableFilenameBasedTypeAcquisition = global::Ts.Core.Tristate.False,
            };
        }

        if (CommandLine is not null)
        {
            return CommandLine.TypeAcquisition();
        }

        return null;
    }

    // project.go:469-476 — GetUnresolvedImports extracts unresolved imports from this
    // project's program.
    public Set<string>? GetUnresolvedImports()
    {
        if (Program is null)
        {
            return null;
        }

        return Program.GetUnresolvedImports();
    }

    // project.go:478-494 — ShouldTriggerATA determines if ATA should be triggered for this
    // project.
    public bool ShouldTriggerATA(ulong snapshotID)
    {
        if (Program is null || CommandLine is null)
        {
            return false;
        }

        global::Ts.Core.TypeAcquisition? typeAcquisition = GetTypeAcquisition();
        if (typeAcquisition is null || !typeAcquisition.Enable.IsTrue())
        {
            return false;
        }

        if (installedTypingsInfo is null || (ProgramLastUpdate == snapshotID && ProgramUpdateKind == ProgramUpdateKind.NewFiles))
        {
            return true;
        }

        return !installedTypingsInfo.Equals(ComputeTypingsInfo());
    }

    // project.go:496-502 — ComputeTypingsInfo.
    public global::Ts.Project.Ata.TypingsInfo ComputeTypingsInfo()
    {
        return new global::Ts.Project.Ata.TypingsInfo
        {
            CompilerOptions = CommandLine!.CompilerOptions(),
            TypeAcquisition = GetTypeAcquisition(),
            UnresolvedImports = GetUnresolvedImports(),
        };
    }

    // ---- Project-system constructors (project.go:102-177) ----

    // project.go:102-110 — NewConfiguredProject (overloads the earlier foundation
    // factory above; this Go-shaped form is what ProjectCollectionBuilder uses).
    internal static Project NewConfiguredProject(
        string configFileName,
        global::Ts.TsPath.Path configFilePath,
        ProjectCollectionBuilder builder,
        global::Ts.Project.Logging.LogTree? logger)
    {
        return NewProject(configFileName, Kind.Configured, global::Ts.TsPath.TsPath.GetDirectoryPath(configFileName), builder, logger);
    }

    // project.go:112-138 — NewInferredProject (project-system path).
    internal static Project NewInferredProject(
        string currentDirectory,
        global::Ts.Core.CompilerOptions? compilerOptions,
        string[] rootFileNames,
        ProjectCollectionBuilder builder,
        global::Ts.Project.Logging.LogTree? logger)
    {
        Project p = NewProject(InferredProjectName, Kind.Inferred, currentDirectory, builder, logger);
        if (compilerOptions is null)
        {
            compilerOptions = new global::Ts.Core.CompilerOptions
            {
                AllowJs = global::Ts.Core.Tristate.True,
                Module = global::Ts.Core.ModuleKind.ESNext,
                ModuleResolution = global::Ts.Core.ModuleResolutionKind.Bundler,
                Target = global::Ts.Core.ScriptTarget.LatestStandard,
                Jsx = global::Ts.Core.JsxEmit.ReactJSX,
                AllowImportingTsExtensions = global::Ts.Core.Tristate.True,
                StrictNullChecks = global::Ts.Core.Tristate.True,
                StrictFunctionTypes = global::Ts.Core.Tristate.True,
                SourceMap = global::Ts.Core.Tristate.True,
                AllowNonTsExtensions = global::Ts.Core.Tristate.True,
                ResolveJsonModule = global::Ts.Core.Tristate.True,
            };
        }
        p.CommandLine = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            compilerOptions,
            rootFileNames,
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = builder.fs.fs.UseCaseSensitiveFileNames(),
                CurrentDirectory = currentDirectory,
            });
        return p;
    }

    // project.go:140-166 — NewProject.
    private static Project NewProject(
        string configFileName,
        Kind kind,
        string currentDirectory,
        ProjectCollectionBuilder builder,
        global::Ts.Project.Logging.LogTree? logger)
    {
        if (logger is not null)
        {
            logger.Log($"Creating {kind}Project: {configFileName}, currentDirectory: {currentDirectory}");
        }
        var project = new Project(kind, configFileName, currentDirectory, builder.fs.fs.UseCaseSensitiveFileNames());
        // Go: lsproto.GetClientCapabilities(builder.ctx) — C# registered deviation: the
        // resolved capabilities travel on SessionOptions (LsProto.Missing.cs).
        bool hasRelativePatternCapability = ClientCapabilitiesAccess.GetClientCapabilities(builder.sessionOptions).Workspace.DidChangeWatchedFiles.RelativePatternSupport;
        project.programFilesWatch = WatchedFiles<SyncSet<global::Ts.TsPath.Path>>.NewWatchedFiles(
            "program files for " + configFileName,
            WatchKind.Create | WatchKind.Change | WatchKind.Delete,
            hasRelativePatternCapability,
            WatchGlobMappers.CreateResolutionLookupGlobMapper(
                builder.sessionOptions.CurrentDirectory,
                builder.sessionOptions.DefaultLibraryPath,
                project.CurrentDirectory,
                builder.fs.fs.UseCaseSensitiveFileNames()));
        if (builder.sessionOptions.TypingsLocation != "")
        {
            project.typingsWatch = WatchedFiles<PatternsAndIgnored>.NewWatchedFiles(
                "typings installer files",
                WatchKind.Create | WatchKind.Change | WatchKind.Delete,
                hasRelativePatternCapability,
                // Go: core.Identity over the PatternsAndIgnored STRUCT (watch.go:107-111,160-165):
                // Watchers() on a fresh typings watch receives the zero struct (nil slices,
                // watch.go:195) and yields no watchers until the first Clone
                // (projectcollectionbuilder.go:898). PatternsAndIgnored is a class here, so the
                // zero value would be nil and crash GetWatchers — substitute the equivalent
                // empty instance to preserve the Go zero-value semantics.
                static input => input ?? new PatternsAndIgnored());
        }
        return project;
    }

    // DisplayName returns a short, human-readable name for the project,
    // relative to the given workspace root directory.
    // For configured projects, this is the config file path made relative.
    // For inferred projects, this is the last component of the current directory.
    // project.go:168-177 — DisplayName.
    public string DisplayName(string cwd)
    {
        if (Kind == Kind.Inferred)
        {
            return global::Ts.TsPath.TsPath.GetBaseFileName(currentDirectory);
        }
        return global::Ts.TsPath.TsPath.ConvertToRelativePath(configFileName, new global::Ts.TsPath.ComparePathsOptions
        {
            CurrentDirectory = cwd,
        });
    }

    // ---- Foundation subset (ProjectService path) ----

    internal static Project NewConfiguredProject(
        string configFileName,
        string currentDirectory,
        bool useCaseSensitiveFileNames)
    {
        return new Project(Kind.Configured, configFileName, currentDirectory, useCaseSensitiveFileNames);
    }

    internal static Project NewInferredProject(
        string currentDirectory,
        global::Ts.Core.CompilerOptions? compilerOptions,
        string[] rootFileNames,
        bool useCaseSensitiveFileNames)
    {
        var project = new Project(Kind.Inferred, InferredProjectName, currentDirectory, useCaseSensitiveFileNames);
        compilerOptions ??= NewInferredCompilerOptions();
        project.CommandLine = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            compilerOptions,
            rootFileNames,
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = useCaseSensitiveFileNames,
                CurrentDirectory = currentDirectory,
            });
        return project;
    }

    internal bool Reload(
        OverlayFileSystem fileSystem,
        string defaultLibraryPath,
        ulong updateId)
    {
        ThrowIfDisposed();

        global::Ts.TsOptions.ParsedCommandLine? newCommandLine = CommandLine;
        var host = new ProjectCompilerHost(currentDirectory, fileSystem, defaultLibraryPath);
        if (Kind == Kind.Configured)
        {
            (newCommandLine, _) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFilePath(
                configFileName,
                configFilePath,
                options: null,
                optionsRaw: null,
                sys: host,
                extendedConfigCache: null);
            if (newCommandLine is null)
            {
                return false;
            }
        }

        if (newCommandLine is null)
        {
            throw new InvalidOperationException("project has no command line: " + configFileName);
        }

        string[] oldRootFileNames = CommandLine?.FileNames() ?? [];
        string[] newRootFileNames = newCommandLine.FileNames() ?? [];
        global::Ts.Compiler.Program newProgram = global::Ts.Core.LargeStackExecutor.Run(() =>
        {
            var program = new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
            {
                Host = host,
                Config = newCommandLine,
                UseSourceOfProjectReference = true,
            });
            program.BindSourceFiles();
            return program;
        });

        ProgramUpdateKind = Program is null || !HaveSameFileNames(oldRootFileNames, newRootFileNames, fileSystem)
            ? ProgramUpdateKind.NewFiles
            : ProgramUpdateKind.SameFileNames;
        CommandLine = newCommandLine;
        Program = newProgram;
        LanguageService = global::Ts.Ls.LanguageService.NewLanguageService(newProgram);
        ProgramLastUpdate = updateId;
        IsDirty = false;
        return true;
    }

    internal void SetInferredRoots(
        string[] rootFileNames,
        global::Ts.Core.CompilerOptions? compilerOptions,
        bool useCaseSensitiveFileNames)
    {
        if (Kind != Kind.Inferred)
        {
            throw new InvalidOperationException("cannot set inferred roots on a configured project");
        }

        compilerOptions ??= CommandLine?.CompilerOptions() ?? NewInferredCompilerOptions();
        CommandLine = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            compilerOptions,
            rootFileNames,
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = useCaseSensitiveFileNames,
                CurrentDirectory = currentDirectory,
            });
        MarkDirty();
    }

    internal void MarkDirty()
    {
        IsDirty = true;
        ProgramUpdateKind = ProgramUpdateKind.None;
    }

    internal void AddWatch(global::Ts.FsWatch.IWatch watch)
    {
        ThrowIfDisposed();
        watches.Add(watch);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (global::Ts.FsWatch.IWatch watch in watches)
        {
            watch.Close();
        }
        watches.Clear();
    }

    private static global::Ts.Core.CompilerOptions NewInferredCompilerOptions()
    {
        return new global::Ts.Core.CompilerOptions
        {
            AllowJs = global::Ts.Core.Tristate.True,
            Module = global::Ts.Core.ModuleKind.ESNext,
            ModuleResolution = global::Ts.Core.ModuleResolutionKind.Bundler,
            Target = global::Ts.Core.ScriptTarget.LatestStandard,
            Jsx = global::Ts.Core.JsxEmit.ReactJSX,
            AllowImportingTsExtensions = global::Ts.Core.Tristate.True,
            StrictNullChecks = global::Ts.Core.Tristate.True,
            StrictFunctionTypes = global::Ts.Core.Tristate.True,
            SourceMap = global::Ts.Core.Tristate.True,
            AllowNonTsExtensions = global::Ts.Core.Tristate.True,
            ResolveJsonModule = global::Ts.Core.Tristate.True,
        };
    }

    private static bool HaveSameFileNames(
        string[] left,
        string[] right,
        global::Ts.Vfs.IFileSystem fileSystem)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var paths = new HashSet<global::Ts.TsPath.Path>();
        foreach (string fileName in left)
        {
            paths.Add(global::Ts.TsPath.TsPath.ToPath(fileName, "", fileSystem.UseCaseSensitiveFileNames()));
        }

        foreach (string fileName in right)
        {
            if (!paths.Remove(global::Ts.TsPath.TsPath.ToPath(fileName, "", fileSystem.UseCaseSensitiveFileNames())))
            {
                return false;
            }
        }

        return paths.Count == 0;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
