// Ported from tsgo internal/collections/syncmap.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Ts.Collections;

// Go: a generic wrapper over sync.Map; ported onto ConcurrentDictionary with the
// Go API shape preserved (plan/10 §2.3.2; 05-conventions.md §7.5/§11).
//
// PORT-NOTE: Go's `_ [0]K; _ [0]V` phantom fields only force the type parameters
// to be used; C# generics are real — dropped. The `val == nil → zero value`
// normalization in Load/LoadOrStore/Range also dissolves: the dictionary is typed,
// so a stored null V round-trips as (null, true) directly.
public sealed class SyncMap<K, V> where K : notnull
{
    private readonly ConcurrentDictionary<K, V> m = new();

    // Go: Load(key) (value V, ok bool) → Try pattern (05-conventions.md §4.1).
    public bool TryLoad(K key, [MaybeNullWhen(false)] out V value)
    {
        return m.TryGetValue(key, out value);
    }

    public void Store(K key, V value)
    {
        m[key] = value;
    }

    // Go: LoadOrStore(key, value) (actual, loaded) — loaded is not a failure bit,
    // so this stays a tuple return (05-conventions.md §4.1 row 2). Matching
    // sync.Map semantics: returns the existing value if present (loaded == true),
    // otherwise stores and returns the given value (loaded == false).
    public (V Actual, bool Loaded) LoadOrStore(K key, V value)
    {
        while (true)
        {
            if (m.TryGetValue(key, out var existing))
            {
                return (existing, true);
            }
            if (m.TryAdd(key, value))
            {
                return (value, false);
            }
        }
    }

    public void Delete(K key)
    {
        m.TryRemove(key, out _);
    }

    public void Clear()
    {
        m.Clear();
    }

    public void Range(Func<K, V, bool> f)
    {
        // map-iter: 序无关 —— sync.Map.Range 的枚举序无契约；ConcurrentDictionary
        // 枚举同为弱一致快照，语义等价（05-conventions.md §7.5）。
        foreach (var (key, value) in m)
        {
            if (!f(key, value))
            {
                break;
            }
        }
    }

    // Size returns the approximate number of items in the map.
    // Note that this is not a precise count, as the map may be modified
    // concurrently while this method is running.
    public int Size()
    {
        int count = 0;
        // map-iter: 序无关 —— 纯计数
        foreach (var _ in m)
        {
            count++;
        }
        return count;
    }

    public Dictionary<K, V> ToMap()
    {
        var result = new Dictionary<K, V>(Size());
        // map-iter: 序无关 —— 填另一张 map
        foreach (var (key, value) in m)
        {
            result[key] = value;
        }
        return result;
    }

    public IEnumerable<K> Keys()
    {
        // map-iter: 序无关 —— 消费者持有与 Range 相同的弱一致枚举
        foreach (var (key, _) in m)
        {
            yield return key;
        }
    }

    public SyncMap<K, V> Clone()
    {
        var clone = new SyncMap<K, V>();
        // map-iter: 序无关 —— 填另一张 map
        foreach (var (key, value) in m)
        {
            clone.m[key] = value;
        }
        return clone;
    }
}
