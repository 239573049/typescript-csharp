// Ported from tsgo internal/transformers/moduletransforms/impliedmodule.go.

using System;
using Ts.Ast;
using Ts.Binder;
using Ts.Core;

namespace Ts.Transformers.ModuleTransforms;

public sealed class ImpliedModuleTransformer : Transformer
{
    private readonly TransformOptions options;
    private readonly IReferenceResolver resolver;
    private readonly Func<HasFileName, ModuleKind> getEmitModuleFormatOfFile;
    private Transformer? cjsTransformer;
    private Transformer? esmTransformer;

    private ImpliedModuleTransformer(TransformOptions options)
    {
        this.options = options;
        resolver = options.Resolver!;
        getEmitModuleFormatOfFile = options.GetEmitModuleFormatOfFile!;
        NewTransformer(Visit, options.Context);
    }

    public static Transformer NewImpliedModuleTransformer(TransformOptions options) => new ImpliedModuleTransformer(options);

    private Node Visit(Node node)
    {
        if (node.Kind == SyntaxKind.SourceFile)
        {
            node = VisitSourceFile(node.AsSourceFile());
        }
        return node;
    }

    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }
        var format = getEmitModuleFormatOfFile(TransformerAstAdapters.AsHasFileName(node));
        Transformer transformer;
        if (format >= ModuleKind.ES2015)
        {
            esmTransformer ??= ESModuleTransformer.NewESModuleTransformer(options);
            transformer = esmTransformer;
        }
        else
        {
            cjsTransformer ??= CommonJSModuleTransformer.NewCommonJSModuleTransformer(options);
            transformer = cjsTransformer;
        }
        _ = resolver;
        return transformer.TransformSourceFile(node).AsNode();
    }
}
