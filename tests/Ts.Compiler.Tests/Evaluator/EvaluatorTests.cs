// Tests for Ts.Evaluator — constant-expression folding (evaluator.go).
using Ts.Ast;
using Ts.Core;
using Ts.Evaluator;
using Ts.JsNum;
using Xunit;

namespace Ts.Evaluator.Tests;

public class EvaluatorTests
{
    // Parse `<expr>;` and return the expression node.
    private static Node ParseExpr(string exprText)
    {
        var opts = new SourceFileParseOptions { FileName = "/t.ts", Path = new Ts.TsPath.Path("/t.ts") };
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(opts, exprText + ";", ScriptKind.TS);
        Node stmt = sf.Statements!.Nodes[0];
        return stmt.Expression()!;
    }

    // An evaluateEntity that resolves nothing (identifiers → null value).
    private static readonly Evaluator NoEntities = (_, _) => new Result(null, false, false, false);

    private static object? Eval(string exprText)
    {
        Evaluator ev = EvaluatorFactory.NewEvaluator(NoEntities, OuterExpressionKinds.All);
        return ev(ParseExpr(exprText), null).Value;
    }

    [Theory]
    [InlineData("1 + 2 * 3", 7.0)]        // precedence via parsed AST
    [InlineData("(1 + 2) * 3", 9.0)]
    [InlineData("10 - 4 - 2", 4.0)]        // left assoc
    [InlineData("2 ** 3 ** 2", 512.0)]     // right assoc
    [InlineData("7 % 3", 1.0)]
    [InlineData("1 << 4", 16.0)]
    [InlineData("0xff & 0x0f", 15.0)]
    [InlineData("5 | 2", 7.0)]
    [InlineData("-5", -5.0)]
    [InlineData("~0", -1.0)]
    [InlineData("+42", 42.0)]
    public void FoldsNumericConstants(string expr, double expected)
    {
        object? v = Eval(expr);
        Assert.IsType<Number>(v);
        Assert.Equal(expected, ((Number)v!).Value);
    }

    [Theory]
    [InlineData("\"a\" + \"b\"", "ab")]
    [InlineData("\"x\" + 1", "x1")]          // string + number → string
    [InlineData("1 + \"y\"", "1y")]
    public void FoldsStringConcat(string expr, string expected)
    {
        Assert.Equal(expected, Eval(expr));
    }

    [Fact]
    public void StringLiteralIsSyntacticallyString()
    {
        Evaluator ev = EvaluatorFactory.NewEvaluator(NoEntities, OuterExpressionKinds.All);
        Result r = ev(ParseExpr("\"hello\""), null);
        Assert.Equal("hello", r.Value);
        Assert.True(r.IsSyntacticallyString);
    }

    [Fact]
    public void UnresolvableIdentifierYieldsNull()
    {
        Assert.Null(Eval("someUnknownConst"));
    }

    [Fact]
    public void AnyToStringAndIsTruthy()
    {
        Assert.Equal("true", EvaluatorFactory.AnyToString(true));
        Assert.Equal("5", EvaluatorFactory.AnyToString((Number)5));
        Assert.True(EvaluatorFactory.IsTruthy("x"));
        Assert.False(EvaluatorFactory.IsTruthy(""));
        Assert.False(EvaluatorFactory.IsTruthy((Number)0));
        Assert.True(EvaluatorFactory.IsTruthy((Number)1));
    }
}
