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
using Diag = global::Ts.Diagnostics.Diagnostics;

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
public sealed partial class Program : global::Ts.Checker.Program, global::Ts.OutputPaths.OutputPathsHost, ProgramLike
{
    private readonly ProgramOptions opts;
    private global::Ts.Checker.ICheckerPool checkerPool = null!; // always set; fallback for project-system pools
    private CheckerPool? compilerCheckerPool; // set only when the built-in compiler checker pool is in use

    private global::Ts.TsPath.ComparePathsOptions comparePathsOptions;

    private readonly ProcessedFiles processed;

    private readonly Once packagesMapOnce = new();
    private Dictionary<string, bool>? packagesMap;

    // program.go:107 — knownSymlinks lazyValue[symlinks.KnownSymlinks].
    private readonly LazyValue<global::Ts.Symlinks.KnownSymlinks> knownSymlinks = new();

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

    // Internal accessors for the include-processor diagnostics surface (Go reads the program
    // fields directly; C# composes ProcessedFiles, so the compiler-package peers go through these).
    internal IncludeProcessor IncludeProcessorInstance => processed.IncludeProcessor;

    internal Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedModule>> ResolvedModulesMap => processed.ResolvedModules;

    internal Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedTypeReferenceDirective>> TypeResolutionsInFileMap => processed.TypeResolutionsInFile;

    // program.go:1906-1908
    public Dictionary<global::Ts.TsPath.Path, ModeAwareCache<ResolvedTypeReferenceDirective>> GetResolvedTypeReferenceDirectives() => processed.TypeResolutionsInFile;

    // program.go:1813-1815
    public Dictionary<global::Ts.TsPath.Path, SourceFile> FilesByPath() => processed.FilesByPath;

    // program.go:1475-1481
    public int LineCount()
    {
        var count = 0;
        foreach (var file in processed.Files)
        {
            count += file.ECMALineMap().Length;
        }
        return count;
    }

    // program.go:1483-1489
    public int IdentifierCount()
    {
        var count = 0;
        foreach (var file in processed.Files)
        {
            count += file.IdentifierCount;
        }
        return count;
    }

    // program.go:1491-1502
    public int SymbolCount()
    {
        var count = 0;
        foreach (var file in processed.Files)
        {
            count += file.SymbolCount;
        }
        var val = (uint)count;
        ForEachCheckerParallel((_, c) => global::System.Threading.Interlocked.Add(ref val, c.SymbolCount));
        return (int)val;
    }

    // program.go:1504-1510
    public int TypeCount()
    {
        var val = 0u;
        ForEachCheckerParallel((_, c) => global::System.Threading.Interlocked.Add(ref val, c.TypeCount));
        return (int)val;
    }

    // program.go:1512-1518
    public int InstantiationCount()
    {
        var val = 0u;
        ForEachCheckerParallel((_, c) => global::System.Threading.Interlocked.Add(ref val, c.TotalInstantiationCount));
        return (int)val;
    }

    internal Dictionary<global::Ts.TsPath.Path, RedirectsFile>? RedirectFilesByPathMap => processed.RedirectFilesByPath;

    internal global::Ts.TsPath.ComparePathsOptions ComparePathsOptions => comparePathsOptions;

    // program.go:96 — programDiagnostics is populated by verifyCompilerOptions / verifyProjectReferences /
    // blockEmittingOfFile and drained by GetProgramDiagnostics.
    private readonly List<Diagnostic> programDiagnostics = new();

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

    // program.go:1553-1558 — IsGlobalTypingsFile.
    public bool IsGlobalTypingsFile(string fileName)
    {
        if (!global::Ts.TsPath.TsPath.IsDeclarationFileName(fileName))
        {
            return false;
        }
        return global::Ts.TsPath.TsPath.ContainsPath(GetGlobalTypingsCacheLocation(), fileName, comparePathsOptions);
    }

    // program.go:1936-1946 — ResolvedPackageNames / UnresolvedPackageNames / DeepImportPackageNames.
    public global::Ts.Collections.Set<string> ResolvedPackageNames() => CollectPackageNames().Resolved;

    public global::Ts.Collections.Set<string> UnresolvedPackageNames() => CollectPackageNames().Unresolved;

    public global::Ts.Collections.Set<string> DeepImportPackageNames() => CollectPackageNames().DeepImportPackages;

    private sealed record PackageNamesInfo(
        global::Ts.Collections.Set<string> Resolved,
        global::Ts.Collections.Set<string> Unresolved,
        global::Ts.Collections.Set<string> DeepImportPackages);

    private readonly LazyValue<PackageNamesInfo> packageNames = new();

    // program.go:1948-2004 — collectPackageNames.
    private PackageNamesInfo CollectPackageNames()
    {
        return packageNames.GetValue(() =>
        {
            var names = new PackageNamesInfo(new(), new(), new());
            foreach (var file in processed.Files)
            {
                if (IsSourceFileDefaultLibrary(file.Path()) || IsSourceFileFromExternalLibrary(file) || file.FileName().Contains("/node_modules/", StringComparison.Ordinal))
                {
                    // Checking for /node_modules/ is a little imprecise, but ATA treats locally installed typings
                    // as root files, which would not pass IsSourceFileFromExternalLibrary.
                    continue;
                }
                foreach (var imp in file.Imports() ?? [])
                {
                    if (global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(imp.Text()))
                    {
                        continue;
                    }
                    if (processed.ResolvedModules.TryGetValue(file.Path(), out var resolvedModules))
                    {
                        var key = new global::Ts.Module.ModeAwareCacheKey(imp.Text(), GetModeForUsageLocation(file, imp));
                        if (resolvedModules.TryGetValue(key, out var resolvedModule) && resolvedModule.IsResolved())
                        {
                            if (!resolvedModule!.IsExternalLibraryImport)
                            {
                                continue;
                            }
                            // Priority order for getting package name:
                            // 1. PackageId.Name (requires both name and version in package.json)
                            var name = resolvedModule.PackageId.Name;
                            if (string.IsNullOrEmpty(name))
                            {
                                // 2. GetPackageScopeForPath - get name from package.json in the package directory
                                var packageScope = Resolver.GetPackageScopeForPath(resolvedModule.ResolvedFileName);
                                if (packageScope != null && packageScope.Exists())
                                {
                                    if (packageScope.Contents!.Name.GetValue(out var scopeName))
                                    {
                                        name = scopeName;
                                    }
                                }
                            }
                            if (string.IsNullOrEmpty(name))
                            {
                                // 3. GetPackageNameFromDirectory - extract from node_modules path
                                name = global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.GetPackageNameFromDirectory(resolvedModule.ResolvedFileName);
                            }
                            // 4. If all fail, don't add empty string
                            if (!string.IsNullOrEmpty(name))
                            {
                                names.Resolved.Add(name!);
                                // Detect deep imports: subpath imports in packages without exports.
                                // These are imports like "lodash/fp" where the package has no exports
                                // map, so auto-import can only find them via recursive directory search.
                                var (_, rest) = global::Ts.Module.Module.ParsePackageName(imp.Text());
                                if (rest.Length != 0)
                                {
                                    var scope = Resolver.GetPackageScopeForPath(resolvedModule.ResolvedFileName);
                                    if (scope != null && scope.Exists() && !scope.Contents!.Exports.IsPresent())
                                    {
                                        names.DeepImportPackages.Add(global::Ts.Module.Module.GetPackageNameFromTypesPackageName(name!));
                                    }
                                }
                            }
                            continue;
                        }
                    }
                    names.Unresolved.Add(imp.Text());
                }
            }
            return names;
        })!;
    }

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

    // program.go:1560-1565
    public LibFile? GetDefaultLibFile(global::Ts.TsPath.Path path) => processed.LibFiles.GetValueOrDefault(path);

    // program.go:1835-1837
    public Dictionary<global::Ts.TsPath.Path, List<FileIncludeReason>> GetIncludeReasons() => processed.IncludeProcessor.FileIncludeReasons;

    // program.go:1846-1884
    public void ExplainFiles(System.IO.TextWriter w, global::Ts.Locale.Locale locale)
    {
        string ToRelativeFileName(string fileName) =>
            global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(GetCurrentDirectory(), fileName, comparePathsOptions);

        var filesExplained = 0;
        void ExplainFile(HasFileName file)
        {
            w.Write(ToRelativeFileName(file.FileName()));
            w.Write('\n');
            if (processed.IncludeProcessor.FileIncludeReasons.TryGetValue(file.Path(), out var reasons))
            {
                foreach (var reason in reasons)
                {
                    w.Write("   ");
                    w.Write(reason.ToDiagnostic(this, true).Localize(locale));
                    w.Write('\n');
                }
            }
            foreach (var diag in processed.IncludeProcessor.ExplainRedirectAndImpliedFormat(this, file.Path(), ToRelativeFileName) ?? [])
            {
                w.Write("   ");
                w.Write(diag.Localize(locale));
                w.Write('\n');
            }
            filesExplained++;
        }

        var redirectFiles = new List<RedirectsFile>((processed.RedirectFilesByPath ?? new Dictionary<global::Ts.TsPath.Path, RedirectsFile>()).Values); // map-iter: 已排序 (sorted by Index below)
        redirectFiles.Sort((a, b) => a.Index.CompareTo(b.Index));

        var files = SourceFiles();
        var sourceFileIndex = 0;
        void ExplainSourceFiles(int endIndex)
        {
            while (filesExplained < endIndex)
            {
                ExplainFile(files[sourceFileIndex]);
                sourceFileIndex++;
            }
        }

        foreach (var redirectFile in redirectFiles)
        {
            // Explain all sourceFiles till we reach this redirectFile index
            ExplainSourceFiles(redirectFile.Index);
            ExplainFile(redirectFile);
        }

        // Explain any remaining sourceFiles
        ExplainSourceFiles(files.Length + redirectFiles.Count);
    }

    // program.go:1840-1844
    public bool IsMissingPath(global::Ts.TsPath.Path path)
    {
        foreach (var missingPath in processed.MissingFiles)
        {
            if (ToPath(missingPath) == path)
            {
                return true;
            }
        }
        return false;
    }

    // program.go:156-158
    public void PackageJsonCacheEntries(Func<global::Ts.TsPath.Path, global::Ts.PackageJson.InfoCacheEntry, bool> f) => Resolver.PackageJsonCacheEntries(f);

    // program.go:407
    public global::Ts.Tracing.Tracing? Tracing() => opts.Tracing;

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

    // program.go:231 — GetSourceFileFromReference. Resolves the SourceFile a triple-slash
    // /// <reference path="..."/> points at (used by declaration emit's reference-directive emit).
    public SourceFile? GetSourceFileFromReference(SourceFile origin, FileReference reference)
    {
        string fileName = global::Ts.TsPath.TsPath.ResolvePath(global::Ts.TsPath.TsPath.GetDirectoryPath(origin.FileName()), reference.FileName);
        string[][] supportedExtensionsBase = global::Ts.TsOptions.TsConfigParsing.GetSupportedExtensions(Options(), null /*extraFileExtensions*/);
        string[][] supportedExtensions = global::Ts.TsOptions.TsConfigParsing.GetSupportedExtensionsWithJsonIfResolveJsonModule(Options(), supportedExtensionsBase);
        bool allowNonTsExtensions = Options().AllowNonTsExtensions.IsTrue();
        if (global::Ts.TsPath.TsPath.HasExtension(fileName))
        {
            if (!allowNonTsExtensions)
            {
                string canonicalFileName = global::Ts.TsPath.TsPath.GetCanonicalFileName(fileName, UseCaseSensitiveFileNames());
                bool supported = false;
                foreach (string[] group in supportedExtensions)
                {
                    if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(canonicalFileName, group))
                    {
                        supported = true;
                        break;
                    }
                }
                if (!supported)
                {
                    return null; // unsupported extensions are forced to fail
                }
            }

            return GetSourceFileForResolvedModule(fileName);
        }
        if (allowNonTsExtensions)
        {
            SourceFile? extensionless = GetSourceFileForResolvedModule(fileName);
            if (extensionless != null)
            {
                return extensionless;
            }
        }

        // Only try adding extensions from the first supported group (which should be .ts/.tsx/.d.ts).
        foreach (string ext in supportedExtensions[0])
        {
            SourceFile? result = GetSourceFileForResolvedModule(fileName + ext);
            if (result != null)
            {
                return result;
            }
        }
        return null;
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

    private string commonSourceDirectory = "";
    private readonly Once commonSourceDirectoryOnce = new();

    // program.go:1567-1583
    public string CommonSourceDirectory()
    {
        commonSourceDirectoryOnce.Do(() =>
        {
            Func<string[]> files = () =>
            {
                // core.MapFiltered over p.files.
                var result = new List<string>(processed.Files.Length);
                foreach (var file in processed.Files)
                {
                    if (SourceFileMayBeEmitted(file, false /*forceDtsEmit*/) && !file.IsDeclarationFile)
                    {
                        result.Add(file.FileName());
                    }
                }
                return result.ToArray();
            };
            commonSourceDirectory = global::Ts.OutputPaths.OutputPaths.GetCommonSourceDirectory(
                Options(),
                files,
                GetCurrentDirectory(),
                UseCaseSensitiveFileNames(),
                CheckSourceFilesBelongToPath);
        });
        return commonSourceDirectory;
    }

    // program.go:1585-1607
    private bool CheckSourceFilesBelongToPath(string[] sourceFiles, string rootDirectory)
    {
        bool allFilesBelongToPath = true;
        foreach (var file in sourceFiles)
        {
            var absoluteSourceFilePath = global::Ts.TsPath.TsPath.GetCanonicalFileName(
                global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(file, GetCurrentDirectory()), UseCaseSensitiveFileNames());
            if (!global::Ts.TsPath.TsPath.ContainsPath(rootDirectory, file, comparePathsOptions))
            {
                processed.IncludeProcessor.AddProcessingDiagnostic(new ProcessingDiagnostic
                {
                    Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                    Data = new IncludeExplainingDiagnostic
                    {
                        File = new global::Ts.TsPath.Path(absoluteSourceFilePath),
                        Message = Diag.File_0_is_not_under_rootDir_1_rootDir_is_expected_to_contain_all_source_files,
                        Args = [file, rootDirectory],
                    },
                });
                allFilesBelongToPath = false;
            }
        }
        return allFilesBelongToPath;
    }

    // program.go:2022 — GetSymlinkCache.
    public global::Ts.Symlinks.KnownSymlinks GetSymlinkCache() =>
        knownSymlinks.GetValue(() =>
        {
            var cache = new global::Ts.Symlinks.KnownSymlinks(GetCurrentDirectory(), UseCaseSensitiveFileNames());

            // Resolved modules store realpath information when they're resolved inside node_modules.
            if (processed.ResolvedModules.Count > 0 || processed.TypeResolutionsInFile.Count > 0)
            {
                cache.SetSymlinksFromResolutions(ForEachResolvedModule, ForEachResolvedTypeReferenceDirective);
            }

            // Check other dependencies for symlinks.
            var seenPackageJsons = new Set<global::Ts.TsPath.Path>();
            // map-iter: 序无关 (symlink accumulation is commutative; SetDirectory/SetFile are keyed and idempotent)
            foreach (var (filePath, meta) in processed.SourceFileMetaDatas)
            {
                if (meta.PackageJsonDirectory == "" ||
                    !SourceFileMayBeEmitted(GetSourceFileByPath(filePath)!, false) ||
                    !seenPackageJsons.AddIfAbsent(ToPath(meta.PackageJsonDirectory)))
                {
                    continue;
                }
                string packageJsonName = global::Ts.TsPath.TsPath.CombinePaths(meta.PackageJsonDirectory, "package.json");
                var info = GetPackageJsonInfo(packageJsonName);
                var contents = info.GetContents();
                if (contents == null)
                {
                    continue;
                }

                foreach (string dep in contents.DependencyFields.GetRuntimeDependencyNames().Keys())
                {
                    // Skip work in common case: we already saved a symlink for this package directory
                    // in the node_modules adjacent to this package.json.
                    global::Ts.TsPath.Path possibleDirectoryPath = ToPath(global::Ts.TsPath.TsPath.CombinePaths(meta.PackageJsonDirectory, "node_modules", dep));
                    if (cache.HasDirectory(possibleDirectoryPath))
                    {
                        continue;
                    }
                    if (!dep.StartsWith("@types", StringComparison.Ordinal))
                    {
                        global::Ts.TsPath.Path possibleTypesDirectoryPath = ToPath(global::Ts.TsPath.TsPath.CombinePaths(meta.PackageJsonDirectory, "node_modules", global::Ts.Module.Module.GetTypesPackageName(dep)));
                        if (cache.HasDirectory(possibleTypesDirectoryPath))
                        {
                            continue;
                        }
                    }

                    ResolvedModule? packageResolution = Resolver.ResolvePackageDirectory(dep, packageJsonName, global::Ts.Core.Core.ResolutionModeCommonJS, null);
                    if (packageResolution.IsResolved())
                    {
                        cache.ProcessResolution(
                            global::Ts.TsPath.TsPath.CombinePaths(packageResolution!.OriginalPath, "package.json"),
                            global::Ts.TsPath.TsPath.CombinePaths(packageResolution.ResolvedFileName, "package.json"));
                    }
                }
            }
            return cache;
        })!;

    // program.go:2076 — ForEachResolvedModule.
    public void ForEachResolvedModule(Action<ResolvedModule, string, ResolutionMode, global::Ts.TsPath.Path> callback, SourceFile? file) =>
        ForEachResolution(processed.ResolvedModules, callback, file);

    // program.go:2080 — ForEachResolvedTypeReferenceDirective.
    public void ForEachResolvedTypeReferenceDirective(Action<ResolvedTypeReferenceDirective, string, ResolutionMode, global::Ts.TsPath.Path> callback, SourceFile? file) =>
        ForEachResolution(processed.TypeResolutionsInFile, callback, file);

    // program.go:2084 — forEachResolution.
    private static void ForEachResolution<T>(
        Dictionary<global::Ts.TsPath.Path, ModeAwareCache<T>> resolutionCache,
        Action<T, string, ResolutionMode, global::Ts.TsPath.Path> callback,
        SourceFile? file)
    {
        if (file != null)
        {
            if (resolutionCache.TryGetValue(file.Path(), out var resolutions))
            {
                // map-iter: 序无关 (callback accumulates into the commutative symlink cache)
                foreach (var (key, resolution) in resolutions)
                {
                    callback(resolution, key.Name, key.Mode, file.Path());
                }
            }
        }
        else
        {
            // map-iter: 序无关
            foreach (var (filePath, resolutions) in resolutionCache)
            {
                foreach (var (key, resolution) in resolutions)
                {
                    callback(resolution, key.Name, key.Mode, filePath);
                }
            }
        }
    }

    // program.go:729-1223 — verifyCompilerOptions.
    // PORT-NOTE: only the emit-output-path verification tail (program.go:1178-1222 — TS5055
    // "would overwrite input file" / TS5056 "overwritten by multiple input files" + emit blocking)
    // is ported in this wave. The option-combination validation body (program.go:731-1176,
    // createDiagnosticForOptionName / createOptionValueDiagnostic and friends) is deferred with the
    // rest of the verifyCompilerOptions surface.
    private void VerifyCompilerOptions()
    {
        var options = Options();

        SourceFile? SourceFileSyntax() => opts.Config.ConfigFile?.SourceFile;

        string ConfigFilePath()
        {
            var file = SourceFileSyntax();
            return file is not null ? file.FileName() : "";
        }

        Node? GetCompilerOptionsPropertySyntax() =>
            global::Ts.TsOptions.TsConfigParsing.ForEachTsConfigPropArray<Node>(SourceFileSyntax(), "compilerOptions", CoreUtil.Identity);

        Node? GetCompilerOptionsObjectLiteralSyntax()
        {
            var compilerOptionsProperty = GetCompilerOptionsPropertySyntax();
            if (compilerOptionsProperty is not null &&
                compilerOptionsProperty.AsPropertyAssignment().Initializer is not null &&
                NodeTests.IsObjectLiteralExpression(compilerOptionsProperty.AsPropertyAssignment().Initializer!))
            {
                return compilerOptionsProperty.AsPropertyAssignment().Initializer;
            }
            return null;
        }

        Diagnostic? CreateOptionDiagnosticInObjectLiteralSyntax(Node? objectLiteral, bool onKey, string key1, string? key2, global::Ts.Diagnostics.Message message, params object?[] args)
        {
            var diag = global::Ts.TsOptions.TsConfigParsing.ForEachPropertyAssignment<Diagnostic>(objectLiteral, key1, property =>
                global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(SourceFileSyntax(), onKey ? property.AsPropertyAssignment().Name() : property.AsPropertyAssignment().Initializer, message, args), key2);
            if (diag is not null)
            {
                programDiagnostics.Add(diag);
            }
            return diag;
        }

        Diagnostic CreateCompilerOptionsDiagnostic(global::Ts.Diagnostics.Message message, params object?[] args)
        {
            var compilerOptionsProperty = GetCompilerOptionsPropertySyntax();
            Diagnostic diag;
            if (compilerOptionsProperty is not null)
            {
                diag = global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(SourceFileSyntax(), compilerOptionsProperty.AsPropertyAssignment().Name(), message, args);
            }
            else
            {
                diag = Diagnostic.NewCompilerDiagnostic(message, args);
            }
            programDiagnostics.Add(diag);
            return diag;
        }

        Diagnostic CreateDiagnosticForOption(bool onKey, string option1, string? option2, global::Ts.Diagnostics.Message message, params object?[] args)
        {
            var diag = CreateOptionDiagnosticInObjectLiteralSyntax(GetCompilerOptionsObjectLiteralSyntax(), onKey, option1, option2, message, args);
            return diag ?? CreateCompilerOptionsDiagnostic(message, args);
        }

        void CreateDiagnosticForOptionName(global::Ts.Diagnostics.Message message, string option1, string? option2, params object?[] args)
        {
            var newArgs = new object?[args.Length + 2];
            newArgs[0] = option1;
            newArgs[1] = option2;
            System.Array.Copy(args, 0, newArgs, 2, args.Length);
            CreateDiagnosticForOption(true /*onKey*/, option1, option2, message, newArgs);
        }

        void CreateOptionValueDiagnostic(string option1, global::Ts.Diagnostics.Message message, params object?[] args)
        {
            CreateDiagnosticForOption(false /*onKey*/, option1, "", message, args);
        }

        void CreateRemovedOptionDiagnostic(string name, string value, string useInstead)
        {
            global::Ts.Diagnostics.Message message;
            object?[] args;
            if (value == "")
            {
                message = Diag.Option_0_has_been_removed_Please_remove_it_from_your_configuration;
                args = [name];
            }
            else
            {
                message = Diag.Option_0_1_has_been_removed_Please_remove_it_from_your_configuration;
                args = [name, value];
            }
            var diag = CreateDiagnosticForOption(value == "", name, "", message, args);
            if (useInstead != "")
            {
                diag.AddMessageChain(Diagnostic.NewCompilerDiagnostic(Diag.Use_0_instead, useInstead));
            }
        }

        // Removed in TS7

        if (options.BaseUrl != "")
        {
            // BaseUrl will have been turned absolute by this point.
            var useInstead = "";
            if (ConfigFilePath() != "")
            {
                var relative = global::Ts.TsPath.TsPath.GetRelativePathFromFile(ConfigFilePath(), options.BaseUrl, ComparePathsOptions);
                if (!(relative.StartsWith("./", StringComparison.Ordinal) || relative.StartsWith("../", StringComparison.Ordinal)))
                {
                    relative = "./" + relative;
                }
                var suggestion = global::Ts.TsPath.TsPath.CombinePaths(relative, "*");
                useInstead = "\"paths\": {\"*\": [" + JsonMarshalString(suggestion) + "]}";
            }
            CreateRemovedOptionDiagnostic("baseUrl", "", useInstead);
        }

        if (options.OutFile != "")
        {
            CreateRemovedOptionDiagnostic("outFile", "", "");
        }

        if (options.Target == ScriptTarget.ES5)
        {
            CreateRemovedOptionDiagnostic("target", "ES5", "");
        }

        if (options.Module == ModuleKind.AMD)
        {
            CreateRemovedOptionDiagnostic("module", "AMD", "");
        }
        if (options.Module == ModuleKind.System)
        {
            CreateRemovedOptionDiagnostic("module", "System", "");
        }
        if (options.Module == ModuleKind.UMD)
        {
            CreateRemovedOptionDiagnostic("module", "UMD", "");
        }

        if (options.ModuleResolution == ModuleResolutionKind.Classic)
        {
            CreateRemovedOptionDiagnostic("moduleResolution", "Classic", "");
        }

        if (options.AlwaysStrict.IsFalse())
        {
            CreateRemovedOptionDiagnostic("alwaysStrict", "false", "");
        }

        if (options.ESModuleInterop.IsFalse())
        {
            CreateRemovedOptionDiagnostic("esModuleInterop", "false", "");
        }

        if (options.AllowSyntheticDefaultImports.IsFalse())
        {
            CreateRemovedOptionDiagnostic("allowSyntheticDefaultImports", "false", "");
        }

        if (options.ModuleResolution == ModuleResolutionKind.Node10)
        {
            CreateRemovedOptionDiagnostic("moduleResolution", "node10", "");
        }

        if (!options.DownlevelIteration.IsUnknown())
        {
            CreateRemovedOptionDiagnostic("downlevelIteration", "", "");
        }

        if (options.StrictPropertyInitialization.IsTrue() && !options.GetStrictOptionValue(options.StrictNullChecks))
        {
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1, "strictPropertyInitialization", "strictNullChecks");
        }
        if (options.ExactOptionalPropertyTypes.IsTrue() && !options.GetStrictOptionValue(options.StrictNullChecks))
        {
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1, "exactOptionalPropertyTypes", "strictNullChecks");
        }

        if (options.IsolatedDeclarations.IsTrue())
        {
            if (options.GetAllowJS())
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_with_option_1, "allowJs", "isolatedDeclarations");
            }
            if (!options.GetEmitDeclarations())
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "isolatedDeclarations", "declaration", "composite");
            }
        }

        if (options.InlineSourceMap.IsTrue())
        {
            if (options.SourceMap.IsTrue())
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_with_option_1, "sourceMap", "inlineSourceMap");
            }
            if (options.MapRoot != "")
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_with_option_1, "mapRoot", "inlineSourceMap");
            }
        }

        if (options.Composite.IsTrue())
        {
            if (options.Declaration.IsFalse())
            {
                CreateDiagnosticForOptionName(Diag.Composite_projects_may_not_disable_declaration_emit, "declaration", "");
            }
            if (options.Incremental.IsFalse())
            {
                CreateDiagnosticForOptionName(Diag.Composite_projects_may_not_disable_incremental_compilation, "declaration", "");
            }
        }

        if (options.TsBuildInfoFile == "" && options.Incremental.IsTrue() && options.ConfigFilePath == "")
        {
            CreateCompilerOptionsDiagnostic(Diag.Option_incremental_is_only_valid_with_a_known_configuration_file_like_tsconfig_json_or_when_tsBuildInfoFile_is_explicitly_provided);
        }

        VerifyProjectReferences();

        if (options.Composite.IsTrue())
        {
            var rootPaths = new global::Ts.Collections.Set<global::Ts.TsPath.Path>();
            foreach (var fileName in opts.Config.FileNames() ?? [])
            {
                rootPaths.Add(ToPath(fileName));
            }

            foreach (var file in Files)
            {
                if (SourceFileMayBeEmitted(file, false) && !rootPaths.Has(file.Path()))
                {
                    processed.IncludeProcessor.AddProcessingDiagnostic(new ProcessingDiagnostic
                    {
                        Kind = ProcessingDiagnosticKind.ExplainingFileInclude,
                        Data = new IncludeExplainingDiagnostic
                        {
                            File = file.Path(),
                            Message = Diag.File_0_is_not_listed_within_the_file_list_of_project_1_Projects_must_list_all_files_or_use_an_include_pattern,
                            Args = [file.FileName(), ConfigFilePath()],
                        },
                    });
                }
            }
        }

        Diagnostic? ForEachOptionPathsSyntax(Func<Node, Diagnostic?> callback) =>
            global::Ts.TsOptions.TsConfigParsing.ForEachPropertyAssignment(GetCompilerOptionsObjectLiteralSyntax(), "paths", callback);

        Diagnostic CreateDiagnosticForOptionPaths(bool onKey, string key, global::Ts.Diagnostics.Message message, params object?[] args)
        {
            var diag = ForEachOptionPathsSyntax(pathProp =>
            {
                if (pathProp.AsPropertyAssignment().Initializer is not null && NodeTests.IsObjectLiteralExpression(pathProp.AsPropertyAssignment().Initializer!))
                {
                    return CreateOptionDiagnosticInObjectLiteralSyntax(pathProp.AsPropertyAssignment().Initializer, onKey, key, "", message, args);
                }
                return null;
            });
            return diag ?? CreateCompilerOptionsDiagnostic(message, args);
        }

        Diagnostic CreateDiagnosticForOptionPathKeyValue(string key, int valueIndex, global::Ts.Diagnostics.Message message, params object?[] args)
        {
            var diag = ForEachOptionPathsSyntax(pathProp =>
            {
                if (pathProp.AsPropertyAssignment().Initializer is not null && NodeTests.IsObjectLiteralExpression(pathProp.AsPropertyAssignment().Initializer!))
                {
                    return global::Ts.TsOptions.TsConfigParsing.ForEachPropertyAssignment<Diagnostic>(pathProp.AsPropertyAssignment().Initializer, key, keyProps =>
                    {
                        var initializer = keyProps.AsPropertyAssignment().Initializer;
                        if (initializer is not null && NodeTests.IsArrayLiteralExpression(initializer))
                        {
                            var elements = initializer.AsArrayLiteralExpression().Elements;
                            if (elements is not null && elements.Nodes.Length > valueIndex)
                            {
                                var d = global::Ts.TsOptions.Errors.CreateDiagnosticForNodeInSourceFile(SourceFileSyntax(), elements.Nodes[valueIndex], message, args);
                                programDiagnostics.Add(d);
                                return d;
                            }
                        }
                        return null;
                    });
                }
                return null;
            });
            return diag ?? CreateCompilerOptionsDiagnostic(message, args);
        }

        if (options.Paths is not null)
        {
            foreach (var (key, value) in options.Paths.Entries())
            {
                // !!! This code does not handle cases where where the path mappings have the wrong types,
                // as that information is mostly lost during the parsing process.
                if (!HasZeroOrOneAsteriskCharacter(key))
                {
                    CreateDiagnosticForOptionPaths(true /*onKey*/, key, Diag.Pattern_0_can_have_at_most_one_Asterisk_character, key);
                }
                if (value is null)
                {
                    CreateDiagnosticForOptionPaths(false /*onKey*/, key, Diag.Substitutions_for_pattern_0_should_be_an_array, key);
                }
                else if (value.Length == 0)
                {
                    CreateDiagnosticForOptionPaths(false /*onKey*/, key, Diag.Substitutions_for_pattern_0_shouldn_t_be_an_empty_array, key);
                }
                if (value is not null)
                {
                    for (var i = 0; i < value.Length; i++)
                    {
                        var subst = value[i];
                        if (!HasZeroOrOneAsteriskCharacter(subst))
                        {
                            CreateDiagnosticForOptionPathKeyValue(key, i, Diag.Substitution_0_in_pattern_1_can_have_at_most_one_Asterisk_character, subst, key);
                        }
                        if (!global::Ts.TsPath.TsPath.PathIsRelative(subst) && !global::Ts.TsPath.TsPath.PathIsAbsolute(subst))
                        {
                            CreateDiagnosticForOptionPathKeyValue(key, i, Diag.Non_relative_paths_are_not_allowed_Did_you_forget_a_leading_Slash);
                        }
                    }
                }
            }
        }

        if (options.SourceMap.IsFalseOrUnknown() && options.InlineSourceMap.IsFalseOrUnknown())
        {
            if (options.InlineSources.IsTrue())
            {
                CreateDiagnosticForOptionName(Diag.Option_0_can_only_be_used_when_either_option_inlineSourceMap_or_option_sourceMap_is_provided, "inlineSources", "");
            }
            if (options.SourceRoot != "")
            {
                CreateDiagnosticForOptionName(Diag.Option_0_can_only_be_used_when_either_option_inlineSourceMap_or_option_sourceMap_is_provided, "sourceRoot", "");
            }
        }

        if (options.MapRoot != "" && !(options.SourceMap.IsTrue() || options.DeclarationMap.IsTrue()))
        {
            // Error to specify --mapRoot without --sourcemap
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "mapRoot", "sourceMap", "declarationMap");
        }

        if (options.DeclarationDir != "")
        {
            if (!options.GetEmitDeclarations())
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "declarationDir", "declaration", "composite");
            }
        }

        if (options.DeclarationMap.IsTrue() && !options.GetEmitDeclarations())
        {
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "declarationMap", "declaration", "composite");
        }

        if (options.Lib is not null && options.NoLib.IsTrue())
        {
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_with_option_1, "lib", "noLib");
        }

        if (options.IsolatedModules.IsTrue() || options.VerbatimModuleSyntax.IsTrue())
        {
            if (options.PreserveConstEnums.IsFalse())
            {
                CreateDiagnosticForOptionName(Diag.Option_preserveConstEnums_cannot_be_disabled_when_0_is_enabled, options.VerbatimModuleSyntax.IsTrue() ? "verbatimModuleSyntax" : "isolatedModules", "preserveConstEnums");
            }
        }

        if (options.OutDir != "" ||
            options.RootDir != "" ||
            options.SourceRoot != "" ||
            options.MapRoot != "" ||
            (options.GetEmitDeclarations() && options.DeclarationDir != ""))
        {
            // !!! sheetal checkSourceFilesBelongToPath - for root Dir and configFile - explaining why file is in the program
            var dir = CommonSourceDirectory();
            if (options.OutDir != "" && dir == "" && CoreUtil.Some(Files, f => global::Ts.TsPath.TsPath.GetRootLength(f.FileName()) > 1))
            {
                CreateDiagnosticForOptionName(Diag.Cannot_find_the_common_subdirectory_path_for_the_input_files, "outDir", "");
            }
        }

        if (!options.NoEmit.IsTrue() &&
            !options.Composite.IsTrue() &&
            options.RootDir == "" &&
            options.ConfigFilePath != "" &&
            (options.OutDir != "" ||
                (options.GetEmitDeclarations() && options.DeclarationDir != "") ||
                options.OutFile != ""))
        {
            // Check if rootDir inferred changed and issue diagnostic
            var dir = CommonSourceDirectory();
            var emittedFiles = new List<string>();
            foreach (var file in Files)
            {
                if (!file.IsDeclarationFile && SourceFileMayBeEmitted(file, false))
                {
                    emittedFiles.Add(file.FileName());
                }
            }
            var dir59 = global::Ts.OutputPaths.OutputPaths.GetComputedCommonSourceDirectory(emittedFiles.ToArray(), GetCurrentDirectory(), UseCaseSensitiveFileNames());
            if (dir59 != "" && global::Ts.TsPath.TsPath.GetCanonicalFileName(dir, UseCaseSensitiveFileNames()) != global::Ts.TsPath.TsPath.GetCanonicalFileName(dir59, UseCaseSensitiveFileNames()))
            {
                // change in layout
                string option1;
                if (options.OutFile != "")
                {
                    option1 = "outFile";
                }
                else if (options.OutDir != "")
                {
                    option1 = "outDir";
                }
                else
                {
                    option1 = "declarationDir";
                }
                string option2 = "";
                if (options.OutFile == "" && options.OutDir != "")
                {
                    option2 = "declarationDir";
                }
                var diag = CreateDiagnosticForOption(
                    true, /*onKey*/
                    option1,
                    option2,
                    Diag.The_common_source_directory_of_0_is_1_The_rootDir_setting_must_be_explicitly_set_to_this_or_another_path_to_adjust_your_output_s_file_layout,
                    global::Ts.TsPath.TsPath.GetBaseFileName(options.ConfigFilePath),
                    global::Ts.TsPath.TsPath.GetRelativePathFromFile(options.ConfigFilePath, dir59, ComparePathsOptions));
                diag.AddMessageChain(Diagnostic.NewCompilerDiagnostic(Diag.Visit_https_Colon_Slash_Slashaka_ms_Slashts6_for_migration_information));
            }
        }

        if (options.CheckJs.IsTrue() && !options.GetAllowJS())
        {
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1, "checkJs", "allowJs");
        }

        if (options.EmitDeclarationOnly.IsTrue())
        {
            if (!options.GetEmitDeclarations())
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1_or_option_2, "emitDeclarationOnly", "declaration", "composite");
            }
        }

        if (options.EmitDecoratorMetadata.IsTrue() && options.ExperimentalDecorators.IsFalseOrUnknown())
        {
            CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1, "emitDecoratorMetadata", "experimentalDecorators");
        }

        if (options.JsxFactory != "")
        {
            if (options.ReactNamespace != "")
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_with_option_1, "reactNamespace", "jsxFactory");
            }
            if (options.Jsx == JsxEmit.ReactJSX || options.Jsx == JsxEmit.ReactJSXDev)
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_when_option_jsx_is_1, "jsxFactory", options.Jsx.ToDisplayString());
            }
            if (global::Ts.Parser.Parser.ParseIsolatedEntityName(options.JsxFactory) is null)
            {
                CreateOptionValueDiagnostic("jsxFactory", Diag.Invalid_value_for_jsxFactory_0_is_not_a_valid_identifier_or_qualified_name, options.JsxFactory);
            }
        }
        else if (options.ReactNamespace != "" && !global::Ts.Scanner.Scanner.IsIdentifierText(options.ReactNamespace, LanguageVariant.Standard))
        {
            CreateOptionValueDiagnostic("reactNamespace", Diag.Invalid_value_for_reactNamespace_0_is_not_a_valid_identifier, options.ReactNamespace);
        }

        if (options.JsxFragmentFactory != "")
        {
            if (options.JsxFactory == "")
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_without_specifying_option_1, "jsxFragmentFactory", "jsxFactory");
            }
            if (options.Jsx == JsxEmit.ReactJSX || options.Jsx == JsxEmit.ReactJSXDev)
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_when_option_jsx_is_1, "jsxFragmentFactory", options.Jsx.ToDisplayString());
            }
            if (global::Ts.Parser.Parser.ParseIsolatedEntityName(options.JsxFragmentFactory) is null)
            {
                CreateOptionValueDiagnostic("jsxFragmentFactory", Diag.Invalid_value_for_jsxFragmentFactory_0_is_not_a_valid_identifier_or_qualified_name, options.JsxFragmentFactory);
            }
        }

        if (options.ReactNamespace != "")
        {
            if (options.Jsx == JsxEmit.ReactJSX || options.Jsx == JsxEmit.ReactJSXDev)
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_when_option_jsx_is_1, "reactNamespace", options.Jsx.ToDisplayString());
            }
        }

        if (options.JsxImportSource != "")
        {
            if (options.Jsx == JsxEmit.React)
            {
                CreateDiagnosticForOptionName(Diag.Option_0_cannot_be_specified_when_option_jsx_is_1, "jsxImportSource", options.Jsx.ToDisplayString());
            }
        }

        var moduleKind = options.GetEmitModuleKind();

        if (options.AllowImportingTsExtensions.IsTrue() && !(options.NoEmit.IsTrue() || options.EmitDeclarationOnly.IsTrue() || options.RewriteRelativeImportExtensions.IsTrue()))
        {
            CreateOptionValueDiagnostic("allowImportingTsExtensions", Diag.Option_allowImportingTsExtensions_can_only_be_used_when_one_of_noEmit_emitDeclarationOnly_or_rewriteRelativeImportExtensions_is_set);
        }

        var moduleResolution = options.GetModuleResolutionKind();
        if (options.ResolvePackageJsonExports.IsTrue() && !ModuleResolutionSupportsPackageJsonExportsAndImports(moduleResolution))
        {
            CreateDiagnosticForOptionName(Diag.Option_0_can_only_be_used_when_moduleResolution_is_set_to_node16_nodenext_or_bundler, "resolvePackageJsonExports", "");
        }
        if (options.ResolvePackageJsonImports.IsTrue() && !ModuleResolutionSupportsPackageJsonExportsAndImports(moduleResolution))
        {
            CreateDiagnosticForOptionName(Diag.Option_0_can_only_be_used_when_moduleResolution_is_set_to_node16_nodenext_or_bundler, "resolvePackageJsonImports", "");
        }
        if (options.CustomConditions is not null && !ModuleResolutionSupportsPackageJsonExportsAndImports(moduleResolution))
        {
            CreateDiagnosticForOptionName(Diag.Option_0_can_only_be_used_when_moduleResolution_is_set_to_node16_nodenext_or_bundler, "customConditions", "");
        }

        if (moduleResolution == ModuleResolutionKind.Bundler && !EmitModuleKindIsNonNodeESM(moduleKind) && moduleKind != ModuleKind.Preserve && moduleKind != ModuleKind.CommonJS)
        {
            CreateOptionValueDiagnostic("moduleResolution", Diag.Option_0_can_only_be_used_when_module_is_set_to_preserve_commonjs_or_es2015_or_later, "bundler");
        }

        if (ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext &&
            !(ModuleResolutionKind.Node16 <= moduleResolution && moduleResolution <= ModuleResolutionKind.NodeNext))
        {
            var moduleKindName = moduleKind.ToDisplayString();
            string moduleResolutionName;
            if (global::Ts.Core.Core.ModuleKindToModuleResolutionKind.TryGetValue(moduleKind, out var mr))
            {
                moduleResolutionName = mr.ToDisplayString();
            }
            else
            {
                moduleResolutionName = "Node16";
            }
            CreateOptionValueDiagnostic("moduleResolution", Diag.Option_moduleResolution_must_be_set_to_0_or_left_unspecified_when_option_module_is_set_to_1, moduleResolutionName, moduleKindName);
        }
        else if (ModuleResolutionKind.Node16 <= moduleResolution && moduleResolution <= ModuleResolutionKind.NodeNext &&
            !(ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext))
        {
            var moduleResolutionName = moduleResolution.ToDisplayString();
            CreateOptionValueDiagnostic("module", Diag.Option_module_must_be_set_to_0_when_option_moduleResolution_is_set_to_1, moduleResolutionName, moduleResolutionName);
        }

        // !!! The below needs filesByName, which is not equivalent to p.filesByPath.

        // If the emit is enabled make sure that every output file is unique and not overwriting any of the input files
        if (!options.NoEmit.IsTrue() && !options.SuppressOutputPathCheck.IsTrue())
        {
            var emitFilesSeen = new Set<string>();

            // Verify that all the emit files are unique and don't overwrite input files
            void VerifyEmitFilePath(string emitFileName)
            {
                if (emitFileName != "")
                {
                    var emitFilePath = ToPath(emitFileName);
                    // Report error if the output overwrites input file
                    if (processed.FilesByPath.ContainsKey(emitFilePath))
                    {
                        var diag = Diagnostic.NewCompilerDiagnostic(Diag.Cannot_write_file_0_because_it_would_overwrite_input_file, emitFileName);
                        if (ConfigFilePath() == "")
                        {
                            // The program is from either an inferred project or an external project
                            diag.AddMessageChain(Diagnostic.NewCompilerDiagnostic(Diag.Adding_a_tsconfig_json_file_will_help_organize_projects_that_contain_both_TypeScript_and_JavaScript_files_Learn_more_at_https_Colon_Slash_Slashaka_ms_Slashtsconfig));
                        }
                        BlockEmittingOfFile(emitFileName, diag);
                    }

                    string emitFileKey = !Host().FS().UseCaseSensitiveFileNames()
                        ? global::Ts.TsPath.TsPath.ToFileNameLowerCase(emitFilePath.Value)
                        : emitFilePath.Value;

                    // Report error if multiple files write into same file
                    if (emitFilesSeen.Has(emitFileKey))
                    {
                        // Already seen the same emit file - report error
                        BlockEmittingOfFile(emitFileName, Diagnostic.NewCompilerDiagnostic(Diag.Cannot_write_file_0_because_it_would_be_overwritten_by_multiple_input_files, emitFileName));
                    }
                    else
                    {
                        emitFilesSeen.Add(emitFileKey);
                    }
                }
            }

            global::Ts.OutputPaths.OutputPaths.ForEachEmittedFile(this, options, (emitFileNames, sourceFile) =>
            {
                VerifyEmitFilePath(emitFileNames.JsFilePath());
                VerifyEmitFilePath(emitFileNames.SourceMapFilePath());
                VerifyEmitFilePath(emitFileNames.DeclarationFilePath());
                VerifyEmitFilePath(emitFileNames.DeclarationMapPath());
                return false;
            }, GetSourceFilesToEmit(null, false), false);
            VerifyEmitFilePath(opts.Config.GetBuildInfoFileName());
        }
    }

    // program.go:1225-1228
    private void BlockEmittingOfFile(string emitFileName, Diagnostic diag)
    {
        hasEmitBlockingDiagnostics.Add(ToPath(emitFileName));
        programDiagnostics.Add(diag);
    }

    // program.go:1246-1284
    private void VerifyProjectReferences()
    {
        var buildInfoFileName = !Options().SuppressOutputPathCheck.IsTrue() ? opts.Config.GetBuildInfoFileName() : "";
        void CreateDiagnosticForReference(global::Ts.TsOptions.ParsedCommandLine config, int index, global::Ts.Diagnostics.Message message, params object?[] args)
        {
            var diag = global::Ts.TsOptions.TsConfigParsing.CreateDiagnosticAtReferenceSyntax(config, index, message, args)
                ?? Diagnostic.NewCompilerDiagnostic(message, args);
            programDiagnostics.Add(diag);
        }

        Mapper.RangeResolvedProjectReference((path, config, parent, index) =>
        {
            var reference = parent.ProjectReferences()![index];
            // !!! Deprecated in 5.0 and removed since 5.5
            // verifyRemovedProjectReference(ref, parent, index);
            if (config is null)
            {
                CreateDiagnosticForReference(parent, index, Diag.File_0_not_found, reference.Path);
                return true;
            }
            var refOptions = config.CompilerOptions();
            if (!refOptions!.Composite.IsTrue() || refOptions.NoEmit.IsTrue())
            {
                if ((parent.FileNames()?.Length ?? 0) > 0)
                {
                    if (!refOptions.Composite.IsTrue())
                    {
                        CreateDiagnosticForReference(parent, index, Diag.Referenced_project_0_must_have_setting_composite_Colon_true, reference.Path);
                    }
                    if (refOptions.NoEmit.IsTrue())
                    {
                        CreateDiagnosticForReference(parent, index, Diag.Referenced_project_0_may_not_disable_emit, reference.Path);
                    }
                }
            }
            if (buildInfoFileName != "" && buildInfoFileName == config.GetBuildInfoFileName())
            {
                CreateDiagnosticForReference(parent, index, Diag.Cannot_write_file_0_because_it_will_overwrite_tsbuildinfo_file_generated_by_referenced_project_1, buildInfoFileName, reference.Path);
                hasEmitBlockingDiagnostics.Add(ToPath(buildInfoFileName));
            }
            return true;
        });
    }

    // program.go:1271-1284
    private static bool HasZeroOrOneAsteriskCharacter(string str)
    {
        var seenAsterisk = false;
        foreach (var ch in str)
        {
            if (ch == '*')
            {
                if (!seenAsterisk)
                {
                    seenAsterisk = true;
                }
                else
                {
                    // have already seen asterisk
                    return false;
                }
            }
        }
        return true;
    }

    // program.go:1286-1289
    private static bool ModuleResolutionSupportsPackageJsonExportsAndImports(ModuleResolutionKind moduleResolution) =>
        (moduleResolution >= ModuleResolutionKind.Node16 && moduleResolution <= ModuleResolutionKind.NodeNext) ||
        moduleResolution == ModuleResolutionKind.Bundler;

    // program.go:1291-1293
    private static bool EmitModuleKindIsNonNodeESM(ModuleKind moduleKind) =>
        moduleKind >= ModuleKind.ES2015 && moduleKind <= ModuleKind.ESNext;

    // AOT-safe equivalent of Go's json.Marshal(string): a hand-written Utf8JsonWriter emitter (the
    // reflection JsonSerializer is banned per plan/07). Default writer options escape <, >, & like
    // Go's default HTML-escaping json encoder, matching bytes for the ASCII path suggestion.
    private static string JsonMarshalString(string value)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    // program.go:1633-1695 — Program.Emit lives in Program.Emit.cs (this wave).

    // program.go:1436-1440 — the diagnostics aggregation surface lands with the CLI type-check-and-report wave.
    // SortAndDeduplicateDiagnostics and the Get*Diagnostics methods are ported in Program.Diagnostics.cs.
}
