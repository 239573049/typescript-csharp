// Ported from tsgo internal/transformers/estransforms/esdecorator.go (lines 1724-2325) —
// expression-level transform: this/call/tagged-template/super-property access rewriting for
// `_classThis`/`_classSuper`, NamedEvaluation sites, destructuring assignment targets, and the
// pending-expression machinery. Function order follows the Go original.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;

internal sealed partial class ESDecoratorTransformer
{
    // esdecorator.go:1724-1729
    private Node? VisitThisExpression(Node node)
    {
        if (classThis != null)
        {
            return classThis;
        }
        return node;
    }

    // esdecorator.go:1731-1742
    private Node? VisitCallExpression(Node node)
    {
        var call = node.AsCallExpression();
        if (AstUtilities.IsSuperProperty(call.Expression!) && classThis != null)
        {
            var expression = Visitor().VisitNode(call.Expression);
            var argumentsList = Visitor().VisitNodes(call.Arguments);
            var invocation = Factory().NewFunctionCallCall(expression!, classThis, argumentsList!.Nodes);
            EmitContext().SetOriginal(invocation, node);
            invocation.Loc = node.Loc;
            return invocation;
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:1744-1755
    private Node? VisitTaggedTemplateExpression(Node node)
    {
        var tte = node.AsTaggedTemplateExpression();
        if (AstUtilities.IsSuperProperty(tte.Tag!) && classThis != null)
        {
            var tag = Visitor().VisitNode(tte.Tag);
            var boundTag = Factory().NewFunctionBindCall(tag!, classThis, []);
            EmitContext().SetOriginal(boundTag, node);
            boundTag.Loc = node.Loc;
            var template = Visitor().VisitNode(tte.Template);
            return Factory().UpdateTaggedTemplateExpression(tte, boundTag, null, null, template, tte.Flags);
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:1757-1767
    private Node? VisitPropertyAccessExpression(Node node)
    {
        var pa = node.AsPropertyAccessExpression();
        if (AstUtilities.IsSuperProperty(node) && NodeTests.IsIdentifier(pa.Name()!) && classThis != null && classSuper != null)
        {
            var propertyName = Factory().NewStringLiteralFromNode(pa.Name()!);
            var superProperty = Factory().NewReflectGetCall(classSuper, propertyName, classThis);
            EmitContext().SetOriginal(superProperty, pa.Expression!);
            superProperty.Loc = pa.Expression!.Loc;
            return superProperty;
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:1769-1779
    private Node? VisitElementAccessExpression(Node node)
    {
        var ea = node.AsElementAccessExpression();
        if (AstUtilities.IsSuperProperty(node) && classThis != null && classSuper != null)
        {
            var propertyName = Visitor().VisitNode(ea.ArgumentExpression);
            var superProperty = Factory().NewReflectGetCall(classSuper, propertyName!, classThis);
            EmitContext().SetOriginal(superProperty, ea.Expression!);
            superProperty.Loc = ea.Expression!.Loc;
            return superProperty;
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:1781-1824 — 8.6.3 RS: IteratorBindingInitialization
    //
    //	SingleNameBinding : BindingIdentifier Initializer?
    //	  ...
    //	  5. If |Initializer| is present and _v_ is *undefined*, then
    //	     a. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //	        i. Set _v_ to ? NamedEvaluation of |Initializer| with argument _bindingId_.
    //	  ...
    //
    // 14.3.3.3 RS: KeyedBindingInitialization
    //
    //	SingleNameBinding : BindingIdentifier Initializer?
    //	  ...
    //	  4. If |Initializer| is present and _v_ is *undefined*, then
    //	     a. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //	        i. Set _v_ to ? NamedEvaluation of |Initializer| with argument _bindingId_.
    //	  ...
    private Node? VisitParameterDeclaration(ParameterDeclaration node)
    {
        var paramNode = node.AsNode();
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), paramNode, IsAnonymousClassNeedingAssignedName))
        {
            paramNode = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), paramNode, CanIgnoreEmptyStringLiteralInAssignedName(paramNode.Initializer()), "");
            node = paramNode.AsParameterDeclaration();
        }

        var updated = Factory().AsNodeFactory().UpdateParameterDeclaration(
            node,
            null, // modifiers - strip all modifiers (including decorators)
            node.DotDotDotToken,
            Visitor().VisitNode(node.Name()),
            null, // questionToken
            null, // type
            Visitor().VisitNode(node.Initializer));
        if (updated != paramNode)
        {
            // While we emit the source map for the node after skipping decorators and modifiers,
            // we need to emit the comments for the original range.
            EmitContext().SetCommentRange(updated, paramNode.Loc);
            var newLoc = global::Ts.Transformers.Transformers.MoveRangePastModifiers(paramNode);
            updated.Loc = newLoc;
            EmitContext().SetSourceMapRange(updated, newLoc);
            EmitContext().SetEmitFlags(updated.Name()!, EF.NoTrailingSourceMap);
        }
        return updated;
    }

    // esdecorator.go:1826-1875 — visitNamedEvaluationSite replaces Strada's visitPropertyAssignment,
    // visitVariableDeclaration, and visitBindingElement, which all share the same logic.
    //
    // 13.2.5.5 RS: PropertyDefinitionEvaluation (PropertyAssignment)
    //
    //	PropertyAssignment : PropertyName `:` AssignmentExpression
    //	  ...
    //	  5. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and _isProtoSetter_ is *false*, then
    //	     a. Let _popValue_ be ? NamedEvaluation of |AssignmentExpression| with argument _propKey_.
    //	  ...
    //
    // 14.3.1.2 RS: Evaluation (VariableDeclaration)
    //
    //	LexicalBinding : BindingIdentifier Initializer
    //	  ...
    //	  3. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //	     a. Let _value_ be ? NamedEvaluation of |Initializer| with argument _bindingId_.
    //	  ...
    //
    // 14.3.2.1 RS: Evaluation (VariableDeclaration)
    //
    //	VariableDeclaration : BindingIdentifier Initializer
    //	  ...
    //	  3. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //	     a. Let _value_ be ? NamedEvaluation of |Initializer| with argument _bindingId_.
    //	  ...
    //
    // 8.6.3 RS: IteratorBindingInitialization (BindingElement)
    //
    //	SingleNameBinding : BindingIdentifier Initializer?
    //	  ...
    //	  5. If |Initializer| is present and _v_ is *undefined*, then
    //	     a. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //	        i. Set _v_ to ? NamedEvaluation of |Initializer| with argument _bindingId_.
    //	  ...
    //
    // 14.3.3.3 RS: KeyedBindingInitialization (BindingElement)
    //
    //	SingleNameBinding : BindingIdentifier Initializer?
    //	  ...
    //	  4. If |Initializer| is present and _v_ is *undefined*, then
    //	     a. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //	        i. Set _v_ to ? NamedEvaluation of |Initializer| with argument _bindingId_.
    //	  ...
    private Node? VisitNamedEvaluationSite(Node node, Node classExpr)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, IsAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, CanIgnoreEmptyStringLiteralInAssignedName(classExpr), "");
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:1877-1879 — isAnonymousClassNeedingAssignedName.
    private static bool IsAnonymousClassNeedingAssignedName(Node node)
    {
        return NodeTests.IsClassExpression(node) && node.Name() == null && IsDecoratedClassLike(node);
    }

    // esdecorator.go:1881-1891 — canIgnoreEmptyStringLiteralInAssignedName.
    //
    // The IIFE produced for `(@dec class {})` will result in an assigned name of the form
    // `var class_1 = class { };`, and thus the empty string cannot be ignored. However, The IIFE
    // produced for `(class { @dec x; })` will not result in an assigned name since it
    // transforms to `return class { };`, and thus the empty string *can* be ignored.
    private static bool CanIgnoreEmptyStringLiteralInAssignedName(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        var innerExpression = AstUtilities.SkipOuterExpressions(node, OuterExpressionKinds.All);
        return NodeTests.IsClassExpression(innerExpression) && innerExpression.Name() == null && !AstUtilities.ClassOrConstructorParameterIsDecorated(false, innerExpression);
    }

    // esdecorator.go:1893-1903
    private Node? VisitForStatement(Node node)
    {
        var f = Factory();
        var forStmt = node.AsForStatement();
        return f.UpdateForStatement(
            forStmt,
            discardedVisitor.VisitNode(forStmt.Initializer),
            Visitor().VisitNode(forStmt.Condition),
            discardedVisitor.VisitNode(forStmt.Incrementor),
            EmitContext().VisitIterationBody(forStmt.Statement, Visitor()));
    }

    // esdecorator.go:1905-1907
    private Node? VisitExpressionStatement(Node node)
    {
        return discardedVisitor.VisitEachChild(node);
    }

    // esdecorator.go:1909-2017
    private Node? VisitBinaryExpression(Node node, bool discarded)
    {
        var f = Factory();
        var ec = EmitContext();
        var bin = node.AsBinaryExpression();

        if (AstUtilities.IsDestructuringAssignment(node))
        {
            var left = VisitAssignmentPattern(bin.Left!);
            var right = Visitor().VisitNode(bin.Right);
            return f.UpdateBinaryExpression(bin, null, left, null, bin.OperatorToken, right);
        }

        if (AstUtilities.IsAssignmentExpression(node, false))
        {
            // 13.15.2 RS: Evaluation
            //   AssignmentExpression : LeftHandSideExpression `=` AssignmentExpression
            //     1. If |LeftHandSideExpression| is neither an |ObjectLiteral| nor an |ArrayLiteral|, then
            //        a. Let _lref_ be ? Evaluation of |LeftHandSideExpression|.
            //        b. If IsAnonymousFunctionDefinition(|AssignmentExpression|) and IsIdentifierRef of |LeftHandSideExpression| are both *true*, then
            //           i. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...
            //
            //   AssignmentExpression : LeftHandSideExpression `&&=` AssignmentExpression
            //     ...
            //     5. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and IsIdentifierRef of |LeftHandSideExpression| is *true*, then
            //        a. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...
            //
            //   AssignmentExpression : LeftHandSideExpression `||=` AssignmentExpression
            //     ...
            //     5. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and IsIdentifierRef of |LeftHandSideExpression| is *true*, then
            //        a. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...
            //
            //   AssignmentExpression : LeftHandSideExpression `??=` AssignmentExpression
            //     ...
            //     4. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true* and IsIdentifierRef of |LeftHandSideExpression| is *true*, then
            //        a. Let _rval_ be ? NamedEvaluation of |AssignmentExpression| with argument _lref_.[[ReferencedName]].
            //     ...

            if (NamedEvaluationUtilities.IsNamedEvaluationAnd(ec, node, IsAnonymousClassNeedingAssignedName))
            {
                node = NamedEvaluationUtilities.TransformNamedEvaluation(ec, node, CanIgnoreEmptyStringLiteralInAssignedName(bin.Right), "");
                return Visitor().VisitEachChild(node);
            }

            if (AstUtilities.IsSuperProperty(bin.Left!) && classThis != null && classSuper != null)
            {
                Node? setterName = null;
                if (NodeTests.IsElementAccessExpression(bin.Left!))
                {
                    setterName = Visitor().VisitNode(bin.Left!.AsElementAccessExpression().ArgumentExpression);
                }
                else if (NodeTests.IsPropertyAccessExpression(bin.Left!) && NodeTests.IsIdentifier(bin.Left!.AsPropertyAccessExpression().Name()!))
                {
                    setterName = f.NewStringLiteralFromNode(bin.Left.AsPropertyAccessExpression().Name()!);
                }
                if (setterName != null)
                {
                    // super.x = ...
                    // super.x += ...
                    // super[x] = ...
                    // super[x] += ...
                    var expression = Visitor().VisitNode(bin.Right)!;
                    if (AstUtilities.IsCompoundAssignment(bin.OperatorToken!.Kind))
                    {
                        var getterName = setterName;
                        if (!global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(setterName))
                        {
                            getterName = f.NewTempVariable();
                            ec.AddVariableDeclaration(getterName);
                            setterName = f.NewAssignmentExpression(getterName, setterName);
                        }
                        var superPropertyGet = f.NewReflectGetCall(classSuper, getterName, classThis);
                        ec.SetOriginal(superPropertyGet, bin.Left!);
                        superPropertyGet.Loc = bin.Left!.Loc;
                        expression = f.AsNodeFactory().NewBinaryExpression(
                            null,
                            superPropertyGet,
                            null,
                            f.NewToken(global::Ts.Transformers.Transformers.GetNonAssignmentOperatorForCompoundAssignment(bin.OperatorToken.Kind)),
                            expression);
                        expression.Loc = node.Loc;
                    }
                    Node? temp = null;
                    if (!discarded)
                    {
                        temp = f.NewTempVariable();
                        ec.AddVariableDeclaration(temp);
                    }
                    if (temp != null)
                    {
                        expression = f.NewAssignmentExpression(temp, expression);
                        expression.Loc = node.Loc;
                    }
                    expression = f.NewReflectSetCall(classSuper, setterName, expression, classThis);
                    ec.SetOriginal(expression, node);
                    expression.Loc = node.Loc;
                    if (temp != null)
                    {
                        expression = f.NewCommaExpression(expression, temp);
                        expression.Loc = node.Loc;
                    }
                    return expression;
                }
            }
        }

        if (bin.OperatorToken!.Kind == SyntaxKind.CommaToken)
        {
            var left = discardedVisitor.VisitNode(bin.Left);
            Node? right;
            if (discarded)
            {
                right = discardedVisitor.VisitNode(bin.Right);
            }
            else
            {
                right = Visitor().VisitNode(bin.Right);
            }
            return f.UpdateBinaryExpression(bin, null, left, null, bin.OperatorToken, right);
        }

        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2019-2097
    private Node? VisitPreOrPostfixUnaryExpression(Node node, bool discarded)
    {
        var f = Factory();
        var ec = EmitContext();

        SyntaxKind @operator;
        Node operandNode;
        if (NodeTests.IsPrefixUnaryExpression(node))
        {
            @operator = node.AsPrefixUnaryExpression().Operator;
            operandNode = node.AsPrefixUnaryExpression().Operand!;
        }
        else
        {
            @operator = node.AsPostfixUnaryExpression().Operator;
            operandNode = node.AsPostfixUnaryExpression().Operand!;
        }

        if (@operator == SyntaxKind.PlusPlusToken || @operator == SyntaxKind.MinusMinusToken)
        {
            var operand = AstUtilities.SkipParentheses(operandNode!);
            if (AstUtilities.IsSuperProperty(operand) && classThis != null && classSuper != null)
            {
                Node? setterName = null;
                if (NodeTests.IsElementAccessExpression(operand))
                {
                    setterName = Visitor().VisitNode(operand.AsElementAccessExpression().ArgumentExpression);
                }
                else if (NodeTests.IsPropertyAccessExpression(operand) && NodeTests.IsIdentifier(operand.AsPropertyAccessExpression().Name()!))
                {
                    setterName = f.NewStringLiteralFromNode(operand.AsPropertyAccessExpression().Name()!);
                }
                if (setterName != null)
                {
                    var getterName = setterName;
                    if (!global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(setterName))
                    {
                        getterName = f.NewTempVariable();
                        ec.AddVariableDeclaration(getterName);
                        setterName = f.NewAssignmentExpression(getterName, setterName);
                    }

                    var expression = f.NewReflectGetCall(classSuper, getterName, classThis);
                    ec.SetOriginal(expression, node);
                    expression.Loc = node.Loc;

                    // If the result of this expression is discarded (i.e., it's in a position where the result
                    // will be otherwise unused, such as in an expression statement or the left side of a comma), we
                    // don't need to create an extra temp variable to hold the result:
                    //
                    //  source (discarded):
                    //    super.x++;
                    //  generated:
                    //    _a = Reflect.get(_super, "x"), _a++, Reflect.set(_super, "x", _a);
                    //
                    // Above, the temp variable `_a` is used to perform the correct coercion (i.e., number or
                    // bigint). Since the result of the postfix unary is discarded, we don't need to capture the
                    // result of the expression.
                    //
                    //  source (not discarded):
                    //    y = super.x++;
                    //  generated:
                    //    y = (_a = Reflect.get(_super, "x"), _b = _a++, Reflect.set(_super, "x", _a), _b);
                    //
                    // When the result isn't discarded, we introduce a new temp variable (`_b`) to capture the
                    // result of the operation so that we can provide it to `y` when the assignment is complete.
                    Node? temp = null;
                    if (!discarded)
                    {
                        temp = f.NewTempVariable();
                        ec.AddVariableDeclaration(temp);
                    }

                    expression = ClassFieldsTransformer.ExpandPreOrPostfixIncrementOrDecrementExpression(f, ec, node, expression, temp);

                    expression = f.NewReflectSetCall(classSuper, setterName, expression, classThis);
                    ec.SetOriginal(expression, node);
                    expression.Loc = node.Loc;

                    if (temp != null)
                    {
                        expression = f.NewCommaExpression(expression, temp);
                        expression.Loc = node.Loc;
                    }

                    return expression;
                }
            }
        }

        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2099-2116
    private (Node ReferencedName, Node UpdatedName) VisitReferencedPropertyName(Node node)
    {
        if (AstUtilities.IsPropertyNameLiteral(node) || NodeTests.IsPrivateIdentifier(node))
        {
            return (Factory().NewStringLiteralFromNode(node), Visitor().VisitNode(node)!);
        }

        var cpn = node.AsComputedPropertyName();
        if (AstUtilities.IsPropertyNameLiteral(cpn.Expression!) && !NodeTests.IsIdentifier(cpn.Expression!))
        {
            return (Factory().NewStringLiteralFromNode(cpn.Expression!), Visitor().VisitNode(node)!);
        }

        var referencedName = Factory().NewGeneratedNameForNode(node);
        EmitContext().AddVariableDeclaration(referencedName);

        var key = Factory().NewPropKeyHelper(Visitor().VisitNode(cpn.Expression)!);
        var assignment = Factory().NewAssignmentExpression(referencedName, key);
        var updatedName = Factory().AsNodeFactory().UpdateComputedPropertyName(cpn, InjectPendingExpressions(assignment));
        return (referencedName, updatedName);
    }

    // esdecorator.go:2118-2123
    private Node? VisitPropertyName(Node node)
    {
        if (NodeTests.IsComputedPropertyName(node))
        {
            return VisitComputedPropertyName(node);
        }
        return Visitor().VisitNode(node);
    }

    // esdecorator.go:2125-2132
    private Node? VisitComputedPropertyName(Node node)
    {
        var cpn = node.AsComputedPropertyName();
        var expression = Visitor().VisitNode(cpn.Expression);
        if (!global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(expression!))
        {
            expression = InjectPendingExpressions(expression);
        }
        return Factory().AsNodeFactory().UpdateComputedPropertyName(cpn, expression);
    }

    // esdecorator.go:2134-2166
    private Node? VisitDestructuringAssignmentTarget(Node node)
    {
        if (NodeTests.IsObjectLiteralExpression(node) || NodeTests.IsArrayLiteralExpression(node))
        {
            return VisitAssignmentPattern(node);
        }

        if (AstUtilities.IsSuperProperty(node) && classThis != null && classSuper != null)
        {
            var f = Factory();
            var ec = EmitContext();
            Node? propertyName = null;
            if (NodeTests.IsElementAccessExpression(node))
            {
                propertyName = Visitor().VisitNode(node.AsElementAccessExpression().ArgumentExpression);
            }
            else if (NodeTests.IsPropertyAccessExpression(node) && NodeTests.IsIdentifier(node.AsPropertyAccessExpression().Name()!))
            {
                propertyName = f.NewStringLiteralFromNode(node.AsPropertyAccessExpression().Name()!);
            }
            if (propertyName != null)
            {
                var paramName = f.NewTempVariable();
                var expression = f.NewAssignmentTargetWrapper(
                    paramName,
                    f.NewReflectSetCall(
                        classSuper,
                        propertyName,
                        paramName,
                        classThis));
                ec.SetOriginal(expression, node);
                expression.Loc = node.Loc;
                return expression;
            }
        }

        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2168-2188
    private Node? VisitAssignmentElement(Node node)
    {
        // 13.15.5.5 RS: IteratorDestructuringAssignmentEvaluation
        //   AssignmentElement : DestructuringAssignmentTarget Initializer?
        //     ...
        //     4. If |Initializer| is present and _value_ is *undefined*, then
        //        a. If IsAnonymousFunctionDefinition(|Initializer|) and IsIdentifierRef of |DestructuringAssignmentTarget| are both *true*, then
        //           i. Let _v_ be ? NamedEvaluation of |Initializer| with argument _lref_.[[ReferencedName]].
        //     ...
        if (AstUtilities.IsAssignmentExpression(node, true /*excludeCompoundAssignment*/))
        {
            var f = Factory();
            var bin = node.AsBinaryExpression();
            if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, IsAnonymousClassNeedingAssignedName))
            {
                node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, CanIgnoreEmptyStringLiteralInAssignedName(bin.Right), "");
                bin = node.AsBinaryExpression();
            }
            var assignmentTarget = VisitDestructuringAssignmentTarget(bin.Left!);
            var initializer = Visitor().VisitNode(bin.Right);
            return f.UpdateBinaryExpression(bin, null, assignmentTarget, null, bin.OperatorToken, initializer);
        }
        return VisitDestructuringAssignmentTarget(node);
    }

    // esdecorator.go:2190-2198
    private Node? VisitAssignmentRestElement(Node node)
    {
        var se = node.AsSpreadElement();
        if (AstUtilities.IsLeftHandSideExpression(se.Expression!))
        {
            var f = Factory();
            var expression = VisitDestructuringAssignmentTarget(se.Expression!);
            return f.UpdateSpreadElement(se, expression);
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2200-2209
    private Node? VisitArrayAssignmentElement(Node node)
    {
        Debug.Assert(AstUtilities.IsArrayBindingOrAssignmentElement(node));
        if (NodeTests.IsSpreadElement(node))
        {
            return VisitAssignmentRestElement(node);
        }
        if (!NodeTests.IsOmittedExpression(node))
        {
            return VisitAssignmentElement(node);
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2211-2235 — visitAssignmentPropertyNode.
    //
    // AssignmentProperty : PropertyName `:` AssignmentElement
    // AssignmentElement : DestructuringAssignmentTarget Initializer?
    //
    // 13.15.5.6 RS: KeyedDestructuringAssignmentEvaluation
    //   AssignmentElement : DestructuringAssignmentTarget Initializer?
    //     ...
    //     3. If |Initializer| is present and _v_ is *undefined*, then
    //        a. If IsAnonymousfunctionDefinition(|Initializer|) and IsIdentifierRef of |DestructuringAssignmentTarget| are both *true*, then
    //           i. Let _rhsValue_ be ? NamedEvaluation of |Initializer| with argument _lref_.[[ReferencedName]].
    //     ...
    private Node? VisitAssignmentPropertyNode(Node node)
    {
        var f = Factory();
        var pa = node.AsPropertyAssignment();
        var name = Visitor().VisitNode(pa.Name());
        if (AstUtilities.IsAssignmentExpression(pa.Initializer!, true /*excludeCompoundAssignment*/))
        {
            var assignmentElement = VisitAssignmentElement(pa.Initializer!);
            return f.UpdatePropertyAssignment(pa, null, name, null, null, assignmentElement);
        }
        if (AstUtilities.IsLeftHandSideExpression(pa.Initializer!))
        {
            var assignmentElement = VisitDestructuringAssignmentTarget(pa.Initializer!);
            return f.UpdatePropertyAssignment(pa, null, name, null, null, assignmentElement);
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2237-2251 — visitShorthandAssignmentProperty.
    //
    // AssignmentProperty : IdentifierReference Initializer?
    //
    // 13.15.5.3 RS: PropertyDestructuringAssignmentEvaluation
    //   AssignmentProperty : IdentifierReference Initializer?
    //     ...
    //     4. If |Initializer?| is present and _v_ is *undefined*, then
    //        a. If IsAnonymousFunctionDefinition(|Initializer|) is *true*, then
    //           i. Set _v_ to ? NamedEvaluation of |Initializer| with argument _P_.
    //     ...
    private Node? VisitShorthandAssignmentProperty(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, IsAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, CanIgnoreEmptyStringLiteralInAssignedName(node.AsShorthandPropertyAssignment().ObjectAssignmentInitializer), "");
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2253-2261
    private Node? VisitAssignmentRestProperty(Node node)
    {
        var sa = node.AsSpreadAssignment();
        if (AstUtilities.IsLeftHandSideExpression(sa.Expression!))
        {
            var f = Factory();
            var expression = VisitDestructuringAssignmentTarget(sa.Expression!);
            return f.UpdateSpreadAssignment(sa, expression);
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2263-2275
    private Node? VisitObjectAssignmentElement(Node node)
    {
        Debug.Assert(AstUtilities.IsObjectBindingOrAssignmentElement(node));
        if (NodeTests.IsSpreadAssignment(node))
        {
            return VisitAssignmentRestProperty(node);
        }
        if (NodeTests.IsShorthandPropertyAssignment(node))
        {
            return VisitShorthandAssignmentProperty(node);
        }
        if (NodeTests.IsPropertyAssignment(node))
        {
            return VisitAssignmentPropertyNode(node);
        }
        return Visitor().VisitEachChild(node);
    }

    // esdecorator.go:2277-2287
    private Node? VisitAssignmentPattern(Node node)
    {
        var f = Factory();
        if (NodeTests.IsArrayLiteralExpression(node))
        {
            var ale = node.AsArrayLiteralExpression();
            var elements = arrayAssignmentVisitor.VisitNodes(ale.Elements);
            return f.AsNodeFactory().UpdateArrayLiteralExpression(ale, elements, ale.MultiLine);
        }
        var ole = node.AsObjectLiteralExpression();
        var properties = objectAssignmentVisitor.VisitNodes(ole.Properties);
        return f.AsNodeFactory().UpdateObjectLiteralExpression(ole, properties, ole.MultiLine);
    }

    // esdecorator.go:2289-2296
    private Node? VisitExportAssignment(Node node)
    {
        // 16.2.3.7 RS: Evaluation
        //   ExportDeclaration : `export` `default` AssignmentExpression `;`
        //     1. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true*, then
        //        a. Let _value_ be ? NamedEvaluation of |AssignmentExpression| with argument `"default"`.
        //     ...
        return VisitNamedEvaluationSite(node, node.Expression()!);
    }

    // esdecorator.go:2298-2313
    private Node? VisitParenthesizedExpression(Node node, bool discarded)
    {
        // 8.4.5 RS: NamedEvaluation
        //   ParenthesizedExpression : `(` Expression `)`
        //     ...
        //     2. Return ? NamedEvaluation of |Expression| with argument _name_.

        var f = Factory();
        var pe = node.AsParenthesizedExpression();
        Node? expression;
        if (discarded)
        {
            expression = discardedVisitor.VisitNode(pe.Expression);
        }
        else
        {
            expression = Visitor().VisitNode(pe.Expression);
        }
        return f.UpdateParenthesizedExpression(pe, expression);
    }

    // esdecorator.go:2315-2325 — visitPartiallyEmittedExpression.
    private Node? VisitPartiallyEmittedExpression(Node node, bool discarded)
    {
        // Emulates 8.4.5 RS: NamedEvaluation
        var pe = node.AsPartiallyEmittedExpression();
        Node? expression;
        if (discarded)
        {
            expression = discardedVisitor.VisitNode(pe.Expression);
        }
        else
        {
            expression = Visitor().VisitNode(pe.Expression);
        }
        return Factory().UpdatePartiallyEmittedExpression(pe, expression);
    }
}
