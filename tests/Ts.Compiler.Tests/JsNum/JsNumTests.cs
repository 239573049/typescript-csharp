// Ported from tsgo internal/jsnum/jsnum_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
//
// PORT-NOTE: the Node.js cross-check subtests (t.Run("Node", ...) driven by
// evalBinaryOp/evalUnaryOp via internal/testutil/jstest) and Benchmark* functions
// are not ported — plan/05 §16.1 (Benchmark → BenchmarkDotNet, phase 10; the Node
// harness re-verifies the same pinned tables against a live engine and is covered
// by the phase-1 G1.2 dumptool fixture instead). assertWithinOneULP and the
// binary/unary JSON plumbing existed only for those subtests and are not needed.
// The `bench` column of toInt32Tests is dropped with the benchmarks.

using System;
using Ts.JsNum;
using Xunit;

namespace Ts.JsNum.Tests;

public class JsNumTests
{
    // Go: assertEqualNumber — parameter order kept as Go's (got, want); the
    // failure message labels both, so no expected/actual confusion arises.
    // Go's assert.Equal on Number is IEEE == (NaN handled separately; -0 == 0
    // passes here exactly as it does in Go).
    internal static void AssertEqualNumber(Number got, Number want)
    {
        if (got.IsNaN() || want.IsNaN())
        {
            Assert.True(got.IsNaN() == want.IsNaN(), FormattableString.Invariant($"got: {got}, want: {want}"));
        }
        else
        {
            Assert.True(got.Value == want.Value, FormattableString.Invariant($"got: {got}, want: {want}"));
        }
    }

    // Go: numberFromBits
    internal static Number NumberFromBits(ulong b) => BitConverter.UInt64BitsToDouble(b);

    // Go: toInt32Tests (bench column dropped)
    public static TheoryData<string, double, int> ToInt32Data()
    {
        var data = new TheoryData<string, double, int>
        {
            { "0.0", 0, 0 },
            { "-0.0", Number.NegativeZero.Value, 0 },
            { "NaN", double.NaN, 0 },
            { "+Inf", double.PositiveInfinity, 0 },
            { "-Inf", double.NegativeInfinity, 0 },
            { "MaxInt32", int.MaxValue, int.MaxValue },
            { "MaxInt32+1", 2147483648.0, int.MinValue },
            { "MinInt32", int.MinValue, int.MinValue },
            { "MinInt32-1", -2147483649.0, int.MaxValue },
            { "MIN_SAFE_INTEGER", Number.MinSafeInteger.Value, 1 },
            { "MIN_SAFE_INTEGER-1", Number.MinSafeInteger.Value - 1, 0 },
            { "MIN_SAFE_INTEGER+1", Number.MinSafeInteger.Value + 1, 2 },
            { "MAX_SAFE_INTEGER", Number.MaxSafeInteger.Value, -1 },
            { "MAX_SAFE_INTEGER-1", Number.MaxSafeInteger.Value - 1, -2 },
            { "MAX_SAFE_INTEGER+1", Number.MaxSafeInteger.Value + 1, 0 },
            { "-8589934590", -8589934590.0, 2 },
            { "0xDEADBEEF", 0xDEADBEEF, -559038737 },
            { "4294967808", 4294967808.0, 512 },
            { "-0.4", -0.4, 0 },
            { "SmallestNonzeroFloat64", double.Epsilon, 0 },
            { "-SmallestNonzeroFloat64", -double.Epsilon, 0 },
            { "MaxFloat64", double.MaxValue, 0 },
            { "-MaxFloat64", -double.MaxValue, 0 },
            { "Largest subnormal number", NumberFromBits(0x000FFFFFFFFFFFFF).Value, 0 },
            { "Smallest positive normal number", NumberFromBits(0x0010000000000000).Value, 0 },
            { "Largest normal number", double.MaxValue, 0 },
            { "-Largest normal number", -double.MaxValue, 0 },
            { "1.0", 1.0, 1 },
            { "-1.0", -1.0, -1 },
            { "1e308", 1e308, 0 },
            { "-1e308", -1e308, 0 },
            { "math.Pi", Math.PI, 3 },
            { "-math.Pi", -Math.PI, -3 },
            { "math.E", Math.E, 2 },
            { "-math.E", -Math.E, -2 },
            { "0.5", 0.5, 0 },
            { "-0.5", -0.5, 0 },
            { "0.49999999999999994", 0.49999999999999994, 0 },
            { "-0.49999999999999994", -0.49999999999999994, 0 },
            { "0.5000000000000001", 0.5000000000000001, 0 },
            { "-0.5000000000000001", -0.5000000000000001, 0 },
            { "2^31 + 0.5", 2147483648.5, -2147483648 },
            { "-2^31 - 0.5", -2147483648.5, -2147483648 },
            { "2^40", 1099511627776.0, 0 },
            { "-2^40", -1099511627776.0, 0 },
            { "TypeFlagsNarrowable", 536624127, 536624127 },
        };
        return data;
    }

    // Go: TestToInt32
    [Theory]
    [MemberData(nameof(ToInt32Data))]
    public void ToInt32(string name, double input, int want)
    {
        int got = ((Number)input).ToInt32();
        Assert.True(got == want, FormattableString.Invariant($"{name}: got {got}, want {want}"));
    }

    public static TheoryData<double, double> BitwiseNOTData() => new()
    {
        // Original pairs: ~(-2147483649) == ~(2147483647)
        { -2147483649.0, -2147483648 },
        { 2147483647, -2147483648 },
        // Original pairs: ~(-4294967296) == ~(0)
        { -4294967296.0, -1 },
        { 0, -1 },
        // Original pairs: ~(2147483648) == ~(-2147483648)
        { 2147483648.0, 2147483647 },
        { -2147483648, 2147483647 },
        // Original pairs: ~(4294967296) == ~(0)
        { 4294967296.0, -1 },
    };

    // Go: TestBitwiseNOT
    [Theory]
    [MemberData(nameof(BitwiseNOTData))]
    public void BitwiseNOT(double x, double want)
    {
        AssertEqualNumber(((Number)x).BitwiseNOT(), want);
    }

    public static TheoryData<double, double, double> BitwiseANDData() => new()
    {
        { 0, 0, 0 },
        { 0, 1, 0 },
        { 1, 0, 0 },
        { 1, 1, 1 },
    };

    // Go: TestBitwiseAND
    [Theory]
    [MemberData(nameof(BitwiseANDData))]
    public void BitwiseAND(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).BitwiseAND(y), want);
    }

    public static TheoryData<double, double, double> BitwiseORData() => new()
    {
        { 0, 0, 0 },
        { 0, 1, 1 },
        { 1, 0, 1 },
        { 1, 1, 1 },
    };

    // Go: TestBitwiseOR
    [Theory]
    [MemberData(nameof(BitwiseORData))]
    public void BitwiseOR(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).BitwiseOR(y), want);
    }

    public static TheoryData<double, double, double> BitwiseXORData() => new()
    {
        { 0, 0, 0 },
        { 0, 1, 1 },
        { 1, 0, 1 },
        { 1, 1, 0 },
    };

    // Go: TestBitwiseXOR
    [Theory]
    [MemberData(nameof(BitwiseXORData))]
    public void BitwiseXOR(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).BitwiseXOR(y), want);
    }

    public static TheoryData<double, double, double> SignedRightShiftData() => new()
    {
        { 1, 0, 1 },
        { 1, 1, 0 },
        { 1, 2, 0 },
        { 1, 31, 0 },
        { 1, 32, 1 },
        { -4, 0, -4 },
        { -4, 1, -2 },
        { -4, 2, -1 },
        { -4, 3, -1 },
        { -4, 4, -1 },
        { -4, 31, -1 },
        { -4, 32, -4 },
        { -4, 33, -2 },
    };

    // Go: TestSignedRightShift
    [Theory]
    [MemberData(nameof(SignedRightShiftData))]
    public void SignedRightShift(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).SignedRightShift(y), want);
    }

    public static TheoryData<double, double, double> UnsignedRightShiftData() => new()
    {
        { 1, 0, 1 },
        { 1, 1, 0 },
        { 1, 2, 0 },
        { 1, 31, 0 },
        { 1, 32, 1 },
        { -4, 0, 4294967292.0 },
        { -4, 1, 2147483646 },
        { -4, 2, 1073741823 },
        { -4, 3, 536870911 },
        { -4, 4, 268435455 },
        { -4, 31, 1 },
        { -4, 32, 4294967292.0 },
        { -4, 33, 2147483646 },
    };

    // Go: TestUnsignedRightShift
    [Theory]
    [MemberData(nameof(UnsignedRightShiftData))]
    public void UnsignedRightShift(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).UnsignedRightShift(y), want);
    }

    public static TheoryData<double, double, double> LeftShiftData() => new()
    {
        { 1, 0, 1 },
        { 1, 1, 2 },
        { 1, 2, 4 },
        { 1, 31, -2147483648 },
        { 1, 32, 1 },
        { -4, 0, -4 },
        { -4, 1, -8 },
        { -4, 2, -16 },
        { -4, 3, -32 },
        { -4, 31, 0 },
        { -4, 32, -4 },
    };

    // Go: TestLeftShift
    [Theory]
    [MemberData(nameof(LeftShiftData))]
    public void LeftShift(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).LeftShift(y), want);
    }

    public static TheoryData<double, double, double> RemainderData() => new()
    {
        { double.NaN, 1, double.NaN },
        { 1, double.NaN, double.NaN },
        { double.PositiveInfinity, 1, double.NaN },
        { double.NegativeInfinity, 1, double.NaN },
        { 123, double.PositiveInfinity, 123 },
        { 123, double.NegativeInfinity, 123 },
        { 123, 0, double.NaN },
        { 123, Number.NegativeZero.Value, double.NaN },
        { 0, 123, 0 },
        { Number.NegativeZero.Value, 123, Number.NegativeZero.Value },
        // Normal cases
        { 10, 3, 1 },
        { -10, 3, -1 },
        { 10, -3, 1 },
        { -10, -3, -1 },
        { 5.5, 2, 1.5 },
        { -5.5, 2, -1.5 },
        { 1, 0.5, 0 },
        { -1, 0.5, Number.NegativeZero.Value },
        { 1.5, 1, 0.5 },
        { -1.5, 1, -0.5 },
        // Edge cases that prove the bug in the manual formula:
        // The manual formula n - d*(n/d).trunc() accumulates floating-point
        // rounding errors that IEEE 754 fmod (math.Mod) avoids.
        { 7, 0.1, 7.0 % 0.1 },
        { 7, 0.2, 7.0 % 0.2 },
        { 7, 0.3, 7.0 % 0.3 },
        { 100, 0.3, 100.0 % 0.3 },
    };

    // Go: TestRemainder
    [Theory]
    [MemberData(nameof(RemainderData))]
    public void Remainder(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).Remainder(y), want);
    }

    public static TheoryData<double, double, double> ExponentiateData() => new()
    {
        { 2, 3, 8 },
        { double.PositiveInfinity, 3, double.PositiveInfinity },
        { double.PositiveInfinity, -5, 0 },
        { double.NegativeInfinity, 3, double.NegativeInfinity },
        { double.NegativeInfinity, 4, double.PositiveInfinity },
        { double.NegativeInfinity, -3, Number.NegativeZero.Value },
        { double.NegativeInfinity, -4, 0 },
        { 0, 3, 0 },
        { 0, -10, double.PositiveInfinity },
        { Number.NegativeZero.Value, 3, Number.NegativeZero.Value },
        { Number.NegativeZero.Value, 4, 0 },
        { Number.NegativeZero.Value, -3, double.NegativeInfinity },
        { Number.NegativeZero.Value, -4, double.PositiveInfinity },
        { 3, double.PositiveInfinity, double.PositiveInfinity },
        { -3, double.PositiveInfinity, double.PositiveInfinity },
        { 3, double.NegativeInfinity, 0 },
        { -3, double.NegativeInfinity, 0 },
        { double.NaN, 3, double.NaN },
        { 1, double.PositiveInfinity, double.NaN },
        { 1, double.NegativeInfinity, double.NaN },
        { -1, double.PositiveInfinity, double.NaN },
        { -1, double.NegativeInfinity, double.NaN },
        { 1, double.NaN, double.NaN },
        // Cases where math.Pow diverges from V8 by >1 ULP.
        // Expected values are the correctly-rounded IEEE 754 results
        // computed via exact integer arithmetic (big.Int).
        // Cross-engine testing (V8, SpiderMonkey, QuickJS, XS via jsvu)
        // confirmed these match the majority of JS engines.
        { 10, 308, NumberFromBits(0x7fe1ccf385ebc8a0).Value },
        { 5, 210, NumberFromBits(0x5e68557f31326bbb).Value },
        { 10, 200, NumberFromBits(0x6974e718d7d7625a).Value },
    };

    // Go: TestExponentiate
    [Theory]
    [MemberData(nameof(ExponentiateData))]
    public void Exponentiate(double x, double y, double want)
    {
        AssertEqualNumber(((Number)x).Exponentiate(y), want);
    }
}
