// Ported from tsgo internal/format/span.go

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Format;

internal static class Span
{
    /** find node that fully contains given text range */
    // Go: func findEnclosingNode(r core.TextRange, sourceFile *ast.SourceFile) *ast.Node
    internal static Node FindEnclosingNode(TextRange r, SourceFile sourceFile)
    {
        Node Find(Node n)
        {
            Node? candidate = null;
            n.ForEachChild(c =>
            {
                if ((c.Flags & NodeFlags.Reparsed) != 0)
                {
                    return false;
                }
                if (r.ContainedBy(FormattingContext.WithTokenStart(c, sourceFile)))
                {
                    candidate = c;
                    return true;
                }
                return false;
            });
            if (candidate is not null)
            {
                Node result = Find(candidate);
                // Go returns the deeper result if non-nil; Find never returns nil here.
                return result;
            }

            return n;
        }
        return Find(sourceFile.AsNode());
    }

    /**
     * Start of the original range might fall inside the comment - scanner will not yield appropriate results...
     */
    // Go: func getScanStartPosition(enclosingNode, originalRange, sourceFile) int
    internal static int GetScanStartPosition(Node enclosingNode, TextRange originalRange, SourceFile sourceFile)
    {
        TextRange adjusted = FormattingContext.WithTokenStart(enclosingNode, sourceFile);
        int start = adjusted.Pos;
        if (start == originalRange.Pos && enclosingNode.End == originalRange.End)
        {
            return start;
        }

        Node? precedingToken = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, originalRange.Pos);
        if (precedingToken is null)
        {
            // no preceding token found - start from the beginning of enclosing node
            return enclosingNode.Pos;
        }

        // preceding token ends after the start of original range
        if (precedingToken.End >= originalRange.Pos)
        {
            return enclosingNode.Pos;
        }

        return precedingToken.End;
    }

    /*
     * getOwnOrInheritedDelta — see span.go comment on binary-expression indentation.
     */
    // Go: func getOwnOrInheritedDelta(n *ast.Node, options lsutil.FormatCodeSettings, sourceFile *ast.SourceFile) int
    internal static int GetOwnOrInheritedDelta(Node? n, FormatCodeSettings options, SourceFile sourceFile)
    {
        int previousLine = -1;
        Node? child = null;
        while (n is not null)
        {
            int line = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, FormattingContext.WithTokenStart(n, sourceFile).Pos);
            if (previousLine != -1 && line != previousLine)
            {
                break;
            }

            if (Indent.ShouldIndentChildNode(options, n, child, sourceFile))
            {
                return options.IndentSize; // !!! nil check???
            }

            previousLine = line;
            child = n;
            n = n.Parent;
        }
        return 0;
    }

    // Go: func rangeHasNoErrors(_ core.TextRange) bool
    internal static bool RangeHasNoErrors(TextRange _) => false;

    // Go: func prepareRangeContainsErrorFunction(errors []*ast.Diagnostic, originalRange core.TextRange) func(r core.TextRange) bool
    internal static Func<TextRange, bool> PrepareRangeContainsErrorFunction(Diagnostic[]? errors, TextRange originalRange)
    {
        if (errors is null || errors.Length == 0)
        {
            return RangeHasNoErrors;
        }

        // pick only errors that fall in range
        var sorted = global::Ts.Core.CoreUtil.Filter(errors, d => originalRange.Overlaps(d.Loc()));
        if (sorted is null || sorted.Length == 0)
        {
            return RangeHasNoErrors;
        }
        // slices.SortStableFunc(sorted, (a, b) => a.Pos() - b.Pos())
        Array.Sort(sorted, (a, b) => a.Pos() - b.Pos());

        int index = 0;
        return r =>
        {
            // in current implementation sequence of arguments [r1, r2...] is monotonically increasing.
            for (; ; )
            {
                if (index >= sorted.Length)
                {
                    return false;
                }

                Diagnostic err = sorted[index];

                if (r.End <= err.Pos())
                {
                    return false;
                }

                if (r.Overlaps(err.Loc()))
                {
                    return true;
                }

                index++;
            }
        };
    }

    // Go: func getNonDecoratorTokenPosOfNode(node *ast.Node, file *ast.SourceFile) int
    internal static int GetNonDecoratorTokenPosOfNode(Node node, SourceFile? file)
    {
        Node? lastDecorator = null;
        if (global::Ts.Ast.AstUtilities.HasDecorators(node))
        {
            lastDecorator = global::Ts.Core.CoreUtil.FindLast(node.ModifierNodes(), global::Ts.Ast.NodeTests.IsDecorator);
        }
        file ??= global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node)!;
        if (lastDecorator is null)
        {
            return FormattingContext.WithTokenStart(node, file).Pos;
        }
        return global::Ts.Scanner.Scanner.SkipTrivia(file.Text(), lastDecorator.End);
    }

    // Go: func newFormatSpanWorker(ctx, originalRange, enclosingNode, initialIndentation, delta, requestKind, rangeContainsError, sourceFile) *formatSpanWorker
    internal static FormatSpanWorker NewFormatSpanWorker(
        FormatContext ctx,
        TextRange originalRange,
        Node enclosingNode,
        int initialIndentation,
        int delta,
        FormatRequestKind requestKind,
        Func<TextRange, bool> rangeContainsError,
        SourceFile sourceFile)
    {
        return new FormatSpanWorker(ctx, originalRange, enclosingNode, initialIndentation, delta, requestKind, rangeContainsError, sourceFile);
    }
}

// Go: type formatSpanWorker struct { ... }
public sealed class FormatSpanWorker
{
    private readonly FormatContext ctx;
    private readonly TextRange originalRange;
    private readonly Node enclosingNode;
    private readonly int initialIndentation;
    private readonly int delta;
    private readonly FormatRequestKind requestKind;
    private readonly Func<TextRange, bool> rangeContainsError;
    private readonly SourceFile sourceFile;

    private FormattingScanner formattingScanner = null!;
    private FormattingContext formattingContext = null!;

    private readonly List<TextChange> edits = new();
    private TextRangeWithKind previousRange;
    private int previousRangeTriviaEnd;
    private Node? previousParent;
    private int previousRangeStartLine;

    private Node? childContextNode;
    private int lastIndentedLine;
    private int indentationOnLastIndentedLine;

    private global::Ts.Ast.NodeVisitor visitor = null!;
    private Node? visitingNode;
    private DynamicIndenter? visitingIndenter;
    private int visitingNodeStartLine;
    private int visitingUndecoratedNodeStartLine;

    private readonly List<RuleImpl> currentRules = new(32);

    internal FormatSpanWorker(
        FormatContext ctx,
        TextRange originalRange,
        Node enclosingNode,
        int initialIndentation,
        int delta,
        FormatRequestKind requestKind,
        Func<TextRange, bool> rangeContainsError,
        SourceFile sourceFile)
    {
        this.ctx = ctx;
        this.originalRange = originalRange;
        this.enclosingNode = enclosingNode;
        this.initialIndentation = initialIndentation;
        this.delta = delta;
        this.requestKind = requestKind;
        this.rangeContainsError = rangeContainsError;
        this.sourceFile = sourceFile;
    }

    internal TextChange[] Execute(FormattingScanner s)
    {
        formattingScanner = s;
        indentationOnLastIndentedLine = -1;
        lastIndentedLine = -1;
        FormatCodeSettings options = ctx.GetFormatCodeSettings();
        formattingContext = new FormattingContext(sourceFile, requestKind, options);

        visitor = new global::Ts.Ast.NodeVisitor(
            child =>
            {
                ProcessChildNode(
                    visitingNode!,
                    visitingIndenter!,
                    visitingNodeStartLine,
                    visitingUndecoratedNodeStartLine,
                    child,
                    -1,
                    visitingNode!,
                    visitingIndenter!,
                    visitingNodeStartLine,
                    visitingUndecoratedNodeStartLine,
                    false,
                    false);
                return child;
            },
            new global::Ts.Ast.NodeFactory(),
            new global::Ts.Ast.NodeVisitorHooks
            {
                VisitNodes = (nodes, _) =>
                {
                    if (nodes is not null)
                    {
                        ProcessChildNodes(
                            visitingNode!,
                            visitingIndenter!,
                            visitingNodeStartLine,
                            visitingUndecoratedNodeStartLine,
                            nodes,
                            visitingNode!,
                            visitingNodeStartLine,
                            visitingIndenter!);
                    }
                    return nodes;
                },
            });

        formattingScanner.Advance();

        if (formattingScanner.IsOnToken())
        {
            int startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(
                sourceFile,
                FormattingContext.WithTokenStart(enclosingNode, sourceFile).Pos);
            int undecoratedStartLine = startLine;
            if (global::Ts.Ast.AstUtilities.HasDecorators(enclosingNode))
            {
                undecoratedStartLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(
                    sourceFile,
                    Span.GetNonDecoratorTokenPosOfNode(enclosingNode, sourceFile));
            }

            ProcessNode(enclosingNode, enclosingNode, startLine, undecoratedStartLine, initialIndentation, delta);
        }

        // Leading trivia items get attached to and processed with the token that proceeds them. If the
        // range ends in the middle of some leading trivia, the token that proceeds them won't be in the
        // range and thus won't get processed. So we process those remaining trivia items here.
        List<TextRangeWithKind>? remainingTrivia = formattingScanner.GetCurrentLeadingTrivia();
        if (remainingTrivia is not null && remainingTrivia.Count > 0)
        {
            int indentation = initialIndentation;
            if (Indent.NodeWillIndentChild(formattingContext.Options, enclosingNode, null, sourceFile, false))
            {
                indentation += options.IndentSize; // !!! TODO: nil check???
            }

            IndentTriviaItems(remainingTrivia, indentation, true, item =>
            {
                (int startLine, int startCharacter) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, item.Loc.Pos);
                ProcessRange(item, startLine, startCharacter, enclosingNode, enclosingNode, null);
                InsertIndentation(item.Loc.Pos, indentation, false);
            });

            if (options.TrimTrailingWhitespace.IsTrue())
            {
                TrimTrailingWhitespacesForRemainingRange(remainingTrivia);
            }
        }

        if (!IsZero(previousRange) && formattingScanner.GetTokenFullStart() >= originalRange.End)
        {
            TextRangeWithKind tokenInfo = default;
            if (formattingScanner.IsOnEof())
            {
                tokenInfo = formattingScanner.ReadEofTokenRange();
            }
            else if (formattingScanner.IsOnToken())
            {
                tokenInfo = formattingScanner.ReadTokenInfo(enclosingNode).Token;
            }

            if (tokenInfo.Loc.Pos == previousRangeTriviaEnd)
            {
                Node? parent = global::Ts.AstNav.Tokens.FindPrecedingToken(sourceFile, tokenInfo.Loc.End)?.Parent;
                parent ??= previousParent;
                int line = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, tokenInfo.Loc.Pos);
                ProcessPair(
                    tokenInfo,
                    line,
                    parent!,
                    previousRange,
                    previousRangeStartLine,
                    previousParent!,
                    parent!,
                    null);
            }
        }

        return edits.ToArray();
    }

    private int ProcessChildNode(
        Node node,
        DynamicIndenter indenter,
        int nodeStartLine,
        int undecoratedNodeStartLine,
        Node child,
        int inheritedIndentation,
        Node parent,
        DynamicIndenter parentDynamicIndentation,
        int parentStartLine,
        int undecoratedParentStartLine,
        bool isListItem,
        bool isFirstListItem)
    {
        global::Ts.Core.Debug.Assert(!global::Ts.Ast.AstUtilities.NodeIsSynthesized(child));

        if (global::Ts.Ast.AstUtilities.NodeIsMissing(child) || Util.IsGrammarError(parent, child) || (child.Flags & NodeFlags.Reparsed) != 0)
        {
            return inheritedIndentation;
        }

        int childStartPos = global::Ts.Scanner.Scanner.GetTokenPosOfNode(child, sourceFile, false);
        int childStartLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, childStartPos);

        int undecoratedChildStartLine = childStartLine;
        if (global::Ts.Ast.AstUtilities.HasDecorators(child))
        {
            undecoratedChildStartLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(
                sourceFile,
                Span.GetNonDecoratorTokenPosOfNode(child, sourceFile));
        }

        int childIndentationAmount = -1;
        if (isListItem && parent.Loc.ContainedBy(originalRange))
        {
            childIndentationAmount = TryComputeIndentationForListItem(
                childStartPos,
                child.End,
                parentStartLine,
                originalRange,
                inheritedIndentation);
            if (childIndentationAmount != -1)
            {
                inheritedIndentation = childIndentationAmount;
            }
        }

        if (!originalRange.Overlaps(child.Loc))
        {
            if (child.End < originalRange.Pos)
            {
                formattingScanner.SkipToEndOf(child.Loc);
            }
            return inheritedIndentation;
        }

        if (child.Loc.Len == 0)
        {
            return inheritedIndentation;
        }

        while (formattingScanner.IsOnToken() && formattingScanner.GetTokenFullStart() < originalRange.End)
        {
            TokenInfo tokenInfo = formattingScanner.ReadTokenInfo(node);
            if (tokenInfo.Token.Loc.End > originalRange.End)
            {
                return inheritedIndentation;
            }
            if (tokenInfo.Token.Loc.End > childStartPos)
            {
                if (tokenInfo.Token.Loc.Pos > childStartPos)
                {
                    formattingScanner.SkipToStartOf(child.Loc);
                }
                break;
            }

            ConsumeTokenAndAdvanceScanner(tokenInfo, node, parentDynamicIndentation, node, false);
        }

        if (!formattingScanner.IsOnToken() || formattingScanner.GetTokenFullStart() >= originalRange.End)
        {
            return inheritedIndentation;
        }

        if (global::Ts.Ast.NodeTests.IsTokenKind(child.Kind))
        {
            TokenInfo tokenInfo = formattingScanner.ReadTokenInfo(child);
            if (child.Kind != SyntaxKind.JsxText)
            {
                global::Ts.Core.Debug.Assert(tokenInfo.Token.Loc.End == child.Loc.End, "Token end is child end");
                ConsumeTokenAndAdvanceScanner(tokenInfo, node, parentDynamicIndentation, child, false);
                return inheritedIndentation;
            }
        }

        int effectiveParentStartLine = undecoratedParentStartLine;
        if (child.Kind == SyntaxKind.Decorator)
        {
            effectiveParentStartLine = childStartLine;
        }
        (int childIndentation, int childDelta) = ComputeIndentation(
            child,
            childStartLine,
            childIndentationAmount,
            node,
            parentDynamicIndentation,
            effectiveParentStartLine);

        ProcessNode(child, childContextNode!, childStartLine, undecoratedChildStartLine, childIndentation, childDelta);

        childContextNode = node;

        if (isFirstListItem && parent.Kind == SyntaxKind.ArrayLiteralExpression && inheritedIndentation == -1)
        {
            inheritedIndentation = childIndentation;
        }

        return inheritedIndentation;
    }

    private void ProcessChildNodes(
        Node node,
        DynamicIndenter indenter,
        int nodeStartLine,
        int undecoratedNodeStartLine,
        NodeList nodes,
        Node parent,
        int parentStartLine,
        DynamicIndenter parentDynamicIndentation)
    {
        global::Ts.Core.Debug.Assert(!global::Ts.Ast.AstUtilities.PositionIsSynthesized(nodes.Pos));
        global::Ts.Core.Debug.Assert(!global::Ts.Ast.AstUtilities.PositionIsSynthesized(nodes.End));

        SyntaxKind listStartToken = Util.GetOpenTokenForList(parent, nodes);
        DynamicIndenter listDynamicIndentation = parentDynamicIndentation;
        int startLine = parentStartLine;

        if (!originalRange.Overlaps(nodes.Loc))
        {
            if (nodes.End < originalRange.Pos &&
                (nodes.Nodes.Length == 0 || (nodes.Nodes[0].Flags & NodeFlags.Reparsed) == 0))
            {
                formattingScanner.SkipToEndOf(nodes.Loc);
            }
            return;
        }

        if (listStartToken != SyntaxKind.Unknown)
        {
            while (formattingScanner.IsOnToken() && formattingScanner.GetTokenFullStart() < originalRange.End)
            {
                TokenInfo tokenInfo = formattingScanner.ReadTokenInfo(parent);
                if (tokenInfo.Token.Loc.End > nodes.Pos)
                {
                    break;
                }
                if (tokenInfo.Token.Kind == listStartToken)
                {
                    startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, tokenInfo.Token.Loc.Pos);
                    ConsumeTokenAndAdvanceScanner(tokenInfo, parent, parentDynamicIndentation, parent, false);

                    int indentationOnListStartToken;
                    if (indentationOnLastIndentedLine != -1)
                    {
                        indentationOnListStartToken = indentationOnLastIndentedLine;
                    }
                    else
                    {
                        int startLinePosition = Util.GetLineStartPositionForPosition(tokenInfo.Token.Loc.Pos, sourceFile);
                        indentationOnListStartToken = Indent.FindFirstNonWhitespaceColumn(
                            startLinePosition,
                            tokenInfo.Token.Loc.Pos,
                            sourceFile,
                            formattingContext.Options);
                    }

                    listDynamicIndentation = GetDynamicIndentation(
                        parent,
                        parentStartLine,
                        indentationOnListStartToken,
                        formattingContext.Options.IndentSize);
                }
                else
                {
                    ConsumeTokenAndAdvanceScanner(tokenInfo, parent, parentDynamicIndentation, parent, false);
                }
            }
        }

        int inheritedIndentation = -1;
        for (int i = 0; i < nodes.Nodes.Length; i++)
        {
            Node child = nodes.Nodes[i];
            inheritedIndentation = ProcessChildNode(
                node,
                indenter,
                nodeStartLine,
                undecoratedNodeStartLine,
                child,
                inheritedIndentation,
                node,
                listDynamicIndentation,
                startLine,
                startLine,
                true,
                i == 0);
        }

        SyntaxKind listEndToken = Util.GetCloseTokenForOpenToken(listStartToken);
        if (listEndToken != SyntaxKind.Unknown && formattingScanner.IsOnToken() && formattingScanner.GetTokenFullStart() < originalRange.End)
        {
            TokenInfo tokenInfo = formattingScanner.ReadTokenInfo(parent);
            if (tokenInfo.Token.Kind == SyntaxKind.CommaToken)
            {
                ConsumeTokenAndAdvanceScanner(tokenInfo, parent, listDynamicIndentation, parent, false);
                if (formattingScanner.IsOnToken())
                {
                    tokenInfo = formattingScanner.ReadTokenInfo(parent);
                }
                else
                {
                    return;
                }
            }

            if (tokenInfo.Token.Kind == listEndToken && tokenInfo.Token.Loc.ContainedBy(parent.Loc))
            {
                ConsumeTokenAndAdvanceScanner(tokenInfo, parent, listDynamicIndentation, parent, true);
            }
        }
    }

    private void ExecuteProcessNodeVisitor(Node node, DynamicIndenter indenter, int nodeStartLine, int undecoratedNodeStartLine)
    {
        Node? oldNode = visitingNode;
        DynamicIndenter? oldIndenter = visitingIndenter;
        int oldStart = visitingNodeStartLine;
        int oldUndecoratedStart = visitingUndecoratedNodeStartLine;
        visitingNode = node;
        visitingIndenter = indenter;
        visitingNodeStartLine = nodeStartLine;
        visitingUndecoratedNodeStartLine = undecoratedNodeStartLine;
        node.VisitEachChild(visitor);
        visitingNode = oldNode;
        visitingIndenter = oldIndenter;
        visitingNodeStartLine = oldStart;
        visitingUndecoratedNodeStartLine = oldUndecoratedStart;
    }

    private (int Indentation, int Delta) ComputeIndentation(
        Node node,
        int startLine,
        int inheritedIndentation,
        Node parent,
        DynamicIndenter parentDynamicIndentation,
        int effectiveParentStartLine)
    {
        int childDelta = Indent.ShouldIndentChildNode(formattingContext.Options, node, null, null)
            ? formattingContext.Options.IndentSize
            : 0;

        if (effectiveParentStartLine == startLine)
        {
            int indentation = indentationOnLastIndentedLine;
            if (startLine != lastIndentedLine)
            {
                indentation = parentDynamicIndentation.GetIndentation();
            }
            childDelta = Math.Min(
                formattingContext.Options.IndentSize,
                parentDynamicIndentation.GetDelta(node) + childDelta);
            return (indentation, childDelta);
        }
        if (inheritedIndentation == -1)
        {
            if (node.Kind == SyntaxKind.OpenParenToken && startLine == lastIndentedLine)
            {
                return (indentationOnLastIndentedLine, parentDynamicIndentation.GetDelta(node));
            }
            if (Indent.ChildStartsOnTheSameLineWithElseInIfStatement(parent, node, startLine, sourceFile) ||
                Indent.ChildIsUnindentedBranchOfConditionalExpression(parent, node, startLine, sourceFile) ||
                Indent.ArgumentStartsOnSameLineAsPreviousArgument(parent, node, startLine, sourceFile))
            {
                return (parentDynamicIndentation.GetIndentation(), childDelta);
            }

            int indentation = parentDynamicIndentation.GetIndentation();
            if (indentation == -1)
            {
                return (indentation, childDelta);
            }
            return (indentation + parentDynamicIndentation.GetDelta(node), childDelta);
        }

        return (inheritedIndentation, childDelta);
    }

    private int TryComputeIndentationForListItem(
        int startPos,
        int endPos,
        int parentStartLine,
        TextRange range,
        int inheritedIndentation)
    {
        var itemRange = new TextRange(startPos, endPos);
        if (range.Overlaps(itemRange) || itemRange.ContainedBy(range))
        {
            if (inheritedIndentation != -1)
            {
                return inheritedIndentation;
            }
        }
        else
        {
            int startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, startPos);
            int startLinePosition = Util.GetLineStartPositionForPosition(startPos, sourceFile);
            int column = Indent.FindFirstNonWhitespaceColumn(startLinePosition, startPos, sourceFile, formattingContext.Options);
            if (startLine != parentStartLine || startPos == column)
            {
                int baseIndentSize = formattingContext.Options.BaseIndentSize;
                return baseIndentSize > column ? baseIndentSize : column;
            }
        }
        return -1;
    }

    private void ProcessNode(
        Node node,
        Node contextNode,
        int nodeStartLine,
        int undecoratedNodeStartLine,
        int indentation,
        int childDelta)
    {
        if (!originalRange.Overlaps(FormattingContext.WithTokenStart(node, sourceFile)))
        {
            return;
        }

        DynamicIndenter nodeDynamicIndentation = GetDynamicIndentation(node, nodeStartLine, indentation, childDelta);
        childContextNode = contextNode;
        ExecuteProcessNodeVisitor(node, nodeDynamicIndentation, nodeStartLine, undecoratedNodeStartLine);

        while (formattingScanner.IsOnToken() && formattingScanner.GetTokenFullStart() < originalRange.End)
        {
            TokenInfo tokenInfo = formattingScanner.ReadTokenInfo(node);
            if (tokenInfo.Token.Loc.End > Math.Min(node.End, originalRange.End))
            {
                break;
            }
            ConsumeTokenAndAdvanceScanner(tokenInfo, node, nodeDynamicIndentation, node, false);
        }
    }

    private LineAction ProcessPair(
        TextRangeWithKind currentItem,
        int currentStartLine,
        Node currentParent,
        TextRangeWithKind previousItem,
        int previousStartLine,
        Node previousItemParent,
        Node contextNode,
        DynamicIndenter? dynamicIndentation)
    {
        formattingContext.UpdateContext(previousItem, previousItemParent, currentItem, currentParent, contextNode);

        currentRules.Clear();
        RulesMap.GetRules(formattingContext, currentRules);

        bool trimTrailingWhitespaces = !formattingContext.Options.TrimTrailingWhitespace.IsFalse();
        LineAction lineAction = LineAction.None;

        if (currentRules.Count > 0)
        {
            for (int i = currentRules.Count - 1; i >= 0; i--)
            {
                RuleImpl rule = currentRules[i];
                lineAction = ApplyRuleEdits(rule, previousItem, previousStartLine, currentItem, currentStartLine);
                if (dynamicIndentation is not null)
                {
                    switch (lineAction)
                    {
                        case LineAction.LineRemoved:
                            if (global::Ts.Scanner.Scanner.GetTokenPosOfNode(currentParent, sourceFile, false) == currentItem.Loc.Pos)
                            {
                                dynamicIndentation.RecomputeIndentation(false, contextNode);
                            }
                            break;
                        case LineAction.LineAdded:
                            if (global::Ts.Scanner.Scanner.GetTokenPosOfNode(currentParent, sourceFile, false) == currentItem.Loc.Pos)
                            {
                                dynamicIndentation.RecomputeIndentation(true, contextNode);
                            }
                            break;
                        default:
                            global::Ts.Core.Debug.Assert(lineAction == LineAction.None);
                            break;
                    }
                }

                trimTrailingWhitespaces = trimTrailingWhitespaces &&
                    (rule.ActionValue() & RuleAction.DeleteSpace) == 0 &&
                    rule.FlagsValue() != RuleFlags.CanDeleteNewLines;
            }
        }
        else
        {
            trimTrailingWhitespaces = trimTrailingWhitespaces && currentItem.Kind != SyntaxKind.EndOfFile;
        }

        if (currentStartLine != previousStartLine && trimTrailingWhitespaces)
        {
            TrimTrailingWhitespacesForLines(previousStartLine, currentStartLine, previousItem);
        }

        return lineAction;
    }

    private LineAction ApplyRuleEdits(
        RuleImpl rule,
        TextRangeWithKind previousRange,
        int previousStartLine,
        TextRangeWithKind currentRange,
        int currentStartLine)
    {
        bool onLaterLine = currentStartLine != previousStartLine;
        switch (rule.ActionValue())
        {
            case RuleAction.StopProcessingSpaceActions:
                return LineAction.None;
            case RuleAction.DeleteSpace:
                if (previousRange.Loc.End != currentRange.Loc.Pos)
                {
                    RecordDelete(previousRange.Loc.End, currentRange.Loc.Pos - previousRange.Loc.End);
                    return onLaterLine ? LineAction.LineRemoved : LineAction.None;
                }
                break;
            case RuleAction.DeleteToken:
                RecordDelete(previousRange.Loc.Pos, previousRange.Loc.Len);
                break;
            case RuleAction.InsertNewLine:
                if (rule.FlagsValue() != RuleFlags.CanDeleteNewLines && previousStartLine != currentStartLine)
                {
                    return LineAction.None;
                }

                int lineDelta = currentStartLine - previousStartLine;
                if (lineDelta != 1)
                {
                    RecordReplace(
                        previousRange.Loc.End,
                        currentRange.Loc.Pos - previousRange.Loc.End,
                        ctx.GetNewLineOrDefault());
                    return onLaterLine ? LineAction.None : LineAction.LineAdded;
                }
                break;
            case RuleAction.InsertSpace:
                if (rule.FlagsValue() != RuleFlags.CanDeleteNewLines && previousStartLine != currentStartLine)
                {
                    return LineAction.None;
                }

                int positionDelta = currentRange.Loc.Pos - previousRange.Loc.End;
                bool alreadySingleSpace = positionDelta == 1 &&
                    previousRange.Loc.End < sourceFile.Text().Length &&
                    sourceFile.Text()[previousRange.Loc.End] == ' ';
                if (!alreadySingleSpace)
                {
                    RecordReplace(previousRange.Loc.End, positionDelta, " ");
                    return onLaterLine ? LineAction.LineRemoved : LineAction.None;
                }
                break;
            case RuleAction.InsertTrailingSemicolon:
                RecordInsert(previousRange.Loc.End, ";");
                break;
        }
        return LineAction.None;
    }

    private LineAction ProcessRange(
        TextRangeWithKind range,
        int rangeStartLine,
        int rangeStartCharacter,
        Node parent,
        Node contextNode,
        DynamicIndenter? dynamicIndentation)
    {
        _ = rangeStartCharacter;
        bool rangeHasError = rangeContainsError(range.Loc);
        LineAction lineAction = LineAction.None;
        if (!rangeHasError)
        {
            if (IsZero(previousRange))
            {
                int originalStartLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, originalRange.Pos);
                TrimTrailingWhitespacesForLines(originalStartLine, rangeStartLine, default);
            }
            else
            {
                lineAction = ProcessPair(
                    range,
                    rangeStartLine,
                    parent,
                    previousRange,
                    previousRangeStartLine,
                    previousParent!,
                    contextNode,
                    dynamicIndentation);
            }
        }

        previousRange = range;
        previousRangeTriviaEnd = range.Loc.End;
        previousParent = parent;
        previousRangeStartLine = rangeStartLine;

        return lineAction;
    }

    private void ProcessTrivia(
        List<TextRangeWithKind> trivia,
        Node parent,
        Node contextNode,
        DynamicIndenter dynamicIndentation)
    {
        foreach (TextRangeWithKind triviaItem in trivia)
        {
            if (IsComment(triviaItem.Kind) && triviaItem.Loc.ContainedBy(originalRange))
            {
                (int triviaItemStartLine, int triviaItemStartCharacter) =
                    global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, triviaItem.Loc.Pos);
                ProcessRange(
                    triviaItem,
                    triviaItemStartLine,
                    triviaItemStartCharacter,
                    parent,
                    contextNode,
                    dynamicIndentation);
            }
        }
    }

    private void TrimTrailingWhitespacesForRemainingRange(List<TextRangeWithKind> trivias)
    {
        int startPos = originalRange.Pos;
        if (!IsZero(previousRange))
        {
            startPos = previousRange.Loc.End;
        }

        foreach (TextRangeWithKind trivia in trivias)
        {
            if (IsComment(trivia.Kind))
            {
                if (startPos < trivia.Loc.Pos)
                {
                    TrimTrailingWhitespacesForPositions(startPos, trivia.Loc.Pos - 1, previousRange);
                }

                startPos = trivia.Loc.End + 1;
            }
        }

        if (startPos < originalRange.End)
        {
            TrimTrailingWhitespacesForPositions(startPos, originalRange.End, previousRange);
        }
    }

    private void TrimTrailingWhitespacesForPositions(int startPos, int endPos, TextRangeWithKind previous)
    {
        int startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, startPos);
        int endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, endPos);
        TrimTrailingWhitespacesForLines(startLine, endLine + 1, previous);
    }

    private void TrimTrailingWhitespacesForLines(int line1, int line2, TextRangeWithKind range)
    {
        int[] lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile);
        int upperLine = Math.Min(line2, lineStarts.Length);
        for (int line = line1; line < upperLine; line++)
        {
            int lineStartPosition = lineStarts[line];
            int lineEndPosition = global::Ts.Scanner.Scanner.GetECMAEndLinePosition(sourceFile, line);

            if (!IsZero(range) &&
                (IsComment(range.Kind) || IsStringOrRegularExpressionOrTemplateLiteral(range.Kind)) &&
                range.Loc.Pos <= lineEndPosition &&
                range.Loc.End > lineEndPosition)
            {
                continue;
            }

            int whitespaceStart = GetTrailingWhitespaceStartPosition(lineStartPosition, lineEndPosition);
            if (whitespaceStart != -1)
            {
                if (whitespaceStart != lineStartPosition)
                {
                    global::Ts.Core.Debug.Assert(
                        !global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(sourceFile.Text()[whitespaceStart - 1]));
                }
                RecordDelete(whitespaceStart, lineEndPosition + 1 - whitespaceStart);
            }
        }
    }

    /**
    * @param start The position of the first character in range
    * @param end The position of the last character in range
    */
    private int GetTrailingWhitespaceStartPosition(int start, int end)
    {
        int pos = Math.Min(end, sourceFile.Text().Length - 1);
        string text = sourceFile.Text();
        while (pos >= start)
        {
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(text[pos]))
            {
                break;
            }
            pos--;
        }
        return pos != Math.Min(end, text.Length - 1) ? pos + 1 : -1;
    }

    private static bool IsStringOrRegularExpressionOrTemplateLiteral(SyntaxKind kind)
    {
        return kind == SyntaxKind.StringLiteral ||
            kind == SyntaxKind.RegularExpressionLiteral ||
            (SyntaxKind.FirstTemplateToken <= kind && kind <= SyntaxKind.LastTemplateToken);
    }

    private static bool IsComment(SyntaxKind kind)
    {
        return kind == SyntaxKind.SingleLineCommentTrivia || kind == SyntaxKind.MultiLineCommentTrivia;
    }

    private void InsertIndentation(int pos, int indentation, bool lineAdded)
    {
        string indentationString = GetIndentationString(indentation, formattingContext.Options);
        if (lineAdded)
        {
            RecordReplace(pos, 0, indentationString);
        }
        else
        {
            (int tokenStartLine, int tokenStartCharacter) =
                global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, pos);
            int startLinePosition = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile)[tokenStartLine];
            if (indentation != CharacterToColumn(startLinePosition, tokenStartCharacter) ||
                IndentationIsDifferent(indentationString, startLinePosition))
            {
                RecordReplace(startLinePosition, tokenStartCharacter, indentationString);
            }
        }
    }

    private int CharacterToColumn(int startLinePosition, int characterInLine)
    {
        int column = 0;
        for (int i = 0; i < characterInLine; i++)
        {
            if (sourceFile.Text()[startLinePosition + i] == '\t')
            {
                if (formattingContext.Options.TabSize > 0)
                {
                    column += formattingContext.Options.TabSize - (column % formattingContext.Options.TabSize);
                }
            }
            else
            {
                column++;
            }
        }
        return column;
    }

    private bool IndentationIsDifferent(string indentationString, int startLinePosition)
    {
        string text = sourceFile.Text();
        int end = startLinePosition + indentationString.Length;
        return end > text.Length ||
            !text.AsSpan(startLinePosition, indentationString.Length).SequenceEqual(indentationString.AsSpan());
    }

    private bool IndentTriviaItems(
        List<TextRangeWithKind> trivia,
        int commentIndentation,
        bool indentNextTokenOrTrivia,
        Action<TextRangeWithKind> indentSingleLine)
    {
        foreach (TextRangeWithKind triviaItem in trivia)
        {
            bool triviaInRange = triviaItem.Loc.ContainedBy(originalRange);
            switch (triviaItem.Kind)
            {
                case SyntaxKind.MultiLineCommentTrivia:
                    if (triviaInRange)
                    {
                        IndentMultilineComment(triviaItem.Loc, commentIndentation, !indentNextTokenOrTrivia, true);
                    }
                    indentNextTokenOrTrivia = false;
                    break;
                case SyntaxKind.SingleLineCommentTrivia:
                    if (indentNextTokenOrTrivia && triviaInRange)
                    {
                        indentSingleLine(triviaItem);
                    }
                    indentNextTokenOrTrivia = false;
                    break;
                case SyntaxKind.NewLineTrivia:
                    indentNextTokenOrTrivia = true;
                    break;
            }
        }
        return indentNextTokenOrTrivia;
    }

    private void IndentMultilineComment(TextRange commentRange, int indentation, bool firstLineIsIndented, bool indentFinalLine)
    {
        int startLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, commentRange.Pos);
        int endLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, commentRange.End);

        if (startLine == endLine)
        {
            if (!firstLineIsIndented)
            {
                InsertIndentation(commentRange.Pos, indentation, false);
            }
            return;
        }

        var parts = new List<TextRange>();
        int startPos = commentRange.Pos;
        int[] lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile);
        for (int line = startLine; line < endLine; line++)
        {
            int endOfLine = global::Ts.Scanner.Scanner.GetECMAEndLinePosition(sourceFile, line);
            parts.Add(new TextRange(startPos, endOfLine));
            startPos = lineStarts[line + 1];
        }

        if (indentFinalLine)
        {
            parts.Add(new TextRange(startPos, commentRange.End));
        }

        if (parts.Count == 0)
        {
            return;
        }

        int startLinePos = lineStarts[startLine];
        (int nonWhitespaceInFirstPartCharacter, int nonWhitespaceInFirstPartColumn) =
            Indent.FindFirstNonWhitespaceCharacterAndColumn(
                startLinePos,
                parts[0].Pos,
                sourceFile,
                formattingContext.Options);

        int startIndex = 0;
        if (firstLineIsIndented)
        {
            startIndex = 1;
            startLine++;
        }

        int indentationDelta = indentation - nonWhitespaceInFirstPartColumn;
        for (int i = startIndex; i < parts.Count; i++)
        {
            startLinePos = lineStarts[startLine];
            int nonWhitespaceCharacter = nonWhitespaceInFirstPartCharacter;
            int nonWhitespaceColumn = nonWhitespaceInFirstPartColumn;
            if (i != 0)
            {
                (nonWhitespaceCharacter, nonWhitespaceColumn) = Indent.FindFirstNonWhitespaceCharacterAndColumn(
                    parts[i].Pos,
                    parts[i].End,
                    sourceFile,
                    formattingContext.Options);
            }
            int newIndentation = nonWhitespaceColumn + indentationDelta;
            if (newIndentation > 0)
            {
                string indentationString = GetIndentationString(newIndentation, formattingContext.Options);
                RecordReplace(startLinePos, nonWhitespaceCharacter, indentationString);
            }
            else
            {
                RecordDelete(startLinePos, nonWhitespaceCharacter);
            }

            startLine++;
        }
    }

    private static string GetIndentationString(int indentation, FormatCodeSettings options)
    {
        if (!options.ConvertTabsToSpaces.IsTrue())
        {
            if (options.TabSize == 0)
            {
                return "";
            }
            int tabs = indentation / options.TabSize;
            int spaces = indentation - (tabs * options.TabSize);
            return new string('\t', tabs) + (spaces > 0 ? new string(' ', spaces) : "");
        }
        return new string(' ', indentation);
    }

    private static TextChange CreateTextChangeFromStartLength(int start, int length, string newText)
    {
        return new TextChange(new TextRange(start, start + length), newText);
    }

    private void RecordDelete(int start, int length)
    {
        if (length != 0)
        {
            edits.Add(CreateTextChangeFromStartLength(start, length, ""));
        }
    }

    private void RecordReplace(int start, int length, string newText)
    {
        if (length != 0 || newText.Length != 0)
        {
            edits.Add(CreateTextChangeFromStartLength(start, length, newText));
        }
    }

    private void RecordInsert(int start, string text)
    {
        if (text.Length != 0)
        {
            edits.Add(CreateTextChangeFromStartLength(start, 0, text));
        }
    }

    private void ConsumeTokenAndAdvanceScanner(
        TokenInfo currentTokenInfo,
        Node parent,
        DynamicIndenter dynamicIndentation,
        Node container,
        bool isListEndToken)
    {
        bool lastTriviaWasNewLine = formattingScanner.LastTrailingTriviaWasNewLine();
        bool indentToken = false;

        if (currentTokenInfo.LeadingTrivia.Count > 0)
        {
            ProcessTrivia(currentTokenInfo.LeadingTrivia, parent, childContextNode!, dynamicIndentation);
        }

        LineAction lineAction = LineAction.None;
        bool isTokenInRange = currentTokenInfo.Token.Loc.ContainedBy(originalRange);

        (int tokenStartLine, int tokenStartCharacter) =
            global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, currentTokenInfo.Token.Loc.Pos);

        if (isTokenInRange)
        {
            bool rangeHasError = rangeContainsError(currentTokenInfo.Token.Loc);
            TextRangeWithKind savedPreviousRange = previousRange;
            lineAction = ProcessRange(
                currentTokenInfo.Token,
                tokenStartLine,
                tokenStartCharacter,
                parent,
                childContextNode!,
                dynamicIndentation);
            if (!rangeHasError)
            {
                if (lineAction == LineAction.None)
                {
                    if (!IsZero(savedPreviousRange))
                    {
                        int previousEndLine = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, savedPreviousRange.Loc.End);
                        indentToken = lastTriviaWasNewLine && tokenStartLine != previousEndLine;
                    }
                    else
                    {
                        indentToken = lastTriviaWasNewLine;
                    }
                }
                else
                {
                    indentToken = lineAction == LineAction.LineAdded;
                }
            }
        }

        if (currentTokenInfo.TrailingTrivia.Count > 0)
        {
            previousRangeTriviaEnd = currentTokenInfo.TrailingTrivia[^1].Loc.End;
            foreach (TextRangeWithKind trivia in currentTokenInfo.TrailingTrivia)
            {
                if (IsComment(trivia.Kind) && !trivia.Loc.ContainedBy(originalRange))
                {
                    previousRangeTriviaEnd = trivia.Loc.Pos;
                    break;
                }
            }
            ProcessTrivia(currentTokenInfo.TrailingTrivia, parent, childContextNode!, dynamicIndentation);
        }

        if (indentToken)
        {
            int tokenIndentation = -1;
            if (isTokenInRange && !rangeContainsError(currentTokenInfo.Token.Loc))
            {
                tokenIndentation = dynamicIndentation.GetIndentationForToken(
                    tokenStartLine,
                    currentTokenInfo.Token.Kind,
                    container,
                    isListEndToken);
            }
            bool indentNextTokenOrTrivia = true;
            if (currentTokenInfo.LeadingTrivia.Count > 0)
            {
                int commentIndentation = dynamicIndentation.GetIndentationForComment(
                    currentTokenInfo.Token.Kind,
                    tokenIndentation,
                    container);
                indentNextTokenOrTrivia = IndentTriviaItems(
                    currentTokenInfo.LeadingTrivia,
                    commentIndentation,
                    indentNextTokenOrTrivia,
                    item => InsertIndentation(item.Loc.Pos, commentIndentation, false));
            }

            if (tokenIndentation != -1 && indentNextTokenOrTrivia)
            {
                InsertIndentation(currentTokenInfo.Token.Loc.Pos, tokenIndentation, lineAction == LineAction.LineAdded);

                lastIndentedLine = tokenStartLine;
                indentationOnLastIndentedLine = tokenIndentation;
            }
        }

        formattingScanner.Advance();
        childContextNode = parent;
    }

    private DynamicIndenter GetDynamicIndentation(Node node, int nodeStartLine, int indentation, int childDelta)
    {
        return new DynamicIndenter(node, nodeStartLine, indentation, childDelta, formattingContext.Options, sourceFile);
    }

    private static bool IsZero(TextRangeWithKind range)
    {
        return range.Loc.Pos == 0 && range.Loc.End == 0 && range.Kind == 0;
    }
}

internal enum LineAction
{
    None = 0,
    LineAdded = 1,
    LineRemoved = 2,
}

internal sealed class DynamicIndenter
{
    private readonly Node node;
    private readonly int nodeStartLine;
    private int indentation;
    private int delta;

    private readonly FormatCodeSettings options;
    private readonly SourceFile sourceFile;

    internal DynamicIndenter(
        Node node,
        int nodeStartLine,
        int indentation,
        int delta,
        FormatCodeSettings options,
        SourceFile sourceFile)
    {
        this.node = node;
        this.nodeStartLine = nodeStartLine;
        this.indentation = indentation;
        this.delta = delta;
        this.options = options;
        this.sourceFile = sourceFile;
    }

    internal int GetIndentationForComment(SyntaxKind kind, int tokenIndentation, Node container)
    {
        switch (kind)
        {
            case SyntaxKind.CloseBraceToken:
            case SyntaxKind.CloseBracketToken:
            case SyntaxKind.CloseParenToken:
                return indentation + GetDelta(container);
        }
        return tokenIndentation != -1 ? tokenIndentation : indentation;
    }

    internal int GetIndentationForToken(int line, SyntaxKind kind, Node container, bool suppressDelta)
    {
        if (!suppressDelta && ShouldAddDelta(line, kind, container))
        {
            return indentation + GetDelta(container);
        }
        return indentation;
    }

    internal int GetIndentation() => indentation;

    internal int GetDelta(Node child)
    {
        return Indent.NodeWillIndentChild(options, node, child, sourceFile, true) ? delta : 0;
    }

    internal void RecomputeIndentation(bool lineAdded, Node parent)
    {
        if (Indent.ShouldIndentChildNode(options, parent, node, sourceFile))
        {
            indentation += lineAdded ? options.IndentSize : -options.IndentSize; // !!! no nil check???
            delta = Indent.ShouldIndentChildNode(options, node, null, null) ? options.IndentSize : 0;
        }
    }

    private bool ShouldAddDelta(int line, SyntaxKind kind, Node container)
    {
        switch (kind)
        {
            case SyntaxKind.OpenBraceToken:
            case SyntaxKind.CloseBraceToken:
            case SyntaxKind.CloseParenToken:
            case SyntaxKind.ElseKeyword:
            case SyntaxKind.WhileKeyword:
            case SyntaxKind.AtToken:
                return false;
            case SyntaxKind.SlashToken:
            case SyntaxKind.GreaterThanToken:
                switch (container.Kind)
                {
                    case SyntaxKind.JsxOpeningElement:
                    case SyntaxKind.JsxClosingElement:
                    case SyntaxKind.JsxSelfClosingElement:
                        return false;
                }
                break;
            case SyntaxKind.OpenBracketToken:
            case SyntaxKind.CloseBracketToken:
                if (container.Kind != SyntaxKind.MappedType)
                {
                    return false;
                }
                break;
        }

        return nodeStartLine != line &&
            !(global::Ts.Ast.AstUtilities.HasDecorators(node) && kind == GetFirstNonDecoratorTokenOfNode(node));
    }

    private static SyntaxKind GetFirstNonDecoratorTokenOfNode(Node node)
    {
        Node[]? modifierNodes = node.ModifierNodes();
        if (modifierNodes is not null)
        {
            int decoratorIndex = Array.FindIndex(modifierNodes, global::Ts.Ast.NodeTests.IsDecorator);
            if (decoratorIndex >= 0)
            {
                for (int i = decoratorIndex; i < modifierNodes.Length; i++)
                {
                    if (global::Ts.Ast.AstUtilities.IsModifier(modifierNodes[i]))
                    {
                        return modifierNodes[i].Kind;
                    }
                }
            }
        }

        switch (node.Kind)
        {
            case SyntaxKind.ClassDeclaration:
                return SyntaxKind.ClassKeyword;
            case SyntaxKind.InterfaceDeclaration:
                return SyntaxKind.InterfaceKeyword;
            case SyntaxKind.FunctionDeclaration:
                return SyntaxKind.FunctionKeyword;
            case SyntaxKind.EnumDeclaration:
                return SyntaxKind.EnumDeclaration;
            case SyntaxKind.GetAccessor:
                return SyntaxKind.GetKeyword;
            case SyntaxKind.SetAccessor:
                return SyntaxKind.SetKeyword;
            case SyntaxKind.MethodDeclaration:
                if (node.AsMethodDeclaration().AsteriskToken is not null)
                {
                    return SyntaxKind.AsteriskToken;
                }
                goto case SyntaxKind.PropertyDeclaration;
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.Parameter:
                return global::Ts.Ast.AstUtilities.GetNameOfDeclaration(node)?.Kind ?? SyntaxKind.Unknown;
            default:
                return SyntaxKind.Unknown;
        }
    }
}
