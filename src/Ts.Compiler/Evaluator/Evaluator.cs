// Ported from tsgo internal/evaluator/evaluator.go — MINIMAL-BUILD for the checker skeleton.
// The `Result` value type and the `Evaluator` delegate are load-bearing (EnumMemberLinks.value
// is evaluator.Result; Checker.evaluate is evaluator.Evaluator). NewEvaluator's constant-folding
// body is deferred to a later wave (it needs jsnum.Number bitwise ops + template folding); the
// returned delegate throws if actually invoked (only reached from enum member checking, stubbed).

using Ts.Ast;

namespace Ts.Evaluator;

// evaluator.go:11-19
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
    // evaluator.go:23 — full constant-evaluation body is a later wave.
    public static Evaluator NewEvaluator(Evaluator evaluateEntity, OuterExpressionKinds outerExpressionsToSkip) =>
        (expr, location) => throw new NotImplementedException("evaluator.NewEvaluator");
}
