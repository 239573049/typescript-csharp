// Tests for internal/parser (Ts.Parser.Parser).
//
// SCOPE NOTE (phase 3, R24): The Go parser tests (parser_test.go) plus the G3.1/G3.2 gates are
// differential baselines over the ~50k-file corpus via a dumptool AST dump, which is blocked
// (tsgo is a read-only non-git snapshot; dumptool cannot be built). These are hand-authored unit
// tests asserting the parser's end-to-end pipeline and produced-structure for the currently
// ported surface: the recursive-descent core (state machine, list handling, error recovery
// machinery), node finishing, source-file construction, pooling, and the LargeStackExecutor
// entry (R01). The full statement/expression/type/JSX/JSDoc grammar bodies are scaffolded
// (NotImplementedException stubs) and are covered as they are ported; see the port report.

using Ts.Ast;
using Ts.Core;
using Ts.Parser;
using Ts.TsPath;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserTests
{
    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile Parse(string text, ScriptKind kind = ScriptKind.TS, string fileName = "/test.ts") =>
        global::Ts.Parser.Parser.ParseSourceFile(Opts(fileName), text, kind);

    [Fact]
    public void EmptyFile_ProducesEmptySourceFileWithEofAtZero()
    {
        SourceFile sf = Parse("");

        Assert.Equal(SyntaxKind.SourceFile, sf.Kind);
        Assert.NotNull(sf.Statements);
        Assert.Empty(sf.Statements!.Nodes);
        Assert.NotNull(sf.EndOfFileToken);
        Assert.Equal(SyntaxKind.EndOfFile, sf.EndOfFileToken!.Kind);
        Assert.Equal(0, sf.Pos);
        Assert.Equal(0, sf.End);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        Assert.Equal(ScriptKind.TS, sf.ScriptKind);
        Assert.False(sf.IsDeclarationFile);
    }

    [Fact]
    public void TriviaOnlyFile_ParsesToEmptyStatementsNoDiagnostics()
    {
        SourceFile sf = Parse("  // a line comment\n  /* block */\n");

        Assert.Empty(sf.Statements!.Nodes);
        Assert.Equal(SyntaxKind.EndOfFile, sf.EndOfFileToken!.Kind);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        // EOF token full-start includes the leading trivia; the source-file end is at text length.
        Assert.Equal("  // a line comment\n  /* block */\n".Length, sf.End);
    }

    [Fact]
    public void ShebangOnlyFile_IsSkippedAsTrivia()
    {
        SourceFile sf = Parse("#!/usr/bin/env node\n");
        Assert.Empty(sf.Statements!.Nodes);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
    }

    [Fact]
    public void DeclarationFileName_SetsIsDeclarationFile()
    {
        SourceFile sf = Parse("", ScriptKind.TS, "/lib.d.ts");
        Assert.True(sf.IsDeclarationFile);
        // The whole file parses in ambient context.
        Assert.Equal(NodeFlags.Ambient, sf.Flags & NodeFlags.Ambient);
    }

    [Fact]
    public void ScriptKind_And_LanguageVariant_ArePropagated()
    {
        SourceFile tsx = Parse("", ScriptKind.Tsx, "/c.tsx");
        Assert.Equal(ScriptKind.Tsx, tsx.ScriptKind);
        Assert.Equal(LanguageVariant.Jsx, tsx.LanguageVariant);

        SourceFile ts = Parse("", ScriptKind.TS);
        Assert.Equal(LanguageVariant.Standard, ts.LanguageVariant);
    }

    [Fact]
    public void EmptyJson_ParsesViaJsonPath()
    {
        SourceFile sf = Parse("", ScriptKind.Json, "/tsconfig.json");
        Assert.Equal(SyntaxKind.SourceFile, sf.Kind);
        Assert.Empty(sf.Statements!.Nodes);
        Assert.Equal(SyntaxKind.EndOfFile, sf.EndOfFileToken!.Kind);
    }

    [Fact]
    public void RepeatedParses_ProduceIndependentSourceFiles()
    {
        // Exercises the ObjectPool<Parser> reuse path (put/get + ClearForReturn).
        SourceFile a = Parse("// first\n");
        SourceFile b = Parse("// second\n");
        Assert.NotSame(a, b);
        Assert.NotSame(a.Statements, b.Statements);
        // Each parse uses its own NodeFactory, so node counts are independent (not accumulated).
        Assert.True(a.NodeCount >= 1); // at least the SourceFile + EOF token
        Assert.True(b.NodeCount >= 1);
    }

    [Fact]
    public void UnexpectedCloseBrace_RecoversWithDeclarationOrStatementExpected()
    {
        // First significant token is a lone `}`: the list machinery cannot start a source element,
        // so ParsingContextErrors emits Declaration_or_statement_expected (code 1128) at that token,
        // then consumes it and terminates at EOF. Verifies the parse-diagnostic (code, pos, len) triple.
        SourceFile sf = Parse("}");
        Diagnostic[] diags = sf.Diagnostics() ?? System.Array.Empty<Diagnostic>();
        Assert.Single(diags);
        Assert.Equal(1128, diags[0].Code());
        Assert.Equal(0, diags[0].Pos());
        Assert.Equal(1, diags[0].Len());
    }

    [Fact]
    public void LargeStackExecutor_HandlesLargeTriviaBuffer()
    {
        // Smoke test that the 64MB large-stack entry (R01) dispatches and the scanner traverses a
        // large source buffer without error. NOTE: this is NOT the deep-recursion canary — that
        // requires the expression parser (in-flight); it is tracked in the port report.
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 20000; i++)
        {
            sb.Append("// comment line ").Append(i).Append('\n');
        }
        SourceFile sf = Parse(sb.ToString());
        Assert.Empty(sf.Statements!.Nodes);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
    }

    [Fact]
    public void ParseSourceFile_ThrowsWhenScriptKindUnknown()
    {
        Assert.Throws<System.InvalidOperationException>(() => Parse("", ScriptKind.Unknown));
    }
}
