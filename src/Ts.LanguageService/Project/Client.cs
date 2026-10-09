// Ported from tsgo internal/project/client.go (whole file).
// PORT-NOTE: Go's error-only returns map to a nullable Exception here (05-conventions
// §4: callers check non-null and log/aggregate it exactly like Go's err).

using System;
using System.Threading;

using FileSystemWatcher = global::Ts.LsProto.FileSystemWatcher;
using TelemetryEvent = global::Ts.LsProto.RequestFailureTelemetryEventOrPerformanceStatsTelemetryEventOrProjectInfoTelemetryEventOrNull;

namespace Ts.Project;

// client.go:11-21 — Client.
// (Go's lsproto.TelemetryEvent alias is the generated union spelled out below.)
public interface Client
{
    Exception? WatchFiles(CancellationToken ctx, WatcherID id, FileSystemWatcher[] watchers);

    Exception? UnwatchFiles(CancellationToken ctx, WatcherID id);

    Exception? RefreshDiagnostics(CancellationToken ctx);

    Exception? PublishDiagnostics(CancellationToken ctx, global::Ts.LsProto.PublishDiagnosticsParams @params);

    Exception? RefreshInlayHints(CancellationToken ctx);

    Exception? RefreshCodeLens(CancellationToken ctx);

    void ProgressStart(global::Ts.Diagnostics.Message message, params object?[] args);

    void ProgressFinish(global::Ts.Diagnostics.Message message, params object?[] args);

    Exception? SendTelemetry(CancellationToken ctx, TelemetryEvent telemetry);

    bool IsActive();
}
