// Tests for the phase-3 parser Types cluster (Ts.Parser.Parser type-grammar bodies:
// parseType/parseUnionOrIntersectionType/parseTypeOperatorOrHigher/parsePostfixTypeOrHigher/
// parseNonArrayType/parseKeywordTypeNode/parseTupleType/parseMappedType/... and helpers).
//
// SCOPE NOTE (phase 3): these are hand-authored unit tests over the AST-kind tree the Types
// cluster produces. They drive the parser through Parser.ParseTypeForTest, an internal test
// seam that enters the grammar at parseUnionTypeOrHigher (what parseType delegates to for
// non-function types), bypassing the function/constructor-type probe
// isStartOfFunctionTypeOrConstructorType() — a NotImplementedException stub owned by the
// still-in-flight Declarations cluster. The covered surface is therefore every type shape
// whose parse path stays inside the Types cluster (plus the ported scanner/factory/predicate
// core): keyword types, type operators (keyof/unique/readonly), array types, postfix !/?,
// and this-type. Shapes that reach still-stubbed clusters — type references, tuples,
// mapped/conditional/template types, multi-constituent unions/intersections, and literal
// types — are asserted structurally here as [Fact(Skip=...)] with the blocking dependency
// named, and become live tests once those clusters land. See the phase-3 port report.

using Ts.Ast;
using Ts.Parser;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserTypeTests
{
    private static Node ParseType(string text) => global::Ts.Parser.Parser.ParseTypeForTest(text);

    // ── keyword types (parseKeywordTypeNode / parseNonArrayType) ──────────────

    [Theory]
    [InlineData("string", SyntaxKind.StringKeyword)]
    [InlineData("number", SyntaxKind.NumberKeyword)]
    [InlineData("boolean", SyntaxKind.BooleanKeyword)]
    [InlineData("any", SyntaxKind.AnyKeyword)]
    [InlineData("unknown", SyntaxKind.UnknownKeyword)]
    [InlineData("never", SyntaxKind.NeverKeyword)]
    [InlineData("void", SyntaxKind.VoidKeyword)]
    [InlineData("object", SyntaxKind.ObjectKeyword)]
    [InlineData("symbol", SyntaxKind.SymbolKeyword)]
    [InlineData("bigint", SyntaxKind.BigIntKeyword)]
    [InlineData("undefined", SyntaxKind.UndefinedKeyword)]
    public void KeywordType_ParsesToKeywordTypeNode(string text, SyntaxKind expected)
    {
        Node node = ParseType(text);
        Assert.Equal(expected, node.Kind);
        // A keyword type node spans exactly the keyword.
        Assert.Equal(0, node.Pos);
        Assert.Equal(text.Length, node.End);
    }

    // ── this-type (parseThisTypeNode) ─────────────────────────────────────────

    [Fact]
    public void ThisType_ParsesToThisTypeNode()
    {
        Node node = ParseType("this");
        Assert.Equal(SyntaxKind.ThisType, node.Kind);
        Assert.Equal(4, node.End);
    }

    // ── type operators (parseTypeOperatorOrHigher / parseTypeOperator) ────────

    [Fact]
    public void KeyofOperator_WrapsOperandType()
    {
        Node node = ParseType("keyof string");
        Assert.Equal(SyntaxKind.TypeOperator, node.Kind);
        TypeOperatorNode op = node.AsTypeOperatorNode();
        Assert.Equal(SyntaxKind.KeyOfKeyword, op.Operator);
        Assert.Equal(SyntaxKind.StringKeyword, op.Type!.Kind);
    }

    [Fact]
    public void UniqueOperator_WrapsOperandType()
    {
        Node node = ParseType("unique symbol");
        Assert.Equal(SyntaxKind.TypeOperator, node.Kind);
        TypeOperatorNode op = node.AsTypeOperatorNode();
        Assert.Equal(SyntaxKind.UniqueKeyword, op.Operator);
        Assert.Equal(SyntaxKind.SymbolKeyword, op.Type!.Kind);
    }

    [Fact]
    public void ReadonlyOperator_WrapsArrayType()
    {
        // `readonly string[]` → TypeOperator(readonly) → ArrayType → StringKeyword
        Node node = ParseType("readonly string[]");
        Assert.Equal(SyntaxKind.TypeOperator, node.Kind);
        TypeOperatorNode op = node.AsTypeOperatorNode();
        Assert.Equal(SyntaxKind.ReadonlyKeyword, op.Operator);
        Assert.Equal(SyntaxKind.ArrayType, op.Type!.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, op.Type.AsArrayTypeNode().ElementType!.Kind);
    }

    [Fact]
    public void NestedTypeOperators_KeyofUniqueSymbol()
    {
        // parseTypeOperator recurses through parseTypeOperatorOrHigher.
        Node node = ParseType("keyof unique symbol");
        Assert.Equal(SyntaxKind.TypeOperator, node.Kind);
        TypeOperatorNode outer = node.AsTypeOperatorNode();
        Assert.Equal(SyntaxKind.KeyOfKeyword, outer.Operator);
        Assert.Equal(SyntaxKind.TypeOperator, outer.Type!.Kind);
        Assert.Equal(SyntaxKind.UniqueKeyword, outer.Type.AsTypeOperatorNode().Operator);
    }

    // ── array / postfix types (parsePostfixTypeOrHigher) ──────────────────────

    [Fact]
    public void ArrayType_WrapsElementType()
    {
        Node node = ParseType("string[]");
        Assert.Equal(SyntaxKind.ArrayType, node.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, node.AsArrayTypeNode().ElementType!.Kind);
    }

    [Fact]
    public void ArrayType_Nested_IsLeftAssociative()
    {
        // `string[][]` → ArrayType(ArrayType(StringKeyword))
        Node node = ParseType("string[][]");
        Assert.Equal(SyntaxKind.ArrayType, node.Kind);
        Node inner = node.AsArrayTypeNode().ElementType!;
        Assert.Equal(SyntaxKind.ArrayType, inner.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, inner.AsArrayTypeNode().ElementType!.Kind);
    }

    [Fact]
    public void PostfixBang_ParsesToJSDocNonNullableType()
    {
        Node node = ParseType("string!");
        Assert.Equal(SyntaxKind.JSDocNonNullableType, node.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, node.AsJSDocNonNullableType().Type!.Kind);
    }

    [Fact]
    public void PostfixQuestion_ParsesToJSDocNullableType()
    {
        // Trailing `?` with no following type is a postfix nullable, not a conditional type.
        Node node = ParseType("string?");
        Assert.Equal(SyntaxKind.JSDocNullableType, node.Kind);
        Assert.Equal(SyntaxKind.StringKeyword, node.AsJSDocNullableType().Type!.Kind);
    }

    [Fact]
    public void ArrayOfKeyof_CombinesPostfixAndOperator()
    {
        // `(keyof string)`-style without parens: `keyof string[]` binds as keyof (string[]),
        // because parseTypeOperator consumes parseTypeOperatorOrHigher which descends to postfix.
        Node node = ParseType("keyof string[]");
        Assert.Equal(SyntaxKind.TypeOperator, node.Kind);
        TypeOperatorNode op = node.AsTypeOperatorNode();
        Assert.Equal(SyntaxKind.KeyOfKeyword, op.Operator);
        Assert.Equal(SyntaxKind.ArrayType, op.Type!.Kind);
    }

    // ── blocked shapes: covered once the named parallel cluster lands ─────────

    [Fact(Skip = "Blocked: multi-constituent unions route through parseFunctionOrConstructorTypeToError → isStartOfFunctionTypeOrConstructorType (Declarations cluster stub).")]
    public void UnionType_TwoKeywordConstituents()
    {
        Node node = ParseType("string | number");
        Assert.Equal(SyntaxKind.UnionType, node.Kind);
        Assert.Equal(2, node.AsUnionTypeNode().Types!.Nodes.Length);
    }

    [Fact(Skip = "Blocked: multi-constituent intersections route through parseFunctionOrConstructorTypeToError → isStartOfFunctionTypeOrConstructorType (Declarations cluster stub).")]
    public void IntersectionType_TwoKeywordConstituents()
    {
        Node node = ParseType("string & number");
        Assert.Equal(SyntaxKind.IntersectionType, node.Kind);
    }

    [Fact(Skip = "Blocked: parseTypeReference → parseEntityName / parseIdentifierName (Statements cluster stubs) and parseTypeArgumentsOfTypeReference (Declarations cluster stub).")]
    public void TypeReference_WithTypeArguments()
    {
        Node node = ParseType("Array<string>");
        Assert.Equal(SyntaxKind.TypeReference, node.Kind);
    }

    [Fact(Skip = "Blocked: parseTupleElementType → parseType reaches isStartOfFunctionTypeOrConstructorType (Declarations cluster stub); named elements use scanStartOfNamedTupleElement (Statements cluster stub).")]
    public void TupleType_TwoElements()
    {
        Node node = ParseType("[string, number]");
        Assert.Equal(SyntaxKind.TupleType, node.Kind);
    }

    [Fact(Skip = "Blocked: mapped-type body parses members via parseTypeMember and parseMappedTypeParameter (Declarations cluster stubs).")]
    public void MappedType_KeyInKeyof()
    {
        Node node = ParseType("{ [K in string]: number }");
        Assert.Equal(SyntaxKind.MappedType, node.Kind);
    }

    [Fact(Skip = "Blocked: conditional type nests parseType (function-type probe) and type references (Declarations/Statements cluster stubs).")]
    public void ConditionalType_ExtendsBranch()
    {
        Node node = ParseType("string extends number ? true : false");
        Assert.Equal(SyntaxKind.ConditionalType, node.Kind);
    }

    [Fact(Skip = "Blocked: template literal type spans call parseTemplateHead / parseLiteralOfTemplateSpan (Expressions cluster) and nested parseType (Declarations cluster stub).")]
    public void TemplateLiteralType_WithSpan()
    {
        Node node = ParseType("`a${string}b`");
        Assert.Equal(SyntaxKind.TemplateLiteralType, node.Kind);
    }

    [Fact(Skip = "Blocked: literal types call parseKeywordExpression / parseLiteralExpression (Expressions cluster); live once that cluster's build lands.")]
    public void LiteralType_StringLiteral()
    {
        Node node = ParseType("\"a\"");
        Assert.Equal(SyntaxKind.LiteralType, node.Kind);
    }

    [Fact(Skip = "Blocked: function-type notation is detected by isStartOfFunctionTypeOrConstructorType and built by parseFunctionOrConstructorType (Declarations cluster stubs).")]
    public void FunctionType_ArrowNotation()
    {
        Node node = ParseType("() => string");
        Assert.Equal(SyntaxKind.FunctionType, node.Kind);
    }
}
