// Ported from tsgo internal/transformers/estransforms/utilities.go.

using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Transformers.EsTransforms;

internal static class EsTransformUtilities
{
    internal static Expression ConvertClassDeclarationToClassExpression(
        global::Ts.Printer.EmitContext emitContext,
        ClassDeclaration node)
    {
        var updated = emitContext.Factory.AsNodeFactory().NewClassExpression(
            global::Ts.Transformers.Transformers.ExtractModifiers(
                emitContext,
                node.Modifiers(),
                ~(ModifierFlags.Export | ModifierFlags.Default)),
            node.Name(),
            node.TypeParameters,
            node.HeritageClauses,
            node.Members);
        emitContext.SetOriginal(updated, node.AsNode());
        updated.Loc = node.Loc;
        return updated;
    }

    internal static Node CreateNotNullCondition(
        global::Ts.Printer.EmitContext emitContext,
        Node left,
        Node right,
        bool invert)
    {
        var token = SyntaxKind.ExclamationEqualsEqualsToken;
        var @operator = SyntaxKind.AmpersandAmpersandToken;
        if (invert)
        {
            token = SyntaxKind.EqualsEqualsEqualsToken;
            @operator = SyntaxKind.BarBarToken;
        }

        return emitContext.Factory.NewBinaryExpression(
            null,
            emitContext.Factory.NewBinaryExpression(
                null,
                left,
                null,
                emitContext.Factory.NewToken(token),
                emitContext.Factory.NewKeywordExpression(SyntaxKind.NullKeyword)),
            null,
            emitContext.Factory.NewToken(@operator),
            emitContext.Factory.NewBinaryExpression(
                null,
                right,
                null,
                emitContext.Factory.NewToken(token),
                emitContext.Factory.NewVoidZeroExpression()));
    }

    // estransforms/utilities.go:280 — createAccessorPropertyBackingField creates a private backing
    // field for an `accessor` PropertyDeclaration.
    internal static Node CreateAccessorPropertyBackingField(global::Ts.Printer.NodeFactory f, PropertyDeclaration node, ModifierList? modifiers, Expression? initializer)
    {
        return f.AsNodeFactory().UpdatePropertyDeclaration(
            node,
            modifiers,
            f.NewGeneratedPrivateNameForNodeEx(node.Name()!, new global::Ts.Printer.AutoGenerateOptions { Suffix = "_accessor_storage" }),
            null, /*postfixToken*/
            null, /*typeNode*/
            initializer);
    }

    // estransforms/utilities.go:910 (async.go) — see AsyncTransformer for the users.
}

// utilities.go:52-59 — superAccessState tracks super property/element accesses and super property
// assignments within async function or async generator bodies. It is embedded by both asyncTransformer
// and forawaitTransformer to share the tracking logic. (Go struct embedding → composition: the
// transformers hold an instance and delegate.)
internal sealed class SuperAccessState
{
    private global::Ts.Printer.NodeFactory factory = null!;

    // Keeps track of property names accessed on super (`super.x`) within async functions.
    internal global::Ts.Collections.OrderedSet<string>? capturedSuperProperties;
    // Whether the async function contains an element access on super (`super[x]`).
    internal bool hasSuperElementAccess;
    internal bool hasSuperPropertyAssignment;

    internal IdentifierNode? superBinding;
    internal IdentifierNode? superIndexBinding;
    internal NodeVisitor superAccessVisitor = null!;

    // utilities.go:69
    internal void InitSuperAccessVisitor(global::Ts.Printer.EmitContext emitContext, global::Ts.Printer.NodeFactory factory)
    {
        this.factory = factory;
        superAccessVisitor = emitContext.NewNodeVisitor(VisitSuperAccessNode);
    }

    // utilities.go:74-109 — visitSuperAccessNode walks the async/generator body and replaces super
    // property/element accesses with _super/_superIndex references. This is necessary because the
    // async body ends up inside a generator function where `super` is not valid.
    private Node? VisitSuperAccessNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.CallExpression:
                var call = node.AsCallExpression();
                if (AstUtilities.IsSuperProperty(call.Expression!))
                {
                    return SubstituteCallExpressionWithSuperAccess(call, superAccessVisitor);
                }
                return superAccessVisitor.VisitEachChild(node);
            case SyntaxKind.PropertyAccessExpression:
                if (node.Expression()!.Kind == SyntaxKind.SuperKeyword)
                {
                    // super.x → _super.x
                    return factory.NewPropertyAccessExpression(
                        superBinding!, null, node.Name()!, NodeFlags.None);
                }
                return superAccessVisitor.VisitEachChild(node);
            case SyntaxKind.ElementAccessExpression:
                if (node.Expression()!.Kind == SyntaxKind.SuperKeyword)
                {
                    // super[x] → _superIndex(x) or _superIndex(x).value
                    return CreateSuperElementAccessInAsyncMethod(
                        node.AsElementAccessExpression().ArgumentExpression!);
                }
                return superAccessVisitor.VisitEachChild(node);
            // Don't recurse into non-arrow function scopes or classes
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.Constructor:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return node;
            default:
                return superAccessVisitor.VisitEachChild(node);
        }
    }

    // utilities.go:111
    internal Node? SubstituteSuperAccessesInBody(Node body) => superAccessVisitor.VisitNode(body);

    // utilities.go:115-155 — substituteCallExpressionWithSuperAccess handles super.x(args) and super[x](args).
    private Node? SubstituteCallExpressionWithSuperAccess(CallExpression call, NodeVisitor visitor)
    {
        var expression = call.Expression!;
        Node target;

        if (NodeTests.IsPropertyAccessExpression(expression))
        {
            // super.x(args) → _super.x.call(this, args)
            target = factory.NewPropertyAccessExpression(
                superBinding!, null,
                expression.AsPropertyAccessExpression().Name()!, NodeFlags.None);
        }
        else if (NodeTests.IsElementAccessExpression(expression))
        {
            // super[x](args) → _superIndex(x).call(this, args) or _superIndex(x).value.call(this, args)
            target = CreateSuperElementAccessInAsyncMethod(
                expression.AsElementAccessExpression().ArgumentExpression!);
        }
        else
        {
            return visitor.VisitEachChild(call.AsNode());
        }

        var callTarget = factory.NewPropertyAccessExpression(
            target, null,
            factory.NewIdentifier("call"), NodeFlags.None);

        var allArgs = new List<Node> { factory.NewThisExpression() };
        if (call.Arguments != null)
        {
            var visitedArgs = visitor.VisitNodes(call.Arguments);
            if (visitedArgs != null)
            {
                allArgs.AddRange(visitedArgs.Nodes);
            }
        }

        var result = factory.NewCallExpression(
            callTarget, null, null,
            factory.NewNodeList(allArgs.ToArray()), NodeFlags.None);
        result.Loc = call.Loc;
        return result;
    }

    // utilities.go:157-171 — createSuperElementAccessInAsyncMethod creates _superIndex(x) or _superIndex(x).value.
    internal Node CreateSuperElementAccessInAsyncMethod(Node argumentExpression)
    {
        var superIndexCall = factory.NewCallExpression(
            superIndexBinding!, null, null,
            factory.NewNodeList([argumentExpression]),
            NodeFlags.None);
        if (hasSuperPropertyAssignment)
        {
            return factory.NewPropertyAccessExpression(
                superIndexCall, null,
                factory.NewIdentifier("value"), NodeFlags.None);
        }
        return superIndexCall;
    }

    // utilities.go:173-245 — createSuperAccessVariableStatement creates a variable named `_super` with
    // accessor properties for the given property names.
    //
    // Create a variable declaration with a getter/setter (if binding) definition for each name:
    //
    //	const _super = Object.create(null, {
    //	    x: { get: () => super.x },                           // read-only
    //	    x: { get: () => super.x, set: (v) => super.x = v }, // read-write
    //	});
    internal Node CreateSuperAccessVariableStatement()
    {
        var f = factory;
        var accessors = new List<Node>();

        foreach (var name in capturedSuperProperties!.Values())
        {
            var descriptorProperties = new List<Node>();

            // getter: get: () => super.name
            var getterBody = f.NewPropertyAccessExpression(
                f.NewKeywordExpression(SyntaxKind.SuperKeyword), null,
                f.NewIdentifier(name), NodeFlags.None);
            var getterArrow = f.NewArrowFunction(
                null, null,
                f.NewNodeList([]),
                null, null,
                f.NewToken(SyntaxKind.EqualsGreaterThanToken),
                getterBody);
            var getter = f.NewPropertyAssignment(null, f.NewIdentifier("get"), null, null, getterArrow);
            descriptorProperties.Add(getter);

            if (hasSuperPropertyAssignment)
            {
                // setter: set: v => super.name = v
                var vParam = f.NewParameterDeclaration(null, null, f.NewIdentifier("v"), null, null, null);
                var superProp = f.NewPropertyAccessExpression(
                    f.NewKeywordExpression(SyntaxKind.SuperKeyword), null,
                    f.NewIdentifier(name), NodeFlags.None);
                var assignExpr = f.NewAssignmentExpression(superProp, f.NewIdentifier("v"));
                var setterArrow = f.NewArrowFunction(
                    null, null,
                    f.NewNodeList([vParam]),
                    null, null,
                    f.NewToken(SyntaxKind.EqualsGreaterThanToken),
                    assignExpr);
                var setter = f.NewPropertyAssignment(null, f.NewIdentifier("set"), null, null, setterArrow);
                descriptorProperties.Add(setter);
            }

            var descriptor = f.NewObjectLiteralExpression(f.NewNodeList(descriptorProperties.ToArray()), false);
            var accessor = f.NewPropertyAssignment(null, f.NewIdentifier(name), null, null, descriptor);
            accessors.Add(accessor);
        }

        var descriptorsObject = f.NewObjectLiteralExpression(f.NewNodeList(accessors.ToArray()), true);

        var objectCreateCall = f.NewCallExpression(
            f.NewPropertyAccessExpression(
                f.NewIdentifier("Object"), null,
                f.NewIdentifier("create"), NodeFlags.None),
            null, null,
            f.NewNodeList([
                f.NewKeywordExpression(SyntaxKind.NullKeyword),
                descriptorsObject,
            ]),
            NodeFlags.None);

        var decl = f.NewVariableDeclaration(superBinding, null, null, objectCreateCall);
        var declList = f.NewVariableDeclarationList(f.NewNodeList([decl]), NodeFlags.Const);
        return f.NewVariableStatement(null, declList);
    }

    // utilities.go:247-277 — trackSuperAccess records super property/element accesses and super property
    // assignments for the enclosing async method body. Called from both the main visitor and auxiliary
    // visitors to ensure super accesses are tracked regardless of whether the node has transform flags.
    internal void TrackSuperAccess(Node node)
    {
        if (capturedSuperProperties == null)
        {
            return;
        }
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
                if (node.Expression()!.Kind == SyntaxKind.SuperKeyword)
                {
                    capturedSuperProperties.Add(node.Name()!.Text());
                }
                break;
            case SyntaxKind.ElementAccessExpression:
                if (node.Expression()!.Kind == SyntaxKind.SuperKeyword)
                {
                    hasSuperElementAccess = true;
                }
                break;
            case SyntaxKind.BinaryExpression:
                if (NodeTests.IsAssignmentOperator(node.AsBinaryExpression().OperatorToken!.Kind) &&
                    AsyncTransformer.AssignmentTargetContainsSuperProperty(node.AsBinaryExpression().Left!))
                {
                    hasSuperPropertyAssignment = true;
                }
                break;
            case SyntaxKind.PrefixUnaryExpression:
                if (AsyncTransformer.IsUpdateExpression(node) &&
                    AsyncTransformer.AssignmentTargetContainsSuperProperty(node.AsPrefixUnaryExpression().Operand!))
                {
                    hasSuperPropertyAssignment = true;
                }
                break;
            case SyntaxKind.PostfixUnaryExpression:
                if (AsyncTransformer.IsUpdateExpression(node) &&
                    AsyncTransformer.AssignmentTargetContainsSuperProperty(node.AsPostfixUnaryExpression().Operand!))
                {
                    hasSuperPropertyAssignment = true;
                }
                break;
        }
    }
}
