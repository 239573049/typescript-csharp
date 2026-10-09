// Ported from tsgo internal/project/snapshotfs.go (whole file).

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Collections;
using Ts.Project.Dirty;
using static global::Ts.LsProto.DocumentUriExtensions;

namespace Ts.Project;

// snapshotfs.go:20-26 — FileSource.
public interface FileSource
{
    global::Ts.Vfs.IFileSystem FS();

    FileHandle? GetFile(string fileName);

    FileHandle? GetFileByPath(string fileName, global::Ts.TsPath.Path path);

    bool FileExists(string fileName, global::Ts.TsPath.Path path);

    global::Ts.Vfs.Entries GetAccessibleEntries(string path);
}

// snapshotfs.go:28-31 — var _ FileSource = ... (static interface conformance checks).
// C# enforces this at compile time via the class declarations below.

// snapshotfs.go:33-38 — realpathAliasSet: a thread-safe set of symlink paths that alias
// a single realpath. It implements dirty.Cloneable so it can be used as a value in dirty.SyncMap.
internal sealed class RealpathAliasSet : Cloneable<RealpathAliasSet>
{
    internal readonly Lock mu = new();
    internal Set<global::Ts.TsPath.Path> paths = new();

    // snapshotfs.go:40-44 — Add.
    public void Add(global::Ts.TsPath.Path path)
    {
        lock (mu)
        {
            paths.Add(path);
        }
    }

    // snapshotfs.go:46-54 — Clone.
    public RealpathAliasSet Clone()
    {
        lock (mu)
        {
            var clone = new RealpathAliasSet();
            if (paths.Len() > 0)
            {
                clone.paths = paths.Clone()!;
            }
            return clone;
        }
    }
}

// snapshotfs.go:56-68 — SnapshotFS.
public sealed class SnapshotFS : FileSource
{
    private readonly Func<string, global::Ts.TsPath.Path> toPath;
    internal readonly global::Ts.Vfs.IFileSystem fs;
    internal readonly Dictionary<global::Ts.TsPath.Path, Overlay> overlays;
    internal readonly Dictionary<global::Ts.TsPath.Path, Dictionary<global::Ts.TsPath.Path, string>> overlayDirectories;
    internal readonly Dictionary<global::Ts.TsPath.Path, DiskFile>? diskFiles;
    internal readonly Dictionary<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>? diskDirectories;
    internal readonly global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, Lazy<FileHandle?>> readFiles = new();
    // nodeModulesRealpathAliases maps realpath-based keys to sets of symlink-based keys,
    // for files inside node_modules that are accessed through directory symlinks.
    // This allows watch events (which use realpaths) to invalidate files cached under symlink paths.
    internal readonly Dictionary<global::Ts.TsPath.Path, RealpathAliasSet>? nodeModulesRealpathAliases;

    // Cross-type accessors (Go: same-package field access; C#: internal properties).
    internal Dictionary<global::Ts.TsPath.Path, Overlay> Overlays => overlays;
    internal Dictionary<global::Ts.TsPath.Path, DiskFile>? DiskFiles => diskFiles;
    internal Dictionary<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>? DiskDirectories => diskDirectories;
    internal Dictionary<global::Ts.TsPath.Path, RealpathAliasSet>? NodeModulesRealpathAliases => nodeModulesRealpathAliases;

    internal SnapshotFS(
        Func<string, global::Ts.TsPath.Path> toPath,
        global::Ts.Vfs.IFileSystem fs,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        Dictionary<global::Ts.TsPath.Path, Dictionary<global::Ts.TsPath.Path, string>> overlayDirectories,
        Dictionary<global::Ts.TsPath.Path, DiskFile>? diskFiles,
        Dictionary<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>? diskDirectories,
        Dictionary<global::Ts.TsPath.Path, RealpathAliasSet>? nodeModulesRealpathAliases)
    {
        this.toPath = toPath;
        this.fs = fs;
        this.overlays = overlays;
        this.overlayDirectories = overlayDirectories;
        this.diskFiles = diskFiles;
        this.diskDirectories = diskDirectories;
        this.nodeModulesRealpathAliases = nodeModulesRealpathAliases;
    }

    // snapshotfs.go:72-74 — FS.
    public global::Ts.Vfs.IFileSystem FS() => fs;

    // snapshotfs.go:76-78 — GetFile.
    public FileHandle? GetFile(string fileName) => GetFileByPath(fileName, toPath(fileName));

    // snapshotfs.go:80-88 — FileExists.
    public bool FileExists(string fileName, global::Ts.TsPath.Path path)
    {
        if (overlays.ContainsKey(path))
        {
            return true;
        }
        if (diskFiles is not null && diskFiles.ContainsKey(path))
        {
            return true;
        }
        return fs.FileExists(fileName);
    }

    // snapshotfs.go:90-105 — GetFileByPath.
    public FileHandle? GetFileByPath(string fileName, global::Ts.TsPath.Path path)
    {
        if (overlays.TryGetValue(path, out Overlay? file))
        {
            return file;
        }
        if (diskFiles is not null && diskFiles.TryGetValue(path, out DiskFile? diskFile))
        {
            return diskFile;
        }
        // Go: memoizedDiskFile(sync.OnceValue(...)) → Lazy with execution-and-publication
        // semantics (evaluated at most once, thread-safely, exceptions cached).
        var newEntry = new Lazy<FileHandle?>(() =>
        {
            if (fs.TryReadFile(fileName, out string contents))
            {
                return DiskFile.NewDiskFile(fileName, contents);
            }
            return null;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        (Lazy<FileHandle?> entry, bool _) = readFiles.LoadOrStore(path, newEntry);
        return entry.Value;
    }

    // snapshotfs.go:107-117 — GetAccessibleEntries.
    public global::Ts.Vfs.Entries GetAccessibleEntries(string directoryName)
    {
        global::Ts.TsPath.Path path = toPath(directoryName);
        var files = new List<string>();
        var directories = new List<string>();
        if (diskDirectories is not null && diskDirectories.TryGetValue(path, out var diskDirectoriesEntry))
        {
            SnapshotFsUtil.ReadDirectoryIntoEntries(diskDirectoriesEntry, IsFile, files, directories);
        }
        if (overlayDirectories.TryGetValue(path, out var overlayDirectoriesEntry))
        {
            SnapshotFsUtil.ReadDirectoryIntoEntries(overlayDirectoriesEntry, IsFile, files, directories);
        }
        return new global::Ts.Vfs.Entries
        {
            Files = files.Count > 0 ? files.ToArray() : null,
            Directories = directories.Count > 0 ? directories.ToArray() : null,
        };
    }

    // snapshotfs.go:119-123 — isOpenFile.
    internal bool IsOpenFile(string fileName)
    {
        global::Ts.TsPath.Path path = toPath(fileName);
        return overlays.ContainsKey(path);
    }

    // snapshotfs.go:125-133 — isFile.
    private bool IsFile(global::Ts.TsPath.Path path)
    {
        if (diskFiles is not null && diskFiles.ContainsKey(path))
        {
            return true;
        }
        if (overlays.ContainsKey(path))
        {
            return true;
        }
        return false;
    }

    // snapshotfs.go:497-533 — expandRealpathAliases: adds synthetic URIs to the Changed and
    // Deleted sets for files that were accessed through node_modules symlinks. When a watch
    // event arrives using a realpath, this expands it to include the symlink-based path so
    // that downstream consumers (markDirtyFiles, markFilesChanged) can find cached entries.
    internal FileChangeSummary ExpandRealpathAliases(FileChangeSummary change)
    {
        if (nodeModulesRealpathAliases is null || nodeModulesRealpathAliases.Count == 0)
        {
            return change;
        }

        var additionalChanged = new Set<global::Ts.LsProto.DocumentUri>();
        foreach (global::Ts.LsProto.DocumentUri uri in change.Changed.Keys())
        {
            global::Ts.TsPath.Path path = toPath(uri.FileName());
            if (nodeModulesRealpathAliases.TryGetValue(path, out RealpathAliasSet? aliases))
            {
                foreach (global::Ts.TsPath.Path aliasPath in aliases.paths.Keys())
                {
                    additionalChanged.Add(global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aliasPath.Value));
                }
            }
        }
        foreach (global::Ts.LsProto.DocumentUri uri in additionalChanged.Keys())
        {
            change.Changed.Add(uri);
        }

        var additionalDeleted = new Set<global::Ts.LsProto.DocumentUri>();
        foreach (global::Ts.LsProto.DocumentUri uri in change.Deleted.Keys())
        {
            global::Ts.TsPath.Path path = toPath(uri.FileName());
            if (nodeModulesRealpathAliases.TryGetValue(path, out RealpathAliasSet? aliases))
            {
                foreach (global::Ts.TsPath.Path aliasPath in aliases.paths.Keys())
                {
                    additionalDeleted.Add(global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(aliasPath.Value));
                }
            }
        }
        foreach (global::Ts.LsProto.DocumentUri uri in additionalDeleted.Keys())
        {
            change.Deleted.Add(uri);
        }

        return change;
    }
}

// snapshotfs.go:70 — memoizedDiskFile is mapped to Lazy<FileHandle?> (sync.OnceValue).

// snapshotfs.go:135-145 — snapshotFSBuilder.
public sealed class SnapshotFSBuilder : FileSource
{
    internal readonly global::Ts.Vfs.IFileSystem fs;
    internal readonly Dictionary<global::Ts.TsPath.Path, Overlay> prevOverlays;
    internal readonly Dictionary<global::Ts.TsPath.Path, Overlay> overlays;
    internal readonly Dictionary<global::Ts.TsPath.Path, Dictionary<global::Ts.TsPath.Path, string>> overlayDirectories;
    internal readonly Dirty.SyncMap<global::Ts.TsPath.Path, DiskFile> diskFiles;
    internal readonly Map<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>> diskDirectories;
    internal readonly Dirty.SyncMap<global::Ts.TsPath.Path, RealpathAliasSet> nodeModulesRealpathAliases;
    internal readonly Func<string, global::Ts.TsPath.Path> toPath;
    internal readonly global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, global::Ts.Vfs.Entries> accessibleEntries = new();

    // Cross-type accessors (Go: same-package field access).
    internal Dirty.SyncMap<global::Ts.TsPath.Path, DiskFile> DiskFiles => diskFiles;

    // snapshotfs.go:147-193 — newSnapshotFSBuilder. (Go unexported → internal; also keeps
    // the internal RealpathAliasSet parameter legal per CS0051.)
    internal static SnapshotFSBuilder NewSnapshotFSBuilder(
        global::Ts.Vfs.IFileSystem fs,
        Dictionary<global::Ts.TsPath.Path, Overlay> prevOverlays,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        Dictionary<global::Ts.TsPath.Path, DiskFile> diskFiles,
        Dictionary<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>> diskDirectories,
        Dictionary<global::Ts.TsPath.Path, RealpathAliasSet> nodeModulesRealpathAliases,
        string positionEncoding,
        Func<string, global::Ts.TsPath.Path> toPath)
    {
        global::Ts.Vfs.CachedVfs.CachedFs cachedFS = global::Ts.Vfs.CachedVfs.CachedFs.From(fs);
        cachedFS.Enable();

        var overlayDirectories = new Dictionary<global::Ts.TsPath.Path, Dictionary<global::Ts.TsPath.Path, string>>();
        // map-iter: 序无关 — each overlay's ancestor chain is built independently.
        foreach (global::Ts.TsPath.Path path in overlays.Keys)
        {
            global::Ts.TsPath.Path childPath = path;
            string child = overlays[path].FileName();
            while (true)
            {
                global::Ts.TsPath.Path parentPath = childPath.GetDirectoryPath();
                string parent = global::Ts.TsPath.TsPath.GetDirectoryPath(child);
                if (childPath.Equals(parentPath))
                {
                    break; // reached root
                }
                string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(child);
                if (overlayDirectories.TryGetValue(parentPath, out Dictionary<global::Ts.TsPath.Path, string>? dir))
                {
                    dir[childPath] = baseName;
                }
                else
                {
                    var newDir = new Dictionary<global::Ts.TsPath.Path, string>
                    {
                        [childPath] = baseName,
                    };
                    overlayDirectories[parentPath] = newDir;
                }
                childPath = parentPath;
                child = parent;
            }
        }

        _ = positionEncoding;
        return new SnapshotFSBuilder(
            cachedFS,
            prevOverlays,
            overlays,
            overlayDirectories,
            new Dirty.SyncMap<global::Ts.TsPath.Path, DiskFile>(diskFiles),
            new Map<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>(diskDirectories),
            new Dirty.SyncMap<global::Ts.TsPath.Path, RealpathAliasSet>(nodeModulesRealpathAliases),
            toPath);
    }

    private SnapshotFSBuilder(
        global::Ts.Vfs.IFileSystem fs,
        Dictionary<global::Ts.TsPath.Path, Overlay> prevOverlays,
        Dictionary<global::Ts.TsPath.Path, Overlay> overlays,
        Dictionary<global::Ts.TsPath.Path, Dictionary<global::Ts.TsPath.Path, string>> overlayDirectories,
        Dirty.SyncMap<global::Ts.TsPath.Path, DiskFile> diskFiles,
        Map<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>> diskDirectories,
        Dirty.SyncMap<global::Ts.TsPath.Path, RealpathAliasSet> nodeModulesRealpathAliases,
        Func<string, global::Ts.TsPath.Path> toPath)
    {
        this.fs = fs;
        this.prevOverlays = prevOverlays;
        this.overlays = overlays;
        this.overlayDirectories = overlayDirectories;
        this.diskFiles = diskFiles;
        this.diskDirectories = diskDirectories;
        this.nodeModulesRealpathAliases = nodeModulesRealpathAliases;
        this.toPath = toPath;
    }

    // snapshotfs.go:195-197 — FS.
    public global::Ts.Vfs.IFileSystem FS() => fs;

    // snapshotfs.go:199-288 — Finalize.
    public (SnapshotFS SnapshotFS, bool Changed) Finalize()
    {
        // Synchronize directory structure based on added and deleted files (including overlays)
        Dictionary<global::Ts.TsPath.Path, DiskFile>? deleted = null;

        void OnAddedFile(global::Ts.TsPath.Path path, string fileName)
        {
            global::Ts.TsPath.Path childPath = path;
            string child = fileName;
            while (true)
            {
                global::Ts.TsPath.Path parentPath = childPath.GetDirectoryPath();
                string parent = global::Ts.TsPath.TsPath.GetDirectoryPath(child);
                if (childPath.Equals(parentPath))
                {
                    break; // reached root
                }
                string baseName = global::Ts.TsPath.TsPath.GetBaseFileName(child);
                (Dirty.MapEntry<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>? dirEntry, bool ok) = diskDirectories.Get(parentPath);
                if (ok)
                {
                    dirEntry!.Change(dir => dir[childPath] = baseName);
                    break;
                }
                else
                {
                    var dir = new CloneableMap<global::Ts.TsPath.Path, string>
                    {
                        [childPath] = baseName,
                    };
                    diskDirectories.Add(parentPath, dir);
                }
                childPath = parentPath;
                child = parent;
            }
        }

        void OnDeletedFileOrDirectory(global::Ts.TsPath.Path path)
        {
            (Dirty.MapEntry<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>? dirEntry, bool ok) = diskDirectories.Get(path.GetDirectoryPath());
            if (!ok)
            {
                return;
            }
            dirEntry!.Change(dir =>
            {
                dir.Remove(path);
                if (dir.Count == 0)
                {
                    dirEntry.Delete();
                    OnDeletedFileOrDirectory(dirEntry.Key());
                }
            });
        }

        (Dictionary<global::Ts.TsPath.Path, DiskFile>? diskFilesResult, bool changed) = diskFiles.FinalizeWith(new FinalizationHooks<global::Ts.TsPath.Path, DiskFile>
        {
            OnDelete = (key, value) =>
            {
                deleted ??= new Dictionary<global::Ts.TsPath.Path, DiskFile>();
                deleted[key] = value;
            },
            OnAdd = (key, value) => OnAddedFile(key, value.FileName()),
        });

        if (deleted is not null)
        {
            // map-iter: 序无关 — each deleted path prunes its own directory entry; the
            // per-parent empty-dir cascades do not interact across paths.
            foreach (global::Ts.TsPath.Path path in deleted.Keys)
            {
                OnDeletedFileOrDirectory(path);
            }
        }

        // Prune deleted symlink paths from realpath alias sets before finalizing,
        // so that empty sets are dropped during finalization.
        if (deleted is not null)
        {
            // map-iter: 序无关 — per-path alias-set pruning.
            foreach ((global::Ts.TsPath.Path deletedPath, DiskFile deletedFile) in deleted)
            {
                if (string.IsNullOrEmpty(deletedFile.realpathPath.Value))
                {
                    continue;
                }
                (Dirty.SyncMapEntry<global::Ts.TsPath.Path, RealpathAliasSet>? entry, bool ok) = nodeModulesRealpathAliases.Load(deletedFile.realpathPath);
                if (ok)
                {
                    entry!.Locked(e =>
                    {
                        e.Change(aliasSet => aliasSet.paths.Delete(deletedPath));
                        if (e.Value().paths.Len() == 0)
                        {
                            e.Delete();
                        }
                    });
                }
            }
        }

        (Dictionary<global::Ts.TsPath.Path, RealpathAliasSet>? nodeModulesRealpathAliasesResult, bool aliasesChanged) = nodeModulesRealpathAliases.Finalize();

        return (new SnapshotFS(
            toPath,
            fs,
            overlays,
            overlayDirectories,
            diskFilesResult,
            diskDirectories.Finalize().Result,
            nodeModulesRealpathAliasesResult), changed || aliasesChanged);
    }

    // snapshotfs.go:290-293 — isOpenFile.
    internal bool IsOpenFile(global::Ts.TsPath.Path path) => overlays.ContainsKey(path);

    // snapshotfs.go:295-298 — GetFile.
    public FileHandle? GetFile(string fileName)
    {
        global::Ts.TsPath.Path path = toPath(fileName);
        return GetFileByPath(fileName, path);
    }

    // snapshotfs.go:300-314 — FileExists.
    public bool FileExists(string fileName, global::Ts.TsPath.Path path)
    {
        if (overlays.ContainsKey(path))
        {
            return true;
        }
        (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? entry, bool ok) = diskFiles.Load(path);
        if (ok)
        {
            DiskFile? val = entry!.Value();
            if (val is null)
            {
                return false;
            }
            // Entry may be dirty - reload to check current state on disk.
            return ReloadEntryIfNeeded(entry!) is not null;
        }
        // Path never loaded into diskFiles - use cached stat (no file read).
        return fs.FileExists(fileName);
    }

    // snapshotfs.go:316-321 — GetFileByPath.
    public FileHandle? GetFileByPath(string fileName, global::Ts.TsPath.Path path)
    {
        if (overlays.TryGetValue(path, out Overlay? file))
        {
            return file;
        }
        return GetDiskFile(fileName, path, false);
    }

    // snapshotfs.go:323-342 — GetAccessibleEntries.
    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        global::Ts.Vfs.Entries entries = fs.GetAccessibleEntries(path);
        global::Ts.TsPath.Path p = toPath(path);
        if (!overlayDirectories.TryGetValue(p, out var overlayDirectoriesEntry))
        {
            return entries;
        }

        if (accessibleEntries.TryLoad(p, out var mergedExisting))
        {
            return mergedExisting;
        }
        var files = new List<string>(entries.Files ?? []);
        var directories = new List<string>(entries.Directories ?? []);
        SnapshotFsUtil.ReadDirectoryIntoEntries(overlayDirectoriesEntry, IsOpenFile, files, directories);
        var merged = new global::Ts.Vfs.Entries
        {
            Files = files.ToArray(),
            Directories = directories.ToArray(),
            Symlinks = entries.Symlinks,
        };
        (global::Ts.Vfs.Entries stored, bool _) = accessibleEntries.LoadOrStore(p, merged);
        return stored;
    }

    // snapshotfs.go:344-356 — getDiskFile.
    internal FileHandle? GetDiskFile(string fileName, global::Ts.TsPath.Path path, bool forceReload)
    {
        (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? entry, bool loaded) = diskFiles.LoadOrStore(path, new DiskFile { fileName = fileName, needsReload = true });
        if (entry is not null)
        {
            if (!loaded && path.Value.Contains("/node_modules/", StringComparison.Ordinal))
            {
                RecordRealpathAlias(entry, fileName, path);
            }
            if (forceReload)
            {
                return ReloadEntry(entry);
            }
            return ReloadEntryIfNeeded(entry);
        }
        return null;
    }

    // snapshotfs.go:358-373 — recordRealpathAlias: checks if fileName is accessed through a
    // symlink and, if so, records a mapping from the realpath-based key to the symlink-based
    // key. This is only called for files inside node_modules where symlinks are common.
    private void RecordRealpathAlias(Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile> diskFileEntry, string symlinkFileName, global::Ts.TsPath.Path symlinkPath)
    {
        string realpath = fs.Realpath(symlinkFileName);
        global::Ts.TsPath.Path realpathPath = toPath(realpath);
        if (!realpathPath.Equals(symlinkPath))
        {
            diskFileEntry.Change(file => file.realpathPath = realpathPath);
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, RealpathAliasSet>? entry, bool _) = nodeModulesRealpathAliases.LoadOrStore(realpathPath, new RealpathAliasSet());
            entry!.Change(aliasSet => aliasSet.Add(symlinkPath));
        }
    }

    // snapshotfs.go:375-405 — reloadEntry.
    internal FileHandle? ReloadEntry(Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile> entry)
    {
        string fileName = "";
        entry.Locked(e =>
        {
            if (e.Value() is not null)
            {
                fileName = e.Value()!.fileName;
            }
        });
        if (fileName.Length == 0)
        {
            return null;
        }
        // Read file outside the lock to avoid blocking other goroutines.
        bool ok = fs.TryReadFile(fileName, out string content);
        entry.Locked(e =>
        {
            if (e.Value() is null)
            {
                return;
            }
            if (ok)
            {
                e.Change(file =>
                {
                    file.content = content;
                    file.hash = SnapshotUtil.HashString128(content);
                    file.needsReload = false;
                });
            }
            else
            {
                e.Delete();
            }
        });
        if (entry.Value() is null)
        {
            return null;
        }
        return entry.Value();
    }

    // snapshotfs.go:407-436 — reloadEntryIfNeeded.
    internal FileHandle? ReloadEntryIfNeeded(Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile> entry)
    {
        string fileName = "";
        entry.Locked(e =>
        {
            // Go: !e.Value().MatchesDiskText() (snapshotfs.go:410) — only reload entries
            // whose cached text is stale (needsReload); the fresh placeholder must read.
            if (e.Value() is not null && !e.Value()!.MatchesDiskText())
            {
                fileName = e.Value()!.fileName;
            }
        });
        if (fileName.Length != 0)
        {
            // Read file outside the lock to avoid blocking other goroutines.
            bool ok = fs.TryReadFile(fileName, out string content);
            entry.Locked(e =>
            {
                if (e.Value() is null || e.Value()!.MatchesDiskText())
                {
                    return; // another goroutine already reloaded it
                }
                if (ok)
                {
                    e.Change(file =>
                    {
                        file.content = content;
                        file.hash = SnapshotUtil.HashString128(content);
                        file.needsReload = false;
                    });
                }
                else
                {
                    e.Delete();
                }
            });
        }
        if (entry.Value() is null)
        {
            return null;
        }
        return entry.Value();
    }

    // snapshotfs.go:438-458 — watchChangesOverlapCache.
    internal bool WatchChangesOverlapCache(FileChangeSummary change)
    {
        foreach (global::Ts.LsProto.DocumentUri uri in change.Changed.Keys())
        {
            global::Ts.TsPath.Path path = toPath(uri.FileName());
            if (diskFiles.Load(path).Ok)
            {
                return true;
            }
            if (nodeModulesRealpathAliases.Load(path).Ok)
            {
                return true;
            }
        }
        foreach (global::Ts.LsProto.DocumentUri uri in change.Deleted.Keys())
        {
            global::Ts.TsPath.Path path = toPath(uri.FileName());
            if (diskFiles.Load(path).Ok)
            {
                return true;
            }
            if (nodeModulesRealpathAliases.Load(path).Ok)
            {
                return true;
            }
        }
        return false;
    }

    // snapshotfs.go:460-467 — invalidateCache.
    internal void InvalidateCache()
    {
        diskFiles.Range(entry =>
        {
            entry.Change(file => file.needsReload = true);
            return true;
        });
    }

    // snapshotfs.go:469-478 — invalidateNodeModulesCache.
    internal void InvalidateNodeModulesCache()
    {
        diskFiles.Range(entry =>
        {
            if (entry.Key().Value.Contains("/node_modules/", StringComparison.Ordinal))
            {
                entry.Change(file => file.needsReload = true);
            }
            return true;
        });
    }

    // snapshotfs.go:480-495 — markDirtyFiles.
    internal void MarkDirtyFiles(FileChangeSummary change)
    {
        foreach (global::Ts.LsProto.DocumentUri uri in change.Changed.Keys())
        {
            global::Ts.TsPath.Path path = toPath(uri.FileName());
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? entry, bool ok) = diskFiles.Load(path);
            if (ok)
            {
                entry!.Change(file => file.needsReload = true);
            }
        }
        foreach (global::Ts.LsProto.DocumentUri uri in change.Deleted.Keys())
        {
            global::Ts.TsPath.Path path = toPath(uri.FileName());
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? entry, bool ok) = diskFiles.Load(path);
            if (ok)
            {
                entry!.Delete();
            }
        }
    }

    // snapshotfs.go:535-556 — isRelevantFileName: returns true if the given URI refers to a
    // file that could affect the project: it has a TypeScript-relevant extension, is a
    // dynamic (e.g. untitled) file, or is currently open as an overlay.
    private bool IsRelevantFileName(global::Ts.LsProto.DocumentUri uri)
    {
        string fileName = uri.FileName();
        if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
        {
            return true;
        }
        global::Ts.TsPath.Path path = toPath(fileName);
        if (overlays.ContainsKey(path))
        {
            return true;
        }
        int i = path.Value.LastIndexOf('.');
        if (i < 0)
        {
            return false;
        }
        switch (path.Value[i..])
        {
            case ".js":
            case ".jsx":
            case ".mjs":
            case ".cjs":
            case ".ts":
            case ".tsx":
            case ".mts":
            case ".cts":
            case ".json":
                return true;
        }
        return false;
    }

    // snapshotfs.go:558-594 — expandAndFilterWatchEvents: expands directory deletion URIs
    // into individual file deletion URIs using the cached directory structure, and filters
    // out watch events for paths that are neither known directories nor have relevant
    // file extensions.
    internal FileChangeSummary ExpandAndFilterWatchEvents(FileChangeSummary change)
    {
        if (change.Deleted.Len() > 0)
        {
            var filteredDeleted = new Set<global::Ts.LsProto.DocumentUri>();
            foreach (global::Ts.LsProto.DocumentUri uri in change.Deleted.Keys())
            {
                global::Ts.TsPath.Path path = toPath(uri.FileName());
                if (diskDirectories.Get(path).Ok)
                {
                    CollectFilesRecursive(path, filteredDeleted);
                }
                else if (IsRelevantFileName(uri) || IsNodeModulesPath(path))
                {
                    // node_modules deletions must always be preserved for auto-import registry change handlers.
                    // They won't be in diskDirectories since the registry doesn't use the snapshotFSBuilder for
                    // its file system, since we don't want to retain files read there.
                    filteredDeleted.Add(uri);
                }
            }
            change.Deleted = filteredDeleted;
        }

        if (change.Changed.Len() > 0)
        {
            var filteredChanged = new Set<global::Ts.LsProto.DocumentUri>();
            foreach (global::Ts.LsProto.DocumentUri uri in change.Changed.Keys())
            {
                if (IsRelevantFileName(uri))
                {
                    filteredChanged.Add(uri);
                }
            }
            change.Changed = filteredChanged;
        }

        // We can't filter created events because any created path could be a directory symlink
        // that includes relevant files. configFileRegistryBuilder will do check if these paths
        // are directories if they fall within a config's wildcard directories.

        return change;
    }

    // snapshotfs.go:596-602 — isNodeModulesPath: reports whether path is a node_modules
    // directory itself or lives inside one. Used to preserve node_modules watch deletions,
    // whose package files are read transiently and therefore never tracked in diskDirectories.
    private static bool IsNodeModulesPath(global::Ts.TsPath.Path path)
    {
        string s = path.Value;
        return s.EndsWith("/node_modules", StringComparison.Ordinal) || s.Contains("/node_modules/", StringComparison.Ordinal);
    }

    // snapshotfs.go:604-619 — collectFilesRecursive: recursively collects all cached file
    // URIs under the given directory path using the diskDirectories and diskFiles maps.
    private void CollectFilesRecursive(global::Ts.TsPath.Path dirPath, Set<global::Ts.LsProto.DocumentUri> files)
    {
        (Dirty.MapEntry<global::Ts.TsPath.Path, CloneableMap<global::Ts.TsPath.Path, string>>? dirEntry, bool ok) = diskDirectories.Get(dirPath);
        if (!ok)
        {
            return;
        }
        // map-iter: 序无关 — result is an unordered set of URIs.
        foreach (global::Ts.TsPath.Path childPath in dirEntry!.Value()!.Keys)
        {
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? entry, bool entryOk) = diskFiles.Load(childPath);
            if (entryOk)
            {
                DiskFile? file = entry!.Value();
                if (file is not null)
                {
                    files.Add(global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(file.FileName()));
                }
            }
            CollectFilesRecursive(childPath, files);
        }
    }

    // snapshotfs.go:621-652 — convertOpenAndCloseToChanges.
    internal FileChangeSummary ConvertOpenAndCloseToChanges(FileChangeSummary change)
    {
        if (!string.IsNullOrEmpty(change.Opened.Value) && !global::Ts.TsPath.TsPath.IsDynamicFileName(change.Opened.FileName()))
        {
            global::Ts.TsPath.Path path = toPath(change.Opened.FileName());
            (Dirty.SyncMapEntry<global::Ts.TsPath.Path, DiskFile>? entry, bool ok) = diskFiles.Load(path);
            if (!ok || entry!.Original() is null)
            {
                change.Created.Add(change.Opened);
            }
            else if (overlays.TryGetValue(path, out Overlay? overlay))
            {
                // The file already exists in the program, but the overlay content from
                // didOpen may differ from what was originally read from disk (e.g. the
                // editor normalizes line endings, or the file changed on disk since the
                // project was loaded). Mark it as Changed so the project rebuilds.
                DiskFile? diskFile = entry!.Original();
                if (diskFile is not null && overlay.Hash() != diskFile.Hash())
                {
                    change.Changed.Add(change.Opened);
                }
            }
        }
        foreach (global::Ts.LsProto.DocumentUri uri in change.Closed.Keys())
        {
            string fileName = uri.FileName();
            if (global::Ts.TsPath.TsPath.IsDynamicFileName(fileName))
            {
                continue;
            }
            global::Ts.TsPath.Path path = toPath(fileName);
            // We may have ignored watcher events while the file was open, so force a reload.
            FileHandle? fh = GetDiskFile(fileName, path, true /*forceReload*/);
            if (fh is not null)
            {
                if (!fh.Hash().Equals(prevOverlays[path].Hash()))
                {
                    change.Changed.Add(uri);
                }
                continue;
            }
            change.Deleted.Add(uri);
        }
        return change;
    }
}

// snapshotfs.go:654-661 — sourceFS is a vfs.FS that sources files from a FileSource and
// tracks seen files.
internal sealed class SourceFS : global::Ts.Vfs.IFileSystem
{
    internal bool tracking;
    internal readonly Func<string, global::Ts.TsPath.Path> toPath;
    internal SyncSet<global::Ts.TsPath.Path>? missingDirectories;
    internal SyncSet<global::Ts.TsPath.Path>? seenFiles;
    // PORT-NOTE: non-readonly — compilerHost.freeze re-points this at the finalized SnapshotFS.
    internal FileSource source;

    // snapshotfs.go:663-674 — newSourceFS.
    internal static SourceFS NewSourceFS(bool tracking, FileSource source, Func<string, global::Ts.TsPath.Path> toPath)
    {
        var fs = new SourceFS(tracking, source, toPath);
        if (tracking)
        {
            fs.seenFiles = new SyncSet<global::Ts.TsPath.Path>();
            fs.missingDirectories = new SyncSet<global::Ts.TsPath.Path>();
        }
        return fs;
    }

    private SourceFS(bool tracking, FileSource source, Func<string, global::Ts.TsPath.Path> toPath)
    {
        this.tracking = tracking;
        this.source = source;
        this.toPath = toPath;
    }

    // snapshotfs.go:678-680 — DisableTracking.
    internal void DisableTracking()
    {
        tracking = false;
    }

    // snapshotfs.go:682-687 — Track.
    internal void Track(string fileName)
    {
        if (!tracking)
        {
            return;
        }
        seenFiles!.Add(toPath(fileName));
    }

    // snapshotfs.go:689-694 — SeenFile.
    internal bool SeenFile(global::Ts.TsPath.Path path)
    {
        if (seenFiles is null)
        {
            return false;
        }
        return seenFiles.Has(path);
    }

    // snapshotfs.go:696-714 — SeenFileOrMissingParentDirectory.
    internal bool SeenFileOrMissingParentDirectory(global::Ts.TsPath.Path path)
    {
        if (seenFiles is not null && seenFiles.Has(path))
        {
            return true;
        }
        if (missingDirectories is not null && !missingDirectories.IsEmpty())
        {
            while (true)
            {
                if (missingDirectories.Has(path))
                {
                    return true;
                }

                global::Ts.TsPath.Path parent = path.GetDirectoryPath();
                if (parent.Equals(path))
                {
                    break;
                }
                path = parent;
            }
        }
        return false;
    }

    // snapshotfs.go:716-719 — GetFile.
    internal FileHandle? GetFile(string fileName)
    {
        Track(fileName);
        return source.GetFile(fileName);
    }

    // snapshotfs.go:721-724 — GetFileByPath.
    internal FileHandle? GetFileByPath(string fileName, global::Ts.TsPath.Path path)
    {
        Track(fileName);
        return source.GetFileByPath(fileName, path);
    }

    // snapshotfs.go:726-733 — DirectoryExists implements vfs.FS.
    public bool DirectoryExists(string path)
    {
        bool exists = source.FS().DirectoryExists(path);
        if (!exists && tracking)
        {
            missingDirectories!.Add(toPath(path));
        }
        return exists;
    }

    // snapshotfs.go:735-739 — FileExists implements vfs.FS.
    public bool FileExists(string path)
    {
        Track(path);
        return source.FileExists(path, toPath(path));
    }

    // snapshotfs.go:741-744 — GetAccessibleEntries implements vfs.FS.
    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        return source.GetAccessibleEntries(path);
    }

    // snapshotfs.go:746-752 — ReadFile implements vfs.FS (C# TryReadFile shape).
    public bool TryReadFile(string path, out string contents)
    {
        FileHandle? fh = GetFile(path);
        if (fh is not null)
        {
            contents = fh.Content();
            return true;
        }
        contents = "";
        return false;
    }

    // snapshotfs.go:754-757 — Realpath implements vfs.FS.
    public string Realpath(string path)
    {
        return source.FS().Realpath(path);
    }

    // snapshotfs.go:759-762 — Stat implements vfs.FS.
    public global::Ts.Vfs.IFileInfo? Stat(string path)
    {
        return source.FS().Stat(path);
    }

    // snapshotfs.go:764-767 — UseCaseSensitiveFileNames implements vfs.FS.
    public bool UseCaseSensitiveFileNames()
    {
        return source.FS().UseCaseSensitiveFileNames();
    }

    // snapshotfs.go:769-772 — WalkDir implements vfs.FS.
    public Exception? WalkDir(string root, global::Ts.Vfs.WalkDirFunc walkFn)
    {
        return source.FS().WalkDir(root, walkFn);
    }

    // snapshotfs.go:774-777 — WriteFile implements vfs.FS.
    public void WriteFile(string path, string data)
    {
        throw new InvalidOperationException("unimplemented");
    }

    // snapshotfs.go:779-782 — AppendFile implements vfs.FS.
    public void AppendFile(string path, string data)
    {
        throw new InvalidOperationException("unimplemented");
    }

    // snapshotfs.go:784-787 — Remove implements vfs.FS.
    public void Remove(string path)
    {
        throw new InvalidOperationException("unimplemented");
    }

    // snapshotfs.go:789-792 — Chtimes implements vfs.FS.
    public void Chtimes(string path, DateTime atime, DateTime mtime)
    {
        throw new InvalidOperationException("unimplemented");
    }
}

// snapshotfs.go:794-802 — readDirectoryIntoEntries. (Go's generic-over-map helper becomes
// a Dictionary overload; appends into the given accumulators so callers keep the Go
// nil-until-first-append semantics by materializing arrays only when non-empty.)
internal static class SnapshotFsUtil
{
    public static void ReadDirectoryIntoEntries(Dictionary<global::Ts.TsPath.Path, string> directories, Func<global::Ts.TsPath.Path, bool> isFile, List<string> files, List<string> dirs)
    {
        // map-iter: 序无关 — entries are appended into unordered vfs entry lists.
        foreach ((global::Ts.TsPath.Path childPath, string childName) in directories)
        {
            if (isFile(childPath))
            {
                files.Add(childName);
            }
            else
            {
                dirs.Add(childName);
            }
        }
    }
}

