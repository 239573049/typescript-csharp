// Tests for the api server-timing collector (ported from tsgo internal/api/timing_test.go).
//
// PORT-NOTE (duration precision): Go time.Duration is nanosecond-grained; C# TimeSpan is
// 100ns-grained. The Go case durationToMillis(1234ns) == 0.001234 has no exact TimeSpan
// input — the representable analog (1200ns == 12 ticks == 0.0012ms) is asserted instead.

using Ts.Api;
using Xunit;

namespace Ts.Api.Tests;

public class TimingTests
{
    [Fact]
    public void AccumulatesTotalsAndRecordsRecentRequests()
    {
        var c = new TimingCollector();
        c.Record("getSourceFile", System.TimeSpan.FromMilliseconds(2));
        c.Record("getSymbolAtPosition", System.TimeSpan.FromMilliseconds(0.5));

        ServerTimingInfo snap = c.Snapshot();
        Assert.True(snap.Enabled);
        Assert.Equal(2UL, snap.Totals.RequestCount);
        Assert.Equal(2.5, snap.Totals.TotalProcessingTimeMs);
        Assert.Equal(2, snap.RecentRequests.Count);
        Assert.Equal("getSourceFile", snap.RecentRequests[0].Method);
        Assert.Equal(2.0, snap.RecentRequests[0].ProcessingTimeMs);
        Assert.Equal("getSymbolAtPosition", snap.RecentRequests[1].Method);
        Assert.Equal(0.5, snap.RecentRequests[1].ProcessingTimeMs);
    }

    [Fact]
    public void RingBufferRetainsOnlyTheMostRecentRequestsOldestToNewest()
    {
        var c = new TimingCollector();
        string[] methods = ["a", "b", "c", "d", "e", "f", "g"];
        foreach (string m in methods)
        {
            c.Record(m, System.TimeSpan.FromMilliseconds(1));
        }

        ServerTimingInfo snap = c.Snapshot();
        Assert.Equal(7UL, snap.Totals.RequestCount);
        Assert.Equal(5, snap.RecentRequests.Count);

        // Expect the last 5 methods, oldest to newest.
        string[] want = methods[^5..];
        for (int i = 0; i < want.Length; i++)
        {
            Assert.Equal(want[i], snap.RecentRequests[i].Method);
        }
    }

    [Fact]
    public void NegativeDurationsClampToZero()
    {
        var c = new TimingCollector();
        c.Record("x", System.TimeSpan.FromSeconds(-5));
        ServerTimingInfo snap = c.Snapshot();
        Assert.Equal(0.0, snap.Totals.TotalProcessingTimeMs);
        Assert.Equal(0.0, snap.RecentRequests[0].ProcessingTimeMs);
    }

    [Fact]
    public void ServerTimingSnapshotDisabled()
    {
        ServerTimingInfo snap = TimingFunctions.ServerTimingSnapshot(null);
        Assert.False(snap.Enabled);
        Assert.Equal(0UL, snap.Totals.RequestCount);
        Assert.Empty(snap.RecentRequests);
    }

    [Fact]
    public void TimingCollectorReset()
    {
        var c = new TimingCollector();
        c.Record("a", System.TimeSpan.FromMilliseconds(1));
        c.Record("b", System.TimeSpan.FromMilliseconds(1));
        c.Reset();

        ServerTimingInfo snap = c.Snapshot();
        Assert.True(snap.Enabled);
        Assert.Equal(0UL, snap.Totals.RequestCount);
        Assert.Equal(0.0, snap.Totals.TotalProcessingTimeMs);
        Assert.Empty(snap.RecentRequests);

        // The collector remains usable after a reset.
        c.Record("c", System.TimeSpan.FromMilliseconds(2));
        snap = c.Snapshot();
        Assert.Equal(1UL, snap.Totals.RequestCount);
        Assert.Equal("c", snap.RecentRequests[0].Method);
    }

    [Fact]
    public void DurationToMillisConversions()
    {
        Assert.Equal(1.5, TimingFunctions.DurationToMillis(System.TimeSpan.FromMilliseconds(1.5)));
        Assert.Equal(0.0, TimingFunctions.DurationToMillis(System.TimeSpan.Zero));
        Assert.Equal(0.0, TimingFunctions.DurationToMillis(System.TimeSpan.FromSeconds(-5)));
        // Sub-microsecond durations retain precision rather than truncating to 0
        // (500ns == exactly 5 TimeSpan ticks).
        Assert.Equal(0.0005, TimingFunctions.DurationToMillis(System.TimeSpan.FromTicks(5)));
        // Go: durationToMillis(1234*time.Nanosecond) == 0.001234 — not representable in
        // TimeSpan; 1200ns is the nearest 100ns-grained analog.
        Assert.Equal(0.0012, TimingFunctions.DurationToMillis(System.TimeSpan.FromTicks(12)));
    }
}
