// Ported from tsgo internal/fswatch/watcher.go (dirWatch + callback +
// triggerCallbacks event filtering), with the OS subscription implemented by
// System.IO.FileSystemWatcher.
//
// PORT-NOTE: 非 1:1，FileSystemWatcher 重写. The dirWatch bookkeeping, callback
// list, sequence-cutoff logic, direct-child-vs-recursive filtering, and
// ignore-filtering are ported faithfully from watcher.go — those are the
// caller-visible contract. The OS glue (Go's per-backend subscribe/closeWatch)
// is one FileSystemWatcher, whose Created/Changed/Deleted/Renamed/Error events
// funnel into the shared eventList + debounce trigger, exactly as the Go
// backends' event pumps did.

using System;
using System.Collections.Generic;
using System.IO;

namespace Ts.FsWatch;

// callback: one logical subscriber on a dirWatch. Go: type callback struct.
internal sealed class Callback
{
    public ulong Id;
    public string Dir = "";
    public string PhysicalDir = "";
    public string WatchDir = "";
    public string WatchPhysicalDir = "";
    public bool Recursive;
    public WatchCallback Fn = null!;
    public Func<string, bool>? Ignore;
    public ulong SinceSeq;
    public Exception? Terminal;
    public bool Delivered;

    // Go: cb.mapEvent(e) — rebase a physical event path back under the
    // caller-visible dir when a symlink diverges dir from physicalDir.
    public Event MapEvent(Event e)
    {
        if (PhysicalDir.Length != 0 && PhysicalDir != Dir)
        {
            string physicalPath = EventPhysicalPath(e.Path);
            if (PathOps.IsInDirectoryOrSelf(PhysicalDir, physicalPath))
            {
                e.Path = PathOps.RebasePath(physicalPath, PhysicalDir, Dir);
            }
        }
        return e;
    }

    public string EventPhysicalPath(string path)
    {
        if (WatchPhysicalDir.Length != 0 && WatchDir.Length != 0 && WatchPhysicalDir != WatchDir
            && PathOps.IsInDirectoryOrSelf(WatchDir, path))
        {
            return PathOps.RebasePath(path, WatchDir, WatchPhysicalDir);
        }
        return path;
    }
}

// dirWatch holds per-directory state: pending events, registered callbacks, and
// a reference to the shared debouncer. Each watched directory has one, plus one
// FileSystemWatcher.
internal sealed class DirWatch
{
    // dir is the caller-visible watch root used in delivered event paths.
    public readonly string Dir;
    // physicalDir is the realpath used for symlink rebasing.
    public readonly string PhysicalDir;
    public bool Recursive;

    private readonly EventList _events = new EventList();

    // Go: sync.Mutex → lock.
    private readonly object _mu = new object();
    private readonly List<Callback> _callbacks = new List<Callback>();
    private Debounce? _debounce;
    private ulong _nextCBID;

    // The FileSystemWatcher backing this dirWatch (deviation: replaces the Go
    // per-platform OS descriptor). Created lazily on Start.
    private FileSystemWatcher? _fsw;
    private bool _started;

    public DirWatch(string dir, string physicalDir, Debounce db)
    {
        Dir = dir;
        PhysicalDir = physicalDir;
        _debounce = db;
        _debounce.Add(this, TriggerCallbacks);
    }

    public void DestroyDebounce()
    {
        Debounce? db;
        lock (_mu)
        {
            db = _debounce;
            _debounce = null;
        }
        db?.Remove(this);
    }

    // ----- FileSystemWatcher glue (deviation) ---------------------------------

    // Start arms the FileSystemWatcher for this dir. Idempotent. Throws for a
    // missing/invalid directory (FsWatcher.WatchDirectories maps that to
    // ErrWatchTerminated).
    public void Start()
    {
        lock (_mu)
        {
            if (_started)
            {
                return;
            }
            // Subscribe on `dir` directly; FileSystemWatcher resolves symlinks.
            var fsw = new FileSystemWatcher(Dir)
            {
                IncludeSubdirectories = Recursive,
                NotifyFilter = NotifyFilters.FileName
                    | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.CreationTime,
            };
            fsw.Created += OnCreated;
            fsw.Changed += OnChanged;
            fsw.Deleted += OnDeleted;
            fsw.Renamed += OnRenamed;
            fsw.Error += OnError;
            // EnableRaisingEvents throws if Dir does not exist / is not a dir.
            fsw.EnableRaisingEvents = true;
            _fsw = fsw;
            _started = true;
        }
    }

    public void Stop()
    {
        FileSystemWatcher? fsw;
        lock (_mu)
        {
            fsw = _fsw;
            _fsw = null;
            _started = false;
        }
        if (fsw != null)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Created -= OnCreated;
            fsw.Changed -= OnChanged;
            fsw.Deleted -= OnDeleted;
            fsw.Renamed -= OnRenamed;
            fsw.Error -= OnError;
            fsw.Dispose();
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        _events.Create(Canonicalize.CanonicalizePath(e.FullPath));
        Notify();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _events.Update(Canonicalize.CanonicalizePath(e.FullPath));
        Notify();
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        _events.Remove(Canonicalize.CanonicalizePath(e.FullPath));
        Notify();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename is a delete of the old name + create of the new name — the
        // eventList coalesces these into the right EventDelete/EventUpdate.
        _events.Remove(Canonicalize.CanonicalizePath(e.OldFullPath));
        _events.Create(Canonicalize.CanonicalizePath(e.FullPath));
        Notify();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // InternalBufferOverflowException → ErrOverflow (recoverable), matching
        // Go's ErrOverflow (kernel queue overflow; watch stays active).
        _events.SetError(new FsWatchException(FsWatchErrors.ErrOverflow.Message, e.GetException()));
        Notify();
    }

    // ----- callback dispatch (ported from watcher.go) -------------------------

    private void Notify()
    {
        bool hasPendingCBs;
        bool hasTerminal;
        bool hasEvents;
        bool hasError;
        Debounce? db;
        lock (_mu)
        {
            hasPendingCBs = false;
            hasTerminal = false;
            foreach (Callback cb in _callbacks)
            {
                if (!cb.Delivered)
                {
                    hasPendingCBs = true;
                }
                if (cb.Terminal != null && !cb.Delivered)
                {
                    hasTerminal = true;
                }
            }
            hasEvents = _events.Size() > 0;
            hasError = _events.HasError();
            db = _debounce;
        }

        if (hasPendingCBs && (hasEvents || hasError || hasTerminal) && db != null)
        {
            db.Trigger();
        }
    }

    public void NotifyError(Exception err)
    {
        Callback[] cbs;
        lock (_mu)
        {
            cbs = _callbacks.ToArray();
            _callbacks.Clear();
        }
        foreach (Callback cb in cbs)
        {
            cb.Fn(Array.Empty<Event>(), err);
        }
    }

    // Go: triggerCallbacks — drains the eventList per-callback (respecting each
    // callback's sinceSeq cutoff) and applies ignore/direct-child/recursive
    // filtering before delivery.
    private void TriggerCallbacks()
    {
        List<Callback> cbs;
        bool hasError;
        bool hasEvents;
        bool hasTerminal = false;
        lock (_mu)
        {
            hasError = _events.HasError();
            hasEvents = _events.Size() > 0;
            cbs = new List<Callback>(_callbacks.Count);
            foreach (Callback cb in _callbacks)
            {
                if (cb.Delivered)
                {
                    continue;
                }
                if (cb.Terminal != null)
                {
                    hasTerminal = true;
                }
                cbs.Add(cb);
            }
            if (cbs.Count == 0)
            {
                if (hasEvents || hasError)
                {
                    _ = _events.Drain();
                }
                return;
            }
            if (!hasEvents && !hasError && !hasTerminal)
            {
                return;
            }
        }

        var startSeqs = new ulong[cbs.Count];
        for (int i = 0; i < cbs.Count; i++)
        {
            startSeqs[i] = cbs[i].SinceSeq;
        }

        (List<Event>[] eventsByCallback, Exception? err) = _events.DrainForSequences(startSeqs);

        lock (_mu)
        {
            foreach (Callback cb in cbs)
            {
                if (cb.Terminal == null)
                {
                    continue;
                }
                foreach (Callback stored in _callbacks)
                {
                    if (stored.Id == cb.Id)
                    {
                        stored.Delivered = true;
                        break;
                    }
                }
            }
        }

        for (int i = 0; i < cbs.Count; i++)
        {
            Callback cb = cbs[i];
            List<Event> cbEvents = eventsByCallback[i];
            if (cb.Ignore != null || !cb.Recursive || cb.Dir != Dir)
            {
                var filtered = new List<Event>(cbEvents.Count);
                foreach (Event ev0 in cbEvents)
                {
                    Event e = cb.MapEvent(ev0);
                    if (cb.Ignore != null && cb.Ignore(e.Path))
                    {
                        continue;
                    }
                    if (cb.Dir != Dir && !e.IncludedWatchRoot && e.Path == cb.Dir && e.Kind == EventKind.EventUpdate)
                    {
                        continue;
                    }
                    if (cb.Recursive)
                    {
                        if (cb.Dir != Dir && !PathOps.IsInDirectoryOrSelf(cb.Dir, e.Path))
                        {
                            continue;
                        }
                    }
                    else if (!PathOps.IsDirectChild(cb.Dir, e.Path) && !(cb.Dir != Dir && e.Path == cb.Dir))
                    {
                        continue;
                    }
                    filtered.Add(e);
                }
                cbEvents = filtered;
            }
            Exception? cbErr = err;
            if (cb.Terminal != null)
            {
                cbErr = cb.Terminal;
            }
            if (cbEvents.Count > 0 || cbErr != null)
            {
                cb.Fn(cbEvents, cbErr);
            }
        }
    }

    public ulong Watch(string dir, string physicalDir, bool recursive, WatchCallback fn, Func<string, bool>? ignore)
    {
        lock (_mu)
        {
            _nextCBID++;
            ulong id = _nextCBID;
            ulong sinceSeq = _events.Sequence();
            _callbacks.Add(new Callback
            {
                Id = id,
                Dir = dir,
                PhysicalDir = physicalDir,
                WatchDir = Dir,
                WatchPhysicalDir = PhysicalDir,
                Recursive = recursive,
                Fn = fn,
                Ignore = ignore,
                SinceSeq = sinceSeq,
            });
            return id;
        }
    }

    // Returns true if this was the last callback (dirWatch is now empty).
    public bool Unwatch(ulong id)
    {
        lock (_mu)
        {
            for (int i = 0; i < _callbacks.Count; i++)
            {
                if (_callbacks[i].Id == id)
                {
                    _callbacks.RemoveAt(i);
                    return _callbacks.Count == 0;
                }
            }
            return false;
        }
    }

    public void Unref(FsWatcher w)
    {
        bool empty;
        lock (_mu)
        {
            empty = _callbacks.Count == 0;
        }
        if (empty)
        {
            w.RemoveDirWatchIfEmpty(this);
        }
    }
}
