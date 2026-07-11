// Ported from tsgo internal/fswatch/debounce.go.
//
// The debounce state machine is pure logic (channels + timers, no platform
// syscalls) so the batching/coalescing semantics carry over 1:1. Only the
// concurrency primitives change per 05-conventions.md:
//   - Go `go d.loop()` background goroutine → a dedicated background Thread.
//     PORT-NOTE: Task.Run is banned in Ts.Compiler (Ts.Compiler.csproj banned
//     symbols); a long-lived loop is a Thread, not a pool task.
//   - Go channel-based resettable latch (waitCh/triggerCh) →
//     ManualResetEventSlim gates. `close(ch)` (broadcast wake) maps to Set();
//     replacing the channel maps to Reset()/new gate.
//   - sync.Mutex → lock (object monitor).

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.FsWatch;

// debounce batches filesystem events for one backend. Each watcher owns one
// debounce instance, created lazily on first subscribe and living for the
// process lifetime. The background thread costs nothing when idle.
//
// Per-backend (rather than process-wide) isolation means a slow user callback
// on one backend cannot starve event delivery on the others.
//
// Internally uses a resettable latch: the loop blocks until Trigger() is
// called, then coalesces for minWaitTime before firing callbacks.
internal sealed class Debounce
{
    // Go: defaultMinWaitTime = 50ms, defaultMaxWaitTime = 500ms. Package-level
    // vars in Go so tests can shrink them; kept mutable here for the same reason.
    internal static readonly TimeSpan DefaultMinWaitTime = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan DefaultMaxWaitTime = TimeSpan.FromMilliseconds(500);

    internal static TimeSpan MinWaitTime = DefaultMinWaitTime;
    internal static TimeSpan MaxWaitTime = DefaultMaxWaitTime;

    private readonly object _mu = new object();
    private readonly Dictionary<object, Action> _callbacks = new Dictionary<object, Action>(ReferenceEqualityComparer.Instance);
    private DateTime _lastTime;

    // Latch state. waitGate is the persistent gate (Set == signalled);
    // triggerGate is replaced on each trigger for timed waits. Guarded by
    // _latchMu, mirroring Go's latchMu.
    private readonly object _latchMu = new object();
    private ManualResetEventSlim _waitGate = new ManualResetEventSlim(false);
    private ManualResetEventSlim _triggerGate = new ManualResetEventSlim(false);
    private bool _notified;
    private volatile bool _closed;

    private Debounce()
    {
    }

    public static Debounce New()
    {
        var d = new Debounce();
        // go d.loop() — background thread, not a pool task (Task.Run banned).
        var t = new Thread(d.Loop)
        {
            IsBackground = true,
            Name = "fswatch-debounce",
        };
        t.Start();
        return d;
    }

    // add registers a callback under key. key is compared by reference
    // (Go used `map[any]func()` keyed by *dirWatch pointer identity).
    public void Add(object key, Action cb)
    {
        lock (_mu)
        {
            _callbacks[key] = cb;
        }
    }

    // remove deregisters the callback for key.
    public void Remove(object key)
    {
        lock (_mu)
        {
            _callbacks.Remove(key);
        }
    }

    // trigger wakes the debounce loop.
    public void Trigger()
    {
        lock (_latchMu)
        {
            if (!_notified)
            {
                _notified = true;
                _waitGate.Set(); // close(waitCh): broadcast wake
            }
            _triggerGate.Set(); // close(triggerCh)
            _triggerGate = new ManualResetEventSlim(false); // triggerCh = make(...)
        }
    }

    // Close stops the background loop. Not present in Go (goroutine lives for
    // process lifetime); added so tests can dispose the thread deterministically.
    public void Close()
    {
        _closed = true;
        lock (_latchMu)
        {
            _waitGate.Set();
            _triggerGate.Set();
        }
    }

    private void Loop()
    {
        while (!_closed)
        {
            LatchWait();
            if (_closed)
            {
                return;
            }
            NotifyIfReady();
        }
    }

    private void NotifyIfReady()
    {
        DateTime now;
        TimeSpan gap;
        lock (_mu)
        {
            now = DateTime.UtcNow;
            gap = now - _lastTime;
            if (gap > MaxWaitTime)
            {
                _lastTime = now;
                // release lock before firing
            }
        }
        if (gap > MaxWaitTime)
        {
            FireCallbacks();
            return;
        }
        CoalesceWait();
    }

    private void CoalesceWait()
    {
        ManualResetEventSlim gate;
        lock (_latchMu)
        {
            gate = _triggerGate;
        }
        // select { case <-ch: /*retriggered*/ case <-time.After(minWaitTime): fire }
        bool retriggered = gate.Wait(MinWaitTime);
        if (retriggered)
        {
            // New event triggered; fire on the next tick. Do nothing.
            return;
        }
        FireCallbacks();
    }

    // fireCallbacks snapshots and invokes all registered callbacks.
    private void FireCallbacks()
    {
        Action[] cbs;
        lock (_mu)
        {
            _lastTime = DateTime.UtcNow;
            cbs = new Action[_callbacks.Count];
            int i = 0;
            // map-iter: 序无关 — callbacks are independent per-dirWatch handlers;
            // invocation order carries no semantics (Go iterated the map directly).
            foreach (Action cb in _callbacks.Values)
            {
                cbs[i++] = cb;
            }
        }

        LatchReset();

        foreach (Action cb in cbs)
        {
            cb();
        }
    }

    // ----- latch helpers (replace signal_) ------------------------------------

    private void LatchWait()
    {
        ManualResetEventSlim gate;
        lock (_latchMu)
        {
            gate = _waitGate;
        }
        gate.Wait();
    }

    private void LatchReset()
    {
        lock (_latchMu)
        {
            if (_notified)
            {
                _notified = false;
                _waitGate = new ManualResetEventSlim(false); // waitCh = make(...)
            }
        }
    }
}
