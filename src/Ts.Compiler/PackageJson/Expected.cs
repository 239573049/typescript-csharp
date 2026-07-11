// Ported from tsgo internal/packagejson/expected.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ts.PackageJson;

// Expected[T] captures a package.json field where the JSON value may be present
// but of the wrong type ("值在但类型错"): Valid is true only when the value's JSON
// type matched T, while ActualJSONType records what was actually seen.
public struct Expected<T> : TypeValidatedField
{
    // Go: unexported `actualJSONType string`. "" means "not present".
    internal string ActualJSONTypeValue;
    public bool Null;
    public bool Valid;
    public T? Value;

    public readonly bool IsPresent()
    {
        return ActualJSONTypeValue != "" && ActualJSONTypeValue != null;
    }

    public readonly bool GetValue(out T? value)
    {
        value = Value;
        return Valid;
    }

    public readonly bool IsValid()
    {
        return Valid;
    }

    // Go: reflect.TypeFor[T]().Kind() (expected.go:51-67). The AOT-safe translation
    // uses typeof(T) comparisons (07-aot-constraints.md typeof allow-list).
    public readonly string ExpectedJSONType()
    {
        Type t = typeof(T);
        if (t == typeof(string))
        {
            return "string";
        }
        if (t == typeof(bool))
        {
            return "boolean";
        }
        if (t.IsArray)
        {
            return "array";
        }
        if (t == typeof(Dictionary<string, string>) ||
            typeof(System.Collections.IDictionary).IsAssignableFrom(t))
        {
            return "object";
        }
        if (t == typeof(int) || t == typeof(sbyte) || t == typeof(short) || t == typeof(long) ||
            t == typeof(uint) || t == typeof(byte) || t == typeof(ushort) || t == typeof(ulong) ||
            t == typeof(double) || t == typeof(float))
        {
            return "number";
        }
        return "unknown";
    }

    public readonly string ActualJSONType()
    {
        return ActualJSONTypeValue ?? "";
    }
}

// Go: package-level functions of expected.go (05-conventions.md §2.1).
public static class Expected
{
    // Go: ExpectedOf[T](value) (expected.go:73-75).
    public static Expected<T> Of<T>(T value)
    {
        return new Expected<T>
        {
            Value = value,
            Valid = true,
            ActualJSONTypeValue = default(Expected<T>).ExpectedJSONType(),
        };
    }

    // Reads an Expected[string] from a Utf8JsonReader positioned at the value's
    // first token. Go: Expected.UnmarshalJSON (expected.go:16-37).
    internal static Expected<string> ReadString(ref Utf8JsonReader reader)
    {
        // Go's Expected[string] zero value carries Value == "" (not nil), so a present-but-
        // wrong-type field reads "" rather than null (expected.go:16-37).
        var e = new Expected<string> { Value = "" };
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                e.Null = true;
                e.ActualJSONTypeValue = "null";
                break;
            case JsonTokenType.String:
                e.Value = reader.GetString();
                e.Valid = true;
                e.ActualJSONTypeValue = "string";
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                e.ActualJSONTypeValue = "boolean";
                break;
            case JsonTokenType.StartArray:
                e.ActualJSONTypeValue = "array";
                reader.Skip();
                break;
            case JsonTokenType.StartObject:
                e.ActualJSONTypeValue = "object";
                reader.Skip();
                break;
            default:
                // Go: `default: e.actualJSONType = "number"` — anything else is a number.
                e.ActualJSONTypeValue = "number";
                break;
        }
        return e;
    }

    // Reads an Expected[map[string]string] from a Utf8JsonReader positioned at the
    // value's first token. Valid is true only when the value is an object whose
    // members all unmarshal into strings (Go's json.Unmarshal into map[string]string).
    internal static Expected<Dictionary<string, string>> ReadStringMap(ref Utf8JsonReader reader)
    {
        var e = new Expected<Dictionary<string, string>>();
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                e.Null = true;
                e.ActualJSONTypeValue = "null";
                break;
            case JsonTokenType.String:
                e.ActualJSONTypeValue = "string";
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                e.ActualJSONTypeValue = "boolean";
                break;
            case JsonTokenType.StartArray:
                e.ActualJSONTypeValue = "array";
                reader.Skip();
                break;
            case JsonTokenType.StartObject:
            {
                e.ActualJSONTypeValue = "object";
                var dict = new Dictionary<string, string>();
                bool allStrings = true;
                while (true)
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        break;
                    }
                    string key = reader.GetString()!;
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        dict[key] = reader.GetString()!;
                    }
                    else if (reader.TokenType == JsonTokenType.Null)
                    {
                        // Go json unmarshals a null value into a string map slot as "".
                        dict[key] = "";
                    }
                    else
                    {
                        allStrings = false;
                        reader.Skip();
                    }
                }
                e.Value = dict;
                e.Valid = allStrings;
                break;
            }
            default:
                e.ActualJSONTypeValue = "number";
                break;
        }
        return e;
    }
}
