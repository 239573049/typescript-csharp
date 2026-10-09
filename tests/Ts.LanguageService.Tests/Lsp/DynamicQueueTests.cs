// Ported from tsgo internal/lsp/dynamic_queue_test.go (whole file).
//
// PORT-NOTE: the third test reaches into Go's unexported channels directly
// (`state, err := q.getAny(...)`, `q.idle <- state` — same-package access); here it
// uses the internal GetAny/ReleaseIdleForTesting equivalents (InternalsVisibleTo).

using System;
using System.Threading;
using Xunit;

namespace Ts.Lsp.Tests;

public sealed class DynamicQueueTests
{
    [Fact]
    public void Fifo()
    {
        var ctx = new CancellationTokenSource().Token;
        var q = new DynamicQueue<int>();

        for (int i = 0; i < 1000; i++)
        {
            var err = q.Put(ctx, i);
            Assert.Null(err);
        }

        for (int i = 0; i < 1000; i++)
        {
            var (got, err) = q.Get(ctx);
            Assert.Null(err);
            Assert.Equal(i, got);
        }
    }

    [Fact]
    public void GetCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ctx = cancelled.Token;

        var q = new DynamicQueue<int>();
        var (got, err) = q.Get(ctx);
        Assert.IsType<OperationCanceledException>(err);
        Assert.Equal(0, got);
    }

    [Fact]
    public void PutCancellationWhileStateUnavailable()
    {
        var testCtx = new CancellationTokenSource().Token;
        var q = new DynamicQueue<int>();

        // Go: state, err := q.getAny(t.Context()) — the state is now held by the test.
        var state = q.GetAny(testCtx);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var ctx = cancelled.Token;

        var putErr = q.Put(ctx, 1);
        Assert.IsType<OperationCanceledException>(putErr);

        // Go: q.idle <- state.
        q.ReleaseIdleForTesting(state);

        var err2 = q.Put(testCtx, 2);
        Assert.Null(err2);
        var (got, err3) = q.Get(testCtx);
        Assert.Null(err3);
        Assert.Equal(2, got);
    }
}
