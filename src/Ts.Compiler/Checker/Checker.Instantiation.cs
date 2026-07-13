// Ported from tsgo internal/checker/checker.go (section: Instantiation, lines ~20658-22712).
// plan/03 §4.2 partial #14. Covers the instantiateType family, the load-bearing depth guards
// (instantiationDepth==100 / instantiationCount>=5,000,000, checker.go:22016) and the active
// mapper stack, apparent/reduced-type resolution, type-argument defaults, and the mapped-type
// accessor family. Heavy member-resolution functions that also fall in this line range
// (resolveMappedTypeMembers / resolveUnionTypeMembers / resolveIntersectionTypeMembers /
// getUnionSignatures / createUnionOrIntersectionProperty / getTypeOfMappedSymbol / …) are
// deferred breadth-first — their stubs remain in the Members/Signatures dedup surface and are
// reported as remaining. A short DEFERRED region at the tail carries the #14 stubs this file's
// implementations reference but do not yet implement.
//
// §1.5: cross-package types are `global::Ts.<Pkg>.<Class>.Member`; CoreUtil / NodeTests /
// AstUtilities are used directly.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:20658-20696
    // PORT-NOTE: return kept non-null to match existing #13 callers that store the result into a
    // SymbolTable (non-null values). The Go nil-input path returns nil verbatim via `null!`.
    internal Symbol InstantiateSymbol(Symbol symbol, TypeMapper? m)
    {
        if (symbol is null)
        {
            return null!;
        }
        var links = valueSymbolLinks.Get(symbol);
        if (m is not null && m.MapsThisOnly() && IsThisless(symbol))
        {
            return symbol;
        }
        // If the type of the symbol is already resolved, and if that type could not possibly
        // be affected by instantiation, simply return the symbol itself.
        if (links.resolvedType is not null && !couldContainTypeVariables(links.resolvedType))
        {
            if ((symbol.Flags & SymbolFlags.SetAccessor) == 0)
            {
                return symbol;
            }
            // If we're a setter, check writeType.
            if (links.writeType is not null && !couldContainTypeVariables(links.writeType))
            {
                return symbol;
            }
        }
        if ((symbol.CheckFlags & CheckFlags.Instantiated) != 0)
        {
            // If symbol being instantiated is itself a instantiation, fetch the original target and combine the
            // type mappers. This ensures that original type identities are properly preserved and that aliases
            // always reference a non-aliases.
            symbol = links.target!;
            m = CombineTypeMappers(links.mapper, m);
        }
        // Keep the flags from the symbol we're instantiating.  Mark that is instantiated, and
        // also transient so that we can just store data on it directly.
        var result = NewSymbol(symbol.Flags, symbol.Name);
        result.CheckFlags = CheckFlags.Instantiated | symbol.CheckFlags & (CheckFlags.Readonly | CheckFlags.Late | CheckFlags.OptionalParameter | CheckFlags.RestParameter);
        result.Declarations = symbol.Declarations;
        result.Parent = symbol.Parent;
        result.ValueDeclaration = symbol.ValueDeclaration;
        var resultLinks = valueSymbolLinks.Get(result);
        resultLinks.target = symbol;
        resultLinks.mapper = m;
        resultLinks.nameType = links.nameType;
        return result;
    }

    // checker.go:21088-21123
    internal Signature CombineUnionOrIntersectionMemberSignatures(Signature left, Signature right, bool isUnion)
    {
        var typeParams = left.typeParameters;
        if (typeParams.Len() == 0)
        {
            typeParams = right.typeParameters;
        }
        TypeMapper? paramMapper = null;
        if (left.typeParameters.Len() != 0 && right.typeParameters.Len() != 0)
        {
            // We just use the type parameter defaults from the first signature
            paramMapper = TypeMapper.NewTypeMapper(right.typeParameters!, left.typeParameters!);
        }
        var flags = (left.flags | right.flags) & (SignatureFlags.PropagatingFlags & ~SignatureFlags.HasRestParameter);
        var declaration = left.declaration;
        var parameters = CombineUnionOrIntersectionParameters(left, right, paramMapper, isUnion);
        var lastParam = CoreUtil.LastOrNil(parameters);
        if (lastParam is not null && (lastParam.CheckFlags & CheckFlags.RestParameter) != 0)
        {
            flags |= SignatureFlags.HasRestParameter;
        }
        var thisParam = CombineUnionOrIntersectionThisParam(left.thisParameter, right.thisParameter, paramMapper, isUnion);
        var minArgCount = Math.Max(left.minArgumentCount, right.minArgumentCount);
        var result = NewSignature(flags, declaration, typeParams, thisParam, parameters, null /*resolvedReturnType*/, null /*resolvedTypePredicate*/, minArgCount);
        Signature[] leftSignatures;
        if (left.composite is not null && left.composite.isUnion)
        {
            leftSignatures = left.composite.signatures;
        }
        else
        {
            leftSignatures = [left];
        }
        result.composite = new CompositeSignature { isUnion = isUnion, signatures = [.. leftSignatures, right] };
        if (paramMapper is not null)
        {
            if (left.composite is not null && left.composite.isUnion == isUnion && left.mapper is not null)
            {
                result.mapper = CombineTypeMappers(left.mapper, paramMapper);
            }
            else
            {
                result.mapper = paramMapper;
            }
        }
        else if (left.composite is not null && left.composite.isUnion == isUnion)
        {
            result.mapper = left.mapper;
        }
        return result;
    }

    // checker.go:21125-21191
    internal Symbol[] CombineUnionOrIntersectionParameters(Signature left, Signature right, TypeMapper? mapper, bool isUnion)
    {
        var leftCount = GetParameterCount(left);
        var rightCount = GetParameterCount(right);
        int longestCount;
        Signature longest, shorter;
        if (leftCount >= rightCount)
        {
            (longestCount, longest, shorter) = (leftCount, left, right);
        }
        else
        {
            (longestCount, longest, shorter) = (rightCount, right, left);
        }
        var eitherHasEffectiveRest = HasEffectiveRestParameter(left) || HasEffectiveRestParameter(right);
        var needsExtraRestElement = eitherHasEffectiveRest && !HasEffectiveRestParameter(longest);
        var parameters = new Symbol[longestCount + (needsExtraRestElement ? 1 : 0)];
        for (int i = 0; i < longestCount; i++)
        {
            var longestParamType = TryGetTypeAtPosition(longest, i);
            if (longest == right)
            {
                longestParamType = InstantiateType(longestParamType, mapper);
            }
            var shorterParamType = TryGetTypeAtPosition(shorter, i) ?? unknownType!;
            if (shorter == right)
            {
                shorterParamType = InstantiateType(shorterParamType, mapper);
            }
            var combinedParamType = GetUnionOrIntersectionType([longestParamType!, shorterParamType], !isUnion, UnionReduction.Literal);
            var isRestParam = eitherHasEffectiveRest && !needsExtraRestElement && i == (longestCount - 1);
            var isOptional = i >= GetMinArgumentCount(longest) && i >= GetMinArgumentCount(shorter);
            var leftName = "";
            var rightName = "";
            if (i < leftCount)
            {
                leftName = GetParameterNameAtPosition(left, i);
            }
            if (i < rightCount)
            {
                rightName = GetParameterNameAtPosition(right, i);
            }
            string paramName = "";
            if (leftName == rightName)
            {
                paramName = leftName;
            }
            else if (leftName == "")
            {
                paramName = rightName;
            }
            else if (rightName == "")
            {
                paramName = leftName;
            }
            if (paramName == "")
            {
                paramName = "arg" + i.ToString(CultureInfo.InvariantCulture);
            }
            var paramSymbol = NewSymbolEx(SymbolFlags.FunctionScopedVariable | (isOptional && !isRestParam ? SymbolFlags.Optional : 0), paramName,
                isRestParam ? CheckFlags.RestParameter : (isOptional ? CheckFlags.OptionalParameter : 0));
            var links = valueSymbolLinks.Get(paramSymbol);
            if (isRestParam)
            {
                links.resolvedType = CreateArrayType(combinedParamType);
            }
            else
            {
                links.resolvedType = combinedParamType;
            }
            parameters[i] = paramSymbol;
        }
        if (needsExtraRestElement)
        {
            var restParamSymbol = NewSymbolEx(SymbolFlags.FunctionScopedVariable, "args", CheckFlags.RestParameter);
            var links = valueSymbolLinks.Get(restParamSymbol);
            links.resolvedType = CreateArrayType(GetTypeAtPosition(shorter, longestCount));
            if (shorter == right)
            {
                links.resolvedType = InstantiateType(links.resolvedType, mapper);
            }
            parameters[longestCount] = restParamSymbol;
        }
        return parameters;
    }

    // checker.go:21193-21207
    internal Symbol? CombineUnionOrIntersectionThisParam(Symbol? left, Symbol? right, TypeMapper? mapper, bool isUnion)
    {
        if (left is null)
        {
            return right;
        }
        if (right is null)
        {
            return left;
        }
        // A signature `this` type might be a read or a write position... It's very possible that it should be invariant
        // and we should refuse to merge signatures if there are `this` types and they do not match. However, so as to be
        // permissive when calling, for now, we'll intersect the `this` types just like we do for param types in union signatures.
        var thisType = GetUnionOrIntersectionType([GetTypeOfSymbol(left), InstantiateType(GetTypeOfSymbol(right), mapper)], !isUnion, UnionReduction.Literal);
        return CreateSymbolWithType(left, thisType);
    }

    // checker.go:21634-21667 — For a type parameter, return the base constraint; for the string,
    // number, boolean, and symbol primitive types, return the corresponding object types.
    internal Type GetApparentType(Type t)
    {
        var originalType = t;
        if ((t.flags & TypeFlags.Instantiable) != 0)
        {
            t = GetBaseConstraintOfType(t)!;
            if (t is null)
            {
                t = unknownType!;
            }
        }
        if ((t.objectFlags & ObjectFlags.Mapped) != 0)
        {
            return GetApparentTypeOfMappedType(t);
        }
        if ((t.objectFlags & ObjectFlags.Reference) != 0 && t != originalType)
        {
            return GetTypeWithThisArgument(t, originalType, false /*needsApparentType*/);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return GetApparentTypeOfIntersectionType(t, originalType);
        }
        if ((t.flags & TypeFlags.StringLike) != 0)
        {
            return globalStringType!;
        }
        if ((t.flags & TypeFlags.NumberLike) != 0)
        {
            return globalNumberType!;
        }
        if ((t.flags & TypeFlags.BigIntLike) != 0)
        {
            return GetGlobalBigIntType();
        }
        if ((t.flags & TypeFlags.BooleanLike) != 0)
        {
            return globalBooleanType!;
        }
        if ((t.flags & TypeFlags.ESSymbolLike) != 0)
        {
            return GetGlobalESSymbolType();
        }
        if ((t.flags & TypeFlags.NonPrimitive) != 0)
        {
            return emptyObjectType!;
        }
        if ((t.flags & TypeFlags.Index) != 0)
        {
            return stringNumberSymbolType!;
        }
        if ((t.flags & TypeFlags.Unknown) != 0 && !strictNullChecks)
        {
            return emptyObjectType!;
        }
        return t;
    }

    // checker.go:21669-21675
    internal Type GetApparentTypeOfMappedType(Type t)
    {
        var m = t.AsMappedType();
        if (m.resolvedApparentType is null)
        {
            m.resolvedApparentType = GetResolvedApparentTypeOfMappedType(t);
        }
        return m.resolvedApparentType;
    }

    // checker.go:21677-21699
    internal Type GetResolvedApparentTypeOfMappedType(Type t)
    {
        var target = t.AsMappedType().target ?? t;
        var typeVariable = GetHomomorphicTypeVariable(target);
        if (typeVariable is not null && target.AsMappedType().declaration!.NameType is null)
        {
            // We have a homomorphic mapped type or an instantiation of a homomorphic mapped type, i.e. a type
            // of the form { [P in keyof T]: X }. Obtain the modifiers type (the T of the keyof T), and if it is
            // another generic mapped type, recursively obtain its apparent type. Otherwise, obtain its base
            // constraint. Then, if every constituent of the base constraint is an array or tuple type, apply
            // this mapped type to the base constraint.
            var modifiersType = GetModifiersTypeFromMappedType(t);
            Type? baseConstraint;
            if (IsGenericMappedType(modifiersType))
            {
                baseConstraint = GetApparentTypeOfMappedType(modifiersType);
            }
            else
            {
                baseConstraint = GetBaseConstraintOfType(modifiersType);
            }
            if (baseConstraint is not null && EveryType(baseConstraint, s => IsArrayOrTupleType(s) || IsArrayOrTupleOrIntersection(s)))
            {
                return InstantiateType(target, TypeMapper.PrependTypeMapping(typeVariable, baseConstraint, t.AsMappedType().mapper));
            }
        }
        return t;
    }

    // checker.go:21701-21716
    internal Type GetApparentTypeOfIntersectionType(Type t, Type thisArgument)
    {
        if (t == thisArgument)
        {
            var d = t.AsIntersectionType();
            if (d.resolvedApparentType is null)
            {
                d.resolvedApparentType = GetTypeWithThisArgument(t, thisArgument, true /*needApparentType*/);
            }
            return d.resolvedApparentType;
        }
        var key = new CachedTypeKey(CachedTypeKind.ApparentType, thisArgument.id);
        var result = cachedTypes.GetValueOrDefault(key);
        if (result is null)
        {
            result = GetTypeWithThisArgument(t, thisArgument, true /*needApparentType*/);
            cachedTypes[key] = result;
        }
        return result;
    }

    // checker.go:21724-21747 — reduced form of the given type (plan/03 §4.2 #14 line-range).
    internal Type GetReducedType(Type t)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            if ((t.objectFlags & ObjectFlags.ContainsIntersections) != 0)
            {
                var cached = t.AsUnionType().resolvedReducedType;
                if (cached is not null)
                {
                    return cached;
                }
                var reducedType = GetReducedUnionType(t);
                t.AsUnionType().resolvedReducedType = reducedType;
                return reducedType;
            }
        }
        else if ((t.flags & TypeFlags.Intersection) != 0)
        {
            if ((t.objectFlags & ObjectFlags.IsNeverIntersectionComputed) == 0)
            {
                t.objectFlags |= ObjectFlags.IsNeverIntersectionComputed;
                if (CoreUtil.Some(GetPropertiesOfUnionOrIntersectionType(t), IsNeverReducedProperty))
                {
                    t.objectFlags |= ObjectFlags.IsNeverIntersection;
                }
            }
            if ((t.objectFlags & ObjectFlags.IsNeverIntersection) != 0)
            {
                return neverType!;
            }
        }
        return t;
    }

    // checker.go:21749-21759
    internal Type GetReducedUnionType(Type unionType)
    {
        var reducedTypes = CoreUtil.SameMap(unionType.Types(), GetReducedType);
        if (CoreUtil.Same(reducedTypes, unionType.Types()))
        {
            return unionType;
        }
        var reduced = GetUnionType(reducedTypes!);
        if ((reduced.flags & TypeFlags.Union) != 0)
        {
            reduced.AsUnionType().resolvedReducedType = reduced;
        }
        return reduced;
    }

    // checker.go:21761-21763
    internal bool IsNeverReducedProperty(Symbol prop) => IsDiscriminantWithNeverType(prop) || IsConflictingPrivateProperty(prop);

    // checker.go:21765-21771 — reduce both before and after obtaining the apparent type.
    internal Type GetReducedApparentType(Type t) => GetReducedType(GetApparentType(GetReducedType(t)));

    // checker.go:21773-21785
    internal Diagnostic? ElaborateNeverIntersection(Diagnostic? chain, Node? node, Type t)
    {
        if ((t.flags & TypeFlags.Intersection) != 0 && (t.objectFlags & ObjectFlags.IsNeverIntersection) != 0)
        {
            var neverProp = CoreUtil.Find(GetPropertiesOfUnionOrIntersectionType(t), IsDiscriminantWithNeverType);
            if (neverProp is not null)
            {
                return NewDiagnosticChainForNode(chain, node, Diag.The_intersection_0_was_reduced_to_never_because_property_1_has_conflicting_types_in_some_constituents, TypeToStringEx(t, null, TypeFormatFlags.NoTypeReduction, null), SymbolToString(neverProp));
            }
            var privateProp = CoreUtil.Find(GetPropertiesOfUnionOrIntersectionType(t), IsConflictingPrivateProperty);
            if (privateProp is not null)
            {
                return NewDiagnosticChainForNode(chain, node, Diag.The_intersection_0_was_reduced_to_never_because_property_1_exists_in_multiple_constituents_and_is_private_in_some, TypeToStringEx(t, null, TypeFormatFlags.NoTypeReduction, null), SymbolToString(privateProp));
            }
        }
        return chain;
    }

    // checker.go:21787-21791
    internal bool IsDiscriminantWithNeverType(Symbol prop)
    {
        // Return true for a synthetic non-optional property with non-uniform types, where at least one is
        // a literal type and none is never, that reduces to never.
        return (prop.Flags & SymbolFlags.Optional) == 0 &&
            (prop.CheckFlags & (CheckFlags.NonUniformAndLiteral | CheckFlags.HasNeverType)) == CheckFlags.NonUniformAndLiteral &&
            (GetTypeOfSymbol(prop).flags & TypeFlags.Never) != 0;
    }

    // checker.go:21798-21836
    internal Type[] GetTypeArguments(Type t)
    {
        var d = t.AsTypeReference()!;
        if (d.resolvedTypeArguments is null)
        {
            var n = d.target!.AsInterfaceType()!;
            if (!PushTypeResolution(t, TypeSystemPropertyName.ResolvedTypeArguments))
            {
                return Enumerable.Repeat(errorType!, LenOrZero(n.TypeParameters())).ToArray();
            }
            Type[]? typeArguments = null;
            var node = t.AsTypeReference()!.node;
            if (node is not null)
            {
                switch (node.Kind)
                {
                    case SyntaxKind.TypeReference:
                        typeArguments = ConcatTypes(n.OuterTypeParameters(), GetEffectiveTypeArguments(node, n.LocalTypeParameters()));
                        break;
                    case SyntaxKind.ArrayType:
                        typeArguments = [GetTypeFromTypeNode(node.AsArrayTypeNode().ElementType!)];
                        break;
                    case SyntaxKind.TupleType:
                        typeArguments = CoreUtil.Map(node.Elements(), GetTypeFromTypeNode);
                        break;
                    default:
                        throw new InvalidOperationException("Unhandled case in getTypeArguments");
                }
            }
            if (PopTypeResolution())
            {
                if (d.resolvedTypeArguments is null)
                {
                    d.resolvedTypeArguments = InstantiateTypes(typeArguments, d.mapper);
                }
            }
            else
            {
                if (d.resolvedTypeArguments is null)
                {
                    d.resolvedTypeArguments = Enumerable.Repeat(errorType!, LenOrZero(n.TypeParameters())).ToArray();
                }
                var errorNode = node ?? currentNode;
                if (d.target!.symbol is not null)
                {
                    Error(errorNode, Diag.Type_arguments_for_0_circularly_reference_themselves, SymbolToString(d.target.symbol));
                }
                else
                {
                    Error(errorNode, Diag.Tuple_type_arguments_circularly_reference_themselves);
                }
            }
        }
        return d.resolvedTypeArguments!;
    }

    // checker.go:21838-21840
    internal Type[] GetEffectiveTypeArguments(Node node, Type[]? typeParameters) =>
        FillMissingTypeArguments(CoreUtil.Map(node.TypeArguments(), GetTypeFromTypeNode), typeParameters, GetMinTypeArgumentCount(typeParameters), AstUtilities.IsInJSFile(node)) ?? [];

    // checker.go:21843-21851 — minimum type args needed to satisfy all non-optional type parameters.
    internal int GetMinTypeArgumentCount(Type[]? typeParameters)
    {
        var minTypeArgumentCount = 0;
        if (typeParameters is not null)
        {
            for (int i = 0; i < typeParameters.Length; i++)
            {
                if (!HasTypeParameterDefault(typeParameters[i]))
                {
                    minTypeArgumentCount = i + 1;
                }
            }
        }
        return minTypeArgumentCount;
    }

    // checker.go:21853-21857
    internal bool HasTypeParameterDefault(Type t) =>
        t.symbol is not null && CoreUtil.Some(t.symbol.Declarations, d => NodeTests.IsTypeParameterDeclaration(d) && d.AsTypeParameterDeclaration().DefaultType is not null);

    // checker.go:21859-21889
    internal Type[]? FillMissingTypeArguments(Type[]? typeArguments, Type[]? typeParameters, int minTypeArgumentCount, bool isJavaScriptImplicitAny)
    {
        var numTypeParameters = LenOrZero(typeParameters);
        if (numTypeParameters == 0)
        {
            return null;
        }
        var numTypeArguments = LenOrZero(typeArguments);
        if (isJavaScriptImplicitAny || numTypeArguments < numTypeParameters)
        {
            var result = new Type[numTypeParameters];
            for (int i = 0; i < numTypeArguments; i++)
            {
                result[i] = typeArguments![i];
            }
            // Map invalid forward references in default types to the error type
            for (int i = numTypeArguments; i < numTypeParameters; i++)
            {
                result[i] = errorType!;
            }
            var baseDefaultType = GetDefaultTypeArgumentType(isJavaScriptImplicitAny);
            for (int i = numTypeArguments; i < numTypeParameters; i++)
            {
                var defaultType = GetDefaultFromTypeParameter(typeParameters![i]);

                if (isJavaScriptImplicitAny && defaultType is not null && (IsTypeIdenticalTo(defaultType, unknownType!) || IsTypeIdenticalTo(defaultType, emptyObjectType!)))
                {
                    defaultType = anyType;
                }

                if (defaultType is not null)
                {
                    result[i] = InstantiateType(defaultType, TypeMapper.NewTypeMapper(typeParameters, result));
                }
                else
                {
                    result[i] = baseDefaultType;
                }
            }
            return result;
        }
        return typeArguments;
    }

    // checker.go:21891-21896
    internal Type GetDefaultTypeArgumentType(bool isInJavaScriptFile) => isInJavaScriptFile ? anyType! : unknownType!;

    // checker.go:21901-21910 — default type for a type parameter (instantiated target's default).
    internal Type? GetDefaultFromTypeParameter(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) == 0)
        {
            return null;
        }
        var defaultType = GetResolvedTypeParameterDefault(t);
        if (defaultType != noConstraintType && defaultType != circularConstraintType)
        {
            return defaultType;
        }
        return null;
    }

    // checker.go:21912-21947
    internal Type? GetResolvedTypeParameterDefault(Type t)
    {
        var d = t.AsTypeParameter();
        if (d.resolvedDefaultType is null)
        {
            if (d.target is not null)
            {
                var targetDefault = GetResolvedTypeParameterDefault(d.target);
                if (targetDefault is not null)
                {
                    d.resolvedDefaultType = InstantiateType(targetDefault, d.mapper);
                }
                else
                {
                    d.resolvedDefaultType = noConstraintType;
                }
            }
            else
            {
                // To block recursion, set the initial value to the resolvingDefaultType.
                d.resolvedDefaultType = resolvingDefaultType;
                var defaultType = noConstraintType;
                if (t.symbol is not null)
                {
                    var defaultDeclaration = CoreUtil.FirstNonNil(t.symbol.Declarations, decl =>
                    {
                        if (NodeTests.IsTypeParameterDeclaration(decl))
                        {
                            return decl.AsTypeParameterDeclaration().DefaultType;
                        }
                        return null;
                    });
                    if (defaultDeclaration is not null)
                    {
                        defaultType = GetTypeFromTypeNode(defaultDeclaration);
                    }
                }
                if (d.resolvedDefaultType == resolvingDefaultType)
                {
                    // If we have not been called recursively, set the correct default type.
                    d.resolvedDefaultType = defaultType;
                }
            }
        }
        else if (d.resolvedDefaultType == resolvingDefaultType)
        {
            // If we are called recursively for this type parameter, mark the default as circular.
            d.resolvedDefaultType = circularConstraintType;
        }
        return d.resolvedDefaultType;
    }

    // checker.go:21949-21952
    internal Type GetDefaultOrUnknownFromTypeParameter(Type t)
    {
        var result = GetDefaultFromTypeParameter(t);
        return result ?? unknownType!;
    }

    // checker.go:21996-21998
    internal bool SymbolIsValue(Symbol symbol) => SymbolIsValueEx(symbol, false /*includeTypeOnlyMembers*/);

    // checker.go:22000-22003
    internal bool SymbolIsValueEx(Symbol symbol, bool includeTypeOnlyMembers) =>
        (symbol.Flags & SymbolFlags.Value) != 0 ||
        (symbol.Flags & SymbolFlags.Alias) != 0 && (GetSymbolFlagsEx(symbol, !includeTypeOnlyMembers, false /*excludeLocalMeanings*/) & SymbolFlags.Value) != 0;

    // checker.go:22005-22007
    internal Type InstantiateType(Type? t, TypeMapper? m) => InstantiateTypeWithAlias(t, m, null /*alias*/);

    // checker.go:22009-22049 — Depth guards (22016) are load-bearing (plan/03 §10.3): the constants
    // 100 and 5,000,000 are behavior, not tunables. Preserved verbatim.
    internal Type InstantiateTypeWithAlias(Type? t, TypeMapper? m, TypeAlias? alias)
    {
        // Check for type variables in the alias, so things like `type Brand<T> = number & {}` can potentially be
        // copied with new alias type args, despite them being unreferenced.
        if (t is null || m is null || !(couldContainTypeVariables(t) || (t.alias is not null && LenOrZero(t.alias.typeArguments) > 0 && CoreUtil.Some(t.alias.typeArguments, couldContainTypeVariables))))
        {
            return t!;
        }
        if (instantiationDepth == 100 || instantiationCount >= 5_000_000)
        {
            // We have reached 100 recursive type instantiations, or 5M type instantiations caused by the same statement
            // or expression. There is a very high likelihood we're dealing with a combination of infinite generic types
            // that perpetually generate new type identities, so we stop the recursion here by yielding the error type.
            // PORT-NOTE: the `tr.Instant(tracing.PhaseCheckTypes, "instantiateType_DepthLimit", …)` trace event
            // (checker.go:22020-22022) is deferred to the tracer.go wave (phase 9); the Tracer shell has no Instant
            // yet and the event has no observable effect on the depth-guard behavior below.
            Error(currentNode, Diag.Type_instantiation_is_excessively_deep_and_possibly_infinite);
            return errorType!;
        }
        var index = FindActiveMapper(m);
        if (index == -1)
        {
            PushActiveMapper(m);
        }
        var b = new KeyBuilder();
        b.WriteType(t);
        b.WriteAlias(alias);
        var key = b.Hash();
        var cache = activeTypeMappersCaches[index != -1 ? index : activeTypeMappersCaches.Count - 1];
        if (cache.TryGetValue(key, out var cachedType))
        {
            return cachedType;
        }
        TotalInstantiationCount++;
        instantiationCount++;
        instantiationDepth++;
        var result = InstantiateTypeWorker(t, m, alias);
        if (index == -1)
        {
            PopActiveMapper();
        }
        else
        {
            cache[key] = result;
        }
        instantiationDepth--;
        return result;
    }

    // checker.go:22051-22064
    internal void PushActiveMapper(TypeMapper mapper)
    {
        activeMappers.Add(mapper);
        // The activeTypeMappersCaches list runs in lock-step with activeMappers; a fresh empty map
        // is added for the new stack level. (Go reuses over-capacity slots; the C# List<T> handles
        // growth, so we simply append a new dictionary.)
        activeTypeMappersCaches.Add(new Dictionary<CacheHashKey, Type>(1));
    }

    // checker.go:22066-22074
    internal void PopActiveMapper()
    {
        activeMappers.RemoveAt(activeMappers.Count - 1);
        // Clear the map, but leave it out of the list (Go clears then truncates for later reuse; the
        // C# List drops the slot — the empty-dictionary reuse is a Go allocation optimization only).
        var lastIndex = activeTypeMappersCaches.Count - 1;
        activeTypeMappersCaches[lastIndex].Clear();
        activeTypeMappersCaches.RemoveAt(lastIndex);
    }

    // checker.go:22076-22078
    internal int FindActiveMapper(TypeMapper mapper) => activeMappers.FindLastIndex(m => m == mapper);

    // checker.go:22080-22084
    internal void ClearActiveMapperCaches()
    {
        foreach (var cache in activeTypeMappersCaches)
        {
            cache.Clear();
        }
    }

    // checker.go:22089-22104 — Return true if the given type could possibly reference a type
    // parameter for which we perform type inference. Cached for union/intersection types.
    internal bool CouldContainTypeVariablesWorker(Type t)
    {
        if ((t.flags & TypeFlags.StructuredOrInstantiable) == 0)
        {
            return false;
        }
        var objectFlags = t.objectFlags;
        if ((objectFlags & ObjectFlags.CouldContainTypeVariablesComputed) != 0)
        {
            return (objectFlags & ObjectFlags.CouldContainTypeVariables) != 0;
        }
        var result = (t.flags & TypeFlags.Instantiable) != 0 ||
            (t.flags & TypeFlags.Object) != 0 && !IsNonGenericTopLevelType(t) &&
                ((objectFlags & ObjectFlags.Reference) != 0 && (t.AsTypeReference()!.node is not null || CoreUtil.Some(GetTypeArguments(t), couldContainTypeVariables)) ||
                (objectFlags & ObjectFlags.Anonymous) != 0 && t.symbol is not null && (t.symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method | SymbolFlags.Class | SymbolFlags.TypeLiteral | SymbolFlags.ObjectLiteral)) != 0 && t.symbol.Declarations is not null ||
                (objectFlags & (ObjectFlags.Mapped | ObjectFlags.ReverseMapped | ObjectFlags.ObjectRestType | ObjectFlags.InstantiationExpressionType)) != 0) ||
            (t.flags & TypeFlags.UnionOrIntersection) != 0 && (t.flags & TypeFlags.EnumLiteral) == 0 && !IsNonGenericTopLevelType(t) && CoreUtil.Some(t.Types(), couldContainTypeVariables);
        t.objectFlags |= ObjectFlags.CouldContainTypeVariablesComputed | (result ? ObjectFlags.CouldContainTypeVariables : 0);
        return result;
    }

    // checker.go:22106-22123
    internal bool IsNonGenericTopLevelType(Type t)
    {
        if (t.alias is not null && LenOrZero(t.alias.typeArguments) == 0)
        {
            var declaration = AstUtilities.GetDeclarationOfKind(t.alias.symbol!,SyntaxKind.TypeAliasDeclaration);
            if (declaration is null)
            {
                declaration = AstUtilities.GetDeclarationOfKind(t.alias.symbol!,SyntaxKind.JSTypeAliasDeclaration);
            }
            return declaration is not null && AstUtilities.FindAncestorOrQuit(declaration.Parent, n =>
            {
                switch (n.Kind)
                {
                    case SyntaxKind.SourceFile:
                        return FindAncestorResult.True;
                    case SyntaxKind.ModuleDeclaration:
                        return FindAncestorResult.False;
                }
                return FindAncestorResult.Quit;
            }) is not null;
        }
        return false;
    }

    // checker.go:22125-22202
    internal Type InstantiateTypeWorker(Type t, TypeMapper m, TypeAlias? alias)
    {
        var flags = t.flags;
        if ((flags & TypeFlags.TypeParameter) != 0)
        {
            return m.Map(t);
        }
        if ((flags & TypeFlags.Object) != 0)
        {
            var objectFlags = t.objectFlags;
            if ((objectFlags & (ObjectFlags.Reference | ObjectFlags.Anonymous | ObjectFlags.Mapped)) != 0)
            {
                if ((objectFlags & ObjectFlags.Reference) != 0 && t.AsTypeReference()!.node is null)
                {
                    var resolvedTypeArguments = t.AsTypeReference()!.resolvedTypeArguments;
                    var newTypeArguments = InstantiateTypes(resolvedTypeArguments, m);
                    if (CoreUtil.Same(newTypeArguments, resolvedTypeArguments))
                    {
                        return t;
                    }
                    return CreateNormalizedTypeReference(t.Target(), newTypeArguments);
                }
                if ((objectFlags & ObjectFlags.ReverseMapped) != 0)
                {
                    return InstantiateReverseMappedType(t, m);
                }
                return GetObjectTypeInstantiation(t, m, alias);
            }
            return t;
        }
        if ((flags & TypeFlags.UnionOrIntersection) != 0)
        {
            var source = t;
            if ((t.flags & TypeFlags.Union) != 0)
            {
                var origin = t.AsUnionType().origin;
                if (origin is not null && (origin.flags & TypeFlags.UnionOrIntersection) != 0)
                {
                    source = origin;
                }
            }
            var types = source.Types();
            var newTypes = InstantiateTypes(types, m);
            if (CoreUtil.Same(newTypes, types) && alias.Symbol() == t.alias.Symbol())
            {
                return t;
            }
            if (alias is null)
            {
                alias = InstantiateTypeAlias(t.alias, m);
            }
            if ((source.flags & TypeFlags.Intersection) != 0)
            {
                return GetIntersectionTypeEx(newTypes, IntersectionFlags.None, alias);
            }
            return GetUnionTypeEx(newTypes, UnionReduction.Literal, alias, null /*origin*/);
        }
        if ((flags & TypeFlags.Index) != 0)
        {
            return GetIndexType(InstantiateType(t.Target(), m));
        }
        if ((flags & TypeFlags.IndexedAccess) != 0)
        {
            if (alias is null)
            {
                alias = InstantiateTypeAlias(t.alias, m);
            }
            var d = t.AsIndexedAccessType();
            return GetIndexedAccessTypeEx(InstantiateType(d.objectType, m), InstantiateType(d.indexType, m), d.accessFlags, null /*accessNode*/, alias);
        }
        if ((flags & TypeFlags.TemplateLiteral) != 0)
        {
            return GetTemplateLiteralType(t.AsTemplateLiteralType().texts, InstantiateTypes(t.AsTemplateLiteralType().types, m)!);
        }
        if ((flags & TypeFlags.StringMapping) != 0)
        {
            return GetStringMappingType(t.symbol!, InstantiateType(t.AsStringMappingType().target, m));
        }
        if ((flags & TypeFlags.Conditional) != 0)
        {
            return GetConditionalTypeInstantiation(t, CombineTypeMappers(t.AsConditionalType().mapper, m), false /*forConstraint*/, alias);
        }
        if ((flags & TypeFlags.Substitution) != 0)
        {
            var newBaseType = InstantiateType(t.AsSubstitutionType().baseType, m);
            if (IsNoInferType(t))
            {
                return GetNoInferType(newBaseType);
            }
            var newConstraint = InstantiateType(t.AsSubstitutionType().constraint, m);
            // A substitution type originates in the true branch of a conditional type and can be resolved
            // to just the base type in the same cases as the conditional type resolves to its true branch.
            if ((newBaseType.flags & TypeFlags.TypeVariable) != 0 && IsGenericType(newConstraint))
            {
                return GetSubstitutionType(newBaseType, newConstraint);
            }
            if ((newConstraint.flags & TypeFlags.AnyOrUnknown) != 0 || IsTypeAssignableTo(GetRestrictiveInstantiation(newBaseType), GetRestrictiveInstantiation(newConstraint)))
            {
                return newBaseType;
            }
            if ((newBaseType.flags & TypeFlags.TypeVariable) != 0)
            {
                return GetSubstitutionType(newBaseType, newConstraint);
            }
            return GetIntersectionType([newConstraint, newBaseType]);
        }
        return t;
    }

    // checker.go:22209-22307 — instantiation of AnonymousType / deferred TypeReference /
    // InstantiationExpressionType / MappedType.
    internal Type GetObjectTypeInstantiation(Type t, TypeMapper m, TypeAlias? alias)
    {
        Node? declaration;
        Type target;
        Type[]? typeParameters;
        if ((t.objectFlags & ObjectFlags.Reference) != 0) // Deferred type reference
        {
            declaration = t.AsTypeReference()!.node;
        }
        else if ((t.objectFlags & ObjectFlags.InstantiationExpressionType) != 0)
        {
            declaration = t.AsInstantiationExpressionType().node;
        }
        else
        {
            declaration = t.symbol!.Declarations![0];
        }
        var links = typeNodeLinks.Get(declaration!);
        if ((t.objectFlags & ObjectFlags.Reference) != 0) // Deferred type reference
        {
            target = links.resolvedType!;
        }
        else if ((t.objectFlags & ObjectFlags.Instantiated) != 0)
        {
            target = t.Target();
        }
        else
        {
            target = t;
        }
        typeParameters = links.outerTypeParameters;
        if (typeParameters is null)
        {
            // The first time an anonymous type is instantiated we compute and store a list of the type
            // parameters that are in scope (and therefore potentially referenced).
            typeParameters = GetOuterTypeParameters(declaration!, true /*includeThisTypes*/);
            if (LenOrZero(target.alias.TypeArguments()) == 0)
            {
                if ((t.objectFlags & (ObjectFlags.Reference | ObjectFlags.InstantiationExpressionType)) != 0)
                {
                    typeParameters = CoreUtil.Filter(typeParameters, tp => IsTypeParameterPossiblyReferenced(tp, declaration!));
                }
                else if ((target.symbol!.Flags & (SymbolFlags.Method | SymbolFlags.TypeLiteral)) != 0)
                {
                    typeParameters = CoreUtil.Filter(typeParameters, tp => CoreUtil.Some(t.symbol!.Declarations, d => IsTypeParameterPossiblyReferenced(tp, d)));
                }
            }
            if (typeParameters is null)
            {
                typeParameters = [];
            }
            links.outerTypeParameters = typeParameters;
        }
        if (typeParameters.Length == 0)
        {
            return t;
        }
        // Apply the mapper to the type parameters to produce the effective list of type arguments, and
        // compute the instantiation cache key from the type IDs of the type arguments.
        var combinedMapper = CombineTypeMappers(t.Mapper(), m)!;
        var typeArguments = new Type[typeParameters.Length];
        for (int i = 0; i < typeParameters.Length; i++)
        {
            typeArguments[i] = combinedMapper.Map(typeParameters[i]);
        }
        var newAlias = alias;
        if (newAlias is null)
        {
            newAlias = InstantiateTypeAlias(t.alias, m);
        }
        var data = target.AsObjectType()!;
        var key = GetTypeInstantiationKey(typeArguments, newAlias, (t.objectFlags & ObjectFlags.SingleSignatureType) != 0);
        if (data.instantiations is null)
        {
            data.instantiations = new Dictionary<CacheHashKey, Type>();
            data.instantiations[GetTypeInstantiationKey(typeParameters, target.alias, false)] = target;
        }
        var result = data.instantiations.GetValueOrDefault(key);
        if (result is null)
        {
            var newMapper = TypeMapper.NewTypeMapper(typeParameters, typeArguments);
            if ((target.objectFlags & ObjectFlags.SingleSignatureType) != 0 && m is not null)
            {
                newMapper = CombineTypeMappers(newMapper, m)!;
            }
            if ((target.objectFlags & ObjectFlags.Reference) != 0)
            {
                result = CreateDeferredTypeReference(t.Target(), t.AsTypeReference()!.node!, newMapper, newAlias);
            }
            else if ((target.objectFlags & ObjectFlags.Mapped) != 0)
            {
                result = InstantiateMappedType(target, newMapper, newAlias);
            }
            else
            {
                result = InstantiateAnonymousType(target, newMapper, newAlias);
            }
            data.instantiations[key] = result;
            if ((result.flags & TypeFlags.ObjectFlagsType) != 0 && (result.objectFlags & ObjectFlags.CouldContainTypeVariablesComputed) == 0)
            {
                // carry forward the type variable containment check from the input type arguments
                var resultCouldContainObjectFlags = CoreUtil.Some(typeArguments, couldContainTypeVariables);
                if ((result.objectFlags & ObjectFlags.CouldContainTypeVariablesComputed) == 0)
                {
                    if ((result.objectFlags & (ObjectFlags.Mapped | ObjectFlags.Anonymous | ObjectFlags.Reference)) != 0)
                    {
                        result.objectFlags |= ObjectFlags.CouldContainTypeVariablesComputed | (resultCouldContainObjectFlags ? ObjectFlags.CouldContainTypeVariables : 0);
                    }
                    else
                    {
                        result.objectFlags |= !resultCouldContainObjectFlags ? ObjectFlags.CouldContainTypeVariablesComputed : 0;
                    }
                }
            }
        }
        return result;
    }

    // checker.go:22364-22389
    internal Type InstantiateAnonymousType(Type t, TypeMapper? m, TypeAlias? alias)
    {
        var result = NewObjectType(t.objectFlags & ~(ObjectFlags.CouldContainTypeVariablesComputed | ObjectFlags.CouldContainTypeVariables) | ObjectFlags.Instantiated, t.symbol);
        if ((t.objectFlags & ObjectFlags.Mapped) != 0)
        {
            result.AsMappedType().declaration = t.AsMappedType().declaration;
            // C.f. instantiateSignature
            var origTypeParameter = GetTypeParameterFromMappedType(t);
            var freshTypeParameter = CloneTypeParameter(origTypeParameter);
            result.AsMappedType().typeParameter = freshTypeParameter;
            m = CombineTypeMappers(TypeMapper.NewSimpleTypeMapper(origTypeParameter, freshTypeParameter), m);
            freshTypeParameter.AsTypeParameter().mapper = m;
        }
        else if ((t.objectFlags & ObjectFlags.InstantiationExpressionType) != 0)
        {
            result.AsInstantiationExpressionType().node = t.AsInstantiationExpressionType().node;
        }
        if (alias is null)
        {
            alias = InstantiateTypeAlias(t.alias, m);
        }
        result.alias = alias;
        if (alias is not null && LenOrZero(alias.typeArguments) != 0)
        {
            result.objectFlags |= GetPropagatingFlagsOfTypes(result.alias!.typeArguments, TypeFlags.None);
        }
        var d = result.AsObjectType()!;
        d.target = t;
        d.mapper = m;
        return result;
    }

    // checker.go:22391-22422
    internal Type GetConditionalTypeInstantiation(Type t, TypeMapper? mapper, bool forConstraint, TypeAlias? alias)
    {
        var root = t.AsConditionalType().root;
        if (LenOrZero(root.outerTypeParameters) != 0)
        {
            // Apply the mapper to the type parameters to produce the effective list of type arguments, and
            // compute the instantiation cache key from the type IDs of the type arguments.
            var typeArguments = CoreUtil.Map(root.outerTypeParameters, mapper!.Map)!;
            var key = GetConditionalTypeKey(typeArguments, alias, forConstraint);
            root.instantiations ??= new Dictionary<CacheHashKey, Type>();
            var result = root.instantiations.GetValueOrDefault(key);
            if (result is null)
            {
                var newMapper = TypeMapper.NewTypeMapper(root.outerTypeParameters!, typeArguments);
                var checkType = root.checkType!;
                Type? distributionType = null;
                if (root.isDistributive)
                {
                    distributionType = GetReducedType(newMapper.Map(checkType));
                }
                // Distributive conditional types are distributed over union types.
                if (distributionType is not null && checkType != distributionType && (distributionType.flags & (TypeFlags.Union | TypeFlags.Never)) != 0)
                {
                    result = MapTypeWithAlias(distributionType, s => GetConditionalType(root, TypeMapper.PrependTypeMapping(checkType, s, newMapper), forConstraint, null), alias);
                }
                else
                {
                    result = GetConditionalType(root, newMapper, forConstraint, alias);
                }
                root.instantiations[key] = result;
            }
            return result;
        }
        return t;
    }

    // checker.go:22424-22428
    internal Type CloneTypeParameter(Type tp)
    {
        var result = NewTypeParameter(tp.symbol);
        result.AsTypeParameter().target = tp;
        return result;
    }

    // checker.go:22430-22439
    internal Type? GetHomomorphicTypeVariable(Type t)
    {
        var constraintType = GetConstraintTypeFromMappedType(t);
        if ((constraintType.flags & TypeFlags.Index) != 0)
        {
            var typeVariable = GetActualTypeVariable(constraintType.AsIndexType().Target());
            if ((typeVariable.flags & TypeFlags.TypeParameter) != 0)
            {
                return typeVariable;
            }
        }
        return null;
    }

    // checker.go:22576-22582
    internal Type GetTypeParameterFromMappedType(Type t)
    {
        var m = t.AsMappedType();
        if (m.typeParameter is null)
        {
            m.typeParameter = GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(m.declaration!.TypeParameter!)!);
        }
        return m.typeParameter;
    }

    // checker.go:22584-22590
    internal Type GetConstraintTypeFromMappedType(Type t)
    {
        var m = t.AsMappedType();
        if (m.constraintType is null)
        {
            m.constraintType = GetConstraintOfTypeParameter(GetTypeParameterFromMappedType(t)) ?? errorType!;
        }
        return m.constraintType;
    }

    // checker.go:22592-22601
    internal Type? GetNameTypeFromMappedType(Type t)
    {
        var m = t.AsMappedType();
        if (m.declaration!.NameType is null)
        {
            return null;
        }
        if (m.nameType is null)
        {
            m.nameType = InstantiateType(GetTypeFromTypeNode(m.declaration.NameType!), m.mapper);
        }
        return m.nameType;
    }

    // checker.go:22603-22613
    internal Type GetTemplateTypeFromMappedType(Type t)
    {
        var m = t.AsMappedType();
        if (m.templateType is null)
        {
            if (m.declaration!.Type is not null)
            {
                m.templateType = InstantiateType(AddOptionalityEx(GetTypeFromTypeNode(m.declaration.Type), true /*isProperty*/, (GetMappedTypeModifiers(t) & MappedTypeModifiers.IncludeOptional) != 0), m.mapper);
            }
            else
            {
                m.templateType = errorType;
            }
        }
        return m.templateType!;
    }

    // checker.go:22615-22618
    internal bool IsMappedTypeWithKeyofConstraintDeclaration(Type t)
    {
        var constraintDeclaration = GetConstraintDeclarationForMappedType(t);
        return constraintDeclaration is not null && NodeTests.IsTypeOperatorNode(constraintDeclaration) && constraintDeclaration.AsTypeOperatorNode().Operator == SyntaxKind.KeyOfKeyword;
    }

    // checker.go:22620-22622
    internal Node? GetConstraintDeclarationForMappedType(Type t) =>
        t.AsMappedType().declaration!.TypeParameter!.AsTypeParameterDeclaration().Constraint;

    // checker.go:22633-22646
    internal void ForEachMappedTypePropertyKeyTypeAndIndexSignatureKeyType(Type t, TypeFlags include, bool stringsOnly, Action<Type> cb)
    {
        foreach (var prop in GetPropertiesOfType(t))
        {
            cb(GetLiteralTypeFromProperty(prop, include, false));
        }
        if ((t.flags & TypeFlags.Any) != 0)
        {
            cb(stringType!);
        }
        else
        {
            foreach (var info in GetIndexInfosOfType(t))
            {
                if (!stringsOnly || (info.keyType.flags & (TypeFlags.String | TypeFlags.TemplateLiteral)) != 0)
                {
                    cb(info.keyType);
                }
            }
        }
    }

    // checker.go:22666-22671
    internal TypeAlias? InstantiateTypeAlias(TypeAlias? alias, TypeMapper? m)
    {
        if (alias is null)
        {
            return null;
        }
        return new TypeAlias { symbol = alias.symbol, typeArguments = InstantiateTypes(alias.typeArguments, m) };
    }

    // checker.go:22673-22675
    internal Type[]? InstantiateTypes(Type[]? types, TypeMapper? m) =>
        InstantiateList(this, types, m, static (c, v, mm) => c.InstantiateType(v, mm));

    // checker.go:22677-22679
    internal Symbol[]? InstantiateSymbols(Symbol[]? symbols, TypeMapper? m) =>
        InstantiateList(this, symbols, m, static (c, v, mm) => c.InstantiateSymbol(v, mm));

    // checker.go:22681-22683
    internal Signature[]? InstantiateSignatures(Signature[]? signatures, TypeMapper m) =>
        InstantiateList(this, signatures, m, static (c, v, mm) => c.InstantiateSignature(v, mm!));

    // checker.go:22685-22687
    internal IndexInfo[]? InstantiateIndexInfos(IndexInfo[]? indexInfos, TypeMapper m) =>
        InstantiateList(this, indexInfos, m, static (c, v, mm) => c.InstantiateIndexInfo(v, mm!));

    // checker.go:22705-22711
    internal Type? TryGetTypeFromTypeNode(Node node)
    {
        var typeNode = node.Type();
        if (typeNode is not null)
        {
            return GetTypeFromTypeNode(typeNode);
        }
        return null;
    }

    // checker.go:21566-21574
    internal Symbol? GetTargetSymbol(Symbol? s)
    {
        // if symbol is instantiated its flags are not copied from the 'target'
        // so we'll need to get back original 'target' symbol to work with correct set of flags
        if (s is not null && (s.CheckFlags & CheckFlags.Instantiated) != 0)
        {
            return valueSymbolLinks.Get(s).target;
        }
        return s;
    }

    // checker.go:21580
    internal static bool IsPrototypeProperty(Symbol symbol) =>
        (symbol.Flags & SymbolFlags.Method) != 0 || (symbol.CheckFlags & CheckFlags.SyntheticMethod) != 0;

    // checker.go:21608-21618
    internal Symbol CreateSymbolWithType(Symbol source, Type? t)
    {
        var symbol = NewSymbolEx(source.Flags, source.Name, source.CheckFlags & CheckFlags.Readonly);
        symbol.Declarations = source.Declarations;
        symbol.Parent = source.Parent;
        symbol.ValueDeclaration = source.ValueDeclaration;
        var links = valueSymbolLinks.Get(symbol);
        links.resolvedType = t;
        links.target = source;
        links.nameType = valueSymbolLinks.Get(source).nameType;
        return symbol;
    }

    // checker.go:21620-21627
    internal bool IsMappedTypeGenericIndexedAccess(Type t)
    {
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            var objectType = t.AsIndexedAccessType().objectType;
            return (objectType.objectFlags & ObjectFlags.Mapped) != 0 && !IsGenericMappedType(objectType) && IsGenericIndexType(t.AsIndexedAccessType().indexType) &&
                (GetMappedTypeModifiers(objectType) & MappedTypeModifiers.ExcludeOptional) == 0 && objectType.AsMappedType().declaration!.NameType is null;
        }
        return false;
    }

    // checker.go:21270
    internal (bool[], int) FindMixins(Type[] types)
    {
        var mixinFlags = CoreUtil.Map(types, IsMixinConstructorType)!;
        int constructorTypeCount = 0, mixinCount = 0;
        var firstMixinIndex = -1;
        for (var i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (GetSignaturesOfType(t, SignatureKind.Construct).Length > 0)
            {
                constructorTypeCount++;
            }
            if (mixinFlags[i])
            {
                if (firstMixinIndex < 0)
                {
                    firstMixinIndex = i;
                }
                mixinCount++;
            }
        }
        if (constructorTypeCount > 0 && constructorTypeCount == mixinCount)
        {
            mixinFlags[firstMixinIndex] = false;
            mixinCount--;
        }
        return (mixinFlags, mixinCount);
    }

    // checker.go:21308-21317 — property by name on an object type, or null.
    internal Symbol? GetPropertyOfObjectType(Type t, string name)
    {
        if ((t.flags & TypeFlags.Object) != 0)
        {
            var resolved = ResolveStructuredTypeMembers(t);
            var symbol = resolved.members?.GetValueOrDefault(name);
            if (symbol is not null && SymbolIsValue(symbol))
            {
                return symbol;
            }
        }
        return null;
    }

    // ===================================================================================================
    // DEFERRED (#14 line-range, breadth-first — not yet implemented). These stubs live in this file per
    // the "未完成留桩在你文件" rule. The mapped-type instantiation subtree (instantiateMappedType /
    // instantiateReverseMappedType and their satellites) and isTypeParameterPossiblyReferenced (blocked
    // on ast.GetFirstIdentifier not yet ported) are the reachable stubs from the implemented core.
    // ===================================================================================================

    // checker.go:22441-22484
    internal Type InstantiateMappedType(Type t, TypeMapper? m, TypeAlias? alias) => throw new NotImplementedException("instantiateMappedType");

    // checker.go:22648-22664
    internal Type InstantiateReverseMappedType(Type t, TypeMapper m) => throw new NotImplementedException("instantiateReverseMappedType");

    // checker.go:22309
    internal bool IsTypeParameterPossiblyReferenced(Type tp, Node node)
    {
        bool ContainsReference(Node node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.ThisType:
                    return tp.AsTypeParameter().isThisType;
                case SyntaxKind.TypeReference:
                    // use worker because we're looking for === equality
                    if (!tp.AsTypeParameter().isThisType && node.TypeArguments().Len() == 0 && GetSymbolFromTypeReference(node) == tp.symbol)
                    {
                        return true;
                    }
                    break;
                case SyntaxKind.TypeQuery:
                    var entityName = node.AsTypeQueryNode().ExprName;
                    var firstIdentifier = GetFirstIdentifier(entityName!);
                    if (!JSDocReparseAstGaps.IsThisIdentifier(firstIdentifier))
                    {
                        var firstIdentifierSymbol = GetResolvedSymbol(firstIdentifier);
                        var tpDeclaration = tp.symbol!.Declarations![0]; // There is exactly one declaration, otherwise `containsReference` is not called
                        Node? tpScope = null;
                        if (NodeTests.IsTypeParameterDeclaration(tpDeclaration))
                        {
                            tpScope = tpDeclaration.Parent; // Type parameter is a regular type parameter, e.g. foo<T>
                        }
                        else if (tp.AsTypeParameter().isThisType)
                        {
                            tpScope = tpDeclaration; // Type parameter is the this type, and its declaration is the class declaration.
                        }
                        if (tpScope != null)
                        {
                            return CoreUtil.Some(firstIdentifierSymbol.Declarations, d => AstUtilities.IsNodeDescendantOf(d, tpScope)) ||
                                CoreUtil.Some(node.TypeArguments(), ContainsReference);
                        }
                    }
                    return true;
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.MethodSignature:
                    var returnType = node.Type();
                    return (returnType == null && node.Body() != null) ||
                        CoreUtil.Some(node.TypeParameters(), ContainsReference) ||
                        CoreUtil.Some(node.Parameters(), ContainsReference) ||
                        (returnType != null && ContainsReference(returnType));
            }
            return node.ForEachChild(ContainsReference);
        }
        // If the type parameter doesn't have exactly one declaration, if there are intervening statement blocks
        // between the node and the type parameter declaration, if the node contains actual references to the
        // type parameter, or if the node contains type queries that we can't prove couldn't contain references to the type parameter,
        // we consider the type parameter possibly referenced.
        if (tp.symbol != null && tp.symbol.Declarations.Len() == 1)
        {
            var container = tp.symbol.Declarations![0].Parent;
            for (var n = node; n != container; n = n!.Parent)
            {
                if (n == null || NodeTests.IsBlock(n) || (NodeTests.IsConditionalTypeNode(n) && ContainsReference(n.AsConditionalTypeNode().ExtendsType!)))
                {
                    return true;
                }
            }
            return ContainsReference(node);
        }
        return true;
    }

    // checker.go:20926-20955 — deferred: needs getKnownKeysOfTupleType / mapTypeEx (not yet ported).
    // Referenced by #18 (getIndexType/keyof); stub keeps the dependent cluster compiling.
    internal Type GetLowerBoundOfKeyType(Type t) => throw new NotImplementedException("getLowerBoundOfKeyType");
}
