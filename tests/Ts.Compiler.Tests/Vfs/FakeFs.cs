// Test-local stand-in for tsgo's vfsmock.FSMock (moq-generated call recorder) layered
// over a tiny canned file map (the role vfstest.FromMap plays in the Go tests).
// The real vfsmock/vfstest ports land with tests/Ts.TestUtil in a later work unit
// (plan/06 §2.6); replace usages of this fake then.

using System;
using System.Collections.Generic;
using Ts.Vfs;

namespace Ts.Vfs.Tests;

internal sealed class FakeFs : IFileSystem
{
    private readonly Dictionary<string, string> files;
    private readonly bool useCaseSensitiveFileNames;

    public FakeFs(Dictionary<string, string>? files = null, bool useCaseSensitiveFileNames = true)
    {
        this.files = files ?? new Dictionary<string, string>(StringComparer.Ordinal);
        this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
    }

    // Go vfsmock records every call's arguments; length checks stand in for call counts.
    public List<string> DirectoryExistsCalls { get; } = new();
    public List<string> FileExistsCalls { get; } = new();
    public List<string> GetAccessibleEntriesCalls { get; } = new();
    public List<string> RealpathCalls { get; } = new();
    public List<string> StatCalls { get; } = new();
    public List<string> ReadFileCalls { get; } = new();
    public List<string> WalkDirCalls { get; } = new();
    public List<string> RemoveCalls { get; } = new();
    public List<(string Path, string Data)> WriteFileCalls { get; } = new();
    public List<(string Path, string Data)> AppendFileCalls { get; } = new();
    public List<string> ChtimesCalls { get; } = new();
    public int UseCaseSensitiveFileNamesCalls { get; private set; }

    // Count views (int-typed so size assertions stay uniform; xUnit2013 rejects
    // Assert.Equal(1, collection.Count)).
    public int DirectoryExistsCallCount => DirectoryExistsCalls.Count;
    public int FileExistsCallCount => FileExistsCalls.Count;
    public int GetAccessibleEntriesCallCount => GetAccessibleEntriesCalls.Count;
    public int RealpathCallCount => RealpathCalls.Count;
    public int StatCallCount => StatCalls.Count;
    public int ReadFileCallCount => ReadFileCalls.Count;
    public int WalkDirCallCount => WalkDirCalls.Count;
    public int RemoveCallCount => RemoveCalls.Count;
    public int WriteFileCallCount => WriteFileCalls.Count;
    public int AppendFileCallCount => AppendFileCalls.Count;

    public bool UseCaseSensitiveFileNames()
    {
        UseCaseSensitiveFileNamesCalls++;
        return useCaseSensitiveFileNames;
    }

    public bool FileExists(string path)
    {
        FileExistsCalls.Add(path);
        return files.ContainsKey(path);
    }

    public bool TryReadFile(string path, out string contents)
    {
        ReadFileCalls.Add(path);
        if (files.TryGetValue(path, out var data))
        {
            contents = data;
            return true;
        }
        contents = "";
        return false;
    }

    public void WriteFile(string path, string data)
    {
        WriteFileCalls.Add((path, data));
    }

    public void AppendFile(string path, string data)
    {
        AppendFileCalls.Add((path, data));
    }

    public void Remove(string path)
    {
        RemoveCalls.Add(path);
    }

    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        ChtimesCalls.Add(path);
    }

    public bool DirectoryExists(string path)
    {
        DirectoryExistsCalls.Add(path);
        string prefix = path + "/";
        foreach (var key in files.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public Entries GetAccessibleEntries(string path)
    {
        GetAccessibleEntriesCalls.Add(path);
        return default;
    }

    public IFileInfo? Stat(string path)
    {
        StatCalls.Add(path);
        return null;
    }

    // Minimal walk: visits the root as a directory, then every canned file under it in
    // sorted order — enough for decorator tests (call counting / path tracking).
    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        WalkDirCalls.Add(root);
        var err = walkFn(root, new Entry(root, isDir: true), null);
        if (err != null)
        {
            return ReferenceEquals(err, VfsUtil.SkipDir) || ReferenceEquals(err, VfsUtil.SkipAll) ? null : err;
        }
        var children = new SortedSet<string>(StringComparer.Ordinal);
        string prefix = root == "/" ? "/" : root + "/";
        foreach (var key in files.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                children.Add(key);
            }
        }
        foreach (var child in children)
        {
            err = walkFn(child, new Entry(child, isDir: false), null);
            if (err != null)
            {
                if (ReferenceEquals(err, VfsUtil.SkipDir) || ReferenceEquals(err, VfsUtil.SkipAll))
                {
                    return null;
                }
                return err;
            }
        }
        return null;
    }

    public string Realpath(string path)
    {
        RealpathCalls.Add(path);
        return path;
    }

    private sealed class Entry : IDirEntry
    {
        private readonly string name;
        private readonly bool isDir;

        public Entry(string name, bool isDir)
        {
            this.name = name;
            this.isDir = isDir;
        }

        public string Name() => name;
        public bool IsDir() => isDir;
        public FileMode Type() => isDir ? FileMode.Dir : FileMode.None;
        public IFileInfo? Info() => null;
    }
}
