// Ported from tsgo internal/jsnum/string.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// PORT-NOTE: file name — plan/06 §2.8 lists string.go → NumberToString.cs; this port
// was dispatched as NumberString.cs (the file holds both directions: Number.String()
// and FromString). Registered as a naming deviation in the session report.

using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ts.JsNum;

public readonly partial record struct Number
{
    // https://tc39.es/ecma262/2024/multipage/ecmascript-data-types-and-values.html#sec-numeric-types-number-tostring
    // Go: func (n Number) String() string
    public override string ToString()
    {
        if (IsNaN())
        {
            return "NaN";
        }
        if (IsInf())
        {
            return Value < 0 ? "-Infinity" : "Infinity";
        }

        // Fast path: for safe integers, directly convert to string.
        if (MinSafeInteger.Value <= Value && Value <= MaxSafeInteger.Value)
        {
            long i = (long)Value;
            if (i == Value)
            {
                return i.ToString(CultureInfo.InvariantCulture);
            }
        }

        // Otherwise, the Go json package handles this correctly.
        // PORT-NOTE (R05 adjudication): Go delegates to internal/json
        // (go-json-experiment jsonwire.AppendFloat) = Ryū shortest round-trip
        // digits + ES6 layout. .NET Core 3.0+ double.ToString() already emits
        // shortest round-trip digits, so we reuse the BCL digits and reimplement
        // only the ES6 layout (ECMA-262 §6.1.6.1.20 Number::toString, radix 10):
        // decimal notation for 1e-6 <= |x| < 1e21, exponent notation otherwise,
        // "e+"/"e-" with no zero padding. -0 never reaches here (fast path).
        return FormatDoubleEs6(Value);
    }

    private static string FormatDoubleEs6(double x)
    {
        bool negative = double.IsNegative(x);
        if (negative)
        {
            x = -x;
        }

        // Split the BCL shortest round-trip representation into a digit string
        // and a decimal exponent n so that x = 0.digits × 10^n (the spec's
        // s × 10^(n−k) with k = digit count).
        string repr = x.ToString(CultureInfo.InvariantCulture);

        // PORT-NOTE (R05): .NET's "shortest round-trippable" formatter drops a
        // digit for some values at power-of-two boundaries — e.g. 2^-25
        // (0x3E60000000000000) prints as "2.980232238769531E-08", which parses
        // to the neighboring double 0x3E5FFFFFFFFFFFFF. Go's Ryū (strconv)
        // correctly emits 17 digits ("2.9802322387695312e-8", pinned by
        // ryu_test.go). Repair: verify the round trip (.NET's parser is
        // correctly rounded) and on failure take the smallest fixed precision
        // whose correctly-rounded digits round-trip. .NET fixed-precision
        // formatting rounds decimal ties to even, matching Ryū's behavior on
        // exact-tie digits (verified for the 2^-25 vector).
        if (double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture) != x)
        {
            for (int precision = 1; precision <= 17; precision++)
            {
                string candidate = x.ToString(
                    "E" + (precision - 1).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);
                if (double.Parse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture) == x)
                {
                    repr = candidate;
                    break;
                }
            }
        }
        string digits;
        int n;
        int eIndex = repr.IndexOf('E');
        if (eIndex >= 0)
        {
            // "d.dddddE±XX" — exactly one digit before the decimal point.
            int exp10 = int.Parse(repr.AsSpan(eIndex + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            string mantissa = repr[..eIndex];
            int dot = mantissa.IndexOf('.');
            digits = dot >= 0 ? mantissa.Remove(dot, 1) : mantissa;
            n = exp10 + 1;
        }
        else
        {
            int dot = repr.IndexOf('.');
            if (dot >= 0)
            {
                digits = repr.Remove(dot, 1);
                n = dot;
            }
            else
            {
                digits = repr;
                n = repr.Length;
            }
        }

        // Normalize: x is finite, nonzero, positive here, so digits has at least
        // one nonzero digit. Leading zeros shift n; trailing zeros do not.
        int start = 0;
        while (digits[start] == '0')
        {
            start++;
            n--;
        }
        int end = digits.Length;
        while (digits[end - 1] == '0')
        {
            end--;
        }
        digits = digits[start..end];
        int k = digits.Length;

        // ECMA-262 §6.1.6.1.20 steps 5–10.
        string result;
        if (k <= n && n <= 21)
        {
            result = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            result = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            result = "0." + new string('0', -n) + digits;
        }
        else
        {
            int e = n - 1;
            string exponent = (e >= 0 ? "e+" : "e-") + Math.Abs(e).ToString(CultureInfo.InvariantCulture);
            result = k == 1 ? digits + exponent : digits[..1] + "." + digits[1..] + exponent;
        }
        return negative ? "-" + result : result;
    }
}

public static partial class JsNum
{
    // https://tc39.es/ecma262/2024/multipage/abstract-operations.html#sec-stringtonumber
    public static Number FromString(string s)
    {
        // Implementing StringToNumber exactly as written in the spec involves
        // writing a parser, along with the conversion of the parsed AST into the
        // actual value.
        //
        // We've already implemented a number parser in the scanner, but we can't
        // import it here. We also do not have the conversion implemented since we
        // previously just wrote `+literal` and let the runtime handle it.
        //
        // The strategy below is to instead break the number apart and fix it up
        // such that Go's own parsing functionality can handle it. This won't be
        // the fastest method, but it saves us from writing the full parser and
        // conversion logic.

        s = TrimStrWhiteSpace(s);

        switch (s)
        {
            case "":
                return 0;
            case "Infinity":
            case "+Infinity":
                return Number.Inf(1);
            case "-Infinity":
                return Number.Inf(-1);
        }

        foreach (System.Text.Rune r in s.EnumerateRunes())
        {
            if (!IsNumberRune(r.Value))
            {
                return Number.NaN;
            }
        }

        if (TryParseInt(s, out Number n))
        {
            return n;
        }

        // Cut this off first so we can ensure -0 is returned as -0.
        bool negative = s.StartsWith('-');
        if (negative)
        {
            s = s[1..];
        }
        else if (s.StartsWith('+'))
        {
            s = s[1..];
        }

        // All remaining runes passed IsNumberRune, so the first one is ASCII.
        if (s.Length == 0 || (!IsDigit(s[0]) && s[0] != '.'))
        {
            return Number.NaN;
        }

        double f = ParseFloatString(s);
        if (double.IsNaN(f))
        {
            return Number.NaN;
        }

        double sign = negative ? -1.0 : 1.0;
        return Math.CopySign(f, sign);
    }

    // Go: strings.TrimFunc(s, isStrWhiteSpace). Every StrWhiteSpace code point is
    // in the BMP (Zs has no astral members) and surrogate halves are never
    // whitespace, so a char-wise trim is equivalent to Go's rune-wise trim.
    private static string TrimStrWhiteSpace(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && IsStrWhiteSpace(s[start]))
        {
            start++;
        }
        while (end > start && IsStrWhiteSpace(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }

    private static bool IsStrWhiteSpace(char r)
    {
        // This is different than stringutil.IsWhiteSpaceLike.

        // https://tc39.es/ecma262/2024/multipage/ecmascript-language-lexical-grammar.html#prod-LineTerminator
        // https://tc39.es/ecma262/2024/multipage/ecmascript-language-lexical-grammar.html#prod-WhiteSpace

        switch (r)
        {
            // LineTerminator
            case '\n':
            case '\r':
            case '\u2028':
            case '\u2029':
            // WhiteSpace
            case '\t':
            case '\v':
            case '\f':
            case '\uFEFF':
                return true;
        }

        // WhiteSpace — Go unicode.Is(unicode.Zs, r)
        return CharUnicodeInfo.GetUnicodeCategory(r) == UnicodeCategory.SpaceSeparator;
    }

    // Go: tryParseInt(s string) (Number, bool). A false return means "not an
    // integer literal, continue with the float path"; a true return may carry NaN.
    private static bool TryParseInt(string s, out Number result)
    {
        if (s.Length > 2 && s[0] == '0')
        {
            int radix = s[1] switch
            {
                'b' or 'B' => 2,
                'o' or 'O' => 8,
                'x' or 'X' => 16,
                _ => 0,
            };
            if (radix != 0)
            {
                string rest = s[2..];
                bool valid = radix switch
                {
                    2 => IsAllBinaryDigits(rest),
                    8 => IsAllOctalDigits(rest),
                    _ => IsAllHexDigits(rest),
                };
                if (!valid)
                {
                    result = Number.NaN;
                    return true;
                }
                result = ParseIntegerDigits(rest, radix);
                return true;
            }
        }

        // StringToNumber does not parse leading zeros as octal.
        string t = TrimLeadingZeros(s);
        if (!IsAllDigits(t))
        {
            result = 0;
            return false;
        }
        result = ParseIntegerDigits(t, 10);
        return true;
    }

    // Go: strconv.ParseInt fast path + big.Int fallback ("Using this to parse
    // large integers."), collapsed: an exact ulong accumulate, falling back to
    // exact BigInteger + round-to-nearest on overflow. Both paths round the
    // exact integer to the nearest double, identical to Go's two-tier result.
    private static Number ParseIntegerDigits(string digits, int radix)
    {
        ulong acc = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            ulong d = (ulong)DigitValue(digits[i]);
            if (acc > (ulong.MaxValue - d) / (ulong)radix)
            {
                BigInteger big = 0;
                for (int j = 0; j < digits.Length; j++)
                {
                    big = big * radix + DigitValue(digits[j]);
                }
                return BigIntToDouble(big);
            }
            acc = acc * (ulong)radix + d;
        }
        return (double)acc;
    }

    // Digit value for bases up to 16; callers pre-validate with IsAll*Digits.
    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => c - 'A' + 10,
    };

    private static double ParseFloatString(string s)
    {
        bool hasDot;
        bool hasExp;

        // <a>
        // <a>.<b>
        // <a>.<b>e<c>
        // <a>e<c>
        string a;
        string b = "";
        string c;
        string rest;

        (a, rest, hasDot) = Cut(s, '.');
        if (hasDot)
        {
            // <a>.<b>
            // <a>.<b>e<c>
            (b, c, hasExp) = CutAny(rest, "eE");
        }
        else
        {
            // <a>
            // <a>e<c>
            (a, c, hasExp) = CutAny(s, "eE");
        }

        var sb = new StringBuilder(a.Length + b.Length + c.Length + 3);

        if (a.Length == 0)
        {
            if (hasDot && b.Length == 0)
            {
                return double.NaN;
            }
            if (hasExp && c.Length == 0)
            {
                return double.NaN;
            }
            sb.Append('0');
        }
        else
        {
            a = TrimLeadingZeros(a);
            if (!IsAllDigits(a))
            {
                return double.NaN;
            }
            sb.Append(a);
        }

        if (hasDot)
        {
            sb.Append('.');
            if (b.Length == 0)
            {
                sb.Append('0');
            }
            else
            {
                b = TrimTrailingZeros(b);
                if (!IsAllDigits(b))
                {
                    return double.NaN;
                }
                sb.Append(b);
            }
        }

        if (hasExp)
        {
            sb.Append('e');

            bool negative = c.StartsWith('-');
            if (negative)
            {
                c = c[1..];
                sb.Append('-');
            }
            else if (c.StartsWith('+'))
            {
                c = c[1..];
            }
            c = TrimLeadingZeros(c);
            if (!IsAllDigits(c))
            {
                return double.NaN;
            }
            sb.Append(c);
        }

        return StringToFloat64(sb.ToString());
    }

    private static (string Before, string After, bool Found) Cut(string s, char sep)
    {
        int i = s.IndexOf(sep);
        if (i >= 0)
        {
            return (s[..i], s[(i + 1)..], true);
        }
        return (s, "", false);
    }

    // Go cutAny decodes the separator rune to skip it; the only cutset used here
    // is "eE" (ASCII, one UTF-16 unit), so skipping a single char is equivalent.
    private static (string Before, string After, bool Found) CutAny(string s, string cutset)
    {
        int i = s.AsSpan().IndexOfAny(cutset);
        if (i >= 0)
        {
            return (s[..i], s[(i + 1)..], true);
        }
        return (s, "", false);
    }

    private static string TrimLeadingZeros(string s)
    {
        if (s.StartsWith('0'))
        {
            s = s.TrimStart('0');
            if (s.Length == 0)
            {
                return "0";
            }
        }
        return s;
    }

    private static string TrimTrailingZeros(string s)
    {
        if (s.EndsWith('0'))
        {
            s = s.TrimEnd('0');
            if (s.Length == 0)
            {
                return "0";
            }
        }
        return s;
    }

    // Go: strconv.ParseFloat(s, 64); on ErrRange Go still returns the clamped
    // value (±Inf on overflow, 0/subnormal on underflow). .NET Core 3.0+
    // double.TryParse does exactly that (IEEE 754:2008), so no special-casing.
    private static double StringToFloat64(string s)
    {
        if (double.TryParse(s, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out double f))
        {
            return f;
        }
        return double.NaN;
    }

    private static bool IsAllDigits(string s)
    {
        foreach (char r in s)
        {
            if (!IsDigit(r))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAllBinaryDigits(string s)
    {
        foreach (char r in s)
        {
            if (r != '0' && r != '1')
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAllOctalDigits(string s)
    {
        foreach (char r in s)
        {
            if (!IsOctalDigit(r))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAllHexDigits(string s)
    {
        foreach (char r in s)
        {
            if (!IsHexDigit(r))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsNumberRune(int r)
    {
        if (IsDigit(r))
        {
            return true;
        }

        if (r is >= 'a' and <= 'f')
        {
            return true;
        }

        if (r is >= 'A' and <= 'F')
        {
            return true;
        }

        switch (r)
        {
            case '.':
            case '-':
            case '+':
            case 'x':
            case 'X':
            case 'o':
            case 'O':
                return true;
        }

        return false;
    }

    // PORT-NOTE: private mirrors of internal/stringutil predicates
    // (util.go:71-81). Ts.StringUtil is a separate phase-1 work item and must
    // not be touched from this port; fold these into it once it lands.
    private static bool IsDigit(int ch) => ch is >= '0' and <= '9';

    private static bool IsOctalDigit(int ch) => ch is >= '0' and <= '7';

    private static bool IsHexDigit(int ch) => ch is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');
}
