// Ported from tsgo internal/printer/printer.go (JSX section).

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using AstUtil = global::Ts.Ast.AstUtilities;

public sealed partial class Printer
{
    private void EmitJsxElement(JsxElement node)
    {
        var state = EnterNode(node.AsNode());
        EmitJsxOpeningElement(node.OpeningElement!.AsJsxOpeningElement());
        EmitList(static (p, n) => p.EmitJsxChild(n), node.AsNode(), node.Children, ListFormat.JsxElementOrFragmentChildren);
        EmitJsxClosingElement(node.ClosingElement!.AsJsxClosingElement());
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxSelfClosingElement(JsxSelfClosingElement node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("<");
        EmitJsxTagName(node.TagName!);
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        WriteSpace();
        EmitJsxAttributes(node.Attributes!.AsJsxAttributes());
        WritePunctuation("/>");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxFragment(JsxFragment node)
    {
        var state = EnterNode(node.AsNode());
        EmitJsxOpeningFragment(node.OpeningFragment!.AsJsxOpeningFragment());
        EmitList(static (p, n) => p.EmitJsxChild(n), node.AsNode(), node.Children, ListFormat.JsxElementOrFragmentChildren);
        EmitJsxClosingFragment(node.ClosingFragment!.AsJsxClosingFragment());
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxOpeningElement(JsxOpeningElement node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("<");
        var indented = WriteLineSeparatorsAndIndentBefore(node.TagName!, node.AsNode());
        EmitJsxTagName(node.TagName!);
        EmitTypeArguments(node.AsNode(), node.TypeArguments);
        if ((node.Attributes!.Properties()?.Length ?? 0) > 0)
        {
            WriteSpace();
        }
        EmitJsxAttributes(node.Attributes!.AsJsxAttributes());
        WriteLineSeparatorsAfter(node.Attributes, node.AsNode());
        DecreaseIndentIf(indented);
        WritePunctuation(">");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxClosingElement(JsxClosingElement node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("</");
        EmitJsxTagName(node.TagName!);
        WritePunctuation(">");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxOpeningFragment(JsxOpeningFragment node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("<");
        WritePunctuation(">");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxClosingFragment(JsxClosingFragment node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("</");
        WritePunctuation(">");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxText(JsxText node)
    {
        var state = EnterNode(node.AsNode());
        WriteLiteral(node.Text);
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxAttributes(JsxAttributes node)
    {
        var state = EnterNode(node.AsNode());
        EmitList(static (p, n) => p.EmitJsxAttributeLike(n), node.AsNode(), node.Properties, ListFormat.JsxElementAttributes);
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxAttribute(JsxAttribute node)
    {
        var state = EnterNode(node.AsNode());
        EmitJsxAttributeName(node.Name()!);
        if (node.Initializer is not null)
        {
            WritePunctuation("=");
            EmitJsxAttributeValue(node.Initializer);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxSpreadAttribute(JsxSpreadAttribute node)
    {
        var state = EnterNode(node.AsNode());
        WritePunctuation("{...");
        EmitExpression(node.Expression!, OperatorPrecedence.Lowest);
        WritePunctuation("}");
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxAttributeLike(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.JsxAttribute:
                EmitJsxAttribute(node.AsJsxAttribute());
                break;
            case SyntaxKind.JsxSpreadAttribute:
                EmitJsxSpreadAttribute(node.AsJsxSpreadAttribute());
                break;
            default:
                throw new InvalidOperationException($"unhandled JsxAttributeLike: {node.Kind}");
        }
    }

    private void EmitJsxExpression(JsxExpression node)
    {
        var state = EnterNode(node.AsNode());
        if (node.Expression is not null || (!commentsDisabled && !AstUtil.NodeIsSynthesized(node.AsNode()) && HasCommentsAtPosition(node.Pos)))
        {
            var indented = currentSourceFile is not null && !AstUtil.NodeIsSynthesized(node.AsNode()) && PrinterUtilities.GetLinesBetweenPositions(currentSourceFile, node.Pos, node.End) != 0;
            IncreaseIndentIf(indented);
            var end = EmitToken(SyntaxKind.OpenBraceToken, node.Pos, WriteKind.Punctuation, node.AsNode());
            EmitTokenNode(node.DotDotDotToken);
            if (node.Expression is not null)
            {
                EmitExpression(node.Expression, OperatorPrecedence.DisallowComma);
            }
            EmitToken(SyntaxKind.CloseBraceToken, PrinterUtilities.GreatestEnd(end, node.Expression, node.DotDotDotToken), WriteKind.Punctuation, node.AsNode());
            DecreaseIndentIf(indented);
        }
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxNamespacedName(JsxNamespacedName node)
    {
        var state = EnterNode(node.AsNode());
        EmitIdentifierName(node.Namespace!.AsIdentifier());
        WritePunctuation(":");
        EmitIdentifierName(node.Name()!.AsIdentifier());
        ExitNode(node.AsNode(), state);
    }

    private void EmitJsxChild(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.JsxText:
                EmitJsxText(node.AsJsxText());
                break;
            case SyntaxKind.JsxExpression:
                EmitJsxExpression(node.AsJsxExpression());
                break;
            case SyntaxKind.JsxElement:
                EmitJsxElement(node.AsJsxElement());
                break;
            case SyntaxKind.JsxSelfClosingElement:
                EmitJsxSelfClosingElement(node.AsJsxSelfClosingElement());
                break;
            case SyntaxKind.JsxFragment:
                EmitJsxFragment(node.AsJsxFragment());
                break;
            default:
                throw new InvalidOperationException($"unhandled JsxChild: {node.Kind}");
        }
    }

    private void EmitJsxTagName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierReference(node.AsIdentifier());
                break;
            case SyntaxKind.ThisKeyword:
                EmitKeywordExpression(node.AsKeywordExpression());
                break;
            case SyntaxKind.JsxNamespacedName:
                EmitJsxNamespacedName(node.AsJsxNamespacedName());
                break;
            case SyntaxKind.PropertyAccessExpression:
                EmitPropertyAccessExpression(node.AsPropertyAccessExpression());
                break;
            default:
                throw new InvalidOperationException($"unhandled JsxTagName: {node.Kind}");
        }
    }

    private void EmitJsxAttributeName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                EmitIdentifierName(node.AsIdentifier());
                break;
            case SyntaxKind.JsxNamespacedName:
                EmitJsxNamespacedName(node.AsJsxNamespacedName());
                break;
            default:
                throw new InvalidOperationException($"unhandled JsxAttributeName: {node.Kind}");
        }
    }

    private void EmitJsxAttributeValue(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
                EmitStringLiteral(node.AsStringLiteral());
                break;
            case SyntaxKind.JsxExpression:
                EmitJsxExpression(node.AsJsxExpression());
                break;
            case SyntaxKind.JsxElement:
                EmitJsxElement(node.AsJsxElement());
                break;
            case SyntaxKind.JsxSelfClosingElement:
                EmitJsxSelfClosingElement(node.AsJsxSelfClosingElement());
                break;
            case SyntaxKind.JsxFragment:
                EmitJsxFragment(node.AsJsxFragment());
                break;
            default:
                EmitExpression(node, OperatorPrecedence.Lowest);
                break;
        }
    }
}
