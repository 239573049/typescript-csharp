using Ts.Ast;
using Ts.Core;

namespace Ts.Scanner;

// Go: internal/scanner/scanner.go — GetScannerForSourceFile (:2506) and GetTokenPosOfNode
// (:2526). Referenced but not yet implemented in Scanner.cs's opening comment; landed here as
// the phase-3 astnav port's first consumer needs them (positioning a scanner at an arbitrary
// offset in a parsed file, and computing a node's token start skipping leading trivia/JSDoc).
public sealed partial class Scanner
{
    // scanner.go:2506
    public static Scanner GetScannerForSourceFile(SourceFile sourceFile, int pos)
    {
        var s = new Scanner();
        s.text = sourceFile.Text();
        s.pos = pos;
        s.end = s.text.Length;
        s.languageVariant = sourceFile.LanguageVariant;
        s.Scan();
        return s;
    }

    // scanner.go:2526
    public static int GetTokenPosOfNode(Node node, SourceFile sourceFile, bool includeJSDoc)
    {
        // With nodes that have no width (i.e. 'Missing' nodes), we actually *don't*
        // want to skip trivia because this will launch us forward to the next token.
        if (AstUtilities.NodeIsMissing(node))
        {
            return node.Pos;
        }
        if (AstUtilities.IsJSDocNode(node) || node.Kind == SyntaxKind.JsxText)
        {
            // JsxText cannot actually contain comments, even though the scanner will think it sees comments
            return SkipTriviaEx(sourceFile.Text(), node.Pos, new SkipTriviaOptions { StopAtComments = true });
        }
        var jsDoc = node.JSDoc(sourceFile);
        if (includeJSDoc && (jsDoc?.Length ?? 0) > 0)
        {
            return GetTokenPosOfNode(jsDoc![0], sourceFile, false /*includeJSDoc*/);
        }
        return SkipTriviaEx(sourceFile.Text(), node.Pos, new SkipTriviaOptions { InJSDoc = (node.Flags & NodeFlags.JSDoc) != 0 });
    }
}
