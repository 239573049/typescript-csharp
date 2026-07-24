// Ported from tsgo internal/ls/completions.go — the class-member and JSX completion filters
// (tryGetObjectTypeDeclarationCompletionContainer/filterClassMembersList/tryGetContainingJsxElement/
// filterJsxAttributes, completions.go:4006-4245).

using System.Collections.Generic;
using Ts.Ast;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:4006-4101 — tryGetObjectTypeDeclarationCompletionContainer.
    internal static Node? TryGetObjectTypeDeclarationCompletionContainer(
        SourceFile file,
        Node? contextToken,
        Node location,
        int position)
    {
        // class c { method() { } | method2() { } }
        switch (location.Kind)
        {
            case SyntaxKind.SyntaxList:
                if (AstUtilities.IsObjectTypeDeclaration(location.Parent!))
                {
                    return location.Parent;
                }
                return null;
            case SyntaxKind.EndOfFile:
                var stmtList = location.Parent!.StatementList();
                if (stmtList != null && stmtList.Nodes.Length > 0 && AstUtilities.IsObjectTypeDeclaration(stmtList.Nodes[^1]))
                {
                    var cls = stmtList.Nodes[^1];
                    if (global::Ts.AstNav.Tokens.FindChildOfKind(cls, SyntaxKind.CloseBraceToken, file) == null)
                    {
                        return cls;
                    }
                }
                break;
            case SyntaxKind.PrivateIdentifier:
                if (NodeTests.IsPropertyDeclaration(location.Parent!))
                {
                    return AstUtilities.FindAncestor(location, AstUtilities.IsClassLike);
                }
                break;
            case SyntaxKind.Identifier:
                var originalKeywordKind = global::Ts.Scanner.Scanner.IdentifierToKeywordKind(location.AsIdentifier());
                if (originalKeywordKind != SyntaxKind.Unknown)
                {
                    return null;
                }
                // class c { public prop = c| }
                if (NodeTests.IsPropertyDeclaration(location.Parent!) && location.Parent!.Initializer() == location)
                {
                    return null;
                }
                // class c extends React.Component { a: () => 1\n compon| }
                if (IsFromObjectTypeDeclaration(location))
                {
                    return AstUtilities.FindAncestor(location, AstUtilities.IsObjectTypeDeclaration);
                }
                break;
        }

        if (contextToken == null)
        {
            return null;
        }

        // class C { blah; constructor/**/ }
        // or
        // class C { blah \n constructor/**/ }
        if (location.Kind == SyntaxKind.ConstructorKeyword ||
            (NodeTests.IsIdentifier(contextToken) && NodeTests.IsPropertyDeclaration(contextToken.Parent!) && AstUtilities.IsClassLike(location)))
        {
            return AstUtilities.FindAncestor(contextToken, AstUtilities.IsClassLike);
        }

        switch (contextToken.Kind)
        {
            // class c { public prop = | /* global completions */ }
            case SyntaxKind.EqualsToken:
                return null;
            // class c {getValue(): number; | }
            // class c { method() { } | }
            case SyntaxKind.SemicolonToken:
            case SyntaxKind.CloseBraceToken:
                // class c { method() { } b| }
                if (IsFromObjectTypeDeclaration(location) && location.Parent!.Name() == location)
                {
                    return location.Parent.Parent;
                }
                if (AstUtilities.IsObjectTypeDeclaration(location))
                {
                    return location;
                }
                return null;
            // class c { |
            // class c {getValue(): number, | }
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.CommaToken:
                if (AstUtilities.IsObjectTypeDeclaration(contextToken.Parent!))
                {
                    return contextToken.Parent;
                }
                return null;
            default:
                if (AstUtilities.IsObjectTypeDeclaration(location))
                {
                    // class C extends React.Component { a: () => 1\n| }
                    // class C { prop = ""\n | }
                    if (LsDeps.GetLineOfPosition(file, contextToken.End) != LsDeps.GetLineOfPosition(file, position))
                    {
                        return location;
                    }
                    var isValidKeyword = AstUtilities.IsClassLike(contextToken.Parent!.Parent!)
                        ? (global::System.Func<SyntaxKind, bool>)IsClassMemberCompletionKeyword
                        : IsInterfaceOrTypeLiteralCompletionKeyword;

                    if (isValidKeyword(contextToken.Kind) || contextToken.Kind == SyntaxKind.AsteriskToken ||
                        NodeTests.IsIdentifier(contextToken) && isValidKeyword(global::Ts.Scanner.Scanner.IdentifierToKeywordKind(contextToken.AsIdentifier())))
                    {
                        return contextToken.Parent.Parent;
                    }
                }

                return null;
        }
    }

    // completions.go:4108-4152 — filterClassMembersList: filters out completion suggestions for class elements.
    internal static Symbol[] FilterClassMembersList(
        Symbol[] baseSymbols,
        Node[] existingMembers,
        ModifierFlags classElementModifierFlags,
        SourceFile file,
        int position)
    {
        var existingMemberNames = new HashSet<string>(global::System.StringComparer.Ordinal);
        foreach (var member in existingMembers)
        {
            // Ignore omitted expressions for missing members.
            if (member.Kind != SyntaxKind.PropertyDeclaration &&
                member.Kind != SyntaxKind.MethodDeclaration &&
                member.Kind != SyntaxKind.GetAccessor &&
                member.Kind != SyntaxKind.SetAccessor)
            {
                continue;
            }

            // If this is the current item we are editing right now, do not filter it out
            if (IsCurrentlyEditingNode(member, file, position))
            {
                continue;
            }

            // Don't filter member even if the name matches if it is declared private in the list.
            if ((member.ModifierFlags() & ModifierFlags.Private) != 0)
            {
                continue;
            }

            // Do not filter it out if the static presence doesn't match.
            if (AstUtilities.IsStatic(member) != ((classElementModifierFlags & ModifierFlags.Static) != 0))
            {
                continue;
            }

            var existingName = AstUtilities.GetPropertyNameForPropertyNameNode(member.Name()!);
            if (existingName.Length != 0)
            {
                existingMemberNames.Add(existingName);
            }
        }

        return global::Ts.Core.CoreUtil.Filter(baseSymbols, propertySymbol =>
            !existingMemberNames.Contains(AstUtilities.SymbolName(propertySymbol)) &&
            (propertySymbol.Declarations ?? []).Length > 0 &&
            (TsChecker.GetDeclarationModifierFlagsFromSymbol(propertySymbol) & ModifierFlags.Private) == 0 &&
            !(propertySymbol.ValueDeclaration != null && AstUtilities.IsPrivateIdentifierClassElementDeclaration(propertySymbol.ValueDeclaration))) ?? [];
    }

    // completions.go:4154-4216 — tryGetContainingJsxElement.
    internal static Node? TryGetContainingJsxElement(Node? contextToken, SourceFile file)
    {
        if (contextToken == null)
        {
            return null;
        }

        var parent = contextToken.Parent;
        switch (contextToken.Kind)
        {
            case SyntaxKind.GreaterThanToken:
            case SyntaxKind.LessThanSlashToken:
            case SyntaxKind.SlashToken:
            case SyntaxKind.Identifier:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.JsxNamespacedName:
            case SyntaxKind.JsxAttributes:
            case SyntaxKind.JsxAttribute:
            case SyntaxKind.JsxSpreadAttribute:
                if (parent != null && (parent.Kind == SyntaxKind.JsxSelfClosingElement || parent.Kind == SyntaxKind.JsxOpeningElement))
                {
                    if (contextToken.Kind == SyntaxKind.GreaterThanToken)
                    {
                        var precedingToken = global::Ts.AstNav.Tokens.FindPrecedingToken(file, contextToken.Pos);
                        if ((parent.TypeArguments() ?? []).Length == 0 ||
                            precedingToken != null && precedingToken.Kind == SyntaxKind.SlashToken)
                        {
                            return null;
                        }
                    }
                    return parent;
                }
                else if (parent != null && NodeTests.IsJsxNamespacedName(parent) &&
                    parent.Parent != null && (parent.Parent.Kind == SyntaxKind.JsxSelfClosingElement || parent.Parent.Kind == SyntaxKind.JsxOpeningElement))
                {
                    return parent.Parent;
                }
                else if (parent != null && parent.Kind == SyntaxKind.JsxAttribute)
                {
                    // Currently we parse JsxOpeningLikeElement as:
                    //      JsxOpeningLikeElement
                    //          attributes: JsxAttributes
                    //             properties: NodeArray<JsxAttributeLike>
                    return parent.Parent!.Parent;
                }
                break;
            // The context token is the closing } or " of an attribute, which means
            // its parent is a JsxExpression, whose parent is a JsxAttribute,
            // whose parent is a JsxOpeningLikeElement
            case SyntaxKind.StringLiteral:
                if (parent != null && (parent.Kind == SyntaxKind.JsxAttribute || parent.Kind == SyntaxKind.JsxSpreadAttribute))
                {
                    // Currently we parse JsxOpeningLikeElement as:
                    //      JsxOpeningLikeElement
                    //          attributes: JsxAttributes
                    //             properties: NodeArray<JsxAttributeLike>
                    return parent.Parent!.Parent;
                }
                break;
            case SyntaxKind.CloseBraceToken:
                if (parent != null && parent.Kind == SyntaxKind.JsxExpression &&
                    parent.Parent != null && parent.Parent.Kind == SyntaxKind.JsxAttribute)
                {
                    // Currently we parse JsxOpeningLikeElement as:
                    //      JsxOpeningLikeElement
                    //          attributes: JsxAttributes
                    //             properties: NodeArray<JsxAttributeLike>
                    //                  each JsxAttribute can have initializer as JsxExpression
                    return parent.Parent.Parent!.Parent;
                }
                if (parent != null && parent.Kind == SyntaxKind.JsxSpreadAttribute)
                {
                    // Currently we parse JsxOpeningLikeElement as:
                    //      JsxOpeningLikeElement
                    //          attributes: JsxAttributes
                    //             properties: NodeArray<JsxAttributeLike>
                    return parent.Parent!.Parent;
                }
                break;
        }

        return null;
    }

    // completions.go:4218-4245 — filterJsxAttributes: filters out completion suggestions from 'symbols'
    // according to existing JSX attributes. Returns symbols to be suggested in a JSX element, barring
    // those whose attributes do not occur at the current position and have not otherwise been typed.
    internal static (Symbol[] filteredMembers, HashSet<string> spreadMemberNames) FilterJsxAttributes(
        Symbol[] symbols,
        Node[] attributes,
        SourceFile file,
        int position,
        TsChecker typeChecker)
    {
        var existingNames = new HashSet<string>(global::System.StringComparer.Ordinal);
        var membersDeclaredBySpreadAssignment = new HashSet<string>(global::System.StringComparer.Ordinal);
        foreach (var attr in attributes)
        {
            // If this is the item we are editing right now, do not filter it out.
            if (IsCurrentlyEditingNode(attr, file, position))
            {
                continue;
            }

            if (attr.Kind == SyntaxKind.JsxAttribute)
            {
                existingNames.Add(attr.Name()!.Text());
            }
            else if (NodeTests.IsJsxSpreadAttribute(attr))
            {
                SetMemberDeclaredBySpreadAssignment(attr, membersDeclaredBySpreadAssignment, typeChecker);
            }
        }

        return (global::Ts.Core.CoreUtil.Filter(symbols, a => !existingNames.Contains(a.Name)) ?? [],
            membersDeclaredBySpreadAssignment);
    }
}
