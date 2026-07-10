using System;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class DebugTests
{
    [Fact]
    public void FailPrefixesReason()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.Fail("boom"));
        Assert.Equal("Debug failure. boom", ex.Message);
        var bare = Assert.Throws<DebugFailureException>(() => Debug.Fail());
        Assert.Equal("Debug failure.", bare.Message);
    }

    [Fact]
    public void AssertOnlyThrowsOnFalse()
    {
        Debug.Assert(true);
        var ex = Assert.Throws<DebugFailureException>(() => Debug.Assert(false, "why"));
        Assert.Contains("False expression: why", ex.Message, StringComparison.Ordinal);
        var bare = Assert.Throws<DebugFailureException>(() => Debug.Assert(false));
        Assert.Contains("False expression.", bare.Message, StringComparison.Ordinal);
    }

    private sealed class FakeKind : IKindString
    {
        public string KindString() => "KindFoo";
    }

    [Fact]
    public void AssertNeverPrefersKindString()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.AssertNever(new FakeKind()));
        Assert.Contains("Illegal value: KindFoo", ex.Message, StringComparison.Ordinal);
        var str = Assert.Throws<DebugFailureException>(() => Debug.AssertNever(42));
        Assert.Contains("Illegal value: 42", str.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailBadSyntaxKindMentionsNode()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.FailBadSyntaxKind(new FakeKind()));
        Assert.Contains("Node KindFoo was unexpected.", ex.Message, StringComparison.Ordinal);
    }
}
