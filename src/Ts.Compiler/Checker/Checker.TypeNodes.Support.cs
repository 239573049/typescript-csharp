// Ported AST/utility helpers required by the TypeNodes cluster (plan/03 §4.2 #15) that the
// upstream `internal/ast/utilities.go` + `internal/checker/utilities.go` waves have not landed
// yet. Placed here (per the "minimally create missing ast helpers into a Checker/ new file"
// directive) as static members on `partial class Checker` so call sites keep the Go form.
// When the ast/utilities waves land these should collapse to Ts.Ast.AstUtilities / Utilities.cs.

using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // ast.go:1829-1831 (ast.IsTypeOrJSTypeAliasDeclaration)
    internal static bool IsTypeOrJSTypeAliasDeclaration(Node node) =>
        node.Kind == SyntaxKind.TypeAliasDeclaration || node.Kind == SyntaxKind.JSTypeAliasDeclaration;

    // utilities.go:238-240 (checker isTypeAlias)
    internal static bool IsTypeAlias(Node node) => IsTypeOrJSTypeAliasDeclaration(node);

    // utilities.go:938-946 (checker isValidESSymbolDeclaration)
    internal static bool IsValidESSymbolDeclaration(Node node)
    {
        if (NodeTests.IsVariableDeclaration(node))
        {
            return IsVarConst(node) && NodeTests.IsIdentifier(node.Name()!) && IsVariableDeclarationInVariableStatement(node);
        }
        if (NodeTests.IsPropertyDeclaration(node))
        {
            return AstUtilities.HasSyntacticModifier(node, ModifierFlags.Readonly) && AstUtilities.HasStaticModifier(node);
        }
        return NodeTests.IsPropertySignatureDeclaration(node) && AstUtilities.HasSyntacticModifier(node, ModifierFlags.Readonly);
    }

    // utilities.go:948-950 (checker isVariableDeclarationInVariableStatement)
    private static bool IsVariableDeclarationInVariableStatement(Node node) =>
        NodeTests.IsVariableDeclarationList(node.Parent!) && NodeTests.IsVariableStatement(node.Parent!.Parent!);

    // ast.go IsVarConst — GetCombinedNodeFlags(node) & BlockScoped == Const.
    // internal so the EmitResolver class (separate type) can reach it.
    internal static bool IsVarConst(Node node) =>
        (AstUtilities.GetCombinedNodeFlags(node) & NodeFlags.BlockScoped) == NodeFlags.Const;

    // ast/utilities.go:4130-4132 (ast.GetContainingFunction)
    internal static Node? GetContainingFunction(Node node) => AstUtilities.FindAncestor(node.Parent, AstUtilities.IsFunctionLike);

    // ast/utilities.go:2082-2084 (ast.IsComputedNonLiteralName)
    internal static bool IsComputedNonLiteralName(Node name) =>
        NodeTests.IsComputedPropertyName(name) && !AstUtilities.IsStringOrNumericLiteralLike(name.Expression()!);

    // ast/utilities.go:2109-2131 (ast.GetTextOfPropertyName / TryGetTextOfPropertyName)
    internal static string GetTextOfPropertyName(Node name)
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
                if (AstUtilities.IsStringOrNumericLiteralLike(name.Expression()!))
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

    // utilities.go:875-899 (checker isNumericLiteralName)
    internal static bool IsNumericLiteralName(string name) => Ts.JsNum.JsNum.FromString(name).ToString() == name;

    // utilities.go:1013-1015 (checker isInfinityOrNaNString)
    internal static bool IsInfinityOrNaNString(string name) => name == "Infinity" || name == "-Infinity" || name == "NaN";

    // ast/utilities.go:1315-1317 (ast.IsLiteralImportTypeNode)
    internal static bool IsLiteralImportTypeNode(Node node) => AstUtilities.IsLiteralImportTypeNode(node);

    // ast/utilities.go:1990-2081 (ast.IsPartOfTypeNode) — the full port lives in AstUtilities.
    internal static bool IsPartOfTypeNode(Node node) => AstUtilities.IsPartOfTypeNode(node);

    // Intrinsic string-mapping / NoInfer alias name table (Go package var intrinsicTypeKinds).
    // TODO(csport): confirm parity with the upstream table when the string-mapping wave lands.
    internal static readonly Dictionary<string, IntrinsicTypeKind> IntrinsicTypeKinds = new(StringComparer.Ordinal)
    {
        ["Uppercase"] = IntrinsicTypeKind.Uppercase,
        ["Lowercase"] = IntrinsicTypeKind.Lowercase,
        ["Capitalize"] = IntrinsicTypeKind.Capitalize,
        ["Uncapitalize"] = IntrinsicTypeKind.Uncapitalize,
        ["NoInfer"] = IntrinsicTypeKind.NoInfer,
    };
}
