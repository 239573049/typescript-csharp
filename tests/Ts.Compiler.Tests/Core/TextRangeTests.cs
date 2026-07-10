using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class TextRangeTests
{
    [Fact]
    public void DefaultIsZeroZeroAndValid()
    {
        TextRange t = default;
        Assert.Equal(0, t.Pos);
        Assert.Equal(0, t.End);
        Assert.True(t.IsValid);
    }

    [Fact]
    public void UndefinedIsMinusOneMinusOneAndInvalid()
    {
        Assert.Equal(-1, TextRange.Undefined.Pos);
        Assert.Equal(-1, TextRange.Undefined.End);
        Assert.False(TextRange.Undefined.IsValid);
    }

    [Theory]
    [InlineData(5, true)]   // start inclusive
    [InlineData(9, true)]
    [InlineData(10, false)] // end exclusive
    [InlineData(4, false)]
    public void ContainsIsHalfOpen(int pos, bool expected)
    {
        var t = new TextRange(5, 10);
        Assert.Equal(expected, t.Contains(pos));
    }

    [Fact]
    public void ContainsInclusiveIncludesEnd()
    {
        var t = new TextRange(5, 10);
        Assert.True(t.ContainsInclusive(10));
        Assert.False(t.ContainsExclusive(5));
        Assert.True(t.ContainsExclusive(6));
    }

    [Fact]
    public void TouchingRangesIntersectButDoNotOverlap()
    {
        var a = new TextRange(0, 5);
        var b = new TextRange(5, 10);
        Assert.False(a.Overlaps(b));
        Assert.True(a.Intersects(b));
    }

    [Fact]
    public void ContainedByAndWithMutatorsWork()
    {
        var outer = new TextRange(0, 10);
        var inner = new TextRange(2, 8);
        Assert.True(inner.ContainedBy(outer));
        Assert.False(outer.ContainedBy(inner));
        Assert.Equal(new TextRange(1, 8), inner.WithPos(1));
        Assert.Equal(new TextRange(2, 9), inner.WithEnd(9));
    }

    [Fact]
    public void CompareOrdersByPosThenEnd()
    {
        Assert.True(TextRange.CompareTextRanges(new TextRange(1, 5), new TextRange(2, 3)) < 0);
        Assert.True(TextRange.CompareTextRanges(new TextRange(2, 5), new TextRange(2, 3)) > 0);
        Assert.Equal(0, TextRange.CompareTextRanges(new TextRange(2, 3), new TextRange(2, 3)));
    }
}
