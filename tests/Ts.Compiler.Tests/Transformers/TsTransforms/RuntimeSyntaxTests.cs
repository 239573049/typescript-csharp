using System.Linq;
using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Xunit;

namespace Ts.Transformers.TsTransforms.Tests;

public sealed class TsTransformRuntimeSyntaxTests
{
    private static SourceFile Parse(string text)
    {
        var file = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions
            {
                FileName = "/tstransform.ts",
                Path = new global::Ts.TsPath.Path("/tstransform.ts"),
            },
            text,
            ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(file);
        return file;
    }

    private static SourceFile Transform(string text)
    {
        var options = new CompilerOptions();
        var context = EmitContext.NewEmitContext();
        var transformOptions = new global::Ts.Transformers.TransformOptions
        {
            Context = context,
            CompilerOptions = options,
        };
        var erased = TypeEraserTransformer.NewTypeEraserTransformer(transformOptions)
            .TransformSourceFile(Parse(text));
        return RuntimeSyntaxTransformer.NewRuntimeSyntaxTransformer(transformOptions)
            .TransformSourceFile(erased);
    }

    [Fact]
    public void NumericEnumLowersToIifeWithReverseMapping()
    {
        var transformed = Transform("enum Direction { Up = 1, Down }");
        Assert.Equal(2, transformed.Statements!.Nodes.Length);
        Assert.Equal(SyntaxKind.VariableStatement, transformed.Statements.Nodes[0].Kind);

        var call = transformed.Statements.Nodes[1].Expression()!.AsCallExpression();
        Assert.Equal(SyntaxKind.ParenthesizedExpression, call.Expression!.Kind);
        var function = call.Expression.Expression()!.AsFunctionExpression();
        var body = function.Body!.AsBlock();
        Assert.Equal(2, body.Statements!.Nodes.Length);

        foreach (var statement in body.Statements.Nodes)
        {
            var outerAssignment = statement.Expression()!.AsBinaryExpression();
            Assert.Equal(SyntaxKind.EqualsToken, outerAssignment.OperatorToken!.Kind);
            Assert.Equal(SyntaxKind.ElementAccessExpression, outerAssignment.Left!.Kind);
            Assert.Equal(SyntaxKind.BinaryExpression,
                outerAssignment.Left.AsElementAccessExpression().ArgumentExpression!.Kind);
        }
    }

    [Fact]
    public void NamespaceLowersToIife()
    {
        var transformed = Transform("namespace N { export const value = 1; }");

        Assert.Equal(SyntaxKind.VariableStatement, transformed.Statements!.Nodes[0].Kind);
        var call = transformed.Statements.Nodes[1].Expression()!.AsCallExpression();
        Assert.Equal(SyntaxKind.FunctionExpression, call.Expression!.Expression()!.Kind);
        Assert.Single(call.Arguments!.Nodes);
    }

    [Fact]
    public void ParameterPropertyCreatesFieldAndThisAssignment()
    {
        var transformed = Transform("class Point { constructor(public x: number) { } }");
        var declaration = transformed.Statements!.Nodes.Single(
            statement => statement.Kind == SyntaxKind.ClassDeclaration).AsClassDeclaration();

        Assert.Equal(SyntaxKind.PropertyDeclaration, declaration.Members!.Nodes[0].Kind);
        Assert.Equal("x", declaration.Members.Nodes[0].Name()!.Text());

        var constructor = declaration.Members.Nodes.Single(
            member => member.Kind == SyntaxKind.Constructor).AsConstructorDeclaration();
        Assert.Null(constructor.Parameters!.Nodes[0].Type());
        Assert.DoesNotContain(
            constructor.Parameters.Nodes[0].Modifiers()?.Nodes ?? [],
            modifier => modifier.Kind == SyntaxKind.PublicKeyword);

        var assignment = constructor.Body!.AsBlock().Statements!.Nodes[0]
            .Expression()!.AsBinaryExpression();
        Assert.Equal(SyntaxKind.EqualsToken, assignment.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.PropertyAccessExpression, assignment.Left!.Kind);
        Assert.Equal(SyntaxKind.ThisKeyword, assignment.Left.Expression()!.Kind);
        Assert.Equal("x", assignment.Left.Name()!.Text());
    }
}
