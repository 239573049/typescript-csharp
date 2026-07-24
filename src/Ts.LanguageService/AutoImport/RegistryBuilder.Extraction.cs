// Ported from tsgo internal/ls/autoimport/registry.go — the extraction pipeline
// (:1119-1826): bucket build helpers/result types, project-bucket extraction, node_modules
// discovery/extraction/install phases and granular updates, plus the extract.go:73-78
// newExportExtractor builder method.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.PackageJson;
using Ts.Project.Logging;

namespace Ts.Ls.AutoImport;

public static partial class AutoImportApi
{
    // registry.go:1119-1132 — hasNewNonNodeModulesFiles.
    internal static bool HasNewNonNodeModulesFiles(global::Ts.Compiler.Program program, RegistryBucket bucket)
    {
        if (bucket.state.newProgramStructure != NewProgramStructure.DifferentFileNames)
        {
            return false;
        }
        foreach (var file in program.SourceFiles())
        {
            if (file.FileName().Contains("/node_modules/", StringComparison.Ordinal) || IsIgnoredFile(program, file))
            {
                continue;
            }
            if (bucket.Paths == null || !bucket.Paths.ContainsKey(file.Path()))
            {
                return true;
            }
        }
        return false;
    }

    // registry.go:1134-1136 — isIgnoredFile.
    internal static bool IsIgnoredFile(global::Ts.Compiler.Program program, SourceFile file)
    {
        return program.IsSourceFileDefaultLibrary(file.Path()) || program.IsGlobalTypingsFile(file.FileName());
    }

    // registry.go:1138-1185 — hasSymlinkToNodeModules checks if a file's realpath has a symlink
    // that points to a node_modules directory. This is used to skip files in the project bucket
    // that would be duplicated by the node_modules bucket via their symlink.
    internal static bool HasSymlinkToNodeModules(global::Ts.TsPath.Path filePath, global::Ts.Symlinks.KnownSymlinks? symlinkCache)
    {
        if (symlinkCache == null)
        {
            return false;
        }

        // First check if the file itself has a symlink to node_modules
        var filesByRealpath = symlinkCache.FilesByRealpath();
        if (filesByRealpath != null)
        {
            if (filesByRealpath.TryLoad(filePath, out var symlinkPaths))
            {
                var found = false;
                symlinkPaths.Range(symlinkPath =>
                {
                    if (symlinkPath.Contains("/node_modules/", StringComparison.Ordinal))
                    {
                        found = true;
                        return false; // stop ranging
                    }
                    return true;
                });
                if (found)
                {
                    return true;
                }
            }
        }

        // Fall back to checking ancestor directories
        var directoriesByRealpath = symlinkCache.DirectoriesByRealpath();
        if (directoriesByRealpath == null)
        {
            return false;
        }
        var foundAncestor = false;
        global::Ts.TsPath.TsPath.ForEachAncestorDirectoryPath<object?>(filePath, dirPath =>
        {
            if (!directoriesByRealpath.TryLoad(dirPath.EnsureTrailingDirectorySeparator(), out var symlinkPaths))
            {
                return (null, false);
            }
            // Check if any of the symlinks point to a node_modules directory
            symlinkPaths.Range(symlinkPath =>
            {
                if (symlinkPath.Contains("/node_modules/", StringComparison.Ordinal))
                {
                    foundAncestor = true;
                    return false; // stop ranging
                }
                return true;
            });
            return (null, foundAncestor); // stop if we found a match
        });
        return foundAncestor;
    }

    // registry.go:1549-1598 — installExtractions aggregates pre-extracted per-package results
    // into a single packageExtractionResult for one bucket. This is the install phase of the
    // three-phase pipeline.
    internal static PackageExtractionResult InstallExtractions(
        List<DiscoveredPackage> discovered,
        Dictionary<string, PerPackageExtractionResult?> extractionCache)
    {
        var result = new PackageExtractionResult
        {
            exports = new Dictionary<global::Ts.TsPath.Path, List<Export>>(),
            packageFiles = new Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>(StringComparer.Ordinal),
            ambientModuleNames = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            workspacePackages = new Set<string>(),
            possibleFailedAmbientModuleLookupSources = new SyncMap<global::Ts.TsPath.Path, FailedAmbientModuleLookupSource>(),
            possibleFailedAmbientModuleLookupTargets = new SyncSet<string>(),
        };

        foreach (var pkg in discovered)
        {
            extractionCache.TryGetValue(pkg.realpath, out var extraction);
            if (extraction == null)
            {
                extractionCache.TryGetValue(pkg.typesRealpath, out extraction);
            }
            if (extraction == null)
            {
                continue;
            }
            // map-iter: 序无关 (maps.Copy 到字典)
            foreach (var (k, v) in extraction.exports)
            {
                result.exports[k] = v;
            }
            if (!result.packageFiles.TryGetValue(pkg.packageName, out var pkgFiles) || pkgFiles == null)
            {
                pkgFiles = new Dictionary<global::Ts.TsPath.Path, string>(extraction.packageFiles.Count);
                result.packageFiles[pkg.packageName] = pkgFiles;
            }
            // map-iter: 序无关 (maps.Copy 到字典)
            foreach (var (k, v) in extraction.packageFiles)
            {
                pkgFiles[k] = v;
            }
            // map-iter: 序无关 (追加合并到字典值列表; 列表内序来自单一提取结果)
            foreach (var (name, fileNames) in extraction.ambientModules)
            {
                if (!result.ambientModuleNames.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    result.ambientModuleNames[name] = list;
                }
                list.AddRange(fileNames);
            }
            if (extraction.entrypoints != null)
            {
                result.entrypoints.Add(extraction.entrypoints);
            }
            // map-iter: 序无关 (LoadOrStore 幂等)
            foreach (var (path, source) in extraction.failedAmbientModuleLookupSources)
            {
                result.possibleFailedAmbientModuleLookupSources.LoadOrStore(path, source);
            }
            foreach (var target in extraction.failedAmbientModuleLookupTargets.Keys())
            {
                result.possibleFailedAmbientModuleLookupTargets.Add(target);
            }
            if (extraction.isSymlinked && pkg.isLocal)
            {
                result.workspacePackages.Add(pkg.packageName);
            }
            result.stats.exports += extraction.statsExports;
            result.stats.usedChecker += extraction.statsUsedChecker;
            result.skippedEntrypointsCount += extraction.skippedEntrypoints;
        }

        return result;
    }
}

// registry.go:1187-1191 — failedAmbientModuleLookupSource.
internal sealed class FailedAmbientModuleLookupSource
{
    internal readonly object mu = new();
    internal string fileName = "";
    internal string packageName = "";
}

// registry.go:1193-1208 — bucketBuildResult.
internal sealed class BucketBuildResult
{
    internal global::Ts.Project.Dirty.MapEntry<global::Ts.TsPath.Path, RegistryBucket> entry = null!;
    internal Exception? err;

    internal RegistryBucket? bucket;
    // entrypoints are the resolved entrypoints from this bucket's packages,
    // to be merged into the registry-level entrypoints map.
    internal Dictionary<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>>? entrypoints;
    // removedEntrypointPaths lists paths whose entrypoints should be removed from
    // the registry-level map before merging new entrypoints. Used for granular updates.
    internal List<global::Ts.TsPath.Path>? removedEntrypointPaths;
    // File path to filename and package name
    internal SyncMap<global::Ts.TsPath.Path, FailedAmbientModuleLookupSource>? possibleFailedAmbientModuleLookupSources;
    // Likely ambient module name
    internal SyncSet<string>? possibleFailedAmbientModuleLookupTargets;
}

// registry.go:1327-1340 — discoveredPackage represents a package found during the discovery
// phase. It holds the resolved package.json and realpath for deduplication.
// When both a real package and a corresponding @types package exist (e.g., react +
// @types/react), both are stored so extraction can fall back to the @types package if the
// real package has no TypeScript entrypoints.
internal sealed class DiscoveredPackage
{
    internal string packageName = "";
    internal global::Ts.PackageJson.InfoCacheEntry? packageJson;
    internal string realpath = "";
    internal global::Ts.PackageJson.InfoCacheEntry? typesPackageJson;
    internal string typesRealpath = "";
    internal global::Ts.TsPath.Path dirPath; // bucket directory path (used as extraction context)
    internal bool isLocal;                   // true if realpath is within the workspace root
}

// registry.go:1342-1356 — perPackageExtractionResult holds the extraction output for one
// physical package. Produced once per unique realpath during the extraction phase, then
// installed into every bucket that needs it during the bucket-building phase.
internal sealed class PerPackageExtractionResult
{
    internal Dictionary<global::Ts.TsPath.Path, string> packageFiles = null!;
    internal List<global::Ts.Module.ResolvedEntrypoint> entrypoints = null!;
    internal Dictionary<global::Ts.TsPath.Path, List<Export>> exports = null!;
    internal Dictionary<string, List<string>> ambientModules = null!;
    internal int statsExports;
    internal int statsUsedChecker;
    internal int skippedEntrypoints;
    internal bool isSymlinked;
    internal Dictionary<global::Ts.TsPath.Path, FailedAmbientModuleLookupSource> failedAmbientModuleLookupSources = null!;
    internal Set<string> failedAmbientModuleLookupTargets = null!;
}

// registry.go:1358-1369 — packageExtractionResult holds the results of extracting exports
// from a set of packages.
internal sealed class PackageExtractionResult
{
    internal Dictionary<global::Ts.TsPath.Path, List<Export>> exports = null!;
    internal Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?> packageFiles = null!;
    internal Dictionary<string, List<string>> ambientModuleNames = null!;
    internal List<List<global::Ts.Module.ResolvedEntrypoint>> entrypoints = new();
    internal Set<string> workspacePackages = null!;
    internal SyncMap<global::Ts.TsPath.Path, FailedAmbientModuleLookupSource> possibleFailedAmbientModuleLookupSources = null!;
    internal SyncSet<string> possibleFailedAmbientModuleLookupTargets = null!;
    internal ExtractorStats stats = new();
    internal int skippedEntrypointsCount;
}

internal sealed partial class RegistryBuilder
{
    // extract.go:73-78 — newExportExtractor.
    internal ExportExtractor NewExportExtractor(string packageName, global::Ts.Checker.Checker checker, global::Ts.Module.Resolver moduleResolver, Func<string, string>? realpath)
    {
        return ExportExtractor.NewExportExtractor(packageName, checker, moduleResolver, baseRegistry.toPath, realpath);
    }

    // registry.go:1210-1295 — buildProjectBucket.
    internal void BuildProjectBucket(
        CancellationToken ctx,
        BucketBuildResult result,
        global::Ts.TsPath.Path projectPath,
        Set<string>? resolvedPackageNames,
        LogTree? logger)
    {
        if (ctx.IsCancellationRequested)
        {
            result.err = new OperationCanceledException(ctx);
            return;
        }

        var start = DateTime.Now;
        var mu = new object();
        var fileExcludePatterns = userPreferences.ParsedAutoImportFileExcludePatterns(host.FS().UseCaseSensitiveFileNames());
        result.bucket = new RegistryBucket();
        var moduleResolver = new global::Ts.Module.Resolver(host, global::Ts.Core.Core.EmptyCompilerOptions, "", "", resolverOptions);
        var program = host.GetProgramForProject(projectPath)!;
        var symlinkCache = program.GetSymlinkCache();
        var (getChecker, closePool, checkerCount) = Util.CreateCheckerPool(program);
        try
        {
            var exports = new Dictionary<global::Ts.TsPath.Path, List<Export>>();
            var wg = global::Ts.Core.WorkGroup.Create(false);
            var skippedFileCount = 0;
            var combinedStats = new ExtractorStats();

            foreach (var file in program.SourceFiles())
            {
                if (AutoImportApi.IsIgnoredFile(program, file))
                {
                    continue;
                }
                if (fileExcludePatterns != null && fileExcludePatterns.MatchString(file.FileName()))
                {
                    skippedFileCount++;
                    continue;
                }
                // Skip all node_modules files - they are always handled by node_modules buckets.
                // This simplifies the logic and ensures exports are indexed consistently.
                if (file.FileName().Contains("/node_modules/", StringComparison.Ordinal))
                {
                    continue;
                }
                // Skip files that are realpaths of symlinks in node_modules.
                // These files will be indexed via their symlinked path in node_modules buckets.
                if (AutoImportApi.HasSymlinkToNodeModules(file.Path(), symlinkCache))
                {
                    continue;
                }
                wg.Queue(() =>
                {
                    if (!ctx.IsCancellationRequested)
                    {
                        var (checker, done) = getChecker();
                        try
                        {
                            var extractor = NewExportExtractor("", checker, moduleResolver, null);
                            var fileExports = extractor.ExtractFromFile(file);
                            lock (mu)
                            {
                                exports[file.Path()] = fileExports;
                            }
                            var stats = extractor.Stats();
                            Interlocked.Add(ref combinedStats.exports, stats.exports);
                            Interlocked.Add(ref combinedStats.usedChecker, stats.usedChecker);
                        }
                        finally
                        {
                            done();
                        }
                    }
                });
            }

            wg.RunAndWait();

            var indexStart = DateTime.Now;
            var idx = new Index<Export>();
            var paths = new Dictionary<global::Ts.TsPath.Path, string>(exports.Count);
            // map-iter: 序无关 (Index.insertAsWords 与词序无关: 词桶按名索引)
            foreach (var (path, fileExports) in exports)
            {
                paths[path] = ""; // Empty string for project buckets
                foreach (var exp in fileExports)
                {
                    idx.InsertAsWords(exp);
                }
            }

            result.bucket.Paths = paths;
            result.bucket.Index = idx;
            result.bucket.ResolvedPackageNames = resolvedPackageNames;
            result.bucket.state.buildPreferences = BucketBuildPreferences.FromUserPreferences(userPreferences);

            if (logger != null)
            {
                logger.Logf("Extracted exports: {0} ({1} exports, {2} used checker, {3} created checkers)", indexStart - start, combinedStats.exports, combinedStats.usedChecker, checkerCount());
                if (skippedFileCount > 0)
                {
                    logger.Logf("Skipped {0} files due to exclude patterns", skippedFileCount);
                }
                logger.Logf("Built index: {0}", DateTime.Now - indexStart);
                logger.Logf("Bucket total: {0}", DateTime.Now - start);
            }
        }
        finally
        {
            closePool();
        }
    }

    // registry.go:1297-1325 — computeDependenciesForNodeModulesDirectory.
    internal Set<string>? ComputeDependenciesForNodeModulesDirectory(RegistryChange change, Dictionary<global::Ts.TsPath.Path, Set<string>?> allResolvedPackageNames, string dirName, global::Ts.TsPath.Path dirPath)
    {
        // If any open files are in scope of this directory but not in scope of any package.json,
        // we need to add all packages in this node_modules directory.
        // map-iter: 序无关 (存在性判定)
        foreach (var path in (change.OpenFiles ?? new Dictionary<global::Ts.TsPath.Path, string>()).Keys)
        {
            if (dirPath.ContainsPath(path) && GetNearestAncestorDirectoryWithPackageJson(path) == null)
            {
                return null;
            }
        }

        // Get all package.jsons that have this node_modules directory in their spine
        var dependencies = new Set<string>();
        directories.Range(entry =>
        {
            if (entry.Value().packageJson.Exists() && dirPath.ContainsPath(entry.Key()))
            {
                var deps = new HashSet<string>(StringComparer.Ordinal);
                Util.AddPackageJsonDependencies(entry.Value().packageJson!.Contents!, deps);
                foreach (var dep in deps)
                {
                    dependencies.Add(dep);
                }
            }
            return true;
        });

        // Add packages that are directly imported by programs but not listed in package.json.
        // This ensures node_modules files are always in node_modules buckets.
        // Include packages from all projects that have this node_modules directory in their spine.
        // map-iter: 序无关 (集合并集)
        foreach (var resolvedPackageNames in allResolvedPackageNames.Values)
        {
            foreach (var name in resolvedPackageNames.Keys())
            {
                dependencies.Add(name);
            }
        }

        return dependencies;
    }

    // registry.go:1371-1415 — discoverBucketPackages resolves the package.json and realpath for
    // each package name in a node_modules directory. This is the discovery phase of the
    // three-phase extraction pipeline.
    internal List<DiscoveredPackage> DiscoverBucketPackages(
        Set<string>? packageNames,
        string dirName,
        global::Ts.TsPath.Path dirPath)
    {
        var result = new List<DiscoveredPackage>(packageNames.Len());
        // map-iter: 序无关 (逐包独立发现; 下游按 realpath 去重与集合聚合)
        foreach (var packageName in packageNames.Keys())
        {
            var typesPackageName = global::Ts.Module.Module.GetTypesPackageName(packageName);
            var packageJson = host.GetPackageJson(global::Ts.TsPath.TsPath.CombinePaths(dirName, "node_modules", packageName, "package.json"));
            global::Ts.PackageJson.InfoCacheEntry? typesPackageJson = null;
            if (packageName != typesPackageName)
            {
                var typesJson = host.GetPackageJson(global::Ts.TsPath.TsPath.CombinePaths(dirName, "node_modules", typesPackageName, "package.json"));
                if (typesJson != null && typesJson.DirectoryExists)
                {
                    typesPackageJson = typesJson;
                }
            }
            var realpath = "";
            if (packageJson != null && packageJson.DirectoryExists)
            {
                realpath = host.FS().Realpath(packageJson.PackageDirectory);
            }
            var typesRealpath = "";
            if (typesPackageJson != null)
            {
                typesRealpath = host.FS().Realpath(typesPackageJson.PackageDirectory);
            }
            var isLocal = realpath.Length != 0 &&
                !realpath.Contains("/node_modules/", StringComparison.Ordinal) &&
                global::Ts.TsPath.TsPath.ContainsPath(
                    host.GetCurrentDirectory(),
                    realpath,
                    new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = host.FS().UseCaseSensitiveFileNames() });
            result.Add(new DiscoveredPackage
            {
                packageName = packageName,
                packageJson = packageJson,
                realpath = realpath,
                typesPackageJson = typesPackageJson,
                typesRealpath = typesRealpath,
                dirPath = dirPath,
                isLocal = isLocal,
            });
        }
        return result;
    }

    // registry.go:1417-1547 — extractPackage extracts exports from a single package.json.
    // This runs once per unique realpath during the extraction phase.
    // Returns null if the package has no extractable entrypoints.
    internal PerPackageExtractionResult? ExtractPackage(
        CancellationToken ctx,
        global::Ts.PackageJson.InfoCacheEntry? packageJson,
        string packageName,
        Dictionary<global::Ts.TsPath.Path, string> projectReferenceOutputs,
        global::Ts.Vfs.VfsMatch.SpecMatcher? fileExcludePatterns,
        bool enableDirectorySearch)
    {
        if (packageJson == null || !packageJson.DirectoryExists)
        {
            return null;
        }
        var (toRealpath, toSymlink) = Util.GetPackageRealpathFuncs(host.FS(), packageJson.PackageDirectory);
        var resolver = Util.GetModuleResolver(host, toRealpath, resolverOptions);
        var packageEntrypoints = resolver.GetEntrypointsFromPackageJsonInfo(packageJson, packageName, enableDirectorySearch);
        if (packageEntrypoints == null)
        {
            return null;
        }

        var skippedEntrypoints = 0;
        if (fileExcludePatterns != null)
        {
            var count = packageEntrypoints.Count;
            packageEntrypoints.RemoveAll(entrypoint => fileExcludePatterns.MatchString(entrypoint.ResolvedFileName));
            skippedEntrypoints = count - packageEntrypoints.Count;
        }
        if (packageEntrypoints.Count == 0)
        {
            return null;
        }

        var result = new PerPackageExtractionResult
        {
            packageFiles = new Dictionary<global::Ts.TsPath.Path, string>(),
            entrypoints = packageEntrypoints,
            exports = new Dictionary<global::Ts.TsPath.Path, List<Export>>(),
            ambientModules = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            skippedEntrypoints = skippedEntrypoints,
            failedAmbientModuleLookupSources = new Dictionary<global::Ts.TsPath.Path, FailedAmbientModuleLookupSource>(),
            failedAmbientModuleLookupTargets = new Set<string>(),
        };

        // Resolve entrypoint source files and build the alias resolver.
        var seenFiles = new Set<global::Ts.TsPath.Path>(packageEntrypoints.Count); // NewSetWithSizeHint
        var rootFiles = new SourceFile?[packageEntrypoints.Count];
        var symlinks = new Dictionary<global::Ts.TsPath.Path, PathAndFileName>();
        var wg = global::Ts.Core.WorkGroup.Create(false);
        for (var i = 0; i < packageEntrypoints.Count; i++)
        {
            var entrypoint = packageEntrypoints[i];
            var fileName = entrypoint.SymlinkOrRealpath();
            var realpathFileName = entrypoint.ResolvedFileName;
            var realpathPath = baseRegistry.toPath(realpathFileName);

            if (projectReferenceOutputs.TryGetValue(realpathPath, out var inputFileName))
            {
                fileName = toSymlink(inputFileName);
                realpathFileName = inputFileName;
                realpathPath = baseRegistry.toPath(realpathFileName);
            }

            if (!seenFiles.AddIfAbsent(realpathPath))
            {
                continue;
            }
            if (fileName != realpathFileName)
            {
                var symlinkPath = baseRegistry.toPath(fileName);
                symlinks[realpathPath] = new PathAndFileName(symlinkPath, fileName);
                result.isSymlinked = true;
            }
            var slot = i;
            var realpathFileNameCopy = realpathFileName;
            var realpathPathCopy = realpathPath;
            wg.Queue(() =>
            {
                var file = host.GetSourceFile(realpathFileNameCopy, realpathPathCopy);
                if (file != null)
                {
                    global::Ts.Binder.Binder.BindSourceFile(file);
                }
                rootFiles[slot] = file;
            });
        }
        wg.RunAndWait();
        var boundRootFiles = new List<SourceFile>(rootFiles.Length);
        foreach (var f in rootFiles)
        {
            if (f != null)
            {
                boundRootFiles.Add(f);
            }
        }

        var aliasResolver = AliasResolver.NewAliasResolver(boundRootFiles.ToArray(), symlinks, host, resolver, baseRegistry.toPath, (source, moduleName) =>
        {
            result.failedAmbientModuleLookupTargets.Add(moduleName);
            if (!result.failedAmbientModuleLookupSources.ContainsKey(source.Path()))
            {
                result.failedAmbientModuleLookupSources[source.Path()] = new FailedAmbientModuleLookupSource
                {
                    fileName = source.FileName(),
                };
            }
        });

        var ch = global::Ts.Checker.Checker.NewChecker(aliasResolver, null).Checker;
        var extractor = NewExportExtractor(packageName, ch, resolver, toRealpath);

        var nonModuleFiles = new Set<global::Ts.TsPath.Path>();
        foreach (var entrypoint in aliasResolver.rootFiles)
        {
            if (ctx.IsCancellationRequested)
            {
                return null;
            }
            var fileExports = extractor.ExtractFromFile(entrypoint);
            foreach (var name in entrypoint.AmbientModuleNames ?? [])
            {
                if (!result.ambientModules.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    result.ambientModules[name] = list;
                }
                list.Add(entrypoint.FileName());
            }
            result.packageFiles[entrypoint.Path()] = entrypoint.FileName();
            var hasSymlink = aliasResolver.symlinks != null && aliasResolver.symlinks.TryGetValue(entrypoint.Path(), out var symlink);
            if (hasSymlink)
            {
                aliasResolver.symlinks!.TryGetValue(entrypoint.Path(), out var sym);
                result.packageFiles[sym.Path] = sym.FileName;
            }

            var hasExports = fileExports.Count > 0 && entrypoint.ExternalModuleIndicator != null;
            if (!result.failedAmbientModuleLookupSources.TryGetValue(entrypoint.Path(), out var source))
            {
                result.exports[entrypoint.Path()] = fileExports;
            }
            else
            {
                source.packageName = packageName;
                hasExports = entrypoint.ExternalModuleIndicator != null;
            }

            if (!hasExports)
            {
                nonModuleFiles.Add(entrypoint.Path());
                if (hasSymlink)
                {
                    aliasResolver.symlinks!.TryGetValue(entrypoint.Path(), out var sym);
                    nonModuleFiles.Add(sym.Path);
                }
            }
        }

        // Discard entrypoints for non-module files and empty modules.
        result.entrypoints.RemoveAll(ep => nonModuleFiles.Has(baseRegistry.toPath(ep.ResolvedFileName)));

        var stats = extractor.Stats();
        result.statsExports = stats.exports;
        result.statsUsedChecker = stats.usedChecker;
        return result;
    }

    // registry.go:1600-1685 — buildNodeModulesBucket.
    internal void BuildNodeModulesBucket(
        CancellationToken ctx,
        BucketBuildResult result,
        Set<string>? dependencies,
        global::Ts.TsPath.Path dirPath,
        List<DiscoveredPackage> discovered,
        Set<string>? directoryPackageNames,
        Dictionary<string, PerPackageExtractionResult?> extractionCache,
        Set<string>? recursiveSearchPackages,
        LogTree? logger)
    {
        if (ctx.IsCancellationRequested)
        {
            result.err = new OperationCanceledException(ctx);
            return;
        }

        var extraction = AutoImportApi.InstallExtractions(discovered, extractionCache);

        var indexStart = DateTime.Now;
        // Build PackageFiles with all directory package names; indexed packages have
        // non-nil maps, unindexed packages have nil maps.
        var allPackageFiles = new Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>(directoryPackageNames.Len(), StringComparer.Ordinal);
        // map-iter: 序无关 (写入目标为字典)
        foreach (var pkgName in directoryPackageNames.Keys())
        {
            extraction.packageFiles.TryGetValue(pkgName, out var files);
            allPackageFiles[pkgName] = files;
        }

        // Build Paths as reverse mapping from path to package name.
        // Only include paths for local workspace packages (eligible for granular updates).
        var paths = new Dictionary<global::Ts.TsPath.Path, string>();
        // map-iter: 序无关 (workspacePackages 键集互斥, 反向映射与序无关)
        foreach (var pkgName in extraction.workspacePackages.Keys())
        {
            if (extraction.packageFiles.TryGetValue(pkgName, out var files) && files != null)
            {
                foreach (var path in files.Keys)
                {
                    paths[path] = pkgName;
                }
            }
        }

        result.bucket = new RegistryBucket
        {
            Index = new Index<Export>(),
            DependencyNames = dependencies,
            PackageFiles = allPackageFiles,
            AmbientModuleNames = extraction.ambientModuleNames,
            Paths = paths,
            state = new BucketState
            {
                buildPreferences = BucketBuildPreferences.FromUserPreferences(userPreferences),
                recursiveSearchPackages = recursiveSearchPackages.Clone(),
            },
        };
        result.entrypoints = new Dictionary<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>>(extraction.exports.Count);
        result.possibleFailedAmbientModuleLookupSources = extraction.possibleFailedAmbientModuleLookupSources;
        result.possibleFailedAmbientModuleLookupTargets = extraction.possibleFailedAmbientModuleLookupTargets;
        // map-iter: 序无关 (Index 词桶按名索引)
        foreach (var fileExports in extraction.exports.Values)
        {
            foreach (var exp in fileExports)
            {
                result.bucket.Index.InsertAsWords(exp);
            }
        }
        foreach (var entrypointSet in extraction.entrypoints)
        {
            foreach (var entrypoint in entrypointSet)
            {
                var path = baseRegistry.toPath(entrypoint.ResolvedFileName);
                if (!result.entrypoints.TryGetValue(path, out var list))
                {
                    list = new List<global::Ts.Module.ResolvedEntrypoint>();
                    result.entrypoints[path] = list;
                }
                list.Add(entrypoint);
            }
        }

        // Compute old entrypoint paths to remove from the registry-level map.
        // For a full rebuild, all entrypoints belonging to the old bucket's packages must be removed.
        var (oldEntry, ok) = nodeModules.Get(dirPath);
        if (ok)
        {
            var oldBucket = oldEntry!.Value();
            // map-iter: 序无关 (removedEntrypointPaths 仅作删除键集使用)
            foreach (var files in (oldBucket.PackageFiles ?? new Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>()).Values)
            {
                foreach (var path in (files ?? new Dictionary<global::Ts.TsPath.Path, string>()).Keys)
                {
                    if (baseRegistry.entrypoints != null && baseRegistry.entrypoints.ContainsKey(path))
                    {
                        result.removedEntrypointPaths ??= new List<global::Ts.TsPath.Path>();
                        result.removedEntrypointPaths.Add(path);
                    }
                }
            }
        }

        if (logger != null)
        {
            logger.Logf("Installed {0} exports ({1} used checker)", extraction.stats.exports, extraction.stats.usedChecker);
            if (extraction.skippedEntrypointsCount > 0)
            {
                logger.Logf("Skipped {0} entrypoints due to exclude patterns", extraction.skippedEntrypointsCount);
            }
            logger.Logf("Built index: {0}", DateTime.Now - indexStart);
        }

        result.err = ctx.IsCancellationRequested ? new OperationCanceledException(ctx) : null;
    }

    // registry.go:1687-1806 — updateNodeModulesBucket performs a granular update of the
    // node_modules bucket, re-extracting only the dirty packages and merging with the
    // existing bucket.
    internal void UpdateNodeModulesBucket(
        CancellationToken ctx,
        BucketBuildResult result,
        RegistryBucket existingBucket,
        Set<string>? dirtyPackages,
        List<DiscoveredPackage> discovered,
        Dictionary<string, PerPackageExtractionResult?> extractionCache,
        Set<string>? recursiveSearchPackages,
        LogTree? logger)
    {
        if (ctx.IsCancellationRequested)
        {
            result.err = new OperationCanceledException(ctx);
            return;
        }

        var start = DateTime.Now;
        var extraction = AutoImportApi.InstallExtractions(discovered, extractionCache);

        var indexStart = DateTime.Now;

        // Clone the existing index, excluding exports from dirty packages
        var newIndex = existingBucket.Index!.Clone(exp => !dirtyPackages.Has(exp.PackageName));

        // Clone PackageFiles, removing dirty packages
        var newPackageFiles = existingBucket.PackageFiles == null
            ? new Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>(StringComparer.Ordinal)
            : new Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>(existingBucket.PackageFiles, StringComparer.Ordinal);
        foreach (var pkgName in dirtyPackages.Keys())
        {
            newPackageFiles.Remove(pkgName);
        }
        // Add newly extracted package files
        // map-iter: 序无关 (maps.Copy 到字典)
        foreach (var (k, v) in extraction.packageFiles)
        {
            newPackageFiles[k] = v;
        }

        // Clone Paths, removing dirty package paths
        var newPaths = new Dictionary<global::Ts.TsPath.Path, string>((existingBucket.Paths ?? new Dictionary<global::Ts.TsPath.Path, string>()).Count);
        // map-iter: 序无关 (过滤复制到字典)
        foreach (var (path, pkgName) in existingBucket.Paths ?? new Dictionary<global::Ts.TsPath.Path, string>())
        {
            if (dirtyPackages.Has(pkgName))
            {
                continue;
            }
            newPaths[path] = pkgName;
        }
        // Add paths for newly extracted workspace packages
        // map-iter: 序无关 (反向映射)
        foreach (var pkgName in extraction.workspacePackages.Keys())
        {
            if (extraction.packageFiles.TryGetValue(pkgName, out var files) && files != null)
            {
                foreach (var path in files.Keys)
                {
                    newPaths[path] = pkgName;
                }
            }
        }

        // Clone AmbientModuleNames, removing dirty package entries
        var newAmbientModuleNames = new Dictionary<string, List<string>>((existingBucket.AmbientModuleNames ?? new Dictionary<string, List<string>>()).Count, StringComparer.Ordinal);
        // map-iter: 序无关 (逐键过滤; 值列表内序保留)
        foreach (var (moduleName, fileNames) in existingBucket.AmbientModuleNames ?? new Dictionary<string, List<string>>())
        {
            // Filter out files from dirty packages
            List<string>? filtered = null;
            foreach (var fileName in fileNames)
            {
                var path = baseRegistry.toPath(fileName);
                if (existingBucket.Paths != null && existingBucket.Paths.TryGetValue(path, out var pkgName) && dirtyPackages.Has(pkgName))
                {
                    continue;
                }
                filtered ??= new List<string>();
                filtered.Add(fileName);
            }
            if (filtered != null && filtered.Count > 0)
            {
                newAmbientModuleNames[moduleName] = filtered;
            }
        }
        // Add newly extracted ambient module names
        // map-iter: 序无关 (追加合并)
        foreach (var (moduleName, fileNames) in extraction.ambientModuleNames)
        {
            if (!newAmbientModuleNames.TryGetValue(moduleName, out var list))
            {
                list = new List<string>();
                newAmbientModuleNames[moduleName] = list;
            }
            list.AddRange(fileNames);
        }

        // Collect entrypoint paths that need to be removed from the registry-level map
        // (paths belonging to dirty packages)
        List<global::Ts.TsPath.Path>? removedEntrypointPaths = null;
        // map-iter: 序无关 (删除键集)
        foreach (var path in (baseRegistry.entrypoints ?? new Dictionary<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>>()).Keys)
        {
            if (existingBucket.Paths != null && existingBucket.Paths.TryGetValue(path, out var pkgName) && dirtyPackages.Has(pkgName))
            {
                removedEntrypointPaths ??= new List<global::Ts.TsPath.Path>();
                removedEntrypointPaths.Add(path);
            }
        }
        // Build new entrypoints from extraction
        var newEntrypoints = new Dictionary<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>>();
        foreach (var entrypointSet in extraction.entrypoints)
        {
            foreach (var entrypoint in entrypointSet)
            {
                var path = baseRegistry.toPath(entrypoint.ResolvedFileName);
                if (!newEntrypoints.TryGetValue(path, out var list))
                {
                    list = new List<global::Ts.Module.ResolvedEntrypoint>();
                    newEntrypoints[path] = list;
                }
                list.Add(entrypoint);
            }
        }

        // Insert newly extracted exports into the index
        // map-iter: 序无关 (Index 词桶按名索引)
        foreach (var fileExports in extraction.exports.Values)
        {
            foreach (var exp in fileExports)
            {
                newIndex.InsertAsWords(exp);
            }
        }

        result.bucket = new RegistryBucket
        {
            Index = newIndex,
            DependencyNames = existingBucket.DependencyNames,
            PackageFiles = newPackageFiles,
            AmbientModuleNames = newAmbientModuleNames,
            Paths = newPaths,
            state = new BucketState
            {
                buildPreferences = BucketBuildPreferences.FromUserPreferences(userPreferences),
                recursiveSearchPackages = recursiveSearchPackages.Clone(),
            },
        };
        result.entrypoints = newEntrypoints;
        result.removedEntrypointPaths = removedEntrypointPaths;
        result.possibleFailedAmbientModuleLookupSources = extraction.possibleFailedAmbientModuleLookupSources;
        result.possibleFailedAmbientModuleLookupTargets = extraction.possibleFailedAmbientModuleLookupTargets;

        if (logger != null)
        {
            logger.Logf("Granular update of {0} packages: {1} ({2} exports)", dirtyPackages.Len(), indexStart - start, extraction.stats.exports);
            logger.Logf("Built index: {0}", DateTime.Now - indexStart);
        }

        result.err = ctx.IsCancellationRequested ? new OperationCanceledException(ctx) : null;
    }

    // registry.go:1808-1815 — getNearestAncestorDirectoryWithPackageJson.
    internal Directory? GetNearestAncestorDirectoryWithPackageJson(global::Ts.TsPath.Path filePath)
    {
        return global::Ts.TsPath.TsPath.ForEachAncestorDirectoryPath<Directory?>(filePath.GetDirectoryPath(), dirPath =>
        {
            var (dirEntry, ok) = directories.Get(dirPath);
            if (ok && dirEntry!.Value().packageJson.Exists())
            {
                return (dirEntry.Value(), true);
            }
            return (null, false);
        }).Result;
    }

    // registry.go:1817-1826 — resolveAmbientModuleName.
    internal List<string>? ResolveAmbientModuleName(string moduleName, global::Ts.TsPath.Path fromPath)
    {
        return global::Ts.TsPath.TsPath.ForEachAncestorDirectoryPath<List<string>?>(fromPath, dirPath =>
        {
            var (bucket, ok) = nodeModules.Get(dirPath);
            if (ok)
            {
                if (bucket!.Value().AmbientModuleNames != null && bucket.Value().AmbientModuleNames!.TryGetValue(moduleName, out var fileNames))
                {
                    return (fileNames, true);
                }
            }
            return (null, false);
        }).Result;
    }
}
