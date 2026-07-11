// Ported from tsgo internal/compiler/processingDiagnostic.go.
//
// The loader/filesParser create processingDiagnostic + includeExplainingDiagnostic as plain
// data during loading; those shapes and accessors are ported real.
//
// PORT-NOTE (phase 4/5): toDiagnostic / createDiagnosticExplainingFile turn that data into
// user-facing diagnostics. That surface is deferred with the rest of program diagnostics and
// throws NotImplementedException; the load pipeline never invokes it.

using System;
using Ts.Ast;

namespace Ts.Compiler;

// processingDiagnostic.go:15-20
internal enum ProcessingDiagnosticKind
{
    UnknownReference = 0,
    ExplainingFileInclude,
}

// processingDiagnostic.go:22-25
internal sealed class ProcessingDiagnostic
{
    internal ProcessingDiagnosticKind Kind;
    internal object? Data;

    // processingDiagnostic.go:27-29
    internal FileIncludeReason AsFileIncludeReason() => (FileIncludeReason)Data!;

    // processingDiagnostic.go:38-40
    internal IncludeExplainingDiagnostic AsIncludeExplainingDiagnostic() => (IncludeExplainingDiagnostic)Data!;

    // processingDiagnostic.go:42-135 — diagnostic surface, deferred.
    internal Diagnostic ToDiagnostic(Program program) =>
        throw new NotImplementedException("processing-diagnostic explanation is deferred (phase 4/5 skeleton)");
}

// processingDiagnostic.go:31-36
internal sealed class IncludeExplainingDiagnostic
{
    internal global::Ts.TsPath.Path File;
    internal FileIncludeReason? DiagnosticReason;
    internal global::Ts.Diagnostics.Message? Message;
    internal object?[]? Args;
}
