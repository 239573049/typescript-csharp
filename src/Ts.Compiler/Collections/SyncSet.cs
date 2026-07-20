// Ported from tsgo internal/collections/syncset.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;

namespace Ts.Collections;

public sealed class SyncSet<T> where T : notnull
{
    // PORT-NOTE: Go wraps SyncMap[T, struct{}]; struct{} → byte, so the carrier is
    // a ConcurrentDictionary<T, byte> (plan/10 §2.3.2; 05-conventions.md §18).
    private readonly SyncMap<T, byte> m = new();

    public bool Has(T key)
    {
        return m.TryLoad(key, out _);
    }

    public void Add(T key)
    {
        AddIfAbsent(key);
    }

    // AddIfAbsent adds the key to the set if it is not already present
    // using LoadOrStore. It returns true if the key was not already present
    // (opposite of the return value of LoadOrStore).
    public bool AddIfAbsent(T key)
    {
        var (_, loaded) = m.LoadOrStore(key, default);
        return !loaded;
    }

    // Go resets by struct reassignment (`*s = SyncSet[T]{}`); shared-reference C# clears in place.
    public void Clear()
    {
        m.Clear();
    }

    public void Delete(T key)
    {
        m.Delete(key);
    }

    public void Range(Func<T, bool> fn)
    {
        m.Range((key, _) => fn(key));
    }

    // Size returns the approximate number of items in the map.
    // Note that this is not a precise count, as the map may be modified
    // concurrently while this method is running.
    public int Size()
    {
        int count = 0;
        m.Range((_, _) =>
        {
            count++;
            return true;
        });
        return count;
    }

    public bool IsEmpty()
    {
        bool empty = true;
        m.Range((_, _) =>
        {
            empty = false;
            return false;
        });

        return empty;
    }

    public T[] ToSlice()
    {
        var arr = new List<T>(m.Size());
        m.Range((key, _) =>
        {
            arr.Add(key);
            return true;
        });
        return arr.ToArray();
    }

    public IEnumerable<T> Keys()
    {
        // PORT-NOTE: Go wraps m.Range in an iter.Seq closure (syncset.go:68-77);
        // C# delegates to SyncMap.Keys — identical weakly-consistent enumeration,
        // and consumer break replaces the yield-false early stop.
        return m.Keys();
    }
}
