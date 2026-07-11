using System;
using System.IO;
using Xunit;

namespace Ts.NativePath.Tests;

public class NativePathTests
{
    [Fact]
    public void RealpathOfPlainDirectoryIsItself()
    {
        string tmp = Directory.CreateTempSubdirectory("nptest").FullName;
        try
        {
            string? resolved = NativePath.Realpath(tmp);
            Assert.NotNull(resolved);
            // Real path may differ from tmp only by the OS canonicalizing an
            // already-symlinked temp root (e.g. macOS /var → /private/var); the
            // resolved path must at least exist and be absolute.
            Assert.True(Path.IsPathRooted(resolved));
            Assert.True(Directory.Exists(resolved));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void RealpathFollowsSymlink()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // creating symlinks needs elevation on Windows
        }
        string root = Directory.CreateTempSubdirectory("nplink").FullName;
        try
        {
            string realDir = Path.Combine(root, "real");
            Directory.CreateDirectory(realDir);
            string file = Path.Combine(realDir, "f.txt");
            File.WriteAllText(file, "x");
            string link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, realDir);

            string? resolved = NativePath.Realpath(Path.Combine(link, "f.txt"));
            Assert.NotNull(resolved);
            Assert.EndsWith(Path.Combine("real", "f.txt"), resolved);
            Assert.DoesNotContain($"{Path.DirectorySeparatorChar}link{Path.DirectorySeparatorChar}", resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IsSymlinkOrReparsePointDistinguishesLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        string root = Directory.CreateTempSubdirectory("npreparse").FullName;
        try
        {
            string realDir = Path.Combine(root, "real");
            Directory.CreateDirectory(realDir);
            string link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, realDir);

            Assert.True(NativePath.IsSymlinkOrReparsePoint(link));
            Assert.False(NativePath.IsSymlinkOrReparsePoint(realDir));
            Assert.False(NativePath.IsSymlinkOrReparsePoint(Path.Combine(root, "nonexistent")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
