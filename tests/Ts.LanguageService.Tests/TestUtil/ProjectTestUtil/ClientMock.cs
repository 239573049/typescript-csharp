// Ported from tsgo internal/testutil/projecttestutil/clientmock_generated.go (whole file).
//
// Go's moq generates a mock of project.Client with per-method Func fields and call
// recording; C# has no moq, so the equivalent mock is hand-written here with the same
// surface: one Func per interface method plus *Calls() accessors. Call recording is
// lock-guarded like the generated Go mock's RWMutex (the session background queue
// invokes the client from worker threads).

using System;
using System.Collections.Generic;
using System.Threading;

using FileSystemWatcher = global::Ts.LsProto.FileSystemWatcher;
using TelemetryEvent = global::Ts.LsProto.RequestFailureTelemetryEventOrPerformanceStatsTelemetryEventOrProjectInfoTelemetryEventOrNull;

namespace Ts.TestUtil.ProjectTestUtil;

// ClientMock is a mock implementation of Ts.Project.Client.
public sealed class ClientMock : global::Ts.Project.Client
{
    // ProgressCall holds details about a call to ProgressStart/ProgressFinish.
    public sealed record ProgressCall(global::Ts.Diagnostics.Message Message, object?[] Args);

    // PublishDiagnosticsCall holds details about a call to PublishDiagnostics.
    public sealed record PublishDiagnosticsCall(CancellationToken Ctx, global::Ts.LsProto.PublishDiagnosticsParams Params);

    // ContextCall holds details about a context-only call (RefreshDiagnostics/RefreshCodeLens/RefreshInlayHints).
    public sealed record ContextCall(CancellationToken Ctx);

    // SendTelemetryCall holds details about a call to SendTelemetry.
    public sealed record SendTelemetryCall(CancellationToken Ctx, TelemetryEvent Telemetry);

    // WatchFilesCall holds details about a call to WatchFiles.
    public sealed record WatchFilesCall(CancellationToken Ctx, global::Ts.Project.WatcherID ID, FileSystemWatcher[] Watchers);

    // UnwatchFilesCall holds details about a call to UnwatchFiles.
    public sealed record UnwatchFilesCall(CancellationToken Ctx, global::Ts.Project.WatcherID ID);

    // IsActiveFunc mocks the IsActive method.
    public Func<bool>? IsActiveFunc;

    // ProgressFinishFunc mocks the ProgressFinish method.
    public Action<global::Ts.Diagnostics.Message, object?[]>? ProgressFinishFunc;

    // ProgressStartFunc mocks the ProgressStart method.
    public Action<global::Ts.Diagnostics.Message, object?[]>? ProgressStartFunc;

    // PublishDiagnosticsFunc mocks the PublishDiagnostics method.
    public Func<CancellationToken, global::Ts.LsProto.PublishDiagnosticsParams, Exception?>? PublishDiagnosticsFunc;

    // RefreshCodeLensFunc mocks the RefreshCodeLens method.
    public Func<CancellationToken, Exception?>? RefreshCodeLensFunc;

    // RefreshDiagnosticsFunc mocks the RefreshDiagnostics method.
    public Func<CancellationToken, Exception?>? RefreshDiagnosticsFunc;

    // RefreshInlayHintsFunc mocks the RefreshInlayHints method.
    public Func<CancellationToken, Exception?>? RefreshInlayHintsFunc;

    // SendTelemetryFunc mocks the SendTelemetry method.
    public Func<CancellationToken, TelemetryEvent, Exception?>? SendTelemetryFunc;

    // UnwatchFilesFunc mocks the UnwatchFiles method.
    public Func<CancellationToken, global::Ts.Project.WatcherID, Exception?>? UnwatchFilesFunc;

    // WatchFilesFunc mocks the WatchFiles method.
    public Func<CancellationToken, global::Ts.Project.WatcherID, FileSystemWatcher[], Exception?>? WatchFilesFunc;

    // calls tracks calls to the methods.
    private readonly List<object> callsIsActive = [];
    private readonly List<ProgressCall> callsProgressFinish = [];
    private readonly List<ProgressCall> callsProgressStart = [];
    private readonly List<PublishDiagnosticsCall> callsPublishDiagnostics = [];
    private readonly List<ContextCall> callsRefreshCodeLens = [];
    private readonly List<ContextCall> callsRefreshDiagnostics = [];
    private readonly List<ContextCall> callsRefreshInlayHints = [];
    private readonly List<SendTelemetryCall> callsSendTelemetry = [];
    private readonly List<UnwatchFilesCall> callsUnwatchFiles = [];
    private readonly List<WatchFilesCall> callsWatchFiles = [];
    private readonly object lockIsActive = new();
    private readonly object lockProgressFinish = new();
    private readonly object lockProgressStart = new();
    private readonly object lockPublishDiagnostics = new();
    private readonly object lockRefreshCodeLens = new();
    private readonly object lockRefreshDiagnostics = new();
    private readonly object lockRefreshInlayHints = new();
    private readonly object lockSendTelemetry = new();
    private readonly object lockUnwatchFiles = new();
    private readonly object lockWatchFiles = new();

    // IsActive calls IsActiveFunc.
    public bool IsActive()
    {
        lock (lockIsActive)
        {
            callsIsActive.Add(new object());
        }
        if (IsActiveFunc == null)
        {
            return false;
        }
        return IsActiveFunc();
    }

    // IsActiveCalls gets all the calls that were made to IsActive.
    public IReadOnlyList<object> IsActiveCalls()
    {
        lock (lockIsActive)
        {
            return callsIsActive.ToArray();
        }
    }

    // ProgressFinish calls ProgressFinishFunc.
    public void ProgressFinish(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var callInfo = new ProgressCall(message, args);
        lock (lockProgressFinish)
        {
            callsProgressFinish.Add(callInfo);
        }
        ProgressFinishFunc?.Invoke(message, args);
    }

    // ProgressFinishCalls gets all the calls that were made to ProgressFinish.
    public IReadOnlyList<ProgressCall> ProgressFinishCalls()
    {
        lock (lockProgressFinish)
        {
            return callsProgressFinish.ToArray();
        }
    }

    // ProgressStart calls ProgressStartFunc.
    public void ProgressStart(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        var callInfo = new ProgressCall(message, args);
        lock (lockProgressStart)
        {
            callsProgressStart.Add(callInfo);
        }
        ProgressStartFunc?.Invoke(message, args);
    }

    // ProgressStartCalls gets all the calls that were made to ProgressStart.
    public IReadOnlyList<ProgressCall> ProgressStartCalls()
    {
        lock (lockProgressStart)
        {
            return callsProgressStart.ToArray();
        }
    }

    // PublishDiagnostics calls PublishDiagnosticsFunc.
    public Exception? PublishDiagnostics(CancellationToken ctx, global::Ts.LsProto.PublishDiagnosticsParams @params)
    {
        var callInfo = new PublishDiagnosticsCall(ctx, @params);
        lock (lockPublishDiagnostics)
        {
            callsPublishDiagnostics.Add(callInfo);
        }
        if (PublishDiagnosticsFunc == null)
        {
            return null;
        }
        return PublishDiagnosticsFunc(ctx, @params);
    }

    // PublishDiagnosticsCalls gets all the calls that were made to PublishDiagnostics.
    public IReadOnlyList<PublishDiagnosticsCall> PublishDiagnosticsCalls()
    {
        lock (lockPublishDiagnostics)
        {
            return callsPublishDiagnostics.ToArray();
        }
    }

    // RefreshCodeLens calls RefreshCodeLensFunc.
    public Exception? RefreshCodeLens(CancellationToken ctx)
    {
        var callInfo = new ContextCall(ctx);
        lock (lockRefreshCodeLens)
        {
            callsRefreshCodeLens.Add(callInfo);
        }
        if (RefreshCodeLensFunc == null)
        {
            return null;
        }
        return RefreshCodeLensFunc(ctx);
    }

    // RefreshCodeLensCalls gets all the calls that were made to RefreshCodeLens.
    public IReadOnlyList<ContextCall> RefreshCodeLensCalls()
    {
        lock (lockRefreshCodeLens)
        {
            return callsRefreshCodeLens.ToArray();
        }
    }

    // RefreshDiagnostics calls RefreshDiagnosticsFunc.
    public Exception? RefreshDiagnostics(CancellationToken ctx)
    {
        var callInfo = new ContextCall(ctx);
        lock (lockRefreshDiagnostics)
        {
            callsRefreshDiagnostics.Add(callInfo);
        }
        if (RefreshDiagnosticsFunc == null)
        {
            return null;
        }
        return RefreshDiagnosticsFunc(ctx);
    }

    // RefreshDiagnosticsCalls gets all the calls that were made to RefreshDiagnostics.
    public IReadOnlyList<ContextCall> RefreshDiagnosticsCalls()
    {
        lock (lockRefreshDiagnostics)
        {
            return callsRefreshDiagnostics.ToArray();
        }
    }

    // RefreshInlayHints calls RefreshInlayHintsFunc.
    public Exception? RefreshInlayHints(CancellationToken ctx)
    {
        var callInfo = new ContextCall(ctx);
        lock (lockRefreshInlayHints)
        {
            callsRefreshInlayHints.Add(callInfo);
        }
        if (RefreshInlayHintsFunc == null)
        {
            return null;
        }
        return RefreshInlayHintsFunc(ctx);
    }

    // RefreshInlayHintsCalls gets all the calls that were made to RefreshInlayHints.
    public IReadOnlyList<ContextCall> RefreshInlayHintsCalls()
    {
        lock (lockRefreshInlayHints)
        {
            return callsRefreshInlayHints.ToArray();
        }
    }

    // SendTelemetry calls SendTelemetryFunc.
    public Exception? SendTelemetry(CancellationToken ctx, TelemetryEvent telemetry)
    {
        var callInfo = new SendTelemetryCall(ctx, telemetry);
        lock (lockSendTelemetry)
        {
            callsSendTelemetry.Add(callInfo);
        }
        if (SendTelemetryFunc == null)
        {
            return null;
        }
        return SendTelemetryFunc(ctx, telemetry);
    }

    // SendTelemetryCalls gets all the calls that were made to SendTelemetry.
    public IReadOnlyList<SendTelemetryCall> SendTelemetryCalls()
    {
        lock (lockSendTelemetry)
        {
            return callsSendTelemetry.ToArray();
        }
    }

    // UnwatchFiles calls UnwatchFilesFunc.
    public Exception? UnwatchFiles(CancellationToken ctx, global::Ts.Project.WatcherID id)
    {
        var callInfo = new UnwatchFilesCall(ctx, id);
        lock (lockUnwatchFiles)
        {
            callsUnwatchFiles.Add(callInfo);
        }
        if (UnwatchFilesFunc == null)
        {
            return null;
        }
        return UnwatchFilesFunc(ctx, id);
    }

    // UnwatchFilesCalls gets all the calls that were made to UnwatchFiles.
    public IReadOnlyList<UnwatchFilesCall> UnwatchFilesCalls()
    {
        lock (lockUnwatchFiles)
        {
            return callsUnwatchFiles.ToArray();
        }
    }

    // WatchFiles calls WatchFilesFunc.
    public Exception? WatchFiles(CancellationToken ctx, global::Ts.Project.WatcherID id, FileSystemWatcher[] watchers)
    {
        var callInfo = new WatchFilesCall(ctx, id, watchers);
        lock (lockWatchFiles)
        {
            callsWatchFiles.Add(callInfo);
        }
        if (WatchFilesFunc == null)
        {
            return null;
        }
        return WatchFilesFunc(ctx, id, watchers);
    }

    // WatchFilesCalls gets all the calls that were made to WatchFiles.
    public IReadOnlyList<WatchFilesCall> WatchFilesCalls()
    {
        lock (lockWatchFiles)
        {
            return callsWatchFiles.ToArray();
        }
    }
}
