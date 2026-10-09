// Ported from typescript-go internal/diagnostics/diagnostics_test.go.

using Xunit;
using D = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Diagnostics.Tests;

public class DiagnosticsTests
{
    private static global::Ts.Locale.Locale Parse(string tag)
    {
        Assert.True(global::Ts.Locale.Locale.TryParse(tag, out var locale), tag);
        return locale;
    }

    // diagnostics_test.go:11-111 — TestLocalize (each t.Run row is a Theory row).
    [Theory]
    [InlineData("english default", "Identifier_expected_1003", "en", null, "Identifier expected.")]
    [InlineData("undefined locale uses english", "Identifier_expected_1003", "und", null, "Identifier expected.")]
    [InlineData("with single argument", "_0_expected_1005", "en", ")", "')' expected.")]
    [InlineData("fallback to english for unknown locale", "Identifier_expected_1003", "af-ZA", null, "Identifier expected.")]
    [InlineData("german", "Identifier_expected_1003", "de-DE", null, "Es wurde ein Bezeichner erwartet.")]
    [InlineData("french", "Identifier_expected_1003", "fr-FR", null, "Identificateur attendu.")]
    [InlineData("spanish", "Identifier_expected_1003", "es-ES", null, "Se esperaba un identificador.")]
    [InlineData("japanese", "Identifier_expected_1003", "ja-JP", null, "識別子が必要です。")]
    [InlineData("chinese simplified", "Identifier_expected_1003", "zh-CN", null, "应为标识符。")]
    [InlineData("korean", "Identifier_expected_1003", "ko-KR", null, "식별자가 필요합니다.")]
    [InlineData("russian", "Identifier_expected_1003", "ru-RU", null, "Ожидался идентификатор.")]
    [InlineData("german with args", "_0_expected_1005", "de-DE", ")", "\")\" wurde erwartet.")]
    public void TestLocalize(string name, string key, string tag, string? arg, string expected)
    {
        _ = name;
        var message = D.KeyToMessage(key)!;
        var result = arg == null ? message.Localize(Parse(tag)) : message.Localize(Parse(tag), arg);
        Assert.Equal(expected, result);
    }

    // diagnostics_test.go:41-46 — "with multiple arguments".
    [Fact]
    public void TestLocalize_WithMultipleArguments()
    {
        var result = D.The_parser_expected_to_find_a_1_to_match_the_0_token_here.Localize(Parse("en"), "{", "}");
        Assert.Equal("The parser expected to find a '}' to match the '{' token here.", result);
    }

    // diagnostics_test.go:113-145 — TestLocalize_ByKey.
    [Theory]
    [InlineData("by key without args", "Identifier_expected_1003", new string[0], "Identifier expected.")]
    [InlineData("by key with args", "_0_expected_1005", new[] { ")" }, "')' expected.")]
    public void TestLocalize_ByKey(string name, string key, string[] args, string expected)
    {
        _ = name;
        Assert.Equal(expected, D.Localize(Parse("en"), null, key, args));
    }

    // PORT-NOTE: C#-only table for the hand-written x/text matcher stand-in
    // (plan/04 §3.5-7: "对照 x/text matcher 在此集合上的行为写表驱动单测").
    [Theory]
    [InlineData("en", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("de", "de-DE")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("pt", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh", "zh-CN")]
    [InlineData("zh-Hans", "zh-CN")]
    [InlineData("zh-SG", "zh-CN")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("ja-jp", "ja-JP")]
    [InlineData("tr", "tr-TR")]
    [InlineData("af-ZA", null)]
    public void MatchLocale(string tag, string? expected)
    {
        int index = D.MatchLocale(tag);
        Assert.Equal(expected, index < 0 ? null : D.LocaleTags[index]);
    }
}
