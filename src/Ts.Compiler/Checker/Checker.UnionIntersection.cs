// Ported from tsgo internal/checker/checker.go (section: union & intersection type construction,
// lines 25518-26549). plan/03 §4.2 #17.
//
// Real union reduction (getUnionTypeWorker) replaces the skeleton stand-in that previously lived in
// Checker.TypeCreation.cs — this unlocks boolean (= false|true union with the Boolean flag) and the
// full literal/subtype reduction pipeline. getIntersectionType(Ex) and the cross-product/expansion
// guards are likewise real here.
//
// §1.5: cross-package package-level helper `Core.BinarySearchUniqueFunc` is called as
// `global::Ts.Core.Core.*` (the class name collides with the sibling `Ts.Core` namespace).

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:25518-25523
    internal Type GetUnionOrIntersectionType(Type[]? types, bool isUnion, UnionReduction unionReduction)
    {
        if (isUnion)
        {
            return GetUnionTypeEx(types, unionReduction, null, null);
        }
        return GetIntersectionType(types);
    }

    // checker.go:25525-25527
    public Type GetUnionType(Type[]? types) =>
        GetUnionTypeEx(types, UnionReduction.Literal, null /*alias*/, null /*origin*/);

    // We sort and deduplicate the constituent types based on object identity. If the subtypeReduction
    // flag is specified we also reduce the constituent type set to only include types that aren't subtypes
    // of other types. Subtype reduction is expensive for large union types and is possible only when union
    // types are known not to circularly reference themselves.
    // checker.go:25536-25559
    internal Type GetUnionTypeEx(Type[]? types, UnionReduction unionReduction, TypeAlias? alias, Type? origin)
    {
        types ??= [];
        if (types.Length == 0)
        {
            return neverType!;
        }
        if (types.Length == 1)
        {
            return types[0];
        }
        // We optimize for the common case of unioning a union type with some other type (such as `undefined`).
        if (types.Length == 2 && origin is null && ((types[0].flags & TypeFlags.Union) != 0 || (types[1].flags & TypeFlags.Union) != 0))
        {
            var id1 = types[0].id;
            var id2 = types[1].id;
            if (id1.Value > id2.Value)
            {
                (id1, id2) = (id2, id1);
            }
            var key = new UnionOfUnionKey(id1, id2, unionReduction, GetAliasKey(alias));
            var cached = unionOfUnionTypes.GetValueOrDefault(key);
            if (cached is null)
            {
                cached = GetUnionTypeWorker(types, unionReduction, alias, null /*origin*/);
                unionOfUnionTypes[key] = cached;
            }
            return cached;
        }
        return GetUnionTypeWorker(types, unionReduction, alias, origin);
    }

    // checker.go:25561-25641
    internal Type GetUnionTypeWorker(Type[] types, UnionReduction unionReduction, TypeAlias? alias, Type? origin)
    {
        var (typeSet, includes) = AddTypesToUnion([], TypeFlags.None, types);
        if (unionReduction != UnionReduction.None)
        {
            if ((includes & TypeFlags.AnyOrUnknown) != 0)
            {
                if ((includes & TypeFlags.Any) != 0)
                {
                    if ((includes & TypeFlags.IncludesWildcard) != 0)
                    {
                        return wildcardType!;
                    }
                    if ((includes & TypeFlags.IncludesError) != 0)
                    {
                        return errorType!;
                    }
                    return anyType!;
                }
                return unknownType!;
            }
            if ((includes & TypeFlags.Undefined) != 0)
            {
                // If type set contains both undefinedType and missingType, remove missingType
                if (typeSet.Length >= 2 && typeSet[0] == undefinedType && typeSet[1] == missingType)
                {
                    typeSet = SlicesDelete(typeSet, 1, 2);
                }
            }
            if ((includes & (TypeFlags.Enum | TypeFlags.Literal | TypeFlags.UniqueESSymbol | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0 ||
                ((includes & TypeFlags.Void) != 0 && (includes & TypeFlags.Undefined) != 0))
            {
                typeSet = RemoveRedundantLiteralTypes(typeSet, includes, (unionReduction & UnionReduction.Subtype) != 0);
            }
            if ((includes & TypeFlags.StringLiteral) != 0 && (includes & (TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
            {
                typeSet = RemoveStringLiteralsMatchedByTemplateLiterals(typeSet);
            }
            if ((includes & TypeFlags.IncludesConstrainedTypeVariable) != 0)
            {
                typeSet = RemoveConstrainedTypeVariables(typeSet);
            }
            if (unionReduction == UnionReduction.Subtype)
            {
                var reduced = RemoveSubtypes(typeSet, (includes & TypeFlags.Object) != 0);
                if (reduced is null)
                {
                    return errorType!;
                }
                typeSet = reduced;
            }
            if (typeSet.Length == 0)
            {
                if ((includes & TypeFlags.Null) != 0)
                {
                    return (includes & TypeFlags.IncludesNonWideningType) != 0 ? nullType! : nullWideningType!;
                }
                if ((includes & TypeFlags.Undefined) != 0)
                {
                    return (includes & TypeFlags.IncludesNonWideningType) != 0 ? undefinedType! : undefinedWideningType!;
                }
                return neverType!;
            }
        }
        if (origin is null && (includes & TypeFlags.Union) != 0)
        {
            var namedUnions = AddNamedUnions([], types);
            Type[] reducedTypes = [];
            foreach (var t in typeSet)
            {
                if (!CoreUtil.Some(namedUnions, u => ContainsType(u.Types(), t)))
                {
                    reducedTypes = [.. reducedTypes, t];
                }
            }
            if (alias is null && namedUnions.Length == 1 && reducedTypes.Length == 0)
            {
                return namedUnions[0];
            }
            // We create a denormalized origin type only when the union was created from one or more named unions
            // (unions with alias symbols or origins) and when there is no overlap between those named unions.
            var namedTypesCount = 0;
            foreach (var u in namedUnions)
            {
                namedTypesCount += u.Types().Length;
            }
            if (namedTypesCount + reducedTypes.Length == typeSet.Length)
            {
                foreach (var t in namedUnions)
                {
                    (reducedTypes, _) = InsertType(reducedTypes, t);
                }
                origin = NewUnionType(ObjectFlags.None, reducedTypes);
            }
        }
        var objectFlags = ((includes & TypeFlags.NotPrimitiveUnion) != 0 ? ObjectFlags.None : ObjectFlags.PrimitiveUnion) |
            ((includes & TypeFlags.Intersection) != 0 ? ObjectFlags.ContainsIntersections : ObjectFlags.None);
        return GetUnionTypeFromSortedList(typeSet, objectFlags, alias, origin);
    }

    // This function assumes the constituent type list is sorted and deduplicated.
    // checker.go:25644-25663
    internal Type GetUnionTypeFromSortedList(Type[] types, ObjectFlags precomputedObjectFlags, TypeAlias? alias, Type? origin)
    {
        if (types.Length == 0)
        {
            return neverType!;
        }
        if (types.Length == 1)
        {
            return types[0];
        }
        var key = GetUnionKey(types, origin, alias);
        var t = unionTypes.GetValueOrDefault(key);
        if (t is null)
        {
            t = NewUnionType(precomputedObjectFlags | GetPropagatingFlagsOfTypes(types, TypeFlags.Nullable), types);
            t.AsUnionType().origin = origin;
            t.alias = alias;
            if (types.Length == 2 && (types[0].flags & TypeFlags.BooleanLiteral) != 0 && (types[1].flags & TypeFlags.BooleanLiteral) != 0)
            {
                t.flags |= TypeFlags.Boolean;
            }
            unionTypes[key] = t;
        }
        return t;
    }

    // checker.go:25665-25667
    internal IEnumerable<Type> UnionTypesSeq() => unionTypes.Values; // map-iter: 序无关 —— tracing enumeration only

    // checker.go:25669-25686
    internal (Type[], TypeFlags) AddTypesToUnion(Type[] typeSet, TypeFlags includes, Type[] types)
    {
        Type? lastType = null;
        foreach (var t in types)
        {
            if (t != lastType)
            {
                if ((t.flags & TypeFlags.Union) != 0)
                {
                    var u = t.AsUnionType();
                    if (t.alias is not null || u.origin is not null)
                    {
                        includes |= TypeFlags.Union;
                    }
                    (typeSet, includes) = AddTypesToUnion(typeSet, includes, u.types);
                }
                else
                {
                    (typeSet, includes) = AddTypeToUnion(typeSet, includes, t);
                }
                lastType = t;
            }
        }
        return (typeSet, includes);
    }

    // checker.go:25688-25716
    internal (Type[], TypeFlags) AddTypeToUnion(Type[] typeSet, TypeFlags includes, Type t)
    {
        var flags = t.flags;
        // We ignore 'never' types in unions
        if ((flags & TypeFlags.Never) == 0)
        {
            includes |= flags & TypeFlags.IncludesMask;
            if ((flags & TypeFlags.Instantiable) != 0)
            {
                includes |= TypeFlags.IncludesInstantiable;
            }
            if ((flags & TypeFlags.Intersection) != 0 && (t.objectFlags & ObjectFlags.IsConstrainedTypeVariable) != 0)
            {
                includes |= TypeFlags.IncludesConstrainedTypeVariable;
            }
            if (t == wildcardType)
            {
                includes |= TypeFlags.IncludesWildcard;
            }
            if (IsErrorType(t))
            {
                includes |= TypeFlags.IncludesError;
            }
            if (!strictNullChecks && (flags & TypeFlags.Nullable) != 0)
            {
                if ((t.objectFlags & ObjectFlags.ContainsWideningType) == 0)
                {
                    includes |= TypeFlags.IncludesNonWideningType;
                }
            }
            else
            {
                var (index, ok) = global::Ts.Core.Core.BinarySearchUniqueFunc(typeSet.AsSpan(), (i, elem) => CompareTypes(elem, t));
                if (!ok)
                {
                    typeSet = SlicesInsert(typeSet, index, t);
                }
            }
        }
        return (typeSet, includes);
    }

    // checker.go:25718-25730
    internal Type[] AddNamedUnions(Type[] namedUnions, Type[] types)
    {
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Union) != 0)
            {
                var u = t.AsUnionType();
                if (t.alias is not null || (u.origin is not null && (u.origin.flags & TypeFlags.Union) == 0))
                {
                    namedUnions = CoreUtil.AppendIfUnique(namedUnions, t);
                }
                else if (u.origin is not null && (u.origin.flags & TypeFlags.Union) != 0)
                {
                    namedUnions = AddNamedUnions(namedUnions, u.origin.Types());
                }
            }
        }
        return namedUnions;
    }

    // checker.go:25732-25749
    internal Type[] RemoveRedundantLiteralTypes(Type[] types, TypeFlags includes, bool reduceVoidUndefined)
    {
        var i = types.Length;
        while (i > 0)
        {
            i--;
            var t = types[i];
            var flags = t.flags;
            var remove = ((flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0 && (includes & TypeFlags.String) != 0) ||
                ((flags & TypeFlags.NumberLiteral) != 0 && (includes & TypeFlags.Number) != 0) ||
                ((flags & TypeFlags.BigIntLiteral) != 0 && (includes & TypeFlags.BigInt) != 0) ||
                ((flags & TypeFlags.UniqueESSymbol) != 0 && (includes & TypeFlags.ESSymbol) != 0) ||
                (reduceVoidUndefined && (flags & TypeFlags.Undefined) != 0 && (includes & TypeFlags.Void) != 0) ||
                (IsFreshLiteralType(t) && ContainsType(types, t.AsLiteralType().regularType!));
            if (remove)
            {
                types = SlicesDelete(types, i, i + 1);
            }
        }
        return types;
    }

    // checker.go:25751-25766
    internal Type[] RemoveStringLiteralsMatchedByTemplateLiterals(Type[] types)
    {
        var templates = CoreUtil.Filter(types, IsPatternLiteralType) ?? [];
        if (templates.Length != 0)
        {
            var i = types.Length;
            while (i > 0)
            {
                i--;
                var t = types[i];
                if ((t.flags & TypeFlags.StringLiteral) != 0 && CoreUtil.Some(templates, template => IsTypeMatchedByTemplateLiteralOrStringMapping(t, template)))
                {
                    types = SlicesDelete(types, i, i + 1);
                }
            }
        }
        return types;
    }

    // checker.go:25768-25773
    internal bool IsTypeMatchedByTemplateLiteralOrStringMapping(Type t, Type template)
    {
        if ((template.flags & TypeFlags.TemplateLiteral) != 0)
        {
            return IsTypeMatchedByTemplateLiteralType(t, template.AsTemplateLiteralType(), compareTypesAssignable);
        }
        return IsMemberOfStringMapping(t, template);
    }

    // checker.go:25775-25826
    internal Type[] RemoveConstrainedTypeVariables(Type[] types)
    {
        Type[] typeVariables = [];
        // First collect a list of the type variables occurring in constraining intersections.
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Intersection) != 0 && (t.objectFlags & ObjectFlags.IsConstrainedTypeVariable) != 0)
            {
                var index = 0;
                if ((t.AsIntersectionType().types[0].flags & TypeFlags.TypeVariable) == 0)
                {
                    index = 1;
                }
                typeVariables = CoreUtil.AppendIfUnique(typeVariables, t.AsIntersectionType().types[index]);
            }
        }
        // For each type variable, check if the constraining intersections for that type variable fully
        // cover the constraint of the type variable; if so, remove them and substitute the type variable.
        foreach (var typeVariable in typeVariables)
        {
            Type[] primitives = [];
            // First collect the primitive types from the constraining intersections.
            foreach (var t in types)
            {
                if ((t.flags & TypeFlags.Intersection) != 0 && (t.objectFlags & ObjectFlags.IsConstrainedTypeVariable) != 0)
                {
                    var index = 0;
                    if ((t.AsIntersectionType().types[0].flags & TypeFlags.TypeVariable) == 0)
                    {
                        index = 1;
                    }
                    if (t.AsIntersectionType().types[index] == typeVariable)
                    {
                        (primitives, _) = InsertType(primitives, t.AsIntersectionType().types[1 - index]);
                    }
                }
            }
            // If every constituent in the type variable's constraint is covered by an intersection of the type
            // variable and that constituent, remove those intersections and substitute the type variable.
            var constraint = GetBaseConstraintOfType(typeVariable);
            if (EveryType(constraint!, t => ContainsType(primitives, t)))
            {
                var i = types.Length;
                while (i > 0)
                {
                    i--;
                    var t = types[i];
                    if ((t.flags & TypeFlags.Intersection) != 0 && (t.objectFlags & ObjectFlags.IsConstrainedTypeVariable) != 0)
                    {
                        var index = 0;
                        if ((t.AsIntersectionType().types[0].flags & TypeFlags.TypeVariable) == 0)
                        {
                            index = 1;
                        }
                        if (t.AsIntersectionType().types[index] == typeVariable && ContainsType(primitives, t.AsIntersectionType().types[1 - index]))
                        {
                            types = SlicesDelete(types, i, i + 1);
                        }
                    }
                }
                (types, _) = InsertType(types, typeVariable);
            }
        }
        return types;
    }

    // checker.go:25828-25916 — returns null on complexity overflow.
    internal Type[]? RemoveSubtypes(Type[] types, bool hasObjectTypes)
    {
        // [] and [T] immediately reduce to [] and [T] respectively
        if (types.Length < 2)
        {
            return types;
        }
        var key = GetTypeListKey(types);
        {
            var cached = subtypeReductionCache.GetValueOrDefault(key);
            if (cached is not null)
            {
                return cached;
            }
        }
        // We assume that redundant primitive types have already been removed and that there are no any/unknown
        // types. Thus, the only possible supertypes for primitive types are empty object types.
        var hasEmptyObject = hasObjectTypes && CoreUtil.Some(types, t =>
            (t.flags & TypeFlags.Object) != 0 && !IsGenericMappedType(t) && IsEmptyResolvedType(ResolveStructuredTypeMembers(t)));
        var length = types.Length;
        var i = length;
        var count = 0;
        while (i > 0)
        {
            i--;
            var source = types[i];
            if (hasEmptyObject || (source.flags & TypeFlags.StructuredOrInstantiable) != 0)
            {
                // A type parameter with a union constraint may be a subtype of some union, but not of the
                // individual constituents of that union.
                if ((source.flags & TypeFlags.TypeParameter) != 0 && (GetBaseConstraintOrType(source).flags & TypeFlags.Union) != 0)
                {
                    var src = source;
                    if (IsTypeRelatedTo(src, GetUnionType(CoreUtil.Map(types, t => t == src ? neverType! : t)), strictSubtypeRelation!))
                    {
                        types = SlicesDelete(types, i, i + 1);
                    }
                    continue;
                }
                // Find the first property with a unit type, if any.
                Symbol? keyProperty = null;
                Type? keyPropertyType = null;
                if ((source.flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.InstantiableNonPrimitive)) != 0)
                {
                    keyProperty = CoreUtil.Find(GetPropertiesOfType(source), p => IsUnitType(GetTypeOfSymbol(p)));
                }
                if (keyProperty is not null)
                {
                    keyPropertyType = GetRegularTypeOfLiteralType(GetTypeOfSymbol(keyProperty));
                }
                foreach (var target in types)
                {
                    if (source != target)
                    {
                        if (count == 100000)
                        {
                            // After 100000 subtype checks we estimate the remaining work. If the estimated
                            // remaining count exceeds 1M we deem the union too complex to represent.
                            var estimatedCount = (count / (length - i)) * length;
                            if (estimatedCount > 1000000)
                            {
                                // PORT-NOTE: tracer.Instant telemetry deferred to the phase-9 tracer wave.
                                Error(currentNode, global::Ts.Diagnostics.Diagnostics.Expression_produces_a_union_type_that_is_too_complex_to_represent);
                                return null;
                            }
                        }
                        count++;
                        if (keyProperty is not null && (target.flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.InstantiableNonPrimitive)) != 0)
                        {
                            var t = GetTypeOfPropertyOfType(target, keyProperty.Name);
                            if (t is not null && IsUnitType(t) && GetRegularTypeOfLiteralType(t) != keyPropertyType)
                            {
                                continue;
                            }
                        }
                        if ((source == emptyObjectType || source == unknownEmptyObjectType) && target.symbol is not null && IsEmptyAnonymousObjectType(target))
                        {
                            continue;
                        }
                        if (IsTypeRelatedTo(source, target, strictSubtypeRelation!) && ((GetTargetType(source).objectFlags & ObjectFlags.Class) == 0 ||
                            (GetTargetType(target).objectFlags & ObjectFlags.Class) == 0 ||
                            IsTypeDerivedFrom(source, target)))
                        {
                            types = SlicesDelete(types, i, i + 1);
                            break;
                        }
                    }
                }
            }
        }
        subtypeReductionCache[key] = types;
        return types;
    }

    // checker.go:25918-25926
    internal Type? IntersectTypes(Type? type1, Type? type2)
    {
        if (type1 is null)
        {
            return type2;
        }
        if (type2 is null)
        {
            return type1;
        }
        return GetIntersectionType([type1, type2]);
    }

    // We normalize combinations of intersection and union types based on the distributive property of '&'.
    // checker.go:25946-25948
    internal Type GetIntersectionType(Type[]? types) =>
        GetIntersectionTypeEx(types, IntersectionFlags.None, null /*alias*/);

    // checker.go:25950-26117
    internal Type GetIntersectionTypeEx(Type[]? types, IntersectionFlags flags, TypeAlias? alias)
    {
        types ??= [];
        var orderedTypes = new OrderedTypeSet();
        var includes = AddTypesToIntersection(orderedTypes, TypeFlags.None, types);
        var typeSet = orderedTypes.values.ToArray();
        var objectFlags = ObjectFlags.None;
        // An intersection type is considered empty if it contains never, more than one unit type, an object type
        // and a nullable type, disjoint domains, etc.
        if ((includes & TypeFlags.Never) != 0)
        {
            if (SlicesContains(typeSet, silentNeverType!))
            {
                return silentNeverType!;
            }
            return neverType!;
        }
        if ((strictNullChecks && (includes & TypeFlags.Nullable) != 0 && (includes & (TypeFlags.Object | TypeFlags.NonPrimitive | TypeFlags.IncludesEmptyObject)) != 0) ||
            ((includes & TypeFlags.NonPrimitive) != 0 && (includes & (TypeFlags.DisjointDomains & ~TypeFlags.NonPrimitive)) != 0) ||
            ((includes & TypeFlags.StringLike) != 0 && (includes & (TypeFlags.DisjointDomains & ~TypeFlags.StringLike)) != 0) ||
            ((includes & TypeFlags.NumberLike) != 0 && (includes & (TypeFlags.DisjointDomains & ~TypeFlags.NumberLike)) != 0) ||
            ((includes & TypeFlags.BigIntLike) != 0 && (includes & (TypeFlags.DisjointDomains & ~TypeFlags.BigIntLike)) != 0) ||
            ((includes & TypeFlags.ESSymbolLike) != 0 && (includes & (TypeFlags.DisjointDomains & ~TypeFlags.ESSymbolLike)) != 0) ||
            ((includes & TypeFlags.VoidLike) != 0 && (includes & (TypeFlags.DisjointDomains & ~TypeFlags.VoidLike)) != 0))
        {
            return neverType!;
        }
        if ((includes & (TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0 && (includes & TypeFlags.StringLiteral) != 0)
        {
            var (reducedSet, isEmptySet) = ExtractRedundantTemplateLiterals(typeSet);
            typeSet = reducedSet;
            if (isEmptySet)
            {
                return neverType!;
            }
        }
        if ((includes & TypeFlags.Any) != 0)
        {
            if ((includes & TypeFlags.IncludesWildcard) != 0)
            {
                return wildcardType!;
            }
            if ((includes & TypeFlags.IncludesError) != 0)
            {
                return errorType!;
            }
            return anyType!;
        }
        if (!strictNullChecks && (includes & TypeFlags.Nullable) != 0)
        {
            if ((includes & TypeFlags.IncludesEmptyObject) != 0)
            {
                return neverType!;
            }
            if ((includes & TypeFlags.Undefined) != 0)
            {
                return undefinedType!;
            }
            return nullType!;
        }
        if (((includes & TypeFlags.String) != 0 && (includes & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0) ||
            ((includes & TypeFlags.Number) != 0 && (includes & TypeFlags.NumberLiteral) != 0) ||
            ((includes & TypeFlags.BigInt) != 0 && (includes & TypeFlags.BigIntLiteral) != 0) ||
            ((includes & TypeFlags.ESSymbol) != 0 && (includes & TypeFlags.UniqueESSymbol) != 0) ||
            ((includes & TypeFlags.Void) != 0 && (includes & TypeFlags.Undefined) != 0) ||
            ((includes & TypeFlags.IncludesEmptyObject) != 0 && (includes & TypeFlags.DefinitelyNonNullable) != 0))
        {
            if ((flags & IntersectionFlags.NoSupertypeReduction) == 0)
            {
                typeSet = RemoveRedundantSupertypes(typeSet, includes);
            }
        }
        if ((includes & TypeFlags.IncludesMissingType) != 0)
        {
            typeSet[SlicesIndex(typeSet, undefinedType!)] = missingType!;
        }
        if (typeSet.Length == 0)
        {
            return unknownType!;
        }
        if (typeSet.Length == 1)
        {
            return typeSet[0];
        }
        if (typeSet.Length == 2 && (flags & IntersectionFlags.NoConstraintReduction) == 0)
        {
            var typeVarIndex = 0;
            if ((typeSet[0].flags & TypeFlags.TypeVariable) == 0)
            {
                typeVarIndex = 1;
            }
            var typeVariable = typeSet[typeVarIndex];
            var primitiveType = typeSet[1 - typeVarIndex];
            if ((typeVariable.flags & TypeFlags.TypeVariable) != 0 && (((primitiveType.flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0 && !IsGenericStringLikeType(primitiveType)) ||
                (includes & TypeFlags.IncludesEmptyObject) != 0))
            {
                // We have an intersection T & P or P & T, where T is a type variable and P is a primitive, the object type, or {}.
                var constraint = GetBaseConstraintOfType(typeVariable);
                // Check that T's constraint is similarly composed of primitive types, the object type, or {}.
                if (constraint is not null && EveryType(constraint, IsPrimitiveOrObjectOrEmptyType))
                {
                    // If T's constraint is a subtype of P, simply return T.
                    if (IsTypeStrictSubtypeOf(constraint, primitiveType))
                    {
                        return typeVariable;
                    }
                    if (!((constraint.flags & TypeFlags.Union) != 0 && SomeType(constraint, n => IsTypeStrictSubtypeOf(n, primitiveType))))
                    {
                        // No constituent of T's constraint is a subtype of P. If P is also not a subtype of T's constraint,
                        // then the constraint and P are unrelated, and the intersection reduces to never.
                        if (!IsTypeStrictSubtypeOf(primitiveType, constraint))
                        {
                            return neverType!;
                        }
                    }
                    // The intersection further constrains the type variable.
                    objectFlags = ObjectFlags.IsConstrainedTypeVariable;
                }
            }
        }
        var intersectionKey = GetIntersectionKey(typeSet, flags, alias);
        var result = intersectionTypes.GetValueOrDefault(intersectionKey);
        if (result is null)
        {
            if ((includes & TypeFlags.Union) != 0)
            {
                var (reducedSet, reduced) = IntersectUnionsOfPrimitiveTypes(typeSet);
                typeSet = reducedSet;
                if (reduced)
                {
                    // When the intersection creates a reduced set, we restart the operation to get a new set of
                    // combined flags. Once reduced we never reduce again, so this occurs at most once.
                    result = GetIntersectionTypeEx(typeSet, flags, alias);
                }
                else if (CoreUtil.Every(typeSet, IsUnionWithUndefined))
                {
                    var containedUndefinedType = undefinedType!;
                    if (CoreUtil.Some(typeSet, ContainsMissingType))
                    {
                        containedUndefinedType = missingType!;
                    }
                    FilterTypes(typeSet, IsNotUndefinedType);
                    result = GetUnionTypeEx([GetIntersectionTypeEx(typeSet, flags, null /*alias*/), containedUndefinedType], UnionReduction.Literal, alias, null /*origin*/);
                }
                else if (CoreUtil.Every(typeSet, IsUnionWithNull))
                {
                    FilterTypes(typeSet, IsNotNullType);
                    result = GetUnionTypeEx([GetIntersectionTypeEx(typeSet, flags, null /*alias*/), nullType!], UnionReduction.Literal, alias, null /*origin*/);
                }
                else if (typeSet.Length >= 3 && types.Length > 2)
                {
                    // Divide and conquer: A & B & C & D -> (A & B) & (C & D).
                    var middle = typeSet.Length / 2;
                    result = GetIntersectionTypeEx([
                        GetIntersectionTypeEx(typeSet[..middle], flags, null /*alias*/),
                        GetIntersectionTypeEx(typeSet[middle..], flags, null /*alias*/),
                    ], flags, alias);
                }
                else
                {
                    // Construct X & (A | B) & (C | D) -> X & A & C | X & A & D | X & B & C | X & B & D.
                    if (!CheckCrossProductUnion(typeSet))
                    {
                        return errorType!;
                    }
                    var constituents = GetCrossProductIntersections(typeSet, flags);
                    // Attach a denormalized origin when at least one constituent is an intersection and the origin
                    // has fewer constituents than the union itself.
                    Type? origin = null;
                    if (CoreUtil.Some(constituents, IsIntersectionType) && GetConstituentCountOfTypes(constituents) > GetConstituentCountOfTypes(typeSet))
                    {
                        origin = NewIntersectionType(ObjectFlags.None, typeSet);
                    }
                    result = GetUnionTypeEx(constituents, UnionReduction.Literal, alias, origin);
                }
            }
            else
            {
                result = NewIntersectionType(objectFlags | GetPropagatingFlagsOfTypes(types /*excludeKinds*/, TypeFlags.Nullable), typeSet);
                result.alias = alias;
            }
            intersectionTypes[intersectionKey] = result;
        }
        return result;
    }

    // checker.go:26119-26121
    internal static bool IsUnionWithUndefined(Type t) =>
        (t.flags & TypeFlags.Union) != 0 && (t.Types()[0].flags & TypeFlags.Undefined) != 0;

    // checker.go:26123-26125
    internal static bool IsUnionWithNull(Type t) =>
        (t.flags & TypeFlags.Union) != 0 && ((t.Types()[0].flags & TypeFlags.Null) != 0 || (t.Types()[1].flags & TypeFlags.Null) != 0);

    // checker.go:26127-26129
    internal static bool IsIntersectionType(Type t) => (t.flags & TypeFlags.Intersection) != 0;

    // checker.go:26131-26133
    internal static bool IsPrimitiveUnion(Type t) => (t.objectFlags & ObjectFlags.PrimitiveUnion) != 0;

    // checker.go:26135-26137
    internal static bool IsNotUndefinedType(Type t) => (t.flags & TypeFlags.Undefined) == 0;

    // checker.go:26139-26141
    internal static bool IsNotNullType(Type t) => (t.flags & TypeFlags.Null) == 0;

    // Add the given types to the given type set. Order is preserved, freshness is removed from literal
    // types, duplicates are removed, and nested types of the given kind are flattened into the set.
    // checker.go:26145-26150
    internal TypeFlags AddTypesToIntersection(OrderedTypeSet typeSet, TypeFlags includes, Type[] types)
    {
        foreach (var t in types)
        {
            includes = AddTypeToIntersection(typeSet, includes, GetRegularTypeOfLiteralType(t));
        }
        return includes;
    }

    // checker.go:26152-26187
    internal TypeFlags AddTypeToIntersection(OrderedTypeSet typeSet, TypeFlags includes, Type t)
    {
        var flags = t.flags;
        if ((flags & TypeFlags.Intersection) != 0)
        {
            return AddTypesToIntersection(typeSet, includes, t.Types());
        }
        if (IsEmptyAnonymousObjectType(t))
        {
            if ((includes & TypeFlags.IncludesEmptyObject) == 0)
            {
                includes |= TypeFlags.IncludesEmptyObject;
                typeSet.Add(t);
            }
        }
        else
        {
            if ((flags & TypeFlags.AnyOrUnknown) != 0)
            {
                if (t == wildcardType)
                {
                    includes |= TypeFlags.IncludesWildcard;
                }
                if (IsErrorType(t))
                {
                    includes |= TypeFlags.IncludesError;
                }
            }
            else if (strictNullChecks || (flags & TypeFlags.Nullable) == 0)
            {
                if (t == missingType)
                {
                    includes |= TypeFlags.IncludesMissingType;
                    t = undefinedType!;
                }
                if (!typeSet.Contains(t))
                {
                    if ((t.flags & TypeFlags.Unit) != 0 && (includes & TypeFlags.Unit) != 0)
                    {
                        // We have seen two distinct unit types which means we should reduce to an
                        // empty intersection. Adding TypeFlags.NonPrimitive causes that to happen.
                        includes |= TypeFlags.NonPrimitive;
                    }
                    typeSet.Add(t);
                }
            }
            includes |= flags & TypeFlags.IncludesMask;
        }
        return includes;
    }

    // checker.go:26189-26205
    internal Type[] RemoveRedundantSupertypes(Type[] types, TypeFlags includes)
    {
        var i = types.Length;
        while (i > 0)
        {
            i--;
            var t = types[i];
            var remove = ((t.flags & TypeFlags.String) != 0 && (includes & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0) ||
                ((t.flags & TypeFlags.Number) != 0 && (includes & TypeFlags.NumberLiteral) != 0) ||
                ((t.flags & TypeFlags.BigInt) != 0 && (includes & TypeFlags.BigIntLiteral) != 0) ||
                ((t.flags & TypeFlags.ESSymbol) != 0 && (includes & TypeFlags.UniqueESSymbol) != 0) ||
                ((t.flags & TypeFlags.Void) != 0 && (includes & TypeFlags.Undefined) != 0) ||
                (IsEmptyAnonymousObjectType(t) && (includes & TypeFlags.DefinitelyNonNullable) != 0);
            if (remove)
            {
                types = SlicesDelete(types, i, i + 1);
            }
        }
        return types;
    }

    // Returns (reduced types, isEmptySet). isEmptySet true if the intersection reduces to never.
    // checker.go:26211-26232
    internal (Type[], bool) ExtractRedundantTemplateLiterals(Type[] types)
    {
        var literals = CoreUtil.Filter(types, t => (t.flags & TypeFlags.StringLiteral) != 0) ?? [];
        var i = types.Length;
        while (i > 0)
        {
            i--;
            var t = types[i];
            if ((t.flags & (TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) == 0)
            {
                continue;
            }
            foreach (var t2 in literals)
            {
                if (IsTypeSubtypeOf(t2, t))
                {
                    // For example, `get${T}` & "getX" is just "getX", and Lowercase<string> & "foo" is just "foo"
                    types = SlicesDelete(types, i, i + 1);
                    break;
                }
                if (IsPatternLiteralType(t))
                {
                    return (types, true);
                }
            }
        }
        return (types, false);
    }

    // If the given list contains more than one union of primitive types, replace the first with a union
    // containing an intersection of those primitive types, remove the others and return true.
    // checker.go:26237-26285
    internal (Type[], bool) IntersectUnionsOfPrimitiveTypes(Type[] types)
    {
        var index = SlicesIndexFunc(types, IsPrimitiveUnion);
        if (index < 0)
        {
            return (types, false);
        }
        // Remove all but the first union of primitive types and collect them in the unionTypes list.
        var unionList = new List<Type> { types[index] };
        var i = index + 1;
        while (i < types.Length)
        {
            var t = types[i];
            if ((t.objectFlags & ObjectFlags.PrimitiveUnion) != 0)
            {
                unionList.Add(t);
                types = SlicesDelete(types, i, i + 1);
            }
            else
            {
                i++;
            }
        }
        // Return false if there was only one union of primitive types
        if (unionList.Count == 1)
        {
            return (types, false);
        }
        var unionArr = unionList.ToArray();
        // We have more than one union of primitive types, now intersect them.
        Type[] checked_ = [];
        Type[] result = [];
        foreach (var u in unionArr)
        {
            foreach (var t in u.Types())
            {
                var (nextChecked, inserted) = InsertType(checked_, t);
                checked_ = nextChecked;
                if (inserted)
                {
                    if (EachUnionContains(unionArr, t))
                    {
                        // undefinedType/missingType are always sorted first so we leverage that here
                        if (t == undefinedType && result.Length != 0 && result[0] == missingType)
                        {
                            continue;
                        }
                        if (t == missingType && result.Length != 0 && result[0] == undefinedType)
                        {
                            result[0] = missingType!;
                            continue;
                        }
                        (result, _) = InsertType(result, t);
                    }
                }
            }
        }
        // Finally replace the first union with the result
        types[index] = GetUnionTypeFromSortedList(result, ObjectFlags.PrimitiveUnion, null /*alias*/, null /*origin*/);
        return (types, true);
    }

    // Check that the given type has a match in every union.
    // checker.go:26290-26317
    internal bool EachUnionContains(Type[] unionTypesArr, Type t)
    {
        foreach (var u in unionTypesArr)
        {
            var types = u.Types();
            if (!ContainsType(types, t))
            {
                if (t == missingType)
                {
                    return ContainsType(types, undefinedType!);
                }
                if (t == undefinedType)
                {
                    return ContainsType(types, missingType!);
                }
                Type? primitive = null;
                if ((t.flags & TypeFlags.StringLiteral) != 0)
                {
                    primitive = stringType;
                }
                else if ((t.flags & (TypeFlags.Enum | TypeFlags.NumberLiteral)) != 0)
                {
                    primitive = numberType;
                }
                else if ((t.flags & TypeFlags.BigIntLiteral) != 0)
                {
                    primitive = bigintType;
                }
                else if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
                {
                    primitive = esSymbolType;
                }
                if (primitive is null || !ContainsType(types, primitive))
                {
                    return false;
                }
            }
        }
        return true;
    }

    // checker.go:26319-26339
    internal Type[] GetCrossProductIntersections(Type[] types, IntersectionFlags flags)
    {
        var count = GetCrossProductUnionSize(types);
        Type[] intersections = [];
        for (int i = 0; i < count; i++)
        {
            var constituents = (Type[])types.Clone();
            var n = i;
            for (int j = types.Length - 1; j >= 0; j--)
            {
                if ((types[j].flags & TypeFlags.Union) != 0)
                {
                    var sourceTypes = types[j].Types();
                    var length = sourceTypes.Length;
                    constituents[j] = sourceTypes[n % length];
                    n /= length;
                }
            }
            var t = GetIntersectionTypeEx(constituents, flags, null /*alias*/);
            if ((t.flags & TypeFlags.Never) == 0)
            {
                intersections = [.. intersections, t];
            }
        }
        return intersections;
    }

    // checker.go:26341-26349
    internal static int GetConstituentCount(Type t)
    {
        if ((t.flags & TypeFlags.UnionOrIntersection) == 0 || t.alias is not null)
        {
            return 1;
        }
        if ((t.flags & TypeFlags.Union) != 0 && t.AsUnionType().origin is not null)
        {
            return GetConstituentCount(t.AsUnionType().origin!);
        }
        return GetConstituentCountOfTypes(t.Types());
    }

    // checker.go:26351-26357
    internal static int GetConstituentCountOfTypes(Type[] types)
    {
        var n = 0;
        foreach (var t in types)
        {
            n += GetConstituentCount(t);
        }
        return n;
    }

    // checker.go:26359-26363 — mutates the array in place.
    internal void FilterTypes(Type[] types, Func<Type, bool> predicate)
    {
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = FilterType(types[i], predicate);
        }
    }

    // checker.go:26365-26368
    public bool IsEmptyAnonymousObjectType(Type t) =>
        (t.objectFlags & ObjectFlags.Anonymous) != 0 && (((t.objectFlags & ObjectFlags.MembersResolved) != 0 && IsEmptyResolvedType(t.AsStructuredType()!)) ||
            (t.symbol is not null && (t.symbol.Flags & SymbolFlags.TypeLiteral) != 0 && GetMembersOfSymbol(t.symbol).Count == 0));

    // checker.go:26370-26372
    internal bool IsEmptyResolvedType(StructuredType t) =>
        t != anyFunctionType && t.properties.Len() == 0 && t.signatures.Len() == 0 && t.indexInfos.Len() == 0;

    // checker.go:26374-26386
    internal bool IsEmptyObjectType(Type t)
    {
        if ((t.flags & TypeFlags.Object) != 0)
        {
            return !IsGenericMappedType(t) && IsEmptyResolvedType(ResolveStructuredTypeMembers(t));
        }
        if ((t.flags & TypeFlags.NonPrimitive) != 0)
        {
            return true;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return CoreUtil.Some(t.Types(), IsEmptyObjectType);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Every(t.Types(), IsEmptyObjectType);
        }
        return false;
    }

    // checker.go:26388-26403
    internal bool IsPatternLiteralPlaceholderType(Type t)
    {
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            // Return true if the intersection consists of one or more placeholders and zero or more object type tags.
            var seenPlaceholder = false;
            foreach (var s in t.Types())
            {
                if ((s.flags & (TypeFlags.Literal | TypeFlags.Nullable)) != 0 || IsPatternLiteralPlaceholderType(s))
                {
                    seenPlaceholder = true;
                }
                else if ((s.flags & TypeFlags.Object) == 0)
                {
                    return false;
                }
            }
            return seenPlaceholder;
        }
        return (t.flags & (TypeFlags.Any | TypeFlags.String | TypeFlags.Number | TypeFlags.BigInt)) != 0 || IsPatternLiteralType(t);
    }

    // checker.go:26405-26410
    internal bool IsPatternLiteralType(Type t) =>
        ((t.flags & TypeFlags.TemplateLiteral) != 0 && CoreUtil.Every(t.AsTemplateLiteralType().types, IsPatternLiteralPlaceholderType)) ||
        ((t.flags & TypeFlags.StringMapping) != 0 && IsPatternLiteralPlaceholderType(t.Target()));

    // checker.go:26412-26414
    internal bool IsGenericStringLikeType(Type t) =>
        (t.flags & (TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0 && !IsPatternLiteralType(t);

    // forEachType/someType/everyType (checker.go:26416-26438) live in Checker.MembersSignatures.Support.cs.

    // checker.go:26440-26445
    internal static bool EveryContainedType(Type t, Func<Type, bool> f) =>
        (t.flags & TypeFlags.UnionOrIntersection) != 0 ? CoreUtil.Every(t.Types(), f) : f(t);

    // checker.go:26447-26481
    internal Type FilterType(Type t, Func<Type, bool> f)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            var types = t.Types();
            var filtered = CoreUtil.Filter(types, f) ?? [];
            if (CoreUtil.Same(types, filtered))
            {
                return t;
            }
            var origin = t.AsUnionType().origin;
            Type? newOrigin = null;
            if (origin is not null && (origin.flags & TypeFlags.Union) != 0)
            {
                // If the origin type is a (denormalized) union, filter its non-union constituents.
                var originTypes = origin.Types();
                var originFiltered = CoreUtil.Filter(originTypes, u => (u.flags & TypeFlags.Union) != 0 || f(u)) ?? [];
                if (originTypes.Length - originFiltered.Length == types.Length - filtered.Length)
                {
                    if (originFiltered.Length == 1)
                    {
                        return originFiltered[0];
                    }
                    newOrigin = NewUnionType(ObjectFlags.None, originFiltered);
                }
            }
            // filtering could remove intersections so `ContainsIntersections` might be forwarded "incorrectly";
            // it is purely an optimization hint so there is no harm.
            return GetUnionTypeFromSortedList(filtered, t.AsUnionType().objectFlags & (ObjectFlags.PrimitiveUnion | ObjectFlags.ContainsIntersections), null /*alias*/, newOrigin);
        }
        if ((t.flags & TypeFlags.Never) != 0 || f(t))
        {
            return t;
        }
        return neverType!;
    }

    // checker.go:26483-26485
    internal Type RemoveType(Type t, Type targetType) => FilterType(t, x => x != targetType);

    // checker.go:26487-26490
    internal static bool ContainsType(Type[]? types, Type t)
    {
        var (_, ok) = global::Ts.Core.Core.BinarySearchUniqueFunc((types ?? []).AsSpan(), (i, elem) => CompareTypes(elem, t));
        return ok;
    }

    // checker.go:26492-26497
    internal static (Type[], bool) InsertType(Type[]? types, Type t)
    {
        var arr = types ?? [];
        var (i, ok) = global::Ts.Core.Core.BinarySearchUniqueFunc(arr.AsSpan(), (idx, elem) => CompareTypes(elem, t));
        if (!ok)
        {
            return (SlicesInsert(arr, i, t), true);
        }
        return (arr, false);
    }

    // checker.go:26499-26507
    internal static int CountTypes(Type t)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return t.Types().Length;
        }
        if ((t.flags & TypeFlags.Never) != 0)
        {
            return 0;
        }
        return 1;
    }

    // checker.go:26509-26513
    internal bool IsErrorType(Type t) =>
        // The only 'any' types that have alias symbols are those manufactured by
        // getTypeFromTypeAliasReference for a reference to an unresolved symbol.
        t == errorType || ((t.flags & TypeFlags.Any) != 0 && t.alias is not null);

    // checker.go:26515-26517
    internal static int CompareTypeIds(Type t1, Type t2) => (int)t1.id.Value - (int)t2.id.Value;

    // checker.go:26519-26529
    internal bool CheckCrossProductUnion(Type[] types)
    {
        var size = GetCrossProductUnionSize(types);
        if (size >= 100_000)
        {
            // PORT-NOTE: tracer.Instant telemetry deferred to the phase-9 tracer wave.
            Error(currentNode, global::Ts.Diagnostics.Diagnostics.Expression_produces_a_union_type_that_is_too_complex_to_represent);
            return false;
        }
        return true;
    }

    // checker.go:26531-26549
    internal int GetCrossProductUnionSize(Type[] types)
    {
        var size = 1;
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Union) != 0)
            {
                var n = t.Types().Length;
                // Cap to avoid integer overflow when computing the cross product of many large unions.
                if (n > 0 && size > int.MaxValue / n)
                {
                    return int.MaxValue;
                }
                size *= n;
            }
            else if ((t.flags & TypeFlags.Never) != 0)
            {
                return 0;
            }
        }
        return size;
    }
}
