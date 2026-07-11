// Ported from tsgo internal/transformers/estransforms/optionalchain.go.

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class OptionalChainTransformer : Transformer
{
    private OptionalChainTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private Node Visit(Node node)
    {
        // PORT-NOTE: The current C# parser does not yet aggregate this SubtreeFacts bit onto
        // SourceFile. Traverse unconditionally until that infrastructure surface is complete.
        // Preserve the Go fast-path for synthesized output from an earlier transform: visiting
        // it again is unnecessary and the currently partial generated visitor surface can erase
        // optional children on synthesized declarations.
        if (AstUtilities.NodeIsSynthesized(node) &&
            (node.SubtreeFacts() & SubtreeFacts.SubtreeContainsOptionalChaining) == 0)
        {
            return node;
        }
        return node.Kind switch
        {
            SyntaxKind.CallExpression => VisitCallExpression(node.AsCallExpression(), false),
            SyntaxKind.PropertyAccessExpression or SyntaxKind.ElementAccessExpression when
                (node.Flags & NodeFlags.OptionalChain) != 0 => VisitOptionalExpression(node, false, false),
            SyntaxKind.DeleteExpression => VisitDeleteExpression(node.AsDeleteExpression()),
            _ => Visitor().VisitEachChild(node)!,
        };
    }

    private Node VisitCallExpression(CallExpression node, bool captureThisArg)
    {
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            // If `node` is an optional chain, then it is the outermost chain of an optional expression.
            return VisitOptionalExpression(node.AsNode(), captureThisArg, false);
        }
        if (NodeTests.IsParenthesizedExpression(node.Expression!))
        {
            var unwrapped = AstUtilities.SkipParentheses(node.Expression!);
            if ((unwrapped.Flags & NodeFlags.OptionalChain) != 0)
            {
                // capture thisArg for calls of parenthesized optional chains like `(foo?.bar)()`
                var expression = VisitParenthesizedExpression(node.Expression!.AsParenthesizedExpression(), true, false);
                var arguments = Visitor().VisitNodes(node.Arguments);
                if (NodeTests.IsSyntheticReferenceExpression(expression))
                {
                    var synthetic = expression.AsSyntheticReferenceExpression();
                    var result = Factory().NewFunctionCallCall(synthetic.Expression!, synthetic.ThisArg, arguments!.Nodes);
                    result.Loc = node.Loc;
                    EmitContext().SetOriginal(result, node.AsNode());
                    return result;
                }
                return Factory().AsNodeFactory().UpdateCallExpression(node, expression, null, null, arguments, node.Flags);
            }
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitParenthesizedExpression(ParenthesizedExpression node, bool captureThisArg, bool isDelete)
    {
        var expression = VisitNonOptionalExpression(node.Expression!, captureThisArg, isDelete);
        if (NodeTests.IsSyntheticReferenceExpression(expression))
        {
            // `(a.b)` -> { expression `((_a = a).b)`, thisArg: `_a` }
            // `(a[b])` -> { expression `((_a = a)[b])`, thisArg: `_a` }
            var synthetic = expression.AsSyntheticReferenceExpression();
            var result = Factory().AsNodeFactory().NewSyntheticReferenceExpression(
                Factory().UpdateParenthesizedExpression(node, synthetic.Expression),
                synthetic.ThisArg);
            EmitContext().SetOriginal(result, node.AsNode());
            return result;
        }
        return Factory().UpdateParenthesizedExpression(node, expression);
    }

    private Node VisitPropertyOrElementAccessExpression(Node node, bool captureThisArg, bool isDelete)
    {
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            return VisitOptionalExpression(node, captureThisArg, isDelete);
        }

        var expression = Visitor().VisitNode(node.Expression()!);
        global::Ts.Core.Debug.Assert(expression is null || !NodeTests.IsSyntheticReferenceExpression(expression));

        Node? thisArg = null;
        if (captureThisArg)
        {
            if (!global::Ts.Transformers.Transformers.IsSimpleCopiableExpression(expression!))
            {
                thisArg = Factory().NewTempVariable();
                EmitContext().AddVariableDeclaration(thisArg);
                expression = Factory().NewAssignmentExpression(thisArg, expression!);
            }
            else
            {
                thisArg = expression;
            }
        }

        if (node.Kind == SyntaxKind.PropertyAccessExpression)
        {
            var property = node.AsPropertyAccessExpression();
            expression = Factory().AsNodeFactory().UpdatePropertyAccessExpression(
                property,
                expression,
                null,
                Visitor().VisitNode(property.Name()),
                property.Flags);
        }
        else
        {
            var element = node.AsElementAccessExpression();
            expression = Factory().AsNodeFactory().UpdateElementAccessExpression(
                element,
                expression,
                null,
                Visitor().VisitNode(element.ArgumentExpression),
                element.Flags);
        }

        if (thisArg is not null)
        {
            var result = Factory().AsNodeFactory().NewSyntheticReferenceExpression(expression, thisArg);
            EmitContext().SetOriginal(result, node);
            return result;
        }
        return expression!;
    }

    private Node VisitDeleteExpression(DeleteExpression node)
    {
        var unwrapped = AstUtilities.SkipParentheses(node.Expression!);
        return (unwrapped.Flags & NodeFlags.OptionalChain) != 0
            ? VisitNonOptionalExpression(node.Expression!, false, true)
            : Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitNonOptionalExpression(Node node, bool captureThisArg, bool isDelete) => node.Kind switch
    {
        SyntaxKind.ParenthesizedExpression => VisitParenthesizedExpression(node.AsParenthesizedExpression(), captureThisArg, isDelete),
        SyntaxKind.ElementAccessExpression or SyntaxKind.PropertyAccessExpression =>
            VisitPropertyOrElementAccessExpression(node, captureThisArg, isDelete),
        SyntaxKind.CallExpression => VisitCallExpression(node.AsCallExpression(), captureThisArg),
        _ => Visitor().VisitNode(node)!,
    };

    private readonly record struct FlattenResult(Node Expression, Node[] Chain);

    private static bool IsNonNullChain(Node node) =>
        NodeTests.IsNonNullExpression(node) && (node.Flags & NodeFlags.OptionalChain) != 0;

    private static FlattenResult FlattenChain(Node chain)
    {
        global::Ts.Core.Debug.Assert(!IsNonNullChain(chain));
        List<Node> links = [chain];
        while (!NodeTests.IsTaggedTemplateExpression(chain) && chain.QuestionDotToken() is null)
        {
            chain = AstUtilities.SkipPartiallyEmittedExpressions(chain.Expression()!);
            global::Ts.Core.Debug.Assert(!IsNonNullChain(chain));
            links.Insert(0, chain);
        }
        return new FlattenResult(chain.Expression()!, links.ToArray());
    }

    private static bool IsCallChain(Node node) =>
        NodeTests.IsCallExpression(node) && (node.Flags & NodeFlags.OptionalChain) != 0;

    private Node VisitOptionalExpression(Node node, bool captureThisArg, bool isDelete)
    {
        var flattened = FlattenChain(node);
        var expression = flattened.Expression;
        var chain = flattened.Chain;
        var left = VisitNonOptionalExpression(
            AstUtilities.SkipPartiallyEmittedExpressions(expression),
            IsCallChain(chain[0]),
            false);
        Node? leftThisArg = null;
        var capturedLeft = left;
        if (NodeTests.IsSyntheticReferenceExpression(left))
        {
            leftThisArg = left.AsSyntheticReferenceExpression().ThisArg;
            capturedLeft = left.AsSyntheticReferenceExpression().Expression!;
        }
        var leftExpression = Factory().RestoreOuterExpressions(
            expression,
            capturedLeft,
            OuterExpressionKinds.PartiallyEmittedExpressions);
        if (!global::Ts.Transformers.Transformers.IsSimpleCopiableExpression(capturedLeft))
        {
            capturedLeft = Factory().NewTempVariable();
            EmitContext().AddVariableDeclaration(capturedLeft);
            leftExpression = Factory().NewAssignmentExpression(capturedLeft, leftExpression);
        }
        var rightExpression = capturedLeft;
        Node? thisArg = null;

        for (var i = 0; i < chain.Length; i++)
        {
            var segment = chain[i];
            switch (segment.Kind)
            {
                case SyntaxKind.ElementAccessExpression:
                case SyntaxKind.PropertyAccessExpression:
                    if (i == chain.Length - 1 && captureThisArg)
                    {
                        if (!global::Ts.Transformers.Transformers.IsSimpleCopiableExpression(rightExpression))
                        {
                            thisArg = Factory().NewTempVariable();
                            EmitContext().AddVariableDeclaration(thisArg);
                            rightExpression = Factory().NewAssignmentExpression(thisArg, rightExpression);
                        }
                        else
                        {
                            thisArg = rightExpression;
                        }
                    }
                    rightExpression = segment.Kind == SyntaxKind.ElementAccessExpression
                        ? Factory().NewElementAccessExpression(
                            rightExpression,
                            null,
                            Visitor().VisitNode(segment.AsElementAccessExpression().ArgumentExpression),
                            NodeFlags.None)
                        : Factory().NewPropertyAccessExpression(
                            rightExpression,
                            null,
                            Visitor().VisitNode(segment.AsPropertyAccessExpression().Name()),
                            NodeFlags.None);
                    break;

                case SyntaxKind.CallExpression:
                    if (i == 0 && leftThisArg is not null)
                    {
                        if (!EmitContext().HasAutoGenerateInfo(leftThisArg))
                        {
                            leftThisArg = leftThisArg.Clone(Factory())!;
                            EmitContext().AddEmitFlags(leftThisArg, global::Ts.Printer.EmitFlags.NoComments);
                        }
                        var callThisArg = leftThisArg.Kind == SyntaxKind.SuperKeyword ? Factory().NewThisExpression() : leftThisArg;
                        rightExpression = Factory().NewFunctionCallCall(
                            rightExpression,
                            callThisArg,
                            Visitor().VisitNodes(segment.ArgumentList())!.Nodes);
                    }
                    else
                    {
                        rightExpression = Factory().NewCallExpression(
                            rightExpression,
                            null,
                            null,
                            Visitor().VisitNodes(segment.ArgumentList()),
                            NodeFlags.None);
                    }
                    break;
            }
            EmitContext().SetOriginal(rightExpression, segment);
        }

        Node target = isDelete
            ? Factory().NewConditionalExpression(
                EsTransformUtilities.CreateNotNullCondition(EmitContext(), leftExpression, capturedLeft, true),
                Factory().NewToken(SyntaxKind.QuestionToken),
                Factory().NewTrueExpression(),
                Factory().NewToken(SyntaxKind.ColonToken),
                Factory().AsNodeFactory().NewDeleteExpression(rightExpression))
            : Factory().NewConditionalExpression(
                EsTransformUtilities.CreateNotNullCondition(EmitContext(), leftExpression, capturedLeft, true),
                Factory().NewToken(SyntaxKind.QuestionToken),
                Factory().NewVoidZeroExpression(),
                Factory().NewToken(SyntaxKind.ColonToken),
                rightExpression);
        target.Loc = node.Loc;
        if (thisArg is not null)
        {
            target = Factory().AsNodeFactory().NewSyntheticReferenceExpression(target, thisArg);
        }
        EmitContext().SetOriginal(target, node);
        return target;
    }

    internal static Transformer NewOptionalChainTransformer(TransformOptions opts) => new OptionalChainTransformer(opts);
}
