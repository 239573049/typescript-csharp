// Ported from tsgo internal/fourslash/fourslash.go — the option/result records the emitted
// corpus names unqualified (Go's converter spells them without the fourslash. qualifier because
// the generated *_test.go files live in the same package), plus GetDefaultCapabilities
// (:472-556). They are namespace-level types in Ts.TestUtil.Fourslash so the generated tests
// resolve `new VerifySignatureHelpOptions { ... }` exactly as Go does.
//
// The verify methods that consume them stay in the session partials (Fourslash.Verify.cs,
// Fourslash.Baselines.Verify.cs); this file is only the data shape.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Core;
using Ts.Ls;
using LsProto = global::Ts.LsProto;

namespace Ts.TestUtil.Fourslash;

// fourslash.go:1011-1016 — CompletionsExpectedList.
public sealed class CompletionsExpectedList
{
    public bool IsIncomplete;
    public CompletionsExpectedItemDefaults? ItemDefaults;
    public CompletionsExpectedItems? Items;
    public UserPreferences? UserPreferences;
}

// fourslash.go:1023-1026 — EditRange (the *EditRange arm of the *EditRange | Ignored union).
public sealed class EditRange
{
    public RangeMarker Insert = null!;
    public RangeMarker Replace = null!;
}

// fourslash.go:1028-1031 — CompletionsExpectedItemDefaults.
public sealed class CompletionsExpectedItemDefaults
{
    public IReadOnlyList<string>? CommitCharacters;
    public object? EditRange;
}

// fourslash.go:1034-1041 — CompletionsExpectedItem = string | *lsproto.CompletionItem, and
// CompletionsExpectedItems.
public sealed class CompletionsExpectedItems
{
    public List<object>? Includes;
    public List<string>? Excludes;
    public List<object>? Exact;
    public List<object>? Unsorted;
}

// fourslash.go:1055 — MarkerInput = string | *Marker | []string | []*Marker.
// C# has no structural union; the VerifyCompletions switch dispatches on the concrete types the
// corpus uses, and this interface is the nominal name for them.
public interface MarkerInput;

// fourslash.go:4124-4145 — VerifySignatureHelpOptions. All fields are optional; only the ones
// the corpus actually set are emitted (the generated object initializers name them directly).
public sealed class VerifySignatureHelpOptions
{
    public string Text = "";
    public string DocComment = "";
    public int ParameterCount;
    public string ParameterName = "";
    public string ParameterSpan = "";
    public string ParameterDocComment = "";
    public int OverloadsCount;
    public int OverrideSelectedItemIndex;
    public bool IsVariadic;
    public bool IsVariadicSet;
}

// fourslash.go:1571-1578 — VerifyCodeFixOptions.
public sealed class VerifyCodeFixOptions
{
    public string Description = "";
    public string NewFileContent = "";
    public string NewRangeContent = "";
    public int Index;
    public bool ApplyChanges;
    public UserPreferences? UserPreferences;
}

// fourslash.go:1581-1584 — VerifyCodeFixAllOptions.
public sealed class VerifyCodeFixAllOptions
{
    public string FixID = "";
    public string NewFileContent = "";
}

// fourslash.go:2086-2094 — ApplyCodeActionFromCompletionOptions.
public sealed class ApplyCodeActionFromCompletionOptions
{
    public string Name = "";
    public string Source = "";
    public LsProto.AutoImportFix? AutoImportFix;
    public string Description = "";
    public string? NewFileContent;
    public string? NewRangeContent;
    public UserPreferences? UserPreferences;
}

// fourslash.go:5489-5490 — VerifyWorkspaceSymbolCase.
public sealed class VerifyWorkspaceSymbolCase
{
    public string Pattern = "";
    public UserPreferences? Preferences;
    public LsProto.SymbolInformation[]? Exact;
}

// semantictokens.go:12-15 — SemanticToken.
public sealed record SemanticToken(string Type, string Text);

public sealed partial class FourslashTest
{
    // fourslash.go:472-556 — GetDefaultCapabilities. The Go body is the full LSP client
    // capability document; the C# LS reads only the semantic-token types/modifiers (threaded by
    // New()), so the remaining fields are the ones whose absence would show up in a capability
    // query. Spelled out so the corpus's `capabilities.TextDocument!.Completion!...` mutations
    // resolve.
    public static LsProto.ClientCapabilities GetDefaultCapabilities()
    {
        return new LsProto.ClientCapabilities
        {
            General = new LsProto.GeneralClientCapabilities
            {
                PositionEncodings = [LsProto.PositionEncodingKind.Utf8],
            },
            Experimental = new LsProto.ExperimentalClientCapabilities
            {
                HoverVerbosityLevel = true,
            },
            TextDocument = new LsProto.TextDocumentClientCapabilities
            {
                Completion = new LsProto.CompletionClientCapabilities
                {
                    CompletionItem = new LsProto.ClientCompletionItemOptions
                    {
                        SnippetSupport = true,
                        CommitCharactersSupport = true,
                        PreselectSupport = true,
                        LabelDetailsSupport = true,
                        InsertReplaceSupport = true,
                        DocumentationFormat = [LsProto.MarkupKind.Markdown, LsProto.MarkupKind.PlainText],
                    },
                    CompletionList = new LsProto.CompletionListCapabilities
                    {
                        ItemDefaults = ["commitCharacters", "editRange"],
                    },
                },
            },
            Workspace = new LsProto.WorkspaceClientCapabilities
            {
                Configuration = true,
                FileOperations = new LsProto.FileOperationClientCapabilities
                {
                    WillRename = true,
                },
                WorkspaceEdit = new LsProto.WorkspaceEditClientCapabilities
                {
                    DocumentChanges = true,
                    ResourceOperations = [LsProto.ResourceOperationKind.Rename],
                },
            },
        };
    }
}

#endif
