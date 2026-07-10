// tsgo has no set_test.go; these supplementary tests pin the nil-tolerance
// semantics of set.go (Go nil-receiver methods → extension methods on Set<T>?,
// 05-conventions.md §10.3) and the basic operations.

using System;
using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class SetTests
{
    [Fact]
    public void NullSetIsSafeForReads()
    {
        Set<string>? s = null;

        Assert.False(s.Has("x"));
        Assert.Equal(0, s.Len());
        Assert.Empty(s.Keys());
        Assert.Null(s.Clone());
        Assert.True(s.IsSubsetOf(null));
        Assert.False(s.Intersects(null));
        Assert.Null(s.UnionedWith(null));

        s.Clear();      // Go: nil-receiver Clear is a no-op
        s.Union(null);  // Go: both sides empty → no-op
    }

    [Fact]
    public void UnionIntoNullSetThrows()
    {
        Set<int>? s = null;
        var other = Set.FromItems(1);

        var ex = Assert.Throws<InvalidOperationException>(() => s.Union(other));
        Assert.Equal("cannot modify nil Set", ex.Message);
    }

    [Fact]
    public void AddDeleteHasLenClear()
    {
        var s = new Set<string>();

        Assert.True(s.AddIfAbsent("a"));
        Assert.False(s.AddIfAbsent("a"));
        s.Add("b");

        Assert.True(s.Has("a"));
        Assert.Equal(2, s.Len());

        s.Delete("a");
        Assert.False(s.Has("a"));
        Assert.Equal(1, s.Len());

        s.Clear();
        Assert.Equal(0, s.Len());
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var s = Set.FromItems(1, 2);
        var clone = s.Clone();

        Assert.NotNull(clone);
        s.Add(3);

        Assert.Equal(3, s.Len());
        Assert.Equal(2, clone.Len());
        Assert.True(clone.Has(1));
        Assert.False(clone.Has(3));
    }

    [Fact]
    public void UnionAndUnionedWith()
    {
        var s = Set.FromItems(1, 2);
        s.Union(Set.FromItems(2, 3));
        Assert.Equal(3, s.Len());
        Assert.True(s.Has(3));

        // Union into an empty (never-written) set clones the other's storage.
        var empty = new Set<int>();
        empty.Union(s);
        Assert.Equal(3, empty.Len());

        Set<int>? left = null;
        var result = left.UnionedWith(Set.FromItems(9));
        Assert.NotNull(result);
        Assert.True(result.Has(9));
        Assert.Equal(1, result.Len());

        var result2 = Set.FromItems(1).UnionedWith(null);
        Assert.NotNull(result2);
        Assert.Equal(1, result2.Len());
    }

    [Fact]
    public void EqualsSubsetIntersects()
    {
        var a = Set.FromItems("x", "y");
        var b = Set.FromItems("y", "x");
        var c = Set.FromItems("x");

        bool abEqual = a.Equals(b);
        Assert.True(abEqual);

        bool acEqual = a.Equals(c);
        Assert.False(acEqual);

        bool aNullEqual = a.Equals(null);
        Assert.False(aNullEqual);

        // maps.Equal treats nil and empty maps as equal.
        bool emptyEqual = new Set<string>().Equals(new Set<string>());
        Assert.True(emptyEqual);

        Assert.True(c.IsSubsetOf(a));
        Assert.False(a.IsSubsetOf(c));
        Assert.True(a.Intersects(c));
        Assert.False(c.Intersects(Set.FromItems("z")));
    }
}
