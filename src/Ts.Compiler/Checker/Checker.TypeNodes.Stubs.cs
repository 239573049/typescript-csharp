// Cross-cluster method stubs referenced by the TypeNodes cluster (plan/03 §4.2 #15) that are not
// yet implemented (or stubbed) by their owning waves (#14 instantiation / #16 type creation /
// #17 union-intersection / #18 indexed-access / conditional / relations / contextual / mapped-type).
// All throw NotImplementedException — call sites compile; deep runtime paths run only when those
// waves land. Each owning wave should delete its stub here when it lands the real implementation.
// Stubs already provided by Checker.MembersSignatures.Deps.cs (a sibling dependency file) are NOT
// duplicated here (createTypeReference / getUnionTypeEx / getIndexedAccessType(Ex) /
// newInferenceContext / inferTypes / getMinTypeArgumentCount / fillMissingTypeArguments /
// getSymbolOfNode / getParentOfSymbol / isTypeAssignableTo / isPatternLiteralType / TypeToStringEx).

using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // #16 type creation
    internal Type CreateTypeReferenceEx(Type target, Type[]? typeArguments, ObjectFlags newObjectFlags) => throw new NotImplementedException("createTypeReferenceEx");

    internal Type CreateDeferredTypeReference(Type target, Node node, TypeMapper? mapper, TypeAlias? alias) => throw new NotImplementedException("createDeferredTypeReference");

    internal Type NewConditionalType(ConditionalRoot root, TypeMapper? mapper, TypeMapper? combinedMapper) => throw new NotImplementedException("newConditionalType");

    internal Type GetEnumLiteralType(object? value, Symbol enumSymbol, Symbol symbol) => throw new NotImplementedException("getEnumLiteralType");

    internal Type GetSubstitutionType(Type baseType, Type constraint) => throw new NotImplementedException("getSubstitutionType");

    internal Type GetStringMappingType(Symbol symbol, Type type) => throw new NotImplementedException("getStringMappingType");

    // #17 union / intersection
    internal Type GetIntersectionTypeEx(Type[]? types, IntersectionFlags flags, TypeAlias? alias) => throw new NotImplementedException("getIntersectionTypeEx");

    internal bool CheckCrossProductUnion(Type[] types) => throw new NotImplementedException("checkCrossProductUnion");

    // #18 keyof (1-arg wrapper)
    internal Type GetIndexType(Type type) => throw new NotImplementedException("getIndexType");

    // #18 getIndexedAccessTypeEx — Go's last param is *TypeAlias (checker.go:22861). The
    // Members/Signatures dep-stub declared a Symbol? overload; this correct-signature overload
    // coexists so the #15 call site stays faithful. The #18 wave's real impl supersedes both.
    internal Type GetIndexedAccessTypeEx(Type objectType, Type indexType, AccessFlags accessFlags, Node? accessNode, TypeAlias? alias) => throw new NotImplementedException("getIndexedAccessTypeEx");

    // #14 instantiation
    internal Type GetActualTypeVariable(Type type) => throw new NotImplementedException("getActualTypeVariable");

    internal Type InstantiateTypeWithAlias(Type type, TypeMapper? mapper, TypeAlias? alias) => throw new NotImplementedException("instantiateTypeWithAlias");

    internal TypeAlias? InstantiateTypeAlias(TypeAlias? alias, TypeMapper? mapper) => throw new NotImplementedException("instantiateTypeAlias");

    internal Type GetInstantiationExpressionType(Type exprType, Node node) => throw new NotImplementedException("getInstantiationExpressionType");

    internal Type GetNoInferType(Type type) => throw new NotImplementedException("getNoInferType");

    // mapped-type helpers
    internal Type GetConstraintTypeFromMappedType(Type type) => throw new NotImplementedException("getConstraintTypeFromMappedType");

    internal Type? GetNameTypeFromMappedType(Type type) => throw new NotImplementedException("getNameTypeFromMappedType");

    internal Type GetTypeParameterFromMappedType(Type type) => throw new NotImplementedException("getTypeParameterFromMappedType");

    internal Type? GetHomomorphicTypeVariable(Type type) => throw new NotImplementedException("getHomomorphicTypeVariable");

    // relations / contextual / checks
    internal bool CheckTypeAssignableTo(Type source, Type target, Node? errorNode, global::Ts.Diagnostics.Message? headMessage) => throw new NotImplementedException("checkTypeAssignableTo");

    internal bool IsContextSensitive(Node node) => throw new NotImplementedException("isContextSensitive");

    internal bool IsTypeParameterPossiblyReferenced(Type tp, Node node) => throw new NotImplementedException("isTypeParameterPossiblyReferenced");

    internal bool IsGenericStringLikeType(Type type) => throw new NotImplementedException("isGenericStringLikeType");

    internal Type CheckExpressionWithTypeArguments(Node node) => throw new NotImplementedException("checkExpressionWithTypeArguments");

    internal bool IsConstantVariable(Symbol symbol) => throw new NotImplementedException("isConstantVariable");

    internal bool IsBlockScopedNameDeclaredBeforeUse(Node declaration, Node usage) => throw new NotImplementedException("isBlockScopedNameDeclaredBeforeUse");

    internal Type GetBuiltinIteratorReturnType() => throw new NotImplementedException("getBuiltinIteratorReturnType");

    // free-function helper (Go package-level; static class in Utilities.cs when that wave lands)
    internal static SymbolTable CreateSymbolTable(Symbol[]? symbols) => throw new NotImplementedException("createSymbolTable");
}
