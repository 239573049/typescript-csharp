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
// The EmitResolver's .d.ts type-node synthesis half delegates to this builder's SerializeX/
// TypeToTypeNode/SymbolToExpression/IndexInfoToIndexSignatureDeclaration entry points. The
// NodeBuilder wave has only landed TypeToTypeNode's intrinsic core (NodeBuilder/TypeToTypeNode.cs);
// the request-builder serialize surface below is a throwing shell until that wave completes, so the
// EmitResolver Create* methods compile and light up when the real builder lands.
public sealed class NodeBuilder
{
    private readonly Checker checker;
    private readonly global::Ts.Printer.EmitContext emitContext;

    internal NodeBuilder(Checker checker, global::Ts.Printer.EmitContext emitContext)
    {
        this.checker = checker;
        this.emitContext = emitContext;
    }

    // nodebuilder.go:117
    internal Node? SerializeReturnTypeForSignature(Node signatureDeclaration, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.SerializeReturnTypeForSignature");

    // nodebuilder.go:126
    internal Node[] SerializeTypeParametersForSignature(Node signatureDeclaration, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.SerializeTypeParametersForSignature");

    // nodebuilder.go:134
    internal Node? SerializeTypeForDeclaration(Node declaration, Symbol? symbol, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.SerializeTypeForDeclaration");

    // nodebuilder.go:140
    internal Node? SerializeTypeForExpression(Node expr, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.SerializeTypeForExpression");

    // nodebuilder.go:231
    internal Node? SymbolToExpression(Symbol symbol, SymbolFlags meaning, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.SymbolToExpression");

    // nodebuilder.go:111
    internal Node? IndexInfoToIndexSignatureDeclaration(IndexInfo info, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.IndexInfoToIndexSignatureDeclaration");

    // nodebuilder.go:267
    internal Node? TypeToTypeNode(Type type, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.TypeToTypeNode(request)");

    // nodebuilder.go:272
    internal Node? TryJSTypeNodeToTypeNode(Node node, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new System.NotImplementedException("NodeBuilder.TryJSTypeNodeToTypeNode");
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
