// Ported from tsgo internal/lsp/lspwatcher/lspwatcher.go (whole file).
//
// Package lspwatcher implements an in-process file watcher used as a
// drop-in replacement for LSP-based file watching when the client does not
// support dynamic registration of file watchers.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ls.LsConv;
using Ts.LsProto;
using Ts.Project.Logging;
using Ts.Vfs;
using FsWatch = Ts.FsWatch;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (plain `using Ts.TsPath;` loses to the sibling namespace at simple-name lookup)

namespace Ts.Lsp.LspWatcher;

// lspwatcher.go:26-28
// PORT-NOTE: Go's WatchDirectory returns (io.Closer, error); the C# fswatch
// IWatcher surface throws on failure instead (plan/05 §4), so Reconcile
// converts a throw back into the returned error value at the same call sites.
internal interface IWatcherBackend
{
    FsWatch.IWatch WatchDirectory(string dir, FsWatch.WatchCallback fn, params FsWatch.IWatchOption[] opts);
}

// lspwatcher.go:30-36
internal sealed class DefaultWatcherBackend(FsWatch.IWatcher watcher) : IWatcherBackend
{
    public FsWatch.IWatch WatchDirectory(string dir, FsWatch.WatchCallback fn, params FsWatch.IWatchOption[] opts)
    {
        return watcher.WatchDirectory(dir, fn, opts);
    }
}

// lspwatcher.go:38-58 — Watcher manages a set of file system subscriptions identified by
// WatcherID strings (matching the LSP server's project.WatcherID type).
// Events are delivered to onChanges in batches as `*lsproto.FileEvent`,
// shaped exactly like a `workspace/didChangeWatchedFiles` notification.
public sealed class Watcher
{
    // lspwatcher.go:22-24
    // throttleWindow mirrors VS Code's parcel watcher integration: give the
    // first batch a short grace window so adjacent filesystem bursts coalesce.
    // Go: const throttleWindow = 75 * time.Millisecond (package-level const →
    // private static field; its only reader is scheduleFlushLocked).
    private static readonly TimeSpan throttleWindow = TimeSpan.FromMilliseconds(75);

    internal readonly IFileSystem fs;
    internal readonly IWatcherBackend backend;
    private readonly Action<IReadOnlyList<FileEvent>> onChanges;
    internal readonly Logger logger;

    // Go: mu sync.Mutex → System.Threading.Lock (plan/05 §11).
    private readonly Lock mu = new();
    // watches holds the watches associated with each LSP WatcherID. A single id
    // may map to more than one watch because each FileSystemWatcher in the
    // registration becomes its own watch (different roots and kinds).
    // Go: map[string][]*watch — the nil-slice value is preserved as a null list.
    private Dictionary<string, List<Watch>?>? watches;
    private bool closed;

    // Pending batch state, protected by mu.
    private Dictionary<string, FileEvent>? pending;
    private Timer? flushTimer;

    // lspwatcher.go:93-97 — New constructs a Watcher backed by internal/fswatch's platform-default
    // watcher implementation.
    public Watcher(IFileSystem fs, Action<IReadOnlyList<FileEvent>> onChanges, Logger logger)
        : this(fs, new DefaultWatcherBackend(FsWatch.Watcher.Default()), onChanges, logger)
    {
    }

    // lspwatcher.go:99-104 — NewWithFSWatcher constructs a Watcher backed by the provided fswatch.Watcher.
    // Use this to select a specific backend (e.g. fswatch.Kqueue()) instead of the
    // platform default.
    public static Watcher NewWithFSWatcher(IFileSystem fs, FsWatch.IWatcher watcher, Action<IReadOnlyList<FileEvent>> onChanges, Logger logger)
    {
        return NewWithBackend(fs, new DefaultWatcherBackend(watcher), onChanges, logger);
    }

    // lspwatcher.go:106-114 — newWithBackend.
    internal static Watcher NewWithBackend(IFileSystem fs, IWatcherBackend backend, Action<IReadOnlyList<FileEvent>> onChanges, Logger logger)
    {
        return new Watcher(fs, backend, onChanges, logger);
    }

    private Watcher(IFileSystem fs, IWatcherBackend backend, Action<IReadOnlyList<FileEvent>> onChanges, Logger logger)
    {
        this.fs = fs;
        this.backend = backend;
        this.onChanges = onChanges;
        this.logger = logger;
        watches = new Dictionary<string, List<Watch>?>(StringComparer.Ordinal);
    }

    // lspwatcher.go:116-171 — WatchFiles subscribes to each FileSystemWatcher under the given id.
    //
    // A watcher whose directory does not exist yet is not an error: an ancestor
    // watch is installed on the nearest existing ancestor and the subscription is
    // reported as successful, so the session's notion of "this watcher is alive"
    // stays true for the subscription's whole lifetime. Only a genuine backend
    // failure (e.g. resource exhaustion while watching an existing directory)
    // causes WatchFiles to roll back the whole id and return an error, so the
    // session's pending/retry path re-registers it on the next reevaluation.
    public Exception? WatchFiles(string id, IReadOnlyList<FileSystemWatcher>? fileSystemWatchers)
    {
        lock (mu)
        {
            if (closed)
            {
                return new InvalidOperationException("lspwatcher: closed");
            }
            if (watches!.ContainsKey(id))
            {
                return new InvalidOperationException($"lspwatcher: watcher \"{id}\" already exists");
            }
            // Mark the id as existing before installing any watches so a concurrent
            // WatchFiles for the same id is rejected above.
            // PORT-NOTE: `!` — the closed check above implies watches is non-null
            // (Close nulls it only after setting closed).
            watches![id] = null;
        }

        var failed = false;
        // PORT-NOTE: Go ranges over a possibly-nil slice; null → iterate nothing.
        foreach (FileSystemWatcher fileSystemWatcher in fileSystemWatchers ?? [])
        {
            if (!LspWatcherUtil.TryWatchRoot(fileSystemWatcher, out string directory) || directory == "")
            {
                logger.Logf("lspwatcher: skipping watcher \"{0}\": unrecognized pattern \"{1}\"", id, LspWatcherUtil.WatchPatternString(fileSystemWatcher));
                continue;
            }
            var newWatch = new Watch(this, directory, LspWatcherUtil.EffectiveKind(fileSystemWatcher), LspWatcherUtil.IsRecursiveGlob(fileSystemWatcher));
            Exception? err = newWatch.Reconcile(false /*emitSynthetic*/);
            if (err != null)
            {
                logger.Logf("lspwatcher: failed to register watcher \"{0}\" for \"{1}\": {2}", id, directory, err.Message);
                newWatch.Close();
                failed = true;
                break;
            }
            lock (mu)
            {
                (watches![id] ??= []).Add(newWatch);
            }
        }

        if (failed)
        {
            // Roll back the whole id so the session's retry (MarkPending) can
            // cleanly re-register it. The session treats an id as a single unit.
            _ = UnwatchFiles(id);
            return new InvalidOperationException($"lspwatcher: failed to register one or more watchers for \"{id}\"");
        }
        return null;
    }

    // lspwatcher.go:173-187 — UnwatchFiles tears down all subscriptions associated with id.
    public Exception? UnwatchFiles(string id)
    {
        List<Watch>? watchesForId;
        lock (mu)
        {
            // Go reads a possibly-nil map after Close (yields "not found").
            if (watches == null || !watches.TryGetValue(id, out List<Watch>? found))
            {
                return new InvalidOperationException($"lspwatcher: no watcher with id \"{id}\"");
            }
            watchesForId = found;
            watches.Remove(id);
        }
        foreach (Watch watch in watchesForId ?? [])
        {
            watch.Close();
        }
        return null;
    }

    // lspwatcher.go:189-210 — Close removes every subscription. Safe to call multiple times.
    public void Close()
    {
        Dictionary<string, List<Watch>?>? watchesById;
        lock (mu)
        {
            if (closed)
            {
                return;
            }
            closed = true;
            watchesById = watches;
            watches = null;
            if (flushTimer != null)
            {
                flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                flushTimer = null;
            }
            pending = null;
        }
        foreach (List<Watch>? watchesForId in watchesById!.Values)
        {
            // map-iter: 序无关 —— 各 watch 的关闭相互独立，顺序不影响结果。
            foreach (Watch watch in watchesForId ?? [])
            {
                watch.Close();
            }
        }
    }

    // lspwatcher.go:389-429 — forwardEvents translates fswatch events into LSP file events and enqueues
    // them for the next debounced flush.
    internal void ForwardEvents(WatchKind kind, IReadOnlyList<FsWatch.Event> events)
    {
        lock (mu)
        {
            if (closed)
            {
                return;
            }
            pending ??= new Dictionary<string, FileEvent>(events.Count, StringComparer.Ordinal);
            for (int i = 0; i < events.Count; i++)
            {
                FsWatch.Event fsEvent = events[i];
                FileChangeType changeType;
                switch (fsEvent.Kind)
                {
                    case FsWatch.EventKind.EventUpdate:
                        // fswatch intentionally doesn't distinguish create vs update.
                        // For LSP consumers this is fine: callers infer create/update
                        // from their own cache and both should invalidate stale state.
                        if ((kind & (WatchKind.Create | WatchKind.Change)) == 0)
                        {
                            continue;
                        }
                        changeType = FileChangeType.Changed;
                        break;
                    case FsWatch.EventKind.EventDelete:
                        if ((kind & WatchKind.Delete) == 0)
                        {
                            continue;
                        }
                        changeType = FileChangeType.Deleted;
                        break;
                    default:
                        continue;
                }

                string path = TsPathApi.NormalizeSlashes(fsEvent.Path);
                DocumentUri uri = Converters.FileNameToDocumentURI(path);
                pending[uri.Value] = new FileEvent
                {
                    Uri = uri,
                    Type = changeType,
                };
            }
            ScheduleFlushLocked();
        }
    }

    // lspwatcher.go:431-464 — emitSyntheticCreates enqueues synthetic create events after a target watch is
    // (re)installed following a missing→present transition, so the session
    // re-resolves files that appeared before the real watch existed. The target
    // directory itself is always included; for a non-recursive watch its immediate
    // children are added, and for a recursive watch its whole subtree is walked.
    // Nothing is emitted if the watch doesn't request create notifications.
    internal void EmitSyntheticCreates(string directory, WatchKind kind, bool recursive)
    {
        if ((kind & WatchKind.Create) == 0)
        {
            return;
        }
        var paths = new List<string> { directory };
        if (recursive)
        {
            _ = fs.WalkDir(directory, (path, entry, err) =>
            {
                if (err != null)
                {
                    return null;
                }
                string normalizedPath = TsPathApi.NormalizeSlashes(path);
                if (normalizedPath == directory)
                {
                    return null;
                }
                paths.Add(normalizedPath);
                return null;
            });
        }
        else
        {
            Entries entries = fs.GetAccessibleEntries(directory);
            foreach (string name in entries.Files ?? [])
            {
                paths.Add(TsPathApi.CombinePaths(directory, name));
            }
            foreach (string name in entries.Directories ?? [])
            {
                paths.Add(TsPathApi.CombinePaths(directory, name));
            }
        }
        EnqueueSyntheticCreates(paths);
    }

    // lspwatcher.go:466-490 — enqueueSyntheticCreates adds synthetic create events for paths, without
    // clobbering a more specific event already pending for the same path (e.g. a
    // real delete).
    private void EnqueueSyntheticCreates(List<string> paths)
    {
        lock (mu)
        {
            if (closed)
            {
                return;
            }
            pending ??= new Dictionary<string, FileEvent>(paths.Count, StringComparer.Ordinal);
            foreach (string path in paths)
            {
                DocumentUri uri = Converters.FileNameToDocumentURI(path);
                if (pending.ContainsKey(uri.Value))
                {
                    continue;
                }
                pending[uri.Value] = new FileEvent
                {
                    Uri = uri,
                    Type = FileChangeType.Created,
                };
            }
            ScheduleFlushLocked();
        }
    }

    // lspwatcher.go:492-498 — scheduleFlushLocked arms the debounce flush timer if it isn't already armed.
    // Callers must hold mu.
    private void ScheduleFlushLocked()
    {
        if (flushTimer == null)
        {
            // Go: time.AfterFunc(throttleWindow, w.flush) → one-shot System.Threading.Timer.
            flushTimer = new Timer(static state => ((Watcher)state!).Flush(), this, throttleWindow, Timeout.InfiniteTimeSpan);
        }
    }

    // lspwatcher.go:500-519
    private void Flush()
    {
        Dictionary<string, FileEvent>? pending;
        lock (mu)
        {
            if (closed)
            {
                return;
            }
            pending = this.pending;
            this.pending = null;
            flushTimer = null;
        }

        if (pending == null || pending.Count == 0)
        {
            return;
        }
        var changes = new List<FileEvent>(pending.Count);
        // map-iter: 序无关 —— Go map 迭代序随机；会话按 URI 独立消费事件，批次内顺序无意义。
        foreach (FileEvent fileEvent in pending.Values)
        {
            changes.Add(fileEvent);
        }
        onChanges(changes.ToArray());
    }
}

// lspwatcher.go:60-79 — watch represents one FileSystemWatcher from the LSP registration.
//
// The directory the session asks to watch may not exist yet (common in
// granular mode, where each probed-but-missing package directory becomes a
// watch) or may be deleted while watched. To honor the watch across those
// transitions, a watch maintains either:
//
//   - a "target" subscription rooted directly at the requested directory, once
//     it exists, or
//   - an "ancestor" subscription on the nearest existing ancestor
//     (non-recursive), used to detect the requested directory — or an
//     intermediate path component — being created, after which the watch
//     descends toward and eventually promotes to the target.
//
// When the target materializes, synthetic create events are emitted for it
// (and, depending on whether the watch is recursive, its immediate children or
// its whole subtree) so the session re-resolves files that appeared in the gap
// before the real subscription was installed.
//
// All path fields are tspath-style (forward-slash) absolute paths.
internal sealed class Watch
{
    internal readonly Watcher watcher;
    internal readonly string requestedDirectory; // directory requested by the LSP layer (possibly a symlink)
    internal readonly WatchKind kind;
    internal readonly bool recursive; // whether the target subscription should be recursive

    // Go: mu sync.Mutex → System.Threading.Lock (plan/05 §11).
    private readonly Lock mu = new();
    private IDisposable? subscription; // current subscription (target or ancestor); nil if none
    private string watchedDirectory = ""; // canonicalized directory 'subscription' is rooted at
    private bool watchingTarget; // whether 'subscription' is rooted at the target directory
    private bool closed;

    // Go struct literal in WatchFiles → constructor.
    internal Watch(Watcher watcher, string requestedDirectory, WatchKind kind, bool recursive)
    {
        this.watcher = watcher;
        this.requestedDirectory = requestedDirectory;
        this.kind = kind;
        this.recursive = recursive;
    }

    // lspwatcher.go:212-224 — close tears down the watch's current subscription and prevents any in-flight
    // reconcile from reinstalling one.
    internal void Close()
    {
        IDisposable? closing;
        lock (mu)
        {
            closed = true;
            closing = subscription;
            subscription = null;
            watchedDirectory = "";
        }
        closing?.Dispose();
    }

    // lspwatcher.go:226-307 — reconcile installs or advances this watch toward the target directory based
    // on the current filesystem state. It is called at registration, whenever a
    // ancestor watch observes activity, and after a target watch is terminated by
    // deletion.
    //
    // emitSynthetic controls whether promoting to the target emits synthetic
    // create events: false for the initial install when the target already exists
    // (the session already knows about those files), true for any missing→present
    // recovery.
    //
    // It returns a non-nil error only on a genuine backend failure to install a
    // watch; a missing target directory is handled by installing an ancestor watch
    // and returns nil.
    //
    // PORT-NOTE: Go's `subscription, err := ...` shadows locals per branch; C#
    // forbids redeclaration across nested blocks (CS0136), so the locals carry a
    // Target/Drop/Ancestor suffix matching the branch they serve.
    internal Exception? Reconcile(bool emitSyntheticCreates)
    {
        lock (mu)
        {
            Watcher watcher = this.watcher;
            while (true)
            {
                if (closed)
                {
                    return null;
                }
                if (watcher.fs.DirectoryExists(requestedDirectory))
                {
                    if (watchingTarget && subscription != null)
                    {
                        return null; // already watching the target
                    }
                    string targetDirectory = requestedDirectory;
                    List<FsWatch.IWatchOption> options = [];
                    if (recursive)
                    {
                        options.Add(FsWatch.Watcher.WithRecursive());
                    }
                    FsWatch.IWatch targetSubscription;
                    try
                    {
                        targetSubscription = watcher.backend.WatchDirectory(targetDirectory, TargetCallback(targetDirectory), options.ToArray());
                    }
                    catch (Exception targetErr)
                    {
                        return targetErr;
                    }
                    IDisposable? targetPrevious = subscription;
                    subscription = targetSubscription;
                    watchedDirectory = targetDirectory;
                    watchingTarget = true;
                    targetPrevious?.Dispose();
                    if (emitSyntheticCreates)
                    {
                        watcher.EmitSyntheticCreates(targetDirectory, kind, recursive);
                    }
                    return null;
                }

                if (!LspWatcherUtil.TryNearestExistingAncestor(watcher.fs, requestedDirectory, out string ancestor))
                {
                    // Nothing exists to watch (even the root is gone); drop any subscription.
                    if (subscription != null)
                    {
                        IDisposable? dropPrevious = subscription;
                        subscription = null;
                        watchedDirectory = "";
                        watchingTarget = false;
                        dropPrevious.Dispose();
                    }
                    return null;
                }
                string ancestorDirectory = ancestor;
                if (!watchingTarget && subscription != null && watchedDirectory == ancestorDirectory)
                {
                    return null; // already watching the correct ancestor
                }
                FsWatch.IWatch ancestorSubscription;
                try
                {
                    ancestorSubscription = watcher.backend.WatchDirectory(ancestorDirectory, AncestorCallback());
                }
                catch (Exception ancestorErr)
                {
                    return ancestorErr;
                }
                IDisposable? ancestorPrevious = subscription;
                subscription = ancestorSubscription;
                watchedDirectory = ancestorDirectory;
                watchingTarget = false;
                ancestorPrevious?.Dispose();
                // The target may have appeared between the DirectoryExists check above
                // and installing this ancestor subscription (e.g. an atomic tree
                // creation), so loop to descend further or promote immediately. Any
                // promotion from here on is a missing→present transition, so synthesize
                // creates.
                emitSyntheticCreates = true;
            }
        }
    }

    // lspwatcher.go:309-337 — targetCallback returns the fswatch callback for a target watch rooted at
    // watchedReal. It forwards events to the session and, on ErrWatchTerminated
    // (the watched directory was deleted), falls back to watching the nearest
    // existing ancestor so the watch re-attaches when the directory is recreated.
    internal FsWatch.WatchCallback TargetCallback(string watchedDirectory)
    {
        Watcher watcher = this.watcher;
        return (events, err) =>
        {
            var terminated = false;
            if (err != null)
            {
                if (LspWatcherUtil.IsError(err, FsWatch.FsWatchErrors.ErrOverflow))
                {
                    watcher.logger.Logf("lspwatcher: watch overflow in \"{0}\" (some events may have been dropped): {1}", watchedDirectory, err.Message);
                }
                else if (LspWatcherUtil.IsError(err, FsWatch.FsWatchErrors.ErrWatchTerminated))
                {
                    terminated = true;
                    watcher.logger.Logf("lspwatcher: watch terminated in \"{0}\" (directory removed): {1}", watchedDirectory, err.Message);
                }
                else
                {
                    watcher.logger.Logf("lspwatcher: watch error in \"{0}\": {1}", watchedDirectory, err.Message);
                }
            }
            if (events.Count > 0)
            {
                watcher.ForwardEvents(kind, events);
            }
            if (terminated)
            {
                // The delete event for the directory was forwarded above; now
                // re-attach to the nearest existing ancestor.
                HandleTerminated();
            }
        };
    }

    // lspwatcher.go:339-361 — handleTerminated clears the dead target watch (the backend has already
    // removed it) and re-evaluates, falling back to an ancestor watch on the nearest
    // existing ancestor so the watch re-attaches when the directory reappears.
    // Clearing the state first is essential: reconcile would otherwise see
    // watchingTarget && subscription != nil and conclude the target is already
    // watched, even though the subscription is dead — losing recovery if the
    // directory is recreated before reconcile runs.
    private void HandleTerminated()
    {
        IDisposable? previous;
        lock (mu)
        {
            if (closed)
            {
                return;
            }
            previous = subscription;
            subscription = null;
            watchedDirectory = "";
            watchingTarget = false;
        }
        previous?.Dispose();
        _ = Reconcile(true /*emitSyntheticCreates*/);
    }

    // lspwatcher.go:363-371 — ancestorCallback returns the fswatch callback for an ancestor watch. Ancestor
    // watches exist only to detect the target — or an intermediate path component —
    // being created; their events are about ancestor directories the session
    // doesn't track, so they are ignored and the watch is simply re-evaluated.
    private FsWatch.WatchCallback AncestorCallback()
    {
        return (events, err) =>
        {
            _ = Reconcile(true /*emitSyntheticCreates*/);
        };
    }
}

// lspwatcher.go package-level funcs (naming per the OsVfsUtil precedent for
// package-level members).
internal static class LspWatcherUtil
{
    // lspwatcher.go:373-387 — nearestExistingAncestor returns the deepest existing directory that is dir or
    // an ancestor of dir, walking upward. ok is false only if nothing in the chain
    // (including the root) exists.
    internal static bool TryNearestExistingAncestor(IFileSystem fs, string dir, out string existing)
    {
        while (true)
        {
            if (fs.DirectoryExists(dir))
            {
                existing = dir;
                return true;
            }
            string parent = TsPathApi.GetDirectoryPath(dir);
            if (parent == dir)
            {
                existing = "";
                return false;
            }
            dir = parent;
        }
    }

    // lspwatcher.go:521-546 — watchRoot extracts the directory the fswatch subscription should be
    // rooted at from a FileSystemWatcher. The patterns the project layer
    // produces are of the form `<dir>/**/*` (recursive) or `<dir>/*`
    // (non-recursive, used by granular watch mode), either as a Pattern
    // with a fully-qualified directory or as a RelativePattern with a
    // file:// BaseUri, so the heuristic of "everything before the first
    // glob meta character" is reliable. Use [isRecursiveGlob] to determine
    // whether the subscription should be recursive.
    //
    // Returned roots are tspath-normalized (forward-slash) absolute paths.
    internal static bool TryWatchRoot(FileSystemWatcher fileSystemWatcher, out string directory)
    {
        if (fileSystemWatcher.GlobPattern.Pattern != null)
        {
            directory = RootFromGlob(fileSystemWatcher.GlobPattern.Pattern);
            return true;
        }
        RelativePattern? relativePattern = fileSystemWatcher.GlobPattern.RelativePattern;
        if (relativePattern != null)
        {
            string baseUri;
            if (relativePattern.BaseUri.URI != null)
            {
                baseUri = new DocumentUri(relativePattern.BaseUri.URI.Value.Value).FileName();
            }
            else
            {
                directory = "";
                return false;
            }
            string pattern = TsPathApi.CombinePaths(baseUri, relativePattern.Pattern);
            directory = RootFromGlob(pattern);
            return true;
        }
        directory = "";
        return false;
    }

    // lspwatcher.go:548-568 — rootFromGlob.
    internal static string RootFromGlob(string pattern)
    {
        pattern = TsPathApi.NormalizeSlashes(pattern);
        var metaIndex = -1;
        for (int i = 0; i < pattern.Length; i++)
        {
            switch (pattern[i])
            {
                case '*':
                case '?':
                case '[':
                case '{':
                    metaIndex = i;
                    break;
            }
            if (metaIndex != -1)
            {
                break;
            }
        }
        if (metaIndex == -1)
        {
            return TsPathApi.NormalizePath(pattern.TrimEnd('/'));
        }
        string directory = pattern[..metaIndex].TrimEnd('/');
        if (directory == "")
        {
            return "";
        }
        return TsPathApi.NormalizePath(directory);
    }

    // lspwatcher.go:570-582 — watchPatternString.
    internal static string WatchPatternString(FileSystemWatcher fileSystemWatcher)
    {
        if (fileSystemWatcher.GlobPattern.Pattern != null)
        {
            return fileSystemWatcher.GlobPattern.Pattern;
        }
        RelativePattern? relativePattern = fileSystemWatcher.GlobPattern.RelativePattern;
        if (relativePattern != null)
        {
            var baseUri = "";
            if (relativePattern.BaseUri.URI != null)
            {
                baseUri = relativePattern.BaseUri.URI.Value.Value;
            }
            return baseUri + "/" + relativePattern.Pattern;
        }
        return "";
    }

    // lspwatcher.go:584-589 — isRecursiveGlob reports whether a FileSystemWatcher's pattern requests
    // recursive watching (contains a `**` segment). Granular watch mode emits
    // non-recursive `<dir>/*` patterns, which watch only the immediate directory.
    internal static bool IsRecursiveGlob(FileSystemWatcher fileSystemWatcher)
    {
        return WatchPatternString(fileSystemWatcher).Contains("**", StringComparison.Ordinal);
    }

    // lspwatcher.go:591-596 — effectiveKind.
    internal static WatchKind EffectiveKind(FileSystemWatcher fileSystemWatcher)
    {
        if (fileSystemWatcher.Kind != null)
        {
            return fileSystemWatcher.Kind.Value;
        }
        return WatchKind.Create | WatchKind.Change | WatchKind.Delete;
    }

    // PORT-NOTE (no Go counterpart): the errors.Is equivalent for the sentinel
    // checks in targetCallback. The C# fswatch backend delivers FsWatchException
    // copies whose Message matches the sentinel (WatchManager precedent:
    // ReferenceEquals || Message equality), and the tests join errors with
    // AggregateException (Go: errors.Join) — so walk identity, then Message
    // equality, through InnerException and AggregateException chains.
    internal static bool IsError(Exception? err, Exception target)
    {
        for (Exception? current = err; current != null; current = current.InnerException)
        {
            if (ReferenceEquals(current, target) || current.Message == target.Message)
            {
                return true;
            }
            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    if (IsError(inner, target))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
