// Missing-callee host for the syntax-side LS features (folding.go / symbols.go ports).
//
// PORT-NOTE: each member below is an already-specified Go function whose C# home has
// not landed yet (or is `internal` to Ts.Compiler and therefore unreachable from this
// assembly). They are hosted here as faithful local ports rather than throwing stubs
// because every one of them sits on the hot path of the folding/document-symbol
// features; when the owning package ports them publicly, these copies should be
// deleted and call sites redirected. Positions are UTF-16 code units throughout.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // ls/utilities.go:287 — createLspRangeFromNode.
    internal global::Ts.LsProto.Range CreateLspRangeFromNode(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile file)
    {
        return CreateLspRangeFromBounds(
            global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, file, false /*includeJSDoc*/),
            node.End,
            file);
    }

    // ls/utilities.go:295 — createLspRangeFromBounds.
    internal global::Ts.LsProto.Range CreateLspRangeFromBounds(int start, int end, global::Ts.Ast.SourceFile file)
    {
        return new global::Ts.LsProto.Range
        {
            Start = CreateLspPosition(start, file),
            End = CreateLspPosition(end, file),
        };
    }

    // ls/utilities.go:303 — createLspPosition. The lsconv.Converters position encoding
    // shim collapses here: C# strings are UTF-16 code units, which is exactly the LSP
    // UTF-16 position encoding, so the conversion is pure line-start arithmetic.
    internal global::Ts.LsProto.Position CreateLspPosition(int position, global::Ts.Ast.SourceFile file)
    {
        return LsDeps.PositionToLineAndCharacter(file, position);
    }
}

internal static class LsDeps
{
    // lsconv/converters.go:194 — Converters.PositionToLineAndCharacter (UTF-16 direct).
    internal static global::Ts.LsProto.Position PositionToLineAndCharacter(global::Ts.Ast.SourceFile file, int position)
    {
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(file);
        var line = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, position);
        var character = position - lineStarts[line];
        return new global::Ts.LsProto.Position
        {
            Line = (uint)line,
            Character = (uint)character,
        };
    }

    // lsconv/converters.go:144 — Converters.LineAndCharacterToPosition (UTF-16 direct).
    // Out-of-range lines clamp to the end of the file (the Go converter asserts; the
    // only C# caller feeds back positions produced by PositionToLineAndCharacter).
    internal static int LineAndCharacterToPosition(global::Ts.Ast.SourceFile file, uint line, uint character)
    {
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(file);
        if (line >= (uint)lineStarts.Length)
        {
            return file.Text().Length;
        }
        return Math.Min(lineStarts[(int)line] + (int)character, file.Text().Length);
    }

    // printer/utilities.go — PositionsAreOnSameLine. The C# port exists at
    // Ts.Printer.PrinterUtilities but is `internal` to Ts.Compiler.
    internal static bool PositionsAreOnSameLine(int pos1, int pos2, global::Ts.Ast.SourceFile sourceFile)
    {
        return global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, pos1) ==
            global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, pos2);
    }

    // ls/completions.go:2458 — getLineOfPosition.
    internal static int GetLineOfPosition(global::Ts.Ast.SourceFile file, int pos)
    {
        return global::Ts.Scanner.Scanner.GetECMALineOfPosition(file, pos);
    }

    // ls/completions.go:2463 — getLineEndOfPosition.
    internal static int GetLineEndOfPosition(global::Ts.Ast.SourceFile file, int pos)
    {
        var line = GetLineOfPosition(file, pos);
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(file);
        int lastCharPos;
        if (line + 1 >= lineStarts.Length)
        {
            lastCharPos = file.End;
        }
        else
        {
            lastCharPos = lineStarts[line + 1] - 1;
        }
        var fullText = file.Text();
        if (lastCharPos > 0 && lastCharPos < fullText.Length && fullText[lastCharPos] == '\n' && fullText[lastCharPos - 1] == '\r')
        {
            return lastCharPos - 1;
        }
        return lastCharPos;
    }

    // ast/utilities.go:1872 — IsAnyImportSyntax.
    internal static bool IsAnyImportSyntax(global::Ts.Ast.Node node)
    {
        return node.Kind is global::Ts.Ast.SyntaxKind.ImportDeclaration or global::Ts.Ast.SyntaxKind.ImportEqualsDeclaration;
    }

    // ast/ast.go:2957 — GetDeclarationName (the string form).
    internal static string GetDeclarationName(global::Ts.Ast.Node declaration)
    {
        var name = global::Ts.Ast.AstUtilities.GetNonAssignedNameOfDeclaration(declaration);
        if (name != null)
        {
            if (global::Ts.Ast.NodeTests.IsComputedPropertyName(name))
            {
                if (global::Ts.Ast.AstUtilities.IsStringOrNumericLiteralLike(name.Expression()!))
                {
                    return name.Expression()!.Text();
                }
                if (global::Ts.Ast.NodeTests.IsPropertyAccessExpression(name.Expression()!))
                {
                    return name.Expression()!.Name()!.Text();
                }
            }
            else if (global::Ts.Ast.AstUtilities.IsPropertyName(name))
            {
                return name.Text();
            }
        }
        return "";
    }

    // ls/utilities.go:94 — isInComment.
    internal static global::Ts.Ast.CommentRange? IsInComment(global::Ts.Ast.SourceFile file, int position, global::Ts.Ast.Node tokenAtPosition)
    {
        return GetRangeOfEnclosingComment(file, position, global::Ts.AstNav.Tokens.FindPrecedingToken(file, position), tokenAtPosition);
    }

    // ls/format.go:137 — getRangeOfEnclosingComment.
    // Unlike the TS implementation, this function *will not* compute default values for
    // `precedingToken` and `tokenAtPosition`.
    internal static global::Ts.Ast.CommentRange? GetRangeOfEnclosingComment(
        global::Ts.Ast.SourceFile file,
        int position,
        global::Ts.Ast.Node? precedingToken,
        global::Ts.Ast.Node tokenAtPosition)
    {
        var jsdoc = global::Ts.Ast.AstUtilities.FindAncestor(tokenAtPosition, global::Ts.Ast.NodeTests.IsJSDoc);
        if (jsdoc != null)
        {
            tokenAtPosition = jsdoc.Parent!;
        }
        var tokenStart = global::Ts.AstNav.Tokens.GetStartOfNode(tokenAtPosition, file, false /*includeJSDoc*/);
        if (tokenStart <= position && position < tokenAtPosition.End)
        {
            return null;
        }

        // Between two consecutive tokens, all comments are either trailing on the former
        // or leading on the latter (and none are in both lists).
        IEnumerable<global::Ts.Ast.CommentRange> trailingRangesOfPreviousToken = precedingToken != null
            ? GetTrailingCommentRanges(file.Text(), precedingToken.End)
            : [];
        var leadingRangesOfNextToken = GetLeadingCommentRangesOfNode(tokenAtPosition, file);
        foreach (var commentRange in Concatenate(trailingRangesOfPreviousToken, leadingRangesOfNextToken))
        {
            // The end marker of a single-line comment does not include the newline character.
            // In the following case where the cursor is at `^`, we are inside a comment:
            //
            //    // asdf   ^\n
            //
            // But for closed multi-line comments, we don't want to be inside the comment in the following case:
            //
            //    /* asdf */^
            //
            // Internally, we represent the end of the comment prior to the newline and at the '/', respectively.
            //
            // However, unterminated multi-line comments lack a `/`, end at the end of the file, and *do* contain their end.
            //
            if (commentRange.TextRange.ContainsExclusive(position) ||
                (position == commentRange.End &&
                    (commentRange.Kind == global::Ts.Ast.SyntaxKind.SingleLineCommentTrivia || position == file.Text().Length)))
            {
                return commentRange;
            }
        }
        return null;
    }

    // core.ConcatenateSeq — sequence concatenation used by getRangeOfEnclosingComment.
    private static IEnumerable<global::Ts.Ast.CommentRange> Concatenate(
        IEnumerable<global::Ts.Ast.CommentRange> first,
        IEnumerable<global::Ts.Ast.CommentRange> second)
    {
        foreach (var item in first)
        {
            yield return item;
        }
        foreach (var item in second)
        {
            yield return item;
        }
    }

    // ls/utilities.go:1205 — getLeadingCommentRangesOfNode.
    internal static IEnumerable<global::Ts.Ast.CommentRange> GetLeadingCommentRangesOfNode(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile file)
    {
        if (node.Kind == global::Ts.Ast.SyntaxKind.JsxText)
        {
            return [];
        }
        return GetLeadingCommentRanges(file.Text(), node.Pos);
    }

    // scanner.go — GetLeadingCommentRanges / GetTrailingCommentRanges / iterateCommentRanges.
    // The C# port exists at Ts.Parser.CommentScanner but is `internal` to Ts.Compiler.
    // The Go *ast.NodeFactory parameter only constructs plain CommentRange values, so it
    // drops out of this copy.
    internal static IEnumerable<global::Ts.Ast.CommentRange> GetLeadingCommentRanges(string text, int pos) =>
        IterateCommentRanges(text, pos, false);

    internal static IEnumerable<global::Ts.Ast.CommentRange> GetTrailingCommentRanges(string text, int pos) =>
        IterateCommentRanges(text, pos, true);

    private static global::Ts.Ast.CommentRange NewCommentRange(global::Ts.Ast.SyntaxKind kind, int pos, int end, bool hasTrailingNewLine)
    {
        return new global::Ts.Ast.CommentRange
        {
            TextRange = new global::Ts.Core.TextRange(pos, end),
            Kind = kind,
            HasTrailingNewLine = hasTrailingNewLine,
        };
    }

    private static IEnumerable<global::Ts.Ast.CommentRange> IterateCommentRanges(string text, int pos, bool trailing)
    {
        var pendingPos = 0;
        var pendingEnd = 0;
        var pendingKind = global::Ts.Ast.SyntaxKind.Unknown;
        var pendingHasTrailingNewLine = false;
        var hasPendingCommentRange = false;
        var collecting = trailing;
        if (pos == 0)
        {
            collecting = true;
            if (IsShebangTrivia(text, pos))
            {
                pos = ScanShebangTrivia(text, pos);
            }
        }

        while (pos >= 0 && pos < text.Length)
        {
            var (ch, size) = DecodeRune(text, pos);
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
                        var nextChar = pos + 1 < text.Length ? text[pos + 1] : '\0';
                        var hasTrailingNewLine = false;
                        if (nextChar == '/' || nextChar == '*')
                        {
                            var kind = nextChar == '/'
                                ? global::Ts.Ast.SyntaxKind.SingleLineCommentTrivia
                                : global::Ts.Ast.SyntaxKind.MultiLineCommentTrivia;

                            var startPos = pos;
                            pos += 2;
                            if (nextChar == '/')
                            {
                                while (pos < text.Length)
                                {
                                    var (c, s) = DecodeRune(text, pos);
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
                                var i = text.IndexOf("*/", pos, StringComparison.Ordinal);
                                pos = i >= 0 ? i + 2 : text.Length;
                            }

                            if (collecting)
                            {
                                if (hasPendingCommentRange)
                                {
                                    yield return NewCommentRange(pendingKind, pendingPos, pendingEnd, pendingHasTrailingNewLine);
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
            yield return NewCommentRange(pendingKind, pendingPos, pendingEnd, pendingHasTrailingNewLine);
        }
    }

    // scanner.go — isShebangTrivia / scanShebangTrivia (Ts.Scanner.Scanner hosts them
    // as `internal`; the shebang can only appear at position 0 and runs to the line end).
    private static bool IsShebangTrivia(string text, int pos)
    {
        return pos == 0 && text.StartsWith("#!", StringComparison.Ordinal);
    }

    private static int ScanShebangTrivia(string text, int pos)
    {
        pos += 2;
        while (pos < text.Length && !global::Ts.StringUtil.StringUtil.IsLineBreak(text[pos]))
        {
            pos++;
        }
        return pos;
    }

    // Decodes the UTF-16 code point at pos; size is in UTF-16 code units (1 or 2).
    private static (int Ch, int Size) DecodeRune(string text, int pos)
    {
        var c = text[pos];
        if (char.IsHighSurrogate(c) && pos + 1 < text.Length && char.IsLowSurrogate(text[pos + 1]))
        {
            return (char.ConvertToUtf32(c, text[pos + 1]), 2);
        }
        return (c, 1);
    }

    // printer/utilities.go:178 — EscapeString, specialized to its only LS call shape
    // (getLiteralTextFlagsNeverAsciiEscape, non-JSX). The full C# port lives at
    // Ts.Printer.PrinterUtilities.EscapeString but is `internal` to Ts.Compiler.
    // Based heavily on the abstract 'Quote'/'QuoteJSONString' operation from ECMA-262 (24.3.2.2),
    // but augmented for a few select characters (e.g. lineSeparator, paragraphSeparator, nextLine)
    // Note that this doesn't actually wrap the input in double quotes.
    internal static string EscapeString(string s, char quoteChar)
    {
        var b = new StringBuilder(s.Length + 2);
        var pos = 0;
        var i = 0;
        while (i < s.Length)
        {
            var (ch, size) = DecodeRune(s, i);

            // A lone surrogate half is always escaped (a valid pair decodes above 0xFFFF).
            var escape = ch >= 0xD800 && ch <= 0xDFFF;

            if (ch == '\\')
            {
                escape = true;
            }
            else if (ch == '$')
            {
                if (quoteChar == '`' && i + 1 < s.Length && s[i + 1] == '{')
                {
                    escape = true;
                }
            }
            else if (ch == quoteChar || ch == 0x2028 || ch == 0x2029 || ch == 0x0085 || ch == '\r')
            {
                escape = true;
            }
            else if (ch == '\n')
            {
                if (quoteChar != '`')
                {
                    // Template strings preserve simple LF newlines, still encode CRLF (or CR).
                    escape = true;
                }
            }
            else if (ch <= 0x1F)
            {
                escape = true;
            }

            if (escape)
            {
                if (pos < i)
                {
                    // Write string up to this point
                    b.Append(s, pos, i - pos);
                }

                if (ch == '\r' && quoteChar == '`' && i + 1 < s.Length && s[i + 1] == '\n')
                {
                    // Template strings preserve simple LF newlines, but still must escape CRLF. Left alone, the
                    // above cases for `\r` and `\n` would inadvertently escape CRLF as two independent characters.
                    size++;
                    b.Append("\\r\\n");
                }
                else if (ch > 0xFFFF)
                {
                    // encode as surrogate pair
                    ch -= 0x10000;
                    EncodeUtf16EscapeSequence(b, ((ch >> 10) & 0x3FF) + 0xD800);
                    EncodeUtf16EscapeSequence(b, (ch & 0x3FF) + 0xDC00);
                }
                else if (ch >= 0xD800 && ch <= 0xDFFF)
                {
                    EncodeUtf16EscapeSequence(b, ch);
                }
                else if (ch == 0)
                {
                    if (i + 1 < s.Length && global::Ts.StringUtil.StringUtil.IsDigit(s[i + 1]))
                    {
                        // If the null character is followed by digits, print as a hex escape to prevent the result from
                        // parsing as an octal (which is forbidden in strict mode)
                        b.Append("\\x00");
                    }
                    else
                    {
                        // Otherwise, keep printing a literal \0 for the null character
                        b.Append("\\0");
                    }
                }
                else if (EscapedChar(ch) is { } match)
                {
                    b.Append(match);
                }
                else
                {
                    EncodeUtf16EscapeSequence(b, ch);
                }
                pos = i + size;
            }

            i += size;
        }

        if (pos < i)
        {
            b.Append(s, pos, s.Length - pos);
        }
        return b.ToString();
    }

    // printer/utilities.go:40 — escapedCharsMap.
    private static string? EscapedChar(int ch)
    {
        return ch switch
        {
            '\t' => "\\t",
            '\v' => "\\v",
            '\f' => "\\f",
            '\b' => "\\b",
            '\r' => "\\r",
            '\n' => "\\n",
            '\\' => "\\\\",
            '"' => "\\\"",
            '\'' => "\\'",
            '`' => "\\`",
            '$' => "\\$", // when quoteChar == '`'
            0x2028 => "\\u2028", // lineSeparator
            0x2029 => "\\u2029", // paragraphSeparator
            0x0085 => "\\u0085", // nextLine
            _ => null,
        };
    }

    // printer/utilities.go:64 — encodeUtf16EscapeSequence.
    private static void EncodeUtf16EscapeSequence(StringBuilder b, int charCode)
    {
        b.Append("\\u");
        b.AppendFormat(CultureInfo.InvariantCulture, "{0:X4}", charCode);
    }
}
