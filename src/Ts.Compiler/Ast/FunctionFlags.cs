using System;

namespace Ts.Ast;

// Go: internal/ast/functionflags.go — `type FunctionFlags uint32`. Members drop the Go
// `FunctionFlags` type-name prefix (plan/05-conventions.md §1.1 enum rule).
[Flags]
public enum FunctionFlags : uint
{
    Normal = 0,
    Generator = 1 << 0,
    Async = 1 << 1,
    Invalid = 1 << 2,
    AsyncGenerator = Async | Generator,
}

public static partial class AstUtilities
{
    // functionflags.go:15-37
    public static FunctionFlags GetFunctionFlags(Node? node)
    {
        if (node == null)
        {
            return FunctionFlags.Invalid;
        }
        var data = node.BodyData();
        if (data == null)
        {
            return FunctionFlags.Invalid;
        }
        var flags = FunctionFlags.Normal;
        switch (node.Kind)
        {
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.MethodDeclaration:
                if (data.AsteriskToken != null)
                {
                    flags |= FunctionFlags.Generator;
                }
                goto case SyntaxKind.ArrowFunction;
            case SyntaxKind.ArrowFunction:
                if (HasSyntacticModifier(node, ModifierFlags.Async))
                {
                    flags |= FunctionFlags.Async;
                }
                break;
        }
        if (data.Body == null)
        {
            flags |= FunctionFlags.Invalid;
        }
        return flags;
    }
}
