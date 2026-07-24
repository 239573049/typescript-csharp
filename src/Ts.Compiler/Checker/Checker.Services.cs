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

    // services.go:689-694 — GetTypeArgumentConstraint.
    public Type? GetTypeArgumentConstraint(Node node)
    {
        if (!AstUtilities.IsTypeNode(node))
        {
            return null;
        }
        return GetTypeArgumentConstraintWorker(node);
    }

    // services.go:696-714 — getUninstantiatedSignatures: gets generic signatures from the
    // function's/constructor's type.
    private Signature[] GetUninstantiatedSignatures(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.CallExpression:
            case SyntaxKind.Decorator:
                return GetSignaturesOfType(GetTypeOfExpression(node.Expression()!), SignatureKind.Call);
            case SyntaxKind.NewExpression:
                return GetSignaturesOfType(GetTypeOfExpression(node.Expression()!), SignatureKind.Construct);
            case SyntaxKind.JsxSelfClosingElement:
            case SyntaxKind.JsxOpeningElement:
                if (IsJsxIntrinsicTagName(node.TagName()!))
                {
                    return [];
                }
                return GetSignaturesOfType(GetTypeOfExpression(node.TagName()!), SignatureKind.Call);
            case SyntaxKind.TaggedTemplateExpression:
                return GetSignaturesOfType(GetTypeOfExpression(node.AsTaggedTemplateExpression().Tag!), SignatureKind.Call);
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.JsxOpeningFragment:
                return [];
        }
        return [];
    }

    // services.go:716-729 — getTypeParameterConstraintForPositionAcrossSignatures.
    private Type GetTypeParameterConstraintForPositionAcrossSignatures(Signature[] signatures, int position)
    {
        List<Type>? relevantConstraints = null;
        foreach (var signature in signatures)
        {
            if (signature.typeParameters == null || position >= signature.typeParameters.Length)
            {
                continue;
            }
            var relevantTypeParameter = signature.typeParameters[position];
            var relevantConstraint = GetConstraintOfTypeParameter(relevantTypeParameter);
            if (relevantConstraint != null)
            {
                (relevantConstraints ??= new List<Type>()).Add(relevantConstraint);
            }
        }
        return GetUnionType(relevantConstraints?.ToArray());
    }

    // services.go:731-803 — getTypeArgumentConstraint.
    private Type? GetTypeArgumentConstraintWorker(Node node)
    {
        var typeArgumentPosition = -1;
        if (AstUtilities.HasTypeArguments(node.Parent!))
        {
            var typeArgs = node.Parent!.TypeArguments() ?? [];
            for (var i = 0; i < typeArgs.Length; i++)
            {
                if (typeArgs[i] == node)
                {
                    typeArgumentPosition = i;
                    break;
                }
            }
        }

        if (typeArgumentPosition >= 0)
        {
            // The node could be a type argument of a call, a `new` expression, a decorator, an
            // instantiation expression, or a generic type instantiation.

            if (AstUtilities.IsCallLikeExpression(node.Parent!))
            {
                return GetTypeParameterConstraintForPositionAcrossSignatures(
                    GetUninstantiatedSignatures(node.Parent!),
                    typeArgumentPosition);
            }

            if (NodeTests.IsDecorator(node.Parent!.Parent!))
            {
                return GetTypeParameterConstraintForPositionAcrossSignatures(
                    GetUninstantiatedSignatures(node.Parent.Parent!),
                    typeArgumentPosition);
            }

            if (NodeTests.IsExpressionWithTypeArguments(node.Parent) && NodeTests.IsExpressionStatement(node.Parent.Parent!))
            {
                var uninstantiatedType = CheckExpression(node.Parent.Expression()!);

                var callConstraint = GetTypeParameterConstraintForPositionAcrossSignatures(
                    GetSignaturesOfType(uninstantiatedType, SignatureKind.Call),
                    typeArgumentPosition);
                var constructConstraint = GetTypeParameterConstraintForPositionAcrossSignatures(
                    GetSignaturesOfType(uninstantiatedType, SignatureKind.Construct),
                    typeArgumentPosition);

                // An instantiation expression instantiates both call and construct signatures, so
                // if both exist type arguments must be assignable to both constraints.
                if ((constructConstraint.flags & TypeFlags.Never) != 0)
                {
                    return callConstraint;
                }
                if ((callConstraint.flags & TypeFlags.Never) != 0)
                {
                    return constructConstraint;
                }
                return GetIntersectionType([callConstraint, constructConstraint]);
            }

            if (AstUtilities.IsTypeReferenceType(node.Parent))
            {
                var typeParameters = GetTypeParametersForTypeReferenceOrImport(node.Parent);
                if (typeParameters is not { Length: > 0 })
                {
                    return null;
                }
                if (typeArgumentPosition >= typeParameters.Length)
                {
                    return null;
                }
                var relevantTypeParameter = typeParameters[typeArgumentPosition];
                var constraint = GetConstraintOfTypeParameter(relevantTypeParameter);
                if (constraint != null)
                {
                    return InstantiateType(
                        constraint,
                        TypeMapper.NewTypeMapper(typeParameters, GetEffectiveTypeArguments(node.Parent, typeParameters)!));
                }
            }
        }
        return null;
    }

    // services.go:17-19 — GetSymbolsInScope.
    public Symbol[] GetSymbolsInScope(Node location, SymbolFlags meaning) => GetSymbolsInScopeWorker(location, meaning);

    // services.go:21-119 — getSymbolsInScope.
    private Symbol[] GetSymbolsInScopeWorker(Node locationIn, SymbolFlags meaning)
    {
        var location = (Node?)locationIn;
        if ((location!.Flags & NodeFlags.InWithStatement) != 0)
        {
            // We cannot answer semantic questions within a with block, do not proceed any further
            return [];
        }

        var symbols = new SymbolTable();
        var isStaticSymbol = false;

        // Copy the given symbol into symbol tables if the symbol has the given meaning
        // and it doesn't already exists in the symbol table.
        void CopySymbol(Symbol symbol, SymbolFlags meaning)
        {
            if ((symbol.CombinedLocalAndExportSymbolFlags() & meaning) != 0)
            {
                var id = symbol.Name;
                // We will copy all symbol regardless of its reserved name because
                // symbolsToArray will check whether the key is a reserved name and
                // it will not copy symbol with reserved name to the array
                if (!symbols.ContainsKey(id))
                {
                    symbols[id] = symbol;
                }
            }
        }

        void CopySymbols(SymbolTable? source, SymbolFlags meaning)
        {
            if (meaning != 0 && source != null)
            {
                foreach (var symbol in source.Values) // map-iter: 序无关 (unique keys per table; result is a name-keyed set and LS consumers sort)
                {
                    CopySymbol(symbol, meaning);
                }
            }
        }

        void CopyLocallyVisibleExportSymbols(SymbolTable? source, SymbolFlags meaning)
        {
            if (meaning != 0 && source != null)
            {
                foreach (var symbol in source.Values) // map-iter: 序无关 (same reasoning as CopySymbols)
                {
                    // Similar condition as in `resolveNameHelper`
                    if (AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.ExportSpecifier) == null &&
                        AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.NamespaceExport) == null &&
                        symbol.Name != InternalSymbolName.Default)
                    {
                        CopySymbol(symbol, meaning);
                    }
                }
            }
        }

        void PopulateSymbols()
        {
            while (location != null)
            {
                if (CanHaveLocals(location) && location.Locals() != null && !AstUtilities.IsGlobalSourceFile(location))
                {
                    CopySymbols(location.Locals(), meaning);
                }

                switch (location.Kind)
                {
                    case SyntaxKind.SourceFile:
                        if (!AstUtilities.IsExternalModule(location.AsSourceFile()))
                        {
                            break;
                        }
                        goto case SyntaxKind.ModuleDeclaration;
                    case SyntaxKind.ModuleDeclaration:
                        CopyLocallyVisibleExportSymbols(GetSymbolOfDeclaration(location)!.Exports, meaning & SymbolFlags.ModuleMember);
                        break;
                    case SyntaxKind.EnumDeclaration:
                        CopySymbols(GetSymbolOfDeclaration(location)!.Exports, meaning & SymbolFlags.EnumMember);
                        break;
                    case SyntaxKind.ClassExpression:
                        var className = location.AsClassExpression().Name();
                        if (className != null)
                        {
                            CopySymbol(location.Symbol()!, meaning);
                        }
                        // this fall-through is necessary because we would like to handle
                        // type parameter inside class expression similar to how we handle it in classDeclaration and interface Declaration.
                        goto case SyntaxKind.ClassDeclaration;
                    case SyntaxKind.ClassDeclaration:
                    case SyntaxKind.InterfaceDeclaration:
                        // If we didn't come from static member of class or interface,
                        // add the type parameters into the symbol table
                        // (type parameters of classDeclaration/classExpression and interface are in member property of the symbol.
                        // Note: that the memberFlags come from previous iteration.
                        if (!isStaticSymbol)
                        {
                            CopySymbols(GetMembersOfSymbol(GetSymbolOfDeclaration(location)!), meaning & SymbolFlags.Type);
                        }
                        break;
                    case SyntaxKind.FunctionExpression:
                        var funcName = location.Name();
                        if (funcName != null)
                        {
                            CopySymbol(location.Symbol()!, meaning);
                        }
                        break;
                }

                if (IntroducesArgumentsExoticObject(location))
                {
                    CopySymbol(argumentsSymbol, meaning);
                }

                isStaticSymbol = AstUtilities.IsStatic(location);
                location = location.Parent;
            }

            CopySymbols(globals, meaning);
        }

        PopulateSymbols();

        symbols.Remove(InternalSymbolName.This); // Not a symbol, a keyword
        return SymbolsToArray(symbols);
    }

    // checker/utilities.go:1603-1610 — introducesArgumentsExoticObject.
    private static bool IntroducesArgumentsExoticObject(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
                return true;
        }
        return false;
    }

    // checker/utilities.go:1612-1620 — symbolsToArray.
    public static Symbol[] SymbolsToArray(SymbolTable symbols)
    {
        var result = new List<Symbol>();
        foreach (var (id, symbol) in symbols) // map-iter: 序无关 (result is treated as a set; LS consumers sort by display name)
        {
            if (!IsReservedMemberName(id))
            {
                result.Add(symbol);
            }
        }
        return result.ToArray();
    }

    // services.go:392-402 — GetRootSymbols.
    public Symbol[] GetRootSymbols(Symbol symbol)
    {
        var roots = GetImmediateRootSymbols(symbol);
        if (roots is not { Length: > 0 })
        {
            return [symbol];
        }
        var result = new List<Symbol>();
        foreach (var root in roots)
        {
            result.AddRange(GetRootSymbols(root));
        }
        return result.ToArray();
    }

    // services.go:404-409 — GetMappedTypeSymbolOfProperty.
    public Symbol? GetMappedTypeSymbolOfProperty(Symbol symbol)
    {
        if (valueSymbolLinks.TryGet(symbol) is { } valueLinks)
        {
            return valueLinks.containingType?.symbol;
        }
        return null;
    }

    // services.go:411-440 — getImmediateRootSymbols.
    private Symbol[]? GetImmediateRootSymbols(Symbol symbol)
    {
        if ((symbol.CheckFlags & CheckFlags.Synthetic) != 0)
        {
            return global::Ts.Core.CoreUtil.MapNonNil(
                valueSymbolLinks.Get(symbol).containingType!.Types(),
                t => GetPropertyOfType(t, symbol.Name))!;
        }
        if ((symbol.Flags & SymbolFlags.Transient) != 0)
        {
            if (spreadLinks.Has(symbol))
            {
                var leftSpread = spreadLinks.Get(symbol).leftSpread;
                var rightSpread = spreadLinks.Get(symbol).rightSpread;
                if (leftSpread != null)
                {
                    return [leftSpread, rightSpread!];
                }
            }
            if (mappedSymbolLinks.Has(symbol))
            {
                var syntheticOrigin = mappedSymbolLinks.Get(symbol).syntheticOrigin;
                if (syntheticOrigin != null)
                {
                    return [syntheticOrigin];
                }
            }
            var target = TryGetTarget(symbol);
            if (target != null)
            {
                return [target];
            }
        }
        return null;
    }

    // services.go:442-459 — tryGetTarget.
    private Symbol? TryGetTarget(Symbol symbol)
    {
        Symbol? target = null;
        var next = (Symbol?)symbol;
        while (true)
        {
            if (valueSymbolLinks.Has(next!))
            {
                next = valueSymbolLinks.Get(next!).target;
            }
            else if (exportTypeLinks.Has(next!))
            {
                next = exportTypeLinks.Get(next!).target;
            }
            else
            {
                next = null;
            }
            if (next == null)
            {
                break;
            }
            target = next;
        }
        return target;
    }

    // services.go:461-463 — GetExportSymbolOfSymbol.
    public Symbol GetExportSymbolOfSymbol(Symbol symbol)
    {
        return GetMergedSymbol(symbol.ExportSymbol ?? symbol)!;
    }

    // services.go:830-841 — GetExportsAndPropertiesOfModule.
    public Symbol[] GetExportsAndPropertiesOfModule(Symbol moduleSymbol)
    {
        var exports = GetExportsOfModuleAsArray(moduleSymbol);
        var exportEquals = ResolveExternalModuleSymbol(moduleSymbol, false /*dontResolveAlias*/);
        if (exportEquals != moduleSymbol)
        {
            var t = GetTypeOfSymbol(exportEquals!);
            if (ShouldTreatPropertiesOfExternalModuleAsExports(t))
            {
                exports = [.. exports, .. GetPropertiesOfType(t)];
            }
        }
        return exports;
    }

    // services.go:843-845 — getExportsOfModuleAsArray.
    private Symbol[] GetExportsOfModuleAsArray(Symbol moduleSymbol) => SymbolsToArray(GetExportsOfModule(moduleSymbol));

    // services.go:846-854 — GetJsxIntrinsicTagNamesAt: returns all the properties of the Jsx.IntrinsicElements interface.
    public Symbol[] GetJsxIntrinsicTagNamesAt(Node location)
    {
        var intrinsics = GetJsxType(JsxNames.IntrinsicElements, location);
        if (intrinsics == null)
        {
            return [];
        }
        return GetPropertiesOfType(intrinsics);
    }

    // services.go:856-858 — GetContextualTypeForJsxAttribute.
    public Type? GetContextualTypeForJsxAttribute(Node attribute) => GetContextualTypeForJsxAttribute(attribute, ContextFlags.None);

    // services.go:936-968 — GetContextualTypeForArrayLiteralAtPosition: returns the contextual type for an
    // element at the given position in an array with the given contextual type.
    public Type? GetContextualTypeForArrayLiteralAtPosition(Type? contextualArrayType, Node arrayLiteral, int position)
    {
        if (contextualArrayType == null)
        {
            return null;
        }
        int firstSpreadIndex = -1, lastSpreadIndex = -1;
        var elementIndex = 0;
        var elements = arrayLiteral.Elements() ?? [];
        for (var i = 0; i < elements.Length; i++)
        {
            var elem = elements[i];
            if (elem.Pos < position)
            {
                elementIndex++;
            }
            if (NodeTests.IsSpreadElement(elem))
            {
                if (firstSpreadIndex == -1)
                {
                    firstSpreadIndex = i;
                }
                lastSpreadIndex = i;
            }
        }
        // The array may be incomplete, so we don't know its final length.
        return GetContextualTypeForElementExpression(
            contextualArrayType,
            elementIndex,
            -1, /*length*/
            firstSpreadIndex,
            lastSpreadIndex);
    }
}
