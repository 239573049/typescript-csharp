// Ported from tsgo internal/collections/multimap.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;

namespace Ts.Collections;

public sealed class MultiMap<K, V> where K : notnull
{
    // Go: M map[K][]V — the exported backing map, nil until first Add
    // ([]V → List<V> for the append-heavy value slices, 05-conventions.md §6.3).
    public Dictionary<K, List<V>>? M;

    public MultiMap()
    {
    }

    // NewMultiMapWithSizeHint creates a new MultiMap with a hint for the number of keys it will contain.
    public MultiMap(int sizeHint)
    {
        M = new Dictionary<K, List<V>>(sizeHint);
    }

    public bool Has(K key)
    {
        return M is not null && M.ContainsKey(key);
    }

    public List<V>? Get(K key)
    {
        // Go returns the (possibly nil) value slice; missing key → null.
        return M?.GetValueOrDefault(key);
    }

    public void Add(K key, V value)
    {
        M ??= new Dictionary<K, List<V>>();
        if (!M.TryGetValue(key, out var values))
        {
            values = new List<V>();
            M[key] = values;
        }
        values.Add(value);
    }

    public void Remove(K key, V value)
    {
        if (M is not null && M.TryGetValue(key, out var values))
        {
            int i = values.IndexOf(value);
            if (i >= 0)
            {
                if (values.Count == 1)
                {
                    M.Remove(key);
                }
                else
                {
                    // Go: values = append(values[:i], values[i+1:]...) — in-place removal.
                    values.RemoveAt(i);
                }
            }
        }
    }

    public void RemoveAll(K key)
    {
        M?.Remove(key);
    }

    public int Len()
    {
        return M?.Count ?? 0;
    }

    // Go returns maps.Keys/maps.Values iterators whose order is random; Dictionary
    // enumeration order is deterministic-but-uncontractual — consumers must carry
    // the map-iter annotation (05-conventions.md §7.3).
    public IEnumerable<K> Keys()
    {
        return (IEnumerable<K>?)M?.Keys ?? [];
    }

    public IEnumerable<List<V>> Values()
    {
        return (IEnumerable<List<V>>?)M?.Values ?? [];
    }

    public void Clear()
    {
        // Go: clear on a nil map is a no-op.
        M?.Clear();
    }
}

// Package-level functions of multimap.go (05-conventions.md §2.1).
public static class MultiMap
{
    public static MultiMap<K, V> GroupBy<K, V>(V[]? items, Func<V, K> groupId) where K : notnull
    {
        var m = new MultiMap<K, V>();
        // Go: for range over a nil slice is zero iterations.
        if (items is not null)
        {
            foreach (var item in items)
            {
                m.Add(groupId(item), item);
            }
        }
        return m;
    }
}
