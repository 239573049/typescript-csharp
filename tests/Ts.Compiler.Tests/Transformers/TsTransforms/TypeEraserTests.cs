using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Xunit;

namespace Ts.Transformers.TsTransforms.Tests;

public sealed class TsTransformTypeEraserTests
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
        return TypeEraserTransformer.NewTypeEraserTransformer(
            new global::Ts.Transformers.TransformOptions
            {
                Context = EmitContext.NewEmitContext(),
                CompilerOptions = new CompilerOptions(),
            }).TransformSourceFile(Parse(text));
    }

    [Fact]
    public void InterfaceAndTypeAliasAreErased()
    {
        var transformed = Transform("interface I { value: number } type T = string; const value = 1;");

        Assert.DoesNotContain(
            transformed.Statements!.Nodes,
            statement => statement.Kind is SyntaxKind.InterfaceDeclaration or SyntaxKind.TypeAliasDeclaration);
        Assert.Contains(
            transformed.Statements.Nodes,
            statement => statement.Kind == SyntaxKind.NotEmittedStatement);
        Assert.Contains(
            transformed.Statements.Nodes,
            statement => statement.Kind == SyntaxKind.VariableStatement);
    }

    [Fact]
    public void TypeAnnotationIsStrippedFromParameter()
    {
        var transformed = Transform("function f(value: number, optional?: string): void { }");
        var function = Assert.Single(
            transformed.Statements!.Nodes,
            statement => statement.Kind == SyntaxKind.FunctionDeclaration).AsFunctionDeclaration();

        Assert.Null(function.Type);
        Assert.All(function.Parameters!.Nodes, parameter => Assert.Null(parameter.Type()));
        Assert.Null(function.Parameters.Nodes[1].QuestionToken());
    }
}
