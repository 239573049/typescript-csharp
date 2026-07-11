// Tests for the Declarations cluster of Ts.Parser.Parser (internal/parser/parser.go declaration
// family: parseClass/Interface/Enum/Module/Function/TypeAlias declarations, import/export, class &
// type members, modifiers, decorators, type & value parameters, binding patterns, variable stmts).
//
// SCOPE NOTE (phase 3): These drive the public ParseSourceFile entry end-to-end and assert the
// produced AST kind trees. Declaration bodies route into the Statements/Expressions/Types clusters
// (all landed), so the declaration structure surface is fully reachable. Deep JSDoc @template/@callback
// reparse of declarations (gatherTypeParameters / parseTemplateTagTypeParameters) is still a stub in
// this cluster and is deferred to the phase-3 conformance baselines. See the port report.

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.Parser.Tests;

public class ParserDeclarationTests
{
    private static SourceFileParseOptions Opts(string fileName = "/test.ts") =>
        new() { FileName = fileName, Path = new Path(fileName) };

    private static SourceFile Parse(string text, ScriptKind kind = ScriptKind.TS) =>
        global::Ts.Parser.Parser.ParseSourceFile(Opts(), text, kind);

    private static Node[] ParseOk(string text, ScriptKind kind = ScriptKind.TS)
    {
        SourceFile sf = Parse(text, kind);
        Assert.Empty(sf.Diagnostics() ?? System.Array.Empty<Diagnostic>());
        return sf.Statements!.Nodes;
    }

    private static Node ParseOneOk(string text, ScriptKind kind = ScriptKind.TS) =>
        Assert.Single(ParseOk(text, kind));

    private static string NameText(Node decl) => decl.Name()!.AsIdentifier().Text;

    // ── classes: shape ──

    [Fact]
    public void ClassDeclaration_Empty()
    {
        Node s = ParseOneOk("class C {}");
        Assert.Equal(SyntaxKind.ClassDeclaration, s.Kind);
        ClassDeclaration c = s.AsClassDeclaration();
        Assert.Equal("C", NameText(s));
        Assert.Null(c.TypeParameters);
        Assert.Null(c.HeritageClauses);
        Assert.Empty(c.Members!.Nodes);
        Assert.Null(s.Modifiers());
    }

    [Fact]
    public void ClassDeclaration_AnonymousName_IsNull()
    {
        // A missing class name is a parse-legal recovery (the checker reports it later).
        Node s = ParseOneOk("class {}");
        Assert.Equal(SyntaxKind.ClassDeclaration, s.Kind);
        Assert.Null(s.Name());
    }

    [Fact]
    public void ClassDeclaration_TypeParameters()
    {
        Node s = ParseOneOk("class C<T, U> {}");
        Node[] tps = s.AsClassDeclaration().TypeParameters!.Nodes;
        Assert.Equal(2, tps.Length);
        Assert.Equal(SyntaxKind.TypeParameter, tps[0].Kind);
        Assert.Equal("T", tps[0].Name()!.AsIdentifier().Text);
    }

    [Fact]
    public void ClassDeclaration_TypeParameterWithConstraintAndDefault()
    {
        Node s = ParseOneOk("class C<T extends U = V> {}");
        TypeParameterDeclaration tp = s.AsClassDeclaration().TypeParameters!.Nodes[0].AsTypeParameterDeclaration();
        Assert.NotNull(tp.Constraint);
        Assert.NotNull(tp.DefaultType);
    }

    // ── classes: heritage ──

    [Fact]
    public void ClassDeclaration_Extends()
    {
        Node s = ParseOneOk("class C extends B {}");
        Node[] clauses = s.AsClassDeclaration().HeritageClauses!.Nodes;
        HeritageClause h = Assert.Single(clauses).AsHeritageClause();
        Assert.Equal(SyntaxKind.ExtendsKeyword, h.Token);
        Node expr = Assert.Single(h.Types!.Nodes);
        Assert.Equal(SyntaxKind.ExpressionWithTypeArguments, expr.Kind);
    }

    [Fact]
    public void ClassDeclaration_ExtendsWithTypeArguments()
    {
        Node s = ParseOneOk("class C extends B<T> {}");
        HeritageClause h = s.AsClassDeclaration().HeritageClauses!.Nodes[0].AsHeritageClause();
        ExpressionWithTypeArguments ewta = h.Types!.Nodes[0].AsExpressionWithTypeArguments();
        Assert.NotNull(ewta.TypeArguments);
        Assert.Single(ewta.TypeArguments!.Nodes);
    }

    [Fact]
    public void ClassDeclaration_ExtendsAndImplements()
    {
        Node s = ParseOneOk("class C extends B implements I, J {}");
        Node[] clauses = s.AsClassDeclaration().HeritageClauses!.Nodes;
        Assert.Equal(2, clauses.Length);
        Assert.Equal(SyntaxKind.ExtendsKeyword, clauses[0].AsHeritageClause().Token);
        Assert.Equal(SyntaxKind.ImplementsKeyword, clauses[1].AsHeritageClause().Token);
        Assert.Equal(2, clauses[1].AsHeritageClause().Types!.Nodes.Length);
    }

    // ── classes: modifiers & decorators ──

    [Fact]
    public void ClassDeclaration_AbstractModifier()
    {
        Node s = ParseOneOk("abstract class C {}");
        Node mod = Assert.Single(s.Modifiers()!.Nodes);
        Assert.Equal(SyntaxKind.AbstractKeyword, mod.Kind);
    }

    [Fact]
    public void ClassDeclaration_ExportModifier()
    {
        Node s = ParseOneOk("export class C {}");
        Assert.Equal(SyntaxKind.ExportKeyword, Assert.Single(s.Modifiers()!.Nodes).Kind);
    }

    [Fact]
    public void ClassDeclaration_ExportDefaultModifiers()
    {
        Node s = ParseOneOk("export default class C {}");
        Node[] mods = s.Modifiers()!.Nodes;
        Assert.Equal(2, mods.Length);
        Assert.Equal(SyntaxKind.ExportKeyword, mods[0].Kind);
        Assert.Equal(SyntaxKind.DefaultKeyword, mods[1].Kind);
    }

    [Fact]
    public void ClassDeclaration_SingleDecorator()
    {
        Node s = ParseOneOk("@sealed class C {}");
        Node dec = Assert.Single(s.Modifiers()!.Nodes);
        Assert.Equal(SyntaxKind.Decorator, dec.Kind);
        Assert.Equal(SyntaxKind.Identifier, dec.AsDecorator().Expression!.Kind);
    }

    [Fact]
    public void ClassDeclaration_MultipleDecoratorsAndModifier()
    {
        Node s = ParseOneOk("@a @b export class C {}");
        Node[] mods = s.Modifiers()!.Nodes;
        Assert.Equal(3, mods.Length);
        Assert.Equal(SyntaxKind.Decorator, mods[0].Kind);
        Assert.Equal(SyntaxKind.Decorator, mods[1].Kind);
        Assert.Equal(SyntaxKind.ExportKeyword, mods[2].Kind);
    }

    [Fact]
    public void ClassDeclaration_DecoratorWithCallExpression()
    {
        Node s = ParseOneOk("@dec(1) class C {}");
        Node dec = Assert.Single(s.Modifiers()!.Nodes);
        Assert.Equal(SyntaxKind.CallExpression, dec.AsDecorator().Expression!.Kind);
    }

    // ── class members ──

    [Fact]
    public void ClassDeclaration_SemicolonElement()
    {
        Node s = ParseOneOk("class C { ; }");
        Node m = Assert.Single(s.AsClassDeclaration().Members!.Nodes);
        Assert.Equal(SyntaxKind.SemicolonClassElement, m.Kind);
    }

    [Fact]
    public void ClassDeclaration_PropertyAndMethod()
    {
        Node s = ParseOneOk("class C { x: number; m() {} }");
        Node[] members = s.AsClassDeclaration().Members!.Nodes;
        Assert.Equal(2, members.Length);
        Assert.Equal(SyntaxKind.PropertyDeclaration, members[0].Kind);
        Assert.Equal("x", members[0].Name()!.AsIdentifier().Text);
        Assert.NotNull(members[0].AsPropertyDeclaration().Type);
        Assert.Equal(SyntaxKind.MethodDeclaration, members[1].Kind);
    }

    [Fact]
    public void ClassDeclaration_PropertyWithInitializer()
    {
        Node s = ParseOneOk("class C { x = 1; }");
        PropertyDeclaration p = s.AsClassDeclaration().Members!.Nodes[0].AsPropertyDeclaration();
        Assert.Equal(SyntaxKind.NumericLiteral, p.Initializer!.Kind);
    }

    [Fact]
    public void ClassDeclaration_GetterAndSetter()
    {
        Node s = ParseOneOk("class C { get x() {} set x(v) {} }");
        Node[] members = s.AsClassDeclaration().Members!.Nodes;
        Assert.Equal(SyntaxKind.GetAccessor, members[0].Kind);
        Assert.Equal(SyntaxKind.SetAccessor, members[1].Kind);
    }

    [Fact]
    public void ClassDeclaration_Constructor()
    {
        Node s = ParseOneOk("class C { constructor(x) {} }");
        Node ctor = Assert.Single(s.AsClassDeclaration().Members!.Nodes);
        Assert.Equal(SyntaxKind.Constructor, ctor.Kind);
        Assert.Single(ctor.AsConstructorDeclaration().Parameters!.Nodes);
    }

    [Fact]
    public void ClassDeclaration_IndexSignature()
    {
        Node s = ParseOneOk("class C { [k: string]: number; }");
        Node idx = Assert.Single(s.AsClassDeclaration().Members!.Nodes);
        Assert.Equal(SyntaxKind.IndexSignature, idx.Kind);
    }

    [Fact]
    public void ClassDeclaration_StaticBlock()
    {
        Node s = ParseOneOk("class C { static {} }");
        Node blk = Assert.Single(s.AsClassDeclaration().Members!.Nodes);
        Assert.Equal(SyntaxKind.ClassStaticBlockDeclaration, blk.Kind);
    }

    [Fact]
    public void ClassDeclaration_MemberModifiersAndAsteriskMethod()
    {
        Node s = ParseOneOk("class C { public static readonly x = 1; *gen() {} }");
        Node[] members = s.AsClassDeclaration().Members!.Nodes;
        Node[] mods = members[0].Modifiers()!.Nodes;
        Assert.Equal(3, mods.Length);
        Assert.Equal(SyntaxKind.PublicKeyword, mods[0].Kind);
        Assert.Equal(SyntaxKind.StaticKeyword, mods[1].Kind);
        Assert.Equal(SyntaxKind.ReadonlyKeyword, mods[2].Kind);
        Assert.Equal(SyntaxKind.MethodDeclaration, members[1].Kind);
    }

    [Fact]
    public void ClassDeclaration_DecoratedMember()
    {
        Node s = ParseOneOk("class C { @dec m() {} }");
        Node m = Assert.Single(s.AsClassDeclaration().Members!.Nodes);
        Assert.Equal(SyntaxKind.Decorator, m.Modifiers()!.Nodes[0].Kind);
    }

    // ── interfaces ──

    [Fact]
    public void InterfaceDeclaration_Empty()
    {
        Node s = ParseOneOk("interface I {}");
        Assert.Equal(SyntaxKind.InterfaceDeclaration, s.Kind);
        Assert.Equal("I", NameText(s));
        Assert.Empty(s.AsInterfaceDeclaration().Members!.Nodes);
    }

    [Fact]
    public void InterfaceDeclaration_ExtendsAndTypeParameters()
    {
        Node s = ParseOneOk("interface I<T> extends A, B {}");
        InterfaceDeclaration i = s.AsInterfaceDeclaration();
        Assert.Single(i.TypeParameters!.Nodes);
        HeritageClause h = Assert.Single(i.HeritageClauses!.Nodes).AsHeritageClause();
        Assert.Equal(SyntaxKind.ExtendsKeyword, h.Token);
        Assert.Equal(2, h.Types!.Nodes.Length);
    }

    [Fact]
    public void InterfaceDeclaration_Members()
    {
        Node s = ParseOneOk("interface I { x: number; m(): void; (): I; new (): I; [k: string]: any; }");
        Node[] members = s.AsInterfaceDeclaration().Members!.Nodes;
        Assert.Equal(5, members.Length);
        Assert.Equal(SyntaxKind.PropertySignature, members[0].Kind);
        Assert.Equal(SyntaxKind.MethodSignature, members[1].Kind);
        Assert.Equal(SyntaxKind.CallSignature, members[2].Kind);
        Assert.Equal(SyntaxKind.ConstructSignature, members[3].Kind);
        Assert.Equal(SyntaxKind.IndexSignature, members[4].Kind);
    }

    [Fact]
    public void InterfaceDeclaration_OptionalPropertySignature()
    {
        Node s = ParseOneOk("interface I { x?: number; }");
        Assert.Equal(SyntaxKind.PropertySignature, s.AsInterfaceDeclaration().Members!.Nodes[0].Kind);
    }

    // ── enums ──

    [Fact]
    public void EnumDeclaration_Empty()
    {
        Node s = ParseOneOk("enum E {}");
        Assert.Equal(SyntaxKind.EnumDeclaration, s.Kind);
        Assert.Equal("E", NameText(s));
        Assert.Empty(s.AsEnumDeclaration().Members!.Nodes);
    }

    [Fact]
    public void EnumDeclaration_MembersWithInitializers()
    {
        Node s = ParseOneOk("enum E { A, B = 2, C }");
        Node[] members = s.AsEnumDeclaration().Members!.Nodes;
        Assert.Equal(3, members.Length);
        Assert.All(members, m => Assert.Equal(SyntaxKind.EnumMember, m.Kind));
        Assert.Null(members[0].AsEnumMember().Initializer);
        Assert.Equal(SyntaxKind.NumericLiteral, members[1].AsEnumMember().Initializer!.Kind);
    }

    [Fact]
    public void EnumDeclaration_ConstEnum()
    {
        Node s = ParseOneOk("const enum E { A }");
        Assert.Equal(SyntaxKind.EnumDeclaration, s.Kind);
        Assert.Equal(SyntaxKind.ConstKeyword, Assert.Single(s.Modifiers()!.Nodes).Kind);
    }

    // ── modules / namespaces ──

    [Fact]
    public void NamespaceDeclaration_Block()
    {
        Node s = ParseOneOk("namespace N { }");
        Assert.Equal(SyntaxKind.ModuleDeclaration, s.Kind);
        ModuleDeclaration m = s.AsModuleDeclaration();
        Assert.Equal(SyntaxKind.NamespaceKeyword, m.Keyword);
        Assert.Equal(SyntaxKind.ModuleBlock, m.Body!.Kind);
    }

    [Fact]
    public void ModuleDeclaration_Keyword()
    {
        Node s = ParseOneOk("module N { }");
        Assert.Equal(SyntaxKind.ModuleKeyword, s.AsModuleDeclaration().Keyword);
    }

    [Fact]
    public void NamespaceDeclaration_DottedIsNested()
    {
        Node s = ParseOneOk("namespace A.B.C { }");
        ModuleDeclaration a = s.AsModuleDeclaration();
        Assert.Equal(SyntaxKind.ModuleDeclaration, a.Body!.Kind);
        ModuleDeclaration b = a.Body.AsModuleDeclaration();
        Assert.Equal(SyntaxKind.ModuleDeclaration, b.Body!.Kind);
    }

    [Fact]
    public void ModuleDeclaration_AmbientExternalModule()
    {
        Node s = ParseOneOk("declare module \"fs\" { }");
        ModuleDeclaration m = s.AsModuleDeclaration();
        Assert.Equal(SyntaxKind.ModuleKeyword, m.Keyword);
        Assert.Equal(SyntaxKind.StringLiteral, m.Name()!.Kind);
        Assert.Equal(SyntaxKind.DeclareKeyword, Assert.Single(s.Modifiers()!.Nodes).Kind);
    }

    [Fact]
    public void ModuleDeclaration_GlobalAugmentation()
    {
        Node s = ParseOneOk("declare global { }");
        ModuleDeclaration m = s.AsModuleDeclaration();
        Assert.Equal(SyntaxKind.GlobalKeyword, m.Keyword);
    }

    // ── functions ──

    [Fact]
    public void FunctionDeclaration_EmptyBody()
    {
        Node s = ParseOneOk("function f() {}");
        Assert.Equal(SyntaxKind.FunctionDeclaration, s.Kind);
        FunctionDeclaration f = s.AsFunctionDeclaration();
        Assert.Equal("f", NameText(s));
        Assert.Empty(f.Parameters!.Nodes);
        Assert.Equal(SyntaxKind.Block, s.Body()!.Kind);
    }

    [Fact]
    public void FunctionDeclaration_Parameters()
    {
        Node s = ParseOneOk("function f(a, b, c) {}");
        Assert.Equal(3, s.AsFunctionDeclaration().Parameters!.Nodes.Length);
    }

    [Fact]
    public void FunctionDeclaration_GenericWithReturnType()
    {
        Node s = ParseOneOk("function f<T>(x: T): T {}");
        FunctionDeclaration f = s.AsFunctionDeclaration();
        Assert.Single(f.TypeParameters!.Nodes);
        Assert.NotNull(f.Type);
    }

    [Fact]
    public void FunctionDeclaration_AsyncGenerator()
    {
        Node s = ParseOneOk("async function* f() {}");
        Assert.Equal(SyntaxKind.AsyncKeyword, Assert.Single(s.Modifiers()!.Nodes).Kind);
    }

    [Fact]
    public void FunctionDeclaration_OverloadSignatureHasNoBody()
    {
        Node s = ParseOneOk("declare function f(): void;");
        FunctionDeclaration f = s.AsFunctionDeclaration();
        Assert.Null(f.Body);
        Assert.NotNull(f.Type);
    }

    // ── type alias ──

    [Fact]
    public void TypeAliasDeclaration_Simple()
    {
        Node s = ParseOneOk("type T = number;");
        Assert.Equal(SyntaxKind.TypeAliasDeclaration, s.Kind);
        Assert.Equal("T", NameText(s));
        Assert.NotNull(s.AsTypeAliasDeclaration().Type);
    }

    [Fact]
    public void TypeAliasDeclaration_Generic()
    {
        Node s = ParseOneOk("type Box<T> = { value: T };");
        Assert.Single(s.AsTypeAliasDeclaration().TypeParameters!.Nodes);
    }

    // ── variable statements ──

    [Theory]
    [InlineData("var x;", NodeFlags.None)]
    [InlineData("let x;", NodeFlags.Let)]
    [InlineData("const x = 1;", NodeFlags.Const)]
    public void VariableStatement_Kinds(string text, NodeFlags expectedFlag)
    {
        Node s = ParseOneOk(text);
        Assert.Equal(SyntaxKind.VariableStatement, s.Kind);
        Node list = s.AsVariableStatement().DeclarationList!;
        Assert.Equal(SyntaxKind.VariableDeclarationList, list.Kind);
        if (expectedFlag != NodeFlags.None)
        {
            Assert.True((list.Flags & expectedFlag) != 0);
        }
        Assert.Single(list.AsVariableDeclarationList().Declarations!.Nodes);
    }

    [Fact]
    public void VariableStatement_MultipleDeclarations()
    {
        Node s = ParseOneOk("let a, b = 1, c;");
        Node[] decls = s.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes;
        Assert.Equal(3, decls.Length);
        Assert.All(decls, d => Assert.Equal(SyntaxKind.VariableDeclaration, d.Kind));
        Assert.NotNull(decls[1].AsVariableDeclaration().Initializer);
    }

    [Fact]
    public void VariableStatement_TypedAndInitialized()
    {
        Node s = ParseOneOk("const x: number = 1;");
        VariableDeclaration d = s.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        Assert.NotNull(d.Type);
        Assert.Equal(SyntaxKind.NumericLiteral, d.Initializer!.Kind);
    }

    [Fact]
    public void VariableStatement_ArrayBindingPattern()
    {
        Node s = ParseOneOk("const [a, , b] = x;");
        VariableDeclaration d = s.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        Assert.Equal(SyntaxKind.ArrayBindingPattern, d.Name()!.Kind);
    }

    [Fact]
    public void VariableStatement_ObjectBindingPattern()
    {
        Node s = ParseOneOk("const { a, b: c } = x;");
        VariableDeclaration d = s.AsVariableStatement().DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].AsVariableDeclaration();
        Assert.Equal(SyntaxKind.ObjectBindingPattern, d.Name()!.Kind);
    }

    // ── imports ──

    [Fact]
    public void ImportDeclaration_DefaultImport()
    {
        Node s = ParseOneOk("import x from \"m\";");
        Assert.Equal(SyntaxKind.ImportDeclaration, s.Kind);
        ImportDeclaration i = s.AsImportDeclaration();
        ImportClause ic = i.ImportClause!.AsImportClause();
        Assert.Equal("x", ic.Name()!.AsIdentifier().Text);
        Assert.Null(ic.NamedBindings);
        Assert.Equal(SyntaxKind.StringLiteral, i.ModuleSpecifier!.Kind);
    }

    [Fact]
    public void ImportDeclaration_NamespaceImport()
    {
        Node s = ParseOneOk("import * as ns from \"m\";");
        ImportClause ic = s.AsImportDeclaration().ImportClause!.AsImportClause();
        Assert.Equal(SyntaxKind.NamespaceImport, ic.NamedBindings!.Kind);
    }

    [Fact]
    public void ImportDeclaration_NamedImports()
    {
        Node s = ParseOneOk("import { a, b as c } from \"m\";");
        ImportClause ic = s.AsImportDeclaration().ImportClause!.AsImportClause();
        Node named = ic.NamedBindings!;
        Assert.Equal(SyntaxKind.NamedImports, named.Kind);
        Node[] elements = named.AsNamedImports().Elements!.Nodes;
        Assert.Equal(2, elements.Length);
        Assert.All(elements, e => Assert.Equal(SyntaxKind.ImportSpecifier, e.Kind));
    }

    [Fact]
    public void ImportDeclaration_TypeOnly()
    {
        Node s = ParseOneOk("import type { a } from \"m\";");
        ImportClause ic = s.AsImportDeclaration().ImportClause!.AsImportClause();
        Assert.Equal(SyntaxKind.TypeKeyword, ic.PhaseModifier);
    }

    [Fact]
    public void ImportDeclaration_SideEffectOnly()
    {
        Node s = ParseOneOk("import \"m\";");
        Assert.Null(s.AsImportDeclaration().ImportClause);
    }

    [Fact]
    public void ImportDeclaration_DefaultAndNamed()
    {
        Node s = ParseOneOk("import d, { a } from \"m\";");
        ImportClause ic = s.AsImportDeclaration().ImportClause!.AsImportClause();
        Assert.Equal("d", ic.Name()!.AsIdentifier().Text);
        Assert.Equal(SyntaxKind.NamedImports, ic.NamedBindings!.Kind);
    }

    [Fact]
    public void ImportEqualsDeclaration_Require()
    {
        Node s = ParseOneOk("import x = require(\"m\");");
        Assert.Equal(SyntaxKind.ImportEqualsDeclaration, s.Kind);
    }

    [Fact]
    public void ImportEqualsDeclaration_EntityName()
    {
        Node s = ParseOneOk("import x = A.B;");
        Assert.Equal(SyntaxKind.ImportEqualsDeclaration, s.Kind);
    }

    // ── exports ──

    [Fact]
    public void ExportDeclaration_NamedNoModuleSpecifier()
    {
        Node s = ParseOneOk("export { a, b };");
        Assert.Equal(SyntaxKind.ExportDeclaration, s.Kind);
        ExportDeclaration e = s.AsExportDeclaration();
        Assert.Equal(SyntaxKind.NamedExports, e.ExportClause!.Kind);
        Assert.Null(e.ModuleSpecifier);
    }

    [Fact]
    public void ExportDeclaration_ReExport()
    {
        Node s = ParseOneOk("export { a } from \"m\";");
        ExportDeclaration e = s.AsExportDeclaration();
        Assert.Equal(SyntaxKind.NamedExports, e.ExportClause!.Kind);
        Assert.Equal(SyntaxKind.StringLiteral, e.ModuleSpecifier!.Kind);
    }

    [Fact]
    public void ExportDeclaration_StarReExport()
    {
        Node s = ParseOneOk("export * from \"m\";");
        ExportDeclaration e = s.AsExportDeclaration();
        Assert.Null(e.ExportClause);
        Assert.Equal(SyntaxKind.StringLiteral, e.ModuleSpecifier!.Kind);
    }

    [Fact]
    public void ExportDeclaration_StarAsNamespace()
    {
        Node s = ParseOneOk("export * as ns from \"m\";");
        Assert.Equal(SyntaxKind.NamespaceExport, s.AsExportDeclaration().ExportClause!.Kind);
    }

    [Fact]
    public void ExportDeclaration_TypeOnly()
    {
        Node s = ParseOneOk("export type { a } from \"m\";");
        Assert.True(s.AsExportDeclaration().IsTypeOnly);
    }

    [Fact]
    public void ExportAssignment_ExportEquals()
    {
        Node s = ParseOneOk("export = x;");
        Assert.Equal(SyntaxKind.ExportAssignment, s.Kind);
        Assert.True(s.AsExportAssignment().IsExportEquals);
    }

    [Fact]
    public void ExportAssignment_ExportDefaultExpression()
    {
        Node s = ParseOneOk("export default x;");
        ExportAssignment e = s.AsExportAssignment();
        Assert.False(e.IsExportEquals);
        Assert.Equal(SyntaxKind.Identifier, e.Expression!.Kind);
    }

    [Fact]
    public void NamespaceExportDeclaration()
    {
        Node s = ParseOneOk("export as namespace N;");
        Assert.Equal(SyntaxKind.NamespaceExportDeclaration, s.Kind);
        Assert.Equal("N", NameText(s));
    }

    // ── parameters ──

    private static ParameterDeclaration OnlyParam(string funcText)
    {
        Node s = ParseOneOk(funcText);
        return Assert.Single(s.AsFunctionDeclaration().Parameters!.Nodes).AsParameterDeclaration();
    }

    [Fact]
    public void Parameter_Typed()
    {
        ParameterDeclaration p = OnlyParam("function f(a: number) {}");
        Assert.Equal("a", p.Name()!.AsIdentifier().Text);
        Assert.NotNull(p.Type);
    }

    [Fact]
    public void Parameter_Optional()
    {
        ParameterDeclaration p = OnlyParam("function f(a?: number) {}");
        Assert.NotNull(p.QuestionToken);
    }

    [Fact]
    public void Parameter_Rest()
    {
        ParameterDeclaration p = OnlyParam("function f(...a: number[]) {}");
        Assert.NotNull(p.DotDotDotToken);
    }

    [Fact]
    public void Parameter_Default()
    {
        ParameterDeclaration p = OnlyParam("function f(a = 1) {}");
        Assert.Equal(SyntaxKind.NumericLiteral, p.Initializer!.Kind);
    }

    [Fact]
    public void Parameter_This()
    {
        ParameterDeclaration p = OnlyParam("function f(this: C) {}");
        // The `this` parameter name is represented as an Identifier with text "this".
        Assert.Equal(SyntaxKind.Identifier, p.Name()!.Kind);
        Assert.Equal("this", p.Name()!.AsIdentifier().Text);
        Assert.NotNull(p.Type);
    }

    [Fact]
    public void Parameter_ParameterPropertyModifiers()
    {
        Node s = ParseOneOk("class C { constructor(private readonly x: number) {} }");
        ParameterDeclaration p = Assert.Single(
            s.AsClassDeclaration().Members!.Nodes[0].AsConstructorDeclaration().Parameters!.Nodes).AsParameterDeclaration();
        Node[] mods = p.Modifiers()!.Nodes;
        Assert.Equal(2, mods.Length);
        Assert.Equal(SyntaxKind.PrivateKeyword, mods[0].Kind);
        Assert.Equal(SyntaxKind.ReadonlyKeyword, mods[1].Kind);
    }

    // ── mixed: multiple top-level declarations parse in order ──

    [Fact]
    public void MultipleDeclarations_ParseInOrder()
    {
        Node[] statements = ParseOk("class C {} interface I {} enum E {} function f() {}");
        Assert.Equal(4, statements.Length);
        Assert.Equal(SyntaxKind.ClassDeclaration, statements[0].Kind);
        Assert.Equal(SyntaxKind.InterfaceDeclaration, statements[1].Kind);
        Assert.Equal(SyntaxKind.EnumDeclaration, statements[2].Kind);
        Assert.Equal(SyntaxKind.FunctionDeclaration, statements[3].Kind);
    }

    // ── JS-file smoke: a class parses cleanly under ScriptKind.JS (checkJSSyntax runs) ──

    [Fact]
    public void ClassWithMethod_ParsesCleanlyInJavaScriptFile()
    {
        Node s = ParseOneOk("class C { m() {} }", ScriptKind.JS);
        Assert.Equal(SyntaxKind.ClassDeclaration, s.Kind);
    }
}
