// PLACEHOLDER STUBS for the relater wave (plan/03 §4.2, §7).
//
// Cross-cluster helpers that relater.go CALLS but does not own — they live in expressions, symbol
// resolution, contextual typing, jsx, printer, evaluator, utilities and other not-yet-landed waves.
// They had no C# definition at build time, so they are stubbed here (throw) purely to compile the
// relater cluster. **When the owning cluster lands a real definition, DELETE the corresponding stub
// here** — this file is the single dedup surface for the relater wave.

using System;
using Ts.Ast;
using Ts.Core;
using Message = global::Ts.Diagnostics.Message;

namespace Ts.Checker;

public partial class Checker
{
    // ---- printer / to-string (printer.go) ----
    internal string SignatureToString(Signature signature) => throw new NotImplementedException("signatureToString");
    internal string TypePredicateToString(TypePredicate predicate) => throw new NotImplementedException("typePredicateToString");
    internal string ValueToString(object? value) => throw new NotImplementedException("valueToString");

    // ---- union / property helpers (checker.go / members) ----
    // checker.go:27669-27681
    internal bool IsUnknownLikeUnionType(Type t)
    {
        if (strictNullChecks && (t.flags & TypeFlags.Union) != 0)
        {
            if ((t.objectFlags & ObjectFlags.IsUnknownLikeUnionComputed) == 0)
            {
                t.objectFlags |= ObjectFlags.IsUnknownLikeUnionComputed;
                var types = t.Types();
                if (types.Length >= 3 && (types[0].flags & TypeFlags.Undefined) != 0 && (types[1].flags & TypeFlags.Null) != 0 && global::Ts.Core.CoreUtil.Some(types, IsEmptyAnonymousObjectType))
                {
                    t.objectFlags |= ObjectFlags.IsUnknownLikeUnion;
                }
            }
            return (t.objectFlags & ObjectFlags.IsUnknownLikeUnion) != 0;
        }
        return false;
    }
    internal Symbol? GetUnionOrIntersectionProperty(Type type, string name, bool skipObjectFunctionPropertyAugment) => throw new NotImplementedException("getUnionOrIntersectionProperty");
    internal Type? GetTypeOfPropertyOrIndexSignatureOfType(Type type, string name) => throw new NotImplementedException("getTypeOfPropertyOrIndexSignatureOfType");
    internal Type GetRegularTypeOfObjectLiteral(Type type) => throw new NotImplementedException("getRegularTypeOfObjectLiteral");
    internal bool TypeHasCallOrConstructSignatures(Type type) => throw new NotImplementedException("typeHasCallOrConstructSignatures");
    internal Type GetBaseTypeOfEnumLikeType(Type type) => throw new NotImplementedException("getBaseTypeOfEnumLikeType");
    internal bool IsFunctionObjectType(Type type) => throw new NotImplementedException("isFunctionObjectType");
    internal Ternary CompareProperties(Symbol sourceProp, Symbol targetProp, Func<Type, Type, Ternary> compareTypes) => throw new NotImplementedException("compareProperties");
    // isNonGenericObjectType / getBaseTypeOfLiteralType / isReadonlySymbol stubbed in Checker.Inference.Deps.cs.
    // getSimplifiedTypeOrConstraint is real in Checker.Constraints.cs.

    // ---- members / modifiers ----
    internal Type? GetDeclaringClass(Symbol prop) => throw new NotImplementedException("getDeclaringClass");
    internal bool IsValidOverrideOf(Symbol sourceProp, Symbol targetProp) => throw new NotImplementedException("isValidOverrideOf");

    // ---- mapped-type helpers ----
    internal int GetCombinedMappedTypeOptionality(Type type) => throw new NotImplementedException("getCombinedMappedTypeOptionality");
    internal Type GetApparentMappedTypeKeys(Type nameType, Type targetType) => throw new NotImplementedException("getApparentMappedTypeKeys");

    // ---- facts (flow.go) ----
    internal TypeFacts GetTypeFacts(Type type, TypeFacts facts) => throw new NotImplementedException("getTypeFacts");

    // ---- suggestions / jsx (services / jsx waves) ----
    // getSuggestedSymbolForNonexistentJSXAttribute: real body landed in Checker.Jsx.cs (jsx wave).
    internal string GetSuggestionForNonexistentProperty(string name, Type containingType) => throw new NotImplementedException("getSuggestionForNonexistentProperty");
    internal Type? GetSuggestedTypeForNonexistentStringLiteralType(Type source, Type target) => throw new NotImplementedException("getSuggestedTypeForNonexistentStringLiteralType");
    internal Symbol[] GetExactOptionalUnassignableProperties(Type source, Type target) => throw new NotImplementedException("getExactOptionalUnassignableProperties");
    // getJsxType / elaborateJsxComponents: real bodies landed in Checker.Jsx.cs (jsx wave).

    // ---- expression checking / contextual (expressions, contextual waves) ----
    internal void PushContextualType(Node node, Type type, bool isCache) => throw new NotImplementedException("pushContextualType");
    internal void PopContextualType() => throw new NotImplementedException("popContextualType");
    internal Type CheckArrayLiteral(Node node, CheckMode checkMode) => throw new NotImplementedException("checkArrayLiteral");
    internal Node GetEffectiveCheckNode(Node node) => throw new NotImplementedException("getEffectiveCheckNode");
    // isExactOptionalPropertyMismatch (13076): real body landed in Checker.OperatorExpressions.cs (#8 wave).

    // isValidNumberString / isValidBigIntString stubbed in Checker.Inference.Deps.cs.
}

// jsx.go:41-63 — JSX namespace member names. Go models this as an anonymous struct value with
// exported string fields; C# uses a static class of const strings (Ordinal, no culture).
internal static class JsxNames
{
    internal const string JSX = "JSX";
    internal const string IntrinsicElements = "IntrinsicElements";
    internal const string ElementClass = "ElementClass";
    internal const string ElementAttributesPropertyNameContainer = "ElementAttributesProperty";
    internal const string ElementChildrenAttributeNameContainer = "ElementChildrenAttribute";
    internal const string Element = "Element";
    internal const string ElementType = "ElementType";
    internal const string IntrinsicAttributes = "IntrinsicAttributes";
    internal const string IntrinsicClassAttributes = "IntrinsicClassAttributes";
    internal const string LibraryManagedAttributes = "LibraryManagedAttributes";
}

// jsx.go:65-69
internal static class ReactNames
{
    internal const string Fragment = "Fragment";
}
