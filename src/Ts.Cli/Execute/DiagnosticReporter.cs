// Ported from tsgo internal/execute/tsc/diagnostics.go (the reporter surface).
//
// SCOPE (type-check-and-report): CreateDiagnosticReporter, CreateReportErrorSummary, the pretty/plain
// selection (shouldBePretty/defaultIsPretty), and getFormatOptsOfSys are ported. The colors struct
// (bold/blue/blueBackground/brightWhite) and the build/watch status reporters are out of scope for
// this wave (they serve --build / --watch, which are stubbed) and are not ported here.
//
// §1.5: the diagnosticwriter is a sibling namespace; its static class is fully qualified with global::.
// PORT-NOTE: tsgo wraps ast.Diagnostic via diagnosticwriter.WrapASTDiagnostic; the C# diagnosticwriter
// consumes Ts.Ast.Diagnostic directly (the adapter collapsed during porting), so WrapASTDiagnostic /
// FromASTDiagnostics have no call here.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.DiagnosticWriter;

namespace Ts.Execute;

// diagnostics.go:26 — DiagnosticReporter = func(*ast.Diagnostic)
public delegate void DiagnosticReporter(Diagnostic diagnostic);

// diagnostics.go:130 — DiagnosticsReporter = func([]*ast.Diagnostic)
public delegate void DiagnosticsReporter(IReadOnlyList<Diagnostic> diagnostics);

public static class DiagnosticReporters
{
    // diagnostics.go:15-24
    private static FormattingOptions GetFormatOptsOfSys(ISystem sys, global::Ts.Locale.Locale locale) =>
        new()
        {
            NewLine = "\n",
            ComparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
            {
                CurrentDirectory = sys.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = sys.FS().UseCaseSensitiveFileNames(),
            },
            Locale = locale,
        };

    // diagnostics.go:28
    public static void QuietDiagnosticReporter(Diagnostic diagnostic) { }

    // diagnostics.go:30-44
    public static DiagnosticReporter CreateDiagnosticReporter(ISystem sys, System.IO.TextWriter w, global::Ts.Locale.Locale locale, CompilerOptions options)
    {
        if (options.Quiet.IsTrue())
        {
            return QuietDiagnosticReporter;
        }
        var formatOpts = GetFormatOptsOfSys(sys, locale);
        if (ShouldBePretty(sys, options))
        {
            return diagnostic =>
            {
                global::Ts.DiagnosticWriter.DiagnosticWriter.FormatDiagnosticWithColorAndContext(w, diagnostic, formatOpts);
                w.Write(formatOpts.NewLine);
            };
        }
        return diagnostic =>
        {
            global::Ts.DiagnosticWriter.DiagnosticWriter.WriteFormatDiagnostic(w, diagnostic, formatOpts);
        };
    }

    // diagnostics.go:46-54
    private static bool DefaultIsPretty(ISystem sys)
    {
        if (sys.GetEnvironmentVariable("NO_COLOR") != "")
        {
            return false;
        }
        if (sys.GetEnvironmentVariable("FORCE_COLOR") != "")
        {
            return true;
        }
        return sys.WriteOutputIsTTY();
    }

    // diagnostics.go:56-61
    private static bool ShouldBePretty(ISystem sys, CompilerOptions? options)
    {
        if (options is null || options.Pretty.IsUnknown())
        {
            return DefaultIsPretty(sys);
        }
        return options.Pretty.IsTrue();
    }

    // diagnostics.go:132
    public static void QuietDiagnosticsReporter(IReadOnlyList<Diagnostic> diagnostics) { }

    // diagnostics.go:134-142
    public static DiagnosticsReporter CreateReportErrorSummary(ISystem sys, global::Ts.Locale.Locale locale, CompilerOptions options)
    {
        if (ShouldBePretty(sys, options))
        {
            var formatOpts = GetFormatOptsOfSys(sys, locale);
            return diagnostics =>
            {
                global::Ts.DiagnosticWriter.DiagnosticWriter.WriteErrorSummaryText(sys.Writer(), diagnostics, formatOpts);
            };
        }
        return QuietDiagnosticsReporter;
    }
}
