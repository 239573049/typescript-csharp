// Ported from tsgo internal/transformers/estransforms/objectrestspread.go.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

internal sealed class ObjectRestSpreadTransformer : Transformer
{
    private readonly CompilerOptions compilerOptions;
    private bool inExportedVariableStatement;
    private bool expressionResultIsUnused;

    private ObjectRestSpreadTransformer(TransformOptions opts)
    {
        compilerOptions = opts.CompilerOptions!;
        NewTransformer(Visit, opts.Context);
    }

    private Node Visit(Node node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsESObjectRestOrSpread) == 0)
        {
            return node;
        }

        // Save the expressionResultIsUnused flag set by the parent for this node,
        // then reset to false for children (the default). Specific cases below override as needed.
        var savedExpressionResultIsUnused = expressionResultIsUnused;
        expressionResultIsUnused = false;
        try
        {
            return node.Kind switch
            {
                SyntaxKind.SourceFile => VisitSourceFile(node.AsSourceFile()),
                SyntaxKind.ObjectLiteralExpression => VisitObjectLiteralExpression(node.AsObjectLiteralExpression()),
                SyntaxKind.BinaryExpression => VisitBinaryExpression(node.AsBinaryExpression(), savedExpressionResultIsUnused),
                SyntaxKind.ExpressionStatement => VisitExpressionStatement(node),
                SyntaxKind.ParenthesizedExpression => VisitParenthesizedExpression(node, savedExpressionResultIsUnused),
                SyntaxKind.VariableStatement => VisitVariableStatement(node.AsVariableStatement()),
                SyntaxKind.VariableDeclaration => VisitVariableDeclaration(node.AsVariableDeclaration()),
                _ => Visitor().VisitEachChild(node)!,
            };
        }
        finally
        {
            expressionResultIsUnused = savedExpressionResultIsUnused;
        }
    }

    private Node VisitExpressionStatement(Node node)
    {
        expressionResultIsUnused = true;
        return Visitor().VisitEachChild(node)!;
    }

    private Node VisitParenthesizedExpression(Node node, bool savedExpressionResultIsUnused)
    {
        expressionResultIsUnused = savedExpressionResultIsUnused;
        return Visitor().VisitEachChild(node)!;
    }

    private Node VisitSourceFile(SourceFile node)
    {
        var visited = Visitor().VisitEachChild(node.AsNode())!;
        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        return visited;
    }

    private Node VisitVariableStatement(VariableStatement node)
    {
        if (AstUtilities.HasSyntacticModifier(node.AsNode(), ModifierFlags.Export))
        {
            var oldInExportedVariableStatement = inExportedVariableStatement;
            inExportedVariableStatement = true;
            try
            {
                return Visitor().VisitEachChild(node.AsNode())!;
            }
            finally
            {
                inExportedVariableStatement = oldInExportedVariableStatement;
            }
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitVariableDeclaration(VariableDeclaration node)
    {
        if (inExportedVariableStatement)
        {
            inExportedVariableStatement = false;
            try
            {
                return VisitVariableDeclarationWorker(node, true);
            }
            finally
            {
                inExportedVariableStatement = true;
            }
        }
        return VisitVariableDeclarationWorker(node, false);
    }

    private Node VisitVariableDeclarationWorker(VariableDeclaration node, bool exported)
    {
        // If we are here it is because the name contains a binding pattern with a rest somewhere in it.
        if (AstUtilities.IsBindingPattern(node.Name()) &&
            (node.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0)
        {
            return global::Ts.Transformers.Transformers.FlattenDestructuringBinding(
                this,
                node.AsNode(),
                null,
                FlattenLevel.ObjectRest,
                exported,
                false)!;
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitBinaryExpression(BinaryExpression node, bool resultIsUnused)
    {
        if (AstUtilities.IsDestructuringAssignment(node.AsNode()) &&
            (node.Left!.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0)
        {
            return global::Ts.Transformers.Transformers.FlattenDestructuringAssignment(
                this,
                node.AsNode(),
                !resultIsUnused,
                FlattenLevel.ObjectRest,
                null);
        }
        if (node.OperatorToken!.Kind == SyntaxKind.CommaToken)
        {
            expressionResultIsUnused = true;
            var left = Visitor().VisitNode(node.Left);
            expressionResultIsUnused = resultIsUnused;
            var right = Visitor().VisitNode(node.Right);
            return Factory().AsNodeFactory().UpdateBinaryExpression(node, null, left, null, node.OperatorToken, right);
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    private Node VisitObjectLiteralExpression(ObjectLiteralExpression node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) == 0)
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }

        // Spread elements are chunked into left-associated __assign calls. More than two helper
        // arguments would evaluate later chunks too early and change observable side effects.
        var objects = ChunkObjectLiteralElements(node.Properties);
        if (objects.Count > 0 && objects[0].Kind != SyntaxKind.ObjectLiteralExpression)
        {
            objects.Insert(0, Factory().NewObjectLiteralExpression(Factory().NewNodeList([]), false));
        }
        if (objects.Count > 1)
        {
            Node expression = objects[0];
            for (var i = 1; i < objects.Count; i++)
            {
                expression = Factory().NewAssignHelper([expression, objects[i]], compilerOptions.GetEmitScriptTarget());
            }
            return expression;
        }
        return Factory().NewAssignHelper(objects.ToArray(), compilerOptions.GetEmitScriptTarget());
    }

    private List<Node> ChunkObjectLiteralElements(NodeList? list)
    {
        if (list is null || list.Nodes.Length == 0)
        {
            return [];
        }
        List<Node> chunkObject = [];
        List<Node> objects = [];
        foreach (var element in list.Nodes)
        {
            if (element.Kind == SyntaxKind.SpreadAssignment)
            {
                if (chunkObject.Count > 0)
                {
                    objects.Add(Factory().NewObjectLiteralExpression(Factory().NewNodeList(chunkObject.ToArray()), false));
                    chunkObject.Clear();
                }
                objects.Add(Visitor().VisitNode(element.Expression())!);
            }
            else
            {
                Node visitedElement;
                if (element.Kind == SyntaxKind.PropertyAssignment)
                {
                    visitedElement = Factory().NewPropertyAssignment(
                        null,
                        element.Name(),
                        null,
                        null,
                        Visitor().VisitNode(element.Initializer()));
                }
                else
                {
                    visitedElement = Visitor().VisitNode(element)!;
                }
                chunkObject.Add(visitedElement);
            }
        }
        if (chunkObject.Count > 0)
        {
            objects.Add(Factory().NewObjectLiteralExpression(Factory().NewNodeList(chunkObject.ToArray()), false));
        }
        return objects;
    }

    // TODO(estransforms): Port parameter/catch/for-of body rewriting from objectrestspread.go.
    // Variable bindings, destructuring assignments, and object-literal spread are implemented above;
    // the deferred paths require the complete function-body variable-environment surface.

    internal static Transformer NewObjectRestSpreadTransformer(TransformOptions opts) => new ObjectRestSpreadTransformer(opts);
}
