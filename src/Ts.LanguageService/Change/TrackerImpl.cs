// Ported from tsgo internal/ls/change/trackerimpl.go (whole file): rendering accumulated
// tracker edits into LSP text edits (sorting/overlap validation, node printing through the
// ChangeTrackerWriter, format-driven indentation, adjusted trivia positions).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Ls.Change;

public sealed partial class Tracker
{
    // trackerimpl.go:21-55 — getTextChangesFromChanges.
    internal Dictionary<string, List<global::Ts.LsProto.TextEdit>> GetTextChangesFromChanges()
    {
        var result = new Dictionary<string, List<global::Ts.LsProto.TextEdit>>(StringComparer.Ordinal);
        foreach (var (sourceFile, changesInFile) in changes.M ?? []) // map-iter: 序无关 (per-file buckets; each file's edits sorted below)
        {
            // order changes by start position
            // If the start position is the same, put the shorter range first, since an empty range (x, x) may precede (x, y) but not vice-versa.
            changesInFile.Sort((a, b) => global::Ts.LsProto.Util.CompareRanges(a.Range, b.Range));
            // verify that change intervals do not overlap, except possibly at end points.
            for (var i = 0; i < changesInFile.Count - 1; i++)
            {
                if (global::Ts.LsProto.Util.ComparePositions(changesInFile[i].Range.End, changesInFile[i + 1].Range.Start) > 0)
                {
                    // assert change[i].End <= change[i + 1].Start
                    throw new InvalidOperationException($"changes overlap: {changesInFile[i].Range} and {changesInFile[i + 1].Range}");
                }
            }

            var textChanges = new List<global::Ts.LsProto.TextEdit>(changesInFile.Count);
            foreach (var change in changesInFile)
            {
                // !!! targetSourceFile

                var newText = ComputeNewText(change, sourceFile, sourceFile);
                // span := createTextSpanFromRange(c.Range)
                // !!!
                // Filter out redundant changes.
                // if (span.length == newText.length && stringContainsAt(targetSourceFile.text, newText, span.start)) { return nil }

                textChanges.Add(new global::Ts.LsProto.TextEdit
                {
                    NewText = newText,
                    Range = change.Range,
                });
            }

            if (textChanges.Count > 0)
            {
                result[sourceFile.FileName()] = textChanges;
            }
        }
        return result;
    }

    // trackerimpl.go:57-90 — computeNewText.
    internal string ComputeNewText(TrackerEdit change, SourceFile targetSourceFile, SourceFile sourceFile)
    {
        switch (change.kind)
        {
            case TrackerEditKind.Remove:
                return "";
            case TrackerEditKind.Text:
                return change.NewText;
        }

        var pos = converters.LineAndCharacterToPosition(sourceFile.AsScript(), change.Range.Start);
        string FormatNode(Node n) => GetFormattedTextOfNode(n, targetSourceFile, sourceFile, pos, change.options);

        string text;
        switch (change.kind)
        {
            case TrackerEditKind.ReplaceWithMultipleNodes:
            {
                if (change.options.joiner.Length == 0)
                {
                    change.options.joiner = newLine;
                }
                var parts = new List<string>(change.nodes!.Length);
                foreach (var n in change.nodes)
                {
                    var formatted = FormatNode(n);
                    if (formatted.EndsWith(newLine, StringComparison.Ordinal))
                    {
                        formatted = formatted[..^newLine.Length];
                    }
                    parts.Add(formatted);
                }
                text = string.Join(change.options.joiner, parts);
                break;
            }
            case TrackerEditKind.ReplaceWithSingleNode:
                text = FormatNode(change.Node!);
                break;
            default:
                throw new InvalidOperationException($"change kind {change.kind} should have been handled earlier");
        }
        // strip initial indentation (spaces or tabs) if text will be inserted in the middle of the line
        var noIndent = text;
        if (!(change.options.indentation != null || global::Ts.Format.Util.GetLineStartPositionForPosition(pos, targetSourceFile) == pos))
        {
            noIndent = TrimLeftUnicodeSpace(text);
        }
        return change.options.Prefix + noIndent + (noIndent.EndsWith(change.options.Suffix, StringComparison.Ordinal) ? "" : change.options.Suffix);
    }

    // Go strings.TrimLeftFunc(text, unicode.IsSpace).
    private static string TrimLeftUnicodeSpace(string text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        return text[i..];
    }

    // trackerimpl.go:92-112 — getFormattedTextOfNode. Note: this may mutate `nodeIn`.
    internal string GetFormattedTextOfNode(Node nodeIn, SourceFile targetSourceFile, SourceFile sourceFile, int pos, NodeOptions options)
    {
        var (text, sourceFileLike) = GetNonformattedText(nodeIn, targetSourceFile);
        // !!! if (validate) validate(node, text);
        var formatOptions = GetFormatCodeSettingsForWriting(formatSettings, targetSourceFile);

        int initialIndentation, delta = 0;
        if (options.indentation == null)
        {
            initialIndentation = global::Ts.Format.Indent.GetIndentation(pos, sourceFile, formatOptions, options.Prefix == newLine || global::Ts.Format.Util.GetLineStartPositionForPosition(pos, targetSourceFile) == pos);
        }
        else
        {
            initialIndentation = options.indentation.Value;
        }

        if (options.delta != null)
        {
            delta = options.delta.Value;
        }
        else if (formatOptions.IndentSize != 0 && global::Ts.Format.Indent.ShouldIndentChildNode(formatOptions, nodeIn, null, null))
        {
            delta = formatOptions.IndentSize;
        }

        var formatCtx = global::Ts.Format.FormatContext.WithFormatCodeSettings(formatOptions, newLine);
        var formatChanges = global::Ts.Format.Api.FormatNodeGivenIndentation(formatCtx, sourceFileLike, sourceFileLike.AsSourceFile(), targetSourceFile.LanguageVariant, initialIndentation, delta);
        return global::Ts.Core.Core.ApplyBulkEdits(text, formatChanges);
    }

    // trackerimpl.go:114-122 — getFormatCodeSettingsForWriting.
    internal static global::Ts.Format.FormatCodeSettings GetFormatCodeSettingsForWriting(global::Ts.Format.FormatCodeSettings options, SourceFile sourceFile)
    {
        var shouldAutoDetectSemicolonPreference = options.Semicolons.IsIgnore;
        var shouldRemoveSemicolons = options.Semicolons.IsRemove || shouldAutoDetectSemicolonPreference && !Asi.ProbablyUsesSemicolons(sourceFile);
        if (shouldRemoveSemicolons)
        {
            options.Semicolons = global::Ts.Format.SemicolonPreference.Remove;
        }

        return options;
    }

    // trackerimpl.go:124-159 — getNonformattedText.
    internal (string Text, Node SourceFileLike) GetNonformattedText(Node node, SourceFile sourceFile)
    {
        var ctWriter = new global::Ts.Printer.ChangeTrackerWriter(newLine, formatSettings.IndentSize);
        global::Ts.Printer.Printer.NewPrinter(
            new global::Ts.Printer.PrinterOptions
            {
                NewLine = global::Ts.Core.Core.GetNewLineKind(newLine),
                NeverAsciiEscape = true,
                PreserveSourceNewlines = true,
                TerminateUnterminatedLiterals = true,
            },
            ctWriter.GetPrintHandlers(),
            EmitContext
        ).Write(node, sourceFile, ctWriter, null);

        var text = ctWriter.String();
        if (text.EndsWith(newLine, StringComparison.Ordinal))
        {
            text = text[..^newLine.Length];
        }

        var nodeOut = ctWriter.AssignPositionsToNode(node, NodeFactory);
        var eofToken = NodeFactory.NewToken(SyntaxKind.EndOfFile);
        var nodeList = NodeFactory.NewNodeList([nodeOut]);
        nodeList.Loc = nodeOut.Loc;
        eofToken.Loc = new TextRange(nodeOut.End, nodeOut.End);
        var sourceFileLike = NodeFactory.NewSourceFile(
            new SourceFileParseOptions { FileName = sourceFile.FileName(), Path = sourceFile.Path() },
            text,
            nodeList,
            eofToken);
        sourceFileLike.ForEachChild(child =>
        {
            child.Parent = sourceFileLike;
            return true;
        });
        sourceFileLike.Loc = nodeOut.Loc;
        return (text, sourceFileLike);
    }

    // trackerimpl.go:161-171 — GetAdjustedRange: computes the adjusted range for a node in a source
    // file, accounting for trivia. (Method on the changeTracker because use of converters.)
    public global::Ts.LsProto.Range GetAdjustedRange(SourceFile sourceFile, Node startNode, Node endNode, LeadingTriviaOption leadingOption, TrailingTriviaOption trailingOption)
    {
        return converters.ToLSPRange(
            sourceFile.AsScript(),
            new TextRange(
                GetAdjustedStartPosition(sourceFile, startNode, leadingOption, false),
                GetAdjustedEndPosition(sourceFile, endNode, trailingOption)));
    }

    // trackerimpl.go:172-233 — getAdjustedStartPosition. (Method on the changeTracker because use
    // of converters.)
    internal int GetAdjustedStartPosition(SourceFile sourceFile, Node node, LeadingTriviaOption leadingOption, bool hasTrailingComment)
    {
        if (leadingOption == LeadingTriviaOption.JSDoc)
        {
            var jsDocComments = global::Ts.Parser.Utilities.GetJSDocCommentRanges(NodeFactory, [], node, sourceFile.Text());
            if (jsDocComments.Count > 0)
            {
                return global::Ts.Format.Util.GetLineStartPositionForPosition(jsDocComments[0].Pos, sourceFile);
            }
        }

        var start = global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false);
        var startOfLinePos = global::Ts.Format.Util.GetLineStartPositionForPosition(start, sourceFile);

        switch (leadingOption)
        {
            case LeadingTriviaOption.Exclude:
                return start;
            case LeadingTriviaOption.StartLine:
                if (node.Loc.ContainsInclusive(startOfLinePos))
                {
                    return startOfLinePos;
                }
                return start;
        }

        var fullStart = node.Pos;
        if (fullStart == start)
        {
            return start;
        }
        var lineStarts = sourceFile.ECMALineMap();
        var fullStartLineIndex = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, fullStart);
        var fullStartLinePos = lineStarts[fullStartLineIndex];
        if (startOfLinePos == fullStartLinePos)
        {
            // full start and start of the node are on the same line
            //   a,     b;
            //    ^     ^
            //    |   start
            // fullstart
            // when b is replaced - we usually want to keep the leading trvia
            // when b is deleted - we delete it
            if (leadingOption == LeadingTriviaOption.IncludeAll)
            {
                return fullStart;
            }
            return start;
        }

        // if node has a trailing comments, use comment end position as the text has already been included.
        if (hasTrailingComment)
        {
            // Check first for leading comments as if the node is the first import, we want to exclude the trivia;
            // otherwise we get the trailing comments.
            var comments = new List<CommentRange>(global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(NodeFactory, sourceFile.Text(), fullStart));
            if (comments.Count == 0)
            {
                comments = new List<CommentRange>(global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(NodeFactory, sourceFile.Text(), fullStart));
            }
            if (comments.Count > 0)
            {
                return global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), comments[0].End, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = true, StopAtComments = true });
            }
        }

        // get start position of the line following the line that contains fullstart position
        // (but only if the fullstart isn't the very beginning of the file)
        var nextLineStart = fullStart > 0 ? 1 : 0;
        var adjustedStartPosition = lineStarts[fullStartLineIndex + nextLineStart];
        // skip whitespaces/newlines
        adjustedStartPosition = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), adjustedStartPosition, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAtComments = true });
        return lineStarts[global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, adjustedStartPosition)];
    }

    // trackerimpl.go:235-261 — getEndPositionOfMultilineTrailingComment: return the end position of
    // a multiline comment of it is on another line; otherwise returns `undefined`. (Method on the
    // changeTracker because of converters.)
    internal int GetEndPositionOfMultilineTrailingComment(SourceFile sourceFile, Node node, TrailingTriviaOption trailingOpt)
    {
        if (trailingOpt == TrailingTriviaOption.Include)
        {
            // If the trailing comment is a multiline comment that extends to the next lines,
            // return the end of the comment and track it for the next nodes to adjust.
            var lineStarts = sourceFile.ECMALineMap();
            var nodeEndLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, node.End);
            foreach (var comment in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(NodeFactory, sourceFile.Text(), node.End))
            {
                // Single line can break the loop as trivia will only be this line.
                // Comments on subsequent lines are also ignored.
                if (comment.Kind == SyntaxKind.SingleLineCommentTrivia || global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, comment.Pos) > nodeEndLine)
                {
                    break;
                }

                // Get the end line of the comment and compare against the end line of the node.
                // If the comment end line position and the multiline comment extends to multiple lines,
                // then is safe to return the end position.
                var commentEndLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineStarts, comment.End);
                if (commentEndLine > nodeEndLine)
                {
                    return global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), comment.End, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = true, StopAtComments = true });
                }
            }
        }

        return 0;
    }

    // trackerimpl.go:263-291 — getAdjustedEndPosition. (Method on the changeTracker because of
    // converters.)
    internal int GetAdjustedEndPosition(SourceFile sourceFile, Node node, TrailingTriviaOption trailingTriviaOption)
    {
        if (trailingTriviaOption == TrailingTriviaOption.Exclude)
        {
            return node.End;
        }
        if (trailingTriviaOption == TrailingTriviaOption.ExcludeWhitespace)
        {
            var comments = new List<CommentRange>(global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(NodeFactory, sourceFile.Text(), node.End));
            comments.AddRange(global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(NodeFactory, sourceFile.Text(), node.End));
            if (comments.Count > 0)
            {
                var realEnd = comments[^1].End;
                if (realEnd != 0)
                {
                    return realEnd;
                }
            }
            return node.End;
        }

        var multilineEndPosition = GetEndPositionOfMultilineTrailingComment(sourceFile, node, trailingTriviaOption);
        if (multilineEndPosition != 0)
        {
            return multilineEndPosition;
        }

        var newEnd = global::Ts.Scanner.Scanner.SkipTriviaEx(sourceFile.Text(), node.End, new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = true });

        if (newEnd != node.End && (trailingTriviaOption == TrailingTriviaOption.Include || global::Ts.StringUtil.StringUtil.IsLineBreak(sourceFile.Text()[newEnd - 1])))
        {
            return newEnd;
        }
        return node.End;
    }

    // ============= utilities =============
}

internal static class TrackerImplHelpers
{
    // trackerimpl.go:293-300 — hasCommentsBeforeLineBreak.
    internal static bool HasCommentsBeforeLineBreak(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                return ch == '/';
            }
        }
        return false;
    }

    // trackerimpl.go:302-308 — needSemicolonBetween.
    internal static bool NeedSemicolonBetween(Node a, Node b)
    {
        return (NodeTests.IsPropertySignatureDeclaration(a) || NodeTests.IsPropertyDeclaration(a)) &&
            AstUtilities.IsClassOrTypeElement(b) &&
            b.Name()!.Kind == SyntaxKind.ComputedPropertyName ||
            AstUtilities.IsStatementButNotDeclaration(a) &&
                AstUtilities.IsStatementButNotDeclaration(b); // TODO: only if b would start with a `(` or `[`
    }
}

public sealed partial class Tracker
{
    // trackerimpl.go:310-402 — getInsertionPositionAtSourceFileTop.
    internal int GetInsertionPositionAtSourceFileTop(SourceFile sourceFile)
    {
        Node? lastPrologue = null;
        foreach (var node in sourceFile.Statements!.Nodes)
        {
            if (AstUtilities.IsPrologueDirective(node))
            {
                lastPrologue = node;
            }
            else
            {
                break;
            }
        }

        var position = 0;
        var text = sourceFile.Text();
        void AdvancePastLineBreak()
        {
            if (position >= text.Length)
            {
                return;
            }
            var lineBreakChar = text[position];
            if (global::Ts.StringUtil.StringUtil.IsLineBreak(lineBreakChar))
            {
                position++;
                if (position < text.Length && lineBreakChar == '\r' && text[position] == '\n')
                {
                    position++;
                }
            }
        }
        if (lastPrologue != null)
        {
            position = lastPrologue.End;
            AdvancePastLineBreak();
            return position;
        }

        var shebang = global::Ts.Scanner.Scanner.GetShebang(text);
        if (shebang.Length != 0)
        {
            position = shebang.Length;
            AdvancePastLineBreak();
        }

        var ranges = new List<CommentRange>(global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(NodeFactory, text, position));
        if (ranges.Count == 0)
        {
            return position;
        }
        // Find the first attached comment to the first node and add before it
        CommentRange? lastComment = null;
        var pinnedOrTripleSlash = false;
        var firstNodeLine = -1;

        var lenStatements = sourceFile.Statements.Nodes.Length;
        var lineMap = sourceFile.ECMALineMap();
        foreach (var r in ranges)
        {
            if (r.Kind == SyntaxKind.MultiLineCommentTrivia)
            {
                if (global::Ts.Printer.PrinterUtilities.IsPinnedComment(text, r))
                {
                    lastComment = r;
                    pinnedOrTripleSlash = true;
                    continue;
                }
            }
            else if (global::Ts.Printer.PrinterUtilities.IsRecognizedTripleSlashComment(text, r))
            {
                lastComment = r;
                pinnedOrTripleSlash = true;
                continue;
            }

            if (lastComment != null)
            {
                // Always insert after pinned or triple slash comments
                if (pinnedOrTripleSlash)
                {
                    break;
                }

                // There was a blank line between the last comment and this comment.
                // This comment is not part of the copyright comments
                var commentLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineMap, r.Pos);
                var lastCommentEndLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineMap, lastComment.Value.End);
                if (commentLine >= lastCommentEndLine + 2)
                {
                    break;
                }
            }

            if (lenStatements > 0)
            {
                if (firstNodeLine == -1)
                {
                    firstNodeLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineMap, global::Ts.AstNav.Tokens.GetStartOfNode(sourceFile.Statements.Nodes[0], sourceFile, false));
                }
                var commentEndLine = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineMap, r.End);
                if (firstNodeLine < commentEndLine + 2)
                {
                    break;
                }
            }
            lastComment = r;
            pinnedOrTripleSlash = false;
        }

        if (lastComment != null)
        {
            position = lastComment.Value.End;
            AdvancePastLineBreak();
        }
        return position;
    }
}
