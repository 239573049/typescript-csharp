// Tests for the path predicates ported from tsgo internal/fswatch/watcher.go
// (isInDirectoryOrSelf, isDirectChild, rebasePath). Pure logic → deterministic.
// Uses forward-slash paths, which are valid separators on every platform per
// PathOps.IsPathSeparator (matching Go's os.IsPathSeparator on Unix and the
// '/'-accepting behavior on Windows).

using Ts.FsWatch;
using Xunit;

namespace Ts.FsWatch.Tests;

public class PathOpsTests
{
    [Theory]
    [InlineData("/foo", "/foo", true)]        // self
    [InlineData("/foo", "/foo/bar", true)]    // direct child
    [InlineData("/foo", "/foo/bar/baz", true)] // nested
    [InlineData("/foo", "/foo2", false)]      // sibling prefix must not match
    [InlineData("/foo", "/bar", false)]       // unrelated
    [InlineData("", "/foo", false)]           // empty dir
    [InlineData("/foo/", "/foo/bar", true)]   // trailing sep on dir
    public void IsInDirectoryOrSelf(string dir, string path, bool expected)
    {
        Assert.Equal(expected, PathOps.IsInDirectoryOrSelf(dir, path));
    }

    [Theory]
    [InlineData("/foo", "/foo", false)]         // self is not a child
    [InlineData("/foo", "/foo/bar", true)]      // direct child
    [InlineData("/foo", "/foo/bar/baz", false)] // grandchild is not direct
    [InlineData("/foo", "/foo2", false)]        // sibling prefix
    [InlineData("/foo", "/bar", false)]         // unrelated
    public void IsDirectChild(string dir, string path, bool expected)
    {
        Assert.Equal(expected, PathOps.IsDirectChild(dir, path));
    }

    [Theory]
    [InlineData("/from/child", "/from", "/to", "/to/child")] // rebase child
    [InlineData("/from", "/from", "/to", "/to")]             // rebase root itself
    [InlineData("/other/x", "/from", "/to", "/other/x")]     // no prefix → unchanged
    [InlineData("/from2/x", "/from", "/to", "/from2/x")]     // sibling prefix not rebased
    [InlineData("/same/x", "/same", "/same", "/same/x")]     // from == to → unchanged
    public void RebasePath(string path, string from, string to, string expected)
    {
        Assert.Equal(expected, PathOps.RebasePath(path, from, to));
    }
}
