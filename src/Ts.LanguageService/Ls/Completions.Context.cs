// Ported from tsgo internal/ls/completions.go — the contextual-type / recommended-completion
// cluster (completions.go:2877-3121, plus argumentInfoForCompletions at :4846-4862) and
// getContextualTypeFromParent (utilities.go:1355-1372).

using Ts.Ast;
using CheckerType = Ts.Checker.Type;
using TsChecker = Ts.Checker.Checker;

namespace Ts.Ls;

// completions.go:4846-4850 — argumentInfoForCompletions.
internal sealed class ArgumentInfoForCompletions
{
    internal Node invocation = null!;
    internal int argumentIndex;
    internal int argumentCount;
}

public sealed partial class LanguageService
{
    // completions.go:4852-4862 — getArgumentInfoForCompletions.
    internal static ArgumentInfoForCompletions? GetArgumentInfoForCompletions(
        Node node, int position, SourceFile file, TsChecker typeChecker)
    {
        var info = GetImmediatelyContainingArgumentInfo(node, position, file, typeChecker);
        if (info == null || info.IsTypeParameterList || info.Invocation.CallInvocation == null)
        {
            return null;
        }
        return new ArgumentInfoForCompletions
        {
            invocation = info.Invocation.CallInvocation.Node,
            argumentIndex = info.ArgumentIndex,
            argumentCount = info.ArgumentCount,
        };
    }

    // completions.go:2877-2889 — getContextualTypeForConditionalExpression.
    internal static CheckerType? GetContextualTypeForConditionalExpression(
        Node conditionalExpr, int position, SourceFile file, TsChecker typeChecker)
    {
        var argInfo = GetArgumentInfoForCompletions(conditionalExpr, position, file, typeChecker);
        if (argInfo != null)
        {
            return typeChecker.GetContextualTypeForArgumentAtIndex(argInfo.invocation, argInfo.argumentIndex);
        }
        // Fall through to regular contextual type logic if not in an argument
        var contextualType = typeChecker.GetContextualType(conditionalExpr, global::Ts.Checker.ContextFlags.IgnoreNodeInferences);
        if (contextualType != null)
        {
            return contextualType;
        }
        return typeChecker.GetContextualType(conditionalExpr, global::Ts.Checker.ContextFlags.None);
    }

    // completions.go:2890-2977 — getContextualType.
    internal static CheckerType? GetContextualType(
        Node previousToken, int position, SourceFile file, TsChecker typeChecker)
    {
        var parent = previousToken.Parent!;
        switch (previousToken.Kind)
        {
            case SyntaxKind.Identifier:
                return GetContextualTypeFromParent(previousToken, typeChecker, global::Ts.Checker.ContextFlags.None);
            case SyntaxKind.EqualsToken:
                switch (parent.Kind)
                {
                    case SyntaxKind.VariableDeclaration:
                        return typeChecker.GetContextualType(parent.Initializer()!, global::Ts.Checker.ContextFlags.None);
                    case SyntaxKind.BinaryExpression:
                        return typeChecker.GetTypeAtLocation(parent.AsBinaryExpression().Left!);
                    case SyntaxKind.JsxAttribute:
                        return typeChecker.GetContextualTypeForJsxAttribute(parent);
                    default:
                        return null;
                }
            case SyntaxKind.NewKeyword:
                return typeChecker.GetContextualType(parent, global::Ts.Checker.ContextFlags.None);
            case SyntaxKind.CaseKeyword:
                var caseClause = NodeTests.IsCaseClause(parent) ? parent : null;
                if (caseClause != null)
                {
                    return GetSwitchedType(caseClause, typeChecker);
                }
                return null;
            case SyntaxKind.OpenBraceToken:
                if (NodeTests.IsJsxExpression(parent) && !NodeTests.IsJsxElement(parent.Parent!) && !NodeTests.IsJsxFragment(parent.Parent!))
                {
                    return typeChecker.GetContextualTypeForJsxAttribute(parent.Parent!);
                }
                return null;
            case SyntaxKind.OpenBracketToken:
                // When completing after `[` in an array literal (e.g., `[/*here*/]`),
                // we should provide contextual type for the first element
                if (NodeTests.IsArrayLiteralExpression(parent))
                {
                    var contextualArrayType = typeChecker.GetContextualType(parent, global::Ts.Checker.ContextFlags.None);
                    if (contextualArrayType != null)
                    {
                        // Get the type for the first element (index 0)
                        return typeChecker.GetContextualTypeForArrayLiteralAtPosition(contextualArrayType, parent, position);
                    }
                }
                return null;
            case SyntaxKind.CloseBracketToken:
                // When completing after `]` (e.g., `[x]/*here*/`), we should not provide a contextual type
                // for the closing bracket token itself. Without this case, CloseBracketToken would fall through
                // to the default case, and if the parent is an array literal, GetContextualType would try to
                // find the token's index in the array elements (returning -1), leading to an out-of-bounds panic
                // in getContextualTypeForElementExpression.
                return null;
            case SyntaxKind.QuestionToken:
                // When completing after `?` in a ternary conditional (e.g., `foo(a ? /*here*/)`),
                // we need to look at the parent conditional expression to find the contextual type.
                if (NodeTests.IsConditionalExpression(parent))
                {
                    return GetContextualTypeForConditionalExpression(parent, position, file, typeChecker);
                }
                return null;
            case SyntaxKind.ColonToken:
                // When completing after `:` in a ternary conditional (e.g., `foo(a ? b : /*here*/)`),
                // we need to look at the parent conditional expression to find the contextual type.
                // Only handle this if parent is ConditionalExpression, otherwise fall through to default
                // (colons are used in other contexts like object literals, type annotations, etc.)
                if (NodeTests.IsConditionalExpression(parent))
                {
                    return GetContextualTypeForConditionalExpression(parent, position, file, typeChecker);
                }
                break;
            case SyntaxKind.CommaToken:
                // When completing after `,` in an array literal (e.g., `[x, /*here*/]`),
                // we should provide contextual type for the element after the comma.
                if (NodeTests.IsArrayLiteralExpression(parent))
                {
                    var contextualArrayType = typeChecker.GetContextualType(parent, global::Ts.Checker.ContextFlags.None);
                    if (contextualArrayType != null)
                    {
                        return typeChecker.GetContextualTypeForArrayLiteralAtPosition(contextualArrayType, parent, position);
                    }
                    return null;
                }
                break;
        }
        // Default case: see if we're in an argument position.
        var argInfo = GetArgumentInfoForCompletions(previousToken, position, file, typeChecker);
        if (argInfo != null)
        {
            return typeChecker.GetContextualTypeForArgumentAtIndex(argInfo.invocation, argInfo.argumentIndex);
        }
        else if (IsEqualityOperatorKind(previousToken.Kind) && NodeTests.IsBinaryExpression(parent) && IsEqualityOperatorKind(parent.AsBinaryExpression().OperatorToken!.Kind))
        {
            // completion at `x ===/**/`
            return typeChecker.GetTypeAtLocation(parent.AsBinaryExpression().Left!);
        }
        else
        {
            var contextualType = typeChecker.GetContextualType(previousToken, global::Ts.Checker.ContextFlags.IgnoreNodeInferences);
            if (contextualType != null)
            {
                return contextualType;
            }
            return typeChecker.GetContextualType(previousToken, global::Ts.Checker.ContextFlags.None);
        }
    }

    // utilities.go:1355-1372 — getContextualTypeFromParent.
    internal static CheckerType? GetContextualTypeFromParent(
        Node node, TsChecker typeChecker, global::Ts.Checker.ContextFlags contextFlags)
    {
        var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent)!;
        switch (parent.Kind)
        {
            case SyntaxKind.NewExpression:
                return typeChecker.GetContextualType(parent, contextFlags);
            case SyntaxKind.BinaryExpression:
                if (IsEqualityOperatorKind(parent.AsBinaryExpression().OperatorToken!.Kind))
                {
                    return typeChecker.GetTypeAtLocation(
                        node == parent.AsBinaryExpression().Right ? parent.AsBinaryExpression().Left! : parent.AsBinaryExpression().Right!);
                }
                return typeChecker.GetContextualType(node, contextFlags);
            case SyntaxKind.CaseClause:
                return GetSwitchedType(parent, typeChecker);
            default:
                return typeChecker.GetContextualType(node, contextFlags);
        }
    }

    // completions.go:2979-2981 — getSwitchedType.
    internal static CheckerType? GetSwitchedType(Node caseClause, TsChecker typeChecker)
    {
        return typeChecker.GetTypeAtLocation(caseClause.Parent!.Parent!.Expression()!);
    }

    // completions.go:2983-2992 — isEqualityOperatorKind.
    internal static bool IsEqualityOperatorKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.EqualsEqualsEqualsToken:
            case SyntaxKind.EqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsEqualsToken:
            case SyntaxKind.ExclamationEqualsToken:
                return true;
            default:
                return false;
        }
    }

    // completions.go:2994-2996 — isLiteral. We disregard boolean literals for completion purposes.
    internal static bool IsLiteral(CheckerType t)
    {
        return t.IsStringLiteral() || t.IsNumberLiteral() || t.IsBigIntLiteral();
    }

    // completions.go:2998-3017 — getRecommendedCompletion.
    internal static Symbol? GetRecommendedCompletion(Node previousToken, CheckerType contextualType, TsChecker typeChecker)
    {
        CheckerType[] types;
        if (contextualType.IsUnion())
        {
            types = contextualType.Types();
        }
        else
        {
            types = [contextualType];
        }
        // For a union, return the first one with a recommended completion.
        return global::Ts.Core.CoreUtil.FirstNonNil(
            types,
            t =>
            {
                var symbol = t.Symbol;
                // Don't make a recommended completion for an abstract class.
                if (symbol != null &&
                    (symbol.Flags & (SymbolFlags.EnumMember | SymbolFlags.Enum | SymbolFlags.Class)) != 0 &&
                    !IsAbstractConstructorSymbol(symbol))
                {
                    return GetFirstSymbolInChain(symbol, previousToken, typeChecker);
                }
                return null;
            });
    }

    // completions.go:3019-3025 — isAbstractConstructorSymbol.
    internal static bool IsAbstractConstructorSymbol(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.Class) != 0)
        {
            var declaration = AstUtilities.GetClassLikeDeclarationOfSymbol(symbol);
            return declaration != null && AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Abstract);
        }
        return false;
    }

    // completions.go:3027-3030 — startsWithQuote.
    internal static bool StartsWithQuote(string s)
    {
        return s.Length > 0 && (s[0] == '"' || s[0] == '\'');
    }

    // completions.go:3034-3062 — getClosestSymbolDeclaration.
    internal static Node? GetClosestSymbolDeclaration(Node? contextToken, Node location)
    {
        if (contextToken == null)
        {
            return null;
        }

        var closestDeclaration = AstUtilities.FindAncestorOrQuit(contextToken, node =>
        {
            if (AstUtilities.IsFunctionBlock(node) || IsArrowFunctionBody(node) || AstUtilities.IsBindingPattern(node))
            {
                return FindAncestorResult.Quit;
            }

            if ((NodeTests.IsParameterDeclaration(node) || NodeTests.IsTypeParameterDeclaration(node)) &&
                !NodeTests.IsIndexSignatureDeclaration(node.Parent!))
            {
                return FindAncestorResult.True;
            }
            return FindAncestorResult.False;
        });

        if (closestDeclaration == null)
        {
            closestDeclaration = AstUtilities.FindAncestorOrQuit(location, node =>
            {
                if (AstUtilities.IsFunctionBlock(node) || IsArrowFunctionBody(node) || AstUtilities.IsBindingPattern(node))
                {
                    return FindAncestorResult.Quit;
                }

                if (NodeTests.IsVariableDeclaration(node))
                {
                    return FindAncestorResult.True;
                }
                return FindAncestorResult.False;
            });
        }
        return closestDeclaration;
    }

    // completions.go:3064-3070 — isArrowFunctionBody.
    internal static bool IsArrowFunctionBody(Node node)
    {
        return node.Parent != null && NodeTests.IsArrowFunction(node.Parent) &&
            (node.Parent.Body() == node ||
                // const a = () => /**/;
                node.Kind == SyntaxKind.EqualsGreaterThanToken);
    }

    // completions.go:3072-3088 — isInTypeParameterDefault.
    internal static bool IsInTypeParameterDefault(Node? contextToken)
    {
        if (contextToken == null)
        {
            return false;
        }

        var node = contextToken;
        var parent = contextToken.Parent;
        while (parent != null)
        {
            if (NodeTests.IsTypeParameterDeclaration(parent))
            {
                return parent.AsTypeParameterDeclaration().DefaultType == node || node.Kind == SyntaxKind.EqualsToken;
            }
            node = parent;
            parent = parent.Parent;
        }

        return false;
    }

    // completions.go:3091-3094 — isDeprecated.
    internal static bool IsDeprecated(Symbol symbol, TsChecker typeChecker)
    {
        var declarations = TsChecker.SkipAlias(symbol, typeChecker)!.Declarations ?? [];
        return declarations.Length > 0 && global::Ts.Core.CoreUtil.Every(declarations, decl => typeChecker.IsDeprecatedDeclaration(decl));
    }

    // completions.go:3096-3108 — getReplacementRangeForContextToken.
    internal global::Ts.LsProto.Range? GetReplacementRangeForContextToken(SourceFile file, Node? contextToken, int position)
    {
        if (contextToken == null)
        {
            return null;
        }

        // !!! ensure range is single line
        switch (contextToken.Kind)
        {
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return CreateRangeFromStringLiteralLikeContent(file, contextToken, position);
            default:
                return CreateLspRangeFromNode(contextToken, file);
        }
    }

    // completions.go:3110-3121 — createRangeFromStringLiteralLikeContent.
    internal global::Ts.LsProto.Range? CreateRangeFromStringLiteralLikeContent(SourceFile file, Node node, int position)
    {
        var replacementEnd = node.End - 1;
        var nodeStart = global::Ts.AstNav.Tokens.GetStartOfNode(node, file, false /*includeJSDoc*/);
        if (AstUtilities.IsUnterminatedLiteral(node))
        {
            // we return no replacement range only if unterminated string is empty
            if (nodeStart == replacementEnd)
            {
                return null;
            }
            replacementEnd = global::System.Math.Min(position, node.End);
        }
        return CreateLspRangeFromBounds(nodeStart + 1, replacementEnd, file);
    }
    // completions.go:5322-5333 — getJSDocTagAtPosition.
    internal static Node? GetJSDocTagAtPosition(Node node, int position)
    {
        return AstUtilities.FindAncestorOrQuit(node, n =>
        {
            if (AstUtilities.IsJSDocTag(n) && n.Loc.ContainsInclusive(position))
            {
                return FindAncestorResult.True;
            }
            if (NodeTests.IsJSDoc(n))
            {
                return FindAncestorResult.Quit;
            }
            return FindAncestorResult.False;
        });
    }

    // completions.go:5335-5351 — tryGetTypeExpressionFromTag.
    internal static Node? TryGetTypeExpressionFromTag(Node tag)
    {
        if (IsTagWithTypeExpression(tag))
        {
            Node? typeExpression;
            if (NodeTests.IsJSDocTemplateTag(tag))
            {
                typeExpression = tag.AsJSDocTemplateTag().Constraint;
            }
            else
            {
                typeExpression = tag.TypeExpression();
            }
            if (typeExpression != null && typeExpression.Kind == SyntaxKind.JSDocTypeExpression)
            {
                return typeExpression;
            }
        }
        if (NodeTests.IsJSDocAugmentsTag(tag) || NodeTests.IsJSDocImplementsTag(tag))
        {
            return tag.ClassName();
        }
        return null;
    }

    // completions.go:5353-5364 — isTagWithTypeExpression.
    internal static bool IsTagWithTypeExpression(Node tag)
    {
        switch (tag.Kind)
        {
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
            case SyntaxKind.JSDocReturnTag:
            case SyntaxKind.JSDocTypeTag:
            case SyntaxKind.JSDocTypedefTag:
            case SyntaxKind.JSDocThrowsTag:
            case SyntaxKind.JSDocSatisfiesTag:
                return true;
            case SyntaxKind.JSDocTemplateTag:
                return tag.AsJSDocTemplateTag().Constraint != null;
            default:
                return false;
        }
    }
}
