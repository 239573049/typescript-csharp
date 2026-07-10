// Ported from tsgo internal/collections/ordered_set.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System.Collections.Generic;

namespace Ts.Collections;

// OrderedSet an insertion ordered set.
public sealed class OrderedSet<T> where T : notnull
{
    // PORT-NOTE: Go's struct{} value type becomes byte (05-conventions.md §18).
    internal readonly OrderedMap<T, byte> m;

    public OrderedSet()
    {
        m = new OrderedMap<T, byte>();
    }

    // NewOrderedSetWithSizeHint creates a new OrderedSet with a hint for the number of elements it will contain.
    public OrderedSet(int sizeHint)
    {
        m = new OrderedMap<T, byte>(sizeHint);
    }

    private OrderedSet(OrderedMap<T, byte> m)
    {
        this.m = m;
    }

    // Add adds a value to the set.
    public void Add(T value)
    {
        m.Set(value, default);
    }

    // Has returns true if the set contains the value.
    public bool Has(T value)
    {
        return m.Has(value);
    }

    // Delete removes a value from the set.
    public bool Delete(T value)
    {
        return m.TryDelete(value, out _);
    }

    // Values returns an iterator over the values in the set.
    public IEnumerable<T> Values()
    {
        return m.Keys();
    }

    // Clear removes all elements from the set.
    // The space allocated for the set will be reused.
    public void Clear()
    {
        m.Clear();
    }

    // Size returns the number of elements in the set.
    public int Size()
    {
        return m.Size();
    }

    // Clone returns a shallow copy of the set.
    public OrderedSet<T> Clone()
    {
        return new OrderedSet<T>(m.CloneCore());
    }
}
