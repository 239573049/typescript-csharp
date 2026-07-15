// Ported from tsgo internal/checker/checker.go — the ES-module symbol resolution + synthetic-default-import
// machinery (esModuleInterop). resolveESModuleSymbol / getExternalModuleMember are the import/export
// name resolvers; the synthetic-default helpers wrap a CommonJS module's value as a `{ default: ... }`
// object when imported into ESM.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // ast/utilities.go — GetNonAugmentationDeclaration / GetSourceFileOfModule (host is checker because
    // IsExternalModuleAugmentation is a checker-side helper here).
    private static Node? GetNonAugmentationDeclaration(Symbol symbol) =>
        CoreUtil.Find(symbol.Declarations, static d => !IsExternalModuleAugmentation(d) && !AstUtilities.IsGlobalScopeAugmentation(d));

    private static SourceFile? GetSourceFileOfModule(Symbol module)
    {
        var declaration = module.ValueDeclaration ?? GetNonAugmentationDeclaration(module);
        return AstUtilities.GetSourceFileOfNode(declaration);
    }

    // checker/utilities.go:227 — isSyntacticDefault.
    private static bool IsSyntacticDefault(Node node) =>
        (NodeTests.IsExportAssignment(node) && !node.AsExportAssignment().IsExportEquals) ||
        AstUtilities.HasSyntacticModifier(node, ModifierFlags.Default) ||
        NodeTests.IsExportSpecifier(node) ||
        NodeTests.IsNamespaceExport(node);

    // checker/utilities.go:234 — hasExportAssignmentSymbol.
    internal bool HasExportAssignmentSymbol(Symbol moduleSymbol) =>
        moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals) != null;

    // checker.go — resolveExportByName.
    internal Symbol? ResolveExportByName(Symbol moduleSymbol, string name, Node? sourceNode, bool dontResolveAlias)
    {
        var exportValue = moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals);
        Symbol? exportSymbol;
        if (exportValue != null)
        {
            exportSymbol = GetPropertyOfTypeEx(GetTypeOfSymbol(exportValue), name, true /*skipObjectFunctionPropertyAugment*/, false /*includeTypeOnlyMembers*/);
        }
        else
        {
            exportSymbol = moduleSymbol.Exports?.GetValueOrDefault(name);
        }
        var resolved = ResolveSymbolEx(exportSymbol, dontResolveAlias);
        MarkSymbolOfAliasDeclarationIfTypeOnly(sourceNode, null);
        return resolved;
    }

    // checker.go — isOnlyImportableAsDefault.
    internal bool IsOnlyImportableAsDefault(Node? usage, Symbol? resolvedModule)
    {
        // In Node.js, JSON modules don't get named exports.
        if (ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext)
        {
            var usageMode = GetEmitSyntaxForModuleSpecifierExpression(usage!);
            if (usageMode == ModuleKind.ESNext)
            {
                if (resolvedModule == null)
                {
                    resolvedModule = ResolveExternalModuleName(usage!, usage!, true /*ignoreErrors*/);
                }
                SourceFile? targetFile = resolvedModule != null ? GetSourceFileOfModule(resolvedModule) : null;
                return targetFile != null && (AstUtilities.IsJsonSourceFile(targetFile) ||
                    global::Ts.TsPath.TsPath.GetDeclarationFileExtension(targetFile.FileName()) == ".d.json.ts");
            }
        }
        return false;
    }

    // checker.go — createDefaultPropertyWrapperForModule.
    private Type CreateDefaultPropertyWrapperForModule(Symbol symbol, Symbol? originalSymbol, Symbol? anonymousSymbol)
    {
        var memberTable = new SymbolTable();
        var newSymbol = NewSymbol(SymbolFlags.Alias, InternalSymbolName.Default);
        newSymbol.Parent = originalSymbol;
        valueSymbolLinks.Get(newSymbol).nameType = GetStringLiteralType("default");
        aliasSymbolLinks.Get(newSymbol).aliasTarget = ResolveSymbol(symbol);
        memberTable[InternalSymbolName.Default] = newSymbol;
        return NewAnonymousType(anonymousSymbol, memberTable, null, null, null);
    }

    // checker.go — getTypeWithSyntheticDefaultOnly.
    private Type? GetTypeWithSyntheticDefaultOnly(Type? t, Symbol symbol, Symbol? originalSymbol, Node? moduleSpecifier)
    {
        var hasDefaultOnly = IsOnlyImportableAsDefault(moduleSpecifier, null);
        if (hasDefaultOnly && t != null && !IsErrorType(t))
        {
            var key = new CachedTypeKey(CachedTypeKind.DefaultOnlyType, t.id);
            if (cachedTypes.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var result = CreateDefaultPropertyWrapperForModule(symbol, originalSymbol, null);
            cachedTypes[key] = result;
            return result;
        }
        return null;
    }

    // checker.go — cloneTypeAsModuleType.
    private Symbol CloneTypeAsModuleType(Symbol symbol, Type moduleType, Node? referenceParent)
    {
        var result = NewSymbol(symbol.Flags, symbol.Name);
        result.Declarations = symbol.Declarations != null ? (Node[])symbol.Declarations.Clone() : null;
        result.ValueDeclaration = symbol.ValueDeclaration;
        result.Members = symbol.Members != null ? new SymbolTable(symbol.Members) : null;
        result.Exports = symbol.Exports != null ? new SymbolTable(symbol.Exports) : null;
        result.Parent = symbol.Parent;
        var links = exportTypeLinks.Get(result);
        links.target = symbol;
        links.originatingImport = referenceParent;
        var resolvedModuleType = ResolveStructuredTypeMembers(moduleType);
        valueSymbolLinks.Get(result).resolvedType = NewAnonymousType(result, resolvedModuleType.members, null, null, resolvedModuleType.indexInfos);
        return result;
    }

    // checker.go — canHaveSyntheticDefault.
    private bool CanHaveSyntheticDefault(Node? file, Symbol moduleSymbol, bool dontResolveAlias, Node? usage)
    {
        ResolutionMode usageMode = ModuleKind.None;
        if (file != null)
        {
            usageMode = GetEmitSyntaxForModuleSpecifierExpression(usage!);
        }
        if (file != null && usageMode != ModuleKind.None)
        {
            var targetMode = program.GetImpliedNodeFormatForEmit(SourceFileAsHasFileName(file.AsSourceFile()));
            if (usageMode == ModuleKind.ESNext && targetMode == ModuleKind.CommonJS && ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext)
            {
                return true;
            }
            if (usageMode == ModuleKind.ESNext && targetMode == ModuleKind.ESNext)
            {
                return false;
            }
            if (targetMode == ModuleKind.None && file.AsSourceFile().IsDeclarationFile)
            {
                if (program.GetRedirectForResolution(SourceFileAsHasFileName(file.AsSourceFile())) != null || program.GetProjectReferenceFromOutputDts(file.AsSourceFile().Path()) != null)
                {
                    var targetModuleKind = program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(file.AsSourceFile()));
                    if (usageMode == ModuleKind.ESNext && ModuleKind.ES2015 <= targetModuleKind && targetModuleKind <= ModuleKind.ESNext)
                    {
                        return false;
                    }
                }
            }
        }
        // Declaration files (and ambient modules).
        if (file == null || file.AsSourceFile().IsDeclarationFile)
        {
            var defaultExportSymbol = ResolveExportByName(moduleSymbol, InternalSymbolName.Default, null /*sourceNode*/, true);
            if (defaultExportSymbol != null && CoreUtil.Some(defaultExportSymbol.Declarations, IsSyntacticDefault))
            {
                return false;
            }
            if (ResolveExportByName(moduleSymbol, "__esModule", null /*sourceNode*/, dontResolveAlias) != null)
            {
                return false;
            }
            return true;
        }
        // TypeScript files never have a synthetic default unless they contain an export= statement.
        if (!AstUtilities.IsInJSFile(file))
        {
            return HasExportAssignmentSymbol(moduleSymbol);
        }
        // JS files: synthetic default if no ES2015+ module syntax and no __esModule marker.
        return (file.AsSourceFile().ExternalModuleIndicator == null || file.AsSourceFile().ExternalModuleIndicator == file) &&
            ResolveExportByName(moduleSymbol, "__esModule", null /*sourceNode*/, dontResolveAlias) == null;
    }

    // checker.go — getTypeWithSyntheticDefaultImportType.
    private Type? GetTypeWithSyntheticDefaultImportType(Type? t, Symbol symbol, Symbol originalSymbol, Node? moduleSpecifier)
    {
        if (t != null && !IsErrorType(t))
        {
            var key = new CachedTypeKey(CachedTypeKind.SyntheticType, t.id);
            if (cachedTypes.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var file = CoreUtil.Find(originalSymbol.Declarations, NodeTests.IsSourceFile);
            var hasSyntheticDefault = CanHaveSyntheticDefault(file, originalSymbol, false /*dontResolveAlias*/, moduleSpecifier);
            Type? syntheticType;
            if (hasSyntheticDefault)
            {
                var anonymousSymbol = NewSymbol(SymbolFlags.TypeLiteral, InternalSymbolName.Type);
                var defaultContainingObject = CreateDefaultPropertyWrapperForModule(symbol, originalSymbol, anonymousSymbol);
                valueSymbolLinks.Get(anonymousSymbol).resolvedType = defaultContainingObject;
                if (IsValidSpreadType(t))
                {
                    syntheticType = GetSpreadType(t, defaultContainingObject, anonymousSymbol, ObjectFlags.None, false /*readonly*/);
                }
                else
                {
                    syntheticType = defaultContainingObject;
                }
            }
            else
            {
                syntheticType = t;
            }
            cachedTypes[key] = syntheticType;
            return syntheticType;
        }
        return t;
    }

    // checker.go:15486 — resolveESModuleSymbol.
    internal Symbol? ResolveESModuleSymbol(Symbol? moduleSymbol, Node node, Node? moduleSpecifier)
    {
        var symbol = ResolveExternalModuleSymbol(moduleSymbol, true /*dontResolveAlias*/);
        if (AstUtilities.IsNonLocalAlias(symbol, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace))
        {
            symbol = GetMergedSymbol(ResolveIndirectionAlias(GetSymbolOfDeclaration(node)!, symbol!));
        }
        if (symbol != null && moduleSpecifier != null)
        {
            var referenceParent = moduleSpecifier.Parent!;
            Node? namespaceImport = null;
            if (NodeTests.IsImportDeclaration(referenceParent))
            {
                namespaceImport = global::Ts.Transformers.TransformerAstAdapters.GetNamespaceDeclarationNode(referenceParent);
            }
            if (namespaceImport != null || AstUtilities.IsImportCall(referenceParent))
            {
                Node reference = AstUtilities.IsImportCall(referenceParent)
                    ? referenceParent.Arguments()![0]
                    : referenceParent.ModuleSpecifier()!;
                var typ = GetTypeOfSymbol(symbol);
                var defaultOnlyType = GetTypeWithSyntheticDefaultOnly(typ, symbol, moduleSymbol!, reference);
                if (defaultOnlyType != null)
                {
                    return CloneTypeAsModuleType(symbol, defaultOnlyType, referenceParent);
                }
                var targetFile = CoreUtil.Find(moduleSymbol!.Declarations, NodeTests.IsSourceFile);
                var usageMode = GetEmitSyntaxForModuleSpecifierExpression(reference);
                Symbol? exportModuleDotExportsSymbol = null;
                if (namespaceImport != null && targetFile != null &&
                    ModuleKind.Node20 <= moduleKind && moduleKind <= ModuleKind.NodeNext &&
                    usageMode == ModuleKind.CommonJS &&
                    program.GetImpliedNodeFormatForEmit(SourceFileAsHasFileName(targetFile.AsSourceFile())) == ModuleKind.ESNext)
                {
                    exportModuleDotExportsSymbol = GetExportOfModule(symbol, InternalSymbolName.ModuleExports, namespaceImport, true /*dontResolveAlias*/);
                }
                if (exportModuleDotExportsSymbol != null)
                {
                    if (TypeHasCallOrConstructSignatures(typ))
                    {
                        return CloneTypeAsModuleType(exportModuleDotExportsSymbol, typ, referenceParent);
                    }
                    return exportModuleDotExportsSymbol;
                }
                var isEsmCjsRef = targetFile != null && IsESMFormatImportImportingCommonjsFormatFile(usageMode, program.GetImpliedNodeFormatForEmit(SourceFileAsHasFileName(targetFile.AsSourceFile())));
                if (TypeHasCallOrConstructSignatures(typ) || GetPropertyOfTypeEx(typ, InternalSymbolName.Default, true /*skipObjectFunctionPropertyAugment*/, false /*includeTypeOnlyMembers*/) != null || isEsmCjsRef)
                {
                    Type moduleType;
                    if ((typ.flags & TypeFlags.StructuredType) != 0)
                    {
                        moduleType = GetTypeWithSyntheticDefaultImportType(typ, symbol, moduleSymbol!, reference)!;
                    }
                    else
                    {
                        moduleType = CreateDefaultPropertyWrapperForModule(symbol, symbol.Parent, null);
                    }
                    return CloneTypeAsModuleType(symbol, moduleType, referenceParent);
                }
            }
        }
        return symbol;
    }

    // checker.go:14593 — getExternalModuleMember.
    internal Symbol? GetExternalModuleMember(Node node, Node specifier, bool dontResolveAlias)
    {
        var moduleSpecifier = GetExternalModuleRequireArgument(node) ?? global::Ts.Printer.PrinterUtilities.GetExternalModuleName(node);
        var moduleSymbol = ResolveExternalModuleName(node, moduleSpecifier, false /*ignoreErrors*/);
        Node? name = !NodeTests.IsPropertyAccessExpression(specifier) ? specifier.PropertyNameOrName() : specifier.Name();
        if (name == null || (!NodeTests.IsIdentifier(name) && !NodeTests.IsStringLiteral(name)))
        {
            return null;
        }
        var nameText = name.Text();
        var targetSymbol = ResolveESModuleSymbol(moduleSymbol, specifier, moduleSpecifier);
        if (targetSymbol != null)
        {
            if (nameText != "" || name.Kind == SyntaxKind.StringLiteral)
            {
                if (IsShorthandAmbientModuleSymbol(moduleSymbol!))
                {
                    return moduleSymbol;
                }
                Symbol? symbolFromVariable;
                if (moduleSymbol != null && moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals) != null)
                {
                    symbolFromVariable = GetPropertyOfTypeEx(GetTypeOfSymbol(targetSymbol), nameText, true /*skipObjectFunctionPropertyAugment*/, false /*includeTypeOnlyMembers*/);
                }
                else
                {
                    symbolFromVariable = GetPropertyOfVariable(targetSymbol, nameText);
                }
                symbolFromVariable = ResolveSymbolEx(symbolFromVariable, dontResolveAlias);
                var exportContainer = targetSymbol;
                if (moduleSymbol != null && moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals) != null)
                {
                    exportContainer = moduleSymbol;
                }
                var symbolFromModule = GetExportOfModule(exportContainer, nameText, specifier, dontResolveAlias);
                if (symbolFromModule == null && nameText == InternalSymbolName.Default)
                {
                    var file = CoreUtil.Find(moduleSymbol!.Declarations, NodeTests.IsSourceFile);
                    if (IsOnlyImportableAsDefault(moduleSpecifier, moduleSymbol) || CanHaveSyntheticDefault(file, moduleSymbol, dontResolveAlias, moduleSpecifier))
                    {
                        symbolFromModule = ResolveExternalModuleSymbol(moduleSymbol, dontResolveAlias);
                        if (symbolFromModule == null)
                        {
                            symbolFromModule = ResolveSymbolEx(moduleSymbol, dontResolveAlias);
                        }
                    }
                }
                var symbol = symbolFromVariable;
                if (symbolFromModule != null)
                {
                    symbol = symbolFromModule;
                    if (symbolFromVariable != null)
                    {
                        symbol = CombineValueAndTypeSymbols(symbolFromVariable, symbolFromModule);
                    }
                }
                if (AstUtilities.IsImportOrExportSpecifier(specifier) && IsOnlyImportableAsDefault(moduleSpecifier, moduleSymbol) && nameText != InternalSymbolName.Default)
                {
                    Error(name, Diag.Named_imports_from_a_JSON_file_into_an_ECMAScript_module_are_not_allowed_when_module_is_set_to_0, moduleKind.ToString());
                }
                else if (symbol == null)
                {
                    ErrorNoModuleMemberSymbol(moduleSymbol!, targetSymbol, node, name);
                }
                return symbol;
            }
        }
        return null;
    }

    // checker.go — errorNoModuleMemberSymbol.
    private void ErrorNoModuleMemberSymbol(Symbol moduleSymbol, Symbol targetSymbol, Node node, Node name)
    {
        if (compilerOptions.NoCheck.IsTrue())
        {
            return;
        }
        var moduleName = GetFullyQualifiedName(moduleSymbol, node);
        var declarationName = global::Ts.Scanner.Scanner.DeclarationNameToString(name);
        Symbol? suggestion = null;
        if (NodeTests.IsIdentifier(name))
        {
            suggestion = GetSuggestedSymbolForNonexistentModule(name, targetSymbol);
        }
        if (suggestion != null)
        {
            var suggestionName = SymbolToString(suggestion);
            var diagnostic = Error(name, Diag.X_0_has_no_exported_member_named_1_Did_you_mean_2, moduleName, declarationName, suggestionName);
            if (suggestion.ValueDeclaration != null)
            {
                diagnostic.AddRelatedInfo(CreateDiagnosticForNode(suggestion.ValueDeclaration, Diag.X_0_is_declared_here, suggestionName));
            }
        }
        else
        {
            if (moduleSymbol.Exports?.GetValueOrDefault(InternalSymbolName.Default) != null)
            {
                Error(name, Diag.Module_0_has_no_exported_member_1_Did_you_mean_to_use_import_1_from_0_instead, moduleName, declarationName);
            }
            else
            {
                ReportNonExportedMember(name, declarationName, moduleSymbol, moduleName);
            }
        }
    }

    // checker.go — isESMFormatImportImportingCommonjsFormatFile.
    private static bool IsESMFormatImportImportingCommonjsFormatFile(ResolutionMode usageMode, ResolutionMode targetMode) =>
        usageMode == ModuleKind.ESNext && targetMode == ModuleKind.CommonJS;
}
