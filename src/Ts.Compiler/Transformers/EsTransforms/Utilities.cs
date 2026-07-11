// Ported from tsgo internal/transformers/estransforms/utilities.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal static class EsTransformUtilities
{
    internal static Expression ConvertClassDeclarationToClassExpression(
        global::Ts.Printer.EmitContext emitContext,
        ClassDeclaration node)
    {
        var updated = emitContext.Factory.AsNodeFactory().NewClassExpression(
            global::Ts.Transformers.Transformers.ExtractModifiers(
                emitContext,
                node.Modifiers(),
                ~(ModifierFlags.Export | ModifierFlags.Default)),
            node.Name(),
            node.TypeParameters,
            node.HeritageClauses,
            node.Members);
        emitContext.SetOriginal(updated, node.AsNode());
        updated.Loc = node.Loc;
        return updated;
    }

    internal static Node CreateNotNullCondition(
        global::Ts.Printer.EmitContext emitContext,
        Node left,
        Node right,
        bool invert)
    {
        var token = SyntaxKind.ExclamationEqualsEqualsToken;
        var @operator = SyntaxKind.AmpersandAmpersandToken;
        if (invert)
        {
            token = SyntaxKind.EqualsEqualsEqualsToken;
            @operator = SyntaxKind.BarBarToken;
        }

        return emitContext.Factory.NewBinaryExpression(
            null,
            emitContext.Factory.NewBinaryExpression(
                null,
                left,
                null,
                emitContext.Factory.NewToken(token),
                emitContext.Factory.NewKeywordExpression(SyntaxKind.NullKeyword)),
            null,
            emitContext.Factory.NewToken(@operator),
            emitContext.Factory.NewBinaryExpression(
                null,
                right,
                null,
                emitContext.Factory.NewToken(token),
                emitContext.Factory.NewVoidZeroExpression()));
    }

    // TODO(estransforms): Port superAccessState and createAccessorPropertyBackingField when the
    // async/class-fields transforms below are enabled. The current deferred transforms do not
    // consume those helpers, so keeping speculative partial behavior here would be less faithful.
}
