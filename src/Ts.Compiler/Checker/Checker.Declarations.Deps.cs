// Single dedup surface for the complex-declaration validator wave (Checker.Declarations.cs).
// Every method here is CALLED by a validator in Checker.Declarations.cs but its own landing belongs to
// a not-yet-ported cluster (full heritage/override-conflict analysis, index-constraint checks, the
// alias/import-export resolution deep branches, deferred JSDoc-parameter matching, iteration-types
// helpers, and a handful of package-level ast utilities). Each appears exactly once here (no CS0111
// duplicates elsewhere in the assembly); helpers already stubbed in another cluster's Deps surface
// (e.g. AddRelatedInfo in Checker.Statements.Deps.cs, ErrorOrNil, GetExtendsHeritageClauseElement*,
// GetFirstIdentifier, IsTypeOnlyImportOrExportDeclaration) are NOT re-declared here.
//
// §1.5: diagnostics via `using Diag = ...`; cross-package classes shadowed by sibling namespaces are
// `global::`-qualified where referenced.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // ---- class/interface heritage + override + index-constraint analysis (members/type-ops, deferred) ----
    // CheckTypeParameterListsIdentical is now REAL in Checker.Declarations.cs (checker.go:4399).
    // CheckObjectTypeForDuplicateDeclarations + ReportDuplicateMemberErrors are now REAL in Checker.Declarations.cs (checker.go:3128).
    // CheckClassForStaticPropertyNameConflicts is now REAL in Checker.Declarations.cs (checker.go:4376).
    // CheckBaseTypeAccessibility is now REAL in Checker.Declarations.cs (checker.go:4464).
    // checker.go:3004
    internal bool CheckTypeArgumentConstraints(Node node, Type[]? typeParameters)
    {
        Type[]? typeArguments = null;
        TypeMapper? mapper = null;
        var result = true;
        for (var i = 0; i < (typeParameters?.Length ?? 0); i++)
        {
            var typeParameter = typeParameters![i];
            var constraint = GetConstraintOfTypeParameter(typeParameter);
            if (constraint != null)
            {
                if (typeArguments == null)
                {
                    typeArguments = GetEffectiveTypeArguments(node, typeParameters);
                    mapper = TypeMapper.NewTypeMapper(typeParameters, typeArguments);
                }
                result = result && CheckTypeAssignableTo(typeArguments[i], InstantiateType(constraint, mapper), CoreUtil.ElementOrNil(node.TypeArguments(), i), Diag.Type_0_does_not_satisfy_the_constraint_1);
            }
        }
        return result;
    }
    // IssueMemberSpecificError is now REAL in Checker.Declarations.cs (checker.go:4477).
    // GetTypeWithoutSignatures is now REAL in Checker.Declarations.cs (checker.go:4502).
    // checker.go:4519
    // CheckKindsOfPropertyMemberOverrides is now REAL in Checker.Declarations.cs (checker.go:4519).
    // CheckMembersForOverrideModifier + CheckMemberForOverrideModifier + GetSuggestedSymbolForNonexistentClassMember are now REAL in Checker.Declarations.cs (checker.go:4687).
    // CheckIndexConstraints + CheckIndexConstraintForProperty + CheckIndexConstraintForIndexSignature are now REAL in Checker.Declarations.cs (checker.go:4769).
    // CheckClassOrInterfaceForDuplicateIndexSignatures + CheckTypeForDuplicateIndexSignatures are now REAL in Checker.Declarations.cs (checker.go:4879).
    // CheckPropertyInitialization + IsPropertyInitializedInStaticBlocks + IsPropertyInitializedInConstructor are now REAL in Checker.Declarations.cs (checker.go:4916).
    // checker.go:5017 — an interface extending 2+ bases must inherit identical declarations of any
    // same-named property (accessibility/optionality/type identity via compareProperties).
    internal bool CheckInheritedPropertiesAreIdentical(Type t, Node? typeNode)
    {
        var baseTypes = GetBaseTypes(t);
        if (baseTypes == null || baseTypes.Length < 2)
        {
            return true;
        }
        var seen = new Dictionary<string, InheritanceInfo>(global::System.StringComparer.Ordinal);
        var declaredMembers = ResolveDeclaredMembers(t).declaredMembers;
        if (declaredMembers != null)
        {
            // map-iter: 序无关 — keyed inserts by unique member name; no order-sensitive output.
            foreach (var (id, p) in declaredMembers)
            {
                if (IsNamedMember(p, id))
                {
                    seen[p.Name] = new InheritanceInfo(p, t);
                }
            }
        }
        var identical = true;
        foreach (var baseType in baseTypes)
        {
            var properties = GetPropertiesOfType(GetTypeWithThisArgument(baseType, t.AsInterfaceType()!.thisType, false));
            foreach (var prop in properties)
            {
                if (!seen.TryGetValue(prop.Name, out var existing))
                {
                    seen[prop.Name] = new InheritanceInfo(prop, baseType);
                }
                else
                {
                    var isInheritedProperty = existing.containingType != t;
                    if (isInheritedProperty && !IsPropertyIdenticalTo(existing.prop, prop))
                    {
                        identical = false;
                        var typeName1 = TypeToString(existing.containingType);
                        var typeName2 = TypeToString(baseType);
                        var errorInfo = NewDiagnosticForNode(typeNode!, Diag.Named_property_0_of_types_1_and_2_are_not_identical, SymbolToString(prop), typeName1, typeName2);
                        AddDiagnostic(Diagnostic.NewDiagnosticChain(errorInfo, Diag.Interface_0_cannot_simultaneously_extend_types_1_and_2, TypeToString(t), typeName1, typeName2));
                    }
                }
            }
        }
        return identical;
    }

    // checker.go InheritanceInfo
    private readonly struct InheritanceInfo(Symbol prop, Type containingType)
    {
        internal readonly Symbol prop = prop;
        internal readonly Type containingType = containingType;
    }
    // AreTypeParametersIdentical is now REAL in Checker.Declarations.cs (checker.go:4427).

    // ---- erasable-syntax / isolated-modules flags (options-driven checks, deferred) ----
    // checker.go:2638
    // checker.go:2638-2640
    internal bool ShouldCheckErasableSyntax(Node node) => compilerOptions.ErasableSyntaxOnly.IsTrue() && !AstUtilities.IsInJSFile(node);
    // checker.go:5217
    internal string GetIsolatedModulesLikeFlagName() => throw new NotImplementedException("getIsolatedModulesLikeFlagName");

    // ---- module augmentation / import-export resolution deep branches (symbol-resolution, deferred) ----
    // checker.go:5221
    internal void CheckModuleAugmentationElement(Node node) => throw new NotImplementedException("checkModuleAugmentationElement");
    // checker.go:5316
    internal bool CheckExternalImportOrExportDeclaration(Node node)
    {
        var moduleName = global::Ts.Printer.PrinterUtilities.GetExternalModuleName(node);
        if (moduleName is null || AstUtilities.NodeIsMissing(moduleName))
        {
            // Should be a parse error.
            return false;
        }
        if (!NodeTests.IsStringLiteral(moduleName))
        {
            Error(moduleName, Diag.String_literal_expected);
            return false;
        }
        var inAmbientExternalModule = NodeTests.IsModuleBlock(node.Parent!) && AstUtilities.IsAmbientModule(node.Parent!.Parent!);
        if (!NodeTests.IsSourceFile(node.Parent!) && !inAmbientExternalModule)
        {
            Error(moduleName, NodeTests.IsExportDeclaration(node)
                ? Diag.Export_declarations_are_not_permitted_in_a_namespace
                : Diag.Import_declarations_in_a_namespace_cannot_reference_a_module);
            return false;
        }
        if (inAmbientExternalModule && global::Ts.TsPath.TsPath.IsExternalModuleNameRelative(moduleName.Text()))
        {
            // we have already reported errors on top level imports/exports in external module augmentations in checkModuleDeclaration
            // no need to do this again.
            if (!IsTopLevelInExternalModuleAugmentation(node))
            {
                // TypeScript 1.0 spec (April 2013): 12.1.6
                // An ExternalImportDeclaration in an AmbientExternalModuleDeclaration may reference
                // other external modules only through top - level external module names.
                // Relative external module names are not permitted.
                Error(node, Diag.Import_or_export_declaration_in_an_ambient_module_declaration_cannot_reference_module_through_relative_module_name);
                return false;
            }
        }
        if (!NodeTests.IsImportEqualsDeclaration(node))
        {
            var attributes = GetImportAttributes(node);
            if (attributes is not null)
            {
                var hasError = false;
                foreach (var attr in attributes.AsImportAttributes().Attributes!.Nodes)
                {
                    if (!NodeTests.IsStringLiteral(attr.AsImportAttribute().Value!))
                    {
                        hasError = true;
                        Error(attr.AsImportAttribute().Value, Diag.Import_attribute_values_must_be_string_literal_expressions);
                    }
                }
                return !hasError;
            }
        }
        return true;
    }

    // ast/utilities.go:1906
    internal static Node? GetImportAttributes(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
                return node.AsImportDeclaration().Attributes;
            case SyntaxKind.ExportDeclaration:
                return node.AsExportDeclaration().Attributes;
        }
        throw new InvalidOperationException("Unhandled case in getImportAttributes");
    }
    // checker.go:5359
    internal void CheckImportBinding(Node node)
    {
        CheckCollisionsForDeclarationName(node, node.Name());
        CheckAliasSymbol(node);
        if (NodeTests.IsImportSpecifier(node))
        {
            CheckModuleExportName(node.PropertyName(), true /*allowStringLiteral*/);
            if (AstUtilities.ModuleExportNameIsDefault(node.PropertyNameOrName()!) &&
                program.GetEmitModuleFormatOfFile(SourceFileAsHasFileName(AstUtilities.GetSourceFileOfNode(node)!)) == ModuleKind.CommonJS)
            {
                CheckExternalEmitHelpers(node, ExternalEmitHelpers.ImportDefault);
            }
        }
    }
    // checker.go:14726
    internal bool IsOnlyImportableAsDefault(Node? usage, Symbol? resolvedModule) => throw new NotImplementedException("isOnlyImportableAsDefault");
    // checker.go:15040 resolveExternalModuleNameWorker: real body landed in Checker.SymbolResolution.cs.
    // checker.go:5391
    internal void CheckImportAttributes(Node declaration) => throw new NotImplementedException("checkImportAttributes");
    // checker.go:5534
    internal void CheckExportSpecifier(Node node) => throw new NotImplementedException("checkExportSpecifier");
    // checker.go:5371
    internal void CheckModuleExportName(Node? name, bool allowStringLiteral)
    {
        if (name == null || name.Kind != SyntaxKind.StringLiteral)
        {
            return;
        }
        if (!allowStringLiteral)
        {
            GrammarErrorOnNode(name, Diag.Identifier_expected);
        }
        else if (moduleKind == ModuleKind.ES2015 || moduleKind == ModuleKind.ES2020)
        {
            if (!AstUtilities.GetSourceFileOfNode(name)!.IsDeclarationFile)
            {
                GrammarErrorOnNode(name, Diag.String_literal_import_and_export_names_are_not_supported_when_the_module_flag_is_set_to_es2015_or_es2020);
            }
        }
    }
    // checker.go:2129 — Return the first type-only alias declaration node (if any) in the resolution
    // chain that affects the symbol for the given meaning
    internal Node? GetTypeOnlyAliasDeclarationEx(Symbol symbol, SymbolFlags meaning)
    {
        while ((symbol.Flags & SymbolFlags.Alias) != 0 && (symbol.Flags & meaning) == 0)
        {
            var resolved = ResolveAliasCore(symbol);
            var links = aliasSymbolLinks.Get(symbol);
            if (links.typeOnlyDeclaration != null)
            {
                return links.typeOnlyDeclaration;
            }
            symbol = resolved;
        }
        return null;
    }
    // checker.go:2154-2160
    internal Diagnostic? AddTypeOnlyDeclarationRelatedInfo(Diagnostic? diagnostic, Node? typeOnlyDeclaration, string name)
    {
        if (typeOnlyDeclaration is null)
        {
            return diagnostic;
        }
        var isExport = NodeTests.IsExportSpecifier(typeOnlyDeclaration) || NodeTests.IsExportDeclaration(typeOnlyDeclaration) || NodeTests.IsNamespaceExport(typeOnlyDeclaration);
        return diagnostic!.AddRelatedInfo(NewDiagnosticForNode(typeOnlyDeclaration, CoreUtil.IfElse(isExport, Diag.X_0_was_exported_here, Diag.X_0_was_imported_here), name));
    }
    // checker.go:5717
    internal bool HasExportedMembersOfKind(Symbol moduleSymbol, SymbolFlags kind) => throw new NotImplementedException("hasExportedMembersOfKind");
    // checker.go:5726
    internal bool HasShadowedNamespace(Symbol symbol) => throw new NotImplementedException("hasShadowedNamespace");
    // checker.go:6938-6977
    internal DeclarationSpaces GetDeclarationSpaces(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.JSDocTypedefTag:
            case SyntaxKind.JSDocCallbackTag:
                return DeclarationSpaces.ExportType;
            case SyntaxKind.ModuleDeclaration:
                if (AstUtilities.IsAmbientModule(node) || AstUtilities.GetModuleInstanceState(node) != ModuleInstanceState.NonInstantiated)
                {
                    return DeclarationSpaces.ExportNamespace | DeclarationSpaces.ExportValue;
                }
                return DeclarationSpaces.ExportNamespace;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.EnumMember:
                return DeclarationSpaces.ExportType | DeclarationSpaces.ExportValue;
            case SyntaxKind.SourceFile:
                return DeclarationSpaces.ExportType | DeclarationSpaces.ExportValue | DeclarationSpaces.ExportNamespace;
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.BinaryExpression:
                {
                    Node expression;
                    if (NodeTests.IsExportAssignment(node))
                    {
                        expression = node.Expression()!;
                    }
                    else
                    {
                        expression = node.AsBinaryExpression().Right!;
                    }
                    // Export assigned entity name expressions act as aliases and should fall through, otherwise they export values.
                    if (!AstUtilities.IsEntityNameExpression(expression) || (GetSymbolOfDeclaration(node)!.Flags & SymbolFlags.Alias) == 0)
                    {
                        return DeclarationSpaces.ExportValue;
                    }
                    // The below options all declare an Alias, which is allowed to merge with other values within the importing module.
                    goto case SyntaxKind.ImportEqualsDeclaration;
                }
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.ImportClause:
                {
                    var result = DeclarationSpaces.None;
                    var target = ResolveAliasCore(GetSymbolOfDeclaration(node)!);
                    foreach (var d in target.Declarations ?? [])
                    {
                        result |= GetDeclarationSpaces(d);
                    }
                    return result;
                }
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.BindingElement:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ImportSpecifier:
                return DeclarationSpaces.ExportValue;
            case SyntaxKind.MethodSignature:
            case SyntaxKind.PropertySignature:
                return DeclarationSpaces.ExportType;
        }
        throw new global::System.InvalidOperationException("Unhandled case in getDeclarationSpaces: " + node.Kind);
    }
    // checker.go:3687
    // getEffectiveDeclarationFlags 3687: real body landed in Checker.FunctionOrConstructorSymbol.cs.

    // ---- private-identifier / accessor / async-return-type checks (deferred) ----
    // SetNodeLinksForPrivateIdentifierScope is now REAL in Checker.Declarations.cs (checker.go:10503).
    // ClassDeclarationExtendsNull is now REAL in Checker.Expressions.cs (checker.go:12253).
    // checker.go:2877
    internal Node? FindFirstSuperCall(Node node)
    {
        Node? superCall = null;
        bool Visit(Node n)
        {
            if (IsSuperCall(n))
            {
                superCall = n;
                return true;
            }
            if (AstUtilities.IsFunctionLike(n))
            {
                return false;
            }
            return n.ForEachChild(Visit);
        }
        Visit(node);
        return superCall;
    }
    // checker.go:2770 — checkAsyncFunctionReturnType: real body landed in Checker.Declarations.cs.
    // checker.go:29454
    internal bool CheckGeneratorInstantiationAssignabilityToReturnType(Type returnType, FunctionFlags functionFlags, Node? errorNode) => throw new NotImplementedException("checkGeneratorInstantiationAssignabilityToReturnType");

    // ---- JSDoc parameter matching / type-predicate parent (deferred) ----
    // checker.go:6998-7016 — Check that type parameter defaults only reference previously declared type parameters */
    internal void CheckTypeParametersNotReferenced(Node root, Node[] typeParameters, int index)
    {
        Visitor visit = null!;
        visit = (node) =>
        {
            if (NodeTests.IsTypeReferenceNode(node))
            {
                var t = GetTypeFromTypeReference(node);
                if ((t.flags & TypeFlags.TypeParameter) != 0)
                {
                    for (var i = index; i < typeParameters.Length; i++)
                    {
                        if (t.symbol == GetSymbolOfDeclaration(typeParameters[i]))
                        {
                            Error(node, Diag.Type_parameter_defaults_can_only_reference_previously_declared_type_parameters);
                        }
                    }
                }
            }
            return node.ForEachChild(visit);
        };
        visit(root);
    }
    // checkUnmatchedJSDocParameters (jsdoc.go:9) landed in Checker.JSDoc.cs; its JS-file branch calls
    // containsArgumentsReference (checker.go:31875), real below.
    // checker.go:31875
    internal bool ContainsArgumentsReference(Node node)
    {
        if (node.Body() == null)
        {
            return false;
        }

        if (cachedArgumentsReferenced.TryGetValue(node, out var cached))
        {
            return cached;
        }

        Func<Node?, bool> visit = null!;
        visit = n =>
        {
            if (n == null)
            {
                return false;
            }
            switch (n.Kind)
            {
                case SyntaxKind.Identifier:
                    return n.Text() == argumentsSymbol.Name && IsArgumentsSymbol(GetResolvedSymbol(n));
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                    if (NodeTests.IsComputedPropertyName(n.Name()!))
                    {
                        return visit(n.Name());
                    }
                    break;
                case SyntaxKind.PropertyAccessExpression:
                case SyntaxKind.ElementAccessExpression:
                    return visit(n.Expression());
                case SyntaxKind.PropertyAssignment:
                    return visit(n.Initializer());
            }
            if (NodeStartsNewLexicalEnvironment(n) || AstUtilities.IsPartOfTypeNode(n))
            {
                return false;
            }
            return n.ForEachChild(child => visit(child));
        };

        var containsArguments = visit(node.Body());
        cachedArgumentsReferenced[node] = containsArguments;
        return containsArguments;
    }
    // services.go:229
    internal bool IsArgumentsSymbol(Symbol symbol) => symbol == argumentsSymbol;
    // checker/utilities.go:1686
    internal static bool NodeStartsNewLexicalEnvironment(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.SourceFile:
                return true;
        }
        return false;
    }
    // checker.go:3085
    internal Node? GetTypePredicateParent(Node node) => throw new NotImplementedException("getTypePredicateParent");
    // checker.go:3097
    internal bool CheckIfTypePredicateVariableIsDeclaredInBindingPattern(Node pattern, Node predicateVariableNode, string predicateVariableName) => throw new NotImplementedException("checkIfTypePredicateVariableIsDeclaredInBindingPattern");

    // ---- type-node deep helpers (type-nodes/indexed-access, deferred) ----
    // checker.go:3318
    internal ResolutionMode GetResolutionModeOverride(ImportAttributes node, bool reportErrors) => throw new NotImplementedException("getResolutionModeOverride");
    // checker.go:2986
    internal void CheckTypeReferenceOrImport(Node node)
    {
        var t = GetTypeFromTypeNode(node);
        if (!IsErrorType(t))
        {
            if (node.TypeArguments().Len() != 0)
            {
                var typeParameters = GetTypeParametersForTypeReferenceOrImport(node);
                if (typeParameters.Len() != 0)
                {
                    CheckTypeArgumentConstraints(node, typeParameters!);
                }
            }
            var symbol = GetResolvedSymbolOrNil(node);
            if (symbol != null)
            {
                if (CoreUtil.Some(symbol.Declarations, d => AstUtilities.IsTypeDeclaration(d) && IsDeprecatedDeclaration(d)))
                {
                    AddDeprecatedSuggestion(GetDeprecatedSuggestionNode(node), symbol.Declarations!, symbol.Name);
                }
            }
        }
    }

    // checker.go:13959
    internal bool IsDeprecatedDeclaration(Node declaration) =>
        AstUtilities.IsDeprecatedDeclarationWithCachedFlags(declaration, GetCombinedNodeFlagsCached(declaration));
    // checker.go:3021 — deprecated suggestion target; deferred. Reached only on the deprecated path above.
    internal Node? GetDeprecatedSuggestionNode(Node node) => throw new NotImplementedException("getDeprecatedSuggestionNode");
    // checker.go:8197 — checkIndexedAccessIndexType: real body landed in Checker.Expressions.cs.

    // ---- package-level ast/checker helpers not yet landed on a host class (deferred) ----
    // utilities.go:64
    // utilities.go:64 — getSelectedModifierFlags.
    internal static ModifierFlags GetSelectedModifierFlags(Node node, ModifierFlags flags) => node.ModifierFlags() & flags;
    // IsInstancePropertyWithInitializerOrPrivateIdentifierProperty is now REAL in Checker.Declarations.cs (checker.go:2894).
    // SuperCallIsRootLevelInConstructor is now REAL in Checker.Declarations.cs (checker.go:2898).
    // NodeImmediatelyReferencesSuperOrThis is now REAL in Checker.Declarations.cs (checker.go:2903).
    // utilities.go:1237
    internal static Node[] GetDeclarationsOfKind(Symbol symbol, SyntaxKind kind) =>
        CoreUtil.Filter(symbol.Declarations, d => d.Kind == kind) ?? [];
    // checker.go:5208
    internal static Node? GetFirstNonAmbientClassOrFunctionDeclaration(Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if ((NodeTests.IsClassDeclaration(declaration) || (NodeTests.IsFunctionDeclaration(declaration) && AstUtilities.NodeIsPresent(declaration.Body()))) && (declaration.Flags & NodeFlags.Ambient) == 0)
            {
                return declaration;
            }
        }
        return null;
    }
    // utilities.go:223
    internal static bool IsTopLevelInExternalModuleAugmentation(Node? node) =>
        node is not null && node.Parent is not null &&
        NodeTests.IsModuleBlock(node.Parent) && IsExternalModuleAugmentation(node.Parent.Parent!);
    // checker.go:5735
    internal static bool IsNotOverload(Node node) =>
        !NodeTests.IsFunctionDeclaration(node) && !NodeTests.IsMethodDeclaration(node) || node.Body() != null;
    // utilities.go:234
    internal bool HasExportAssignmentSymbol(Symbol moduleSymbol) => throw new NotImplementedException("hasExportAssignmentSymbol");
    // checker.go:5384
    internal static bool HasTypeJsonImportAttribute(Node node) => throw new NotImplementedException("hasTypeJsonImportAttribute");
    // GetVerbatimModuleSyntaxErrorMessage (checker.go:5660) already stubbed in Checker.Grammar.Deps.cs.
    // utilities.go:1070
    // utilities.go:1070 — isSuperCall.
    internal static bool IsSuperCall(Node n) => NodeTests.IsCallExpression(n) && n.Expression()!.Kind == SyntaxKind.SuperKeyword;
    // nodebuilderimpl.go:1187
    internal static Node? TryGetModuleSpecifierFromDeclaration(Node node) => throw new NotImplementedException("TryGetModuleSpecifierFromDeclaration");
    // ast/utilities.go:4054
    internal static bool IsImportOrImportEqualsDeclaration(Node node) => throw new NotImplementedException("ast.IsImportOrImportEqualsDeclaration");
    // ast/utilities.go:4113
    internal static bool HasModifier(Node node, ModifierFlags flags) => (node.ModifierFlags() & flags) != 0;
    // ast/utilities.go:1705 — delegates to the ast utility.
    internal static Node[] GetImplementsHeritageClauseElements(Node node) => AstUtilities.GetImplementsHeritageClauseElements(node);
    // ast/utilities.go:3548
    internal static bool IsExternalModuleAugmentation(Node node) =>
        AstUtilities.IsAmbientModule(node) && AstUtilities.IsModuleAugmentationExternal(node);
    // ast/utilities.go:2442 — implemented by the EmitResolver wave (markLinkedAliases dependency).
    internal static bool IsInternalModuleImportEqualsDeclaration(Node node) =>
        NodeTests.IsImportEqualsDeclaration(node) &&
        node.AsImportEqualsDeclaration().ModuleReference!.Kind != SyntaxKind.ExternalModuleReference;
    // checker.go: module kind display for diagnostics (ModuleKind.String())
    internal static string ModuleKindToString(ModuleKind moduleKind) => throw new NotImplementedException("ModuleKind.String");
    // scanner.go:2516 — scanner.ScanTokenAtPosition (positions a fresh scanner at pos and returns the token kind).
    internal static SyntaxKind ScanTokenAtPosition(SourceFile sourceFile, int pos) =>
        global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, pos).Token();
}
