// Ported from tsgo internal/ls/utilities.go — the possible-type-argument probe shared by
// completions and signature help (getPossibleTypeArgumentsInfo / getPossibleGenericSignatures /
// findPrecedingMatchingToken / removeOptionality).

using System;
using Ts.Ast;

namespace Ts.Ls;

// utilities.go:102-105
internal sealed class PossibleTypeArgumentInfo
{
    internal Node called = null!; // IdentifierNode
    internal int nTypeArguments;
}

internal static class LsTypeArgs
{
    // utilities.go:107-188 — Get info for an expression like `f <` that may be the start of type
    // arguments.
    internal static PossibleTypeArgumentInfo? GetPossibleTypeArgumentsInfo(Node? tokenIn, SourceFile sourceFile)
    {
        // This is a rare case, but one that saves on a _lot_ of work if true - if the source file has _no_ `<` character,
        // then there obviously can't be any type arguments - no expensive brace-matching backwards scanning required
        if (!sourceFile.Text().Contains('<'))
        {
            return null;
        }

        var token = tokenIn;
        // This function determines if the node could be a type argument position
        // When editing, it is common to have an incomplete type argument list (e.g. missing ">"),
        // so the tree can have any shape depending on the tokens before the current node.
        // Instead, scanning for an identifier followed by a "<" before current node
        // will typically give us better results than inspecting the tree.
        // Note that we also balance out the already provided type arguments, arrays, object literals while doing so.
        var remainingLessThanTokens = 0;
        var nTypeArguments = 0;
        while (token != null)
        {
            switch (token.Kind)
            {
                case SyntaxKind.LessThanToken:
                    // Found the beginning of the generic argument expression
                    token = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, token.Pos);
                    if (token != null && token.Kind == SyntaxKind.QuestionDotToken)
                    {
                        token = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, token.Pos);
                    }
                    if (token == null || !NodeTests.IsIdentifier(token))
                    {
                        return null;
                    }
                    if (remainingLessThanTokens == 0)
                    {
                        if (AstUtilities.IsDeclarationName(token))
                        {
                            return null;
                        }
                        return new PossibleTypeArgumentInfo
                        {
                            called = token,
                            nTypeArguments = nTypeArguments,
                        };
                    }
                    remainingLessThanTokens--;
                    break;
                case SyntaxKind.GreaterThanGreaterThanGreaterThanToken:
                    remainingLessThanTokens += 3;
                    break;
                case SyntaxKind.GreaterThanGreaterThanToken:
                    remainingLessThanTokens += 2;
                    break;
                case SyntaxKind.GreaterThanToken:
                    remainingLessThanTokens++;
                    break;
                case SyntaxKind.CloseBraceToken:
                    // This can be object type, skip until we find the matching open brace token
                    token = FindPrecedingMatchingToken(token, SyntaxKind.OpenBraceToken, sourceFile);
                    if (token == null)
                    {
                        return null;
                    }
                    break;
                case SyntaxKind.CloseParenToken:
                    token = FindPrecedingMatchingToken(token, SyntaxKind.OpenParenToken, sourceFile);
                    if (token == null)
                    {
                        return null;
                    }
                    break;
                case SyntaxKind.CloseBracketToken:
                    token = FindPrecedingMatchingToken(token, SyntaxKind.OpenBracketToken, sourceFile);
                    if (token == null)
                    {
                        return null;
                    }
                    break;
                case SyntaxKind.CommaToken:
                    // Valid tokens in a type name. Skip.
                    nTypeArguments++;
                    break;
                case SyntaxKind.EqualsGreaterThanToken:
                case SyntaxKind.Identifier:
                case SyntaxKind.StringLiteral:
                case SyntaxKind.NumericLiteral:
                case SyntaxKind.BigIntLiteral:
                case SyntaxKind.TrueKeyword:
                case SyntaxKind.FalseKeyword:
                case SyntaxKind.TypeOfKeyword:
                case SyntaxKind.ExtendsKeyword:
                case SyntaxKind.KeyOfKeyword:
                case SyntaxKind.DotToken:
                case SyntaxKind.BarToken:
                case SyntaxKind.QuestionToken:
                case SyntaxKind.ColonToken:
                    // do nothing
                    break;
                default:
                    if (!AstUtilities.IsTypeNode(token))
                    {
                        // Invalid token in type
                        return null;
                    }
                    break;
            }
            token = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, token.Pos);
        }
        return null;
    }

    // utilities.go:1106-1121
    internal static global::Ts.Checker.Signature[] GetPossibleGenericSignatures(Node called, int typeArgumentCount, global::Ts.Checker.Checker c)
    {
        var typeAtLocation = c.GetTypeAtLocation(called);
        if (AstUtilities.IsOptionalChain(called.Parent!))
        {
            typeAtLocation = RemoveOptionality(typeAtLocation, AstUtilities.IsOptionalChainRoot(called.Parent!), true /*isOptionalChain*/, c);
        }
        global::Ts.Checker.Signature[] signatures;
        if (NodeTests.IsNewExpression(called.Parent!))
        {
            signatures = c.GetSignaturesOfType(typeAtLocation, global::Ts.Checker.SignatureKind.Construct);
        }
        else
        {
            signatures = c.GetSignaturesOfType(typeAtLocation, global::Ts.Checker.SignatureKind.Call);
        }
        return System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(signatures,
            s => s.TypeParameters() != null && s.TypeParameters()!.Length >= typeArgumentCount));
    }

    // utilities.go:1123-1130
    internal static global::Ts.Checker.Type RemoveOptionality(global::Ts.Checker.Type t, bool isOptionalExpression, bool isOptionalChain, global::Ts.Checker.Checker c)
    {
        if (isOptionalExpression)
        {
            return c.GetNonNullableType(t);
        }
        else if (isOptionalChain)
        {
            return c.GetNonOptionalType(t);
        }
        return t;
    }

    // utilities.go:1152-1186
    internal static Node? FindPrecedingMatchingToken(Node token, SyntaxKind matchingTokenKind, SourceFile sourceFile)
    {
        var closeTokenText = global::Ts.Scanner.Scanner.TokenToString(token.Kind);
        var matchingTokenText = global::Ts.Scanner.Scanner.TokenToString(matchingTokenKind);
        // Text-scan based fast path - can be bamboozled by comments and other trivia, but often provides
        // a good, fast approximation without too much extra work in the cases where it fails.
        var bestGuessIndex = sourceFile.Text().LastIndexOf(matchingTokenText, StringComparison.Ordinal);
        if (bestGuessIndex == -1)
        {
            return null; // if the token text doesn't appear in the file, there can't be a match - super fast bail
        }
        // we can only use the textual result directly if we didn't have to count any close tokens within the range
        if (sourceFile.Text().LastIndexOf(closeTokenText, StringComparison.Ordinal) < bestGuessIndex)
        {
            var nodeAtGuess = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, bestGuessIndex + 1);
            if (nodeAtGuess != null && nodeAtGuess.Kind == matchingTokenKind)
            {
                return nodeAtGuess;
            }
        }
        var tokenKind = token.Kind;
        var remainingMatchingTokens = 0;
        while (true)
        {
            var preceding = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, token.Pos);
            if (preceding == null)
            {
                return null;
            }
            token = preceding;
            if (token.Kind == matchingTokenKind)
            {
                if (remainingMatchingTokens == 0)
                {
                    return token;
                }
                remainingMatchingTokens--;
            }
            else if (token.Kind == tokenKind)
            {
                remainingMatchingTokens++;
            }
        }
    }
}
