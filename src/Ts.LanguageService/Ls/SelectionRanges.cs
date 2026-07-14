// Ported from tsgo internal/ls/selectionranges.go.
//
// PORT-NOTE: the Go entry point takes LSP line/character positions and converts
// them through l.converters. The C# LS foundation's position currency is the raw
// UTF-16 offset (see LanguageService.GetNodeAtPosition), so the entry point takes
// offsets directly and the LineAndCharacterToPosition conversion dissolves. The
// Go `factory := &ast.NodeFactory{}` exists only to construct plain CommentRange
// values inside scanner.GetTrailingCommentRanges; the LsDeps copy of that iterator
// has no factory parameter, so it drops out. lsproto.Range is a class in C#, so
// Go's struct equality `current.Range == lspRange` becomes the RangesEqual helper.
// Positions are UTF-16 code units throughout.

using System.Collections.Generic;
using Ts.Ast; // instance-syntax resolution of the Ts.Ast.NodeAccessors extension methods
using SelectionRange = Ts.LsProto.SelectionRange;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // selectionranges.go:13 — ProvideSelectionRanges.
    public SelectionRange[] ProvideSelectionRanges(string fileName, IReadOnlyList<int> positions)
    {
        var sourceFile = GetProgramAndFile(fileName);

        var results = new List<SelectionRange>();
        foreach (var position in positions)
        {
            var selectionRange = GetSmartSelectionRange(sourceFile, position);
            if (selectionRange != null)
            {
                results.Add(selectionRange);
            }
        }

        return results.ToArray();
    }

    // selectionranges.go:31 — getSmartSelectionRange.
    private static SelectionRange? GetSmartSelectionRange(SourceFile sourceFile, int pos)
    {
        bool NodeContainsPosition(Node? node)
        {
            if (node == null)
            {
                return false;
            }
            var start = global::Ts.Scanner.Scanner.GetTokenPosOfNode(node, sourceFile, true /*includeJSDoc*/);
            var end = node.End;
            return start <= pos && pos < end;
        }

        SelectionRange? PushSelectionRange(SelectionRange? current, int start, int end)
        {
            if (start == end)
            {
                return current;
            }

            if (!(start <= pos && pos <= end))
            {
                return current;
            }

            var lspRange = LsDeps.ToLspRange(sourceFile, new global::Ts.Core.TextRange(start, end));

            if (current != null && RangesEqual(current.Range, lspRange))
            {
                return current;
            }

            return new SelectionRange
            {
                Range = lspRange,
                Parent = current,
            };
        }

        SelectionRange? PushSelectionCommentRange(SelectionRange? current, int start, int end)
        {
            current = PushSelectionRange(current, start, end);

            var commentPos = start;
            var text = sourceFile.Text();
            while (commentPos < end && commentPos < text.Length && text[commentPos] == '/')
            {
                commentPos++;
            }
            current = PushSelectionRange(current, commentPos, end);

            return current;
        }

        bool PositionsAreOnSameLine(int pos1, int pos2)
        {
            if (pos1 == pos2)
            {
                return true;
            }
            return LsDeps.PositionsAreOnSameLine(pos1, pos2, sourceFile);
        }

        static bool ShouldSkipNode(Node node, Node? parent)
        {
            if (NodeTests.IsBlock(node))
            {
                return true;
            }

            if (NodeTests.IsTemplateSpan(node) || NodeTests.IsTemplateHead(node) || NodeTests.IsTemplateTail(node))
            {
                return true;
            }

            if (parent != null && NodeTests.IsVariableDeclarationList(node) && NodeTests.IsVariableStatement(parent))
            {
                return true;
            }

            // Skip lone variable declarations
            if (parent != null && NodeTests.IsVariableDeclaration(node) && NodeTests.IsVariableDeclarationList(parent))
            {
                var decl = parent.AsVariableDeclarationList();
                if (decl != null && decl.Declarations!.Nodes.Length == 1)
                {
                    return true;
                }
            }

            if (NodeTests.IsJSDocTypeExpression(node) || NodeTests.IsJSDocSignature(node) || NodeTests.IsJSDocTypeLiteral(node))
            {
                return true;
            }

            return false;
        }

        var fullRange = LsDeps.ToLspRange(sourceFile, new global::Ts.Core.TextRange(sourceFile.Pos, sourceFile.End));
        var result = new SelectionRange
        {
            Range = fullRange,
        };

        Node? current = sourceFile.AsNode();
        while (current != null)
        {
            Node? next = null;
            var parent = current;

            Node? Visit(Node? node)
            {
                if (node != null && next == null)
                {
                    CommentRange? foundComment = null;
                    foreach (var comment in LsDeps.GetTrailingCommentRanges(sourceFile.Text(), node.End))
                    {
                        foundComment = comment;
                        break;
                    }
                    if (foundComment != null && foundComment.Value.Kind == SyntaxKind.SingleLineCommentTrivia)
                    {
                        result = PushSelectionCommentRange(result, foundComment.Value.Pos, foundComment.Value.End);
                    }

                    if (NodeContainsPosition(node))
                    {
                        // Add range for multi-line function bodies before skipping the block
                        if (NodeTests.IsBlock(node) && AstUtilities.IsFunctionLikeDeclaration(parent))
                        {
                            if (!PositionsAreOnSameLine(global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false), node.End))
                            {
                                var blockStart = global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false);
                                var blockEnd = node.End;
                                result = PushSelectionRange(result, blockStart, blockEnd);
                            }
                        }

                        // Synthesize a stop for '${ ... }' since '${' and '}' actually belong to siblings.
                        if (NodeTests.IsTemplateSpan(parent))
                        {
                            var templateSpan = parent.AsTemplateSpan();
                            if (templateSpan.Literal != null)
                            {
                                // Start from just before the '${' and end after the '}'
                                // The '${' is 2 characters before the expression start
                                var spanStart = node.Pos - 2;
                                // The '}' is the first character of the template literal (middle or tail)
                                var spanEnd = global::Ts.AstNav.Tokens.GetStartOfNode(templateSpan.Literal, sourceFile, false) + 1;
                                // Validate the positions are reasonable
                                var text = sourceFile.Text();
                                if (spanStart >= 0 && spanEnd <= text.Length && spanStart < spanEnd)
                                {
                                    result = PushSelectionRange(result, spanStart, spanEnd);
                                }
                            }
                        }

                        if (!ShouldSkipNode(node, parent))
                        {
                            var start = global::Ts.AstNav.Tokens.GetStartOfNode(node, sourceFile, false);
                            var end = node.End;
                            result = PushSelectionRange(result, start, end);

                            // String literals should have a stop both inside and outside their quotes.
                            if (NodeTests.IsStringLiteral(node) || node.Kind == SyntaxKind.TemplateExpression || node.Kind == SyntaxKind.NoSubstitutionTemplateLiteral)
                            {
                                // Only add inner content range if there's actually content (handles unterminated literals)
                                if (start + 1 < end - 1)
                                {
                                    result = PushSelectionRange(result, start + 1, end - 1);
                                }
                            }
                        }

                        next = node;
                    }
                }
                return node;
            }

            NodeList? VisitNodesHook(NodeList? nodes, NodeVisitor v)
            {
                if (nodes != null && nodes.Nodes.Length > 0)
                {
                    var shouldSkipList = parent != null && (NodeTests.IsVariableDeclarationList(parent) || NodeTests.IsTemplateExpression(parent));

                    if (!shouldSkipList)
                    {
                        var start = global::Ts.AstNav.Tokens.GetStartOfNode(nodes.Nodes[0], sourceFile, false);
                        var end = nodes.Nodes[nodes.Nodes.Length - 1].End;

                        if (start <= pos && pos < end)
                        {
                            result = PushSelectionRange(result, start, end);
                        }
                    }
                }
                return v.VisitNodes(nodes);
            }

            // Visit JSDoc nodes first if they exist
            foreach (var jsdoc in current.JSDoc(sourceFile) ?? [])
            {
                Visit(jsdoc);
            }

            var tempVisitor = new NodeVisitor(n => Visit(n), null, new NodeVisitorHooks
            {
                VisitNodes = VisitNodesHook,
            });

            current.VisitEachChild(tempVisitor);
            current = next;
        }
        return result;
    }

    // Go struct equality of lsproto.Range (selectionranges.go:54); lsproto.Range is a
    // class in C#, so the value comparison is spelled out.
    private static bool RangesEqual(global::Ts.LsProto.Range a, global::Ts.LsProto.Range b)
    {
        return a.Start.Line == b.Start.Line && a.Start.Character == b.Start.Character &&
            a.End.Line == b.End.Line && a.End.Character == b.End.Character;
    }
}
