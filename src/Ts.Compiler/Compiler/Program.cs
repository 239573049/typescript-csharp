// Ported from tsgo internal/compiler/program.go.
//
// SCOPE (phase 4/5 milestone): this brings the load→parse→bind pipeline online and satisfies the
// Ts.Checker.Program consumer interface. The load pipeline (NewProgram → processAllProgramFiles),
// binding (BindSourceFiles), the checker-pool wiring, and the module-format / resolution interface
// methods are ported real. The diagnostics-collection orchestration, emit, project-reference
// verification, symlink cache, and common-source-directory surfaces depend on packages/helpers that
// are deferred (printer/transformers, outputpaths emit helpers, IsPlainJSFile/IsCheckJSEnabledForFile);
// those methods throw NotImplementedException with a PORT-NOTE and are not reached by the milestone.
//
// C# has no Go struct embedding, so Program composes the immutable ProcessedFiles snapshot rather
// than embedding it.
//
// §1.5 (namespace shadowing): sibling-namespace static classes / types are fully qualified with global::.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Module;
using Ts.PackageJson;

namespace Ts.Compiler;

// program.go:35-44
public sealed class ProgramOptions
{
    public CompilerHost Host = null!;
    public global::Ts.TsOptions.ParsedCommandLine Config = null!;
    public bool UseSourceOfProjectReference;
    public Tristate SingleThreaded;
    public Func<Program, global::Ts.Checker.ICheckerPool>? CreateCheckerPool;
    public string TypingsLocation = "";
    public string ProjectName = "";
    public global::Ts.Tracing.Tracing? Tracing;

    // program.go:46-48
    internal bool CanUseProjectReferenceSource() =>
        UseSourceOfProjectReference && !Config.CompilerOptions()!.DisableSourceOfProjectReferenceRedirect.IsTrue();
}

// program.go:79-119
public sealed partial class Program : global::Ts.Checker.Program
{
    private readonly ProgramOptions opts;
    private global::Ts.Checker.ICheckerPool checkerPool = null!; // always set; fallback for project-system pools
    private CheckerPool? compilerCheckerPool; // set only when the built-in compiler checker pool is in use

    private global::Ts.TsPath.ComparePathsOptions comparePathsOptions;

    private readonly ProcessedFiles processed;

    private readonly Once packagesMapOnce = new();
    private Dictionary<string, bool>? packagesMap;

    // program.go:274-283 (NewProgram)
    public Program(ProgramOptions opts)
    {
        this.opts = opts;
        processed = FileLoader.ProcessAllProgramFiles(opts, SingleThreaded());
        comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
        {
            UseCaseSensitiveFileNames = opts.Host.FS().UseCaseSensitiveFileNames(),
            CurrentDirectory = opts.Host.GetCurrentDirectory(),
        };
        InitCheckerPool();
        VerifyCompilerOptions();
    }

    internal ProgramOptions Opts => opts;

    internal SourceFile[] Files => processed.Files;

    internal Resolver Resolver => processed.Resolver;

    private ProjectReferenceFileMapper Mapper => processed.ProjectReferenceFileMapper;

    // program.go:340-352
    private void InitCheckerPool()
    {
        if (!processed.FinishedProcessing)
        {
            throw new InvalidOperationException("Program must finish processing files before initializing checker pool");
        }

        if (opts.CreateCheckerPool is not null)
        {
            checkerPool = opts.CreateCheckerPool(this);
        }
        else
        {
            var pool = new CheckerPool(this, opts.Tracing);
            checkerPool = pool;
            compilerCheckerPool = pool;
        }
    }

    // program.go:355-357
    public global::Ts.Checker.ICheckerPool GetCheckerPool() => checkerPool;

    // program.go:446-448
    public bool SingleThreaded() => opts.SingleThreaded.DefaultIfUnknown(Options().SingleThreaded).IsTrue();

    // program.go:402-407
    public SourceFile[] SourceFiles() => processed.Files;

    public global::Ts.TsOptions.ParsedCommandLine CommandLine() => opts.Config;

    public CompilerHost Host() => opts.Host;

    // program.go:404
    public CompilerOptions Options() => opts.Config.CompilerOptions()!;

    // program.go:450-463 — the phase 4/5 milestone: parallel binding of all parsed files.
    public void BindSourceFiles()
    {
        var wg = WorkGroup.Create(SingleThreaded());
        foreach (var file in processed.Files)
        {
            if (!file.IsBound())
            {
                var f = file;
                wg.Queue(() => global::Ts.Binder.Binder.BindSourceFile(f));
            }
        }
        wg.RunAndWait();
    }

    // program.go:466-497 — type checker acquisition (delegates to the pool).
    public (global::Ts.Checker.Checker Checker, Action Release) GetTypeChecker(CancellationToken ctx)
    {
        if (compilerCheckerPool is not null)
        {
            return compilerCheckerPool.GetCheckerNonExclusive();
        }
        return checkerPool.GetChecker(ctx, null);
    }

    public void ForEachCheckerParallel(Action<int, global::Ts.Checker.Checker> cb)
    {
        compilerCheckerPool?.ForEachCheckerParallel(cb);
    }

    public (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFile(CancellationToken ctx, SourceFile file)
    {
        if (compilerCheckerPool is not null)
        {
            return compilerCheckerPool.GetCheckerForFileNonExclusive(file);
        }
        return checkerPool.GetChecker(ctx, file);
    }

    public (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFileExclusive(CancellationToken ctx, SourceFile file)
    {
        if (compilerCheckerPool is not null)
        {
            return compilerCheckerPool.GetCheckerForFileExclusive(ctx, file);
        }
        return checkerPool.GetChecker(ctx, file);
    }

    // program.go:499-518
    public ResolvedModule? GetResolvedModule(HasFileName file, string moduleReference, ResolutionMode mode)
    {
        if (processed.ResolvedModules.TryGetValue(file.Path(), out var resolutions) &&
            resolutions.TryGetValue(new ModeAwareCacheKey(moduleReference, mode), out var resolved))
        {
            return resolved;
        }
        return null;
    }

    // program.go:508-514
    public ResolvedModule? GetResolvedModuleFromModuleSpecifier(HasFileName file, StringLiteralLikeNode? moduleSpecifier)
    {
        if (moduleSpecifier is null || !AstUtilities.IsStringLiteralLike(moduleSpecifier))
        {
            throw new InvalidOperationException("moduleSpecifier must be a StringLiteralLike");
        }
        var mode = GetModeForUsageLocation(file, moduleSpecifier);
        return GetResolvedModule(file, moduleSpecifier.Text(), mode);
    }

    // program.go:516-518
    public Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedModule>> GetResolvedModules() => processed.ResolvedModules;

    // program.go:522-534
    public Dictionary<string, bool> GetPackagesMap()
    {
        packagesMapOnce.Do(() =>
        {
            var map = new Dictionary<string, bool>();
            // map-iter: 序无关 —— OR-folds a per-package boolean; independent of iteration order.
            foreach (var resolvedModulesInFile in processed.ResolvedModules.Values)
            {
                foreach (var mod in resolvedModulesInFile.Values)
                {
                    // PORT-NOTE: Go PackageId.Name zero value "" ↔ C# default(PackageId).Name null.
                    if (!string.IsNullOrEmpty(mod.PackageId.Name))
                    {
                        map[mod.PackageId.Name] = map.GetValueOrDefault(mod.PackageId.Name) || mod.Extension == global::Ts.TsPath.TsPath.ExtensionDts;
                    }
                }
            }
            packagesMap = map;
        });
        return packagesMap!;
    }

    // program.go:1524-1526
    public SourceFileMetaData GetSourceFileMetaData(global::Ts.TsPath.Path path) =>
        processed.SourceFileMetaDatas.GetValueOrDefault(path);

    // program.go:1528-1530
    public ModuleKind GetEmitModuleFormatOfFile(HasFileName sourceFile) =>
        ModuleFormat.GetEmitModuleFormatOfFileWorker(sourceFile.FileName(), Mapper.GetCompilerOptionsForFile(sourceFile), GetSourceFileMetaData(sourceFile.Path()));

    // program.go:1536-1538
    public ModuleKind GetImpliedNodeFormatForEmit(HasFileName sourceFile) =>
        ModuleFormat.GetImpliedNodeFormatForEmitWorker(sourceFile.FileName(), Mapper.GetCompilerOptionsForFile(sourceFile).GetEmitModuleKind(), GetSourceFileMetaData(sourceFile.Path()));

    // program.go:1540-1542
    public ResolutionMode GetModeForUsageLocation(HasFileName sourceFile, StringLiteralLikeNode? location) =>
        FileLoader.GetModeForUsageLocation(sourceFile.FileName(), GetSourceFileMetaData(sourceFile.Path()), location!, Mapper.GetCompilerOptionsForFile(sourceFile));

    // program.go:1544-1546
    public ResolutionMode GetDefaultResolutionModeForFile(HasFileName sourceFile) =>
        FileLoader.GetDefaultResolutionModeForFile(sourceFile.FileName(), GetSourceFileMetaData(sourceFile.Path()), Mapper.GetCompilerOptionsForFile(sourceFile));

    // program.go:1532-1534
    public ResolutionMode GetEmitSyntaxForUsageLocation(HasFileName sourceFile, StringLiteralLikeNode? location) =>
        FileLoader.GetEmitSyntaxForUsageLocationWorker(sourceFile.FileName(), GetSourceFileMetaData(sourceFile.Path()), location!, Mapper.GetCompilerOptionsForFile(sourceFile));

    // program.go:1921-1926
    public (string ModuleReference, Node? Specifier) GetJSXRuntimeImportSpecifier(global::Ts.TsPath.Path path)
    {
        if (processed.JsxRuntimeImportSpecifiers is not null && processed.JsxRuntimeImportSpecifiers.TryGetValue(path, out var result))
        {
            return (result.ModuleReference, result.Specifier);
        }
        return ("", null);
    }

    // program.go:1928-1930
    public Node? GetImportHelpersImportSpecifier(global::Ts.TsPath.Path path) =>
        processed.ImportHelpersImportSpecifiers?.GetValueOrDefault(path);

    // program.go:1548-1551
    public bool IsSourceFileDefaultLibrary(global::Ts.TsPath.Path path) => processed.LibFiles.ContainsKey(path);

    // program.go:1917-1919
    public bool IsSourceFileFromExternalLibrary(SourceFile file) => processed.SourceFilesFoundSearchingNodeModules.Has(file.Path());

    // emitter.go:450-503 — sourceFileMayBeEmitted (the common, non-json path is real).
    public bool SourceFileMayBeEmitted(SourceFile sourceFile, bool forceDtsEmit)
    {
        var options = Options();
        if (options.NoEmitForJsFiles.IsTrue() && AstUtilities.IsSourceFileJS(sourceFile))
        {
            return false;
        }
        if (sourceFile.IsDeclarationFile)
        {
            return false;
        }
        if (IsSourceFileFromExternalLibrary(sourceFile))
        {
            return false;
        }
        if (forceDtsEmit)
        {
            return true;
        }
        if (GetProjectReferenceFromSource(sourceFile.Path()) is not null)
        {
            return false;
        }
        if (!AstUtilities.IsJsonSourceFile(sourceFile))
        {
            return true;
        }
        if (options.OutDir == "")
        {
            return false;
        }
        // PORT-NOTE (phase 4/5): the json-file emit-location check needs outputpaths.GetCommonSourceDirectory /
        // GetSourceFilePathInNewDirWorker (deferred emit helpers). For a json root under outDir, tsgo emits it;
        // returning true is the safe over-approximation until those land.
        return true;
    }

    // program.go:1793-1795
    private global::Ts.TsPath.Path ToPath(string filename) =>
        global::Ts.TsPath.TsPath.ToPath(filename, GetCurrentDirectory(), UseCaseSensitiveFileNames());

    // program.go:1797-1800
    public SourceFile? GetSourceFile(string filename) => GetSourceFileByPath(ToPath(filename));

    // program.go:1817-1819
    public SourceFile? GetSourceFileByPath(global::Ts.TsPath.Path path) => processed.FilesByPath.GetValueOrDefault(path);

    // program.go:1802-1811
    public SourceFile? GetSourceFileForResolvedModule(string fileName)
    {
        var file = GetSourceFile(fileName);
        if (file is null)
        {
            var redirected = GetParseFileRedirect(fileName);
            if (redirected != "")
            {
                return GetSourceFile(redirected);
            }
        }
        return file;
    }

    // program.go:200-202
    public string GetParseFileRedirect(string fileName) =>
        Mapper.GetParseFileRedirect(new CompilerHasFileName(fileName, ToPath(fileName)));

    // program.go:187-189
    public global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromOutputDts(global::Ts.TsPath.Path path) =>
        Mapper.GetProjectReferenceFromOutputDts(path);

    // program.go:178-180
    public global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(global::Ts.TsPath.Path path) =>
        Mapper.GetProjectReferenceFromSource(path);

    // program.go:195-198
    public global::Ts.TsOptions.ParsedCommandLine? GetRedirectForResolution(HasFileName file)
    {
        var (redirect, _) = Mapper.GetRedirectForResolution(file);
        return redirect;
    }

    // program.go:162-164
    public string[] GetRedirectTargets(global::Ts.TsPath.Path path) =>
        processed.RedirectTargetsMap?.GetValueOrDefault(path)?.ToArray() ?? [];

    // program.go:170-175
    public string GetSourceOfProjectReferenceIfOutputIncluded(HasFileName file)
    {
        if (processed.OutputFileToProjectReferenceSource is not null && processed.OutputFileToProjectReferenceSource.TryGetValue(file.Path(), out var source))
        {
            return source;
        }
        return file.FileName();
    }

    // program.go:122-124
    public bool FileExists(string path) => Host().FS().FileExists(path);

    // program.go:127-129
    public string GetCurrentDirectory() => Host().GetCurrentDirectory();

    // program.go:220-222
    public bool UseCaseSensitiveFileNames() => Host().FS().UseCaseSensitiveFileNames();

    // program.go:132-134
    public string GetGlobalTypingsCacheLocation() => opts.TypingsLocation;

    // program.go:137-143
    public string GetNearestAncestorDirectoryWithPackageJson(string dirname)
    {
        var scoped = Resolver.GetPackageScopeForPath(dirname);
        if (scoped is not null && scoped.Exists())
        {
            return scoped.PackageDirectory;
        }
        return "";
    }

    // program.go:146-153
    public InfoCacheEntry? GetPackageJsonInfo(string pkgJsonPath)
    {
        var directory = global::Ts.TsPath.TsPath.GetDirectoryPath(pkgJsonPath);
        var scoped = Resolver.GetPackageScopeForPath(directory);
        if (scoped is not null && scoped.Exists() && scoped.PackageDirectory == directory)
        {
            return scoped;
        }
        return null;
    }

    // program.go:1567-1583 — deferred (needs outputpaths.GetCommonSourceDirectory + checkSourceFilesBelongToPath).
    public string CommonSourceDirectory() =>
        throw new NotImplementedException("CommonSourceDirectory is deferred (outputpaths common-source-directory, phase 4/5)");

    // program.go:2022-2069 — deferred (lazy symlink cache from resolutions/package.json).
    public global::Ts.Symlinks.KnownSymlinks GetSymlinkCache() =>
        throw new NotImplementedException("GetSymlinkCache is deferred (symlink cache population, phase 4/5)");

    // program.go:729-1223 — full compiler-option validation emits program diagnostics; deferred with the
    // rest of the diagnostics surface (depends on outputpaths emit helpers). No-op for the milestone.
    private void VerifyCompilerOptions()
    {
        // PORT-NOTE (phase 4/5): option validation is a program-diagnostics surface; deferred.
    }

    // program.go:1633-1695 — Program.Emit lives in Program.Emit.cs (this wave).

    // program.go:1436-1440 — the diagnostics aggregation surface lands with the CLI type-check-and-report wave.
    // SortAndDeduplicateDiagnostics and the Get*Diagnostics methods are ported in Program.Diagnostics.cs.
}
