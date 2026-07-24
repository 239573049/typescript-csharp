// Ported from tsgo internal/ls/autoimport/registry.go — Registry.Clone (:388-410), the
// registryBuilder skeleton (:505-546) and its existence/dirty passes
// (updateBucketAndDirectoryExistence :548-696, markBucketsDirty :698-780).
// PORT-NOTE: updateIndexes (:782-1117) and the extraction pipeline (:1210-1826) land with the
// next registry batch; until then Clone panics when change.RequestedFile is set.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.Project.Dirty;
using Ts.Project.Logging;

namespace Ts.Ls.AutoImport;

public sealed partial class Registry
{
    // registry.go:388-410 — Clone.
    public (Registry Registry, Exception? Error) Clone(CancellationToken ctx, RegistryChange change, RegistryCloneHost host, LogTree? logger)
    {
        var start = DateTime.Now;
        if (logger != null)
        {
            logger = logger.Fork("Building autoimport registry");
        }
        var builder = new RegistryBuilder(this, host);
        if (change.UserPreferences != null)
        {
            builder.userPreferences = change.UserPreferences;
            if (!CoreUtil.UnorderedEqual(builder.userPreferences.AutoImportSpecifierExcludeRegexes ?? [], userPreferences.AutoImportSpecifierExcludeRegexes ?? []))
            {
                builder.specifierCache.Clear();
            }
        }
        builder.UpdateBucketAndDirectoryExistence(change, logger);
        builder.MarkBucketsDirty(change, logger);
        if (change.RequestedFile.Value.Length != 0)
        {
            builder.UpdateIndexes(ctx, change, logger);
        }
        if (logger != null)
        {
            logger.Logf("Built autoimport registry in {0}", DateTime.Now - start);
        }
        var registry = builder.Build();
        return (registry, null);
    }
}

// registry.go:505-518 — registryBuilder.
internal sealed partial class RegistryBuilder
{
    internal RegistryCloneHost host;
    internal Registry baseRegistry; // Go: base

    internal UserPreferences userPreferences;
    internal Map<global::Ts.TsPath.Path, Directory> directories;
    internal Map<global::Ts.TsPath.Path, RegistryBucket> nodeModules;
    internal Map<global::Ts.TsPath.Path, RegistryBucket> projects;
    internal MapBuilder<global::Ts.TsPath.Path, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>> specifierCache;
    internal global::Ts.Module.ResolverOptions resolverOptions = new();

    internal int uniquePackageCount;
    internal MapBuilder<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>, List<global::Ts.Module.ResolvedEntrypoint>> entrypoints;

    // registry.go:520-533 — newRegistryBuilder.
    internal RegistryBuilder(Registry registry, RegistryCloneHost host)
    {
        this.host = host;
        baseRegistry = registry;

        userPreferences = registry.userPreferences;
        directories = new Map<global::Ts.TsPath.Path, Directory>(registry.directories);
        nodeModules = new Map<global::Ts.TsPath.Path, RegistryBucket>(registry.nodeModules);
        projects = new Map<global::Ts.TsPath.Path, RegistryBucket>(registry.projects);
        specifierCache = new MapBuilder<global::Ts.TsPath.Path, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>>(registry.specifierCache, static x => x, static x => x); // core.Identity
        uniquePackageCount = registry.uniquePackageCount;
        entrypoints = new MapBuilder<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>, List<global::Ts.Module.ResolvedEntrypoint>>(registry.entrypoints, static x => x, static x => x); // core.Identity
    }

    // registry.go:535-546 — Build.
    internal Registry Build()
    {
        return new Registry
        {
            toPath = baseRegistry.toPath,
            userPreferences = userPreferences,
            directories = directories.Finalize().Result ?? new Dictionary<global::Ts.TsPath.Path, Directory>(),
            nodeModules = nodeModules.Finalize().Result,
            projects = projects.Finalize().Result,
            specifierCache = specifierCache.Build(),
            uniquePackageCount = uniquePackageCount,
            entrypoints = entrypoints.Build(),
        };
    }

    // registry.go:548-696 — updateBucketAndDirectoryExistence.
    internal void UpdateBucketAndDirectoryExistence(RegistryChange change, LogTree? logger)
    {
        var start = DateTime.Now;
        var neededProjects = new HashSet<global::Ts.TsPath.Path>();
        var neededDirectories = new Dictionary<global::Ts.TsPath.Path, string>();
        // map-iter: 序无关 (集合/字典聚合)
        foreach (var (path, fileName) in change.OpenFiles ?? new Dictionary<global::Ts.TsPath.Path, string>())
        {
            neededProjects.Add(host.GetDefaultProject(path).Path);
            if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
            {
                continue;
            }
            var dir = fileName;
            var dirPath = path;
            while (true)
            {
                dir = global::Ts.TsPath.TsPath.GetDirectoryPath(dir);
                var lastDirPath = dirPath;
                dirPath = dirPath.GetDirectoryPath();
                if (dirPath == lastDirPath)
                {
                    break;
                }
                if (neededDirectories.ContainsKey(dirPath))
                {
                    break;
                }
                neededDirectories[dirPath] = dir;
            }

            if (!specifierCache.Has(path))
            {
                specifierCache.Set(path, new global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>());
            }
        }

        if (change.RequestedFile.Value.Length != 0)
        {
            neededProjects.Add(host.GetDefaultProject(change.RequestedFile).Path);
            if (!specifierCache.Has(change.RequestedFile))
            {
                specifierCache.Set(change.RequestedFile, new global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>());
            }
        }

        // map-iter: 序无关 (逐键删除判定)
        foreach (var path in (baseRegistry.specifierCache ?? new Dictionary<global::Ts.TsPath.Path, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>>()).Keys)
        {
            if ((change.OpenFiles == null || !change.OpenFiles.ContainsKey(path)) && path != change.RequestedFile)
            {
                specifierCache.Delete(path);
            }
        }

        var addedProjects = new List<global::Ts.TsPath.Path>();
        var removedProjects = new List<global::Ts.TsPath.Path>();
        var neededProjectsMap = new Dictionary<global::Ts.TsPath.Path, byte>(); // Go: map[tspath.Path]struct{}
        foreach (var p in neededProjects)
        {
            neededProjectsMap[p] = 0;
        }
        CoreUtil.DiffMapsFunc(
            baseRegistry.projects,
            neededProjectsMap,
            (RegistryBucket _, byte _) => throw new InvalidOperationException("never called because onChanged is nil"),
            (projectPath, _) =>
            {
                // Need and don't have
                projects.Add(projectPath, RegistryBucket.NewRegistryBucket());
                addedProjects.Add(projectPath);
            },
            (projectPath, _) =>
            {
                // Have and don't need
                projects.Delete(projectPath);
                removedProjects.Add(projectPath);
            },
            null);
        if (logger != null)
        {
            foreach (var projectPath in addedProjects)
            {
                logger.Logf("Added project: {0}", projectPath.Value);
            }
            foreach (var projectPath in removedProjects)
            {
                logger.Logf("Removed project: {0}", projectPath.Value);
            }
        }

        void UpdateDirectory(global::Ts.TsPath.Path dirPath, string dirName, bool packageJsonChangedFlag)
        {
            var packageJsonFileName = global::Ts.TsPath.TsPath.CombinePaths(dirName, "package.json");
            var hasNodeModules = host.FS().DirectoryExists(global::Ts.TsPath.TsPath.CombinePaths(dirName, "node_modules"));
            var (entry, ok) = directories.Get(dirPath);
            if (ok)
            {
                entry!.ChangeIf(
                    dir => packageJsonChangedFlag || dir.hasNodeModules != hasNodeModules,
                    dir =>
                    {
                        dir.packageJson = host.GetPackageJson(packageJsonFileName);
                        dir.hasNodeModules = hasNodeModules;
                    });
            }
            else
            {
                directories.Add(dirPath, new Directory
                {
                    name = dirName,
                    packageJson = host.GetPackageJson(packageJsonFileName),
                    hasNodeModules = hasNodeModules,
                });
            }

            if (hasNodeModules)
            {
                if (!nodeModules.Get(dirPath).Ok)
                {
                    nodeModules.Add(dirPath, RegistryBucket.NewRegistryBucket());
                }
            }
            else
            {
                nodeModules.TryDelete(dirPath);
            }
        }

        var addedNodeModulesDirs = new List<global::Ts.TsPath.Path>();
        var removedNodeModulesDirs = new List<global::Ts.TsPath.Path>();
        bool PackageJsonChanged(string dirName)
        {
            var uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(global::Ts.TsPath.TsPath.CombinePaths(dirName, "package.json"));
            return change.Changed.Has(uri) || change.Deleted.Has(uri) || change.Created.Has(uri);
        }
        CoreUtil.DiffMapsFunc(
            baseRegistry.directories,
            neededDirectories,
            (dir, dirName) => !PackageJsonChanged(dirName) && dir.hasNodeModules == host.FS().DirectoryExists(global::Ts.TsPath.TsPath.CombinePaths(dirName, "node_modules")),
            (dirPath, dirName) =>
            {
                // Need and don't have
                var hadNodeModules = baseRegistry.nodeModules != null && baseRegistry.nodeModules.ContainsKey(dirPath);
                UpdateDirectory(dirPath, dirName, false);
                if (logger != null)
                {
                    logger.Logf("Added directory: {0}", dirPath.Value);
                }
                if (nodeModules.Get(dirPath).Ok && !hadNodeModules)
                {
                    addedNodeModulesDirs.Add(dirPath);
                }
            },
            (dirPath, dir) =>
            {
                // Have and don't need
                var hadNodeModules = baseRegistry.nodeModules != null && baseRegistry.nodeModules.ContainsKey(dirPath);
                directories.Delete(dirPath);
                nodeModules.TryDelete(dirPath);
                if (logger != null)
                {
                    logger.Logf("Removed directory: {0}", dirPath.Value);
                }
                if (hadNodeModules)
                {
                    removedNodeModulesDirs.Add(dirPath);
                }
            },
            (dirPath, dir, dirName) =>
            {
                UpdateDirectory(dirPath, dirName, PackageJsonChanged(dirName));
                if (logger != null)
                {
                    logger.Logf("Changed directory: {0}", dirPath.Value);
                }
            });

        if (logger != null)
        {
            foreach (var dirPath in addedNodeModulesDirs)
            {
                logger.Logf("Added node_modules bucket: {0}", dirPath.Value);
            }
            foreach (var dirPath in removedNodeModulesDirs)
            {
                logger.Logf("Removed node_modules bucket: {0}", dirPath.Value);
            }
            logger.Logf("Updated buckets and directories in {0}", DateTime.Now - start);
        }
    }

    // registry.go:698-780 — markBucketsDirty.
    internal void MarkBucketsDirty(RegistryChange change, LogTree? logger)
    {
        // Mark new program structures
        // map-iter: 序无关 (逐桶独立标脏)
        foreach (var (projectPath, newFileNames) in change.RebuiltPrograms ?? new Dictionary<global::Ts.TsPath.Path, bool>())
        {
            var (bucket, ok) = projects.Get(projectPath);
            if (ok)
            {
                bucket!.Change(b =>
                {
                    b.state.newProgramStructure = newFileNames ? NewProgramStructure.DifferentFileNames : NewProgramStructure.SameFileNames;
                });
            }
        }

        // Mark files dirty, bailing out if all buckets already have multiple files dirty
        var cleanNodeModulesBuckets = new HashSet<global::Ts.TsPath.Path>();
        var cleanProjectBuckets = new HashSet<global::Ts.TsPath.Path>();
        nodeModules.Range(entry =>
        {
            if (!entry.Value().state.multipleFilesDirty)
            {
                cleanNodeModulesBuckets.Add(entry.Key());
            }
            return true;
        });
        projects.Range(entry =>
        {
            if (!entry.Value().state.multipleFilesDirty)
            {
                cleanProjectBuckets.Add(entry.Key());
            }
            return true;
        });

        void MarkFilesDirty(Set<global::Ts.LsProto.DocumentUri> uris)
        {
            if (cleanNodeModulesBuckets.Count == 0 && cleanProjectBuckets.Count == 0)
            {
                return;
            }
            // map-iter: 序无关 (逐 uri 标脏, 桶态单调)
            foreach (var uri in uris.Keys())
            {
                var path = baseRegistry.toPath(global::Ts.LsProto.DocumentUriExtensions.FileName(uri));
                if (cleanNodeModulesBuckets.Count > 0)
                {
                    // For node_modules, mark the bucket dirty if anything changes in the directory.
                    // The path could be either a symlink path (containing /node_modules/) or a realpath
                    // (for symlinked project references). Both are recorded in Paths for granular updates.
                    var nodeModulesIndex = path.Value.IndexOf("/node_modules/", StringComparison.Ordinal);
                    if (nodeModulesIndex != -1)
                    {
                        var dirPath = new global::Ts.TsPath.Path(path.Value[..nodeModulesIndex]);
                        if (cleanNodeModulesBuckets.Contains(dirPath))
                        {
                            var entry = nodeModules.Get(dirPath).Entry!;
                            // Look up the package name for granular updates
                            var packageName = "";
                            entry.Value().Paths?.TryGetValue(path, out packageName!);
                            entry.Change(bucket => bucket.MarkNodeModulesDirty(packageName ?? ""));
                            if (!entry.Value().state.multipleFilesDirty)
                            {
                                cleanNodeModulesBuckets.Remove(dirPath);
                            }
                        }
                    }
                    else
                    {
                        // Check if this path (possibly a realpath of a workspace package) is in any bucket's Paths.
                        // This handles local workspace packages where the realpath doesn't contain /node_modules/.
                        foreach (var bucketDirPath in new List<global::Ts.TsPath.Path>(cleanNodeModulesBuckets))
                        {
                            var entry = nodeModules.Get(bucketDirPath).Entry!;
                            if (entry.Value().Paths != null && entry.Value().Paths!.TryGetValue(path, out var packageName))
                            {
                                // Use the package name for granular updates
                                entry.Change(bucket => bucket.MarkNodeModulesDirty(packageName));
                                if (!entry.Value().state.multipleFilesDirty)
                                {
                                    cleanNodeModulesBuckets.Remove(bucketDirPath);
                                }
                            }
                        }
                    }
                }

                // For projects, mark the bucket dirty if the bucket contains the file directly.
                // Any other significant change, like a created failed lookup location, is
                // handled by newProgramStructure.
                foreach (var projectDirPath in new List<global::Ts.TsPath.Path>(cleanProjectBuckets))
                {
                    var (entry, _) = projects.Get(projectDirPath);
                    if (entry != null && entry.Value().Paths != null && entry.Value().Paths!.ContainsKey(path))
                    {
                        // Project buckets don't use package-based granular updates
                        entry.Change(bucket => bucket.MarkProjectFileDirty(path));
                        if (!entry.Value().state.multipleFilesDirty)
                        {
                            cleanProjectBuckets.Remove(projectDirPath);
                        }
                    }
                }
            }
        }

        MarkFilesDirty(change.Created);
        MarkFilesDirty(change.Deleted);
        MarkFilesDirty(change.Changed);
    }

    // registry.go:783-798 — nodeModulesBucketTask.
    private sealed class NodeModulesBucketTask
    {
        internal global::Ts.Project.Dirty.MapEntry<global::Ts.TsPath.Path, RegistryBucket> entry = null!;
        internal Set<string>? dependencyNames;
        internal string dirName = "";
        internal global::Ts.TsPath.Path dirPath;

        // For granular updates.
        internal bool isUpdate;
        internal RegistryBucket? existingBucket;
        internal Set<string>? dirtyPackages;

        // Filled by discovery.
        internal Set<string>? packageNames;
        internal Set<string>? directoryPackageNames;
        internal List<DiscoveredPackage> discovered = new();
    }

    // registry.go:782-1117 — updateIndexes.
    internal void UpdateIndexes(CancellationToken ctx, RegistryChange change, LogTree? logger)
    {
        var (projectPath, _) = host.GetDefaultProject(change.RequestedFile);
        if (projectPath.Value.Length == 0)
        {
            return;
        }

        // Compute resolved package names and project reference output mappings for all projects upfront.
        // Resolved package names are needed to compute node_modules dependencies so packages that are
        // directly imported by programs are included even if not listed in package.json.
        // Project reference output mappings are needed to redirect extraction from output .d.ts files
        // to source files for packages that are project references.
        // We need all projects because a node_modules directory can be used by multiple projects.
        var allResolvedPackageNames = new Dictionary<global::Ts.TsPath.Path, Set<string>?>();
        var projectReferenceOutputs = new Dictionary<global::Ts.TsPath.Path, string>();
        // Compute which packages have implicit deep imports (subpath imports in packages
        // without exports). These packages need recursive directory search to discover
        // all auto-importable files, even when the preference is disabled.
        var allDeepImportPackages = new Set<string>();
        projects.Range(entry =>
        {
            var program = host.GetProgramForProject(entry.Key());
            if (program != null)
            {
                allResolvedPackageNames[entry.Key()] = Util.GetResolvedPackageNames(program);
                Util.AddProjectReferenceOutputMappings(program, projectReferenceOutputs);
                foreach (var name in program.DeepImportPackageNames().Keys())
                {
                    allDeepImportPackages.Add(name);
                }
            }
            return true;
        });

        var fileExcludePatterns = userPreferences.ParsedAutoImportFileExcludePatterns(host.FS().UseCaseSensitiveFileNames());

        // Determine which packages need recursive directory search for this build.
        // nil means all packages (preference is enabled for all).
        Set<string>? targetRecursivePackages = null;
        if (!userPreferences.AutoImportEntrypointDirectorySearch.IsTrue())
        {
            targetRecursivePackages = allDeepImportPackages;
        }

        // --- Collect node_modules tasks ---
        var nodeModulesTasks = new List<NodeModulesBucketTask>();
        global::Ts.TsPath.TsPath.ForEachAncestorDirectoryPath<object?>(change.RequestedFile, dirPath =>
        {
            var (nodeModulesBucket, ok) = nodeModules.Get(dirPath);
            if (ok)
            {
                var dirName = directories.Get(dirPath).Entry!.Value().name;
                var dependencies = ComputeDependenciesForNodeModulesDirectory(change, allResolvedPackageNames, dirName, dirPath);
                var bucketState = nodeModulesBucket!.Value().state;
                // !!! Optimization: handle different dependency set via granular updates
                var needsFullRebuild = bucketState.multipleFilesDirty ||
                    !(nodeModulesBucket.Value().DependencyNames == null ? dependencies == null : nodeModulesBucket.Value().DependencyNames!.Equals(dependencies)) ||
                    !bucketState.buildPreferences.Equal(BucketBuildPreferences.FromUserPreferences(userPreferences)) ||
                    !AutoImportApi.RecursiveSearchSubset(targetRecursivePackages, bucketState.recursiveSearchPackages);
                var dirtyPackages = bucketState.DirtyPackages();
                var canDoGranularUpdate = !needsFullRebuild && dirtyPackages.Len() > 0;

                if (needsFullRebuild)
                {
                    nodeModulesTasks.Add(new NodeModulesBucketTask
                    {
                        entry = nodeModulesBucket,
                        dependencyNames = dependencies,
                        dirName = dirName,
                        dirPath = dirPath,
                    });
                }
                else if (canDoGranularUpdate)
                {
                    nodeModulesTasks.Add(new NodeModulesBucketTask
                    {
                        entry = nodeModulesBucket,
                        dependencyNames = dependencies,
                        dirName = dirName,
                        dirPath = dirPath,
                        isUpdate = true,
                        existingBucket = nodeModulesBucket.Value(),
                        dirtyPackages = dirtyPackages,
                    });
                }
            }
            return (null, false);
        });

        LogTree? nodeModulesLogger = null;
        if (logger != null && nodeModulesTasks.Count > 0)
        {
            nodeModulesLogger = logger.Fork("Building node_modules indexes");
        }

        // --- Phase 1: Discovery (parallel per bucket) ---
        // Resolve package.json and realpath for each package in each bucket.
        var discoveryStart = DateTime.Now;
        var wg1 = global::Ts.Core.WorkGroup.Create(false);
        foreach (var task in nodeModulesTasks)
        {
            wg1.Queue(() =>
            {
                if (task.isUpdate)
                {
                    task.packageNames = task.dirtyPackages;
                }
                else
                {
                    var dirSet = Util.GetPackageNamesInNodeModules(global::Ts.TsPath.TsPath.CombinePaths(task.dirName, "node_modules"), host.FS());
                    task.directoryPackageNames = new Set<string>(dirSet.Count);
                    foreach (var n in dirSet)
                    {
                        task.directoryPackageNames.Add(n);
                    }
                    task.packageNames = task.dependencyNames ?? task.directoryPackageNames; // core.Coalesce
                }
                task.discovered = DiscoverBucketPackages(task.packageNames, task.dirName, task.dirPath);
            });
        }
        wg1.RunAndWait();
        if (nodeModulesLogger != null)
        {
            nodeModulesLogger.Logf("Discovered packages: {0}", DateTime.Now - discoveryStart);
        }

        // --- Phase 2: Extraction (parallel per unique realpath) ---
        // Extract from main packages first. If a main package has no TypeScript entrypoints,
        // we fall back to extracting from @types in a second pass. Packages with no main
        // package extract directly from @types in the primary pass.
        var extractionStart = DateTime.Now;
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        var extractionCache = new Dictionary<string, PerPackageExtractionResult?>(StringComparer.Ordinal);
        var extractionMu = new object();
        // Collect all packages that have an @types fallback. After the primary pass, we
        // filter to only those whose main extraction failed, then deduplicate by typesRealpath.
        var typesFallbackCandidates = new List<DiscoveredPackage>();
        var wg2 = global::Ts.Core.WorkGroup.Create(false);
        foreach (var task in nodeModulesTasks)
        {
            foreach (var pkg in task.discovered)
            {
                if (pkg.realpath.Length != 0)
                {
                    if (!seen.ContainsKey(pkg.realpath))
                    {
                        seen[pkg.realpath] = true;
                        var enableDirSearch = targetRecursivePackages == null ||
                            targetRecursivePackages.Has(pkg.packageName) ||
                            AutoImportApi.KnownRecursiveSearchPackages.Has(pkg.packageName);
                        // Record actual directory-searched packages so the stored set
                        // reflects reality for rebuild detection and stats.
                        if (enableDirSearch && targetRecursivePackages != null)
                        {
                            targetRecursivePackages.Add(pkg.packageName);
                        }
                        var pkgCopy = pkg;
                        wg2.Queue(() =>
                        {
                            if (ctx.IsCancellationRequested)
                            {
                                return;
                            }
                            var extractResult = ExtractPackage(ctx, pkgCopy.packageJson, pkgCopy.packageName, projectReferenceOutputs, fileExcludePatterns, enableDirSearch);
                            if (extractResult != null)
                            {
                                lock (extractionMu)
                                {
                                    extractionCache[pkgCopy.realpath] = extractResult;
                                }
                            }
                        });
                    }
                    if (pkg.typesRealpath.Length != 0)
                    {
                        typesFallbackCandidates.Add(pkg);
                    }
                }
                else if (pkg.typesRealpath.Length != 0)
                {
                    if (!seen.ContainsKey(pkg.typesRealpath))
                    {
                        seen[pkg.typesRealpath] = true;
                        // @types packages always get directory search
                        if (targetRecursivePackages != null)
                        {
                            targetRecursivePackages.Add(pkg.packageName);
                        }
                        var pkgCopy = pkg;
                        wg2.Queue(() =>
                        {
                            if (ctx.IsCancellationRequested)
                            {
                                return;
                            }
                            var extractResult = ExtractPackage(ctx, pkgCopy.typesPackageJson, pkgCopy.packageName, projectReferenceOutputs, fileExcludePatterns, true /*enableDirectorySearch*/);
                            if (extractResult != null)
                            {
                                lock (extractionMu)
                                {
                                    extractionCache[pkgCopy.typesRealpath] = extractResult;
                                }
                            }
                        });
                    }
                }
            }
        }
        wg2.RunAndWait();

        // For packages whose main extraction yielded nothing, fall back to @types.
        var wg3 = global::Ts.Core.WorkGroup.Create(false);
        foreach (var pkg in typesFallbackCandidates)
        {
            bool mainExtracted;
            lock (extractionMu)
            {
                mainExtracted = extractionCache.TryGetValue(pkg.realpath, out var e) && e != null;
            }
            if (mainExtracted || seen.ContainsKey(pkg.typesRealpath))
            {
                continue;
            }
            seen[pkg.typesRealpath] = true;
            // @types fallback packages always get directory search
            if (targetRecursivePackages != null)
            {
                targetRecursivePackages.Add(pkg.packageName);
            }
            var pkgCopy = pkg;
            wg3.Queue(() =>
            {
                if (ctx.IsCancellationRequested)
                {
                    return;
                }
                var extractResult = ExtractPackage(ctx, pkgCopy.typesPackageJson, pkgCopy.packageName, projectReferenceOutputs, fileExcludePatterns, true /*enableDirectorySearch*/);
                if (extractResult != null)
                {
                    lock (extractionMu)
                    {
                        extractionCache[pkgCopy.typesRealpath] = extractResult;
                    }
                }
            });
        }
        wg3.RunAndWait();
        if (nodeModulesLogger != null)
        {
            nodeModulesLogger.Logf("Extracted exports: {0} ({1} packages)", DateTime.Now - extractionStart, seen.Count);
        }
        uniquePackageCount = seen.Count;

        // --- Phase 3: Bucket building (parallel per bucket) ---
        // Each bucket installs the shared extraction results and builds its index.
        var allResults = new List<BucketBuildResult>();
        var wg4 = global::Ts.Core.WorkGroup.Create(false);

        foreach (var task in nodeModulesTasks)
        {
            var br = new BucketBuildResult { entry = task.entry };
            allResults.Add(br);
            wg4.Queue(() =>
            {
                if (task.isUpdate)
                {
                    UpdateNodeModulesBucket(
                        ctx, br, task.existingBucket!, task.dirtyPackages, task.discovered, extractionCache,
                        targetRecursivePackages, nodeModulesLogger.Fork(task.dirName));
                }
                else
                {
                    BuildNodeModulesBucket(
                        ctx, br, task.dependencyNames, task.dirPath, task.discovered, task.directoryPackageNames, extractionCache,
                        targetRecursivePackages, nodeModulesLogger.Fork(task.dirName));
                }
            });
        }

        // Project bucket (not part of the three-phase pipeline — no cross-bucket dedup needed).
        var (project, hasProject) = projects.Get(projectPath);
        if (hasProject)
        {
            var program = host.GetProgramForProject(projectPath);
            allResolvedPackageNames.TryGetValue(projectPath, out var resolvedPackageNames);
            var shouldRebuild = project!.Value().state.HasDirtyFileBesides(change.RequestedFile) ||
                !project.Value().state.buildPreferences.Equal(BucketBuildPreferences.FromUserPreferences(userPreferences));
            if (!shouldRebuild && project.Value().state.newProgramStructure > 0)
            {
                if (!(project.Value().ResolvedPackageNames == null ? resolvedPackageNames == null : project.Value().ResolvedPackageNames!.Equals(resolvedPackageNames)) || AutoImportApi.HasNewNonNodeModulesFiles(program!, project.Value()))
                {
                    shouldRebuild = true;
                }
                else
                {
                    project.Change(b => b.state.newProgramStructure = NewProgramStructure.False);
                }
            }
            if (shouldRebuild)
            {
                var br = new BucketBuildResult { entry = project };
                allResults.Add(br);
                wg4.Queue(() =>
                {
                    BuildProjectBucket(
                        ctx, br, projectPath, resolvedPackageNames,
                        logger.Fork("Building project bucket " + projectPath.Value));
                });
            }
        }

        wg4.RunAndWait();

        foreach (var br in allResults)
        {
            if (br.err != null)
            {
                continue;
            }
            foreach (var path in br.removedEntrypointPaths ?? new List<global::Ts.TsPath.Path>())
            {
                entrypoints.Delete(path);
            }
            // map-iter: 序无关 (逐键 Set)
            foreach (var (path, entries) in br.entrypoints ?? new Dictionary<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>>())
            {
                entrypoints.Set(path, entries);
            }
            br.entry.Replace(br.bucket!);
        }

        // If we failed to resolve any alias exports by ending up at a non-relative module specifier
        // that didn't resolve to another package, it's probably an ambient module declared in another package.
        // We recorded these failures, along with the name of every ambient module declared elsewhere, so we
        // can do a second pass on the failed files, this time including the ambient modules declarations that
        // were missing the first time. Example: node_modules/fs-extra/index.d.ts is simply `export * from "fs"`,
        // but when trying to resolve the `export *`, we don't know where "fs" is declared. The aliasResolver
        // tries to find packages named "fs" on the file system, but after failing, records "fs" as a failure
        // for fs-extra/index.d.ts. Meanwhile, if we also processed node_modules/@types/node/fs.d.ts, we
        // recorded that file as declaring the ambient module "fs". In the second pass, we combine those two
        // files and reprocess fs-extra/index.d.ts, this time finding "fs" declared in @types/node.
        var secondPassStart = DateTime.Now;
        var secondPassFileCount = 0;
        foreach (var br in allResults)
        {
            if (br.err != null)
            {
                continue;
            }
            if (br.possibleFailedAmbientModuleLookupTargets == null)
            {
                continue;
            }
            var rootFiles = new Dictionary<string, SourceFile?>(StringComparer.Ordinal);
            foreach (var target in br.possibleFailedAmbientModuleLookupTargets.Keys())
            {
                foreach (var fileName in ResolveAmbientModuleName(target, br.entry.Key()) ?? new List<string>())
                {
                    if (rootFiles.ContainsKey(fileName))
                    {
                        continue;
                    }
                    rootFiles[fileName] = host.GetSourceFile(fileName, baseRegistry.toPath(fileName));
                    secondPassFileCount++;
                }
            }
            if (rootFiles.Count > 0)
            {
                var moduleResolver = new global::Ts.Module.Resolver(host, global::Ts.Core.Core.EmptyCompilerOptions, "", "", resolverOptions);
                var rootFileList = new List<SourceFile>();
                // map-iter: 序无关 (根文件集; 提取按文件独立)
                foreach (var f in rootFiles.Values)
                {
                    if (f != null)
                    {
                        rootFileList.Add(f);
                    }
                }
                var aliasResolver = AliasResolver.NewAliasResolver(
                    rootFileList.ToArray(),
                    null,
                    host,
                    moduleResolver,
                    baseRegistry.toPath,
                    (_, _) =>
                    {
                        // no-op
                    });
                var ch = global::Ts.Checker.Checker.NewChecker(aliasResolver, null).Checker;
                br.possibleFailedAmbientModuleLookupSources!.Range((path, source) =>
                {
                    var sourceFile = aliasResolver.GetSourceFile(source.fileName);
                    var extractor = NewExportExtractor(source.packageName, ch, moduleResolver, host.FS().Realpath);
                    var fileExports = extractor.ExtractFromFile(sourceFile!);
                    foreach (var exp in fileExports)
                    {
                        br.bucket!.Index!.InsertAsWords(exp);
                    }
                    return true;
                });
            }
        }

        if (nodeModulesLogger != null)
        {
            if (secondPassFileCount > 0)
            {
                nodeModulesLogger.Logf("{0} files required second pass, took {1}", secondPassFileCount, DateTime.Now - secondPassStart);
            }
            nodeModulesLogger.Logf("Total: {0}", DateTime.Now - discoveryStart);
        }
    }
}
