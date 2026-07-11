// Watcher-level tests for the FileSystemWatcher-backed fswatch port. Per
// plan/06 §8.5 the platform-backend-specific Go tests (fsevents/inotify/etc.)
// are NOT ported (the backend was replaced); their behavioral assertions fold
// into these watcher-level tests, which drive the *public contract*
// (WatchDirectory / WatchFile / event kinds / recursive vs direct-child /
// WithIgnore / Close) against a real FileSystemWatcher on a temp directory.
//
// These tests use a real OS watcher with bounded polling waits. FileSystemWatcher
// delivery timing/coalescing is platform-dependent; tests that assert *presence*
// of an event within a deadline are robust, while assertions about exact event
// counts are avoided (the debounce/backend may coalesce). Any case that proves
// environmentally unstable in CI should be marked [Fact(Skip=...)] with the
// reason, per the task's platform-stability guidance.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Ts.FsWatch;
using Xunit;

namespace Ts.FsWatch.Tests;

public class WatcherTests : IDisposable
{
    private readonly string _root;
    private readonly List<IWatch> _watches = new List<IWatch>();

    // Deadline for a change to surface. min debounce is 50ms, max 500ms; give a
    // generous multiple to absorb OS backend latency without flaking.
    private static readonly TimeSpan EventDeadline = TimeSpan.FromSeconds(5);

    public WatcherTests()
    {
        // Resolve to a canonical (symlink-free) root so delivered event paths
        // (which the backend reports canonically) compare against our
        // expectations. On macOS TempPath is often under /var → /private/var.
        string raw = Path.Combine(Path.GetTempPath(), "fswatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(raw);
        _root = global::Ts.NativePath.NativePath.Realpath(raw) ?? raw;
    }

    public void Dispose()
    {
        foreach (IWatch w in _watches)
        {
            try { w.Close(); } catch (Exception) { }
        }
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private IWatch Track(IWatch w)
    {
        _watches.Add(w);
        return w;
    }

    // Collects delivered events on a background-safe list until a predicate is
    // satisfied or the deadline elapses.
    private sealed class Collector
    {
        private readonly object _lock = new object();
        private readonly List<Event> _events = new List<Event>();
        public Exception? LastError;

        public WatchCallback Callback => (events, err) =>
        {
            lock (_lock)
            {
                if (err != null)
                {
                    LastError = err;
                }
                _events.AddRange(events);
            }
        };

        public List<Event> Snapshot()
        {
            lock (_lock)
            {
                return new List<Event>(_events);
            }
        }

        public bool WaitFor(Func<List<Event>, bool> predicate, TimeSpan deadline)
        {
            DateTime end = DateTime.UtcNow + deadline;
            while (DateTime.UtcNow < end)
            {
                if (predicate(Snapshot()))
                {
                    return true;
                }
                Thread.Sleep(20);
            }
            return predicate(Snapshot());
        }
    }

    [Fact]
    public void WatcherIdentityAndAvailability()
    {
        IWatcher w = Watcher.Default();
        Assert.True(w.Available());
        Assert.False(string.IsNullOrEmpty(w.Name()));
        // Named factories preserve identity for call sites that branch on Name().
        Assert.Equal("inotify", Watcher.Inotify().Name());
        Assert.Equal("fsevents", Watcher.FSEvents().Name());
        Assert.Equal("windows", Watcher.Windows().Name());
        Assert.Equal("kqueue", Watcher.Kqueue().Name());
        Assert.Equal("fanotify", Watcher.Fanotify().Name());
    }

    [Fact]
    public void RejectsNilCallback()
    {
        IWatcher w = Watcher.Default();
        Assert.Throws<FsWatchException>(() => w.WatchDirectory(_root, null!));
    }

    [Fact]
    public void RejectsRelativePath()
    {
        IWatcher w = Watcher.Default();
        var c = new Collector();
        Assert.Throws<FsWatchException>(() => w.WatchDirectory("relative/dir", c.Callback));
    }

    [Fact]
    public void WatchFileRejectsRootPath()
    {
        IWatcher w = Watcher.Default();
        var c = new Collector();
        string rootPath = OperatingSystem.IsWindows() ? "C:\\" : "/";
        Assert.Throws<FsWatchException>(() => w.WatchFile(rootPath, c.Callback));
    }

    [Fact]
    public void DetectsFileCreateInDirectory()
    {
        IWatcher w = Watcher.Default();
        var c = new Collector();
        Track(w.WatchDirectory(_root, c.Callback));

        string file = Path.Combine(_root, "created.txt");
        File.WriteAllText(file, "hi");

        bool ok = c.WaitFor(evs => evs.Exists(e => e.Path == file), EventDeadline);
        Assert.True(ok, $"expected a create/update event for {file}; got: {Describe(c.Snapshot())}");
    }

    [Fact]
    public void DetectsFileDeleteInDirectory()
    {
        string file = Path.Combine(_root, "todelete.txt");
        File.WriteAllText(file, "hi");

        IWatcher w = Watcher.Default();
        var c = new Collector();
        Track(w.WatchDirectory(_root, c.Callback));

        // Give the watch a moment to arm before mutating.
        Thread.Sleep(100);
        File.Delete(file);

        bool ok = c.WaitFor(
            evs => evs.Exists(e => e.Path == file && e.Kind == EventKind.EventDelete),
            EventDeadline);
        Assert.True(ok, $"expected a delete event for {file}; got: {Describe(c.Snapshot())}");
    }

    [Fact]
    public void NonRecursiveIgnoresNestedFile()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        IWatcher w = Watcher.Default();
        var c = new Collector();
        Track(w.WatchDirectory(_root, c.Callback)); // non-recursive
        Thread.Sleep(100);

        string nested = Path.Combine(sub, "deep.txt");
        File.WriteAllText(nested, "deep");

        // The nested file must NOT be delivered under a non-recursive watch.
        bool sawNested = c.WaitFor(evs => evs.Exists(e => e.Path == nested), TimeSpan.FromMilliseconds(800));
        Assert.False(sawNested, $"non-recursive watch leaked nested event: {Describe(c.Snapshot())}");
    }

    [Fact]
    public void RecursiveDetectsNestedFile()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);

        IWatcher w = Watcher.Default();
        var c = new Collector();
        Track(w.WatchDirectory(_root, c.Callback, Watcher.WithRecursive()));
        Thread.Sleep(100);

        string nested = Path.Combine(sub, "deep.txt");
        File.WriteAllText(nested, "deep");

        bool ok = c.WaitFor(evs => evs.Exists(e => e.Path == nested), EventDeadline);
        Assert.True(ok, $"recursive watch missed nested event for {nested}; got: {Describe(c.Snapshot())}");
    }

    [Fact]
    public void WithIgnoreDropsMatchingPaths()
    {
        IWatcher w = Watcher.Default();
        var c = new Collector();
        string ignored = Path.Combine(_root, "ignored.txt");
        string kept = Path.Combine(_root, "kept.txt");
        Track(w.WatchDirectory(_root, c.Callback, Watcher.WithIgnore(p => p == ignored)));
        Thread.Sleep(100);

        File.WriteAllText(ignored, "x");
        File.WriteAllText(kept, "y");

        bool sawKept = c.WaitFor(evs => evs.Exists(e => e.Path == kept), EventDeadline);
        Assert.True(sawKept, $"expected event for kept file; got: {Describe(c.Snapshot())}");
        Assert.DoesNotContain(c.Snapshot(), e => e.Path == ignored);
    }

    [Fact]
    public void WatchFileOnlyReportsTarget()
    {
        IWatcher w = Watcher.Default();
        var c = new Collector();
        string target = Path.Combine(_root, "target.txt");
        string other = Path.Combine(_root, "other.txt");
        Track(w.WatchFile(target, c.Callback));
        Thread.Sleep(100);

        File.WriteAllText(other, "no");
        File.WriteAllText(target, "yes");

        bool sawTarget = c.WaitFor(evs => evs.Exists(e => e.Path == target), EventDeadline);
        Assert.True(sawTarget, $"expected event for target; got: {Describe(c.Snapshot())}");
        // A file watch must never surface sibling files.
        Assert.DoesNotContain(c.Snapshot(), e => e.Path == other);
    }

    [Fact]
    public void CloseIsIdempotent()
    {
        IWatcher w = Watcher.Default();
        var c = new Collector();
        IWatch watch = w.WatchDirectory(_root, c.Callback);
        watch.Close();
        watch.Close(); // idempotent, must not throw
        watch.Dispose();
    }

    private static string Describe(List<Event> events)
    {
        if (events.Count == 0)
        {
            return "(no events)";
        }
        var parts = new List<string>();
        foreach (Event e in events)
        {
            parts.Add($"{e.Kind.StringValue()}:{e.Path}");
        }
        return string.Join(", ", parts);
    }
}
