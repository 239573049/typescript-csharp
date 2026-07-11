// Tests for the Statements cluster of Ts.Parser.Parser (internal/parser/parser.go statement family:
// parseStatement/parseBlock/parseIf/parseFor*/parseWhile/parseDo/parseSwitch/parseTry/parseReturn/
// parseThrow/parseBreak/parseContinue/parseWith/parseEmpty/parseDebugger/parseLabeled + clauses).
//
// SCOPE NOTE (phase 3): These drive the public ParseSourceFile entry end-to-end and assert the
// produced AST kind trees and the parse-diagnostic (code, pos, len) triples. Statement *bodies*
// route into the Expressions cluster and variable/binding forms into the Declarations cluster; both
// have since landed, so the full statement surface (control flow, blocks, labels, switch, try/catch
// with and without a binding, variable statements, and expression statements) is reachable here.
// Genuinely deep cross-cluster paths (JSX children, JSDoc reparse, full type-annotation ladders) are
// still deferred to the phase-3 conformance/integration baselines. See the port report.

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserStatementTests
{
    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile Parse(string text, ScriptKind kind = ScriptKind.TS) =>
        global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, kind);

    private static Node[] ParseOk(string text, ScriptKind kind = ScriptKind.TS)
    {
        SourceFile sf = Parse(text, kind);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        return sf.Statements!.Nodes;
    }

    private static Node ParseOneOk(string text, ScriptKind kind = ScriptKind.TS)
    {
        Node[] statements = ParseOk(text, kind);
        return Assert.Single(statements);
    }

    // ── empty / block ──

    [Fact]
    public void EmptyStatement_ProducesEmptyStatementNode()
    {
        Node s = ParseOneOk(";");
        Assert.Equal(SyntaxKind.EmptyStatement, s.Kind);
    }

    [Fact]
    public void Block_Empty_ProducesBlockWithNoStatements()
    {
        Node s = ParseOneOk("{}");
        Assert.Equal(SyntaxKind.Block, s.Kind);
        Block b = s.AsBlock();
        Assert.Empty(b.Statements!.Nodes);
        Assert.False(b.MultiLine);
    }

    [Fact]
    public void Block_MultilineFlagSetWhenOpenBraceFollowedByNewline()
    {
        Node s = ParseOneOk("{\n ;\n}");
        Block b = s.AsBlock();
        Assert.True(b.MultiLine);
        Node inner = Assert.Single(b.Statements!.Nodes);
        Assert.Equal(SyntaxKind.EmptyStatement, inner.Kind);
    }

    [Fact]
    public void MultipleStatements_ParseInOrder()
    {
        Node[] statements = ParseOk("; ; ;");
        Assert.Equal(3, statements.Length);
        Assert.All(statements, s => Assert.Equal(SyntaxKind.EmptyStatement, s.Kind));
    }

    // ── if / else ──

    [Fact]
    public void IfStatement_WithoutElse()
    {
        Node s = ParseOneOk("if (x) ;");
        Assert.Equal(SyntaxKind.IfStatement, s.Kind);
        IfStatement iff = s.AsIfStatement();
        Assert.Equal(SyntaxKind.Identifier, iff.Expression!.Kind);
        Assert.Equal(SyntaxKind.EmptyStatement, iff.ThenStatement!.Kind);
        Assert.Null(iff.ElseStatement);
    }

    [Fact]
    public void IfStatement_WithElse()
    {
        Node s = ParseOneOk("if (x) ; else ;");
        IfStatement iff = s.AsIfStatement();
        Assert.NotNull(iff.ElseStatement);
        Assert.Equal(SyntaxKind.EmptyStatement, iff.ElseStatement!.Kind);
    }

    [Fact]
    public void IfStatement_ElseIfChain()
    {
        Node s = ParseOneOk("if (a) ; else if (b) ; else ;");
        IfStatement iff = s.AsIfStatement();
        Assert.Equal(SyntaxKind.IfStatement, iff.ElseStatement!.Kind);
    }

    // ── iteration ──

    [Fact]
    public void WhileStatement()
    {
        Node s = ParseOneOk("while (x) ;");
        Assert.Equal(SyntaxKind.WhileStatement, s.Kind);
        WhileStatement w = s.AsWhileStatement();
        Assert.Equal(SyntaxKind.Identifier, w.Expression!.Kind);
        Assert.Equal(SyntaxKind.EmptyStatement, w.Statement!.Kind);
    }

    [Fact]
    public void DoStatement()
    {
        Node s = ParseOneOk("do ; while (x);");
        Assert.Equal(SyntaxKind.DoStatement, s.Kind);
        DoStatement d = s.AsDoStatement();
        Assert.Equal(SyntaxKind.EmptyStatement, d.Statement!.Kind);
        Assert.Equal(SyntaxKind.Identifier, d.Expression!.Kind);
    }

    [Fact]
    public void ForStatement_Empty()
    {
        Node s = ParseOneOk("for (;;) ;");
        Assert.Equal(SyntaxKind.ForStatement, s.Kind);
        ForStatement f = s.AsForStatement();
        Assert.Null(f.Initializer);
        Assert.Null(f.Condition);
        Assert.Null(f.Incrementor);
        Assert.Equal(SyntaxKind.EmptyStatement, f.Statement!.Kind);
    }

    [Fact]
    public void ForStatement_WithExpressions()
    {
        Node s = ParseOneOk("for (a; b; c) ;");
        ForStatement f = s.AsForStatement();
        Assert.Equal(SyntaxKind.Identifier, f.Initializer!.Kind);
        Assert.Equal(SyntaxKind.Identifier, f.Condition!.Kind);
        Assert.Equal(SyntaxKind.Identifier, f.Incrementor!.Kind);
    }

    [Fact]
    public void ForInStatement()
    {
        Node s = ParseOneOk("for (a in b) ;");
        Assert.Equal(SyntaxKind.ForInStatement, s.Kind);
        ForInOrOfStatement f = s.AsForInOrOfStatement();
        Assert.Null(f.AwaitModifier);
        Assert.Equal(SyntaxKind.Identifier, f.Initializer!.Kind);
        Assert.Equal(SyntaxKind.Identifier, f.Expression!.Kind);
    }

    [Fact]
    public void ForOfStatement()
    {
        Node s = ParseOneOk("for (a of b) ;");
        Assert.Equal(SyntaxKind.ForOfStatement, s.Kind);
        ForInOrOfStatement f = s.AsForInOrOfStatement();
        Assert.Null(f.AwaitModifier);
        Assert.Equal(SyntaxKind.Identifier, f.Expression!.Kind);
    }

    // ── switch ──

    [Fact]
    public void SwitchStatement_WithCaseAndDefault()
    {
        Node s = ParseOneOk("switch (x) { case 1: break; default: ; }");
        Assert.Equal(SyntaxKind.SwitchStatement, s.Kind);
        SwitchStatement sw = s.AsSwitchStatement();
        Assert.Equal(SyntaxKind.Identifier, sw.Expression!.Kind);
        Node[] clauses = sw.CaseBlock!.AsCaseBlock().Clauses!.Nodes;
        Assert.Equal(2, clauses.Length);
        Assert.Equal(SyntaxKind.CaseClause, clauses[0].Kind);
        CaseOrDefaultClause caseClause = clauses[0].AsCaseOrDefaultClause();
        Assert.Equal(SyntaxKind.NumericLiteral, caseClause.Expression!.Kind);
        Node caseStmt = Assert.Single(caseClause.Statements!.Nodes);
        Assert.Equal(SyntaxKind.BreakStatement, caseStmt.Kind);
        Assert.Equal(SyntaxKind.DefaultClause, clauses[1].Kind);
        Assert.Null(clauses[1].AsCaseOrDefaultClause().Expression);
    }

    [Fact]
    public void SwitchStatement_EmptyBlock()
    {
        Node s = ParseOneOk("switch (x) { }");
        SwitchStatement sw = s.AsSwitchStatement();
        Assert.Empty(sw.CaseBlock!.AsCaseBlock().Clauses!.Nodes);
    }

    // ── try / catch / finally (no catch binding: parseVariableDeclaration is a Declarations stub) ──

    [Fact]
    public void TryCatch_NoBinding()
    {
        Node s = ParseOneOk("try { } catch { }");
        Assert.Equal(SyntaxKind.TryStatement, s.Kind);
        TryStatement t = s.AsTryStatement();
        Assert.Equal(SyntaxKind.Block, t.TryBlock!.Kind);
        Assert.NotNull(t.CatchClause);
        Assert.Equal(SyntaxKind.CatchClause, t.CatchClause!.Kind);
        Assert.Null(t.CatchClause.AsCatchClause().VariableDeclaration);
        Assert.Null(t.FinallyBlock);
    }

    [Fact]
    public void TryFinally()
    {
        Node s = ParseOneOk("try { } finally { }");
        TryStatement t = s.AsTryStatement();
        Assert.Null(t.CatchClause);
        Assert.Equal(SyntaxKind.Block, t.FinallyBlock!.Kind);
    }

    [Fact]
    public void TryCatchFinally_NoBinding()
    {
        Node s = ParseOneOk("try { } catch { } finally { }");
        TryStatement t = s.AsTryStatement();
        Assert.NotNull(t.CatchClause);
        Assert.NotNull(t.FinallyBlock);
    }

    // ── jump statements ──

    [Fact]
    public void ThrowStatement()
    {
        Node s = ParseOneOk("throw x;");
        Assert.Equal(SyntaxKind.ThrowStatement, s.Kind);
        Assert.Equal(SyntaxKind.Identifier, s.AsThrowStatement().Expression!.Kind);
    }

    [Fact]
    public void ReturnStatement_NoExpression()
    {
        Node s = ParseOneOk("return;");
        Assert.Equal(SyntaxKind.ReturnStatement, s.Kind);
        Assert.Null(s.AsReturnStatement().Expression);
    }

    [Fact]
    public void ReturnStatement_WithExpression()
    {
        Node s = ParseOneOk("return x;");
        Assert.Equal(SyntaxKind.Identifier, s.AsReturnStatement().Expression!.Kind);
    }

    [Fact]
    public void BreakStatement_NoLabel()
    {
        Node s = ParseOneOk("break;");
        Assert.Equal(SyntaxKind.BreakStatement, s.Kind);
        Assert.Null(s.AsBreakStatement().Label);
    }

    [Fact]
    public void BreakStatement_WithLabel()
    {
        Node s = ParseOneOk("break loop;");
        BreakStatement b = s.AsBreakStatement();
        Assert.NotNull(b.Label);
        Assert.Equal("loop", b.Label!.AsIdentifier().Text);
    }

    [Fact]
    public void ContinueStatement_WithLabel()
    {
        Node s = ParseOneOk("continue loop;");
        Assert.Equal(SyntaxKind.ContinueStatement, s.Kind);
        Assert.Equal("loop", s.AsContinueStatement().Label!.AsIdentifier().Text);
    }

    // ── with / debugger ──

    [Fact]
    public void WithStatement()
    {
        Node s = ParseOneOk("with (x) ;");
        Assert.Equal(SyntaxKind.WithStatement, s.Kind);
        WithStatement w = s.AsWithStatement();
        Assert.Equal(SyntaxKind.Identifier, w.Expression!.Kind);
        Assert.Equal(SyntaxKind.EmptyStatement, w.Statement!.Kind);
    }

    [Fact]
    public void DebuggerStatement()
    {
        Node s = ParseOneOk("debugger;");
        Assert.Equal(SyntaxKind.DebuggerStatement, s.Kind);
    }

    // ── labeled / expression statements ──

    [Fact]
    public void LabeledStatement()
    {
        Node s = ParseOneOk("outer: ;");
        Assert.Equal(SyntaxKind.LabeledStatement, s.Kind);
        LabeledStatement l = s.AsLabeledStatement();
        Assert.Equal("outer", l.Label!.AsIdentifier().Text);
        Assert.Equal(SyntaxKind.EmptyStatement, l.Statement!.Kind);
    }

    [Fact]
    public void LabeledStatement_LabelingALoop()
    {
        Node s = ParseOneOk("loop: while (x) break loop;");
        LabeledStatement l = s.AsLabeledStatement();
        Assert.Equal(SyntaxKind.WhileStatement, l.Statement!.Kind);
    }

    [Fact]
    public void ExpressionStatement()
    {
        Node s = ParseOneOk("x;");
        Assert.Equal(SyntaxKind.ExpressionStatement, s.Kind);
        Assert.Equal(SyntaxKind.Identifier, s.AsExpressionStatement().Expression!.Kind);
    }

    // ── variable statements (dispatch through parseStatement → Declarations cluster) ──

    [Fact]
    public void VarStatement_SingleDeclaration()
    {
        Node s = ParseOneOk("var x;");
        Assert.Equal(SyntaxKind.VariableStatement, s.Kind);
        Node list = s.AsVariableStatement().DeclarationList!;
        Assert.Equal(SyntaxKind.VariableDeclarationList, list.Kind);
        Assert.Equal(NodeFlags.None, list.Flags & (NodeFlags.Let | NodeFlags.Const));
        Node decl = Assert.Single(list.AsVariableDeclarationList().Declarations!.Nodes);
        Assert.Equal("x", decl.Name()!.AsIdentifier().Text);
        Assert.Null(decl.AsVariableDeclaration().Initializer);
    }

    [Fact]
    public void LetStatement_WithInitializer()
    {
        Node s = ParseOneOk("let x = 1;");
        Node list = s.AsVariableStatement().DeclarationList!;
        Assert.Equal(NodeFlags.Let, list.Flags & NodeFlags.Let);
        Node decl = Assert.Single(list.AsVariableDeclarationList().Declarations!.Nodes);
        Assert.Equal(SyntaxKind.NumericLiteral, decl.AsVariableDeclaration().Initializer!.Kind);
    }

    [Fact]
    public void ConstStatement_MultipleDeclarations()
    {
        Node s = ParseOneOk("const a = 1, b = 2;");
        Node list = s.AsVariableStatement().DeclarationList!;
        Assert.Equal(NodeFlags.Const, list.Flags & NodeFlags.Const);
        Assert.Equal(2, list.AsVariableDeclarationList().Declarations!.Nodes.Length);
    }

    [Fact]
    public void ForStatement_WithVariableDeclarationInitializer()
    {
        // Exercises parseForOrForInOrForOfStatement's variable-declaration-list initializer branch.
        Node s = ParseOneOk("for (var i = 0; i < n; i++) ;");
        ForStatement f = s.AsForStatement();
        Assert.Equal(SyntaxKind.VariableDeclarationList, f.Initializer!.Kind);
    }

    [Fact]
    public void ForOfStatement_WithConstBinding()
    {
        Node s = ParseOneOk("for (const x of xs) ;");
        Assert.Equal(SyntaxKind.ForOfStatement, s.Kind);
        ForInOrOfStatement f = s.AsForInOrOfStatement();
        Assert.Equal(SyntaxKind.VariableDeclarationList, f.Initializer!.Kind);
    }

    // ── catch clause WITH a binding (exercises parseCatchClause's parseVariableDeclaration path) ──

    [Fact]
    public void TryCatch_WithBinding()
    {
        Node s = ParseOneOk("try { } catch (e) { }");
        CatchClause c = s.AsTryStatement().CatchClause!.AsCatchClause();
        Assert.NotNull(c.VariableDeclaration);
        Assert.Equal("e", c.VariableDeclaration!.Name()!.AsIdentifier().Text);
    }

    // ── error recovery: (code, pos, len) diagnostic triples ──

    [Fact]
    public void IfStatement_MissingOpenParen_ReportsExpectedTriple()
    {
        // `if x) ;` — parseExpected(OpenParen) fails at the `x` token (index 3), emitting
        // "'(' expected" (code 1005); the ')' still matches via parseExpectedMatchingBrackets,
        // and the then-statement parses cleanly, so exactly one diagnostic is produced.
        SourceFile sf = Parse("if x) ;");
        Node s = Assert.Single(sf.Statements!.Nodes);
        Assert.Equal(SyntaxKind.IfStatement, s.Kind);
        Diagnostic[] diags = sf.Diagnostics() ?? System.Array.Empty<Diagnostic>();
        Assert.Single(diags);
        Assert.Equal(1005, diags[0].Code());
        Assert.Equal(3, diags[0].Pos());
        Assert.Equal(1, diags[0].Len());
    }

    [Fact]
    public void BlockFollowedByEquals_ReportsDestructuringHint()
    {
        // `{} = x;` — after the block, a trailing `=` triggers parseBlock's dedicated recovery
        // diagnostic (code 2809), which then consumes the `=`.
        SourceFile sf = Parse("{} = x;");
        Diagnostic[] diags = sf.Diagnostics() ?? System.Array.Empty<Diagnostic>();
        Assert.Single(diags);
        Assert.Equal(2809, diags[0].Code());
    }

    [Fact]
    public void MissingSemicolonBetweenIdentifiers_ReportsUnexpectedKeywordOrIdentifier()
    {
        // `zzz qqq` — the first expression statement cannot terminate (no ASI, next token is an
        // identifier with no preceding line break), so parseErrorForMissingSemicolonAfter falls all
        // the way through to "Unexpected keyword or identifier." (code 1434). `zzz`/`qqq` are chosen
        // far from any keyword so no spelling/space suggestion pre-empts the fallback message.
        SourceFile sf = Parse("zzz qqq");
        Diagnostic[] diags = sf.Diagnostics() ?? System.Array.Empty<Diagnostic>();
        Assert.Single(diags);
        Assert.Equal(1434, diags[0].Code());
        Assert.Equal(2, sf.Statements!.Nodes.Length);
    }

    // ── JS-file syntax check surface (checkJSSyntax is a no-op for pure-statement TS above; ensure
    //    a control-flow tree still parses cleanly under ScriptKind.JS) ──

    [Fact]
    public void ControlFlow_ParsesCleanlyInJavaScriptFile()
    {
        Node s = ParseOneOk("if (a) ; else while (b) ;", ScriptKind.JS);
        Assert.Equal(SyntaxKind.IfStatement, s.Kind);
    }
}
