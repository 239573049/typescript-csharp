// Ported from tsgo internal/fswatch/watcher.go (the concrete *watcher, dirWatch,
// callback, and event-filtering logic) — with the platform backend replaced by
// System.IO.FileSystemWatcher.
//
// PORT-NOTE: 非 1:1，FileSystemWatcher 重写. See Watcher.cs header for the full
// contract-vs-deviation split. This file is where the deviation is concentrated:
//   - Go's watcherImpl/watcherBase virtual-dispatch layer (start/run/subscribe/
//     closeWatch across inotify/fsevents/kqueue/windows) → one
//     FileSystemWatcher per dirWatch.
//   - The recursive-watch consolidation heuristics
//     (findCoveringRecursiveWatchLocked / findConsolidationDirLocked /
//     recursiveConsolidateThreshold) are dropped: FileSystemWatcher subtree mode
//     is cheap and self-contained, so each (dir, recursive) key gets its own
//     watch. STUB noted below.
//   - physicalDir/symlink rebasing is preserved as a data path (via
//     Ts.NativePath.NativePath.Realpath) but the OS subscribes on `dir` directly
//     (FileSystemWatcher resolves the link itself); rebasePath/displayPath helpers
//     are ported for the WatchFile/recursive-filter contract.
//
// Concurrency: FileSystemWatcher raises events on its own thread; those handlers
// feed the shared Debounce (Debounce.cs), which fires user callbacks on the
// debounce thread — the same "callback never runs concurrently with itself,
// runs on a library thread not the caller's" guarantee Go documented.

using System;
using System.Collections.Generic;
using System.IO;

namespace Ts.FsWatch;

internal sealed class FsWatcher : IWatcher
{
    private readonly string _name;

    private readonly object _mu = new object();
    private Dictionary<string, DirWatch>? _dirWatches;
    private Debounce? _debounce;

    private FsWatcher(string name) => _name = name;

    public static FsWatcher Named(string name) => new FsWatcher(name);

    public string Name() => _name;
    public override string ToString() => _name;

    // FileSystemWatcher is available on every supported platform.
    public bool Available() => true;

    // FileSystemWatcher.IncludeSubdirectories is always subtree-native.
    public bool HasFastRecursiveBackend() => true;

    // Go: keyForDirWatch(dir, recursive).
    private static string KeyForDirWatch(string dir, bool recursive)
    {
        if (recursive)
        {
            return dir + "\x00recursive";
        }
        return dir;
    }

    private DirWatch GetOrCreateDirWatch(string dir, string physicalDir, bool recursive)
    {
        lock (_mu)
        {
            _dirWatches ??= new Dictionary<string, DirWatch>(StringComparer.Ordinal);
            _debounce ??= Debounce.New();

            // PORT-NOTE (STUB): Go's recursive-watch consolidation
            // (canShareRecursiveDirWatches / findCoveringRecursiveWatchLocked /
            // findConsolidationDirLocked) is intentionally not ported — it was a
            // per-backend optimization (fsevents/windows) to coalesce many
            // sibling recursive watches under one OS descriptor. FileSystemWatcher
            // per-dir subtree watches are cheap enough to skip it. Each unique
            // (dir, recursive) key gets its own dirWatch/FileSystemWatcher.

            string key = KeyForDirWatch(dir, recursive);
            if (_dirWatches.TryGetValue(key, out DirWatch? existing))
            {
                return existing;
            }
            var dw = new DirWatch(dir, physicalDir, _debounce)
            {
                Recursive = recursive,
            };
            _dirWatches[key] = dw;
            return dw;
        }
    }

    private void RemoveDirWatch(DirWatch dw)
    {
        lock (_mu)
        {
            if (_dirWatches == null)
            {
                return;
            }
            string key = KeyForDirWatch(dw.Dir, dw.Recursive);
            if (_dirWatches.TryGetValue(key, out DirWatch? existing) && ReferenceEquals(existing, dw))
            {
                _dirWatches.Remove(key);
                dw.Stop();
                dw.DestroyDebounce();
            }
        }
    }

    public IWatch WatchDirectory(string dir, WatchCallback fn, params IWatchOption[] opts)
    {
        IReadOnlyList<IWatch> watches = WatchDirectories(new[]
        {
            new WatchDirectoryRequest { Dir = dir, Callback = fn, Options = opts },
        });
        return watches[0];
    }

    public IReadOnlyList<IWatch> WatchDirectories(IReadOnlyList<WatchDirectoryRequest> requests)
    {
        if (requests.Count == 0)
        {
            return Array.Empty<IWatch>();
        }

        var prepared = new List<(DirWatch Dw, ulong Id)>(requests.Count);
        var uniqueDirWatches = new List<DirWatch>(requests.Count);
        var seen = new HashSet<DirWatch>(ReferenceEqualityComparer.Instance);

        void Rollback()
        {
            for (int i = prepared.Count - 1; i >= 0; i--)
            {
                (DirWatch dw, ulong id) = prepared[i];
                dw.Unwatch(id);
                dw.Unref(this);
            }
        }

        foreach (WatchDirectoryRequest request in requests)
        {
            string dir = request.Dir;
            WatchCallback? fn = request.Callback;
            if (fn == null)
            {
                Rollback();
                throw FsWatchErrors.ErrNilCallback;
            }
            dir = PathUtil.Clean(dir);
            if (!PathUtil.IsAbs(dir))
            {
                Rollback();
                throw FsWatchErrors.ErrNotAbsolute;
            }
            dir = Canonicalize.CanonicalizePath(dir);
            string physicalDir = PhysicalDirFor(dir);

            var sopts = new WatchOptions();
            if (request.Options != null)
            {
                foreach (IWatchOption o in request.Options)
                {
                    o.ApplyWatchOption(sopts);
                }
            }

            DirWatch dw = GetOrCreateDirWatch(dir, physicalDir, sopts.Recursive);
            ulong id = dw.Watch(dir, physicalDir, sopts.Recursive, fn, sopts.Ignore);
            prepared.Add((dw, id));
            if (seen.Add(dw))
            {
                uniqueDirWatches.Add(dw);
            }
        }

        // Arm the underlying FileSystemWatchers once for the batch. A start
        // failure (e.g. the directory does not exist) surfaces to the callback
        // as ErrWatchTerminated, matching Go's start-error propagation.
        foreach (DirWatch dw in uniqueDirWatches)
        {
            try
            {
                dw.Start();
            }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
            {
                // Deliver a terminal error to the just-added callbacks, then roll
                // back registration for a clean caller-visible failure.
                dw.NotifyError(new FsWatchException(FsWatchErrors.ErrWatchTerminated.Message, e));
                Rollback();
                throw new FsWatchException(FsWatchErrors.ErrWatchTerminated.Message, e);
            }
        }

        var result = new IWatch[prepared.Count];
        for (int i = 0; i < prepared.Count; i++)
        {
            result[i] = new WatchHandle(this, prepared[i].Dw, prepared[i].Id);
        }
        return result;
    }

    public IWatch WatchFile(string path, WatchCallback fn)
    {
        if (fn == null)
        {
            throw FsWatchErrors.ErrNilCallback;
        }
        path = PathUtil.Clean(path);
        if (!PathUtil.IsAbs(path))
        {
            throw FsWatchErrors.ErrNotAbsolute;
        }
        path = Canonicalize.CanonicalizePath(path);
        string dir = PathUtil.Dir(path);
        if (dir == path)
        {
            throw FsWatchErrors.ErrRootPath;
        }
        return WatchDirectory(dir, FileCallback(path, fn));
    }

    // fileCallback wraps a WatchCallback so it only sees events for the specific
    // target path. Errors are always forwarded (with any matching events
    // delivered alongside) so callers don't lose overflow signals just because
    // their target wasn't in the same batch.
    private static WatchCallback FileCallback(string target, WatchCallback fn)
    {
        return (events, err) =>
        {
            List<Event>? filtered = null;
            foreach (Event e in events)
            {
                if (e.Path == target)
                {
                    filtered ??= new List<Event>();
                    filtered.Add(e);
                }
            }
            if ((filtered != null && filtered.Count > 0) || err != null)
            {
                fn((IReadOnlyList<Event>?)filtered ?? Array.Empty<Event>(), err);
            }
        };
    }

    // physicalDirFor returns the physical path to watch for dir. If dir, or an
    // ancestor of dir, is a symlink or reparse point, events map back to dir
    // while state tracks the realpath. Go: physicalDirFor(dir).
    internal static string PhysicalDirFor(string dir)
    {
        string? realpath = global::Ts.NativePath.NativePath.Realpath(dir);
        if (realpath == null)
        {
            return dir;
        }
        if (realpath == dir)
        {
            return dir;
        }
        return Canonicalize.CanonicalizePath(PathUtil.Clean(realpath));
    }

    // Called by WatchHandle.Close when a callback is the last on its dirWatch.
    internal void RemoveDirWatchIfEmpty(DirWatch dw) => RemoveDirWatch(dw);
}

// WatchHandle is the concrete IWatch. Go: type watch struct { ... }.
internal sealed class WatchHandle : IWatch
{
    private readonly object _mu = new object();
    private readonly FsWatcher _w;
    private readonly DirWatch _dw;
    private readonly ulong _id;
    private bool _cancelled;

    public WatchHandle(FsWatcher w, DirWatch dw, ulong id)
    {
        _w = w;
        _dw = dw;
        _id = id;
    }

    public void Close()
    {
        lock (_mu)
        {
            if (_cancelled)
            {
                return;
            }
            _cancelled = true;
            bool last = _dw.Unwatch(_id);
            if (last)
            {
                _dw.Unref(_w);
            }
        }
    }

    public void Dispose() => Close();
}
