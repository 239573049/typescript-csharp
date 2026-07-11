// Tests for the Jsx cluster of Ts.Parser.Parser (internal/parser/parser.go JSX family:
// parseJsxElementOrSelfClosingElementOrFragment/parseJsxOpeningOrSelfClosingElementOrOpeningFragment/
// parseJsxChildren/parseJsxChild/parseJsxText/parseJsxExpression/parseJsxClosingElement/
// parseJsxClosingFragment/parseJsxElementName/parseJsxTagName/parseJsxAttributes/parseJsxAttribute/
// parseJsxAttributeName/parseJsxAttributeValue/parseJsxSpreadAttribute + the scanJsx* shims).
//
// SCOPE NOTE (phase 3): JSX parsing is driven in LanguageVariant.Jsx via ScriptKind.Tsx. Most tests
// use the ParseJsxExpressionForTest seam (parses one top-level JSX expression through the expression
// ladder without depending on statement-level start-of-expression heuristics); a few drive the public
// ParseSourceFile entry end-to-end on .tsx sources to confirm the integration path. Sources avoid JSDoc
// comments so the (still-parallel) JsDoc cluster's lazy path is never forced eager.

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserJsxTests
{
    private static (Node Node, Diagnostic[] Diagnostics) ParseJsx(string text) =>
        global::Ts.Parser.Parser.ParseJsxExpressionForTest(text);

    private static Node ParseJsxOk(string text)
    {
        (Node node, Diagnostic[] diags) = ParseJsx(text);
        Assert.Empty(diags);
        return node;
    }

    private static Node TagNameOf(Node jsxSelfClosingOrOpening) => jsxSelfClosingOrOpening.Kind switch
    {
        SyntaxKind.JsxSelfClosingElement => jsxSelfClosingOrOpening.AsJsxSelfClosingElement().TagName!,
        SyntaxKind.JsxOpeningElement => jsxSelfClosingOrOpening.AsJsxOpeningElement().TagName!,
        _ => throw new System.InvalidOperationException("not an opening/self-closing element"),
    };

    private static Node[] AttrsOf(Node openingLike)
    {
        Node attributes = openingLike.Kind == SyntaxKind.JsxSelfClosingElement
            ? openingLike.AsJsxSelfClosingElement().Attributes!
            : openingLike.AsJsxOpeningElement().Attributes!;
        return attributes.AsJsxAttributes().Properties!.Nodes;
    }

    // ── self-closing elements ──

    [Fact]
    public void SelfClosingElement_ProducesSelfClosingNode()
    {
        Node node = ParseJsxOk("<div/>");
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, node.Kind);
        Assert.Equal("div", TagNameOf(node).Text());
        Assert.Empty(AttrsOf(node));
    }

    [Fact]
    public void SelfClosingElement_WithSpaceBeforeSlash()
    {
        Node node = ParseJsxOk("<div />");
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, node.Kind);
        Assert.Equal("div", TagNameOf(node).Text());
    }

    // ── elements with children ──

    [Fact]
    public void Element_WithTextChild_ProducesJsxElement()
    {
        Node node = ParseJsxOk("<div>hello</div>");
        Assert.Equal(SyntaxKind.JsxElement, node.Kind);
        JsxElement el = node.AsJsxElement();
        Assert.Equal(SyntaxKind.JsxOpeningElement, el.OpeningElement!.Kind);
        Assert.Equal("div", el.OpeningElement!.AsJsxOpeningElement().TagName!.Text());
        Assert.Equal(SyntaxKind.JsxClosingElement, el.ClosingElement!.Kind);
        Assert.Equal("div", el.ClosingElement!.AsJsxClosingElement().TagName!.Text());
        Node child = Assert.Single(el.Children!.Nodes);
        Assert.Equal(SyntaxKind.JsxText, child.Kind);
        Assert.Equal("hello", child.AsJsxText().Text);
        Assert.False(child.AsJsxText().ContainsOnlyTriviaWhiteSpaces);
    }

    [Fact]
    public void Element_Empty_HasNoChildren()
    {
        Node node = ParseJsxOk("<div></div>");
        Assert.Equal(SyntaxKind.JsxElement, node.Kind);
        Assert.Empty(node.AsJsxElement().Children!.Nodes);
    }

    [Fact]
    public void Element_WithNestedElementChild()
    {
        Node node = ParseJsxOk("<div><span/></div>");
        Node child = Assert.Single(node.AsJsxElement().Children!.Nodes);
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, child.Kind);
        Assert.Equal("span", child.AsJsxSelfClosingElement().TagName!.Text());
    }

    // ── fragments ──

    [Fact]
    public void Fragment_ProducesFragmentNode()
    {
        Node node = ParseJsxOk("<></>");
        Assert.Equal(SyntaxKind.JsxFragment, node.Kind);
        JsxFragment frag = node.AsJsxFragment();
        Assert.Equal(SyntaxKind.JsxOpeningFragment, frag.OpeningFragment!.Kind);
        Assert.Equal(SyntaxKind.JsxClosingFragment, frag.ClosingFragment!.Kind);
        Assert.Empty(frag.Children!.Nodes);
    }

    [Fact]
    public void Fragment_WithTextAndElementChildren()
    {
        Node node = ParseJsxOk("<>a<b/></>");
        JsxFragment frag = node.AsJsxFragment();
        Assert.Equal(2, frag.Children!.Nodes.Length);
        Assert.Equal(SyntaxKind.JsxText, frag.Children!.Nodes[0].Kind);
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, frag.Children!.Nodes[1].Kind);
    }

    // ── attributes ──

    [Fact]
    public void Attribute_StringValue()
    {
        Node node = ParseJsxOk("<div className=\"box\"/>");
        Node attr = Assert.Single(AttrsOf(node));
        Assert.Equal(SyntaxKind.JsxAttribute, attr.Kind);
        Assert.Equal("className", attr.AsJsxAttribute().name!.Text());
        Node init = attr.AsJsxAttribute().Initializer!;
        Assert.Equal(SyntaxKind.StringLiteral, init.Kind);
        Assert.Equal("box", init.AsStringLiteral().Text);
    }

    [Fact]
    public void Attribute_ExpressionValue()
    {
        Node node = ParseJsxOk("<div id={x}/>");
        Node attr = Assert.Single(AttrsOf(node));
        Node init = attr.AsJsxAttribute().Initializer!;
        Assert.Equal(SyntaxKind.JsxExpression, init.Kind);
        Assert.Equal(SyntaxKind.Identifier, init.AsJsxExpression().Expression!.Kind);
        Assert.Equal("x", init.AsJsxExpression().Expression!.Text());
    }

    [Fact]
    public void Attribute_Shorthand_HasNoInitializer()
    {
        Node node = ParseJsxOk("<input disabled/>");
        Node attr = Assert.Single(AttrsOf(node));
        Assert.Equal("disabled", attr.AsJsxAttribute().name!.Text());
        Assert.Null(attr.AsJsxAttribute().Initializer);
    }

    [Fact]
    public void Attribute_Multiple()
    {
        Node node = ParseJsxOk("<div a=\"1\" b={c} d/>");
        Node[] attrs = AttrsOf(node);
        Assert.Equal(3, attrs.Length);
        Assert.Equal("a", attrs[0].AsJsxAttribute().name!.Text());
        Assert.Equal("b", attrs[1].AsJsxAttribute().name!.Text());
        Assert.Equal("d", attrs[2].AsJsxAttribute().name!.Text());
    }

    [Fact]
    public void Attribute_JsxElementValue()
    {
        Node node = ParseJsxOk("<div icon=<i/>/>");
        Node attr = Assert.Single(AttrsOf(node));
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, attr.AsJsxAttribute().Initializer!.Kind);
    }

    // ── spread attributes ──

    [Fact]
    public void SpreadAttribute_ProducesSpreadNode()
    {
        Node node = ParseJsxOk("<div {...props}/>");
        Node attr = Assert.Single(AttrsOf(node));
        Assert.Equal(SyntaxKind.JsxSpreadAttribute, attr.Kind);
        Assert.Equal(SyntaxKind.Identifier, attr.AsJsxSpreadAttribute().Expression!.Kind);
        Assert.Equal("props", attr.AsJsxSpreadAttribute().Expression!.Text());
    }

    // ── expression containers as children ──

    [Fact]
    public void ExpressionChild_ProducesJsxExpression()
    {
        Node node = ParseJsxOk("<div>{value}</div>");
        Node child = Assert.Single(node.AsJsxElement().Children!.Nodes);
        Assert.Equal(SyntaxKind.JsxExpression, child.Kind);
        Assert.Equal("value", child.AsJsxExpression().Expression!.Text());
        Assert.Null(child.AsJsxExpression().DotDotDotToken);
    }

    [Fact]
    public void ExpressionChild_WithSpread()
    {
        Node node = ParseJsxOk("<div>{...items}</div>");
        Node child = Assert.Single(node.AsJsxElement().Children!.Nodes);
        Assert.Equal(SyntaxKind.JsxExpression, child.Kind);
        Assert.NotNull(child.AsJsxExpression().DotDotDotToken);
    }

    [Fact]
    public void ExpressionChild_Empty()
    {
        Node node = ParseJsxOk("<div>{}</div>");
        Node child = Assert.Single(node.AsJsxElement().Children!.Nodes);
        Assert.Equal(SyntaxKind.JsxExpression, child.Kind);
        Assert.Null(child.AsJsxExpression().Expression);
    }

    // ── tag name forms ──

    [Fact]
    public void MemberExpressionTag()
    {
        Node node = ParseJsxOk("<a.b.c/>");
        Node tagName = TagNameOf(node);
        Assert.Equal(SyntaxKind.PropertyAccessExpression, tagName.Kind);
        Assert.Equal("c", tagName.AsPropertyAccessExpression().name!.Text());
        Node inner = tagName.AsPropertyAccessExpression().Expression!;
        Assert.Equal(SyntaxKind.PropertyAccessExpression, inner.Kind);
        Assert.Equal("b", inner.AsPropertyAccessExpression().name!.Text());
        Assert.Equal("a", inner.AsPropertyAccessExpression().Expression!.Text());
    }

    [Fact]
    public void NamespacedNameTag()
    {
        Node node = ParseJsxOk("<svg:rect/>");
        Node tagName = TagNameOf(node);
        Assert.Equal(SyntaxKind.JsxNamespacedName, tagName.Kind);
        Assert.Equal("svg", tagName.AsJsxNamespacedName().Namespace!.Text());
        Assert.Equal("rect", tagName.AsJsxNamespacedName().name!.Text());
    }

    [Fact]
    public void NamespacedAttributeName()
    {
        Node node = ParseJsxOk("<a xlink:href=\"u\"/>");
        Node attr = Assert.Single(AttrsOf(node));
        Node attrName = attr.AsJsxAttribute().name!;
        Assert.Equal(SyntaxKind.JsxNamespacedName, attrName.Kind);
        Assert.Equal("xlink", attrName.AsJsxNamespacedName().Namespace!.Text());
        Assert.Equal("href", attrName.AsJsxNamespacedName().name!.Text());
    }

    [Fact]
    public void ThisKeywordTag()
    {
        Node node = ParseJsxOk("<this/>");
        Assert.Equal(SyntaxKind.ThisKeyword, TagNameOf(node).Kind);
    }

    [Fact]
    public void TypeArgumentsOnTag()
    {
        Node node = ParseJsxOk("<Foo<number>/>");
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, node.Kind);
        Assert.Equal("Foo", TagNameOf(node).Text());
        NodeList? typeArgs = node.AsJsxSelfClosingElement().TypeArguments;
        Assert.NotNull(typeArgs);
        Node typeArg = Assert.Single(typeArgs!.Nodes);
        Assert.Equal(SyntaxKind.NumberKeyword, typeArg.Kind);
    }

    // ── JsxText classification ──

    [Fact]
    public void JsxText_WhitespaceWithNewline_IsAllWhitespace()
    {
        Node node = ParseJsxOk("<div>\n   </div>");
        Node child = Assert.Single(node.AsJsxElement().Children!.Nodes);
        Assert.Equal(SyntaxKind.JsxText, child.Kind);
        Assert.True(child.AsJsxText().ContainsOnlyTriviaWhiteSpaces);
    }

    [Fact]
    public void JsxText_MixedWithExpression()
    {
        Node node = ParseJsxOk("<div>a{b}c</div>");
        Node[] children = node.AsJsxElement().Children!.Nodes;
        Assert.Equal(3, children.Length);
        Assert.Equal(SyntaxKind.JsxText, children[0].Kind);
        Assert.Equal("a", children[0].AsJsxText().Text);
        Assert.Equal(SyntaxKind.JsxExpression, children[1].Kind);
        Assert.Equal(SyntaxKind.JsxText, children[2].Kind);
        Assert.Equal("c", children[2].AsJsxText().Text);
    }

    // ── positions (UTF-16 code units) ──

    [Fact]
    public void SelfClosingElement_Positions()
    {
        Node node = ParseJsxOk("<div/>");
        Assert.Equal(0, node.Pos);
        Assert.Equal(6, node.End);
    }

    // ── error recovery / diagnostics ──

    [Fact]
    public void MismatchedClosingTag_ReportsExpectedClosingTag()
    {
        (Node node, Diagnostic[] diags) = ParseJsx("<div></span>");
        Assert.Equal(SyntaxKind.JsxElement, node.Kind);
        Assert.NotEmpty(diags);
        // 17002: Expected corresponding JSX closing tag for '{0}'.
        Assert.Contains(diags, d => d.Code() == 17002);
    }

    [Fact]
    public void UnclosedElement_ReportsNoCorrespondingClosingTag()
    {
        (Node _, Diagnostic[] diags) = ParseJsx("<div>");
        Assert.NotEmpty(diags);
        // 17008: JSX element '{0}' has no corresponding closing tag.
        Assert.Contains(diags, d => d.Code() == 17008);
    }

    [Fact]
    public void UnclosedFragment_ReportsNoCorrespondingClosingTag()
    {
        (Node _, Diagnostic[] diags) = ParseJsx("<>");
        Assert.NotEmpty(diags);
        // 17014: JSX fragment has no corresponding closing tag.
        Assert.Contains(diags, d => d.Code() == 17014);
    }

    // ── end-to-end via ParseSourceFile on a .tsx source ──

    private static SourceFileParseOptions Opts(string fileName = "/test.tsx") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    [Fact]
    public void SourceFile_VariableInitializedWithJsx()
    {
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(Opts(), "const el = <div className=\"x\">{value}</div>;", ScriptKind.Tsx);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        Node statement = Assert.Single(sf.Statements!.Nodes);
        Assert.Equal(SyntaxKind.VariableStatement, statement.Kind);
        Node decl = statement.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0];
        Node initializer = decl.AsVariableDeclaration().Initializer!;
        Assert.Equal(SyntaxKind.JsxElement, initializer.Kind);
        Assert.Equal("div", initializer.AsJsxElement().OpeningElement!.AsJsxOpeningElement().TagName!.Text());
    }

    [Fact]
    public void SourceFile_ExpressionStatementJsx()
    {
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(Opts(), "<App a={1}/>;", ScriptKind.Tsx);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        Node statement = Assert.Single(sf.Statements!.Nodes);
        Assert.Equal(SyntaxKind.ExpressionStatement, statement.Kind);
        Assert.Equal(SyntaxKind.JsxSelfClosingElement, statement.AsExpressionStatement().Expression!.Kind);
    }
}
