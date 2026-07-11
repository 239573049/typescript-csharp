// Ported from tsgo internal/scanner/utilities.go
//
// The AST-dependent functions (IdentifierToKeywordKind, GetSourceTextOfNodeFromSourceFile,
// GetTextOfNodeFromSourceText, GetTextOfNode, GetTextOfJSDocComment, DeclarationNameToString)
// were deferred from phase 2 (they consume ast.Node/ast.SourceFile navigation) and land here
// now that the ast package is available (phase 3).

using System.Collections.Generic;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Scanner;

using StringUtil = Ts.StringUtil.StringUtil;

public sealed partial class Scanner
{
    // Ported from tsgo internal/scanner/utilities.go:13 (tokenIsIdentifierOrKeyword)
    internal static bool TokenIsIdentifierOrKeyword(SyntaxKind token)
    {
        return token >= SyntaxKind.Identifier;
    }

    // Ported from tsgo internal/scanner/utilities.go:17 (IdentifierToKeywordKind)
    public static SyntaxKind IdentifierToKeywordKind(Identifier node)
    {
        // Go map read of a missing key yields the zero value (KindUnknown); plan/05 §7.1.
        return textToKeyword.GetValueOrDefault(node.Text);
    }

    // Ported from tsgo internal/scanner/utilities.go:21 (GetSourceTextOfNodeFromSourceFile)
    public static string GetSourceTextOfNodeFromSourceFile(SourceFile sourceFile, Node node, bool includeTrivia)
    {
        return GetTextOfNodeFromSourceText(sourceFile.Text(), node, includeTrivia);
    }

    // Ported from tsgo internal/scanner/utilities.go:25 (GetTextOfNodeFromSourceText)
    public static string GetTextOfNodeFromSourceText(string sourceText, Node node, bool includeTrivia)
    {
        if (AstUtilities.NodeIsMissing(node))
        {
            return "";
        }
        var pos = node.Pos;
        if (!includeTrivia)
        {
            pos = SkipTrivia(sourceText, pos);
        }
        var text = sourceText.Substring(pos, node.End - pos); // Go sourceText[pos:node.End()]
        if ((node.Flags & NodeFlags.ReparserTransformedLiteral) != 0)
        {
            // This is similar to `getLiteralTextOfNode` in the printer, but without the context of an `emitContext` to provide overrides
            if (NodeTests.IsStringLiteral(node))
            {
                // TokenFlags (the enum) is fully qualified: the Scanner class has a
                // TokenFlags() accessor method that otherwise shadows the type name.
                if ((node.AsStringLiteral().TokenFlags & Ts.Ast.TokenFlags.SingleQuote) != 0)
                {
                    return "'" + text + "'";
                }
                return "\"" + text + "\"";
            }
            else if (NodeTests.IsIdentifier(node))
            {
                return node.Text();
            }
            // Only the above node kinds are currently transformed into one another by the reparser, requiring the textual remapping.
            // (Any reamppings done by emit transforms are handled by `getLiteralTextOfNode` in the printer)
            // Fail on any other kinds.
            Ts.Core.Debug.FailBadSyntaxKind(node, "Unexpected reparser-transformed node kind");
        }
        // if (isJSDocTypeExpressionOrChild(node)) {
        //     // strip space + asterisk at line start
        //     text = text.split(/\r\n|\n|\r/).map(line => line.replace(/^\s*\*/, "").trimStart()).join("\n");
        // }
        return text;
    }

    // Ported from tsgo internal/scanner/utilities.go:56 (GetTextOfNode)
    public static string GetTextOfNode(Node node)
    {
        return GetSourceTextOfNodeFromSourceFile(AstUtilities.GetSourceFileOfNode(node)!, node, false /*includeTrivia*/);
    }

    // Ported from tsgo internal/scanner/utilities.go:60 (GetTextOfJSDocComment)
    public static string GetTextOfJSDocComment(NodeList? comment)
    {
        if (comment == null)
        {
            return "";
        }
        var b = new StringBuilder();
        foreach (var n in comment.Nodes)
        {
            switch (n.Kind)
            {
                case SyntaxKind.JSDocText:
                    b.Append(n.Text());
                    break;
                case SyntaxKind.JSDocLink:
                case SyntaxKind.JSDocLinkCode:
                case SyntaxKind.JSDocLinkPlain:
                    b.Append(GetTextOfNode(n));
                    break;
            }
        }
        // PORT-NOTE: strings.TrimRightFunc(s, unicode.IsSpace) → string.TrimEnd() (Char.IsWhiteSpace).
        return b.ToString().TrimEnd();
    }

    // Ported from tsgo internal/scanner/utilities.go:76 (DeclarationNameToString)
    public static string DeclarationNameToString(Node? name)
    {
        if (name == null || name.Pos == name.End)
        {
            return "(Missing)";
        }
        return GetTextOfNode(name);
    }

    // Ported from tsgo internal/scanner/utilities.go:83 (IsIdentifierText)
    public static bool IsIdentifierText(string name, LanguageVariant languageVariant)
    {
        var (ch, size) = StringUtil.DecodeRuneAt(name, 0);
        if (!IsIdentifierStart(ch))
        {
            return false;
        }
        for (int i = size; i < name.Length;)
        {
            (ch, size) = StringUtil.DecodeRuneAt(name, i);
            if (!IsIdentifierPartEx(ch, languageVariant))
            {
                return false;
            }
            i += size;
        }
        return true;
    }

    // Ported from tsgo internal/scanner/utilities.go:98 (IsIntrinsicJsxName)
    public static bool IsIntrinsicJsxName(string name)
    {
        return name.Length != 0 && ((name[0] >= 'a' && name[0] <= 'z') || name.Contains('-'));
    }
}
