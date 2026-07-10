// Ported from tsgo internal/stringutil/js_case_test.go (TestJSCasing;
// t.Parallel() dropped per 05 §16.1). The Go table computes `got` eagerly in the
// table literal; the theory data factory mirrors that shape.

using Xunit;

namespace Ts.StringUtil.Tests;

public class JsCaseTests
{
    public static TheoryData<string, string, string> JsCasingData() => new()
    {
        { "ascii lowercase", StringUtil.ToLowerJS("HELLO"), "hello" },
        { "ascii uppercase", StringUtil.ToUpperJS("hello"), "HELLO" },
        { "lowercase dotted i", StringUtil.ToLowerJS("İSPANYOL"), "i̇spanyol" },
        { "lowercase lone sigma", StringUtil.ToLowerJS("Σ"), "σ" },
        { "lowercase final sigma", StringUtil.ToLowerJS("ΟΣ"), "ος" },
        { "lowercase non-sigma greek", StringUtil.ToLowerJS("Ω"), "ω" },
        { "uppercase sharp s", StringUtil.ToUpperJS("ßfoo"), "SSFOO" },
        { "uppercase non-ascii simple mapping", StringUtil.ToUpperJS("ω"), "Ω" },
        { "uppercase ligature", StringUtil.ToUpperJS("ﬁoo"), "FIOO" },
        { "capitalize-style uppercase", StringUtil.ToUpperJS("ß") + "foo", "SSfoo" },
        { "uncapitalize-style lowercase", StringUtil.ToLowerJS("İ") + "foo", "i̇foo" },
        { "lowercase final sigma after lowercase letter without uppercase mapping", StringUtil.ToLowerJS("ʕΣ"), "ʕς" },
        { "lowercase sigma after modifier letter", StringUtil.ToLowerJS("ʰΣ"), "ʰσ" },
        { "lowercase sigma after case ignorable ypogegrammeni", StringUtil.ToLowerJS("ͅΣ"), "ͅσ" },
        { "lowercase final sigma after feminine ordinal indicator", StringUtil.ToLowerJS("ªΣ"), "ªς" },
        { "lowercase final sigma after masculine ordinal indicator", StringUtil.ToLowerJS("ºΣ"), "ºς" },
        { "lowercase final sigma after roman numeral", StringUtil.ToLowerJS("ⅠΣ"), "ⅰς" },
        { "lowercase sigma after uppercase property added after unicode 15", StringUtil.ToLowerJS("\u1C89Σ"), "\u1C89σ" },
        { "lowercase sigma after uppercase property skewed from local v8 unicode data", StringUtil.ToLowerJS("\uA7CBΣ"), "\uA7CBσ" },
        { "lowercase sigma before immediate latin letter", StringUtil.ToLowerJS("ΣA"), "σa" },
        { "lowercase sigma before immediate roman numeral letter", StringUtil.ToLowerJS("ΣⅠ"), "σⅰ" },
        { "lowercase sigma before case ignorable then latin letter", StringUtil.ToLowerJS("ΣͅA"), "σͅa" },
        { "uppercase lone surrogate", StringUtil.ToUpperJS(StringUtil.EncodeJSStringRune(0xD800)), StringUtil.EncodeJSStringRune(0xD800) },
        { "lowercase lone surrogate", StringUtil.ToLowerJS("A" + StringUtil.EncodeJSStringRune(0xD800) + "B"), "a" + StringUtil.EncodeJSStringRune(0xD800) + "b" },
        { "uppercase lone low surrogate with text", StringUtil.ToUpperJS(StringUtil.EncodeJSStringRune(0xDC00) + "x"), StringUtil.EncodeJSStringRune(0xDC00) + "X" },
        { "lowercase lone surrogate before sigma", StringUtil.ToLowerJS(StringUtil.EncodeJSStringRune(0xD800) + "Σ"), StringUtil.EncodeJSStringRune(0xD800) + "σ" },
    };

    [Theory]
    [MemberData(nameof(JsCasingData))]
    public void JsCasing(string name, string got, string want)
    {
        Assert.True(got == want, $"{name}: got \"{got}\", want \"{want}\"");
    }
}
