// Ported from tsgo internal/printer/printer_test.go
//
// Mapping notes (plan/05 §16):
//   * `t.Run` tables (TestEmit, TestParenthesizeBinary, TestParenthesizeBinaryExpressionMixingNullishCoalescing)
//     become [Theory] + [MemberData]; the row shapes are kept verbatim from the Go table.
//   * Every other Go test func becomes one [Fact] in Go order, with the Go `// will be parenthesized
//     on emit:` / named-argument comments preserved.
//   * The Go helpers parsetestutil.ParseTypeScript/CheckDiagnostics/MarkSyntheticRecursive and
//     emittestutil.CheckEmit are inlined below (they live in internal/testutil, not in a package
//     under port); the Go bodies are reproduced 1:1.
//   * Deliberately NOT ported (commented out in Go): the `CallExpression#12`, `ParameterDeclaration#7`
//     and `TypeParameterDeclaration#7` TestEmit rows (trailing-comma preservation, TODO Strada) and
//     `////func TestParenthesizeYield2` (printer_test.go:1621-1623).

using System;
using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Ts.TsPath;
using Xunit;

using AstNodeFactory = global::Ts.Ast.NodeFactory;

namespace Ts.Printer.Tests;

public class PrinterTests
{
    private static SourceFileParseOptions FileOpts => new() { FileName = "/file.ts", Path = new Path("/file.ts") };

    //
    // parsetestutil (internal/testutil/parsetestutil/parsetestutil.go)
    //

    // parsetestutil.go:14-22 — ParseTypeScript. The Go helper takes a jsx flag and derives the file
    // name / script kind from it.
    private static SourceFile ParseTypeScript(string text, bool jsx)
    {
        string fileName = jsx ? "/main.tsx" : "/main.ts";
        var opts = new SourceFileParseOptions { FileName = fileName, Path = new Path(fileName) };
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, jsx ? ScriptKind.Tsx : ScriptKind.TS);
    }

    // parsetestutil.go:25-33 — CheckDiagnostics (rendered through diagnosticwriter).
    private static void CheckDiagnostics(SourceFile file)
    {
        Diagnostic[] diags = file.Diagnostics() ?? [];
        if (diags.Length == 0)
        {
            return;
        }
        var b = new System.IO.StringWriter();
        global::Ts.DiagnosticWriter.DiagnosticWriter.WriteFormatDiagnostics(
            b,
            diags,
            new global::Ts.DiagnosticWriter.FormattingOptions { NewLine = "\n" });
        Assert.Fail(b.ToString());
    }

    // parsetestutil.go:36-44 — CheckDiagnosticsMessage.
    private static void CheckDiagnosticsMessage(SourceFile file, string message)
    {
        Diagnostic[] diags = file.Diagnostics() ?? [];
        if (diags.Length == 0)
        {
            return;
        }
        var b = new System.IO.StringWriter();
        global::Ts.DiagnosticWriter.DiagnosticWriter.WriteFormatDiagnostics(
            b,
            diags,
            new global::Ts.DiagnosticWriter.FormattingOptions { NewLine = "\n" });
        Assert.Fail(message + b);
    }

    // parsetestutil.go:46-84 — newSyntheticRecursiveVisitor + MarkSyntheticRecursive: sets the Loc of
    // the node and every node in its subtree to an undefined TextRange (-1,-1).
    private static void MarkSyntheticRecursive(Node node)
    {
        NodeVisitor? v = null;
        v = new NodeVisitor(
            n => v!.VisitEachChild(n),
            new AstNodeFactory(),
            new NodeVisitorHooks
            {
                VisitNode = (n, vis) =>
                {
                    if (n != null)
                    {
                        n.Loc = TextRange.Undefined;
                    }
                    return vis.VisitNode(n);
                },
                VisitToken = (n, vis) =>
                {
                    if (n != null)
                    {
                        n.Loc = TextRange.Undefined;
                    }
                    return vis.VisitNode(n);
                },
                VisitNodes = (nodes, vis) =>
                {
                    if (nodes != null)
                    {
                        nodes.Loc = TextRange.Undefined;
                    }
                    return vis.VisitNodes(nodes);
                },
                VisitModifiers = (nodes, vis) =>
                {
                    if (nodes != null)
                    {
                        nodes.Loc = TextRange.Undefined;
                    }
                    return vis.VisitModifiers(nodes);
                },
            });
        v.VisitNode(node);
    }

    //
    // emittestutil (internal/testutil/emittestutil/emittestutil.go)
    //

    // emittestutil.go:15-31 — CheckEmit.
    private static void CheckEmit(EmitContext? emitContext, SourceFile file, string expected)
    {
        Printer printer = Printer.NewPrinter(
            new PrinterOptions { NewLine = NewLineKind.LF },
            new PrintHandlers(),
            emitContext);
        string text = printer.EmitSourceFile(file);
        string actual = TrimSuffix(text, "\n");
        Assert.Equal(expected, actual);
        SourceFile file2 = ParseTypeScript(text, file.LanguageVariant == LanguageVariant.Jsx);
        CheckDiagnosticsMessage(file2, "error on reparse: ");
    }

    private static void CheckEmit(SourceFile file, string expected) => CheckEmit(null, file, expected);

    private static string TrimSuffix(string text, string suffix) =>
        text.EndsWith(suffix, StringComparison.Ordinal) ? text[..^suffix.Length] : text;

    // factory.NewSourceFile(ast.SourceFileParseOptions{FileName: "/file.ts", Path: "/file.ts"}, "",
    //     factory.NewNodeList([]*ast.Node{...}), factory.NewToken(ast.KindEndOfFile))
    private static SourceFile NewFile(AstNodeFactory factory, params Node[] statements) =>
        factory.NewSourceFile(FileOpts, "", factory.NewNodeList(statements), factory.NewToken(SyntaxKind.EndOfFile)).AsSourceFile();

    // printer_test.go:15-590 — TestEmit
    // printer_test.go:582-589 — the row body.
    [Theory]
    [MemberData(nameof(TestEmitData))]
    public void Emit(string title, string input, string output, bool jsx)
    {
        _ = title;
        SourceFile file = ParseTypeScript(input, jsx);
        CheckDiagnostics(file);
        CheckEmit(file, output);
    }

    // The TestEmit row table (printer_test.go:23-579). Fields mirror the Go anonymous struct
    // {title, input, output, jsx}; the three rows commented out in Go (CallExpression#12,
    // ParameterDeclaration#7, TypeParameterDeclaration#7) are omitted.
    public static TheoryData<string, string, string, bool> TestEmitData() => new()
    {
        { "StringLiteral#1", ";\"test\"", ";\n\"test\";", false },
        { "StringLiteral#2", ";'test'", ";\n'test';", false },
        { "NumericLiteral#1", "0", "0;", false },
        { "NumericLiteral#2", "10_000", "10000;", false },
        { "BigIntLiteral#1", "0n", "0n;", false },
        { "BigIntLiteral#2", "10_000n", "10000n;", false },
        { "BooleanLiteral#1", "true", "true;", false },
        { "BooleanLiteral#2", "false", "false;", false },
        { "NoSubstitutionTemplateLiteral", "``", "``;", false },
        { "NoSubstitutionTemplateLiteral#2", "`\n`", "`\n`;", false },
        { "RegularExpressionLiteral#1", "/a/", "/a/;", false },
        { "RegularExpressionLiteral#2", "/a/g", "/a/g;", false },
        { "NullLiteral", "null", "null;", false },
        { "ThisExpression", "this", "this;", false },
        { "SuperExpression", "super()", "super();", false },
        { "ImportExpression", "import()", "import();", false },
        { "PropertyAccess#1", "a.b", "a.b;", false },
        { "PropertyAccess#2", "a.#b", "a.#b;", false },
        { "PropertyAccess#3", "a?.b", "a?.b;", false },
        { "PropertyAccess#4", "a?.b.c", "a?.b.c;", false },
        { "PropertyAccess#5", "1..b", "1..b;", false },
        { "PropertyAccess#6", "1.0.b", "1.0.b;", false },
        { "PropertyAccess#7", "0x1.b", "0x1.b;", false },
        { "PropertyAccess#8", "0b1.b", "0b1.b;", false },
        { "PropertyAccess#9", "0o1.b", "0o1.b;", false },
        { "PropertyAccess#10", "10e1.b", "10e1.b;", false },
        { "PropertyAccess#11", "10E1.b", "10E1.b;", false },
        { "PropertyAccess#12", "a.b?.c", "a.b?.c;", false },
        { "PropertyAccess#13", "a\n.b", "a\n    .b;", false },
        { "PropertyAccess#14", "a.\nb", "a.\n    b;", false },
        { "ElementAccess#1", "a[b]", "a[b];", false },
        { "ElementAccess#2", "a?.[b]", "a?.[b];", false },
        { "ElementAccess#3", "a?.[b].c", "a?.[b].c;", false },
        { "CallExpression#1", "a()", "a();", false },
        { "CallExpression#2", "a<T>()", "a<T>();", false },
        { "CallExpression#3", "a(b)", "a(b);", false },
        { "CallExpression#4", "a<T>(b)", "a<T>(b);", false },
        { "CallExpression#5", "a(b).c", "a(b).c;", false },
        { "CallExpression#6", "a<T>(b).c", "a<T>(b).c;", false },
        { "CallExpression#7", "a?.(b)", "a?.(b);", false },
        { "CallExpression#8", "a?.<T>(b)", "a?.<T>(b);", false },
        { "CallExpression#9", "a?.(b).c", "a?.(b).c;", false },
        { "CallExpression#10", "a?.<T>(b).c", "a?.<T>(b).c;", false },
        { "CallExpression#11", "a<T, U>()", "a<T, U>();", false },
        { "CallExpression#13", "a?.b()", "a?.b();", false },
        { "NewExpression#1", "new a", "new a;", false },
        { "NewExpression#2", "new a.b", "new a.b;", false },
        { "NewExpression#3", "new a()", "new a();", false },
        { "NewExpression#4", "new a.b()", "new a.b();", false },
        { "NewExpression#5", "new a<T>()", "new a<T>();", false },
        { "NewExpression#6", "new a.b<T>()", "new a.b<T>();", false },
        { "NewExpression#7", "new a(b)", "new a(b);", false },
        { "NewExpression#8", "new a.b(c)", "new a.b(c);", false },
        { "NewExpression#9", "new a<T>(b)", "new a<T>(b);", false },
        { "NewExpression#10", "new a.b<T>(c)", "new a.b<T>(c);", false },
        { "NewExpression#11", "new a(b).c", "new a(b).c;", false },
        { "NewExpression#12", "new a<T>(b).c", "new a<T>(b).c;", false },
        { "TaggedTemplateExpression#1", "tag``", "tag ``;", false },
        { "TaggedTemplateExpression#2", "tag<T>``", "tag<T> ``;", false },
        { "TypeAssertionExpression#1", "<T>a", "<T>a;", false },
        { "FunctionExpression#1", "(function(){})", "(function () { });", false },
        { "FunctionExpression#2", "(function f(){})", "(function f() { });", false },
        { "FunctionExpression#3", "(function*f(){})", "(function* f() { });", false },
        { "FunctionExpression#4", "(async function f(){})", "(async function f() { });", false },
        { "FunctionExpression#5", "(async function*f(){})", "(async function* f() { });", false },
        { "FunctionExpression#6", "(function<T>(){})", "(function <T>() { });", false },
        { "FunctionExpression#7", "(function(a){})", "(function (a) { });", false },
        { "FunctionExpression#8", "(function():T{})", "(function (): T { });", false },
        { "ArrowFunction#1", "a=>{}", "a => { };", false },
        { "ArrowFunction#2", "()=>{}", "() => { };", false },
        { "ArrowFunction#3", "(a)=>{}", "(a) => { };", false },
        { "ArrowFunction#4", "<T>(a)=>{}", "<T>(a) => { };", false },
        { "ArrowFunction#5", "async a=>{}", "async (a) => { };", false },
        { "ArrowFunction#6", "async()=>{}", "async () => { };", false },
        { "ArrowFunction#7", "async<T>()=>{}", "async <T>() => { };", false },
        { "ArrowFunction#8", "():T=>{}", "(): T => { };", false },
        { "ArrowFunction#9", "()=>a", "() => a;", false },
        { "DeleteExpression", "delete a", "delete a;", false },
        { "TypeOfExpression", "typeof a", "typeof a;", false },
        { "VoidExpression", "void a", "void a;", false },
        { "AwaitExpression", "await a", "await a;", false },
        { "PrefixUnaryExpression#1", "+a", "+a;", false },
        { "PrefixUnaryExpression#2", "++a", "++a;", false },
        { "PrefixUnaryExpression#3", "+ +a", "+ +a;", false },
        { "PrefixUnaryExpression#4", "+ ++a", "+ ++a;", false },
        { "PrefixUnaryExpression#5", "-a", "-a;", false },
        { "PrefixUnaryExpression#6", "--a", "--a;", false },
        { "PrefixUnaryExpression#7", "- -a", "- -a;", false },
        { "PrefixUnaryExpression#8", "- --a", "- --a;", false },
        { "PrefixUnaryExpression#9", "+-a", "+-a;", false },
        { "PrefixUnaryExpression#10", "+--a", "+--a;", false },
        { "PrefixUnaryExpression#11", "-+a", "-+a;", false },
        { "PrefixUnaryExpression#12", "-++a", "-++a;", false },
        { "PrefixUnaryExpression#13", "~a", "~a;", false },
        { "PrefixUnaryExpression#14", "!a", "!a;", false },
        { "PostfixUnaryExpression#1", "a++", "a++;", false },
        { "PostfixUnaryExpression#2", "a--", "a--;", false },
        { "BinaryExpression#1", "a,b", "a, b;", false },
        { "BinaryExpression#2", "a+b", "a + b;", false },
        { "BinaryExpression#3", "a**b", "a ** b;", false },
        { "BinaryExpression#4", "a instanceof b", "a instanceof b;", false },
        { "BinaryExpression#5", "a in b", "a in b;", false },
        { "BinaryExpression#6", "a\n&& b", "a\n    && b;", false },
        { "BinaryExpression#7", "a &&\nb", "a &&\n    b;", false },
        { "ConditionalExpression#1", "a?b:c", "a ? b : c;", false },
        { "ConditionalExpression#2", "a\n?b:c", "a\n    ? b : c;", false },
        { "ConditionalExpression#3", "a?\nb:c", "a ?\n    b : c;", false },
        { "ConditionalExpression#4", "a?b\n:c", "a ? b\n    : c;", false },
        { "ConditionalExpression#5", "a?b:\nc", "a ? b :\n    c;", false },
        { "TemplateExpression#1", "`a${b}c`", "`a${b}c`;", false },
        { "TemplateExpression#2", "`a${b}c${d}e`", "`a${b}c${d}e`;", false },
        { "YieldExpression#1", "(function*() { yield })", "(function* () { yield; });", false },
        { "YieldExpression#2", "(function*() { yield a })", "(function* () { yield a; });", false },
        { "YieldExpression#3", "(function*() { yield*a })", "(function* () { yield* a; });", false },
        { "SpreadElement", "[...a]", "[...a];", false },
        { "ClassExpression#1", "(class {})", "(class {\n});", false },
        { "ClassExpression#2", "(class a {})", "(class a {\n});", false },
        { "ClassExpression#3", "(class<T>{})", "(class<T> {\n});", false },
        { "ClassExpression#4", "(class a<T>{})", "(class a<T> {\n});", false },
        { "ClassExpression#5", "(class extends b {})", "(class extends b {\n});", false },
        { "ClassExpression#6", "(class a extends b {})", "(class a extends b {\n});", false },
        { "ClassExpression#7", "(class implements b {})", "(class implements b {\n});", false },
        { "ClassExpression#8", "(class a implements b {})", "(class a implements b {\n});", false },
        { "ClassExpression#9", "(class implements b, c {})", "(class implements b, c {\n});", false },
        { "ClassExpression#10", "(class a implements b, c {})", "(class a implements b, c {\n});", false },
        { "ClassExpression#11", "(class extends b implements c, d {})", "(class extends b implements c, d {\n});", false },
        { "ClassExpression#12", "(class a extends b implements c, d {})", "(class a extends b implements c, d {\n});", false },
        { "ClassExpression#13", "(@a class {})", "(\n@a\nclass {\n});", false },
        { "OmittedExpression", "[,]", "[,];", false },
        { "ExpressionWithTypeArguments", "a<T>", "a<T>;", false },
        { "AsExpression", "a as T", "a as T;", false },
        { "SatisfiesExpression", "a satisfies T", "a satisfies T;", false },
        { "NonNullExpression", "a!", "a!;", false },
        { "MetaProperty#1", "new.target", "new.target;", false },
        { "MetaProperty#2", "import.meta", "import.meta;", false },
        { "ArrayLiteralExpression#1", "[]", "[];", false },
        { "ArrayLiteralExpression#2", "[a]", "[a];", false },
        { "ArrayLiteralExpression#3", "[a,]", "[a,];", false },
        { "ArrayLiteralExpression#4", "[,a]", "[, a];", false },
        { "ArrayLiteralExpression#5", "[...a]", "[...a];", false },
        { "ArrayLiteralExpression#6", "const array = [/* comment */];", "const array = [ /* comment */];", false },
        { "ObjectLiteralExpression#1", "({})", "({});", false },
        { "ObjectLiteralExpression#2", "({a,})", "({ a, });", false },
        { "ShorthandPropertyAssignment", "({a})", "({ a });", false },
        { "PropertyAssignment", "({a:b})", "({ a: b });", false },
        { "SpreadAssignment", "({...a})", "({ ...a });", false },
        { "Block", "{}", "{ }", false },
        { "VariableStatement#1", "var a", "var a;", false },
        { "VariableStatement#2", "let a", "let a;", false },
        { "VariableStatement#3", "const a = b", "const a = b;", false },
        { "VariableStatement#4", "using a = b", "using a = b;", false },
        { "VariableStatement#5", "await using a = b", "await using a = b;", false },
        { "EmptyStatement", ";", ";", false },
        { "IfStatement#1", "if(a);", "if (a)\n    ;", false },
        { "IfStatement#2", "if(a);else;", "if (a)\n    ;\nelse\n    ;", false },
        { "IfStatement#3", "if(a);else{}", "if (a)\n    ;\nelse { }", false },
        { "IfStatement#4", "if(a);else if(b);", "if (a)\n    ;\nelse if (b)\n    ;", false },
        { "IfStatement#5", "if(a);else if(b) {}", "if (a)\n    ;\nelse if (b) { }", false },
        { "IfStatement#6", "if(a) {}", "if (a) { }", false },
        { "IfStatement#7", "if(a) {} else;", "if (a) { }\nelse\n    ;", false },
        { "IfStatement#8", "if(a) {} else {}", "if (a) { }\nelse { }", false },
        { "IfStatement#9", "if(a) {} else if(b);", "if (a) { }\nelse if (b)\n    ;", false },
        { "IfStatement#10", "if(a) {} else if(b){}", "if (a) { }\nelse if (b) { }", false },
        { "DoStatement#1", "do;while(a);", "do\n    ;\nwhile (a);", false },
        { "DoStatement#2", "do {} while(a);", "do { } while (a);", false },
        { "WhileStatement#1", "while(a);", "while (a)\n    ;", false },
        { "WhileStatement#2", "while(a) {}", "while (a) { }", false },
        { "ForStatement#1", "for(;;);", "for (;;)\n    ;", false },
        { "ForStatement#2", "for(a;;);", "for (a;;)\n    ;", false },
        { "ForStatement#3", "for(var a;;);", "for (var a;;)\n    ;", false },
        { "ForStatement#4", "for(;a;);", "for (; a;)\n    ;", false },
        { "ForStatement#5", "for(;;a);", "for (;; a)\n    ;", false },
        { "ForStatement#6", "for(;;){}", "for (;;) { }", false },
        { "ForInStatement#1", "for(a in b);", "for (a in b)\n    ;", false },
        { "ForInStatement#2", "for(var a in b);", "for (var a in b)\n    ;", false },
        { "ForInStatement#3", "for(a in b){}", "for (a in b) { }", false },
        { "ForOfStatement#1", "for(a of b);", "for (a of b)\n    ;", false },
        { "ForOfStatement#2", "for(var a of b);", "for (var a of b)\n    ;", false },
        { "ForOfStatement#3", "for(a of b){}", "for (a of b) { }", false },
        { "ForOfStatement#4", "for await(a of b);", "for await (a of b)\n    ;", false },
        { "ForOfStatement#5", "for await(var a of b);", "for await (var a of b)\n    ;", false },
        { "ForOfStatement#6", "for await(a of b){}", "for await (a of b) { }", false },
        { "ContinueStatement#1", "continue", "continue;", false },
        { "ContinueStatement#2", "continue a", "continue a;", false },
        { "BreakStatement#1", "break", "break;", false },
        { "BreakStatement#2", "break a", "break a;", false },
        { "ReturnStatement#1", "return", "return;", false },
        { "ReturnStatement#2", "return a", "return a;", false },
        { "WithStatement#1", "with(a);", "with (a)\n    ;", false },
        { "WithStatement#2", "with(a){}", "with (a) { }", false },
        { "SwitchStatement", "switch (a) {}", "switch (a) {\n}", false },
        { "CaseClause#1", "switch (a) {case b:}", "switch (a) {\n    case b:\n}", false },
        { "CaseClause#2", "switch (a) {case b:;}", "switch (a) {\n    case b: ;\n}", false },
        { "DefaultClause#1", "switch (a) {default:}", "switch (a) {\n    default:\n}", false },
        { "DefaultClause#2", "switch (a) {default:;}", "switch (a) {\n    default: ;\n}", false },
        { "LabeledStatement", "a:;", "a: ;", false },
        { "ThrowStatement", "throw a", "throw a;", false },
        { "TryStatement#1", "try {} catch {}", "try { }\ncatch { }", false },
        { "TryStatement#2", "try {} finally {}", "try { }\nfinally { }", false },
        { "TryStatement#3", "try {} catch {} finally {}", "try { }\ncatch { }\nfinally { }", false },
        { "DebuggerStatement", "debugger", "debugger;", false },
        { "FunctionDeclaration#1", "export default function(){}", "export default function () { }", false },
        { "FunctionDeclaration#2", "function f(){}", "function f() { }", false },
        { "FunctionDeclaration#3", "function*f(){}", "function* f() { }", false },
        { "FunctionDeclaration#4", "async function f(){}", "async function f() { }", false },
        { "FunctionDeclaration#5", "async function*f(){}", "async function* f() { }", false },
        { "FunctionDeclaration#6", "function f<T>(){}", "function f<T>() { }", false },
        { "FunctionDeclaration#7", "function f(a){}", "function f(a) { }", false },
        { "FunctionDeclaration#8", "function f():T{}", "function f(): T { }", false },
        { "FunctionDeclaration#9", "function f();", "function f();", false },
        { "ClassDeclaration#1", "class a {}", "class a {\n}", false },
        { "ClassDeclaration#2", "class a<T>{}", "class a<T> {\n}", false },
        { "ClassDeclaration#3", "class a extends b {}", "class a extends b {\n}", false },
        { "ClassDeclaration#4", "class a implements b {}", "class a implements b {\n}", false },
        { "ClassDeclaration#5", "class a implements b, c {}", "class a implements b, c {\n}", false },
        { "ClassDeclaration#6", "class a extends b implements c, d {}", "class a extends b implements c, d {\n}", false },
        { "ClassDeclaration#7", "export default class {}", "export default class {\n}", false },
        { "ClassDeclaration#8", "export default class<T>{}", "export default class<T> {\n}", false },
        { "ClassDeclaration#9", "export default class extends b {}", "export default class extends b {\n}", false },
        { "ClassDeclaration#10", "export default class implements b {}", "export default class implements b {\n}", false },
        { "ClassDeclaration#11", "export default class implements b, c {}", "export default class implements b, c {\n}", false },
        { "ClassDeclaration#12", "export default class extends b implements c, d {}", "export default class extends b implements c, d {\n}", false },
        { "ClassDeclaration#13", "@a class b {}", "@a\nclass b {\n}", false },
        { "ClassDeclaration#14", "@a export class b {}", "@a\nexport class b {\n}", false },
        { "ClassDeclaration#15", "export @a class b {}", "export \n@a\nclass b {\n}", false },
        { "InterfaceDeclaration#1", "interface a {}", "interface a {\n}", false },
        { "InterfaceDeclaration#2", "interface a<T>{}", "interface a<T> {\n}", false },
        { "InterfaceDeclaration#3", "interface a extends b {}", "interface a extends b {\n}", false },
        { "InterfaceDeclaration#4", "interface a extends b, c {}", "interface a extends b, c {\n}", false },
        { "TypeAliasDeclaration#1", "type a = b", "type a = b;", false },
        { "TypeAliasDeclaration#2", "type a<T> = b", "type a<T> = b;", false },
        { "EnumDeclaration#1", "enum a{}", "enum a {\n}", false },
        { "EnumDeclaration#2", "enum a{b}", "enum a {\n    b\n}", false },
        { "EnumDeclaration#3", "enum a{b=c}", "enum a {\n    b = c\n}", false },
        { "ModuleDeclaration#1", "module a{}", "module a { }", false },
        { "ModuleDeclaration#2", "module a.b{}", "module a.b { }", false },
        { "ModuleDeclaration#3", "module \"a\";", "module \"a\";", false },
        { "ModuleDeclaration#4", "module \"a\"{}", "module \"a\" { }", false },
        { "ModuleDeclaration#5", "namespace a{}", "namespace a { }", false },
        { "ModuleDeclaration#6", "namespace a.b{}", "namespace a.b { }", false },
        { "ModuleDeclaration#7", "global;", "global;", false },
        { "ModuleDeclaration#8", "global{}", "global { }", false },
        { "ImportEqualsDeclaration#1", "import a = b", "import a = b;", false },
        { "ImportEqualsDeclaration#2", "import a = b.c", "import a = b.c;", false },
        { "ImportEqualsDeclaration#3", "import a = require(\"b\")", "import a = require(\"b\");", false },
        { "ImportEqualsDeclaration#4", "export import a = b", "export import a = b;", false },
        { "ImportEqualsDeclaration#5", "export import a = require(\"b\")", "export import a = require(\"b\");", false },
        { "ImportEqualsDeclaration#6", "import type a = b", "import type a = b;", false },
        { "ImportEqualsDeclaration#7", "import type a = b.c", "import type a = b.c;", false },
        { "ImportEqualsDeclaration#8", "import type a = require(\"b\")", "import type a = require(\"b\");", false },
        { "ImportDeclaration#1", "import \"a\"", "import \"a\";", false },
        { "ImportDeclaration#2", "import a from \"b\"", "import a from \"b\";", false },
        { "ImportDeclaration#3", "import type a from \"b\"", "import type a from \"b\";", false },
        { "ImportDeclaration#4", "import * as a from \"b\"", "import * as a from \"b\";", false },
        { "ImportDeclaration#5", "import type * as a from \"b\"", "import type * as a from \"b\";", false },
        { "ImportDeclaration#6", "import {} from \"b\"", "import {} from \"b\";", false },
        { "ImportDeclaration#7", "import type {} from \"b\"", "import type {} from \"b\";", false },
        { "ImportDeclaration#8", "import { a } from \"b\"", "import { a } from \"b\";", false },
        { "ImportDeclaration#9", "import type { a } from \"b\"", "import type { a } from \"b\";", false },
        { "ImportDeclaration#8", "import { a as b } from \"c\"", "import { a as b } from \"c\";", false },
        { "ImportDeclaration#9", "import type { a as b } from \"c\"", "import type { a as b } from \"c\";", false },
        { "ImportDeclaration#10", "import { \"a\" as b } from \"c\"", "import { \"a\" as b } from \"c\";", false },
        { "ImportDeclaration#11", "import type { \"a\" as b } from \"c\"", "import type { \"a\" as b } from \"c\";", false },
        { "ImportDeclaration#12", "import a, {} from \"b\"", "import a, {} from \"b\";", false },
        { "ImportDeclaration#13", "import a, * as b from \"c\"", "import a, * as b from \"c\";", false },
        { "ImportDeclaration#14", "import {} from \"a\" with {}", "import {} from \"a\" with {};", false },
        { "ImportDeclaration#15", "import {} from \"a\" with { b: \"c\" }", "import {} from \"a\" with { b: \"c\" };", false },
        { "ImportDeclaration#16", "import {} from \"a\" with { \"b\": \"c\" }", "import {} from \"a\" with { \"b\": \"c\" };", false },
        { "ExportAssignment#1", "export = a", "export = a;", false },
        { "ExportAssignment#2", "export default a", "export default a;", false },
        { "NamespaceExportDeclaration", "export as namespace a", "export as namespace a;", false },
        { "ExportDeclaration#1", "export * from \"a\"", "export * from \"a\";", false },
        { "ExportDeclaration#2", "export type * from \"a\"", "export type * from \"a\";", false },
        { "ExportDeclaration#3", "export * as a from \"b\"", "export * as a from \"b\";", false },
        { "ExportDeclaration#4", "export type * as a from \"b\"", "export type * as a from \"b\";", false },
        { "ExportDeclaration#5", "export { } from \"a\"", "export {} from \"a\";", false },
        { "ExportDeclaration#6", "export type { } from \"a\"", "export type {} from \"a\";", false },
        { "ExportDeclaration#7", "export { a } from \"b\"", "export { a } from \"b\";", false },
        { "ExportDeclaration#8", "export { type a } from \"b\"", "export { type a } from \"b\";", false },
        { "ExportDeclaration#9", "export type { a } from \"b\"", "export type { a } from \"b\";", false },
        { "ExportDeclaration#10", "export { a as b } from \"c\"", "export { a as b } from \"c\";", false },
        { "ExportDeclaration#11", "export { type a as b } from \"c\"", "export { type a as b } from \"c\";", false },
        { "ExportDeclaration#12", "export type { a as b } from \"c\"", "export type { a as b } from \"c\";", false },
        { "ExportDeclaration#13", "export { a as \"b\" } from \"c\"", "export { a as \"b\" } from \"c\";", false },
        { "ExportDeclaration#14", "export { type a as \"b\" } from \"c\"", "export { type a as \"b\" } from \"c\";", false },
        { "ExportDeclaration#15", "export type { a as \"b\" } from \"c\"", "export type { a as \"b\" } from \"c\";", false },
        { "ExportDeclaration#16", "export { \"a\" } from \"b\"", "export { \"a\" } from \"b\";", false },
        { "ExportDeclaration#17", "export { type \"a\" } from \"b\"", "export { type \"a\" } from \"b\";", false },
        { "ExportDeclaration#18", "export type { \"a\" } from \"b\"", "export type { \"a\" } from \"b\";", false },
        { "ExportDeclaration#19", "export { \"a\" as b } from \"c\"", "export { \"a\" as b } from \"c\";", false },
        { "ExportDeclaration#20", "export { type \"a\" as b } from \"c\"", "export { type \"a\" as b } from \"c\";", false },
        { "ExportDeclaration#21", "export type { \"a\" as b } from \"c\"", "export type { \"a\" as b } from \"c\";", false },
        { "ExportDeclaration#22", "export { \"a\" as \"b\" } from \"c\"", "export { \"a\" as \"b\" } from \"c\";", false },
        { "ExportDeclaration#23", "export { type \"a\" as \"b\" } from \"c\"", "export { type \"a\" as \"b\" } from \"c\";", false },
        { "ExportDeclaration#24", "export type { \"a\" as \"b\" } from \"c\"", "export type { \"a\" as \"b\" } from \"c\";", false },
        { "ExportDeclaration#25", "export { }", "export {};", false },
        { "ExportDeclaration#26", "export type { }", "export type {};", false },
        { "ExportDeclaration#27", "export { a }", "export { a };", false },
        { "ExportDeclaration#28", "export { type a }", "export { type a };", false },
        { "ExportDeclaration#29", "export type { a }", "export type { a };", false },
        { "ExportDeclaration#30", "export { a as b }", "export { a as b };", false },
        { "ExportDeclaration#31", "export { type a as b }", "export { type a as b };", false },
        { "ExportDeclaration#32", "export type { a as b }", "export type { a as b };", false },
        { "ExportDeclaration#33", "export { a as \"b\" }", "export { a as \"b\" };", false },
        { "ExportDeclaration#34", "export { type a as \"b\" }", "export { type a as \"b\" };", false },
        { "ExportDeclaration#35", "export type { a as \"b\" }", "export type { a as \"b\" };", false },
        { "ExportDeclaration#36", "export {} from \"a\" with {}", "export {} from \"a\" with {};", false },
        { "ExportDeclaration#37", "export {} from \"a\" with { b: \"c\" }", "export {} from \"a\" with { b: \"c\" };", false },
        { "ExportDeclaration#38", "export {} from \"a\" with { \"b\": \"c\" }", "export {} from \"a\" with { \"b\": \"c\" };", false },
        { "KeywordTypeNode#1", "type T = any", "type T = any;", false },
        { "KeywordTypeNode#2", "type T = unknown", "type T = unknown;", false },
        { "KeywordTypeNode#3", "type T = never", "type T = never;", false },
        { "KeywordTypeNode#4", "type T = void", "type T = void;", false },
        { "KeywordTypeNode#5", "type T = undefined", "type T = undefined;", false },
        { "KeywordTypeNode#6", "type T = null", "type T = null;", false },
        { "KeywordTypeNode#7", "type T = object", "type T = object;", false },
        { "KeywordTypeNode#8", "type T = string", "type T = string;", false },
        { "KeywordTypeNode#9", "type T = symbol", "type T = symbol;", false },
        { "KeywordTypeNode#10", "type T = number", "type T = number;", false },
        { "KeywordTypeNode#11", "type T = bigint", "type T = bigint;", false },
        { "KeywordTypeNode#12", "type T = boolean", "type T = boolean;", false },
        { "KeywordTypeNode#13", "type T = intrinsic", "type T = intrinsic;", false },
        { "TypePredicateNode#1", "function f(): asserts a", "function f(): asserts a;", false },
        { "TypePredicateNode#2", "function f(): asserts a is b", "function f(): asserts a is b;", false },
        { "TypePredicateNode#3", "function f(): asserts this", "function f(): asserts this;", false },
        { "TypePredicateNode#4", "function f(): asserts this is b", "function f(): asserts this is b;", false },
        { "TypeReferenceNode#1", "type T = a", "type T = a;", false },
        { "TypeReferenceNode#2", "type T = a.b", "type T = a.b;", false },
        { "TypeReferenceNode#3", "type T = a<U>", "type T = a<U>;", false },
        { "TypeReferenceNode#4", "type T = a.b<U>", "type T = a.b<U>;", false },
        { "FunctionTypeNode#1", "type T = () => a", "type T = () => a;", false },
        { "FunctionTypeNode#2", "type T = <T>() => a", "type T = <T>() => a;", false },
        { "FunctionTypeNode#3", "type T = (a) => b", "type T = (a) => b;", false },
        { "ConstructorTypeNode#1", "type T = new () => a", "type T = new () => a;", false },
        { "ConstructorTypeNode#2", "type T = new <T>() => a", "type T = new <T>() => a;", false },
        { "ConstructorTypeNode#3", "type T = new (a) => b", "type T = new (a) => b;", false },
        { "ConstructorTypeNode#4", "type T = abstract new () => a", "type T = abstract new () => a;", false },
        { "TypeQueryNode#1", "type T = typeof a", "type T = typeof a;", false },
        { "TypeQueryNode#2", "type T = typeof a.b", "type T = typeof a.b;", false },
        { "TypeQueryNode#3", "type T = typeof a<U>", "type T = typeof a<U>;", false },
        { "TypeLiteralNode#1", "type T = {}", "type T = {};", false },
        { "TypeLiteralNode#2", "type T = {a}", "type T = {\n    a;\n};", false },
        { "ArrayTypeNode", "type T = a[]", "type T = a[];", false },
        { "TupleTypeNode#1", "type T = []", "type T = [\n];", false },
        { "TupleTypeNode#2", "type T = [a]", "type T = [\n    a\n];", false },
        { "TupleTypeNode#3", "type T = [a,]", "type T = [\n    a\n];", false },
        { "RestTypeNode", "type T = [...a]", "type T = [\n    ...a\n];", false },
        { "OptionalTypeNode", "type T = [a?]", "type T = [\n    a?\n];", false },
        { "NamedTupleMember#1", "type T = [a: b]", "type T = [\n    a: b\n];", false },
        { "NamedTupleMember#2", "type T = [a?: b]", "type T = [\n    a?: b\n];", false },
        { "NamedTupleMember#3", "type T = [...a: b]", "type T = [\n    ...a: b\n];", false },
        { "UnionTypeNode#1", "type T = a | b", "type T = a | b;", false },
        { "UnionTypeNode#2", "type T = a | b | c", "type T = a | b | c;", false },
        { "UnionTypeNode#3", "type T = | a | b", "type T = a | b;", false },
        { "IntersectionTypeNode#1", "type T = a & b", "type T = a & b;", false },
        { "IntersectionTypeNode#2", "type T = a & b & c", "type T = a & b & c;", false },
        { "IntersectionTypeNode#3", "type T = & a & b", "type T = a & b;", false },
        { "ConditionalTypeNode", "type T = a extends b ? c : d", "type T = a extends b ? c : d;", false },
        { "InferTypeNode#1", "type T = a extends infer b ? c : d", "type T = a extends infer b ? c : d;", false },
        { "InferTypeNode#2", "type T = a extends infer b extends c ? d : e", "type T = a extends infer b extends c ? d : e;", false },
        { "ParenthesizedTypeNode", "type T = (U)", "type T = (U);", false },
        { "ThisTypeNode", "type T = this", "type T = this;", false },
        { "TypeOperatorNode#1", "type T = keyof U", "type T = keyof U;", false },
        { "TypeOperatorNode#2", "type T = readonly U[]", "type T = readonly U[];", false },
        { "TypeOperatorNode#3", "type T = unique symbol", "type T = unique symbol;", false },
        { "IndexedAccessTypeNode", "type T = a[b]", "type T = a[b];", false },
        { "MappedTypeNode#1", "type T = { [a in b]: c }", "type T = {\n    [a in b]: c;\n};", false },
        { "MappedTypeNode#2", "type T = { [a in b as c]: d }", "type T = {\n    [a in b as c]: d;\n};", false },
        { "MappedTypeNode#3", "type T = { readonly [a in b]: c }", "type T = {\n    readonly [a in b]: c;\n};", false },
        { "MappedTypeNode#4", "type T = { +readonly [a in b]: c }", "type T = {\n    +readonly [a in b]: c;\n};", false },
        { "MappedTypeNode#5", "type T = { -readonly [a in b]: c }", "type T = {\n    -readonly [a in b]: c;\n};", false },
        { "MappedTypeNode#6", "type T = { [a in b]?: c }", "type T = {\n    [a in b]?: c;\n};", false },
        { "MappedTypeNode#7", "type T = { [a in b]+?: c }", "type T = {\n    [a in b]+?: c;\n};", false },
        { "MappedTypeNode#8", "type T = { [a in b]-?: c }", "type T = {\n    [a in b]-?: c;\n};", false },
        { "MappedTypeNode#9", "type T = { [a in b]: c; d }", "type T = {\n    [a in b]: c;\n    d;\n};", false },
        { "LiteralTypeNode#1", "type T = null", "type T = null;", false },
        { "LiteralTypeNode#2", "type T = true", "type T = true;", false },
        { "LiteralTypeNode#3", "type T = false", "type T = false;", false },
        { "LiteralTypeNode#4", "type T = \"\"", "type T = \"\";", false },
        { "LiteralTypeNode#5", "type T = ''", "type T = '';", false },
        { "LiteralTypeNode#6", "type T = ``", "type T = ``;", false },
        { "LiteralTypeNode#7", "type T = 0", "type T = 0;", false },
        { "LiteralTypeNode#8", "type T = 0n", "type T = 0n;", false },
        { "LiteralTypeNode#9", "type T = -0", "type T = -0;", false },
        { "LiteralTypeNode#10", "type T = -0n", "type T = -0n;", false },
        { "TemplateTypeNode#1", "type T = `a${b}c`", "type T = `a${b}c`;", false },
        { "TemplateTypeNode#2", "type T = `a${b}c${d}e`", "type T = `a${b}c${d}e`;", false },
        { "ImportTypeNode#1", "type T = import(a)", "type T = import(a);", false },
        { "ImportTypeNode#2", "type T = import(a).b", "type T = import(a).b;", false },
        { "ImportTypeNode#3", "type T = import(a).b<U>", "type T = import(a).b<U>;", false },
        { "ImportTypeNode#4", "type T = typeof import(a)", "type T = typeof import(a);", false },
        { "ImportTypeNode#5", "type T = typeof import(a).b", "type T = typeof import(a).b;", false },
        { "ImportTypeNode#6", "type T = import(a, { with: { } })", "type T = import(a, { with: {} });", false },
        { "ImportTypeNode#6", "type T = import(a, { with: { b: \"c\" } })", "type T = import(a, { with: { b: \"c\" } });", false },
        { "ImportTypeNode#7", "type T = import(a, { with: { \"b\": \"c\" } })", "type T = import(a, { with: { \"b\": \"c\" } });", false },
        { "PropertySignature#1", "interface I {a}", "interface I {\n    a;\n}", false },
        { "PropertySignature#2", "interface I {readonly a}", "interface I {\n    readonly a;\n}", false },
        { "PropertySignature#3", "interface I {\"a\"}", "interface I {\n    \"a\";\n}", false },
        { "PropertySignature#4", "interface I {'a'}", "interface I {\n    'a';\n}", false },
        { "PropertySignature#5", "interface I {0}", "interface I {\n    0;\n}", false },
        { "PropertySignature#6", "interface I {0n}", "interface I {\n    0n;\n}", false },
        { "PropertySignature#7", "interface I {[a]}", "interface I {\n    [a];\n}", false },
        { "PropertySignature#8", "interface I {a?}", "interface I {\n    a?;\n}", false },
        { "PropertySignature#9", "interface I {a: b}", "interface I {\n    a: b;\n}", false },
        { "MethodSignature#1", "interface I {a()}", "interface I {\n    a();\n}", false },
        { "MethodSignature#2", "interface I {\"a\"()}", "interface I {\n    \"a\"();\n}", false },
        { "MethodSignature#3", "interface I {'a'()}", "interface I {\n    'a'();\n}", false },
        { "MethodSignature#4", "interface I {0()}", "interface I {\n    0();\n}", false },
        { "MethodSignature#5", "interface I {0n()}", "interface I {\n    0n();\n}", false },
        { "MethodSignature#6", "interface I {[a]()}", "interface I {\n    [a]();\n}", false },
        { "MethodSignature#7", "interface I {a?()}", "interface I {\n    a?();\n}", false },
        { "MethodSignature#8", "interface I {a<T>()}", "interface I {\n    a<T>();\n}", false },
        { "MethodSignature#9", "interface I {a(): b}", "interface I {\n    a(): b;\n}", false },
        { "MethodSignature#10", "interface I {a(b): c}", "interface I {\n    a(b): c;\n}", false },
        { "CallSignature#1", "interface I {()}", "interface I {\n    ();\n}", false },
        { "CallSignature#2", "interface I {():a}", "interface I {\n    (): a;\n}", false },
        { "CallSignature#3", "interface I {(p)}", "interface I {\n    (p);\n}", false },
        { "CallSignature#4", "interface I {<T>()}", "interface I {\n    <T>();\n}", false },
        { "ConstructSignature#1", "interface I {new ()}", "interface I {\n    new ();\n}", false },
        { "ConstructSignature#2", "interface I {new ():a}", "interface I {\n    new (): a;\n}", false },
        { "ConstructSignature#3", "interface I {new (p)}", "interface I {\n    new (p);\n}", false },
        { "ConstructSignature#4", "interface I {new <T>()}", "interface I {\n    new <T>();\n}", false },
        { "IndexSignatureDeclaration#1", "interface I {[a]}", "interface I {\n    [a];\n}", false },
        { "IndexSignatureDeclaration#2", "interface I {[a: b]}", "interface I {\n    [a: b];\n}", false },
        { "IndexSignatureDeclaration#3", "interface I {[a: b]: c}", "interface I {\n    [a: b]: c;\n}", false },
        { "PropertyDeclaration#1", "class C {a}", "class C {\n    a;\n}", false },
        { "PropertyDeclaration#2", "class C {readonly a}", "class C {\n    readonly a;\n}", false },
        { "PropertyDeclaration#3", "class C {static a}", "class C {\n    static a;\n}", false },
        { "PropertyDeclaration#4", "class C {accessor a}", "class C {\n    accessor a;\n}", false },
        { "PropertyDeclaration#5", "class C {\"a\"}", "class C {\n    \"a\";\n}", false },
        { "PropertyDeclaration#6", "class C {'a'}", "class C {\n    'a';\n}", false },
        { "PropertyDeclaration#7", "class C {0}", "class C {\n    0;\n}", false },
        { "PropertyDeclaration#8", "class C {0n}", "class C {\n    0n;\n}", false },
        { "PropertyDeclaration#9", "class C {[a]}", "class C {\n    [a];\n}", false },
        { "PropertyDeclaration#10", "class C {#a}", "class C {\n    #a;\n}", false },
        { "PropertyDeclaration#11", "class C {a?}", "class C {\n    a?;\n}", false },
        { "PropertyDeclaration#12", "class C {a!}", "class C {\n    a!;\n}", false },
        { "PropertyDeclaration#13", "class C {a: b}", "class C {\n    a: b;\n}", false },
        { "PropertyDeclaration#14", "class C {a = b}", "class C {\n    a = b;\n}", false },
        { "PropertyDeclaration#15", "class C {@a b}", "class C {\n    @a\n    b;\n}", false },
        { "MethodDeclaration#1", "class C {a()}", "class C {\n    a();\n}", false },
        { "MethodDeclaration#2", "class C {\"a\"()}", "class C {\n    \"a\"();\n}", false },
        { "MethodDeclaration#3", "class C {'a'()}", "class C {\n    'a'();\n}", false },
        { "MethodDeclaration#4", "class C {0()}", "class C {\n    0();\n}", false },
        { "MethodDeclaration#5", "class C {0n()}", "class C {\n    0n();\n}", false },
        { "MethodDeclaration#6", "class C {[a]()}", "class C {\n    [a]();\n}", false },
        { "MethodDeclaration#7", "class C {#a()}", "class C {\n    #a();\n}", false },
        { "MethodDeclaration#8", "class C {a?()}", "class C {\n    a?();\n}", false },
        { "MethodDeclaration#9", "class C {a<T>()}", "class C {\n    a<T>();\n}", false },
        { "MethodDeclaration#10", "class C {a(): b}", "class C {\n    a(): b;\n}", false },
        { "MethodDeclaration#11", "class C {a(b): c}", "class C {\n    a(b): c;\n}", false },
        { "MethodDeclaration#12", "class C {a() {} }", "class C {\n    a() { }\n}", false },
        { "MethodDeclaration#13", "class C {@a b() {} }", "class C {\n    @a\n    b() { }\n}", false },
        { "MethodDeclaration#14", "class C {static a() {} }", "class C {\n    static a() { }\n}", false },
        { "MethodDeclaration#15", "class C {async a() {} }", "class C {\n    async a() { }\n}", false },
        { "GetAccessorDeclaration#1", "class C {get a()}", "class C {\n    get a();\n}", false },
        { "GetAccessorDeclaration#2", "class C {get \"a\"()}", "class C {\n    get \"a\"();\n}", false },
        { "GetAccessorDeclaration#3", "class C {get 'a'()}", "class C {\n    get 'a'();\n}", false },
        { "GetAccessorDeclaration#4", "class C {get 0()}", "class C {\n    get 0();\n}", false },
        { "GetAccessorDeclaration#5", "class C {get 0n()}", "class C {\n    get 0n();\n}", false },
        { "GetAccessorDeclaration#6", "class C {get [a]()}", "class C {\n    get [a]();\n}", false },
        { "GetAccessorDeclaration#7", "class C {get #a()}", "class C {\n    get #a();\n}", false },
        { "GetAccessorDeclaration#8", "class C {get a(): b}", "class C {\n    get a(): b;\n}", false },
        { "GetAccessorDeclaration#9", "class C {get a(b): c}", "class C {\n    get a(b): c;\n}", false },
        { "GetAccessorDeclaration#10", "class C {get a() {} }", "class C {\n    get a() { }\n}", false },
        { "GetAccessorDeclaration#11", "class C {@a get b() {} }", "class C {\n    @a\n    get b() { }\n}", false },
        { "GetAccessorDeclaration#12", "class C {static get a() {} }", "class C {\n    static get a() { }\n}", false },
        { "SetAccessorDeclaration#1", "class C {set a()}", "class C {\n    set a();\n}", false },
        { "SetAccessorDeclaration#2", "class C {set \"a\"()}", "class C {\n    set \"a\"();\n}", false },
        { "SetAccessorDeclaration#3", "class C {set 'a'()}", "class C {\n    set 'a'();\n}", false },
        { "SetAccessorDeclaration#4", "class C {set 0()}", "class C {\n    set 0();\n}", false },
        { "SetAccessorDeclaration#5", "class C {set 0n()}", "class C {\n    set 0n();\n}", false },
        { "SetAccessorDeclaration#6", "class C {set [a]()}", "class C {\n    set [a]();\n}", false },
        { "SetAccessorDeclaration#7", "class C {set #a()}", "class C {\n    set #a();\n}", false },
        { "SetAccessorDeclaration#8", "class C {set a(): b}", "class C {\n    set a(): b;\n}", false },
        { "SetAccessorDeclaration#9", "class C {set a(b): c}", "class C {\n    set a(b): c;\n}", false },
        { "SetAccessorDeclaration#10", "class C {set a() {} }", "class C {\n    set a() { }\n}", false },
        { "SetAccessorDeclaration#11", "class C {@a set b() {} }", "class C {\n    @a\n    set b() { }\n}", false },
        { "SetAccessorDeclaration#12", "class C {static set a() {} }", "class C {\n    static set a() { }\n}", false },
        { "ConstructorDeclaration#1", "class C {constructor()}", "class C {\n    constructor();\n}", false },
        { "ConstructorDeclaration#2", "class C {constructor(): b}", "class C {\n    constructor(): b;\n}", false },
        { "ConstructorDeclaration#3", "class C {constructor(b): c}", "class C {\n    constructor(b): c;\n}", false },
        { "ConstructorDeclaration#4", "class C {constructor() {} }", "class C {\n    constructor() { }\n}", false },
        { "ConstructorDeclaration#5", "class C {@a constructor() {} }", "class C {\n    constructor() { }\n}", false },
        { "ConstructorDeclaration#6", "class C {private constructor() {} }", "class C {\n    private constructor() { }\n}", false },
        { "ClassStaticBlockDeclaration", "class C {static { }}", "class C {\n    static { }\n}", false },
        { "SemicolonClassElement#1", "class C {;}", "class C {\n    ;\n}", false },
        { "ParameterDeclaration#1", "function f(a)", "function f(a);", false },
        { "ParameterDeclaration#2", "function f(a: b)", "function f(a: b);", false },
        { "ParameterDeclaration#3", "function f(a = b)", "function f(a = b);", false },
        { "ParameterDeclaration#4", "function f(a?)", "function f(a?);", false },
        { "ParameterDeclaration#5", "function f(...a)", "function f(...a);", false },
        { "ParameterDeclaration#6", "function f(this)", "function f(this);", false },
        { "ObjectBindingPattern#1", "function f({})", "function f({});", false },
        { "ObjectBindingPattern#2", "function f({a})", "function f({ a });", false },
        { "ObjectBindingPattern#3", "function f({a = b})", "function f({ a = b });", false },
        { "ObjectBindingPattern#4", "function f({a: b})", "function f({ a: b });", false },
        { "ObjectBindingPattern#5", "function f({a: b = c})", "function f({ a: b = c });", false },
        { "ObjectBindingPattern#6", "function f({\"a\": b})", "function f({ \"a\": b });", false },
        { "ObjectBindingPattern#7", "function f({'a': b})", "function f({ 'a': b });", false },
        { "ObjectBindingPattern#8", "function f({0: b})", "function f({ 0: b });", false },
        { "ObjectBindingPattern#9", "function f({[a]: b})", "function f({ [a]: b });", false },
        { "ObjectBindingPattern#10", "function f({...a})", "function f({ ...a });", false },
        { "ObjectBindingPattern#11", "function f({a: {}})", "function f({ a: {} });", false },
        { "ObjectBindingPattern#12", "function f({a: []})", "function f({ a: [] });", false },
        { "ArrayBindingPattern#1", "function f([])", "function f([]);", false },
        { "ArrayBindingPattern#2", "function f([,])", "function f([,]);", false },
        { "ArrayBindingPattern#3", "function f([a])", "function f([a]);", false },
        { "ArrayBindingPattern#4", "function f([a, b])", "function f([a, b]);", false },
        { "ArrayBindingPattern#5", "function f([a, , b])", "function f([a, , b]);", false },
        { "ArrayBindingPattern#6", "function f([a = b])", "function f([a = b]);", false },
        { "ArrayBindingPattern#7", "function f([...a])", "function f([...a]);", false },
        { "ArrayBindingPattern#8", "function f([{}])", "function f([{}]);", false },
        { "ArrayBindingPattern#9", "function f([[]])", "function f([[]]);", false },
        { "TypeParameterDeclaration#1", "function f<T>();", "function f<T>();", false },
        { "TypeParameterDeclaration#2", "function f<in T>();", "function f<in T>();", false },
        { "TypeParameterDeclaration#3", "function f<T extends U>();", "function f<T extends U>();", false },
        { "TypeParameterDeclaration#4", "function f<T = U>();", "function f<T = U>();", false },
        { "TypeParameterDeclaration#5", "function f<T extends U = V>();", "function f<T extends U = V>();", false },
        { "TypeParameterDeclaration#6", "function f<T, U>();", "function f<T, U>();", false },
        { "JsxElement1", "<a></a>", "<a></a>;", true },
        { "JsxElement2", "<this></this>", "<this></this>;", true },
        { "JsxElement3", "<a:b></a:b>", "<a:b></a:b>;", true },
        { "JsxElement4", "<a.b></a.b>", "<a.b></a.b>;", true },
        { "JsxElement5", "<a<b>></a>", "<a<b>></a>;", true },
        { "JsxElement6", "<a b></a>", "<a b></a>;", true },
        { "JsxElement7", "<a>b</a>", "<a>b</a>;", true },
        { "JsxElement8", "<a>{b}</a>", "<a>{b}</a>;", true },
        { "JsxElement9", "<a><b></b></a>", "<a><b></b></a>;", true },
        { "JsxElement10", "<a><b /></a>", "<a><b /></a>;", true },
        { "JsxElement11", "<a><></></a>", "<a><></></a>;", true },
        { "JsxElement12", "<a>\n    {/* missing */}\n    {\n        // foo\n    }\n</a>", "<a>\n    {/* missing */}\n    {\n    // foo\n    }\n</a>;", true },
        { "JsxSelfClosingElement1", "<a />", "<a />;", true },
        { "JsxSelfClosingElement2", "<this />", "<this />;", true },
        { "JsxSelfClosingElement3", "<a:b />", "<a:b />;", true },
        { "JsxSelfClosingElement4", "<a.b />", "<a.b />;", true },
        { "JsxSelfClosingElement5", "<a<b> />", "<a<b> />;", true },
        { "JsxSelfClosingElement6", "<a b/>", "<a b/>;", true },
        { "JsxFragment1", "<></>", "<></>;", true },
        { "JsxFragment2", "<>b</>", "<>b</>;", true },
        { "JsxFragment3", "<>{b}</>", "<>{b}</>;", true },
        { "JsxFragment4", "<><b></b></>", "<><b></b></>;", true },
        { "JsxFragment5", "<><b /></>", "<><b /></>;", true },
        { "JsxFragment6", "<><></></>", "<><></></>;", true },
        { "JsxAttribute1", "<a b/>", "<a b/>;", true },
        { "JsxAttribute2", "<a b:c/>", "<a b:c/>;", true },
        { "JsxAttribute3", "<a b=\"c\"/>", "<a b=\"c\"/>;", true },
        { "JsxAttribute4", "<a b='c'/>", "<a b='c'/>;", true },
        { "JsxAttribute5", "<a b={c}/>", "<a b={c}/>;", true },
        { "JsxAttribute6", "<a b=<c></c>/>", "<a b=<c></c>/>;", true },
        { "JsxAttribute7", "<a b=<c />/>", "<a b=<c />/>;", true },
        { "JsxAttribute8", "<a b=<></>/>", "<a b=<></>/>;", true },
        { "JsxSpreadAttribute", "<a {...b}/>", "<a {...b}/>;", true },
    };

    // printer_test.go:592-622 — TestParenthesizeDecorator
    [Fact]
    public void ParenthesizeDecorator()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewClassDeclaration(
                factory.NewModifierList(
                [
                    factory.NewDecorator(
                        factory.NewBinaryExpression(
                            null, // modifiers
                            factory.NewIdentifier("a"),
                            null, // typeNode
                            factory.NewToken(SyntaxKind.PlusToken),
                            factory.NewIdentifier("b"))),
                ]),
                factory.NewIdentifier("C"),
                null, // typeParameters
                null, // heritageClauses
                factory.NewNodeList([])));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "@(a + b)\nclass C {\n}");
    }

    // printer_test.go:624-659 — TestParenthesizeComputedPropertyName
    [Fact]
    public void ParenthesizeComputedPropertyName()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewClassDeclaration(
                null, // modifiers
                factory.NewIdentifier("C"),
                null, // typeParameters
                null, // heritageClauses
                factory.NewNodeList(
                [
                    factory.NewPropertyDeclaration(
                        null, // modifiers
                        factory.NewComputedPropertyName(
                            // will be parenthesized on emit:
                            factory.NewBinaryExpression(
                                null, // modifiers
                                factory.NewIdentifier("a"),
                                null, // typeNode
                                factory.NewToken(SyntaxKind.CommaToken),
                                factory.NewIdentifier("b"))),
                        null, // postfixToken
                        null, // typeNode
                        null), // initializer
                ])));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "class C {\n    [(a, b)];\n}");
    }

    // printer_test.go:661-689 — TestParenthesizeArrayLiteral
    [Fact]
    public void ParenthesizeArrayLiteral()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewArrayLiteralExpression(
                    factory.NewNodeList(
                    [
                        // will be parenthesized on emit:
                        factory.NewBinaryExpression(
                            null, // modifiers
                            factory.NewIdentifier("a"),
                            null, // typeNode
                            factory.NewToken(SyntaxKind.CommaToken),
                            factory.NewIdentifier("b")),
                    ]),
                    false))); // multiLine

        MarkSyntheticRecursive(file);
        CheckEmit(file, "[(a, b)];");
    }

    // printer_test.go:691-717 — TestParenthesizePropertyAccess1
    [Fact]
    public void ParenthesizePropertyAccess1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewPropertyAccessExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    null, // questionDotToken
                    factory.NewIdentifier("c"),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b).c;");
    }

    // printer_test.go:719-744 — TestParenthesizePropertyAccess2
    [Fact]
    public void ParenthesizePropertyAccess2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewPropertyAccessExpression(
                    // will be parenthesized on emit:
                    factory.NewPropertyAccessExpression(
                        factory.NewIdentifier("a"),
                        factory.NewToken(SyntaxKind.QuestionDotToken),
                        factory.NewIdentifier("b"),
                        NodeFlags.OptionalChain),
                    null, // questionDotToken
                    factory.NewIdentifier("c"),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a?.b).c;");
    }

    // printer_test.go:746-770 — TestParenthesizePropertyAccess3
    [Fact]
    public void ParenthesizePropertyAccess3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewPropertyAccessExpression(
                    // will be parenthesized on emit:
                    factory.NewNewExpression(
                        factory.NewIdentifier("a"),
                        null, // typeArguments
                        null), // arguments
                    null, // questionDotToken
                    factory.NewIdentifier("b"),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(new a).b;");
    }

    // printer_test.go:772-798 — TestParenthesizeElementAccess1
    [Fact]
    public void ParenthesizeElementAccess1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewElementAccessExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    null, // questionDotToken
                    factory.NewIdentifier("c"),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b)[c];");
    }

    // printer_test.go:800-825 — TestParenthesizeElementAccess2
    [Fact]
    public void ParenthesizeElementAccess2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewElementAccessExpression(
                    // will be parenthesized on emit:
                    factory.NewPropertyAccessExpression(
                        factory.NewIdentifier("a"),
                        factory.NewToken(SyntaxKind.QuestionDotToken),
                        factory.NewIdentifier("b"),
                        NodeFlags.OptionalChain),
                    null, // questionDotToken
                    factory.NewIdentifier("c"),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a?.b)[c];");
    }

    // printer_test.go:827-851 — TestParenthesizeElementAccess3
    [Fact]
    public void ParenthesizeElementAccess3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewElementAccessExpression(
                    // will be parenthesized on emit:
                    factory.NewNewExpression(
                        factory.NewIdentifier("a"),
                        null, // typeArguments
                        null), // arguments
                    null, // questionDotToken
                    factory.NewIdentifier("b"),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(new a)[b];");
    }

    // printer_test.go:853-880 — TestParenthesizeCall1
    [Fact]
    public void ParenthesizeCall1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewCallExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNodeList([]),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b)();");
    }

    // printer_test.go:882-908 — TestParenthesizeCall2
    [Fact]
    public void ParenthesizeCall2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewCallExpression(
                    // will be parenthesized on emit:
                    factory.NewPropertyAccessExpression(
                        factory.NewIdentifier("a"),
                        factory.NewToken(SyntaxKind.QuestionDotToken),
                        factory.NewIdentifier("b"),
                        NodeFlags.OptionalChain),
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNodeList([]),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a?.b)();");
    }

    // printer_test.go:910-935 — TestParenthesizeCall3
    [Fact]
    public void ParenthesizeCall3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewCallExpression(
                    // will be parenthesized on emit:
                    factory.NewNewExpression(
                        factory.NewIdentifier("C"),
                        null, // typeArguments
                        null), // arguments
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNodeList([]),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(new C)();");
    }

    // printer_test.go:937-965 — TestParenthesizeCall4
    [Fact]
    public void ParenthesizeCall4()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewCallExpression(
                    factory.NewIdentifier("a"),
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNodeList(
                    [
                        factory.NewBinaryExpression(
                            null, // modifiers
                            factory.NewIdentifier("b"),
                            null, // typeNode
                            factory.NewToken(SyntaxKind.CommaToken),
                            factory.NewIdentifier("c")),
                    ]),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "a((b, c));");
    }

    // printer_test.go:967-992 — TestParenthesizeNew1
    [Fact]
    public void ParenthesizeNew1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewNewExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    null, // typeArguments
                    factory.NewNodeList([]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "new (a, b)();");
    }

    // printer_test.go:994-1019 — TestParenthesizeNew2
    [Fact]
    public void ParenthesizeNew2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewNewExpression(
                    // will be parenthesized on emit:
                    factory.NewCallExpression(
                        factory.NewIdentifier("C"),
                        null, // questionDotToken
                        null, // typeArguments
                        factory.NewNodeList([]),
                        NodeFlags.None),
                    null, // typeArguments
                    null))); // arguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "new (C());");
    }

    // printer_test.go:1021-1047 — TestParenthesizeNew3
    [Fact]
    public void ParenthesizeNew3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewNewExpression(
                    factory.NewIdentifier("C"),
                    null, // typeArguments
                    factory.NewNodeList(
                    [
                        factory.NewBinaryExpression(
                            null, // modifiers
                            factory.NewIdentifier("a"),
                            null, // typeNode
                            factory.NewToken(SyntaxKind.CommaToken),
                            factory.NewIdentifier("b")),
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "new C((a, b));");
    }

    // printer_test.go:1049-1076 — TestParenthesizeTaggedTemplate1
    [Fact]
    public void ParenthesizeTaggedTemplate1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewTaggedTemplateExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNoSubstitutionTemplateLiteral("", TokenFlags.None),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b) ``;");
    }

    // printer_test.go:1078-1104 — TestParenthesizeTaggedTemplate2
    [Fact]
    public void ParenthesizeTaggedTemplate2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewTaggedTemplateExpression(
                    // will be parenthesized on emit:
                    factory.NewPropertyAccessExpression(
                        factory.NewIdentifier("a"),
                        factory.NewToken(SyntaxKind.QuestionDotToken),
                        factory.NewIdentifier("b"),
                        NodeFlags.OptionalChain),
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNoSubstitutionTemplateLiteral("", TokenFlags.None),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a?.b) ``;");
    }

    // printer_test.go:1106-1133 — TestParenthesizeTypeAssertion1
    [Fact]
    public void ParenthesizeTypeAssertion1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewTypeAssertion(
                    factory.NewTypeReferenceNode(
                        factory.NewIdentifier("T"),
                        null), // typeArguments
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.PlusToken),
                        factory.NewIdentifier("b")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "<T>(a + b);");
    }

    // printer_test.go:1135-1161 — TestParenthesizeArrowFunction1
    [Fact]
    public void ParenthesizeArrowFunction1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewArrowFunction(
                    null, // modifiers
                    null, // typeParameters
                    factory.NewNodeList([]),
                    null, // typeNode
                    null, // fullSignature
                    factory.NewToken(SyntaxKind.EqualsGreaterThanToken),
                    // will be parenthesized on emit:
                    factory.NewObjectLiteralExpression(
                        factory.NewNodeList([]),
                        false)))); // multiLine

        MarkSyntheticRecursive(file);
        CheckEmit(file, "() => ({});");
    }

    // printer_test.go:1163-1194 — TestParenthesizeArrowFunction2
    [Fact]
    public void ParenthesizeArrowFunction2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewArrowFunction(
                    null, // modifiers
                    null, // typeParameters
                    factory.NewNodeList([]),
                    null, // typeNode
                    null, // fullSignature
                    factory.NewToken(SyntaxKind.EqualsGreaterThanToken),
                    // will be parenthesized on emit:
                    factory.NewPropertyAccessExpression(
                        factory.NewObjectLiteralExpression(
                            factory.NewNodeList([]),
                            false), // multiLine
                        null, // questionDotToken
                        factory.NewIdentifier("a"),
                        NodeFlags.None))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "() => ({}.a);");
    }

    // printer_test.go:1196-1219 — TestParenthesizeDelete
    [Fact]
    public void ParenthesizeDelete()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewDeleteExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.PlusToken),
                        factory.NewIdentifier("b")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "delete (a + b);");
    }

    // printer_test.go:1221-1244 — TestParenthesizeVoid
    [Fact]
    public void ParenthesizeVoid()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewVoidExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.PlusToken),
                        factory.NewIdentifier("b")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "void (a + b);");
    }

    // printer_test.go:1246-1269 — TestParenthesizeTypeOf
    [Fact]
    public void ParenthesizeTypeOf()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewTypeOfExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.PlusToken),
                        factory.NewIdentifier("b")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "typeof (a + b);");
    }

    // printer_test.go:1271-1294 — TestParenthesizeAwait
    [Fact]
    public void ParenthesizeAwait()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewAwaitExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.PlusToken),
                        factory.NewIdentifier("b")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "await (a + b);");
    }

    // printer_test.go:1296-1343 — isBinaryOperator
    private static bool IsBinaryOperator(SyntaxKind token)
    {
        switch (token)
        {
            case SyntaxKind.CommaToken:
            case SyntaxKind.LessThanToken:
            case SyntaxKind.GreaterThanToken:
            case SyntaxKind.LessThanEqualsToken:
            case SyntaxKind.GreaterThanEqualsToken:
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.AsteriskToken:
            case SyntaxKind.AsteriskAsteriskToken:
            case SyntaxKind.SlashToken:
            case SyntaxKind.PercentToken:
            case SyntaxKind.LessThanLessThanToken:
            case SyntaxKind.GreaterThanGreaterThanToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
            case SyntaxKind.AmpersandToken:
            case SyntaxKind.BarToken:
            case SyntaxKind.CaretToken:
            case SyntaxKind.AmpersandAmpersandToken:
            case SyntaxKind.BarBarToken:
            case SyntaxKind.QuestionQuestionToken:
            case SyntaxKind.EqualsToken:
            case SyntaxKind.PlusEqualsToken:
            case SyntaxKind.MinusEqualsToken:
            case SyntaxKind.AsteriskEqualsToken:
            case SyntaxKind.AsteriskAsteriskEqualsToken:
            case SyntaxKind.SlashEqualsToken:
            case SyntaxKind.PercentEqualsToken:
            case SyntaxKind.LessThanLessThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanEqualsToken:
            case SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken:
            case SyntaxKind.AmpersandEqualsToken:
            case SyntaxKind.BarEqualsToken:
            case SyntaxKind.BarBarEqualsToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
            case SyntaxKind.QuestionQuestionEqualsToken:
            case SyntaxKind.CaretEqualsToken:
            case SyntaxKind.InKeyword:
            case SyntaxKind.InstanceOfKeyword:
                return true;
            default:
                return false;
        }
    }

    // printer_test.go:1345-1370 — makeSide
    private static Node MakeSide(string label, SyntaxKind kind, AstNodeFactory factory)
    {
        if (kind == SyntaxKind.Identifier || kind == SyntaxKind.Unknown)
        {
            return factory.NewIdentifier(label);
        }
        if (kind == SyntaxKind.ArrowFunction)
        {
            return factory.NewArrowFunction(
                null, // modifiers
                null, // typeParameters
                factory.NewNodeList([]),
                null, // typeNode
                null, // fullSignature
                factory.NewToken(SyntaxKind.EqualsGreaterThanToken),
                factory.NewBlock(factory.NewNodeList([]), false)); // multiLine
        }
        if (IsBinaryOperator(kind))
        {
            return factory.NewBinaryExpression(
                null, // modifiers
                factory.NewIdentifier(label + "l"),
                null, // typeNode
                factory.NewToken(kind),
                factory.NewIdentifier(label + "r"));
        }
        throw new InvalidOperationException("unsupported kind");
    }

    // printer_test.go:1372-1420 — TestParenthesizeBinary
    public static TheoryData<SyntaxKind, SyntaxKind, SyntaxKind, string> ParenthesizeBinaryData() => new()
    {
        { SyntaxKind.Unknown, SyntaxKind.CommaToken, SyntaxKind.Unknown, "l, r" },
        { SyntaxKind.PlusToken, SyntaxKind.CommaToken, SyntaxKind.Unknown, "ll + lr, r" },
        { SyntaxKind.PlusToken, SyntaxKind.AsteriskToken, SyntaxKind.Unknown, "(ll + lr) * r" },
        { SyntaxKind.Unknown, SyntaxKind.AsteriskToken, SyntaxKind.PlusToken, "l * (rl + rr)" },
        { SyntaxKind.AsteriskToken, SyntaxKind.PlusToken, SyntaxKind.Unknown, "ll * lr + r" },
        { SyntaxKind.Unknown, SyntaxKind.PlusToken, SyntaxKind.AsteriskToken, "l + rl * rr" },
        { SyntaxKind.AsteriskToken, SyntaxKind.SlashToken, SyntaxKind.Unknown, "ll * lr / r" },
        { SyntaxKind.AsteriskAsteriskToken, SyntaxKind.SlashToken, SyntaxKind.Unknown, "ll ** lr / r" },
        { SyntaxKind.AsteriskToken, SyntaxKind.AsteriskAsteriskToken, SyntaxKind.Unknown, "(ll * lr) ** r" },
        { SyntaxKind.AsteriskAsteriskToken, SyntaxKind.AsteriskAsteriskToken, SyntaxKind.Unknown, "(ll ** lr) ** r" },
        { SyntaxKind.Unknown, SyntaxKind.AsteriskToken, SyntaxKind.AsteriskToken, "l * rl * rr" },
        { SyntaxKind.Unknown, SyntaxKind.BarToken, SyntaxKind.BarToken, "l | rl | rr" },
        { SyntaxKind.Unknown, SyntaxKind.AmpersandToken, SyntaxKind.AmpersandToken, "l & rl & rr" },
        { SyntaxKind.Unknown, SyntaxKind.CaretToken, SyntaxKind.CaretToken, "l ^ rl ^ rr" },
        { SyntaxKind.Unknown, SyntaxKind.AmpersandAmpersandToken, SyntaxKind.ArrowFunction, "l && (() => { })" },
    };

    [Theory]
    [MemberData(nameof(ParenthesizeBinaryData))]
    public void ParenthesizeBinary(SyntaxKind left, SyntaxKind @operator, SyntaxKind right, string output)
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewBinaryExpression(
                    null, // modifiers
                    MakeSide("l", left, factory),
                    null, // typeNode
                    factory.NewToken(@operator),
                    MakeSide("r", right, factory))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, output + ";");
    }

    // printer_test.go:1422-1449 — TestParenthesizeConditional1
    [Fact]
    public void ParenthesizeConditional1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewConditionalExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    factory.NewToken(SyntaxKind.QuestionToken),
                    factory.NewIdentifier("c"),
                    factory.NewToken(SyntaxKind.ColonToken),
                    factory.NewIdentifier("d"))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b) ? c : d;");
    }

    // printer_test.go:1451-1478 — TestParenthesizeConditional2
    [Fact]
    public void ParenthesizeConditional2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewConditionalExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.EqualsToken),
                        factory.NewIdentifier("b")),
                    factory.NewToken(SyntaxKind.QuestionToken),
                    factory.NewIdentifier("c"),
                    factory.NewToken(SyntaxKind.ColonToken),
                    factory.NewIdentifier("d"))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a = b) ? c : d;");
    }

    // printer_test.go:1480-1512 — TestParenthesizeConditional3
    [Fact]
    public void ParenthesizeConditional3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewConditionalExpression(
                    // will be parenthesized on emit:
                    factory.NewArrowFunction(
                        null, // modifiers
                        null, // typeParameters
                        factory.NewNodeList([]),
                        null, // typeNode
                        null, // fullSignature
                        factory.NewToken(SyntaxKind.EqualsGreaterThanToken),
                        factory.NewBlock(
                            factory.NewNodeList([]),
                            false)), // multiLine
                    factory.NewToken(SyntaxKind.QuestionToken),
                    factory.NewIdentifier("a"),
                    factory.NewToken(SyntaxKind.ColonToken),
                    factory.NewIdentifier("b"))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(() => { }) ? a : b;");
    }

    // printer_test.go:1514-1535 — TestParenthesizeConditional4
    [Fact]
    public void ParenthesizeConditional4()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewConditionalExpression(
                    // will be parenthesized on emit:
                    factory.NewYieldExpression(null, null),
                    factory.NewToken(SyntaxKind.QuestionToken),
                    factory.NewIdentifier("a"),
                    factory.NewToken(SyntaxKind.ColonToken),
                    factory.NewIdentifier("b"))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(yield) ? a : b;");
    }

    // printer_test.go:1537-1564 — TestParenthesizeConditional5
    [Fact]
    public void ParenthesizeConditional5()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewConditionalExpression(
                    factory.NewIdentifier("a"),
                    factory.NewToken(SyntaxKind.QuestionToken),
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("b"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("c")),
                    factory.NewToken(SyntaxKind.ColonToken),
                    factory.NewIdentifier("d"))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "a ? (b, c) : d;");
    }

    // printer_test.go:1566-1593 — TestParenthesizeConditional6
    [Fact]
    public void ParenthesizeConditional6()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewConditionalExpression(
                    factory.NewIdentifier("a"),
                    factory.NewToken(SyntaxKind.QuestionToken),
                    factory.NewIdentifier("b"),
                    factory.NewToken(SyntaxKind.ColonToken),
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("c"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("d")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "a ? b : (c, d);");
    }

    // printer_test.go:1595-1619 — TestParenthesizeYield1
    // (printer_test.go:1621-1623 `////func TestParenthesizeYield2` is commented out in Go and is not ported.)
    [Fact]
    public void ParenthesizeYield1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewYieldExpression(
                    null, // asteriskToken
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "yield (a, b);");
    }

    // printer_test.go:1625-1655 — TestParenthesizeSpreadElement1
    [Fact]
    public void ParenthesizeSpreadElement1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewArrayLiteralExpression(
                    factory.NewNodeList(
                    [
                        factory.NewSpreadElement(
                            // will be parenthesized on emit:
                            factory.NewBinaryExpression(
                                null, // modifiers
                                factory.NewIdentifier("a"),
                                null, // typeNode
                                factory.NewToken(SyntaxKind.CommaToken),
                                factory.NewIdentifier("b"))),
                    ]),
                    false))); // multiLine

        MarkSyntheticRecursive(file);
        CheckEmit(file, "[...(a, b)];");
    }

    // printer_test.go:1657-1690 — TestParenthesizeSpreadElement2
    [Fact]
    public void ParenthesizeSpreadElement2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewCallExpression(
                    factory.NewIdentifier("a"),
                    null, // questionDotToken
                    null, // typeArguments
                    factory.NewNodeList(
                    [
                        factory.NewSpreadElement(
                            // will be parenthesized on emit:
                            factory.NewBinaryExpression(
                                null, // modifiers
                                factory.NewIdentifier("b"),
                                null, // typeNode
                                factory.NewToken(SyntaxKind.CommaToken),
                                factory.NewIdentifier("c"))),
                    ]),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "a(...(b, c));");
    }

    // printer_test.go:1692-1723 — TestParenthesizeSpreadElement3
    [Fact]
    public void ParenthesizeSpreadElement3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewNewExpression(
                    factory.NewIdentifier("a"),
                    null, // typeArguments
                    factory.NewNodeList(
                    [
                        factory.NewSpreadElement(
                            // will be parenthesized on emit:
                            factory.NewBinaryExpression(
                                null, // modifiers
                                factory.NewIdentifier("b"),
                                null, // typeNode
                                factory.NewToken(SyntaxKind.CommaToken),
                                factory.NewIdentifier("c"))),
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "new a(...(b, c));");
    }

    // printer_test.go:1725-1756 — TestParenthesizeExpressionWithTypeArguments
    [Fact]
    public void ParenthesizeExpressionWithTypeArguments()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewExpressionWithTypeArguments(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    factory.NewNodeList(
                    [
                        factory.NewTypeReferenceNode(
                            factory.NewIdentifier("c"),
                            null),
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b)<c>;");
    }

    // printer_test.go:1758-1785 — TestParenthesizeAsExpression
    [Fact]
    public void ParenthesizeAsExpression()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewAsExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    factory.NewTypeReferenceNode(
                        factory.NewIdentifier("c"),
                        null))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b) as c;");
    }

    // printer_test.go:1787-1814 — TestParenthesizeSatisfiesExpression
    [Fact]
    public void ParenthesizeSatisfiesExpression()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewSatisfiesExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    factory.NewTypeReferenceNode(
                        factory.NewIdentifier("c"),
                        null))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b) satisfies c;");
    }

    // printer_test.go:1816-1840 — TestParenthesizeNonNullExpression
    [Fact]
    public void ParenthesizeNonNullExpression()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewNonNullExpression(
                    // will be parenthesized on emit:
                    factory.NewBinaryExpression(
                        null, // modifiers
                        factory.NewIdentifier("a"),
                        null, // typeNode
                        factory.NewToken(SyntaxKind.CommaToken),
                        factory.NewIdentifier("b")),
                    NodeFlags.None)));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(a, b)!;");
    }

    // printer_test.go:1842-1861 — TestParenthesizeExpressionStatement1
    [Fact]
    public void ParenthesizeExpressionStatement1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewObjectLiteralExpression(
                    factory.NewNodeList([]),
                    false))); // multiLine

        MarkSyntheticRecursive(file);
        CheckEmit(file, "({});");
    }

    // printer_test.go:1863-1891 — TestParenthesizeExpressionStatement2
    [Fact]
    public void ParenthesizeExpressionStatement2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewFunctionExpression(
                    null, // modifiers
                    null, // asteriskToken
                    null, // name
                    null, // typeParameters
                    factory.NewNodeList([]),
                    null, // typeNode
                    null, // fullSignature
                    factory.NewBlock(
                        factory.NewNodeList([]),
                        false)))); // multiLine

        MarkSyntheticRecursive(file);
        CheckEmit(file, "(function () { });");
    }

    // printer_test.go:1893-1915 — TestParenthesizeExpressionStatement3
    [Fact]
    public void ParenthesizeExpressionStatement3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(
                factory.NewClassExpression(
                    null, // modifiers
                    null, // name
                    null, // typeParameters
                    null, // heritageClauses
                    factory.NewNodeList([]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "class {\n};");
    }

    // printer_test.go:1917-1943 — TestParenthesizeExpressionDefault1
    [Fact]
    public void ParenthesizeExpressionDefault1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExportAssignment(
                null, // modifiers
                false, // isExportEquals
                null, // typeNode
                // will be parenthesized on emit:
                factory.NewClassExpression(
                    null, // modifiers
                    null, // name
                    null, // typeParameters
                    null, // heritageClauses
                    factory.NewNodeList([]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "export default (class {\n});");
    }

    // printer_test.go:1945-1979 — TestParenthesizeExpressionDefault2
    [Fact]
    public void ParenthesizeExpressionDefault2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExportAssignment(
                null, // modifiers
                false, // isExportEquals
                null, // typeNode
                // will be parenthesized on emit:
                factory.NewFunctionExpression(
                    null, // modifiers
                    null, // asteriskToken
                    null, // name
                    null, // typeParameters
                    factory.NewNodeList([]),
                    null, // typeNode
                    null, // fullSignature
                    factory.NewBlock(
                        factory.NewNodeList([]),
                        false)))); // multiLine

        MarkSyntheticRecursive(file);
        CheckEmit(file, "export default (function () { });");
    }

    // printer_test.go:1981-2005 — TestParenthesizeExpressionDefault3
    [Fact]
    public void ParenthesizeExpressionDefault3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewExportAssignment(
                null, // modifiers
                false, // isExportEquals
                null, // typeNode
                // will be parenthesized on emit:
                factory.NewBinaryExpression(
                    null, // modifiers
                    factory.NewIdentifier("a"),
                    null, // typeNode
                    factory.NewToken(SyntaxKind.CommaToken),
                    factory.NewIdentifier("b"))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "export default (a, b);");
    }

    // printer_test.go:2007-2034 — TestParenthesizeArrayType
    [Fact]
    public void ParenthesizeArrayType()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewArrayTypeNode(
                    // will be parenthesized on emit:
                    factory.NewUnionTypeNode(
                        factory.NewNodeList(
                        [
                            factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                            factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                        ])))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = (a | b)[];");
    }

    // printer_test.go:2036-2069 — TestParenthesizeOptionalType
    [Fact]
    public void ParenthesizeOptionalType()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewTupleTypeNode(
                    factory.NewNodeList(
                    [
                        factory.NewOptionalTypeNode(
                            // will be parenthesized on emit:
                            factory.NewUnionTypeNode(
                                factory.NewNodeList(
                                [
                                    factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                                    factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                                ]))),
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = [\n    (a | b)?\n];");
    }

    // printer_test.go:2071-2102 — TestParenthesizeUnionType1
    [Fact]
    public void ParenthesizeUnionType1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewUnionTypeNode(
                    factory.NewNodeList(
                    [
                        factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                        // will be parenthesized on emit:
                        factory.NewFunctionTypeNode(
                            null, // typeParameters
                            factory.NewNodeList([]),
                            factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null)), // typeArguments
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = a | (() => b);");
    }

    // printer_test.go:2104-2137 — TestParenthesizeUnionType2
    [Fact]
    public void ParenthesizeUnionType2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewUnionTypeNode(
                    factory.NewNodeList(
                    [
                        // will be parenthesized on emit:
                        factory.NewInferTypeNode(
                            factory.NewTypeParameterDeclaration(
                                null,
                                factory.NewIdentifier("a"),
                                factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                                null, // expression
                                null)), // defaultType
                        factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null), // typeArguments
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = (infer a extends b) | c;");
    }

    // printer_test.go:2139-2171 — TestParenthesizeIntersectionType
    [Fact]
    public void ParenthesizeIntersectionType()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewIntersectionTypeNode(
                    factory.NewNodeList(
                    [
                        factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                        // will be parenthesized on emit:
                        factory.NewUnionTypeNode(
                            factory.NewNodeList(
                            [
                                factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                                factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null), // typeArguments
                            ])),
                    ]))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = a & (b | c);");
    }

    // printer_test.go:2173-2201 — TestParenthesizeReadonlyTypeOperator1
    [Fact]
    public void ParenthesizeReadonlyTypeOperator1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewTypeOperatorNode(
                    SyntaxKind.ReadonlyKeyword,
                    // will be parenthesized on emit:
                    factory.NewUnionTypeNode(
                        factory.NewNodeList(
                        [
                            factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                            factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                        ])))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = readonly (a | b);");
    }

    // printer_test.go:2203-2227 — TestParenthesizeReadonlyTypeOperator2
    [Fact]
    public void ParenthesizeReadonlyTypeOperator2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewTypeOperatorNode(
                    SyntaxKind.ReadonlyKeyword,
                    // will be parenthesized on emit:
                    factory.NewTypeOperatorNode(
                        SyntaxKind.KeyOfKeyword,
                        factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null))))); // typeArguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = readonly (keyof a);");
    }

    // printer_test.go:2229-2257 — TestParenthesizeKeyofTypeOperator
    [Fact]
    public void ParenthesizeKeyofTypeOperator()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewTypeOperatorNode(
                    SyntaxKind.KeyOfKeyword,
                    // will be parenthesized on emit:
                    factory.NewUnionTypeNode(
                        factory.NewNodeList(
                        [
                            factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                            factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                        ])))));

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = keyof (a | b);");
    }

    // printer_test.go:2259-2287 — TestParenthesizeIndexedAccessType
    [Fact]
    public void ParenthesizeIndexedAccessType()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewIndexedAccessTypeNode(
                    // will be parenthesized on emit:
                    factory.NewUnionTypeNode(
                        factory.NewNodeList(
                        [
                            factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                            factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                        ])),
                    factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null)))); // typeArguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = (a | b)[c];");
    }

    // printer_test.go:2289-2318 — TestParenthesizeConditionalType1
    [Fact]
    public void ParenthesizeConditionalType1()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewConditionalTypeNode(
                    // will be parenthesized on emit:
                    factory.NewFunctionTypeNode(
                        null, // typeParameters
                        factory.NewNodeList([]),
                        factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null)), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("d"), null)))); // typeArguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = (() => a) extends b ? c : d;");
    }

    // printer_test.go:2320-2348 — TestParenthesizeConditionalType2
    [Fact]
    public void ParenthesizeConditionalType2()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewConditionalTypeNode(
                    factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                    // will be parenthesized on emit:
                    factory.NewConditionalTypeNode(
                        factory.NewTypeReferenceNode(factory.NewIdentifier("b"), null), // typeArguments
                        factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null), // typeArguments
                        factory.NewTypeReferenceNode(factory.NewIdentifier("d"), null), // typeArguments
                        factory.NewTypeReferenceNode(factory.NewIdentifier("e"), null)), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("f"), null), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("g"), null)))); // typeArguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = a extends (b extends c ? d : e) ? f : g;");
    }

    // printer_test.go:2350-2387 — TestParenthesizeConditionalType3
    [Fact]
    public void ParenthesizeConditionalType3()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewConditionalTypeNode(
                    factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                    factory.NewFunctionTypeNode(
                        null, // typeParameters
                        factory.NewNodeList([]),
                        // will be parenthesized on emit:
                        factory.NewInferTypeNode(
                            factory.NewTypeParameterDeclaration(
                                null,
                                factory.NewIdentifier("b"),
                                factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null), // typeArguments
                                null, // expression
                                null))), // defaultType
                    factory.NewTypeReferenceNode(factory.NewIdentifier("d"), null), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("e"), null)))); // typeArguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = a extends () => (infer b extends c) ? d : e;");
    }

    // printer_test.go:2389-2431 — TestParenthesizeConditionalType4
    [Fact]
    public void ParenthesizeConditionalType4()
    {
        var factory = new AstNodeFactory();
        SourceFile file = NewFile(
            factory,
            factory.NewTypeAliasDeclaration(
                null, // modifiers
                factory.NewIdentifier("_"), // name
                null, // typeParameters
                factory.NewConditionalTypeNode(
                    factory.NewTypeReferenceNode(factory.NewIdentifier("a"), null), // typeArguments
                    factory.NewFunctionTypeNode(
                        null, // typeParameters
                        factory.NewNodeList([]),
                        // will be parenthesized on emit:
                        factory.NewUnionTypeNode(
                            factory.NewNodeList(
                            [
                                factory.NewInferTypeNode(
                                    factory.NewTypeParameterDeclaration(
                                        null,
                                        factory.NewIdentifier("b"),
                                        factory.NewTypeReferenceNode(factory.NewIdentifier("c"), null), // typeArguments
                                        null, // expression
                                        null)), // defaultType
                                factory.NewTypeReferenceNode(factory.NewIdentifier("d"), null), // typeArguments
                            ]))),
                    factory.NewTypeReferenceNode(factory.NewIdentifier("e"), null), // typeArguments
                    factory.NewTypeReferenceNode(factory.NewIdentifier("f"), null)))); // typeArguments

        MarkSyntheticRecursive(file);
        CheckEmit(file, "type _ = a extends () => (infer b extends c) | d ? e : f;");
    }

    // printer_test.go:2433-2464 — TestNameGeneration
    [Fact]
    public void NameGeneration()
    {
        EmitContext ec = EmitContext.NewEmitContext();
        // The Go test mixes ec.Factory (a printer factory, for the generated temp variables) with
        // base ast.NodeFactory constructors that the printer factory does not forward.
        var factory = new AstNodeFactory();
        StatementList statements = factory.NewNodeList(
        [
            factory.NewVariableStatement(
                null,
                factory.NewVariableDeclarationList(
                    factory.NewNodeList(
                    [
                        factory.NewVariableDeclaration(ec.Factory.NewTempVariable(), null, null, null),
                    ]),
                    NodeFlags.None)),
            factory.NewFunctionDeclaration(
                null,
                null,
                factory.NewIdentifier("f"),
                null,
                factory.NewNodeList([]),
                null,
                null,
                factory.NewBlock(
                    factory.NewNodeList(
                    [
                        factory.NewVariableStatement(
                            null,
                            factory.NewVariableDeclarationList(
                                factory.NewNodeList(
                                [
                                    factory.NewVariableDeclaration(ec.Factory.NewTempVariable(), null, null, null),
                                ]),
                                NodeFlags.None)),
                    ]),
                    true)),
        ]);
        SourceFile file = factory.NewSourceFile(
            FileOpts,
            "",
            statements,
            factory.NewToken(SyntaxKind.EndOfFile)).AsSourceFile();

        MarkSyntheticRecursive(file);
        CheckEmit(ec, file, "var _a;\nfunction f() {\n    var _a;\n}");
    }

    // printer_test.go:2466-2485 — TestNoTrailingCommaAfterTransform
    [Fact]
    public void NoTrailingCommaAfterTransform()
    {
        SourceFile file = ParseTypeScript("[a!]", false); // jsx
        EmitContext emitContext = EmitContext.NewEmitContext();

        NodeVisitor? visitor = null;
        visitor = emitContext.NewNodeVisitor(node =>
        {
            switch (node.Kind)
            {
                case SyntaxKind.NonNullExpression:
                    node = node.Expression()!;
                    break;
                default:
                    node = node.VisitEachChild(visitor!);
                    break;
            }
            return node;
        });
        file = visitor.VisitSourceFile(file);

        CheckEmit(emitContext, file, "[a];");
    }

    // printer_test.go:2487-2506 — TestTrailingCommaAfterTransform
    [Fact]
    public void TrailingCommaAfterTransform()
    {
        SourceFile file = ParseTypeScript("[a!,]", false); // jsx
        EmitContext emitContext = EmitContext.NewEmitContext();

        NodeVisitor? visitor = null;
        visitor = emitContext.NewNodeVisitor(node =>
        {
            switch (node.Kind)
            {
                case SyntaxKind.NonNullExpression:
                    node = node.Expression()!;
                    break;
                default:
                    node = node.VisitEachChild(visitor!);
                    break;
            }
            return node;
        });
        file = visitor.VisitSourceFile(file);

        CheckEmit(emitContext, file, "[a,];");
    }

    // printer_test.go:2508-2524 — TestPartiallyEmittedExpression
    [Fact]
    public void PartiallyEmittedExpression()
    {
        var compilerOptions = new CompilerOptions();

        SourceFile file = ParseTypeScript(
            """
            return ((container.parent
                .left as PropertyAccessExpression)
                .expression as PropertyAccessExpression)
                .expression;
            """,
            false); // jsx

        EmitContext emitContext = EmitContext.NewEmitContext();
        file = global::Ts.Transformers.TsTransforms.TypeEraserTransformer
            .NewTypeEraserTransformer(new global::Ts.Transformers.TransformOptions
            {
                CompilerOptions = compilerOptions,
                Context = emitContext,
            })
            .TransformSourceFile(file);

        CheckEmit(
            emitContext,
            file,
            """
            return container.parent
                .left
                .expression
                .expression;
            """);
    }

    // printer_test.go:2526-2592 — TestParenthesizeBinaryExpressionMixingNullishCoalescing
    public static TheoryData<string, SyntaxKind, SyntaxKind, string, string> NullishCoalescingData() => new()
    {
        // inner ?? on left side of || or &&
        { "BarBarWithLeftQuestionQuestion", SyntaxKind.QuestionQuestionToken, SyntaxKind.BarBarToken, "left", "(a ?? b) || c;" },
        { "AmpersandAmpersandWithLeftQuestionQuestion", SyntaxKind.QuestionQuestionToken, SyntaxKind.AmpersandAmpersandToken, "left", "(a ?? b) && c;" },
        // inner ?? on right side of || or &&
        { "BarBarWithRightQuestionQuestion", SyntaxKind.QuestionQuestionToken, SyntaxKind.BarBarToken, "right", "a || (b ?? c);" },
        { "AmpersandAmpersandWithRightQuestionQuestion", SyntaxKind.QuestionQuestionToken, SyntaxKind.AmpersandAmpersandToken, "right", "a && (b ?? c);" },
        // inner || or && on left side of ??
        { "QuestionQuestionWithLeftBarBar", SyntaxKind.BarBarToken, SyntaxKind.QuestionQuestionToken, "left", "(a || b) ?? c;" },
        { "QuestionQuestionWithLeftAmpersandAmpersand", SyntaxKind.AmpersandAmpersandToken, SyntaxKind.QuestionQuestionToken, "left", "(a && b) ?? c;" },
        // inner || or && on right side of ??
        { "QuestionQuestionWithRightBarBar", SyntaxKind.BarBarToken, SyntaxKind.QuestionQuestionToken, "right", "a ?? (b || c);" },
        { "QuestionQuestionWithRightAmpersandAmpersand", SyntaxKind.AmpersandAmpersandToken, SyntaxKind.QuestionQuestionToken, "right", "a ?? (b && c);" },
    };

    [Theory]
    [MemberData(nameof(NullishCoalescingData))]
    public void ParenthesizeBinaryExpressionMixingNullishCoalescing(
        string title,
        SyntaxKind innerOp,
        SyntaxKind outerOp,
        string side,
        string output)
    {
        _ = title;
        var factory = new AstNodeFactory();
        Node innerExpr = factory.NewBinaryExpression(
            null, // modifiers
            factory.NewIdentifier("a"),
            null, // typeNode
            factory.NewToken(innerOp),
            factory.NewIdentifier("b"));

        Node outerExpr;
        if (side == "left")
        {
            outerExpr = factory.NewBinaryExpression(
                null, // modifiers
                innerExpr, // left: (a innerOp b)
                null, // typeNode
                factory.NewToken(outerOp),
                factory.NewIdentifier("c"));
        }
        else
        {
            outerExpr = factory.NewBinaryExpression(
                null, // modifiers
                factory.NewIdentifier("a"),
                null, // typeNode
                factory.NewToken(outerOp),
                innerExpr); // right: (b innerOp c)
            // adjust identifiers for right side
            innerExpr.AsBinaryExpression().Left = factory.NewIdentifier("b");
            innerExpr.AsBinaryExpression().Right = factory.NewIdentifier("c");
        }

        SourceFile file = NewFile(
            factory,
            factory.NewExpressionStatement(outerExpr));

        MarkSyntheticRecursive(file);
        CheckEmit(file, output);
    }
}
