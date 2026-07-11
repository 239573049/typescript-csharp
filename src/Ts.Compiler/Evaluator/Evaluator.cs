// Ported from tsgo internal/evaluator/evaluator.go — constant-expression evaluator for enum
// members and literal types. `Result` and the `Evaluator` delegate are load-bearing for the
// checker (EnumMemberLinks.value is Result; Checker.evaluate is Evaluator). Depends only on
// ast + jsnum + core.

using System.Text;
using Ts.Ast;
using Ts.JsNum;

namespace Ts.Evaluator;

// evaluator.go:11-19 — Value is `any` (object?): string, jsnum.Number, bool, or PseudoBigInt.
public readonly record struct Result(object? Value, bool IsSyntacticallyString, bool ResolvedOtherFiles, bool HasExternalReferences)
{
    // evaluator.go:17-19
    public static Result NewResult(object? value, bool isSyntacticallyString, bool resolvedOtherFiles, bool hasExternalReferences) =>
        new(value, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
}

// evaluator.go:21
public delegate Result Evaluator(Node expr, Node? location);

// Package-level factory (Go: func NewEvaluator). Named distinctly from the delegate.
public static class EvaluatorFactory
{
    // evaluator.go:23 — returns the recursive evaluate closure.
    public static Evaluator NewEvaluator(Evaluator evaluateEntity, OuterExpressionKinds outerExpressionsToSkip)
    {
        var impl = new Impl(evaluateEntity, outerExpressionsToSkip);
        return impl.Evaluate;
    }

    private sealed class Impl(Evaluator evaluateEntity, OuterExpressionKinds outerExpressionsToSkip)
    {
        public Result Evaluate(Node exprIn, Node? location)
        {
            bool isSyntacticallyString = false;
            bool resolvedOtherFiles = false;
            bool hasExternalReferences = false;

            // Type assertions intentionally break evaluation when evaluating literal types; the
            // conservative skip set matches Go (SatisfiesExpression/non-null nuance in comment).
            Node expr = AstUtilities.SkipOuterExpressions(exprIn, outerExpressionsToSkip | OuterExpressionKinds.Parentheses);
            switch (expr.Kind)
            {
                case SyntaxKind.PrefixUnaryExpression:
                {
                    Result result = Evaluate(expr.AsPrefixUnaryExpression().Operand!, location);
                    resolvedOtherFiles = result.ResolvedOtherFiles;
                    hasExternalReferences = result.HasExternalReferences;
                    if (result.Value is Number value)
                    {
                        switch (expr.AsPrefixUnaryExpression().Operator)
                        {
                            case SyntaxKind.PlusToken:
                                return new Result(value, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.MinusToken:
                                return new Result(-value, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.TildeToken:
                                return new Result(value.BitwiseNOT(), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                        }
                    }
                    break;
                }
                case SyntaxKind.BinaryExpression:
                {
                    Result left = Evaluate(expr.AsBinaryExpression().Left!, location);
                    Result right = Evaluate(expr.AsBinaryExpression().Right!, location);
                    SyntaxKind op = expr.AsBinaryExpression().OperatorToken!.Kind;
                    isSyntacticallyString = (left.IsSyntacticallyString || right.IsSyntacticallyString) && op == SyntaxKind.PlusToken;
                    resolvedOtherFiles = left.ResolvedOtherFiles || right.ResolvedOtherFiles;
                    hasExternalReferences = left.HasExternalReferences || right.HasExternalReferences;
                    bool leftIsNum = left.Value is Number;
                    bool rightIsNum = right.Value is Number;
                    if (leftIsNum && rightIsNum)
                    {
                        var leftNum = (Number)left.Value!;
                        var rightNum = (Number)right.Value!;
                        switch (op)
                        {
                            case SyntaxKind.BarToken:
                                return new Result(leftNum.BitwiseOR(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.AmpersandToken:
                                return new Result(leftNum.BitwiseAND(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.GreaterThanGreaterThanToken:
                                return new Result(leftNum.SignedRightShift(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
                                return new Result(leftNum.UnsignedRightShift(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.LessThanLessThanToken:
                                return new Result(leftNum.LeftShift(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.CaretToken:
                                return new Result(leftNum.BitwiseXOR(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.AsteriskToken:
                                return new Result(leftNum * rightNum, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.SlashToken:
                                return new Result(leftNum / rightNum, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.PlusToken:
                                return new Result(leftNum + rightNum, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.MinusToken:
                                return new Result(leftNum - rightNum, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.PercentToken:
                                return new Result(leftNum.Remainder(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                            case SyntaxKind.AsteriskAsteriskToken:
                                return new Result(leftNum.Exponentiate(rightNum), isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                        }
                    }
                    bool leftIsStr = left.Value is string;
                    bool rightIsStr = right.Value is string;
                    if ((leftIsStr || leftIsNum) && (rightIsStr || rightIsNum) && op == SyntaxKind.PlusToken)
                    {
                        string leftStr = leftIsNum ? ((Number)left.Value!).ToString() : (string)left.Value!;
                        string rightStr = rightIsNum ? ((Number)right.Value!).ToString() : (string)right.Value!;
                        return new Result(leftStr + rightStr, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
                    }
                    break;
                }
                case SyntaxKind.StringLiteral:
                case SyntaxKind.NoSubstitutionTemplateLiteral:
                    return new Result(expr.Text(), true /*isSyntacticallyString*/, false, false);
                case SyntaxKind.TemplateExpression:
                    return EvaluateTemplateExpression(expr, location);
                case SyntaxKind.NumericLiteral:
                    return new Result(global::Ts.JsNum.JsNum.FromString(expr.Text()), false, false, false);
                case SyntaxKind.Identifier:
                    return evaluateEntity(expr, location);
                case SyntaxKind.ElementAccessExpression:
                case SyntaxKind.PropertyAccessExpression:
                    if (AstUtilities.IsEntityNameExpression(expr.Expression()!))
                    {
                        return evaluateEntity(expr, location);
                    }
                    break;
            }
            return new Result(null, isSyntacticallyString, resolvedOtherFiles, hasExternalReferences);
        }

        private Result EvaluateTemplateExpression(Node expr, Node? location)
        {
            var sb = new StringBuilder();
            sb.Append(expr.AsTemplateExpression().Head!.Text());
            bool resolvedOtherFiles = false;
            bool hasExternalReferences = false;
            foreach (Node span in expr.AsTemplateExpression().TemplateSpans!.Nodes)
            {
                Result spanResult = Evaluate(span.Expression()!, location);
                if (spanResult.Value == null)
                {
                    return new Result(null, true /*isSyntacticallyString*/, false, false);
                }
                sb.Append(AnyToString(spanResult.Value));
                sb.Append(span.AsTemplateSpan().Literal!.Text());
                resolvedOtherFiles = resolvedOtherFiles || spanResult.ResolvedOtherFiles;
                hasExternalReferences = hasExternalReferences || spanResult.HasExternalReferences;
            }
            return new Result(sb.ToString(), true, resolvedOtherFiles, hasExternalReferences);
        }
    }

    // evaluator.go:157
    public static string AnyToString(object? v) => v switch
    {
        string s => s,
        Number n => n.ToString(),
        bool b => b ? "true" : "false",
        PseudoBigInt pbi => pbi.ToString(),
        _ => throw new System.InvalidOperationException("Unhandled case in AnyToString"),
    };

    // evaluator.go:170
    public static bool IsTruthy(object? v) => v switch
    {
        string s => s.Length != 0,
        Number n => n != (Number)0 && !n.IsNaN(),
        bool b => b,
        PseudoBigInt pbi => pbi != default,
        _ => throw new System.InvalidOperationException("Unhandled case in IsTruthy"),
    };
}
