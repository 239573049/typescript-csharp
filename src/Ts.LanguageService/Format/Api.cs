// Ported from tsgo internal/format/api.go
//
// PORT-NOTE: Go threads options + newline through a context.Context
// (WithFormatCodeSettings / GetFormatCodeSettingsFromContext /
// GetNewLineOrDefaultFromContext). C# has no ambient context; we model the format
// context as an explicit `FormatContext` value passed to the API entry points. The
// three Go context helpers become methods on FormatContext with identical defaults.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

// Go: type FormatRequestKind int (iota order).
public enum FormatRequestKind
{
    FormatDocument = 0,
    FormatSelection = 1,
    FormatOnEnter = 2,
    FormatOnSemicolon = 3,
    FormatOnOpeningCurlyBrace = 4,
    FormatOnClosingCurlyBrace = 5,
}

// PORT-NOTE: replaces Go's context.Context values (formatOptionsKey/formatNewlineKey).
public sealed class FormatContext
{
    public FormatCodeSettings? Options;
    public string HostNewLine = "";

    // Go: func WithFormatCodeSettings(ctx, options, newLine) context.Context
    public static FormatContext WithFormatCodeSettings(FormatCodeSettings options, string newLine)
    {
        // In strada, the rules map was both globally cached *and* cached into the context, for some reason.
        // We skip that here and just use the global one.
        return new FormatContext { Options = options, HostNewLine = newLine };
    }

    // Go: func GetFormatCodeSettingsFromContext(ctx) lsutil.FormatCodeSettings
    public FormatCodeSettings GetFormatCodeSettings()
    {
        if (Options is not null)
        {
            return Options;
        }
        return FormatCodeSettings.GetDefaultFormatCodeSettings();
    }

    // Go: func GetNewLineOrDefaultFromContext(ctx) string
    public string GetNewLineOrDefault()
    {
        var opt = GetFormatCodeSettings();
        if (opt.NewLineCharacter.Length > 0)
        {
            return opt.NewLineCharacter;
        }
        if (HostNewLine.Length > 0)
        {
            return HostNewLine;
        }
        return "\n";
    }
}

public static class Api
{
    // Go: func FormatSpan(ctx, span, file, kind) []core.TextChange
    public static TextChange[] FormatSpan(FormatContext ctx, TextRange span, SourceFile file, FormatRequestKind kind)
    {
        // find the smallest node that fully wraps the range and compute the initial indentation for the node
        Node enclosingNode = Span.FindEnclosingNode(span, file);
        FormatCodeSettings opts = ctx.GetFormatCodeSettings();

        return FormattingScanner.NewFormattingScanner(
            file.Text(),
            file.LanguageVariant,
            Span.GetScanStartPosition(enclosingNode, span, file),
            span.End,
            Span.NewFormatSpanWorker(
                ctx,
                span,
                enclosingNode,
                Indent.GetIndentationForNode(enclosingNode, span, file, opts),
                Span.GetOwnOrInheritedDelta(enclosingNode, opts, file),
                kind,
                Span.PrepareRangeContainsErrorFunction(file.Diagnostics(), span),
                file));
    }

    // Go: func FormatNodeGivenIndentation(ctx, node, file, languageVariant, initialIndentation, delta) []core.TextChange
    public static TextChange[] FormatNodeGivenIndentation(FormatContext ctx, Node node, SourceFile file, LanguageVariant languageVariant, int initialIndentation, int delta)
    {
        var textRange = new TextRange(node.Pos, node.End);
        return FormattingScanner.NewFormattingScanner(
            file.Text(),
            languageVariant,
            textRange.Pos,
            textRange.End,
            Span.NewFormatSpanWorker(
                ctx,
                textRange,
                node,
                initialIndentation,
                delta,
                FormatRequestKind.FormatSelection,
                static (TextRange _) => false, // assume that node does not have any errors
                file));
    }

    // Go: func formatNodeLines(ctx, sourceFile, node, requestKind) []core.TextChange
    internal static TextChange[] FormatNodeLines(FormatContext ctx, SourceFile sourceFile, Node? node, FormatRequestKind requestKind)
    {
        if (node is null)
        {
            return Array.Empty<TextChange>();
        }
        int tokenStart = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, false);
        int lineStart = Util.GetLineStartPositionForPosition(tokenStart, sourceFile);
        var span = new TextRange(lineStart, node.End);
        return FormatSpan(ctx, span, sourceFile, requestKind);
    }

    // Go: func FormatDocument(ctx, sourceFile) []core.TextChange
    public static TextChange[] FormatDocument(FormatContext ctx, SourceFile sourceFile)
    {
        return FormatSpan(ctx, new TextRange(0, sourceFile.End), sourceFile, FormatRequestKind.FormatDocument);
    }

    // Go: func FormatSelection(ctx, sourceFile, start, end) []core.TextChange
    public static TextChange[] FormatSelection(FormatContext ctx, SourceFile sourceFile, int start, int end)
    {
        return FormatSpan(ctx, new TextRange(Util.GetLineStartPositionForPosition(start, sourceFile), end), sourceFile, FormatRequestKind.FormatSelection);
    }

    // Go: func FormatOnOpeningCurly(ctx, sourceFile, position) []core.TextChange
    public static TextChange[] FormatOnOpeningCurly(FormatContext ctx, SourceFile sourceFile, int position)
    {
        Node? openingCurly = Util.FindImmediatelyPrecedingTokenOfKind(position, SyntaxKind.OpenBraceToken, sourceFile);
        if (openingCurly is null)
        {
            return Array.Empty<TextChange>();
        }
        Node? curlyBraceRange = openingCurly.Parent;
        Node? outermostNode = Util.FindOutermostNodeWithinListLevel(curlyBraceRange);
        // We limit the span to end at the opening curly to handle the case where the brace
        // matched to that just typed will be incorrect after further edits.
        var textRange = new TextRange(
            Util.GetLineStartPositionForPosition(global::Ts.Scanner.Scanner.GetTokenPosOfNode(outermostNode!, sourceFile, false), sourceFile),
            position);
        return FormatSpan(ctx, textRange, sourceFile, FormatRequestKind.FormatOnOpeningCurlyBrace);
    }

    // Go: func FormatOnClosingCurly(ctx, sourceFile, position) []core.TextChange
    public static TextChange[] FormatOnClosingCurly(FormatContext ctx, SourceFile sourceFile, int position)
    {
        Node? precedingToken = Util.FindImmediatelyPrecedingTokenOfKind(position, SyntaxKind.CloseBraceToken, sourceFile);
        return FormatNodeLines(ctx, sourceFile, Util.FindOutermostNodeWithinListLevel(precedingToken), FormatRequestKind.FormatOnClosingCurlyBrace);
    }

    // Go: func FormatOnSemicolon(ctx, sourceFile, position) []core.TextChange
    public static TextChange[] FormatOnSemicolon(FormatContext ctx, SourceFile sourceFile, int position)
    {
        Node? semicolon = Util.FindImmediatelyPrecedingTokenOfKind(position, SyntaxKind.SemicolonToken, sourceFile);
        return FormatNodeLines(ctx, sourceFile, Util.FindOutermostNodeWithinListLevel(semicolon), FormatRequestKind.FormatOnSemicolon);
    }

    // Go: func FormatOnEnter(ctx, sourceFile, position) []core.TextChange
    public static TextChange[] FormatOnEnter(FormatContext ctx, SourceFile sourceFile, int position)
    {
        int line = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, position);
        if (line == 0)
        {
            return Array.Empty<TextChange>();
        }
        // get start position for the previous line
        int startPos = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile)[line - 1];
        // After the enter key, the cursor is now at a new line...
        int endOfFormatSpan = global::Ts.Scanner.Scanner.GetECMAEndLinePosition(sourceFile, line);
        string text = sourceFile.Text();
        while (endOfFormatSpan > startPos)
        {
            (int ch, int s) = DecodeRuneInString(text, endOfFormatSpan);
            if (s == 0 || global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch)) // on multibyte character keep backing up
            {
                endOfFormatSpan--;
                continue;
            }
            break;
        }

        // if the character at the end of the span is a line break, we shouldn't include it...
        (int lastCh, int _) = DecodeRuneInString(text, endOfFormatSpan);
        if (global::Ts.StringUtil.StringUtil.IsLineBreak(lastCh))
        {
            endOfFormatSpan--;
        }

        var span = new TextRange(
            startPos,
            // end value is exclusive so add 1 to the result
            endOfFormatSpan + 1);

        return FormatSpan(ctx, span, sourceFile, FormatRequestKind.FormatOnEnter);
    }

    // PORT-NOTE: reproduces utf8.DecodeRuneInString(s[pos:]) semantics over a C# UTF-16
    // string. Positions in this port are UTF-16 code units (plan/01 §UTF-16). Returns
    // the code point at `pos` (combining a surrogate pair when present) and the number
    // of UTF-16 code units consumed; (0, 0) at or past end of string.
    private static (int Ch, int Size) DecodeRuneInString(string s, int pos)
    {
        if (pos < 0 || pos >= s.Length)
        {
            return (0, 0);
        }
        char c = s[pos];
        if (char.IsHighSurrogate(c) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
        {
            return (char.ConvertToUtf32(c, s[pos + 1]), 2);
        }
        return (c, 1);
    }
}
