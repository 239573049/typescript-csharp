// Checker logic stubs (skeleton wave). plan/03 §4.2 #4–#22: the check*/get*/resolve*/instantiate*
// surface is generated as NotImplementedException stubs (real signatures, `throw`) to be filled by
// later clustered waves — mirroring the parser bring-up. This file carries (a) the stubs actually
// REFERENCED by the real skeleton (construction / keys / mappers / name resolver), which must exist
// for the assembly to compile, and (b) a representative set of the section entry-point functions
// anchored per §4.2 partial. It is not an exhaustive transcription of every checker.go function.

using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ===== Referenced by the real skeleton (must exist to compile) =====

    // Free functions used by Types.cs / TypeMapper.cs / Keys.cs.
    // isTupleType (checker.go:23397) implemented in Checker.TypeNodes.cs (#15).
    internal static bool IsThisTypeParameter(Type type) => throw new NotImplementedException("isThisTypeParameter");

    internal static void ClearCachedInferences(InferenceInfo[] inferences) => throw new NotImplementedException("clearCachedInferences");

    // NameResolver closures (createNameResolver / createNameResolverForSuggestion).
    internal Symbol? GetSymbolOfDeclaration(Node node) => throw new NotImplementedException("getSymbolOfDeclaration");

    internal Diagnostic? Error(Node? location, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("error");

    internal Symbol? GetSymbol(SymbolTable? symbols, string name, SymbolFlags meaning) => throw new NotImplementedException("getSymbol");

    internal Symbol? GetSuggestionForSymbolNameLookup(SymbolTable? symbols, string name, SymbolFlags meaning) => throw new NotImplementedException("getSuggestionForSymbolNameLookup");

    internal bool CheckAndReportErrorForInvalidInitializer(Node? errorLocation, string name, Node? propertyWithInvalidInitializer, Symbol? result) => throw new NotImplementedException("checkAndReportErrorForInvalidInitializer");

    internal void OnFailedToResolveSymbol(Node? errorLocation, string name, SymbolFlags meaning, global::Ts.Diagnostics.Message? nameNotFoundMessage) => throw new NotImplementedException("onFailedToResolveSymbol");

    internal void OnSuccessfullyResolvedSymbol(Node? errorLocation, Symbol symbol, SymbolFlags meaning, Node? lastLocation, Node? associatedDeclarationForContainingInitializerOrBindingName, bool withinDeferredContext) => throw new NotImplementedException("onSuccessfullyResolvedSymbol");

    // Constructor closures.
    internal int CompareSymbolsWorker(Symbol a, Symbol b) => throw new NotImplementedException("compareSymbolsWorker");

    internal int CompareSymbolChainsWorker(Symbol[] a, Symbol[] b) => throw new NotImplementedException("compareSymbolChainsWorker");

    // evaluateEntity (23932), getUniqueLiteralTypeForTypeParameter (24853), restrictiveMapperWorker
    // (24433), permissiveMapperWorker (24440) implemented in Checker.TypeNodes.cs (#15).
    internal bool IsEmptyAnonymousObjectType(Type t) => throw new NotImplementedException("IsEmptyAnonymousObjectType");

    internal bool CouldContainTypeVariablesWorker(Type t) => throw new NotImplementedException("couldContainTypeVariablesWorker");

    internal bool IsStringIndexSignatureOnlyTypeWorker(Type t) => throw new NotImplementedException("isStringIndexSignatureOnlyTypeWorker");

    internal bool MarkNodeAssignmentsWorker(Node node) => throw new NotImplementedException("markNodeAssignmentsWorker");

    internal Ternary CompareTypesAssignableWorker(Type source, Type target, bool reportErrors) => throw new NotImplementedException("compareTypesAssignableWorker");

    // Mapper / key back-references.
    internal Type InstantiateType(Type t, TypeMapper? mapper) => throw new NotImplementedException("instantiateType");

    internal void InferFromIntraExpressionSites(InferenceContext context) => throw new NotImplementedException("inferFromIntraExpressionSites");

    internal Type GetInferredType(InferenceContext context, int index) => throw new NotImplementedException("getInferredType");

    internal Type MapType(Type t, Func<Type, Type> mapper) => throw new NotImplementedException("mapType");

    internal Type[] GetTypeArguments(Type t) => throw new NotImplementedException("getTypeArguments");

    // ===== #3 construction (deep global resolution, reached only via memoized resolvers) =====

    internal Type GetGlobalType(string name, int arity, bool reportErrors) => throw new NotImplementedException("getGlobalType");

    internal Symbol? GetGlobalTypeAliasSymbol(string name, int arity, bool reportErrors) => throw new NotImplementedException("getGlobalTypeAliasSymbol");

    // ===== #5 statements / source elements =====

    internal void CheckSourceFile(SourceFile node) => throw new NotImplementedException("checkSourceFile");

    internal void CheckSourceElement(Node? node) => throw new NotImplementedException("checkSourceElement");

    // ===== #6 unused identifiers =====

    internal void RegisterForUnusedIdentifiersCheck(Node node) => throw new NotImplementedException("registerForUnusedIdentifiersCheck");

    // ===== #7 expressions =====

    internal Type CheckExpression(Node node) => throw new NotImplementedException("checkExpression");

    internal Type CheckExpressionCached(Node node) => throw new NotImplementedException("checkExpressionCached");

    // ===== #8 operator expressions =====

    internal Type CheckBinaryExpression(Node node, CheckMode checkMode) => throw new NotImplementedException("checkBinaryExpression");

    internal Symbol GetResolvedSymbol(Node node) => throw new NotImplementedException("getResolvedSymbol");

    // ===== #9 diagnostics =====

    internal Diagnostic[] GetDiagnostics(SourceFile? sourceFile) => throw new NotImplementedException("GetDiagnostics");

    // Aggregated by the checker pool. Empty for the skeleton (no checking is performed).
    public Diagnostic[] GetGlobalDiagnostics() => [];

    // ===== #10 symbol resolution =====

    internal Symbol? ResolveSymbol(Symbol? symbol) => throw new NotImplementedException("resolveSymbol");

    // ===== #11 symbol types =====
    // getDeclaredTypeOfSymbol (checker.go:23578) is in the #15 line-range (22713-24910), implemented
    // in Checker.TypeNodes.cs despite the #11 label above (§4.2 is authoritative).

    // ===== #12 members + #13 signatures =====
    // Implemented in Checker.Members.cs / Checker.Signatures.cs (getPropertiesOfType,
    // getPropertyOfType, getSignaturesOfType, getSignatureFromDeclaration, getReturnTypeOfSignature).

    // ===== #14 instantiation =====

    internal Symbol InstantiateSymbol(Symbol symbol, TypeMapper mapper) => throw new NotImplementedException("instantiateSymbol");

    internal Type GetApparentType(Type type) => throw new NotImplementedException("getApparentType");

    // ===== #15 type nodes =====
    // getTypeFromTypeNode and family implemented in Checker.TypeNodes.cs.

    // ===== #17 union / intersection =====

    internal Type GetIntersectionType(Type[] types) => throw new NotImplementedException("getIntersectionType");

    internal Type GetReducedType(Type type) => throw new NotImplementedException("getReducedType");

    // ===== #18 indexed access =====

    internal Type GetIndexType(Type type, IndexFlags indexFlags) => throw new NotImplementedException("getIndexType");

    // ===== #19 constraints =====

    internal Type? GetBaseConstraintOfType(Type type) => throw new NotImplementedException("getBaseConstraintOfType");

    // ===== #21 contextual types =====

    internal Type? GetContextualType(Node node, ContextFlags contextFlags) => throw new NotImplementedException("getContextualType");

    // ===== #22 awaited types =====

    internal Type GetAwaitedTypeEx(Type type, Node? errorNode, global::Ts.Diagnostics.Message diagnosticMessage) => throw new NotImplementedException("getAwaitedTypeEx");
}
