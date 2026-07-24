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

// lsutil/userpreferences.go:226 — QuotePreference. The Go type is a string enum whose
// "" (unknown) and "auto" arms both resolve through GetQuotePreference; only the two
// resolved arms survive resolution.
internal enum QuotePreference
{
    Double,
    Single,
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

    // ls/utilities.go:27-46 — IsInString.
    internal static bool IsInString(global::Ts.Ast.SourceFile sourceFile, int position, global::Ts.Ast.Node? previousToken)
    {
        if (previousToken != null && global::Ts.Ast.AstUtilities.IsStringTextContainingNode(previousToken))
        {
            var start = global::Ts.AstNav.Tokens.GetStartOfNode(previousToken, sourceFile, false /*includeJSDoc*/);
            var end = previousToken.End;

            // To be "in" one of these literals, the position has to be:
            //   1. entirely within the token text.
            //   2. at the end position of an unterminated token.
            //   3. at the end of a regular expression (due to trailing flags like '/foo/g').
            if (start < position && position < end)
            {
                return true;
            }

            if (position == end)
            {
                return global::Ts.Ast.AstUtilities.IsUnterminatedLiteral(previousToken);
            }
        }
        return false;
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

    // lsconv/converters.go:110 — FileNameToDocumentURI (plain-path subset: bundled://
    // and dynamic `^/…` file names belong to the deferred project layer, and Windows
    // volume splitting to the deferred tspath.SplitVolumePath port). Go escapes each
    // path segment with url.PathEscape plus an extra sub-delims replacer; C#'s
    // Uri.EscapeDataString escapes that same combined set.
    internal static global::Ts.LsProto.DocumentUri FileNameToDocumentURI(string fileName)
    {
        if (fileName.StartsWith("//", StringComparison.Ordinal))
        {
            fileName = fileName["//".Length..];
        }
        var parts = fileName.Split('/');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Uri.EscapeDataString(parts[i]);
        }
        return new global::Ts.LsProto.DocumentUri("file://" + string.Join("/", parts));
    }

    // lsconv/converters.go:161 — Converters.ToLSPRange (UTF-16 direct).
    internal static global::Ts.LsProto.Range ToLspRange(global::Ts.Ast.SourceFile file, global::Ts.Core.TextRange range)
    {
        return new global::Ts.LsProto.Range
        {
            Start = PositionToLineAndCharacter(file, range.Pos),
            End = PositionToLineAndCharacter(file, range.End),
        };
    }

    // lsconv/converters.go:262 — styleCheckDiagnostics.
    // https://github.com/microsoft/vscode/blob/93e08afe0469712706ca4e268f778cfadf1a43ef/extensions/typescript-language-features/src/typeScriptServiceClientHost.ts#L40C7-L40C29
    private static readonly HashSet<int> StyleCheckDiagnostics =
    [
        global::Ts.Diagnostics.Diagnostics.X_0_is_declared_but_never_used.Code,
        global::Ts.Diagnostics.Diagnostics.X_0_is_declared_but_its_value_is_never_read.Code,
        global::Ts.Diagnostics.Diagnostics.Property_0_is_declared_but_its_value_is_never_read.Code,
        global::Ts.Diagnostics.Diagnostics.All_imports_in_import_declaration_are_unused.Code,
        global::Ts.Diagnostics.Diagnostics.Unreachable_code_detected.Code,
        global::Ts.Diagnostics.Diagnostics.Unused_label.Code,
        global::Ts.Diagnostics.Diagnostics.Fallthrough_case_in_switch.Code,
        global::Ts.Diagnostics.Diagnostics.Not_all_code_paths_return_a_value.Code,
    ];

    // lsconv/converters.go:234 — DiagnosticToLSPPull converts a diagnostic for pull
    // diagnostics (textDocument/diagnostic). PORT-NOTE: the Go options come from the
    // request-context client capabilities; without a context carrier the pull options
    // collapse to "related information and both tags supported, not Visual Studio".
    internal static global::Ts.LsProto.Diagnostic DiagnosticToLSPPull(
        global::Ts.Ast.Diagnostic diagnostic,
        bool reportStyleChecksAsWarnings)
    {
        return DiagnosticToLSP(
            diagnostic,
            reportStyleChecksAsWarnings,
            relatedInformation: true,
            supportsUnnecessaryTag: true,
            supportsDeprecatedTag: true,
            visualStudio: false);
    }

    // lsconv/converters.go:273 — diagnosticToLSP. The Go diagnosticOptions struct is
    // threaded as parameters (tagValueSet dissolves into the two supports* flags), and
    // locale.FromContext(ctx) collapses to Locale.Default until the locale carrier lands.
    internal static global::Ts.LsProto.Diagnostic DiagnosticToLSP(
        global::Ts.Ast.Diagnostic diagnostic,
        bool reportStyleChecksAsWarnings,
        bool relatedInformation,
        bool supportsUnnecessaryTag,
        bool supportsDeprecatedTag,
        bool visualStudio)
    {
        var locale = global::Ts.Locale.Locale.Default;
        var severity = diagnostic.Category() switch
        {
            global::Ts.Diagnostics.Category.Suggestion => global::Ts.LsProto.DiagnosticSeverity.Hint,
            global::Ts.Diagnostics.Category.Message => global::Ts.LsProto.DiagnosticSeverity.Information,
            global::Ts.Diagnostics.Category.Warning => global::Ts.LsProto.DiagnosticSeverity.Warning,
            _ => global::Ts.LsProto.DiagnosticSeverity.Error,
        };

        if (reportStyleChecksAsWarnings &&
            severity == global::Ts.LsProto.DiagnosticSeverity.Error &&
            StyleCheckDiagnostics.Contains(diagnostic.Code()))
        {
            severity = global::Ts.LsProto.DiagnosticSeverity.Warning;
        }

        List<global::Ts.LsProto.DiagnosticRelatedInformation>? related = null;
        if (relatedInformation)
        {
            var relatedDiagnostics = diagnostic.RelatedInformation() ?? [];
            related = new List<global::Ts.LsProto.DiagnosticRelatedInformation>(relatedDiagnostics.Length);
            foreach (var rel in relatedDiagnostics)
            {
                related.Add(new global::Ts.LsProto.DiagnosticRelatedInformation
                {
                    Location = new global::Ts.LsProto.Location
                    {
                        Uri = FileNameToDocumentURI(rel.File()!.FileName()), // Go dereferences unconditionally.
                        Range = ToLspRange(rel.File()!, rel.Loc()),
                    },
                    Message = rel.Localize(locale),
                });
            }
        }

        List<global::Ts.LsProto.DiagnosticTag>? tags = null;
        if ((supportsUnnecessaryTag || supportsDeprecatedTag) &&
            (diagnostic.ReportsUnnecessary() || diagnostic.ReportsDeprecated()))
        {
            tags = new List<global::Ts.LsProto.DiagnosticTag>(2);
            if (diagnostic.ReportsUnnecessary() && supportsUnnecessaryTag)
            {
                tags.Add(global::Ts.LsProto.DiagnosticTag.Unnecessary);
            }
            if (diagnostic.ReportsDeprecated() && supportsDeprecatedTag)
            {
                tags.Add(global::Ts.LsProto.DiagnosticTag.Deprecated);
            }
        }

        // For diagnostics without a file (e.g., program diagnostics), use a zero range
        var lspRange = diagnostic.File() is { } diagnosticFile
            ? ToLspRange(diagnosticFile, diagnostic.Loc())
            : new global::Ts.LsProto.Range
            {
                Start = new global::Ts.LsProto.Position(),
                End = new global::Ts.LsProto.Position(),
            };

        global::Ts.LsProto.IntegerOrString code;
        string? source = null;
        if (visualStudio)
        {
            code = global::Ts.LsProto.IntegerOrString.FromString(
                "TS" + diagnostic.Code().ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            code = global::Ts.LsProto.IntegerOrString.FromInteger(diagnostic.Code());
            source = "ts";
        }

        return new global::Ts.LsProto.Diagnostic
        {
            Range = lspRange,
            Code = code,
            Severity = severity,
            Message = global::Ts.LsProto.StringOrMarkupContent.FromString(MessageChainToString(diagnostic, locale)),
            Source = source,
            RelatedInformation = related is { Count: > 0 } ? related.ToArray() : null,
            Tags = tags is { Count: > 0 } ? tags.ToArray() : null,
        };
    }

    // lsconv/converters.go:342 — messageChainToString.
    internal static string MessageChainToString(global::Ts.Ast.Diagnostic diagnostic, global::Ts.Locale.Locale locale)
    {
        if ((diagnostic.MessageChain()?.Length ?? 0) == 0)
        {
            return diagnostic.Localize(locale);
        }
        using var b = new System.IO.StringWriter(CultureInfo.InvariantCulture);
        global::Ts.DiagnosticWriter.DiagnosticWriter.WriteFlattenedASTDiagnosticMessage(b, diagnostic, "\n", locale);
        return b.ToString();
    }

    // ls/lsutil/children.go:59-83 — GetLastVisitedChild: the last visited (non-reparsed) child.
    // Go drives a NodeVisitor with a tail-first list scan; a full forward walk recording the last
    // non-reparsed child is equivalent.
    internal static global::Ts.Ast.Node? GetLastVisitedChild(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile sourceFile)
    {
        global::Ts.Ast.Node? lastChild = null;
        node.ForEachChild(n =>
        {
            if (n != null && (n.Flags & global::Ts.Ast.NodeFlags.Reparsed) == 0)
            {
                lastChild = n;
            }
            return false;
        });
        return lastChild;
    }

    // ls/lsutil/children.go:10-33 — GetLastChild: replaces last(node.getChildren(sourceFile)).
    internal static global::Ts.Ast.Node? GetLastChild(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile sourceFile)
    {
        var lastChildNode = GetLastVisitedChild(node, sourceFile);
        if (global::Ts.Ast.AstUtilities.IsJSDocSingleCommentNode(node) && lastChildNode == null)
        {
            return null;
        }
        var tokenStartPos = lastChildNode != null ? lastChildNode.End : node.Pos;
        global::Ts.Ast.Node? lastToken = null;
        var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, tokenStartPos);
        for (var startPos = tokenStartPos; startPos < node.End;)
        {
            var tokenKind = scanner.Token();
            var tokenFullStart = scanner.TokenFullStart();
            var tokenEnd = scanner.TokenEnd();
            lastToken = sourceFile.GetOrCreateToken(tokenKind, tokenFullStart, tokenEnd, node, scanner.TokenFlags());
            startPos = tokenEnd;
            scanner.Scan();
        }
        return lastToken ?? lastChildNode;
    }

    // ls/lsutil/children.go:35-57 — GetLastToken.
    internal static global::Ts.Ast.Node? GetLastToken(global::Ts.Ast.Node? node, global::Ts.Ast.SourceFile sourceFile)
    {
        if (node == null)
        {
            return null;
        }

        if (global::Ts.Ast.NodeTests.IsTokenKind(node.Kind) || global::Ts.Ast.NodeTests.IsIdentifier(node))
        {
            return null;
        }

        AssertHasRealPosition(node);

        var lastChild = GetLastChild(node, sourceFile);
        if (lastChild == null)
        {
            return null;
        }

        if (lastChild.Kind < global::Ts.Ast.SyntaxKind.FirstNode)
        {
            return lastChild;
        }
        return GetLastToken(lastChild, sourceFile);
    }

    // ls/lsutil/utilities.go:155-157 — IsNonContextualKeyword.
    internal static bool IsNonContextualKeyword(global::Ts.Ast.SyntaxKind token)
    {
        return global::Ts.Ast.NodeTests.IsKeywordKind(token) && !global::Ts.Ast.AstUtilities.IsContextualKeyword(token);
    }

    // ls/lsutil/children.go:85 — GetFirstToken.
    internal static global::Ts.Ast.Node? GetFirstToken(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile sourceFile)
    {
        if (global::Ts.Ast.NodeTests.IsIdentifier(node) || global::Ts.Ast.NodeTests.IsTokenKind(node.Kind))
        {
            return null;
        }
        AssertHasRealPosition(node);
        global::Ts.Ast.Node? firstChild = null;
        node.ForEachChild(n =>
        {
            // Go checks the *containing* node's Reparsed flag here (children.go:92); kept faithfully.
            if (n == null || (node.Flags & global::Ts.Ast.NodeFlags.Reparsed) != 0)
            {
                return false;
            }
            firstChild = n;
            return true;
        });

        var tokenEndPosition = firstChild != null ? firstChild.Pos : node.End;
        var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, node.Pos);
        global::Ts.Ast.Node? firstToken = null;
        if (node.Pos < tokenEndPosition)
        {
            var tokenKind = scanner.Token();
            var tokenFullStart = scanner.TokenFullStart();
            var tokenEnd = scanner.TokenEnd();
            firstToken = sourceFile.GetOrCreateToken(tokenKind, tokenFullStart, tokenEnd, node, scanner.TokenFlags());
        }

        if (firstToken != null)
        {
            return firstToken;
        }
        if (firstChild == null)
        {
            return null;
        }
        if (firstChild.Kind < global::Ts.Ast.SyntaxKind.FirstNode)
        {
            return firstChild;
        }
        return GetFirstToken(firstChild, sourceFile);
    }

    // ls/lsutil/children.go:132 — AssertHasRealPosition.
    private static void AssertHasRealPosition(global::Ts.Ast.Node node)
    {
        if (global::Ts.Ast.AstUtilities.PositionIsSynthesized(node.Pos) || global::Ts.Ast.AstUtilities.PositionIsSynthesized(node.End))
        {
            // Go panic; InvalidOperationException is the repository's panic mapping.
            throw new InvalidOperationException("Node must have a real position for this operation.");
        }
    }

    // ls/utilities.go:1213 — getChildrenFromNonJSDocNode.
    // Equivalent to Strada's `node.getChildren()` for non-JSDoc nodes.
    internal static List<global::Ts.Ast.Node> GetChildrenFromNonJSDocNode(global::Ts.Ast.Node node, global::Ts.Ast.SourceFile sourceFile)
    {
        var childNodes = new List<global::Ts.Ast.Node>();
        node.ForEachChild(child =>
        {
            childNodes.Add(child);
            return false;
        });

        // If the node has no children, don't scan for tokens.
        // This prevents creating tokens for leaf nodes' own text.
        var children = new List<global::Ts.Ast.Node>();
        if (childNodes.Count == 0)
        {
            return children;
        }

        var pos = node.Pos;
        foreach (var child in childNodes)
        {
            var scanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, pos);
            while (pos < child.Pos)
            {
                var token = scanner.Token();
                var tokenFullStart = scanner.TokenFullStart();
                var tokenEnd = scanner.TokenEnd();
                children.Add(sourceFile.GetOrCreateToken(token, tokenFullStart, tokenEnd, node, scanner.TokenFlags()));
                pos = tokenEnd;
                scanner.Scan();
            }
            children.Add(child);
            pos = child.End;
        }
        var trailingScanner = global::Ts.Scanner.Scanner.GetScannerForSourceFile(sourceFile, pos);
        while (pos < node.End)
        {
            var token = trailingScanner.Token();
            var tokenFullStart = trailingScanner.TokenFullStart();
            var tokenEnd = trailingScanner.TokenEnd();
            children.Add(sourceFile.GetOrCreateToken(token, tokenFullStart, tokenEnd, node, trailingScanner.TokenFlags()));
            pos = tokenEnd;
            trailingScanner.Scan();
        }
        return children;
    }

    // ast/utilities.go:2268 — IsBreakOrContinueStatement.
    internal static bool IsBreakOrContinueStatement(global::Ts.Ast.Node node)
    {
        return node.Kind is global::Ts.Ast.SyntaxKind.BreakStatement or global::Ts.Ast.SyntaxKind.ContinueStatement;
    }

    // ast/utilities.go — GetContainingFunction. The C# port exists at
    // Ts.Checker (Checker.TypeNodes.Support.cs) but is `internal` to Ts.Compiler.
    internal static global::Ts.Ast.Node? GetContainingFunction(global::Ts.Ast.Node node)
    {
        return global::Ts.Ast.AstUtilities.FindAncestor(node.Parent, global::Ts.Ast.AstUtilities.IsFunctionLike);
    }

    // ast/ast.go:1273 — IsWriteAccessForReference.
    internal static bool IsWriteAccessForReference(global::Ts.Ast.Node node)
    {
        var decl = GetDeclarationFromName(node);
        return (decl != null && DeclarationIsWriteAccess(decl)) ||
            node.Kind == global::Ts.Ast.SyntaxKind.DefaultKeyword ||
            global::Ts.Ast.AstUtilities.IsWriteAccess(node);
    }

    // ast/ast.go:1278 — GetDeclarationFromName.
    internal static global::Ts.Ast.Node? GetDeclarationFromName(global::Ts.Ast.Node? name)
    {
        if (name == null || name.Parent == null)
        {
            return null;
        }
        var parent = name.Parent;
        switch (name.Kind)
        {
            case global::Ts.Ast.SyntaxKind.StringLiteral:
            case global::Ts.Ast.SyntaxKind.NoSubstitutionTemplateLiteral:
            case global::Ts.Ast.SyntaxKind.NumericLiteral:
                if (global::Ts.Ast.NodeTests.IsComputedPropertyName(parent))
                {
                    return parent.Parent;
                }
                goto case global::Ts.Ast.SyntaxKind.Identifier;
            case global::Ts.Ast.SyntaxKind.Identifier:
                if (global::Ts.Ast.AstUtilities.IsDeclaration(parent))
                {
                    if (parent.Name() == name)
                    {
                        return parent;
                    }
                    return null;
                }
                if (global::Ts.Ast.NodeTests.IsQualifiedName(parent))
                {
                    var tag = parent.Parent;
                    if (tag != null && global::Ts.Ast.NodeTests.IsJSDocParameterTag(tag) && tag.Name() == parent)
                    {
                        return tag;
                    }
                    return null;
                }
                var binExp = parent.Parent;
                if (binExp != null &&
                    global::Ts.Ast.NodeTests.IsBinaryExpression(binExp) &&
                    global::Ts.Ast.AstUtilities.GetAssignmentDeclarationKind(binExp) != global::Ts.Ast.JSDeclarationKind.None)
                {
                    // (binExp.left as BindableStaticNameExpression).symbol || binExp.symbol
                    var leftHasSymbol = binExp.AsBinaryExpression().Left != null && binExp.AsBinaryExpression().Left!.Symbol() != null;
                    if (leftHasSymbol || binExp.Symbol() != null)
                    {
                        if (global::Ts.Ast.AstUtilities.GetNameOfDeclaration(binExp) == name)
                        {
                            return binExp;
                        }
                    }
                }
                break;
            case global::Ts.Ast.SyntaxKind.PrivateIdentifier:
                if (global::Ts.Ast.AstUtilities.IsDeclaration(parent) && parent.Name() == name)
                {
                    return parent;
                }
                break;
        }
        return null;
    }

    // ast/ast.go:1323 — declarationIsWriteAccess.
    internal static bool DeclarationIsWriteAccess(global::Ts.Ast.Node? decl)
    {
        if (decl == null)
        {
            return false;
        }
        // Consider anything in an ambient declaration to be a write access since it may be coming from JS.
        if ((decl.Flags & global::Ts.Ast.NodeFlags.Ambient) != 0)
        {
            return true;
        }

        switch (decl.Kind)
        {
            case global::Ts.Ast.SyntaxKind.BinaryExpression:
            case global::Ts.Ast.SyntaxKind.BindingElement:
            case global::Ts.Ast.SyntaxKind.ClassDeclaration:
            case global::Ts.Ast.SyntaxKind.ClassExpression:
            case global::Ts.Ast.SyntaxKind.DefaultKeyword:
            case global::Ts.Ast.SyntaxKind.EnumDeclaration:
            case global::Ts.Ast.SyntaxKind.EnumMember:
            case global::Ts.Ast.SyntaxKind.ExportSpecifier:
            case global::Ts.Ast.SyntaxKind.ImportClause: // default import
            case global::Ts.Ast.SyntaxKind.ImportEqualsDeclaration:
            case global::Ts.Ast.SyntaxKind.ImportSpecifier:
            case global::Ts.Ast.SyntaxKind.InterfaceDeclaration:
            case global::Ts.Ast.SyntaxKind.JSDocCallbackTag:
            case global::Ts.Ast.SyntaxKind.JSDocTypedefTag:
            case global::Ts.Ast.SyntaxKind.JsxAttribute:
            case global::Ts.Ast.SyntaxKind.ModuleDeclaration:
            case global::Ts.Ast.SyntaxKind.NamespaceExportDeclaration:
            case global::Ts.Ast.SyntaxKind.NamespaceImport:
            case global::Ts.Ast.SyntaxKind.NamespaceExport:
            case global::Ts.Ast.SyntaxKind.Parameter:
            case global::Ts.Ast.SyntaxKind.ShorthandPropertyAssignment:
            case global::Ts.Ast.SyntaxKind.TypeAliasDeclaration:
            case global::Ts.Ast.SyntaxKind.JSTypeAliasDeclaration:
            case global::Ts.Ast.SyntaxKind.TypeParameter:
                return true;

            case global::Ts.Ast.SyntaxKind.PropertyAssignment:
                // In `({ x: y } = 0);`, `x` is not a write access.
                return !IsArrayLiteralOrObjectLiteralDestructuringPattern(decl.Parent!);

            case global::Ts.Ast.SyntaxKind.FunctionDeclaration:
            case global::Ts.Ast.SyntaxKind.FunctionExpression:
            case global::Ts.Ast.SyntaxKind.Constructor:
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
            case global::Ts.Ast.SyntaxKind.GetAccessor:
            case global::Ts.Ast.SyntaxKind.SetAccessor:
                // functions considered write if they provide a value (have a body)
                return decl.Body() != null;

            case global::Ts.Ast.SyntaxKind.VariableDeclaration:
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
                // variable/property write if initializer present or is in catch clause
                return decl.Initializer() != null || global::Ts.Ast.NodeTests.IsCatchClause(decl.Parent!);

            case global::Ts.Ast.SyntaxKind.MethodSignature:
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.JSDocPropertyTag:
            case global::Ts.Ast.SyntaxKind.JSDocParameterTag:
                return false;

            default:
                // preserve TS behavior: crash on unexpected kinds
                throw new InvalidOperationException("Unhandled case in declarationIsWriteAccess");
        }
    }

    // ls/symbols.go:590 — isInsideNodeModules.
    internal static bool IsInsideNodeModules(string fileName)
    {
        return fileName.Contains("/node_modules/", StringComparison.Ordinal);
    }

    // ast/utilities.go:2244 — IsLabelName.
    internal static bool IsLabelName(global::Ts.Ast.Node node)
    {
        return IsLabelOfLabeledStatement(node) || IsJumpStatementTarget(node);
    }

    // ast/utilities.go:2248 — IsLabelOfLabeledStatement.
    internal static bool IsLabelOfLabeledStatement(global::Ts.Ast.Node node)
    {
        if (!global::Ts.Ast.NodeTests.IsIdentifier(node))
        {
            return false;
        }
        if (!global::Ts.Ast.NodeTests.IsLabeledStatement(node.Parent!)) // Go dereferences Parent unconditionally.
        {
            return false;
        }
        return node == node.Parent!.Label();
    }

    // ast/utilities.go:2258 — IsJumpStatementTarget.
    internal static bool IsJumpStatementTarget(global::Ts.Ast.Node node)
    {
        if (!global::Ts.Ast.NodeTests.IsIdentifier(node))
        {
            return false;
        }
        if (!IsBreakOrContinueStatement(node.Parent!)) // Go dereferences Parent unconditionally.
        {
            return false;
        }
        return node == node.Parent!.Label();
    }

    // ast/utilities.go:4142 — TryGetImportFromModuleSpecifier.
    internal static global::Ts.Ast.Node? TryGetImportFromModuleSpecifier(global::Ts.Ast.Node node)
    {
        switch (node.Parent!.Kind) // Go dereferences Parent unconditionally.
        {
            case global::Ts.Ast.SyntaxKind.ImportDeclaration:
            case global::Ts.Ast.SyntaxKind.JSImportDeclaration:
            case global::Ts.Ast.SyntaxKind.ExportDeclaration:
                return node.Parent;
            case global::Ts.Ast.SyntaxKind.ExternalModuleReference:
                return node.Parent!.Parent;
            case global::Ts.Ast.SyntaxKind.CallExpression:
                if (global::Ts.Ast.AstUtilities.IsImportCall(node.Parent!) ||
                    global::Ts.Ast.AstUtilities.IsRequireCall(node.Parent!, false /*requireStringLiteralLikeArgument*/))
                {
                    return node.Parent;
                }
                return null;
            case global::Ts.Ast.SyntaxKind.LiteralType:
                if (!global::Ts.Ast.NodeTests.IsStringLiteral(node))
                {
                    return null;
                }
                if (node.Parent!.Parent!.Kind == global::Ts.Ast.SyntaxKind.ImportType)
                {
                    return node.Parent!.Parent;
                }
                return null;
        }
        return null;
    }

    // ast/utilities.go:4487 — GetReparsedNodeForNode.
    // If the given node is part of a subtree of JSDoc nodes that have been cloned into a reparsed construct,
    // return the corresponding reparsed clone in the subtree. Otherwise, just return the node.
    internal static global::Ts.Ast.Node GetReparsedNodeForNode(global::Ts.Ast.Node node)
    {
        if (node != null &&
            (node.Flags & global::Ts.Ast.NodeFlags.JSDoc) != 0 &&
            (node.Flags & global::Ts.Ast.NodeFlags.Reparsed) == 0)
        {
            var file = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node);
            if (file != null && (file.ReparsedClones?.Length ?? 0) != 0)
            {
                var clones = file.ReparsedClones!;
                // slices.BinarySearchFunc(clones, node, CompareNodePositions): lower-bound
                // search over Loc (Pos, then End).
                var pos = 0;
                var hi = clones.Length;
                while (pos < hi)
                {
                    var mid = (pos + hi) / 2;
                    if (global::Ts.Core.TextRange.CompareTextRanges(clones[mid].Loc, node.Loc) < 0)
                    {
                        pos = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                var found = pos < clones.Length &&
                    global::Ts.Core.TextRange.CompareTextRanges(clones[pos].Loc, node.Loc) == 0;
                if (!found && pos > 0)
                {
                    pos--;
                }
                if (pos < clones.Length)
                {
                    var candidate = clones[pos];
                    if (node.Loc.ContainedBy(candidate.Loc))
                    {
                        var reparsed = FindCloneInNode(candidate, node);
                        if (reparsed != null)
                        {
                            return reparsed;
                        }
                    }
                }
            }
        }
        return node!;
    }

    // ast/utilities.go:4505 — findCloneInNode.
    private static global::Ts.Ast.Node? FindCloneInNode(global::Ts.Ast.Node node, global::Ts.Ast.Node original)
    {
        while (true)
        {
            if (node.Kind == original.Kind && node.Loc == original.Loc)
            {
                return node;
            }
            var foundContainingChild = node.ForEachChild(n =>
            {
                if (original.Loc.ContainedBy(n.Loc))
                {
                    node = n;
                    return true;
                }
                return false;
            });
            if (!foundContainingChild)
            {
                return null;
            }
        }
    }

    // ast/utilities.go:1081 — CanHaveModifiers. The C# port exists at
    // Ts.Checker (Checker.Grammar.Deps.cs) but is `internal` to Ts.Compiler.
    internal static bool CanHaveModifiers(global::Ts.Ast.Node node)
    {
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.TypeParameter:
            case global::Ts.Ast.SyntaxKind.Parameter:
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
            case global::Ts.Ast.SyntaxKind.MethodSignature:
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
            case global::Ts.Ast.SyntaxKind.Constructor:
            case global::Ts.Ast.SyntaxKind.GetAccessor:
            case global::Ts.Ast.SyntaxKind.SetAccessor:
            case global::Ts.Ast.SyntaxKind.IndexSignature:
            case global::Ts.Ast.SyntaxKind.ConstructorType:
            case global::Ts.Ast.SyntaxKind.FunctionExpression:
            case global::Ts.Ast.SyntaxKind.ArrowFunction:
            case global::Ts.Ast.SyntaxKind.ClassExpression:
            case global::Ts.Ast.SyntaxKind.VariableStatement:
            case global::Ts.Ast.SyntaxKind.FunctionDeclaration:
            case global::Ts.Ast.SyntaxKind.ClassDeclaration:
            case global::Ts.Ast.SyntaxKind.InterfaceDeclaration:
            case global::Ts.Ast.SyntaxKind.TypeAliasDeclaration:
            case global::Ts.Ast.SyntaxKind.EnumDeclaration:
            case global::Ts.Ast.SyntaxKind.ModuleDeclaration:
            case global::Ts.Ast.SyntaxKind.ImportEqualsDeclaration:
            case global::Ts.Ast.SyntaxKind.ImportDeclaration:
            case global::Ts.Ast.SyntaxKind.JSImportDeclaration:
            case global::Ts.Ast.SyntaxKind.ExportAssignment:
            case global::Ts.Ast.SyntaxKind.ExportDeclaration:
                return true;
        }
        return false;
    }

    // ls/utilities.go:348 — isLiteralNameOfPropertyDeclarationOrIndexAccess.
    internal static bool IsLiteralNameOfPropertyDeclarationOrIndexAccess(global::Ts.Ast.Node node)
    {
        // utilities
        switch (node.Parent!.Kind) // Go dereferences Parent unconditionally.
        {
            case global::Ts.Ast.SyntaxKind.PropertyDeclaration:
            case global::Ts.Ast.SyntaxKind.PropertySignature:
            case global::Ts.Ast.SyntaxKind.PropertyAssignment:
            case global::Ts.Ast.SyntaxKind.EnumMember:
            case global::Ts.Ast.SyntaxKind.MethodDeclaration:
            case global::Ts.Ast.SyntaxKind.MethodSignature:
            case global::Ts.Ast.SyntaxKind.GetAccessor:
            case global::Ts.Ast.SyntaxKind.SetAccessor:
            case global::Ts.Ast.SyntaxKind.ModuleDeclaration:
                return global::Ts.Ast.AstUtilities.GetNameOfDeclaration(node.Parent) == node;
            case global::Ts.Ast.SyntaxKind.ElementAccessExpression:
                return node.Parent!.AsElementAccessExpression().ArgumentExpression == node;
            case global::Ts.Ast.SyntaxKind.ComputedPropertyName:
                return true;
            case global::Ts.Ast.SyntaxKind.LiteralType:
                return node.Parent!.Parent!.Kind == global::Ts.Ast.SyntaxKind.IndexedAccessType;
            default:
                return false;
        }
    }

    // ls/utilities.go:372 — isObjectBindingElementWithoutPropertyName.
    internal static bool IsObjectBindingElementWithoutPropertyName(global::Ts.Ast.Node bindingElement)
    {
        return bindingElement.Kind == global::Ts.Ast.SyntaxKind.BindingElement &&
            bindingElement.Parent!.Kind == global::Ts.Ast.SyntaxKind.ObjectBindingPattern &&
            bindingElement.Name()!.Kind == global::Ts.Ast.SyntaxKind.Identifier &&
            bindingElement.PropertyName() == null;
    }

    // lsutil/utilities.go:92 — QuotePreferenceFromString.
    internal static QuotePreference QuotePreferenceFromString(global::Ts.Ast.StringLiteral str)
    {
        if ((str.TokenFlags & global::Ts.Ast.TokenFlags.SingleQuote) != 0)
        {
            return QuotePreference.Single;
        }
        return QuotePreference.Double;
    }

    // lsutil/utilities.go:99-113 — GetQuotePreference.
    internal static QuotePreference GetQuotePreference(global::Ts.Ast.SourceFile sourceFile, UserPreferences preferences)
    {
        if (preferences.QuotePreference.Length != 0 && preferences.QuotePreference != "auto")
        {
            if (preferences.QuotePreference == "single")
            {
                return QuotePreference.Single;
            }
            return QuotePreference.Double;
        }
        // ignore synthetic import added when importHelpers: true
        foreach (var n in sourceFile.Imports() ?? [])
        {
            if (global::Ts.Ast.NodeTests.IsStringLiteral(n) && !global::Ts.Ast.AstUtilities.NodeIsSynthesized(n.Parent!))
            {
                return QuotePreferenceFromString(n.AsStringLiteral());
            }
        }
        return QuotePreference.Double;
    }

    // Zero-preferences overload retained for pre-UserPreferences call sites (rename.go path);
    // Go's zero value falls through to the module-specifier scan identically.
    internal static QuotePreference GetQuotePreference(global::Ts.Ast.SourceFile sourceFile)
    {
        return GetQuotePreference(sourceFile, new UserPreferences());
    }

    // ls/utilities.go:452 — getAdjustedLocation.
    internal static global::Ts.Ast.Node GetAdjustedLocation(global::Ts.Ast.Node node, bool forRename, global::Ts.Ast.SourceFile? sourceFile)
    {
        // todo: check if this function needs to be changed for jsdoc updates

        var parent = node.Parent!;
        // /**/<modifier> [|name|] ...
        // /**/<modifier> <class|interface|type|enum|module|namespace|function|get|set> [|name|] ...
        // /**/<class|interface|type|enum|module|namespace|function|get|set> [|name|] ...
        // /**/import [|name|] = ...
        //
        // NOTE: If the node is a modifier, we don't adjust its location if it is the `default` modifier as that is handled
        // specially by `getSymbolAtLocation`.
        bool IsModifier(global::Ts.Ast.Node n)
        {
            if (global::Ts.Ast.AstUtilities.IsModifier(n) && (forRename || n.Kind != global::Ts.Ast.SyntaxKind.DefaultKeyword))
            {
                if (!CanHaveModifiers(parent))
                {
                    return false;
                }
                foreach (var modifier in parent.ModifierNodes() ?? [])
                {
                    if (modifier == n)
                    {
                        return true;
                    }
                }
                return false;
            }
            switch (n.Kind)
            {
                case global::Ts.Ast.SyntaxKind.ClassKeyword:
                    return global::Ts.Ast.NodeTests.IsClassDeclaration(parent) || global::Ts.Ast.NodeTests.IsClassExpression(n);
                case global::Ts.Ast.SyntaxKind.FunctionKeyword:
                    return global::Ts.Ast.NodeTests.IsFunctionDeclaration(parent) || global::Ts.Ast.NodeTests.IsFunctionExpression(n);
                case global::Ts.Ast.SyntaxKind.InterfaceKeyword:
                    return global::Ts.Ast.NodeTests.IsInterfaceDeclaration(parent);
                case global::Ts.Ast.SyntaxKind.EnumKeyword:
                    return global::Ts.Ast.NodeTests.IsEnumDeclaration(parent);
                case global::Ts.Ast.SyntaxKind.TypeKeyword:
                    return global::Ts.Ast.NodeTests.IsTypeAliasDeclaration(parent);
                case global::Ts.Ast.SyntaxKind.NamespaceKeyword:
                case global::Ts.Ast.SyntaxKind.ModuleKeyword:
                    return global::Ts.Ast.NodeTests.IsModuleDeclaration(parent);
                case global::Ts.Ast.SyntaxKind.ImportKeyword:
                    return global::Ts.Ast.NodeTests.IsImportEqualsDeclaration(parent);
                case global::Ts.Ast.SyntaxKind.GetKeyword:
                    return global::Ts.Ast.NodeTests.IsGetAccessorDeclaration(parent);
                case global::Ts.Ast.SyntaxKind.SetKeyword:
                    return global::Ts.Ast.NodeTests.IsSetAccessorDeclaration(parent);
            }
            return false;
        }
        if (IsModifier(node))
        {
            sourceFile ??= global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node);
            var declLocation = GetAdjustedLocationForDeclaration(parent, forRename, sourceFile!);
            if (declLocation != null)
            {
                return declLocation;
            }
        }

        // /**/<var|let|const> [|name|] ...
        if ((node.Kind == global::Ts.Ast.SyntaxKind.VarKeyword ||
                node.Kind == global::Ts.Ast.SyntaxKind.ConstKeyword ||
                node.Kind == global::Ts.Ast.SyntaxKind.LetKeyword) &&
            global::Ts.Ast.NodeTests.IsVariableDeclarationList(parent) &&
            parent.AsVariableDeclarationList().Declarations!.Nodes.Length == 1)
        {
            var declaration = parent.AsVariableDeclarationList().Declarations!.Nodes[0];
            if (global::Ts.Ast.NodeTests.IsIdentifier(declaration.Name()!))
            {
                return declaration.Name()!;
            }
        }

        if (node.Kind == global::Ts.Ast.SyntaxKind.TypeKeyword)
        {
            // import /**/type [|name|] from ...;
            // import /**/type { [|name|] } from ...;
            // import /**/type { propertyName as [|name|] } from ...;
            // import /**/type ... from "[|module|]";
            if (global::Ts.Ast.NodeTests.IsImportClause(parent) && parent.IsTypeOnly())
            {
                var location = GetAdjustedLocationForImportDeclaration(parent.Parent!.AsImportDeclaration(), forRename);
                if (location != null)
                {
                    return location;
                }
            }
            // export /**/type { [|name|] } from ...;
            // export /**/type { propertyName as [|name|] } from ...;
            // export /**/type * from "[|module|]";
            // export /**/type * as ... from "[|module|]";
            if (global::Ts.Ast.NodeTests.IsExportDeclaration(parent) && parent.IsTypeOnly())
            {
                var location = GetAdjustedLocationForExportDeclaration(parent.AsExportDeclaration(), forRename);
                if (location != null)
                {
                    return location;
                }
            }
        }

        // import { propertyName /**/as [|name|] } ...
        // import * /**/as [|name|] ...
        // export { propertyName /**/as [|name|] } ...
        // export * /**/as [|name|] ...
        if (node.Kind == global::Ts.Ast.SyntaxKind.AsKeyword)
        {
            if ((parent.Kind == global::Ts.Ast.SyntaxKind.ImportSpecifier && parent.PropertyName() != null) ||
                (parent.Kind == global::Ts.Ast.SyntaxKind.ExportSpecifier && parent.PropertyName() != null) ||
                parent.Kind == global::Ts.Ast.SyntaxKind.NamespaceImport ||
                parent.Kind == global::Ts.Ast.SyntaxKind.NamespaceExport)
            {
                return parent.Name()!;
            }
            if (parent.Kind == global::Ts.Ast.SyntaxKind.ExportDeclaration)
            {
                var exportClause = parent.AsExportDeclaration().ExportClause;
                if (exportClause != null && exportClause.Kind == global::Ts.Ast.SyntaxKind.NamespaceExport)
                {
                    return exportClause.Name()!;
                }
            }
        }

        // /**/import [|name|] from ...;
        // /**/import { [|name|] } from ...;
        // /**/import { propertyName as [|name|] } from ...;
        // /**/import ... from "[|module|]";
        // /**/import "[|module|]";
        if (node.Kind == global::Ts.Ast.SyntaxKind.ImportKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.ImportDeclaration)
        {
            var location = GetAdjustedLocationForImportDeclaration(parent.AsImportDeclaration(), forRename);
            if (location != null)
            {
                return location;
            }
        }

        if (node.Kind == global::Ts.Ast.SyntaxKind.ExportKeyword)
        {
            // /**/export { [|name|] } ...;
            // /**/export { propertyName as [|name|] } ...;
            // /**/export * from "[|module|]";
            // /**/export * as ... from "[|module|]";
            if (parent.Kind == global::Ts.Ast.SyntaxKind.ExportDeclaration)
            {
                var location = GetAdjustedLocationForExportDeclaration(parent.AsExportDeclaration(), forRename);
                if (location != null)
                {
                    return location;
                }
            }
            // NOTE: We don't adjust the location of the `default` keyword as that is handled specially by `getSymbolAtLocation`.
            // /**/export default [|name|];
            // /**/export = [|name|];
            if (parent.Kind == global::Ts.Ast.SyntaxKind.ExportAssignment)
            {
                return global::Ts.Ast.AstUtilities.SkipOuterExpressions(parent.Expression()!, global::Ts.Ast.OuterExpressionKinds.All);
            }
        }
        // import name = /**/require("[|module|]");
        if (node.Kind == global::Ts.Ast.SyntaxKind.RequireKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.ExternalModuleReference)
        {
            return parent.Expression()!;
        }
        // import ... /**/from "[|module|]";
        // export ... /**/from "[|module|]";
        if (node.Kind == global::Ts.Ast.SyntaxKind.FromKeyword)
        {
            if ((parent.Kind == global::Ts.Ast.SyntaxKind.ImportDeclaration || parent.Kind == global::Ts.Ast.SyntaxKind.ExportDeclaration) &&
                parent.ModuleSpecifier() != null)
            {
                return parent.ModuleSpecifier()!;
            }
        }
        // class ... /**/extends [|name|] ...
        // class ... /**/implements [|name|] ...
        // class ... /**/implements name1, name2 ...
        // interface ... /**/extends [|name|] ...
        // interface ... /**/extends name1, name2 ...
        if ((node.Kind == global::Ts.Ast.SyntaxKind.ExtendsKeyword || node.Kind == global::Ts.Ast.SyntaxKind.ImplementsKeyword) &&
            parent.Kind == global::Ts.Ast.SyntaxKind.HeritageClause &&
            parent.AsHeritageClause().Token == node.Kind)
        {
            // /**/extends [|name|]
            // /**/implements [|name|]
            //
            // (with more than one type, fall through `getAdjustedLocation`:
            //    /**/extends name1, name2 ...
            //    /**/implements name1, name2 ...)
            var heritageClause = parent.AsHeritageClause();
            if (heritageClause.Types!.Nodes.Length == 1)
            {
                var location = heritageClause.Types!.Nodes[0].Expression();
                if (location != null)
                {
                    return location;
                }
            }
        }
        if (node.Kind == global::Ts.Ast.SyntaxKind.ExtendsKeyword)
        {
            // ... <T /**/extends [|U|]> ...
            if (parent.Kind == global::Ts.Ast.SyntaxKind.TypeParameter)
            {
                var constraint = parent.AsTypeParameterDeclaration().Constraint;
                if (constraint != null && constraint.Kind == global::Ts.Ast.SyntaxKind.TypeReference)
                {
                    return constraint.AsTypeReferenceNode().TypeName!;
                }
            }
            // ... T /**/extends [|U|] ? ...
            if (parent.Kind == global::Ts.Ast.SyntaxKind.ConditionalType)
            {
                var extendsType = parent.AsConditionalTypeNode().ExtendsType;
                if (extendsType != null && extendsType.Kind == global::Ts.Ast.SyntaxKind.TypeReference)
                {
                    return extendsType.AsTypeReferenceNode().TypeName!;
                }
            }
        }
        // ... T extends /**/infer [|U|] ? ...
        if (node.Kind == global::Ts.Ast.SyntaxKind.InferKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.InferType)
        {
            return parent.AsInferTypeNode().TypeParameter!.Name()!;
        }
        // { [ [|K|] /**/in keyof T]: ... }
        if (node.Kind == global::Ts.Ast.SyntaxKind.InKeyword &&
            parent.Kind == global::Ts.Ast.SyntaxKind.TypeParameter &&
            parent.Parent!.Kind == global::Ts.Ast.SyntaxKind.MappedType)
        {
            return parent.Name()!;
        }
        // /**/keyof [|T|]
        if (node.Kind == global::Ts.Ast.SyntaxKind.KeyOfKeyword &&
            parent.Kind == global::Ts.Ast.SyntaxKind.TypeOperator &&
            parent.AsTypeOperatorNode().Operator == global::Ts.Ast.SyntaxKind.KeyOfKeyword)
        {
            var parentType = parent.AsTypeOperatorNode().Type;
            if (parentType != null && parentType.Kind == global::Ts.Ast.SyntaxKind.TypeReference)
            {
                return parentType.AsTypeReferenceNode().TypeName!;
            }
        }
        // /**/readonly [|name|][]
        if (node.Kind == global::Ts.Ast.SyntaxKind.ReadonlyKeyword &&
            parent.Kind == global::Ts.Ast.SyntaxKind.TypeOperator &&
            parent.AsTypeOperatorNode().Operator == global::Ts.Ast.SyntaxKind.ReadonlyKeyword)
        {
            var parentType = parent.AsTypeOperatorNode().Type;
            if (parentType != null &&
                parentType.Kind == global::Ts.Ast.SyntaxKind.ArrayType &&
                parentType.AsArrayTypeNode().ElementType!.Kind == global::Ts.Ast.SyntaxKind.TypeReference)
            {
                return parentType.AsArrayTypeNode().ElementType!.AsTypeReferenceNode().TypeName!;
            }
        }

        if (!forRename)
        {
            // /**/new [|name|]
            // /**/void [|name|]
            // /**/void obj.[|name|]
            // /**/typeof [|name|]
            // /**/typeof obj.[|name|]
            // /**/await [|name|]
            // /**/await obj.[|name|]
            // /**/yield [|name|]
            // /**/yield obj.[|name|]
            // /**/delete obj.[|name|]
            if ((node.Kind == global::Ts.Ast.SyntaxKind.NewKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.NewExpression) ||
                (node.Kind == global::Ts.Ast.SyntaxKind.VoidKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.VoidExpression) ||
                (node.Kind == global::Ts.Ast.SyntaxKind.TypeOfKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.TypeOfExpression) ||
                (node.Kind == global::Ts.Ast.SyntaxKind.AwaitKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.AwaitExpression) ||
                (node.Kind == global::Ts.Ast.SyntaxKind.YieldKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.YieldExpression) ||
                (node.Kind == global::Ts.Ast.SyntaxKind.DeleteKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.DeleteExpression))
            {
                var expr = parent.Expression();
                if (expr != null)
                {
                    return global::Ts.Ast.AstUtilities.SkipOuterExpressions(expr, global::Ts.Ast.OuterExpressionKinds.All);
                }
            }

            // left /**/in [|name|]
            // left /**/instanceof [|name|]
            if ((node.Kind == global::Ts.Ast.SyntaxKind.InKeyword || node.Kind == global::Ts.Ast.SyntaxKind.InstanceOfKeyword) &&
                parent.Kind == global::Ts.Ast.SyntaxKind.BinaryExpression &&
                parent.AsBinaryExpression().OperatorToken == node)
            {
                return global::Ts.Ast.AstUtilities.SkipOuterExpressions(parent.AsBinaryExpression().Right!, global::Ts.Ast.OuterExpressionKinds.All);
            }

            // left /**/as [|name|]
            if (node.Kind == global::Ts.Ast.SyntaxKind.AsKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.AsExpression)
            {
                var asExprType = parent.Type();
                if (asExprType != null && asExprType.Kind == global::Ts.Ast.SyntaxKind.TypeReference)
                {
                    return asExprType.AsTypeReferenceNode().TypeName!;
                }
            }

            // for (... /**/in [|name|])
            // for (... /**/of [|name|])
            if ((node.Kind == global::Ts.Ast.SyntaxKind.InKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.ForInStatement) ||
                (node.Kind == global::Ts.Ast.SyntaxKind.OfKeyword && parent.Kind == global::Ts.Ast.SyntaxKind.ForOfStatement))
            {
                return global::Ts.Ast.AstUtilities.SkipOuterExpressions(parent.Expression()!, global::Ts.Ast.OuterExpressionKinds.All);
            }
        }

        return node;
    }

    // ls/utilities.go:683 — getAdjustedLocationForDeclaration.
    private static global::Ts.Ast.Node? GetAdjustedLocationForDeclaration(global::Ts.Ast.Node node, bool forRename, global::Ts.Ast.SourceFile sourceFile)
    {
        if (node.Name() != null)
        {
            return node.Name();
        }
        if (forRename)
        {
            return null;
        }
        switch (node.Kind)
        {
            case global::Ts.Ast.SyntaxKind.ClassDeclaration:
            case global::Ts.Ast.SyntaxKind.FunctionDeclaration:
                // for class and function declarations, use the `default` modifier
                // when the declaration is unnamed.
                // PORT-NOTE: the Go predicate ignores its parameter and closes over the
                // declaration `node` (utilities.go:700); kept faithfully.
                foreach (var modifier in node.ModifierNodes() ?? [])
                {
                    if (node.Kind == global::Ts.Ast.SyntaxKind.DefaultKeyword)
                    {
                        return modifier;
                    }
                }
                return null;
            case global::Ts.Ast.SyntaxKind.ClassExpression:
                // for class expressions, use the `class` keyword when the class is unnamed
                return global::Ts.AstNav.Tokens.FindChildOfKind(node, global::Ts.Ast.SyntaxKind.ClassKeyword, sourceFile);
            case global::Ts.Ast.SyntaxKind.FunctionExpression:
                // for function expressions, use the `function` keyword when the function is unnamed
                return global::Ts.AstNav.Tokens.FindChildOfKind(node, global::Ts.Ast.SyntaxKind.FunctionKeyword, sourceFile);
            case global::Ts.Ast.SyntaxKind.Constructor:
                return node;
        }
        return null;
    }

    // ls/utilities.go:713 — getAdjustedLocationForImportDeclaration.
    private static global::Ts.Ast.Node? GetAdjustedLocationForImportDeclaration(global::Ts.Ast.ImportDeclaration node, bool forRename)
    {
        if (node.ImportClause != null)
        {
            var importClause = node.ImportClause; // ImportClauseNode aliases Ts.Ast.Node.
            var name = importClause.Name();
            if (name != null)
            {
                if (importClause.AsImportClause().NamedBindings != null)
                {
                    // do not adjust if we have both a name and named bindings
                    return null;
                }
                // /**/import [|name|] from ...;
                // import /**/type [|name|] from ...;
                return name;
            }

            // /**/import { [|name|] } from ...;
            // /**/import { propertyName as [|name|] } from ...;
            // /**/import * as [|name|] from ...;
            // import /**/type { [|name|] } from ...;
            // import /**/type { propertyName as [|name|] } from ...;
            // import /**/type * as [|name|] from ...;
            var namedBindings = importClause.AsImportClause().NamedBindings;
            if (namedBindings != null)
            {
                switch (namedBindings.Kind)
                {
                    case global::Ts.Ast.SyntaxKind.NamedImports:
                        // do nothing if there is more than one binding
                        var elements = namedBindings.Elements();
                        if (elements == null || elements.Length != 1)
                        {
                            return null;
                        }
                        return elements[0].Name();

                    case global::Ts.Ast.SyntaxKind.NamespaceImport:
                        return namedBindings.Name();
                }
            }
        }
        if (!forRename)
        {
            // /**/import "[|module|]";
            // /**/import ... from "[|module|]";
            // import /**/type ... from "[|module|]";
            return node.ModuleSpecifier;
        }
        return null;
    }

    // ls/utilities.go:756 — getAdjustedLocationForExportDeclaration.
    private static global::Ts.Ast.Node? GetAdjustedLocationForExportDeclaration(global::Ts.Ast.ExportDeclaration node, bool forRename)
    {
        if (node.ExportClause != null)
        {
            // /**/export { [|name|] } ...
            // /**/export { propertyName as [|name|] } ...
            // /**/export * as [|name|] ...
            // export /**/type { [|name|] } from ...
            // export /**/type { propertyName as [|name|] } from ...
            // export /**/type * as [|name|] ...
            var exportClause = node.ExportClause; // NamedExportBindings aliases Ts.Ast.Node.
            switch (exportClause.Kind)
            {
                case global::Ts.Ast.SyntaxKind.NamedExports:
                    // do nothing if there is more than one binding
                    var elements = exportClause.Elements();
                    if (elements == null || elements.Length != 1)
                    {
                        return null;
                    }
                    return elements[0].Name();
                case global::Ts.Ast.SyntaxKind.NamespaceExport:
                    return exportClause.Name();
            }
        }
        if (!forRename)
        {
            // /**/export * from "[|module|]";
            // export /**/type * from "[|module|]";
            return node.ModuleSpecifier;
        }
        return null;
    }

    // ast/ast.go:1404 — IsArrayLiteralOrObjectLiteralDestructuringPattern.
    internal static bool IsArrayLiteralOrObjectLiteralDestructuringPattern(global::Ts.Ast.Node node)
    {
        if (!(global::Ts.Ast.NodeTests.IsArrayLiteralExpression(node) || global::Ts.Ast.NodeTests.IsObjectLiteralExpression(node)))
        {
            return false;
        }
        var parent = node.Parent!;
        // [a,b,c] from:
        // [a, b, c] = someExpression;
        if (global::Ts.Ast.NodeTests.IsBinaryExpression(parent) &&
            parent.AsBinaryExpression().Left == node &&
            parent.AsBinaryExpression().OperatorToken!.Kind == global::Ts.Ast.SyntaxKind.EqualsToken)
        {
            return true;
        }
        // [a, b, c] from:
        // for([a, b, c] of expression)
        if (global::Ts.Ast.NodeTests.IsForOfStatement(parent) && parent.Initializer() == node)
        {
            return true;
        }
        // {x, a: {a, b, c} } = someExpression
        if (global::Ts.Ast.NodeTests.IsPropertyAssignment(parent))
        {
            return IsArrayLiteralOrObjectLiteralDestructuringPattern(parent.Parent!);
        }
        // [a, b, c] of
        // [x, [a, b, c] ] = someExpression
        return IsArrayLiteralOrObjectLiteralDestructuringPattern(parent);
    }

    // utilities.go:1139-1141 — isInsideTemplateLiteral.
    internal static bool IsInsideTemplateLiteral(global::Ts.Ast.Node node, int position, global::Ts.Ast.SourceFile sourceFile)
    {
        return global::Ts.Ast.AstUtilities.IsTemplateLiteralKind(node.Kind) &&
            (global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false) < position && position < node.End ||
                (global::Ts.Ast.AstUtilities.IsUnterminatedLiteral(node) && position == node.End));
    }
}
