// Ported from tsgo internal/api/session.go (lines 2863-3211: the diagnostics family,
// resolveOptionalSourceFile, and the language-service handlers — references, signature
// usages, completions, referenced symbols). Go function order preserved; handlers that
// route through setupLanguageService activate once that seam (Session.cs) lands.
//
// PORT-NOTE (diagnostics ctx): Go wraps ctx with core.WithCheckerLifetime(ctx,
// core.CheckerLifetimeDiagnostics) before program diagnostics; the C# RequestContext
// carries the same via ctx.WithCheckerLifetime(...) and Program methods take the
// cancellation token off it (Program.Diagnostics.cs).

using System;
using System.Collections.Generic;
using Ts.Checker;

namespace Ts.Api;

public sealed partial class Session
{
    // handleGetSyntacticDiagnostics returns syntactic diagnostics for a file or all files.
    // (session.go:2863-2884)
    private DiagnosticResponse[] HandleGetSyntacticDiagnostics(global::Ts.Core.RequestContext ctx, GetDiagnosticsParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = ResolveOptionalSourceFile(program, @params.File);

        global::Ts.Ast.Diagnostic[] diags = program.GetSyntacticDiagnostics(ctx.Cancellation, sourceFile);
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetBindDiagnostics returns bind diagnostics for a file or all files.
    // (session.go:2886-2907)
    private DiagnosticResponse[] HandleGetBindDiagnostics(global::Ts.Core.RequestContext ctx, GetDiagnosticsParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = ResolveOptionalSourceFile(program, @params.File);

        global::Ts.Ast.Diagnostic[] diags = program.GetBindDiagnostics(ctx.Cancellation, sourceFile);
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetSemanticDiagnostics returns semantic diagnostics for a file or all files.
    // (session.go:2909-2930)
    private DiagnosticResponse[] HandleGetSemanticDiagnostics(global::Ts.Core.RequestContext ctx, GetDiagnosticsParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = ResolveOptionalSourceFile(program, @params.File);

        global::Ts.Ast.Diagnostic[] diags = program.GetSemanticDiagnostics(ctx.Cancellation, sourceFile);
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetSuggestionDiagnostics returns suggestion diagnostics for a file or all files.
    // (session.go:2932-2953)
    private DiagnosticResponse[] HandleGetSuggestionDiagnostics(global::Ts.Core.RequestContext ctx, GetDiagnosticsParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = ResolveOptionalSourceFile(program, @params.File);

        global::Ts.Ast.Diagnostic[] diags = program.GetSuggestionDiagnostics(ctx.Cancellation, sourceFile);
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetDeclarationDiagnostics returns declaration diagnostics for a file or all
    // files. (session.go:2955-2976)
    private DiagnosticResponse[] HandleGetDeclarationDiagnostics(global::Ts.Core.RequestContext ctx, GetDiagnosticsParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.SourceFile? sourceFile = ResolveOptionalSourceFile(program, @params.File);

        global::Ts.Ast.Diagnostic[] diags = program.GetDeclarationDiagnostics(ctx.Cancellation, sourceFile);
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetConfigFileParsingDiagnostics returns config file parsing diagnostics.
    // (session.go:2978-2990)
    private DiagnosticResponse[] HandleGetConfigFileParsingDiagnostics(global::Ts.Core.RequestContext ctx, GetProjectDiagnosticsParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.Diagnostic[] diags = program.GetConfigFileParsingDiagnostics();
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetProgramDiagnostics returns program-wide diagnostics, including options
    // diagnostics. (session.go:2992-3004)
    private DiagnosticResponse[] HandleGetProgramDiagnostics(global::Ts.Core.RequestContext ctx, GetProjectDiagnosticsParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.Diagnostic[] diags = program.GetProgramDiagnostics();
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // handleGetGlobalDiagnostics returns global (non-file-specific) semantic diagnostics.
    // (session.go:3006-3042)
    private DiagnosticResponse[] HandleGetGlobalDiagnostics(global::Ts.Core.RequestContext ctx, GetProjectDiagnosticsParams @params)
    {
        ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Diagnostics);
        SnapshotData sd = GetSnapshotData(@params.Snapshot);

        global::Ts.Project.Project proj = sd.GetProject(@params.Project);

        global::Ts.Compiler.Program? program = proj.GetProgram();
        if (program is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: project has no program");
        }

        // Global diagnostics are accumulated lazily by the project's checker pool as
        // files are checked. Force a full semantic pass so any global (non-file-specific)
        // diagnostics are produced; otherwise this would return an empty result for
        // projects using an external checker pool (the typical API case), since
        // compiler.Program.GetGlobalDiagnostics only reports for the internal pool.
        program.GetSemanticDiagnostics(ctx.Cancellation, null);

        // Go: core.Filter(proj.GetProjectDiagnostics(ctx), func(d) { return d.File() == nil }).
        global::Ts.Ast.Diagnostic[] projectDiagnostics = proj.GetProjectDiagnostics(ctx);
        int count = 0;
        foreach (global::Ts.Ast.Diagnostic d in projectDiagnostics)
        {
            if (d.File() is null)
            {
                count++;
            }
        }
        var diags = new global::Ts.Ast.Diagnostic[count];
        int n = 0;
        foreach (global::Ts.Ast.Diagnostic d in projectDiagnostics)
        {
            if (d.File() is null)
            {
                diags[n++] = d;
            }
        }
        return ProtoFunctions.NewDiagnosticResponses(diags);
    }

    // resolveOptionalSourceFile resolves an optional DocumentIdentifier to a source file.
    // Returns nil if the identifier is nil (meaning all files). (session.go:3044-3055)
    private global::Ts.Ast.SourceFile? ResolveOptionalSourceFile(global::Ts.Compiler.Program program, DocumentIdentifier? file)
    {
        if (file is null)
        {
            return null;
        }
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile(file.ToFileName());
        if (sourceFile is null)
        {
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {file}");
        }
        return sourceFile;
    }

    // handleGetReferencesToSymbolInFile returns node handles for all identifiers in a file
    // that reference the given symbol. (session.go:3057-3080)
    private NodeHandle[]? HandleGetReferencesToSymbolInFile(global::Ts.Core.RequestContext ctx, GetReferencesToSymbolInFileParams @params)
    {
        CheckerSetup setup = SetupChecker(ctx, @params.Snapshot, @params.Project);
        try
        {
            global::Ts.Ast.Symbol symbol = setup.ResolveSymbolHandle(@params.Symbol)!;
            if (symbol is null)
            {
                return null;
            }

            global::Ts.Ast.SourceFile? sourceFile = setup.Program.GetSourceFile(@params.File.ToFileName());
            if (sourceFile is null)
            {
                throw new ApiRequestException($"{ProtoErrors.ErrClientError}: source file not found: {@params.File}");
            }

            // Go: nodes := setup.checker.GetReferencesToSymbolInFile(sourceFile, symbol);
            // then one sd.nodeHandleFrom(node) per entry (session.go:3074-3078). The
            // checker core is ported into Checker.ApiExports.cs (services.go:576-687).
            global::Ts.Ast.Node[] nodes = setup.Checker.GetReferencesToSymbolInFile(sourceFile, symbol);
            var result = new NodeHandle[nodes.Length];
            for (int i = 0; i < nodes.Length; i++)
            {
                result[i] = setup.Sd.NodeHandleFrom(nodes[i]);
            }
            return result;
        }
        finally
        {
            setup.Done();
        }
    }

    // session.go:3082-3122.
    private SignatureUsageResponse[]? HandleGetSignatureUsages(global::Ts.Core.RequestContext ctx, GetSignatureUsagesParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);
        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.Node? signatureDecl = sd.ResolveNodeHandle(program, @params.SignatureDecl);
        if (signatureDecl is null)
        {
            // Go: resolveNodeHandle err → returned to the client (session.go:3091-3093);
            // the C# port throws inside ResolveNodeHandle, so this is the defensive path.
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: signature declaration handle could not be resolved");
        }

        // Seams (throws at runtime) until the LS host adapter lands; the tail below is the
        // Go body ported verbatim so it activates with the seam.
        global::Ts.Ls.LanguageService? langSvc = SetupLanguageService(sd, program, @params.Project, "");

        List<global::Ts.Ls.SignatureUsage> usages = langSvc!.GetSignatureUsages(ctx.Cancellation, signatureDecl);
        if (usages is null)
        {
            return null;
        }

        var result = new List<SignatureUsageResponse>(usages.Count);
        foreach (global::Ts.Ls.SignatureUsage u in usages)
        {
            var entry = new SignatureUsageResponse
            {
                Name = sd.NodeHandleFrom(u.Name),
            };
            if (u.Call is not null)
            {
                entry.Call = sd.NodeHandleFrom(u.Call);
            }
            result.Add(entry);
        }
        return [.. result];
    }

    // handleGetCompletionsAtPosition returns completions at a position in a document.
    // (session.go:3124-3179)
    internal CompletionInfoResponse? HandleGetCompletionsAtPosition(global::Ts.Core.RequestContext ctx, GetCompletionsAtPositionParams @params)
    {
        if (@params.IncludeSymbol)
        {
            ctx = ctx.WithCheckerLifetime(global::Ts.Core.CheckerLifetime.Api);
        }
        SnapshotData sd = GetSnapshotData(@params.Snapshot);
        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile(@params.File.ToFileName());
        if (sourceFile is null)
        {
            return null;
        }
        // Seams (throws at runtime) until the LS host adapter lands; the tail below is the
        // Go body ported verbatim so it activates with the seam.
        global::Ts.Ls.LanguageService? langSvc = SetupLanguageService(sd, program, @params.Project, "");

        // PORT-NOTE(api): Go langSvc.GetCompletionsAtPosition(ctx, sourceFile, internalPos,
        // triggerCharacter, includeSymbol) passes the source file and a UTF-8 offset; the
        // C# LS entry (Ls/Completions.cs:42) takes fileName + a UTF-16 position and resolves
        // the file itself — the same worker, and UTF-16 is native here (plan/01).
        global::Ts.Ls.CompletionList? result = langSvc!.GetCompletionsAtPosition(
            @params.File.ToFileName(), (int)@params.Position, @params.TriggerCharacter, @params.IncludeSymbol, ctx.Cancellation);
        if (result is null)
        {
            return null;
        }
        var entries = new List<CompletionEntryResponse>(result.Items?.Length ?? 0);
        foreach (global::Ts.Ls.CompletionItem item in result.Items ?? [])
        {
            var entry = new CompletionEntryResponse
            {
                Name = item.Item?.Label ?? "",
                SortText = item.Item?.SortText,
                InsertText = item.Item?.InsertText,
                FilterText = item.Item?.FilterText,
                Detail = item.Item?.Detail,
            };
            if (item.Item?.Kind is not null)
            {
                entry.Kind = (uint)item.Item.Kind!.Value;
            }
            if (item.Item?.LabelDetails is not null)
            {
                entry.LabelDetails = new CompletionEntryLabelDetailsResponse
                {
                    Detail = item.Item.LabelDetails.Detail,
                    Description = item.Item.LabelDetails.Description,
                };
            }
            if (item.Symbol is not null)
            {
                entry.Symbol = sd.NewSymbolResponse(item.Symbol, @params.Project);
            }
            entries.Add(entry);
        }
        return new CompletionInfoResponse
        {
            IsIncomplete = result.IsIncomplete,
            Entries = [.. entries],
        };
    }

    // handleGetReferencedSymbolsForNode returns node handles for all references found at a
    // node. (session.go:3181-3233)
    private ReferencedSymbolEntry[]? HandleGetReferencedSymbolsForNode(global::Ts.Core.RequestContext ctx, GetReferencedSymbolsForNodeParams @params)
    {
        SnapshotData sd = GetSnapshotData(@params.Snapshot);
        global::Ts.Compiler.Program program = sd.GetProgram(@params.Project);

        global::Ts.Ast.Node? node = sd.ResolveNodeHandle(program, @params.Node);
        if (node is null)
        {
            // Go: resolveNodeHandle err → returned to the client (session.go:3189-3191);
            // the C# port throws inside ResolveNodeHandle, so this is the defensive path.
            throw new ApiRequestException($"{ProtoErrors.ErrClientError}: node handle could not be resolved");
        }

        // Seams (throws at runtime) until the LS host adapter lands; the tail below is the
        // Go body ported verbatim so it activates with the seam.
        global::Ts.Ls.LanguageService? langSvc = SetupLanguageService(sd, program, @params.Project, "");

        global::Ts.Ast.SourceFile[] sourceFiles = program.SourceFiles();
        List<global::Ts.Ls.SymbolAndEntries> entries = langSvc!.GetReferencedSymbolsForNode(ctx.Cancellation, @params.Position, node, sourceFiles);
        if (entries is null)
        {
            return null;
        }

        var result = new List<ReferencedSymbolEntry>();
        foreach (global::Ts.Ls.SymbolAndEntries entry in entries)
        {
            global::Ts.Ast.Node? defNode = entry.DefinitionNode();
            if (defNode is null)
            {
                continue;
            }
            var refs = new List<NodeHandle>();
            foreach (global::Ts.Ls.ReferenceEntry reference in entry.References)
            {
                if (reference.IsNodeEntry())
                {
                    refs.Add(sd.NodeHandleFrom(reference.Node!));
                }
            }
            var re = new ReferencedSymbolEntry
            {
                Definition = sd.NodeHandleFrom(defNode),
                References = [.. refs],
            };
            if (entry.DefinitionSymbol() is global::Ts.Ast.Symbol sym)
            {
                re.Symbol = sd.NewSymbolResponse(sym, @params.Project);
            }
            result.Add(re);
        }
        return [.. result];
    }
}
