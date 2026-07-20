// Ported from tsgo internal/testutil/stringtestutil/stringtestutil.go.

using System;
using System.Collections.Generic;

namespace Ts.TestUtil;

public static class StringTestUtil
{
    // stringtestutil.go:9-51 — Dedent.
    public static string Dedent(string text)
    {
        var lines = text.Split('\n');
        // Remove blank lines in the beginning and end
        // and convert all tabs in the beginning of line to spaces
        var startLine = -1;
        var lastLine = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var firstNonWhite = -1;
            for (var j = 0; j < line.Length; j++)
            {
                if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(line[j]))
                {
                    firstNonWhite = j;
                    break;
                }
            }
            if (firstNonWhite > 0)
            {
                line = line[..firstNonWhite].Replace("\t", "    ") + line[firstNonWhite..];
                lines[i] = line;
            }
            if (line.Trim().Length > 0)
            {
                if (startLine == -1)
                {
                    startLine = i;
                }
                lastLine = i;
            }
        }
        var body = lines[Math.Max(startLine, 0)..(lastLine + 1)];
        var mappedLines = new string[body.Length];
        for (var i = 0; i < body.Length; i++)
        {
            mappedLines[i] = body[i].Trim().Length == 0 ? "" : body[i];
        }
        var indentation = global::Ts.StringUtil.StringUtil.GuessIndentation(mappedLines);
        if (indentation > 0)
        {
            for (var i = 0; i < body.Length; i++)
            {
                body[i] = body[i].Length > indentation ? body[i][indentation..] : "";
            }
        }
        return string.Join("\n", body);
    }
}
