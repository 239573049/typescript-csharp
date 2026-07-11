using Ts.Core;

namespace Ts.Ast;

// Go: internal/ast/deepclone.go — deep-clone visitors on NodeFactory.
public sealed partial class NodeFactory
{
    // deepclone.go:6-69 — Ideally, this would get cached on the node factory so there's
    // only ever one set of closures made per factory.
    private static NodeVisitor GetDeepCloneVisitor(NodeFactory f, bool syntheticLocation)
    {
        NodeVisitor visitor = null!;
        visitor = new NodeVisitor(
            node =>
            {
                var visited = visitor.VisitEachChild(node);
                if (visited != node)
                {
                    if (syntheticLocation)
                    {
                        visited!.Loc = new TextRange(-1, -1);
                    }
                    return visited;
                }
                var c = node.Clone(f); // forcibly clone leaf nodes, which will then cascade new nodes/arrays upwards via `update` calls
                // In strada, `factory.cloneNode` was dynamic and did _not_ clone positions for any "special cases", meanwhile
                // Node.Clone in corsa reliably uses `Update` calls for all nodes and so copies locations by default.
                // Deep clones are done to copy a node across files, so here, we explicitly make the location range synthetic on all cloned nodes
                if (syntheticLocation)
                {
                    c!.Loc = new TextRange(-1, -1);
                }
                return c;
            },
            f,
            new NodeVisitorHooks
            {
                VisitNodes = (nodes, v) =>
                {
                    if (nodes == null)
                    {
                        return null;
                    }
                    var visited = v.VisitNodes(nodes);
                    NodeList newList;
                    if (visited != nodes)
                    {
                        newList = visited!;
                    }
                    else
                    {
                        newList = nodes.Clone(v.Factory);
                    }
                    if (syntheticLocation)
                    {
                        newList.Loc = new TextRange(-1, -1);
                        if (nodes.HasTrailingComma())
                        {
                            newList.Nodes[newList.Nodes.Length - 1].Loc = new TextRange(-2, -2);
                        }
                    }
                    return newList;
                },
                VisitModifiers = (nodes, v) =>
                {
                    if (nodes == null)
                    {
                        return null;
                    }
                    var visited = v.VisitModifiers(nodes);
                    ModifierList newList;
                    if (visited != nodes)
                    {
                        newList = visited!;
                    }
                    else
                    {
                        newList = nodes.Clone(v.Factory);
                    }
                    if (syntheticLocation)
                    {
                        newList.Loc = new TextRange(-1, -1);
                        if (nodes.HasTrailingComma())
                        {
                            newList.Nodes[newList.Nodes.Length - 1].Loc = new TextRange(-2, -2);
                        }
                    }
                    return newList;
                },
            });
        return visitor;
    }

    // deepclone.go:71-73
    public Node? DeepCloneNode(Node node)
    {
        return GetDeepCloneVisitor(this, true /*syntheticLocation*/).VisitNode(node);
    }

    // deepclone.go:75-82
    public Node? DeepCloneReparse(Node? node)
    {
        if (node != null)
        {
            node = GetDeepCloneVisitor(this, false /*syntheticLocation*/).VisitNode(node);
            AstUtilities.SetParentInChildren(node!);
            node!.Flags |= NodeFlags.Reparsed;
        }
        return node;
    }

    // deepclone.go:84-86
    public ModifierList? DeepCloneReparseModifiers(ModifierList? modifiers)
    {
        return GetDeepCloneVisitor(this, false /*syntheticLocation*/).VisitModifiers(modifiers);
    }
}
