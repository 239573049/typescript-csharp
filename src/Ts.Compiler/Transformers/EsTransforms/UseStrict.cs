// Ported from tsgo internal/transformers/estransforms/usestrict.go.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

public sealed class UseStrictTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;
    private readonly Func<HasFileName, ModuleKind> getEmitModuleFormatOfFile;

    private UseStrictTransformer(TransformOptions opts)
    {
        compilerOptions = opts.CompilerOptions!;
        getEmitModuleFormatOfFile = opts.GetEmitModuleFormatOfFile!;
        NewTransformer(Visit, opts.Context);
    }

    public static Transformer NewUseStrictTransformer(TransformOptions opts) => new UseStrictTransformer(opts);

    private Node Visit(Node node) => node.Kind != SyntaxKind.SourceFile ? node : VisitSourceFile(node.AsSourceFile());

    private Node VisitSourceFile(SourceFile node)
    {
        if (node.ScriptKind == ScriptKind.Json)
        {
            return node.AsNode();
        }

        var isExternalModule = AstUtilities.IsExternalModule(node);
        var moduleKind = compilerOptions.GetEmitModuleKind();
        var format = getEmitModuleFormatOfFile(global::Ts.Transformers.TransformerAstAdapters.AsHasFileName(node));

        // ESM is always strict. If the file is ESM, and CJS emit
        // has not been requested, then skip adding "use strict".
        if (isExternalModule && moduleKind >= ModuleKind.ES2015 &&
            (moduleKind == ModuleKind.Preserve || format >= ModuleKind.ES2015))
        {
            return node.AsNode();
        }

        var statements = Factory().EnsureUseStrict(node.Statements!.Nodes);
        var statementList = Factory().NewNodeList(statements);
        statementList.Loc = node.Statements.Loc;
        return Factory().AsNodeFactory().UpdateSourceFile(node, statementList, node.EndOfFileToken);
    }
}
