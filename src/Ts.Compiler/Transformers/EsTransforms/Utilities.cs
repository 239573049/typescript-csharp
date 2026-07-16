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

    // estransforms/utilities.go:280 — createAccessorPropertyBackingField creates a private backing
    // field for an `accessor` PropertyDeclaration.
    internal static Node CreateAccessorPropertyBackingField(global::Ts.Printer.NodeFactory f, PropertyDeclaration node, ModifierList? modifiers, Expression? initializer)
    {
        return f.AsNodeFactory().UpdatePropertyDeclaration(
            node,
            modifiers,
            f.NewGeneratedPrivateNameForNodeEx(node.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" }),
            null, /*postfixToken*/
            null, /*typeNode*/
            initializer);
    }

    // TODO(estransforms): Port superAccessState when the async transforms below are enabled.
}
