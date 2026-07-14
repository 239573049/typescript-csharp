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
    // checker.go:25015-25027
    internal Type CreateTypeReferenceEx(Type target, Type[]? typeArguments, ObjectFlags newObjectFlags)
    {
        var id = GetTypeListKey(typeArguments);
        var intf = target.AsInterfaceType()!;
        if (intf.instantiations!.TryGetValue(id, out var cached))
        {
            return cached;
        }
        var t = NewObjectType(ObjectFlags.Reference | newObjectFlags | GetPropagatingFlagsOfTypes(typeArguments, TypeFlags.None), target.symbol);
        var d = t.AsTypeReference()!;
        d.target = target;
        d.resolvedTypeArguments = typeArguments;
        intf.instantiations[id] = t;
        return t;
    }

    // checker.go:25029-25043 — real body (mapped-type wave O); previously a stub here.
    internal Type CreateDeferredTypeReference(Type target, Node node, TypeMapper? mapper, TypeAlias? alias)
    {
        if (alias is null)
        {
            alias = GetAliasForTypeNode(node);
            if (alias is not null && mapper is not null)
            {
                alias.typeArguments = InstantiateTypes(alias.typeArguments, mapper);
            }
        }
        var t = NewObjectType(ObjectFlags.Reference, target.symbol);
        t.alias = alias;
        var d = t.AsTypeReference()!;
        d.target = target;
        d.mapper = mapper;
        d.node = node;
        return t;
    }

    internal Type NewConditionalType(ConditionalRoot root, TypeMapper? mapper, TypeMapper? combinedMapper) => throw new NotImplementedException("newConditionalType");

    // GetEnumLiteralType is now REAL in Checker.TypeCreation.cs (checker.go:25270).

    // getSubstitutionType (27277) implemented in Checker.IndexedAccess.cs (#18).

    internal Type GetStringMappingType(Symbol symbol, Type type) => throw new NotImplementedException("getStringMappingType");

    // #17 union / intersection: getIntersectionTypeEx (25950) / checkCrossProductUnion (26519)
    // implemented in Checker.UnionIntersection.cs (#17).

    // #18 getIndexType (1-arg) / getIndexedAccessTypeEx (26551/26793) implemented in Checker.IndexedAccess.cs (#18).

    // #14 instantiation
    // checker.go:31328-31336 — real body (mapped-type wave O); previously a stub here.
    internal Type GetActualTypeVariable(Type type)
    {
        if ((type.flags & TypeFlags.Substitution) != 0)
        {
            return GetActualTypeVariable(type.AsSubstitutionType().baseType);
        }
        if ((type.flags & TypeFlags.IndexedAccess) != 0 && ((type.AsIndexedAccessType().objectType.flags & TypeFlags.Substitution) != 0 || (type.AsIndexedAccessType().indexType.flags & TypeFlags.Substitution) != 0))
        {
            return GetIndexedAccessType(GetActualTypeVariable(type.AsIndexedAccessType().objectType), GetActualTypeVariable(type.AsIndexedAccessType().indexType));
        }
        return type;
    }

    // instantiateTypeWithAlias / instantiateTypeAlias / getConstraintTypeFromMappedType /
    // getNameTypeFromMappedType / getTypeParameterFromMappedType / getHomomorphicTypeVariable /
    // isTypeParameterPossiblyReferenced implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // GetInstantiationExpressionType is now REAL in Checker.Expressions.cs (checker.go:10634).

    // getNoInferType (27260) implemented in Checker.IndexedAccess.cs (#18).

    // relations / contextual / checks
    // checkTypeAssignableTo owned by the relater wave (Checker.Relater.cs).

    // IsContextSensitive is now REAL in Checker.ContextSensitive.cs (checker.go:30654).

    // isGenericStringLikeType (26412) implemented in Checker.UnionIntersection.cs (#17).

    // CheckExpressionWithTypeArguments is now REAL in Checker.Expressions.cs (checker.go:10611).

    // utilities.go:1017
    internal bool IsConstantVariable(Symbol symbol) =>
        (symbol.Flags & SymbolFlags.Variable) != 0 && (GetDeclarationNodeFlagsFromSymbol(symbol) & NodeFlags.Constant) != 0;

    // IsBlockScopedNameDeclaredBeforeUse (+ isUsedInFunctionOrInstanceProperty / isImmediatelyUsedInInitializerOfBlockScopedVariable / isSameScopeDescendentOf / isPropertyImmediatelyReferencedWithinDeclaration) are now REAL in Checker.NameLookup.cs (checker.go:1908).

    // getBuiltinIteratorReturnType (6379): real body landed in Checker.IterationTypes.cs (iteration wave).

    // free-function helper (Go package-level; static class in Utilities.cs when that wave lands)
    internal static SymbolTable CreateSymbolTable(Symbol[]? symbols) => throw new NotImplementedException("createSymbolTable");
}
