// PORT-NOTE: Selected self-contained helpers from internal/printer/utilities.go, hosted here for the printer
// emit-body wave (utilities.go is now fully real across the PrinterUtilities*.cs partials).
// These are the literal-text / string-escaping / line-position helpers that the common emit paths require to
// produce real output. They are faithful 1:1 ports. Relocate to Utilities.cs when that wave lands.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using StringUtil = global::Ts.StringUtil.StringUtil;

public enum QuoteChar
{
    SingleQuote = '\'',
    DoubleQuote = '"',
    Backtick = '`',
}

public static partial class PrinterUtilities
{
    // Go: jsxEscapedCharsMap (utilities.go:36) — map-iter: N/A (lookup only)
    private static readonly Dictionary<int, string> JsxEscapedCharsMap = new()
    {
        ['"'] = "&quot;",
        ['\''] = "&apos;",
    };

    // Go: escapedCharsMap (utilities.go:41) — map-iter: N/A (lookup only)
    private static readonly Dictionary<int, string> EscapedCharsMap = new()
    {
        ['\t'] = "\\t",
        ['\v'] = "\\v",
        ['\f'] = "\\f",
        ['\b'] = "\\b",
        ['\r'] = "\\r",
        ['\n'] = "\\n",
        ['\\'] = "\\\\",
        ['"'] = "\\\"",
        ['\''] = "\\'",
        ['`'] = "\\`",
        ['$'] = "\\$", // when quoteChar == '`'
        ['\u2028'] = "\\u2028", // lineSeparator
        ['\u2029'] = "\\u2029", // paragraphSeparator
        ['\u0085'] = "\\u0085", // nextLine
    };

    private static void EncodeJsxCharacterEntity(StringBuilder b, int charCode)
    {
        string hexCharCode = ((uint)charCode).ToString("X", CultureInfo.InvariantCulture);
        b.Append("&#x");
        b.Append(hexCharCode);
        b.Append(';');
    }

    private static void EncodeUtf16EscapeSequence(StringBuilder b, int charCode)
    {
        string hexCharCode = ((uint)charCode).ToString("X", CultureInfo.InvariantCulture);
        b.Append("\\u");
        for (int i = hexCharCode.Length; i < 4; i++)
        {
            b.Append('0');
        }
        b.Append(hexCharCode);
    }

    // Based heavily on the abstract 'Quote'/'QuoteJSONString' operation from ECMA-262 (24.3.2.2),
    // but augmented for a few select characters (e.g. lineSeparator, paragraphSeparator, nextLine)
    // Note that this doesn't actually wrap the input in double quotes.
    private static void EscapeStringWorker(string s, QuoteChar quoteChar, GetLiteralTextFlags flags, StringBuilder b)
    {
        int pos = 0;
        int i = 0;
        while (i < s.Length)
        {
            var (ch, size) = StringUtil.DecodeJSStringRune(s, i);

            bool escape = false;
            if (ch >= 0xD800 && ch <= 0xDFFF)
            {
                escape = true;
            }
            else if (ch == 0xFFFD && size == 1)
            {
                // A stray unit that is not valid UTF-16. Escape it as the Unicode replacement character so the
                // output is always well-formed rather than containing raw invalid units.
                escape = true;
            }

            // This consists of the first 19 unprintable ASCII characters, canonical escapes, lineSeparator,
            // paragraphSeparator, and nextLine.
            switch (ch)
            {
                case '\\':
                    if ((flags & GetLiteralTextFlags.JsxAttributeEscape) == 0)
                    {
                        escape = true;
                    }
                    break;
                case '$':
                    if (quoteChar == QuoteChar.Backtick && i + 1 < s.Length && s[i + 1] == '{')
                    {
                        escape = true;
                    }
                    break;
                case '\u2028':
                case '\u2029':
                case '\u0085':
                case '\r':
                    escape = true;
                    break;
                case '\n':
                    if (quoteChar != QuoteChar.Backtick)
                    {
                        // Template strings preserve simple LF newlines, still encode CRLF (or CR).
                        escape = true;
                    }
                    break;
                default:
                    if (ch == (int)quoteChar)
                    {
                        escape = true;
                    }
                    else if (ch <= '\u001f' || ((flags & GetLiteralTextFlags.NeverAsciiEscape) == 0 && ch > '\u007f'))
                    {
                        escape = true;
                    }
                    break;
            }

            if (escape)
            {
                if (pos < i)
                {
                    // Write string up to this point
                    b.Append(s, pos, i - pos);
                }

                if ((flags & GetLiteralTextFlags.JsxAttributeEscape) != 0)
                {
                    if (ch == 0)
                    {
                        b.Append("&#0;");
                    }
                    else if (JsxEscapedCharsMap.TryGetValue(ch, out var match))
                    {
                        b.Append(match);
                    }
                    else
                    {
                        EncodeJsxCharacterEntity(b, ch);
                    }
                }
                else
                {
                    if (ch == '\r' && quoteChar == QuoteChar.Backtick && i + 1 < s.Length && s[i + 1] == '\n')
                    {
                        // Template strings preserve simple LF newlines, but still must escape CRLF.
                        size++;
                        b.Append("\\r\\n");
                    }
                    else if (ch > 0xffff)
                    {
                        // encode as surrogate pair
                        ch -= 0x10000;
                        EncodeUtf16EscapeSequence(b, ((ch & 0b11111111110000000000) >> 10) + 0xD800);
                        EncodeUtf16EscapeSequence(b, (ch & 0b00000000001111111111) + 0xDC00);
                    }
                    else if (ch >= 0xD800 && ch <= 0xDFFF)
                    {
                        EncodeUtf16EscapeSequence(b, ch);
                    }
                    else if (ch == 0)
                    {
                        if (i + 1 < s.Length && StringUtil.IsDigit(s[i + 1]))
                        {
                            // If the null character is followed by digits, print as a hex escape to prevent the result
                            // from parsing as an octal (which is forbidden in strict mode)
                            b.Append("\\x00");
                        }
                        else
                        {
                            // Otherwise, keep printing a literal \0 for the null character
                            b.Append("\\0");
                        }
                    }
                    else
                    {
                        if (EscapedCharsMap.TryGetValue(ch, out var match))
                        {
                            b.Append(match);
                        }
                        else
                        {
                            EncodeUtf16EscapeSequence(b, ch);
                        }
                    }
                }
                pos = i + size;
            }

            i += size;
        }

        if (pos < i)
        {
            b.Append(s, pos, s.Length - pos);
        }
    }

    // Go: EscapeString (utilities.go:178)
    public static string EscapeString(string s, QuoteChar quoteChar)
    {
        var b = new StringBuilder(s.Length + 2);
        EscapeStringWorker(s, quoteChar, GetLiteralTextFlags.NeverAsciiEscape, b);
        return b.ToString();
    }

    // Go: escapeNonAsciiString (utilities.go:185)
    internal static string EscapeNonAsciiString(string s, QuoteChar quoteChar)
    {
        var b = new StringBuilder(s.Length + 2);
        EscapeStringWorker(s, quoteChar, GetLiteralTextFlags.None, b);
        return b.ToString();
    }

    // Go: escapeJsxAttributeString (utilities.go:192)
    internal static string EscapeJsxAttributeString(string s, QuoteChar quoteChar)
    {
        var b = new StringBuilder(s.Length + 2);
        EscapeStringWorker(s, quoteChar, GetLiteralTextFlags.JsxAttributeEscape | GetLiteralTextFlags.NeverAsciiEscape, b);
        return b.ToString();
    }

    // Go: ast.IsTemplateLiteralKind (internal/ast/utilities.go:3218) — hosted here (unported in ast package).
    internal static bool IsTemplateLiteralKind(SyntaxKind kind)
    {
        return SyntaxKind.FirstTemplateToken <= kind && kind <= SyntaxKind.LastTemplateToken;
    }

    // Go: ast.IsUnterminatedLiteral (internal/ast/utilities.go:3734) — hosted here (unported in ast package).
    internal static bool IsUnterminatedLiteral(Node node)
    {
        return NodeTests.IsLiteralKind(node.Kind) && (node.LiteralLikeData()!.TokenFlags & TokenFlags.Unterminated) != 0 ||
            IsTemplateLiteralKind(node.Kind) && (node.TemplateLiteralLikeData()!.TemplateFlags & TokenFlags.Unterminated) != 0;
    }

    private static bool CanUseOriginalText(Node node, GetLiteralTextFlags flags)
    {
        // A synthetic node has no original text, nor does a node without a parent as we would be unable to find the
        // containing SourceFile. We also cannot use the original text if the literal was unterminated and the caller has
        // requested proper termination of unterminated literals
        if (global::Ts.Ast.AstUtilities.NodeIsSynthesized(node) || node.Parent is null || ((flags & GetLiteralTextFlags.TerminateUnterminatedLiterals) != 0 && IsUnterminatedLiteral(node)))
        {
            return false;
        }

        if (node.Kind == SyntaxKind.NumericLiteral)
        {
            var tokenFlags = node.AsNumericLiteral().TokenFlags;
            // For a numeric literal, we cannot use the original text if the original text was an invalid literal
            if ((tokenFlags & TokenFlags.IsInvalid) != 0)
            {
                return false;
            }
            // We also cannot use the original text if the literal contains numeric separators, but numeric separators
            // are not permitted
            if ((tokenFlags & TokenFlags.ContainsSeparator) != 0)
            {
                return (flags & GetLiteralTextFlags.AllowNumericSeparator) != 0;
            }
        }

        // Finally, we do not use the original text of a BigInt literal
        return node.Kind != SyntaxKind.BigIntLiteral;
    }

    // Go: getLiteralText (utilities.go:227)
    internal static string GetLiteralText(Node node, SourceFile? sourceFile, GetLiteralTextFlags flags)
    {
        // If we don't need to downlevel and we can reach the original source text using
        // the node's parent reference, then simply get the text as it was originally written.
        if (sourceFile is not null && CanUseOriginalText(node, flags))
        {
            return global::Ts.Scanner.Scanner.GetSourceTextOfNodeFromSourceFile(sourceFile, node, false /*includeTrivia*/);
        }

        // If we can't reach the original source text, use the canonical form if it's a number,
        // or a (possibly escaped) quoted form of the original text if it's string-like.
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
            {
                var b = new StringBuilder();
                QuoteChar quoteChar;
                if ((node.AsStringLiteral().TokenFlags & TokenFlags.SingleQuote) != 0)
                {
                    quoteChar = QuoteChar.SingleQuote;
                }
                else
                {
                    quoteChar = QuoteChar.DoubleQuote;
                }

                var text = node.Text();

                // Write leading quote character
                b.EnsureCapacity(text.Length + 2);
                b.Append((char)quoteChar);

                // Write text
                EscapeStringWorker(text, quoteChar, flags, b);

                // Write trailing quote character
                b.Append((char)quoteChar);
                return b.ToString();
            }

            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
            case SyntaxKind.TemplateMiddle:
            case SyntaxKind.TemplateTail:
            {
                // If a NoSubstitutionTemplateLiteral appears to have a substitution in it, the original text
                // had to include a backslash: `not \${a} substitution`.
                var b = new StringBuilder();
                var text = node.Text();
                var rawText = node.TemplateLiteralLikeData()!.RawText;
                bool raw = rawText.Length > 0 || text.Length == 0;

                int textLen = raw ? rawText.Length : text.Length;

                // Write leading quote character
                switch (node.Kind)
                {
                    case SyntaxKind.NoSubstitutionTemplateLiteral:
                        b.EnsureCapacity(2 + textLen);
                        b.Append('`');
                        break;
                    case SyntaxKind.TemplateHead:
                        b.EnsureCapacity(3 + textLen);
                        b.Append('`');
                        break;
                    case SyntaxKind.TemplateMiddle:
                        b.EnsureCapacity(3 + textLen);
                        b.Append('}');
                        break;
                    case SyntaxKind.TemplateTail:
                        b.EnsureCapacity(2 + textLen);
                        b.Append('}');
                        break;
                }

                // Write text
                if (rawText.Length > 0 || text.Length == 0)
                {
                    // If rawText is set, it is expected to be valid.
                    b.Append(rawText);
                }
                else
                {
                    EscapeStringWorker(text, QuoteChar.Backtick, flags, b);
                }

                // Write trailing quote character
                switch (node.Kind)
                {
                    case SyntaxKind.NoSubstitutionTemplateLiteral:
                        b.Append('`');
                        break;
                    case SyntaxKind.TemplateHead:
                        b.Append("${");
                        break;
                    case SyntaxKind.TemplateMiddle:
                        b.Append("${");
                        break;
                    case SyntaxKind.TemplateTail:
                        b.Append('`');
                        break;
                }
                return b.ToString();
            }

            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
                return node.Text();

            case SyntaxKind.RegularExpressionLiteral:
                if ((flags & GetLiteralTextFlags.TerminateUnterminatedLiterals) != 0 && IsUnterminatedLiteral(node))
                {
                    var b = new StringBuilder();
                    var text = node.Text();
                    if (text.Length > 0 && text[text.Length - 1] == '\\')
                    {
                        b.EnsureCapacity(2 + text.Length);
                        b.Append(text);
                        b.Append(" /");
                    }
                    else
                    {
                        b.EnsureCapacity(1 + text.Length);
                        b.Append(text);
                        b.Append("/");
                    }
                    return b.ToString();
                }
                return node.Text();

            default:
                throw new InvalidOperationException("Unsupported LiteralLikeNode");
        }
    }

    //
    // Line-position helpers (utilities.go:345-420)
    //

    // Go: RangeIsOnSingleLine (utilities.go:345)
    internal static bool RangeIsOnSingleLine(TextRange r, SourceFile sourceFile)
    {
        return RangeStartIsOnSameLineAsRangeEnd(r, r, sourceFile);
    }

    // Go: RangeStartPositionsAreOnSameLine (utilities.go:349)
    public static bool RangeStartPositionsAreOnSameLine(TextRange range1, TextRange range2, SourceFile sourceFile)
    {
        return PositionsAreOnSameLine(
            GetStartPositionOfRange(range1, sourceFile, false /*includeComments*/),
            GetStartPositionOfRange(range2, sourceFile, false /*includeComments*/),
            sourceFile);
    }

    // Go: rangeEndPositionsAreOnSameLine (utilities.go:357)
    internal static bool RangeEndPositionsAreOnSameLine(TextRange range1, TextRange range2, SourceFile sourceFile)
    {
        return PositionsAreOnSameLine(range1.End, range2.End, sourceFile);
    }

    // Go: rangeStartIsOnSameLineAsRangeEnd (utilities.go:361)
    private static bool RangeStartIsOnSameLineAsRangeEnd(TextRange range1, TextRange range2, SourceFile sourceFile)
    {
        return PositionsAreOnSameLine(GetStartPositionOfRange(range1, sourceFile, false /*includeComments*/), range2.End, sourceFile);
    }

    // Go: rangeEndIsOnSameLineAsRangeStart (utilities.go:365)
    internal static bool RangeEndIsOnSameLineAsRangeStart(TextRange range1, TextRange range2, SourceFile sourceFile)
    {
        return PositionsAreOnSameLine(range1.End, GetStartPositionOfRange(range2, sourceFile, false /*includeComments*/), sourceFile);
    }

    // Go: getStartPositionOfRange (utilities.go:369)
    private static int GetStartPositionOfRange(TextRange r, SourceFile sourceFile, bool includeComments)
    {
        if (global::Ts.Ast.AstUtilities.PositionIsSynthesized(r.Pos))
        {
            return -1;
        }
        return global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), r.Pos, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = includeComments });
    }

    // Go: PositionsAreOnSameLine (utilities.go:376)
    internal static bool PositionsAreOnSameLine(int pos1, int pos2, SourceFile sourceFile)
    {
        return GetLinesBetweenPositions(sourceFile, pos1, pos2) == 0;
    }

    // Go: GetLinesBetweenPositions (utilities.go:380)
    public static int GetLinesBetweenPositions(SourceFile sourceFile, int pos1, int pos2)
    {
        if (pos1 == pos2)
        {
            return 0;
        }
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile);
        int lower = pos1 < pos2 ? pos1 : pos2;
        bool isNegative = lower == pos2;
        int upper = isNegative ? pos1 : pos2;
        int lowerLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, lower);
        int upperLine = lowerLine + global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts[lowerLine..], upper);
        if (isNegative)
        {
            return lowerLine - upperLine;
        }
        else
        {
            return upperLine - lowerLine;
        }
    }

    // Go: getLinesBetweenRangeEndAndRangeStart (utilities.go:397)
    internal static int GetLinesBetweenRangeEndAndRangeStart(TextRange range1, TextRange range2, SourceFile sourceFile, bool includeSecondRangeComments)
    {
        int range2Start = GetStartPositionOfRange(range2, sourceFile, includeSecondRangeComments);
        return GetLinesBetweenPositions(sourceFile, range1.End, range2Start);
    }

    // Go: getLinesBetweenPositionAndPrecedingNonWhitespaceCharacter (utilities.go:402)
    internal static int GetLinesBetweenPositionAndPrecedingNonWhitespaceCharacter(int pos, int stopPos, SourceFile sourceFile, bool includeComments)
    {
        int startPos = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), pos, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = includeComments });
        int prevPos = GetPreviousNonWhitespacePosition(startPos, stopPos, sourceFile);
        return GetLinesBetweenPositions(sourceFile, prevPos >= 0 ? prevPos : stopPos, startPos);
    }

    // Go: getLinesBetweenPositionAndNextNonWhitespaceCharacter (utilities.go:408)
    internal static int GetLinesBetweenPositionAndNextNonWhitespaceCharacter(int pos, int stopPos, SourceFile sourceFile, bool includeComments)
    {
        int nextPos = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), pos, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = includeComments });
        return GetLinesBetweenPositions(sourceFile, pos, stopPos < nextPos ? stopPos : nextPos);
    }

    // Go: getPreviousNonWhitespacePosition (utilities.go:413)
    private static int GetPreviousNonWhitespacePosition(int pos, int stopPos, SourceFile sourceFile)
    {
        var text = sourceFile.Text();
        for (; pos >= stopPos; pos--)
        {
            if (!StringUtil.IsWhiteSpaceLike(text[pos]))
            {
                return pos;
            }
        }
        return -1;
    }

    //
    // Small structural helpers (utilities.go:605-649)
    //

    // Go: greatestEnd (utilities.go:605). Heterogeneous variadic of `interface{ End() int }`.
    // In C# the possible dynamic types are Node, NodeList (incl. ModifierList and the list aliases), or TextRange.
    internal static int GreatestEnd(int end, params object?[] nodes)
    {
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            if (TryGetEnd(nodes[i], out int nodeEnd) && end < nodeEnd)
            {
                end = nodeEnd;
            }
        }
        return end;
    }

    // Go: tryGetEnd (utilities.go:578)
    private static bool TryGetEnd(object? node, out int end)
    {
        switch (node)
        {
            case Node n:
                end = n.End;
                return true;
            case NodeList list:
                end = list.End;
                return true;
            case TextRange tr:
                end = tr.End;
                return true;
            case null:
                end = 0;
                return false;
            default:
                throw new InvalidOperationException($"unhandled type: {node.GetType().Name}");
        }
    }

    // Go: skipSynthesizedParentheses (utilities.go:615)
    internal static Node SkipSynthesizedParentheses(Node node)
    {
        while (node.Kind == SyntaxKind.ParenthesizedExpression && global::Ts.Ast.AstUtilities.NodeIsSynthesized(node))
        {
            node = node.Expression()!;
        }
        return node;
    }

    // Go: isNewExpressionWithoutArguments (utilities.go:622)
    internal static bool IsNewExpressionWithoutArguments(Node node)
    {
        return node.Kind == SyntaxKind.NewExpression && node.ArgumentList() is null;
    }

    // Go: isBinaryOperation (utilities.go:626)
    internal static bool IsBinaryOperation(Node node, SyntaxKind token)
    {
        node = global::Ts.Ast.AstUtilities.SkipPartiallyEmittedExpressions(node);
        return node.Kind == SyntaxKind.BinaryExpression &&
            node.AsBinaryExpression().OperatorToken!.Kind == token;
    }

    // Go: mixingBinaryOperatorsRequiresParentheses (utilities.go:632)
    internal static bool MixingBinaryOperatorsRequiresParentheses(SyntaxKind a, SyntaxKind b)
    {
        if (a == SyntaxKind.QuestionQuestionToken)
        {
            return b == SyntaxKind.AmpersandAmpersandToken || b == SyntaxKind.BarBarToken;
        }
        if (b == SyntaxKind.QuestionQuestionToken)
        {
            return a == SyntaxKind.AmpersandAmpersandToken || a == SyntaxKind.BarBarToken;
        }
        return false;
    }

    // Go: isImmediatelyInvokedFunctionExpressionOrArrowFunction (utilities.go:642)
    internal static bool IsImmediatelyInvokedFunctionExpressionOrArrowFunction(Node node)
    {
        node = global::Ts.Ast.AstUtilities.SkipPartiallyEmittedExpressions(node);
        if (!NodeTests.IsCallExpression(node))
        {
            return false;
        }
        node = global::Ts.Ast.AstUtilities.SkipPartiallyEmittedExpressions(node.Expression()!);
        return NodeTests.IsFunctionExpression(node) || NodeTests.IsArrowFunction(node);
    }
}
