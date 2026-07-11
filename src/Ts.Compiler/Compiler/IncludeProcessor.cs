// Ported from tsgo internal/compiler/includeprocessor.go.
//
// The include processor accumulates file-include reasons and processing diagnostics during
// loading (fileIncludeReasons map + processingDiagnostics list + the add* mutators are ported
// real; the loader/filesParser depend on them).
//
// PORT-NOTE (phase 4/5): the diagnostic *materialization* surface (getDiagnostics,
// getReferenceLocation, getRelatedInfo, getCompilerOptionsObjectLiteralSyntax,
// explainRedirectAndImpliedFormat, addProcessingDiagnosticsForFileCasing) is deferred with the
// rest of program diagnostics and throws NotImplementedException. The load pipeline populates the
// maps but never materializes diagnostics.
//
// §1.5 (namespace shadowing): cross-package class names equal to a sibling namespace segment
// are fully qualified with global::.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;

namespace Ts.Compiler;

// includeprocessor.go:15-26
internal sealed class IncludeProcessor
{
    internal Dictionary<global::Ts.TsPath.Path, List<FileIncludeReason>> FileIncludeReasons = new();
    internal List<ProcessingDiagnostic> ProcessingDiagnostics = new();

    // includeprocessor.go:59-61
    internal void AddProcessingDiagnostic(params ProcessingDiagnostic[] d)
    {
        ProcessingDiagnostics.AddRange(d);
    }

    // includeprocessor.go:35-57 — diagnostic surface, deferred.
    internal DiagnosticsCollection GetDiagnostics(Program p) =>
        throw new NotImplementedException("include-processor diagnostics are deferred (phase 4/5 skeleton)");

    // includeprocessor.go:63-87 — diagnostic surface, deferred.
    internal void AddProcessingDiagnosticsForFileCasing(global::Ts.TsPath.Path file, string existingCasing, string currentCasing, FileIncludeReason reason) =>
        throw new NotImplementedException("file-casing diagnostics are deferred (phase 4/5 skeleton)");

    // includeprocessor.go:89-96 — diagnostic surface, deferred.
    internal ReferenceFileLocation GetReferenceLocation(FileIncludeReason r, Program program) =>
        throw new NotImplementedException("include-processor diagnostics are deferred (phase 4/5 skeleton)");
}
