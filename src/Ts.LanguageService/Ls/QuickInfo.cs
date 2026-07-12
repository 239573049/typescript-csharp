// Ported from tsgo internal/ls/hover.go (quick-info foundation subset).

using System;
using System.Threading;

namespace Ts.Ls;

public sealed record QuickInfo(string DisplayString, global::Ts.Core.TextRange TextSpan);

public sealed partial class LanguageService
{
    public QuickInfo? GetQuickInfoAtPosition(
        string fileName,
        int position,
        CancellationToken cancellationToken = default)
    {
        var node = GetNodeAtPosition(fileName, position, out var file);
        if (node.Kind == global::Ts.Ast.SyntaxKind.SourceFile)
        {
            return null;
        }

        var rangeNode = GetNodeForQuickInfo(node);
        var (checker, done) = host.GetTypeCheckerForFile(cancellationToken, file);
        try
        {
            // PORT-NOTE: The faithful next steps in hover.go are
            //   checker.GetSymbolAtLocation(rangeNode)
            //   checker.GetTypeOfSymbolAtLocation(symbol, rangeNode)
            //   checker.TypeToStringEx(type, ...)
            // As of this LS foundation wave all three C# members are internal to
            // Ts.Compiler, and GetTypeOfSymbolAtLocation is still a throwing checker
            // stub. Reflection is forbidden by the AOT rules, so this path is
            // intentionally skip-gated until the checker exposes its query surface.
            _ = checker;
            return null;
        }
        finally
        {
            done();
        }
    }

    // hover.go:getNodeForQuickInfo
    private static global::Ts.Ast.Node GetNodeForQuickInfo(global::Ts.Ast.Node node)
    {
        if (node.Parent is null)
        {
            return node;
        }
        if (global::Ts.Ast.NodeTests.IsNewExpression(node.Parent) && node.Pos == node.Parent.Pos)
        {
            return node.Parent.AsNewExpression().Expression!; // Go unconditional dereference.
        }
        if (global::Ts.Ast.NodeTests.IsNamedTupleMember(node.Parent) && node.Pos == node.Parent.Pos)
        {
            return node.Parent;
        }
        if (IsImportMeta(node.Parent) && node.Parent.Name() == node)
        {
            return node.Parent;
        }
        if (global::Ts.Ast.NodeTests.IsJsxNamespacedName(node.Parent))
        {
            return node.Parent;
        }
        return node;
    }

    // ast/utilities.go:IsImportMeta. The C# AST utility has not exposed this
    // small helper yet, so it remains local to the only LS call site in scope.
    private static bool IsImportMeta(global::Ts.Ast.Node node)
    {
        return node.Kind == global::Ts.Ast.SyntaxKind.MetaProperty &&
            node.AsMetaProperty().KeywordToken == global::Ts.Ast.SyntaxKind.ImportKeyword &&
            string.Equals(node.AsMetaProperty().Name()!.AsIdentifier().Text, "meta", StringComparison.Ordinal);
    }
}
