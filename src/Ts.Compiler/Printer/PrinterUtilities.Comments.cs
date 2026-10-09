// Ported from tsgo internal/printer/utilities.go — comment-recognition + indent helpers used on the
// preserveSourceNewlines / comment emit paths.

namespace Ts.Printer;

using System;

using Ts.Ast;

public static partial class PrinterUtilities
{
    // utilities.go:422 — siblingNodePositionsAreComparable.
    internal static bool SiblingNodePositionsAreComparable(EmitContext emitContext, Node previousNode, Node nextNode)
    {
        if (nextNode.Pos < previousNode.End)
        {
            return false;
        }

        previousNode = emitContext.MostOriginal(previousNode)!;
        nextNode = emitContext.MostOriginal(nextNode)!;
        Node? parent = previousNode.Parent;
        if (parent == null || parent != nextNode.Parent)
        {
            return false;
        }

        NodeList? parentNodeArray = GetContainingNodeArray(previousNode);
        if (parentNodeArray != null)
        {
            int prevNodeIndex = Array.IndexOf(parentNodeArray.Nodes, previousNode);
            return prevNodeIndex >= 0 && Array.IndexOf(parentNodeArray.Nodes, nextNode) == prevNodeIndex + 1;
        }

        return false;
    }

    // utilities.go:443 — getContainingNodeArray.
    private static NodeList? GetContainingNodeArray(Node node)
    {
        Node? parent = node.Parent;
        if (parent == null)
        {
            return null;
        }

        switch (node.Kind)
        {
            case SyntaxKind.TypeParameter:
                if (AstUtilities.IsFunctionLike(parent) || AstUtilities.IsClassLike(parent) || NodeTests.IsInterfaceDeclaration(parent) || AstUtilities.IsTypeOrJSTypeAliasDeclaration(parent))
                {
                    return parent.TypeParameterList();
                }
                else if (NodeTests.IsInferTypeNode(parent))
                {
                    // Falls through to the parent-kind switch below.
                }
                else
                {
                    throw new InvalidOperationException($"Unexpected TypeParameter parent: {parent.Kind}");
                }
                break;
            case SyntaxKind.Parameter:
                return node.Parent!.ParameterList();
            case SyntaxKind.TemplateLiteralTypeSpan:
                return node.Parent!.AsTemplateLiteralTypeNode().TemplateSpans;
            case SyntaxKind.TemplateSpan:
                return node.Parent!.AsTemplateExpression().TemplateSpans;
            case SyntaxKind.Decorator:
                if (AstUtilities.CanHaveDecorators(node.Parent!))
                {
                    ModifierList? modifiers = node.Parent!.Modifiers();
                    if (modifiers != null)
                    {
                        return modifiers;
                    }
                }
                return null;
            case SyntaxKind.HeritageClause:
                if (AstUtilities.IsClassLike(node.Parent!))
                {
                    return node.Parent!.ClassLikeData()!.HeritageClauses;
                }
                return node.Parent!.AsInterfaceDeclaration().HeritageClauses;
        }

        switch (parent.Kind)
        {
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.InterfaceDeclaration:
                if (AstUtilities.IsTypeElement(node))
                {
                    return parent.MemberList();
                }
                break;
            case SyntaxKind.UnionType:
                return parent.AsUnionTypeNode().Types;
            case SyntaxKind.IntersectionType:
                return parent.AsIntersectionTypeNode().Types;
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.TupleType:
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
                return parent.ElementList();
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.JsxAttributes:
                return parent.PropertyList();
            case SyntaxKind.CallExpression:
            {
                CallExpression p = parent.AsCallExpression();
                if (AstUtilities.IsTypeNode(node))
                {
                    return p.TypeArguments;
                }
                if (node != p.Expression)
                {
                    return p.Arguments;
                }
                break;
            }
            case SyntaxKind.NewExpression:
            {
                NewExpression p = parent.AsNewExpression();
                if (AstUtilities.IsTypeNode(node))
                {
                    return p.TypeArguments;
                }
                if (node != p.Expression)
                {
                    return p.Arguments;
                }
                break;
            }
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxFragment:
                if (AstUtilities.IsJsxChild(node))
                {
                    return parent.Children();
                }
                break;
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
                if (AstUtilities.IsTypeNode(node))
                {
                    return parent.TypeArgumentList();
                }
                break;
            case SyntaxKind.Block:
            case SyntaxKind.ModuleBlock:
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                return parent.StatementList();
            case SyntaxKind.CaseBlock:
                return parent.AsCaseBlock().Clauses;
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                if (AstUtilities.IsClassElement(node))
                {
                    return parent.MemberList();
                }
                break;
            case SyntaxKind.EnumDeclaration:
                if (NodeTests.IsEnumMember(node))
                {
                    return parent.MemberList();
                }
                break;
            case SyntaxKind.SourceFile:
                if (AstUtilities.IsStatement(node))
                {
                    return parent.StatementList();
                }
                break;
        }

        if (AstUtilities.IsModifier(node))
        {
            ModifierList? modifiers = parent.Modifiers();
            if (modifiers != null)
            {
                return modifiers;
            }
        }

        return null;
    }

    // utilities.go:651 — IsFileLevelUniqueName.
    internal static bool IsFileLevelUniqueName(SourceFile sourceFile, string name, Func<string, bool>? hasGlobalName)
    {
        if (hasGlobalName != null && hasGlobalName(name))
        {
            return false;
        }
        return !(sourceFile.Identifiers?.ContainsKey(name) ?? false);
    }

    // utilities.go:804 — IsRecognizedTripleSlashComment.
    public static bool IsRecognizedTripleSlashComment(string text, CommentRange commentRange)
    {
        if (commentRange.Kind == SyntaxKind.SingleLineCommentTrivia &&
            commentRange.Len > 2 &&
            text[commentRange.Pos + 1] == '/' &&
            text[commentRange.Pos + 2] == '/')
        {
            string sub = text[(commentRange.Pos + 3)..commentRange.End];
            int pos = 0;
            SkipWhiteSpaceSingleLine(sub, ref pos);
            if (!MatchRune(sub, ref pos, '<'))
            {
                return false;
            }
            if (MatchString(sub, ref pos, "reference"))
            {
                if (!MatchWhiteSpaceSingleLine(sub, ref pos))
                {
                    return false;
                }
                if (!MatchString(sub, ref pos, "path") &&
                    !MatchString(sub, ref pos, "types") &&
                    !MatchString(sub, ref pos, "lib") &&
                    !MatchString(sub, ref pos, "no-default-lib"))
                {
                    return false;
                }
                SkipWhiteSpaceSingleLine(sub, ref pos);
                if (!MatchRune(sub, ref pos, '='))
                {
                    return false;
                }
                SkipWhiteSpaceSingleLine(sub, ref pos);
                if (!MatchQuotedString(sub, ref pos))
                {
                    return false;
                }
            }
            else if (MatchString(sub, ref pos, "amd-dependency"))
            {
                if (!MatchWhiteSpaceSingleLine(sub, ref pos))
                {
                    return false;
                }
                if (!MatchString(sub, ref pos, "path"))
                {
                    return false;
                }
                SkipWhiteSpaceSingleLine(sub, ref pos);
                if (!MatchRune(sub, ref pos, '='))
                {
                    return false;
                }
                SkipWhiteSpaceSingleLine(sub, ref pos);
                if (!MatchQuotedString(sub, ref pos))
                {
                    return false;
                }
            }
            else if (MatchString(sub, ref pos, "amd-module"))
            {
                SkipWhiteSpaceSingleLine(sub, ref pos);
            }
            else
            {
                return false;
            }
            int index = sub[pos..].IndexOf("/>", StringComparison.Ordinal);
            return index != -1;
        }

        return false;
    }

    // utilities.go — skipWhiteSpaceSingleLine. PORT-NOTE: Go decodes UTF-8 runes; single-line whitespace
    // is all BMP, so char iteration is equivalent.
    private static void SkipWhiteSpaceSingleLine(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(text[pos]))
            {
                break;
            }
            pos++;
        }
    }

    // utilities.go — matchRune (matched runes are ASCII here, so char comparison is faithful).
    private static bool MatchRune(string text, ref int pos, char expected)
    {
        if (pos < text.Length && text[pos] == expected)
        {
            pos++;
            return true;
        }
        return false;
    }

    // utilities.go — matchString.
    private static bool MatchString(string text, ref int pos, string expected)
    {
        int textPos = pos;
        int expectedPos = 0;
        while (expectedPos < expected.Length)
        {
            if (textPos >= text.Length)
            {
                return false;
            }
            if (!MatchRune(text, ref textPos, expected[expectedPos]))
            {
                return false;
            }
            expectedPos++;
        }
        pos = textPos;
        return true;
    }

    // utilities.go — matchWhiteSpaceSingleLine.
    private static bool MatchWhiteSpaceSingleLine(string text, ref int pos)
    {
        int startPos = pos;
        SkipWhiteSpaceSingleLine(text, ref pos);
        return pos != startPos;
    }

    // utilities.go — matchQuotedString.
    private static bool MatchQuotedString(string text, ref int pos)
    {
        int textPos = pos;
        char quoteChar;
        if (MatchRune(text, ref textPos, '\''))
        {
            quoteChar = '\'';
        }
        else if (MatchRune(text, ref textPos, '"'))
        {
            quoteChar = '"';
        }
        else
        {
            return false;
        }
        while (textPos < text.Length)
        {
            char ch = text[textPos];
            textPos++;
            if (ch == quoteChar)
            {
                pos = textPos;
                return true;
            }
        }
        return false;
    }

    // utilities.go:861 — isJSDocLikeText.
    internal static bool IsJSDocLikeText(string text, CommentRange comment) =>
        comment.Kind == SyntaxKind.MultiLineCommentTrivia &&
        comment.Len >= 5 &&
        text[comment.Pos + 2] == '*' &&
        text[comment.Pos + 3] != '/';

    // utilities.go:868 — IsPinnedComment.
    public static bool IsPinnedComment(string text, CommentRange comment) =>
        comment.Kind == SyntaxKind.MultiLineCommentTrivia &&
        comment.Len > 5 &&
        text[comment.Pos + 2] == '!';

    // utilities.go:567 — originalNodesHaveSameParent.
    internal static bool OriginalNodesHaveSameParent(EmitContext emitContext, Node nodeA, Node nodeB)
    {
        var a = emitContext.MostOriginal(nodeA);
        if (a!.Parent != null)
        {
            // For performance, do not call MostOriginal for nodeB if nodeA doesn't even have a parent node.
            var b = emitContext.MostOriginal(nodeB);
            return a.Parent == b!.Parent;
        }
        return false;
    }

    // utilities.go:874 — calculateIndent. PORT-NOTE: Go decodes UTF-8 runes and advances by byte size;
    // every single-line whitespace character is in the BMP (one UTF-16 code unit), so iterating chars
    // and advancing by one is equivalent.
    internal static int CalculateIndent(string text, int pos, int end)
    {
        int currentLineIndent = 0;
        int indentSize = TextWriter.GetDefaultIndentSize();
        while (pos < end)
        {
            char ch = text[pos];
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                break;
            }
            if (ch == '\t')
            {
                // Tabs = TabSize = indent size and go to next tabStop
                currentLineIndent += indentSize - (currentLineIndent % indentSize);
            }
            else
            {
                // Single space
                currentLineIndent++;
            }
            pos++;
        }

        return currentLineIndent;
    }
}
