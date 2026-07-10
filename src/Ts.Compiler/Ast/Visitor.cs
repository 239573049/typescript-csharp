using System;
using System.Collections.Generic;

namespace Ts.Ast;

// Go: internal/ast/visitor.go:16-26. These hooks intercept the default behavior of
// the visitor; delegate parameters/results mirror Go pointers as nullable references.
public struct NodeVisitorHooks
{
    public Func<Node?, NodeVisitor, Node?>? VisitNode; // Overrides visiting a Node. Only invoked by the VisitEachChild method on a given Node subtype.
    public Func<TokenNode?, NodeVisitor, Node?>? VisitToken; // Overrides visiting a TokenNode.
    public Func<NodeList?, NodeVisitor, NodeList?>? VisitNodes; // Overrides visiting a NodeList.
    public Func<ModifierList?, NodeVisitor, ModifierList?>? VisitModifiers; // Overrides visiting a ModifierList.
    public Func<Statement?, NodeVisitor, Statement?>? VisitEmbeddedStatement; // Overrides visiting the embedded statement body of an iteration statement, `if` statement, or `with` statement.
    public Func<Statement?, NodeVisitor, Statement?>? VisitIterationBody; // Overrides visiting the embedded statement body of an iteration statement.
    public Func<ParameterList?, NodeVisitor, ParameterList?>? VisitParameters; // Overrides visiting a ParameterList.
    public Func<BlockOrExpression?, NodeVisitor, BlockOrExpression?>? VisitFunctionBody; // Overrides visiting a function body.
    public Func<StatementList?, NodeVisitor, StatementList?>? VisitTopLevelStatements; // Overrides visiting a variable environment.
}

// Go: internal/ast/visitor.go:9-13 + NewNodeVisitor (:28-33). The internal camelCase
// methods keep their Go spelling: they are what generated VisitEachChild bodies call,
// and PascalCasing them would collide with the public methods of the same name.
public sealed class NodeVisitor
{
    public Func<Node, Node?>? Visit; // Required. The callback used to visit a node
    public NodeFactory Factory; // Required. The NodeFactory used to produce new nodes when passed to VisitEachChild
    public NodeVisitorHooks Hooks; // Hooks to be invoked when visiting a node

    // visitor.go:28-33
    public NodeVisitor(Func<Node, Node?>? visit, NodeFactory? factory, NodeVisitorHooks hooks)
    {
        Visit = visit;
        Factory = factory ?? new NodeFactory();
        Hooks = hooks;
    }

    // visitor.go:35-37
    public SourceFile VisitSourceFile(SourceFile node)
    {
        return VisitNode(node.AsNode())!.AsSourceFile();
    }

    // Visits a Node, possibly returning a new Node in its place (visitor.go:39-66).
    //
    //   - If the input node is null, then the output is null.
    //   - If Visit is null, then the output is the input.
    //   - If Visit returns null, then the output is null.
    //   - If Visit returns a SyntaxList Node, then the output is the only child of the SyntaxList Node.
    public Node? VisitNode(Node? node)
    {
        if (node == null || Visit == null)
        {
            return node;
        }
        var visited = Visit(node);
        if (visited != null && visited.Kind == SyntaxKind.SyntaxList)
        {
            var nodes = visited.AsSyntaxList().Children;
            if (nodes == null || nodes.Length != 1)
            {
                throw new InvalidOperationException("Expected only a single node to be written to output");
            }
            visited = nodes[0];
            if (visited != null && visited.Kind == SyntaxKind.SyntaxList)
            {
                throw new InvalidOperationException("The result of visiting and lifting a Node may not be SyntaxList");
            }
        }
        return visited;
    }

    // Visits an embedded Statement, possibly returning a new Statement in its place
    // (visitor.go:68-84).
    public Statement? VisitEmbeddedStatement(Statement? node)
    {
        if (node == null || Visit == null)
        {
            return node;
        }
        var visited = Visit(node);
        if (visited == null)
        {
            return null;
        }
        return liftToBlock(visited);
    }

    // Visits a NodeList, possibly returning a new NodeList in its place (visitor.go:86-106).
    public NodeList? VisitNodes(NodeList? nodes)
    {
        if (nodes == null || Visit == null)
        {
            return nodes;
        }
        var (result, changed) = VisitSlice(nodes.Nodes);
        if (changed)
        {
            var list = Factory.NewNodeList(result!);
            list.Loc = nodes.Loc;
            return list;
        }
        return nodes;
    }

    // Visits a ModifierList, possibly returning a new ModifierList in its place
    // (visitor.go:108-128).
    public ModifierList? VisitModifiers(ModifierList? nodes)
    {
        if (nodes == null || Visit == null)
        {
            return nodes;
        }
        var (result, changed) = VisitSlice(nodes.Nodes);
        if (changed)
        {
            var list = Factory.NewModifierList(result!);
            list.Loc = nodes.Loc;
            return list;
        }
        return nodes;
    }

    // Visits a slice of Nodes, returning the resulting slice and a value indicating
    // whether it was changed (visitor.go:130-183).
    public (Node[]? Nodes, bool Changed) VisitSlice(Node[]? nodes)
    {
        if (nodes == null || Visit == null)
        {
            return (nodes, false);
        }
        for (int i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i];
            if (Visit == null)
            {
                break;
            }
            var visited = Visit(node);
            if (visited == null || visited != node)
            {
                var updated = new List<Node>(nodes.Length);
                for (int j = 0; j < i; j++)
                {
                    updated.Add(nodes[j]);
                }
                while (true)
                {
                    // finish prior loop
                    if (visited == null)
                    {
                        // do nothing
                    }
                    else if (visited.Kind == SyntaxKind.SyntaxList)
                    {
                        var children = visited.AsSyntaxList().Children;
                        if (children != null)
                        {
                            updated.AddRange(children);
                        }
                    }
                    else
                    {
                        updated.Add(visited);
                    }

                    i++;

                    // loop over remaining elements
                    if (i >= nodes.Length)
                    {
                        break;
                    }

                    if (Visit != null)
                    {
                        node = nodes[i];
                        visited = Visit(node);
                    }
                    else
                    {
                        for (int j = i; j < nodes.Length; j++)
                        {
                            updated.Add(nodes[j]);
                        }
                        break;
                    }
                }
                return (updated.ToArray(), true);
            }
        }
        return (nodes, false);
    }

    // Visits each child of a Node, possibly returning a new Node of the same kind in
    // its place (visitor.go:185-192).
    public Node? VisitEachChild(Node? node)
    {
        if (node == null || Visit == null)
        {
            return node;
        }
        return node.VisitEachChild(this);
    }

    // visitor.go:194-199
    internal Node? visitNode(Node? node)
    {
        if (Hooks.VisitNode != null)
        {
            return Hooks.VisitNode(node, this);
        }
        return VisitNode(node);
    }

    // visitor.go:201-209
    internal Node? visitEmbeddedStatement(Node? node)
    {
        if (Hooks.VisitEmbeddedStatement != null)
        {
            return Hooks.VisitEmbeddedStatement(node, this);
        }
        if (Hooks.VisitNode != null)
        {
            return liftToBlock(Hooks.VisitNode(node, this));
        }
        return VisitEmbeddedStatement(node);
    }

    // visitor.go:211-216
    internal Statement? visitIterationBody(Statement? node)
    {
        if (Hooks.VisitIterationBody != null)
        {
            return Hooks.VisitIterationBody(node, this);
        }
        return visitEmbeddedStatement(node);
    }

    // visitor.go:218-223
    internal BlockOrExpression? visitFunctionBody(BlockOrExpression? node)
    {
        if (Hooks.VisitFunctionBody != null)
        {
            return Hooks.VisitFunctionBody(node, this);
        }
        return visitNode(node);
    }

    // visitor.go:225-230
    internal Node? visitToken(Node? node)
    {
        if (Hooks.VisitToken != null)
        {
            return Hooks.VisitToken(node, this);
        }
        return VisitNode(node);
    }

    // visitor.go:232-237
    internal NodeList? visitNodes(NodeList? nodes)
    {
        if (Hooks.VisitNodes != null)
        {
            return Hooks.VisitNodes(nodes, this);
        }
        return VisitNodes(nodes);
    }

    // visitor.go:239-244
    internal ModifierList? visitModifiers(ModifierList? nodes)
    {
        if (Hooks.VisitModifiers != null)
        {
            return Hooks.VisitModifiers(nodes, this);
        }
        return VisitModifiers(nodes);
    }

    // visitor.go:246-251
    internal ParameterList? visitParameters(ParameterList? nodes)
    {
        if (Hooks.VisitParameters != null)
        {
            return Hooks.VisitParameters(nodes, this);
        }
        return visitNodes(nodes);
    }

    // visitor.go:253-258
    internal StatementList? visitTopLevelStatements(StatementList? nodes)
    {
        if (Hooks.VisitTopLevelStatements != null)
        {
            return Hooks.VisitTopLevelStatements(nodes, this);
        }
        return visitNodes(nodes);
    }

    // visitor.go:260-278
    internal Statement? liftToBlock(Statement? node)
    {
        Node[]? nodes = null;
        if (node != null)
        {
            if (node.Kind == SyntaxKind.SyntaxList)
            {
                nodes = node.AsSyntaxList().Children;
            }
            else
            {
                nodes = new[] { node };
            }
        }
        if (nodes != null && nodes.Length == 1)
        {
            node = nodes[0];
        }
        else
        {
            node = Factory.NewBlock(Factory.NewNodeList(nodes ?? Array.Empty<Node>()), true /*multiLine*/);
        }
        if (node.Kind == SyntaxKind.SyntaxList)
        {
            throw new InvalidOperationException("The result of visiting and lifting a Node may not be SyntaxList");
        }
        return node;
    }
}
