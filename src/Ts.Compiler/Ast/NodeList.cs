using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/ast.go:121-150 — NodeList. A class, not a struct: Update* factories
// compare lists by reference identity (Go `*NodeList` pointer identity), plan/02 §11.
public class NodeList
{
    public TextRange Loc;
    public Node[] Nodes;

    public NodeList(Node[] nodes)
    {
        Loc = TextRange.Undefined;
        Nodes = nodes;
    }

    public int Pos => Loc.Pos; // ast.go:135
    public int End => Loc.End; // ast.go:136

    // ast.go:138-144
    public bool HasTrailingComma()
    {
        if (Nodes.Length == 0)
        {
            return false;
        }
        var last = Nodes[Nodes.Length - 1];
        return last.End < End;
    }

    // ast.go:146-150
    public NodeList Clone(NodeFactoryCoercible f)
    {
        var result = f.AsNodeFactory().NewNodeList(Nodes);
        result.Loc = Loc;
        return result;
    }
}

// Go: internal/ast/ast.go:152-173 — ModifierList embeds NodeList + ModifierFlags.
public sealed class ModifierList : NodeList
{
    public ModifierFlags ModifierFlags;

    public ModifierList(Node[] nodes)
        : base(nodes)
    {
    }

    // ast.go:167-173 — Go declares a distinct, non-virtual Clone(*NodeFactory); the
    // different parameter type keeps it a plain overload here (no shadowing).
    public ModifierList Clone(NodeFactory f)
    {
        var res = new ModifierList(Nodes)
        {
            Loc = Loc,
            ModifierFlags = ModifierFlags,
        };
        return res;
    }
}
