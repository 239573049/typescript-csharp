// tsgo has no multimap_test.go; these supplementary tests pin the slice
// add/remove semantics of multimap.go and the GroupBy grouping order.

using System.Linq;
using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class MultiMapTests
{
    [Fact]
    public void AddGetHasLen()
    {
        var m = new MultiMap<string, int>();

        Assert.False(m.Has("a"));
        Assert.Null(m.Get("a"));

        m.Add("a", 1);
        m.Add("a", 2);
        m.Add("b", 3);

        Assert.True(m.Has("a"));
        Assert.Equal(new[] { 1, 2 }, m.Get("a"));
        Assert.Equal(2, m.Len());
    }

    [Fact]
    public void RemoveDropsKeyWhenLastValueRemoved()
    {
        var m = new MultiMap<string, int>();
        m.Add("a", 1);
        m.Add("a", 2);

        m.Remove("a", 1);
        Assert.Equal(new[] { 2 }, m.Get("a"));

        m.Remove("a", 42); // absent value: no-op
        Assert.Equal(new[] { 2 }, m.Get("a"));

        m.Remove("a", 2); // last value: the key disappears entirely
        Assert.False(m.Has("a"));
        Assert.Null(m.Get("a"));

        m.Remove("missing", 1); // absent key: no-op
    }

    [Fact]
    public void RemoveAllClearKeysValues()
    {
        var m = new MultiMap<string, int>();
        m.Add("a", 1);
        m.Add("b", 2);

        m.RemoveAll("a");
        Assert.False(m.Has("a"));
        Assert.Equal(1, m.Len());

        // map-iter: 序无关 —— 此处仅剩单键，断言与顺序无关
        Assert.Equal(new[] { "b" }, m.Keys().ToArray());
        Assert.Equal(new[] { 2 }, Assert.Single(m.Values()));

        m.Clear();
        Assert.Equal(0, m.Len());
    }

    [Fact]
    public void GroupByGroupsInInputOrder()
    {
        var m = MultiMap.GroupBy(new[] { 1, 2, 3, 4, 5 }, static v => v % 2);

        Assert.Equal(new[] { 1, 3, 5 }, m.Get(1));
        Assert.Equal(new[] { 2, 4 }, m.Get(0));

        // Go: for range over a nil slice is zero iterations.
        var empty = MultiMap.GroupBy<int, int>(null, static v => v);
        Assert.Equal(0, empty.Len());
    }
}
