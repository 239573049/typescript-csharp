// Ported from tsgo internal/transformers/estransforms/forawait.go — the ES2018 for-await-of /
// async-generator downlevel transformer (__asyncGenerator/__await/__asyncValues/__asyncDelegator).

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;
using PrinterNs = global::Ts.Printer;

// forawait.go:12-41 — Facts we track as we traverse the tree
[Flags]
internal enum ForAwaitHierarchyFacts
{
    None = 0,

    //
    // Ancestor facts
    //

    HasLexicalThis = 1 << 0,
    IterationContainer = 1 << 1,

    //
    // Ancestor masks
    //

    AncestorFactsMask = (1 << 2) - 1,

    SourceFileExcludes = IterationContainer,
    StrictModeSourceFileIncludes = None,

    ClassOrFunctionIncludes = HasLexicalThis,
    ClassOrFunctionExcludes = IterationContainer,

    ArrowFunctionIncludes = None,
    ArrowFunctionExcludes = ClassOrFunctionExcludes,

    IterationStatementIncludes = IterationContainer,
    IterationStatementExcludes = None,
}

internal sealed class ForAwaitTransformer : Transformer
{
    // forawait.go:45 — superAccessState (Go embedding → composition).
    private readonly SuperAccessState super = new();
    private readonly CompilerOptions compilerOptions;

    private FunctionFlags enclosingFunctionFlags;
    private ForAwaitHierarchyFacts forAwaitHierarchyFacts;
    private bool exportedVariableStatement;

    private NodeVisitor fallbackNodeVisitor = null!;
    private NodeVisitor noAsyncModifierVisitor = null!;

    // forawait.go:56-70
    private ForAwaitTransformer(TransformOptions opts)
    {
        compilerOptions = opts.CompilerOptions ?? new CompilerOptions();
        NewTransformer(Visit, opts.Context);
        super.InitSuperAccessVisitor(EmitContext(), Factory());
        fallbackNodeVisitor = EmitContext().NewNodeVisitor(VisitFallback);
        noAsyncModifierVisitor = EmitContext().NewNodeVisitor(static node =>
        {
            if (node.Kind == SyntaxKind.AsyncKeyword)
            {
                return null;
            }
            return node;
        });
    }

    internal static Transformer NewForAwaitTransformer(TransformOptions opts) => new ForAwaitTransformer(opts);

    // forawait.go:72-74
    private bool AffectsSubtree(ForAwaitHierarchyFacts excludeFacts, ForAwaitHierarchyFacts includeFacts)
    {
        return forAwaitHierarchyFacts != ((forAwaitHierarchyFacts & ~excludeFacts) | includeFacts);
    }

    // forawait.go:76-82 — enterSubtree sets the HierarchyFacts for this node prior to visiting this
    // node's subtree, returning the facts set prior to modification.
    private ForAwaitHierarchyFacts EnterSubtree(ForAwaitHierarchyFacts excludeFacts, ForAwaitHierarchyFacts includeFacts)
    {
        var ancestorFacts = forAwaitHierarchyFacts;
        forAwaitHierarchyFacts = ((forAwaitHierarchyFacts & ~excludeFacts) | includeFacts) & ForAwaitHierarchyFacts.AncestorFactsMask;
        return ancestorFacts;
    }

    // forawait.go:84-88 — exitSubtree restores the HierarchyFacts for this node's ancestor after
    // visiting this node's subtree.
    private void ExitSubtree(ForAwaitHierarchyFacts ancestorFacts)
    {
        forAwaitHierarchyFacts = ancestorFacts;
    }

    // forawait.go:90-92
    private ModifierList? VisitModifiersNoAsync(ModifierList? modifiers) => noAsyncModifierVisitor.VisitModifiers(modifiers);

    // forawait.go:94-102
    private Node? DoWithHierarchyFacts(Func<ForAwaitTransformer, Node, Node?> cb, Node node, ForAwaitHierarchyFacts excludeFacts, ForAwaitHierarchyFacts includeFacts)
    {
        if (AffectsSubtree(excludeFacts, includeFacts))
        {
            var ancestorFacts = EnterSubtree(excludeFacts, includeFacts);
            var result = cb(this, node);
            ExitSubtree(ancestorFacts);
            return result;
        }
        return cb(this, node);
    }

    // forawait.go:104-106
    private Node? VisitDefault(Node node) => Visitor().VisitEachChild(node);

    // forawait.go:108-120
    private Node? FallbackVisitorCore(Node node)
    {
        if (super.capturedSuperProperties == null)
        {
            return node;
        }
        switch (node.Kind)
        {
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.Constructor:
                return node;
        }
        super.TrackSuperAccess(node);
        return fallbackNodeVisitor.VisitEachChild(node);
    }

    // forawait.go:122-124
    private Node? VisitFallback(Node node) => FallbackVisitorCore(node);

    // forawait.go:126-217
    private Node? Visit(Node node)
    {
        if ((node.SubtreeFacts() & SubtreeFacts.SubtreeContainsForAwaitOrAsyncGenerator) == 0)
        {
            return FallbackVisitorCore(node);
        }
        super.TrackSuperAccess(node);
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
                return VisitSourceFile(node.AsSourceFile());
            case SyntaxKind.AwaitExpression:
                return VisitAwaitExpression(node.AsAwaitExpression());
            case SyntaxKind.YieldExpression:
                return VisitYieldExpression(node.AsYieldExpression());
            case SyntaxKind.ReturnStatement:
                return VisitReturnStatement(node.AsReturnStatement());
            case SyntaxKind.LabeledStatement:
                return VisitLabeledStatement(node.AsLabeledStatement());
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.ForInStatement:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitDefault(n),
                    node,
                    ForAwaitHierarchyFacts.IterationStatementExcludes,
                    ForAwaitHierarchyFacts.IterationStatementIncludes);
            case SyntaxKind.ForOfStatement:
                return VisitForOfStatement(node.AsForInOrOfStatement(), null);
            case SyntaxKind.ForStatement:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitDefault(n),
                    node,
                    ForAwaitHierarchyFacts.IterationStatementExcludes,
                    ForAwaitHierarchyFacts.IterationStatementIncludes);
            case SyntaxKind.Constructor:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitConstructorDeclaration(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            case SyntaxKind.MethodDeclaration:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitMethodDeclaration(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            case SyntaxKind.GetAccessor:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitGetAccessorDeclaration(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            case SyntaxKind.SetAccessor:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitSetAccessorDeclaration(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            case SyntaxKind.FunctionDeclaration:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitFunctionDeclaration(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            case SyntaxKind.FunctionExpression:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitFunctionExpression(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            case SyntaxKind.ArrowFunction:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitArrowFunction(n),
                    node,
                    ForAwaitHierarchyFacts.ArrowFunctionExcludes,
                    ForAwaitHierarchyFacts.ArrowFunctionIncludes);
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return DoWithHierarchyFacts(
                    static (tx, n) => tx.VisitDefault(n),
                    node,
                    ForAwaitHierarchyFacts.ClassOrFunctionExcludes,
                    ForAwaitHierarchyFacts.ClassOrFunctionIncludes);
            default:
                return Visitor().VisitEachChild(node);
        }
    }

    // forawait.go:219-230
    private Node? VisitAwaitExpression(AwaitExpression node)
    {
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0 && (enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            var result = Factory().AsNodeFactory().NewYieldExpression(
                null, /*asteriskToken*/
                Factory().NewAwaitHelper(Visitor().VisitNode(node.Expression)!));
            result.Loc = node.Loc;
            EmitContext().SetOriginal(result, node.AsNode());
            return result;
        }
        return Visitor().VisitEachChild(node.AsNode());
    }

    // forawait.go:232-277
    private Node? VisitYieldExpression(YieldExpression node)
    {
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0 && (enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            if (node.AsteriskToken != null)
            {
                var expression = Visitor().VisitNode(node.Expression)!;

                var asyncValuesResult = Factory().NewAsyncValuesHelper(expression);
                asyncValuesResult.Loc = expression.Loc;

                var asyncDelegatorResult = Factory().NewAsyncDelegatorHelper(asyncValuesResult);
                asyncDelegatorResult.Loc = expression.Loc;

                var innerYield = Factory().AsNodeFactory().UpdateYieldExpression(
                    node,
                    node.AsteriskToken,
                    asyncDelegatorResult);

                var awaitedYield = Factory().NewAwaitHelper(innerYield);

                var yieldResult = Factory().AsNodeFactory().NewYieldExpression(
                    null, /*asteriskToken*/
                    awaitedYield);
                yieldResult.Loc = node.Loc;
                EmitContext().SetOriginal(yieldResult, node.AsNode());
                return yieldResult;
            }

            Node innerExpression;
            if (node.Expression != null)
            {
                innerExpression = Visitor().VisitNode(node.Expression)!;
            }
            else
            {
                innerExpression = Factory().NewVoidZeroExpression();
            }

            var result = Factory().AsNodeFactory().NewYieldExpression(
                null, /*asteriskToken*/
                CreateDownlevelAwait(innerExpression));
            result.Loc = node.Loc;
            EmitContext().SetOriginal(result, node.AsNode());
            return result;
        }

        return Visitor().VisitEachChild(node.AsNode());
    }

    // forawait.go:279-294
    private Node? VisitReturnStatement(ReturnStatement node)
    {
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0 && (enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            Node expression;
            if (node.Expression != null)
            {
                expression = Visitor().VisitNode(node.Expression)!;
            }
            else
            {
                expression = Factory().NewVoidZeroExpression();
            }
            return Factory().AsNodeFactory().UpdateReturnStatement(
                node,
                CreateDownlevelAwait(expression));
        }

        return Visitor().VisitEachChild(node.AsNode());
    }

    // forawait.go:296-305
    private Node? VisitLabeledStatement(LabeledStatement node)
    {
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0)
        {
            var statement = UnwrapInnermostStatementOfLabel(node);
            if (statement.Kind == SyntaxKind.ForOfStatement && statement.AsForInOrOfStatement().AwaitModifier != null)
            {
                return VisitForOfStatement(statement.AsForInOrOfStatement(), node);
            }
            return Factory().RestoreEnclosingLabel(Visitor().VisitNode(statement)!, node);
        }
        return Visitor().VisitEachChild(node.AsNode());
    }

    // forawait.go:307-315 — unwrapInnermostStatementOfLabel follows LabeledStatement chains to find
    // the innermost statement.
    private static Node UnwrapInnermostStatementOfLabel(LabeledStatement node)
    {
        while (true)
        {
            if (node.Statement!.Kind != SyntaxKind.LabeledStatement)
            {
                return node.Statement;
            }
            node = node.Statement.AsLabeledStatement();
        }
    }

    // forawait.go:317-327
    private Node VisitSourceFile(SourceFile node)
    {
        var ancestorFacts = EnterSubtree(
            ForAwaitHierarchyFacts.SourceFileExcludes,
            ForAwaitHierarchyFacts.StrictModeSourceFileIncludes);
        exportedVariableStatement = false;
        _ = exportedVariableStatement;
        _ = compilerOptions;
        var visited = Visitor().VisitEachChild(node.AsNode())!;
        EmitContext().AddEmitHelper(visited, EmitContext().ReadEmitHelpers());
        ExitSubtree(ancestorFacts);
        return visited;
    }

    // forawait.go:329-340 — visitForOfStatement visits a ForOfStatement and converts it into a
    // ES2015-compatible ForOfStatement.
    private Node? VisitForOfStatement(ForInOrOfStatement node, LabeledStatement? outermostLabeledStatement)
    {
        var ancestorFacts = EnterSubtree(ForAwaitHierarchyFacts.IterationStatementExcludes, ForAwaitHierarchyFacts.IterationStatementIncludes);
        Node? result;
        if (node.AwaitModifier != null)
        {
            result = TransformForAwaitOfStatement(node, outermostLabeledStatement, ancestorFacts);
        }
        else
        {
            result = Factory().RestoreEnclosingLabel(Visitor().VisitEachChild(node.AsNode())!, outermostLabeledStatement);
        }
        ExitSubtree(ancestorFacts);
        return result;
    }

    // forawait.go:342-374
    private Node ConvertForOfStatementHead(ForInOrOfStatement node, Node boundValue, Node nonUserCode)
    {
        var f = Factory();
        var value = f.NewTempVariable();
        EmitContext().AddVariableDeclaration(value);
        var iteratorValueExpression = f.NewAssignmentExpression(value, boundValue);
        var iteratorValueStatement = f.NewExpressionStatement(iteratorValueExpression);
        EmitContext().SetSourceMapRange(iteratorValueStatement, node.Expression!.Loc);

        var exitNonUserCodeExpression = f.NewAssignmentExpression(nonUserCode, f.NewKeywordExpression(SyntaxKind.FalseKeyword));
        var exitNonUserCodeStatement = f.NewExpressionStatement(exitNonUserCodeExpression);
        EmitContext().SetSourceMapRange(exitNonUserCodeStatement, node.Expression!.Loc);

        var statements = new System.Collections.Generic.List<Node> { iteratorValueStatement, exitNonUserCodeStatement };
        var binding = Factory().CreateForOfBindingStatement(node.Initializer!, value);
        statements.Add(Visitor().VisitNode(binding)!);

        var bodyLocation = default(TextRange);
        var statementsLocation = default(TextRange);
        var statement = Visitor().VisitEmbeddedStatement(node.Statement)!;
        if (NodeTests.IsBlock(statement))
        {
            statements.AddRange(statement.Statements()!);
            bodyLocation = statement.Loc;
            statementsLocation = statement.StatementList()!.Loc;
        }
        else
        {
            statements.Add(statement);
        }

        var stmtList = f.NewNodeList(statements.ToArray());
        stmtList.Loc = statementsLocation;
        var block = f.NewBlock(stmtList, true);
        block.Loc = bodyLocation;
        return block;
    }

    // forawait.go:376-384
    private Node CreateDownlevelAwait(Node expression)
    {
        if ((enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            return Factory().AsNodeFactory().NewYieldExpression(
                null, /*asteriskToken*/
                Factory().NewAwaitHelper(expression));
        }
        return Factory().AsNodeFactory().NewAwaitExpression(expression);
    }

    // forawait.go:386-529
    private Node TransformForAwaitOfStatement(ForInOrOfStatement node, LabeledStatement? outermostLabeledStatement, ForAwaitHierarchyFacts ancestorFacts)
    {
        var f = Factory();
        var expression = Visitor().VisitNode(node.Expression)!;

        Node iterator;
        if (NodeTests.IsIdentifier(expression))
        {
            iterator = f.NewGeneratedNameForNode(expression);
        }
        else
        {
            iterator = f.NewTempVariable();
        }

        Node result;
        if (NodeTests.IsIdentifier(expression))
        {
            result = f.NewGeneratedNameForNode(iterator);
        }
        else
        {
            result = f.NewTempVariable();
        }

        var nonUserCode = f.NewTempVariable();
        var done = f.NewTempVariable();
        EmitContext().AddVariableDeclaration(done);
        var errorRecord = f.NewUniqueName("e");
        var catchVariable = f.NewGeneratedNameForNode(errorRecord);
        var returnMethod = f.NewTempVariable();
        var callValues = f.NewAsyncValuesHelper(expression);
        callValues.Loc = node.Expression!.Loc;
        var callNext = f.NewCallExpression(
            f.NewPropertyAccessExpression(iterator, null, f.NewIdentifier("next"), NodeFlags.None),
            null, null,
            f.NewNodeList([]),
            NodeFlags.None);
        var getDone = f.NewPropertyAccessExpression(result, null, f.NewIdentifier("done"), NodeFlags.None);
        var getValue = f.NewPropertyAccessExpression(result, null, f.NewIdentifier("value"), NodeFlags.None);
        var callReturn = f.NewFunctionCallCall(returnMethod, iterator, []);

        EmitContext().AddVariableDeclaration(errorRecord);
        EmitContext().AddVariableDeclaration(returnMethod);

        // if we are enclosed in an outer loop ensure we reset 'errorRecord' per each iteration
        Node? initializer;
        if ((ancestorFacts & ForAwaitHierarchyFacts.IterationContainer) != 0)
        {
            initializer = f.InlineExpressions([
                f.NewAssignmentExpression(errorRecord, f.NewVoidZeroExpression()),
                callValues,
            ]);
        }
        else
        {
            initializer = callValues;
        }

        // Build the for statement
        var iteratorDecl = f.NewVariableDeclaration(iterator, null, null, initializer);
        iteratorDecl.Loc = node.Expression!.Loc;
        var varDeclList = f.NewVariableDeclarationList(f.NewNodeList([
            f.NewVariableDeclaration(nonUserCode, null, null, f.NewKeywordExpression(SyntaxKind.TrueKeyword)),
            iteratorDecl,
            f.NewVariableDeclaration(result, null, null, null),
        ]), NodeFlags.None);
        varDeclList.Loc = node.Expression!.Loc;

        var condition = f.InlineExpressions([
            f.NewAssignmentExpression(result, CreateDownlevelAwait(callNext)),
            f.NewAssignmentExpression(done, getDone),
            f.AsNodeFactory().NewPrefixUnaryExpression(SyntaxKind.ExclamationToken, done),
        ]);

        var incrementor = f.NewAssignmentExpression(nonUserCode, f.NewKeywordExpression(SyntaxKind.TrueKeyword));

        var forStatement = f.AsNodeFactory().NewForStatement(
            varDeclList,
            condition,
            incrementor,
            ConvertForOfStatementHead(node, getValue, nonUserCode));
        forStatement.Loc = node.Loc;
        EmitContext().AddEmitFlags(forStatement, EF.NoTokenTrailingSourceMaps);
        EmitContext().SetOriginal(forStatement, node.AsNode());

        // Build the try/catch/finally
        var tryBlock = f.NewBlock(f.NewNodeList([
            f.RestoreEnclosingLabel(forStatement, outermostLabeledStatement),
        ]), true);

        // catch clause: { e_1 = { error: e_2 }; }
        var catchBody = f.NewBlock(f.NewNodeList([
            f.NewExpressionStatement(
                f.NewAssignmentExpression(
                    errorRecord,
                    f.NewObjectLiteralExpression(f.NewNodeList([
                        f.NewPropertyAssignment(null, f.NewIdentifier("error"), null, null, catchVariable),
                    ]), false))),
        ]), false);
        EmitContext().AddEmitFlags(catchBody, EF.SingleLine);
        var catchClause = f.AsNodeFactory().NewCatchClause(
            f.NewVariableDeclaration(catchVariable, null, null, null),
            catchBody);

        // finally block
        // inner try: if (!nonUserCode && !done && (returnMethod = iterator.return)) await returnMethod.call(iterator);
        var innerIfCondition = f.NewBinaryExpression(
            null,
            f.NewBinaryExpression(
                null,
                f.AsNodeFactory().NewPrefixUnaryExpression(SyntaxKind.ExclamationToken, nonUserCode),
                null,
                f.NewToken(SyntaxKind.AmpersandAmpersandToken),
                f.AsNodeFactory().NewPrefixUnaryExpression(SyntaxKind.ExclamationToken, done)),
            null,
            f.NewToken(SyntaxKind.AmpersandAmpersandToken),
            f.NewAssignmentExpression(
                returnMethod,
                f.NewPropertyAccessExpression(iterator, null, f.NewIdentifier("return"), NodeFlags.None)));
        var innerIfStatement = f.AsNodeFactory().NewIfStatement(
            innerIfCondition,
            f.NewExpressionStatement(CreateDownlevelAwait(callReturn)),
            null);
        EmitContext().AddEmitFlags(innerIfStatement, EF.SingleLine);

        var innerTryBlock = f.NewBlock(f.NewNodeList([innerIfStatement]), false);

        // inner finally: if (errorRecord) throw errorRecord.error;
        var innerFinallyIf = f.AsNodeFactory().NewIfStatement(
            errorRecord,
            f.AsNodeFactory().NewThrowStatement(
                f.NewPropertyAccessExpression(errorRecord, null, f.NewIdentifier("error"), NodeFlags.None)),
            null);
        EmitContext().AddEmitFlags(innerFinallyIf, EF.SingleLine);
        var innerFinallyBlock = f.NewBlock(f.NewNodeList([innerFinallyIf]), false);
        EmitContext().AddEmitFlags(innerFinallyBlock, EF.SingleLine);

        var innerTryStatement = f.AsNodeFactory().NewTryStatement(innerTryBlock, null, innerFinallyBlock);
        var finallyBlock = f.NewBlock(f.NewNodeList([innerTryStatement]), true);

        return f.AsNodeFactory().NewTryStatement(tryBlock, catchClause, finallyBlock);
    }

    // forawait.go:531-546
    private Node VisitConstructorDeclaration(Node node)
    {
        var decl = node.AsConstructorDeclaration();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);
        var updated = Factory().AsNodeFactory().UpdateConstructorDeclaration(
            decl,
            decl.Modifiers(),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            EmitContext().VisitFunctionBody(node.Body(), Visitor()));
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:548-564
    private Node VisitGetAccessorDeclaration(Node node)
    {
        var decl = node.AsGetAccessorDeclaration();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);
        var updated = Factory().AsNodeFactory().UpdateGetAccessorDeclaration(
            decl,
            decl.Modifiers(),
            Visitor().VisitNode(decl.Name()),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            EmitContext().VisitFunctionBody(node.Body(), Visitor()));
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:566-582
    private Node VisitSetAccessorDeclaration(Node node)
    {
        var decl = node.AsSetAccessorDeclaration();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);
        var updated = Factory().AsNodeFactory().UpdateSetAccessorDeclaration(
            decl,
            decl.Modifiers(),
            Visitor().VisitNode(decl.Name()),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            EmitContext().VisitFunctionBody(node.Body(), Visitor()));
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:584-627
    private Node VisitMethodDeclaration(Node node)
    {
        var decl = node.AsMethodDeclaration();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);

        ModifierList? modifiers;
        if ((enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            modifiers = VisitModifiersNoAsync(decl.Modifiers());
        }
        else
        {
            modifiers = decl.Modifiers();
        }

        AsteriskToken? asteriskToken;
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0)
        {
            asteriskToken = null;
        }
        else
        {
            asteriskToken = decl.AsteriskToken;
        }

        ParameterList? parameters;
        Node? body;
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0 && (enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            parameters = TransformAsyncGeneratorFunctionParameterList(node);
            body = TransformAsyncGeneratorFunctionBody(node);
        }
        else
        {
            parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
            body = EmitContext().VisitFunctionBody(node.Body(), Visitor());
        }

        var updated = Factory().AsNodeFactory().UpdateMethodDeclaration(
            decl,
            modifiers,
            asteriskToken,
            Visitor().VisitNode(decl.Name()),
            null, /*postfixToken*/
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:629-671
    private Node VisitFunctionDeclaration(Node node)
    {
        var decl = node.AsFunctionDeclaration();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);

        ModifierList? modifiers;
        if ((enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            modifiers = VisitModifiersNoAsync(decl.Modifiers());
        }
        else
        {
            modifiers = decl.Modifiers();
        }

        AsteriskToken? asteriskToken;
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0)
        {
            asteriskToken = null;
        }
        else
        {
            asteriskToken = decl.AsteriskToken;
        }

        ParameterList? parameters;
        Node? body;
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0 && (enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            parameters = TransformAsyncGeneratorFunctionParameterList(node);
            body = TransformAsyncGeneratorFunctionBody(node);
        }
        else
        {
            parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
            body = EmitContext().VisitFunctionBody(node.Body(), Visitor());
        }

        var updated = Factory().AsNodeFactory().UpdateFunctionDeclaration(
            decl,
            modifiers,
            asteriskToken,
            decl.Name(),
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:673-689
    private Node VisitArrowFunction(Node node)
    {
        var decl = node.AsArrowFunction();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);
        var updated = Factory().AsNodeFactory().UpdateArrowFunction(
            decl,
            decl.Modifiers(),
            null, /*typeParameters*/
            EmitContext().VisitParameters(decl.Parameters, Visitor()),
            null, /*returnType*/
            null, /*fullSignature*/
            decl.EqualsGreaterThanToken,
            EmitContext().VisitFunctionBody(node.Body(), Visitor()));
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:691-733
    private Node VisitFunctionExpression(Node node)
    {
        var decl = node.AsFunctionExpression();
        var savedEnclosingFunctionFlags = enclosingFunctionFlags;
        enclosingFunctionFlags = AstUtilities.GetFunctionFlags(node);

        ModifierList? modifiers;
        if ((enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            modifiers = VisitModifiersNoAsync(decl.Modifiers());
        }
        else
        {
            modifiers = decl.Modifiers();
        }

        AsteriskToken? asteriskToken;
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0)
        {
            asteriskToken = null;
        }
        else
        {
            asteriskToken = decl.AsteriskToken;
        }

        ParameterList? parameters;
        Node? body;
        if ((enclosingFunctionFlags & FunctionFlags.Async) != 0 && (enclosingFunctionFlags & FunctionFlags.Generator) != 0)
        {
            parameters = TransformAsyncGeneratorFunctionParameterList(node);
            body = TransformAsyncGeneratorFunctionBody(node);
        }
        else
        {
            parameters = EmitContext().VisitParameters(decl.Parameters, Visitor());
            body = EmitContext().VisitFunctionBody(node.Body(), Visitor());
        }

        var updated = Factory().AsNodeFactory().UpdateFunctionExpression(
            decl,
            modifiers,
            asteriskToken,
            decl.Name(),
            null, /*typeParameters*/
            parameters,
            null, /*returnType*/
            null, /*fullSignature*/
            body);
        enclosingFunctionFlags = savedEnclosingFunctionFlags;
        return updated;
    }

    // forawait.go:735-759
    private ParameterList? TransformAsyncGeneratorFunctionParameterList(Node node)
    {
        if (AsyncTransformer.IsSimpleParameterList(node.Parameters()))
        {
            return EmitContext().VisitParameters(node.ParameterList(), Visitor());
        }
        // Add fixed parameters to preserve the function's `length` property.
        var newParameters = new System.Collections.Generic.List<Node>();
        foreach (var parameter in node.Parameters())
        {
            var param = parameter.AsParameterDeclaration();
            if (param.Initializer != null || param.DotDotDotToken != null)
            {
                break;
            }
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

    // forawait.go:761-856
    private Node TransformAsyncGeneratorFunctionBody(Node node)
    {
        var f = Factory();
        ParameterList? innerParameters = null;
        if (!AsyncTransformer.IsSimpleParameterList(node.Parameters()))
        {
            innerParameters = EmitContext().VisitParameters(node.ParameterList(), Visitor());
        }

        var savedCapturedSuperProperties = super.capturedSuperProperties;
        var savedHasSuperElementAccess = super.hasSuperElementAccess;
        var savedHasSuperPropertyAssignment = super.hasSuperPropertyAssignment;
        var savedSuperBinding = super.superBinding;
        var savedSuperIndexBinding = super.superIndexBinding;
        super.capturedSuperProperties = new global::Ts.Collections.OrderedSet<string>();
        super.hasSuperElementAccess = false;
        super.hasSuperPropertyAssignment = false;
        super.superBinding = f.NewUniqueNameEx("_super", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.Optimistic | PrinterNs.GeneratedIdentifierFlags.FileLevel });
        super.superIndexBinding = f.NewUniqueNameEx("_superIndex", new PrinterNs.AutoGenerateOptions { Flags = PrinterNs.GeneratedIdentifierFlags.Optimistic | PrinterNs.GeneratedIdentifierFlags.FileLevel });

        var asyncBody = f.AsNodeFactory().UpdateBlock(
            node.Body()!.AsBlock(),
            Visitor().VisitNodes(node.Body()!.StatementList()),
            node.Body()!.AsBlock().MultiLine);
        asyncBody = f.AsNodeFactory().UpdateBlock(
            asyncBody.AsBlock(),
            EmitContext().EndAndMergeVariableEnvironmentList(asyncBody.StatementList()),
            asyncBody.AsBlock().MultiLine);

        // Substitute super property accesses with _super/_superIndex helpers
        var emitSuperHelpers = super.capturedSuperProperties.Size() > 0 || super.hasSuperElementAccess;
        if (emitSuperHelpers)
        {
            asyncBody = super.SubstituteSuperAccessesInBody(asyncBody)!;
        }

        NodeList innerParams;
        if (innerParameters != null)
        {
            innerParams = innerParameters;
        }
        else
        {
            innerParams = f.NewNodeList([]);
        }

        Node? name = null;
        if (node.Name() != null)
        {
            name = f.NewGeneratedNameForNode(node.Name()!);
        }

        var generatorFunc = f.NewFunctionExpression(
            null, /*modifiers*/
            f.NewToken(SyntaxKind.AsteriskToken),
            name,
            null, /*typeParameters*/
            innerParams,
            null, /*returnType*/
            null, /*fullSignature*/
            asyncBody);

        var returnStatement = f.NewReturnStatement(
            f.NewAsyncGeneratorHelper(
                generatorFunc,
                (forAwaitHierarchyFacts & ForAwaitHierarchyFacts.HasLexicalThis) != 0));

        EmitContext().StartVariableEnvironment();
        if (emitSuperHelpers)
        {
            if (super.capturedSuperProperties.Size() > 0)
            {
                EmitContext().AddInitializationStatement(super.CreateSuperAccessVariableStatement());
            }
        }

        Node[] outerStatements = [returnStatement];

        var block = f.AsNodeFactory().UpdateBlock(
            node.Body()!.AsBlock(),
            EmitContext().EndAndMergeVariableEnvironmentList(f.NewNodeList(outerStatements)),
            node.Body()!.AsBlock().MultiLine);

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

        super.capturedSuperProperties = savedCapturedSuperProperties;
        super.hasSuperElementAccess = savedHasSuperElementAccess;
        super.hasSuperPropertyAssignment = savedHasSuperPropertyAssignment;
        super.superBinding = savedSuperBinding;
        super.superIndexBinding = savedSuperIndexBinding;

        return block;
    }
}
