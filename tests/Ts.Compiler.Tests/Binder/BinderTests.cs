// Unit tests for Ts.Binder.Binder (internal/binder/binder.go). These drive the public
// ParseSourceFile → BindSourceFile pipeline end-to-end and assert the produced symbol
// tables (locals/members/exports), value declarations, classifiable names, control-flow
// wiring, idempotency, and bind diagnostics.
//
// G4.1 (symbol/flow dump full-corpus byte-diff) is deferred to R24 (dumptool env blocker);
// these self-built assertions cover the binder's structural output in the interim.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.TsPath;
using Xunit;

namespace Ts.Binder.Tests;

public class BinderTests
{
    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile ParseAndBind(string text, ScriptKind kind = ScriptKind.TS)
    {
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, kind);
        global::Ts.Binder.Binder.BindSourceFile(sf);
        return sf;
    }

    private static Symbol Local(SourceFile sf, string name)
    {
        Assert.NotNull(sf.Locals);
        Symbol? s = sf.Locals!.GetValueOrDefault(name);
        Assert.NotNull(s);
        return s!;
    }

    private static Node? FindFirst(Node node, SyntaxKind kind)
    {
        Node? found = null;
        node.ForEachChild(child =>
        {
            if (child.Kind == kind)
            {
                found = child;
                return true;
            }
            found = FindFirst(child, kind);
            return found != null;
        });
        return found;
    }

    // ── locals: var/let/function ──

    [Fact]
    public void Var_DeclaresFunctionScopedLocal()
    {
        SourceFile sf = ParseAndBind("var x = 1;");
        Symbol x = Local(sf, "x");
        Assert.True((x.Flags & SymbolFlags.FunctionScopedVariable) != 0);
        Assert.NotNull(x.ValueDeclaration);
        Assert.Equal(SyntaxKind.VariableDeclaration, x.ValueDeclaration!.Kind);
        Assert.Single(x.Declarations!);
        Assert.True(sf.SymbolCount > 0);
    }

    [Fact]
    public void Let_DeclaresBlockScopedLocal()
    {
        SourceFile sf = ParseAndBind("let y = 2;");
        Symbol y = Local(sf, "y");
        Assert.True((y.Flags & SymbolFlags.BlockScopedVariable) != 0);
        Assert.Equal(SymbolFlags.None, y.Flags & SymbolFlags.FunctionScopedVariable);
    }

    [Fact]
    public void FunctionDeclaration_DeclaresFunctionLocal()
    {
        SourceFile sf = ParseAndBind("function foo() {}");
        Symbol foo = Local(sf, "foo");
        Assert.True((foo.Flags & SymbolFlags.Function) != 0);
        Assert.Equal(SyntaxKind.FunctionDeclaration, foo.ValueDeclaration!.Kind);
    }

    [Fact]
    public void MultipleVars_MergeIntoOneSymbolWithTwoDeclarations()
    {
        SourceFile sf = ParseAndBind("var v = 1; var v = 2;");
        Symbol v = Local(sf, "v");
        Assert.Equal(2, v.Declarations!.Length);
        // var/var merge is legal — no bind diagnostics.
        Assert.True((sf.BindDiagnostics()?.Length ?? 0) == 0);
    }

    // ── classes: members / exports / classifiable ──

    [Fact]
    public void ClassDeclaration_MembersExportsAndPrototype()
    {
        SourceFile sf = ParseAndBind("class C { m() {} p = 1; static s() {} }");
        Symbol c = Local(sf, "C");
        Assert.True((c.Flags & SymbolFlags.Class) != 0);

        Assert.NotNull(c.Members);
        Symbol m = c.Members!.GetValueOrDefault("m")!;
        Assert.NotNull(m);
        Assert.True((m.Flags & SymbolFlags.Method) != 0);
        Symbol p = c.Members!.GetValueOrDefault("p")!;
        Assert.NotNull(p);
        Assert.True((p.Flags & SymbolFlags.Property) != 0);

        Assert.NotNull(c.Exports);
        // 'prototype' is always synthesized into exports; a static member goes to exports too.
        Assert.NotNull(c.Exports!.GetValueOrDefault("prototype"));
        Assert.NotNull(c.Exports!.GetValueOrDefault("s"));

        // class names are classifiable
        Assert.True(sf.ClassifiableNames.Has("C"));
    }

    [Fact]
    public void ClassMergedWithNamespacePrototype_ReportsDuplicate()
    {
        // The bind-time prototype clash fires when a class merges with a namespace that
        // already exported 'prototype' (binder.go:969-972). (A plain `static prototype`
        // clash is reported later by the checker, not the binder.)
        SourceFile sf = ParseAndBind("class C {}\nnamespace C { export const prototype = 1; }");
        Assert.NotNull(sf.BindDiagnostics());
        Assert.NotEmpty(sf.BindDiagnostics()!);
    }

    // ── interfaces ──

    [Fact]
    public void InterfaceDeclaration_TypeMembers()
    {
        SourceFile sf = ParseAndBind("interface I { x: number; foo(): void; }");
        Symbol i = Local(sf, "I");
        Assert.True((i.Flags & SymbolFlags.Interface) != 0);
        Assert.NotNull(i.Members);
        Assert.NotNull(i.Members!.GetValueOrDefault("x"));
        Assert.NotNull(i.Members!.GetValueOrDefault("foo"));
    }

    // ── enums ──

    [Fact]
    public void EnumDeclaration_ExportsMembers()
    {
        SourceFile sf = ParseAndBind("enum E { A, B }");
        Symbol e = Local(sf, "E");
        Assert.True((e.Flags & SymbolFlags.RegularEnum) != 0);
        Assert.NotNull(e.Exports);
        Symbol a = e.Exports!.GetValueOrDefault("A")!;
        Assert.NotNull(a);
        Assert.True((a.Flags & SymbolFlags.EnumMember) != 0);
        Assert.NotNull(e.Exports!.GetValueOrDefault("B"));
    }

    [Fact]
    public void ConstEnum_HasConstEnumFlag()
    {
        SourceFile sf = ParseAndBind("const enum CE { X }");
        Symbol ce = Local(sf, "CE");
        Assert.True((ce.Flags & SymbolFlags.ConstEnum) != 0);
    }

    // ── namespaces ──

    [Fact]
    public void Namespace_ExportsMembers()
    {
        SourceFile sf = ParseAndBind("namespace N { export const a = 1; }");
        Symbol n = Local(sf, "N");
        Assert.True((n.Flags & SymbolFlags.ValueModule) != 0);
        Assert.NotNull(n.Exports);
        Assert.NotNull(n.Exports!.GetValueOrDefault("a"));
    }

    // ── external modules: local + export symbol pairing ──

    [Fact]
    public void ExportConst_MakesModuleAndPairsLocalWithExport()
    {
        SourceFile sf = ParseAndBind("export const e = 1;");
        // File is forced to be an external module.
        Assert.NotNull(sf.ExternalModuleIndicator);
        Assert.NotNull(sf.Symbol);
        Assert.NotNull(sf.Symbol!.Exports);
        Assert.NotNull(sf.Symbol.Exports!.GetValueOrDefault("e"));

        // A local ExportValue symbol is also created, linked to the export symbol.
        Symbol local = Local(sf, "e");
        Assert.True((local.Flags & SymbolFlags.ExportValue) != 0);
        Assert.NotNull(local.ExportSymbol);
        Assert.Same(sf.Symbol.Exports!.GetValueOrDefault("e"), local.ExportSymbol);
    }

    // ── control flow ──

    [Fact]
    public void Binding_SetsSourceFileEndFlowNode()
    {
        SourceFile sf = ParseAndBind("let a = 1; a = 2;");
        Assert.NotNull(sf.EndFlowNode);
    }

    [Fact]
    public void Identifier_ReferenceGetsFlowNode()
    {
        SourceFile sf = ParseAndBind("x;");
        Node? id = FindFirst(sf.AsNode(), SyntaxKind.Identifier);
        Assert.NotNull(id);
        Assert.NotNull(id!.AsIdentifier().FlowNode);
    }

    [Fact]
    public void FunctionBody_GetsEndFlowNode()
    {
        // A function whose body end is reachable (no unconditional return) records its
        // end-of-body flow node (binder.go:1554-1562). `return;` would leave the end
        // unreachable, so EndFlowNode would stay null.
        SourceFile sf = ParseAndBind("function f() { const x = 1; }");
        Node? fn = FindFirst(sf.AsNode(), SyntaxKind.FunctionDeclaration);
        Assert.NotNull(fn);
        Assert.NotNull(fn!.BodyData());
        Assert.NotNull(fn.BodyData()!.EndFlowNode);
    }

    [Fact]
    public void IfStatement_ConditionIdentifierIsNarrowed()
    {
        // The `if (x)` condition identifier is a narrowable reference and must carry a flow node.
        SourceFile sf = ParseAndBind("function f(x: unknown) { if (x) { } }");
        Node? ifStmt = FindFirst(sf.AsNode(), SyntaxKind.IfStatement);
        Assert.NotNull(ifStmt);
        Node? cond = ifStmt!.Expression();
        Assert.NotNull(cond);
        Assert.Equal(SyntaxKind.Identifier, cond!.Kind);
        Assert.NotNull(cond.AsIdentifier().FlowNode);
    }

    // ── diagnostics ──

    [Fact]
    public void RedeclaredBlockScopedVariable_ReportsBindDiagnostic()
    {
        SourceFile sf = ParseAndBind("let a; let a;");
        Assert.NotNull(sf.BindDiagnostics());
        Assert.NotEmpty(sf.BindDiagnostics()!);
    }

    // ── idempotency (BindOnce) ──

    [Fact]
    public void BindSourceFile_IsIdempotent()
    {
        SourceFile sf = global::Ts.Parser.Parser.ParseSourceFile(Opts(), "var x = 1; function foo() {}", ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(sf);
        Assert.True(sf.IsBound());
        int localsAfterFirst = sf.Locals!.Count;
        int symbolCountAfterFirst = sf.SymbolCount;

        // Second bind must be a no-op (does not re-populate the tables).
        global::Ts.Binder.Binder.BindSourceFile(sf);
        Assert.Equal(localsAfterFirst, sf.Locals!.Count);
        Assert.Equal(symbolCountAfterFirst, sf.SymbolCount);
    }

    // ── clean programs produce no bind diagnostics ──

    [Fact]
    public void CleanProgram_HasNoBindDiagnostics()
    {
        SourceFile sf = ParseAndBind("class C {}\nfunction f() { return 1; }\nconst x = f();");
        Assert.True((sf.BindDiagnostics()?.Length ?? 0) == 0);
    }
}
