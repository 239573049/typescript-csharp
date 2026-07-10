// Ported from tsgo internal/stringutil/util_test.go
// (TestEncodeURI, TestContainsNonASCII; t.Parallel() dropped per 05 §16.1),
// plus C#-side pinning of the UTF-16 re-audit of the Go sentinel scheme
// (EncodeJSStringRune / DecodeJSStringRune / CombineSurrogatePairs — see the
// PORT-NOTEs in StringUtil/Util.cs; these have no Go counterpart).

using Xunit;

namespace Ts.StringUtil.Tests;

public class UtilTests
{
    // Go: TestEncodeURI table.
    public static TheoryData<string, string, string> EncodeURIData() => new()
    {
        { "encodes spaces as percent20", "a b", "a%20b" },
        { "preserves reserved uri characters", ";/?:@&=+$,#", ";/?:@&=+$,#" },
        { "encodes brackets and unicode using utf8 bytes", "①Ⅻㄨㄩ U1[abc]", "%E2%91%A0%E2%85%AB%E3%84%A8%E3%84%A9%20U1%5Babc%5D" },
    };

    [Theory]
    [MemberData(nameof(EncodeURIData))]
    public void EncodeURI(string name, string input, string expected)
    {
        string got = StringUtil.EncodeURI(input);
        Assert.True(got == expected, $"{name}: EncodeURI(\"{input}\") = \"{got}\", expected \"{expected}\"");
    }

    // Go: TestContainsNonASCII table.
    public static TheoryData<string, string, bool> ContainsNonASCIIData() => new()
    {
        { "ascii", "abc", false },
        { "non-ascii", "é", true },
        { "lone surrogate sentinel", StringUtil.EncodeJSStringRune(0xD800), true },
    };

    [Theory]
    [MemberData(nameof(ContainsNonASCIIData))]
    public void ContainsNonASCII(string name, string text, bool want)
    {
        bool got = StringUtil.ContainsNonASCII(text);
        Assert.True(got == want, $"{name}: ContainsNonASCII(\"{text}\") = {got}, want {want}");
    }

    // ── C#-side re-audit pins (no Go counterpart) ───────────────────────────

    [Fact]
    public void EncodeJSStringRuneEmitsLoneSurrogateAsSingleUnit()
    {
        // Go emitted a 3-byte WTF-8 sentinel; the UTF-16 equivalent is the code
        // unit itself.
        Assert.Equal("\uD800", StringUtil.EncodeJSStringRune(0xD800));
        Assert.Equal("\uDFFF", StringUtil.EncodeJSStringRune(0xDFFF));
        Assert.Equal("a", StringUtil.EncodeJSStringRune('a'));
        Assert.Equal("𐐷", StringUtil.EncodeJSStringRune(0x10437));
        // Go: string(ch) yields U+FFFD for out-of-range runes.
        Assert.Equal("\uFFFD", StringUtil.EncodeJSStringRune(0x110000));
    }

    [Fact]
    public void DecodeJSStringRuneDecodesPairsAndLoneSurrogates()
    {
        Assert.Equal((0x61, 1), StringUtil.DecodeJSStringRune("a"));
        Assert.Equal((0x10437, 2), StringUtil.DecodeJSStringRune("𐐷"));
        // A lone surrogate decodes to its own code point over one unit (Go: the
        // 3-byte sentinel decoded to the same value).
        Assert.Equal((0xD800, 1), StringUtil.DecodeJSStringRune("\uD800x"));
        Assert.Equal((0xDC00, 1), StringUtil.DecodeJSStringRune("\uDC00"));
        // Exhausted input mirrors utf8.DecodeRuneInString("") = (RuneError, 0).
        Assert.Equal((0xFFFD, 0), StringUtil.DecodeJSStringRune(""));
        Assert.Equal((0x62, 1), StringUtil.DecodeJSStringRune("ab", 1));
    }

    [Fact]
    public void CombineSurrogatePairsIsIdentityInUtf16()
    {
        // Concatenating a lone high and a lone low surrogate already IS the
        // supplementary code point in UTF-16 — nothing to canonicalize.
        string s = StringUtil.EncodeJSStringRune(0xD801) + StringUtil.EncodeJSStringRune(0xDC37);
        Assert.Equal("𐐷", s);
        Assert.Same(s, StringUtil.CombineSurrogatePairs(s));
        Assert.Equal((0x10437, 2), StringUtil.DecodeJSStringRune(StringUtil.CombineSurrogatePairs(s)));
    }

    [Fact]
    public void SurrogateHelpersMatchGoUtf16Semantics()
    {
        Assert.True(StringUtil.IsHighSurrogate(0xD800));
        Assert.False(StringUtil.IsHighSurrogate(0xDC00));
        Assert.True(StringUtil.IsLowSurrogate(0xDC00));
        Assert.False(StringUtil.IsSurrogate(0x61));
        Assert.Equal(0x10000, StringUtil.SurrogatePairToCodePoint(0xD800, 0xDC00));
        // Go utf16.DecodeRune yields U+FFFD on an invalid pair.
        Assert.Equal(0xFFFD, StringUtil.SurrogatePairToCodePoint(0xDC00, 0xD800));
        Assert.Equal((0xD801, 0xDC37), StringUtil.CodePointToSurrogatePair(0x10437));
        // Go utf16.EncodeRune yields (U+FFFD, U+FFFD) for non-supplementary input.
        Assert.Equal((0xFFFD, 0xFFFD), StringUtil.CodePointToSurrogatePair(0x61));
    }

    [Fact]
    public void TruncateByRunesCutsAtRuneBoundaries()
    {
        Assert.Equal("ab", StringUtil.TruncateByRunes("abcd", 2));
        Assert.Equal("abcd", StringUtil.TruncateByRunes("abcd", 10));
        Assert.Equal("", StringUtil.TruncateByRunes("abcd", 0));
        // A surrogate pair counts as one rune and is never split.
        Assert.Equal("a𐐷", StringUtil.TruncateByRunes("a𐐷b", 2));
    }

    [Fact]
    public void SplitLinesHandlesAllTerminators()
    {
        Assert.Equal(new[] { "a", "b", "c" }, StringUtil.SplitLines("a\r\nb\nc"));
        Assert.Equal(new[] { "a", "b" }, StringUtil.SplitLines("a\rb"));
        Assert.Equal(new[] { "a" }, StringUtil.SplitLines("a"));
        Assert.Empty(StringUtil.SplitLines(""));
        // No trailing empty element after a final newline (Go parity).
        Assert.Equal(new[] { "a" }, StringUtil.SplitLines("a\n"));
    }

    [Fact]
    public void BomHelpersUseTheDecodedFeffForm()
    {
        Assert.Equal("abc", StringUtil.RemoveByteOrderMark("\uFEFFabc"));
        Assert.Equal("abc", StringUtil.RemoveByteOrderMark("abc"));
        Assert.Equal("\uFEFFabc", StringUtil.AddUTF8ByteOrderMark("abc"));
        Assert.Equal("\uFEFFabc", StringUtil.AddUTF8ByteOrderMark("\uFEFFabc"));
    }

    [Fact]
    public void StripQuotesAndUnquoteStringMatchGo()
    {
        Assert.Equal("abc", StringUtil.StripQuotes("'abc'"));
        Assert.Equal("abc", StringUtil.StripQuotes("\"abc\""));
        Assert.Equal("abc", StringUtil.StripQuotes("`abc`"));
        Assert.Equal("'abc\"", StringUtil.StripQuotes("'abc\""));
        Assert.Equal("x", StringUtil.StripQuotes("x"));
        // Strada-faithful \-something → something replacement.
        Assert.Equal(@"anb", StringUtil.UnquoteString(@"'a\nb'"));
        Assert.Equal("ab", StringUtil.UnquoteString(@"a\b"));
    }
}
