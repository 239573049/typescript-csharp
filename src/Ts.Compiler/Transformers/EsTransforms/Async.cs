// Ported from tsgo internal/transformers/estransforms/async.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class AsyncTransformer : Transformer
{
    private AsyncTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private static Node Visit(Node node) => node;

    // TODO(estransforms): Port asyncContextFlags, lexicalArgumentsInfo, super-access rewriting,
    // parameter collision handling, and __awaiter/__generator body construction. This explicit
    // pass-through preserves the chain position without pretending that async lowering occurred.
    internal static Transformer NewAsyncTransformer(TransformOptions opts) => new AsyncTransformer(opts);
}
