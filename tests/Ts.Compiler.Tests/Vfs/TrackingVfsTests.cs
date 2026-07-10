// Sanity coverage for Ts.Vfs.TrackingVfs. No Go counterpart file exists (tsgo's
// trackingvfs package ships without tests); these pin the read-tracked /
// write-untracked split its doc comment promises.

using System;
using Ts.Vfs;
using Ts.Vfs.TrackingVfs;
using Xunit;

namespace Ts.Vfs.Tests;

public class TrackingVfsTests
{
    private static (TrackingFs Tracking, FakeFs Inner) CreateTrackingFs()
    {
        var inner = new FakeFs(new()
        {
            ["/some/path/file.txt"] = "hello world",
            ["/some/path/other.txt"] = "hi",
        });
        return (new TrackingFs { Inner = inner }, inner);
    }

    [Fact]
    public void ReadOperationsAreTracked()
    {
        var (tracking, _) = CreateTrackingFs();

        tracking.TryReadFile("/some/path/file.txt", out _);
        tracking.FileExists("/missing.txt");
        tracking.DirectoryExists("/some/path");
        tracking.GetAccessibleEntries("/some");
        tracking.Stat("/stat.txt");
        tracking.Realpath("/real.txt");

        Assert.True(tracking.SeenFiles.Has("/some/path/file.txt"));
        Assert.True(tracking.SeenFiles.Has("/missing.txt")); // non-existent paths count as dependencies
        Assert.True(tracking.SeenFiles.Has("/some/path"));
        Assert.True(tracking.SeenFiles.Has("/some"));
        Assert.True(tracking.SeenFiles.Has("/stat.txt"));
        Assert.True(tracking.SeenFiles.Has("/real.txt"));
        Assert.Equal(6, tracking.SeenFiles.Size());
    }

    [Fact]
    public void WriteOperationsAreNotTracked()
    {
        var (tracking, inner) = CreateTrackingFs();

        tracking.WriteFile("/w.txt", "data");
        tracking.AppendFile("/a.txt", "data");
        tracking.Remove("/r.txt");
        tracking.Chtimes("/c.txt", default, default);
        tracking.UseCaseSensitiveFileNames();

        Assert.True(tracking.SeenFiles.IsEmpty());
        // Writes still pass through to the inner FS.
        Assert.Single(inner.WriteFileCalls);
        Assert.Single(inner.AppendFileCalls);
        Assert.Single(inner.RemoveCalls);
        Assert.Single(inner.ChtimesCalls);
    }

    [Fact]
    public void WalkDirTracksRootAndVisitedPaths()
    {
        var (tracking, _) = CreateTrackingFs();

        var err = tracking.WalkDir("/some/path", (path, d, walkErr) => walkErr);
        Assert.Null(err);

        Assert.True(tracking.SeenFiles.Has("/some/path"));
        Assert.True(tracking.SeenFiles.Has("/some/path/file.txt"));
        Assert.True(tracking.SeenFiles.Has("/some/path/other.txt"));
    }

    [Fact]
    public void WalkDirForwardsCallbackResults()
    {
        var (tracking, _) = CreateTrackingFs();

        int calls = 0;
        var err = tracking.WalkDir("/some/path", (path, d, walkErr) =>
        {
            calls++;
            return calls == 1 ? null : VfsUtil.SkipDir;
        });
        Assert.Null(err);
        Assert.Equal(2, calls);
    }
}
