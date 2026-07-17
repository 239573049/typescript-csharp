// Ported from tsgo internal/checker/checker.go (section: symbol / alias / import resolution,
// lines ~14358-16405). plan/03 §4.2 #10 (SymbolResolution cluster).
//
// This wave ports the alias/export/entity-name resolution spine faithfully. The heavy
// module-resolution machinery (resolveExternalModule and its diagnostic helpers), which drags
// in a large program/module/tspath surface, is left as `NotImplementedException` stubs in this
// file's cross-cluster region and reported as remaining. Real ports call those stubs freely
// (build stays green); do not "fix" the stubs here.
//
// §1.5 shadowing: core free funcs via CoreUtil.X; diagnostic messages fully qualified
// (global::Ts.Diagnostics.Diagnostics.X); scanner via global::Ts.Scanner.Scanner.X. ast Is*
// predicates + AstUtilities helpers imported `using static`.

using Ts.Ast;
using Ts.Core;
using static Ts.Ast.NodeTests;
using static Ts.Ast.AstUtilities;

namespace Ts.Checker;

// utilities.go:1753 — DiagnosticDetails (message + args for a module-resolution diagnostic chain entry;
// shared between the checker and the incremental builder's diagnostic repopulation).
public readonly record struct DiagnosticDetails(global::Ts.Diagnostics.Message Message, object?[]? Args);

public partial class Checker
{
    // checker.go:14358
    internal Symbol? ResolveSymbolEx(Symbol? symbol, bool dontResolveAlias)
    {
        if (!dontResolveAlias && IsNonLocalAlias(symbol, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace))
        {
            return ResolveAliasCore(symbol!);
        }
        return symbol;
    }

    // checker.go:14365
    internal Symbol? GetTargetOfImportEqualsDeclaration(Node node)
    {
        // Node is ImportEqualsDeclaration | VariableDeclaration
        if (IsVariableDeclaration(node) || node.AsImportEqualsDeclaration().ModuleReference!.Kind == SyntaxKind.ExternalModuleReference)
        {
            var moduleReference = GetExternalModuleRequireArgument(node);
            if (moduleReference == null)
            {
                moduleReference = GetExternalModuleImportEqualsDeclarationExpression(node);
            }
            var immediate = ResolveExternalModuleName(node, moduleReference, false /*ignoreErrors*/);
            var resolved = ResolveExternalModuleSymbol(immediate, true /*dontResolveAlias*/);
            if (resolved != null && ModuleKind.Node20 <= moduleKind && moduleKind <= ModuleKind.NodeNext)
            {
                var moduleExports = GetExportOfModule(resolved, InternalSymbolName.ModuleExports, node, true /*dontResolveAlias*/);
                if (moduleExports != null)
                {
                    return moduleExports;
                }
            }
            MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
            return resolved;
        }
        var resolved2 = GetSymbolOfPartOfRightHandSideOfImportEquals(node.AsImportEqualsDeclaration().ModuleReference!);
        CheckAndReportErrorForResolvingImportAliasToTypeOnlySymbol(node, resolved2);
        return resolved2;
    }

    // checker.go:14388
    internal Type ResolveExternalModuleTypeByLiteral(Node name)
    {
        var moduleSym = ResolveExternalModuleName(name, name, false /*ignoreErrors*/);
        if (moduleSym != null)
        {
            var resolvedModuleSymbol = ResolveExternalModuleSymbol(moduleSym, false /*dontResolveAlias*/);
            if (resolvedModuleSymbol != null)
            {
                return GetTypeOfSymbol(resolvedModuleSymbol);
            }
        }
        return anyType!;
    }

    // checker.go:14399 — This function is only for imports with entity names
    internal Symbol? GetSymbolOfPartOfRightHandSideOfImportEquals(Node entityNameIn)
    {
        var entityName = entityNameIn;
        //     import a = |b|; // Namespace
        //     import a = |b.c|; // Value, type, namespace
        //     import a = |b.c|.d; // Namespace
        if (entityName.Kind == SyntaxKind.Identifier && IsRightSideOfQualifiedNameOrPropertyAccess(entityName))
        {
            entityName = entityName.Parent!; // QualifiedName
        }
        // Check for case 1 and 3 in the above example
        if (entityName.Kind == SyntaxKind.Identifier || entityName.Parent!.Kind == SyntaxKind.QualifiedName)
        {
            return ResolveEntityName(entityName, SymbolFlags.Namespace, false /*ignoreErrors*/, true /*dontResolveAlias*/, null /*location*/);
        }
        // Case 2 in above example
        Debug.Assert(entityName.Parent.Kind == SyntaxKind.ImportEqualsDeclaration);
        return ResolveEntityName(entityName, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace, false /*ignoreErrors*/, true /*dontResolveAlias*/, null /*location*/);
    }

    // checker.go:14454
    internal Symbol? GetTargetOfImportClause(Node node)
    {
        var moduleSymbol = ResolveExternalModuleName(node, GetModuleSpecifierFromNode(node.Parent!), false /*ignoreErrors*/);
        if (moduleSymbol != null)
        {
            return GetTargetOfModuleDefault(moduleSymbol, node, true /*dontResolveAlias*/);
        }
        return null;
    }

    // checker.go:14554
    internal Symbol? GetTargetOfNamespaceImport(Node node)
    {
        var moduleSpecifier = GetModuleSpecifierForImportOrExport(node);
        var immediate = ResolveExternalModuleName(node, moduleSpecifier, false /*ignoreErrors*/);
        var resolved = ResolveESModuleSymbol(immediate, node, moduleSpecifier);
        MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
        return resolved;
    }

    // checker.go:14562
    internal Symbol? GetTargetOfNamespaceExport(Node node)
    {
        var moduleSpecifier = GetModuleSpecifierForImportOrExport(node);
        if (moduleSpecifier != null)
        {
            var immediate = ResolveExternalModuleName(node, moduleSpecifier, false /*ignoreErrors*/);
            var resolved = ResolveESModuleSymbol(immediate, node, moduleSpecifier);
            MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
            return resolved;
        }
        return null;
    }

    // checker.go:14573
    internal Symbol? GetTargetOfImportSpecifier(Node node)
    {
        var name = node.PropertyNameOrName();
        if (IsImportSpecifier(node) && ModuleExportNameIsDefault(name!))
        {
            var specifier = GetModuleSpecifierForImportOrExport(node);
            if (specifier != null)
            {
                var moduleSymbol2 = ResolveExternalModuleName(node, specifier, false /*ignoreErrors*/);
                if (moduleSymbol2 != null)
                {
                    return GetTargetOfModuleDefault(moduleSymbol2, node, true /*dontResolveAlias*/);
                }
            }
        }
        var root = node.Parent!.Parent!.Parent!; // ImportDeclaration
        if (IsBindingElement(node))
        {
            root = GetRootDeclaration(node);
        }
        var resolved = GetExternalModuleMember(root, node, true /*dontResolveAlias*/);
        MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
        return resolved;
    }

    // checker.go:14664
    internal Symbol? GetPropertyOfVariable(Symbol symbol, string name)
    {
        if ((symbol.Flags & SymbolFlags.Variable) != 0)
        {
            var typeAnnotation = symbol.ValueDeclaration!.Type();
            if (typeAnnotation != null)
            {
                return ResolveSymbol(GetPropertyOfType(GetTypeFromTypeNode(typeAnnotation), name));
            }
        }
        return null;
    }

    // checker.go:14692 — synthetic symbol combining value side of one symbol with type/namespace of another.
    internal Symbol CombineValueAndTypeSymbols(Symbol valueSymbol, Symbol typeSymbol)
    {
        if (valueSymbol == unknownSymbol && typeSymbol == unknownSymbol)
        {
            return unknownSymbol;
        }
        if ((typeSymbol.Flags & SymbolFlags.Value) != 0)
        {
            return typeSymbol;
        }
        if ((valueSymbol.Flags & (SymbolFlags.Type | SymbolFlags.Namespace)) != 0)
        {
            return valueSymbol;
        }
        var result = NewSymbol(valueSymbol.Flags | typeSymbol.Flags, valueSymbol.Name);
        Debug.Assert(valueSymbol.Declarations.Len() > 0 || typeSymbol.Declarations.Len() > 0);
        result.Declarations = SlicesCompact(SlicesConcat(valueSymbol.Declarations, typeSymbol.Declarations));
        result.Parent = valueSymbol.Parent;
        if (result.Parent == null)
        {
            result.Parent = typeSymbol.Parent;
        }
        result.ValueDeclaration = valueSymbol.ValueDeclaration;
        result.Members = typeSymbol.Members is null ? null : new SymbolTable(typeSymbol.Members);
        result.Exports = valueSymbol.Exports is null ? null : new SymbolTable(valueSymbol.Exports);
        return result;
    }

    // checker.go:14715
    internal Symbol? GetExportOfModule(Symbol symbol, string nameText, Node? specifier, bool dontResolveAlias)
    {
        if ((symbol.Flags & SymbolFlags.Module) != 0)
        {
            var exportSymbol = GetExportsOfSymbol(symbol)?.GetValueOrDefault(nameText);
            var resolved = ResolveSymbolEx(exportSymbol, dontResolveAlias);
            Node? exportStarDeclaration = null;
            var typeOnlyMap = moduleSymbolLinks.Get(symbol).typeOnlyExportStarMap;
            if (typeOnlyMap != null)
            {
                exportStarDeclaration = typeOnlyMap.GetValueOrDefault(nameText);
            }
            MarkSymbolOfAliasDeclarationIfTypeOnly(specifier, exportStarDeclaration);
            return resolved;
        }
        return null;
    }

    // checker.go:14877
    internal Symbol? GetTargetOfExportSpecifier(Node node, SymbolFlags meaning, bool dontResolveAlias)
    {
        var name = node.PropertyNameOrName();
        if (ModuleExportNameIsDefault(name!))
        {
            var specifier = GetModuleSpecifierForImportOrExport(node);
            if (specifier != null)
            {
                var moduleSymbol = ResolveExternalModuleName(node, specifier, false /*ignoreErrors*/);
                if (moduleSymbol != null)
                {
                    return GetTargetOfModuleDefault(moduleSymbol, node, dontResolveAlias);
                }
            }
        }
        var exportDeclaration = node.Parent!.Parent!;
        Symbol? resolved;
        if (exportDeclaration.ModuleSpecifier() != null)
        {
            resolved = GetExternalModuleMember(exportDeclaration, node, dontResolveAlias);
        }
        else if (IsStringLiteral(name!))
        {
            resolved = null;
        }
        else
        {
            resolved = ResolveEntityName(name!, meaning, false /*ignoreErrors*/, dontResolveAlias, null /*location*/);
        }
        MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
        return resolved;
    }

    // checker.go:14902
    internal Symbol? GetTargetOfExportAssignment(Node node)
    {
        var resolved = GetTargetOfAliasLikeExpression(node.Expression()!);
        MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
        return resolved;
    }

    // checker.go:14908
    internal Symbol? GetTargetOfBinaryExpression(Node node)
    {
        var resolved = GetTargetOfAliasLikeExpression(node.AsBinaryExpression().Right!);
        MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
        return resolved;
    }

    // checker.go:14914
    internal Symbol? GetTargetOfAliasLikeExpression(Node expression)
    {
        if (IsClassExpression(expression))
        {
            return CheckExpressionCached(expression).symbol;
        }
        if (!IsEntityName(expression) && !IsEntityNameExpression(expression))
        {
            return null;
        }
        var aliasLike = ResolveEntityName(expression, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace, true /*ignoreErrors*/, true /*dontResolveAlias*/, null /*location*/);
        if (aliasLike != null)
        {
            return aliasLike;
        }
        CheckExpressionCached(expression);
        return GetResolvedSymbolOrNil(expression);
    }

    // checker.go:14929
    internal Symbol? GetTargetOfNamespaceExportDeclaration(Node node)
    {
        if (CanHaveSymbol(node.Parent!))
        {
            var resolved = ResolveExternalModuleSymbol(node.Parent!.Symbol(), true /*dontResolveAlias*/);
            MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
            return resolved;
        }
        return null;
    }

    // checker.go:14938
    internal Symbol? GetTargetOfAccessExpression(Node node)
    {
        if (IsBinaryExpression(node.Parent!))
        {
            var expr = node.Parent!.AsBinaryExpression();
            if (expr.Left == node && expr.OperatorToken!.Kind == SyntaxKind.EqualsToken)
            {
                return GetTargetOfAliasLikeExpression(expr.Right!);
            }
        }
        return null;
    }

    // checker.go:14948
    internal Node? GetModuleSpecifierForImportOrExport(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportClause:
                return GetModuleSpecifierFromNode(node.Parent!);
            case SyntaxKind.ImportEqualsDeclaration:
                if (IsExternalModuleReference(node.AsImportEqualsDeclaration().ModuleReference!))
                {
                    return node.AsImportEqualsDeclaration().ModuleReference!.Expression();
                }
                return null;
            case SyntaxKind.NamespaceImport:
                return GetModuleSpecifierFromNode(node.Parent!.Parent!);
            case SyntaxKind.ImportSpecifier:
                return GetModuleSpecifierFromNode(node.Parent!.Parent!.Parent!);
            case SyntaxKind.NamespaceExport:
                return GetModuleSpecifierFromNode(node.Parent!);
            case SyntaxKind.ExportSpecifier:
                return GetModuleSpecifierFromNode(node.Parent!.Parent!);
        }
        throw new InvalidOperationException("Unhandled case in getModuleSpecifierForImportOrExport");
    }

    // checker.go:14970
    private static Node? GetModuleSpecifierFromNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                return node.ModuleSpecifier();
            case SyntaxKind.ExportDeclaration:
                return node.ModuleSpecifier();
        }
        throw new InvalidOperationException("Unhandled case in getModuleSpecifierFromNode");
    }

    // checker.go:15001 — Marks a symbol as type-only if its declaration is syntactically type-only.
    internal bool MarkSymbolOfAliasDeclarationIfTypeOnly(Node? aliasDeclaration, Node? exportStarDeclaration)
    {
        if (aliasDeclaration == null || !IsDeclarationNode(aliasDeclaration))
        {
            return false;
        }
        // If the declaration itself is type-only, mark it and return.
        var sourceSymbol = GetSymbolOfDeclaration(aliasDeclaration);
        var links = aliasSymbolLinks.Get(sourceSymbol!);
        if (links.typeOnlyDeclaration == null && IsTypeOnlyImportOrExportDeclaration(aliasDeclaration))
        {
            links.typeOnlyDeclaration = aliasDeclaration;
            return true;
        }
        if (links.typeOnlyDeclaration == null && exportStarDeclaration != null)
        {
            links.typeOnlyDeclaration = exportStarDeclaration;
            return true;
        }
        return links.typeOnlyDeclaration != null;
    }

    // checker.go:15019
    internal Symbol? ResolveExternalModuleName(Node location, Node? moduleReferenceExpression, bool ignoreErrors)
    {
        var errorMessage = GetCannotResolveModuleNameErrorForSpecificModule(moduleReferenceExpression);
        if (errorMessage == null)
        {
            errorMessage = global::Ts.Diagnostics.Diagnostics.Cannot_find_module_0_or_its_corresponding_type_declarations;
        }
        ignoreErrors = ignoreErrors || compilerOptions.NoCheck.IsTrue();
        return ResolveExternalModuleNameWorker(location, moduleReferenceExpression, ignoreErrors ? null : errorMessage, ignoreErrors, false /*isForAugmentation*/);
    }

    // checker.go:15028
    private global::Ts.Diagnostics.Message? GetCannotResolveModuleNameErrorForSpecificModule(Node? moduleName)
    {
        if (IsStringLiteral(moduleName!))
        {
            if (global::Ts.Core.Core.NodeCoreModules.Value.Contains(moduleName!.Text()))
            {
                if (compilerOptions.UsesWildcardTypes())
                {
                    return global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode;
                }
                return global::Ts.Diagnostics.Diagnostics.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode_and_then_add_node_to_the_types_field_in_your_tsconfig;
            }
        }
        return null;
    }

    // checker.go:15040
    internal Symbol? ResolveExternalModuleNameWorker(Node location, Node? moduleReferenceExpression, global::Ts.Diagnostics.Message? moduleNotFoundError, bool ignoreErrors, bool isForAugmentation)
    {
        if (IsStringLiteralLike(moduleReferenceExpression!))
        {
            return ResolveExternalModule(location, moduleReferenceExpression!.Text(), moduleNotFoundError, !ignoreErrors ? moduleReferenceExpression : null, isForAugmentation);
        }
        return null;
    }

    // checker.go:15067
    internal Symbol? ResolveExternalModule(Node? location, string moduleReference, global::Ts.Diagnostics.Message? moduleNotFoundError, Node? errorNode, bool isForAugmentation)
    {
        if (errorNode != null && moduleReference.StartsWith("@types/", StringComparison.Ordinal))
        {
            var withoutAtTypePrefix = moduleReference["@types/".Length..];
            Error(errorNode, global::Ts.Diagnostics.Diagnostics.Cannot_import_type_declaration_files_Consider_importing_0_instead_of_1, withoutAtTypePrefix, moduleReference);
        }
        var ambientModule = TryFindAmbientModule(moduleReference, true /*withAugmentations*/);
        if (ambientModule != null)
        {
            return ambientModule;
        }

        var importingSourceFile = GetSourceFileOfNode(location);
        Node? contextSpecifier = null;
        ResolutionMode mode;

        if (IsStringLiteralLike(location!) || location!.Parent != null && IsModuleDeclaration(location.Parent) && location.Parent.Name() == location)
        {
            contextSpecifier = location;
        }
        else if (IsModuleDeclaration(location))
        {
            contextSpecifier = location.Name();
        }
        else if (IsLiteralImportTypeNode(location))
        {
            contextSpecifier = location.AsImportTypeNode().Argument!.AsLiteralTypeNode().Literal;
        }
        else if (IsVariableDeclarationInitializedToBareOrAccessedRequire(location))
        {
            contextSpecifier = GetModuleSpecifierOfBareOrAccessedRequire(location);
        }
        else
        {
            var ancestor = FindAncestor(location, IsImportCall);
            if (ancestor != null)
            {
                contextSpecifier = ancestor.Arguments()![0];
            }

            if (ancestor == null)
            {
                ancestor = FindAncestor(location, IsImportDeclarationOrJSImportDeclaration);
                if (ancestor != null)
                {
                    contextSpecifier = ancestor.ModuleSpecifier();
                }
            }
            if (ancestor == null)
            {
                ancestor = FindAncestor(location, IsExportDeclaration);
                if (ancestor != null)
                {
                    contextSpecifier = ancestor.ModuleSpecifier();
                }
            }
            if (ancestor == null)
            {
                ancestor = FindAncestor(location, IsImportEqualsDeclaration);
                if (ancestor != null)
                {
                    var moduleRefrence = ancestor.AsImportEqualsDeclaration().ModuleReference;
                    if (moduleRefrence!.Kind == SyntaxKind.ExternalModuleReference)
                    {
                        contextSpecifier = moduleRefrence.Expression();
                    }
                }
            }
        }

        if (contextSpecifier != null && IsStringLiteralLike(contextSpecifier))
        {
            mode = program.GetModeForUsageLocation(SourceFileAsHasFileName(importingSourceFile!), contextSpecifier);
        }
        else
        {
            mode = program.GetDefaultResolutionModeForFile(SourceFileAsHasFileName(importingSourceFile!));
        }

        var resolvedModule = program.GetResolvedModule(SourceFileAsHasFileName(importingSourceFile!), moduleReference, mode);

        global::Ts.Diagnostics.Message? resolutionDiagnostic = null;
        if (errorNode != null && global::Ts.Module.ResolvedModuleExtensions.IsResolved(resolvedModule))
        {
            resolutionDiagnostic = global::Ts.Module.Module.GetResolutionDiagnostic(compilerOptions, resolvedModule!, importingSourceFile!);
        }

        SourceFile? sourceFile = null;
        if (global::Ts.Module.ResolvedModuleExtensions.IsResolved(resolvedModule) && (resolutionDiagnostic == null || resolutionDiagnostic == global::Ts.Diagnostics.Diagnostics.Module_0_was_resolved_to_1_but_jsx_is_not_set))
        {
            sourceFile = program.GetSourceFileForResolvedModule(resolvedModule!.ResolvedFileName);
        }

        if (sourceFile != null)
        {
            // If there's a resolutionDiagnostic we need to report it even if a sourceFile is found.
            if (resolutionDiagnostic != null)
            {
                Error(errorNode, resolutionDiagnostic, moduleReference, resolvedModule!.ResolvedFileName);
            }

            if (errorNode != null)
            {
                // checker.go:15142 — resolvedUsingTsExtension / rewriteRelativeImportExtensions error suite.
                if (resolvedModule!.ResolvedUsingTsExtension && global::Ts.TsPath.TsPath.IsDeclarationFileName(moduleReference))
                {
                    if (AstUtilities.FindAncestor(location, AstUtilities.IsEmittableImport) != null)
                    {
                        string tsExtension = global::Ts.TsPath.TsPath.TryExtractTSExtension(moduleReference);
                        if (tsExtension == "")
                        {
                            throw new InvalidOperationException("should be able to extract TS extension from string that passes IsDeclarationFileName");
                        }
                        Error(errorNode, global::Ts.Diagnostics.Diagnostics.A_declaration_file_cannot_be_imported_without_import_type_Did_you_mean_to_import_an_implementation_file_0_instead, GetSuggestedImportSource(moduleReference, tsExtension, mode));
                    }
                }
                else if (resolvedModule.ResolvedUsingTsExtension && !compilerOptions.AllowImportingTsExtensionsFrom(importingSourceFile!.FileName()))
                {
                    if (AstUtilities.FindAncestor(location, AstUtilities.IsEmittableImport) != null)
                    {
                        string tsExtension = global::Ts.TsPath.TsPath.TryExtractTSExtension(moduleReference);
                        if (tsExtension == "")
                        {
                            // Fallback: best-effort extraction using Contains — handles a wildcard pattern
                            // matching a TS extension that's not at the end of the specifier, e.g.
                            // "#/foo.ts.omg" through "#/*.omg": "./src/*".
                            foreach (string ext in global::Ts.TsPath.TsPath.SupportedTSExtensionsFlat)
                            {
                                if (moduleReference.Contains(ext, StringComparison.Ordinal))
                                {
                                    tsExtension = ext;
                                    break;
                                }
                            }
                        }
                        if (tsExtension == "")
                        {
                            throw new InvalidOperationException("should be able to extract TS extension from string when resolvedUsingTsExtension is true");
                        }
                        Error(errorNode, global::Ts.Diagnostics.Diagnostics.An_import_path_can_only_end_with_a_0_extension_when_allowImportingTsExtensions_is_enabled, tsExtension);
                    }
                }
                else if (compilerOptions.RewriteRelativeImportExtensions.IsTrue() &&
                    (location!.Flags & NodeFlags.Ambient) == 0 &&
                    !global::Ts.TsPath.TsPath.IsDeclarationFileName(moduleReference) &&
                    !AstUtilities.IsLiteralImportTypeNode(location) &&
                    !IsPartOfTypeOnlyImportOrExportDeclaration(location))
                {
                    bool shouldRewrite = global::Ts.Core.CoreUtil.ShouldRewriteModuleSpecifier(moduleReference, compilerOptions);
                    if (!resolvedModule.ResolvedUsingTsExtension && shouldRewrite)
                    {
                        string relativeToSourceFile = global::Ts.TsPath.TsPath.GetRelativePathFromFile(
                            global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(importingSourceFile!.FileName(), program.GetCurrentDirectory()),
                            resolvedModule.ResolvedFileName,
                            new global::Ts.TsPath.ComparePathsOptions
                            {
                                UseCaseSensitiveFileNames = program.UseCaseSensitiveFileNames(),
                                CurrentDirectory = program.GetCurrentDirectory(),
                            });
                        Error(errorNode, global::Ts.Diagnostics.Diagnostics.This_relative_import_path_is_unsafe_to_rewrite_because_it_looks_like_a_file_name_but_actually_resolves_to_0, relativeToSourceFile);
                    }
                    else if (resolvedModule.ResolvedUsingTsExtension && !shouldRewrite && program.SourceFileMayBeEmitted(sourceFile, false))
                    {
                        Error(errorNode, global::Ts.Diagnostics.Diagnostics.This_import_uses_a_0_extension_to_resolve_to_an_input_TypeScript_file_but_will_not_be_rewritten_during_emit_because_it_is_not_a_relative_path, global::Ts.TsPath.TsPath.GetAnyExtensionFromPath(moduleReference, null, false));
                    }
                    else if (resolvedModule.ResolvedUsingTsExtension && shouldRewrite)
                    {
                        var redirect = program.GetRedirectForResolution(SourceFileAsHasFileName(sourceFile));
                        if (redirect != null)
                        {
                            string ownRootDir = program.CommonSourceDirectory();
                            string otherRootDir = redirect.CommonSourceDirectory();

                            var compareOptions = new global::Ts.TsPath.ComparePathsOptions
                            {
                                UseCaseSensitiveFileNames = program.UseCaseSensitiveFileNames(),
                                CurrentDirectory = program.GetCurrentDirectory(),
                            };

                            string rootDirPath = global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(ownRootDir, otherRootDir, compareOptions);

                            // Get outDir paths, defaulting to root directories if not specified.
                            string ownOutDir = compilerOptions.OutDir;
                            if (ownOutDir == "")
                            {
                                ownOutDir = ownRootDir;
                            }
                            string otherOutDir = redirect.CompilerOptions()!.OutDir;
                            if (otherOutDir == "")
                            {
                                otherOutDir = otherRootDir;
                            }
                            string outDirPath = global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(ownOutDir, otherOutDir, compareOptions);

                            if (rootDirPath != outDirPath)
                            {
                                Error(errorNode, global::Ts.Diagnostics.Diagnostics.This_import_path_is_unsafe_to_rewrite_because_it_resolves_to_another_project_and_the_relative_path_between_the_projects_output_files_is_not_the_same_as_the_relative_path_between_its_input_files);
                            }
                        }
                    }
                }
            }

            if (sourceFile.Symbol != null)
            {
                if (errorNode != null)
                {
                    if (resolvedModule!.IsExternalLibraryImport && !ResolutionExtensionIsTSOrJson(resolvedModule.Extension))
                    {
                        ErrorOnImplicitAnyModule(false /*isError*/, errorNode, mode, resolvedModule, moduleReference);
                    }
                    if (moduleKind == ModuleKind.Node16 || moduleKind == ModuleKind.Node18)
                    {
                        // checker.go:15243 — Node16/Node18 sync-import-of-ESM interop errors.
                        bool isSyncImport =
                            (program.GetDefaultResolutionModeForFile(SourceFileAsHasFileName(importingSourceFile!)) == ModuleKind.CommonJS && AstUtilities.FindAncestor(location, AstUtilities.IsImportCall) == null) ||
                            AstUtilities.FindAncestor(location, NodeTests.IsImportEqualsDeclaration) != null;
                        Node? overrideHost = AstUtilities.FindAncestor(location, AstUtilities.IsResolutionModeOverrideHost);
                        if (isSyncImport && program.GetDefaultResolutionModeForFile(SourceFileAsHasFileName(sourceFile)) == ModuleKind.ESNext && !AstUtilities.HasResolutionModeOverride(overrideHost))
                        {
                            if (AstUtilities.FindAncestorKind(location, SyntaxKind.ImportEqualsDeclaration) != null)
                            {
                                // ImportEquals in an ESM file resolving to another ESM file.
                                Error(errorNode, global::Ts.Diagnostics.Diagnostics.Module_0_cannot_be_imported_using_this_construct_The_specifier_only_resolves_to_an_ES_module_which_cannot_be_imported_with_require_Use_an_ECMAScript_import_instead, moduleReference);
                            }
                            else
                            {
                                // CJS file resolving to an ESM file.
                                Diagnostic? diagnosticDetails = null;
                                string ext = global::Ts.TsPath.TsPath.TryGetExtensionFromPath(importingSourceFile!.FileName());
                                if (ext == global::Ts.TsPath.TsPath.ExtensionTs || ext == global::Ts.TsPath.TsPath.ExtensionJs || ext == global::Ts.TsPath.TsPath.ExtensionTsx || ext == global::Ts.TsPath.TsPath.ExtensionJsx)
                                {
                                    diagnosticDetails = CreateModeMismatchDetails(importingSourceFile, errorNode);
                                }

                                global::Ts.Diagnostics.Message message;
                                if (overrideHost != null && overrideHost.Kind == SyntaxKind.ImportDeclaration && overrideHost.ImportClause() != null && overrideHost.ImportClause()!.IsTypeOnly())
                                {
                                    message = global::Ts.Diagnostics.Diagnostics.Type_only_import_of_an_ECMAScript_module_from_a_CommonJS_module_must_have_a_resolution_mode_attribute;
                                }
                                else if (overrideHost != null && overrideHost.Kind == SyntaxKind.ImportType)
                                {
                                    message = global::Ts.Diagnostics.Diagnostics.Type_import_of_an_ECMAScript_module_from_a_CommonJS_module_must_have_a_resolution_mode_attribute;
                                }
                                else
                                {
                                    message = global::Ts.Diagnostics.Diagnostics.The_current_file_is_a_CommonJS_module_whose_imports_will_produce_require_calls_however_the_referenced_file_is_an_ECMAScript_module_and_cannot_be_imported_with_require_Consider_writing_a_dynamic_import_0_call_instead;
                                }

                                AddDiagnostic(NewDiagnosticChainForNode(diagnosticDetails, errorNode, message, moduleReference));
                            }
                        }
                    }
                }
                return GetMergedSymbol(sourceFile.Symbol);
            }
            if (errorNode != null && moduleNotFoundError != null && !IsSideEffectImport(errorNode))
            {
                Error(errorNode, global::Ts.Diagnostics.Diagnostics.File_0_is_not_a_module, resolvedModule!.ResolvedFileName);
            }
            return null;
        }

        if (patternAmbientModules.Len() != 0)
        {
            var pattern = global::Ts.Core.Core.FindBestPatternMatch(patternAmbientModules, static v => v.Pattern, moduleReference);
            if (pattern != null)
            {
                var augmentation = patternAmbientModuleAugmentations?.GetValueOrDefault(moduleReference);
                if (augmentation != null)
                {
                    return GetMergedSymbol(augmentation);
                }
                return GetMergedSymbol(pattern.Symbol);
            }
        }

        if (errorNode == null)
        {
            return null;
        }

        if (global::Ts.Module.ResolvedModuleExtensions.IsResolved(resolvedModule) && !ResolutionExtensionIsTSOrJson(resolvedModule!.Extension) && resolutionDiagnostic == null || resolutionDiagnostic == global::Ts.Diagnostics.Diagnostics.Could_not_find_a_declaration_file_for_module_0_1_implicitly_has_an_any_type)
        {
            if (isForAugmentation)
            {
                Error(errorNode, global::Ts.Diagnostics.Diagnostics.Invalid_module_name_in_augmentation_Module_0_resolves_to_an_untyped_module_at_1_which_cannot_be_augmented, moduleReference, resolvedModule!.ResolvedFileName);
            }
            else
            {
                ErrorOnImplicitAnyModule(noImplicitAny && moduleNotFoundError != null, errorNode, mode, resolvedModule!, moduleReference);
            }
            return null;
        }

        if (moduleNotFoundError != null)
        {
            // See if this was possibly a projectReference redirect
            if (global::Ts.Module.ResolvedModuleExtensions.IsResolved(resolvedModule))
            {
                var redirect = program.GetProjectReferenceFromSource(global::Ts.TsPath.TsPath.ToPath(resolvedModule!.ResolvedFileName, program.GetCurrentDirectory(), program.UseCaseSensitiveFileNames()));
                if (redirect != null && redirect.OutputDts != "")
                {
                    Error(errorNode, global::Ts.Diagnostics.Diagnostics.Output_file_0_has_not_been_built_from_source_file_1, redirect.OutputDts, resolvedModule.ResolvedFileName);
                    return null;
                }
            }

            if (resolutionDiagnostic != null)
            {
                Error(errorNode, resolutionDiagnostic, moduleReference, resolvedModule!.ResolvedFileName);
            }
            else
            {
                var isExtensionlessRelativePathImport = global::Ts.TsPath.TsPath.PathIsRelative(moduleReference) && !global::Ts.TsPath.TsPath.HasExtension(moduleReference);
                var resolutionIsNode16OrNext = moduleResolutionKind == ModuleResolutionKind.Node16 || moduleResolutionKind == ModuleResolutionKind.NodeNext;
                if (!compilerOptions.GetResolveJsonModule() && global::Ts.TsPath.TsPath.FileExtensionIs(moduleReference, global::Ts.TsPath.TsPath.ExtensionJson))
                {
                    Error(errorNode, global::Ts.Diagnostics.Diagnostics.Cannot_find_module_0_Consider_using_resolveJsonModule_to_import_module_with_json_extension, moduleReference);
                }
                else if (mode == global::Ts.Core.Core.ResolutionModeESM && resolutionIsNode16OrNext && isExtensionlessRelativePathImport)
                {
                    var absoluteRef = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(moduleReference, global::Ts.TsPath.TsPath.GetDirectoryPath(importingSourceFile!.FileName()));
                    var suggestedExt = GetSuggestedImportExtension(absoluteRef);
                    if (suggestedExt != "")
                    {
                        Error(errorNode, global::Ts.Diagnostics.Diagnostics.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Did_you_mean_0, moduleReference + suggestedExt);
                    }
                    else
                    {
                        Error(errorNode, global::Ts.Diagnostics.Diagnostics.Relative_import_paths_need_explicit_file_extensions_in_ECMAScript_imports_when_moduleResolution_is_node16_or_nodenext_Consider_adding_an_extension_to_the_import_path);
                    }
                }
                else if (resolvedModule != null && resolvedModule.AlternateResult != "")
                {
                    var errorInfo = CreateModuleNotFoundChain(resolvedModule, errorNode, moduleReference, mode, moduleReference);
                    AddDiagnostic(NewDiagnosticChainForNode(errorInfo, errorNode, moduleNotFoundError!, moduleReference));
                }
                else
                {
                    Error(errorNode, moduleNotFoundError, moduleReference);
                }
            }
        }

        return null;
    }

    // checker.go:15352
    private static bool ResolutionExtensionIsTSOrJson(string ext)
    {
        return global::Ts.TsPath.TsPath.ExtensionIsTs(ext) || ext == global::Ts.TsPath.TsPath.ExtensionJson;
    }

    // checker.go:15381
    private string GetSuggestedImportExtension(string extensionlessImportPath)
    {
        switch (true)
        {
            case true when program.FileExists(extensionlessImportPath + ".mts"):
                return ".mjs";
            case true when program.FileExists(extensionlessImportPath + ".ts"):
                return ".js";
            case true when program.FileExists(extensionlessImportPath + ".cts"):
                return ".cjs";
            case true when program.FileExists(extensionlessImportPath + ".mjs"):
                return ".mjs";
            case true when program.FileExists(extensionlessImportPath + ".js"):
                return ".js";
            case true when program.FileExists(extensionlessImportPath + ".cjs"):
                return ".cjs";
            case true when program.FileExists(extensionlessImportPath + ".tsx"):
                return compilerOptions.Jsx == JsxEmit.Preserve ? ".jsx" : ".js";
            case true when program.FileExists(extensionlessImportPath + ".jsx"):
                return ".jsx";
            case true when program.FileExists(extensionlessImportPath + ".json"):
                return ".json";
        }
        return "";
    }

    // checker.go:15355 — getSuggestedImportSource.
    private string GetSuggestedImportSource(string moduleReference, string tsExtension, ResolutionMode mode)
    {
        string importSourceWithoutExtension = global::Ts.TsPath.TsPath.RemoveExtension(moduleReference, tsExtension);

        // Direct users to import source with .js extension if outputting an ES module.
        // @see https://github.com/microsoft/TypeScript/issues/42151
        if (moduleKind.IsNonNodeESM() || mode == ModuleKind.ESNext)
        {
            bool preferTs = global::Ts.TsPath.TsPath.IsDeclarationFileName(moduleReference) && compilerOptions.GetAllowImportingTsExtensions();
            string ext;
            if (tsExtension == global::Ts.TsPath.TsPath.ExtensionMts || tsExtension == global::Ts.TsPath.TsPath.ExtensionDmts)
            {
                ext = preferTs ? ".mts" : ".mjs";
            }
            else if (tsExtension == global::Ts.TsPath.TsPath.ExtensionCts || tsExtension == global::Ts.TsPath.TsPath.ExtensionDcts)
            {
                ext = preferTs ? ".cts" : ".cjs";
            }
            else
            {
                ext = preferTs ? ".ts" : ".js";
            }

            return importSourceWithoutExtension + ext;
        }

        return importSourceWithoutExtension;
    }

    // checker.go:15406
    private void ErrorOnImplicitAnyModule(bool isError, Node errorNode, ResolutionMode mode, global::Ts.Module.ResolvedModule resolvedModule, string moduleReference)
    {
        if (IsSideEffectImport(errorNode))
        {
            return;
        }

        Diagnostic? errorInfo = null;
        if (!global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(moduleReference) && !string.IsNullOrEmpty(resolvedModule.PackageId.Name))
        {
            errorInfo = CreateModuleNotFoundChain(resolvedModule, errorNode, moduleReference, mode, resolvedModule.PackageId.Name);
        }
        AddErrorOrSuggestion(
            isError,
            NewDiagnosticChainForNode(
                errorInfo,
                errorNode,
                global::Ts.Diagnostics.Diagnostics.Could_not_find_a_declaration_file_for_module_0_1_implicitly_has_an_any_type,
                moduleReference,
                resolvedModule.ResolvedFileName));
    }

    // utilities.go:1762 — CreateModuleNotFoundChain. Computes the message+args for a module-not-found
    // suggestion chain; shared with the incremental builder (repopulation of cached diagnostics).
    public static DiagnosticDetails CreateModuleNotFoundChain(Program program, SourceFile? file, string moduleReference, ResolutionMode mode, string packageName)
    {
        global::Ts.Module.ResolvedModule? resolvedModule = program.GetResolvedModule(file!, moduleReference, mode);

        if (resolvedModule != null && resolvedModule.AlternateResult != "")
        {
            if (resolvedModule.AlternateResult.Contains("/node_modules/@types/", StringComparison.Ordinal))
            {
                packageName = "@types/" + global::Ts.Module.Module.MangleScopedPackageName(packageName);
            }
            return new DiagnosticDetails(
                global::Ts.Diagnostics.Diagnostics.There_are_types_at_0_but_this_result_could_not_be_resolved_when_respecting_package_json_exports_The_1_library_may_need_to_update_its_package_json_or_typings,
                [resolvedModule.AlternateResult, packageName]);
        }

        Dictionary<string, bool> packagesMap = program.GetPackagesMap();
        if (packagesMap.ContainsKey(global::Ts.Module.Module.GetTypesPackageName(packageName)))
        {
            return new DiagnosticDetails(
                global::Ts.Diagnostics.Diagnostics.If_the_0_package_actually_exposes_this_module_consider_sending_a_pull_request_to_amend_https_Colon_Slash_Slashgithub_com_SlashDefinitelyTyped_SlashDefinitelyTyped_Slashtree_Slashmaster_Slashtypes_Slash_1,
                [packageName, global::Ts.Module.Module.MangleScopedPackageName(packageName)]);
        }
        if (packagesMap.TryGetValue(packageName, out bool present) && present)
        {
            return new DiagnosticDetails(
                global::Ts.Diagnostics.Diagnostics.If_the_0_package_actually_exposes_this_module_try_adding_a_new_declaration_d_ts_file_containing_declare_module_1,
                [packageName, moduleReference]);
        }
        return new DiagnosticDetails(
            global::Ts.Diagnostics.Diagnostics.Try_npm_i_save_dev_types_Slash_1_if_it_exists_or_add_a_new_declaration_d_ts_file_containing_declare_module_0,
            [moduleReference, global::Ts.Module.Module.MangleScopedPackageName(packageName)]);
    }

    // utilities.go:1798 — CreateModeMismatchDetails. Message+args for a mode-mismatch chain entry;
    // shared between initial diagnostic creation and incremental repopulation.
    public static DiagnosticDetails CreateModeMismatchDetails(Program program, SourceFile file)
    {
        string ext = global::Ts.TsPath.TsPath.TryGetExtensionFromPath(file.FileName());
        string targetExt = ext == global::Ts.TsPath.TsPath.ExtensionTs
            ? global::Ts.TsPath.TsPath.ExtensionMts
            : (ext == global::Ts.TsPath.TsPath.ExtensionJs ? global::Ts.TsPath.TsPath.ExtensionMjs : "");
        SourceFileMetaData meta = program.GetSourceFileMetaData(file.Path());
        string packageJsonType = meta.PackageJsonType;
        string packageJsonDirectory = meta.PackageJsonDirectory;

        if (packageJsonDirectory != "" && packageJsonType == "")
        {
            if (targetExt != "")
            {
                return new DiagnosticDetails(
                    global::Ts.Diagnostics.Diagnostics.To_convert_this_file_to_an_ECMAScript_module_change_its_file_extension_to_0_or_add_the_field_type_Colon_module_to_1,
                    [targetExt, global::Ts.TsPath.TsPath.CombinePaths(packageJsonDirectory, "package.json")]);
            }
            return new DiagnosticDetails(
                global::Ts.Diagnostics.Diagnostics.To_convert_this_file_to_an_ECMAScript_module_add_the_field_type_Colon_module_to_0,
                [global::Ts.TsPath.TsPath.CombinePaths(packageJsonDirectory, "package.json")]);
        }
        if (targetExt != "")
        {
            return new DiagnosticDetails(
                global::Ts.Diagnostics.Diagnostics.To_convert_this_file_to_an_ECMAScript_module_change_its_file_extension_to_0_or_create_a_local_package_json_file_with_type_Colon_module,
                [targetExt]);
        }
        return new DiagnosticDetails(
            global::Ts.Diagnostics.Diagnostics.To_convert_this_file_to_an_ECMAScript_module_create_a_local_package_json_file_with_type_Colon_module,
            null);
    }

    // checker.go:15424 — createModuleNotFoundChain (instance wrapper: builds the diagnostic + repopulate info).
    private Diagnostic CreateModuleNotFoundChain(global::Ts.Module.ResolvedModule resolvedModule, Node errorNode, string moduleReference, ResolutionMode mode, string packageName)
    {
        // Store the original packageName for repopulateInfo before any modifications.
        string storedPackageName = packageName;
        if (storedPackageName == moduleReference)
        {
            storedPackageName = "";
        }

        DiagnosticDetails details = CreateModuleNotFoundChain(program, AstUtilities.GetSourceFileOfNode(errorNode), moduleReference, mode, packageName);
        Diagnostic result = NewDiagnosticForNode(errorNode, details.Message, details.Args ?? []);
        result.SetRepopulateInfo(new RepopulateDiagnosticInfo
        {
            Kind = RepopulateDiagnosticKind.RepopulateModuleNotFound,
            ModuleReference = moduleReference,
            Mode = mode,
            PackageName = storedPackageName,
        });
        return result;
    }

    // checker.go:15442 — createModeMismatchDetails (instance wrapper).
    private Diagnostic CreateModeMismatchDetails(SourceFile sourceFile, Node errorNode)
    {
        DiagnosticDetails details = CreateModeMismatchDetails(program, sourceFile);
        Diagnostic result = NewDiagnosticForNode(errorNode, details.Message, details.Args ?? []);
        result.SetRepopulateInfo(new RepopulateDiagnosticInfo { Kind = RepopulateDiagnosticKind.RepopulateModeMismatch });
        return result;
    }

    // checker/utilities.go:206
    private static bool IsSideEffectImport(Node node)
    {
        var ancestor = FindAncestor(node, IsImportDeclaration);
        return ancestor != null && ancestor.ImportClause() == null;
    }

    // checker.go:15463
    public Symbol[] GetAmbientModules()
    {
        ambientModulesOnce.Do(() =>
        {
            List<Symbol>? modules = null;
            foreach (var (sym, global) in globals) // map-iter: 序无关 (results only feed set-inserts)
            {
                if (sym.StartsWith('"') && sym.EndsWith('"'))
                {
                    modules ??= new List<Symbol>();
                    modules.Add(global);
                }
            }
            ambientModules = modules?.ToArray();
        });
        return ambientModules ?? [];
    }

    // checker.go:15451
    internal Symbol? TryFindAmbientModule(string moduleName, bool withAugmentations)
    {
        if (global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(moduleName))
        {
            return null;
        }
        var symbol = GetSymbol(globals, "\"" + moduleName + "\"", SymbolFlags.ValueModule);
        // merged symbol is module declaration symbol combined with all augmentations
        if (withAugmentations)
        {
            return GetMergedSymbol(symbol);
        }
        return symbol;
    }

    // checker.go:15474
    internal Symbol? ResolveExternalModuleSymbol(Symbol? moduleSymbol, bool dontResolveAlias)
    {
        if (moduleSymbol != null)
        {
            var exportEquals = ResolveSymbolEx(moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals), dontResolveAlias);
            if (exportEquals != null)
            {
                return GetMergedSymbol(exportEquals);
            }
        }
        return moduleSymbol;
    }

    // checker.go:15649
    internal Symbol? GetTargetOfAliasDeclaration(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        switch (node.Kind)
        {
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.VariableDeclaration:
                return GetTargetOfImportEqualsDeclaration(node);
            case SyntaxKind.ImportClause:
                return GetTargetOfImportClause(node);
            case SyntaxKind.NamespaceImport:
                return GetTargetOfNamespaceImport(node);
            case SyntaxKind.NamespaceExport:
                return GetTargetOfNamespaceExport(node);
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.BindingElement:
                return GetTargetOfImportSpecifier(node);
            case SyntaxKind.ExportSpecifier:
                return GetTargetOfExportSpecifier(node, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace, true /*dontRecursivelyResolve*/);
            case SyntaxKind.ExportAssignment:
                return GetTargetOfExportAssignment(node);
            case SyntaxKind.BinaryExpression:
                return GetTargetOfBinaryExpression(node);
            case SyntaxKind.NamespaceExportDeclaration:
                return GetTargetOfNamespaceExportDeclaration(node);
            case SyntaxKind.ShorthandPropertyAssignment:
                return ResolveEntityName(node.Name()!, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace, true /*ignoreErrors*/, true /*dontRecursivelyResolve*/, null /*location*/);
            case SyntaxKind.PropertyAssignment:
                return GetTargetOfAliasLikeExpression(node.Initializer()!);
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.PropertyAccessExpression:
                return GetTargetOfAccessExpression(node);
        }
        throw new InvalidOperationException("Unhandled case in getTargetOfAliasDeclaration: " + node.Kind);
    }

    // checker.go:15685 — Resolves a qualified name and any involved aliases.
    internal Symbol? ResolveEntityName(Node name, SymbolFlags meaning, bool ignoreErrors, bool dontResolveAlias, Node? location)
    {
        if (NodeIsMissing(name))
        {
            return null;
        }
        Symbol? symbol;
        switch (name.Kind)
        {
            case SyntaxKind.Identifier:
            {
                global::Ts.Diagnostics.Message? message = null;
                if (!ignoreErrors)
                {
                    if (meaning == SymbolFlags.Namespace || NodeIsSynthesized(name))
                    {
                        message = global::Ts.Diagnostics.Diagnostics.Cannot_find_namespace_0;
                    }
                    else
                    {
                        message = GetCannotFindNameDiagnosticForName(GetFirstIdentifier(name));
                    }
                }
                var resolveLocation = location ?? name;
                if (meaning == SymbolFlags.Namespace)
                {
                    symbol = GetMergedSymbol(resolveName(resolveLocation, name.Text(), meaning, null, true /*isUse*/, false /*excludeGlobals*/));
                    if (symbol == null)
                    {
                        var alias = GetMergedSymbol(resolveName(resolveLocation, name.Text(), SymbolFlags.Alias, null, true /*isUse*/, false /*excludeGlobals*/));
                        if (alias != null && alias.Name == InternalSymbolName.ExportEquals)
                        {
                            // resolve typedefs exported from commonjs, stored on the module symbol
                            symbol = alias.Parent;
                        }
                    }
                    if (symbol == null && message != null)
                    {
                        resolveName(resolveLocation, name.Text(), meaning, message, true /*isUse*/, false /*excludeGlobals*/);
                    }
                }
                else
                {
                    symbol = GetMergedSymbol(resolveName(resolveLocation, name.Text(), meaning, message, true /*isUse*/, false /*excludeGlobals*/));
                }
                break;
            }
            case SyntaxKind.QualifiedName:
            {
                var qualified = name.AsQualifiedName();
                symbol = ResolveQualifiedName(name, qualified.Left!, qualified.Right!, meaning, ignoreErrors, location);
                break;
            }
            case SyntaxKind.PropertyAccessExpression:
            {
                var access = name.AsPropertyAccessExpression();
                symbol = ResolveQualifiedName(name, access.Expression!, access.Name()!, meaning, ignoreErrors, location);
                break;
            }
            default:
                throw new InvalidOperationException("Unknown entity name kind");
        }
        if (symbol != null && symbol != unknownSymbol)
        {
            if (!NodeIsSynthesized(name) && IsEntityName(name) &&
                ((symbol.Flags & SymbolFlags.Alias) != 0 || (name.Parent != null && name.Parent.Kind == SyntaxKind.ExportAssignment)))
            {
                MarkSymbolOfAliasDeclarationIfTypeOnly(GetAliasDeclarationFromName(name), null);
            }
            // We know a symbol with the given meaning exists along the alias chain, so resolve until we find it.
            while ((symbol!.Flags & meaning) == 0 && !dontResolveAlias && (symbol.Flags & SymbolFlags.Alias) != 0)
            {
                symbol = ResolveAliasCore(symbol);
            }
        }
        return symbol;
    }

    // checker.go:15741
    internal Symbol? ResolveQualifiedName(Node name, Node left, Node right, SymbolFlags meaning, bool ignoreErrors, Node? location)
    {
        var namespace_ = ResolveEntityName(left, SymbolFlags.Namespace, ignoreErrors, false /*dontResolveAlias*/, location);
        if (namespace_ == null || NodeIsMissing(right))
        {
            return null;
        }
        if (namespace_ == unknownSymbol)
        {
            return namespace_;
        }
        if (namespace_.ValueDeclaration != null &&
            IsInJSFile(namespace_.ValueDeclaration) &&
            compilerOptions.GetModuleResolutionKind() != ModuleResolutionKind.Bundler &&
            IsVariableDeclaration(namespace_.ValueDeclaration) &&
            namespace_.ValueDeclaration.Initializer() != null &&
            IsCommonJSRequire(namespace_.ValueDeclaration.Initializer()!))
        {
            var moduleName = namespace_.ValueDeclaration!.Initializer()!.Arguments()![0];
            var moduleSym = ResolveExternalModuleName(moduleName, moduleName, false /*ignoreErrors*/);
            if (moduleSym != null)
            {
                var resolvedModuleSymbol = ResolveExternalModuleSymbol(moduleSym, false /*dontResolveAlias*/);
                if (resolvedModuleSymbol != null)
                {
                    namespace_ = resolvedModuleSymbol;
                }
            }
        }
        var text = right.Text();
        var symbol = GetMergedSymbol(GetSymbol(GetExportsOfSymbol(namespace_), text, meaning));
        if (symbol == null && (namespace_.Flags & SymbolFlags.Alias) != 0)
        {
            // `namespace` can be resolved further if there was a symbol merge with a re-export
            symbol = GetMergedSymbol(GetSymbol(GetExportsOfSymbol(ResolveAliasCore(namespace_)), text, meaning));
        }
        if (symbol == null)
        {
            if (!ignoreErrors)
            {
                var namespaceName = GetFullyQualifiedName(namespace_, null /*containingLocation*/);
                var declarationName = global::Ts.Scanner.Scanner.DeclarationNameToString(right);
                var suggestionForNonexistentModule = GetSuggestedSymbolForNonexistentModule(right, namespace_);
                if (suggestionForNonexistentModule != null)
                {
                    Error(right, global::Ts.Diagnostics.Diagnostics.X_0_has_no_exported_member_named_1_Did_you_mean_2, namespaceName, declarationName, SymbolToString(suggestionForNonexistentModule));
                    return null;
                }
                Node? containingQualifiedName = null;
                if (IsQualifiedName(name))
                {
                    containingQualifiedName = GetContainingQualifiedNameNode(name);
                }
                var canSuggestTypeof = globalObjectType != null && (meaning & SymbolFlags.Type) != 0 && containingQualifiedName != null && !IsTypeOfExpression(containingQualifiedName.Parent!) && TryGetQualifiedNameAsValue(containingQualifiedName) != null;
                if (canSuggestTypeof)
                {
                    Error(containingQualifiedName, global::Ts.Diagnostics.Diagnostics.X_0_refers_to_a_value_but_is_being_used_as_a_type_here_Did_you_mean_typeof_0, EntityNameToString(containingQualifiedName!));
                    return null;
                }
                if ((meaning & SymbolFlags.Namespace) != 0)
                {
                    if (IsQualifiedName(name.Parent!))
                    {
                        var exportedTypeSymbol = GetMergedSymbol(GetSymbol(GetExportsOfSymbol(namespace_), text, SymbolFlags.Type));
                        if (exportedTypeSymbol != null)
                        {
                            var qualified = name.Parent!.AsQualifiedName();
                            Error(qualified.Right, global::Ts.Diagnostics.Diagnostics.Cannot_access_0_1_because_0_is_a_type_but_not_a_namespace_Did_you_mean_to_retrieve_the_type_of_the_property_1_in_0_with_0_1, SymbolToString(exportedTypeSymbol), qualified.Right!.Text());
                            return null;
                        }
                    }
                }
                Error(right, global::Ts.Diagnostics.Diagnostics.Namespace_0_has_no_exported_member_1, namespaceName, declarationName);
            }
        }
        return symbol;
    }

    // checker.go:15804
    internal Symbol? TryGetQualifiedNameAsValue(Node node)
    {
        var id = GetFirstIdentifier(node);
        var symbol = resolveName(id, id.Text(), SymbolFlags.Value, null /*nameNotFoundMessage*/, true /*isUse*/, false /*excludeGlobals*/);
        if (symbol == null)
        {
            return null;
        }
        var n = id;
        while (IsQualifiedName(n.Parent!))
        {
            var t = GetTypeOfSymbol(symbol);
            symbol = GetPropertyOfType(t, n.Parent!.AsQualifiedName().Right!.Text());
            if (symbol == null)
            {
                return null;
            }
            n = n.Parent!;
        }
        return symbol;
    }

    // checker.go:15826
    internal string GetFullyQualifiedName(Symbol symbol, Node? containingLocation)
    {
        if (symbol.Parent != null)
        {
            return GetFullyQualifiedName(symbol.Parent, containingLocation) + "." + SymbolToString(symbol);
        }
        return SymbolToStringEx(symbol, containingLocation, SymbolFlags.All, SymbolFormatFlags.DoNotIncludeSymbolChain | SymbolFormatFlags.AllowAnyNodeKind);
    }

    // checker.go:15833
    internal SymbolTable? GetExportsOfSymbol(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.LateBindingContainer) != 0)
        {
            return GetResolvedMembersOrExportsOfSymbol(symbol, MembersOrExportsResolutionKind.ResolvedExports);
        }
        if ((symbol.Flags & SymbolFlags.Module) != 0)
        {
            return GetExportsOfModule(symbol);
        }
        return symbol.Exports;
    }

    // checker.go:15843
    internal SymbolTable? GetResolvedMembersOrExportsOfSymbol(Symbol symbol, MembersOrExportsResolutionKind resolutionKind)
    {
        var links = membersAndExportsLinks.Get(symbol);
        if (links[resolutionKind] == null)
        {
            var isStatic = resolutionKind == MembersOrExportsResolutionKind.ResolvedExports;
            SymbolTable? earlySymbols = symbol.Exports;
            if (!isStatic)
            {
                earlySymbols = symbol.Members;
            }
            else if ((symbol.Flags & SymbolFlags.Module) != 0)
            {
                (earlySymbols, _) = GetExportsOfModuleWorker(symbol);
            }
            links[resolutionKind] = earlySymbols;
            // fill in any as-yet-unresolved late-bound members.
            SymbolTable? lateSymbols = null;
            foreach (var decl in symbol.Declarations.OrEmpty())
            {
                foreach (var member in GetMembersOfDeclaration(decl).OrEmpty())
                {
                    if (isStatic == HasStaticModifier(member))
                    {
                        if (HasLateBindableName(member))
                        {
                            lateSymbols ??= new SymbolTable();
                            LateBindMember(symbol, earlySymbols, lateSymbols, member);
                        }
                        else if (HasLateBindableIndexSignature(member))
                        {
                            lateSymbols ??= new SymbolTable();
                            LateBindIndexSignature(symbol, earlySymbols, lateSymbols, member);
                        }
                    }
                }
            }
            if (isStatic)
            {
                var assignmentSymbol = symbol.Exports?.GetValueOrDefault(InternalSymbolName.AssignmentDeclaration);
                if (assignmentSymbol != null)
                {
                    foreach (var member in assignmentSymbol.Declarations.OrEmpty())
                    {
                        if (HasLateBindableName(member))
                        {
                            lateSymbols ??= new SymbolTable();
                            LateBindMember(symbol, earlySymbols, lateSymbols, member);
                        }
                    }
                }
            }
            links[resolutionKind] = CombineSymbolTables(earlySymbols, lateSymbols);
        }
        return links[resolutionKind];
    }

    // checker.go:15918 — Performs late-binding of a dynamic member.
    internal Symbol? LateBindMember(Symbol parent, SymbolTable? earlySymbols, SymbolTable lateSymbols, Node decl)
    {
        Debug.Assert(decl.Symbol() != null, "The member is expected to have a symbol.");
        var links = symbolNodeLinks.Get(decl);
        if (links.resolvedSymbol == null)
        {
            // In the event we attempt to resolve the late-bound name of this member recursively,
            // fall back to the early-bound name of this member.
            links.resolvedSymbol = decl.Symbol();
            Node? declName;
            if (IsBinaryExpression(decl))
            {
                declName = decl.AsBinaryExpression().Left;
            }
            else
            {
                declName = decl.Name();
            }
            Type t;
            if (IsElementAccessExpression(declName!))
            {
                t = CheckExpressionCached(declName!.AsElementAccessExpression().ArgumentExpression!);
            }
            else
            {
                t = CheckComputedPropertyName(declName!);
            }
            if (IsTypeUsableAsPropertyName(t))
            {
                var memberName = GetPropertyNameFromType(t);
                var symbolFlags = decl.Symbol()!.Flags;
                // Get or add a late-bound symbol for the member.
                var lateSymbol = lateSymbols.GetValueOrDefault(memberName);
                if (lateSymbol == null)
                {
                    lateSymbol = NewSymbolEx(SymbolFlags.None, memberName, CheckFlags.Late);
                    lateSymbols[memberName] = lateSymbol;
                }
                // Report an error if there's a symbol declaration with the same name and conflicting flags.
                var earlySymbol = earlySymbols?.GetValueOrDefault(memberName); // Go nil-map read
                if ((lateSymbol.Flags & GetExcludedSymbolFlags(symbolFlags)) != 0)
                {
                    Node[] declarations;
                    if (earlySymbol != null)
                    {
                        declarations = CoreUtil.Concatenate(earlySymbol.Declarations, lateSymbol.Declarations) ?? [];
                    }
                    else
                    {
                        declarations = lateSymbol.Declarations.OrEmpty();
                    }
                    var name = memberName;
                    if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
                    {
                        name = global::Ts.Scanner.Scanner.DeclarationNameToString(declName);
                    }
                    foreach (var d in declarations)
                    {
                        Error(CoreUtil.OrElse(GetNameOfDeclaration(d), d), global::Ts.Diagnostics.Diagnostics.Duplicate_identifier_0, name);
                    }
                    Error(CoreUtil.OrElse(declName, decl), global::Ts.Diagnostics.Diagnostics.Duplicate_identifier_0, name);
                    if ((lateSymbol.Flags & SymbolFlags.Accessor) != 0 && (lateSymbol.Flags & SymbolFlags.Accessor) != (symbolFlags & SymbolFlags.Accessor))
                    {
                        lateSymbol.Flags |= SymbolFlags.Accessor;
                    }
                    lateSymbol = NewSymbolEx(SymbolFlags.None, memberName, CheckFlags.Late);
                }
                valueSymbolLinks.Get(lateSymbol).nameType = t;
                AddDeclarationToLateBoundSymbol(lateSymbol, decl, symbolFlags);
                if (lateSymbol.Parent == null)
                {
                    lateSymbol.Parent = parent;
                }
                links.resolvedSymbol = lateSymbol;
            }
        }
        return links.resolvedSymbol;
    }

    // checker.go:15981
    internal void LateBindIndexSignature(Symbol parent, SymbolTable? earlySymbols, SymbolTable lateSymbols, Node decl)
    {
        // First, late bind the index symbol itself, if needed
        var indexSymbol = lateSymbols.GetValueOrDefault(InternalSymbolName.Index);
        if (indexSymbol == null)
        {
            var early = earlySymbols?.GetValueOrDefault(InternalSymbolName.Index); // Go nil-map read
            if (early == null)
            {
                indexSymbol = NewSymbolEx(SymbolFlags.None, InternalSymbolName.Index, CheckFlags.Late);
            }
            else
            {
                indexSymbol = CloneSymbol(early);
                indexSymbol.CheckFlags |= CheckFlags.Late;
            }
            lateSymbols[InternalSymbolName.Index] = indexSymbol;
        }
        // Then just add the computed name as a late bound declaration
        if (indexSymbol.Declarations.Len() == 0 || (decl.Symbol()!.Flags & SymbolFlags.ReplaceableByMethod) == 0)
        {
            indexSymbol.Declarations = AppendDeclaration(indexSymbol.Declarations, decl);
        }
    }

    // checker.go:16002
    private static bool IsNotReplacableByMethod(Node decl)
    {
        return (decl.Symbol()!.Flags & SymbolFlags.ReplaceableByMethod) == 0;
    }

    // checker.go:16009 — Adds a declaration to a late-bound dynamic member.
    internal void AddDeclarationToLateBoundSymbol(Symbol symbol, Node member, SymbolFlags symbolFlags)
    {
        Debug.Assert((symbol.CheckFlags & CheckFlags.Late) != 0, "Expected a late-bound symbol.");
        lateBoundLinks.Get(member.Symbol()!).lateSymbol = symbol;
        if (symbol.Declarations.Len() == 0 || (member.Symbol()!.Flags & SymbolFlags.ReplaceableByMethod) == 0)
        {
            symbol.Flags |= symbolFlags;
            symbol.Declarations = AppendDeclaration(symbol.Declarations, member);
        }
        else if ((symbol.Flags & SymbolFlags.ReplaceableByMethod) != 0 && (member.Symbol()!.Flags & SymbolFlags.Method) != 0)
        {
            // Remove all replacable-by-method members, along with their flags.
            symbol.Declarations = AppendDeclaration(CoreUtil.Filter(symbol.Declarations, IsNotReplacableByMethod), member);
            var oldFlags = symbol.Flags;
            symbol.Flags = SymbolFlags.None;
            foreach (var d in symbol.Declarations.OrEmpty())
            {
                symbol.Flags |= d.Symbol()!.Flags;
            }
            if ((oldFlags & SymbolFlags.Accessor) != 0)
            {
                symbol.Flags |= SymbolFlags.Accessor;
            }
        }
        if ((symbolFlags & SymbolFlags.Value) != 0)
        {
            global::Ts.Binder.Binder.SetValueDeclaration(symbol, member);
        }
    }

    // checker.go:16037 — SymbolTable of both early- and late-bound members of a symbol.
    internal SymbolTable GetMembersOfSymbol(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.LateBindingContainer) != 0)
        {
            return GetResolvedMembersOrExportsOfSymbol(symbol, MembersOrExportsResolutionKind.ResolvedMembers) ?? EmptySymbolTable;
        }
        return symbol.Members ?? EmptySymbolTable;
    }

    // checker.go:16044
    internal SymbolTable GetExportsOfModule(Symbol moduleSymbol)
    {
        var links = moduleSymbolLinks.Get(moduleSymbol);
        if (links.resolvedExports == null)
        {
            var (exports, typeOnlyExportStarMap) = GetExportsOfModuleWorker(moduleSymbol);
            links.resolvedExports = exports;
            links.typeOnlyExportStarMap = typeOnlyExportStarMap;
        }
        return links.resolvedExports;
    }

    // checker.go:16054-16059
    internal sealed class ExportCollision
    {
        internal string specifierText = "";
        internal List<Node> exportsWithDuplicate = [];
    }

    // checker.go:16061
    internal (SymbolTable exports, Dictionary<string, Node>? typeOnlyExportStarMap) GetExportsOfModuleWorker(Symbol? moduleSymbol)
    {
        var visitedSymbols = new List<Symbol>();
        var nonTypeOnlyNames = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Node>? typeOnlyExportStarMap = null;

        // The ES6 spec permits export * declarations to circularly reference the module itself.
        SymbolTable? Visit(Symbol? symbol, Node? exportStar, bool isTypeOnly)
        {
            if (!isTypeOnly && symbol != null)
            {
                // Add non-type-only names before checking if we've visited this module.
                foreach (var name in (symbol.Exports ?? EmptySymbolTable).Keys)
                {
                    nonTypeOnlyNames.Add(name);
                }
            }
            if (symbol == null || symbol.Exports == null || visitedSymbols.Contains(symbol))
            {
                return null;
            }
            visitedSymbols.Add(symbol);
            var symbols = new SymbolTable(symbol.Exports);
            // All export * declarations are collected in an __export symbol by the binder
            var exportStars = symbol.Exports.GetValueOrDefault(InternalSymbolName.ExportStar);
            if (exportStars != null)
            {
                var nestedSymbols = new SymbolTable();
                var lookupTable = new Dictionary<string, ExportCollision>(StringComparer.Ordinal);
                foreach (var node in exportStars.Declarations.OrEmpty())
                {
                    var resolvedModule = ResolveExternalModuleName(node, node.ModuleSpecifier(), false /*ignoreErrors*/);
                    var exportedSymbols = Visit(resolvedModule, node, isTypeOnly || node.IsTypeOnly());
                    ExtendExportSymbols(nestedSymbols, exportedSymbols, lookupTable, node);
                }
                // map-iter: 序无关 —— diagnostics keyed by export name; each id independent
                foreach (var (id, s) in lookupTable)
                {
                    // It's not an error if the file with multiple `export *`s with duplicate names exports a member with that name itself
                    if (id == InternalSymbolName.ExportEquals || s.exportsWithDuplicate.Count == 0 || symbols.ContainsKey(id))
                    {
                        continue;
                    }
                    foreach (var node in s.exportsWithDuplicate)
                    {
                        AddDiagnostic(CreateDiagnosticForNode(node, global::Ts.Diagnostics.Diagnostics.Module_0_has_already_exported_a_member_named_1_Consider_explicitly_re_exporting_to_resolve_the_ambiguity, s.specifierText, id));
                    }
                }
                ExtendExportSymbols(symbols, nestedSymbols, null, null);
            }
            if (exportStar != null && exportStar.IsTypeOnly())
            {
                typeOnlyExportStarMap ??= new Dictionary<string, Node>(StringComparer.Ordinal);
                // map-iter: 序无关 —— fills typeOnlyExportStarMap by name
                foreach (var name in symbols.Keys)
                {
                    typeOnlyExportStarMap[name] = exportStar;
                }
            }
            return symbols;
        }

        Symbol? originalModule = null;
        if (moduleSymbol != null)
        {
            if (ResolveSymbolEx(moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals), false /*dontResolveAlias*/) != null)
            {
                originalModule = moduleSymbol;
            }
        }
        // A module defined by an 'export=' consists of one export that needs to be resolved
        moduleSymbol = ResolveExternalModuleSymbol(moduleSymbol, false /*dontResolveAlias*/);
        var exports = Visit(moduleSymbol, null, false) ?? new SymbolTable();
        // A CommonJS module defined by an 'export=' might also export typedefs, stored on the original module
        if (originalModule != null && originalModule.Exports.Len() > 1)
        {
            // map-iter: 序无关 —— copies type/namespace-only exports into `exports` by name
            foreach (var symbol in originalModule.Exports!.Values)
            {
                if (symbol.Name == InternalSymbolName.ExportEquals || symbol.Name == InternalSymbolName.ExportStar)
                {
                    continue;
                }
                var flags = GetSymbolFlags(symbol);
                if ((flags & (SymbolFlags.Type | SymbolFlags.Namespace)) != 0 &&
                    (flags & SymbolFlags.Value) == 0 &&
                    !exports.ContainsKey(symbol.Name))
                {
                    exports[symbol.Name] = symbol;
                }
            }
        }
        if (typeOnlyExportStarMap != null)
        {
            // map-iter: 序无关 —— removes non-type-only names from typeOnlyExportStarMap
            foreach (var name in nonTypeOnlyNames)
            {
                typeOnlyExportStarMap.Remove(name);
            }
        }
        return (exports, typeOnlyExportStarMap);
    }

    // checker.go:16148 — Extends one symbol table with another while collecting collisions.
    internal void ExtendExportSymbols(SymbolTable target, SymbolTable? source, Dictionary<string, ExportCollision>? lookupTable, Node? exportNode)
    {
        // map-iter: 序无关 —— merges source into target keyed by name (collisions collected)
        foreach (var (id, sourceSymbol) in (source ?? EmptySymbolTable))
        {
            if (id == InternalSymbolName.Default)
            {
                continue;
            }
            var targetSymbol = target.GetValueOrDefault(id);
            if (targetSymbol == null)
            {
                target[id] = sourceSymbol;
                if (lookupTable != null && exportNode != null)
                {
                    lookupTable[id] = new ExportCollision
                    {
                        specifierText = global::Ts.Scanner.Scanner.GetTextOfNode(exportNode.ModuleSpecifier()!),
                    };
                }
            }
            else if (lookupTable != null && exportNode != null && ResolveSymbol(targetSymbol) != ResolveSymbol(sourceSymbol))
            {
                var s = lookupTable[id];
                s.exportsWithDuplicate.Add(exportNode);
            }
        }
    }

    // checker.go:16168
    public (Symbol?, bool) ResolveAlias(Symbol? symbol)
    {
        if (symbol == null)
        {
            return (null, false);
        }
        var resolved = ResolveAliasCore(symbol);
        return (resolved, resolved != unknownSymbol);
    }

    // checker.go:16179 — Resolve an alias symbol to the first target with some other meaning.
    internal Symbol ResolveAliasCore(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.Alias) == 0)
        {
            throw new InvalidOperationException("Should only get alias here");
        }
        var links = aliasSymbolLinks.Get(symbol);
        if (links.aliasTarget == null)
        {
            if (!PushTypeResolution(symbol, TypeSystemPropertyName.AliasTarget))
            {
                return unknownSymbol;
            }
            var node = GetDeclarationOfAliasSymbol(symbol);
            if (node == null)
            {
                throw new InvalidOperationException("Unexpected nil in resolveAlias for symbol: " + SymbolToString(symbol));
            }
            var target = GetTargetOfAliasDeclaration(node);
            if (IsNonLocalAlias(target, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace))
            {
                // When the target is a pure alias, we transitively resolve and propagate any typeOnlyDeclaration
                target = ResolveIndirectionAlias(symbol, target!);
            }
            links.aliasTarget = CoreUtil.OrElse(target, unknownSymbol);
            if (!PopTypeResolution())
            {
                Error(node, global::Ts.Diagnostics.Diagnostics.Circular_definition_of_import_alias_0, SymbolToString(symbol));
                links.aliasTarget = unknownSymbol;
            }
        }
        return links.aliasTarget!;
    }

    // checker.go:16206
    internal Symbol? ResolveIndirectionAlias(Symbol source, Symbol target)
    {
        var result = GetMergedSymbol(ResolveAliasCore(target));
        var targetLinks = aliasSymbolLinks.Get(target);
        if (targetLinks.typeOnlyDeclaration != null)
        {
            var sourceLinks = aliasSymbolLinks.Get(source);
            if (sourceLinks.typeOnlyDeclaration == null)
            {
                sourceLinks.typeOnlyDeclaration = targetLinks.typeOnlyDeclaration;
            }
        }
        return result;
    }

    // checker.go:16216
    internal Symbol? TryResolveAlias(Symbol symbol)
    {
        var links = aliasSymbolLinks.Get(symbol);
        if (links.aliasTarget != null || FindResolutionCycleStartIndex(symbol, TypeSystemPropertyName.AliasTarget) < 0)
        {
            return ResolveAliasCore(symbol);
        }
        return null;
    }

    // checker.go:16224
    internal Symbol ResolveAliasWithDeprecationCheck(Symbol symbol, Node? location)
    {
        if ((symbol.Flags & SymbolFlags.Alias) == 0 || IsDeprecatedSymbol(symbol) || GetDeclarationOfAliasSymbol(symbol) == null)
        {
            return symbol;
        }
        var targetSymbol = ResolveAliasCore(symbol);
        if (targetSymbol == unknownSymbol)
        {
            return targetSymbol;
        }
        while ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            var target = GetImmediateAliasedSymbol(symbol);
            if (target != null)
            {
                if (target == targetSymbol)
                {
                    break;
                }
                if (target.Declarations.Len() != 0)
                {
                    if (IsDeprecatedSymbol(target))
                    {
                        AddDeprecatedSuggestion(location, target.Declarations!, target.Name);
                        break;
                    }
                    else
                    {
                        if (symbol == targetSymbol)
                        {
                            break;
                        }
                        symbol = target;
                    }
                }
            }
            else
            {
                break;
            }
        }
        return targetSymbol;
    }

    // checker.go:16276 — Combined flags of a symbol and all alias targets it resolves to.
    internal SymbolFlags GetSymbolFlags(Symbol symbol)
    {
        return GetSymbolFlagsEx(symbol, false /*excludeTypeOnlyMeanings*/, false /*excludeLocalMeanings*/);
    }

    // checker.go:16280
    internal SymbolFlags GetSymbolFlagsEx(Symbol symbol, bool excludeTypeOnlyMeanings, bool excludeLocalMeanings)
    {
        HashSet<Symbol>? seenSymbols = null;
        var flags = SymbolFlags.None;
        if (!excludeLocalMeanings)
        {
            flags = symbol.Flags;
        }
        while ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            if (excludeTypeOnlyMeanings && GetTypeOnlyAliasDeclaration(symbol) != null)
            {
                break;
            }
            var target = GetExportSymbolOfValueSymbolIfExported(ResolveAliasCore(symbol));
            if (target == unknownSymbol)
            {
                return SymbolFlags.All;
            }
            if ((target!.Flags & SymbolFlags.Alias) != 0)
            {
                // Optimization - try to avoid creating or adding to `seenSymbols` if possible
                if (target == symbol || (seenSymbols != null && seenSymbols.Contains(target)))
                {
                    break;
                }
                seenSymbols ??= new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
                if (seenSymbols.Count == 0)
                {
                    seenSymbols.Add(symbol);
                }
                seenSymbols.Add(target);
            }
            flags |= target.Flags;
            symbol = target;
        }
        return flags;
    }

    // checker.go:16310
    internal Node? GetDeclarationOfAliasSymbol(Symbol symbol)
    {
        return CoreUtil.FindLast(symbol.Declarations, IsAliasSymbolDeclaration);
    }

    // ===================================================================================
    // Cross-cluster stubs (methods OUTSIDE lines 14358-16405 that the ports above call).
    // Real implementations belong to other §4.2 partials; kept here as stubs so the
    // assembly compiles (plan iron law 10). Do not implement — remove if the owning
    // cluster lands a real version. `NotImplementedException("goName")`.
    // ===================================================================================

    // #10 module-resolution machinery — resolveExternalModuleName/-Worker/resolveExternalModule
    // trunk is now REAL above (checker.go:15019-15473); deep branches remain `// !!!` stubs there.
    // ResolveESModuleSymbol / GetExternalModuleMember: real bodies in Checker.SyntheticDefault.cs.

    // checker.go — getTargetOfModuleDefault.
    internal Symbol? GetTargetOfModuleDefault(Symbol moduleSymbol, Node node, bool dontResolveAlias)
    {
        var file = CoreUtil.Find(moduleSymbol.Declarations, NodeTests.IsSourceFile);
        var specifier = GetModuleSpecifierForImportOrExport(node);
        Symbol? exportDefaultSymbol = null;
        Symbol? exportModuleDotExportsSymbol = null;
        if (IsShorthandAmbientModuleSymbol(moduleSymbol))
        {
            // exportDefaultSymbol = moduleSymbol (Go no-op)
        }
        else if (file != null && specifier != null &&
            ModuleKind.Node20 <= moduleKind && moduleKind <= ModuleKind.NodeNext &&
            GetEmitSyntaxForModuleSpecifierExpression(specifier) == ModuleKind.CommonJS &&
            program.GetImpliedNodeFormatForEmit(SourceFileAsHasFileName(file.AsSourceFile())) == ModuleKind.ESNext)
        {
            exportModuleDotExportsSymbol = ResolveExportByName(moduleSymbol, InternalSymbolName.ModuleExports, node, dontResolveAlias);
        }
        if (exportModuleDotExportsSymbol != null)
        {
            MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
            return exportModuleDotExportsSymbol;
        }
        exportDefaultSymbol = ResolveExportByName(moduleSymbol, InternalSymbolName.Default, node, dontResolveAlias);
        if (specifier == null)
        {
            return exportDefaultSymbol;
        }
        var hasDefaultOnly = IsOnlyImportableAsDefault(specifier, moduleSymbol);
        var hasSyntheticDefault = CanHaveSyntheticDefault(file, moduleSymbol, dontResolveAlias, specifier);
        if (exportDefaultSymbol == null && !hasSyntheticDefault && !hasDefaultOnly)
        {
            if (NodeTests.IsImportClause(node))
            {
                ReportNonDefaultExport(moduleSymbol, node);
            }
            else
            {
                var name = AstUtilities.IsImportOrExportSpecifier(node) ? node.PropertyNameOrName() : node.Name();
                ErrorNoModuleMemberSymbol(moduleSymbol, moduleSymbol, node, name!);
            }
        }
        else if (hasSyntheticDefault || hasDefaultOnly)
        {
            // per emit behavior, a synthetic default overrides a "real" .default member if `__esModule` is not present
            var resolved = ResolveExternalModuleSymbol(moduleSymbol, dontResolveAlias) ?? ResolveSymbolEx(moduleSymbol, dontResolveAlias);
            MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
            return resolved;
        }
        MarkSymbolOfAliasDeclarationIfTypeOnly(node, null);
        return exportDefaultSymbol;
    }

    // checker.go — reportNonDefaultExport.
    private void ReportNonDefaultExport(Symbol moduleSymbol, Node node)
    {
        if (moduleSymbol.Exports != null && moduleSymbol.Exports.TryGetValue(node.Symbol()!.Name, out Symbol? existing) && existing != null)
        {
            Error(node, global::Ts.Diagnostics.Diagnostics.Module_0_has_no_default_export_Did_you_mean_to_use_import_1_from_0_instead, SymbolToString(moduleSymbol), SymbolToString(node.Symbol()));
        }
        else
        {
            Diagnostic diagnostic = Error(node.Name(), global::Ts.Diagnostics.Diagnostics.Module_0_has_no_default_export, SymbolToString(moduleSymbol));
            Symbol? exportStar = null;
            if (moduleSymbol.Exports != null)
            {
                moduleSymbol.Exports.TryGetValue(InternalSymbolName.ExportStar, out exportStar);
            }
            if (exportStar != null)
            {
                Node? defaultExport = CoreUtil.Find(exportStar.Declarations, decl =>
                {
                    if (!(NodeTests.IsExportDeclaration(decl) && decl.ModuleSpecifier() != null))
                    {
                        return false;
                    }
                    Symbol? resolvedExternalModuleName = ResolveExternalModuleName(decl, decl.ModuleSpecifier(), false /*ignoreErrors*/);
                    return resolvedExternalModuleName?.Exports != null &&
                        resolvedExternalModuleName.Exports.TryGetValue(InternalSymbolName.Default, out Symbol? d) && d != null;
                });
                if (defaultExport != null)
                {
                    diagnostic.AddRelatedInfo(CreateDiagnosticForNode(defaultExport, global::Ts.Diagnostics.Diagnostics.X_export_Asterisk_does_not_re_export_a_default));
                }
            }
        }
    }



    // checker.go:14420
    internal void CheckAndReportErrorForResolvingImportAliasToTypeOnlySymbol(Node node, Symbol? resolved)
    {
        var decl = node.AsImportEqualsDeclaration();
        Node name = decl.ModuleReference!;
        while (true)
        {
            var typeOnlyDeclaration = GetTypeOnlyDeclarationOfEntityName(name);
            if (typeOnlyDeclaration != null)
            {
                bool isExport = AstUtilities.NodeKindIs(typeOnlyDeclaration, SyntaxKind.ExportSpecifier, SyntaxKind.ExportDeclaration);
                var message = isExport
                    ? global::Ts.Diagnostics.Diagnostics.An_import_alias_cannot_reference_a_declaration_that_was_exported_using_export_type
                    : global::Ts.Diagnostics.Diagnostics.An_import_alias_cannot_reference_a_declaration_that_was_imported_using_import_type;
                var relatedMessage = isExport
                    ? global::Ts.Diagnostics.Diagnostics.X_0_was_exported_here
                    : global::Ts.Diagnostics.Diagnostics.X_0_was_imported_here;
                // TODO: how to get name for export *?
                string relatedName = "*";
                if (!NodeTests.IsExportDeclaration(typeOnlyDeclaration))
                {
                    relatedName = typeOnlyDeclaration.Name()!.Text();
                }
                Error(decl.ModuleReference, message).AddRelatedInfo(CreateDiagnosticForNode(typeOnlyDeclaration, relatedMessage, relatedName));
                break;
            }
            if (NodeTests.IsIdentifier(name))
            {
                break;
            }
            name = name.AsQualifiedName().Left!;
        }
    }

    // checker.go:14447
    internal Node? GetTypeOnlyDeclarationOfEntityName(Node name)
    {
        var symbol = ResolveEntityName(name, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace, true /*ignoreErrors*/, true /*dontResolveAlias*/, null /*location*/);
        if (symbol != null)
        {
            return GetTypeOnlyAliasDeclaration(symbol);
        }
        return null;
    }

    // checker.go:15822
    internal Symbol? GetSuggestedSymbolForNonexistentModule(Node name, Symbol targetModule)
    {
        // map-iter: 序无关 — Go maps.Values(exports) feeds the spelling-suggestion scan, which picks
        // the best-distance candidate (ties resolved by compareSymbols inside GetSpellingSuggestion).
        return GetSpellingSuggestionForName(name.Text(), GetExportsOfModule(targetModule).Values, SymbolFlags.ModuleMember);
    }

    // checker.go:15591
    internal bool IsCommonJSRequire(Node node)
    {
        if (!AstUtilities.IsRequireCall(node, true /*requireStringLiteralLikeArgument*/))
        {
            return false;
        }
        if (!NodeTests.IsIdentifier(node.Expression()!))
        {
            throw new InvalidOperationException("Expected identifier for require call");
        }
        // Make sure require is not a local function
        var resolvedRequire = ResolveName(node.Expression(), node.Expression()!.Text(), SymbolFlags.Value, null /*nameNotFoundMessage*/, true /*isUse*/, false /*excludeGlobals*/);
        if (resolvedRequire == requireSymbol)
        {
            return true;
        }
        // project includes symbol named 'require' - make sure that it is ambient and local non-alias
        if (resolvedRequire == null || (resolvedRequire.Flags & SymbolFlags.Alias) != 0)
        {
            return false;
        }

        SyntaxKind targetDeclarationKind;
        if ((resolvedRequire.Flags & SymbolFlags.Function) != 0)
        {
            targetDeclarationKind = SyntaxKind.FunctionDeclaration;
        }
        else if ((resolvedRequire.Flags & SymbolFlags.Variable) != 0)
        {
            targetDeclarationKind = SyntaxKind.VariableDeclaration;
        }
        else
        {
            targetDeclarationKind = SyntaxKind.Unknown;
        }
        if (targetDeclarationKind != SyntaxKind.Unknown)
        {
            var decl = AstUtilities.GetDeclarationOfKind(resolvedRequire, targetDeclarationKind);
            // function/variable declaration should be ambient
            return decl != null && (decl.Flags & NodeFlags.Ambient) != 0;
        }
        return false;
    }

    // Cross-cluster helpers used by both #10 and #11 (shared; single declaration here).
    // checker.go:14281
    internal Symbol? GetMergedSymbol(Symbol? symbol)
    {
        if (symbol != null)
        {
            var merged = mergedSymbols.GetValueOrDefault(symbol);
            if (merged != null)
            {
                return merged;
            }
        }
        return symbol;
    }

    // checker.go:14302-14307
    internal Symbol? GetSymbolIfSameReference(Symbol s1, Symbol s2)
    {
        if (GetMergedSymbol(ResolveSymbol(GetMergedSymbol(s1))) == GetMergedSymbol(ResolveSymbol(GetMergedSymbol(s2))))
        {
            return s1;
        }
        return null;
    }

    // checker.go:14316
    internal Symbol? GetSymbolOfDeclaration(Node node)
    {
        var symbol = node.Symbol();
        if (symbol != null)
        {
            return GetMergedSymbol(GetLateBoundSymbol(symbol));
        }
        return null;
    }

    // checker.go:14326
    internal Symbol? GetSymbolOfNode(Node node)
    {
        var data = node.DeclarationData();
        if (data != null && data.Symbol != null)
        {
            return GetMergedSymbol(GetLateBoundSymbol(data.Symbol));
        }
        return null;
    }

    // checker.go:14334
    internal Symbol GetLateBoundSymbol(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.ClassMember) == 0 || symbol.Name != InternalSymbolName.Computed)
        {
            return symbol;
        }
        var links = lateBoundLinks.Get(symbol);
        if (links.lateSymbol == null && global::Ts.Core.CoreUtil.Some(symbol.Declarations, HasLateBindableName))
        {
            // force late binding of members/exports. This will set the late-bound symbol
            var parent = GetMergedSymbol(symbol.Parent)!;
            if (global::Ts.Core.CoreUtil.Some(symbol.Declarations, HasStaticModifier))
            {
                GetExportsOfSymbol(parent);
            }
            else
            {
                GetMembersOfSymbol(parent);
            }
        }
        if (links.lateSymbol == null)
        {
            links.lateSymbol = symbol;
        }
        return links.lateSymbol;
    }




    // checker.go:14309
    internal Symbol? GetExportSymbolOfValueSymbolIfExported(Symbol? symbol)
    {
        if (symbol != null && (symbol.Flags & SymbolFlags.ExportValue) != 0 && symbol.ExportSymbol != null)
        {
            symbol = symbol.ExportSymbol;
        }
        return GetMergedSymbol(symbol);
    }

    // SymbolToString / SymbolToStringEx are now REAL in Checker.SymbolToString.cs (printer.go:250/262).

    // checker.go:2119
    internal Node? GetTypeOnlyAliasDeclaration(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            ResolveAliasCore(symbol);
            return aliasSymbolLinks.Get(symbol).typeOnlyDeclaration;
        }
        return null;
    }

    // checker.go:2141
    internal Symbol? GetImmediateAliasedSymbol(Symbol symbol)
    {
        global::Ts.Core.Debug.Assert((symbol.Flags & SymbolFlags.Alias) != 0, "Should only get Alias here.");
        var links = aliasSymbolLinks.Get(symbol);
        if (links.immediateTarget == null)
        {
            var node = GetDeclarationOfAliasSymbol(symbol);
            if (node == null)
            {
                throw new global::System.InvalidOperationException("Unexpected nil in getImmediateAliasedSymbol");
            }
            links.immediateTarget = GetTargetOfAliasDeclaration(node);
        }
        return links.immediateTarget;
    }

    // CombineSymbolTables is now REAL in Checker.Merge.cs (checker.go:14022).

    // checker.go:14269
    internal Symbol CloneSymbol(Symbol symbol)
    {
        var result = NewSymbol(symbol.Flags, symbol.Name);
        // Force reallocation if anything is ever appended to declarations
        result.Declarations = symbol.Declarations is null ? null : symbol.Declarations[..];
        result.Parent = symbol.Parent;
        result.ValueDeclaration = symbol.ValueDeclaration;
        result.Members = symbol.Members is null ? null : new SymbolTable(symbol.Members);
        result.Exports = symbol.Exports is null ? null : new SymbolTable(symbol.Exports);
        RecordMergedSymbol(result, symbol);
        return result;
    }

    // checker.go:13980
    internal bool IsDeprecatedSymbol(Symbol symbol)
    {
        var parentSymbol = GetParentOfSymbol(symbol);
        if (parentSymbol != null && symbol.Declarations.Len() > 1)
        {
            if ((parentSymbol.Flags & SymbolFlags.Interface) != 0)
            {
                return CoreUtil.Some(symbol.Declarations, IsDeprecatedDeclaration);
            }
            else
            {
                return CoreUtil.Every(symbol.Declarations, IsDeprecatedDeclaration);
            }
        }
        return symbol.ValueDeclaration != null && IsDeprecatedDeclaration(symbol.ValueDeclaration) || symbol.Declarations.Len() != 0 && CoreUtil.Every(symbol.Declarations, IsDeprecatedDeclaration);
    }

    // addDeprecatedSuggestion 13897: real body landed in Checker.Deprecation.cs.

    // getCannotFindNameDiagnosticForName (13839) / getResolvedSymbolOrNil (13827): real bodies landed
    // in Checker.OperatorExpressions.cs (#8 wave).



    // checkComputedPropertyName (26667) implemented in Checker.IndexedAccess.cs (#18).

    // checker.go:13911-13916
    internal void AddDiagnostic(Diagnostic diagnostic)
    {
        // Discard diagnostics created while at the maximum number of recursive TypeToString invocations.
        if (serializationLevel < MaxSerializationLevel)
        {
            diagnostics.Add(diagnostic);
        }
    }

    // ===================================================================================
    // Free-function helpers (Go package-level, not Checker methods). In-range ones are real;
    // the rest are minimal stubs (their bodies live in checker/utilities.go — another wave).
    // ===================================================================================

    // utilities.go:211
    private static Node? GetExternalModuleRequireArgument(Node node)
    {
        if (AstUtilities.IsVariableDeclarationInitializedToRequire(node))
        {
            return node.Initializer()!.Arguments()![0];
        }
        return null;
    }

    // checker/utilities.go:184
    private static Node? GetAliasDeclarationFromName(Node node)
    {
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.ImportClause:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.NamespaceExport:
                return node.Parent;
            case SyntaxKind.QualifiedName:
                return GetAliasDeclarationFromName(node.Parent!);
        }
        return null;
    }

    // checker/utilities.go:199-204
    private static Node? GetContainingQualifiedNameNode(Node node)
    {
        while (IsQualifiedName(node.Parent!))
        {
            node = node.Parent!;
        }
        return node;
    }

    // checker/utilities.go:195-197
    private static string EntityNameToString(Node name)
    {
        return AstUtilities.EntityNameToString(name, global::Ts.Scanner.Scanner.GetTextOfNode);
    }

    // utilities.go:1074-1082
    private static Node[]? GetMembersOfDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.TypeLiteral:
                return node.Members();
            case SyntaxKind.ObjectLiteralExpression:
                return node.Properties();
        }
        return null;
    }

    // utilities.go:856
    private static bool IsTypeUsableAsPropertyName(Type t)
    {
        return (t.flags & TypeFlags.StringOrNumberLiteralOrUnique) != 0;
    }

    // utilities.go:863 — Gets the symbolic name for a member from its type.
    private static string GetPropertyNameFromType(Type t)
    {
        if ((t.flags & TypeFlags.StringLiteral) != 0)
        {
            return GetStringLiteralValue(t);
        }
        if ((t.flags & TypeFlags.NumberLiteral) != 0)
        {
            return GetNumberLiteralValue(t).ToString();
        }
        if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
        {
            return t.AsUniqueESSymbolType().name;
        }
        throw new global::System.InvalidOperationException("Unhandled case in getPropertyNameFromType");
    }

    // checker.go:14213
    private static SymbolFlags GetExcludedSymbolFlags(SymbolFlags flags)
    {
        SymbolFlags result = 0;
        if ((flags & SymbolFlags.BlockScopedVariable) != 0) { result |= SymbolFlags.BlockScopedVariableExcludes; }
        if ((flags & SymbolFlags.FunctionScopedVariable) != 0) { result |= SymbolFlags.FunctionScopedVariableExcludes; }
        if ((flags & SymbolFlags.Property) != 0) { result |= SymbolFlags.PropertyExcludes; }
        if ((flags & SymbolFlags.EnumMember) != 0) { result |= SymbolFlags.EnumMemberExcludes; }
        if ((flags & SymbolFlags.Function) != 0) { result |= SymbolFlags.FunctionExcludes; }
        if ((flags & SymbolFlags.Class) != 0) { result |= SymbolFlags.ClassExcludes; }
        if ((flags & SymbolFlags.Interface) != 0) { result |= SymbolFlags.InterfaceExcludes; }
        if ((flags & SymbolFlags.RegularEnum) != 0) { result |= SymbolFlags.RegularEnumExcludes; }
        if ((flags & SymbolFlags.ConstEnum) != 0) { result |= SymbolFlags.ConstEnumExcludes; }
        if ((flags & SymbolFlags.ValueModule) != 0) { result |= SymbolFlags.ValueModuleExcludes; }
        if ((flags & SymbolFlags.Method) != 0) { result |= SymbolFlags.MethodExcludes; }
        if ((flags & SymbolFlags.GetAccessor) != 0) { result |= SymbolFlags.GetAccessorExcludes; }
        if ((flags & SymbolFlags.SetAccessor) != 0) { result |= SymbolFlags.SetAccessorExcludes; }
        if ((flags & SymbolFlags.TypeParameter) != 0) { result |= SymbolFlags.TypeParameterExcludes; }
        if ((flags & SymbolFlags.TypeAlias) != 0) { result |= SymbolFlags.TypeAliasExcludes; }
        if ((flags & SymbolFlags.Alias) != 0) { result |= SymbolFlags.AliasExcludes; }
        if ((flags & SymbolFlags.ReplaceableByMethod) != 0) { result &= ~SymbolFlags.Method; }
        return result;
    }

    // checker.go:14184-14186
    private Diagnostic CreateDiagnosticForNode(Node node, global::Ts.Diagnostics.Message message, params object?[] args) => NewDiagnosticForNode(node, message, args);

    // ===================================================================================
    // Missing ast helpers (absent from Ts.Ast). Minimal local stubs (report to add to ast).
    // ===================================================================================

    // ast/utilities.go:3226 (assert inlined from ast.IsExternalModuleImportEqualsDeclaration, utilities.go:1307)
    private static Node? GetExternalModuleImportEqualsDeclarationExpression(Node node)
    {
        Debug.Assert(node.Kind == SyntaxKind.ImportEqualsDeclaration &&
            node.AsImportEqualsDeclaration().ModuleReference!.Kind == SyntaxKind.ExternalModuleReference);
        return node.AsImportEqualsDeclaration().ModuleReference!.Expression();
    }

    // ast.IsRightSideOfQualifiedNameOrPropertyAccess: real body in AstUtilities (Utilities.Binder.cs, utilities.go:3674).
    private static bool IsRightSideOfQualifiedNameOrPropertyAccess(Node node) => AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(node);

    // internal so the EmitResolver class (separate type) can reach it; body still deferred to the ast wave.
    // ast/utilities.go:2881
    internal static bool IsTypeOnlyImportDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportSpecifier:
                return node.IsTypeOnly() || node.Parent!.Parent!.IsTypeOnly();
            case SyntaxKind.NamespaceImport:
                return node.Parent!.IsTypeOnly();
            case SyntaxKind.ImportClause:
            case SyntaxKind.ImportEqualsDeclaration:
                return node.IsTypeOnly();
        }
        return false;
    }

    // ast/utilities.go:2893
    private static bool IsTypeOnlyExportDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ExportSpecifier:
                return node.IsTypeOnly() || node.Parent!.Parent!.IsTypeOnly();
            case SyntaxKind.ExportDeclaration:
                var d = node.AsExportDeclaration();
                return d.IsTypeOnly && d.ModuleSpecifier != null && d.ExportClause == null;
            case SyntaxKind.NamespaceExport:
                return node.Parent!.IsTypeOnly();
        }
        return false;
    }

    // ast/utilities.go:2906
    internal static bool IsTypeOnlyImportOrExportDeclaration(Node node) =>
        IsTypeOnlyImportDeclaration(node) || IsTypeOnlyExportDeclaration(node);

    // ast/utilities.go:3163 — isPartOfTypeOnlyImportOrExportDeclaration.
    internal static bool IsPartOfTypeOnlyImportOrExportDeclaration(Node node) =>
        FindAncestor(node, IsTypeOnlyImportOrExportDeclaration) != null;

    // checker/utilities.go:1630 — IsExternalModuleSymbol. (Go decodes the first rune; '"' is ASCII, so
    // the UTF-16 first code unit is equivalent, and an empty name yields false either way.)
    public static bool IsExternalModuleSymbol(Symbol moduleSymbol) =>
        (moduleSymbol.Flags & SymbolFlags.Module) != 0 && moduleSymbol.Name.Length > 0 && moduleSymbol.Name[0] == '"';

    // Forwarder: real body is AstUtilities.IsDeclarationNode (Ast/Utilities.Binder.cs, ast/utilities.go).
    private static bool IsDeclarationNode(Node node) => AstUtilities.IsDeclarationNode(node);

    // ast/utilities.go:2488 — implemented by the EmitResolver wave. internal so the EmitResolver
    // class (separate type, faithful to Go's EmitResolver struct) can reach it.
    internal static Node GetFirstIdentifier(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                return node;
            case SyntaxKind.QualifiedName:
                return GetFirstIdentifier(node.AsQualifiedName().Left!);
            case SyntaxKind.PropertyAccessExpression:
                return GetFirstIdentifier(node.AsPropertyAccessExpression().Expression!);
        }
        throw new global::System.InvalidOperationException("Unhandled case in GetFirstIdentifier");
    }

    // ast.GetNameOfDeclaration: real body in AstUtilities (Utilities.Binder.cs, utilities.go:1428).
    private static Node? GetNameOfDeclaration(Node? declaration) => AstUtilities.GetNameOfDeclaration(declaration);

    // ast.IsTypeOfExpression: real body in NodeTests (NodeCasts.g.cs, ast_generated.go:4968).
    private static bool IsTypeOfExpression(Node node) => NodeTests.IsTypeOfExpression(node);

    // Local slice helpers matching Go slices.Compact / slices.Concat semantics on Node[]?.
    private static Node[] SlicesConcat(Node[]? a, Node[]? b)
    {
        var list = new List<Node>(a.Len() + b.Len());
        list.AddRange(a.OrEmpty());
        list.AddRange(b.OrEmpty());
        return list.ToArray();
    }

    private static Node[] SlicesCompact(Node[] a)
    {
        // slices.Compact: remove consecutive duplicates.
        if (a.Length == 0)
        {
            return a;
        }
        var list = new List<Node> { a[0] };
        for (int i = 1; i < a.Length; i++)
        {
            if (a[i] != a[i - 1])
            {
                list.Add(a[i]);
            }
        }
        return list.ToArray();
    }

    private static Node[] AppendDeclaration(Node[]? declarations, Node decl)
    {
        var list = new List<Node>(declarations.Len() + 1);
        list.AddRange(declarations.OrEmpty());
        list.Add(decl);
        return list.ToArray();
    }

    // Shared empty symbol table (Go: reading a nil SymbolTable yields the empty set).
    private static readonly SymbolTable EmptySymbolTable = new();
}

// Null-tolerant slice/map helpers matching Go's nil-slice/nil-map read semantics (plan/05 §6.1).
// PORT-NOTE: Ts.Core.CollectionExtensions.{Len,OrEmpty} are not yet in the tree; provided locally
// for the checker symbol-cluster wave. Remove once the core wave lands the canonical versions.
internal static class SymbolClusterCollectionExtensions
{
    public static int Len<T>(this T[]? a) => a?.Length ?? 0;

    public static T[] OrEmpty<T>(this T[]? a) => a ?? [];

    public static int Len<K, V>(this Dictionary<K, V>? d) where K : notnull => d?.Count ?? 0;
}
