// Ported from tsgo internal/lsp/lsproto/util.go (comparison helpers subset).

namespace Ts.LsProto;

public static class Util
{
    // Implements a cmp.Compare like function for two Position
    // ComparePositions(pos, other) == cmp.Compare(pos, other)
    public static int ComparePositions(Position pos, Position other)
    {
        var lineComp = pos.Line.CompareTo(other.Line);
        if (lineComp != 0)
        {
            return lineComp;
        }
        return pos.Character.CompareTo(other.Character);
    }

    // Implements a cmp.Compare like function for two Range
    // CompareRanges(lsRange, other) == cmp.Compare(lsRange, other)
    //
    //	Range.Start is compared before Range.End
    public static int CompareRanges(Range lsRange, Range other)
    {
        var startComp = ComparePositions(lsRange.Start, other.Start);
        if (startComp != 0)
        {
            return startComp;
        }
        return ComparePositions(lsRange.End, other.End);
    }
}
