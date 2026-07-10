// Ported from tsgo internal/jsnum/pseudobigint_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System.Collections.Generic;
using Ts.JsNum;
using Xunit;

namespace Ts.JsNum.Tests;

public class PseudoBigIntTests
{
    private static List<Number> TestNumbers()
    {
        var testNumbers = new List<Number>();
        for (long i = 0; i < 1000; i++)
        {
            testNumbers.Add(i);
        }
        for (int bits = 0; bits < 53; bits++)
        {
            testNumbers.Add((double)(1L << bits));
            testNumbers.Add((double)((1L << bits) - 1));
        }
        return testNumbers;
    }

    // Go: TestParsePseudoBigInt t.Run("strip base-10 strings")
    [Fact]
    public void ParsePseudoBigInt_StripBase10Strings()
    {
        foreach (Number testNumber in TestNumbers())
        {
            for (int leadingZeros = 0; leadingZeros < 10; leadingZeros++)
            {
                Assert.Equal(
                    testNumber.ToString(),
                    JsNum.ParsePseudoBigInt(new string('0', leadingZeros) + testNumber.ToString() + "n"));
            }
        }
    }

    // Go: TestParsePseudoBigInt t.Run("parse non-decimal bases (small numbers)")
    [Fact]
    public void ParsePseudoBigInt_ParseNonDecimalBasesSmallNumbers()
    {
        (string Lit, string Out)[] cases =
        [
            // binary
            ("0b0n", "0"),
            ("0b1n", "1"),
            ("0b1010n", "10"),
            ("0b1010_0101n", "165"),
            ("0B1101n", "13"), // uppercase prefix

            // octal
            ("0o0n", "0"),
            ("0o7n", "7"),
            ("0o755n", "493"),
            ("0o7_5_5n", "493"),
            ("0O12n", "10"), // uppercase prefix

            // hex
            ("0x0n", "0"),
            ("0xFn", "15"),
            ("0xFFn", "255"),
            ("0xF_Fn", "255"),
            ("0X1Fn", "31"), // uppercase prefix
        ];

        foreach ((string lit, string @out) in cases)
        {
            Assert.Equal(@out, JsNum.ParsePseudoBigInt(lit));
        }
    }

    // Go: TestParsePseudoBigInt t.Run("can parse large literals")
    [Fact]
    public void ParsePseudoBigInt_CanParseLargeLiterals()
    {
        Assert.Equal("123456789012345678901234567890", JsNum.ParsePseudoBigInt("123456789012345678901234567890n"));
        Assert.Equal("123456789012345678901234567890", JsNum.ParsePseudoBigInt("0b1100011101110100100001111111101101100001101110011111000001110111001001110001111110000101011010010n"));
        Assert.Equal("123456789012345678901234567890", JsNum.ParsePseudoBigInt("0o143564417755415637016711617605322n"));
        Assert.Equal("123456789012345678901234567890", JsNum.ParsePseudoBigInt("0x18ee90ff6c373e0ee4e3f0ad2n"));
    }
}
