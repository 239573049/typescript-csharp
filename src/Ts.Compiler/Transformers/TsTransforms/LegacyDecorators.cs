// Ported from tsgo internal/transformers/tstransforms/legacydecorators.go.
// PORT-NOTE: Deferred branches: helper injection, class self-reference aliasing, private-name
// decorator evaluation, and member/parameter __decorate emission require the complete transform
// helper surface. This scaffolding deliberately preserves the tree and strips no decorators.

using Ts.Ast;

namespace Ts.Transformers.TsTransforms;

public sealed class LegacyDecoratorsTransformer : global::Ts.Transformers.Transformer
{
    private LegacyDecoratorsTransformer(global::Ts.Transformers.TransformOptions options) =>
        NewTransformer(Visit, options.Context);

    public static global::Ts.Transformers.Transformer NewLegacyDecoratorsTransformer(
        global::Ts.Transformers.TransformOptions options) => new LegacyDecoratorsTransformer(options);

    private Node Visit(Node node)
    {
        // Decorator lowering is intentionally deferred as a unit. Returning the original subtree
        // is safer than partially eliding decorators before their runtime helper calls exist.
        return node;
    }
}
