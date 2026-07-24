// Ported from tsgo internal/project/dirty/map.go (whole file): the single-threaded dirty map.

using System;
using System.Collections.Generic;

namespace Ts.Project.Dirty;

// map.go:5-8 — MapEntry.
public sealed class MapEntry<K, V> : MapEntryBase<K, V>, DirtyValue<V>
    where K : notnull
    where V : Cloneable<V>
{
    internal Map<K, V> m = null!;

    // map.go:10-20 — Change.
    public void Change(Action<V> apply)
    {
        if (delete)
        {
            throw new InvalidOperationException("tried to change a deleted entry");
        }
        if (!dirty)
        {
            value = value.Clone();
            dirty = true;
            m.dirty[key] = this;
        }
        apply(value);
    }

    // map.go:22-31 — Replace.
    public void Replace(V newValue)
    {
        if (delete)
        {
            throw new InvalidOperationException("tried to change a deleted entry");
        }
        if (!dirty)
        {
            dirty = true;
            m.dirty[key] = this;
        }
        value = newValue;
    }

    // map.go:33-39 — ChangeIf.
    public bool ChangeIf(Func<V, bool> cond, Action<V> apply)
    {
        if (cond(Value()))
        {
            Change(apply);
            return true;
        }
        return false;
    }

    // map.go:41-47 — Delete.
    public void Delete()
    {
        if (!dirty)
        {
            m.dirty[key] = this;
        }
        delete = true;
    }

    // map.go:49-51 — Locked.
    public void Locked(Action<DirtyValue<V>> fn)
    {
        fn(this);
    }
}

// map.go:53-56 — Map.
public sealed class Map<K, V>
    where K : notnull
    where V : Cloneable<V>
{
    internal Dictionary<K, V>? baseMap; // Go: base
    internal Dictionary<K, MapEntry<K, V>> dirty;

    // map.go:58-63 — NewMap.
    public Map(Dictionary<K, V>? baseMap)
    {
        this.baseMap = baseMap;
        dirty = new Dictionary<K, MapEntry<K, V>>();
    }

    // map.go:65-84 — Get.
    public (MapEntry<K, V>? Entry, bool Ok) Get(K key)
    {
        if (dirty.TryGetValue(key, out var entry))
        {
            if (entry.delete)
            {
                return (null, false);
            }
            return (entry, true);
        }
        if (baseMap == null || !baseMap.TryGetValue(key, out var value))
        {
            return (null, false);
        }
        return (new MapEntry<K, V>
        {
            m = this,
            key = key,
            original = value,
            value = value,
            dirty = false,
        }, true);
    }

    // map.go:86-101 — Add sets a new entry in the dirty map without checking if it exists
    // in the base map. The entry added is considered dirty, so it should
    // be a fresh value, mutable until finalized (i.e., it will not be cloned
    // before changing if a change is made). If modifying an entry that may
    // exist in the base map, use `Change` instead.
    public void Add(K key, V value)
    {
        dirty[key] = new MapEntry<K, V>
        {
            m = this,
            key = key,
            value = value,
            dirty = true,
        };
    }

    // map.go:103-109 — Change.
    public void Change(K key, Action<V> apply)
    {
        var (entry, ok) = Get(key);
        if (ok)
        {
            entry!.Change(apply);
        }
        else
        {
            throw new InvalidOperationException("tried to change a non-existent entry");
        }
    }

    // map.go:111-117 — TryDelete.
    public bool TryDelete(K key)
    {
        var (entry, ok) = Get(key);
        if (ok)
        {
            entry!.Delete();
            return true;
        }
        return false;
    }

    // map.go:119-123 — Delete.
    public void Delete(K key)
    {
        if (!TryDelete(key))
        {
            throw new InvalidOperationException("tried to delete a non-existent entry");
        }
    }

    // map.go:125-146 — Range.
    public void Range(Func<MapEntry<K, V>, bool> fn)
    {
        var seenInDirty = new HashSet<K>();
        // map-iter: 序无关 (Go 迭代序随机; 调用方不得依赖序)
        foreach (var entry in dirty.Values)
        {
            seenInDirty.Add(entry.key);
            if (!entry.delete && !fn(entry))
            {
                break;
            }
        }
        // map-iter: 序无关
        foreach (var (key, value) in baseMap ?? new Dictionary<K, V>())
        {
            if (seenInDirty.Contains(key))
            {
                continue; // already processed in dirty entries
            }
            if (!fn(new MapEntry<K, V>
            {
                m = this,
                key = key,
                original = value,
                value = value,
                dirty = false,
            }))
            {
                break;
            }
        }
    }

    // map.go:148-151 — Clear.
    public void Clear()
    {
        dirty = new Dictionary<K, MapEntry<K, V>>();
        baseMap = new Dictionary<K, V>();
    }

    // map.go:153-169 — Finalize.
    public (Dictionary<K, V>? Result, bool Changed) Finalize()
    {
        if (dirty.Count == 0)
        {
            return (baseMap, false); // no changes, return base map
        }
        Dictionary<K, V> result;
        if (baseMap == null)
        {
            result = new Dictionary<K, V>(dirty.Count);
        }
        else
        {
            result = new Dictionary<K, V>(baseMap);
        }
        // map-iter: 序无关 (写入目标为字典, 与序无关)
        foreach (var (key, entry) in dirty)
        {
            if (entry.delete)
            {
                result.Remove(key);
            }
            else
            {
                result[key] = entry.value;
            }
        }
        return (result, true);
    }
}
