// Tests for Ts.Locale.global::Ts.Locale.Locale.TryParse (locale.go:23-27 → BCP-47 well-formedness stand-in for
// x/text language.Parse; deviation D-020). No Go test file exists for internal/locale.

using Xunit;

namespace Ts.Locale.Tests;

public class LocaleTests
{
    [Theory]
    [InlineData("en", "en")]
    [InlineData("ja-jp", "ja-JP")]
    [InlineData("zh_hant_tw", "zh-Hant-TW")]
    [InlineData("es-419", "es-419")]
    [InlineData("de-DE-1996", "de-DE-1996")]
    [InlineData("en-US-u-ca-gregory", "en-US-u-ca-gregory")]
    [InlineData("en-x-private", "en-x-private")]
    [InlineData("und", "")]
    public void ParsesWellFormedTags(string input, string canonical)
    {
        Assert.True(global::Ts.Locale.Locale.TryParse(input, out var locale));
        Assert.Equal(canonical, locale.Tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("e")]
    [InlineData("englishlanguage")]
    [InlineData("en--US")]
    [InlineData("en-US-u")]
    [InlineData("en-x")]
    [InlineData("1en")]
    [InlineData("en US")]
    public void RejectsMalformedTags(string input)
    {
        Assert.False(global::Ts.Locale.Locale.TryParse(input, out var locale));
        Assert.Equal(global::Ts.Locale.Locale.Default, locale);
    }
}
