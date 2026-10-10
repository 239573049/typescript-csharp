// Ported from tsgo internal/printer/namegenerator_test.go

using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Ts.TsPath;
using Xunit;

namespace Ts.Printer.Tests;

public class NameGeneratorTests
{
    // Go: parsetestutil.ParseTypeScript(text, jsx) — namegenerator_test.go always passes jsx=false.
    private static SourceFile ParseTypeScript(string text)
    {
        const string fileName = "/main.ts";
        var opts = new SourceFileParseOptions { FileName = fileName, Path = new Path(fileName) };
        SourceFile file = global::Ts.Parser.Parser.ParseSourceFile(opts, text, ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(file);
        return file;
    }

    private static NameGenerator NewGenerator(EmitContext ec)
    {
        return new NameGenerator { Context = ec, GetTextOfNode = n => n.Text() };
    }

    // namegenerator_test.go:13-26 — TestTempVariable1
    [Fact]
    public void TempVariable1()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewTempVariable();
        Node name2 = ec.Factory.NewTempVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("_a", text1);
        Assert.Equal("_b", text2);
    }

    // namegenerator_test.go:28-47 — TestTempVariable2
    [Fact]
    public void TempVariable2()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewTempVariableEx(new AutoGenerateOptions
        {
            Prefix = "A",
            Suffix = "B",
        });
        Node name2 = ec.Factory.NewTempVariableEx(new AutoGenerateOptions
        {
            Prefix = "A",
            Suffix = "B",
        });

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("A_aB", text1);
        Assert.Equal("A_bB", text2);
    }

    // namegenerator_test.go:49-61 — TestTempVariable3
    [Fact]
    public void TempVariable3()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewTempVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name1);

        Assert.Equal("_a", text1);
        Assert.Equal("_a", text2);
    }

    // namegenerator_test.go:63-78 — TestTempVariableScoped
    [Fact]
    public void TempVariableScoped()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewTempVariable();
        Node name2 = ec.Factory.NewTempVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        g.PushScope(false);
        string text2 = g.GenerateName(name2);
        g.PopScope(false);

        Assert.Equal("_a", text1);
        Assert.Equal("_a", text2);
    }

    // namegenerator_test.go:80-95 — TestTempVariableScopedReserved
    [Fact]
    public void TempVariableScopedReserved()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewTempVariableEx(new AutoGenerateOptions { Flags = GeneratedIdentifierFlags.ReservedInNestedScopes });
        Node name2 = ec.Factory.NewTempVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        g.PushScope(false);
        string text2 = g.GenerateName(name2);
        g.PopScope(false);

        Assert.Equal("_a", text1);
        Assert.Equal("_b", text2);
    }

    // namegenerator_test.go:97-110 — TestLoopVariable1
    [Fact]
    public void LoopVariable1()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewLoopVariable();
        Node name2 = ec.Factory.NewLoopVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("_i", text1);
        Assert.Equal("_a", text2);
    }

    // namegenerator_test.go:112-131 — TestLoopVariable2
    [Fact]
    public void LoopVariable2()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewLoopVariableEx(new AutoGenerateOptions
        {
            Prefix = "A",
            Suffix = "B",
        });
        Node name2 = ec.Factory.NewLoopVariableEx(new AutoGenerateOptions
        {
            Prefix = "A",
            Suffix = "B",
        });

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("A_iB", text1);
        Assert.Equal("A_aB", text2);
    }

    // namegenerator_test.go:133-145 — TestLoopVariable3
    [Fact]
    public void LoopVariable3()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewLoopVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name1);

        Assert.Equal("_i", text1);
        Assert.Equal("_i", text2);
    }

    // namegenerator_test.go:147-162 — TestLoopVariableScoped
    [Fact]
    public void LoopVariableScoped()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewLoopVariable();
        Node name2 = ec.Factory.NewLoopVariable();

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        g.PushScope(false);
        string text2 = g.GenerateName(name2);
        g.PopScope(false);

        Assert.Equal("_i", text1);
        Assert.Equal("_i", text2);
    }

    // namegenerator_test.go:164-177 — TestUniqueName1
    [Fact]
    public void UniqueName1()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewUniqueName("foo");
        Node name2 = ec.Factory.NewUniqueName("foo");

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("foo_1", text1);
        Assert.Equal("foo_2", text2);
    }

    // namegenerator_test.go:179-192 — TestUniqueName2
    [Fact]
    public void UniqueName2()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewUniqueName("foo");

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name1);

        Assert.Equal("foo_1", text1);
        // Expected to be same because GenerateName goes off object identity
        Assert.Equal("foo_1", text2);
    }

    // namegenerator_test.go:194-208 — TestUniqueNameScoped
    [Fact]
    public void UniqueNameScoped()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewUniqueName("foo");
        Node name2 = ec.Factory.NewUniqueName("foo");

        var g = NewGenerator(ec);
        Assert.Equal("foo_1", g.GenerateName(name1));

        g.PushScope(false);
        Assert.Equal("foo_2", g.GenerateName(name2)); // Matches Strada, but is incorrect
        // Assert.Equal("foo_1", g.GenerateName(name2)); // TODO: Fix after Strada port is complete.
        g.PopScope(false);
    }

    // namegenerator_test.go:210-223 — TestUniquePrivateName1
    [Fact]
    public void UniquePrivateName1()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewUniquePrivateName("#foo");
        Node name2 = ec.Factory.NewUniquePrivateName("#foo");

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("#foo_1", text1);
        Assert.Equal("#foo_2", text2);
    }

    // namegenerator_test.go:225-237 — TestUniquePrivateName2
    [Fact]
    public void UniquePrivateName2()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewUniquePrivateName("#foo");

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name1);

        Assert.Equal("#foo_1", text1);
        Assert.Equal("#foo_1", text2);
    }

    // namegenerator_test.go:239-252 — TestUniquePrivateNameScoped
    [Fact]
    public void UniquePrivateNameScoped()
    {
        var ec = EmitContext.NewEmitContext();
        Node name1 = ec.Factory.NewUniquePrivateName("#foo");
        Node name2 = ec.Factory.NewUniquePrivateName("#foo");

        var g = NewGenerator(ec);
        Assert.Equal("#foo_1", g.GenerateName(name1));

        g.PushScope(false); // private names are always reserved in nested scopes
        Assert.Equal("#foo_2", g.GenerateName(name2));
        g.PopScope(false);
    }

    // namegenerator_test.go:254-269 — TestGeneratedNameForIdentifier1
    [Fact]
    public void GeneratedNameForIdentifier1()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("function f() {}");

        Node n = file.Statements!.Nodes[0].Name()!;
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("f_1", text1);
    }

    // namegenerator_test.go:271-289 — TestGeneratedNameForIdentifier2
    [Fact]
    public void GeneratedNameForIdentifier2()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("function f() {}");

        Node n = file.Statements!.Nodes[0].Name()!;
        Node name1 = ec.Factory.NewGeneratedNameForNodeEx(n, new AutoGenerateOptions
        {
            Prefix = "a",
            Suffix = "b",
        });

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("afb", text1);
    }

    // namegenerator_test.go:291-310 — TestGeneratedNameForIdentifier3
    [Fact]
    public void GeneratedNameForIdentifier3()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("function f() {}");

        Node n = file.Statements!.Nodes[0].Name()!;
        Node name1 = ec.Factory.NewGeneratedNameForNodeEx(n, new AutoGenerateOptions
        {
            Prefix = "a",
            Suffix = "b",
        });
        Node name2 = ec.Factory.NewGeneratedNameForNode(name1);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name2);

        Assert.Equal("afb_1", text1);
    }

    // namespace reuses name if it does not collide with locals
    // namegenerator_test.go:312-328 — TestGeneratedNameForNamespace1
    [Fact]
    public void GeneratedNameForNamespace1()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("namespace foo { }");

        Node ns1 = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(ns1);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("foo", text1);
    }

    // namespace uses generated name if it collides with locals
    // namegenerator_test.go:330-346 — TestGeneratedNameForNamespace2
    [Fact]
    public void GeneratedNameForNamespace2()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("namespace foo { var foo; }");

        Node ns1 = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(ns1);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("foo_1", text1);
    }

    // avoids collisions when unscoped
    // namegenerator_test.go:348-368 — TestGeneratedNameForNamespace3
    [Fact]
    public void GeneratedNameForNamespace3()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("namespace ns1 { namespace foo { var foo; } } namespace ns2 { namespace foo { var foo; } }");

        Node ns1 = file.Statements!.Nodes[0].Body()!.Statements()![0];
        Node ns2 = file.Statements!.Nodes[1].Body()!.Statements()![0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(ns1);
        Node name2 = ec.Factory.NewGeneratedNameForNode(ns2);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("foo_1", text1);
        Assert.Equal("foo_2", text2);
    }

    // reuse name when scoped
    // namegenerator_test.go:370-396 — TestGeneratedNameForNamespace4
    [Fact]
    public void GeneratedNameForNamespace4()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("namespace ns1 { namespace foo { var foo; } } namespace ns2 { namespace foo { var foo; } }");

        Node ns1 = file.Statements!.Nodes[0].Body()!.Statements()![0];
        Node ns2 = file.Statements!.Nodes[1].Body()!.Statements()![0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(ns1);
        Node name2 = ec.Factory.NewGeneratedNameForNode(ns2);

        var g = NewGenerator(ec);
        g.PushScope(false);
        string text1 = g.GenerateName(name1);
        g.PopScope(false);

        g.PushScope(false);
        string text2 = g.GenerateName(name2);
        g.PopScope(false);

        Assert.Equal("foo_1", text1);
        Assert.Equal("foo_2", text2); // Matches Strada, but is incorrect
        // Assert.Equal("foo_1", text2); // TODO: Fix after Strada port is complete.
    }

    // namegenerator_test.go:398-416 — TestGeneratedNameForNodeCached
    [Fact]
    public void GeneratedNameForNodeCached()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("namespace foo { var foo; }");

        Node ns1 = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(ns1);
        Node name2 = ec.Factory.NewGeneratedNameForNode(ns1);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);
        string text2 = g.GenerateName(name2);

        Assert.Equal("foo_1", text1);
        Assert.Equal("foo_1", text2);
    }

    // namegenerator_test.go:418-433 — TestGeneratedNameForImport
    [Fact]
    public void GeneratedNameForImport()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("import * as foo from 'foo'");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("foo_1", text1);
    }

    // namegenerator_test.go:435-450 — TestGeneratedNameForExport
    [Fact]
    public void GeneratedNameForExport()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("export * as foo from 'foo'");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("foo_1", text1);
    }

    // namegenerator_test.go:452-467 — TestGeneratedNameForFunctionDeclaration1
    [Fact]
    public void GeneratedNameForFunctionDeclaration1()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("export function f() {}");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("f_1", text1);
    }

    // namegenerator_test.go:469-484 — TestGeneratedNameForFunctionDeclaration2
    [Fact]
    public void GeneratedNameForFunctionDeclaration2()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("export default function () {}");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("default_1", text1);
    }

    // namegenerator_test.go:486-501 — TestGeneratedNameForClassDeclaration1
    [Fact]
    public void GeneratedNameForClassDeclaration1()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("export class C {}");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("C_1", text1);
    }

    // namegenerator_test.go:503-518 — TestGeneratedNameForClassDeclaration2
    [Fact]
    public void GeneratedNameForClassDeclaration2()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("export default class {}");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("default_1", text1);
    }

    // namegenerator_test.go:520-535 — TestGeneratedNameForExportAssignment
    [Fact]
    public void GeneratedNameForExportAssignment()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("export default 0");

        Node n = file.Statements!.Nodes[0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("default_1", text1);
    }

    // namegenerator_test.go:537-552 — TestGeneratedNameForClassExpression
    [Fact]
    public void GeneratedNameForClassExpression()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("(class {})");

        Node n = file.Statements!.Nodes[0].Expression()!.Expression()!;
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("class_1", text1);
    }

    // namegenerator_test.go:554-569 — TestGeneratedNameForMethod1
    [Fact]
    public void GeneratedNameForMethod1()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("class C { m() {} }");

        Node n = file.Statements!.Nodes[0].Members()![0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("m_1", text1);
    }

    // namegenerator_test.go:571-586 — TestGeneratedNameForMethod2
    [Fact]
    public void GeneratedNameForMethod2()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("class C { 0() {} }");

        Node n = file.Statements!.Nodes[0].Members()![0];
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("_a", text1);
    }

    // namegenerator_test.go:588-603 — TestGeneratedPrivateNameForMethod
    [Fact]
    public void GeneratedPrivateNameForMethod()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("class C { m() {} }");

        Node n = file.Statements!.Nodes[0].Members()![0];
        Node name1 = ec.Factory.NewGeneratedPrivateNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("#m_1", text1);
    }

    // namegenerator_test.go:605-620 — TestGeneratedNameForComputedPropertyName
    [Fact]
    public void GeneratedNameForComputedPropertyName()
    {
        var ec = EmitContext.NewEmitContext();

        SourceFile file = ParseTypeScript("class C { [x] }");

        Node n = file.Statements!.Nodes[0].Members()![0].Name()!;
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("_a", text1);
    }

    // namegenerator_test.go:622-640 — TestGeneratedNameForOther
    [Fact]
    public void GeneratedNameForOther()
    {
        var ec = EmitContext.NewEmitContext();

        Node n = ec.Factory.NewObjectLiteralExpression(
            ec.Factory.NewNodeList([]),
            false /*multiLine*/);
        Node name1 = ec.Factory.NewGeneratedNameForNode(n);

        var g = NewGenerator(ec);
        string text1 = g.GenerateName(name1);

        Assert.Equal("_a", text1);
    }
}
