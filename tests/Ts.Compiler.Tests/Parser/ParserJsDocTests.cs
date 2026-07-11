// Tests for the JSDoc cluster of Ts.Parser.Parser (internal/parser/jsdoc.go + reparser.go):
// JSDoc comment/tag grammar (@param/@returns/@type/@typedef/@see/@link + inline comment text +
// type expressions), the lazy JSDoc parse hook for TS files, and the JS reparse path that folds
// JSDoc types back into the TS-shaped AST.
//
// SCOPE NOTE (phase 3): @template/@callback/@overload/@augments/@implements parse but pull in
// sibling stubs (gatherTypeParameters / parseTemplateTagTypeParameters /
// parseExpressionWithTypeArgumentsForAugments) still pending in the Declarations cluster, so they
// are not exercised here. See the port report.

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserJsDocTests
{
    private static SourceFileParseOptions Opts(string fileName) =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile Parse(string text, ScriptKind kind = ScriptKind.TS, string fileName = "/test.ts") =>
        global::Ts.Parser.Parser.ParseSourceFile(Opts(fileName), text, kind);

    private static Node Stmt0(SourceFile sf) => sf.Statements!.Nodes[0];

    // Resolve the (single) JSDoc comment attached to a node (lazy for TS, eager for @see/@link/JS).
    private static Node SingleJSDoc(SourceFile sf, Node node)
    {
        Node[]? docs = node.JSDoc(sf);
        Assert.NotNull(docs);
        return Assert.Single(docs!);
    }

    private static Node SingleTag(SourceFile sf, Node node)
    {
        Node doc = SingleJSDoc(sf, node);
        Assert.Equal(SyntaxKind.JSDoc, doc.Kind);
        return Assert.Single(doc.AsJSDoc().Tags!.Nodes);
    }

    // ── TS lazy-parse: tag structure ──

    [Fact]
    public void ParamTag_ParsesTagNameNameAndType()
    {
        SourceFile sf = Parse("/** @param {string} x */\nfunction f(x) {}");
        Node fn = Stmt0(sf);
        Assert.Equal(SyntaxKind.FunctionDeclaration, fn.Kind);

        Node tag = SingleTag(sf, fn);
        Assert.Equal(SyntaxKind.JSDocParameterTag, tag.Kind);
        Assert.Equal("param", tag.TagName()!.Text());
        Assert.Equal("x", tag.Name()!.Text());

        Node te = tag.AsJSDocParameterOrPropertyTag().TypeExpression!;
        Assert.Equal(SyntaxKind.JSDocTypeExpression, te.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, te.Type()!.Kind);
    }

    [Fact]
    public void ParamTag_NameFirst_NoType()
    {
        SourceFile sf = Parse("/** @param x the count */\nfunction f(x) {}");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocParameterTag, tag.Kind);
        Assert.Equal("x", tag.Name()!.Text());
        Assert.Null(tag.AsJSDocParameterOrPropertyTag().TypeExpression);
        Assert.True(tag.AsJSDocParameterOrPropertyTag().IsNameFirst);
    }

    [Fact]
    public void ParamTag_Bracketed_IsOptional()
    {
        SourceFile sf = Parse("/** @param {number} [x] optional */\nfunction f(x) {}");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocParameterTag, tag.Kind);
        Assert.True(tag.AsJSDocParameterOrPropertyTag().IsBracketed);
        Assert.Equal("x", tag.Name()!.Text());
    }

    [Fact]
    public void MultipleParamTags_AreOrdered()
    {
        SourceFile sf = Parse("/**\n * @param {string} a\n * @param {number} b\n */\nfunction f(a, b) {}");
        Node doc = SingleJSDoc(sf, Stmt0(sf));
        Node[] tags = doc.AsJSDoc().Tags!.Nodes;
        Assert.Equal(2, tags.Length);
        Assert.Equal("a", tags[0].Name()!.Text());
        Assert.Equal("b", tags[1].Name()!.Text());
        Assert.Equal(SyntaxKind.NumberKeyword, tags[1].AsJSDocParameterOrPropertyTag().TypeExpression!.Type()!.Kind);
    }

    [Fact]
    public void ReturnsTag_ParsesType()
    {
        SourceFile sf = Parse("/** @returns {number} the answer */\nfunction f() {}");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocReturnTag, tag.Kind);
        Assert.Equal("returns", tag.TagName()!.Text());
        Assert.Equal(SyntaxKind.NumberKeyword, tag.AsJSDocReturnTag().TypeExpression!.Type()!.Kind);
    }

    [Fact]
    public void ReturnTagAlias_IsAlsoAReturnTag()
    {
        SourceFile sf = Parse("/** @return {boolean} */\nfunction f() {}");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocReturnTag, tag.Kind);
        Assert.Equal(SyntaxKind.BooleanKeyword, tag.AsJSDocReturnTag().TypeExpression!.Type()!.Kind);
    }

    [Fact]
    public void TypeTag_ParsesTypeExpression()
    {
        SourceFile sf = Parse("/** @type {boolean} */\nlet x;");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocTypeTag, tag.Kind);
        Assert.Equal(SyntaxKind.BooleanKeyword, tag.AsJSDocTypeTag().TypeExpression!.Type()!.Kind);
    }

    [Fact]
    public void TypeTag_TypeReference()
    {
        SourceFile sf = Parse("/** @type {Foo} */\nlet x;");
        Node tag = SingleTag(sf, Stmt0(sf));
        Node inner = tag.AsJSDocTypeTag().TypeExpression!.Type()!;
        Assert.Equal(SyntaxKind.TypeReference, inner.Kind);
        Assert.Equal("Foo", inner.AsTypeReferenceNode().TypeName!.Text());
    }

    [Fact]
    public void SeeTag_ParsesNameReference()
    {
        SourceFile sf = Parse("/** @see {Foo} for details */\nfunction f() {}");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocSeeTag, tag.Kind);
        Node? nameExpr = tag.AsJSDocSeeTag().NameExpression;
        Assert.NotNull(nameExpr);
        Assert.Equal(SyntaxKind.JSDocNameReference, nameExpr!.Kind);
        Assert.Equal("Foo", nameExpr.AsJSDocNameReference().name!.Text());
    }

    [Fact]
    public void TypedefTag_ParsesNameAndType()
    {
        SourceFile sf = Parse("/** @typedef {string} MyStr */\nlet a;");
        Node tag = SingleTag(sf, Stmt0(sf));
        Assert.Equal(SyntaxKind.JSDocTypedefTag, tag.Kind);
        Assert.Equal("MyStr", tag.Name()!.Text());
        Node te = tag.AsJSDocTypedefTag().TypeExpression!;
        Assert.Equal(SyntaxKind.JSDocTypeExpression, te.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, te.Type()!.Kind);
    }

    // ── TS lazy-parse: comment text + inline links ──

    [Fact]
    public void PlainComment_ProducesJSDocText()
    {
        SourceFile sf = Parse("/** Hello world. */\nfunction f() {}");
        Node doc = SingleJSDoc(sf, Stmt0(sf));
        Assert.Null(doc.AsJSDoc().Tags);
        NodeList? comment = doc.AsJSDoc().Comment;
        Assert.NotNull(comment);
        Node part = Assert.Single(comment!.Nodes);
        Assert.Equal(SyntaxKind.JSDocText, part.Kind);
        Assert.Equal("Hello world.", part.Text());
    }

    [Fact]
    public void InlineLink_ProducesJSDocLinkInComment()
    {
        SourceFile sf = Parse("/** See {@link Foo} now */\nfunction f() {}");
        Node doc = SingleJSDoc(sf, Stmt0(sf));
        Node[] parts = doc.AsJSDoc().Comment!.Nodes;
        // text "See " , link {@link Foo}, text " now"
        Node? link = System.Array.Find(parts, p => p.Kind == SyntaxKind.JSDocLink);
        Assert.NotNull(link);
        Assert.Equal("Foo", link!.AsJSDocLink().name!.Text());
    }

    [Fact]
    public void TrailingTagComment_IsCaptured()
    {
        SourceFile sf = Parse("/** @param {string} x the name of the thing */\nfunction f(x) {}");
        Node tag = SingleTag(sf, Stmt0(sf));
        NodeList? comment = tag.AsJSDocParameterOrPropertyTag().Comment;
        Assert.NotNull(comment);
        Node text = Assert.Single(comment!.Nodes);
        Assert.Equal("the name of the thing", text.Text());
    }

    [Fact]
    public void DeprecatedTag_SetsPossiblyContainsDeprecatedFlag()
    {
        SourceFile sf = Parse("/** @deprecated use bar instead */\nfunction f() {}");
        Node fn = Stmt0(sf);
        Assert.True((fn.Flags & NodeFlags.PossiblyContainsDeprecatedTag) != 0);
        Node tag = SingleTag(sf, fn);
        Assert.Equal(SyntaxKind.JSDocDeprecatedTag, tag.Kind);
    }

    // ── JS reparse: JSDoc types fold back into the AST (eager, no lazy hook) ──

    [Fact]
    public void Js_TypeTag_ReparsesVariableType()
    {
        SourceFile sf = Parse("/** @type {string} */\nvar x;", ScriptKind.JS, "/test.js");
        Node vs = Stmt0(sf);
        Assert.Equal(SyntaxKind.VariableStatement, vs.Kind);
        Node decl = vs.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0];
        Assert.NotNull(decl.Type());
        Assert.Equal(SyntaxKind.StringKeyword, decl.Type()!.Kind);
    }

    [Fact]
    public void Js_ParamTag_ReparsesParameterType()
    {
        SourceFile sf = Parse("/** @param {number} a */\nfunction f(a) {}", ScriptKind.JS, "/test.js");
        Node fn = Stmt0(sf);
        Assert.Equal(SyntaxKind.FunctionDeclaration, fn.Kind);
        Node param = fn.Parameters()[0];
        Assert.NotNull(param.Type());
        Assert.Equal(SyntaxKind.NumberKeyword, param.Type()!.Kind);
    }

    [Fact]
    public void Js_ReturnsTag_ReparsesReturnType()
    {
        SourceFile sf = Parse("/** @returns {boolean} */\nfunction f() {}", ScriptKind.JS, "/test.js");
        Node fn = Stmt0(sf);
        Assert.Equal(SyntaxKind.BooleanKeyword, fn.AsFunctionDeclaration().Type!.Kind);
    }

    [Fact]
    public void Js_OptionalParamTag_ReparsesQuestionToken()
    {
        SourceFile sf = Parse("/** @param {string} [a] */\nfunction f(a) {}", ScriptKind.JS, "/test.js");
        Node fn = Stmt0(sf);
        Node param = fn.Parameters()[0];
        Assert.NotNull(param.AsParameterDeclaration().QuestionToken);
    }

    // ── lazy hook wiring ──

    [Fact]
    public void LazyJSDoc_ResolvesWithoutThrowing_AndCaches()
    {
        SourceFile sf = Parse("/** @type {number} */\nlet y;");
        Node stmt = Stmt0(sf);
        // First access parses lazily; second access returns the same cached array.
        Node[]? first = stmt.JSDoc(sf);
        Node[]? second = stmt.JSDoc(sf);
        Assert.NotNull(first);
        Assert.Same(first, second);
    }
}
