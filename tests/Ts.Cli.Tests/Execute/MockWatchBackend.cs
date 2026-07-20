// Ported from tsgo internal/execute/tsctests/mock_watch_backend.go.

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Execute.WatchManagement;
using Ts.FsWatch;
using Ts.TestUtil.FsBaselineUtil;

namespace Ts.Cli.Tests.Execute;

// mock_watch_backend.go:16-25 — MockWatchBackend implements watchmanager.WatchBackend for testing.
// It records all WatchDirectory calls so tests can verify that the correct watches are registered.
// Events can be delivered through SendEvents, which routes them only through watches whose paths
// match, enforcing that tests fail if the wrong watches are set up.
internal sealed class MockWatchBackend : WatchBackend
{
    private readonly System.Threading.Lock mu = new();
    internal readonly Dictionary<string, MockWatch> Dirs = new(StringComparer.Ordinal);
    internal Func<string, bool>? DirectoryExists = null; // if set (by TestSys), WatchDirectory fails for non-existent dirs

    // mock_watch_backend.go:36-41 — HasWatches reports whether any watches have been registered.
    internal bool HasWatches()
    {
        lock (mu)
        {
            return Dirs.Count > 0;
        }
    }

    // mock_watch_backend.go:57-68
    public IWatch WatchDirectory(string dir, WatchCallback fn, bool recursive, Func<string, bool>? ignore)
    {
        var closers = WatchDirectories([new WatchDirectoryRequestWm
        {
            Dir = dir,
            Callback = fn,
            Recursive = recursive,
            Ignore = ignore,
        }]);
        return closers[0];
    }

    // mock_watch_backend.go:70-85
    public IReadOnlyList<IWatch> WatchDirectories(IReadOnlyList<WatchDirectoryRequestWm> requests)
    {
        lock (mu)
        {
            foreach (var request in requests)
            {
                if (DirectoryExists != null && !DirectoryExists(request.Dir))
                {
                    throw new InvalidOperationException("directory does not exist: " + request.Dir);
                }
            }
            var closers = new IWatch[requests.Count];
            for (var i = 0; i < requests.Count; i++)
            {
                var request = requests[i];
                var w = new MockWatch { Path = request.Dir, Callback = request.Callback, Recursive = request.Recursive, Ignore = request.Ignore };
                Dirs[request.Dir] = w;
                closers[i] = w;
            }
            return closers;
        }
    }

    // mock_watch_backend.go:87-127 — SendEvents routes events through the registered watch
    // callbacks that match each event's path. Directory watches match if the event path is a
    // child (or recursive descendant) of the watched directory. Events that match no watch are
    // silently dropped — this is by design so that tests fail when the production code doesn't
    // register the needed watches.
    internal void SendEvents(IReadOnlyList<Event> events)
    {
        // Snapshot callbacks under the lock, then invoke outside the lock
        // to avoid deadlock if the callback re-enters the mock.
        List<(WatchCallback Cb, List<Event> Events)> targets;
        lock (mu)
        {
            var byWatch = new Dictionary<MockWatch, (WatchCallback Cb, List<Event> Events)>();
            foreach (var e in events)
            {
                // Check directory watches.
                foreach (var w in Dirs.Values) // map-iter: 序无关 (per-watch accumulation)
                {
                    if (w.Closed)
                    {
                        continue;
                    }
                    if (w.Ignore != null && w.Ignore(e.Path))
                    {
                        continue;
                    }
                    if (!PathIsUnder(e.Path, w.Path, w.Recursive))
                    {
                        continue;
                    }
                    if (byWatch.TryGetValue(w, out var t))
                    {
                        t.Events.Add(e);
                    }
                    else
                    {
                        byWatch[w] = (w.Callback, new List<Event> { e });
                    }
                }
            }
            targets = new List<(WatchCallback, List<Event>)>(byWatch.Values);
        }

        foreach (var (cb, targetEvents) in targets) // map-iter: 序无关 (independent callbacks)
        {
            cb(targetEvents, null);
        }
    }

    // mock_watch_backend.go:129-161 — SendChangedPaths converts a list of file changes into
    // fswatch events with appropriate event kinds and routes them through registered watches
    // via SendEvents. For new/modified files, it also emits update events for their parent
    // directories, simulating how real filesystem watchers report directory events.
    internal void SendChangedPaths(IReadOnlyList<FSDiffer.FileChange> changes)
    {
        var events = new List<Event>(changes.Count * 2);
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in changes)
        {
            var kind = EventKind.EventUpdate;
            if (c.Deleted)
            {
                kind = EventKind.EventDelete;
            }
            events.Add(new Event(kind, c.Path));
            // Emit update events for parent directories of changed files.
            // Real filesystem watchers deliver events to non-recursive watches
            // when a child directory is created, which the mock must replicate.
            var dir = PathDir(c.Path);
            while (dir.Length > 0 && dir != "/" && dir != ".")
            {
                if (!seenDirs.Add(dir))
                {
                    break;
                }
                events.Add(new Event(EventKind.EventUpdate, dir));
                var parent = PathDir(dir);
                if (parent == dir)
                {
                    break;
                }
                dir = parent;
            }
        }
        SendEvents(events);
    }

    // Go path.Dir (POSIX): trailing element removed, no trailing slash, "" → ".".
    private static string PathDir(string p)
    {
        var idx = p.LastIndexOf('/');
        if (idx < 0)
        {
            return ".";
        }
        if (idx == 0)
        {
            return "/";
        }
        return p[..idx];
    }

    // mock_watch_backend.go:163-181 — pathIsUnder reports whether eventPath is inside dir.
    // If recursive is false, only direct children match.
    private static bool PathIsUnder(string eventPath, string dir, bool recursive)
    {
        if (!eventPath.StartsWith(dir, StringComparison.Ordinal))
        {
            return false;
        }
        var rest = eventPath[dir.Length..];
        if (rest.Length == 0)
        {
            return false; // exact match = the dir itself, not a child
        }
        if (rest[0] != '/')
        {
            return false; // e.g. dir="/foo", path="/foobar"
        }
        if (!recursive)
        {
            // Direct child only: no further '/' after the separator.
            return !rest[1..].Contains('/');
        }
        return true;
    }

    // mock_watch_backend.go:183-216 — WatchState returns a deterministic, human-readable summary
    // of all active watches. This is intended to be included in test baselines so that watch
    // registration correctness is verified via snapshot diffs.
    internal string WatchState()
    {
        lock (mu)
        {
            var b = new StringBuilder();
            b.Append("Watch Registrations::\n");

            // Directory watches, sorted by path.
            var dirs = new List<string>();
            foreach (var (dir, w) in Dirs) // map-iter: 已排序 (sorted below)
            {
                if (!w.Closed)
                {
                    dirs.Add(dir);
                }
            }
            dirs.Sort(StringComparer.Ordinal);

            b.Append("Directory watches::\n");
            if (dirs.Count == 0)
            {
                b.Append("  (none)\n");
            }
            foreach (var d in dirs)
            {
                var w = Dirs[d];
                if (w.Recursive)
                {
                    b.Append("  ").Append(d).Append(" (recursive)\n");
                }
                else
                {
                    b.Append("  ").Append(d).Append('\n');
                }
            }

            return b.ToString();
        }
    }
}

// mock_watch_backend.go:43-55 — MockWatch records a single registered watch.
internal sealed class MockWatch : IWatch
{
    internal string Path = "";
    internal WatchCallback Callback = null!;
    internal bool Recursive;
    internal Func<string, bool>? Ignore;
    internal bool Closed;

    public void Close() => Closed = true;

    public void Dispose() => Close();
}
