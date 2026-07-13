// Foundation subset of tsgo internal/project/overlayfs.go and snapshotfs.go.
//
// Go publishes an immutable overlay map to each Snapshot. This implementation
// uses the same copy-on-write shape: ProjectService replaces one OverlayState,
// while compiler worker threads only read the published state. Open virtual
// files are also merged into accessible directory entries so tsconfig include
// expansion observes them.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project;

internal sealed class OverlayFileSystem : global::Ts.Vfs.IFileSystem
{
    private readonly global::Ts.Vfs.IFileSystem source;
    private readonly string currentDirectory;
    private OverlayState state = OverlayState.Empty;

    internal OverlayFileSystem(global::Ts.Vfs.IFileSystem source, string currentDirectory)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.currentDirectory = currentDirectory;
    }

    internal void ReplaceOpenFiles(IEnumerable<ScriptInfo> scriptInfos)
    {
        var files = new Dictionary<global::Ts.TsPath.Path, OpenFileSnapshot>();
        // map-iter: 序无关 — each canonical path independently replaces one overlay entry.
        foreach (ScriptInfo scriptInfo in scriptInfos)
        {
            OpenFileSnapshot? openFile = scriptInfo.GetOpenSnapshot();
            if (openFile is not null)
            {
                files[scriptInfo.Path] = openFile.Value;
            }
        }

        Volatile.Write(ref state, OverlayState.Create(files, currentDirectory, UseCaseSensitiveFileNames()));
    }

    internal bool TryGetOpenFile(global::Ts.TsPath.Path path, out OpenFileSnapshot file) =>
        Volatile.Read(ref state).Files.TryGetValue(path, out file);

    public bool UseCaseSensitiveFileNames() => source.UseCaseSensitiveFileNames();

    public bool FileExists(string path)
    {
        global::Ts.TsPath.Path key = ToPath(path);
        return Volatile.Read(ref state).Files.ContainsKey(key) || source.FileExists(path);
    }

    public bool TryReadFile(string path, out string contents)
    {
        global::Ts.TsPath.Path key = ToPath(path);
        if (Volatile.Read(ref state).Files.TryGetValue(key, out OpenFileSnapshot file))
        {
            contents = file.Content;
            return true;
        }

        return source.TryReadFile(path, out contents);
    }

    public void WriteFile(string path, string data) => source.WriteFile(path, data);

    public void AppendFile(string path, string data) => source.AppendFile(path, data);

    public void Remove(string path) => source.Remove(path);

    public void Chtimes(string path, DateTime aTime, DateTime mTime) => source.Chtimes(path, aTime, mTime);

    public bool DirectoryExists(string path)
    {
        global::Ts.TsPath.Path key = ToPath(path);
        return Volatile.Read(ref state).Directories.ContainsKey(key) || source.DirectoryExists(path);
    }

    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        global::Ts.Vfs.Entries diskEntries = source.GetAccessibleEntries(path);
        global::Ts.TsPath.Path directoryPath = ToPath(path);
        OverlayState snapshot = Volatile.Read(ref state);
        if (!snapshot.Directories.TryGetValue(directoryPath, out OverlayDirectoryEntry[]? overlayEntries))
        {
            return diskEntries;
        }

        var files = new List<string>(diskEntries.Files ?? []);
        var directories = new List<string>(diskEntries.Directories ?? []);
        var seen = new HashSet<global::Ts.TsPath.Path>();

        foreach (string file in files)
        {
            seen.Add(ToPath(global::Ts.TsPath.TsPath.CombinePaths(path, file)));
        }

        foreach (string directory in directories)
        {
            seen.Add(ToPath(global::Ts.TsPath.TsPath.CombinePaths(path, directory)));
        }

        foreach (OverlayDirectoryEntry entry in overlayEntries)
        {
            if (!seen.Add(entry.Path))
            {
                continue;
            }

            if (entry.IsFile)
            {
                files.Add(entry.Name);
            }
            else
            {
                directories.Add(entry.Name);
            }
        }

        return new global::Ts.Vfs.Entries
        {
            Files = files.Count == 0 ? null : files.ToArray(),
            Directories = directories.Count == 0 ? null : directories.ToArray(),
            Symlinks = diskEntries.Symlinks,
        };
    }

    // Go sourceFS delegates Stat/WalkDir/Realpath to the underlying disk FS;
    // overlays affect compiler reads and directory matching, not disk metadata.
    public global::Ts.Vfs.IFileInfo? Stat(string path) => source.Stat(path);

    public Exception? WalkDir(string root, global::Ts.Vfs.WalkDirFunc walkFn) => source.WalkDir(root, walkFn);

    public string Realpath(string path) => source.Realpath(path);

    private global::Ts.TsPath.Path ToPath(string fileName) =>
        global::Ts.TsPath.TsPath.ToPath(fileName, currentDirectory, UseCaseSensitiveFileNames());
}

internal sealed class OverlayState
{
    internal static readonly OverlayState Empty = new(
        new Dictionary<global::Ts.TsPath.Path, OpenFileSnapshot>(),
        new Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry[]>());

    private OverlayState(
        Dictionary<global::Ts.TsPath.Path, OpenFileSnapshot> files,
        Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry[]> directories)
    {
        Files = files;
        Directories = directories;
    }

    internal Dictionary<global::Ts.TsPath.Path, OpenFileSnapshot> Files { get; }

    internal Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry[]> Directories { get; }

    internal static OverlayState Create(
        Dictionary<global::Ts.TsPath.Path, OpenFileSnapshot> files,
        string currentDirectory,
        bool useCaseSensitiveFileNames)
    {
        var directories = new Dictionary<global::Ts.TsPath.Path, Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry>>();

        // map-iter: 序无关 — directory entries are sorted below before publication.
        foreach ((global::Ts.TsPath.Path filePath, OpenFileSnapshot file) in files)
        {
            global::Ts.TsPath.Path childPath = filePath;
            string childName = file.FileName;
            bool childIsFile = true;
            while (true)
            {
                global::Ts.TsPath.Path parentPath = childPath.GetDirectoryPath();
                string parentName = global::Ts.TsPath.TsPath.GetDirectoryPath(childName);
                if (parentPath == childPath)
                {
                    break;
                }

                if (!directories.TryGetValue(parentPath, out Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry>? children))
                {
                    children = new Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry>();
                    directories[parentPath] = children;
                }

                children[childPath] = new OverlayDirectoryEntry(
                    childPath,
                    global::Ts.TsPath.TsPath.GetBaseFileName(childName),
                    childIsFile);
                childPath = parentPath;
                childName = parentName;
                childIsFile = false;
            }
        }

        var publishedDirectories = new Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry[]>(directories.Count);
        // map-iter: 序无关 — outer directory keys are independent; every child array is sorted.
        foreach ((global::Ts.TsPath.Path path, Dictionary<global::Ts.TsPath.Path, OverlayDirectoryEntry> children) in directories)
        {
            var entries = new List<OverlayDirectoryEntry>(children.Values);
            entries.Sort(static (left, right) => string.CompareOrdinal(left.Path.Value, right.Path.Value));
            publishedDirectories[path] = entries.ToArray();
        }

        _ = currentDirectory;
        _ = useCaseSensitiveFileNames;
        return new OverlayState(files, publishedDirectories);
    }
}

internal readonly record struct OverlayDirectoryEntry(
    global::Ts.TsPath.Path Path,
    string Name,
    bool IsFile);
