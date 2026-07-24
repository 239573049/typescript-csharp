// Ported from tsgo internal/ls/jsdoc.go (JSDoc tag rendering for the LS API surface).
// GetSymbolDocumentationComment (jsdoc.go:27-45) lands with the hover.go main body — it delegates
// to getDocumentationFromDeclaration, which is part of that slice.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ts.Ast;

namespace Ts.Ls;

// jsdoc.go:15-20 — JSDocTagInfo mirrors Strada's `JSDocTagInfo`, but renders the tag's text as a
// plain string instead of `SymbolDisplayPart[]`.
public sealed record JSDocTagInfo(string Name, string Text);

public static class JsDocInfo
{
    // jsdoc.go:47-80 — GetSymbolJSDocTags collects a symbol's JSDoc tags. It backs the API's
    // Symbol.getJsDocTags and mirrors Strada's getJsDocTagsFromDeclarations, except each tag's text
    // is rendered as a plain string rather than SymbolDisplayPart[]. Tags with no text have an
    // empty Text field.
    public static JSDocTagInfo[] GetSymbolJSDocTags(Symbol? symbol)
    {
        if (symbol == null)
        {
            return [];
        }
        List<JSDocTagInfo>? infos = null;
        var seen = new HashSet<Node>();
        foreach (var decl in symbol.Declarations ?? [])
        {
            if (decl == null)
            {
                continue;
            }
            if (!seen.Add(decl))
            {
                continue;
            }
            var tags = DeclarationJSDocTags(decl);
            if (tags == null)
            {
                continue;
            }
            // Skip comments containing @typedef/@callback since they're not associated with a
            // particular declaration, unless they also carry @param/@return (treated as local docs).
            var hasTypedef = tags.Any(t => t.Kind == SyntaxKind.JSDocTypedefTag || t.Kind == SyntaxKind.JSDocCallbackTag);
            var hasParamOrReturn = tags.Any(t => t.Kind == SyntaxKind.JSDocParameterTag || t.Kind == SyntaxKind.JSDocReturnTag);
            if (hasTypedef && !hasParamOrReturn)
            {
                continue;
            }
            foreach (var tag in tags)
            {
                infos ??= new List<JSDocTagInfo>();
                infos.Add(new JSDocTagInfo(tag.TagName()!.AsIdentifier().Text, GetJSDocTagText(tag)));
            }
        }
        return infos?.ToArray() ?? [];
    }

    // jsdoc.go:82-98 — declarationJSDocTags returns the JSDoc tags associated with a declaration,
    // walking the JSDoc comment location chain like the checker's getAllJSDocTags.
    internal static Node[]? DeclarationJSDocTags(Node node)
    {
        if ((node.Flags & NodeFlags.JSDoc) == 0)
        {
            for (var current = node; current != null; current = AstUtilities.GetNextJSDocCommentLocation(current))
            {
                var jsdocs = current.JSDoc(null);
                if (jsdocs is null || jsdocs.Length == 0)
                {
                    continue;
                }
                var lastJSDoc = jsdocs[^1].AsJSDoc();
                if (lastJSDoc.Tags != null)
                {
                    return lastJSDoc.Tags.Nodes;
                }
            }
        }
        return null;
    }

    // jsdoc.go:100-161 — getJSDocTagText renders the text of a single JSDoc tag as a plain string,
    // mirroring Strada's getCommentDisplayParts collapsed from SymbolDisplayPart[] to a string.
    internal static string GetJSDocTagText(Node tag)
    {
        var comment = global::Ts.Scanner.Scanner.GetTextOfJSDocComment(tag.CommentList());
        string AddComment(string s) => comment.Length == 0 ? s : s + " " + comment;
        switch (tag.Kind)
        {
            case SyntaxKind.JSDocThrowsTag:
            {
                if (tag.AsJSDocThrowsTag().TypeExpression is { } te)
                {
                    return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(te));
                }
                return comment;
            }
            case SyntaxKind.JSDocImplementsTag:
                return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(tag.AsJSDocImplementsTag().ClassName!));
            case SyntaxKind.JSDocAugmentsTag:
                return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(tag.AsJSDocAugmentsTag().ClassName!));
            case SyntaxKind.JSDocTemplateTag:
            {
                var templateTag = tag.AsJSDocTemplateTag();
                var b = new StringBuilder();
                if (templateTag.Constraint != null)
                {
                    b.Append(global::Ts.Scanner.Scanner.GetTextOfNode(templateTag.Constraint));
                }
                if (templateTag.TypeParameters != null)
                {
                    for (var i = 0; i < templateTag.TypeParameters.Nodes.Length; i++)
                    {
                        if (i == 0 && b.Length != 0)
                        {
                            b.Append(' ');
                        }
                        if (i != 0)
                        {
                            b.Append(", ");
                        }
                        b.Append(global::Ts.Scanner.Scanner.GetTextOfNode(templateTag.TypeParameters.Nodes[i]));
                    }
                }
                if (comment.Length != 0)
                {
                    if (b.Length != 0)
                    {
                        b.Append(' ');
                    }
                    b.Append(comment);
                }
                return b.ToString();
            }
            case SyntaxKind.JSDocTypeTag:
                return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(tag.AsJSDocTypeTag().TypeExpression!));
            case SyntaxKind.JSDocSatisfiesTag:
                return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(tag.AsJSDocSatisfiesTag().TypeExpression!));
            case SyntaxKind.JSDocSeeTag:
            {
                if (tag.AsJSDocSeeTag().NameExpression is { } ne)
                {
                    return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(ne));
                }
                return comment;
            }
            case SyntaxKind.JSDocParameterTag:
            case SyntaxKind.JSDocPropertyTag:
            {
                if (tag.Name() is { } name)
                {
                    return AddComment(global::Ts.Scanner.Scanner.GetTextOfNode(name));
                }
                return comment;
            }
            default:
                return comment;
        }
    }
}
