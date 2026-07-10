// Ported from tsgo internal/collections/ordered_map.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Ts.Collections;

// OrderedMap is an insertion ordered map.
//
// PORT-NOTE: Go embeds a `noCopy` vet marker (ordered_map.go:17,27-31) because the
// struct must not be copied after first use; C# classes have reference semantics,
// so the marker and its Lock/Unlock no-ops are dropped.
public sealed class OrderedMap<K, V> where K : notnull
{
    internal readonly List<K> keys;
    internal readonly Dictionary<K, V> mp;

    // PORT-NOTE: Go's zero value carries nil keys/mp, lazily initialized in Set
    // (ordered_map.go:61-63); C# initializes both in the constructor — the
    // observable behavior is identical (05-conventions.md §7.2).
    public OrderedMap()
    {
        keys = new List<K>();
        mp = new Dictionary<K, V>();
    }

    // NewOrderedMapWithSizeHint creates a new OrderedMap with a hint for the number of elements it will contain.
    public OrderedMap(int sizeHint)
    {
        keys = new List<K>(sizeHint);
        mp = new Dictionary<K, V>(sizeHint);
    }

    private OrderedMap(List<K> keys, Dictionary<K, V> mp)
    {
        this.keys = keys;
        this.mp = mp;
    }

    // Set sets a key-value pair in the map.
    public void Set(K key, V value)
    {
        if (!mp.ContainsKey(key))
        {
            keys.Add(key);
        }
        mp[key] = value;
    }

    // Get retrieves a value from the map.
    public bool TryGet(K key, [MaybeNullWhen(false)] out V value)
    {
        return mp.TryGetValue(key, out value);
    }

    // GetOrZero retrieves a value from the map, or returns the zero value of the value type if the key is not present.
    public V? GetOrZero(K key)
    {
        return mp.GetValueOrDefault(key);
    }

    // EntryAt retrieves the key-value pair at the specified index.
    public bool TryEntryAt(int index, [MaybeNullWhen(false)] out K key, [MaybeNullWhen(false)] out V value)
    {
        if (index < 0 || index >= keys.Count)
        {
            key = default;
            value = default;
            return false;
        }

        key = keys[index];
        value = mp[key];
        return true;
    }

    // Has returns true if the map contains the key.
    public bool Has(K key)
    {
        return mp.ContainsKey(key);
    }

    // Delete removes a key-value pair from the map.
    public bool TryDelete(K key, [MaybeNullWhen(false)] out V value)
    {
        if (!mp.TryGetValue(key, out value))
        {
            return false;
        }

        mp.Remove(key);
        int i = keys.IndexOf(key);
        // If we're just removing the first or last element, avoid shifting everything around.
        // PORT-NOTE: Go's first/last fast paths re-slice the backing array in O(1)
        // (ordered_map.go:112-122); List<T>.RemoveAt clears the vacated slot itself,
        // so all three Go branches collapse into one call with identical observable
        // insertion order.
        keys.RemoveAt(i);

        return true;
    }

    // Clear removes all key-value pairs from the map.
    // The space allocated for the map will be reused.
    public void Clear()
    {
        keys.Clear();
        mp.Clear();
    }

    // Go's unexported clone (ordered_map.go:208-213); the exported nil-tolerant
    // Clone lives in OrderedMapExtensions (05-conventions.md §1.1 Core-suffix rule).
    internal OrderedMap<K, V> CloneCore()
    {
        return new OrderedMap<K, V>(new List<K>(keys), new Dictionary<K, V>(mp));
    }
}

public struct MapEntry<K, V>
{
    public K Key;
    public V Value;

    public MapEntry(K key, V value)
    {
        Key = key;
        Value = value;
    }
}

// Nil-tolerant receivers (Go methods that begin with `if m == nil`) become
// extension methods on OrderedMap<K, V>? per 05-conventions.md §10.3.
public static class OrderedMapExtensions
{
    // Keys returns an iterator over the keys in the map.
    // A slice of the keys can be obtained by calling `slices.Collect`.
    public static IEnumerable<K> Keys<K, V>(this OrderedMap<K, V>? m) where K : notnull
    {
        if (m is null)
        {
            yield break;
        }

        // We use a for loop here to ensure we enumerate new items added during iteration.
        for (int i = 0; i < m.keys.Count; i++)
        {
            yield return m.keys[i];
        }
    }

    // Values returns an iterator over the values in the map.
    // A slice of the values can be obtained by calling `slices.Collect`.
    public static IEnumerable<V> Values<K, V>(this OrderedMap<K, V>? m) where K : notnull
    {
        if (m is null)
        {
            yield break;
        }

        // We use a for loop here to ensure we enumerate new items added during iteration.
        for (int i = 0; i < m.keys.Count; i++)
        {
            yield return m.mp[m.keys[i]];
        }
    }

    // Entries returns an iterator over the key-value pairs in the map.
    public static IEnumerable<KeyValuePair<K, V>> Entries<K, V>(this OrderedMap<K, V>? m) where K : notnull
    {
        if (m is null)
        {
            yield break;
        }

        // We use a for loop here to ensure we enumerate new items added during iteration.
        for (int i = 0; i < m.keys.Count; i++)
        {
            var key = m.keys[i];
            yield return new KeyValuePair<K, V>(key, m.mp[key]);
        }
    }

    // Size returns the number of key-value pairs in the map.
    public static int Size<K, V>(this OrderedMap<K, V>? m) where K : notnull
    {
        if (m is null)
        {
            return 0;
        }

        return m.keys.Count;
    }

    // Clone returns a shallow copy of the map.
    public static OrderedMap<K, V>? Clone<K, V>(this OrderedMap<K, V>? m) where K : notnull
    {
        if (m is null)
        {
            return null;
        }

        return m.CloneCore();
    }
}

// Package-level functions of ordered_map.go (05-conventions.md §2.1: static class
// named after the file).
public static class OrderedMap
{
    // Go: NewOrderedMapFromList (Go-style New prefix dropped, 05-conventions.md §0.3).
    public static OrderedMap<K, V> FromList<K, V>(MapEntry<K, V>[] items) where K : notnull
    {
        var mp = new OrderedMap<K, V>(items.Length);
        foreach (var item in items)
        {
            mp.Set(item.Key, item.Value);
        }
        return mp;
    }

    // TODO(csport): MarshalJSONTo / resolveKeyName / UnmarshalJSONFrom
    // (ordered_map.go:215-293) are deferred in this wave — plan/10 §2.2 wave 3:
    // "collections 全包（叶包，ordered_map 的 JSON 面推迟）". Their consumption points
    // (showconfig/buildinfo) are taken over by hand-written Utf8JsonWriter emitters
    // (plan/10 §2.3.2 ordered_map row; plan/07 §4). resolveKeyName's reflection is
    // AOT-banned and must not be ported as-is.

    public static void DiffOrderedMaps<K, V>(
        OrderedMap<K, V> m1,
        OrderedMap<K, V> m2,
        Action<K, V> onAdded,
        Action<K, V> onRemoved,
        Action<K, V, V> onModified)
        where K : notnull
    {
        // Go constrains V to comparable and uses ==; EqualityComparer<T>.Default is
        // the matching translation (05-conventions.md §6.2).
        DiffOrderedMapsFunc(m1, m2, static (a, b) => EqualityComparer<V>.Default.Equals(a, b), onAdded, onRemoved, onModified);
    }

    public static void DiffOrderedMapsFunc<K, V>(
        OrderedMap<K, V> m1,
        OrderedMap<K, V> m2,
        Func<V, V, bool> equalValues,
        Action<K, V> onAdded,
        Action<K, V> onRemoved,
        Action<K, V, V> onModified)
        where K : notnull
    {
        foreach (var (k, v2) in m2.Entries())
        {
            if (!m1.TryGet(k, out _))
            {
                onAdded(k, v2);
            }
        }
        foreach (var (k, v1) in m1.Entries())
        {
            if (m2.TryGet(k, out var v2))
            {
                if (!equalValues(v1, v2))
                {
                    onModified(k, v1, v2);
                }
            }
            else
            {
                onRemoved(k, v1);
            }
        }
    }
}
