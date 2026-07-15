// Ported from tsgo internal/printer/utilities.go — comment-recognition + indent helpers used on the
// preserveSourceNewlines / comment emit paths.

namespace Ts.Printer;

using Ts.Ast;

internal static partial class PrinterUtilities
{
    // utilities.go:861 — isJSDocLikeText.
    internal static bool IsJSDocLikeText(string text, CommentRange comment) =>
        comment.Kind == SyntaxKind.MultiLineCommentTrivia &&
        comment.Len >= 5 &&
        text[comment.Pos + 2] == '*' &&
        text[comment.Pos + 3] != '/';

    // utilities.go:868 — IsPinnedComment.
    internal static bool IsPinnedComment(string text, CommentRange comment) =>
        comment.Kind == SyntaxKind.MultiLineCommentTrivia &&
        comment.Len > 5 &&
        text[comment.Pos + 2] == '!';

    // utilities.go:567 — originalNodesHaveSameParent.
    internal static bool OriginalNodesHaveSameParent(EmitContext emitContext, Node nodeA, Node nodeB)
    {
        var a = emitContext.MostOriginal(nodeA);
        if (a!.Parent != null)
        {
            // For performance, do not call MostOriginal for nodeB if nodeA doesn't even have a parent node.
            var b = emitContext.MostOriginal(nodeB);
            return a.Parent == b!.Parent;
        }
        return false;
    }

    // utilities.go:874 — calculateIndent. PORT-NOTE: Go decodes UTF-8 runes and advances by byte size;
    // every single-line whitespace character is in the BMP (one UTF-16 code unit), so iterating chars
    // and advancing by one is equivalent.
    internal static int CalculateIndent(string text, int pos, int end)
    {
        int currentLineIndent = 0;
        int indentSize = TextWriter.GetDefaultIndentSize();
        while (pos < end)
        {
            char ch = text[pos];
            if (!global::Ts.StringUtil.StringUtil.IsWhiteSpaceSingleLine(ch))
            {
                break;
            }
            if (ch == '\t')
            {
                // Tabs = TabSize = indent size and go to next tabStop
                currentLineIndent += indentSize - (currentLineIndent % indentSize);
            }
            else
            {
                // Single space
                currentLineIndent++;
            }
            pos++;
        }

        return currentLineIndent;
    }
}
