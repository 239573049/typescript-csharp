// Ported from tsgo internal/transformers/transformer.go.

using System;
using Ts.Ast;

namespace Ts.Transformers;

public class Transformer
{
    private global::Ts.Printer.EmitContext? emitContext;
    private global::Ts.Printer.NodeFactory? factory;
    private NodeVisitor? visitor;

    public Transformer NewTransformer(Func<Node, Node?> visit, global::Ts.Printer.EmitContext? context)
    {
        if (emitContext is not null)
        {
            throw new InvalidOperationException("Transformer already initialized");
        }

        context ??= global::Ts.Printer.EmitContext.NewEmitContext();
        emitContext = context;
        factory = context.Factory;
        visitor = context.NewNodeVisitor(visit);
        return this;
    }

    public global::Ts.Printer.EmitContext EmitContext() => emitContext!;

    public NodeVisitor Visitor() => visitor!;

    public global::Ts.Printer.NodeFactory Factory() => factory!;

    public SourceFile TransformSourceFile(SourceFile file) => visitor!.VisitSourceFile(file);
}
