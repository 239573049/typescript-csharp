// Ported from tsgo internal/execute/tsc/extendedconfigcache.go.

using System.Threading;
using Ts.Collections;

namespace Ts.Execute;

using PathT = global::Ts.TsPath.Path;

// extendedconfigcache.go:15-17 — extendedConfigCache is a minimal implementation of
// tsoptions.ExtendedConfigCache. It is concurrency-safe, but stores cached entries permanently.
// This implementation should not be used for long-running processes where configuration changes
// over the course of multiple compilations.
public sealed class TscExtendedConfigCache : global::Ts.TsOptions.ExtendedConfigCache
{
    private readonly SyncMap<PathT, Entry> m = new();

    // extendedconfigcache.go:19-22
    private sealed class Entry
    {
        internal global::Ts.TsOptions.ExtendedConfigCacheEntry? entry;
        internal readonly object mu = new(); // Monitor-based: unlock happens on a different control path than lock
    }

    // extendedconfigcache.go:26-34 — GetExtendedConfig implements tsoptions.ExtendedConfigCache.
    public global::Ts.TsOptions.ExtendedConfigCacheEntry GetExtendedConfig(
        string fileName, PathT path, PathT[] resolutionStack, global::Ts.TsOptions.ParseConfigHost host)
    {
        var (entry, loaded) = LoadOrStoreNewLockedEntry(path);
        try
        {
            if (!loaded)
            {
                entry.entry = global::Ts.TsOptions.TsConfigParsing.ParseExtendedConfig(fileName, path, resolutionStack, host, this);
            }
            return entry.entry!;
        }
        finally
        {
            Monitor.Exit(entry.mu);
        }
    }

    // extendedconfigcache.go:36-45 — loadOrStoreNewLockedEntry loads an existing entry or creates a
    // new one. The returned entry's mutex is locked.
    private (Entry Entry, bool Loaded) LoadOrStoreNewLockedEntry(PathT path)
    {
        var entry = new Entry();
        Monitor.Enter(entry.mu);
        var (existing, loaded) = m.LoadOrStore(path, entry);
        if (loaded)
        {
            Monitor.Enter(existing.mu);
            Monitor.Exit(entry.mu);
            return (existing, true);
        }
        return (entry, false);
    }
}
