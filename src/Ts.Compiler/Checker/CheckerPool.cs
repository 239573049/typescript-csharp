// Ported from tsgo internal/compiler/checkerpool.go (plan/03 §9.1).
//
// PORT-NOTE: the Go pool lives in the `compiler` package and depends on the concrete
// `compiler.Program`; that package is not yet ported (this skeleton unlocks it). The pool is
// retargeted here onto the checker `Program` interface so it is self-contained. It will move to
// the compiler namespace and consume the concrete Program once that lands. `singleThreaded` is a
// ctor flag (Go reads program.SingleThreaded(), not on the checker Program interface).
//
// Determinism (plan/03 §9): N checkers (default 4), files pinned by index%N, one lock per checker;
// creation is Once-guarded and dispatched through WorkGroup (large-stack workers).

using System.Threading;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

// checkerpool.go:20-22
public interface ICheckerPool
{
    (Checker Checker, Action Release) GetChecker(CancellationToken ctx, SourceFile? file);
}

// checkerpool.go:24-58
public sealed class CheckerPool : ICheckerPool
{
    private readonly Program program;
    private readonly SourceFile[] files;
    private readonly bool singleThreaded;
    private readonly global::Ts.Tracing.Tracing? tracing;

    private readonly Once createCheckersOnce = new();
    private readonly Checker[] checkers;
    private readonly Lock[] locks;
    private Dictionary<SourceFile, Checker> fileAssociations = null!;

    public CheckerPool(Program program, bool singleThreaded = false, global::Ts.Tracing.Tracing? tracing = null)
    {
        this.program = program;
        files = program.SourceFiles();
        this.singleThreaded = singleThreaded;
        this.tracing = tracing;

        int checkerCount = 4;
        if (singleThreaded)
        {
            checkerCount = 1;
        }
        else if (program.Options().Checkers is int c)
        {
            checkerCount = c;
        }
        checkerCount = Math.Max(Math.Min(Math.Min(checkerCount, files.Length), 256), 1);

        checkers = new Checker[checkerCount];
        locks = new Lock[checkerCount];
    }

    // checkerpool.go:62-72
    public (Checker Checker, Action Release) GetChecker(CancellationToken ctx, SourceFile? file)
    {
        if (file is not null)
        {
            return GetCheckerForFileExclusive(ctx, file);
        }
        CreateCheckers();
        var c = checkers[0];
        locks[0].Enter();
        return (c, OnceRelease(locks[0]));
    }

    // checkerpool.go:82-90
    private (Checker Checker, Action Release) GetCheckerForFileExclusive(CancellationToken ctx, SourceFile file)
    {
        CreateCheckers();
        var c = fileAssociations[file];
        int idx = Array.IndexOf(checkers, c);
        locks[idx].Enter();
        return (c, OnceRelease(locks[idx]));
    }

    // checkerpool.go:98-119
    private void CreateCheckers()
    {
        createCheckersOnce.Do(() =>
        {
            int checkerCount = checkers.Length;
            var wg = WorkGroup.Create(singleThreaded);
            for (int i = 0; i < checkerCount; i++)
            {
                int idx = i;
                wg.Queue(() =>
                {
                    Tracer? tracer = tracing is not null ? Tracer.NewTracer(tracing, idx) : null;
                    var (checker, lockObj) = Checker.NewChecker(program, tracer);
                    checkers[idx] = checker;
                    locks[idx] = lockObj;
                });
            }
            wg.RunAndWait();

            fileAssociations = new Dictionary<SourceFile, Checker>(files.Length, ReferenceEqualityComparer<SourceFile>.Instance);
            for (int i = 0; i < files.Length; i++)
            {
                // map-iter: n/a — deterministic index%N pinning over the fixed program file order.
                fileAssociations[files[i]] = checkers[i % checkerCount];
            }
        });
    }

    // sync.OnceFunc: idempotent release of the checker's lock (checkerpool.go:69-71, 87-89).
    private static Action OnceRelease(Lock l)
    {
        int released = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                l.Exit();
            }
        };
    }
}
