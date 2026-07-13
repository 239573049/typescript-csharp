using System;
using System.Collections.Generic;

namespace Ts.Lsp;

// A program snapshot sees open editor buffers before the underlying file system.
// Rebuilding creates a new instance over immutable string values, so a checker never
// observes half-applied didChange edits.
internal sealed class OverlayFileSystem : global::Ts.Vfs.IFileSystem
{
    private readonly global::Ts.Vfs.IFileSystem? underlying;
    private readonly Dictionary<string, string> files;

    public OverlayFileSystem(global::Ts.Vfs.IFileSystem? underlying, IReadOnlyDictionary<string, string> files)
    {
        this.underlying = underlying;
        this.files = new Dictionary<string, string>(files, StringComparer.Ordinal);
    }

    public bool UseCaseSensitiveFileNames() => underlying?.UseCaseSensitiveFileNames() ?? true;

    public bool FileExists(string path) => files.ContainsKey(Normalize(path)) || (underlying?.FileExists(path) ?? false);

    public bool TryReadFile(string path, out string contents)
    {
        if (files.TryGetValue(Normalize(path), out var overlayContents))
        {
            contents = overlayContents;
            return true;
        }
        if (underlying is not null)
        {
            return underlying.TryReadFile(path, out contents);
        }
        contents = "";
        return false;
    }

    public void WriteFile(string path, string data) => files[Normalize(path)] = data;

    public void AppendFile(string path, string data)
    {
        var normalized = Normalize(path);
        if (TryReadFile(normalized, out var current))
        {
            files[normalized] = current + data;
        }
        else
        {
            files[normalized] = data;
        }
    }

    public void Remove(string path)
    {
        var normalized = Normalize(path);
        if (!files.Remove(normalized))
        {
            underlying?.Remove(path);
        }
    }

    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        if (!files.ContainsKey(Normalize(path)))
        {
            underlying?.Chtimes(path, aTime, mTime);
        }
    }

    public bool DirectoryExists(string path)
    {
        var normalized = Normalize(path);
        // map-iter: 序无关 — this is an existential prefix test only.
        foreach (var fileName in files.Keys)
        {
            if (string.Equals(global::Ts.TsPath.TsPath.GetDirectoryPath(fileName), normalized, StringComparison.Ordinal) ||
                fileName.StartsWith(normalized + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return underlying?.DirectoryExists(path) ?? false;
    }

    public global::Ts.Vfs.Entries GetAccessibleEntries(string path)
    {
        var normalized = Normalize(path);
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        var directoryNames = new HashSet<string>(StringComparer.Ordinal);
        if (underlying is not null)
        {
            var baseEntries = underlying.GetAccessibleEntries(path);
            foreach (var file in baseEntries.Files ?? [])
            {
                fileNames.Add(file);
            }
            foreach (var directory in baseEntries.Directories ?? [])
            {
                directoryNames.Add(directory);
            }
        }

        // map-iter: 序无关 — sets are sorted ordinally before they become observable.
        foreach (var fileName in files.Keys)
        {
            var directory = global::Ts.TsPath.TsPath.GetDirectoryPath(fileName);
            if (string.Equals(directory, normalized, StringComparison.Ordinal))
            {
                fileNames.Add(global::Ts.TsPath.TsPath.GetBaseFileName(fileName));
            }
            else if (directory.StartsWith(normalized + "/", StringComparison.Ordinal))
            {
                var relative = directory[(normalized.Length + 1)..];
                var separator = relative.IndexOf('/', StringComparison.Ordinal);
                directoryNames.Add(separator < 0 ? relative : relative[..separator]);
            }
        }

        var orderedFiles = new List<string>(fileNames);
        var orderedDirectories = new List<string>(directoryNames);
        orderedFiles.Sort(StringComparer.Ordinal);
        orderedDirectories.Sort(StringComparer.Ordinal);
        return new global::Ts.Vfs.Entries
        {
            Files = orderedFiles.ToArray(),
            Directories = orderedDirectories.ToArray(),
            Symlinks = new HashSet<string>(StringComparer.Ordinal),
        };
    }

    public global::Ts.Vfs.IFileInfo? Stat(string path)
    {
        var normalized = Normalize(path);
        if (files.TryGetValue(normalized, out var contents))
        {
            return new OverlayFileInfo(global::Ts.TsPath.TsPath.GetBaseFileName(normalized), contents.Length, false);
        }
        if (DirectoryExists(normalized) && !(underlying?.DirectoryExists(path) ?? false))
        {
            return new OverlayFileInfo(global::Ts.TsPath.TsPath.GetBaseFileName(normalized), 0, true);
        }
        return underlying?.Stat(path);
    }

    public Exception? WalkDir(string root, global::Ts.Vfs.WalkDirFunc walkFn)
    {
        // Program loading in this subset is driven by explicit root files. Preserve
        // the underlying walk surface when one exists; an overlay-only walk has no
        // current consumer and completes successfully.
        return underlying?.WalkDir(root, walkFn);
    }

    public string Realpath(string path)
    {
        var normalized = Normalize(path);
        return files.ContainsKey(normalized) ? normalized : underlying?.Realpath(path) ?? normalized;
    }

    private static string Normalize(string path) => global::Ts.TsPath.TsPath.NormalizePath(path);

    private sealed class OverlayFileInfo : global::Ts.Vfs.IFileInfo
    {
        private readonly string name;
        private readonly long size;
        private readonly bool isDirectory;

        public OverlayFileInfo(string name, long size, bool isDirectory)
        {
            this.name = name;
            this.size = size;
            this.isDirectory = isDirectory;
        }

        public string Name() => name;
        public long Size() => size;
        public global::Ts.Vfs.FileMode Mode() => isDirectory ? global::Ts.Vfs.FileMode.Dir : global::Ts.Vfs.FileMode.None;
        public DateTime ModTime() => DateTime.UnixEpoch;
        public bool IsDir() => isDirectory;
        public object? Sys() => null;
    }
}
