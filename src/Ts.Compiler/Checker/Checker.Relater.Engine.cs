// Ported from tsgo internal/checker/relater.go — the object-pooled `Relater` structural relation
// engine (plan/03 §4.2, §7). Each Relater borrows a relationCount budget and drives recursive
// structural comparison (recursiveTypeRelatedTo/structuredTypeRelatedTo and the union/intersection,
// property, signature and index-info sub-relations).
//
// PORT-NOTE: `tracing.*` (Instant/Push) calls are omitted (--generateTrace is phase 9; the Tracer
// shell has no Push/Instant surface). They are purely observational and never affect results.
// §1.5: diagnostics via the `Diag` alias.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;
using Message = global::Ts.Diagnostics.Message;

namespace Ts.Checker;

// relater.go:2547-2564
internal sealed class Relater
{
    internal Checker c;
    internal Relation relation = null!;
    internal Node? errorNode;
    internal ErrorChain? errorChain;
    internal List<Diagnostic> relatedInfo = [];
    internal List<CacheHashKey> maybeKeys = [];
    internal HashSet<CacheHashKey> maybeKeysSet = [];
    internal List<Type> sourceStack = [];
    internal List<Type> targetStack = [];
    internal int maybeCount;
    internal int sourceDepth;
    internal int targetDepth;
    internal ExpandingFlags expandingFlags;
    internal bool overflow;
    internal int relationCount;
    internal Relater? next;

    internal Relater(Checker c)
    {
        this.c = c;
    }

    // relater.go:2588-2590
    internal Ternary IsRelatedToSimple(Type source, Type target) =>
        IsRelatedToEx(source, target, RecursionFlags.Both, false, null, IntersectionState.None);

    // relater.go:2592-2594
    internal Ternary IsRelatedToWorker(Type source, Type target, bool reportErrors) =>
        IsRelatedToEx(source, target, RecursionFlags.Both, reportErrors, null, IntersectionState.None);

    // relater.go:2596-2598
    internal Ternary IsRelatedTo(Type source, Type target, RecursionFlags recursionFlags, bool reportErrors) =>
        IsRelatedToEx(source, target, recursionFlags, reportErrors, null, IntersectionState.None);

    // relater.go:2600-2712
    internal Ternary IsRelatedToEx(Type originalSource, Type originalTarget, RecursionFlags recursionFlags, bool reportErrors, Message? headMessage, IntersectionState intersectionState)
    {
        if (originalSource == originalTarget)
        {
            return Ternary.True;
        }
        // Before normalization: if `source` is an object type, and `target` is primitive,
        // skip all the checks we don't need and just return `isSimpleTypeRelatedTo` result
        if ((originalSource.flags & TypeFlags.Object) != 0 && (originalTarget.flags & TypeFlags.Primitive) != 0)
        {
            if (relation == c.comparableRelation && (originalTarget.flags & TypeFlags.Never) == 0 && c.IsSimpleTypeRelatedTo(originalTarget, originalSource, relation, null) ||
                c.IsSimpleTypeRelatedTo(originalSource, originalTarget, relation, reportErrors ? ReportError : null))
            {
                return Ternary.True;
            }
            if (reportErrors)
            {
                ReportErrorResults(originalSource, originalTarget, originalSource, originalTarget, headMessage);
            }
            return Ternary.False;
        }
        // Normalize the source and target types.
        var source = c.GetNormalizedType(originalSource, false);
        var target = c.GetNormalizedType(originalTarget, true);
        if (source == target)
        {
            return Ternary.True;
        }
        if (relation == c.identityRelation)
        {
            if (source.flags != target.flags)
            {
                return Ternary.False;
            }
            if ((source.flags & TypeFlags.Singleton) != 0)
            {
                return Ternary.True;
            }
            TraceUnionsOrIntersectionsTooLarge(source, target);
            return RecursiveTypeRelatedTo(source, target, false, IntersectionState.None, recursionFlags);
        }
        // Fastpath comparing a type parameter to exactly its constraint.
        if ((source.flags & TypeFlags.TypeParameter) != 0 && c.GetConstraintOfType(source) == target)
        {
            return Ternary.True;
        }
        // Relating a definitely non-nullable type to a union that includes null/undefined plus a single non-nullable type.
        if ((source.flags & TypeFlags.DefinitelyNonNullable) != 0 && (target.flags & TypeFlags.Union) != 0)
        {
            var types = target.Types();
            Type? candidate = null;
            if (types.Length == 2 && (types[0].flags & TypeFlags.Nullable) != 0)
            {
                candidate = types[1];
            }
            else if (types.Length == 3 && (types[0].flags & TypeFlags.Nullable) != 0 && (types[1].flags & TypeFlags.Nullable) != 0)
            {
                candidate = types[2];
            }
            if (candidate != null && (candidate.flags & TypeFlags.Nullable) == 0)
            {
                target = c.GetNormalizedType(candidate, true);
                if (source == target)
                {
                    return Ternary.True;
                }
            }
        }
        if (relation == c.comparableRelation && (target.flags & TypeFlags.Never) == 0 && c.IsSimpleTypeRelatedTo(target, source, relation, null) ||
            c.IsSimpleTypeRelatedTo(source, target, relation, reportErrors ? ReportError : null))
        {
            return Ternary.True;
        }
        if ((source.flags & TypeFlags.StructuredOrInstantiable) != 0 || (target.flags & TypeFlags.StructuredOrInstantiable) != 0)
        {
            bool isPerformingExcessPropertyChecks = (intersectionState & IntersectionState.Target) == 0 && Checker.IsObjectLiteralType(source) && (source.objectFlags & ObjectFlags.FreshLiteral) != 0;
            if (isPerformingExcessPropertyChecks)
            {
                if (HasExcessProperties(source, target, reportErrors))
                {
                    if (reportErrors)
                    {
                        ReportRelationError(headMessage, source, originalTarget.alias != null ? originalTarget : target);
                    }
                    return Ternary.False;
                }
            }
            bool isPerformingCommonPropertyChecks = (relation != c.comparableRelation || Checker.IsUnitType(source)) &&
                (intersectionState & IntersectionState.Target) == 0 &&
                (source.flags & (TypeFlags.Primitive | TypeFlags.Object | TypeFlags.Intersection)) != 0 && source != c.globalObjectType &&
                (target.flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && c.IsWeakType(target) && (c.GetPropertiesOfType(source).Length > 0 || c.TypeHasCallOrConstructSignatures(source));
            bool isComparingJsxAttributes = (source.objectFlags & ObjectFlags.JsxAttributes) != 0;
            if (isPerformingCommonPropertyChecks && !c.HasCommonProperties(source, target, isComparingJsxAttributes))
            {
                if (reportErrors)
                {
                    var sourceString = c.TypeToString(originalSource.alias != null ? originalSource : source);
                    var targetString = c.TypeToString(originalTarget.alias != null ? originalTarget : target);
                    var calls = c.GetSignaturesOfType(source, SignatureKind.Call);
                    var constructs = c.GetSignaturesOfType(source, SignatureKind.Construct);
                    if (calls.Length > 0 && IsRelatedTo(c.GetReturnTypeOfSignature(calls[0]), target, RecursionFlags.Source, false) != Ternary.False ||
                        constructs.Length > 0 && IsRelatedTo(c.GetReturnTypeOfSignature(constructs[0]), target, RecursionFlags.Source, false) != Ternary.False)
                    {
                        ReportError(Diag.Value_of_type_0_has_no_properties_in_common_with_type_1_Did_you_mean_to_call_it, sourceString, targetString);
                    }
                    else
                    {
                        ReportError(Diag.Type_0_has_no_properties_in_common_with_type_1, sourceString, targetString);
                    }
                }
                return Ternary.False;
            }
            TraceUnionsOrIntersectionsTooLarge(source, target);
            bool skipCaching = (source.flags & TypeFlags.Union) != 0 && source.Types().Length < 4 && (target.flags & TypeFlags.Union) == 0 ||
                (target.flags & TypeFlags.Union) != 0 && target.Types().Length < 4 && (source.flags & TypeFlags.StructuredOrInstantiable) == 0;
            Ternary result;
            if (skipCaching)
            {
                result = UnionOrIntersectionRelatedTo(source, target, reportErrors, intersectionState);
            }
            else
            {
                result = RecursiveTypeRelatedTo(source, target, reportErrors, intersectionState, recursionFlags);
            }
            if (result != Ternary.False)
            {
                return result;
            }
        }
        if (reportErrors)
        {
            ReportErrorResults(originalSource, originalTarget, source, target, headMessage);
        }
        return Ternary.False;
    }

    // relater.go:2714-2794
    internal bool HasExcessProperties(Type source, Type target, bool reportErrors)
    {
        if (!Checker.IsExcessPropertyCheckTarget(target) || !c.noImplicitAny && (target.objectFlags & ObjectFlags.JSLiteral) != 0)
        {
            return false;
        }
        bool isComparingJsxAttributes = (source.objectFlags & ObjectFlags.JsxAttributes) != 0;
        if ((relation == c.assignableRelation || relation == c.comparableRelation) && (c.IsTypeSubsetOf(c.globalObjectType!, target) || (!isComparingJsxAttributes && c.IsEmptyObjectType(target))))
        {
            return false;
        }
        var reducedTarget = target;
        Type[]? checkTypes = null;
        if ((target.flags & TypeFlags.Union) != 0)
        {
            reducedTarget = c.FindMatchingDiscriminantType(source, target, IsRelatedToSimple);
            if (reducedTarget == null)
            {
                reducedTarget = c.FilterPrimitivesIfContainsNonPrimitive(target);
            }
            checkTypes = reducedTarget.Distributed();
        }
        foreach (var prop in c.GetPropertiesOfType(source))
        {
            if (Checker.ShouldCheckAsExcessProperty(prop, source.symbol!) && !Checker.IsIgnoredJsxProperty(source, prop))
            {
                if (!c.IsKnownProperty(reducedTarget, prop.Name, isComparingJsxAttributes))
                {
                    if (reportErrors)
                    {
                        var errorTarget = c.FilterType(reducedTarget, Checker.IsExcessPropertyCheckTarget);
                        if (errorNode == null)
                        {
                            throw new InvalidOperationException("No errorNode in hasExcessProperties");
                        }
                        if (NodeTests.IsJsxAttributes(errorNode) || (NodeTests.IsJsxOpeningElement(errorNode) || NodeTests.IsJsxSelfClosingElement(errorNode)) || (NodeTests.IsJsxOpeningElement(errorNode.Parent!) || NodeTests.IsJsxSelfClosingElement(errorNode.Parent!)))
                        {
                            if (prop.ValueDeclaration != null && NodeTests.IsJsxAttribute(prop.ValueDeclaration) && AstUtilities.GetSourceFileOfNode(errorNode) == AstUtilities.GetSourceFileOfNode(prop.ValueDeclaration.Name()))
                            {
                                errorNode = prop.ValueDeclaration.Name();
                            }
                            var propName = c.SymbolToString(prop);
                            var suggestionSymbol = c.GetSuggestedSymbolForNonexistentJSXAttribute(propName, errorTarget);
                            if (suggestionSymbol != null)
                            {
                                ReportError(Diag.Property_0_does_not_exist_on_type_1_Did_you_mean_2, propName, c.TypeToString(errorTarget), c.SymbolToString(suggestionSymbol));
                            }
                            else
                            {
                                ReportError(Diag.Property_0_does_not_exist_on_type_1, propName, c.TypeToString(errorTarget));
                            }
                        }
                        else
                        {
                            Node? objectLiteralDeclaration = null;
                            if (source.symbol != null)
                            {
                                objectLiteralDeclaration = CoreUtil.FirstOrNil(source.symbol.Declarations);
                            }
                            string suggestion = "";
                            if (prop.ValueDeclaration != null && AstUtilities.IsObjectLiteralElement(prop.ValueDeclaration) &&
                                AstUtilities.FindAncestor(prop.ValueDeclaration, d => d == objectLiteralDeclaration) != null &&
                                AstUtilities.GetSourceFileOfNode(objectLiteralDeclaration) == AstUtilities.GetSourceFileOfNode(errorNode))
                            {
                                var name = prop.ValueDeclaration.Name();
                                errorNode = name;
                                if (name != null && NodeTests.IsIdentifier(name))
                                {
                                    suggestion = c.GetSuggestionForNonexistentProperty(name.Text(), errorTarget);
                                }
                            }
                            if (suggestion != "")
                            {
                                ReportError(Diag.Object_literal_may_only_specify_known_properties_but_0_does_not_exist_in_type_1_Did_you_mean_to_write_2, c.SymbolToString(prop), c.TypeToString(errorTarget), suggestion);
                            }
                            else
                            {
                                ReportError(Diag.Object_literal_may_only_specify_known_properties_and_0_does_not_exist_in_type_1, c.SymbolToString(prop), c.TypeToString(errorTarget));
                            }
                        }
                    }
                    return true;
                }
                if (checkTypes != null && IsRelatedTo(c.GetTypeOfSymbol(prop), c.GetTypeOfPropertyInTypes(checkTypes, prop.Name), RecursionFlags.Both, reportErrors) == Ternary.False)
                {
                    if (reportErrors)
                    {
                        ReportError(Diag.Types_of_property_0_are_incompatible, c.SymbolToString(prop));
                    }
                    return true;
                }
            }
        }
        return false;
    }

    // relater.go:2849-2916
    internal Ternary UnionOrIntersectionRelatedTo(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        if ((source.flags & TypeFlags.Union) != 0)
        {
            if ((target.flags & TypeFlags.Union) != 0)
            {
                var sourceOrigin = source.AsUnionType().origin;
                if (sourceOrigin != null && (sourceOrigin.flags & TypeFlags.Intersection) != 0 && target.alias != null && Array.IndexOf(sourceOrigin.Types(), target) >= 0)
                {
                    return Ternary.True;
                }
                var targetOrigin = target.AsUnionType().origin;
                if (targetOrigin != null && (targetOrigin.flags & TypeFlags.Union) != 0 && source.alias != null && Array.IndexOf(targetOrigin.Types(), source) >= 0)
                {
                    return Ternary.True;
                }
            }
            if (relation == c.comparableRelation)
            {
                return SomeTypeRelatedToType(source, target, reportErrors && (source.flags & TypeFlags.Primitive) == 0, intersectionState);
            }
            return EachTypeRelatedToType(source, target, reportErrors && (source.flags & TypeFlags.Primitive) == 0, intersectionState);
        }
        if ((target.flags & TypeFlags.Union) != 0)
        {
            return TypeRelatedToSomeType(c.GetRegularTypeOfObjectLiteral(source), target, reportErrors && (source.flags & TypeFlags.Primitive) == 0 && (target.flags & TypeFlags.Primitive) == 0, intersectionState);
        }
        if ((target.flags & TypeFlags.Intersection) != 0)
        {
            return TypeRelatedToEachType(source, target, reportErrors, IntersectionState.Target);
        }
        // Source is an intersection.
        if (relation == c.comparableRelation && (target.flags & TypeFlags.Primitive) != 0)
        {
            var constraints = CoreUtil.SameMap(source.Types(), t =>
            {
                if ((t.flags & TypeFlags.Instantiable) != 0)
                {
                    var constraint = c.GetBaseConstraintOfType(t);
                    if (constraint != null)
                    {
                        return constraint;
                    }
                    return c.unknownType!;
                }
                return t;
            })!;
            if (!CoreUtil.Same(constraints, source.Types()))
            {
                source = c.GetIntersectionType(constraints);
                if ((source.flags & TypeFlags.Never) != 0)
                {
                    return Ternary.False;
                }
                if ((source.flags & TypeFlags.Intersection) == 0)
                {
                    var result = IsRelatedTo(source, target, RecursionFlags.Source, false);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                    return IsRelatedTo(target, source, RecursionFlags.Source, false);
                }
            }
        }
        return SomeTypeRelatedToType(source, target, false, IntersectionState.Source);
    }

    // relater.go:2918-2930
    internal Ternary SomeTypeRelatedToType(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        var sourceTypes = source.Types();
        if ((source.flags & TypeFlags.Union) != 0 && Checker.ContainsType(sourceTypes, target))
        {
            return Ternary.True;
        }
        for (int i = 0; i < sourceTypes.Length; i++)
        {
            var related = IsRelatedToEx(sourceTypes[i], target, RecursionFlags.Source, reportErrors && i == sourceTypes.Length - 1, null, intersectionState);
            if (related != Ternary.False)
            {
                return related;
            }
        }
        return Ternary.False;
    }

    // relater.go:2932-2962
    internal Ternary EachTypeRelatedToType(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        var result = Ternary.True;
        var sourceTypes = source.Types();
        var strippedTarget = GetUndefinedStrippedTargetIfNeeded(source, target);
        Type[] strippedTypes = [];
        if ((strippedTarget.flags & TypeFlags.Union) != 0)
        {
            strippedTypes = strippedTarget.Types();
        }
        for (int i = 0; i < sourceTypes.Length; i++)
        {
            var sourceType = sourceTypes[i];
            if ((strippedTarget.flags & TypeFlags.Union) != 0 && sourceTypes.Length >= strippedTypes.Length && sourceTypes.Length % strippedTypes.Length == 0)
            {
                var related0 = IsRelatedToEx(sourceType, strippedTypes[i % strippedTypes.Length], RecursionFlags.Both, false, null, intersectionState);
                if (related0 != Ternary.False)
                {
                    result &= related0;
                    continue;
                }
            }
            var related = IsRelatedToEx(sourceType, target, RecursionFlags.Source, reportErrors, null, intersectionState);
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        return result;
    }

    // relater.go:2964-2969
    internal Type GetUndefinedStrippedTargetIfNeeded(Type source, Type target)
    {
        if ((source.flags & TypeFlags.Union) != 0 && (target.flags & TypeFlags.Union) != 0 && (source.Types()[0].flags & TypeFlags.Undefined) == 0 && (target.Types()[0].flags & TypeFlags.Undefined) != 0)
        {
            return c.ExtractTypesOfKind(target, ~TypeFlags.Undefined);
        }
        return target;
    }

    // relater.go:2971-3028
    internal Ternary TypeRelatedToSomeType(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        var targetTypes = target.Types();
        if ((target.flags & TypeFlags.Union) != 0)
        {
            if (Checker.ContainsType(targetTypes, source))
            {
                return Ternary.True;
            }
            if (relation != c.comparableRelation && (target.objectFlags & ObjectFlags.PrimitiveUnion) != 0 && (source.flags & TypeFlags.EnumLiteral) == 0 &&
                ((source.flags & (TypeFlags.StringLiteral | TypeFlags.BooleanLiteral | TypeFlags.BigIntLiteral)) != 0 ||
                    (relation == c.subtypeRelation || relation == c.strictSubtypeRelation) && (source.flags & TypeFlags.NumberLiteral) != 0))
            {
                Type? alternateForm;
                if (source == source.AsLiteralType().regularType)
                {
                    alternateForm = source.AsLiteralType().freshType;
                }
                else
                {
                    alternateForm = source.AsLiteralType().regularType;
                }
                Type? primitive = null;
                if ((source.flags & TypeFlags.StringLiteral) != 0)
                {
                    primitive = c.stringType;
                }
                else if ((source.flags & TypeFlags.NumberLiteral) != 0)
                {
                    primitive = c.numberType;
                }
                else if ((source.flags & TypeFlags.BigIntLiteral) != 0)
                {
                    primitive = c.bigintType;
                }
                if (primitive != null && Checker.ContainsType(targetTypes, primitive) || alternateForm != null && Checker.ContainsType(targetTypes, alternateForm))
                {
                    return Ternary.True;
                }
                return Ternary.False;
            }
            var match = c.GetMatchingUnionConstituentForType(target, source);
            if (match != null)
            {
                var related = IsRelatedToEx(source, match, RecursionFlags.Target, false, null, intersectionState);
                if (related != Ternary.False)
                {
                    return related;
                }
            }
        }
        foreach (var t in targetTypes)
        {
            var related = IsRelatedToEx(source, t, RecursionFlags.Target, false, null, intersectionState);
            if (related != Ternary.False)
            {
                return related;
            }
        }
        if (reportErrors)
        {
            var bestMatchingType = c.GetBestMatchingType(source, target, IsRelatedToSimple);
            if (bestMatchingType != null)
            {
                IsRelatedToEx(source, bestMatchingType, RecursionFlags.Target, true, null, intersectionState);
            }
        }
        return Ternary.False;
    }

    // relater.go:3030-3041
    internal Ternary TypeRelatedToEachType(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        var result = Ternary.True;
        var targetTypes = target.Types();
        foreach (var targetType in targetTypes)
        {
            var related = IsRelatedToEx(source, targetType, RecursionFlags.Target, reportErrors, null, intersectionState);
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        return result;
    }

    // relater.go:3043-3054
    internal Ternary EachTypeRelatedToSomeType(Type source, Type target)
    {
        var result = Ternary.True;
        var sourceTypes = source.Types();
        foreach (var sourceType in sourceTypes)
        {
            var related = TypeRelatedToSomeType(sourceType, target, false, IntersectionState.None);
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        return result;
    }

    // relater.go:3061-3167
    internal Ternary RecursiveTypeRelatedTo(Type source, Type target, bool reportErrors, IntersectionState intersectionState, RecursionFlags recursionFlags)
    {
        if (overflow)
        {
            return Ternary.False;
        }
        var (id, constrained) = Checker.GetRelationKey(source, target, intersectionState, relation == c.identityRelation, false);
        var entry = relation.Get(id);
        if (entry != RelationComparisonResult.None)
        {
            if (reportErrors && (entry & RelationComparisonResult.Failed) != 0 && (entry & RelationComparisonResult.Overflow) == 0)
            {
                // Elaborating errors and cached result is a failure not due to overflow — redo to build a message.
            }
            else
            {
                c.reliabilityFlags |= entry & (RelationComparisonResult.ReportsUnmeasurable | RelationComparisonResult.ReportsUnreliable);
                if (reportErrors && (entry & RelationComparisonResult.Overflow) != 0)
                {
                    var message = (entry & RelationComparisonResult.ComplexityOverflow) != 0
                        ? Diag.Excessive_complexity_comparing_types_0_and_1
                        : Diag.Excessive_stack_depth_comparing_types_0_and_1;
                    ReportError(message, c.TypeToString(source), c.TypeToString(target));
                }
                if ((entry & RelationComparisonResult.Succeeded) != 0)
                {
                    return Ternary.True;
                }
                return Ternary.False;
            }
        }
        if (relationCount <= 0)
        {
            overflow = true;
            return Ternary.False;
        }
        // If source and target are already being compared, consider them related with assumptions.
        if (maybeKeysSet.Contains(id))
        {
            return Ternary.Maybe;
        }
        if (constrained)
        {
            var (broadestEquivalentId, _) = Checker.GetRelationKey(source, target, intersectionState, relation == c.identityRelation, true);
            if (maybeKeysSet.Contains(broadestEquivalentId))
            {
                return Ternary.Maybe;
            }
        }
        if (sourceStack.Count == 100 || targetStack.Count == 100)
        {
            overflow = true;
            return Ternary.False;
        }
        int maybeStart = maybeKeys.Count;
        maybeKeys.Add(id);
        maybeKeysSet.Add(id);
        var saveExpandingFlags = expandingFlags;
        if ((recursionFlags & RecursionFlags.Source) != 0)
        {
            sourceStack.Add(source);
            if ((expandingFlags & ExpandingFlags.Source) == 0 && c.IsDeeplyNestedType(source, sourceStack, 3))
            {
                expandingFlags |= ExpandingFlags.Source;
            }
        }
        if ((recursionFlags & RecursionFlags.Target) != 0)
        {
            targetStack.Add(target);
            if ((expandingFlags & ExpandingFlags.Target) == 0 && c.IsDeeplyNestedType(target, targetStack, 3))
            {
                expandingFlags |= ExpandingFlags.Target;
            }
        }
        var saveReliabilityFlags = c.reliabilityFlags;
        c.reliabilityFlags = 0;
        Ternary result;
        if (expandingFlags == ExpandingFlags.Both)
        {
            result = Ternary.Maybe;
        }
        else
        {
            result = StructuredTypeRelatedTo(source, target, reportErrors, intersectionState);
        }
        var propagatingVarianceFlags = c.reliabilityFlags;
        c.reliabilityFlags |= saveReliabilityFlags;
        if ((recursionFlags & RecursionFlags.Source) != 0)
        {
            sourceStack.RemoveAt(sourceStack.Count - 1);
        }
        if ((recursionFlags & RecursionFlags.Target) != 0)
        {
            targetStack.RemoveAt(targetStack.Count - 1);
        }
        expandingFlags = saveExpandingFlags;
        if (result != Ternary.False)
        {
            if (result == Ternary.True || (sourceStack.Count == 0 && targetStack.Count == 0))
            {
                if (result == Ternary.True || result == Ternary.Maybe)
                {
                    ResetMaybeStack(maybeStart, propagatingVarianceFlags, true);
                }
                else
                {
                    ResetMaybeStack(maybeStart, propagatingVarianceFlags, false);
                }
            }
        }
        else
        {
            relation.Set(id, RelationComparisonResult.Failed | propagatingVarianceFlags);
            relationCount--;
            ResetMaybeStack(maybeStart, propagatingVarianceFlags, false);
        }
        return result;
    }

    // relater.go:3169-3178
    internal void ResetMaybeStack(int maybeStart, RelationComparisonResult propagatingVarianceFlags, bool markAllAsSucceeded)
    {
        for (int i = maybeStart; i < maybeKeys.Count; i++)
        {
            maybeKeysSet.Remove(maybeKeys[i]);
            if (markAllAsSucceeded)
            {
                relation.Set(maybeKeys[i], RelationComparisonResult.Succeeded | propagatingVarianceFlags);
                relationCount--;
            }
        }
        maybeKeys.RemoveRange(maybeStart, maybeKeys.Count - maybeStart);
    }

    // relater.go:3180-3185
    internal ErrorState GetErrorState() => new(errorChain, relatedInfo.Count);

    // relater.go:3187-3190
    internal void RestoreErrorState(ErrorState e)
    {
        errorChain = e.ErrorChain;
        if (relatedInfo.Count > e.RelatedInfoLength)
        {
            relatedInfo.RemoveRange(e.RelatedInfoLength, relatedInfo.Count - e.RelatedInfoLength);
        }
    }

    // relater.go:3192-3252
    internal Ternary StructuredTypeRelatedTo(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        var saveErrorState = GetErrorState();
        var result = StructuredTypeRelatedToWorker(source, target, reportErrors, intersectionState);
        if (relation != c.identityRelation)
        {
            if (result == Ternary.False && ((source.flags & TypeFlags.Intersection) != 0 || (source.flags & TypeFlags.TypeParameter) != 0 && (target.flags & TypeFlags.Union) != 0))
            {
                Type[] sourceTypes;
                if ((source.flags & TypeFlags.Intersection) != 0)
                {
                    sourceTypes = source.Types();
                }
                else
                {
                    sourceTypes = [source];
                }
                var constraint = c.GetEffectiveConstraintOfIntersection(sourceTypes, (target.flags & TypeFlags.Union) != 0);
                if (constraint != null && Checker.EveryType(constraint, cc => cc != source))
                {
                    result = IsRelatedToEx(constraint!, target, RecursionFlags.Source, false, null, intersectionState);
                }
            }
            if (result != Ternary.False && (intersectionState & IntersectionState.Target) == 0 && (target.flags & TypeFlags.Intersection) != 0 && !c.IsGenericObjectType(target) && (source.flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0)
            {
                result &= PropertiesRelatedTo(source, target, reportErrors, new Set<string>(), false, IntersectionState.None);
                if (result != Ternary.False && Checker.IsObjectLiteralType(source) && (source.objectFlags & ObjectFlags.FreshLiteral) != 0)
                {
                    result &= IndexSignaturesRelatedTo(source, target, false, reportErrors, IntersectionState.None);
                }
            }
            else if (result != Ternary.False && c.IsNonGenericObjectType(target) && !c.IsArrayOrTupleType(target) && IsSourceIntersectionNeedingExtraCheck(source, target))
            {
                result &= PropertiesRelatedTo(source, target, reportErrors, new Set<string>(), true, intersectionState);
            }
        }
        if (result != Ternary.False)
        {
            RestoreErrorState(saveErrorState);
        }
        return result;
    }

    // relater.go:3254-3259
    internal bool IsSourceIntersectionNeedingExtraCheck(Type source, Type target) =>
        (source.flags & TypeFlags.Intersection) != 0 && (c.GetApparentType(source).flags & TypeFlags.StructuredType) != 0 &&
        !CoreUtil.Some(source.Types(), t => t == target || (t.objectFlags & ObjectFlags.NonInferrableType) != 0);

    // relater.go:3261-3901
    internal Ternary StructuredTypeRelatedToWorker(Type source, Type target, bool reportErrors, IntersectionState intersectionState)
    {
        Ternary result = Ternary.False;
        bool varianceCheckFailed = false;
        ErrorChain? originalErrorChain = null;
        var saveErrorState = GetErrorState();

        (Ternary, bool) RelateVariances(Type[]? sourceTypeArguments, Type[]? targetTypeArguments, VarianceFlags[] variances, IntersectionState iState)
        {
            result = TypeArgumentsRelatedTo(sourceTypeArguments, targetTypeArguments, variances, reportErrors, iState);
            if (result != Ternary.False)
            {
                return (result, true);
            }
            if (CoreUtil.Some(variances, v => (v & VarianceFlags.AllowsStructuralFallback) != 0))
            {
                originalErrorChain = null;
                RestoreErrorState(saveErrorState);
                return (Ternary.False, false);
            }
            bool allowStructuralFallback = c.HasCovariantVoidArgument(targetTypeArguments!, variances);
            varianceCheckFailed = !allowStructuralFallback;
            if (variances.Length != 0 && !allowStructuralFallback)
            {
                if (varianceCheckFailed && !(reportErrors && CoreUtil.Some(variances, v => (v & VarianceFlags.VarianceMask) == VarianceFlags.Invariant)))
                {
                    return (Ternary.False, true);
                }
                originalErrorChain = errorChain;
                RestoreErrorState(saveErrorState);
            }
            return (Ternary.False, false);
        }

        if (relation == c.identityRelation)
        {
            // We've already checked that source.flags and target.flags are identical.
            if ((source.flags & TypeFlags.UnionOrIntersection) != 0)
            {
                var r = EachTypeRelatedToSomeType(source, target);
                if (r != Ternary.False)
                {
                    r &= EachTypeRelatedToSomeType(target, source);
                }
                return r;
            }
            else if ((source.flags & TypeFlags.Index) != 0)
            {
                return IsRelatedTo(source.Target(), target.Target(), RecursionFlags.Both, false);
            }
            else if ((source.flags & TypeFlags.IndexedAccess) != 0)
            {
                result = IsRelatedTo(source.AsIndexedAccessType().objectType, target.AsIndexedAccessType().objectType, RecursionFlags.Both, false);
                if (result != Ternary.False)
                {
                    result &= IsRelatedTo(source.AsIndexedAccessType().indexType, target.AsIndexedAccessType().indexType, RecursionFlags.Both, false);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
            else if ((source.flags & TypeFlags.Conditional) != 0)
            {
                if (source.AsConditionalType().root.isDistributive == target.AsConditionalType().root.isDistributive)
                {
                    result = IsRelatedTo(source.AsConditionalType().checkType!, target.AsConditionalType().checkType!, RecursionFlags.Both, false);
                    if (result != Ternary.False)
                    {
                        result &= IsRelatedTo(source.AsConditionalType().extendsType!, target.AsConditionalType().extendsType!, RecursionFlags.Both, false);
                        if (result != Ternary.False)
                        {
                            result &= IsRelatedTo(c.GetTrueTypeFromConditionalType(source), c.GetTrueTypeFromConditionalType(target), RecursionFlags.Both, false);
                            if (result != Ternary.False)
                            {
                                result &= IsRelatedTo(c.GetFalseTypeFromConditionalType(source), c.GetFalseTypeFromConditionalType(target), RecursionFlags.Both, false);
                                if (result != Ternary.False)
                                {
                                    return result;
                                }
                            }
                        }
                    }
                }
            }
            else if ((source.flags & TypeFlags.Substitution) != 0)
            {
                result = IsRelatedTo(source.AsSubstitutionType().baseType, target.AsSubstitutionType().baseType, RecursionFlags.Both, false);
                if (result != Ternary.False)
                {
                    result &= IsRelatedTo(source.AsSubstitutionType().constraint, target.AsSubstitutionType().constraint, RecursionFlags.Both, false);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
            else if ((source.flags & TypeFlags.TemplateLiteral) != 0)
            {
                if (SlicesEqualStrings(source.AsTemplateLiteralType().texts, target.AsTemplateLiteralType().texts))
                {
                    result = Ternary.True;
                    var sTypes = source.AsTemplateLiteralType().types;
                    for (int i = 0; i < sTypes.Length; i++)
                    {
                        var targetType = target.AsTemplateLiteralType().types[i];
                        result &= IsRelatedTo(sTypes[i], targetType, RecursionFlags.Both, false);
                        if (result == Ternary.False)
                        {
                            return result;
                        }
                    }
                    return result;
                }
            }
            else if ((source.flags & TypeFlags.StringMapping) != 0)
            {
                if (source.AsStringMappingType().Symbol == target.AsStringMappingType().Symbol)
                {
                    return IsRelatedTo(source.AsStringMappingType().target, target.AsStringMappingType().target, RecursionFlags.Both, false);
                }
            }
            if ((source.flags & TypeFlags.Object) == 0)
            {
                return Ternary.False;
            }
        }
        else if ((source.flags & TypeFlags.UnionOrIntersection) != 0 || (target.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            result = UnionOrIntersectionRelatedTo(source, target, reportErrors, intersectionState);
            if (result != Ternary.False)
            {
                return result;
            }
            if (!((source.flags & TypeFlags.Instantiable) != 0 ||
                (source.flags & TypeFlags.Object) != 0 && (target.flags & TypeFlags.Union) != 0 ||
                (source.flags & TypeFlags.Intersection) != 0 && (target.flags & (TypeFlags.Object | TypeFlags.Union | TypeFlags.Instantiable)) != 0))
            {
                return Ternary.False;
            }
        }

        // Alias variance probing (object and conditional types only).
        if ((source.flags & (TypeFlags.Object | TypeFlags.Conditional)) != 0 && source.alias != null && (source.alias.typeArguments?.Length ?? 0) != 0 &&
            target.alias != null && source.alias.symbol == target.alias.symbol && !(c.IsMarkerType(source) || c.IsMarkerType(target)))
        {
            var variances = c.GetAliasVariances(source.alias.symbol!);
            if (variances.Length == 0)
            {
                return Ternary.Unknown;
            }
            var @params = c.typeAliasLinks.Get(source.alias.symbol!).typeParameters;
            var minParams = c.GetMinTypeArgumentCount(@params);
            bool nodeIsInJsFile = AstUtilities.IsInJSFile(source.alias.symbol!.ValueDeclaration);
            var sourceTypes = c.FillMissingTypeArguments(source.alias.typeArguments, @params, minParams, nodeIsInJsFile);
            var targetTypes = c.FillMissingTypeArguments(target.alias.typeArguments, @params, minParams, nodeIsInJsFile);
            var (varianceResult, ok) = RelateVariances(sourceTypes, targetTypes, variances, intersectionState);
            if (ok)
            {
                return varianceResult;
            }
        }

        if (Checker.IsSingleElementGenericTupleType(source) && !source.TargetTupleType().readonlyElements)
        {
            result = IsRelatedTo(c.GetTypeArguments(source)[0], target, RecursionFlags.Source, false);
            if (result != Ternary.False)
            {
                return result;
            }
        }
        if (Checker.IsSingleElementGenericTupleType(target) && (target.TargetTupleType().readonlyElements || c.IsMutableArrayOrTuple(c.GetBaseConstraintOrType(source))))
        {
            result = IsRelatedTo(source, c.GetTypeArguments(target)[0], RecursionFlags.Target, false);
            if (result != Ternary.False)
            {
                return result;
            }
        }

        if ((target.flags & TypeFlags.TypeParameter) != 0)
        {
            if ((source.objectFlags & ObjectFlags.Mapped) != 0 && source.AsMappedType().declaration!.NameType == null && IsRelatedTo(c.GetIndexType(target), c.GetConstraintTypeFromMappedType(source), RecursionFlags.Both, false) != Ternary.False)
            {
                if ((c.GetMappedTypeModifiers(source) & MappedTypeModifiers.IncludeOptional) == 0)
                {
                    var templateType = c.GetTemplateTypeFromMappedType(source);
                    var indexedAccessType = c.GetIndexedAccessType(target, c.GetTypeParameterFromMappedType(source));
                    result = IsRelatedTo(templateType, indexedAccessType, RecursionFlags.Both, reportErrors);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
            if (relation == c.comparableRelation && (source.flags & TypeFlags.TypeParameter) != 0)
            {
                var constraint = c.GetConstraintOfTypeParameter(source);
                if (constraint != null && Checker.SomeType(constraint, cc => (cc.flags & TypeFlags.TypeParameter) != 0))
                {
                    return IsRelatedTo(constraint, target, RecursionFlags.Source, false);
                }
                return Ternary.False;
            }
        }
        else if ((target.flags & TypeFlags.IndexedAccess) != 0)
        {
            if ((source.flags & TypeFlags.IndexedAccess) != 0)
            {
                result = IsRelatedTo(source.AsIndexedAccessType().objectType, target.AsIndexedAccessType().objectType, RecursionFlags.Both, reportErrors);
                if (result != Ternary.False)
                {
                    result &= IsRelatedTo(source.AsIndexedAccessType().indexType, target.AsIndexedAccessType().indexType, RecursionFlags.Both, reportErrors);
                }
                if (result != Ternary.False)
                {
                    return result;
                }
                if (reportErrors)
                {
                    originalErrorChain = errorChain;
                }
            }
            if (relation == c.assignableRelation || relation == c.comparableRelation)
            {
                var objectType = target.AsIndexedAccessType().objectType;
                var indexType = target.AsIndexedAccessType().indexType;
                var baseObjectType = c.GetBaseConstraintOrType(objectType);
                var baseIndexType = c.GetBaseConstraintOrType(indexType);
                if (!c.IsGenericObjectType(baseObjectType) && !c.IsGenericIndexType(baseIndexType))
                {
                    var accessFlags = AccessFlags.Writing | (baseObjectType != objectType ? AccessFlags.NoIndexSignatures : AccessFlags.None);
                    var constraint = c.GetIndexedAccessTypeOrUndefined(baseObjectType, baseIndexType, accessFlags, null, null);
                    if (constraint != null)
                    {
                        if (reportErrors && originalErrorChain != null)
                        {
                            RestoreErrorState(saveErrorState);
                        }
                        result = IsRelatedToEx(source, constraint, RecursionFlags.Target, reportErrors, null, intersectionState);
                        if (result != Ternary.False)
                        {
                            return result;
                        }
                        if (reportErrors && originalErrorChain != null && errorChain != null)
                        {
                            if (Checker.ChainDepth(originalErrorChain) <= Checker.ChainDepth(errorChain))
                            {
                                errorChain = originalErrorChain;
                            }
                        }
                    }
                }
            }
            if (reportErrors)
            {
                originalErrorChain = null;
            }
        }
        else if ((target.flags & TypeFlags.Index) != 0)
        {
            var targetType = target.AsIndexType().target;
            if ((source.flags & TypeFlags.Index) != 0)
            {
                result = IsRelatedTo(targetType, source.AsIndexType().target, RecursionFlags.Both, false);
                if (result != Ternary.False)
                {
                    return result;
                }
            }
            if (Checker.IsTupleType(targetType))
            {
                result = IsRelatedTo(source, c.GetKnownKeysOfTupleType(targetType), RecursionFlags.Target, reportErrors);
                if (result != Ternary.False)
                {
                    return result;
                }
            }
            else
            {
                var constraint = c.GetSimplifiedTypeOrConstraint(targetType);
                if (constraint != null)
                {
                    if (IsRelatedTo(source, c.GetIndexTypeEx(constraint, target.AsIndexType().indexFlags | IndexFlags.NoReducibleCheck), RecursionFlags.Target, reportErrors) == Ternary.True)
                    {
                        return Ternary.True;
                    }
                }
                else if (c.IsGenericMappedType(targetType))
                {
                    var nameType = c.GetNameTypeFromMappedType(targetType);
                    var constraintType = c.GetConstraintTypeFromMappedType(targetType);
                    Type targetKeys;
                    if (nameType != null && c.IsMappedTypeWithKeyofConstraintDeclaration(targetType))
                    {
                        var mappedKeys = c.GetApparentMappedTypeKeys(nameType, targetType);
                        targetKeys = c.GetUnionType([mappedKeys, nameType]);
                    }
                    else if (nameType != null)
                    {
                        targetKeys = nameType;
                    }
                    else
                    {
                        targetKeys = constraintType;
                    }
                    if (IsRelatedTo(source, targetKeys, RecursionFlags.Target, reportErrors) == Ternary.True)
                    {
                        return Ternary.True;
                    }
                }
            }
        }
        else if ((target.flags & TypeFlags.Conditional) != 0)
        {
            if (c.IsDeeplyNestedType(target, targetStack, 10))
            {
                return Ternary.Maybe;
            }
            var ct = target.AsConditionalType();
            if (ct.root.inferTypeParameters == null && !c.IsDistributionDependent(ct.root) && !((source.flags & TypeFlags.Conditional) != 0 && source.AsConditionalType().root == ct.root))
            {
                bool skipTrue = !c.IsTypeAssignableTo(c.GetPermissiveInstantiation(ct.checkType!), c.GetPermissiveInstantiation(ct.extendsType!));
                bool skipFalse = !skipTrue && c.IsTypeAssignableTo(c.GetRestrictiveInstantiation(ct.checkType!), c.GetRestrictiveInstantiation(ct.extendsType!));
                if (skipTrue)
                {
                    result = Ternary.True;
                }
                else
                {
                    result = IsRelatedToEx(source, c.GetTrueTypeFromConditionalType(target), RecursionFlags.Target, false, null, intersectionState);
                }
                if (result != Ternary.False)
                {
                    if (skipFalse)
                    {
                        result &= Ternary.True;
                    }
                    else
                    {
                        result &= IsRelatedToEx(source, c.GetFalseTypeFromConditionalType(target), RecursionFlags.Target, false, null, intersectionState);
                    }
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
        }
        else if ((target.flags & TypeFlags.TemplateLiteral) != 0)
        {
            if ((source.flags & TypeFlags.TemplateLiteral) != 0)
            {
                if (relation == c.comparableRelation)
                {
                    if (c.TemplateLiteralTypesDefinitelyUnrelated(source.AsTemplateLiteralType(), target.AsTemplateLiteralType()))
                    {
                        return Ternary.False;
                    }
                    return Ternary.True;
                }
                c.InstantiateType(source, c.reportUnreliableMapper);
            }
            if (c.IsTypeMatchedByTemplateLiteralType(source, target.AsTemplateLiteralType(), IsRelatedToWorker))
            {
                return Ternary.True;
            }
        }
        else if ((target.flags & TypeFlags.StringMapping) != 0)
        {
            if ((source.flags & TypeFlags.StringMapping) == 0)
            {
                if (c.IsMemberOfStringMapping(source, target))
                {
                    return Ternary.True;
                }
            }
        }
        else if (c.IsGenericMappedType(target) && relation != c.identityRelation)
        {
            bool keysRemapped = target.AsMappedType().declaration!.NameType != null;
            var templateType = c.GetTemplateTypeFromMappedType(target);
            var modifiers = c.GetMappedTypeModifiers(target);
            if ((modifiers & MappedTypeModifiers.ExcludeOptional) == 0)
            {
                if (!keysRemapped && (templateType.flags & TypeFlags.IndexedAccess) != 0 && templateType.AsIndexedAccessType().objectType == source && templateType.AsIndexedAccessType().indexType == c.GetTypeParameterFromMappedType(target))
                {
                    return Ternary.True;
                }
                if (!c.IsGenericMappedType(source))
                {
                    Type targetKeys;
                    if (keysRemapped)
                    {
                        targetKeys = c.GetNameTypeFromMappedType(target)!;
                    }
                    else
                    {
                        targetKeys = c.GetConstraintTypeFromMappedType(target);
                    }
                    var sourceKeys = c.GetIndexTypeEx(source, IndexFlags.NoIndexSignatures);
                    bool includeOptional = (modifiers & MappedTypeModifiers.IncludeOptional) != 0;
                    Type? filteredByApplicability = null;
                    if (includeOptional)
                    {
                        filteredByApplicability = c.IntersectTypes(targetKeys, sourceKeys);
                    }
                    if (includeOptional && (filteredByApplicability!.flags & TypeFlags.Never) == 0 || !includeOptional && IsRelatedTo(targetKeys, sourceKeys, RecursionFlags.Both, false) != Ternary.False)
                    {
                        var templateType2 = c.GetTemplateTypeFromMappedType(target);
                        var typeParameter = c.GetTypeParameterFromMappedType(target);
                        var nonNullComponent = c.ExtractTypesOfKind(templateType2, ~TypeFlags.Nullable);
                        if (!keysRemapped && (nonNullComponent.flags & TypeFlags.IndexedAccess) != 0 && nonNullComponent.AsIndexedAccessType().indexType == typeParameter)
                        {
                            result = IsRelatedTo(source, nonNullComponent.AsIndexedAccessType().objectType, RecursionFlags.Target, reportErrors);
                            if (result != Ternary.False)
                            {
                                return result;
                            }
                        }
                        else
                        {
                            var indexingType = typeParameter;
                            if (keysRemapped)
                            {
                                indexingType = filteredByApplicability ?? targetKeys;
                            }
                            else if (filteredByApplicability != null)
                            {
                                indexingType = c.GetIntersectionType([filteredByApplicability, typeParameter]);
                            }
                            var indexedAccessType = c.GetIndexedAccessType(source, indexingType);
                            result = IsRelatedTo(indexedAccessType, templateType2, RecursionFlags.Both, reportErrors);
                            if (result != Ternary.False)
                            {
                                return result;
                            }
                        }
                    }
                    originalErrorChain = errorChain;
                    RestoreErrorState(saveErrorState);
                }
            }
        }

        if ((source.flags & TypeFlags.TypeVariable) != 0)
        {
            if ((source.flags & TypeFlags.IndexedAccess) == 0 || (target.flags & TypeFlags.IndexedAccess) == 0)
            {
                var constraint = c.GetConstraintOfType(source);
                if (constraint == null)
                {
                    constraint = c.unknownType;
                }
                result = IsRelatedToEx(constraint!, target, RecursionFlags.Source, false, null, intersectionState);
                if (result != Ternary.False)
                {
                    return result;
                }
                var constraintWithThis = c.GetTypeWithThisArgument(constraint!, source, false);
                result = IsRelatedToEx(constraintWithThis, target, RecursionFlags.Source, reportErrors && constraint != c.unknownType && (target.flags & source.flags & TypeFlags.TypeParameter) == 0, null, intersectionState);
                if (result != Ternary.False)
                {
                    return result;
                }
                if (c.IsMappedTypeGenericIndexedAccess(source))
                {
                    var indexConstraint = c.GetConstraintOfType(source.AsIndexedAccessType().indexType);
                    if (indexConstraint != null)
                    {
                        result = IsRelatedTo(c.GetIndexedAccessType(source.AsIndexedAccessType().objectType, indexConstraint), target, RecursionFlags.Source, reportErrors);
                        if (result != Ternary.False)
                        {
                            return result;
                        }
                    }
                }
            }
        }
        else if ((source.flags & TypeFlags.Index) != 0)
        {
            bool isDeferredMappedIndex = c.ShouldDeferIndexType(source.AsIndexType().target, source.AsIndexType().indexFlags) && (source.AsIndexType().target.objectFlags & ObjectFlags.Mapped) != 0;
            result = IsRelatedTo(c.stringNumberSymbolType!, target, RecursionFlags.Source, reportErrors && !isDeferredMappedIndex);
            if (result != Ternary.False)
            {
                return result;
            }
            if (isDeferredMappedIndex)
            {
                var mappedType = source.AsIndexType().target;
                var nameType = c.GetNameTypeFromMappedType(mappedType);
                Type sourceMappedKeys;
                if (nameType != null && c.IsMappedTypeWithKeyofConstraintDeclaration(mappedType))
                {
                    sourceMappedKeys = c.GetApparentMappedTypeKeys(nameType, mappedType);
                }
                else if (nameType != null)
                {
                    sourceMappedKeys = nameType;
                }
                else
                {
                    sourceMappedKeys = c.GetConstraintTypeFromMappedType(mappedType);
                }
                result = IsRelatedTo(sourceMappedKeys, target, RecursionFlags.Source, reportErrors);
                if (result != Ternary.False)
                {
                    return result;
                }
            }
        }
        else if ((source.flags & TypeFlags.Conditional) != 0)
        {
            if (c.IsDeeplyNestedType(source, sourceStack, 10))
            {
                return Ternary.Maybe;
            }
            if ((target.flags & TypeFlags.Conditional) != 0)
            {
                var sourceParams = source.AsConditionalType().root.inferTypeParameters;
                var sourceExtends = source.AsConditionalType().extendsType;
                TypeMapper? mapper = null;
                if ((sourceParams?.Length ?? 0) != 0)
                {
                    var ctx = c.NewInferenceContext(sourceParams, null, InferenceFlags.None, IsRelatedToWorker);
                    c.InferTypes(ctx.inferences, target.AsConditionalType().extendsType!, sourceExtends!, InferencePriority.NoConstraints | InferencePriority.AlwaysStrict, false);
                    sourceExtends = c.InstantiateType(sourceExtends, ctx.mapper);
                    mapper = ctx.mapper;
                }
                if (c.IsTypeIdenticalTo(sourceExtends!, target.AsConditionalType().extendsType!) && (IsRelatedTo(source.AsConditionalType().checkType!, target.AsConditionalType().checkType!, RecursionFlags.Both, false) != Ternary.False || IsRelatedTo(target.AsConditionalType().checkType!, source.AsConditionalType().checkType!, RecursionFlags.Both, false) != Ternary.False))
                {
                    result = IsRelatedTo(c.InstantiateType(c.GetTrueTypeFromConditionalType(source), mapper), c.GetTrueTypeFromConditionalType(target), RecursionFlags.Both, reportErrors);
                    if (result != Ternary.False)
                    {
                        result &= IsRelatedTo(c.GetFalseTypeFromConditionalType(source), c.GetFalseTypeFromConditionalType(target), RecursionFlags.Both, reportErrors);
                    }
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
            var defaultConstraint = c.GetDefaultConstraintOfConditionalType(source);
            if (defaultConstraint != null)
            {
                result = IsRelatedTo(defaultConstraint, target, RecursionFlags.Source, reportErrors);
                if (result != Ternary.False)
                {
                    return result;
                }
            }
            if ((target.flags & TypeFlags.Conditional) == 0 && c.HasNonCircularBaseConstraint(source))
            {
                var distributiveConstraint = c.GetConstraintOfDistributiveConditionalType(source);
                if (distributiveConstraint != null)
                {
                    RestoreErrorState(saveErrorState);
                    result = IsRelatedTo(distributiveConstraint, target, RecursionFlags.Source, reportErrors);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
        }
        else if ((source.flags & TypeFlags.TemplateLiteral) != 0 && (target.flags & TypeFlags.Object) == 0)
        {
            if ((target.flags & TypeFlags.TemplateLiteral) == 0)
            {
                var constraint = c.GetBaseConstraintOfType(source);
                if (constraint != null && constraint != source)
                {
                    result = IsRelatedTo(constraint, target, RecursionFlags.Source, reportErrors);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
        }
        else if ((source.flags & TypeFlags.StringMapping) != 0)
        {
            if ((target.flags & TypeFlags.StringMapping) != 0)
            {
                if (source.AsStringMappingType().symbol != target.AsStringMappingType().symbol)
                {
                    return Ternary.False;
                }
                result = IsRelatedTo(source.AsStringMappingType().target, target.AsStringMappingType().target, RecursionFlags.Both, reportErrors);
                if (result != Ternary.False)
                {
                    return result;
                }
            }
            else
            {
                var constraint = c.GetBaseConstraintOfType(source);
                if (constraint != null)
                {
                    result = IsRelatedTo(constraint, target, RecursionFlags.Source, reportErrors);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
            }
        }
        else
        {
            // default
            if (relation != c.subtypeRelation && relation != c.strictSubtypeRelation && c.IsPartialMappedType(target) && c.IsEmptyObjectType(source))
            {
                return Ternary.True;
            }
            if (c.IsGenericMappedType(target))
            {
                if (c.IsGenericMappedType(source))
                {
                    result = MappedTypeRelatedTo(source, target, reportErrors);
                    if (result != Ternary.False)
                    {
                        return result;
                    }
                }
                return Ternary.False;
            }
            bool sourceIsPrimitive = (source.flags & TypeFlags.Primitive) != 0;
            if (relation != c.identityRelation)
            {
                source = c.GetApparentType(source);
            }
            else if (c.IsGenericMappedType(source))
            {
                return Ternary.False;
            }
            if ((source.objectFlags & ObjectFlags.Reference) != 0 && (target.objectFlags & ObjectFlags.Reference) != 0 && source.Target() == target.Target() && !Checker.IsTupleType(source) && !c.IsMarkerType(source) && !c.IsMarkerType(target))
            {
                if (c.IsEmptyArrayLiteralType(source))
                {
                    return Ternary.True;
                }
                var variances = c.GetVariances(source.Target());
                if (variances.Length == 0)
                {
                    return Ternary.Unknown;
                }
                var (varianceResult, ok) = RelateVariances(c.GetTypeArguments(source), c.GetTypeArguments(target), variances, intersectionState);
                if (ok)
                {
                    return varianceResult;
                }
            }
            else if (c.IsArrayType(target) && (c.IsReadonlyArrayType(target) && Checker.EveryType(source, c.IsArrayOrTupleType) || Checker.EveryType(source, Checker.IsMutableTupleType)))
            {
                if (relation != c.identityRelation)
                {
                    return IsRelatedTo(c.GetIndexTypeOfTypeEx(source, c.numberType!, c.anyType!), c.GetIndexTypeOfTypeEx(target, c.numberType!, c.anyType!), RecursionFlags.Both, reportErrors);
                }
                return Ternary.False;
            }
            else if (Checker.IsGenericTupleType(source) && Checker.IsTupleType(target) && !Checker.IsGenericTupleType(target))
            {
                var constraint = c.GetBaseConstraintOrType(source);
                if (constraint != source)
                {
                    return IsRelatedTo(constraint, target, RecursionFlags.Source, reportErrors);
                }
            }
            else if ((relation == c.subtypeRelation || relation == c.strictSubtypeRelation) && c.IsEmptyObjectType(target) && (target.objectFlags & ObjectFlags.FreshLiteral) != 0 && !c.IsEmptyObjectType(source))
            {
                return Ternary.False;
            }
            if ((source.flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && (target.flags & TypeFlags.Object) != 0)
            {
                bool reportStructuralErrors = reportErrors && errorChain == saveErrorState.ErrorChain && !sourceIsPrimitive;
                result = PropertiesRelatedTo(source, target, reportStructuralErrors, new Set<string>(), false, intersectionState);
                if (result != Ternary.False)
                {
                    result &= SignaturesRelatedTo(source, target, SignatureKind.Call, reportStructuralErrors, intersectionState);
                    if (result != Ternary.False)
                    {
                        result &= SignaturesRelatedTo(source, target, SignatureKind.Construct, reportStructuralErrors, intersectionState);
                        if (result != Ternary.False)
                        {
                            result &= IndexSignaturesRelatedTo(source, target, sourceIsPrimitive, reportStructuralErrors, intersectionState);
                        }
                    }
                }
                if (result != Ternary.False)
                {
                    if (!varianceCheckFailed)
                    {
                        return result;
                    }
                    if (originalErrorChain != null)
                    {
                        errorChain = originalErrorChain;
                    }
                    else if (errorChain == null)
                    {
                        errorChain = saveErrorState.ErrorChain;
                    }
                }
            }
            if ((source.flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0 && (target.flags & TypeFlags.Union) != 0)
            {
                var objectOnlyTarget = c.ExtractTypesOfKind(target, TypeFlags.Object | TypeFlags.Intersection | TypeFlags.Substitution);
                if ((objectOnlyTarget.flags & TypeFlags.Union) != 0)
                {
                    var r = TypeRelatedToDiscriminatedType(source, objectOnlyTarget);
                    if (r != Ternary.False)
                    {
                        return r;
                    }
                }
            }
        }
        return Ternary.False;
    }

    // relater.go:3903-3967
    internal Ternary TypeArgumentsRelatedTo(Type[]? sources, Type[]? targets, VarianceFlags[] variances, bool reportErrors, IntersectionState intersectionState)
    {
        sources ??= [];
        targets ??= [];
        if (sources.Length != targets.Length && relation == c.identityRelation)
        {
            return Ternary.False;
        }
        int length = Math.Min(sources.Length, targets.Length);
        var result = Ternary.True;
        for (int i = 0; i < length; i++)
        {
            var varianceFlags = VarianceFlags.Covariant;
            if (i < variances.Length)
            {
                varianceFlags = variances[i];
            }
            var variance = varianceFlags & VarianceFlags.VarianceMask;
            if (variance != VarianceFlags.Independent)
            {
                var s = sources[i];
                var t = targets[i];
                Ternary related;
                if ((varianceFlags & VarianceFlags.Unmeasurable) != 0)
                {
                    if (relation == c.identityRelation)
                    {
                        related = IsRelatedTo(s, t, RecursionFlags.Both, false);
                    }
                    else
                    {
                        related = c.CompareTypesIdentical(s, t);
                    }
                }
                else
                {
                    if (c.inVarianceComputation && (varianceFlags & VarianceFlags.Unreliable) != 0)
                    {
                        c.InstantiateType(s, c.reportUnreliableMapper);
                    }
                    if (variance == VarianceFlags.Covariant)
                    {
                        related = IsRelatedToEx(s, t, RecursionFlags.Both, reportErrors, null, intersectionState);
                    }
                    else if (variance == VarianceFlags.Contravariant)
                    {
                        related = IsRelatedToEx(t, s, RecursionFlags.Both, reportErrors, null, intersectionState);
                    }
                    else if (variance == VarianceFlags.Bivariant)
                    {
                        related = IsRelatedTo(t, s, RecursionFlags.Both, false);
                        if (related == Ternary.False)
                        {
                            related = IsRelatedToEx(s, t, RecursionFlags.Both, reportErrors, null, intersectionState);
                        }
                    }
                    else
                    {
                        related = IsRelatedToEx(s, t, RecursionFlags.Both, reportErrors, null, intersectionState);
                        if (related != Ternary.False)
                        {
                            related &= IsRelatedToEx(t, s, RecursionFlags.Both, reportErrors, null, intersectionState);
                        }
                    }
                }
                if (related == Ternary.False)
                {
                    return Ternary.False;
                }
                result &= related;
            }
        }
        return result;
    }

    // relater.go:3972-3987
    internal Ternary MappedTypeRelatedTo(Type source, Type target, bool reportErrors)
    {
        bool modifiersRelated = relation == c.comparableRelation ||
            relation == c.identityRelation && c.GetMappedTypeModifiers(source) == c.GetMappedTypeModifiers(target) ||
            relation != c.identityRelation && c.GetCombinedMappedTypeOptionality(source) <= c.GetCombinedMappedTypeOptionality(target);
        if (modifiersRelated)
        {
            var targetConstraint = c.GetConstraintTypeFromMappedType(target);
            var sourceConstraint = c.InstantiateType(c.GetConstraintTypeFromMappedType(source), c.GetCombinedMappedTypeOptionality(source) < 0 ? c.reportUnmeasurableMapper : c.reportUnreliableMapper);
            var result = IsRelatedTo(targetConstraint, sourceConstraint, RecursionFlags.Both, reportErrors);
            if (result != Ternary.False)
            {
                var mapper = TypeMapper.NewSimpleTypeMapper(c.GetTypeParameterFromMappedType(source), c.GetTypeParameterFromMappedType(target));
                if (c.InstantiateType(c.GetNameTypeFromMappedType(source), mapper) == c.InstantiateType(c.GetNameTypeFromMappedType(target), mapper))
                {
                    return result & IsRelatedTo(c.InstantiateType(c.GetTemplateTypeFromMappedType(source), mapper), c.GetTemplateTypeFromMappedType(target), RecursionFlags.Both, reportErrors);
                }
            }
        }
        return Ternary.False;
    }

    // relater.go:3989-4098
    internal Ternary TypeRelatedToDiscriminatedType(Type source, Type target)
    {
        var sourceProperties = c.GetPropertiesOfType(source);
        var sourcePropertiesFiltered = c.FindDiscriminantProperties(sourceProperties, target);
        if (sourcePropertiesFiltered.Length == 0)
        {
            return Ternary.False;
        }
        int numCombinations = 1;
        foreach (var sourceProperty in sourcePropertiesFiltered)
        {
            numCombinations *= Checker.CountTypes(c.GetNonMissingTypeOfSymbol(sourceProperty));
            if (numCombinations > 25)
            {
                return Ternary.False;
            }
            if (numCombinations == 0)
            {
                return Ternary.False;
            }
        }
        var sourceDiscriminantTypes = new Type[sourcePropertiesFiltered.Length][];
        var excludedProperties = new Set<string>();
        for (int i = 0; i < sourcePropertiesFiltered.Length; i++)
        {
            var sourceProperty = sourcePropertiesFiltered[i];
            var sourcePropertyType = c.GetNonMissingTypeOfSymbol(sourceProperty);
            sourceDiscriminantTypes[i] = sourcePropertyType.Distributed();
            excludedProperties.Add(sourceProperty.Name);
        }
        var discriminantCombinations = new Type[numCombinations][];
        for (int i = 0; i < numCombinations; i++)
        {
            var combination = new Type[sourceDiscriminantTypes.Length];
            int n = i;
            for (int j = sourceDiscriminantTypes.Length - 1; j >= 0; j--)
            {
                var sourceTypes = sourceDiscriminantTypes[j];
                int length = sourceTypes.Length;
                combination[j] = sourceTypes[n % length];
                n = n / length;
            }
            discriminantCombinations[i] = combination;
        }
        List<Type> matchingTypes = [];
        foreach (var combination in discriminantCombinations)
        {
            bool hasMatch = false;
            foreach (var t in target.Types())
            {
                bool continueOuter = false;
                for (int i = 0; i < sourcePropertiesFiltered.Length; i++)
                {
                    var sourceProperty = sourcePropertiesFiltered[i];
                    var targetProperty = c.GetPropertyOfType(t, sourceProperty.Name);
                    if (targetProperty == null)
                    {
                        continueOuter = true;
                        break;
                    }
                    if (sourceProperty == targetProperty)
                    {
                        continue;
                    }
                    int captured = i;
                    var related = PropertyRelatedTo(source, target, sourceProperty, targetProperty, _ => combination[captured],
                        false, IntersectionState.None, c.strictNullChecks || relation == c.comparableRelation);
                    if (related == Ternary.False)
                    {
                        continueOuter = true;
                        break;
                    }
                }
                if (continueOuter)
                {
                    continue;
                }
                matchingTypes = new System.Collections.Generic.List<Type>(CoreUtil.AppendIfUnique(matchingTypes.ToArray(), t));
                hasMatch = true;
            }
            if (!hasMatch)
            {
                return Ternary.False;
            }
        }
        var result = Ternary.True;
        foreach (var t in matchingTypes)
        {
            result &= PropertiesRelatedTo(source, t, false, excludedProperties, false, IntersectionState.None);
            if (result != Ternary.False)
            {
                result &= SignaturesRelatedTo(source, t, SignatureKind.Call, false, IntersectionState.None);
                if (result != Ternary.False)
                {
                    result &= SignaturesRelatedTo(source, t, SignatureKind.Construct, false, IntersectionState.None);
                    if (result != Ternary.False && !(Checker.IsTupleType(source) && Checker.IsTupleType(t)))
                    {
                        result &= IndexSignaturesRelatedTo(source, t, false, false, IntersectionState.None);
                    }
                }
            }
            if (result == Ternary.False)
            {
                return result;
            }
        }
        return result;
    }

    // relater.go:4100-4261
    internal Ternary PropertiesRelatedTo(Type source, Type target, bool reportErrors, Set<string> excludedProperties, bool optionalsOnly, IntersectionState intersectionState)
    {
        if (relation == c.identityRelation)
        {
            return PropertiesIdenticalTo(source, target, excludedProperties);
        }
        var result = Ternary.True;
        if (Checker.IsTupleType(target))
        {
            if (c.IsArrayOrTupleType(source))
            {
                if (!target.TargetTupleType().readonlyElements && (c.IsReadonlyArrayType(source) || Checker.IsTupleType(source) && source.TargetTupleType().readonlyElements))
                {
                    return Ternary.False;
                }
                int sourceArity = c.GetTypeReferenceArity(source);
                int targetArity = c.GetTypeReferenceArity(target);
                bool sourceRest;
                if (Checker.IsTupleType(source))
                {
                    sourceRest = (source.TargetTupleType().combinedFlags & ElementFlags.Rest) != 0;
                }
                else
                {
                    sourceRest = true;
                }
                bool targetHasRestElement = (target.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0;
                int sourceMinLength;
                if (Checker.IsTupleType(source))
                {
                    sourceMinLength = source.TargetTupleType().minLength;
                }
                else
                {
                    sourceMinLength = 0;
                }
                int targetMinLength = target.TargetTupleType().minLength;
                if (!sourceRest && sourceArity < targetMinLength)
                {
                    if (reportErrors)
                    {
                        ReportError(Diag.Source_has_0_element_s_but_target_requires_1, sourceArity, targetMinLength);
                    }
                    return Ternary.False;
                }
                if (!targetHasRestElement && targetArity < sourceMinLength)
                {
                    if (reportErrors)
                    {
                        ReportError(Diag.Source_has_0_element_s_but_target_allows_only_1, sourceMinLength, targetArity);
                    }
                    return Ternary.False;
                }
                if (!targetHasRestElement && (sourceRest || targetArity < sourceArity))
                {
                    if (reportErrors)
                    {
                        if (sourceMinLength < targetMinLength)
                        {
                            ReportError(Diag.Target_requires_0_element_s_but_source_may_have_fewer, targetMinLength);
                        }
                        else
                        {
                            ReportError(Diag.Target_allows_only_0_element_s_but_source_may_have_more, targetArity);
                        }
                    }
                    return Ternary.False;
                }
                var sourceTypeArguments = c.GetTypeArguments(source);
                var targetTypeArguments = c.GetTypeArguments(target);
                int targetStartCount = Checker.GetStartElementCount(target.TargetTupleType(), ElementFlags.NonRest);
                int targetEndCount = Checker.GetEndElementCount(target.TargetTupleType(), ElementFlags.NonRest);
                bool canExcludeDiscriminants = excludedProperties.Len() != 0;
                for (int sourcePosition = 0; sourcePosition < sourceArity; sourcePosition++)
                {
                    ElementFlags sourceFlags;
                    if (Checker.IsTupleType(source))
                    {
                        sourceFlags = source.TargetTupleType().elementInfos![sourcePosition].flags;
                    }
                    else
                    {
                        sourceFlags = ElementFlags.Rest;
                    }
                    int sourcePositionFromEnd = sourceArity - 1 - sourcePosition;
                    int targetPosition;
                    if (targetHasRestElement && sourcePosition >= targetStartCount)
                    {
                        targetPosition = targetArity - 1 - Math.Min(sourcePositionFromEnd, targetEndCount);
                    }
                    else
                    {
                        targetPosition = sourcePosition;
                    }
                    var targetFlags = ElementFlags.None;
                    if (targetPosition >= 0)
                    {
                        targetFlags = target.TargetTupleType().elementInfos![targetPosition].flags;
                    }
                    if ((targetFlags & ElementFlags.Variadic) != 0 && (sourceFlags & ElementFlags.Variadic) == 0)
                    {
                        if (reportErrors)
                        {
                            ReportError(Diag.Source_provides_no_match_for_variadic_element_at_position_0_in_target, targetPosition);
                        }
                        return Ternary.False;
                    }
                    if ((sourceFlags & ElementFlags.Variadic) != 0 && (targetFlags & ElementFlags.Variable) == 0)
                    {
                        if (reportErrors)
                        {
                            ReportError(Diag.Variadic_element_at_position_0_in_source_does_not_match_element_at_position_1_in_target, sourcePosition, targetPosition);
                        }
                        return Ternary.False;
                    }
                    if ((targetFlags & ElementFlags.Required) != 0 && (sourceFlags & ElementFlags.Required) == 0)
                    {
                        if (reportErrors)
                        {
                            ReportError(Diag.Source_provides_no_match_for_required_element_at_position_0_in_target, targetPosition);
                        }
                        return Ternary.False;
                    }
                    if (canExcludeDiscriminants)
                    {
                        if ((sourceFlags & ElementFlags.Variable) != 0 || (targetFlags & ElementFlags.Variable) != 0)
                        {
                            canExcludeDiscriminants = false;
                        }
                        if (canExcludeDiscriminants && excludedProperties.Has(sourcePosition.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                        {
                            continue;
                        }
                    }
                    var sourceType = c.RemoveMissingType(sourceTypeArguments[sourcePosition], (sourceFlags & targetFlags & ElementFlags.Optional) != 0);
                    var targetType = targetTypeArguments[targetPosition];
                    Type targetCheckType;
                    if ((sourceFlags & ElementFlags.Variadic) != 0 && (targetFlags & ElementFlags.Rest) != 0)
                    {
                        targetCheckType = c.CreateArrayType(targetType);
                    }
                    else
                    {
                        targetCheckType = c.RemoveMissingType(targetType, (targetFlags & ElementFlags.Optional) != 0);
                    }
                    var related = IsRelatedToEx(sourceType, targetCheckType, RecursionFlags.Both, reportErrors, null, intersectionState);
                    if (related == Ternary.False)
                    {
                        if (reportErrors && (targetArity > 1 || sourceArity > 1))
                        {
                            if (targetHasRestElement && sourcePosition >= targetStartCount && sourcePositionFromEnd >= targetEndCount && targetStartCount != sourceArity - targetEndCount - 1)
                            {
                                ReportError(Diag.Type_at_positions_0_through_1_in_source_is_not_compatible_with_type_at_position_2_in_target, targetStartCount, sourceArity - targetEndCount - 1, targetPosition);
                            }
                            else
                            {
                                ReportError(Diag.Type_at_position_0_in_source_is_not_compatible_with_type_at_position_1_in_target, sourcePosition, targetPosition);
                            }
                        }
                        return Ternary.False;
                    }
                    result &= related;
                }
                return result;
            }
            if ((target.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0)
            {
                return Ternary.False;
            }
        }
        bool requireOptionalProperties = (relation == c.subtypeRelation || relation == c.strictSubtypeRelation) && !Checker.IsObjectLiteralType(source) && !c.IsEmptyArrayLiteralType(source) && !Checker.IsTupleType(source);
        var unmatchedProperty = c.GetUnmatchedProperty(source, target, requireOptionalProperties, false);
        if (unmatchedProperty != null)
        {
            if (reportErrors && c.ShouldReportUnmatchedPropertyError(source, target))
            {
                ReportUnmatchedProperty(source, target, unmatchedProperty, requireOptionalProperties);
            }
            return Ternary.False;
        }
        if (Checker.IsObjectLiteralType(target))
        {
            foreach (var sourceProp in Checker.ExcludeProperties(c.GetPropertiesOfType(source), excludedProperties))
            {
                if (c.GetPropertyOfObjectType(target, sourceProp.Name) == null)
                {
                    if (reportErrors)
                    {
                        ReportError(Diag.Property_0_does_not_exist_on_type_1, c.SymbolToString(sourceProp), c.TypeToString(target));
                    }
                    return Ternary.False;
                }
            }
        }
        var properties = c.GetPropertiesOfType(target);
        bool numericNamesOnly = Checker.IsTupleType(source) && Checker.IsTupleType(target);
        foreach (var targetProp in Checker.ExcludeProperties(properties, excludedProperties))
        {
            var name = targetProp.Name;
            if ((targetProp.Flags & SymbolFlags.Prototype) == 0 && (!numericNamesOnly || Checker.IsNumericLiteralName(name) || name == "length") && (!optionalsOnly || (targetProp.Flags & SymbolFlags.Optional) != 0))
            {
                var sourceProp = c.GetPropertyOfType(source, name);
                if (sourceProp != null && sourceProp != targetProp)
                {
                    var related = PropertyRelatedTo(source, target, sourceProp, targetProp, c.GetNonMissingTypeOfSymbol, reportErrors, intersectionState, relation == c.comparableRelation);
                    if (related == Ternary.False)
                    {
                        return Ternary.False;
                    }
                    result &= related;
                }
            }
        }
        return result;
    }

    // relater.go:4263-4325
    internal Ternary PropertyRelatedTo(Type source, Type target, Symbol sourceProp, Symbol targetProp, Func<Symbol, Type> getTypeOfSourceProperty, bool reportErrors, IntersectionState intersectionState, bool skipOptional)
    {
        var sourcePropFlags = c.GetDeclarationModifierFlagsFromSymbol(sourceProp);
        var targetPropFlags = c.GetDeclarationModifierFlagsFromSymbol(targetProp);
        if ((sourcePropFlags & ModifierFlags.Private) != 0 || (targetPropFlags & ModifierFlags.Private) != 0)
        {
            if (sourceProp.ValueDeclaration != targetProp.ValueDeclaration)
            {
                if (reportErrors)
                {
                    if ((sourcePropFlags & ModifierFlags.Private) != 0 && (targetPropFlags & ModifierFlags.Private) != 0)
                    {
                        ReportError(Diag.Types_have_separate_declarations_of_a_private_property_0, c.SymbolToString(targetProp));
                    }
                    else
                    {
                        ReportError(Diag.Property_0_is_private_in_type_1_but_not_in_type_2, c.SymbolToString(targetProp), c.TypeToString((sourcePropFlags & ModifierFlags.Private) != 0 ? source : target), c.TypeToString((sourcePropFlags & ModifierFlags.Private) != 0 ? target : source));
                    }
                }
                return Ternary.False;
            }
        }
        else if ((targetPropFlags & ModifierFlags.Protected) != 0)
        {
            if (!c.IsValidOverrideOf(sourceProp, targetProp))
            {
                if (reportErrors)
                {
                    var sourceType = c.GetDeclaringClass(sourceProp) ?? source;
                    var targetType = c.GetDeclaringClass(targetProp) ?? target;
                    ReportError(Diag.Property_0_is_protected_but_type_1_is_not_a_class_derived_from_2, c.SymbolToString(targetProp), c.TypeToString(sourceType), c.TypeToString(targetType));
                }
                return Ternary.False;
            }
        }
        else if ((sourcePropFlags & ModifierFlags.Protected) != 0)
        {
            if (reportErrors)
            {
                ReportError(Diag.Property_0_is_protected_in_type_1_but_public_in_type_2, c.SymbolToString(targetProp), c.TypeToString(source), c.TypeToString(target));
            }
            return Ternary.False;
        }
        if (relation == c.strictSubtypeRelation && c.IsReadonlySymbol(sourceProp) && !c.IsReadonlySymbol(targetProp))
        {
            return Ternary.False;
        }
        var related = IsPropertySymbolTypeRelated(sourceProp, targetProp, getTypeOfSourceProperty, reportErrors, intersectionState);
        if (related == Ternary.False)
        {
            if (reportErrors)
            {
                ReportError(Diag.Types_of_property_0_are_incompatible, c.SymbolToString(targetProp));
            }
            return Ternary.False;
        }
        if (!skipOptional && (sourceProp.Flags & SymbolFlags.Optional) != 0 && (targetProp.Flags & SymbolFlags.ClassMember) != 0 && (targetProp.Flags & SymbolFlags.Optional) == 0)
        {
            if (reportErrors)
            {
                ReportError(Diag.Property_0_is_optional_in_type_1_but_required_in_type_2, c.SymbolToString(targetProp), c.TypeToString(source), c.TypeToString(target));
            }
            return Ternary.False;
        }
        return related;
    }

    // relater.go:4327-4336
    internal Ternary IsPropertySymbolTypeRelated(Symbol sourceProp, Symbol targetProp, Func<Symbol, Type> getTypeOfSourceProperty, bool reportErrors, IntersectionState intersectionState)
    {
        bool targetIsOptional = c.strictNullChecks && (targetProp.CheckFlags & CheckFlags.Partial) != 0;
        var effectiveTarget = c.AddOptionalityEx(c.GetNonMissingTypeOfSymbol(targetProp), false, targetIsOptional);
        if ((effectiveTarget.flags & (relation == c.strictSubtypeRelation ? TypeFlags.Any : TypeFlags.AnyOrUnknown)) != 0)
        {
            return Ternary.True;
        }
        var effectiveSource = getTypeOfSourceProperty(sourceProp);
        return IsRelatedToEx(effectiveSource, effectiveTarget, RecursionFlags.Both, reportErrors, null, intersectionState);
    }

    // relater.go:4338-4370
    internal void ReportUnmatchedProperty(Type source, Type target, Symbol unmatchedProperty, bool requireOptionalProperties)
    {
        if (unmatchedProperty.ValueDeclaration != null &&
            unmatchedProperty.ValueDeclaration.Name() != null &&
            NodeTests.IsPrivateIdentifier(unmatchedProperty.ValueDeclaration.Name()!) &&
            source.symbol != null &&
            (source.symbol.Flags & SymbolFlags.Class) != 0)
        {
            var privateIdentifierDescription = unmatchedProperty.ValueDeclaration.Name()!.Text();
            var symbolTableKey = global::Ts.Binder.Binder.GetSymbolNameForPrivateIdentifier(source.symbol, privateIdentifierDescription);
            if (c.GetPropertyOfType(source, symbolTableKey) != null)
            {
                ReportError(Diag.Property_0_in_type_1_refers_to_a_different_member_that_cannot_be_accessed_from_within_type_2, privateIdentifierDescription, c.SymbolToString(source.symbol), c.SymbolToString(target.symbol!));
                return;
            }
        }
        var props = c.GetUnmatchedProperties(source, target, requireOptionalProperties, false);
        if (props.Length == 1)
        {
            var (sourceType, targetType) = c.GetTypeNamesForErrorDisplay(source, target);
            var propName = c.SymbolToString(unmatchedProperty);
            ReportError(Diag.Property_0_is_missing_in_type_1_but_required_in_type_2, propName, sourceType, targetType);
            if ((unmatchedProperty.Declarations?.Length ?? 0) != 0)
            {
                relatedInfo.Add(c.MakeDiagnosticForNode(unmatchedProperty.Declarations![0], Diag.X_0_is_declared_here, propName));
            }
        }
        else if (TryElaborateArrayLikeErrors(source, target, false))
        {
            var (sourceType, targetType) = c.GetTypeNamesForErrorDisplay(source, target);
            if (props.Length > 5)
            {
                var propNames = string.Join(", ", CoreUtil.Map(props[..4], s => c.SymbolToString(s))!);
                ReportError(Diag.Type_0_is_missing_the_following_properties_from_type_1_Colon_2_and_3_more, sourceType, targetType, propNames, props.Length - 4);
            }
            else
            {
                var propNames = string.Join(", ", CoreUtil.Map(props, s => c.SymbolToString(s))!);
                ReportError(Diag.Type_0_is_missing_the_following_properties_from_type_1_Colon_2, sourceType, targetType, propNames);
            }
        }
    }

    // relater.go:4372-4399
    internal bool TryElaborateArrayLikeErrors(Type source, Type target, bool reportErrors)
    {
        if (Checker.IsTupleType(source))
        {
            if (source.TargetTupleType().readonlyElements && c.IsMutableArrayOrTuple(target))
            {
                if (reportErrors)
                {
                    ReportError(Diag.The_type_0_is_readonly_and_cannot_be_assigned_to_the_mutable_type_1, c.TypeToString(source), c.TypeToString(target));
                }
                return false;
            }
            return c.IsArrayOrTupleType(target);
        }
        if (c.IsReadonlyArrayType(source) && c.IsMutableArrayOrTuple(target))
        {
            if (reportErrors)
            {
                ReportError(Diag.The_type_0_is_readonly_and_cannot_be_assigned_to_the_mutable_type_1, c.TypeToString(source), c.TypeToString(target));
            }
            return false;
        }
        if (Checker.IsTupleType(target))
        {
            return c.IsArrayType(source);
        }
        return true;
    }

    // relater.go:4401-4408
    internal void TryElaborateErrorsForPrimitivesAndObjects(Type source, Type target)
    {
        if ((source == c.globalStringType && target == c.stringType) ||
            (source == c.globalNumberType && target == c.numberType) ||
            (source == c.globalBooleanType && target == c.booleanType) ||
            (source == c.GetGlobalESSymbolType() && target == c.esSymbolType))
        {
            ReportError(Diag.X_0_is_a_primitive_but_1_is_a_wrapper_object_Prefer_using_0_when_possible, c.TypeToString(target), c.TypeToString(source));
        }
    }

    // relater.go:4410-4432
    internal Ternary PropertiesIdenticalTo(Type source, Type target, Set<string> excludedProperties)
    {
        if ((source.flags & TypeFlags.Object) == 0 || (target.flags & TypeFlags.Object) == 0)
        {
            return Ternary.False;
        }
        var sourceProperties = Checker.ExcludeProperties(c.GetPropertiesOfObjectType(source), excludedProperties);
        var targetProperties = Checker.ExcludeProperties(c.GetPropertiesOfObjectType(target), excludedProperties);
        if (sourceProperties.Length != targetProperties.Length)
        {
            return Ternary.False;
        }
        var result = Ternary.True;
        foreach (var sourceProp in sourceProperties)
        {
            var targetProp = c.GetPropertyOfObjectType(target, sourceProp.Name);
            if (targetProp == null)
            {
                return Ternary.False;
            }
            var related = c.CompareProperties(sourceProp, targetProp, IsRelatedToSimple);
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        return result;
    }

    // relater.go:4434-4509
    internal Ternary SignaturesRelatedTo(Type source, Type target, SignatureKind kind, bool reportErrors, IntersectionState intersectionState)
    {
        if (relation == c.identityRelation)
        {
            return SignaturesIdenticalTo(source, target, kind);
        }
        if (source == c.anyFunctionType)
        {
            return Ternary.True;
        }
        if (target == c.anyFunctionType)
        {
            return Ternary.False;
        }
        var sourceSignatures = c.GetSignaturesOfType(source, kind);
        var targetSignatures = c.GetSignaturesOfType(target, kind);
        if (kind == SignatureKind.Construct && sourceSignatures.Length != 0 && targetSignatures.Length != 0)
        {
            bool sourceIsAbstract = (sourceSignatures[0].flags & SignatureFlags.Abstract) != 0;
            bool targetIsAbstract = (targetSignatures[0].flags & SignatureFlags.Abstract) != 0;
            if (sourceIsAbstract && !targetIsAbstract)
            {
                if (reportErrors)
                {
                    ReportError(Diag.Cannot_assign_an_abstract_constructor_type_to_a_non_abstract_constructor_type);
                }
                return Ternary.False;
            }
            if (!ConstructorVisibilitiesAreCompatible(sourceSignatures[0], targetSignatures[0], reportErrors))
            {
                return Ternary.False;
            }
        }
        var result = Ternary.True;
        if ((source.objectFlags & ObjectFlags.Instantiated) != 0 && (target.objectFlags & ObjectFlags.Instantiated) != 0 && source.symbol == target.symbol ||
            (source.objectFlags & ObjectFlags.Reference) != 0 && (target.objectFlags & ObjectFlags.Reference) != 0 && source.Target() == target.Target())
        {
            for (int i = 0; i < targetSignatures.Length; i++)
            {
                var related = SignatureRelatedTo(sourceSignatures[i], targetSignatures[i], true, reportErrors, intersectionState);
                if (related == Ternary.False)
                {
                    return Ternary.False;
                }
                result &= related;
            }
        }
        else if (sourceSignatures.Length == 1 && targetSignatures.Length == 1)
        {
            bool eraseGenerics = relation == c.comparableRelation;
            result = SignatureRelatedTo(sourceSignatures[0], targetSignatures[0], eraseGenerics, reportErrors, intersectionState);
        }
        else
        {
            foreach (var t in targetSignatures)
            {
                var saveErrorState = GetErrorState();
                bool shouldElaborateErrors = reportErrors;
                bool matched = false;
                foreach (var s in sourceSignatures)
                {
                    var related = SignatureRelatedTo(s, t, true, shouldElaborateErrors, intersectionState);
                    if (related != Ternary.False)
                    {
                        result &= related;
                        RestoreErrorState(saveErrorState);
                        matched = true;
                        break;
                    }
                    shouldElaborateErrors = false;
                }
                if (matched)
                {
                    continue;
                }
                if (shouldElaborateErrors)
                {
                    ReportError(Diag.Type_0_provides_no_match_for_the_signature_1, c.TypeToString(source), c.SignatureToString(t));
                }
                return Ternary.False;
            }
        }
        return result;
    }

    // relater.go:4511-4533
    internal bool ConstructorVisibilitiesAreCompatible(Signature sourceSignature, Signature targetSignature, bool reportErrors)
    {
        if (sourceSignature.declaration == null || targetSignature.declaration == null)
        {
            return true;
        }
        var sourceAccessibility = sourceSignature.declaration.ModifierFlags() & ModifierFlags.NonPublicAccessibilityModifier;
        var targetAccessibility = targetSignature.declaration.ModifierFlags() & ModifierFlags.NonPublicAccessibilityModifier;
        if (targetAccessibility == ModifierFlags.Private)
        {
            return true;
        }
        if (targetAccessibility == ModifierFlags.Protected && sourceAccessibility != ModifierFlags.Private)
        {
            return true;
        }
        if (targetAccessibility != ModifierFlags.Protected && sourceAccessibility == 0)
        {
            return true;
        }
        if (reportErrors)
        {
            ReportError(Diag.Cannot_assign_a_0_constructor_type_to_a_1_constructor_type, Checker.VisibilityToString(sourceAccessibility), Checker.VisibilityToString(targetAccessibility));
        }
        return false;
    }

    // relater.go:4536-4552
    internal Ternary SignatureRelatedTo(Signature source, Signature target, bool erase, bool reportErrors, IntersectionState intersectionState)
    {
        var checkMode = SignatureCheckMode.None;
        if (relation == c.subtypeRelation)
        {
            checkMode = SignatureCheckMode.StrictTopSignature;
        }
        else if (relation == c.strictSubtypeRelation)
        {
            checkMode = SignatureCheckMode.StrictTopSignature | SignatureCheckMode.StrictArity;
        }
        if (erase)
        {
            source = c.GetErasedSignature(source);
            target = c.GetErasedSignature(target);
        }
        Ternary IsRelatedToWorkerLocal(Type s, Type t, bool re) => IsRelatedToEx(s, t, RecursionFlags.Both, re, null, intersectionState);
        return c.CompareSignaturesRelated(source, target, checkMode, reportErrors, ReportError, IsRelatedToWorkerLocal, c.reportUnreliableMapper);
    }

    // relater.go:4554-4569
    internal Ternary SignaturesIdenticalTo(Type source, Type target, SignatureKind kind)
    {
        var sourceSignatures = c.GetSignaturesOfType(source, kind);
        var targetSignatures = c.GetSignaturesOfType(target, kind);
        if (sourceSignatures.Length != targetSignatures.Length)
        {
            return Ternary.False;
        }
        var result = Ternary.True;
        for (int i = 0; i < sourceSignatures.Length; i++)
        {
            var related = c.CompareSignaturesIdentical(sourceSignatures[i], targetSignatures[i], false, false, false, IsRelatedToSimple);
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        return result;
    }

    // relater.go:4571-4594
    internal Ternary IndexSignaturesRelatedTo(Type source, Type target, bool sourceIsPrimitive, bool reportErrors, IntersectionState intersectionState)
    {
        if (relation == c.identityRelation)
        {
            return IndexSignaturesIdenticalTo(source, target);
        }
        var indexInfos = c.GetIndexInfosOfType(target);
        bool targetHasStringIndex = CoreUtil.Some(indexInfos, info => info.keyType == c.stringType);
        var result = Ternary.True;
        foreach (var targetInfo in indexInfos)
        {
            Ternary related;
            if (relation != c.strictSubtypeRelation && !sourceIsPrimitive && targetHasStringIndex && (targetInfo.valueType.flags & TypeFlags.Any) != 0)
            {
                related = Ternary.True;
            }
            else if (c.IsGenericMappedType(source) && targetHasStringIndex)
            {
                related = IsRelatedTo(c.GetTemplateTypeFromMappedType(source), targetInfo.valueType, RecursionFlags.Both, reportErrors);
            }
            else
            {
                related = TypeRelatedToIndexInfo(source, targetInfo, reportErrors, intersectionState);
            }
            if (related == Ternary.False)
            {
                return Ternary.False;
            }
            result &= related;
        }
        return result;
    }

    // relater.go:4596-4611
    internal Ternary TypeRelatedToIndexInfo(Type source, IndexInfo targetInfo, bool reportErrors, IntersectionState intersectionState)
    {
        var sourceInfo = c.GetApplicableIndexInfo(source, targetInfo.keyType);
        if (sourceInfo != null)
        {
            return IndexInfoRelatedTo(sourceInfo, targetInfo, reportErrors, intersectionState);
        }
        if ((intersectionState & IntersectionState.Source) == 0 && (relation != c.strictSubtypeRelation || (source.objectFlags & ObjectFlags.FreshLiteral) != 0) && c.IsObjectTypeWithInferableIndex(source))
        {
            return MembersRelatedToIndexInfo(source, targetInfo, reportErrors, intersectionState);
        }
        if (reportErrors)
        {
            ReportError(Diag.Index_signature_for_type_0_is_missing_in_type_1, c.TypeToString(targetInfo.keyType), c.TypeToString(source));
        }
        return Ternary.False;
    }

    // relater.go:4627-4669
    internal Ternary MembersRelatedToIndexInfo(Type source, IndexInfo targetInfo, bool reportErrors, IntersectionState intersectionState)
    {
        var result = Ternary.True;
        var keyType = targetInfo.keyType;
        Symbol[] props;
        if ((source.flags & TypeFlags.Intersection) != 0)
        {
            props = c.GetPropertiesOfUnionOrIntersectionType(source);
        }
        else
        {
            props = c.GetPropertiesOfObjectType(source);
        }
        foreach (var prop in props)
        {
            if (Checker.IsIgnoredJsxProperty(source, prop))
            {
                continue;
            }
            if (c.IsApplicableIndexType(c.GetLiteralTypeFromProperty(prop, TypeFlags.StringOrNumberLiteralOrUnique, false), keyType))
            {
                var propType = c.GetNonMissingTypeOfSymbol(prop);
                Type t;
                if (c.exactOptionalPropertyTypes || (propType.flags & TypeFlags.Undefined) != 0 || keyType == c.numberType || (prop.Flags & SymbolFlags.Optional) == 0)
                {
                    t = propType;
                }
                else
                {
                    t = c.GetTypeWithFacts(propType, TypeFacts.NEUndefined);
                }
                var related = IsRelatedToEx(t, targetInfo.valueType, RecursionFlags.Both, reportErrors, null, intersectionState);
                if (related == Ternary.False)
                {
                    if (reportErrors)
                    {
                        ReportError(Diag.Property_0_is_incompatible_with_index_signature, c.SymbolToString(prop));
                    }
                    return Ternary.False;
                }
                result &= related;
            }
        }
        foreach (var info in c.GetIndexInfosOfType(source))
        {
            if (c.IsApplicableIndexType(info.keyType, keyType))
            {
                var related = IndexInfoRelatedTo(info, targetInfo, reportErrors, intersectionState);
                if (related == Ternary.False)
                {
                    return Ternary.False;
                }
                result &= related;
            }
        }
        return result;
    }

    // relater.go:4671-4681
    internal Ternary IndexInfoRelatedTo(IndexInfo sourceInfo, IndexInfo targetInfo, bool reportErrors, IntersectionState intersectionState)
    {
        var related = IsRelatedToEx(sourceInfo.valueType, targetInfo.valueType, RecursionFlags.Both, reportErrors, null, intersectionState);
        if (related == Ternary.False && reportErrors)
        {
            if (sourceInfo.keyType == targetInfo.keyType)
            {
                ReportError(Diag.X_0_index_signatures_are_incompatible, c.TypeToString(sourceInfo.keyType));
            }
            else
            {
                ReportError(Diag.X_0_and_1_index_signatures_are_incompatible, c.TypeToString(sourceInfo.keyType), c.TypeToString(targetInfo.keyType));
            }
        }
        return related;
    }

    // relater.go:4683-4696
    internal Ternary IndexSignaturesIdenticalTo(Type source, Type target)
    {
        var sourceInfos = c.GetIndexInfosOfType(source);
        var targetInfos = c.GetIndexInfosOfType(target);
        if (sourceInfos.Length != targetInfos.Length)
        {
            return Ternary.False;
        }
        foreach (var targetInfo in targetInfos)
        {
            var sourceInfo = c.GetIndexInfoOfType(source, targetInfo.keyType);
            if (!(sourceInfo != null && IsRelatedTo(sourceInfo.valueType, targetInfo.valueType, RecursionFlags.Both, false) != Ternary.False && sourceInfo.isReadonly == targetInfo.isReadonly))
            {
                return Ternary.False;
            }
        }
        return Ternary.True;
    }

    // relater.go:4698-4742
    internal void ReportErrorResults(Type originalSource, Type originalTarget, Type source, Type target, Message? headMessage)
    {
        bool sourceHasBase = c.GetSingleBaseForNonAugmentingSubtype(originalSource) != null;
        bool targetHasBase = c.GetSingleBaseForNonAugmentingSubtype(originalTarget) != null;
        if (originalSource.alias != null || sourceHasBase)
        {
            source = originalSource;
        }
        if (originalTarget.alias != null || targetHasBase)
        {
            target = originalTarget;
        }
        if ((source.flags & TypeFlags.Object) != 0 && (target.flags & TypeFlags.Object) != 0)
        {
            TryElaborateArrayLikeErrors(source, target, true);
        }
        if ((source.flags & TypeFlags.Object) != 0 && (target.flags & TypeFlags.Primitive) != 0)
        {
            TryElaborateErrorsForPrimitivesAndObjects(source, target);
        }
        else if (source.symbol != null && (source.flags & TypeFlags.Object) != 0 && c.globalObjectType == source)
        {
            ReportError(Diag.The_Object_type_is_assignable_to_very_few_other_types_Did_you_mean_to_use_the_any_type_instead);
        }
        else if ((source.objectFlags & ObjectFlags.JsxAttributes) != 0 && (target.flags & TypeFlags.Intersection) != 0)
        {
            var targetTypes = target.Types();
            var intrinsicAttributes = c.GetJsxType(JsxNames.IntrinsicAttributes, errorNode);
            var intrinsicClassAttributes = c.GetJsxType(JsxNames.IntrinsicClassAttributes, errorNode);
            if (!c.IsErrorType(intrinsicAttributes) && !c.IsErrorType(intrinsicClassAttributes) && (Array.IndexOf(targetTypes, intrinsicAttributes) >= 0 || Array.IndexOf(targetTypes, intrinsicClassAttributes) >= 0))
            {
                return;
            }
        }
        else if ((originalTarget.flags & TypeFlags.Intersection) != 0 && (originalTarget.objectFlags & ObjectFlags.IsNeverIntersection) != 0)
        {
            var message = Diag.The_intersection_0_was_reduced_to_never_because_property_1_has_conflicting_types_in_some_constituents;
            var prop = CoreUtil.Find(c.GetPropertiesOfUnionOrIntersectionType(originalTarget), p => c.IsDiscriminantWithNeverType(p));
            if (prop == null)
            {
                message = Diag.The_intersection_0_was_reduced_to_never_because_property_1_exists_in_multiple_constituents_and_is_private_in_some;
                prop = CoreUtil.Find(c.GetPropertiesOfUnionOrIntersectionType(originalTarget), Checker.IsConflictingPrivateProperty);
            }
            if (prop != null)
            {
                ReportError(message, c.TypeToStringEx(originalTarget, null, TypeFormatFlags.NoTypeReduction, null), c.SymbolToString(prop));
            }
        }
        ReportRelationError(headMessage, source, target);
        if ((source.flags & TypeFlags.TypeParameter) != 0 && source.symbol != null && (source.symbol.Declarations?.Length ?? 0) != 0 && c.GetConstraintOfType(source) == null)
        {
            var syntheticParam = c.CloneTypeParameter(source);
            syntheticParam.AsTypeParameter().constraint = c.InstantiateType(target, TypeMapper.NewSimpleTypeMapper(source, syntheticParam));
            if (c.HasNonCircularBaseConstraint(syntheticParam))
            {
                var targetConstraintString = c.TypeToString(target);
                relatedInfo.Add(c.NewDiagnosticForNode(source.symbol.Declarations![0], Diag.This_type_parameter_might_need_an_extends_0_constraint, targetConstraintString));
            }
        }
    }

    // relater.go:4744-4822
    internal void ReportRelationError(Message? message, Type source, Type target)
    {
        var (sourceType, targetType) = c.GetTypeNamesForErrorDisplay(source, target);
        var generalizedSource = source;
        var generalizedSourceType = sourceType;
        if ((target.flags & TypeFlags.Never) == 0 && Checker.IsLiteralType(source) && !c.TypeCouldHaveTopLevelSingletonTypes(target))
        {
            generalizedSource = c.GetBaseTypeOfLiteralType(source);
            generalizedSourceType = c.GetTypeNameForErrorDisplay(generalizedSource);
        }
        TypeFlags targetFlags;
        if ((target.flags & TypeFlags.IndexedAccess) != 0 && (source.flags & TypeFlags.IndexedAccess) == 0)
        {
            targetFlags = target.AsIndexedAccessType().objectType.flags;
        }
        else
        {
            targetFlags = target.flags;
        }
        if ((targetFlags & TypeFlags.TypeParameter) != 0 && target != c.markerSuperTypeForCheck && target != c.markerSubTypeForCheck)
        {
            var constraint = c.GetBaseConstraintOfType(target);
            if (constraint != null && c.IsTypeAssignableTo(generalizedSource, constraint))
            {
                ReportError(Diag.X_0_is_assignable_to_the_constraint_of_type_1_but_1_could_be_instantiated_with_a_different_subtype_of_constraint_2, generalizedSourceType, targetType, c.TypeToString(constraint));
            }
            else if (constraint != null && c.IsTypeAssignableTo(source, constraint))
            {
                ReportError(Diag.X_0_is_assignable_to_the_constraint_of_type_1_but_1_could_be_instantiated_with_a_different_subtype_of_constraint_2, sourceType, targetType, c.TypeToString(constraint));
            }
            else
            {
                errorChain = null;
                ReportError(Diag.X_0_could_be_instantiated_with_an_arbitrary_type_which_could_be_unrelated_to_1, targetType, generalizedSourceType);
            }
        }
        if (message == null)
        {
            if (relation == c.comparableRelation)
            {
                message = Diag.Type_0_is_not_comparable_to_type_1;
            }
            else if (sourceType == targetType)
            {
                message = Diag.Type_0_is_not_assignable_to_type_1_Two_different_types_with_this_name_exist_but_they_are_unrelated;
            }
            else if (c.exactOptionalPropertyTypes && c.GetExactOptionalUnassignableProperties(source, target).Length != 0)
            {
                message = Diag.Type_0_is_not_assignable_to_type_1_with_exactOptionalPropertyTypes_Colon_true_Consider_adding_undefined_to_the_types_of_the_target_s_properties;
            }
            else
            {
                if ((source.flags & TypeFlags.StringLiteral) != 0 && (target.flags & TypeFlags.Union) != 0)
                {
                    var suggestedType = c.GetSuggestedTypeForNonexistentStringLiteralType(source, target);
                    if (suggestedType != null)
                    {
                        ReportError(Diag.Type_0_is_not_assignable_to_type_1_Did_you_mean_2, generalizedSourceType, targetType, c.TypeToString(suggestedType));
                        return;
                    }
                }
                message = Diag.Type_0_is_not_assignable_to_type_1;
            }
        }
        else if (message == Diag.Argument_of_type_0_is_not_assignable_to_parameter_of_type_1 && c.exactOptionalPropertyTypes && c.GetExactOptionalUnassignableProperties(source, target).Length > 0)
        {
            message = Diag.Argument_of_type_0_is_not_assignable_to_parameter_of_type_1_with_exactOptionalPropertyTypes_Colon_true_Consider_adding_undefined_to_the_types_of_the_target_s_properties;
        }
        var chainMessage = GetChainMessage(0);
        if (chainMessage == Diag.Object_literal_may_only_specify_known_properties_and_0_does_not_exist_in_type_1 ||
            chainMessage == Diag.Object_literal_may_only_specify_known_properties_but_0_does_not_exist_in_type_1_Did_you_mean_to_write_2)
        {
            return;
        }
        if (chainMessage == Diag.Excessive_complexity_comparing_types_0_and_1 ||
            chainMessage == Diag.Excessive_stack_depth_comparing_types_0_and_1 ||
            chainMessage == Diag.The_type_0_is_readonly_and_cannot_be_assigned_to_the_mutable_type_1)
        {
            if (ChainArgsMatch(generalizedSourceType, targetType))
            {
                return;
            }
        }
        else if (chainMessage == Diag.Property_0_is_missing_in_type_1_but_required_in_type_2)
        {
            if (!Checker.IsConversionOrInterfaceImplementationMessage(message) && ChainArgsMatch(null, generalizedSourceType, targetType))
            {
                return;
            }
        }
        else if (chainMessage == Diag.Type_0_is_missing_the_following_properties_from_type_1_Colon_2_and_3_more ||
            chainMessage == Diag.Type_0_is_missing_the_following_properties_from_type_1_Colon_2)
        {
            if (!Checker.IsConversionOrInterfaceImplementationMessage(message) && ChainArgsMatch(generalizedSourceType, targetType))
            {
                return;
            }
        }
        ReportError(message, generalizedSourceType, targetType);
    }

    // relater.go:4824-4870
    internal void ReportError(Message message, params object?[] args)
    {
        if (message == Diag.Types_of_property_0_are_incompatible)
        {
            var cm = GetChainMessage(0);
            if (cm == Diag.Object_literal_may_only_specify_known_properties_and_0_does_not_exist_in_type_1 ||
                cm == Diag.Object_literal_may_only_specify_known_properties_but_0_does_not_exist_in_type_1_Did_you_mean_to_write_2)
            {
                return;
            }
            string arg = "";
            var cm1 = GetChainMessage(1);
            if (cm1 == Diag.Call_signatures_with_no_arguments_have_incompatible_return_types_0_and_1)
            {
                arg = Checker.GetPropertyNameArg(args[0]) + "()";
            }
            else if (cm1 == Diag.Construct_signatures_with_no_arguments_have_incompatible_return_types_0_and_1)
            {
                arg = "new " + Checker.GetPropertyNameArg(args[0]) + "()";
            }
            else if (cm1 == Diag.Call_signature_return_types_0_and_1_are_incompatible)
            {
                arg = Checker.GetPropertyNameArg(args[0]) + "(...)";
            }
            else if (cm1 == Diag.Construct_signature_return_types_0_and_1_are_incompatible)
            {
                arg = "new " + Checker.GetPropertyNameArg(args[0]) + "(...)";
            }
            if (arg != "")
            {
                message = Diag.The_types_returned_by_0_are_incompatible_between_these_types;
                args[0] = arg;
                errorChain = errorChain!.next!.next;
            }
            var cm1b = GetChainMessage(1);
            if (cm1b == Diag.Types_of_property_0_are_incompatible ||
                cm1b == Diag.The_types_of_0_are_incompatible_between_these_types ||
                cm1b == Diag.The_types_returned_by_0_are_incompatible_between_these_types)
            {
                var head = Checker.GetPropertyNameArg(args[0]);
                var tail = Checker.GetPropertyNameArg(errorChain!.next!.args[0]);
                var arg2 = Checker.AddToDottedName(head, tail);
                errorChain = errorChain.next.next;
                if (message == Diag.Types_of_property_0_are_incompatible)
                {
                    message = Diag.The_types_of_0_are_incompatible_between_these_types;
                }
                ReportError(message, arg2);
                return;
            }
        }
        errorChain = new ErrorChain { next = errorChain, message = message, args = args };
    }

    // relater.go:4894-4906
    internal Message? GetChainMessage(int index)
    {
        var e = errorChain;
        while (true)
        {
            if (e == null)
            {
                return null;
            }
            if (index == 0)
            {
                return e.message;
            }
            e = e.next;
            index--;
        }
    }

    // relater.go:4910-4917
    internal bool ChainArgsMatch(params object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a != null && !Equals(a, errorChain!.args[i]))
            {
                return false;
            }
        }
        return true;
    }

    // relater.go:4990-5005 — PORT-NOTE: tracing.Instant omitted (phase 9); body is a no-op observer.
    internal void TraceUnionsOrIntersectionsTooLarge(Type source, Type target)
    {
        _ = source;
        _ = target;
    }

    // relater.go — helper mirroring slices.Equal over string slices.
    private static bool SlicesEqualStrings(string[] a, string[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }
}
