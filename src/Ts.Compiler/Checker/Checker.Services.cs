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
}
