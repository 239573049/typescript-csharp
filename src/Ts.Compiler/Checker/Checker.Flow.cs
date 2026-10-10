// Ported from tsgo internal/checker/flow.go (control-flow narrowing engine), lines ~1-1531.
// plan/03 §4.2 #(flow). Faithful per-function translation: same function order, same branch
// structure, Go comments and `// !!!` markers preserved.
//
// This file carries: FlowType/FlowState/SharedFlow types, newFlowType/getFlowState/putFlowState,
// getFlowNodeOfNode, getFlowTypeOfReference(Ex), getTypeAtFlowNode + its per-flag dispatch
// (assignment/call/condition/switch/branch/loop/array-mutation), and the whole narrowType* family
// (truthiness/typeof/equality/binary/instanceof/discriminant/in-keyword/typeFacts/getNarrowedType).
// The tail (switch witnesses, isMatchingReference, flow cache keys, initial/assigned types,
// reachability, assignment marking) is in Checker.Flow.Support.cs. Single dedup stubs for
// non-flow clusters are in Checker.Flow.Deps.cs.
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified; the
// no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used directly, as in relater/inference.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

// flow.go:19-22
internal struct FlowType
{
    internal Type? t;
    internal bool incomplete;

    // flow.go:24-26
    internal readonly bool IsNil() => t is null;
}

// flow.go:35-38
internal struct SharedFlow
{
    internal FlowNode flow;
    internal FlowType flowType;
}

// flow.go:40-50
internal sealed class FlowState
{
    internal Node reference = null!;
    internal Type declaredType = null!;
    internal Type initialType = null!;
    internal Node? flowContainer;
    internal CacheHashKey refKey;
    internal int depth;
    internal int sharedFlowStart;
    internal List<FlowReduceLabelData> reduceLabels = [];
    internal FlowState? next;
}

public partial class Checker
{
    // flow.go:28-33
    internal FlowType NewFlowType(Type t, bool incomplete)
    {
        if (incomplete && (t.flags & TypeFlags.Never) != 0)
        {
            t = silentNeverType!;
        }
        return new FlowType { t = t, incomplete = incomplete };
    }

    // flow.go:52-59
    internal FlowState GetFlowState()
    {
        var f = freeFlowState;
        if (f is null)
        {
            f = new FlowState();
        }
        freeFlowState = f.next;
        return f;
    }

    // flow.go:61-67
    internal void PutFlowState(FlowState f)
    {
        f.reduceLabels.Clear();
        var reduceLabels = f.reduceLabels;
        // *f = FlowState{ reduceLabels: f.reduceLabels[:0], next: c.freeFlowState }
        f.reference = null!;
        f.declaredType = null!;
        f.initialType = null!;
        f.flowContainer = null;
        f.refKey = default;
        f.depth = 0;
        f.sharedFlowStart = 0;
        f.reduceLabels = reduceLabels;
        f.next = freeFlowState;
        freeFlowState = f;
    }

    // flow.go:69-75
    internal static FlowNode? GetFlowNodeOfNode(Node node)
    {
        var flowNodeData = node.FlowNodeData();
        if (flowNodeData is not null)
        {
            return flowNodeData.FlowNode;
        }
        return null;
    }

    // flow.go:77-79
    internal Type GetFlowTypeOfReference(Node reference, Type declaredType)
    {
        return GetFlowTypeOfReferenceEx(reference, declaredType, declaredType, null, null);
    }

    // flow.go:81-115
    internal Type GetFlowTypeOfReferenceEx(Node reference, Type declaredType, Type initialType, Node? flowContainer, FlowNode? flowNode)
    {
        if (flowAnalysisDisabled)
        {
            return errorType!;
        }
        if (flowNode is null)
        {
            flowNode = GetFlowNodeOfNode(reference);
            if (flowNode is null)
            {
                return declaredType;
            }
        }
        var f = GetFlowState();
        f.reference = reference;
        f.declaredType = declaredType;
        f.initialType = CoreUtil.Coalesce(initialType, declaredType)!;
        f.flowContainer = flowContainer;
        f.sharedFlowStart = sharedFlows.Count;
        flowInvocationCount++;
        var evolvedType = GetTypeAtFlowNode(f, flowNode).t!;
        // c.sharedFlows = c.sharedFlows[:f.sharedFlowStart]
        sharedFlows.RemoveRange(f.sharedFlowStart, sharedFlows.Count - f.sharedFlowStart);
        PutFlowState(f);
        // When the reference is 'x' in an 'x.length', 'x.push(value)', 'x.unshift(value)' or x[n] = value' operation,
        // we give type 'any[]' to 'x' instead of using the type determined by control flow analysis such that operations
        // on empty arrays are possible without implicit any errors and new element types can be inferred without
        // type mismatch errors.
        Type resultType;
        if ((evolvedType.objectFlags & ObjectFlags.EvolvingArray) != 0 && IsEvolvingArrayOperationTarget(reference))
        {
            resultType = autoArrayType!;
        }
        else
        {
            resultType = FinalizeEvolvingArrayType(evolvedType);
        }
        if (resultType == unreachableNeverType || reference.Parent is not null && NodeTests.IsNonNullExpression(reference.Parent) && (resultType.flags & TypeFlags.Never) == 0 && (GetTypeWithFacts(resultType, TypeFacts.NEUndefinedOrNull).flags & TypeFlags.Never) != 0)
        {
            return declaredType;
        }
        return resultType;
    }

    // flow.go:117-206
    internal FlowType GetTypeAtFlowNode(FlowState f, FlowNode flow)
    {
        if (f.depth == 2000)
        {
            // We have made 2000 recursive invocations. To avoid overflowing the call stack we report an error
            // and disable further control flow analysis in the containing function or module body.
            if (tracer is { } tr)
            {
                tr.Instant(global::Ts.Tracing.Phase.CheckTypes, "getTypeAtFlowNode_DepthLimit", new Dictionary<string, object?> { ["depth"] = f.depth });
            }
            flowAnalysisDisabled = true;
            ReportFlowControlError(f.reference);
            return new FlowType { t = errorType };
        }
        f.depth++;
        FlowNode? sharedFlow = null;
        while (true)
        {
            var flags = flow.Flags;
            if ((flags & FlowFlags.Shared) != 0)
            {
                // We cache results of flow type resolution for shared nodes that were previously visited in
                // the same getFlowTypeOfReference invocation. A node is considered shared when it is the
                // antecedent of more than one node.
                for (int i = f.sharedFlowStart; i < sharedFlows.Count; i++)
                {
                    if (sharedFlows[i].flow == flow)
                    {
                        f.depth--;
                        return sharedFlows[i].flowType;
                    }
                }
                sharedFlow = flow;
            }
            FlowType t;
            if ((flags & FlowFlags.Assignment) != 0)
            {
                t = GetTypeAtFlowAssignment(f, flow);
                if (t.IsNil())
                {
                    flow = flow.Antecedent!;
                    continue;
                }
            }
            else if ((flags & FlowFlags.Call) != 0)
            {
                t = GetTypeAtFlowCall(f, flow);
                if (t.IsNil())
                {
                    flow = flow.Antecedent!;
                    continue;
                }
            }
            else if ((flags & FlowFlags.Condition) != 0)
            {
                t = GetTypeAtFlowCondition(f, flow);
            }
            else if ((flags & FlowFlags.SwitchClause) != 0)
            {
                t = GetTypeAtSwitchClause(f, flow);
            }
            else if ((flags & FlowFlags.BranchLabel) != 0)
            {
                var antecedents = GetBranchLabelAntecedents(flow, f.reduceLabels);
                if (antecedents!.Next is null)
                {
                    flow = antecedents.Flow!;
                    continue;
                }
                t = GetTypeAtFlowBranchLabel(f, flow, antecedents);
            }
            else if ((flags & FlowFlags.LoopLabel) != 0)
            {
                if (flow.Antecedents!.Next is null)
                {
                    flow = flow.Antecedents.Flow!;
                    continue;
                }
                t = GetTypeAtFlowLoopLabel(f, flow);
            }
            else if ((flags & FlowFlags.ArrayMutation) != 0)
            {
                t = GetTypeAtFlowArrayMutation(f, flow);
                if (t.IsNil())
                {
                    flow = flow.Antecedent!;
                    continue;
                }
            }
            else if ((flags & FlowFlags.ReduceLabel) != 0)
            {
                f.reduceLabels.Add(flow.Node!.AsFlowReduceLabelData());
                t = GetTypeAtFlowNode(f, flow.Antecedent!);
                f.reduceLabels.RemoveAt(f.reduceLabels.Count - 1);
            }
            else if ((flags & FlowFlags.Start) != 0)
            {
                // Check if we should continue with the control flow of the containing function.
                var container = flow.Node;
                if (container is not null && container != f.flowContainer && !NodeTests.IsPropertyAccessExpression(f.reference) && !NodeTests.IsElementAccessExpression(f.reference) && !(f.reference.Kind == SyntaxKind.ThisKeyword && !NodeTests.IsArrowFunction(container)))
                {
                    flow = container.FlowNodeData()!.FlowNode!;
                    continue;
                }
                // At the top of the flow we have the initial type.
                t = new FlowType { t = f.initialType };
            }
            else
            {
                // Unreachable code errors are reported in the binding phase. Here we
                // simply return the non-auto declared type to reduce follow-on errors.
                t = new FlowType { t = ConvertAutoToAny(f.declaredType) };
            }
            if (sharedFlow is not null)
            {
                // Record visited node and the associated type in the cache.
                sharedFlows.Add(new SharedFlow { flow = sharedFlow, flowType = t });
            }
            f.depth--;
            return t;
        }
    }

    // flow.go:208-218
    internal static FlowList? GetBranchLabelAntecedents(FlowNode flow, List<FlowReduceLabelData> reduceLabels)
    {
        int i = reduceLabels.Count;
        while (i != 0)
        {
            i--;
            var data = reduceLabels[i];
            if (data.Target == flow)
            {
                return data.Antecedents;
            }
        }
        return flow.Antecedents;
    }

    // flow.go:220-274
    internal FlowType GetTypeAtFlowAssignment(FlowState f, FlowNode flow)
    {
        var node = flow.Node!;
        // Assignments only narrow the computed type if the declared type is a union type. Thus, we
        // only need to evaluate the assigned type if the declared type is a union type.
        if (IsMatchingReference(f.reference, node))
        {
            if (!IsReachableFlowNode(flow))
            {
                return new FlowType { t = unreachableNeverType };
            }
            if (GetAssignmentTargetKind(node) == AssignmentKind.Compound)
            {
                var flowType = GetTypeAtFlowNode(f, flow.Antecedent!);
                return NewFlowType(GetBaseTypeOfLiteralType(flowType.t!), flowType.incomplete);
            }
            if (f.declaredType == autoType || f.declaredType == autoArrayType)
            {
                if (IsEmptyArrayAssignment(node))
                {
                    return new FlowType { t = GetEvolvingArrayType(neverType!) };
                }
                var assignedType = GetWidenedLiteralType(GetInitialOrAssignedType(f, flow));
                if (IsTypeAssignableTo(assignedType, f.declaredType))
                {
                    return new FlowType { t = assignedType };
                }
                return new FlowType { t = anyArrayType };
            }
            var t = f.declaredType;
            if (IsInCompoundLikeAssignment(node))
            {
                t = GetBaseTypeOfLiteralType(t);
            }
            if ((t.flags & TypeFlags.Union) != 0)
            {
                return new FlowType { t = GetAssignmentReducedType(t, GetInitialOrAssignedType(f, flow)) };
            }
            return new FlowType { t = t };
        }
        // We didn't have a direct match. However, if the reference is a dotted name, this
        // may be an assignment to a left hand part of the reference. For example, for a
        // reference 'x.y.z', we may be at an assignment to 'x.y' or 'x'. In that case,
        // return the declared type.
        if (ContainsMatchingReference(f.reference, node))
        {
            if (!IsReachableFlowNode(flow))
            {
                return new FlowType { t = unreachableNeverType };
            }
            // A matching dotted name might also be an expando property on a function *expression*,
            // in which case we continue control flow analysis back to the function's declaration
            if (NodeTests.IsVariableDeclaration(node) && (AstUtilities.IsInJSFile(node) || AstUtilities.IsVarConstLike(node)))
            {
                var init = node.Initializer();
                if (init is not null && AstUtilities.IsFunctionExpressionOrArrowFunction(init))
                {
                    return GetTypeAtFlowNode(f, flow.Antecedent!);
                }
            }
            return new FlowType { t = f.declaredType };
        }
        // for (const _ in ref) acts as a nonnull on ref
        if (NodeTests.IsVariableDeclaration(node) && NodeTests.IsForInStatement(node.Parent!.Parent!) && (IsMatchingReference(f.reference, node.Parent!.Parent!.Expression()!) || OptionalChainContainsReference(node.Parent!.Parent!.Expression()!, f.reference)))
        {
            return new FlowType { t = GetNonNullableTypeIfNeeded(FinalizeEvolvingArrayType(GetTypeAtFlowNode(f, flow.Antecedent!).t!)) };
        }
        // Assignment doesn't affect reference
        return default;
    }

    // flow.go:276-281
    internal Type GetInitialOrAssignedType(FlowState f, FlowNode flow)
    {
        if (NodeTests.IsVariableDeclaration(flow.Node!) || NodeTests.IsBindingElement(flow.Node!))
        {
            return GetNarrowableTypeForReference(GetInitialType(flow.Node!), f.reference, CheckMode.Normal);
        }
        return GetNarrowableTypeForReference(GetAssignedType(flow.Node!), f.reference, CheckMode.Normal);
    }

    // flow.go:283-286
    internal bool IsEmptyArrayAssignment(Node node)
    {
        return NodeTests.IsVariableDeclaration(node) && node.Initializer() is not null && AstUtilities.IsEmptyArrayLiteral(node.Initializer()!) ||
            !NodeTests.IsBindingElement(node) && NodeTests.IsBinaryExpression(node.Parent!) && AstUtilities.IsEmptyArrayLiteral(node.Parent!.AsBinaryExpression().Right!);
    }

    // flow.go:288-314
    internal FlowType GetTypeAtFlowCall(FlowState f, FlowNode flow)
    {
        var signature = GetEffectsSignature(flow.Node!);
        if (signature is not null)
        {
            var predicate = GetTypePredicateOfSignature(signature);
            if (predicate is not null && (predicate.kind == TypePredicateKind.AssertsThis || predicate.kind == TypePredicateKind.AssertsIdentifier))
            {
                var flowType = GetTypeAtFlowNode(f, flow.Antecedent!);
                var t = FinalizeEvolvingArrayType(flowType.t!);
                Type narrowedType;
                if (predicate.t is not null)
                {
                    narrowedType = NarrowTypeByTypePredicate(f, t, predicate, flow.Node!, true /*assumeTrue*/);
                }
                else if (predicate.kind == TypePredicateKind.AssertsIdentifier && predicate.parameterIndex >= 0 && predicate.parameterIndex < (flow.Node!.Arguments()?.Length ?? 0))
                {
                    narrowedType = NarrowTypeByAssertion(f, t, flow.Node!.Arguments()![predicate.parameterIndex]);
                }
                else
                {
                    narrowedType = t;
                }
                if (narrowedType == t)
                {
                    return flowType;
                }
                return NewFlowType(narrowedType, flowType.incomplete);
            }
            if ((GetReturnTypeOfSignature(signature).flags & TypeFlags.Never) != 0)
            {
                return new FlowType { t = unreachableNeverType };
            }
        }
        return default;
    }

    // flow.go:316-336
    internal Type NarrowTypeByTypePredicate(FlowState f, Type t, TypePredicate predicate, Node callExpression, bool assumeTrue)
    {
        // Don't narrow from 'any' if the predicate type is exactly 'Object' or 'Function'
        if (predicate.t is not null && !(IsTypeAny(t) && (predicate.t == globalObjectType || predicate.t == globalFunctionType)))
        {
            var predicateArgument = GetTypePredicateArgument(predicate, callExpression);
            if (predicateArgument is not null)
            {
                if (IsMatchingReference(f.reference, predicateArgument))
                {
                    return GetNarrowedType(t, predicate.t, assumeTrue, false /*checkDerived*/);
                }
                if (strictNullChecks && OptionalChainContainsReference(predicateArgument, f.reference) && (assumeTrue && !HasTypeFacts(predicate.t, TypeFacts.EQUndefined) || !assumeTrue && EveryType(predicate.t, IsNullableType)))
                {
                    t = GetAdjustedTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
                }
                var access = GetDiscriminantPropertyAccess(f, predicateArgument, t);
                if (access is not null)
                {
                    return NarrowTypeByDiscriminant(t, access, tt =>
                        GetNarrowedType(tt, predicate.t, assumeTrue, false /*checkDerived*/));
                }
            }
        }
        return t;
    }

    // flow.go:338-352
    internal Type NarrowTypeByAssertion(FlowState f, Type t, Node expr)
    {
        var node = AstUtilities.SkipParentheses(expr);
        if (node.Kind == SyntaxKind.FalseKeyword)
        {
            return unreachableNeverType!;
        }
        if (node.Kind == SyntaxKind.BinaryExpression)
        {
            if (node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.AmpersandAmpersandToken)
            {
                return NarrowTypeByAssertion(f, NarrowTypeByAssertion(f, t, node.AsBinaryExpression().Left!), node.AsBinaryExpression().Right!);
            }
            if (node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.BarBarToken)
            {
                return GetUnionType([NarrowTypeByAssertion(f, t, node.AsBinaryExpression().Left!), NarrowTypeByAssertion(f, t, node.AsBinaryExpression().Right!)]);
            }
        }
        return NarrowType(f, t, node, true /*assumeTrue*/);
    }

    // flow.go:354-373
    internal FlowType GetTypeAtFlowCondition(FlowState f, FlowNode flow)
    {
        var flowType = GetTypeAtFlowNode(f, flow.Antecedent!);
        if ((flowType.t!.flags & TypeFlags.Never) != 0)
        {
            return flowType;
        }
        // If we have an antecedent type (meaning we're reachable in some way), we first
        // attempt to narrow the antecedent type. If that produces the never type, and if
        // the antecedent type is incomplete (i.e. a transient type in a loop), then we
        // take the type guard as an indication that control *could* reach here once we
        // have the complete type. We proceed by switching to the silent never type which
        // doesn't report errors when operators are applied to it. Note that this is the
        // *only* place a silent never type is ever generated.
        var assumeTrue = (flow.Flags & FlowFlags.TrueCondition) != 0;
        var nonEvolvingType = FinalizeEvolvingArrayType(flowType.t);
        var narrowedType = NarrowType(f, nonEvolvingType, flow.Node!, assumeTrue);
        if (narrowedType == nonEvolvingType)
        {
            return flowType;
        }
        return NewFlowType(narrowedType, flowType.incomplete);
    }

    // Narrow the given type based on the given expression having the assumed boolean value. The returned type
    // will be a subtype or the same type as the argument.
    // flow.go:377-413
    internal Type NarrowType(FlowState f, Type t, Node expr, bool assumeTrue)
    {
        // for `a?.b`, we emulate a synthetic `a !== null && a !== undefined` condition for `a`
        if (AstUtilities.IsExpressionOfOptionalChainRoot(expr) || NodeTests.IsBinaryExpression(expr.Parent!) && (expr.Parent!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.QuestionQuestionToken || expr.Parent!.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.QuestionQuestionEqualsToken) && expr.Parent!.AsBinaryExpression().Left == expr)
        {
            return NarrowTypeByOptionality(f, t, expr, assumeTrue);
        }
        switch (expr.Kind)
        {
            case SyntaxKind.Identifier:
                // When narrowing a reference to a const variable, non-assigned parameter, or readonly property, we inline
                // up to five levels of aliased conditional expressions that are themselves declared as const variables.
                if (!IsMatchingReference(f.reference, expr) && inlineLevel < 5)
                {
                    var symbol = GetResolvedSymbol(expr);
                    if (IsConstantVariable(symbol))
                    {
                        var declaration = symbol.ValueDeclaration;
                        if (declaration is not null && NodeTests.IsVariableDeclaration(declaration) && declaration.Type() is null && declaration.Initializer() is not null && IsConstantReference(f.reference))
                        {
                            inlineLevel++;
                            var result = NarrowType(f, t, declaration.Initializer()!, assumeTrue);
                            inlineLevel--;
                            return result;
                        }
                    }
                }
                goto case SyntaxKind.ThisKeyword;
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                return NarrowTypeByTruthiness(f, t, expr, assumeTrue);
            case SyntaxKind.CallExpression:
                return NarrowTypeByCallExpression(f, t, expr, assumeTrue);
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.SatisfiesExpression:
                return NarrowType(f, t, expr.Expression()!, assumeTrue);
            case SyntaxKind.BinaryExpression:
                return NarrowTypeByBinaryExpression(f, t, expr.AsBinaryExpression(), assumeTrue);
            case SyntaxKind.PrefixUnaryExpression:
                if (expr.AsPrefixUnaryExpression().Operator == SyntaxKind.ExclamationToken)
                {
                    return NarrowType(f, t, expr.AsPrefixUnaryExpression().Operand!, !assumeTrue);
                }
                break;
        }
        return t;
    }

    // flow.go:415-426
    internal Type NarrowTypeByOptionality(FlowState f, Type t, Node expr, bool assumePresent)
    {
        if (IsMatchingReference(f.reference, expr))
        {
            return GetAdjustedTypeWithFacts(t, CoreUtil.IfElse(assumePresent, TypeFacts.NEUndefinedOrNull, TypeFacts.EQUndefinedOrNull));
        }
        var access = GetDiscriminantPropertyAccess(f, expr, t);
        if (access is not null)
        {
            return NarrowTypeByDiscriminant(t, access, tt =>
                GetTypeWithFacts(tt, CoreUtil.IfElse(assumePresent, TypeFacts.NEUndefinedOrNull, TypeFacts.EQUndefinedOrNull)));
        }
        return t;
    }

    // flow.go:428-442
    internal Type NarrowTypeByTruthiness(FlowState f, Type t, Node expr, bool assumeTrue)
    {
        if (IsMatchingReference(f.reference, expr))
        {
            return GetAdjustedTypeWithFacts(t, CoreUtil.IfElse(assumeTrue, TypeFacts.Truthy, TypeFacts.Falsy));
        }
        if (strictNullChecks && assumeTrue && OptionalChainContainsReference(expr, f.reference))
        {
            t = GetAdjustedTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
        }
        var access = GetDiscriminantPropertyAccess(f, expr, t);
        if (access is not null)
        {
            return NarrowTypeByDiscriminant(t, access, tt =>
                GetTypeWithFacts(tt, CoreUtil.IfElse(assumeTrue, TypeFacts.Truthy, TypeFacts.Falsy)));
        }
        return t;
    }

    // flow.go:444-467
    internal Type NarrowTypeByCallExpression(FlowState f, Type t, Node callExpression, bool assumeTrue)
    {
        if (HasMatchingArgument(callExpression, f.reference))
        {
            TypePredicate? predicate = null;
            if (assumeTrue || !IsCallChain(callExpression))
            {
                var signature = GetEffectsSignature(callExpression);
                if (signature is not null)
                {
                    predicate = GetTypePredicateOfSignature(signature);
                }
            }
            if (predicate is not null && (predicate.kind == TypePredicateKind.This || predicate.kind == TypePredicateKind.Identifier))
            {
                return NarrowTypeByTypePredicate(f, t, predicate, callExpression, assumeTrue);
            }
        }
        if (ContainsMissingType(t) && AstUtilities.IsAccessExpression(f.reference) && NodeTests.IsPropertyAccessExpression(callExpression.Expression()!))
        {
            var callAccess = callExpression.Expression()!;
            if (IsMatchingReference(f.reference.Expression()!, GetReferenceCandidate(callAccess.Expression()!)) && NodeTests.IsIdentifier(callAccess.Name()!) && callAccess.Name()!.Text() == "hasOwnProperty" && (callExpression.Arguments()?.Length ?? 0) == 1)
            {
                var argument = callExpression.Arguments()![0];
                if (GetAccessedPropertyName(f.reference, out var accessedName) && AstUtilities.IsStringLiteralLike(argument) && accessedName == argument.Text())
                {
                    return GetTypeWithFacts(t, CoreUtil.IfElse(assumeTrue, TypeFacts.NEUndefined, TypeFacts.EQUndefined));
                }
            }
        }
        return t;
    }

    // flow.go:469-554
    internal Type NarrowTypeByBinaryExpression(FlowState f, Type t, BinaryExpression expr, bool assumeTrue)
    {
        switch (expr.OperatorToken!.Kind)
        {
            case SyntaxKind.EqualsToken:
            case SyntaxKind.BarBarEqualsToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
            case SyntaxKind.QuestionQuestionEqualsToken:
                return NarrowTypeByTruthiness(f, NarrowType(f, t, expr.Right!, assumeTrue), expr.Left!, assumeTrue);
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
            {
                var operatorKind = expr.OperatorToken.Kind;
                var left = GetReferenceCandidate(expr.Left!);
                var right = GetReferenceCandidate(expr.Right!);
                if (left.Kind == SyntaxKind.TypeOfExpression && AstUtilities.IsStringLiteralLike(right))
                {
                    return NarrowTypeByTypeof(f, t, left.AsTypeOfExpression(), operatorKind, right, assumeTrue);
                }
                if (right.Kind == SyntaxKind.TypeOfExpression && AstUtilities.IsStringLiteralLike(left))
                {
                    return NarrowTypeByTypeof(f, t, right.AsTypeOfExpression(), operatorKind, left, assumeTrue);
                }
                if (IsMatchingReference(f.reference, left))
                {
                    return NarrowTypeByEquality(t, operatorKind, right, assumeTrue);
                }
                if (IsMatchingReference(f.reference, right))
                {
                    return NarrowTypeByEquality(t, operatorKind, left, assumeTrue);
                }
                if (strictNullChecks)
                {
                    if (OptionalChainContainsReference(left, f.reference))
                    {
                        t = NarrowTypeByOptionalChainContainment(f, t, operatorKind, right, assumeTrue);
                    }
                    else if (OptionalChainContainsReference(right, f.reference))
                    {
                        t = NarrowTypeByOptionalChainContainment(f, t, operatorKind, left, assumeTrue);
                    }
                }
                var leftAccess = GetDiscriminantPropertyAccess(f, left, t);
                if (leftAccess is not null)
                {
                    return NarrowTypeByDiscriminantProperty(t, leftAccess, operatorKind, right, assumeTrue);
                }
                var rightAccess = GetDiscriminantPropertyAccess(f, right, t);
                if (rightAccess is not null)
                {
                    return NarrowTypeByDiscriminantProperty(t, rightAccess, operatorKind, left, assumeTrue);
                }
                if (IsMatchingConstructorReference(f, left))
                {
                    return NarrowTypeByConstructor(t, operatorKind, right, assumeTrue);
                }
                if (IsMatchingConstructorReference(f, right))
                {
                    return NarrowTypeByConstructor(t, operatorKind, left, assumeTrue);
                }
                if (AstUtilities.IsBooleanLiteral(right) && !AstUtilities.IsAccessExpression(left))
                {
                    return NarrowTypeByBooleanComparison(f, t, left, right, operatorKind, assumeTrue);
                }
                if (AstUtilities.IsBooleanLiteral(left) && !AstUtilities.IsAccessExpression(right))
                {
                    return NarrowTypeByBooleanComparison(f, t, right, left, operatorKind, assumeTrue);
                }
                break;
            }
            case SyntaxKind.InstanceOfKeyword:
                return NarrowTypeByInstanceof(f, t, expr, assumeTrue);
            case SyntaxKind.InKeyword:
            {
                if (NodeTests.IsPrivateIdentifier(expr.Left!))
                {
                    return NarrowTypeByPrivateIdentifierInInExpression(f, t, expr, assumeTrue);
                }
                var target = GetReferenceCandidate(expr.Right!);
                if (ContainsMissingType(t) && AstUtilities.IsAccessExpression(f.reference) && IsMatchingReference(f.reference.Expression()!, target))
                {
                    var leftType = GetTypeOfExpression(expr.Left!);
                    if (IsTypeUsableAsPropertyName(leftType))
                    {
                        if (GetAccessedPropertyName(f.reference, out var accessedName) && accessedName == GetPropertyNameFromType(leftType))
                        {
                            return GetTypeWithFacts(t, CoreUtil.IfElse(assumeTrue, TypeFacts.NEUndefined, TypeFacts.EQUndefined));
                        }
                    }
                }
                if (IsMatchingReference(f.reference, target))
                {
                    var leftType = GetTypeOfExpression(expr.Left!);
                    if (IsTypeUsableAsPropertyName(leftType))
                    {
                        return NarrowTypeByInKeyword(f, t, leftType, assumeTrue);
                    }
                }
                break;
            }
            case SyntaxKind.CommaToken:
                return NarrowType(f, t, expr.Right!, assumeTrue);
            case SyntaxKind.AmpersandAmpersandToken:
                // Ordinarily we won't see && and || expressions in control flow analysis because the Binder breaks those
                // expressions down to individual conditional control flows. However, we may encounter them when analyzing
                // aliased conditional expressions.
                if (assumeTrue)
                {
                    return NarrowType(f, NarrowType(f, t, expr.Left!, true /*assumeTrue*/), expr.Right!, true /*assumeTrue*/);
                }
                return GetUnionType([NarrowType(f, t, expr.Left!, false /*assumeTrue*/), NarrowType(f, t, expr.Right!, false /*assumeTrue*/)]);
            case SyntaxKind.BarBarToken:
                if (assumeTrue)
                {
                    return GetUnionType([NarrowType(f, t, expr.Left!, true /*assumeTrue*/), NarrowType(f, t, expr.Right!, true /*assumeTrue*/)]);
                }
                return NarrowType(f, NarrowType(f, t, expr.Left!, false /*assumeTrue*/), expr.Right!, false /*assumeTrue*/);
        }
        return t;
    }

    // flow.go:556-600
    internal Type NarrowTypeByEquality(Type t, SyntaxKind operatorKind, Node value, bool assumeTrue)
    {
        if ((t.flags & TypeFlags.Any) != 0)
        {
            return t;
        }
        if (operatorKind == SyntaxKind.ExclamationEqualsToken || operatorKind == SyntaxKind.ExclamationEqualsEqualsToken)
        {
            assumeTrue = !assumeTrue;
        }
        var valueType = GetTypeOfExpression(value);
        var doubleEquals = operatorKind == SyntaxKind.EqualsEqualsToken || operatorKind == SyntaxKind.ExclamationEqualsToken;
        if ((valueType.flags & TypeFlags.Nullable) != 0)
        {
            if (!strictNullChecks)
            {
                return t;
            }
            TypeFacts facts;
            if (doubleEquals)
            {
                facts = CoreUtil.IfElse(assumeTrue, TypeFacts.EQUndefinedOrNull, TypeFacts.NEUndefinedOrNull);
            }
            else if ((valueType.flags & TypeFlags.Null) != 0)
            {
                facts = CoreUtil.IfElse(assumeTrue, TypeFacts.EQNull, TypeFacts.NENull);
            }
            else
            {
                facts = CoreUtil.IfElse(assumeTrue, TypeFacts.EQUndefined, TypeFacts.NEUndefined);
            }
            return GetAdjustedTypeWithFacts(t, facts);
        }
        if (assumeTrue)
        {
            if (!doubleEquals && ((t.flags & TypeFlags.Unknown) != 0 || SomeType(t, IsEmptyAnonymousObjectType)))
            {
                if ((valueType.flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0 || IsEmptyAnonymousObjectType(valueType))
                {
                    return valueType;
                }
                if ((valueType.flags & TypeFlags.Object) != 0)
                {
                    return nonPrimitiveType!;
                }
            }
            var filteredType = FilterType(t, tt =>
                AreTypesComparable(tt, valueType) || doubleEquals && IsCoercibleUnderDoubleEquals(tt, valueType));
            return ReplacePrimitivesWithLiterals(filteredType, valueType);
        }
        if (IsUnitType(valueType))
        {
            return FilterType(t, tt =>
                !(IsUnitLikeType(tt) && AreTypesComparable(tt, valueType)));
        }
        return t;
    }

    // flow.go:602-621
    internal Type NarrowTypeByTypeof(FlowState f, Type t, TypeOfExpression typeOfExpr, SyntaxKind operatorKind, Node literal, bool assumeTrue)
    {
        // We have '==', '!=', '===', or !==' operator with 'typeof xxx' and string literal operands
        if (operatorKind == SyntaxKind.ExclamationEqualsToken || operatorKind == SyntaxKind.ExclamationEqualsEqualsToken)
        {
            assumeTrue = !assumeTrue;
        }
        var target = GetReferenceCandidate(typeOfExpr.Expression!);
        if (!IsMatchingReference(f.reference, target))
        {
            if (strictNullChecks && OptionalChainContainsReference(target, f.reference) && assumeTrue == (literal.Text() != "undefined"))
            {
                t = GetAdjustedTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
            }
            var propertyAccess = GetDiscriminantPropertyAccess(f, target, t);
            if (propertyAccess is not null)
            {
                return NarrowTypeByDiscriminant(t, propertyAccess, tt =>
                    NarrowTypeByLiteralExpression(tt, literal, assumeTrue));
            }
            return t;
        }
        return NarrowTypeByLiteralExpression(t, literal, assumeTrue);
    }

    // flow.go:623-632
    internal static readonly Dictionary<string, TypeFacts> TypeofNEFacts = new(StringComparer.Ordinal)
    {
        ["string"] = TypeFacts.TypeofNEString,
        ["number"] = TypeFacts.TypeofNENumber,
        ["bigint"] = TypeFacts.TypeofNEBigInt,
        ["boolean"] = TypeFacts.TypeofNEBoolean,
        ["symbol"] = TypeFacts.TypeofNESymbol,
        ["undefined"] = TypeFacts.NEUndefined,
        ["object"] = TypeFacts.TypeofNEObject,
        ["function"] = TypeFacts.TypeofNEFunction,
    };

    // flow.go:634-643
    internal Type NarrowTypeByLiteralExpression(Type t, LiteralExpression literal, bool assumeTrue)
    {
        if (assumeTrue)
        {
            return NarrowTypeByTypeName(t, literal.Text());
        }
        if (!TypeofNEFacts.TryGetValue(literal.Text(), out var facts))
        {
            facts = TypeFacts.TypeofNEHostObject;
        }
        return GetAdjustedTypeWithFacts(t, facts);
    }

    // flow.go:645-671
    internal Type NarrowTypeByTypeName(Type t, string typeName)
    {
        switch (typeName)
        {
            case "string":
                return NarrowTypeByTypeFacts(t, stringType!, TypeFacts.TypeofEQString);
            case "number":
                return NarrowTypeByTypeFacts(t, numberType!, TypeFacts.TypeofEQNumber);
            case "bigint":
                return NarrowTypeByTypeFacts(t, bigintType!, TypeFacts.TypeofEQBigInt);
            case "boolean":
                return NarrowTypeByTypeFacts(t, booleanType!, TypeFacts.TypeofEQBoolean);
            case "symbol":
                return NarrowTypeByTypeFacts(t, esSymbolType!, TypeFacts.TypeofEQSymbol);
            case "object":
                if ((t.flags & TypeFlags.Any) != 0)
                {
                    return t;
                }
                return GetUnionType([NarrowTypeByTypeFacts(t, nonPrimitiveType!, TypeFacts.TypeofEQObject), NarrowTypeByTypeFacts(t, nullType!, TypeFacts.EQNull)]);
            case "function":
                if ((t.flags & TypeFlags.Any) != 0)
                {
                    return t;
                }
                return NarrowTypeByTypeFacts(t, globalFunctionType!, TypeFacts.TypeofEQFunction);
            case "undefined":
                return NarrowTypeByTypeFacts(t, undefinedType!, TypeFacts.EQUndefined);
        }
        return NarrowTypeByTypeFacts(t, nonPrimitiveType!, TypeFacts.TypeofEQHostObject);
    }

    // flow.go:673-688
    internal Type NarrowTypeByTypeFacts(Type t, Type impliedType, TypeFacts facts)
    {
        return MapType(t, tt =>
        {
            if (IsTypeRelatedTo(tt, impliedType, strictSubtypeRelation!))
            {
                if (HasTypeFacts(tt, facts))
                {
                    return tt;
                }
                return neverType!;
            }
            if (IsTypeSubtypeOf(impliedType, tt))
            {
                return impliedType;
            }
            if (HasTypeFacts(tt, facts))
            {
                return GetIntersectionType([tt, impliedType]);
            }
            return neverType!;
        });
    }

    // flow.go:690-711
    internal Type NarrowTypeByDiscriminantProperty(Type t, Node access, SyntaxKind operatorKind, Node value, bool assumeTrue)
    {
        if ((operatorKind == SyntaxKind.EqualsEqualsEqualsToken || operatorKind == SyntaxKind.ExclamationEqualsEqualsToken) && (t.flags & TypeFlags.Union) != 0)
        {
            var keyPropertyName = GetKeyPropertyName(t);
            if (keyPropertyName != "")
            {
                if (GetAccessedPropertyName(access, out var accessedName) && keyPropertyName == accessedName)
                {
                    var candidate = GetConstituentTypeForKeyType(t, GetTypeOfExpression(value));
                    if (candidate is not null)
                    {
                        if (assumeTrue && operatorKind == SyntaxKind.EqualsEqualsEqualsToken || !assumeTrue && operatorKind == SyntaxKind.ExclamationEqualsEqualsToken)
                        {
                            return candidate;
                        }
                        var propType = GetTypeOfPropertyOfType(candidate, keyPropertyName);
                        if (propType is not null && IsUnitType(propType))
                        {
                            return RemoveType(t, candidate);
                        }
                        return t;
                    }
                }
            }
        }
        return NarrowTypeByDiscriminant(t, access, tt =>
            NarrowTypeByEquality(tt, operatorKind, value, assumeTrue));
    }

    // flow.go:713-736
    internal Type NarrowTypeByDiscriminant(Type t, Node access, Func<Type, Type> narrowType)
    {
        if (!GetAccessedPropertyName(access, out var propName))
        {
            return t;
        }
        var optionalChain = AstUtilities.IsOptionalChain(access);
        var removeNullable = strictNullChecks && (optionalChain || IsNonNullAccess(access)) && MaybeTypeOfKind(t, TypeFlags.Nullable);
        var nonNullType = t;
        if (removeNullable)
        {
            nonNullType = GetTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
        }
        var propType = GetTypeOfPropertyOfType(nonNullType, propName);
        if (propType is null)
        {
            return t;
        }
        if (removeNullable && optionalChain)
        {
            propType = GetOptionalType(propType, false);
        }
        var narrowedPropType = narrowType(propType);
        return FilterType(t, tt =>
        {
            var discriminantType = CoreUtil.OrElse(GetTypeOfPropertyOrIndexSignatureOfType(tt, propName), unknownType!);
            return (discriminantType!.flags & TypeFlags.Never) == 0 && (narrowedPropType.flags & TypeFlags.Never) == 0 && AreTypesComparable(narrowedPropType, discriminantType);
        });
    }

    // flow.go:738-745
    internal bool IsMatchingConstructorReference(FlowState f, Node expr)
    {
        if (AstUtilities.IsAccessExpression(expr))
        {
            if (GetAccessedPropertyName(expr, out var accessedName) && accessedName == "constructor" && IsMatchingReference(f.reference, expr.Expression()!))
            {
                return true;
            }
        }
        return false;
    }

    // flow.go:747-779
    internal Type NarrowTypeByConstructor(Type t, SyntaxKind operatorKind, Node identifier, bool assumeTrue)
    {
        // Do not narrow when checking inequality.
        if (assumeTrue && operatorKind != SyntaxKind.EqualsEqualsToken && operatorKind != SyntaxKind.EqualsEqualsEqualsToken || !assumeTrue && operatorKind != SyntaxKind.ExclamationEqualsToken && operatorKind != SyntaxKind.ExclamationEqualsEqualsToken)
        {
            return t;
        }
        // Get the type of the constructor identifier expression, if it is not a function then do not narrow.
        var identifierType = GetTypeOfExpression(identifier);
        if (!IsFunctionType(identifierType) && !IsConstructorType(identifierType))
        {
            return t;
        }
        // Get the prototype property of the type identifier so we can find out its type.
        var prototypeProperty = GetPropertyOfType(identifierType, "prototype");
        if (prototypeProperty is null)
        {
            return t;
        }
        // Get the type of the prototype, if it is undefined, or the global `Object` or `Function` types then do not narrow.
        var prototypeType = GetTypeOfSymbol(prototypeProperty);
        Type? candidate = null;
        if (!IsTypeAny(prototypeType))
        {
            candidate = prototypeType;
        }
        if (candidate is null || candidate == globalObjectType || candidate == globalFunctionType)
        {
            return t;
        }
        // If the type that is being narrowed is `any` then just return the `candidate` type since every type is a subtype of `any`.
        if (IsTypeAny(t))
        {
            return candidate;
        }
        // Filter out types that are not considered to be "constructed by" the `candidate` type.
        return FilterType(t, tt => IsConstructedBy(tt, candidate));
    }

    // flow.go:781-791
    internal bool IsConstructedBy(Type source, Type target)
    {
        // If either the source or target type are a class type then we need to check that they are the same exact type.
        // This is because you may have a class `A` that defines some set of properties, and another class `B`
        // that defines the same set of properties as class `A`, in that case they are structurally the same
        // type, but when you do something like `instanceOfA.constructor === B` it will return false.
        if ((source.flags & TypeFlags.Object) != 0 && (source.objectFlags & ObjectFlags.Class) != 0 || (target.flags & TypeFlags.Object) != 0 && (target.objectFlags & ObjectFlags.Class) != 0)
        {
            return source.symbol == target.symbol;
        }
        // For all other types just check that the `source` type is a subtype of the `target` type.
        return IsTypeSubtypeOf(source, target);
    }

    // flow.go:793-796
    internal Type NarrowTypeByBooleanComparison(FlowState f, Type t, Node expr, Node boolValue, SyntaxKind operatorKind, bool assumeTrue)
    {
        assumeTrue = (assumeTrue != (boolValue.Kind == SyntaxKind.TrueKeyword)) != (operatorKind != SyntaxKind.ExclamationEqualsEqualsToken && operatorKind != SyntaxKind.ExclamationEqualsToken);
        return NarrowType(f, t, expr, assumeTrue);
    }

    // flow.go:798-831
    internal Type NarrowTypeByInstanceof(FlowState f, Type t, BinaryExpression expr, bool assumeTrue)
    {
        var left = GetReferenceCandidate(expr.Left!);
        if (!IsMatchingReference(f.reference, left))
        {
            if (assumeTrue && strictNullChecks && OptionalChainContainsReference(left, f.reference))
            {
                return GetAdjustedTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
            }
            return t;
        }
        var right = expr.Right!;
        var rightType = GetTypeOfExpression(right);
        if (!IsTypeDerivedFrom(rightType, globalObjectType!))
        {
            return t;
        }
        // if the right-hand side has an object type with a custom `[Symbol.hasInstance]` method, and that method
        // has a type predicate, use the type predicate to perform narrowing. This allows normal `object` types to
        // participate in `instanceof`, as per Step 2 of https://tc39.es/ecma262/#sec-instanceofoperator.
        TypePredicate? predicate = null;
        var signature = GetEffectsSignature(expr.AsNode());
        if (signature is not null)
        {
            predicate = GetTypePredicateOfSignature(signature);
        }
        if (predicate is not null && predicate.kind == TypePredicateKind.Identifier && predicate.parameterIndex == 0)
        {
            return GetNarrowedType(t, predicate.t!, assumeTrue, true /*checkDerived*/);
        }
        if (!IsTypeDerivedFrom(rightType, globalFunctionType!))
        {
            return t;
        }
        var instanceType = MapType(rightType, GetInstanceType);
        // Don't narrow from `any` if the target type is exactly `Object` or `Function`, and narrow
        // in the false branch only if the target is a non-empty object type.
        if (IsTypeAny(t) && (instanceType == globalObjectType || instanceType == globalFunctionType) || !assumeTrue && !((instanceType.flags & TypeFlags.Object) != 0 && !IsEmptyAnonymousObjectType(instanceType)))
        {
            return t;
        }
        return GetNarrowedType(t, instanceType, assumeTrue, true /*checkDerived*/);
    }

    // flow.go:833-844
    internal Type GetNarrowedType(Type t, Type candidate, bool assumeTrue, bool checkDerived)
    {
        if ((t.flags & TypeFlags.Union) == 0)
        {
            return GetNarrowedTypeWorker(t, candidate, assumeTrue, checkDerived);
        }
        var key = new NarrowedTypeKey(t, candidate, assumeTrue, checkDerived);
        if (narrowedTypes.TryGetValue(key, out var narrowedType))
        {
            return narrowedType;
        }
        narrowedType = GetNarrowedTypeWorker(t, candidate, assumeTrue, checkDerived);
        narrowedTypes[key] = narrowedType;
        return narrowedType;
    }

    // flow.go:846-951
    internal Type GetNarrowedTypeWorker(Type t, Type candidate, bool assumeTrue, bool checkDerived)
    {
        if (!assumeTrue)
        {
            if (t == candidate)
            {
                return neverType!;
            }
            if (checkDerived)
            {
                return FilterType(t, tt => !IsTypeDerivedFrom(tt, candidate));
            }
            if ((t.flags & TypeFlags.Unknown) != 0)
            {
                t = unknownUnionType!;
            }
            var trueTypeInner = GetNarrowedType(t, candidate, true /*assumeTrue*/, false /*checkDerived*/);
            return RecombineUnknownType(FilterType(t, tt => !IsTypeSubsetOf(tt, trueTypeInner)));
        }
        if ((t.flags & TypeFlags.AnyOrUnknown) != 0)
        {
            return candidate;
        }
        if (t == candidate)
        {
            return candidate;
        }
        // We first attempt to filter the current type, narrowing constituents as appropriate and removing
        // constituents that are unrelated to the candidate.
        var keyPropertyName = "";
        if ((t.flags & TypeFlags.Union) != 0)
        {
            keyPropertyName = GetKeyPropertyName(t);
        }
        var narrowedType = MapType(candidate, n =>
        {
            // If a discriminant property is available, use that to reduce the type.
            var matching = t;
            if (keyPropertyName != "")
            {
                var discriminant = GetTypeOfPropertyOfType(n, keyPropertyName);
                if (discriminant is not null)
                {
                    var constituent = GetConstituentTypeForKeyType(t, discriminant);
                    if (constituent is not null)
                    {
                        matching = constituent;
                    }
                }
            }
            // For each constituent t in the current type, if t and c are directly related, pick the most
            // specific of the two. When t and c are related in both directions, we prefer c for type predicates
            // because that is the asserted type, but t for `instanceof` because generics aren't reflected in
            // prototype object types.
            Func<Type, Type> mapTypeFn;
            if (checkDerived)
            {
                mapTypeFn = tt =>
                {
                    if (IsTypeDerivedFrom(tt, n))
                    {
                        return tt;
                    }
                    if (IsTypeDerivedFrom(n, tt))
                    {
                        return n;
                    }
                    return neverType!;
                };
            }
            else
            {
                mapTypeFn = tt =>
                {
                    if (IsTypeStrictSubtypeOf(tt, n))
                    {
                        return tt;
                    }
                    if (IsTypeStrictSubtypeOf(n, tt))
                    {
                        return n;
                    }
                    if (IsTypeSubtypeOf(tt, n))
                    {
                        return tt;
                    }
                    if (IsTypeSubtypeOf(n, tt))
                    {
                        return n;
                    }
                    return neverType!;
                };
            }
            var directlyRelated = MapType(matching, mapTypeFn);
            if ((directlyRelated.flags & TypeFlags.Never) == 0)
            {
                return directlyRelated;
            }
            // If no constituents are directly related, create intersections for any generic constituents that
            // are related by constraint.
            Func<Type, Type, bool> isRelated;
            if (checkDerived)
            {
                isRelated = IsTypeDerivedFrom;
            }
            else
            {
                isRelated = IsTypeSubtypeOf;
            }
            return MapType(t, tt =>
            {
                if (MaybeTypeOfKind(tt, TypeFlags.Instantiable))
                {
                    var constraint = GetBaseConstraintOfType(tt);
                    if (constraint is null || isRelated(n, constraint))
                    {
                        return GetIntersectionType([tt, n]);
                    }
                }
                return neverType!;
            });
        });
        // If filtering produced a non-empty type, return that. Otherwise, pick the most specific of the two
        // based on assignability, or as a last resort produce an intersection.
        if ((narrowedType.flags & TypeFlags.Never) == 0)
        {
            return narrowedType;
        }
        if (IsTypeSubtypeOf(candidate, t))
        {
            return candidate;
        }
        if (IsTypeAssignableTo(t, candidate))
        {
            return t;
        }
        if (IsTypeAssignableTo(candidate, t))
        {
            return candidate;
        }
        return GetIntersectionType([t, candidate]);
    }

    // flow.go:953-967
    internal Type GetInstanceType(Type constructorType)
    {
        var prototypePropertyType = GetTypeOfPropertyOfType(constructorType, "prototype");
        if (prototypePropertyType is not null && !IsTypeAny(prototypePropertyType))
        {
            return prototypePropertyType;
        }
        var constructSignatures = GetSignaturesOfType(constructorType, SignatureKind.Construct);
        if (constructSignatures.Length != 0)
        {
            return GetUnionType(CoreUtil.Map(constructSignatures, signature =>
                GetReturnTypeOfSignature(GetErasedSignature(signature)))!);
        }
        // We use the empty object type to indicate we don't know the type of objects created by
        // this constructor function.
        return emptyObjectType!;
    }

    // flow.go:969-986
    internal Type NarrowTypeByPrivateIdentifierInInExpression(FlowState f, Type t, BinaryExpression expr, bool assumeTrue)
    {
        var target = GetReferenceCandidate(expr.Right!);
        if (!IsMatchingReference(f.reference, target))
        {
            return t;
        }
        var symbol = GetSymbolForPrivateIdentifierExpression(expr.Left!);
        if (symbol is null)
        {
            return t;
        }
        var classSymbol = symbol.Parent;
        Type targetType;
        if (AstUtilities.HasStaticModifier(symbol.ValueDeclaration!))
        {
            targetType = GetTypeOfSymbol(classSymbol!);
        }
        else
        {
            targetType = GetDeclaredTypeOfSymbol(classSymbol!);
        }
        return GetNarrowedType(t, targetType, assumeTrue, true /*checkDerived*/);
    }

    // flow.go:988-1009
    internal Type NarrowTypeByInKeyword(FlowState f, Type t, Type nameType, bool assumeTrue)
    {
        var name = GetPropertyNameFromType(nameType);
        var isKnownProperty = SomeType(t, tt =>
            IsTypePresencePossible(tt, name, true /*assumeTrue*/));
        if (isKnownProperty)
        {
            // If the check is for a known property (i.e. a property declared in some constituent of
            // the target type), we filter the target type by presence of absence of the property.
            return FilterType(t, tt =>
                IsTypePresencePossible(tt, name, assumeTrue));
        }
        if (assumeTrue)
        {
            // If the check is for an unknown property, we intersect the target type with `Record<X, unknown>`,
            // where X is the name of the property.
            var recordSymbol = GetGlobalRecordSymbol();
            if (recordSymbol is not null)
            {
                return GetIntersectionType([t, GetTypeAliasInstantiation(recordSymbol, [nameType, unknownType!], null)]);
            }
        }
        return t;
    }

    // flow.go:1011-1017
    internal bool IsTypePresencePossible(Type t, string propName, bool assumeTrue)
    {
        var prop = GetPropertyOfType(t, propName);
        if (prop is not null)
        {
            return (prop.Flags & SymbolFlags.Optional) != 0 || (prop.CheckFlags & CheckFlags.Partial) != 0 || assumeTrue;
        }
        return GetApplicableIndexInfoForName(t, propName) is not null || !assumeTrue;
    }

    // flow.go:1019-1044
    internal Type NarrowTypeByOptionalChainContainment(FlowState f, Type t, SyntaxKind operatorKind, Node value, bool assumeTrue)
    {
        // We are in a branch of obj?.foo === value (or any one of the other equality operators). We narrow obj as follows:
        // When operator is === and type of value excludes undefined, null and undefined is removed from type of obj in true branch.
        // When operator is !== and type of value excludes undefined, null and undefined is removed from type of obj in false branch.
        // When operator is == and type of value excludes null and undefined, null and undefined is removed from type of obj in true branch.
        // When operator is != and type of value excludes null and undefined, null and undefined is removed from type of obj in false branch.
        // When operator is === and type of value is undefined, null and undefined is removed from type of obj in false branch.
        // When operator is !== and type of value is undefined, null and undefined is removed from type of obj in true branch.
        // When operator is == and type of value is null or undefined, null and undefined is removed from type of obj in false branch.
        // When operator is != and type of value is null or undefined, null and undefined is removed from type of obj in true branch.
        var equalsOperator = operatorKind == SyntaxKind.EqualsEqualsToken || operatorKind == SyntaxKind.EqualsEqualsEqualsToken;
        TypeFlags nullableFlags;
        if (operatorKind == SyntaxKind.EqualsEqualsToken || operatorKind == SyntaxKind.ExclamationEqualsToken)
        {
            nullableFlags = TypeFlags.Nullable;
        }
        else
        {
            nullableFlags = TypeFlags.Undefined;
        }
        var valueType = GetTypeOfExpression(value);
        // Note that we include any and unknown in the exclusion test because their domain includes null and undefined.
        var removeNullable = equalsOperator != assumeTrue && EveryType(valueType, tt => (tt.flags & nullableFlags) != 0) ||
            equalsOperator == assumeTrue && EveryType(valueType, tt => (tt.flags & (TypeFlags.AnyOrUnknown | nullableFlags)) == 0);
        if (removeNullable)
        {
            return GetAdjustedTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
        }
        return t;
    }
}
