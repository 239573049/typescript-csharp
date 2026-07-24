// Ported from tsgo internal/ls/autoimport/registry.go — Registry.Clone (:388-410), the
// registryBuilder skeleton (:505-546) and its existence/dirty passes
// (updateBucketAndDirectoryExistence :548-696, markBucketsDirty :698-780).
// PORT-NOTE: updateIndexes (:782-1117) and the extraction pipeline (:1210-1826) land with the
// next registry batch; until then Clone panics when change.RequestedFile is set.

using System;
using System.Collections.Generic;
using System.Threading;
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

    // registry.go:782-1117 — updateIndexes. PORT-NOTE: the three-phase node_modules extraction
    // pipeline lands with the next registry batch.
    internal void UpdateIndexes(CancellationToken ctx, RegistryChange change, LogTree? logger)
    {
        throw new NotImplementedException("autoimport.registryBuilder.updateIndexes: extraction batch");
    }
}
