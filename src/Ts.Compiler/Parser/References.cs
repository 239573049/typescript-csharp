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
    // references.go:11 — collectExternalModuleReferences.
    public static void CollectExternalModuleReferences(SourceFile file)
    {
        foreach (var node in file.Statements?.Nodes ?? [])
        {
            CollectModuleReferences(file, node, false /*inAmbientModule*/);
        }

        if ((file.Flags & NodeFlags.PossiblyContainsDynamicImport) != 0 || AstUtilities.IsInJSFile(file.AsNode()))
        {
            AstUtilities.ForEachDynamicImportOrRequireCall(file, /*includeTypeSpaceImports*/ true, /*requireStringLiteralLikeArgument*/ true, (node, moduleSpecifier) =>
            {
                AstUtilities.SetImportsOfSourceFile(file, [.. file.Imports() ?? [], moduleSpecifier]);
                return false;
            });
        }
    }

    // references.go:23 — collectModuleReferences.
    private static void CollectModuleReferences(SourceFile file, Node node, bool inAmbientModule)
    {
        if (IsAnyImportOrReExport(node))
        {
            var moduleNameExpr = GetExternalModuleName(node);
            if (moduleNameExpr != null && NodeTests.IsStringLiteral(moduleNameExpr))
            {
                var moduleName = moduleNameExpr.Text();
                if (moduleName != "" && (!inAmbientModule || !global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(moduleName)))
                {
                    AstUtilities.SetImportsOfSourceFile(file, [.. file.Imports() ?? [], moduleNameExpr]);
                    if (file.UsesUriStyleNodeCoreModules != Tristate.True && !file.IsDeclarationFile)
                    {
                        if (moduleName.StartsWith("node:", StringComparison.Ordinal) && !global::Ts.Core.Core.ExclusivelyPrefixedNodeCoreModules.Contains(moduleName))
                        {
                            file.UsesUriStyleNodeCoreModules = Tristate.True;
                        }
                        else if (file.UsesUriStyleNodeCoreModules == Tristate.Unknown && global::Ts.Core.Core.UnprefixedNodeCoreModules.Contains(moduleName))
                        {
                            file.UsesUriStyleNodeCoreModules = Tristate.False;
                        }
                    }
                }
            }
            return;
        }
        if (NodeTests.IsModuleDeclaration(node) && AstUtilities.IsAmbientModule(node) && (inAmbientModule || AstUtilities.HasSyntacticModifier(node, ModifierFlags.Ambient) || file.IsDeclarationFile))
        {
            var nameText = node.AsModuleDeclaration().Name()!.Text();
            // Ambient module declarations can be interpreted as augmentations for existing external modules.
            if (AstUtilities.IsExternalModule(file) || (inAmbientModule && !global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(nameText)))
            {
                file.ModuleAugmentations = [.. file.ModuleAugmentations ?? [], node.AsModuleDeclaration().Name()!];
            }
            else if (!inAmbientModule)
            {
                file.AmbientModuleNames = [.. file.AmbientModuleNames ?? [], nameText];
                // Body of an ambient module is always a module block, if it exists.
                if (node.Body() != null)
                {
                    foreach (var statement in node.Body()!.Statements() ?? [])
                    {
                        CollectModuleReferences(file, statement, true /*inAmbientModule*/);
                    }
                }
            }
        }
    }

    // ast/utilities.go:1859 — IsAnyImportOrReExport / IsImportNode (inlined predicates).
    private static bool IsAnyImportOrReExport(Node node) => IsImportNode(node) || NodeTests.IsExportDeclaration(node);
    private static bool IsImportNode(Node node) =>
        node.Kind is SyntaxKind.ImportDeclaration or SyntaxKind.ImportEqualsDeclaration or SyntaxKind.JSImportDeclaration;

    // ast/utilities.go:1884 — getExternalModuleName. The ImportType case (getImportTypeNodeLiteral) is
    // deferred; it is not reached from collectModuleReferences (which only passes import/export nodes).
    private static Node? GetExternalModuleName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportDeclaration:
                return node.ModuleSpecifier();
            case SyntaxKind.ImportEqualsDeclaration:
                if (node.AsImportEqualsDeclaration().ModuleReference!.Kind == SyntaxKind.ExternalModuleReference)
                {
                    return node.AsImportEqualsDeclaration().ModuleReference!.Expression();
                }
                return null;
            case SyntaxKind.ImportType:
                throw new NotImplementedException("getExternalModuleName(ImportType)"); // deferred getImportTypeNodeLiteral
            case SyntaxKind.CallExpression:
                return CoreUtil.FirstOrNil(node.Arguments());
            case SyntaxKind.ModuleDeclaration:
                if (NodeTests.IsStringLiteral(node.AsModuleDeclaration().Name()!))
                {
                    return node.AsModuleDeclaration().Name();
                }
                return null;
        }
        throw new InvalidOperationException("Unhandled case in getExternalModuleName");
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
