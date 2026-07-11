using Ts.Ast;
using Ts.Core;
using Xunit;

namespace Ts.PseudoChecker.Tests;

public class PseudoCheckerTests
{
    private static SourceFile Parse(string text)
    {
        var options = new SourceFileParseOptions
        {
            FileName = "/pseudochecker.ts",
            Path = new global::Ts.TsPath.Path("/pseudochecker.ts"),
        };
        return global::Ts.Parser.Parser.ParseSourceFile(options, text, ScriptKind.TS);
    }

    private static Node ParseExpression(string text)
    {
        var sourceFile = Parse(text + ";");
        return sourceFile.Statements!.Nodes[0].AsExpressionStatement().Expression!;
    }

    [Fact]
    public void NewPseudoChecker_ResolvesPrimitiveExpressions()
    {
        var checker = global::Ts.PseudoChecker.PseudoChecker.NewPseudoChecker(strictNullChecks: true, exactOptionalPropertyTypes: true);

        var undefinedType = checker.GetTypeOfExpression(ParseExpression("undefined"));
        var nullType = checker.GetTypeOfExpression(ParseExpression("null"));
        var numericType = checker.GetTypeOfExpression(ParseExpression("42"));

        Assert.Same(global::Ts.PseudoChecker.PseudoChecker.PseudoTypeUndefined, undefinedType);
        Assert.Same(global::Ts.PseudoChecker.PseudoChecker.PseudoTypeNull, nullType);
        Assert.Equal(PseudoTypeKind.MaybeConstLocation, numericType.Kind);
        Assert.Equal(PseudoTypeKind.NumericLiteral, numericType.AsPseudoTypeMaybeConstLocation().ConstType.Kind);
        Assert.Same(global::Ts.PseudoChecker.PseudoChecker.PseudoTypeNumber, numericType.AsPseudoTypeMaybeConstLocation().RegularType);
    }

    [Fact]
    public void GetTypeOfDeclaration_UsesExplicitTypeAnnotationDirectly()
    {
        var sourceFile = Parse("let value: string = 'value';");
        var declaration = sourceFile.Statements!.Nodes[0]
            .AsVariableStatement().DeclarationList!
            .AsVariableDeclarationList().Declarations!.Nodes[0]
            .AsVariableDeclaration();
        var checker = global::Ts.PseudoChecker.PseudoChecker.NewPseudoChecker(strictNullChecks: true, exactOptionalPropertyTypes: false);

        var type = checker.GetTypeOfDeclaration(declaration);

        Assert.Equal(PseudoTypeKind.Direct, type.Kind);
        Assert.Same(declaration.Type, type.AsPseudoTypeDirect().TypeNode);
    }

    [Fact]
    public void CouldAlreadyReferToUndefinedType_InspectsDirectAndUnionPseudoTypes()
    {
        var sourceFile = Parse("type Maybe = string | undefined; type Definite = number;");
        var maybeNode = sourceFile.Statements!.Nodes[0].AsTypeAliasDeclaration().Type!;
        var definiteNode = sourceFile.Statements.Nodes[1].AsTypeAliasDeclaration().Type!;
        var maybe = global::Ts.PseudoChecker.PseudoChecker.NewPseudoTypeDirect(maybeNode);
        var definite = global::Ts.PseudoChecker.PseudoChecker.NewPseudoTypeDirect(definiteNode);
        var union = global::Ts.PseudoChecker.PseudoChecker.NewPseudoTypeUnion(
            [definite, global::Ts.PseudoChecker.PseudoChecker.PseudoTypeUndefined]);

        Assert.True(global::Ts.PseudoChecker.PseudoChecker.CouldAlreadyReferToUndefinedType(maybe));
        Assert.False(global::Ts.PseudoChecker.PseudoChecker.CouldAlreadyReferToUndefinedType(definite));
        Assert.True(global::Ts.PseudoChecker.PseudoChecker.CouldAlreadyReferToUndefinedType(union));
    }

    [Fact]
    public void PseudoObjectElement_SignatureLookupMatchesElementKind()
    {
        var signature = ParseExpression("() => 1");
        var name = ParseExpression("member");
        var method = global::Ts.PseudoChecker.PseudoChecker.NewPseudoObjectMethod(
            signature,
            name,
            optional: false,
            typeParameters: null,
            parameters: null,
            returnType: global::Ts.PseudoChecker.PseudoChecker.PseudoTypeNumber);
        var property = global::Ts.PseudoChecker.PseudoChecker.NewPseudoPropertyAssignment(
            readOnly: true,
            name: name,
            optional: false,
            t: global::Ts.PseudoChecker.PseudoChecker.PseudoTypeString);

        Assert.Same(signature, method.Signature());
        Assert.Null(property.Signature());
        Assert.True(property.AsPseudoPropertyAssignment().Readonly);
    }
}
