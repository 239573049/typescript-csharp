// Ported from tsgo internal/checker/tracer_test.go (whole file).
//
// PORT-NOTEs:
//  * Go json.Unmarshal into []testTraceEvent → JsonDocument walk (AOT policy: no reflection-based
//    deserialization, plan/07); testTraceEvent keeps the three fields the test reads.
//  * Go `args["variances"] = []string{"out"}` / DeepEqual([]any{"out"}) → string[] in, JSON array out.

using System.Collections.Generic;
using System.Text.Json;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Checker.Tests;

public class TracerTests
{
    // tracer_test.go:14-52
    [Fact]
    public void TestTracerPushPreservesEndArgMutations()
    {
        var fsys = VfsTestUtil.FromMap(new Dictionary<string, MapFile>
        {
            ["/trace"] = new MapFile { Mode = FileMode.Dir },
        }, true);

        var tr = global::Ts.Tracing.Tracing.StartTracing(fsys, "/trace", "", true /*deterministic*/);

        var args = new Dictionary<string, object?> { ["id"] = 1 };
        var tracer = Tracer.NewTracer(tr, 7);
        var pop = tracer.Push(global::Ts.Tracing.Phase.CheckTypes, "getVariancesWorker", args, true);
        Assert.False(args.ContainsKey("checkerId"));

        args["variances"] = new[] { "out" };
        pop();
        Assert.False(args.ContainsKey("checkerId"));

        tr.StopTracing();

        Assert.True(fsys.TryReadFile("/trace/trace.json", out string traceText));

        using var doc = JsonDocument.Parse(traceText);
        var events = new List<TestTraceEvent>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            events.Add(new TestTraceEvent(e));
        }

        var beginEvent = FindTestTraceEvent(events, "B", "getVariancesWorker");
        Assert.Equal(7.0, beginEvent.Arg("checkerId")!.Value.GetDouble());
        Assert.Null(beginEvent.Arg("variances"));

        var endEvent = FindTestTraceEvent(events, "E", "getVariancesWorker");
        Assert.Equal(7.0, endEvent.Arg("checkerId")!.Value.GetDouble());
        var variances = endEvent.Arg("variances");
        Assert.True(variances is { ValueKind: JsonValueKind.Array });
        var items = new List<string?>();
        foreach (var v in variances!.Value.EnumerateArray())
        {
            items.Add(v.GetString());
        }
        Assert.Equal(new string?[] { "out" }, items);
    }

    // tracer_test.go:54-58
    private sealed class TestTraceEvent
    {
        public readonly string PH;
        public readonly string Name;
        private readonly JsonElement element;

        public TestTraceEvent(JsonElement element)
        {
            this.element = element;
            PH = element.TryGetProperty("ph", out var ph) ? ph.GetString() ?? "" : "";
            Name = element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
        }

        // Go: event.Args[key] (nil when args or the key is absent).
        public JsonElement? Arg(string key)
        {
            if (element.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out var v))
            {
                return v;
            }
            return null;
        }
    }

    // tracer_test.go:60-69
    private static TestTraceEvent FindTestTraceEvent(List<TestTraceEvent> events, string phase, string name)
    {
        foreach (var e in events)
        {
            if (e.PH == phase && e.Name == name)
            {
                return e;
            }
        }
        Assert.Fail($"failed to find {phase} event \"{name}\"");
        return null!;
    }
}
