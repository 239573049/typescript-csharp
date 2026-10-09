// Ported from tsgo internal/project/checkerpool_test.go (whole file).
//
// PORT-NOTEs:
//  * One Go test function → one [Fact] (t.Parallel() has no counterpart; xUnit parallelizes
//    test classes by default).
//  * gotest.tools: assert.Assert(t, cond[, msg]) → Assert.True(cond); assert.Assert(t, !cond)
//    → Assert.False(cond); assert.Equal(t, actual, expected) → Assert.Equal(expected, actual)
//    (argument order swapped); assert.NilError(t, err) → Assert.Null(err).
//  * setupCheckerPoolSession builds the session with the file-map FS + bundled default library
//    and LoggingEnabled (SessionUtils.GetSessionInitOptions + NewSession); the checker-pool
//    knobs ride on SessionOptions.CheckerPoolOptions. The Go
//    `if !bundled.Embedded { t.Skip(...) }` guard is omitted: Bundled.Embedded is `const true`
//    in the C# build, so the branch is unreachable (and CS0162 fatal).
//  * core.WithRequestID / core.WithCheckerLifetime → RequestContext.WithRequestId /
//    WithCheckerLifetime; context.WithCancel → CancellationTokenSource, whose token goes on
//    RequestContext.Cancellation — that is what CanBeCanceled (Go ctx.Done() == nil) and
//    registerRequestCleanup read. RequestContext is a value struct, so "the same ctx" is the
//    same RequestContext value carrying the same token.
//  * testing/synctest has no C# counterpart (its fake clock needs the goroutine/timer
//    scheduler test hook, which .NET has no equivalent of). Every body is ported against the
//    real System.Threading.Timer at real speed: synctest.Wait() → WaitUntil(...) polling until
//    the background state settles, the fake `time.Sleep(d)` → an equivalent real wait of d.
//    The two `time.Sleep(60 * time.Second)` cases keep Go's intent (the pool must not move on
//    its own long after every deadline) with the same slack, at real speed.
//  * checkerPool's per-field state that the Go test reads directly (checkers/fileAssociations/
//    requestAssociations/persistentChecker/cleanupTimer/opts/program/mu/querySem) is `internal`
//    in C#, since Go's unexported fields are package-visible (plan/05 §1.1); this file reaches
//    them through the same [InternalsVisibleTo] seam the sibling ProjectTests use for
//    Project.Program and Session.pendingUpdateKind.
//  * Go `pool.cleanupIdleCheckers()` → CheckerPool.CleanupIdleCheckers().

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Core;
using Ts.TestUtil.ProjectTestUtil;
using Xunit;

using DocumentUri = global::Ts.LsProto.DocumentUri;
using LanguageKind = global::Ts.LsProto.LanguageKind;

namespace Ts.Project.Tests;

public sealed class CheckerPoolTests
{
    // checkerpool_test.go:20-51 — setupCheckerPoolSession.
    private static (Session Session, CheckerPool Pool, SessionUtils Utils) SetupCheckerPoolSession(CheckerPoolOptions opts)
    {
        var files = new Dictionary<string, object?>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"noLib\": true } }",
            ["/src/index.ts"] = "export const x: number = 1;",
        };
        (SessionInit init, SessionUtils utils) = SessionUtils.GetSessionInitOptions(files, new SessionOptions
        {
            CurrentDirectory = "/",
            DefaultLibraryPath = global::Ts.Bundled.Bundled.LibPath(),
            PositionEncoding = global::Ts.LsProto.PositionEncodingKind.UTF8,
            WatchEnabled = false,
            LoggingEnabled = true,
            CheckerPoolOptions = opts,
        }, new TypingsInstallerOptions());
        Session session = Session.NewSession(init);
        session.DidOpenFile(RequestContext.Background, new DocumentUri("file:///src/index.ts"), 1, "export const x: number = 1;", LanguageKind.TypeScript.Value!);

        Snapshot snapshot = session.Snapshot()!;
        Project? project = snapshot.ProjectCollection.ConfiguredProject(new global::Ts.TsPath.Path("/src/tsconfig.json"));
        Assert.NotNull(project);
        Assert.NotNull(project!.checkerPool);
        return (session, project.checkerPool!, utils);
    }

    // checkerpool_test.go:53-57 — newTestCheckerPool. (Go builds it "inside the current
    // goroutine context"; a C# pool has no goroutine affinity, so this is just construction.)
    private static CheckerPool NewTestCheckerPool(global::Ts.Compiler.Program program, CheckerPoolOptions opts)
    {
        return CheckerPool.NewCheckerPool(opts, program, static _ => { });
    }

    // --- real-time stand-ins for the synctest-scoped driver code -------------------------------

    // synctest.Wait() waits until every goroutine in the bubble is blocked. The C# equivalent
    // is a poll on the observable state. `budget` defaults to 10s; callers whose deadline is
    // itself ~10s away (staggered cleanup, where the timeout is armed at t=6 and expires at
    // t=16) must pass a budget that exceeds their deadline plus scheduling slack, otherwise the
    // poll expires in a dead heat with the timer callback it is waiting for.
    private static void WaitUntil(Func<bool> condition, string what, TimeSpan? budget = null)
    {
        DateTime deadline = DateTime.UtcNow + (budget ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            Thread.Sleep(10);
        }
        Assert.Fail("timed out waiting for " + what);
    }

    // Go loops start at index 1 (index 0 is the diagnostics checker).
    private static int IndexOfChecker(CheckerPool pool, global::Ts.Checker.Checker c, int from = 1)
    {
        lock (pool.mu)
        {
            for (int i = from; i < pool.checkers.Length; i++)
            {
                if (ReferenceEquals(pool.checkers[i], c))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static bool CheckerIsDisposed(CheckerPool pool, global::Ts.Checker.Checker c)
    {
        return IndexOfChecker(pool, c, from: 0) < 0;
    }

    // checkerpool_test.go:59-70 — TestCheckerPoolDiagnosticsRouting.
    [Fact]
    public void TestCheckerPoolDiagnosticsRouting()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        // Diagnostics requests should get checker at index 0.
        RequestContext ctx = RequestContext.Background.WithRequestId("diag-req-1").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        Assert.Same(pool.checkers[0], c); // "diagnostics should use checker index 0"
        release();
    }

    // checkerpool_test.go:72-85 — TestCheckerPoolQueryRouting.
    [Fact]
    public void TestCheckerPoolQueryRouting()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        // Query requests should get a checker at index > 0.
        RequestContext ctx = RequestContext.Background.WithRequestId("query-req-1").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);

        // Verify it's not the diagnostics checker slot.
        Assert.NotSame(pool.checkers[0], c); // "query should not use checker index 0"
        release();
    }

    // checkerpool_test.go:87-111 — TestCheckerPoolRequestAffinity.
    [Fact]
    public void TestCheckerPoolRequestAffinity()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        using var cts = new CancellationTokenSource();
        RequestContext ctx = new RequestContext { Cancellation = cts.Token }.WithRequestId("req-affinity").WithCheckerLifetime(CheckerLifetime.Temporary);

        // First call acquires.
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx, null);

        // Second call with same request ID while still held returns same checker (noop release).
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx, null);
        release2();
        release1();

        Assert.Same(c2, c1); // "same request ID should return the same checker while held"

        // After release, same request should still get the same checker (cross-release affinity).
        (global::Ts.Checker.Checker c3, Action release3) = pool.GetChecker(ctx, null);
        release3();

        Assert.Same(c3, c1); // "same request ID should return the same checker after release"
    }

    // checkerpool_test.go:113-164 — TestCheckerPoolIdleCleanup.
    [Fact]
    public void TestCheckerPoolIdleCleanup()
    {
        // Get a real program to use for checker creation, then test the pool.
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(5) });

        // Create a checker via a diagnostics request.
        RequestContext ctx = RequestContext.Background.WithRequestId("diag-cleanup").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        // Create a query checker as well.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("query-cleanup").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotNull(c2);
        release2();

        // Both checkers should exist.
        int queryIdx = IndexOfChecker(pool, c2);
        lock (pool.mu)
        {
            Assert.NotNull(pool.checkers[0]); // "diagnostics checker should exist"
        }
        Assert.True(queryIdx > 0, "query checker should exist");

        // Advance past idle timeout.
        Thread.Sleep(TimeSpan.FromSeconds(5));

        // After cleanup, both checkers should be disposed.
        WaitUntil(() => CheckerIsDisposed(pool, c), "idle cleanup of the diagnostics checker");
        WaitUntil(() => CheckerIsDisposed(pool, c2), "idle cleanup of the query checker");
        lock (pool.mu)
        {
            Assert.Null(pool.checkers[0]); // "diagnostics checker should be disposed after idle timeout"
            Assert.Null(pool.checkers[queryIdx]); // "query checker should be disposed after idle timeout"
        }
    }

    // checkerpool_test.go:166-202 — TestCheckerPoolFileAssociationCleanup.
    [Fact]
    public void TestCheckerPoolFileAssociationCleanup()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile("/src/index.ts");
        Assert.NotNull(sourceFile);

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(5) });

        // Create a query checker with file affinity.
        RequestContext ctx = RequestContext.Background.WithRequestId("file-assoc-req").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, sourceFile);
        Assert.NotNull(c);
        release();

        // File association should exist.
        bool hasAssoc()
        {
            lock (pool.mu)
            {
                return pool.fileAssociations.ContainsKey(sourceFile!);
            }
        }
        Assert.True(hasAssoc(), "file should have a checker association");

        // Advance past idle timeout.
        Thread.Sleep(TimeSpan.FromSeconds(5));

        // File association should be cleared.
        WaitUntil(() => !hasAssoc(), "file association cleanup after checker disposal");
    }

    // checkerpool_test.go:204-210 — TestCheckerPoolMinCheckers.
    [Fact]
    public void TestCheckerPoolMinCheckers()
    {
        // Requesting maxCheckers=1 should be clamped to 2.
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 1, IdleTimeout = TimeSpan.FromSeconds(10) });
        Assert.Equal(2, pool.opts.MaxCheckers);
        Assert.Equal(2, pool.checkers.Length);
    }

    // checkerpool_test.go:212-217 — TestCheckerPoolDefaultIdleTimeout.
    [Fact]
    public void TestCheckerPoolDefaultIdleTimeout()
    {
        // Zero idle timeout should default to 30s.
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4 });
        Assert.Equal(TimeSpan.FromSeconds(30), pool.opts.IdleTimeout);
    }

    // checkerpool_test.go:219-256 — TestCheckerPoolQueryContention.
    [Fact]
    public void TestCheckerPoolQueryContention()
    {
        // maxCheckers=2 means 1 diagnostics + 1 query checker slot.
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire the only query checker slot.
        RequestContext ctx1 = RequestContext.Background.WithRequestId("query-hold").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx1, null);
        Assert.NotNull(c1);

        // A second query request from a different request ID should block.
        int c2Got = 0;
        var waiter = new Thread(() =>
        {
            RequestContext ctx2 = RequestContext.Background.WithRequestId("query-wait").WithCheckerLifetime(CheckerLifetime.Temporary);
            (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
            _ = c2; // verified via c2Got flag
            Interlocked.Exchange(ref c2Got, c2 is not null ? 1 : 0);
            release2();
        });
        waiter.IsBackground = true;
        waiter.Start();

        // Wait for the goroutine to reach the semaphore send.
        Assert.False(SpinUntilBlocked(ref c2Got), "second query should be blocked while first holds the checker");
        // Release the first checker — second should unblock.
        release1();
        WaitUntil(() => Volatile.Read(ref c2Got) == 1, "second query acquiring the checker after release");
    }

    // checkerpool_test.go:258-301 — TestCheckerPoolDiagnosticsContention.
    [Fact]
    public void TestCheckerPoolDiagnosticsContention()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire the diagnostics checker.
        RequestContext ctx1 = RequestContext.Background.WithRequestId("diag-hold").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx1, null);
        Assert.NotNull(c1);

        // A second diagnostics request should block since there's only one diag checker.
        int c2Got = 0;
        var waiter = new Thread(() =>
        {
            RequestContext ctx2 = RequestContext.Background.WithRequestId("diag-wait").WithCheckerLifetime(CheckerLifetime.Diagnostics);
            (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
            _ = c2; // verified via c2Got flag
            Interlocked.Exchange(ref c2Got, c2 is not null ? 1 : 0);
            release2();
        });
        waiter.IsBackground = true;
        waiter.Start();

        Assert.False(SpinUntilBlocked(ref c2Got), "second diagnostics request should be blocked");

        // A query request should NOT be blocked (separate slot).
        RequestContext ctx3 = RequestContext.Background.WithRequestId("query-concurrent").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c3, Action release3) = pool.GetChecker(ctx3, null);
        Assert.NotNull(c3);
        Assert.NotSame(c1, c3); // "query checker should be different from diagnostics checker"
        release3();

        // Release the diagnostics checker — second diag request should unblock.
        release1();
        WaitUntil(() => Volatile.Read(ref c2Got) == 1, "second diagnostics request acquiring the checker after release");
    }

    // checkerpool_test.go:303-337 — TestCheckerPoolCanceledCheckerDisposal.
    [Fact]
    public void TestCheckerPoolCanceledCheckerDisposal()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile("/src/index.ts");
        Assert.NotNull(sourceFile);

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire a query checker and cancel it.
        RequestContext ctx = RequestContext.Background.WithRequestId("cancel-test").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);

        var canceled = new CancellationTokenSource();
        canceled.Cancel();
        c.GetDiagnostics(sourceFile, canceled.Token);
        Assert.True(c.WasCanceled());

        // Release should dispose the canceled checker.
        release();
        WaitUntil(() => CheckerIsDisposed(pool, c), "disposal of the canceled checker");

        // Next request should get a fresh checker.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("after-cancel").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotSame(c, c2); // "should get a new checker, not the canceled one"
        release2();
    }

    // checkerpool_test.go:339-373 — TestCheckerPoolRequestAssociationCleanupOnDisposal.
    [Fact]
    public void TestCheckerPoolRequestAssociationCleanupOnDisposal()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(5) });

        // Create a query checker with a request association.
        using var reqCts = new CancellationTokenSource();
        RequestContext ctx = new RequestContext { Cancellation = reqCts.Token }.WithRequestId("assoc-cleanup-req").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);

        // Cancel the checker to trigger disposal on release.
        var canceled = new CancellationTokenSource();
        canceled.Cancel();
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile("/src/index.ts");
        c.GetDiagnostics(sourceFile, canceled.Token);
        Assert.True(c.WasCanceled());

        release();

        // Request association should be cleared after checker disposal.
        WaitUntil(() =>
        {
            lock (pool.mu)
            {
                return !pool.requestAssociations.ContainsKey("assoc-cleanup-req");
            }
        }, "request association cleanup after checker disposal");
    }

    // checkerpool_test.go:375-410 — TestCheckerPoolRequestAssociationCleanupOnContextDone.
    [Fact]
    public void TestCheckerPoolRequestAssociationCleanupOnContextDone()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Create a cancellable context to simulate request lifecycle.
        using var reqCts = new CancellationTokenSource();
        RequestContext ctx = new RequestContext { Cancellation = reqCts.Token }.WithRequestId("ctx-cleanup-req").WithCheckerLifetime(CheckerLifetime.Temporary);

        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        bool hasAssoc()
        {
            lock (pool.mu)
            {
                return pool.requestAssociations.ContainsKey("ctx-cleanup-req");
            }
        }

        // Association should still exist after release.
        Assert.True(hasAssoc(), "request association should persist after release");

        // Cancel the request context — association should be cleaned up.
        reqCts.Cancel();
        WaitUntil(() => !hasAssoc(), "request association cleanup after context cancellation");
    }

    // checkerpool_test.go:412-446 — TestCheckerPoolDiagnosticsRecreatedAfterIdleDisposal.
    [Fact]
    public void TestCheckerPoolDiagnosticsRecreatedAfterIdleDisposal()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(5) });

        // Create and release diagnostics checker.
        RequestContext ctx = RequestContext.Background.WithRequestId("diag-recreate-1").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx, null);
        Assert.NotNull(c1);
        release1();

        // Advance past idle timeout — diagnostics checker should be disposed.
        Thread.Sleep(TimeSpan.FromSeconds(5));
        WaitUntil(() =>
        {
            lock (pool.mu)
            {
                return pool.checkers[0] is null;
            }
        }, "idle disposal of the diagnostics checker");

        // Request diagnostics checker again — should get a fresh one.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("diag-recreate-2").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotNull(c2); // "diagnostics checker should be re-created"
        Assert.NotSame(c1, c2); // "should be a new checker instance"
        release2();
    }

    // checkerpool_test.go:448-500 — TestCheckerPoolCrossReleaseAffinityWithContention.
    [Fact]
    public void TestCheckerPoolCrossReleaseAffinityWithContention()
    {
        // maxCheckers=2: 1 diagnostics + 1 query slot.
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(30) });

        using var reqCts = new CancellationTokenSource();

        // Request A acquires the only query slot.
        RequestContext ctxA = new RequestContext { Cancellation = reqCts.Token }.WithRequestId("req-A").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker cA, Action releaseA) = pool.GetChecker(ctxA, null);
        Assert.NotNull(cA);
        releaseA();

        // Request B takes the query slot while A is released.
        RequestContext ctxB = RequestContext.Background.WithRequestId("req-B").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker cB, Action releaseB) = pool.GetChecker(ctxB, null);
        Assert.NotNull(cB);

        // Request A reacquires — should block because B holds the slot.
        int reacquired = 0;
        global::Ts.Checker.Checker? cA2 = null;
        var waiter = new Thread(() =>
        {
            (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctxA, null);
            cA2 = c;
            Interlocked.Exchange(ref reacquired, 1);
            release();
        });
        waiter.IsBackground = true;
        waiter.Start();

        Assert.False(SpinUntilBlocked(ref reacquired), "request A should block while B holds the slot");

        // Release B — A should unblock and get the same checker.
        releaseB();
        WaitUntil(() => Volatile.Read(ref reacquired) == 1, "request A reacquiring after B releases");
        Assert.Same(cA, cA2); // "request A should get the same checker on reacquire"
    }

    // checkerpool_test.go:502-541 — TestCheckerPoolLifetimeMismatchIgnoresAssociation.
    [Fact]
    public void TestCheckerPoolLifetimeMismatchIgnoresAssociation()
    {
        // Verify that if a request first uses a diagnostics checker, then switches
        // to a temporary lifetime (or vice versa), the stale association is ignored
        // rather than returning a checker from the wrong category.
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        using var reqCts = new CancellationTokenSource();

        // Acquire a diagnostics checker with request ID "mixed".
        RequestContext ctxDiag = new RequestContext { Cancellation = reqCts.Token }.WithRequestId("mixed").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker cDiag, Action releaseDiag) = pool.GetChecker(ctxDiag, null);
        Assert.NotNull(cDiag);
        Assert.Same(pool.checkers[0], cDiag); // "diagnostics checker should be at index 0"
        releaseDiag();

        // Now use the same request ID but with query purpose.
        // The old association points to index 0 (diagnostics), which should
        // be rejected — the returned checker must be a query checker (index > 0).
        RequestContext ctxQuery = new RequestContext { Cancellation = reqCts.Token }.WithRequestId("mixed").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker cQuery, Action releaseQuery) = pool.GetChecker(ctxQuery, null);
        Assert.NotNull(cQuery);
        Assert.NotSame(cDiag, cQuery); // "query should not reuse the diagnostics checker"

        lock (pool.mu)
        {
            Assert.NotSame(cQuery, pool.checkers[0]); // "query checker should not be at diagnostics index 0"
        }
        releaseQuery();
    }

    // checkerpool_test.go:543-559 — TestCheckerPoolNoRequestID.
    [Fact]
    public void TestCheckerPoolNoRequestID()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        // Calls without a request ID should still work (e.g., callhierarchy uses context.Background()).
        RequestContext ctx = RequestContext.Background;

        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx, null);
        Assert.NotNull(c1);
        release1();

        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx, null);
        Assert.NotNull(c2);
        release2();

        // Without request ID, no affinity guarantee — just verify it doesn't crash.
    }

    // checkerpool_test.go:561-587 — TestCheckerPoolDiagnosticsCrossReleaseAffinity.
    [Fact]
    public void TestCheckerPoolDiagnosticsCrossReleaseAffinity()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        using var reqCts = new CancellationTokenSource();
        RequestContext ctx = new RequestContext { Cancellation = reqCts.Token }.WithRequestId("diag-affinity").WithCheckerLifetime(CheckerLifetime.Diagnostics);

        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx, null);
        Assert.NotNull(c1);
        Assert.Same(pool.checkers[0], c1); // "should be the diagnostics checker"
        release1();

        // Same request reacquiring diagnostics should get the same checker.
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx, null);
        Assert.Same(c1, c2); // "same diagnostics request should get the same checker after release"
        release2();
    }

    // checkerpool_test.go:589-644 — TestCheckerPoolDiscardKeepsIdleCheckers.
    [Fact]
    public void TestCheckerPoolDiscardKeepsIdleCheckers()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Create both a diagnostics and a query checker.
        RequestContext ctx1 = RequestContext.Background.WithRequestId("obs-diag").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx1, null);
        Assert.NotNull(c1);
        release1();

        RequestContext ctx2 = RequestContext.Background.WithRequestId("obs-query").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotNull(c2);
        release2();

        // Both checkers should exist before Discard.
        lock (pool.mu)
        {
            Assert.NotNull(pool.checkers[0]); // "diagnostics checker should exist"
        }

        // Discard should keep idle checkers alive (they may be referenced by
        // API type handles) and just stop the cleanup timer.
        pool.Discard();

        lock (pool.mu)
        {
            Assert.Same(c1, pool.checkers[0]); // "diagnostics checker should survive Discard"
            Assert.True(IndexOfCheckerLocked(pool, c2) > 0, "query checker should survive Discard");
            Assert.Null(pool.cleanupTimer); // "cleanup timer should be stopped after Discard"
        }

        // Even after a long wait, checkers should not be disposed (no timer running).
        Thread.Sleep(TimeSpan.FromSeconds(60));

        lock (pool.mu)
        {
            Assert.Same(c1, pool.checkers[0]); // "diagnostics checker should persist indefinitely on discarded pool"
        }
    }

    // checkerpool_test.go:646-697 — TestCheckerPoolDiscardHeldCheckerSurvivesRelease.
    [Fact]
    public void TestCheckerPoolDiscardHeldCheckerSurvivesRelease()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire a checker and hold it.
        RequestContext ctx = RequestContext.Background.WithRequestId("held-obs").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);

        // Find which slot it's in.
        int heldIndex = IndexOfChecker(pool, c);
        Assert.True(heldIndex > 0, "should find the held checker");

        // Discard while checker is held — should NOT dispose it.
        pool.Discard();

        lock (pool.mu)
        {
            Assert.Same(c, pool.checkers[heldIndex]); // "held checker should survive Discard"
        }

        // Release — checker should remain alive on a discarded pool.
        release();

        lock (pool.mu)
        {
            Assert.Same(c, pool.checkers[heldIndex]); // "checker should persist after release on discarded pool"
        }

        // Even after a long wait, checker persists (no cleanup timer running).
        Thread.Sleep(TimeSpan.FromSeconds(60));

        lock (pool.mu)
        {
            Assert.Same(c, pool.checkers[heldIndex]); // "checker should persist indefinitely on discarded pool"
        }
    }

    // checkerpool_test.go:699-743 — TestCheckerPoolDiscardStillFunctional.
    [Fact]
    public void TestCheckerPoolDiscardStillFunctional()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });
        pool.Discard();

        // Pool should still work — GetChecker should create a fresh checker.
        RequestContext ctx = RequestContext.Background.WithRequestId("post-obs").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c); // "should still create checkers after Discard"

        // Find the slot.
        int idx = IndexOfChecker(pool, c);
        Assert.True(idx > 0, "checker should be in a query slot");

        // Release — checker should persist on discarded pool (no cleanup timer).
        release();

        lock (pool.mu)
        {
            Assert.Same(c, pool.checkers[idx]); // "checker should persist after release on discarded pool"
        }

        // Re-acquire — should get the same checker back.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("post-obs-2").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.Same(c, c2); // "should get the same checker on discarded pool"
        release2();
    }

    // checkerpool_test.go:745-770 — TestCheckerPoolDiagnosticsCheckerStableIdentity.
    [Fact]
    public void TestCheckerPoolDiagnosticsCheckerStableIdentity()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire the diagnostics checker.
        RequestContext ctx = RequestContext.Background.WithRequestId("diag-stable-1").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx, null);
        Assert.NotNull(c1); // "diagnostics checker should be created"
        release1();

        // Re-acquire before idle timeout — should be the same instance.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("diag-stable-2").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.Same(c1, c2); // "diagnostics checker should be the same instance before idle timeout"
        release2();
    }

    // checkerpool_test.go:772-804 — TestCheckerPoolDiagnosticsCheckerSurvivesDiscard.
    [Fact]
    public void TestCheckerPoolDiagnosticsCheckerSurvivesDiscard()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Create the diagnostics checker.
        RequestContext ctx = RequestContext.Background.WithRequestId("diag-discard").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        pool.Discard();

        // Diagnostics checker should survive Discard.
        lock (pool.mu)
        {
            Assert.Same(c, pool.checkers[0]); // "diagnostics checker should survive Discard"
        }

        // Should still be acquirable and be the same instance.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("diag-discard-2").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.Same(c, c2); // "diagnostics checker identity should be stable after Discard"
        release2();
    }

    // checkerpool_test.go:806-831 — TestCheckerPoolDiagnosticsCheckerIndependentFromQuery.
    [Fact]
    public void TestCheckerPoolDiagnosticsCheckerIndependentFromQuery()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire diagnostics and query checkers.
        RequestContext diagCtx = RequestContext.Background.WithRequestId("diag-indep").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        RequestContext queryCtx = RequestContext.Background.WithRequestId("query-indep").WithCheckerLifetime(CheckerLifetime.Temporary);

        (global::Ts.Checker.Checker diagC, Action diagRelease) = pool.GetChecker(diagCtx, null);
        (global::Ts.Checker.Checker queryC, Action queryRelease) = pool.GetChecker(queryCtx, null);

        // They should be different checker instances.
        Assert.NotSame(queryC, diagC); // "diagnostics and query checkers should be different"

        diagRelease();
        queryRelease();
    }

    // checkerpool_test.go:833-860 — TestCheckerPoolAPICheckerStableIdentity.
    [Fact]
    public void TestCheckerPoolAPICheckerStableIdentity()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        RequestContext ctx = RequestContext.Background.WithCheckerLifetime(CheckerLifetime.Api);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx, null);
        Assert.NotNull(c1);
        release1();

        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx, null);
        Assert.Same(c1, c2); // "API checker should be the same instance"
        release2();

        // Should survive idle timeout.
        Thread.Sleep(TimeSpan.FromSeconds(60));

        (global::Ts.Checker.Checker c3, Action release3) = pool.GetChecker(ctx, null);
        Assert.Same(c1, c3); // "API checker should survive idle timeout"
        release3();
    }

    // checkerpool_test.go:862-887 — TestCheckerPoolAPICheckerSurvivesDiscard.
    [Fact]
    public void TestCheckerPoolAPICheckerSurvivesDiscard()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        RequestContext ctx = RequestContext.Background.WithCheckerLifetime(CheckerLifetime.Api);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        pool.Discard();

        lock (pool.mu)
        {
            Assert.Same(c, pool.persistentChecker); // "API checker should survive Discard"
        }

        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx, null);
        Assert.Same(c, c2); // "API checker identity should be stable after Discard"
        release2();
    }

    // checkerpool_test.go:889-917 — TestCheckerPoolAllThreeIndependent.
    [Fact]
    public void TestCheckerPoolAllThreeIndependent()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        RequestContext dedCtx = RequestContext.Background.WithRequestId("ded-req").WithCheckerLifetime(CheckerLifetime.Diagnostics);
        RequestContext tmpCtx = RequestContext.Background.WithRequestId("tmp-req").WithCheckerLifetime(CheckerLifetime.Temporary);
        RequestContext perCtx = RequestContext.Background.WithCheckerLifetime(CheckerLifetime.Api);

        (global::Ts.Checker.Checker dedC, Action dedRelease) = pool.GetChecker(dedCtx, null);
        (global::Ts.Checker.Checker tmpC, Action tmpRelease) = pool.GetChecker(tmpCtx, null);
        (global::Ts.Checker.Checker perC, Action perRelease) = pool.GetChecker(perCtx, null);

        Assert.NotSame(tmpC, dedC); // "diagnostics and temporary should be different"
        Assert.NotSame(perC, dedC); // "diagnostics and API should be different"
        Assert.NotSame(perC, tmpC); // "temporary and API should be different"

        dedRelease();
        tmpRelease();
        perRelease();
    }

    // checkerpool_test.go:919-947 — TestCheckerPoolFileAffinity.
    [Fact]
    public void TestCheckerPoolFileAffinity()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile("/src/index.ts");
        Assert.NotNull(sourceFile);

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // First query with a file should create a checker and associate it.
        RequestContext ctx1 = RequestContext.Background.WithRequestId("file-aff-1").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx1, sourceFile);
        Assert.NotNull(c1);
        release1();

        // Second query with the same file (different request) should get the same checker via file affinity.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("file-aff-2").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, sourceFile);
        Assert.NotNull(c2);
        Assert.Same(c1, c2); // "same file should return the same checker via file affinity"
        release2();
    }

    // checkerpool_test.go:949-1008 — TestCheckerPoolMultipleConcurrentQueryCheckers.
    [Fact]
    public void TestCheckerPoolMultipleConcurrentQueryCheckers()
    {
        // maxCheckers=4: 1 diagnostics + 3 query slots.
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Acquire 3 query checkers concurrently (all slots).
        RequestContext ctx1 = RequestContext.Background.WithRequestId("multi-q-1").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c1, Action release1) = pool.GetChecker(ctx1, null);
        Assert.NotNull(c1);

        RequestContext ctx2 = RequestContext.Background.WithRequestId("multi-q-2").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotNull(c2);

        RequestContext ctx3 = RequestContext.Background.WithRequestId("multi-q-3").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c3, Action release3) = pool.GetChecker(ctx3, null);
        Assert.NotNull(c3);

        // All three should be distinct checkers.
        Assert.NotSame(c2, c1); // "concurrent query checkers should be distinct (1 vs 2)"
        Assert.NotSame(c3, c1); // "concurrent query checkers should be distinct (1 vs 3)"
        Assert.NotSame(c3, c2); // "concurrent query checkers should be distinct (2 vs 3)"

        // None should be the diagnostics checker at index 0.
        lock (pool.mu)
        {
            Assert.True(pool.checkers[0] != c1 && pool.checkers[0] != c2 && pool.checkers[0] != c3,
                "query checkers should not occupy the diagnostics slot");
        }

        // A 4th query request should block since all 3 slots are full.
        int c4Got = 0;
        var waiter = new Thread(() =>
        {
            RequestContext ctx4 = RequestContext.Background.WithRequestId("multi-q-4").WithCheckerLifetime(CheckerLifetime.Temporary);
            (global::Ts.Checker.Checker c4, Action release4) = pool.GetChecker(ctx4, null);
            _ = c4; // verified via c4Got flag
            Interlocked.Exchange(ref c4Got, c4 is not null ? 1 : 0);
            release4();
        });
        waiter.IsBackground = true;
        waiter.Start();

        Assert.False(SpinUntilBlocked(ref c4Got), "4th query should block when all 3 query slots are held");

        release1();
        WaitUntil(() => Volatile.Read(ref c4Got) == 1, "4th query acquiring a slot after release");

        release2();
        release3();
    }

    // checkerpool_test.go:1010-1030 — TestCheckerPoolDoubleReleaseSafe.
    [Fact]
    public void TestCheckerPoolDoubleReleaseSafe()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        RequestContext ctx = RequestContext.Background.WithRequestId("double-release").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);

        // First release should work normally.
        release();
        // Second release should be a no-op (sync.OnceFunc).
        release();

        // Pool should still be functional after double release.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("after-double").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotNull(c2);
        release2();
    }

    // checkerpool_test.go:1032-1039 — TestCheckerPoolDefaultMaxCheckers.
    [Fact]
    public void TestCheckerPoolDefaultMaxCheckers()
    {
        // Zero MaxCheckers should default to 4.
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 0, IdleTimeout = TimeSpan.FromSeconds(10) });
        Assert.Equal(4, pool.opts.MaxCheckers);
        Assert.Equal(4, pool.checkers.Length);
        Assert.Equal(3, pool.querySem.CurrentCount); // "querySem capacity should be MaxCheckers-1"
    }

    // checkerpool_test.go:1041-1104 — TestCheckerPoolStaggeredIdleCleanup.
    [Fact]
    public void TestCheckerPoolStaggeredIdleCleanup()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        // Acquire checker A and hold it.
        RequestContext ctxA = RequestContext.Background.WithRequestId("stagger-A").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker cA, Action releaseA) = pool.GetChecker(ctxA, null);
        Assert.NotNull(cA);

        // While A is held, acquire a second checker B.
        RequestContext ctxB = RequestContext.Background.WithRequestId("stagger-B").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker cB, Action releaseB) = pool.GetChecker(ctxB, null);
        Assert.NotNull(cB);
        Assert.NotSame(cA, cB); // "B should be a different checker since A is held"

        // Find their indices.
        int idxA = IndexOfChecker(pool, cA);
        int idxB = IndexOfChecker(pool, cB);
        Assert.True(idxA > 0);
        Assert.True(idxB > 0);

        // Release A first. Timer is set for t=10.
        releaseA();

        // Release B 6 seconds later. Timer is reset for t=16.
        Thread.Sleep(TimeSpan.FromSeconds(6));
        releaseB();

        // At t < 16, both should still exist (timer hasn't fired yet).
        lock (pool.mu)
        {
            Assert.NotNull(pool.checkers[idxA]); // "checker A should still exist before timer fires"
            Assert.NotNull(pool.checkers[idxB]); // "checker B should still exist before timer fires"
        }

        // Advance past t=16 (when the timer fires). Both should be disposed
        // because A has been idle 16s and B has been idle 10s.
        WaitUntil(() =>
        {
            lock (pool.mu)
            {
                return pool.checkers[idxA] is null && pool.checkers[idxB] is null;
            }
        }, "staggered idle disposal of checkers A and B", TimeSpan.FromSeconds(20));
    }

    // checkerpool_test.go:1106-1147 — TestCheckerPoolDiscardIdempotent.
    [Fact]
    public void TestCheckerPoolDiscardIdempotent()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 2, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // Create a checker so there's something to discard.
        RequestContext ctx = RequestContext.Background.WithRequestId("idem-q").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        // First discard should keep idle checkers alive.
        pool.Discard();
        bool hasChecker;
        lock (pool.mu)
        {
            hasChecker = false;
            foreach (global::Ts.Checker.Checker? cc in pool.checkers)
            {
                if (cc is not null)
                {
                    hasChecker = true;
                    break;
                }
            }
        }
        Assert.True(hasChecker, "first Discard should keep idle checkers alive");

        // Second discard should be a no-op (no panic, no state corruption).
        pool.Discard();

        // Pool should still be functional after double Discard.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("post-idem").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, null);
        Assert.NotNull(c2); // "pool should still work after double Discard"
        release2();
    }

    // checkerpool_test.go:1149-1156 — TestCheckerPoolGetGlobalDiagnosticsEmpty.
    [Fact]
    public void TestCheckerPoolGetGlobalDiagnosticsEmpty()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        // Before any checker is used, global diagnostics should be empty.
        global::Ts.Ast.Diagnostic[] diags = pool.GetGlobalDiagnostics();
        Assert.Empty(diags); // "global diagnostics should be empty initially"
    }

    // checkerpool_test.go:1158-1193 — TestCheckerPoolTakeNewGlobalDiagnostics.
    [Fact]
    public void TestCheckerPoolTakeNewGlobalDiagnostics()
    {
        (_, CheckerPool pool, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });

        // Initially, no new globals.
        Assert.False(pool.TakeNewGlobalDiagnostics(), "should report no new globals initially");

        // Use a checker and trigger diagnostics, then release to run the merge.
        RequestContext ctx = RequestContext.Background.WithRequestId("global-diag-req").WithCheckerLifetime(CheckerLifetime.Temporary);
        global::Ts.Ast.SourceFile? sourceFile = pool.program.GetSourceFile("/src/index.ts");
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, sourceFile);
        Assert.NotNull(c);
        c.GetDiagnostics(sourceFile, ctx.Cancellation);
        release();

        // Whether globals were produced depends on the program, but the flag
        // should reflect the merge result.
        bool firstTake = pool.TakeNewGlobalDiagnostics();

        // After taking, a second call should always return false (flag is reset).
        Assert.False(pool.TakeNewGlobalDiagnostics(), "TakeNewGlobalDiagnostics should reset after first call");

        // Releasing the same checker again with the same state should not set the flag.
        RequestContext ctx2 = RequestContext.Background.WithRequestId("global-diag-req-2").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx2, sourceFile);
        Assert.NotNull(c2);
        c2.GetDiagnostics(sourceFile, ctx2.Cancellation);
        release2();

        // If first call produced globals, the count is now stable, so no new change.
        // If first call produced no globals, still no change.
        _ = firstTake;
        Assert.False(pool.TakeNewGlobalDiagnostics(), "should not report new globals when checker state is unchanged");
    }

    // checkerpool_test.go:1195-1230 — TestCheckerPoolAPICheckerDisposedOnCancel.
    [Fact]
    public void TestCheckerPoolAPICheckerDisposedOnCancel()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();
        global::Ts.Ast.SourceFile? sourceFile = program.GetSourceFile("/src/index.ts");
        Assert.NotNull(sourceFile);

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        RequestContext ctx = RequestContext.Background.WithCheckerLifetime(CheckerLifetime.Api);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);

        // Cancel the API checker.
        var canceled = new CancellationTokenSource();
        canceled.Cancel();
        c.GetDiagnostics(sourceFile, canceled.Token);
        Assert.True(c.WasCanceled());

        // Releasing a canceled API checker must drop it so it isn't reused.
        release();

        WaitUntil(() =>
        {
            lock (pool.mu)
            {
                return pool.persistentChecker is null;
            }
        }, "dropping the canceled API checker on release");

        // Next API acquisition gets a fresh, usable checker rather than panicking.
        (global::Ts.Checker.Checker c2, Action release2) = pool.GetChecker(ctx, null);
        Assert.NotSame(c, c2); // "should get a fresh API checker after cancellation"
        release2();
    }

    // checkerpool_test.go:1232-1258 — TestCheckerPoolNonCancelableContextNoAffinity.
    [Fact]
    public void TestCheckerPoolNonCancelableContextNoAffinity()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        // A context that carries a request ID but can never be canceled
        // (ctx.Done() == nil) must not register a request association, since
        // there would be no way to clean it up.
        RequestContext ctx = RequestContext.Background.WithRequestId("uncancelable-req").WithCheckerLifetime(CheckerLifetime.Temporary);
        Assert.False(ctx.CanBeCanceled, "test precondition: context must be non-cancelable");

        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        lock (pool.mu)
        {
            Assert.Empty(pool.requestAssociations); // "non-cancelable context must not grow requestAssociations"
        }
    }

    // checkerpool_test.go:1260-1295 — TestCheckerPoolCleanupAfterDiscardIsNoop.
    [Fact]
    public void TestCheckerPoolCleanupAfterDiscardIsNoop()
    {
        (Session session, _, _) = SetupCheckerPoolSession(new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(10) });
        (global::Ts.Ls.LanguageService? ls, Exception? err) = session.GetLanguageService(RequestContext.Background, new DocumentUri("file:///src/index.ts"));
        Assert.Null(err);
        global::Ts.Compiler.Program program = ls!.GetProgram();

        CheckerPool pool = NewTestCheckerPool(program, new CheckerPoolOptions { MaxCheckers = 4, IdleTimeout = TimeSpan.FromSeconds(30) });

        RequestContext ctx = RequestContext.Background.WithRequestId("discard-cleanup").WithCheckerLifetime(CheckerLifetime.Temporary);
        (global::Ts.Checker.Checker c, Action release) = pool.GetChecker(ctx, null);
        Assert.NotNull(c);
        release();

        pool.Discard();

        // Simulate the timer callback firing after Discard() (Stop() does not
        // guarantee the callback won't run). It must be a no-op and must not
        // re-arm the cleanup timer, which would keep the discarded pool alive.
        pool.CleanupIdleCheckers();

        lock (pool.mu)
        {
            Assert.Null(pool.cleanupTimer); // "cleanup must not reschedule a timer on a discarded pool"
            bool hasChecker = false;
            foreach (global::Ts.Checker.Checker? cc in pool.checkers)
            {
                if (cc is not null)
                {
                    hasChecker = true;
                }
            }
            Assert.True(hasChecker, "idle checkers must survive cleanup on a discarded pool");
        }
    }

    // IndexOfChecker needs the lock-free variant when the caller already holds mu (Go's loop
    // in TestCheckerPoolDiscardKeepsIdleCheckers runs with pool.mu held); `lock` is reentrant
    // in C#, but keeping the two spellings mirrors the Go source.
    private static int IndexOfCheckerLocked(CheckerPool pool, global::Ts.Checker.Checker c, int from = 1)
    {
        for (int i = from; i < pool.checkers.Length; i++)
        {
            if (ReferenceEquals(pool.checkers[i], c))
            {
                return i;
            }
        }
        return -1;
    }

    // synctest.Wait() then "assert the flag is still unset": there is no way to ask .NET whether
    // a thread is parked on a semaphore, so the helper request is given a bounded settling window
    // (its earlier asserts and the semaphore hand-off are the only work it does) and the flag is
    // then read — false still proves Go's assertion.
    private static bool SpinUntilBlocked(ref int flag)
    {
        Thread.Sleep(TimeSpan.FromSeconds(1));
        return Volatile.Read(ref flag) != 0;
    }
}
