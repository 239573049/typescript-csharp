// Supporting types for the Checker skeleton.
//   - Relation: faithful port (relater.go:99-116, plan/03 §7.1).
//   - Relater / InferenceState / FlowState / SharedFlow / EmitResolver / NodeBuilder / Tracer:
//     MINIMAL-BUILD shells — only their field/parameter types are needed now; full ports land in
//     later waves (relater.go, inference.go, flow.go, emitresolver.go, nodebuilder*.go, tracer.go).
//   - Once: sync.Once analogue (plan/05 §5.1; belongs to a later Core once-helper).

using System.Threading;
using Ts.Ast;

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

// relater.go — object-pooled relation engine. Real implementation in Checker.Relater.Engine.cs.

// inference.go:16-31 — inference recursion state: ported in Checker.Inference.Support.cs
// (InferenceState with its full field set); this skeleton shell was removed by the inference wave.

// flow.go — FlowType / FlowState / SharedFlow are fully ported in Checker.Flow.cs (flow wave);
// the skeleton shells that lived here were removed by that wave.

// emitresolver.go:34 — public emit-time resolver. FULLY PORTED in Checker.EmitResolver.cs +
// Checker.EmitResolver.Synthesis.cs (the class wraps a Checker and implements
// global::Ts.Printer.EmitResolver, faithful to the Go `EmitResolver` struct).

// nodebuilder.go:279 — request-scoped type/symbol → node serializer (NewNodeBuilder(ch, e)).
// The real request-builder (NodeBuilder + its NodeBuilderContext + the breadth-first serialize
// surface) now lives in src/Ts.Compiler/NodeBuilder/NodeBuilderImpl.cs (Go: package checker →
// namespace Ts.Checker); the throwing shell that lived here was removed by the phase-6 NodeBuilder
// request-builder wave.

// tracer.go:13-31 — the --generateTrace tracer lives in Tracer.cs (full port: tracing
// session + per-checker type recorder + Type→ITracedType adapter).

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
