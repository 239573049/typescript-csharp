// Ported from tsgo internal/transformers/estransforms/exponentiation.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class ExponentiationTransformer : Transformer
{
    private ExponentiationTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private Node Visit(Node node)
    {
        // PORT-NOTE: The current C# parser does not yet aggregate this SubtreeFacts bit onto
        // SourceFile. Traverse unconditionally until that infrastructure surface is complete.
        return node.Kind == SyntaxKind.BinaryExpression
            ? VisitBinaryExpression(node.AsBinaryExpression())
            : Visitor().VisitEachChild(node)!;
    }

    private Node VisitBinaryExpression(BinaryExpression node) => node.OperatorToken!.Kind switch
    {
        SyntaxKind.AsteriskAsteriskEqualsToken => VisitExponentiationAssignmentExpression(node),
        SyntaxKind.AsteriskAsteriskToken => VisitExponentiationExpression(node),
        _ => Visitor().VisitEachChild(node.AsNode())!,
    };

    private Node VisitExponentiationAssignmentExpression(BinaryExpression node)
    {
        Node target;
        Node value;
        var left = Visitor().VisitNode(node.Left)!;
        var right = Visitor().VisitNode(node.Right)!;
        if (NodeTests.IsElementAccessExpression(left))
        {
            // Transforms `a[x] **= b` into `(_a = a)[_x = x] = Math.pow(_a[_x], b)`
            var expressionTemp = Factory().NewTempVariable();
            EmitContext().AddVariableDeclaration(expressionTemp);
            var argumentExpressionTemp = Factory().NewTempVariable();
            EmitContext().AddVariableDeclaration(argumentExpressionTemp);

            var objectExpression = Factory().NewAssignmentExpression(expressionTemp, left.Expression()!);
            objectExpression.Loc = left.Expression()!.Loc;
            var accessExpression = Factory().NewAssignmentExpression(
                argumentExpressionTemp,
                left.AsElementAccessExpression().ArgumentExpression!);
            accessExpression.Loc = left.AsElementAccessExpression().ArgumentExpression!.Loc;

            target = Factory().NewElementAccessExpression(objectExpression, null, accessExpression, NodeFlags.None);
            value = Factory().NewElementAccessExpression(expressionTemp, null, argumentExpressionTemp, NodeFlags.None);
            value.Loc = left.Loc;
        }
        else if (NodeTests.IsPropertyAccessExpression(left))
        {
            // Transforms `a.x **= b` into `(_a = a).x = Math.pow(_a.x, b)`
            var expressionTemp = Factory().NewTempVariable();
            EmitContext().AddVariableDeclaration(expressionTemp);
            var assignment = Factory().NewAssignmentExpression(expressionTemp, left.Expression()!);
            assignment.Loc = left.Expression()!.Loc;
            target = Factory().NewPropertyAccessExpression(assignment, null, left.Name(), NodeFlags.None);
            target.Loc = left.Loc;

            value = Factory().NewPropertyAccessExpression(expressionTemp, null, left.Name(), NodeFlags.None);
            value.Loc = left.Loc;
        }
        else
        {
            // Transforms `a **= b` into `a = Math.pow(a, b)`
            target = left;
            value = left;
        }

        var rhs = Factory().NewGlobalMethodCall("Math", "pow", [value, right]);
        rhs.Loc = node.Loc;
        var result = Factory().NewAssignmentExpression(target, rhs);
        result.Loc = node.Loc;
        return result;
    }

    private Node VisitExponentiationExpression(BinaryExpression node)
    {
        var left = Visitor().VisitNode(node.Left)!;
        var right = Visitor().VisitNode(node.Right)!;
        var result = Factory().NewGlobalMethodCall("Math", "pow", [left, right]);
        result.Loc = node.Loc;
        return result;
    }

    internal static Transformer NewExponentiationTransformer(TransformOptions opts) => new ExponentiationTransformer(opts);
}
