// Ported from tsgo internal/transformers/utilities.go.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Transformers;

public static partial class Transformers
{
    public static bool IsGeneratedIdentifier(global::Ts.Printer.EmitContext emitContext, IdentifierNode? name) =>
        emitContext.HasAutoGenerateInfo(name);

    public static bool IsHelperName(global::Ts.Printer.EmitContext emitContext, IdentifierNode name) =>
        (emitContext.EmitFlags(name) & global::Ts.Printer.EmitFlags.HelperName) != 0;

    public static bool IsLocalName(global::Ts.Printer.EmitContext emitContext, IdentifierNode name) =>
        (emitContext.EmitFlags(name) & global::Ts.Printer.EmitFlags.LocalName) != 0;

    public static bool IsExportName(global::Ts.Printer.EmitContext emitContext, IdentifierNode name) =>
        (emitContext.EmitFlags(name) & global::Ts.Printer.EmitFlags.ExportName) != 0;

    public static bool IsIdentifierReference(IdentifierNode name, Node parent)
    {
        switch (parent.Kind)
        {
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.PrefixUnaryExpression:
            case SyntaxKind.PostfixUnaryExpression:
            case SyntaxKind.YieldExpression:
            case SyntaxKind.AsExpression:
            case SyntaxKind.SatisfiesExpression:
            case SyntaxKind.ElementAccessExpression:
            case SyntaxKind.NonNullExpression:
            case SyntaxKind.SpreadElement:
            case SyntaxKind.SpreadAssignment:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.DeleteExpression:
            case SyntaxKind.TypeOfExpression:
            case SyntaxKind.VoidExpression:
            case SyntaxKind.AwaitExpression:
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.ExpressionWithTypeArguments:
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxSpreadAttribute:
            case SyntaxKind.JsxExpression:
            case SyntaxKind.PartiallyEmittedExpression:
                return true;

            case SyntaxKind.ComputedPropertyName:
            case SyntaxKind.Decorator:
            case SyntaxKind.IfStatement:
            case SyntaxKind.DoStatement:
            case SyntaxKind.WhileStatement:
            case SyntaxKind.WithStatement:
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.SwitchStatement:
            case SyntaxKind.CaseClause:
            case SyntaxKind.ThrowStatement:
            case SyntaxKind.ExpressionStatement:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.TemplateSpan:
                return parent.Expression() == name;

            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.BindingElement:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.EnumMember:
            case SyntaxKind.JsxAttribute:
                return parent.Initializer() == name;

            case SyntaxKind.ShorthandPropertyAssignment:
                return parent.AsShorthandPropertyAssignment().ObjectAssignmentInitializer == name;

            case SyntaxKind.ForStatement:
                return parent.Initializer() == name ||
                    parent.AsForStatement().Condition == name ||
                    parent.AsForStatement().Incrementor == name;

            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                return parent.Initializer() == name || parent.Expression() == name;

            case SyntaxKind.ImportEqualsDeclaration:
                return parent.AsImportEqualsDeclaration().ModuleReference == name;

            case SyntaxKind.ArrowFunction:
                return parent.Body() == name;

            case SyntaxKind.ConditionalExpression:
                return parent.AsConditionalExpression().Condition == name ||
                    parent.AsConditionalExpression().WhenTrue == name ||
                    parent.AsConditionalExpression().WhenFalse == name;

            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
                return parent.Expression() == name || Array.IndexOf(parent.Arguments() ?? [], name) >= 0;

            case SyntaxKind.TaggedTemplateExpression:
                return parent.AsTaggedTemplateExpression().Tag == name;

            case SyntaxKind.ImportAttribute:
                return parent.AsImportAttribute().Value == name;

            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxClosingElement:
                return parent.TagName() == name;

            default:
                return false;
        }
    }

    private static Expression ConvertBindingElementToArrayAssignmentElement(
        global::Ts.Printer.EmitContext emitContext,
        BindingElement element)
    {
        if (element.Name() is null)
        {
            var elision = emitContext.Factory.NewOmittedExpression();
            emitContext.SetOriginal(elision, element.AsNode());
            emitContext.AssignCommentAndSourceMapRanges(elision, element.AsNode());
            return elision;
        }
        if (element.AsBindingElement().DotDotDotToken is not null)
        {
            var spread = emitContext.Factory.NewSpreadElement(element.Name());
            emitContext.SetOriginal(spread, element.AsNode());
            emitContext.AssignCommentAndSourceMapRanges(spread, element.AsNode());
            return spread;
        }
        var expression = ConvertBindingNameToAssignmentElementTarget(emitContext, element.Name()!);
        if (element.Initializer() is { } initializer)
        {
            var assignment = emitContext.Factory.NewAssignmentExpression(expression, initializer);
            emitContext.SetOriginal(assignment, element.AsNode());
            emitContext.AssignCommentAndSourceMapRanges(assignment, element.AsNode());
            return assignment;
        }
        return expression;
    }

    private static ObjectLiteralElement ConvertBindingElementToObjectAssignmentElement(
        global::Ts.Printer.EmitContext emitContext,
        BindingElement element)
    {
        if (element.AsBindingElement().DotDotDotToken is not null)
        {
            var spread = emitContext.Factory.NewSpreadAssignment(element.Name());
            emitContext.SetOriginal(spread, element.AsNode());
            emitContext.AssignCommentAndSourceMapRanges(spread, element.AsNode());
            return spread;
        }
        if (element.AsBindingElement().PropertyName is { } propertyName)
        {
            var expression = ConvertBindingNameToAssignmentElementTarget(emitContext, element.Name()!);
            if (element.Initializer() is { } initializer)
            {
                expression = emitContext.Factory.NewAssignmentExpression(expression, initializer);
            }
            var assignment = emitContext.Factory.NewPropertyAssignment(null, propertyName, null, null, expression);
            emitContext.SetOriginal(assignment, element.AsNode());
            emitContext.AssignCommentAndSourceMapRanges(assignment, element.AsNode());
            return assignment;
        }

        TokenNode? equalsToken = element.Initializer() is not null
            ? emitContext.Factory.NewToken(SyntaxKind.EqualsToken)
            : null;
        var shorthand = emitContext.Factory.NewShorthandPropertyAssignment(
            null,
            element.Name(),
            null,
            null,
            equalsToken,
            element.Initializer());
        emitContext.SetOriginal(shorthand, element.AsNode());
        emitContext.AssignCommentAndSourceMapRanges(shorthand, element.AsNode());
        return shorthand;
    }

    public static Expression ConvertBindingPatternToAssignmentPattern(
        global::Ts.Printer.EmitContext emitContext,
        BindingPattern element)
    {
        return element.Kind switch
        {
            SyntaxKind.ArrayBindingPattern => ConvertBindingElementToArrayAssignmentPattern(emitContext, element),
            SyntaxKind.ObjectBindingPattern => ConvertBindingElementToObjectAssignmentPattern(emitContext, element),
            _ => throw new InvalidOperationException("Unknown binding pattern"),
        };
    }

    private static Expression ConvertBindingElementToObjectAssignmentPattern(
        global::Ts.Printer.EmitContext emitContext,
        BindingPattern element)
    {
        var sourceElements = element.AsBindingPattern().Elements?.Nodes ?? [];
        var properties = new Node[sourceElements.Length];
        for (var i = 0; i < sourceElements.Length; i++)
        {
            properties[i] = ConvertBindingElementToObjectAssignmentElement(emitContext, sourceElements[i].AsBindingElement());
        }
        var propertyList = emitContext.Factory.NewNodeList(properties);
        propertyList.Loc = element.AsBindingPattern().Elements!.Loc;
        var result = emitContext.Factory.NewObjectLiteralExpression(propertyList, false);
        emitContext.SetOriginal(result, element.AsNode());
        emitContext.AssignCommentAndSourceMapRanges(result, element.AsNode());
        return result;
    }

    private static Expression ConvertBindingElementToArrayAssignmentPattern(
        global::Ts.Printer.EmitContext emitContext,
        BindingPattern element)
    {
        var sourceElements = element.AsBindingPattern().Elements?.Nodes ?? [];
        var elements = new Node[sourceElements.Length];
        for (var i = 0; i < sourceElements.Length; i++)
        {
            elements[i] = ConvertBindingElementToArrayAssignmentElement(emitContext, sourceElements[i].AsBindingElement());
        }
        var elementList = emitContext.Factory.NewNodeList(elements);
        elementList.Loc = element.AsBindingPattern().Elements!.Loc;
        var result = emitContext.Factory.NewArrayLiteralExpression(elementList, false);
        emitContext.SetOriginal(result, element.AsNode());
        emitContext.AssignCommentAndSourceMapRanges(result, element.AsNode());
        return result;
    }

    private static Expression ConvertBindingNameToAssignmentElementTarget(
        global::Ts.Printer.EmitContext emitContext,
        Node element) =>
        global::Ts.Ast.AstUtilities.IsBindingPattern(element)
            ? ConvertBindingPatternToAssignmentPattern(emitContext, element.AsBindingPattern())
            : element;

    public static Expression? ConvertVariableDeclarationToAssignmentExpression(
        global::Ts.Printer.EmitContext emitContext,
        VariableDeclaration element)
    {
        if (element.Initializer() is null)
        {
            return null;
        }
        var expression = ConvertBindingNameToAssignmentElementTarget(emitContext, element.Name()!);
        var assignment = emitContext.Factory.NewAssignmentExpression(expression, element.Initializer()!);
        emitContext.SetOriginal(assignment, element.AsNode());
        emitContext.AssignCommentAndSourceMapRanges(assignment, element.AsNode());
        return assignment;
    }

    public static Node? SingleOrMany(Node[]? nodes, global::Ts.Printer.NodeFactory factory)
    {
        if (nodes is null)
        {
            return null;
        }
        return nodes.Length == 1 ? nodes[0] : factory.NewSyntaxList(nodes);
    }

    public static bool IsSimpleCopiableExpression(Expression expression) =>
        global::Ts.Ast.AstUtilities.IsStringLiteralLike(expression) ||
        global::Ts.Ast.NodeTests.IsNumericLiteral(expression) ||
        global::Ts.Ast.NodeTests.IsKeywordKind(expression.Kind) ||
        global::Ts.Ast.NodeTests.IsIdentifier(expression);

    public static bool IsOriginalNodeSingleLine(global::Ts.Printer.EmitContext emitContext, Node? node)
    {
        if (node is null || emitContext.MostOriginal(node) is not { } original)
        {
            return false;
        }
        var source = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(original);
        if (source is null)
        {
            return false;
        }
        var startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(source, original.Loc.Pos);
        var endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(source, original.Loc.End);
        return startLine == endLine;
    }

    /**
     * A simple inlinable expression is an expression which can be copied into multiple locations
     * without risk of repeating any sideeffects and whose value could not possibly change between
     * any such locations.
     */
    public static bool IsSimpleInlineableExpression(Expression expression) =>
        !global::Ts.Ast.NodeTests.IsIdentifier(expression) && IsSimpleCopiableExpression(expression);

    // FindSuperStatementIndexPath finds a path of indices to a statement containing a `super()` call.
    public static int[]? FindSuperStatementIndexPath(Statement[] statements, int start)
    {
        var indices = FindSuperStatementIndexPathWorker(statements, start, null);
        Array.Reverse(indices ?? []);
        return indices;
    }

    private static int[]? FindSuperStatementIndexPathWorker(Statement[] statements, int start, int[]? indices)
    {
        for (var i = start; i < statements.Length; i++)
        {
            var statement = statements[i];
            if (GetSuperCallFromStatement(statement) is not null)
            {
                return Append(indices, i);
            }
            if (global::Ts.Ast.NodeTests.IsTryStatement(statement))
            {
                var nested = statement.AsTryStatement().TryBlock!.Statements() ?? [];
                if (FindSuperStatementIndexPathWorker(nested, 0, indices) is { } result)
                {
                    return Append(result, i);
                }
            }
        }
        return null;
    }

    private static int[] Append(int[]? source, int value)
    {
        var result = new int[(source?.Length ?? 0) + 1];
        if (source is not null)
        {
            Array.Copy(source, result, source.Length);
        }
        result[^1] = value;
        return result;
    }

    // GetSuperCallFromStatement extracts the super() call expression from an expression statement, if any.
    public static Node? GetSuperCallFromStatement(Statement statement)
    {
        if (!global::Ts.Ast.NodeTests.IsExpressionStatement(statement))
        {
            return null;
        }
        var expression = global::Ts.Ast.AstUtilities.SkipParentheses(statement.Expression()!);
        return TransformerAstAdapters.IsSuperCall(expression) ? expression : null;
    }

    // MoveRangePastModifiers returns a text range that starts past any modifiers on the node.
    public static TextRange MoveRangePastModifiers(Node node)
    {
        if (global::Ts.Ast.NodeTests.IsPropertyDeclaration(node) || global::Ts.Ast.NodeTests.IsMethodDeclaration(node))
        {
            return new TextRange(node.Name()!.Pos, node.End);
        }
        var lastModifier = TransformerAstAdapters.CanHaveModifiers(node)
            ? global::Ts.Core.CoreUtil.LastOrNil(node.ModifierNodes())
            : null;
        if (lastModifier is not null && !global::Ts.Ast.AstUtilities.PositionIsSynthesized(lastModifier.End))
        {
            return new TextRange(lastModifier.End, node.End);
        }
        return MoveRangePastDecorators(node);
    }

    // MoveRangePastDecorators returns a text range that starts past any decorators on the node.
    public static TextRange MoveRangePastDecorators(Node node)
    {
        Node? lastDecorator = null;
        if (TransformerAstAdapters.CanHaveModifiers(node) && node.ModifierNodes() is { } nodes)
        {
            lastDecorator = global::Ts.Core.CoreUtil.FindLast(nodes, global::Ts.Ast.NodeTests.IsDecorator);
        }
        if (lastDecorator is not null && !global::Ts.Ast.AstUtilities.PositionIsSynthesized(lastDecorator.End))
        {
            return new TextRange(lastDecorator.End, node.End);
        }
        return node.Loc;
    }

    // GetNonAssignmentOperatorForCompoundAssignment returns the non-assignment operator for a compound assignment.
    public static SyntaxKind GetNonAssignmentOperatorForCompoundAssignment(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
        SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
        SyntaxKind.AsteriskEqualsToken => SyntaxKind.AsteriskToken,
        SyntaxKind.AsteriskAsteriskEqualsToken => SyntaxKind.AsteriskAsteriskToken,
        SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
        SyntaxKind.PercentEqualsToken => SyntaxKind.PercentToken,
        SyntaxKind.LessThanLessThanEqualsToken => SyntaxKind.LessThanLessThanToken,
        SyntaxKind.GreaterThanGreaterThanEqualsToken => SyntaxKind.GreaterThanGreaterThanToken,
        SyntaxKind.GreaterThanGreaterThanGreaterThanEqualsToken => SyntaxKind.GreaterThanGreaterThanGreaterThanToken,
        SyntaxKind.AmpersandEqualsToken => SyntaxKind.AmpersandToken,
        SyntaxKind.BarEqualsToken => SyntaxKind.BarToken,
        SyntaxKind.CaretEqualsToken => SyntaxKind.CaretToken,
        SyntaxKind.BarBarEqualsToken => SyntaxKind.BarBarToken,
        SyntaxKind.AmpersandAmpersandEqualsToken => SyntaxKind.AmpersandAmpersandToken,
        SyntaxKind.QuestionQuestionEqualsToken => SyntaxKind.QuestionQuestionToken,
        _ => kind,
    };
}
