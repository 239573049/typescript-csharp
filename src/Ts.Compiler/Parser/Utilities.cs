// Ported from tsgo internal/parser/utilities.go
using System;
using System.Collections.Generic;

using Ts.Ast;
using Ts.Core;


namespace Ts.Parser;

public static class Utilities
{
    public static LanguageVariant GetLanguageVariant(ScriptKind scriptKind)
    {
        switch (scriptKind)
        {
            case ScriptKind.Tsx:
            case ScriptKind.Jsx:
            case ScriptKind.JS:
            case ScriptKind.Json:
                // .tsx and .jsx files are treated as jsx language variant.
                return LanguageVariant.Jsx;
        }
        return LanguageVariant.Standard;
    }

    public static bool TokenIsIdentifierOrKeyword(SyntaxKind token) => token >= SyntaxKind.Identifier;

    public static bool TokenIsIdentifierOrKeywordOrGreaterThan(SyntaxKind token) =>
        token == SyntaxKind.GreaterThanToken || TokenIsIdentifierOrKeyword(token);

    // GetJSDocCommentRanges collects the /** */ comment ranges attached to a node.
    public static List<CommentRange> GetJSDocCommentRanges(NodeFactory f, List<CommentRange> commentRanges, Node node, string text)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Parameter:
            case SyntaxKind.TypeParameter:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.ParenthesizedExpression:
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.ExportSpecifier:
                foreach (CommentRange commentRange in CommentScanner.GetTrailingCommentRanges(f, text, node.Pos))
                {
                    commentRanges.Add(commentRange);
                }
                foreach (CommentRange commentRange in CommentScanner.GetLeadingCommentRanges(f, text, node.Pos))
                {
                    commentRanges.Add(commentRange);
                }
                break;
            default:
                foreach (CommentRange commentRange in CommentScanner.GetLeadingCommentRanges(f, text, node.Pos))
                {
                    commentRanges.Add(commentRange);
                }
                break;
        }
        // Keep if the comment starts with '/**' but not if it is '/**/'
        commentRanges.RemoveAll(comment =>
        {
            int commentStart = comment.Pos;
            int commentLen = comment.End - commentStart;
            return comment.End > node.End || commentLen < 4 || text[commentStart + 1] != '*' ||
                   text[commentStart + 2] != '*' || text[commentStart + 3] == '/';
        });
        return commentRanges;
    }

    public static bool IsKeywordOrPunctuation(SyntaxKind token) =>
        NodeTests.IsKeywordKind(token) || NodeTests.IsPunctuationKind(token);

    public static bool IsJSDocLikeText(string text) =>
        text.Length >= 4 && text[1] == '*' && text[2] == '*' && text[3] != '/';
}
