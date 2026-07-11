// EventSource for the .NET-native observability surface (EventPipe / dotnet-trace).
//
// PORT-NOTE: 非 1:1，EventSource 重写 (plan/06 §9.2). tsgo emits Chrome trace_event JSON;
// the plan's C# 归宿 routes the *event stream* onto System.Diagnostics.Tracing.EventSource
// so it is consumable by EventPipe (`dotnet-trace collect --providers Ts-Tracing`) and
// works under Native AOT. The byte-faithful trace.json is written separately by Tracing
// (for --generateTrace / test baselines); this type is the live counterpart.
//
// AOT note (plan/07): EventSource is AOT-compatible when events are declared as explicit
// [Event]-annotated methods with primitive/string parameters (no reflection over payload
// types). We keep every payload primitive/string. The manifest is generated at build via
// the source-generated EventSource support; no runtime reflection of user types occurs.

using System.Diagnostics.Tracing;

namespace Ts.Tracing;

// PORT-NOTE: EventSourceName is stable ("Ts-Tracing") so external collectors can target
// it; it has no Go equivalent.
[EventSource(Name = "Ts-Tracing")]
internal sealed class TscTraceEventSource : EventSource
{
    internal static readonly TscTraceEventSource Log = new();

    private TscTraceEventSource()
    {
    }

    // TraceEvent mirrors one Chrome trace_event record's discriminating fields.
    //   ph  — phase ("B"/"E"/"I"/"X"/"M")
    //   cat — category (the tracing Phase value, e.g. "check")
    //   name, tid, ts as in the Chrome event.
    [Event(1, Level = EventLevel.Informational)]
    internal void TraceEvent(string ph, string cat, string name, int tid, double ts)
    {
        if (IsEnabled())
        {
            WriteEvent(1, ph, cat, name, tid, ts);
        }
    }
}
