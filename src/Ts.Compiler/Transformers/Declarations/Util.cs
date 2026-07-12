// Ported from tsgo internal/transformers/declarations/util.go.

using global::Ts.Ast;

namespace Ts.Transformers.Declarations;

internal static class DeclarationUtilities
{
    internal static bool NeedsScopeMarker(Node result) =>
        result.Kind is not (
            SyntaxKind.ImportEqualsDeclaration or
            SyntaxKind.ImportDeclaration or
            SyntaxKind.JSImportDeclaration or
            SyntaxKind.ExportDeclaration) &&
        !global::Ts.Ast.NodeTests.IsExportAssignment(result) &&
        !global::Ts.Ast.AstUtilities.HasSyntacticModifier(result, ModifierFlags.Export) &&
        !global::Ts.Ast.AstUtilities.IsAmbientModule(result);

    internal static bool CanHaveLiteralInitializer(IDeclarationEmitHost? host, Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
                return (host?.GetEffectiveDeclarationFlags(node, ModifierFlags.Private) ??
                    (global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(node) & ModifierFlags.Private)) == 0;
            case SyntaxKind.Parameter:
            case SyntaxKind.VariableDeclaration:
                return true;
            default:
                return false;
        }
    }

    internal static bool CanProduceDiagnostics(Node node) => node.Kind is
        SyntaxKind.VariableDeclaration or
        SyntaxKind.PropertyDeclaration or
        SyntaxKind.PropertySignature or
        SyntaxKind.BindingElement or
        SyntaxKind.SetAccessor or
        SyntaxKind.GetAccessor or
        SyntaxKind.ConstructSignature or
        SyntaxKind.CallSignature or
        SyntaxKind.MethodDeclaration or
        SyntaxKind.MethodSignature or
        SyntaxKind.FunctionDeclaration or
        SyntaxKind.Parameter or
        SyntaxKind.TypeParameter or
        SyntaxKind.ExpressionWithTypeArguments or
        SyntaxKind.ImportEqualsDeclaration or
        SyntaxKind.TypeAliasDeclaration or
        SyntaxKind.JSTypeAliasDeclaration or
        SyntaxKind.Constructor or
        SyntaxKind.IndexSignature or
        SyntaxKind.PropertyAccessExpression or
        SyntaxKind.ElementAccessExpression or
        SyntaxKind.BinaryExpression or
        SyntaxKind.CallExpression;

    internal static bool CanReuseModifierNodes(Node[] nodes)
    {
        foreach (var node in nodes)
        {
            if (global::Ts.Ast.AstUtilities.ModifierToFlag(node.Kind) != ModifierFlags.None &&
                (node.Flags & NodeFlags.Reparsed) != 0)
            {
                return false;
            }
        }
        return true;
    }

    internal static bool IsEnclosingDeclaration(Node node) => node.Kind is
        SyntaxKind.SourceFile or
        SyntaxKind.TypeAliasDeclaration or
        SyntaxKind.JSTypeAliasDeclaration or
        SyntaxKind.ModuleDeclaration or
        SyntaxKind.ClassDeclaration or
        SyntaxKind.InterfaceDeclaration or
        SyntaxKind.FunctionDeclaration or
        SyntaxKind.FunctionExpression or
        SyntaxKind.ArrowFunction or
        SyntaxKind.MethodDeclaration or
        SyntaxKind.Constructor or
        SyntaxKind.GetAccessor or
        SyntaxKind.SetAccessor or
        SyntaxKind.IndexSignature or
        SyntaxKind.MappedType or
        SyntaxKind.VariableDeclaration;

    internal static bool IsAlwaysType(Node node) => node.Kind == SyntaxKind.InterfaceDeclaration;

    internal static ModifierFlags MaskModifierFlags(
        Node node,
        ModifierFlags modifierMask,
        ModifierFlags modifierAdditions)
    {
        var flags = (global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(node) & modifierMask) |
            modifierAdditions;
        if ((flags & ModifierFlags.Default) != 0 && (flags & ModifierFlags.Export) == 0)
        {
            flags ^= ModifierFlags.Export;
        }
        if ((flags & ModifierFlags.Default) != 0 && (flags & ModifierFlags.Ambient) != 0)
        {
            flags ^= ModifierFlags.Ambient;
        }
        return flags;
    }

    internal static Node UnwrapParenthesizedExpression(Node node)
    {
        while (node.Kind == SyntaxKind.ParenthesizedExpression)
        {
            node = node.Expression()!;
        }
        return node;
    }

    internal static bool IsPrivateMethodTypeParameter(IDeclarationEmitHost? host, TypeParameterDeclaration node)
    {
        var parent = node.AsNode().Parent;
        return parent?.Kind == SyntaxKind.MethodDeclaration &&
            (host?.GetEffectiveDeclarationFlags(parent, ModifierFlags.Private) ??
                (global::Ts.Ast.AstUtilities.GetCombinedModifierFlags(parent) & ModifierFlags.Private)) != 0;
    }

    internal static bool ShouldEmitFunctionProperties(FunctionDeclaration input)
    {
        if (input.Body is not null)
        {
            return true;
        }
        foreach (var declaration in input.Symbol?.Declarations ?? [])
        {
            if (declaration.Kind == SyntaxKind.FunctionDeclaration &&
                declaration.AsFunctionDeclaration().Body is not null)
            {
                return true;
            }
        }
        return false;
    }

    internal static Node? GetEffectiveBaseTypeNode(Node node)
    {
        foreach (var clause in node.ClassLikeData()?.HeritageClauses?.Nodes ?? [])
        {
            if (clause.AsHeritageClause().Token == SyntaxKind.ExtendsKeyword)
            {
                var types = clause.AsHeritageClause().Types;
                return types is { Nodes.Length: > 0 } ? types.Nodes[0] : null;
            }
        }
        return null;
    }

    internal static bool IsScopeMarker(Node node) =>
        global::Ts.Ast.NodeTests.IsExportAssignment(node) ||
        global::Ts.Ast.NodeTests.IsExportDeclaration(node);

    internal static bool HasScopeMarker(StatementList? statements)
    {
        foreach (var statement in statements?.Nodes ?? [])
        {
            if (IsScopeMarker(statement))
            {
                return true;
            }
        }
        return false;
    }
}
