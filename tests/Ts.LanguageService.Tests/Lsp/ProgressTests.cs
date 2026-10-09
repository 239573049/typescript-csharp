// Ported from tsgo internal/lsp/progress_test.go (whole file).
//
// PORT-NOTE: Go drives these subtests under testing/synctest (virtual time). The C#
// ProjectLoadingProgress takes a delay-timer factory, so each subtest installs a fake
// timer it fires manually, and `synctest.Wait` maps to awaiting an idle signal that the
// run loop completes after every select-arm handling.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DiagnosticsMessage = global::Ts.Diagnostics.Message;
using LsDiagnostics = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Lsp.Tests;

// progress_test.go:15-20 — progressCall.
internal sealed record ProgressCall(string Method, string Token, string Title, string Msg);

// progress_test.go:22-67 — fakeProgressReporter.
internal sealed class FakeProgressReporter(CancellationToken done) : ProgressReporter
{
    private readonly Lock mu = new();
    private readonly List<ProgressCall> calls = [];

    public CancellationToken Done() => done;

    public string Localize(DiagnosticsMessage msg, params object?[] args)
    {
        return msg.Localize(global::Ts.Locale.Locale.Default, args);
    }

    public void CreateWorkDoneProgress(string token)
    {
        lock (mu)
        {
            calls.Add(new ProgressCall("create", token, "", ""));
        }
    }

    public void SendProgress(string token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd value)
    {
        lock (mu)
        {
            if (value.Begin is { } begin)
            {
                calls.Add(new ProgressCall("begin", token, begin.Title, begin.Message ?? ""));
            }
            else if (value.Report is { } report)
            {
                calls.Add(new ProgressCall("report", token, "", report.Message ?? ""));
            }
            else if (value.End is not null)
            {
                calls.Add(new ProgressCall("end", token, "", ""));
            }
        }
    }

    internal ProgressCall[] GetCalls()
    {
        lock (mu)
        {
            return [.. calls];
        }
    }
}

// Fake delay timer: tests fire pending timers manually instead of sleeping real time.
internal sealed class FakeDelayTimerFactory
{
    private readonly Lock mu = new();
    private readonly List<TaskCompletionSource> timers = [];

    internal Task Start(TimeSpan delay, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (mu)
        {
            timers.Add(tcs);
        }
        return tcs.Task;
    }

    // Complete every outstanding timer (abandoned ones are dropped by the run loop and
    // completing them has no effect, like a stopped Go timer).
    internal void FirePending()
    {
        TaskCompletionSource[] pending;
        lock (mu)
        {
            pending = [.. timers];
            timers.Clear();
        }
        foreach (var tcs in pending)
        {
            tcs.TrySetResult();
        }
    }
}

public sealed class ProgressTests
{
    private static void Settle(ProjectLoadingProgress p, Action act, FakeDelayTimerFactory? timers = null)
    {
        Task idle = p.NextIdleSignalForTesting();
        act();
        idle.GetAwaiter().GetResult();
        timers?.FirePending();
    }

    [Fact]
    public void StartFinishBeforeDelay()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(500), timers.Start);

        Settle(p, () => p.Start(LsDiagnostics.Project_0, "myProject"), timers);

        // Finish before the delay fires — no UI should appear.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "myProject"), timers);

        // Advance time past the delay to ensure no progress is sent.
        Settle(p, () => { }, timers);

        var calls = reporter.GetCalls();
        Assert.Empty(calls);

        cts.Cancel();
    }

    [Fact]
    public void ShowsAfterDelay()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(500), timers.Start);

        Settle(p, () => p.Start(LsDiagnostics.Project_0, "myProject"), timers);

        // Let the delay fire.
        timers.FirePending();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        var calls = reporter.GetCalls();
        Assert.True(calls.Length == 2, $"expected 2 calls (create + begin), got {calls.Length}");
        Assert.Equal("create", calls[0].Method);
        Assert.Equal("begin", calls[1].Method);
        Assert.Equal(LsDiagnostics.Loading.ToString(), calls[1].Title);

        // Finish the operation.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "myProject"), timers);

        calls = reporter.GetCalls();
        Assert.Equal("end", calls[^1].Method);

        cts.Cancel();
    }

    [Fact]
    public void ReportsMultipleOperations()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(100), timers.Start);

        // Start two different operations.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "projA"), timers);
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "projB"), timers);

        // Let the delay fire.
        timers.FirePending();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        var calls = reporter.GetCalls();
        // Should have: create, begin (with first message).
        Assert.True(calls.Length >= 2, $"expected at least 2 calls, got {calls.Length}");
        Assert.Equal("create", calls[0].Method);
        Assert.Equal("begin", calls[1].Method);

        // Finish one — should send a report with the remaining operation.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "projA"), timers);

        calls = reporter.GetCalls();
        bool found = false;
        foreach (var c in calls)
        {
            if (c.Method == "report")
            {
                found = true;
                break;
            }
        }
        Assert.True(found, $"expected a report after partial finish, got {calls.Length} calls");

        // Finish the second — should send end.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "projB"), timers);

        calls = reporter.GetCalls();
        Assert.Equal("end", calls[^1].Method);

        cts.Cancel();
    }

    [Fact]
    public void RefCounting()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(100), timers.Start);

        // Start the same operation twice (ref count = 2).
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj"), timers);
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj"), timers);

        timers.FirePending();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        // Finish once (ref count = 1) — should NOT end.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj"), timers);

        var calls = reporter.GetCalls();
        foreach (var c in calls)
        {
            Assert.NotEqual("end", c.Method);
        }

        // Finish again (ref count = 0) — should end.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj"), timers);

        calls = reporter.GetCalls();
        Assert.Equal("end", calls[^1].Method);

        cts.Cancel();
    }

    [Fact]
    public void NewTokenAfterEnd()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(100), timers.Start);

        // First cycle.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj"), timers);
        timers.FirePending();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        var calls = reporter.GetCalls();
        string firstToken = calls[0].Token;

        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj"), timers);

        // Second cycle — should get a new token.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj2"), timers);
        timers.FirePending();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        calls = reporter.GetCalls();
        string secondToken = "";
        foreach (var c in calls)
        {
            if (c.Method == "create" && c.Token != firstToken)
            {
                secondToken = c.Token;
                break;
            }
        }
        Assert.True(secondToken != "", $"expected a new token for second cycle, got {calls.Length} calls");
        Assert.NotEqual(firstToken, secondToken);

        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj2"), timers);

        cts.Cancel();
    }

    [Fact]
    public void StartBeforeDelayThenMoreAfterDelay()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(200), timers.Start);

        // Start before delay.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "projA"), timers);

        // Let delay fire.
        timers.FirePending();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        var calls = reporter.GetCalls();
        Assert.True(calls.Length >= 2, $"expected create + begin after delay, got {calls.Length} calls");

        // Start another operation after delay — should send a report immediately.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "projB"), timers);

        calls = reporter.GetCalls();
        Assert.Equal("report", calls[^1].Method);

        // Clean up.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "projA"), timers);
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "projB"), timers);

        cts.Cancel();
    }

    [Fact]
    public void FinishWithNoActiveToken()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(100), timers.Start);

        // Finish without any prior start — should be a no-op.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj"), timers);

        Assert.Empty(reporter.GetCalls());

        cts.Cancel();
    }

    [Fact]
    public void ShutdownDuringStartAndFinish()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(100), timers.Start);

        // Cancel context so the run goroutine exits.
        cts.Cancel();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();

        // Fill the channel buffer so start/finish block on send.
        for (int i = 0; i < 64; i++)
        {
            Assert.True(p.TryEnqueueForTesting(
                ProjectLoadingProgress.NewEventForTesting(LsDiagnostics.Project_0, ["fill"], false)));
        }

        // These should return immediately via the done() path
        // since the channel is full and the context is cancelled.
        p.Start(LsDiagnostics.Project_0, "proj");
        p.Finish(LsDiagnostics.Project_0, "proj");
    }

    [Fact]
    public void ShutdownWithActiveTimer()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(500), timers.Start);

        // Start an operation so the delay timer is created.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj"), timers);

        // Shutdown while the delay timer is still pending.
        cts.Cancel();
        p.NextIdleSignalForTesting().GetAwaiter().GetResult();
    }

    [Fact]
    public void ZeroDelay()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.Zero, timers.Start);

        // With zero delay, progress should begin immediately.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj"), timers);

        var calls = reporter.GetCalls();
        Assert.True(calls.Length == 2, $"expected 2 calls (create + begin), got {calls.Length}");
        Assert.Equal("create", calls[0].Method);
        Assert.Equal("begin", calls[1].Method);
        Assert.Equal("Project 'proj'", calls[1].Msg);

        // Start+finish should still produce begin and end.
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj"), timers);

        calls = reporter.GetCalls();
        Assert.Equal("end", calls[^1].Method);

        cts.Cancel();
    }

    [Fact]
    public void FinishBeforeDelayNoBegun()
    {
        using var cts = new CancellationTokenSource();
        var reporter = new FakeProgressReporter(cts.Token);
        var timers = new FakeDelayTimerFactory();
        var p = ProjectLoadingProgress.NewFromReporter(reporter, TimeSpan.FromMilliseconds(500), timers.Start);

        // Start, then finish before delay — begun is false, so no end is sent.
        Settle(p, () => p.Start(LsDiagnostics.Project_0, "proj"), timers);
        Settle(p, () => p.Finish(LsDiagnostics.Project_0, "proj"), timers);

        var calls = reporter.GetCalls();
        foreach (var c in calls)
        {
            Assert.NotEqual("end", c.Method);
        }

        cts.Cancel();
    }
}
