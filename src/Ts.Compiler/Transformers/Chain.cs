// Ported from tsgo internal/transformers/chain.go.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Binder;
using Ts.Core;

namespace Ts.Transformers;

public sealed class TransformOptions
{
    public global::Ts.Printer.EmitContext? Context;
    public CompilerOptions? CompilerOptions;
    public IReferenceResolver? Resolver;
    public global::Ts.Printer.EmitResolver? EmitResolver;
    public Func<HasFileName, ModuleKind>? GetEmitModuleFormatOfFile;
}

public delegate Transformer? TransformerFactory(TransformOptions options);

public static partial class Transformers
{
    // Chains transforms in left-to-right order, running them one at a time in order
    // (as opposed to interleaved at each node). The resulting combined transform only
    // operates on SourceFile nodes.
    public static TransformerFactory Chain(params TransformerFactory[] transforms)
    {
        if (transforms.Length < 2)
        {
            if (transforms.Length == 0)
            {
                throw new InvalidOperationException("Expected some number of transforms to chain, but got none");
            }
            return transforms[0];
        }

        return options =>
        {
            List<Transformer> constructed = new(transforms.Length);
            foreach (var transform in transforms)
            {
                if (transform(options) is { } result)
                {
                    constructed.Add(result);
                }
            }

            return constructed.Count switch
            {
                0 => null,
                1 => constructed[0],
                _ => new ChainedTransformer(constructed.ToArray()).Initialize(options.Context),
            };
        };
    }

    private sealed class ChainedTransformer : Transformer
    {
        private readonly Transformer[] components;

        public ChainedTransformer(Transformer[] components)
        {
            this.components = components;
        }

        public Transformer Initialize(global::Ts.Printer.EmitContext? context) => NewTransformer(Visit, context);

        private Node Visit(Node node)
        {
            if (node.Kind != SyntaxKind.SourceFile)
            {
                throw new InvalidOperationException("Chained transform passed non-sourcefile initial node");
            }

            var result = node.AsSourceFile();
            foreach (var transformer in components)
            {
                result = transformer.TransformSourceFile(result);
            }
            return result.AsNode();
        }
    }
}
