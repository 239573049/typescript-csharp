// Ported from tsgo internal/project/dirty/syncmap.go (whole file): the concurrent dirty map.
// sync.Mutex → System.Threading.Lock per plan/05; Go's conditional `defer mu.Unlock()` after
// LoadOrStore becomes explicit Enter/Exit in try/finally.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project.Dirty;

// syncmap.go:10-12 — lockedEntry.
internal sealed class LockedEntry<K, V> : DirtyValue<V>
    where K : notnull
    where V : Cloneable<V>
{
    internal SyncMapEntry<K, V> e = null!;

    // syncmap.go:14-16 — Value.
    public V Value()
    {
        return e.ValueLocked();
    }

    // syncmap.go:18-20 — Original.
    public V Original()
    {
        return e.original;
    }

    // syncmap.go:22-24 — Dirty.
    public bool Dirty()
    {
        return e.dirty;
    }

    // syncmap.go:26-28 — Change.
    public void Change(Action<V> apply)
    {
        e.ChangeLocked(apply);
    }

    // syncmap.go:30-36 — ChangeIf.
    public bool ChangeIf(Func<V, bool> cond, Action<V> apply)
    {
        if (cond(e.ValueLocked()))
        {
            e.ChangeLocked(apply);
            return true;
        }
        return false;
    }

    // syncmap.go:38-40 — Delete.
    public void Delete()
    {
        e.DeleteLocked();
    }

    // syncmap.go:42-44 — Locked.
    public void Locked(Action<DirtyValue<V>> fn)
    {
        fn(this);
    }
}

// syncmap.go:46-55 — SyncMapEntry.
public sealed class SyncMapEntry<K, V> : MapEntryBase<K, V>, DirtyValue<V>
    where K : notnull
    where V : Cloneable<V>
{
    internal SyncMap<K, V> m = null!;
    internal readonly Lock mu = new();
    // proxyFor is set when this entry loses a race to become the dirty entry
    // for a value. Since two goroutines hold a reference to two entries that
    // may try to mutate the same underlying value, all mutations are routed
    // through the one that actually exists in the dirty map.
    internal SyncMapEntry<K, V>? proxyFor;

    // syncmap.go:57-64 — Value.
    public override V Value()
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                return proxyFor.Value();
            }
            return ValueLocked();
        }
    }

    // syncmap.go:66-72 — valueLocked.
    internal V ValueLocked()
    {
        if (delete)
        {
            return default!;
        }
        return value;
    }

    // syncmap.go:74-81 — Dirty.
    public override bool Dirty()
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                return proxyFor.Dirty();
            }
            return dirty;
        }
    }

    // syncmap.go:83-91 — Locked.
    public void Locked(Action<DirtyValue<V>> fn)
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                proxyFor.Locked(fn);
                return;
            }
            fn(new LockedEntry<K, V> { e = this });
        }
    }

    // syncmap.go:93-101 — Change.
    public void Change(Action<V> apply)
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                proxyFor.Change(apply);
                return;
            }
            ChangeLocked(apply);
        }
    }

    // syncmap.go:103-125 — changeLocked.
    internal void ChangeLocked(Action<V> apply)
    {
        if (dirty)
        {
            apply(value);
            return;
        }

        var (entry, loaded) = m.dirty.LoadOrStore(key, this);
        if (loaded)
        {
            entry.mu.Enter();
        }
        try
        {
            if (!entry.dirty)
            {
                entry.value = entry.value.Clone();
                entry.dirty = true;
            }
            if (loaded)
            {
                proxyFor = entry;
                value = entry.value;
                dirty = true;
                delete = entry.delete;
            }
            apply(entry.value);
        }
        finally
        {
            if (loaded)
            {
                entry.mu.Exit();
            }
        }
    }

    // syncmap.go:127-139 — ChangeIf.
    public bool ChangeIf(Func<V, bool> cond, Action<V> apply)
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                return proxyFor.ChangeIf(cond, apply);
            }

            if (cond(value))
            {
                ChangeLocked(apply);
                return true;
            }
            return false;
        }
    }

    // syncmap.go:141-161 — Delete.
    public void Delete()
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                proxyFor.Delete();
                return;
            }

            if (dirty)
            {
                delete = true;
                return;
            }
            var (entry, loaded) = m.dirty.LoadOrStore(key, this);
            if (loaded)
            {
                entry.mu.Enter();
                try
                {
                    delete = true;
                }
                finally
                {
                    entry.mu.Exit();
                }
            }
            else
            {
                entry.delete = true;
            }
        }
    }

    // syncmap.go:163-178 — deleteLocked.
    internal void DeleteLocked()
    {
        if (dirty)
        {
            delete = true;
            return;
        }
        var (entry, loaded) = m.dirty.LoadOrStore(key, this);
        if (loaded)
        {
            entry.mu.Enter();
            try
            {
                proxyFor = entry;
                value = entry.value;
                delete = true;
                dirty = entry.dirty;
                entry.delete = true;
            }
            finally
            {
                entry.mu.Exit();
            }
        }
        else
        {
            entry.delete = true;
        }
    }

    // syncmap.go:180-190 — DeleteIf.
    public void DeleteIf(Func<V, bool> cond)
    {
        lock (mu)
        {
            if (proxyFor != null)
            {
                proxyFor.DeleteIf(cond);
                return;
            }
            if (cond(value))
            {
                DeleteLocked();
            }
        }
    }
}

// syncmap.go:192-195 — SyncMap.
public sealed class SyncMap<K, V>
    where K : notnull
    where V : Cloneable<V>
{
    internal Dictionary<K, V>? baseMap; // Go: base
    internal global::Ts.Collections.SyncMap<K, SyncMapEntry<K, V>> dirty;

    // syncmap.go:197-202 — NewSyncMap.
    public SyncMap(Dictionary<K, V>? baseMap)
    {
        this.baseMap = baseMap;
        dirty = new global::Ts.Collections.SyncMap<K, SyncMapEntry<K, V>>();
    }

    // syncmap.go:204-226 — Load.
    public (SyncMapEntry<K, V>? Entry, bool Ok) Load(K key)
    {
        if (dirty.TryLoad(key, out var entry))
        {
            lock (entry.mu)
            {
                if (entry.delete)
                {
                    return (null, false);
                }
                return (entry, true);
            }
        }
        if (baseMap != null && baseMap.TryGetValue(key, out var val))
        {
            return (new SyncMapEntry<K, V>
            {
                m = this,
                key = key,
                original = val,
                value = val,
                dirty = false,
                delete = false,
            }, true);
        }
        return (null, false);
    }

    // syncmap.go:228-266 — LoadOrStore.
    public (SyncMapEntry<K, V>? Entry, bool Loaded) LoadOrStore(K key, V value)
    {
        // Check for existence in the base map first so the sync map access is atomic.
        if (baseMap != null && baseMap.TryGetValue(key, out var baseValue))
        {
            if (dirty.TryLoad(key, out var dirtyEntry))
            {
                lock (dirtyEntry.mu)
                {
                    if (dirtyEntry.delete)
                    {
                        return (null, false);
                    }
                    return (dirtyEntry, true);
                }
            }
            return (new SyncMapEntry<K, V>
            {
                m = this,
                key = key,
                original = baseValue,
                value = baseValue,
                dirty = false,
                delete = false,
            }, true);
        }
        var (entry, loaded) = dirty.LoadOrStore(key, new SyncMapEntry<K, V>
        {
            m = this,
            key = key,
            value = value,
            dirty = true,
        });
        if (loaded)
        {
            lock (entry.mu)
            {
                if (entry.delete)
                {
                    return (null, false);
                }
                return (entry, loaded);
            }
        }
        return (entry, loaded);
    }

    // syncmap.go:268-280 — Delete.
    public void Delete(K key)
    {
        V original = default!;
        if (baseMap != null)
        {
            baseMap.TryGetValue(key, out original!);
        }
        var (entry, loaded) = dirty.LoadOrStore(key, new SyncMapEntry<K, V>
        {
            m = this,
            key = key,
            original = original,
            delete = true,
        });
        if (loaded)
        {
            entry.Delete();
        }
    }

    // syncmap.go:282-307 — Range.
    public void Range(Func<SyncMapEntry<K, V>, bool> fn)
    {
        var seenInDirty = new HashSet<K>();
        dirty.Range((key, entry) =>
        {
            seenInDirty.Add(key);
            bool deleted;
            lock (entry.mu)
            {
                deleted = entry.delete;
            }
            if (!deleted && !fn(entry))
            {
                return false;
            }
            return true;
        });
        // map-iter: 序无关 (Go 迭代序随机)
        foreach (var (key, value) in baseMap ?? new Dictionary<K, V>())
        {
            if (seenInDirty.Contains(key))
            {
                continue; // already processed in dirty entries
            }
            if (!fn(new SyncMapEntry<K, V>
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

    // syncmap.go:315-354 — finalize.
    private (Dictionary<K, V>? Result, bool Changed) FinalizeWorker(FinalizationHooks<K, V> hooks)
    {
        var changed = false;
        var result = baseMap;
        void EnsureCloned()
        {
            if (!changed)
            {
                if (baseMap == null)
                {
                    result = new Dictionary<K, V>();
                }
                else
                {
                    result = new Dictionary<K, V>(baseMap);
                }
                changed = true;
            }
        }

        dirty.Range((key, entry) =>
        {
            lock (entry.mu)
            {
                if (entry.delete)
                {
                    EnsureCloned();
                    hooks.OnDelete?.Invoke(key, entry.value);
                    result!.Remove(key);
                }
                else if (entry.dirty)
                {
                    EnsureCloned();
                    if (hooks.OnChange != null || hooks.OnAdd != null)
                    {
                        if (baseMap != null && baseMap.ContainsKey(key))
                        {
                            hooks.OnChange?.Invoke(key, entry.original, entry.value);
                        }
                        else
                        {
                            hooks.OnAdd?.Invoke(key, entry.value);
                        }
                    }
                    result![key] = entry.value;
                }
                return true;
            }
        });
        return (result, changed);
    }

    // syncmap.go:356-358 — Finalize.
    public (Dictionary<K, V>? Result, bool Changed) Finalize()
    {
        return FinalizeWorker(new FinalizationHooks<K, V>());
    }

    // syncmap.go:360-362 — FinalizeWith.
    public (Dictionary<K, V>? Result, bool Changed) FinalizeWith(FinalizationHooks<K, V> hooks)
    {
        return FinalizeWorker(hooks);
    }
}

// syncmap.go:309-313 — FinalizationHooks.
public sealed class FinalizationHooks<K, V>
{
    public Action<K, V>? OnDelete;
    public Action<K, V, V>? OnChange;
    public Action<K, V>? OnAdd;
}
