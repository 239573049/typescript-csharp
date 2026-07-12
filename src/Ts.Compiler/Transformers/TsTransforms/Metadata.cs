// Ported from tsgo internal/transformers/tstransforms/metadata.go.
// PORT-NOTE: Decorator-metadata tree injection is scaffolded but deferred until legacy decorator
// helper emission lands. The serializer itself is available in TypeSerializer.cs.

using Ts.Ast;

namespace Ts.Transformers.TsTransforms;

public sealed class MetadataTransformer : global::Ts.Transformers.Transformer
{
    private const bool UseNewTypeMetadataFormat = false;

    private MetadataTransformer(global::Ts.Transformers.TransformOptions options) =>
        NewTransformer(Visit, options.Context);

    public static global::Ts.Transformers.Transformer NewMetadataTransformer(
        global::Ts.Transformers.TransformOptions options) => new MetadataTransformer(options);

    private Node Visit(Node node)
    {
        _ = UseNewTypeMetadataFormat;
        // Preserve decorators and their type nodes for the later legacy decorator transform.
        return node;
    }
}
