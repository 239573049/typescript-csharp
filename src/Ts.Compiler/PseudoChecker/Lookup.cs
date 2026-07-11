// Ported from tsgo internal/pseudochecker/lookup.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.PseudoChecker;

public sealed partial class PseudoChecker
{
    public PseudoType GetReturnTypeOfSignature(global::Ts.Ast.Node signatureNode)
    {
        switch (signatureNode.Kind)
        {
            case global::Ts.Ast.SyntaxKind.GetAccessor:
                return GetTypeOfAccessor(signatureNode);
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
            case global::Ts.Ast.SyntaxKind.FunctionDeclaration:
            case global::Ts.Ast.SyntaxKind.Constructor:
            case global::Ts.Ast.SyntaxKind.MethodSignature:
            case global::Ts.Ast.SyntaxKind.CallSignature:
            case global::Ts.Ast.SyntaxKind.ConstructSignature:
            case global::Ts.Ast.SyntaxKind.SetAccessor:
            case global::Ts.Ast.SyntaxKind.IndexSignature:
            case global::Ts.Ast.SyntaxKind.FunctionType:
            case global::Ts.Ast.SyntaxKind.ConstructorType:
            case global::Ts.Ast.SyntaxKind.FunctionExpression:
            case global::Ts.Ast.SyntaxKind.ArrowFunction:
            case global::Ts.Ast.SyntaxKind.JSDocSignature:
                return CreateReturnFromSignature(signatureNode);
            default:
                global::Ts.Core.Debug.FailBadSyntaxKind(signatureNode, "Node needs to be an inferrable node");
                return null!;
        }
    }

    public PseudoType GetTypeOfAccessor(global::Ts.Ast.Node accessor)
    {
        return TypeFromAccessor(accessor);
    }

    public PseudoType GetTypeOfExpression(global::Ts.Ast.Node node)
    {
        return TypeFromExpression(node);
    }

    public PseudoType GetTypeOfDeclaration(global::Ts.Ast.Node node)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.Parameter:
                return TypeFromParameter(node.AsParameterDeclaration());
            case global::Ts.Ast.SyntaxKind.VariableDeclaration:
                return TypeFromVariable(node.AsVariableDeclaration());
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
            case global::Ts.Ast.SyntaxKind.JSDocPropertyTag:
                return TypeFromProperty(node);
            case global::Ts.Ast.SyntaxKind.BindingElement:
                return NewPseudoTypeNoResult(node);
            case global::Ts.Ast.SyntaxKind.ExportAssignment:
                return TypeFromExpression(node.AsExportAssignment().Expression!);
            case global::Ts.Ast.SyntaxKind.PropertyAccessExpression:
            case global::Ts.Ast.SyntaxKind.ElementAccessExpression:
            case global::Ts.Ast.SyntaxKind.BinaryExpression:
                return TypeFromExpandoProperty(node);
            case global::Ts.Ast.SyntaxKind.PropertyAssignment:
            case global::Ts.Ast.SyntaxKind.ShorthandPropertyAssignment:
                return TypeFromPropertyAssignment(node);
            case global::Ts.Ast.SyntaxKind.CallExpression:
                switch (AstUtilities.GetAssignmentDeclarationKind(node))
                {
                    // TODO: How much of the checker's getTypeFromPropertyDescriptor is worth trying to emulate over ASTs?
                    case global::Ts.Ast.JSDeclarationKind.ObjectDefinePropertyValue:
                    {
                        // !!!
                        break;
                    }
                    case global::Ts.Ast.JSDeclarationKind.ObjectDefinePropertyExports:
                    {
                        // !!!
                        break;
                    }
                }
                return NewPseudoTypeNoResult(node);
            default:
                global::Ts.Core.Debug.FailBadSyntaxKind(node, "node needs to be an inferrable node");
                return null!;
        }
    }

    private PseudoType TypeFromPropertyAssignment(global::Ts.Ast.Node node)
    {
        var annotation = node.Type();
        if (annotation != null)
        {
            return NewPseudoTypeDirect(annotation);
        }
        if (node.Kind == global::Ts.Ast.SyntaxKind.PropertyAssignment)
        {
            var init = node.Initializer();
            if (init != null)
            {
                var expr = TypeFromExpression(init);
                if (expr != null && (expr.Kind != PseudoTypeKind.Inferred || (expr.AsPseudoTypeInferred().ErrorNodes?.Length ?? 0) > 0))
                {
                    return expr;
                }
                // fallback to NoResult if PseudoTypeKindInferred without error nodes
            }
        }
        return NewPseudoTypeNoResult(node);
    }

    // This is _not_ redundant with the reparser; see how expandoFunctionSymbolProperty.ts and similar behaves
    private PseudoType TypeFromExpandoProperty(global::Ts.Ast.Node node)
    {
        var declaredType = node.Type();
        if (declaredType != null)
        {
            return NewPseudoTypeDirect(declaredType);
        }
        // While `node` is an expression, as an expando, it should also always be a
        // declaration with a `.Symbol()` which requires declaration fallback handling
        return NewPseudoTypeNoResult(node);
    }

    private PseudoType TypeFromProperty(global::Ts.Ast.Node node)
    {
        var t = node.Type();
        if (t != null)
        {
            return NewPseudoTypeDirect(t);
        }
        if (NodeTests.IsPropertyDeclaration(node))
        {
            var init = node.Initializer();
            if (init != null && !IsContextuallyTyped(node))
            {
                // explicit fail on readonly template literals to allow for literal freshness in the future
                if (AstUtilities.HasSyntacticModifier(node, global::Ts.Ast.ModifierFlags.Readonly) && NodeTests.IsTemplateExpression(init))
                {
                    return NewPseudoTypeNoResult(node);
                }
                var expr = TypeFromExpression(init);
                if (expr != null && (expr.Kind != PseudoTypeKind.Inferred || (expr.AsPseudoTypeInferred().ErrorNodes?.Length ?? 0) > 0))
                {
                    if (expr.Kind != PseudoTypeKind.Direct && node.AsPropertyDeclaration().PostfixToken != null && node.AsPropertyDeclaration().PostfixToken!.Kind == global::Ts.Ast.SyntaxKind.QuestionToken)
                    {
                        // type comes from the initializer expression on a property with a `?` - add `| undefined` to the type
                        return AddUndefinedIfDefinitelyRequired(expr);
                    }
                    return expr;
                }
                // fallback to NoResult if PseudoTypeKindInferred without error nodes
            }
        }
        return NewPseudoTypeNoResult(node);
    }

    private PseudoType TypeFromVariable(global::Ts.Ast.VariableDeclaration declaration)
    {
        var t = declaration.Type;
        if (t != null)
        {
            return NewPseudoTypeDirect(t);
        }
        var init = declaration.Initializer;
        if (init != null && (declaration.Symbol!.Declarations!.Length == 1 || CoreUtil.CountWhere(declaration.Symbol.Declarations, NodeTests.IsVariableDeclaration) == 1))
        {
            if (!IsContextuallyTyped(declaration.AsNode())) // TODO: also should bail on expando declarations; reuse syntactic expando check used in declaration emit
            {
                // TODO: Strada forces an inference fallback on `const` variables with template expression initializers, to leave space for template literal freshness in the future
                if (IsVarConst(declaration.AsNode()) && NodeTests.IsTemplateExpression(init))
                {
                    return NewPseudoTypeNoResult(declaration.AsNode());
                }
                var expr = TypeFromExpression(init);
                if (expr != null && (expr.Kind != PseudoTypeKind.Inferred || (expr.AsPseudoTypeInferred().ErrorNodes?.Length ?? 0) > 0))
                {
                    return expr;
                }
                // fallback to NoResult if PseudoTypeKindInferred without error nodes
            }
        }
        return NewPseudoTypeNoResult(declaration.AsNode());
    }

    private PseudoType TypeFromAccessor(global::Ts.Ast.Node accessor)
    {
        var accessorDeclarations = GetAllAccessorDeclarationsForDeclaration(accessor, accessor.DeclarationData()!.Symbol!.Declarations!);
        var accessorType = GetTypeAnnotationFromAllAccessorDeclarations(accessor, accessorDeclarations);
        if (accessorType != null && !NodeTests.IsTypePredicateNode(accessorType))
        {
            return NewPseudoTypeDirect(accessorType);
        }
        if (accessorDeclarations.GetAccessor != null)
        {
            var res = CreateReturnFromSignature(accessorDeclarations.GetAccessor.AsNode());
            if (res.Kind == PseudoTypeKind.Inferred && (res.AsPseudoTypeInferred().ErrorNodes?.Length ?? 0) == 0)
            {
                var errorNodes = new List<global::Ts.Ast.Node> { accessorDeclarations.GetAccessor.AsNode() };
                if (accessorDeclarations.SetAccessor != null)
                {
                    errorNodes.Add(accessorDeclarations.SetAccessor.AsNode());
                }
                res = NewPseudoTypeInferredWithErrors(res.AsPseudoTypeInferred().Expression, res.AsPseudoTypeInferred().IsSignatureReturn, errorNodes.ToArray()); // Move error up to the accessor
            }
            return res;
        }
        return NewPseudoTypeNoResult(accessor);
    }

    private global::Ts.Ast.Node? GetTypeAnnotationFromAllAccessorDeclarations(global::Ts.Ast.Node node, AllAccessorDeclarations accessors)
    {
        var accessorType = GetTypeAnnotationFromAccessor(node);
        if (accessorType == null && node != accessors.FirstAccessor)
        {
            accessorType = GetTypeAnnotationFromAccessor(accessors.FirstAccessor);
        }
        if (accessorType == null && accessors.SecondAccessor != null && node != accessors.SecondAccessor)
        {
            accessorType = GetTypeAnnotationFromAccessor(accessors.SecondAccessor);
        }
        return accessorType;
    }

    private global::Ts.Ast.Node? GetTypeAnnotationFromAccessor(global::Ts.Ast.Node? node)
    {
        if (node == null)
        {
            return null;
        }
        // !!! TODO: support ripping return type off of .FullSignature
        if (node.Kind == global::Ts.Ast.SyntaxKind.GetAccessor)
        {
            return node.AsGetAccessorDeclaration().Type;
        }
        var set = node.AsSetAccessorDeclaration();
        if (set.Parameters == null || set.Parameters.Nodes.Length < 1)
        {
            return null;
        }
        var p = set.Parameters.Nodes[0];
        if (!NodeTests.IsParameterDeclaration(p))
        {
            return null;
        }
        return p.AsParameterDeclaration().Type;
    }

    private static bool IsValueSignatureDeclaration(global::Ts.Ast.Node node)
    {
        return NodeTests.IsFunctionExpression(node) || NodeTests.IsArrowFunction(node) || NodeTests.IsMethodDeclaration(node) || AstUtilities.IsAccessor(node) || NodeTests.IsFunctionDeclaration(node) || NodeTests.IsConstructorDeclaration(node);
    }

    // does not return `nil`, returns a `NoResult` pseudotype instead
    private PseudoType CreateReturnFromSignature(global::Ts.Ast.Node fn)
    {
        if (AstUtilities.IsFunctionLike(fn))
        {
            var d = fn.FunctionLikeData()!;
            // !!! TODO: support ripping return type off of .FullSignature
            var r = d.Type;
            if (r != null)
            {
                return NewPseudoTypeDirect(r);
            }
        }
        if (IsValueSignatureDeclaration(fn))
        {
            return TypeFromSingleReturnExpression(fn);
        }
        return NewPseudoTypeNoResult(fn);
    }

    private PseudoType TypeFromSingleReturnExpression(global::Ts.Ast.Node fn)
    {
        global::Ts.Ast.Node? candidateExpr = null;
        if (fn != null && !AstUtilities.NodeIsMissing(fn.Body()))
        {
            var flags = AstUtilities.GetFunctionFlags(fn);
            if ((flags & global::Ts.Ast.FunctionFlags.AsyncGenerator) != 0)
            {
                return NewPseudoTypeInferred(fn!, true);
            }

            var body = fn.Body()!;
            if (NodeTests.IsBlock(body))
            {
                ForEachReturnStatement(body, stmt =>
                {
                    if (stmt.Parent != body) // Why bail on nested return statements?
                    {
                        candidateExpr = null;
                        return true;
                    }
                    if (candidateExpr == null)
                    {
                        candidateExpr = stmt.AsReturnStatement().Expression;
                    }
                    else
                    {
                        candidateExpr = null;
                        return true;
                    }
                    return false;
                });
            }
            else
            {
                candidateExpr = body;
            }
        }
        if (candidateExpr != null)
        {
            if (IsContextuallyTyped(candidateExpr))
            {
                global::Ts.Ast.Node? t = null;
                if (candidateExpr.Kind == global::Ts.Ast.SyntaxKind.TypeAssertionExpression)
                {
                    t = candidateExpr.AsTypeAssertion().Type;
                }
                else if (candidateExpr.Kind == global::Ts.Ast.SyntaxKind.AsExpression)
                {
                    t = candidateExpr.AsAsExpression().Type;
                }
                if (t != null && !AstUtilities.IsConstTypeReference(t))
                {
                    return NewPseudoTypeDirect(t);
                }
            }
            else
            {
                return TypeFromExpression(candidateExpr);
            }
        }
        return NewPseudoTypeInferred(fn!, true);
    }

    // This is basically `checkExpression` for pseudotypes
    private PseudoType TypeFromExpression(global::Ts.Ast.Node node)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.OmittedExpression:
                return PseudoTypeUndefined;
            case global::Ts.Ast.SyntaxKind.ParenthesizedExpression:
                // assertions transformed on reparse, just unwrap
                return TypeFromExpression(node.AsParenthesizedExpression().Expression!);
            case global::Ts.Ast.SyntaxKind.Identifier:
                // !!! TODO: in strada, this uses symbol information to ensure `node` refers to the global `undefined` symbol instead
                // we should probably import `resolveName` and use it here to check for the same; but we have to setup some barebones pseudoglobals for that to work!
                if (string.Equals(node.AsIdentifier().Text, "undefined", StringComparison.Ordinal))
                {
                    return PseudoTypeUndefined;
                }
                break;
            case global::Ts.Ast.SyntaxKind.NullKeyword:
                return PseudoTypeNull;
            case global::Ts.Ast.SyntaxKind.ArrowFunction:
            case global::Ts.Ast.SyntaxKind.FunctionExpression:
                return TypeFromFunctionLikeExpression(node);
            case global::Ts.Ast.SyntaxKind.TypeAssertionExpression:
                return TypeFromTypeAssertion(node.AsTypeAssertion().Expression!, node.AsTypeAssertion().Type!);
            case global::Ts.Ast.SyntaxKind.AsExpression:
                return TypeFromTypeAssertion(node.AsAsExpression().Expression!, node.AsAsExpression().Type!);
            case global::Ts.Ast.SyntaxKind.PrefixUnaryExpression:
                if (IsPrimitiveLiteralValue(node, true))
                {
                    return TypeFromPrimitiveLiteralPrefix(node.AsPrefixUnaryExpression());
                }
                break;
            case global::Ts.Ast.SyntaxKind.ArrayLiteralExpression:
                return TypeFromArrayLiteral(node.AsArrayLiteralExpression());
            case global::Ts.Ast.SyntaxKind.ObjectLiteralExpression:
                return TypeFromObjectLiteral(node.AsObjectLiteralExpression());
            case global::Ts.Ast.SyntaxKind.ClassExpression:
                return NewPseudoTypeInferredWithErrors(node, false, [node]); // No possible annotation/directly mappable syntax
            case global::Ts.Ast.SyntaxKind.TemplateExpression:
                // templateLitWithHoles as const, not supported
                if (IsInConstContext(node))
                {
                    return NewPseudoTypeInferred(node, false);
                }
                return NewPseudoTypeMaybeConstLocation(node, NewPseudoTypeInferred(node, false), PseudoTypeString);
            case global::Ts.Ast.SyntaxKind.NumericLiteral:
                return NewPseudoTypeMaybeConstLocation(node, NewPseudoTypeNumericLiteral(node), PseudoTypeNumber);
            case global::Ts.Ast.SyntaxKind.NoSubstitutionTemplateLiteral:
                return NewPseudoTypeMaybeConstLocation(node, NewPseudoTypeStringLiteral(node), PseudoTypeString);
            case global::Ts.Ast.SyntaxKind.StringLiteral:
                return NewPseudoTypeMaybeConstLocation(node, NewPseudoTypeStringLiteral(node), PseudoTypeString);
            case global::Ts.Ast.SyntaxKind.BigIntLiteral:
                return NewPseudoTypeMaybeConstLocation(node, NewPseudoTypeBigIntLiteral(node), PseudoTypeBigInt);
            case global::Ts.Ast.SyntaxKind.TrueKeyword:
                return NewPseudoTypeMaybeConstLocation(node, PseudoTypeTrue, PseudoTypeBoolean);
            case global::Ts.Ast.SyntaxKind.FalseKeyword:
                return NewPseudoTypeMaybeConstLocation(node, PseudoTypeFalse, PseudoTypeBoolean);
        }
        return NewPseudoTypeInferred(node, false);
    }

    private PseudoType TypeFromObjectLiteral(global::Ts.Ast.ObjectLiteralExpression node)
    {
        var errorNodes = CanGetTypeFromObjectLiteral(node);
        if (errorNodes != null)
        {
            return NewPseudoTypeInferredWithErrors(node.AsNode(), false, errorNodes);
        }
        // we are in a const context producing an object literal type, there are no shorthand or spread assignments
        if (node.Properties == null || node.Properties.Nodes.Length == 0)
        {
            return NewPseudoTypeObjectLiteral(null);
        }
        var results = new List<PseudoObjectElement>(node.Properties.Nodes.Length);
        foreach (var e in node.Properties.Nodes)
        {
            switch (e.Kind)
            {
                case global::Ts.Ast.SyntaxKind.MethodDeclaration:
                {
                    var optional = e.AsMethodDeclaration().PostfixToken != null && e.AsMethodDeclaration().PostfixToken!.Kind == global::Ts.Ast.SyntaxKind.QuestionToken;
                    if (e.FunctionLikeData()!.FullSignature != null)
                    {
                        results.Add(NewPseudoPropertyAssignment(
                            false,
                            e.Name()!,
                            optional,
                            NewPseudoTypeDirect(e.FunctionLikeData()!.FullSignature!)));
                    }
                    else
                    {
                        results.Add(NewPseudoObjectMethod(
                            e,
                            e.Name()!,
                            optional,
                            CloneTypeParameters(e.AsMethodDeclaration().TypeParameters),
                            CloneParameters(e.ParameterList()),
                            CreateReturnFromSignature(e)));
                    }
                    break;
                }
                case global::Ts.Ast.SyntaxKind.PropertyAssignment:
                    results.Add(NewPseudoPropertyAssignment(
                        false,
                        e.Name()!,
                        e.AsPropertyAssignment().PostfixToken != null && e.AsPropertyAssignment().PostfixToken!.Kind == global::Ts.Ast.SyntaxKind.QuestionToken,
                        TypeFromExpression(e.Initializer()!)));
                    break;
                case global::Ts.Ast.SyntaxKind.SetAccessor:
                case global::Ts.Ast.SyntaxKind.GetAccessor:
                {
                    var member = GetAccessorMember(e, e.Name()!);
                    if (member != null)
                    {
                        results.Add(member);
                    }
                    break;
                }
            }
        }
        return NewPseudoTypeObjectLiteral(results.ToArray());
    }

    // roughly analogous to typeFromObjectLiteralAccessor in strada
    private PseudoObjectElement? GetAccessorMember(global::Ts.Ast.Node accessor, global::Ts.Ast.Node name)
    {
        var allAccessors = GetAllAccessorDeclarationsForDeclaration(accessor, accessor.Symbol()!.Declarations!); // TODO: node preservation for late-bound accessor pairs?

        // TODO: handle pseudo-annotations from get accessor return positions?
        if (allAccessors.GetAccessor != null && allAccessors.GetAccessor.Type != null &&
            allAccessors.SetAccessor != null && allAccessors.SetAccessor.Parameters!.Nodes.Length > 0 && allAccessors.SetAccessor.Parameters.Nodes[0].AsParameterDeclaration().Type != null)
        {
            // We have possible types for both accessors, we can't know if they are the same type so we keep both accessors

            if (NodeTests.IsGetAccessorDeclaration(accessor))
            {
                return NewPseudoGetAccessor(
                    accessor,
                    name,
                    false,
                    TypeFromAccessor(accessor));
            }
            else
            {
                return NewPseudoSetAccessor(
                    accessor,
                    name,
                    false,
                    CloneParameters(accessor.AsSetAccessorDeclaration().Parameters)![0]);
            }
        }

        if (accessor == allAccessors.FirstAccessor)
        {
            // only one annotated accessor; output a property - `readonly` for a single `get` accessor

            var accessorType = TypeFromAccessor(accessor);
            var readOnly = NodeTests.IsGetAccessorDeclaration(accessor) && allAccessors.SecondAccessor == null;
            return NewPseudoPropertyAssignment(
                readOnly,
                name,
                false,
                accessorType);
        }
        return null;
    }

    // canGetTypeFromObjectLiteral checks whether an object literal can be typed by the pseudochecker.
    // Returns nil if the object can be typed, or a slice of error nodes (shorthand/spread properties,
    // non-literal computed names) that prevent typing.
    private global::Ts.Ast.Node[]? CanGetTypeFromObjectLiteral(global::Ts.Ast.ObjectLiteralExpression node)
    {
        if (node.Properties == null || node.Properties.Nodes.Length == 0)
        {
            return null; // empty object, ok
        }
        List<global::Ts.Ast.Node>? errorNodes = null;
        foreach (var e in node.Properties.Nodes)
        {
            if ((e.Flags & global::Ts.Ast.NodeFlags.ThisNodeHasError) != 0)
            {
                (errorNodes ??= []).Add(e);
                continue;
            }
            if (e.Kind == global::Ts.Ast.SyntaxKind.ShorthandPropertyAssignment || e.Kind == global::Ts.Ast.SyntaxKind.SpreadAssignment)
            {
                (errorNodes ??= []).Add(e);
                continue;
            }
            if ((e.Name()!.Flags & global::Ts.Ast.NodeFlags.ThisNodeHasError) != 0)
            {
                (errorNodes ??= []).Add(e.Name()!);
                continue;
            }
            if (e.Name()!.Kind == global::Ts.Ast.SyntaxKind.PrivateIdentifier)
            {
                (errorNodes ??= []).Add(e);
                continue;
            }
            if (e.Name()!.Kind == global::Ts.Ast.SyntaxKind.ComputedPropertyName)
            {
                var expression = e.Name()!.Expression()!;
                if (!IsPrimitiveLiteralValue(expression, false))
                {
                    (errorNodes ??= []).Add(e.Name()!);
                }
            }
        }
        return errorNodes?.ToArray();
    }

    private PseudoType TypeFromArrayLiteral(global::Ts.Ast.ArrayLiteralExpression node)
    {
        var errorNodes = CanGetTypeFromArrayLiteral(node);
        if (errorNodes != null)
        {
            return NewPseudoTypeInferredWithErrors(node.AsNode(), false, errorNodes);
        }
        if (IsInConstContext(node.AsNode()) && IsContextuallyTyped(node.AsNode()))
        {
            return NewPseudoTypeInferred(node.AsNode(), false); // expr in an as const cast with a contextual type has variable readonly state, bail
        }
        // we are in a const context producing a tuple type, there are no spread elements
        var results = new List<PseudoType>(node.Elements!.Nodes.Length);
        foreach (var e in node.Elements.Nodes)
        {
            results.Add(TypeFromExpression(e));
        }
        return NewPseudoTypeTuple(results.ToArray());
    }

    // canGetTypeFromArrayLiteral checks whether an array literal can be typed by the pseudochecker.
    // Returns nil if the array can be typed, or a slice of error nodes that prevent typing.
    // For non-const arrays, the error node is the array expression itself.
    // For const arrays with spreads, the error node is the spread element.
    private global::Ts.Ast.Node[]? CanGetTypeFromArrayLiteral(global::Ts.Ast.ArrayLiteralExpression node)
    {
        if (!IsInConstContext(node.AsNode()))
        {
            return [node.AsNode()];
        }
        foreach (var e in node.Elements!.Nodes)
        {
            if (e.Kind == global::Ts.Ast.SyntaxKind.SpreadElement)
            {
                return [e];
            }
        }
        return null;
    }

    // See `isConstContext` in `checker.go` - this is basically any node kind mentioned in that
    private static bool IsConstContextPropagatingKind(global::Ts.Ast.SyntaxKind kind)
    {
        switch (kind)
        {
            case global::Ts.Ast.SyntaxKind.ArrayLiteralExpression:
            case global::Ts.Ast.SyntaxKind.ObjectLiteralExpression:
            case global::Ts.Ast.SyntaxKind.ParenthesizedExpression:
            case global::Ts.Ast.SyntaxKind.SpreadElement:
            case global::Ts.Ast.SyntaxKind.PropertyAssignment:
            case global::Ts.Ast.SyntaxKind.ShorthandPropertyAssignment:
            case global::Ts.Ast.SyntaxKind.TemplateSpan:
            case global::Ts.Ast.SyntaxKind.PrefixUnaryExpression:
                return true;
        }
        return false;
    }

    // IsInConstContext traverses up the parent chain to determine if the node is within a const context without needing any
    // persistent traversal scope tracking (which could be unreliable in the presence of `typeof` queries anyway!)
    public static bool IsInConstContext(global::Ts.Ast.Node node)
    {
        // An expression is in a const context if an ancestor is a const type maybeAssertion expression
        var maybeAssertion = AstUtilities.FindAncestor(
            node.Parent,
            n =>
            {
                // stop traversing at assertions or anything not an array/object literal, since only those create or transfer const-ness
                return AstUtilities.IsAssertionExpression(n) || !IsConstContextPropagatingKind(n.Kind);
            });
        return maybeAssertion != null && AstUtilities.IsConstAssertion(maybeAssertion);
    }

    private PseudoType TypeFromPrimitiveLiteralPrefix(global::Ts.Ast.PrefixUnaryExpression node)
    {
        var expr = node.AsNode();
        if (node.Operator == global::Ts.Ast.SyntaxKind.PlusToken)
        {
            expr = node.Operand!;
        }
        var inner = node.Operand!;
        if (inner.Kind == global::Ts.Ast.SyntaxKind.BigIntLiteral)
        {
            return NewPseudoTypeMaybeConstLocation(node.AsNode(), NewPseudoTypeBigIntLiteral(expr.AsNode()), PseudoTypeBigInt);
        }
        if (inner.Kind == global::Ts.Ast.SyntaxKind.NumericLiteral)
        {
            return NewPseudoTypeMaybeConstLocation(node.AsNode(), NewPseudoTypeNumericLiteral(expr.AsNode()), PseudoTypeNumber);
        }
        global::Ts.Core.Debug.FailBadSyntaxKind(inner);
        return null!;
    }

    private PseudoType TypeFromTypeAssertion(global::Ts.Ast.Node expression, global::Ts.Ast.Node typeNode)
    {
        if (AstUtilities.IsConstTypeReference(typeNode))
        {
            return TypeFromExpression(expression);
        }
        return NewPseudoTypeDirect(typeNode);
    }

    private PseudoType TypeFromFunctionLikeExpression(global::Ts.Ast.Node node)
    {
        if (node.FunctionLikeData()!.FullSignature != null)
        {
            return NewPseudoTypeDirect(node.FunctionLikeData()!.FullSignature!);
        }
        var returnType = CreateReturnFromSignature(node);
        var typeParameters = CloneTypeParameters(node.FunctionLikeData()!.TypeParameters);
        var parameters = CloneParameters(node.FunctionLikeData()!.Parameters);
        return NewPseudoTypeSingleCallSignature(
            node,
            parameters,
            typeParameters,
            returnType);
    }

    private static global::Ts.Ast.TypeParameterDeclaration[]? CloneTypeParameters(global::Ts.Ast.NodeList? nodes)
    {
        if (nodes == null)
        {
            return null;
        }
        if (nodes.Nodes.Length == 0)
        {
            return null;
        }
        var result = new global::Ts.Ast.TypeParameterDeclaration[nodes.Nodes.Length];
        for (var i = 0; i < nodes.Nodes.Length; i++)
        {
            result[i] = nodes.Nodes[i].AsTypeParameterDeclaration();
        }
        return result;
    }

    private static bool IsUndefinedPseudoType(PseudoType t)
    {
        return t.Kind == PseudoTypeKind.Undefined || (t.Kind == PseudoTypeKind.MaybeConstLocation && IsUndefinedPseudoType(t.AsPseudoTypeMaybeConstLocation().ConstType));
    }

    private static bool TypeNodeCouldReferToUndefined(global::Ts.Ast.Node node)
    {
        while (node.Kind == global::Ts.Ast.SyntaxKind.ParenthesizedType)
        {
            node = node.AsParenthesizedTypeNode().Type!;
        }
        switch (node.Kind)
        {
            // these types require symbolic/type resolution to know if they definitely do or do not refer to `undefined`, so might (or definitely do)
            case global::Ts.Ast.SyntaxKind.TypeReference:
            case global::Ts.Ast.SyntaxKind.IndexedAccessType:
            case global::Ts.Ast.SyntaxKind.TypeQuery:
            case global::Ts.Ast.SyntaxKind.OptionalType:
            case global::Ts.Ast.SyntaxKind.RestType:
            case global::Ts.Ast.SyntaxKind.ImportType:
                return true;
            case global::Ts.Ast.SyntaxKind.IntersectionType:
                // TODO: why is this not `core.Every`? strada treated unions and intersections the same, but logically every intersection member needs to contain a possible `undefined`
                // for the result type to contain `undefined`. Likely a bug persisting from strada.
                return CoreUtil.Some(node.AsIntersectionTypeNode().Types!.Nodes, TypeNodeCouldReferToUndefined);
            case global::Ts.Ast.SyntaxKind.UnionType:
                return CoreUtil.Some(node.AsUnionTypeNode().Types!.Nodes, TypeNodeCouldReferToUndefined);
            case global::Ts.Ast.SyntaxKind.ConditionalType: // suspect - should be treated as a union of both branches instead, likely a bug persisted from strada
                return true;
            case global::Ts.Ast.SyntaxKind.TypeOperator: // suspect - always refers to a subset of `string | number | symbol` for `keyof` or `symbol` for `unique`
                return true;
            case global::Ts.Ast.SyntaxKind.TypePredicate: // suspect - always refers to `never` or `boolean`, depending on kind - considered possibly-`undefined` referencing for strada compat
                return true;
            case global::Ts.Ast.SyntaxKind.UndefinedKeyword:
                return true;
            default: // all other keywords, literal types, function-y types, array/tuple types, type literals, template types, this types
                return false;
        }
    }

    // see this as the inverse of `canAddUndefined` in `expressionToTypeNode` in strada
    public static bool CouldAlreadyReferToUndefinedType(PseudoType t)
    {
        if (t.Kind == PseudoTypeKind.NoResult || t.Kind == PseudoTypeKind.Inferred || IsUndefinedPseudoType(t))
        {
            return true;
        }
        if (t.Kind == PseudoTypeKind.MaybeConstLocation)
        {
            var mc = t.AsPseudoTypeMaybeConstLocation();
            return CouldAlreadyReferToUndefinedType(mc.RegularType); // if we're even asking this question, it's not a `const` location
        }
        if (t.Kind == PseudoTypeKind.Direct)
        {
            // inspect the direct type node
            var node = t.AsPseudoTypeDirect().TypeNode;
            return TypeNodeCouldReferToUndefined(node);
        }
        if (t.Kind == PseudoTypeKind.Union)
        {
            return CoreUtil.Some(t.AsPseudoTypeUnion().Types, CouldAlreadyReferToUndefinedType);
        }
        return false;
    }

    private static bool IsOptionalInitializedOrRestParameter(global::Ts.Ast.Node node)
    {
        var p = node.AsParameterDeclaration();
        if (p.DotDotDotToken != null || p.Initializer != null || p.QuestionToken != null)
        {
            return true;
        }
        return false;
    }

    // lastRequiredParamIndex returns the index just past the last required parameter
    // in the list. A parameter is "required" if it has no question token, no initializer,
    // and no rest token. This is computed in a single reverse pass so callers can
    // determine "has required parameter after index i" with `i+1 < lastRequired`
    // (equivalently, `i < lastRequired-1`) in O(1).
    private static int LastRequiredParamIndex(global::Ts.Ast.Node[] parameters)
    {
        for (var i = parameters.Length - 1; i >= 0; i--)
        {
            if (!IsOptionalInitializedOrRestParameter(parameters[i]))
            {
                return i + 1;
            }
        }
        return 0;
    }

    private static PseudoType AddUndefinedIfDefinitelyRequired(PseudoType expr)
    {
        // If `expr` doesn't already contain `| undefined` or a direct/inferred type that may contain `undefined`, add `| undefined`
        // in Strada, this reached into the checker to see if `undefined` was necessary, using `isRequiredOptionalParameter` from the emit resolver,
        // but that's not required on top of the syntactic checks to get the same behavior. (If we get the type wrong, it'll mismatch later and be discarded
        // for an inference error since corsa actually validates that pseudotypes semantically match the inferred type the checker produces)
        if (CouldAlreadyReferToUndefinedType(expr))
        {
            return expr; // will just error later, more like than not, unless the `undefined` is explicit in the pseudo
        }
        // Explicitly add an `| undefined`
        return NewPseudoTypeUnion([expr, PseudoTypeUndefined]);
    }

    private PseudoType TypeFromParameter(global::Ts.Ast.ParameterDeclaration node)
    {
        var parent = node.Parent!;
        if (parent.Kind == global::Ts.Ast.SyntaxKind.SetAccessor)
        {
            return GetTypeOfAccessor(parent);
        }
        // Fast path: no initializer means we never need parameter position info.
        if (node.Initializer == null)
        {
            if (node.Type != null)
            {
                return NewPseudoTypeDirect(node.Type);
            }
            return NewPseudoTypeNoResult(node.AsNode());
        }
        var p = parent.Parameters();
        var selfIdx = Array.IndexOf(p, node.AsNode());
        var lastRequired = LastRequiredParamIndex(p);
        return TypeFromParameterWorker(node, selfIdx, lastRequired);
    }

    private PseudoType TypeFromParameterWorker(global::Ts.Ast.ParameterDeclaration node, int selfIdx, int lastRequired)
    {
        var parent = node.Parent!;
        if (parent.Kind == global::Ts.Ast.SyntaxKind.SetAccessor)
        {
            return GetTypeOfAccessor(parent);
        }
        var hasRequiredAfter = selfIdx < lastRequired - 1;
        var declaredType = node.Type;
        if (declaredType != null)
        {
            var result = NewPseudoTypeDirect(declaredType);
            // When the parameter has an initializer and strict null checks are enabled,
            // check if `| undefined` needs to be added because there are required parameters after this one.
            // This mirrors the checker's getTypeOfParameter which adds optionality for initialized parameters.
            if (strictNullChecks && node.Initializer != null && hasRequiredAfter)
            {
                return AddUndefinedIfDefinitelyRequired(result);
            }
            return result;
        }
        if (node.Initializer != null && NodeTests.IsIdentifier(node.Name()!) && !IsContextuallyTyped(node.AsNode()))
        {
            var expr = TypeFromExpression(node.Initializer);
            if (expr != null && (expr.Kind == PseudoTypeKind.Inferred && (expr.AsPseudoTypeInferred().ErrorNodes?.Length ?? 0) == 0))
            {
                expr = NewPseudoTypeInferredWithErrors(expr.AsPseudoTypeInferred().Expression, false, [node.AsNode()]); // Move error up to the parameter
            }
            if (!strictNullChecks)
            {
                return expr!;
            }
            if (!hasRequiredAfter)
            {
                return expr!;
            }
            // if there is a non-optional parameter after this one, a `| undefined` will need to explicitly be emitted on this parameter, if it's not already there
            return AddUndefinedIfDefinitelyRequired(expr!);
        }
        // TODO: In strada, the ID checker doesn't infer a parameter type from binding pattern names, but the real checker _does_!
        // This means ID won't let you write, say, `({elem}) => false` without an annotation, even though it's trivially of type
        // `(p0: {elem: any}) => boolean` and error-free under `noImplicitAny: false`!
        // That limitation is retained here.
        return NewPseudoTypeNoResult(node.AsNode());
    }

    private PseudoParameter[]? CloneParameters(global::Ts.Ast.NodeList? nodes)
    {
        if (nodes == null)
        {
            return null;
        }
        if (nodes.Nodes.Length == 0)
        {
            return null;
        }
        var lastRequired = LastRequiredParamIndex(nodes.Nodes);
        var result = new PseudoParameter[nodes.Nodes.Length];
        for (var i = 0; i < nodes.Nodes.Length; i++)
        {
            var e = nodes.Nodes[i];
            var p = e.AsParameterDeclaration();
            var optional = p.QuestionToken != null;
            if (!optional && p.Initializer != null)
            {
                // A parameter with an initializer is optional only if all subsequent
                // parameters are also optional/have initializers/are rest parameters.
                // This matches the checker's isOptionalParameter semantics.
                optional = i >= lastRequired - 1;
            }
            result[i] = NewPseudoParameter(
                p.DotDotDotToken != null,
                e.Name()!,
                optional,
                TypeFromParameterWorker(p, i, lastRequired));
        }
        return result;
    }

    private static bool IsContextuallyTyped(global::Ts.Ast.Node node)
    {
        return AstUtilities.FindAncestor(node.Parent, n =>
        {
            // Functions calls or parent type annotations (but not the return type of a function expression) may impact the inferred type and local inference is unreliable
            if (NodeTests.IsCallExpression(n))
            {
                return true;
            }
            if (NodeTests.IsSatisfiesExpression(n))
            {
                return true;
            }
            if ((IsVariableParameterOrProperty(n) || AstUtilities.IsAssertionExpression(n)) && n.Type() != null && !AstUtilities.IsConstAssertion(n))
            {
                return true;
            }
            return NodeTests.IsJsxElement(n) || NodeTests.IsJsxExpression(n);
        }) != null;
    }

    // TODO(csport): Remove these local adapters when the corresponding complete ast utilities land.
    // The bodies are direct ports of internal/ast/utilities.go and keep pseudochecker executable.
    private static bool IsVarConst(global::Ts.Ast.Node node)
    {
        return (AstUtilities.GetCombinedNodeFlags(node) & global::Ts.Ast.NodeFlags.BlockScoped) == global::Ts.Ast.NodeFlags.Const;
    }

    private static bool IsVariableParameterOrProperty(global::Ts.Ast.Node node)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.VariableDeclaration:
            case global::Ts.Ast.SyntaxKind.Parameter:
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
                return true;
            default:
                return false;
        }
    }

    private static bool IsPrimitiveLiteralValue(global::Ts.Ast.Node node, bool includeBigInt)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.TrueKeyword:
            case global::Ts.Ast.SyntaxKind.FalseKeyword:
            case global::Ts.Ast.SyntaxKind.NumericLiteral:
            case global::Ts.Ast.SyntaxKind.StringLiteral:
            case global::Ts.Ast.SyntaxKind.NoSubstitutionTemplateLiteral:
                return true;
            case global::Ts.Ast.SyntaxKind.BigIntLiteral:
                return includeBigInt;
            case global::Ts.Ast.SyntaxKind.PrefixUnaryExpression:
            {
                var prefix = node.AsPrefixUnaryExpression();
                if (prefix.Operator == global::Ts.Ast.SyntaxKind.MinusToken)
                {
                    return NodeTests.IsNumericLiteral(prefix.Operand!) || (includeBigInt && NodeTests.IsBigIntLiteral(prefix.Operand!));
                }
                if (prefix.Operator == global::Ts.Ast.SyntaxKind.PlusToken)
                {
                    return NodeTests.IsNumericLiteral(prefix.Operand!);
                }
                return false;
            }
            default:
                return false;
        }
    }

    private static bool ForEachReturnStatement(global::Ts.Ast.Node body, Func<global::Ts.Ast.Node, bool> visitor)
    {
        // Warning: This has the same semantics as the forEach family of functions in that traversal terminates
        // in the event that 'visitor' returns true.
        bool Traverse(global::Ts.Ast.Node node)
        {
            switch (node.Kind)
            {
                case global::Ts.Ast.SyntaxKind.ReturnStatement:
                    return visitor(node);
                case global::Ts.Ast.SyntaxKind.CaseBlock:
                case global::Ts.Ast.SyntaxKind.Block:
                case global::Ts.Ast.SyntaxKind.IfStatement:
                case global::Ts.Ast.SyntaxKind.DoStatement:
                case global::Ts.Ast.SyntaxKind.WhileStatement:
                case global::Ts.Ast.SyntaxKind.ForStatement:
                case global::Ts.Ast.SyntaxKind.ForInStatement:
                case global::Ts.Ast.SyntaxKind.ForOfStatement:
                case global::Ts.Ast.SyntaxKind.WithStatement:
                case global::Ts.Ast.SyntaxKind.SwitchStatement:
                case global::Ts.Ast.SyntaxKind.CaseClause:
                case global::Ts.Ast.SyntaxKind.DefaultClause:
                case global::Ts.Ast.SyntaxKind.LabeledStatement:
                case global::Ts.Ast.SyntaxKind.TryStatement:
                case global::Ts.Ast.SyntaxKind.CatchClause:
                    return node.ForEachChild(Traverse);
                default:
                    return false;
            }
        }

        return Traverse(body);
    }

    private sealed class AllAccessorDeclarations
    {
        public global::Ts.Ast.Node FirstAccessor = null!;
        public global::Ts.Ast.Node? SecondAccessor;
        public global::Ts.Ast.SetAccessorDeclaration? SetAccessor;
        public global::Ts.Ast.GetAccessorDeclaration? GetAccessor;
    }

    private static AllAccessorDeclarations GetAllAccessorDeclarationsForDeclaration(global::Ts.Ast.Node accessor, global::Ts.Ast.Node[] declarationsOfSymbol)
    {
        global::Ts.Ast.SyntaxKind otherKind;
        if (accessor.Kind == global::Ts.Ast.SyntaxKind.SetAccessor)
        {
            otherKind = global::Ts.Ast.SyntaxKind.GetAccessor;
        }
        else if (accessor.Kind == global::Ts.Ast.SyntaxKind.GetAccessor)
        {
            otherKind = global::Ts.Ast.SyntaxKind.SetAccessor;
        }
        else
        {
            throw new InvalidOperationException($"Unexpected node kind {accessor.Kind}");
        }
        // otherAccessor := GetDeclarationOfKind(c.getSymbolOfDeclaration(accessor), otherKind)
        global::Ts.Ast.Node? otherAccessor = null;
        foreach (var declaration in declarationsOfSymbol)
        {
            if (declaration.Kind == otherKind)
            {
                otherAccessor = declaration;
                break;
            }
        }

        global::Ts.Ast.Node firstAccessor;
        global::Ts.Ast.Node? secondAccessor;
        if (otherAccessor != null && otherAccessor.Pos < accessor.Pos)
        {
            firstAccessor = otherAccessor;
            secondAccessor = accessor;
        }
        else
        {
            firstAccessor = accessor;
            secondAccessor = otherAccessor;
        }

        global::Ts.Ast.SetAccessorDeclaration? setAccessor = null;
        global::Ts.Ast.GetAccessorDeclaration? getAccessor = null;
        if (accessor.Kind == global::Ts.Ast.SyntaxKind.SetAccessor)
        {
            setAccessor = accessor.AsSetAccessorDeclaration();
            if (otherAccessor != null)
            {
                getAccessor = otherAccessor.AsGetAccessorDeclaration();
            }
        }
        else
        {
            getAccessor = accessor.AsGetAccessorDeclaration();
            if (otherAccessor != null)
            {
                setAccessor = otherAccessor.AsSetAccessorDeclaration();
            }
        }

        return new AllAccessorDeclarations
        {
            FirstAccessor = firstAccessor,
            SecondAccessor = secondAccessor,
            SetAccessor = setAccessor,
            GetAccessor = getAccessor,
        };
    }
}
