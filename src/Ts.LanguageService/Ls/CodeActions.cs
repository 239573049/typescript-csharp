// Ported from tsgo internal/ls/codeactions.go (whole file): the CodeFixProvider
// infrastructure, the ProvideCodeActions dispatch, the fix-all pipeline and the
// organize-imports action shaping.
//
// PORT-NOTE(lsproto subset): the wire types consumed here (CodeActionKind, CodeActionContext,
// CodeActionParams, Command, CodeAction, CommandOrCodeAction, CommandOrCodeActionArrayOrNull)
// have not landed in Lsp/Protocol.cs yet, and this session is scoped to new CodeActions* files
// — so the subset is hosted in the Ts.LsProto block below, ported 1:1 from
// internal/lsp/lsproto/lsp_generated.go (:2809 CodeActionParams, :2868 CodeAction,
// :5934 CodeActionContext, :14140 CommandOrCodeAction, :14173 CommandOrCodeActionArrayOrNull;
// :11324 CodeActionResponse = CommandOrCodeActionArrayOrNull) plus the CodeActionKind constants
// from lsp_generated.go:9895-9959 and lsp.go:310-311. JSON converters land with the
// lsp-server codeAction request wave (the types are not wired into LspJsonContext yet).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Collections; // Set<T>/SetExtensions.Has
using Ts.Diagnostics; // Message.Localize extension

namespace Ts.LsProto
{

// lsp_generated.go:9895 — CodeActionKind (Go string type; hierarchy semantics demand the raw
// string, so a record struct wrapper instead of an enum — matching DocumentUri's shape).
[System.Text.Json.Serialization.JsonConverter(typeof(CodeActionKindJsonConverter))]
public readonly record struct CodeActionKind
{
    public CodeActionKind(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value { get; }

    // lsp_generated.go:9901 — Empty kind.
    public static readonly CodeActionKind Empty = new("");
    // lsp_generated.go:9904 — Base kind for quickfix actions: 'quickfix'.
    public static readonly CodeActionKind QuickFix = new("quickfix");
    // lsp_generated.go:9907 — Base kind for refactoring actions: 'refactor'.
    public static readonly CodeActionKind Refactor = new("refactor");
    // lsp_generated.go:9916 — Base kind for refactoring extraction actions: 'refactor.extract'.
    public static readonly CodeActionKind RefactorExtract = new("refactor.extract");
    // lsp_generated.go:9928 — Base kind for refactoring inline actions: 'refactor.inline'.
    public static readonly CodeActionKind RefactorInline = new("refactor.inline");
    // lsp_generated.go:9940 — Base kind for refactoring move actions: 'refactor.move'.
    public static readonly CodeActionKind RefactorMove = new("refactor.move");
    // lsp_generated.go:9950 — Base kind for refactoring rewrite actions: 'refactor.rewrite'.
    public static readonly CodeActionKind RefactorRewrite = new("refactor.rewrite");
    // lsp_generated.go:9953 — Base kind for source actions: 'source'.
    public static readonly CodeActionKind Source = new("source");
    // lsp_generated.go:9956 — Base kind for an organize imports source action.
    public static readonly CodeActionKind SourceOrganizeImports = new("source.organizeImports");
    // lsp_generated.go:9959 — Base kind for auto-fix source actions: 'source.fixAll'.
    public static readonly CodeActionKind SourceFixAll = new("source.fixAll");
    // lsp.go:310 — source.removeUnusedImports.
    public static readonly CodeActionKind SourceRemoveUnusedImports = new("source.removeUnusedImports");
    // lsp.go:311 — source.sortImports.
    public static readonly CodeActionKind SourceSortImports = new("source.sortImports");
}

public sealed class CodeActionKindJsonConverter : System.Text.Json.Serialization.JsonConverter<CodeActionKind>
{
    public override CodeActionKind Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
        {
            throw new System.Text.Json.JsonException("CodeActionKind must be a string");
        }
        return new CodeActionKind(reader.GetString()!); // A String token is never null unless its value is JSON null.
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, CodeActionKind value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

// lsp_generated.go:10323 — CodeActionTriggerKind.
public enum CodeActionTriggerKind : uint
{
    Invoked = 1,
    Automatic = 2,
}

// lsp_generated.go:5934 — CodeActionContext: contains additional diagnostic information about
// the context in which a action is run.
public sealed class CodeActionContext
{
    [System.Text.Json.Serialization.JsonPropertyName("diagnostics")]
    [System.Text.Json.Serialization.JsonRequired]
    public Diagnostic[] Diagnostics { get; set; } = [];

    // Requested kind of actions to return.
    [System.Text.Json.Serialization.JsonPropertyName("only")]
    public CodeActionKind[]? Only { get; set; }

    // The reason why code actions were requested. Since: 3.17.0.
    [System.Text.Json.Serialization.JsonPropertyName("triggerKind")]
    public CodeActionTriggerKind? TriggerKind { get; set; }
}

// lsp_generated.go:2809 — CodeActionParams: the parameters of a CodeActionRequest.
public sealed class CodeActionParams
{
    // An optional token that a server can use to report work done progress.
    [System.Text.Json.Serialization.JsonPropertyName("workDoneToken")]
    public IntegerOrString? WorkDoneToken { get; set; }

    // An optional token that a server can use to report partial results (e.g. streaming) to
    // the client.
    [System.Text.Json.Serialization.JsonPropertyName("partialResultToken")]
    public IntegerOrString? PartialResultToken { get; set; }

    // The document in which the command was invoked.
    [System.Text.Json.Serialization.JsonPropertyName("textDocument")]
    [System.Text.Json.Serialization.JsonRequired]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    // The range for which the command was invoked.
    [System.Text.Json.Serialization.JsonPropertyName("range")]
    [System.Text.Json.Serialization.JsonRequired]
    public Range Range { get; set; } = null!;

    // Context carrying additional information.
    [System.Text.Json.Serialization.JsonPropertyName("context")]
    [System.Text.Json.Serialization.JsonRequired]
    public CodeActionContext? Context { get; set; }

    // lsp_generated.go:2831 — TextDocumentURI.
    public DocumentUri TextDocumentURI() => TextDocument.Uri;
}

// lsp_generated.go:2837 — Command: represents a reference to a command.
public sealed class Command
{
    // Title of the command, like `save`.
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    [System.Text.Json.Serialization.JsonRequired]
    public string Title { get; set; } = "";

    // An optional tooltip. Since: 3.18.0.
    [System.Text.Json.Serialization.JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }

    // The identifier of the actual command handler.
    [System.Text.Json.Serialization.JsonPropertyName("command")]
    [System.Text.Json.Serialization.JsonRequired]
    public string CommandName { get; set; } = "";

    // Arguments that the command handler should be invoked with.
    [System.Text.Json.Serialization.JsonPropertyName("arguments")]
    public object[]? Arguments { get; set; }
}

// lsp_generated.go:2868 — CodeAction: represents a change that can be performed in code, e.g.
// to fix a problem or to refactor code.
//
// PORT-NOTE(subset): the Disabled/Command/Data/Tags arms (:2905-:2927) ride the
// codeAction/resolve request wave and land with it; every field consumed by the ls
// code-actions pipeline is present.
public sealed class CodeAction
{
    // A short, human-readable, title for this code action.
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    [System.Text.Json.Serialization.JsonRequired]
    public string Title { get; set; } = "";

    // The kind of the code action. Used to filter code actions.
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public CodeActionKind? Kind { get; set; }

    // The diagnostics that this code action resolves.
    [System.Text.Json.Serialization.JsonPropertyName("diagnostics")]
    public Diagnostic[]? Diagnostics { get; set; }

    // Marks this as a preferred action. Since: 3.15.0.
    [System.Text.Json.Serialization.JsonPropertyName("isPreferred")]
    public bool? IsPreferred { get; set; }

    // The workspace edit this code action performs.
    [System.Text.Json.Serialization.JsonPropertyName("edit")]
    public WorkspaceEdit? Edit { get; set; }
}

// lsp_generated.go:14140 — CommandOrCodeAction union.
public sealed class CommandOrCodeAction
{
    public Command? Command { get; set; }
    public CodeAction? CodeAction { get; set; }

    public static CommandOrCodeAction FromCommand(Command value) => new() { Command = value };
    public static CommandOrCodeAction FromCodeAction(CodeAction value) => new() { CodeAction = value };
}

// lsp_generated.go:14173 — CommandOrCodeActionArrayOrNull (:11324 CodeActionResponse alias).
public sealed class CommandOrCodeActionArrayOrNull
{
    public List<CommandOrCodeAction>? CommandOrCodeActionArray { get; set; }

    public static CommandOrCodeActionArrayOrNull FromArray(List<CommandOrCodeAction> value) =>
        new() { CommandOrCodeActionArray = value };
}

} // namespace Ts.LsProto

namespace Ts.Ls
{

// codeactions.go:19-25 — CodeFixProvider represents a provider for a specific type of code fix.
// (Go's `(T, error)` returns throw in the C# port per plan/05 §4.)
public sealed class CodeFixProvider
{
    public int[] ErrorCodes = [];
    public Func<System.Threading.CancellationToken, CodeFixContext, CodeAction[]?>? GetCodeActions;
    public string[] FixIds = [];
    public Func<System.Threading.CancellationToken, CodeFixContext, CombinedCodeActions?>? GetAllCodeActions;
}

// codeactions.go:27-36 — CodeFixContext contains the context needed to generate code fixes.
public sealed class CodeFixContext
{
    public SourceFile SourceFile = null!;
    public TextRange Span;
    public int ErrorCode;
    public global::Ts.Compiler.Program Program = null!;
    public LanguageService LS = null!;
    public global::Ts.LsProto.Diagnostic? Diagnostic;
    public global::Ts.LsProto.CodeActionParams? Params;
}

// codeactions.go:38-44 — CodeAction represents a single code action fix.
public sealed class CodeAction
{
    public string Description = "";
    public global::Ts.LsProto.TextEdit[]? Changes;
    public string FixID = "";
    public string FixAllDescription = "";

    // codeactions.go:46-61 — Compare defines a total ordering for CodeAction values, comparing
    // description then text edits lexicographically. Used with BinarySearchFunc.
    public int Compare(CodeAction b)
    {
        var c = string.CompareOrdinal(Description, b.Description);
        if (c != 0)
        {
            return c;
        }
        c = (Changes?.Length ?? 0).CompareTo(b.Changes?.Length ?? 0);
        if (c != 0)
        {
            return c;
        }
        for (var i = 0; i < (Changes?.Length ?? 0); i++)
        {
            c = CompareTextEdits(Changes![i], b.Changes![i]);
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    // Go: edit.Compare(other) (lsproto.TextEdit total order — see internal/lsp/lsproto
    // TextEdit.Compare). Range start, then range end, then newText — ordinal.
    private static int CompareTextEdits(global::Ts.LsProto.TextEdit a, global::Ts.LsProto.TextEdit b)
    {
        var c = ComparePosition(a.Range.Start, b.Range.Start);
        if (c != 0)
        {
            return c;
        }
        c = ComparePosition(a.Range.End, b.Range.End);
        if (c != 0)
        {
            return c;
        }
        return string.CompareOrdinal(a.NewText, b.NewText);
    }

    private static int ComparePosition(global::Ts.LsProto.Position a, global::Ts.LsProto.Position b)
    {
        var c = a.Line.CompareTo(b.Line);
        if (c != 0)
        {
            return c;
        }
        return a.Character.CompareTo(b.Character);
    }
}

// codeactions.go:63-67 — CombinedCodeActions represents combined code actions for fix-all
// scenarios.
public sealed class CombinedCodeActions
{
    public string Description = "";
    public global::Ts.LsProto.TextEdit[]? Changes;
}

// Package-level helpers of codeactions.go (Go package funcs → static partial class).
public static partial class CodeActions
{
    // codeactions.go:69-75 — codeFixProviders is the list of all registered code fix providers.
    // (Provider singletons are declared in their port files as fields of this static partial
    // class, mirroring the Go package vars.) PORT-NOTE(init-order): a static readonly field
    // initializer would race the singletons' own initializers — C# does not order static field
    // initializers across partial declarations — so the Go package-var read-at-use semantics are
    // preserved with a property.
    public static CodeFixProvider[] codeFixProviders =>
    [
        ImportFixProvider,
        IsolatedDeclarationsFixProvider,
        FixClassIncorrectlyImplementsInterfaceProvider,
        // Add more code fix providers here as they are implemented
    ];

    // slices.BinarySearchFunc over a growing sorted list: leftmost index i where
    // cmp(list[i], target) >= 0, plus whether list[i] == target.
    internal static (int Index, bool Found) BinarySearchFunc<T>(List<T> list, T target, Func<T, T, int> cmp)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (cmp(list[mid], target) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        if (lo < list.Count && cmp(list[lo], target) == 0)
        {
            return (lo, true);
        }
        return (lo, false);
    }

    // codeactions.go:231-238 — codeActionKindContains returns true if the requested kind equals
    // or is a hierarchical parent of actionKind, using '.' as the separator. This matches the
    // semantics of VS Code's HierarchicalKind.contains.
    internal static bool CodeActionKindContains(global::Ts.LsProto.CodeActionKind requestedKind, global::Ts.LsProto.CodeActionKind actionKind)
    {
        return requestedKind == actionKind ||
            requestedKind.Value.Length == 0 ||
            actionKind.Value.StartsWith(requestedKind.Value + ".", StringComparison.Ordinal);
    }

    // codeactions.go:240-243 — isFixAllKind returns true if the requested kind matches
    // source.fixAll.
    internal static bool IsFixAllKind(global::Ts.LsProto.CodeActionKind kind)
    {
        return CodeActionKindContains(kind, global::Ts.LsProto.CodeActionKind.SourceFixAll);
    }

    // codeactions.go:245-257 — wantsQuickFixes returns true if the Only filter is nil/empty
    // (meaning all kinds are wanted) or explicitly includes the quickfix kind.
    internal static bool WantsQuickFixes(global::Ts.LsProto.CodeActionKind[]? only)
    {
        if (only == null || only.Length == 0)
        {
            return true;
        }
        foreach (var kind in only)
        {
            if (CodeActionKindContains(kind, global::Ts.LsProto.CodeActionKind.QuickFix))
            {
                return true;
            }
        }
        return false;
    }

    // codeactions.go:303-314 — getOrganizeImportsActionTitle returns the appropriate title for
    // the given organize imports kind. (locale.FromContext(ctx) collapses to Locale.Default per
    // the LS port's locale convention.)
    internal static string GetOrganizeImportsActionTitle(System.Threading.CancellationToken ctx, global::Ts.LsProto.CodeActionKind kind)
    {
        var loc = global::Ts.Locale.Locale.Default;
        if (kind == global::Ts.LsProto.CodeActionKind.SourceRemoveUnusedImports)
        {
            return global::Ts.Diagnostics.Diagnostics.Remove_Unused_Imports.Localize(loc);
        }
        if (kind == global::Ts.LsProto.CodeActionKind.SourceSortImports)
        {
            return global::Ts.Diagnostics.Diagnostics.Sort_Imports.Localize(loc);
        }
        return global::Ts.Diagnostics.Diagnostics.Organize_Imports.Localize(loc);
    }

    // codeactions.go:316-337 — getOrganizeImportsActionsForKind returns the organize imports
    // code action kinds that should be returned for the given requested kind.
    internal static List<global::Ts.LsProto.CodeActionKind> GetOrganizeImportsActionsForKind(global::Ts.LsProto.CodeActionKind requestedKind)
    {
        var organizeImportsKinds = new[]
        {
            global::Ts.LsProto.CodeActionKind.SourceOrganizeImports,
            global::Ts.LsProto.CodeActionKind.SourceRemoveUnusedImports,
            global::Ts.LsProto.CodeActionKind.SourceSortImports,
        };

        List<global::Ts.LsProto.CodeActionKind>? result = null;
        foreach (var organizeKind in organizeImportsKinds)
        {
            if (CodeActionKindContains(requestedKind, organizeKind))
            {
                result ??= [];
                result.Add(organizeKind);
            }
        }

        if (result != null && result.Contains(requestedKind))
        {
            return [requestedKind];
        }

        return result ?? [];
    }

    // codeactions.go:378-381 — containsErrorCode checks if the error code is in the list.
    internal static bool ContainsErrorCode(int[] codes, int code)
    {
        return Array.IndexOf(codes, code) >= 0;
    }

    // codeactions.go:383-399 — convertToLSPCodeAction converts an internal CodeAction to an LSP
    // CodeAction.
    internal static global::Ts.LsProto.CommandOrCodeAction ConvertToLSPCodeAction(CodeAction action, global::Ts.LsProto.Diagnostic diag, global::Ts.LsProto.DocumentUri uri)
    {
        var kind = global::Ts.LsProto.CodeActionKind.QuickFix;
        var changes = new Dictionary<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.TextEdit[]>
        {
            [uri] = action.Changes ?? [],
        };
        var diagnostics = new[] { diag };

        return global::Ts.LsProto.CommandOrCodeAction.FromCodeAction(new global::Ts.LsProto.CodeAction
        {
            Title = action.Description,
            Kind = kind,
            Edit = new global::Ts.LsProto.WorkspaceEdit { Changes = changes },
            Diagnostics = diagnostics,
        });
    }
}

public sealed partial class LanguageService
{
    // codeactions.go:77-158 — ProvideCodeActions returns code actions for the given range and
    // context.
    public global::Ts.LsProto.CommandOrCodeActionArrayOrNull ProvideCodeActions(global::Ts.LsProto.CodeActionParams parameters)
    {
        var program = this.program;
        var file = GetProgramAndFile(parameters.TextDocument.Uri.Value);

        var actions = new List<global::Ts.LsProto.CommandOrCodeAction>();

        if (parameters.Context != null && parameters.Context.Only != null)
        {
            foreach (var kind in parameters.Context.Only)
            {
                var matchingKinds = CodeActions.GetOrganizeImportsActionsForKind(kind);
                foreach (var matchingKind in matchingKinds)
                {
                    var organizeAction = CreateOrganizeImportsAction(System.Threading.CancellationToken.None, program, file, matchingKind);
                    actions.Add(organizeAction);
                }

                if (CodeActions.IsFixAllKind(kind))
                {
                    var fixAllAction = CreateFixAllAction(System.Threading.CancellationToken.None, program, file, parameters.TextDocument.Uri);
                    if (fixAllAction != null)
                    {
                        actions.Add(fixAllAction);
                    }
                }
            }
        }

        if (parameters.Context != null && parameters.Context.Diagnostics != null && CodeActions.WantsQuickFixes(parameters.Context.Only))
        {
            var fixIdSeen = new Dictionary<string, CodeFixProvider>(StringComparer.Ordinal);

            // sorted for binary search dedup, dedup across all diagnostics and providers so if
            // multiple diags produce the same codefix, only one is returned
            var seen = new List<CodeAction>();

            foreach (var diag in parameters.Context.Diagnostics)
            {
                if (diag.Code == null || diag.Code.Integer == null)
                {
                    continue;
                }

                var errorCode = diag.Code.Integer.Value;

                foreach (var provider in CodeActions.codeFixProviders)
                {
                    if (!CodeActions.ContainsErrorCode(provider.ErrorCodes, errorCode))
                    {
                        continue;
                    }

                    var position = LsDeps.LineAndCharacterToPosition(file, diag.Range.Start.Line, diag.Range.Start.Character);
                    var endPosition = LsDeps.LineAndCharacterToPosition(file, diag.Range.End.Line, diag.Range.End.Character);
                    var fixContext = new CodeFixContext
                    {
                        SourceFile = file,
                        Span = new TextRange(position, endPosition),
                        ErrorCode = errorCode,
                        Program = program,
                        LS = this,
                        Diagnostic = diag,
                        Params = parameters,
                    };

                    var providerActions = provider.GetCodeActions!(System.Threading.CancellationToken.None, fixContext);
                    foreach (var action in providerActions ?? [])
                    {
                        var (i, found) = CodeActions.BinarySearchFunc(seen, action, static (a, b) => a.Compare(b));
                        if (found)
                        {
                            continue;
                        }
                        seen.Insert(i, action);
                        actions.Add(CodeActions.ConvertToLSPCodeAction(action, diag, parameters.TextDocument.Uri));
                        if (action.FixID.Length != 0)
                        {
                            fixIdSeen[action.FixID] = provider;
                        }
                    }
                }
            }

            var fixAllActions = GetFixAllQuickFixes(System.Threading.CancellationToken.None, program, file, parameters.TextDocument.Uri, fixIdSeen);
            actions.AddRange(fixAllActions);
        }

        return new global::Ts.LsProto.CommandOrCodeActionArrayOrNull { CommandOrCodeActionArray = actions };
    }

    // codeactions.go:160-212 — getFixAllQuickFixes returns per-provider "Fix all in file"
    // quickfix entries for providers that matched at least 2 diagnostics in the full file.
    private List<global::Ts.LsProto.CommandOrCodeAction> GetFixAllQuickFixes(
        System.Threading.CancellationToken ctx,
        global::Ts.Compiler.Program program,
        SourceFile file,
        global::Ts.LsProto.DocumentUri uri,
        Dictionary<string, CodeFixProvider> fixIdSeen)
    {
        var actions = new List<global::Ts.LsProto.CommandOrCodeAction>();

        // Deduplicate providers; multiple fixIds may map to the same provider.
        // map-iter: 序无关 (provider set iteration; output actions order follows first-seen,
        // matching Go's random iteration being unobserved by clients — see the Go source's
        // fixIdSeen map)
        var seen = new global::Ts.Collections.Set<CodeFixProvider>(); // providers are singletons; reference identity is the default
        foreach (var provider in fixIdSeen.Values)
        {
            if (seen.Has(provider))
            {
                continue;
            }
            seen.Add(provider);

            if (provider.GetAllCodeActions == null)
            {
                continue;
            }

            if (!HasMultipleFixableDiagnostics(ctx, program, file, provider.ErrorCodes))
            {
                continue;
            }

            var fixContext = new CodeFixContext
            {
                SourceFile = file,
                Program = program,
                LS = this,
            };
            var combined = provider.GetAllCodeActions(ctx, fixContext);
            if (combined != null && (combined.Changes?.Length ?? 0) > 0)
            {
                var kind = global::Ts.LsProto.CodeActionKind.QuickFix;
                var changes = new Dictionary<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.TextEdit[]>
                {
                    [uri] = combined.Changes!,
                };
                actions.Add(global::Ts.LsProto.CommandOrCodeAction.FromCodeAction(new global::Ts.LsProto.CodeAction
                {
                    Title = combined.Description,
                    Kind = kind,
                    Edit = new global::Ts.LsProto.WorkspaceEdit { Changes = changes },
                }));
            }
        }

        return actions;
    }

    // codeactions.go:214-229 — hasMultipleFixableDiagnostics returns true if the file has at
    // least 2 diagnostics matching the given error codes. Checks all diagnostic sources
    // (semantic, syntactic, suggestion, declaration) to match ProvideDiagnostics.
    private static bool HasMultipleFixableDiagnostics(System.Threading.CancellationToken ctx, global::Ts.Compiler.Program program, SourceFile file, int[] errorCodes)
    {
        var allDiags = GetAllDiagnostics(ctx, program, file);
        var count = 0;
        foreach (var d in allDiags)
        {
            if (CodeActions.ContainsErrorCode(errorCodes, d.Code()))
            {
                count++;
                if (count >= 2)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // codeactions.go:259-301 — createFixAllAction creates a source.fixAll code action that
    // applies all auto-fixable code fixes across the file.
    private global::Ts.LsProto.CommandOrCodeAction? CreateFixAllAction(
        System.Threading.CancellationToken ctx,
        global::Ts.Compiler.Program program,
        SourceFile file,
        global::Ts.LsProto.DocumentUri uri)
    {
        var kind = global::Ts.LsProto.CodeActionKind.SourceFixAll;
        var lspChanges = new Dictionary<global::Ts.LsProto.DocumentUri, List<global::Ts.LsProto.TextEdit>>();

        foreach (var provider in CodeActions.codeFixProviders)
        {
            if (provider.GetAllCodeActions == null)
            {
                continue;
            }

            var fixContext = new CodeFixContext
            {
                SourceFile = file,
                Program = program,
                LS = this,
            };

            var combined = provider.GetAllCodeActions(ctx, fixContext);
            if (combined != null && (combined.Changes?.Length ?? 0) > 0)
            {
                if (!lspChanges.TryGetValue(uri, out var list))
                {
                    list = [];
                    lspChanges[uri] = list;
                }
                list.AddRange(combined.Changes!);
            }
        }

        if (lspChanges.Count == 0)
        {
            return null;
        }

        var changesArray = new Dictionary<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.TextEdit[]>(lspChanges.Count);
        foreach (var (key, value) in lspChanges)
        {
            changesArray[key] = value.ToArray();
        }
        return global::Ts.LsProto.CommandOrCodeAction.FromCodeAction(new global::Ts.LsProto.CodeAction
        {
            Title = global::Ts.Diagnostics.Diagnostics.Fix_All.Localize(global::Ts.Locale.Locale.Default),
            Kind = kind,
            Edit = new global::Ts.LsProto.WorkspaceEdit { Changes = changesArray },
        });
    }

    // codeactions.go:339-376 — createOrganizeImportsAction creates the organize imports code
    // action.
    private global::Ts.LsProto.CommandOrCodeAction CreateOrganizeImportsAction(
        System.Threading.CancellationToken ctx,
        global::Ts.Compiler.Program program,
        SourceFile file,
        global::Ts.LsProto.CodeActionKind kind)
    {
        var title = CodeActions.GetOrganizeImportsActionTitle(ctx, kind);
        var changes = OrganizeImports(
            ctx,
            file,
            program,
            kind);
        if (changes.Count == 0)
        {
            return global::Ts.LsProto.CommandOrCodeAction.FromCodeAction(new global::Ts.LsProto.CodeAction
            {
                Title = title,
                Kind = kind,
                Edit = new global::Ts.LsProto.WorkspaceEdit { Changes = [] },
            });
        }

        var lspChanges = new Dictionary<global::Ts.LsProto.DocumentUri, global::Ts.LsProto.TextEdit[]>();
        // map-iter: 序无关 (changes keyed by file name; per-file edit arrays are position-sorted)
        foreach (var (fileName, edits) in changes)
        {
            var fileURI = LsDeps.FileNameToDocumentURI(fileName);
            lspChanges[fileURI] = edits.ToArray();
        }

        return global::Ts.LsProto.CommandOrCodeAction.FromCodeAction(new global::Ts.LsProto.CodeAction
        {
            Title = title,
            Kind = kind,
            Edit = new global::Ts.LsProto.WorkspaceEdit { Changes = lspChanges },
        });
    }
}

} // namespace Ts.Ls
