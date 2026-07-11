// Ported from typescript-go internal/diagnostics/diagnostics.go — the message
// localization/formatting surface (diagnostics.go:67-159): Message.Localize, the package-level
// Localize, Format (the "{0}" placeholder substitution), StringifyArgs, and NewAdHocMessage.
//
// This is the output-side text formatter that the diagnosticwriter package (errors.txt) depends
// on. It was previously deferred to phase 9 along with the *localized* message tables; here we
// land only the formatting half, which is English-identity and byte-load-bearing:
//
//   * getLocalizedMessages (diagnostics.go:89-113) uses `golang.org/x/text/language`'s matcher
//     against the 13 embedded `loc/*.json.gz` tables. Under InvariantGlobalization those tables
//     and matcher are unavailable (plan/07 §5.3, §7), and no localized message files are in
//     scope. Faithful reduction: for the undefined tag (Go's `language.Und`, == global::Ts.Locale.Locale.Default)
//     getLocalizedMessages returns nil, so `text` stays `message.text` and Localize == Format.
//     Since Default is the only tag exercised by the errors.txt path, GetLocalizedMessages always
//     returns null here — Localize therefore returns the (English) template with args substituted,
//     byte-identical to Go running with locale.Default. The gzip/matcher plumbing arrives in
//     phase 9 (plan/06 §9.3) and will replace GetLocalizedMessages then.
//
// PORT-NOTE (AGENTS.md §6): the localized-table lookup is reduced to a null return pending the
// phase-9 locale loader. The Format placeholder logic and StringifyArgs are ported verbatim.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ts.Diagnostics;

public static partial class Diagnostics
{
    // diagnostics.go:67-69 — Message.Localize(locale, args...any).
    public static string Localize(this Message m, global::Ts.Locale.Locale locale, params object?[] args)
    {
        return Localize(locale, m, "", StringifyArgs(args) ?? System.Array.Empty<string>());
    }

    // diagnostics.go:71-85 — package-level Localize.
    public static string Localize(global::Ts.Locale.Locale locale, Message? message, string key, params string[] args)
    {
        if (message == null)
        {
            message = KeyToMessage(key);
        }
        if (message == null)
        {
            throw new InvalidOperationException("Unknown diagnostic message: " + key);
        }

        var text = message.Text;
        var localized = GetLocalizedMessages(locale);
        if (localized != null && localized.TryGetValue(message.Key, out var localizedText))
        {
            text = localizedText;
        }

        return Format(text, args);
    }

    // diagnostics.go:89-113 — getLocalizedMessages. See file header: reduced to null (English
    // identity) until the phase-9 locale loader lands. Go returns nil for `language.Und`; every
    // errors.txt-path caller passes global::Ts.Locale.Locale.Default (== Und), so returning null is exact.
    private static Dictionary<string, string>? GetLocalizedMessages(global::Ts.Locale.Locale locale)
    {
        // Go: `if loc == language.Und { return nil }` — Default is the only tag in scope.
        _ = locale;
        return null;
    }

    // diagnostics.go:115 — `var placeholderRegexp = regexp.MustCompile(`{(\d+)}`)`. Source-generated
    // for AOT (plan/07 §1.3: RegexOptions.Compiled is banned; [GeneratedRegex] compiles to code).
    [GeneratedRegex(@"{(\d+)}")]
    private static partial Regex PlaceholderRegexp();

    // diagnostics.go:117-134 — Format substitutes {0}, {1}, … placeholders with args, replacing
    // invalid UTF-8 in each arg with U+FFFD first.
    public static string Format(string text, string[] args)
    {
        if (args.Length == 0)
        {
            return text;
        }

        // diagnostics.go:122-125 — Replace invalid UTF-8 with the Unicode replacement character.
        // Go's strings.ToValidUTF8 operates on UTF-8 bytes; in the UTF-16 world (plan/05 §8.1) the
        // analogue is replacing unpaired surrogates with U+FFFD (a C# string is already valid
        // UTF-16 except for lone surrogates). core.SameMap returns the original slice when nothing
        // changes; ReplaceInvalidUtf16 mirrors that by returning the same reference when clean.
        args = SameMapValidUtf16(args);

        return PlaceholderRegexp().Replace(text, match =>
        {
            var indexText = match.Value.Substring(1, match.Value.Length - 2);
            // diagnostics.go:128-131 — ParseInt(base 10); on error or out-of-range → panic.
            if (!int.TryParse(indexText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) || index >= args.Length)
            {
                throw new InvalidOperationException("Invalid formatting placeholder");
            }
            return args[index];
        });
    }

    // diagnostics.go:123-125 — core.SameMap(args, ToValidUTF8) analogue for UTF-16: returns the
    // original array when no arg contains a lone surrogate, else a new array with them replaced by
    // U+FFFD (preserving core.SameMap's "no change → same reference" identity, plan/05 §6.2).
    private static string[] SameMapValidUtf16(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var replaced = ReplaceLoneSurrogates(args[i]);
            if (!ReferenceEquals(replaced, args[i]))
            {
                var result = new string[args.Length];
                System.Array.Copy(args, result, i);
                result[i] = replaced;
                for (int j = i + 1; j < args.Length; j++)
                {
                    result[j] = ReplaceLoneSurrogates(args[j]);
                }
                return result;
            }
        }
        return args;
    }

    private static string ReplaceLoneSurrogates(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                // Valid surrogate pair — pass both code units through unchanged.
                sb?.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
            {
                // Lone (unpaired) surrogate — Go's invalid UTF-8 → U+FFFD analogue.
                sb ??= new StringBuilder(s.Length).Append(s, 0, i);
                sb.Append('�');
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? s;
    }

    // diagnostics.go:136-150 — StringifyArgs.
    public static string[]? StringifyArgs(object?[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        var result = new string[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is string s)
            {
                result[i] = s;
            }
            else
            {
                // Go: fmt.Sprintf("%v", arg). AST/diagnostic args are strings/numbers in practice;
                // ToString() matches for those (plan/05 §8, and mirrors the local StringifyArgs in
                // Ast/Diagnostic.cs which this can eventually supersede).
                result[i] = args[i]?.ToString() ?? "";
            }
        }
        return result;
    }

    // diagnostics.go:152-159 — NewAdHocMessage.
    public static Message NewAdHocMessage(string message)
    {
        return new Message(
            code: -1,
            category: Category.Error,
            key: "-1",
            text: message);
    }
}
