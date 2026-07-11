using System;
using System.Collections.Concurrent;

namespace Ts.Core;

// PORT-NOTE (plan/01-architecture.md §5.2): replacement for Go's sync.Pool. Go's sync.Pool
// clears on GC and is per-P sharded; this pool uses a ConcurrentQueue with a retention cap.
// The difference is purely performance, not correctness, PROVIDED the reset delegate fully
// restores the object (see plan §5.2: the reset must be reviewed against Go's put function).
// Microsoft.Extensions.ObjectPool is banned (plan/00 decision); this is the hand-written host.
public sealed class ObjectPool<T> where T : class
{
    private readonly Func<T> _factory;
    private readonly Action<T>? _reset;
    private readonly int _maxRetained;
    private readonly ConcurrentQueue<T> _items = new();
    private int _count;

    // maxRetained == -1 => 2×CPU (a suggested value, not a gate; plan/01 §5.2).
    public ObjectPool(Func<T> factory, Action<T>? reset = null, int maxRetained = -1)
    {
        _factory = factory;
        _reset = reset;
        _maxRetained = maxRetained < 0 ? 2 * Environment.ProcessorCount : maxRetained;
    }

    public T Get()
    {
        if (_items.TryDequeue(out T? item))
        {
            System.Threading.Interlocked.Decrement(ref _count);
            return item;
        }
        return _factory();
    }

    // Resets the item (mirroring Go's putX) and returns it to the pool, up to the retention cap.
    public void Return(T item)
    {
        _reset?.Invoke(item);
        if (System.Threading.Interlocked.Increment(ref _count) <= _maxRetained)
        {
            _items.Enqueue(item);
        }
        else
        {
            System.Threading.Interlocked.Decrement(ref _count);
        }
    }

    // using-pattern rental, mirroring the Go `p := getX(); defer putX(p)` idiom.
    public Rental Rent(out T item)
    {
        item = Get();
        return new Rental(this, item);
    }

    public readonly ref struct Rental
    {
        private readonly ObjectPool<T> _pool;
        private readonly T _item;

        internal Rental(ObjectPool<T> pool, T item)
        {
            _pool = pool;
            _item = item;
        }

        public void Dispose() => _pool.Return(_item);
    }
}
