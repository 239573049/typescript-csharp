// Go: internal/ast/ast.go — SourceFile.GetNameTable (:2716-2747) and its literalIsName
// helpers (utilities.go:4472-4483). Listed as deferred in SourceFile.cs's SCOPE note;
// landed here as the phase-8 completions port needs it (getJSCompletionEntries).

using System.Collections.Generic;
using System.Threading;

namespace Ts.Ast;

public sealed partial class SourceFile
{
    private readonly Lock nameTableMu = new();
    private Dictionary<string, int>? nameTable;

    // ast.go:2716-2747 — GetNameTable returns a map of all names in the file to their positions.
    // If the name appears more than once, the value is -1. (Go sync.Once → double-checked lock.)
    public Dictionary<string, int> GetNameTable()
    {
        if (nameTable == null)
        {
            lock (nameTableMu)
            {
                if (nameTable == null)
                {
                    var table = new Dictionary<string, int>(IdentifierCount, System.StringComparer.Ordinal);

                    bool Walk(Node node)
                    {
                        if (NodeTests.IsIdentifier(node) && !AstUtilities.IsTagName(node) && node.Text().Length != 0 ||
                            AstUtilities.IsStringOrNumericLiteralLike(node) && AstUtilities.LiteralIsName(node) ||
                            NodeTests.IsPrivateIdentifier(node))
                        {
                            var text = node.Text();
                            if (table.ContainsKey(text))
                            {
                                table[text] = -1;
                            }
                            else
                            {
                                table[text] = node.Pos;
                            }
                        }

                        node.ForEachChild(Walk);
                        var jsdocNodes = node.JSDoc(this);
                        foreach (var jsdoc in jsdocNodes ?? [])
                        {
                            jsdoc.ForEachChild(Walk);
                        }
                        return false;
                    }
                    ForEachChild(Walk);

                    nameTable = table;
                }
            }
        }
        return nameTable;
    }
}

public static partial class AstUtilities
{
    // utilities.go:4472-4477 — literalIsName.
    public static bool LiteralIsName(Node node)
    {
        return IsDeclarationName(node) ||
            node.Parent!.Kind == SyntaxKind.ExternalModuleReference ||
            IsArgumentOfElementAccessExpression(node) ||
            IsLiteralComputedPropertyDeclarationName(node);
    }

    // utilities.go:4479-4483 — isArgumentOfElementAccessExpression.
    private static bool IsArgumentOfElementAccessExpression(Node? node)
    {
        return node != null && node.Parent != null &&
            node.Parent.Kind == SyntaxKind.ElementAccessExpression &&
            node.Parent.AsElementAccessExpression().ArgumentExpression == node;
    }
}
