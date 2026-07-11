// Ported from tsgo internal/packagejson/jsonvalue.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Ts.Collections;

namespace Ts.PackageJson;

public enum JSONValueType : sbyte
{
    // JSONValueTypeNotPresent = iota
    JSONValueTypeNotPresent = 0,
    JSONValueTypeNull,
    JSONValueTypeString,
    JSONValueTypeNumber,
    JSONValueTypeBoolean,
    JSONValueTypeArray,
    JSONValueTypeObject,
}

// Reads a single JSON value from a Utf8JsonReader. On entry the reader is
// positioned at the value's first token; on exit at the value's last token.
// PORT-NOTE: Utf8JsonReader is a ref struct, so nested-value recursion uses this
// explicit delegate shape rather than a captured closure. Replaces the Go
// json.UnmarshalerFrom streaming decoder (jsonvalue.go:88-90,125-175).
internal delegate T RefReader<T>(ref Utf8JsonReader reader);

public struct JSONValue
{
    public JSONValueType Type;
    public object? Value;

    public readonly bool IsPresent()
    {
        return Type != JSONValueType.JSONValueTypeNotPresent;
    }

    public readonly bool IsFalsy()
    {
        switch (Type)
        {
            case JSONValueType.JSONValueTypeNotPresent:
            case JSONValueType.JSONValueTypeNull:
                return true;
            case JSONValueType.JSONValueTypeString:
                return (string?)Value == "";
            case JSONValueType.JSONValueTypeNumber:
                // PORT-NOTE: Go's `v.Value == 0` (jsonvalue.go:57) compares an `any`
                // holding float64 to the untyped constant 0, which Go converts to
                // int — the dynamic types differ, so the comparison is always false.
                // A JSON number is therefore never falsy here; ported exactly.
                return false;
            case JSONValueType.JSONValueTypeBoolean:
                return !(bool)Value!;
            default:
                return false;
        }
    }

    public readonly OrderedMap<string, JSONValue> AsObject()
    {
        if (Type != JSONValueType.JSONValueTypeObject)
        {
            throw new InvalidOperationException($"expected object, got {ToDisplayString(Type)}");
        }
        return (OrderedMap<string, JSONValue>)Value!;
    }

    public readonly List<JSONValue> AsArray()
    {
        if (Type != JSONValueType.JSONValueTypeArray)
        {
            throw new InvalidOperationException($"expected array, got {ToDisplayString(Type)}");
        }
        return (List<JSONValue>)Value!;
    }

    public readonly string AsString()
    {
        if (Type != JSONValueType.JSONValueTypeString)
        {
            throw new InvalidOperationException($"expected string, got {ToDisplayString(Type)}");
        }
        return (string)Value!;
    }

    // Go: func (t JSONValueType) String() string (jsonvalue.go:22-39).
    public static string ToDisplayString(JSONValueType t)
    {
        return t switch
        {
            JSONValueType.JSONValueTypeNull => "null",
            JSONValueType.JSONValueTypeString => "string",
            JSONValueType.JSONValueTypeNumber => "number",
            JSONValueType.JSONValueTypeBoolean => "boolean",
            JSONValueType.JSONValueTypeArray => "array",
            JSONValueType.JSONValueTypeObject => "object",
            _ => string.Create(CultureInfo.InvariantCulture, $"unknown({(int)t})"),
        };
    }

    // Go: func (v *JSONValue) UnmarshalJSONFrom(dec *json.Decoder) — reads a JSONValue
    // whose nested arrays/objects contain JSONValue children.
    internal static JSONValue Read(ref Utf8JsonReader reader)
    {
        var (type, value) = ReadCore<JSONValue>(ref reader, Read);
        return new JSONValue { Type = type, Value = value };
    }

    // Go: unmarshalJSONValueV2[T] (jsonvalue.go:125-175). TChild is the type used for
    // nested array elements and object values.
    internal static (JSONValueType Type, object? Value) ReadCore<TChild>(ref Utf8JsonReader reader, RefReader<TChild> readChild)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return (JSONValueType.JSONValueTypeNull, null);
            case JsonTokenType.String:
                return (JSONValueType.JSONValueTypeString, reader.GetString()!);
            case JsonTokenType.StartArray:
            {
                var elements = new List<TChild>();
                while (true)
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }
                    elements.Add(readChild(ref reader));
                }
                return (JSONValueType.JSONValueTypeArray, elements);
            }
            case JsonTokenType.StartObject:
            {
                var obj = new OrderedMap<string, TChild>();
                while (true)
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }
                    // reader.TokenType == PropertyName
                    string key = reader.GetString()!;
                    reader.Read();
                    obj.Set(key, readChild(ref reader));
                }
                return (JSONValueType.JSONValueTypeObject, obj);
            }
            case JsonTokenType.True:
                return (JSONValueType.JSONValueTypeBoolean, true);
            case JsonTokenType.False:
                return (JSONValueType.JSONValueTypeBoolean, false);
            case JsonTokenType.Number:
                // Go json decodes a bare number into `any` as float64 (jsonvalue.go:169-171).
                return (JSONValueType.JSONValueTypeNumber, reader.GetDouble());
            default:
                throw new JsonException($"unexpected token {reader.TokenType}");
        }
    }
}
