// Ported from tsgo internal/ls — the LSP-currency entry points (ctx, documentURI,
// lsproto.Position → lsproto response unions) that internal/lsp/server.go calls.
//
// HOSTING-NOTE: in Go these are the canonical exported entries of each feature file
// (diagnostics.go, folding.go, documenthighlights.go, ...). The C# LS foundation landed
// those features in the raw UTF-16 offset / file-name currency (see the
// SelectionRanges.cs header), and the fourslash runner calls that currency directly
// (D-012). The LSP currency therefore lives in this partial: each entry resolves the
// document URI to its file name, converts LSP positions through l.converters, reads the
// stored client capabilities (Go: lsproto.GetClientCapabilities(ctx), see
// LanguageService.ClientCapabilities), delegates to the foundation member, and shapes
// the generated response union exactly as the Go entry does. Go's (Resp, error) pairs
// collapse to the response value: every entry below returns a nil error in Go.

using System.Collections.Generic;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Core; // Tristate.IsTrue / IsFalse extension methods
using RequestContext = Ts.Core.RequestContext;
using LsProtoT = Ts.LsProto;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    private static string FileNameOf(LsProtoT.DocumentUri documentURI) =>
        LsProtoT.DocumentUriExtensions.FileName(documentURI);

    private int ToPosition(SourceFile file, LsProtoT.Position position) =>
        (int)Converters().LineAndCharacterToPosition(global::Ts.Ls.Change.SourceFileScriptExtensions.AsScript(file), position);

    // diagnostics.go:25-44 — ProvideDiagnostics.
    public LsProtoT.RelatedFullDocumentDiagnosticReportOrUnchangedDocumentDiagnosticReport ProvideDiagnostics(RequestContext ctx, LsProtoT.DocumentUri uri)
    {
        string fileName = FileNameOf(uri);
        if (UserPreferences().EnableValidation.IsFalse())
        {
            _ = GetProgramAndFile(fileName);
            LsProtoT.Diagnostic[] diagnostics = [];
            return LsProtoT.RelatedFullDocumentDiagnosticReportOrUnchangedDocumentDiagnosticReport.FromFullDocumentDiagnosticReport(new LsProtoT.RelatedFullDocumentDiagnosticReport
            {
                Items = diagnostics,
            });
        }

        return LsProtoT.RelatedFullDocumentDiagnosticReportOrUnchangedDocumentDiagnosticReport.FromFullDocumentDiagnosticReport(new LsProtoT.RelatedFullDocumentDiagnosticReport
        {
            Items = ProvideDiagnostics(fileName, UserPreferences().ReportStyleChecksAsWarnings.IsTrue(), ctx.Cancellation),
        });
    }

    // folding.go:18-32 — ProvideFoldingRange.
    public LsProtoT.FoldingRangesOrNull ProvideFoldingRange(RequestContext ctx, LsProtoT.DocumentUri documentURI)
    {
        LsProtoT.ResolvedFoldingRangeClientCapabilities caps = ClientCapabilities().TextDocument.FoldingRange;
        LsProtoT.FoldingRange[] res = ProvideFoldingRange(FileNameOf(documentURI), caps.LineFoldingOnly, caps.FoldingRange.CollapsedText, ctx.Cancellation);
        return LsProtoT.FoldingRangesOrNull.FromFoldingRanges(res);
    }

    // documenthighlights.go:18-33 — ProvideDocumentHighlights.
    public LsProtoT.DocumentHighlightsOrNull ProvideDocumentHighlights(RequestContext ctx, LsProtoT.DocumentUri documentUri, LsProtoT.Position documentPosition)
    {
        string fileName = FileNameOf(documentUri);
        SourceFile file = GetProgramAndFile(fileName);
        LsProtoT.DocumentHighlight[] documentHighlights = ProvideDocumentHighlights(fileName, ToPosition(file, documentPosition), ctx.Cancellation);
        return LsProtoT.DocumentHighlightsOrNull.FromDocumentHighlights(documentHighlights);
    }

    // documenthighlights.go:35-37 — ProvideMultiDocumentHighlights. The foundation worker
    // searches the current file only (see the DocumentHighlights.cs header), so the
    // response carries the single current-file group the worker produces.
    public LsProtoT.MultiDocumentHighlightsOrNull ProvideMultiDocumentHighlights(RequestContext ctx, LsProtoT.DocumentUri documentUri, LsProtoT.Position documentPosition, LsProtoT.DocumentUri[] filesToSearch)
    {
        string fileName = FileNameOf(documentUri);
        SourceFile file = GetProgramAndFile(fileName);
        LsProtoT.DocumentHighlight[] highlights = ProvideDocumentHighlights(fileName, ToPosition(file, documentPosition), ctx.Cancellation);
        LsProtoT.MultiDocumentHighlight[] multiHighlights = highlights.Length == 0
            ? []
            : [new LsProtoT.MultiDocumentHighlight { Uri = documentUri, Highlights = highlights }];
        return LsProtoT.MultiDocumentHighlightsOrNull.FromMultiDocumentHighlights(multiHighlights);
    }

    // selectionranges.go:13-29 — ProvideSelectionRanges.
    public LsProtoT.SelectionRangesOrNull ProvideSelectionRanges(RequestContext ctx, LsProtoT.SelectionRangeParams @params)
    {
        string fileName = FileNameOf(@params.TextDocument.Uri);
        SourceFile sourceFile = GetProgramAndFile(fileName);
        var positions = new List<int>(@params.Positions.Length);
        foreach (LsProtoT.Position position in @params.Positions)
        {
            positions.Add(ToPosition(sourceFile, position));
        }
        return LsProtoT.SelectionRangesOrNull.FromSelectionRanges(ProvideSelectionRanges(fileName, positions));
    }

    // linkedediting.go:17 — ProvideLinkedEditingRange.
    public LsProtoT.LinkedEditingRangesOrNull ProvideLinkedEditingRange(RequestContext ctx, LsProtoT.LinkedEditingRangeParams @params)
    {
        string fileName = FileNameOf(@params.TextDocument.Uri);
        SourceFile sourceFile = GetProgramAndFile(fileName);
        LsProtoT.LinkedEditingRanges? ranges = ProvideLinkedEditingRange(fileName, ToPosition(sourceFile, @params.Position));
        return ranges is null ? new LsProtoT.LinkedEditingRangesOrNull() : LsProtoT.LinkedEditingRangesOrNull.FromLinkedEditingRanges(ranges);
    }

    // autoinsert.go:12 — ProvideOnAutoInsert (request-context form).
    public LsProtoT.VSOnAutoInsertResponseItemOrNull ProvideOnAutoInsert(RequestContext ctx, LsProtoT.VSOnAutoInsertParams @params) =>
        ProvideOnAutoInsert(ctx.Cancellation, @params);

    // definition.go:17-27 — ProvideDefinition.
    public LsProtoT.LocationOrLocationsOrDefinitionLinksOrNull ProvideDefinition(RequestContext ctx, LsProtoT.DocumentUri documentURI, LsProtoT.Position position)
    {
        if (UserPreferences().PreferGoToSourceDefinition)
        {
            return ProvideSourceDefinition(ctx, documentURI, position);
        }
        string fileName = FileNameOf(documentURI);
        SourceFile file = GetProgramAndFile(fileName);
        return ProvideDefinition(fileName, ToPosition(file, position), ctx.Cancellation) ?? new LsProtoT.LocationOrLocationsOrDefinitionLinksOrNull();
    }

    // definition.go:100 — ProvideTypeDefinition.
    public LsProtoT.LocationOrLocationsOrDefinitionLinksOrNull ProvideTypeDefinition(RequestContext ctx, LsProtoT.DocumentUri documentURI, LsProtoT.Position position)
    {
        string fileName = FileNameOf(documentURI);
        SourceFile file = GetProgramAndFile(fileName);
        return ProvideTypeDefinition(fileName, ToPosition(file, position), ctx.Cancellation) ?? new LsProtoT.LocationOrLocationsOrDefinitionLinksOrNull();
    }

    // sourcedefinition.go:24 — ProvideSourceDefinition.
    public LsProtoT.LocationOrLocationsOrDefinitionLinksOrNull ProvideSourceDefinition(RequestContext ctx, LsProtoT.DocumentUri documentURI, LsProtoT.Position position)
    {
        bool clientSupportsLink = ClientCapabilities().TextDocument.Definition.LinkSupport;
        return ProvideSourceDefinition(FileNameOf(documentURI), position, clientSupportsLink, ctx.Cancellation);
    }

    // symbols.go:24-37 — ProvideDocumentSymbols.
    public LsProtoT.SymbolInformationsOrDocumentSymbolsOrNull ProvideDocumentSymbols(RequestContext ctx, LsProtoT.DocumentUri documentURI)
    {
        bool hierarchical = ClientCapabilities().TextDocument.DocumentSymbol.HierarchicalDocumentSymbolSupport;
        return ProvideDocumentSymbols(FileNameOf(documentURI), hierarchical, ctx.Cancellation);
    }

    // codeactions.go:78 — ProvideCodeActions (request-context form).
    public LsProtoT.CommandOrCodeActionArrayOrNull ProvideCodeActions(RequestContext ctx, LsProtoT.CodeActionParams @params)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        return ProvideCodeActions(@params);
    }

    // inlay_hints.go:24-33 — ProvideInlayHint.
    public LsProtoT.InlayHintsOrNull ProvideInlayHint(RequestContext ctx, LsProtoT.InlayHintParams @params)
    {
        InlayHintsPreferences inlayHintPreferences = UserPreferences().InlayHints;
        if (!IsAnyInlayHintEnabled(inlayHintPreferences))
        {
            return new LsProtoT.InlayHintsOrNull();
        }
        return LsProtoT.InlayHintsOrNull.FromInlayHints(ProvideInlayHint(FileNameOf(@params.TextDocument.Uri), @params.Range, inlayHintPreferences, ctx.Cancellation));
    }

    // rename.go:47-60 — GetRenameInfo.
    public RenameInfo GetRenameInfo(RequestContext ctx, string newName, LsProtoT.DocumentUri documentURI, LsProtoT.Position position)
    {
        string fileName = FileNameOf(documentURI);
        SourceFile sourceFile = GetProgramAndFile(fileName);
        return GetRenameInfo(fileName, ToPosition(sourceFile, position), newName, ctx.Cancellation);
    }

    // rename.go:33-45 — ProvideRename. The foundation rename engine resolves references
    // within the default project (see the Rename.cs header), so the orchestrator's
    // cross-project fan-out reduces to the default project's edits.
    public LsProtoT.WorkspaceEditOrNull ProvideRename(RequestContext ctx, LsProtoT.RenameParams @params, CrossProjectOrchestrator orchestrator)
    {
        string fileName = FileNameOf(@params.TextDocument.Uri);
        SourceFile sourceFile = GetProgramAndFile(fileName);
        LsProtoT.WorkspaceEdit? edit = ProvideRename(fileName, ToPosition(sourceFile, @params.Position), @params.NewName, ctx.Cancellation);
        return edit is null ? new LsProtoT.WorkspaceEditOrNull() : LsProtoT.WorkspaceEditOrNull.FromWorkspaceEdit(edit);
    }

    // callhierarchy.go:976-1008 — ProvidePrepareCallHierarchy.
    public LsProtoT.CallHierarchyItemsOrNull ProvidePrepareCallHierarchy(RequestContext ctx, LsProtoT.DocumentUri documentURI, LsProtoT.Position position)
    {
        string fileName = FileNameOf(documentURI);
        SourceFile file = GetProgramAndFile(fileName);
        LsProtoT.CallHierarchyItem[] items = ProvidePrepareCallHierarchy(fileName, ToPosition(file, position), ctx.Cancellation);
        if (items.Length == 0)
        {
            return new LsProtoT.CallHierarchyItemsOrNull();
        }
        return LsProtoT.CallHierarchyItemsOrNull.FromCallHierarchyItems(items);
    }

    // callhierarchy.go:1010-1054 — ProvideCallHierarchyIncomingCalls. The foundation
    // getIncomingCalls searches the default project's program (see the CallHierarchy.cs
    // header), so the orchestrator's cross-project fan-out reduces to that program.
    public LsProtoT.CallHierarchyIncomingCallsOrNull ProvideCallHierarchyIncomingCalls(RequestContext ctx, LsProtoT.CallHierarchyItem item, CrossProjectOrchestrator orchestrator)
    {
        return LsProtoT.CallHierarchyIncomingCallsOrNull.FromCallHierarchyIncomingCalls(ProvideCallHierarchyIncomingCalls(item, ctx.Cancellation));
    }

    // callhierarchy.go:1056 — ProvideCallHierarchyOutgoingCalls.
    public LsProtoT.CallHierarchyOutgoingCallsOrNull ProvideCallHierarchyOutgoingCalls(RequestContext ctx, LsProtoT.CallHierarchyItem item)
    {
        return LsProtoT.CallHierarchyOutgoingCallsOrNull.FromCallHierarchyOutgoingCalls(ProvideCallHierarchyOutgoingCalls(item, ctx.Cancellation));
    }

    // semantictokens.go:126-146 — ProvideSemanticTokens.
    public LsProtoT.SemanticTokensOrNull ProvideSemanticTokens(RequestContext ctx, LsProtoT.DocumentUri documentURI)
    {
        LsProtoT.SemanticTokens? tokens = ProvideSemanticTokens(FileNameOf(documentURI), ClientCapabilities().TextDocument.SemanticTokens, ctx.Cancellation);
        return tokens is null ? new LsProtoT.SemanticTokensOrNull() : LsProtoT.SemanticTokensOrNull.FromSemanticTokens(tokens);
    }

    // semantictokens.go:148-170 — ProvideSemanticTokensRange.
    public LsProtoT.SemanticTokensOrNull ProvideSemanticTokensRange(RequestContext ctx, LsProtoT.DocumentUri documentURI, LsProtoT.Range rng)
    {
        LsProtoT.SemanticTokens? tokens = ProvideSemanticTokensRange(FileNameOf(documentURI), rng, ClientCapabilities().TextDocument.SemanticTokens, ctx.Cancellation);
        return tokens is null ? new LsProtoT.SemanticTokensOrNull() : LsProtoT.SemanticTokensOrNull.FromSemanticTokens(tokens);
    }
}
