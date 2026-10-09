// Ported from tsgo internal/lsp/lspwatcher/lspwatcher_test.go (whole file).
//
// PORT-NOTES:
//  - Go's bundled.WrapFS(osvfs.FS()) → Ts.Bundled.BundledApi.WrapFS(Ts.Vfs.OsVfs.OsVfsUtil.FS()).
//  - t.TempDir() → a private TempDir : IDisposable fixture (plan/05 §16.4 leaves the
//    Ts.TestUtil registration to a later work item).
//  - TestWatcher_SyntheticCreateDepth's two t.Run subtests split into two [Fact]s
//    (plan/05 §16.1, non-table-driven scenario grouping).
//  - errors.Join(fswatch.ErrWatchTerminated, errors.New("...")) → AggregateException;
//    Watcher-side detection goes through LspWatcherUtil.IsError (the errors.Is walk).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Ts.Bundled;
using Ts.LsProto;
using Ts.Project.Logging;
using Ts.Vfs;
using Ts.Vfs.OsVfs;
using FsWatch = Ts.FsWatch;
using BundledApi = Ts.Bundled.Bundled; // plain `Bundled` binds to the enclosing Ts.Bundled namespace
using FileSystemWatcher = Ts.LsProto.FileSystemWatcher; // vs System.IO.FileSystemWatcher
using TsPathApi = Ts.TsPath.TsPath;
using Xunit;

namespace Ts.Lsp.LspWatcher.Tests;

// lspwatcher_test.go:21-31 — waitFor.
internal static class LspWatcherTestUtil
{
    internal static void WaitFor(Func<bool> cond, string msg)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (cond())
            {
                return;
            }
            Thread.Sleep(20);
        }
        Assert.Fail($"timed out waiting for {msg}");
    }

    // Go: t.TempDir() — a unique temp directory removed at test end.
    internal sealed class TempDir : IDisposable
    {
        internal string Dir { get; }

        internal TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "lspwatcher-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best-effort, like Go's t.TempDir cleanup; a lingering temp
                // directory must not fail the test.
            }
        }
    }

    internal static TempDir NewTempDir() => new();

    // Go: filepath.FromSlash(filepath.Join(a, b)) — OS-separator join of slash paths.
    internal static string OsJoin(string a, string b)
    {
        return Path.Combine(a, b).Replace('/', Path.DirectorySeparatorChar);
    }
}

public sealed class LspWatcherTests : IDisposable
{
    private readonly LspWatcherTestUtil.TempDir tmp = LspWatcherTestUtil.NewTempDir();

    public void Dispose()
    {
        tmp.Dispose();
    }

    // lspwatcher_test.go:33-98 — TestWatcher_CreateChangeDelete.
    [Fact]
    public void Watcher_CreateChangeDelete()
    {
        string dir = tmp.Dir;

        var mu = new Lock();
        var batches = new List<FileEvent[]>();
        var w = Watcher.New(BundledApi.WrapFS(OsVfsUtil.FS()), changes =>
        {
            lock (mu)
            {
                batches.Add(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string pattern = TsPathApi.NormalizeSlashes(dir) + "/**/*";
            var kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete;
            Assert.Null(w.WatchFiles("test",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern }, Kind = kind },
            ]));

            Thread.Sleep(200);

            string file = Path.Combine(dir, "a.ts");
            File.WriteAllText(file, "export {}");

            List<FileEvent> Collected()
            {
                lock (mu)
                {
                    var all = new List<FileEvent>();
                    foreach (FileEvent[] b in batches)
                    {
                        all.AddRange(b);
                    }
                    return all;
                }
            }

            LspWatcherTestUtil.WaitFor(() =>
            {
                foreach (FileEvent e in Collected())
                {
                    if (e.Type == FileChangeType.Changed)
                    {
                        return true;
                    }
                }
                return false;
            }, "update event");

            // PORT-NOTE (timing only; assertions unchanged): on macOS the CoreFX
            // FileSystemWatcher emits spurious Changed+Created events immediately
            // before a Deleted one. If the delete lands in the same fswatch
            // debounce batch as those phantoms, the eventList's Go-faithful
            // create+delete cancellation drops it (Go's own backends produce no
            // phantoms). Letting the create batch settle (> Debounce.MaxWaitTime
            // = 500ms) before deleting keeps this real-backend test stable.
            Thread.Sleep(600);
            File.Delete(file);
            LspWatcherTestUtil.WaitFor(() =>
            {
                foreach (FileEvent e in Collected())
                {
                    if (e.Type == FileChangeType.Deleted)
                    {
                        return true;
                    }
                }
                return false;
            }, "delete event");

            Assert.Null(w.UnwatchFiles("test"));
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:100-148 — TestWatcher_KindFilter.
    [Fact]
    public void Watcher_KindFilter()
    {
        string dir = tmp.Dir;
        string dirNorm = TsPathApi.NormalizeSlashes(dir);

        var mu = new Lock();
        var got = new List<FileEvent>();
        var backend = new FakeBackend();
        var w = Watcher.NewWithBackend(BundledApi.WrapFS(OsVfsUtil.FS()), backend, changes =>
        {
            lock (mu)
            {
                got.AddRange(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string pattern = dirNorm + "/**/*";
            var kind = WatchKind.Delete;
            Assert.Null(w.WatchFiles("test",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern }, Kind = kind },
            ]));
            backend.EmitAll(
            [
                new FsWatch.Event(FsWatch.EventKind.EventUpdate, LspWatcherTestUtil.OsJoin(dirNorm, "x.ts")),
                new FsWatch.Event(FsWatch.EventKind.EventDelete, LspWatcherTestUtil.OsJoin(dirNorm, "x.ts")),
            ], null);

            LspWatcherTestUtil.WaitFor(() =>
            {
                lock (mu)
                {
                    foreach (FileEvent e in got)
                    {
                        if (e.Type == FileChangeType.Deleted)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }, "delete event");

            lock (mu)
            {
                foreach (FileEvent e in got)
                {
                    Assert.True(e.Type == FileChangeType.Deleted, $"unexpected non-delete event: {e.Type} {e.Uri.Value}");
                }
            }
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:150-166 — TestRootFromGlob.
    [Fact]
    public void RootFromGlob()
    {
        (string Pattern, string Want)[] cases =
        [
            ("/abs/path/**/*", "/abs/path"),
            ("/abs/path/", "/abs/path"),
            ("/abs/path/?.ts", "/abs/path"),
            ("/abs/path/{a,b}/*", "/abs/path"),
        ];
        foreach ((string pattern, string want) in cases)
        {
            Assert.Equal(want, LspWatcherUtil.RootFromGlob(pattern));
        }
    }

    // lspwatcher_test.go:245-297 — TestWatcher_BookkeepingAndOverflow.
    [Fact]
    public void Watcher_BookkeepingAndOverflow()
    {
        string dir = tmp.Dir;
        string dirNorm = TsPathApi.NormalizeSlashes(dir);
        string pattern = dirNorm + "/**/*";

        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var mu = new Lock();
        var got = new List<FileEvent>();
        var w = Watcher.NewWithBackend(fs, backend, changes =>
        {
            lock (mu)
            {
                got.AddRange(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));

        Assert.Null(w.WatchFiles("id",
        [
            new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
        ]));
        Assert.NotNull(w.WatchFiles("id",
        [
            new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
        ]));

        backend.EmitAll(
        [
            new FsWatch.Event(FsWatch.EventKind.EventUpdate, LspWatcherTestUtil.OsJoin(dirNorm, "a.ts")),
        ], FsWatch.FsWatchErrors.ErrOverflow);
        LspWatcherTestUtil.WaitFor(() =>
        {
            lock (mu)
            {
                return got.Count > 0;
            }
        }, "events after overflow");

        Assert.NotNull(w.UnwatchFiles("missing"));
        Assert.Null(w.UnwatchFiles("id"));
        Assert.Null(w.WatchFiles("id2", null));
        w.Close();
        Assert.NotNull(w.WatchFiles("id3", null));
    }

    // lspwatcher_test.go:299-331 — TestWatcher_NonRecursiveGlobIsNotRecursive.
    [Fact]
    public void Watcher_NonRecursiveGlobIsNotRecursive()
    {
        string dir = tmp.Dir;
        string dirNorm = TsPathApi.NormalizeSlashes(dir);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        string subNorm = TsPathApi.NormalizeSlashes(Path.Combine(dir, "sub"));

        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var w = Watcher.NewWithBackend(fs, backend, _ => { }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string recursive = dirNorm + "/**/*";
            string nonRecursive = subNorm + "/*";
            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = recursive } },
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = nonRecursive } },
            ]));

            lock (backend.mu)
            {
                Assert.True(backend.optCount[dirNorm] == 1,
                    $"recursive glob \"{recursive}\": expected 1 watch option (WithRecursive), got {backend.optCount[dirNorm]}");
                Assert.True(backend.optCount[subNorm] == 0,
                    $"non-recursive glob \"{nonRecursive}\": expected 0 watch options, got {backend.optCount[subNorm]}");
            }
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:333-391 — TestWatcher_RealBackend_MissingThenCreate.
    [Fact]
    public void Watcher_RealBackend_MissingThenCreate()
    {
        string dir = tmp.Dir;
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());

        var mu = new Lock();
        var batches = new List<FileEvent[]>();
        var w = Watcher.New(fs, changes =>
        {
            lock (mu)
            {
                batches.Add(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            // Watch a directory that does not exist yet.
            string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "pkg"));
            string pattern = target + "/*";
            var kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete;
            Assert.Null(w.WatchFiles("test",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern }, Kind = kind },
            ]));

            // Give the ancestor watch time to install.
            Thread.Sleep(200);

            // Create the target directory and a file inside it. The real backend's
            // ancestor watch should fire, promote to the target, and
            // the file should ultimately surface.
            Directory.CreateDirectory(Path.Combine(dir, "pkg"));
            File.WriteAllText(Path.Combine(dir, "pkg", "index.ts"), "export {}");

            List<FileEvent> Collected()
            {
                lock (mu)
                {
                    var all = new List<FileEvent>();
                    foreach (FileEvent[] b in batches)
                    {
                        all.AddRange(b);
                    }
                    return all;
                }
            }

            LspWatcherTestUtil.WaitFor(() =>
            {
                foreach (FileEvent e in Collected())
                {
                    if (e.Uri.Value.EndsWith("/pkg/index.ts", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }, "event for file created in a previously-missing directory");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:393-427 — TestWatcher_MissingDirectoryTracksAncestor.
    [Fact]
    public void Watcher_MissingDirectoryTracksAncestor()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var w = Watcher.NewWithBackend(fs, backend, _ => { }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string dir = tmp.Dir;
            string baseNorm = TsPathApi.NormalizeSlashes(dir);
            string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "pkg"));
            string pattern = target + "/*";

            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
            ]));

            // A missing target directory installs an ancestor watch on the nearest
            // existing ancestor (the base dir), not on the target.
            Assert.True(backend.IsWatching(baseNorm),
                $"expected ancestor watch on ancestor \"{baseNorm}\", watched: [{string.Join(" ", backend.WatchedDirs())}]");
            List<string> dirs = backend.WatchedDirs();
            Assert.True(dirs.Count == 1, $"expected exactly one (ancestor) watch, got [{string.Join(" ", dirs)}]");

            Assert.Null(w.UnwatchFiles("id"));
            List<string> after = backend.WatchedDirs();
            Assert.True(after.Count == 0, $"expected all watches closed after unwatch, got [{string.Join(" ", after)}]");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:429-491 — TestWatcher_MissingDirectoryPromotesOnCreate.
    [Fact]
    public void Watcher_MissingDirectoryPromotesOnCreate()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var mu = new Lock();
        var got = new List<FileEvent>();
        var w = Watcher.NewWithBackend(fs, backend, changes =>
        {
            lock (mu)
            {
                got.AddRange(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string dir = tmp.Dir;
            string baseNorm = TsPathApi.NormalizeSlashes(dir);
            string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "pkg"));
            string pattern = target + "/*";
            var kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete;

            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern }, Kind = kind },
            ]));

            // Create the target directory with a file, then notify the ancestor watch.
            Directory.CreateDirectory(Path.Combine(dir, "pkg"));
            File.WriteAllText(Path.Combine(dir, "pkg", "index.ts"), "export {}");
            backend.Emit(baseNorm,
            [
                new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "pkg")),
            ], null);

            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(target), "promotion to target watch");

            // Synthetic creates must cover the target dir and its immediate child so
            // the session re-resolves files created before the watch was installed.
            LspWatcherTestUtil.WaitFor(() =>
            {
                lock (mu)
                {
                    var sawDir = false;
                    var sawChild = false;
                    foreach (FileEvent e in got)
                    {
                        if (e.Type != FileChangeType.Created)
                        {
                            continue;
                        }
                        string s = e.Uri.Value;
                        if (s.EndsWith("/pkg", StringComparison.Ordinal))
                        {
                            sawDir = true;
                        }
                        if (s.EndsWith("/pkg/index.ts", StringComparison.Ordinal))
                        {
                            sawChild = true;
                        }
                    }
                    return sawDir && sawChild;
                }
            }, "synthetic create events for target and child");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:493-535 — TestWatcher_MultiLevelDescend.
    [Fact]
    public void Watcher_MultiLevelDescend()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var w = Watcher.NewWithBackend(fs, backend, _ => { }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string dir = tmp.Dir;
            string baseNorm = TsPathApi.NormalizeSlashes(dir);
            string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "a", "b", "c"));
            string pattern = target + "/*";

            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
            ]));
            Assert.True(backend.IsWatching(baseNorm),
                $"expected initial ancestor watch on \"{baseNorm}\", got [{string.Join(" ", backend.WatchedDirs())}]");

            // Reveal one path component at a time; the ancestor watch should descend.
            string MkdirAndPath(string rel)
            {
                string p = Path.Combine(dir, rel);
                Directory.CreateDirectory(p);
                return TsPathApi.NormalizeSlashes(p);
            }

            string aDir = MkdirAndPath("a");
            backend.Emit(baseNorm, [new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "a"))], null);
            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(aDir), "descend to a");

            string abDir = MkdirAndPath(Path.Combine("a", "b"));
            backend.Emit(aDir, [new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "a", "b"))], null);
            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(abDir), "descend to a/b");

            string abcDir = MkdirAndPath(Path.Combine("a", "b", "c"));
            backend.Emit(abDir, [new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "a", "b", "c"))], null);
            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(abcDir), "promote to target a/b/c");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:537-565 — TestWatcher_AtomicTreeCreateRace.
    [Fact]
    public void Watcher_AtomicTreeCreateRace()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var w = Watcher.NewWithBackend(fs, backend, _ => { }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string dir = tmp.Dir;
            string baseNorm = TsPathApi.NormalizeSlashes(dir);
            string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "a", "b", "c"));
            string pattern = target + "/*";

            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
            ]));

            // The whole tree appears at once (e.g. an extraction/symlink). A single
            // notification on the base ancestor watch must descend all the way and
            // promote to the target in one reconcile pass.
            Directory.CreateDirectory(Path.Combine(dir, "a", "b", "c"));
            backend.Emit(baseNorm, [new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "a"))], null);

            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(target), "promote to target in one pass");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:567-665 — TestWatcher_SyntheticCreateDepth (the two t.Run
    // subtests map to separate Facts per plan/05 §16.1).
    [Fact]
    public void Watcher_SyntheticCreateDepth_NonRecursive()
    {
        SyntheticCreateDepth(recursive: false);
    }

    [Fact]
    public void Watcher_SyntheticCreateDepth_Recursive()
    {
        SyntheticCreateDepth(recursive: true);
    }

    private void SyntheticCreateDepth(bool recursive)
    {
        string dir = tmp.Dir;
        string baseNorm = TsPathApi.NormalizeSlashes(dir);
        string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "pkg"));
        var kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete;
        string pattern = recursive ? target + "/**/*" : target + "/*";

        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var mu = new Lock();
        var got = new List<FileEvent>();
        var w = Watcher.NewWithBackend(fs, backend, changes =>
        {
            lock (mu)
            {
                got.AddRange(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern }, Kind = kind },
            ]));

            // Materialize the target with a nested file under a subdirectory.
            Directory.CreateDirectory(Path.Combine(dir, "pkg", "sub"));
            File.WriteAllText(Path.Combine(dir, "pkg", "top.ts"), "export {}");
            File.WriteAllText(Path.Combine(dir, "pkg", "sub", "deep.ts"), "export {}");
            backend.Emit(baseNorm, [new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "pkg"))], null);

            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(target), "promotion");

            HashSet<string> Created()
            {
                lock (mu)
                {
                    var m = new HashSet<string>(StringComparer.Ordinal);
                    foreach (FileEvent e in got)
                    {
                        if (e.Type == FileChangeType.Created)
                        {
                            m.Add(e.Uri.Value);
                        }
                    }
                    return m;
                }
            }

            // Both modes must synthesize the immediate child.
            LspWatcherTestUtil.WaitFor(() =>
            {
                foreach (string s in Created())
                {
                    if (s.EndsWith("/pkg/top.ts", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }, "synthetic create for immediate child");

            // Only the recursive watch should synthesize the deep descendant.
            bool SawDeep()
            {
                foreach (string s in Created())
                {
                    if (s.EndsWith("/pkg/sub/deep.ts", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                return false;
            }

            if (recursive)
            {
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < deadline && !SawDeep())
                {
                    Thread.Sleep(20);
                }
                Assert.True(SawDeep(), $"recursive watch should synthesize deep descendant; got [{string.Join(" ", Created())}]");
            }
            else
            {
                Thread.Sleep(300); // allow any erroneous deep event to arrive
                Assert.False(SawDeep(), $"non-recursive watch must not synthesize deep descendant; got [{string.Join(" ", Created())}]");
            }
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:667-731 — TestWatcher_TerminatedFallsBackAndRecovers.
    [Fact]
    public void Watcher_TerminatedFallsBackAndRecovers()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var mu = new Lock();
        var got = new List<FileEvent>();
        var w = Watcher.NewWithBackend(fs, backend, changes =>
        {
            lock (mu)
            {
                got.AddRange(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string dir = tmp.Dir;
            string baseNorm = TsPathApi.NormalizeSlashes(dir);
            string target = TsPathApi.NormalizeSlashes(Path.Combine(dir, "pkg"));
            Directory.CreateDirectory(Path.Combine(dir, "pkg"));
            string pattern = target + "/*";
            var kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete;

            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern }, Kind = kind },
            ]));
            Assert.True(backend.IsWatching(target),
                $"expected target watch on \"{target}\", got [{string.Join(" ", backend.WatchedDirs())}]");

            // Delete the directory and deliver ErrWatchTerminated together with the
            // directory's own delete event (as the real backends do).
            // Go: errors.Join(fswatch.ErrWatchTerminated, errors.New("removed")).
            Directory.Delete(Path.Combine(dir, "pkg"));
            backend.Emit(target,
            [
                new FsWatch.Event(FsWatch.EventKind.EventDelete, Path.Combine(dir, "pkg")),
            ], new AggregateException(FsWatch.FsWatchErrors.ErrWatchTerminated, new InvalidOperationException("removed")));

            // The delete must be forwarded, and the watch must fall back to watching
            // the ancestor.
            LspWatcherTestUtil.WaitFor(() =>
            {
                lock (mu)
                {
                    foreach (FileEvent e in got)
                    {
                        if (e.Type == FileChangeType.Deleted && e.Uri.Value.EndsWith("/pkg", StringComparison.Ordinal))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }, "forwarded delete of terminated dir");
            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(baseNorm) && !backend.IsWatching(target), "fallback to ancestor watch");

            // Recreate the directory; the ancestor watch must promote back to target.
            Directory.CreateDirectory(Path.Combine(dir, "pkg"));
            backend.Emit(baseNorm, [new FsWatch.Event(FsWatch.EventKind.EventUpdate, Path.Combine(dir, "pkg"))], null);
            LspWatcherTestUtil.WaitFor(() => backend.IsWatching(target), "recovery to target watch after recreation");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:733-772 — TestWatcher_GenuineFailureRollsBackForRetry.
    [Fact]
    public void Watcher_GenuineFailureRollsBackForRetry()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var w = Watcher.NewWithBackend(fs, backend, _ => { }, LoggingUtil.NewLogger(Console.Error));
        try
        {
            string dir = tmp.Dir;
            string dirNorm = TsPathApi.NormalizeSlashes(dir);
            string pattern = dirNorm + "/*";

            // Inject a genuine backend failure for the existing directory.
            lock (backend.mu)
            {
                backend.failDirs[dirNorm] = new InvalidOperationException("too many open files");
            }

            Exception? err = w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
            ]);
            Assert.NotNull(err);

            // The id must have been rolled back so a retry can re-register cleanly
            // (rather than hitting the duplicate-id error). Clear the injected failure
            // to simulate the resource pressure easing on retry.
            lock (backend.mu)
            {
                backend.failDirs.Remove(dirNorm);
            }

            Assert.Null(w.WatchFiles("id",
            [
                new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
            ]));
            Assert.True(backend.IsWatching(dirNorm),
                $"expected watch on \"{dirNorm}\" after successful retry, got [{string.Join(" ", backend.WatchedDirs())}]");
        }
        finally
        {
            w.Close();
        }
    }

    // lspwatcher_test.go:774-805 — TestWatcher_WatchTerminatedDoesNotDropEvents.
    // (Go has no t.Cleanup here either: the watcher is left unclosed, matching the
    // source test.)
    [Fact]
    public void Watcher_WatchTerminatedDoesNotDropEvents()
    {
        string dir = tmp.Dir;
        string dirNorm = TsPathApi.NormalizeSlashes(dir);
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());
        var backend = new FakeBackend();
        var got = new List<FileEvent>();
        var mu = new Lock();
        var w = Watcher.NewWithBackend(fs, backend, changes =>
        {
            lock (mu)
            {
                got.AddRange(changes);
            }
        }, LoggingUtil.NewLogger(Console.Error));

        string pattern = dirNorm + "/**/*";
        Assert.Null(w.WatchFiles("id",
        [
            new FileSystemWatcher { GlobPattern = new PatternOrRelativePattern { Pattern = pattern } },
        ]));

        backend.EmitAll(
        [
            new FsWatch.Event(FsWatch.EventKind.EventUpdate, LspWatcherTestUtil.OsJoin(dirNorm, "b.ts")),
        ], new AggregateException(FsWatch.FsWatchErrors.ErrWatchTerminated, new InvalidOperationException("simulated")));

        LspWatcherTestUtil.WaitFor(() =>
        {
            lock (mu)
            {
                return got.Count > 0;
            }
        }, "events with watch-terminated error");
    }
}

// lspwatcher_test.go:168-243 — fakeBackend + fakeWatch (the watcherBackend test double).
internal sealed class FakeBackend : IWatcherBackend
{
    internal readonly Lock mu = new();
    internal readonly Dictionary<string, FsWatch.WatchCallback> byDir = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> closedCount = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> optCount = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Exception?> failDirs = new(StringComparer.Ordinal);

    // lspwatcher_test.go:185-200 — WatchDirectory. PORT-NOTE: Go returns (nil, err);
    // the C# backend surface throws, which Watcher.Reconcile converts back into the
    // returned error value at the same call site.
    public FsWatch.IWatch WatchDirectory(string dir, FsWatch.WatchCallback fn, params FsWatch.IWatchOption[] opts)
    {
        lock (mu)
        {
            if (failDirs.TryGetValue(dir, out Exception? err) && err != null)
            {
                throw err;
            }
            byDir[dir] = fn;
            optCount[dir] = opts.Length;
            return new FakeWatch(() =>
            {
                lock (mu)
                {
                    byDir.Remove(dir);
                    closedCount[dir] = closedCount.GetValueOrDefault(dir) + 1;
                }
            });
        }
    }

    // lspwatcher_test.go:202-211 — watchedDirs returns the directories currently subscribed, for assertions.
    internal List<string> WatchedDirs()
    {
        lock (mu)
        {
            var dirs = new List<string>(byDir.Count);
            // map-iter: 序无关 —— 仅供断言统计/成员判断。
            foreach (string d in byDir.Keys)
            {
                dirs.Add(d);
            }
            return dirs;
        }
    }

    // lspwatcher_test.go:213-218 — isWatching.
    internal bool IsWatching(string dir)
    {
        lock (mu)
        {
            return byDir.ContainsKey(dir);
        }
    }

    // lspwatcher_test.go:220-227 — emit.
    internal void Emit(string dir, IReadOnlyList<FsWatch.Event> events, Exception? err)
    {
        FsWatch.WatchCallback? cb;
        lock (mu)
        {
            byDir.TryGetValue(dir, out cb);
        }
        cb?.Invoke(events, err);
    }

    // lspwatcher_test.go:229-239 — emitAll.
    internal void EmitAll(IReadOnlyList<FsWatch.Event> events, Exception? err)
    {
        List<FsWatch.WatchCallback> cbs;
        lock (mu)
        {
            cbs = new List<FsWatch.WatchCallback>(byDir.Count);
            // map-iter: 序无关 —— Go map 迭代序随机，向各订阅分发与顺序无关。
            foreach (FsWatch.WatchCallback cb in byDir.Values)
            {
                cbs.Add(cb);
            }
        }
        foreach (FsWatch.WatchCallback cb in cbs)
        {
            cb(events, err);
        }
    }
}

// lspwatcher_test.go:241-243 — fakeWatch. Go: fakeWatch{closeFn func() error} with
// Close() error; the C# IWatch surface is Close()/Dispose() void.
internal sealed class FakeWatch(Action closeFn) : FsWatch.IWatch
{
    public void Close()
    {
        closeFn();
    }

    public void Dispose()
    {
        Close();
    }
}
