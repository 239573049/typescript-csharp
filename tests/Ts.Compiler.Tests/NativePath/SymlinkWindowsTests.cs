// Ported from tsgo internal/nativepath/symlink_windows_test.go (whole file).
//
// PORT-NOTES:
//  * The Go file is GOOS=windows only (filename suffix `_windows_test.go`), so every test
//    returns immediately off Windows — the same "platform gate, run on the other OS"
//    shape the sibling NativePathTests.cs uses for its Unix-only cases.
//  * Go's `mklink` helper shells out to `cmd /c mklink /J` for junctions and to
//    os.Symlink for files. os.Symlink's "A required privilege is not held by the client"
//    skip is mirrored: only that privilege error becomes Assert.Skip, any other failure
//    still fails the test.
//  * t.TempDir() → Directory.CreateTempSubdirectory + Directory.Delete in finally.
//  * Assert.Equal(t, x, bool) → Assert.True/Assert.False (same evaluation, terser failure).
//  * filepath.Join uses '\\' on Windows → Path.Combine.
//  * The Go benchmark BenchmarkIsSymlinkOrJunction is not a test and has no xUnit
//    counterpart (conventions §16.1: benchmarks → BenchmarkDotNet in phase 10).
//  * symlink_windows_test.go:125 cannot use t.Parallel() (t.Chdir); xUnit has no
//    per-test opt-out, so the process CWD is restored in a finally block instead.

using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Ts.NativePath.Tests;

public class SymlinkWindowsTests
{
    // symlink_windows_test.go:172-185 — mklink.
    private static void Mklink(string target, string link, bool isDir)
    {
        if (isDir)
        {
            RunCmd("mklink", "/J", link, target);
            return;
        }
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Go: os.Symlink error containing "A required privilege is not held by the client"
            // → tb.Skip("file symlink support is not enabled without elevation or developer mode").
            if (e.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Skip($"file symlink support is not enabled without elevation or developer mode: {e.Message}");
            }
            throw;
        }
    }

    // Go: exec.Command("cmd", "/c", mklink, ...).Run() — a non-zero exit (or a missing
    // cmd) fails the test the way assert.NilError(t, err) would.
    private static void RunCmd(params string[] args)
    {
        var startInfo = new ProcessStartInfo("cmd")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("/c");
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        using Process? process = Process.Start(startInfo);
        Assert.NotNull(process);
        string stderr = process!.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"cmd /c {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
    }

    // symlink_windows_test.go:13-74 — TestIsSymlinkOrReparsePoint.
    [Fact]
    public void IsSymlinkOrReparsePoint()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // symlink_windows_test.go is GOOS=windows only
        }

        string tmp = Directory.CreateTempSubdirectory("npsymlink").FullName;
        try
        {
            // "regular file"
            string file = Path.Combine(tmp, "regular.txt");
            File.WriteAllText(file, "hello");
            Assert.False(NativePath.IsSymlinkOrReparsePoint(file));

            // "regular directory"
            string dir = Path.Combine(tmp, "regular-dir");
            Directory.CreateDirectory(dir);
            Assert.False(NativePath.IsSymlinkOrReparsePoint(dir));

            // "junction point"
            string junctionTarget = Path.Combine(tmp, "junction-target");
            string junctionLink = Path.Combine(tmp, "junction-link");
            Directory.CreateDirectory(junctionTarget);
            Mklink(junctionTarget, junctionLink, isDir: true);
            Assert.True(NativePath.IsSymlinkOrReparsePoint(junctionLink));

            // "file symlink"
            string symlinkTarget = Path.Combine(tmp, "symlink-target.txt");
            string symlinkLink = Path.Combine(tmp, "symlink-link.txt");
            File.WriteAllText(symlinkTarget, "hello");
            Mklink(symlinkTarget, symlinkLink, isDir: false);
            Assert.True(NativePath.IsSymlinkOrReparsePoint(symlinkLink));

            // "directory symlink"
            string dirSymlinkTarget = Path.Combine(tmp, "dir-symlink-target");
            string dirSymlinkLink = Path.Combine(tmp, "dir-symlink-link");
            Directory.CreateDirectory(dirSymlinkTarget);
            Mklink(dirSymlinkTarget, dirSymlinkLink, isDir: false);
            Assert.True(NativePath.IsSymlinkOrReparsePoint(dirSymlinkLink));

            // "nonexistent path"
            string nonexistent = Path.Combine(tmp, "does-not-exist");
            Assert.False(NativePath.IsSymlinkOrReparsePoint(nonexistent));

            // "empty path"
            Assert.False(NativePath.IsSymlinkOrReparsePoint(""));

            // "invalid path with null byte"
            Assert.False(NativePath.IsSymlinkOrReparsePoint("invalid\0path"));
        }
        finally
        {
            DeleteTree(tmp);
        }
    }

    // symlink_windows_test.go:76-101 — TestIsSymlinkOrReparsePointLongPath.
    [Fact]
    public void IsSymlinkOrReparsePointLongPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // symlink_windows_test.go is GOOS=windows only
        }

        string tmp = Directory.CreateTempSubdirectory("nplongpath").FullName;
        try
        {
            // Create a deeply nested path that exceeds 248 characters
            string longPathBase = tmp;
            const string pathComponent = "very_long_directory_name_to_exceed_max_path_limit_abcdefghijklmnopqrstuvwxyz";

            while (longPathBase.Length < 250)
            {
                longPathBase = Path.Combine(longPathBase, pathComponent);
            }

            string target = Path.Combine(longPathBase, "target");
            string link = Path.Combine(longPathBase, "link");

            // Use \\?\ prefix to enable long path support for mklink
            string longTarget = @"\\?\" + target;
            string longLink = @"\\?\" + link;

            Directory.CreateDirectory(longTarget);
            RunCmd("mklink", "/J", longLink, longTarget);

            // With long path support enabled, this should work even for paths >= 248 chars
            Assert.True(NativePath.IsSymlinkOrReparsePoint(link));
        }
        finally
        {
            DeleteTree(tmp);
        }
    }

    // symlink_windows_test.go:103-123 — TestIsSymlinkOrReparsePointNestedInSymlink.
    [Fact]
    public void IsSymlinkOrReparsePointNestedInSymlink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // symlink_windows_test.go is GOOS=windows only
        }

        string tmp = Directory.CreateTempSubdirectory("npnested").FullName;
        try
        {
            // Create a structure: target/inner-target, link -> target, then check link/inner-link
            string target = Path.Combine(tmp, "target");
            string innerTarget = Path.Combine(target, "inner-target");
            Directory.CreateDirectory(innerTarget);

            string link = Path.Combine(tmp, "link");
            Mklink(target, link, isDir: true);

            // Create a junction inside the target
            string innerLink = Path.Combine(target, "inner-link");
            Mklink(innerTarget, innerLink, isDir: true);

            // Check the junction through the symlink path
            string nestedPath = Path.Combine(link, "inner-link");
            Assert.True(NativePath.IsSymlinkOrReparsePoint(nestedPath));
        }
        finally
        {
            DeleteTree(tmp);
        }
    }

    // symlink_windows_test.go:125-136 — TestIsSymlinkOrReparsePointRelativePath.
    // (Go: //nolint:paralleltest — Cannot use t.Parallel() with t.Chdir().)
    [Fact]
    public void IsSymlinkOrReparsePointRelativePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // symlink_windows_test.go is GOOS=windows only
        }

        string tmp = Directory.CreateTempSubdirectory("nprel").FullName;
        string previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(tmp);

            const string target = "target-rel";
            const string link = "link-rel";
            Directory.CreateDirectory(target);
            Mklink(target, link, isDir: true);

            Assert.True(NativePath.IsSymlinkOrReparsePoint(link));
            Assert.False(NativePath.IsSymlinkOrReparsePoint(target));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            DeleteTree(tmp);
        }
    }

    // Best-effort cleanup of junctions/nested trees created above (Go: t.TempDir auto-cleanup).
    private static void DeleteTree(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
