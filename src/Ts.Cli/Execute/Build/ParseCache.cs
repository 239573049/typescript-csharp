// Ported from tsgo internal/execute/build/parseCache.go.

using System.Threading;
using Ts.Collections;

namespace Ts.Execute.Build;

// parseCache.go:9-12
internal sealed class ParseCacheEntry<V> where V : class?
{
    internal V? value;
    internal readonly Lock mu = new();
}

// parseCache.go:14-16 — parse-once cache: concurrent callers for the same key block on the entry
// mutex until the first parse completes. (Go V comparable with zero-value check; the C# uses are all
// reference types, so zero == null.)
internal sealed class ParseCache<K, V>
    where K : notnull
    where V : class?
{
    internal SyncMap<K, ParseCacheEntry<V>> entries = new();

    // parseCache.go:18-31
    internal V? LoadOrStore(K key, System.Func<K, V?> parse, bool allowZero)
    {
        var newEntry = new ParseCacheEntry<V>();
        lock (newEntry.mu)
        {
            var (entry, loaded) = entries.LoadOrStore(key, newEntry);
            if (loaded)
            {
                lock (entry.mu)
                {
                    if (allowZero || entry.value != null)
                    {
                        return entry.value;
                    }
                    newEntry = entry;
                    newEntry.value = parse(key);
                    return newEntry.value;
                }
            }
            newEntry.value = parse(key);
            return newEntry.value;
        }
    }

    // parseCache.go:33-35
    internal void Store(K key, V? value) => entries.Store(key, new ParseCacheEntry<V> { value = value });

    // parseCache.go:37-39
    internal void Delete(K key) => entries.Delete(key);

    // parseCache.go:41-43
    internal void Reset() => entries = new SyncMap<K, ParseCacheEntry<V>>();
}
