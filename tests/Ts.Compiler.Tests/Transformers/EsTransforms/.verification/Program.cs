using System;
using Ts.Ast;
using Ts.Core;
using Ts.Printer;
using Ts.Transformers;
using Ts.Transformers.EsTransforms;
using Ts.TsPath;

static SourceFile Parse(string text)
{
    var file = global::Ts.Parser.Parser.ParseSourceFile(
        new SourceFileParseOptions { FileName = "/test.ts", Path = new Path("/test.ts") },
        text,
        ScriptKind.TS);
    global::Ts.Binder.Binder.BindSourceFile(file);
    return file;
}

static SourceFile Transform(string text, ScriptTarget target)
{
    var transformer = EsTransformers.GetESTransformer(new TransformOptions
    {
        Context = EmitContext.NewEmitContext(),
        CompilerOptions = new CompilerOptions { Target = target },
        GetEmitModuleFormatOfFile = _ => ModuleKind.CommonJS,
    });
    return transformer.TransformSourceFile(Parse(text));
}

static Node Initializer(SourceFile file) => file.Statements!.Nodes[0].AsVariableStatement()
    .DeclarationList!.AsVariableDeclarationList().Declarations!.Nodes[0].Initializer()!;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var exponentiation = Initializer(Transform("const value = left ** right;", ScriptTarget.ES2015));
Require(exponentiation.Kind == SyntaxKind.CallExpression, $"exponentiation: {exponentiation.Kind}");
Require(exponentiation.Expression()!.Name()!.Text() == "pow", "exponentiation callee");

var nullish = Initializer(Transform("const value = getValue() ?? fallback;", ScriptTarget.ES2019));
Require(nullish.Kind == SyntaxKind.ConditionalExpression, $"nullish: {nullish.Kind}");

var logical = Transform("value ||= fallback;", ScriptTarget.ES2020).Statements!.Nodes[0].Expression()!;
Require(logical.Kind == SyntaxKind.BinaryExpression, $"logical: {logical.Kind}");
Require(logical.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.BarBarToken, "logical operator");

var optionalCatch = Transform("try { work(); } catch { recover(); }", ScriptTarget.ES2018)
    .Statements!.Nodes[0].AsTryStatement().CatchClause!.AsCatchClause();
Require(optionalCatch.VariableDeclaration is not null, "optional catch variable");

var objectSpread = Initializer(Transform("const value = { a: 1, ...other, b: 2 };", ScriptTarget.ES2017));
Require(objectSpread.Kind == SyntaxKind.CallExpression, $"object spread: {objectSpread.Kind}");
Require(objectSpread.Expression()!.Text() == "__assign", "object spread helper");

Console.WriteLine("EsTransform direct verification passed (5/5).");
