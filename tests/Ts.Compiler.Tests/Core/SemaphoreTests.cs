using System;
using System.Threading;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class SemaphoreTests
{
    [Fact]
    public void UnlimitedSemaphoreAlwaysAcquires()
    {
        var s = new UnlimitedSemaphore();
        Action release = s.Acquire();
        release(); // no-op, must be callable

        Assert.True(s.TryAcquire(CancellationToken.None, out Action release2));
        release2();
    }

    [Fact]
    public void LimitedSemaphoreRejectsNonPositiveConcurrency()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new LimitedSemaphore(0));
        Assert.Equal("maxConcurrency must be positive", ex.Message); // Go: panic text
    }

    [Fact]
    public void LimitedSemaphoreTryAcquireFailsWhenFullAndCancelled()
    {
        var s = new LimitedSemaphore(2);
        Action r1 = s.Acquire();
        Action r2 = s.Acquire();

        // Both slots taken: a cancelled TryAcquire must give up (Go: ctx.Done() arm).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(s.TryAcquire(cts.Token, out Action noop));
        noop(); // the failure-path release func is a no-op and must be callable

        // Releasing one slot makes TryAcquire succeed again.
        r1();
        Assert.True(s.TryAcquire(CancellationToken.None, out Action r3));
        r3();
        r2();
    }

    [Fact]
    public void LimitedSemaphoreReleaseUnblocksWaitingAcquire()
    {
        var s = new LimitedSemaphore(1);
        Action r1 = s.Acquire();

        bool acquired = false;
        var thread = new Thread(() =>
        {
            Action r = s.Acquire(); // blocks until r1() below
            acquired = true;
            r();
        });
        thread.Start();

        Assert.False(thread.Join(TimeSpan.FromMilliseconds(50))); // still blocked
        r1();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.True(acquired);
    }
}
