// Ported from tsgo internal/ls/host.go (foundation subset).
//
// The complete Go host also exposes preferences, converters, source-map line data,
// auto-import state, and file-system enumeration. Those members belong to the
// deferred LSP/project/completions layers. This foundation host keeps only the
// Program/checker lifetime surface used by position-based semantic queries.

using System;
using System.Threading;

namespace Ts.Ls;

public interface Host
{
    global::Ts.Compiler.Program GetProgram();

    (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFile(
        CancellationToken cancellationToken,
        global::Ts.Ast.SourceFile file);
}

public sealed class ProgramHost : Host
{
    private readonly global::Ts.Compiler.Program program;

    public ProgramHost(global::Ts.Compiler.Program program)
    {
        this.program = program ?? throw new ArgumentNullException(nameof(program));
    }

    public global::Ts.Compiler.Program GetProgram() => program;

    public (global::Ts.Checker.Checker Checker, Action Release) GetTypeCheckerForFile(
        CancellationToken cancellationToken,
        global::Ts.Ast.SourceFile file)
    {
        return program.GetTypeCheckerForFile(cancellationToken, file);
    }
}
