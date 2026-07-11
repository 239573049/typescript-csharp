// Port shim for Go's testing.TB error surface used by internal/testutil/baseline.
//
// Go's baseline.Run / writeComparison take a *testing.T and report failures with
// t.Error(f) (record + continue) and t.Fatal(f) (record + abort the test goroutine).
// xUnit v3 has no soft-assert, so this interface mirrors those two behaviours and the
// concrete xUnit wiring is deferred to phase 4 (compiler_runner, when Program lands).
// plan/08-testing-strategy.md §4.1: "gotest.tools assert.Check (non-fatal) -> 收集式断言".

using System;
using System.Collections.Generic;

namespace Ts.TestUtil;

// Go: the subset of testing.TB that baseline.go actually uses.
public interface IBaselineTestContext
{
    // Go: t.Error / t.Errorf — record a failure and continue.
    void Error(string message);

    // Go: t.Fatal / t.Fatalf — record a failure and abort. Implementations must not return
    // normally (they throw); annotated so the caller's control flow matches Go's goroutine
    // abort.
    void Fatal(string message);
}

// Thrown by BaselineTestContext.Fatal to unwind like Go's t.Fatal.
public sealed class BaselineFatalException : Exception
{
    public BaselineFatalException(string message)
        : base(message)
    {
    }
}

// Default collecting implementation: accumulates Error() calls (faithful to Go's
// "record and continue") and throws on Fatal(). Phase-4 wiring inspects Errors after
// Run and fails the xUnit test if any were recorded.
public sealed class BaselineTestContext : IBaselineTestContext
{
    public List<string> Errors { get; } = new();

    public bool Failed => Errors.Count > 0;

    public void Error(string message) => Errors.Add(message);

    public void Fatal(string message)
    {
        Errors.Add(message);
        throw new BaselineFatalException(message);
    }
}
