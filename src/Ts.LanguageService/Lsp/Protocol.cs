// Layout-compatible hand-ported subset of tsgo internal/lsp/lsproto/lsp_generated.go.
//
// The complete protocol surface will be emitted by the later generate-lsp codegen
// wave. Keep type names, member names, JSON property names, required/nullability
// behavior, and union wire shapes compatible with that generated replacement.

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ts.LsProto;

public static class Methods
{
    public const string Initialize = "initialize";
    public const string Initialized = "initialized";
    public const string Shutdown = "shutdown";
    public const string Exit = "exit";
    public const string TextDocumentDidOpen = "textDocument/didOpen";
    public const string TextDocumentDidChange = "textDocument/didChange";
    public const string TextDocumentDidClose = "textDocument/didClose";
    public const string TextDocumentHover = "textDocument/hover";
    public const string TextDocumentDefinition = "textDocument/definition";
    public const string TextDocumentCompletion = "textDocument/completion";
    public const string TextDocumentPublishDiagnostics = "textDocument/publishDiagnostics";
}

[JsonConverter(typeof(DocumentUriJsonConverter))]
public readonly record struct DocumentUri
{
    public DocumentUri(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Value { get; }

    public override string ToString() => Value ?? "";
}

public sealed class InitializeParams
{
    [JsonPropertyName("workDoneToken")]
    public IntegerOrString? WorkDoneToken { get; set; }

    [JsonPropertyName("processId")]
    [JsonRequired]
    public int? ProcessId { get; set; }

    [JsonPropertyName("clientInfo")]
    public ClientInfo? ClientInfo { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }

    [JsonPropertyName("rootPath")]
    public string? RootPath { get; set; }

    [JsonPropertyName("rootUri")]
    [JsonRequired]
    public DocumentUri? RootUri { get; set; }

    [JsonPropertyName("capabilities")]
    [JsonRequired]
    public ClientCapabilities Capabilities { get; set; } = null!;

    [JsonPropertyName("initializationOptions")]
    public JsonElement? InitializationOptions { get; set; }

    [JsonPropertyName("trace")]
    public string? Trace { get; set; }

    [JsonPropertyName("workspaceFolders")]
    public WorkspaceFolder[]? WorkspaceFolders { get; set; }
}

public sealed class ClientInfo
{
    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

// Unknown client capability members are intentionally ignored by this bootstrap
// subset. The future generated type expands this class without changing its wire shape.
public sealed class ClientCapabilities
{
}

public sealed class WorkspaceFolder
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public string Uri { get; set; } = "";

    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = "";
}

public sealed class InitializeResult
{
    [JsonPropertyName("capabilities")]
    [JsonRequired]
    public ServerCapabilities Capabilities { get; set; } = null!;

    [JsonPropertyName("serverInfo")]
    public ServerInfo? ServerInfo { get; set; }
}

public sealed class ServerInfo
{
    [JsonPropertyName("name")]
    [JsonRequired]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class ServerCapabilities
{
    [JsonPropertyName("positionEncoding")]
    public string? PositionEncoding { get; set; }

    [JsonPropertyName("textDocumentSync")]
    public TextDocumentSyncOptionsOrKind? TextDocumentSync { get; set; }

    [JsonPropertyName("completionProvider")]
    public CompletionOptions? CompletionProvider { get; set; }

    [JsonPropertyName("hoverProvider")]
    public BooleanOrHoverOptions? HoverProvider { get; set; }

    [JsonPropertyName("definitionProvider")]
    public BooleanOrDefinitionOptions? DefinitionProvider { get; set; }
}

public enum TextDocumentSyncKind : uint
{
    None = 0,
    Full = 1,
    Incremental = 2,
}

public sealed class TextDocumentSyncOptions
{
    [JsonPropertyName("openClose")]
    public bool? OpenClose { get; set; }

    [JsonPropertyName("change")]
    public TextDocumentSyncKind? Change { get; set; }

    [JsonPropertyName("willSave")]
    public bool? WillSave { get; set; }

    [JsonPropertyName("willSaveWaitUntil")]
    public bool? WillSaveWaitUntil { get; set; }

    [JsonPropertyName("save")]
    public BooleanOrSaveOptions? Save { get; set; }
}

public sealed class SaveOptions
{
    [JsonPropertyName("includeText")]
    public bool? IncludeText { get; set; }
}

public sealed class CompletionOptions
{
    [JsonPropertyName("workDoneProgress")]
    public bool? WorkDoneProgress { get; set; }

    [JsonPropertyName("triggerCharacters")]
    public string[]? TriggerCharacters { get; set; }

    [JsonPropertyName("allCommitCharacters")]
    public string[]? AllCommitCharacters { get; set; }

    [JsonPropertyName("resolveProvider")]
    public bool? ResolveProvider { get; set; }
}

public sealed class HoverOptions
{
    [JsonPropertyName("workDoneProgress")]
    public bool? WorkDoneProgress { get; set; }
}

public sealed class DefinitionOptions
{
    [JsonPropertyName("workDoneProgress")]
    public bool? WorkDoneProgress { get; set; }
}

public sealed class TextDocumentItem
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public DocumentUri Uri { get; set; }

    [JsonPropertyName("languageId")]
    [JsonRequired]
    public string LanguageId { get; set; } = "";

    [JsonPropertyName("version")]
    [JsonRequired]
    public int Version { get; set; }

    [JsonPropertyName("text")]
    [JsonRequired]
    public string Text { get; set; } = "";
}

public sealed class DidOpenTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public TextDocumentItem TextDocument { get; set; } = null!;
}

public sealed class TextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public DocumentUri Uri { get; set; }
}

public sealed class VersionedTextDocumentIdentifier
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public DocumentUri Uri { get; set; }

    [JsonPropertyName("version")]
    [JsonRequired]
    public int Version { get; set; }
}

public sealed class DidChangeTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public VersionedTextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonPropertyName("contentChanges")]
    [JsonRequired]
    public TextDocumentContentChangePartialOrWholeDocument[] ContentChanges { get; set; } = [];
}

public sealed class DidCloseTextDocumentParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;
}

public sealed class TextDocumentContentChangePartial
{
    [JsonPropertyName("range")]
    [JsonRequired]
    public Range Range { get; set; } = null!;

    [JsonPropertyName("rangeLength")]
    public uint? RangeLength { get; set; }

    [JsonPropertyName("text")]
    [JsonRequired]
    public string Text { get; set; } = "";
}

public sealed class TextDocumentContentChangeWholeDocument
{
    [JsonPropertyName("text")]
    [JsonRequired]
    public string Text { get; set; } = "";
}

public sealed class Position
{
    [JsonPropertyName("line")]
    [JsonRequired]
    public uint Line { get; set; }

    [JsonPropertyName("character")]
    [JsonRequired]
    public uint Character { get; set; }
}

public sealed class Range
{
    [JsonPropertyName("start")]
    [JsonRequired]
    public Position Start { get; set; } = null!;

    [JsonPropertyName("end")]
    [JsonRequired]
    public Position End { get; set; } = null!;
}

public sealed class Location
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public DocumentUri Uri { get; set; }

    [JsonPropertyName("range")]
    [JsonRequired]
    public Range Range { get; set; } = null!;
}

public sealed class TextDocumentPositionParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonPropertyName("position")]
    [JsonRequired]
    public Position Position { get; set; } = null!;
}

public sealed class HoverParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonPropertyName("position")]
    [JsonRequired]
    public Position Position { get; set; } = null!;

    [JsonPropertyName("workDoneToken")]
    public IntegerOrString? WorkDoneToken { get; set; }

    [JsonPropertyName("verbosityLevel")]
    public int? VerbosityLevel { get; set; }
}

public sealed class MarkupContent
{
    [JsonPropertyName("kind")]
    [JsonRequired]
    public string Kind { get; set; } = "";

    [JsonPropertyName("value")]
    [JsonRequired]
    public string Value { get; set; } = "";
}

public sealed class MarkedStringWithLanguage
{
    [JsonPropertyName("language")]
    [JsonRequired]
    public string Language { get; set; } = "";

    [JsonPropertyName("value")]
    [JsonRequired]
    public string Value { get; set; } = "";
}

public sealed class Hover
{
    [JsonPropertyName("contents")]
    [JsonRequired]
    public MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings Contents { get; set; } = null!;

    [JsonPropertyName("range")]
    public Range? Range { get; set; }

    [JsonPropertyName("canIncreaseVerbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CanIncreaseVerbosity { get; set; }
}

public sealed class DefinitionParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonPropertyName("position")]
    [JsonRequired]
    public Position Position { get; set; } = null!;

    [JsonPropertyName("workDoneToken")]
    public IntegerOrString? WorkDoneToken { get; set; }

    [JsonPropertyName("partialResultToken")]
    public IntegerOrString? PartialResultToken { get; set; }
}

public sealed class CompletionParams
{
    [JsonPropertyName("textDocument")]
    [JsonRequired]
    public TextDocumentIdentifier TextDocument { get; set; } = null!;

    [JsonPropertyName("position")]
    [JsonRequired]
    public Position Position { get; set; } = null!;

    [JsonPropertyName("workDoneToken")]
    public IntegerOrString? WorkDoneToken { get; set; }

    [JsonPropertyName("partialResultToken")]
    public IntegerOrString? PartialResultToken { get; set; }

    [JsonPropertyName("context")]
    public CompletionContext? Context { get; set; }
}

public enum CompletionTriggerKind : uint
{
    Invoked = 1,
    TriggerCharacter = 2,
    TriggerForIncompleteCompletions = 3,
}

public sealed class CompletionContext
{
    [JsonPropertyName("triggerKind")]
    [JsonRequired]
    public CompletionTriggerKind TriggerKind { get; set; }

    [JsonPropertyName("triggerCharacter")]
    public string? TriggerCharacter { get; set; }
}

public enum CompletionItemKind : uint
{
    Text = 1,
    Method = 2,
    Function = 3,
    Constructor = 4,
    Field = 5,
    Variable = 6,
    Class = 7,
    Interface = 8,
    Module = 9,
    Property = 10,
    Unit = 11,
    Value = 12,
    Enum = 13,
    Keyword = 14,
    Snippet = 15,
    Color = 16,
    File = 17,
    Reference = 18,
    Folder = 19,
    EnumMember = 20,
    Constant = 21,
    Struct = 22,
    Event = 23,
    Operator = 24,
    TypeParameter = 25,
}

public sealed class CompletionItem
{
    [JsonPropertyName("label")]
    [JsonRequired]
    public string Label { get; set; } = "";

    [JsonPropertyName("labelDetails")]
    public JsonElement? LabelDetails { get; set; }

    [JsonPropertyName("kind")]
    public CompletionItemKind? Kind { get; set; }

    [JsonPropertyName("tags")]
    public JsonElement? Tags { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("documentation")]
    public StringOrMarkupContent? Documentation { get; set; }

    [JsonPropertyName("deprecated")]
    public bool? Deprecated { get; set; }

    [JsonPropertyName("preselect")]
    public bool? Preselect { get; set; }

    [JsonPropertyName("sortText")]
    public string? SortText { get; set; }

    [JsonPropertyName("filterText")]
    public string? FilterText { get; set; }

    [JsonPropertyName("insertText")]
    public string? InsertText { get; set; }

    [JsonPropertyName("insertTextFormat")]
    public uint? InsertTextFormat { get; set; }

    [JsonPropertyName("insertTextMode")]
    public uint? InsertTextMode { get; set; }

    [JsonPropertyName("textEdit")]
    public JsonElement? TextEdit { get; set; }

    [JsonPropertyName("textEditText")]
    public string? TextEditText { get; set; }

    [JsonPropertyName("additionalTextEdits")]
    public JsonElement? AdditionalTextEdits { get; set; }

    [JsonPropertyName("commitCharacters")]
    public string[]? CommitCharacters { get; set; }

    [JsonPropertyName("command")]
    public JsonElement? Command { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

public sealed class CompletionList
{
    [JsonPropertyName("isIncomplete")]
    [JsonRequired]
    public bool IsIncomplete { get; set; }

    [JsonPropertyName("itemDefaults")]
    public JsonElement? ItemDefaults { get; set; }

    [JsonPropertyName("applyKind")]
    public JsonElement? ApplyKind { get; set; }

    [JsonPropertyName("items")]
    [JsonRequired]
    public CompletionItem[] Items { get; set; } = [];
}

public enum DiagnosticSeverity : uint
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed class Diagnostic
{
    [JsonPropertyName("range")]
    [JsonRequired]
    public Range Range { get; set; } = null!;

    [JsonPropertyName("severity")]
    public DiagnosticSeverity? Severity { get; set; }

    [JsonPropertyName("code")]
    public IntegerOrString? Code { get; set; }

    [JsonPropertyName("codeDescription")]
    public JsonElement? CodeDescription { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("message")]
    [JsonRequired]
    public StringOrMarkupContent Message { get; set; } = null!;

    [JsonPropertyName("tags")]
    public JsonElement? Tags { get; set; }

    [JsonPropertyName("relatedInformation")]
    public JsonElement? RelatedInformation { get; set; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

public sealed class PublishDiagnosticsParams
{
    [JsonPropertyName("uri")]
    [JsonRequired]
    public DocumentUri Uri { get; set; }

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("diagnostics")]
    [JsonRequired]
    public Diagnostic[] Diagnostics { get; set; } = [];
}

// The union classes below deliberately use the same variant member names as
// lsp_generated.go. Their converters enforce exactly one active variant.

[JsonConverter(typeof(IntegerOrStringJsonConverter))]
public sealed class IntegerOrString
{
    public int? Integer { get; private set; }
    public string? String { get; private set; }

    public static IntegerOrString FromInteger(int value) => new() { Integer = value };
    public static IntegerOrString FromString(string value) => new() { String = value };
}

[JsonConverter(typeof(TextDocumentSyncOptionsOrKindJsonConverter))]
public sealed class TextDocumentSyncOptionsOrKind
{
    public TextDocumentSyncOptions? Options { get; private set; }
    public TextDocumentSyncKind? Kind { get; private set; }

    public static TextDocumentSyncOptionsOrKind FromOptions(TextDocumentSyncOptions value) => new() { Options = value };
    public static TextDocumentSyncOptionsOrKind FromKind(TextDocumentSyncKind value) => new() { Kind = value };
}

[JsonConverter(typeof(BooleanOrSaveOptionsJsonConverter))]
public sealed class BooleanOrSaveOptions
{
    public bool? Boolean { get; private set; }
    public SaveOptions? SaveOptions { get; private set; }

    public static BooleanOrSaveOptions FromBoolean(bool value) => new() { Boolean = value };
    public static BooleanOrSaveOptions FromSaveOptions(SaveOptions value) => new() { SaveOptions = value };
}

[JsonConverter(typeof(BooleanOrHoverOptionsJsonConverter))]
public sealed class BooleanOrHoverOptions
{
    public bool? Boolean { get; private set; }
    public HoverOptions? HoverOptions { get; private set; }

    public static BooleanOrHoverOptions FromBoolean(bool value) => new() { Boolean = value };
    public static BooleanOrHoverOptions FromHoverOptions(HoverOptions value) => new() { HoverOptions = value };
}

[JsonConverter(typeof(BooleanOrDefinitionOptionsJsonConverter))]
public sealed class BooleanOrDefinitionOptions
{
    public bool? Boolean { get; private set; }
    public DefinitionOptions? DefinitionOptions { get; private set; }

    public static BooleanOrDefinitionOptions FromBoolean(bool value) => new() { Boolean = value };
    public static BooleanOrDefinitionOptions FromDefinitionOptions(DefinitionOptions value) => new() { DefinitionOptions = value };
}

[JsonConverter(typeof(TextDocumentContentChangeJsonConverter))]
public sealed class TextDocumentContentChangePartialOrWholeDocument
{
    public TextDocumentContentChangePartial? Partial { get; private set; }
    public TextDocumentContentChangeWholeDocument? WholeDocument { get; private set; }

    public static TextDocumentContentChangePartialOrWholeDocument FromPartial(TextDocumentContentChangePartial value) => new() { Partial = value };
    public static TextDocumentContentChangePartialOrWholeDocument FromWholeDocument(TextDocumentContentChangeWholeDocument value) => new() { WholeDocument = value };
}

[JsonConverter(typeof(StringOrMarkupContentJsonConverter))]
public sealed class StringOrMarkupContent
{
    public string? String { get; private set; }
    public MarkupContent? MarkupContent { get; private set; }

    public static StringOrMarkupContent FromString(string value) => new() { String = value };
    public static StringOrMarkupContent FromMarkupContent(MarkupContent value) => new() { MarkupContent = value };
}

[JsonConverter(typeof(HoverContentsJsonConverter))]
public sealed class MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings
{
    public MarkupContent? MarkupContent { get; private set; }
    public string? String { get; private set; }
    public MarkedStringWithLanguage? MarkedStringWithLanguage { get; private set; }
    public StringOrMarkedStringWithLanguage[]? MarkedStrings { get; private set; }

    public static MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings FromMarkupContent(MarkupContent value) => new() { MarkupContent = value };
    public static MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings FromString(string value) => new() { String = value };
    public static MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings FromMarkedString(MarkedStringWithLanguage value) => new() { MarkedStringWithLanguage = value };
    public static MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings FromMarkedStrings(StringOrMarkedStringWithLanguage[] value) => new() { MarkedStrings = value };
}

[JsonConverter(typeof(StringOrMarkedStringWithLanguageJsonConverter))]
public sealed class StringOrMarkedStringWithLanguage
{
    public string? String { get; private set; }
    public MarkedStringWithLanguage? MarkedStringWithLanguage { get; private set; }

    public static StringOrMarkedStringWithLanguage FromString(string value) => new() { String = value };
    public static StringOrMarkedStringWithLanguage FromMarkedString(MarkedStringWithLanguage value) => new() { MarkedStringWithLanguage = value };
}

[JsonConverter(typeof(DefinitionJsonConverter))]
public sealed class Definition
{
    public Location? Location { get; private set; }
    public Location[]? Locations { get; private set; }

    public static Definition FromLocation(Location value) => new() { Location = value };
    public static Definition FromLocations(Location[] value) => new() { Locations = value };
}
