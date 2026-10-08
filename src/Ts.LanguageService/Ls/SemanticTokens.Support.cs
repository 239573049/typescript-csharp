// Support partial for Ls/SemanticTokens.cs (tsgo internal/ls/semantictokens.go).
//
// Holds the pieces the 1:1 port needs that are not yet provided by the shared
// foundation files (LsDeps.cs / LanguageService.cs / Host.cs / Lsp/Protocol.cs):
// the lsproto wire types for semantic tokens (lsp_generated.go subset) —
// SemanticTokenType / SemanticTokenModifier (string-typed), SemanticTokens,
// SemanticTokensLegend, and ResolvedSemanticTokensClientCapabilities. The foundation
// has no per-request context carrier (lsproto.GetClientCapabilities), so the client
// capabilities are threaded as an explicit parameter (null = the client declared no
// token types/modifiers, matching Go's slices.Contains-over-nil behavior). JSON
// converters/context registration land with the lsp-server wave (ProtocolJsonConverters.cs
// is outside this port's file scope).

using System;

namespace Ts.LsProto;

// lsp_generated.go: SemanticTokenType — a string-typed enumeration on the wire.
[System.Text.Json.Serialization.JsonConverter(typeof(SemanticTokenTypeJsonConverter))]
public readonly record struct SemanticTokenType(string? Value)
{
    public static SemanticTokenType Namespace => new("namespace");
    public static SemanticTokenType Class => new("class");
    public static SemanticTokenType Enum => new("enum");
    public static SemanticTokenType Interface => new("interface");
    public static SemanticTokenType Struct => new("struct");
    public static SemanticTokenType TypeParameter => new("typeParameter");
    public static SemanticTokenType Type => new("type");
    public static SemanticTokenType Parameter => new("parameter");
    public static SemanticTokenType Variable => new("variable");
    public static SemanticTokenType Property => new("property");
    public static SemanticTokenType EnumMember => new("enumMember");
    public static SemanticTokenType Decorator => new("decorator");
    public static SemanticTokenType Event => new("event");
    public static SemanticTokenType Function => new("function");
    public static SemanticTokenType Method => new("method");
    public static SemanticTokenType Macro => new("macro");
    public static SemanticTokenType Label => new("label");
    public static SemanticTokenType Comment => new("comment");
    public static SemanticTokenType String => new("string");
    public static SemanticTokenType Keyword => new("keyword");
    public static SemanticTokenType Number => new("number");
    public static SemanticTokenType Regexp => new("regexp");
    public static SemanticTokenType Operator => new("operator");
}

public sealed class SemanticTokenTypeJsonConverter : System.Text.Json.Serialization.JsonConverter<SemanticTokenType>
{
    public override SemanticTokenType Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
        {
            throw new System.Text.Json.JsonException("SemanticTokenType must be a string");
        }
        return new SemanticTokenType(reader.GetString()); // A String token is never null unless its value is JSON null.
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, SemanticTokenType value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

// lsp_generated.go: SemanticTokenModifier — a string-typed enumeration on the wire.
[System.Text.Json.Serialization.JsonConverter(typeof(SemanticTokenModifierJsonConverter))]
public readonly record struct SemanticTokenModifier(string? Value)
{
    public static SemanticTokenModifier Declaration => new("declaration");
    public static SemanticTokenModifier Definition => new("definition");
    public static SemanticTokenModifier Readonly => new("readonly");
    public static SemanticTokenModifier Static => new("static");
    public static SemanticTokenModifier Deprecated => new("deprecated");
    public static SemanticTokenModifier Abstract => new("abstract");
    public static SemanticTokenModifier Async => new("async");
    public static SemanticTokenModifier Modification => new("modification");
    public static SemanticTokenModifier Documentation => new("documentation");
    public static SemanticTokenModifier DefaultLibrary => new("defaultLibrary");
}

public sealed class SemanticTokenModifierJsonConverter : System.Text.Json.Serialization.JsonConverter<SemanticTokenModifier>
{
    public override SemanticTokenModifier Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
        {
            throw new System.Text.Json.JsonException("SemanticTokenModifier must be a string");
        }
        return new SemanticTokenModifier(reader.GetString()); // A String token is never null unless its value is JSON null.
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, SemanticTokenModifier value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

// lsp_generated.go: ResolvedSemanticTokensClientCapabilities — the resolved tokenTypes/
// tokenModifiers lists the client declared (lsproto GetClientCapabilities().TextDocument.
// SemanticTokens). A null/absent capability set is represented by empty arrays.
public sealed class ResolvedSemanticTokensClientCapabilities
{
    public required string[] TokenTypes { get; init; } = [];
    public required string[] TokenModifiers { get; init; } = [];
}

// lsp_generated.go: SemanticTokens.
public sealed class SemanticTokens
{
    [System.Runtime.Serialization.DataMember(Name = "resultId")]
    [System.Text.Json.Serialization.JsonPropertyName("resultId")]
    public string? ResultId { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "data")]
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public required uint[] Data { get; set; }
}

// lsp_generated.go: SemanticTokensLegend.
public sealed class SemanticTokensLegend
{
    [System.Runtime.Serialization.DataMember(Name = "tokenTypes")]
    [System.Text.Json.Serialization.JsonPropertyName("tokenTypes")]
    public required string[] TokenTypes { get; init; }

    [System.Runtime.Serialization.DataMember(Name = "tokenModifiers")]
    [System.Text.Json.Serialization.JsonPropertyName("tokenModifiers")]
    public required string[] TokenModifiers { get; init; }
}
