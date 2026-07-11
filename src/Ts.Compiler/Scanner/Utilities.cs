// Ported from tsgo internal/scanner/utilities.go
//
// SCOPE (phase 2): only the functions that do not depend on ast.Node / ast.SourceFile
// navigation are ported here. Deferred to phase 3 (with ast/parser), because they consume
// node accessors (Pos/End/Text/Flags), ast.NodeIsMissing, ast.IsStringLiteral,
// ast.GetSourceFileOfNode, ast.Identifier, ast.NodeList, etc. which are not yet available:
//   - IdentifierToKeywordKind
//   - GetSourceTextOfNodeFromSourceFile / GetTextOfNodeFromSourceText
//   - GetTextOfNode / GetTextOfJSDocComment
//   - DeclarationNameToString

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
