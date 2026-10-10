// Package locale — ported from typescript-go internal/locale/locale.go (28 LOC micro-package).
//
// Go wraps `golang.org/x/text/language.Tag` (a BCP-47 language tag) and threads it through a
// context.Context. Per plan/06 §9.3 and plan/07 §7 (InvariantGlobalization), the .NET runtime
// has no ICU/CultureInfo tables and the `golang.org/x/text/language` package is unavailable, so
// this is a `readonly struct Locale` carrying the canonicalized tag string. The empty tag
// (`Default`, corresponding to Go's zero `language.Tag` == `language.Und`) resolves to no
// localized messages; any other tag is matched against the 13 embedded locale tables by
// Ts.Diagnostics.Diagnostics.GetLocalizedMessages (plan/04 §3.5-7).
//
// PORT-NOTE: Go's context.Context threading (WithLocale/FromContext) is not modelled here — per
// plan/06 §9.3 「context 传递 → 显式参数」, callers pass the Locale explicitly (the diagnosticwriter
// surface already does so via FormattingOptions.Locale).

using System;
using System.Text;

namespace Ts.Locale;

// locale.go:9 — `type Locale language.Tag`. In Go a `language.Tag` is an opaque struct that
// stringifies to a canonical BCP-47 tag; here we retain the canonical tag string (language
// lower-case, script title-case, region upper-case, everything else lower-case — the casing
// x/text's Tag.String() produces). Equality is by tag value.
public readonly struct Locale : IEquatable<Locale>
{
    private readonly string? tag;

    public Locale(string tag)
    {
        this.tag = tag;
    }

    // The canonical BCP-47 tag string. The zero value reads as "" (Go's `language.Und`).
    public string Tag => tag ?? "";

    // locale.go:11 — `var Default Locale`. Go's package-level zero value; the undefined tag.
    public static readonly Locale Default = default;

    public bool Equals(Locale other) => string.Equals(Tag, other.Tag, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Locale other && Equals(other);

    public override int GetHashCode() => Tag.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Tag;

    // locale.go:23-27 — `func Parse(localeStr string) (locale Locale, ok bool)`. Go delegates to
    // `language.Parse`, which "gracefully fails" (returns the parsed tag plus an error that is
    // discarded except for the ok flag).
    //
    // PORT-NOTE (deviation D-020): the well-formedness half of x/text's parser is mirrored here
    // (RFC 5646 §2.1 grammar, '_' accepted as a separator like x/text, plus internal/language's
    // 4+ character primary-subtag rejection — see Canonicalize). What is NOT mirrored is the IANA
    // registry lookup: x/text additionally fails 2-3 letter languages, scripts, regions and variants
    // that are well-formed but unregistered ("xy" → ValueError), and normalizes extlang/grandfathered
    // tags. The remaining over-acceptance only affects the tsconfig/CLI "locale" validation
    // diagnostic (tsconfigparsing.go:386-388); message matching (Diagnostics.GetLocalizedMessages)
    // treats an unknown language as "no translation", matching Go's matcher confidence No → English.
    public static bool TryParse(string localeStr, out Locale locale)
    {
        string? canonical = Canonicalize(localeStr);
        if (canonical == null)
        {
            locale = Default;
            return false;
        }
        // x/text: "und" is the zero tag.
        locale = canonical == "und" ? Default : new Locale(canonical);
        return true;
    }

    // Returns the canonical form of a well-formed BCP-47 tag, or null if it is not well-formed.
    //   langtag    = language ["-" script] ["-" region] *("-" variant) *("-" extension) ["-" privateuse]
    //   language   = 2*3ALPHA ["-" extlang] / 4ALPHA / 5*8ALPHA ; extlang = 3ALPHA *2("-" 3ALPHA)
    //   script     = 4ALPHA ; region = 2ALPHA / 3DIGIT
    //   variant    = 5*8alphanum / (DIGIT 3alphanum)
    //   extension  = singleton 1*("-" (2*8alphanum)) ; privateuse = "x" 1*("-" (1*8alphanum))
    private static string? Canonicalize(string s)
    {
        if (s.Length == 0)
        {
            return null;
        }
        string[] parts = s.Replace('_', '-').Split('-');
        foreach (string part in parts)
        {
            if (part.Length == 0 || part.Length > 8 || !IsAlphaNum(part))
            {
                return null;
            }
        }

        var sb = new StringBuilder(s.Length);
        int i = 0;

        // Private-use-only tag ("x-…").
        if (IsSingleton(parts[0], 'x'))
        {
            return AppendPrivateUse(parts, 0, sb) ? sb.ToString() : null;
        }

        // language
        string lang = parts[i];
        if (!IsAlpha(lang) || lang.Length < 2)
        {
            return null;
        }
        // x/text internal/language.Parse (parse.go:261-271): the scanner takes the leading run of
        // alphanumeric characters as the language subtag and rejects the whole tag as a syntax error
        // when that token is 4+ characters (`else if n >= 4 { return Und, ErrSyntax }`), before any
        // IANA registry lookup. That is why "whoops"/"abcd" are errors while 2-3 letter languages
        // ("en", "abc") go on to the registry check. BCP-47 reserves 4-8 letter primary subtags for
        // future use, so rejecting them here matches Go without needing the registry.
        if (lang.Length >= 4)
        {
            return null;
        }
        sb.Append(Lower(lang));
        i++;
        if (lang.Length is 2 or 3)
        {
            // up to three extlang subtags
            for (int n = 0; n < 3 && i < parts.Length && parts[i].Length == 3 && IsAlpha(parts[i]); n++, i++)
            {
                sb.Append('-').Append(Lower(parts[i]));
            }
        }

        // script
        if (i < parts.Length && parts[i].Length == 4 && IsAlpha(parts[i]))
        {
            string script = Lower(parts[i]);
            sb.Append('-').Append(char.ToUpperInvariant(script[0])).Append(script, 1, 3);
            i++;
        }

        // region
        if (i < parts.Length && ((parts[i].Length == 2 && IsAlpha(parts[i])) || (parts[i].Length == 3 && IsDigits(parts[i]))))
        {
            sb.Append('-').Append(global::Ts.StringUtil.StringUtil.ToUpperJS(parts[i]));
            i++;
        }

        // variants
        while (i < parts.Length && (parts[i].Length >= 5 || (parts[i].Length == 4 && char.IsAsciiDigit(parts[i][0]))))
        {
            sb.Append('-').Append(Lower(parts[i]));
            i++;
        }

        // extensions
        while (i < parts.Length && parts[i].Length == 1 && !IsSingleton(parts[i], 'x'))
        {
            sb.Append('-').Append(Lower(parts[i]));
            i++;
            int start = i;
            while (i < parts.Length && parts[i].Length >= 2)
            {
                sb.Append('-').Append(Lower(parts[i]));
                i++;
            }
            if (i == start)
            {
                return null;
            }
        }

        // private use
        if (i < parts.Length)
        {
            if (!IsSingleton(parts[i], 'x'))
            {
                return null;
            }
            sb.Append('-');
            return AppendPrivateUse(parts, i, sb) ? sb.ToString() : null;
        }
        return sb.ToString();
    }

    private static bool AppendPrivateUse(string[] parts, int i, StringBuilder sb)
    {
        if (i + 1 >= parts.Length)
        {
            return false;
        }
        sb.Append('x');
        for (int j = i + 1; j < parts.Length; j++)
        {
            sb.Append('-').Append(Lower(parts[j]));
        }
        return true;
    }

    private static bool IsSingleton(string part, char c) => part.Length == 1 && (part[0] | 0x20) == c;

    private static string Lower(string s) => global::Ts.StringUtil.StringUtil.ToLowerJS(s); // inputs are validated ASCII alphanumerics

    private static bool IsAlphaNum(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsAlpha(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsDigits(string s)
    {
        foreach (char c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }
        return true;
    }
}
