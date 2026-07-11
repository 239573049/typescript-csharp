// Byte-faithful Chrome trace_event / legend / types.json writer.
//
// PORT-NOTE: 非 1:1，EventSource 重写 (plan/06 §9.2) — but the *bytes* here are the
// faithful counterpart to Go's json.MarshalWrite(..., json.Deterministic(true)). tsgo
// serializes traceEvent / TraceRecord / TypeDescriptor structs and map[string]any args
// with the experimental encoding/json/v2 in "deterministic" mode (sorted map keys).
// We reproduce those exact bytes with a hand-written Utf8JsonWriter (plan/07 §4.1:
// byte-diff surfaces are hand-written writers, never the reflection serializer) using
// JavaScriptEncoder.UnsafeRelaxedJsonEscaping so non-ASCII / <>& pass through like Go.
//
// Field ordering is Go struct-declaration order; `omitzero` fields are dropped when
// they hold their zero value. Map (args) keys are emitted in Ordinal-sorted order to
// match json.Deterministic(true).

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ts.Tracing;

internal static class TraceJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = true,
    };

    // writeEventTo (tracing.go:201-205): appends one compact JSON object to the builder.
    public static void WriteEventTo(StringBuilder buf, TraceEvent evt)
    {
        buf.Append(SerializeEvent(evt));
    }

    private static string SerializeEvent(TraceEvent evt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            // Struct field order (tracing.go:79-89).
            writer.WriteNumber("pid", evt.PID);
            writer.WriteNumber("tid", evt.TID);
            writer.WriteString("ph", evt.PH);
            writer.WriteString("cat", evt.Cat);
            WriteTs(writer, "ts", evt.TS);
            if (evt.Name.Length != 0)
            {
                writer.WriteString("name", evt.Name);
            }
            if (evt.S.Length != 0)
            {
                writer.WriteString("s", evt.S);
            }
            if (evt.Dur is double dur)
            {
                WriteTs(writer, "dur", dur);
            }
            if (evt.Args is { Count: > 0 } args)
            {
                writer.WritePropertyName("args");
                WriteArgs(writer, args);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // Go marshals float64 ts/dur. encoding/json emits integral floats without a decimal
    // point (e.g. 1, not 1.0) and uses the shortest round-trippable representation
    // otherwise. Utf8JsonWriter.WriteNumber(double) matches this (shortest round-trip,
    // integral values without fraction).
    private static void WriteTs(Utf8JsonWriter writer, string name, double value)
    {
        writer.WriteNumber(name, value);
    }

    // WriteArgs serializes map[string]any with Ordinal-sorted keys (json.Deterministic).
    private static void WriteArgs(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> args)
    {
        writer.WriteStartObject();
        var keys = new List<string>(args.Count);
        // map-iter: 序无关 —— collecting keys, then sorting explicitly below.
        foreach (KeyValuePair<string, object?> kv in args)
        {
            keys.Add(kv.Key);
        }
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            writer.WritePropertyName(key);
            WriteAny(writer, args[key]);
        }
        writer.WriteEndObject();
    }

    private static void WriteAny(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case uint u:
                writer.WriteNumberValue(u);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            default:
                // PORT-NOTE: Go's map[string]any only ever holds the primitives the
                // callers pass (strings, ints, bools). Any other shape is stringified
                // rather than reflected, to stay within the AOT reflection ban.
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    // MarshalLegend reproduces json.MarshalIndent(legend, "", "  ") (tracing.go:473).
    // Go's MarshalIndent emits 2-space indentation and drops omitzero string fields.
    public static string MarshalLegend(IReadOnlyList<TraceRecord> legend)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var indentedOptions = new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true,
            IndentCharacter = ' ',
            IndentSize = 2,
            SkipValidation = true,
        };
        using (var writer = new Utf8JsonWriter(buffer, indentedOptions))
        {
            writer.WriteStartArray();
            foreach (TraceRecord record in legend)
            {
                writer.WriteStartObject();
                // Struct field order (tracing.go:72-77).
                if (record.ConfigFilePath.Length != 0)
                {
                    writer.WriteString("configFilePath", record.ConfigFilePath);
                }
                if (record.TracePath.Length != 0)
                {
                    writer.WriteString("tracePath", record.TracePath);
                }
                if (record.TypesPath.Length != 0)
                {
                    writer.WriteString("typesPath", record.TypesPath);
                }
                writer.WriteNumber("checkerId", record.CheckerID);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // SerializeTypeDescriptor reproduces json.MarshalWrite(&sb, descriptor) for one
    // TypeDescriptor (tracing.go:519). Compact (no indent), Go struct field order,
    // omitzero drops empty/zero fields; `flags` is always present (no omitzero tag).
    public static string SerializeTypeDescriptor(TypeDescriptor d)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", d.ID);
            WriteStrIf(writer, "intrinsicName", d.IntrinsicName);
            WriteStrIf(writer, "symbolName", d.SymbolName);
            WriteIntPtrIf(writer, "recursionId", d.RecursionID);
            if (d.IsTuple)
            {
                writer.WriteBoolean("isTuple", true);
            }
            WriteUintArrIf(writer, "unionTypes", d.UnionTypes);
            WriteUintArrIf(writer, "intersectionTypes", d.IntersectionTypes);
            WriteUintArrIf(writer, "aliasTypeArguments", d.AliasTypeArguments);
            WriteUintPtrIf(writer, "keyofType", d.KeyofType);
            WriteUintPtrIf(writer, "indexedAccessObjectType", d.IndexedAccessObjectType);
            WriteUintPtrIf(writer, "indexedAccessIndexType", d.IndexedAccessIndexType);
            WriteUintPtrIf(writer, "conditionalCheckType", d.ConditionalCheckType);
            WriteUintPtrIf(writer, "conditionalExtendsType", d.ConditionalExtendsType);
            WriteInt32PtrIf(writer, "conditionalTrueType", d.ConditionalTrueType);
            WriteInt32PtrIf(writer, "conditionalFalseType", d.ConditionalFalseType);
            WriteUintPtrIf(writer, "substitutionBaseType", d.SubstitutionBaseType);
            WriteUintPtrIf(writer, "constraintType", d.ConstraintType);
            WriteUintPtrIf(writer, "instantiatedType", d.InstantiatedType);
            WriteUintArrIf(writer, "typeArguments", d.TypeArguments);
            WriteLocationIf(writer, "referenceLocation", d.ReferenceLocation);
            WriteUintPtrIf(writer, "reverseMappedSourceType", d.ReverseMappedSourceType);
            WriteUintPtrIf(writer, "reverseMappedMappedType", d.ReverseMappedMappedType);
            WriteUintPtrIf(writer, "reverseMappedConstraintType", d.ReverseMappedConstraintType);
            WriteUintPtrIf(writer, "evolvingArrayElementType", d.EvolvingArrayElementType);
            WriteUintPtrIf(writer, "evolvingArrayFinalType", d.EvolvingArrayFinalType);
            WriteLocationIf(writer, "destructuringPattern", d.DestructuringPattern);
            WriteLocationIf(writer, "firstDeclaration", d.FirstDeclaration);
            // flags has no omitzero: always emitted (empty array if none).
            writer.WritePropertyName("flags");
            writer.WriteStartArray();
            if (d.Flags != null)
            {
                foreach (string flag in d.Flags)
                {
                    writer.WriteStringValue(flag);
                }
            }
            writer.WriteEndArray();
            WriteStrIf(writer, "display", d.Display);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteStrIf(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteIntPtrIf(Utf8JsonWriter writer, string name, int? value)
    {
        // *int with omitzero: Go emits the pointer's target when the pointer is non-nil,
        // even if the target is 0. omitzero on a pointer means "omit when nil".
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteInt32PtrIf(Utf8JsonWriter writer, string name, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteUintPtrIf(Utf8JsonWriter writer, string name, uint? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static void WriteUintArrIf(Utf8JsonWriter writer, string name, uint[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return;
        }
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (uint v in values)
        {
            writer.WriteNumberValue(v);
        }
        writer.WriteEndArray();
    }

    private static void WriteLocationIf(Utf8JsonWriter writer, string name, Location? loc)
    {
        if (loc == null)
        {
            return;
        }
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        // Location field order (tracing.go:569-573): path always present, start/end omitzero.
        writer.WriteString("path", loc.Path);
        WriteLineAndCharIf(writer, "start", loc.Start);
        WriteLineAndCharIf(writer, "end", loc.End);
        writer.WriteEndObject();
    }

    private static void WriteLineAndCharIf(Utf8JsonWriter writer, string name, LineAndChar? lc)
    {
        if (lc == null)
        {
            return;
        }
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("line", lc.Line);
        writer.WriteNumber("character", lc.Character);
        writer.WriteEndObject();
    }

    // Formats an integer to invariant string; kept for parity with any callers needing it.
    internal static string Itoa(int value) => value.ToString(CultureInfo.InvariantCulture);
}
