// Ported from tsgo internal/vfs/cachedvfs/cachedvfs_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Go's createMockFS = vfsmock.Wrap(vfstest.FromMap(...)); FakeFs.cs is the stand-in
// call recorder until the vfsmock/vfstest ports land (plan/06 §2.6).

using System;
using System.Collections.Generic;
using Ts.Vfs;
using Ts.Vfs.CachedVfs;
using Xunit;

namespace Ts.Vfs.Tests;

public class CachedVfsTests
{
    private static FakeFs CreateMockFs()
    {
        return new FakeFs(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["/some/path/file.txt"] = "hello world",
        });
    }

    [Fact]
    public void DirectoryExists()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.DirectoryExists("/some/path");
        Assert.Equal(1, underlying.DirectoryExistsCallCount);

        cached.DirectoryExists("/some/path");
        Assert.Equal(1, underlying.DirectoryExistsCallCount);

        cached.ClearCache();
        cached.DirectoryExists("/some/path");
        Assert.Equal(2, underlying.DirectoryExistsCallCount);

        cached.DirectoryExists("/other/path");
        Assert.Equal(3, underlying.DirectoryExistsCallCount);

        cached.DisableAndClearCache();
        cached.DirectoryExists("/some/path");
        Assert.Equal(4, underlying.DirectoryExistsCallCount);

        cached.DirectoryExists("/some/path");
        Assert.Equal(5, underlying.DirectoryExistsCallCount);

        cached.Enable();
        cached.DirectoryExists("/some/path");
        Assert.Equal(6, underlying.DirectoryExistsCallCount);

        cached.DirectoryExists("/some/path");
        Assert.Equal(6, underlying.DirectoryExistsCallCount);
    }

    [Fact]
    public void FileExists()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.FileExists("/some/path/file.txt");
        Assert.Equal(1, underlying.FileExistsCallCount);

        cached.FileExists("/some/path/file.txt");
        Assert.Equal(1, underlying.FileExistsCallCount);

        cached.ClearCache();
        cached.FileExists("/some/path/file.txt");
        Assert.Equal(2, underlying.FileExistsCallCount);

        cached.FileExists("/other/path/file.txt");
        Assert.Equal(3, underlying.FileExistsCallCount);

        cached.DisableAndClearCache();
        cached.FileExists("/some/path/file.txt");
        Assert.Equal(4, underlying.FileExistsCallCount);

        cached.FileExists("/some/path/file.txt");
        Assert.Equal(5, underlying.FileExistsCallCount);

        cached.Enable();
        cached.FileExists("/some/path/file.txt");
        Assert.Equal(6, underlying.FileExistsCallCount);

        cached.FileExists("/some/path/file.txt");
        Assert.Equal(6, underlying.FileExistsCallCount);
    }

    [Fact]
    public void GetAccessibleEntries()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(1, underlying.GetAccessibleEntriesCallCount);

        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(1, underlying.GetAccessibleEntriesCallCount);

        cached.ClearCache();
        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(2, underlying.GetAccessibleEntriesCallCount);

        cached.GetAccessibleEntries("/other/path");
        Assert.Equal(3, underlying.GetAccessibleEntriesCallCount);

        cached.DisableAndClearCache();
        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(4, underlying.GetAccessibleEntriesCallCount);

        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(5, underlying.GetAccessibleEntriesCallCount);

        cached.Enable();
        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(6, underlying.GetAccessibleEntriesCallCount);

        cached.GetAccessibleEntries("/some/path");
        Assert.Equal(6, underlying.GetAccessibleEntriesCallCount);
    }

    [Fact]
    public void Realpath()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.Realpath("/some/path");
        Assert.Equal(1, underlying.RealpathCallCount);

        cached.Realpath("/some/path");
        Assert.Equal(1, underlying.RealpathCallCount);

        cached.ClearCache();
        cached.Realpath("/some/path");
        Assert.Equal(2, underlying.RealpathCallCount);

        cached.Realpath("/other/path");
        Assert.Equal(3, underlying.RealpathCallCount);

        cached.DisableAndClearCache();
        cached.Realpath("/some/path");
        Assert.Equal(4, underlying.RealpathCallCount);

        cached.Realpath("/some/path");
        Assert.Equal(5, underlying.RealpathCallCount);

        cached.Enable();
        cached.Realpath("/some/path");
        Assert.Equal(6, underlying.RealpathCallCount);

        cached.Realpath("/some/path");
        Assert.Equal(6, underlying.RealpathCallCount);
    }

    [Fact]
    public void Stat()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.Stat("/some/path");
        Assert.Equal(1, underlying.StatCallCount);

        cached.Stat("/some/path");
        Assert.Equal(1, underlying.StatCallCount);

        cached.ClearCache();
        cached.Stat("/some/path");
        Assert.Equal(2, underlying.StatCallCount);

        cached.Stat("/other/path");
        Assert.Equal(3, underlying.StatCallCount);

        cached.DisableAndClearCache();
        cached.Stat("/some/path");
        Assert.Equal(4, underlying.StatCallCount);

        cached.Stat("/some/path");
        Assert.Equal(5, underlying.StatCallCount);

        cached.Enable();
        cached.Stat("/some/path");
        Assert.Equal(6, underlying.StatCallCount);

        cached.Stat("/some/path");
        Assert.Equal(6, underlying.StatCallCount);
    }

    [Fact]
    public void ReadFile()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(1, underlying.ReadFileCallCount);

        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(2, underlying.ReadFileCallCount);

        cached.ClearCache();
        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(3, underlying.ReadFileCallCount);

        cached.DisableAndClearCache();
        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(4, underlying.ReadFileCallCount);

        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(5, underlying.ReadFileCallCount);

        cached.Enable();
        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(6, underlying.ReadFileCallCount);

        cached.TryReadFile("/some/path/file.txt", out _);
        Assert.Equal(7, underlying.ReadFileCallCount);
    }

    [Fact]
    public void UseCaseSensitiveFileNames()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.UseCaseSensitiveFileNames();
        Assert.Equal(1, underlying.UseCaseSensitiveFileNamesCalls);

        cached.UseCaseSensitiveFileNames();
        Assert.Equal(2, underlying.UseCaseSensitiveFileNamesCalls);

        cached.ClearCache();
        cached.UseCaseSensitiveFileNames();
        Assert.Equal(3, underlying.UseCaseSensitiveFileNamesCalls);

        cached.DisableAndClearCache();
        cached.UseCaseSensitiveFileNames();
        Assert.Equal(4, underlying.UseCaseSensitiveFileNamesCalls);

        cached.UseCaseSensitiveFileNames();
        Assert.Equal(5, underlying.UseCaseSensitiveFileNamesCalls);

        cached.Enable();
        cached.UseCaseSensitiveFileNames();
        Assert.Equal(6, underlying.UseCaseSensitiveFileNamesCalls);

        cached.UseCaseSensitiveFileNames();
        Assert.Equal(7, underlying.UseCaseSensitiveFileNamesCalls);
    }

    [Fact]
    public void WalkDir()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        WalkDirFunc walkFn = (path, info, err) => null;

        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(1, underlying.WalkDirCallCount);

        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(2, underlying.WalkDirCallCount);

        cached.ClearCache();
        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(3, underlying.WalkDirCallCount);

        cached.DisableAndClearCache();
        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(4, underlying.WalkDirCallCount);

        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(5, underlying.WalkDirCallCount);

        cached.Enable();
        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(6, underlying.WalkDirCallCount);

        _ = cached.WalkDir("/some/path", walkFn);
        Assert.Equal(7, underlying.WalkDirCallCount);
    }

    [Fact]
    public void Remove()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.Remove("/some/path/file.txt");
        Assert.Equal(1, underlying.RemoveCallCount);

        cached.Remove("/some/path/file.txt");
        Assert.Equal(2, underlying.RemoveCallCount);

        cached.ClearCache();
        cached.Remove("/some/path/file.txt");
        Assert.Equal(3, underlying.RemoveCallCount);

        cached.DisableAndClearCache();
        cached.Remove("/some/path/file.txt");
        Assert.Equal(4, underlying.RemoveCallCount);

        cached.Remove("/some/path/file.txt");
        Assert.Equal(5, underlying.RemoveCallCount);

        cached.Enable();
        cached.Remove("/some/path/file.txt");
        Assert.Equal(6, underlying.RemoveCallCount);

        cached.Remove("/some/path/file.txt");
        Assert.Equal(7, underlying.RemoveCallCount);
    }

    [Fact]
    public void WriteFile()
    {
        var underlying = CreateMockFs();
        var cached = CachedFs.From(underlying);

        cached.WriteFile("/some/path/file.txt", "new content");
        Assert.Equal(1, underlying.WriteFileCallCount);

        cached.WriteFile("/some/path/file.txt", "another content");
        Assert.Equal(2, underlying.WriteFileCallCount);

        cached.ClearCache();
        cached.WriteFile("/some/path/file.txt", "third content");
        Assert.Equal(3, underlying.WriteFileCallCount);

        var call = underlying.WriteFileCalls[2];
        Assert.Equal("/some/path/file.txt", call.Path);
        Assert.Equal("third content", call.Data);

        cached.DisableAndClearCache();
        cached.WriteFile("/some/path/file.txt", "fourth content");
        Assert.Equal(4, underlying.WriteFileCallCount);

        cached.WriteFile("/some/path/file.txt", "fifth content");
        Assert.Equal(5, underlying.WriteFileCallCount);

        cached.Enable();
        cached.WriteFile("/some/path/file.txt", "sixth content");
        Assert.Equal(6, underlying.WriteFileCallCount);

        cached.WriteFile("/some/path/file.txt", "seventh content");
        Assert.Equal(7, underlying.WriteFileCallCount);
    }
}
