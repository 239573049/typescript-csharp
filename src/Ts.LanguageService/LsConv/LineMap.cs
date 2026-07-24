// Ported from tsgo internal/ls/lsconv/linemap.go (whole file). PORT-NOTE: Go builds the map
// over UTF-8 bytes; C# text is UTF-16, so LineStarts are UTF-16 code-unit offsets and AsciiOnly
// records whether every code unit is ASCII (the property both users rely on: offsets == column
// counts for the negotiated encoding).

using System.Collections.Generic;

namespace Ts.Ls.LsConv;

// linemap.go:12-17 — LSPLineStarts / LSPLineMap.
public sealed class LSPLineMap
{
    public List<int> LineStarts = [];
    public bool AsciiOnly; // TODO(jakebailey): collect ascii-only info per line
}

public static class LineMap
{
    // linemap.go:19-54 — ComputeLSPLineStarts: like core.ComputeLineStarts, but only considers
    // "\n", "\r", and "\r\n" as line breaks, and reports when the text is ASCII-only.
    public static LSPLineMap ComputeLSPLineStarts(string text)
    {
        var lineStarts = new List<int>();
        var asciiOnly = true;

        var textLen = text.Length;
        var pos = 0;
        var lineStart = 0;
        while (pos < textLen)
        {
            var b = text[pos];
            if (b < 0x80)
            {
                pos++;
                switch (b)
                {
                    case '\r':
                        if (pos < textLen && text[pos] == '\n')
                        {
                            pos++;
                        }
                        goto case '\n';
                    case '\n':
                        lineStarts.Add(lineStart);
                        lineStart = pos;
                        break;
                }
            }
            else
            {
                pos++;
                asciiOnly = false;
            }
        }
        lineStarts.Add(lineStart);

        return new LSPLineMap
        {
            LineStarts = lineStarts,
            AsciiOnly = asciiOnly,
        };
    }

    // linemap.go:56-72 — ComputeIndexOfLineStart.
    public static int ComputeIndexOfLineStart(this LSPLineMap lm, int targetPos)
    {
        // port of computeLineOfPosition(lineStarts: readonly number[], position: number, lowerBound?: number): number {
        var lineNumber = lm.LineStarts.BinarySearch(targetPos);
        if (lineNumber < 0)
        {
            // If the actual position was not found, the binary search returns where the target line start would be inserted
            // if the target was in the slice.
            // e.g. if the line starts at [5, 10, 23, 80] and the position requested was 20
            // then the search will return (3, false).
            //
            // We want the index of the previous line start, so we subtract 1.
            lineNumber = ~lineNumber;
            if (lineNumber > 0)
            {
                lineNumber -= 1;
            }
        }
        return lineNumber;
    }
}
