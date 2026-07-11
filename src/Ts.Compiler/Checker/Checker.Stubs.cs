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

    // clearCachedInferences (inference.go:1609) implemented in Checker.Inference.cs (inference wave).

    // NameResolver closures (createNameResolver / createNameResolverForSuggestion).
    internal Symbol? GetSymbolOfDeclaration(Node node) => throw new NotImplementedException("getSymbolOfDeclaration");

    internal Diagnostic? Error(Node? location, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("error");

    internal Symbol? GetSymbol(SymbolTable? symbols, string name, SymbolFlags meaning) => throw new NotImplementedException("getSymbol");

    internal Symbol? GetSuggestionForSymbolNameLookup(SymbolTable? symbols, string name, SymbolFlags meaning) => throw new NotImplementedException("getSuggestionForSymbolNameLookup");

    internal bool CheckAndReportErrorForInvalidInitializer(Node? errorLocation, string name, Node? propertyWithInvalidInitializer, Symbol? result) => throw new NotImplementedException("checkAndReportErrorForInvalidInitializer");

    internal void OnFailedToResolveSymbol(Node? errorLocation, string name, SymbolFlags meaning, global::Ts.Diagnostics.Message? nameNotFoundMessage) => throw new NotImplementedException("onFailedToResolveSymbol");

    internal void OnSuccessfullyResolvedSymbol(Node? errorLocation, Symbol symbol, SymbolFlags meaning, Node? lastLocation, Node? associatedDeclarationForContainingInitializerOrBindingName, bool withinDeferredContext) => throw new NotImplementedException("onSuccessfullyResolvedSymbol");

    // Constructor closures.
    // compareSymbolsWorker (utilities.go:343) implemented in Checker.TypeOps.Support.cs (needed by CompareTypes).
    internal int CompareSymbolChainsWorker(Symbol[] a, Symbol[] b) => throw new NotImplementedException("compareSymbolChainsWorker");

    // evaluateEntity (23932), getUniqueLiteralTypeForTypeParameter (24853), restrictiveMapperWorker
    // (24433), permissiveMapperWorker (24440) implemented in Checker.TypeNodes.cs (#15).
    // IsEmptyAnonymousObjectType (26365) implemented in Checker.UnionIntersection.cs (#17).
    // IsStringIndexSignatureOnlyTypeWorker (27231) implemented in Checker.IndexedAccess.cs (#18).
    // couldContainTypeVariablesWorker / instantiateType / getTypeArguments implemented by the #14
    // Instantiation wave (Checker.Instantiation.cs).
    // markNodeAssignmentsWorker: owned by flow.go — real body in Checker.Flow.cs.

    // compareTypesAssignableWorker owned by the relater wave (Checker.Relater.cs).

    // Mapper / key back-references.
    // inferFromIntraExpressionSites (inference.go:1268) / getInferredType (inference.go:1283)
    // implemented in Checker.Inference.cs (inference wave).

    internal Type MapType(Type t, Func<Type, Type> mapper) => throw new NotImplementedException("mapType");

    // ===== #3 construction (deep global resolution, reached only via memoized resolvers) =====

    internal Type GetGlobalType(string name, int arity, bool reportErrors) => throw new NotImplementedException("getGlobalType");

    internal Symbol? GetGlobalTypeAliasSymbol(string name, int arity, bool reportErrors) => throw new NotImplementedException("getGlobalTypeAliasSymbol");

    // ===== #5 statements / source elements =====
    // CheckSourceFile / CheckSourceElement implemented in Checker.Statements.cs (#5).

    // ===== #6 unused identifiers =====

    internal void RegisterForUnusedIdentifiersCheck(Node node) => throw new NotImplementedException("registerForUnusedIdentifiersCheck");

    // ===== #7 expressions =====
    // Dispatch trunk landed in Checker.Expressions.cs; these are the canonical entry wrappers
    // (checker.go:7530 checkExpression, 7490 checkExpressionCached).

    internal Type CheckExpression(Node node) => CheckExpressionCore(node);

    internal Type CheckExpressionCached(Node node) => CheckExpressionCachedEx(node, CheckMode.Normal);

    // ===== #8 operator expressions =====
    // Real bodies landed in Checker.OperatorExpressions.cs (checkBinaryExpression 12293,
    // getResolvedSymbol 13814).

    internal Type CheckBinaryExpression(Node node, CheckMode checkMode) => CheckBinaryExpressionCore(node, checkMode);

    internal Symbol GetResolvedSymbol(Node node) => GetResolvedSymbolCore(node);

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
    // instantiateSymbol / getApparentType / getReducedType implemented by the #14 Instantiation wave.

    // ===== #15 type nodes =====
    // getTypeFromTypeNode and family implemented in Checker.TypeNodes.cs.

    // ===== #17 union / intersection =====
    // getIntersectionType (25946) implemented in Checker.UnionIntersection.cs (#17).

    // ===== #18 indexed access =====
    // getIndexType/getIndexTypeEx (26551/26555) implemented in Checker.IndexedAccess.cs (#18).

    // ===== #19 constraints =====
    // getBaseConstraintOfType (27302) implemented in Checker.Constraints.cs (#19).

    // ===== #21 contextual types =====

    internal Type? GetContextualType(Node node, ContextFlags contextFlags) => throw new NotImplementedException("getContextualType");

    // ===== #22 awaited types =====

    internal Type GetAwaitedTypeEx(Type type, Node? errorNode, global::Ts.Diagnostics.Message diagnosticMessage) => throw new NotImplementedException("getAwaitedTypeEx");
}
