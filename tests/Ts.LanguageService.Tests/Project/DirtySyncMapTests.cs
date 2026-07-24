// Ported from tsgo internal/project/dirty/syncmap_test.go (whole file).

using System.Collections.Generic;
using System.Threading.Tasks;
using Ts.Project.Dirty;
using Xunit;

namespace Ts.Project.Tests;

// syncmap_test.go:10-17 — testValue is a simple cloneable type for testing.
internal sealed class TestValue : Cloneable<TestValue>
{
    internal string data = "";

    public TestValue Clone()
    {
        return new TestValue { data = data };
    }
}

public sealed class DirtySyncMapTests
{
    // syncmap_test.go:22-103 — proxy for race condition.
    [Fact]
    public async Task ProxyForRaceCondition()
    {
        // Create a sync map with a base value
        var baseMap = new Dictionary<string, TestValue>
        {
            ["key1"] = new TestValue { data = "original" },
        };
        var syncMap = new SyncMap<string, TestValue>(baseMap);

        // Load the same entry from multiple goroutines to simulate race condition
        SyncMapEntry<string, TestValue>? entry1 = null, entry2 = null;

        var ct = TestContext.Current.CancellationToken;
        var t1 = Task.Run(() =>
        {
            var (e, ok) = syncMap.Load("key1");
            Assert.True(ok, "entry1 should be loaded");
            entry1 = e;
        }, ct);
        var t2 = Task.Run(() =>
        {
            var (e, ok) = syncMap.Load("key1");
            Assert.True(ok, "entry2 should be loaded");
            entry2 = e;
        }, ct);
        await Task.WhenAll(t1, t2);

        // Both entries should exist and have the same initial value
        Assert.Equal("original", entry1!.Value().data);
        Assert.Equal("original", entry2!.Value().data);
        Assert.False(entry1.Dirty());
        Assert.False(entry2.Dirty());

        // Now try to change both entries concurrently to trigger the proxy mechanism.
        var c1 = Task.Run(() => entry1.Change(v => v.data = "changed_by_entry1"), ct);
        var c2 = Task.Run(() => entry2.Change(v => v.data = "changed_by_entry2"), ct);
        await Task.WhenAll(c1, c2);

        // After the race, one entry should have proxyFor set and both should reflect the same final state
        var finalValue1 = entry1.Value().data;
        var finalValue2 = entry2.Value().data;
        Assert.Equal(finalValue1, finalValue2);

        // Both entries should be marked as dirty
        Assert.True(entry1.Dirty());
        Assert.True(entry2.Dirty());

        // At least one entry should have proxyFor set (the one that lost the race)
        var hasProxy = entry1.proxyFor != null || entry2.proxyFor != null;
        Assert.True(hasProxy, "at least one entry should have proxyFor set");

        // If entry1 has a proxy, it should point to entry2, and vice versa
        if (entry1.proxyFor != null)
        {
            Assert.Same(entry2, entry1.proxyFor);
        }
        if (entry2.proxyFor != null)
        {
            Assert.Same(entry1, entry2.proxyFor);
        }
    }

    // syncmap_test.go:105-165 — proxy operations delegation.
    [Fact]
    public void ProxyOperationsDelegation()
    {
        var baseMap = new Dictionary<string, TestValue>
        {
            ["key1"] = new TestValue { data = "original" },
        };
        var syncMap = new SyncMap<string, TestValue>(baseMap);

        // Load two entries for the same key
        var (entry1, ok1) = syncMap.Load("key1");
        Assert.True(ok1);
        var (entry2, ok2) = syncMap.Load("key1");
        Assert.True(ok2);

        // Force one to become a proxy by making them both dirty in sequence
        entry1!.Change(v => v.data = "changed_by_entry1");
        entry2!.Change(v => v.data = "changed_by_entry2");

        // Determine which is the proxy and which is the target
        SyncMapEntry<string, TestValue> proxy, target;
        if (entry1.proxyFor != null)
        {
            proxy = entry1;
            target = entry2;
        }
        else
        {
            proxy = entry2;
            target = entry1;
        }

        // Test that proxy operations are delegated to the target
        // Change through proxy should affect target
        proxy.Change(v => v.data = "changed_through_proxy");
        Assert.Equal("changed_through_proxy", target.Value().data);
        Assert.Equal("changed_through_proxy", proxy.Value().data);

        // ChangeIf through proxy should work
        var changed = proxy.ChangeIf(
            v => v.data == "changed_through_proxy",
            v => v.data = "conditional_change");
        Assert.True(changed);
        Assert.Equal("conditional_change", target.Value().data);
        Assert.Equal("conditional_change", proxy.Value().data);

        // Dirty status should be consistent
        Assert.Equal(target.Dirty(), proxy.Dirty());

        // Locked operations should work through proxy
        proxy.Locked(v => v.Change(val => val.data = "locked_change"));
        Assert.Equal("locked_change", target.Value().data);
        Assert.Equal("locked_change", proxy.Value().data);
    }

    // syncmap_test.go:167-222 — proxy delete operations.
    [Fact]
    public void ProxyDeleteOperations()
    {
        var baseMap = new Dictionary<string, TestValue>
        {
            ["key1"] = new TestValue { data = "original" },
        };
        var syncMap = new SyncMap<string, TestValue>(baseMap);

        // Load two entries and make one a proxy
        var (entry1, _) = syncMap.Load("key1");
        var (entry2, _) = syncMap.Load("key1");

        entry1!.Change(v => v.data = "modified");
        entry2!.Change(v => v.data = "modified2");

        // Determine which is the proxy
        var proxy = entry1.proxyFor != null ? entry1 : entry2;

        // Delete through proxy should affect target
        proxy.Delete();

        // Both should reflect the deletion
        var (_, exists) = syncMap.Load("key1");
        Assert.False(exists, "key should be deleted from sync map");

        // DeleteIf through proxy should work
        var base2 = new Dictionary<string, TestValue>
        {
            ["key2"] = new TestValue { data = "test" },
        };
        var syncMap2 = new SyncMap<string, TestValue>(base2);

        var (entry3, _) = syncMap2.Load("key2");
        var (entry4, _) = syncMap2.Load("key2");

        entry3!.Change(v => v.data = "modified");
        entry4!.Change(v => v.data = "modified2");

        var proxy2 = entry3.proxyFor != null ? entry3 : entry4;

        proxy2.DeleteIf(v => v.data == "modified2" || v.data == "modified");

        var (_, exists2) = syncMap2.Load("key2");
        Assert.False(exists2, "key2 should be deleted conditionally");
    }

    // syncmap_test.go:224-244 — no proxy when no race.
    [Fact]
    public void NoProxyWhenNoRace()
    {
        var baseMap = new Dictionary<string, TestValue>
        {
            ["key1"] = new TestValue { data = "original" },
        };
        var syncMap = new SyncMap<string, TestValue>(baseMap);

        // Load and modify a single entry - no race condition
        var (entry, ok) = syncMap.Load("key1");
        Assert.True(ok);

        entry!.Change(v => v.data = "changed");

        // Should not have a proxy since there was no race
        Assert.Null(entry.proxyFor);
        Assert.True(entry.Dirty());
        Assert.Equal("changed", entry.Value().data);
    }
}
