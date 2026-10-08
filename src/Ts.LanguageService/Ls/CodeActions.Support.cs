// Shared helpers for the Ls/CodeActions* partials that do not belong to a single Go file.
//
// PORT-NOTE(deferred dependency): codeactions.go:347 createOrganizeImportsAction calls
// l.OrganizeImports — that method lives in internal/ls/organizeimports.go:24-115, whose worker
// chain (organizeImportsWorker / organizeExportsWorker / coalesceImportsWorker /
// removeUnusedImports / groupByNewlineContiguous, ~950 lines) is its own port unit and depends
// on the checker.IsDeclarationUsed surface that has not landed. The lsutil half of
// organizeimports.go (comparer detection) is already ported as the static OrganizeImports class
// (Ls/OrganizeImports.cs). Until the method's own port unit lands, the faithful call shape is
// kept here and throws, exactly like the deferred compiler surfaces (see Diagnostics.cs's
// GetDeclarationDiagnostics note).

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // organizeimports.go:24-115 — OrganizeImports organizes imports by:
    //  1. Removing unused imports
    //  2. Coalescing imports from the same module
    //  3. Sorting imports
    internal Dictionary<string, List<global::Ts.LsProto.TextEdit>> OrganizeImports(
        System.Threading.CancellationToken ctx,
        SourceFile sourceFile,
        global::Ts.Compiler.Program program,
        global::Ts.LsProto.CodeActionKind kind)
    {
        throw new System.NotImplementedException(
            "internal/ls/organizeimports.go LanguageService.OrganizeImports lands with its own port unit (see CodeActions.Support.cs header).");
    }
}
