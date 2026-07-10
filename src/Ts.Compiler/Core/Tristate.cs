using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ts.Core;

// Go: internal/core/tristate.go
// byte-backed; Unknown MUST stay 0 — the zero-value/omitzero semantics are
// load-bearing throughout CompilerOptions. Member mapping: TSUnknown→Unknown,
// TSFalse→False, TSTrue→True (C# enum members are scoped by the type, so the
// Go "TS" prefix is dropped).
public enum Tristate : byte
{
    Unknown = 0,
    False = 1,
    True = 2,
}

public static class TristateExtensions
{
    public static bool IsTrue(this Tristate t) => t == Tristate.True;

    public static bool IsTrueOrUnknown(this Tristate t) => t is Tristate.True or Tristate.Unknown;

    public static bool IsFalse(this Tristate t) => t == Tristate.False;

    public static bool IsFalseOrUnknown(this Tristate t) => t is Tristate.False or Tristate.Unknown;

    public static bool IsUnknown(this Tristate t) => t == Tristate.Unknown;

    public static Tristate DefaultIfUnknown(this Tristate t, Tristate value) =>
        t == Tristate.Unknown ? value : t;

    public static Tristate BoolToTristate(bool b) => b ? Tristate.True : Tristate.False;
}

// Go: tristate.go MarshalJSON/UnmarshalJSON — true/false on the wire, null for
// Unknown; anything else (including null) reads back as Unknown.
public sealed class TristateJsonConverter : JsonConverter<Tristate>
{
    public override Tristate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => Tristate.True,
            JsonTokenType.False => Tristate.False,
            _ => Tristate.Unknown,
        };

    public override void Write(Utf8JsonWriter writer, Tristate value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case Tristate.True:
                writer.WriteBooleanValue(true);
                break;
            case Tristate.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
