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
    // fourslash.go:346-471 — defaultSemanticTokenTypes/Modifiers live in Fourslash.cs; the rest of
    // the Go package-level default capability vars are spelled inline below.

    // fourslash.go:472-556 — GetDefaultCapabilities. Byte-for-byte the Go capability document
    // (the weaker earlier C# version omitted Hover/Definition/SignatureHelp/Diagnostic/
    // FoldingRange, which is why every hover baseline came out plaintext in the C# port).
    public static LsProto.ClientCapabilities GetDefaultCapabilities()
    {
        return new LsProto.ClientCapabilities
        {
            General = new LsProto.GeneralClientCapabilities
            {
                PositionEncodings = [LsProto.PositionEncodingKind.UTF8],
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
                Diagnostic = new LsProto.DiagnosticClientCapabilities
                {
                    RelatedInformation = true,
                    TagSupport = new LsProto.ClientDiagnosticsTagOptions
                    {
                        ValueSet = [LsProto.DiagnosticTag.Unnecessary, LsProto.DiagnosticTag.Deprecated],
                    },
                },
                PublishDiagnostics = new LsProto.PublishDiagnosticsClientCapabilities
                {
                    RelatedInformation = true,
                    TagSupport = new LsProto.ClientDiagnosticsTagOptions
                    {
                        ValueSet = [LsProto.DiagnosticTag.Unnecessary, LsProto.DiagnosticTag.Deprecated],
                    },
                },
                Definition = new LsProto.DefinitionClientCapabilities
                {
                    LinkSupport = true,
                },
                TypeDefinition = new LsProto.TypeDefinitionClientCapabilities
                {
                    LinkSupport = true,
                },
                Implementation = new LsProto.ImplementationClientCapabilities
                {
                    LinkSupport = true,
                },
                Hover = new LsProto.HoverClientCapabilities
                {
                    ContentFormat = [LsProto.MarkupKind.Markdown, LsProto.MarkupKind.PlainText],
                },
                SignatureHelp = new LsProto.SignatureHelpClientCapabilities
                {
                    SignatureInformation = new LsProto.ClientSignatureInformationOptions
                    {
                        DocumentationFormat = [LsProto.MarkupKind.Markdown, LsProto.MarkupKind.PlainText],
                        ParameterInformation = new LsProto.ClientSignatureParameterInformationOptions
                        {
                            LabelOffsetSupport = true,
                        },
                        ActiveParameterSupport = true,
                    },
                    ContextSupport = true,
                },
                DocumentSymbol = new LsProto.DocumentSymbolClientCapabilities
                {
                    HierarchicalDocumentSymbolSupport = true,
                },
                FoldingRange = new LsProto.FoldingRangeClientCapabilities
                {
                    RangeLimit = 5000,
                    FoldingRangeKind = new LsProto.ClientFoldingRangeKindOptions
                    {
                        ValueSet =
                        [
                            LsProto.FoldingRangeKind.Comment,
                            LsProto.FoldingRangeKind.Imports,
                            LsProto.FoldingRangeKind.Region,
                        ],
                    },
                    FoldingRange = new LsProto.ClientFoldingRangeOptions
                    {
                        CollapsedText = true,
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

    // fourslash.go:557-636 — getCapabilitiesWithDefaults. The C# harness carried only the raw
    // (un-defaulted) capabilities into ResolvedCapabilities(); Go runs every initialize() through
    // this merge first, and the missing pieces are exactly the ones an LS entry reads when a
    // corpus test leaves them unspecified (hover ContentFormat, signature-help documentation
    // format/label offsets, folding-range kinds, diagnostics tags, …). The merge is applied in
    // ResolvedCapabilities(), mirroring Go's ordering.
    public static LsProto.ClientCapabilities WithDefaults(LsProto.ClientCapabilities? capabilities)
    {
        LsProto.ClientCapabilities result = capabilities ?? new LsProto.ClientCapabilities();
        result.General = new LsProto.GeneralClientCapabilities
        {
            PositionEncodings = [LsProto.PositionEncodingKind.UTF8],
        };
        result.Experimental ??= new LsProto.ExperimentalClientCapabilities
        {
            HoverVerbosityLevel = true,
        };
        result.TextDocument ??= new LsProto.TextDocumentClientCapabilities();
        result.TextDocument.Completion ??= new LsProto.CompletionClientCapabilities
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
        };
        result.TextDocument.Diagnostic ??= new LsProto.DiagnosticClientCapabilities
        {
            RelatedInformation = true,
            TagSupport = new LsProto.ClientDiagnosticsTagOptions
            {
                ValueSet = [LsProto.DiagnosticTag.Unnecessary, LsProto.DiagnosticTag.Deprecated],
            },
        };
        result.TextDocument.PublishDiagnostics ??= new LsProto.PublishDiagnosticsClientCapabilities
        {
            RelatedInformation = true,
            TagSupport = new LsProto.ClientDiagnosticsTagOptions
            {
                ValueSet = [LsProto.DiagnosticTag.Unnecessary, LsProto.DiagnosticTag.Deprecated],
            },
        };
        result.TextDocument.SemanticTokens ??= new LsProto.SemanticTokensClientCapabilities
        {
            Requests = new LsProto.ClientSemanticTokensRequestOptions
            {
                Full = new LsProto.BooleanOrClientSemanticTokensRequestFullDelta
                {
                    Boolean = true,
                },
            },
            TokenTypes = DefaultSemanticTokenTypes(),
            TokenModifiers = DefaultSemanticTokenModifiers(),
            Formats = [LsProto.TokenFormat.Relative],
        };
        result.Workspace ??= new LsProto.WorkspaceClientCapabilities();
        result.Workspace.FileOperations ??= new LsProto.FileOperationClientCapabilities
        {
            WillRename = true,
        };
        result.Workspace.WorkspaceEdit ??= new LsProto.WorkspaceEditClientCapabilities
        {
            DocumentChanges = true,
            ResourceOperations = [LsProto.ResourceOperationKind.Rename],
        };
        result.Workspace.Configuration ??= true;
        result.TextDocument.Definition ??= new LsProto.DefinitionClientCapabilities
        {
            LinkSupport = true,
        };
        result.TextDocument.TypeDefinition ??= new LsProto.TypeDefinitionClientCapabilities
        {
            LinkSupport = true,
        };
        result.TextDocument.Implementation ??= new LsProto.ImplementationClientCapabilities
        {
            LinkSupport = true,
        };
        result.TextDocument.Hover ??= new LsProto.HoverClientCapabilities
        {
            ContentFormat = [LsProto.MarkupKind.Markdown, LsProto.MarkupKind.PlainText],
        };
        result.TextDocument.SignatureHelp ??= new LsProto.SignatureHelpClientCapabilities
        {
            SignatureInformation = new LsProto.ClientSignatureInformationOptions
            {
                DocumentationFormat = [LsProto.MarkupKind.Markdown, LsProto.MarkupKind.PlainText],
                ParameterInformation = new LsProto.ClientSignatureParameterInformationOptions
                {
                    LabelOffsetSupport = true,
                },
                ActiveParameterSupport = true,
            },
            ContextSupport = true,
        };
        result.TextDocument.DocumentSymbol ??= new LsProto.DocumentSymbolClientCapabilities
        {
            HierarchicalDocumentSymbolSupport = true,
        };
        result.TextDocument.FoldingRange ??= new LsProto.FoldingRangeClientCapabilities
        {
            RangeLimit = 5000,
            FoldingRangeKind = new LsProto.ClientFoldingRangeKindOptions
            {
                ValueSet =
                [
                    LsProto.FoldingRangeKind.Comment,
                    LsProto.FoldingRangeKind.Imports,
                    LsProto.FoldingRangeKind.Region,
                ],
            },
            FoldingRange = new LsProto.ClientFoldingRangeOptions
            {
                CollapsedText = true,
            },
        };
        return result;
    }
}

#endif
