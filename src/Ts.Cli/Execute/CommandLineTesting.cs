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
    global::Ts.FsWatch.IWatcher Watcher();

    // Called immediately before a project/configuration is compiled.
    void OnCompilation(global::Ts.TsOptions.ParsedCommandLine config);

    // Called after diagnostics/emit complete for the configuration.
    void OnCompilationComplete(global::Ts.TsOptions.ParsedCommandLine config, int diagnosticCount);

    // Receives the localized message body before timestamp/status formatting.
    void OnWatchStatus(string message);

    // Receives the localized message body before timestamp/status formatting.
    void OnBuildStatus(string message);
}
