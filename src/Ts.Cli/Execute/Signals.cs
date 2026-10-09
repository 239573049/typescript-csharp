// Ported from tsgo cmd/tsgo/main.go:28-29 — `ctx, stop := signal.NotifyContext(
// context.Background(), syscall.SIGINT, syscall.SIGTERM)`.
//
// Semantics being mirrored (Go os/signal.NotifyContext):
//   - The first SIGINT/SIGTERM cancels the returned token; it does NOT terminate the
//     process (default signal behavior is replaced from registration until Dispose).
//   - Signals after the first are swallowed (Go's relay goroutine exits after one
//     receive; nothing reads the channel afterwards), so a double Ctrl-C cannot kill a
//     watch session — matching tsgo.
//   - stop()/Dispose unregisters and restores default behavior.
//
// carriers: SIGINT → Console.CancelKeyPress (e.Cancel suppresses the default kill, all
// platforms); SIGTERM → PosixSignalRegistration (unix; on Windows SIGTERM rides the same
// console ctrl-handler surface Go's os/signal uses). No reflection, AOT-safe.
//
// WIRED (Go main.go:28-32, lsp.go:47-48, api.go:53-54): Program.cs (default tsc path),
// Lsp.cs RunLSP and Api.cs RunAPI each scope a NotifyContext; the default path passes
// Token into Tsc.CommandLine (→ Watcher.Start / Build.Orchestrator.Start, matching Go's
// ctx threading into watcher.start (tsc.go:242) and orchestrator.Start (tsc.go:118)).

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ts.Execute;

public static class Signals
{
    // main.go:28 — signal.NotifyContext(context.Background(), SIGINT, SIGTERM).
    public static SignalNotifyContext NotifyContext() => new();
}

// The (ctx, stop) pair of Go's signal.NotifyContext: Token is ctx, Dispose is stop.
public sealed class SignalNotifyContext : IDisposable
{
    private readonly CancellationTokenSource cts = new();
    private PosixSignalRegistration? sigterm;
    private int signaled;

    // Go's NotifyContext stopper is a sync.Once: the LSP watchdog calls stop() from its
    // goroutine while scope exit runs it again on the main thread (lsp.go:102 + defer).
    private int stopOnce;

    public SignalNotifyContext()
    {
        // SIGINT: Ctrl-C / Ctrl-Break on every platform.
        Console.CancelKeyPress += OnConsoleCancel;
        // SIGTERM:PosixSignalRegistration keeps the process alive via e.Cancel, mirroring
        // Go's Notify replacing the default handler. Registration is best-effort (Go's
        // signal.Notify likewise never fails at startup).
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    public CancellationToken Token => cts.Token;

    private void OnConsoleCancel(object? sender, ConsoleCancelEventArgs e)
    {
        // Cancel the default process-kill: Go replaced SIGINT handling with ctx delivery.
        e.Cancel = true;
        CancelOnce();
    }

    private void OnPosixSignal(PosixSignalContext e)
    {
        e.Cancel = true;
        CancelOnce();
    }

    // Only the FIRST signal cancels; later ones are swallowed until Dispose (main.go
    // NotifyContext contract).
    private void CancelOnce()
    {
        if (Interlocked.Exchange(ref signaled, 1) == 0)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // stop() won the race with a signal already in flight — same as Go, where
                // the relay goroutine is gone and the signal falls back to default handling.
            }
        }
    }

    // main.go:29 — defer stop(): unregister and restore default signal behavior. Safe to
    // call repeatedly and from any thread (Go: sync.Once-guarded stop).
    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopOnce, 1) != 0)
        {
            return;
        }
        Console.CancelKeyPress -= OnConsoleCancel;
        sigterm?.Dispose();
        sigterm = null;
        cts.Dispose();
    }
}
