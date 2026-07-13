// Ported from tsgo internal/checker/flow.go (control-flow narrowing engine), lines ~1046-2734.
// Continuation of Checker.Flow.cs. plan/03 §4.2 #(flow). Faithful per-function translation.
//
// This file carries: switch-clause narrowing (discriminant/typeof/true/optional-chain), branch/loop
// label junctions, array-mutation & evolving-array types, reportFlowControlError, isMatchingReference,
// flow reference cache keys, accessed-property-name helpers, constant-reference, reference candidates/
// roots, replacePrimitivesWithLiterals, exhaustiveness, switch witnesses, getEffectsSignature and the
// dotted-name/explicit-type helpers, initial/assigned-type resolution, flow-in-constructor/static-blocks,
// reachability (isReachableFlowNode / isPostSuperFlowNode) and assignment marking.

using System;
using System.Collections.Generic;
using System.Globalization;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // flow.go:1046-1076
    internal FlowType GetTypeAtSwitchClause(FlowState f, FlowNode flow)
    {
        var data = flow.Node!.AsFlowSwitchClauseData();
        var expr = AstUtilities.SkipParentheses(data.SwitchStatement!.Expression()!);
        var flowType = GetTypeAtFlowNode(f, flow.Antecedent!);
        var t = flowType.t!;
        if (IsMatchingReference(f.reference, expr))
        {
            t = NarrowTypeBySwitchOnDiscriminant(t, data);
        }
        else if (expr.Kind == SyntaxKind.TypeOfExpression && IsMatchingReference(f.reference, expr.Expression()!))
        {
            t = NarrowTypeBySwitchOnTypeOf(t, data);
        }
        else if (expr.Kind == SyntaxKind.TrueKeyword)
        {
            t = NarrowTypeBySwitchOnTrue(f, t, data);
        }
        else
        {
            if (strictNullChecks)
            {
                if (OptionalChainContainsReference(expr, f.reference))
                {
                    t = NarrowTypeBySwitchOptionalChainContainment(t, data, tt =>
                        (tt.flags & (TypeFlags.Undefined | TypeFlags.Never)) == 0);
                }
                else if (NodeTests.IsTypeOfExpression(expr) && OptionalChainContainsReference(expr.Expression()!, f.reference))
                {
                    t = NarrowTypeBySwitchOptionalChainContainment(t, data, tt =>
                        !((tt.flags & TypeFlags.Never) != 0 || (tt.flags & TypeFlags.StringLiteral) != 0 && GetStringLiteralValue(tt) == "undefined"));
                }
            }
            var access = GetDiscriminantPropertyAccess(f, expr, t);
            if (access is not null)
            {
                t = NarrowTypeBySwitchOnDiscriminantProperty(t, access, data);
            }
        }
        return NewFlowType(t, flowType.incomplete);
    }

    // flow.go:1078-1134
    internal Type NarrowTypeBySwitchOnDiscriminant(Type t, FlowSwitchClauseData data)
    {
        // We only narrow if all case expressions specify
        // values with unit types, except for the case where
        // `type` is unknown. In this instance we map object
        // types to the nonPrimitive type and narrow with that.
        var switchTypes = GetSwitchClauseTypes(data.SwitchStatement!);
        if (switchTypes.Length == 0)
        {
            return t;
        }
        // clauseTypes := switchTypes[data.ClauseStart:data.ClauseEnd]
        var clauseTypes = switchTypes[data.ClauseStart..data.ClauseEnd];
        var hasDefaultClause = data.ClauseStart == data.ClauseEnd || Array.IndexOf(clauseTypes, neverType!) >= 0;
        if ((t.flags & TypeFlags.Unknown) != 0 && !hasDefaultClause)
        {
            List<Type>? groundClauseTypes = null;
            for (int i = 0; i < clauseTypes.Length; i++)
            {
                var s = clauseTypes[i];
                if ((s.flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0)
                {
                    if (groundClauseTypes is not null)
                    {
                        groundClauseTypes.Add(s);
                    }
                }
                else if ((s.flags & TypeFlags.Object) != 0)
                {
                    if (groundClauseTypes is null)
                    {
                        // groundClauseTypes = clauseTypes[:i:i]
                        groundClauseTypes = new List<Type>(clauseTypes[..i]);
                    }
                    groundClauseTypes.Add(nonPrimitiveType!);
                }
                else
                {
                    return t;
                }
            }
            return GetUnionType(groundClauseTypes is null ? clauseTypes : groundClauseTypes.ToArray());
        }
        var discriminantType = GetUnionType(clauseTypes);
        Type caseType;
        if ((discriminantType.flags & TypeFlags.Never) != 0)
        {
            caseType = neverType!;
        }
        else
        {
            var filtered = FilterType(t, tt => AreTypesComparable(discriminantType, tt));
            caseType = ReplacePrimitivesWithLiterals(filtered, discriminantType);
        }
        if (!hasDefaultClause)
        {
            return caseType;
        }
        var defaultType = FilterType(t, tt =>
        {
            if (!IsUnitLikeType(tt))
            {
                return true;
            }
            var u = undefinedType!;
            if ((tt.flags & TypeFlags.Undefined) == 0)
            {
                u = GetRegularTypeOfLiteralType(ExtractUnitType(tt));
            }
            return SlicesIndexFunc(switchTypes, st =>
                IsUnitType(st) && AreTypesComparable(st, u)) < 0;
        });
        if ((caseType.flags & TypeFlags.Never) != 0)
        {
            return defaultType;
        }
        return GetUnionType([caseType, defaultType]);
    }

    // flow.go:1136-1164
    internal Type NarrowTypeBySwitchOnTypeOf(Type t, FlowSwitchClauseData data)
    {
        var witnesses = GetSwitchClauseTypeOfWitnesses(data.SwitchStatement!);
        if (witnesses is null)
        {
            return t;
        }
        var clauses = data.SwitchStatement!.AsSwitchStatement().CaseBlock!.AsCaseBlock().Clauses!.Nodes;
        // Equal start and end denotes implicit fallthrough; undefined marks explicit default clause.
        var defaultIndex = CoreUtil.FindIndex(clauses, clause =>
            clause.Kind == SyntaxKind.DefaultClause);
        var clauseStart = data.ClauseStart;
        var clauseEnd = data.ClauseEnd;
        var hasDefaultClause = clauseStart == clauseEnd || (defaultIndex >= clauseStart && defaultIndex < clauseEnd);
        if (hasDefaultClause)
        {
            // In the default clause we filter constituents down to those that are not-equal to all handled cases.
            var notEqualFacts = GetNotEqualFactsFromTypeofSwitch(clauseStart, clauseEnd, witnesses);
            return FilterType(t, tt => GetTypeFacts(tt, notEqualFacts) == notEqualFacts);
        }
        // In the non-default cause we create a union of the type narrowed by each of the listed cases.
        var clauseWitnesses = witnesses[clauseStart..clauseEnd];
        return GetUnionType(CoreUtil.Map(clauseWitnesses, text =>
        {
            if (text != "")
            {
                return NarrowTypeByTypeName(t, text);
            }
            return neverType!;
        })!);
    }

    // flow.go:1166-1200
    internal Type NarrowTypeBySwitchOnTrue(FlowState f, Type t, FlowSwitchClauseData data)
    {
        var clauses = data.SwitchStatement!.AsSwitchStatement().CaseBlock!.AsCaseBlock().Clauses!.Nodes;
        var defaultIndex = CoreUtil.FindIndex(clauses, clause =>
            clause.Kind == SyntaxKind.DefaultClause);
        var clauseStart = data.ClauseStart;
        var clauseEnd = data.ClauseEnd;
        var hasDefaultClause = clauseStart == clauseEnd || (defaultIndex >= clauseStart && defaultIndex < clauseEnd);
        // First, narrow away all of the cases that preceded this set of cases.
        for (int i = 0; i < clauseStart; i++)
        {
            var clause = clauses[i];
            if (clause.Kind == SyntaxKind.CaseClause)
            {
                t = NarrowType(f, t, clause.Expression()!, false /*assumeTrue*/);
            }
        }
        // If our current set has a default, then none the other cases were hit either.
        // There's no point in narrowing by the other cases in the set, since we can
        // get here through other paths.
        if (hasDefaultClause)
        {
            for (int i = clauseEnd; i < clauses.Length; i++)
            {
                var clause = clauses[i];
                if (clause.Kind == SyntaxKind.CaseClause)
                {
                    t = NarrowType(f, t, clause.Expression()!, false /*assumeTrue*/);
                }
            }
            return t;
        }
        // Now, narrow based on the cases in this set.
        return GetUnionType(CoreUtil.Map(clauses[clauseStart..clauseEnd], clause =>
        {
            if (clause.Kind == SyntaxKind.CaseClause)
            {
                return NarrowType(f, t, clause.Expression()!, true /*assumeTrue*/);
            }
            return neverType!;
        })!);
    }

    // flow.go:1202-1208
    internal Type NarrowTypeBySwitchOptionalChainContainment(Type t, FlowSwitchClauseData data, Func<Type, bool> clauseCheck)
    {
        var everyClauseChecks = data.ClauseStart != data.ClauseEnd && CoreUtil.Every(GetSwitchClauseTypes(data.SwitchStatement!)[data.ClauseStart..data.ClauseEnd], clauseCheck);
        if (everyClauseChecks)
        {
            return GetTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
        }
        return t;
    }

    // flow.go:1210-1230
    internal Type NarrowTypeBySwitchOnDiscriminantProperty(Type t, Node access, FlowSwitchClauseData data)
    {
        if (data.ClauseStart < data.ClauseEnd && (t.flags & TypeFlags.Union) != 0)
        {
            GetAccessedPropertyName(access, out var accessedName);
            if (accessedName != "" && GetKeyPropertyName(t) == accessedName)
            {
                var clauseTypes = GetSwitchClauseTypes(data.SwitchStatement!)[data.ClauseStart..data.ClauseEnd];
                var candidate = GetUnionType(CoreUtil.Map(clauseTypes, s =>
                {
                    var result = GetConstituentTypeForKeyType(t, s);
                    if (result is not null)
                    {
                        return result;
                    }
                    return unknownType!;
                })!);
                if (candidate != unknownType)
                {
                    return candidate;
                }
            }
        }
        return NarrowTypeByDiscriminant(t, access, tt =>
            NarrowTypeBySwitchOnDiscriminant(tt, data));
    }

    // flow.go:1232-1288
    internal FlowType GetTypeAtFlowBranchLabel(FlowState f, FlowNode flow, FlowList antecedents)
    {
        var antecedentStart = antecedentTypes.Length;
        var subtypeReduction = false;
        var seenIncomplete = false;
        FlowNode? bypassFlow = null;
        for (var list = antecedents; list is not null; list = list.Next)
        {
            var antecedent = list.Flow!;
            if (bypassFlow is null && (antecedent.Flags & FlowFlags.SwitchClause) != 0 && antecedent.Node!.AsFlowSwitchClauseData().IsEmpty())
            {
                // The antecedent is the bypass branch of a potentially exhaustive switch statement.
                bypassFlow = antecedent;
                continue;
            }
            var flowType = GetTypeAtFlowNode(f, antecedent);
            // If the type at a particular antecedent path is the declared type and the
            // reference is known to always be assigned (i.e. when declared and initial types
            // are the same), there is no reason to process more antecedents since the only
            // possible outcome is subtypes that will be removed in the final union type anyway.
            if (flowType.t == f.declaredType && f.declaredType == f.initialType)
            {
                antecedentTypes = antecedentTypes[..antecedentStart];
                return new FlowType { t = flowType.t };
            }
            if (!SlicesContains(antecedentTypes[antecedentStart..], flowType.t!))
            {
                antecedentTypes = [.. antecedentTypes, flowType.t!];
            }
            // If an antecedent type is not a subset of the declared type, we need to perform
            // subtype reduction. This happens when a "foreign" type is injected into the control
            // flow using the instanceof operator or a user defined type predicate.
            if (!IsTypeSubsetOf(flowType.t!, f.initialType))
            {
                subtypeReduction = true;
            }
            if (flowType.incomplete)
            {
                seenIncomplete = true;
            }
        }
        if (bypassFlow is not null)
        {
            var flowType = GetTypeAtFlowNode(f, bypassFlow);
            // If the bypass flow contributes a type we haven't seen yet and the switch statement
            // isn't exhaustive, process the bypass flow type. Since exhaustiveness checks increase
            // the risk of circularities, we only want to perform them when they make a difference.
            if ((flowType.t!.flags & TypeFlags.Never) == 0 && !SlicesContains(antecedentTypes[antecedentStart..], flowType.t) && !IsExhaustiveSwitchStatement(bypassFlow.Node!.AsFlowSwitchClauseData().SwitchStatement!))
            {
                if (flowType.t == f.declaredType && f.declaredType == f.initialType)
                {
                    antecedentTypes = antecedentTypes[..antecedentStart];
                    return new FlowType { t = flowType.t };
                }
                antecedentTypes = [.. antecedentTypes, flowType.t];
                if (!IsTypeSubsetOf(flowType.t, f.initialType))
                {
                    subtypeReduction = true;
                }
                if (flowType.incomplete)
                {
                    seenIncomplete = true;
                }
            }
        }
        var result = NewFlowType(GetUnionOrEvolvingArrayType(f, antecedentTypes[antecedentStart..], CoreUtil.IfElse(subtypeReduction, UnionReduction.Subtype, UnionReduction.Literal)), seenIncomplete);
        antecedentTypes = antecedentTypes[..antecedentStart];
        return result;
    }

    // At flow control branch or loop junctions, if the type along every antecedent code path
    // is an evolving array type, we construct a combined evolving array type. Otherwise we
    // finalize all evolving array types.
    // flow.go:1293-1302
    internal Type GetUnionOrEvolvingArrayType(FlowState f, Type[] types, UnionReduction subtypeReduction)
    {
        if (IsEvolvingArrayTypeList(types))
        {
            return GetEvolvingArrayType(GetUnionType(CoreUtil.Map(types, GetElementTypeOfEvolvingArrayType)!));
        }
        var result = RecombineUnknownType(GetUnionTypeEx(CoreUtil.SameMap(types, FinalizeEvolvingArrayType), subtypeReduction, null, null));
        if (result != f.declaredType && (result.flags & f.declaredType.flags & TypeFlags.Union) != 0 && SlicesEqual(result.AsUnionType().types, f.declaredType.AsUnionType().types))
        {
            return f.declaredType;
        }
        return result;
    }

    // flow.go:1304-1381
    internal FlowType GetTypeAtFlowLoopLabel(FlowState f, FlowNode flow)
    {
        if (f.refKey.IsZero)
        {
            f.refKey = GetFlowReferenceKey(f);
        }
        if (f.refKey == NonDottedNameCacheKey)
        {
            // No cache key is generated when binding patterns are in unnarrowable situations
            return new FlowType { t = f.declaredType };
        }
        var key = new FlowLoopKey(flow, f.refKey);
        // If we have previously computed the control flow type for the reference at
        // this flow loop junction, return the cached type.
        if (flowLoopCache.TryGetValue(key, out var cachedLoop))
        {
            return new FlowType { t = cachedLoop };
        }
        // If this flow loop junction and reference are already being processed, return
        // the union of the types computed for each branch so far, marked as incomplete.
        // It is possible to see an empty array in cases where loops are nested and the
        // back edge of the outer loop reaches an inner loop that is already being analyzed.
        // In such cases we restart the analysis of the inner loop, which will then see
        // a non-empty in-process array for the outer loop and eventually terminate because
        // the first antecedent of a loop junction is always the non-looping control flow
        // path that leads to the top.
        foreach (var loopInfo in flowLoopStack)
        {
            if (loopInfo.key == key && loopInfo.types.Len() != 0)
            {
                return NewFlowType(GetUnionOrEvolvingArrayType(f, loopInfo.types!, UnionReduction.Literal), true /*incomplete*/);
            }
        }
        // Add the flow loop junction and reference to the in-process stack and analyze
        // each antecedent code path.
        var antecedentTypesList = new List<Type>(4);
        var subtypeReduction = false;
        FlowType firstAntecedentType = default;
        for (var list = flow.Antecedents; list is not null; list = list.Next)
        {
            FlowType flowType;
            if (firstAntecedentType.IsNil())
            {
                // The first antecedent of a loop junction is always the non-looping control
                // flow path that leads to the top.
                firstAntecedentType = GetTypeAtFlowNode(f, list.Flow!);
                flowType = firstAntecedentType;
            }
            else
            {
                // All but the first antecedent are the looping control flow paths that lead
                // back to the loop junction. We track these on the flow loop stack.
                flowLoopStack.Add(new FlowLoopInfo { key = key, types = antecedentTypesList.ToArray() });
                var saveFlowTypeCache = flowTypeCache;
                flowTypeCache = null!;
                flowType = GetTypeAtFlowNode(f, list.Flow!);
                flowTypeCache = saveFlowTypeCache;
                flowLoopStack.RemoveAt(flowLoopStack.Count - 1);
                // If we see a value appear in the cache it is a sign that control flow analysis
                // was restarted and completed by checkExpressionCached. We can simply pick up
                // the resulting type and bail out.
                if (flowLoopCache.TryGetValue(key, out var cachedRestart))
                {
                    return new FlowType { t = cachedRestart };
                }
            }
            AppendIfUniqueType(antecedentTypesList, flowType.t!);
            // If an antecedent type is not a subset of the declared type, we need to perform
            // subtype reduction. This happens when a "foreign" type is injected into the control
            // flow using the instanceof operator or a user defined type predicate.
            if (!IsTypeSubsetOf(flowType.t!, f.initialType))
            {
                subtypeReduction = true;
            }
            // If the type at a particular antecedent path is the declared type there is no
            // reason to process more antecedents since the only possible outcome is subtypes
            // that will be removed in the final union type anyway.
            if (flowType.t == f.declaredType)
            {
                break;
            }
        }
        // The result is incomplete if the first antecedent (the non-looping control flow path)
        // is incomplete.
        var result = GetUnionOrEvolvingArrayType(f, antecedentTypesList.ToArray(), CoreUtil.IfElse(subtypeReduction, UnionReduction.Subtype, UnionReduction.Literal));
        if (firstAntecedentType.incomplete)
        {
            return NewFlowType(result, true /*incomplete*/);
        }
        flowLoopCache[key] = result;
        return new FlowType { t = result };
    }

    // flow.go:1383-1413
    internal FlowType GetTypeAtFlowArrayMutation(FlowState f, FlowNode flow)
    {
        if (f.declaredType == autoType || f.declaredType == autoArrayType)
        {
            var node = flow.Node!;
            Node expr;
            if (NodeTests.IsCallExpression(node))
            {
                expr = node.Expression()!.Expression()!;
            }
            else
            {
                expr = node.AsBinaryExpression().Left!.Expression()!;
            }
            if (IsMatchingReference(f.reference, GetReferenceCandidate(expr)))
            {
                var flowType = GetTypeAtFlowNode(f, flow.Antecedent!);
                if ((flowType.t!.objectFlags & ObjectFlags.EvolvingArray) != 0)
                {
                    var evolvedType = flowType.t;
                    if (NodeTests.IsCallExpression(node))
                    {
                        foreach (var arg in node.Arguments() ?? [])
                        {
                            evolvedType = AddEvolvingArrayElementType(evolvedType, arg);
                        }
                    }
                    else
                    {
                        // We must get the context free expression type so as to not recur in an uncached fashion on the LHS (which causes exponential blowup in compile time)
                        var indexType = GetContextFreeTypeOfExpression(node.AsBinaryExpression().Left!.AsElementAccessExpression().ArgumentExpression!);
                        if (IsTypeAssignableToKind(indexType, TypeFlags.NumberLike))
                        {
                            evolvedType = AddEvolvingArrayElementType(evolvedType, node.AsBinaryExpression().Right!);
                        }
                    }
                    return NewFlowType(evolvedType, flowType.incomplete);
                }
                return flowType;
            }
        }
        return default;
    }

    // flow.go:1415-1434
    internal Node? GetDiscriminantPropertyAccess(FlowState f, Node expr, Type computedType)
    {
        // As long as the computed type is a subset of the declared type, we use the full declared type to detect
        // a discriminant property. In cases where the computed type isn't a subset, e.g because of a preceding type
        // predicate narrowing, we use the actual computed type.
        if ((f.declaredType.flags & TypeFlags.Union) != 0 || (computedType.flags & TypeFlags.Union) != 0)
        {
            var access = GetCandidateDiscriminantPropertyAccess(f, expr);
            if (access is not null)
            {
                if (GetAccessedPropertyName(access, out var name))
                {
                    var t = computedType;
                    if ((f.declaredType.flags & TypeFlags.Union) != 0 && IsTypeSubsetOf(computedType, f.declaredType))
                    {
                        t = f.declaredType;
                    }
                    if (IsDiscriminantProperty(t, name))
                    {
                        return access;
                    }
                }
            }
        }
        return null;
    }

    // flow.go:1436-1473
    internal Node? GetCandidateDiscriminantPropertyAccess(FlowState f, Node expr)
    {
        if (AstUtilities.IsBindingPattern(f.reference) || AstUtilities.IsFunctionExpressionOrArrowFunction(f.reference) || AstUtilities.IsObjectLiteralMethod(f.reference))
        {
            // When the reference is a binding pattern or function or arrow expression, we are narrowing a pseudo-reference in
            // getNarrowedTypeOfSymbol. An identifier for a destructuring variable declared in the same binding pattern or
            // parameter declared in the same parameter list is a candidate.
            if (NodeTests.IsIdentifier(expr))
            {
                var symbol = GetResolvedSymbol(expr);
                var declaration = GetExportSymbolOfValueSymbolIfExported(symbol)!.ValueDeclaration;
                if (declaration is not null && (NodeTests.IsBindingElement(declaration) || NodeTests.IsParameterDeclaration(declaration)) && f.reference == declaration.Parent && declaration.Initializer() is null && !HasDotDotDotToken(declaration))
                {
                    return declaration;
                }
            }
        }
        else if (AstUtilities.IsAccessExpression(expr))
        {
            // An access expression is a candidate if the reference matches the left hand expression.
            if (IsMatchingReference(f.reference, expr.Expression()!))
            {
                return expr;
            }
        }
        else if (NodeTests.IsIdentifier(expr))
        {
            var symbol = GetResolvedSymbol(expr);
            if (IsConstantVariable(symbol))
            {
                var declaration = symbol.ValueDeclaration;
                var initializer = GetCandidateVariableDeclarationInitializer(declaration!);
                // Given 'const x = obj.kind', allow 'x' as an alias for 'obj.kind'
                if (initializer is not null && AstUtilities.IsAccessExpression(initializer) && IsMatchingReference(f.reference, initializer.Expression()!))
                {
                    return initializer;
                }
                // Given 'const { kind: x } = obj', allow 'x' as an alias for 'obj.kind'
                if (NodeTests.IsBindingElement(declaration!) && declaration!.Initializer() is null)
                {
                    initializer = GetCandidateVariableDeclarationInitializer(declaration!.Parent!.Parent!);
                    if (initializer is not null && (NodeTests.IsIdentifier(initializer) || AstUtilities.IsAccessExpression(initializer)) && IsMatchingReference(f.reference, initializer))
                    {
                        return declaration;
                    }
                }
            }
        }
        return null;
    }

    // flow.go:1475-1482
    internal static Node? GetCandidateVariableDeclarationInitializer(Node node)
    {
        if (NodeTests.IsVariableDeclaration(node) && node.Type() is null)
        {
            var initializer = node.Initializer();
            if (initializer is not null)
            {
                return AstUtilities.SkipParentheses(initializer);
            }
        }
        return null;
    }

    // An evolving array type tracks the element types that have so far been seen in an
    // 'x.push(value)' or 'x[n] = value' operation along the control flow graph. Evolving
    // array types are ultimately converted into manifest array types (using getFinalArrayType)
    // and never escape the getFlowTypeOfReference function.
    // flow.go:1488-1497
    internal Type GetEvolvingArrayType(Type elementType)
    {
        var key = new CachedTypeKey(CachedTypeKind.EvolvingArrayType, elementType.id);
        if (!cachedTypes.TryGetValue(key, out var result))
        {
            result = NewObjectType(ObjectFlags.EvolvingArray, null);
            result.AsEvolvingArrayType().elementType = elementType;
            cachedTypes[key] = result;
        }
        return result;
    }

    // flow.go:1499-1504
    internal Type GetElementTypeOfEvolvingArrayType(Type t)
    {
        if ((t.objectFlags & ObjectFlags.EvolvingArray) != 0)
        {
            return t.AsEvolvingArrayType().elementType!;
        }
        return neverType!;
    }

    // flow.go:1506-1517
    internal static bool IsEvolvingArrayTypeList(Type[] types)
    {
        var hasEvolvingArrayType = false;
        foreach (var t in types)
        {
            if ((t.flags & TypeFlags.Never) == 0)
            {
                if ((t.objectFlags & ObjectFlags.EvolvingArray) == 0)
                {
                    return false;
                }
                hasEvolvingArrayType = true;
            }
        }
        return hasEvolvingArrayType;
    }

    // Return true if the given node is 'x' in an 'x.length', x.push(value)', 'x.unshift(value)' or
    // 'x[n] = value' operation, where 'n' is an expression of type any, undefined, or a number-like type.
    // flow.go:1521-1531
    internal bool IsEvolvingArrayOperationTarget(Node node)
    {
        var root = GetReferenceRoot(node);
        var parent = root.Parent!;
        var isLengthPushOrUnshift = NodeTests.IsPropertyAccessExpression(parent) && (parent.Name()!.Text() == "length" ||
            NodeTests.IsCallExpression(parent.Parent!) && NodeTests.IsIdentifier(parent.Name()!) && AstUtilities.IsPushOrUnshiftIdentifier(parent.Name()!));
        var isElementAssignment = NodeTests.IsElementAccessExpression(parent) && parent.Expression() == root &&
            NodeTests.IsBinaryExpression(parent.Parent!) && parent.Parent!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken &&
            parent.Parent!.AsBinaryExpression().Left == parent && !AstUtilities.IsAssignmentTarget(parent.Parent!) &&
            IsTypeAssignableToKind(GetTypeOfExpression(parent.AsElementAccessExpression().ArgumentExpression!), TypeFlags.NumberLike);
        return isLengthPushOrUnshift || isElementAssignment;
    }

    // When adding evolving array element types we do not perform subtype reduction. Instead,
    // we defer subtype reduction until the evolving array type is finalized into a manifest
    // array type.
    // flow.go:1536-1543
    internal Type AddEvolvingArrayElementType(Type evolvingArrayType, Node node)
    {
        var newElementType = GetRegularTypeOfObjectLiteral(GetBaseTypeOfLiteralType(GetContextFreeTypeOfExpression(node)));
        var elementType = evolvingArrayType.AsEvolvingArrayType().elementType!;
        if (IsTypeSubsetOf(newElementType, elementType))
        {
            return evolvingArrayType;
        }
        return GetEvolvingArrayType(GetUnionType([elementType, newElementType]));
    }

    // flow.go:1545-1550
    internal Type FinalizeEvolvingArrayType(Type t)
    {
        if ((t.objectFlags & ObjectFlags.EvolvingArray) != 0)
        {
            return GetFinalArrayType(t.AsEvolvingArrayType());
        }
        return t;
    }

    // flow.go:1552-1557
    internal Type GetFinalArrayType(EvolvingArrayType t)
    {
        if (t.finalArrayType is null)
        {
            t.finalArrayType = CreateFinalArrayType(t.elementType!);
        }
        return t.finalArrayType;
    }

    // flow.go:1559-1567
    internal Type CreateFinalArrayType(Type elementType)
    {
        if ((elementType.flags & TypeFlags.Never) != 0)
        {
            return autoArrayType!;
        }
        if ((elementType.flags & TypeFlags.Union) != 0)
        {
            return CreateArrayType(GetUnionTypeEx(elementType.Types(), UnionReduction.Subtype, null, null));
        }
        return CreateArrayType(elementType);
    }

    // flow.go:1569-1574
    internal void ReportFlowControlError(Node node)
    {
        var block = AstUtilities.FindAncestor(node, AstUtilities.IsFunctionOrModuleBlock);
        var sourceFile = AstUtilities.GetSourceFileOfNode(node);
        var span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(sourceFile!, block!.StatementList()!.Pos);
        AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, span, Diag.The_containing_function_or_module_body_is_too_large_for_control_flow_analysis));
    }

    // flow.go:1576-1628
    internal bool IsMatchingReference(Node source, Node target)
    {
        switch (target.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.NonNullExpression:
                return IsMatchingReference(source, target.Expression()!);
            case SyntaxKind.BinaryExpression:
                return AstUtilities.IsAssignmentExpression(target, false) && IsMatchingReference(source, target.AsBinaryExpression().Left!) ||
                    NodeTests.IsBinaryExpression(target) && target.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken &&
                        IsMatchingReference(source, target.AsBinaryExpression().Right!);
        }
        switch (source.Kind)
        {
            case SyntaxKind.MetaProperty:
                return NodeTests.IsMetaProperty(target) && source.AsMetaProperty().KeywordToken == target.AsMetaProperty().KeywordToken && source.Name()!.Text() == target.Name()!.Text();
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
                if (AstUtilities.IsThisInTypeQuery(source))
                {
                    return target.Kind == SyntaxKind.ThisKeyword;
                }
                return NodeTests.IsIdentifier(target) && GetResolvedSymbol(source) == GetResolvedSymbol(target) ||
                    (NodeTests.IsVariableDeclaration(target) || NodeTests.IsBindingElement(target)) && GetExportSymbolOfValueSymbolIfExported(GetResolvedSymbol(source)) == GetSymbolOfDeclaration(target);
            case SyntaxKind.ThisKeyword:
                return target.Kind == SyntaxKind.ThisKeyword;
            case SyntaxKind.SuperKeyword:
                return target.Kind == SyntaxKind.SuperKeyword;
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.SatisfiesExpression:
                return IsMatchingReference(source.Expression()!, target);
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                if (GetAccessedPropertyName(source, out var sourcePropertyName))
                {
                    if (AstUtilities.IsAccessExpression(target))
                    {
                        if (GetAccessedPropertyName(target, out var targetPropertyName))
                        {
                            return targetPropertyName == sourcePropertyName && IsMatchingReference(source.Expression()!, target.Expression()!);
                        }
                    }
                }
                if (NodeTests.IsElementAccessExpression(source) && NodeTests.IsElementAccessExpression(target))
                {
                    var sourceArg = source.AsElementAccessExpression().ArgumentExpression!;
                    var targetArg = target.AsElementAccessExpression().ArgumentExpression!;
                    if (NodeTests.IsIdentifier(sourceArg) && NodeTests.IsIdentifier(targetArg))
                    {
                        var symbol = GetResolvedSymbol(sourceArg);
                        if (symbol == GetResolvedSymbol(targetArg) && (IsConstantVariable(symbol) || IsParameterOrMutableLocalVariable(symbol) && !IsSymbolAssigned(symbol)))
                        {
                            return IsMatchingReference(source.Expression()!, target.Expression()!);
                        }
                    }
                }
                break;
            case SyntaxKind.QualifiedName:
                if (AstUtilities.IsAccessExpression(target))
                {
                    if (GetAccessedPropertyName(target, out var targetPropertyName))
                    {
                        return source.AsQualifiedName().Right!.Text() == targetPropertyName && IsMatchingReference(source.AsQualifiedName().Left!, target.Expression()!);
                    }
                }
                break;
            case SyntaxKind.BinaryExpression:
                return NodeTests.IsBinaryExpression(source) && source.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken && IsMatchingReference(source.AsBinaryExpression().Right!, target);
        }
        return false;
    }

    // flow.go:1630
    internal static readonly CacheHashKey NonDottedNameCacheKey = new(System.IO.Hashing.XxHash128.HashToUInt128(System.Text.Encoding.UTF8.GetBytes("?")));

    // Return the flow cache key for a "dotted name" (i.e. a sequence of identifiers
    // separated by dots). The key consists of the id of the symbol referenced by the
    // leftmost identifier followed by zero or more property names separated by dots.
    // The result is nonDottedNameCacheKey if the reference isn't a dotted name.
    // flow.go:1636-1642
    internal CacheHashKey GetFlowReferenceKey(FlowState f)
    {
        var b = new KeyBuilder();
        if (WriteFlowCacheKey(b, f.reference, f.declaredType, f.initialType, f.flowContainer))
        {
            return b.Hash();
        }
        return NonDottedNameCacheKey; // Reference isn't a dotted name
    }

    // flow.go:1644-1704
    internal bool WriteFlowCacheKey(KeyBuilder b, Node node, Type declaredType, Type initialType, Node? flowContainer)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                if (!AstUtilities.IsThisInTypeQuery(node))
                {
                    var symbol = GetResolvedSymbol(node);
                    if (symbol == unknownSymbol)
                    {
                        return false;
                    }
                    b.WriteSymbol(symbol);
                }
                goto case SyntaxKind.ThisKeyword;
            case SyntaxKind.ThisKeyword:
                b.WriteByte((byte)':');
                b.WriteType(declaredType);
                if (initialType != declaredType)
                {
                    b.WriteByte((byte)'=');
                    b.WriteType(initialType);
                }
                if (flowContainer is not null)
                {
                    b.WriteByte((byte)'@');
                    b.WriteNode(flowContainer);
                }
                return true;
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.ParenthesizedExpression:
                return WriteFlowCacheKey(b, node.Expression()!, declaredType, initialType, flowContainer);
            case SyntaxKind.QualifiedName:
                if (!WriteFlowCacheKey(b, node.AsQualifiedName().Left!, declaredType, initialType, flowContainer))
                {
                    return false;
                }
                b.WriteByte((byte)'.');
                b.WriteString(node.AsQualifiedName().Right!.Text());
                return true;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                if (GetAccessedPropertyName(node, out var propName))
                {
                    if (!WriteFlowCacheKey(b, node.Expression()!, declaredType, initialType, flowContainer))
                    {
                        return false;
                    }
                    b.WriteByte((byte)'.');
                    b.WriteString(propName);
                    return true;
                }
                if (NodeTests.IsElementAccessExpression(node) && NodeTests.IsIdentifier(node.AsElementAccessExpression().ArgumentExpression!))
                {
                    var symbol = GetResolvedSymbol(node.AsElementAccessExpression().ArgumentExpression!);
                    if (IsConstantVariable(symbol) || IsParameterOrMutableLocalVariable(symbol) && !IsSymbolAssigned(symbol))
                    {
                        if (!WriteFlowCacheKey(b, node.Expression()!, declaredType, initialType, flowContainer))
                        {
                            return false;
                        }
                        b.WriteString(".@");
                        b.WriteSymbol(symbol);
                        return true;
                    }
                }
                break;
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
                b.WriteNode(node);
                b.WriteByte((byte)'#');
                b.WriteType(declaredType);
                return true;
        }
        return false;
    }

    // flow.go:1706-1720
    internal bool GetAccessedPropertyName(Node access, out string name)
    {
        if (NodeTests.IsPropertyAccessExpression(access))
        {
            name = access.Name()!.Text();
            return true;
        }
        if (NodeTests.IsElementAccessExpression(access))
        {
            return TryGetElementAccessExpressionName(access.AsElementAccessExpression(), out name);
        }
        if (NodeTests.IsBindingElement(access))
        {
            return GetDestructuringPropertyName(access, out name);
        }
        if (NodeTests.IsParameterDeclaration(access))
        {
            name = Array.IndexOf(access.Parent!.Parameters(), access).ToString(CultureInfo.InvariantCulture);
            return true;
        }
        name = "";
        return false;
    }

    // flow.go:1722-1730
    internal bool TryGetElementAccessExpressionName(ElementAccessExpression node, out string name)
    {
        if (AstUtilities.IsStringOrNumericLiteralLike(node.ArgumentExpression!))
        {
            name = node.ArgumentExpression!.Text();
            return true;
        }
        if (AstUtilities.IsEntityNameExpression(node.ArgumentExpression!))
        {
            return TryGetNameFromEntityNameExpression(node.ArgumentExpression!, out name);
        }
        name = "";
        return false;
    }

    // flow.go:1732-1759
    internal bool TryGetNameFromEntityNameExpression(Node node, out string name)
    {
        var symbol = ResolveEntityName(node, SymbolFlags.Value, true /*ignoreErrors*/, false, null);
        if (symbol is null || !(IsConstantVariable(symbol) || (symbol.Flags & SymbolFlags.EnumMember) != 0))
        {
            name = "";
            return false;
        }
        var declaration = symbol.ValueDeclaration;
        if (declaration is null)
        {
            name = "";
            return false;
        }
        var t = TryGetTypeFromTypeNode(declaration);
        if (t is not null)
        {
            if (TryGetNameFromType(t, out name))
            {
                return true;
            }
        }
        // We exclude binding elements because their initializers don't solely determine their types and resolving
        // full types can cause circularities (see https://github.com/microsoft/TypeScript/issues/63192).
        if (HasOnlyExpressionInitializer(declaration) && !NodeTests.IsBindingElement(declaration) && IsBlockScopedNameDeclaredBeforeUse(declaration, node))
        {
            var initializer = declaration.Initializer();
            if (initializer is not null)
            {
                var initializerType = GetTypeOfExpression(initializer);
                if (initializerType is not null)
                {
                    return TryGetNameFromType(initializerType, out name);
                }
            }
            else if (NodeTests.IsEnumMember(declaration))
            {
                return AstUtilities.TryGetTextOfPropertyName(declaration.Name()!, out name);
            }
        }
        name = "";
        return false;
    }

    // flow.go:1761-1769
    internal static bool TryGetNameFromType(Type t, out string name)
    {
        if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
        {
            name = t.AsUniqueESSymbolType().name;
            return true;
        }
        if ((t.flags & TypeFlags.StringOrNumberLiteral) != 0)
        {
            name = global::Ts.Evaluator.EvaluatorFactory.AnyToString(t.AsLiteralType().value);
            return true;
        }
        name = "";
        return false;
    }

    // flow.go:1771-1783
    internal bool GetDestructuringPropertyName(Node node, out string name)
    {
        var parent = node.Parent!;
        if (NodeTests.IsBindingElement(node) && NodeTests.IsObjectBindingPattern(parent))
        {
            return GetLiteralPropertyNameText(GetBindingElementPropertyName(node)!, out name);
        }
        if (NodeTests.IsPropertyAssignment(node) || NodeTests.IsShorthandPropertyAssignment(node))
        {
            return GetLiteralPropertyNameText(node.Name()!, out name);
        }
        if (NodeTests.IsArrayLiteralExpression(parent) || NodeTests.IsArrayBindingPattern(parent))
        {
            name = Array.IndexOf(parent.Elements()!, node).ToString(CultureInfo.InvariantCulture);
            return true;
        }
        name = "";
        return false;
    }

    // flow.go:1785-1791
    internal bool GetLiteralPropertyNameText(Node name, out string text)
    {
        var t = GetLiteralTypeFromPropertyName(name);
        if ((t.flags & (TypeFlags.StringLiteral | TypeFlags.NumberLiteral)) != 0)
        {
            text = global::Ts.Evaluator.EvaluatorFactory.AnyToString(t.AsLiteralType().value);
            return true;
        }
        text = "";
        return false;
    }

    // flow.go:1793-1818
    internal bool IsConstantReference(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ThisKeyword:
                return true;
            case SyntaxKind.Identifier:
                if (!AstUtilities.IsThisInTypeQuery(node))
                {
                    var symbol = GetResolvedSymbol(node);
                    return IsConstantVariable(symbol) || IsParameterOrMutableLocalVariable(symbol) && !IsSymbolAssigned(symbol) || symbol.ValueDeclaration is not null && NodeTests.IsFunctionExpression(symbol.ValueDeclaration);
                }
                break;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                // The resolvedSymbol property is initialized by checkPropertyAccess or checkElementAccess before we get here.
                if (IsConstantReference(node.Expression()!))
                {
                    var symbol = GetResolvedSymbolOrNil(node);
                    if (symbol is not null)
                    {
                        return IsReadonlySymbol(symbol);
                    }
                }
                break;
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
                var rootDeclaration = AstUtilities.GetRootDeclaration(node.Parent!);
                if (NodeTests.IsParameterDeclaration(rootDeclaration) || NodeTests.IsVariableDeclaration(rootDeclaration) && NodeTests.IsCatchClause(rootDeclaration.Parent!))
                {
                    return !IsSomeSymbolAssigned(rootDeclaration);
                }
                return NodeTests.IsVariableDeclaration(rootDeclaration) && IsVarConstLike(rootDeclaration);
        }
        return false;
    }

    // flow.go:1820-1828
    internal bool ContainsMatchingReference(Node source, Node target)
    {
        while (AstUtilities.IsAccessExpression(source))
        {
            source = source.Expression()!;
            if (IsMatchingReference(source, target))
            {
                return true;
            }
        }
        return false;
    }

    // flow.go:1830-1838
    internal bool OptionalChainContainsReference(Node source, Node target)
    {
        while (AstUtilities.IsOptionalChain(source))
        {
            source = source.Expression()!;
            if (IsMatchingReference(source, target))
            {
                return true;
            }
        }
        return false;
    }

    // flow.go:1840-1853
    internal Node GetReferenceCandidate(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return GetReferenceCandidate(node.Expression()!);
            case SyntaxKind.BinaryExpression:
                switch (node.AsBinaryExpression().OperatorToken!.Kind)
                {
                    case SyntaxKind.EqualsToken:
                    case SyntaxKind.BarBarEqualsToken:
                    case SyntaxKind.AmpersandAmpersandEqualsToken:
                    case SyntaxKind.QuestionQuestionEqualsToken:
                        return GetReferenceCandidate(node.AsBinaryExpression().Left!);
                    case SyntaxKind.CommaToken:
                        return GetReferenceCandidate(node.AsBinaryExpression().Right!);
                }
                break;
        }
        return node;
    }

    // flow.go:1855-1863
    internal Node GetReferenceRoot(Node node)
    {
        var parent = node.Parent!;
        if (NodeTests.IsParenthesizedExpression(parent) ||
            NodeTests.IsBinaryExpression(parent) && parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken && parent.AsBinaryExpression().Left == node ||
            NodeTests.IsBinaryExpression(parent) && parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken && parent.AsBinaryExpression().Right == node)
        {
            return GetReferenceRoot(parent);
        }
        return node;
    }

    // flow.go:1865-1875
    internal bool HasMatchingArgument(Node expression, Node reference)
    {
        foreach (var argument in expression.Arguments() ?? [])
        {
            if (IsOrContainsMatchingReference(reference, argument) || OptionalChainContainsReference(argument, reference))
            {
                return true;
            }
        }
        if (NodeTests.IsPropertyAccessExpression(expression.Expression()!) && IsOrContainsMatchingReference(reference, expression.Expression()!.Expression()!))
        {
            return true;
        }
        return false;
    }

    // flow.go:1877-1879
    internal bool IsOrContainsMatchingReference(Node source, Node target)
    {
        return IsMatchingReference(source, target) || ContainsMatchingReference(source, target);
    }

    // Return a new type in which occurrences of the string, number and bigint primitives and placeholder template
    // literal types in typeWithPrimitives have been replaced with occurrences of compatible and more specific types
    // from typeWithLiterals. This is essentially a limited form of intersection between the two types. We avoid a
    // true intersection because it is more costly and, when applied to union types, generates a large number of
    // types we don't actually care about.
    // flow.go:1886-1905
    internal Type ReplacePrimitivesWithLiterals(Type typeWithPrimitives, Type typeWithLiterals)
    {
        if (MaybeTypeOfKind(typeWithPrimitives, TypeFlags.String | TypeFlags.TemplateLiteral | TypeFlags.Number | TypeFlags.BigInt) &&
            MaybeTypeOfKind(typeWithLiterals, TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping | TypeFlags.NumberLiteral | TypeFlags.BigIntLiteral))
        {
            return MapType(typeWithPrimitives, t =>
            {
                if ((t.flags & TypeFlags.String) != 0)
                {
                    return ExtractTypesOfKind(typeWithLiterals, TypeFlags.String | TypeFlags.StringLiteral | TypeFlags.TemplateLiteral | TypeFlags.StringMapping);
                }
                if (IsPatternLiteralType(t) && !MaybeTypeOfKind(typeWithLiterals, TypeFlags.String | TypeFlags.TemplateLiteral | TypeFlags.StringMapping))
                {
                    return ExtractTypesOfKind(typeWithLiterals, TypeFlags.StringLiteral);
                }
                if ((t.flags & TypeFlags.Number) != 0)
                {
                    return ExtractTypesOfKind(typeWithLiterals, TypeFlags.Number | TypeFlags.NumberLiteral);
                }
                if ((t.flags & TypeFlags.BigInt) != 0)
                {
                    return ExtractTypesOfKind(typeWithLiterals, TypeFlags.BigInt | TypeFlags.BigIntLiteral);
                }
                return t;
            });
        }
        return typeWithPrimitives;
    }

    // flow.go:1907-1910
    internal static bool IsCoercibleUnderDoubleEquals(Type source, Type target)
    {
        return (source.flags & (TypeFlags.Number | TypeFlags.String | TypeFlags.BooleanLiteral)) != 0 &&
            (target.flags & (TypeFlags.Number | TypeFlags.String | TypeFlags.Boolean)) != 0;
    }

    // flow.go:1912-1926
    internal bool IsExhaustiveSwitchStatement(Node node)
    {
        var links = switchStatementLinks.Get(node);
        if (links.exhaustiveState == ExhaustiveState.Unknown)
        {
            // Indicate resolution is in process
            links.exhaustiveState = ExhaustiveState.Computing;
            var isExhaustive = ComputeExhaustiveSwitchStatement(node);
            if (links.exhaustiveState == ExhaustiveState.Computing)
            {
                links.exhaustiveState = CoreUtil.IfElse(isExhaustive, ExhaustiveState.True, ExhaustiveState.False);
            }
        }
        else if (links.exhaustiveState == ExhaustiveState.Computing)
        {
            // Resolve circularity to false
            links.exhaustiveState = ExhaustiveState.False;
        }
        return links.exhaustiveState == ExhaustiveState.True;
    }

    // flow.go:1928-1955
    internal bool ComputeExhaustiveSwitchStatement(Node node)
    {
        if (NodeTests.IsTypeOfExpression(node.Expression()!))
        {
            var witnesses = GetSwitchClauseTypeOfWitnesses(node);
            if (witnesses is null)
            {
                return false;
            }
            var operandConstraint = GetBaseConstraintOrType(CheckExpressionCached(node.Expression()!.Expression()!));
            // Get the not-equal flags for all handled cases.
            var notEqualFacts = GetNotEqualFactsFromTypeofSwitch(0, 0, witnesses);
            if ((operandConstraint.flags & TypeFlags.AnyOrUnknown) != 0)
            {
                // We special case the top types to be exhaustive when all cases are handled.
                return (TypeFacts.AllTypeofNE & notEqualFacts) == TypeFacts.AllTypeofNE;
            }
            // A missing not-equal flag indicates that the type wasn't handled by some case.
            return !SomeType(operandConstraint, t =>
                GetTypeFacts(t, notEqualFacts) == notEqualFacts);
        }
        var typeConstraint = GetBaseConstraintOrType(CheckExpressionCached(node.Expression()!));
        if (!IsLiteralType(typeConstraint))
        {
            return false;
        }
        var switchTypes = GetSwitchClauseTypes(node);
        if (switchTypes.Length == 0 || CoreUtil.Some(switchTypes, IsNeitherUnitTypeNorNever))
        {
            return false;
        }
        return EachTypeContainedIn(MapType(typeConstraint, GetRegularTypeOfLiteralType), switchTypes);
    }

    // flow.go:1957-1964
    internal bool EachTypeContainedIn(Type source, Type[] types)
    {
        if ((source.flags & TypeFlags.Union) != 0)
        {
            return !CoreUtil.Some(source.AsUnionType().types, t =>
                !SlicesContains(types, t));
        }
        return SlicesContains(types, source);
    }

    // Get the type names from all cases in a switch on `typeof`. The default clause and/or duplicate type names are
    // represented as empty strings. Return nil if one or more case clause expressions are not string literals.
    // flow.go:1968-1988
    internal string[]? GetSwitchClauseTypeOfWitnesses(Node node)
    {
        var links = switchStatementLinks.Get(node);
        if (!links.witnessesComputed)
        {
            var clauses = node.AsSwitchStatement().CaseBlock!.AsCaseBlock().Clauses!.Nodes;
            string[]? witnesses = new string[clauses.Length];
            for (int i = 0; i < clauses.Length; i++)
            {
                var clause = clauses[i];
                if (clause.Kind == SyntaxKind.CaseClause)
                {
                    if (!AstUtilities.IsStringLiteralLike(clause.Expression()!))
                    {
                        witnesses = null;
                        break;
                    }
                    var text = clause.Expression()!.Text();
                    if (Array.IndexOf(witnesses, text) < 0)
                    {
                        witnesses[i] = text;
                    }
                }
            }
            links.witnesses = witnesses;
            links.witnessesComputed = true;
        }
        return links.witnesses;
    }

    // Return the combined not-equal type facts for all cases except those between the start and end indices.
    // flow.go:1991-2003
    internal TypeFacts GetNotEqualFactsFromTypeofSwitch(int start, int end, string[] witnesses)
    {
        TypeFacts facts = TypeFacts.None;
        for (int i = 0; i < witnesses.Length; i++)
        {
            var witness = witnesses[i];
            if ((i < start || i >= end) && witness != "")
            {
                if (!TypeofNEFacts.TryGetValue(witness, out var fF))
                {
                    fF = TypeFacts.TypeofNEHostObject;
                }
                facts |= fF;
            }
        }
        return facts;
    }

    // flow.go:2005-2017
    internal Type[] GetSwitchClauseTypes(Node node)
    {
        var links = switchStatementLinks.Get(node);
        if (!links.switchTypesComputed)
        {
            var clauses = node.AsSwitchStatement().CaseBlock!.AsCaseBlock().Clauses!.Nodes;
            var types = new Type[clauses.Length];
            for (int i = 0; i < clauses.Length; i++)
            {
                types[i] = GetTypeOfSwitchClause(clauses[i]);
            }
            links.switchTypes = types;
            links.switchTypesComputed = true;
        }
        return links.switchTypes!;
    }

    // flow.go:2019-2024
    internal Type GetTypeOfSwitchClause(Node clause)
    {
        if (clause.Kind == SyntaxKind.CaseClause)
        {
            return GetRegularTypeOfLiteralType(GetTypeOfExpression(clause.Expression()!));
        }
        return neverType!;
    }

    // flow.go:2026-2067
    internal Signature? GetEffectsSignature(Node node)
    {
        var links = signatureLinks.Get(node);
        var signature = links.effectsSignature;
        if (signature is null)
        {
            // A call expression parented by an expression statement is a potential assertion. Other call
            // expressions are potential type predicate function calls. In order to avoid triggering
            // circularities in control flow analysis, we use getTypeOfDottedName when resolving the call
            // target expression of an assertion.
            Type? funcType = null;
            if (NodeTests.IsBinaryExpression(node))
            {
                var rightType = CheckNonNullExpression(node.AsBinaryExpression().Right!);
                funcType = GetSymbolHasInstanceMethodOfObjectType(rightType);
            }
            else if (NodeTests.IsExpressionStatement(node.Parent!))
            {
                funcType = GetTypeOfDottedName(node.Expression()!, null /*diagnostic*/);
            }
            else if (node.Expression()!.Kind != SyntaxKind.SuperKeyword)
            {
                if (AstUtilities.IsOptionalChain(node))
                {
                    funcType = CheckNonNullType(GetOptionalExpressionType(CheckExpression(node.Expression()!), node.Expression()!), node.Expression()!);
                }
                else
                {
                    funcType = CheckNonNullExpression(node.Expression()!);
                }
            }
            Type? apparentType = null;
            if (funcType is not null)
            {
                apparentType = GetApparentType(funcType);
            }
            var signatures = GetSignaturesOfType(CoreUtil.OrElse(apparentType, unknownType!)!, SignatureKind.Call);
            if (signatures.Length == 1 && (signatures[0].typeParameters is null || signatures[0].typeParameters!.Length == 0))
            {
                signature = signatures[0];
            }
            else if (CoreUtil.Some(signatures, HasTypePredicateOrNeverReturnType))
            {
                signature = GetResolvedSignature(node, null, CheckMode.Normal);
            }
            if (!(signature is not null && HasTypePredicateOrNeverReturnType(signature)))
            {
                signature = unknownSignature;
            }
            links.effectsSignature = signature;
        }
        if (signature == unknownSignature)
        {
            return null;
        }
        return signature;
    }

    // Get the type of the `[Symbol.hasInstance]` method of an object type.
    // flow.go:2072-2084
    internal Type? GetSymbolHasInstanceMethodOfObjectType(Type t)
    {
        var hasInstancePropertyName = GetPropertyNameForKnownSymbolName("hasInstance");
        if (AllTypesAssignableToKind(t, TypeFlags.NonPrimitive))
        {
            var hasInstanceProperty = GetPropertyOfType(t, hasInstancePropertyName);
            if (hasInstanceProperty is not null)
            {
                var hasInstancePropertyType = GetTypeOfSymbol(hasInstanceProperty);
                if (hasInstancePropertyType is not null && GetSignaturesOfType(hasInstancePropertyType, SignatureKind.Call).Length != 0)
                {
                    return hasInstancePropertyType;
                }
            }
        }
        return null;
    }

    // flow.go:2086-2095
    internal string GetPropertyNameForKnownSymbolName(string symbolName)
    {
        var ctorType = GetGlobalESSymbolConstructorSymbolOrNil();
        if (ctorType is not null)
        {
            var uniqueType = GetTypeOfPropertyOfType(GetTypeOfSymbol(ctorType), symbolName);
            if (uniqueType is not null && IsTypeUsableAsPropertyName(uniqueType))
            {
                return GetPropertyNameFromType(uniqueType);
            }
        }
        return global::Ts.Ast.InternalSymbolName.Prefix + "@" + symbolName;
    }

    // We require the dotted function name in an assertion expression to be comprised of identifiers
    // that reference function, method, class or value module symbols; or variable, property or
    // parameter symbols with declarations that have explicit type annotations. Such references are
    // resolvable with no possibility of triggering circularities in control flow analysis.
    // flow.go:2101-2132
    internal Type? GetTypeOfDottedName(Node node, Diagnostic? diagnostic)
    {
        if ((node.Flags & NodeFlags.InWithStatement) == 0)
        {
            switch (node.Kind)
            {
                case SyntaxKind.Identifier:
                {
                    var symbol = GetExportSymbolOfValueSymbolIfExported(GetResolvedSymbol(node));
                    return GetExplicitTypeOfSymbol(symbol!, diagnostic);
                }
                case SyntaxKind.ThisKeyword:
                    return GetExplicitThisType(node);
                case SyntaxKind.SuperKeyword:
                    return CheckSuperExpression(node);
                case SyntaxKind.PropertyAccessExpression:
                {
                    var t = GetTypeOfDottedName(node.Expression()!, diagnostic);
                    if (t is not null)
                    {
                        var name = node.Name()!;
                        Symbol? prop;
                        if (NodeTests.IsPrivateIdentifier(name))
                        {
                            prop = null;
                            if (t.symbol is not null)
                            {
                                prop = GetPropertyOfType(t, global::Ts.Binder.Binder.GetSymbolNameForPrivateIdentifier(t.symbol, name.Text()));
                            }
                        }
                        else
                        {
                            prop = GetPropertyOfType(t, name.Text());
                        }
                        if (prop is not null)
                        {
                            return GetExplicitTypeOfSymbol(prop, diagnostic);
                        }
                    }
                    break;
                }
                case SyntaxKind.ParenthesizedExpression:
                    return GetTypeOfDottedName(node.Expression()!, diagnostic);
            }
        }
        return null;
    }

    // flow.go:2134-2170
    internal Type? GetExplicitTypeOfSymbol(Symbol symbol, Diagnostic? diagnostic)
    {
        symbol = ResolveSymbol(symbol)!;
        if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method | SymbolFlags.Class | SymbolFlags.ValueModule)) != 0)
        {
            return GetTypeOfSymbol(symbol);
        }
        if ((symbol.Flags & (SymbolFlags.Variable | SymbolFlags.Property)) != 0)
        {
            if ((symbol.CheckFlags & CheckFlags.Mapped) != 0)
            {
                var origin = mappedSymbolLinks.Get(symbol).syntheticOrigin;
                if (origin is not null && GetExplicitTypeOfSymbol(origin, diagnostic) is not null)
                {
                    return GetTypeOfSymbol(symbol);
                }
            }
            var declaration = symbol.ValueDeclaration;
            if (declaration is not null)
            {
                if (IsDeclarationWithExplicitTypeAnnotation(declaration))
                {
                    return GetTypeOfSymbol(symbol);
                }
                if (NodeTests.IsVariableDeclaration(declaration) && NodeTests.IsForOfStatement(declaration.Parent!.Parent!))
                {
                    var statement = declaration.Parent!.Parent!;
                    var expressionType = GetTypeOfDottedName(statement.Expression()!, null /*diagnostic*/);
                    if (expressionType is not null)
                    {
                        IterationUse use;
                        if (statement.AsForInOrOfStatement().AwaitModifier is not null)
                        {
                            use = IterationUse.ForAwaitOf;
                        }
                        else
                        {
                            use = IterationUse.ForOf;
                        }
                        return CheckIteratedTypeOrElementType(use, expressionType, undefinedType!, null /*errorNode*/);
                    }
                }
                if (diagnostic is not null)
                {
                    diagnostic.AddRelatedInfo(CreateDiagnosticForNode(declaration, Diag.X_0_needs_an_explicit_type_annotation, SymbolToString(symbol)));
                }
            }
        }
        return null;
    }

    // flow.go:2172-2175
    internal bool IsDeclarationWithExplicitTypeAnnotation(Node node)
    {
        return (NodeTests.IsVariableDeclaration(node) || NodeTests.IsPropertyDeclaration(node) || NodeTests.IsPropertySignatureDeclaration(node) || NodeTests.IsParameterDeclaration(node)) && node.Type() is not null ||
            IsExpandoPropertyFunctionWithReturnTypeAnnotation(node);
    }

    // flow.go:2177-2184
    internal bool IsExpandoPropertyFunctionWithReturnTypeAnnotation(Node node)
    {
        if (NodeTests.IsBinaryExpression(node))
        {
            var expr = node.AsBinaryExpression().Right!;
            if (AstUtilities.IsFunctionLike(expr) && expr.Type() is not null)
            {
                return true;
            }
        }
        return false;
    }

    // flow.go:2186-2188
    internal bool HasTypePredicateOrNeverReturnType(Signature sig)
    {
        return GetTypePredicateOfSignature(sig) is not null || sig.declaration is not null && (CoreUtil.OrElse(GetReturnTypeFromAnnotation(sig.declaration!), unknownType!)!.flags & TypeFlags.Never) != 0;
    }

    // flow.go:2190-2207
    internal Type? GetExplicitThisType(Node node)
    {
        var container = AstUtilities.GetThisContainer(node, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
        if (AstUtilities.IsFunctionLike(container))
        {
            var signature = GetSignatureFromDeclaration(container);
            if (signature.thisParameter is not null)
            {
                return GetExplicitTypeOfSymbol(signature.thisParameter, null);
            }
        }
        if (container.Parent is not null && AstUtilities.IsClassLike(container.Parent))
        {
            var symbol = GetSymbolOfDeclaration(container.Parent);
            if (AstUtilities.IsStatic(container))
            {
                return GetTypeOfSymbol(symbol!);
            }
            else
            {
                return GetDeclaredTypeOfSymbol(symbol!)!.AsInterfaceType()!.thisType;
            }
        }
        return null;
    }

    // flow.go:2209-2217
    internal Type GetInitialType(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                return GetInitialTypeOfVariableDeclaration(node);
            case SyntaxKind.BindingElement:
                return GetInitialTypeOfBindingElement(node);
        }
        throw new InvalidOperationException("Unhandled case in getInitialType");
    }

    // flow.go:2219-2233
    internal Type GetInitialTypeOfVariableDeclaration(Node node)
    {
        if (node.Initializer() is not null)
        {
            return GetTypeOfInitializer(node.Initializer()!);
        }
        if (NodeTests.IsForInStatement(node.Parent!.Parent!))
        {
            return stringType!;
        }
        if (NodeTests.IsForOfStatement(node.Parent!.Parent!))
        {
            var t = CheckRightHandSideOfForOf(node.Parent!.Parent!);
            if (t is not null)
            {
                return t;
            }
        }
        return errorType!;
    }

    // flow.go:2235-2246
    internal Type GetTypeOfInitializer(Node node)
    {
        // Return the cached type if one is available. If the type of the variable was inferred
        // from its initializer, we'll already have cached the type. Otherwise we compute it now
        // without caching such that transient types are reflected.
        if (typeNodeLinks.Has(node))
        {
            var t = typeNodeLinks.Get(node).resolvedType;
            if (t is not null)
            {
                return t;
            }
        }
        return GetTypeOfExpression(node);
    }

    // flow.go:2248-2261
    internal Type GetInitialTypeOfBindingElement(Node node)
    {
        var pattern = node.Parent!;
        var parentType = GetInitialType(pattern.Parent!);
        Type? t;
        if (NodeTests.IsObjectBindingPattern(pattern))
        {
            t = GetTypeOfDestructuredProperty(parentType, GetBindingElementPropertyName(node)!);
        }
        else if (!HasDotDotDotToken(node))
        {
            t = GetTypeOfDestructuredArrayElement(parentType, Array.IndexOf(pattern.Elements()!, node));
        }
        else
        {
            t = GetTypeOfDestructuredSpreadExpression(parentType);
        }
        return GetTypeWithDefault(t!, node.Initializer());
    }

    // flow.go:2263-2287
    internal Type GetAssignedType(Node node)
    {
        var parent = node.Parent!;
        switch (parent.Kind)
        {
            case SyntaxKind.ForInStatement:
                return stringType!;
            case SyntaxKind.ForOfStatement:
            {
                var t = CheckRightHandSideOfForOf(parent);
                if (t is not null)
                {
                    return t;
                }
                break;
            }
            case SyntaxKind.BinaryExpression:
                return GetAssignedTypeOfBinaryExpression(parent);
            case SyntaxKind.DeleteExpression:
                return undefinedType!;
            case SyntaxKind.ArrayLiteralExpression:
                return GetAssignedTypeOfArrayLiteralElement(parent, node);
            case SyntaxKind.SpreadElement:
                return GetAssignedTypeOfSpreadExpression(parent);
            case SyntaxKind.PropertyAssignment:
                return GetAssignedTypeOfPropertyAssignment(parent);
            case SyntaxKind.ShorthandPropertyAssignment:
                return GetAssignedTypeOfShorthandPropertyAssignment(parent);
        }
        return errorType!;
    }

    // flow.go:2289-2296
    internal Type GetAssignedTypeOfBinaryExpression(Node node)
    {
        var isDestructuringDefaultAssignment = NodeTests.IsArrayLiteralExpression(node.Parent!) && IsDestructuringAssignmentTarget(node.Parent!) ||
            NodeTests.IsPropertyAssignment(node.Parent!) && IsDestructuringAssignmentTarget(node.Parent!.Parent!);
        if (isDestructuringDefaultAssignment)
        {
            return GetTypeWithDefault(GetAssignedType(node), node.AsBinaryExpression().Right);
        }
        return GetTypeOfExpression(node.AsBinaryExpression().Right!);
    }

    // flow.go:2298-2300
    internal Type GetAssignedTypeOfArrayLiteralElement(Node node, Node element)
    {
        return GetTypeOfDestructuredArrayElement(GetAssignedType(node), Array.IndexOf(node.Elements()!, element));
    }

    // flow.go:2302-2312
    internal Type GetTypeOfDestructuredArrayElement(Type t, int index)
    {
        if (EveryType(t, IsTupleLikeType))
        {
            var elementType0 = GetTupleElementType(t, index);
            if (elementType0 is not null)
            {
                return elementType0;
            }
        }
        var elementType = CheckIteratedTypeOrElementType(IterationUse.Destructuring, t, undefinedType!, null /*errorNode*/);
        if (elementType is not null)
        {
            return IncludeUndefinedInIndexSignature(elementType)!;
        }
        return errorType!;
    }

    // flow.go:2314-2322
    internal Type? IncludeUndefinedInIndexSignature(Type? t)
    {
        if (t is null)
        {
            return null;
        }
        if (compilerOptions.NoUncheckedIndexedAccess == Tristate.True)
        {
            return GetUnionType([t, missingType!]);
        }
        return t;
    }

    // flow.go:2324-2326
    internal Type GetAssignedTypeOfSpreadExpression(Node node)
    {
        return GetTypeOfDestructuredSpreadExpression(GetAssignedType(node.Parent!));
    }

    // flow.go:2328-2334
    internal Type GetTypeOfDestructuredSpreadExpression(Type t)
    {
        var elementType = CheckIteratedTypeOrElementType(IterationUse.Destructuring, t, undefinedType!, null /*errorNode*/);
        if (elementType is null)
        {
            elementType = errorType!;
        }
        return CreateArrayType(elementType);
    }

    // flow.go:2336-2338
    internal Type GetAssignedTypeOfPropertyAssignment(Node node)
    {
        return GetTypeOfDestructuredProperty(GetAssignedType(node.Parent!), node.Name()!);
    }

    // flow.go:2340-2353
    internal Type GetTypeOfDestructuredProperty(Type t, Node name)
    {
        var nameType = GetLiteralTypeFromPropertyName(name);
        if (!IsTypeUsableAsPropertyName(nameType))
        {
            return errorType!;
        }
        var text = GetPropertyNameFromType(nameType);
        var propType = GetTypeOfPropertyOfType(t, text);
        if (propType is not null)
        {
            return propType;
        }
        var indexInfo = GetApplicableIndexInfoForName(t, text);
        if (indexInfo is not null)
        {
            return IncludeUndefinedInIndexSignature(indexInfo.valueType)!;
        }
        return errorType!;
    }

    // flow.go:2355-2357
    internal Type GetAssignedTypeOfShorthandPropertyAssignment(Node node)
    {
        return GetTypeWithDefault(GetAssignedTypeOfPropertyAssignment(node), node.AsShorthandPropertyAssignment().ObjectAssignmentInitializer);
    }

    // flow.go:2359-2362
    internal bool IsDestructuringAssignmentTarget(Node parent)
    {
        return NodeTests.IsBinaryExpression(parent.Parent!) && parent.Parent!.AsBinaryExpression().Left == parent ||
            NodeTests.IsForOfStatement(parent.Parent!) && parent.Parent!.Initializer() == parent;
    }

    // flow.go:2364-2369
    internal Type GetTypeWithDefault(Type t, Node? defaultExpression)
    {
        if (defaultExpression is not null)
        {
            return GetUnionType([GetNonUndefinedType(t), GetTypeOfExpression(defaultExpression)]);
        }
        return t;
    }

    // Remove those constituent types of declaredType to which no constituent type of assignedType is assignable.
    // For example, when a variable of type number | string | boolean is assigned a value of type number | boolean,
    // we remove type string.
    // flow.go:2374-2388
    internal Type GetAssignmentReducedType(Type declaredType, Type assignedType)
    {
        if (declaredType == assignedType)
        {
            return declaredType;
        }
        if ((assignedType.flags & TypeFlags.Never) != 0)
        {
            return assignedType;
        }
        var key = new AssignmentReducedKey(declaredType.id, assignedType.id);
        if (!assignmentReducedTypes.TryGetValue(key, out var result))
        {
            result = GetAssignmentReducedTypeWorker(declaredType, assignedType);
            assignmentReducedTypes[key] = result;
        }
        return result;
    }

    // flow.go:2390-2407
    internal Type GetAssignmentReducedTypeWorker(Type declaredType, Type assignedType)
    {
        var filteredType = FilterType(declaredType, t =>
            TypeMaybeAssignableTo(assignedType, t));
        // Ensure that we narrow to fresh types if the assignment is a fresh boolean literal type.
        var reducedType = filteredType;
        if ((assignedType.flags & TypeFlags.BooleanLiteral) != 0 && IsFreshLiteralType(assignedType))
        {
            reducedType = MapType(filteredType, GetFreshTypeOfLiteralType);
        }
        // Our crude heuristic produces an invalid result in some cases: see GH#26130.
        // For now, when that happens, we give up and don't narrow at all.  (This also
        // means we'll never narrow for erroneous assignments where the assigned type
        // is not assignable to the declared type.)
        if (IsTypeAssignableTo(assignedType, reducedType))
        {
            return reducedType;
        }
        return declaredType;
    }

    // flow.go:2409-2419
    internal bool TypeMaybeAssignableTo(Type source, Type target)
    {
        if ((source.flags & TypeFlags.Union) == 0)
        {
            return IsTypeAssignableTo(source, target);
        }
        foreach (var t in source.AsUnionType().types)
        {
            if (IsTypeAssignableTo(t, target))
            {
                return true;
            }
        }
        return false;
    }

    // flow.go:2421-2434
    internal Node? GetTypePredicateArgument(TypePredicate predicate, Node callExpression)
    {
        if (predicate.kind == TypePredicateKind.Identifier || predicate.kind == TypePredicateKind.AssertsIdentifier)
        {
            var arguments = callExpression.Arguments();
            if (predicate.parameterIndex >= 0 && predicate.parameterIndex < (arguments?.Length ?? 0))
            {
                return arguments![predicate.parameterIndex];
            }
        }
        else
        {
            var invokedExpression = AstUtilities.SkipParentheses(callExpression.Expression()!);
            if (AstUtilities.IsAccessExpression(invokedExpression))
            {
                return AstUtilities.SkipParentheses(invokedExpression.Expression()!);
            }
        }
        return null;
    }

    // flow.go:2436-2456
    internal Type? GetFlowTypeInConstructor(Symbol symbol, Node constructor)
    {
        Node accessName;
        if (symbol.Name.StartsWith(global::Ts.Ast.InternalSymbolName.Prefix + "#", StringComparison.Ordinal))
        {
            accessName = factory.NewPrivateIdentifier(symbol.Name[(symbol.Name.IndexOf('@', StringComparison.Ordinal) + 1)..]);
        }
        else
        {
            accessName = factory.NewIdentifier(symbol.Name);
        }
        var reference = factory.NewPropertyAccessExpression(factory.NewKeywordExpression(KeywordExpressionSyntaxKind.ThisKeyword), null, accessName, NodeFlags.None);
        reference.Expression()!.Parent = reference;
        reference.Parent = constructor;
        reference.FlowNodeData()!.FlowNode = constructor.AsConstructorDeclaration().ReturnFlowNode;
        var flowType = GetFlowTypeOfProperty(reference, symbol);
        if (noImplicitAny && (flowType == autoType || flowType == autoArrayType))
        {
            Error(symbol.ValueDeclaration, Diag.Member_0_implicitly_has_an_1_type, SymbolToString(symbol), TypeToString(flowType));
        }
        // We don't infer a type if assignments are only null or undefined.
        if (EveryType(flowType, IsNullableType))
        {
            return null;
        }
        return ConvertAutoToAny(flowType);
    }

    // flow.go:2458-2481
    internal Type? GetFlowTypeInStaticBlocks(Symbol symbol, Node[] staticBlocks)
    {
        Node accessName;
        if (symbol.Name.StartsWith(global::Ts.Ast.InternalSymbolName.Prefix + "#", StringComparison.Ordinal))
        {
            accessName = factory.NewPrivateIdentifier(symbol.Name[(symbol.Name.IndexOf('@', StringComparison.Ordinal) + 1)..]);
        }
        else
        {
            accessName = factory.NewIdentifier(symbol.Name);
        }
        foreach (var staticBlock in staticBlocks)
        {
            var reference = factory.NewPropertyAccessExpression(factory.NewKeywordExpression(KeywordExpressionSyntaxKind.ThisKeyword), null, accessName, NodeFlags.None);
            reference.Expression()!.Parent = reference;
            reference.Parent = staticBlock;
            reference.FlowNodeData()!.FlowNode = staticBlock.AsClassStaticBlockDeclaration().ReturnFlowNode;
            var flowType = GetFlowTypeOfProperty(reference, symbol);
            if (noImplicitAny && (flowType == autoType || flowType == autoArrayType))
            {
                Error(symbol.ValueDeclaration, Diag.Member_0_implicitly_has_an_1_type, SymbolToString(symbol), TypeToString(flowType));
            }
            // We don't infer a type if assignments are only null or undefined.
            if (EveryType(flowType, IsNullableType))
            {
                continue;
            }
            return ConvertAutoToAny(flowType);
        }
        return null;
    }

    // flow.go:2483-2490
    internal bool IsReachableFlowNode(FlowNode flow)
    {
        var f = GetFlowState();
        var result = IsReachableFlowNodeWorker(f, flow, false /*noCacheCheck*/);
        PutFlowState(f);
        lastFlowNode = flow;
        lastFlowNodeReachable = result;
        return result;
    }

    // flow.go:2492-2557
    internal bool IsReachableFlowNodeWorker(FlowState f, FlowNode flow, bool noCacheCheck)
    {
        while (true)
        {
            if (flow == lastFlowNode)
            {
                return lastFlowNodeReachable;
            }
            var flags = flow.Flags;
            if ((flags & FlowFlags.Shared) != 0)
            {
                if (!noCacheCheck)
                {
                    if (flowNodeReachable.TryGetValue(flow, out var reachableCached))
                    {
                        return reachableCached;
                    }
                    var reachable = IsReachableFlowNodeWorker(f, flow, true /*noCacheCheck*/);
                    flowNodeReachable[flow] = reachable;
                    return reachable;
                }
                noCacheCheck = false;
            }
            if ((flags & (FlowFlags.Assignment | FlowFlags.Condition | FlowFlags.ArrayMutation)) != 0)
            {
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.Call) != 0)
            {
                var signature = GetEffectsSignature(flow.Node!);
                if (signature is not null)
                {
                    var predicate = GetTypePredicateOfSignature(signature);
                    if (predicate is not null && predicate.kind == TypePredicateKind.AssertsIdentifier && predicate.t is null)
                    {
                        var arguments = flow.Node!.Arguments();
                        if (predicate.parameterIndex >= 0 && predicate.parameterIndex < (arguments?.Length ?? 0) && IsFalseExpression(arguments![predicate.parameterIndex]))
                        {
                            return false;
                        }
                    }
                    if ((GetReturnTypeOfSignature(signature).flags & TypeFlags.Never) != 0)
                    {
                        return false;
                    }
                }
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.BranchLabel) != 0)
            {
                // A branching point is reachable if any branch is reachable.
                for (var list = GetBranchLabelAntecedents(flow, f.reduceLabels); list is not null; list = list.Next)
                {
                    if (IsReachableFlowNodeWorker(f, list.Flow!, false /*noCacheCheck*/))
                    {
                        return true;
                    }
                }
                return false;
            }
            else if ((flags & FlowFlags.LoopLabel) != 0)
            {
                if (flow.Antecedents is null)
                {
                    return false;
                }
                // A loop is reachable if the control flow path that leads to the top is reachable.
                flow = flow.Antecedents.Flow!;
            }
            else if ((flags & FlowFlags.SwitchClause) != 0)
            {
                // The control flow path representing an unmatched value in a switch statement with
                // no default clause is unreachable if the switch statement is exhaustive.
                var data = flow.Node!.AsFlowSwitchClauseData();
                if (data.ClauseStart == data.ClauseEnd && IsExhaustiveSwitchStatement(data.SwitchStatement!))
                {
                    return false;
                }
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.ReduceLabel) != 0)
            {
                // Cache is unreliable once we start adjusting labels
                lastFlowNode = null;
                f.reduceLabels.Add(flow.Node!.AsFlowReduceLabelData());
                var result = IsReachableFlowNodeWorker(f, flow.Antecedent!, false /*noCacheCheck*/);
                f.reduceLabels.RemoveAt(f.reduceLabels.Count - 1);
                return result;
            }
            else
            {
                return (flags & FlowFlags.Unreachable) == 0;
            }
        }
    }

    // flow.go:2559-2570
    internal bool IsFalseExpression(Node expr)
    {
        var node = AstUtilities.SkipParentheses(expr);
        if (node.Kind == SyntaxKind.FalseKeyword)
        {
            return true;
        }
        if (NodeTests.IsBinaryExpression(node))
        {
            var binary = node.AsBinaryExpression();
            return binary.OperatorToken!.Kind == SyntaxKind.AmpersandAmpersandToken && (IsFalseExpression(binary.Left!) || IsFalseExpression(binary.Right!)) ||
                binary.OperatorToken.Kind == SyntaxKind.BarBarToken && IsFalseExpression(binary.Left!) && IsFalseExpression(binary.Right!);
        }
        return false;
    }

    // Return true if the given flow node is preceded by a 'super(...)' call in every possible code path
    // leading to the node.
    // flow.go:2574-2579
    internal bool IsPostSuperFlowNode(FlowNode flow, bool noCacheCheck)
    {
        var f = GetFlowState();
        var result = IsPostSuperFlowNodeWorker(f, flow, noCacheCheck);
        PutFlowState(f);
        return result;
    }

    // flow.go:2581-2622
    internal bool IsPostSuperFlowNodeWorker(FlowState f, FlowNode flow, bool noCacheCheck)
    {
        while (true)
        {
            var flags = flow.Flags;
            if ((flags & FlowFlags.Shared) != 0)
            {
                if (!noCacheCheck)
                {
                    if (flowNodePostSuper.TryGetValue(flow, out var postSuperCached))
                    {
                        return postSuperCached;
                    }
                    var postSuper = IsPostSuperFlowNodeWorker(f, flow, true /*noCacheCheck*/);
                    flowNodePostSuper[flow] = postSuper;
                }
                noCacheCheck = false;
            }
            if ((flags & (FlowFlags.Assignment | FlowFlags.Condition | FlowFlags.ArrayMutation | FlowFlags.SwitchClause)) != 0)
            {
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.Call) != 0)
            {
                if (flow.Node!.Expression()!.Kind == SyntaxKind.SuperKeyword)
                {
                    return true;
                }
                flow = flow.Antecedent!;
            }
            else if ((flags & FlowFlags.BranchLabel) != 0)
            {
                for (var list = GetBranchLabelAntecedents(flow, f.reduceLabels); list is not null; list = list.Next)
                {
                    if (!IsPostSuperFlowNodeWorker(f, list.Flow!, false /*noCacheCheck*/))
                    {
                        return false;
                    }
                }
                return true;
            }
            else if ((flags & FlowFlags.LoopLabel) != 0)
            {
                // A loop is post-super if the control flow path that leads to the top is post-super.
                flow = flow.Antecedents!.Flow!;
            }
            else if ((flags & FlowFlags.ReduceLabel) != 0)
            {
                f.reduceLabels.Add(flow.Node!.AsFlowReduceLabelData());
                var result = IsPostSuperFlowNodeWorker(f, flow.Antecedent!, false /*noCacheCheck*/);
                f.reduceLabels.RemoveAt(f.reduceLabels.Count - 1);
                return result;
            }
            else
            {
                // Unreachable nodes are considered post-super to silence errors
                return (flags & FlowFlags.Unreachable) != 0;
            }
        }
    }

    // Check if a parameter, catch variable, or mutable local variable is definitely assigned anywhere
    // flow.go:2625-2628
    internal bool IsSymbolAssignedDefinitely(Symbol symbol)
    {
        EnsureAssignmentsMarked(symbol);
        return markedAssignmentSymbolLinks.Get(symbol).hasDefiniteAssignment;
    }

    // Check if a parameter, catch variable, or mutable local variable is assigned anywhere
    // flow.go:2631-2634
    internal bool IsSymbolAssigned(Symbol symbol)
    {
        EnsureAssignmentsMarked(symbol);
        return markedAssignmentSymbolLinks.Get(symbol).lastAssignmentPos != 0;
    }

    // Return true if there are no assignments to the given symbol or if the given location
    // is past the last assignment to the symbol.
    // flow.go:2638-2642
    internal bool IsPastLastAssignment(Symbol symbol, Node? location)
    {
        EnsureAssignmentsMarked(symbol);
        var lastAssignmentPos = markedAssignmentSymbolLinks.Get(symbol).lastAssignmentPos;
        return lastAssignmentPos == 0 || location is not null && lastAssignmentPos < location.Pos;
    }

    // flow.go:2644-2659
    internal void EnsureAssignmentsMarked(Symbol symbol)
    {
        if (markedAssignmentSymbolLinks.Get(symbol).lastAssignmentPos != 0)
        {
            return;
        }
        var parent = AstUtilities.FindAncestor(symbol.ValueDeclaration, AstUtilities.IsFunctionOrSourceFile);
        if (parent is null)
        {
            return;
        }
        var links = nodeLinks.Get(parent);
        if ((links.flags & NodeCheckFlags.AssignmentsMarked) == 0)
        {
            links.flags |= NodeCheckFlags.AssignmentsMarked;
            if (!HasParentWithAssignmentsMarked(parent))
            {
                MarkNodeAssignments(parent);
            }
        }
    }

    // flow.go:2661-2665
    internal bool HasParentWithAssignmentsMarked(Node node)
    {
        return AstUtilities.FindAncestor(node.Parent, n =>
            AstUtilities.IsFunctionOrSourceFile(n) && (nodeLinks.Get(n).flags & NodeCheckFlags.AssignmentsMarked) != 0) is not null;
    }

    // For all assignments within the given root node, record the last assignment source position for all
    // referenced parameters and mutable local variables. When assignments occur in nested functions  or
    // references occur in export specifiers, record math.MaxInt32 as the assignment position. When
    // assignments occur in compound statements, record the ending source position of the compound statement
    // as the assignment position (this is more conservative than full control flow analysis, but requires
    // only a single walk over the AST).
    // flow.go:2673-2717
    internal bool MarkNodeAssignmentsWorker(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            {
                var assignmentKind = GetAssignmentTargetKind(node);
                if (assignmentKind != AssignmentKind.None)
                {
                    var symbol = GetResolvedSymbol(node);
                    if (IsParameterOrMutableLocalVariable(symbol))
                    {
                        var links = markedAssignmentSymbolLinks.Get(symbol);
                        var pos = links.lastAssignmentPos;
                        if (pos == 0 || pos != int.MaxValue)
                        {
                            var referencingFunction = AstUtilities.FindAncestor(node, AstUtilities.IsFunctionOrSourceFile);
                            var declaringFunction = AstUtilities.FindAncestor(symbol.ValueDeclaration, AstUtilities.IsFunctionOrSourceFile);
                            if (referencingFunction == declaringFunction)
                            {
                                links.lastAssignmentPos = ExtendAssignmentPosition(node, symbol.ValueDeclaration!);
                            }
                            else
                            {
                                links.lastAssignmentPos = int.MaxValue;
                            }
                        }
                        if (assignmentKind == AssignmentKind.Definite)
                        {
                            links.hasDefiniteAssignment = true;
                        }
                    }
                }
                return false;
            }
            case SyntaxKind.ExportSpecifier:
            {
                var exportDeclaration = node.AsExportSpecifier().Parent!.Parent!.AsExportDeclaration();
                var name = node.PropertyNameOrName();
                if (!node.IsTypeOnly() && !exportDeclaration.IsTypeOnly && exportDeclaration.ModuleSpecifier is null && !NodeTests.IsStringLiteral(name!))
                {
                    var symbol = ResolveEntityName(name!, SymbolFlags.Value, true /*ignoreErrors*/, true /*dontResolveAlias*/, null);
                    if (symbol is not null && IsParameterOrMutableLocalVariable(symbol))
                    {
                        var links = markedAssignmentSymbolLinks.Get(symbol);
                        links.lastAssignmentPos = int.MaxValue;
                    }
                }
                return false;
            }
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.EnumDeclaration:
                return false;
        }
        if (AstUtilities.IsTypeNode(node))
        {
            return false;
        }
        return node.ForEachChild(MarkNodeAssignments);
    }

    // Extend the position of the given assignment target node to the end of any intervening variable statement,
    // expression statement, compound statement, or class declaration occurring between the node and the given
    // declaration node.
    // flow.go:2722-2734
    internal int ExtendAssignmentPosition(Node node, Node declaration)
    {
        var pos = node.Pos;
        Node? cur = node;
        while (cur is not null && cur.Pos > declaration.Pos)
        {
            switch (cur.Kind)
            {
                case SyntaxKind.VariableStatement:
                case SyntaxKind.ExpressionStatement:
                case SyntaxKind.IfStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.ForStatement:
                case SyntaxKind.ForInStatement:
                case SyntaxKind.ForOfStatement:
                case SyntaxKind.WithStatement:
                case SyntaxKind.SwitchStatement:
                case SyntaxKind.TryStatement:
                case SyntaxKind.ClassDeclaration:
                    pos = cur.End;
                    break;
            }
            cur = cur.Parent;
        }
        return pos;
    }

    // markNodeAssignments is the Go method value `c.markNodeAssignments` passed to ForEachChild.
    internal bool MarkNodeAssignments(Node node) => MarkNodeAssignmentsWorker(node);

    // core.AppendIfUnique on a List<Type> (reference identity). SlicesContains/SlicesEqual/
    // SlicesIndexFunc reuse the existing Type[] helpers in Checker.TypeOps.Support.cs /
    // Checker.MembersSignatures.Support.cs.
    private static void AppendIfUniqueType(List<Type> list, Type value)
    {
        if (!list.Contains(value))
        {
            list.Add(value);
        }
    }
}
