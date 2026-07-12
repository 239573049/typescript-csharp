// Ported from tsgo internal/ast/ast.go (IsWriteOnlyAccess 1265 / IsWriteAccess 1269 + accessKind 1427 +
// reverseAccessKind 1489 + the AccessKind enum). Faithful per-function translation: same function order
// as Go, same branch structure; Go comments and `// !!!` markers preserved.

using static Ts.Ast.NodeTests;

namespace Ts.Ast;

// ast.go:1498
public enum AccessKind
{
    Read,      // Only reads from a variable
    Write,     // Only writes to a variable without ever reading it. E.g.: `x=1;`.
    ReadWrite, // Reads from and writes to a variable. E.g.: `f(x++);`, `x/=1`.
}

public static partial class AstUtilities
{
    // ast.go:1265
    public static bool IsWriteOnlyAccess(Node node)
    {
        return AccessKind_(node) == AccessKind.Write;
    }

    // ast.go:1269
    public static bool IsWriteAccess(Node node)
    {
        return AccessKind_(node) != AccessKind.Read;
    }

    // ast.go:1427 — Go: accessKind (package-private). Named AccessKind_ to avoid colliding with the enum.
    private static AccessKind AccessKind_(Node node)
    {
        var parent = node.Parent;
        if (parent == null)
        {
            return AccessKind.Read;
        }
        switch (parent.Kind)
        {
            case SyntaxKind.ParenthesizedExpression:
                return AccessKind_(parent);
            case SyntaxKind.PrefixUnaryExpression:
                var prefixOperator = parent.AsPrefixUnaryExpression().Operator;
                if (prefixOperator == SyntaxKind.PlusPlusToken || prefixOperator == SyntaxKind.MinusMinusToken)
                {
                    return AccessKind.ReadWrite;
                }
                return AccessKind.Read;
            case SyntaxKind.PostfixUnaryExpression:
                var postfixOperator = parent.AsPostfixUnaryExpression().Operator;
                if (postfixOperator == SyntaxKind.PlusPlusToken || postfixOperator == SyntaxKind.MinusMinusToken)
                {
                    return AccessKind.ReadWrite;
                }
                return AccessKind.Read;
            case SyntaxKind.BinaryExpression:
                if (parent.AsBinaryExpression().Left == node)
                {
                    var operatorToken = parent.AsBinaryExpression().OperatorToken!;
                    if (IsAssignmentOperator(operatorToken.Kind))
                    {
                        if (operatorToken.Kind == SyntaxKind.EqualsToken)
                        {
                            return AccessKind.Write;
                        }
                        return AccessKind.ReadWrite;
                    }
                }
                return AccessKind.Read;
            case SyntaxKind.PropertyAccessExpression:
                if (parent.AsPropertyAccessExpression().Name() != node)
                {
                    return AccessKind.Read;
                }
                return AccessKind_(parent);
            case SyntaxKind.PropertyAssignment:
                var parentAccess = AccessKind_(parent.Parent!);
                // In `({ x: varname }) = { x: 1 }`, the left `x` is a read, the right `x` is a write.
                if (node == parent.AsPropertyAssignment().Name())
                {
                    return ReverseAccessKind(parentAccess);
                }
                return parentAccess;
            case SyntaxKind.ShorthandPropertyAssignment:
                // Assume it's the local variable being accessed, since we don't check public properties for --noUnusedLocals.
                if (node == parent.AsShorthandPropertyAssignment().ObjectAssignmentInitializer)
                {
                    return AccessKind.Read;
                }
                return AccessKind_(parent.Parent!);
            case SyntaxKind.ArrayLiteralExpression:
                return AccessKind_(parent);
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
                if (node == parent.AsForInOrOfStatement().Initializer)
                {
                    return AccessKind.Write;
                }
                return AccessKind.Read;
            default:
                return AccessKind.Read;
        }
    }

    // ast.go:1489
    private static AccessKind ReverseAccessKind(AccessKind a)
    {
        switch (a)
        {
            case AccessKind.Read:
                return AccessKind.Write;
            case AccessKind.Write:
                return AccessKind.Read;
            case AccessKind.ReadWrite:
                return AccessKind.ReadWrite;
        }
        throw new System.InvalidOperationException("Unhandled case in reverseAccessKind");
    }
}
