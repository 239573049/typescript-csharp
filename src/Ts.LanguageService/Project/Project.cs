// Foundation subset of tsgo internal/project/project.go.
//
// The project-specific checker pool, ATA state, auto-import cache, and project-
// reference graph are intentionally not part of this milestone. Program update
// still follows the Go dirty/reload lifecycle, but rebuilds because the current
// C# Compiler.Program does not yet expose Go's UpdateProgram/HasSameFileNames.

using System;
using System.Collections.Generic;

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

public sealed class Project : IDisposable
{
    internal const string InferredProjectName = "/dev/null/inferred";

    private readonly string currentDirectory;
    private readonly string configFileName;
    private readonly global::Ts.TsPath.Path configFilePath;
    private readonly List<global::Ts.FsWatch.IWatch> watches = [];
    private bool disposed;

    private Project(Kind kind, string name, string currentDirectory, bool useCaseSensitiveFileNames)
    {
        Kind = kind;
        configFileName = name;
        this.currentDirectory = currentDirectory;
        configFilePath = global::Ts.TsPath.TsPath.ToPath(name, currentDirectory, useCaseSensitiveFileNames);
        IsDirty = true;
    }

    public Kind Kind { get; }

    public global::Ts.TsOptions.ParsedCommandLine? CommandLine { get; private set; }

    public global::Ts.Compiler.Program? Program { get; private set; }

    public ProgramUpdateKind ProgramUpdateKind { get; private set; }

    public ulong ProgramLastUpdate { get; private set; }

    public bool IsDirty { get; private set; }

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

    public string DisplayName(string cwd)
    {
        if (Kind == Kind.Inferred)
        {
            return global::Ts.TsPath.TsPath.GetBaseFileName(currentDirectory);
        }

        return global::Ts.TsPath.TsPath.ConvertToRelativePath(
            configFileName,
            new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = cwd,
            });
    }

    public bool HasFile(string fileName)
    {
        global::Ts.Compiler.Program? program = Program;
        if (program is null)
        {
            return false;
        }

        global::Ts.TsPath.Path path = global::Ts.TsPath.TsPath.ToPath(
            fileName,
            currentDirectory,
            program.Host().FS().UseCaseSensitiveFileNames());
        return program.GetSourceFileByPath(path) is not null;
    }

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
