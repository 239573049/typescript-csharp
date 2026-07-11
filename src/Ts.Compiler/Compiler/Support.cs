// Compiler-package support primitives ported from Go stdlib idioms used across the
// compiler package (sync.Once, and program.go's lazyValue[T]).
//
// PORT-NOTE: Ts.Core has no public sync.Once port (the checker package has an internal
// one). Per 05-conventions.md §5, `sync.Once` -> a double-checked-lock idempotent guard.

using System;
using System.Threading;

namespace Ts.Compiler;

// sync.Once — runs an action at most once, blocking concurrent callers until the first completes.
internal sealed class Once
{
    private int state; // 0 = not run, 1 = running/done
    private volatile bool done;
    private readonly Lock gate = new();

    public void Do(Action action)
    {
        if (done)
        {
            return;
        }
        lock (gate)
        {
            if (done)
            {
                return;
            }
            action();
            done = true;
            Volatile.Write(ref state, 1);
        }
    }
}

// program.go:50-71 — lazyValue[T]: compute-once cache with reuse across program updates.
// T is a reference type here (Go stores *T; C# stores T?).
internal sealed class LazyValue<T> where T : class
{
    private T? value;
    private volatile bool initialized;
    private readonly Once once = new();

    // program.go:56-64
    public T? GetValue(Func<T?> compute)
    {
        once.Do(() =>
        {
            value ??= compute();
            initialized = true;
        });
        return value;
    }

    // program.go:66-71
    public void TryReuse(LazyValue<T> from)
    {
        if (from.initialized)
        {
            value = from.value;
            initialized = true;
        }
    }
}
