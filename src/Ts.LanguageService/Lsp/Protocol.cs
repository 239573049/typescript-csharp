// Hand-written residual of the LSP protocol surface.
//
// The full protocol face is generated into Protocol.g.cs / Protocol.Unions.g.cs /
// Protocol.Json.g.cs by tools/generator/generate-lsp.ts (npm run gen:lsp). This file
// retains ONLY the six types that the generator's temporary skip list still holds
// out of the generated surface (they transitively reference Command/CodeAction
// family types that are hand-hosted in the Ts.Ls subsets). When those subsets are
// absorbed and the skip list is emptied, this file goes away entirely.
//
// Everything the generated surface covers — including DocumentUri, Method, and the
// union/converter families that used to live here — is now generated; their
// hand-written copies were retired in the generate-lsp wave.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ts.LsProto;

public sealed class InitializeParams
{
    [JsonPropertyName("workDoneToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IntegerOrString? WorkDoneToken { get; set; }

    [JsonPropertyName("processId")]
    [JsonRequired]
    public int? ProcessId { get; set; }

    [JsonPropertyName("clientInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClientInfo? ClientInfo { get; set; }

    [JsonPropertyName("locale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Locale { get; set; }

    [JsonPropertyName("rootPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RootPath { get; set; }

    [JsonPropertyName("rootUri")]
    [JsonRequired]
    public DocumentUri? RootUri { get; set; }

    [JsonPropertyName("capabilities")]
    [JsonRequired]
    public ClientCapabilities Capabilities { get; set; } = null!;

    [JsonPropertyName("initializationOptions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? InitializationOptions { get; set; }

    [JsonPropertyName("trace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Trace { get; set; }

    [JsonPropertyName("workspaceFolders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceFolder[]? WorkspaceFolders { get; set; }
}

// Unknown client capability members are intentionally ignored by this bootstrap
// subset. The generated type expands this class without changing its wire shape
// once the CodeAction-family subsets are absorbed.
public sealed class ClientCapabilities
{
}

public sealed class InitializeResult
{
    [JsonPropertyName("capabilities")]
    [JsonRequired]
    public ServerCapabilities Capabilities { get; set; } = null!;

    [JsonPropertyName("serverInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServerInfo? ServerInfo { get; set; }
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("positionEncoding")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PositionEncoding { get; set; }

    [JsonPropertyName("textDocumentSync")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextDocumentSyncOptionsOrKind? TextDocumentSync { get; set; }

    [JsonPropertyName("completionProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionOptions? CompletionProvider { get; set; }

    [JsonPropertyName("hoverProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BooleanOrHoverOptions? HoverProvider { get; set; }

    [JsonPropertyName("definitionProvider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BooleanOrDefinitionOptions? DefinitionProvider { get; set; }
}

public sealed class CompletionItem
{
    [JsonPropertyName("label")]
    [JsonRequired]
    public string Label { get; set; } = "";

    [JsonPropertyName("labelDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionItemLabelDetails? LabelDetails { get; set; }

    [JsonPropertyName("kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionItemKind? Kind { get; set; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionItemTag[]? Tags { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonPropertyName("documentation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StringOrMarkupContent? Documentation { get; set; }

    [JsonPropertyName("deprecated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Deprecated { get; set; }

    [JsonPropertyName("preselect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Preselect { get; set; }

    [JsonPropertyName("sortText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SortText { get; set; }

    [JsonPropertyName("filterText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilterText { get; set; }

    [JsonPropertyName("insertText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InsertText { get; set; }

    [JsonPropertyName("insertTextFormat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InsertTextFormat? InsertTextFormat { get; set; }

    [JsonPropertyName("insertTextMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? InsertTextMode { get; set; }

    [JsonPropertyName("textEdit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextEditOrInsertReplaceEdit? TextEdit { get; set; }

    [JsonPropertyName("textEditText")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextEditText { get; set; }

    [JsonPropertyName("additionalTextEdits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TextEdit[]? AdditionalTextEdits { get; set; }

    [JsonPropertyName("commitCharacters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? CommitCharacters { get; set; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Command { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionItemData? Data { get; set; }
}

public sealed class CompletionList
{
    [JsonPropertyName("isIncomplete")]
    [JsonRequired]
    public bool IsIncomplete { get; set; }

    [JsonPropertyName("itemDefaults")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionItemDefaults? ItemDefaults { get; set; }

    [JsonPropertyName("applyKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ApplyKind { get; set; }

    [JsonPropertyName("items")]
    [JsonRequired]
    public CompletionItem[] Items { get; set; } = [];
}

// lsp_generated.go — the Method constants of the two requests whose params/result
// surface is still hand-hosted above (the generator's skip list withholds them from
// the generated Methods class). Retired together with the skip list.
public static class LsHostedMethods
{
    // lsp_generated.go — MethodInitialize.
    public static readonly Method Initialize = new("initialize");
    // lsp_generated.go — MethodTextDocumentCompletion.
    public static readonly Method TextDocumentCompletion = new("textDocument/completion");
}
