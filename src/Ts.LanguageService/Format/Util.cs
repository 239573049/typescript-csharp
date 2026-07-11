// Ported from tsgo internal/format/util.go

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

internal static class Util
{
    // Go: func rangeIsOnOneLine(node core.TextRange, file *ast.SourceFile) bool
    internal static bool RangeIsOnOneLine(TextRange node, SourceFile file)
    {
        int startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, node.Pos);
        int endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, node.End);
        return startLine == endLine;
    }

    // Go: func getOpenTokenForList(node *ast.Node, list *ast.NodeList) ast.Kind
    internal static SyntaxKind GetOpenTokenForList(Node node, NodeList? list)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Constructor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                if (node.TypeParameterList() == list)
                {
                    return SyntaxKind.LessThanToken;
                }
                else if (node.ParameterList() == list)
                {
                    return SyntaxKind.OpenParenToken;
                }
                break;
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
                if (node.TypeArgumentList() == list)
                {
                    return SyntaxKind.LessThanToken;
                }
                else if (node.ArgumentList() == list)
                {
                    return SyntaxKind.OpenParenToken;
                }
                break;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
                if (node.TypeParameterList() == list)
                {
                    return SyntaxKind.LessThanToken;
                }
                break;
            case SyntaxKind.TypeReference:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.TypeQuery:
            case SyntaxKind.ExpressionWithTypeArguments:
            case SyntaxKind.ImportType:
                if (node.TypeArgumentList() == list)
                {
                    return SyntaxKind.LessThanToken;
                }
                break;
            case SyntaxKind.TypeLiteral:
                return SyntaxKind.OpenBraceToken;
        }

        return SyntaxKind.Unknown;
    }

    // Go: func getCloseTokenForOpenToken(kind ast.Kind) ast.Kind
    // TODO: matches strada - seems like it could handle more pairs of braces, though? [] notably missing
    internal static SyntaxKind GetCloseTokenForOpenToken(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.OpenParenToken:
                return SyntaxKind.CloseParenToken;
            case SyntaxKind.LessThanToken:
                return SyntaxKind.GreaterThanToken;
            case SyntaxKind.OpenBraceToken:
                return SyntaxKind.CloseBraceToken;
        }
        return SyntaxKind.Unknown;
    }

    // Go: func GetLineStartPositionForPosition(position int, sourceFile *ast.SourceFile) int
    public static int GetLineStartPositionForPosition(int position, SourceFile sourceFile)
    {
        int[] lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile);
        int line = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, position);
        return lineStarts[line];
    }

    /**
     * Tests whether `child` is a grammar error on `parent`.
     * In strada, this also checked node arrays, but it is never actually called with one in practice.
     */
    // Go: func isGrammarError(parent *ast.Node, child *ast.Node) bool
    internal static bool IsGrammarError(Node parent, Node child)
    {
        if (NodeTests.IsTypeParameterDeclaration(parent))
        {
            return child == parent.AsTypeParameterDeclaration().Expression;
        }
        if (NodeTests.IsPropertySignatureDeclaration(parent))
        {
            return child == parent.Initializer();
        }
        if (NodeTests.IsPropertyDeclaration(parent))
        {
            return AstUtilities.IsAutoAccessorPropertyDeclaration(parent) && child == parent.PostfixToken() && child.Kind == SyntaxKind.QuestionToken;
        }
        if (NodeTests.IsPropertyAssignment(parent))
        {
            var pa = parent.AsPropertyAssignment();
            var mods = pa.Modifiers();
            return child == pa.PostfixToken || (mods is not null && IsGrammarErrorElement(mods, child, AstUtilities.IsModifierLike));
        }
        if (NodeTests.IsShorthandPropertyAssignment(parent))
        {
            var sp = parent.AsShorthandPropertyAssignment();
            var mods = sp.Modifiers();
            return child == sp.EqualsToken || child == sp.PostfixToken || (mods is not null && IsGrammarErrorElement(mods, child, AstUtilities.IsModifierLike));
        }
        if (NodeTests.IsMethodDeclaration(parent))
        {
            return child == parent.PostfixToken() && child.Kind == SyntaxKind.ExclamationToken;
        }
        if (NodeTests.IsConstructorDeclaration(parent))
        {
            return child == parent.AsConstructorDeclaration().Type || IsGrammarErrorElement(parent.TypeParameterList(), child, NodeTests.IsTypeParameterDeclaration);
        }
        if (NodeTests.IsGetAccessorDeclaration(parent))
        {
            return IsGrammarErrorElement(parent.TypeParameterList(), child, NodeTests.IsTypeParameterDeclaration);
        }
        if (NodeTests.IsSetAccessorDeclaration(parent))
        {
            // PORT-NOTE: Go uses `parent.AsSetAccessorDeclaration().Type`; in C# the Type
            // field lives on the accessor base, so we use the Type() node accessor which
            // is semantically identical for set accessors.
            return child == parent.Type() || IsGrammarErrorElement(parent.TypeParameterList(), child, NodeTests.IsTypeParameterDeclaration);
        }
        if (NodeTests.IsNamespaceExportDeclaration(parent))
        {
            var mods = parent.AsNamespaceExportDeclaration().Modifiers();
            return mods is not null && IsGrammarErrorElement(mods, child, AstUtilities.IsModifierLike);
        }
        return false;
    }

    // Go: func isGrammarErrorElement(list *ast.NodeList, child *ast.Node, isPossibleElement func(node *ast.Node) bool) bool
    internal static bool IsGrammarErrorElement(NodeList? list, Node child, Func<Node, bool> isPossibleElement)
    {
        if (list is null || list.Nodes.Length == 0)
        {
            return false;
        }
        if (!isPossibleElement(child))
        {
            return false;
        }
        return Array.IndexOf(list.Nodes, child) >= 0; // slices.Contains
    }

    /**
     * Validating `expectedTokenKind` ensures the token was typed in the context we expect (eg: not a comment).
     */
    // Go: func findImmediatelyPrecedingTokenOfKind(end int, expectedTokenKind ast.Kind, sourceFile *ast.SourceFile) *ast.Node
    internal static Node? FindImmediatelyPrecedingTokenOfKind(int end, SyntaxKind expectedTokenKind, SourceFile sourceFile)
    {
        Node? precedingToken = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, end);
        if (precedingToken is null || precedingToken.Kind != expectedTokenKind || precedingToken.End != end)
        {
            return null;
        }
        return precedingToken;
    }

    /**
     * Finds the highest node enclosing `node` at the same list level as `node`
     * and whose end does not exceed `node.end`.
     */
    // Go: func findOutermostNodeWithinListLevel(node *ast.Node) *ast.Node
    internal static Node? FindOutermostNodeWithinListLevel(Node? node)
    {
        Node? current = node;
        while (current is not null &&
               current.Parent is not null &&
               current.Parent.End == node!.End &&
               !IsListElement(current.Parent, current))
        {
            current = current.Parent;
        }

        return current;
    }

    // Returns true if node is a element in some list in parent
    // Go: func isListElement(parent *ast.Node, node *ast.Node) bool
    internal static bool IsListElement(Node parent, Node node)
    {
        switch (parent.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
                return node.Loc.ContainedBy(parent.MemberList()!.Loc);
            case SyntaxKind.ModuleDeclaration:
                {
                    Node? body = parent.Body();
                    return body is not null && body.Kind == SyntaxKind.ModuleBlock && node.Loc.ContainedBy(body.StatementList()!.Loc);
                }
            case SyntaxKind.SourceFile:
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
                return node.Loc.ContainedBy(parent.StatementList()!.Loc);
            case SyntaxKind.CatchClause:
                return node.Loc.ContainedBy(parent.AsCatchClause().Block!.StatementList()!.Loc);
        }

        return false;
    }
}
