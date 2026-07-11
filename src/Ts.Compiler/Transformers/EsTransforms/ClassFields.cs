// Ported from tsgo internal/transformers/estransforms/classfields.go.

using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class ClassFieldsTransformer : Transformer
{
    private ClassFieldsTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private static Node Visit(Node node) => node;

    // TODO(estransforms): Port the complete class lexical/private environment and the public,
    // private, auto-accessor, static-block, constructor-initializer, and substitution paths.
    // ES5 class declaration lowering is not part of this Go file; it lives in the ES2015 package.
    internal static Transformer NewClassFieldsTransformer(TransformOptions opts) => new ClassFieldsTransformer(opts);
}
