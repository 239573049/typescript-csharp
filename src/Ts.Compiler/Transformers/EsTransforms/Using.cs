// Ported from tsgo internal/transformers/estransforms/using.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class UsingDeclarationTransformer : Transformer
{
    private UsingDeclarationTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private static Node Visit(Node node) => node;

    // TODO(estransforms): Port usingKind analysis, declaration hoisting, environment binding,
    // __addDisposableResource/__disposeResources helpers, and top-level export preservation.
    internal static Transformer NewUsingDeclarationTransformer(TransformOptions opts) => new UsingDeclarationTransformer(opts);
}
