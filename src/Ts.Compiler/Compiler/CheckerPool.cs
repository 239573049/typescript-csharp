// Ported from tsgo internal/compiler/checkerpool.go.
//
// This is the concrete compiler-package checker pool (Go: compiler.checkerPool). Per the PORT-NOTE
// in Checker/CheckerPool.cs — "It will move to the compiler namespace and consume the concrete
// Program once that lands" — the concretization lives here, holding the concrete Ts.Compiler.Program
// (which implements the Ts.Checker.Program consumer interface) and creating checkers via
// Ts.Checker.Checker.NewChecker. The Ts.Checker.CheckerPool skeleton is thereby superseded.
//
// Determinism (plan/03 §9): N checkers (default 4), files pinned by index%N, one lock per checker;
// creation is Once-guarded and dispatched through WorkGroup (large-stack workers).
//
// PORT-NOTE (phase 4/5): checker diagnostic methods (GetGlobalDiagnostics, GetDiagnostics) are stubs
// in the checker skeleton; the pool's orchestration is ported faithfully and its runtime deep path
// fills in as the checker does. GetGlobalDiagnostics' sort delegates to the deferred
// Program.SortAndDeduplicateDiagnostics.
//
// §1.5 (namespace shadowing): the sibling namespace Ts.Checker is fully qualified with global::.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;

namespace Ts.Compiler;

// checkerpool.go:20-58 — Go's compiler.CheckerPool interface is Ts.Checker.ICheckerPool (GetChecker).
internal sealed class CheckerPool : global::Ts.Checker.ICheckerPool
{
    private readonly Program program;
    private readonly global::Ts.Tracing.Tracing? tracing;

    private readonly Once createCheckersOnce = new();
    private readonly global::Ts.Checker.Checker[] checkers;
    private readonly Lock[] locks;
    private Dictionary<SourceFile, global::Ts.Checker.Checker> fileAssociations = null!;

    // checkerpool.go:38-56 (newCheckerPoolWithTracing)
    internal CheckerPool(Program program, global::Ts.Tracing.Tracing? tr = null)
    {
        this.program = program;
        tracing = tr;

        int checkerCount = 4;
        if (program.SingleThreaded())
        {
            checkerCount = 1;
        }
        else if (program.Options().Checkers is int c)
        {
            checkerCount = c;
        }
        checkerCount = Math.Max(Math.Min(Math.Min(checkerCount, program.Files.Length), 256), 1);

        checkers = new global::Ts.Checker.Checker[checkerCount];
        locks = new Lock[checkerCount];
    }

    // checkerpool.go:60-72
    public (global::Ts.Checker.Checker Checker, Action Release) GetChecker(CancellationToken ctx, SourceFile? file)
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

    // checkerpool.go:76-79
    internal (global::Ts.Checker.Checker Checker, Action Release) GetCheckerForFileNonExclusive(SourceFile file)
    {
        CreateCheckers();
        return (fileAssociations[file], Noop);
    }

    // checkerpool.go:81-90
    internal (global::Ts.Checker.Checker Checker, Action Release) GetCheckerForFileExclusive(CancellationToken ctx, SourceFile file)
    {
        _ = ctx;
        CreateCheckers();
        var c = fileAssociations[file];
        int idx = Array.IndexOf(checkers, c);
        locks[idx].Enter();
        return (c, OnceRelease(locks[idx]));
    }

    // checkerpool.go:92-95
    internal (global::Ts.Checker.Checker Checker, Action Release) GetCheckerNonExclusive()
    {
        CreateCheckers();
        return (checkers[0], Noop);
    }

    // checkerpool.go:97-119
    private void CreateCheckers()
    {
        createCheckersOnce.Do(() =>
        {
            int checkerCount = checkers.Length;
            var wg = WorkGroup.Create(program.SingleThreaded());
            for (int i = 0; i < checkerCount; i++)
            {
                int idx = i;
                wg.Queue(() =>
                {
                    global::Ts.Checker.Tracer? tracer = tracing is not null ? global::Ts.Checker.Tracer.NewTracer(tracing, idx) : null;
                    var (checker, lockObj) = global::Ts.Checker.Checker.NewChecker(program, tracer);
                    checkers[idx] = checker;
                    locks[idx] = lockObj;
                });
            }
            wg.RunAndWait();

            fileAssociations = new Dictionary<SourceFile, global::Ts.Checker.Checker>(program.Files.Length, ReferenceEqualityComparer<SourceFile>.Instance);
            for (int i = 0; i < program.Files.Length; i++)
            {
                // map-iter: n/a — deterministic index%N pinning over the fixed program file order.
                fileAssociations[program.Files[i]] = checkers[i % checkerCount];
            }
        });
    }

    // checkerpool.go:122-135
    internal void ForEachCheckerParallel(Action<int, global::Ts.Checker.Checker> cb)
    {
        CreateCheckers();
        var wg = WorkGroup.Create(program.SingleThreaded());
        for (int i = 0; i < checkers.Length; i++)
        {
            int idx = i;
            var checker = checkers[idx];
            wg.Queue(() =>
            {
                locks[idx].Enter();
                try
                {
                    cb(idx, checker);
                }
                finally
                {
                    locks[idx].Exit();
                }
            });
        }
        wg.RunAndWait();
    }

    // checkerpool.go:137-144
    internal Diagnostic[] GetGlobalDiagnostics()
    {
        CreateCheckers();
        var globalDiagnostics = new Diagnostic[checkers.Length][];
        ForEachCheckerParallel((idx, checker) => globalDiagnostics[idx] = checker.GetGlobalDiagnostics());
        var all = new List<Diagnostic>();
        foreach (var d in globalDiagnostics)
        {
            if (d is not null)
            {
                all.AddRange(d);
            }
        }
        return Program.SortAndDeduplicateDiagnostics(all.ToArray());
    }

    // checkerpool.go:149-166
    internal void ForEachCheckerGroupDo(CancellationToken ctx, SourceFile[] files, bool singleThreaded, Action<global::Ts.Checker.Checker, int, SourceFile> cb)
    {
        _ = ctx;
        CreateCheckers();

        int checkerCount = checkers.Length;
        var wg = WorkGroup.Create(singleThreaded);
        for (int c = 0; c < checkerCount; c++)
        {
            int checkerIdx = c;
            wg.Queue(() =>
            {
                locks[checkerIdx].Enter();
                try
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        var file = files[i];
                        var checker = checkers[checkerIdx];
                        if (ReferenceEquals(checker, fileAssociations[file]))
                        {
                            cb(checker, i, file);
                        }
                    }
                }
                finally
                {
                    locks[checkerIdx].Exit();
                }
            });
        }
        wg.RunAndWait();
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

    private static void Noop()
    {
    }
}
