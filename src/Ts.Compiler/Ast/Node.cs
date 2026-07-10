using System.Threading;
using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/ast.go — Visitor (ast.go:28) and the visit helpers (ast.go:30-58).
public delegate bool Visitor(Node node);

// Go: internal/ast/ast.go — the Node header (ast.go:179-186), the nodeData virtual
// table (ast.go:1181-1202), the NodeDefault default bodies (ast.go:1206-1245) and
// NodeBase (ast.go:1249-1251), collapsed into a single abstract class (plan/02 §1.2:
// C# inheritance replaces the Go header+data compensation; the data pointer vanishes).
//
// Phase-0 scope: only the members the generated files (Ast/*.g.cs) need. The rest of
// the hand-written Node surface (IterChildren, Contains, JSDoc/EagerJSDoc, the
// convenience accessors Symbol/LocalSymbol/Locals/Body/Decorators/ParameterList/…,
// AsMutable and the §4.4 switch accessors) lands with the phase-3 ast package port —
// note that some of those introduce member names that will require `new`/shadowing
// decisions against generated mixin properties (tracked in the phase-0 work log).
public abstract partial class Node
{
    public SyntaxKind Kind;
    public NodeFlags Flags;
    public TextRange Loc; // positions are UTF-16 code unit offsets (plan/01)
    internal ulong id; // Go atomic.Uint64; accessed via Volatile/Interlocked in AstUtilities.GetNodeId
    public Node? Parent;

    public Node AsNode() => this; // ast.go:192
    public int Pos => Loc.Pos; // ast.go:193
    public int End => Loc.End; // ast.go:194
    public string KindString() => Kind.ToString(); // ast.go:226 — debug only; enum ToString is AOT-whitelisted
    public short KindValue() => (short)Kind; // ast.go:227

    // ── nodeData virtual table (ast.go:1181-1202) with NodeDefault bodies (plan/02 §4.2) ──

    public virtual bool ForEachChild(Visitor v) => false; // ast.go:1211
    public virtual Node VisitEachChild(NodeVisitor v) => this; // ast.go:1213
    public virtual Node? Clone(NodeFactoryCoercible f) => null; // ast.go:1214
    public virtual DeclarationName? Name() => null; // ast.go:1215
    public virtual ModifierList? Modifiers() => null; // ast.go:1216
    internal virtual void SetModifiers(ModifierList? modifiers) { } // ast.go:1217
    public virtual FlowNodeBase? FlowNodeData() => null; // ast.go:1218
    public virtual DeclarationBase? DeclarationData() => null; // ast.go:1219
    public virtual ExportableBase? ExportableData() => null; // ast.go:1220
    public virtual LocalsContainerBase? LocalsContainerData() => null; // ast.go:1221
    public virtual FunctionLikeBase? FunctionLikeData() => null; // ast.go:1222
    public virtual ClassLikeBase? ClassLikeData() => null; // ast.go:1223
    public virtual BodyBase? BodyData() => null; // ast.go:1224
    public virtual LiteralLikeNodeBase? LiteralLikeData() => null; // ast.go:1225
    public virtual TemplateLiteralLikeNodeBase? TemplateLiteralLikeData() => null; // ast.go:1226

    // Subtree-facts family (ast.go:1227-1245). Method name == enum type name is kept
    // per review verdict R9; expression-position references are Ts.Ast-qualified
    // (plan/02 §17-3).
    public virtual SubtreeFacts SubtreeFacts() => SubtreeFactsWorker(); // ast.go:1227-1229
    internal virtual SubtreeFacts SubtreeFactsWorker() => ComputeSubtreeFacts(); // ast.go:1231-1237 (`self` parameter dissolved: C# virtual dispatch, plan/02 §3.4)
    internal virtual SubtreeFacts ComputeSubtreeFacts() => Ts.Ast.SubtreeFacts.None; // ast.go:1239-1241
    internal virtual SubtreeFacts PropagateSubtreeFacts() => SubtreeFacts() & ~Ts.Ast.SubtreeFacts.SubtreeExclusionsNode; // ast.go:1243-1245

    // CompositeBase cached worker body (ast.go:1599-1607), shared by the generated
    // per-introduction-class overrides (plan/02 §3.4).
    // computeSubtreeFacts() is expected to be idempotent, so races will only impact
    // time, not correctness (Go comment, ast.go:1600).
    private protected SubtreeFacts CachedSubtreeFacts(ref uint facts)
    {
        var f = (SubtreeFacts)Volatile.Read(ref facts);
        if ((f & Ts.Ast.SubtreeFacts.Computed) == 0)
        {
            f |= ComputeSubtreeFacts() | Ts.Ast.SubtreeFacts.Computed;
            Volatile.Write(ref facts, (uint)f);
        }
        return f & ~Ts.Ast.SubtreeFacts.Computed;
    }

    // ── package-level visit helpers (ast.go:30-58) ──
    // Statics on Node so generated ForEachChild bodies call them unqualified,
    // matching the Go call text.

    internal static bool Visit(Visitor v, Node? node)
    {
        if (node != null)
        {
            return v(node);
        }
        return false;
    }

    internal static bool VisitNodes(Visitor v, Node[]? nodes)
    {
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                if (v(node))
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool VisitNodeList(Visitor v, NodeList? nodeList)
    {
        if (nodeList != null)
        {
            return VisitNodes(v, nodeList.Nodes);
        }
        return false;
    }

    internal static bool VisitModifiers(Visitor v, ModifierList? modifiers)
    {
        if (modifiers != null)
        {
            return VisitNodes(v, modifiers.Nodes);
        }
        return false;
    }
}
