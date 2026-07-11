// Ported from tsgo internal/transformers/estransforms/optionalcatch.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class OptionalCatchTransformer : Transformer
{
    private OptionalCatchTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private Node Visit(Node node)
    {
        // PORT-NOTE: The current C# parser does not yet aggregate this SubtreeFacts bit onto
        // SourceFile. Traverse unconditionally until that infrastructure surface is complete.
        return node.Kind == SyntaxKind.CatchClause
            ? VisitCatchClause(node.AsCatchClause())
            : Visitor().VisitEachChild(node)!;
    }

    private Node VisitCatchClause(CatchClause node)
    {
        if (node.VariableDeclaration is null)
        {
            return Factory().AsNodeFactory().NewCatchClause(
                Factory().NewVariableDeclaration(Factory().NewTempVariable(), null, null, null),
                Visitor().VisitNode(node.Block));
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    internal static Transformer NewOptionalCatchTransformer(TransformOptions opts) => new OptionalCatchTransformer(opts);
}
