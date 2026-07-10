// Ported from tsgo internal/core/semaphore.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Go's chan-struct{} semaphore → SemaphoreSlim wrapper; TryAcquire(ctx) takes a
// CancellationToken (plan/10 §2.3.1). Note that TryAcquire, like the Go select,
// BLOCKS until either a slot is acquired or the token is cancelled — it is not a
// non-blocking probe.

using System;
using System.Threading;

namespace Ts.Core;

// Go: type Semaphore interface — regular behavioral interface, I prefix (05 §1.3).
public interface ISemaphore
{
    // Go: Acquire() (release func())
    Action Acquire();

    // Go: TryAcquire(ctx) (release func(), acquired bool) → Try pattern (05 §4.1).
    bool TryAcquire(CancellationToken cancellationToken, out Action release);
}

public readonly struct UnlimitedSemaphore : ISemaphore
{
    private static readonly Action noopRelease = static () => { };

    public Action Acquire()
    {
        return noopRelease; // Go: func() {}
    }

    public bool TryAcquire(CancellationToken cancellationToken, out Action release)
    {
        release = noopRelease;
        return true;
    }
}

public sealed class LimitedSemaphore : ISemaphore
{
    private static readonly Action noopRelease = static () => { };

    private readonly SemaphoreSlim ch; // Go: ch chan struct{} (capacity maxConcurrency)
    private readonly Action release;

    // Go: func NewLimitedSemaphore(maxConcurrency int) *LimitedSemaphore
    public LimitedSemaphore(int maxConcurrency)
    {
        if (maxConcurrency <= 0)
        {
            throw new InvalidOperationException("maxConcurrency must be positive");
        }
        ch = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        release = () => ch.Release(); // Go: s.release = func() { <-s.ch } — one shared delegate
    }

    public Action Acquire()
    {
        ch.Wait(); // Go: s.ch <- struct{}{}
        return release;
    }

    public bool TryAcquire(CancellationToken cancellationToken, out Action release)
    {
        // Go: select { case s.ch <- struct{}{}: ...; case <-ctx.Done(): ... }
        // PORT-NOTE: with an already-cancelled token Go's select picks a ready arm
        // at random; SemaphoreSlim.Wait(ct) deterministically observes cancellation
        // first. Callers cannot rely on the Go behavior (it is nondeterministic).
        try
        {
            ch.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            release = noopRelease;
            return false;
        }
        release = this.release;
        return true;
    }
}
