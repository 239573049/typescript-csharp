// Ported from tsgo internal/execute/watchmanager/watchmanager.go.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ts.FsWatch;

namespace Ts.Execute.WatchManagement;

// watchmanager.go:15-18
internal sealed class WatchedDir
{
    internal IWatch? closer;
    internal bool recursive;
}

// watchmanager.go:20-23
internal struct DirWatchUpdate
{
    internal string dir;
    internal bool recursive;
}

// watchmanager.go:25-48 — WatchManager manages fswatch directory watches, event accumulation,
// and DoCycle signaling. It is shared by the CLI watcher and the build mode orchestrator.
//
// Locking contract:
//   - Call Lock/Unlock around the entire DoCycle body.
//   - ReconcileWatches must be called under Lock.
//   - CloseAllWatches and handleWatchTerminated manage their own locking.
public sealed class WatchManager
{
    private readonly object mu = new(); // Monitor-based: Lock/Unlock are exposed as separate calls
    private WatchBackend? backend;
    private readonly Dictionary<string, WatchedDir> watchedDirs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim doCycleCh = new(0, 1); // Go: chan struct{} (capacity 1, coalescing)

    // DebugLog receives verbose watch diagnostics when non-nil
    public TextWriter? DebugLog;

    private readonly TextWriter warnWriter;
    private readonly Func<string, bool> dirExists;

    private readonly object changedMu = new();
    private Dictionary<string, EventKind>? changedPaths;
    private bool changedOverflow;

    // watchmanager.go:50-57
    public WatchManager(TextWriter warnWriter, Func<string, bool> dirExists)
    {
        this.warnWriter = warnWriter;
        this.dirExists = dirExists;
    }

    // watchmanager.go:59-61
    public void SetBackend(WatchBackend b) => backend = b;

    public WatchBackend? Backend() => backend;

    // watchmanager.go:63-71
    public void EnsureDefaultBackend()
    {
        if (backend == null)
        {
            var fsw = global::Ts.FsWatch.Watcher.Default();
            backend = new FSWatchBackend { Inner = fsw };
            DebugLog?.Write($"[watch] using {fsw.Name()} backend\n");
        }
    }

    // watchmanager.go:73-75
    public void Lock() => Monitor.Enter(mu);

    public void Unlock() => Monitor.Exit(mu);

    // watchmanager.go:79-87
    public (Dictionary<string, EventKind>? Changed, bool Overflow) DrainEvents()
    {
        lock (changedMu)
        {
            var changed = changedPaths;
            var overflow = changedOverflow;
            changedPaths = null;
            changedOverflow = false;
            return (changed, overflow);
        }
    }

    // watchmanager.go:89-93
    public void ForceOverflow()
    {
        lock (changedMu)
        {
            changedOverflow = true;
        }
    }

    // watchmanager.go:95-102
    private void SignalDoCycle()
    {
        try
        {
            doCycleCh.Release();
            // Signal sent; the DoCycle loop will pick it up.
        }
        catch (SemaphoreFullException)
        {
            // A signal is already pending; coalesced.
        }
    }

    // watchmanager.go:104-145
    private void OnWatchEvents(IReadOnlyList<Event> events, Exception? err)
    {
        if (err != null)
        {
            if (ReferenceEquals(err, FsWatchErrors.ErrOverflow) || err.Message == FsWatchErrors.ErrOverflow.Message)
            {
                DebugLog?.Write("[watch] event overflow, triggering rebuild\n");
                lock (changedMu)
                {
                    changedOverflow = true;
                }
                SignalDoCycle();
                return;
            }
            warnWriter.Write($"Warning: File watch error: {err.Message}\n");
            return;
        }

        if (events.Count > 0)
        {
            if (DebugLog != null)
            {
                DebugLog.Write($"[watch] {events.Count} event(s): ");
                for (var i = 0; i < events.Count; i++)
                {
                    if (i > 0)
                    {
                        DebugLog.Write(", ");
                    }
                    if (i >= 5)
                    {
                        DebugLog.Write($"... and {events.Count - i} more");
                        break;
                    }
                    DebugLog.Write($"{events[i].Kind} {events[i].Path}");
                }
                DebugLog.Write("\n");
            }
            lock (changedMu)
            {
                changedPaths ??= new Dictionary<string, EventKind>(events.Count, StringComparer.Ordinal);
                foreach (var e in events)
                {
                    changedPaths[e.Path] = e.Kind;
                }
            }
            SignalDoCycle();
        }
    }

    // watchmanager.go:147-165
    private void HandleWatchTerminated(string dir, WatchedDir identity)
    {
        DebugLog?.Write($"[watch] watch terminated: {dir}\n");
        IWatch? staleCloser = null;
        lock (mu)
        {
            if (watchedDirs.TryGetValue(dir, out var wd) && ReferenceEquals(wd, identity))
            {
                staleCloser = wd.closer;
                watchedDirs.Remove(dir);
            }
        }
        staleCloser?.Close();
        lock (changedMu)
        {
            changedOverflow = true;
        }
        SignalDoCycle();
    }

    // watchmanager.go:167-178
    public void CloseAllWatches()
    {
        var closers = new List<IWatch>();
        lock (mu)
        {
            foreach (var (dir, wd) in watchedDirs) // map-iter: 序无关 (collect closers)
            {
                if (wd.closer != null)
                {
                    closers.Add(wd.closer);
                }
            }
            watchedDirs.Clear();
        }
        foreach (var c in closers)
        {
            c.Close();
        }
    }

    // watchmanager.go:195-208
    private WatchDirectoryRequestWm CreateDirWatchRequest(string dir, WatchedDir entry)
    {
        return new WatchDirectoryRequestWm
        {
            Dir = dir,
            Recursive = entry.recursive,
            Ignore = WatchPathUtil.ShouldIgnoreWatchPath,
            Callback = (events, err) =>
            {
                if (err != null && (ReferenceEquals(err, FsWatchErrors.ErrWatchTerminated) || err.Message == FsWatchErrors.ErrWatchTerminated.Message))
                {
                    HandleWatchTerminated(dir, entry);
                    return;
                }
                OnWatchEvents(events, err);
            },
        };
    }

    // watchmanager.go:210-239
    public Dictionary<string, bool> ResolveDesiredDirs(Dictionary<string, bool> desiredDirs)
    {
        var resolved = new Dictionary<string, bool>(desiredDirs.Count, StringComparer.Ordinal);
        foreach (var (dir, recursive) in desiredDirs) // map-iter: 序无关 (per-dir resolution merged with ||)
        {
            var watchDir = dir;
            var watchRecursive = recursive;
            while (!dirExists(watchDir))
            {
                var parent = global::Ts.TsPath.TsPath.GetDirectoryPath(watchDir);
                if (parent == watchDir)
                {
                    break;
                }
                watchDir = parent;
                watchRecursive = false; // ancestor fallbacks are always non-recursive
            }
            if (!dirExists(watchDir) || !WatchPathUtil.CanWatchDirectory(watchDir))
            {
                DebugLog?.Write($"[watch] no watchable ancestor for {dir}\n");
                continue;
            }
            if (watchDir != dir)
            {
                DebugLog?.Write($"[watch] resolved {dir} to ancestor {watchDir}\n");
            }
            if (resolved.TryGetValue(watchDir, out var existing))
            {
                resolved[watchDir] = existing || watchRecursive;
            }
            else
            {
                resolved[watchDir] = watchRecursive;
            }
        }
        return resolved;
    }

    // watchmanager.go:241-277
    public void ReconcileWatches(Dictionary<string, bool> desiredDirs)
    {
        if (backend == null)
        {
            return;
        }

        var additions = new List<DirWatchUpdate>();
        var changes = new List<DirWatchUpdate>();

        global::Ts.Core.CoreUtil.DiffMapsFunc(
            watchedDirs,
            desiredDirs,
            (wd, recursive) => wd.recursive == recursive,
            (dir, recursive) =>
            {
                DebugLog?.Write($"[watch] watching directory {dir} (recursive={(recursive ? "true" : "false")})\n");
                additions.Add(new DirWatchUpdate { dir = dir, recursive = recursive });
            },
            (dir, wd) =>
            {
                DebugLog?.Write($"[watch] closing stale dir watch: {dir}\n");
                wd.closer?.Close();
                watchedDirs.Remove(dir);
            },
            (dir, wd, recursive) =>
            {
                DebugLog?.Write($"[watch] recreating dir watch {dir} (recursive {(wd.recursive ? "true" : "false")}→{(recursive ? "true" : "false")})\n");
                wd.closer?.Close();
                watchedDirs.Remove(dir);
                changes.Add(new DirWatchUpdate { dir = dir, recursive = recursive });
            });
        additions.AddRange(changes);
        CreateDirWatches(additions);
    }

    // watchmanager.go:279-307 — Go returns error; the C# fswatch surface throws, and the failure
    // path (close partial closers, propagate) is expressed with try/catch at the call sites.
    private void CreateDirWatches(List<DirWatchUpdate> updates)
    {
        if (updates.Count == 0)
        {
            return;
        }
        var requests = new WatchDirectoryRequestWm[updates.Count];
        var entries = new WatchedDir[updates.Count];
        for (var i = 0; i < updates.Count; i++)
        {
            var entry = new WatchedDir { recursive = updates[i].recursive };
            entries[i] = entry;
            requests[i] = CreateDirWatchRequest(updates[i].dir, entry);
        }
        var closers = backend!.WatchDirectories(requests);
        for (var i = 0; i < updates.Count; i++)
        {
            entries[i].closer = closers[i];
            watchedDirs[updates[i].dir] = entries[i];
        }
    }

    // watchmanager.go:322-329
    public bool IsPathUnderWatch(string path, global::Ts.TsPath.ComparePathsOptions opts)
    {
        lock (mu)
        {
            foreach (var dir in watchedDirs.Keys) // map-iter: 序无关 (existence check)
            {
                if (global::Ts.TsPath.TsPath.ContainsPath(dir, path, opts))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // watchmanager.go:331-341
    public void RunLoop(CancellationToken ctx, Action doCycle)
    {
        while (true)
        {
            try
            {
                doCycleCh.Wait(ctx);
            }
            catch (OperationCanceledException)
            {
                CloseAllWatches();
                return;
            }
            doCycle();
        }
    }
}

public static class WatchManagerUtil
{
    // watchmanager.go:309-320
    public static bool IsDirCoveredByWatch(Dictionary<string, bool> dirs, string dir, global::Ts.TsPath.ComparePathsOptions opts)
    {
        foreach (var (wdir, recursive) in dirs) // map-iter: 序无关 (existence check)
        {
            if (recursive)
            {
                if (global::Ts.TsPath.TsPath.ContainsPath(wdir, dir, opts))
                {
                    return true;
                }
            }
            else if (global::Ts.TsPath.TsPath.ComparePaths(dir, wdir, opts) == 0)
            {
                return true;
            }
        }
        return false;
    }
}
