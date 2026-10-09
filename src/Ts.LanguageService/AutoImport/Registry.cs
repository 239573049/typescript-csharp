// Ported from tsgo internal/ls/autoimport/registry.go — the model layer and builder skeleton
// (:31-533 + :548-780 builder existence/dirty passes + :1119-1208 helpers/result types).
// PORT-NOTE: updateIndexes (:782-1117) and the extraction pipeline (:1210-1826) land with the
// next registry batch; until then Clone panics when RequestedFile is set.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.Project.Logging;

namespace Ts.Ls.AutoImport;

public static partial class AutoImportApi
{
    // registry.go:31-79 — knownRecursiveSearchPackages.
    internal static readonly Set<string> KnownRecursiveSearchPackages = Set.FromItems(
        "@material-ui/core",
        "@material-ui/icons",
        "@sap/cds",
        "@testing-library/react-native",
        "ajv",
        "asap",
        "async",
        "aws-sdk",
        "braintree-web",
        "core-js",
        "core-js-pure",
        "crypto-js",
        "cypress-mochawesome-reporter",
        "dd-trace",
        "dumi",
        "dva",
        "egg-mock",
        "electron-log",
        "es-abstract",
        "es6-promise",
        "eslint-config-taro",
        "expo",
        "expo-router",
        "flow-remove-types",
        "gatsby",
        "glamor",
        "gluegun",
        "graphology-indices",
        "graphology-traversal",
        "graphology-utils",
        "jest-expo",
        "lodash",
        "lodash-es",
        "moment",
        "mz",
        "next",
        "pdfjs-dist",
        "protobufjs",
        "react-app-polyfill",
        "react-dev-utils",
        "react-devtools-inline",
        "recast",
        "semver",
        "stylelint-config-html",
        "umi",
        "web3-provider-engine",
        "webpack");
}

// registry.go:81-87 — newProgramStructure.
internal enum NewProgramStructure
{
    False = 0,
    SameFileNames,
    DifferentFileNames,
}

// registry.go:89-96 — bucketBuildPreferences holds user preferences that affect how a bucket
// is built. When any of these change between builds, the bucket must be rebuilt.
// Adding a new preference here automatically integrates it into the rebuild checks via Equal.
internal struct BucketBuildPreferences
{
    internal string[]? fileExcludePatterns;
    internal Tristate autoImportEntrypointDirectorySearch;

    // registry.go:98-103 — bucketBuildPreferencesFromUserPreferences.
    internal static BucketBuildPreferences FromUserPreferences(UserPreferences prefs)
    {
        return new BucketBuildPreferences
        {
            fileExcludePatterns = prefs.AutoImportFileExcludePatterns,
            autoImportEntrypointDirectorySearch = prefs.AutoImportEntrypointDirectorySearch,
        };
    }

    // registry.go:105-108 — Equal.
    internal bool Equal(BucketBuildPreferences other)
    {
        return CoreUtil.UnorderedEqual(fileExcludePatterns ?? [], other.fileExcludePatterns ?? []) &&
            autoImportEntrypointDirectorySearch == other.autoImportEntrypointDirectorySearch;
    }

    // registry.go:110-113 — Clone.
    internal BucketBuildPreferences Clone()
    {
        var p = this;
        p.fileExcludePatterns = fileExcludePatterns == null ? null : (string[])fileExcludePatterns.Clone();
        return p;
    }
}

// registry.go:115-149 — BucketState represents the dirty state of a bucket.
// In general, a bucket can be used for an auto-imports request if it is clean
// or if the only edited file is the one that was requested for auto-imports.
// Most edits within a file will not change the imports available to that file.
// However, one exception causes the bucket to be rebuilt after a change to a
// single file: local files are newly added to the project by a manual import.
// This can only happen after a full (non-clone) program update. When this
// happens, the `newProgramStructure` flag is set until the next time the bucket
// is rebuilt, when this condition will be checked.
public struct BucketState
{
    // dirtyFile is the file that was edited last, if any. It does not necessarily
    // indicate that no other files have been edited, so it should be ignored if
    // `multipleFilesDirty` is set. It should not be used for node_modules buckets,
    // which rely on `dirtyPackages` instead.
    internal global::Ts.TsPath.Path dirtyFile;
    internal bool multipleFilesDirty;
    internal NewProgramStructure newProgramStructure;
    // buildPreferences holds the user preferences that were in effect when
    // the bucket was built. If changed, the bucket should be rebuilt.
    internal BucketBuildPreferences buildPreferences;
    // dirtyPackages is the set of package names that need to be re-indexed.
    // This is used for granular updates: when a file in a local workspace package
    // changes, only that package needs to be re-extracted rather than rebuilding
    // the entire node_modules bucket.
    // If nil, no granular updates are pending.
    // If set but multipleFilesDirty is true, the entire bucket needs to be rebuilt.
    internal Set<string>? dirtyPackages;
    // recursiveSearchPackages tracks which packages were recursively directory-searched
    // when the bucket was built. nil means all non-exports packages were searched
    // (e.g. when the autoImportEntrypointDirectorySearch preference is enabled).
    // A non-nil set lists only the specific packages that were searched.
    // Used for rebuild detection: a rebuild is triggered when target packages are
    // not a subset of the currently searched packages.
    internal Set<string>? recursiveSearchPackages;

    // registry.go:151-156 — Clone.
    public BucketState Clone()
    {
        var b = this;
        b.buildPreferences = buildPreferences.Clone();
        b.dirtyPackages = dirtyPackages.Clone();
        b.recursiveSearchPackages = recursiveSearchPackages.Clone();
        return b;
    }

    // registry.go:158-160 — Dirty. (Path is a struct in C#: its default has Value == null,
    // while Go's `type Path string` zero value is "" — hence IsNullOrEmpty instead of
    // len(...) != 0.)
    public bool Dirty()
    {
        return multipleFilesDirty || !string.IsNullOrEmpty(dirtyFile.Value) || newProgramStructure > 0 || dirtyPackages.Len() > 0;
    }

    // registry.go:162-167 — DirtyFile.
    public global::Ts.TsPath.Path DirtyFile()
    {
        if (multipleFilesDirty)
        {
            return default;
        }
        return dirtyFile;
    }

    // registry.go:169-174 — DirtyPackages.
    public Set<string>? DirtyPackages()
    {
        if (multipleFilesDirty)
        {
            return null;
        }
        return dirtyPackages;
    }

    // registry.go:176-178 — RecursiveSearchPackages.
    public Set<string>? RecursiveSearchPackages()
    {
        return recursiveSearchPackages;
    }

    // registry.go:180-185 — possiblyNeedsRebuildForFile.
    internal bool PossiblyNeedsRebuildForFile(global::Ts.TsPath.Path file, UserPreferences preferences)
    {
        return newProgramStructure > 0 ||
            HasDirtyFileBesides(file) ||
            !buildPreferences.Equal(BucketBuildPreferences.FromUserPreferences(preferences)) ||
            dirtyPackages.Len() > 0;
    }

    // registry.go:187-189 — hasDirtyFileBesides. (IsNullOrEmpty: default(Path).Value is
    // null in C#, whereas Go's `type Path string` zero value is "" — see Dirty above.)
    internal bool HasDirtyFileBesides(global::Ts.TsPath.Path file)
    {
        return multipleFilesDirty || !string.IsNullOrEmpty(dirtyFile.Value) && dirtyFile != file;
    }
}

public static partial class AutoImportApi
{
    // registry.go:191-205 — recursiveSearchSubset reports whether target is a subset of current.
    // nil represents "all packages" — a superset of every concrete set.
    // Returns true if the current set already covers everything the target needs,
    // meaning no rebuild is required for recursive search purposes.
    internal static bool RecursiveSearchSubset(Set<string>? target, Set<string>? current)
    {
        if (target == null)
        {
            // Target wants all packages searched — only satisfied if current is also all.
            return current == null;
        }
        if (current == null)
        {
            // Current searched all packages, so any concrete target is satisfied.
            return true;
        }
        return target.IsSubsetOf(current);
    }
}

// registry.go:207-256 — RegistryBucket.
public sealed class RegistryBucket : global::Ts.Project.Dirty.Cloneable<RegistryBucket>
{
    internal BucketState state;

    // Paths maps file paths to package names. For project buckets, the package name
    // is always empty string. For node_modules buckets, this enables reverse lookup
    // from path to package for granular updates. Only paths for local workspace
    // packages (symlinked and within the workspace root) have entries here, since
    // their realpaths are outside node_modules and need reverse lookup for dirty
    // detection.
    //
    // Paths is considered immutable after the bucket is finalized.
    // It should be fully replaced rather than mutated while changing a bucket.
    public Dictionary<global::Ts.TsPath.Path, string>? Paths;
    // PackageFiles maps package names to their file paths and file names.
    // All package directory names in node_modules are keys; indexed packages have
    // non-nil maps with path→fileName entries, unindexed packages have nil maps.
    // This enables efficient removal of a package's files during granular updates
    // without iterating through all entries. Only defined for node_modules buckets.
    //
    // PackageFiles is considered immutable after the bucket is finalized.
    // It should be fully replaced rather than mutated while changing a bucket.
    public Dictionary<string, Dictionary<global::Ts.TsPath.Path, string>?>? PackageFiles;
    // ResolvedPackageNames is only defined for project buckets. It is the set of
    // package names that were resolved from imports in the project's program files.
    // This is passed to node_modules buckets so they include packages that are
    // directly imported even if not listed in package.json dependencies.
    //
    // ResolvedPackageNames is considered immutable after the bucket is finalized.
    // It should be fully replaced rather than mutated while changing a bucket.
    public Set<string>? ResolvedPackageNames;
    // DependencyNames is only defined for node_modules buckets. It is the set of
    // package names that will be included in the bucket if present in the directory,
    // computed from package.json dependencies plus resolved package names from
    // active programs. If nil, all packages are included because at least one open
    // file has access to this node_modules directory without being filtered by a
    // package.json.
    //
    // DependencyNames is considered immutable after the bucket is finalized.
    // It should be fully replaced rather than mutated while changing a bucket.
    public Set<string>? DependencyNames;
    // AmbientModuleNames is only defined for node_modules buckets. It is the set of
    // ambient module names found while extracting exports in the bucket.
    //
    // AmbientModuleNames is considered immutable after the bucket is finalized.
    // It should be fully replaced rather than mutated while changing a bucket.
    public Dictionary<string, List<string>>? AmbientModuleNames;
    // Index is considered immutable after the bucket is finalized.
    // It should be cloned and replaced rather than mutated while changing a bucket.
    public Index<Export>? Index;

    // registry.go:258-265 — newRegistryBucket.
    internal static RegistryBucket NewRegistryBucket()
    {
        return new RegistryBucket
        {
            state = new BucketState
            {
                multipleFilesDirty = true,
                newProgramStructure = NewProgramStructure.DifferentFileNames,
            },
        };
    }

    // registry.go:267-277 — Clone.
    public RegistryBucket Clone()
    {
        return new RegistryBucket
        {
            state = state.Clone(),
            Paths = Paths,
            PackageFiles = PackageFiles,
            ResolvedPackageNames = ResolvedPackageNames,
            DependencyNames = DependencyNames,
            AmbientModuleNames = AmbientModuleNames,
            Index = Index,
        };
    }

    // registry.go:279-288 — markProjectFileDirty should only be called within a Change call on
    // the dirty map. Buckets are considered immutable once in a finalized registry. Should only
    // be used for project buckets.
    internal void MarkProjectFileDirty(global::Ts.TsPath.Path file)
    {
        if (state.HasDirtyFileBesides(file))
        {
            state.multipleFilesDirty = true;
        }
        else
        {
            state.dirtyFile = file;
        }
    }

    // registry.go:290-307 — markNodeModulesDirty should only be called within a Change call on
    // the dirty map. Buckets are considered immutable once in a finalized registry. If
    // packageName is non-empty, that package is marked for granular update. Otherwise, the
    // entire bucket is marked dirty.
    internal void MarkNodeModulesDirty(string packageName)
    {
        if (state.multipleFilesDirty)
        {
            return;
        }
        if (packageName.Length == 0)
        {
            state.multipleFilesDirty = true;
            return;
        }
        // Track the package for granular updates
        state.dirtyPackages ??= new Set<string>();
        state.dirtyPackages.Add(packageName);
    }
}

// registry.go:309-321 — directory.
internal sealed class Directory : global::Ts.Project.Dirty.Cloneable<Directory>
{
    internal string name = "";
    internal global::Ts.PackageJson.InfoCacheEntry? packageJson;
    internal bool hasNodeModules;

    public Directory Clone()
    {
        return new Directory
        {
            name = name,
            packageJson = packageJson,
            hasNodeModules = hasNodeModules,
        };
    }
}

// registry.go:323-339 — Registry.
public sealed partial class Registry
{
    internal Func<string, global::Ts.TsPath.Path> toPath = null!;
    internal UserPreferences userPreferences = null!;

    // exports      map[tspath.Path][]*RawExport
    internal Dictionary<global::Ts.TsPath.Path, Directory> directories = null!;

    internal Dictionary<global::Ts.TsPath.Path, RegistryBucket>? nodeModules;
    internal Dictionary<global::Ts.TsPath.Path, RegistryBucket>? projects;
    internal int uniquePackageCount;

    // entrypoints maps from file path to the resolved entrypoints for that file, shared across all node_modules buckets.
    internal Dictionary<global::Ts.TsPath.Path, List<global::Ts.Module.ResolvedEntrypoint>>? entrypoints;

    // specifierCache maps from importing file to target file to specifier.
    internal Dictionary<global::Ts.TsPath.Path, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>>? specifierCache;

    // registry.go:341-347 — NewRegistry.
    public Registry(Func<string, global::Ts.TsPath.Path> toPath, UserPreferences preferences)
    {
        this.toPath = toPath;
        userPreferences = preferences;
        directories = new Dictionary<global::Ts.TsPath.Path, Directory>();
    }

    internal Registry()
    {
    }

    // registry.go:378-386 — NodeModulesDirectories.
    public Dictionary<global::Ts.TsPath.Path, string> NodeModulesDirectories()
    {
        var dirs = new Dictionary<global::Ts.TsPath.Path, string>();
        // map-iter: 序无关 (写入目标为字典)
        foreach (var (dirPath, dir) in directories)
        {
            if (dir.hasNodeModules)
            {
                dirs[new global::Ts.TsPath.Path(global::Ts.TsPath.TsPath.CombinePaths(dirPath.Value, "node_modules"))] = global::Ts.TsPath.TsPath.CombinePaths(dir.name, "node_modules");
            }
        }
        return dirs;
    }

    // registry.go:427-480 — GetCacheStats.
    public CacheStats GetCacheStats()
    {
        var stats = new CacheStats
        {
            UniquePackageCount = uniquePackageCount,
        };

        // map-iter: 序无关 (结果随后按 Path 排序)
        foreach (var (path, bucket) in projects ?? new Dictionary<global::Ts.TsPath.Path, RegistryBucket>())
        {
            var exportCount = 0;
            if (bucket.Index != null)
            {
                exportCount = bucket.Index.entries.Count;
            }
            stats.ProjectBuckets.Add(new BucketStats
            {
                Path = path,
                ExportCount = exportCount,
                FileCount = (bucket.Paths ?? new Dictionary<global::Ts.TsPath.Path, string>()).Count,
                State = bucket.state,
                DependencyNames = bucket.DependencyNames,
                PackageNames = null,
            });
        }

        // map-iter: 序无关 (结果随后按 Path 排序)
        foreach (var (path, bucket) in nodeModules ?? new Dictionary<global::Ts.TsPath.Path, RegistryBucket>())
        {
            var exportCount = 0;
            if (bucket.Index != null)
            {
                exportCount = bucket.Index.entries.Count;
            }
            // Derive PackageNames from PackageFiles keys
            Set<string>? packageNames = null;
            var fileCount = 0;
            if (bucket.PackageFiles != null)
            {
                packageNames = new Set<string>(bucket.PackageFiles.Count); // NewSetWithSizeHint
                // map-iter: 序无关 (仅聚合计数与集合)
                foreach (var (name, paths) in bucket.PackageFiles)
                {
                    packageNames.Add(name);
                    fileCount += paths?.Count ?? 0;
                }
            }
            stats.NodeModulesBuckets.Add(new BucketStats
            {
                Path = path,
                ExportCount = exportCount,
                FileCount = fileCount,
                State = bucket.state,
                DependencyNames = bucket.DependencyNames,
                PackageNames = packageNames,
            });
        }

        stats.ProjectBuckets.Sort((a, b) => string.CompareOrdinal(a.Path.Value, b.Path.Value));
        stats.NodeModulesBuckets.Sort((a, b) => string.CompareOrdinal(a.Path.Value, b.Path.Value));

        return stats;
    }
}

public static partial class AutoImportApi
{
    // registry.go:349-376 — IsPreparedForImportingFile (nil-receiver → extension per plan/05).
    public static bool IsPreparedForImportingFile(this Registry? r, string fileName, global::Ts.TsPath.Path projectPath, UserPreferences preferences)
    {
        if (r == null)
        {
            return false;
        }
        if (r.projects == null || !r.projects.TryGetValue(projectPath, out var projectBucket))
        {
            return false;
        }
        var path = r.toPath(fileName);
        if (projectBucket.state.PossiblyNeedsRebuildForFile(path, preferences))
        {
            return false;
        }

        var dirPath = path.GetDirectoryPath();
        while (true)
        {
            if (r.nodeModules != null && r.nodeModules.TryGetValue(dirPath, out var dirBucket))
            {
                if (dirBucket.state.PossiblyNeedsRebuildForFile(path, preferences))
                {
                    return false;
                }
            }
            var parent = dirPath.GetDirectoryPath();
            if (parent == dirPath)
            {
                break;
            }
            dirPath = parent;
        }
        return true;
    }
}

// registry.go:412-419 — BucketStats.
public sealed class BucketStats
{
    public global::Ts.TsPath.Path Path;
    public int ExportCount;
    public int FileCount;
    public BucketState State;
    public Set<string>? DependencyNames;
    public Set<string>? PackageNames;
}

// registry.go:421-425 — CacheStats.
public sealed class CacheStats
{
    public List<BucketStats> ProjectBuckets = new();
    public List<BucketStats> NodeModulesBuckets = new();
    public int UniquePackageCount;
}

// registry.go:482-493 — RegistryChange.
public sealed class RegistryChange
{
    public global::Ts.TsPath.Path RequestedFile;
    public Dictionary<global::Ts.TsPath.Path, string>? OpenFiles;
    public Set<global::Ts.LsProto.DocumentUri> Changed = new();
    public Set<global::Ts.LsProto.DocumentUri> Created = new();
    public Set<global::Ts.LsProto.DocumentUri> Deleted = new();
    // RebuiltPrograms maps from project path to:
    //   - true: the program was rebuilt with a different set of file names
    //   - false: the program was rebuilt but the set of file names is unchanged
    public Dictionary<global::Ts.TsPath.Path, bool>? RebuiltPrograms;
    public UserPreferences? UserPreferences;
}
