// Ported from tsgo internal/transformers/moduletransforms/externalmoduleinfo.go.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Binder;
using Ts.Collections;
using Ts.Core;

namespace Ts.Transformers.ModuleTransforms;

internal sealed class ExternalModuleInfo
{
    internal readonly List<Node> ExternalImports = [];
    internal readonly MultiMap<string, Node> ExportSpecifiers = new();
    internal readonly MultiMap<Node, Node> ExportedBindings = new();
    internal readonly List<Node> ExportedNames = [];
    internal readonly OrderedSet<Node> ExportedFunctions = new();
    internal Node? ExportEquals;
    internal bool HasExportStarsToExportValues;
}

internal sealed class ExternalModuleInfoCollector
{
    private readonly SourceFile sourceFile;
    private readonly CompilerOptions compilerOptions;
    private readonly global::Ts.Printer.EmitContext emitContext;
    private readonly IReferenceResolver resolver;
    private readonly Set<string> uniqueExports = new();
    private bool hasExportDefault;
    private readonly ExternalModuleInfo output = new();

    private ExternalModuleInfoCollector(
        SourceFile sourceFile,
        CompilerOptions compilerOptions,
        global::Ts.Printer.EmitContext emitContext,
        IReferenceResolver resolver)
    {
        this.sourceFile = sourceFile;
        this.compilerOptions = compilerOptions;
        this.emitContext = emitContext;
        this.resolver = resolver;
    }

    internal static ExternalModuleInfo CollectExternalModuleInfo(
        SourceFile sourceFile,
        CompilerOptions compilerOptions,
        global::Ts.Printer.EmitContext emitContext,
        IReferenceResolver resolver) =>
        new ExternalModuleInfoCollector(sourceFile, compilerOptions, emitContext, resolver).Collect();

    private ExternalModuleInfo Collect()
    {
        var hasImportStar = false;
        var hasImportDefault = false;
        foreach (var node in sourceFile.Statements!.Nodes)
        {
            // Look through NotEmittedStatement to find elided export= declarations.
            if (global::Ts.Ast.NodeTests.IsNotEmittedStatement(node))
            {
                var original = emitContext.MostOriginal(node);
                if (original is not null && global::Ts.Ast.NodeTests.IsExportAssignment(original))
                {
                    var exportAssignment = original.AsExportAssignment();
                    if (exportAssignment.IsExportEquals && output.ExportEquals is null)
                    {
                        output.ExportEquals = exportAssignment.AsNode();
                    }
                }
                continue;
            }

            switch (node.Kind)
            {
                case SyntaxKind.ImportDeclaration:
                    var importDeclaration = node.AsImportDeclaration();
                    AddExternalImport(node);
                    if (!hasImportStar && GetImportNeedsImportStarHelper(importDeclaration))
                    {
                        hasImportStar = true;
                    }
                    if (!hasImportDefault && GetImportNeedsImportDefaultHelper(importDeclaration))
                    {
                        hasImportDefault = true;
                    }
                    break;

                case SyntaxKind.ImportEqualsDeclaration:
                    if (global::Ts.Ast.NodeTests.IsExternalModuleReference(node.AsImportEqualsDeclaration().ModuleReference!))
                    {
                        AddExternalImport(node);
                    }
                    break;

                case SyntaxKind.ExportDeclaration:
                    var exportDeclaration = node.AsExportDeclaration();
                    if (exportDeclaration.ModuleSpecifier is not null)
                    {
                        AddExternalImport(node);
                        if (exportDeclaration.ExportClause is null)
                        {
                            output.HasExportStarsToExportValues = true;
                        }
                        else if (global::Ts.Ast.NodeTests.IsNamedExports(exportDeclaration.ExportClause))
                        {
                            AddExportedNamesForExportDeclaration(exportDeclaration);
                            if (!hasImportDefault)
                            {
                                hasImportDefault = ContainsDefaultReference(exportDeclaration.ExportClause);
                            }
                        }
                        else
                        {
                            var name = exportDeclaration.ExportClause.AsNamespaceExport().Name()!;
                            if (AddUniqueExport(name.Text()))
                            {
                                AddExportedBinding(node, name);
                                AddExportedName(name);
                            }
                            hasImportStar = true;
                        }
                    }
                    else
                    {
                        AddExportedNamesForExportDeclaration(exportDeclaration);
                    }
                    break;

                case SyntaxKind.ExportAssignment:
                    var assignment = node.AsExportAssignment();
                    if (assignment.IsExportEquals && output.ExportEquals is null)
                    {
                        output.ExportEquals = assignment.AsNode();
                    }
                    break;

                case SyntaxKind.VariableStatement:
                    if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export))
                    {
                        foreach (var declaration in node.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
                        {
                            CollectExportedVariableInfo(declaration);
                        }
                    }
                    break;

                case SyntaxKind.FunctionDeclaration:
                    if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export))
                    {
                        AddExportedFunctionDeclaration(
                            node.AsFunctionDeclaration(),
                            null,
                            global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default));
                    }
                    break;

                case SyntaxKind.ClassDeclaration:
                    var classDeclaration = node.AsClassDeclaration();
                    if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export))
                    {
                        if (global::Ts.Ast.AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default))
                        {
                            if (!hasExportDefault)
                            {
                                var name = classDeclaration.Name() ?? emitContext.Factory.NewGeneratedNameForNode(node);
                                AddExportedBinding(node, name);
                                hasExportDefault = true;
                            }
                        }
                        else if (classDeclaration.Name() is { } name && AddUniqueExport(name.Text()))
                        {
                            AddExportedBinding(node, name);
                            AddExportedName(name);
                        }
                    }
                    break;
            }
        }
        _ = compilerOptions;
        return output;
    }

    private bool AddUniqueExport(string name)
    {
        if (!uniqueExports.Has(name))
        {
            uniqueExports.Add(name);
            return true;
        }
        return false;
    }

    private void AddExportedBinding(Declaration declaration, ModuleExportName name) =>
        output.ExportedBindings.Add(emitContext.MostOriginal(declaration)!, name);

    private void AddExternalImport(Node node) => output.ExternalImports.Add(node);

    private void AddExportedName(ModuleExportName name) => output.ExportedNames.Add(name);

    private void AddExportedNamesForExportDeclaration(ExportDeclaration node)
    {
        foreach (var specifier in node.ExportClause!.Elements() ?? [])
        {
            var specifierNameText = specifier.Name()!.Text();
            if (!AddUniqueExport(specifierNameText))
            {
                continue;
            }
            var name = specifier.PropertyNameOrName()!;
            if (name.Kind != SyntaxKind.StringLiteral)
            {
                if (node.ModuleSpecifier is null)
                {
                    output.ExportSpecifiers.Add(name.Text(), specifier.AsExportSpecifier().AsNode());
                }
                var declaration = resolver.GetReferencedImportDeclaration(emitContext.MostOriginal(name)!) ??
                    resolver.GetReferencedValueDeclaration(emitContext.MostOriginal(name)!);
                if (declaration is not null)
                {
                    if (declaration.Kind == SyntaxKind.FunctionDeclaration)
                    {
                        uniqueExports.Delete(specifierNameText);
                        AddExportedFunctionDeclaration(
                            declaration.AsFunctionDeclaration(),
                            specifier.Name(),
                            global::Ts.Ast.AstUtilities.ModuleExportNameIsDefault(specifier.Name()!));
                        continue;
                    }
                    AddExportedBinding(declaration, specifier.Name()!);
                }
            }
            AddExportedName(specifier.Name()!);
        }
    }

    private void AddExportedFunctionDeclaration(FunctionDeclaration node, ModuleExportName? name, bool isDefault)
    {
        output.ExportedFunctions.Add(emitContext.MostOriginal(node.AsNode())!);
        if (isDefault)
        {
            if (!hasExportDefault)
            {
                name ??= emitContext.Factory.NewGeneratedNameForNode(node.AsNode());
                AddExportedBinding(node.AsNode(), name!);
                hasExportDefault = true;
            }
        }
        else
        {
            name ??= node.Name();
            if (AddUniqueExport(name!.Text()))
            {
                AddExportedBinding(node.AsNode(), name!);
            }
        }
    }

    private void CollectExportedVariableInfo(Node declaration)
    {
        if (global::Ts.Ast.AstUtilities.IsBindingPattern(declaration.Name()))
        {
            foreach (var element in declaration.Name()!.Elements() ?? [])
            {
                if (element.AsBindingElement().Name() is not null)
                {
                    CollectExportedVariableInfo(element);
                }
            }
        }
        else if (!emitContext.HasAutoGenerateInfo(declaration.Name()))
        {
            var text = declaration.Name()!.Text();
            if (AddUniqueExport(text))
            {
                AddExportedName(declaration.Name()!);
                if (Transformers.IsLocalName(emitContext, declaration.Name()!))
                {
                    AddExportedBinding(declaration, declaration.Name()!);
                }
            }
        }
    }

    private const string ExternalHelpersModuleNameText = "tslib";

    internal static Node? CreateExternalHelpersImportDeclarationIfNeeded(
        global::Ts.Printer.EmitContext emitContext,
        SourceFile sourceFile,
        CompilerOptions compilerOptions,
        ModuleKind fileModuleKind,
        bool hasExportStarsToExportValues,
        bool hasImportStar,
        bool hasImportDefault)
    {
        if (compilerOptions.ImportHelpers.IsTrue() && TransformerAstAdapters.IsEffectiveExternalModule(sourceFile, compilerOptions))
        {
            var moduleKind = compilerOptions.GetEmitModuleKind();
            var helpers = GetImportedHelpers(emitContext, sourceFile);
            if (fileModuleKind == ModuleKind.CommonJS || fileModuleKind == ModuleKind.None && moduleKind == ModuleKind.CommonJS)
            {
                var externalHelpersModuleName = GetOrCreateExternalHelpersModuleNameIfNeeded(
                    emitContext,
                    sourceFile,
                    helpers,
                    hasExportStarsToExportValues,
                    hasImportStar || hasImportDefault,
                    fileModuleKind);
                if (externalHelpersModuleName is not null)
                {
                    var declaration = emitContext.Factory.NewImportEqualsDeclaration(
                        null,
                        false,
                        externalHelpersModuleName,
                        emitContext.Factory.NewExternalModuleReference(
                            emitContext.Factory.NewStringLiteral(ExternalHelpersModuleNameText, TokenFlags.None)));
                    emitContext.AddEmitFlags(declaration, global::Ts.Printer.EmitFlags.CustomPrologue);
                    return declaration;
                }
            }
            else
            {
                List<string> helperNames = [];
                foreach (var helper in helpers)
                {
                    if (helper.ImportName.Length > 0 && !helperNames.Exists(name => string.Equals(name, helper.ImportName, StringComparison.Ordinal)))
                    {
                        helperNames.Add(helper.ImportName);
                    }
                }
                if (helperNames.Count > 0)
                {
                    helperNames.Sort(global::Ts.StringUtil.StringUtil.CompareStringsCaseSensitive);
                    var importSpecifiers = new Node[helperNames.Count];
                    for (var i = 0; i < helperNames.Count; i++)
                    {
                        var name = helperNames[i];
                        importSpecifiers[i] = TransformerAstAdapters.IsFileLevelUniqueName(sourceFile, name)
                            ? emitContext.Factory.NewImportSpecifier(false, null, emitContext.Factory.NewIdentifier(name))
                            : emitContext.Factory.NewImportSpecifier(
                                false,
                                emitContext.Factory.NewIdentifier(name),
                                emitContext.Factory.NewUnscopedHelperName(name));
                    }
                    var namedBindings = emitContext.Factory.NewNamedImports(emitContext.Factory.NewNodeList(importSpecifiers));
                    emitContext.AddEmitFlags(emitContext.MostOriginal(sourceFile.AsNode())!, global::Ts.Printer.EmitFlags.ExternalHelpers);
                    var declaration = emitContext.Factory.NewImportDeclaration(
                        null,
                        emitContext.Factory.NewImportClause(SyntaxKind.Unknown, null, namedBindings),
                        emitContext.Factory.NewStringLiteral(ExternalHelpersModuleNameText, TokenFlags.None),
                        null);
                    emitContext.AddEmitFlags(declaration, global::Ts.Printer.EmitFlags.CustomPrologue);
                    return declaration;
                }
            }
        }
        return null;
    }

    private static global::Ts.Printer.EmitHelper[] GetImportedHelpers(global::Ts.Printer.EmitContext emitContext, SourceFile sourceFile)
    {
        List<global::Ts.Printer.EmitHelper> helpers = [];
        foreach (var helper in emitContext.GetEmitHelpers(sourceFile.AsNode()) ?? [])
        {
            if (!helper.Scoped)
            {
                helpers.Add(helper);
            }
        }
        return helpers.ToArray();
    }

    private static IdentifierNode? GetOrCreateExternalHelpersModuleNameIfNeeded(
        global::Ts.Printer.EmitContext emitContext,
        SourceFile node,
        global::Ts.Printer.EmitHelper[] helpers,
        bool hasExportStarsToExportValues,
        bool hasImportStarOrImportDefault,
        ModuleKind fileModuleKind)
    {
        var externalHelpersModuleName = emitContext.GetExternalHelpersModuleName(node);
        if (externalHelpersModuleName is not null)
        {
            return externalHelpersModuleName;
        }
        var create = helpers.Length > 0 ||
            (hasExportStarsToExportValues || hasImportStarOrImportDefault) && fileModuleKind < ModuleKind.System;
        if (create)
        {
            externalHelpersModuleName = emitContext.Factory.NewUniqueName(ExternalHelpersModuleNameText);
            emitContext.SetExternalHelpersModuleName(node, externalHelpersModuleName);
        }
        return externalHelpersModuleName;
    }

    private static bool IsNamedDefaultReference(Node node) =>
        global::Ts.Ast.AstUtilities.ModuleExportNameIsDefault(node.PropertyNameOrName()!);

    private static bool ContainsDefaultReference(Node? node) =>
        node is not null &&
        (global::Ts.Ast.NodeTests.IsNamedImports(node) || global::Ts.Ast.NodeTests.IsNamedExports(node)) &&
        global::Ts.Core.CoreUtil.Some(node.Elements(), IsNamedDefaultReference);

    internal static bool GetExportNeedsImportStarHelper(ExportDeclaration node) =>
        TransformerAstAdapters.GetNamespaceDeclarationNode(node.AsNode()) is not null;

    internal static bool GetImportNeedsImportStarHelper(ImportDeclaration node)
    {
        if (TransformerAstAdapters.GetNamespaceDeclarationNode(node.AsNode()) is not null)
        {
            return true;
        }
        var bindings = node.ImportClause?.AsImportClause().NamedBindings;
        if (bindings is null || !global::Ts.Ast.NodeTests.IsNamedImports(bindings))
        {
            return false;
        }
        var namedImports = bindings.AsNamedImports();
        var defaultReferenceCount = 0;
        foreach (var binding in namedImports.Elements!.Nodes)
        {
            if (IsNamedDefaultReference(binding))
            {
                defaultReferenceCount++;
            }
        }
        return (defaultReferenceCount > 0 && defaultReferenceCount != namedImports.Elements.Nodes.Length) ||
            (namedImports.Elements.Nodes.Length - defaultReferenceCount != 0 && TransformerAstAdapters.IsDefaultImport(node.AsNode()));
    }

    internal static bool GetImportNeedsImportDefaultHelper(ImportDeclaration node) =>
        !GetImportNeedsImportStarHelper(node) &&
        (TransformerAstAdapters.IsDefaultImport(node.AsNode()) ||
        (node.ImportClause is not null && global::Ts.Ast.NodeTests.IsNamedImports(node.ImportClause.AsImportClause().NamedBindings!) &&
        ContainsDefaultReference(node.ImportClause.AsImportClause().NamedBindings)));
}
