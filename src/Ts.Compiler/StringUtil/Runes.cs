// Port infrastructure for Ts.StringUtil (not a 1:1 file port).
// Contents and their charters:
//   - DecodeRuneAt        — 05-conventions.md §8.1: the unified helper replacing Go's
//                           utf8.DecodeRuneInString in the UTF-16 world (lone surrogate
//                           → (U+FFFD, size 1); end of string → (U+FFFD, size 0), which
//                           mirrors Go's ("", 0) exhaustion signal).
//   - CompareRuneOrdinal  — 05-conventions.md §8.4: code-point-order comparison equal to
//                           Go's string byte order (UTF-8), used wherever an output-bearing
//                           sort may contain non-BMP characters. Returns -1/0/+1.
//   - EqualFold/SimpleFold — mirrors of Go stdlib strings.EqualFold and unicode.SimpleFold
//                           (Go 1.26.4, Unicode 15.0.0), required by the 1:1 port of
//                           internal/stringutil/compare.go. 05 §8.4: strings.EqualFold is
//                           NOT OrdinalIgnoreCase; the rune algorithm is ported instead.
//   - MapRunes            — mirror of Go stdlib strings.Map (used by strings.ToLower and
//                           tspath.ToFileNameLowerCase); writes runes via AppendRune.
//   - AppendRune          — 05-conventions.md §8.3 StringBuilder rune append helper.

using System;
using System.Text;

namespace Ts.StringUtil;

public static partial class StringUtil
{
    // Go: utf8.DecodeRuneInString(s[pos:]) in the UTF-16 world (05 §8.1). Size is in
    // UTF-16 code units (1 or 2); invalid input (lone surrogate) yields (U+FFFD, 1),
    // exhaustion yields (U+FFFD, 0) — same shape as Go's (RuneError, 0).
    public static (int Rune, int Size) DecodeRuneAt(string s, int pos)
    {
        if (pos >= s.Length)
        {
            return (0xFFFD, 0);
        }
        char c = s[pos];
        if (!char.IsSurrogate(c))
        {
            return (c, 1);
        }
        if (char.IsHighSurrogate(c) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
        {
            return (char.ConvertToUtf32(c, s[pos + 1]), 2);
        }
        return (0xFFFD, 1);
    }

    // 05 §8.4: Go string comparison is UTF-8 byte order, i.e. code-point order; C#'s
    // string.CompareOrdinal is UTF-16 unit order, which disagrees when characters in
    // [U+E000, U+FFFF] meet supplementary-plane characters. This helper compares by
    // code point (= Go semantics) and returns strictly -1/0/+1.
    public static Comparison CompareRuneOrdinal(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            char c1 = a[i];
            char c2 = b[i];
            if (c1 == c2)
            {
                continue;
            }
            if (c1 >= 0xD800 && c2 >= 0xD800)
            {
                // Both sides are surrogate-or-above at the first difference: decode and
                // compare full code points (lone surrogates decode to U+FFFD, matching
                // DecodeRuneAt; ties fall back to unit order for determinism).
                var (r1, _) = DecodeRuneAt(a, i);
                var (r2, _) = DecodeRuneAt(b, i);
                if (r1 != r2)
                {
                    return r1 < r2 ? -1 : 1;
                }
                return c1 < c2 ? -1 : 1;
            }
            // Otherwise UTF-16 unit order equals code-point order.
            return c1 < c2 ? -1 : 1;
        }
        if (a.Length == b.Length)
        {
            return 0;
        }
        return a.Length < b.Length ? -1 : 1;
    }

    // Mirror of Go strings.EqualFold (simple Unicode case folding, no normalization).
    // Positions are UTF-16 code units (05 §8.1 world shift).
    public static bool EqualFold(string s, string t)
    {
        // ASCII fast path.
        int i = 0;
        for (; i < s.Length && i < t.Length; i++)
        {
            char sr = s[i];
            char tr = t[i];
            if (sr >= 0x80 || tr >= 0x80)
            {
                goto hasUnicode;
            }
            // Easy case.
            if (tr == sr)
            {
                continue;
            }
            // Make sr < tr to simplify what follows.
            if (tr < sr)
            {
                (tr, sr) = (sr, tr);
            }
            // ASCII only, sr/tr must be upper/lower case.
            if ('A' <= sr && sr <= 'Z' && tr == sr + ('a' - 'A'))
            {
                continue;
            }
            return false;
        }
        // Check if we've exhausted both strings.
        return s.Length == t.Length;

    hasUnicode:
        int si = i;
        int ti = i;
        while (si < s.Length)
        {
            var (sr, ssize) = DecodeRuneAt(s, si);
            si += ssize;
            // If t is exhausted the strings are not equal.
            if (ti >= t.Length)
            {
                return false;
            }
            // Extract first rune from second string.
            var (tr, tsize) = DecodeRuneAt(t, ti);
            ti += tsize;
            // If they match, keep going; if not, return false.
            // Easy case.
            if (tr == sr)
            {
                continue;
            }
            // Make sr < tr to simplify what follows.
            if (tr < sr)
            {
                (tr, sr) = (sr, tr);
            }
            // Fast check for ASCII.
            if (tr < 0x80)
            {
                // ASCII only, sr/tr must be upper/lower case.
                if ('A' <= sr && sr <= 'Z' && tr == sr + ('a' - 'A'))
                {
                    continue;
                }
                return false;
            }
            // General case. SimpleFold(x) returns the next equivalent rune > x
            // or wraps around to smaller values.
            int r = SimpleFold(sr);
            while (r != sr && r < tr)
            {
                r = SimpleFold(r);
            }
            if (r == tr)
            {
                continue;
            }
            return false;
        }
        // First string is empty, so check if the second one is also empty.
        return ti >= t.Length;
    }

    // Mirror of Go unicode.SimpleFold (Go 1.26.4, Unicode 15.0.0).
    // PORT-NOTE: Go's asciiFold[128] fast-path table is omitted — it is a generated cache
    // of exactly this general path (caseOrbit lookup, then ToLower/ToUpper), so results
    // are identical for ASCII inputs.
    internal static int SimpleFold(int r)
    {
        if (r < 0 || r > 0x10FFFF)
        {
            return r;
        }
        // Consult caseOrbit table for special cases.
        ReadOnlySpan<ushort> from = CaseOrbitFrom;
        int lo = 0;
        int hi = from.Length;
        while (lo < hi)
        {
            int m = (int)((uint)(lo + hi) >> 1);
            if (from[m] < r)
            {
                lo = m + 1;
            }
            else
            {
                hi = m;
            }
        }
        if (lo < from.Length && from[lo] == r)
        {
            return CaseOrbitTo[lo];
        }
        // No folding specified. This is a one- or two-element
        // equivalence class containing rune and ToLower(rune)
        // and ToUpper(rune) if they are different from rune.
        int l = ToLowerRune(r);
        if (l != r)
        {
            return l;
        }
        return ToUpperRune(r);
    }

    // Go unicode.ToLower / unicode.ToUpper (simple case mapping, Go 1.26.4 tables =
    // Unicode 15.0.0). Backed by Rune.To{Lower,Upper}Invariant plus a small exception
    // layer: a full-sweep diff over all code points (this port session) showed exactly
    // two divergence classes between Go's tables and .NET 10 invariant ordinal casing:
    //   (a) .NET intentionally omits three UnicodeData mappings — lower(U+0130 İ)=i,
    //       upper(U+0131 ı)=I, upper(U+017F ſ)=S — which Go applies; restored below.
    //   (b) .NET 10 carries post-15.0.0 (Unicode 16/17) case additions Go lacks
    //       (U+019B/U+0264/U+1C89/U+1C8A/U+A7CB–A7CD/U+A7DA–A7DC and Garay
    //       U+10D50–U+10D85); suppressed below so folding matches Go exactly.
    // PORT-NOTE: the durable fix is transcribing Go's case tables in the stringutil
    // work item (tools/generator/transcribe-unicode.ts already does this for other
    // tables); this exception layer pins today's behavior to the Go blueprint and is
    // registered as a version-skew risk (05 §8.4). Surrogate code points map to
    // themselves, as in Go (they are absent from the Go tables).
    public static int ToLowerRune(int r)
    {
        if (r == 0x0130)
        {
            return 0x0069; // İ → i (UnicodeData simple mapping; .NET invariant omits it)
        }
        if (IsPostUnicode15Cased(r) || !Rune.IsValid(r))
        {
            return r;
        }
        return Rune.ToLowerInvariant(new Rune(r)).Value;
    }

    public static int ToUpperRune(int r)
    {
        if (r == 0x0131)
        {
            return 0x0049; // ı → I (UnicodeData simple mapping; .NET invariant omits it)
        }
        if (r == 0x017F)
        {
            return 0x0053; // ſ → S (UnicodeData simple mapping; .NET invariant omits it)
        }
        if (IsPostUnicode15Cased(r) || !Rune.IsValid(r))
        {
            return r;
        }
        return Rune.ToUpperInvariant(new Rune(r)).Value;
    }

    // Code points whose case mappings were added after Unicode 15.0.0 (so they are
    // cased under .NET 10 but caseless under Go 1.26.4's tables).
    private static bool IsPostUnicode15Cased(int r)
    {
        return r is 0x019B or 0x0264 or 0x1C89 or 0x1C8A
            or 0xA7CB or 0xA7CC or 0xA7CD or 0xA7DA or 0xA7DB or 0xA7DC
            || (r >= 0x10D50 && r <= 0x10D85);
    }

    // Mirror of Go strings.Map: applies mapping to each rune; a negative mapped value
    // drops the rune; the original string is returned unchanged (no allocation) when the
    // mapping changes nothing. Invalid input (lone surrogate) is decoded as U+FFFD and,
    // as in Go's handling of invalid UTF-8, re-encoded as U+FFFD in the output.
    internal static string MapRunes(Func<int, int> mapping, string s)
    {
        StringBuilder? b = null;
        for (int i = 0; i < s.Length;)
        {
            var (c, size) = DecodeRuneAt(s, i);
            int r = mapping(c);
            if (b == null)
            {
                // `c != utf8.RuneError` clause in Go: a decoded U+FFFD that is not a
                // literal U+FFFD character in the source is an invalid sequence and must
                // be rewritten even when the mapping is the identity.
                if (r == c && (c != 0xFFFD || s[i] == '�'))
                {
                    i += size;
                    continue;
                }
                b = new StringBuilder(s.Length);
                b.Append(s, 0, i);
            }
            if (r >= 0)
            {
                AppendRune(b, r);
            }
            i += size;
        }
        return b?.ToString() ?? s;
    }

    // Go: strings.Builder.WriteRune analog (plan/05-conventions.md §8.3) — a surrogate
    // or out-of-range value becomes U+FFFD, exactly like Go's encoder. Shared by
    // MapRunes (above) and JsCase.cs.
    internal static void AppendRune(StringBuilder sb, int rune)
    {
        if (!Rune.IsValid(rune))
        {
            sb.Append('�');
            return;
        }
        Span<char> buf = stackalloc char[2];
        int n = new Rune(rune).EncodeToUtf16(buf);
        sb.Append(buf[..n]);
    }

    // Go unicode.caseOrbit (unicode/tables.go, Unicode 15.0.0): the special simple-fold
    // orbits; all other runes fold via ToLower/ToUpper. Two parallel spans (From sorted
    // ascending) compiled to static data.
    private static ReadOnlySpan<ushort> CaseOrbitFrom =>
    [
        0x004B, 0x0053, 0x006B, 0x0073, 0x00B5, 0x00C5, 0x00DF, 0x00E5,
        0x0130, 0x0131, 0x017F, 0x01C4, 0x01C5, 0x01C6, 0x01C7, 0x01C8,
        0x01C9, 0x01CA, 0x01CB, 0x01CC, 0x01F1, 0x01F2, 0x01F3, 0x0345,
        0x0392, 0x0395, 0x0398, 0x0399, 0x039A, 0x039C, 0x03A0, 0x03A1,
        0x03A3, 0x03A6, 0x03A9, 0x03B2, 0x03B5, 0x03B8, 0x03B9, 0x03BA,
        0x03BC, 0x03C0, 0x03C1, 0x03C2, 0x03C3, 0x03C6, 0x03C9, 0x03D0,
        0x03D1, 0x03D5, 0x03D6, 0x03F0, 0x03F1, 0x03F4, 0x03F5, 0x0412,
        0x0414, 0x041E, 0x0421, 0x0422, 0x042A, 0x0432, 0x0434, 0x043E,
        0x0441, 0x0442, 0x044A, 0x0462, 0x0463, 0x1C80, 0x1C81, 0x1C82,
        0x1C83, 0x1C84, 0x1C85, 0x1C86, 0x1C87, 0x1C88, 0x1E60, 0x1E61,
        0x1E9B, 0x1E9E, 0x1FBE, 0x2126, 0x212A, 0x212B, 0xA64A, 0xA64B,
    ];

    private static ReadOnlySpan<ushort> CaseOrbitTo =>
    [
        0x006B, 0x0073, 0x212A, 0x017F, 0x039C, 0x00E5, 0x1E9E, 0x212B,
        0x0130, 0x0131, 0x0053, 0x01C5, 0x01C6, 0x01C4, 0x01C8, 0x01C9,
        0x01C7, 0x01CB, 0x01CC, 0x01CA, 0x01F2, 0x01F3, 0x01F1, 0x0399,
        0x03B2, 0x03B5, 0x03B8, 0x03B9, 0x03BA, 0x03BC, 0x03C0, 0x03C1,
        0x03C2, 0x03C6, 0x03C9, 0x03D0, 0x03F5, 0x03D1, 0x1FBE, 0x03F0,
        0x00B5, 0x03D6, 0x03F1, 0x03C3, 0x03A3, 0x03D5, 0x2126, 0x0392,
        0x03F4, 0x03A6, 0x03A0, 0x039A, 0x03A1, 0x0398, 0x0395, 0x0432,
        0x0434, 0x043E, 0x0441, 0x0442, 0x044A, 0x1C80, 0x1C81, 0x1C82,
        0x1C83, 0x1C84, 0x1C86, 0x0463, 0x1C87, 0x0412, 0x0414, 0x041E,
        0x0421, 0x1C85, 0x0422, 0x042A, 0x0462, 0xA64A, 0x1E61, 0x1E9B,
        0x1E60, 0x00DF, 0x0345, 0x03A9, 0x004B, 0x00C5, 0xA64B, 0x1C88,
    ];
}
