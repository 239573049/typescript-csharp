// Compiler-local port of tsgo internal/parser/references.go collectExternalModuleReferences /
// collectModuleReferences, restricted to the static import/re-export path.
//
// PORT-NOTE / DEVIATION (registered): in tsgo this runs inside the parser (parser.go:458) so that
// SourceFile.Imports is populated at parse time. The C# Parser/References.cs port is still a
// `TODO(phase-4)` stub (it depends on ast helpers not yet ported: IsAnyImportOrReExport,
// GetExternalModuleName), so SourceFile.Imports() comes back empty and the file-loader has nothing
// to resolve — which blocks the whole phase 4/5 milestone. Rather than edit the parser package
// (out of scope), the loader calls this from ParseSourceFile immediately after the host returns the
// file, populating Imports via the public Ast API SetImportsOfSourceFile. It is a no-op once Imports
// is already set, so it composes cleanly with the eventual parser port. IsAnyImportOrReExport and
// GetExternalModuleName are inlined here (utilities.go:1861-1901); the dynamic-import branch,
// ambient module-declaration augmentation collection, and UsesUriStyleNodeCoreModules `node:`
// detection are deferred (not needed by the milestone).
//
// §1.5 (namespace shadowing): sibling-namespace static classes are fully qualified with global::.

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Compiler;

internal static class ImportCollector
{
    // references.go:10-21 (static path)
    internal static void CollectExternalModuleReferences(SourceFile file)
    {
        if (file.Imports() is { Length: > 0 })
        {
            return; // already collected (e.g. once the parser port lands)
        }
        if (file.Statements is null)
        {
            return;
        }

        List<Node>? imports = null;
        foreach (var node in file.Statements.Nodes)
        {
            CollectModuleReferences(node, ref imports);
        }
        if (imports is not null)
        {
            AstUtilities.SetImportsOfSourceFile(file, imports.ToArray());
        }
    }

    // references.go:23-72 (import/re-export branch)
    private static void CollectModuleReferences(Node node, ref List<Node>? imports)
    {
        if (IsAnyImportOrReExport(node))
        {
            var moduleNameExpr = GetExternalModuleName(node);
            if (moduleNameExpr is not null && NodeTests.IsStringLiteral(moduleNameExpr))
            {
                var moduleName = moduleNameExpr.Text();
                // inAmbientModule is always false on this static top-level path.
                if (moduleName != "")
                {
                    (imports ??= new List<Node>()).Add(moduleNameExpr);
                }
            }
            return;
        }
        // PORT-NOTE (phase 4/5): ambient module-declaration augmentation collection deferred.
    }

    // utilities.go:1861-1882
    private static bool IsAnyImportOrReExport(Node node) =>
        AstUtilities.NodeKindIs(node, SyntaxKind.ImportDeclaration, SyntaxKind.ImportEqualsDeclaration) ||
        node.Kind == SyntaxKind.JSImportDeclaration ||
        NodeTests.IsExportDeclaration(node);

    // utilities.go:1884-1901 (only the import/re-export kinds are reachable from CollectModuleReferences)
    private static Node? GetExternalModuleName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportDeclaration:
                return node.ModuleSpecifier();
            case SyntaxKind.ImportEqualsDeclaration:
                var moduleReference = node.AsImportEqualsDeclaration().ModuleReference;
                if (moduleReference is not null && moduleReference.Kind == SyntaxKind.ExternalModuleReference)
                {
                    return moduleReference.Expression();
                }
                return null;
            default:
                return null;
        }
    }
}
