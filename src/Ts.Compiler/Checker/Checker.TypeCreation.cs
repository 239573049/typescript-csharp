// Ported from tsgo internal/checker/checker.go (section: newType..literal interning, lines
// 24911-25246). plan/03 §4.2 #16. These primitives are REAL (the constructor's field
// initialization depends on them); the deeper members of the TypeCreation section remain stubs.
//
// Arenas deleted (plan §一.5): newType no longer takes a `data` arg (the concrete object IS the
// header); newSignature/newIndexInfo just `new`.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using StrUtil = global::Ts.StringUtil.StringUtil;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:24911-24923
    internal Type NewType(TypeFlags flags, ObjectFlags objectFlags, Type t)
    {
        TypeCount++;
        t.flags = flags;
        t.objectFlags = objectFlags & ~(ObjectFlags.CouldContainTypeVariablesComputed | ObjectFlags.CouldContainTypeVariables | ObjectFlags.MembersResolved);
        t.id = new TypeId(TypeCount);
        t.checker = this;
        if (tracer is not null)
        {
            tracer.RecordType(t);
        }
        return t;
    }

    // checker.go:24925-24927
    internal Type NewIntrinsicType(TypeFlags flags, string intrinsicName) =>
        NewIntrinsicTypeEx(flags, intrinsicName, ObjectFlags.None);

    // checker.go:24929-24933
    internal Type NewIntrinsicTypeEx(TypeFlags flags, string intrinsicName, ObjectFlags objectFlags)
    {
        var data = new IntrinsicType { intrinsicName = intrinsicName };
        return NewType(flags, objectFlags, data);
    }

    // checker.go:24935-24942
    internal Type CreateWideningType(Type nonWideningType)
    {
        if (strictNullChecks)
        {
            return nonWideningType;
        }
        var t = NewIntrinsicType(nonWideningType.flags, nonWideningType.AsIntrinsicType().intrinsicName);
        t.objectFlags |= ObjectFlags.ContainsWideningType;
        return t;
    }

    // checker.go:24944-24949
    internal Type CreateUnknownUnionType()
    {
        if (strictNullChecks)
        {
            return GetUnionType([undefinedType!, nullType!, unknownEmptyObjectType!]);
        }
        return unknownType!;
    }

    // checker.go:24951-24961
    internal Type NewLiteralType(TypeFlags flags, object? value, Type? regularType)
    {
        var data = new LiteralType { value = value };
        var t = NewType(flags, ObjectFlags.None, data);
        data.regularType = regularType ?? t;
        return t;
    }

    // checker.go:24963-24969
    internal Type NewUniqueESSymbolType(Symbol? symbol, string name)
    {
        var data = new UniqueESSymbolType { name = name };
        var t = NewType(TypeFlags.UniqueESSymbol, ObjectFlags.None, data);
        t.symbol = symbol;
        return t;
    }

    // checker.go:24971-24996 — ObjectFlags priority chain (plan/03 §3); order is load-bearing.
    internal Type NewObjectType(ObjectFlags objectFlags, Symbol? symbol)
    {
        Type data =
            (objectFlags & ObjectFlags.ClassOrInterface) != 0 ? new InterfaceType() :
            (objectFlags & ObjectFlags.Tuple) != 0 ? new TupleType() :
            (objectFlags & ObjectFlags.Reference) != 0 ? new TypeReference() :
            (objectFlags & ObjectFlags.Mapped) != 0 ? new MappedType() :
            (objectFlags & ObjectFlags.ReverseMapped) != 0 ? new ReverseMappedType() :
            (objectFlags & ObjectFlags.EvolvingArray) != 0 ? new EvolvingArrayType() :
            (objectFlags & ObjectFlags.InstantiationExpressionType) != 0 ? new InstantiationExpressionType() :
            (objectFlags & ObjectFlags.Anonymous) != 0 ? new ObjectType() :
            throw new InvalidOperationException("Unhandled case in newObjectType");
        var t = NewType(TypeFlags.Object, objectFlags, data);
        t.symbol = symbol;
        return t;
    }

    // checker.go:24998-25002
    internal Type NewAnonymousType(Symbol? symbol, SymbolTable? members, Signature[]? callSignatures, Signature[]? constructSignatures, IndexInfo[]? indexInfos)
    {
        var t = NewObjectType(ObjectFlags.Anonymous, symbol);
        SetStructuredTypeMembers(t, members, callSignatures, constructSignatures, indexInfos);
        return t;
    }

    // checker.go:25053-25074
    internal void SetStructuredTypeMembers(Type t, SymbolTable? members, Signature[]? callSignatures, Signature[]? constructSignatures, IndexInfo[]? indexInfos)
    {
        t.objectFlags |= ObjectFlags.MembersResolved;
        var data = t.AsStructuredType()!;
        data.members = members;
        data.properties = GetNamedMembers(members, t.symbol);
        if (callSignatures is { Length: > 0 })
        {
            data.signatures = constructSignatures is { Length: > 0 } ? [.. callSignatures, .. constructSignatures] : callSignatures;
            data.callSignatureCount = callSignatures.Length;
        }
        else
        {
            data.signatures = constructSignatures is { Length: > 0 } ? constructSignatures : null;
            data.callSignatureCount = 0;
        }
        data.indexInfos = indexInfos;
    }

    // checker.go:21954-21979
    internal Symbol[] GetNamedMembers(SymbolTable? members, Symbol? container)
    {
        if (members is null || members.Count == 0)
        {
            return [];
        }
        // For classes and interfaces, we store explicitly declared members ahead of inherited members. This ensures we process
        // explicitly declared members first in type relations, which is beneficial because explicitly declared members are more
        // likely to contain discriminating differences. See for example https://github.com/microsoft/typescript-go/issues/1968.
        List<Symbol> result = new(members.Count);
        int containedCount = 0;
        if (container != null && (container.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
        {
            // map-iter: 序无关 —— result is sorted below via sortSymbols(result[:containedCount]).
            foreach (var (id, symbol) in members)
            {
                if (IsNamedMember(symbol, id) && IsDeclarationContainedBy(symbol, container))
                {
                    result.Add(symbol);
                }
            }
            containedCount = result.Count;
        }
        // map-iter: 序无关 —— result is sorted below via sortSymbols(result[containedCount:]).
        foreach (var (id, symbol) in members)
        {
            if (IsNamedMember(symbol, id) && (container == null || (container.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) == 0 || !IsDeclarationContainedBy(symbol, container)))
            {
                result.Add(symbol);
            }
        }
        SortSymbols(result, 0, containedCount);
        SortSymbols(result, containedCount, result.Count);
        return result.ToArray();
    }

    // checker.go:21981-21990
    internal bool IsDeclarationContainedBy(Symbol symbol, Symbol container)
    {
        var declaration = symbol.ValueDeclaration;
        if (declaration != null)
        {
            foreach (var d in container.Declarations ?? [])
            {
                if (declaration.Loc.ContainedBy(d.Loc))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // checker.go:21992-21994
    internal bool IsNamedMember(Symbol symbol, string id) => !IsReservedMemberName(id) && SymbolIsValue(symbol);

    // utilities.go:1599-1601
    internal static bool IsReservedMemberName(string name) =>
        name.Length >= 2 && name[0] == '\xFE' && name[1] != '@' && name[1] != '#';

    // utilities.go:339-341 — sortSymbols over the [start, end) subrange (slices.SortFunc is non-stable).
    internal void SortSymbols(List<Symbol> symbols, int start, int end)
    {
        if (end - start > 1)
        {
            symbols.Sort(start, end - start, Comparer<Symbol>.Create((a, b) => compareSymbols(a, b)));
        }
    }

    // checker.go:25076-25080
    internal Type NewTypeParameter(Symbol? symbol)
    {
        var t = NewType(TypeFlags.TypeParameter, ObjectFlags.None, new TypeParameter());
        t.symbol = symbol;
        return t;
    }

    // checker.go:25086-25094
    internal ObjectFlags GetPropagatingFlagsOfTypes(Type[]? types, TypeFlags excludeKinds)
    {
        var result = ObjectFlags.None;
        foreach (var t in types ?? [])
        {
            if ((t.flags & excludeKinds) == 0)
            {
                result |= t.objectFlags;
            }
        }
        return result & ObjectFlags.PropagatingFlags;
    }

    // checker.go:25096-25100
    internal Type NewUnionType(ObjectFlags objectFlags, Type[] types)
    {
        var data = new UnionType { types = types };
        return NewType(TypeFlags.Union, objectFlags, data);
    }

    // checker.go:25102-25106
    internal Type NewIntersectionType(ObjectFlags objectFlags, Type[] types)
    {
        var data = new IntersectionType { types = types };
        return NewType(TypeFlags.Intersection, objectFlags, data);
    }

    // checker.go:25108-25114
    internal Type NewIndexedAccessType(Type objectType, Type indexType, AccessFlags accessFlags)
    {
        var data = new IndexedAccessType { objectType = objectType, indexType = indexType, accessFlags = accessFlags };
        return NewType(TypeFlags.IndexedAccess, ObjectFlags.None, data);
    }

    // checker.go:25116-25121
    internal Type NewIndexType(Type target, IndexFlags indexFlags)
    {
        var data = new IndexType { target = target, indexFlags = indexFlags };
        return NewType(TypeFlags.Index, ObjectFlags.None, data);
    }

    // checker.go:25123-25128
    internal Type NewTemplateLiteralType(string[] texts, Type[] types)
    {
        var data = new TemplateLiteralType { texts = texts, types = types };
        return NewType(TypeFlags.TemplateLiteral, ObjectFlags.None, data);
    }

    // checker.go:25130-25136
    internal Type NewStringMappingType(Symbol? symbol, Type target)
    {
        var data = new StringMappingType { target = target };
        var t = NewType(TypeFlags.StringMapping, ObjectFlags.None, data);
        t.symbol = symbol;
        return t;
    }

    // checker.go:25148-25153
    internal Type NewSubstitutionType(Type baseType, Type constraint)
    {
        var data = new SubstitutionType { baseType = baseType, constraint = constraint };
        return NewType(TypeFlags.Substitution, ObjectFlags.None, data);
    }

    // checker.go:25155-25169 (arena → new)
    internal Signature NewSignature(SignatureFlags flags, Node? declaration, Type[]? typeParameters, Symbol? thisParameter, Symbol[]? parameters, Type? resolvedReturnType, TypePredicate? resolvedTypePredicate, int minArgumentCount)
    {
        SignatureCount++;
        var sig = new Signature
        {
            id = new SignatureId(SignatureCount),
            flags = flags,
            declaration = declaration,
            typeParameters = typeParameters,
            parameters = parameters,
            thisParameter = thisParameter,
            resolvedReturnType = resolvedReturnType,
            resolvedTypePredicate = resolvedTypePredicate,
            minArgumentCount = minArgumentCount,
            resolvedMinArgumentCount = -1,
        };
        return sig;
    }

    // checker.go:25171-25179 (arena → new)
    internal IndexInfo NewIndexInfo(Type keyType, Type valueType, bool isReadonly, Node? declaration, Node[]? components) =>
        new()
        {
            keyType = keyType,
            valueType = valueType,
            isReadonly = isReadonly,
            declaration = declaration,
            components = components,
        };

    // checker.go:25181-25193
    internal Type GetRegularTypeOfLiteralType(Type t)
    {
        if ((t.flags & TypeFlags.Freshable) != 0)
        {
            return t.AsLiteralType().regularType!;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            var u = t.AsUnionType();
            u.regularType ??= MapType(t, GetRegularTypeOfLiteralType);
            return u.regularType;
        }
        return t;
    }

    // checker.go:25195-25207
    internal Type GetFreshTypeOfLiteralType(Type t)
    {
        if ((t.flags & TypeFlags.Freshable) != 0)
        {
            var d = t.AsLiteralType();
            if (d.freshType is null)
            {
                var f = NewLiteralType(t.flags, d.value, t);
                f.symbol = t.symbol;
                f.AsLiteralType().freshType = f;
                d.freshType = f;
            }
            return d.freshType;
        }
        return t;
    }

    // checker.go:25209-25211
    internal static bool IsFreshLiteralType(Type t) =>
        (t.flags & TypeFlags.Freshable) != 0 && t.AsLiteralType().freshType == t;

    // checker.go:25213-25220
    internal Type GetStringLiteralType(string value)
    {
        var t = stringLiteralTypes.GetValueOrDefault(value);
        if (t is null)
        {
            t = NewLiteralType(TypeFlags.StringLiteral, value, null);
            stringLiteralTypes[value] = t;
        }
        return t;
    }

    // checker.go:25222-25237 — NaN never enters the map (plan/03 §6.4); own field.
    internal Type GetNumberLiteralType(Ts.JsNum.Number value)
    {
        if (double.IsNaN(value.Value))
        {
            nanType ??= NewLiteralType(TypeFlags.NumberLiteral, value, null);
            return nanType;
        }
        var t = numberLiteralTypes.GetValueOrDefault(value);
        if (t is null)
        {
            t = NewLiteralType(TypeFlags.NumberLiteral, value, null);
            numberLiteralTypes[value] = t;
        }
        return t;
    }

    // checker.go:25239-25246
    internal Type GetBigIntLiteralType(Ts.JsNum.PseudoBigInt value)
    {
        var t = bigintLiteralTypes.GetValueOrDefault(value);
        if (t is null)
        {
            t = NewLiteralType(TypeFlags.BigIntLiteral, value, null);
            bigintLiteralTypes[value] = t;
        }
        return t;
    }

    // checker.go:25270
    internal Type GetEnumLiteralType(object? value, Symbol enumSymbol, Symbol symbol)
    {
        TypeFlags flags;
        switch (value)
        {
            case string:
                flags = TypeFlags.EnumLiteral | TypeFlags.StringLiteral;
                break;
            case Ts.JsNum.Number v:
                flags = TypeFlags.EnumLiteral | TypeFlags.NumberLiteral;
                // NaN cannot be used as a Go map key because NaN != NaN in IEEE 754,
                // so Go map lookups for NaN always miss. Cache NaN enum types separately by enum symbol.
                if (double.IsNaN(v.Value))
                {
                    var nan = enumNaNLiteralTypes.GetValueOrDefault(enumSymbol);
                    if (nan is null)
                    {
                        nan = NewLiteralType(flags, value, null);
                        nan.symbol = symbol;
                        enumNaNLiteralTypes[enumSymbol] = nan;
                    }
                    return nan;
                }
                break;
            default:
                throw new InvalidOperationException("Unhandled case in getEnumLiteralType");
        }
        var key = new EnumLiteralKey(enumSymbol, value);
        var t = enumLiteralTypes.GetValueOrDefault(key);
        if (t is null)
        {
            t = NewLiteralType(flags, value, null);
            t.symbol = symbol;
            enumLiteralTypes[key] = t;
        }
        return t;
    }

    // getUnionType / getUnionTypeWorker (real reduction) now live in Checker.UnionIntersection.cs (#17).

    // checker.go:28902-28968 — real body (lib-gate wave P); previously a no-normalization stand-in.
    internal Type GetTemplateLiteralType(string[] texts, Type[] types)
    {
        var unionIndex = CoreUtil.FindIndex(types, t => (t.flags & (TypeFlags.Never | TypeFlags.Union)) != 0);
        if (unionIndex >= 0)
        {
            if (!CheckCrossProductUnion(types))
            {
                return errorType!;
            }
            return MapType(types[unionIndex], t => GetTemplateLiteralType(texts, CoreUtil.ReplaceElement(types, unionIndex, t)));
        }
        if (System.Array.IndexOf(types, wildcardType) >= 0)
        {
            return wildcardType!;
        }
        var newTypes = new List<Type>();
        var newTexts = new List<string>();
        var sb = new System.Text.StringBuilder();
        sb.Append(texts[0]);
        Func<string[], Type[], bool> addSpans = null!;
        addSpans = (spanTexts, spanTypes) =>
        {
            for (var i = 0; i < spanTypes.Length; i++)
            {
                var t = spanTypes[i];
                if ((t.flags & (TypeFlags.Literal | TypeFlags.Null | TypeFlags.Undefined)) != 0)
                {
                    sb.Append(GetTemplateStringForType(t));
                    sb.Append(spanTexts[i + 1]);
                }
                else if ((t.flags & TypeFlags.TemplateLiteral) != 0)
                {
                    sb.Append(t.AsTemplateLiteralType().texts[0]);
                    if (!addSpans(t.AsTemplateLiteralType().texts, t.AsTemplateLiteralType().types))
                    {
                        return false;
                    }
                    sb.Append(spanTexts[i + 1]);
                }
                else if (IsGenericIndexType(t) || IsPatternLiteralPlaceholderType(t))
                {
                    newTypes.Add(t);
                    newTexts.Add(StrUtil.CombineSurrogatePairs(sb.ToString()));
                    sb.Clear();
                    sb.Append(spanTexts[i + 1]);
                }
                else
                {
                    return false;
                }
            }
            return true;
        };
        if (!addSpans(texts, types))
        {
            return stringType!;
        }
        if (newTypes.Count == 0)
        {
            return GetStringLiteralType(StrUtil.CombineSurrogatePairs(sb.ToString()));
        }
        newTexts.Add(StrUtil.CombineSurrogatePairs(sb.ToString()));
        var newTextsArray = newTexts.ToArray();
        var newTypesArray = newTypes.ToArray();
        if (CoreUtil.Every(newTextsArray, t => t == ""))
        {
            if (CoreUtil.Every(newTypesArray, t => (t.flags & TypeFlags.String) != 0))
            {
                return stringType!;
            }
            // Normalize `${Mapping<xxx>}` into Mapping<xxx>
            if (newTypesArray.Length == 1 && IsPatternLiteralType(newTypesArray[0]))
            {
                return newTypesArray[0];
            }
        }
        var key = GetTemplateTypeKey(newTextsArray, newTypesArray);
        var t2 = templateLiteralTypes.GetValueOrDefault(key);
        if (t2 is null)
        {
            t2 = NewTemplateLiteralType(newTextsArray, newTypesArray);
            templateLiteralTypes[key] = t2;
        }
        return t2;
    }

    // checker.go:28970-28978
    internal string GetTemplateStringForType(Type t)
    {
        if ((t.flags & (TypeFlags.StringLiteral | TypeFlags.NumberLiteral | TypeFlags.BooleanLiteral | TypeFlags.BigIntLiteral)) != 0)
        {
            return global::Ts.Evaluator.EvaluatorFactory.AnyToString(t.AsLiteralType().value);
        }
        if ((t.flags & TypeFlags.Nullable) != 0)
        {
            return t.AsIntrinsicType().intrinsicName;
        }
        return "";
    }
}
