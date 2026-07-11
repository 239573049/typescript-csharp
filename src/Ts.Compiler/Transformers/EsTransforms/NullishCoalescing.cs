// Ported from tsgo internal/transformers/estransforms/nullishcoalescing.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class NullishCoalescingTransformer : Transformer
{
    private NullishCoalescingTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private Node Visit(Node node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsNullishCoalescing) == 0)
        {
            return node;
        }

        return node.Kind == SyntaxKind.BinaryExpression
            ? VisitBinaryExpression(node.AsBinaryExpression())
            : Visitor().VisitEachChild(node)!;
    }

    private Node VisitBinaryExpression(BinaryExpression node)
    {
        if (node.OperatorToken!.Kind != SyntaxKind.QuestionQuestionToken)
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }

        var left = Visitor().VisitNode(node.Left)!;
        var right = left;
        if (!global::Ts.Transformers.Transformers.IsSimpleCopiableExpression(left))
        {
            right = Factory().NewTempVariable();
            EmitContext().AddVariableDeclaration(right);
            left = Factory().NewAssignmentExpression(right, left);
        }

        return Factory().NewConditionalExpression(
            EsTransformUtilities.CreateNotNullCondition(EmitContext(), left, right, false),
            Factory().NewToken(SyntaxKind.QuestionToken),
            right,
            Factory().NewToken(SyntaxKind.ColonToken),
            Visitor().VisitNode(node.Right));
    }

    internal static Transformer NewNullishCoalescingTransformer(TransformOptions opts) => new NullishCoalescingTransformer(opts);
}
