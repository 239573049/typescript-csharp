// Ported from tsgo internal/debug/debug_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTEs:
//  * The debug package is absorbed as the Ts.Core.Debug static class (plan/01 §2.3, ruling R3),
//    so the tests live under Core/ and keep the Go file order.
//  * Go `testutil.AssertPanics(t, fn, expected)` → `var ex = Assert.Throws<DebugFailureException>(fn);
//    Assert.Equal(expected, ex.Message)` (05-conventions.md §16.3). The messages are compared
//    verbatim (full equality, not Contains) exactly as the Go helper does.
//  * `t.Parallel()` → deleted (xUnit collection-level parallelism, §16.1).
//  * Go `mockNode`/`mockStringer` → the IKindString/ToString-backed private fakes below.

using System;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class DebugTests
{
    // debug_test.go:10-15 — TestFailEmptyReason.
    [Fact]
    public void FailEmptyReason()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.Fail(""));
        Assert.Equal("Debug failure.", ex.Message);
    }

    // debug_test.go:17-22 — TestFailWithReason.
    [Fact]
    public void FailWithReason()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.Fail("something went wrong"));
        Assert.Equal("Debug failure. something went wrong", ex.Message);
    }

    // debug_test.go:24-26 — mockNode.
    private sealed class MockNode : IKindString
    {
        private readonly string kind;

        public MockNode(string kind) => this.kind = kind;

        public string KindString() => kind;
    }

    // debug_test.go:28-33 — TestFailBadSyntaxKindNoMessage.
    [Fact]
    public void FailBadSyntaxKindNoMessage()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.FailBadSyntaxKind(new MockNode("FooNode")));
        Assert.Equal("Debug failure. Unexpected node.\nNode FooNode was unexpected.", ex.Message);
    }

    // debug_test.go:35-40 — TestFailBadSyntaxKindWithMessage.
    [Fact]
    public void FailBadSyntaxKindWithMessage()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.FailBadSyntaxKind(new MockNode("BarNode"), "custom message"));
        Assert.Equal("Debug failure. custom message\nNode BarNode was unexpected.", ex.Message);
    }

    // debug_test.go:42-47 — TestAssertNeverDefaultMessageKindString.
    [Fact]
    public void AssertNeverDefaultMessageKindString()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.AssertNever(new MockNode("TestNode")));
        Assert.Equal("Debug failure. Illegal value: TestNode", ex.Message);
    }

    // debug_test.go:49-54 — TestAssertNeverCustomMessageKindString.
    [Fact]
    public void AssertNeverCustomMessageKindString()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.AssertNever(new MockNode("TestNode"), "bad value:"));
        Assert.Equal("Debug failure. bad value: TestNode", ex.Message);
    }

    // debug_test.go:56-58 — mockStringer.
    private sealed class MockStringer
    {
        private readonly string s;

        public MockStringer(string s) => this.s = s;

        public override string ToString() => s;
    }

    // debug_test.go:60-65 — TestAssertNeverStringer.
    [Fact]
    public void AssertNeverStringer()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.AssertNever(new MockStringer("hello")));
        Assert.Equal("Debug failure. Illegal value: hello", ex.Message);
    }

    // debug_test.go:67-72 — TestAssertNeverFallback.
    [Fact]
    public void AssertNeverFallback()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.AssertNever(42));
        Assert.Equal("Debug failure. Illegal value: 42", ex.Message);
    }

    // debug_test.go:74-77 — TestAssertTrue.
    [Fact]
    public void AssertTrue()
    {
        Debug.Assert(true);
    }

    // debug_test.go:79-82 — TestAssertTrueWithMessage.
    [Fact]
    public void AssertTrueWithMessage()
    {
        Debug.Assert(true, "this should not trigger");
    }

    // debug_test.go:84-89 — TestAssertFalseNoMessage.
    [Fact]
    public void AssertFalseNoMessage()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.Assert(false));
        Assert.Equal("Debug failure. False expression.", ex.Message);
    }

    // debug_test.go:91-96 — TestAssertFalseWithMessage.
    [Fact]
    public void AssertFalseWithMessage()
    {
        var ex = Assert.Throws<DebugFailureException>(() => Debug.Assert(false, "expected x > 0"));
        Assert.Equal("Debug failure. False expression: expected x > 0", ex.Message);
    }
}
