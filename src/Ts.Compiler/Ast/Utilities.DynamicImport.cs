using System;

namespace Ts.Ast;

// Ported from tsgo internal/ast/utilities.go — the dynamic-import/require text scan used by the
// parser's collectExternalModuleReferences and by later import-resolution phases.
public static partial class AstUtilities
{
    private static readonly char[] s_importOrRequireFirstChars = ['i', 'r'];

    // ast/utilities.go — findImportOrRequire. Scans `text` from `start` for the next occurrence of
    // the words "import" or "require", returning its index and length (or (-1, 0) when none remain).
    // PORT-NOTE: Go scans UTF-8 bytes; here we scan the UTF-16 string. 'i'/'r' are ASCII (< 0x80) so
    // they never appear as surrogate/continuation code units — the occurrences found are identical,
    // and the returned index is a UTF-16 position consumed by GetNodeAtPosition (also UTF-16).
    private static (int index, int size) FindImportOrRequire(string text, int start)
    {
        int index = Math.Max(start, 0);
        int n = text.Length;
        while (index < n)
        {
            int next = text.IndexOfAny(s_importOrRequireFirstChars, index);
            if (next < 0)
            {
                break;
            }
            index = next;

            int size;
            string expected;
            if (text[index] == 'i')
            {
                size = 6;
                expected = "import";
            }
            else
            {
                size = 7;
                expected = "require";
            }
            if (index + size <= n && text.AsSpan(index, size).SequenceEqual(expected))
            {
                return (index, size);
            }
            index++;
        }

        return (-1, 0);
    }

    // ast/utilities.go — nodeContainsPosition.
    private static bool NodeContainsPosition(Node node, int position) =>
        node.Kind >= SyntaxKind.FirstNode && node.Pos <= position &&
        (position < node.End || (position == node.End && node.Kind == SyntaxKind.EndOfFile));

    // ast/utilities.go — GetNodeAtPosition. Descends the tree to the deepest node whose span contains
    // `position` (optionally including JSDoc), stopping at meta-properties.
    public static Node GetNodeAtPosition(SourceFile file, int position, bool includeJSDoc)
    {
        Node current = file.AsNode();
        while (true)
        {
            Node? child = null;
            if (includeJSDoc)
            {
                foreach (var jsdoc in current.JSDoc(file) ?? [])
                {
                    if (NodeContainsPosition(jsdoc, position))
                    {
                        child = jsdoc;
                        break;
                    }
                }
            }
            if (child == null)
            {
                current.ForEachChild(node =>
                {
                    if (NodeContainsPosition(node, position))
                    {
                        child = node;
                        return true;
                    }
                    return false;
                });
            }
            if (child == null || NodeTests.IsMetaProperty(child))
            {
                return current;
            }
            current = child;
        }
    }

    // ast/utilities.go:1315 — IsLiteralImportTypeNode.
    public static bool IsLiteralImportTypeNode(Node node) =>
        NodeTests.IsImportTypeNode(node) && NodeTests.IsLiteralTypeNode(node.AsImportTypeNode().Argument!) &&
        NodeTests.IsStringLiteral(node.AsImportTypeNode().Argument!.AsLiteralTypeNode().Literal!);

    // ast/utilities.go — ForEachDynamicImportOrRequireCall. Text-scans for import()/require() calls
    // (and, when includeTypeSpaceImports, literal `import("...")` type nodes) and invokes cb for each;
    // returns true as soon as cb returns true.
    public static bool ForEachDynamicImportOrRequireCall(
        SourceFile file,
        bool includeTypeSpaceImports,
        bool requireStringLiteralLikeArgument,
        Func<Node, Node, bool> cb)
    {
        bool isJavaScriptFile = IsInJSFile(file.AsNode());
        var (lastIndex, size) = FindImportOrRequire(file.Text(), 0);
        while (lastIndex >= 0)
        {
            Node node = GetNodeAtPosition(file, lastIndex, isJavaScriptFile && includeTypeSpaceImports);
            if (isJavaScriptFile && IsRequireCall(node, requireStringLiteralLikeArgument))
            {
                if (cb(node, node.Arguments()![0]))
                {
                    return true;
                }
            }
            else if (IsImportCall(node) && (node.Arguments()?.Length ?? 0) > 0 && (!requireStringLiteralLikeArgument || IsStringLiteralLike(node.Arguments()![0])))
            {
                if (cb(node, node.Arguments()![0]))
                {
                    return true;
                }
            }
            else if (includeTypeSpaceImports && IsLiteralImportTypeNode(node))
            {
                if (cb(node, node.AsImportTypeNode().Argument!.AsLiteralTypeNode().Literal!))
                {
                    return true;
                }
            }
            // skip past import/require
            lastIndex += size;
            (lastIndex, size) = FindImportOrRequire(file.Text(), lastIndex);
        }
        return false;
    }
}
