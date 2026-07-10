using System;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class BinarySearchTests
{
    private static readonly int[] values = [1, 3, 5, 7, 9];

    private static (int Index, bool Found) Search(int target) =>
        Core.BinarySearchUniqueFunc<int>(values, (_, v) => v.CompareTo(target));

    [Fact]
    public void FindsExistingElement()
    {
        Assert.Equal((3, true), Search(7));
        Assert.Equal((0, true), Search(1));
        Assert.Equal((4, true), Search(9));
    }

    // Go sort.Search semantics: the not-found index is the insertion point,
    // NOT the complement Array.BinarySearch would return.
    [Fact]
    public void ReturnsInsertionPointWhenNotFound()
    {
        Assert.Equal((0, false), Search(0));
        Assert.Equal((2, false), Search(4));
        Assert.Equal((5, false), Search(10));
    }

    [Fact]
    public void EmptyInputReturnsZeroNotFound()
    {
        Assert.Equal((0, false), Core.BinarySearchUniqueFunc<int>(ReadOnlySpan<int>.Empty, (_, _) => 0));
    }

    [Fact]
    public void ComparisonReceivesTheElementIndex()
    {
        // cmp gets (index, element); resolve the probe against the index alone.
        var (index, found) = Core.BinarySearchUniqueFunc<int>(values, (i, _) => i.CompareTo(2));
        Assert.Equal(2, index);
        Assert.True(found);
    }
}
