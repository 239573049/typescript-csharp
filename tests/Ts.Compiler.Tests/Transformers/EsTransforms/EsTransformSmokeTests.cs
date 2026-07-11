using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Ts.Transformers.EsTransforms;
using Ts.TsPath;
using Xunit;

namespace Ts.Transformers.EsTransforms.Tests;

public sealed class EsTransformSmokeTests
{
    private static SourceFile Parse(string text)
    {
        var file = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions { FileName = "/test.ts", Path = new Path("/test.ts") },
            text,
            ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(file);
        return file;
    }

    private static SourceFile Transform(string text, ScriptTarget target)
    {
        var options = new CompilerOptions { Target = target };
        var transformer = EsTransformers.GetESTransformer(new TransformOptions
        {
            Context = EmitContext.NewEmitContext(),
            CompilerOptions = options,
            GetEmitModuleFormatOfFile = _ => ModuleKind.CommonJS,
        });
        return transformer.TransformSourceFile(Parse(text));
    }

    private static Node FirstVariableInitializer(SourceFile sourceFile)
    {
        foreach (var statement in sourceFile.Statements!.Nodes)
        {
            if (!NodeTests.IsVariableStatement(statement))
            {
                continue;
            }
            foreach (var declaration in statement.AsVariableStatement().DeclarationList!
                .AsVariableDeclarationList().Declarations!.Nodes)
            {
                if (declaration.Initializer() is { } initializer)
                {
                    return initializer;
                }
            }
        }
        throw new System.InvalidOperationException("Expected an initialized variable declaration");
    }

    [Fact]
    public void Exponentiation_LowersToMathPowCall()
    {
        var transformed = Transform("const value = left ** right;", ScriptTarget.ES2015);
        var call = FirstVariableInitializer(transformed).AsCallExpression();

        Assert.Equal(SyntaxKind.PropertyAccessExpression, call.Expression!.Kind);
        Assert.Equal("Math", call.Expression.Expression()!.Text());
        Assert.Equal("pow", call.Expression.Name()!.Text());
        Assert.Equal(2, call.Arguments!.Nodes.Length);
    }

    [Fact]
    public void NullishCoalescing_LowersToConditionalExpression()
    {
        var transformed = Transform("const value = getValue() ?? fallback;", ScriptTarget.ES2019);
        var conditional = FirstVariableInitializer(transformed).AsConditionalExpression();

        Assert.Equal(SyntaxKind.BinaryExpression, conditional.Condition!.Kind);
        Assert.Equal(SyntaxKind.Identifier, conditional.WhenTrue!.Kind);
        Assert.Equal("fallback", conditional.WhenFalse!.Text());
    }

    [Fact]
    public void LogicalAssignment_LowersToShortCircuitAndAssignment()
    {
        var transformed = Transform("value ||= fallback;", ScriptTarget.ES2020);
        var binary = transformed.Statements!.Nodes[0].Expression()!.AsBinaryExpression();

        Assert.Equal(SyntaxKind.BarBarToken, binary.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.ParenthesizedExpression, binary.Right!.Kind);
        Assert.Equal(SyntaxKind.EqualsToken, binary.Right.Expression()!.AsBinaryExpression().OperatorToken!.Kind);
    }

    [Fact]
    public void OptionalCatch_InsertsGeneratedCatchVariable()
    {
        var transformed = Transform("try { work(); } catch { recover(); }", ScriptTarget.ES2018);
        var tryStatement = transformed.Statements!.Nodes[0].AsTryStatement();

        Assert.NotNull(tryStatement.CatchClause);
        Assert.NotNull(tryStatement.CatchClause!.AsCatchClause().VariableDeclaration);
        Assert.Equal(
            SyntaxKind.Identifier,
            tryStatement.CatchClause.AsCatchClause().VariableDeclaration!.Name()!.Kind);
    }

    [Fact]
    public void ObjectSpread_LowersToAssignHelperCall()
    {
        var transformed = Transform("const value = { a: 1, ...other, b: 2 };", ScriptTarget.ES2017);
        var call = FirstVariableInitializer(transformed).AsCallExpression();

        Assert.Equal(SyntaxKind.PropertyAccessExpression, call.Expression!.Kind);
        Assert.Equal("Object", call.Expression.Expression()!.Text());
        Assert.Equal("assign", call.Expression.Name()!.Text());
        Assert.Equal(2, call.Arguments!.Nodes.Length);
    }

    [Fact(Skip = "Arrow-to-function lowering belongs to internal/transformers/es2015, not tsgo estransforms; the ES2015 package is not present in this task scope")]
    public void ArrowFunction_LowersToFunctionExpression()
    {
    }

    [Fact(Skip = "let/const block-scoping lowering belongs to internal/transformers/es2015, not tsgo estransforms; the ES2015 package is not present in this task scope")]
    public void LetAndConst_LowerToVarWithPreservedScoping()
    {
    }

    [Fact(Skip = "ES5 class-to-function/prototype lowering belongs to internal/transformers/es2015, not tsgo estransforms; classfields.go only lowers class fields/private elements")]
    public void ClassDeclaration_LowersToFunctionAndPrototypeAssignments()
    {
    }
}
