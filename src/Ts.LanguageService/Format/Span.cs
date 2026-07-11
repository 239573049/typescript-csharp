// Ported from tsgo internal/format/span.go (partial)
//
// PORT-NOTE (breadth-first, per dispatch): span.go (1,252 lines) is the formatting
// main loop. The standalone entry helpers that bracket the loop — findEnclosingNode,
// getScanStartPosition, prepareRangeContainsErrorFunction, getOwnOrInheritedDelta,
// getNonDecoratorTokenPosOfNode — are ported verbatim (they only need ast/scanner/
// core surfaces already available). The recursive worker itself (formatSpanWorker.
// execute → processNode/processChildNode/processChildNodes/processPair/processRange/
// insertIndentation/applyRuleEdits, plus dynamicIndenter and the ast.NodeVisitor
// wiring) is the "complex indentation/JSX/comment-alignment" path the dispatch
// authorizes stubbing. FormatSpanWorker.Execute currently returns no edits; it is
// the single remaining hole before this engine emits real formatting edits, and is
// flagged in the dispatch report.

using System;
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
        var sorted = CoreUtil.Filter(errors, d => originalRange.Overlaps(d.Loc()));
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
        if (AstUtilities.HasDecorators(node))
        {
            lastDecorator = CoreUtil.FindLast(node.ModifierNodes(), NodeTests.IsDecorator);
        }
        file ??= AstUtilities.GetSourceFileOfNode(node)!;
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
//
// !!! STUB (see file header): fields are carried faithfully; the recursive Execute
// body is not yet ported. Execute currently produces no edits.
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

    // Go: func (w *formatSpanWorker) execute(s *formattingScanner) []core.TextChange
    //
    // !!! STUB: the full worker (processNode/processPair/insertIndentation/
    // applyRuleEdits + dynamicIndenter + NodeVisitor) is pending. Returns no edits.
    internal TextChange[] Execute(FormattingScanner s)
    {
        // !!! TODO: port formatSpanWorker.execute. Until then, formatting yields no
        // edits (a no-op formatter), which is a safe conservative behavior.
        return Array.Empty<TextChange>();
    }
}
