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
    internal Type CheckExpressionCachedEx(Node node, CheckMode checkMode) => throw new NotImplementedException("checkExpressionCachedEx");
    internal Type CheckExpressionEx(Node node, CheckMode checkMode) => throw new NotImplementedException("checkExpressionEx");
    internal Type CheckExpressionForMutableLocation(Node node, CheckMode checkMode) => throw new NotImplementedException("checkExpressionForMutableLocation");
    internal Type CheckNonNullExpression(Node node) => throw new NotImplementedException("checkNonNullExpression");
    internal Type CheckIteratedTypeOrElementType(IterationUse use, Type inputType, Type sentinelType, Node? errorNode) => throw new NotImplementedException("checkIteratedTypeOrElementType");
    internal Type CheckDeclarationInitializer(Node declaration, CheckMode checkMode, Type? contextualType) => throw new NotImplementedException("checkDeclarationInitializer");
    internal bool IsConstContext(Node node) => throw new NotImplementedException("isConstContext");
    internal bool IsConstantReference(Node node) => throw new NotImplementedException("isConstantReference");
    internal Type GetTypeOfExpression(Node node) => throw new NotImplementedException("getTypeOfExpression");
    internal Type GetYieldedTypeOfYieldExpression(Node node, Type yieldExprType, Type sentType, bool isAsync) => throw new NotImplementedException("getYieldedTypeOfYieldExpression");

    // ---- symbol resolution / symbol types (#10/#11) ----
    internal Symbol? GetParentOfSymbol(Symbol symbol) => throw new NotImplementedException("getParentOfSymbol");
    internal Symbol? GetSymbolOfNode(Node node) => throw new NotImplementedException("getSymbolOfNode");
    // symbolIsValueEx / createSymbolWithType implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    internal bool IsNamedMember(Symbol symbol, string id) => throw new NotImplementedException("isNamedMember");
    internal Symbol GetSpreadSymbol(Symbol prop, bool isReadonly) => throw new NotImplementedException("getSpreadSymbol");
    internal ModifierFlags GetDeclarationModifierFlagsFromSymbol(Symbol s) => throw new NotImplementedException("getDeclarationModifierFlagsFromSymbol");
    internal Type GetFlowTypeInConstructor(Symbol symbol, Node constructor) => throw new NotImplementedException("getFlowTypeInConstructor");
    internal Type? GetTypeOfPropertyInBaseClass(Symbol symbol) => throw new NotImplementedException("getTypeOfPropertyInBaseClass");
    internal Type GetTypeOfInitializer(Node node) => throw new NotImplementedException("getTypeOfInitializer");
    internal Node? GetDeclaringConstructor(Symbol symbol) => throw new NotImplementedException("getDeclaringConstructor");
    internal bool IsSpreadableProperty(Symbol prop) => throw new NotImplementedException("isSpreadableProperty");
    internal bool IsSymbolAssigned(Symbol symbol) => throw new NotImplementedException("isSymbolAssigned");
    internal bool IsKnownSymbol(Symbol symbol) => throw new NotImplementedException("IsKnownSymbol");
    // getLiteralTypeFromProperty (26617) / getLiteralTypeFromPropertyName (26640) implemented in Checker.IndexedAccess.cs (#18).
    // getPropertyOfObjectType implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    internal Symbol? GetPropertyOfUnionOrIntersectionType(Type t, string name, bool skipObjectFunctionPropertyAugment) => throw new NotImplementedException("getPropertyOfUnionOrIntersectionType");

    // ---- instantiation (#14) ----
    // getReducedApparentType / instantiateSignatures / instantiateSymbols / instantiateIndexInfos /
    // instantiateTypes / cloneTypeParameter / fillMissingTypeArguments / getMinTypeArgumentCount
    // implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // getBaseConstraintOrType (27294) implemented in Checker.IndexedAccess.cs (#18).
    internal InferenceContext NewInferenceContext(Type[]? typeParameters, Signature? signature, InferenceFlags flags, TypeComparer? compareTypes) => throw new NotImplementedException("newInferenceContext");
    internal Type? GetEffectiveRestType(Signature signature) => throw new NotImplementedException("getEffectiveRestType");
    internal void ApplyToParameterTypes(Signature source, Signature target, Action<Type, Type> callback) => throw new NotImplementedException("applyToParameterTypes");
    internal void ApplyToReturnTypes(Signature source, Signature target, Action<Type, Type> callback) => throw new NotImplementedException("applyToReturnTypes");
    internal void InferTypes(InferenceInfo[] inferences, Type source, Type target, InferencePriority priority, bool contravariant) => throw new NotImplementedException("inferTypes");
    internal Type[] GetInferredTypes(InferenceContext context) => throw new NotImplementedException("getInferredTypes");

    // ---- type creation (#16) ----
    internal Type CreateTypeReference(Type target, Type[]? typeArguments) => throw new NotImplementedException("createTypeReference");
    internal Type CloneTypeReference(Type source) => throw new NotImplementedException("cloneTypeReference");

    // ---- union / intersection (#17) ----
    // getUnionTypeEx / getUnionOrIntersectionType / filterType / isEmptyObjectType / isPatternLiteralType
    // (25536/25518/26447/26374/26405) implemented in Checker.UnionIntersection.cs (#17).

    // ---- indexed access / mapped (#18) ----
    // getIndexedAccessType (26789) implemented in Checker.IndexedAccess.cs (#18).
    internal Type SliceTupleType(Type t, int index, int endSkipCount) => throw new NotImplementedException("sliceTupleType");
    internal void ResolveMappedTypeMembers(Type t) => throw new NotImplementedException("resolveMappedTypeMembers");
    internal void ResolveReverseMappedTypeMembers(Type t) => throw new NotImplementedException("resolveReverseMappedTypeMembers");
    internal void ResolveUnionTypeMembers(Type t) => throw new NotImplementedException("resolveUnionTypeMembers");
    internal void ResolveIntersectionTypeMembers(Type t) => throw new NotImplementedException("resolveIntersectionTypeMembers");

    // ---- widening / initializer inference ----
    internal Type WidenTypeInferredFromInitializer(Node declaration, Type t) => throw new NotImplementedException("widenTypeInferredFromInitializer");
    internal Type GetWidenedLiteralTypeForInitializer(Node declaration, Type t) => throw new NotImplementedException("getWidenedLiteralTypeForInitializer");
    internal Type? GetWidenedLiteralLikeTypeForContextualType(Type? t, Type? contextualType) => throw new NotImplementedException("getWidenedLiteralLikeTypeForContextualType");
    internal Type GetNonUndefinedType(Type t) => throw new NotImplementedException("getNonUndefinedType");

    // ---- facts / assignability (flow, relater) ----
    internal bool HasTypeFacts(Type t, TypeFacts facts) => throw new NotImplementedException("hasTypeFacts");
    internal Type GetTypeWithFacts(Type t, TypeFacts include) => throw new NotImplementedException("getTypeWithFacts");
    internal Type GetAdjustedTypeWithFacts(Type t, TypeFacts facts) => throw new NotImplementedException("getAdjustedTypeWithFacts");
    internal bool IsTypeAssignableTo(Type source, Type target) => throw new NotImplementedException("isTypeAssignableTo");
    // isTypeAssignableToKind (27507) implemented in Checker.Constraints.cs (#19).
    internal bool IsValidSpreadType(Type t) => throw new NotImplementedException("isValidSpreadType");
    internal bool IsMatchingReference(Node source, Node target) => throw new NotImplementedException("isMatchingReference");
    internal bool HasDefaultValue(Node node) => throw new NotImplementedException("hasDefaultValue");

    // ---- flow ----
    internal FlowNode? GetFlowNodeOfNode(Node node) => throw new NotImplementedException("getFlowNodeOfNode");
    internal Type GetFlowTypeOfReference(Node reference, Type declaredType) => throw new NotImplementedException("getFlowTypeOfReference");
    internal Type GetFlowTypeOfReferenceEx(Node reference, Type declaredType, Type initialType, Node? flowContainer, FlowNode? flowNode) => throw new NotImplementedException("getFlowTypeOfReferenceEx");
    internal bool IsReachableFlowNode(FlowNode flowNode) => throw new NotImplementedException("isReachableFlowNode");
    internal bool GetDestructuringPropertyName(Node node, out string name) => throw new NotImplementedException("getDestructuringPropertyName");

    // ---- contextual (#21) ----
    internal Type? GetContextualReturnType(Node functionDecl, ContextFlags contextFlags) => throw new NotImplementedException("getContextualReturnType");
    internal Signature? GetContextualSignatureForFunctionLikeDeclaration(Node node) => throw new NotImplementedException("getContextualSignatureForFunctionLikeDeclaration");
    internal Type InstantiateContextualType(Type contextualType, Node node, ContextFlags contextFlags) => throw new NotImplementedException("instantiateContextualType");

    // ---- awaited / iteration (#22) ----
    internal Type UnwrapAwaitedType(Type t) => throw new NotImplementedException("unwrapAwaitedType");
    internal Type CheckAwaitedType(Type t, bool withAlias, Node? errorNode, global::Ts.Diagnostics.Message diagnosticMessage) => throw new NotImplementedException("checkAwaitedType");
    internal Type? GetAwaitedTypeNoAlias(Type t) => throw new NotImplementedException("getAwaitedTypeNoAlias");
    internal Type? GetPromisedTypeOfPromise(Type t) => throw new NotImplementedException("getPromisedTypeOfPromise");
    internal Type? GetContextualIterationType(IterationTypeKind kind, Node functionDecl) => throw new NotImplementedException("getContextualIterationType");
    internal Type? GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind kind, Type returnType, bool isAsyncGenerator) => throw new NotImplementedException("getIterationTypeOfGeneratorFunctionReturnType");
    internal IterationTypes GetIterationTypesOfIterable(Type t, IterationUse use, Node? errorNode) => throw new NotImplementedException("getIterationTypesOfIterable");
    internal bool IsResolvingReturnTypeOfSignature(Signature sig) => throw new NotImplementedException("isResolvingReturnTypeOfSignature");

    // ---- signatures / parameters (helpers in adjacent waves) ----
    internal Type GetTypeOfFirstParameterOfSignature(Signature signature) => throw new NotImplementedException("getTypeOfFirstParameterOfSignature");
    internal Type GetTypeAtPosition(Signature signature, int pos) => throw new NotImplementedException("getTypeAtPosition");
    internal Type GetRestTypeAtPosition(Signature source, int pos, bool readonlyElements) => throw new NotImplementedException("getRestTypeAtPosition");
    internal Signature[] GetDefaultConstructSignatures(Type classType) => throw new NotImplementedException("getDefaultConstructSignatures");
    internal TypePredicate NewTypePredicate(TypePredicateKind kind, string parameterName, int parameterIndex, Type? t) => throw new NotImplementedException("newTypePredicate");

    // ---- diagnostics helpers ----
    internal void ErrorOrSuggestion(bool isError, Node? location, global::Ts.Diagnostics.Message? message, params object?[] args) => throw new NotImplementedException("errorOrSuggestion");
    internal string TypeToStringEx(Type t, Node? enclosingDeclaration, TypeFormatFlags flags, object? writer) => throw new NotImplementedException("typeToStringEx");
    // elaborateNeverIntersection implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    internal Diagnostic? NewDiagnosticChainForNode(Diagnostic? details, Node? node, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("NewDiagnosticChainForNode");

    // ---- ast helpers with no C# home yet ----
    internal static Node? GetClassLikeDeclarationOfSymbol(Symbol symbol) => throw new NotImplementedException("ast.GetClassLikeDeclarationOfSymbol");
    internal static Node? GetExtendsHeritageClauseElement(Node node) => throw new NotImplementedException("ast.GetExtendsHeritageClauseElement");
    internal static Node? GetThisParameter(Node signature) => throw new NotImplementedException("ast.GetThisParameter");
    internal static Node? GetSetAccessorValueParameter(Node accessor) => throw new NotImplementedException("GetSetAccessorValueParameter");
    internal static string GetPropertyNameForPropertyNameNode(Node name) => throw new NotImplementedException("ast.GetPropertyNameForPropertyNameNode");
    internal static bool IsImportCall(Node node) => throw new NotImplementedException("ast.IsImportCall");
    internal static bool IsLateBoundName(string name) => throw new NotImplementedException("isLateBoundName");
    internal bool IsCheckJSEnabledForFile(SourceFile sourceFile, CompilerOptions options) => throw new NotImplementedException("ast.IsCheckJSEnabledForFile");
    internal bool ForEachReturnStatement(Node body, Func<Node, bool> visitor) => throw new NotImplementedException("ast.ForEachReturnStatement");
    internal void ForEachYieldExpression(Node body, Func<Node, bool> visitor) => throw new NotImplementedException("forEachYieldExpression");
}
