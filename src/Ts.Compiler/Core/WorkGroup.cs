// Ported from tsgo internal/core/workgroup.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Interface shape is fixed by plan/01-architecture.md §4.5 (IWorkGroup + WorkGroup.Create).
// PORT-NOTE: ThrottleGroup (workgroup.go:92-123) is deliberately not ported here —
// its only production consumer is project/ata (phase 8); plan/01 §4.5 keeps it
// out of the core interface.

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Ts.Core;

public interface IWorkGroup
{
    // Queue queues a function to run. It may be invoked immediately, or deferred until RunAndWait.
    // It is not safe to call Queue after RunAndWait has returned.
    void Queue(Action fn);

    // RunAndWait runs all queued functions, blocking until they have all completed.
    void RunAndWait();
}

public static class WorkGroup
{
    // Go: func NewWorkGroup(singleThreaded bool) WorkGroup
    public static IWorkGroup Create(bool singleThreaded) =>
        singleThreaded ? new SingleThreadedWorkGroup() : new ParallelWorkGroup();
}

// Go: parallelWorkGroup — Queue starts a goroutine immediately (wg.Go); RunAndWait joins.
// PORT-NOTE: goroutine-per-Queue → worker threads whose bodies run inside
// LargeStackExecutor.Run — compiler tasks (parse/bind/check/emit) recurse deeply
// and must never execute on a default ~1 MB stack (plan/01 §4.5/§4.6 red line).
// Workers (capped at Environment.ProcessorCount per group) drain a shared list,
// so functions queued by a running function are picked up too — the observable
// semantics of one-goroutine-per-Queue. Groups never share workers, so a task
// blocking on a nested group's RunAndWait cannot starve that inner group.
// PORT-NOTE: an unrecovered panic in a Go goroutine kills the process; here the
// first task exception is captured and rethrown from RunAndWait (deterministic
// join-point propagation). Remaining queued tasks still run before it rethrows.
internal sealed class ParallelWorkGroup : IWorkGroup
{
    private static readonly int maxWorkers = Math.Max(1, Environment.ProcessorCount);

    private volatile bool done; // Go: done atomic.Bool
    private readonly object gate = new object();
    private readonly List<Action?> fns = new List<Action?>();
    private int inFlight; // queued + running (Go: wg sync.WaitGroup counter); guarded by gate
    private int workers; // guarded by gate
    private ExceptionDispatchInfo? firstException; // guarded by gate

    public void Queue(Action fn)
    {
        if (done)
        {
            throw new InvalidOperationException("Queue called after RunAndWait returned");
        }

        bool spawn = false;
        lock (gate)
        {
            fns.Add(fn);
            inFlight++;
            if (workers < maxWorkers)
            {
                workers++;
                spawn = true;
            }
        }
        if (spawn)
        {
            // Carrier thread only parks in LargeStackExecutor.Run; the loop itself
            // executes on the executor's 64 MB stack thread.
            var thread = new Thread(() => LargeStackExecutor.Run(WorkerLoop))
            {
                IsBackground = true,
                Name = "ts-workgroup",
            };
            thread.Start();
        }
    }

    public void RunAndWait()
    {
        try
        {
            lock (gate)
            {
                while (inFlight != 0)
                {
                    Monitor.Wait(gate);
                }
            }
            firstException?.Throw();
        }
        finally
        {
            done = true; // Go: defer w.done.Store(true)
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            Action fn;
            lock (gate)
            {
                if (fns.Count == 0)
                {
                    // Exit under the same lock Queue uses, so Queue reliably
                    // observes the reduced worker count and respawns.
                    workers--;
                    return;
                }
                int end = fns.Count - 1;
                fn = fns[end]!;
                fns[end] = null; // Allow GC
                fns.RemoveAt(end);
            }

            try
            {
                fn();
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    firstException ??= ExceptionDispatchInfo.Capture(ex);
                }
            }
            finally
            {
                lock (gate)
                {
                    inFlight--;
                    if (inFlight == 0)
                    {
                        Monitor.PulseAll(gate);
                    }
                }
            }
        }
    }
}

// Go: singleThreadedWorkGroup — Queue only appends; RunAndWait pops from the TAIL
// (LIFO, workgroup.go:78-89) and runs inline on the calling thread, so functions
// queued during execution are drained in the same RunAndWait (fileloader's
// recursive discovery relies on this). The caller is expected to already be on a
// large-stack thread (plan/01 §4.6 entry rules E1–E7).
internal sealed class SingleThreadedWorkGroup : IWorkGroup
{
    private volatile bool done; // Go: done atomic.Bool
    private readonly Lock fnsMu = new Lock(); // Go: fnsMu sync.Mutex
    private readonly List<Action?> fns = new List<Action?>();

    public void Queue(Action fn)
    {
        if (done)
        {
            throw new InvalidOperationException("Queue called after RunAndWait returned");
        }

        lock (fnsMu)
        {
            fns.Add(fn);
        }
    }

    public void RunAndWait()
    {
        try
        {
            while (true)
            {
                Action? fn = Pop();
                if (fn == null)
                {
                    return;
                }
                fn();
            }
        }
        finally
        {
            done = true; // Go: defer w.done.Store(true)
        }
    }

    private Action? Pop()
    {
        lock (fnsMu)
        {
            if (fns.Count == 0)
            {
                return null;
            }
            int end = fns.Count - 1;
            Action fn = fns[end]!;
            fns[end] = null; // Allow GC
            fns.RemoveAt(end);
            return fn;
        }
    }
}
