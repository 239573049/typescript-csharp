// Ported from tsgo internal/fswatch/watcher.go.
//
// PORT-NOTE: 非 1:1，FileSystemWatcher 重写.
// ============================================================================
// This file preserves the *public API shape and call-site contract* of Go's
// fswatch.Watcher (the surface the CLI --watch loop and watchmanager consume:
// plan/06 §8.5, §13.3) while replacing all platform backends
// (inotify/fanotify/kqueue/FSEvents/ReadDirectoryChangesW + .s FFI + syscall)
// with a single System.IO.FileSystemWatcher-based implementation.
//
// What is preserved 1:1 (contract): the Watcher interface, WatchDirectory /
// WatchDirectories / WatchFile, WatchCallback = (Event[], Exception?), the
// WatchDirectoryRequest shape, WithRecursive / WithIgnore options, the Watch
// (Close) handle, the sentinel errors (ErrOverflow / ErrWatchTerminated /
// ErrUnavailable), the Default()/Inotify()/FSEvents()/Kqueue()/Windows()/
// Fanotify() factory names, event coalescing (Event.cs) and debounce
// (Debounce.cs) semantics, and direct-child-vs-recursive event filtering.
//
// What is collapsed (deviation): the per-platform backend selection folds into
// FileSystemWatcher. All the named factory functions return the SAME
// FileSystemWatcher-backed watcher, differing only in Name() (identity is kept
// so .Name()/.String() call sites still work). Symlink/reparse "physical dir"
// rebasing, recursive-watch consolidation heuristics, and the kernel overflow
// path are simplified: recursive mode maps to
// FileSystemWatcher.IncludeSubdirectories; non-recursive mode filters to direct
// children; FileSystemWatcher.Error → ErrOverflow (recoverable) is surfaced.
//
// Concurrency mapping (05-conventions.md): sync.Mutex → lock; the Go event
// pump goroutine is subsumed by FileSystemWatcher's own event thread, which
// feeds the shared Debounce (Debounce.cs) exactly as the Go backends fed it.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace Ts.FsWatch;

// WatchCallback receives batched filesystem events. Rapid changes are coalesced
// before delivery. For a given Watch, the callback is never invoked
// concurrently with itself. When err is non-null, compare against ErrOverflow
// (recoverable) or ErrWatchTerminated (terminal).
//
// Go: type WatchCallback func(events []Event, err error).
public delegate void WatchCallback(IReadOnlyList<Event> events, Exception? err);

// WatchOption configures a watch.
public interface IWatchOption
{
    void ApplyWatchOption(WatchOptions opts);
}

// Go: watchOptions (unexported struct). Kept public so IWatchOption
// implementations across the assembly can mutate it; the type is internal to
// the watch contract.
public sealed class WatchOptions
{
    public Func<string, bool>? Ignore;
    public bool Recursive;
}

// WatchDirectoryRequest describes one directory subscription in a
// WatchDirectories batch. Go: fswatch.WatchDirectoryRequest{Dir, Callback, Options}.
public struct WatchDirectoryRequest
{
    public string Dir;
    public WatchCallback Callback;
    public IReadOnlyList<IWatchOption>? Options;
}

// Watch represents a live watch. Close stops watching and releases resources.
// It is idempotent. Go: type Watch interface { Close() error; unexported() }.
// Mapped to IDisposable so `using` works; Close() kept for call-site parity.
public interface IWatch : IDisposable
{
    void Close();
}

// Watcher represents a filesystem watching implementation. Use one of the
// factory functions (Watcher.Inotify/FSEvents/Kqueue/Windows/Fanotify) or
// Watcher.Default for the platform default.
//
// PORT-NOTE: 非 1:1 — all backends collapse to one FileSystemWatcher impl.
public interface IWatcher
{
    // Name returns a stable identifier ("inotify", "fsevents", "kqueue",
    // "windows", "fanotify").
    string Name();

    // Available reports whether this watcher works on the current OS.
    // FileSystemWatcher is available on all supported platforms, so this is
    // always true (Go returned per-backend availability).
    bool Available();

    // HasFastRecursiveBackend reports whether recursive watching is efficient
    // without a userspace tree walk. FileSystemWatcher.IncludeSubdirectories is
    // always subtree-native, so this is true (Go: true only for windows/fsevents).
    bool HasFastRecursiveBackend();

    // WatchDirectory watches dir for changes, calling fn with batched events.
    // By default only direct children are watched; use Watcher.WithRecursive to
    // watch the whole tree. dir must be an absolute path to an existing dir.
    IWatch WatchDirectory(string dir, WatchCallback fn, params IWatchOption[] opts);

    // WatchDirectories watches multiple directories as a batch. Same semantics
    // as calling WatchDirectory for each request. Returned watches are in the
    // same order as requests.
    IReadOnlyList<IWatch> WatchDirectories(IReadOnlyList<WatchDirectoryRequest> requests);

    // WatchFile watches a single file for changes. path must be absolute; the
    // file need not exist at subscribe time (its creation is reported). The
    // parent directory must exist. Multiple WatchFile calls for files in the
    // same directory share a single OS watch on the parent directory.
    IWatch WatchFile(string path, WatchCallback fn);
}

// Sentinel errors (Go: package-level error vars). Reference-compared exactly
// like Go's errors.Is on these singletons.
public sealed class FsWatchException : Exception
{
    public FsWatchException(string message) : base(message) { }
    public FsWatchException(string message, Exception inner) : base(message, inner) { }
}

public static class FsWatchErrors
{
    // callback must not be nil.
    public static readonly FsWatchException ErrNilCallback = new FsWatchException("fswatch: callback must not be nil");

    // cannot watch a filesystem root with no parent directory.
    public static readonly FsWatchException ErrRootPath = new FsWatchException("fswatch: cannot watch a root path");

    // path is not absolute.
    public static readonly FsWatchException ErrNotAbsolute = new FsWatchException("fswatch: path must be absolute");

    // ErrOverflow indicates the kernel event queue overflowed and some changes
    // were missed. The watch remains active; treat as a signal to rescan.
    public static readonly FsWatchException ErrOverflow = new FsWatchException("fswatch: event overflow; some changes were missed");

    // ErrWatchTerminated indicates the watch was terminated (e.g. the watched
    // directory was deleted). No further events are delivered.
    public static readonly FsWatchException ErrWatchTerminated = new FsWatchException("fswatch: watch terminated");

    // ErrUnavailable indicates a requested watcher is not available on the
    // current platform. Retained for API parity; FileSystemWatcher is always
    // available, so this is not returned by the current backend.
    public static readonly FsWatchException ErrUnavailable = new FsWatchException("fswatch: watcher not available on this platform");
}

// Factory + shared option constructors. Go exposed these as package-level
// funcs (fswatch.Default(), fswatch.WithRecursive(), ...). Gathered on a static
// class to keep the same call-site names.
public static class Watcher
{
    // WithRecursive enables recursive watching of the entire directory tree.
    // Without it, WatchDirectory watches only direct children. Go: WithRecursive().
    public static IWatchOption WithRecursive() => RecursiveOption.Instance;

    // WithIgnore filters events before delivery. If fn returns true for a path,
    // events for that path are silently dropped. The filtering is
    // per-subscriber. Go: WithIgnore(fn).
    public static IWatchOption WithIgnore(Func<string, bool> fn) => new IgnoreOption(fn);

    // Named factories. PORT-NOTE: 非 1:1 — every factory returns a
    // FileSystemWatcher-backed watcher; only Name() differs (kept for identity
    // at call sites that log or branch on watcher.Name()).
    public static IWatcher Inotify() => FsWatcher.Named("inotify");
    public static IWatcher FSEvents() => FsWatcher.Named("fsevents");
    public static IWatcher Kqueue() => FsWatcher.Named("kqueue");
    public static IWatcher Windows() => FsWatcher.Named("windows");
    public static IWatcher Fanotify() => FsWatcher.Named("fanotify");

    // Default returns the recommended watcher for the current OS. Go picked a
    // platform backend; here all map to FileSystemWatcher, but the reported
    // Name() matches what Go's Default would have chosen so identity is stable.
    public static IWatcher Default()
    {
        if (OperatingSystem.IsLinux())
        {
            return FsWatcher.Named("inotify");
        }
        if (OperatingSystem.IsMacOS())
        {
            return FsWatcher.Named("fsevents");
        }
        if (OperatingSystem.IsWindows())
        {
            return FsWatcher.Named("windows");
        }
        return FsWatcher.Named("kqueue");
    }

    // AllWatchers returns a fresh list of every watcher backend name the package
    // knows about. Go: AllWatchers(). All are FileSystemWatcher-backed here.
    public static IReadOnlyList<IWatcher> AllWatchers()
    {
        return new IWatcher[]
        {
            FsWatcher.Named("inotify"),
            FsWatcher.Named("fsevents"),
            FsWatcher.Named("kqueue"),
            FsWatcher.Named("windows"),
            FsWatcher.Named("fanotify"),
        };
    }
}

internal sealed class RecursiveOption : IWatchOption
{
    public static readonly RecursiveOption Instance = new RecursiveOption();
    public void ApplyWatchOption(WatchOptions opts) => opts.Recursive = true;
}

internal sealed class IgnoreOption : IWatchOption
{
    private readonly Func<string, bool> _fn;
    public IgnoreOption(Func<string, bool> fn) => _fn = fn;
    public void ApplyWatchOption(WatchOptions opts) => opts.Ignore = _fn;
}
