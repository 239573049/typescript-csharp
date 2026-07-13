using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ts.LsProto;

public sealed class DocumentUriJsonConverter : JsonConverter<DocumentUri>
{
    public override DocumentUri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("DocumentUri must be a string");
        }
        return new DocumentUri(reader.GetString()!); // A String token is never null unless its value is JSON null.
    }

    public override void Write(Utf8JsonWriter writer, DocumentUri value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class IntegerOrStringJsonConverter : JsonConverter<IntegerOrString>
{
    public override IntegerOrString Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var integer))
        {
            return IntegerOrString.FromInteger(integer);
        }
        if (reader.TokenType == JsonTokenType.String)
        {
            return IntegerOrString.FromString(reader.GetString()!); // String token is the asserted Go union arm.
        }
        throw new JsonException("invalid IntegerOrString");
    }

    public override void Write(Utf8JsonWriter writer, IntegerOrString value, JsonSerializerOptions options)
    {
        if (value.String is not null)
        {
            writer.WriteStringValue(value.String);
            return;
        }
        if (value.Integer is not null)
        {
            writer.WriteNumberValue(value.Integer.Value);
            return;
        }
        throw new JsonException("IntegerOrString must contain exactly one value");
    }
}

public sealed class TextDocumentSyncOptionsOrKindJsonConverter : JsonConverter<TextDocumentSyncOptionsOrKind>
{
    public override TextDocumentSyncOptionsOrKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return TextDocumentSyncOptionsOrKind.FromKind((TextDocumentSyncKind)reader.GetUInt32());
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.TextDocumentSyncOptions)!;
            return TextDocumentSyncOptionsOrKind.FromOptions(value);
        }
        throw new JsonException("invalid TextDocumentSyncOptionsOrKind");
    }

    public override void Write(Utf8JsonWriter writer, TextDocumentSyncOptionsOrKind value, JsonSerializerOptions options)
    {
        if (value.Options is not null)
        {
            JsonSerializer.Serialize(writer, value.Options, LspJsonContext.Default.TextDocumentSyncOptions);
            return;
        }
        if (value.Kind is not null)
        {
            writer.WriteNumberValue((uint)value.Kind.Value);
            return;
        }
        throw new JsonException("TextDocumentSyncOptionsOrKind must contain exactly one value");
    }
}

public sealed class BooleanOrSaveOptionsJsonConverter : JsonConverter<BooleanOrSaveOptions>
{
    public override BooleanOrSaveOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return BooleanOrSaveOptions.FromBoolean(reader.GetBoolean());
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.SaveOptions)!;
            return BooleanOrSaveOptions.FromSaveOptions(value);
        }
        throw new JsonException("invalid BooleanOrSaveOptions");
    }

    public override void Write(Utf8JsonWriter writer, BooleanOrSaveOptions value, JsonSerializerOptions options)
    {
        if (value.Boolean is not null)
        {
            writer.WriteBooleanValue(value.Boolean.Value);
            return;
        }
        if (value.SaveOptions is not null)
        {
            JsonSerializer.Serialize(writer, value.SaveOptions, LspJsonContext.Default.SaveOptions);
            return;
        }
        throw new JsonException("BooleanOrSaveOptions must contain exactly one value");
    }
}

public sealed class BooleanOrHoverOptionsJsonConverter : JsonConverter<BooleanOrHoverOptions>
{
    public override BooleanOrHoverOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return BooleanOrHoverOptions.FromBoolean(reader.GetBoolean());
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.HoverOptions)!;
            return BooleanOrHoverOptions.FromHoverOptions(value);
        }
        throw new JsonException("invalid BooleanOrHoverOptions");
    }

    public override void Write(Utf8JsonWriter writer, BooleanOrHoverOptions value, JsonSerializerOptions options)
    {
        if (value.Boolean is not null)
        {
            writer.WriteBooleanValue(value.Boolean.Value);
            return;
        }
        if (value.HoverOptions is not null)
        {
            JsonSerializer.Serialize(writer, value.HoverOptions, LspJsonContext.Default.HoverOptions);
            return;
        }
        throw new JsonException("BooleanOrHoverOptions must contain exactly one value");
    }
}

public sealed class BooleanOrDefinitionOptionsJsonConverter : JsonConverter<BooleanOrDefinitionOptions>
{
    public override BooleanOrDefinitionOptions Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return BooleanOrDefinitionOptions.FromBoolean(reader.GetBoolean());
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.DefinitionOptions)!;
            return BooleanOrDefinitionOptions.FromDefinitionOptions(value);
        }
        throw new JsonException("invalid BooleanOrDefinitionOptions");
    }

    public override void Write(Utf8JsonWriter writer, BooleanOrDefinitionOptions value, JsonSerializerOptions options)
    {
        if (value.Boolean is not null)
        {
            writer.WriteBooleanValue(value.Boolean.Value);
            return;
        }
        if (value.DefinitionOptions is not null)
        {
            JsonSerializer.Serialize(writer, value.DefinitionOptions, LspJsonContext.Default.DefinitionOptions);
            return;
        }
        throw new JsonException("BooleanOrDefinitionOptions must contain exactly one value");
    }
}

public sealed class TextDocumentContentChangeJsonConverter : JsonConverter<TextDocumentContentChangePartialOrWholeDocument>
{
    public override TextDocumentContentChangePartialOrWholeDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("text document content change must be an object");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.TryGetProperty("range", out _))
        {
            var partial = root.Deserialize(LspJsonContext.Default.TextDocumentContentChangePartial)!;
            return TextDocumentContentChangePartialOrWholeDocument.FromPartial(partial);
        }
        var whole = root.Deserialize(LspJsonContext.Default.TextDocumentContentChangeWholeDocument)!;
        return TextDocumentContentChangePartialOrWholeDocument.FromWholeDocument(whole);
    }

    public override void Write(Utf8JsonWriter writer, TextDocumentContentChangePartialOrWholeDocument value, JsonSerializerOptions options)
    {
        if (value.Partial is not null)
        {
            JsonSerializer.Serialize(writer, value.Partial, LspJsonContext.Default.TextDocumentContentChangePartial);
            return;
        }
        if (value.WholeDocument is not null)
        {
            JsonSerializer.Serialize(writer, value.WholeDocument, LspJsonContext.Default.TextDocumentContentChangeWholeDocument);
            return;
        }
        throw new JsonException("text document content change must contain exactly one value");
    }
}

public sealed class StringOrMarkupContentJsonConverter : JsonConverter<StringOrMarkupContent>
{
    public override StringOrMarkupContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return StringOrMarkupContent.FromString(reader.GetString()!);
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.MarkupContent)!;
            return StringOrMarkupContent.FromMarkupContent(value);
        }
        throw new JsonException("invalid StringOrMarkupContent");
    }

    public override void Write(Utf8JsonWriter writer, StringOrMarkupContent value, JsonSerializerOptions options)
    {
        if (value.String is not null)
        {
            writer.WriteStringValue(value.String);
            return;
        }
        if (value.MarkupContent is not null)
        {
            JsonSerializer.Serialize(writer, value.MarkupContent, LspJsonContext.Default.MarkupContent);
            return;
        }
        throw new JsonException("StringOrMarkupContent must contain exactly one value");
    }
}

public sealed class StringOrMarkedStringWithLanguageJsonConverter : JsonConverter<StringOrMarkedStringWithLanguage>
{
    public override StringOrMarkedStringWithLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return StringOrMarkedStringWithLanguage.FromString(reader.GetString()!);
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.MarkedStringWithLanguage)!;
            return StringOrMarkedStringWithLanguage.FromMarkedString(value);
        }
        throw new JsonException("invalid StringOrMarkedStringWithLanguage");
    }

    public override void Write(Utf8JsonWriter writer, StringOrMarkedStringWithLanguage value, JsonSerializerOptions options)
    {
        if (value.String is not null)
        {
            writer.WriteStringValue(value.String);
            return;
        }
        if (value.MarkedStringWithLanguage is not null)
        {
            JsonSerializer.Serialize(writer, value.MarkedStringWithLanguage, LspJsonContext.Default.MarkedStringWithLanguage);
            return;
        }
        throw new JsonException("StringOrMarkedStringWithLanguage must contain exactly one value");
    }
}

public sealed class HoverContentsJsonConverter : JsonConverter<MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings>
{
    public override MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings.FromString(reader.GetString()!);
        }
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.StringOrMarkedStringWithLanguageArray)!;
            return MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings.FromMarkedStrings(values);
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.TryGetProperty("kind", out _))
            {
                return MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings.FromMarkupContent(
                    root.Deserialize(LspJsonContext.Default.MarkupContent)!);
            }
            if (root.TryGetProperty("language", out _))
            {
                return MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings.FromMarkedString(
                    root.Deserialize(LspJsonContext.Default.MarkedStringWithLanguage)!);
            }
        }
        throw new JsonException("invalid hover contents");
    }

    public override void Write(Utf8JsonWriter writer, MarkupContentOrStringOrMarkedStringWithLanguageOrMarkedStrings value, JsonSerializerOptions options)
    {
        if (value.MarkupContent is not null)
        {
            JsonSerializer.Serialize(writer, value.MarkupContent, LspJsonContext.Default.MarkupContent);
            return;
        }
        if (value.String is not null)
        {
            writer.WriteStringValue(value.String);
            return;
        }
        if (value.MarkedStringWithLanguage is not null)
        {
            JsonSerializer.Serialize(writer, value.MarkedStringWithLanguage, LspJsonContext.Default.MarkedStringWithLanguage);
            return;
        }
        if (value.MarkedStrings is not null)
        {
            JsonSerializer.Serialize(writer, value.MarkedStrings, LspJsonContext.Default.StringOrMarkedStringWithLanguageArray);
            return;
        }
        throw new JsonException("hover contents must contain exactly one value");
    }
}

public sealed class DefinitionJsonConverter : JsonConverter<Definition>
{
    public override Definition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var values = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.LocationArray)!;
            return Definition.FromLocations(values);
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var value = JsonSerializer.Deserialize(ref reader, LspJsonContext.Default.Location)!;
            return Definition.FromLocation(value);
        }
        throw new JsonException("invalid definition response");
    }

    public override void Write(Utf8JsonWriter writer, Definition value, JsonSerializerOptions options)
    {
        if (value.Location is not null)
        {
            JsonSerializer.Serialize(writer, value.Location, LspJsonContext.Default.Location);
            return;
        }
        if (value.Locations is not null)
        {
            JsonSerializer.Serialize(writer, value.Locations, LspJsonContext.Default.LocationArray);
            return;
        }
        throw new JsonException("definition response must contain exactly one value");
    }
}
