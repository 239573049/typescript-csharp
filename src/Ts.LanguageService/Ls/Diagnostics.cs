// Ported from tsgo internal/ls/diagnostics.go.
//
// PORT-NOTE: the Go entry point reads the user preferences (EnableValidation,
// ReportStyleChecksAsWarnings) and the client capabilities from the request context.
// The C# LS foundation has no per-request context carrier yet, so the
// ReportStyleChecksAsWarnings preference is an explicit parameter and the
// EnableValidation gate (diagnostics.go:28-35, empty result when validation is off)
// stays with the deferred preferences layer — callers that want it off simply do not
// ask for diagnostics. The lsproto pull-report union wrapper
// (RelatedFullDocumentDiagnosticReportOrUnchangedDocumentDiagnosticReport) is part of
// the LSP server wire layer; this port returns the report's Items directly.
// Positions are UTF-16 code units throughout.

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
        // PORT-NOTE: Go appends program.GetSuggestionDiagnostics here (diagnostics.go:18).
        // That compiler surface is deferred (phase 6; see Program.Diagnostics.cs header),
        // so the append lands with it.
        if (program.Options().GetEmitDeclarations())
        {
            // Faithful call shape: the C# GetDeclarationDiagnostics is a compiler-side
            // NotImplementedException stub until the emit host lands (phase 6).
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
