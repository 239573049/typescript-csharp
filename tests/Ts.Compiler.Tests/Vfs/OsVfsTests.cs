// Ported from tsgo internal/vfs/osvfs/{os_test.go, realpath_test.go, helpers_test.go}
// (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTES:
//  - Go reads repo.RootPath()/go.mod; here a freshly written temp file stands in (there is
//    no go.mod in this repo), exercising the same ReadFile path.
//  - OS-specific behavior gates on OperatingSystem.IsWindows()/IsMacOS()/IsLinux(). Symlink
//    tests skip on Windows (dir symlinks/junctions need elevation, per helpers_test.go).
//  - BenchmarkRealpath is not ported (benchmarks are un-gated, plan/06).

using System;
using System.Collections.Generic;
using System.IO;
using Ts.Vfs;
using Ts.Vfs.OsVfs;
using Xunit;
using TsPathApi = Ts.TsPath.TsPath;

namespace Ts.Vfs.Tests;

public class OsVfsTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tsvfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReadFile()
    {
        var fs = OsVfsUtil.FS();

        string dir = NewTempDir();
        try
        {
            string file = Path.Combine(dir, "content.txt");
            const string expected = "hello from osvfs\nsecond line\n";
            File.WriteAllText(file, expected);

            string normalized = TsPathApi.NormalizePath(file);
            Assert.True(fs.TryReadFile(normalized, out string contents));
            Assert.Equal(expected, contents);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Realpath()
    {
        var fs = OsVfsUtil.FS();

        string? home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
        {
            Assert.Skip("home directory unavailable");
            return;
        }

        home = TsPathApi.NormalizePath(home);
        string expected = home;
        if (OperatingSystem.IsWindows())
        {
            // Windows drive letters can be lowercase, but realpath always returns uppercase.
            expected = char.ToUpperInvariant(expected[0]) + expected[1..];
        }

        string realpath = fs.Realpath(home);
        Assert.Equal(expected, realpath);
    }

    [Fact]
    public void UseCaseSensitiveFileNames()
    {
        var fs = OsVfsUtil.FS();

        // Just check that it works.
        _ = fs.UseCaseSensitiveFileNames();

        if (OperatingSystem.IsWindows())
        {
            Assert.False(fs.UseCaseSensitiveFileNames());
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.True(fs.UseCaseSensitiveFileNames());
        }
        // macOS is left unasserted (APFS case-sensitivity varies).
    }

    [Fact]
    public void SymlinkRealpath()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("directory symlinks require elevation on Windows");
            return;
        }

        var fs = OsVfsUtil.FS();

        string tmp = NewTempDir();
        try
        {
            string target = Path.Combine(tmp, "target");
            string targetFile = Path.Combine(target, "file");
            string link = Path.Combine(tmp, "link");
            string linkFile = Path.Combine(link, "file");

            Directory.CreateDirectory(target);
            File.WriteAllText(targetFile, "hello");
            Directory.CreateSymbolicLink(link, target);

            Assert.Equal("hello", File.ReadAllText(linkFile));

            string targetRealpath = fs.Realpath(TsPathApi.NormalizePath(targetFile));
            string linkRealpath = fs.Realpath(TsPathApi.NormalizePath(linkFile));

            Assert.Equal(targetRealpath, linkRealpath);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void GetAccessibleEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("symlink creation requires elevation on Windows");
            return;
        }

        var fs = OsVfsUtil.FS();

        string tmp = NewTempDir();
        try
        {
            string target = Path.Combine(tmp, "target");
            string link = Path.Combine(tmp, "link");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(link);

            string targetFile1 = Path.Combine(target, "file1");
            string targetFile2 = Path.Combine(target, "file2");
            File.WriteAllText(targetFile1, "hello");
            File.WriteAllText(targetFile2, "world");

            string targetDir1 = Path.Combine(target, "dir1");
            string targetDir2 = Path.Combine(target, "dir2");
            Directory.CreateDirectory(targetDir1);
            Directory.CreateDirectory(targetDir2);

            File.CreateSymbolicLink(Path.Combine(link, "file1"), targetFile1);
            File.CreateSymbolicLink(Path.Combine(link, "file2"), targetFile2);
            Directory.CreateSymbolicLink(Path.Combine(link, "dir1"), targetDir1);
            Directory.CreateSymbolicLink(Path.Combine(link, "dir2"), targetDir2);

            var entries = fs.GetAccessibleEntries(TsPathApi.NormalizePath(link));

            Assert.Equal(new[] { "dir1", "dir2" }, entries.Directories);
            Assert.Equal(new[] { "file1", "file2" }, entries.Files);
            Assert.NotNull(entries.Symlinks);
            Assert.Equal(4, entries.Symlinks!.Count);
            foreach (string name in new[] { "file1", "file2", "dir1", "dir2" })
            {
                Assert.Contains(name, entries.Symlinks);
            }

            // Non-symlink directory should have empty Symlinks.
            var targetEntries = fs.GetAccessibleEntries(TsPathApi.NormalizePath(target));
            Assert.Equal(new[] { "dir1", "dir2" }, targetEntries.Directories);
            Assert.Equal(new[] { "file1", "file2" }, targetEntries.Files);
            Assert.NotNull(targetEntries.Symlinks);
            Assert.Empty(targetEntries.Symlinks!);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (IOException) { }
        }
    }
}
