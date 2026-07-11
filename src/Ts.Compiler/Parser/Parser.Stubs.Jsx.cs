// Partitioned stub file (cluster: Jsx). Replace each stub with the real body in place.
// Build stays green; when all stubs in a cluster are implemented, delete this file.
#nullable enable
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;

using Message = Ts.Diagnostics.Message;

namespace Ts.Parser;

public sealed partial class Parser
{
    private Node ParseJsxAttribute() => throw new System.NotImplementedException("parseJsxAttribute");
    private Node ParseJsxAttributeName() => throw new System.NotImplementedException("parseJsxAttributeName");
    private Node ParseJsxAttributeValue() => throw new System.NotImplementedException("parseJsxAttributeValue");
    private Node ParseJsxAttributes() => throw new System.NotImplementedException("parseJsxAttributes");
    private Node ParseJsxChild(Node? openingTag, SyntaxKind token) => throw new System.NotImplementedException("parseJsxChild");
    private Node ParseJsxClosingElement(Node? open, bool inExpressionContext) => throw new System.NotImplementedException("parseJsxClosingElement");
    private Node ParseJsxClosingFragment(bool inExpressionContext) => throw new System.NotImplementedException("parseJsxClosingFragment");
    private Node ParseJsxElementName() => throw new System.NotImplementedException("parseJsxElementName");
    private Node ParseJsxElementOrSelfClosingElementOrFragment(bool inExpressionContext, int topInvalidNodePosition, Node? openingTag, bool mustBeUnary) => throw new System.NotImplementedException("parseJsxElementOrSelfClosingElementOrFragment");
    private Node ParseJsxExpression(bool inExpressionContext) => throw new System.NotImplementedException("parseJsxExpression");
    private Node ParseJsxOpeningOrSelfClosingElementOrOpeningFragment(bool inExpressionContext) => throw new System.NotImplementedException("parseJsxOpeningOrSelfClosingElementOrOpeningFragment");
    private Node ParseJsxSpreadAttribute() => throw new System.NotImplementedException("parseJsxSpreadAttribute");
    private Node ParseJsxTagName() => throw new System.NotImplementedException("parseJsxTagName");
    private Node ParseJsxText() => throw new System.NotImplementedException("parseJsxText");
    private NodeList ParseJsxChildren(Node? openingTag) => throw new System.NotImplementedException("parseJsxChildren");
    private SyntaxKind ScanJsxAttributeValue() => throw new System.NotImplementedException("scanJsxAttributeValue");
    private SyntaxKind ScanJsxIdentifier() => throw new System.NotImplementedException("scanJsxIdentifier");
    private SyntaxKind ScanJsxText() => throw new System.NotImplementedException("scanJsxText");
}
