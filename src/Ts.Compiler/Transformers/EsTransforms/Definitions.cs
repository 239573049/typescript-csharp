// Ported from tsgo internal/transformers/estransforms/definitions.go.

using Ts.Core;

namespace Ts.Transformers.EsTransforms;

public static class EsTransformers
{
    private static readonly TransformerFactory ESDecoratorAndClassFields =
        global::Ts.Transformers.Transformers.Chain(
            ESDecoratorTransformer.NewESDecoratorTransformer,
            ClassFieldsTransformer.NewClassFieldsTransformer);

    public static readonly TransformerFactory NewESNextTransformer =
        global::Ts.Transformers.Transformers.Chain(
            UsingDeclarationTransformer.NewUsingDeclarationTransformer,
            ESDecoratorAndClassFields);

    // 2025: only module system syntax (import attributes, json modules), untransformed regex modifiers
    // 2024: no new downlevel syntax
    // 2023: no new downlevel syntax
    // 2022: class static blocks and class fields are handled by NewClassFieldsTransformer
    public static readonly TransformerFactory NewES2021Transformer =
        global::Ts.Transformers.Transformers.Chain(NewESNextTransformer, LogicalAssignmentTransformer.NewLogicalAssignmentTransformer);

    public static readonly TransformerFactory NewES2020Transformer =
        global::Ts.Transformers.Transformers.Chain(
            NewES2021Transformer,
            NullishCoalescingTransformer.NewNullishCoalescingTransformer,
            OptionalChainTransformer.NewOptionalChainTransformer);

    public static readonly TransformerFactory NewES2019Transformer =
        global::Ts.Transformers.Transformers.Chain(NewES2020Transformer, OptionalCatchTransformer.NewOptionalCatchTransformer);

    public static readonly TransformerFactory NewES2018Transformer =
        global::Ts.Transformers.Transformers.Chain(
            NewES2019Transformer,
            ObjectRestSpreadTransformer.NewObjectRestSpreadTransformer,
            ForAwaitTransformer.NewForAwaitTransformer,
            TaggedTemplateTransformer.NewTaggedTemplateLiftRestrictionTransformer);

    public static readonly TransformerFactory NewES2017Transformer =
        global::Ts.Transformers.Transformers.Chain(NewES2018Transformer, AsyncTransformer.NewAsyncTransformer);

    public static readonly TransformerFactory NewES2016Transformer =
        global::Ts.Transformers.Transformers.Chain(NewES2017Transformer, ExponentiationTransformer.NewExponentiationTransformer);

    public static Transformer GetESTransformer(TransformOptions opts)
    {
        var options = opts.CompilerOptions!;
        return options.GetEmitScriptTarget() switch
        {
            ScriptTarget.ESNext => ESDecoratorAndClassFields(opts)!,
            ScriptTarget.ES2025 or ScriptTarget.ES2024 or ScriptTarget.ES2023 or ScriptTarget.ES2022 or ScriptTarget.ES2021 =>
                NewESNextTransformer(opts)!,
            ScriptTarget.ES2020 => NewES2021Transformer(opts)!,
            ScriptTarget.ES2019 => NewES2020Transformer(opts)!,
            ScriptTarget.ES2018 => NewES2019Transformer(opts)!,
            ScriptTarget.ES2017 => NewES2018Transformer(opts)!,
            ScriptTarget.ES2016 => NewES2017Transformer(opts)!,
            _ => NewES2016Transformer(opts)!, // other, older, option, transform maximally
        };
    }
}
