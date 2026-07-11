// Ported from tsgo internal/transformers/inliners/constenum.go.

using System;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.Inliners;

public sealed class ConstEnumInliningTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;
    private SourceFile? currentSourceFile;
    private readonly global::Ts.Printer.EmitResolver emitResolver;

    private ConstEnumInliningTransformer(TransformOptions options)
    {
        compilerOptions = options.CompilerOptions!;
        currentSourceFile = null;
        if (compilerOptions.GetIsolatedModules())
        {
            global::Ts.Core.Debug.Fail("const enums are not inlined under isolated modules");
        }
        emitResolver = options.EmitResolver!;
        NewTransformer(Visit, options.Context);
    }

    public static Transformer NewConstEnumInliningTransformer(TransformOptions options) =>
        new ConstEnumInliningTransformer(options);

    private Node? Visit(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                var parse = EmitContext().ParseNode(node);
                if (parse is null)
                {
                    return Visitor().VisitEachChild(node);
                }
                var value = emitResolver.GetConstantValue(parse);
                if (value is not null)
                {
                    Node? replacement = null;
                    switch (value)
                    {
                        case global::Ts.JsNum.Number number:
                            if (number.IsInf())
                            {
                                replacement = number.Abs() == number
                                    ? Factory().NewIdentifier("Infinity")
                                    : Factory().NewPrefixUnaryExpression(SyntaxKind.MinusToken, Factory().NewIdentifier("Infinity"));
                            }
                            else if (number.IsNaN())
                            {
                                replacement = Factory().NewIdentifier("NaN");
                            }
                            else if (number.Abs() == number)
                            {
                                replacement = Factory().NewNumericLiteral(number.ToString(), TokenFlags.None);
                            }
                            else
                            {
                                replacement = Factory().NewPrefixUnaryExpression(
                                    SyntaxKind.MinusToken,
                                    Factory().NewNumericLiteral(number.Abs().ToString(), TokenFlags.None));
                            }
                            break;
                        case string text:
                            replacement = Factory().NewStringLiteral(text, TokenFlags.None);
                            break;
                        case global::Ts.JsNum.PseudoBigInt pseudoBigInt:
                            if (pseudoBigInt == default)
                            {
                                replacement = Factory().NewBigIntLiteral("0", TokenFlags.None);
                            }
                            else if (!pseudoBigInt.Negative)
                            {
                                replacement = Factory().NewBigIntLiteral(pseudoBigInt.Base10Value, TokenFlags.None);
                            }
                            else
                            {
                                replacement = Factory().NewPrefixUnaryExpression(
                                    SyntaxKind.MinusToken,
                                    Factory().NewBigIntLiteral(pseudoBigInt.Base10Value, TokenFlags.None));
                            }
                            break;
                    }

                    if (compilerOptions.RemoveComments.IsFalseOrUnknown())
                    {
                        var original = EmitContext().MostOriginal(node);
                        if (original is not null && !global::Ts.Ast.AstUtilities.NodeIsSynthesized(original))
                        {
                            var originalText = global::Ts.Scanner.Scanner.GetTextOfNode(original);
                            var escapedText = SafeMultiLineComment(originalText);
                            EmitContext().AddSyntheticTrailingComment(
                                replacement!, SyntaxKind.MultiLineCommentTrivia, escapedText, false);
                        }
                    }
                    return replacement;
                }
                return Visitor().VisitEachChild(node);
        }
        _ = currentSourceFile;
        return Visitor().VisitEachChild(node);
    }

    internal static string SafeMultiLineComment(string text)
    {
        StringBuilder builder = new(text.Length + 2);
        builder.Append(' ');
        while (true)
        {
            var index = text.IndexOf("*/", StringComparison.Ordinal);
            if (index < 0)
            {
                break;
            }
            builder.Append(text.AsSpan(0, index));
            builder.Append("*_/" );
            text = text[(index + 2)..];
        }
        builder.Append(text);
        builder.Append(' ');
        return builder.ToString();
    }
}
