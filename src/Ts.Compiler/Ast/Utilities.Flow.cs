// Ported from tsgo internal/ast/utilities.go — the flow-narrowing-facing slice of the ast utility
// surface pulled in for the phase-5 checker flow wave (Checker.Flow.cs / Checker.Flow.Support.cs).
// Ported 逐函数直译 in Go source order; Go comments and `// !!!` markers are kept verbatim.
// Guards (IsSourceFile, IsModuleBlock, IsBlock, IsIdentifier, …) resolve to Ts.Ast.NodeTests via
// `using static`; composite predicates/accessors already ported live in AstUtilities (Utilities.cs)
// / NodeAccessors and are called unqualified. plan/05 §8.1 — positions/text are UTF-16 code units.
// Go implicit non-nil derefs become `!` (NRE ↔ Go panic, plan/05 §4.3).

using System;
using Ts.Core;
using static Ts.Ast.NodeTests;

namespace Ts.Ast;

public static partial class AstUtilities
{
    // Go: utilities.go:1128-1130
    public static bool IsFunctionOrModuleBlock(Node node)
    {
        return IsSourceFile(node) || IsModuleBlock(node) || IsBlock(node) && IsFunctionLike(node.Parent);
    }

    // Go: utilities.go:1243-1248
    public static bool IsVarConstLike(Node node)
    {
        switch (GetCombinedNodeFlags(node) & NodeFlags.BlockScoped)
        {
            case NodeFlags.Const:
            case NodeFlags.Using:
            case NodeFlags.AwaitUsing:
                return true;
        }
        return false;
    }

    // Go: utilities.go:2090-2107 (ast.EntityNameToString)
    public static string EntityNameToString(Node name, Func<Node, string>? getTextOfNode)
    {
        switch (name.Kind)
        {
            case SyntaxKind.ThisKeyword:
                return "this";
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
                if (NodeIsSynthesized(name) || getTextOfNode == null)
                {
                    return name.Text();
                }
                return getTextOfNode(name);
            case SyntaxKind.QualifiedName:
                return EntityNameToString(name.AsQualifiedName().Left!, getTextOfNode) + "." + EntityNameToString(name.AsQualifiedName().Right!, getTextOfNode);
            case SyntaxKind.PropertyAccessExpression:
                return EntityNameToString(name.Expression()!, getTextOfNode) + "." + EntityNameToString(name.AsPropertyAccessExpression().Name()!, getTextOfNode);
            case SyntaxKind.JsxNamespacedName:
                return EntityNameToString(name.AsJsxNamespacedName().Namespace!, getTextOfNode) + ":" + EntityNameToString(name.AsJsxNamespacedName().Name()!, getTextOfNode);
        }
        throw new InvalidOperationException("Unhandled case in EntityNameToString");
    }

    // Go: utilities.go:2114-2127 — returns (string, bool); C# Try pattern (plan/05 §4.1)
    // ast/utilities.go:2109 — GetTextOfPropertyName.
    public static string GetTextOfPropertyName(Node name)
    {
        TryGetTextOfPropertyName(name, out var text);
        return text;
    }

    public static bool TryGetTextOfPropertyName(Node name, out string text)
    {
        switch (name.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                text = name.Text();
                return true;
            case SyntaxKind.ComputedPropertyName:
                if (IsStringOrNumericLiteralLike(name.Expression()!))
                {
                    text = name.Expression()!.Text();
                    return true;
                }
                break;
            case SyntaxKind.JsxNamespacedName:
                text = name.AsJsxNamespacedName().Namespace!.Text() + ":" + name.Name()!.Text();
                return true;
        }
        text = "";
        return false;
    }

    // Go: utilities.go:2964-2972
    public static bool IsThisInTypeQuery(Node node)
    {
        if (!JSDocReparseAstGaps.IsThisIdentifier(node))
        {
            return false;
        }
        while (IsQualifiedName(node.Parent!) && node.Parent!.AsQualifiedName().Left == node)
        {
            node = node.Parent!;
        }
        return node.Parent!.Kind == SyntaxKind.TypeQuery;
    }

    // Go: utilities.go:3981-3983
    public static bool IsEmptyArrayLiteral(Node expression)
    {
        return IsArrayLiteralExpression(expression) && (expression.Elements()?.Length ?? 0) == 0;
    }

    // Go: utilities.go:2082-2084 (ast.IsComputedNonLiteralName). Ported for the tsoptions
    // JSON-source-file walk (convertObjectLiteralExpressionToJson) which cannot reach the
    // checker package's internal copy.
    public static bool IsComputedNonLiteralName(Node name)
    {
        return IsComputedPropertyName(name) && !IsStringOrNumericLiteralLike(name.Expression()!);
    }
}
