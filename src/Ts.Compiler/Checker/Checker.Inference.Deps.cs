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

namespace Ts.Checker;

public partial class Checker
{
    // getAliasVariances / getVariances / isObjectTypeWithInferableIndex / isDeeplyNestedType /
    // getUnmatchedProperty / getParameterCount / getThisTypeOfSignature / getTypePredicateOfSignature /
    // typePredicateKindsMatch / inferTypesFromTemplateLiteralType are OWNED by the relater wave
    // (Checker.Relater*.cs) — real implementations live there.

    // ---- misc type predicates / literal base (checker.go, later waves) ----
    internal bool IsNonGenericObjectType(Type t) => throw new NotImplementedException("isNonGenericObjectType");

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

    // getBaseTypeOfLiteralTypeUnion (checker.go) — deferred; not reached by the intrinsic-literal path.
    internal Type GetBaseTypeOfLiteralTypeUnion(Type t) => throw new NotImplementedException("getBaseTypeOfLiteralTypeUnion");

    internal Type RemoveMissingOrUndefinedType(Type type) => throw new NotImplementedException("removeMissingOrUndefinedType");

    // ---- const type variables / readonly symbols ----
    internal bool IsConstTypeVariable(Type? type, int depth) => throw new NotImplementedException("isConstTypeVariable");
    internal bool IsReadonlySymbol(Symbol symbol) => throw new NotImplementedException("isReadonlySymbol");

    // ---- bigint literal type (checker.go, later wave) ----
    internal Type ParseBigIntLiteralType(string text) => throw new NotImplementedException("parseBigIntLiteralType");

    // ---- contextual (#21) ----
    internal Type? GetContextualTypeForObjectLiteralMethod(Node node, ContextFlags contextFlags) => throw new NotImplementedException("getContextualTypeForObjectLiteralMethod");

    // ---- jsnum / literal-value free functions (checker.go package funcs) ----
    internal static bool IsValidNumberString(string s, bool roundTripOnly) => throw new NotImplementedException("isValidNumberString");
    internal static bool IsValidBigIntString(string s, bool roundTripOnly) => throw new NotImplementedException("isValidBigIntString");
    internal static string ApplyStringMapping(Symbol symbol, string str) => throw new NotImplementedException("applyStringMapping");
    internal static global::Ts.JsNum.PseudoBigInt GetBigIntLiteralValue(Type t) => throw new NotImplementedException("getBigIntLiteralValue");
    internal static bool GetBooleanLiteralValue(Type t) => throw new NotImplementedException("getBooleanLiteralValue");
}
