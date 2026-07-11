// Ported from tsgo internal/fswatch/walkdir_test.go. Go ran each case against
// two implementations (native + generic); here there is one portable
// WalkDir.WalkDirGeneric (plan/06 §8.5 collapses the platform walkers), so each
// test runs once. Uses real temp directories.

using System;
using System.Collections.Generic;
using System.IO;
using Ts.FsWatch;
using Xunit;

namespace Ts.FsWatch.Tests;

public class WalkDirTests : IDisposable
{
    private readonly string _root;

    public WalkDirTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fswatch-walk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void MissingDir()
    {
        string dir = Path.Combine(_root, "nonexistent");
        Exception? err = WalkDir.WalkDirGeneric(dir, recursive: true, fn: null);
        Assert.NotNull(err); // expected error for missing directory
    }

    [Fact]
    public void NotADir()
    {
        string f = Path.Combine(_root, "file");
        File.WriteAllText(f, "x");
        Exception? err = WalkDir.WalkDirGeneric(f, recursive: true, fn: null);
        Assert.NotNull(err); // expected error for non-directory
    }

    [Fact]
    public void Entries()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "b.txt"), "b");

        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        Exception? err = WalkDir.WalkDirGeneric(_root, recursive: true, (path, isDir) =>
        {
            found[path] = isDir;
            return null;
        });
        Assert.Null(err);
        Assert.True(found.ContainsKey(Path.Combine(_root, "a.txt")), "missing a.txt");
        Assert.True(found.ContainsKey(sub), "missing sub/");
        Assert.True(found.ContainsKey(Path.Combine(sub, "b.txt")), "missing sub/b.txt");
    }

    [Fact]
    public void Callback()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "f.txt"), "f");

        var dirs = new List<string>();
        var files = new List<string>();
        Exception? err = WalkDir.WalkDirGeneric(_root, recursive: true, (path, isDir) =>
        {
            if (isDir)
            {
                dirs.Add(path);
            }
            else
            {
                files.Add(path);
            }
            return null;
        });
        Assert.Null(err);
        Assert.True(dirs.Count >= 2, $"expected at least 2 dirs (root + sub), got {dirs.Count}");
        Assert.True(files.Count >= 1, $"expected at least 1 file, got {files.Count}");
    }

    [Fact]
    public void CallbackError()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "a");
        var sentinel = new Exception("stop");
        Exception? err = WalkDir.WalkDirGeneric(_root, recursive: true, (path, isDir) => sentinel);
        Assert.Same(sentinel, err); // expected the sentinel error to propagate
    }

    [Fact]
    public void NonRecursiveReportsSubdirButDoesNotDescend()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "deep.txt"), "deep");

        var found = new Dictionary<string, bool>(StringComparer.Ordinal);
        Exception? err = WalkDir.WalkDirGeneric(_root, recursive: false, (path, isDir) =>
        {
            found[path] = isDir;
            return null;
        });
        Assert.Null(err);
        Assert.True(found.ContainsKey(sub), "sub/ should be reported as a direct child");
        // Non-recursive walk must not descend into sub/.
        Assert.False(found.ContainsKey(Path.Combine(sub, "deep.txt")), "should not descend into sub/");
    }
}
