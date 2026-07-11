using static Ts.Ast.NodeTests;

namespace Ts.Ast;

// Go: internal/ast/utilities.go — the subset of the hand-written ast utility surface that
// astnav (internal/astnav/tokens.go) consumes. These functions were listed as deferred in
// Utilities.cs's SCOPE note ("comment/JSDoc navigation"); they are landed here, verbatim from
// Go source order, as part of the phase-3 astnav port (their first consumer). A later dedicated
// utilities.go completion pass may fold them back into Utilities.cs; kept in a separate partial
// file to avoid touching the existing hand-written file.
public static partial class AstUtilities
{
    // utilities.go:2062
    public static bool IsJSDocLinkLike(Node node)
    {
        return NodeKindIs(node, SyntaxKind.JSDocLink, SyntaxKind.JSDocLinkCode, SyntaxKind.JSDocLinkPlain);
    }

    // utilities.go:2066
    public static bool IsJSDocTag(Node node)
    {
        return node.Kind >= SyntaxKind.FirstJSDocTagNode && node.Kind <= SyntaxKind.LastJSDocTagNode;
    }

    // utilities.go:2129
    public static bool IsJSDocNode(Node node)
    {
        return node.Kind >= SyntaxKind.FirstJSDocNode && node.Kind <= SyntaxKind.LastJSDocNode;
    }

    // utilities.go:2133
    public static bool IsNonWhitespaceToken(Node node)
    {
        return IsTokenKind(node.Kind) && !IsWhitespaceOnlyJsxText(node);
    }

    // utilities.go:2137
    public static bool IsWhitespaceOnlyJsxText(Node node)
    {
        return node.Kind == SyntaxKind.JsxText && node.AsJsxText().ContainsOnlyTriviaWhiteSpaces;
    }

    // utilities.go:2988
    public static bool ForEachChildAndJSDoc(Node node, SourceFile sourceFile, Visitor v)
    {
        if (Node.VisitNodes(v, node.JSDoc(sourceFile)))
        {
            return true;
        }
        return node.ForEachChild(v);
    }

    // utilities.go:3080 — See IsJSDocSingleCommentNode.
    public static bool IsJSDocSingleCommentNodeList(NodeList? nodeList)
    {
        if (nodeList == null || nodeList.Nodes.Length == 0)
        {
            return false;
        }
        var parent = nodeList.Nodes[0].Parent;
        if (parent == null)
        {
            return false;
        }
        return IsJSDocSingleCommentNode(parent) && ReferenceEquals(nodeList, parent.CommentList());
    }

    // utilities.go:3092 — See IsJSDocSingleCommentNode.
    public static bool IsJSDocSingleCommentNodeComment(Node? node)
    {
        if (node == null || node.Parent == null)
        {
            return false;
        }
        return IsJSDocSingleCommentNode(node.Parent) && ReferenceEquals(node, node.Parent.CommentList()!.Nodes[0]);
    }

    // utilities.go:3101
    public static bool IsJSDocSingleCommentNode(Node node)
    {
        return HasComment(node.Kind) && node.CommentList() != null && node.CommentList()!.Nodes.Length == 1;
    }

    // utilities.go:3787
    internal static bool HasComment(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.JSDoc:
            case SyntaxKind.JSDocUnknownTag:
            case SyntaxKind.JSDocAugmentsTag:
            case SyntaxKind.JSDocImplementsTag:
            case SyntaxKind.JSDocDeprecatedTag:
            case SyntaxKind.JSDocPublicTag:
            case SyntaxKind.JSDocPrivateTag:
            case SyntaxKind.JSDocProtectedTag:
            case SyntaxKind.JSDocReadonlyTag:
            case SyntaxKind.JSDocOverrideTag:
            case SyntaxKind.JSDocCallbackTag:
            case SyntaxKind.JSDocOverloadTag:
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
            case SyntaxKind.JSDocReturnTag:
            case SyntaxKind.JSDocThisTag:
            case SyntaxKind.JSDocTypeTag:
            case SyntaxKind.JSDocTemplateTag:
            case SyntaxKind.JSDocTypedefTag:
            case SyntaxKind.JSDocSeeTag:
            case SyntaxKind.JSDocThrowsTag:
            case SyntaxKind.JSDocSatisfiesTag:
            case SyntaxKind.JSDocImportTag:
                return true;
            default:
                return false;
        }
    }
}
