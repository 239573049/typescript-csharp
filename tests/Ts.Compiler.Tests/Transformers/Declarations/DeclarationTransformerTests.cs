using global::Ts.Ast;
using global::Ts.Core;
using global::Ts.Printer;
using Xunit;

namespace Ts.Transformers.Declarations.Tests;

public sealed class DeclarationTransformerTests
{
    private static SourceFile Parse(string text)
    {
        var file = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions
            {
                FileName = "/declarations.ts",
                Path = new global::Ts.TsPath.Path("/declarations.ts"),
            },
            text,
            ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(file);
        return file;
    }

    private static SourceFile Transform(string text)
    {
        return DeclarationTransformer.NewDeclarationTransformer(
            new global::Ts.Transformers.TransformOptions
            {
                Context = EmitContext.NewEmitContext(),
                CompilerOptions = new CompilerOptions
                {
                    StrictNullChecks = Tristate.True,
                },
            }).TransformSourceFile(Parse(text));
    }

    [Fact]
    public void FunctionBodyIsStrippedToSignature()
    {
        var transformed = Transform("export function answer(value: number): string { return String(value); }");
        var function = Assert.Single(transformed.Statements!.Nodes).AsFunctionDeclaration();

        Assert.True(transformed.IsDeclarationFile);
        Assert.Null(function.Body);
        Assert.Null(function.AsteriskToken);
        Assert.NotNull(function.Type);
        Assert.Equal(SyntaxKind.StringKeyword, function.Type!.Kind);
        Assert.True(global::Ts.Ast.AstUtilities.HasSyntacticModifier(
            function.AsNode(), ModifierFlags.Export));
        Assert.True(global::Ts.Ast.AstUtilities.HasSyntacticModifier(
            function.AsNode(), ModifierFlags.Ambient));
    }

    [Fact]
    public void ExportedConstGetsDeclareAndInferredType()
    {
        var transformed = Transform("export const answer = 42;");
        var statement = Assert.Single(transformed.Statements!.Nodes).AsVariableStatement();
        var declaration = Assert.Single(
            statement.DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes)
            .AsVariableDeclaration();

        Assert.True(global::Ts.Ast.AstUtilities.HasSyntacticModifier(
            statement.AsNode(), ModifierFlags.Export));
        Assert.True(global::Ts.Ast.AstUtilities.HasSyntacticModifier(
            statement.AsNode(), ModifierFlags.Ambient));
        Assert.Equal(SyntaxKind.NumberKeyword, declaration.Type!.Kind);
        Assert.Null(declaration.Initializer);
        Assert.True((statement.DeclarationList.Flags & NodeFlags.Const) != 0);
    }
}
