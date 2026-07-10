// Ported from tsgo internal/jsnum/pseudobigint.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Globalization;
using System.Numerics;

namespace Ts.JsNum;

// PseudoBigInt represents a JS-like bigint. The zero state of the struct represents the value 0.
//
// PORT-NOTE: Go's zero-value string is ""; C# default(PseudoBigInt) holds a null
// field, so Base10Value normalizes null → "" and Equals/GetHashCode go through
// the property to keep default(PseudoBigInt) equal to an explicitly-built zero.
public readonly record struct PseudoBigInt
{
    private readonly string? base10Value;

    // true if the value is a non-zero negative number.
    public bool Negative { get; init; }

    // The absolute value in base 10 with no leading zeros. The value zero is represented as an empty string.
    public string Base10Value
    {
        get => base10Value ?? "";
        init => base10Value = value;
    }

    // Go: func NewPseudoBigInt(value string, negative bool) PseudoBigInt
    public PseudoBigInt(string value, bool negative)
    {
        value = value.TrimStart('0');
        Negative = negative && value.Length != 0;
        base10Value = value;
    }

    public bool Equals(PseudoBigInt other) =>
        Negative == other.Negative && Base10Value == other.Base10Value;

    public override int GetHashCode() => HashCode.Combine(Negative, Base10Value);

    // Go: func (value PseudoBigInt) String() string
    public override string ToString()
    {
        if (Base10Value.Length == 0)
        {
            return "0";
        }
        if (Negative)
        {
            return "-" + Base10Value;
        }
        return Base10Value;
    }

    public int Sign()
    {
        if (Base10Value.Length == 0)
        {
            return 0;
        }
        if (Negative)
        {
            return -1;
        }
        return 1;
    }
}

public static partial class JsNum
{
    // Go: func ParseValidBigInt(text string) PseudoBigInt
    public static PseudoBigInt ParseValidBigInt(string text)
    {
        bool negative = text.StartsWith('-');
        if (negative)
        {
            text = text[1..];
        }
        return new PseudoBigInt(ParsePseudoBigInt(text), negative);
    }

    // Go: func ParsePseudoBigInt(stringValue string) string
    public static string ParsePseudoBigInt(string stringValue)
    {
        if (stringValue.EndsWith('n'))
        {
            stringValue = stringValue[..^1];
        }
        char b1 = stringValue.Length > 1 ? stringValue[1] : '\0';
        switch (b1)
        {
            case 'b':
            case 'B':
            case 'o':
            case 'O':
            case 'x':
            case 'X':
                // Not decimal.
                break;
            default:
                stringValue = stringValue.TrimStart('0');
                if (stringValue.Length == 0)
                {
                    return "0";
                }
                return stringValue;
        }

        // Go: new(big.Int).SetString(stringValue, 0) — base inferred from the
        // 0b/0o/0x prefix, underscores permitted between digits. Inputs are
        // scanner-validated bigint literals; anything unparsable panics in Go
        // and throws here.
        int radix = b1 switch
        {
            'b' or 'B' => 2,
            'o' or 'O' => 8,
            _ => 16,
        };
        BigInteger value = 0;
        int digitCount = 0;
        for (int i = 2; i < stringValue.Length; i++)
        {
            char c = stringValue[i];
            if (c == '_')
            {
                continue;
            }
            bool valid = radix switch
            {
                2 => c is '0' or '1',
                8 => c is >= '0' and <= '7',
                _ => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'),
            };
            if (!valid)
            {
                throw new InvalidOperationException($"Failed to parse big int: \"{stringValue}\"");
            }
            value = value * radix + DigitValue(c);
            digitCount++;
        }
        if (digitCount == 0)
        {
            throw new InvalidOperationException($"Failed to parse big int: \"{stringValue}\"");
        }
        return value.ToString(CultureInfo.InvariantCulture); // !!!
    }
}
