// Ported from tsgo internal/ls/completions.go — the object-literal / object-binding-pattern
// completion helpers (completions.go:3681-3999, minus the type-argument-constraint pair which
// lands with checker getTypeArgumentConstraint).

using System.Collections.Generic;
using System.Linq;
using Ts.Ast;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:3681-3699 — tryGetTypeLiteralNode.
    internal static Node? TryGetTypeLiteralNode(Node? node)
    {
        if (node == null)
        {
            return null;
        }

        var parent = node.Parent!;
        switch (node.Kind)
        {
            case SyntaxKind.OpenBraceToken:
                if (NodeTests.IsTypeLiteralNode(parent))
                {
                    return parent;
                }
                break;
            case SyntaxKind.SemicolonToken:
            case SyntaxKind.CommaToken:
            case SyntaxKind.Identifier:
                if (parent.Kind == SyntaxKind.PropertySignature && NodeTests.IsTypeLiteralNode(parent.Parent!))
                {
                    return parent.Parent;
                }
                break;
        }

        return null;
    }

    // completions.go:3749-3805 — tryGetObjectLikeCompletionContainer.
    internal static Node? TryGetObjectLikeCompletionContainer(Node? contextToken, int position, SourceFile file)
    {
        if (contextToken == null)
        {
            return null;
        }

        var parent = contextToken.Parent!;
        switch (contextToken.Kind)
        {
            // const x = { |
            // const x = { a: 0, |
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.CommaToken:
                if (NodeTests.IsObjectLiteralExpression(parent) || NodeTests.IsObjectBindingPattern(parent))
                {
                    return parent;
                }
                break;
            case SyntaxKind.AsteriskToken:
                if (NodeTests.IsMethodDeclaration(parent) && NodeTests.IsObjectLiteralExpression(parent.Parent!))
                {
                    return parent.Parent;
                }
                break;
            case SyntaxKind.AsyncKeyword:
                if (NodeTests.IsObjectLiteralExpression(parent.Parent!))
                {
                    return parent.Parent;
                }
                break;
            case SyntaxKind.Identifier:
                if (contextToken.Text() == "async" && NodeTests.IsShorthandPropertyAssignment(parent))
                {
                    return parent.Parent;
                }
                else
                {
                    if (NodeTests.IsObjectLiteralExpression(parent.Parent!) &&
                        (NodeTests.IsSpreadAssignment(parent) ||
                            (NodeTests.IsShorthandPropertyAssignment(parent) &&
                                LsDeps.GetLineOfPosition(file, contextToken.End) != LsDeps.GetLineOfPosition(file, position))))
                    {
                        return parent.Parent;
                    }
                    var ancestor = AstUtilities.FindAncestor(parent, NodeTests.IsPropertyAssignment);
                    if (ancestor != null && LsDeps.GetLastToken(ancestor, file) == contextToken && NodeTests.IsObjectLiteralExpression(ancestor.Parent!))
                    {
                        return ancestor.Parent;
                    }
                }
                break;
            default:
                if (parent.Parent?.Parent != null &&
                    (NodeTests.IsMethodDeclaration(parent.Parent) ||
                        NodeTests.IsGetAccessorDeclaration(parent.Parent) ||
                        NodeTests.IsSetAccessorDeclaration(parent.Parent)) &&
                    NodeTests.IsObjectLiteralExpression(parent.Parent.Parent))
                {
                    return parent.Parent.Parent;
                }
                if (NodeTests.IsSpreadAssignment(parent) && NodeTests.IsObjectLiteralExpression(parent.Parent!))
                {
                    return parent.Parent;
                }
                var ancestorNode = AstUtilities.FindAncestor(parent, NodeTests.IsPropertyAssignment);
                if (contextToken.Kind != SyntaxKind.ColonToken &&
                    ancestorNode != null && LsDeps.GetLastToken(ancestorNode, file) == contextToken &&
                    NodeTests.IsObjectLiteralExpression(ancestorNode.Parent!))
                {
                    return ancestorNode.Parent;
                }
                break;
        }

        return null;
    }

    // completions.go:3807-3826 — tryGetObjectLiteralContextualType.
    internal static global::Ts.Checker.Type? TryGetObjectLiteralContextualType(Node node, global::Ts.Checker.Checker typeChecker)
    {
        var t = typeChecker.GetContextualType(node, global::Ts.Checker.ContextFlags.None);
        if (t != null)
        {
            return t;
        }

        var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent!)!;
        if (NodeTests.IsBinaryExpression(parent) &&
            parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken &&
            node == parent.AsBinaryExpression().Left)
        {
            // Object literal is assignment pattern: ({ | } = x)
            return typeChecker.GetTypeAtLocation(parent);
        }
        if (AstUtilities.IsExpression(parent))
        {
            // f(() => (({ | })));
            return typeChecker.GetContextualType(parent, global::Ts.Checker.ContextFlags.None);
        }

        return null;
    }

    // completions.go:3828-3873 — getPropertiesForObjectExpression.
    internal static Symbol[] GetPropertiesForObjectExpression(
        global::Ts.Checker.Type contextualType,
        global::Ts.Checker.Type? completionsType,
        Node obj,
        global::Ts.Checker.Checker typeChecker)
    {
        var hasCompletionsType = completionsType != null && completionsType != contextualType;
        var types = (contextualType.Flags & global::Ts.Checker.TypeFlags.Union) != 0
            ? contextualType.Types()
            : [contextualType];
        var promiseFilteredContextualType = typeChecker.GetUnionType(
            types.Where(t => typeChecker.GetPromisedTypeOfPromise(t) == null).ToArray());

        var merged = hasCompletionsType && (completionsType!.Flags & global::Ts.Checker.TypeFlags.AnyOrUnknown) == 0
            ? typeChecker.GetUnionType([promiseFilteredContextualType, completionsType])
            : promiseFilteredContextualType;

        // Filter out members whose only declaration is the object literal itself to avoid
        // self-fulfilling completions like:
        //
        // function f<T>(x: T) {}
        // f({ abc/**/: "" }) // `abc` is a member of `T` but only because it declares itself
        bool HasDeclarationOtherThanSelf(Symbol member)
        {
            if (member.Declarations is not { Length: > 0 })
            {
                return true;
            }
            return member.Declarations.Any(decl => decl.Parent != obj);
        }

        var properties = GetApparentPropertiesForObject(merged, obj, typeChecker);
        if ((merged.ObjectFlags & global::Ts.Checker.ObjectFlags.Class) != 0 && ContainsNonPublicProperties(properties))
        {
            return [];
        }
        else if (hasCompletionsType)
        {
            return properties.Where(HasDeclarationOtherThanSelf).ToArray();
        }
        return properties;
    }

    // completions.go:3875-3886 — getApparentProperties (object-expression variant).
    internal static Symbol[] GetApparentPropertiesForObject(global::Ts.Checker.Type t, Node node, global::Ts.Checker.Checker typeChecker)
    {
        if ((t.Flags & global::Ts.Checker.TypeFlags.Union) == 0)
        {
            return typeChecker.GetApparentProperties(t);
        }
        return typeChecker.GetAllPossiblePropertiesOfTypes(t.Types().Where(memberType =>
            !((memberType.Flags & global::Ts.Checker.TypeFlags.Primitive) != 0 ||
                typeChecker.IsArrayLikeType(memberType) ||
                typeChecker.IsTypeInvalidDueToUnionDiscriminant(memberType, node) ||
                typeChecker.TypeHasCallOrConstructSignatures(memberType) ||
                ((memberType.ObjectFlags & global::Ts.Checker.ObjectFlags.Class) != 0 && ContainsNonPublicProperties(typeChecker.GetApparentProperties(memberType)))))
            .ToArray());
    }

    // completions.go:3888-3892 — containsNonPublicProperties.
    internal static bool ContainsNonPublicProperties(Symbol[] props)
    {
        return props.Any(p => (global::Ts.Checker.Checker.GetDeclarationModifierFlagsFromSymbol(p) & ModifierFlags.NonPublicAccessibilityModifier) != 0);
    }

    // completions.go:3895-3954 — filterObjectMembersList: filters out members that are already
    // declared in the object literal or binding pattern; also computes the set of existing members
    // declared by spread assignment.
    internal static (Symbol[] FilteredMembers, HashSet<string> SpreadMemberNames) FilterObjectMembersList(
        Symbol[] contextualMemberSymbols,
        Node[] existingMembers,
        SourceFile file,
        int position,
        global::Ts.Checker.Checker typeChecker)
    {
        if (existingMembers.Length == 0)
        {
            return (contextualMemberSymbols, new HashSet<string>());
        }

        var membersDeclaredBySpreadAssignment = new HashSet<string>();
        var existingMemberNames = new HashSet<string>();
        foreach (var member in existingMembers)
        {
            // Ignore omitted expressions for missing members.
            if (member.Kind != SyntaxKind.PropertyAssignment &&
                member.Kind != SyntaxKind.ShorthandPropertyAssignment &&
                member.Kind != SyntaxKind.BindingElement &&
                member.Kind != SyntaxKind.MethodDeclaration &&
                member.Kind != SyntaxKind.GetAccessor &&
                member.Kind != SyntaxKind.SetAccessor &&
                member.Kind != SyntaxKind.SpreadAssignment)
            {
                continue;
            }

            // If this is the current item we are editing right now, do not filter it out.
            if (IsCurrentlyEditingNode(member, file, position))
            {
                continue;
            }

            var existingName = "";

            if (NodeTests.IsSpreadAssignment(member))
            {
                SetMemberDeclaredBySpreadAssignment(member, membersDeclaredBySpreadAssignment, typeChecker);
            }
            else if (NodeTests.IsBindingElement(member) && member.PropertyName() != null)
            {
                // include only identifiers in completion list
                if (member.PropertyName()!.Kind == SyntaxKind.Identifier)
                {
                    existingName = member.PropertyName()!.Text();
                }
            }
            else
            {
                // TODO: Account for computed property name
                // NOTE: if one only performs this step when m.name is an identifier,
                // things like '__proto__' are not filtered out.
                var name = AstUtilities.GetNameOfDeclaration(member);
                if (name != null && AstUtilities.IsPropertyNameLiteral(name))
                {
                    existingName = name.Text();
                }
            }

            if (existingName.Length != 0)
            {
                existingMemberNames.Add(existingName);
            }
        }

        var filteredSymbols = contextualMemberSymbols.Where(m => !existingMemberNames.Contains(m.Name)).ToArray();
        return (filteredSymbols, membersDeclaredBySpreadAssignment);
    }

    // completions.go:3961-3975 — setMemberDeclaredBySpreadAssignment.
    internal static void SetMemberDeclaredBySpreadAssignment(Node declaration, HashSet<string> members, global::Ts.Checker.Checker typeChecker)
    {
        var expression = declaration.Expression()!;
        var symbol = typeChecker.GetSymbolAtLocation(expression);
        global::Ts.Checker.Type? t = null;
        if (symbol != null)
        {
            t = typeChecker.GetTypeOfSymbolAtLocation(symbol, expression);
        }
        if (t != null && (t.Flags & global::Ts.Checker.TypeFlags.StructuredType) != 0)
        {
            foreach (var property in t.AsStructuredType()!.Properties())
            {
                members.Add(property.Name);
            }
        }
    }

    // completions.go:3977-3996 — tryGetConstructorLikeCompletionContainer.
    internal static Node? TryGetConstructorLikeCompletionContainer(Node? contextToken)
    {
        if (contextToken == null)
        {
            return null;
        }

        var parent = contextToken.Parent!;
        switch (contextToken.Kind)
        {
            case SyntaxKind.OpenParenToken:
            case SyntaxKind.CommaToken:
                if (NodeTests.IsConstructorDeclaration(parent))
                {
                    return parent;
                }
                return null;
            default:
                if (IsConstructorParameterCompletion(contextToken))
                {
                    return parent.Parent;
                }
                break;
        }
        return null;
    }

    // completions.go:3152-3155 — isNamedImportsOrExports.
    internal static bool IsNamedImportsOrExports(Node node) =>
        node.Kind == SyntaxKind.NamedImports || node.Kind == SyntaxKind.NamedExports;
}
