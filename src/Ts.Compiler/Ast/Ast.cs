namespace Ts.Ast;

// Go: internal/ast/ast.go — partial port (phase 0): only the hand-written visitor
// bridges for nodes with runtime-dependent child ordering (ast.go:3037-3051), which
// the generated ForEachChild/VisitEachChild delegate to (plan/04 §2.2 #9). The rest
// of ast.go (switch accessors, subtree-facts implementations, GetDeclarationFromName,
// …) lands in phase 3, keeping Go source order within that port.
public abstract partial class Node
{
    // ast.go:3040-3047
    internal static bool ForEachChild_JSDocParameterOrPropertyTag(JSDocParameterOrPropertyTag node, Visitor v)
    {
        return Visit(v, node.TagName) ||
            (node.IsNameFirst &&
                (Visit(v, node.name) || Visit(v, node.TypeExpression))) ||
            (!node.IsNameFirst &&
                (Visit(v, node.TypeExpression) || Visit(v, node.name))) ||
            VisitNodeList(v, node.Comment);
    }

    // ast.go:3049-3051
    internal static Node VisitEachChild_JSDocParameterOrPropertyTag(JSDocParameterOrPropertyTag node, NodeVisitor v)
    {
        return v.Factory.UpdateJSDocParameterOrPropertyTag(node, v.visitNode(node.TagName), v.visitNode(node.name), node.IsBracketed, v.visitNode(node.TypeExpression), node.IsNameFirst, v.visitNodes(node.Comment));
    }
}
