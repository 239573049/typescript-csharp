// Ported from tsgo internal/ls/diagnostics.go.
//
// PORT-NOTE: the LSP-currency entry (ctx, DocumentUri → pull-report union, with the
// EnableValidation gate of diagnostics.go:28-35) lives in LanguageService.Lsp.cs and
// delegates here; this file-name form takes ReportStyleChecksAsWarnings explicitly so
// direct (non-LSP) callers can use it. Positions are UTF-16 code units throughout.

using System.Collections.Generic;
using System.Threading;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // diagnostics.go:14 — getAllDiagnostics collects all diagnostics for a file:
    // syntactic, semantic, suggestion, and (when declarations are emitted) declaration
    // diagnostics.
    private static List<global::Ts.Ast.Diagnostic> GetAllDiagnostics(
        CancellationToken ctx,
        global::Ts.Compiler.Program program,
        global::Ts.Ast.SourceFile file)
    {
        var diags = new List<global::Ts.Ast.Diagnostic>();
        diags.AddRange(program.GetSyntacticDiagnostics(ctx, file));
        diags.AddRange(program.GetSemanticDiagnostics(ctx, file));
        diags.AddRange(program.GetSuggestionDiagnostics(ctx, file));
        if (program.Options().GetEmitDeclarations())
        {
            diags.AddRange(program.GetDeclarationDiagnostics(ctx, file));
        }
        return diags;
    }

    // diagnostics.go:25 — ProvideDiagnostics.
    public global::Ts.LsProto.Diagnostic[] ProvideDiagnostics(
        string fileName,
        bool reportStyleChecksAsWarnings = false,
        CancellationToken cancellationToken = default)
    {
        var file = GetProgramAndFile(fileName);

        var diagnostics = GetAllDiagnostics(cancellationToken, program, file);

        return ToLSPDiagnostics(reportStyleChecksAsWarnings, diagnostics);
    }

    // diagnostics.go:46 — toLSPDiagnostics.
    internal global::Ts.LsProto.Diagnostic[] ToLSPDiagnostics(
        bool reportStyleChecksAsWarnings,
        params IReadOnlyList<global::Ts.Ast.Diagnostic>[] diagnostics)
    {
        var size = 0;
        foreach (var diagSlice in diagnostics)
        {
            size += diagSlice.Count;
        }
        var lspDiagnostics = new List<global::Ts.LsProto.Diagnostic>(size);
        foreach (var diagSlice in diagnostics)
        {
            foreach (var diag in diagSlice)
            {
                lspDiagnostics.Add(LsDeps.DiagnosticToLSPPull(diag, reportStyleChecksAsWarnings));
            }
        }
        return lspDiagnostics.ToArray();
    }
}
