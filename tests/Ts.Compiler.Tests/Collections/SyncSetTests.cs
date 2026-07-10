// tsgo has no syncset_test.go; these supplementary tests pin the SyncSet API
// surface over its SyncMap carrier.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class SyncSetTests
{
    [Fact]
    public void AddHasDeleteSize()
    {
        var s = new SyncSet<string>();

        Assert.True(s.IsEmpty());
        Assert.False(s.Has("a"));

        s.Add("a");
        Assert.True(s.Has("a"));
        Assert.False(s.IsEmpty());
        Assert.Equal(1, s.Size());

        Assert.False(s.AddIfAbsent("a"));
        Assert.True(s.AddIfAbsent("b"));
        Assert.Equal(2, s.Size());

        s.Delete("a");
        Assert.False(s.Has("a"));
        Assert.Equal(1, s.Size());
    }

    [Fact]
    public void RangeToSliceKeysAndEarlyStop()
    {
        var s = new SyncSet<int>();
        s.Add(1);
        s.Add(2);
        s.Add(3);

        var viaRange = new List<int>();
        s.Range(k =>
        {
            viaRange.Add(k);
            return true;
        });
        Assert.Equal(3, viaRange.Count);

        var viaSlice = s.ToSlice();
        Array.Sort(viaSlice);
        Assert.Equal(new[] { 1, 2, 3 }, viaSlice);

        Assert.Equal(3, s.Keys().Count());

        // Returning false stops the enumeration after the first item.
        int count = 0;
        s.Range(_ =>
        {
            count++;
            return false;
        });
        Assert.Equal(1, count);
    }
}
