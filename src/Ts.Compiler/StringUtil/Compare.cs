// Ported from tsgo internal/stringutil/compare.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Ported ahead of the full stringutil work item as the dependency of tspath
// (06-module-inventory.md §2.5). `type Comparison = int` lives in GlobalUsings.cs
// (05-conventions.md §2.3); the rune helpers live in Runes.cs.

using System;

namespace Ts.StringUtil;

public static partial class StringUtil
{
    public static bool EquateStringCaseInsensitive(string a, string b)
    {
        // !!!
        // return a == b || strings.ToUpper(a) == strings.ToUpper(b)
        return EqualFold(a, b);
    }

    public static bool EquateStringCaseSensitive(string a, string b)
    {
        return a == b;
    }

    public static Func<string, string, bool> GetStringEqualityComparer(bool ignoreCase)
    {
        if (ignoreCase)
        {
            return EquateStringCaseInsensitive;
        }
        return EquateStringCaseSensitive;
    }

    public const Comparison ComparisonLessThan = -1;
    public const Comparison ComparisonEqual = 0;
    public const Comparison ComparisonGreaterThan = 1;

    public static Comparison CompareStringsCaseInsensitive(string a, string b)
    {
        if (a == b)
        {
            return ComparisonEqual;
        }
        int ai = 0;
        int bi = 0;
        while (true)
        {
            var (ca, sa) = DecodeRuneAt(a, ai);
            var (cb, sb) = DecodeRuneAt(b, bi);
            if (sa == 0)
            {
                if (sb == 0)
                {
                    return ComparisonEqual;
                }
                return ComparisonLessThan;
            }
            if (sb == 0)
            {
                return ComparisonGreaterThan;
            }
            int lca = ToLowerRune(ca);
            int lcb = ToLowerRune(cb);
            if (lca != lcb)
            {
                if (lca < lcb)
                {
                    return ComparisonLessThan;
                }
                return ComparisonGreaterThan;
            }
            ai += sa;
            bi += sb;
        }
    }

    public static Comparison CompareStringsCaseSensitive(string a, string b)
    {
        // Go strings.Compare is UTF-8 byte order = code-point order; see
        // CompareRuneOrdinal (05 §8.4 non-BMP ordering rule).
        return CompareRuneOrdinal(a, b);
    }

    public static Func<string, string, Comparison> GetStringComparer(bool ignoreCase)
    {
        if (ignoreCase)
        {
            return CompareStringsCaseInsensitive;
        }
        return CompareStringsCaseSensitive;
    }

    public static bool HasPrefix(string s, string prefix, bool caseSensitive)
    {
        if (caseSensitive)
        {
            return s.StartsWith(prefix, StringComparison.Ordinal);
        }
        if (prefix.Length > s.Length)
        {
            return false;
        }
        return EqualFold(s[..prefix.Length], prefix);
    }

    public static bool HasSuffix(string s, string suffix, bool caseSensitive)
    {
        if (caseSensitive)
        {
            return s.EndsWith(suffix, StringComparison.Ordinal);
        }
        if (suffix.Length > s.Length)
        {
            return false;
        }
        return EqualFold(s[(s.Length - suffix.Length)..], suffix);
    }

    public static bool HasPrefixAndSuffixWithoutOverlap(string s, string prefix, string suffix, bool caseSensitive)
    {
        if (prefix.Length + suffix.Length > s.Length)
        {
            return false;
        }

        return HasPrefix(s, prefix, caseSensitive) && HasSuffix(s, suffix, caseSensitive);
    }

    public static Comparison CompareStringsCaseInsensitiveThenSensitive(string a, string b)
    {
        Comparison cmp = CompareStringsCaseInsensitive(a, b);
        if (cmp != ComparisonEqual)
        {
            return cmp;
        }
        return CompareStringsCaseSensitive(a, b);
    }

    // CompareStringsCaseInsensitiveEslintCompatible performs a case-insensitive comparison
    // using toLowerCase() instead of toUpperCase() for ESLint compatibility.
    //
    // `CompareStringsCaseInsensitive` transforms letters to uppercase for unicode reasons,
    // while eslint's `sort-imports` rule transforms letters to lowercase. Which one you choose
    // affects the relative order of letters and ASCII characters 91-96, of which `_` is a
    // valid character in an identifier. So if we used `CompareStringsCaseInsensitive` for
    // import sorting, TypeScript and eslint would disagree about the correct case-insensitive
    // sort order for `__String` and `Foo`. Since eslint's whole job is to create consistency
    // by enforcing nitpicky details like this, it makes way more sense for us to just adopt
    // their convention so users can have auto-imports without making eslint angry.
    public static Comparison CompareStringsCaseInsensitiveEslintCompatible(string a, string b)
    {
        if (a == b)
        {
            return ComparisonEqual;
        }
        // Go strings.ToLower = per-rune simple lowering via strings.Map.
        a = MapRunes(ToLowerRune, a);
        b = MapRunes(ToLowerRune, b);
        return CompareStringsCaseSensitive(a, b);
    }
}
