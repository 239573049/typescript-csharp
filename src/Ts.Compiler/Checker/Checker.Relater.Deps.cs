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
    // signatureToString / signatureToStringEx: real bodies in Checker.SymbolToString.cs (printer.go:309).
    // typePredicateToString / typePredicateToStringEx: real bodies in Checker.SymbolToString.cs.
    internal string ValueToString(object? value) => throw new NotImplementedException("valueToString");
    internal void ReportNonExportedMember(Node name, string declarationName, Symbol moduleSymbol, string moduleName) => throw new NotImplementedException("reportNonExportedMember");

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
    // getUnionOrIntersectionProperty (checker.go:21333): real body landed in Checker.Instantiation.cs.
    // checker.go:29071-29081
    internal Type? GetTypeOfPropertyOrIndexSignatureOfType(Type type, string name)
    {
        var propType = GetTypeOfPropertyOfType(type, name);
        if (propType is not null)
        {
            return propType;
        }
        var indexInfo = GetApplicableIndexInfoForName(type, name);
        if (indexInfo is not null)
        {
            return AddOptionalityEx(indexInfo.valueType, true /*isProperty*/, true /*isOptional*/);
        }
        return null;
    }
    // GetRegularTypeOfObjectLiteral is now REAL in Checker.Widening.cs (checker.go:27988).
    // checker.go:27690
    internal bool TypeHasCallOrConstructSignatures(Type type) =>
        (type.flags & TypeFlags.StructuredType) != 0 && (ResolveStructuredTypeMembers(type).signatures?.Length ?? 0) != 0;
    // GetBaseTypeOfEnumLikeType is now REAL in Checker.Widening.cs (checker.go:25378).
    // checker.go:30897
    internal bool IsFunctionObjectType(Type type)
    {
        if ((type.objectFlags & ObjectFlags.EvolvingArray) != 0)
        {
            return false;
        }
        // We do a quick check for a "bind" property before performing the more expensive subtype
        // check. This gives us a quicker out in the common case where an object type is not a function.
        var resolved = ResolveStructuredTypeMembers(type);
        return (resolved.signatures?.Length ?? 0) != 0 ||
            resolved.members?.GetValueOrDefault("bind") != null && IsTypeSubtypeOf(type, globalFunctionType!);
    }
    // compareProperties (checker.go:27538) — two members are identical when they are public
    // properties with identical names, optionality, and types, or private/protected properties
    // originating in the same declaration and having identical types.
    internal Ternary CompareProperties(Symbol sourceProp, Symbol targetProp, Func<Type, Type, Ternary> compareTypes)
    {
        if (sourceProp == targetProp)
        {
            return Ternary.True;
        }
        var sourcePropAccessibility = GetDeclarationModifierFlagsFromSymbol(sourceProp) & ModifierFlags.NonPublicAccessibilityModifier;
        var targetPropAccessibility = GetDeclarationModifierFlagsFromSymbol(targetProp) & ModifierFlags.NonPublicAccessibilityModifier;
        if (sourcePropAccessibility != targetPropAccessibility)
        {
            return Ternary.False;
        }
        if (sourcePropAccessibility != ModifierFlags.None)
        {
            if (GetTargetSymbol(sourceProp) != GetTargetSymbol(targetProp))
            {
                return Ternary.False;
            }
        }
        else
        {
            if ((sourceProp.Flags & SymbolFlags.Optional) != (targetProp.Flags & SymbolFlags.Optional))
            {
                return Ternary.False;
            }
        }
        if (IsReadonlySymbol(sourceProp) != IsReadonlySymbol(targetProp))
        {
            return Ternary.False;
        }
        return compareTypes(GetNonMissingTypeOfSymbol(sourceProp), GetNonMissingTypeOfSymbol(targetProp));
    }

    // isPropertyIdenticalTo (checker.go)
    internal bool IsPropertyIdenticalTo(Symbol sourceProp, Symbol targetProp) =>
        CompareProperties(sourceProp, targetProp, CompareTypesIdentical) != Ternary.False;
    // isNonGenericObjectType / getBaseTypeOfLiteralType / isReadonlySymbol stubbed in Checker.Inference.Deps.cs.
    // getSimplifiedTypeOrConstraint is real in Checker.Constraints.cs.

    // ---- members / modifiers ----
    // checker.go:11889-11894
    internal Type? GetDeclaringClass(Symbol prop)
    {
        if (prop.Parent is not null && (prop.Parent.Flags & SymbolFlags.Class) != 0)
        {
            return GetDeclaredTypeOfSymbol(GetParentOfSymbol(prop)!);
        }
        return null;
    }
    // checker.go:11897
    internal bool IsValidOverrideOf(Symbol sourceProp, Symbol targetProp) =>
        !ForEachProperty(targetProp, tp =>
        {
            if ((GetDeclarationModifierFlagsFromSymbol(tp) & ModifierFlags.Protected) != 0)
            {
                return !IsPropertyInClassDerivedFrom(sourceProp, GetDeclaringClass(tp));
            }
            return false;
        });

    // checker.go:11907 — return true if some underlying source property is declared in a class
    // that derives (directly or indirectly) from the given base class.
    private bool IsPropertyInClassDerivedFrom(Symbol prop, Type? baseClass) =>
        ForEachProperty(prop, sp =>
        {
            var sourceClass = GetDeclaringClass(sp);
            if (sourceClass != null)
            {
                return HasBaseType(sourceClass, baseClass);
            }
            return false;
        });

    // ---- mapped-type helpers ----
    // getCombinedMappedTypeOptionality (28797) implemented in Checker.Relater.Support.cs (#20).
    // getApparentMappedTypeKeys (22624) implemented in Checker.Instantiation.cs (#14).

    // ---- facts (flow.go) ----
    // getTypeFacts 30731: real body landed in Checker.TypeFacts.cs.

    // ---- suggestions / jsx (services / jsx waves) ----
    // getSuggestedSymbolForNonexistentJSXAttribute: real body landed in Checker.Jsx.cs (jsx wave).
    // getSuggestionForNonexistentProperty (27089): real body landed in Checker.IndexedAccess.cs
    // (spelling-suggestion wave).
    // checker.go:27118
    internal Type? GetSuggestedTypeForNonexistentStringLiteralType(Type source, Type target)
    {
        var candidates = global::Ts.Core.CoreUtil.FilterSeq(target.Types(), t => (t.flags & TypeFlags.StringLiteral) != 0);
        return global::Ts.Core.CoreUtil.GetSpellingSuggestion(GetStringLiteralValue(source), candidates, GetStringLiteralValue, CompareTypes);
    }
    internal Symbol[] GetExactOptionalUnassignableProperties(Type source, Type target) => throw new NotImplementedException("getExactOptionalUnassignableProperties");
    // getJsxType / elaborateJsxComponents: real bodies landed in Checker.Jsx.cs (jsx wave).

    // ---- expression checking / contextual (expressions, contextual waves) ----
    // pushContextualType / popContextualType (30633/30637): real bodies in Checker.ContextualType.cs.
    // getEffectiveCheckNode (9357): real body in Checker.CallResolution.Applicability.cs.
    // checker.go:7998 — checkArrayLiteral: real body landed in Checker.ArrayLiteral.cs.
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
