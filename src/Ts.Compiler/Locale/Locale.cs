// Package locale — ported from typescript-go internal/locale/locale.go (28 LOC micro-package).
//
// Go wraps `golang.org/x/text/language.Tag` (a BCP-47 language tag) and threads it through a
// context.Context. Per plan/06 §9.3 and plan/07 §7 (InvariantGlobalization), the .NET runtime
// has no ICU/CultureInfo tables and the `golang.org/x/text/language` matcher is unavailable, so
// this is a `readonly struct Locale(string Tag)` carrying the raw tag string. There are no
// localized message files in scope, so the effective behaviour is English identity: the empty
// tag (`Default`, corresponding to Go's zero `language.Tag` == `language.Und`) resolves to no
// localized messages, and diagnostics.Localize returns the message template unchanged (see
// Ts.Diagnostics.Diagnostics.Localize).
//
// PORT-NOTE: Go's context.Context threading (WithLocale/FromContext) is not modelled here — per
// plan/06 §9.3 「context 传递 → 显式参数」, callers pass the Locale explicitly (the diagnosticwriter
// surface already does so via FormattingOptions.Locale). Those two functions are omitted until a
// consumer needs them; the current output-side formatter has none.

using System;

namespace Ts.Locale;

// locale.go:9 — `type Locale language.Tag`. In Go a `language.Tag` is an opaque struct that
// stringifies to a canonical BCP-47 tag; here we retain only the raw tag string because no
// culture-aware operation is performed on it under InvariantGlobalization. Equality is by tag
// value (default record-struct semantics), matching Go's Tag comparison well enough for the
// closed set of locale keys used by the localized-message lookup (empty == Und == default).
public readonly struct Locale : IEquatable<Locale>
{
    private readonly string? tag;

    public Locale(string tag)
    {
        this.tag = tag;
    }

    // The canonical BCP-47 tag string. The zero value reads as "" (Go's `language.Und` also
    // renders as the empty/undefined tag and yields no localized messages).
    public string Tag => tag ?? "";

    // locale.go:11 — `var Default Locale`. Go's package-level zero value; the undefined tag.
    public static readonly Locale Default = default;

    public bool Equals(Locale other) => string.Equals(Tag, other.Tag, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Locale other && Equals(other);

    public override int GetHashCode() => Tag.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => Tag;

    // locale.go:23-27 — `func Parse(localeStr string) (locale Locale, ok bool)`. Go delegates to
    // `language.Parse`, which "gracefully fails" (returns the parsed tag plus an error that is
    // discarded except for the ok flag). Under InvariantGlobalization there is no BCP-47 parser
    // available; per plan/06 §9.3 the corpus is a closed set and a lenient normalization suffices.
    // We accept any non-empty, syntactically plausible tag (letters, digits, '-') and store it
    // verbatim; ok reflects whether it looked like a tag. The message-lookup side (phase 9) does
    // the actual matching against the fixed locale table, so exact canonicalization is not
    // load-bearing here.
    //
    // PORT-NOTE: Go's `language.Parse` performs canonicalization/validation this cannot; kept
    // minimal because no localized-message files are in scope and Default (Und) is the only tag
    // exercised by the errors.txt output path. (AGENTS.md §6 deviation registration.)
    public static bool TryParse(string localeStr, out Locale locale)
    {
        if (!IsWellFormed(localeStr))
        {
            locale = Default;
            return false;
        }
        locale = new Locale(localeStr);
        return true;
    }

    private static bool IsWellFormed(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }
        foreach (char c in s)
        {
            bool ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}
