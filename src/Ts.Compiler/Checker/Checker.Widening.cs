// Ported from tsgo internal/checker/checker.go — the type-widening family
// (getWidenedLiteralType / getWidenedUniqueESSymbolType / getWidenedLiteralLikeTypeForContextualType /
//  isLiteralOfContextualType / getRegularTypeOfObjectLiteral / getWidenedLiteralTypeForInitializer /
//  transformTypeOfMembers / getBaseTypeOfEnumLikeType), plus the co-located mapType engine
// (mapType / mapTypeEx / mapTypeWithAlias) that literal widening distributes through.
//
// The getWidenedType / getWidenedTypeWithContext / getWidenedTypeOfObjectLiteral / reportImplicitAny /
// reportErrorsFromWidening / reportWideningErrorsInType core already live in Checker.Members.cs /
// Checker.Signatures.cs (foundation wave); this file completes the literal-widening leaves they call.
//
// §1.5 cross-package hosts: diagnostics referenced via global::Ts.Diagnostics.*; no sibling-namespace
// collisions in this file (all symbols are Checker members / Ts.Ast host types used unqualified).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:16810-16815
    internal Type GetWidenedLiteralTypeForInitializer(Node declaration, Type t)
    {
        if ((GetCombinedNodeFlagsCached(declaration) & NodeFlags.Constant) != 0 || IsDeclarationReadonly(declaration))
        {
            return t;
        }
        return GetWidenedLiteralType(t);
    }

    // checker.go:25378-25383
    internal Type GetBaseTypeOfEnumLikeType(Type t)
    {
        if ((t.flags & TypeFlags.EnumLike) != 0 && (t.symbol!.Flags & SymbolFlags.EnumMember) != 0)
        {
            return GetDeclaredTypeOfSymbol(GetParentOfSymbol(t.symbol)!);
        }
        return t;
    }

    // checker.go:25395-25411
    internal Type GetWidenedLiteralType(Type t)
    {
        if ((t.flags & TypeFlags.EnumLike) != 0 && IsFreshLiteralType(t))
        {
            return GetBaseTypeOfEnumLikeType(t);
        }
        if ((t.flags & TypeFlags.StringLiteral) != 0 && IsFreshLiteralType(t))
        {
            return stringType!;
        }
        if ((t.flags & TypeFlags.NumberLiteral) != 0 && IsFreshLiteralType(t))
        {
            return numberType!;
        }
        if ((t.flags & TypeFlags.BigIntLiteral) != 0 && IsFreshLiteralType(t))
        {
            return bigintType!;
        }
        if ((t.flags & TypeFlags.BooleanLiteral) != 0 && IsFreshLiteralType(t))
        {
            return booleanType!;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return MapType(t, GetWidenedLiteralType);
        }
        return t;
    }

    // checker.go:25413-25421
    internal Type GetWidenedUniqueESSymbolType(Type t)
    {
        if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
        {
            return esSymbolType!;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return MapType(t, GetWidenedUniqueESSymbolType);
        }
        return t;
    }

    // checker.go:25423-25428
    internal Type GetWidenedLiteralLikeTypeForContextualType(Type t, Type? contextualType)
    {
        if (!IsLiteralOfContextualType(t, contextualType))
        {
            t = GetWidenedUniqueESSymbolType(GetWidenedLiteralType(t));
        }
        return GetRegularTypeOfLiteralType(t);
    }

    // checker.go:25430-25460
    internal bool IsLiteralOfContextualType(Type candidateType, Type? contextualType)
    {
        if (contextualType is not null)
        {
            if ((contextualType.flags & TypeFlags.UnionOrIntersection) != 0)
            {
                return CoreUtil.Some(contextualType.Types(), t => IsLiteralOfContextualType(candidateType, t));
            }
            if ((contextualType.flags & TypeFlags.InstantiableNonPrimitive) != 0)
            {
                // If the contextual type is a type variable constrained to a primitive type, consider
                // this a literal context for literals of that primitive type. For example, given a
                // type parameter 'T extends string', infer string literal types for T.
                var constraint = GetBaseConstraintOfType(contextualType);
                constraint ??= unknownType!;
                return (MaybeTypeOfKind(constraint, TypeFlags.String) && MaybeTypeOfKind(candidateType, TypeFlags.StringLiteral)) ||
                    (MaybeTypeOfKind(constraint, TypeFlags.Number) && MaybeTypeOfKind(candidateType, TypeFlags.NumberLiteral)) ||
                    (MaybeTypeOfKind(constraint, TypeFlags.BigInt) && MaybeTypeOfKind(candidateType, TypeFlags.BigIntLiteral)) ||
                    (MaybeTypeOfKind(constraint, TypeFlags.ESSymbol) && MaybeTypeOfKind(candidateType, TypeFlags.UniqueESSymbol)) ||
                    IsLiteralOfContextualType(candidateType, constraint);
            }
            // If the contextual type is a literal of a particular primitive type, we consider this a
            // literal context for all literals of that primitive type.
            return ((contextualType.flags & (TypeFlags.StringLiteral | TypeFlags.Index | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0 && MaybeTypeOfKind(candidateType, TypeFlags.StringLiteral)) ||
                ((contextualType.flags & TypeFlags.NumberLiteral) != 0 && MaybeTypeOfKind(candidateType, TypeFlags.NumberLiteral)) ||
                ((contextualType.flags & TypeFlags.BigIntLiteral) != 0 && MaybeTypeOfKind(candidateType, TypeFlags.BigIntLiteral)) ||
                ((contextualType.flags & TypeFlags.BooleanLiteral) != 0 && MaybeTypeOfKind(candidateType, TypeFlags.BooleanLiteral)) ||
                ((contextualType.flags & TypeFlags.UniqueESSymbol) != 0 && MaybeTypeOfKind(candidateType, TypeFlags.UniqueESSymbol));
        }
        return false;
    }

    // checker.go:27988-28003
    internal Type GetRegularTypeOfObjectLiteral(Type t)
    {
        if (!(IsObjectLiteralType(t) && (t.objectFlags & ObjectFlags.FreshLiteral) != 0))
        {
            return t;
        }
        var key = new CachedTypeKey(CachedTypeKind.RegularObjectLiteral, t.id);
        var cached = cachedTypes.GetValueOrDefault(key);
        if (cached is not null)
        {
            return cached;
        }
        var resolved = ResolveStructuredTypeMembers(t);
        var members = TransformTypeOfMembers(t, GetRegularTypeOfObjectLiteral);
        var regular = NewAnonymousType(t.symbol, members, resolved.CallSignatures(), resolved.ConstructSignatures(), resolved.indexInfos);
        regular.flags = resolved.flags;
        regular.objectFlags |= resolved.objectFlags & ~ObjectFlags.FreshLiteral;
        cachedTypes[key] = regular;
        return regular;
    }

    // checker.go:28005-28016
    internal SymbolTable TransformTypeOfMembers(Type t, Func<Type, Type> f)
    {
        var members = new SymbolTable();
        foreach (var propertyOrig in GetPropertiesOfObjectType(t))
        {
            var property = propertyOrig;
            var original = GetTypeOfSymbol(property);
            var updated = f(original);
            if (updated != original)
            {
                property = CreateSymbolWithType(property, updated);
            }
            members[property.Name] = property;
        }
        return members;
    }

    // checker.go:25469-25471
    internal Type MapType(Type t, Func<Type, Type> f) => MapTypeEx(t, f, false /*noReductions*/)!;

    // checker.go:25473-25508
    internal Type? MapTypeEx(Type t, Func<Type, Type?> f, bool noReductions)
    {
        if ((t.flags & TypeFlags.Never) != 0)
        {
            return t;
        }
        if ((t.flags & TypeFlags.Union) == 0)
        {
            return f(t);
        }
        var u = t.AsUnionType();
        var types = u.types;
        if (u.origin is not null && (u.origin.flags & TypeFlags.Union) != 0)
        {
            types = u.origin.Types();
        }
        var mappedTypes = new List<Type>(16);
        var changed = false;
        foreach (var s in types)
        {
            Type? mapped;
            if ((s.flags & TypeFlags.Union) != 0)
            {
                mapped = MapTypeEx(s, f, noReductions);
            }
            else
            {
                mapped = f(s);
            }
            if (mapped != s)
            {
                changed = true;
            }
            if (mapped is not null)
            {
                mappedTypes.Add(mapped);
            }
        }
        if (changed)
        {
            if (mappedTypes.Count == 0)
            {
                return null;
            }
            return GetUnionTypeEx(mappedTypes.ToArray(), CoreUtil.IfElse(noReductions, UnionReduction.None, UnionReduction.Literal), null /*alias*/, null /*origin*/);
        }
        return t;
    }

    // checker.go:25462-25467
    internal Type MapTypeWithAlias(Type t, Func<Type, Type> f, TypeAlias? alias)
    {
        if ((t.flags & TypeFlags.Union) != 0 && alias is not null)
        {
            return GetUnionTypeEx(CoreUtil.Map(t.Types(), f), UnionReduction.Literal, alias, null);
        }
        return MapType(t, f);
    }

    // utilities.go:805-807
    internal static bool IsDeclarationReadonly(Node declaration) =>
        (AstUtilities.GetCombinedModifierFlags(declaration) & ModifierFlags.Readonly) != 0 &&
        !AstUtilities.IsParameterPropertyDeclaration(declaration, declaration.Parent!);
}
