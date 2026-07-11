// Ported from tsgo internal/fswatch/event.go.
//
// This file is a 1:1 port: the event-coalescing state machine is pure logic
// (no platform syscalls), so it carries over verbatim. Only the platform
// backends of the fswatch package are rewritten (see Watcher.cs).

using System;
using System.Collections.Generic;

namespace Ts.FsWatch;

// EventKind classifies a filesystem change.
public enum EventKind
{
    // Go: EventUpdate EventKind = iota + 1 (so EventUpdate == 1, EventDelete == 2).
    EventUpdate = 1,
    EventDelete = 2,
}

public static class EventKindExtensions
{
    public static string StringValue(this EventKind k)
    {
        switch (k)
        {
            case EventKind.EventUpdate:
                return "update";
            case EventKind.EventDelete:
                return "delete";
            default:
                return "unknown";
        }
    }
}

// Event describes a single filesystem change.
public struct Event
{
    public EventKind Kind;
    public string Path;
    internal bool IncludedWatchRoot;

    public Event(EventKind kind, string path)
    {
        Kind = kind;
        Path = path;
        IncludedWatchRoot = false;
    }
}

// eventEntry tracks coalescing state during a debounce batch.
internal sealed class EventEntry
{
    public ulong CreatedSeq;
    public ulong UpdatedSeq;
    public ulong DeletedSeq;
    public bool IncludedWatchRoot;

    public bool IsDeleted()
    {
        return DeletedSeq > CreatedSeq && DeletedSeq > UpdatedSeq;
    }

    // Go: kindSince(startSeq uint64) (EventKind, bool).
    public bool TryKindSince(ulong startSeq, out EventKind kind)
    {
        if (DeletedSeq > startSeq)
        {
            if (CreatedSeq > startSeq && CreatedSeq < DeletedSeq && UpdatedSeq < DeletedSeq)
            {
                kind = default;
                return false;
            }
            kind = EventKind.EventDelete;
            return true;
        }
        ulong seq = Math.Max(CreatedSeq, UpdatedSeq);
        if (seq > startSeq)
        {
            kind = EventKind.EventUpdate;
            return true;
        }
        kind = default;
        return false;
    }
}

// eventList coalesces filesystem events by path within a debounce window.
//   - create after delete → update (rapid delete+recreate)
//   - getEvents skips entries that were both created and deleted
internal sealed class EventList
{
    // Go: sync.Mutex → Lock (05-conventions.md concurrency mapping).
    private readonly object _mu = new object();
    private Dictionary<string, EventEntry>? _entries;
    private Exception? _err;
    private ulong _seq;

    // create records a new-file event for path. Both create and update
    // produce EventUpdate externally; sequence state tracks coalescing
    // (create+delete within a batch cancels out).
    public void Create(string path)
    {
        lock (_mu)
        {
            ulong seq = NextSeqLocked();
            CreateLocked(path, seq);
        }
    }

    public void CreateAt(string path, ulong seq)
    {
        lock (_mu)
        {
            AdvanceSeqLocked(seq);
            CreateLocked(path, seq);
        }
    }

    private void CreateLocked(string path, ulong seq)
    {
        EventEntry entry = GetOrCreate(path);
        if (entry.IsDeleted())
        {
            // Rapid delete+recreate: clear both flags so the entry
            // emits EventUpdate (the default for non-deleted entries).
            // https://github.com/parcel-bundler/watcher/issues/72
            entry.DeletedSeq = 0;
            entry.CreatedSeq = 0;
            entry.UpdatedSeq = seq;
        }
        else
        {
            entry.CreatedSeq = seq;
        }
    }

    // update records an update event for path.
    public void Update(string path)
    {
        lock (_mu)
        {
            ulong seq = NextSeqLocked();
            UpdateLocked(path, seq);
        }
    }

    public void UpdateAt(string path, ulong seq)
    {
        lock (_mu)
        {
            AdvanceSeqLocked(seq);
            UpdateLocked(path, seq);
        }
    }

    public void UpdateWatchRootAt(string path, ulong seq)
    {
        lock (_mu)
        {
            AdvanceSeqLocked(seq);
            UpdateLocked(path, seq);
            GetOrCreate(path).IncludedWatchRoot = true;
        }
    }

    private void UpdateLocked(string path, ulong seq)
    {
        GetOrCreate(path).UpdatedSeq = seq;
    }

    // remove records a delete event for path.
    public void Remove(string path)
    {
        lock (_mu)
        {
            ulong seq = NextSeqLocked();
            RemoveLocked(path, seq);
        }
    }

    public ulong RemoveAndGetSequence(string path)
    {
        lock (_mu)
        {
            ulong seq = NextSeqLocked();
            RemoveLocked(path, seq);
            return seq;
        }
    }

    public void RemoveAt(string path, ulong seq)
    {
        lock (_mu)
        {
            AdvanceSeqLocked(seq);
            RemoveLocked(path, seq);
        }
    }

    public void RemoveWatchRootAt(string path, ulong seq)
    {
        lock (_mu)
        {
            AdvanceSeqLocked(seq);
            RemoveLocked(path, seq);
            GetOrCreate(path).IncludedWatchRoot = true;
        }
    }

    private void RemoveLocked(string path, ulong seq)
    {
        EventEntry entry = GetOrCreate(path);
        entry.DeletedSeq = seq;
    }

    // size returns the number of tracked entries (including ones that may
    // cancel out in getEvents).
    public int Size()
    {
        lock (_mu)
        {
            return _entries?.Count ?? 0;
        }
    }

    // snapshotLocked returns the current set of pending events with
    // create+delete pairs filtered out. Caller must hold _mu.
    private List<Event> SnapshotLocked()
    {
        return SnapshotSinceLocked(0);
    }

    private List<Event> SnapshotSinceLocked(ulong startSeq)
    {
        var outEvents = new List<Event>(_entries?.Count ?? 0);
        if (_entries != null)
        {
            // map-iter: 序无关 — output order is documented as not guaranteed
            // (getEvents: "Order is not guaranteed").
            foreach (KeyValuePair<string, EventEntry> kv in _entries)
            {
                if (!kv.Value.TryKindSince(startSeq, out EventKind kind))
                {
                    continue;
                }
                outEvents.Add(new Event(kind, kv.Key) { IncludedWatchRoot = kv.Value.IncludedWatchRoot });
            }
        }
        return outEvents;
    }

    // getEvents returns a snapshot of events, skipping entries that were both
    // created and deleted. Order is not guaranteed.
    public List<Event> GetEvents()
    {
        lock (_mu)
        {
            return SnapshotLocked();
        }
    }

    // drain atomically snapshots all pending events and the stored error,
    // then clears the list. This prevents events added between a separate
    // getEvents+clear from being silently dropped.
    public (List<Event> Events, Exception? Err) Drain()
    {
        lock (_mu)
        {
            List<Event> outEvents = SnapshotLocked();
            Exception? err = _err;
            _entries = null;
            _err = null;
            return (outEvents, err);
        }
    }

    public (List<Event>[] Events, Exception? Err) DrainForSequences(ulong[] startSeqs)
    {
        lock (_mu)
        {
            var outEvents = new List<Event>[startSeqs.Length];
            for (int i = 0; i < startSeqs.Length; i++)
            {
                outEvents[i] = SnapshotSinceLocked(startSeqs[i]);
            }
            Exception? err = _err;
            _entries = null;
            _err = null;
            return (outEvents, err);
        }
    }

    // setError stores the first error encountered (later errors are ignored).
    public void SetError(Exception err)
    {
        lock (_mu)
        {
            if (_err == null)
            {
                _err = err;
            }
        }
    }

    // hasError reports whether an error has been recorded.
    public bool HasError()
    {
        lock (_mu)
        {
            return _err != null;
        }
    }

    // getError returns the stored error (or null if none).
    public Exception? GetError()
    {
        lock (_mu)
        {
            return _err;
        }
    }

    // clear is only used by tests; live code drains via Drain() so the
    // snapshot and the reset happen atomically.
    internal void Clear()
    {
        lock (_mu)
        {
            _entries = null;
            _err = null;
        }
    }

    private EventEntry GetOrCreate(string path)
    {
        _entries ??= new Dictionary<string, EventEntry>(StringComparer.Ordinal);
        if (_entries.TryGetValue(path, out EventEntry? e))
        {
            return e;
        }
        e = new EventEntry();
        _entries[path] = e;
        return e;
    }

    public ulong Sequence()
    {
        lock (_mu)
        {
            return _seq;
        }
    }

    private ulong NextSeqLocked()
    {
        _seq++;
        return _seq;
    }

    private void AdvanceSeqLocked(ulong seq)
    {
        if (seq > _seq)
        {
            _seq = seq;
        }
    }
}
