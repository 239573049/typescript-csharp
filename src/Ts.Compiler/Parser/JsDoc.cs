// Ported from tsgo internal/parser/jsdoc.go (JSDoc spelling preserved per plan R6).
// Only the eager entry `withJSDoc` is ported here so far; the deep JSDoc-comment parser
// (parseJSDocComment / reparseTags and the tag family) remains stubbed in
// Parser.Generated.Stubs.cs and is hit only for JS files or TS comments with @see/@link.
using System.Collections.Generic;

using Ts.Ast;

namespace Ts.Parser;

public sealed partial class Parser
{
    // withJSDoc: attach JSDoc to a node. For TS/TSX, JSDoc parsing is deferred to first
    // access (lazy) unless the comment has @see/@link; @deprecated is flagged cheaply.
    private Node[]? WithJSDoc(Node node, JSDocScannerInfo info)
    {
        if ((info & JSDocScannerInfo.HasJSDoc) == 0)
        {
            return null;
        }

        if (!IsJavaScript())
        {
            node.Flags |= NodeFlags.HasJSDoc;
            if ((info & JSDocScannerInfo.HasDeprecated) != 0)
            {
                node.Flags |= NodeFlags.PossiblyContainsDeprecatedTag;
            }
            if ((info & JSDocScannerInfo.HasSeeOrLink) == 0)
            {
                return null;
            }
            // Fall through to eager parse for @see/@link
        }

        jsdocCommentRangesSpace.Clear();
        List<CommentRange> ranges = Utilities.GetJSDocCommentRanges(factory, jsdocCommentRangesSpace, node, sourceText);
        jsdocCommentRangesSpace = ranges;

        // Should only be called once per node
        hasDeprecatedTag = false;
        var jsdoc = new List<Node>(ranges.Count);
        int pos = node.Pos;
        foreach (CommentRange comment in ranges)
        {
            Node? parsed = ParseJSDocComment(node, comment.Pos, comment.End, pos);
            if (parsed != null)
            {
                parsed.Parent = node;
                jsdoc.Add(parsed);
                pos = parsed.End;
            }
        }
        if (jsdoc.Count != 0)
        {
            if ((node.Flags & NodeFlags.HasJSDoc) == 0)
            {
                node.Flags |= NodeFlags.HasJSDoc;
            }
            if (hasDeprecatedTag)
            {
                hasDeprecatedTag = false;
                node.Flags |= NodeFlags.PossiblyContainsDeprecatedTag;
            }
            Node[] jsdocArray = jsdoc.ToArray();
            if (IsJavaScript())
            {
                ReparseTags(node, jsdocArray);
            }
            jsdocInfos.Add(new JSDocInfo { Parent = node, JsDocs = jsdocArray });
            return jsdocArray;
        }
        return null;
    }
}
