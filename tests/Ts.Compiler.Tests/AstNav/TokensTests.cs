// Tests for internal/astnav/tokens.go (Ts.AstNav.Tokens).
//
// PORT-NOTE: the Go tests (tokens_test.go) are differential baselines that parse the official
// TypeScript submodule corpus (repo.TypeScriptSubmodulePath) with parser.ParseSourceFile and
// compare against Node.js-evaluated tsserver output (jstest). All three prerequisites — the
// parser (in-flight, phase 3), the TS submodule, and Node.js — are unavailable here, so those
// baseline tests are represented as skipped placeholders below and are to be landed once the
// parser is green. In the meantime these hand-authored unit tests build minimal AST trees with
// the NodeFactory (no parser) and assert AstNav's position-hit behaviour, its scanner-synthesized
// token cache (GetOrCreateToken pointer identity), and UTF-16 code-unit position fidelity
// (surrogate-pair identifier), matching the coverage the task requested.

using Ts.Ast;
using Ts.Core;
using Xunit;

namespace Ts.AstNav.Tests;

public class TokensTests
{
    // Builds a minimal parsed-shape SourceFile for `<identText>;` without invoking the parser:
    //   SourceFile[0,N) -> Statements[0,N-...) -> ExpressionStatement -> Identifier(identText)
    //   plus a synthetic-in-text semicolon (only produced by the scanner) and an EndOfFile token.
    // Positions are UTF-16 code units. Returns the pieces tests assert identity against.
    private static (SourceFile File, Node Ident, Node ExprStmt, Node Eof) BuildIdentStatementFile(string identText)
    {
        var text = identText + ";";
        var factory = new NodeFactory();

        var ident = factory.NewIdentifier(identText);
        ident.Loc = new TextRange(0, identText.Length);

        var exprStmt = factory.NewExpressionStatement(ident);
        exprStmt.Loc = new TextRange(0, identText.Length + 1); // includes the ';'
        ident.Parent = exprStmt;

        var statements = factory.NewNodeList([exprStmt]);
        statements.Loc = new TextRange(0, identText.Length + 1);

        var eof = factory.NewToken(SyntaxKind.EndOfFile);
        eof.Loc = new TextRange(text.Length, text.Length);

        var opts = new SourceFileParseOptions { FileName = "/test.ts" }; // Path unused by astnav
        var file = factory.NewSourceFile(opts, text, statements, eof).AsSourceFile();
        file.Loc = new TextRange(0, text.Length);
        file.LanguageVariant = LanguageVariant.Standard;
        exprStmt.Parent = file.AsNode();
        eof.Parent = file.AsNode();
        return (file, ident, exprStmt, eof);
    }

    [Fact]
    public void GetTokenAtPosition_ReturnsRealIdentifierNode()
    {
        var (file, ident, _, _) = BuildIdentStatementFile("a");

        var token = Tokens.GetTokenAtPosition(file, 0);

        Assert.Equal(SyntaxKind.Identifier, token.Kind);
        Assert.Equal(0, token.Pos);
        Assert.Equal(1, token.End);
        // The identifier is a real AST node, returned by reference (not synthesized).
        Assert.Same(ident, token);
    }

    [Fact]
    public void GetTouchingToken_ReturnsRealIdentifierNode()
    {
        var (file, ident, _, _) = BuildIdentStatementFile("a");

        var token = Tokens.GetTouchingToken(file, 0);

        Assert.Same(ident, token);
    }

    [Fact]
    public void GetTokenAtPosition_SynthesizesScannerToken_AtSemicolon()
    {
        var (file, _, exprStmt, _) = BuildIdentStatementFile("a");

        // Position 1 sits at the ';', which is not a stored AST node; astnav runs the scanner
        // and constructs a synthesized SemicolonToken via SourceFile.GetOrCreateToken.
        var token = Tokens.GetTokenAtPosition(file, 1);

        Assert.Equal(SyntaxKind.SemicolonToken, token.Kind);
        Assert.Equal(1, token.Pos);
        Assert.Equal(2, token.End);
        Assert.Same(exprStmt, token.Parent); // synthesized token is parented to its containing node
    }

    [Fact]
    public void GetTokenAtPosition_SynthesizedTokenIsCached_PointerEquality()
    {
        var (file, _, _, _) = BuildIdentStatementFile("a");

        // Mirrors tokens_test.go "pointer equality": repeated calls return the same cached object.
        var a = Tokens.GetTokenAtPosition(file, 1);
        var b = Tokens.GetTokenAtPosition(file, 1);

        Assert.Same(a, b);
    }

    [Fact]
    public void GetTokenAtPosition_RealNodeIsStablePointer()
    {
        var (file, _, _, _) = BuildIdentStatementFile("a");

        Assert.Same(Tokens.GetTokenAtPosition(file, 0), Tokens.GetTokenAtPosition(file, 0));
    }

    [Fact]
    public void FindPrecedingToken_AtEndOfFile_ReturnsSemicolon()
    {
        var (file, _, _, _) = BuildIdentStatementFile("a");

        // Position 2 is the end of file (after the ';'); the preceding token is the ';'.
        var token = Tokens.FindPrecedingToken(file, 2);

        Assert.NotNull(token);
        Assert.Equal(SyntaxKind.SemicolonToken, token!.Kind);
        Assert.Equal(1, token.Pos);
        Assert.Equal(2, token.End);
    }

    [Fact]
    public void Positions_AreUtf16CodeUnits_SurrogatePairIdentifier()
    {
        // U+1D54F (MATHEMATICAL DOUBLE-STRUCK CAPITAL X) is a valid identifier char occupying two
        // UTF-16 code units. The identifier must span [0,2) and the ';' must land at [2,3),
        // confirming AstNav/scanner positions are UTF-16 code units (positionmap.go dropped).
        const string x = "\U0001D54F"; // length 2 in UTF-16
        Assert.Equal(2, x.Length);
        var (file, ident, _, _) = BuildIdentStatementFile(x);

        var identToken = Tokens.GetTokenAtPosition(file, 0);
        Assert.Same(ident, identToken);
        Assert.Equal(0, identToken.Pos);
        Assert.Equal(2, identToken.End);

        var semi = Tokens.GetTokenAtPosition(file, 2);
        Assert.Equal(SyntaxKind.SemicolonToken, semi.Kind);
        Assert.Equal(2, semi.Pos);
        Assert.Equal(3, semi.End);
    }

    [Fact]
    public void FindNextToken_FromIdentifier_ReturnsSemicolon()
    {
        var (file, ident, _, _) = BuildIdentStatementFile("a");

        // The token following the identifier `a` is the synthesized `;`.
        var next = Tokens.FindNextToken(ident, file.AsNode(), file);

        Assert.NotNull(next);
        Assert.Equal(SyntaxKind.SemicolonToken, next!.Kind);
        Assert.Equal(1, next.Pos);
        Assert.Equal(2, next.End);
    }

    [Fact]
    public void FindChildOfKind_ReturnsRealIdentifierChild()
    {
        var (file, ident, exprStmt, _) = BuildIdentStatementFile("a");

        var child = Tokens.FindChildOfKind(exprStmt, SyntaxKind.Identifier, file);

        Assert.Same(ident, child);
    }

    [Fact]
    public void FindChildOfKind_ReturnsSynthesizedSemicolonChild()
    {
        var (file, _, exprStmt, _) = BuildIdentStatementFile("a");

        var child = Tokens.FindChildOfKind(exprStmt, SyntaxKind.SemicolonToken, file);

        Assert.NotNull(child);
        Assert.Equal(SyntaxKind.SemicolonToken, child!.Kind);
        Assert.Equal(1, child.Pos);
        Assert.Equal(2, child.End);
    }

    [Fact]
    public void FindChildOfKind_ReturnsNull_WhenAbsent()
    {
        var (file, _, exprStmt, _) = BuildIdentStatementFile("a");

        Assert.Null(Tokens.FindChildOfKind(exprStmt, SyntaxKind.ColonToken, file));
    }

    // ── Ported Go baseline/parser-dependent tests (pending the phase-3 parser) ───────────────────
    // These require parser.ParseSourceFile + the TS submodule corpus + Node.js (jstest); land them
    // once the parser is green. Kinds/positions to assert are carried in the skip reason so they
    // are not lost.

    [Fact(Skip = "tokens_test.go TestGetTokenAtPosition baseline: needs parser + TS submodule + Node.js")]
    public void GetTokenAtPosition_Baseline_MapCodeTs() { }

    [Fact(Skip = "tokens_test.go TestGetTouchingPropertyName baseline: needs parser + TS submodule + Node.js")]
    public void GetTouchingPropertyName_Baseline_MapCodeTs() { }

    [Fact(Skip = "tokens_test.go TestFindPrecedingToken baseline: needs parser + TS submodule + Node.js")]
    public void FindPrecedingToken_Baseline_MapCodeTs() { }

    [Fact(Skip = "tokens_test.go TestFindNextToken baseline: needs parser + TS submodule")]
    public void FindNextToken_Baseline_MapCodeTs() { }

    // tokens_test.go TestGetTokenAtPosition/"JSDoc type assertion" — needs parser to parse
    // `const s = /**@type {string}*/(x)`; expect GetTouchingPropertyName not to panic and return
    // Identifier or ParenthesizedExpression.
    [Fact(Skip = "tokens_test.go JSDoc type assertion regression: needs parser (JS parse + lazy JSDoc)")]
    public void GetTouchingPropertyName_JSDocTypeAssertion_DoesNotThrow() { }

    // tokens_test.go TestUnitFindPrecedingToken — parser-built cases: "after dot in jsdoc"
    // (position 839 -> DotToken) and "after comma in parameter list" `takesCb((n, s, ))`
    // (position 15 -> CommaToken). The `<<` rescan and JSX-child paths are exercised by the
    // mapCode.ts baseline corpus above.
    [Fact(Skip = "tokens_test.go TestUnitFindPrecedingToken: needs parser.ParseSourceFile")]
    public void FindPrecedingToken_UnitCases() { }
}
