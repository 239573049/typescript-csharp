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

    // getBaseTypeOfLiteralTypeUnion (checker.go) — deferred; not reached by the intrinsic-literal path.
    internal Type GetBaseTypeOfLiteralTypeUnion(Type t) => throw new NotImplementedException("getBaseTypeOfLiteralTypeUnion");

    internal Type RemoveMissingOrUndefinedType(Type type) => throw new NotImplementedException("removeMissingOrUndefinedType");

    // ---- const type variables / readonly symbols ----
    internal bool IsConstTypeVariable(Type? type, int depth) => throw new NotImplementedException("isConstTypeVariable");
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
    internal Type ParseBigIntLiteralType(string text) => throw new NotImplementedException("parseBigIntLiteralType");

    // ---- contextual (#21) ----
    // getContextualTypeForObjectLiteralMethod (29721): real body landed in Checker.ContextualType.cs.

    // ---- jsnum / literal-value free functions (checker.go package funcs) ----
    internal static bool IsValidNumberString(string s, bool roundTripOnly) => throw new NotImplementedException("isValidNumberString");
    internal static bool IsValidBigIntString(string s, bool roundTripOnly) => throw new NotImplementedException("isValidBigIntString");
    internal static string ApplyStringMapping(Symbol symbol, string str) => throw new NotImplementedException("applyStringMapping");
    internal static global::Ts.JsNum.PseudoBigInt GetBigIntLiteralValue(Type t) => throw new NotImplementedException("getBigIntLiteralValue");
    internal static bool GetBooleanLiteralValue(Type t) => throw new NotImplementedException("getBooleanLiteralValue");
}
