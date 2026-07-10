using System.Threading;

namespace Ts.Ast;

// Go: internal/ast/ids.go — defined types NodeId/SymbolId (structured keys per
// plan/02 §4.1).
public readonly record struct NodeId(ulong Value);

public readonly record struct SymbolId(ulong Value);

// Go: internal/ast/utilities.go:14-43 — GetNodeId/GetSymbolId with their package
// counters. Host class Ts.Ast.AstUtilities per plan/02 §9 (partial: Utilities.cs
// carries the rest of the phase-0 utilities.go surface).
public static partial class AstUtilities
{
    private static ulong nextNodeId;
    private static ulong nextSymbolId;

    // utilities.go:22-31
    public static NodeId GetNodeId(Node node)
    {
        var id = Volatile.Read(ref node.id);
        if (id == 0)
        {
            // Worst case, we burn a few ids if we have to CAS.
            id = Interlocked.Increment(ref nextNodeId);
            var seen = Interlocked.CompareExchange(ref node.id, id, 0);
            if (seen != 0)
            {
                id = seen;
            }
        }
        return new NodeId(id);
    }

    // utilities.go:33-43
    public static SymbolId GetSymbolId(Symbol symbol)
    {
        var id = Volatile.Read(ref symbol.id);
        if (id == 0)
        {
            // Worst case, we burn a few ids if we have to CAS.
            id = Interlocked.Increment(ref nextSymbolId);
            var seen = Interlocked.CompareExchange(ref symbol.id, id, 0);
            if (seen != 0)
            {
                id = seen;
            }
        }
        return new SymbolId(id);
    }
}
