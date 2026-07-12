// Ported from tsgo internal/checker/checker.go — the context-sensitivity predicates consumed by
// inference / contextual typing (isContextSensitive 30654 / isContextSensitiveFunctionLikeDeclaration
// 30688 / hasContextSensitiveReturnExpression 30692 / hasContextSensitiveYieldExpression 30708 /
// isContextSensitiveFunctionOrObjectLiteralMethod 29253) plus the co-located forEachYieldExpression
// traversal (internal/checker/utilities.go:1197). Faithful per-function translation: same function
// order intent, same branch structure; Go comments preserved.
//
// §1.5 cross-package hosts: the no-collision ast hosts (AstUtilities/NodeTests/CoreUtil) are used
// directly; ast.ForEachReturnStatement / ast.HasContextSensitiveParameters / ast.GetFunctionFlags are
// AstUtilities members. No sibling-namespace collisions in this file.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:29253-29255
    internal bool IsContextSensitiveFunctionOrObjectLiteralMethod(Node fn)
    {
        return (AstUtilities.IsFunctionExpressionOrArrowFunction(fn) || AstUtilities.IsObjectLiteralMethod(fn)) && IsContextSensitiveFunctionLikeDeclaration(fn);
    }

    // checker.go:30654-30686
    internal bool IsContextSensitive(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.FunctionDeclaration:
                return IsContextSensitiveFunctionLikeDeclaration(node);
            case SyntaxKind.ObjectLiteralExpression:
                return CoreUtil.Some(node.Properties(), IsContextSensitive);
            case SyntaxKind.ArrayLiteralExpression:
                return CoreUtil.Some(node.Elements(), IsContextSensitive);
            case SyntaxKind.ConditionalExpression:
                return IsContextSensitive(node.AsConditionalExpression().WhenTrue!) || IsContextSensitive(node.AsConditionalExpression().WhenFalse!);
            case SyntaxKind.BinaryExpression:
                var binary = node.AsBinaryExpression();
                return AstUtilities.NodeKindIs(binary.OperatorToken!, SyntaxKind.BarBarToken, SyntaxKind.QuestionQuestionToken) && (IsContextSensitive(binary.Left!) || IsContextSensitive(binary.Right!));
            case SyntaxKind.PropertyAssignment:
                return IsContextSensitive(node.Initializer()!);
            case SyntaxKind.ParenthesizedExpression:
                return IsContextSensitive(node.Expression()!);
            case SyntaxKind.JsxAttributes:
                return CoreUtil.Some(node.Properties(), IsContextSensitive) || NodeTests.IsJsxOpeningElement(node.Parent!) && CoreUtil.Some(node.Parent!.Parent!.Children()!.Nodes, IsContextSensitive);
            case SyntaxKind.JsxAttribute:
                {
                    // If there is no initializer, JSX attribute has a boolean value of true which is not context sensitive.
                    var initializer = node.Initializer();
                    return initializer != null && IsContextSensitive(initializer);
                }
            case SyntaxKind.JsxExpression:
                {
                    // It is possible to that node.expression is undefined (e.g <div x={} />)
                    var expression = node.Expression();
                    return expression != null && IsContextSensitive(expression);
                }
            case SyntaxKind.YieldExpression:
                {
                    var expression = node.Expression();
                    return expression != null && IsContextSensitive(expression);
                }
        }
        return false;
    }

    // checker.go:30688-30690
    internal bool IsContextSensitiveFunctionLikeDeclaration(Node node)
    {
        return AstUtilities.HasContextSensitiveParameters(node) || HasContextSensitiveReturnExpression(node) || HasContextSensitiveYieldExpression(node);
    }

    // checker.go:30692-30706
    internal bool HasContextSensitiveReturnExpression(Node node)
    {
        if (node.TypeParameters() != null || node.Type() != null)
        {
            return false;
        }
        var body = node.Body();
        if (body == null)
        {
            return false;
        }
        if (!NodeTests.IsBlock(body))
        {
            return IsContextSensitive(body);
        }
        return AstUtilities.ForEachReturnStatement(body, statement =>
            statement.Expression() != null && IsContextSensitive(statement.Expression()!));
    }

    // checker.go:30708-30710
    internal bool HasContextSensitiveYieldExpression(Node node)
    {
        return (AstUtilities.GetFunctionFlags(node) & FunctionFlags.Generator) != 0 && node.Body() != null && ForEachYieldExpression(node.Body()!, IsContextSensitive);
    }

    // internal/checker/utilities.go:1197-1229
    internal bool ForEachYieldExpression(Node body, Func<Node, bool> visitor)
    {
        bool Traverse(Node node)
        {
            switch (node.Kind)
            {
                case SyntaxKind.YieldExpression:
                    if (visitor(node))
                    {
                        return true;
                    }
                    var operand = node.Expression();
                    if (operand == null)
                    {
                        return false;
                    }
                    return Traverse(operand);
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.ModuleDeclaration:
                case SyntaxKind.TypeAliasDeclaration:
                    // These are not allowed inside a generator now, but eventually they may be allowed
                    // as local types. Regardless, skip them to avoid the work.
                    break;
                default:
                    if (AstUtilities.IsFunctionLike(node))
                    {
                        if (node.Name() != null && NodeTests.IsComputedPropertyName(node.Name()!))
                        {
                            // Note that we will not include methods/accessors of a class because they would require
                            // first descending into the class. This is by design.
                            return Traverse(node.Name()!.Expression()!);
                        }
                    }
                    else if (!AstUtilities.IsPartOfTypeNode(node))
                    {
                        // This is the general case, which should include mostly expressions and statements.
                        // Also includes NodeArrays.
                        return node.ForEachChild(Traverse);
                    }
                    break;
            }
            return false;
        }
        return Traverse(body);
    }
}
