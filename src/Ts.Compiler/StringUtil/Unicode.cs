using System;

namespace Ts.StringUtil;

// Hand-written range-table query layer over the transcribed Unicode tables
// (plan/04-codegen.md §6.2: generated files carry data only; the lookup lives here).
//
// Go: GOROOT src/unicode/letter.go Is/is16/is32, specialized to the flattened
// {lo, hi, stride} triple spans emitted by tools/generator/transcribe-unicode.ts.
// The tables' LatinOffset is intentionally unused: Go's unicode.Is — the only
// accessor stringutil uses — never consults it (only the unexported
// isExcludingLatin does).
internal static class Unicode
{
    // Go: unicode.linearMax — maximum table size (in ranges) for linear search of a
    // non-Latin1 rune.
    private const int LinearMax = 18;

    // Go: unicode.MaxLatin1.
    private const int MaxLatin1 = 0xFF;

    // Go: unicode.Is(rangeTab, r) with the two range slices passed explicitly.
    internal static bool Is(ReadOnlySpan<ushort> r16, ReadOnlySpan<uint> r32, int r)
    {
        // Compare as uint to correctly handle negative runes (Go: uint32(r) compare).
        if (r16.Length > 0 && (uint)r <= r16[^2])
        {
            return Is16(r16, (ushort)r);
        }
        if (r32.Length > 0 && r >= (int)r32[0])
        {
            return Is32(r32, (uint)r);
        }
        return false;
    }

    // Go: unicode.is16. `ranges` holds {lo, hi, stride} triples flattened, so range i
    // occupies elements [3*i, 3*i+2] and the range count is ranges.Length / 3.
    private static bool Is16(ReadOnlySpan<ushort> ranges, ushort r)
    {
        int count = ranges.Length / 3;
        if (count <= LinearMax || r <= MaxLatin1)
        {
            for (int i = 0; i < count; i++)
            {
                ushort lo = ranges[3 * i];
                if (r < lo)
                {
                    return false;
                }
                ushort hi = ranges[3 * i + 1];
                if (r <= hi)
                {
                    ushort stride = ranges[3 * i + 2];
                    return stride == 1 || (r - lo) % stride == 0;
                }
            }
            return false;
        }

        // binary search over ranges
        int loIdx = 0;
        int hiIdx = count;
        while (loIdx < hiIdx)
        {
            int m = (int)((uint)(loIdx + hiIdx) >> 1);
            ushort lo = ranges[3 * m];
            ushort hi = ranges[3 * m + 1];
            if (lo <= r && r <= hi)
            {
                ushort stride = ranges[3 * m + 2];
                return stride == 1 || (r - lo) % stride == 0;
            }
            if (r < lo)
            {
                hiIdx = m;
            }
            else
            {
                loIdx = m + 1;
            }
        }
        return false;
    }

    // Go: unicode.is32 (flattened triples, as above).
    private static bool Is32(ReadOnlySpan<uint> ranges, uint r)
    {
        int count = ranges.Length / 3;
        if (count <= LinearMax)
        {
            for (int i = 0; i < count; i++)
            {
                uint lo = ranges[3 * i];
                if (r < lo)
                {
                    return false;
                }
                uint hi = ranges[3 * i + 1];
                if (r <= hi)
                {
                    uint stride = ranges[3 * i + 2];
                    return stride == 1 || (r - lo) % stride == 0;
                }
            }
            return false;
        }

        // binary search over ranges
        int loIdx = 0;
        int hiIdx = count;
        while (loIdx < hiIdx)
        {
            int m = (int)((uint)(loIdx + hiIdx) >> 1);
            uint lo = ranges[3 * m];
            uint hi = ranges[3 * m + 1];
            if (lo <= r && r <= hi)
            {
                uint stride = ranges[3 * m + 2];
                return stride == 1 || (r - lo) % stride == 0;
            }
            if (r < lo)
            {
                hiIdx = m;
            }
            else
            {
                loIdx = m + 1;
            }
        }
        return false;
    }
}
