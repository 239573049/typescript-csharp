// Ported from tsgo internal/transformers/tstransforms/importelision.go.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.TsTransforms;

public sealed class ImportElisionTransformer : global::Ts.Transformers.Transformer
{
    private readonly CompilerOptions compilerOptions;
    private readonly global::Ts.Printer.EmitResolver? emitResolver;
    private SourceFile? currentSourceFile;

    private ImportElisionTransformer(global::Ts.Transformers.TransformOptions options)
    {
        compilerOptions = options.CompilerOptions ?? new CompilerOptions();
        if (compilerOptions.VerbatimModuleSyntax == Tristate.True)
        {
            throw new InvalidOperationException(
                "ImportElisionTransformer should not be used with VerbatimModuleSyntax");
        }
        emitResolver = options.EmitResolver;
        NewTransformer(Visit, options.Context);
    }

    public static global::Ts.Transformers.Transformer NewImportElisionTransformer(
        global::Ts.Transformers.TransformOptions options) => new ImportElisionTransformer(options);

    private Node? Visit(Node node)
    {
        if (global::Ts.Ast.NodeTests.IsSourceFile(node) && emitResolver is not null)
        {
            emitResolver.MarkLinkedReferencesRecursively(
                EmitContext().MostOriginal(node)!.AsSourceFile());
        }

        switch (node.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
                if (global::Ts.Transformers.TransformerAstAdapters.IsExternalModuleImportEqualsDeclaration(node))
                {
                    if (!ShouldEmitAliasDeclaration(node))
                    {
                        return null;
                    }
                }
                else if (!ShouldEmitImportEqualsDeclaration(node.AsImportEqualsDeclaration()))
                {
                    return null;
                }
                return Visitor().VisitEachChild(node);

            case SyntaxKind.ImportDeclaration:
            {
                var declaration = node.AsImportDeclaration();
                if (declaration.ImportClause is not null)
                {
                    var importClause = Visitor().VisitNode(declaration.ImportClause);
                    if (importClause is null)
                    {
                        return null;
                    }
                    return Factory().AsNodeFactory().UpdateImportDeclaration(
                        declaration,
                        declaration.Modifiers(),
                        importClause,
                        declaration.ModuleSpecifier,
                        Visitor().VisitNode(declaration.Attributes));
                }
                return Visitor().VisitEachChild(node);
            }

            case SyntaxKind.ImportClause:
            {
                var declaration = node.AsImportClause();
                var name = ShouldEmitAliasDeclaration(node) ? declaration.Name() : null;
                var namedBindings = Visitor().VisitNode(declaration.NamedBindings);
                if (name is null && namedBindings is null)
                {
                    return null;
                }
                return Factory().AsNodeFactory().UpdateImportClause(
                    declaration, declaration.PhaseModifier, name, namedBindings);
            }

            case SyntaxKind.NamespaceImport:
                return ShouldEmitAliasDeclaration(node) ? node : null;

            case SyntaxKind.NamedImports:
            {
                var declaration = node.AsNamedImports();
                var elements = Visitor().VisitNodes(declaration.Elements);
                return (elements?.Nodes.Length ?? 0) == 0
                    ? null
                    : Factory().AsNodeFactory().UpdateNamedImports(declaration, elements);
            }

            case SyntaxKind.ImportSpecifier:
                return ShouldEmitAliasDeclaration(node) ? node : null;

            case SyntaxKind.ExportAssignment:
                return compilerOptions.VerbatimModuleSyntax != Tristate.True &&
                    !IsValueAliasDeclaration(node)
                    ? null
                    : Visitor().VisitEachChild(node);

            case SyntaxKind.ExportDeclaration:
            {
                var declaration = node.AsExportDeclaration();
                Node? exportClause = null;
                if (declaration.ExportClause is not null)
                {
                    exportClause = Visitor().VisitNode(declaration.ExportClause);
                    if (exportClause is null)
                    {
                        return null;
                    }
                }
                return Factory().AsNodeFactory().UpdateExportDeclaration(
                    declaration, null, false, exportClause,
                    Visitor().VisitNode(declaration.ModuleSpecifier),
                    Visitor().VisitNode(declaration.Attributes));
            }

            case SyntaxKind.NamedExports:
            {
                var declaration = node.AsNamedExports();
                var elements = Visitor().VisitNodes(declaration.Elements);
                return (elements?.Nodes.Length ?? 0) == 0
                    ? null
                    : Factory().AsNodeFactory().UpdateNamedExports(declaration, elements);
            }

            case SyntaxKind.ExportSpecifier:
                return IsValueAliasDeclaration(node) ? node : null;

            case SyntaxKind.SourceFile:
            {
                var savedCurrentSourceFile = currentSourceFile;
                currentSourceFile = node.AsSourceFile();
                var result = Visitor().VisitEachChild(node);
                currentSourceFile = savedCurrentSourceFile;
                return result;
            }

            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ModuleBlock:
                return Visitor().VisitEachChild(node);

            default:
                return node;
        }
    }

    private bool ShouldEmitAliasDeclaration(Node node) =>
        global::Ts.Ast.AstUtilities.IsInJSFile(node) || IsReferencedAliasDeclaration(node);

    private bool ShouldEmitImportEqualsDeclaration(ImportEqualsDeclaration node)
    {
        return ShouldEmitAliasDeclaration(node.AsNode()) ||
            (currentSourceFile is not null &&
             !global::Ts.Ast.AstUtilities.IsExternalModule(currentSourceFile) &&
             IsTopLevelValueImportEqualsWithEntityName(node.AsNode()));
    }

    private bool IsReferencedAliasDeclaration(Node node)
    {
        node = EmitContext().ParseNode(node)!;
        // PORT-NOTE: Preserving is the only safe behavior when the checker-backed resolver is
        // absent. Production compiler assembly supplies EmitResolver before enabling elision.
        return node is null || emitResolver is null || emitResolver.IsReferencedAliasDeclaration(node);
    }

    private bool IsValueAliasDeclaration(Node node)
    {
        node = EmitContext().ParseNode(node)!;
        return node is null || emitResolver is null || emitResolver.IsValueAliasDeclaration(node);
    }

    private bool IsTopLevelValueImportEqualsWithEntityName(Node node)
    {
        node = EmitContext().ParseNode(node)!;
        return node is not null && emitResolver is not null &&
            emitResolver.IsTopLevelValueImportEqualsWithEntityName(node);
    }
}
