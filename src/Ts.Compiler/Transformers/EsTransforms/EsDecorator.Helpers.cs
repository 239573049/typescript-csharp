// Ported from tsgo internal/transformers/estransforms/esdecorator.go (lines 2327-2751) —
// pending-expression injection, decorator/call-binding lowering, private-element descriptor
// objects and forwarders, metadata initialization, and the `_classThis` assignment injector.
// Function order follows the Go original.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;

internal sealed partial class ESDecoratorTransformer
{
    // esdecorator.go:2327-2348 — prependExpressions prepends a list of expressions before a target
    // expression, preserving parenthesization. If expression is nil, the pending expressions are
    // inlined alone.
    private Node? PrependExpressions(List<Node>? pending, Node? expression)
    {
        var f = Factory();
        if (pending == null || pending.Count == 0)
        {
            return expression;
        }
        if (expression == null)
        {
            return f.InlineExpressions(pending.ToArray());
        }
        if (NodeTests.IsParenthesizedExpression(expression))
        {
            var pe = expression.AsParenthesizedExpression();
            var exprs = new List<Node>(pending.Count + 1) { pe.Expression! };
            exprs.InsertRange(0, pending);
            return f.UpdateParenthesizedExpression(pe, f.InlineExpressions(exprs.ToArray()));
        }
        var exprs2 = new List<Node>(pending.Count + 1);
        exprs2.AddRange(pending);
        exprs2.Add(expression);
        return f.InlineExpressions(exprs2.ToArray());
    }

    // esdecorator.go:2350-2357
    private Node? InjectPendingExpressions(Node? expression)
    {
        var result = PrependExpressions(pendingExpressions, expression);
        Debug.Assert(result != null);
        if (result != expression)
        {
            pendingExpressions = null;
        }
        return result;
    }

    // esdecorator.go:2359-2371
    private Node? InjectPendingInitializers(ClassInfo ci, bool isStatic, Node? expression)
    {
        var pending = isStatic ? ci.pendingStaticInitializers : ci.pendingInstanceInitializers;
        var result = PrependExpressions(pending, expression);
        if (result != expression)
        {
            if (isStatic)
            {
                ci.pendingStaticInitializers = null;
            }
            else
            {
                ci.pendingInstanceInitializers = null;
            }
        }
        return result;
    }

    // esdecorator.go:2373-2383 — Transforms all of the decorators for a declaration into an array
    // of expressions.
    private Node[] TransformAllDecoratorsOfDeclaration(Node[]? decorators)
    {
        if (decorators == null || decorators.Length == 0)
        {
            return [];
        }
        var result = new List<Node>(decorators.Length);
        foreach (var d in decorators)
        {
            result.Add(TransformDecorator(d));
        }
        return result.ToArray();
    }

    // esdecorator.go:2385-2398 — Transforms a decorator into an expression.
    private Node TransformDecorator(Node decorator)
    {
        var expression = Visitor().VisitNode(decorator.AsDecorator().Expression)!;
        EmitContext().SetEmitFlags(expression, EF.NoComments);

        // preserve the 'this' binding for an access expression
        var innerExpression = AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.All);
        if (AstUtilities.IsAccessExpression(innerExpression))
        {
            var (target, thisArg) = CreateCallBinding(expression);
            var bindCall = Factory().NewFunctionBindCall(target, thisArg, []);
            return Factory().RestoreOuterExpressions(expression, bindCall, OuterExpressionKinds.All);
        }
        return expression;
    }

    // esdecorator.go:2400-2439
    private (Node Target, Node ThisArg) CreateCallBinding(Node expression)
    {
        var f = Factory();
        var callee = AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.All);
        if (AstUtilities.IsSuperProperty(callee))
        {
            return (callee, f.NewThisExpression());
        }
        if (callee.Kind == SyntaxKind.SuperKeyword)
        {
            return (callee, f.NewThisExpression());
        }
        if ((EmitContext().EmitFlags(callee) & EF.HelperName) != 0)
        {
            return (callee, f.NewVoidZeroExpression());
        }
        if (NodeTests.IsPropertyAccessExpression(callee))
        {
            var pa = callee.AsPropertyAccessExpression();
            if (ShouldBeCapturedInTempVariable(pa.Expression!))
            {
                var thisArg = f.NewTempVariable();
                EmitContext().AddVariableDeclaration(thisArg);
                var assign = f.NewAssignmentExpression(thisArg, pa.Expression!);
                assign.Loc = pa.Expression!.Loc;
                var target = f.NewPropertyAccessExpression(assign, null, pa.Name(), NodeFlags.None);
                target.Loc = callee.Loc;
                return (target, thisArg);
            }
            return (callee, pa.Expression!);
        }
        if (NodeTests.IsElementAccessExpression(callee))
        {
            var ea = callee.AsElementAccessExpression();
            if (ShouldBeCapturedInTempVariable(ea.Expression!))
            {
                var thisArg = f.NewTempVariable();
                EmitContext().AddVariableDeclaration(thisArg);
                var assign = f.NewAssignmentExpression(thisArg, ea.Expression!);
                assign.Loc = ea.Expression!.Loc;
                var target = f.NewElementAccessExpression(assign, null, ea.ArgumentExpression, NodeFlags.None);
                target.Loc = callee.Loc;
                return (target, thisArg);
            }
            return (callee, ea.Expression!);
        }
        return (expression, f.NewVoidZeroExpression());
    }

    // esdecorator.go:2441-2458
    private bool ShouldBeCapturedInTempVariable(Node node)
    {
        // This is a simplified version of the general shouldBeCapturedInTempVariable from
        // nodeFactory with cacheIdentifiers=true, since createCallBinding in this transform
        // always caches identifiers.
        var target = AstUtilities.SkipParentheses(node);
        switch (target.Kind)
        {
            case SyntaxKind.Identifier:
                // cacheIdentifiers is always true for this transform's createCallBinding
                return true;
            case SyntaxKind.ThisKeyword:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.StringLiteral:
                return false;
            default:
                return true;
        }
    }

    // esdecorator.go:2460-2504 — Creates a "value", "get", or "set" method for a
    // pseudo-PropertyDescriptor object created for a private element.
    private Node CreateDescriptorMethod(
        Node original,
        Node name, // PrivateIdentifier
        ModifierList? modifiers,
        TokenNode? asteriskToken,
        string kind,
        NodeList parameters,
        Node? body)
    {
        var f = Factory();
        var ec = EmitContext();

        body ??= f.NewBlock(f.NewNodeList([]), false);

        var funcExpr = f.NewFunctionExpression(
            modifiers,
            asteriskToken,
            null, // name
            null, // typeParameters
            parameters,
            null, // type
            null, // fullSignature
            body);
        ec.SetOriginal(funcExpr, original);
        ec.SetSourceMapRange(funcExpr, global::Ts.Transformers.Transformers.MoveRangePastDecorators(original));
        ec.SetEmitFlags(funcExpr, EF.NoComments);

        string prefix = null!;
        if (kind == "get" || kind == "set")
        {
            prefix = kind;
        }
        var functionName = f.NewStringLiteralFromNode(name);
        var namedFunction = f.NewSetFunctionNameHelper(funcExpr, functionName, prefix);

        var method = f.NewPropertyAssignment(null, f.NewIdentifier(kind), null, null, namedFunction);
        ec.SetOriginal(method, original);
        ec.SetSourceMapRange(method, global::Ts.Transformers.Transformers.MoveRangePastDecorators(original));
        ec.SetEmitFlags(method, EF.NoComments);
        return method;
    }

    // esdecorator.go:2506-2518 — Creates a pseudo-PropertyDescriptor object used when decorating a
    // private MethodDeclaration.
    private Node? CreateMethodDescriptorObject(Node member, ModifierList? modifiers)
    {
        var f = Factory();
        var parameters = Visitor().VisitNodes(member.ParameterList());
        var body = Visitor().VisitNode(member.Body());
        var method = member.AsMethodDeclaration();
        return f.NewObjectLiteralExpression(
            f.NewNodeList(
            [
                CreateDescriptorMethod(member, member.Name()!, modifiers, method.AsteriskToken, "value", parameters!, body),
            ]),
            false);
    }

    // esdecorator.go:2520-2530 — Creates a pseudo-PropertyDescriptor object used when decorating a
    // private GetAccessor.
    private Node? CreateGetAccessorDescriptorObject(Node member, ModifierList? modifiers)
    {
        var f = Factory();
        var body = Visitor().VisitNode(member.Body());
        return f.NewObjectLiteralExpression(
            f.NewNodeList(
            [
                CreateDescriptorMethod(member, member.Name()!, modifiers, null, "get", f.NewNodeList([]), body),
            ]),
            false);
    }

    // esdecorator.go:2532-2543 — Creates a pseudo-PropertyDescriptor object used when decorating a
    // private SetAccessor.
    private Node? CreateSetAccessorDescriptorObject(Node member, ModifierList? modifiers)
    {
        var f = Factory();
        var parameters = Visitor().VisitNodes(member.ParameterList());
        var body = Visitor().VisitNode(member.Body());
        return f.NewObjectLiteralExpression(
            f.NewNodeList(
            [
                CreateDescriptorMethod(member, member.Name()!, modifiers, null, "set", parameters!, body),
            ]),
            false);
    }

    // esdecorator.go:2545-2582 — Creates a pseudo-PropertyDescriptor object used when decorating a
    // private auto-accessor PropertyDeclaration. The descriptor contains get/set methods that
    // access the generated backing field.
    private Node? CreateAccessorPropertyDescriptorObject(Node member, ModifierList? _)
    {
        //  {
        //      get() { return this.${privateName}; },
        //      set(value) { this.${privateName} = value; },
        //  }
        var f = Factory();
        var backingFieldName = f.NewGeneratedPrivateNameForNodeEx(member.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" });
        return f.NewObjectLiteralExpression(
            f.NewNodeList(
            [
                CreateDescriptorMethod(
                    member, member.Name()!, null, null, "get",
                    f.NewNodeList([]),
                    f.NewBlock(f.NewNodeList(
                    [
                        f.NewReturnStatement(
                            f.NewPropertyAccessExpression(f.NewThisExpression(), null, backingFieldName, NodeFlags.None)),
                    ]), false)),
                CreateDescriptorMethod(
                    member, member.Name()!, null, null, "set",
                    f.NewNodeList(
                    [
                        f.NewParameterDeclaration(null, null, f.NewIdentifier("value"), null, null, null),
                    ]),
                    f.NewBlock(f.NewNodeList(
                    [
                        f.NewExpressionStatement(
                            f.NewAssignmentExpression(
                                f.NewPropertyAccessExpression(f.NewThisExpression(), null, backingFieldName, NodeFlags.None),
                                f.NewIdentifier("value"))),
                    ]), false)),
            ]),
            false);
    }

    // esdecorator.go:2584-2601 — Creates a MethodDeclaration that forwards its invocation to a
    // PropertyDescriptor object.
    private Node? CreateMethodDescriptorForwarder(ModifierList? modifiers, Node name, IdentifierNode descriptorName)
    {
        var f = Factory();
        var staticOnly = staticOnlyModifierVisitor.VisitModifiers(modifiers);
        return f.AsNodeFactory().NewGetAccessorDeclaration(
            staticOnly,
            name,
            null, // typeParameters
            f.NewNodeList([]),
            null, // type
            null, // fullSignature
            f.NewBlock(f.NewNodeList(
            [
                f.NewReturnStatement(
                    f.NewPropertyAccessExpression(descriptorName, null, f.NewIdentifier("value"), NodeFlags.None)),
            ]), false));
    }

    // esdecorator.go:2603-2624 — Creates a GetAccessor that forwards its invocation to a
    // PropertyDescriptor object.
    private Node? CreateGetAccessorDescriptorForwarder(ModifierList? modifiers, Node name, IdentifierNode descriptorName)
    {
        var f = Factory();
        var staticOnly = staticOnlyModifierVisitor.VisitModifiers(modifiers);
        return f.AsNodeFactory().NewGetAccessorDeclaration(
            staticOnly,
            name,
            null, // typeParameters
            f.NewNodeList([]),
            null, // type
            null, // fullSignature
            f.NewBlock(f.NewNodeList(
            [
                f.NewReturnStatement(
                    f.NewFunctionCallCall(
                        f.NewPropertyAccessExpression(descriptorName, null, f.NewIdentifier("get"), NodeFlags.None),
                        f.NewThisExpression(),
                        [])),
            ]), false));
    }

    // esdecorator.go:2626-2649 — Creates a SetAccessor that forwards its invocation to a
    // PropertyDescriptor object.
    private Node? CreateSetAccessorDescriptorForwarder(ModifierList? modifiers, Node name, IdentifierNode descriptorName)
    {
        var f = Factory();
        var staticOnly = staticOnlyModifierVisitor.VisitModifiers(modifiers);
        return f.NewSetAccessorDeclaration(
            staticOnly,
            name,
            null, // typeParameters
            f.NewNodeList(
            [
                f.NewParameterDeclaration(null, null, f.NewIdentifier("value"), null, null, null),
            ]),
            null, // type
            null, // fullSignature
            f.NewBlock(f.NewNodeList(
            [
                f.NewReturnStatement(
                    f.NewFunctionCallCall(
                        f.NewPropertyAccessExpression(descriptorName, null, f.NewIdentifier("set"), NodeFlags.None),
                        f.NewThisExpression(),
                        [f.NewIdentifier("value")])),
            ]), false));
    }

    // esdecorator.go:2651-2684
    private Node CreateMetadata(IdentifierNode name, IdentifierNode? classSuper)
    {
        var f = Factory();

        Node superMetadata;
        if (classSuper != null)
        {
            superMetadata = CreateSymbolMetadataReference(classSuper);
        }
        else
        {
            superMetadata = f.NewToken(SyntaxKind.NullKeyword);
        }

        var objectCreate = f.NewCallExpression(
            f.NewPropertyAccessExpression(f.NewIdentifier("Object"), null, f.NewIdentifier("create"), NodeFlags.None),
            null, null,
            f.NewNodeList([superMetadata]),
            NodeFlags.None);

        var symbolCheck = f.NewLogicalANDExpression(
            f.NewTypeCheck(f.NewIdentifier("Symbol"), "function"),
            f.NewPropertyAccessExpression(f.NewIdentifier("Symbol"), null, f.NewIdentifier("metadata"), NodeFlags.None));

        var conditional = f.NewConditionalExpression(
            symbolCheck,
            f.NewToken(SyntaxKind.QuestionToken),
            objectCreate,
            f.NewToken(SyntaxKind.ColonToken),
            f.NewVoidZeroExpression());

        var varDecl = f.NewVariableDeclaration(name, null, null, conditional);
        var varDeclList = f.NewVariableDeclarationList(f.NewNodeList([varDecl]), NodeFlags.Const);
        return f.NewVariableStatement(null, varDeclList);
    }

    // esdecorator.go:2686-2710
    private Node CreateSymbolMetadata(Node target, IdentifierNode value)
    {
        var f = Factory();

        // Object.defineProperty(target, Symbol.metadata, { configurable: true, writable: true, enumerable: true, value })
        var symbolMetadata = f.NewPropertyAccessExpression(f.NewIdentifier("Symbol"), null, f.NewIdentifier("metadata"), NodeFlags.None);

        Node[] descriptorProps =
        [
            f.NewPropertyAssignment(null, f.NewIdentifier("enumerable"), null, null, f.NewTrueExpression()),
            f.NewPropertyAssignment(null, f.NewIdentifier("configurable"), null, null, f.NewTrueExpression()),
            f.NewPropertyAssignment(null, f.NewIdentifier("writable"), null, null, f.NewTrueExpression()),
            f.NewPropertyAssignment(null, f.NewIdentifier("value"), null, null, value),
        ];
        var descriptor = f.NewObjectLiteralExpression(f.NewNodeList(descriptorProps), false);

        var defineProperty = f.NewCallExpression(
            f.NewPropertyAccessExpression(f.NewIdentifier("Object"), null, f.NewIdentifier("defineProperty"), NodeFlags.None),
            null, null,
            f.NewNodeList([target, symbolMetadata, descriptor]),
            NodeFlags.None);

        var ifStatement = f.AsNodeFactory().NewIfStatement(value, f.NewExpressionStatement(defineProperty), null);
        EmitContext().SetEmitFlags(ifStatement, EF.SingleLine);
        return ifStatement;
    }

    // esdecorator.go:2712-2717
    private Node CreateSymbolMetadataReference(IdentifierNode classSuper)
    {
        var f = Factory();
        var symbolMetadata = f.NewPropertyAccessExpression(f.NewIdentifier("Symbol"), null, f.NewIdentifier("metadata"), NodeFlags.None);
        var elementAccess = f.NewElementAccessExpression(classSuper, null, symbolMetadata, NodeFlags.None);
        return f.NewBinaryExpression(null, elementAccess, null, f.NewToken(SyntaxKind.QuestionQuestionToken), f.NewToken(SyntaxKind.NullKeyword));
    }

    // esdecorator.go:2719-2751 — injectClassThisAssignmentIfMissing.
    private static Node InjectClassThisAssignmentIfMissing(global::Ts.Printer.EmitContext ec, global::Ts.Printer.NodeFactory f, Node node, IdentifierNode classThis)
    {
        if (ClassFieldsTransformer.ClassHasClassThisAssignment(ec, node))
        {
            return node;
        }

        // Create: static { _classThis = this; }
        var expression = f.NewAssignmentExpression(classThis, f.NewThisExpression());
        var statement = f.NewExpressionStatement(expression);
        var body = f.NewBlock(f.NewNodeList([statement]), false);
        var staticBlock = f.AsNodeFactory().NewClassStaticBlockDeclaration(null, body);
        ec.SetClassThis(staticBlock, classThis);

        if (node.Name() != null)
        {
            ec.SetSourceMapRange(statement, node.Name()!.Loc);
        }

        var newMembers = new List<Node>(1 + node.Members()!.Length)
        {
            staticBlock,
        };
        newMembers.AddRange(node.Members()!);
        var membersList = f.NewNodeList(newMembers.ToArray());
        membersList.Loc = node.MemberList()!.Loc;

        Node updatedNode;
        if (NodeTests.IsClassDeclaration(node))
        {
            var cd = node.AsClassDeclaration();
            updatedNode = f.UpdateClassDeclaration(cd, cd.Modifiers(), cd.Name(), null, cd.HeritageClauses, membersList);
        }
        else
        {
            var ce = node.AsClassExpression();
            updatedNode = f.AsNodeFactory().UpdateClassExpression(ce, ce.Modifiers(), ce.Name(), null, ce.HeritageClauses, membersList);
        }
        ec.SetClassThis(updatedNode, classThis);
        return updatedNode;
    }
}
