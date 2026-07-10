// Ported from tsgo internal/vfs/iovfs/iofs_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// TestIOFS's t.Run subtests → one [Fact] each (05-conventions.md §16.1).
//
// MapFileProvider below is a test-local stand-in for Go stdlib testing/fstest.MapFS
// (synthesized directories, entries sorted by name); the real in-memory FS is the
// vfstest port, which lands with tests/Ts.TestUtil in a later work unit (plan/06 §2.6).

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Vfs;
using Ts.Vfs.IoVfs;
using Xunit;

namespace Ts.Vfs.Tests;

public class IoVfsTests
{
    private static IFsWithSys CreateTestFs()
    {
        return IoVfsUtil.From(new MapFileProvider(new Dictionary<string, string>
        {
            ["foo.ts"] = "hello, world",
            ["dir1/file1.ts"] = "export const foo = 42;",
            ["dir1/file2.ts"] = "export const foo = 42;",
            ["dir2/file1.ts"] = "export const foo = 42;",
        }), useCaseSensitiveFileNames: true);
    }

    [Fact]
    public void IoFs_ReadFile()
    {
        var fs = CreateTestFs();

        Assert.True(fs.TryReadFile("/foo.ts", out string content));
        Assert.Equal("hello, world", content);

        Assert.False(fs.TryReadFile("/does/not/exist.ts", out content));
        Assert.Equal("", content);
    }

    [Fact]
    public void IoFs_ReadFileUnrooted()
    {
        var fs = CreateTestFs();

        // Go: testutil.AssertPanics(t, ..., `vfs: path "bar" is not absolute`)
        var ex = Assert.Throws<InvalidOperationException>(() => fs.TryReadFile("bar", out _));
        Assert.Equal("vfs: path \"bar\" is not absolute", ex.Message);
    }

    [Fact]
    public void IoFs_FileExists()
    {
        var fs = CreateTestFs();

        Assert.True(fs.FileExists("/foo.ts"));
        Assert.False(fs.FileExists("/bar"));
    }

    [Fact]
    public void IoFs_DirectoryExists()
    {
        var fs = CreateTestFs();

        Assert.True(fs.DirectoryExists("/"));
        Assert.True(fs.DirectoryExists("/dir1"));
        Assert.True(fs.DirectoryExists("/dir1/"));
        Assert.True(fs.DirectoryExists("/dir1/./"));
        Assert.False(fs.DirectoryExists("/bar"));
    }

    [Fact]
    public void IoFs_GetAccessibleEntries()
    {
        var fs = CreateTestFs();

        var entries = fs.GetAccessibleEntries("/");
        Assert.Equal(new[] { "dir1", "dir2" }, entries.Directories);
        Assert.Equal(new[] { "foo.ts" }, entries.Files);
    }

    [Fact]
    public void IoFs_WalkDir()
    {
        var fs = CreateTestFs();

        var files = new List<string>();
        var err = fs.WalkDir("/", (path, d, walkErr) =>
        {
            if (walkErr != null)
            {
                return walkErr;
            }
            if (!d!.IsDir())
            {
                files.Add(path);
            }
            return null;
        });
        Assert.Null(err);

        files.Sort(StringComparer.Ordinal);

        Assert.Equal(new[] { "/dir1/file1.ts", "/dir1/file2.ts", "/dir2/file1.ts", "/foo.ts" }, files);
    }

    [Fact]
    public void IoFs_WalkDirSkip()
    {
        var fs = CreateTestFs();

        var files = new List<string>();
        var err = fs.WalkDir("/", (path, d, walkErr) =>
        {
            if (walkErr != null)
            {
                return walkErr;
            }
            if (!d!.IsDir())
            {
                files.Add(path);
            }

            if (path == "/")
            {
                return null;
            }

            return VfsUtil.SkipDir;
        });
        Assert.Null(err);

        files.Sort(StringComparer.Ordinal);

        Assert.Equal(new[] { "/foo.ts" }, files);
    }

    [Fact]
    public void IoFs_Realpath()
    {
        var fs = CreateTestFs();

        Assert.Equal("/foo.ts", fs.Realpath("/foo.ts"));
    }

    [Fact]
    public void IoFs_UseCaseSensitiveFileNames()
    {
        var fs = CreateTestFs();

        Assert.True(fs.UseCaseSensitiveFileNames());
    }
}

// Test-local minimal fstest.MapFS equivalent (see file header).
internal sealed class MapFileProvider : IFileProvider
{
    private readonly SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal);

    public MapFileProvider(IReadOnlyDictionary<string, string> map)
    {
        foreach (var (name, contents) in map)
        {
            files.Add(name, Encoding.UTF8.GetBytes(contents));
        }
    }

    public IFileInfo? Stat(string name)
    {
        if (name == ".")
        {
            return new MapFileInfo(".", 0, isDir: true);
        }
        if (files.TryGetValue(name, out var data))
        {
            return new MapFileInfo(LastSegment(name), data.Length, isDir: false);
        }
        string prefix = name + "/";
        foreach (var key in files.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return new MapFileInfo(LastSegment(name), 0, isDir: true);
            }
        }
        return null;
    }

    public IReadOnlyList<IDirEntry>? ReadDir(string name)
    {
        var stat = Stat(name);
        if (stat == null || !stat.IsDir())
        {
            return null;
        }
        string prefix = name == "." ? "" : name + "/";
        // name → isDir; SortedDictionary keeps the fs.ReadDir sorted-by-filename contract.
        var children = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in files.Keys)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            string rest = key[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                children[rest] = false;
            }
            else
            {
                children[rest[..slash]] = true;
            }
        }
        var result = new List<IDirEntry>(children.Count);
        foreach (var (childName, isDir) in children)
        {
            result.Add(new MapDirEntry(this, prefix + childName, childName, isDir));
        }
        return result;
    }

    public byte[]? ReadFile(string name)
    {
        return files.TryGetValue(name, out var data) ? data : null;
    }

    private static string LastSegment(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? name : name[(slash + 1)..];
    }

    private sealed class MapFileInfo : IFileInfo
    {
        private readonly string name;
        private readonly long size;
        private readonly bool isDir;

        public MapFileInfo(string name, long size, bool isDir)
        {
            this.name = name;
            this.size = size;
            this.isDir = isDir;
        }

        public string Name() => name;
        public long Size() => size;
        public FileMode Mode() => isDir ? FileMode.Dir : FileMode.None;
        public DateTime ModTime() => default;
        public bool IsDir() => isDir;
        public object? Sys() => null;
    }

    private sealed class MapDirEntry : IDirEntry
    {
        private readonly MapFileProvider provider;
        private readonly string fullName;
        private readonly string name;
        private readonly bool isDir;

        public MapDirEntry(MapFileProvider provider, string fullName, string name, bool isDir)
        {
            this.provider = provider;
            this.fullName = fullName;
            this.name = name;
            this.isDir = isDir;
        }

        public string Name() => name;
        public bool IsDir() => isDir;
        public FileMode Type() => isDir ? FileMode.Dir : FileMode.None;
        public IFileInfo? Info() => provider.Stat(fullName);
    }
}
