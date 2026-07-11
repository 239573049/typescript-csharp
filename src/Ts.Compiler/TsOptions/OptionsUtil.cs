namespace Ts.TsOptions;

// PORT-NOTE: small port helper (not a Go file). Go's `strings.ToLower` is per-rune
// simple lowering via strings.Map (stringutil/compare.go:155 comment); the AOT-safe
// equivalent already lives in Ts.StringUtil.StringUtil.MapRunes/ToLowerRune (internal
// to this assembly). Centralized here so every tsoptions `strings.ToLower` site routes
// through the pinned Unicode 15.1 table rather than a culture-sensitive BCL call
// (plan/07 §7).
internal static class OptionsUtil
{
    internal static string ToLower(string s) =>
        global::Ts.StringUtil.StringUtil.MapRunes(global::Ts.StringUtil.StringUtil.ToLowerRune, s);

    // Go null-tolerant slice helpers (05-conventions §6.1). The repo has no array-level
    // CollectionExtensions yet, so these are scoped to Ts.TsOptions to translate
    // `len(nil)`/`range nil`.
    internal static int Len<T>(this T[]? a) => a?.Length ?? 0;

    internal static T[] OrEmpty<T>(this T[]? a) => a ?? System.Array.Empty<T>();
}
