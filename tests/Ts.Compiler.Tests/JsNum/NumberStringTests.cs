// Ported from tsgo internal/jsnum/string_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
//
// PORT-NOTE: TestStringJS, FuzzStringJS, FuzzFromStringJS and their Node.js
// plumbing (getStringResultsFromJS, numberToUint32Array, uint32ArrayToNumber,
// isFuzzing) are not ported — plan/05 §16.1 (Fuzz 不移植; the Node cross-check
// is covered by the phase-1 G1.2 dumptool fixture). All pinned tables are
// ported in full.

using System;
using Ts.JsNum;
using Xunit;

namespace Ts.JsNum.Tests;

// Go: stringTest
internal readonly record struct StringTest(Number Number, string Str);

public class NumberStringTests
{
    // Go: stringTests = slices.Concat([]stringTest{...}, ryuTests)
    internal static readonly StringTest[] StringTests =
    [
        new(Number.NaN, "NaN"),
        new(Number.Inf(1), "Infinity"),
        new(Number.Inf(-1), "-Infinity"),
        new(0, "0"),
        new(Number.NegativeZero, "0"),
        new(1, "1"),
        new(-1, "-1"),
        new(0.3, "0.3"),
        new(-0.3, "-0.3"),
        new(1.5, "1.5"),
        new(-1.5, "-1.5"),
        new(1e308, "1e+308"),
        new(-1e308, "-1e+308"),
        new(Math.PI, "3.141592653589793"),
        new(-Math.PI, "-3.141592653589793"),
        new(Number.MaxSafeInteger, "9007199254740991"),
        new(Number.MinSafeInteger, "-9007199254740991"),
        new(JsNumTests.NumberFromBits(0x000FFFFFFFFFFFFF), "2.225073858507201e-308"),
        new(JsNumTests.NumberFromBits(0x0010000000000000), "2.2250738585072014e-308"),
        new(1234567.8, "1234567.8"),
        new(19686109595169230000.0, "19686109595169230000"),
        new(123.456, "123.456"),
        new(-123.456, "-123.456"),
        new(444123, "444123"),
        new(-444123, "-444123"),
        new(444123.789123456789875436, "444123.7891234568"),
        new(-444123.78963636363636363636, "-444123.7896363636"),
        new(1e21, "1e+21"),
        new(1e20, "100000000000000000000"),
        .. RyuTests.Tests,
    ];

    public static TheoryData<double, string> StringData()
    {
        var data = new TheoryData<double, string>();
        foreach (StringTest test in StringTests)
        {
            data.Add(test.Number.Value, test.Str);
        }
        return data;
    }

    // Go: TestString
    [Theory]
    [MemberData(nameof(StringData))]
    public void String(double number, string str)
    {
        Assert.Equal(str, ((Number)number).ToString());
    }

    // Go: fromStringTests
    internal static readonly StringTest[] FromStringTests =
    [
        new(Number.NaN, "    NaN"),
        new(Number.Inf(1), "Infinity    "),
        new(Number.Inf(-1), "    -Infinity"),
        new(1, "1."),
        new(1, "1.0   "),
        new(1, "+1"),
        new(1, "+1."),
        new(1, "+1.0"),
        new(Number.NaN, "whoops"),
        new(0, ""),
        new(0, "0"),
        new(0, "0."),
        new(0, "0.0"),
        new(0, "0.0000"),
        new(0, ".0000"),
        new(Number.NegativeZero, "-0"),
        new(Number.NegativeZero, "-0."),
        new(Number.NegativeZero, "-0.0"),
        new(Number.NegativeZero, "-.0"),
        new(Number.NaN, "."),
        new(Number.NaN, "e"),
        new(Number.NaN, ".e"),
        new(Number.NaN, "+"),
        new(0, "0X0"),
        new(Number.NaN, "e0"),
        new(Number.NaN, "E0"),
        new(Number.NaN, "1e"),
        new(Number.NaN, "1e+"),
        new(Number.NaN, "1e-"),
        new(1, "1e+0"),
        new(Number.NaN, "++0"),
        new(Number.NaN, "0_0"),
        new(Number.Inf(1), "1e1000"),
        new(Number.Inf(-1), "-1e1000"),
        new(0, ".0e0"),
        new(Number.NaN, "0e++0"),
        new(10, "0XA"),
        new(0b1010, "0b1010"),
        new(0b1010, "0B1010"),
        new(10, "0o12"), // Go: 0o12
        new(10, "0O12"), // Go: 0o12
        new(0x123456789abcdef0, "0x123456789abcdef0"),
        new(0x123456789abcdef0, "0X123456789ABCDEF0"),
        new(18446744073709552000.0, "0X10000000000000000"),
        new(18446744073709597000.0, "0X1000000000000A801"),
        new(Number.NaN, "0B0.0"),
        new(1.231235345083403e+91, "12312353450834030486384068034683603046834603806830644850340602384608368034634603680348603864"),
        new(Number.NaN, "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX8OOOOOOOOOOOOOOOOOOO"),
        new(Number.Inf(1), "+Infinity"),
        new(1234.56, "  \t1234.56  "),
        new(Number.NaN, "\u200b"),
        new(0, " "),
        new(0, "\n"),
        new(0, "\r"),
        new(0, "\r\n"),
        new(0, "\u2028"),
        new(0, "\u2029"),
        new(0, "\t"),
        new(0, "\v"),
        new(0, "\f"),
        new(0, "\uFEFF"),
        new(0, "\u00A0"),
        new(10000000000000000000.0, "010000000000000000000"),
        new(Number.NaN, "0x1.fffffffffffffp1023"), // Make sure Go's extended float syntax doesn't work.
        new(Number.NaN, "0X_1FFFP-16"),
        new(Number.NaN, "1_000"), // NumberToString doesn't handle underscores.
        new(0, "0x0"),
        new(0, "0X0"),
        new(Number.NaN, "0xOOPS"),
        new(0xABCDEF, "0xABCDEF"),
        new(0xABCDEF, "0xABCDEF"),
        new(0, "0o0"),
        new(0, "0O0"),
        new(Number.NaN, "0o8"),
        new(Number.NaN, "0O8"),
        new(5349, "0o12345"), // Go: 0o12345
        new(5349, "0O12345"), // Go: 0o12345
        new(0, "0b0"),
        new(0, "0B0"),
        new(Number.NaN, "0b2"),
        new(Number.NaN, "0b2"),
        new(0b10101, "0b10101"),
        new(0b10101, "0B10101"),
        new(Number.NaN, "1.f"),
        new(Number.NaN, "1.e"),
        new(Number.NaN, "1.0ef"),
        new(Number.NaN, "1.0e"),
        new(Number.NaN, ".f"),
        new(Number.NaN, ".e"),
        new(Number.NaN, ".0ef"),
        new(Number.NaN, ".0e"),
        new(Number.NaN, "a.f"),
        new(Number.NaN, "a.e"),
        new(Number.NaN, "a.0ef"),
        new(Number.NaN, "a.0e"),
    ];

    public static TheoryData<double, string> FromStringData()
    {
        var data = new TheoryData<double, string>();
        foreach (StringTest test in FromStringTests)
        {
            data.Add(test.Number.Value, test.Str);
        }
        return data;
    }

    // Go: TestFromString t.Run("stringTests")
    [Theory]
    [MemberData(nameof(StringData))]
    public void FromString_StringTests(double number, string str)
    {
        JsNumTests.AssertEqualNumber(JsNum.FromString(str), number);
        JsNumTests.AssertEqualNumber(JsNum.FromString(str + " "), number);
        JsNumTests.AssertEqualNumber(JsNum.FromString(" " + str), number);
    }

    // Go: TestFromString t.Run("fromStringTests")
    [Theory]
    [MemberData(nameof(FromStringData))]
    public void FromString_FromStringTests(double number, string str)
    {
        JsNumTests.AssertEqualNumber(JsNum.FromString(str), number);
    }

    public static TheoryData<string> StringRoundtripData()
    {
        var data = new TheoryData<string>();
        foreach (StringTest test in StringTests)
        {
            data.Add(test.Str);
        }
        return data;
    }

    // Go: TestStringRoundtrip
    [Theory]
    [MemberData(nameof(StringRoundtripData))]
    public void StringRoundtrip(string str)
    {
        Assert.Equal(str, JsNum.FromString(str).ToString());
    }
}
