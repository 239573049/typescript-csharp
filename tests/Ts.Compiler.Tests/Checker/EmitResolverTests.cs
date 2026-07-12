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

    // ── phase-6 NodeBuilder request-builder wave (nodebuilder.go / nodebuilderimpl.go) ──────────────
    // The request-scoped NodeBuilder (NewNodeBuilder) is now a real builder, not a throwing stub. These
    // tests exercise the landed serialize surface directly (TypeToTypeNode / IndexInfoToIndexSignature),
    // which is reachable without binding. The full CreateTypeOfDeclaration('const x = 1') path still
    // needs GetWidenedLiteralType (a not-yet-ported widening-wave stub) + binding, so it stays deferred.

    [Fact]
    public void NodeBuilder_TypeToTypeNode_NumberType_IsNumberKeyword()
    {
        var c = NewChecker();
        var nb = c.NewNodeBuilder(global::Ts.Printer.EmitContext.NewEmitContext());

        // nodebuilder.go:267 -> nodebuilderimpl.go:3176: the number intrinsic serializes to `number`.
        var node = nb.TypeToTypeNode(
            c.numberType!, null,
            global::Ts.NodeBuilder.Flags.None, global::Ts.NodeBuilder.InternalFlags.None, NullTracker.Instance);

        Assert.NotNull(node);
        Assert.Equal(SyntaxKind.NumberKeyword, node!.Kind);
    }

    [Fact]
    public void NodeBuilder_TypeToTypeNode_StringLiteralType_IsLiteralTypeNode()
    {
        var c = NewChecker();
        var nb = c.NewNodeBuilder(global::Ts.Printer.EmitContext.NewEmitContext());

        // A string literal type serializes to a LiteralTypeNode wrapping a string literal.
        var litType = c.GetStringLiteralType("hi");
        var node = nb.TypeToTypeNode(
            litType, null,
            global::Ts.NodeBuilder.Flags.None, global::Ts.NodeBuilder.InternalFlags.None, NullTracker.Instance);

        Assert.NotNull(node);
        Assert.Equal(SyntaxKind.LiteralType, node!.Kind);
    }

    [Fact]
    public void NodeBuilder_IndexInfoToIndexSignatureDeclaration_NumberToString()
    {
        var c = NewChecker();
        var nb = c.NewNodeBuilder(global::Ts.Printer.EmitContext.NewEmitContext());

        // nodebuilder.go:111 -> nodebuilderimpl.go:2132: `{ [x: number]: string }` synthesizes an
        // IndexSignatureDeclaration whose parameter/value type nodes come from typeToTypeNode.
        var info = new IndexInfo { keyType = c.numberType!, valueType = c.stringType!, isReadonly = false };
        var node = nb.IndexInfoToIndexSignatureDeclaration(
            info, null,
            global::Ts.NodeBuilder.Flags.None, global::Ts.NodeBuilder.InternalFlags.None, NullTracker.Instance);

        Assert.NotNull(node);
        Assert.Equal(SyntaxKind.IndexSignature, node!.Kind);
        var indexSig = node.AsIndexSignatureDeclaration();
        Assert.Equal(SyntaxKind.StringKeyword, indexSig.Type!.Kind);
        var param = indexSig.Parameters!.Nodes[0];
        Assert.Equal(SyntaxKind.NumberKeyword, param.Type()!.Kind);
    }

    // Minimal no-op SymbolTracker for the request-builder tests (the serialize surface exercised here
    // never invokes the tracker; it only needs a non-null instance).
    private sealed class NullTracker : global::Ts.NodeBuilder.ISymbolTracker
    {
        public static readonly NullTracker Instance = new();
        public bool TrackSymbol(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning) => false;
        public void ReportInaccessibleThisError() { }
        public void ReportPrivateInBaseOfClassExpression(string propertyName) { }
        public void ReportInaccessibleUniqueSymbolError() { }
        public void ReportCyclicStructureError() { }
        public void ReportLikelyUnsafeImportRequiredError(string specifier, string symbolName) { }
        public void ReportTruncationError() { }
        public void ReportNonlocalAugmentation(SourceFile containingFile, Symbol parentSymbol, Symbol augmentingSymbol) { }
        public void ReportNonSerializableProperty(string propertyName) { }
        public void ReportInferenceFallback(Node node) { }
        public void PushErrorFallbackNode(Node? node) { }
        public void PopErrorFallbackNode() { }
    }
}
