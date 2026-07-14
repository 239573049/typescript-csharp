// Ported from tsgo internal/ls/folding.go.
//
// PORT-NOTE: the Go entry point reads the client capabilities from the request
// context (lsproto.GetClientCapabilities(ctx): TextDocument.FoldingRange.LineFoldingOnly
// and .FoldingRange.CollapsedText). The C# LS foundation has no per-request context
// carrier yet, so both capability bits are explicit parameters, and Go's
// supportsCollapsedText(ctx) helper dissolves into the threaded flag. ctx.Err() checks
// map to CancellationToken.IsCancellationRequested. Positions are UTF-16 code units.

using System;
using System.Collections.Generic;
using System.Threading;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using FoldingRange = Ts.LsProto.FoldingRange;
using FoldingRangeKind = Ts.LsProto.FoldingRangeKind;
using LspRange = Ts.LsProto.Range;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // folding.go:18 — ProvideFoldingRange.
    public FoldingRange[] ProvideFoldingRange(
        string fileName,
        bool lineFoldingOnly = false,
        bool supportsCollapsedText = false,
        CancellationToken cancellationToken = default)
    {
        var sourceFile = GetProgramAndFile(fileName);
        var res = AddNodeOutliningSpans(cancellationToken, supportsCollapsedText, sourceFile);
        res.AddRange(AddRegionOutliningSpans(supportsCollapsedText, sourceFile));
        if (lineFoldingOnly)
        {
            res = AdjustFoldingEnd(res, sourceFile);
        }
        res.Sort((a, b) =>
        {
            var c = a.StartLine.CompareTo(b.StartLine);
            if (c != 0)
            {
                return c;
            }
            return a.StartCharacter!.Value.CompareTo(b.StartCharacter!.Value); // Go dereferences unconditionally.
        });
        return res.ToArray();
    }

    // adjustFoldingEnd adjusts the end line of folding ranges when the client signals lineFoldingOnly.
    // This mirrors the behavior of VS Code's built-in TypeScript extension (workaround for vscode#47240).
    // When lineFoldingOnly is true, we hide lines from startLine+1 to endLine. And to keep closing
    // brackets/braces visible, we subtract 1 from endLine when the range ends with a closing pair character.
    private static List<FoldingRange> AdjustFoldingEnd(List<FoldingRange> ranges, SourceFile sourceFile)
    {
        var sourceText = sourceFile.Text();
        var result = new List<FoldingRange>(ranges.Count);
        foreach (var r in ranges)
        {
            if (r.EndCharacter != null && r.EndCharacter.Value > 0)
            {
                var endOffset = LsDeps.LineAndCharacterToPosition(sourceFile, r.EndLine, r.EndCharacter.Value);
                if (endOffset > 0 && endOffset <= sourceText.Length)
                {
                    var foldEndChar = sourceText[endOffset - 1];
                    if (foldEndChar is '}' or ']' or ')' or '`' or '>')
                    {
                        if (r.EndLine > r.StartLine)
                        {
                            r.EndLine--;
                        }
                    }
                }
            }
            result.Add(r);
        }
        return result;
    }

    // folding.go:61 — addNodeOutliningSpans.
    private List<FoldingRange> AddNodeOutliningSpans(CancellationToken cancellationToken, bool supportsCollapsedText, SourceFile sourceFile)
    {
        var depthRemaining = 40;
        var current = 0;

        var statements = sourceFile.Statements!;
        var n = statements.Nodes.Length;
        var foldingRange = new List<FoldingRange>(40);
        while (current < n)
        {
            while (current < n && !LsDeps.IsAnyImportSyntax(statements.Nodes[current]))
            {
                foldingRange.AddRange(VisitNode(cancellationToken, supportsCollapsedText, statements.Nodes[current], depthRemaining, sourceFile));
                current++;
            }
            if (current == n)
            {
                break;
            }
            var firstImport = current;
            while (current < n && LsDeps.IsAnyImportSyntax(statements.Nodes[current]))
            {
                foldingRange.AddRange(VisitNode(cancellationToken, supportsCollapsedText, statements.Nodes[current], depthRemaining, sourceFile));
                current++;
            }
            var lastImport = current - 1;
            if (lastImport != firstImport)
            {
                var foldingRangeKind = FoldingRangeKind.Imports;
                foldingRange.Add(CreateFoldingRangeFromBounds(
                    supportsCollapsedText,
                    global::Ts.AstNav.Tokens.GetStartOfNode(
                        global::Ts.AstNav.Tokens.FindChildOfKind(statements.Nodes[firstImport], SyntaxKind.ImportKeyword, sourceFile)!, // Go unconditional dereference.
                        sourceFile,
                        false /*includeJSDoc*/),
                    statements.Nodes[lastImport].End,
                    foldingRangeKind,
                    sourceFile));
            }
        }

        // Visit the EOF Token so that comments which aren't attached to statements are included.
        foldingRange.AddRange(VisitNode(cancellationToken, supportsCollapsedText, sourceFile.EndOfFileToken!, depthRemaining, sourceFile));
        return foldingRange;
    }

    // folding.go:101 — addRegionOutliningSpans.
    private List<FoldingRange> AddRegionOutliningSpans(bool supportsCollapsedText, SourceFile sourceFile)
    {
        var regions = new List<FoldingRange>(40);
        var @out = new List<FoldingRange>(40);
        var lineStarts = global::Ts.Scanner.Scanner.GetECMALineStarts(sourceFile);
        foreach (var currentLineStart in lineStarts)
        {
            var lineEnd = LsDeps.GetLineEndOfPosition(sourceFile, currentLineStart);
            var lineText = sourceFile.Text()[currentLineStart..lineEnd];
            var result = ParseRegionDelimiter(lineText);
            if (result == null ||
                LsDeps.IsInComment(sourceFile, currentLineStart, global::Ts.AstNav.Tokens.GetTokenAtPosition(sourceFile, currentLineStart)) != null)
            {
                continue;
            }

            if (result.IsStart)
            {
                // strings.Index over the line slice + currentLineStart == absolute IndexOf below.
                var commentStart = CreateLspPosition(
                    sourceFile.Text().IndexOf("//", currentLineStart, lineEnd - currentLineStart, StringComparison.Ordinal),
                    sourceFile);
                var foldingRangeKindRegion = FoldingRangeKind.Region;
                var region = new FoldingRange
                {
                    StartLine = commentStart.Line,
                    StartCharacter = commentStart.Character,
                    Kind = foldingRangeKindRegion,
                };
                if (supportsCollapsedText)
                {
                    var collapsedText = "#region";
                    if (result.Name != "")
                    {
                        collapsedText = result.Name;
                    }
                    region.CollapsedText = collapsedText;
                }
                // Our spans start out with some initial data.
                // On every `#endregion`, we'll come back to these `FoldingRange`s
                // and fill in their EndLine/EndCharacter.
                regions.Add(region);
            }
            else
            {
                if (regions.Count > 0)
                {
                    var region = regions[^1];
                    regions.RemoveAt(regions.Count - 1);
                    var endingPosition = CreateLspPosition(lineEnd, sourceFile);
                    region.EndLine = endingPosition.Line;
                    region.EndCharacter = endingPosition.Character;
                    @out.Add(region);
                }
            }
        }
        return @out;
    }

    // folding.go:146 — visitNode (Go free function taking `l`; instance method here).
    private List<FoldingRange> VisitNode(CancellationToken cancellationToken, bool supportsCollapsedText, Node n, int depthRemaining, SourceFile sourceFile)
    {
        if ((n.Flags & NodeFlags.Reparsed) != 0 || depthRemaining == 0 || cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        var foldingRange = new List<FoldingRange>(40);
        if ((!NodeTests.IsBinaryExpression(n) && AstUtilities.IsDeclaration(n)) || NodeTests.IsVariableStatement(n) || NodeTests.IsReturnStatement(n) || AstUtilities.IsCallOrNewExpression(n) || n.Kind == SyntaxKind.EndOfFile)
        {
            foldingRange.AddRange(AddOutliningForLeadingCommentsForNode(cancellationToken, supportsCollapsedText, n, sourceFile));
        }
        if (AstUtilities.IsFunctionLike(n) && n.Parent != null && NodeTests.IsBinaryExpression(n.Parent) && n.Parent.AsBinaryExpression().Left != null && NodeTests.IsPropertyAccessExpression(n.Parent.AsBinaryExpression().Left!))
        {
            foldingRange.AddRange(AddOutliningForLeadingCommentsForNode(cancellationToken, supportsCollapsedText, n.Parent.AsBinaryExpression().Left!, sourceFile));
        }
        if (NodeTests.IsBlock(n))
        {
            var statements = n.AsBlock().Statements;
            if (statements != null)
            {
                foldingRange.AddRange(AddOutliningForLeadingCommentsForPos(cancellationToken, supportsCollapsedText, statements.End, sourceFile));
            }
        }
        if (NodeTests.IsModuleBlock(n))
        {
            var statements = n.AsModuleBlock().Statements;
            if (statements != null)
            {
                foldingRange.AddRange(AddOutliningForLeadingCommentsForPos(cancellationToken, supportsCollapsedText, statements.End, sourceFile));
            }
        }
        if (AstUtilities.IsClassLike(n) || NodeTests.IsInterfaceDeclaration(n))
        {
            NodeList? members;
            if (NodeTests.IsClassDeclaration(n))
            {
                members = n.AsClassDeclaration().Members;
            }
            else if (NodeTests.IsClassExpression(n))
            {
                members = n.AsClassExpression().Members;
            }
            else
            {
                members = n.AsInterfaceDeclaration().Members;
            }
            if (members != null)
            {
                foldingRange.AddRange(AddOutliningForLeadingCommentsForPos(cancellationToken, supportsCollapsedText, members.End, sourceFile));
            }
        }

        var span = GetOutliningSpanForNode(supportsCollapsedText, n, sourceFile);
        if (span != null)
        {
            foldingRange.Add(span);
        }

        depthRemaining--;
        if (NodeTests.IsCallExpression(n))
        {
            depthRemaining++;
            var expressionNodes = VisitNode(cancellationToken, supportsCollapsedText, n.Expression()!, depthRemaining, sourceFile);
            foldingRange.AddRange(expressionNodes);
            depthRemaining--;
            foreach (var arg in n.Arguments() ?? [])
            {
                if (arg != null)
                {
                    foldingRange.AddRange(VisitNode(cancellationToken, supportsCollapsedText, arg, depthRemaining, sourceFile));
                }
            }
            var typeArguments = n.TypeArguments();
            foreach (var typeArg in typeArguments ?? [])
            {
                if (typeArg != null)
                {
                    foldingRange.AddRange(VisitNode(cancellationToken, supportsCollapsedText, typeArg, depthRemaining, sourceFile));
                }
            }
        }
        else if (NodeTests.IsIfStatement(n) && n.AsIfStatement().ElseStatement != null && NodeTests.IsIfStatement(n.AsIfStatement().ElseStatement!))
        {
            // Consider an 'else if' to be on the same depth as the 'if'.
            var ifStatement = n.AsIfStatement();
            var expressionNodes = VisitNode(cancellationToken, supportsCollapsedText, n.Expression()!, depthRemaining, sourceFile);
            foldingRange.AddRange(expressionNodes);
            var thenNode = VisitNode(cancellationToken, supportsCollapsedText, ifStatement.ThenStatement!, depthRemaining, sourceFile);
            foldingRange.AddRange(thenNode);
            depthRemaining++;
            var elseNode = VisitNode(cancellationToken, supportsCollapsedText, ifStatement.ElseStatement!, depthRemaining, sourceFile);
            foldingRange.AddRange(elseNode);
            depthRemaining--;
        }
        else
        {
            bool Visit(Node node)
            {
                var childNode = VisitNode(cancellationToken, supportsCollapsedText, node, depthRemaining, sourceFile);
                foldingRange.AddRange(childNode);
                return false;
            }
            n.ForEachChild(Visit);
        }
        depthRemaining++;
        return foldingRange;
    }

    // folding.go:238 — addOutliningForLeadingCommentsForNode.
    private List<FoldingRange> AddOutliningForLeadingCommentsForNode(CancellationToken cancellationToken, bool supportsCollapsedText, Node n, SourceFile sourceFile)
    {
        if (NodeTests.IsJsxText(n))
        {
            return [];
        }
        return AddOutliningForLeadingCommentsForPos(cancellationToken, supportsCollapsedText, n.Pos, sourceFile);
    }

    // folding.go:245 — addOutliningForLeadingCommentsForPos.
    // PORT-NOTE: Go allocates a printer.EmitContext purely to obtain a NodeFactory for
    // scanner.GetLeadingCommentRanges; the C# comment iterator builds plain CommentRange
    // structs, so the factory drops out.
    private List<FoldingRange> AddOutliningForLeadingCommentsForPos(CancellationToken cancellationToken, bool supportsCollapsedText, int pos, SourceFile sourceFile)
    {
        var foldingRange = new List<FoldingRange>(40);
        var firstSingleLineCommentStart = -1;
        var lastSingleLineCommentEnd = -1;
        var singleLineCommentCount = 0;
        var foldingRangeKindComment = FoldingRangeKind.Comment;

        FoldingRange? CombineAndAddMultipleSingleLineComments()
        {
            // Only outline spans of two or more consecutive single line comments
            if (singleLineCommentCount > 1)
            {
                return CreateFoldingRangeFromBounds(supportsCollapsedText, firstSingleLineCommentStart, lastSingleLineCommentEnd, foldingRangeKindComment, sourceFile);
            }
            return null;
        }

        var sourceText = sourceFile.Text();
        foreach (var comment in LsDeps.GetLeadingCommentRanges(sourceText, pos))
        {
            var commentPos = comment.Pos;
            var commentEnd = comment.End;

            if (cancellationToken.IsCancellationRequested)
            {
                return [];
            }
            switch (comment.Kind)
            {
                case SyntaxKind.SingleLineCommentTrivia:
                    // never fold region delimiters into single-line comment regions
                    var commentText = sourceText[commentPos..commentEnd];
                    if (ParseRegionDelimiter(commentText) != null)
                    {
                        var comments = CombineAndAddMultipleSingleLineComments();
                        if (comments != null)
                        {
                            foldingRange.Add(comments);
                        }
                        singleLineCommentCount = 0;
                        break;
                    }

                    // For single line comments, combine consecutive ones (2 or more) into
                    // a single span from the start of the first till the end of the last
                    if (singleLineCommentCount == 0)
                    {
                        firstSingleLineCommentStart = commentPos;
                    }
                    lastSingleLineCommentEnd = commentEnd;
                    singleLineCommentCount++;
                    break;
                case SyntaxKind.MultiLineCommentTrivia:
                    var multiLinePending = CombineAndAddMultipleSingleLineComments();
                    if (multiLinePending != null)
                    {
                        foldingRange.Add(multiLinePending);
                    }
                    foldingRange.Add(CreateFoldingRangeFromBounds(supportsCollapsedText, commentPos, commentEnd, foldingRangeKindComment, sourceFile));
                    singleLineCommentCount = 0;
                    break;
                default:
                    global::Ts.Core.Debug.AssertNever(comment.Kind);
                    break;
            }
        }
        var addedComments = CombineAndAddMultipleSingleLineComments();
        if (addedComments != null)
        {
            foldingRange.Add(addedComments);
        }
        return foldingRange;
    }

    // folding.go:309 — regionDelimiterResult.
    private sealed record RegionDelimiterResult(bool IsStart, string Name);

    // folding.go:314 — parseRegionDelimiter.
    // PORT-NOTE: Go trims with unicode.IsSpace / strings.TrimSpace; char.IsWhiteSpace is
    // the C# mapping (plan/05 §8), a superset differing only in exotic Zs code points.
    private static RegionDelimiterResult? ParseRegionDelimiter(string lineText)
    {
        // We trim the leading whitespace and // without the regex since the
        // multiple potential whitespace matches can make for some gnarly backtracking behavior
        var start = 0;
        while (start < lineText.Length && char.IsWhiteSpace(lineText[start]))
        {
            start++;
        }
        lineText = lineText[start..];
        if (!lineText.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }
        lineText = lineText[2..].Trim();
        if (lineText.EndsWith('\r'))
        {
            lineText = lineText[..^1];
        }
        if (!lineText.StartsWith('#'))
        {
            return null;
        }
        lineText = lineText[1..];
        var isStart = true;
        if (lineText.StartsWith("end", StringComparison.Ordinal))
        {
            isStart = false;
            lineText = lineText[3..];
        }
        if (!lineText.StartsWith("region", StringComparison.Ordinal))
        {
            return null;
        }
        lineText = lineText[6..];
        return new RegionDelimiterResult(isStart, lineText.Trim());
    }

    // folding.go:342 — getOutliningSpanForNode.
    private FoldingRange? GetOutliningSpanForNode(bool supportsCollapsedText, Node n, SourceFile sourceFile)
    {
        switch (n.Kind)
        {
            case SyntaxKind.Block:
                if (AstUtilities.IsFunctionLike(n.Parent))
                {
                    return FunctionSpan(supportsCollapsedText, n.Parent!, n, sourceFile);
                }
                // Check if the block is standalone, or 'attached' to some parent statement.
                // If the latter, we want to collapse the block, but consider its hint span
                // to be the entire span of the parent.
                switch (n.Parent!.Kind)
                {
                    case SyntaxKind.DoStatement:
                    case SyntaxKind.ForInStatement:
                    case SyntaxKind.ForOfStatement:
                    case SyntaxKind.ForStatement:
                    case SyntaxKind.IfStatement:
                    case SyntaxKind.WhileStatement:
                    case SyntaxKind.WithStatement:
                    case SyntaxKind.CatchClause:
                        return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBraceToken, true /*useFullStart*/, sourceFile);
                    case SyntaxKind.TryStatement:
                        // Could be the try-block, or the finally-block.
                        var tryStatement = n.Parent.AsTryStatement();
                        if (tryStatement.TryBlock == n)
                        {
                            return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBraceToken, true /*useFullStart*/, sourceFile);
                        }
                        else if (tryStatement.FinallyBlock == n)
                        {
                            if (SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBraceToken, true /*useFullStart*/, sourceFile) is { } trySpan)
                            {
                                return trySpan;
                            }
                        }
                        goto default; // Go fallthrough
                    default:
                        // Block was a standalone block.  In this case we want to only collapse
                        // the span of the block, independent of any parent span.
                        return CreateFoldingRange(supportsCollapsedText, CreateLspRangeFromNode(n, sourceFile), default, "");
                }
            case SyntaxKind.ModuleBlock:
                return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBraceToken, true /*useFullStart*/, sourceFile);
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.CaseBlock:
            case SyntaxKind.TypeLiteral:
            case SyntaxKind.ObjectBindingPattern:
                return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBraceToken, true /*useFullStart*/, sourceFile);
            case SyntaxKind.TupleType:
                return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBracketToken, !NodeTests.IsTupleTypeNode(n.Parent!) /*useFullStart*/, sourceFile);
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
                return SpanForNodeArray(supportsCollapsedText, n.AsCaseOrDefaultClause().Statements, sourceFile);
            case SyntaxKind.ObjectLiteralExpression:
                return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBraceToken, !NodeTests.IsArrayLiteralExpression(n.Parent!) && !NodeTests.IsCallExpression(n.Parent!) /*useFullStart*/, sourceFile);
            case SyntaxKind.ArrayLiteralExpression:
                return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBracketToken, !NodeTests.IsArrayLiteralExpression(n.Parent!) && !NodeTests.IsCallExpression(n.Parent!) /*useFullStart*/, sourceFile);
            case SyntaxKind.JsxElement:
            case SyntaxKind.JsxFragment:
                return SpanForJSXElement(supportsCollapsedText, n, sourceFile);
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxOpeningElement:
                return SpanForJSXAttributes(supportsCollapsedText, n, sourceFile);
            case SyntaxKind.TemplateExpression:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                return SpanForTemplateLiteral(supportsCollapsedText, n, sourceFile);
            case SyntaxKind.ArrayBindingPattern:
                return SpanForNode(supportsCollapsedText, n, SyntaxKind.OpenBracketToken, !NodeTests.IsBindingElement(n.Parent!) /*useFullStart*/, sourceFile);
            case SyntaxKind.ArrowFunction:
                return SpanForArrowFunction(supportsCollapsedText, n, sourceFile);
            case SyntaxKind.CallExpression:
                return SpanForCallExpression(supportsCollapsedText, n, sourceFile);
            case SyntaxKind.ParenthesizedExpression:
                return SpanForParenthesizedExpression(supportsCollapsedText, n, sourceFile);
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
            case SyntaxKind.ImportAttributes:
                return SpanForImportExportElements(supportsCollapsedText, n, sourceFile);
        }
        return null;
    }

    // folding.go:402 — spanForImportExportElements.
    private FoldingRange? SpanForImportExportElements(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        NodeList? elements = null;
        switch (node.Kind)
        {
            case SyntaxKind.NamedImports:
                elements = node.AsNamedImports().Elements;
                break;
            case SyntaxKind.NamedExports:
                elements = node.AsNamedExports().Elements;
                break;
            case SyntaxKind.ImportAttributes:
                elements = node.AsImportAttributes().Attributes;
                break;
        }
        if (elements == null || elements.Nodes.Length == 0)
        {
            return null;
        }
        var openToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.OpenBraceToken, sourceFile);
        var closeToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.CloseBraceToken, sourceFile);
        if (openToken == null || closeToken == null || LsDeps.PositionsAreOnSameLine(openToken.Pos, closeToken.Pos, sourceFile))
        {
            return null;
        }
        return RangeBetweenTokens(supportsCollapsedText, openToken, closeToken, sourceFile, false /*useFullStart*/);
    }

    // folding.go:423 — spanForParenthesizedExpression.
    private FoldingRange? SpanForParenthesizedExpression(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        var start = global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false /*includeJSDoc*/);
        if (LsDeps.PositionsAreOnSameLine(start, node.End, sourceFile))
        {
            return null;
        }
        var textRange = CreateLspRangeFromBounds(start, node.End, sourceFile);
        return CreateFoldingRange(supportsCollapsedText, textRange, default, "");
    }

    // folding.go:432 — spanForCallExpression.
    private FoldingRange? SpanForCallExpression(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        if (node.AsCallExpression().Arguments == null || node.AsCallExpression().Arguments!.Nodes.Length == 0)
        {
            return null;
        }
        var openToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.OpenParenToken, sourceFile);
        var closeToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.CloseParenToken, sourceFile);
        if (openToken == null || closeToken == null || LsDeps.PositionsAreOnSameLine(openToken.Pos, closeToken.Pos, sourceFile))
        {
            return null;
        }

        return RangeBetweenTokens(supportsCollapsedText, openToken, closeToken, sourceFile, true /*useFullStart*/);
    }

    // folding.go:445 — spanForArrowFunction.
    private FoldingRange? SpanForArrowFunction(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        var arrowFunctionNode = node.AsArrowFunction();
        if (NodeTests.IsBlock(arrowFunctionNode.Body!) || NodeTests.IsParenthesizedExpression(arrowFunctionNode.Body!) || LsDeps.PositionsAreOnSameLine(arrowFunctionNode.Body!.Pos, arrowFunctionNode.Body!.End, sourceFile))
        {
            return null;
        }
        var textRange = CreateLspRangeFromBounds(arrowFunctionNode.Body!.Pos, arrowFunctionNode.Body!.End, sourceFile);
        return CreateFoldingRange(supportsCollapsedText, textRange, default, "");
    }

    // folding.go:454 — spanForTemplateLiteral.
    private FoldingRange? SpanForTemplateLiteral(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        if (node.Kind == SyntaxKind.NoSubstitutionTemplateLiteral && node.Text().Length == 0)
        {
            return null;
        }
        return CreateFoldingRangeFromBounds(supportsCollapsedText, global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false /*includeJSDoc*/), node.End, default, sourceFile);
    }

    // folding.go:461 — spanForJSXElement.
    private FoldingRange SpanForJSXElement(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        if (node.Kind == SyntaxKind.JsxElement)
        {
            var jsxElement = node.AsJsxElement();
            var textRange = CreateLspRangeFromBounds(global::Ts.AstNav.Tokens.GetStartOfNode(jsxElement.OpeningElement!, sourceFile, false /*includeJSDoc*/), jsxElement.ClosingElement!.End, sourceFile);
            var tagName = global::Ts.Scanner.Scanner.GetTextOfNode(jsxElement.OpeningElement!.TagName()!);
            var bannerText = "<" + tagName + ">...</" + tagName + ">";
            return CreateFoldingRange(supportsCollapsedText, textRange, default, bannerText);
        }
        // JsxFragment
        var jsxFragment = node.AsJsxFragment();
        var fragmentRange = CreateLspRangeFromBounds(global::Ts.AstNav.Tokens.GetStartOfNode(jsxFragment.OpeningFragment!, sourceFile, false /*includeJSDoc*/), jsxFragment.ClosingFragment!.End, sourceFile);
        return CreateFoldingRange(supportsCollapsedText, fragmentRange, default, "<>...</>");
    }

    // folding.go:475 — spanForJSXAttributes.
    private FoldingRange? SpanForJSXAttributes(bool supportsCollapsedText, Node node, SourceFile sourceFile)
    {
        JsxAttributesNode? attributes;
        if (node.Kind == SyntaxKind.JsxSelfClosingElement)
        {
            attributes = node.AsJsxSelfClosingElement().Attributes;
        }
        else
        {
            attributes = node.AsJsxOpeningElement().Attributes;
        }
        var properties = attributes!.Properties();
        if (properties == null || properties.Length == 0)
        {
            return null;
        }
        return CreateFoldingRangeFromBounds(supportsCollapsedText, global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false /*includeJSDoc*/), node.End, default, sourceFile);
    }

    // folding.go:488 — spanForNodeArray.
    private FoldingRange? SpanForNodeArray(bool supportsCollapsedText, NodeList? statements, SourceFile sourceFile)
    {
        if (statements != null && statements.Nodes.Length != 0)
        {
            return CreateFoldingRange(supportsCollapsedText, CreateLspRangeFromBounds(statements.Pos, statements.End, sourceFile), default, "");
        }
        return null;
    }

    // folding.go:495 — spanForNode.
    private FoldingRange? SpanForNode(bool supportsCollapsedText, Node node, SyntaxKind open, bool useFullStart, SourceFile sourceFile)
    {
        var closeBrace = SyntaxKind.CloseBraceToken;
        if (open != SyntaxKind.OpenBraceToken)
        {
            closeBrace = SyntaxKind.CloseBracketToken;
        }
        var openToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, open, sourceFile);
        var closeToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, closeBrace, sourceFile);
        if (openToken != null && closeToken != null)
        {
            return RangeBetweenTokens(supportsCollapsedText, openToken, closeToken, sourceFile, useFullStart);
        }
        return null;
    }

    // folding.go:508 — rangeBetweenTokens.
    private FoldingRange RangeBetweenTokens(bool supportsCollapsedText, Node openToken, Node closeToken, SourceFile sourceFile, bool useFullStart)
    {
        LspRange textRange;
        if (useFullStart)
        {
            textRange = CreateLspRangeFromBounds(openToken.Pos, closeToken.End, sourceFile);
        }
        else
        {
            textRange = CreateLspRangeFromBounds(global::Ts.AstNav.Tokens.GetStartOfNode(openToken, sourceFile, false /*includeJSDoc*/), closeToken.End, sourceFile);
        }
        return CreateFoldingRange(supportsCollapsedText, textRange, default, "");
    }

    // folding.go:522 — createFoldingRange. The Go supportsCollapsedText(ctx) capability
    // probe is the threaded flag here (see the header PORT-NOTE).
    private static FoldingRange CreateFoldingRange(bool supportsCollapsedText, LspRange textRange, FoldingRangeKind foldingRangeKind, string collapsedText)
    {
        FoldingRangeKind? kind = null;
        if (!string.IsNullOrEmpty(foldingRangeKind.Value))
        {
            kind = foldingRangeKind;
        }
        var result = new FoldingRange
        {
            StartLine = textRange.Start.Line,
            StartCharacter = textRange.Start.Character,
            EndLine = textRange.End.Line,
            EndCharacter = textRange.End.Character,
            Kind = kind,
        };
        if (collapsedText != "" && supportsCollapsedText)
        {
            result.CollapsedText = collapsedText;
        }
        return result;
    }

    // folding.go:540 — createFoldingRangeFromBounds.
    private FoldingRange CreateFoldingRangeFromBounds(bool supportsCollapsedText, int pos, int end, FoldingRangeKind foldingRangeKind, SourceFile sourceFile)
    {
        return CreateFoldingRange(supportsCollapsedText, CreateLspRangeFromBounds(pos, end, sourceFile), foldingRangeKind, "");
    }

    // folding.go:544 — functionSpan.
    private FoldingRange? FunctionSpan(bool supportsCollapsedText, Node node, Node body, SourceFile sourceFile)
    {
        var openToken = TryGetFunctionOpenToken(node, body, sourceFile);
        var closeToken = global::Ts.AstNav.Tokens.FindChildOfKind(body, SyntaxKind.CloseBraceToken, sourceFile);
        if (openToken != null && closeToken != null)
        {
            return RangeBetweenTokens(supportsCollapsedText, openToken, closeToken, sourceFile, true /*useFullStart*/);
        }
        return null;
    }

    // folding.go:553 — tryGetFunctionOpenToken.
    private static Node? TryGetFunctionOpenToken(Node node, Node body, SourceFile sourceFile)
    {
        if (IsNodeArrayMultiLine(node.Parameters(), sourceFile))
        {
            var openParenToken = global::Ts.AstNav.Tokens.FindChildOfKind(node, SyntaxKind.OpenParenToken, sourceFile);
            if (openParenToken != null)
            {
                return openParenToken;
            }
        }
        return global::Ts.AstNav.Tokens.FindChildOfKind(body, SyntaxKind.OpenBraceToken, sourceFile);
    }

    // folding.go:563 — isNodeArrayMultiLine.
    private static bool IsNodeArrayMultiLine(Node[] list, SourceFile sourceFile)
    {
        if (list.Length == 0)
        {
            return false;
        }
        return !LsDeps.PositionsAreOnSameLine(list[0].Pos, list[^1].End, sourceFile);
    }
}
