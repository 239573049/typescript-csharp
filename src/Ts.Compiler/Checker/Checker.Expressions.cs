// Ported from tsgo internal/checker/checker.go (section: expressions), lines ~7494-12292.
// plan/03 §4.2 #7. Faithful per-function translation: same function order, same branch structure,
// Go comments and `// !!!` markers preserved.
//
// Breadth-first bring-up (plan mandate: dispatch trunk + literals + identifier/property-access core;
// complex leaves stubbed and annotated). This file carries the checkExpression* dispatch trunk
// (checkExpressionCachedEx 7494 → checkExpressionEx → checkExpressionWorker 7708 big switch), the
// inline literal cases (string/number/bigint/true/false/null — the smoke-testable currency), and
// getResolvedSymbol's public entry (CheckExpression). The large leaf checkers (identifier,
// property/element access, call/new overload resolution, object/array literals, templates,
// decorators, JSX, contextual typing) are stubbed in Checker.Expressions.Deps.cs pending their own
// breadth passes; the dispatch switch routes to those stubs so the assembly compiles and simple
// literal expressions type-check for real.
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified; the
// no-collision hosts (NodeTests/AstUtilities/CoreUtil/Scanner) are used directly, as in flow/relater.
//
// R05 (深递归): the expression tree descends via checkExpressionEx with no trampoline (faithful to
// tsgo); deep left-associative chains rely on the 64MB LargeStackExecutor stack (AGENTS.md §4 铁律8).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:7494-7517
    internal Type CheckExpressionCachedEx(Node node, CheckMode checkMode)
    {
        if (checkMode != CheckMode.Normal)
        {
            return CheckExpressionEx(node, checkMode);
        }
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType == null)
        {
            // When computing a type that we're going to cache, we need to ignore any ongoing control flow
            // analysis because variables may have transient types in indeterminable states. Moving flowLoopStart
            // to the top of the stack ensures all transient types are computed from a known point.
            var saveFlowLoopStack = flowLoopStack;
            var saveFlowTypeCache = flowTypeCache;
            flowLoopStack = null!;
            flowTypeCache = null!;
            links.resolvedType = CheckExpressionEx(node, checkMode);
            flowTypeCache = saveFlowTypeCache;
            flowLoopStack = saveFlowLoopStack;
        }
        return links.resolvedType!;
    }

    // Returns the type of an expression. Unlike checkExpression, this function is simply concerned
    // with computing the type and may not fully check all contained sub-expressions for errors.
    // checker.go:7519-7528
    internal Type GetContextFreeTypeOfExpressionCore(Node node)
    {
        if (contextFreeTypes.TryGetValue(node, out var cached) && cached != null)
        {
            return cached;
        }
        PushContextualType(node, anyType!, false /*isCache*/);
        var t = CheckExpressionEx(node, CheckMode.SkipContextSensitive);
        contextFreeTypes[node] = t;
        PopContextualType();
        return t;
    }

    // checker.go:7530-7532
    internal Type CheckExpressionCore(Node node)
    {
        return CheckExpressionEx(node, CheckMode.Normal);
    }

    // checker.go:7534-7548
    internal Type CheckExpressionEx(Node node, CheckMode checkMode)
    {
        // !!! tracing (tr.Push): omitted (tracer wiring lands with the tracing wave).
        var saveCurrentNode = currentNode;
        currentNode = node;
        instantiationCount = 0;
        var uninstantiatedType = CheckExpressionWorker(node, checkMode);
        var t = InstantiateTypeWithSingleGenericCallSignature(node, uninstantiatedType, checkMode);
        if (IsConstEnumObjectType(t))
        {
            CheckConstEnumAccess(node, t);
        }
        currentNode = saveCurrentNode;
        return t;
    }

    // checker.go:10019
    internal Type CheckParenthesizedExpression(Node node, CheckMode checkMode)
    {
        return CheckExpressionEx(node.Expression()!, checkMode);
    }

    // checker.go:7708-7812
    internal Type CheckExpressionWorker(Node node, CheckMode checkMode)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                return CheckIdentifier(node, checkMode);
            case SyntaxKind.PrivateIdentifier:
                return CheckPrivateIdentifierExpression(node);
            case SyntaxKind.ThisKeyword:
                return CheckThisExpression(node);
            case SyntaxKind.SuperKeyword:
                return CheckSuperExpression(node);
            case SyntaxKind.NullKeyword:
                return nullWideningType!;
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                if (IsSkipDirectInferenceNode(node))
                {
                    return blockedStringType!;
                }
                return GetFreshTypeOfLiteralType(GetStringLiteralType(node.Text()));
            case SyntaxKind.NumericLiteral:
                CheckGrammarNumericLiteral(node.AsNumericLiteral());
                return GetFreshTypeOfLiteralType(GetNumberLiteralType(Ts.JsNum.JsNum.FromString(node.Text())));
            case SyntaxKind.BigIntLiteral:
                CheckGrammarBigIntLiteral(node.AsBigIntLiteral());
                return GetFreshTypeOfLiteralType(GetBigIntLiteralType(new Ts.JsNum.PseudoBigInt(Ts.JsNum.JsNum.ParsePseudoBigInt(node.Text()), false /*negative*/)));
            case SyntaxKind.TrueKeyword:
                return trueType!;
            case SyntaxKind.FalseKeyword:
                return falseType!;
            case SyntaxKind.TemplateExpression:
                return CheckTemplateExpression(node);
            case SyntaxKind.RegularExpressionLiteral:
                return CheckRegularExpressionLiteral(node);
            case SyntaxKind.ArrayLiteralExpression:
                return CheckArrayLiteral(node, checkMode);
            case SyntaxKind.ObjectLiteralExpression:
                return CheckObjectLiteral(node, checkMode);
            case SyntaxKind.PropertyAccessExpression:
                return CheckPropertyAccessExpression(node, checkMode, false /*writeOnly*/);
            case SyntaxKind.QualifiedName:
                return CheckQualifiedName(node, checkMode);
            case SyntaxKind.ElementAccessExpression:
                return CheckIndexedAccess(node, checkMode);
            case SyntaxKind.CallExpression:
                if (IsImportCall(node))
                {
                    return CheckImportCallExpression(node);
                }
                return CheckCallExpression(node, checkMode);
            case SyntaxKind.NewExpression:
                return CheckCallExpression(node, checkMode);
            case SyntaxKind.TaggedTemplateExpression:
                return CheckTaggedTemplateExpression(node);
            case SyntaxKind.ParenthesizedExpression:
                return CheckParenthesizedExpression(node, checkMode);
            case SyntaxKind.ClassExpression:
                return CheckClassExpression(node);
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                return CheckFunctionExpressionOrObjectLiteralMethod(node, checkMode);
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.AsExpression:
                return CheckAssertion(node, checkMode);
            case SyntaxKind.TypeOfExpression:
                return CheckTypeOfExpression(node);
            case SyntaxKind.NonNullExpression:
                return CheckNonNullAssertion(node);
            case SyntaxKind.ExpressionWithTypeArguments:
                return CheckExpressionWithTypeArguments(node);
            case SyntaxKind.SatisfiesExpression:
                return CheckSatisfiesExpression(node);
            case SyntaxKind.MetaProperty:
                return CheckMetaProperty(node);
            case SyntaxKind.DeleteExpression:
                return CheckDeleteExpression(node);
            case SyntaxKind.VoidExpression:
                return CheckVoidExpression(node);
            case SyntaxKind.AwaitExpression:
                return CheckAwaitExpression(node);
            case SyntaxKind.PrefixUnaryExpression:
                return CheckPrefixUnaryExpression(node);
            case SyntaxKind.PostfixUnaryExpression:
                return CheckPostfixUnaryExpression(node);
            case SyntaxKind.BinaryExpression:
                return CheckBinaryExpressionCore(node, checkMode);
            case SyntaxKind.ConditionalExpression:
                return CheckConditionalExpression(node, checkMode);
            case SyntaxKind.SpreadElement:
                return CheckSpreadExpression(node, checkMode);
            case SyntaxKind.OmittedExpression:
                return undefinedWideningType!;
            case SyntaxKind.YieldExpression:
                return CheckYieldExpression(node);
            case SyntaxKind.SyntheticExpression:
                return CheckSyntheticExpression(node);
            case SyntaxKind.JsxExpression:
                return CheckJsxExpression(node, checkMode);
            case SyntaxKind.JsxElement:
                return CheckJsxElement(node, checkMode);
            case SyntaxKind.JsxSelfClosingElement:
                return CheckJsxSelfClosingElement(node, checkMode);
            case SyntaxKind.JsxFragment:
                return CheckJsxFragment(node);
            case SyntaxKind.JsxAttributes:
                return CheckJsxAttributes(node, checkMode);
            case SyntaxKind.JsxOpeningElement:
                throw new InvalidOperationException("Should never directly check a JsxOpeningElement");
        }
        return errorType!;
    }
}
