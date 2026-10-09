// Ported from tsgo internal/project/ownercache.go (whole file).

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project;

// ownercache.go:7-11 — ownerCacheEntry.
internal sealed class OwnerCacheEntry<V>
{
    internal readonly Lock mu = new();
    internal V value = default!;
    internal HashSet<ulong> owners = [];
}

// OwnerCache is like RefCountCache, but each entry tracks the set of its
// owners instead of a count. We use this to associate extended config cache
// entries with each snapshot that contains them, since the same config can
// be Acquired multiple times during config parsing while only appearing once in
// the ParsedCommandLine's list of extended files. When updating this code, check
// if the same changes should be made to RefCountCache as well.
public class OwnerCache<K, V, LoadArgs>
    where K : notnull
{
    internal readonly global::Ts.Collections.SyncMap<K, OwnerCacheEntry<V>> entries = new();

    private readonly Func<K, V, LoadArgs, bool>? isExpired;
    private readonly Func<K, LoadArgs, V> parse;

    // ownercache.go:30-39 — NewOwnerCache.
    public static OwnerCache<K, V, LoadArgs> NewOwnerCache(
        Func<K, LoadArgs, V> parse,
        Func<K, V, LoadArgs, bool>? isExpired)
    {
        return new OwnerCache<K, V, LoadArgs>(parse, isExpired);
    }

    protected OwnerCache(Func<K, LoadArgs, V> parse, Func<K, V, LoadArgs, bool>? isExpired)
    {
        this.isExpired = isExpired;
        this.parse = parse;
    }

    // ownercache.go:41-49 — LoadAndAcquire.
    public V LoadAndAcquire(K identity, ulong owner, LoadArgs loadArgs)
    {
        (OwnerCacheEntry<V> entry, bool loaded) = LoadOrStoreLockedEntry(identity);
        try
        {
            if (!loaded || (isExpired is not null && isExpired(identity, entry.value, loadArgs)))
            {
                entry.value = parse(identity, loadArgs);
            }
            entry.owners.Add(owner);
            return entry.value;
        }
        finally
        {
            entry.mu.Exit();
        }
    }

    // ownercache.go:51-58 — Acquire.
    public void Acquire(K identity, ulong owner, V value)
    {
        (OwnerCacheEntry<V> entry, bool loaded) = LoadOrStoreLockedEntry(identity);
        try
        {
            if (!loaded)
            {
                entry.value = value;
            }
            entry.owners.Add(owner);
        }
        finally
        {
            entry.mu.Exit();
        }
    }

    // AddOwner adds an owner to an existing live entry. The entry must exist
    // and have at least one current owner; callers must ensure the entry is
    // kept alive (e.g. via snapshot ref counting).
    public void AddOwner(K identity, ulong owner)
    {
        if (!entries.TryLoad(identity, out OwnerCacheEntry<V>? value))
        {
            throw new InvalidOperationException("OwnerCache.AddOwner: entry not found");
        }
        lock (value.mu)
        {
            if (value.owners.Count == 0)
            {
                throw new InvalidOperationException("OwnerCache.AddOwner: entry has no owners");
            }
            value.owners.Add(owner);
        }
    }

    // ownercache.go:73-76 — Has.
    public bool Has(K identity)
    {
        return entries.TryLoad(identity, out _);
    }

    // ownercache.go:78-89 — Release.
    public void Release(K identity, ulong owner)
    {
        if (!entries.TryLoad(identity, out OwnerCacheEntry<V>? value))
        {
            return;
        }
        lock (value.mu)
        {
            value.owners.Remove(owner);
            if (value.owners.Count == 0)
            {
                entries.Delete(identity);
            }
        }
    }

    // ownercache.go:91-113 — loadOrStoreLockedEntry.
    private (OwnerCacheEntry<V> Entry, bool Loaded) LoadOrStoreLockedEntry(K key)
    {
        var entry = new OwnerCacheEntry<V> { owners = [] };
        entry.mu.Enter();
        (OwnerCacheEntry<V> existingEntry, bool loaded) = entries.LoadOrStore(key, entry);
        if (loaded)
        {
            entry.mu.Exit();
            OwnerCacheEntry<V> existing = existingEntry;
            existing.mu.Enter();
            if (existing.owners.Count == 0)
            {
                existing.mu.Exit();
                return LoadOrStoreLockedEntry(key);
            }
            return (existing, true);
        }
        return (entry, false);
    }
}
