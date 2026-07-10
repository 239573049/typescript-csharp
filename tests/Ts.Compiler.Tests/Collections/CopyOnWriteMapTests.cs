// tsgo has no cow_test.go; these supplementary tests pin the copy-on-write and
// scope-restore semantics of cow.go (EnterScope restore closure → Scope ref
// struct consumed with `using`, 05-conventions.md §5.1 form ③/§5.2).

using System.Collections.Generic;
using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class CopyOnWriteMapTests
{
    [Fact]
    public void ZeroValueIsEmptyAndUsable()
    {
        var m = new CopyOnWriteMap<string, int>();

        Assert.False(m.Has("a"));
        Assert.False(m.TryGet("a", out _));

        m.Set("a", 1);

        Assert.True(m.Has("a"));
        Assert.True(m.TryGet("a", out var v));
        Assert.Equal(1, v);
    }

    [Fact]
    public void ScopeWritesDoNotLeakToParent()
    {
        var m = new CopyOnWriteMap<string, int>();
        m.Set("a", 1);

        using (m.EnterScope())
        {
            // The scope reads the inherited entries...
            Assert.True(m.TryGet("a", out var inherited));
            Assert.Equal(1, inherited);

            // ...and the first mutation transparently clones the storage.
            m.Set("b", 2);
            m.Set("a", 100);

            Assert.True(m.Has("b"));
            Assert.True(m.TryGet("a", out var inner));
            Assert.Equal(100, inner);
        }

        // Scope exit restored the parent's view.
        Assert.True(m.TryGet("a", out var outer));
        Assert.Equal(1, outer);
        Assert.False(m.Has("b"));
    }

    [Fact]
    public void NestedScopesRestoreLifo()
    {
        var m = new CopyOnWriteMap<string, int>();
        m.Set("k", 0);

        using (m.EnterScope())
        {
            m.Set("k", 1);

            using (m.EnterScope())
            {
                Assert.True(m.TryGet("k", out var v2));
                Assert.Equal(1, v2);

                m.Set("k", 2);
                m.Set("inner", 9);
            }

            // Inner scope restored the outer scope's clone.
            Assert.True(m.TryGet("k", out var v1));
            Assert.Equal(1, v1);
            Assert.False(m.Has("inner"));

            // Writes after an inner scope exits keep mutating the owned clone.
            m.Set("k2", 11);
            Assert.True(m.Has("k2"));
        }

        Assert.True(m.TryGet("k", out var v0));
        Assert.Equal(0, v0);
        Assert.False(m.Has("k2"));
    }

    [Fact]
    public void WritesAfterScopeExitReCloneWhenNotOwned()
    {
        // After EnterScope the map no longer owns its storage; a snapshot taken by a
        // sibling scope must never observe later writes.
        var m = new CopyOnWriteMap<string, int>();
        m.Set("a", 1);

        var observed = new List<int>();

        using (m.EnterScope())
        {
            m.Set("a", 2);
            Assert.True(m.TryGet("a", out var inScope));
            observed.Add(inScope);
        }

        using (m.EnterScope())
        {
            Assert.True(m.TryGet("a", out var restored));
            observed.Add(restored);
        }

        Assert.Equal(new[] { 2, 1 }, observed);
    }

    [Fact]
    public void CopyOnWriteSetScopeRestores()
    {
        var s = new CopyOnWriteSet<string>();
        s.Add("a");

        using (s.EnterScope())
        {
            s.Add("b");
            Assert.True(s.Has("a"));
            Assert.True(s.Has("b"));
        }

        Assert.True(s.Has("a"));
        Assert.False(s.Has("b"));
    }
}
