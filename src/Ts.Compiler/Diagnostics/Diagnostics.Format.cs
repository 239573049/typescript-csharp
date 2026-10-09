// Ported from typescript-go internal/diagnostics/diagnostics.go — the message
// localization/formatting surface (diagnostics.go:67-159): Message.Localize, the package-level
// Localize, getLocalizedMessages, Format (the "{0}" placeholder substitution), StringifyArgs, and
// NewAdHocMessage; plus the hand-written half of loc_generated.go (loadLocaleData and the
// x/text language matcher, plan/04 §3.5-7).
//
// PORT-NOTE (plan/04 §3.5-7, risk registered in plan/12): Go matches the requested tag with
// `golang.org/x/text/language.NewMatcher` over English + the 13 embedded locales and accepts any
// confidence >= Low. Under InvariantGlobalization that package (and CultureInfo matching) is
// unavailable, so MatchLocale hand-codes the matcher's behaviour on this closed set: English →
// index 0 (no translation); Chinese → zh-TW for the Hant script or TW/HK/MO regions, else
// zh-CN; any other language → the registered locale with the same primary language subtag
// (x/text yields High/Low confidence for a same-language match regardless of region); unknown
// languages → confidence No → no translation.

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

    // diagnostics.go:87 — `var localizedMessagesCache sync.Map // map[language.Tag]map[Key]string`.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, string>?> localizedMessagesCache =
        new(StringComparer.Ordinal);

    // diagnostics.go:89-113 — getLocalizedMessages.
    private static Dictionary<string, string>? GetLocalizedMessages(global::Ts.Locale.Locale locale)
    {
        string loc = locale.Tag;
        if (loc.Length == 0)
        {
            // Go: `if loc == language.Und { return nil }`.
            return null;
        }

        // Check cache first
        if (localizedMessagesCache.TryGetValue(loc, out var cached))
        {
            return cached;
        }

        Dictionary<string, string>? messages = null;

        int index = MatchLocale(loc);
        if (index >= 0 && index < LocaleFuncs.Length)
        {
            var fn = LocaleFuncs[index];
            if (fn != null)
            {
                messages = fn.Value;
            }
        }

        localizedMessagesCache[loc] = messages;
        return messages;
    }

    // ≙ `_, index, confidence := matcher.Match(loc)` + `confidence >= language.Low`
    // (diagnostics.go:104-105). Returns the LocaleTags index, or -1 for confidence No.
    // See the file header PORT-NOTE for the rules.
    internal static int MatchLocale(string tag)
    {
        string[] parts = tag.Replace('_', '-').Split('-');
        string lang = global::Ts.StringUtil.StringUtil.ToLowerJS(parts[0]);
        string script = "";
        string region = "";
        for (int i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 1)
            {
                break; // extensions / private use carry no matching information
            }
            if (part.Length == 4 && script.Length == 0 && region.Length == 0 && char.IsAsciiLetter(part[0]))
            {
                script = global::Ts.StringUtil.StringUtil.ToLowerJS(part);
            }
            else if (region.Length == 0 && (part.Length == 2 || (part.Length == 3 && char.IsAsciiDigit(part[0]))))
            {
                region = global::Ts.StringUtil.StringUtil.ToUpperJS(part);
            }
        }

        if (lang == "zh")
        {
            bool traditional = script == "hant" || (script.Length == 0 && region is "TW" or "HK" or "MO");
            return Array.IndexOf(LocaleTags, traditional ? "zh-TW" : "zh-CN");
        }

        int sameLanguage = -1;
        for (int i = 0; i < LocaleTags.Length; i++)
        {
            string candidate = LocaleTags[i];
            int dash = candidate.IndexOf('-', StringComparison.Ordinal);
            string candidateLang = dash < 0 ? candidate : candidate[..dash];
            if (!string.Equals(candidateLang, lang, StringComparison.Ordinal))
            {
                continue;
            }
            if (dash >= 0 && string.Equals(candidate[(dash + 1)..], region, StringComparison.Ordinal))
            {
                return i; // exact language + region
            }
            if (sameLanguage < 0)
            {
                sameLanguage = i;
            }
        }
        return sameLanguage;
    }

    // loc_generated.go — loadLocaleData: gunzip + JSON-decode a flat `map[Key]string`. Go
    // panics on a malformed resource (it is a build product); we throw likewise. The JSON is
    // read with Utf8JsonReader (AOT-safe, no serializer metadata needed for string→string).
    private static Dictionary<string, string> LoadLocaleData(string resourceName)
    {
        using var stream = typeof(Diagnostics).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("failed to create gzip reader: missing resource " + resourceName);
        using var gr = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        using var buffer = new System.IO.MemoryStream();
        gr.CopyTo(buffer);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var reader = new System.Text.Json.Utf8JsonReader(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        try
        {
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject)
            {
                throw new InvalidOperationException("failed to unmarshal locale data: expected object");
            }
            while (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
            {
                string key = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.String)
                {
                    throw new InvalidOperationException("failed to unmarshal locale data: expected string value for " + key);
                }
                result[key] = reader.GetString()!;
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException("failed to unmarshal locale data: " + ex.Message, ex);
        }
        return result;
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
                result[i] = SprintV(args[i]);
            }
        }
        return result;
    }

    // diagnostics.go:146 — Go's `fmt.Sprintf("%v", arg)` fallback. Diagnostic args are strings,
    // booleans, numbers or a nested diagnostic (handled by the caller); this reproduces %v for
    // those, most importantly bool → "true"/"false" (C# ToString would give "True"/"False"),
    // which is byte-load-bearing in the tsc trace baselines (e.g. resolver.go:395's
    // `result.Primary`). Numbers use the invariant culture; the default case keeps ToString, so
    // types Go would print as their struct value are only approximate (none observed in practice).
    private static string SprintV(object? arg)
    {
        switch (arg)
        {
            case null:
                return "<nil>";
            case bool b:
                return b ? "true" : "false";
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return System.Convert.ToString(arg, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            case float f:
                return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            default:
                return arg.ToString() ?? "";
        }
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
