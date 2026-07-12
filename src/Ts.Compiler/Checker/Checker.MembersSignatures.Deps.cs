// PLACEHOLDER STUBS for the Members + Signatures cluster (plan/03 §4.2 #12/#13).
//
// Every member below is a cross-cluster helper that #12/#13 CALL but do not own (they live in #7/#8
// expressions, #10 symbol resolution, #11 symbol types, #14 instantiation, #16 type creation, #17
// union/intersection, #18 indexed access, #19 constraints, #21 contextual, #22 awaited, flow, etc.).
// They had no C# definition at build time, so they are stubbed here (throw) purely to compile the
// cluster. **When the owning cluster lands a real definition, DELETE the corresponding stub here** —
// this file is the single dedup surface for the Members/Signatures wave. Signatures are chosen to
// match the #12/#13 call sites (nullable-tolerant); the owning cluster's real signature is authoritative.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ---- expressions (#7/#8) ----
    // checkExpressionCachedEx (7494) / checkExpressionEx (7534): real bodies landed in
    // Checker.Expressions.cs (#7 wave) — stubs removed from here.
    internal Type CheckExpressionForMutableLocation(Node node, CheckMode checkMode) => throw new NotImplementedException("checkExpressionForMutableLocation");
    internal Type CheckNonNullExpression(Node node) => throw new NotImplementedException("checkNonNullExpression");
    // checkIteratedTypeOrElementType (6074): real body landed in Checker.IterationTypes.cs (iteration wave).
    internal Type CheckDeclarationInitializer(Node declaration, CheckMode checkMode, Type? contextualType) => throw new NotImplementedException("checkDeclarationInitializer");
    internal bool IsConstContext(Node node) => throw new NotImplementedException("isConstContext");
    // isConstantReference: owned by flow.go — real body in Checker.Flow.cs.
    internal Type GetTypeOfExpression(Node node) => throw new NotImplementedException("getTypeOfExpression");
    internal Type GetYieldedTypeOfYieldExpression(Node node, Type yieldExprType, Type sentType, bool isAsync) => throw new NotImplementedException("getYieldedTypeOfYieldExpression");

    // ---- symbol resolution / symbol types (#10/#11) ----
    internal Symbol? GetParentOfSymbol(Symbol symbol) => throw new NotImplementedException("getParentOfSymbol");
    internal Symbol? GetSymbolOfNode(Node node) => throw new NotImplementedException("getSymbolOfNode");
    // symbolIsValueEx / createSymbolWithType implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    internal bool IsNamedMember(Symbol symbol, string id) => throw new NotImplementedException("isNamedMember");
    internal Symbol GetSpreadSymbol(Symbol prop, bool isReadonly) => throw new NotImplementedException("getSpreadSymbol");
    internal ModifierFlags GetDeclarationModifierFlagsFromSymbol(Symbol s) => throw new NotImplementedException("getDeclarationModifierFlagsFromSymbol");
    // getFlowTypeInConstructor: owned by flow.go — real body in Checker.Flow.cs.
    internal Type? GetTypeOfPropertyInBaseClass(Symbol symbol) => throw new NotImplementedException("getTypeOfPropertyInBaseClass");
    // getTypeOfInitializer: owned by flow.go — real body in Checker.Flow.cs.
    internal Node? GetDeclaringConstructor(Symbol symbol) => throw new NotImplementedException("getDeclaringConstructor");
    internal bool IsSpreadableProperty(Symbol prop) => throw new NotImplementedException("isSpreadableProperty");
    // isSymbolAssigned: owned by flow.go — real body in Checker.Flow.cs.
    internal bool IsKnownSymbol(Symbol symbol) => throw new NotImplementedException("IsKnownSymbol");
    // getLiteralTypeFromProperty (26617) / getLiteralTypeFromPropertyName (26640) implemented in Checker.IndexedAccess.cs (#18).
    // getPropertyOfObjectType implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    internal Symbol? GetPropertyOfUnionOrIntersectionType(Type t, string name, bool skipObjectFunctionPropertyAugment) => throw new NotImplementedException("getPropertyOfUnionOrIntersectionType");

    // ---- instantiation (#14) ----
    // getReducedApparentType / instantiateSignatures / instantiateSymbols / instantiateIndexInfos /
    // instantiateTypes / cloneTypeParameter / fillMissingTypeArguments / getMinTypeArgumentCount
    // implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // getBaseConstraintOrType (27294) implemented in Checker.IndexedAccess.cs (#18).
    // newInferenceContext / applyToParameterTypes / applyToReturnTypes / inferTypes / getInferredTypes
    // implemented in Checker.Inference.cs (inference wave). getEffectiveRestType owned by relater.

    // ---- type creation (#16) ----
    internal Type CreateTypeReference(Type target, Type[]? typeArguments) => throw new NotImplementedException("createTypeReference");
    internal Type CloneTypeReference(Type source) => throw new NotImplementedException("cloneTypeReference");

    // ---- union / intersection (#17) ----
    // getUnionTypeEx / getUnionOrIntersectionType / filterType / isEmptyObjectType / isPatternLiteralType
    // (25536/25518/26447/26374/26405) implemented in Checker.UnionIntersection.cs (#17).

    // ---- indexed access / mapped (#18) ----
    // getIndexedAccessType (26789) implemented in Checker.IndexedAccess.cs (#18).
    // sliceTupleType owned by the relater wave (Checker.Relater.cs).
    internal void ResolveMappedTypeMembers(Type t) => throw new NotImplementedException("resolveMappedTypeMembers");
    // resolveReverseMappedTypeMembers (inference.go:1065) implemented in Checker.Inference.cs.
    internal void ResolveUnionTypeMembers(Type t) => throw new NotImplementedException("resolveUnionTypeMembers");
    internal void ResolveIntersectionTypeMembers(Type t) => throw new NotImplementedException("resolveIntersectionTypeMembers");

    // ---- widening / initializer inference ----
    internal Type WidenTypeInferredFromInitializer(Node declaration, Type t) => throw new NotImplementedException("widenTypeInferredFromInitializer");
    // GetWidenedLiteralTypeForInitializer is now REAL in Checker.Widening.cs (checker.go:16810).
    // GetWidenedLiteralLikeTypeForContextualType is now REAL in Checker.Widening.cs (checker.go:25423).
    internal Type GetNonUndefinedType(Type t) => throw new NotImplementedException("getNonUndefinedType");

    // ---- facts / assignability (flow, relater) ----
    internal bool HasTypeFacts(Type t, TypeFacts facts) => throw new NotImplementedException("hasTypeFacts");
    internal Type GetTypeWithFacts(Type t, TypeFacts include) => throw new NotImplementedException("getTypeWithFacts");
    internal Type GetAdjustedTypeWithFacts(Type t, TypeFacts facts) => throw new NotImplementedException("getAdjustedTypeWithFacts");
    // isTypeAssignableTo owned by the relater wave (Checker.Relater.cs).
    // isTypeAssignableToKind (27507) implemented in Checker.Constraints.cs (#19).
    internal bool IsValidSpreadType(Type t) => throw new NotImplementedException("isValidSpreadType");
    // isMatchingReference: owned by flow.go — real body in Checker.Flow.cs.
    internal bool HasDefaultValue(Node node) => throw new NotImplementedException("hasDefaultValue");

    // ---- flow ----
    // getFlowNodeOfNode / getFlowTypeOfReference / getFlowTypeOfReferenceEx / isReachableFlowNode /
    // getDestructuringPropertyName: owned by flow.go — real bodies in Checker.Flow.cs.

    // ---- contextual (#21) ----
    // getContextualReturnType / getContextualSignatureForFunctionLikeDeclaration: real bodies landed in
    // Checker.ContextualType.cs (#21 wave).
    // checker.go:30574 — deep return-mapper instantiation; deferred. PORT-NOTE: signature made nullable to
    // match Go (nil in → nil out) so contextual-type callers pass through cleanly.
    internal Type? InstantiateContextualType(Type? contextualType, Node node, ContextFlags contextFlags) => contextualType;

    // ---- awaited / iteration (#22) ----
    internal Type UnwrapAwaitedType(Type t) => throw new NotImplementedException("unwrapAwaitedType");
    internal Type CheckAwaitedType(Type t, bool withAlias, Node? errorNode, global::Ts.Diagnostics.Message diagnosticMessage) => throw new NotImplementedException("checkAwaitedType");
    internal Type? GetAwaitedTypeNoAlias(Type t) => throw new NotImplementedException("getAwaitedTypeNoAlias");
    internal Type? GetPromisedTypeOfPromise(Type t) => throw new NotImplementedException("getPromisedTypeOfPromise");
    internal Type? GetContextualIterationType(IterationTypeKind kind, Node functionDecl) => throw new NotImplementedException("getContextualIterationType");
    // getIterationTypeOfGeneratorFunctionReturnType (6195) / getIterationTypesOfIterable (6244):
    // real bodies landed in Checker.IterationTypes.cs (iteration wave).
    // isResolvingReturnTypeOfSignature owned by the relater wave (Checker.Relater.cs).

    // ---- signatures / parameters (helpers in adjacent waves) ----
    internal Type GetTypeOfFirstParameterOfSignature(Signature signature) => throw new NotImplementedException("getTypeOfFirstParameterOfSignature");
    // getTypeAtPosition / getRestTypeAtPosition / newTypePredicate owned by the relater wave.
    internal Signature[] GetDefaultConstructSignatures(Type classType) => throw new NotImplementedException("getDefaultConstructSignatures");

    // ---- diagnostics helpers ----
    internal void ErrorOrSuggestion(bool isError, Node? location, global::Ts.Diagnostics.Message? message, params object?[] args) => throw new NotImplementedException("errorOrSuggestion");
    // TypeToStringEx: real implementation in Checker.TypeToString.cs (phase-5 type-display wave).
    // elaborateNeverIntersection implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    internal Diagnostic? NewDiagnosticChainForNode(Diagnostic? details, Node? node, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("NewDiagnosticChainForNode");

    // ---- ast helpers with no C# home yet ----
    internal static Node? GetClassLikeDeclarationOfSymbol(Symbol symbol) => throw new NotImplementedException("ast.GetClassLikeDeclarationOfSymbol");
    internal static Node? GetExtendsHeritageClauseElement(Node node) => throw new NotImplementedException("ast.GetExtendsHeritageClauseElement");
    // GetThisParameter is now REAL in Ts.Ast (AstUtilities, Utilities.Decorators.cs); call sites use
    // AstUtilities.GetThisParameter directly.
    internal static Node? GetSetAccessorValueParameter(Node accessor) => throw new NotImplementedException("GetSetAccessorValueParameter");
    internal static string GetPropertyNameForPropertyNameNode(Node name) => throw new NotImplementedException("ast.GetPropertyNameForPropertyNameNode");
    internal static bool IsImportCall(Node node) => throw new NotImplementedException("ast.IsImportCall");
    internal static bool IsLateBoundName(string name) => throw new NotImplementedException("isLateBoundName");
    internal bool IsCheckJSEnabledForFile(SourceFile sourceFile, CompilerOptions options) => throw new NotImplementedException("ast.IsCheckJSEnabledForFile");
    internal bool ForEachReturnStatement(Node body, Func<Node, bool> visitor) => throw new NotImplementedException("ast.ForEachReturnStatement");
    internal void ForEachYieldExpression(Node body, Func<Node, bool> visitor) => throw new NotImplementedException("forEachYieldExpression");
}
