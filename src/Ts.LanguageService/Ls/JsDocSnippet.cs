// Ported from tsgo internal/ls/jsdoc_snippet.go — the position-probe slice
// (isPotentiallyValidJSDocSnippetCompletionPosition and its prefix/suffix string walkers).
// getJSDocSnippetCompletion itself (snippet synthesis + LSP edits) lands with the completions
// shell wave.

using Ts.Ast;

namespace Ts.Ls;

internal static class JsDocSnippet
{
    // jsdoc_snippet.go:74-85
    internal static bool IsPotentiallyValidJSDocSnippetCompletionPosition(SourceFile file, int position)
    {
        var text = file.Text();
        var lineStart = global::Ts.Format.Util.GetLineStartPositionForPosition(position, file);
        var prefix = text[lineStart..position];
        if (!IsJSDocSnippetPrefix(prefix))
        {
            return false;
        }

        var lineEnd = LsDeps.GetLineEndOfPosition(file, position);
        var suffix = text[position..lineEnd];
        return IsJSDocSnippetSuffix(suffix);
    }

    // jsdoc_snippet.go:497-515 — isJSDocSnippetPrefix.
    internal static bool IsJSDocSnippetPrefix(string prefix)
    {
        var trimmed = TrimRightSingleLineWhitespace(prefix);
        if (trimmed.EndsWith("/**", System.StringComparison.Ordinal))
        {
            return true;
        }
        var start = SkipSingleLineWhitespace(prefix, 0);
        if (start >= trimmed.Length || trimmed[start] != '/')
        {
            return false;
        }
        if (start + 3 > trimmed.Length)
        {
            return false;
        }
        for (var i = start + 1; i < trimmed.Length; i++)
        {
            if (trimmed[i] != '*')
            {
                return false;
            }
        }
        return trimmed.Length - start >= 3;
    }

    // jsdoc_snippet.go:517-528 — getJSDocSnippetPrefixStart.
    internal static (int Start, bool Ok) GetJSDocSnippetPrefixStart(string prefix)
    {
        var trimmed = TrimRightSingleLineWhitespace(prefix);
        for (var i = trimmed.Length - 1; i >= 0 && trimmed[i] == '*'; i--)
        {
            if (i > 0 && trimmed[i - 1] == '/')
            {
                return (i - 1, true);
            }
        }
        if (trimmed.EndsWith('/'))
        {
            return (trimmed.Length - 1, true);
        }
        return (0, false);
    }

    // jsdoc_snippet.go:530-544 — isJSDocSnippetSuffix.
    internal static bool IsJSDocSnippetSuffix(string suffix)
    {
        var trimmed = TrimRightSingleLineWhitespace(suffix[SkipSingleLineWhitespace(suffix, 0)..]);
        if (trimmed.Length == 0)
        {
            return true;
        }
        if (!trimmed.EndsWith('/'))
        {
            return false;
        }
        for (var i = 0; i < trimmed.Length - 1; i++)
        {
            if (trimmed[i] != '*')
            {
                return false;
            }
        }
        return true;
    }

    // jsdoc_snippet.go:537-546 — getJSDocSnippetSuffixEnd.
    internal static (int End, bool Ok) GetJSDocSnippetSuffixEnd(string suffix)
    {
        var pos = SkipSingleLineWhitespace(suffix, 0);
        while (pos < suffix.Length && suffix[pos] == '*')
        {
            pos++;
        }
        if (pos < suffix.Length && suffix[pos] == '/')
        {
            return (pos + 1, true);
        }
        return (0, false);
    }

    // jsdoc_snippet.go:549-562 — trimRightSingleLineWhitespace. Go walks UTF-8 bytes; C# walks
    // UTF-16 units with surrogate pairing — equivalent per code point.
    internal static string TrimRightSingleLineWhitespace(string text)
    {
        var end = 0;
        for (var pos = 0; pos < text.Length;)
        {
            int ch;
            int size;
            if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
            {
                ch = char.ConvertToUtf32(text[pos], text[pos + 1]);
                size = 2;
            }
            else
            {
                ch = text[pos];
                size = 1;
            }
            pos += size;
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                end = pos;
            }
        }
        return text[..end];
    }

    // jsdoc_snippet.go:564-573 — skipSingleLineWhitespace.
    internal static int SkipSingleLineWhitespace(string text, int pos)
    {
        while (pos < text.Length)
        {
            int ch;
            int size;
            if (char.IsHighSurrogate(text[pos]) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
            {
                ch = char.ConvertToUtf32(text[pos], text[pos + 1]);
                size = 2;
            }
            else
            {
                ch = text[pos];
                size = 1;
            }
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                break;
            }
            pos += size;
        }
        return pos;
    }
}
