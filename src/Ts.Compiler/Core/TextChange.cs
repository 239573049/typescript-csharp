// Ported from tsgo internal/core/textchange.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Text;

namespace Ts.Core;

// Go embeds TextRange by value; C# structs cannot inherit, so the range is a
// named member (same spelling as Go's promoted field, t.TextRange) with Pos/End
// pass-throughs standing in for Go's method promotion.
public readonly record struct TextChange(TextRange TextRange, string NewText)
{
    public int Pos => TextRange.Pos;

    public int End => TextRange.End;

    public string ApplyTo(string text) =>
        // Go: text[:t.Pos()] + t.NewText + text[t.End():]
        string.Concat(text.AsSpan(0, Pos), NewText, text.AsSpan(End));
}

public static partial class Core
{
    public static string ApplyBulkEdits(string text, TextChange[]? edits)
    {
        var b = new StringBuilder(text.Length); // Go: b.Grow(len(text))
        int lastEnd = 0;
        foreach (TextChange e in edits ?? [])
        {
            int start = e.TextRange.Pos;
            if (start != lastEnd)
            {
                b.Append(text, lastEnd, e.TextRange.Pos - lastEnd);
            }
            b.Append(e.NewText);

            lastEnd = e.TextRange.End;
        }
        b.Append(text, lastEnd, text.Length - lastEnd);

        return b.ToString();
    }
}
