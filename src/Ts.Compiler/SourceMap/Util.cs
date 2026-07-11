// Ported from tsgo internal/sourcemap/util.go

using System;

namespace Ts.SourceMap;

public static class Util
{
    // Tries to find the sourceMappingURL comment at the end of a file.
    // Go: func TryGetSourceMappingURL(lineInfo *ECMALineInfo) string
    public static string TryGetSourceMappingURL(ECMALineInfo? lineInfo)
    {
        if (lineInfo != null)
        {
            for (int index = lineInfo.LineCount() - 1; index >= 0; index--)
            {
                string line = lineInfo.LineText(index);
                line = TrimLeftSpace(line);
                line = TrimRightLineBreak(line);
                if (line.Length == 0)
                {
                    continue;
                }
                if (line.Length < 4 || !line.StartsWith("//", StringComparison.Ordinal) || (line[2] != '#' && line[2] != '@') || line[3] != ' ')
                {
                    break;
                }
                const string prefix = "sourceMappingURL=";
                if (line[4..].StartsWith(prefix, StringComparison.Ordinal))
                {
                    return TrimRightSpace(line[(4 + prefix.Length)..]);
                }
            }
        }
        return "";
    }

    // Go: strings.TrimLeftFunc(line, unicode.IsSpace).
    // PORT-NOTE: Go's unicode.IsSpace and .NET char.IsWhiteSpace are both derived from the
    // Unicode White_Space property; the whitespace code points are all BMP, so char-level
    // (UTF-16 unit) trimming matches Go's rune-level trimming here.
    private static string TrimLeftSpace(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i]))
        {
            i++;
        }
        return s[i..];
    }

    // Go: strings.TrimRightFunc(url, unicode.IsSpace).
    private static string TrimRightSpace(string s)
    {
        int end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1]))
        {
            end--;
        }
        return s[..end];
    }

    // Go: strings.TrimRightFunc(line, stringutil.IsLineBreak).
    private static string TrimRightLineBreak(string s)
    {
        int end = s.Length;
        while (end > 0 && global::Ts.StringUtil.StringUtil.IsLineBreak(s[end - 1]))
        {
            end--;
        }
        return s[..end];
    }
}
