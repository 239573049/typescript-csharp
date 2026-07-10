using System;

namespace Ts.Core;

// Go: internal/core/text.go
// TextPos (int32) dissolves to bare int (05-conventions.md §3: sources are < 2 GiB
// upstream, and every position in the port is a UTF-16 code-unit offset).
// Go's NewTextRange(pos, end) is the primary constructor; UndefinedTextRange() is
// the Undefined field (default(TextRange) is {0,0}, matching Go's zero value).
public readonly record struct TextRange(int Pos, int End)
{
    public static readonly TextRange Undefined = new(-1, -1);

    public int Len => End - Pos;

    public bool IsValid => Pos >= 0 || End >= 0;

    public bool Contains(int pos) => pos >= Pos && pos < End;

    public bool ContainsInclusive(int pos) => pos >= Pos && pos <= End;

    public bool ContainsExclusive(int pos) => Pos < pos && pos < End;

    public TextRange WithPos(int pos) => new(pos, End);

    public TextRange WithEnd(int end) => new(Pos, end);

    public bool ContainedBy(TextRange t2) => t2.Pos <= Pos && t2.End >= End;

    public bool Overlaps(TextRange t2)
    {
        int start = Math.Max(Pos, t2.Pos);
        int end = Math.Min(End, t2.End);
        return start < end;
    }

    // Similar to Overlaps, but treats touching ranges as intersecting.
    // For example, [0, 5) intersects [5, 10).
    public bool Intersects(TextRange t2)
    {
        int start = Math.Max(Pos, t2.Pos);
        int end = Math.Min(End, t2.End);
        return start <= end;
    }

    public static int CompareTextRanges(TextRange r1, TextRange r2)
    {
        int c = r1.Pos - r2.Pos;
        if (c != 0)
        {
            return c;
        }
        return r1.End - r2.End;
    }
}
