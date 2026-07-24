// Ported from tsgo internal/ls/completions.go — the scope/global cluster used by
// getGlobalCompletions (getScopeNode/isSnippetScope/isProbablyGlobalType at :3642-3697,
// tryGetFunctionLikeBodyCompletionContainer at :3486-3503) and getAllSuperTypeNodes
// (utilities.go:915-926).

using Ts.Ast;
using CheckerType = Ts.Checker.Type;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:3486-3503 — tryGetFunctionLikeBodyCompletionContainer.
    internal static Node? TryGetFunctionLikeBodyCompletionContainer(Node? contextToken)
    {
        if (contextToken == null)
        {
            return null;
        }

        Node? prev = null;
        var container = AstUtilities.FindAncestorOrQuit(contextToken, node =>
        {
            if (AstUtilities.IsClassLike(node))
            {
                return FindAncestorResult.Quit;
            }
            if (AstUtilities.IsFunctionLikeDeclaration(node) && prev == node.Body())
            {
                return FindAncestorResult.True;
            }
            prev = node;
            return FindAncestorResult.False;
        });
        return container;
    }

    // completions.go:3642-3648 — getScopeNode.
    internal static Node? GetScopeNode(Node? initialToken, int position, SourceFile file)
    {
        var scope = initialToken;
        while (scope != null && !CompletedNode.PositionBelongsToNode(scope, position, file))
        {
            scope = scope.Parent;
        }
        return scope;
    }

    // completions.go:3650-3660 — isSnippetScope.
    internal static bool IsSnippetScope(Node scopeNode)
    {
        switch (scopeNode.Kind)
        {
            case SyntaxKind.SourceFile:
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.JsxExpression:
            case SyntaxKind.Block:
                return true;
            default:
                return AstUtilities.IsStatement(scopeNode);
        }
    }

    // completions.go:3662-3681 — isProbablyGlobalType: determines if a type is exactly the same type
    // resolved by the global 'self', 'global', or 'globalThis'.
    internal static bool IsProbablyGlobalType(CheckerType t, SourceFile file, TsChecker typeChecker)
    {
        // The type of `self` and `window` is the same in lib.dom.d.ts, but `window` does not exist in
        // lib.webworker.d.ts, so checking against `self` is also a check against `window` when it exists.
        var selfSymbol = typeChecker.GetGlobalSymbol("self", SymbolFlags.Value, null /*diagnostic*/);
        if (selfSymbol != null && typeChecker.GetTypeOfSymbolAtLocation(selfSymbol, file) == t)
        {
            return true;
        }
        var globalSymbol = typeChecker.GetGlobalSymbol("global", SymbolFlags.Value, null /*diagnostic*/);
        if (globalSymbol != null && typeChecker.GetTypeOfSymbolAtLocation(globalSymbol, file) == t)
        {
            return true;
        }
        var globalThisSymbol = typeChecker.GetGlobalSymbol("globalThis", SymbolFlags.Value, null /*diagnostic*/);
        if (globalThisSymbol != null && typeChecker.GetTypeOfSymbolAtLocation(globalThisSymbol, file) == t)
        {
            return true;
        }
        return false;
    }

    // utilities.go:915-926 — getAllSuperTypeNodes.
    internal static Node[] GetAllSuperTypeNodes(Node node)
    {
        if (NodeTests.IsInterfaceDeclaration(node))
        {
            return AstUtilities.GetHeritageElements(node, SyntaxKind.ExtendsKeyword);
        }
        if (AstUtilities.IsClassLike(node))
        {
            return [
                .. global::Ts.Core.CoreUtil.SingleElementSlice(AstUtilities.GetClassExtendsHeritageElement(node)) ?? [],
                .. AstUtilities.GetImplementsTypeNodes(node),
            ];
        }
        return [];
    }
}
