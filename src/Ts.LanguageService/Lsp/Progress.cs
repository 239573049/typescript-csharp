// Ported from tsgo internal/lsp/progress.go (whole file).
//
// PORT-NOTE (plan/05 §11): Go's single run goroutine with a select over
// {p.ch, delay.C, done} maps to a dedicated task that Task.WhenAny's over the channel
// reader, the pending delay task, and a done-task built from reporter.Done(). The delay
// timer is created through a timer factory (real Task.Delay by default) so tests can
// replace testing/synctest with manual firing. `stopDelay` maps to dropping the pending
// task reference (an abandoned Task.Delay completing has no observer, exactly like a
// stopped Go timer).

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using OrderedMap = global::Ts.Collections.OrderedMap<string, int>;

namespace Ts.Lsp;

// progress.go:13-17 — progressEvent.
internal readonly record struct ProgressEvent(
    global::Ts.Diagnostics.Message? Message,
    object?[] Args,
    bool Finish);

// progress.go:19-31 — progressReporter abstracts the LSP transport operations needed by
// projectLoadingProgress so the progress logic can be tested without a full Server
// instance.
internal interface ProgressReporter
{
    // done returns a token that is cancelled when the server is shutting down.
    // (Go: done() <-chan struct{} → CancellationToken, plan/05 §11.)
    CancellationToken Done();

    // localize converts a diagnostic message to a display string.
    string Localize(global::Ts.Diagnostics.Message msg, params object?[] args);

    // createWorkDoneProgress asks the client to create a progress token.
    void CreateWorkDoneProgress(string token);

    // sendProgress sends a $/progress notification.
    void SendProgress(string token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd value);
}

// progress.go:33-57 — serverProgressReporter adapts *Server to the progressReporter
// interface.
internal sealed class ServerProgressReporter(Server server) : ProgressReporter
{
    public CancellationToken Done() => server.BackgroundCancellation;

    public string Localize(global::Ts.Diagnostics.Message msg, params object?[] args)
    {
        return msg.Localize(server.Locale, args);
    }

    public void CreateWorkDoneProgress(string token)
    {
        _ = server.SendClientRequestFireAndForget(
            global::Ts.LsProto.Methods.WindowWorkDoneProgressCreateInfo,
            new global::Ts.LsProto.WorkDoneProgressCreateParams
            {
                Token = global::Ts.LsProto.IntegerOrString.FromString(token),
            });
    }

    public void SendProgress(string token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd value)
    {
        _ = server.SendNotification(
            global::Ts.LsProto.Methods.ProgressInfo,
            new global::Ts.LsProto.ProgressParams
            {
                Token = global::Ts.LsProto.IntegerOrString.FromString(token),
                Value = value,
            });
    }
}

// progress.go:59-88 — projectLoadingProgress manages LSP WorkDoneProgress indicators for
// long-running operations. A single persistent goroutine processes start/finish events,
// maintains a ref-counted map of active operations, and sends progress messages in
// order.
//
// To avoid flickering on fast operations, the indicator is not shown until progressDelay
// has elapsed since the first start event. If all operations complete before then, no
// progress UI is displayed.
//
// start/finish may block if the internal buffer (64 events) is full, but will bail out
// if the server's background context is cancelled.
internal sealed class ProjectLoadingProgress
{
    // Default delay-timer factory (Go: time.NewTimer).
    private static readonly Func<TimeSpan, CancellationToken, Task> RealDelayTimer =
        static (delay, _) => Task.Delay(delay, CancellationToken.None);

    private readonly ProgressReporter reporter;

    // progress.go:72 — ch chan progressEvent (cap 64).
    internal readonly Channel<ProgressEvent> ch = Channel.CreateBounded<ProgressEvent>(64);

    // progress.go:73 — delay time.Duration (time to wait before showing progress UI).
    private readonly TimeSpan delay;
    private readonly Func<TimeSpan, CancellationToken, Task> delayTimerFactory;

    // Test hook: completed at the end of every select-arm handling, so tests can wait
    // for the run loop to reach a quiescent state (Go: testing/synctest.Wait).
    private TaskCompletionSource idleSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // progress.go:76-78 — newProjectLoadingProgress(server *Server, delay time.Duration).
    internal static ProjectLoadingProgress New(Server server, TimeSpan delay)
    {
        return NewFromReporter(new ServerProgressReporter(server), delay);
    }

    // progress.go:80-88 — newProjectLoadingProgressFromReporter.
    internal static ProjectLoadingProgress NewFromReporter(
        ProgressReporter reporter,
        TimeSpan delay,
        Func<TimeSpan, CancellationToken, Task>? delayTimerFactory = null)
    {
        var p = new ProjectLoadingProgress(reporter, delay, delayTimerFactory ?? RealDelayTimer);
        _ = Task.Run(p.Run);
        return p;
    }

    private ProjectLoadingProgress(
        ProgressReporter reporter,
        TimeSpan delay,
        Func<TimeSpan, CancellationToken, Task> delayTimerFactory)
    {
        this.reporter = reporter;
        this.delay = delay;
        this.delayTimerFactory = delayTimerFactory;
    }

    // Test hook: capture a task that completes after the loop finishes processing the
    // event enqueued next (Go: synctest.Wait).
    internal Task NextIdleSignalForTesting() => idleSignal.Task;

    // progress.go:90-97 — start.
    internal void Start(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        Send(new ProgressEvent(message, args, false));
    }

    // progress.go:99-106 — finish.
    internal void Finish(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        Send(new ProgressEvent(message, args, true));
    }

    // Go: select { case p.ch <- ev: ; case <-p.reporter.done(): } — send, or drop when
    // the server is shutting down.
    private void Send(ProgressEvent ev)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(reporter.Done());
            ch.Writer.WriteAsync(ev, linked.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Server shutting down; drop the event.
        }
    }

    // Test hook for the ShutdownDuringStartAndFinish subtest, which fills the channel
    // buffer directly (Go: `for range cap(p.ch) { p.ch <- ... }`).
    internal bool TryEnqueueForTesting(ProgressEvent ev) => ch.Writer.TryWrite(ev);

    internal static ProgressEvent NewEventForTesting(
        global::Ts.Diagnostics.Message message,
        object?[] args,
        bool finish) => new(message, args, finish);

    // progress.go:108-196 — run is the persistent goroutine that processes all progress
    // events. It owns all mutable state: no external synchronization needed.
    private void Run()
    {
        OrderedMap loading = new();
        string token = ""; // current token; empty if no progress active
        int tokenID = 0;
        bool begun = false; // whether "begin" has been sent for the current token

        Task? delay = null;
        bool delayFired = false; // true after the delay timer fires

        // done arm: reporter.Done() as a task.
        var doneTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (CancellationTokenRegistration reg = reporter.Done().Register(
                   () => doneTcs.TrySetResult(), useSynchronizationContext: false))
        {
            while (true)
            {
                try
                {
                    Task<bool> readTask = ch.Reader.WaitToReadAsync(CancellationToken.None).AsTask();
                    Task completed = delay is not null
                        ? Task.WhenAny(readTask, delay, doneTcs.Task)
                        : Task.WhenAny(readTask, doneTcs.Task);
                    completed.GetAwaiter().GetResult();

                    if (completed == doneTcs.Task)
                    {
                        // Go: stopDelay(); return.
                        SignalIdle();
                        return;
                    }
                    if (delay is not null && completed == delay)
                    {
                        // Go: case <-delayC(): — a Go timer's channel delivers once; the
                        // pending task is dropped here for the same effect.
                        delay = null;
                        delayFired = true;
                        if (token != "" && loading.Size() > 0)
                        {
                            reporter.CreateWorkDoneProgress(token);
                            string? first = global::Ts.Core.CoreUtil.FirstOrNilSeq(loading.Keys());
                            begun = BeginOrReport(token, first ?? "", begun);
                        }
                        SignalIdle();
                        continue;
                    }

                    // Event arm.
                    if (!readTask.GetAwaiter().GetResult() || !ch.Reader.TryRead(out ProgressEvent ev))
                    {
                        SignalIdle();
                        return; // channel closed (never in Go); defensive.
                    }
                    string text = reporter.Localize(ev.Message!, ev.Args);
                    if (!ev.Finish)
                    {
                        int count = loading.GetOrZero(text);
                        loading.Set(text, count + 1);
                        if (token == "")
                        {
                            tokenID++;
                            token = $"tsgo-loading-{tokenID}";
                            begun = false;
                            if (delay <= TimeSpan.Zero)
                            {
                                delayFired = true;
                                reporter.CreateWorkDoneProgress(token);
                            }
                            else
                            {
                                delayFired = false;
                                delay = delayTimerFactory(delay, CancellationToken.None);
                            }
                        }
                        if (delayFired)
                        {
                            begun = BeginOrReport(token, text, begun);
                        }
                    }
                    else
                    {
                        int count = loading.GetOrZero(text);
                        if (count <= 1)
                        {
                            loading.TryDelete(text, out _);
                        }
                        else
                        {
                            loading.Set(text, count - 1);
                        }
                        if (token == "")
                        {
                            SignalIdle();
                            continue;
                        }
                        if (loading.Size() == 0)
                        {
                            if (begun)
                            {
                                reporter.SendProgress(token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd.FromEnd(
                                    new global::Ts.LsProto.WorkDoneProgressEnd()));
                            }
                            // Go: stopDelay().
                            delay = null;
                            token = "";
                        }
                        else if (delayFired)
                        {
                            string? first = global::Ts.Core.CoreUtil.FirstOrNilSeq(loading.Keys());
                            reporter.SendProgress(token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd.FromReport(
                                new global::Ts.LsProto.WorkDoneProgressReport
                                {
                                    Message = first,
                                }));
                        }
                    }
                    SignalIdle();
                }
                catch (Exception)
                {
                    // The loop never fails in Go; a fault here would spin the task.
                    // Defensive: stop processing, mirroring Go's goroutine exit on panic.
                    SignalIdle();
                    return;
                }
            }
        }
    }

    // Complete the idle signal and rotate it for the next waiter. Runs once per select
    // arm; only tests observe it.
    private void SignalIdle()
    {
        TaskCompletionSource current = idleSignal;
        idleSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        current.TrySetResult();
    }

    // progress.go:198-217 — beginOrReport sends WorkDoneProgressBegin if not yet begun,
    // otherwise sends WorkDoneProgressReport. Returns true to indicate begun state.
    private bool BeginOrReport(string token, string text, bool begun)
    {
        if (!begun)
        {
            string title = reporter.Localize(global::Ts.Diagnostics.Diagnostics.Loading);
            reporter.SendProgress(token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd.FromBegin(
                new global::Ts.LsProto.WorkDoneProgressBegin
                {
                    Title = title,
                    Message = text,
                }));
        }
        else
        {
            reporter.SendProgress(token, global::Ts.LsProto.WorkDoneProgressBeginOrReportOrEnd.FromReport(
                new global::Ts.LsProto.WorkDoneProgressReport
                {
                    Message = text,
                }));
        }
        return true;
    }
}
