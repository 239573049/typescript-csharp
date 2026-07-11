// Ported from tsgo internal/checker/checker.go (section: base constraints, type-kind predicates,
// and type simplification/normalization, lines 27302-28258). plan/03 §4.2 #19.
//
// Breadth-first scope: the base-constraint stack (getBaseConstraintOfType / getResolvedBaseConstraint
// / computeBaseConstraint with the conditionalConstraintDepth guard), the assignable-to-kind
// predicates, and the getSimplified* cluster (needed by computeBaseConstraint) are real. The
// unreferenced peripheral tail of this line range — compareProperties, markPropertyAsReferenced,
// expandSignatureParametersWithTupleMembers, isUnknownLikeUnionType, getNormalized* / tuple-normalize,
// getSingleBaseForNonAugmentingSubtype, getRegularTypeOfObjectLiteral/transformTypeOfMembers, and the
// alias-reference-marking functions (markLinkedReferences..markExportAssignmentAliasReferenced) — is
// left unported (0 landed callers); it lands with #20 AliasReferences / the flow & relater waves.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:27302-27311
    internal Type? GetBaseConstraintOfType(Type t)
    {
        if ((t.flags & (TypeFlags.InstantiableNonPrimitive | TypeFlags.UnionOrIntersection | TypeFlags.TemplateLiteral | TypeFlags.StringMapping | TypeFlags.Index)) != 0 || IsGenericTupleType(t))
        {
            var constraint = GetResolvedBaseConstraint(t, null);
            if (constraint != noConstraintType && constraint != circularConstraintType)
            {
                return constraint;
            }
            return null;
        }
        return null;
    }

    // checker.go:27313-27354
    internal Type GetResolvedBaseConstraint(Type t, RecursionId[]? stack)
    {
        var constrained = t.AsConstrainedType();
        if (constrained is null)
        {
            return t;
        }
        if (constrained.resolvedBaseConstraint is not null)
        {
            return constrained.resolvedBaseConstraint;
        }
        if (!PushTypeResolution(t, TypeSystemPropertyName.ResolvedBaseConstraint))
        {
            return circularConstraintType!;
        }
        Type? constraint = null;
        // We always explore at least 10 levels of nested constraints. Thereafter, up to 50 levels
        // provided there are no "deeply nested" types on the stack. 50 levels is effectively a safety stop.
        var identity = GetRecursionIdentity(t);
        var stackLen = stack?.Length ?? 0;
        if (stackLen < 10 || (stackLen < 50 && Array.IndexOf(stack ?? [], identity) < 0))
        {
            constraint = ComputeBaseConstraint(GetSimplifiedType(t, false /*writing*/), [.. (stack ?? []), identity]);
        }
        if (!PopTypeResolution())
        {
            if ((t.flags & TypeFlags.TypeParameter) != 0)
            {
                var errorNode = GetConstraintDeclaration(t);
                if (errorNode is not null)
                {
                    var diagnostic = Error(errorNode, global::Ts.Diagnostics.Diagnostics.Type_parameter_0_has_a_circular_constraint, TypeToString(t));
                    if (currentNode is not null && !AstUtilities.IsNodeDescendantOf(errorNode, currentNode) && !AstUtilities.IsNodeDescendantOf(currentNode, errorNode))
                    {
                        diagnostic?.AddRelatedInfo(NewDiagnosticForNode(currentNode, global::Ts.Diagnostics.Diagnostics.Circularity_originates_in_type_at_this_location));
                    }
                }
            }
            constraint = circularConstraintType;
        }
        if (constraint is null)
        {
            constraint = noConstraintType;
        }
        if (constrained.resolvedBaseConstraint is null)
        {
            constrained.resolvedBaseConstraint = constraint;
        }
        return constraint!;
    }

    // checker.go:27356-27457
    internal Type ComputeBaseConstraint(Type t, RecursionId[] stack)
    {
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            var constraint = GetConstraintFromTypeParameter(t);
            if (t.AsTypeParameter().isThisType)
            {
                return constraint!;
            }
            return GetNextBaseConstraint(constraint, stack)!;
        }
        if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            var types = t.Types();
            var constraints = new List<Type>(types.Length);
            var different = false;
            foreach (var s in types)
            {
                var constraint = GetNextBaseConstraint(s, stack);
                if (constraint is not null)
                {
                    if (constraint != s)
                    {
                        different = true;
                    }
                    constraints.Add(constraint);
                }
                else
                {
                    different = true;
                }
            }
            if (!different)
            {
                return t;
            }
            if ((t.flags & TypeFlags.Union) != 0 && constraints.Count == types.Length)
            {
                return GetUnionType(constraints.ToArray());
            }
            if ((t.flags & TypeFlags.Intersection) != 0 && constraints.Count != 0)
            {
                return GetIntersectionType(constraints.ToArray());
            }
            return null!;
        }
        if ((t.flags & TypeFlags.Index) != 0)
        {
            if (IsGenericMappedType(t.AsIndexType().target))
            {
                var mappedType = t.AsIndexType().target;
                if (GetNameTypeFromMappedType(mappedType) is not null && !IsMappedTypeWithKeyofConstraintDeclaration(mappedType))
                {
                    return GetNextBaseConstraint(GetIndexTypeForMappedType(mappedType, IndexFlags.None), stack)!;
                }
            }
            return stringNumberSymbolType!;
        }
        if ((t.flags & TypeFlags.TemplateLiteral) != 0)
        {
            var types = t.Types();
            var constraints = new List<Type>(types.Length);
            foreach (var s in types)
            {
                var constraint = GetNextBaseConstraint(s, stack);
                if (constraint is not null)
                {
                    constraints.Add(constraint);
                }
            }
            if (constraints.Count == types.Length)
            {
                return GetTemplateLiteralType(t.AsTemplateLiteralType().texts, constraints.ToArray());
            }
            return stringType!;
        }
        if ((t.flags & TypeFlags.StringMapping) != 0)
        {
            var constraint = GetNextBaseConstraint(t.Target(), stack);
            if (constraint is not null && constraint != t.Target())
            {
                return GetStringMappingType(t.symbol!, constraint);
            }
            return stringType!;
        }
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            if (IsMappedTypeGenericIndexedAccess(t))
            {
                // For { [P in K]: E }[X], where K is non-generic and X is generic, substitute an
                // instantiation of E where P is replaced with X.
                return GetNextBaseConstraint(SubstituteIndexedMappedType(t.AsIndexedAccessType().objectType, t.AsIndexedAccessType().indexType), stack)!;
            }
            var baseObjectType = GetNextBaseConstraint(t.AsIndexedAccessType().objectType, stack);
            var baseIndexType = GetNextBaseConstraint(t.AsIndexedAccessType().indexType, stack);
            if (baseObjectType is null || baseIndexType is null)
            {
                return null!;
            }
            return GetNextBaseConstraint(GetIndexedAccessTypeOrUndefined(baseObjectType, baseIndexType, t.AsIndexedAccessType().accessFlags, null, null), stack)!;
        }
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            if (conditionalConstraintDepth >= 100)
            {
                return null!;
            }
            conditionalConstraintDepth++;
            var constraint = GetConstraintFromConditionalType(t);
            conditionalConstraintDepth--;
            return GetNextBaseConstraint(constraint, stack)!;
        }
        if ((t.flags & TypeFlags.Substitution) != 0)
        {
            return GetNextBaseConstraint(GetSubstitutionIntersection(t), stack)!;
        }
        if (IsGenericTupleType(t))
        {
            // Substitute constraints for variadic elements only when they are array types or non-variadic tuples.
            var elementTypes = GetElementTypes(t);
            var elementInfos = t.TargetTupleType().elementInfos ?? [];
            var newElements = new List<Type>(elementTypes.Length);
            for (int i = 0; i < elementTypes.Length; i++)
            {
                var v = elementTypes[i];
                var newElement = v;
                if ((v.flags & TypeFlags.TypeParameter) != 0 && (elementInfos[i].flags & ElementFlags.Variadic) != 0)
                {
                    var constraint = GetNextBaseConstraint(v, stack);
                    if (constraint is not null && constraint != v && EveryType(constraint, n => IsArrayOrTupleType(n) && !IsGenericTupleType(n)))
                    {
                        newElement = constraint;
                    }
                }
                newElements.Add(newElement);
            }
            return CreateTupleTypeEx(newElements.ToArray(), elementInfos, t.TargetTupleType().readonlyElements);
        }
        return t;
    }

    // checker.go:27459-27468
    internal Type? GetNextBaseConstraint(Type? t, RecursionId[] stack)
    {
        if (t is null)
        {
            return null;
        }
        var constraint = GetResolvedBaseConstraint(t, stack);
        if (constraint == noConstraintType || constraint == circularConstraintType)
        {
            return null;
        }
        return constraint;
    }

    // Return true if type might be of the given kind.
    // checker.go:27472-27484
    internal bool MaybeTypeOfKind(Type t, TypeFlags kind)
    {
        if ((t.flags & kind) != 0)
        {
            return true;
        }
        if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            foreach (var u in t.Types())
            {
                if (MaybeTypeOfKind(u, kind))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // checker.go:27486-27492
    internal bool MaybeTypeOfKindConsideringBaseConstraint(Type t, TypeFlags kind)
    {
        if (MaybeTypeOfKind(t, kind))
        {
            return true;
        }
        var baseConstraint = GetBaseConstraintOrType(t);
        return baseConstraint is not null && MaybeTypeOfKind(baseConstraint, kind);
    }

    // checker.go:27494-27496
    internal bool AllTypesAssignableToKind(Type source, TypeFlags kind) => AllTypesAssignableToKindEx(source, kind, false);

    // checker.go:27498-27505
    internal bool AllTypesAssignableToKindEx(Type source, TypeFlags kind, bool strict)
    {
        if ((source.flags & TypeFlags.Union) != 0)
        {
            return CoreUtil.Every(source.Types(), subType => AllTypesAssignableToKindEx(subType, kind, strict));
        }
        return IsTypeAssignableToKindEx(source, kind, strict);
    }

    // checker.go:27507-27509
    internal bool IsTypeAssignableToKind(Type source, TypeFlags kind) => IsTypeAssignableToKindEx(source, kind, false);

    // checker.go:27511-27528
    internal bool IsTypeAssignableToKindEx(Type source, TypeFlags kind, bool strict)
    {
        if ((source.flags & kind) != 0)
        {
            return true;
        }
        if (strict && (source.flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void | TypeFlags.Undefined | TypeFlags.Null)) != 0)
        {
            return false;
        }
        return ((kind & TypeFlags.NumberLike) != 0 && IsTypeAssignableTo(source, numberType!)) ||
            ((kind & TypeFlags.BigIntLike) != 0 && IsTypeAssignableTo(source, bigintType!)) ||
            ((kind & TypeFlags.StringLike) != 0 && IsTypeAssignableTo(source, stringType!)) ||
            ((kind & TypeFlags.BooleanLike) != 0 && IsTypeAssignableTo(source, booleanType!)) ||
            ((kind & TypeFlags.Void) != 0 && IsTypeAssignableTo(source, voidType!)) ||
            ((kind & TypeFlags.Never) != 0 && IsTypeAssignableTo(source, neverType!)) ||
            ((kind & TypeFlags.Null) != 0 && IsTypeAssignableTo(source, nullType!)) ||
            ((kind & TypeFlags.Undefined) != 0 && IsTypeAssignableTo(source, undefinedType!)) ||
            ((kind & TypeFlags.ESSymbol) != 0 && IsTypeAssignableTo(source, esSymbolType!)) ||
            ((kind & TypeFlags.NonPrimitive) != 0 && IsTypeAssignableTo(source, nonPrimitiveType!));
    }

    // checker.go:27530-27532
    internal static bool IsConstEnumObjectType(Type t) =>
        (t.objectFlags & ObjectFlags.Anonymous) != 0 && t.symbol is not null && IsConstEnumSymbol(t.symbol);

    // checker.go:27534-27536
    internal static bool IsConstEnumSymbol(Symbol symbol) => (symbol.Flags & SymbolFlags.ConstEnum) != 0;

    // checker.go:27694-27729
    internal Type GetNormalizedType(Type t, bool writing)
    {
        while (true)
        {
            Type n;
            if (IsFreshLiteralType(t))
            {
                n = t.AsLiteralType().regularType!;
            }
            else if (IsGenericTupleType(t))
            {
                n = GetNormalizedTupleType(t, writing);
            }
            else if ((t.objectFlags & ObjectFlags.Reference) != 0)
            {
                if (t.AsTypeReference()!.node is not null)
                {
                    n = CreateTypeReference(t.Target(), GetTypeArguments(t));
                }
                else
                {
                    n = GetSingleBaseForNonAugmentingSubtype(t) ?? t;
                }
            }
            else if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
            {
                n = GetNormalizedUnionOrIntersectionType(t, writing);
            }
            else if ((t.flags & TypeFlags.Substitution) != 0)
            {
                n = writing ? t.AsSubstitutionType().baseType : GetSubstitutionIntersection(t);
            }
            else if ((t.flags & TypeFlags.Simplifiable) != 0)
            {
                n = GetSimplifiedType(t, writing);
            }
            else
            {
                return t;
            }
            if (n == t)
            {
                return n;
            }
            t = n;
        }
    }

    // checker.go:27731-27739
    internal Type GetSimplifiedType(Type t, bool writing)
    {
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            return GetSimplifiedIndexedAccessType(t, writing);
        }
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            return GetSimplifiedConditionalType(t, writing);
        }
        return t;
    }

    // Transform an indexed access to a simpler form, if possible.
    // checker.go:27744-27757
    internal Type GetSimplifiedIndexedAccessType(Type t, bool writing)
    {
        var key = new CachedTypeKey(writing ? CachedTypeKind.IndexedAccessForWriting : CachedTypeKind.IndexedAccessForReading, t.id);
        {
            var cached = cachedTypes.GetValueOrDefault(key);
            if (cached is not null)
            {
                return cached == circularConstraintType ? t : cached;
            }
        }
        cachedTypes[key] = t;
        var result = GetSimplifiedIndexedAccessTypeWorker(t, writing);
        if (result != t)
        {
            // If the simplification is a union type that includes t, remove t from the type.
            result = RemoveType(result, t);
            cachedTypes[key] = result;
        }
        return result;
    }

    // checker.go:27759-27802
    internal Type GetSimplifiedIndexedAccessTypeWorker(Type t, bool writing)
    {
        // Recursively simplify the object type as it may in turn be an indexed access type.
        var objectType = GetSimplifiedType(t.AsIndexedAccessType().objectType, writing);
        var indexType = GetSimplifiedType(t.AsIndexedAccessType().indexType, writing);
        // T[A | B] -> T[A] | T[B] (reading) / T[A] & T[B] (writing)
        var distributedOverIndex = DistributeObjectOverIndexType(objectType, indexType, writing);
        if (distributedOverIndex is not null)
        {
            return distributedOverIndex;
        }
        // Only do the inner distributions if the index can no longer be instantiated.
        if ((indexType.flags & TypeFlags.Instantiable) == 0)
        {
            // (T | U)[K] -> T[K] | U[K] (reading) etc.
            var distributedOverObject = DistributeIndexOverObjectType(objectType, indexType, writing);
            if (distributedOverObject is not null)
            {
                return distributedOverObject;
            }
        }
        // A generic tuple type indexed by a number simplifies to the combined element type.
        if (IsGenericTupleType(objectType) && (indexType.flags & TypeFlags.NumberLike) != 0)
        {
            var elementType = GetElementTypeOfSliceOfTupleType(objectType, (indexType.flags & TypeFlags.Number) != 0 ? 0 : objectType.TargetTupleType().fixedLength, 0 /*endSkipCount*/, writing, false);
            if (elementType is not null)
            {
                return elementType;
            }
        }
        // If the object type is a mapped type { [P in K]: E }, where K is generic (or with an assignable
        // remap), instantiate E using a mapper that substitutes the index type for P.
        if (IsGenericMappedType(objectType))
        {
            if (GetMappedTypeNameTypeKind(objectType) != MappedTypeNameTypeKind.Remapping)
            {
                return MapType(SubstituteIndexedMappedType(objectType, t.AsIndexedAccessType().indexType), x => GetSimplifiedType(x, writing));
            }
        }
        return t;
    }

    // checker.go:27804-27817
    internal Type? DistributeObjectOverIndexType(Type objectType, Type indexType, bool writing)
    {
        // T[A | B] -> T[A] | T[B] (reading) / T[A] & T[B] (writing)
        if ((indexType.flags & TypeFlags.Union) != 0)
        {
            var types = CoreUtil.Map(indexType.Types(), t => GetSimplifiedType(GetIndexedAccessType(objectType, t), writing));
            if (writing)
            {
                return GetIntersectionType(types);
            }
            return GetUnionType(types);
        }
        return null;
    }

    // checker.go:27819-27833
    internal Type? DistributeIndexOverObjectType(Type objectType, Type indexType, bool writing)
    {
        // (T | U)[K] -> T[K] | U[K] (reading) / T[K] & U[K] (writing); (T & U)[K] -> T[K] & U[K]
        if ((objectType.flags & TypeFlags.Union) != 0 || ((objectType.flags & TypeFlags.Intersection) != 0 && !ShouldDeferIndexType(objectType, IndexFlags.None)))
        {
            var types = CoreUtil.Map(objectType.Types(), t => GetSimplifiedType(GetIndexedAccessType(t, indexType), writing));
            if ((objectType.flags & TypeFlags.Intersection) != 0 || writing)
            {
                return GetIntersectionType(types);
            }
            return GetUnionType(types);
        }
        return null;
    }

    // checker.go:27835-27855
    internal Type GetSimplifiedConditionalType(Type t, bool writing)
    {
        var checkType = t.AsConditionalType().checkType!;
        var extendsType = t.AsConditionalType().extendsType!;
        var trueType = GetTrueTypeFromConditionalType(t);
        var falseType = GetFalseTypeFromConditionalType(t);
        // Simplifications for `T extends U ? T : never` and `T extends U ? never : T`.
        if ((falseType.flags & TypeFlags.Never) != 0 && GetActualTypeVariable(trueType) == GetActualTypeVariable(checkType))
        {
            if ((checkType.flags & TypeFlags.Any) != 0 || IsTypeAssignableTo(GetRestrictiveInstantiation(checkType), GetRestrictiveInstantiation(extendsType)))
            {
                return GetSimplifiedType(trueType, writing);
            }
            else if (IsIntersectionEmpty(checkType, extendsType))
            {
                return neverType!;
            }
        }
        else if ((trueType.flags & TypeFlags.Never) != 0 && GetActualTypeVariable(falseType) == GetActualTypeVariable(checkType))
        {
            if ((checkType.flags & TypeFlags.Any) == 0 && IsTypeAssignableTo(GetRestrictiveInstantiation(checkType), GetRestrictiveInstantiation(extendsType)))
            {
                return neverType!;
            }
            else if ((checkType.flags & TypeFlags.Any) != 0 || IsIntersectionEmpty(checkType, extendsType))
            {
                return GetSimplifiedType(falseType, writing);
            }
        }
        return t;
    }

    // Invokes union simplification logic to determine if an intersection is considered empty.
    // checker.go:27858-27860
    internal bool IsIntersectionEmpty(Type type1, Type type2) =>
        (GetUnionType([IntersectTypes(type1, type2)!, neverType!]).flags & TypeFlags.Never) != 0;

    // checker.go:27862-27867
    internal Type? GetSimplifiedTypeOrConstraint(Type t)
    {
        var simplified = GetSimplifiedType(t, false /*writing*/);
        if (simplified != t)
        {
            return simplified;
        }
        return GetConstraintOfType(t);
    }

    // checker.go:27869-27887
    internal Type GetNormalizedUnionOrIntersectionType(Type t, bool writing)
    {
        var reduced = GetReducedType(t);
        if (reduced != t)
        {
            return reduced;
        }
        if ((t.flags & TypeFlags.Intersection) != 0 && ShouldNormalizeIntersection(t))
        {
            // Normalization handles cases like Partial<T>[K] & ({} | null).
            var types = t.Types();
            var normalizedTypes = CoreUtil.SameMap(types, u => GetNormalizedType(u, writing))!;
            if (!CoreUtil.Same(normalizedTypes, types))
            {
                return GetIntersectionType(normalizedTypes);
            }
        }
        return t;
    }

    // checker.go:27889-27900
    internal bool ShouldNormalizeIntersection(Type t)
    {
        var hasInstantiable = false;
        var hasNullableOrEmpty = false;
        foreach (var u in t.Types())
        {
            hasInstantiable = hasInstantiable || (u.flags & TypeFlags.Instantiable) != 0;
            hasNullableOrEmpty = hasNullableOrEmpty || (u.flags & TypeFlags.Nullable) != 0 || IsEmptyAnonymousObjectType(u);
            if (hasInstantiable && hasNullableOrEmpty)
            {
                return true;
            }
        }
        return false;
    }

    // checker.go:27902-27914
    internal Type GetNormalizedTupleType(Type t, bool writing)
    {
        var elements = GetElementTypes(t);
        var normalizedElements = CoreUtil.SameMap(elements, x => (x.flags & TypeFlags.Simplifiable) != 0 ? GetSimplifiedType(x, writing) : x)!;
        if (!CoreUtil.Same(elements, normalizedElements))
        {
            return CreateNormalizedTupleType(t.Target(), normalizedElements);
        }
        return t;
    }

    // checker.go:27916-27954
    internal Type? GetSingleBaseForNonAugmentingSubtype(Type t)
    {
        if ((t.objectFlags & ObjectFlags.Reference) == 0 || (t.Target().objectFlags & ObjectFlags.ClassOrInterface) == 0)
        {
            return null;
        }
        var key = new CachedTypeKey(CachedTypeKind.EquivalentBaseType, t.id);
        if ((t.objectFlags & ObjectFlags.IdenticalBaseTypeCalculated) != 0)
        {
            return cachedTypes.GetValueOrDefault(key);
        }
        t.objectFlags |= ObjectFlags.IdenticalBaseTypeCalculated;
        var target = t.Target();
        if ((target.objectFlags & ObjectFlags.Class) != 0)
        {
            var baseTypeNode = GetBaseTypeNodeOfClass(target);
            // A base type expression may circularly reference the class itself, so we only check for
            // base types specified as simple qualified names.
            if (baseTypeNode is not null && !NodeTests.IsIdentifier(baseTypeNode.Expression()!) && !NodeTests.IsPropertyAccessExpression(baseTypeNode.Expression()!))
            {
                return null;
            }
        }
        var bases = GetBaseTypes(target);
        if (bases.Length != 1)
        {
            return null;
        }
        if (GetMembersOfSymbol(t.symbol!).Count != 0)
        {
            // If the interface has any members, do a full structural comparison instead.
            return null;
        }
        Type instantiatedBase;
        var typeParameters = target.AsInterfaceType()!.TypeParameters() ?? [];
        if (typeParameters.Length == 0)
        {
            instantiatedBase = bases[0];
        }
        else
        {
            instantiatedBase = InstantiateType(bases[0], TypeMapper.NewTypeMapper(typeParameters, GetTypeArguments(t)[..typeParameters.Length]));
        }
        if (GetTypeArguments(t).Length > typeParameters.Length)
        {
            instantiatedBase = GetTypeWithThisArgument(instantiatedBase, CoreUtil.LastOrNil(GetTypeArguments(t)), false);
        }
        cachedTypes[key] = instantiatedBase;
        return instantiatedBase;
    }

    // checker.go:27956-27982
    internal Type GetModifiersTypeFromMappedType(Type t)
    {
        var m = t.AsMappedType();
        if (m.modifiersType is null)
        {
            if (IsMappedTypeWithKeyofConstraintDeclaration(t))
            {
                // If the constraint declaration is a 'keyof T' node, the modifiers type is T.
                m.modifiersType = InstantiateType(GetTypeFromTypeNode(GetConstraintDeclarationForMappedType(t)!.Type()!), m.mapper);
            }
            else
            {
                // Otherwise, get the declared constraint type; if it is a type parameter, get its constraint.
                var declaredType = GetTypeFromMappedTypeNode(m.declaration!.AsNode());
                var constraint = GetConstraintTypeFromMappedType(declaredType);
                var extendedConstraint = constraint;
                if (constraint is not null && (constraint.flags & TypeFlags.TypeParameter) != 0)
                {
                    extendedConstraint = GetConstraintOfTypeParameter(constraint);
                }
                if (extendedConstraint is not null && (extendedConstraint.flags & TypeFlags.Index) != 0)
                {
                    m.modifiersType = InstantiateType(extendedConstraint.AsIndexType().target, m.mapper);
                }
                else
                {
                    m.modifiersType = unknownType;
                }
            }
        }
        return m.modifiersType!;
    }

    // checker.go:27984-27986
    internal Type ExtractTypesOfKind(Type t, TypeFlags kind) => FilterType(t, x => (x.flags & kind) != 0);
}
