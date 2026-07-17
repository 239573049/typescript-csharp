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
    private HashSet<Node>? parametersWithPrecedingObjectRestOrSpread;

    private ObjectRestSpreadTransformer(TransformOptions opts)
    {
        compilerOptions = opts.CompilerOptions!;
        NewTransformer(Visit, opts.Context);
    }

    private Node Visit(Node node)
    {
        // PORT-NOTE: The current C# parser does not yet aggregate this SubtreeFacts bit onto
        // SourceFile. Traverse unconditionally until that infrastructure surface is complete.
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
                SyntaxKind.ForOfStatement => VisitForOfStatement(node.AsForInOrOfStatement()),
                SyntaxKind.CatchClause => VisitCatchClause(node.AsCatchClause()),
                SyntaxKind.Parameter => VisitParameter(node.AsParameterDeclaration()),
                SyntaxKind.Constructor => VisitConstructorDeclaration(node.AsConstructorDeclaration()),
                SyntaxKind.GetAccessor => VisitGetAccessorDeclaration(node.AsGetAccessorDeclaration()),
                SyntaxKind.SetAccessor => VisitSetAccessorDeclaration(node.AsSetAccessorDeclaration()),
                SyntaxKind.MethodDeclaration => VisitMethodDeclaration(node.AsMethodDeclaration()),
                SyntaxKind.FunctionDeclaration => VisitFunctionDeclaration(node.AsFunctionDeclaration()),
                SyntaxKind.ArrowFunction => VisitArrowFunction(node.AsArrowFunction()),
                SyntaxKind.FunctionExpression => VisitFunctionExpression(node.AsFunctionExpression()),
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
        if (!HasSpreadAssignment(node.Properties))
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

    private static bool HasSpreadAssignment(NodeList? list)
    {
        if (list is null)
        {
            return false;
        }
        foreach (var element in list.Nodes)
        {
            if (element.Kind == SyntaxKind.SpreadAssignment)
            {
                return true;
            }
        }
        return false;
    }

    // objectrestspread.go:77-109
    private Node VisitParameter(ParameterDeclaration node)
    {
        if (parametersWithPrecedingObjectRestOrSpread != null &&
            parametersWithPrecedingObjectRestOrSpread.Contains(node.AsNode()))
        {
            var name = node.Name()!;
            if (AstUtilities.IsBindingPattern(name))
            {
                name = Factory().NewGeneratedNameForNode(node.AsNode());
            }
            return Factory().AsNodeFactory().UpdateParameterDeclaration(
                node,
                null,
                node.DotDotDotToken,
                name,
                null,
                null,
                null);
        }
        if ((node.AsNode().SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0)
        {
            // Binding patterns are converted into a generated name and are
            // evaluated inside the function body.
            return Factory().AsNodeFactory().UpdateParameterDeclaration(
                node,
                null,
                node.DotDotDotToken,
                Factory().NewGeneratedNameForNode(node.AsNode()),
                null,
                null,
                Visitor().VisitNode(node.Initializer));
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    // objectrestspread.go:111-121
    private HashSet<Node>? CollectParametersWithPrecedingObjectRestOrSpread(Node node)
    {
        HashSet<Node>? result = null;
        foreach (var parameter in node.Parameters())
        {
            if (result != null)
            {
                result.Add(parameter);
            }
            else if ((parameter.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0)
            {
                result = new HashSet<Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
            }
        }
        return result;
    }

    // objectrestspread.go:125-133
    private HashSet<Node>? EnterParameterListContext(Node node)
    {
        var old = parametersWithPrecedingObjectRestOrSpread;
        parametersWithPrecedingObjectRestOrSpread = CollectParametersWithPrecedingObjectRestOrSpread(node);
        return old;
    }

    private void ExitParameterListContext(HashSet<Node>? scope)
    {
        parametersWithPrecedingObjectRestOrSpread = scope;
    }

    // objectrestspread.go:135-147
    private Node VisitConstructorDeclaration(ConstructorDeclaration node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateConstructorDeclaration(
                node,
                node.Modifiers(),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:149-162
    private Node VisitGetAccessorDeclaration(GetAccessorDeclaration node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
                node,
                node.Modifiers(),
                Visitor().VisitNode(node.Name()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:164-177
    private Node VisitSetAccessorDeclaration(SetAccessorDeclaration node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
                node,
                node.Modifiers(),
                Visitor().VisitNode(node.Name()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:179-194
    private Node VisitMethodDeclaration(MethodDeclaration node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateMethodDeclaration(
                node,
                node.Modifiers(),
                node.AsteriskToken,
                Visitor().VisitNode(node.Name()),
                node.PostfixToken,
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:196-210
    private Node VisitFunctionDeclaration(FunctionDeclaration node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateFunctionDeclaration(
                node,
                node.Modifiers(),
                node.AsteriskToken,
                Visitor().VisitNode(node.Name()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:212-225
    private Node VisitArrowFunction(ArrowFunction node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateArrowFunction(
                node,
                node.Modifiers(),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                node.EqualsGreaterThanToken,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:227-241
    private Node VisitFunctionExpression(FunctionExpression node)
    {
        var old = EnterParameterListContext(node.AsNode());
        try
        {
            return Factory().AsNodeFactory().UpdateFunctionExpression(
                node,
                node.Modifiers(),
                node.AsteriskToken,
                Visitor().VisitNode(node.Name()),
                null,
                Visitor().VisitNodes(node.Parameters),
                null,
                null,
                TransformFunctionBody(node.AsNode()));
        }
        finally
        {
            ExitParameterListContext(old);
        }
    }

    // objectrestspread.go:243-286
    private Node? TransformFunctionBody(Node node)
    {
        // EmitContext().VisitFunctionBody is not used here because this transformer needs to inject
        // object rest assignments between visiting the body and merging the variable environment.
        EmitContext().StartVariableEnvironment();
        var body = Visitor().VisitNode(node.Body());
        var extras = EmitContext().EndVariableEnvironment();
        EmitContext().StartVariableEnvironment();
        var newStatements = CollectObjectRestAssignments(node);
        extras = EmitContext().EndAndMergeVariableEnvironment(extras)!;
        if (newStatements.Count == 0 && extras.Length == 0)
        {
            return body;
        }

        body ??= Factory().NewBlock(Factory().NewNodeList([]), true);
        var prefix = new List<Node>();
        var suffix = new List<Node>();
        if (NodeTests.IsBlock(body))
        {
            var custom = false;
            var bodyStatements = body.Statements()!;
            for (var i = 0; i < bodyStatements.Length; i++)
            {
                var statement = bodyStatements[i];
                if (!custom && AstUtilities.IsPrologueDirective(statement))
                {
                    prefix.Add(statement);
                }
                else if ((EmitContext().EmitFlags(statement) & global::Ts.Printer.EmitFlags.CustomPrologue) != 0)
                {
                    custom = true;
                    prefix.Add(statement);
                }
                else
                {
                    suffix.AddRange(bodyStatements[i..]);
                    break;
                }
            }
        }
        else
        {
            var ret = Factory().NewReturnStatement(body);
            ret.Loc = body.Loc;
            var emptyList = Factory().NewNodeList([]);
            emptyList.Loc = body.Loc;
            body = Factory().NewBlock(emptyList, true);
            suffix.Add(ret);
        }

        var combined = new List<Node>(prefix.Count + extras.Length + newStatements.Count + suffix.Count);
        combined.AddRange(prefix);
        combined.AddRange(extras);
        combined.AddRange(newStatements);
        combined.AddRange(suffix);
        var newStatementList = Factory().NewNodeList(combined.ToArray());
        newStatementList.Loc = body.StatementList()!.Loc;
        return Factory().AsNodeFactory().UpdateBlock(body.AsBlock(), newStatementList, body.AsBlock().MultiLine);
    }

    // objectrestspread.go:288-376
    private List<Node> CollectObjectRestAssignments(Node node)
    {
        var containsPrecedingObjectRestOrSpread = false;
        var results = new List<Node>();
        foreach (var parameter in node.Parameters())
        {
            if (containsPrecedingObjectRestOrSpread)
            {
                if (AstUtilities.IsBindingPattern(parameter.Name()!))
                {
                    // In cases where a binding pattern is simply '[]' or '{}',
                    // we usually don't want to emit a var declaration; however, in the presence
                    // of an initializer, we must emit that expression to preserve side effects.
                    if ((parameter.Name()!.Elements()?.Length ?? 0) > 0)
                    {
                        var declarations = global::Ts.Transformers.Transformers.FlattenDestructuringBinding(
                            this,
                            parameter, Factory().NewGeneratedNameForNode(parameter),
                            FlattenLevel.All, false, false);
                        if (declarations != null)
                        {
                            var declarationList = Factory().NewVariableDeclarationList(Factory().NewNodeList([]), NodeFlags.None);
                            Node[] decls = [declarations];
                            if (declarations.Kind == SyntaxKind.SyntaxList)
                            {
                                decls = declarations.AsSyntaxList().Children!;
                            }
                            var existing = declarationList.AsVariableDeclarationList().Declarations!.Nodes;
                            declarationList.AsVariableDeclarationList().Declarations!.Nodes = [.. existing, .. decls];
                            var statement = Factory().NewVariableStatement(null, declarationList);
                            EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.CustomPrologue);
                            results.Add(statement);
                        }
                    }
                    else if (parameter.Initializer() != null)
                    {
                        var name = Factory().NewGeneratedNameForNode(parameter);
                        var initializer = Visitor().VisitNode(parameter.Initializer())!;
                        var assignment = Factory().NewAssignmentExpression(name, initializer);
                        var statement = Factory().NewExpressionStatement(assignment);
                        EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.CustomPrologue);
                        results.Add(statement);
                    }
                }
                else if (parameter.Initializer() != null)
                {
                    // Converts a parameter initializer into a function body statement, i.e.:
                    //
                    //  function f(x = 1) { }
                    //
                    // becomes
                    //
                    //  function f(x) {
                    //    if (typeof x === "undefined") { x = 1; }
                    //  }
                    var name = parameter.Name()!.Clone(Factory())!;
                    name.Loc = parameter.Name()!.Loc;
                    EmitContext().AddEmitFlags(name, global::Ts.Printer.EmitFlags.NoSourceMap);

                    var initializer = Visitor().VisitNode(parameter.Initializer())!;
                    EmitContext().AddEmitFlags(initializer, global::Ts.Printer.EmitFlags.NoSourceMap | global::Ts.Printer.EmitFlags.NoComments);

                    var assignment = Factory().NewAssignmentExpression(name, initializer);
                    assignment.Loc = parameter.Loc;
                    EmitContext().AddEmitFlags(assignment, global::Ts.Printer.EmitFlags.NoComments);

                    var block = Factory().NewBlock(Factory().NewNodeList([Factory().NewExpressionStatement(assignment)]), false);
                    block.Loc = parameter.Loc;
                    EmitContext().AddEmitFlags(block, global::Ts.Printer.EmitFlags.SingleLine | global::Ts.Printer.EmitFlags.NoTrailingSourceMap | global::Ts.Printer.EmitFlags.NoTokenSourceMaps | global::Ts.Printer.EmitFlags.NoComments);

                    var typeCheck = Factory().NewTypeCheck(name.Clone(Factory())!, "undefined");
                    var statement = Factory().AsNodeFactory().NewIfStatement(typeCheck, block, null);
                    statement.Loc = parameter.Loc;
                    EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.NoTokenSourceMaps | global::Ts.Printer.EmitFlags.NoTrailingSourceMap | global::Ts.Printer.EmitFlags.CustomPrologue | global::Ts.Printer.EmitFlags.NoComments | global::Ts.Printer.EmitFlags.StartOnNewLine);
                    results.Add(statement);
                }
            }
            else if ((parameter.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0)
            {
                containsPrecedingObjectRestOrSpread = true;
                var declarations = global::Ts.Transformers.Transformers.FlattenDestructuringBinding(
                    this,
                    parameter, Factory().NewGeneratedNameForNode(parameter),
                    FlattenLevel.ObjectRest, false, true);
                if (declarations != null)
                {
                    var declarationList = Factory().NewVariableDeclarationList(Factory().NewNodeList([]), NodeFlags.None);
                    Node[] decls = [declarations];
                    if (declarations.Kind == SyntaxKind.SyntaxList)
                    {
                        decls = declarations.AsSyntaxList().Children!;
                    }
                    var existing = declarationList.AsVariableDeclarationList().Declarations!.Nodes;
                    declarationList.AsVariableDeclarationList().Declarations!.Nodes = [.. existing, .. decls];
                    var statement = Factory().NewVariableStatement(null, declarationList);
                    EmitContext().AddEmitFlags(statement, global::Ts.Printer.EmitFlags.CustomPrologue);
                    results.Add(statement);
                }
            }
        }

        return results;
    }

    // objectrestspread.go:378-410
    private Node VisitCatchClause(CatchClause node)
    {
        if (node.VariableDeclaration != null && AstUtilities.IsBindingPattern(node.VariableDeclaration.Name()!) &&
            (node.VariableDeclaration.Name()!.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0)
        {
            var name = Factory().NewGeneratedNameForNode(node.VariableDeclaration.Name()!);
            var updatedDecl = Factory().AsNodeFactory().UpdateVariableDeclaration(node.VariableDeclaration.AsVariableDeclaration(), node.VariableDeclaration.Name(), null, null, name);
            var visitedBindings = global::Ts.Transformers.Transformers.FlattenDestructuringBinding(
                this,
                updatedDecl, null,
                FlattenLevel.ObjectRest, false, false);
            var block = Visitor().VisitNode(node.Block)!;
            if (visitedBindings != null)
            {
                Node[] decls;
                if (visitedBindings.Kind == SyntaxKind.SyntaxList)
                {
                    decls = visitedBindings.AsSyntaxList().Children!;
                }
                else
                {
                    decls = [visitedBindings];
                }
                var newStatement = Factory().NewVariableStatement(null, Factory().NewVariableDeclarationList(Factory().NewNodeList(decls), NodeFlags.None));
                var statements = new List<Node> { newStatement };
                statements.AddRange(block.Statements()!);
                var statementList = Factory().NewNodeList(statements.ToArray());
                statementList.Loc = block.StatementList()!.Loc;

                block = Factory().AsNodeFactory().UpdateBlock(block.AsBlock(), statementList, block.AsBlock().MultiLine);
            }
            return Factory().AsNodeFactory().UpdateCatchClause(
                node,
                Factory().AsNodeFactory().UpdateVariableDeclaration(node.VariableDeclaration.AsVariableDeclaration(), name, null, null, null),
                block);
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    // objectrestspread.go:445-496
    private Node VisitForOfStatement(ForInOrOfStatement node)
    {
        if ((node.Initializer!.SubtreeFacts() & SubtreeFacts.SubtreeContainsObjectRestOrSpread) != 0 ||
            (global::Ts.Transformers.TransformerAstAdapters.IsAssignmentPattern(node.Initializer) && AstUtilities.ContainsObjectRestOrSpread(node.Initializer)))
        {
            var initializerWithoutParens = AstUtilities.SkipParentheses(node.Initializer);
            if (NodeTests.IsVariableDeclarationList(initializerWithoutParens) || global::Ts.Transformers.TransformerAstAdapters.IsAssignmentPattern(initializerWithoutParens))
            {
                var bodyLocation = default(TextRange);
                var statementsLocation = default(TextRange);
                var temp = Factory().NewTempVariable();
                var res = Visitor().VisitNode(Factory().CreateForOfBindingStatement(initializerWithoutParens, temp));
                var statements = new List<Node>(1);
                if (res != null)
                {
                    statements.Add(res);
                }
                if (NodeTests.IsBlock(node.Statement!))
                {
                    foreach (var statement in node.Statement!.Statements()!)
                    {
                        var visited = Visitor().VisitEachChild(statement);
                        if (visited != null)
                        {
                            statements.Add(visited);
                        }
                    }
                    bodyLocation = node.Statement!.Loc;
                    statementsLocation = node.Statement!.StatementList()!.Loc;
                }
                else if (node.Statement != null)
                {
                    statements.Add(Visitor().VisitEachChild(node.Statement)!);
                    bodyLocation = node.Statement.Loc;
                    statementsLocation = node.Statement.Loc;
                }

                var list = Factory().NewVariableDeclarationList(
                    Factory().NewNodeList([Factory().NewVariableDeclaration(temp, null, null, null)]),
                    NodeFlags.Let);
                list.Loc = node.Initializer.Loc;

                var expr = Visitor().VisitEachChild(node.Expression);

                var statementsList = Factory().NewNodeList(statements.ToArray());
                statementsList.Loc = statementsLocation;

                var block = Factory().NewBlock(statementsList, true);
                block.Loc = bodyLocation;

                return Factory().AsNodeFactory().UpdateForInOrOfStatement(
                    node,
                    node.AwaitModifier,
                    list,
                    expr,
                    block);
            }
        }
        return Visitor().VisitEachChild(node.AsNode())!;
    }

    internal static Transformer NewObjectRestSpreadTransformer(TransformOptions opts) => new ObjectRestSpreadTransformer(opts);
}
