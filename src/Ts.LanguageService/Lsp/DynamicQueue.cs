// Ported from tsgo internal/lsp/dynamic_queue.go (whole file).
//
// Inspired by Brian C. Mills' "Rethinking Classical Concurrency Patterns" talk:
// https://www.youtube.com/watch?v=5zXAHh5tJqQ
//
// This queue is a state machine, where each state is a channel, "idle" or "ready".
// Only one caller ever has the actual state struct at a time. The Get function
// will wait until the "ready" channel holds the state. Putting an item
// means grabbing the state from any channel, modifying it, and putting it
// back on the "ready" channel. Since this is all managed via contexts, any method
// can be cancelled while waiting for the state.
//
// PORT-NOTE (plan/05 §11): Go's two cap-1 channels holding a single state struct
// map to two nullable slots guarded by a lock plus waiter queues of
// TaskCompletionSource completions. Exactly one QueueState exists, so a release
// always finds its slot empty and a waiter hand-off is unambiguous; cancellation
// while waiting removes the waiter under the same lock (if a state was handed over
// in the interim, the hand-off wins, mirroring Go's non-deterministic select).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ts.Lsp;

// Go: dynamicQueueState[T] — items []T.
internal sealed class DynamicQueueState<T>
{
    internal readonly List<T> Items = [];
}

// Go: dynamicQueue[T any] (dynamic_queue.go:17-20). Unexported in Go; internal here
// (the LSP server and the tests are the only consumers).
internal sealed class DynamicQueue<T>
{
    private readonly Lock mu = new();

    // Go: idle/ready chan *dynamicQueueState[T] with capacity 1 — the single state
    // struct parked in either slot; null = empty channel.
    private DynamicQueueState<T>? idle;
    private DynamicQueueState<T>? ready;

    // Waiters blocked in getAny/getReady (Go: goroutines blocked in a select over
    // the channels). Completed under mu, in FIFO order.
    private readonly List<TaskCompletionSource<DynamicQueueState<T>>> anyWaiters = [];
    private readonly List<TaskCompletionSource<DynamicQueueState<T>>> readyWaiters = [];

    // Go: newDynamicQueue[T]() (dynamic_queue.go:26-33).
    public DynamicQueue()
    {
        idle = new DynamicQueueState<T>();
    }

    // dynamic_queue.go:35-48.
    public Exception? Put(CancellationToken ctx, T item)
    {
        if (ctx.IsCancellationRequested)
        {
            return new OperationCanceledException(ctx);
        }

        DynamicQueueState<T> state;
        try
        {
            state = GetAny(ctx);
        }
        catch (OperationCanceledException e)
        {
            return e;
        }

        state.Items.Add(item);
        ReleaseReady(state);
        return null;
    }

    // dynamic_queue.go:50-74.
    public (T Item, Exception? Error) Get(CancellationToken ctx)
    {
        if (ctx.IsCancellationRequested)
        {
            return (default!, new OperationCanceledException(ctx));
        }

        DynamicQueueState<T> state;
        try
        {
            state = GetReady(ctx);
        }
        catch (OperationCanceledException e)
        {
            return (default!, e);
        }

        T item = state.Items[0];
        state.Items.RemoveAt(0);

        if (state.Items.Count == 0)
        {
            ReleaseIdle(state);
        }
        else
        {
            ReleaseReady(state);
        }
        return (item, null);
    }

    // dynamic_queue.go:76-85 — getAny. Throws OperationCanceledException for Go's
    // ctx.Err() arm (plan/05 §4).
    internal DynamicQueueState<T> GetAny(CancellationToken ctx)
    {
        lock (mu)
        {
            if (idle is not null)
            {
                DynamicQueueState<T> state = idle;
                idle = null;
                return state;
            }
            if (ready is not null)
            {
                DynamicQueueState<T> state = ready;
                ready = null;
                return state;
            }
        }

        TaskCompletionSource<DynamicQueueState<T>> tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (mu)
        {
            // Re-check under the lock: a state may have been parked since.
            if (idle is not null)
            {
                DynamicQueueState<T> state = idle;
                idle = null;
                return state;
            }
            if (ready is not null)
            {
                DynamicQueueState<T> state = ready;
                ready = null;
                return state;
            }
            anyWaiters.Add(tcs);
        }
        using (CancellationTokenRegistration reg = ctx.Register(() => CancelWaiter(anyWaiters, tcs, ctx)))
        {
            try
            {
                return tcs.Task.GetAwaiter().GetResult();
            }
            finally
            {
                reg.Dispose();
            }
        }
    }

    // dynamic_queue.go:87-94 — getReady.
    internal DynamicQueueState<T> GetReady(CancellationToken ctx)
    {
        lock (mu)
        {
            if (ready is not null)
            {
                DynamicQueueState<T> state = ready;
                ready = null;
                return state;
            }
        }

        TaskCompletionSource<DynamicQueueState<T>> tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (mu)
        {
            if (ready is not null)
            {
                DynamicQueueState<T> state = ready;
                ready = null;
                return state;
            }
            readyWaiters.Add(tcs);
        }
        using (CancellationTokenRegistration reg = ctx.Register(() => CancelWaiter(readyWaiters, tcs, ctx)))
        {
            try
            {
                return tcs.Task.GetAwaiter().GetResult();
            }
            finally
            {
                reg.Dispose();
            }
        }
    }

    // Go: `q.idle <- state` in dynamic_queue_test.go — parks a caller-held state back
    // on "idle" (test-only hook; Go reaches into the channel directly).
    internal void ReleaseIdleForTesting(DynamicQueueState<T> state) => ReleaseIdle(state);

    // Park the state back on "idle" (Go: q.idle <- state), handing it straight to a
    // getAny waiter if one is blocked.
    private void ReleaseIdle(DynamicQueueState<T> state)
    {
        lock (mu)
        {
            if (anyWaiters.Count > 0)
            {
                TaskCompletionSource<DynamicQueueState<T>> tcs = anyWaiters[0];
                anyWaiters.RemoveAt(0);
                tcs.TrySetResult(state);
                return;
            }
            idle = state;
        }
    }

    // Park the state back on "ready" (Go: q.ready <- state); both getReady and getAny
    // waiters can consume it (Go's select picks arbitrarily; here getReady waits have
    // priority for determinism).
    private void ReleaseReady(DynamicQueueState<T> state)
    {
        lock (mu)
        {
            if (readyWaiters.Count > 0)
            {
                TaskCompletionSource<DynamicQueueState<T>> tcs = readyWaiters[0];
                readyWaiters.RemoveAt(0);
                tcs.TrySetResult(state);
                return;
            }
            if (anyWaiters.Count > 0)
            {
                TaskCompletionSource<DynamicQueueState<T>> tcs = anyWaiters[0];
                anyWaiters.RemoveAt(0);
                tcs.TrySetResult(state);
                return;
            }
            ready = state;
        }
    }

    // Remove the waiter and fail it with context-cancellation. Runs under mu: if the
    // waiter is no longer registered, a state was already handed over and that
    // completion wins (the caller proceeds with the state, like Go's select either-arm).
    private void CancelWaiter(
        List<TaskCompletionSource<DynamicQueueState<T>>> waiters,
        TaskCompletionSource<DynamicQueueState<T>> tcs,
        CancellationToken ctx)
    {
        lock (mu)
        {
            if (waiters.Remove(tcs))
            {
                tcs.TrySetException(new OperationCanceledException(ctx));
            }
        }
    }
}
