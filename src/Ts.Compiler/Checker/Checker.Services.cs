// Ported from tsgo internal/checker/services.go (LS-facing checker services; hover wave opens this
// file with the lib-detection pair — later LS waves add their services here).

using System.Linq;
using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // services.go:1112-1125 — IsLibSymbolForHoverVerbosity returns true if a symbol is declared in a
    // lib file. Don't expand symbols like Array or Promise, instead treating them as opaque.
    public bool IsLibSymbolForHoverVerbosity(Symbol? symbol)
    {
        if (symbol == null)
        {
            return false;
        }
        foreach (var decl in symbol.Declarations ?? [])
        {
            var sf = AstUtilities.GetSourceFileOfNode(decl);
            if (sf != null && program.IsSourceFileDefaultLibrary(sf.Path()))
            {
                return true;
            }
        }
        return false;
    }

    // services.go:1127-1140 — IsLibTypeForHoverVerbosity returns true if a type is declared in a lib
    // file. Don't expand types like Array or Promise, instead treating them as opaque.
    public bool IsLibTypeForHoverVerbosity(Type t)
    {
        Symbol? symbol;
        if ((t.objectFlags & ObjectFlags.Reference) != 0)
        {
            symbol = t.Target().symbol;
        }
        else
        {
            symbol = t.symbol;
        }
        if (IsLibSymbolForHoverVerbosity(symbol))
        {
            return true;
        }
        return IsTupleType(t);
    }

    // services.go:805-827 — IsTypeInvalidDueToUnionDiscriminant.
    public bool IsTypeInvalidDueToUnionDiscriminant(Type contextualType, Node obj)
    {
        var properties = obj.Properties();
        foreach (var property in properties ?? [])
        {
            Type? nameType = null;
            var propertyName = property.Name();
            if (propertyName != null)
            {
                if (NodeTests.IsJsxNamespacedName(propertyName))
                {
                    nameType = GetStringLiteralType(propertyName.Text());
                }
                else
                {
                    nameType = GetLiteralTypeFromPropertyName(propertyName);
                }
            }
            var name = "";
            if (nameType != null && IsTypeUsableAsPropertyName(nameType))
            {
                name = GetPropertyNameFromType(nameType);
            }
            Type? expected = null;
            if (name.Length > 0)
            {
                expected = GetTypeOfPropertyOfType(contextualType, name);
            }
            if (expected != null && IsLiteralType(expected) && !IsTypeAssignableTo(GetTypeOfNode(property), expected))
            {
                return true;
            }
        }
        return false;
    }

    // services.go:1015-1049 — GetPropertySymbolsFromContextualType.
    public Symbol[] GetPropertySymbolsFromContextualType(Node node, Type contextualType, bool unionSymbolOk)
    {
        var name = GetTextOfPropertyName(node.Name()!);
        if (name.Length == 0)
        {
            return [];
        }
        if ((contextualType.flags & TypeFlags.Union) == 0)
        {
            if (GetPropertyOfType(contextualType, name) is { } symbol)
            {
                return [symbol];
            }
            return [];
        }
        var filteredTypes = contextualType.Types();
        if (NodeTests.IsObjectLiteralExpression(node.Parent!) || NodeTests.IsJsxAttributes(node.Parent!))
        {
            filteredTypes = filteredTypes.Where(t => !IsTypeInvalidDueToUnionDiscriminant(t, node.Parent!)).ToArray();
        }
        var discriminatedPropertySymbols = filteredTypes.Select(t => GetPropertyOfType(t, name)).Where(s => s != null).Select(s => s!).ToArray();
        if (unionSymbolOk && (discriminatedPropertySymbols.Length == 0 || discriminatedPropertySymbols.Length == contextualType.Types().Length))
        {
            if (GetPropertyOfType(contextualType, name) is { } symbol)
            {
                return [symbol];
            }
        }
        if (filteredTypes.Length == 0 && discriminatedPropertySymbols.Length == 0)
        {
            // Bad discriminant -- do again without discriminating
            return contextualType.Types().Select(t => GetPropertyOfType(t, name)).Where(s => s != null).Select(s => s!).ToArray();
        }
        // by eliminating duplicates we might even end up with a single symbol
        // that helps with displaying better quick infos on properties of union types
        return global::Ts.Core.CoreUtil.Deduplicate(discriminatedPropertySymbols) ?? [];
    }

    // checker.go:1202-1208 — GetTypeAliasTypeParameters.
    public Type[] GetTypeAliasTypeParameters(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.TypeAlias) == 0)
        {
            throw new System.InvalidOperationException("Attempted to fetch type alias parameters for non-type-alias symbol");
        }
        GetDeclaredTypeOfSymbol(symbol);
        return typeAliasLinks.Get(symbol).typeParameters ?? [];
    }

    // services.go:234-236 — GetNonOptionalType.
    public Type GetNonOptionalType(Type t) => RemoveOptionalTypeMarker(t);

    // services.go:199-219 — GetAllPossiblePropertiesOfTypes.
    public Symbol[] GetAllPossiblePropertiesOfTypes(Type[] types)
    {
        var unionType = GetUnionType(types);
        if ((unionType.flags & TypeFlags.Union) == 0)
        {
            return GetAugmentedPropertiesOfType(unionType);
        }

        var props = new Dictionary<string, Symbol>();
        foreach (var memberType in types)
        {
            foreach (var p in GetAugmentedPropertiesOfType(memberType))
            {
                if (!props.ContainsKey(p.Name))
                {
                    var prop = CreateUnionOrIntersectionProperty(unionType, p.Name, false /*skipObjectFunctionPropertyAugment*/);
                    // May be undefined if the property is private
                    if (prop != null)
                    {
                        props[p.Name] = prop;
                    }
                }
            }
        }
        return props.Values.ToArray(); // map-iter: 序无关 (совokupность唯一名; consumers sort/dedupe)
    }

    // services.go:221-231
    public bool IsUnknownSymbol(Symbol symbol) => symbol == unknownSymbol;

    public bool IsUndefinedSymbol(Symbol symbol) => symbol == undefinedSymbol;

    // services.go:238-244
    public Type? GetStringIndexType(Type t) => GetIndexTypeOfType(t, stringType!);

    public Type? GetNumberIndexType(Type t) => GetIndexTypeOfType(t, numberType!);

    // services.go:250-256
    public Signature[] GetCallSignatures(Type t) => GetSignaturesOfType(t, SignatureKind.Call);

    public Signature[] GetConstructSignatures(Type t) => GetSignaturesOfType(t, SignatureKind.Construct);

    // services.go:258-260
    public Symbol[] GetApparentProperties(Type t) => GetAugmentedPropertiesOfType(t);

    // services.go:262-283 — getAugmentedPropertiesOfType.
    private Symbol[] GetAugmentedPropertiesOfType(Type t)
    {
        t = GetApparentType(t);
        var propsByName = new Dictionary<string, Symbol>();
        foreach (var p in GetPropertiesOfType(t) ?? [])
        {
            propsByName.TryAdd(p.Name, p);
        }
        Type? functionType = null;
        if (GetSignaturesOfType(t, SignatureKind.Call).Length > 0)
        {
            functionType = globalCallableFunctionType;
        }
        else if (GetSignaturesOfType(t, SignatureKind.Construct).Length > 0)
        {
            functionType = globalNewableFunctionType;
        }

        if (functionType != null)
        {
            foreach (var p in GetPropertiesOfType(functionType) ?? [])
            {
                propsByName.TryAdd(p.Name, p);
            }
        }
        return GetNamedMembers(propsByName, null);
    }

    // services.go:285-301 — TryGetMemberInModuleExportsAndProperties.
    public Symbol? TryGetMemberInModuleExportsAndProperties(string memberName, Symbol moduleSymbol)
    {
        var symbol = TryGetMemberInModuleExports(memberName, moduleSymbol);
        if (symbol != null)
        {
            return symbol;
        }

        var exportEquals = ResolveExternalModuleSymbol(moduleSymbol, false /*dontResolveAlias*/);
        if (exportEquals == moduleSymbol)
        {
            return null;
        }

        var t = GetTypeOfSymbol(exportEquals!);
        if (ShouldTreatPropertiesOfExternalModuleAsExports(t))
        {
            return GetPropertyOfType(t, memberName);
        }
        return null;
    }

    // services.go:303-306 — TryGetMemberInModuleExports.
    public Symbol? TryGetMemberInModuleExports(string memberName, Symbol moduleSymbol)
    {
        var symbolTable = GetExportsOfModule(moduleSymbol);
        return symbolTable != null && symbolTable.TryGetValue(memberName, out var member) ? member : null;
    }

    // services.go:308-314 — shouldTreatPropertiesOfExternalModuleAsExports.
    private bool ShouldTreatPropertiesOfExternalModuleAsExports(Type resolvedExternalModuleType)
    {
        return (resolvedExternalModuleType.flags & TypeFlags.Primitive) == 0 ||
            (resolvedExternalModuleType.objectFlags & ObjectFlags.Class) != 0 ||
            // `isArrayOrTupleLikeType` is too expensive to use in this auto-imports hot path.
            IsArrayType(resolvedExternalModuleType) ||
            IsTupleType(resolvedExternalModuleType);
    }

    // services.go:154-156 — IsValidPropertyAccess.
    public bool IsValidPropertyAccess(Node node, string propertyName) => IsValidPropertyAccessWorker(node, propertyName);

    // services.go:158-168 — isValidPropertyAccess.
    private bool IsValidPropertyAccessWorker(Node node, string propertyName)
    {
        switch (node.Kind)
        {
            case SyntaxKind.PropertyAccessExpression:
                return IsValidPropertyAccessWithType(node, node.Expression()!.Kind == SyntaxKind.SuperKeyword, propertyName, GetWidenedType(CheckExpression(node.Expression()!)));
            case SyntaxKind.QualifiedName:
                return IsValidPropertyAccessWithType(node, false /*isSuper*/, propertyName, GetWidenedType(CheckExpression(node.AsQualifiedName().Left!)));
            case SyntaxKind.ImportType:
                return IsValidPropertyAccessWithType(node, false /*isSuper*/, propertyName, GetTypeFromTypeNode(node));
        }
        throw new System.InvalidOperationException("Unexpected node kind in isValidPropertyAccess: " + node.Kind);
    }

    // services.go:170-178 — isValidPropertyAccessWithType.
    private bool IsValidPropertyAccessWithType(Node node, bool isSuper, string propertyName, Type t)
    {
        // Short-circuiting for improved performance.
        if ((t.flags & TypeFlags.Any) != 0)
        {
            return true;
        }

        var prop = GetPropertyOfType(t, propertyName);
        return prop != null && IsPropertyAccessible(node, isSuper, false /*isWrite*/, t, prop);
    }
}
