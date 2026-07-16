// Ported from tsgo internal/printer/factory.go
//
// This is the printer package's own emit-side NodeFactory (synthetic-node construction and update
// helpers used during transformation/emit). It is distinct from the AST-side `global::Ts.Ast.NodeFactory`:
// the printer NodeFactory wraps an ast.NodeFactory (installed with EmitContext hooks) and adds the
// generated-identifier family, compound-node builders, declaration-name helpers, and emit-helper
// constructors.
//
// PORT-NOTE: the generated-identifier slice, the base-factory forwards, and the `AsNodeFactory()` bridge
// were originally co-hosted in EmitContext.cs (the emit-infrastructure wave) with a PORT-NOTE that they
// belonged here. This wave relocates them into this file (the class is `partial`) and completes the rest
// of factory.go.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

using EF = global::Ts.Printer.EmitFlags;

namespace Ts.Printer;

// Go: type NodeFactory struct { ast.NodeFactory; emitContext *EmitContext }
//
// The Go type embeds ast.NodeFactory (so all `New*`/`Update*` methods are promoted). C# has no embedding;
// we hold an `ast.NodeFactory` field and forward the base-factory methods this file needs. The factory
// implements NodeFactoryCoercible so `node.Clone(this)` resolves to the wrapped ast factory (mirroring the
// Go `node.Clone(f.AsNodeFactory())` / `nodeName.Clone(f)` calls).
public sealed partial class NodeFactory : NodeFactoryCoercible
{
    private readonly global::Ts.Ast.NodeFactory factory;
    private readonly EmitContext emitContext;

    // Go: var nextAutoGenerateId atomic.Uint32 (package-level, emitcontext.go:427)
    private static uint nextAutoGenerateId;

    // Go: func NewNodeFactory(context *EmitContext) *NodeFactory
    internal NodeFactory(EmitContext context)
    {
        factory = new global::Ts.Ast.NodeFactory(new NodeFactoryHooks
        {
            OnCreate = context.OnCreate,
            OnUpdate = context.OnUpdate,
            OnClone = context.OnClone,
        });
        emitContext = context;
    }

    // NodeFactoryCoercible: exposes the wrapped ast factory (Go: promoted ast.NodeFactory / f.AsNodeFactory()).
    public global::Ts.Ast.NodeFactory AsNodeFactory()
    {
        return factory;
    }

    //
    // Base-factory forwards (Go: promoted via the embedded `ast.NodeFactory`).
    //

    public Node NewIdentifier(string text) => factory.NewIdentifier(text);

    public Node NewPrivateIdentifier(string text) => factory.NewPrivateIdentifier(text);

    public NodeList NewNodeList(Node[] nodes) => factory.NewNodeList(nodes);

    public Node NewToken(SyntaxKind kind) => factory.NewToken(kind);

    public Node NewKeywordExpression(SyntaxKind kind) => factory.NewKeywordExpression(kind);

    public Node NewStringLiteral(string text, TokenFlags tokenFlags) => factory.NewStringLiteral(text, tokenFlags);

    public Node NewNumericLiteral(string text, TokenFlags tokenFlags) => factory.NewNumericLiteral(text, tokenFlags);

    public Node NewBinaryExpression(ModifierList? modifiers, Expression? left, TypeNode? typeNode, BinaryOperatorToken? operatorToken, Expression? right) =>
        factory.NewBinaryExpression(modifiers, left, typeNode, operatorToken, right);

    public Node NewVoidExpression(Expression? expression) => factory.NewVoidExpression(expression);

    public Node NewTypeOfExpression(Expression? expression) => factory.NewTypeOfExpression(expression);

    public Node NewCallExpression(Expression? expression, QuestionDotToken? questionDotToken, TypeList? typeArguments, ElementList? arguments, NodeFlags flags) =>
        factory.NewCallExpression(expression, questionDotToken, typeArguments, arguments, flags);

    public Node NewPropertyAccessExpression(Expression? expression, QuestionDotToken? questionDotToken, MemberName? name, NodeFlags flags) =>
        factory.NewPropertyAccessExpression(expression, questionDotToken, name, flags);

    public Node NewElementAccessExpression(Expression? expression, QuestionDotToken? questionDotToken, Expression? argumentExpression, NodeFlags flags) =>
        factory.NewElementAccessExpression(expression, questionDotToken, argumentExpression, flags);

    public Node NewArrayLiteralExpression(ElementList? elements, bool multiLine) => factory.NewArrayLiteralExpression(elements, multiLine);

    public Node NewObjectLiteralExpression(NodeList? properties, bool multiLine) => factory.NewObjectLiteralExpression(properties, multiLine);

    public Node NewConditionalExpression(Expression? condition, QuestionToken? questionToken, Expression? whenTrue, ColonToken? colonToken, Expression? whenFalse) =>
        factory.NewConditionalExpression(condition, questionToken, whenTrue, colonToken, whenFalse);

    public Node NewArrowFunction(ModifierList? modifiers, TypeParameterList? typeParameters, ParameterList? parameters, TypeNode? typeNode, TypeNode? fullSignature, EqualsGreaterThanToken? equalsGreaterThanToken, ConciseBody? body) =>
        factory.NewArrowFunction(modifiers, typeParameters, parameters, typeNode, fullSignature, equalsGreaterThanToken, body);

    public Node NewFunctionExpression(ModifierList? modifiers, AsteriskToken? asteriskToken, IdentifierNode? name, TypeParameterList? typeParameters, ParameterList? parameters, TypeNode? typeNode, TypeNode? fullSignature, FunctionBody? body) =>
        factory.NewFunctionExpression(modifiers, asteriskToken, name, typeParameters, parameters, typeNode, fullSignature, body);

    public Node NewParenthesizedExpression(Expression? expression) => factory.NewParenthesizedExpression(expression);

    public Node NewParameterDeclaration(ModifierList? modifiers, DotDotDotToken? dotDotDotToken, BindingName? name, QuestionToken? questionToken, TypeNode? typeNode, Expression? initializer) =>
        factory.NewParameterDeclaration(modifiers, dotDotDotToken, name, questionToken, typeNode, initializer);

    public Node NewPropertyAssignment(ModifierList? modifiers, PropertyName? name, TokenNode? postfixToken, TypeNode? typeNode, Expression? initializer) =>
        factory.NewPropertyAssignment(modifiers, name, postfixToken, typeNode, initializer);

    public Node NewSetAccessorDeclaration(ModifierList? modifiers, PropertyName? name, TypeParameterList? typeParameters, ParameterList? parameters, TypeNode? typeNode, TypeNode? fullSignature, FunctionBody? body) =>
        factory.NewSetAccessorDeclaration(modifiers, name, typeParameters, parameters, typeNode, fullSignature, body);

    public Node NewExpressionStatement(Expression? expression) => factory.NewExpressionStatement(expression);

    public Node NewVariableDeclaration(BindingName? name, ExclamationToken? exclamationToken, TypeNode? typeNode, Expression? initializer) =>
        factory.NewVariableDeclaration(name, exclamationToken, typeNode, initializer);

    public Node NewVariableDeclarationList(VariableDeclarationNodeList? declarations, NodeFlags flags) =>
        factory.NewVariableDeclarationList(declarations, flags);

    public Node NewVariableStatement(ModifierList? modifiers, VariableDeclarationListNode? declarationList) =>
        factory.NewVariableStatement(modifiers, declarationList);

    public Node NewBlock(StatementList? statements, bool multiLine) => factory.NewBlock(statements, multiLine);

    public Node NewReturnStatement(Expression? expression) => factory.NewReturnStatement(expression);

    public Node NewExportAssignment(ModifierList? modifiers, bool isExportEquals, TypeNode? typeNode, Expression? expression) =>
        factory.NewExportAssignment(modifiers, isExportEquals, typeNode, expression);

    public Node NewExportSpecifier(bool isTypeOnly, ModuleExportName? propertyName, ModuleExportName? name) =>
        factory.NewExportSpecifier(isTypeOnly, propertyName, name);

    public Node NewNamedExports(ExportSpecifierList? elements) => factory.NewNamedExports(elements);

    public Node NewExportDeclaration(ModifierList? modifiers, bool isTypeOnly, NamedExportBindings? exportClause, Expression? moduleSpecifier, ImportAttributesNode? attributes) =>
        factory.NewExportDeclaration(modifiers, isTypeOnly, exportClause, moduleSpecifier, attributes);

    public Node NewNotEmittedStatement() => factory.NewNotEmittedStatement();

    public Node UpdateBlock(Block node, StatementList? statements, bool multiLine) => factory.UpdateBlock(node, statements, multiLine);

    public Node UpdateLabeledStatement(LabeledStatement node, IdentifierNode? label, Statement? statement) =>
        factory.UpdateLabeledStatement(node, label, statement);

    public Node UpdateVariableDeclaration(VariableDeclaration node, BindingName? name, ExclamationToken? exclamationToken, TypeNode? typeNode, Expression? initializer) =>
        factory.UpdateVariableDeclaration(node, name, exclamationToken, typeNode, initializer);

    public Node UpdateVariableDeclarationList(VariableDeclarationList node, VariableDeclarationNodeList? declarations, NodeFlags flags) =>
        factory.UpdateVariableDeclarationList(node, declarations, flags);

    public Node UpdateParenthesizedExpression(ParenthesizedExpression node, Expression? expression) =>
        factory.UpdateParenthesizedExpression(node, expression);

    public Node UpdateTypeAssertion(TypeAssertion node, TypeNode? typeNode, Expression? expression) =>
        factory.UpdateTypeAssertion(node, typeNode, expression);

    public Node UpdateAsExpression(AsExpression node, Expression? expression, TypeNode? typeNode) =>
        factory.UpdateAsExpression(node, expression, typeNode);

    public Node UpdateSatisfiesExpression(SatisfiesExpression node, Expression? expression, TypeNode? typeNode) =>
        factory.UpdateSatisfiesExpression(node, expression, typeNode);

    public Node UpdateNonNullExpression(NonNullExpression node, Expression? expression, NodeFlags flags) =>
        factory.UpdateNonNullExpression(node, expression, flags);

    public Node UpdateExpressionWithTypeArguments(ExpressionWithTypeArguments node, Expression? expression, TypeList? typeArguments) =>
        factory.UpdateExpressionWithTypeArguments(node, expression, typeArguments);

    public Node UpdatePartiallyEmittedExpression(PartiallyEmittedExpression node, Expression? expression) =>
        factory.UpdatePartiallyEmittedExpression(node, expression);

    //
    // Generated identifiers (factory.go:29-161).
    //

    private Node NewGeneratedIdentifier(GeneratedIdentifierFlags kind, string text, Node? node, AutoGenerateOptions options)
    {
        var id = new AutoGenerateId(Interlocked.Increment(ref nextAutoGenerateId));

        if (text.Length == 0)
        {
            if (node is null)
            {
                text = $"(auto@{id.Value})";
            }
            else if (AstUtilities.IsMemberName(node))
            {
                text = node.Text();
            }
            else
            {
                text = $"(generated@{AstUtilities.GetNodeId(emitContext.GetNodeForGeneratedNameWorker(node, id)).Value})";
            }
            text = Utilities.FormatGeneratedName(false /*privateName*/, options.Prefix, text, options.Suffix);
        }

        var name = NewIdentifier(text);
        var autoGenerate = new AutoGenerateInfo
        {
            Id = id,
            Flags = kind | (options.Flags & ~GeneratedIdentifierFlags.KindMask),
            Prefix = options.Prefix,
            Suffix = options.Suffix,
            Node = node,
        };
        emitContext.autoGenerate ??= new Dictionary<Node, AutoGenerateInfo>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        emitContext.autoGenerate[name] = autoGenerate;
        return name;
    }

    // Allocates a new temp variable name, but does not record it in the environment. It is recommended to pass this to either
    // `AddVariableDeclaration` or `AddLexicalDeclaration` to ensure it is properly tracked, if you are not otherwise handling
    // it yourself.
    public Node NewTempVariable() => NewTempVariableEx(new AutoGenerateOptions());

    // Allocates a new temp variable name, but does not record it in the environment. It is recommended to pass this to either
    // `AddVariableDeclaration` or `AddLexicalDeclaration` to ensure it is properly tracked, if you are not otherwise handling
    // it yourself.
    public Node NewTempVariableEx(AutoGenerateOptions options) => NewGeneratedIdentifier(GeneratedIdentifierFlags.Auto, "", null /*node*/, options);

    // Allocates a new loop variable name.
    public Node NewLoopVariable() => NewLoopVariableEx(new AutoGenerateOptions());

    // Allocates a new loop variable name.
    public Node NewLoopVariableEx(AutoGenerateOptions options) => NewGeneratedIdentifier(GeneratedIdentifierFlags.Loop, "", null /*node*/, options);

    // Allocates a new unique name based on the provided text.
    public Node NewUniqueName(string text) => NewUniqueNameEx(text, new AutoGenerateOptions());

    // Allocates a new unique name based on the provided text.
    public Node NewUniqueNameEx(string text, AutoGenerateOptions options) => NewGeneratedIdentifier(GeneratedIdentifierFlags.Unique, text, null /*node*/, options);

    // Allocates a new unique name based on the provided node.
    public Node NewGeneratedNameForNode(Node node) => NewGeneratedNameForNodeEx(node, new AutoGenerateOptions());

    // Allocates a new unique name based on the provided node.
    public Node NewGeneratedNameForNodeEx(Node node, AutoGenerateOptions options)
    {
        if (options.Prefix.Length > 0 || options.Suffix.Length > 0)
        {
            options.Flags |= GeneratedIdentifierFlags.Optimistic;
        }

        return NewGeneratedIdentifier(GeneratedIdentifierFlags.Node, "", node, options);
    }

    private Node NewGeneratedPrivateIdentifier(GeneratedIdentifierFlags kind, string text, Node? node, AutoGenerateOptions options)
    {
        var id = new AutoGenerateId(Interlocked.Increment(ref nextAutoGenerateId));

        if (text.Length == 0)
        {
            if (node is null)
            {
                text = $"(auto@{id.Value})";
            }
            else if (AstUtilities.IsMemberName(node))
            {
                text = node.Text();
            }
            else
            {
                text = $"(generated@{AstUtilities.GetNodeId(emitContext.GetNodeForGeneratedNameWorker(node, id)).Value})";
            }
            text = Utilities.FormatGeneratedName(true /*privateName*/, options.Prefix, text, options.Suffix);
        }
        else if (!text.StartsWith('#'))
        {
            throw new InvalidOperationException("First character of private identifier must be #: " + text);
        }

        var name = NewPrivateIdentifier(text);
        var autoGenerate = new AutoGenerateInfo
        {
            Id = id,
            Flags = kind | (options.Flags & ~GeneratedIdentifierFlags.KindMask),
            Prefix = options.Prefix,
            Suffix = options.Suffix,
            Node = node,
        };
        emitContext.autoGenerate ??= new Dictionary<Node, AutoGenerateInfo>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        emitContext.autoGenerate[name] = autoGenerate;
        return name;
    }

    // Allocates a new unique private name based on the provided text.
    public Node NewUniquePrivateName(string text) => NewUniquePrivateNameEx(text, new AutoGenerateOptions());

    // Allocates a new unique private name based on the provided text.
    public Node NewUniquePrivateNameEx(string text, AutoGenerateOptions options) => NewGeneratedPrivateIdentifier(GeneratedIdentifierFlags.Unique, text, null /*node*/, options);

    // Allocates a new unique private name based on the provided node.
    public Node NewGeneratedPrivateNameForNode(Node node) => NewGeneratedPrivateNameForNodeEx(node, new AutoGenerateOptions());

    // Allocates a new unique private name based on the provided node.
    public Node NewGeneratedPrivateNameForNodeEx(Node node, AutoGenerateOptions options)
    {
        if (options.Prefix.Length > 0 || options.Suffix.Length > 0)
        {
            options.Flags |= GeneratedIdentifierFlags.Optimistic;
        }

        return NewGeneratedPrivateIdentifier(GeneratedIdentifierFlags.Node, "", node, options);
    }

    // Allocates a new StringLiteral whose source text is derived from the provided node. This is often used to create a
    // string representation of an Identifier or NumericLiteral.
    public Node NewStringLiteralFromNode(Node textSourceNode)
    {
        string text = "";
        switch (textSourceNode.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.JsxNamespacedName:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
            case SyntaxKind.TemplateMiddle:
            case SyntaxKind.TemplateTail:
            case SyntaxKind.RegularExpressionLiteral:
                text = textSourceNode.Text();
                break;
        }
        var node = NewStringLiteral(text, TokenFlags.None);
        emitContext.textSource ??= new Dictionary<Node, Node>(global::Ts.Core.ReferenceEqualityComparer<Node>.Instance);
        emitContext.textSource[node] = textSourceNode;
        return node;
    }

    //
    // Common Tokens
    //

    public Node NewThisExpression()
    {
        return NewKeywordExpression(SyntaxKind.ThisKeyword);
    }

    public Node NewTrueExpression()
    {
        return NewKeywordExpression(SyntaxKind.TrueKeyword);
    }

    public Node NewFalseExpression()
    {
        return NewKeywordExpression(SyntaxKind.FalseKeyword);
    }

    //
    // Common Operators
    //

    public Node NewCommaExpression(Expression left, Expression right)
    {
        return NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, NewToken(SyntaxKind.CommaToken), right);
    }

    public Node NewAssignmentExpression(Expression left, Expression right)
    {
        return NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, NewToken(SyntaxKind.EqualsToken), right);
    }

    public Node NewLogicalORExpression(Expression left, Expression right)
    {
        return NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, NewToken(SyntaxKind.BarBarToken), right);
    }

    public Node NewLogicalANDExpression(Expression left, Expression right)
    {
        return NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, NewToken(SyntaxKind.AmpersandAmpersandToken), right);
    }

    // func (f *NodeFactory) NewLogicalANDExpression(left *ast.Expression, right *ast.Expression) *ast.Expression
    // func (f *NodeFactory) NewBitwiseORExpression(left *ast.Expression, right *ast.Expression) *ast.Expression
    // func (f *NodeFactory) NewBitwiseXORExpression(left *ast.Expression, right *ast.Expression) *ast.Expression
    // func (f *NodeFactory) NewBitwiseANDExpression(left *ast.Expression, right *ast.Expression) *ast.Expression
    public Node NewStrictEqualityExpression(Expression left, Expression right)
    {
        return NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, NewToken(SyntaxKind.EqualsEqualsEqualsToken), right);
    }

    public Node NewStrictInequalityExpression(Expression left, Expression right)
    {
        return NewBinaryExpression(null /*modifiers*/, left, null /*typeNode*/, NewToken(SyntaxKind.ExclamationEqualsEqualsToken), right);
    }

    //
    // Compound Nodes
    //

    public Node NewVoidZeroExpression()
    {
        return NewVoidExpression(NewNumericLiteral("0", TokenFlags.None));
    }

    private Expression[] FlattenCommaElement(Expression node, Expression[] expressions)
    {
        if (NodeTests.IsBinaryExpression(node) && AstUtilities.NodeIsSynthesized(node) && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken)
        {
            expressions = FlattenCommaElement(node.AsBinaryExpression().Left!, expressions);
            expressions = FlattenCommaElement(node.AsBinaryExpression().Right!, expressions);
        }
        else
        {
            expressions = [.. expressions, node];
        }
        return expressions;
    }

    private Expression[] FlattenCommaElements(Expression[] expressions)
    {
        Expression[] result = [];
        foreach (var expression in expressions)
        {
            result = FlattenCommaElement(expression, result);
        }
        return result;
    }

    // Converts a slice of expressions into a single comma-delimited expression. Returns nil if expressions is nil or empty.
    public Expression? InlineExpressions(Expression[] expressions)
    {
        if (expressions.Length == 0)
        {
            return null;
        }
        if (expressions.Length == 1)
        {
            return expressions[0];
        }
        expressions = FlattenCommaElements(expressions);
        var expression = expressions[0];
        foreach (var next in expressions[1..])
        {
            expression = NewCommaExpression(expression, next);
        }
        return expression;
    }

    //
    // Utilities
    //

    public Expression CreateExpressionFromEntityName(Node node)
    {
        if (NodeTests.IsQualifiedName(node))
        {
            var left = CreateExpressionFromEntityName(node.AsQualifiedName().Left!);
            var right = node.AsQualifiedName().Right!.Clone(AsNodeFactory())!;
            right.Loc = node.AsQualifiedName().Right!.Loc;
            // TODO(rbuckton): Does this need to be parented?
            right.Parent = node.AsQualifiedName().Right!.Parent;
            var propAccess = NewPropertyAccessExpression(left, null, right, NodeFlags.None);
            propAccess.Loc = node.Loc;
            return propAccess;
        }
        var res = node.Clone(AsNodeFactory())!;
        res.Loc = node.Loc;
        // TODO(rbuckton): Does this need to be parented?
        res.Parent = node.Parent;
        return res;
    }

    public Node RestoreEnclosingLabel(Node node, LabeledStatement? outermostLabeledStatement)
    {
        if (outermostLabeledStatement is null)
        {
            return node;
        }
        var innerLabel = node;
        if (NodeTests.IsLabeledStatement(outermostLabeledStatement.Statement!))
        {
            innerLabel = RestoreEnclosingLabel(node, outermostLabeledStatement.Statement!.AsLabeledStatement());
        }
        return UpdateLabeledStatement(
            outermostLabeledStatement,
            outermostLabeledStatement.Label,
            innerLabel);
    }

    // CreateForOfBindingStatement creates a statement to bind the iteration value.
    public Node CreateForOfBindingStatement(Node node, Node boundValue)
    {
        if (NodeTests.IsVariableDeclarationList(node))
        {
            var firstDeclaration = node.AsVariableDeclarationList().Declarations!.Nodes[0];
            var updatedDeclaration = UpdateVariableDeclaration(
                firstDeclaration.AsVariableDeclaration(),
                firstDeclaration.Name(),
                null, /*exclamationToken*/
                null, /*type*/
                boundValue);
            var statement = NewVariableStatement(
                null,
                UpdateVariableDeclarationList(
                    node.AsVariableDeclarationList(),
                    NewNodeList([updatedDeclaration]),
                    node.AsVariableDeclarationList().Flags));
            statement.Loc = node.Loc;
            return statement;
        }
        var updatedExpression = NewAssignmentExpression(node, boundValue);
        updatedExpression.Loc = node.Loc;
        var exprStatement = NewExpressionStatement(updatedExpression);
        exprStatement.Loc = node.Loc;
        return exprStatement;
    }

    public Node NewTypeCheck(Node value, string tag)
    {
        if (tag == "null")
        {
            return NewStrictEqualityExpression(value, NewKeywordExpression(SyntaxKind.NullKeyword));
        }
        else if (tag == "undefined")
        {
            return NewStrictEqualityExpression(value, NewVoidZeroExpression());
        }
        else
        {
            return NewStrictEqualityExpression(NewTypeOfExpression(value), NewStringLiteral(tag, TokenFlags.None));
        }
    }

    public Node NewMethodCall(Node @object, Node methodName, Node[] argumentsList)
    {
        // Preserve the optionality of `object`.
        if (NodeTests.IsCallExpression(@object) && (@object.Flags & NodeFlags.OptionalChain) != 0)
        {
            return NewCallExpression(
                NewPropertyAccessExpression(@object, null, methodName, NodeFlags.None),
                null,
                null,
                NewNodeList(argumentsList),
                NodeFlags.OptionalChain);
        }
        return NewCallExpression(
            NewPropertyAccessExpression(@object, null, methodName, NodeFlags.None),
            null,
            null,
            NewNodeList(argumentsList),
            NodeFlags.None);
    }

    public Node NewGlobalMethodCall(string globalObjectName, string methodName, Node[] argumentsList)
    {
        return NewMethodCall(NewIdentifier(globalObjectName), NewIdentifier(methodName), argumentsList);
    }

    public Node NewFunctionCallCall(Expression target, Expression? thisArg, Node[] argumentsList)
    {
        if (thisArg is null)
        {
            throw new InvalidOperationException("Attempted to construct function call call without this argument expression");
        }
        var args = CoreUtil.Concatenate(new[] { thisArg }, argumentsList)!;
        return NewMethodCall(target, NewIdentifier("call"), args);
    }

    public Node NewArraySliceCall(Expression array, int start)
    {
        Node[]? args = null;
        if (start != 0)
        {
            args = [.. args ?? [], NewNumericLiteral(start.ToString(CultureInfo.InvariantCulture), TokenFlags.None)];
        }
        return NewMethodCall(array, NewIdentifier("slice"), args ?? []);
    }

    // Determines whether a node is a parenthesized expression that can be ignored when recreating outer expressions.
    //
    // A parenthesized expression can be ignored when all of the following are true:
    //
    // - It's `pos` and `end` are not -1
    // - It does not have a custom source map range
    // - It does not have a custom comment range
    // - It does not have synthetic leading or trailing comments
    //
    // If an outermost parenthesized expression is ignored, but the containing expression requires a parentheses around
    // the expression to maintain precedence, a new parenthesized expression should be created automatically when
    // the containing expression is created/updated.
    private bool IsIgnorableParen(Expression node)
    {
        return NodeTests.IsParenthesizedExpression(node) &&
            AstUtilities.NodeIsSynthesized(node) &&
            AstUtilities.RangeIsSynthesized(emitContext.SourceMapRange(node)) &&
            AstUtilities.RangeIsSynthesized(emitContext.CommentRange(node)); // &&
        // len(emitContext.SyntheticLeadingComments(node)) == 0 &&
        // len(emitContext.SyntheticTrailingComments(node)) == 0
    }

    private Expression UpdateOuterExpression(Expression outerExpression /*OuterExpression*/, Expression expression)
    {
        switch (outerExpression.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return UpdateParenthesizedExpression(outerExpression.AsParenthesizedExpression(), expression);
            case SyntaxKind.TypeAssertionExpression:
                return UpdateTypeAssertion(outerExpression.AsTypeAssertion(), outerExpression.Type(), expression);
            case SyntaxKind.AsExpression:
                return UpdateAsExpression(outerExpression.AsAsExpression(), expression, outerExpression.Type());
            case SyntaxKind.SatisfiesExpression:
                return UpdateSatisfiesExpression(outerExpression.AsSatisfiesExpression(), expression, outerExpression.Type());
            case SyntaxKind.NonNullExpression:
                return UpdateNonNullExpression(outerExpression.AsNonNullExpression(), expression, outerExpression.Flags);
            case SyntaxKind.ExpressionWithTypeArguments:
                return UpdateExpressionWithTypeArguments(outerExpression.AsExpressionWithTypeArguments(), expression, outerExpression.TypeArgumentList());
            case SyntaxKind.PartiallyEmittedExpression:
                return UpdatePartiallyEmittedExpression(outerExpression.AsPartiallyEmittedExpression(), expression);
            default:
                throw new InvalidOperationException($"Unexpected outer expression kind: {outerExpression.Kind}");
        }
    }

    public Expression RestoreOuterExpressions(Expression? outerExpression, Expression innerExpression, OuterExpressionKinds kinds)
    {
        if (outerExpression is not null && AstUtilities.IsOuterExpression(outerExpression, kinds) && !IsIgnorableParen(outerExpression))
        {
            return UpdateOuterExpression(
                outerExpression,
                RestoreOuterExpressions(outerExpression.Expression(), innerExpression, OuterExpressionKinds.All));
        }
        return innerExpression;
    }

    // Ensures `"use strict"` is the first statement of a slice of statements.
    public Statement[] EnsureUseStrict(Statement[] statements)
    {
        foreach (var statement in statements)
        {
            if (AstUtilities.IsPrologueDirective(statement) && statement.Expression()!.Text() == "use strict")
            {
                return statements;
            }
            else
            {
                break;
            }
        }
        var useStrictPrologue = NewExpressionStatement(NewStringLiteral("use strict", TokenFlags.None));
        statements = CoreUtil.Concatenate(new[] { useStrictPrologue }, statements)!;
        return statements;
    }

    // Splits a slice of statements into two parts: standard prologue statements and the rest of the statements
    // PORT-NOTE: Go multi-return (prologue, rest) → out parameter for `rest`, return value for `prologue`.
    public Statement[] SplitStandardPrologue(Statement[] source, out Statement[] rest)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (!AstUtilities.IsPrologueDirective(source[i]))
            {
                rest = source[i..];
                return source[..i];
            }
        }
        rest = [];
        return source;
    }

    // Splits a slice of statements into two parts: custom prologue statements (e.g., with `EFCustomPrologue` set) and the rest of the statements
    // PORT-NOTE: Go multi-return (prologue, rest) → out parameter for `rest`, return value for `prologue`.
    public Statement[]? SplitCustomPrologue(Statement[] source, out Statement[] rest)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (AstUtilities.IsPrologueDirective(source[i]) || (emitContext.EmitFlags(source[i]) & EF.CustomPrologue) == 0)
            {
                rest = source[i..];
                return source[..i];
            }
        }
        rest = source;
        return null;
    }

    //
    // Declaration Names
    //

    private Node GetName(Declaration? node, EmitFlags emitFlags, AssignedNameOptions opts)
    {
        IdentifierNode? nodeName = null;
        if (node is not null)
        {
            if (opts.IgnoreAssignedName)
            {
                nodeName = AstUtilities.GetNonAssignedNameOfDeclaration(node);
            }
            else
            {
                nodeName = AstUtilities.GetNameOfDeclaration(node);
            }
        }

        if (nodeName is not null)
        {
            var name = nodeName.Clone(this)!;
            if (!opts.AllowComments)
            {
                emitFlags |= EF.NoComments;
            }
            if (!opts.AllowSourceMaps)
            {
                emitFlags |= EF.NoSourceMap;
            }
            emitContext.AddEmitFlags(name, emitFlags);
            return name;
        }

        return NewGeneratedNameForNode(node!);
    }

    // Gets the local name of a declaration. This is primarily used for declarations that can be referred to by name in the
    // declaration's immediate scope (classes, enums, namespaces). A local name will *never* be prefixed with a module or
    // namespace export modifier like "exports." when emitted as an expression.
    public Node GetLocalName(Declaration node) => GetLocalNameEx(node, new AssignedNameOptions());

    // Gets the local name of a declaration. This is primarily used for declarations that can be referred to by name in the
    // declaration's immediate scope (classes, enums, namespaces). A local name will *never* be prefixed with a module or
    // namespace export modifier like "exports." when emitted as an expression.
    public Node GetLocalNameEx(Declaration node, AssignedNameOptions opts) => GetName(node, EF.LocalName, opts);

    // Gets the export name of a declaration. This is primarily used for declarations that can be
    // referred to by name in the declaration's immediate scope (classes, enums, namespaces). An
    // export name will *always* be prefixed with an module or namespace export modifier like
    // `"exports."` when emitted as an expression if the name points to an exported symbol.
    public Node GetExportName(Declaration node) => GetExportNameEx(node, new AssignedNameOptions());

    // Gets the export name of a declaration. This is primarily used for declarations that can be
    // referred to by name in the declaration's immediate scope (classes, enums, namespaces). An
    // export name will *always* be prefixed with an module or namespace export modifier like
    // `"exports."` when emitted as an expression if the name points to an exported symbol.
    public Node GetExportNameEx(Declaration node, AssignedNameOptions opts) => GetName(node, EF.ExportName, opts);

    // Gets the name of a declaration to use during emit.
    public Node GetDeclarationName(Declaration node) => GetDeclarationNameEx(node, new NameOptions());

    // Gets the name of a declaration to use during emit.
    public Node GetDeclarationNameEx(Declaration node, NameOptions opts) =>
        GetName(node, EF.None, new AssignedNameOptions { AllowComments = opts.AllowComments, AllowSourceMaps = opts.AllowSourceMaps });

    public Node GetNamespaceMemberName(IdentifierNode ns, IdentifierNode name, NameOptions opts)
    {
        if (!emitContext.HasAutoGenerateInfo(name))
        {
            name = name.Clone(this)!;
        }
        var qualifiedName = NewPropertyAccessExpression(ns, null /*questionDotToken*/, name, NodeFlags.None);
        emitContext.AssignCommentAndSourceMapRanges(qualifiedName, name);
        if (!opts.AllowComments)
        {
            emitContext.AddEmitFlags(qualifiedName, EF.NoComments);
        }
        if (!opts.AllowSourceMaps)
        {
            emitContext.AddEmitFlags(qualifiedName, EF.NoSourceMap);
        }
        return qualifiedName;
    }

    // Gets the export name of a declaration for use in expressions.
    //
    // An export name will *always* be prefixed with a module or namespace export modifier like
    // `"exports."` when emitted as an expression if the name points to an exported symbol.
    public Node GetExternalModuleOrNamespaceExportName(IdentifierNode? ns, Declaration node, bool allowComments, bool allowSourceMaps)
    {
        if (ns is not null && AstUtilities.HasSyntacticModifier(node, ModifierFlags.Export))
        {
            var nameOpts = new NameOptions { AllowComments = allowComments, AllowSourceMaps = allowSourceMaps };
            return GetNamespaceMemberName(ns, GetDeclarationNameEx(node, nameOpts), nameOpts);
        }
        return GetExportNameEx(node, new AssignedNameOptions { AllowComments = allowComments, AllowSourceMaps = allowSourceMaps });
    }

    //
    // Emit Helpers
    //

    // Allocates a new Identifier representing a reference to a helper function.
    public Node NewUnscopedHelperName(string name)
    {
        var node = NewIdentifier(name);
        emitContext.SetEmitFlags(node, EF.HelperName);
        return node;
    }

    // TypeScript Helpers

    public Node NewDecorateHelper(Node[] decoratorExpressions, Node target, Node? memberName, Node? descriptor)
    {
        emitContext.RequestEmitHelper(Helpers.DecorateHelper);

        Node[]? argumentsArray = null;
        argumentsArray = [.. argumentsArray ?? [], NewArrayLiteralExpression(NewNodeList(decoratorExpressions), true)];
        argumentsArray = [.. argumentsArray ?? [], target];
        if (memberName is not null)
        {
            argumentsArray = [.. argumentsArray ?? [], memberName];
            if (descriptor is not null)
            {
                argumentsArray = [.. argumentsArray ?? [], descriptor];
            }
        }

        return NewCallExpression(
            NewUnscopedHelperName("__decorate"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList(argumentsArray ?? []),
            NodeFlags.None);
    }

    public Node NewMetadataHelper(string metadataKey, Node metadataValue)
    {
        emitContext.RequestEmitHelper(Helpers.MetadataHelper);

        return NewCallExpression(
            NewUnscopedHelperName("__metadata"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([
                NewStringLiteral(metadataKey, TokenFlags.None),
                metadataValue,
            ]),
            NodeFlags.None);
    }

    public Node NewParamHelper(Node expression, int parameterOffset, TextRange location)
    {
        emitContext.RequestEmitHelper(Helpers.ParamHelper);
        var helper = NewCallExpression(
            NewUnscopedHelperName("__param"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([NewNumericLiteral(parameterOffset.ToString(CultureInfo.InvariantCulture), TokenFlags.None), expression]),
            NodeFlags.None);
        helper.Loc = location;
        return helper;
    }

    // ESNext Helpers

    public Node NewAddDisposableResourceHelper(Expression envBinding, Expression value, bool async)
    {
        emitContext.RequestEmitHelper(Helpers.AddDisposableResourceHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__addDisposableResource"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([envBinding, value, NewKeywordExpression(CoreUtil.IfElse(async, SyntaxKind.TrueKeyword, SyntaxKind.FalseKeyword))]),
            NodeFlags.None);
    }

    public Node NewDisposeResourcesHelper(Expression envBinding)
    {
        emitContext.RequestEmitHelper(Helpers.DisposeResourcesHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__disposeResources"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([envBinding]),
            NodeFlags.None);
    }

    // Class Fields Helpers

    public Node NewClassPrivateFieldGetHelper(Expression receiver, IdentifierNode state, PrivateIdentifierKind kind, IdentifierNode? fn)
    {
        emitContext.RequestEmitHelper(Helpers.ClassPrivateFieldGetHelper);
        Node[] args;
        if (fn is null)
        {
            args = [receiver, state, NewStringLiteral((string)kind, TokenFlags.None)];
        }
        else
        {
            args = [receiver, state, NewStringLiteral((string)kind, TokenFlags.None), fn];
        }
        return NewCallExpression(
            NewUnscopedHelperName("__classPrivateFieldGet"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList(args),
            NodeFlags.None);
    }

    public Node NewClassPrivateFieldSetHelper(Expression receiver, IdentifierNode state, Expression value, PrivateIdentifierKind kind, IdentifierNode? fn)
    {
        emitContext.RequestEmitHelper(Helpers.ClassPrivateFieldSetHelper);
        Node[] args;
        if (fn is null)
        {
            args = [receiver, state, value, NewStringLiteral((string)kind, TokenFlags.None)];
        }
        else
        {
            args = [receiver, state, value, NewStringLiteral((string)kind, TokenFlags.None), fn];
        }
        return NewCallExpression(
            NewUnscopedHelperName("__classPrivateFieldSet"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList(args),
            NodeFlags.None);
    }

    public Node NewClassPrivateFieldInHelper(IdentifierNode state, Expression receiver)
    {
        emitContext.RequestEmitHelper(Helpers.ClassPrivateFieldInHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__classPrivateFieldIn"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([state, receiver]),
            NodeFlags.None);
    }

    // Creates `Object.defineProperty(target, name, descriptor)`.
    public Node NewObjectDefinePropertyCall(Expression target, Expression name, Expression descriptor)
    {
        return NewCallExpression(
            NewPropertyAccessExpression(
                NewIdentifier("Object"),
                null,
                NewIdentifier("defineProperty"),
                NodeFlags.None),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([target, name, descriptor]),
            NodeFlags.None);
    }

    // Creates `Reflect.get(target, propertyKey, receiver)`.
    public Node NewReflectGetCall(Expression target, Expression propertyKey, Expression receiver)
    {
        return NewCallExpression(
            NewPropertyAccessExpression(
                NewIdentifier("Reflect"),
                null,
                NewIdentifier("get"),
                NodeFlags.None),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([target, propertyKey, receiver]),
            NodeFlags.None);
    }

    // Creates `Reflect.set(target, propertyKey, value, receiver)`.
    public Node NewReflectSetCall(Expression target, Expression propertyKey, Expression value, Expression receiver)
    {
        return NewCallExpression(
            NewPropertyAccessExpression(
                NewIdentifier("Reflect"),
                null,
                NewIdentifier("set"),
                NodeFlags.None),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([target, propertyKey, value, receiver]),
            NodeFlags.None);
    }

    // Creates `target.bind(thisArg, ...args)`.
    public Node NewFunctionBindCall(Expression target, Expression thisArg, Node[] argumentsList)
    {
        var args = new List<Node>(1 + argumentsList.Length);
        args.Add(thisArg);
        args.AddRange(argumentsList);
        return NewMethodCall(target, NewIdentifier("bind"), args.ToArray());
    }

    // Creates `(() => { ...statements })()` — an immediately invoked arrow function.
    public Node NewImmediatelyInvokedArrowFunction(Statement[] statements)
    {
        var arrow = NewArrowFunction(
            null,                        /*modifiers*/
            null,                        /*typeParameters*/
            NewNodeList([]),             /*parameters*/
            null,                        /*returnType*/
            null,                        /*fullSignature*/
            NewToken(SyntaxKind.EqualsGreaterThanToken), /*equalsGreaterThanToken*/
            NewBlock(NewNodeList(statements), true));
        return NewCallExpression(
            NewParenthesizedExpression(arrow),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([]),
            NodeFlags.None);
    }

    // Creates `export default <expression>;`.
    public Node NewExportDefault(Expression expression)
    {
        return NewExportAssignment(null, false, null, expression);
    }

    // Creates `export { <name> };`.
    public Node NewExternalModuleExport(IdentifierNode name)
    {
        var specifier = NewExportSpecifier(false, null, name);
        var namedExports = NewNamedExports(NewNodeList([specifier]));
        return NewExportDeclaration(null, false, namedExports, null, null);
    }

    // ES2018 Helpers
    // Chains a sequence of expressions using the __assign helper or Object.assign if available in the target
    public Node NewAssignHelper(Expression[] attributesSegments, ScriptTarget scriptTarget)
    {
        return NewCallExpression(NewPropertyAccessExpression(NewIdentifier("Object"), null, NewIdentifier("assign"), NodeFlags.None), null, null, NewNodeList(attributesSegments), NodeFlags.None);
    }

    // ES2018 Destructuring Helpers

    public Node NewRestHelper(Expression value, Node[] elements, Node[]? computedTempVariables, TextRange location)
    {
        emitContext.RequestEmitHelper(Helpers.RestHelper);
        Node[]? propertyNames = null;
        int computedTempVariableOffset = 0;
        for (int i = 0; i < elements.Length; i++)
        {
            var element = elements[i];
            if (i == elements.Length - 1)
            {
                break;
            }
            var propertyName = PrinterStubs.TryGetPropertyNameOfBindingOrAssignmentElement(element);
            if (propertyName is not null)
            {
                if (NodeTests.IsComputedPropertyName(propertyName))
                {
                    Debug.Assert(computedTempVariables is not null, "Encountered computed property name but 'computedTempVariables' argument was not provided.");
                    var temp = computedTempVariables![computedTempVariableOffset];
                    computedTempVariableOffset++;
                    // typeof _tmp === "symbol" ? _tmp : _tmp + ""
                    propertyNames = [.. propertyNames ?? [], NewConditionalExpression(
                        NewTypeCheck(temp, "symbol"),
                        NewToken(SyntaxKind.QuestionToken),
                        temp,
                        NewToken(SyntaxKind.ColonToken),
                        NewBinaryExpression(null, temp, null, NewToken(SyntaxKind.PlusToken), NewStringLiteral("", TokenFlags.None)))];
                }
                else
                {
                    propertyNames = [.. propertyNames ?? [], NewStringLiteralFromNode(propertyName)];
                }
            }
        }
        var propNames = NewArrayLiteralExpression(NewNodeList(propertyNames ?? []), false);
        propNames.Loc = location;
        return NewCallExpression(
            NewUnscopedHelperName("__rest"),
            null,
            null,
            NewNodeList([
                value,
                propNames,
            ]),
            NodeFlags.None);
    }

    // ES2018 Helpers

    // Allocates a new Call expression to the `__await` helper.
    public Node NewAwaitHelper(Expression expression)
    {
        emitContext.RequestEmitHelper(Helpers.AwaitHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__await"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([expression]),
            NodeFlags.None);
    }

    // Allocates a new Call expression to the `__asyncGenerator` helper.
    public Node NewAsyncGeneratorHelper(
        Expression generatorFunc,
        bool hasLexicalThis)
    {
        emitContext.RequestEmitHelper(Helpers.AwaitHelper);
        emitContext.RequestEmitHelper(Helpers.AsyncGeneratorHelper);

        // Mark this node as originally an async function body
        emitContext.AddEmitFlags(generatorFunc, EF.AsyncFunctionBody | EF.ReuseTempVariableScope);

        Expression thisArg;
        if (hasLexicalThis)
        {
            thisArg = NewKeywordExpression(SyntaxKind.ThisKeyword);
        }
        else
        {
            thisArg = NewVoidZeroExpression();
        }

        return NewCallExpression(
            NewUnscopedHelperName("__asyncGenerator"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([
                thisArg,
                NewIdentifier("arguments"),
                generatorFunc,
            ]),
            NodeFlags.None);
    }

    // Allocates a new Call expression to the `__asyncDelegator` helper.
    public Node NewAsyncDelegatorHelper(Expression expression)
    {
        emitContext.RequestEmitHelper(Helpers.AwaitHelper);
        emitContext.RequestEmitHelper(Helpers.AsyncDelegatorHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__asyncDelegator"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([expression]),
            NodeFlags.None);
    }

    // Allocates a new Call expression to the `__asyncValues` helper.
    public Node NewAsyncValuesHelper(Expression expression)
    {
        emitContext.RequestEmitHelper(Helpers.AsyncValuesHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__asyncValues"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([expression]),
            NodeFlags.None);
    }

    // !!! ES2017 Helpers

    // Allocates a new Call expression to the `__awaiter` helper.
    public Node NewAwaiterHelper(
        bool hasLexicalThis,
        Expression? argumentsExpression,
        NodeList? parameters,
        BlockNode? body)
    {
        emitContext.RequestEmitHelper(Helpers.AwaiterHelper);

        NodeList? @params;
        if (parameters is not null)
        {
            @params = parameters;
        }
        else
        {
            @params = NewNodeList([]);
        }

        var generatorFunc = NewFunctionExpression(
            null, /*modifiers*/
            NewToken(SyntaxKind.AsteriskToken),
            null, /*name*/
            null, /*typeParameters*/
            @params,
            null, /*returnType*/
            null, /*fullSignature*/
            body);

        // Mark this node as originally an async function body
        emitContext.AddEmitFlags(generatorFunc, EF.AsyncFunctionBody | EF.ReuseTempVariableScope);

        Expression thisArg;
        if (hasLexicalThis)
        {
            thisArg = NewKeywordExpression(SyntaxKind.ThisKeyword);
        }
        else
        {
            thisArg = NewVoidZeroExpression();
        }

        Expression argsArg;
        if (argumentsExpression is not null)
        {
            argsArg = argumentsExpression;
        }
        else
        {
            argsArg = NewVoidZeroExpression();
        }

        return NewCallExpression(
            NewUnscopedHelperName("__awaiter"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([
                thisArg,
                argsArg,
                NewVoidZeroExpression(),
                generatorFunc,
            ]),
            NodeFlags.None);
    }

    // ES Decorator Helpers

    public Node NewESDecorateClassContextObject(Expression nameExpr, IdentifierNode metadata)
    {
        Node[] props = [
            NewPropertyAssignment(null, NewIdentifier("kind"), null, null, NewStringLiteral("class", 0)),
            NewPropertyAssignment(null, NewIdentifier("name"), null, null, nameExpr),
            NewPropertyAssignment(null, NewIdentifier("metadata"), null, null, metadata),
        ];
        return NewObjectLiteralExpression(NewNodeList(props), false);
    }

    public Node NewESDecorateClassElementAccessGetMethod(
        bool nameComputed,
        Expression nameExpr)
    {
        Expression accessor;
        if (nameComputed)
        {
            accessor = NewElementAccessExpression(NewIdentifier("obj"), null, nameExpr, NodeFlags.None);
        }
        else
        {
            accessor = NewPropertyAccessExpression(NewIdentifier("obj"), null, nameExpr, NodeFlags.None);
        }

        var objParam = NewParameterDeclaration(null, null, NewIdentifier("obj"), null, null, null);

        var arrow = NewArrowFunction(
            null, null,
            NewNodeList([objParam]),
            null, null,
            NewToken(SyntaxKind.EqualsGreaterThanToken),
            accessor);

        return NewPropertyAssignment(null, NewIdentifier("get"), null, null, arrow);
    }

    public Node NewESDecorateClassElementAccessSetMethod(
        bool nameComputed,
        Expression nameExpr)
    {
        Expression accessor;
        if (nameComputed)
        {
            accessor = NewElementAccessExpression(NewIdentifier("obj"), null, nameExpr, NodeFlags.None);
        }
        else
        {
            accessor = NewPropertyAccessExpression(NewIdentifier("obj"), null, nameExpr, NodeFlags.None);
        }

        var assignment = NewAssignmentExpression(accessor, NewIdentifier("value"));
        var stmt = NewExpressionStatement(assignment);
        var body = NewBlock(NewNodeList([stmt]), false);

        var objParam = NewParameterDeclaration(null, null, NewIdentifier("obj"), null, null, null);
        var valueParam = NewParameterDeclaration(null, null, NewIdentifier("value"), null, null, null);

        var arrow = NewArrowFunction(
            null, null,
            NewNodeList([objParam, valueParam]),
            null, null,
            NewToken(SyntaxKind.EqualsGreaterThanToken),
            body);

        return NewPropertyAssignment(null, NewIdentifier("set"), null, null, arrow);
    }

    public Node NewESDecorateClassElementAccessHasMethod(
        bool nameComputed,
        Expression? nameExpr)
    {
        // The property name for the "in" expression
        Expression? propertyName;
        if (!nameComputed && nameExpr is not null && NodeTests.IsIdentifier(nameExpr))
        {
            propertyName = NewStringLiteralFromNode(nameExpr);
        }
        else
        {
            propertyName = nameExpr;
        }

        var objParam = NewParameterDeclaration(null, null, NewIdentifier("obj"), null, null, null);
        var inExpr = NewBinaryExpression(null, propertyName, null, NewToken(SyntaxKind.InKeyword), NewIdentifier("obj"));

        var arrow = NewArrowFunction(
            null, null,
            NewNodeList([objParam]),
            null, null,
            NewToken(SyntaxKind.EqualsGreaterThanToken),
            inExpr);

        return NewPropertyAssignment(null, NewIdentifier("has"), null, null, arrow);
    }

    // Creates the "access" object for a class element decorator context.
    //
    // 15.7.3 CreateDecoratorAccessObject (kind, name)
    //
    //  2. If _kind_ is ~field~, ~method~, ~accessor~, or ~getter~, then
    //     a. Let _getAccess_ be a new Abstract Closure with parameters (_object_) that captures _kind_ and _name_ ...
    //     b. Perform ! CreateDataPropertyOrThrow(_access_, "get", _getAccess_).
    //  3. If _kind_ is ~field~, ~accessor~, or ~setter~, then
    //     a. Let _setAccess_ be a new Abstract Closure with parameters (_object_, _value_) that captures _kind_ and _name_ ...
    //     b. Perform ! CreateDataPropertyOrThrow(_access_, "set", _setAccess_).
    public Node NewESDecorateClassElementAccessObject(
        bool nameComputed,
        Expression nameExpr,
        bool hasGet,
        bool hasSet)
    {
        Node[] accessProps = [];

        // "has" method: obj => name in obj
        accessProps = [.. accessProps, NewESDecorateClassElementAccessHasMethod(nameComputed, nameExpr)];

        // "get" method: obj => obj.name or obj => obj[name]
        if (hasGet)
        {
            accessProps = [.. accessProps, NewESDecorateClassElementAccessGetMethod(nameComputed, nameExpr)];
        }

        // "set" method: (obj, value) => { obj.name = value; } or (obj, value) => { obj[name] = value; }
        if (hasSet)
        {
            accessProps = [.. accessProps, NewESDecorateClassElementAccessSetMethod(nameComputed, nameExpr)];
        }

        return NewObjectLiteralExpression(NewNodeList(accessProps), false);
    }

    public Node NewESDecorateClassElementContextObject(
        string kind,
        bool nameComputed,
        Expression nameExpr,
        bool isStatic,
        bool isPrivate,
        bool hasGet,
        bool hasSet,
        IdentifierNode metadata)
    {
        // Build the name value for the context's "name" property
        Expression? nameValue;
        if (!nameComputed && nameExpr is not null && (NodeTests.IsPrivateIdentifier(nameExpr) || NodeTests.IsIdentifier(nameExpr)))
        {
            nameValue = NewStringLiteralFromNode(nameExpr);
        }
        else
        {
            nameValue = nameExpr;
        }

        // Build the access object with has/get/set arrow functions
        var accessObj = NewESDecorateClassElementAccessObject(nameComputed, nameExpr!, hasGet, hasSet);

        Node staticExpr;
        if (isStatic)
        {
            staticExpr = NewTrueExpression();
        }
        else
        {
            staticExpr = NewFalseExpression();
        }

        Node privateExpr;
        if (isPrivate)
        {
            privateExpr = NewTrueExpression();
        }
        else
        {
            privateExpr = NewFalseExpression();
        }

        Node[] props = [
            NewPropertyAssignment(null, NewIdentifier("kind"), null, null, NewStringLiteral(kind, 0)),
            NewPropertyAssignment(null, NewIdentifier("name"), null, null, nameValue),
            NewPropertyAssignment(null, NewIdentifier("static"), null, null, staticExpr),
            NewPropertyAssignment(null, NewIdentifier("private"), null, null, privateExpr),
            NewPropertyAssignment(null, NewIdentifier("access"), null, null, accessObj),
            NewPropertyAssignment(null, NewIdentifier("metadata"), null, null, metadata),
        ];
        return NewObjectLiteralExpression(NewNodeList(props), false);
    }

    public Node NewESDecorateHelper(Expression ctor, Expression descriptorIn, Expression decorators, Expression contextIn, Expression initializers, Expression extraInitializers)
    {
        emitContext.RequestEmitHelper(Helpers.EsDecorateHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__esDecorate"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([ctor, descriptorIn, decorators, contextIn, initializers, extraInitializers]),
            NodeFlags.None);
    }

    public Node NewRunInitializersHelper(Expression thisArg, Expression initializers, Expression? value)
    {
        emitContext.RequestEmitHelper(Helpers.RunInitializersHelper);
        Expression[] arguments;
        if (value is not null)
        {
            arguments = [thisArg, initializers, value];
        }
        else
        {
            arguments = [thisArg, initializers];
        }
        return NewCallExpression(
            NewUnscopedHelperName("__runInitializers"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList(arguments),
            NodeFlags.None);
    }

    // ES2015 Helpers

    public Node NewTemplateObjectHelper(Expression cookedArray, Expression rawArray)
    {
        emitContext.RequestEmitHelper(Helpers.MakeTemplateObjectHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__makeTemplateObject"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([cookedArray, rawArray]),
            NodeFlags.None);
    }

    public Node NewPropKeyHelper(Expression expr)
    {
        emitContext.RequestEmitHelper(Helpers.PropKeyHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__propKey"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([expr]),
            NodeFlags.None);
    }

    public Node NewSetFunctionNameHelper(Expression fn, Expression name, string prefix)
    {
        emitContext.RequestEmitHelper(Helpers.SetFunctionNameHelper);
        Expression[] arguments;
        if (prefix.Length > 0)
        {
            arguments = [fn, name, NewStringLiteral(prefix, TokenFlags.None)];
        }
        else
        {
            arguments = [fn, name];
        }
        return NewCallExpression(
            NewUnscopedHelperName("__setFunctionName"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList(arguments),
            NodeFlags.None);
    }

    // ES Module Helpers

    // Allocates a new Call expression to the `__importDefault` helper.
    public Node NewImportDefaultHelper(Expression expression)
    {
        emitContext.RequestEmitHelper(Helpers.ImportDefaultHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__importDefault"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([expression]),
            NodeFlags.None);
    }

    // Allocates a new Call expression to the `__importStar` helper.
    public Node NewImportStarHelper(Expression expression)
    {
        emitContext.RequestEmitHelper(Helpers.ImportStarHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__importStar"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([expression]),
            NodeFlags.None);
    }

    // Allocates a new Call expression to the `__exportStar` helper.
    public Node NewExportStarHelper(Expression moduleExpression, Expression exportsExpression)
    {
        emitContext.RequestEmitHelper(Helpers.ExportStarHelper);
        return NewCallExpression(
            NewUnscopedHelperName("__exportStar"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList([moduleExpression, exportsExpression]),
            NodeFlags.None);
    }

    public Node NewAssignmentTargetWrapper(IdentifierNode paramName, Expression expression)
    {
        var setAccessor = NewSetAccessorDeclaration(
            null, /*modifiers*/
            NewIdentifier("value"),
            null, /*typeParameters*/
            NewNodeList([
                NewParameterDeclaration(null, null, paramName, null, null, null),
            ]),
            null, /*returnType*/
            null, /*fullSignature*/
            NewBlock(NewNodeList([
                NewExpressionStatement(expression),
            ]), false));
        var objLiteral = NewObjectLiteralExpression(NewNodeList([setAccessor]), false);
        // Explicit parens required because of v8 regression (https://bugs.chromium.org/p/v8/issues/detail?id=9560)
        return NewPropertyAccessExpression(
            NewParenthesizedExpression(objLiteral),
            null, /*questionDotToken*/
            NewIdentifier("value"),
            NodeFlags.None);
    }

    // Allocates a new Call expression to the `__rewriteRelativeImportExtension` helper.
    public Node NewRewriteRelativeImportExtensionsHelper(Node firstArgument, bool preserveJsx)
    {
        emitContext.RequestEmitHelper(Helpers.RewriteRelativeImportExtensionsHelper);
        Expression[] arguments;
        if (preserveJsx)
        {
            arguments = [firstArgument, NewToken(SyntaxKind.TrueKeyword)];
        }
        else
        {
            arguments = [firstArgument];
        }
        return NewCallExpression(
            NewUnscopedHelperName("__rewriteRelativeImportExtension"),
            null, /*questionDotToken*/
            null, /*typeArguments*/
            NewNodeList(arguments),
            NodeFlags.None);
    }
}

// Go: factory.go NameOptions / AssignedNameOptions / PrivateIdentifierKind + its consts.

public struct NameOptions
{
    public bool AllowComments;   // indicates whether comments may be emitted for the name.
    public bool AllowSourceMaps; // indicates whether source maps may be emitted for the name.
}

public struct AssignedNameOptions
{
    public bool AllowComments;      // indicates whether comments may be emitted for the name.
    public bool AllowSourceMaps;    // indicates whether source maps may be emitted for the name.
    public bool IgnoreAssignedName; // indicates whether the assigned name of a declaration shouldn't be considered.
}

// Class Fields Helpers
// Go: type PrivateIdentifierKind string
public readonly struct PrivateIdentifierKind
{
    private readonly string value;

    private PrivateIdentifierKind(string value)
    {
        this.value = value;
    }

    public static explicit operator string(PrivateIdentifierKind kind) => kind.value ?? "";

    public static readonly PrivateIdentifierKind Field = new("f");
    public static readonly PrivateIdentifierKind Method = new("m");
    public static readonly PrivateIdentifierKind Accessor = new("a");
    public static readonly PrivateIdentifierKind Untransformed = new("untransformed");

    // Go strings compare with ==; mirror that for the C# value wrapper.
    public bool Equals(PrivateIdentifierKind other) => string.Equals(value, other.value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is PrivateIdentifierKind other && Equals(other);
    public override int GetHashCode() => value?.GetHashCode(StringComparison.Ordinal) ?? 0;
    public static bool operator ==(PrivateIdentifierKind left, PrivateIdentifierKind right) => left.Equals(right);
    public static bool operator !=(PrivateIdentifierKind left, PrivateIdentifierKind right) => !left.Equals(right);
}
