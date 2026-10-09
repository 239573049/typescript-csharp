// Ported from tsgo internal/api/encoder/decoder_test.go (the BenchmarkDecodeSourceFile
// arm needs the _submodules/TypeScript corpus and is not ported).

using System;
using Ts.Ast;
using Ts.Core;
using Xunit;
// The namespace segment Ts.Api.Encoder shadows the class name Encoder at this nesting
// level, so bind the entry-point types through aliases (call sites keep the Go form).

namespace Ts.Api.Tests;

public class DecoderTests
{
    private static SourceFile ParseTs(string code)
    {
        return global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions
            {
                FileName = "/test.ts",
                Path = new global::Ts.TsPath.Path("/test.ts"),
            },
            code,
            ScriptKind.TS);
    }

    private static SourceFile EncodeDecode(string code)
    {
        SourceFile sf = ParseTs(code);
        byte[] buf = Ts.Api.Encoder.Encoder.EncodeSourceFile(sf).Data;
        return Ts.Api.Encoder.AstDecoder.DecodeSourceFile(buf);
    }

    // ── TestDecodeSourceFile_Basic ───────────────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_Basic()
    {
        SourceFile decoded = EncodeDecode("let x = 1;");
        Assert.Equal(SyntaxKind.SourceFile, decoded.AsNode().Kind);
        Assert.Equal("/test.ts", decoded.FileName());
        Assert.Equal("let x = 1;", decoded.Text());
        Assert.NotNull(decoded.Statements);
        Assert.NotNull(decoded.EndOfFileToken);
    }

    // ── TestDecodeSourceFile_Statements ──────────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_Statements()
    {
        SourceFile decoded = EncodeDecode("let a = 1;\nlet b = 2;\nlet c = 3;");
        Assert.Equal(3, decoded.Statements!.Nodes.Length);
        for (int i = 0; i < decoded.Statements.Nodes.Length; i++)
        {
            Assert.Equal(SyntaxKind.VariableStatement, decoded.Statements.Nodes[i].Kind);
        }
    }

    // ── TestDecodeSourceFile_VariableDeclaration ─────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_VariableDeclaration()
    {
        SourceFile decoded = EncodeDecode("let x = 1;");

        VariableStatement varStmt = decoded.Statements!.Nodes[0].AsVariableStatement();
        Assert.NotNull(varStmt.DeclarationList);
        VariableDeclarationList declList = varStmt.DeclarationList!.AsVariableDeclarationList();
        Assert.NotNull(declList.Declarations);
        Assert.Single(declList.Declarations!.Nodes);

        VariableDeclaration decl = declList.Declarations!.Nodes[0].AsVariableDeclaration();
        Assert.Equal(SyntaxKind.Identifier, decl.Name()!.Kind);
        Assert.Equal("x", decl.Name()!.AsIdentifier().Text);
        Assert.NotNull(decl.Initializer);
        Assert.Equal(SyntaxKind.NumericLiteral, decl.Initializer!.Kind);
        Assert.Equal("1", decl.Initializer!.AsNumericLiteral().Text);
    }

    // ── TestDecodeSourceFile_VariableDeclarationListFlags ────────────────────────────────────

    [Theory]
    [InlineData("const x = 1;", NodeFlags.Const)]
    [InlineData("let x = 1;", NodeFlags.Let)]
    [InlineData("var x = 1;", NodeFlags.None)]
    public void DecodeSourceFile_VariableDeclarationListFlags(string code, NodeFlags expected)
    {
        SourceFile decoded = EncodeDecode(code);
        VariableDeclarationList declList = decoded.Statements!.Nodes[0].AsVariableStatement().DeclarationList!.AsVariableDeclarationList();
        NodeFlags got = declList.Flags & (NodeFlags.Let | NodeFlags.Const);
        Assert.Equal(expected, got);
    }

    // ── TestDecodeSourceFile_FunctionDeclaration ─────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_FunctionDeclaration()
    {
        SourceFile decoded = EncodeDecode("function add(a: number, b: number): number { return a + b; }");

        FunctionDeclaration funcDecl = decoded.Statements!.Nodes[0].AsFunctionDeclaration();
        Assert.NotNull(funcDecl.Name());
        Assert.Equal("add", funcDecl.Name()!.AsIdentifier().Text);
        Assert.NotNull(funcDecl.Parameters);
        Assert.Equal(2, funcDecl.Parameters!.Nodes.Length);
        Assert.NotNull(funcDecl.Type);
        Assert.NotNull(funcDecl.Body);

        ParameterDeclaration param0 = funcDecl.Parameters!.Nodes[0].AsParameterDeclaration();
        Assert.Equal("a", param0.Name()!.AsIdentifier().Text);
        Assert.NotNull(param0.Type);
    }

    // ── TestDecodeSourceFile_ImportDeclaration ───────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_ImportDeclaration()
    {
        SourceFile decoded = EncodeDecode("import { bar } from \"bar\";");

        ImportDeclaration imp = decoded.Statements!.Nodes[0].AsImportDeclaration();
        Assert.NotNull(imp.ImportClause);
        Assert.NotNull(imp.ModuleSpecifier);
        Assert.Equal("bar", imp.ModuleSpecifier!.AsStringLiteral().Text);

        ImportClause clause = imp.ImportClause!.AsImportClause();
        Assert.NotNull(clause.NamedBindings);
        NamedImports namedImports = clause.NamedBindings!.AsNamedImports();
        Assert.NotNull(namedImports.Elements);
        Assert.Single(namedImports.Elements!.Nodes);
        ImportSpecifier spec = namedImports.Elements!.Nodes[0].AsImportSpecifier();
        Assert.Equal("bar", spec.Name()!.AsIdentifier().Text);
    }

    // ── TestDecodeSourceFile_IfStatement ─────────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_IfStatement()
    {
        SourceFile decoded = EncodeDecode("if (true) { } else { }");

        IfStatement ifStmt = decoded.Statements!.Nodes[0].AsIfStatement();
        Assert.NotNull(ifStmt.Expression);
        Assert.NotNull(ifStmt.ThenStatement);
        Assert.NotNull(ifStmt.ElseStatement);
        Assert.Equal(SyntaxKind.Block, ifStmt.ThenStatement!.Kind);
        Assert.Equal(SyntaxKind.Block, ifStmt.ElseStatement!.Kind);
    }

    // ── TestDecodeSourceFile_TemplateExpression ──────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_TemplateExpression()
    {
        SourceFile decoded = EncodeDecode("let x = `hello ${name} world`;");

        VariableDeclaration varDecl = decoded.Statements!.Nodes[0].AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        TemplateExpression tmplExpr = varDecl.Initializer!.AsTemplateExpression();
        Assert.NotNull(tmplExpr.Head);
        Assert.Equal("hello ", tmplExpr.Head!.AsTemplateHead().Text);
        Assert.NotNull(tmplExpr.TemplateSpans);
        Assert.Single(tmplExpr.TemplateSpans!.Nodes);

        TemplateSpan span = tmplExpr.TemplateSpans!.Nodes[0].AsTemplateSpan();
        Assert.NotNull(span.Expression);
        Assert.Equal(SyntaxKind.Identifier, span.Expression!.Kind);
        Assert.NotNull(span.Literal);
        Assert.Equal(" world", span.Literal!.AsTemplateTail().Text);
    }

    // ── TestDecodeSourceFile_ExportModifier ──────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_ExportModifier()
    {
        SourceFile decoded = EncodeDecode("export function foo() {}");

        FunctionDeclaration funcDecl = decoded.Statements!.Nodes[0].AsFunctionDeclaration();
        Assert.NotNull(funcDecl.Modifiers());
        Assert.Single(funcDecl.Modifiers()!.Nodes);
        Assert.Equal(SyntaxKind.ExportKeyword, funcDecl.Modifiers()!.Nodes[0].Kind);
    }

    // ── TestDecodeSourceFile_Positions ───────────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_Positions()
    {
        string code = "let x = 1;";
        SourceFile decoded = EncodeDecode(code);
        Assert.Equal(0, decoded.AsNode().Pos);
        Assert.Equal(code.Length, decoded.AsNode().End);
    }

    // ── TestDecodeSourceFile_ClassDeclaration ────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_ClassDeclaration()
    {
        SourceFile decoded = EncodeDecode("class Foo { bar(): void {} }");

        ClassDeclaration classDecl = decoded.Statements!.Nodes[0].AsClassDeclaration();
        Assert.NotNull(classDecl.Name());
        Assert.Equal("Foo", classDecl.Name()!.AsIdentifier().Text);
        Assert.NotNull(classDecl.Members);
        Assert.Single(classDecl.Members!.Nodes);
        Assert.Equal(SyntaxKind.MethodDeclaration, classDecl.Members!.Nodes[0].Kind);
    }

    // ── TestDecodeNodes_SubtreeRoundTrip ─────────────────────────────────────────────────────

    [Fact]
    public void DecodeNodes_SubtreeRoundTrip()
    {
        SourceFile sf = ParseTs("function greet(name: string) { return `Hello, ${name}!`; }");

        // Go uses a NodeVisitor pass to find the FunctionDeclaration; the same first-match
        // walk here picks it straight out of the statement list.
        Node? funcNode = null;
        foreach (Node stmt in sf.Statements!.Nodes)
        {
            if (stmt.Kind == SyntaxKind.FunctionDeclaration && funcNode == null)
            {
                funcNode = stmt;
            }
        }
        Assert.NotNull(funcNode);

        byte[] buf = Ts.Api.Encoder.Encoder.EncodeNode(funcNode!, sf).Data;
        Node decoded = Ts.Api.Encoder.AstDecoder.DecodeNodes(buf);

        Assert.Equal(SyntaxKind.FunctionDeclaration, decoded.Kind);
        FunctionDeclaration funcDecl = decoded.AsFunctionDeclaration();
        Assert.NotNull(funcDecl.Name());
        Assert.Equal("greet", funcDecl.Name()!.AsIdentifier().Text);
        Assert.NotNull(funcDecl.Parameters);
        Assert.Single(funcDecl.Parameters!.Nodes);
        Assert.NotNull(funcDecl.Body);
    }

    // ── TestDecodeSourceFile_BinaryExpression ────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_BinaryExpression()
    {
        SourceFile decoded = EncodeDecode("let x = 1 + 2;");

        VariableDeclaration decl = decoded.Statements!.Nodes[0].AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        BinaryExpression binExpr = decl.Initializer!.AsBinaryExpression();
        Assert.NotNull(binExpr.Left);
        Assert.NotNull(binExpr.Right);
        Assert.NotNull(binExpr.OperatorToken);
        Assert.Equal(SyntaxKind.NumericLiteral, binExpr.Left!.Kind);
        Assert.Equal(SyntaxKind.NumericLiteral, binExpr.Right!.Kind);
    }

    // ── TestDecodeSourceFile_KeywordExpressions ──────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_KeywordExpressions()
    {
        // "this" must decode as KeywordExpression, not Token, or the printer panics.
        SourceFile decoded = EncodeDecode("const x = this;");

        VariableDeclaration decl = decoded.Statements!.Nodes[0].AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        Node thisExpr = decl.Initializer!;
        Assert.Equal(SyntaxKind.ThisKeyword, thisExpr.Kind);
        // This would throw if decoded as Token instead of KeywordExpression.
        Assert.NotNull(thisExpr.AsKeywordExpression());
    }

    // ── TestDecodeSourceFile_EmptyModuleBlock ────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_EmptyModuleBlock()
    {
        SourceFile decoded = EncodeDecode("namespace N { }");

        // Navigate: namespace N { } → ModuleDeclaration → ModuleBlock.
        ModuleDeclaration mod = decoded.Statements!.Nodes[0].AsModuleDeclaration();
        Assert.NotNull(mod.Body);
        ModuleBlock block = mod.Body!.AsModuleBlock();
        // Statements must be non-nil even when empty, otherwise the printer panics.
        Assert.NotNull(block.Statements);
        Assert.Empty(block.Statements!.Nodes);
    }

    // ── TestDecodeSourceFile_EmptyBlockAndParams ─────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_EmptyBlockAndParams()
    {
        // Empty blocks and parameter lists must decode with non-null NodeLists (not null),
        // matching parser behavior.
        SourceFile decoded = EncodeDecode("function foo() {}");

        FunctionDeclaration funcDecl = decoded.Statements!.Nodes[0].AsFunctionDeclaration();
        Assert.NotNull(funcDecl.Parameters);
        Assert.Empty(funcDecl.Parameters!.Nodes);
        Assert.NotNull(funcDecl.Body);
        Block block = funcDecl.Body!.AsBlock();
        Assert.NotNull(block.Statements);
        Assert.Empty(block.Statements!.Nodes);
    }

    // ── TestDecodeSourceFile_ArrowFunctionEmptyParams ────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_ArrowFunctionEmptyParams()
    {
        SourceFile decoded = EncodeDecode("const f = () => {};");

        VariableDeclaration decl = decoded.Statements!.Nodes[0].AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        ArrowFunction arrow = decl.Initializer!.AsArrowFunction();
        Assert.NotNull(arrow.Parameters);
        Assert.Empty(arrow.Parameters!.Nodes);
        Assert.NotNull(arrow.Body);
        Block block = arrow.Body!.AsBlock();
        Assert.NotNull(block.Statements);
        Assert.Empty(block.Statements!.Nodes);
    }

    // ── TestDecodeSourceFile_FunctionExpressionEmptyParams ───────────────────────────────────

    [Fact]
    public void DecodeSourceFile_FunctionExpressionEmptyParams()
    {
        SourceFile decoded = EncodeDecode("const f = function() {};");

        VariableDeclaration decl = decoded.Statements!.Nodes[0].AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        FunctionExpression funcExpr = decl.Initializer!.AsFunctionExpression();
        Assert.NotNull(funcExpr.Parameters);
        Assert.Empty(funcExpr.Parameters!.Nodes);
    }

    // ── TestDecodeSourceFile_PostfixUnaryOperator ────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_PostfixUnaryOperator()
    {
        SourceFile decoded = EncodeDecode("let i = 0; i++;");

        ExpressionStatement exprStmt = decoded.Statements!.Nodes[1].AsExpressionStatement();
        PostfixUnaryExpression postfix = exprStmt.Expression!.AsPostfixUnaryExpression();
        Assert.Equal(SyntaxKind.PlusPlusToken, postfix.Operator);
        Assert.Equal(SyntaxKind.Identifier, postfix.Operand!.Kind);
    }

    // ── TestDecodeSourceFile_PrefixUnaryOperator ─────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_PrefixUnaryOperator()
    {
        SourceFile decoded = EncodeDecode("let x = true; !x;");

        ExpressionStatement exprStmt = decoded.Statements!.Nodes[1].AsExpressionStatement();
        PrefixUnaryExpression prefix = exprStmt.Expression!.AsPrefixUnaryExpression();
        Assert.Equal(SyntaxKind.ExclamationToken, prefix.Operator);
        Assert.Equal(SyntaxKind.Identifier, prefix.Operand!.Kind);
    }

    // ── TestDecodeSourceFile_PostfixDecrement ────────────────────────────────────────────────

    [Fact]
    public void DecodeSourceFile_PostfixDecrement()
    {
        SourceFile decoded = EncodeDecode("let n = 5; n--;");

        ExpressionStatement exprStmt = decoded.Statements!.Nodes[1].AsExpressionStatement();
        PostfixUnaryExpression postfix = exprStmt.Expression!.AsPostfixUnaryExpression();
        Assert.Equal(SyntaxKind.MinusMinusToken, postfix.Operator);
    }
}
