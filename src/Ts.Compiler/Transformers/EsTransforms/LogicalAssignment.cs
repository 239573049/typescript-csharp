// Ported from tsgo internal/transformers/estransforms/logicalassignment.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class LogicalAssignmentTransformer : Transformer
{
    private LogicalAssignmentTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private Node Visit(Node node)
    {
        // PORT-NOTE: The current C# parser does not yet aggregate this SubtreeFacts bit onto
        // SourceFile. Traverse unconditionally until that infrastructure surface is complete.
        return node.Kind == SyntaxKind.BinaryExpression
            ? VisitBinaryExpression(node.AsBinaryExpression())
            : Visitor().VisitEachChild(node)!;
    }

    private Node VisitBinaryExpression(BinaryExpression node)
    {
        var nonAssignmentOperator = node.OperatorToken!.Kind switch
        {
            SyntaxKind.BarBarEqualsToken => SyntaxKind.BarBarToken,
            SyntaxKind.AmpersandAmpersandEqualsToken => SyntaxKind.AmpersandAmpersandToken,
            SyntaxKind.QuestionQuestionEqualsToken => SyntaxKind.QuestionQuestionToken,
            _ => SyntaxKind.Unknown,
        };
        if (nonAssignmentOperator == SyntaxKind.Unknown)
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }

        var left = AstUtilities.SkipParentheses(Visitor().VisitNode(node.Left)!);
        Node assignmentTarget = left;
        var right = AstUtilities.SkipParentheses(Visitor().VisitNode(node.Right)!);

        if (AstUtilities.IsAccessExpression(left))
        {
            var propertyAccessTargetSimpleCopiable = global::Ts.Transformers.Transformers.IsSimpleCopiableExpression(left.Expression()!);
            var propertyAccessTarget = left.Expression()!;
            var propertyAccessTargetAssignment = left.Expression()!;
            if (!propertyAccessTargetSimpleCopiable)
            {
                propertyAccessTarget = Factory().NewTempVariable();
                EmitContext().AddVariableDeclaration(propertyAccessTarget);
                propertyAccessTargetAssignment = Factory().NewAssignmentExpression(propertyAccessTarget, left.Expression()!);
            }

            if (NodeTests.IsPropertyAccessExpression(left))
            {
                assignmentTarget = Factory().NewPropertyAccessExpression(propertyAccessTarget, null, left.Name(), NodeFlags.None);
                left = Factory().NewPropertyAccessExpression(propertyAccessTargetAssignment, null, left.Name(), NodeFlags.None);
            }
            else
            {
                var argument = left.AsElementAccessExpression().ArgumentExpression!;
                var elementAccessArgumentSimpleCopiable = global::Ts.Transformers.Transformers.IsSimpleCopiableExpression(argument);
                Node elementAccessArgument = argument;
                Node argumentExpression = argument;
                if (!elementAccessArgumentSimpleCopiable)
                {
                    elementAccessArgument = Factory().NewTempVariable();
                    EmitContext().AddVariableDeclaration(elementAccessArgument);
                    argumentExpression = Factory().NewAssignmentExpression(elementAccessArgument, argument);
                }

                assignmentTarget = Factory().NewElementAccessExpression(propertyAccessTarget, null, elementAccessArgument, NodeFlags.None);
                left = Factory().NewElementAccessExpression(propertyAccessTargetAssignment, null, argumentExpression, NodeFlags.None);
            }
        }

        return Factory().NewBinaryExpression(
            null,
            left,
            null,
            Factory().NewToken(nonAssignmentOperator),
            Factory().NewParenthesizedExpression(Factory().NewAssignmentExpression(assignmentTarget, right)));
    }

    internal static Transformer NewLogicalAssignmentTransformer(TransformOptions opts) => new LogicalAssignmentTransformer(opts);
}
