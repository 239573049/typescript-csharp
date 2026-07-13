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

    // utilities.go:2152
    public static Node? GetEnclosingBlockScopeContainer(Node node)
    {
        return FindAncestor(node.Parent, current => IsBlockScope(current, current.Parent));
    }

    // utilities.go:2158
    public static bool IsBlockScope(Node node, Node? parentNode)
    {
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
            case SyntaxKind.CaseBlock:
            case SyntaxKind.CatchClause:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ForStatement:
            case SyntaxKind.ForInStatement:
            case SyntaxKind.ForOfStatement:
            case SyntaxKind.Constructor:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.ClassStaticBlockDeclaration:
                return true;
            case SyntaxKind.Block:
                // function block is not considered block-scope container
                // see comment in binder.ts: bind(...), case for SyntaxKind.Block
                return !IsFunctionLikeOrClassStaticBlockDeclaration(parentNode);
        }
        return false;
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

    // utilities.go:1200
    public static Node? GetJSDocDeprecatedTag(Node node)
    {
        foreach (var jsdoc in node.JSDoc(null) ?? System.Array.Empty<Node>())
        {
            var tags = jsdoc.AsJSDoc().Tags;
            if (tags != null)
            {
                foreach (var tag in tags.Nodes)
                {
                    if (IsJSDocDeprecatedTag(tag))
                    {
                        return tag;
                    }
                }
            }
        }
        return null;
    }

    // utilities.go:1223 — core logic for IsDeprecatedDeclaration, parameterized on pre-computed combined
    // flags so the checker can supply cached flags.
    public static bool IsDeprecatedDeclarationWithCachedFlags(Node declaration, NodeFlags combinedFlags)
    {
        if ((combinedFlags & NodeFlags.PossiblyContainsDeprecatedTag) == 0)
        {
            return false;
        }
        // Walk up to find the node that directly has the flag, since JSDoc is
        // attached to that node (e.g. VariableStatement, not VariableDeclaration).
        for (var n = declaration; n != null; n = n.Parent)
        {
            if ((n.Flags & NodeFlags.PossiblyContainsDeprecatedTag) != 0)
            {
                return GetJSDocDeprecatedTag(n) != null;
            }
        }
        return false;
    }

    // utilities.go:1637
    public static bool IsAmbientModuleSymbolName(string s)
    {
        return s.StartsWith('"') && s.EndsWith('"');
    }

    // utilities.go:3566
    public static bool IsTypeDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TypeParameter:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.EnumDeclaration:
                return true;
            case SyntaxKind.ImportClause:
                return node.IsTypeOnly();
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ExportSpecifier:
                return node.Parent!.Parent!.IsTypeOnly();
            default:
                return false;
        }
    }

    // utilities.go:3997
    public static bool IsJSDocNameReferenceContext(Node node)
    {
        return (node.Flags & NodeFlags.JSDoc) != 0 && FindAncestor(node, static n => IsJSDocNameReference(n) || IsJSDocLinkLike(n)) != null;
    }

    // utilities.go:4004
    // GetJSDocRoot returns the containing JSDoc node for a node inside a JSDoc comment.
    public static Node? GetJSDocRoot(Node node)
    {
        return FindAncestor(node.Parent, static n => n.Kind == SyntaxKind.JSDoc);
    }

    // utilities.go:4010
    // GetJSDocHost returns the declaration that the JSDoc comment containing the given node is attached to.
    public static Node? GetJSDocHost(Node node)
    {
        var jsDoc = GetJSDocRoot(node);
        if (jsDoc == null)
        {
            return null;
        }
        return jsDoc.Parent;
    }

    // utilities.go:4021
    // GetHostSignatureFromJSDoc returns the function-like declaration that hosts the JSDoc comment
    // containing the given node. This is used to resolve @link references to parameters.
    public static Node? GetHostSignatureFromJSDoc(Node node)
    {
        var host = GetJSDocHost(node);
        if (host == null)
        {
            return null;
        }
        // !!! Strada's getEffectiveJSDocHost applies JS assignment pattern transforms (getSourceOfAssignment, getSourceOfDefaultedAssignment, etc.) not yet ported
        if (IsPropertySignatureDeclaration(host) && host.Type() != null && IsFunctionLike(host.Type()))
        {
            return host.Type();
        }
        if (IsFunctionLike(host))
        {
            return host;
        }
        return null;
    }

    // utilities.go:4039
    public static Node? GetNextJSDocCommentLocation(Node node)
    {
        var parent = node.Parent;
        if (parent != null)
        {
            switch (parent.Kind)
            {
                case SyntaxKind.PropertyAssignment:
                case SyntaxKind.ExportAssignment:
                case SyntaxKind.PropertyDeclaration:
                case SyntaxKind.VariableDeclaration:
                case SyntaxKind.SatisfiesExpression:
                case SyntaxKind.ReturnStatement:
                case SyntaxKind.VariableStatement:
                case SyntaxKind.ExpressionStatement:
                    return parent;
                case SyntaxKind.VariableDeclarationList:
                    if (ReferenceEquals(parent.AsVariableDeclarationList().Declarations!.Nodes[0], node))
                    {
                        return parent;
                    }
                    break;
            }
        }
        return null;
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
