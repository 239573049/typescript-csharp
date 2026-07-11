// AST-gap helpers required by the parser Declarations cluster (phase 3).
// These are 1:1 ports of ast package free functions that the declaration parser
// needs but which had not yet been ported when this cluster landed:
//   - IsParameterPropertyModifier / IsClassMemberModifier (utilities.go:2979)
//   - CanHaveDecorators (utilities.go:1114)
// Added as a new partial extension of AstUtilities so no existing Ast/ file is
// touched (parallel clusters own those). Verified missing via grep before adding.
#nullable enable

namespace Ts.Ast;

public static partial class AstUtilities
{
    // utilities.go:2983-2985
    public static bool IsParameterPropertyModifier(SyntaxKind kind)
    {
        return (ModifierToFlag(kind) & ModifierFlags.ParameterPropertyModifier) != 0;
    }

    // utilities.go:2979-2982
    public static bool IsClassMemberModifier(SyntaxKind token)
    {
        return IsParameterPropertyModifier(token) ||
            token == SyntaxKind.StaticKeyword ||
            token == SyntaxKind.OverrideKeyword ||
            token == SyntaxKind.AccessorKeyword;
    }

    // utilities.go:1053-1069
    public static bool CanHaveIllegalDecorators(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.Constructor:
            case SyntaxKind.IndexSignature:
            case SyntaxKind.ClassStaticBlockDeclaration:
            case SyntaxKind.MissingDeclaration:
            case SyntaxKind.VariableStatement:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.ModuleDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.NamespaceExportDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ExportAssignment:
                return true;
        }
        return false;
    }

    // utilities.go:1114-1126
    public static bool CanHaveDecorators(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassDeclaration:
                return true;
        }
        return false;
    }
}
