// Ported from tsgo internal/checker/checker.go — the LS/API "symbol at a node" surface
// (GetSymbolAtLocation / getTypeOfNode and their name/property-access resolver). These are the gateway
// the language service uses for hover / go-to-definition / completions / fourslash; they are deliberately
// "fuzzy" (return *some* symbol) and must not be used by the checker itself.
//
// PORT-NOTE: the JSDoc @param-tag branch of getSymbolAtLocation (checker.go, uses astnav.GetNodeAtPosition
// to locate the host function) is deferred — it returns the fall-through (nil) for JSDoc param names, which
// only affects JSDoc-in-.js hover; TS conformance does not reach it.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go — getRegularTypeOfExpression.
    internal Type GetRegularTypeOfExpression(Node expr)
    {
        if (AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(expr))
        {
            expr = expr.Parent!;
        }
        return GetRegularTypeOfLiteralType(GetTypeOfExpression(expr));
    }

    // checker/utilities.go:1108 — isImportTypeQualifierPart.
    private static Node? IsImportTypeQualifierPart(Node node)
    {
        var parent = node.Parent;
        while (parent != null && NodeTests.IsQualifiedName(parent))
        {
            node = parent;
            parent = parent.Parent;
        }
        if (parent != null && parent.Kind == SyntaxKind.ImportType && parent.AsImportTypeNode().Qualifier == node)
        {
            return parent;
        }
        return null;
    }

    // checker/utilities.go:1122 — isInNameOfExpressionWithTypeArguments.
    private static bool IsInNameOfExpressionWithTypeArguments(Node node)
    {
        while (node.Parent!.Kind == SyntaxKind.PropertyAccessExpression)
        {
            node = node.Parent;
        }
        return node.Parent!.Kind == SyntaxKind.ExpressionWithTypeArguments;
    }

    // checker.go — getSymbolForPrivateIdentifierExpression.
    internal Symbol? GetSymbolForPrivateIdentifierExpression(Node node)
    {
        var links = symbolNodeLinks.Get(node);
        if (links.resolvedSymbol == null)
        {
            links.resolvedSymbol = LookupSymbolForPrivateIdentifierDeclaration(node.Text(), node);
        }
        return links.resolvedSymbol;
    }

    // checker.go:10773 — checkMetaPropertyKeyword. (Go stub: returns errorType.)
    internal Type CheckMetaPropertyKeyword(Node node) => errorType!;

    // checker.go:31537 — getSymbolOfNameOrPropertyAccessExpression.
    internal Symbol? GetSymbolOfNameOrPropertyAccessExpression(Node name)
    {
        if (AstUtilities.IsDeclarationName(name))
        {
            return GetSymbolOfNode(name.Parent!);
        }
        if (name.Parent!.Kind == SyntaxKind.ExportAssignment && AstUtilities.IsEntityNameExpression(name))
        {
            var success = ResolveEntityName(name, SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace | SymbolFlags.Alias, true /*ignoreErrors*/, false /*dontResolveAlias*/, null);
            if (success != null && success != unknownSymbol)
            {
                return success;
            }
        }
        else if (AstUtilities.IsEntityName(name) && AstUtilities.IsInRightSideOfImportOrExportAssignment(name))
        {
            var importEqualsDeclaration = AstUtilities.FindAncestorKind(name, SyntaxKind.ImportEqualsDeclaration);
            if (importEqualsDeclaration == null)
            {
                throw new InvalidOperationException("ImportEqualsDeclaration should be defined");
            }
            return GetSymbolOfPartOfRightHandSideOfImportEquals(name);
        }

        if (AstUtilities.IsEntityName(name))
        {
            var possibleImportNode = IsImportTypeQualifierPart(name);
            if (possibleImportNode != null)
            {
                GetTypeFromTypeNode(possibleImportNode);
                var sym = GetResolvedSymbolOrNil(name);
                return sym == unknownSymbol ? null : sym;
            }
        }

        while (AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(name))
        {
            name = name.Parent!;
        }

        if (IsInNameOfExpressionWithTypeArguments(name))
        {
            SymbolFlags meaning;
            if (name.Parent!.Kind == SyntaxKind.ExpressionWithTypeArguments)
            {
                meaning = AstUtilities.IsPartOfTypeNode(name) ? SymbolFlags.Type : SymbolFlags.Value;
                if (AstUtilities.IsExpressionWithTypeArgumentsInClassExtendsClause(name.Parent!))
                {
                    meaning |= SymbolFlags.Value;
                }
            }
            else
            {
                meaning = SymbolFlags.Namespace;
            }
            meaning |= SymbolFlags.Alias;
            Symbol? entityNameSymbol = null;
            if (AstUtilities.IsEntityNameExpression(name))
            {
                entityNameSymbol = ResolveEntityName(name, meaning, true /*ignoreErrors*/, false /*dontResolveAlias*/, null);
            }
            if (entityNameSymbol != null)
            {
                return entityNameSymbol;
            }
        }

        if (AstUtilities.IsExpressionNode(name))
        {
            if (AstUtilities.NodeIsMissing(name))
            {
                return null;
            }
            var isJSDoc = AstUtilities.IsJSDocNameReferenceContext(name);
            if (NodeTests.IsIdentifier(name))
            {
                if (AstUtilities.IsJsxTagName(name) && IsJsxIntrinsicTagName(name))
                {
                    var symbol = GetIntrinsicTagSymbol(name.Parent!);
                    return symbol == unknownSymbol ? null : symbol;
                }
                var meaning = isJSDoc ? SymbolFlags.Value | SymbolFlags.Type | SymbolFlags.Namespace : SymbolFlags.Value;
                Node? location = null;
                if (isJSDoc)
                {
                    location = AstUtilities.GetHostSignatureFromJSDoc(name);
                }
                var result = ResolveEntityName(name, meaning, true /*ignoreErrors*/, true /*dontResolveAlias*/, location);
                if (result == null && isJSDoc)
                {
                    var container = AstUtilities.FindAncestor(name, AstUtilities.IsClassOrInterfaceLike);
                    if (container != null)
                    {
                        var symbol = GetSymbolOfDeclaration(container);
                        result = GetMergedSymbol(GetSymbol(GetExportsOfSymbol(symbol!), name.Text(), meaning));
                        if (result == null)
                        {
                            result = GetPropertyOfType(GetDeclaredTypeOfSymbol(symbol!), name.Text());
                        }
                    }
                }
                return result;
            }
            else if (NodeTests.IsPrivateIdentifier(name))
            {
                return GetSymbolForPrivateIdentifierExpression(name);
            }
            else if (NodeTests.IsPropertyAccessExpression(name) || NodeTests.IsQualifiedName(name))
            {
                var links = symbolNodeLinks.Get(name);
                if (links.resolvedSymbol != null)
                {
                    return links.resolvedSymbol;
                }
                if (NodeTests.IsPropertyAccessExpression(name))
                {
                    CheckPropertyAccessExpression(name, CheckMode.Normal, false /*writeOnly*/);
                    if (links.resolvedSymbol == null && !NodeTests.IsPrivateIdentifier(name.Name()!))
                    {
                        links.resolvedSymbol = GetApplicableIndexSymbol(
                            CheckExpressionCached(name.Expression()!),
                            GetLiteralTypeFromPropertyName(name.Name()!));
                    }
                }
                else
                {
                    CheckQualifiedName(name, CheckMode.Normal);
                }
                if (links.resolvedSymbol == null && isJSDoc && NodeTests.IsQualifiedName(name))
                {
                    return ResolveJSDocMemberName(name);
                }
                return links.resolvedSymbol;
            }
        }
        else if (AstUtilities.IsEntityName(name) && IsTypeReferenceIdentifier(name))
        {
            var meaning = name.Parent!.Kind == SyntaxKind.TypeReference ? SymbolFlags.Type : SymbolFlags.Namespace;
            var symbol = ResolveEntityName(name, meaning, true /*ignoreErrors*/, true /*dontResolveAlias*/, null);
            if (symbol != null && symbol != unknownSymbol)
            {
                return symbol;
            }
            return GetUnresolvedSymbolForEntityName(name);
        }

        if (name.Parent!.Kind == SyntaxKind.TypePredicate)
        {
            return ResolveEntityName(name, SymbolFlags.FunctionScopedVariable, true /*ignoreErrors*/, false /*dontResolveAlias*/, null);
        }
        return null;
    }

    // checker.go:31338 — GetSymbolAtLocation (LS/API entry).
    public Symbol? GetSymbolAtLocation(Node node)
    {
        // set ignoreErrors: true because any lookups invoked by the API shouldn't cause any new errors
        return GetSymbolAtLocation(node, true /*ignoreErrors*/);
    }

    // checker.go:31351 — getSymbolAtLocation.
    internal Symbol? GetSymbolAtLocation(Node node, bool ignoreErrors)
    {
        if (NodeTests.IsSourceFile(node))
        {
            if (AstUtilities.IsExternalOrCommonJSModule(node.AsSourceFile()))
            {
                return GetMergedSymbol(node.Symbol());
            }
            return null;
        }
        var parent = node.Parent!;
        var grandParent = parent.Parent;

        if ((node.Flags & NodeFlags.InWithStatement) != 0)
        {
            return null;
        }

        if (AstUtilities.IsDeclarationNameOrImportPropertyName(node))
        {
            var parentSymbol = GetSymbolOfDeclaration(parent);
            if (AstUtilities.IsImportOrExportSpecifier(parent) && parent.PropertyName() == node)
            {
                return GetImmediateAliasedSymbol(parentSymbol!);
            }
            return parentSymbol;
        }
        else if (AstUtilities.IsLiteralComputedPropertyDeclarationName(node))
        {
            return GetSymbolOfDeclaration(grandParent!);
        }

        if (NodeTests.IsIdentifier(node))
        {
            if (AstUtilities.IsInRightSideOfImportOrExportAssignment(node))
            {
                return GetSymbolOfNameOrPropertyAccessExpression(node);
            }
            else if (NodeTests.IsBindingElement(parent) && NodeTests.IsObjectBindingPattern(grandParent!) && node == parent.PropertyName())
            {
                var typeOfPattern = GetTypeOfNode(grandParent!);
                var propertyDeclaration = GetPropertyOfType(typeOfPattern, node.Text());
                if (propertyDeclaration != null)
                {
                    return propertyDeclaration;
                }
            }
            else if (NodeTests.IsMetaProperty(parent) && parent.Name() == node)
            {
                var metaProp = parent.AsMetaProperty();
                if (metaProp.KeywordToken == SyntaxKind.NewKeyword && node.Text() == "target")
                {
                    return CheckNewTargetMetaProperty(parent).symbol;
                }
                if (metaProp.KeywordToken == SyntaxKind.ImportKeyword && node.Text() == "meta")
                {
                    return GetGlobalImportMetaExpressionType().AsObjectType()!.members!.GetValueOrDefault("meta");
                }
                return null;
            }
            // PORT-NOTE: JSDoc @param-tag name branch (astnav.GetNodeAtPosition) deferred.
        }

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.QualifiedName:
                if (!AstUtilities.IsThisInTypeQuery(node))
                {
                    return GetSymbolOfNameOrPropertyAccessExpression(node);
                }
                goto case SyntaxKind.ThisKeyword;
            case SyntaxKind.ThisKeyword:
            {
                var container = GetThisContainer(node, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
                if (AstUtilities.IsFunctionLike(container))
                {
                    var sig = GetSignatureFromDeclaration(container!);
                    if (sig.thisParameter != null)
                    {
                        return sig.thisParameter;
                    }
                }
                if (AstUtilities.IsInExpressionContext(node))
                {
                    return CheckExpression(node).symbol;
                }
                goto case SyntaxKind.ThisType;
            }
            case SyntaxKind.ThisType:
                return GetTypeFromThisTypeNode(node).symbol;
            case SyntaxKind.SuperKeyword:
                return CheckExpression(node).symbol;
            case SyntaxKind.ConstructorKeyword:
                if (parent.Kind == SyntaxKind.Constructor)
                {
                    return parent.Parent!.Symbol();
                }
                return null;
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                if ((IsExternalModuleImportEqualsDeclaration(grandParent!) && GetExternalModuleImportEqualsDeclarationExpression(grandParent!) == node) ||
                    ((parent.Kind == SyntaxKind.ImportDeclaration || parent.Kind == SyntaxKind.JSImportDeclaration || parent.Kind == SyntaxKind.ExportDeclaration) && global::Ts.Printer.PrinterUtilities.GetExternalModuleName(parent) == node) ||
                    AstUtilities.IsVariableDeclarationInitializedToRequire(grandParent!) || AstUtilities.IsImportCall(parent) ||
                    (NodeTests.IsLiteralTypeNode(parent) && IsLiteralImportTypeNode(grandParent!) && grandParent!.AsImportTypeNode().Argument == parent))
                {
                    return ResolveExternalModuleName(node, node, ignoreErrors);
                }
                if (NodeTests.IsCallExpression(parent) && AstUtilities.IsBindableObjectDefinePropertyCall(parent) && parent.Arguments()![1] == node)
                {
                    return GetSymbolOfDeclaration(parent);
                }
                goto case SyntaxKind.NumericLiteral;
            case SyntaxKind.NumericLiteral:
            {
                Type? objectType = null;
                if (NodeTests.IsElementAccessExpression(parent))
                {
                    if (parent.AsElementAccessExpression().ArgumentExpression == node)
                    {
                        objectType = GetTypeOfExpression(parent.Expression()!);
                    }
                }
                else if (NodeTests.IsLiteralTypeNode(parent) && NodeTests.IsIndexedAccessTypeNode(grandParent!))
                {
                    objectType = GetTypeFromTypeNode(grandParent!.AsIndexedAccessTypeNode().ObjectType!);
                }
                if (objectType != null)
                {
                    return GetPropertyOfType(objectType, node.Text());
                }
                return null;
            }
            case SyntaxKind.DefaultKeyword:
            case SyntaxKind.FunctionKeyword:
            case SyntaxKind.EqualsGreaterThanToken:
            case SyntaxKind.ClassKeyword:
                return GetSymbolOfNode(node.Parent!);
            case SyntaxKind.ImportType:
                if (IsLiteralImportTypeNode(node))
                {
                    return GetSymbolAtLocation(node.AsImportTypeNode().Argument!.AsLiteralTypeNode().Literal!, ignoreErrors);
                }
                return null;
            case SyntaxKind.ExportKeyword:
                if (NodeTests.IsExportAssignment(parent))
                {
                    if (parent.Symbol() == null)
                    {
                        throw new InvalidOperationException("Symbol should be defined");
                    }
                    return parent.Symbol();
                }
                return null;
            case SyntaxKind.ImportKeyword:
                if (NodeTests.IsMetaProperty(node.Parent!) && node.Parent!.Text() == "defer")
                {
                    return null;
                }
                goto case SyntaxKind.NewKeyword;
            case SyntaxKind.NewKeyword:
                if (NodeTests.IsMetaProperty(parent))
                {
                    return CheckMetaPropertyKeyword(parent).symbol;
                }
                return null;
            case SyntaxKind.InstanceOfKeyword:
                if (NodeTests.IsBinaryExpression(parent))
                {
                    var t = GetTypeOfExpression(parent.AsBinaryExpression().Right!);
                    var hasInstanceMethodType = GetSymbolHasInstanceMethodOfObjectType(t);
                    if (hasInstanceMethodType != null && hasInstanceMethodType.symbol != null)
                    {
                        return hasInstanceMethodType.symbol;
                    }
                    return t.symbol;
                }
                return null;
            case SyntaxKind.MetaProperty:
                return CheckExpression(node).symbol;
            case SyntaxKind.JsxNamespacedName:
                if (AstUtilities.IsJsxTagName(node) && IsJsxIntrinsicTagName(node))
                {
                    var symbol = GetIntrinsicTagSymbol(node.Parent!);
                    return symbol == unknownSymbol ? null : symbol;
                }
                return null;
            default:
                return null;
        }
    }

    // checker.go:31684 — getTypeOfNode.
    internal Type GetTypeOfNode(Node node)
    {
        if (NodeTests.IsSourceFile(node) && !AstUtilities.IsExternalOrCommonJSModule(node.AsSourceFile()))
        {
            return errorType!;
        }
        if ((node.Flags & NodeFlags.InWithStatement) != 0)
        {
            return errorType!;
        }
        var (classDecl, isImplements) = AstUtilities.TryGetClassImplementingOrExtendingExpressionWithTypeArguments(node);
        Type? classType = null;
        if (classDecl != null)
        {
            classType = GetDeclaredTypeOfClassOrInterface(GetSymbolOfDeclaration(classDecl)!);
        }
        if (AstUtilities.IsPartOfTypeNode(node))
        {
            var typeFromTypeNode = GetTypeFromTypeNode(node);
            if (classType != null)
            {
                return GetTypeWithThisArgument(typeFromTypeNode, classType.AsInterfaceType()!.thisType, false /*needApparentType*/);
            }
            return typeFromTypeNode;
        }
        if (AstUtilities.IsExpressionNode(node))
        {
            return GetRegularTypeOfExpression(node);
        }
        if (classType != null && !isImplements)
        {
            var baseType = CoreUtil.FirstOrNil(GetBaseTypes(classType));
            if (baseType != null)
            {
                return GetTypeWithThisArgument(baseType, classType.AsInterfaceType()!.thisType, false /*needApparentType*/);
            }
            return errorType!;
        }
        if (AstUtilities.IsTypeDeclaration(node))
        {
            var symbol = GetSymbolOfDeclaration(node);
            return GetDeclaredTypeOfSymbol(symbol!);
        }
        if (AstUtilities.IsTypeDeclarationName(node))
        {
            var symbol = GetSymbolAtLocation(node, false /*ignoreErrors*/);
            if (symbol != null)
            {
                return GetDeclaredTypeOfSymbol(symbol);
            }
            return errorType!;
        }
        if (NodeTests.IsBindingElement(node))
        {
            var t = GetTypeForVariableLikeDeclaration(node, true /*includeOptionality*/, CheckMode.Normal);
            return t ?? errorType!;
        }
        if (AstUtilities.IsDeclaration(node))
        {
            var symbol = GetSymbolOfDeclaration(node);
            if (symbol != null)
            {
                return GetTypeOfSymbol(symbol);
            }
            return errorType!;
        }
        if (AstUtilities.IsDeclarationNameOrImportPropertyName(node))
        {
            var symbol = GetSymbolAtLocation(node, false /*ignoreErrors*/);
            if (symbol != null)
            {
                return GetTypeOfSymbol(symbol);
            }
            return errorType!;
        }
        if (AstUtilities.IsBindingPattern(node))
        {
            var t = GetTypeForVariableLikeDeclaration(node.Parent!, true /*includeOptionality*/, CheckMode.Normal);
            return t ?? errorType!;
        }
        if (AstUtilities.IsInRightSideOfImportOrExportAssignment(node))
        {
            var symbol = GetSymbolAtLocation(node, false /*ignoreErrors*/);
            if (symbol != null)
            {
                var declaredType = GetDeclaredTypeOfSymbol(symbol);
                if (!IsErrorType(declaredType))
                {
                    return declaredType;
                }
                return GetTypeOfSymbol(symbol);
            }
        }
        if (NodeTests.IsMetaProperty(node.Parent!) && node.Parent!.AsMetaProperty().KeywordToken == node.Kind)
        {
            return CheckMetaPropertyKeyword(node.Parent!);
        }
        if (NodeTests.IsImportAttributes(node))
        {
            return GetGlobalImportAttributesType();
        }
        return errorType!;
    }
}
