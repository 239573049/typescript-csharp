// Ported from tsgo internal/checker/inference.go (plan/03 §4.2 #— "inference.go → Checker.Inference.cs,
// containing InferenceContext/InferenceInfo"). 1:1 per-function translation, Go source order preserved.
//
// InferenceInfo / InferenceContext / IntraExpressionInferenceSite already live in Checker.cs (skeleton
// wave); InferencePriority / InferenceFlags / ExpandingFlags in Enums.cs — NOT redefined here.
// InferenceKey and the filled-in InferenceState are in Checker.Inference.Support.cs. Cross-cluster
// helpers this cluster calls but does not own are stubbed in Checker.Inference.Deps.cs (owner deletes
// on landing). Local slice/priority helpers live in Checker.Inference.Support.cs.
//
// §1.5: every name referenced here is a Checker member, a Ts.Ast type (via `using Ts.Ast;`,
// AstUtilities/NodeTests used directly), or Ts.Core.CoreUtil — no name collides with a sibling
// namespace, so no `global::` qualification is required except jsnum (Ts.JsNum has a `JsNum` class
// that would be shadowed by the sibling namespace, so it is fully qualified).

using System;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // inference.go:33-40
    private InferenceState GetInferenceState()
    {
        var n = freeinferenceState;
        if (n is null)
        {
            n = new InferenceState();
        }
        freeinferenceState = n.next;
        return n;
    }

    // inference.go:42-52
    private void PutInferenceState(InferenceState n)
    {
        n.visited?.Clear();
        // *n = InferenceState{...} — reset to zero value, preserving reusable buffers.
        n.inferences = [];
        n.originalSource = null;
        n.originalTarget = null;
        n.priority = InferencePriority.None;
        n.inferencePriority = InferencePriority.None;
        n.contravariant = false;
        n.bivariant = false;
        n.expandingFlags = ExpandingFlags.None;
        n.propagationType = null;
        // n.visited preserved (cleared above)
        n.sourceStack.Clear();
        n.targetStack.Clear();
        n.depth = 0;
        n.next = freeinferenceState;
        freeinferenceState = n;
    }

    // inference.go:54-64
    internal void InferTypes(InferenceInfo[] inferences, Type originalSource, Type originalTarget, InferencePriority priority, bool contravariant)
    {
        var n = GetInferenceState();
        n.inferences = inferences;
        n.originalSource = originalSource;
        n.originalTarget = originalTarget;
        n.priority = priority;
        n.inferencePriority = InferencePriority.MaxValue;
        n.contravariant = contravariant;
        InferFromTypes(n, originalSource, originalTarget);
        PutInferenceState(n);
    }

    // inference.go:66-303
    internal void InferFromTypes(InferenceState n, Type source, Type target)
    {
        if (!couldContainTypeVariables(target) || IsNoInferType(target))
        {
            return;
        }
        if (source == wildcardType || source == blockedStringType)
        {
            // We are inferring from an 'any' type. We want to infer this type for every type parameter
            // referenced in the target type, so we record it as the propagation type and infer from the
            // target to itself. Then, as we find candidates we substitute the propagation type.
            var savePropagationType = n.propagationType;
            n.propagationType = source;
            InferFromTypes(n, target, target);
            n.propagationType = savePropagationType;
            return;
        }
        if (source.alias is not null && target.alias is not null && source.alias.symbol == target.alias.symbol)
        {
            if ((source.alias.typeArguments?.Length ?? 0) != 0 || (target.alias.typeArguments?.Length ?? 0) != 0)
            {
                // Source and target are types originating in the same generic type alias declaration.
                // Simply infer from source type arguments to target type arguments, with defaults applied.
                var parms = typeAliasLinks.Get(source.alias.symbol!).typeParameters;
                var minParams = GetMinTypeArgumentCount(parms);
                var nodeIsInJsFile = AstUtilities.IsInJSFile(source.alias.symbol!.ValueDeclaration);
                var sourceTypes = FillMissingTypeArguments(source.alias.typeArguments, parms, minParams, nodeIsInJsFile);
                var targetTypes = FillMissingTypeArguments(target.alias.typeArguments, parms, minParams, nodeIsInJsFile);
                InferFromTypeArguments(n, sourceTypes, targetTypes, GetAliasVariances(source.alias.symbol!));
            }
            // And if there weren't any type arguments, there's no reason to run inference as the types must be the same.
            return;
        }
        if (source == target && (source.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            // When source and target are the same union or intersection type, just relate each constituent
            // type to itself.
            foreach (var t in source.Types())
            {
                InferFromTypes(n, t, t);
            }
            return;
        }
        if ((target.flags & TypeFlags.Union) != 0)
        {
            Type[] sourceTypes;
            if ((source.flags & TypeFlags.Union) != 0)
            {
                sourceTypes = source.Types();
            }
            else
            {
                sourceTypes = [source];
            }
            // First, infer between identically matching source and target constituents and remove the
            // matching types.
            var (tempSources, tempTargets) = InferFromMatchingTypes(n, sourceTypes, target.Distributed(), IsTypeOrBaseIdenticalTo);
            // Next, infer between closely matching source and target constituents and remove
            // the matching types. Types closely match when they are instantiations of the same
            // object type or instantiations of the same type alias.
            var (sources, targets) = InferFromMatchingTypes(n, tempSources, tempTargets, IsTypeCloselyMatchedBy);
            if (targets.Length == 0)
            {
                return;
            }
            target = GetUnionType(targets);
            if (sources.Length == 0)
            {
                // All source constituents have been matched and there is nothing further to infer from.
                // However, simply making no inferences is undesirable because it could ultimately mean
                // inferring a type parameter constraint. Instead, make a lower priority inference from
                // the full source to whatever remains in the target. For example, when inferring from
                // string to 'string | T', make a lower priority inference of string for T.
                InferWithPriority(n, source, target, InferencePriority.NakedTypeVariable);
                return;
            }
            source = GetUnionType(sources);
        }
        else if ((target.flags & TypeFlags.Intersection) != 0 && !CoreUtil.Every(target.Types(), IsNonGenericObjectType))
        {
            // We reduce intersection types unless they're simple combinations of object types. For example,
            // when inferring from 'string[] & { extra: any }' to 'string[] & T' we want to remove string[] and
            // infer { extra: any } for T. But when inferring to 'string[] & Iterable<T>' we want to keep the
            // string[] on the source side and infer string for T.
            if ((source.flags & TypeFlags.Union) == 0)
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
                // Infer between identically matching source and target constituents and remove the matching types.
                var (sources, targets) = InferFromMatchingTypes(n, sourceTypes, target.Types(), IsTypeIdenticalTo);
                if (sources.Length == 0 || targets.Length == 0)
                {
                    return;
                }
                source = GetIntersectionType(sources);
                target = GetIntersectionType(targets);
            }
        }
        if ((target.flags & (TypeFlags.IndexedAccess | TypeFlags.Substitution)) != 0)
        {
            if (IsNoInferType(target))
            {
                return;
            }
            target = GetActualTypeVariable(target);
        }
        if ((target.flags & TypeFlags.TypeVariable) != 0)
        {
            // Skip inference if the source is "blocked", which is used by the language service to
            // prevent inference on nodes currently being edited.
            if (IsFromInferenceBlockedSource(source))
            {
                return;
            }
            var inference = GetInferenceInfoForType(n, target);
            if (inference is not null)
            {
                // If target is a type parameter, make an inference, unless the source type contains
                // a "non-inferrable" type. Types with this flag set are markers used to prevent inference.
                if ((source.objectFlags & ObjectFlags.NonInferrableType) != 0 || source == nonInferrableAnyType)
                {
                    return;
                }
                if (!inference.isFixed)
                {
                    var candidate = n.propagationType ?? source;
                    if (candidate == blockedStringType)
                    {
                        return;
                    }
                    if (n.priority < inference.priority)
                    {
                        inference.candidates = null;
                        inference.candidateDepths = null;
                        inference.contraCandidates = null;
                        inference.topLevel = true;
                        inference.priority = n.priority;
                    }
                    if (n.priority == inference.priority)
                    {
                        // We make contravariant inferences only if we are in a pure contravariant position,
                        // i.e. only if we have not descended into a bivariant position.
                        if (n.contravariant && !n.bivariant)
                        {
                            if (!SlicesContains(inference.contraCandidates, candidate))
                            {
                                inference.contraCandidates = SlicesAppend(inference.contraCandidates, candidate);
                                ClearCachedInferences(n.inferences);
                            }
                        }
                        else
                        {
                            var index = SlicesIndex(inference.candidates, candidate);
                            if (index < 0 || inference.candidateDepths![index] < n.depth)
                            {
                                // Candidate isn't present or is present with lower depth
                                if (index >= 0)
                                {
                                    // Remove candidate with lower depth
                                    inference.candidates = SlicesDelete(inference.candidates!, index, index + 1);
                                    inference.candidateDepths = SlicesDelete(inference.candidateDepths!, index, index + 1);
                                }
                                index = 0;
                                while (index < (inference.candidateDepths?.Length ?? 0))
                                {
                                    if (inference.candidateDepths![index] < n.depth)
                                    {
                                        break;
                                    }
                                    index++;
                                }
                                // Insert candidate at end or immediately before first candidate with lower depth.
                                // This ensures candidates with the highest depth are stored first.
                                inference.candidates = SlicesInsert(inference.candidates, index, candidate);
                                inference.candidateDepths = SlicesInsert(inference.candidateDepths, index, n.depth);
                                ClearCachedInferences(n.inferences);
                            }
                        }
                    }
                    if ((n.priority & InferencePriority.ReturnType) == 0 && (target.flags & TypeFlags.TypeParameter) != 0 && inference.topLevel && !IsTypeParameterAtTopLevel(n.originalTarget!, target, 0))
                    {
                        inference.topLevel = false;
                        ClearCachedInferences(n.inferences);
                    }
                }
                n.inferencePriority = MinInf(n.inferencePriority, n.priority);
                return;
            }
            // Infer to the simplified version of an indexed access, if possible, to (hopefully) expose more bare type parameters to the inference engine
            var simplified = GetSimplifiedType(target, false /*writing*/);
            if (simplified != target)
            {
                InferFromTypes(n, source, simplified);
            }
            else if ((target.flags & TypeFlags.IndexedAccess) != 0)
            {
                var indexType = GetSimplifiedType(target.AsIndexedAccessType().indexType, false /*writing*/);
                // Generally simplifications of instantiable indexes are avoided to keep relationship checking correct, however if our target is an access, we can consider
                // that key of that access to be "instantiated", since we're looking to find the inference goal in any way we can.
                if ((indexType.flags & TypeFlags.Instantiable) != 0)
                {
                    var simplifiedAccess = DistributeIndexOverObjectType(GetSimplifiedType(target.AsIndexedAccessType().objectType, false /*writing*/), indexType, false /*writing*/);
                    if (simplifiedAccess is not null && simplifiedAccess != target)
                    {
                        InferFromTypes(n, source, simplifiedAccess);
                    }
                }
            }
        }
        if ((source.objectFlags & ObjectFlags.Reference) != 0 && (target.objectFlags & ObjectFlags.Reference) != 0 && (source.AsTypeReference()!.target == target.AsTypeReference()!.target || (IsArrayType(source) && IsArrayType(target))) && !(source.AsTypeReference()!.node is not null && target.AsTypeReference()!.node is not null))
        {
            // If source and target are references to the same generic type, infer from type arguments
            InferFromTypeArguments(n, GetTypeArguments(source), GetTypeArguments(target), GetVariances(source.AsTypeReference()!.target!));
        }
        else if ((source.flags & TypeFlags.Index) != 0 && (target.flags & TypeFlags.Index) != 0)
        {
            InferFromContravariantTypes(n, source.AsIndexType().target, target.AsIndexType().target);
        }
        else if ((IsLiteralType(source) || (source.flags & TypeFlags.String) != 0) && (target.flags & TypeFlags.Index) != 0)
        {
            var empty = CreateEmptyObjectTypeFromStringLiteral(source);
            InferFromContravariantTypesWithPriority(n, empty, target.AsIndexType().target, InferencePriority.LiteralKeyof);
        }
        else if ((source.flags & TypeFlags.IndexedAccess) != 0 && (target.flags & TypeFlags.IndexedAccess) != 0)
        {
            InferFromTypes(n, source.AsIndexedAccessType().objectType, target.AsIndexedAccessType().objectType);
            InferFromTypes(n, source.AsIndexedAccessType().indexType, target.AsIndexedAccessType().indexType);
        }
        else if ((source.flags & TypeFlags.StringMapping) != 0 && (target.flags & TypeFlags.StringMapping) != 0)
        {
            if (source.symbol == target.symbol)
            {
                InferFromTypes(n, source.AsStringMappingType().target, target.AsStringMappingType().target);
            }
        }
        else if ((source.flags & TypeFlags.Substitution) != 0)
        {
            InferFromTypes(n, source.AsSubstitutionType().baseType, target);
            // Make substitute inference at a lower priority
            InferWithPriority(n, GetSubstitutionIntersection(source), target, InferencePriority.SubstituteSource);
        }
        else if ((target.flags & TypeFlags.Conditional) != 0)
        {
            InvokeOnce(n, source, target, InferToConditionalType);
        }
        else if ((target.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            InferToMultipleTypes(n, source, target.Types(), target.flags);
        }
        else if ((source.flags & TypeFlags.Union) != 0)
        {
            // Source is a union or intersection type, infer from each constituent type
            foreach (var sourceType in source.Types())
            {
                InferFromTypes(n, sourceType, target);
            }
        }
        else if ((target.flags & TypeFlags.TemplateLiteral) != 0)
        {
            InferToTemplateLiteralType(n, source, target.AsTemplateLiteralType());
        }
        else
        {
            source = GetReducedType(source);
            if (IsGenericMappedType(source) && IsGenericMappedType(target))
            {
                InvokeOnce(n, source, target, InferFromGenericMappedTypes);
            }
            if (!((n.priority & InferencePriority.NoConstraints) != 0 && (source.flags & (TypeFlags.Intersection | TypeFlags.Instantiable)) != 0))
            {
                var apparentSource = GetApparentType(source);
                // getApparentType can return _any_ type, since an indexed access or conditional may simplify to any other type.
                // If that occurs and it doesn't simplify to an object or intersection, we'll need to restart `inferFromTypes`
                // with the simplified source.
                if (apparentSource != source && (apparentSource.flags & (TypeFlags.Object | TypeFlags.Intersection)) == 0)
                {
                    InferFromTypes(n, apparentSource, target);
                    return;
                }
                source = apparentSource;
            }
            if ((source.flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0)
            {
                InvokeOnce(n, source, target, InferFromObjectTypes);
            }
        }
    }

    // inference.go:305-315
    internal void InferFromTypeArguments(InferenceState n, Type[]? sourceTypes, Type[]? targetTypes, VarianceFlags[]? variances)
    {
        n.depth++;
        var count = Math.Min(sourceTypes?.Length ?? 0, targetTypes?.Length ?? 0);
        var varianceCount = variances?.Length ?? 0;
        for (int i = 0; i < count; i++)
        {
            if (i < varianceCount && (variances![i] & VarianceFlags.VarianceMask) == VarianceFlags.Contravariant)
            {
                InferFromContravariantTypes(n, sourceTypes![i], targetTypes![i]);
            }
            else
            {
                InferFromTypes(n, sourceTypes![i], targetTypes![i]);
            }
        }
        n.depth--;
    }

    // inference.go:317-322
    internal void InferWithPriority(InferenceState n, Type source, Type target, InferencePriority newPriority)
    {
        var savePriority = n.priority;
        n.priority |= newPriority;
        InferFromTypes(n, source, target);
        n.priority = savePriority;
    }

    // inference.go:324-329
    internal void InferFromContravariantTypesWithPriority(InferenceState n, Type source, Type target, InferencePriority newPriority)
    {
        var savePriority = n.priority;
        n.priority |= newPriority;
        InferFromContravariantTypes(n, source, target);
        n.priority = savePriority;
    }

    // inference.go:331-335
    internal void InferFromContravariantTypes(InferenceState n, Type source, Type target)
    {
        n.contravariant = !n.contravariant;
        InferFromTypes(n, source, target);
        n.contravariant = !n.contravariant;
    }

    // inference.go:337-343
    internal void InferFromContravariantTypesIfStrictFunctionTypes(InferenceState n, Type source, Type target)
    {
        if (strictFunctionTypes || (n.priority & InferencePriority.AlwaysStrict) != 0)
        {
            InferFromContravariantTypes(n, source, target);
        }
        else
        {
            InferFromTypes(n, source, target);
        }
    }

    // inference.go:345-391 — Ensure an inference action is performed only once for the given source and
    // target types (avoids duplicate and circular inference between the same pair).
    internal void InvokeOnce(InferenceState n, Type source, Type target, Action<InferenceState, Type, Type> action)
    {
        var key = new InferenceKey(source.id, target.id);
        if (n.visited is not null && n.visited.TryGetValue(key, out var status))
        {
            n.inferencePriority = MinInf(n.inferencePriority, status);
            return;
        }
        n.visited ??= new Dictionary<InferenceKey, InferencePriority>();
        n.visited[key] = InferencePriority.Circularity;
        var saveInferencePriority = n.inferencePriority;
        n.inferencePriority = InferencePriority.MaxValue;
        // We stop inferring and report a circularity if we encounter duplicate recursion identities on both
        // the source side and the target side.
        var saveExpandingFlags = n.expandingFlags;
        n.sourceStack.Add(source);
        n.targetStack.Add(target);
        if (IsDeeplyNestedType(source, n.sourceStack, 2))
        {
            n.expandingFlags |= ExpandingFlags.Source;
        }
        if (IsDeeplyNestedType(target, n.targetStack, 2))
        {
            n.expandingFlags |= ExpandingFlags.Target;
        }
        if (n.expandingFlags != ExpandingFlags.Both)
        {
            action(n, source, target);
        }
        else
        {
            n.inferencePriority = InferencePriority.Circularity;
        }
        n.targetStack.RemoveAt(n.targetStack.Count - 1);
        n.sourceStack.RemoveAt(n.sourceStack.Count - 1);
        n.expandingFlags = saveExpandingFlags;
        n.visited[key] = n.inferencePriority;
        n.inferencePriority = MinInf(n.inferencePriority, saveInferencePriority);
    }

    // inference.go:393-412
    internal (Type[], Type[]) InferFromMatchingTypes(InferenceState n, Type[] sources, Type[] targets, Func<Type, Type, bool> matches)
    {
        Type[]? matchedSources = null;
        Type[]? matchedTargets = null;
        foreach (var t in targets)
        {
            foreach (var s in sources)
            {
                if (matches(s, t))
                {
                    InferFromTypes(n, s, t);
                    matchedSources = CoreUtil.AppendIfUnique(matchedSources, s);
                    matchedTargets = CoreUtil.AppendIfUnique(matchedTargets, t);
                }
            }
        }
        if (matchedSources is not null)
        {
            sources = CoreUtil.Filter(sources, t => !SlicesContains(matchedSources, t)) ?? [];
        }
        if (matchedTargets is not null)
        {
            targets = CoreUtil.Filter(targets, t => !SlicesContains(matchedTargets, t)) ?? [];
        }
        return (sources, targets);
    }

    // inference.go:414-496
    internal void InferToMultipleTypes(InferenceState n, Type source, Type[] targets, TypeFlags targetFlags)
    {
        var typeVariableCount = 0;
        if ((targetFlags & TypeFlags.Union) != 0)
        {
            Type? nakedTypeVariable = null;
            Type[] sources;
            if ((source.flags & TypeFlags.Union) != 0)
            {
                sources = source.Types();
            }
            else
            {
                sources = [source];
            }
            var matched = new bool[sources.Length];
            var inferenceCircularity = false;
            // First infer to types that are not naked type variables. For each source type we
            // track whether inferences were made from that particular type to some target with
            // equal priority (i.e. of equal quality) to what we would infer for a naked type
            // parameter.
            foreach (var t in targets)
            {
                if (GetInferenceInfoForType(n, t) is not null)
                {
                    nakedTypeVariable = t;
                    typeVariableCount++;
                }
                else
                {
                    for (int i = 0; i < sources.Length; i++)
                    {
                        var saveInferencePriority = n.inferencePriority;
                        n.inferencePriority = InferencePriority.MaxValue;
                        InferFromTypes(n, sources[i], t);
                        if (n.inferencePriority == n.priority)
                        {
                            matched[i] = true;
                        }
                        inferenceCircularity = inferenceCircularity || n.inferencePriority == InferencePriority.Circularity;
                        n.inferencePriority = MinInf(n.inferencePriority, saveInferencePriority);
                    }
                }
            }
            if (typeVariableCount == 0)
            {
                // If every target is an intersection of types containing a single naked type variable,
                // make a lower priority inference to that type variable. This handles inferring from
                // 'A | B' to 'T & (X | Y)' where we want to infer 'A | B' for T.
                var intersectionTypeVariable = GetSingleTypeVariableFromIntersectionTypes(n, targets);
                if (intersectionTypeVariable is not null)
                {
                    InferWithPriority(n, source, intersectionTypeVariable, InferencePriority.NakedTypeVariable);
                }
                return;
            }
            // If the target has a single naked type variable and no inference circularities were
            // encountered above (meaning we explored the types fully), create a union of the source
            // types from which no inferences have been made so far and infer from that union to the
            // naked type variable.
            if (typeVariableCount == 1 && !inferenceCircularity)
            {
                Type[]? unmatched = null;
                for (int i = 0; i < sources.Length; i++)
                {
                    if (!matched[i])
                    {
                        unmatched = SlicesAppend(unmatched, sources[i]);
                    }
                }
                if (unmatched is not null && unmatched.Length != 0)
                {
                    InferFromTypes(n, GetUnionType(unmatched), nakedTypeVariable!);
                    return;
                }
            }
        }
        else
        {
            // We infer from types that are not naked type variables first so that inferences we
            // make from nested naked type variables and given slightly higher priority by virtue
            // of being first in the candidates array.
            foreach (var t in targets)
            {
                if (GetInferenceInfoForType(n, t) is not null)
                {
                    typeVariableCount++;
                }
                else
                {
                    InferFromTypes(n, source, t);
                }
            }
        }
        // Inferences directly to naked type variables are given lower priority as they are
        // less specific. For example, when inferring from Promise<string> to T | Promise<T>,
        // we want to infer string for T, not Promise<string> | string. For intersection types
        // we only infer to single naked type variables.
        if (((targetFlags & TypeFlags.Intersection) != 0 && typeVariableCount == 1) || ((targetFlags & TypeFlags.Intersection) == 0 && typeVariableCount > 0))
        {
            foreach (var t in targets)
            {
                if (GetInferenceInfoForType(n, t) is not null)
                {
                    InferWithPriority(n, source, t, InferencePriority.NakedTypeVariable);
                }
            }
        }
    }

    // inference.go:498-511
    internal static Type? GetSingleTypeVariableFromIntersectionTypes(InferenceState n, Type[] types)
    {
        Type? typeVariable = null;
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Intersection) == 0)
            {
                return null;
            }
            var v = CoreUtil.Find(t.Types(), tt => GetInferenceInfoForType(n, tt) is not null);
            if (v is null || (typeVariable is not null && v != typeVariable))
            {
                return null;
            }
            typeVariable = v;
        }
        return typeVariable;
    }

    // inference.go:513-518
    internal void InferToMultipleTypesWithPriority(InferenceState n, Type source, Type[] targets, TypeFlags targetFlags, InferencePriority newPriority)
    {
        var savePriority = n.priority;
        n.priority |= newPriority;
        InferToMultipleTypes(n, source, targets, targetFlags);
        n.priority = savePriority;
    }

    // inference.go:520-530
    internal void InferToConditionalType(InferenceState n, Type source, Type target)
    {
        if ((source.flags & TypeFlags.Conditional) != 0)
        {
            InferFromTypes(n, source.AsConditionalType().checkType!, target.AsConditionalType().checkType!);
            InferFromTypes(n, source.AsConditionalType().extendsType!, target.AsConditionalType().extendsType!);
            InferFromTypes(n, GetTrueTypeFromConditionalType(source), GetTrueTypeFromConditionalType(target));
            InferFromTypes(n, GetFalseTypeFromConditionalType(source), GetFalseTypeFromConditionalType(target));
        }
        else
        {
            Type[] targetTypes = [GetTrueTypeFromConditionalType(target), GetFalseTypeFromConditionalType(target)];
            InferToMultipleTypesWithPriority(n, source, targetTypes, target.flags, n.contravariant ? InferencePriority.ContravariantConditional : InferencePriority.None);
        }
    }

    // inference.go:532-651
    internal void InferToTemplateLiteralType(InferenceState n, Type source, TemplateLiteralType target)
    {
        var matches = InferTypesFromTemplateLiteralType(source, target);
        var types = target.types;
        // When the target template literal contains only placeholders (meaning that inference is intended to extract
        // single characters and remainder strings) and inference fails to produce matches, we want to infer 'never' for
        // each placeholder such that instantiation with the inferred value(s) produces 'never', a type for which an
        // assignment check will fail.
        if ((matches?.Length ?? 0) != 0 || CoreUtil.Every(target.texts, s => s == ""))
        {
            for (int i = 0; i < types.Length; i++)
            {
                var targetType = types[i];
                Type src;
                if ((matches?.Length ?? 0) != 0)
                {
                    src = matches![i];
                }
                else
                {
                    src = neverType!;
                }
                // If we are inferring from a string literal type to a type variable whose constraint includes one of the
                // allowed template literal placeholder types, infer from a literal type corresponding to the constraint.
                if ((src.flags & TypeFlags.StringLiteral) != 0 && (targetType.flags & TypeFlags.TypeVariable) != 0)
                {
                    var inferenceContext = GetInferenceInfoForType(n, targetType);
                    if (inferenceContext is not null)
                    {
                        var constraint = GetBaseConstraintOfType(inferenceContext.typeParameter);
                        if (constraint is not null && !IsTypeAny(constraint))
                        {
                            var allTypeFlags = TypeFlags.None;
                            foreach (var t in constraint.Distributed())
                            {
                                allTypeFlags |= t.flags;
                            }
                            // If the constraint contains `string`, we don't need to look for a more preferred type
                            if ((allTypeFlags & TypeFlags.String) == 0)
                            {
                                var str = GetStringLiteralValue(src);
                                // If the type contains `number` or a number literal and the string isn't a valid number, exclude numbers
                                if ((allTypeFlags & TypeFlags.NumberLike) != 0 && !IsValidNumberString(str, true /*roundTripOnly*/))
                                {
                                    allTypeFlags &= ~TypeFlags.NumberLike;
                                }
                                // If the type contains `bigint` or a bigint literal and the string isn't a valid bigint, exclude bigints
                                if ((allTypeFlags & TypeFlags.BigIntLike) != 0 && !IsValidBigIntString(str, true /*roundTripOnly*/))
                                {
                                    allTypeFlags &= ~TypeFlags.BigIntLike;
                                }
                                Type Choose(Type left, Type right)
                                {
                                    if ((right.flags & allTypeFlags) == 0) return left;
                                    if ((left.flags & TypeFlags.String) != 0) return left;
                                    if ((right.flags & TypeFlags.String) != 0) return src;
                                    if ((left.flags & TypeFlags.TemplateLiteral) != 0) return left;
                                    if ((right.flags & TypeFlags.TemplateLiteral) != 0 && IsTypeMatchedByTemplateLiteralType(src, right.AsTemplateLiteralType(), compareTypesAssignable)) return src;
                                    if ((left.flags & TypeFlags.StringMapping) != 0) return left;
                                    if ((right.flags & TypeFlags.StringMapping) != 0 && str == ApplyStringMapping(right.symbol!, str)) return src;
                                    if ((left.flags & TypeFlags.StringLiteral) != 0) return left;
                                    if ((right.flags & TypeFlags.StringLiteral) != 0 && GetStringLiteralValue(right) == str) return right;
                                    if ((left.flags & TypeFlags.Number) != 0) return left;
                                    if ((right.flags & TypeFlags.Number) != 0) return GetNumberLiteralType(global::Ts.JsNum.JsNum.FromString(str));
                                    if ((left.flags & TypeFlags.Enum) != 0) return left;
                                    if ((right.flags & TypeFlags.Enum) != 0) return GetNumberLiteralType(global::Ts.JsNum.JsNum.FromString(str));
                                    if ((left.flags & TypeFlags.NumberLiteral) != 0) return left;
                                    if ((right.flags & TypeFlags.NumberLiteral) != 0 && GetNumberLiteralValue(right) == global::Ts.JsNum.JsNum.FromString(str)) return right;
                                    if ((left.flags & TypeFlags.BigInt) != 0) return left;
                                    if ((right.flags & TypeFlags.BigInt) != 0) return ParseBigIntLiteralType(str);
                                    if ((left.flags & TypeFlags.BigIntLiteral) != 0) return left;
                                    if ((right.flags & TypeFlags.BigIntLiteral) != 0 && GetBigIntLiteralValue(right).ToString() == str) return right;
                                    if ((left.flags & TypeFlags.Boolean) != 0) return left;
                                    if ((right.flags & TypeFlags.Boolean) != 0)
                                    {
                                        if (str == "true") return trueType!;
                                        if (str == "false") return falseType!;
                                        return booleanType!;
                                    }
                                    if ((left.flags & TypeFlags.BooleanLiteral) != 0) return left;
                                    if ((right.flags & TypeFlags.BooleanLiteral) != 0 && (GetBooleanLiteralValue(right) ? "true" : "false") == str) return right;
                                    if ((left.flags & TypeFlags.Undefined) != 0) return left;
                                    if ((right.flags & TypeFlags.Undefined) != 0 && right.AsIntrinsicType().intrinsicName == str) return right;
                                    if ((left.flags & TypeFlags.Null) != 0) return left;
                                    if ((right.flags & TypeFlags.Null) != 0 && right.AsIntrinsicType().intrinsicName == str) return right;
                                    return left;
                                }
                                var matchingType = neverType!;
                                foreach (var t in constraint.Distributed())
                                {
                                    matchingType = Choose(matchingType, t);
                                }
                                if ((matchingType.flags & TypeFlags.Never) == 0)
                                {
                                    InferFromTypes(n, matchingType, targetType);
                                    continue;
                                }
                            }
                        }
                    }
                }
                InferFromTypes(n, src, targetType);
            }
        }
    }

    // inference.go:653-663
    internal void InferFromGenericMappedTypes(InferenceState n, Type source, Type target)
    {
        // The source and target types are generic types { [P in S]: X } and { [P in T]: Y }, so we infer
        // from S to T and from X to Y.
        InferFromTypes(n, GetConstraintTypeFromMappedType(source), GetConstraintTypeFromMappedType(target));
        InferFromTypes(n, GetTemplateTypeFromMappedType(source), GetTemplateTypeFromMappedType(target));
        var sourceNameType = GetNameTypeFromMappedType(source);
        var targetNameType = GetNameTypeFromMappedType(target);
        if (sourceNameType is not null && targetNameType is not null)
        {
            InferFromTypes(n, sourceNameType, targetNameType);
        }
    }

    // inference.go:665-792
    internal void InferFromObjectTypes(InferenceState n, Type source, Type target)
    {
        if ((source.objectFlags & ObjectFlags.Reference) != 0 && (target.objectFlags & ObjectFlags.Reference) != 0 && (source.Target() == target.Target() || (IsArrayType(source) && IsArrayType(target))))
        {
            // If source and target are references to the same generic type, infer from type arguments
            InferFromTypeArguments(n, GetTypeArguments(source), GetTypeArguments(target), GetVariances(source.Target()));
            return;
        }
        if (IsGenericMappedType(source) && IsGenericMappedType(target))
        {
            InferFromGenericMappedTypes(n, source, target);
        }
        if ((target.objectFlags & ObjectFlags.Mapped) != 0 && target.AsMappedType().declaration!.NameType is null)
        {
            var constraintType = GetConstraintTypeFromMappedType(target);
            if (InferToMappedType(n, source, target, constraintType))
            {
                return;
            }
        }
        // Infer from the members of source and target only if the two types are possibly related
        if (TypesDefinitelyUnrelated(source, target))
        {
            return;
        }
        if (IsArrayOrTupleType(source))
        {
            if (IsTupleType(target))
            {
                var sourceArity = GetTypeReferenceArity(source);
                var targetArity = GetTypeReferenceArity(target);
                var elementTypes = GetTypeArguments(target);
                var elementInfos = target.TargetTupleType().elementInfos!;
                // When source and target are tuple types with the same structure (fixed, variadic, and rest are matched
                // to the same kind in each position), simply infer between the element types.
                if (IsTupleType(source) && IsTupleTypeStructureMatching(source, target))
                {
                    for (int i = 0; i < targetArity; i++)
                    {
                        InferFromTypes(n, GetTypeArguments(source)[i], elementTypes[i]);
                    }
                    return;
                }
                var startLength = 0;
                var endLength = 0;
                if (IsTupleType(source))
                {
                    startLength = Math.Min(source.TargetTupleType().fixedLength, target.TargetTupleType().fixedLength);
                    if ((target.TargetTupleType().combinedFlags & ElementFlags.Variable) != 0)
                    {
                        endLength = Math.Min(GetEndElementCount(source.TargetTupleType(), ElementFlags.Fixed), GetEndElementCount(target.TargetTupleType(), ElementFlags.Fixed));
                    }
                }
                // Infer between starting fixed elements.
                for (int i = 0; i < startLength; i++)
                {
                    InferFromTypes(n, GetTypeArguments(source)[i], elementTypes[i]);
                }
                if (!IsTupleType(source) || (sourceArity - startLength - endLength == 1 && (source.TargetTupleType().elementInfos![startLength].flags & ElementFlags.Rest) != 0))
                {
                    // Single rest element remains in source, infer from that to every element in target
                    var restType = GetTypeArguments(source)[startLength];
                    for (int i = startLength; i < targetArity - endLength; i++)
                    {
                        var t = restType;
                        if ((elementInfos[i].flags & ElementFlags.Variadic) != 0)
                        {
                            t = CreateArrayType(t);
                        }
                        InferFromTypes(n, t, elementTypes[i]);
                    }
                }
                else
                {
                    var middleLength = targetArity - startLength - endLength;
                    if (middleLength == 2)
                    {
                        if ((elementInfos[startLength].flags & elementInfos[startLength + 1].flags & ElementFlags.Variadic) != 0)
                        {
                            // Middle of target is [...T, ...U] and source is tuple type
                            var targetInfo = GetInferenceInfoForType(n, elementTypes[startLength]);
                            if (targetInfo is not null && targetInfo.impliedArity >= 0)
                            {
                                // Infer slices from source based on implied arity of T.
                                InferFromTypes(n, SliceTupleType(source, startLength, endLength + sourceArity - targetInfo.impliedArity), elementTypes[startLength]);
                                InferFromTypes(n, SliceTupleType(source, startLength + targetInfo.impliedArity, endLength), elementTypes[startLength + 1]);
                            }
                        }
                        else if ((elementInfos[startLength].flags & ElementFlags.Variadic) != 0 && (elementInfos[startLength + 1].flags & ElementFlags.Rest) != 0)
                        {
                            // Middle of target is [...T, ...rest] and source is tuple type
                            // if T is constrained by a fixed-size tuple we might be able to use its arity to infer T
                            var info = GetInferenceInfoForType(n, elementTypes[startLength]);
                            if (info is not null)
                            {
                                var constraint = GetBaseConstraintOfType(info.typeParameter);
                                if (constraint is not null && IsTupleType(constraint) && (constraint.TargetTupleType().combinedFlags & ElementFlags.Variable) == 0)
                                {
                                    var impliedArity = constraint.TargetTupleType().fixedLength;
                                    InferFromTypes(n, SliceTupleType(source, startLength, sourceArity - (startLength + impliedArity)), elementTypes[startLength]);
                                    var restType = GetElementTypeOfSliceOfTupleType(source, startLength + impliedArity, endLength, false, false);
                                    if (restType is not null)
                                    {
                                        InferFromTypes(n, restType, elementTypes[startLength + 1]);
                                    }
                                }
                            }
                        }
                        else if ((elementInfos[startLength].flags & ElementFlags.Rest) != 0 && (elementInfos[startLength + 1].flags & ElementFlags.Variadic) != 0)
                        {
                            // Middle of target is [...rest, ...T] and source is tuple type
                            // if T is constrained by a fixed-size tuple we might be able to use its arity to infer T
                            var info = GetInferenceInfoForType(n, elementTypes[startLength + 1]);
                            if (info is not null)
                            {
                                var constraint = GetBaseConstraintOfType(info.typeParameter);
                                if (constraint is not null && IsTupleType(constraint) && (constraint.TargetTupleType().combinedFlags & ElementFlags.Variable) == 0)
                                {
                                    var impliedArity = constraint.TargetTupleType().fixedLength;
                                    var endIndex = sourceArity - GetEndElementCount(target.TargetTupleType(), ElementFlags.Fixed);
                                    var startIndex = endIndex - impliedArity;
                                    if (startIndex >= startLength)
                                    {
                                        var trailingSlice = CreateTupleTypeEx(GetTypeArguments(source)[startIndex..endIndex], source.TargetTupleType().elementInfos![startIndex..endIndex], false /*readonly*/);
                                        var restType = GetElementTypeOfSliceOfTupleType(source, startLength, endLength + impliedArity, false, false);
                                        if (restType is not null)
                                        {
                                            InferFromTypes(n, restType, elementTypes[startLength]);
                                        }
                                        InferFromTypes(n, trailingSlice, elementTypes[startLength + 1]);
                                    }
                                }
                            }
                        }
                    }
                    else if (middleLength == 1 && (elementInfos[startLength].flags & ElementFlags.Variadic) != 0)
                    {
                        // Middle of target is exactly one variadic element. Infer the slice between the fixed parts in the source.
                        // If target ends in optional element(s), make a lower priority a speculative inference.
                        var priority = (elementInfos[targetArity - 1].flags & ElementFlags.Optional) != 0 ? InferencePriority.SpeculativeTuple : InferencePriority.None;
                        var sourceSlice = SliceTupleType(source, startLength, endLength);
                        InferWithPriority(n, sourceSlice, elementTypes[startLength], priority);
                    }
                    else if (middleLength == 1 && (elementInfos[startLength].flags & ElementFlags.Rest) != 0)
                    {
                        // Middle of target is exactly one rest element. If middle of source is not empty, infer union of middle element types.
                        var restType = GetElementTypeOfSliceOfTupleType(source, startLength, endLength, false, false);
                        if (restType is not null)
                        {
                            InferFromTypes(n, restType, elementTypes[startLength]);
                        }
                    }
                }
                // Infer between ending fixed elements
                for (int i = 0; i < endLength; i++)
                {
                    InferFromTypes(n, GetTypeArguments(source)[sourceArity - i - 1], elementTypes[targetArity - i - 1]);
                }
                return;
            }
            if (IsArrayType(target))
            {
                InferFromIndexTypes(n, source, target);
                return;
            }
        }
        InferFromProperties(n, source, target);
        InferFromSignatures(n, source, target, SignatureKind.Call);
        InferFromSignatures(n, source, target, SignatureKind.Construct);
        InferFromIndexTypes(n, source, target);
    }

    // inference.go:794-802
    internal void InferFromProperties(InferenceState n, Type source, Type target)
    {
        var properties = GetPropertiesOfObjectType(target);
        foreach (var targetProp in properties)
        {
            var sourceProp = GetPropertyOfType(source, targetProp.Name);
            if (sourceProp is not null && !CoreUtil.Some(sourceProp.Declarations, IsSkipDirectInferenceNode))
            {
                InferFromTypes(n, RemoveMissingType(GetTypeOfSymbol(sourceProp), (sourceProp.Flags & SymbolFlags.Optional) != 0), RemoveMissingType(GetTypeOfSymbol(targetProp), (targetProp.Flags & SymbolFlags.Optional) != 0));
            }
        }
    }

    // inference.go:804-817
    internal void InferFromSignatures(InferenceState n, Type source, Type target, SignatureKind kind)
    {
        var sourceSignatures = GetSignaturesOfType(source, kind);
        var sourceLen = sourceSignatures.Length;
        if (sourceLen > 0)
        {
            // We match source and target signatures from the bottom up, and if the source has fewer signatures
            // than the target, we infer from the first source signature to the excess target signatures.
            var targetSignatures = GetSignaturesOfType(target, kind);
            var targetLen = targetSignatures.Length;
            for (int i = 0; i < targetLen; i++)
            {
                var sourceIndex = Math.Max(sourceLen - targetLen + i, 0);
                InferFromSignature(n, GetBaseSignature(sourceSignatures[sourceIndex]), GetErasedSignature(targetSignatures[i]));
            }
        }
    }

    // inference.go:819-832
    internal void InferFromSignature(InferenceState n, Signature source, Signature target)
    {
        if ((source.flags & SignatureFlags.IsNonInferrable) == 0)
        {
            var saveBivariant = n.bivariant;
            var kind = SyntaxKind.Unknown;
            if (target.declaration is not null)
            {
                kind = target.declaration.Kind;
            }
            // Once we descend into a bivariant signature we remain bivariant for all nested inferences
            n.bivariant = n.bivariant || kind == SyntaxKind.MethodDeclaration || kind == SyntaxKind.MethodSignature || kind == SyntaxKind.Constructor;
            ApplyToParameterTypes(source, target, (s, t) => InferFromContravariantTypesIfStrictFunctionTypes(n, s, t));
            n.bivariant = saveBivariant;
        }
        ApplyToReturnTypes(source, target, (s, t) => InferFromTypes(n, s, t));
    }

    // inference.go:834-860
    internal void ApplyToParameterTypes(Signature source, Signature target, Action<Type, Type> callback)
    {
        var sourceCount = GetParameterCount(source);
        var targetCount = GetParameterCount(target);
        var sourceRestType = GetEffectiveRestType(source);
        var targetRestType = GetEffectiveRestType(target);
        var targetNonRestCount = targetCount;
        if (targetRestType is not null)
        {
            targetNonRestCount--;
        }
        var paramCount = targetNonRestCount;
        if (sourceRestType is null)
        {
            paramCount = Math.Min(sourceCount, targetNonRestCount);
        }
        var sourceThisType = GetThisTypeOfSignature(source);
        if (sourceThisType is not null)
        {
            var targetThisType = GetThisTypeOfSignature(target);
            if (targetThisType is not null)
            {
                callback(sourceThisType, targetThisType);
            }
        }
        for (int i = 0; i < paramCount; i++)
        {
            callback(GetTypeAtPosition(source, i), GetTypeAtPosition(target, i));
        }
        if (targetRestType is not null)
        {
            callback(GetRestTypeAtPosition(source, paramCount, IsConstTypeVariable(targetRestType, 0) && !SomeType(targetRestType, IsMutableArrayLikeType) /*readonly*/), targetRestType);
        }
    }

    // inference.go:862-875
    internal void ApplyToReturnTypes(Signature source, Signature target, Action<Type, Type> callback)
    {
        var targetTypePredicate = GetTypePredicateOfSignature(target);
        if (targetTypePredicate is not null)
        {
            var sourceTypePredicate = GetTypePredicateOfSignature(source);
            if (sourceTypePredicate is not null && TypePredicateKindsMatch(sourceTypePredicate, targetTypePredicate) && sourceTypePredicate.t is not null && targetTypePredicate.t is not null)
            {
                callback(sourceTypePredicate.t, targetTypePredicate.t);
                return;
            }
        }
        var targetReturnType = GetReturnTypeOfSignature(target);
        if (couldContainTypeVariables(targetReturnType))
        {
            callback(GetReturnTypeOfSignature(source), targetReturnType);
        }
    }

    // inference.go:877-912
    internal void InferFromIndexTypes(InferenceState n, Type source, Type target)
    {
        // Inferences across mapped type index signatures are pretty much the same a inferences to homomorphic variables
        var priority = InferencePriority.None;
        if ((source.objectFlags & target.objectFlags & ObjectFlags.Mapped) != 0)
        {
            priority = InferencePriority.HomomorphicMappedType;
        }
        var indexInfos = GetIndexInfosOfType(target);
        if (IsObjectTypeWithInferableIndex(source))
        {
            foreach (var targetInfo in indexInfos)
            {
                Type[]? propTypes = null;
                foreach (var prop in GetPropertiesOfType(source))
                {
                    if (IsApplicableIndexType(GetLiteralTypeFromProperty(prop, TypeFlags.StringOrNumberLiteralOrUnique, false), targetInfo.keyType))
                    {
                        var propType = GetTypeOfSymbol(prop);
                        if ((prop.Flags & SymbolFlags.Optional) != 0)
                        {
                            propType = RemoveMissingOrUndefinedType(propType);
                        }
                        propTypes = SlicesAppend(propTypes, propType);
                    }
                }
                foreach (var info in GetIndexInfosOfType(source))
                {
                    if (IsApplicableIndexType(info.keyType, targetInfo.keyType))
                    {
                        propTypes = SlicesAppend(propTypes, info.valueType);
                    }
                }
                if (propTypes is not null && propTypes.Length != 0)
                {
                    InferWithPriority(n, GetUnionType(propTypes), targetInfo.valueType, priority);
                }
            }
        }
        foreach (var targetInfo in indexInfos)
        {
            var sourceInfo = GetApplicableIndexInfo(source, targetInfo.keyType);
            if (sourceInfo is not null)
            {
                InferWithPriority(n, sourceInfo.valueType, targetInfo.valueType, priority);
            }
        }
    }

    // inference.go:914-964
    internal bool InferToMappedType(InferenceState n, Type source, Type target, Type constraintType)
    {
        if ((constraintType.flags & TypeFlags.Union) != 0 || (constraintType.flags & TypeFlags.Intersection) != 0)
        {
            var result = false;
            foreach (var t in constraintType.Types())
            {
                result = InferToMappedType(n, source, target, t) || result;
            }
            return result;
        }
        if ((constraintType.flags & TypeFlags.Index) != 0)
        {
            // We're inferring from some source type S to a homomorphic mapped type { [P in keyof T]: X },
            // where T is a type variable. Use inferTypeForHomomorphicMappedType to infer a suitable source
            // type and then make a secondary inference from that type to T.
            var inference = GetInferenceInfoForType(n, constraintType.AsIndexType().target);
            if (inference is not null && !inference.isFixed && !IsFromInferenceBlockedSource(source))
            {
                var inferredType = InferTypeForHomomorphicMappedType(source, target, constraintType);
                if (inferredType is not null)
                {
                    // We assign a lower priority to inferences made from types containing non-inferrable
                    // types because we may only have a partial result (i.e. we may have failed to make
                    // reverse inferences for some properties).
                    InferWithPriority(n, inferredType, inference.typeParameter, (source.objectFlags & ObjectFlags.NonInferrableType) != 0 ? InferencePriority.PartialHomomorphicMappedType : InferencePriority.HomomorphicMappedType);
                }
            }
            return true;
        }
        if ((constraintType.flags & TypeFlags.TypeParameter) != 0)
        {
            // We're inferring from some source type S to a mapped type { [P in K]: X }, where K is a type
            // parameter. First infer from 'keyof S' to K.
            InferWithPriority(n, GetIndexTypeEx(source, patternForType.GetValueOrDefault(source) is not null ? IndexFlags.NoIndexSignatures : IndexFlags.None), constraintType, InferencePriority.MappedTypeConstraint);
            // If K is constrained to a type C, also infer to C.
            var extendedConstraint = GetConstraintOfType(constraintType);
            if (extendedConstraint is not null && InferToMappedType(n, source, target, extendedConstraint))
            {
                return true;
            }
            // If no inferences can be made to K's constraint, infer from a union of the property types
            // in the source to the template type X.
            var propTypes = CoreUtil.Map(GetPropertiesOfType(source), GetTypeOfSymbol);
            var indexTypes = CoreUtil.Map(GetIndexInfosOfType(source), info => info != enumNumberIndexInfo ? info.valueType : neverType!);
            InferFromTypes(n, GetUnionType(CoreUtil.Concatenate(propTypes, indexTypes)), GetTemplateTypeFromMappedType(target));
            return true;
        }
        return false;
    }

    // inference.go:970-978 — Infer a suitable input type for a homomorphic mapped type { [P in keyof T]: X }.
    internal Type? InferTypeForHomomorphicMappedType(Type source, Type target, Type constraint)
    {
        var key = new ReverseMappedTypeKey(source.id, target.id, constraint.id);
        var cached = reverseHomomorphicMappedCache.GetValueOrDefault(key);
        if (cached is not null)
        {
            return cached;
        }
        var t = CreateReverseMappedType(source, target, constraint);
        reverseHomomorphicMappedCache[key] = t!;
        return t;
    }

    // inference.go:980-1020
    internal Type? CreateReverseMappedType(Type source, Type target, Type constraint)
    {
        // We consider a source type reverse mappable if it has a string index signature or if
        // it has one or more properties and is of a partially inferable type.
        if (!(GetIndexInfoOfType(source, stringType!) is not null || (GetPropertiesOfType(source).Length != 0 && IsPartiallyInferableType(source))))
        {
            return null;
        }
        // For arrays and tuples we infer new arrays and tuples where the reverse mapping has been
        // applied to the element type(s).
        if (IsArrayType(source))
        {
            var elementType = InferReverseMappedType(GetTypeArguments(source)[0], target, constraint);
            if (elementType is null)
            {
                return null;
            }
            return CreateArrayTypeEx(elementType, IsReadonlyArrayType(source));
        }
        if (IsTupleType(source))
        {
            var elementTypes = CoreUtil.Map(GetElementTypes(source), t => InferReverseMappedType(t, target, constraint));
            if (elementTypes is null || !CoreUtil.Every(elementTypes, t => t is not null))
            {
                return null;
            }
            var elementInfos = source.TargetTupleType().elementInfos!;
            if ((GetMappedTypeModifiers(target) & MappedTypeModifiers.IncludeOptional) != 0)
            {
                elementInfos = CoreUtil.SameMap(elementInfos, info =>
                {
                    if ((info.flags & ElementFlags.Optional) != 0)
                    {
                        return new TupleElementInfo(ElementFlags.Required, info.labeledDeclaration);
                    }
                    return info;
                })!;
            }
            return CreateTupleTypeEx(Array.ConvertAll(elementTypes, t => t!), elementInfos, source.TargetTupleType().readonlyElements);
        }
        // For all other object types we infer a new object type where the reverse mapping has been
        // applied to the type of each property.
        var reversed = NewObjectType(ObjectFlags.ReverseMapped | ObjectFlags.Anonymous, null /*symbol*/);
        reversed.AsReverseMappedType().source = source;
        reversed.AsReverseMappedType().mappedType = target;
        reversed.AsReverseMappedType().constraintType = constraint;
        return reversed;
    }

    // inference.go:1026-1030 — partially inferable type predicate.
    internal bool IsPartiallyInferableType(Type t)
    {
        return (t.objectFlags & ObjectFlags.NonInferrableType) == 0 ||
            (IsObjectLiteralType(t) && CoreUtil.Some(GetPropertiesOfType(t), prop => IsPartiallyInferableType(GetTypeOfSymbol(prop)))) ||
            (IsTupleType(t) && CoreUtil.Some(GetElementTypes(t), IsPartiallyInferableType));
    }

    // inference.go:1032-1055
    internal Type? InferReverseMappedType(Type source, Type target, Type constraint)
    {
        var key = new ReverseMappedTypeKey(source.id, target.id, constraint.id);
        if (reverseMappedCache.TryGetValue(key, out var cached))
        {
            return cached ?? unknownType;
        }
        reverseMappedSourceStack.Add(source);
        reverseMappedTargetStack.Add(target);
        var saveExpandingFlags = reverseExpandingFlags;
        if (IsDeeplyNestedType(source, reverseMappedSourceStack, 2))
        {
            reverseExpandingFlags |= ExpandingFlags.Source;
        }
        if (IsDeeplyNestedType(target, reverseMappedTargetStack, 2))
        {
            reverseExpandingFlags |= ExpandingFlags.Target;
        }
        Type? t = null;
        if (reverseExpandingFlags != ExpandingFlags.Both)
        {
            t = InferReverseMappedTypeWorker(source, target, constraint);
        }
        reverseMappedSourceStack.RemoveAt(reverseMappedSourceStack.Count - 1);
        reverseMappedTargetStack.RemoveAt(reverseMappedTargetStack.Count - 1);
        reverseExpandingFlags = saveExpandingFlags;
        reverseMappedCache[key] = t!;
        return t;
    }

    // inference.go:1057-1063
    internal Type InferReverseMappedTypeWorker(Type source, Type target, Type constraint)
    {
        var typeParameter = GetIndexedAccessType(constraint.AsIndexType().target, GetTypeParameterFromMappedType(target));
        var templateType = GetTemplateTypeFromMappedType(target);
        var inference = NewInferenceInfo(typeParameter);
        InferTypes([inference], source, templateType, InferencePriority.None, false);
        return GetWidenedType(GetTypeFromInference(inference) ?? unknownType!);
    }

    // inference.go:1065-1109
    internal void ResolveReverseMappedTypeMembers(Type t)
    {
        var r = t.AsReverseMappedType();
        var indexInfo = GetIndexInfoOfType(r.source!, stringType!);
        var modifiers = GetMappedTypeModifiers(r.mappedType!);
        var readonlyMask = (modifiers & MappedTypeModifiers.IncludeReadonly) == 0;
        var optionalMask = (modifiers & MappedTypeModifiers.IncludeOptional) != 0 ? SymbolFlags.None : SymbolFlags.Optional;
        IndexInfo[]? indexInfos = null;
        if (indexInfo is not null)
        {
            indexInfos = [NewIndexInfo(stringType!, InferReverseMappedType(indexInfo.valueType, r.mappedType!, r.constraintType!) ?? unknownType!, readonlyMask && indexInfo.isReadonly, null, null)];
        }
        var members = new SymbolTable();
        var limitedConstraint = GetLimitedConstraint(t);
        foreach (var prop in GetPropertiesOfType(r.source!))
        {
            // In case of a reverse mapped type with an intersection constraint, if we were able to
            // extract the filtering type literals we skip those properties that are not assignable to them.
            if (limitedConstraint is not null)
            {
                var propertyNameType = GetLiteralTypeFromProperty(prop, TypeFlags.StringOrNumberLiteralOrUnique, false);
                if (!IsTypeAssignableTo(propertyNameType, limitedConstraint))
                {
                    continue;
                }
            }
            var checkFlags = CheckFlags.ReverseMapped | (readonlyMask && IsReadonlySymbol(prop) ? CheckFlags.Readonly : CheckFlags.None);
            var inferredProp = NewSymbolEx(SymbolFlags.Property | (prop.Flags & optionalMask), prop.Name, checkFlags);
            inferredProp.Declarations = prop.Declarations;
            valueSymbolLinks.Get(inferredProp).nameType = valueSymbolLinks.Get(prop).nameType;
            var links = reverseMappedSymbolLinks.Get(inferredProp);
            links.propertyType = GetTypeOfSymbol(prop);
            var constraintTarget = r.constraintType!.AsIndexType().target;
            if ((constraintTarget.flags & TypeFlags.IndexedAccess) != 0 && (constraintTarget.AsIndexedAccessType().objectType.flags & TypeFlags.TypeParameter) != 0 && (constraintTarget.AsIndexedAccessType().indexType.flags & TypeFlags.TypeParameter) != 0)
            {
                // A reverse mapping of `{[K in keyof T[K_1]]: T[K_1]}` is the same as that of `{[K in keyof T]: T}`;
                // simplify such indexed access occurrences to reduce the number of type identities produced.
                var newTypeParam = constraintTarget.AsIndexedAccessType().objectType;
                var newMappedType = ReplaceIndexedAccess(r.mappedType!, constraintTarget, newTypeParam);
                links.mappedType = newMappedType;
                links.constraintType = GetIndexType(newTypeParam);
            }
            else
            {
                links.mappedType = r.mappedType;
                links.constraintType = r.constraintType;
            }
            members[prop.Name] = inferredProp;
        }
        SetStructuredTypeMembers(t, members, null, null, indexInfos);
    }

    // inference.go:1111-1118
    internal Type GetTypeOfReverseMappedSymbol(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType is null)
        {
            var reverseLinks = reverseMappedSymbolLinks.Get(symbol);
            links.resolvedType = InferReverseMappedType(reverseLinks.propertyType!, reverseLinks.mappedType!, reverseLinks.constraintType!) ?? unknownType;
        }
        return links.resolvedType!;
    }

    // inference.go:1124-1142
    internal Type? GetLimitedConstraint(Type t)
    {
        var constraint = GetConstraintTypeFromMappedType(t.AsReverseMappedType().mappedType!);
        if (!((constraint.flags & TypeFlags.Union) != 0 || (constraint.flags & TypeFlags.Intersection) != 0))
        {
            return null;
        }
        var origin = constraint;
        if ((constraint.flags & TypeFlags.Union) != 0)
        {
            origin = constraint.AsUnionType().origin!;
        }
        if (origin is null || (origin.flags & TypeFlags.Intersection) == 0)
        {
            return null;
        }
        var constraintType = t.AsReverseMappedType().constraintType;
        var limitedConstraint = GetIntersectionType(CoreUtil.Filter(origin.Types(), tt => tt != constraintType));
        if (limitedConstraint != neverType)
        {
            return limitedConstraint;
        }
        return null;
    }

    // inference.go:1144-1149
    internal Type ReplaceIndexedAccess(Type instantiable, Type t, Type replacement)
    {
        // map type.indexType to 0
        // map type.objectType to `[TReplacement]`
        // thus making the indexed access `[TReplacement][0]` or `TReplacement`
        return InstantiateType(instantiable, TypeMapper.NewTypeMapper(
            [t.AsIndexedAccessType().indexType, t.AsIndexedAccessType().objectType],
            [GetNumberLiteralType(new global::Ts.JsNum.Number(0)), CreateTupleType([replacement])]));
    }

    // inference.go:1151-1159
    internal bool TypesDefinitelyUnrelated(Type source, Type target)
    {
        // Two tuple types with incompatible arities are definitely unrelated.
        // Two object types that each have a property that is unmatched in the other are definitely unrelated.
        if (IsTupleType(source) && IsTupleType(target))
        {
            return TupleTypesDefinitelyUnrelated(source, target);
        }
        return GetUnmatchedProperty(source, target, false /*requireOptionalProperties*/, true /*matchDiscriminantProperties*/) is not null &&
            GetUnmatchedProperty(target, source, false /*requireOptionalProperties*/, false /*matchDiscriminantProperties*/) is not null;
    }

    // inference.go:1161-1166
    internal static bool TupleTypesDefinitelyUnrelated(Type source, Type target)
    {
        var s = source.TargetTupleType();
        var t = target.TargetTupleType();
        return ((t.combinedFlags & ElementFlags.Variadic) == 0 && t.minLength > s.minLength) ||
            ((t.combinedFlags & ElementFlags.Variable) == 0 && ((s.combinedFlags & ElementFlags.Variable) != 0 || t.fixedLength < s.fixedLength));
    }

    // inference.go:1168-1178
    internal bool IsTupleTypeStructureMatching(Type t1, Type t2)
    {
        if (GetTypeReferenceArity(t1) != GetTypeReferenceArity(t2))
        {
            return false;
        }
        var infos1 = t1.TargetTupleType().elementInfos!;
        var infos2 = t2.TargetTupleType().elementInfos!;
        for (int i = 0; i < infos1.Length; i++)
        {
            if ((infos1[i].flags & ElementFlags.Variable) != (infos2[i].flags & ElementFlags.Variable))
            {
                return false;
            }
        }
        return true;
    }

    // inference.go:1180-1187
    internal bool IsTypeOrBaseIdenticalTo(Type s, Type t)
    {
        if (t == missingType)
        {
            return s == t;
        }
        return IsTypeIdenticalTo(s, t) ||
            ((t.flags & TypeFlags.String) != 0 && (s.flags & TypeFlags.StringLiteral) != 0) ||
            ((t.flags & TypeFlags.Number) != 0 && (s.flags & TypeFlags.NumberLiteral) != 0);
    }

    // inference.go:1189-1192
    internal bool IsTypeCloselyMatchedBy(Type s, Type t)
    {
        return ((s.flags & TypeFlags.Object) != 0 && (t.flags & TypeFlags.Object) != 0 && s.symbol is not null && s.symbol == t.symbol) ||
            (s.alias is not null && t.alias is not null && (s.alias.typeArguments?.Length ?? 0) != 0 && s.alias.symbol == t.alias.symbol);
    }

    // inference.go:1195-1215 — Create an object with properties named in the string literal type. Every property has type `any`.
    internal Type CreateEmptyObjectTypeFromStringLiteral(Type t)
    {
        var members = new SymbolTable();
        foreach (var d in t.Distributed())
        {
            if ((d.flags & TypeFlags.StringLiteral) == 0)
            {
                continue;
            }
            var name = GetStringLiteralValue(d);
            var literalProp = NewSymbol(SymbolFlags.Property, name);
            valueSymbolLinks.Get(literalProp).resolvedType = anyType;
            if (d.symbol is not null)
            {
                literalProp.Declarations = d.symbol.Declarations;
                literalProp.ValueDeclaration = d.symbol.ValueDeclaration;
            }
            members[name] = literalProp;
        }
        IndexInfo[]? indexInfos = null;
        if ((t.flags & TypeFlags.String) != 0)
        {
            indexInfos = [NewIndexInfo(stringType!, emptyObjectType!, false /*isReadonly*/, null, null)];
        }
        return NewAnonymousType(null, members, null, null, indexInfos);
    }

    // inference.go:1217-1222
    internal InferenceContext NewInferenceContext(Type[]? typeParameters, Signature? signature, InferenceFlags flags, TypeComparer? compareTypes)
    {
        compareTypes ??= compareTypesAssignable;
        return NewInferenceContextWorker(CoreUtil.Map(typeParameters, NewInferenceInfo) ?? [], signature, flags, compareTypes);
    }

    // inference.go:1224-1229
    internal InferenceContext? CloneInferenceContext(InferenceContext? n, InferenceFlags extraFlags)
    {
        if (n is null)
        {
            return null;
        }
        return NewInferenceContextWorker(CoreUtil.Map(n.inferences, CloneInferenceInfo)!, n.signature, n.flags | extraFlags, n.compareTypes);
    }

    // inference.go:1231-1237
    internal InferenceContext? CloneInferredPartOfContext(InferenceContext n)
    {
        var inferences = CoreUtil.Filter(n.inferences, HasInferenceCandidates);
        if (inferences is null || inferences.Length == 0)
        {
            return null;
        }
        return NewInferenceContextWorker(CoreUtil.Map(inferences, CloneInferenceInfo)!, n.signature, n.flags, n.compareTypes);
    }

    // inference.go:1239-1249
    internal InferenceContext NewInferenceContextWorker(InferenceInfo[] inferences, Signature? signature, InferenceFlags flags, TypeComparer compareTypes)
    {
        var n = new InferenceContext
        {
            inferences = inferences,
            signature = signature,
            flags = flags,
            compareTypes = compareTypes,
        };
        n.mapper = NewInferenceTypeMapper(n, true /*fixing*/);
        n.nonFixingMapper = NewInferenceTypeMapper(n, false /*fixing*/);
        return n;
    }

    // inference.go:1251-1253
    internal void AddIntraExpressionInferenceSite(InferenceContext n, Node node, Type t)
    {
        n.intraExpressionInferenceSites = SlicesAppend(n.intraExpressionInferenceSites, new IntraExpressionInferenceSite { node = node, t = t });
    }

    // inference.go:1268-1281 — commit intra-expression inferences early (see Go comment for the motivating case).
    internal void InferFromIntraExpressionSites(InferenceContext n)
    {
        foreach (var site in n.intraExpressionInferenceSites ?? [])
        {
            Type? contextualType;
            if (NodeTests.IsMethodDeclaration(site.node!))
            {
                contextualType = GetContextualTypeForObjectLiteralMethod(site.node!, ContextFlags.NoConstraints);
            }
            else
            {
                contextualType = GetContextualType(site.node!, ContextFlags.NoConstraints);
            }
            if (contextualType is not null)
            {
                InferTypes(n.inferences, site.t!, contextualType, InferencePriority.None, false);
            }
        }
        n.intraExpressionInferenceSites = null;
    }

    // inference.go:1283-1370
    internal Type GetInferredType(InferenceContext n, int index)
    {
        var inference = n.inferences[index];
        if (inference.inferredType is null)
        {
            if (inference.typeParameter == errorType)
            {
                return inference.typeParameter;
            }
            Type? inferredType = null;
            Type? fallbackType = null;
            if (n.signature is not null)
            {
                Type? inferredCovariantType = null;
                if ((inference.candidates?.Length ?? 0) != 0)
                {
                    inferredCovariantType = GetCovariantInference(inference, n.signature);
                }
                Type? inferredContravariantType = null;
                if ((inference.contraCandidates?.Length ?? 0) != 0)
                {
                    inferredContravariantType = GetContravariantInference(inference);
                }
                if (inferredCovariantType is not null || inferredContravariantType is not null)
                {
                    // If we have both co- and contra-variant inferences, we prefer the co-variant inference under
                    // the conditions in the Go comment; otherwise we prefer the contra-variant inference.
                    // inferredCovariantType is non-null here (the `!` inside the lambdas is guarded by the
                    // leading `is not null`, which C# flow analysis does not carry into captured lambdas).
                    var preferCovariantType = inferredCovariantType is not null && (inferredContravariantType is null ||
                        ((inferredCovariantType.flags & (TypeFlags.Never | TypeFlags.Any)) == 0 &&
                            CoreUtil.Some(inference.contraCandidates, t => IsTypeAssignableTo(inferredCovariantType!, t)) &&
                            CoreUtil.Every(n.inferences, other =>
                                (other != inference && GetConstraintOfTypeParameter(other.typeParameter) != inference.typeParameter) ||
                                CoreUtil.Every(other.candidates, t => IsTypeAssignableTo(t, inferredCovariantType!)))));
                    if (preferCovariantType)
                    {
                        inferredType = inferredCovariantType;
                        fallbackType = inferredContravariantType;
                    }
                    else
                    {
                        inferredType = inferredContravariantType;
                        fallbackType = inferredCovariantType;
                    }
                }
                else if ((n.flags & InferenceFlags.NoDefault) != 0)
                {
                    // We use silentNeverType as the wildcard that signals no inferences.
                    inferredType = silentNeverType;
                }
                else
                {
                    // Infer either the default or the empty object type when no inferences were made.
                    var defaultType = GetDefaultFromTypeParameter(inference.typeParameter);
                    if (defaultType is not null)
                    {
                        // Instantiate the default type. Any forward reference to a type parameter should be
                        // instantiated to the empty object type.
                        inferredType = InstantiateType(defaultType, TypeMapper.MergeTypeMappers(NewBackreferenceMapper(n, index), n.nonFixingMapper!));
                    }
                }
            }
            else
            {
                inferredType = GetTypeFromInference(inference);
            }
            inference.inferredType = inferredType;
            if (inference.inferredType is null)
            {
                inference.inferredType = (n.flags & InferenceFlags.AnyDefault) != 0 ? anyType : unknownType;
            }
            var constraint = GetConstraintOfTypeParameter(inference.typeParameter);
            if (constraint is not null)
            {
                var instantiatedConstraint = InstantiateType(constraint, n.nonFixingMapper);
                if (inferredType is not null)
                {
                    var constraintWithThis = GetTypeWithThisArgument(instantiatedConstraint, inferredType, false);
                    if (n.compareTypes(inferredType, constraintWithThis, false) == Ternary.False)
                    {
                        Type? filteredByConstraint = null;
                        if (inference.priority == InferencePriority.ReturnType)
                        {
                            // If we have a pure return type inference, we may succeed by removing constituents of the inferred type
                            // that aren't assignable to the constraint type (pure return type inferences are speculation anyway).
                            filteredByConstraint = MapType(inferredType, t =>
                                n.compareTypes(t, constraintWithThis, false) != Ternary.False ? t : neverType!);
                        }
                        inferredType = filteredByConstraint is not null && (filteredByConstraint.flags & TypeFlags.Never) == 0 ? filteredByConstraint : null;
                    }
                }
                if (inferredType is null)
                {
                    // If the fallback type satisfies the constraint, we pick it. Otherwise, we pick the constraint.
                    inferredType = fallbackType is not null && n.compareTypes(fallbackType, GetTypeWithThisArgument(instantiatedConstraint, fallbackType, false), false) != Ternary.False ? fallbackType : instantiatedConstraint;
                }
                inference.inferredType = inferredType;
            }
            ClearActiveMapperCaches();
        }
        return inference.inferredType!;
    }

    // inference.go:1372-1378
    internal Type[] GetInferredTypes(InferenceContext n)
    {
        var result = new Type[n.inferences.Length];
        for (int i = 0; i < n.inferences.Length; i++)
        {
            result[i] = GetInferredType(n, i);
        }
        return result;
    }

    // inference.go:1380-1385
    internal TypeMapper? GetMapperFromContext(InferenceContext? n)
    {
        if (n is null)
        {
            return null;
        }
        return n.mapper;
    }

    // inference.go:1387-1398 — return mapper combining the context's return mapper with a mapper that
    // erases any additional type parameters to their inferences at the time of creation.
    internal TypeMapper CreateOuterReturnMapper(InferenceContext context)
    {
        if (context.outerReturnMapper is null)
        {
            var mapper = CloneInferenceContext(context, InferenceFlags.None)!.mapper!;
            if (context.returnMapper is not null)
            {
                mapper = TypeMapper.NewMergedTypeMapper(context.returnMapper, mapper);
            }
            context.outerReturnMapper = mapper;
        }
        return context.outerReturnMapper;
    }

    // inference.go:1400-1427
    internal Type GetCovariantInference(InferenceInfo inference, Signature signature)
    {
        // Extract all object and array literal types and replace them with a single widened and normalized type.
        var candidates = UnionObjectAndArrayLiteralCandidates(inference.candidates!);
        // We widen inferred literal types per the conditions in the Go comment.
        var primitiveConstraint = HasPrimitiveConstraint(inference.typeParameter) || IsConstTypeVariable(inference.typeParameter, 0);
        var widenLiteralTypes = !primitiveConstraint && inference.topLevel && (inference.isFixed || !IsTypeParameterAtTopLevelInReturnType(signature, inference.typeParameter));
        Type[]? baseCandidates;
        if (primitiveConstraint)
        {
            baseCandidates = CoreUtil.SameMap(candidates, GetRegularTypeOfLiteralType);
        }
        else if (widenLiteralTypes)
        {
            baseCandidates = CoreUtil.SameMap(candidates, GetWidenedLiteralType);
        }
        else
        {
            baseCandidates = candidates;
        }
        // If all inferences were made from a position that implies a combined result, infer a union type.
        // Otherwise, infer a common supertype.
        Type unwidenedType;
        if ((inference.priority & InferencePriority.PriorityImpliesCombination) != 0)
        {
            unwidenedType = GetUnionTypeEx(baseCandidates, UnionReduction.Subtype, null, null);
        }
        else
        {
            unwidenedType = GetCommonSupertype(baseCandidates!);
        }
        return GetWidenedType(unwidenedType);
    }

    // inference.go:1429-1434
    internal Type GetContravariantInference(InferenceInfo inference)
    {
        if ((inference.priority & InferencePriority.PriorityImpliesCombination) != 0)
        {
            return GetIntersectionType(inference.contraCandidates);
        }
        return GetCommonSubtype(inference.contraCandidates!);
    }

    // inference.go:1436-1446
    internal Type[] UnionObjectAndArrayLiteralCandidates(Type[] candidates)
    {
        if (candidates.Length > 1)
        {
            var objectLiterals = CoreUtil.Filter(candidates, IsObjectOrArrayLiteralType);
            if (objectLiterals is not null && objectLiterals.Length != 0)
            {
                var literalsType = GetUnionTypeEx(objectLiterals, UnionReduction.Subtype, null, null);
                var nonLiteralTypes = CoreUtil.Filter(candidates, t => !IsObjectOrArrayLiteralType(t));
                return CoreUtil.Concatenate(nonLiteralTypes, [literalsType])!;
            }
        }
        return candidates;
    }

    // inference.go:1448-1457
    internal bool HasPrimitiveConstraint(Type t)
    {
        var constraint = GetConstraintOfTypeParameter(t);
        if (constraint is not null)
        {
            if ((constraint.flags & TypeFlags.Conditional) != 0)
            {
                constraint = GetDefaultConstraintOfConditionalType(constraint);
            }
            return MaybeTypeOfKind(constraint, TypeFlags.Primitive | TypeFlags.Index | TypeFlags.TemplateLiteral | TypeFlags.StringMapping);
        }
        return false;
    }

    // inference.go:1459-1465
    internal bool IsTypeParameterAtTopLevel(Type t, Type tp, int depth)
    {
        return t == tp ||
            ((t.flags & TypeFlags.UnionOrIntersection) != 0 && CoreUtil.Some(t.Types(), tt => IsTypeParameterAtTopLevel(tt, tp, depth))) ||
            (depth < 3 && (t.flags & TypeFlags.Conditional) != 0 &&
                (IsTypeParameterAtTopLevel(GetTrueTypeFromConditionalType(t), tp, depth + 1) ||
                    IsTypeParameterAtTopLevel(GetFalseTypeFromConditionalType(t), tp, depth + 1)));
    }

    // inference.go:1467-1473
    internal bool IsTypeParameterAtTopLevelInReturnType(Signature signature, Type typeParameter)
    {
        var typePredicate = GetTypePredicateOfSignature(signature);
        if (typePredicate is not null)
        {
            return typePredicate.t is not null && IsTypeParameterAtTopLevel(typePredicate.t, typeParameter, 0);
        }
        return IsTypeParameterAtTopLevel(GetReturnTypeOfSignature(signature), typeParameter, 0);
    }

    // inference.go:1475-1483
    internal Type? GetTypeFromInference(InferenceInfo inference)
    {
        if ((inference.candidates?.Length ?? 0) != 0)
        {
            return GetUnionTypeEx(inference.candidates, UnionReduction.Subtype, null, null);
        }
        if ((inference.contraCandidates?.Length ?? 0) != 0)
        {
            return GetIntersectionType(inference.contraCandidates);
        }
        return null;
    }

    // inference.go:1485-1494
    internal static InferenceInfo? GetInferenceInfoForType(InferenceState n, Type t)
    {
        if ((t.flags & TypeFlags.TypeVariable) != 0)
        {
            foreach (var inference in n.inferences)
            {
                if (t == inference.typeParameter)
                {
                    return inference;
                }
            }
        }
        return null;
    }

    // inference.go:1496-1521
    internal Type GetCommonSupertype(Type[] types)
    {
        if (types.Length == 1)
        {
            return types[0];
        }
        // Remove nullable types from each of the candidates.
        var primaryTypes = types;
        if (strictNullChecks)
        {
            primaryTypes = CoreUtil.SameMap(types, t => FilterType(t, u => (u.flags & TypeFlags.Nullable) == 0))!;
        }
        // When the candidate types are all literal types with the same base type, return a union
        // of those literal types. Otherwise, return the leftmost type for which no type to the
        // right is a supertype.
        Type supertype;
        if (LiteralTypesWithSameBaseType(primaryTypes))
        {
            supertype = GetUnionType(primaryTypes);
        }
        else
        {
            supertype = GetSingleCommonSupertype(primaryTypes);
        }
        // Add any nullable types that occurred in the candidates back to the result.
        if (CoreUtil.Same(primaryTypes, types))
        {
            return supertype;
        }
        return GetNullableType(supertype, GetCombinedTypeFlags(types) & TypeFlags.Nullable);
    }

    // inference.go:1523-1532
    internal Type GetSingleCommonSupertype(Type[] types)
    {
        // First, find the leftmost type for which no type to the right is a strict supertype, and if that
        // type is a strict supertype of all other candidates, return it. Otherwise, return the leftmost type
        // for which no type to the right is a (regular) supertype.
        var candidate = FindLeftmostType(types, IsTypeStrictSubtypeOf);
        if (CoreUtil.Every(types, t => t == candidate || IsTypeStrictSubtypeOf(t, candidate!)))
        {
            return candidate!;
        }
        return FindLeftmostType(types, IsTypeSubtypeOf)!;
    }

    // inference.go:1534-1542
    internal Type? FindLeftmostType(Type[] types, Func<Type, Type, bool> f)
    {
        Type? candidate = null;
        foreach (var t in types)
        {
            if (candidate is null || f(candidate, t))
            {
                candidate = t;
            }
        }
        return candidate;
    }

    // inference.go:1544-1553 — Return the leftmost type for which no type to the right is a subtype.
    internal Type GetCommonSubtype(Type[] types)
    {
        Type? subtype = null;
        foreach (var t in types)
        {
            if (subtype is null || IsTypeSubtypeOf(t, subtype))
            {
                subtype = t;
            }
        }
        return subtype!;
    }

    // inference.go:1555-1565
    internal TypeFlags GetCombinedTypeFlags(Type[] types)
    {
        var flags = TypeFlags.None;
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Union) != 0)
            {
                flags |= GetCombinedTypeFlags(t.Types());
            }
            else
            {
                flags |= t.flags;
            }
        }
        return flags;
    }

    // inference.go:1567-1581
    internal bool LiteralTypesWithSameBaseType(Type[] types)
    {
        Type? commonBaseType = null;
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Never) == 0)
            {
                var baseType = GetBaseTypeOfLiteralType(t);
                if (commonBaseType is null)
                {
                    commonBaseType = baseType;
                }
                if (baseType == t || baseType != commonBaseType)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // inference.go:1583-1585
    internal bool IsFromInferenceBlockedSource(Type t)
    {
        return t.symbol is not null && CoreUtil.Some(t.symbol.Declarations, IsSkipDirectInferenceNode);
    }

    // inference.go:1587-1589
    internal bool IsSkipDirectInferenceNode(Node node)
    {
        return skipDirectInferenceNodes.Has(node);
    }

    // inference.go:1591-1593
    internal static InferenceInfo NewInferenceInfo(Type typeParameter)
    {
        return new InferenceInfo { typeParameter = typeParameter, priority = InferencePriority.MaxValue, topLevel = true, impliedArity = -1 };
    }

    // inference.go:1595-1607
    internal static InferenceInfo CloneInferenceInfo(InferenceInfo info)
    {
        return new InferenceInfo
        {
            typeParameter = info.typeParameter,
            candidates = info.candidates is null ? null : (Type[])info.candidates.Clone(),
            candidateDepths = info.candidateDepths is null ? null : (int[])info.candidateDepths.Clone(),
            contraCandidates = info.contraCandidates is null ? null : (Type[])info.contraCandidates.Clone(),
            inferredType = info.inferredType,
            priority = info.priority,
            topLevel = info.topLevel,
            isFixed = info.isFixed,
            impliedArity = info.impliedArity,
        };
    }

    // inference.go:1609-1615
    internal static void ClearCachedInferences(InferenceInfo[] inferences)
    {
        foreach (var inference in inferences)
        {
            if (!inference.isFixed)
            {
                inference.inferredType = null;
            }
        }
    }

    // inference.go:1617-1619
    internal static bool HasInferenceCandidates(InferenceInfo info)
    {
        return (info.candidates?.Length ?? 0) != 0 || (info.contraCandidates?.Length ?? 0) != 0;
    }

    // inference.go:1621-1623
    internal bool HasInferenceCandidatesOrDefault(InferenceInfo info)
    {
        return HasInferenceCandidates(info) || HasTypeParameterDefault(info.typeParameter);
    }

    // inference.go:1625-1634 — hasTypeParameterDefault is implemented in Checker.Instantiation.cs
    // (checker.go:21853, instance method) by the instantiation wave; consumed here via that definition.

    // inference.go:1636-1643
    internal static bool HasOverlappingInferences(InferenceInfo[] a, InferenceInfo[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            if (HasInferenceCandidates(a[i]) && HasInferenceCandidates(b[i]))
            {
                return true;
            }
        }
        return false;
    }

    // inference.go:1645-1651
    internal void MergeInferences(InferenceInfo[] target, InferenceInfo[] source)
    {
        for (int i = 0; i < target.Length; i++)
        {
            if (!HasInferenceCandidates(target[i]) && HasInferenceCandidates(source[i]))
            {
                target[i] = source[i];
            }
        }
    }
}
