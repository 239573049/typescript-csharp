// Ported from tsgo internal/transformers/estransforms/esdecorator.go (lines 1232-1722) —
// member-level transform: partialTransformClassElement, decoration statement routing, and the
// method/accessor/static-block/property class element visitors. Function order follows Go.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers.EsTransforms;

using EF = global::Ts.Printer.EmitFlags;

internal sealed partial class ESDecoratorTransformer
{
    // esdecorator.go:1232-1240 — partialResult.
    private sealed class PartialResult
    {
        internal ModifierList? modifiers;
        internal Node? referencedName;
        internal Node? name;
        internal IdentifierNode? initializersName;
        internal IdentifierNode? extraInitializersName;
        internal IdentifierNode? descriptorName;
        internal IdentifierNode? thisArg;
    }

    // esdecorator.go:1242 — createDescriptorFunc.
    private delegate Node? CreateDescriptorFunc(Node member, ModifierList? modifiers);

    // esdecorator.go:1244-1443
    private PartialResult PartialTransformClassElement(Node member, ClassInfo? ci, CreateDescriptorFunc? createDescriptor)
    {
        var f = Factory();
        var ec = EmitContext();

        if (ci == null)
        {
            var modifiers = modifierVisitor.VisitModifiers(member.Modifiers());
            EnterName();
            var name = VisitPropertyName(member.Name()!);
            ExitName();
            return new PartialResult { modifiers = modifiers, name = name };
        }

        // Member decorators require privileged access to private names. However, computed property
        // evaluation occurs interspersed with decorator evaluation. This means that if we encounter
        // a computed property name we must inline decorator evaluation.

        // Collect decorators for this member. Decorator expressions evaluate outside the class body,
        // so `this` should NOT be replaced with `_classThis`.
        var savedClassThis = classThis;
        classThis = null;
        var memberDecorators = TransformAllDecoratorsOfDeclaration(member.Decorators());
        classThis = savedClassThis;
        var modifiers2 = modifierVisitor.VisitModifiers(member.Modifiers());

        var result = new PartialResult();
        result.modifiers = modifiers2;

        if (memberDecorators.Length > 0)
        {
            var memberDecoratorsName = CreateHelperVariable(member, "decorators");
            var memberDecoratorsArray = f.NewArrayLiteralExpression(
                f.NewNodeList(memberDecorators),
                false);
            var memberDecoratorsAssignment = f.NewAssignmentExpression(memberDecoratorsName, memberDecoratorsArray);
            var mi = new MemberInfo { memberDecoratorsName = memberDecoratorsName };
            ci.memberInfos.Set(member, mi);
            (pendingExpressions ??= new List<Node>()).Add(memberDecoratorsAssignment);

            // 5. Static non-field (method/getter/setter/auto-accessor) element decorators are applied
            // 6. Non-static non-field (method/getter/setter/auto-accessor) element decorators are applied
            // 7. Static field (excl. auto-accessor) element decorators are applied
            // 8. Non-static field (excl. auto-accessor) element decorators are applied

            // Determine decorator kind
            string kind;
            if (NodeTests.IsGetAccessorDeclaration(member))
            {
                kind = "getter";
            }
            else if (NodeTests.IsSetAccessorDeclaration(member))
            {
                kind = "setter";
            }
            else if (NodeTests.IsMethodDeclaration(member))
            {
                kind = "method";
            }
            else if (AstUtilities.IsAutoAccessorPropertyDeclaration(member))
            {
                kind = "accessor";
            }
            else if (NodeTests.IsPropertyDeclaration(member))
            {
                kind = "field";
            }
            else
            {
                kind = "";
                Debug.Fail("Unexpected class element kind.");
            }

            // Determine the property name for the context
            var propertyNameComputed = false;
            Node? propertyNameExpr = null;
            if (member.Name() != null && (NodeTests.IsIdentifier(member.Name()!) || NodeTests.IsPrivateIdentifier(member.Name()!)))
            {
                propertyNameComputed = false;
                propertyNameExpr = member.Name();
            }
            else if (member.Name() != null && AstUtilities.IsPropertyNameLiteral(member.Name()!))
            {
                propertyNameComputed = true;
                propertyNameExpr = f.NewStringLiteralFromNode(member.Name()!);
            }
            else if (member.Name() != null && NodeTests.IsComputedPropertyName(member.Name()!))
            {
                var cpn = member.Name()!.AsComputedPropertyName();
                if (AstUtilities.IsPropertyNameLiteral(cpn.Expression!) && !NodeTests.IsIdentifier(cpn.Expression!))
                {
                    propertyNameComputed = true;
                    propertyNameExpr = f.NewStringLiteralFromNode(cpn.Expression!);
                }
                else
                {
                    EnterName();
                    (result.referencedName, result.name) = VisitReferencedPropertyName(member.Name()!);
                    ExitName();
                    propertyNameComputed = true;
                    propertyNameExpr = result.referencedName;
                }
            }

            var contextObj = f.NewESDecorateClassElementContextObject(
                kind,
                propertyNameComputed,
                propertyNameExpr!,
                AstUtilities.IsStatic(member),
                member.Name() != null && NodeTests.IsPrivateIdentifier(member.Name()!),
                // 15.7.3 CreateDecoratorAccessObject (kind,
                // 2. If _kind_ is ~field~, ~method~, ~accessor~, or ~getter~, then ...
                NodeTests.IsPropertyDeclaration(member) || NodeTests.IsGetAccessorDeclaration(member) || NodeTests.IsMethodDeclaration(member),
                // 3. If _kind_ is ~field~, ~accessor~, or ~setter~, then ...
                NodeTests.IsPropertyDeclaration(member) || NodeTests.IsSetAccessorDeclaration(member),
                ci.metadataReference);

            if (AstUtilities.IsMethodOrAccessor(member))
            {
                // produces (public elements):
                //   __esDecorate(this, null, _static_member_decorators, { kind: "method", name: "...", static: true, private: false, access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(this, null, _member_decorators, { kind: "method", name: "...", static: false, private: false, access: { ... } }, _instanceExtraInitializers);
                //   __esDecorate(this, null, _static_member_decorators, { kind: "getter", name: "...", static: true, private: false, access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(this, null, _member_decorators, { kind: "getter", name: "...", static: false, private: false, access: { ... } }, _instanceExtraInitializers);
                //   __esDecorate(this, null, _static_member_decorators, { kind: "setter", name: "...", static: true, private: false, access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(this, null, _member_decorators, { kind: "setter", name: "...", static: false, private: false, access: { ... } }, _instanceExtraInitializers);
                //
                // produces (private elements):
                //   __esDecorate(this, _static_member_descriptor = { value() { ... } }, _static_member_decorators, { kind: "method", name: "...", static: true, private: true, access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(this, _member_descriptor = { value() { ... } }, _member_decorators, { kind: "method", name: "...", static: false, private: true, access: { ... } }, _instanceExtraInitializers);
                //   __esDecorate(this, _static_member_descriptor = { get() { ... } }, _static_member_decorators, { kind: "getter", name: "...", static: true, private: true, access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(this, _member_descriptor = { get() { ... } }, _member_decorators, { kind: "getter", name: "...", static: false, private: true, access: { ... } }, _instanceExtraInitializers);
                //   __esDecorate(this, _static_member_descriptor = { set() { ... } }, _static_member_decorators, { kind: "setter", name: "...", static: true, private: true, access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(this, _member_descriptor = { set() { ... } }, _member_decorators, { kind: "setter", name: "...", static: false, private: true, access: { ... } }, _instanceExtraInitializers);
                var methodExtraInitializersName = ci.instanceMethodExtraInitializersName;
                if (AstUtilities.IsStatic(member))
                {
                    methodExtraInitializersName = ci.staticMethodExtraInitializersName;
                }
                Debug.Assert(methodExtraInitializersName != null, "methodExtraInitializersName should be defined");

                Node? descriptorArg;
                if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(member) && createDescriptor != null)
                {
                    // For private members, extract the method/accessor body into a descriptor object.
                    // Filter modifiers to only keep async.
                    var asyncMods = asyncOnlyModifierVisitor.VisitModifiers(modifiers2);
                    var descriptor = createDescriptor(member, asyncMods)!;
                    mi.memberDescriptorName = CreateHelperVariable(member, "descriptor");
                    result.descriptorName = mi.memberDescriptorName;
                    descriptorArg = f.NewAssignmentExpression(mi.memberDescriptorName, descriptor!);
                }
                else
                {
                    descriptorArg = f.NewToken(SyntaxKind.NullKeyword);
                }

                var esDecorateExpr = f.NewESDecorateHelper(
                    f.NewThisExpression(),
                    descriptorArg,
                    memberDecoratorsName,
                    contextObj,
                    f.NewToken(SyntaxKind.NullKeyword),
                    methodExtraInitializersName!);
                var esDecorateStatement = f.NewExpressionStatement(esDecorateExpr);
                ec.SetSourceMapRange(esDecorateStatement, global::Ts.Transformers.Transformers.MoveRangePastDecorators(member));
                AppendDecorationStatement(ci, member, esDecorateStatement);
            }
            else if (NodeTests.IsPropertyDeclaration(member))
            {
                mi.memberInitializersName = CreateHelperVariable(member, "initializers");
                mi.memberExtraInitializersName = CreateHelperVariable(member, "extraInitializers");
                result.initializersName = mi.memberInitializersName;
                result.extraInitializersName = mi.memberExtraInitializersName;
                if (AstUtilities.IsStatic(member))
                {
                    result.thisArg = ci.classThis;
                }

                Node ctorArg;
                if (AstUtilities.IsAutoAccessorPropertyDeclaration(member))
                {
                    ctorArg = f.NewThisExpression();
                }
                else
                {
                    ctorArg = f.NewToken(SyntaxKind.NullKeyword);
                }

                Node? descriptorArg;
                if (AstUtilities.IsPrivateIdentifierClassElementDeclaration(member) && AstUtilities.HasAccessorModifier(member) && createDescriptor != null)
                {
                    var descriptor = createDescriptor(member, null);
                    mi.memberDescriptorName = CreateHelperVariable(member, "descriptor");
                    result.descriptorName = mi.memberDescriptorName;
                    descriptorArg = f.NewAssignmentExpression(mi.memberDescriptorName, descriptor!);
                }
                else
                {
                    descriptorArg = f.NewToken(SyntaxKind.NullKeyword);
                }

                // produces:
                //   __esDecorate(null, null, _static_member_decorators, { kind: "field", name: "...", static: true, private: ..., access: { ... } }, _staticExtraInitializers);
                //   __esDecorate(null, null, _member_decorators, { kind: "field", name: "...", static: false, private: ..., access: { ... } }, _instanceExtraInitializers);
                var esDecorateExpr = f.NewESDecorateHelper(
                    ctorArg,
                    descriptorArg,
                    memberDecoratorsName,
                    contextObj,
                    mi.memberInitializersName,
                    mi.memberExtraInitializersName);
                var esDecorateStatement = f.NewExpressionStatement(esDecorateExpr);
                ec.SetSourceMapRange(esDecorateStatement, global::Ts.Transformers.Transformers.MoveRangePastDecorators(member));
                AppendDecorationStatement(ci, member, esDecorateStatement);
            }
        }

        if (result.name == null)
        {
            EnterName();
            result.name = VisitPropertyName(member.Name()!);
            ExitName();
        }

        if ((modifiers2 == null || modifiers2.Nodes.Length == 0) && (NodeTests.IsMethodDeclaration(member) || NodeTests.IsPropertyDeclaration(member)))
        {
            // Don't emit leading comments on the name for methods and properties without modifiers, otherwise we
            // will end up printing duplicate comments.
            ec.SetEmitFlags(result.name!, EF.NoLeadingComments);
        }

        return result;
    }

    // esdecorator.go:1445-1463 — appendDecorationStatement appends an __esDecorate statement to the
    // appropriate decoration statement list on classInfo based on the member's kind and static-ness.
    private void AppendDecorationStatement(ClassInfo ci, Node member, Node stmt)
    {
        if (AstUtilities.IsMethodOrAccessor(member) || AstUtilities.IsAutoAccessorPropertyDeclaration(member))
        {
            if (AstUtilities.IsStatic(member))
            {
                ci.staticNonFieldDecorationStatements.Add(stmt);
            }
            else
            {
                ci.nonStaticNonFieldDecorationStatements.Add(stmt);
            }
        }
        else if (NodeTests.IsPropertyDeclaration(member) && !AstUtilities.IsAutoAccessorPropertyDeclaration(member))
        {
            if (AstUtilities.IsStatic(member))
            {
                ci.staticFieldDecorationStatements.Add(stmt);
            }
            else
            {
                ci.nonStaticFieldDecorationStatements.Add(stmt);
            }
        }
        else
        {
            Debug.Fail("Unexpected class element kind.");
        }
    }

    // esdecorator.go:1465-1480
    private Node? VisitMethodDeclaration(Node node)
    {
        EnterClassElement(node);
        var result = PartialTransformClassElement(node, classInfoStack, CreateMethodDescriptorObject);
        if (result.descriptorName != null)
        {
            ExitClassElement();
            return FinishClassElement(CreateMethodDescriptorForwarder(result.modifiers, result.name!, result.descriptorName)!, node);
        }
        var parameters = Visitor().VisitNodes(node.ParameterList());
        var body = Visitor().VisitNode(node.Body());
        ExitClassElement();
        var method = node.AsMethodDeclaration();
        return FinishClassElement(
            Factory().AsNodeFactory().UpdateMethodDeclaration(method, result.modifiers, method.AsteriskToken, result.name, null, null, parameters, null, null, body),
            node);
    }

    // esdecorator.go:1482-1497
    private Node? VisitGetAccessorDeclaration(Node node)
    {
        EnterClassElement(node);
        var result = PartialTransformClassElement(node, classInfoStack, CreateGetAccessorDescriptorObject);
        if (result.descriptorName != null)
        {
            ExitClassElement();
            return FinishClassElement(CreateGetAccessorDescriptorForwarder(result.modifiers, result.name!, result.descriptorName)!, node);
        }
        var parameters = Visitor().VisitNodes(node.ParameterList());
        var body = Visitor().VisitNode(node.Body());
        ExitClassElement();
        var accessor = node.AsGetAccessorDeclaration();
        return FinishClassElement(
            Factory().AsNodeFactory().UpdateGetAccessorDeclaration(accessor, result.modifiers, result.name, null, parameters, null, null, body),
            node);
    }

    // esdecorator.go:1499-1514
    private Node? VisitSetAccessorDeclaration(Node node)
    {
        EnterClassElement(node);
        var result = PartialTransformClassElement(node, classInfoStack, CreateSetAccessorDescriptorObject);
        if (result.descriptorName != null)
        {
            ExitClassElement();
            return FinishClassElement(CreateSetAccessorDescriptorForwarder(result.modifiers, result.name!, result.descriptorName)!, node);
        }
        var parameters = Visitor().VisitNodes(node.ParameterList());
        var body = Visitor().VisitNode(node.Body());
        ExitClassElement();
        var accessor = node.AsSetAccessorDeclaration();
        return FinishClassElement(
            Factory().AsNodeFactory().UpdateSetAccessorDeclaration(accessor, result.modifiers, result.name, null, parameters, null, null, body),
            node);
    }

    // esdecorator.go:1516-1572
    private Node? VisitClassStaticBlockDeclaration(Node node)
    {
        EnterClassElement(node);
        var f = Factory();

        Node? result;
        if (NamedEvaluationUtilities.IsClassNamedEvaluationHelperBlock(EmitContext(), node))
        {
            result = Visitor().VisitEachChild(node);
            // Transfer AssignedName metadata to the new node so isClassNamedEvaluationHelperBlock
            // can still find it after visiting (visiting may create a new node when this->_classThis)
            var assignedName = EmitContext().AssignedName(node);
            if (assignedName != null && result != node)
            {
                EmitContext().SetAssignedName(result!, assignedName);
            }
        }
        else if (ClassThisUtilities.IsClassThisAssignmentBlock(EmitContext(), node))
        {
            var savedClassThis = classThis;
            classThis = null;
            result = Visitor().VisitEachChild(node);
            classThis = savedClassThis;
        }
        else
        {
            // Use a nested variable environment so temp vars generated during static block
            // content transformation (e.g., super access temps) stay scoped to the static block.
            var ec = EmitContext();
            ec.StartVariableEnvironment();
            result = Visitor().VisitEachChild(node);
            var varStatements = ec.EndVariableEnvironment();
            if (varStatements.Length > 0)
            {
                // Inject var declarations at the start of the static block's body
                var blockBody = result!.AsClassStaticBlockDeclaration().Body!.AsBlock();
                var newStmts = new List<Node>(varStatements.Length + blockBody.Statements!.Nodes.Length);
                newStmts.AddRange(varStatements);
                newStmts.AddRange(blockBody.Statements!.Nodes);
                result = f.AsNodeFactory().NewClassStaticBlockDeclaration(null, f.NewBlock(f.NewNodeList(newStmts.ToArray()), blockBody.MultiLine));
            }
            if (classInfoStack != null)
            {
                classInfoStack.hasStaticInitializers = true;
                if ((classInfoStack.pendingStaticInitializers?.Count ?? 0) > 0)
                {
                    // If we tried to inject the pending initializers into the current block, we might run into
                    // variable name collisions due to sharing this blocks scope. To avoid this, we inject a new
                    // static block that contains the pending initializers that precedes this block.
                    var stmts = new List<Node>();
                    foreach (var init in classInfoStack.pendingStaticInitializers!)
                    {
                        var initStmt = f.NewExpressionStatement(init);
                        EmitContext().SetSourceMapRange(initStmt, EmitContext().SourceMapRange(init));
                        stmts.Add(initStmt);
                    }
                    var body = f.NewBlock(f.NewNodeList(stmts.ToArray()), true);
                    var staticBlock = f.AsNodeFactory().NewClassStaticBlockDeclaration(null, body);
                    classInfoStack.pendingStaticInitializers = null;
                    // Return both the new static block and the original
                    ExitClassElement();
                    return global::Ts.Transformers.Transformers.SingleOrMany([staticBlock, result!], f);
                }
            }
        }

        ExitClassElement();
        return result;
    }

    // esdecorator.go:1574-1722
    private Node? VisitPropertyDeclaration(Node node)
    {
        if (NamedEvaluationUtilities.IsNamedEvaluationAnd(EmitContext(), node, IsAnonymousClassNeedingAssignedName))
        {
            node = NamedEvaluationUtilities.TransformNamedEvaluation(EmitContext(), node, CanIgnoreEmptyStringLiteralInAssignedName(node.Initializer()), "");
        }

        EnterClassElement(node);

        // TODO(rbuckton): We support decorating `declare x` fields with legacyDecorators, but we currently don't
        //                 support them with esDecorators. We need to consider whether we will support them in the
        //                 future, and how. For now, these should be elided by the `ts` transform.
        Debug.Assert(!AstUtilities.HasSyntacticModifier(node, ModifierFlags.Ambient), "Not yet implemented.");

        // 10.2.1.3 RS: EvaluateBody
        //   Initializer : `=` AssignmentExpression
        //     ...
        //     3. If IsAnonymousFunctionDefinition(|AssignmentExpression|) is *true*, then
        //        a. Let _value_ be ? NamedEvaluation of |Initializer| with argument _functionObject_.[[ClassFieldInitializerName]].
        //     ...

        var f = Factory();
        var ec = EmitContext();

        CreateDescriptorFunc? createDescriptor = null;
        if (AstUtilities.HasAccessorModifier(node))
        {
            createDescriptor = CreateAccessorPropertyDescriptorObject;
        }
        var result = PartialTransformClassElement(node, classInfoStack, createDescriptor);

        ec.StartVariableEnvironment();

        var initializer = Visitor().VisitNode(node.Initializer());
        if (result.initializersName != null)
        {
            Node thisArg;
            if (result.thisArg != null)
            {
                thisArg = result.thisArg;
            }
            else
            {
                thisArg = f.NewThisExpression();
            }
            initializer ??= f.NewVoidZeroExpression();
            initializer = f.NewRunInitializersHelper(thisArg, result.initializersName, initializer);
        }

        if (AstUtilities.IsStatic(node) && classInfoStack != null && initializer != null)
        {
            classInfoStack.hasStaticInitializers = true;
        }

        var declarations = ec.EndVariableEnvironment();
        if (declarations.Length > 0)
        {
            var stmts = new Node[declarations.Length + 1];
            Array.Copy(declarations, stmts, declarations.Length);
            stmts[declarations.Length] = f.NewReturnStatement(initializer);
            initializer = f.NewImmediatelyInvokedArrowFunction(stmts);
        }

        if (classInfoStack != null)
        {
            if (AstUtilities.IsStatic(node))
            {
                initializer = InjectPendingInitializers(classInfoStack, isStatic: true, initializer);
                if (result.extraInitializersName != null)
                {
                    Node thisArg;
                    if (classInfoStack.classThis != null)
                    {
                        thisArg = classInfoStack.classThis;
                    }
                    else
                    {
                        thisArg = f.NewThisExpression();
                    }
                    (classInfoStack.pendingStaticInitializers ??= new List<Node>()).Add(
                        f.NewRunInitializersHelper(thisArg, result.extraInitializersName, null));
                }
            }
            else
            {
                initializer = InjectPendingInitializers(classInfoStack, isStatic: false, initializer);
                if (result.extraInitializersName != null)
                {
                    (classInfoStack.pendingInstanceInitializers ??= new List<Node>()).Add(
                        f.NewRunInitializersHelper(f.NewThisExpression(), result.extraInitializersName, null));
                }
            }
        }

        ExitClassElement();

        if (AstUtilities.HasAccessorModifier(node) && result.descriptorName != null)
        {
            // given:
            //  accessor #x = 1;
            //
            // emits:
            //  static {
            //      _esDecorate(null, _private_x_descriptor = { get() { return this.#x_1; }, set(value) { this.#x_1 = value; } }, ...)
            //  }
            //  ...
            //  #x_1 = 1;
            //  get #x() { return _private_x_descriptor.get.call(this); }
            //  set #x(value) { _private_x_descriptor.set.call(this, value); }

            var commentRange = ec.CommentRange(node);
            var sourceMapRange = ec.SourceMapRange(node);

            // Since we're creating two declarations where there was previously one, cache
            // the expression for any computed property names.
            var propName = node.Name();
            var getterName = result.name;
            var setterName = result.name;
            if (NodeTests.IsComputedPropertyName(propName!) && !global::Ts.Transformers.Transformers.IsSimpleInlineableExpression(propName!.Expression()!))
            {
                var cacheAssignment = ClassFieldsTransformer.FindComputedPropertyNameCacheAssignment(ec, propName!);
                if (cacheAssignment != null)
                {
                    getterName = f.AsNodeFactory().UpdateComputedPropertyName(propName!.AsComputedPropertyName(), Visitor().VisitNode(propName.Expression()!));
                    setterName = f.AsNodeFactory().UpdateComputedPropertyName(propName!.AsComputedPropertyName(), cacheAssignment.Left!);
                }
                else
                {
                    var temp = f.NewTempVariable();
                    ec.SetSourceMapRange(temp, propName!.Expression()!.Loc);
                    ec.AddVariableDeclaration(temp);
                    var expression = Visitor().VisitNode(propName!.Expression()!);
                    var assignment = f.NewAssignmentExpression(temp, expression!);
                    ec.SetSourceMapRange(assignment, propName!.Expression()!.Loc);
                    getterName = f.AsNodeFactory().UpdateComputedPropertyName(propName!.AsComputedPropertyName(), assignment);
                    setterName = f.AsNodeFactory().UpdateComputedPropertyName(propName!.AsComputedPropertyName(), temp);
                }
            }

            var modifiersWithoutAccessor = accessorStrippingModifierVisitor.VisitModifiers(result.modifiers);

            var backingField = EsTransformUtilities.CreateAccessorPropertyBackingField(f, node.AsPropertyDeclaration(), modifiersWithoutAccessor, initializer);
            ec.SetOriginal(backingField, node);
            ec.SetEmitFlags(backingField, EF.NoComments);
            ec.SetSourceMapRange(backingField, sourceMapRange);
            ec.SetSourceMapRange(backingField.AsPropertyDeclaration().Name()!, ec.SourceMapRange(node.Name()!));

            var getter = CreateGetAccessorDescriptorForwarder(modifiersWithoutAccessor, getterName!, result.descriptorName);
            ec.SetOriginal(getter!, node);
            ec.SetCommentRange(getter!, commentRange);
            ec.SetSourceMapRange(getter!, sourceMapRange);

            var setter = CreateSetAccessorDescriptorForwarder(modifiersWithoutAccessor, setterName!, result.descriptorName);
            ec.SetOriginal(setter!, node);
            ec.SetEmitFlags(setter!, EF.NoComments);
            ec.SetSourceMapRange(setter!, sourceMapRange);

            return global::Ts.Transformers.Transformers.SingleOrMany([backingField, getter!, setter!], f);
        }

        var prop = node.AsPropertyDeclaration();
        return FinishClassElement(
            f.AsNodeFactory().UpdatePropertyDeclaration(prop, result.modifiers, result.name, null, null, initializer),
            node);
    }
}
