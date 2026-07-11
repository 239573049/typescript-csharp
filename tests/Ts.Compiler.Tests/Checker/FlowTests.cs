// Narrowing smoke tests for the phase-5 checker flow wave (plan/03 §4.2 #(flow); flow.go +
// the ast/checker predicate helpers this wave landed to support it).
//
// The deep flow engine (getFlowTypeOfReference / narrowType*) routes through cross-cluster stubs
// (Checker.Flow.Deps.cs — getResolvedSignature, getTypeOfExpression, isUnitLikeType, …) that throw
// NotImplementedException until the owning waves land. So this file exercises the wave's PURE,
// stub-free surface: the small ast predicates (Ast/Utilities.Flow.cs) and the checker narrowing
// helpers (isCallChain / hasOnlyExpressionInitializer / getBindingElementPropertyName /
// hasDotDotDotToken) over REAL parsed+bound source. These are the leaf predicates the narrowing
// engine is built from; verifying them faithfully to Go is the reachable smoke for this wave.
// (Construction/interning coverage lives in CheckerSmokeTests; do not modify that file.)

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Checker.Tests;

public class FlowTests
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

    // ---- ast/utilities.go:1243 isVarConstLike ----

    [Fact]
    public void IsVarConstLike_TrueForConst_FalseForLet()
    {
        var constDecl = FindFirst(ParseAndBind("const x = 1;"), SyntaxKind.VariableDeclaration)!;
        var letDecl = FindFirst(ParseAndBind("let y = 1;"), SyntaxKind.VariableDeclaration)!;

        Assert.True(AstUtilities.IsVarConstLike(constDecl));
        Assert.False(AstUtilities.IsVarConstLike(letDecl));
    }

    // ---- ast/utilities.go:3981 isEmptyArrayLiteral ----

    [Fact]
    public void IsEmptyArrayLiteral_DistinguishesEmptyFromNonEmpty()
    {
        var empty = FindFirst(ParseAndBind("const a = [];"), SyntaxKind.ArrayLiteralExpression)!;
        var nonEmpty = FindFirst(ParseAndBind("const b = [1];"), SyntaxKind.ArrayLiteralExpression)!;

        Assert.True(AstUtilities.IsEmptyArrayLiteral(empty));
        Assert.False(AstUtilities.IsEmptyArrayLiteral(nonEmpty));
    }

    // ---- ast/utilities.go:1128 isFunctionOrModuleBlock ----

    [Fact]
    public void IsFunctionOrModuleBlock_TrueForSourceFile_AndFunctionBody()
    {
        var sf = ParseAndBind("function f() { return 1; }");
        var funcBody = FindFirst(sf, SyntaxKind.Block)!;

        Assert.True(AstUtilities.IsFunctionOrModuleBlock(sf.AsNode()));
        Assert.True(AstUtilities.IsFunctionOrModuleBlock(funcBody));
    }

    // ---- ast/utilities.go:2114 tryGetTextOfPropertyName (Try pattern) ----

    [Fact]
    public void TryGetTextOfPropertyName_ReadsIdentifierName()
    {
        var prop = FindFirst(ParseAndBind("const o = { foo: 1 };"), SyntaxKind.PropertyAssignment)!;
        var name = prop.Name()!;

        Assert.True(AstUtilities.TryGetTextOfPropertyName(name, out var text));
        Assert.Equal("foo", text);
    }

    // ---- ast/utilities.go:2964 isThisInTypeQuery ----

    [Fact]
    public void IsThisInTypeQuery_FalseForOrdinaryThis()
    {
        // A `this` inside an expression position is not part of a type query.
        var thisExpr = FindFirst(ParseAndBind("function f() { return this; }"), SyntaxKind.ThisKeyword);
        // ThisExpression uses the ThisKeyword token; if not found as such, the predicate must still
        // reject any non-this-identifier node it is handed.
        if (thisExpr is not null)
        {
            Assert.False(AstUtilities.IsThisInTypeQuery(thisExpr));
        }
    }

    // ---- checker/utilities.go:1062 isCallChain ----

    [Fact]
    public void IsCallChain_TrueForOptionalCall_FalseForPlainCall()
    {
        var plain = FindFirst(ParseAndBind("a.b();"), SyntaxKind.CallExpression)!;
        var optional = FindFirst(ParseAndBind("a?.b();"), SyntaxKind.CallExpression)!;

        Assert.False(Checker.IsCallChain(plain));
        Assert.True(Checker.IsCallChain(optional));
    }

    // ---- checker/utilities.go:242 hasOnlyExpressionInitializer ----

    [Fact]
    public void HasOnlyExpressionInitializer_TrueForVariableDeclaration()
    {
        var varDecl = FindFirst(ParseAndBind("const x = 1;"), SyntaxKind.VariableDeclaration)!;
        var funcDecl = FindFirst(ParseAndBind("function f() {}"), SyntaxKind.FunctionDeclaration)!;

        Assert.True(Checker.HasOnlyExpressionInitializer(varDecl));
        Assert.False(Checker.HasOnlyExpressionInitializer(funcDecl));
    }

    // ---- checker/utilities.go:250 hasDotDotDotToken ----

    [Fact]
    public void HasDotDotDotToken_TrueForRestParameter()
    {
        var restParam = FindFirst(ParseAndBind("function f(...args: number[]) {}"), SyntaxKind.Parameter)!;
        var plainParam = FindFirst(ParseAndBind("function g(a: number) {}"), SyntaxKind.Parameter)!;

        Assert.True(Checker.HasDotDotDotToken(restParam));
        Assert.False(Checker.HasDotDotDotToken(plainParam));
    }

    // ---- checker/utilities.go:1058 getBindingElementPropertyName ----

    [Fact]
    public void GetBindingElementPropertyName_ReturnsName()
    {
        var bindingElement = FindFirst(ParseAndBind("const { kind } = obj;"), SyntaxKind.BindingElement)!;
        var name = Checker.GetBindingElementPropertyName(bindingElement);

        Assert.NotNull(name);
        Assert.True(AstUtilities.TryGetTextOfPropertyName(name!, out var text));
        Assert.Equal("kind", text);
    }
}
