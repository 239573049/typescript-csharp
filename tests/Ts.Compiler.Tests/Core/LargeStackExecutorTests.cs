using System;
using System.Runtime.CompilerServices;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class LargeStackExecutorTests
{
    // Each frame keeps several locals alive so the JIT cannot shrink it to nothing;
    // 200k frames comfortably overflows a default 1 MB stack but fits in 64 MB.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long DeepRecurse(int depth, long a, long b, long c)
    {
        if (depth == 0)
        {
            return a + b + c;
        }
        long r = DeepRecurse(depth - 1, a + 1, b + 2, c + 3);
        return r - 1;
    }

    [Fact]
    public void RunsDeepRecursionThatWouldOverflowTheDefaultStack()
    {
        long result = LargeStackExecutor.Run(() => DeepRecurse(200_000, 0, 0, 0));
        Assert.Equal(200_000L * 6 - 200_000, result);
    }

    [Fact]
    public void ReturnsResult()
    {
        Assert.Equal(42, LargeStackExecutor.Run(() => 42));
    }

    [Fact]
    public void PropagatesExceptionsWithOriginalType()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            LargeStackExecutor.Run<int>(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", thrown.Message);
    }

    [Fact]
    public void NestedRunReusesTheLargeStackThread()
    {
        int outerThread = -1;
        int innerThread = -2;
        LargeStackExecutor.Run(() =>
        {
            outerThread = Environment.CurrentManagedThreadId;
            LargeStackExecutor.Run(() =>
            {
                innerThread = Environment.CurrentManagedThreadId;
            });
        });
        Assert.Equal(outerThread, innerThread);
    }

    [Fact]
    public void ActionOverloadRuns()
    {
        bool ran = false;
        LargeStackExecutor.Run(() => { ran = true; });
        Assert.True(ran);
    }
}
