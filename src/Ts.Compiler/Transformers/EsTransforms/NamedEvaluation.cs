// Ported from tsgo internal/transformers/estransforms/namedevaluation.go — named-evaluation detection
// and SetFunctionName / class static-block injection, consumed by the class-fields and ES-decorator
// transformers.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

internal static class NamedEvaluationUtilities
{
    // namedevaluation.go:16 — isClassNamedEvaluationHelperBlock: whether a node is a `static {}` block
    // containing only a single call to the `__setFunctionName` helper where that call's second argument
    // is the value stored in the `assignedName` property of the block's EmitNode.
    internal static bool IsClassNamedEvaluationHelperBlock(global::Ts.Printer.EmitContext emitContext, Node node)
    {
        if (!NodeTests.IsClassStaticBlockDeclaration(node) || node.AsClassStaticBlockDeclaration().Body!.Statements()!.Length != 1)
        {
            return false;
        }

        Node statement = node.AsClassStaticBlockDeclaration().Body!.Statements()![0];
        if (NodeTests.IsExpressionStatement(statement))
        {
            Node expression = statement.Expression()!;
            if (emitContext.IsCallToHelper(expression, "__setFunctionName"))
            {
                var arguments = expression.AsCallExpression().Arguments;
                return arguments!.Nodes.Length >= 2 &&
                    arguments.Nodes[1] == emitContext.AssignedName(node);
            }
        }
        return false;
    }

    // namedevaluation.go:38 — classHasExplicitlyAssignedName.
    internal static bool ClassHasExplicitlyAssignedName(global::Ts.Printer.EmitContext emitContext, Node node)
    {
        if (emitContext.AssignedName(node) != null)
        {
            foreach (var member in node.Members() ?? [])
            {
                if (IsClassNamedEvaluationHelperBlock(emitContext, member))
                {
                    return true;
                }
            }
        }
        return false;
    }

    // namedevaluation.go:54 — classHasDeclaredOrExplicitlyAssignedName.
    internal static bool ClassHasDeclaredOrExplicitlyAssignedName(global::Ts.Printer.EmitContext emitContext, Node node) =>
        node.Name() != null || ClassHasExplicitlyAssignedName(emitContext, node);

    // namedevaluation.go:63 — isAnonymousFunctionDefinition.
    // See https://tc39.es/ecma262/#sec-isanonymousfunctiondefinition
    internal static bool IsAnonymousFunctionDefinition(global::Ts.Printer.EmitContext emitContext, Node node, Func<Node, bool>? cb)
    {
        node = AstUtilities.SkipOuterExpressions(node, OuterExpressionKinds.All);
        switch (node.Kind)
        {
            case SyntaxKind.ClassExpression:
                if (ClassHasDeclaredOrExplicitlyAssignedName(emitContext, node))
                {
                    return false;
                }
                break;
            case SyntaxKind.FunctionExpression:
                if (node.AsFunctionExpression().Name() != null)
                {
                    return false;
                }
                break;
            case SyntaxKind.ArrowFunction:
                break;
            default:
                return false;
        }
        if (cb != null)
        {
            return cb(node);
        }
        return true;
    }

    // namedevaluation.go:87 — isNamedEvaluation.
    internal static bool IsNamedEvaluation(global::Ts.Printer.EmitContext emitContext, Node node) =>
        IsNamedEvaluationAnd(emitContext, node, null);

    // namedevaluation.go:91 — isNamedEvaluationAnd. PORT-NOTE: ast.IsNamedEvaluationSource currently
    // lives as a Checker static (Checker.Expressions.Deps.cs); relocating it to AstUtilities is a
    // follow-up cleanup.
    internal static bool IsNamedEvaluationAnd(global::Ts.Printer.EmitContext emitContext, Node node, Func<Node, bool>? cb)
    {
        if (!global::Ts.Checker.Checker.IsNamedEvaluationSource(node))
        {
            return false;
        }
        switch (node.Kind)
        {
            case SyntaxKind.ShorthandPropertyAssignment:
                return IsAnonymousFunctionDefinition(emitContext, node.AsShorthandPropertyAssignment().ObjectAssignmentInitializer!, cb);
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.BindingElement:
            case SyntaxKind.PropertyDeclaration:
                return IsAnonymousFunctionDefinition(emitContext, node.Initializer()!, cb);
            case SyntaxKind.BinaryExpression:
                return IsAnonymousFunctionDefinition(emitContext, node.AsBinaryExpression().Right!, cb);
            case SyntaxKind.ExportAssignment:
                return IsAnonymousFunctionDefinition(emitContext, node.Expression()!, cb);
            default:
                Debug.Fail("Unhandled case in isNamedEvaluation");
                return false;
        }
    }

    // namedevaluation.go:111 — getAssignedNameOfIdentifier.
    private static Node GetAssignedNameOfIdentifier(global::Ts.Printer.EmitContext emitContext, Node name, Node expression)
    {
        var original = emitContext.MostOriginal(AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.All));
        if (original != null && (NodeTests.IsClassDeclaration(original) || NodeTests.IsFunctionDeclaration(original)) &&
            original.Name() == null && AstUtilities.HasSyntacticModifier(original, ModifierFlags.Default))
        {
            return emitContext.Factory.NewStringLiteral("default", TokenFlags.None);
        }
        return emitContext.Factory.NewStringLiteralFromNode(name);
    }

    // namedevaluation.go:120 — getAssignedNameOfPropertyName.
    private static (Node AssignedName, Node UpdatedName) GetAssignedNameOfPropertyName(global::Ts.Printer.EmitContext emitContext, Node name, string assignedNameText)
    {
        var factory = emitContext.Factory;
        if (assignedNameText.Length > 0)
        {
            return (factory.NewStringLiteral(assignedNameText, TokenFlags.None), name);
        }

        if (AstUtilities.IsPropertyNameLiteral(name) || NodeTests.IsPrivateIdentifier(name))
        {
            return (factory.NewStringLiteralFromNode(name), name);
        }

        Node expression = name.Expression()!;
        if (AstUtilities.IsPropertyNameLiteral(expression) && !NodeTests.IsIdentifier(expression))
        {
            return (factory.NewStringLiteralFromNode(expression), name);
        }

        Debug.Assert(NodeTests.IsComputedPropertyName(name), "Expected computed property name");

        Node assignedName = factory.NewGeneratedNameForNode(name);
        emitContext.AddVariableDeclaration(assignedName);

        var key = factory.NewPropKeyHelper(expression);
        var assignment = factory.NewAssignmentExpression(assignedName, key);
        Node updatedName = factory.AsNodeFactory().UpdateComputedPropertyName(name.AsComputedPropertyName(), assignment);
        return (assignedName, updatedName);
    }

    // namedevaluation.go:155 — createClassNamedEvaluationHelperBlock: produces
    //   static { __setFunctionName(this, "C"); }
    // The assignedName parameter is the expression used to resolve the assigned name at runtime and
    // should not produce side effects; thisExpression overrides the `this` reference expression.
    internal static Node CreateClassNamedEvaluationHelperBlock(global::Ts.Printer.EmitContext emitContext, Node assignedName, Node? thisExpression)
    {
        thisExpression ??= emitContext.Factory.NewThisExpression();

        var factory = emitContext.Factory;
        var expression = factory.NewSetFunctionNameHelper(thisExpression, assignedName, "" /*prefix*/);
        var statement = factory.NewExpressionStatement(expression);
        var body = factory.NewBlock(factory.NewNodeList([statement]), false /*multiLine*/);
        var block = factory.AsNodeFactory().NewClassStaticBlockDeclaration(null /*modifiers*/, body);

        // We use `emitNode.assignedName` to indicate this is a NamedEvaluation helper block
        // and to stash the expression used to resolve the assigned name.
        emitContext.SetAssignedName(block, assignedName);
        return block;
    }

    // namedevaluation.go:178 — injectClassNamedEvaluationHelperBlockIfMissing. NOTE: if the class has a
    // `_classThis` assignment block, this helper is injected after that block.
    internal static Node InjectClassNamedEvaluationHelperBlockIfMissing(
        global::Ts.Printer.EmitContext emitContext,
        Node node,
        Node assignedName,
        Node? thisExpression)
    {
        if (ClassHasExplicitlyAssignedName(emitContext, node))
        {
            return node;
        }

        var factory = emitContext.Factory;
        var namedEvaluationBlock = CreateClassNamedEvaluationHelperBlock(emitContext, assignedName, thisExpression);
        if (node.Name() != null)
        {
            emitContext.SetSourceMapRange(namedEvaluationBlock.Body()!.Statements()![0], node.Name()!.Loc);
        }

        var members = node.Members() ?? [];
        int insertionIndex = 0;
        for (int i = 0; i < members.Length; i++)
        {
            if (ClassThisUtilities.IsClassThisAssignmentBlock(emitContext, members[i]))
            {
                insertionIndex = i + 1;
                break;
            }
        }
        var newMembers = new List<Node>(members.Length + 1);
        newMembers.AddRange(members[..insertionIndex]);
        newMembers.Add(namedEvaluationBlock);
        newMembers.AddRange(members[insertionIndex..]);
        var membersList = factory.NewNodeList(newMembers.ToArray());
        membersList.Loc = node.MemberList()!.Loc;

        Node oldNode = node;
        if (NodeTests.IsClassDeclaration(node))
        {
            node = factory.AsNodeFactory().UpdateClassDeclaration(
                node.AsClassDeclaration(),
                node.Modifiers(),
                node.Name(),
                node.TypeParameterList(),
                node.AsClassDeclaration().HeritageClauses,
                membersList);
        }
        else
        {
            node = factory.AsNodeFactory().UpdateClassExpression(
                node.AsClassExpression(),
                node.Modifiers(),
                node.Name(),
                node.TypeParameterList(),
                node.AsClassExpression().HeritageClauses,
                membersList);
        }

        emitContext.SetAssignedName(node, assignedName);

        // Transfer ClassThis from old to new node, since UpdateClassExpression creates
        // a new node that won't have ClassThis set on it.
        var ct = emitContext.ClassThis(oldNode);
        if (ct != null)
        {
            emitContext.SetClassThis(node, ct);
        }

        return node;
    }

    // namedevaluation.go:252 — finishTransformNamedEvaluation.
    private static Node FinishTransformNamedEvaluation(
        global::Ts.Printer.EmitContext emitContext,
        Node expression,
        Node assignedName,
        bool ignoreEmptyStringLiteral)
    {
        if (ignoreEmptyStringLiteral && NodeTests.IsStringLiteral(assignedName) && assignedName.Text().Length == 0)
        {
            return expression;
        }

        var factory = emitContext.Factory;
        var innerExpression = AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.All);

        Node updatedExpression;
        if (NodeTests.IsClassExpression(innerExpression))
        {
            updatedExpression = InjectClassNamedEvaluationHelperBlockIfMissing(emitContext, innerExpression, assignedName, null /*thisExpression*/);
        }
        else
        {
            updatedExpression = factory.NewSetFunctionNameHelper(innerExpression, assignedName, "" /*prefix*/);
        }

        return factory.RestoreOuterExpressions(expression, updatedExpression, OuterExpressionKinds.All);
    }

    // namedevaluation.go:275 — transformNamedEvaluationOfPropertyAssignment (13.2.5.5 RS).
    private static Node TransformNamedEvaluationOfPropertyAssignment(global::Ts.Printer.EmitContext context, PropertyAssignment node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = context.Factory;
        var (assignedName, name) = GetAssignedNameOfPropertyName(context, node.Name()!, assignedNameText);
        var initializer = FinishTransformNamedEvaluation(context, node.Initializer!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdatePropertyAssignment(node, null /*modifiers*/, name, null /*postfixToken*/, null /*typeNode*/, initializer);
    }

    // namedevaluation.go:289 — transformNamedEvaluationOfShorthandAssignmentProperty (13.15.5.3 RS).
    private static Node TransformNamedEvaluationOfShorthandAssignmentProperty(global::Ts.Printer.EmitContext emitContext, ShorthandPropertyAssignment node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        Node assignedName = assignedNameText.Length > 0
            ? factory.NewStringLiteral(assignedNameText, TokenFlags.None)
            : GetAssignedNameOfIdentifier(emitContext, node.Name()!, node.ObjectAssignmentInitializer!);
        var objectAssignmentInitializer = FinishTransformNamedEvaluation(emitContext, node.ObjectAssignmentInitializer!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdateShorthandPropertyAssignment(
            node,
            null /*modifiers*/,
            node.Name(),
            null /*postfixToken*/,
            null /*typeNode*/,
            node.EqualsToken,
            objectAssignmentInitializer);
    }

    // namedevaluation.go:317 — transformNamedEvaluationOfVariableDeclaration (14.3.1.2 / 14.3.2.1 RS).
    private static Node TransformNamedEvaluationOfVariableDeclaration(global::Ts.Printer.EmitContext emitContext, VariableDeclaration node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        Node assignedName = assignedNameText.Length > 0
            ? factory.NewStringLiteral(assignedNameText, TokenFlags.None)
            : GetAssignedNameOfIdentifier(emitContext, node.Name()!, node.Initializer!);
        var initializer = FinishTransformNamedEvaluation(emitContext, node.Initializer!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdateVariableDeclaration(
            node,
            node.Name(),
            null /*exclamationToken*/,
            null /*typeNode*/,
            initializer);
    }

    // namedevaluation.go:349 — transformNamedEvaluationOfParameterDeclaration (8.6.3 / 14.3.3.3 RS).
    private static Node TransformNamedEvaluationOfParameterDeclaration(global::Ts.Printer.EmitContext emitContext, ParameterDeclaration node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        Node assignedName = assignedNameText.Length > 0
            ? factory.NewStringLiteral(assignedNameText, TokenFlags.None)
            : GetAssignedNameOfIdentifier(emitContext, node.Name()!, node.Initializer!);
        var initializer = FinishTransformNamedEvaluation(emitContext, node.Initializer!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdateParameterDeclaration(
            node,
            null /*modifiers*/,
            node.DotDotDotToken,
            node.Name(),
            null /*questionToken*/,
            null /*typeNode*/,
            initializer);
    }

    // namedevaluation.go:385 — transformNamedEvaluationOfBindingElement (8.6.3 / 14.3.3.3 RS).
    private static Node TransformNamedEvaluationOfBindingElement(global::Ts.Printer.EmitContext emitContext, BindingElement node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        Node assignedName = assignedNameText.Length > 0
            ? factory.NewStringLiteral(assignedNameText, TokenFlags.None)
            : GetAssignedNameOfIdentifier(emitContext, node.Name()!, node.Initializer!);
        var initializer = FinishTransformNamedEvaluation(emitContext, node.Initializer!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdateBindingElement(
            node,
            node.DotDotDotToken,
            node.PropertyName,
            node.Name(),
            initializer);
    }

    // namedevaluation.go:419 — transformNamedEvaluationOfPropertyDeclaration (10.2.1.3 RS).
    private static Node TransformNamedEvaluationOfPropertyDeclaration(global::Ts.Printer.EmitContext emitContext, PropertyDeclaration node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        var (assignedName, name) = GetAssignedNameOfPropertyName(emitContext, node.Name()!, assignedNameText);
        var initializer = FinishTransformNamedEvaluation(emitContext, node.Initializer!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdatePropertyDeclaration(
            node,
            node.Modifiers(),
            name,
            null /*postfixToken*/,
            null /*typeNode*/,
            initializer);
    }

    // namedevaluation.go:440 — transformNamedEvaluationOfAssignmentExpression (13.15.2 RS).
    private static Node TransformNamedEvaluationOfAssignmentExpression(global::Ts.Printer.EmitContext emitContext, BinaryExpression node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        Node assignedName = assignedNameText.Length > 0
            ? factory.NewStringLiteral(assignedNameText, TokenFlags.None)
            : GetAssignedNameOfIdentifier(emitContext, node.Left!, node.Right!);
        var right = FinishTransformNamedEvaluation(emitContext, node.Right!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdateBinaryExpression(
            node,
            null /*modifiers*/,
            node.Left,
            null /*typeNode*/,
            node.OperatorToken,
            right);
    }

    // namedevaluation.go:485 — transformNamedEvaluationOfExportAssignment (16.2.3.7 RS).
    // NOTE: since emit for `export =` translates to `module.exports = ...`, the assigned name of the
    // class or function is `""`.
    private static Node TransformNamedEvaluationOfExportAssignment(global::Ts.Printer.EmitContext emitContext, ExportAssignment node, bool ignoreEmptyStringLiteral, string assignedNameText)
    {
        var factory = emitContext.Factory;
        Node assignedName;
        if (assignedNameText.Length > 0)
        {
            assignedName = factory.NewStringLiteral(assignedNameText, TokenFlags.None);
        }
        else if (node.IsExportEquals)
        {
            assignedName = factory.NewStringLiteral("", TokenFlags.None);
        }
        else
        {
            assignedName = factory.NewStringLiteral("default", TokenFlags.None);
        }
        var expression = FinishTransformNamedEvaluation(emitContext, node.Expression!, assignedName, ignoreEmptyStringLiteral);
        return factory.AsNodeFactory().UpdateExportAssignment(
            node,
            null /*modifiers*/,
            node.IsExportEquals,
            null /*typeNode*/,
            expression);
    }

    // namedevaluation.go:515 — transformNamedEvaluation: performs a shallow transformation of a
    // NamedEvaluation node, such that a valid name will be assigned.
    internal static Node TransformNamedEvaluation(global::Ts.Printer.EmitContext context, Node node, bool ignoreEmptyStringLiteral, string assignedName)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAssignment:
                return TransformNamedEvaluationOfPropertyAssignment(context, node.AsPropertyAssignment(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.ShorthandPropertyAssignment:
                return TransformNamedEvaluationOfShorthandAssignmentProperty(context, node.AsShorthandPropertyAssignment(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.VariableDeclaration:
                return TransformNamedEvaluationOfVariableDeclaration(context, node.AsVariableDeclaration(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.Parameter:
                return TransformNamedEvaluationOfParameterDeclaration(context, node.AsParameterDeclaration(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.BindingElement:
                return TransformNamedEvaluationOfBindingElement(context, node.AsBindingElement(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.PropertyDeclaration:
                return TransformNamedEvaluationOfPropertyDeclaration(context, node.AsPropertyDeclaration(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.BinaryExpression:
                return TransformNamedEvaluationOfAssignmentExpression(context, node.AsBinaryExpression(), ignoreEmptyStringLiteral, assignedName);
            case SyntaxKind.ExportAssignment:
                return TransformNamedEvaluationOfExportAssignment(context, node.AsExportAssignment(), ignoreEmptyStringLiteral, assignedName);
            default:
                Debug.Fail("Unhandled case in transformNamedEvaluation");
                return node;
        }
    }
}
