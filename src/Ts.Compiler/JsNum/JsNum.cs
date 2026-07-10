// Ported from tsgo internal/jsnum/jsnum.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Package jsnum provides JS-like number handling.

using System;
using System.Numerics;

namespace Ts.JsNum;

// Number represents a JS-like number.
//
// All operations that can be performed directly on this type
// (e.g., conversion, arithmetic, etc.) behave as they would in JavaScript,
// but any other operation should use this type's methods,
// not the "math" package and conversions.
//
// PORT-NOTE (plan/05 §9.1): Go `type Number float64` → readonly record struct
// wrapping double, with operator overloads. The record-synthesized
// `==(Number, Number)` has Equals semantics (NaN == NaN is true; +0 and -0 are
// distinct) — NOT IEEE semantics like Go's `==` on float64. The mixed
// `==(Number, double)` overloads below ARE IEEE, so literal comparisons such
// as `n == 0` behave like Go. For IEEE equality of two Numbers, compare
// `a.Value == b.Value`.
public readonly partial record struct Number(double Value)
{
    // Go: MaxSafeInteger Number = 1<<53 - 1; MinSafeInteger = -MaxSafeInteger
    public static readonly Number MaxSafeInteger = 9007199254740991.0;
    public static readonly Number MinSafeInteger = -9007199254740991.0;

    public static implicit operator Number(double value) => new(value);

    // Go float64(n) is an explicit conversion; keep it explicit here too.
    public static explicit operator double(Number n) => n.Value;

    public static Number operator +(Number x, Number y) => x.Value + y.Value;

    public static Number operator -(Number x, Number y) => x.Value - y.Value;

    public static Number operator *(Number x, Number y) => x.Value * y.Value;

    public static Number operator /(Number x, Number y) => x.Value / y.Value;

    public static Number operator -(Number x) => -x.Value;

    public static bool operator <(Number x, Number y) => x.Value < y.Value;

    public static bool operator <=(Number x, Number y) => x.Value <= y.Value;

    public static bool operator >(Number x, Number y) => x.Value > y.Value;

    public static bool operator >=(Number x, Number y) => x.Value >= y.Value;

    // IEEE equality against double (see PORT-NOTE above). Overload resolution
    // prefers these over the synthesized ==(Number, Number) for `n == 0` etc.
    public static bool operator ==(Number x, double y) => x.Value == y;

    public static bool operator !=(Number x, double y) => x.Value != y;

    public static bool operator ==(double x, Number y) => x == y.Value;

    public static bool operator !=(double x, Number y) => x != y.Value;

    // Go: func NaN() Number
    public static Number NaN => new(double.NaN);

    public bool IsNaN() => double.IsNaN(Value);

    // Go: func Inf(sign int) Number — sign >= 0 yields +Inf (math.Inf contract).
    public static Number Inf(int sign) => new(sign >= 0 ? double.PositiveInfinity : double.NegativeInfinity);

    public bool IsInf() => double.IsInfinity(Value);

    private static bool IsNonFinite(double x)
    {
        // This is equivalent to checking `math.IsNaN(x) || math.IsInf(x, 0)` in one operation.
        const ulong mask = 0x7FF0000000000000;
        return (BitConverter.DoubleToUInt64Bits(x) & mask) == mask;
    }

    // https://tc39.es/ecma262/2024/multipage/abstract-operations.html#sec-touint32
    internal uint ToUint32()
    {
        // The only difference between ToUint32 and ToInt32 is the interpretation of the bits.
        return unchecked((uint)ToInt32());
    }

    // https://tc39.es/ecma262/2024/multipage/abstract-operations.html#sec-toint32
    internal int ToInt32()
    {
        double x = Value;

        // Fast path: if the number is in the range (-2^31, 2^32), i.e. an SMI,
        // then we don't need to do any special mapping.
        // (Out-of-range double→int conversion saturates on .NET Core 3.0+ and NaN
        // yields 0; like Go's implementation-defined conversion, the round-trip
        // equality below filters every such case.)
        int smi = unchecked((int)x);
        if (smi == x)
        {
            return smi;
        }

        // 2. If number is not finite or number is either +0𝔽 or -0𝔽, return +0𝔽.
        // Zero was covered by the test above.
        if (IsNonFinite(x))
        {
            return 0;
        }

        // Let int be truncate(ℝ(number)).
        x = Math.Truncate(x);
        // Let int32bit be int modulo 2**32.
        x %= 4294967296.0; // C# % on doubles is fmod, same as Go math.Mod (sign of dividend).
        // If int32bit ≥ 2**31, return 𝔽(int32bit - 2**32); otherwise return 𝔽(int32bit).
        return unchecked((int)(long)x);
    }

    internal uint ToShiftCount()
    {
        return ToUint32() & 31;
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-signedRightShift
    public Number SignedRightShift(Number y)
    {
        return ToInt32() >> (int)y.ToShiftCount();
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-unsignedRightShift
    public Number UnsignedRightShift(Number y)
    {
        return ToUint32() >> (int)y.ToShiftCount();
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-leftShift
    public Number LeftShift(Number y)
    {
        return unchecked(ToInt32() << (int)y.ToShiftCount());
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-bitwiseNOT
    public Number BitwiseNOT()
    {
        return ~ToInt32();
    }

    // The below are implemented by https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numberbitwiseop.

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-bitwiseOR
    public Number BitwiseOR(Number y)
    {
        return ToInt32() | y.ToInt32();
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-bitwiseAND
    public Number BitwiseAND(Number y)
    {
        return ToInt32() & y.ToInt32();
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-bitwiseXOR
    public Number BitwiseXOR(Number y)
    {
        return ToInt32() ^ y.ToInt32();
    }

    internal Number Trunc()
    {
        return Math.Truncate(Value);
    }

    public Number Floor()
    {
        return Math.Floor(Value);
    }

    public Number Abs()
    {
        return Math.Abs(Value);
    }

    // Go: package-level `var negativeZero = Number(math.Copysign(0, -1))`
    // (kept inside the struct to preserve jsnum.go source order).
    internal static readonly Number NegativeZero = Math.CopySign(0, -1);

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-remainder
    public Number Remainder(Number d)
    {
        Number n = this;
        if (n.IsNaN() || d.IsNaN())
        {
            return NaN;
        }
        if (n.IsInf())
        {
            return NaN;
        }
        if (d.IsInf())
        {
            return n;
        }
        if (d.Value == 0)
        {
            return NaN;
        }
        if (n.Value == 0)
        {
            return n;
        }
        // C# double remainder (%) is IEEE 754 fmod, identical to Go math.Mod.
        return n.Value % d.Value;
    }

    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-exponentiate
    public Number Exponentiate(Number exponent)
    {
        if ((Value == 1 || Value == -1) && exponent.IsInf())
        {
            return NaN;
        }
        if (Value == 1 && exponent.IsNaN())
        {
            return NaN;
        }

        double b = Value;
        double e = exponent.Value;

        // For integer base ** integer exponent where the result exceeds 53 bits,
        // math.Pow can be off by multiple ULPs vs JS engines. Use exact big.Int
        // arithmetic and IEEE 754 round-to-nearest-even conversion instead.
        // The ES spec (§6.1.6.1.3) says exponentiate returns an
        // "implementation-approximated" value, so engines are allowed to differ.
        // This won't exactly match every engine (V8's fdlibm-compiled pow can
        // round halfway ties differently), but will always be within 1 ULP
        // (unit in the last place, i.e. the least significant bit of the result).
        //
        // PORT-NOTE: Go compares against float64(math.MaxInt64), which rounds up
        // to 2^63; the constants below reproduce that. For b exactly 2^63 the
        // Go int64(b) conversion is implementation-defined (amd64 gives
        // math.MinInt64, flipping the sign for odd e); C#'s saturating (long)b
        // gives long.MaxValue instead, which stays within the 1 ULP contract.
        const double minInt64 = -9223372036854775808.0; // float64(math.MinInt64), exact
        const double maxInt64 = 9223372036854775807.0;  // float64(math.MaxInt64) == 2^63
        if (b >= minInt64 && b <= maxInt64 && b == Math.Truncate(b) &&
            e >= 0 && e <= maxInt64 && e == Math.Truncate(e) && !double.IsInfinity(e))
        {
            double magnitude = e * Math.Log2(Math.Abs(b));
            if (magnitude > 53 && magnitude <= Math.Log2(double.MaxValue))
            {
                // Reaching here requires |b| >= 2, so e <= 1024 and the (int) cast is safe.
                BigInteger ri = BigInteger.Pow(new BigInteger((long)b), (int)e);
                return JsNum.BigIntToDouble(ri);
            }
        }

        return Math.Pow(b, e);
    }
}

public static partial class JsNum
{
    // PORT-NOTE: shared replacement for Go's two exact-integer→float64 conversions:
    // big.Int.Float64 (string.go:169) and big.Float.SetPrec(256).SetInt(...).Float64
    // (jsnum.go:170-171). Implements a single IEEE 754 round-to-nearest-even
    // rounding from the exact integer — at least as accurate as Go's 256-bit
    // intermediate (which double-rounds 256→53 bits).
    internal static double BigIntToDouble(BigInteger value)
    {
        if (value.Sign < 0)
        {
            return -BigIntToDouble(-value);
        }

        long bitLength = value.GetBitLength();
        if (bitLength <= 53)
        {
            return (double)value; // exact, no rounding involved
        }

        // Keep the top 54 bits (53-bit mantissa + round bit); OR everything below
        // into a sticky bit, then round to nearest, ties to even.
        int shift = (int)(bitLength - 54);
        BigInteger top = value >> shift;
        bool sticky = (value & ((BigInteger.One << shift) - 1)) != 0;
        ulong mantissa54 = (ulong)top;
        ulong mantissa = mantissa54 >> 1;
        bool round = (mantissa54 & 1) != 0;
        if (round && (sticky || (mantissa & 1) != 0))
        {
            mantissa++; // may become 2^53; still exactly representable
        }
        // value ≈ mantissa * 2^(shift+1); ScaleB is exact and overflows to +Inf,
        // matching big.Float.Float64's +Inf on overflow.
        return Math.ScaleB(mantissa, shift + 1);
    }
}
