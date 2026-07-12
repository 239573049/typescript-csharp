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
    // checker.go:4399
    internal void CheckTypeParameterListsIdentical(Symbol symbol) => throw new NotImplementedException("checkTypeParameterListsIdentical");
    // checker.go:3128
    internal void CheckObjectTypeForDuplicateDeclarations(Node node, bool checkPrivateNames) => throw new NotImplementedException("checkObjectTypeForDuplicateDeclarations");
    // checker.go:4376
    internal void CheckClassForStaticPropertyNameConflicts(Node node) => throw new NotImplementedException("checkClassForStaticPropertyNameConflicts");
    // checker.go:4464
    internal void CheckBaseTypeAccessibility(Type t, Node node) => throw new NotImplementedException("checkBaseTypeAccessibility");
    // checker.go:3004
    internal bool CheckTypeArgumentConstraints(Node node, Type[]? typeParameters) => throw new NotImplementedException("checkTypeArgumentConstraints");
    // checker.go:4477
    internal void IssueMemberSpecificError(Node node, Type typeWithThis, Type baseWithThis, global::Ts.Diagnostics.Message broadDiag) => throw new NotImplementedException("issueMemberSpecificError");
    // checker.go:4502
    internal Type GetTypeWithoutSignatures(Type t) => throw new NotImplementedException("getTypeWithoutSignatures");
    // checker.go:4519
    internal void CheckKindsOfPropertyMemberOverrides(Type t, Type baseType) => throw new NotImplementedException("checkKindsOfPropertyMemberOverrides");
    // checker.go:4687
    internal void CheckMembersForOverrideModifier(Node node, Type t, Type typeWithThis, Type staticType) => throw new NotImplementedException("checkMembersForOverrideModifier");
    // checker.go:4769
    internal void CheckIndexConstraints(Type t, Symbol symbol, bool isStaticIndex) => throw new NotImplementedException("checkIndexConstraints");
    // checker.go:4879
    internal void CheckClassOrInterfaceForDuplicateIndexSignatures(Node node) => throw new NotImplementedException("checkClassOrInterfaceForDuplicateIndexSignatures");
    // checker.go:4916
    internal void CheckPropertyInitialization(Node node) => throw new NotImplementedException("checkPropertyInitialization");
    // checker.go:5017
    internal bool CheckInheritedPropertiesAreIdentical(Type t, Node? typeNode) => throw new NotImplementedException("checkInheritedPropertiesAreIdentical");
    // checker.go:4887
    internal void CheckTypeForDuplicateIndexSignatures(Node node) => throw new NotImplementedException("checkTypeForDuplicateIndexSignatures");
    // checker.go:4427
    internal bool AreTypeParametersIdentical(Node[] declarations, Type[] targetParameters, Func<Node, Node[]> getTypeParameterDeclarations) => throw new NotImplementedException("areTypeParametersIdentical");

    // ---- erasable-syntax / isolated-modules flags (options-driven checks, deferred) ----
    // checker.go:2638
    internal bool ShouldCheckErasableSyntax(Node node) => throw new NotImplementedException("shouldCheckErasableSyntax");
    // checker.go:5217
    internal string GetIsolatedModulesLikeFlagName() => throw new NotImplementedException("getIsolatedModulesLikeFlagName");

    // ---- module augmentation / import-export resolution deep branches (symbol-resolution, deferred) ----
    // checker.go:5221
    internal void CheckModuleAugmentationElement(Node node) => throw new NotImplementedException("checkModuleAugmentationElement");
    // checker.go:5316
    internal bool CheckExternalImportOrExportDeclaration(Node node) => throw new NotImplementedException("checkExternalImportOrExportDeclaration");
    // checker.go:5359
    internal void CheckImportBinding(Node node) => throw new NotImplementedException("checkImportBinding");
    // checker.go:14726
    internal bool IsOnlyImportableAsDefault(Node? usage, Symbol? resolvedModule) => throw new NotImplementedException("isOnlyImportableAsDefault");
    // checker.go:15040
    internal Symbol? ResolveExternalModuleNameWorker(Node location, Node? moduleReferenceExpression, global::Ts.Diagnostics.Message? moduleNotFoundError, bool ignoreErrors, bool isForAugmentation) => throw new NotImplementedException("resolveExternalModuleNameWorker");
    // checker.go:5391
    internal void CheckImportAttributes(Node declaration) => throw new NotImplementedException("checkImportAttributes");
    // checker.go:5534
    internal void CheckExportSpecifier(Node node) => throw new NotImplementedException("checkExportSpecifier");
    // checker.go:5371
    internal void CheckModuleExportName(Node? name, bool allowStringLiteral) => throw new NotImplementedException("checkModuleExportName");
    // checker.go:2129
    internal Node? GetTypeOnlyAliasDeclarationEx(Symbol symbol, SymbolFlags meaning) => throw new NotImplementedException("getTypeOnlyAliasDeclarationEx");
    // checker.go:2154
    internal Diagnostic? AddTypeOnlyDeclarationRelatedInfo(Diagnostic? diagnostic, Node? typeOnlyDeclaration, string name) => throw new NotImplementedException("addTypeOnlyDeclarationRelatedInfo");
    // checker.go:5717
    internal bool HasExportedMembersOfKind(Symbol moduleSymbol, SymbolFlags kind) => throw new NotImplementedException("hasExportedMembersOfKind");
    // checker.go:5726
    internal bool HasShadowedNamespace(Symbol symbol) => throw new NotImplementedException("hasShadowedNamespace");
    // checker.go:6938
    internal DeclarationSpaces GetDeclarationSpaces(Node node) => throw new NotImplementedException("getDeclarationSpaces");
    // checker.go:3687
    internal ModifierFlags GetEffectiveDeclarationFlags(Node n, ModifierFlags flagsToCheck) => throw new NotImplementedException("getEffectiveDeclarationFlags");

    // ---- private-identifier / accessor / async-return-type checks (deferred) ----
    // checker.go:10503
    internal void SetNodeLinksForPrivateIdentifierScope(Node node) => throw new NotImplementedException("setNodeLinksForPrivateIdentifierScope");
    // checker.go:12249
    internal bool ClassDeclarationExtendsNull(Node classDecl) => throw new NotImplementedException("classDeclarationExtendsNull");
    // checker.go:2877
    internal Node? FindFirstSuperCall(Node node) => throw new NotImplementedException("findFirstSuperCall");
    // checker.go:2770
    internal void CheckAsyncFunctionReturnType(Node node, Node returnTypeNode) => throw new NotImplementedException("checkAsyncFunctionReturnType");
    // checker.go:29454
    internal bool CheckGeneratorInstantiationAssignabilityToReturnType(Type returnType, FunctionFlags functionFlags, Node? errorNode) => throw new NotImplementedException("checkGeneratorInstantiationAssignabilityToReturnType");

    // ---- JSDoc parameter matching / type-predicate parent (deferred) ----
    // checker.go:6999
    internal void CheckTypeParametersNotReferenced(Node root, Node[] typeParameters, int index) => throw new NotImplementedException("checkTypeParametersNotReferenced");
    internal void CheckUnmatchedJSDocParameters(Node node) => throw new NotImplementedException("checkUnmatchedJSDocParameters");
    // checker.go:3085
    internal Node? GetTypePredicateParent(Node node) => throw new NotImplementedException("getTypePredicateParent");
    // checker.go:3097
    internal bool CheckIfTypePredicateVariableIsDeclaredInBindingPattern(Node pattern, Node predicateVariableNode, string predicateVariableName) => throw new NotImplementedException("checkIfTypePredicateVariableIsDeclaredInBindingPattern");

    // ---- type-node deep helpers (type-nodes/indexed-access, deferred) ----
    // checker.go:3318
    internal ResolutionMode GetResolutionModeOverride(ImportAttributes node, bool reportErrors) => throw new NotImplementedException("getResolutionModeOverride");
    // checker.go:2986
    internal void CheckTypeReferenceOrImport(Node node) => throw new NotImplementedException("checkTypeReferenceOrImport");
    // checker.go:8197
    internal Type CheckIndexedAccessIndexType(Type t, Node accessNode) => throw new NotImplementedException("checkIndexedAccessIndexType");

    // ---- package-level ast/checker helpers not yet landed on a host class (deferred) ----
    // utilities.go:64
    internal static ModifierFlags GetSelectedModifierFlags(Node node, ModifierFlags flags) => throw new NotImplementedException("getSelectedModifierFlags");
    // checker.go:2894
    internal static bool IsInstancePropertyWithInitializerOrPrivateIdentifierProperty(Node n) => throw new NotImplementedException("isInstancePropertyWithInitializerOrPrivateIdentifierProperty");
    // checker.go:2898
    internal static bool SuperCallIsRootLevelInConstructor(Node superCall, Node body) => throw new NotImplementedException("superCallIsRootLevelInConstructor");
    // checker.go:2903
    internal static bool NodeImmediatelyReferencesSuperOrThis(Node node) => throw new NotImplementedException("nodeImmediatelyReferencesSuperOrThis");
    // utilities.go:1237
    internal static Node[] GetDeclarationsOfKind(Symbol symbol, SyntaxKind kind) => throw new NotImplementedException("getDeclarationsOfKind");
    // checker.go:5208
    internal static Node? GetFirstNonAmbientClassOrFunctionDeclaration(Symbol symbol) => throw new NotImplementedException("getFirstNonAmbientClassOrFunctionDeclaration");
    // utilities.go:223
    internal static bool IsTopLevelInExternalModuleAugmentation(Node node) => throw new NotImplementedException("isTopLevelInExternalModuleAugmentation");
    // checker.go:5735
    internal static bool IsNotOverload(Node node) => throw new NotImplementedException("isNotOverload");
    // utilities.go:234
    internal bool HasExportAssignmentSymbol(Symbol moduleSymbol) => throw new NotImplementedException("hasExportAssignmentSymbol");
    // checker.go:5384
    internal static bool HasTypeJsonImportAttribute(Node node) => throw new NotImplementedException("hasTypeJsonImportAttribute");
    // GetVerbatimModuleSyntaxErrorMessage (checker.go:5660) already stubbed in Checker.Grammar.Deps.cs.
    // utilities.go:1070
    internal static bool IsSuperCall(Node n) => throw new NotImplementedException("isSuperCall");
    // nodebuilderimpl.go:1187
    internal static Node? TryGetModuleSpecifierFromDeclaration(Node node) => throw new NotImplementedException("TryGetModuleSpecifierFromDeclaration");
    // ast/utilities.go:4054
    internal static bool IsImportOrImportEqualsDeclaration(Node node) => throw new NotImplementedException("ast.IsImportOrImportEqualsDeclaration");
    // ast/utilities.go:4113
    internal static bool HasModifier(Node node, ModifierFlags flags) => throw new NotImplementedException("ast.HasModifier");
    // ast/utilities.go:1705
    internal static Node[] GetImplementsHeritageClauseElements(Node node) => throw new NotImplementedException("ast.GetImplementsHeritageClauseElements");
    // ast/utilities.go:3548
    internal static bool IsExternalModuleAugmentation(Node node) => throw new NotImplementedException("ast.IsExternalModuleAugmentation");
    // ast/utilities.go:2442 — implemented by the EmitResolver wave (markLinkedAliases dependency).
    internal static bool IsInternalModuleImportEqualsDeclaration(Node node) =>
        NodeTests.IsImportEqualsDeclaration(node) &&
        node.AsImportEqualsDeclaration().ModuleReference!.Kind != SyntaxKind.ExternalModuleReference;
    // checker.go: module kind display for diagnostics (ModuleKind.String())
    internal static string ModuleKindToString(ModuleKind moduleKind) => throw new NotImplementedException("ModuleKind.String");
    // scanner.go:2516 — scanner.ScanTokenAtPosition not yet landed on the Scanner host.
    internal static SyntaxKind ScanTokenAtPosition(SourceFile sourceFile, int pos) => throw new NotImplementedException("scanner.ScanTokenAtPosition");
}
