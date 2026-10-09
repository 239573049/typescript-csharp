// Ported from tsgo internal/project/background/queue.go (whole file).
// PORT-NOTE: Go's context.Context maps to CancellationToken throughout the C# project
// package (plan/01 §4.4); ctx.Err() != nil ⇒ !CanBeNull-token.IsCancellationRequested.

using System;
using System.Threading;

namespace Ts.Project.Background;

// queue.go:8-13 — Queue manages background tasks execution.
public sealed class Queue
{
    private readonly CountdownWaitGroup wg = new();
    private readonly ReaderWriterLockSlim mu = new();
    private bool closed;

    // queue.go:15-18 — NewQueue creates a new background queue for managing background tasks execution.
    public static Queue NewQueue()
    {
        return new Queue();
    }

    private Queue()
    {
    }

    // queue.go:20-40 — Enqueue.
    public void Enqueue(CancellationToken ctx, Action<CancellationToken> fn)
    {
        mu.EnterReadLock();
        try
        {
            if (closed)
            {
                return;
            }
        }
        finally
        {
            mu.ExitReadLock();
        }

        // Don't start new tasks if context is already cancelled
        if (ctx.IsCancellationRequested)
        {
            return;
        }

        wg.Go(() =>
        {
            // Check context again before executing
            if (ctx.IsCancellationRequested)
            {
                return;
            }
            fn(ctx);
        });
    }

    // queue.go:42-46 — Wait waits for all active tasks to complete.
    // It does not prevent new tasks from being enqueued while waiting.
    public void Wait()
    {
        wg.Wait();
    }

    // queue.go:48-52 — Close.
    public void Close()
    {
        mu.EnterWriteLock();
        try
        {
            closed = true;
        }
        finally
        {
            mu.ExitWriteLock();
        }
    }
}

// Go: sync.WaitGroup + wg.Go (queue.go uses only the Go form). A thread per task,
// tracked so Wait joins all in-flight tasks.
internal sealed class CountdownWaitGroup
{
    private int count;
    private readonly ManualResetEvent idle = new(true);

    public void Go(Action fn)
    {
        lock (idle)
        {
            count++;
            idle.Reset();
        }
        var thread = new Thread(() =>
        {
            try
            {
                fn();
            }
            finally
            {
                lock (idle)
                {
                    count--;
                    if (count == 0)
                    {
                        idle.Set();
                    }
                }
            }
        })
        {
            IsBackground = true,
            Name = "ts-background",
        };
        thread.Start();
    }

    public void Wait()
    {
        idle.WaitOne();
    }
}
