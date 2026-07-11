// Ported from tsgo internal/transformers/estransforms/forawait.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class ForAwaitTransformer : Transformer
{
    private ForAwaitTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private static Node Visit(Node node) => node;

    // TODO(estransforms): Port forAwaitHierarchyFacts, for-await-of try/finally lowering,
    // async-generator await/yield rewriting, and shared super-access tracking.
    internal static Transformer NewForAwaitTransformer(TransformOptions opts) => new ForAwaitTransformer(opts);
}
