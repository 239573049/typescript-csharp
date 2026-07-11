// Ported from tsgo internal/packagejson/exportsorimports.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Text.Json;
using Ts.Collections;

namespace Ts.PackageJson;

internal enum ObjectKind : sbyte
{
    ObjectKindUnknown = 0,
    ObjectKindSubpaths,
    ObjectKindConditions,
    ObjectKindImports,
    ObjectKindInvalid,
}

// Go embeds JSONValue (exportsorimports.go:18-21). C# holds it as a field and
// forwards the reads the algorithm needs.
public struct ExportsOrImports
{
    public JSONValue Json;

    public readonly JSONValueType Type => Json.Type;
    public readonly object? Value => Json.Value;

    public readonly bool IsPresent() => Json.IsPresent();

    public readonly bool IsFalsy() => Json.IsFalsy();

    public readonly OrderedMap<string, ExportsOrImports> AsObject()
    {
        if (Json.Type != JSONValueType.JSONValueTypeObject)
        {
            throw new InvalidOperationException("expected object");
        }
        return (OrderedMap<string, ExportsOrImports>)Json.Value!;
    }

    public readonly List<ExportsOrImports> AsArray()
    {
        if (Json.Type != JSONValueType.JSONValueTypeArray)
        {
            throw new InvalidOperationException("expected array");
        }
        return (List<ExportsOrImports>)Json.Value!;
    }

    public readonly string AsString() => Json.AsString();

    public readonly bool IsSubpaths()
    {
        return ComputeObjectKind() == ObjectKind.ObjectKindSubpaths;
    }

    public readonly bool IsImports()
    {
        return ComputeObjectKind() == ObjectKind.ObjectKindImports;
    }

    public readonly bool IsConditions()
    {
        return ComputeObjectKind() == ObjectKind.ObjectKindConditions;
    }

    // Go: initObjectKind (exportsorimports.go:58-84). PORT-NOTE: the Go memoization
    // never persists — IsSubpaths/IsImports/IsConditions take a value receiver, so
    // initObjectKind mutates a copy and the field resets to Unknown on every call.
    // The C# port therefore recomputes fresh each call, which is behaviorally identical.
    private readonly ObjectKind ComputeObjectKind()
    {
        // Go: initObjectKind only assigns a kind when Type == Object; otherwise the
        // field stays Unknown and every Is* query returns false.
        if (Json.Type != JSONValueType.JSONValueTypeObject)
        {
            return ObjectKind.ObjectKindUnknown;
        }
        OrderedMap<string, ExportsOrImports> obj = AsObject();
        if (obj.Size() > 0)
        {
            bool seenDot = false, seenHash = false, seenOther = false;
            // map-iter: 已排序 (OrderedMap preserves insertion order, matching Go's obj.Keys()).
            foreach (string k in obj.Keys())
            {
                if (k.Length > 0)
                {
                    seenDot = seenDot || k[0] == '.';
                    seenHash = seenHash || k[0] == '#';
                    seenOther = seenOther || (k[0] != '.' && k[0] != '#');
                    if (seenOther && (seenDot || seenHash))
                    {
                        return ObjectKind.ObjectKindInvalid;
                    }
                }
            }
            if (seenDot)
            {
                return ObjectKind.ObjectKindSubpaths;
            }
            if (seenHash)
            {
                return ObjectKind.ObjectKindImports;
            }
        }
        return ObjectKind.ObjectKindConditions;
    }

    // Go: func (e *ExportsOrImports) UnmarshalJSONFrom(dec) → reads into the embedded
    // JSONValue using ExportsOrImports as the nested child type.
    internal static ExportsOrImports Read(ref Utf8JsonReader reader)
    {
        var (type, value) = JSONValue.ReadCore<ExportsOrImports>(ref reader, Read);
        return new ExportsOrImports { Json = new JSONValue { Type = type, Value = value } };
    }
}
