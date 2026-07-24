// PORT-NOTE: This file hosts, for the printer emit-body wave:
//   * A handful of small ast helpers that printer.go reaches into but whose C# equivalents are not (yet)
//     exposed as public ast utilities (IsVarLet/IsVarConst/IsVarUsing/IsInJsonFile/IsParseTreeNode/
//     GetExternalModuleName). These are faithful 1:1 reimplementations (each is a one-liner in
//     internal/ast/utilities.go). Relocate to Ts.Ast.AstUtilities when those helpers land there.
//   * NameGenerator.isUniqueLocalName / nextContainer (from namegenerator.go).
//   * The `getLiteralTextFlags` enum and `lineCharacterCache` type from internal/printer/utilities.go.
//     internal/printer/utilities.go is a *later* printer wave; the functions it exports that printer.go
//     calls are stubbed in PrinterStubs.cs. The two data types are hosted here because printer.go's
//     signatures reference them directly.

using System;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;

namespace Ts.Printer;

// Go: type getLiteralTextFlags int (internal/printer/utilities.go:18)
[Flags]
internal enum GetLiteralTextFlags
{
    None = 0,
    NeverAsciiEscape = 1 << 0,
    JsxAttributeEscape = 1 << 1,
    TerminateUnterminatedLiterals = 1 << 2,
    AllowNumericSeparator = 1 << 3,
}

public static partial class PrinterUtilities
{
    // Go: ast.IsVarLet (internal/ast/utilities.go:1252)
    internal static bool IsVarLet(Node node)
    {
        return (global::Ts.Ast.AstUtilities.GetCombinedNodeFlags(node) & NodeFlags.BlockScoped) == NodeFlags.Let;
    }

    // Go: ast.IsVarConst (internal/ast/utilities.go:1238)
    internal static bool IsVarConst(Node node)
    {
        return (global::Ts.Ast.AstUtilities.GetCombinedNodeFlags(node) & NodeFlags.BlockScoped) == NodeFlags.Const;
    }

    // Go: ast.IsVarUsing (internal/ast/utilities.go:1195)
    internal static bool IsVarUsing(Node node)
    {
        return (global::Ts.Ast.AstUtilities.GetCombinedNodeFlags(node) & NodeFlags.BlockScoped) == NodeFlags.Using;
    }

    // Go: ast.IsInJsonFile (internal/ast/utilities.go:1880)
    internal static bool IsInJsonFile(Node node)
    {
        return (node.Flags & NodeFlags.JsonFile) != 0;
    }

    // Go: ast.IsParseTreeNode (internal/ast/utilities.go:2632)
    internal static bool IsParseTreeNode(Node node)
    {
        return (node.Flags & NodeFlags.Synthesized) == 0;
    }

    // Go: ast.GetExternalModuleName (internal/ast/utilities.go:1884)
    internal static Node? GetExternalModuleName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportDeclaration:
                return node.ModuleSpecifier();
            case SyntaxKind.ImportEqualsDeclaration:
                if (node.AsImportEqualsDeclaration().ModuleReference!.Kind == SyntaxKind.ExternalModuleReference)
                {
                    return node.AsImportEqualsDeclaration().ModuleReference!.Expression();
                }
                return null;
            default:
                return null;
        }
    }

    // Go: nextContainer (internal/printer/namegenerator.go:355)
    private static Node? NextContainer(Node node)
    {
        var data = node.LocalsContainerData();
        if (data is not null)
        {
            return data.NextContainer;
        }
        return null;
    }

    // Go: isUniqueLocalName (internal/printer/namegenerator.go:363)
    internal static bool IsUniqueLocalName(string name, Node container)
    {
        var node = container;
        while (node is not null && global::Ts.Ast.AstUtilities.IsNodeDescendantOf(node, container) && node.LocalsContainerData() is not null)
        {
            var locals = node.LocalsContainerData()!.Locals;
            if (locals is not null)
            {
                // We conservatively include alias symbols to cover cases where they're emitted as locals
                if (locals.TryGetValue(name, out var local) && local is not null &&
                    (local.Flags & (SymbolFlags.Value | SymbolFlags.ExportValue | SymbolFlags.Alias)) != 0)
                {
                    return false;
                }
            }
            node = NextContainer(node);
        }
        return true;
    }
}
