// Ported from tsgo internal/parser/references.go, plus two helpers that live in
// tsgo internal/ast (parseoptions.go SetExternalModuleIndicator, utilities.go
// CompareNodePositions) but are not yet present in Ts.Ast. They are localized here
// (parser is their consumer) with PORT-NOTEs rather than modifying out-of-scope ast files.
using System;

using Ts.Ast;
using Ts.Core;

namespace Ts.Parser;

internal static class References
{
    // PORT-NOTE / DEVIATION (AGENTS §6): faithful port deferred. references.go's
    // collectModuleReferences depends on ast helpers not yet ported:
    // IsAnyImportOrReExport, GetExternalModuleName, ForEachDynamicImportOrRequireCall,
    // IsAmbientModule, IsExternalModule. This populates SourceFile.Imports /
    // ModuleAugmentations / AmbientModuleNames, consumed by binder/checker in later
    // phases — not by the parse-tree structure. Left as a no-op until those ast utilities land.
    // TODO(phase-4): restore the faithful collectExternalModuleReferences / collectModuleReferences.
    public static void CollectExternalModuleReferences(SourceFile file)
    {
        // !!! pending ast helpers (see PORT-NOTE above)
    }

    // Ported from tsgo internal/ast/utilities.go:3730 CompareNodePositions.
    public static int CompareNodePositions(Node n1, Node n2) => TextRange.CompareTextRanges(n1.Loc, n2.Loc);

    // Ported from tsgo internal/ast/parseoptions.go:56 SetExternalModuleIndicator.
    // PORT-NOTE: belongs in ast.SetExternalModuleIndicator; localized until the ast port lands.
    public static void SetExternalModuleIndicator(SourceFile file, ExternalModuleIndicatorOptions opts)
    {
        file.ExternalModuleIndicator = GetExternalModuleIndicator(file, opts);
    }

    private static Node? GetExternalModuleIndicator(SourceFile file, ExternalModuleIndicatorOptions opts)
    {
        if (file.ScriptKind == ScriptKind.Json)
        {
            return null;
        }
        Node? node = IsFileProbablyExternalModule(file);
        if (node != null)
        {
            return node;
        }
        if (file.IsDeclarationFile)
        {
            return null;
        }
        // PORT-NOTE / DEVIATION: opts.Jsx path (isFileModuleFromUsingJSXTag) not yet ported
        // (ast helper missing). JSX-tag-only module detection is deferred.
        if (opts.Force)
        {
            return file.AsNode();
        }
        return null;
    }

    private static Node? IsFileProbablyExternalModule(SourceFile sourceFile)
    {
        if (sourceFile.Statements != null)
        {
            foreach (Node statement in sourceFile.Statements.Nodes)
            {
                if (IsAnExternalModuleIndicatorNode(statement))
                {
                    return statement;
                }
            }
        }
        return GetImportMetaIfNecessary(sourceFile);
    }

    private static bool IsAnExternalModuleIndicatorNode(Node node) =>
        AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export) ||
        (NodeTests.IsImportEqualsDeclaration(node) && NodeTests.IsExternalModuleReference(node.AsImportEqualsDeclaration().ModuleReference!)) ||
        NodeTests.IsImportDeclaration(node) || NodeTests.IsExportAssignment(node) || NodeTests.IsExportDeclaration(node);

    private static Node? GetImportMetaIfNecessary(SourceFile sourceFile)
    {
        // PORT-NOTE / DEVIATION: ast.IsImportMeta not yet ported; import.meta module
        // detection deferred. Affects only files with NodeFlags.PossiblyContainsImportMeta.
        return null;
    }
}
