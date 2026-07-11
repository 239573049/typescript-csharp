// CLI wave (tsc type-check-and-report): exercises the Program diagnostics-aggregation surface ported in
// src/Ts.Compiler/Compiler/Program.Diagnostics.cs — the checker-independent stages the tsc flow relies
// on (config-parse, syntactic, bind, sort-and-dedupe) plus the diagnosticwriter formatting the CLI uses.
//
// The full semantic report path (GetSemanticDiagnostics → checker) is verified via the Ts.Cli smoke
// test; here it is skipped because the checker's CheckSourceFile path is still being filled in parallel
// (throws NotImplementedException: checkDecorators for even a trivial legal program).

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.DiagnosticWriter;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Compiler.Tests;

public sealed class ProgramDiagnosticsTests
{
    private const string CurrentDirectory = "/src";

    private static Program BuildProgram(IReadOnlyDictionary<string, string> files, string[] roots)
    {
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var host = CompilerHosts.NewCompilerHost(CurrentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            new CompilerOptions { NoLib = Tristate.True, Types = [] },
            roots,
            new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = CurrentDirectory });
        return new Program(new ProgramOptions { Host = host, Config = config, SingleThreaded = Tristate.True });
    }

    [Fact]
    public void LegalProgram_ConfigSyntacticBind_AreEmpty()
    {
        var program = BuildProgram(
            new Dictionary<string, string> { ["/src/only.ts"] = "export const x = 1;\n" },
            ["/src/only.ts"]);

        Assert.Empty(program.GetConfigFileParsingDiagnostics());
        Assert.Empty(program.GetSyntacticDiagnostics(CancellationToken.None, null));
        Assert.Empty(program.GetBindDiagnostics(CancellationToken.None, null));
        // Note: GetGlobalDiagnostics is intentionally NOT asserted empty — under NoLib the checker's
        // global-type resolution legitimately reports "cannot find global type 'Array'/'Function'/…"
        // (no lib ⇒ no globals). Global diagnostics are a semantic concern, outside this test's
        // config/syntactic/bind scope.
    }

    [Fact]
    public void SyntaxError_ProducesSyntacticDiagnostic()
    {
        // Missing initializer expression → parser (syntactic) diagnostic; no checker involvement.
        var program = BuildProgram(
            new Dictionary<string, string> { ["/src/bad.ts"] = "const x = ;\n" },
            ["/src/bad.ts"]);

        var syn = program.GetSyntacticDiagnostics(CancellationToken.None, null);
        Assert.NotEmpty(syn);
    }

    [Fact]
    public void SortAndDeduplicateDiagnostics_RemovesExactDuplicates()
    {
        var sf = (SourceFile?)null;
        var loc = new TextRange(0, 3);
        var d1 = Diagnostic.NewDiagnostic(sf, loc, global::Ts.Diagnostics.Diagnostics.Unused_ts_expect_error_directive);
        var d2 = Diagnostic.NewDiagnostic(sf, loc, global::Ts.Diagnostics.Diagnostics.Unused_ts_expect_error_directive);

        var result = Program.SortAndDeduplicateDiagnostics([d1, d2]);
        Assert.Single(result);
    }

    [Fact]
    public void SortAndDeduplicateDiagnostics_SortsByPosition()
    {
        var loA = new TextRange(10, 12);
        var loB = new TextRange(2, 4);
        var a = Diagnostic.NewDiagnostic(null, loA, global::Ts.Diagnostics.Diagnostics.Unused_ts_expect_error_directive);
        var b = Diagnostic.NewDiagnostic(null, loB, global::Ts.Diagnostics.Diagnostics.Decorators_are_not_valid_here);

        var result = Program.SortAndDeduplicateDiagnostics([a, b]);
        Assert.Equal(2, result.Length);
        // Lower position sorts first (CompareDiagnostics orders by file then pos).
        Assert.Equal(2, result[0].Pos());
        Assert.Equal(10, result[1].Pos());
    }

    [Fact]
    public void DiagnosticWriter_FormatsCompilerDiagnostic_ForReporterPath()
    {
        // The CLI's plain (non-pretty) reporter path funnels each diagnostic through
        // DiagnosticWriter.WriteFormatDiagnostic; assert it renders the message text the CLI would print.
        var diag = Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Unused_ts_expect_error_directive);
        var formatOpts = new FormattingOptions
        {
            NewLine = "\n",
            ComparePathsOptions = new global::Ts.TsPath.ComparePathsOptions { CurrentDirectory = CurrentDirectory, UseCaseSensitiveFileNames = true },
            Locale = global::Ts.Locale.Locale.Default,
        };

        using var sw = new StringWriter();
        global::Ts.DiagnosticWriter.DiagnosticWriter.WriteFormatDiagnostic(sw, diag, formatOpts);
        var output = sw.ToString();

        Assert.Contains("error", output);
        Assert.Contains("Unused '@ts-expect-error' directive.", output);
    }
}
