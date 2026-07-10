// C#-side tests for the compare.go port (tsgo has no compare_test.go). Expected
// values below are pinned against the Go toolchain the port mirrors
// (go1.26.4 strings.EqualFold / strings.Compare, verified at port time).

using Xunit;

namespace Ts.StringUtil.Tests;

public class CompareTests
{
    [Fact]
    public void EquateStringCaseInsensitiveIsSimpleCaseFolding()
    {
        Assert.True(StringUtil.EquateStringCaseInsensitive("hello", "HELLO"));
        // Multi-member fold orbits (K KELVIN SIGN, ſ LONG S, final sigma, theta symbols).
        Assert.True(StringUtil.EquateStringCaseInsensitive("K", "k"));
        Assert.True(StringUtil.EquateStringCaseInsensitive("ſ", "S"));
        Assert.True(StringUtil.EquateStringCaseInsensitive("ς", "σ"));
        Assert.True(StringUtil.EquateStringCaseInsensitive("ϑ", "ϴ"));
        Assert.True(StringUtil.EquateStringCaseInsensitive("ß", "ẞ"));
        // Simple folding is per-rune: no full-folding expansions.
        Assert.False(StringUtil.EquateStringCaseInsensitive("ß", "SS"));
        // Turkish dotted/dotless i fold only to themselves (Go parity; contrast
        // OrdinalIgnoreCase-style per-unit uppercasing).
        Assert.False(StringUtil.EquateStringCaseInsensitive("İ", "i"));
        Assert.False(StringUtil.EquateStringCaseInsensitive("ı", "I"));
    }

    [Fact]
    public void CompareStringsCaseSensitiveUsesCodePointOrder()
    {
        // Go string order is UTF-8 byte order = code point order. U+FFFF sorts
        // below U+10000 even though its UTF-16 unit (0xFFFF) is above the
        // surrogates that encode U+10000 (string.CompareOrdinal would say +1).
        Assert.Equal(StringUtil.ComparisonLessThan, StringUtil.CompareStringsCaseSensitive("￿", "𐀀"));
        Assert.Equal(StringUtil.ComparisonGreaterThan, StringUtil.CompareStringsCaseSensitive("𐀀", "￿"));
        Assert.Equal(StringUtil.ComparisonEqual, StringUtil.CompareStringsCaseSensitive("abc", "abc"));
        Assert.Equal(StringUtil.ComparisonLessThan, StringUtil.CompareStringsCaseSensitive("ab", "abc"));
        Assert.Equal(StringUtil.ComparisonGreaterThan, StringUtil.CompareStringsCaseSensitive("b", "a"));
    }

    [Fact]
    public void CompareStringsCaseInsensitiveLowersPerRune()
    {
        Assert.Equal(StringUtil.ComparisonEqual, StringUtil.CompareStringsCaseInsensitive("HeLLo", "hello"));
        Assert.Equal(StringUtil.ComparisonLessThan, StringUtil.CompareStringsCaseInsensitive("A", "b"));
        Assert.Equal(StringUtil.ComparisonGreaterThan, StringUtil.CompareStringsCaseInsensitive("b", "A"));
        // '_' (0x5F) sorts below lowered letters — the property the eslint variant
        // documents; both functions lower here (Go parity).
        Assert.Equal(StringUtil.ComparisonLessThan, StringUtil.CompareStringsCaseInsensitive("__String", "Foo"));
        Assert.Equal(StringUtil.ComparisonLessThan, StringUtil.CompareStringsCaseInsensitiveEslintCompatible("__String", "Foo"));
    }

    [Fact]
    public void CaseInsensitiveThenSensitiveBreaksTiesCaseSensitively()
    {
        Assert.Equal(StringUtil.ComparisonEqual, StringUtil.CompareStringsCaseInsensitiveThenSensitive("abc", "abc"));
        // "ABC" vs "abc": equal ignoring case; code-point order then puts 'A' first.
        Assert.Equal(StringUtil.ComparisonLessThan, StringUtil.CompareStringsCaseInsensitiveThenSensitive("ABC", "abc"));
        Assert.Equal(StringUtil.ComparisonGreaterThan, StringUtil.CompareStringsCaseInsensitiveThenSensitive("abc", "ABC"));
    }

    [Fact]
    public void PrefixSuffixHelpersRespectCaseSensitivity()
    {
        Assert.True(StringUtil.HasPrefix("Foobar", "Foo", caseSensitive: true));
        Assert.False(StringUtil.HasPrefix("Foobar", "foo", caseSensitive: true));
        Assert.True(StringUtil.HasPrefix("Foobar", "foo", caseSensitive: false));
        Assert.False(StringUtil.HasPrefix("Fo", "Foo", caseSensitive: false));
        Assert.True(StringUtil.HasSuffix("Foobar", "bar", caseSensitive: true));
        Assert.True(StringUtil.HasSuffix("Foobar", "BAR", caseSensitive: false));
        Assert.False(StringUtil.HasSuffix("Foobar", "baz", caseSensitive: false));
        // Prefix and suffix must not overlap.
        Assert.True(StringUtil.HasPrefixAndSuffixWithoutOverlap("abcde", "ab", "de", caseSensitive: true));
        Assert.False(StringUtil.HasPrefixAndSuffixWithoutOverlap("abc", "ab", "bc", caseSensitive: true));
    }

    [Fact]
    public void GetComparerFactoriesSelectTheRightFunction()
    {
        Assert.True(StringUtil.GetStringEqualityComparer(ignoreCase: true)("A", "a"));
        Assert.False(StringUtil.GetStringEqualityComparer(ignoreCase: false)("A", "a"));
        Assert.Equal(StringUtil.ComparisonEqual, StringUtil.GetStringComparer(ignoreCase: true)("A", "a"));
        Assert.NotEqual(StringUtil.ComparisonEqual, StringUtil.GetStringComparer(ignoreCase: false)("A", "a"));
    }
}
