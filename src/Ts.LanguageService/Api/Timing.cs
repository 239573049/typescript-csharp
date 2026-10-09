// Ported from tsgo internal/api/timing.go (whole file).

using System.Collections.Generic;
using System.Diagnostics;

namespace Ts.Api;

// timing.go:8-10 — serverRecentRequestCapacity is the number of most-recent requests
// retained in the server-side timing ring buffer.
internal static class TimingConsts
{
    public const int ServerRecentRequestCapacity = 5;
}

// timing.go:13-21 — serverRequestTiming is a single server-side request's
// processing-time sample.
public sealed class ServerRequestTiming
{
    // Method is the API method that was handled.
    [System.Text.Json.Serialization.JsonPropertyName("method")]
    public string Method = "";

    // ProcessingTimeMs is the wall-clock time the server spent handling the
    // request, in milliseconds.
    [System.Text.Json.Serialization.JsonPropertyName("processingTimeMs")]
    public double ProcessingTimeMs;

    // Timestamp is the Unix time in milliseconds when the request completed.
    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public long Timestamp;
}

// timing.go:24-29 — serverTimingTotals holds running totals accumulated across every
// handled request.
public sealed class ServerTimingTotals
{
    // RequestCount is the total number of requests measured.
    [System.Text.Json.Serialization.JsonPropertyName("requestCount")]
    public ulong RequestCount;

    // TotalProcessingTimeMs is the sum of server processing time, in milliseconds.
    [System.Text.Json.Serialization.JsonPropertyName("totalProcessingTimeMs")]
    public double TotalProcessingTimeMs;
}

// timing.go:32-41 — serverTimingInfo is a point-in-time snapshot of collected server
// timing, returned to clients in response to a getServerTiming request.
public sealed class ServerTimingInfo
{
    // Enabled reports whether server-side timing collection is active.
    [System.Text.Json.Serialization.JsonPropertyName("enabled")]
    public bool Enabled;

    // Totals are the running totals across every handled request.
    [System.Text.Json.Serialization.JsonPropertyName("totals")]
    public ServerTimingTotals Totals = new();

    // RecentRequests are the most recent requests, oldest to newest, up to
    // serverRecentRequestCapacity.
    [System.Text.Json.Serialization.JsonPropertyName("recentRequests")]
    public List<ServerRequestTiming> RecentRequests = [];
}

// timingCollector accumulates per-request server processing times into running
// totals and a fixed-size ring buffer of the most recent requests. It is safe
// for concurrent use so the async connection can record from multiple request
// goroutines. (timing.go:44-54)
public sealed class TimingCollector
{
    private readonly object mu = new();
    private ServerTimingTotals totals = new();

    // ring holds up to serverRecentRequestCapacity entries; once full, head
    // marks the oldest entry.
    private List<ServerRequestTiming> ring = [];
    private int head;

    // newTimingCollector (timing.go:56-58).
    public TimingCollector()
    {
    }

    // record adds a single request's processing time to the totals and ring buffer
    // (timing.go:61-81).
    public void Record(string method, System.TimeSpan d)
    {
        double processingMs = TimingFunctions.DurationToMillis(d);

        lock (mu)
        {
            totals.RequestCount++;
            totals.TotalProcessingTimeMs += processingMs;

            var entry = new ServerRequestTiming
            {
                Method = method,
                ProcessingTimeMs = processingMs,
                Timestamp = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            if (ring.Count < TimingConsts.ServerRecentRequestCapacity)
            {
                ring.Add(entry);
            }
            else
            {
                ring[head] = entry;
                head = (head + 1) % TimingConsts.ServerRecentRequestCapacity;
            }
        }
    }

    // snapshot returns a copy of the currently collected timing information, with
    // recent requests ordered from oldest to newest (timing.go:84-98).
    public ServerTimingInfo Snapshot()
    {
        lock (mu)
        {
            var recent = new List<ServerRequestTiming>(ring.Count);
            for (int i = 0; i < ring.Count; i++)
            {
                recent.Add(ring[(head + i) % ring.Count]);
            }
            return new ServerTimingInfo
            {
                Enabled = true,
                Totals = new ServerTimingTotals
                {
                    RequestCount = totals.RequestCount,
                    TotalProcessingTimeMs = totals.TotalProcessingTimeMs,
                },
                RecentRequests = recent,
            };
        }
    }

    // reset clears all accumulated totals and recent-request history (timing.go:101-108).
    public void Reset()
    {
        lock (mu)
        {
            totals = new ServerTimingTotals();
            ring = [];
            head = 0;
        }
    }
}

// timing.go:111-136 — package-level functions hosted in a static class (C# has no
// namespace-level functions).
public static class TimingFunctions
{
    // serverTimingSnapshot returns the collector's snapshot, or a disabled snapshot
    // when timing collection is not enabled (collector is nil) (timing.go:111-117).
    public static ServerTimingInfo ServerTimingSnapshot(TimingCollector? c)
    {
        if (c is null)
        {
            return DisabledServerTimingInfo();
        }
        return c.Snapshot();
    }

    // disabledServerTimingInfo is the snapshot returned when timing collection is
    // not enabled (timing.go:120-126).
    public static ServerTimingInfo DisabledServerTimingInfo()
    {
        return new ServerTimingInfo
        {
            Enabled = false,
            RecentRequests = [],
        };
    }

    // durationToMillis converts a duration to fractional milliseconds, clamped to be
    // non-negative. It preserves sub-microsecond precision by converting from the
    // full nanosecond duration (timing.go:129-136).
    public static double DurationToMillis(System.TimeSpan d)
    {
        if (d < System.TimeSpan.Zero)
        {
            return 0;
        }
        return d.TotalMilliseconds;
    }
}
