// Ported from tsgo internal/checker/checker.go (section: index type (keyof) & indexed-access type
// construction, lines 26551-27301). plan/03 §4.2 #18.
//
// Breadth-first scope: the mainline index / indexed-access machinery is real. The deep property-lookup
// leaf getPropertyTypeForIndexType (26867-27079) — ~210 lines of error/suggestion/deprecation
// reporting into ~40 not-yet-landed cross-cluster methods — is left as a stub here (its private leaf
// helpers are consequently unported); getIndexedAccessType's shallow/deferred paths are real.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:26551-26553
    internal Type GetIndexType(Type t) => GetIndexTypeEx(t, IndexFlags.None);

    // PORT-NOTE: compat overload for callers written against the skeleton's 2-arg `GetIndexType`
    // name (Go's faithful name for the 2-arg form is getIndexTypeEx). Behaviour-identical; delegates.
    internal Type GetIndexType(Type t, IndexFlags indexFlags) => GetIndexTypeEx(t, indexFlags);

    // checker.go:26555-26578
    internal Type GetIndexTypeEx(Type t, IndexFlags indexFlags)
    {
        t = GetReducedType(t);
        if (IsNoInferType(t))
        {
            return GetNoInferType(GetIndexTypeEx(t.AsSubstitutionType().baseType, indexFlags));
        }
        if (ShouldDeferIndexType(t, indexFlags))
        {
            return GetIndexTypeForGenericType(t, indexFlags);
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return GetIntersectionType(CoreUtil.Map(t.Types(), x => GetIndexTypeEx(x, indexFlags)));
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return GetUnionType(CoreUtil.Map(t.Types(), x => GetIndexTypeEx(x, indexFlags)));
        }
        if ((t.objectFlags & ObjectFlags.Mapped) != 0)
        {
            return GetIndexTypeForMappedType(t, indexFlags);
        }
        if (t == wildcardType)
        {
            return wildcardType!;
        }
        if ((t.flags & TypeFlags.Unknown) != 0)
        {
            return neverType!;
        }
        if ((t.flags & (TypeFlags.Any | TypeFlags.Never)) != 0)
        {
            return stringNumberSymbolType!;
        }
        var include = ((indexFlags & IndexFlags.NoIndexSignatures) != 0 ? TypeFlags.StringLiteral : TypeFlags.StringLike) |
            ((indexFlags & IndexFlags.StringsOnly) != 0 ? TypeFlags.None : TypeFlags.NumberLike | TypeFlags.ESSymbolLike);
        return GetLiteralTypeFromProperties(t, include, indexFlags == IndexFlags.None);
    }

    // checker.go:26580-26586
    internal Type GetExtractStringType(Type t)
    {
        var extractTypeAlias = GetGlobalExtractSymbol();
        if (extractTypeAlias is not null)
        {
            return GetTypeAliasInstantiation(extractTypeAlias, [t, stringType!], null);
        }
        return stringType!;
    }

    // checker.go:26588-26615
    internal Type GetLiteralTypeFromProperties(Type t, TypeFlags include, bool includeOrigin)
    {
        var key = new PropertiesTypesKey(t.id, include, includeOrigin, (t.objectFlags & ObjectFlags.UnresolvedMembers) != 0);
        if (propertiesTypes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        Type? origin = null;
        if ((includeOrigin && (t.objectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0) || t.alias is not null)
        {
            origin = NewIndexType(t, IndexFlags.None);
        }
        var props = GetPropertiesOfType(t);
        var indexInfos = GetIndexInfosOfType(t);
        var types = new List<Type>(props.Length + indexInfos.Length);
        foreach (var prop in props)
        {
            types.Add(GetLiteralTypeFromProperty(prop, include, false));
        }
        foreach (var info in indexInfos)
        {
            if (info != enumNumberIndexInfo && IsKeyTypeIncluded(info.keyType, include))
            {
                if (info.keyType == stringType && (include & TypeFlags.Number) != 0)
                {
                    types.Add(stringOrNumberType!);
                }
                else
                {
                    types.Add(info.keyType);
                }
            }
        }
        var result = GetUnionTypeEx(types.ToArray(), UnionReduction.Literal, null, origin);
        propertiesTypes[key] = result;
        return result;
    }

    // checker.go:26617-26638
    internal Type GetLiteralTypeFromProperty(Symbol prop, TypeFlags include, bool includeNonPublic)
    {
        if (includeNonPublic || (GetDeclarationModifierFlagsFromSymbol(prop) & ModifierFlags.NonPublicAccessibilityModifier) == 0)
        {
            var t = valueSymbolLinks.Get(GetLateBoundSymbol(prop)).nameType;
            if (t is null)
            {
                if (prop.Name == InternalSymbolName.Default)
                {
                    t = GetStringLiteralType("default");
                }
                else
                {
                    var declName = AstUtilities.GetNameOfDeclaration(prop.ValueDeclaration);
                    if (declName is not null)
                    {
                        t = GetLiteralTypeFromPropertyName(declName);
                    }
                    if (t is null && !IsKnownSymbol(prop))
                    {
                        t = GetStringLiteralType(AstUtilities.SymbolName(prop));
                    }
                }
            }
            if (t is not null && (t.flags & include) != 0)
            {
                return t;
            }
        }
        return neverType!;
    }

    // checker.go:26640-26658
    internal Type GetLiteralTypeFromPropertyName(Node name)
    {
        if (NodeTests.IsPrivateIdentifier(name))
        {
            return neverType!;
        }
        if (NodeTests.IsNumericLiteral(name))
        {
            return GetRegularTypeOfLiteralType(CheckExpression(name));
        }
        if (NodeTests.IsComputedPropertyName(name))
        {
            return GetRegularTypeOfLiteralType(CheckComputedPropertyName(name));
        }
        var propertyName = GetPropertyNameForPropertyNameNode(name);
        if (propertyName != InternalSymbolName.Missing)
        {
            return GetStringLiteralType(propertyName);
        }
        if (AstUtilities.IsExpression(name))
        {
            return GetRegularTypeOfLiteralType(CheckExpression(name));
        }
        return neverType!;
    }

    // checker.go:26660-26665
    internal bool IsKeyTypeIncluded(Type keyType, TypeFlags include) =>
        (keyType.flags & include) != 0 ||
        ((keyType.flags & TypeFlags.Intersection) != 0 && CoreUtil.Some(keyType.Types(), t => IsKeyTypeIncluded(t, include)));

    // checker.go:26667-26686
    internal Type CheckComputedPropertyName(Node node)
    {
        var links = typeNodeLinks.Get(node.Expression()!);
        if (links.resolvedType is null)
        {
            if ((NodeTests.IsTypeLiteralNode(node.Parent!.Parent!) || AstUtilities.IsClassLike(node.Parent!.Parent!) || NodeTests.IsInterfaceDeclaration(node.Parent!.Parent!)) &&
                NodeTests.IsBinaryExpression(node.Expression()!) && node.Expression()!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.InKeyword &&
                !AstUtilities.IsAccessor(node.Parent!))
            {
                links.resolvedType = errorType;
                return links.resolvedType!;
            }
            links.resolvedType = CheckExpression(node.Expression()!);
            // This will allow types number, string, symbol or any, enums, unknown, and any union of these.
            if ((links.resolvedType.flags & TypeFlags.Nullable) != 0 ||
                (!IsTypeAssignableToKind(links.resolvedType, TypeFlags.StringLike | TypeFlags.NumberLike | TypeFlags.ESSymbolLike) &&
                    !IsTypeAssignableTo(links.resolvedType, stringNumberSymbolType!)))
            {
                Error(node, global::Ts.Diagnostics.Diagnostics.A_computed_property_name_must_be_of_type_string_number_symbol_or_any);
            }
        }
        return links.resolvedType!;
    }

    // checker.go:26688-26691
    internal bool IsNoInferType(Type t) =>
        // A NoInfer<T> type is represented as a substitution type with a TypeFlags.Unknown constraint.
        (t.flags & TypeFlags.Substitution) != 0 && (t.AsSubstitutionType().constraint.flags & TypeFlags.Unknown) != 0;

    // checker.go:26693-26698
    internal Type GetSubstitutionIntersection(Type t)
    {
        if (IsNoInferType(t))
        {
            return t.AsSubstitutionType().baseType;
        }
        return GetIntersectionType([t.AsSubstitutionType().constraint, t.AsSubstitutionType().baseType]);
    }

    // checker.go:26700-26706
    internal bool ShouldDeferIndexType(Type t, IndexFlags indexFlags) =>
        (t.flags & TypeFlags.InstantiableNonPrimitive) != 0 ||
        IsGenericTupleType(t) ||
        (IsGenericMappedType(t) && GetNameTypeFromMappedType(t) is not null) ||
        ((t.flags & TypeFlags.Union) != 0 && (indexFlags & IndexFlags.NoReducibleCheck) == 0 && IsGenericReducibleType(t)) ||
        ((t.flags & TypeFlags.Intersection) != 0 && MaybeTypeOfKind(t, TypeFlags.Instantiable) && CoreUtil.Some(t.Types(), IsEmptyAnonymousObjectType));

    // checker.go:26708-26717
    internal MappedTypeNameTypeKind GetMappedTypeNameTypeKind(Type t)
    {
        var nameType = GetNameTypeFromMappedType(t);
        if (nameType is null)
        {
            return MappedTypeNameTypeKind.None;
        }
        if (IsTypeAssignableTo(nameType, GetTypeParameterFromMappedType(t)))
        {
            return MappedTypeNameTypeKind.Filtering;
        }
        return MappedTypeNameTypeKind.Remapping;
    }

    // checker.go:26719-26730
    internal Type GetIndexTypeForGenericType(Type t, IndexFlags indexFlags)
    {
        var key = new CachedTypeKey((indexFlags & IndexFlags.StringsOnly) != 0 ? CachedTypeKind.StringIndexType : CachedTypeKind.IndexType, t.id);
        {
            var cached = cachedTypes.GetValueOrDefault(key);
            if (cached is not null)
            {
                return cached;
            }
        }
        var indexType = NewIndexType(t, indexFlags & IndexFlags.StringsOnly);
        cachedTypes[key] = indexType;
        return indexType;
    }

    // This roughly mirrors resolveMappedTypeMembers in the nongeneric case, reporting a union of keys.
    // checker.go:26737-26787
    internal Type GetIndexTypeForMappedType(Type t, IndexFlags indexFlags)
    {
        var typeParameter = GetTypeParameterFromMappedType(t);
        var constraintType = GetConstraintTypeFromMappedType(t);
        var nameType = GetNameTypeFromMappedType(t.AsMappedType().target ?? t);
        if (nameType is null && (indexFlags & IndexFlags.NoIndexSignatures) == 0)
        {
            // no mapping and no filtering required, just quickly bail to returning the constraint
            return constraintType;
        }
        var keyTypes = new List<Type>();
        void AddMemberForKeyType(Type keyType)
        {
            var propNameType = keyType;
            if (nameType is not null)
            {
                propNameType = InstantiateType(nameType, TypeMapper.AppendTypeMapping(t.AsMappedType().mapper, typeParameter, keyType));
            }
            // `keyof` currently always returns `string | number` for concrete `string` index signatures.
            keyTypes.Add(propNameType == stringType ? stringOrNumberType! : propNameType);
        }
        // Calling getApparentType on the `T` of a `keyof T` in the constraint type of a generic mapped type can
        // trigger a circularity, so we only eagerly manifest the keys if the constraint is non-generic.
        if (IsGenericIndexType(constraintType))
        {
            if (IsMappedTypeWithKeyofConstraintDeclaration(t))
            {
                // We have a generic index and a homomorphic mapping and a key remapping - defer the whole keyof.
                return GetIndexTypeForGenericType(t, indexFlags);
            }
            // Include the generic component in the resulting type.
            ForEachType(constraintType, AddMemberForKeyType);
        }
        else if (IsMappedTypeWithKeyofConstraintDeclaration(t))
        {
            var modifiersType = GetApparentType(GetModifiersTypeFromMappedType(t));
            // The 'T' in 'keyof T'
            ForEachMappedTypePropertyKeyTypeAndIndexSignatureKeyType(modifiersType, TypeFlags.StringOrNumberLiteralOrUnique, (indexFlags & IndexFlags.StringsOnly) != 0, AddMemberForKeyType);
        }
        else
        {
            ForEachType(GetLowerBoundOfKeyType(constraintType), AddMemberForKeyType);
        }
        // Compare the resulting list with the original constraintType so we can preserve aliases/origin if possible.
        Type result;
        if ((indexFlags & IndexFlags.NoIndexSignatures) != 0)
        {
            result = FilterType(GetUnionType(keyTypes.ToArray()), t2 => (t2.flags & (TypeFlags.Any | TypeFlags.String)) == 0);
        }
        else
        {
            result = GetUnionType(keyTypes.ToArray());
        }
        if ((result.flags & TypeFlags.Union) != 0 && (constraintType.flags & TypeFlags.Union) != 0 && GetTypeListKey(result.Types()) == GetTypeListKey(constraintType.Types()))
        {
            return constraintType;
        }
        return result;
    }

    // checker.go:26789-26791
    internal Type GetIndexedAccessType(Type objectType, Type indexType) =>
        GetIndexedAccessTypeEx(objectType, indexType, AccessFlags.None, null, null);

    // checker.go:26793-26799
    internal Type GetIndexedAccessTypeEx(Type objectType, Type indexType, AccessFlags accessFlags, Node? accessNode, TypeAlias? alias)
    {
        var result = GetIndexedAccessTypeOrUndefined(objectType, indexType, accessFlags, accessNode, alias);
        if (result is null)
        {
            result = accessNode is not null ? errorType : unknownType;
        }
        return result!;
    }

    // checker.go:26801-26865
    internal Type? GetIndexedAccessTypeOrUndefined(Type objectType, Type indexType, AccessFlags accessFlags, Node? accessNode, TypeAlias? alias)
    {
        if (objectType == wildcardType || indexType == wildcardType)
        {
            return wildcardType;
        }
        objectType = GetReducedType(objectType);
        // If the object type has a string index signature and no other members, simplify the result.
        if (IsStringIndexSignatureOnlyType(objectType) && (indexType.flags & TypeFlags.Nullable) == 0 && IsTypeAssignableToKind(indexType, TypeFlags.String | TypeFlags.Number))
        {
            indexType = stringType!;
        }
        // In noUncheckedIndexedAccess mode, index signatures in read position have 'undefined' included.
        if (compilerOptions.NoUncheckedIndexedAccess == Ts.Core.Tristate.True && (accessFlags & AccessFlags.ExpressionPosition) != 0)
        {
            accessFlags |= AccessFlags.IncludeUndefined;
        }
        // If the index type is generic (or object type generic and not from an expression, and not fixed tuple
        // indexing), defer by creating an indexed access type.
        if (ShouldDeferIndexedAccessType(objectType, indexType, accessNode))
        {
            if ((objectType.flags & TypeFlags.AnyOrUnknown) != 0)
            {
                return objectType;
            }
            var persistentAccessFlags = accessFlags & AccessFlags.Persistent;
            var key = GetIndexedAccessKey(objectType, indexType, accessFlags, alias);
            var t = indexedAccessTypes.GetValueOrDefault(key);
            if (t is null)
            {
                t = NewIndexedAccessType(objectType, indexType, persistentAccessFlags);
                t.alias = alias;
                indexedAccessTypes[key] = t;
            }
            return t;
        }
        // Resolve T[K] to the type of the property in T selected by K.
        var apparentObjectType = GetReducedApparentType(objectType);
        if ((indexType.flags & TypeFlags.Union) != 0 && (indexType.flags & TypeFlags.Boolean) == 0)
        {
            var propTypes = new List<Type>();
            var wasMissingProp = false;
            foreach (var t in indexType.Types())
            {
                var propType = GetPropertyTypeForIndexType(objectType, apparentObjectType, t, indexType, accessNode, accessFlags | (wasMissingProp ? AccessFlags.SuppressNoImplicitAnyError : 0));
                if (propType is not null)
                {
                    propTypes.Add(propType);
                }
                else if (accessNode is null)
                {
                    // If there's no error node, we can immediately stop, since error reporting is off
                    return null;
                }
                else
                {
                    // Otherwise we set a flag and return at the end of the loop so we still mark all errors
                    wasMissingProp = true;
                }
            }
            if (wasMissingProp)
            {
                return null;
            }
            if ((accessFlags & AccessFlags.Writing) != 0)
            {
                return GetIntersectionTypeEx(propTypes.ToArray(), IntersectionFlags.None, alias);
            }
            return GetUnionTypeEx(propTypes.ToArray(), UnionReduction.Literal, alias, null);
        }
        return GetPropertyTypeForIndexType(objectType, apparentObjectType, indexType, indexType, accessNode, accessFlags | AccessFlags.CacheSymbol | AccessFlags.ReportDeprecated);
    }

    // checker.go:26867-27079 — deep property-lookup + error/suggestion/deprecation machinery.
    // BREADTH-FIRST STUB: routes into ~40 not-yet-landed cross-cluster methods; landed by a later
    // pass. getIndexedAccessType's shallow/deferred paths above are real.
    internal Type? GetPropertyTypeForIndexType(Type originalObjectType, Type objectType, Type indexType, Type fullIndexType, Node? accessNode, AccessFlags accessFlags) =>
        throw new NotImplementedException("getPropertyTypeForIndexType");

    // checker.go:27123-27133
    internal static Node GetIndexNodeForAccessExpression(Node accessNode)
    {
        switch (accessNode.Kind)
        {
            case SyntaxKind.ElementAccessExpression:
                return accessNode.AsElementAccessExpression().ArgumentExpression!;
            case SyntaxKind.IndexedAccessType:
                return accessNode.AsIndexedAccessTypeNode().IndexType!;
            case SyntaxKind.ComputedPropertyName:
                return accessNode.Expression()!;
        }
        return accessNode;
    }

    // checker.go:27135-27139
    internal void ErrorIfWritingToReadonlyIndex(IndexInfo? indexInfo, Type objectType, Node? accessExpression)
    {
        if (indexInfo is not null && indexInfo.isReadonly && accessExpression is not null && (AstUtilities.IsAssignmentTarget(accessExpression) || IsDeleteTarget(accessExpression)))
        {
            Error(accessExpression, global::Ts.Diagnostics.Diagnostics.Index_signature_in_type_0_only_permits_reading, TypeToString(objectType));
        }
    }

    // checker.go:27221-27229
    internal string GetPropertyNameFromIndex(Type indexType, Node? accessNode)
    {
        if (IsTypeUsableAsPropertyName(indexType))
        {
            return GetPropertyNameFromType(indexType);
        }
        if (accessNode is not null && AstUtilities.IsPropertyName(accessNode))
        {
            return GetPropertyNameForPropertyNameNode(accessNode);
        }
        return InternalSymbolName.Missing;
    }

    // checker.go:27231-27234
    internal bool IsStringIndexSignatureOnlyTypeWorker(Type t) =>
        ((t.flags & TypeFlags.Object) != 0 && !IsGenericMappedType(t) && GetPropertiesOfType(t).Length == 0 && GetIndexInfosOfType(t).Length == 1 && GetIndexInfoOfType(t, stringType!) is not null) ||
        ((t.flags & TypeFlags.UnionOrIntersection) != 0 && CoreUtil.Every(t.Types(), IsStringIndexSignatureOnlyType));

    // checker.go:27236-27245
    internal bool ShouldDeferIndexedAccessType(Type objectType, Type indexType, Node? accessNode)
    {
        if (IsGenericIndexType(indexType))
        {
            return true;
        }
        if (accessNode is not null && !NodeTests.IsIndexedAccessTypeNode(accessNode))
        {
            return IsGenericTupleType(objectType) && !IndexTypeLessThan(indexType, GetTotalFixedElementCount(objectType.TargetTupleType()));
        }
        return (IsGenericObjectType(objectType) && !(IsTupleType(objectType) && IndexTypeLessThan(indexType, GetTotalFixedElementCount(objectType.TargetTupleType())))) ||
            IsGenericReducibleType(objectType);
    }

    // checker.go:27247-27258
    internal static bool IndexTypeLessThan(Type indexType, int limit) =>
        EveryType(indexType, t =>
        {
            if ((t.flags & TypeFlags.StringOrNumberLiteral) != 0)
            {
                var propName = GetPropertyNameFromType(t);
                if (IsNumericLiteralName(propName))
                {
                    var index = Ts.JsNum.JsNum.FromString(propName);
                    return index >= new Ts.JsNum.Number(0) && index < new Ts.JsNum.Number(limit);
                }
            }
            return false;
        });

    // checker.go:27260-27265
    internal Type GetNoInferType(Type t)
    {
        if (IsNoInferTargetType(t))
        {
            return GetOrCreateSubstitutionType(t, unknownType!);
        }
        return t;
    }

    // checker.go:27267-27275
    internal bool IsNoInferTargetType(Type t) =>
        // A more conservative and predictable form of couldContainTypeVariables.
        ((t.flags & TypeFlags.UnionOrIntersection) != 0 && CoreUtil.Some(t.AsUnionOrIntersectionType()!.types, IsNoInferTargetType)) ||
        ((t.flags & TypeFlags.Substitution) != 0 && !IsNoInferType(t) && IsNoInferTargetType(t.AsSubstitutionType().baseType)) ||
        ((t.flags & TypeFlags.Object) != 0 && !IsEmptyAnonymousObjectType(t)) ||
        ((t.flags & (TypeFlags.Instantiable & ~TypeFlags.Substitution)) != 0 && !IsPatternLiteralType(t));

    // checker.go:27277-27282
    internal Type GetSubstitutionType(Type baseType, Type constraint)
    {
        if ((constraint.flags & TypeFlags.AnyOrUnknown) != 0 || constraint == baseType || (baseType.flags & TypeFlags.Any) != 0)
        {
            return baseType;
        }
        return GetOrCreateSubstitutionType(baseType, constraint);
    }

    // checker.go:27284-27292
    internal Type GetOrCreateSubstitutionType(Type baseType, Type constraint)
    {
        var key = new SubstitutionTypeKey(baseType.id, constraint.id);
        {
            var cached = substitutionTypes.GetValueOrDefault(key);
            if (cached is not null)
            {
                return cached;
            }
        }
        var result = NewSubstitutionType(baseType, constraint);
        substitutionTypes[key] = result;
        return result;
    }

    // checker.go:27294-27300
    internal Type GetBaseConstraintOrType(Type t)
    {
        var constraint = GetBaseConstraintOfType(t);
        if (constraint is not null)
        {
            return constraint;
        }
        return t;
    }
}
