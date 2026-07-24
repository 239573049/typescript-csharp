// Ported from tsgo internal/ls/utilities.go — the semantic-meaning cluster (hover/definition/
// rename share it): getMeaningFromLocation / getMeaningFromDeclaration / type- and namespace-
// reference detection / container lookup / object-literal-element lookup.

using System.Linq;
using Ts.Ast;

namespace Ts.Ls;

internal static class LsMeaning
{
    // utilities.go:800-837
    internal static SemanticMeaning GetMeaningFromLocation(Node node)
    {
        // todo: check if this function needs to be changed for jsdoc updates
        node = LsDeps.GetAdjustedLocation(AstUtilities.GetReparsedNodeForNode(node), false /*forRename*/, null);
        var parent = node.Parent;
        if (NodeTests.IsSourceFile(node))
        {
            return SemanticMeaning.Value;
        }
        if (AstUtilities.NodeKindIs(parent!, SyntaxKind.ExportAssignment, SyntaxKind.ExportSpecifier, SyntaxKind.ExternalModuleReference, SyntaxKind.ImportSpecifier, SyntaxKind.ImportClause) ||
            (parent!.Kind == SyntaxKind.ImportEqualsDeclaration && node == parent.Name()))
        {
            return SemanticMeaning.All;
        }
        if (IsInRightSideOfInternalImportEqualsDeclaration(node))
        {
            //     import a = |b|; // Namespace
            //     import a = |b.c|; // Value, type, namespace
            //     import a = |b.c|.d; // Namespace
            var name = node;
            if (node.Kind != SyntaxKind.QualifiedName)
            {
                name = node.Parent!.Kind == SyntaxKind.QualifiedName && node.Parent.AsQualifiedName().Right == node ? node.Parent : null;
            }
            if (name != null && name.Parent!.Kind == SyntaxKind.ImportEqualsDeclaration)
            {
                return SemanticMeaning.All;
            }
            return SemanticMeaning.Namespace;
        }
        if (AstUtilities.IsDeclarationName(node))
        {
            return GetMeaningFromDeclaration(parent);
        }
        if (AstUtilities.IsEntityName(node) && AstUtilities.IsJSDocNameReferenceContext(node))
        {
            return SemanticMeaning.All;
        }
        if (IsTypeReference(node))
        {
            return SemanticMeaning.Type;
        }
        if (IsNamespaceReference(node))
        {
            return SemanticMeaning.Namespace;
        }
        if (NodeTests.IsTypeParameterDeclaration(parent))
        {
            return SemanticMeaning.Type;
        }
        if (NodeTests.IsLiteralTypeNode(parent))
        {
            // This might be T["name"], which is actually referencing a property and not a type. So allow both meanings.
            return SemanticMeaning.Type | SemanticMeaning.Value;
        }
        return SemanticMeaning.Value;
    }

    // utilities.go:839-877
    internal static SemanticMeaning GetMeaningFromDeclaration(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.BindingElement:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.CatchClause:
            case SyntaxKind.JsxAttribute:
                return SemanticMeaning.Value;

            case SyntaxKind.TypeParameter:
            case SyntaxKind.InterfaceDeclaration:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.TypeLiteral:
                return SemanticMeaning.Type;

            case SyntaxKind.EnumMember:
            case SyntaxKind.ClassDeclaration:
                return SemanticMeaning.Value | SemanticMeaning.Type;

            case SyntaxKind.ModuleDeclaration:
                if (AstUtilities.IsAmbientModule(node))
                {
                    return SemanticMeaning.Namespace | SemanticMeaning.Value;
                }
                else if (AstUtilities.GetModuleInstanceState(node) == ModuleInstanceState.Instantiated)
                {
                    return SemanticMeaning.Namespace | SemanticMeaning.Value;
                }
                else
                {
                    return SemanticMeaning.Namespace;
                }

            case SyntaxKind.EnumDeclaration:
            case SyntaxKind.NamedImports:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.JSImportDeclaration:
            case SyntaxKind.ExportAssignment:
            case SyntaxKind.ExportDeclaration:
                return SemanticMeaning.All;

            // An external module can be a Value
            case SyntaxKind.SourceFile:
                return SemanticMeaning.Namespace | SemanticMeaning.Value;
        }

        return SemanticMeaning.All;
    }

    // utilities.go:879-916 — getIntersectingMeaningFromDeclarations.
    internal static SemanticMeaning GetIntersectingMeaningFromDeclarations(Node? node, Symbol symbol, SemanticMeaning defaultMeaning)
    {
        if (node == null)
        {
            return defaultMeaning;
        }

        var meaning = GetMeaningFromLocation(node);
        var declarations = symbol.Declarations;
        if (declarations is not { Length: > 0 })
        {
            return meaning;
        }

        var lastIterationMeaning = meaning;

        // !!! TODO check if the port is correct and the for loop is needed
        SemanticMeaning Iteration(SemanticMeaning m)
        {
            foreach (var declaration in declarations)
            {
                var declarationMeaning = GetMeaningFromDeclaration(declaration);
                if ((declarationMeaning & m) != 0)
                {
                    m |= declarationMeaning;
                }
            }
            return m;
        }
        meaning = Iteration(meaning);

        while (meaning != lastIterationMeaning)
        {
            // The result is order-sensitive, for instance if initialMeaning == Namespace, and declarations = [class, instantiated module]
            // we need to consider both as the initialMeaning intersects with the module in the namespace space, and the module
            // intersects with the class in the value space.
            // To achieve that we will keep iterating until the result stabilizes.

            // Remember the last meaning
            lastIterationMeaning = meaning;
            meaning = Iteration(meaning);
        }

        return meaning;
    }

    // utilities.go:201-203
    internal static bool IsNamespaceReference(Node node) =>
        IsQualifiedNameNamespaceReference(node) || IsPropertyAccessNamespaceReference(node);

    // utilities.go:205-217
    private static bool IsQualifiedNameNamespaceReference(Node node)
    {
        var root = node;
        var isLastClause = true;
        if (root.Parent!.Kind == SyntaxKind.QualifiedName)
        {
            while (root.Parent != null && root.Parent.Kind == SyntaxKind.QualifiedName)
            {
                root = root.Parent;
            }
            isLastClause = root.AsQualifiedName().Right == node;
        }

        return root.Parent!.Kind == SyntaxKind.TypeReference && !isLastClause;
    }

    // utilities.go:219-237
    private static bool IsPropertyAccessNamespaceReference(Node node)
    {
        var root = node;
        var isLastClause = true;
        if (root.Parent!.Kind == SyntaxKind.PropertyAccessExpression)
        {
            while (root.Parent != null && root.Parent.Kind == SyntaxKind.PropertyAccessExpression)
            {
                root = root.Parent;
            }
            isLastClause = root.Name() == node;
        }

        if (!isLastClause && root.Parent!.Kind == SyntaxKind.ExpressionWithTypeArguments && root.Parent.Parent!.Kind == SyntaxKind.HeritageClause)
        {
            var decl = root.Parent.Parent.Parent!;
            return (decl.Kind == SyntaxKind.ClassDeclaration && root.Parent.Parent.AsHeritageClause().Token == SyntaxKind.ImplementsKeyword) ||
                (decl.Kind == SyntaxKind.InterfaceDeclaration && root.Parent.Parent.AsHeritageClause().Token == SyntaxKind.ExtendsKeyword);
        }

        return false;
    }

    // utilities.go:252-274
    internal static bool IsTypeReference(Node node)
    {
        if (AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(node))
        {
            node = node.Parent!;
        }

        switch (node.Kind)
        {
            case SyntaxKind.ThisKeyword:
                return !AstUtilities.IsExpressionNode(node);
            case SyntaxKind.ThisType:
                return true;
        }

        switch (node.Parent!.Kind)
        {
            case SyntaxKind.TypeReference:
                return true;
            case SyntaxKind.ImportType:
                return !node.Parent.AsImportTypeNode().IsTypeOf;
            case SyntaxKind.ExpressionWithTypeArguments:
                return AstUtilities.IsPartOfTypeNode(node.Parent);
        }

        return false;
    }

    // utilities.go:276-285
    internal static bool IsInRightSideOfInternalImportEqualsDeclaration(Node node)
    {
        if (node.Parent == null)
        {
            return false;
        }
        while (node.Parent!.Kind == SyntaxKind.QualifiedName)
        {
            node = node.Parent;
        }

        return IsInternalModuleImportEqualsDeclaration(node.Parent) && node.Parent.AsImportEqualsDeclaration().ModuleReference == node;
    }

    // ast/utilities.go:2442-2444 — not yet surfaced on the shared Ast utilities; local 1:1 copy.
    private static bool IsInternalModuleImportEqualsDeclaration(Node node)
    {
        return NodeTests.IsImportEqualsDeclaration(node) && node.AsImportEqualsDeclaration().ModuleReference!.Kind != SyntaxKind.ExternalModuleReference;
    }

    // utilities.go:441-450
    internal static Node? GetContainerNode(Node node)
    {
        for (var parent = node.Parent; parent != null; parent = parent.Parent)
        {
            switch (parent.Kind)
            {
                case SyntaxKind.SourceFile:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.MethodSignature:
                case SyntaxKind.FunctionDeclaration:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.EnumDeclaration:
                case SyntaxKind.ModuleDeclaration:
                    return parent;
            }
        }
        return null;
    }

    // utilities.go:1254-1260
    internal static Node? GetContainingObjectLiteralElement(Node node)
    {
        var element = GetContainingObjectLiteralElementWorker(node);
        if (element != null && (NodeTests.IsObjectLiteralExpression(element.Parent!) || NodeTests.IsJsxAttributes(element.Parent!)))
        {
            return element;
        }
        return null;
    }

    // utilities.go:1262-1278
    private static Node? GetContainingObjectLiteralElementWorker(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
                if (node.Parent!.Kind == SyntaxKind.ComputedPropertyName)
                {
                    if (IsObjectLiteralOrJsxElement(node.Parent.Parent!))
                    {
                        return node.Parent.Parent;
                    }
                    return null;
                }
                goto case SyntaxKind.Identifier;
            case SyntaxKind.Identifier:
            case SyntaxKind.JsxNamespacedName:
                if (IsObjectLiteralOrJsxElement(node.Parent!) && node.Parent!.Parent is { } grand && (grand.Kind == SyntaxKind.ObjectLiteralExpression || grand.Kind == SyntaxKind.JsxAttributes) && node.Parent.Name() == node)
                {
                    return node.Parent;
                }
                break;
        }
        return null;
    }

    // utilities.go:1280-1282
    private static bool IsObjectLiteralOrJsxElement(Node node) =>
        AstUtilities.IsObjectLiteralElement(node) || NodeTests.IsJsxAttribute(node) || NodeTests.IsJsxSpreadAttribute(node);
}
