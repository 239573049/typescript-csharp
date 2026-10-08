// Support partial for Ls/InlayHints.cs (tsgo internal/ls/inlay_hints.go).
//
// Holds the pieces the 1:1 port needs that are not yet provided by the shared
// foundation files (LsDeps.cs / LanguageService.cs / Host.cs / Lsp/Protocol.cs):
//   1. lsproto wire types for inlay hints (lsp_generated.go subset).
//   2. lsutil.InlayHintsPreferences + the IncludeInlayParameterNameHints enum
//      (userpreferences.go:205-249). The shared C# UserPreferences does not carry the
//      InlayHints field yet, so the preference struct is threaded as an explicit entry
//      parameter (default = Go's zero value: every hint kind disabled) until the
//      lsp/config wave adds it.
//   3. ast.IsInfinityOrNaNString (ast/utilities.go:4196) — not yet ported into Ts.Ast.
//   4. converters.FromLSPRange over a plain SourceFile (converters.go:47-52) — the
//      foundation has no per-request Converters/Script pair.

using System;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using Ts.Core; // Tristate

namespace Ts.LsProto
{

// lsp_generated.go: InlayHintLabelPart.
public sealed class InlayHintLabelPart
{
    [System.Runtime.Serialization.DataMember(Name = "value")]
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public required string Value { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "tooltip")]
    [System.Text.Json.Serialization.JsonPropertyName("tooltip")]
    public object? Tooltip { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "location")]
    [System.Text.Json.Serialization.JsonPropertyName("location")]
    public Location? Location { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "command")]
    [System.Text.Json.Serialization.JsonPropertyName("command")]
    public object? Command { get; set; }
}

// lsp_generated.go: StringOrInlayHintLabelParts.
public sealed class StringOrInlayHintLabelParts
{
    [System.Runtime.Serialization.DataMember(Name = "string")]
    [System.Text.Json.Serialization.JsonPropertyName("string")]
    public string? String { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "parts")]
    [System.Text.Json.Serialization.JsonPropertyName("parts")]
    public InlayHintLabelPart[]? InlayHintLabelParts { get; set; }
}

// lsp_generated.go: InlayHintKind.
public enum InlayHintKind : uint
{
    Type = 1,
    Parameter = 2,
}

// lsp_generated.go: InlayHint.
public sealed class InlayHint
{
    [System.Runtime.Serialization.DataMember(Name = "label")]
    [System.Text.Json.Serialization.JsonPropertyName("label")]
    public required StringOrInlayHintLabelParts Label { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "position")]
    [System.Text.Json.Serialization.JsonPropertyName("position")]
    public required Position Position { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "kind")]
    [System.Text.Json.Serialization.JsonPropertyName("kind")]
    public InlayHintKind? Kind { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "paddingLeft")]
    [System.Text.Json.Serialization.JsonPropertyName("paddingLeft")]
    public bool? PaddingLeft { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "paddingRight")]
    [System.Text.Json.Serialization.JsonPropertyName("paddingRight")]
    public bool? PaddingRight { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "textEdits")]
    [System.Text.Json.Serialization.JsonPropertyName("textEdits")]
    public object[]? TextEdits { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "tooltip")]
    [System.Text.Json.Serialization.JsonPropertyName("tooltip")]
    public object? Tooltip { get; set; }

    [System.Runtime.Serialization.DataMember(Name = "data")]
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public object? Data { get; set; }
}

}

namespace Ts.Ls
{

// lsutil/userpreferences.go:244-249 — IncludeInlayParameterNameHints (Go string enum).
public enum IncludeInlayParameterNameHints
{
    // userpreferences.go:247 — IncludeInlayParameterNameHintsNone (the zero value, "").
    None = 0,
    // userpreferences.go:248 — IncludeInlayParameterNameHintsAll ("all").
    All,
    // userpreferences.go:249 — IncludeInlayParameterNameHintsLiterals ("literals").
    Literals,
}

// lsutil/userpreferences.go:205-214 — InlayHintsPreferences.
public struct InlayHintsPreferences
{
    // userpreferences.go:206 — IncludeInlayParameterNameHints.
    public IncludeInlayParameterNameHints IncludeInlayParameterNameHints;
    // userpreferences.go:207 — IncludeInlayParameterNameHintsWhenArgumentMatchesName (config ",invert").
    public Tristate IncludeInlayParameterNameHintsWhenArgumentMatchesName;
    // userpreferences.go:208 — IncludeInlayFunctionParameterTypeHints.
    public Tristate IncludeInlayFunctionParameterTypeHints;
    // userpreferences.go:209 — IncludeInlayVariableTypeHints.
    public Tristate IncludeInlayVariableTypeHints;
    // userpreferences.go:210 — IncludeInlayVariableTypeHintsWhenTypeMatchesName (config ",invert").
    public Tristate IncludeInlayVariableTypeHintsWhenTypeMatchesName;
    // userpreferences.go:211 — IncludeInlayPropertyDeclarationTypeHints.
    public Tristate IncludeInlayPropertyDeclarationTypeHints;
    // userpreferences.go:212 — IncludeInlayFunctionLikeReturnTypeHints.
    public Tristate IncludeInlayFunctionLikeReturnTypeHints;
    // userpreferences.go:213 — IncludeInlayEnumMemberValueHints.
    public Tristate IncludeInlayEnumMemberValueHints;
}

public sealed partial class LanguageService
{
    // ast/utilities.go:4196 — IsInfinityOrNaNString.
    internal static bool IsInfinityOrNaNString(string name)
    {
        return name == "Infinity" || name == "-Infinity" || name == "NaN";
    }

    // lsconv/converters.go:47-52 — FromLSPRange over a SourceFile (foundation form; the
    // per-request Converters/Script pair dissolves into the LsDeps line map).
    private static global::Ts.Core.TextRange FromLspRange(SourceFile file, global::Ts.LsProto.Range range)
    {
        return new global::Ts.Core.TextRange(
            LsDeps.LineAndCharacterToPosition(file, range.Start.Line, range.Start.Character),
            LsDeps.LineAndCharacterToPosition(file, range.End.Line, range.End.Character));
    }
}
}
