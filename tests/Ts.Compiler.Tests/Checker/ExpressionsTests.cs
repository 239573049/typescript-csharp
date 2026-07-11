// Smoke tests for the phase-5 checker expression-core wave (plan/03 §4.2 #7/#8: Checker.Expressions.cs
// dispatch trunk + Checker.OperatorExpressions.cs binary engine / operator support).
//
// The deep expression engine (checkIdentifier / property-access / call-overload resolution / object &
// array literals / templates / JSX / contextual typing) routes through cross-cluster stubs
// (Checker.Expressions.Deps.cs — checkGrammar*, checkObjectLiteral, contextual typing, awaited types, …)
// that throw NotImplementedException until their owning waves land. A bare `1 + 2` therefore reaches
// checkGrammarNumericLiteral (a grammar-wave stub) before any arithmetic runs. So this file exercises
// the wave's PURE, stub-free surface over REAL parsed source:
//   - GetSuggestedBooleanOperator: the token→token bitwise-suggestion map (checker.go:12749).
//   - GetSyntacticTruthySemantics / GetSyntacticNullishnessSemantics: the syntactic predicate walkers
//     over literal nodes (checker.go:12848 / 12910) — no symbol resolution or type computation.
//   - IsSideEffectFree: the shallow syntactic side-effect predicate (checker.go:12961).
//   - PredicateSemantics: the flag algebra (Sometimes == Always | Never).
// These are the leaf building blocks the binary engine dispatches into; verifying them faithfully to
// Go is the reachable smoke for this wave. (Construction/interning coverage lives in CheckerSmokeTests;
// do not modify that file.)

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Checker.Tests;

public class ExpressionsTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile ParseAndBind(string text)
    {
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(sf);
        return sf;
    }

    private static Node? FindFirst(Node node, SyntaxKind kind)
    {
        Node? found = null;
        void Visit(Node n)
        {
            if (found is not null)
            {
                return;
            }
            if (n.Kind == kind)
            {
                found = n;
                return;
            }
            n.ForEachChild(child =>
            {
                Visit(child);
                return found is not null;
            });
        }
        Visit(node);
        return found;
    }

    // ===== getSuggestedBooleanOperator (checker.go:12749) =====

    [Fact]
    public void GetSuggestedBooleanOperator_MapsBitwiseTokensToLogicalTokens()
    {
        var c = NewChecker();
        Assert.Equal(SyntaxKind.BarBarToken, c.GetSuggestedBooleanOperator(SyntaxKind.BarToken));
        Assert.Equal(SyntaxKind.BarBarToken, c.GetSuggestedBooleanOperator(SyntaxKind.BarEqualsToken));
        Assert.Equal(SyntaxKind.ExclamationEqualsEqualsToken, c.GetSuggestedBooleanOperator(SyntaxKind.CaretToken));
        Assert.Equal(SyntaxKind.ExclamationEqualsEqualsToken, c.GetSuggestedBooleanOperator(SyntaxKind.CaretEqualsToken));
        Assert.Equal(SyntaxKind.AmpersandAmpersandToken, c.GetSuggestedBooleanOperator(SyntaxKind.AmpersandToken));
        Assert.Equal(SyntaxKind.AmpersandAmpersandToken, c.GetSuggestedBooleanOperator(SyntaxKind.AmpersandEqualsToken));
        // Non-bitwise operators have no suggestion.
        Assert.Equal(SyntaxKind.Unknown, c.GetSuggestedBooleanOperator(SyntaxKind.PlusToken));
    }

    // ===== PredicateSemantics flag algebra (checker.go:12839) =====

    [Fact]
    public void PredicateSemantics_SometimesIsAlwaysOrNever()
    {
        Assert.Equal(PredicateSemantics.Always | PredicateSemantics.Never, PredicateSemantics.Sometimes);
        Assert.NotEqual(PredicateSemantics.Always, PredicateSemantics.Sometimes);
        Assert.NotEqual(PredicateSemantics.Never, PredicateSemantics.Sometimes);
    }

    // ===== getSyntacticTruthySemantics (checker.go:12848) =====

    [Fact]
    public void GetSyntacticTruthySemantics_ZeroAndOne_AreSometimes()
    {
        var c = NewChecker();
        var zero = FindFirst(ParseAndBind("const a = 0;"), SyntaxKind.NumericLiteral)!;
        var one = FindFirst(ParseAndBind("const a = 1;"), SyntaxKind.NumericLiteral)!;
        // `while(0)` / `while(1)` are intentionally allowed — Sometimes.
        Assert.Equal(PredicateSemantics.Sometimes, c.GetSyntacticTruthySemantics(zero));
        Assert.Equal(PredicateSemantics.Sometimes, c.GetSyntacticTruthySemantics(one));
    }

    [Fact]
    public void GetSyntacticTruthySemantics_OtherNumericLiteral_IsAlways()
    {
        var c = NewChecker();
        var two = FindFirst(ParseAndBind("const a = 2;"), SyntaxKind.NumericLiteral)!;
        Assert.Equal(PredicateSemantics.Always, c.GetSyntacticTruthySemantics(two));
    }

    [Fact]
    public void GetSyntacticTruthySemantics_StringLiterals_TrackEmptiness()
    {
        var c = NewChecker();
        var nonEmpty = FindFirst(ParseAndBind("const a = \"x\";"), SyntaxKind.StringLiteral)!;
        var empty = FindFirst(ParseAndBind("const a = \"\";"), SyntaxKind.StringLiteral)!;
        Assert.Equal(PredicateSemantics.Always, c.GetSyntacticTruthySemantics(nonEmpty));
        Assert.Equal(PredicateSemantics.Never, c.GetSyntacticTruthySemantics(empty));
    }

    [Fact]
    public void GetSyntacticTruthySemantics_ObjectAndArrayLiterals_AreAlways()
    {
        var c = NewChecker();
        var obj = FindFirst(ParseAndBind("const a = {};"), SyntaxKind.ObjectLiteralExpression)!;
        var arr = FindFirst(ParseAndBind("const a = [];"), SyntaxKind.ArrayLiteralExpression)!;
        Assert.Equal(PredicateSemantics.Always, c.GetSyntacticTruthySemantics(obj));
        Assert.Equal(PredicateSemantics.Always, c.GetSyntacticTruthySemantics(arr));
    }

    [Fact]
    public void GetSyntacticTruthySemantics_NullKeyword_IsNever()
    {
        var c = NewChecker();
        var nullLit = FindFirst(ParseAndBind("const a = null;"), SyntaxKind.NullKeyword)!;
        Assert.Equal(PredicateSemantics.Never, c.GetSyntacticTruthySemantics(nullLit));
    }

    // ===== getSyntacticNullishnessSemantics (checker.go:12910) =====

    [Fact]
    public void GetSyntacticNullishnessSemantics_NullKeyword_IsAlways()
    {
        var c = NewChecker();
        var nullLit = FindFirst(ParseAndBind("const a = null;"), SyntaxKind.NullKeyword)!;
        Assert.Equal(PredicateSemantics.Always, c.GetSyntacticNullishnessSemantics(nullLit));
    }

    [Fact]
    public void GetSyntacticNullishnessSemantics_NumericLiteral_IsNever()
    {
        var c = NewChecker();
        // A plain numeric literal can never be null/undefined.
        var num = FindFirst(ParseAndBind("const a = 5;"), SyntaxKind.NumericLiteral)!;
        Assert.Equal(PredicateSemantics.Never, c.GetSyntacticNullishnessSemantics(num));
    }

    // ===== isSideEffectFree (checker.go:12961) =====

    [Fact]
    public void IsSideEffectFree_LiteralsAndPureUnary_AreFree()
    {
        var c = NewChecker();
        var str = FindFirst(ParseAndBind("const a = \"x\";"), SyntaxKind.StringLiteral)!;
        Assert.True(c.IsSideEffectFree(str));

        // Prefix `!` has no side effects (operand is a pure literal).
        var not = FindFirst(ParseAndBind("const a = !1;"), SyntaxKind.PrefixUnaryExpression)!;
        Assert.True(c.IsSideEffectFree(not));
    }

    [Fact]
    public void IsSideEffectFree_Assignment_IsNotFree()
    {
        var c = NewChecker();
        // `x = 1` (an assignment BinaryExpression) has a side effect.
        var assign = FindFirst(ParseAndBind("let x = 0; x = 1;"), SyntaxKind.BinaryExpression)!;
        Assert.False(c.IsSideEffectFree(assign));
    }
}
