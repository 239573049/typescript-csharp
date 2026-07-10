// Ported from tsgo internal/collections/ordered_set_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class OrderedSetTests
{
    [Fact]
    public void OrderedSet()
    {
        var s = new OrderedSet<int>();

        s.Add(1);
        s.Add(2);
        s.Add(3);

        Assert.True(s.Has(1));
        Assert.True(s.Has(2));
        Assert.True(s.Has(3));

        Assert.True(s.Delete(2));

        var values = s.Values().ToArray();
        Assert.Equal(2, values.Length);
        Assert.True(IsSorted(values));

        s.Clear();

        Assert.Equal(0, s.Size());
        Assert.False(s.Has(1));
        Assert.False(s.Has(2));
        Assert.False(s.Has(3));

        var s2 = s.Clone();
        Assert.NotSame(s, s2);
        Assert.Equal(0, s2.Size());
    }

    // Go's TestOrderedSetWithSizeHint asserts allocation counts via
    // testing.AllocsPerRun, which has no .NET equivalent; ported as a functional
    // smoke test of the size-hint constructor. PORT-NOTE: alloc-count assertion dropped.
    [Fact]
    public void OrderedSetWithSizeHint()
    {
        const int N = 1024;
        var m = new OrderedSet<int>(N);
        for (int i = 0; i < N; i++)
        {
            m.Add(i);
        }
        Assert.Equal(N, m.Size());
    }

    private static bool IsSorted(IReadOnlyList<int> list)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i - 1] > list[i])
            {
                return false;
            }
        }
        return true;
    }
}
