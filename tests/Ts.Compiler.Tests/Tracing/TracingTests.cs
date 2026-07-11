// Tests for Ts.Tracing — ported/adapted from tsgo internal/tracing/tracing_test.go.
//
// PORT-NOTE: 非 1:1，EventSource 重写 (plan/06 §9.2). The Go tests assert on the Chrome
// trace_event JSON bytes written to /trace/trace.json. The C# port keeps a byte-faithful
// JSON writer (TraceJson) so the *structural* assertions (thread-id assignment, B/E
// pairing, well-nesting) port directly; they run against an injected recording sink for
// determinism (plan/08 "可注入 sink 做确定性断言") AND against the round-tripped JSON to
// prove the faithful writer path. deterministic=true is used throughout, matching Go.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Ts.Vfs;
using Xunit;

namespace Ts.Tracing.Tests;

public class TracingTests
{
    // In-memory FS whose AppendFile accumulates, so trace.json can be read back
    // (the role vfstest.FromMap plays in the Go tests). Ordinal keying, deterministic.
    private sealed class AccumulatingFs : IFileSystem
    {
        private readonly Dictionary<string, StringBuilder> files = new(StringComparer.Ordinal);

        public bool UseCaseSensitiveFileNames() => true;
        public bool FileExists(string path) => files.ContainsKey(path);

        public bool TryReadFile(string path, out string contents)
        {
            if (files.TryGetValue(path, out StringBuilder? sb))
            {
                contents = sb.ToString();
                return true;
            }
            contents = "";
            return false;
        }

        public void WriteFile(string path, string data)
        {
            files[path] = new StringBuilder(data);
        }

        public void AppendFile(string path, string data)
        {
            if (!files.TryGetValue(path, out StringBuilder? sb))
            {
                sb = new StringBuilder();
                files[path] = sb;
            }
            sb.Append(data);
        }

        public void Remove(string path) => files.Remove(path);
        public void Chtimes(string path, DateTime aTime, DateTime mTime) { }
        public bool DirectoryExists(string path) => true;
        public Entries GetAccessibleEntries(string path) => default;
        public IFileInfo? Stat(string path) => null;
        public Exception? WalkDir(string root, WalkDirFunc walkFn) => null;
        public string Realpath(string path) => path;
    }

    // Recording sink: captures every emitted event for deterministic assertions.
    private sealed class RecordingSink : ITraceSink
    {
        public List<TraceEvent> Events { get; } = new();

        public void OnEvent(TraceEvent evt) => Events.Add(evt);
    }

    private static Dictionary<string, object?> Args(params (string, object?)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string k, object? v) in pairs)
        {
            d[k] = v;
        }
        return d;
    }

    // Parse the round-tripped trace.json back into TraceEvent-shaped records.
    private static List<TraceEvent> ParseTrace(string json)
    {
        var result = new List<TraceEvent>();
        using JsonDocument doc = JsonDocument.Parse(json);
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            var evt = new TraceEvent
            {
                PID = el.GetProperty("pid").GetInt32(),
                TID = el.GetProperty("tid").GetInt32(),
                PH = el.GetProperty("ph").GetString() ?? "",
                Cat = el.GetProperty("cat").GetString() ?? "",
                TS = el.GetProperty("ts").GetDouble(),
                Name = el.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
            };
            if (el.TryGetProperty("args", out JsonElement a))
            {
                var args = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty p in a.EnumerateObject())
                {
                    args[p.Name] = p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString(),
                        JsonValueKind.Number => p.Value.TryGetInt64(out long l) ? l : p.Value.GetDouble(),
                        _ => p.Value.ToString(),
                    };
                }
                evt.Args = args;
            }
            result.Add(evt);
        }
        return result;
    }

    private static TraceEvent FindEvent(List<TraceEvent> events, string phase, string name, string argName, object? argValue)
    {
        foreach (TraceEvent e in events)
        {
            if (e.PH == phase && e.Name == name && e.Args != null &&
                e.Args.TryGetValue(argName, out object? v) && ArgEquals(v, argValue))
            {
                return e;
            }
        }
        throw new Xunit.Sdk.XunitException($"failed to find {phase} event '{name}' with {argName}={argValue}");
    }

    // Numeric-agnostic arg compare: after a JSON round-trip an int arg comes back as
    // long/double, so compare numeric values by magnitude (mirrors Go's float64 args).
    private static bool ArgEquals(object? a, object? b)
    {
        if (a is string || b is string)
        {
            return Equals(a, b);
        }
        if (a is null || b is null)
        {
            return Equals(a, b);
        }
        return Convert.ToDouble(a, System.Globalization.CultureInfo.InvariantCulture)
            == Convert.ToDouble(b, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertThreadName(List<TraceEvent> events, int tid, string name)
    {
        foreach (TraceEvent e in events)
        {
            if (e.PH == "M" && e.Name == "thread_name" && e.TID == tid &&
                e.Args != null && e.Args.TryGetValue("name", out object? v) && Equals(v, name))
            {
                return;
            }
        }
        throw new Xunit.Sdk.XunitException($"failed to find thread_name metadata for thread {tid} named '{name}'");
    }

    // Port of TestConcurrentDurationEventsUseSeparateThreadIDs.
    [Fact]
    public void ConcurrentDurationEventsUseSeparateThreadIDs()
    {
        var fs = new AccumulatingFs();
        Tracing tr = Tracing.StartTracing(fs, "/trace", "", deterministic: true);

        Action endA = tr.Push(Phase.Parse, "createSourceFile", Args(("path", "/a.ts")), true);
        Action endB = tr.Push(Phase.Parse, "createSourceFile", Args(("path", "/b.ts")), true);
        endA();
        endB();

        Action endCheck = tr.Push(Phase.Check, "checkSourceFile", Args(("checkerId", 0), ("path", "/a.ts")), true);
        Action endVariance = tr.Push(Phase.CheckTypes, "getVariancesWorker", Args(("checkerId", 0), ("id", 1)), true);
        endVariance();
        endCheck();

        tr.StopTracing();

        Assert.True(fs.TryReadFile("/trace/trace.json", out string traceText));
        List<TraceEvent> events = ParseTrace(traceText);

        TraceEvent aBegin = FindEvent(events, "B", "createSourceFile", "path", "/a.ts");
        TraceEvent aEnd = FindEvent(events, "E", "createSourceFile", "path", "/a.ts");
        TraceEvent bBegin = FindEvent(events, "B", "createSourceFile", "path", "/b.ts");
        TraceEvent bEnd = FindEvent(events, "E", "createSourceFile", "path", "/b.ts");
        Assert.Equal(aBegin.TID, aEnd.TID);
        Assert.Equal(bBegin.TID, bEnd.TID);
        Assert.NotEqual(aBegin.TID, bBegin.TID);
        AssertThreadName(events, aBegin.TID, "file:/a.ts");
        AssertThreadName(events, bBegin.TID, "file:/b.ts");

        TraceEvent checkBegin = FindEvent(events, "B", "checkSourceFile", "path", "/a.ts");
        // Go asserts float64(1); after round-trip the number parses as long 1.
        TraceEvent varianceBegin = FindEvent(events, "B", "getVariancesWorker", "id", 1L);
        Assert.Equal(checkBegin.TID, varianceBegin.TID);
        AssertThreadName(events, checkBegin.TID, "checker:0");

        AssertDurationEventsAreWellNestedByThread(events);
    }

    // Port of TestThreadIDsAreStableAcrossFirstSeenOrder.
    [Fact]
    public void ThreadIDsAreStableAcrossFirstSeenOrder()
    {
        Dictionary<string, int> first = TraceThreadIDsForPaths(["/a.ts", "/b.ts"]);
        Dictionary<string, int> second = TraceThreadIDsForPaths(["/b.ts", "/a.ts"]);
        Assert.Equal(first, second);
    }

    private static Dictionary<string, int> TraceThreadIDsForPaths(string[] paths)
    {
        var fs = new AccumulatingFs();
        Tracing tr = Tracing.StartTracing(fs, "/trace", "", deterministic: true);

        foreach (string path in paths)
        {
            Action end = tr.Push(Phase.Parse, "createSourceFile", Args(("path", path)), true);
            end();
        }

        tr.StopTracing();

        Assert.True(fs.TryReadFile("/trace/trace.json", out string traceText));
        List<TraceEvent> events = ParseTrace(traceText);

        var threadIDs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            threadIDs[path] = FindEvent(events, "B", "createSourceFile", "path", path).TID;
        }
        return threadIDs;
    }

    private static void AssertDurationEventsAreWellNestedByThread(List<TraceEvent> events)
    {
        var stacks = new Dictionary<int, List<TraceEvent>>();
        foreach (TraceEvent e in events)
        {
            if (e.PH == "B")
            {
                if (!stacks.TryGetValue(e.TID, out List<TraceEvent>? s))
                {
                    s = new List<TraceEvent>();
                    stacks[e.TID] = s;
                }
                s.Add(e);
            }
            else if (e.PH == "E")
            {
                List<TraceEvent> s = stacks[e.TID];
                Assert.True(s.Count > 0, $"unmatched end event '{e.Name}' on thread {e.TID}");
                TraceEvent begin = s[^1];
                Assert.Equal(begin.Cat, e.Cat);
                Assert.Equal(begin.Name, e.Name);
                s.RemoveAt(s.Count - 1);
            }
        }
        foreach (KeyValuePair<int, List<TraceEvent>> kv in stacks)
        {
            Assert.True(kv.Value.Count == 0, $"thread {kv.Key} has {kv.Value.Count} unterminated events");
        }
    }

    // The injected sink observes the Push/Pop sequence directly, without disk.
    [Fact]
    public void InjectedSinkObservesPushPopSequence()
    {
        var fs = new AccumulatingFs();
        var sink = new RecordingSink();
        Tracing tr = Tracing.StartTracing(fs, "/trace", "", deterministic: true, sink);

        Action end = tr.Push(Phase.Check, "checkSourceFile", Args(("checkerId", 0)), true);
        end();
        tr.Instant(Phase.Program, "createProgram", Args(("path", "/x.ts")));

        // Header (3 metadata) + thread_name(checker:0) + B + E + thread_name(file:/x.ts) + I.
        // Assert the ordered B → E → I discriminators are present in order.
        var phases = new List<string>();
        foreach (TraceEvent e in sink.Events)
        {
            if (e.PH is "B" or "E" or "I")
            {
                phases.Add(e.PH);
            }
        }
        Assert.Equal(new[] { "B", "E", "I" }, phases);

        // Push on a null Tracing is a safe no-op returning a callable end.
        Tracing? none = null;
        Action noop = none.Push(Phase.Bind, "x", null, true);
        noop(); // must not throw
        none.Instant(Phase.Bind, "y", null); // must not throw
    }

    // Sampled (separateBeginAndEnd=false) events are skipped in deterministic mode.
    [Fact]
    public void SampledEventsSkippedInDeterministicMode()
    {
        var fs = new AccumulatingFs();
        var sink = new RecordingSink();
        Tracing tr = Tracing.StartTracing(fs, "/trace", "", deterministic: true, sink);

        Action end = tr.Push(Phase.Check, "sampledWork", Args(("path", "/s.ts")), false);
        end();
        tr.StopTracing();

        foreach (TraceEvent e in sink.Events)
        {
            Assert.NotEqual("X", e.PH);
            Assert.NotEqual("sampledWork", e.Name);
        }
    }

    // NewTypeTracer + DumpTypes writes a legend and (empty tracer) no types file.
    [Fact]
    public void StopTracingWritesLegend()
    {
        var fs = new AccumulatingFs();
        Tracing tr = Tracing.StartTracing(fs, "/trace", "tsconfig.json", deterministic: true);

        ITracer tracer = tr.NewTypeTracer(0);
        Assert.NotNull(tracer);
        // No types recorded → DumpTypes writes nothing, but legend is still emitted.
        tr.StopTracing();

        Assert.True(fs.TryReadFile("/trace/legend.json", out string legend));
        using JsonDocument doc = JsonDocument.Parse(legend);
        JsonElement first = doc.RootElement[0];
        Assert.Equal("tsconfig.json", first.GetProperty("configFilePath").GetString());
        Assert.Equal(0, first.GetProperty("checkerId").GetInt32());
    }
}
