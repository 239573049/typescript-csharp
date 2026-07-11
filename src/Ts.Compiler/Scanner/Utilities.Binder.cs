using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Scanner;

using StringUtil = Ts.StringUtil.StringUtil;

// Ported from tsgo internal/scanner/scanner.go — the error-span helpers needed by the binder
// (GetRangeOfTokenAtPosition / GetErrorRangeForNode) plus their missing ECMA line-map and
// arrow/JSDoc sub-helpers. Deferred from phase 2 (they consume ast.Node/ast.SourceFile
// navigation); landed here for phase 4. 逐函数直译; Go comments kept verbatim. Positions are
// UTF-16 code unit offsets (plan/01); `core.TextRange` → new TextRange(pos, end).
public sealed partial class Scanner
{
    // Go: scanner.go:2521-2524
    public static TextRange GetRangeOfTokenAtPosition(SourceFile sourceFile, int pos)
    {
        var s = GetScannerForSourceFile(sourceFile, pos);
        return new TextRange(s.tokenStart, s.pos);
    }

    // Go: scanner.go:2542-2554
    private static TextRange GetErrorRangeForArrowFunction(SourceFile sourceFile, Node node)
    {
        var pos = SkipTrivia(sourceFile.Text(), node.Pos);
        var body = node.Body();
        if (body != null && body.Kind == SyntaxKind.Block)
        {
            var startLine = GetECMALineOfPosition(sourceFile, body.Pos);
            var endLine = GetECMALineOfPosition(sourceFile, body.End);
            if (startLine < endLine)
            {
                // The arrow function spans multiple lines, make the error span be the first line, inclusive.
                return new TextRange(pos, GetECMAEndLinePosition(sourceFile, startLine) + 1);
            }
        }
        return new TextRange(pos, node.End);
    }

    // Go: scanner.go:2556-2586
    private static Node? FindOriginatingJSDocSatisfiesTag(SourceFile sourceFile, Node node)
    {
        var targetType = node.AsSatisfiesExpression().Type;
        if ((targetType!.Flags & NodeFlags.Reparsed) == 0)
        {
            return null;
        }
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if ((current.Flags & NodeFlags.HasJSDoc) == 0)
            {
                continue;
            }
            Node? firstSatisfiesTag = null;
            foreach (var jsDoc in current.EagerJSDoc(sourceFile) ?? [])
            {
                var tags = jsDoc.AsJSDoc().Tags;
                if (tags != null)
                {
                    foreach (var tag in tags.Nodes)
                    {
                        if (!NodeTests.IsJSDocSatisfiesTag(tag))
                        {
                            continue;
                        }
                        if (firstSatisfiesTag == null)
                        {
                            firstSatisfiesTag = tag;
                        }
                        var typeExpr = tag.AsJSDocSatisfiesTag().TypeExpression;
                        if (typeExpr != null)
                        {
                            var t = typeExpr.Type();
                            if (t != null && t.Loc == targetType.Loc)
                            {
                                return tag;
                            }
                        }
                    }
                }
                return firstSatisfiesTag;
            }
            return firstSatisfiesTag;
        }
        return null;
    }

    // Go: scanner.go:2588-2654
    public static TextRange GetErrorRangeForNode(SourceFile sourceFile, Node node)
    {
        Node? errorNode = node;
        switch (node.Kind)
        {
            case SyntaxKind.SourceFile:
            {
                var pos = SkipTrivia(sourceFile.Text(), 0);
                if (pos == sourceFile.Text().Length)
                {
                    return new TextRange(0, 0);
                }
                return GetRangeOfTokenAtPosition(sourceFile, pos);
            }
            // This list is a work in progress. Add missing node kinds to improve their error spans
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
                if ((node.Flags & NodeFlags.Reparsed) != 0)
                {
                    errorNode = node;
                    break;
                }
                // PORT-NOTE: Go `fallthrough` into the declaration-name case below.
                errorNode = AstUtilities.GetNameOfDeclaration(node);
                break;
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.BindingElement:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.EnumMember:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.NamespaceImport:
                errorNode = AstUtilities.GetNameOfDeclaration(node);
                break;
            case SyntaxKind.ClassExpression:
                errorNode = node.Name();
                break;
            case SyntaxKind.ArrowFunction:
                return GetErrorRangeForArrowFunction(sourceFile, node);
            case SyntaxKind.CaseClause:
            case SyntaxKind.DefaultClause:
            {
                var start = SkipTrivia(sourceFile.Text(), node.Pos);
                var end = node.End;
                var statements = node.Statements();
                if (statements != null && statements.Length != 0)
                {
                    end = statements[0].Pos;
                }
                return new TextRange(start, end);
            }
            case SyntaxKind.ReturnStatement:
            case SyntaxKind.YieldExpression:
            {
                var pos = SkipTrivia(sourceFile.Text(), node.Pos);
                return GetRangeOfTokenAtPosition(sourceFile, pos);
            }
            case SyntaxKind.SatisfiesExpression:
            {
                var jsDocSatisfiesTag = FindOriginatingJSDocSatisfiesTag(sourceFile, node);
                if (jsDocSatisfiesTag != null)
                {
                    var pos = SkipTrivia(sourceFile.Text(), jsDocSatisfiesTag.TagName()!.Pos);
                    return GetRangeOfTokenAtPosition(sourceFile, pos);
                }
                var pos2 = SkipTrivia(sourceFile.Text(), node.AsSatisfiesExpression().Expression!.End);
                return GetRangeOfTokenAtPosition(sourceFile, pos2);
            }
            case SyntaxKind.Constructor:
            {
                if ((node.Flags & NodeFlags.Reparsed) != 0)
                {
                    errorNode = node;
                    break;
                }
                var scanner = GetScannerForSourceFile(sourceFile, node.Pos);
                var start = scanner.TokenStart();
                for (; scanner.Token() != SyntaxKind.ConstructorKeyword && scanner.Token() != SyntaxKind.StringLiteral && scanner.Token() != SyntaxKind.EndOfFile;)
                {
                    scanner.Scan();
                }
                return new TextRange(start, scanner.TokenEnd());
            }
        }
        if (errorNode == null)
        {
            // If we don't have a better node, then just set the error on the first token of
            // construct.
            return GetRangeOfTokenAtPosition(sourceFile, node.Pos);
        }
        var pos3 = errorNode.Pos;
        if (!AstUtilities.NodeIsMissing(errorNode) && !NodeTests.IsJsxText(errorNode))
        {
            pos3 = SkipTrivia(sourceFile.Text(), pos3);
        }
        return new TextRange(pos3, errorNode.End);
    }

    // Go: scanner.go:2656-2671
    public static int ComputeLineOfPosition(int[] lineStarts, int pos)
    {
        var low = 0;
        var high = lineStarts.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var value = lineStarts[middle];
            if (value < pos)
            {
                low = middle + 1;
            }
            else if (value > pos)
            {
                high = middle - 1;
            }
            else
            {
                return middle;
            }
        }
        return low - 1;
    }

    // Go: scanner.go:2673-2675
    public static int[] GetECMALineStarts(SourceFileLike sourceFile)
    {
        return sourceFile.ECMALineMap();
    }

    // Go: scanner.go:2677-2680
    public static int GetECMALineOfPosition(SourceFileLike sourceFile, int pos)
    {
        var lineMap = GetECMALineStarts(sourceFile);
        return ComputeLineOfPosition(lineMap, pos);
    }

    // Go: scanner.go:2682-2690
    // GetECMALineAndUTF16CharacterOfPosition returns the 0-based line number and the
    // UTF-16 code unit offset from the start of that line for the given byte position.
    // Uses ECMAScript line separators (LF, CR, CRLF, LS, PS).
    // PORT-NOTE: Go's `character core.UTF16Offset` → int (positions are UTF-16 code units,
    // plan/01 §UTF-16); core.UTF16Len(text[lineMap[line]:pos]) == (pos - lineMap[line]) because a
    // C# string index already IS a UTF-16 code-unit offset, so the substring's Length equals that
    // difference. Kept as a substring-Length call to mirror the Go control flow verbatim.
    public static (int Line, int Character) GetECMALineAndUTF16CharacterOfPosition(SourceFileLike sourceFile, int pos)
    {
        var lineMap = GetECMALineStarts(sourceFile);
        var line = ComputeLineOfPosition(lineMap, pos);
        var character = CoreUtil.UTF16Len(sourceFile.Text().Substring(lineMap[line], pos - lineMap[line]));
        return (line, character);
    }

    // Go: scanner.go:2722-2727
    // GetECMAPositionOfLineAndByteOffset converts a 0-based line number and byte offset
    // from line start back to an absolute byte position in the source text.
    // Uses ECMAScript line separators.
    public static int GetECMAPositionOfLineAndByteOffset(SourceFileLike sourceFile, int line, int byteOffset)
    {
        return ComputePositionOfLineAndByteOffset(GetECMALineStarts(sourceFile), line, byteOffset);
    }

    // Go: scanner.go:2729-2736
    // ComputePositionOfLineAndByteOffset computes a byte position from a line and
    // raw byte offset from the line start. This is a simple addition with validation.
    public static int ComputePositionOfLineAndByteOffset(int[] lineStarts, int line, int byteOffset)
    {
        if (line < 0 || line >= lineStarts.Length)
        {
            throw new System.InvalidOperationException($"Bad line number. Line: {line}, lineStarts.length: {lineStarts.Length}.");
        }
        return lineStarts[line] + byteOffset;
    }

    // Go: scanner.go:2703-2712
    public static int GetECMAEndLinePosition(SourceFile sourceFile, int line)
    {
        var pos = GetECMALineStarts(sourceFile)[line];
        while (true)
        {
            var (ch, size) = StringUtil.DecodeRuneAt(sourceFile.Text(), pos);
            if (size == 0 || StringUtil.IsLineBreak(ch))
            {
                return pos - 1;
            }
            pos += size;
        }
    }
}
