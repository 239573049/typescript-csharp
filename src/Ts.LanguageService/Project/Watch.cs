// Ported from tsgo internal/project/watch.go (whole file).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Collections;

namespace Ts.Project;

// watch.go:18-20 — minWatchLocationDepth. (Go package-level const → WatchConstants
// wrapper; qualified name is used cross-file, e.g. ConfigFileRegistryBuilder.)
internal static class WatchConstants
{
    internal const int MinWatchLocationDepth = 2;
}

// watch.go:22-25 — fileSystemWatcherKey.
internal readonly record struct FileSystemWatcherKey(string Pattern, WatchKind Kind);

// watch.go:27-30 — fileSystemWatcherValue.
internal sealed class FileSystemWatcherValue
{
    internal int count;
    internal WatcherID id;
}

// watch.go:32-41 — watchRegistry tracks the current watch globs and how many individual
// WatchedFiles reference each glob. It provides ref-count helpers so callers
// don't manipulate the map directly.
//
// All methods are safe for concurrent use; locking is handled internally.
internal sealed class WatchRegistry
{
    private readonly Lock mu = new();
    private readonly Dictionary<FileSystemWatcherKey, FileSystemWatcherValue> entries;
    private readonly HashSet<WatcherID> pending;

    // watch.go:43-48 — newWatchRegistry.
    internal static WatchRegistry NewWatchRegistry()
    {
        return new WatchRegistry();
    }

    private WatchRegistry()
    {
        entries = new Dictionary<FileSystemWatcherKey, FileSystemWatcherValue>();
        pending = new HashSet<WatcherID>();
    }

    // watch.go:50-64 — Acquire increments the ref count for a watcher. If this is the first
    // reference (count goes from 0 to 1), it returns true so the caller knows
    // to register the watcher with the client.
    internal bool Acquire(FileSystemWatcher watcher, WatcherID id)
    {
        lock (mu)
        {
            FileSystemWatcherKey key = WatchUtil.ToFileSystemWatcherKey(watcher);
            if (!entries.TryGetValue(key, out FileSystemWatcherValue? value))
            {
                value = new FileSystemWatcherValue { id = id };
                entries[key] = value;
            }
            value.count++;
            return value.count == 1;
        }
    }

    // watch.go:66-83 — Release decrements the ref count for a watcher. If no references
    // remain, the entry is removed and the function returns the WatcherID and true so
    // the caller knows to unregister the watcher from the client.
    internal (WatcherID Id, bool Removed) Release(FileSystemWatcher watcher)
    {
        lock (mu)
        {
            FileSystemWatcherKey key = WatchUtil.ToFileSystemWatcherKey(watcher);
            if (!entries.TryGetValue(key, out FileSystemWatcherValue? value))
            {
                return (default, false);
            }
            if (value.count <= 1)
            {
                entries.Remove(key);
                return (value.id, true);
            }
            value.count--;
            return (default, false);
        }
    }

    // watch.go:85-90 — MarkPending records that a watcher's registration failed and needs retry.
    internal void MarkPending(WatcherID id)
    {
        lock (mu)
        {
            pending.Add(id);
        }
    }

    // watch.go:92-97 — ClearPending removes a watcher from the pending set after successful registration.
    internal void ClearPending(WatcherID id)
    {
        lock (mu)
        {
            pending.Remove(id);
        }
    }

    // watch.go:99-105 — IsPending returns true if the watcher needs retry due to a previous failure.
    internal bool IsPending(WatcherID id)
    {
        lock (mu)
        {
            return pending.Contains(id);
        }
    }
}

// watch.go:107-111 — PatternsAndIgnored.
public sealed class PatternsAndIgnored
{
    internal string[] directoriesOutsideWorkspace = [];
    internal string[] patternsInsideWorkspace = [];
    internal HashSet<string>? ignored;
}

// watch.go:113-137 — toFileSystemWatcherKey: produces a deduplication key for a file system
// watcher. Note: this key is a simple string concatenation of the base and pattern, so
// structurally different watchers (Pattern vs RelativePattern, URI vs WorkspaceFolder)
// could theoretically collide. In practice, workspace watchers use plain Pattern
// with filesystem paths while outside-workspace watchers use RelativePattern with
// file:// URIs, so collisions don't occur.
internal static class WatchUtil
{
    internal static FileSystemWatcherKey ToFileSystemWatcherKey(FileSystemWatcher w)
    {
        WatchKind kind = w.Kind ?? (WatchKind.Create | WatchKind.Change | WatchKind.Delete);
        string pattern;
        if (w.GlobPattern.Pattern is not null)
        {
            pattern = w.GlobPattern.Pattern;
        }
        else if (w.GlobPattern.RelativePattern is not null)
        {
            string baseUri;
            if (w.GlobPattern.RelativePattern.BaseUri.URI is { } uri)
            {
                baseUri = uri.Value!;
            }
            else if (w.GlobPattern.RelativePattern.BaseUri.WorkspaceFolder is not null)
            {
                throw new InvalidOperationException("workspace folder-based relative patterns not implemented");
            }
            else
            {
                baseUri = "";
            }
            pattern = baseUri + "/" + w.GlobPattern.RelativePattern.Pattern;
        }
        else
        {
            pattern = "";
        }
        return new FileSystemWatcherKey(pattern, kind);
    }

    // watch.go:139-153 — fileSystemWatcherGlobString.
    internal static string FileSystemWatcherGlobString(FileSystemWatcher w)
    {
        if (w.GlobPattern.Pattern is not null)
        {
            return w.GlobPattern.Pattern;
        }
        if (w.GlobPattern.RelativePattern is not null)
        {
            string baseUri;
            if (w.GlobPattern.RelativePattern.BaseUri.URI is { } uri)
            {
                baseUri = uri.Value!;
            }
            else if (w.GlobPattern.RelativePattern.BaseUri.WorkspaceFolder is not null)
            {
                throw new InvalidOperationException("workspace folder-based relative patterns not implemented");
            }
            else
            {
                baseUri = "";
            }
            return baseUri + "/" + w.GlobPattern.RelativePattern.Pattern;
        }
        return "";
    }

    // watch.go:431-433 — getRecursiveGlobPattern.
    internal static string GetRecursiveGlobPattern(string directory)
    {
        // Go: fmt.Sprintf("%s/%s", ...) — string.Format mirror; the `$"{global::…}/**/*"
        // interpolation form trips Roslyn's CS0103 here.
        return string.Format("{0}/{1}", global::Ts.TsPath.TsPath.RemoveTrailingDirectorySeparator(directory), "**/*");
    }

    // watch.go:435-442 — recursiveDirectoryGlobPattern: returns the string form of a
    // recursive watcher for the given directory that would be produced by
    // newRecursiveDirectoryWatcher.
    internal static string RecursiveDirectoryGlobPattern(string directory, bool useRelativePattern)
    {
        if (useRelativePattern)
        {
            return global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(directory).Value + "/**/*";
        }
        return GetRecursiveGlobPattern(directory);
    }

    // watch.go:444-469 — newRecursiveDirectoryWatcher: creates a FileSystemWatcher for
    // recursively watching a directory. When useRelativePattern is true, a RelativePattern
    // with a file:// base URI is used; otherwise a plain glob Pattern is used.
    internal static FileSystemWatcher NewRecursiveDirectoryWatcher(string directory, WatchKind kind, bool useRelativePattern)
    {
        if (useRelativePattern)
        {
            global::Ts.LsProto.DocumentUri baseUri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(directory);
            return new FileSystemWatcher
            {
                GlobPattern = new GlobPattern
                {
                    RelativePattern = new RelativePattern
                    {
                        BaseUri = new WorkspaceFolderOrURI
                        {
                            URI = baseUri,
                        },
                        Pattern = "**/*",
                    },
                },
                Kind = kind,
            };
        }
        string glob = GetRecursiveGlobPattern(directory);
        return new FileSystemWatcher
        {
            GlobPattern = new GlobPattern
            {
                Pattern = glob,
            },
            Kind = kind,
        };
    }

    // watch.go:397-405 — getPathComponentsForWatching.
    internal static List<string> GetPathComponentsForWatching(string path, string currentDirectory)
    {
        List<string> components = global::Ts.TsPath.TsPath.GetPathComponents(path, currentDirectory);
        int rootLength = PerceivedOsRootLengthForWatching(components);
        if (rootLength <= 1)
        {
            return components;
        }
        string newRoot = global::Ts.TsPath.TsPath.CombinePaths(components[0], components.GetRange(1, rootLength - 1).ToArray());
        var result = new List<string>(components.Count - rootLength + 1)
        {
            newRoot,
        };
        for (int i = rootLength; i < components.Count; i++)
        {
            result.Add(components[i]);
        }
        return result;
    }

    // watch.go:407-429 — perceivedOsRootLengthForWatching.
    internal static int PerceivedOsRootLengthForWatching(List<string> pathComponents)
    {
        int length = pathComponents.Count;
        if (length <= 1)
        {
            return length;
        }
        if (pathComponents[0].StartsWith("//", StringComparison.Ordinal))
        {
            // Group UNC roots (//server/share) into a single component
            return 2;
        }
        if (pathComponents[0].Length == 3 && global::Ts.TsPath.TsPath.IsVolumeCharacter(pathComponents[0][0]) && pathComponents[0][1] == ':' && pathComponents[0][2] == '/')
        {
            // Windows-style volume
            if (string.Equals(pathComponents[1], "users", StringComparison.OrdinalIgnoreCase))
            {
                // Group C:/Users/username into a single component
                return Math.Min(3, length);
            }
            return 1;
        }
        if (pathComponents[1] == "home")
        {
            // Group /home/username into a single component
            return Math.Min(3, length);
        }
        return 1;
    }

    // slices.Compact(slices.Sorted(...)) — ordinal sort then adjacent dedupe.
    internal static string[] SortedCompact(IEnumerable<string> values)
    {
        string[] sorted = values.ToArray();
        Array.Sort(sorted, StringComparer.Ordinal);
        if (sorted.Length == 0)
        {
            return sorted;
        }
        var result = new List<string>(sorted.Length) { sorted[0] };
        for (int i = 1; i < sorted.Length; i++)
        {
            if (!string.Equals(sorted[i], result[result.Count - 1], StringComparison.Ordinal))
            {
                result.Add(sorted[i]);
            }
        }
        return result.ToArray();
    }

    // slices.EqualFunc — element-wise comparison.
    internal static bool EqualFunc<TA, TB>(IReadOnlyList<TA> a, IReadOnlyList<TB> b, Func<TA, TB, bool> eq)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!eq(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }
}

// watch.go:155-157 — WatcherID + watcherID counter.
public readonly record struct WatcherID(string Value)
{
    public override string ToString() => Value ?? "";
}

internal static class WatcherIdCounter
{
    // Go: var watcherID atomic.Uint64 → Interlocked on a long (05-conventions.md §14).
    internal static long watcherID;

    internal static ulong Next()
    {
        long value = Interlocked.Increment(ref watcherID);
        return unchecked((ulong)value);
    }
}

// watch.go:159-172 — WatchedFiles.
public sealed class WatchedFiles<T> where T : notnull
{
    private readonly string name;
    private readonly WatchKind watchKind;
    private readonly bool hasRelativePatternCapability;
    private readonly Func<T, PatternsAndIgnored> computeGlobPatterns;

    private readonly ReaderWriterLockSlim mu = new();
    private T input = default!;
    private readonly object computeWatchersOnce = new();
    private bool watchersComputed;
    private FileSystemWatcher[] workspaceWatchers = [];
    private FileSystemWatcher[] outsideWorkspaceWatchers = [];
    private HashSet<string>? ignored;
    private ulong id;

    // watch.go:174-182 — NewWatchedFiles.
    public static WatchedFiles<T> NewWatchedFiles(string name, WatchKind watchKind, bool hasRelativePatternCapability, Func<T, PatternsAndIgnored> computeGlobPatterns)
    {
        return new WatchedFiles<T>(name, watchKind, hasRelativePatternCapability, computeGlobPatterns);
    }

    private WatchedFiles(string name, WatchKind watchKind, bool hasRelativePatternCapability, Func<T, PatternsAndIgnored> computeGlobPatterns)
    {
        id = WatcherIdCounter.Next();
        this.name = name;
        this.watchKind = watchKind;
        this.hasRelativePatternCapability = hasRelativePatternCapability;
        this.computeGlobPatterns = computeGlobPatterns;
    }

    // watch.go:184-189 — Watchers.
    public struct Watchers
    {
        public WatcherID WatcherID;
        public FileSystemWatcher[] WorkspaceWatchers;
        public FileSystemWatcher[] OutsideWorkspaceWatchers;
        public HashSet<string>? IgnoredPaths;
    }

    // watch.go:191-237 — Watchers.
    public Watchers GetWatchers()
    {
        if (!watchersComputed)
        {
            lock (computeWatchersOnce)
            {
                if (!watchersComputed)
                {
                    mu.EnterWriteLock();
                    try
                    {
                        PatternsAndIgnored result = computeGlobPatterns(input);
                        string[] globs = WatchUtil.SortedCompact(result.patternsInsideWorkspace);

                        HashSet<string>? ignoredResult = result.ignored;
                        // ignored is only used for logging and doesn't affect watcher identity
                        ignored = ignoredResult;
                        bool changed = false;
                        if (!WatchUtil.EqualFunc<FileSystemWatcher, string>(workspaceWatchers, globs, (a, b) => a.GlobPattern.Pattern == b))
                        {
                            workspaceWatchers = Array.ConvertAll(globs, glob => new FileSystemWatcher
                            {
                                GlobPattern = new GlobPattern
                                {
                                    Pattern = glob,
                                },
                                Kind = watchKind,
                            });
                            changed = true;
                        }
                        string[] dirsOutside = WatchUtil.SortedCompact(result.directoriesOutsideWorkspace);
                        if (!WatchUtil.EqualFunc<FileSystemWatcher, string>(outsideWorkspaceWatchers, dirsOutside, (a, b) => WatchUtil.FileSystemWatcherGlobString(a) == WatchUtil.RecursiveDirectoryGlobPattern(b, hasRelativePatternCapability)))
                        {
                            outsideWorkspaceWatchers = Array.ConvertAll(dirsOutside, dir => WatchUtil.NewRecursiveDirectoryWatcher(dir, watchKind, hasRelativePatternCapability));
                            changed = true;
                        }
                        if (changed)
                        {
                            id = WatcherIdCounter.Next();
                        }
                        watchersComputed = true;
                    }
                    finally
                    {
                        mu.ExitWriteLock();
                    }
                }
            }
        }

        mu.EnterReadLock();
        try
        {
            return new Watchers
            {
                WatcherID = new WatcherID($"{name} watcher {id}"),
                WorkspaceWatchers = workspaceWatchers,
                OutsideWorkspaceWatchers = outsideWorkspaceWatchers,
                IgnoredPaths = ignored,
            };
        }
        finally
        {
            mu.ExitReadLock();
        }
    }

    // watch.go:239-244 — ID. (Go tolerates a nil receiver; the nullable extension form
    // WatchedFilesExtensions.ID is the only ported surface so nullable call sites keep
    // Go's nil tolerance instead of a CS8602 dereference.)
    //
    // watch.go:246-248 — Name.
    public string Name() => name;

    // watch.go:250-252 — WatchKind.
    public WatchKind WatchKind() => watchKind;

    // watch.go:254-269 — Clone.
    public WatchedFiles<T>? Clone(T input)
    {
        mu.EnterReadLock();
        try
        {
            var clone = new WatchedFiles<T>(name, watchKind, hasRelativePatternCapability, computeGlobPatterns);
            clone.workspaceWatchers = workspaceWatchers;
            clone.outsideWorkspaceWatchers = outsideWorkspaceWatchers;
            clone.input = input;
            return clone;
        }
        finally
        {
            mu.ExitReadLock();
        }
    }
}

// watch.go:239-244 — ID's nil-receiver tolerance, as an extension.
public static class WatchedFilesExtensions
{
    public static WatcherID ID<T>(this WatchedFiles<T>? w) where T : notnull
    {
        if (w is null)
        {
            return default;
        }
        return w.GetWatchers().WatcherID;
    }
}

// watch.go:271-351 — createResolutionLookupGlobMapper.
public static class WatchGlobMappers
{
    public static Func<SyncSet<global::Ts.TsPath.Path>, PatternsAndIgnored> CreateResolutionLookupGlobMapper(string workspaceDirectory, string libDirectory, string currentDirectory, bool useCaseSensitiveFileNames)
    {
        global::Ts.TsPath.Path workspaceDirectoryPath = global::Ts.TsPath.TsPath.ToPath(workspaceDirectory, currentDirectory, useCaseSensitiveFileNames);
        global::Ts.TsPath.Path currentDirectoryPath = global::Ts.TsPath.TsPath.ToPath(currentDirectory, currentDirectory, useCaseSensitiveFileNames);
        global::Ts.TsPath.Path libDirectoryPath = global::Ts.TsPath.TsPath.ToPath(libDirectory, currentDirectory, useCaseSensitiveFileNames);

        return data =>
        {
            HashSet<string>? ignored = null;
            var seenDirs = new Set<global::Ts.TsPath.Path>();
            bool includeWorkspace = false, includeRoot = false, includeLib = false;
            var nodeModulesDirectories = new Set<global::Ts.TsPath.Path>();
            var externalDirectories = new Set<global::Ts.TsPath.Path>();

            if (data is not null)
            {
                data.Range(path =>
                {
                    if (global::Ts.TsPath.TsPath.IsDynamicFileName(path.Value))
                    {
                        return true;
                    }
                    // Assuming all of the input paths are file paths, we can avoid
                    // duplicate work by only taking one file per dir, since their outputs
                    // will always be the same.
                    if (!seenDirs.AddIfAbsent(path.GetDirectoryPath()))
                    {
                        return true;
                    }

                    if (workspaceDirectoryPath.ContainsPath(path))
                    {
                        includeWorkspace = true;
                    }
                    else if (currentDirectoryPath.ContainsPath(path))
                    {
                        includeRoot = true;
                    }
                    else if (libDirectoryPath.ContainsPath(path))
                    {
                        includeLib = true;
                    }
                    else
                    {
                        int idx = path.Value.IndexOf("/node_modules/", StringComparison.Ordinal);
                        if (idx != -1)
                        {
                            nodeModulesDirectories.Add(new global::Ts.TsPath.Path(path.Value[..(idx + "/node_modules".Length)]));
                        }
                        else
                        {
                            externalDirectories.Add(path.GetDirectoryPath());
                        }
                    }
                    return true;
                });
            }

            var globs = new List<string>();
            if (includeWorkspace)
            {
                globs.Add(WatchUtil.GetRecursiveGlobPattern(workspaceDirectoryPath.Value));
            }
            if (includeRoot)
            {
                globs.Add(WatchUtil.GetRecursiveGlobPattern(currentDirectoryPath.Value));
            }
            if (includeLib)
            {
                globs.Add(WatchUtil.GetRecursiveGlobPattern(libDirectoryPath.Value));
            }
            if (nodeModulesDirectories.Len() > 0)
            {
                var nodeModulesGlobs = new List<string>(nodeModulesDirectories.Len());
                // map-iter: 已排序 — globs are sorted below to match Go's slices.Sort.
                foreach (global::Ts.TsPath.Path dir in nodeModulesDirectories.Keys())
                {
                    nodeModulesGlobs.Add(WatchUtil.GetRecursiveGlobPattern(dir.Value));
                }
                globs.AddRange(WatchUtil.SortedCompact(nodeModulesGlobs));
            }
            string[] outsideDirs = [];
            if (externalDirectories.Len() > 0)
            {
                var externalDirStrings = new List<string>(externalDirectories.Len());
                // map-iter: 已排序 — Go collects then sorts via GetCommonParents/slices.Sort.
                foreach (global::Ts.TsPath.Path dir in externalDirectories.Keys())
                {
                    externalDirStrings.Add(dir.Value);
                }
                (string[]? externalDirectoryParents, HashSet<string>? ignoredExternalDirs) = global::Ts.TsPath.TsPath.GetCommonParents(
                    externalDirStrings.ToArray(),
                    WatchConstants.MinWatchLocationDepth,
                    WatchUtil.GetPathComponentsForWatching,
                    new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true }); // Already using tspath.Path
                outsideDirs = externalDirectoryParents ?? [];
                ignored = ignoredExternalDirs;
            }

            return new PatternsAndIgnored
            {
                directoriesOutsideWorkspace = outsideDirs,
                patternsInsideWorkspace = globs.ToArray(),
                ignored = ignored,
            };
        };
    }

    // watch.go:353-395 — getTypingsLocationsGlobs.
    public static PatternsAndIgnored GetTypingsLocationsGlobs(
        string[] typingsFiles,
        string typingsLocation,
        string workspaceDirectory,
        string currentDirectory,
        bool useCaseSensitiveFileNames)
    {
        bool includeTypingsLocation = false, includeWorkspace = false;
        var externalDirectories = new Dictionary<global::Ts.TsPath.Path, string>();
        var globs = new Dictionary<global::Ts.TsPath.Path, string>();
        var comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
        {
            CurrentDirectory = currentDirectory,
            UseCaseSensitiveFileNames = useCaseSensitiveFileNames,
        };
        // map-iter: 序无关 — per-file classification is independent.
        foreach (string file in typingsFiles)
        {
            if (global::Ts.TsPath.TsPath.ContainsPath(typingsLocation, file, comparePathsOptions))
            {
                includeTypingsLocation = true;
            }
            else if (!global::Ts.TsPath.TsPath.ContainsPath(workspaceDirectory, file, comparePathsOptions))
            {
                string directory = global::Ts.TsPath.TsPath.GetDirectoryPath(file);
                externalDirectories[global::Ts.TsPath.TsPath.ToPath(directory, currentDirectory, useCaseSensitiveFileNames)] = directory;
            }
            else
            {
                includeWorkspace = true;
            }
        }
        (string[]? externalDirectoryParents, HashSet<string>? ignored) = global::Ts.TsPath.TsPath.GetCommonParents(
            [.. externalDirectories.Values],
            WatchConstants.MinWatchLocationDepth,
            WatchUtil.GetPathComponentsForWatching,
            comparePathsOptions);
        if (includeWorkspace)
        {
            globs[global::Ts.TsPath.TsPath.ToPath(workspaceDirectory, currentDirectory, useCaseSensitiveFileNames)] = WatchUtil.GetRecursiveGlobPattern(workspaceDirectory);
        }
        if (includeTypingsLocation)
        {
            globs[global::Ts.TsPath.TsPath.ToPath(typingsLocation, currentDirectory, useCaseSensitiveFileNames)] = WatchUtil.GetRecursiveGlobPattern(typingsLocation);
        }
        return new PatternsAndIgnored
        {
            directoriesOutsideWorkspace = externalDirectoryParents ?? [],
            // map-iter: 已排序 — Go sorts the directory parents; glob values are deduped by key.
            patternsInsideWorkspace = [.. globs.Values],
            ignored = ignored,
        };
    }
}

