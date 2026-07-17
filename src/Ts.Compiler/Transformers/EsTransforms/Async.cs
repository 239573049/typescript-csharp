// Ported from tsgo internal/transformers/estransforms/async.go — the ES2017 async-function
// downlevel transformer (async/await → __awaiter + generator for targets < ES2017).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;
using PrinterNs = global::Ts.Printer;

// async.go:12-17
[Flags]
internal enum AsyncContextFlags
{
    None = 0,
    NonTopLevel = 1 << 0,
    HasLexicalThis = 1 << 1,
}

// async.go:19-22
internal struct LexicalArgumentsInfo
{
    internal IdentifierNode? binding;
    internal bool used;
}

internal sealed class AsyncTransformer : Transformer
{
    // async.go:26 — superAccessState (Go embedding → composition; `super` fields/methods delegate).
    private readonly SuperAccessState super = new();

    private AsyncContextFlags contextFlags;

    private global::Ts.Collections.Set<string>? enclosingFunctionParameterNames;
    private LexicalArgumentsInfo lexicalArguments;

    private NodeVisitor asyncBodyVisitor = null!;
    private NodeVisitor fallbackNodeVisitor = null!;

    // async.go:37-44
    private AsyncTransformer(TransformOptions opts)
    {
        NewTransformer(Visit, opts.Context);
        super.InitSuperAccessVisitor(EmitContext(), Factory());
        asyncBodyVisitor = EmitContext().NewNodeVisitor(VisitAsyncBodyNode);
        fallbackNodeVisitor = EmitContext().NewNodeVisitor(VisitFallback);
    }

    internal static Transformer NewAsyncTransformer(TransformOptions opts) => new AsyncTransformer(opts);

    // async.go:46-56
    private Node VisitSourceFile(SourceFile node)
    {
        if (node.IsDeclarationFile)
        {
            return node.AsNode();
        }

        SetContextFlag(AsyncContextFlags.NonTopLevel, false);
        SetContextFlag(AsyncContextFlags.HasLexicalThis, false);
        var visited = Visitor().VisitEachChild(node.AsNode())!;
        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        return visited;
    }

    // async.go:58-64
    private void SetContextFlag(AsyncContextFlags flag, bool val)
    {
        if (val)
        {
            contextFlags |= flag;
        }
        else
        {
            contextFlags &= ~flag;
        }
    }

    // async.go:66-68
    private bool InContext(AsyncContextFlags flags) => (contextFlags & flags) != 0;

    // async.go:70-72
    private bool InTopLevelContext() => !InContext(AsyncContextFlags.NonTopLevel);

    // async.go:74-76
    private bool InHasLexicalThisContext() => InContext(AsyncContextFlags.HasLexicalThis);

    // async.go:78-87
    private Node? DoWithContext(AsyncContextFlags flags, Func<AsyncTransformer, Node, Node?> cb, Node node)
    {
        var flagsToSet = flags & ~contextFlags;
        if (flagsToSet != AsyncContextFlags.None)
        {
            SetContextFlag(flagsToSet, true);
            var result = cb(this, node);
            SetContextFlag(flagsToSet, false);
            return result;
        }
        return cb(this, node);
    }

    // async.go:89-91
    private Node? VisitDefault(Node node) => Visitor().VisitEachChild(node);

    // async.go:93-120
    private Node? FallbackVisitorCore(Node node)
    {
        if (super.capturedSuperProperties == null && lexicalArguments.binding == null)
        {
            return node;
        }
        super.TrackSuperAccess(node);
        switch (node.Kind)
        {
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.Constructor:
                return node;
            case SyntaxKind.Parameter:
            case SyntaxKind.BindingElement:
            case SyntaxKind.VariableDeclaration:
                // fall through to visitEachChild
                break;
            case SyntaxKind.Identifier:
                if (lexicalArguments.binding != null &&
                    node.Text() == "arguments" &&
                    !AstUtilities.IsIdentifierName(node) &&
                    !AstUtilities.IsLabelName(node))
                {
                    lexicalArguments.used = true;
                    return lexicalArguments.binding;
                }
                break;
        }
        return fallbackNodeVisitor.VisitEachChild(node);
    }

    // async.go:122-124
    private Node? VisitFallback(Node node) => FallbackVisitorCore(node);

    // async.go:126-163
    private Node? Visit(Node node)
    {
        var restoreLexicalThis = false;
        if ((EmitContext().EmitFlags(node) & EF.NoLexicalThis) != 0 && InHasLexicalThisContext())
        {
            SetContextFlag(AsyncContextFlags.HasLexicalThis, false);
            restoreLexicalThis = true;
        }
        try
        {
            if ((node.SubtreeFacts() & (SubtreeFacts.SubtreeContainsAnyAwait | SubtreeFacts.SubtreeContainsAwait)) == 0)
            {
                return FallbackVisitorCore(node);
            }
            super.TrackSuperAccess(node);
            switch (node.Kind)
            {
                case SyntaxKind.AsyncKeyword:
                    // ES2017 async modifier should be elided for targets < ES2017
                    return null;
                case SyntaxKind.SourceFile:
                    return VisitSourceFile(node.AsSourceFile());
                case SyntaxKind.AwaitExpression:
                    return VisitAwaitExpression(node.AsAwaitExpression());
                case SyntaxKind.MethodDeclaration:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitMethodDeclaration(n), node);
                case SyntaxKind.FunctionDeclaration:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitFunctionDeclaration(n), node);
                case SyntaxKind.FunctionExpression:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitFunctionExpression(n), node);
                case SyntaxKind.ArrowFunction:
                    return DoWithContext(AsyncContextFlags.NonTopLevel, static (tx, n) => tx.VisitArrowFunction(n), node);
                case SyntaxKind.GetAccessor:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitGetAccessorDeclaration(n), node);
                case SyntaxKind.SetAccessor:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitSetAccessorDeclaration(n), node);
                case SyntaxKind.Constructor:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitConstructorDeclaration(n), node);
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                    return DoWithContext(AsyncContextFlags.NonTopLevel | AsyncContextFlags.HasLexicalThis, static (tx, n) => tx.VisitDefault(n), node);
                default:
                    return Visitor().VisitEachChild(node);
            }
        }
        finally
        {
            if (restoreLexicalThis)
            {
                SetContextFlag(AsyncContextFlags.HasLexicalThis, true);
            }
        }
    }

    // async.go:165-193
    private Node? VisitAsyncBodyNode(Node node)
    {
        if (IsNodeWithPossibleHoistedDeclaration(node))
        {
            switch (node.Kind)
            {
                case SyntaxKind.VariableStatement:
                    return VisitVariableStatementInAsyncBody(node);
                case SyntaxKind.ForStatement:
                    return VisitForStatementInAsyncBody(node.AsForStatement());
                case SyntaxKind.ForInStatement:
                    return VisitForInStatementInAsyncBody(node.AsForInOrOfStatement());
                case SyntaxKind.ForOfStatement:
                    return VisitForOfStatementInAsyncBody(node.AsForInOrOfStatement());
                case SyntaxKind.CatchClause:
                    return VisitCatchClauseInAsyncBody(node.AsCatchClause());
                case SyntaxKind.Block:
                case SyntaxKind.SwitchStatement:
                case SyntaxKind.CaseBlock:
                case SyntaxKind.CaseClause:
                case SyntaxKind.DefaultClause:
                case SyntaxKind.TryStatement:
                case SyntaxKind.DoStatement:
                case SyntaxKind.WhileStatement:
                case SyntaxKind.IfStatement:
                case SyntaxKind.WithStatement:
                case SyntaxKind.LabeledStatement:
                    return asyncBodyVisitor.VisitEachChild(node);
            }
        }
        return Visit(node);
    }

    // async.go:195-220
    private Node? VisitCatchClauseInAsyncBody(CatchClause node)
    {
        var catchClauseNames = new global::Ts.Collections.Set<string>();
        if (node.VariableDeclaration != null)
        {
            RecordDeclarationName(node.VariableDeclaration, catchClauseNames);
        }

        // names declared in a catch variable are block scoped
        global::Ts.Collections.Set<string>? catchClauseUnshadowedNames = null;
        foreach (var escapedName in global::Ts.Collections.SetExtensions.Keys(catchClauseNames)) // map-iter: 序无关 (只做成员删除)
        {
            if (enclosingFunctionParameterNames != null && global::Ts.Collections.SetExtensions.Has(enclosingFunctionParameterNames, escapedName))
            {
                catchClauseUnshadowedNames ??= global::Ts.Collections.SetExtensions.Clone(enclosingFunctionParameterNames);
                catchClauseUnshadowedNames!.Delete(escapedName);
            }
        }

        if (catchClauseUnshadowedNames != null)
        {
            var savedEnclosingFunctionParameterNames = enclosingFunctionParameterNames;
            enclosingFunctionParameterNames = catchClauseUnshadowedNames;
            var result = asyncBodyVisitor.VisitEachChild(node.AsNode());
            enclosingFunctionParameterNames = savedEnclosingFunctionParameterNames;
            return result;
        }
        return asyncBodyVisitor.VisitEachChild(node.AsNode());
    }

    // async.go:222-232
    private Node? VisitVariableStatementInAsyncBody(Node node)
    {
        var declList = node.AsVariableStatement().DeclarationList;
        if (IsVariableDeclarationListWithCollidingName(declList))
        {
            var expression = VisitVariableDeclarationListWithCollidingNames(declList!.AsVariableDeclarationList(), false);
            if (expression != null)
            {
                return Factory().NewExpressionStatement(expression);
            }
            return null;
        }
        return Visitor().VisitEachChild(node);
    }

    // async.go:234-249
    private Node VisitForInStatementInAsyncBody(ForInOrOfStatement node)
    {
        Node? visitedInitializer;
        if (IsVariableDeclarationListWithCollidingName(node.Initializer))
        {
            visitedInitializer = VisitVariableDeclarationListWithCollidingNames(node.Initializer!.AsVariableDeclarationList(), true);
        }
        else
        {
            visitedInitializer = Visitor().VisitNode(node.Initializer);
        }

        return Factory().AsNodeFactory().UpdateForInOrOfStatement(
            node,
            null, /*awaitModifier*/
            visitedInitializer,
            Visitor().VisitNode(node.Expression),
            asyncBodyVisitor.VisitEmbeddedStatement(node.Statement));
    }

    // async.go:251-266
    private Node VisitForOfStatementInAsyncBody(ForInOrOfStatement node)
    {
        Node? visitedInitializer;
        if (IsVariableDeclarationListWithCollidingName(node.Initializer))
        {
            visitedInitializer = VisitVariableDeclarationListWithCollidingNames(node.Initializer!.AsVariableDeclarationList(), true);
        }
        else
        {
            visitedInitializer = Visitor().VisitNode(node.Initializer);
        }

        return Factory().AsNodeFactory().UpdateForInOrOfStatement(
            node,
            Visitor().VisitNode(node.AwaitModifier),
            visitedInitializer,
            Visitor().VisitNode(node.Expression),
            asyncBodyVisitor.VisitEmbeddedStatement(node.Statement));
    }

    // async.go:268-284
    private Node VisitForStatementInAsyncBody(ForStatement node)
    {
        var initializer = node.Initializer;
        Node? visitedInitializer;
        if (initializer != null && IsVariableDeclarationListWithCollidingName(initializer))
        {
            visitedInitializer = VisitVariableDeclarationListWithCollidingNames(initializer.AsVariableDeclarationList(), false);
        }
        else
        {
            visitedInitializer = Visitor().VisitNode(node.Initializer);
        }

        return Factory().AsNodeFactory().UpdateForStatement(
            node,
            visitedInitializer,
            Visitor().VisitNode(node.Condition),
            Visitor().VisitNode(node.Incrementor),
            asyncBodyVisitor.VisitEmbeddedStatement(node.Statement));
    }

    // async.go:286-301 — visitAwaitExpression visits an AwaitExpression node.
    //
    // This function will be called any time a ES2017 await expression is encountered.
    private Node? VisitAwaitExpression(AwaitExpression node)
    {
        // do not downlevel a top-level await as it is module syntax...
        if (InTopLevelContext())
        {
            return Visitor().VisitEachChild(node.AsNode());
        }
        var yieldExpr = Factory().AsNodeFactory().NewYieldExpression(
            null, /*asteriskToken*/
            Visitor().VisitNode(node.Expression));
        yieldExpr.Loc = node.Loc;
        EmitContext().SetOriginal(yieldExpr, node.AsNode());
        return yieldExpr;
    }

    // async.go:303-318
    private Node VisitConstructorDeclaration(Node node)
    {
        var decl = node.AsConstructorDeclaration();
        var savedLexicalArguments = lexicalArguments;
        lexicalArguments = default;
        var updated = Factory().AsNodeFactory().UpdateConstructorDeclaration(
            decl,
            Visitor().VisitModifiers(decl.Modifiers()),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            TransformMethodBody(node));
        lexicalArguments = savedLexicalArguments;
        return updated;
    }

    // async.go:320-354 — visitMethodDeclaration visits a MethodDeclaration node.
    //
    // This function will be called when one of the following conditions are met:
    // - The node is marked as async
    private Node VisitMethodDeclaration(Node node)
    {
        var decl = node.AsMethodDeclaration();
        var functionFlags = AstUtilities.GetFunctionFlags(node);
        var savedLexicalArguments = lexicalArguments;
        lexicalArguments = default;

        ParameterList? parameters;
        Node? body;
        if ((functionFlags & FunctionFlags.Async) != 0)
        {
            parameters = TransformAsyncFunctionParameterList(node);
            body = TransformAsyncFunctionBody(node, parameters);
        }
        else
        {
            parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
            body = TransformMethodBody(node);
        }

        var updated = Factory().AsNodeFactory().UpdateMethodDeclaration(
            decl,
            Visitor().VisitModifiers(decl.Modifiers()),
            decl.AsteriskToken,
            decl.Name(),
            null, /*postfixToken*/
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        lexicalArguments = savedLexicalArguments;
        return updated;
    }

    // async.go:356-372
    private Node VisitGetAccessorDeclaration(Node node)
    {
        var decl = node.AsGetAccessorDeclaration();
        var savedLexicalArguments = lexicalArguments;
        lexicalArguments = default;
        var updated = Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
            decl,
            Visitor().VisitModifiers(decl.Modifiers()),
            decl.Name(),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            TransformMethodBody(node));
        lexicalArguments = savedLexicalArguments;
        return updated;
    }

    // async.go:374-390
    private Node VisitSetAccessorDeclaration(Node node)
    {
        var decl = node.AsSetAccessorDeclaration();
        var savedLexicalArguments = lexicalArguments;
        lexicalArguments = default;
        var updated = Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
            decl,
            Visitor().VisitModifiers(decl.Modifiers()),
            decl.Name(),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            TransformMethodBody(node));
        lexicalArguments = savedLexicalArguments;
        return updated;
    }

    // async.go:392-425 — visitFunctionDeclaration visits a FunctionDeclaration node.
    //
    // This function will be called when one of the following conditions are met:
    // - The node is marked async
    private Node VisitFunctionDeclaration(Node node)
    {
        var decl = node.AsFunctionDeclaration();
        var functionFlags = AstUtilities.GetFunctionFlags(node);
        var savedLexicalArguments = lexicalArguments;
        lexicalArguments = default;

        ParameterList? parameters;
        Node? body;
        if ((functionFlags & FunctionFlags.Async) != 0)
        {
            parameters = TransformAsyncFunctionParameterList(node);
            body = TransformAsyncFunctionBody(node, parameters);
        }
        else
        {
            parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
            body = EmitContext().VisitFunctionBody(decl.Body, Visitor());
        }

        var updated = Factory().AsNodeFactory().UpdateFunctionDeclaration(
            decl,
            Visitor().VisitModifiers(decl.Modifiers()),
            decl.AsteriskToken,
            Visitor().VisitNode(decl.Name()),
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        lexicalArguments = savedLexicalArguments;
        return updated;
    }

    // async.go:427-460 — visitFunctionExpression visits a FunctionExpression node.
    //
    // This function will be called when one of the following conditions are met:
    // - The node is marked async
    private Node VisitFunctionExpression(Node node)
    {
        var decl = node.AsFunctionExpression();
        var functionFlags = AstUtilities.GetFunctionFlags(node);
        var savedLexicalArguments = lexicalArguments;
        lexicalArguments = default;

        ParameterList? parameters;
        Node? body;
        if ((functionFlags & FunctionFlags.Async) != 0)
        {
            parameters = TransformAsyncFunctionParameterList(node);
            body = TransformAsyncFunctionBody(node, parameters);
        }
        else
        {
            parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
            body = EmitContext().VisitFunctionBody(decl.Body, Visitor());
        }

        var updated = Factory().AsNodeFactory().UpdateFunctionExpression(
            decl,
            Visitor().VisitModifiers(decl.Modifiers()),
            decl.AsteriskToken,
            Visitor().VisitNode(decl.Name()),
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        lexicalArguments = savedLexicalArguments;
        return updated;
    }

    // async.go:462-500 — visitArrowFunction visits an ArrowFunction.
    //
    // This function will be called when one of the following conditions are met:
    // - The node is marked async
    private Node VisitArrowFunction(Node node)
    {
        // `arguments` in class static blocks is always an error, but we preserve Strada's emit
        // behavior for baseline compatibility. In Strada, checker-based `isArgumentsLocalBinding`
        // returns false for `arguments` in static blocks (since the binding doesn't exist due to
        // the error), so the async transform leaves them untouched.
        var suppressLexicalArguments = (EmitContext().EmitFlags(node) & EF.NoLexicalArguments) != 0;
        var savedSuppressedLexicalArguments = lexicalArguments;
        if (suppressLexicalArguments)
        {
            lexicalArguments = default;
        }
        try
        {
            var decl = node.AsArrowFunction();
            var functionFlags = AstUtilities.GetFunctionFlags(node);

            ParameterList? parameters;
            Node? body;
            if ((functionFlags & FunctionFlags.Async) != 0)
            {
                parameters = TransformAsyncFunctionParameterList(node);
                body = TransformAsyncFunctionBody(node, parameters);
            }
            else
            {
                parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
                body = EmitContext().VisitFunctionBody(decl.Body, Visitor());
            }

            return Factory().AsNodeFactory().UpdateArrowFunction(
                decl,
                Visitor().VisitModifiers(decl.Modifiers()),
                null, /*typeParameters*/
                parameters,
                null, /*returnType*/
                null, /*fullSignature*/
                decl.EqualsGreaterThanToken,
                body);
        }
        finally
        {
            if (suppressLexicalArguments)
            {
                lexicalArguments = savedSuppressedLexicalArguments;
            }
        }
    }

    // async.go:502-516
    private void RecordDeclarationName(Node node, global::Ts.Collections.Set<string> names)
    {
        var name = node.Name();
        if (name == null)
        {
            return;
        }
        if (NodeTests.IsIdentifier(name))
        {
            names.Add(name.Text());
        }
        else if (AstUtilities.IsBindingPattern(name))
        {
            foreach (var element in name.AsBindingPattern().Elements!.Nodes)
            {
                if (!NodeTests.IsOmittedExpression(element))
                {
                    RecordDeclarationName(element, names);
                }
            }
        }
    }

    // async.go:518-523
    private bool IsVariableDeclarationListWithCollidingName(Node? node)
    {
        return node != null &&
            NodeTests.IsVariableDeclarationList(node) &&
            (node.Flags & NodeFlags.BlockScoped) == 0 &&
            node.AsVariableDeclarationList().Declarations!.Nodes.Any(CollidesWithParameterName);
    }

    // async.go:525-554
    private Node? VisitVariableDeclarationListWithCollidingNames(VariableDeclarationList node, bool hasReceiver)
    {
        HoistVariableDeclarationList(node);

        var variables = new List<Node>();
        foreach (var decl in node.Declarations!.Nodes)
        {
            if (decl.AsVariableDeclaration().Initializer != null)
            {
                variables.Add(decl);
            }
        }

        if (variables.Count == 0)
        {
            if (hasReceiver)
            {
                var name = node.Declarations!.Nodes[0].Name()!;
                Node target;
                if (AstUtilities.IsBindingPattern(name))
                {
                    target = Transformers.ConvertBindingPatternToAssignmentPattern(EmitContext(), name.AsBindingPattern());
                }
                else
                {
                    target = name;
                }
                return Visitor().VisitNode(target);
            }
            return null;
        }

        var expressions = new List<Node>();
        foreach (var variable in variables)
        {
            expressions.Add(TransformInitializedVariable(variable.AsVariableDeclaration()));
        }
        return Factory().InlineExpressions(expressions.ToArray());
    }

    // async.go:556-560
    private void HoistVariableDeclarationList(VariableDeclarationList node)
    {
        foreach (var decl in node.Declarations!.Nodes)
        {
            HoistVariable(decl);
        }
    }

    // async.go:562-576
    private void HoistVariable(Node node)
    {
        var name = node.Name();
        if (name == null)
        {
            return;
        }
        if (NodeTests.IsIdentifier(name))
        {
            EmitContext().AddVariableDeclaration(name.AsIdentifier());
        }
        else if (AstUtilities.IsBindingPattern(name))
        {
            foreach (var element in name.AsBindingPattern().Elements!.Nodes)
            {
                if (!NodeTests.IsOmittedExpression(element))
                {
                    HoistVariable(element);
                }
            }
        }
    }

    // async.go:578-588
    private Node TransformInitializedVariable(VariableDeclaration node)
    {
        Node target;
        if (AstUtilities.IsBindingPattern(node.Name()!))
        {
            target = Transformers.ConvertBindingPatternToAssignmentPattern(EmitContext(), node.Name()!.AsBindingPattern());
        }
        else
        {
            target = node.Name()!;
        }
        var converted = Factory().NewAssignmentExpression(target, node.Initializer!);
        EmitContext().SetSourceMapRange(converted, node.Loc);
        return Visitor().VisitNode(converted)!;
    }

    // async.go:590-606
    private bool CollidesWithParameterName(Node node)
    {
        var name = node.Name();
        if (name == null)
        {
            return false;
        }
        if (NodeTests.IsIdentifier(name))
        {
            return enclosingFunctionParameterNames != null && global::Ts.Collections.SetExtensions.Has(enclosingFunctionParameterNames, name.Text());
        }
        if (AstUtilities.IsBindingPattern(name))
        {
            foreach (var element in name.AsBindingPattern().Elements!.Nodes)
            {
                if (!NodeTests.IsOmittedExpression(element) && CollidesWithParameterName(element))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // async.go:608-656
    private Node TransformMethodBody(Node node)
    {
        var savedCapturedSuperProperties = super.capturedSuperProperties;
        var savedHasSuperElementAccess = super.hasSuperElementAccess;
        var savedHasSuperPropertyAssignment = super.hasSuperPropertyAssignment;
        var savedSuperBinding = super.superBinding;
        var savedSuperIndexBinding = super.superIndexBinding;
        super.capturedSuperProperties = new global::Ts.Collections.OrderedSet<string>();
        super.hasSuperElementAccess = false;
        super.hasSuperPropertyAssignment = false;
        super.superBinding = Factory().NewUniqueNameEx("_super", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.Optimistic | PrinterNs.GeneratedIdentifierFlags.FileLevel });
        super.superIndexBinding = Factory().NewUniqueNameEx("_superIndex", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.Optimistic | PrinterNs.GeneratedIdentifierFlags.FileLevel });
        EmitContext().StartVariableEnvironment();
        var updated = EmitContext().VisitFunctionBody(node.Body(), Visitor())!;

        // Minor optimization, emit `_super` helper to capture `super` access in an arrow.
        var emitSuperHelpers = (super.capturedSuperProperties.Size() > 0 || super.hasSuperElementAccess) &&
            (AstUtilities.GetFunctionFlags(GetOriginalIfFunctionLike(node)) & FunctionFlags.AsyncGenerator) != FunctionFlags.AsyncGenerator;

        if (emitSuperHelpers)
        {
            if (super.capturedSuperProperties.Size() > 0)
            {
                EmitContext().AddInitializationStatement(super.CreateSuperAccessVariableStatement());
            }
        }

        var mergedStatements = EmitContext().EndAndMergeVariableEnvironmentList(updated.StatementList());
        if (emitSuperHelpers && super.hasSuperElementAccess && !updated.AsBlock().MultiLine)
        {
            var newBlock = Factory().NewBlock(mergedStatements, true);
            newBlock.Loc = updated.Loc;
            updated = newBlock;
        }
        else
        {
            updated = Factory().AsNodeFactory().UpdateBlock(updated.AsBlock(), mergedStatements, updated.AsBlock().MultiLine);
        }

        if (emitSuperHelpers && super.hasSuperElementAccess)
        {
            if (super.hasSuperPropertyAssignment)
            {
                EmitContext().AddEmitHelper(updated, PrinterNs.Helpers.AdvancedAsyncSuperHelper);
            }
            else
            {
                EmitContext().AddEmitHelper(updated, PrinterNs.Helpers.AsyncSuperHelper);
            }
        }

        super.capturedSuperProperties = savedCapturedSuperProperties;
        super.hasSuperElementAccess = savedHasSuperElementAccess;
        super.hasSuperPropertyAssignment = savedHasSuperPropertyAssignment;
        super.superBinding = savedSuperBinding;
        super.superIndexBinding = savedSuperIndexBinding;
        return updated;
    }

    // async.go:658-669
    private Node CreateCaptureArgumentsStatement()
    {
        var variable = Factory().NewVariableDeclaration(
            lexicalArguments.binding,
            null,
            null,
            Factory().NewIdentifier("arguments"));
        var declList = Factory().NewVariableDeclarationList(Factory().NewNodeList([variable]), NodeFlags.None);
        var statement = Factory().NewVariableStatement(null, declList);
        EmitContext().AddEmitFlags(statement, EF.StartOnNewLine | EF.CustomPrologue);
        return statement;
    }

    // async.go:671-710
    private ParameterList? TransformAsyncFunctionParameterList(Node node)
    {
        if (IsSimpleParameterList(node.Parameters()))
        {
            return EmitContext().VisitParameters(node.ParameterList(), Visitor());
        }

        var newParameters = new List<Node>();
        foreach (var parameter in node.Parameters())
        {
            var param = parameter.AsParameterDeclaration();
            if (param.Initializer != null || param.DotDotDotToken != null)
            {
                // for an arrow function, capture the remaining arguments in a rest parameter.
                // for any other function/method this isn't necessary as we can just use `arguments`.
                if (node.Kind == SyntaxKind.ArrowFunction)
                {
                    var restParameter = Factory().NewParameterDeclaration(
                        null,
                        Factory().NewToken(SyntaxKind.DotDotDotToken),
                        Factory().NewUniqueNameEx("args", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.ReservedInNestedScopes }),
                        null,
                        null,
                        null);
                    newParameters.Add(restParameter);
                }
                break;
            }
            // for arrow functions we capture fixed parameters to forward to `__awaiter`. For all other functions
            // we add fixed parameters to preserve the function's `length` property.
            var newParameter = Factory().NewParameterDeclaration(
                null,
                null,
                Factory().NewGeneratedNameForNodeEx(param.Name()!, new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.ReservedInNestedScopes }),
                null,
                null,
                null);
            newParameters.Add(newParameter);
        }
        var newParametersArray = Factory().NewNodeList(newParameters.ToArray());
        newParametersArray.Loc = node.ParameterList()!.Loc;
        return newParametersArray;
    }

    // async.go:712-871
    private Node TransformAsyncFunctionBody(Node node, ParameterList? outerParameters)
    {
        var isArrow = node.Kind == SyntaxKind.ArrowFunction;
        var savedCapturedSuperProperties = super.capturedSuperProperties;
        var savedHasSuperElementAccess = super.hasSuperElementAccess;
        var savedHasSuperPropertyAssignment = super.hasSuperPropertyAssignment;
        var savedSuperBinding = super.superBinding;
        var savedSuperIndexBinding = super.superIndexBinding;
        if (!isArrow)
        {
            super.capturedSuperProperties = new global::Ts.Collections.OrderedSet<string>();
            super.hasSuperElementAccess = false;
            super.hasSuperPropertyAssignment = false;
            super.superBinding = Factory().NewUniqueNameEx("_super", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.Optimistic | PrinterNs.GeneratedIdentifierFlags.FileLevel });
            super.superIndexBinding = Factory().NewUniqueNameEx("_superIndex", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.Optimistic | PrinterNs.GeneratedIdentifierFlags.FileLevel });
        }

        ParameterList? innerParameters = null;
        if (!IsSimpleParameterList(node.Parameters()))
        {
            innerParameters = EmitContext().VisitParameters(node.ParameterList(), Visitor());
        }

        var savedLexicalArguments = lexicalArguments;
        var captureLexicalArguments = lexicalArguments.binding == null;
        if (captureLexicalArguments)
        {
            lexicalArguments = new LexicalArgumentsInfo
            {
                binding = Factory().NewUniqueName("arguments"),
            };
        }

        Expression? argumentsExpression = null;
        if (innerParameters != null)
        {
            if (isArrow)
            {
                // `node` does not have a simple parameter list, so `outerParameters` refers to placeholders that are
                // forwarded to `innerParameters`, matching how they are introduced in `transformAsyncFunctionParameterList`.
                var parameterBindings = new List<Node>();
                var outerLen = outerParameters!.Nodes.Length;
                var i = 0;
                foreach (var param in node.Parameters())
                {
                    if (i >= outerLen)
                    {
                        break;
                    }
                    var originalParameter = param.AsParameterDeclaration();
                    var outerParameter = outerParameters.Nodes[i].AsParameterDeclaration();
                    if (originalParameter.Initializer != null || originalParameter.DotDotDotToken != null)
                    {
                        parameterBindings.Add(Factory().NewSpreadElement(outerParameter.Name()!));
                        break;
                    }
                    parameterBindings.Add(outerParameter.Name()!);
                    i++;
                }
                argumentsExpression = Factory().NewArrayLiteralExpression(Factory().NewNodeList(parameterBindings.ToArray()), false);
            }
            else
            {
                argumentsExpression = Factory().NewIdentifier("arguments");
            }
        }

        // An async function is emit as an outer function that calls an inner
        // generator function. To preserve lexical bindings, we pass the current
        // `this` and `arguments` objects to `__awaiter`. The generator function
        // passed to `__awaiter` is executed inside of the callback to the
        // promise constructor.

        var savedEnclosingFunctionParameterNames = enclosingFunctionParameterNames;
        enclosingFunctionParameterNames = new global::Ts.Collections.Set<string>();
        foreach (var parameter in node.Parameters())
        {
            RecordDeclarationName(parameter, enclosingFunctionParameterNames);
        }

        var hasLexicalThis = InHasLexicalThisContext();

        var asyncBody = TransformAsyncFunctionBodyWorker(node.Body()!);
        asyncBody = Factory().AsNodeFactory().UpdateBlock(
            asyncBody.AsBlock(),
            EmitContext().EndAndMergeVariableEnvironmentList(asyncBody.StatementList()),
            asyncBody.AsBlock().MultiLine);

        // Substitute super property accesses with _super/_superIndex helpers
        var emitSuperHelpers = super.capturedSuperProperties != null &&
            (super.capturedSuperProperties.Size() > 0 || super.hasSuperElementAccess);
        if (emitSuperHelpers)
        {
            innerParameters = super.superAccessVisitor.VisitNodes(innerParameters);
            asyncBody = super.SubstituteSuperAccessesInBody(asyncBody)!;
        }

        Node result;
        if (!isArrow)
        {
            EmitContext().StartVariableEnvironment();

            // Minor optimization, emit `_super` helper to capture `super` access in an arrow.
            if (emitSuperHelpers)
            {
                if (super.capturedSuperProperties!.Size() > 0)
                {
                    EmitContext().AddInitializationStatement(super.CreateSuperAccessVariableStatement());
                }
            }

            if (captureLexicalArguments && lexicalArguments.used)
            {
                EmitContext().AddInitializationStatement(CreateCaptureArgumentsStatement());
            }

            Node[] statements =
            [
                Factory().NewReturnStatement(
                    Factory().NewAwaiterHelper(
                        hasLexicalThis,
                        argumentsExpression,
                        innerParameters,
                        asyncBody)),
            ];

            var block = Factory().NewBlock(
                EmitContext().EndAndMergeVariableEnvironmentList(Factory().NewNodeList(statements)),
                true);
            block.Loc = node.Body()!.Loc;

            if (emitSuperHelpers && super.hasSuperElementAccess)
            {
                if (super.hasSuperPropertyAssignment)
                {
                    EmitContext().AddEmitHelper(block, PrinterNs.Helpers.AdvancedAsyncSuperHelper);
                }
                else
                {
                    EmitContext().AddEmitHelper(block, PrinterNs.Helpers.AsyncSuperHelper);
                }
            }

            result = block;
        }
        else
        {
            result = Factory().NewAwaiterHelper(
                hasLexicalThis,
                argumentsExpression,
                innerParameters,
                asyncBody);

            if (captureLexicalArguments && lexicalArguments.used)
            {
                var block = ConvertToFunctionBlock(result);
                result = Factory().AsNodeFactory().UpdateBlock(
                    block.AsBlock(),
                    EmitContext().MergeEnvironmentList(block.StatementList(), [CreateCaptureArgumentsStatement()]),
                    block.AsBlock().MultiLine);
            }
        }

        enclosingFunctionParameterNames = savedEnclosingFunctionParameterNames;
        if (!isArrow)
        {
            super.capturedSuperProperties = savedCapturedSuperProperties;
            super.hasSuperElementAccess = savedHasSuperElementAccess;
            super.hasSuperPropertyAssignment = savedHasSuperPropertyAssignment;
            super.superBinding = savedSuperBinding;
            super.superIndexBinding = savedSuperIndexBinding;
            lexicalArguments = savedLexicalArguments;
        }
        else if (captureLexicalArguments && !lexicalArguments.used)
        {
            // If we created a new binding but it wasn't used, restore the previous state.
            // If it was used, keep the binding alive so sibling arrows can reuse it
            // (the `var` declaration hoists to the enclosing function scope).
            lexicalArguments = savedLexicalArguments;
        }
        else if (captureLexicalArguments)
        {
            // Keep the binding but clear the used flag so siblings don't re-emit the capture statement.
            lexicalArguments.used = false;
        }
        return result;
    }

    // async.go:873-890
    private Node TransformAsyncFunctionBodyWorker(Node body)
    {
        if (NodeTests.IsBlock(body))
        {
            return Factory().AsNodeFactory().UpdateBlock(
                body.AsBlock(),
                asyncBodyVisitor.VisitNodes(body.StatementList()),
                body.AsBlock().MultiLine);
        }
        // Convert expression body to block body with return statement
        var visited = asyncBodyVisitor.VisitNode(body);
        var ret = Factory().NewReturnStatement(visited);
        ret.Loc = body.Loc;
        var list = Factory().NewNodeList([ret]);
        list.Loc = body.Loc;
        var block = Factory().NewBlock(list, false /*multiLine*/);
        block.Loc = body.Loc;
        return block;
    }

    // async.go:892-904
    private Node ConvertToFunctionBlock(Node node)
    {
        if (NodeTests.IsBlock(node))
        {
            return node;
        }
        var ret = Factory().NewReturnStatement(node);
        ret.Loc = node.Loc;
        EmitContext().SetOriginal(ret, node);
        var list = Factory().NewNodeList([ret]);
        list.Loc = node.Loc;
        var block = Factory().NewBlock(list, true);
        block.Loc = node.Loc;
        return block;
    }

    // async.go:906-939 — assignmentTargetContainsSuperProperty checks top-down whether an assignment
    // target expression contains a super property or element access (super.x or super[x]).
    // This avoids relying on parent pointers (IsAssignmentTarget) which may not be set
    // on synthesized AST nodes from prior transforms.
    internal static bool AssignmentTargetContainsSuperProperty(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                return node.Expression()!.Kind == SyntaxKind.SuperKeyword;
            case SyntaxKind.ParenthesizedExpression:
                return AssignmentTargetContainsSuperProperty(node.AsParenthesizedExpression().Expression!);
            case SyntaxKind.ArrayLiteralExpression:
                return node.AsArrayLiteralExpression().Elements!.Nodes.Any(AssignmentTargetContainsSuperProperty);
            case SyntaxKind.ObjectLiteralExpression:
                foreach (var prop in node.AsObjectLiteralExpression().Properties!.Nodes)
                {
                    switch (prop.Kind)
                    {
                        case SyntaxKind.PropertyAssignment:
                            if (AssignmentTargetContainsSuperProperty(prop.AsPropertyAssignment().Initializer!))
                            {
                                return true;
                            }
                            break;
                        case SyntaxKind.ShorthandPropertyAssignment:
                            if (AssignmentTargetContainsSuperProperty(prop.AsShorthandPropertyAssignment().Name()!))
                            {
                                return true;
                            }
                            break;
                        case SyntaxKind.SpreadAssignment:
                            if (AssignmentTargetContainsSuperProperty(prop.AsSpreadAssignment().Expression!))
                            {
                                return true;
                            }
                            break;
                    }
                }
                break;
            case SyntaxKind.SpreadElement:
                return AssignmentTargetContainsSuperProperty(node.AsSpreadElement().Expression!);
        }
        return false;
    }

    // async.go:941-952 — isUpdateExpression checks if a prefix/postfix unary expression is ++ or --.
    internal static bool IsUpdateExpression(Node node)
    {
        if (NodeTests.IsPrefixUnaryExpression(node))
        {
            var op = node.AsPrefixUnaryExpression().Operator;
            return op == SyntaxKind.PlusPlusToken || op == SyntaxKind.MinusMinusToken;
        }
        if (NodeTests.IsPostfixUnaryExpression(node))
        {
            var op = node.AsPostfixUnaryExpression().Operator;
            return op == SyntaxKind.PlusPlusToken || op == SyntaxKind.MinusMinusToken;
        }
        return false;
    }

    // async.go:954-960
    private Node GetOriginalIfFunctionLike(Node node)
    {
        var original = EmitContext().MostOriginal(node);
        if (original != null && AstUtilities.IsFunctionLikeDeclaration(original))
        {
            return original;
        }
        return node;
    }

    // async.go:962-971 — isSimpleParameterList checks if every parameter has no initializer and an
    // Identifier name.
    internal static bool IsSimpleParameterList(IReadOnlyList<Node> @params)
    {
        foreach (var param in @params)
        {
            var p = param.AsParameterDeclaration();
            if (p.Initializer != null || !NodeTests.IsIdentifier(p.Name()!))
            {
                return false;
            }
        }
        return true;
    }

    // async.go:973-995 — isNodeWithPossibleHoistedDeclaration checks if a node could contain hoisted
    // declarations.
    internal static bool IsNodeWithPossibleHoistedDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Block:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.IfStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.CaseBlock:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
            case SyntaxKind.LabeledStatement:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.TryStatement:
            case SyntaxKind.CatchClause:
                return true;
        }
        return false;
    }
}
