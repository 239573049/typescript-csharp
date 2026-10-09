// JSON converters for the api proto handle types and DocumentIdentifier.
//
// PORT-NOTE: Go realizes these via custom UnmarshalJSONFrom (proto.go:223-259) and the
// default struct-tag marshal paths. The converters reproduce the exact wire shapes:
// handle ids are bare JSON numbers, Method/NodeHandle/ProjectID are bare strings, and
// DocumentIdentifier accepts string | { uri } exactly like Go (unknown object fields
// are ignored, other token kinds error with Go's message text).

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ts.Api;

public sealed class MethodJsonConverter : JsonConverter<Method>
{
    public override Method Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new Method(reader.GetString() ?? throw new JsonException("Method must be a string"));
    }

    public override void Write(Utf8JsonWriter writer, Method value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class SnapshotIdJsonConverter : JsonConverter<SnapshotId>
{
    public override SnapshotId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new SnapshotId(reader.GetUInt64());
    }

    public override void Write(Utf8JsonWriter writer, SnapshotId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}

public sealed class ProjectIdJsonConverter : JsonConverter<ProjectId>
{
    public override ProjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new ProjectId(reader.GetString() ?? throw new JsonException("ProjectID must be a string"));
    }

    public override void Write(Utf8JsonWriter writer, ProjectId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class SymbolIdJsonConverter : JsonConverter<SymbolId>
{
    public override SymbolId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new SymbolId(reader.GetUInt64());
    }

    public override void Write(Utf8JsonWriter writer, SymbolId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}

public sealed class TypeIdJsonConverter : JsonConverter<ApiTypeId>
{
    public override ApiTypeId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new ApiTypeId(reader.GetUInt32());
    }

    public override void Write(Utf8JsonWriter writer, ApiTypeId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}

public sealed class SignatureIdJsonConverter : JsonConverter<ApiSignatureId>
{
    public override ApiSignatureId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new ApiSignatureId(reader.GetUInt64());
    }

    public override void Write(Utf8JsonWriter writer, ApiSignatureId value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Value);
    }
}

public sealed class NodeHandleJsonConverter : JsonConverter<NodeHandle>
{
    public override NodeHandle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new NodeHandle(reader.GetString() ?? throw new JsonException("NodeHandle must be a string"));
    }

    public override void Write(Utf8JsonWriter writer, NodeHandle value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

// DocumentIdentifier (proto.go:223-259 — UnmarshalJSONFrom, ported branch-for-branch;
// the Marshal side reproduces the struct tags fileName,omitempty / uri,omitempty).
public sealed class DocumentIdentifierJsonConverter : JsonConverter<DocumentIdentifier>
{
    public override DocumentIdentifier Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var d = new DocumentIdentifier();

        // Try reading as a plain string first
        if (reader.TokenType == JsonTokenType.String)
        {
            d.FileName = reader.GetString() ?? "";
            return d;
        }
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            // Read the object fields
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString() ?? "";
                bool isUri = key == "uri";
                reader.Read();
                if (isUri && reader.TokenType == JsonTokenType.String)
                {
                    d.URI = new global::Ts.LsProto.DocumentUri(reader.GetString()!);
                }
                else if (reader.TokenType == JsonTokenType.Null)
                {
                    // Go reads the value token regardless; only "uri" is recorded.
                }
            }
            // Consume the closing brace (the while exit consumed it via Read()).
            if (reader.TokenType != JsonTokenType.EndObject)
            {
                throw new JsonException("DocumentIdentifier: expected string or object, got invalid");
            }
            return d;
        }

        // Go: fmt.Errorf("DocumentIdentifier: expected string or object, got %v", tok.Kind()).
        throw new JsonException($"DocumentIdentifier: expected string or object, got {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, DocumentIdentifier value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (!string.IsNullOrEmpty(value.FileName))
        {
            writer.WriteString("fileName", value.FileName);
        }
        if (value.URI.Value is not null and not "")
        {
            writer.WriteString("uri", value.URI.Value);
        }
        writer.WriteEndObject();
    }
}
