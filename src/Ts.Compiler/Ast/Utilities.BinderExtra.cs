// Ported from tsgo internal/ast/utilities.go — binder-facing utilities not covered by
// Utilities.Binder.cs. Added with the binder port (phase 4).
using System;

namespace Ts.Ast;

public static partial class AstUtilities
{
    // Go: utilities.go:1759-1770
    public static bool IsInTopLevelContext(Node node)
    {
        // The name of a class or function declaration is a BindingIdentifier in its surrounding scope.
        if (NodeTests.IsIdentifier(node))
        {
            var parent = node.Parent!;
            if ((NodeTests.IsClassDeclaration(parent) || NodeTests.IsFunctionDeclaration(parent)) && parent.Name() == node)
            {
                node = parent;
            }
        }
        var container = GetThisContainer(node, true /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
        return NodeTests.IsSourceFile(container);
    }

    // Go: utilities.go:1772-1809
    public static Node GetThisContainer(Node node, bool includeArrowFunctions, bool includeClassComputedPropertyName)
    {
        Node? n = node;
        while (true)
        {
            n = n!.Parent;
            if (n == null)
            {
                throw new InvalidOperationException("nil parent in getThisContainer");
            }
            switch (n.Kind)
            {
                case SyntaxKind.ComputedPropertyName:
                    if (includeClassComputedPropertyName && IsClassLike(n.Parent!.Parent!))
                    {
                        return n;
                    }
                    n = n.Parent!.Parent;
                    break;
                case SyntaxKind.Decorator:
                    if (n.Parent!.Kind == SyntaxKind.Parameter && IsClassElement(n.Parent!.Parent!))
                    {
                        // If the decorator's parent is a ParameterDeclaration, we resolve the this container from
                        // the grandparent class declaration.
                        n = n.Parent!.Parent;
                    }
                    else if (IsClassElement(n.Parent!))
                    {
                        // If the decorator's parent is a class element, we resolve the 'this' container
                        // from the parent class declaration.
                        n = n.Parent;
                    }
                    break;
                case SyntaxKind.ArrowFunction:
                    if (includeArrowFunctions)
                    {
                        return n;
                    }
                    break;
                case SyntaxKind.FunctionDeclaration:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.ModuleDeclaration:
                case SyntaxKind.ClassStaticBlockDeclaration:
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.PropertySignature:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.MethodSignature:
                case SyntaxKind.Constructor:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.CallSignature:
                case SyntaxKind.ConstructSignature:
                case SyntaxKind.IndexSignature:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.SourceFile:
                    return n;
            }
        }
    }

    // Go: utilities.go:2479-2486
    public static Node? FindConstructorDeclaration(Node node)
    {
        foreach (var member in node.Members() ?? [])
        {
            if (NodeTests.IsConstructorDeclaration(member) && NodeIsPresent(member.Body()))
            {
                return member;
            }
        }
        return null;
    }
}
