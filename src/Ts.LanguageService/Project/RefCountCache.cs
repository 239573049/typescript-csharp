// Ported from tsgo internal/project/refcountcache.go (whole file).
// PORT-NOTE: like Go, RefCountCache keeps its own per-entry mutex on top of the
// dirty.SyncMap storage; the dirty primitives are used only for the outer map.

using System;
using System.Threading;

namespace Ts.Project;

// refcountcache.go:5-9 — refCountCacheEntry.
internal sealed class RefCountCacheEntry<V>
{
    internal readonly Lock mu = new();
    internal V value = default!;
    internal int refCount;
}

// refcountcache.go:11-16 — RefCountCacheOptions.
public sealed class RefCountCacheOptions
{
    // DisableDeletion prevents entries from being removed from the cache.
    // Used for testing.
    public bool DisableDeletion { get; init; }
}

// refcountcache.go:18-26 — RefCountCache.
public sealed class RefCountCache<K, V, AcquireArgs>
    where K : notnull
{
    public RefCountCacheOptions Options { get; } = new();

    // internal: session.go's logCacheStats ranges over it (Go same-package access).
    internal readonly global::Ts.Collections.SyncMap<K, RefCountCacheEntry<V>> entries = new();

    private readonly Func<K, AcquireArgs, V> parse;

    // refcountcache.go:28-36 — NewRefCountCache.
    public static RefCountCache<K, V, AcquireArgs> NewRefCountCache(
        RefCountCacheOptions options,
        Func<K, AcquireArgs, V> parse)
    {
        return new RefCountCache<K, V, AcquireArgs>(options, parse);
    }

    private RefCountCache(RefCountCacheOptions options, Func<K, AcquireArgs, V> parse)
    {
        Options = options;
        this.parse = parse;
    }

    // Acquire retrieves or creates a cache entry for the given identity and hash.
    // If an entry exists with matching identity and hash, its refcount is incremented
    // and the cached value is returned. Otherwise, parse() is called to create the
    // value, which is stored and returned with refcount 1.
    //
    // The caller is responsible for calling Deref when done with the value.
    public V Acquire(K identity, AcquireArgs acquireArgs)
    {
        (RefCountCacheEntry<V> entry, bool loaded) = LoadOrStoreNewLockedEntry(identity);
        try
        {
            if (!loaded)
            {
                // New entry - parse the value.
                entry.value = parse(identity, acquireArgs);
                return entry.value;
            }
            return entry.value;
        }
        finally
        {
            entry.mu.Exit();
        }
    }

    // refcountcache.go:60-63 — Has.
    public bool Has(K identity)
    {
        return entries.TryLoad(identity, out _);
    }

    // Ref increments the reference count for an existing entry.
    // Panics if the entry does not exist.
    public void Ref(K identity)
    {
        if (!entries.TryLoad(identity, out RefCountCacheEntry<V>? value))
        {
            throw new InvalidOperationException("cache entry not found");
        }
        lock (value.mu)
        {
            if (value.refCount <= 0 && !Options.DisableDeletion)
            {
                // Entry was deleted while we were acquiring the lock.
                (RefCountCacheEntry<V> newEntry, bool _) = LoadOrStoreNewLockedEntry(identity);
                try
                {
                    newEntry.value = value.value;
                }
                finally
                {
                    newEntry.mu.Exit();
                }
                return;
            }
            value.refCount++;
        }
    }

    // Deref decrements the reference count for an entry.
    // When the refcount reaches zero, the entry is removed from the cache
    // (unless DisableDeletion is set).
    public void Deref(K identity)
    {
        if (!entries.TryLoad(identity, out RefCountCacheEntry<V>? value))
        {
            return;
        }
        lock (value.mu)
        {
            value.refCount--;
            if (value.refCount <= 0 && !Options.DisableDeletion)
            {
                entries.Delete(identity);
            }
        }
    }

    // loadOrStoreNewLockedEntry loads an existing entry or creates a new one.
    // The returned entry's mutex is locked and its refCount is incremented
    // (or initialized to 1 in the case of a new entry).
    private (RefCountCacheEntry<V> Entry, bool Loaded) LoadOrStoreNewLockedEntry(K key)
    {
        var entry = new RefCountCacheEntry<V> { refCount = 1 };
        entry.mu.Enter();
        (RefCountCacheEntry<V> existingEntry, bool loaded) = entries.LoadOrStore(key, entry);
        if (loaded)
        {
            entry.mu.Exit();
            RefCountCacheEntry<V> existing = existingEntry;
            existing.mu.Enter();
            if (existing.refCount <= 0 && !Options.DisableDeletion)
            {
                // Existing entry was deleted while we were acquiring the lock.
                existing.mu.Exit();
                return LoadOrStoreNewLockedEntry(key);
            }
            existing.refCount++;
            return (existing, true);
        }
        return (entry, false);
    }
}
