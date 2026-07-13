// Ported from tsgo internal/execute/tsc/diagnostics.go (ExitStatus) and internal/execute/tsc.go
// (CommandLineResult).
//
// SCOPE: plan/06 assigns the execute package to the CLI project (Ts.Execute.*, §13.2 归属待澄清);
// this wave lands the tsc type-check-and-report subset under src/Ts.Cli/Execute/ with namespace
// Ts.Execute (per plan/05 §1.5: `internal/execute` → `Ts.Execute`).

namespace Ts.Execute;

// diagnostics.go:30-39
public enum ExitStatus
{
    Success = 0,
    DiagnosticsPresent_OutputsSkipped = 1,
    DiagnosticsPresent_OutputsGenerated = 2,
    InvalidProject_OutputsSkipped = 3,
    ProjectReferenceCycle_OutputsSkipped = 4,
    NotImplemented = 5,
}

// compile.go:45-48 — watcher returned by watch-mode command lines. Go exposes DoCycle to the
// deterministic watch tests; IDisposable is the C# lifetime equivalent of closing all watches.
public interface IExecuteWatcher : System.IDisposable
{
    void DoCycle();
}

// tsc.go:45-48 — the CommandLine driver's result.
public sealed class CommandLineResult
{
    public ExitStatus Status;
    public IExecuteWatcher? Watcher;
}
