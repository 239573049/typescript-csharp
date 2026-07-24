// PORT-NOTE: ports tsgo internal/scanner/scanner.go GetLeadingCommentRanges /
// GetTrailingCommentRanges / iterateCommentRanges. These live in the Go scanner package
// but were not yet present in Ts.Scanner; they are localized here (parser is their only
// consumer) rather than modifying the out-of-scope scanner. Positions are UTF-16 code units.
using System.Collections.Generic;

using Ts.Ast;


namespace Ts.Parser;

public static class CommentScanner
{
    public static IEnumerable<CommentRange> GetLeadingCommentRanges(NodeFactory f, string text, int pos) =>
        IterateCommentRanges(f, text, pos, false);

    public static IEnumerable<CommentRange> GetTrailingCommentRanges(NodeFactory f, string text, int pos) =>
        IterateCommentRanges(f, text, pos, true);

    private static IEnumerable<CommentRange> IterateCommentRanges(NodeFactory f, string text, int pos, bool trailing)
    {
        int pendingPos = 0;
        int pendingEnd = 0;
        SyntaxKind pendingKind = SyntaxKind.Unknown;
        bool pendingHasTrailingNewLine = false;
        bool hasPendingCommentRange = false;
        bool collecting = trailing;
        if (pos == 0)
        {
            collecting = true;
            if (global::Ts.Scanner.Scanner.IsShebangTrivia(text, pos))
            {
                pos = global::Ts.Scanner.Scanner.ScanShebangTrivia(text, pos);
            }
        }

        while (pos >= 0 && pos < text.Length)
        {
            (int ch, int size) = DecodeRune(text, pos);
            switch (ch)
            {
                case '\r':
                    if (pos + 1 < text.Length && text[pos + 1] == '\n')
                    {
                        pos++;
                    }
                    goto case '\n';
                case '\n':
                    pos++;
                    if (trailing)
                    {
                        goto endScan;
                    }
                    collecting = true;
                    if (hasPendingCommentRange)
                    {
                        pendingHasTrailingNewLine = true;
                    }
                    continue;
                case '\t':
                case '\v':
                case '\f':
                case ' ':
                    pos++;
                    continue;
                case '/':
                    {
                        char nextChar = pos + 1 < text.Length ? text[pos + 1] : '\0';
                        bool hasTrailingNewLine = false;
                        if (nextChar == '/' || nextChar == '*')
                        {
                            SyntaxKind kind = nextChar == '/'
                                ? SyntaxKind.SingleLineCommentTrivia
                                : SyntaxKind.MultiLineCommentTrivia;

                            int startPos = pos;
                            pos += 2;
                            if (nextChar == '/')
                            {
                                while (pos < text.Length)
                                {
                                    (int c, int s) = DecodeRune(text, pos);
                                    if (global::Ts.StringUtil.StringUtil.IsLineBreak(c))
                                    {
                                        hasTrailingNewLine = true;
                                        break;
                                    }
                                    pos += s;
                                }
                            }
                            else
                            {
                                int i = text.IndexOf("*/", pos, System.StringComparison.Ordinal);
                                if (i >= 0)
                                {
                                    pos = i + 2;
                                }
                                else
                                {
                                    pos = text.Length;
                                }
                            }

                            if (collecting)
                            {
                                if (hasPendingCommentRange)
                                {
                                    yield return f.NewCommentRange(pendingKind, pendingPos, pendingEnd, pendingHasTrailingNewLine);
                                }
                                pendingPos = startPos;
                                pendingEnd = pos;
                                pendingKind = kind;
                                pendingHasTrailingNewLine = hasTrailingNewLine;
                                hasPendingCommentRange = true;
                            }
                            continue;
                        }
                        goto endScan;
                    }
                default:
                    if (ch > 0x7F && global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(ch))
                    {
                        if (hasPendingCommentRange && global::Ts.StringUtil.StringUtil.IsLineBreak(ch))
                        {
                            pendingHasTrailingNewLine = true;
                        }
                        pos += size;
                        continue;
                    }
                    goto endScan;
            }
        }

    endScan:
        if (hasPendingCommentRange)
        {
            yield return f.NewCommentRange(pendingKind, pendingPos, pendingEnd, pendingHasTrailingNewLine);
        }
    }

    // Decodes the UTF-16 code point at pos; size is in UTF-16 code units (1 or 2).
    private static (int Ch, int Size) DecodeRune(string text, int pos)
    {
        char c = text[pos];
        if (char.IsHighSurrogate(c) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
        {
            return (char.ConvertToUtf32(c, text[pos + 1]), 2);
        }
        return (c, 1);
    }
}
