// Tests for the Expressions cluster of Ts.Parser.Parser (internal/parser/parser.go expression ladder).
//
// SCOPE NOTE (phase 3): The public ParseSourceFile entry cannot yet reach expression parsing — it
// routes through statement parsing, which is still a NotImplementedException stub in the sibling
// Statements cluster. Moreover the full assignment/binary ladder recurses into arrow-function,
// type-argument-in-expression, and optional-chain helpers that live in the Declarations/Statements/
// Types clusters and are likewise stubbed. These unit tests therefore exercise the *reachable*
// primary-expression surface via the internal ParsePrimaryExpressionForTest seam: literals, keyword
// expressions, identifiers, private identifiers, and empty array/object literals (whose delimited
// lists terminate before any element parser — and thus any cross-cluster stub — is invoked).
//
// The deeper structures the task enumerates (binary precedence, unary, update, call chains, member
// access, optional chains, await/yield, arrow functions, templates with substitutions, and non-empty
// array/object literals) are fully implemented in Parser.Expressions.cs but only become runtime-
// exercisable once the Declarations/Statements/Types clusters land; they are deferred to the phase-3
// cross-cluster integration tests. See the port report.

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserExpressionTests
{
    private static (Node Node, Diagnostic[] Diagnostics) ParsePrimary(string text, ScriptKind kind = ScriptKind.TS) =>
        global::Ts.Parser.Parser.ParsePrimaryExpressionForTest(text, kind);

    private static Node ParsePrimaryOk(string text, ScriptKind kind = ScriptKind.TS)
    {
        (Node node, Diagnostic[] diags) = ParsePrimary(text, kind);
        Assert.Empty(diags);
        return node;
    }

    // ── literals ──

    [Fact]
    public void NumericLiteral_ProducesNumericLiteralNode()
    {
        Node node = ParsePrimaryOk("42");
        Assert.Equal(SyntaxKind.NumericLiteral, node.Kind);
        Assert.Equal("42", node.AsNumericLiteral().Text);
        Assert.Equal(0, node.Pos);
        Assert.Equal(2, node.End);
    }

    [Fact]
    public void StringLiteral_ProducesStringLiteralNodeWithUnquotedText()
    {
        Node node = ParsePrimaryOk("\"hello\"");
        Assert.Equal(SyntaxKind.StringLiteral, node.Kind);
        Assert.Equal("hello", node.AsStringLiteral().Text);
    }

    [Fact]
    public void BigIntLiteral_ProducesBigIntLiteralNode()
    {
        Node node = ParsePrimaryOk("123n");
        Assert.Equal(SyntaxKind.BigIntLiteral, node.Kind);
        Assert.Equal("123n", node.AsBigIntLiteral().Text);
    }

    [Fact]
    public void NoSubstitutionTemplateLiteral_ProducesTemplateLiteralNode()
    {
        Node node = ParsePrimaryOk("`hello`");
        Assert.Equal(SyntaxKind.NoSubstitutionTemplateLiteral, node.Kind);
        Assert.Equal("hello", node.AsNoSubstitutionTemplateLiteral().Text);
    }

    // ── keyword expressions (the node carries the keyword's own SyntaxKind) ──

    [Theory]
    [InlineData("this", SyntaxKind.ThisKeyword)]
    [InlineData("super", SyntaxKind.SuperKeyword)]
    [InlineData("null", SyntaxKind.NullKeyword)]
    [InlineData("true", SyntaxKind.TrueKeyword)]
    [InlineData("false", SyntaxKind.FalseKeyword)]
    public void KeywordExpression_CarriesKeywordKind(string text, SyntaxKind expected)
    {
        Node node = ParsePrimaryOk(text);
        Assert.Equal(expected, node.Kind);
    }

    // ── identifiers ──

    [Fact]
    public void Identifier_ProducesIdentifierNode()
    {
        Node node = ParsePrimaryOk("foo");
        Assert.Equal(SyntaxKind.Identifier, node.Kind);
        Assert.Equal("foo", node.AsIdentifier().Text);
    }

    [Fact]
    public void PrivateIdentifier_ProducesPrivateIdentifierNode()
    {
        Node node = ParsePrimaryOk("#field");
        Assert.Equal(SyntaxKind.PrivateIdentifier, node.Kind);
        Assert.Equal("#field", node.AsPrivateIdentifier().Text);
    }

    // ── empty array / object literals (delimited list terminates before any element parser) ──

    [Fact]
    public void EmptyArrayLiteral_ProducesArrayLiteralWithNoElements()
    {
        Node node = ParsePrimaryOk("[]");
        Assert.Equal(SyntaxKind.ArrayLiteralExpression, node.Kind);
        ArrayLiteralExpression arr = node.AsArrayLiteralExpression();
        Assert.NotNull(arr.Elements);
        Assert.Empty(arr.Elements!.Nodes);
        Assert.False(arr.MultiLine);
    }

    [Fact]
    public void EmptyArrayLiteral_MultiLineSetWhenOpenBracketFollowedByNewline()
    {
        Node node = ParsePrimaryOk("[\n]");
        ArrayLiteralExpression arr = node.AsArrayLiteralExpression();
        Assert.Empty(arr.Elements!.Nodes);
        Assert.True(arr.MultiLine);
    }

    [Fact]
    public void EmptyObjectLiteral_ProducesObjectLiteralWithNoProperties()
    {
        Node node = ParsePrimaryOk("{}");
        Assert.Equal(SyntaxKind.ObjectLiteralExpression, node.Kind);
        ObjectLiteralExpression obj = node.AsObjectLiteralExpression();
        Assert.NotNull(obj.Properties);
        Assert.Empty(obj.Properties!.Nodes);
        Assert.False(obj.MultiLine);
    }

    // ── error recovery ──

    [Fact]
    public void UnexpectedToken_ReportsExpressionExpectedAndYieldsMissingIdentifier()
    {
        // ')' cannot start a primary expression: parsePrimaryExpression falls through to
        // parseIdentifierWithDiagnostic(Expression_expected), producing a zero-width missing
        // identifier and diagnostic code 1109.
        (Node node, Diagnostic[] diags) = ParsePrimary(")");
        Assert.Equal(SyntaxKind.Identifier, node.Kind);
        Assert.Single(diags);
        Assert.Equal(1109, diags[0].Code());
    }

    [Fact]
    public void RepeatedPrimaryParses_ProduceIndependentNodes()
    {
        // Exercises the ObjectPool<Parser> reuse path through the test seam.
        Node a = ParsePrimaryOk("1");
        Node b = ParsePrimaryOk("2");
        Assert.NotSame(a, b);
        Assert.Equal("1", a.AsNumericLiteral().Text);
        Assert.Equal("2", b.AsNumericLiteral().Text);
    }
}

// End-to-end tests of the full expression ladder via the public ParseSourceFile entry. These are
// integration-level: they depend on the now-landed Statements/Declarations/Types clusters to route
// `<expr>;` through parseStatement -> parseExpressionOrLabeledStatement (Expressions cluster) and to
// resolve the arrow-function / type-argument / optional-chain helpers those clusters own. They assert
// the AST-kind trees produced by the Expressions cluster's precedence climbing, associativity, unary/
// update handling, and call/member/optional chains.
public class ParserExpressionLadderTests
{
    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile Parse(string text) =>
        global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, ScriptKind.TS);

    // Extract the sole expression from `<expr>;`, asserting a clean (diagnostic-free) parse.
    private static Node Expr(string exprText)
    {
        SourceFile sf = Parse(exprText + ";");
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        Node stmt = sf.Statements!.Nodes[0];
        Assert.Equal(SyntaxKind.ExpressionStatement, stmt.Kind);
        return stmt.AsExpressionStatement().Expression!;
    }

    [Fact]
    public void MultiplicativeBindsTighterThanAdditive()
    {
        // 1 + 2 * 3  ==>  (+ 1 (* 2 3))
        Node e = Expr("1 + 2 * 3");
        Assert.Equal(SyntaxKind.BinaryExpression, e.Kind);
        BinaryExpression add = e.AsBinaryExpression();
        Assert.Equal(SyntaxKind.PlusToken, add.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.NumericLiteral, add.Left!.Kind);
        Assert.Equal(SyntaxKind.BinaryExpression, add.Right!.Kind);
        Assert.Equal(SyntaxKind.AsteriskToken, add.Right.AsBinaryExpression().OperatorToken!.Kind);
    }

    [Fact]
    public void AdditiveIsLeftAssociative()
    {
        // 1 - 2 - 3  ==>  (- (- 1 2) 3)
        Node e = Expr("1 - 2 - 3");
        BinaryExpression outer = e.AsBinaryExpression();
        Assert.Equal(SyntaxKind.MinusToken, outer.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.BinaryExpression, outer.Left!.Kind);
        Assert.Equal(SyntaxKind.NumericLiteral, outer.Right!.Kind);
    }

    [Fact]
    public void ExponentiationIsRightAssociative()
    {
        // 2 ** 3 ** 4  ==>  (** 2 (** 3 4))
        Node e = Expr("2 ** 3 ** 4");
        BinaryExpression outer = e.AsBinaryExpression();
        Assert.Equal(SyntaxKind.AsteriskAsteriskToken, outer.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.NumericLiteral, outer.Left!.Kind);
        Assert.Equal(SyntaxKind.BinaryExpression, outer.Right!.Kind);
    }

    [Fact]
    public void AssignmentIsRightAssociative()
    {
        // a = b = c  ==>  (= a (= b c))
        Node e = Expr("a = b = c");
        BinaryExpression outer = e.AsBinaryExpression();
        Assert.Equal(SyntaxKind.EqualsToken, outer.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.Identifier, outer.Left!.Kind);
        Assert.Equal(SyntaxKind.BinaryExpression, outer.Right!.Kind);
    }

    [Fact]
    public void LogicalPrecedence_AndBindsTighterThanOr()
    {
        // a || b && c  ==>  (|| a (&& b c))
        Node e = Expr("a || b && c");
        BinaryExpression or = e.AsBinaryExpression();
        Assert.Equal(SyntaxKind.BarBarToken, or.OperatorToken!.Kind);
        Assert.Equal(SyntaxKind.BinaryExpression, or.Right!.Kind);
        Assert.Equal(SyntaxKind.AmpersandAmpersandToken, or.Right.AsBinaryExpression().OperatorToken!.Kind);
    }

    [Fact]
    public void CommaOperatorFormsBinaryExpression()
    {
        Node e = Expr("a, b");
        Assert.Equal(SyntaxKind.BinaryExpression, e.Kind);
        Assert.Equal(SyntaxKind.CommaToken, e.AsBinaryExpression().OperatorToken!.Kind);
    }

    [Fact]
    public void PrefixUnaryMinus()
    {
        Node e = Expr("-a");
        Assert.Equal(SyntaxKind.PrefixUnaryExpression, e.Kind);
        Assert.Equal(SyntaxKind.MinusToken, e.AsPrefixUnaryExpression().Operator);
        Assert.Equal(SyntaxKind.Identifier, e.AsPrefixUnaryExpression().Operand!.Kind);
    }

    [Fact]
    public void LogicalNotChainsAsNestedPrefixUnary()
    {
        Node e = Expr("!!a");
        Assert.Equal(SyntaxKind.PrefixUnaryExpression, e.Kind);
        Assert.Equal(SyntaxKind.ExclamationToken, e.AsPrefixUnaryExpression().Operator);
        Assert.Equal(SyntaxKind.PrefixUnaryExpression, e.AsPrefixUnaryExpression().Operand!.Kind);
    }

    [Fact]
    public void PostfixIncrement()
    {
        Node e = Expr("a++");
        Assert.Equal(SyntaxKind.PostfixUnaryExpression, e.Kind);
        Assert.Equal(SyntaxKind.PlusPlusToken, e.AsPostfixUnaryExpression().Operator);
    }

    [Fact]
    public void PrefixIncrement()
    {
        Node e = Expr("++a");
        Assert.Equal(SyntaxKind.PrefixUnaryExpression, e.Kind);
        Assert.Equal(SyntaxKind.PlusPlusToken, e.AsPrefixUnaryExpression().Operator);
    }

    [Fact]
    public void TypeofVoidDeleteUnaryExpressions()
    {
        Assert.Equal(SyntaxKind.TypeOfExpression, Expr("typeof a").Kind);
        Assert.Equal(SyntaxKind.VoidExpression, Expr("void a").Kind);
        Assert.Equal(SyntaxKind.DeleteExpression, Expr("delete a.b").Kind);
    }

    [Fact]
    public void CallExpressionWithArguments()
    {
        Node e = Expr("f(1, 2, 3)");
        Assert.Equal(SyntaxKind.CallExpression, e.Kind);
        CallExpression call = e.AsCallExpression();
        Assert.Equal(SyntaxKind.Identifier, call.Expression!.Kind);
        Assert.Equal(3, call.Arguments!.Nodes.Length);
    }

    [Fact]
    public void ChainedPropertyAccessIsLeftNested()
    {
        // a.b.c  ==>  (. (. a b) c)
        Node e = Expr("a.b.c");
        Assert.Equal(SyntaxKind.PropertyAccessExpression, e.Kind);
        Assert.Equal(SyntaxKind.PropertyAccessExpression, e.AsPropertyAccessExpression().Expression!.Kind);
    }

    [Fact]
    public void ElementAccess()
    {
        Node e = Expr("a[0]");
        Assert.Equal(SyntaxKind.ElementAccessExpression, e.Kind);
        Assert.Equal(SyntaxKind.NumericLiteral, e.AsElementAccessExpression().ArgumentExpression!.Kind);
    }

    [Fact]
    public void MixedCallMemberElementChain()
    {
        // f().g[0]  ==>  ([] (. (call f) g) 0)
        Node e = Expr("f().g[0]");
        Assert.Equal(SyntaxKind.ElementAccessExpression, e.Kind);
        Node g = e.AsElementAccessExpression().Expression!;
        Assert.Equal(SyntaxKind.PropertyAccessExpression, g.Kind);
        Assert.Equal(SyntaxKind.CallExpression, g.AsPropertyAccessExpression().Expression!.Kind);
    }

    [Fact]
    public void OptionalChainSetsFlag()
    {
        Node e = Expr("a?.b");
        Assert.Equal(SyntaxKind.PropertyAccessExpression, e.Kind);
        Assert.True((e.Flags & NodeFlags.OptionalChain) != 0);
    }

    [Fact]
    public void ConditionalExpressionTree()
    {
        Node e = Expr("a ? b : c");
        Assert.Equal(SyntaxKind.ConditionalExpression, e.Kind);
        ConditionalExpression c = e.AsConditionalExpression();
        Assert.Equal(SyntaxKind.Identifier, c.Condition!.Kind);
        Assert.Equal(SyntaxKind.Identifier, c.WhenTrue!.Kind);
        Assert.Equal(SyntaxKind.Identifier, c.WhenFalse!.Kind);
    }

    [Fact]
    public void NonEmptyArrayLiteral()
    {
        Node e = Expr("[1, 2, 3]");
        Assert.Equal(SyntaxKind.ArrayLiteralExpression, e.Kind);
        Assert.Equal(3, e.AsArrayLiteralExpression().Elements!.Nodes.Length);
    }

    [Fact]
    public void SpreadElementInArrayLiteral()
    {
        Node e = Expr("[...a, b]");
        Assert.Equal(SyntaxKind.SpreadElement, e.AsArrayLiteralExpression().Elements!.Nodes[0].Kind);
    }

    [Fact]
    public void ObjectLiteralInParens()
    {
        // Parenthesized to keep the object literal in expression (not block) position.
        Node e = Expr("({ a: 1, b })");
        Assert.Equal(SyntaxKind.ParenthesizedExpression, e.Kind);
        Node obj = e.AsParenthesizedExpression().Expression!;
        Assert.Equal(SyntaxKind.ObjectLiteralExpression, obj.Kind);
        Assert.Equal(2, obj.AsObjectLiteralExpression().Properties!.Nodes.Length);
    }

    [Fact]
    public void TemplateExpressionWithSubstitution()
    {
        Node e = Expr("`a${b}c`");
        Assert.Equal(SyntaxKind.TemplateExpression, e.Kind);
    }

    [Fact]
    public void TaggedTemplateExpression()
    {
        Node e = Expr("tag`a${b}c`");
        Assert.Equal(SyntaxKind.TaggedTemplateExpression, e.Kind);
    }

    [Fact]
    public void NewExpressionWithArguments()
    {
        Node e = Expr("new Foo(1)");
        Assert.Equal(SyntaxKind.NewExpression, e.Kind);
    }
}
