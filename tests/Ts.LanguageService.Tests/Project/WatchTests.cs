// Ported from tsgo internal/project/watch_test.go (whole file).

using System.Collections.Generic;
using Xunit;

namespace Ts.Project.Tests;

public sealed class WatchTests
{
    // watch_test.go:9-20 — TestGetPathComponentsForWatching.
    [Fact]
    public void GetPathComponentsForWatching()
    {
        Assert.Equal(new[] { "/", "project" }, WatchUtil.GetPathComponentsForWatching("/project", ""));
        Assert.Equal(new[] { "C:/", "project" }, WatchUtil.GetPathComponentsForWatching("C:\\project", ""));
        Assert.Equal(new[] { "//server/share", "project", "tsconfig.json" }, WatchUtil.GetPathComponentsForWatching("//server/share/project/tsconfig.json", ""));
        Assert.Equal(new[] { "//server/share", "project", "tsconfig.json" }, WatchUtil.GetPathComponentsForWatching("\\\\server\\share\\project\\tsconfig.json", ""));
        Assert.Equal(new[] { "C:/Users" }, WatchUtil.GetPathComponentsForWatching("C:\\Users", ""));
        Assert.Equal(new[] { "C:/Users/andrew", "project" }, WatchUtil.GetPathComponentsForWatching("C:\\Users\\andrew\\project", ""));
        Assert.Equal(new[] { "/home" }, WatchUtil.GetPathComponentsForWatching("/home", ""));
        Assert.Equal(new[] { "/home/andrew", "project" }, WatchUtil.GetPathComponentsForWatching("/home/andrew/project", ""));
    }

    // watch_test.go:22-28 — TestNilWatchedFilesClone: Go calls Clone on a nil *WatchedFiles
    // receiver and asserts nil comes back. Go nil-receiver methods have no C# equivalent —
    // the C# Clone is an instance method (null target throws NRE) per plan/05 §10; only the
    // ID accessor got the nullable extension form (Watch.cs PORT-NOTE at watch.go:239-244).
    // Skipped: depends on Go nil-receiver semantics that are deliberately not ported.
    [Fact(Skip = "Go nil-receiver method semantics have no C# equivalent: WatchedFiles<T>.Clone is an instance method (plan/05 §10); a null receiver throws NRE instead of returning null")]
    public void NilWatchedFilesClone()
    {
        WatchedFiles<int>? w = null;
        WatchedFiles<int>? result = w!.Clone(42);
        Assert.Null(result);
    }
}
