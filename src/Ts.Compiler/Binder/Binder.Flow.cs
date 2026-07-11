// Ported from tsgo internal/binder/binder.go (container + control-flow binding).
using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;
using Diag = Ts.Diagnostics.Diagnostics;

namespace Ts.Binder;

public sealed partial class Binder
{
    // All container nodes are kept on a linked list in declaration order. This list is used by
    // the getLocalNameOfContainer function in the type checker to validate that the local name
    // used for a container is unique.
    private void BindContainer(Node node, ContainerFlags containerFlags)
    {
        // Before we recurse into a node's children, we first save the existing parent, container
        // and block-container.  Then after we pop out of processing the children, we restore
        // these saved values.
        var saveContainer = container;
        var saveThisContainer = thisContainer;
        var savedBlockScopeContainer = blockScopeContainer;
        // Depending on what kind of node this is, we may have to adjust the current container
        // and block-container.   If the current node is a container, then it is automatically
        // considered the current block-container as well.  Also, for containers that we know
        // may contain locals, we eagerly initialize the .locals field. We do this because
        // it's highly likely that the .locals will be needed to place some child in (for example,
        // a parameter, or variable declaration).
        //
        // However, we do not proactively create the .locals for block-containers because it's
        // totally normal and common for block-containers to never actually have a block-scoped
        // variable in them.  We don't want to end up allocating an object for every 'block' we
        // run into when most of them won't be necessary.
        //
        // Finally, if this is a block-container, then we clear out any existing .locals object
        // it may contain within it.  This happens in incremental scenarios.  Because we can be
        // reusing a node from a previous compilation, that node may have had 'locals' created
        // for it.  We must clear this so we don't accidentally move any stale data forward from
        // a previous compilation.
        if ((containerFlags & ContainerFlags.IsContainer) != 0)
        {
            container = node;
            blockScopeContainer = node;
            if ((containerFlags & ContainerFlags.HasLocals) != 0)
            {
                // localsContainer := node
                // localsContainer.LocalsContainerData().locals = make(SymbolTable)
                AddToContainerChain(node);
            }
        }
        else if ((containerFlags & ContainerFlags.IsBlockScopedContainer) != 0)
        {
            blockScopeContainer = node;
            AddToContainerChain(node);
        }
        if ((containerFlags & ContainerFlags.IsThisContainer) != 0)
        {
            thisContainer = node;
        }
        if ((containerFlags & ContainerFlags.IsControlFlowContainer) != 0)
        {
            var saveCurrentFlow = currentFlow;
            var saveBreakTarget = currentBreakTarget;
            var saveContinueTarget = currentContinueTarget;
            var saveReturnTarget = currentReturnTarget;
            var saveExceptionTarget = currentExceptionTarget;
            var saveActiveLabelList = activeLabelList;
            var saveHasExplicitReturn = hasExplicitReturn;
            var saveSeenThisKeyword = seenThisKeyword;
            var isImmediatelyInvoked = ((containerFlags & ContainerFlags.IsFunctionExpression) != 0 &&
                !AstUtilities.HasSyntacticModifier(node, ModifierFlags.Async) &&
                !IsGeneratorFunctionExpression(node) &&
                AstUtilities.GetImmediatelyInvokedFunctionExpression(node) != null) || node.Kind == SyntaxKind.ClassStaticBlockDeclaration;
            // A non-async, non-generator IIFE is considered part of the containing control flow. Return statements behave
            // similarly to break statements that exit to a label just past the statement body.
            if (!isImmediatelyInvoked)
            {
                var flowStart = NewFlowNode(FlowFlags.Start);
                currentFlow = flowStart;
                if ((containerFlags & (ContainerFlags.IsFunctionExpression | ContainerFlags.IsObjectLiteralOrClassExpressionMethodOrAccessor)) != 0)
                {
                    flowStart.Node = node;
                }
            }
            // We create a return control flow graph for IIFEs and constructors. For constructors
            // we use the return control flow graph in strict property initialization checks.
            if (isImmediatelyInvoked || node.Kind == SyntaxKind.Constructor)
            {
                currentReturnTarget = NewFlowNode(FlowFlags.BranchLabel);
            }
            else
            {
                currentReturnTarget = null;
            }
            currentExceptionTarget = null;
            currentBreakTarget = null;
            currentContinueTarget = null;
            activeLabelList = null;
            hasExplicitReturn = false;
            seenThisKeyword = false;
            BindChildren(node);
            // Reset flags (for incremental scenarios)
            node.Flags &= ~(NodeFlags.ReachabilityAndEmitFlags | NodeFlags.ContainsThis);
            if ((currentFlow!.Flags & FlowFlags.Unreachable) == 0 && (containerFlags & ContainerFlags.IsFunctionLike) != 0)
            {
                var bodyData = node.BodyData();
                if (bodyData != null && AstUtilities.NodeIsPresent(bodyData.Body))
                {
                    node.Flags |= NodeFlags.HasImplicitReturn;
                    if (hasExplicitReturn)
                    {
                        node.Flags |= NodeFlags.HasExplicitReturn;
                    }
                    bodyData.EndFlowNode = currentFlow;
                }
            }
            if (seenThisKeyword)
            {
                node.Flags |= NodeFlags.ContainsThis;
            }
            if (node.Kind == SyntaxKind.SourceFile)
            {
                node.Flags |= emitFlags;
                node.AsSourceFile().EndFlowNode = currentFlow;
            }
            if (currentReturnTarget != null)
            {
                AddAntecedent(currentReturnTarget, currentFlow!);
                currentFlow = FinishFlowLabel(currentReturnTarget);
                if (node.Kind == SyntaxKind.Constructor || node.Kind == SyntaxKind.ClassStaticBlockDeclaration)
                {
                    SetReturnFlowNode(node, currentFlow);
                }
            }
            if (!isImmediatelyInvoked)
            {
                currentFlow = saveCurrentFlow;
            }
            currentBreakTarget = saveBreakTarget;
            currentContinueTarget = saveContinueTarget;
            currentReturnTarget = saveReturnTarget;
            currentExceptionTarget = saveExceptionTarget;
            activeLabelList = saveActiveLabelList;
            hasExplicitReturn = saveHasExplicitReturn;
            if ((containerFlags & ContainerFlags.PropagatesThisKeyword) != 0)
            {
                seenThisKeyword = saveSeenThisKeyword || seenThisKeyword;
            }
            else
            {
                seenThisKeyword = saveSeenThisKeyword;
            }
        }
        else if ((containerFlags & ContainerFlags.IsInterface) != 0)
        {
            var saveSeenThisKeyword = seenThisKeyword;
            seenThisKeyword = false;
            BindChildren(node);
            // ContainsThis cannot overlap with HasExtendedUnicodeEscape on Identifier
            if (seenThisKeyword)
            {
                node.Flags |= NodeFlags.ContainsThis;
            }
            else
            {
                node.Flags &= ~NodeFlags.ContainsThis;
            }
            seenThisKeyword = saveSeenThisKeyword;
        }
        else
        {
            BindChildren(node);
        }
        if (NodeTests.IsSourceFile(node) && AstUtilities.IsInJSFile(node))
        {
            // Binding of top-level JSTypeAliasDeclaration nodes is deferred to ensure CommonJS module
            // indicators, if any, are processed first.
            foreach (var statement in node.Statements() ?? [])
            {
                if (NodeTests.IsJSTypeAliasDeclaration(statement))
                {
                    BindBlockScopedDeclaration(statement, SymbolFlags.TypeAlias, SymbolFlags.TypeAliasExcludes);
                }
            }
            if (file.CommonJSModuleIndicator != null)
            {
                DeclareCommonJSVariable("module");
                DeclareCommonJSVariable("exports");
            }
        }
        if (NodeTests.IsSourceFile(node) && AstUtilities.IsExternalOrCommonJSModule(node.AsSourceFile()) || AstUtilities.IsAmbientModule(node))
        {
            BindCommonJSTypeExports(node.Symbol()!);
        }
        container = saveContainer;
        thisContainer = saveThisContainer;
        blockScopeContainer = savedBlockScopeContainer;
    }

    private void DeclareCommonJSVariable(string name)
    {
        var locals = AstUtilities.GetLocals(file.AsNode());
        if (locals.GetValueOrDefault(name) == null)
        {
            var symbol = NewSymbol(SymbolFlags.FunctionScopedVariable | SymbolFlags.ModuleExports, name);
            symbol.Declarations = NewSingleDeclaration(file.AsNode());
            symbol.ValueDeclaration = symbol.Declarations[0];
            if (name == "module")
            {
                var exportsProperty = NewSymbol(SymbolFlags.ModuleExports | SymbolFlags.Property, "exports");
                exportsProperty.Declarations = symbol.Declarations;
                exportsProperty.ValueDeclaration = symbol.ValueDeclaration;
                exportsProperty.Parent = symbol;
                symbol.Members = new SymbolTable(1);
                symbol.Members["exports"] = exportsProperty;
            }
            locals[name] = symbol;
        }
    }

    private void BindChildren(Node node)
    {
        var saveInAssignmentPattern = inAssignmentPattern;
        // Most nodes aren't valid in an assignment pattern, so we clear the value here
        // and set it before we descend into nodes that could actually be part of an assignment pattern.
        inAssignmentPattern = false;

        if (currentFlow == unreachableFlow)
        {
            var flowNodeData = node.FlowNodeData();
            if (flowNodeData != null)
            {
                flowNodeData.FlowNode = null;
            }
            if (AstUtilities.IsPotentiallyExecutableNode(node))
            {
                node.Flags |= NodeFlags.Unreachable;
            }
            BindEachChild(node);
            inAssignmentPattern = saveInAssignmentPattern;
            return;
        }

        if (SyntaxKind.FirstStatement <= node.Kind && node.Kind <= SyntaxKind.LastStatement)
        {
            var flowNodeData = node.FlowNodeData();
            if (flowNodeData != null)
            {
                flowNodeData.FlowNode = currentFlow;
            }
        }

        switch (node.Kind)
        {
            case SyntaxKind.WhileStatement:
                BindWhileStatement(node);
                break;
            case SyntaxKind.DoStatement:
                BindDoStatement(node);
                break;
            case SyntaxKind.ForStatement:
                BindForStatement(node);
                break;
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                BindForInOrForOfStatement(node);
                break;
            case SyntaxKind.IfStatement:
                BindIfStatement(node);
                break;
            case SyntaxKind.ReturnStatement:
                BindReturnStatement(node);
                break;
            case SyntaxKind.ThrowStatement:
                BindThrowStatement(node);
                break;
            case SyntaxKind.BreakStatement:
                BindBreakStatement(node);
                break;
            case SyntaxKind.ContinueStatement:
                BindContinueStatement(node);
                break;
            case SyntaxKind.TryStatement:
                BindTryStatement(node);
                break;
            case SyntaxKind.SwitchStatement:
                BindSwitchStatement(node);
                break;
            case SyntaxKind.CaseBlock:
                BindCaseBlock(node);
                break;
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                BindCaseOrDefaultClause(node);
                break;
            case SyntaxKind.ExpressionStatement:
                BindExpressionStatement(node);
                break;
            case SyntaxKind.LabeledStatement:
                BindLabeledStatement(node);
                break;
            case SyntaxKind.PrefixUnaryExpression:
                BindPrefixUnaryExpressionFlow(node);
                break;
            case SyntaxKind.PostfixUnaryExpression:
                BindPostfixUnaryExpressionFlow(node);
                break;
            case SyntaxKind.BinaryExpression:
                if (AstUtilities.IsDestructuringAssignment(node))
                {
                    // Carry over whether we are in an assignment pattern to
                    // binary expressions that could actually be an initializer
                    inAssignmentPattern = saveInAssignmentPattern;
                    BindDestructuringAssignmentFlow(node);
                    return;
                }
                BindBinaryExpressionFlow(node);
                break;
            case SyntaxKind.DeleteExpression:
                BindDeleteExpressionFlow(node);
                break;
            case SyntaxKind.ConditionalExpression:
                BindConditionalExpressionFlow(node);
                break;
            case SyntaxKind.VariableDeclaration:
                BindVariableDeclarationFlow(node);
                break;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                BindAccessExpressionFlow(node);
                break;
            case SyntaxKind.CallExpression:
                BindCallExpressionFlow(node);
                break;
            case SyntaxKind.NonNullExpression:
                BindNonNullExpressionFlow(node);
                break;
            case SyntaxKind.SourceFile:
                var sourceFile = node.AsSourceFile();
                BindEachStatementFunctionsFirst(sourceFile.Statements);
                Bind(sourceFile.EndOfFileToken);
                break;
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
                BindEachStatementFunctionsFirst(node.StatementList());
                break;
            case SyntaxKind.BindingElement:
                BindBindingElementFlow(node);
                break;
            case SyntaxKind.Parameter:
                BindParameterFlow(node);
                break;
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.SpreadElement:
                inAssignmentPattern = saveInAssignmentPattern;
                BindEachChild(node);
                break;
            default:
                BindEachChild(node);
                break;
        }
        inAssignmentPattern = saveInAssignmentPattern;
    }

    private void BindEachChild(Node node)
    {
        node.ForEachChild(bindFunc);
    }

    private void BindEach(Node[]? nodes)
    {
        if (nodes == null)
        {
            return;
        }
        foreach (var node in nodes)
        {
            Bind(node);
        }
    }

    private void BindNodeList(NodeList? nodeList)
    {
        if (nodeList != null)
        {
            BindEach(nodeList.Nodes);
        }
    }

    private void BindModifiers(ModifierList? modifiers)
    {
        if (modifiers != null)
        {
            BindEach(modifiers.Nodes);
        }
    }

    private void BindEachStatementFunctionsFirst(NodeList? statements)
    {
        foreach (var node in statements!.Nodes)
        {
            if (node.Kind == SyntaxKind.FunctionDeclaration)
            {
                Bind(node);
            }
        }
        foreach (var node in statements!.Nodes)
        {
            if (node.Kind != SyntaxKind.FunctionDeclaration)
            {
                Bind(node);
            }
        }
    }

    private FlowLabel SetContinueTarget(Node node, FlowLabel target)
    {
        var label = activeLabelList;
        while (label != null && node.Parent!.Kind == SyntaxKind.LabeledStatement)
        {
            label.continueTarget = target;
            label = label.next;
            node = node.Parent;
        }
        return target;
    }

    private void DoWithConditionalBranches(Func<Node?, bool> action, Node? value, FlowLabel trueTarget, FlowLabel falseTarget)
    {
        var savedTrueTarget = currentTrueTarget;
        var savedFalseTarget = currentFalseTarget;
        currentTrueTarget = trueTarget;
        currentFalseTarget = falseTarget;
        action(value);
        currentTrueTarget = savedTrueTarget;
        currentFalseTarget = savedFalseTarget;
    }

    private void BindCondition(Node? node, FlowLabel trueTarget, FlowLabel falseTarget)
    {
        DoWithConditionalBranches(Bind, node, trueTarget, falseTarget);
        if (node == null || !IsLogicalAssignmentExpression(node) && !AstUtilities.IsLogicalExpression(node) && !(AstUtilities.IsOptionalChain(node) && AstUtilities.IsOutermostOptionalChain(node)))
        {
            AddAntecedent(trueTarget, CreateFlowCondition(FlowFlags.TrueCondition, currentFlow!, node));
            AddAntecedent(falseTarget, CreateFlowCondition(FlowFlags.FalseCondition, currentFlow!, node));
        }
    }

    private void BindIterativeStatement(Node node, FlowLabel breakTarget, FlowLabel continueTarget)
    {
        var saveBreakTarget = currentBreakTarget;
        var saveContinueTarget = currentContinueTarget;
        currentBreakTarget = breakTarget;
        currentContinueTarget = continueTarget;
        Bind(node);
        currentBreakTarget = saveBreakTarget;
        currentContinueTarget = saveContinueTarget;
    }

    private static bool IsLogicalAssignmentExpression(Node node)
    {
        return AstUtilities.IsLogicalOrCoalescingAssignmentExpression(AstUtilities.SkipParentheses(node));
    }

    private void BindAssignmentTargetFlow(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ArrayLiteralExpression:
                foreach (var e in node.Elements() ?? [])
                {
                    if (e.Kind == SyntaxKind.SpreadElement)
                    {
                        BindAssignmentTargetFlow(e.Expression()!);
                    }
                    else
                    {
                        BindDestructuringTargetFlow(e);
                    }
                }
                break;
            case SyntaxKind.ObjectLiteralExpression:
                foreach (var p in node.Properties() ?? [])
                {
                    switch (p.Kind)
                    {
                        case SyntaxKind.PropertyAssignment:
                            BindDestructuringTargetFlow(p.Initializer()!);
                            break;
                        case SyntaxKind.ShorthandPropertyAssignment:
                            BindAssignmentTargetFlow(p.AsShorthandPropertyAssignment().Name()!);
                            break;
                        case SyntaxKind.SpreadAssignment:
                            BindAssignmentTargetFlow(p.Expression()!);
                            break;
                    }
                }
                break;
            default:
                if (IsNarrowableReference(node))
                {
                    currentFlow = CreateFlowMutation(FlowFlags.Assignment, currentFlow!, node);
                }
                break;
        }
    }

    private void BindDestructuringTargetFlow(Node node)
    {
        if (NodeTests.IsBinaryExpression(node) && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken)
        {
            BindAssignmentTargetFlow(node.AsBinaryExpression().Left!);
        }
        else
        {
            BindAssignmentTargetFlow(node);
        }
    }

    private void BindWhileStatement(Node node)
    {
        var stmt = node.AsWhileStatement();
        var preWhileLabel = SetContinueTarget(node, CreateLoopLabel());
        var preBodyLabel = CreateBranchLabel();
        var postWhileLabel = CreateBranchLabel();
        AddAntecedent(preWhileLabel, currentFlow!);
        currentFlow = preWhileLabel;
        BindCondition(stmt.Expression, preBodyLabel, postWhileLabel);
        currentFlow = FinishFlowLabel(preBodyLabel);
        BindIterativeStatement(stmt.Statement!, postWhileLabel, preWhileLabel);
        AddAntecedent(preWhileLabel, currentFlow!);
        currentFlow = FinishFlowLabel(postWhileLabel);
    }

    private void BindDoStatement(Node node)
    {
        var stmt = node.AsDoStatement();
        var preDoLabel = CreateLoopLabel();
        var preConditionLabel = SetContinueTarget(node, CreateBranchLabel());
        var postDoLabel = CreateBranchLabel();
        AddAntecedent(preDoLabel, currentFlow!);
        currentFlow = preDoLabel;
        BindIterativeStatement(stmt.Statement!, postDoLabel, preConditionLabel);
        AddAntecedent(preConditionLabel, currentFlow!);
        currentFlow = FinishFlowLabel(preConditionLabel);
        BindCondition(stmt.Expression, preDoLabel, postDoLabel);
        currentFlow = FinishFlowLabel(postDoLabel);
    }

    private void BindForStatement(Node node)
    {
        var stmt = node.AsForStatement();
        var preLoopLabel = SetContinueTarget(node, CreateLoopLabel());
        var preBodyLabel = CreateBranchLabel();
        var preIncrementorLabel = CreateBranchLabel();
        var postLoopLabel = CreateBranchLabel();
        Bind(stmt.Initializer);
        AddAntecedent(preLoopLabel, currentFlow!);
        currentFlow = preLoopLabel;
        BindCondition(stmt.Condition, preBodyLabel, postLoopLabel);
        currentFlow = FinishFlowLabel(preBodyLabel);
        BindIterativeStatement(stmt.Statement!, postLoopLabel, preIncrementorLabel);
        AddAntecedent(preIncrementorLabel, currentFlow!);
        currentFlow = FinishFlowLabel(preIncrementorLabel);
        Bind(stmt.Incrementor);
        AddAntecedent(preLoopLabel, currentFlow!);
        currentFlow = FinishFlowLabel(postLoopLabel);
    }

    private void BindForInOrForOfStatement(Node node)
    {
        var stmt = node.AsForInOrOfStatement();
        var preLoopLabel = SetContinueTarget(node, CreateLoopLabel());
        var postLoopLabel = CreateBranchLabel();
        Bind(stmt.Expression);
        AddAntecedent(preLoopLabel, currentFlow!);
        currentFlow = preLoopLabel;
        if (node.Kind == SyntaxKind.ForOfStatement)
        {
            Bind(stmt.AwaitModifier);
        }
        AddAntecedent(postLoopLabel, currentFlow!);
        Bind(stmt.Initializer);
        if (stmt.Initializer!.Kind != SyntaxKind.VariableDeclarationList)
        {
            BindAssignmentTargetFlow(stmt.Initializer);
        }
        BindIterativeStatement(stmt.Statement!, postLoopLabel, preLoopLabel);
        AddAntecedent(preLoopLabel, currentFlow!);
        currentFlow = FinishFlowLabel(postLoopLabel);
    }

    private void BindIfStatement(Node node)
    {
        var stmt = node.AsIfStatement();
        var thenLabel = CreateBranchLabel();
        var elseLabel = CreateBranchLabel();
        var postIfLabel = CreateBranchLabel();
        BindCondition(stmt.Expression, thenLabel, elseLabel);
        currentFlow = FinishFlowLabel(thenLabel);
        Bind(stmt.ThenStatement);
        AddAntecedent(postIfLabel, currentFlow!);
        currentFlow = FinishFlowLabel(elseLabel);
        Bind(stmt.ElseStatement);
        AddAntecedent(postIfLabel, currentFlow!);
        currentFlow = FinishFlowLabel(postIfLabel);
    }

    private void BindReturnStatement(Node node)
    {
        Bind(node.Expression());
        if (currentReturnTarget != null)
        {
            AddAntecedent(currentReturnTarget, currentFlow!);
        }
        currentFlow = unreachableFlow;
        hasExplicitReturn = true;
        hasFlowEffects = true;
    }

    private void BindThrowStatement(Node node)
    {
        Bind(node.Expression());
        currentFlow = unreachableFlow;
        hasFlowEffects = true;
    }

    private void BindBreakStatement(Node node)
    {
        BindBreakOrContinueStatement(node.Label(), currentBreakTarget, static l => l.BreakTarget());
    }

    private void BindContinueStatement(Node node)
    {
        BindBreakOrContinueStatement(node.Label(), currentContinueTarget, static l => l.ContinueTarget());
    }

    private void BindBreakOrContinueStatement(Node? label, FlowNode? currentTarget, Func<ActiveLabel, FlowNode?> getTarget)
    {
        Bind(label);
        if (label != null)
        {
            var activeLabel = FindActiveLabel(label.Text());
            if (activeLabel != null)
            {
                activeLabel.referenced = true;
                BindBreakOrContinueFlow(getTarget(activeLabel));
            }
        }
        else
        {
            BindBreakOrContinueFlow(currentTarget);
        }
    }

    private ActiveLabel? FindActiveLabel(string name)
    {
        for (var label = activeLabelList; label != null; label = label.next)
        {
            if (label.name == name)
            {
                return label;
            }
        }
        return null;
    }

    private void BindBreakOrContinueFlow(FlowLabel? flowLabel)
    {
        if (flowLabel != null)
        {
            AddAntecedent(flowLabel, currentFlow!);
            currentFlow = unreachableFlow;
            hasFlowEffects = true;
        }
    }

    private void BindTryStatement(Node node)
    {
        // We conservatively assume that *any* code in the try block can cause an exception, but we only need
        // to track code that causes mutations (because only mutations widen the possible control flow type of
        // a variable). The exceptionLabel is the target label for control flows that result from exceptions.
        // We add all mutation flow nodes as antecedents of this label such that we can analyze them as possible
        // antecedents of the start of catch or finally blocks. Furthermore, we add the current control flow to
        // represent exceptions that occur before any mutations.
        var stmt = node.AsTryStatement();
        var saveReturnTarget = currentReturnTarget;
        var saveExceptionTarget = currentExceptionTarget;
        var normalExitLabel = CreateBranchLabel();
        var returnLabel = CreateBranchLabel();
        var exceptionLabel = CreateBranchLabel();
        if (stmt.FinallyBlock != null)
        {
            currentReturnTarget = returnLabel;
        }
        AddAntecedent(exceptionLabel, currentFlow!);
        currentExceptionTarget = exceptionLabel;
        Bind(stmt.TryBlock);
        AddAntecedent(normalExitLabel, currentFlow!);
        if (stmt.CatchClause != null)
        {
            // Start of catch clause is the target of exceptions from try block.
            currentFlow = FinishFlowLabel(exceptionLabel);
            // The currentExceptionTarget now represents control flows from exceptions in the catch clause.
            // Effectively, in a try-catch-finally, if an exception occurs in the try block, the catch block
            // acts like a second try block.
            exceptionLabel = CreateBranchLabel();
            AddAntecedent(exceptionLabel, currentFlow!);
            currentExceptionTarget = exceptionLabel;
            Bind(stmt.CatchClause);
            AddAntecedent(normalExitLabel, currentFlow!);
        }
        currentReturnTarget = saveReturnTarget;
        currentExceptionTarget = saveExceptionTarget;
        if (stmt.FinallyBlock != null)
        {
            // Possible ways control can reach the finally block:
            // 1) Normal completion of try block of a try-finally or try-catch-finally
            // 2) Normal completion of catch block (following exception in try block) of a try-catch-finally
            // 3) Return in try or catch block of a try-finally or try-catch-finally
            // 4) Exception in try block of a try-finally
            // 5) Exception in catch block of a try-catch-finally
            // When analyzing a control flow graph that starts inside a finally block we want to consider all
            // five possibilities above. However, when analyzing a control flow graph that starts outside (past)
            // the finally block, we only want to consider the first two (if we're past a finally block then it
            // must have completed normally). Likewise, when analyzing a control flow graph from return statements
            // in try or catch blocks in an IIFE, we only want to consider the third. To make this possible, we
            // inject a ReduceLabel node into the control flow graph. This node contains an alternate reduced
            // set of antecedents for the pre-finally label. As control flow analysis passes by a ReduceLabel
            // node, the pre-finally label is temporarily switched to the reduced antecedent set.
            var finallyLabel = CreateBranchLabel();
            finallyLabel.Antecedents = CombineFlowLists(normalExitLabel.Antecedents, CombineFlowLists(exceptionLabel.Antecedents, returnLabel.Antecedents));
            currentFlow = finallyLabel;
            Bind(stmt.FinallyBlock);
            if ((currentFlow!.Flags & FlowFlags.Unreachable) != 0)
            {
                // If the end of the finally block is unreachable, the end of the entire try statement is unreachable.
                currentFlow = unreachableFlow;
            }
            else
            {
                // If we have an IIFE return target and return statements in the try or catch blocks, add a control
                // flow that goes back through the finally block and back through only the return statements.
                if (currentReturnTarget != null && returnLabel.Antecedents != null)
                {
                    AddAntecedent(currentReturnTarget, CreateReduceLabel(finallyLabel, returnLabel.Antecedents, currentFlow!));
                }
                // If we have an outer exception target (i.e. a containing try-finally or try-catch-finally), add a
                // control flow that goes back through the finally block and back through each possible exception source.
                if (currentExceptionTarget != null && exceptionLabel.Antecedents != null)
                {
                    AddAntecedent(currentExceptionTarget, CreateReduceLabel(finallyLabel, exceptionLabel.Antecedents, currentFlow!));
                }
                // If the end of the finally block is reachable, but the end of the try and catch blocks are not,
                // convert the current flow to unreachable. For example, 'try { return 1; } finally { ... }' should
                // result in an unreachable current control flow.
                if (normalExitLabel.Antecedents != null)
                {
                    currentFlow = CreateReduceLabel(finallyLabel, normalExitLabel.Antecedents, currentFlow!);
                }
                else
                {
                    currentFlow = unreachableFlow;
                }
            }
        }
        else
        {
            currentFlow = FinishFlowLabel(normalExitLabel);
        }
    }

    private void BindSwitchStatement(Node node)
    {
        var stmt = node.AsSwitchStatement();
        var postSwitchLabel = CreateBranchLabel();
        Bind(stmt.Expression);
        var saveBreakTarget = currentBreakTarget;
        var savePreSwitchCaseFlow = preSwitchCaseFlow;
        currentBreakTarget = postSwitchLabel;
        preSwitchCaseFlow = currentFlow;
        Bind(stmt.CaseBlock);
        AddAntecedent(postSwitchLabel, currentFlow!);
        var hasDefault = CoreUtil.Some(stmt.CaseBlock!.AsCaseBlock().Clauses!.Nodes, static c =>
            c.Kind == SyntaxKind.DefaultClause);
        if (!hasDefault)
        {
            AddAntecedent(postSwitchLabel, CreateFlowSwitchClause(preSwitchCaseFlow!, node, 0, 0));
        }
        currentBreakTarget = saveBreakTarget;
        preSwitchCaseFlow = savePreSwitchCaseFlow;
        currentFlow = FinishFlowLabel(postSwitchLabel);
    }

    private void BindCaseBlock(Node node)
    {
        var switchStatement = node.Parent!;
        var clauses = node.AsCaseBlock().Clauses!.Nodes;
        var isNarrowingSwitch = switchStatement.Expression()!.Kind == SyntaxKind.TrueKeyword || IsNarrowingExpression(switchStatement.Expression()!);
        FlowNode fallthroughFlow = unreachableFlow;
        for (int i = 0; i < clauses.Length; i++)
        {
            var clauseStart = i;
            while ((clauses[i].Statements()?.Length ?? 0) == 0 && i + 1 < clauses.Length)
            {
                if (fallthroughFlow == unreachableFlow)
                {
                    currentFlow = preSwitchCaseFlow;
                }
                Bind(clauses[i]);
                i++;
            }
            var preCaseLabel = CreateBranchLabel();
            var preCaseFlow = preSwitchCaseFlow;
            if (isNarrowingSwitch)
            {
                preCaseFlow = CreateFlowSwitchClause(preSwitchCaseFlow!, switchStatement, clauseStart, i + 1);
            }
            AddAntecedent(preCaseLabel, preCaseFlow!);
            AddAntecedent(preCaseLabel, fallthroughFlow);
            currentFlow = FinishFlowLabel(preCaseLabel);
            var clause = clauses[i];
            Bind(clause);
            fallthroughFlow = currentFlow!;
            if ((currentFlow!.Flags & FlowFlags.Unreachable) == 0 && i != clauses.Length - 1)
            {
                clause.AsCaseOrDefaultClause().FallthroughFlowNode = currentFlow;
            }
        }
    }

    private void BindCaseOrDefaultClause(Node node)
    {
        var clause = node.AsCaseOrDefaultClause();
        if (clause.Expression != null)
        {
            var saveCurrentFlow = currentFlow;
            currentFlow = preSwitchCaseFlow;
            Bind(clause.Expression);
            currentFlow = saveCurrentFlow;
        }
        BindEach(clause.Statements!.Nodes);
    }

    private void BindExpressionStatement(Node node)
    {
        var stmt = node.AsExpressionStatement();
        Bind(stmt.Expression);
        MaybeBindExpressionFlowIfCall(stmt.Expression!);
    }

    private void MaybeBindExpressionFlowIfCall(Node node)
    {
        // A top level or comma expression call expression with a dotted function name and at least one argument
        // is potentially an assertion and is therefore included in the control flow.
        if (NodeTests.IsCallExpression(node))
        {
            if (node.Expression()!.Kind != SyntaxKind.SuperKeyword && AstUtilities.IsDottedName(node.Expression()!))
            {
                currentFlow = CreateFlowCall(currentFlow!, node);
            }
        }
    }

    private void BindLabeledStatement(Node node)
    {
        var stmt = node.AsLabeledStatement();
        var postStatementLabel = CreateBranchLabel();
        activeLabelList = new ActiveLabel
        {
            next = activeLabelList,
            name = stmt.Label!.Text(),
            breakTarget = postStatementLabel,
            continueTarget = null,
            referenced = false,
        };
        Bind(stmt.Label);
        Bind(stmt.Statement);
        if (!activeLabelList.referenced)
        {
            // Mark the label as unused; the checker will decide whether to report it
            stmt.Label!.Flags |= NodeFlags.Unreachable;
        }
        activeLabelList = activeLabelList.next;
        AddAntecedent(postStatementLabel, currentFlow!);
        currentFlow = FinishFlowLabel(postStatementLabel);
    }

    private void BindPrefixUnaryExpressionFlow(Node node)
    {
        var expr = node.AsPrefixUnaryExpression();
        if (expr.Operator == SyntaxKind.ExclamationToken)
        {
            var saveTrueTarget = currentTrueTarget;
            currentTrueTarget = currentFalseTarget;
            currentFalseTarget = saveTrueTarget;
            BindEachChild(node);
            currentFalseTarget = currentTrueTarget;
            currentTrueTarget = saveTrueTarget;
        }
        else
        {
            BindEachChild(node);
            if (expr.Operator == SyntaxKind.PlusPlusToken || expr.Operator == SyntaxKind.MinusMinusToken)
            {
                BindAssignmentTargetFlow(expr.Operand!);
            }
        }
    }

    private void BindPostfixUnaryExpressionFlow(Node node)
    {
        var expr = node.AsPostfixUnaryExpression();
        BindEachChild(node);
        if (expr.Operator == SyntaxKind.PlusPlusToken || expr.Operator == SyntaxKind.MinusMinusToken)
        {
            BindAssignmentTargetFlow(expr.Operand!);
        }
    }

    private void BindDestructuringAssignmentFlow(Node node)
    {
        var expr = node.AsBinaryExpression();
        if (inAssignmentPattern)
        {
            inAssignmentPattern = false;
            Bind(expr.OperatorToken);
            Bind(expr.Right);
            inAssignmentPattern = true;
            Bind(expr.Left);
            Bind(expr.Type);
        }
        else
        {
            inAssignmentPattern = true;
            Bind(expr.Left);
            Bind(expr.Type);
            inAssignmentPattern = false;
            Bind(expr.OperatorToken);
            Bind(expr.Right);
        }
        BindAssignmentTargetFlow(expr.Left!);
    }

    private void BindBinaryExpressionFlow(Node node)
    {
        var expr = node.AsBinaryExpression();
        var operatorKind = expr.OperatorToken!.Kind;
        if (AstUtilities.IsLogicalOrCoalescingBinaryOperator(operatorKind) || NodeTests.IsLogicalOrCoalescingAssignmentOperator(operatorKind))
        {
            if (IsTopLevelLogicalExpression(node))
            {
                var postExpressionLabel = CreateBranchLabel();
                var saveCurrentFlow = currentFlow;
                var saveHasFlowEffects = hasFlowEffects;
                hasFlowEffects = false;
                BindLogicalLikeExpression(node, postExpressionLabel, postExpressionLabel);
                if (hasFlowEffects)
                {
                    currentFlow = FinishFlowLabel(postExpressionLabel);
                }
                else
                {
                    currentFlow = saveCurrentFlow;
                }
                hasFlowEffects = hasFlowEffects || saveHasFlowEffects;
            }
            else
            {
                BindLogicalLikeExpression(node, currentTrueTarget!, currentFalseTarget!);
            }
        }
        else
        {
            Bind(expr.Left);
            Bind(expr.Type);
            if (operatorKind == SyntaxKind.CommaToken)
            {
                MaybeBindExpressionFlowIfCall(expr.Left!);
            }
            Bind(expr.OperatorToken);
            Bind(expr.Right);
            if (operatorKind == SyntaxKind.CommaToken)
            {
                MaybeBindExpressionFlowIfCall(expr.Right!);
            }
            if (NodeTests.IsAssignmentOperator(operatorKind) && !AstUtilities.IsAssignmentTarget(node))
            {
                BindAssignmentTargetFlow(expr.Left!);
                if (operatorKind == SyntaxKind.EqualsToken && expr.Left!.Kind == SyntaxKind.ElementAccessExpression)
                {
                    var elementAccess = expr.Left.AsElementAccessExpression();
                    if (IsNarrowableOperand(elementAccess.Expression!))
                    {
                        currentFlow = CreateFlowMutation(FlowFlags.ArrayMutation, currentFlow!, node);
                    }
                }
            }
        }
    }

    private void BindLogicalLikeExpression(Node node, FlowLabel trueTarget, FlowLabel falseTarget)
    {
        var expr = node.AsBinaryExpression();
        var preRightLabel = CreateBranchLabel();
        if (expr.OperatorToken!.Kind == SyntaxKind.AmpersandAmpersandToken || expr.OperatorToken.Kind == SyntaxKind.AmpersandAmpersandEqualsToken)
        {
            BindCondition(expr.Left, preRightLabel, falseTarget);
        }
        else
        {
            BindCondition(expr.Left, trueTarget, preRightLabel);
        }
        currentFlow = FinishFlowLabel(preRightLabel);
        Bind(expr.OperatorToken);
        if (NodeTests.IsLogicalOrCoalescingAssignmentOperator(expr.OperatorToken.Kind))
        {
            DoWithConditionalBranches(Bind, expr.Right, trueTarget, falseTarget);
            BindAssignmentTargetFlow(expr.Left!);
            AddAntecedent(trueTarget, CreateFlowCondition(FlowFlags.TrueCondition, currentFlow!, node));
            AddAntecedent(falseTarget, CreateFlowCondition(FlowFlags.FalseCondition, currentFlow!, node));
        }
        else
        {
            BindCondition(expr.Right, trueTarget, falseTarget);
        }
    }

    private void BindDeleteExpressionFlow(Node node)
    {
        var expr = node.AsDeleteExpression();
        BindEachChild(node);
        if (expr.Expression!.Kind == SyntaxKind.PropertyAccessExpression)
        {
            BindAssignmentTargetFlow(expr.Expression);
        }
    }

    private void BindConditionalExpressionFlow(Node node)
    {
        var expr = node.AsConditionalExpression();
        var trueLabel = CreateBranchLabel();
        var falseLabel = CreateBranchLabel();
        var postExpressionLabel = CreateBranchLabel();
        var saveCurrentFlow = currentFlow;
        var saveHasFlowEffects = hasFlowEffects;
        hasFlowEffects = false;
        BindCondition(expr.Condition, trueLabel, falseLabel);
        currentFlow = FinishFlowLabel(trueLabel);
        Bind(expr.QuestionToken);
        Bind(expr.WhenTrue);
        AddAntecedent(postExpressionLabel, currentFlow!);
        currentFlow = FinishFlowLabel(falseLabel);
        Bind(expr.ColonToken);
        Bind(expr.WhenFalse);
        AddAntecedent(postExpressionLabel, currentFlow!);
        if (hasFlowEffects)
        {
            currentFlow = FinishFlowLabel(postExpressionLabel);
        }
        else
        {
            currentFlow = saveCurrentFlow;
        }
        hasFlowEffects = hasFlowEffects || saveHasFlowEffects;
    }

    private void BindVariableDeclarationFlow(Node node)
    {
        BindEachChild(node);
        if (node.Initializer() != null || AstUtilities.IsForInOrOfStatement(node.Parent!.Parent!))
        {
            BindInitializedVariableFlow(node);
        }
    }

    private void BindInitializedVariableFlow(Node node)
    {
        Node? name = null;
        switch (node.Kind)
        {
            case SyntaxKind.VariableDeclaration:
                name = node.AsVariableDeclaration().Name();
                break;
            case SyntaxKind.BindingElement:
                name = node.AsBindingElement().Name();
                break;
        }
        if (name != null && AstUtilities.IsBindingPattern(name))
        {
            foreach (var child in name.Elements() ?? [])
            {
                BindInitializedVariableFlow(child);
            }
        }
        else
        {
            currentFlow = CreateFlowMutation(FlowFlags.Assignment, currentFlow!, node);
        }
    }

    private void BindAccessExpressionFlow(Node node)
    {
        if (AstUtilities.IsOptionalChain(node))
        {
            BindOptionalChainFlow(node);
        }
        else
        {
            BindEachChild(node);
        }
    }

    private void BindOptionalChainFlow(Node node)
    {
        if (IsTopLevelLogicalExpression(node))
        {
            var postExpressionLabel = CreateBranchLabel();
            var saveCurrentFlow = currentFlow;
            var saveHasFlowEffects = hasFlowEffects;
            BindOptionalChain(node, postExpressionLabel, postExpressionLabel);
            if (hasFlowEffects)
            {
                currentFlow = FinishFlowLabel(postExpressionLabel);
            }
            else
            {
                currentFlow = saveCurrentFlow;
            }
            hasFlowEffects = hasFlowEffects || saveHasFlowEffects;
        }
        else
        {
            BindOptionalChain(node, currentTrueTarget!, currentFalseTarget!);
        }
    }

    private void BindOptionalChain(Node node, FlowLabel trueTarget, FlowLabel falseTarget)
    {
        // For an optional chain, we emulate the behavior of a logical expression:
        //
        // a?.b         -> a && a.b
        // a?.b.c       -> a && a.b.c
        // a?.b?.c      -> a && a.b && a.b.c
        // a?.[x = 1]   -> a && a[x = 1]
        //
        // To do this we descend through the chain until we reach the root of a chain (the expression with a `?.`)
        // and build it's CFA graph as if it were the first condition (`a && ...`). Then we bind the rest
        // of the node as part of the "true" branch, and continue to do so as we ascend back up to the outermost
        // chain node. We then treat the entire node as the right side of the expression.
        FlowLabel? preChainLabel = null;
        if (AstUtilities.IsOptionalChainRoot(node))
        {
            preChainLabel = CreateBranchLabel();
        }
        BindOptionalExpression(node.Expression()!, CoreUtil.IfElse(preChainLabel != null, preChainLabel!, trueTarget), falseTarget);
        if (preChainLabel != null)
        {
            currentFlow = FinishFlowLabel(preChainLabel);
        }
        DoWithConditionalBranches(BindOptionalChainRest, node, trueTarget, falseTarget);
        if (AstUtilities.IsOutermostOptionalChain(node))
        {
            AddAntecedent(trueTarget, CreateFlowCondition(FlowFlags.TrueCondition, currentFlow!, node));
            AddAntecedent(falseTarget, CreateFlowCondition(FlowFlags.FalseCondition, currentFlow!, node));
        }
    }

    private void BindOptionalExpression(Node node, FlowLabel trueTarget, FlowLabel falseTarget)
    {
        DoWithConditionalBranches(Bind, node, trueTarget, falseTarget);
        if (!AstUtilities.IsOptionalChain(node) || AstUtilities.IsOutermostOptionalChain(node))
        {
            AddAntecedent(trueTarget, CreateFlowCondition(FlowFlags.TrueCondition, currentFlow!, node));
            AddAntecedent(falseTarget, CreateFlowCondition(FlowFlags.FalseCondition, currentFlow!, node));
        }
    }

    private bool BindOptionalChainRest(Node? node)
    {
        switch (node!.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
                Bind(node.QuestionDotToken());
                Bind(node.Name());
                break;
            case SyntaxKind.ElementAccessExpression:
                Bind(node.QuestionDotToken());
                Bind(node.AsElementAccessExpression().ArgumentExpression);
                break;
            case SyntaxKind.CallExpression:
                Bind(node.QuestionDotToken());
                BindNodeList(node.TypeArgumentList());
                BindEach(node.Arguments());
                break;
        }
        return false;
    }

    private void BindCallExpressionFlow(Node node)
    {
        var call = node.AsCallExpression();
        if (AstUtilities.IsOptionalChain(node))
        {
            BindOptionalChainFlow(node);
        }
        else
        {
            // If the target of the call expression is a function expression or arrow function we have
            // an immediately invoked function expression (IIFE). Initialize the flowNode property to
            // the current control flow (which includes evaluation of the IIFE arguments).
            var expr = AstUtilities.SkipParentheses(call.Expression!);
            if (expr.Kind == SyntaxKind.FunctionExpression || expr.Kind == SyntaxKind.ArrowFunction)
            {
                BindNodeList(call.TypeArguments);
                BindEach(call.Arguments!.Nodes);
                Bind(call.Expression);
            }
            else
            {
                BindEachChild(node);
                if (call.Expression!.Kind == SyntaxKind.SuperKeyword)
                {
                    currentFlow = CreateFlowCall(currentFlow!, node);
                }
            }
        }
        if (NodeTests.IsPropertyAccessExpression(call.Expression!))
        {
            var access = call.Expression!.AsPropertyAccessExpression();
            if (NodeTests.IsIdentifier(access.Name()!) && IsNarrowableOperand(access.Expression!) && AstUtilities.IsPushOrUnshiftIdentifier(access.Name()!))
            {
                currentFlow = CreateFlowMutation(FlowFlags.ArrayMutation, currentFlow!, node);
            }
        }
    }

    private void BindNonNullExpressionFlow(Node node)
    {
        if (AstUtilities.IsOptionalChain(node))
        {
            BindOptionalChainFlow(node);
        }
        else
        {
            BindEachChild(node);
        }
    }

    private void BindBindingElementFlow(Node node)
    {
        // When evaluating a binding pattern, the initializer is evaluated before the binding pattern, per:
        // - https://tc39.es/ecma262/#sec-destructuring-binding-patterns-runtime-semantics-iteratorbindinginitialization
        //   - `BindingElement: BindingPattern Initializer?`
        // - https://tc39.es/ecma262/#sec-runtime-semantics-keyedbindinginitialization
        //   - `BindingElement: BindingPattern Initializer?`
        var elem = node.AsBindingElement();
        Bind(elem.DotDotDotToken);
        Bind(elem.PropertyName);
        BindInitializer(elem.Initializer);
        Bind(elem.Name());
    }

    private void BindParameterFlow(Node node)
    {
        var param = node.AsParameterDeclaration();
        BindModifiers(param.Modifiers());
        Bind(param.DotDotDotToken);
        Bind(param.QuestionToken);
        Bind(param.Type);
        BindInitializer(param.Initializer);
        Bind(param.Name());
    }

    // a BindingElement/Parameter does not have side effects if initializers are not evaluated and used. (see GH#49759)
    private void BindInitializer(Node? node)
    {
        if (node == null)
        {
            return;
        }
        var entryFlow = currentFlow;
        Bind(node);
        if (entryFlow == unreachableFlow || entryFlow == currentFlow)
        {
            return;
        }
        var exitFlow = CreateBranchLabel();
        AddAntecedent(exitFlow, entryFlow!);
        AddAntecedent(exitFlow, currentFlow!);
        currentFlow = FinishFlowLabel(exitFlow);
    }

    private static void SetFlowNode(Node node, FlowNode? flowNode)
    {
        var data = node.FlowNodeData();
        if (data != null)
        {
            data.FlowNode = flowNode;
        }
    }

    private static void SetReturnFlowNode(Node node, FlowNode? returnFlowNode)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
                node.AsConstructorDeclaration().ReturnFlowNode = returnFlowNode;
                break;
            case SyntaxKind.FunctionDeclaration:
                node.AsFunctionDeclaration().ReturnFlowNode = returnFlowNode;
                break;
            case SyntaxKind.FunctionExpression:
                node.AsFunctionExpression().ReturnFlowNode = returnFlowNode;
                break;
            case SyntaxKind.ClassStaticBlockDeclaration:
                node.AsClassStaticBlockDeclaration().ReturnFlowNode = returnFlowNode;
                break;
        }
    }

    private static bool IsGeneratorFunctionExpression(Node node)
    {
        return NodeTests.IsFunctionExpression(node) && node.AsFunctionExpression().AsteriskToken != null;
    }

    private void AddToContainerChain(Node next)
    {
        if (lastContainer != null)
        {
            lastContainer.LocalsContainerData()!.NextContainer = next;
        }
        lastContainer = next;
    }

    private void AddDeclarationToSymbol(Symbol symbol, Node node, SymbolFlags symbolFlags)
    {
        symbol.Flags |= symbolFlags;
        node.DeclarationData()!.Symbol = symbol;
        if (symbol.Declarations == null)
        {
            symbol.Declarations = NewSingleDeclaration(node);
        }
        else
        {
            symbol.Declarations = CoreUtil.AppendIfUnique(symbol.Declarations, node);
        }
        // On merge of const enum module with class or function, reset const enum only flag (namespaces will already recalculate)
        if ((symbol.Flags & SymbolFlags.ConstEnumOnlyModule) != 0 && (symbol.Flags & (SymbolFlags.Function | SymbolFlags.Class | SymbolFlags.RegularEnum)) != 0)
        {
            symbol.Flags &= ~SymbolFlags.ConstEnumOnlyModule;
            notConstEnumOnlyModules.Add(symbol);
        }
        if ((symbolFlags & SymbolFlags.Value) != 0)
        {
            SetValueDeclaration(symbol, node);
        }
    }

    public static void SetValueDeclaration(Symbol symbol, Node node)
    {
        var valueDeclaration = symbol.ValueDeclaration;
        if (valueDeclaration == null ||
            IsAssignmentDeclaration(valueDeclaration) && !IsAssignmentDeclaration(node) ||
            valueDeclaration.Kind != node.Kind && IsEffectiveModuleDeclaration(valueDeclaration))
        {
            // Non-assignment declarations take precedence over assignment declarations and
            // non-namespace declarations take precedence over namespace declarations.
            symbol.ValueDeclaration = node;
        }
    }

    public static ContainerFlags GetContainerFlags(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.JsxAttributes:
                return ContainerFlags.IsContainer;
            case SyntaxKind.InterfaceDeclaration:
                return ContainerFlags.IsContainer | ContainerFlags.IsInterface;
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.MappedType:
            case SyntaxKind.IndexSignature:
                return ContainerFlags.IsContainer | ContainerFlags.HasLocals;
            case SyntaxKind.SourceFile:
                return ContainerFlags.IsContainer | ContainerFlags.IsControlFlowContainer | ContainerFlags.HasLocals;
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.MethodDeclaration:
                if (AstUtilities.IsObjectLiteralOrClassExpressionMethodOrAccessor(node))
                {
                    return ContainerFlags.IsContainer | ContainerFlags.IsControlFlowContainer | ContainerFlags.HasLocals | ContainerFlags.IsFunctionLike | ContainerFlags.IsObjectLiteralOrClassExpressionMethodOrAccessor | ContainerFlags.IsThisContainer;
                }
                goto case SyntaxKind.Constructor;
            case SyntaxKind.Constructor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.ClassStaticBlockDeclaration:
                return ContainerFlags.IsContainer | ContainerFlags.IsControlFlowContainer | ContainerFlags.HasLocals | ContainerFlags.IsFunctionLike | ContainerFlags.IsThisContainer;
            case SyntaxKind.MethodSignature:
            case SyntaxKind.CallSignature:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.ConstructorType:
                return ContainerFlags.IsContainer | ContainerFlags.IsControlFlowContainer | ContainerFlags.HasLocals | ContainerFlags.IsFunctionLike | ContainerFlags.PropagatesThisKeyword;
            case SyntaxKind.FunctionExpression:
                return ContainerFlags.IsContainer | ContainerFlags.IsControlFlowContainer | ContainerFlags.HasLocals | ContainerFlags.IsFunctionLike | ContainerFlags.IsFunctionExpression | ContainerFlags.IsThisContainer;
            case SyntaxKind.ArrowFunction:
                return ContainerFlags.IsContainer | ContainerFlags.IsControlFlowContainer | ContainerFlags.HasLocals | ContainerFlags.IsFunctionLike | ContainerFlags.IsFunctionExpression | ContainerFlags.PropagatesThisKeyword;
            case SyntaxKind.ModuleBlock:
                return ContainerFlags.IsControlFlowContainer;
            case SyntaxKind.PropertyDeclaration:
                if (node.Initializer() != null)
                {
                    return ContainerFlags.IsControlFlowContainer | ContainerFlags.IsThisContainer;
                }
                else
                {
                    return ContainerFlags.None;
                }
            case SyntaxKind.CatchClause:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.CaseBlock:
                return ContainerFlags.IsBlockScopedContainer | ContainerFlags.HasLocals;
            case SyntaxKind.Block:
                if (AstUtilities.IsFunctionLike(node.Parent) || NodeTests.IsClassStaticBlockDeclaration(node.Parent!))
                {
                    return ContainerFlags.None;
                }
                else
                {
                    return ContainerFlags.IsBlockScopedContainer | ContainerFlags.HasLocals;
                }
        }
        return ContainerFlags.None;
    }

    private static bool IsNarrowingExpression(Node expr)
    {
        switch (expr.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.ThisKeyword:
                return true;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                return ContainsNarrowableReference(expr);
            case SyntaxKind.CallExpression:
                return HasNarrowableArgument(expr);
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.TypeOfExpression:
                return IsNarrowingExpression(expr.Expression()!);
            case SyntaxKind.BinaryExpression:
                return IsNarrowingBinaryExpression(expr.AsBinaryExpression());
            case SyntaxKind.PrefixUnaryExpression:
                return expr.AsPrefixUnaryExpression().Operator == SyntaxKind.ExclamationToken && IsNarrowingExpression(expr.AsPrefixUnaryExpression().Operand!);
        }
        return false;
    }

    private static bool ContainsNarrowableReference(Node expr)
    {
        if (IsNarrowableReference(expr))
        {
            return true;
        }
        if ((expr.Flags & NodeFlags.OptionalChain) != 0)
        {
            switch (expr.Kind)
            {
                case SyntaxKind.PropertyAccessExpression:
                case SyntaxKind.ElementAccessExpression:
                case SyntaxKind.CallExpression:
                case SyntaxKind.NonNullExpression:
                    return ContainsNarrowableReference(expr.Expression()!);
            }
        }
        return false;
    }

    private static bool IsNarrowableReference(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.SuperKeyword:
            case SyntaxKind.MetaProperty:
                return true;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.NonNullExpression:
                return IsNarrowableReference(node.Expression()!);
            case SyntaxKind.ElementAccessExpression:
                var expr = node.AsElementAccessExpression();
                return AstUtilities.IsStringOrNumericLiteralLike(expr.ArgumentExpression!) ||
                    AstUtilities.IsEntityNameExpression(expr.ArgumentExpression!) && IsNarrowableReference(expr.Expression!);
            case SyntaxKind.BinaryExpression:
                var bin = node.AsBinaryExpression();
                return bin.OperatorToken!.Kind == SyntaxKind.CommaToken && IsNarrowableReference(bin.Right!) ||
                    NodeTests.IsAssignmentOperator(bin.OperatorToken.Kind) && AstUtilities.IsLeftHandSideExpression(bin.Left!);
        }
        return false;
    }

    private static bool HasNarrowableArgument(Node expr)
    {
        var call = expr.AsCallExpression();
        foreach (var argument in call.Arguments!.Nodes)
        {
            if (ContainsNarrowableReference(argument))
            {
                return true;
            }
        }
        if (NodeTests.IsPropertyAccessExpression(call.Expression!))
        {
            if (ContainsNarrowableReference(call.Expression!.Expression()!))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsNarrowingBinaryExpression(BinaryExpression expr)
    {
        switch (expr.OperatorToken!.Kind)
        {
            case SyntaxKind.EqualsToken:
            case SyntaxKind.BarBarEqualsToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
            case SyntaxKind.QuestionQuestionEqualsToken:
                return ContainsNarrowableReference(expr.Left!);
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
                var left = AstUtilities.SkipParentheses(expr.Left!);
                var right = AstUtilities.SkipParentheses(expr.Right!);
                return IsNarrowableOperand(left) || IsNarrowableOperand(right) ||
                    IsNarrowingTypeOfOperands(right, left) || IsNarrowingTypeOfOperands(left, right) ||
                    (AstUtilities.IsBooleanLiteral(right) && IsNarrowingExpression(left) || AstUtilities.IsBooleanLiteral(left) && IsNarrowingExpression(right));
            case SyntaxKind.InstanceOfKeyword:
                return IsNarrowableOperand(expr.Left!);
            case SyntaxKind.InKeyword:
                return IsNarrowingExpression(expr.Right!);
            case SyntaxKind.CommaToken:
                return IsNarrowingExpression(expr.Right!);
        }
        return false;
    }

    private static bool IsNarrowableOperand(Node expr)
    {
        switch (expr.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return IsNarrowableOperand(expr.Expression()!);
            case SyntaxKind.BinaryExpression:
                var binary = expr.AsBinaryExpression();
                switch (binary.OperatorToken!.Kind)
                {
                    case SyntaxKind.EqualsToken:
                        return IsNarrowableOperand(binary.Left!);
                    case SyntaxKind.CommaToken:
                        return IsNarrowableOperand(binary.Right!);
                }
                break;
        }
        return ContainsNarrowableReference(expr);
    }

    private static bool IsNarrowingTypeOfOperands(Node expr1, Node expr2)
    {
        return NodeTests.IsTypeOfExpression(expr1) && IsNarrowableOperand(expr1.Expression()!) && AstUtilities.IsStringLiteralLike(expr2);
    }

    private void ErrorOnNode(Node node, Message message, params object?[] args)
    {
        AddDiagnostic(CreateDiagnosticForNode(node, message, args));
    }

    private void ErrorOnFirstToken(Node node, Message message, params object?[] args)
    {
        var span = global::Ts.Scanner.Scanner.GetRangeOfTokenAtPosition(file, node.Pos);
        AddDiagnostic(Diagnostic.NewDiagnostic(file, span, message, args));
    }

    // Inside the binder, we may create a diagnostic for an as-yet unbound node (with potentially no parent pointers, implying no accessible source file)
    // If so, the node _must_ be in the current file (as that's the only way anything could have traversed to it to yield it as the error node)
    // This version of `createDiagnosticForNode` uses the binder's context to account for this, and always yields correct diagnostics even in these situations.
    private Diagnostic CreateDiagnosticForNode(Node node, Message message, params object?[] args)
    {
        return Diagnostic.NewDiagnostic(file, global::Ts.Scanner.Scanner.GetErrorRangeForNode(file, node), message, args);
    }

    private void AddDiagnostic(Diagnostic diagnostic)
    {
        file.SetBindDiagnostics([.. (file.BindDiagnostics() ?? []), diagnostic]);
    }

    private static SymbolFlags GetOptionalSymbolFlagForNode(Node node)
    {
        var postfixToken = node.PostfixToken();
        return CoreUtil.IfElse(postfixToken != null && postfixToken.Kind == SyntaxKind.QuestionToken, SymbolFlags.Optional, SymbolFlags.None);
    }

    private static bool IsStatementCondition(Node node)
    {
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.IfStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.DoStatement:
                return node.Parent.Expression() == node;
            case SyntaxKind.ForStatement:
                return node.Parent.AsForStatement().Condition == node;
            case SyntaxKind.ConditionalExpression:
                return node.Parent.AsConditionalExpression().Condition == node;
        }
        return false;
    }

    private static bool IsTopLevelLogicalExpression(Node node)
    {
        while (NodeTests.IsParenthesizedExpression(node.Parent!) || NodeTests.IsPrefixUnaryExpression(node.Parent!) && node.Parent!.AsPrefixUnaryExpression().Operator == SyntaxKind.ExclamationToken)
        {
            node = node.Parent!;
        }
        return !IsStatementCondition(node) && !AstUtilities.IsLogicalExpression(node.Parent!) && !(AstUtilities.IsOptionalChain(node.Parent!) && node.Parent!.Expression() == node);
    }

    private static bool IsAssignmentDeclaration(Node decl)
    {
        return NodeTests.IsBinaryExpression(decl) || AstUtilities.IsAccessExpression(decl) || NodeTests.IsIdentifier(decl) || NodeTests.IsCallExpression(decl);
    }

    private static bool IsEffectiveModuleDeclaration(Node node)
    {
        return NodeTests.IsModuleDeclaration(node) || NodeTests.IsIdentifier(node);
    }
}
