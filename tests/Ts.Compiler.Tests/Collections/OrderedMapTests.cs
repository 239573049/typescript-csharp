// Ported from tsgo internal/collections/ordered_map_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class OrderedMapTests
{
    [Fact]
    public void OrderedMap()
    {
        var m = new OrderedMap<int, string>();

        Assert.False(m.Has(1));

        const int N = 1000;
        const int start = 1;
        const int end = start + N;

        // Seed the map with ascending keys and values for easier testing.
        for (int i = start; i < end; i++)
        {
            m.Set(i, PadInt(i));
        }

        Assert.Equal(N, m.Size());

        // Attempt to overwrite existing keys in reverse order.
        for (int i = end - 1; i >= start; i--)
        {
            m.Set(i, PadInt(i));
        }

        Assert.Equal(N, m.Size());

        for (int i = start; i < end; i++)
        {
            Assert.True(m.TryGet(i, out var got));
            Assert.Equal(PadInt(i), got);
        }

        foreach (var (k, v) in m.Entries())
        {
            Assert.Equal(PadInt(k), v);
        }

        var keys = m.Keys().ToArray();
        Assert.Equal(N, keys.Length);
        Assert.True(IsSorted(keys, static (a, b) => a.CompareTo(b)));

        var values = m.Values().ToArray();
        Assert.Equal(N, values.Length);
        Assert.True(IsSorted(values, string.CompareOrdinal));

        int firstKey = 0;
        foreach (var k in m.Keys())
        {
            firstKey = k;
            break;
        }
        Assert.Equal(start, firstKey);

        string firstValue = "";
        foreach (var v in m.Values())
        {
            firstValue = v;
            break;
        }
        Assert.Equal(PadInt(start), firstValue);

        foreach (var (k, v) in m.Entries())
        {
            firstKey = k;
            firstValue = v;
            break;
        }

        Assert.Equal(start, firstKey);
        Assert.Equal(PadInt(start), firstValue);

        for (int i = start + 1; i < end; i++)
        {
            Assert.True(m.TryDelete(i, out var deleted));
            Assert.Equal(PadInt(i), deleted);
            Assert.False(m.Has(i));

            // PORT-NOTE: Go asserts the zero value "" after a failed Get/Delete; the
            // C# Try pattern hands back default(string) == null instead.
            Assert.False(m.TryGet(i, out var afterGet));
            Assert.Null(afterGet);

            Assert.False(m.TryDelete(i, out var afterDelete));
            Assert.Null(afterDelete);
        }

        Assert.Equal(1, m.Size());
        Assert.True(m.Has(start));

        Assert.True(m.TryDelete(start, out var last));
        Assert.Equal(PadInt(start), last);

        Assert.Equal(0, m.Size());
    }

    [Fact]
    public void OrderedMapClone()
    {
        var m = new OrderedMap<int, string>();
        m.Set(1, "one");
        m.Set(2, "two");

        var clone = m.Clone()!;

        Assert.NotSame(m, clone);
        Assert.Equal(2, clone.Size());
        Assert.Equal(new[] { 1, 2 }, clone.Keys().ToArray());
        Assert.Equal(new[] { "one", "two" }, clone.Values().ToArray());

        Assert.True(clone.TryGet(1, out var v));
        Assert.Equal("one", v);

        m.TryDelete(1, out _);

        Assert.Equal(1, m.Size());
        Assert.Equal(2, clone.Size());
        Assert.Equal(new[] { 1, 2 }, clone.Keys().ToArray());
        Assert.Equal(new[] { "one", "two" }, clone.Values().ToArray());
    }

    [Fact]
    public void OrderedMapClear()
    {
        var m = new OrderedMap<int, string>();
        m.Set(1, "one");
        m.Set(2, "two");

        m.Clear();

        Assert.Equal(0, m.Size());
    }

    // Go's TestOrderedMapWithSizeHint asserts allocation counts via
    // testing.AllocsPerRun, which has no .NET equivalent; ported as a functional
    // smoke test of the size-hint constructor. PORT-NOTE: alloc-count assertion dropped.
    [Fact]
    public void OrderedMapWithSizeHint()
    {
        const int N = 1024;
        var m = new OrderedMap<int, int>(N);
        for (int i = 0; i < N; i++)
        {
            m.Set(i, i);
        }
        Assert.Equal(N, m.Size());
    }

    // TODO(csport): TestOrderedMapUnmarshalJSON is deferred together with the JSON
    // surface (plan/10 §2.2 wave 3); port it when MarshalJSONTo/UnmarshalJSONFrom land.

    // Supplementary (not in Go tests): pins Go's delete semantics — Delete removes
    // the key from the insertion-order list (ordered_map.go:102-125), so a later Set
    // re-appends it at the end, while overwriting an existing key keeps its slot.
    [Fact]
    public void DeleteThenReinsertMovesKeyToEnd()
    {
        var m = new OrderedMap<string, int>();
        m.Set("a", 1);
        m.Set("b", 2);
        m.Set("c", 3);

        Assert.True(m.TryDelete("a", out _));
        m.Set("a", 10);

        Assert.Equal(new[] { "b", "c", "a" }, m.Keys().ToArray());

        // Overwriting an existing key must not move it.
        m.Set("b", 20);
        Assert.Equal(new[] { "b", "c", "a" }, m.Keys().ToArray());
    }

    // Supplementary: the iterators must observe items added during iteration
    // ("We use a for loop here to ensure we enumerate new items added during
    // iteration", ordered_map.go:135).
    [Fact]
    public void KeysEnumeratesItemsAddedDuringIteration()
    {
        var m = new OrderedMap<int, int>();
        m.Set(1, 1);

        var seen = new List<int>();
        foreach (var k in m.Keys())
        {
            seen.Add(k);
            if (k == 1)
            {
                m.Set(2, 2);
            }
        }

        Assert.Equal(new[] { 1, 2 }, seen);
    }

    // Supplementary: covers the DiffOrderedMaps family (no Go unit test upstream);
    // callback order is deterministic because both inputs are insertion ordered.
    [Fact]
    public void DiffOrderedMapsReportsAddedRemovedModified()
    {
        var m1 = new OrderedMap<string, int>();
        m1.Set("a", 1);
        m1.Set("b", 2);
        m1.Set("c", 3);

        var m2 = new OrderedMap<string, int>();
        m2.Set("b", 20);
        m2.Set("c", 3);
        m2.Set("d", 4);

        var added = new List<(string Key, int Value)>();
        var removed = new List<(string Key, int Value)>();
        var modified = new List<(string Key, int OldValue, int NewValue)>();

        Ts.Collections.OrderedMap.DiffOrderedMaps(
            m1,
            m2,
            (k, v) => added.Add((k, v)),
            (k, v) => removed.Add((k, v)),
            (k, oldV, newV) => modified.Add((k, oldV, newV)));

        Assert.Equal(new[] { ("d", 4) }, added);
        Assert.Equal(new[] { ("a", 1) }, removed);
        Assert.Equal(new[] { ("b", 2, 20) }, modified);
    }

    // Supplementary: TryEntryAt (ordered_map.go:83-93) — indexed access follows
    // insertion order.
    [Fact]
    public void TryEntryAtFollowsInsertionOrder()
    {
        var m = new OrderedMap<string, int>();
        m.Set("x", 7);
        m.Set("y", 8);

        Assert.True(m.TryEntryAt(0, out var k0, out var v0));
        Assert.Equal("x", k0);
        Assert.Equal(7, v0);

        Assert.True(m.TryEntryAt(1, out var k1, out var v1));
        Assert.Equal("y", k1);
        Assert.Equal(8, v1);

        Assert.False(m.TryEntryAt(-1, out _, out _));
        Assert.False(m.TryEntryAt(2, out _, out _));
    }

    private static string PadInt(int n)
    {
        // Go: fmt.Sprintf("%10d", n)
        return string.Format(CultureInfo.InvariantCulture, "{0,10}", n);
    }

    private static bool IsSorted<T>(IReadOnlyList<T> list, Comparison<T> cmp)
    {
        for (int i = 1; i < list.Count; i++)
        {
            if (cmp(list[i - 1], list[i]) > 0)
            {
                return false;
            }
        }
        return true;
    }
}
