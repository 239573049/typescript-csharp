// Ported from tsgo internal/project/checkerpool.go (whole file).
// PORT-NOTEs:
//  * Lives in namespace Ts.Project; the name does not collide with the compiler
//    pipeline's Ts.Checker.CheckerPool (plan/03 §9.1 keeps them separate).
//  * Go's context.Value plumbing (CheckerLifetime/requestID) maps to the explicit
//    RequestContext fields (plan/01 §4.4). Ts.Checker.ICheckerPool's interface only
//    carries a CancellationToken, so the typed RequestContext overload is the real
//    entry point and the interface method adapts with a default (Temporary) lifetime.
//  * checker.WasCanceled() (checker.go:615, exports.go:164) is the real flag: the checker
//    records cancellation for the request whose token it checked last.
//  * context.AfterFunc → CancellationToken.Register; sync.OnceFunc → a small
//    execute-once guard; time.Timer → System.Threading.Timer.

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project;

// checkerpool.go:17-20 — checkerHeldAnonymous is a sentinel stored in heldBy when a
// checker is held by a caller that has no request ID (e.g., context.Background()). This
// distinguishes "held without ID" from "not held" (empty string).
file static class CheckerPoolConstants
{
    internal const string CheckerHeldAnonymous = "<anonymous>";
}

// checkerpool.go:22-30 — CheckerPoolOptions. (Go value struct → record: newCheckerPool
// normalizes zero fields via `opts with { ... }` copies.)
public sealed record CheckerPoolOptions
{
    // MaxCheckers controls the total number of checker slots per project
    // (1 dedicated diagnostics checker + N-1 query checkers). Minimum 2.
    // Zero uses the default (4).
    public int MaxCheckers { get; init; }

    // IdleTimeout controls how long an idle checker is kept
    // before being disposed. Zero uses the default (30s).
    public TimeSpan IdleTimeout { get; init; }
}

// checkerpool.go:32-80 — checkerPool manages three categories of type checkers for a project:
//
//   - Diagnostics (index 0): A single checker for LSP diagnostics, providing
//     consistent walk order. Idle-cleaned.
//   - Temporary (indices 1+): Ephemeral query checkers for LSP operations.
//     Idle-cleaned after a configurable timeout.
//   - API: A single checker for API operations, providing stable
//     instance identity for reference equality on type/symbol handles.
//     Never idle-cleaned.
internal sealed class CheckerPool : global::Ts.Checker.ICheckerPool
{
    // PORT-NOTE (visibility): Go's unexported fields are package-visible, so the Go
    // checkerpool_test.go reads checkers/opts/program/fileAssociations/requestAssociations/
    // persistentChecker/cleanupTimer directly. C# has no package-private visibility, so those
    // become `internal` (visible across files of the assembly and to Ts.LanguageService.Tests
    // via InternalsVisibleTo) per plan/05 §1.1; the pool's own locks still guard them.
    internal CheckerPoolOptions opts;
    internal readonly global::Ts.Compiler.Program program;

    internal readonly object mu = new();

    // discarded is set when the pool's program has been replaced. The pool
    // remains fully functional but stops its idle-cleanup timer so that
    // query checkers are not disposed until the pool is GC'd.
    private bool discarded;

    // checkers[0] is the diagnostics checker.
    // checkers[1:] are ephemeral query checkers.
    // All are idle-cleaned.
    internal readonly global::Ts.Checker.Checker?[] checkers;
    private readonly string?[] heldBy; // heldBy[i] is the requestID holding checker i, checkerHeldAnonymous, or "" if not held
    internal readonly Dictionary<global::Ts.Ast.SourceFile, int> fileAssociations; // file → query checker index (1+)
    internal readonly Dictionary<string, int> requestAssociations; // requestID → checker index

    // lastReleased tracks when each checker was last released.
    private readonly DateTime[] lastReleased;

    // cleanupTimer is reset each time a checker is released.
    // When it fires, idle checkers are disposed.
    internal Timer? cleanupTimer;

    // persistentChecker is the API checker. It is never idle-cleaned,
    // providing stable instance identity for API clients.
    internal global::Ts.Checker.Checker? persistentChecker;
    // Go: persistentHeld — package-visible in Go, hence internal here.
    internal bool persistentHeld;

    private readonly SemaphoreSlim diagSem;
    internal readonly SemaphoreSlim querySem;
    private readonly SemaphoreSlim persistentSem;

    private readonly Action<string> log;
    private readonly List<global::Ts.Ast.Diagnostic> globalDiagAccumulated = [];
    private bool globalDiagChanged;
    private readonly int[] globalDiagCheckerCount; // per-checker count of globals last seen

    // checkerpool.go:84-113 — newCheckerPool.
    internal static CheckerPool NewCheckerPool(CheckerPoolOptions opts, global::Ts.Compiler.Program program, Action<string>? log)
    {
        if (opts.MaxCheckers <= 0)
        {
            opts = opts with { MaxCheckers = 4 };
        }
        else if (opts.MaxCheckers < 2)
        {
            opts = opts with { MaxCheckers = 2 }; // at least 1 diagnostics + 1 query checker
        }
        if (opts.IdleTimeout <= TimeSpan.Zero)
        {
            opts = opts with { IdleTimeout = TimeSpan.FromSeconds(30) };
        }
        int querySlots = opts.MaxCheckers - 1;
        CheckerPool pool = new(opts, program, querySlots, log);
        return pool;
    }

    private CheckerPool(CheckerPoolOptions opts, global::Ts.Compiler.Program program, int querySlots, Action<string>? log)
    {
        this.opts = opts;
        this.program = program;
        checkers = new global::Ts.Checker.Checker?[opts.MaxCheckers];
        heldBy = new string[opts.MaxCheckers];
        fileAssociations = [];
        requestAssociations = [];
        lastReleased = new DateTime[opts.MaxCheckers];
        diagSem = new SemaphoreSlim(1, 1);
        querySem = new SemaphoreSlim(querySlots, querySlots);
        persistentSem = new SemaphoreSlim(1, 1);
        globalDiagCheckerCount = new int[opts.MaxCheckers];
        if (log is null)
        {
            this.log = static _ => { };
        }
        else
        {
            this.log = log;
        }
    }

    // checkerpool.go:115-121 — holdTag returns the value to store in heldBy for the given request ID.
    private static string HoldTag(string requestID)
    {
        if (requestID == "")
        {
            return CheckerPoolConstants.CheckerHeldAnonymous;
        }
        return requestID;
    }

    // checkerpool.go:123-143 — GetChecker.
    internal (global::Ts.Checker.Checker Checker, Action Release) GetChecker(global::Ts.Core.RequestContext ctx, global::Ts.Ast.SourceFile? file)
    {
        global::Ts.Core.CheckerLifetime lifetime = ctx.CheckerLifetime;
        string requestID = ctx.RequestId;

        // Request affinity is cleaned up via a cancellation registration when the request
        // context is done. If the context can never be canceled, that cleanup would never
        // run and requestAssociations would grow unboundedly, so disable affinity entirely.
        if (!ctx.CanBeCanceled)
        {
            requestID = "";
        }

        switch (lifetime)
        {
            case global::Ts.Core.CheckerLifetime.Diagnostics:
                return GetDiagnosticsChecker(ctx, requestID);
            case global::Ts.Core.CheckerLifetime.Api:
                return GetPersistentChecker();
            default:
                return GetQueryChecker(ctx, requestID, file);
        }
    }

    // ICheckerPool adapter (see file header PORT-NOTE).
    (global::Ts.Checker.Checker Checker, Action Release) global::Ts.Checker.ICheckerPool.GetChecker(CancellationToken ctx, global::Ts.Ast.SourceFile? file)
    {
        return GetChecker(new global::Ts.Core.RequestContext { Cancellation = ctx }, file);
    }

    // checkerpool.go:145-216 — tryReacquireForRequest checks whether the given request already
    // has an associated checker. If so, it either returns the checker directly (still held)
    // or reacquires it by claiming a semaphore slot. The caller must provide the
    // appropriate semaphore and indicate whether this is a diagnostics request (isDiag).
    // If the associated checker is in the wrong category (e.g. a diagnostics index for a
    // query request), the association is deleted and normal acquisition proceeds.
    //
    // Returns (checker, release, true) if the request was served (either still held
    // or reclaimed). Returns (null, null, false) if the caller must proceed with
    // normal acquisition — in this case, a semaphore slot has already been claimed.
    // Must NOT be called with p.mu held.
    private (global::Ts.Checker.Checker? Checker, Action? Release, bool Served) TryReacquireForRequest(string requestID, SemaphoreSlim sem, bool isDiag)
    {
        if (requestID == "")
        {
            sem.Wait();
            return (null, null, false);
        }

        // Go pairs mu.Lock()/mu.Unlock() explicitly (unlocking before sem.Wait), so the
        // C# port uses Monitor.Enter/Exit directly instead of `lock`.
        Monitor.Enter(mu);
        if (!requestAssociations.TryGetValue(requestID, out int index))
        {
            Monitor.Exit(mu);
            sem.Wait();
            return (null, null, false);
        }

        // Validate that the associated index matches the expected category.
        // Index 0 is for diagnostics; indices 1+ are for queries.
        if ((isDiag && index != 0) || (!isDiag && index == 0))
        {
            requestAssociations.Remove(requestID);
            Monitor.Exit(mu);
            sem.Wait();
            return (null, null, false);
        }

        global::Ts.Checker.Checker? c = checkers[index];
        if (c is null)
        {
            requestAssociations.Remove(requestID);
            Monitor.Exit(mu);
            sem.Wait();
            return (null, null, false);
        }

        string? held = heldBy[index];
        if (held == requestID)
        {
            // Same request, checker still held — return without claiming a slot.
            Monitor.Exit(mu);
            return (c, Noop, true);
        }

        if (held == "")
        {
            // Same request reacquiring after release — need a semaphore slot.
            Monitor.Exit(mu);
            sem.Wait();
            Monitor.Enter(mu);
            try
            {
                // Re-check: checker may have been disposed while waiting for the slot.
                global::Ts.Checker.Checker? cc = checkers[index];
                if (ReferenceEquals(cc, c) && heldBy[index] == "")
                {
                    heldBy[index] = requestID;
                    return (c, CreateRelease(requestID, index, c), true);
                }
            }
            finally
            {
                Monitor.Exit(mu);
            }
            // Checker was replaced/disposed while waiting for the slot.
            // The slot is still claimed; the caller will use it for normal acquisition.
            return (null, null, false);
        }

        // Checker held by another request — claim a slot normally.
        Monitor.Exit(mu);
        sem.Wait();
        return (null, null, false);
    }

    // checkerpool.go:218-247 — getDiagnosticsChecker returns the dedicated diagnostics checker
    // (index 0). Creates it on first use. Blocks on diagSem if it's currently in use.
    private (global::Ts.Checker.Checker Checker, Action Release) GetDiagnosticsChecker(global::Ts.Core.RequestContext ctx, string requestID)
    {
        const int diagIndex = 0;

        if (TryReacquireForRequest(requestID, diagSem, true) is { Served: true } served)
        {
            return (served.Checker!, served.Release!);
        }

        // Token consumed — proceed with normal acquisition.
        lock (mu)
        {
            if (checkers[diagIndex] is null)
            {
                log("checkerpool: Creating diagnostics checker");
                var (c, _) = global::Ts.Checker.Checker.NewChecker(program, null);
                checkers[diagIndex] = c;
            }

            global::Ts.Checker.Checker checker = checkers[diagIndex]!;
            heldBy[diagIndex] = HoldTag(requestID);
            log("checkerpool: Acquired diagnostics checker for request " + HoldTag(requestID));
            if (requestID != "")
            {
                if (!requestAssociations.ContainsKey(requestID))
                {
                    requestAssociations[requestID] = diagIndex;
                    RegisterRequestCleanup(ctx, requestID);
                }
            }
            return (checker, CreateRelease(requestID, diagIndex, checker));
        }
    }

    // checkerpool.go:249-291 — getQueryChecker returns an ephemeral query checker from indices 1+.
    // Uses request affinity, then file affinity, then finds/creates.
    // Blocks on querySem if all query slots are in use.
    private (global::Ts.Checker.Checker Checker, Action Release) GetQueryChecker(global::Ts.Core.RequestContext ctx, string requestID, global::Ts.Ast.SourceFile? file)
    {
        if (TryReacquireForRequest(requestID, querySem, false) is { Served: true } served)
        {
            return (served.Checker!, served.Release!);
        }

        // Token consumed — proceed with normal acquisition.
        lock (mu)
        {
            // Try file affinity.
            if (file is not null)
            {
                if (fileAssociations.TryGetValue(file, out int index) && index > 0)
                {
                    global::Ts.Checker.Checker? c = checkers[index];
                    if (c is not null && heldBy[index] == "")
                    {
                        heldBy[index] = HoldTag(requestID);
                        if (requestID != "")
                        {
                            if (!requestAssociations.ContainsKey(requestID))
                            {
                                requestAssociations[requestID] = index;
                                RegisterRequestCleanup(ctx, requestID);
                            }
                        }
                        return (c, CreateRelease(requestID, index, c));
                    }
                }
            }

            // Find any available query checker or create one.
            // (Go reuses the names c/index from the file-affinity scope above; C# CS0136
            // forbids the shadowing, so this scope uses distinct names.)
            (global::Ts.Checker.Checker acquired, int acquiredIndex) = FindOrCreateQueryCheckerLocked();
            heldBy[acquiredIndex] = HoldTag(requestID);
            log($"checkerpool: Acquired query checker {acquiredIndex} for request {HoldTag(requestID)}");
            if (requestID != "")
            {
                if (!requestAssociations.ContainsKey(requestID))
                {
                    requestAssociations[requestID] = acquiredIndex;
                    RegisterRequestCleanup(ctx, requestID);
                }
            }
            if (file is not null)
            {
                fileAssociations[file] = acquiredIndex;
            }
            return (acquired, CreateRelease(requestID, acquiredIndex, acquired));
        }
    }

    // checkerpool.go:293-313 — findOrCreateQueryCheckerLocked returns an idle query checker or
    // creates one in the first empty slot. The semaphore guarantees at least one slot is
    // available. Must be called with p.mu held.
    private (global::Ts.Checker.Checker Checker, int Index) FindOrCreateQueryCheckerLocked()
    {
        // Prefer an existing idle checker.
        for (int i = 1; i < checkers.Length; i++)
        {
            global::Ts.Checker.Checker? c = checkers[i];
            if (c is not null && heldBy[i] == "")
            {
                return (c, i);
            }
        }
        // Create in the first empty slot.
        for (int i = 1; i < checkers.Length; i++)
        {
            if (checkers[i] is null)
            {
                log($"checkerpool: Creating query checker {i}");
                var (c, _) = global::Ts.Checker.Checker.NewChecker(program, null);
                checkers[i] = c;
                return (c, i);
            }
        }
        throw new InvalidOperationException("checkerpool: no available query slot despite holding semaphore token");
    }

    // checkerpool.go:315-343 — getPersistentChecker.
    private (global::Ts.Checker.Checker Checker, Action Release) GetPersistentChecker()
    {
        persistentSem.Wait();
        lock (mu)
        {
            if (persistentChecker is null)
            {
                log("checkerpool: Creating persistent checker");
                var (c, _) = global::Ts.Checker.Checker.NewChecker(program, null);
                persistentChecker = c;
            }

            global::Ts.Checker.Checker checker = persistentChecker!;
            persistentHeld = true;

            bool releaseRan = false;
            return (checker, () =>
            {
                if (releaseRan)
                {
                    return;
                }
                releaseRan = true;
                lock (mu)
                {
                    persistentHeld = false;
                    if (checker.WasCanceled()) // checkerpool.go:332
                    {
                        // A canceled checker panics on reuse, so drop it; the next API
                        // acquisition will create a fresh persistent checker.
                        log("checkerpool: Persistent checker was canceled, disposing");
                        if (ReferenceEquals(persistentChecker, checker))
                        {
                            persistentChecker = null;
                        }
                    }
                }
                persistentSem.Release();
            });
        }
    }

    // checkerpool.go:345-378 — createRelease.
    private Action CreateRelease(string requestID, int index, global::Ts.Checker.Checker c)
    {
        bool releaseRan = false;
        return () =>
        {
            if (releaseRan)
            {
                return;
            }
            releaseRan = true;

            lock (mu)
            {
                bool wasCanceled = c.WasCanceled(); // checkerpool.go:349
                if (wasCanceled)
                {
                    // Canceled checkers must be disposed.
                    log($"checkerpool: Checker {index} for request {HoldTag(requestID)} was canceled, disposing");
                    DisposeCheckerLocked(index, c);
                }
                else
                {
                    MergeGlobalDiagnosticsFromCheckerLocked(index, c);
                    heldBy[index] = "";
                    lastReleased[index] = DateTime.Now;
                    if (!discarded)
                    {
                        ScheduleCleanupLocked();
                    }
                    // If discarded, skip scheduling cleanup — checkers stay alive
                    // until the pool is garbage collected so that API clients can
                    // continue resolving type/symbol handles.
                }
            }

            // Unlock before releasing the semaphore slot. If we received from
            // the channel while holding p.mu, a woken goroutine could immediately
            // try to acquire p.mu, risking priority inversion or unnecessary
            // contention. (C# `lock` handles this; the release happens outside.)
            // Release the semaphore slot.
            if (index == 0)
            {
                diagSem.Release();
            }
            else
            {
                querySem.Release();
            }
        };
    }

    // checkerpool.go:380-390 — registerRequestCleanup deletes the request association when
    // the request context is done. This prevents the map from growing unboundedly with
    // completed request IDs. Must be called with p.mu held; the cleanup runs asynchronously.
    private void RegisterRequestCleanup(global::Ts.Core.RequestContext ctx, string requestID)
    {
        _ = ctx.Cancellation.Register(() =>
        {
            lock (mu)
            {
                requestAssociations.Remove(requestID);
            }
        });
    }

    // checkerpool.go:392-424 — scheduleCleanupLocked resets (or starts) the cleanup timer so
    // it fires at the earliest pending checker-expiration deadline among all currently idle,
    // unheld checkers. Must be called with p.mu held. Must NOT be called on discarded pools.
    private void ScheduleCleanupLocked()
    {
        DateTime earliestDeadline = DateTime.MinValue;
        for (int i = 0; i < checkers.Length; i++)
        {
            if (checkers[i] is null || heldBy[i] != "" || lastReleased[i] == DateTime.MinValue)
            {
                continue;
            }
            DateTime deadline = lastReleased[i] + opts.IdleTimeout;
            if (earliestDeadline == DateTime.MinValue || deadline < earliestDeadline)
            {
                earliestDeadline = deadline;
            }
        }
        if (earliestDeadline == DateTime.MinValue)
        {
            // No idle checkers remain — stop the timer if it exists.
            cleanupTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            cleanupTimer = null;
            return;
        }
        TimeSpan delay = earliestDeadline - DateTime.Now;
        if (delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromMilliseconds(1);
        }
        if (cleanupTimer is not null)
        {
            cleanupTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            cleanupTimer = new Timer(_ => CleanupIdleCheckers(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    // checkerpool.go:426-456 — cleanupIdleCheckers disposes checkers that have been idle for
    // longer than the idle timeout. The API checker is separate and never idle-cleaned.
    // checkerpool.go:428 — cleanupIdleCheckers. internal so the ported checkerpool_test.go can
    // drive the timer callback after Discard, exactly as Go's package-visible method allows.
    internal void CleanupIdleCheckers()
    {
        lock (mu)
        {
            // The timer callback may already have been in flight when Discard() called
            // Stop() (which does not guarantee the callback won't run). Bail out without
            // rescheduling so a discarded pool doesn't keep itself alive via a new timer.
            if (discarded)
            {
                return;
            }
            DateTime now = DateTime.Now;
            for (int i = 0; i < checkers.Length; i++)
            {
                global::Ts.Checker.Checker? c = checkers[i];
                if (c is null || heldBy[i] != "")
                {
                    continue;
                }
                if (lastReleased[i] == DateTime.MinValue)
                {
                    continue;
                }
                TimeSpan idle = now - lastReleased[i];
                if (idle >= opts.IdleTimeout)
                {
                    log($"checkerpool: Disposing idle checker {i} (idle {idle})");
                    DisposeCheckerLocked(i, c);
                }
            }
            // Reschedule for any remaining idle-but-not-yet-expired checkers.
            // scheduleCleanupLocked will Reset the existing timer rather than
            // creating a new one, avoiding goroutine leaks.
            ScheduleCleanupLocked();
        }
    }

    // checkerpool.go:458-476 — disposeCheckerLocked removes a checker from the pool and clears
    // all associations (file and request) that reference it. Must be called with p.mu held.
    private void DisposeCheckerLocked(int index, global::Ts.Checker.Checker c)
    {
        global::Ts.Core.Debug.Assert(ReferenceEquals(checkers[index], c));
        checkers[index] = null;
        heldBy[index] = "";
        globalDiagCheckerCount[index] = 0;
        lastReleased[index] = DateTime.MinValue;
        // map-iter: 序无关 — removing the references to one index; independent entries.
        foreach (global::Ts.Ast.SourceFile file in new List<global::Ts.Ast.SourceFile>(fileAssociations.Keys))
        {
            if (fileAssociations[file] == index)
            {
                fileAssociations.Remove(file);
            }
        }
        // map-iter: 序无关 — same for request associations.
        foreach (string req in new List<string>(requestAssociations.Keys))
        {
            if (requestAssociations[req] == index)
            {
                requestAssociations.Remove(req);
            }
        }
    }

    // checkerpool.go:478-492 — mergeGlobalDiagnosticsFromCheckerLocked checks if the given
    // checker has produced new global diagnostics since the last time we looked, and if so
    // merges them into the accumulated set. Must be called with p.mu held.
    private void MergeGlobalDiagnosticsFromCheckerLocked(int index, global::Ts.Checker.Checker c)
    {
        global::Ts.Ast.Diagnostic[] globals = c.GetGlobalDiagnostics();
        if (globals.Length == globalDiagCheckerCount[index])
        {
            return;
        }
        globalDiagCheckerCount[index] = globals.Length;
        int before = globalDiagAccumulated.Count;
        // Go: append(p.globalDiagAccumulated, globals...) then SortAndDeduplicateDiagnostics.
        var combined = new List<global::Ts.Ast.Diagnostic>(globalDiagAccumulated.Count + globals.Length);
        combined.AddRange(globalDiagAccumulated);
        combined.AddRange(globals);
        globalDiagAccumulated.Clear();
        globalDiagAccumulated.AddRange(global::Ts.Compiler.Program.SortAndDeduplicateDiagnostics(combined.ToArray()));
        if (globalDiagAccumulated.Count != before)
        {
            globalDiagChanged = true;
        }
    }

    // checkerpool.go:494-500 — GetGlobalDiagnostics returns the accumulated global diagnostics
    // collected from all checkers that have been used so far in this pool's lifetime.
    internal global::Ts.Ast.Diagnostic[] GetGlobalDiagnostics()
    {
        lock (mu)
        {
            return [.. globalDiagAccumulated];
        }
    }

    // checkerpool.go:502-510 — TakeNewGlobalDiagnostics reports whether new global diagnostics
    // have been accumulated since the last call, and resets the flag.
    internal bool TakeNewGlobalDiagnostics()
    {
        lock (mu)
        {
            bool changed = globalDiagChanged;
            globalDiagChanged = false;
            return changed;
        }
    }

    // checkerpool.go:512-528 — Discard signals that this pool's program has been replaced. The
    // pool remains functional but stops its idle-cleanup timer so that checkers
    // are not disposed until the pool is GC'd. The API checker is unaffected
    // since it is never idle-cleaned.
    internal void Discard()
    {
        lock (mu)
        {
            if (discarded)
            {
                return; // already discarded
            }
            log("checkerpool: Discarding pool, stopping idle cleanup");
            discarded = true;
            if (cleanupTimer is not null)
            {
                cleanupTimer.Change(Timeout.Infinite, Timeout.Infinite);
                cleanupTimer = null;
            }
        }
    }

    // checkerpool.go:530 — noop.
    private static void Noop()
    {
    }
}
