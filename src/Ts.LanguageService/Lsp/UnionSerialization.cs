// Ported from tsgo internal/lsp/lsproto/lsp.go:123-184 — the low-level raw JSON
// scan helpers that the generated union converters (Protocol.Json.g.cs) dispatch
// on. Go scans raw object bytes with a json.Decoder; here the converters have
// already materialized a JsonElement, so the scans walk its top-level properties
// in document order (identical semantics, including early bail on first match).
//
// The Go error-string helpers in the same block (errNotObject/errNull/errMissing/
// errInvalidKind/errInvalidValue/errLiteralMismatch) dissolve into the generated
// converters' inline JsonException throws; required-property rejection rides the
// [JsonRequired] attribute instead of errMissing.

using System.Text.Json;

namespace Ts.LsProto;

public static class UnionSerialization
{
    // lsp.go:123 — jsonKeyCheck: compares a raw JSON key token against a C# string.
    // JsonElement property names arrive already unquoted/unescaped, so the quote
    // handling drops out.
    private static bool JsonKeyCheck(string name, string key) => name == key;

    // lsp.go:128 — jsonObjectRawField: scans the top-level keys of a JSON object
    // looking for the given field name, and returns its raw JSON value (e.g.
    // `"full"` with quotes). Returns null if the field is not found (or the root
    // is not an object — Go's PeekKind('{') guard).
    public static string? JsonObjectRawField(JsonElement root, string field)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in root.EnumerateObject())
        {
            if (JsonKeyCheck(property.Name, field))
            {
                return property.Value.GetRawText();
            }
        }
        return null;
    }

    // lsp.go:158 — jsonObjectHasKey: scans the top-level keys of a JSON object
    // looking for any of the given keys. Returns the index of the first key found
    // (in keys order for a single property, in document order across properties),
    // or -1 if none match. Bails early on first match without decoding any values.
    public static int JsonObjectHasKey(JsonElement root, params string[] keys)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return -1;
        }
        foreach (var property in root.EnumerateObject())
        {
            for (var i = 0; i < keys.Length; i++)
            {
                if (JsonKeyCheck(property.Name, keys[i]))
                {
                    return i;
                }
            }
        }
        return -1;
    }
}
