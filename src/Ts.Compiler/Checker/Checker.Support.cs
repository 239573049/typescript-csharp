// Supporting types for the Checker skeleton.
//   - Relation: faithful port (relater.go:99-116, plan/03 §7.1).
//   - Relater / InferenceState / FlowState / SharedFlow / EmitResolver / NodeBuilder / Tracer:
//     MINIMAL-BUILD shells — only their field/parameter types are needed now; full ports land in
//     later waves (relater.go, inference.go, flow.go, emitresolver.go, nodebuilder*.go, tracer.go).
//   - Once: sync.Once analogue (plan/05 §5.1; belongs to a later Core once-helper).

using System.Threading;

namespace Ts.Checker;

// relater.go:99-116 (plan/03 §7.1)
internal sealed class Relation
{
    private Dictionary<CacheHashKey, RelationComparisonResult>? results; // lazy

    // relater.go:103-105 — miss → zero value None.
    public RelationComparisonResult Get(CacheHashKey key) =>
        results is null ? RelationComparisonResult.None : results.GetValueOrDefault(key);

    // relater.go:107-112
    public void Set(CacheHashKey key, RelationComparisonResult result)
    {
        results ??= new Dictionary<CacheHashKey, RelationComparisonResult>();
        results[key] = result;
    }

    // relater.go:114-116
    public int Size => results?.Count ?? 0;
}

// relater.go — object-pooled relation engine. Shell for skeleton.
internal sealed class Relater
{
}

// inference.go — inference recursion state. Shell for skeleton.
internal sealed class InferenceState
{
}

// flow.go — control-flow narrowing state. Shell for skeleton.
internal sealed class FlowState
{
}

// flow.go:35 — SharedFlow{flow *ast.FlowNode; flowType FlowType}. flowType deferred to the
// flow.go wave (FlowType is a union type not yet ported).
internal struct SharedFlow
{
    internal Ts.Ast.FlowNode? flow;
}

// emitresolver.go:34 — public emit-time resolver. Shell for skeleton (phase 6).
public sealed class EmitResolver
{
}

// nodebuilder.go:10 — type/symbol → node serializer. Shell for skeleton.
public sealed class NodeBuilder
{
}

// tracer.go:13-31 — --generateTrace tracer (phase 9). Minimal shell + factory.
public sealed class Tracer
{
    // tracer.go:21
    public static Tracer NewTracer(global::Ts.Tracing.Tracing tracing, int checkerIndex) => new();

    // Records a type for trace output. Full body deferred (needs Type→ITracedType adapter).
    internal void RecordType(Type type)
    {
    }

    internal void DumpTypes()
    {
    }
}

// sync.Once analogue (plan/05 §5.1).
internal sealed class Once
{
    private readonly Lock gate = new();
    private bool done;

    public void Do(Action action)
    {
        if (done)
        {
            return;
        }
        lock (gate)
        {
            if (!done)
            {
                action();
                done = true;
            }
        }
    }
}
