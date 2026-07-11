// Ported from tsgo internal/transformers/estransforms/taggedtemplate.go.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal sealed class TaggedTemplateTransformer : Transformer
{
    private SourceFile currentSourceFile = null!;
    private readonly List<Node> taggedTemplateStringDeclarations = [];

    private TaggedTemplateTransformer(TransformOptions opts) => NewTransformer(Visit, opts.Context);

    private Node Visit(Node node)
    {
        // PORT-NOTE: The current C# parser does not yet aggregate this SubtreeFacts bit onto
        // SourceFile. Traverse unconditionally until that infrastructure surface is complete.
        return node.Kind switch
        {
            SyntaxKind.SourceFile => VisitSourceFile(node.AsSourceFile()),
            SyntaxKind.TaggedTemplateExpression => VisitTaggedTemplateExpression(node.AsTaggedTemplateExpression()),
            _ => Visitor().VisitEachChild(node)!,
        };
    }

    private Node VisitSourceFile(SourceFile node)
    {
        currentSourceFile = node;
        taggedTemplateStringDeclarations.Clear();
        var visited = Visitor().VisitEachChild(node.AsNode())!;

        if (taggedTemplateStringDeclarations.Count > 0)
        {
            var visitedSourceFile = visited.AsSourceFile();
            var statements = new Node[visitedSourceFile.Statements!.Nodes.Length + 1];
            Array.Copy(visitedSourceFile.Statements.Nodes, statements, visitedSourceFile.Statements.Nodes.Length);
            statements[^1] = Factory().NewVariableStatement(
                null,
                Factory().NewVariableDeclarationList(
                    Factory().NewNodeList(taggedTemplateStringDeclarations.ToArray()),
                    NodeFlags.None));
            var statementList = Factory().NewNodeList(statements);
            statementList.Loc = node.Statements!.Loc;
            visited = Factory().AsNodeFactory().UpdateSourceFile(visitedSourceFile, statementList, visitedSourceFile.EndOfFileToken);
        }

        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        return visited;
    }

    private Node VisitTaggedTemplateExpression(TaggedTemplateExpression node) => ProcessTaggedTemplateExpression(node);

    private Node ProcessTaggedTemplateExpression(TaggedTemplateExpression node)
    {
        var tag = Visitor().VisitNode(node.Tag)!;
        var template = node.Template!;

        if (!HasInvalidEscape(template))
        {
            return Visitor().VisitEachChild(node.AsNode())!;
        }

        var factory = Factory();
        List<Node> templateArguments = [null!]; // placeholder for the template object
        List<Node> cookedStrings = [];
        List<Node> rawStrings = [];

        if (NodeTests.IsNoSubstitutionTemplateLiteral(template))
        {
            cookedStrings.Add(CreateTemplateCooked(factory, template.TemplateLiteralLikeData()!));
            rawStrings.Add(GetRawLiteral(factory, template));
        }
        else
        {
            var templateExpression = template.AsTemplateExpression();
            cookedStrings.Add(CreateTemplateCooked(factory, templateExpression.Head!.TemplateLiteralLikeData()!));
            rawStrings.Add(GetRawLiteral(factory, templateExpression.Head));
            foreach (var spanNode in templateExpression.TemplateSpans!.Nodes)
            {
                var span = spanNode.AsTemplateSpan();
                cookedStrings.Add(CreateTemplateCooked(factory, span.Literal!.TemplateLiteralLikeData()!));
                rawStrings.Add(GetRawLiteral(factory, span.Literal));
                templateArguments.Add(Visitor().VisitNode(span.Expression)!);
            }
        }

        var helperCall = factory.NewTemplateObjectHelper(
            factory.NewArrayLiteralExpression(factory.NewNodeList(cookedStrings.ToArray()), false),
            factory.NewArrayLiteralExpression(factory.NewNodeList(rawStrings.ToArray()), false));

        // Create a variable to cache the template object if we're in a module.
        // Do not do this in the global scope, as any variable we currently generate could conflict with
        // variables from outside of the current compilation. In the future, we can revisit this behavior.
        if (AstUtilities.IsExternalModule(currentSourceFile))
        {
            var tempVariable = factory.NewUniqueName("templateObject");
            taggedTemplateStringDeclarations.Add(factory.NewVariableDeclaration(tempVariable, null, null, null));
            templateArguments[0] = factory.NewLogicalORExpression(
                tempVariable,
                factory.NewAssignmentExpression(tempVariable, helperCall));
        }
        else
        {
            templateArguments[0] = helperCall;
        }

        var call = factory.NewCallExpression(tag, null, null, factory.NewNodeList(templateArguments.ToArray()), NodeFlags.None);
        call.Loc = node.Loc;
        return call;
    }

    private static Node CreateTemplateCooked(global::Ts.Printer.NodeFactory factory, TemplateLiteralLikeNodeBase template)
    {
        if ((template.TemplateFlags & TokenFlags.IsInvalid) != 0)
        {
            return factory.NewVoidZeroExpression();
        }
        return factory.NewStringLiteral(template.Text, TokenFlags.None);
    }

    private static Node GetRawLiteral(global::Ts.Printer.NodeFactory factory, Node node)
    {
        var text = node.TemplateLiteralLikeData()!.RawText;
        if (text.Length == 0)
        {
            text = global::Ts.Scanner.Scanner.GetSourceTextOfNodeFromSourceFile(
                AstUtilities.GetSourceFileOfNode(node)!,
                node,
                false /*includeTrivia*/);
            // text contains the original source, it will also contain quotes ("`"), dollar signs and braces ("${" and "}"),
            // thus we need to remove those characters.
            // First template piece starts with "`", others with "}"
            // Last template piece ends with "`", others with "${"
            var isLast = node.Kind is SyntaxKind.NoSubstitutionTemplateLiteral or SyntaxKind.TemplateTail;
            var endLength = isLast ? 1 : 2;
            text = text.Substring(1, text.Length - 1 - endLength);
        }

        // Newline normalization:
        // ES6 Spec 11.8.6.1 - Static Semantics of TV's and TRV's
        // <CR><LF> and <CR> LineTerminatorSequences are normalized to <LF> for both TV and TRV.
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

        var result = factory.NewStringLiteral(text, TokenFlags.None);
        result.Loc = node.Loc;
        return result;
    }

    private static bool HasInvalidEscape(Node template)
    {
        if (NodeTests.IsNoSubstitutionTemplateLiteral(template))
        {
            return (template.TemplateLiteralLikeData()!.TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0;
        }
        var templateExpression = template.AsTemplateExpression();
        if ((templateExpression.Head!.TemplateLiteralLikeData()!.TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0)
        {
            return true;
        }
        foreach (var spanNode in templateExpression.TemplateSpans!.Nodes)
        {
            if ((spanNode.AsTemplateSpan().Literal!.TemplateLiteralLikeData()!.TemplateFlags & TokenFlags.ContainsInvalidEscape) != 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static Transformer NewTaggedTemplateLiftRestrictionTransformer(TransformOptions opts) =>
        new TaggedTemplateTransformer(opts);
}
