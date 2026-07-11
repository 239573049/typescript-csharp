// Ported from tsgo internal/transformers/estransforms/classthis.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal static class ClassThisUtilities
{
    // Gets whether a node is a `static {}` block containing only a single assignment of the static `this` to the `_classThis`
    // (or similar) variable stored in the `classthis` property of the block's `EmitNode`.
    internal static bool IsClassThisAssignmentBlock(global::Ts.Printer.EmitContext emitContext, Node node)
    {
        if (NodeTests.IsClassStaticBlockDeclaration(node))
        {
            var body = node.AsClassStaticBlockDeclaration().Body!.AsBlock();
            if (body.Statements!.Nodes.Length == 1)
            {
                var statement = body.Statements.Nodes[0];
                if (NodeTests.IsExpressionStatement(statement))
                {
                    var expression = statement.Expression()!;
                    if (AstUtilities.IsAssignmentExpression(expression, true))
                    {
                        var binary = expression.AsBinaryExpression();
                        return NodeTests.IsIdentifier(binary.Left!) &&
                            ReferenceEquals(emitContext.ClassThis(node), binary.Left) &&
                            binary.Right!.Kind == SyntaxKind.ThisKeyword;
                    }
                }
            }
        }
        return false;
    }
}
