using System;
using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/ast.go:60-64
public struct NodeFactoryHooks
{
    public Action<Node>? OnCreate; // Hooks the creation of a node.
    public Action<Node, Node>? OnUpdate; // Hooks the updating of a node: (updated, original).
    public Action<Node, Node>? OnClone; // Hooks the cloning of a node: (updated, original).
}

// Go: internal/ast/ast.go:66-68. No `I` prefix: ast-package interfaces keep their Go
// names for grep parity (plan/02 §17-5 leaning; mixin precedent plan/05 §1.3).
public interface NodeFactoryCoercible
{
    NodeFactory AsNodeFactory();
}

// Hand-written half of NodeFactory (ast.go:60-119 plus the hand-written factories
// ast.go:128/159/1671); the generated half (fields + New*/Update*) is in
// Ast/NodeFactory.g.cs. Arena machinery is gone (GC replaces arenas, plan/04 §2.2 #2);
// ReleaseArenas (ast.go:3053-3059) is dropped with it (plan/02 §17-7).
public sealed partial class NodeFactory
    : NodeFactoryCoercible
{
    public NodeFactory()
    {
    }

    // ast.go:70-72
    public NodeFactory(NodeFactoryHooks hooks)
    {
        this.hooks = hooks;
    }

    // ast.go:74-83 (newNode free function). Go's `n.data = data` wiring dissolved:
    // the node object is its own data (plan/02 §1.2).
    internal static Node InitNode(SyntaxKind kind, Node n, NodeFactoryHooks hooks)
    {
        n.Loc = TextRange.Undefined;
        n.Kind = kind;
        hooks.OnCreate?.Invoke(n);
        return n;
    }

    // ast.go:85-88
    private Node NewNode(SyntaxKind kind, Node data)
    {
        nodeCount++;
        return InitNode(kind, data, hooks);
    }

    public int NodeCount() => nodeCount; // ast.go:90-92
    public int TextCount() => textCount; // ast.go:94-96
    public NodeFactory AsNodeFactory() => this; // ast.go:98-100

    // ast.go:102-111
    internal static Node UpdateNode(Node updated, Node original, NodeFactoryHooks hooks)
    {
        if (updated != original)
        {
            updated.Flags = original.Flags;
            updated.Loc = original.Loc;
            hooks.OnUpdate?.Invoke(updated, original);
        }
        return updated;
    }

    // ast.go:113-119
    internal static Node CloneNode(Node updated, Node original, NodeFactoryHooks hooks)
    {
        UpdateNode(updated, original, hooks);
        if (updated != original)
        {
            hooks.OnClone?.Invoke(updated, original);
        }
        return updated;
    }

    // ast.go:128-133 (the NodeList constructor already sets Loc = Undefined)
    public NodeList NewNodeList(Node[] nodes) => new(nodes);

    // ast.go:159-165
    public ModifierList NewModifierList(Node[] nodes)
    {
        var list = new ModifierList(nodes)
        {
            ModifierFlags = AstUtilities.ModifiersToFlags(nodes),
        };
        return list;
    }

    // ast.go:1671-1673
    public Node NewModifier(SyntaxKind kind) => NewToken(kind);
}
