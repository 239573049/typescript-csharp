// Ported from the test seam around tsgo internal/execute/tsc.CommandLineTesting and
// internal/execute/watchmanager.CommandLineTestingWithWatchBackend.

namespace Ts.Execute;

/// <summary>
/// Deterministic hooks used by execute-package tests. Production callers pass null.
/// </summary>
public interface ICommandLineTesting
{
    // Supplies the fswatch backend used by a watch session. Tests return a manually-triggered
    // watcher so no assertion depends on FileSystemWatcher scheduling or wall-clock sleeps.
    // (Legacy seam pre-dating WatchBackend; default null keeps Go-shape implementations clean.)
    global::Ts.FsWatch.IWatcher? Watcher()
    {
        return null;
    }

    // Called immediately before a project/configuration is compiled. (Legacy seam; default no-op.)
    void OnCompilation(global::Ts.TsOptions.ParsedCommandLine config)
    {
    }

    // Called after diagnostics/emit complete for the configuration. (Legacy seam; default no-op.)
    void OnCompilationComplete(global::Ts.TsOptions.ParsedCommandLine config, int diagnosticCount)
    {
    }

    // tsc.CommandLineTesting emit/list/statistics hooks (Go interface members; default no-ops so
    // existing execute-test fakes keep compiling until the tsbuild baseline runner needs them).
    void OnEmittedFiles(global::Ts.Compiler.EmitResult result, global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, System.DateTime>? mTimesCache)
    {
    }

    void OnListFilesStart(System.IO.TextWriter w)
    {
    }

    void OnListFilesEnd(System.IO.TextWriter w)
    {
    }

    void OnStatisticsStart(System.IO.TextWriter w)
    {
    }

    void OnStatisticsEnd(System.IO.TextWriter w)
    {
    }

    void OnBuildStatusReportStart(System.IO.TextWriter w)
    {
    }

    void OnBuildStatusReportEnd(System.IO.TextWriter w)
    {
    }

    void OnWatchStatusReportStart()
    {
    }

    void OnWatchStatusReportEnd()
    {
    }

    // compile.go:61 — deterministic trace writer for baselining; null → production arm.
    System.Action<global::Ts.Diagnostics.Message, object?[]>? GetTrace(System.IO.TextWriter w, global::Ts.Locale.Locale locale)
    {
        return null;
    }

    // Called with the (incremental) program after a project build (tsc.CommandLineTesting.OnProgram).
    void OnProgram(global::Ts.Execute.Incremental.Program? program)
    {
    }
}
