// Smoke tests for the phase-5/6 EmitResolver wave (emitresolver.go). These exercise only the
// fully-reachable, no-bind visibility/reference paths of the EmitResolver (the class wraps a Checker
// and implements global::Ts.Printer.EmitResolver). Paths that require binding (GetSymbolOfDeclaration)
// or the not-yet-ported request NodeBuilder (CreateTypeOfDeclaration → SerializeTypeForDeclaration)
// are marked Skip, matching the current skeleton state.

using Ts.Ast;
using Ts.Checker;
using Ts.Core;
using Xunit;

namespace Ts.Checker.Tests;

public class EmitResolverTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    private static SourceFile Parse(string text)
    {
        var opts = new SourceFileParseOptions { FileName = "/t.ts", Path = new Ts.TsPath.Path("/t.ts") };
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, ScriptKind.TS);
    }

    [Fact]
    public void GetEmitResolver_ReturnsSameInstance()
    {
        var c = NewChecker();
        var r1 = c.GetEmitResolver();
        var r2 = c.GetEmitResolver();

        Assert.NotNull(r1);
        Assert.Same(r1, r2); // emitResolverOnce — lazily constructed once
        // Implements the printer contract consumed by transformers/declaration emit.
        Assert.IsAssignableFrom<global::Ts.Printer.EmitResolver>(r1);
    }

    [Fact]
    public void IsDeclarationVisible_SourceFile_IsTrue()
    {
        var c = NewChecker();
        var sf = Parse("const x = 1;");
        var r = c.GetEmitResolver();

        // determineIfDeclarationIsVisible: KindSourceFile => always visible (emitresolver.go:214-216).
        Assert.True(r.IsDeclarationVisible(sf.AsNode()));
    }

    [Fact]
    public void IsDeclarationVisible_TypeParameter_IsTrue()
    {
        var c = NewChecker();
        // function f<T>(): void {}  — grab the type parameter node.
        var sf = Parse("function f<T>(): void {}");
        Node fn = sf.Statements!.Nodes[0];
        Node typeParam = fn.TypeParameterList()!.Nodes[0];
        var r = c.GetEmitResolver();

        // determineIfDeclarationIsVisible: KindTypeParameter => always visible (emitresolver.go:210-212).
        Assert.True(r.IsDeclarationVisible(typeParam));
    }

    [Fact]
    public void IsDeclarationVisible_ImportClause_IsFalse()
    {
        var c = NewChecker();
        // import d from "mod";  — the import clause is not visible by default (emitresolver.go:205-208).
        var sf = Parse("import d from \"mod\";");
        Node importDecl = sf.Statements!.Nodes[0];
        Node importClause = importDecl.AsImportDeclaration().ImportClause!.AsNode();
        var r = c.GetEmitResolver();

        Assert.False(r.IsDeclarationVisible(importClause));
    }

    [Fact]
    public void IsLateBound_Null_IsFalse()
    {
        var c = NewChecker();
        var r = c.GetEmitResolver();

        // emitresolver.go:74-76 — nil node => false (no symbol lookup, no bind required).
        Assert.False(r.IsLateBound(null!));
    }

    [Fact(Skip = "Requires binding: IsValueAliasDeclaration -> GetSymbolOfDeclaration is not reachable with FakeProgram (no bind), and default options keep canCollectSymbolAliasAccessibilityData=true.")]
    public void IsValueAliasDeclaration_ValueImport_IsTrue()
    {
    }

    [Fact(Skip = "Requires the request NodeBuilder wave: CreateTypeOfDeclaration -> NewNodeBuilder(...).SerializeTypeForDeclaration is a throwing stub.")]
    public void CreateTypeOfDeclaration_ConstOne_SynthesizesNumberTypeNode()
    {
        // Target once the NodeBuilder request-builder lands: `const x = 1;` should synthesize the
        // `number` (literal) type node via SerializeTypeForDeclaration.
    }
}
