// Ported from tsgo internal/transformers/estransforms/esdecorator.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class ESDecoratorTransformer : Transformer
{
    private ESDecoratorTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private static Node Visit(Node node) => node;

    // TODO(estransforms): Port lexicalEntry/memberInfo/classInfo, decorator evaluation ordering,
    // descriptor construction, metadata initialization, and class-this/name helper injection.
    internal static Transformer NewESDecoratorTransformer(TransformOptions opts) => new ESDecoratorTransformer(opts);
}
