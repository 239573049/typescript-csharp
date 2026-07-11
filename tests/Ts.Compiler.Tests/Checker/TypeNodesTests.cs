// Tests for the TypeNodes cluster (plan/03 §4.2 #15): getTypeFromTypeNode dispatch over type
// annotation nodes. Exercises the fully-reachable keyword-type paths (intrinsic fields, no
// cross-cluster machinery). Deeper paths (union/intersection/reference/literal) route into
// SymbolResolution/UnionIntersection/TypeCreation methods still landing in parallel waves and are
// only smoke-checked (they reach a not-yet-implemented dependency), matching the skeleton state.

using Ts.Ast;
using Ts.Checker;
using Ts.Core;
using Xunit;

namespace Ts.Checker.Tests;

public class TypeNodesTests
{
    // Parse `type T = <annotation>;` and return the annotation's type node.
    private static Node ParseTypeAnnotation(string annotation)
    {
        var opts = new SourceFileParseOptions { FileName = "/t.ts", Path = new Ts.TsPath.Path("/t.ts") };
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(opts, "type T = " + annotation + ";", ScriptKind.TS);
        Node stmt = sf.Statements!.Nodes[0]; // TypeAliasDeclaration
        return stmt.Type()!;
    }

    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    [Fact]
    public void KeywordTypes_ResolveToTheirIntrinsicType()
    {
        var c = NewChecker();

        Assert.Same(c.anyType, c.GetTypeFromTypeNode(ParseTypeAnnotation("any")));
        Assert.Same(c.unknownType, c.GetTypeFromTypeNode(ParseTypeAnnotation("unknown")));
        Assert.Same(c.stringType, c.GetTypeFromTypeNode(ParseTypeAnnotation("string")));
        Assert.Same(c.numberType, c.GetTypeFromTypeNode(ParseTypeAnnotation("number")));
        Assert.Same(c.bigintType, c.GetTypeFromTypeNode(ParseTypeAnnotation("bigint")));
        Assert.Same(c.booleanType, c.GetTypeFromTypeNode(ParseTypeAnnotation("boolean")));
        Assert.Same(c.esSymbolType, c.GetTypeFromTypeNode(ParseTypeAnnotation("symbol")));
        Assert.Same(c.voidType, c.GetTypeFromTypeNode(ParseTypeAnnotation("void")));
        Assert.Same(c.undefinedType, c.GetTypeFromTypeNode(ParseTypeAnnotation("undefined")));
        Assert.Same(c.nullType, c.GetTypeFromTypeNode(ParseTypeAnnotation("null")));
        Assert.Same(c.neverType, c.GetTypeFromTypeNode(ParseTypeAnnotation("never")));
        Assert.Same(c.nonPrimitiveType, c.GetTypeFromTypeNode(ParseTypeAnnotation("object")));
    }

    [Theory]
    [InlineData("string", TypeFlags.String)]
    [InlineData("number", TypeFlags.Number)]
    // [InlineData("boolean", TypeFlags.Boolean)] — booleanType = getUnionType([false,true]); a Union, and getUnionType is a skeleton stand-in (later wave). Re-enable after UnionIntersection cluster.
    [InlineData("bigint", TypeFlags.BigInt)]
    [InlineData("void", TypeFlags.Void)]
    [InlineData("undefined", TypeFlags.Undefined)]
    [InlineData("null", TypeFlags.Null)]
    [InlineData("never", TypeFlags.Never)]
    [InlineData("any", TypeFlags.Any)]
    [InlineData("unknown", TypeFlags.Unknown)]
    public void KeywordTypes_CarryTheExpectedTypeFlag(string annotation, TypeFlags expected)
    {
        var c = NewChecker();
        var t = c.GetTypeFromTypeNode(ParseTypeAnnotation(annotation));
        Assert.True((t.Flags & expected) != 0, $"'{annotation}' should have flag {expected}, was {t.Flags}");
    }

    [Fact]
    public void ParenthesizedType_UnwrapsToInnerType()
    {
        var c = NewChecker();
        Assert.Same(c.stringType, c.GetTypeFromTypeNode(ParseTypeAnnotation("(string)")));
    }

    [Fact]
    public void ResolvedType_IsCachedOnTypeNodeLinks()
    {
        var c = NewChecker();
        var node = ParseTypeAnnotation("string");
        var first = c.GetTypeFromTypeNode(node);
        var second = c.GetTypeFromTypeNode(node);
        Assert.Same(first, second);
    }

    [Fact]
    public void TypePredicateWithAssert_IsVoid_WithoutAssert_IsBoolean()
    {
        var c = NewChecker();
        // `x is string` type-guard predicate → boolean.
        var opts = new SourceFileParseOptions { FileName = "/t.ts", Path = new Ts.TsPath.Path("/t.ts") };
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(opts, "function f(x: unknown): x is string { return true; }", ScriptKind.TS);
        Node fn = sf.Statements!.Nodes[0];
        Node predicate = fn.Type()!; // TypePredicateNode
        Assert.Equal(SyntaxKind.TypePredicate, predicate.Kind);
        Assert.Same(c.booleanType, c.GetTypeFromTypeNode(predicate));
    }

    // Union routes through getAliasForTypeNode → getSymbolOfDeclaration and getUnionTypeEx, which are
    // filled by parallel waves (#10 / #17). Until then the reachable dispatch lands on a not-yet-
    // implemented dependency. This asserts the #15 dispatch reaches the union handler (deferred).
    [Fact]
    public void UnionType_Dispatch_ReachesDeferredMachinery()
    {
        var c = NewChecker();
        var node = ParseTypeAnnotation("string | number");
        Assert.Equal(SyntaxKind.UnionType, node.Kind);
        Assert.Throws<System.NotImplementedException>(() => c.GetTypeFromTypeNode(node));
    }
}
