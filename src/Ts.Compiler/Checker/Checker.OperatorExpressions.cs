// Ported from tsgo internal/checker/checker.go (section: operator expressions), lines ~12293-13874.
// plan/03 §4.2 #8. Faithful per-function translation: same function order, same branch structure,
// Go comments and `// !!!` markers preserved.
//
// This file carries the binary-like expression engine (checkBinaryExpression/checkBinaryLikeExpression),
// assignment / arithmetic / comparison / logical / in / instanceof operator support, the truthiness &
// nullishness syntactic-semantics predicates, and getResolvedSymbol family (13814). Object-literal /
// spread checking (checkObjectLiteral 13094, getSpreadType 13319) and destructuring-assignment support
// (12514-12666) are large sub-engines and are stubbed in Checker.Expressions.Deps.cs pending their own
// breadth pass; call sites here reference those stubs so the assembly compiles.
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified; the
// no-collision hosts (NodeTests/AstUtilities/CoreUtil/Scanner) are used directly, as in flow/relater.
//
// R05 (深递归): checkBinaryLikeExpression recurses directly into checkExpressionEx for left/right
// operands (no trampoline) — faithful to tsgo; left-associative long chains rely on the 64MB
// LargeStackExecutor stack (AGENTS.md §4 铁律8), not a per-node trampoline.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

// checker.go:12839-12846 — PredicateSemantics.
// (Owned by #8; the truthiness/nullishness syntactic predicates live here.)
[System.Flags]
internal enum PredicateSemantics : uint
{
    None = 0,
    Always = 1 << 0,
    Never = 1 << 1,
    Sometimes = Always | Never,
}

public partial class Checker
{
    // checker.go:12293-12296
    internal Type CheckBinaryExpressionCore(Node node, CheckMode checkMode)
    {
        var binary = node.AsBinaryExpression();
        return CheckBinaryLikeExpression(binary.Left!, binary.OperatorToken!, binary.Right!, checkMode, node);
    }

    // checker.go:12298-12513
    internal Type CheckBinaryLikeExpression(Node left, Node operatorToken, Node right, CheckMode checkMode, Node? errorNode)
    {
        var op = operatorToken.Kind;
        if (op == SyntaxKind.EqualsToken && (left.Kind == SyntaxKind.ObjectLiteralExpression || left.Kind == SyntaxKind.ArrayLiteralExpression))
        {
            return CheckDestructuringAssignment(left, CheckExpressionEx(right, checkMode), checkMode, right.Kind == SyntaxKind.ThisKeyword);
        }
        var leftType = CheckExpressionEx(left, checkMode);
        var rightType = CheckExpressionEx(right, checkMode);
        if (AstUtilities.IsLogicalOrCoalescingBinaryOperator(op))
        {
            var parent = left.Parent!.Parent;
            while (NodeTests.IsParenthesizedExpression(parent!) || AstUtilities.IsLogicalOrCoalescingBinaryExpression(parent!))
            {
                parent = parent!.Parent;
            }
            if (op == SyntaxKind.AmpersandAmpersandToken || NodeTests.IsIfStatement(parent!))
            {
                Node? body = null;
                if (NodeTests.IsIfStatement(parent!))
                {
                    body = parent!.AsIfStatement().ThenStatement;
                }
                CheckTestingKnownTruthyCallableOrAwaitableOrEnumMemberType(left, leftType, body);
            }
            if (AstUtilities.IsLogicalBinaryOperator(op))
            {
                CheckTruthinessOfType(leftType, left);
            }
        }
        switch (op)
        {
            case SyntaxKind.AsteriskToken:
            case SyntaxKind.AsteriskAsteriskToken:
            case SyntaxKind.AsteriskEqualsToken:
            case SyntaxKind.AsteriskAsteriskEqualsToken:
            case SyntaxKind.SlashToken:
            case SyntaxKind.SlashEqualsToken:
            case SyntaxKind.PercentToken:
            case SyntaxKind.PercentEqualsToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.MinusEqualsToken:
            case SyntaxKind.LessThanLessThanToken:
            case SyntaxKind.LessThanLessThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanToken:
            case SyntaxKind.GreaterThanGreaterThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
            case SyntaxKind.BarToken:
            case SyntaxKind.BarEqualsToken:
            case SyntaxKind.CaretToken:
            case SyntaxKind.CaretEqualsToken:
            case SyntaxKind.AmpersandToken:
            case SyntaxKind.AmpersandEqualsToken:
            {
                if (leftType == silentNeverType || rightType == silentNeverType)
                {
                    return silentNeverType!;
                }
                leftType = CheckNonNullType(leftType, left);
                rightType = CheckNonNullType(rightType, right);
                // if a user tries to apply a bitwise operator to 2 boolean operands
                // try and return them a helpful suggestion
                if ((leftType.flags & TypeFlags.BooleanLike) != 0 && (rightType.flags & TypeFlags.BooleanLike) != 0)
                {
                    var suggestedOperator = GetSuggestedBooleanOperator(op);
                    if (suggestedOperator != SyntaxKind.Unknown)
                    {
                        Error(operatorToken, Diag.The_0_operator_is_not_allowed_for_boolean_types_Consider_using_1_instead, global::Ts.Scanner.Scanner.TokenToString(operatorToken.Kind), global::Ts.Scanner.Scanner.TokenToString(suggestedOperator));
                        return numberType!;
                    }
                }
                // otherwise just check each operand separately and report errors as normal
                var leftOk = CheckArithmeticOperandType(left, leftType, Diag.The_left_hand_side_of_an_arithmetic_operation_must_be_of_type_any_number_bigint_or_an_enum_type, true /*isAwaitValid*/);
                var rightOk = CheckArithmeticOperandType(right, rightType, Diag.The_right_hand_side_of_an_arithmetic_operation_must_be_of_type_any_number_bigint_or_an_enum_type, true /*isAwaitValid*/);
                Type resultType;
                // If both are any or unknown, allow operation; assume it will resolve to number
                if ((IsTypeAssignableToKind(leftType, TypeFlags.AnyOrUnknown) && IsTypeAssignableToKind(rightType, TypeFlags.AnyOrUnknown)) || (!MaybeTypeOfKind(leftType, TypeFlags.BigIntLike) && !MaybeTypeOfKind(rightType, TypeFlags.BigIntLike)))
                {
                    resultType = numberType!;
                }
                else if (BothAreBigIntLike(leftType, rightType))
                {
                    switch (op)
                    {
                        case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
                        case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
                            ReportOperatorError(leftType, op, rightType, errorNode, null);
                            break;
                        case SyntaxKind.AsteriskAsteriskToken:
                        case SyntaxKind.AsteriskAsteriskEqualsToken:
                            if (languageVersion < ScriptTarget.ES2016)
                            {
                                Error(errorNode, Diag.Exponentiation_cannot_be_performed_on_bigint_values_unless_the_target_option_is_set_to_es2016_or_later);
                            }
                            break;
                    }
                    resultType = bigintType!;
                }
                else
                {
                    ReportOperatorError(leftType, op, rightType, errorNode, BothAreBigIntLike);
                    resultType = errorType!;
                }
                if (leftOk && rightOk)
                {
                    CheckAssignmentOperator(left, op, right, leftType, resultType);
                    switch (op)
                    {
                        case SyntaxKind.LessThanLessThanToken:
                        case SyntaxKind.LessThanLessThanEqualsToken:
                        case SyntaxKind.GreaterThanGreaterThanToken:
                        case SyntaxKind.GreaterThanGreaterThanEqualsToken:
                        case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
                        case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
                        {
                            var rhsEval = evaluate(right, right);
                            if (rhsEval.Value is Ts.JsNum.Number numValue && numValue.Abs() >= new Ts.JsNum.Number(32))
                            {
                                // Elevate from suggestion to error within an enum member
                                ErrorOrSuggestion(NodeTests.IsEnumMember(AstUtilities.WalkUpParenthesizedExpressions(right.Parent!.Parent)!), errorNode, Diag.This_operation_can_be_simplified_This_shift_is_identical_to_0_1_2, global::Ts.Scanner.Scanner.GetTextOfNode(left), global::Ts.Scanner.Scanner.TokenToString(op), numValue.Remainder(new Ts.JsNum.Number(32)));
                            }
                            break;
                        }
                    }
                }
                return resultType;
            }
            case SyntaxKind.PlusToken:
            case SyntaxKind.PlusEqualsToken:
            {
                if (leftType == silentNeverType || rightType == silentNeverType)
                {
                    return silentNeverType!;
                }
                if (!IsTypeAssignableToKind(leftType, TypeFlags.StringLike) && !IsTypeAssignableToKind(rightType, TypeFlags.StringLike))
                {
                    leftType = CheckNonNullType(leftType, left);
                    rightType = CheckNonNullType(rightType, right);
                }
                Type? resultType = null;
                if (IsTypeAssignableToKindEx(leftType, TypeFlags.NumberLike, true /*strict*/) && IsTypeAssignableToKindEx(rightType, TypeFlags.NumberLike, true /*strict*/))
                {
                    // Operands of an enum type are treated as having the primitive type Number.
                    // If both operands are of the Number primitive type, the result is of the Number primitive type.
                    resultType = numberType!;
                }
                else if (IsTypeAssignableToKindEx(leftType, TypeFlags.BigIntLike, true /*strict*/) && IsTypeAssignableToKindEx(rightType, TypeFlags.BigIntLike, true /*strict*/))
                {
                    // If both operands are of the BigInt primitive type, the result is of the BigInt primitive type.
                    resultType = bigintType!;
                }
                else if (IsTypeAssignableToKindEx(leftType, TypeFlags.StringLike, true /*strict*/) || IsTypeAssignableToKindEx(rightType, TypeFlags.StringLike, true /*strict*/))
                {
                    // If one or both operands are of the String primitive type, the result is of the String primitive type.
                    resultType = stringType!;
                }
                else if (IsTypeAny(leftType) || IsTypeAny(rightType))
                {
                    // Otherwise, the result is of type Any.
                    // NOTE: unknown type here denotes error type. Old compiler treated this case as any type so do we.
                    resultType = (IsErrorType(leftType) || IsErrorType(rightType)) ? errorType! : anyType!;
                }
                // Symbols are not allowed at all in arithmetic expressions
                if (resultType != null && !CheckForDisallowedESSymbolOperand(left, right, leftType, rightType, op))
                {
                    return resultType;
                }
                if (resultType == null)
                {
                    // Types that have a reasonably good chance of being a valid operand type.
                    // If both types have an awaited type of one of these, we'll assume the user
                    // might be missing an await without doing an exhaustive check that inserting
                    // await(s) will actually be a completely valid binary expression.
                    var closeEnoughKind = TypeFlags.NumberLike | TypeFlags.BigIntLike | TypeFlags.StringLike | TypeFlags.AnyOrUnknown;
                    ReportOperatorError(leftType, op, rightType, errorNode, (l, r) => IsTypeAssignableToKind(l, closeEnoughKind) && IsTypeAssignableToKind(r, closeEnoughKind));
                    return anyType!;
                }
                if (op == SyntaxKind.PlusEqualsToken)
                {
                    CheckAssignmentOperator(left, op, right, leftType, resultType);
                }
                return resultType;
            }
            case SyntaxKind.LessThanToken:
            case SyntaxKind.GreaterThanToken:
            case SyntaxKind.LessThanEqualsToken:
            case SyntaxKind.GreaterThanEqualsToken:
            {
                if (CheckForDisallowedESSymbolOperand(left, right, leftType, rightType, op))
                {
                    leftType = GetBaseTypeOfLiteralTypeForComparison(CheckNonNullType(leftType, left));
                    rightType = GetBaseTypeOfLiteralTypeForComparison(CheckNonNullType(rightType, right));
                    ReportOperatorErrorUnless(leftType, op, rightType, errorNode, (l, r) =>
                    {
                        if (IsTypeAny(l) || IsTypeAny(r))
                        {
                            return true;
                        }
                        var leftAssignableToNumber = IsTypeAssignableTo(l, numberOrBigIntType!);
                        var rightAssignableToNumber = IsTypeAssignableTo(r, numberOrBigIntType!);
                        return (leftAssignableToNumber && rightAssignableToNumber) || (!leftAssignableToNumber && !rightAssignableToNumber && AreTypesComparable(l, r));
                    });
                }
                return booleanType!;
            }
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
            {
                // We suppress errors in CheckMode.TypeOnly (meaning the invocation came from getTypeOfExpression). During
                // control flow analysis it is possible for operands to temporarily have narrower types, and those narrower
                // types may cause the operands to not be comparable. We don't want such errors reported (see #46475).
                if ((checkMode & CheckMode.TypeOnly) == 0)
                {
                    if ((IsLiteralExpressionOfObject(left) || IsLiteralExpressionOfObject(right)) &&
                        // only report for === and !== in JS, not == or !=
                        (!AstUtilities.IsInJSFile(left) || (op == SyntaxKind.EqualsEqualsEqualsToken || op == SyntaxKind.ExclamationEqualsEqualsToken)))
                    {
                        var eqType = op == SyntaxKind.EqualsEqualsToken || op == SyntaxKind.EqualsEqualsEqualsToken;
                        Error(errorNode, Diag.This_condition_will_always_return_0_since_JavaScript_compares_objects_by_reference_not_value, CoreUtil.IfElse(eqType, "false", "true"));
                    }
                    CheckNaNEquality(errorNode, op, left, right);
                    ReportOperatorErrorUnless(leftType, op, rightType, errorNode, (l, r) => IsTypeEqualityComparableTo(l, r) || IsTypeEqualityComparableTo(r, l));
                }
                return booleanType!;
            }
            case SyntaxKind.InstanceOfKeyword:
                return CheckInstanceOfExpression(left, right, leftType, rightType, checkMode);
            case SyntaxKind.InKeyword:
                return CheckInExpression(left, right, leftType, rightType);
            case SyntaxKind.AmpersandAmpersandToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
            {
                var resultType = leftType;
                if (HasTypeFacts(leftType, TypeFacts.Truthy))
                {
                    var t = leftType;
                    if (!strictNullChecks)
                    {
                        t = GetBaseTypeOfLiteralType(rightType);
                    }
                    resultType = GetUnionType([ExtractDefinitelyFalsyTypes(t), rightType]);
                }
                if (op == SyntaxKind.AmpersandAmpersandEqualsToken)
                {
                    CheckAssignmentOperator(left, op, right, leftType, rightType);
                }
                return resultType;
            }
            case SyntaxKind.BarBarToken:
            case SyntaxKind.BarBarEqualsToken:
            {
                var resultType = leftType;
                if (HasTypeFacts(leftType, TypeFacts.Falsy))
                {
                    resultType = GetUnionTypeEx([GetNonNullableType(RemoveDefinitelyFalsyTypes(leftType)), rightType], UnionReduction.Subtype, null, null);
                }
                if (op == SyntaxKind.BarBarEqualsToken)
                {
                    CheckAssignmentOperator(left, op, right, leftType, rightType);
                }
                return resultType;
            }
            case SyntaxKind.QuestionQuestionToken:
            case SyntaxKind.QuestionQuestionEqualsToken:
            {
                if (op == SyntaxKind.QuestionQuestionToken)
                {
                    CheckNullishCoalesceOperands(left, right);
                }
                var resultType = leftType;
                if (HasTypeFacts(leftType, TypeFacts.EQUndefinedOrNull))
                {
                    resultType = GetUnionTypeEx([GetNonNullableType(leftType), rightType], UnionReduction.Subtype, null, null);
                }
                if (op == SyntaxKind.QuestionQuestionEqualsToken)
                {
                    CheckAssignmentOperator(left, op, right, leftType, rightType);
                }
                return resultType;
            }
            case SyntaxKind.EqualsToken:
                CheckAssignmentOperator(left, op, right, leftType, rightType);
                return rightType;
            case SyntaxKind.CommaToken:
            {
                if (!compilerOptions.AllowUnreachableCode.IsTrue() && IsSideEffectFree(left) && !IsIndirectCall(left.Parent!))
                {
                    var sf = AstUtilities.GetSourceFileOfNode(left)!;
                    var start = global::Ts.Scanner.Scanner.SkipTrivia(sf.Text(), left.Pos);
                    var isInDiag2657 = CoreUtil.Some(sf.Diagnostics(), d =>
                    {
                        if (d.Code() != Diag.JSX_expressions_must_have_one_parent_element.Code)
                        {
                            return false;
                        }
                        return d.Loc().Contains(start);
                    });
                    if (!isInDiag2657)
                    {
                        Error(left, Diag.Left_side_of_comma_operator_is_unused_and_has_no_side_effects);
                    }
                }
                return rightType;
            }
        }
        throw new InvalidOperationException("Unhandled case in checkBinaryLikeExpression");
    }

    // checker.go:12680-12699
    internal void ReportOperatorError(Type leftType, SyntaxKind op, Type rightType, Node? errorNode, Func<Type, Type, bool>? isRelated)
    {
        var wouldWorkWithAwait = false;
        if (isRelated != null)
        {
            var awaitedLeftType = GetAwaitedTypeNoAlias(leftType);
            var awaitedRightType = GetAwaitedTypeNoAlias(rightType);
            wouldWorkWithAwait = !(awaitedLeftType == leftType && awaitedRightType == rightType) && awaitedLeftType != null && awaitedRightType != null && isRelated(awaitedLeftType, awaitedRightType);
        }
        var effectiveLeft = leftType;
        var effectiveRight = rightType;
        if (!wouldWorkWithAwait && isRelated != null)
        {
            (effectiveLeft, effectiveRight) = GetBaseTypesIfUnrelated(leftType, rightType, isRelated);
        }
        var (leftStr, rightStr) = GetTypeNamesForErrorDisplay(effectiveLeft, effectiveRight);
        switch (op)
        {
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
                ErrorAndMaybeSuggestAwait(errorNode, wouldWorkWithAwait, Diag.This_comparison_appears_to_be_unintentional_because_the_types_0_and_1_have_no_overlap, leftStr, rightStr);
                break;
            default:
                ErrorAndMaybeSuggestAwait(errorNode, wouldWorkWithAwait, Diag.Operator_0_cannot_be_applied_to_types_1_and_2, global::Ts.Scanner.Scanner.TokenToString(op), leftStr, rightStr);
                break;
        }
    }

    // checker.go:12701-12705
    internal void ReportOperatorErrorUnless(Type leftType, SyntaxKind op, Type rightType, Node? errorNode, Func<Type, Type, bool> typesAreCompatible)
    {
        if (!typesAreCompatible(leftType, rightType))
        {
            ReportOperatorError(leftType, op, rightType, errorNode, typesAreCompatible);
        }
    }

    // checker.go:12707-12717
    internal (Type, Type) GetBaseTypesIfUnrelated(Type leftType, Type rightType, Func<Type, Type, bool> isRelated)
    {
        var effectiveLeft = leftType;
        var effectiveRight = rightType;
        var leftBase = GetBaseTypeOfLiteralType(leftType);
        var rightBase = GetBaseTypeOfLiteralType(rightType);
        if (!isRelated(leftBase, rightBase))
        {
            effectiveLeft = leftBase;
            effectiveRight = rightBase;
        }
        return (effectiveLeft, effectiveRight);
    }

    // checker.go:12719-12743
    internal void CheckAssignmentOperator(Node left, SyntaxKind op, Node right, Type leftType, Type rightType)
    {
        if (NodeTests.IsAssignmentOperator(op))
        {
            // We ignore assignments of undefined to CommonJS exports when there are multiple assignment declarations
            if (AstUtilities.IsDeclarationNode(left.Parent!) && AstUtilities.GetAssignmentDeclarationKind(left.Parent!) == JSDeclarationKind.ExportsProperty)
            {
                var symbol = symbolNodeLinks.Get(left).resolvedSymbol;
                if (symbol != null && symbol.Declarations.Len() > 1 && (rightType.flags & TypeFlags.Undefined) != 0)
                {
                    return;
                }
            }
            // getters can be a subtype of setters, so to check for assignability we use the setter's type instead
            if (AstUtilities.IsCompoundAssignment(op) && NodeTests.IsPropertyAccessExpression(left))
            {
                leftType = CheckPropertyAccessExpression(left, CheckMode.Normal, true /*writeOnly*/);
            }
            if (CheckReferenceExpression(left, Diag.The_left_hand_side_of_an_assignment_expression_must_be_a_variable_or_a_property_access, Diag.The_left_hand_side_of_an_assignment_expression_may_not_be_an_optional_property_access))
            {
                global::Ts.Diagnostics.Message? headMessage = null;
                if (exactOptionalPropertyTypes && NodeTests.IsPropertyAccessExpression(left) && MaybeTypeOfKind(rightType, TypeFlags.Undefined))
                {
                    var target = GetTypeOfPropertyOfType(GetTypeOfExpression(left.Expression()!), left.Name()!.Text());
                    if (IsExactOptionalPropertyMismatch(rightType, target))
                    {
                        headMessage = Diag.Type_0_is_not_assignable_to_type_1_with_exactOptionalPropertyTypes_Colon_true_Consider_adding_undefined_to_the_type_of_the_target;
                    }
                }
                // to avoid cascading errors check assignability only if 'isReference' check succeeded and no errors were reported
                CheckTypeAssignableToAndOptionallyElaborate(rightType, leftType, left, right, headMessage, null);
            }
        }
    }

    // checker.go:12745-12747
    internal bool BothAreBigIntLike(Type left, Type right)
    {
        return IsTypeAssignableToKind(left, TypeFlags.BigIntLike) && IsTypeAssignableToKind(right, TypeFlags.BigIntLike);
    }

    // checker.go:12749-12759
    internal SyntaxKind GetSuggestedBooleanOperator(SyntaxKind op)
    {
        switch (op)
        {
            case SyntaxKind.BarToken:
            case SyntaxKind.BarEqualsToken:
                return SyntaxKind.BarBarToken;
            case SyntaxKind.CaretToken:
            case SyntaxKind.CaretEqualsToken:
                return SyntaxKind.ExclamationEqualsEqualsToken;
            case SyntaxKind.AmpersandToken:
            case SyntaxKind.AmpersandEqualsToken:
                return SyntaxKind.AmpersandAmpersandToken;
        }
        return SyntaxKind.Unknown;
    }

    // checker.go:12761-12772
    internal bool CheckArithmeticOperandType(Node operand, Type t, global::Ts.Diagnostics.Message diagnostic, bool isAwaitValid)
    {
        if (!IsTypeAssignableTo(t, numberOrBigIntType!))
        {
            Type? awaitedType = null;
            if (isAwaitValid)
            {
                awaitedType = GetAwaitedTypeOfPromise(t);
            }
            ErrorAndMaybeSuggestAwait(operand, awaitedType != null && IsTypeAssignableTo(awaitedType, numberOrBigIntType!), diagnostic);
            return false;
        }
        return true;
    }

    // Return true if there was no error, false if there was an error.
    // checker.go:12774-12787
    internal bool CheckForDisallowedESSymbolOperand(Node left, Node right, Type leftType, Type rightType, SyntaxKind op)
    {
        Node? offendingSymbolOperand = null;
        if (MaybeTypeOfKindConsideringBaseConstraint(leftType, TypeFlags.ESSymbolLike))
        {
            offendingSymbolOperand = left;
        }
        else if (MaybeTypeOfKindConsideringBaseConstraint(rightType, TypeFlags.ESSymbolLike))
        {
            offendingSymbolOperand = right;
        }
        if (offendingSymbolOperand != null)
        {
            Error(offendingSymbolOperand, Diag.The_0_operator_cannot_be_applied_to_type_symbol, global::Ts.Scanner.Scanner.TokenToString(op));
            return false;
        }
        return true;
    }

    // checker.go:12789-12813
    internal void CheckNaNEquality(Node? errorNode, SyntaxKind op, Node left, Node right)
    {
        var isLeftNaN = IsGlobalNaN(AstUtilities.SkipParentheses(left));
        var isRightNaN = IsGlobalNaN(AstUtilities.SkipParentheses(right));
        if (isLeftNaN || isRightNaN)
        {
            var err = Error(errorNode, Diag.This_condition_will_always_return_0, global::Ts.Scanner.Scanner.TokenToString(CoreUtil.IfElse(op == SyntaxKind.EqualsEqualsEqualsToken || op == SyntaxKind.EqualsEqualsToken, SyntaxKind.FalseKeyword, SyntaxKind.TrueKeyword)));
            if (isLeftNaN && isRightNaN)
            {
                return;
            }
            var operatorString = "";
            if (op == SyntaxKind.ExclamationEqualsEqualsToken || op == SyntaxKind.ExclamationEqualsToken)
            {
                operatorString = global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.ExclamationToken);
            }
            var location = left;
            if (isLeftNaN)
            {
                location = right;
            }
            var expression = AstUtilities.SkipParentheses(location);
            var entityName = "...";
            if (AstUtilities.IsEntityNameExpression(expression))
            {
                entityName = EntityNameToString(expression);
            }
            var suggestion = operatorString + "Number.isNaN(" + entityName + ")";
            err!.AddRelatedInfo(CreateDiagnosticForNode(location, Diag.Did_you_mean_0, suggestion));
        }
    }

    // checker.go:12815-12821
    internal bool IsGlobalNaN(Node expr)
    {
        if (NodeTests.IsIdentifier(expr) && expr.Text() == "NaN")
        {
            var globalNaNSymbol = GetGlobalNaNSymbolOrNil();
            return globalNaNSymbol != null && globalNaNSymbol == GetResolvedSymbolCore(expr);
        }
        return false;
    }

    // checker.go:12823-12825
    internal bool IsTypeEqualityComparableTo(Type source, Type target)
    {
        return (target.flags & TypeFlags.Nullable) != 0 || IsTypeComparableTo(source, target);
    }

    // checker.go:12827-12837
    internal Type CheckTruthinessOfType(Type t, Node node)
    {
        if ((t.flags & TypeFlags.Void) != 0)
        {
            Error(node, Diag.An_expression_of_type_void_cannot_be_tested_for_truthiness);
            return t;
        }
        var semantics = GetSyntacticTruthySemantics(node);
        if (semantics != PredicateSemantics.Sometimes)
        {
            Error(node, CoreUtil.IfElse(semantics == PredicateSemantics.Always, Diag.This_kind_of_expression_is_always_truthy, Diag.This_kind_of_expression_is_always_falsy));
        }
        return t;
    }

    // checker.go:12848-12875
    internal PredicateSemantics GetSyntacticTruthySemantics(Node node)
    {
        node = AstUtilities.SkipOuterExpressions(node, OuterExpressionKinds.All);
        switch (node.Kind)
        {
            case SyntaxKind.NumericLiteral:
                // Allow `while(0)` or `while(1)`
                if (node.Text() == "0" || node.Text() == "1")
                {
                    return PredicateSemantics.Sometimes;
                }
                return PredicateSemantics.Always;
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.RegularExpressionLiteral:
                return PredicateSemantics.Always;
            case SyntaxKind.VoidExpression:
            case SyntaxKind.NullKeyword:
                return PredicateSemantics.Never;
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.StringLiteral:
                if (node.Text() != "")
                {
                    return PredicateSemantics.Always;
                }
                return PredicateSemantics.Never;
            case SyntaxKind.ConditionalExpression:
                return GetSyntacticTruthySemantics(node.AsConditionalExpression().WhenTrue!) | GetSyntacticTruthySemantics(node.AsConditionalExpression().WhenFalse!);
            case SyntaxKind.Identifier:
                if (GetResolvedSymbolCore(node) == undefinedSymbol)
                {
                    return PredicateSemantics.Never;
                }
                break;
        }
        return PredicateSemantics.Sometimes;
    }

    // checker.go:12877-12896
    internal void CheckNullishCoalesceOperands(Node left, Node right)
    {
        if (NodeTests.IsBinaryExpression(left.Parent!.Parent!))
        {
            var grandparentLeft = left.Parent!.Parent!.AsBinaryExpression().Left!;
            var grandparentOperatorToken = left.Parent!.Parent!.AsBinaryExpression().OperatorToken!;
            if (NodeTests.IsBinaryExpression(grandparentLeft) && grandparentOperatorToken.Kind == SyntaxKind.BarBarToken)
            {
                GrammarErrorOnNode(grandparentLeft, Diag.X_0_and_1_operations_cannot_be_mixed_without_parentheses, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.QuestionQuestionToken), global::Ts.Scanner.Scanner.TokenToString(grandparentOperatorToken.Kind));
            }
        }
        else if (NodeTests.IsBinaryExpression(left))
        {
            var operatorToken = left.AsBinaryExpression().OperatorToken!;
            if (operatorToken.Kind == SyntaxKind.BarBarToken || operatorToken.Kind == SyntaxKind.AmpersandAmpersandToken)
            {
                GrammarErrorOnNode(left, Diag.X_0_and_1_operations_cannot_be_mixed_without_parentheses, global::Ts.Scanner.Scanner.TokenToString(operatorToken.Kind), global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.QuestionQuestionToken));
            }
        }
        else if (NodeTests.IsBinaryExpression(right))
        {
            var operatorToken = right.AsBinaryExpression().OperatorToken!;
            if (operatorToken.Kind == SyntaxKind.AmpersandAmpersandToken)
            {
                GrammarErrorOnNode(right, Diag.X_0_and_1_operations_cannot_be_mixed_without_parentheses, global::Ts.Scanner.Scanner.TokenToString(SyntaxKind.QuestionQuestionToken), global::Ts.Scanner.Scanner.TokenToString(operatorToken.Kind));
            }
        }
        CheckNullishCoalesceOperandLeft(left);
    }

    // checker.go:12898-12908
    internal void CheckNullishCoalesceOperandLeft(Node left)
    {
        var leftTarget = AstUtilities.SkipOuterExpressions(left, OuterExpressionKinds.All);
        var nullishSemantics = GetSyntacticNullishnessSemantics(leftTarget);
        if (nullishSemantics != PredicateSemantics.Sometimes)
        {
            if (nullishSemantics == PredicateSemantics.Always)
            {
                Error(leftTarget, Diag.This_expression_is_always_nullish);
            }
            else
            {
                Error(leftTarget, Diag.Right_operand_of_is_unreachable_because_the_left_operand_is_never_nullish);
            }
        }
    }

    // checker.go:12910-12959
    internal PredicateSemantics GetSyntacticNullishnessSemantics(Node node)
    {
        node = AstUtilities.SkipOuterExpressions(node, OuterExpressionKinds.All);
        switch (node.Kind)
        {
            case SyntaxKind.AwaitExpression:
            case SyntaxKind.CallExpression:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.MetaProperty:
            case SyntaxKind.NewExpression:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.YieldExpression:
            case SyntaxKind.ThisKeyword:
                return PredicateSemantics.Sometimes;
            case SyntaxKind.BinaryExpression:
                // List of operators that can produce null/undefined:
                // || ||= && &&=
                switch (node.AsBinaryExpression().OperatorToken!.Kind)
                {
                    case SyntaxKind.BarBarToken:
                    case SyntaxKind.BarBarEqualsToken:
                    case SyntaxKind.AmpersandAmpersandToken:
                    case SyntaxKind.AmpersandAmpersandEqualsToken:
                        return PredicateSemantics.Sometimes;
                    // For these operator kinds, the right operand is effectively controlling
                    case SyntaxKind.CommaToken:
                    case SyntaxKind.EqualsToken:
                    case SyntaxKind.QuestionQuestionToken:
                    case SyntaxKind.QuestionQuestionEqualsToken:
                        return GetSyntacticNullishnessSemantics(node.AsBinaryExpression().Right!);
                }
                return PredicateSemantics.Never;
            case SyntaxKind.ConditionalExpression:
                return GetSyntacticNullishnessSemantics(node.AsConditionalExpression().WhenTrue!) | GetSyntacticNullishnessSemantics(node.AsConditionalExpression().WhenFalse!);
            case SyntaxKind.NullKeyword:
                return PredicateSemantics.Always;
            case SyntaxKind.Identifier:
                if (GetResolvedSymbolCore(node) == undefinedSymbol)
                {
                    return PredicateSemantics.Always;
                }
                return PredicateSemantics.Sometimes;
        }
        return PredicateSemantics.Never;
    }

    // This is a *shallow* check: An expression is side-effect-free if the
    // evaluation of the expression *itself* cannot produce side effects.
    // checker.go:12961-12987
    internal bool IsSideEffectFree(Node node)
    {
        node = AstUtilities.SkipParentheses(node);
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.RegularExpressionLiteral:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.NullKeyword:
            case SyntaxKind.UndefinedKeyword:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.TypeOfExpression:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxElement:
                return true;
            case SyntaxKind.ConditionalExpression:
                return IsSideEffectFree(node.AsConditionalExpression().WhenTrue!) && IsSideEffectFree(node.AsConditionalExpression().WhenFalse!);
            case SyntaxKind.BinaryExpression:
                if (NodeTests.IsAssignmentOperator(node.AsBinaryExpression().OperatorToken!.Kind))
                {
                    return false;
                }
                return IsSideEffectFree(node.AsBinaryExpression().Left!) && IsSideEffectFree(node.AsBinaryExpression().Right!);
            case SyntaxKind.PrefixUnaryExpression:
                // Unary operators ~, !, +, and - have no side effects.
                // The rest do.
                switch (node.AsPrefixUnaryExpression().Operator)
                {
                    case SyntaxKind.ExclamationToken:
                    case SyntaxKind.PlusToken:
                    case SyntaxKind.MinusToken:
                    case SyntaxKind.TildeToken:
                        return true;
                }
                break;
        }
        return false;
    }

    // Return true for "indirect calls", (i.e. `(0, x.f)(...)` or `(0, eval)(...)`), which prevents passing `this`.
    // checker.go:12989-12995
    internal bool IsIndirectCall(Node node)
    {
        var left = node.AsBinaryExpression().Left!;
        var right = node.AsBinaryExpression().Right!;
        return NodeTests.IsParenthesizedExpression(node.Parent!) && NodeTests.IsNumericLiteral(left) && left.Text() == "0" &&
            ((NodeTests.IsCallExpression(node.Parent!.Parent!) && node.Parent!.Parent!.Expression() == node.Parent) ||
                NodeTests.IsTaggedTemplateExpression(node.Parent!.Parent!)) && (AstUtilities.IsAccessExpression(right) || (NodeTests.IsIdentifier(right) && right.Text() == "eval"));
    }

    // checker.go:12997-13025
    internal Type CheckInstanceOfExpression(Node left, Node right, Type leftType, Type rightType, CheckMode checkMode)
    {
        if (leftType == silentNeverType || rightType == silentNeverType)
        {
            return silentNeverType!;
        }
        // The instanceof operator requires the left operand to be of type Any, an object type, or a type parameter type,
        // and the right operand to be of type Any, a subtype of the 'Function' interface type, or have a call or construct signature.
        // The result is always of the Boolean primitive type.
        // NOTE: do not raise error if leftType is unknown as related error was already reported
        if (!IsTypeAny(leftType) && AllTypesAssignableToKind(leftType, TypeFlags.Primitive))
        {
            Error(left, Diag.The_left_hand_side_of_an_instanceof_expression_must_be_of_type_any_an_object_type_or_a_type_parameter);
        }
        var signature = GetResolvedSignature(left.Parent!, null /*candidatesOutArray*/, checkMode);
        if (signature == resolvingSignature)
        {
            // CheckMode.SkipGenericFunctions is enabled and this is a call to a generic function that
            // returns a function type. We defer checking and return silentNeverType.
            return silentNeverType!;
        }
        // If rightType has a `[Symbol.hasInstance]` method that is not `(value: unknown) => boolean`, we
        // must check the expression as if it were a call to `right[Symbol.hasInstance](left)`.
        var returnType = GetReturnTypeOfSignature(signature);
        // We also verify that the return type of the `[Symbol.hasInstance]` method is assignable to `boolean`.
        CheckTypeAssignableTo(returnType, booleanType!, right, Diag.An_object_s_Symbol_hasInstance_method_must_return_a_boolean_value_for_it_to_be_used_on_the_right_hand_side_of_an_instanceof_expression);
        return booleanType!;
    }

    // checker.go:13027-13059
    internal Type CheckInExpression(Node left, Node right, Type leftType, Type rightType)
    {
        if (leftType == silentNeverType || rightType == silentNeverType)
        {
            return silentNeverType!;
        }
        if (NodeTests.IsPrivateIdentifier(left))
        {
            if (languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.PrivateNamesAndClassStaticBlocks ||
                languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.ClassAndClassElementDecorators ||
                !compilerOptions.GetUseDefineForClassFields())
            {
                CheckExternalEmitHelpers(left, ExternalEmitHelpers.ClassPrivateFieldIn);
            }
            // Unlike in 'checkPrivateIdentifierExpression' we now have access to the RHS type
            // which provides us with the opportunity to emit more detailed errors
            if (symbolNodeLinks.Get(left).resolvedSymbol == null && AstUtilities.GetContainingClass(left) != null)
            {
                var isUncheckedJS = IsUncheckedJSSuggestion(left, rightType.symbol, true /*excludeClasses*/);
                ReportNonexistentProperty(left, rightType, isUncheckedJS);
            }
        }
        else
        {
            // The type of the left operand must be assignable to string, number, or symbol.
            CheckTypeAssignableTo(CheckNonNullType(leftType, left), stringNumberSymbolType!, left, null);
        }
        // The type of the right operand must be assignable to 'object'.
        if (CheckTypeAssignableTo(CheckNonNullType(rightType, right), nonPrimitiveType!, right, null))
        {
            // The {} type is assignable to the object type, yet {} might represent a primitive type.
            if (HasEmptyObjectIntersection(rightType))
            {
                Error(right, Diag.Type_0_may_represent_a_primitive_value_which_is_not_permitted_as_the_right_operand_of_the_in_operator, TypeToString(rightType));
            }
        }
        // The result is always of the Boolean primitive type.
        return booleanType!;
    }

    // checker.go:13061-13065
    internal bool HasEmptyObjectIntersection(Type t)
    {
        return SomeType(t, tt => tt == unknownEmptyObjectType || ((tt.flags & TypeFlags.Intersection) != 0 && IsEmptyAnonymousObjectType(GetBaseConstraintOrType(tt))));
    }

    // checker.go:13076-13078
    internal bool IsExactOptionalPropertyMismatch(Type? source, Type? target)
    {
        return source != null && target != null && MaybeTypeOfKind(source, TypeFlags.Undefined) && ContainsMissingType(target);
    }

    // checker.go:13080-13092
    internal bool CheckReferenceExpression(Node expr, global::Ts.Diagnostics.Message invalidReferenceMessage, global::Ts.Diagnostics.Message invalidOptionalChainMessage)
    {
        // References are combinations of identifiers, parentheses, and property accesses.
        var node = AstUtilities.SkipOuterExpressions(expr, OuterExpressionKinds.Assertions | OuterExpressionKinds.Parentheses);
        if (node.Kind != SyntaxKind.Identifier && !AstUtilities.IsAccessExpression(node))
        {
            Error(expr, invalidReferenceMessage);
            return false;
        }
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            Error(expr, invalidOptionalChainMessage);
            return false;
        }
        return true;
    }

    // checker.go:13814-13825
    internal Symbol GetResolvedSymbolCore(Node node)
    {
        var links = symbolNodeLinks.Get(node);
        if (links.resolvedSymbol == null)
        {
            Symbol? symbol = null;
            if (!AstUtilities.NodeIsMissing(node))
            {
                symbol = resolveName(node, node.Text(), SymbolFlags.Value | SymbolFlags.ExportValue,
                    GetCannotFindNameDiagnosticForName(node), !AstUtilities.IsWriteOnlyAccess(node), false /*excludeGlobals*/);
            }
            links.resolvedSymbol = CoreUtil.OrElse(symbol, unknownSymbol);
        }
        return links.resolvedSymbol!;
    }

    // checker.go:13827-13829
    internal Symbol? GetResolvedSymbolOrNil(Node node)
    {
        return symbolNodeLinks.Get(node).resolvedSymbol;
    }

    // checker.go:13831-13837
    internal Symbol? GetReferencedValueOrAliasSymbol(Node reference)
    {
        var resolvedSymbol = symbolNodeLinks.Get(reference).resolvedSymbol;
        if (resolvedSymbol != null && resolvedSymbol != unknownSymbol)
        {
            return resolvedSymbol;
        }
        return resolveName(reference, reference.Text(), SymbolFlags.Value | SymbolFlags.ExportValue | SymbolFlags.Alias, null, true /*isUse*/, false /*excludeGlobals*/);
    }

    // checker.go:13839-13874
    internal global::Ts.Diagnostics.Message GetCannotFindNameDiagnosticForName(Node node)
    {
        switch (node.Text())
        {
            case "document":
            case "console":
                return Diag.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_include_dom;
            case "$":
                return CoreUtil.IfElse(compilerOptions.UsesWildcardTypes(),
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery,
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_jQuery_Try_npm_i_save_dev_types_Slashjquery_and_then_add_jquery_to_the_types_field_in_your_tsconfig);
            case "beforeEach":
            case "describe":
            case "suite":
            case "it":
            case "test":
                return CoreUtil.IfElse(compilerOptions.UsesWildcardTypes(),
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha,
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_a_test_runner_Try_npm_i_save_dev_types_Slashjest_or_npm_i_save_dev_types_Slashmocha_and_then_add_jest_or_mocha_to_the_types_field_in_your_tsconfig);
            case "process":
            case "require":
            case "Buffer":
            case "module":
            case "NodeJS":
                return CoreUtil.IfElse(compilerOptions.UsesWildcardTypes(),
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode,
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_node_Try_npm_i_save_dev_types_Slashnode_and_then_add_node_to_the_types_field_in_your_tsconfig);
            case "Bun":
                return CoreUtil.IfElse(compilerOptions.UsesWildcardTypes(),
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_Bun_Try_npm_i_save_dev_types_Slashbun,
                    Diag.Cannot_find_name_0_Do_you_need_to_install_type_definitions_for_Bun_Try_npm_i_save_dev_types_Slashbun_and_then_add_bun_to_the_types_field_in_your_tsconfig);
            case "Map":
            case "Set":
            case "Promise":
            case "ast.Symbol":
            case "WeakMap":
            case "WeakSet":
            case "Iterator":
            case "AsyncIterator":
            case "SharedArrayBuffer":
            case "Atomics":
            case "AsyncIterable":
            case "AsyncIterableIterator":
            case "AsyncGenerator":
            case "AsyncGeneratorFunction":
            case "BigInt":
            case "Reflect":
            case "BigInt64Array":
            case "BigUint64Array":
                return Diag.Cannot_find_name_0_Do_you_need_to_change_your_target_library_Try_changing_the_lib_compiler_option_to_1_or_later;
            case "await":
                if (NodeTests.IsCallExpression(node.Parent!))
                {
                    return Diag.Cannot_find_name_0_Did_you_mean_to_write_this_in_an_async_function;
                }
                goto default;
            default:
                if (node.Parent!.Kind == SyntaxKind.ShorthandPropertyAssignment)
                {
                    return Diag.No_value_exists_in_scope_for_the_shorthand_property_0_Either_declare_one_or_provide_an_initializer;
                }
                return Diag.Cannot_find_name_0;
        }
    }
}
