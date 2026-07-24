// Single dedup surface for cross-cluster methods that the inference cluster (inference.go →
// Checker.Inference.cs) CALLS but does not own — they belong to not-yet-landed waves (relater,
// variance, signatures/parameters, symbol-types, contextual, template-literal matching, jsnum
// predicates). All throw NotImplementedException so the call sites compile; deep runtime paths run
// only once the owning wave lands its real implementation, at which point that wave DELETES the
// corresponding stub here.
//
// §1.5: every name here is a Checker member (Symbol/Node come from Ts.Ast via the using below; the
// checker enums/types live in Ts.Checker) — no name collides with a sibling namespace, so no
// global:: qualification is needed except jsnum (Ts.JsNum.PseudoBigInt fully qualified).
//
// NOTE (do NOT duplicate — already stubbed/real elsewhere and merely CALLED here):
//   isTypeIdenticalTo (Instantiation.Deps), getActualTypeVariable/getStringMappingType (TypeNodes.Stubs),
//   isTypeSubtypeOf/isTypeStrictSubtypeOf/isTypeMatchedByTemplateLiteralType/isObjectOrArrayLiteralType
//   (TypeOps.Deps), mapType/getContextualType (Checker.Stubs), removeMissingType/getWidenedLiteralType/
//   isTypeAny (SymbolTypes), sliceTupleType/getEffectiveRestType/getTypeAtPosition/getRestTypeAtPosition/
//   isTypeAssignableTo (MembersSignatures.Deps), getMappedTypeModifiers/mapTypeWithAlias (Instantiation.Deps),
//   isMutableArrayLikeType (TypeNodes, real).

using System;
using Ts.Ast;
using Ts.Core;
using StrUtil = global::Ts.StringUtil.StringUtil;

namespace Ts.Checker;

public partial class Checker
{
    // getAliasVariances / getVariances / isObjectTypeWithInferableIndex / isDeeplyNestedType /
    // getUnmatchedProperty / getParameterCount / getThisTypeOfSignature / getTypePredicateOfSignature /
    // typePredicateKindsMatch / inferTypesFromTemplateLiteralType are OWNED by the relater wave
    // (Checker.Relater*.cs) — real implementations live there.

    // ---- misc type predicates / literal base (checker.go, later waves) ----
    // checker.go:13458-13460
    internal bool IsNonGenericObjectType(Type t) =>
        (t.flags & TypeFlags.Object) != 0 && !IsGenericMappedType(t);

    // getBaseTypeOfLiteralType (checker.go): widen a literal/enum type to its base intrinsic type.
    internal Type GetBaseTypeOfLiteralType(Type t)
    {
        if ((t.Flags & TypeFlags.EnumLike) != 0)
        {
            return GetBaseTypeOfEnumLikeType(t);
        }
        if ((t.Flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping)) != 0)
        {
            return stringType!;
        }
        if ((t.Flags & TypeFlags.NumberLiteral) != 0)
        {
            return numberType!;
        }
        if ((t.Flags & TypeFlags.BigIntLiteral) != 0)
        {
            return bigintType!;
        }
        if ((t.Flags & TypeFlags.BooleanLiteral) != 0)
        {
            return booleanType!;
        }
        if ((t.Flags & TypeFlags.Union) != 0)
        {
            return GetBaseTypeOfLiteralTypeUnion(t);
        }
        return t;
    }

    // checker.go:25385-25393
    internal Type GetBaseTypeOfLiteralTypeUnion(Type t)
    {
        var key = new CachedTypeKey(CachedTypeKind.LiteralUnionBaseType, t.id);
        if (cachedTypes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var result = MapType(t, GetBaseTypeOfLiteralType);
        cachedTypes[key] = result;
        return result;
    }

    // checker.go:28856-28861
    public Type RemoveMissingOrUndefinedType(Type t)
    {
        if (exactOptionalPropertyTypes)
        {
            return RemoveType(t, missingType!);
        }
        return GetTypeWithFacts(t, TypeFacts.NEUndefined);
    }

    // ---- const type variables / readonly symbols ----
    // checker.go:27967-27994
    internal bool IsConstTypeVariable(Type? t, int depth)
    {
        if (depth >= 5 || t is null)
        {
            return false;
        }
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            return t.symbol != null && CoreUtil.Some(t.symbol.Declarations, d => AstUtilities.HasSyntacticModifier(d, ModifierFlags.Const));
        }
        if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            return CoreUtil.Some(t.Types(), s => IsConstTypeVariable(s, depth));
        }
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            return IsConstTypeVariable(t.AsIndexedAccessType().objectType, depth + 1);
        }
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            return IsConstTypeVariable(GetConstraintOfConditionalType(t), depth + 1);
        }
        if ((t.flags & TypeFlags.Substitution) != 0)
        {
            return IsConstTypeVariable(t.AsSubstitutionType().baseType, depth);
        }
        if ((t.objectFlags & ObjectFlags.Mapped) != 0)
        {
            var typeVariable = GetHomomorphicTypeVariable(t);
            return typeVariable != null && IsConstTypeVariable(typeVariable, depth);
        }
        if (IsGenericTupleType(t))
        {
            var elementTypes = GetElementTypes(t);
            for (var i = 0; i < elementTypes.Length; i++)
            {
                if ((t.TargetTupleType().elementInfos![i].flags & ElementFlags.Variadic) != 0 && IsConstTypeVariable(elementTypes[i], depth))
                {
                    return true;
                }
            }
        }
        return false;
    }
    // checker.go:13773 — The following symbols are considered read-only:
    // Properties with a 'readonly' modifier; Variables declared with 'const'; Get accessors without matching
    // set accessors; Enum members; Object.defineProperty assignments with writable false or no setter;
    // Unions and intersections of the above (eagerly set isReadonly on creation).
    internal bool IsReadonlySymbol(Symbol symbol) =>
        (symbol.CheckFlags & CheckFlags.Readonly) != 0 ||
        (symbol.Flags & SymbolFlags.Property) != 0 && (GetDeclarationModifierFlagsFromSymbol(symbol) & ModifierFlags.Readonly) != 0 ||
        (symbol.Flags & SymbolFlags.Variable) != 0 && (GetDeclarationNodeFlagsFromSymbol(symbol) & NodeFlags.Constant) != 0 ||
        (symbol.Flags & SymbolFlags.Accessor) != 0 && (symbol.Flags & SymbolFlags.SetAccessor) == 0 ||
        (symbol.Flags & SymbolFlags.EnumMember) != 0 ||
        CoreUtil.Some(symbol.Declarations, IsReadonlyAssignmentDeclaration);

    // checker.go:13753
    private bool IsReadonlyAssignmentDeclaration(Node node)
    {
        if (!NodeTests.IsCallExpression(node))
        {
            return false;
        }
        var propertyDescriptorType = CheckExpressionCached(node.Arguments()![2]);
        var valueType = GetTypeOfPropertyOfType(propertyDescriptorType, "value");
        if (valueType != null)
        {
            var writableProp = GetPropertyOfType(propertyDescriptorType, "writable");
            if (writableProp != null)
            {
                Type writableType;
                if (writableProp.ValueDeclaration != null && NodeTests.IsPropertyAssignment(writableProp.ValueDeclaration))
                {
                    writableType = CheckExpression(writableProp.ValueDeclaration.Initializer()!);
                }
                else
                {
                    writableType = GetTypeOfSymbol(writableProp);
                }
                return (writableType.flags & TypeFlags.BooleanLiteral) != 0 && GetBooleanLiteralValue(writableType) == false;
            }
            return true;
        }
        return GetTypeOfPropertyOfType(propertyDescriptorType, "set") == null;
    }

    // ---- bigint literal type (checker.go, later wave) ----
    // checker.go — parseBigIntLiteralType.
    internal Type ParseBigIntLiteralType(string text) => GetBigIntLiteralType(global::Ts.JsNum.JsNum.ParseValidBigInt(text));

    // ---- contextual (#21) ----
    // getContextualTypeForObjectLiteralMethod (29721): real body landed in Checker.ContextualType.cs.

    // ---- jsnum / literal-value free functions (checker.go package funcs) ----
    // utilities.go:904
    internal static bool IsValidNumberString(string s, bool roundTripOnly)
    {
        if (s == "")
        {
            return false;
        }
        var n = global::Ts.JsNum.JsNum.FromString(s);
        return !n.IsNaN() && !n.IsInf() && (!roundTripOnly || n.ToString() == s);
    }
    // utilities.go:912
    internal static bool IsValidBigIntString(string s, bool roundTripOnly)
    {
        if (s == "")
        {
            return false;
        }
        var scanner = new global::Ts.Scanner.Scanner();
        scanner.SetSkipTrivia(false);
        var success = true;
        scanner.SetOnError((diagnostic, start, length, args) => { success = false; });
        scanner.SetText(s + "n");
        var result = scanner.Scan();
        var negative = result == SyntaxKind.MinusToken;
        if (negative)
        {
            result = scanner.Scan();
        }
        var flags = scanner.TokenFlags();
        // validate that scanning proceeded without error, a bigint was scanned covering the full input
        // (scanner one char beyond the augmented length), with no numeric separator.
        return success && result == SyntaxKind.BigIntLiteral && scanner.TokenEnd() == s.Length + 1 && (flags & TokenFlags.ContainsSeparator) == 0 &&
            (!roundTripOnly || s == new global::Ts.JsNum.PseudoBigInt(global::Ts.JsNum.JsNum.ParsePseudoBigInt(scanner.TokenValue()), negative).ToString());
    }
    // checker.go:28999-29013 — real body (lib-gate wave P); previously a stub here.
    internal static string ApplyStringMapping(Symbol symbol, string str)
    {
        switch (IntrinsicTypeKinds.GetValueOrDefault(symbol.Name))
        {
            case IntrinsicTypeKind.Uppercase:
                return StrUtil.ToUpperJS(str);
            case IntrinsicTypeKind.Lowercase:
                return StrUtil.ToLowerJS(str);
            case IntrinsicTypeKind.Capitalize:
            {
                var (_, size) = StrUtil.DecodeJSStringRune(str);
                return StrUtil.ToUpperJS(str[..size]) + str[size..];
            }
            case IntrinsicTypeKind.Uncapitalize:
            {
                var (_, size) = StrUtil.DecodeJSStringRune(str);
                return StrUtil.ToLowerJS(str[..size]) + str[size..];
            }
        }
        return str;
    }
    // checker.go:25262
    internal static global::Ts.JsNum.PseudoBigInt GetBigIntLiteralValue(Type t) => (global::Ts.JsNum.PseudoBigInt)t.AsLiteralType().value!;
    // checker.go:25266
    internal static bool GetBooleanLiteralValue(Type t) => (bool)t.AsLiteralType().value!;
}
