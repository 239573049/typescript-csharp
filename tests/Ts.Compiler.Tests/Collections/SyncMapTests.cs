// Ported from tsgo internal/collections/syncmap_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using Ts.Collections;
using Xunit;

namespace Ts.Collections.Tests;

public class SyncMapTests
{
    [Fact]
    public void SyncMapWithNil()
    {
        var m = new SyncMap<string, object?>();

        Assert.False(m.TryLoad("foo", out var got1));
        Assert.Null(got1);

        m.Store("foo", null);

        Assert.True(m.TryLoad("foo", out var got2));
        Assert.Null(got2);

        var (too, loaded) = m.LoadOrStore("too", null);
        Assert.False(loaded);
        Assert.Null(too);

        m.Range((k, v) => true);
    }
}
