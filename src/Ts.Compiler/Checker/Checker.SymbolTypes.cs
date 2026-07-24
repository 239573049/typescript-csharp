// Ported from tsgo internal/checker/checker.go (section: symbol -> type, lines ~16406-17288).
// plan/03 §4.2 #11 (SymbolTypes cluster): getTypeOfSymbol dispatch + variable/parameter/property
// symbol types + type-parameter/indexed-access/conditional constraints + declared class/interface
// type.
//
// The variable-typing worker's deepest helper (getTypeForVariableLikeDeclaration and its widening
// helpers) and getInferredTypeParameterConstraint drag in a very large expression/flow surface;
// they are left as `NotImplementedException` stubs in the cross-cluster region below and reported
// as remaining. Real ports call the stubs freely (build stays green).

using Ts.Ast;
using Ts.Core;
using static Ts.Ast.NodeTests;
using static Ts.Ast.AstUtilities;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:16314
    internal Type GetTypeOfSymbolWithDeferredType(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType == null)
        {
            var deferred = deferredSymbolLinks.Get(symbol);
            if ((deferred.parent!.flags & TypeFlags.Union) != 0)
            {
                links.resolvedType = GetUnionType(deferred.constituents!);
            }
            else
            {
                links.resolvedType = GetIntersectionType(deferred.constituents!);
            }
        }
        return links.resolvedType;
    }

    // checker.go:16327
    internal Type GetWriteTypeOfSymbolWithDeferredType(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.writeType == null)
        {
            var deferred = deferredSymbolLinks.Get(symbol);
            if (deferred.writeConstituents.Len() != 0)
            {
                if ((deferred.parent!.flags & TypeFlags.Union) != 0)
                {
                    links.writeType = GetUnionType(deferred.writeConstituents!);
                }
                else
                {
                    links.writeType = GetIntersectionType(deferred.writeConstituents!);
                }
            }
            else
            {
                links.writeType = GetTypeOfSymbolWithDeferredType(symbol);
            }
        }
        return links.writeType;
    }

    // checker.go:16347 — Distinct write types come only from set accessors.
    internal Type GetWriteTypeOfSymbol(Symbol symbol)
    {
        if ((symbol.CheckFlags & CheckFlags.SyntheticProperty) != 0)
        {
            if ((symbol.CheckFlags & CheckFlags.DeferredType) != 0)
            {
                return GetWriteTypeOfSymbolWithDeferredType(symbol);
            }
            var links = valueSymbolLinks.Get(symbol);
            return CoreUtil.OrElse(links.writeType, links.resolvedType)!;
        }
        if ((symbol.Flags & SymbolFlags.Property) != 0)
        {
            return RemoveMissingType(GetTypeOfSymbol(symbol), (symbol.Flags & SymbolFlags.Optional) != 0);
        }
        if ((symbol.Flags & SymbolFlags.Accessor) != 0)
        {
            if ((symbol.CheckFlags & CheckFlags.Instantiated) != 0)
            {
                return GetWriteTypeOfInstantiatedSymbol(symbol);
            }
            return GetWriteTypeOfAccessors(symbol);
        }
        return GetTypeOfSymbol(symbol);
    }

    // checker.go:16406
    public Type GetTypeOfSymbol(Symbol symbol)
    {
        if ((symbol.CheckFlags & CheckFlags.DeferredType) != 0)
        {
            return GetTypeOfSymbolWithDeferredType(symbol);
        }
        if ((symbol.CheckFlags & CheckFlags.Instantiated) != 0)
        {
            return GetTypeOfInstantiatedSymbol(symbol);
        }
        if ((symbol.CheckFlags & CheckFlags.Mapped) != 0)
        {
            return GetTypeOfMappedSymbol(symbol);
        }
        if ((symbol.CheckFlags & CheckFlags.ReverseMapped) != 0)
        {
            return GetTypeOfReverseMappedSymbol(symbol);
        }
        if ((symbol.Flags & SymbolFlags.Accessor) != 0)
        {
            return GetTypeOfAccessors(symbol);
        }
        if ((symbol.Flags & (SymbolFlags.Variable | SymbolFlags.Property)) != 0)
        {
            return GetTypeOfVariableOrParameterOrProperty(symbol);
        }
        if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method | SymbolFlags.Class | SymbolFlags.Enum | SymbolFlags.ValueModule)) != 0)
        {
            return GetTypeOfFuncClassEnumModule(symbol);
        }
        if ((symbol.Flags & SymbolFlags.EnumMember) != 0)
        {
            return GetTypeOfEnumMember(symbol);
        }
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            return GetTypeOfAlias(symbol);
        }
        return errorType!;
    }

    // checker.go:16437
    internal Type GetNonMissingTypeOfSymbol(Symbol symbol)
    {
        return RemoveMissingType(GetTypeOfSymbol(symbol), (symbol.Flags & SymbolFlags.Optional) != 0);
    }

    // checker.go:16441
    internal Type GetTypeOfInstantiatedSymbol(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType == null)
        {
            links.resolvedType = InstantiateType(GetTypeOfSymbol(links.target!), links.mapper);
        }
        return links.resolvedType;
    }

    // checker.go:16449
    internal Type GetWriteTypeOfInstantiatedSymbol(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.writeType == null)
        {
            links.writeType = InstantiateType(GetWriteTypeOfSymbol(links.target!), links.mapper);
        }
        return links.writeType;
    }

    // checker.go:16457
    internal Type GetTypeOfVariableOrParameterOrProperty(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType == null)
        {
            var t = GetTypeOfVariableOrParameterOrPropertyWorker(symbol);
            if (t == null)
            {
                throw new InvalidOperationException("Unexpected nil type");
            }
            // For a contextually typed parameter it is possible that a type has already been
            // assigned, and we want to preserve (and _prefer_) it; defer where contextual typing
            // may take place.
            if (links.resolvedType == null && !IsParameterOfContextSensitiveSignature(symbol))
            {
                links.resolvedType = t;
            }
            return t;
        }
        return links.resolvedType;
    }

    // checker.go:16477
    internal bool IsParameterOfContextSensitiveSignature(Symbol symbol)
    {
        var decl = symbol.ValueDeclaration;
        if (decl == null)
        {
            return false;
        }
        if (IsBindingElement(decl))
        {
            decl = WalkUpBindingElementsAndPatterns(decl);
        }
        if (IsParameterDeclaration(decl))
        {
            return IsContextSensitiveFunctionOrObjectLiteralMethod(decl.Parent!);
        }
        return false;
    }

    // checker.go:16491
    internal Type? GetTypeOfVariableOrParameterOrPropertyWorker(Symbol symbol)
    {
        // Handle prototype property
        if ((symbol.Flags & SymbolFlags.Prototype) != 0)
        {
            return GetTypeOfPrototypeProperty(symbol);
        }
        // CommonJS require and module both have type any.
        if (symbol == requireSymbol)
        {
            return anyType!;
        }
        Debug.Assert(symbol.ValueDeclaration != null);
        var declaration = symbol.ValueDeclaration!;
        if (IsSourceFile(declaration) && IsJsonSourceFile(declaration.AsSourceFile()))
        {
            var statements = declaration.Statements() ?? [];
            if (statements.Length == 0)
            {
                return emptyObjectType!;
            }
            return GetWidenedType(GetWidenedLiteralType(CheckExpression(statements[0].Expression()!)));
        }
        // Handle variable, parameter or property
        if (!PushTypeResolution(symbol, TypeSystemPropertyName.Type))
        {
            return ReportCircularityError(symbol);
        }
        if ((symbol.Flags & SymbolFlags.ModuleExports) != 0)
        {
            if (symbol.Name == "exports")
            {
                return GetTypeOfSymbol(ResolveExternalModuleSymbol(symbol.ValueDeclaration!.Symbol(), false /*dontResolveAlias*/)!);
            }
            return NewAnonymousType(symbol, symbol.Members, null, null, null);
        }
        Type? result;
        switch (declaration.Kind)
        {
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.BindingElement:
                result = GetWidenedTypeForVariableLikeDeclaration(declaration, true /*reportErrors*/);
                break;
            case SyntaxKind.PropertyAssignment:
                result = CheckPropertyAssignment(declaration, CheckMode.Normal);
                break;
            case SyntaxKind.ShorthandPropertyAssignment:
                result = CheckShorthandPropertyAssignment(declaration, true /*inDestructuringPattern*/, CheckMode.Normal);
                break;
            case SyntaxKind.MethodDeclaration:
                result = CheckObjectLiteralMethod(declaration, CheckMode.Normal);
                break;
            case SyntaxKind.ExportAssignment:
                if (declaration.Type() != null)
                {
                    result = GetTypeFromTypeNode(declaration.Type()!);
                }
                else
                {
                    result = WidenTypeForVariableLikeDeclaration(CheckExpressionCached(declaration.Expression()!), declaration, false /*reportErrors*/);
                }
                break;
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.CallExpression:
                result = GetWidenedTypeForAssignmentDeclaration(symbol);
                break;
            case SyntaxKind.JsxAttribute:
                result = CheckJsxAttribute(declaration, CheckMode.Normal);
                break;
            case SyntaxKind.EnumMember:
                result = GetTypeOfEnumMember(symbol);
                break;
            default:
                throw new InvalidOperationException("Unhandled case in getTypeOfVariableOrParameterOrPropertyWorker: " + declaration.Kind);
        }
        if (!PopTypeResolution())
        {
            return ReportCircularityError(symbol);
        }
        return result;
    }

    // checker.go:16560
    internal Type GetWidenedTypeForVariableLikeDeclaration(Node declaration, bool reportErrors)
    {
        return WidenTypeForVariableLikeDeclaration(GetTypeForVariableLikeDeclaration(declaration, /*includeOptionality*/ true, CheckMode.Normal), declaration, reportErrors);
    }

    // checker.go:16817
    internal Type GetTypeOfFuncClassEnumModule(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType == null)
        {
            links.resolvedType = GetTypeOfFuncClassEnumModuleWorker(symbol);
        }
        return links.resolvedType;
    }

    // checker.go:16825
    internal Type GetTypeOfFuncClassEnumModuleWorker(Symbol symbol)
    {
        if ((symbol.Flags & SymbolFlags.Module) != 0 && IsShorthandAmbientModuleSymbol(symbol))
        {
            return anyType!;
        }
        if ((symbol.Flags & SymbolFlags.ValueModule) != 0 && symbol.ValueDeclaration != null &&
            IsSourceFile(symbol.ValueDeclaration) && symbol.ValueDeclaration.AsSourceFile().CommonJSModuleIndicator != null)
        {
            var resolvedModule = ResolveExternalModuleSymbol(symbol, false /*dontResolveAlias*/);
            if (resolvedModule != symbol)
            {
                return GetTypeOfSymbol(resolvedModule!);
            }
        }
        var t = NewObjectType(ObjectFlags.Anonymous, symbol);
        if ((symbol.Flags & SymbolFlags.Class) != 0)
        {
            var baseTypeVariable = GetBaseTypeVariableOfClass(symbol);
            if (baseTypeVariable != null)
            {
                return GetIntersectionType([t, baseTypeVariable]);
            }
            return t;
        }
        if (strictNullChecks && (symbol.Flags & SymbolFlags.Optional) != 0)
        {
            return GetOptionalType(t, /*isProperty*/ true);
        }
        return t;
    }

    // checker.go:16849
    internal Type? GetBaseTypeVariableOfClass(Symbol symbol)
    {
        var baseConstructorType = GetBaseConstructorTypeOfClass(GetDeclaredTypeOfClassOrInterface(symbol));
        if ((baseConstructorType.flags & TypeFlags.TypeVariable) != 0)
        {
            return baseConstructorType;
        }
        if ((baseConstructorType.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Find(baseConstructorType.Types(), t => (t.flags & TypeFlags.TypeVariable) != 0);
        }
        return null;
    }

    // checker.go:16870 — base constructor of a class.
    public Type GetBaseConstructorTypeOfClass(Type t)
    {
        var data = t.AsInterfaceType()!;
        if (data.resolvedBaseConstructorType != null)
        {
            return data.resolvedBaseConstructorType;
        }
        var baseTypeNode = GetBaseTypeNodeOfClass(t);
        if (baseTypeNode == null)
        {
            data.resolvedBaseConstructorType = undefinedType!;
            return data.resolvedBaseConstructorType;
        }
        if (!PushTypeResolution(t, TypeSystemPropertyName.ResolvedBaseConstructorType))
        {
            return errorType!;
        }
        var baseConstructorType = CheckExpression(baseTypeNode.Expression()!);
        if ((baseConstructorType.flags & (TypeFlags.Object | TypeFlags.Intersection)) != 0)
        {
            // Force resolution here such that we catch circularities now.
            ResolveStructuredTypeMembers(baseConstructorType);
        }
        if (!PopTypeResolution())
        {
            Error(t.symbol!.ValueDeclaration, global::Ts.Diagnostics.Diagnostics.X_0_is_referenced_directly_or_indirectly_in_its_own_base_expression, SymbolToString(t.symbol));
            data.resolvedBaseConstructorType ??= errorType!;
            return data.resolvedBaseConstructorType;
        }
        if ((baseConstructorType.flags & TypeFlags.Any) == 0 && baseConstructorType != nullWideningType && !IsConstructorType(baseConstructorType))
        {
            var err = Error(baseTypeNode.Expression(), global::Ts.Diagnostics.Diagnostics.Type_0_is_not_a_constructor_function_type, TypeToString(baseConstructorType));
            if ((baseConstructorType.flags & TypeFlags.TypeParameter) != 0)
            {
                var constraint = GetConstraintFromTypeParameter(baseConstructorType);
                Type ctorReturn = unknownType!;
                if (constraint != null)
                {
                    var ctorSigs = GetSignaturesOfType(constraint, SignatureKind.Construct);
                    if (ctorSigs.Length != 0)
                    {
                        ctorReturn = GetReturnTypeOfSignature(ctorSigs[0]);
                    }
                }
                if (baseConstructorType.symbol!.Declarations != null)
                {
                    err?.AddRelatedInfo(CreateDiagnosticForNode(baseConstructorType.symbol.Declarations[0], global::Ts.Diagnostics.Diagnostics.Did_you_mean_for_0_to_be_constrained_to_type_new_args_Colon_any_1, SymbolToString(baseConstructorType.symbol), TypeToString(ctorReturn)));
                }
            }
            data.resolvedBaseConstructorType ??= errorType!;
            return data.resolvedBaseConstructorType;
        }
        data.resolvedBaseConstructorType ??= baseConstructorType;
        return data.resolvedBaseConstructorType;
    }

    // checker.go:16922
    internal bool IsFunctionType(Type t)
    {
        return (t.flags & TypeFlags.Object) != 0 && GetSignaturesOfType(t, SignatureKind.Call).Length > 0;
    }

    // checker.go:16926
    internal bool IsConstructorType(Type t)
    {
        if (GetSignaturesOfType(t, SignatureKind.Construct).Length > 0)
        {
            return true;
        }
        if ((t.flags & TypeFlags.TypeVariable) != 0)
        {
            var constraint = GetBaseConstraintOfType(t);
            return constraint != null && IsMixinConstructorType(constraint);
        }
        return false;
    }

    // checker.go:16939 — mixin constructor: single construct sig, no type params, single any[] rest param.
    internal bool IsMixinConstructorType(Type t)
    {
        var signatures = GetSignaturesOfType(t, SignatureKind.Construct);
        if (signatures.Length == 1)
        {
            var s = signatures[0];
            if (s.typeParameters.Len() == 0 && s.parameters.Len() == 1 && SignatureHasRestParameter(s))
            {
                var paramType = GetTypeOfParameter(s.parameters![0]);
                return IsTypeAny(paramType) || GetElementTypeOfArrayType(paramType) == anyType;
            }
        }
        return false;
    }

    // checker.go:16951
    private static bool SignatureHasRestParameter(Signature sig)
    {
        return (sig.flags & SignatureFlags.HasRestParameter) != 0;
    }

    // checker.go:16955
    internal Type GetTypeOfParameter(Symbol symbol)
    {
        var declaration = symbol.ValueDeclaration;
        return AddOptionalityEx(GetTypeOfSymbol(symbol), false, declaration != null && (declaration.Initializer() != null || IsOptionalDeclaration(declaration)));
    }

    // checker.go:16960
    internal Type? GetConstraintOfType(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            return GetConstraintOfTypeParameter(t);
        }
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            return GetConstraintOfIndexedAccess(t);
        }
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            return GetConstraintOfConditionalType(t);
        }
        return GetBaseConstraintOfType(t);
    }

    // checker.go:16972
    public Type? GetConstraintOfTypeParameter(Type typeParameter)
    {
        if (HasNonCircularBaseConstraint(typeParameter))
        {
            return GetConstraintFromTypeParameter(typeParameter);
        }
        return null;
    }

    // checker.go:16979
    internal bool HasNonCircularBaseConstraint(Type t)
    {
        return GetResolvedBaseConstraint(t, null) != circularConstraintType;
    }

    // checker.go:16984 — worker; use getConstraintOfTypeParameter which guards circular constraints.
    internal Type? GetConstraintFromTypeParameter(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) == 0)
        {
            return null;
        }
        var tp = t.AsTypeParameter();
        if (tp.constraint == null)
        {
            Type? constraint;
            if (tp.target != null)
            {
                constraint = InstantiateType(GetConstraintOfTypeParameter(tp.target)!, tp.mapper);
            }
            else
            {
                var constraintDeclaration = GetConstraintDeclaration(t);
                if (constraintDeclaration != null)
                {
                    constraint = GetTypeFromTypeNode(constraintDeclaration);
                    if ((constraint.flags & TypeFlags.Any) != 0 && !IsErrorType(constraint))
                    {
                        if (IsMappedTypeNode(constraintDeclaration.Parent!.Parent!))
                        {
                            constraint = stringNumberSymbolType!;
                        }
                        else
                        {
                            constraint = unknownType!;
                        }
                    }
                }
                else
                {
                    constraint = GetInferredTypeParameterConstraint(t, false);
                }
            }
            constraint ??= noConstraintType!;
            tp.constraint = constraint;
        }
        if (tp.constraint != noConstraintType)
        {
            return tp.constraint;
        }
        return null;
    }

    // checker.go:17022
    internal Type GetConstraintOrUnknownFromTypeParameter(Type t)
    {
        var result = GetConstraintFromTypeParameter(t);
        return result ?? unknownType!;
    }

    // checker.go:17100
    internal Type[]? GetTypeParametersForTypeReferenceOrImport(Node node)
    {
        var t = GetTypeFromTypeNode(node);
        if (!IsErrorType(t))
        {
            var symbol = GetResolvedSymbolOrNil(node);
            if (symbol != null)
            {
                return GetTypeParametersForTypeAndSymbol(t, symbol);
            }
        }
        return null;
    }

    // checker.go:17111
    internal Type[]? GetTypeParametersForTypeAndSymbol(Type t, Symbol symbol)
    {
        if (!IsErrorType(t))
        {
            if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
            {
                var typeParameters = typeAliasLinks.Get(symbol).typeParameters;
                if (typeParameters.Len() != 0)
                {
                    return typeParameters;
                }
            }
            if ((t.objectFlags & ObjectFlags.Reference) != 0)
            {
                return t.Target().AsInterfaceType()!.LocalTypeParameters();
            }
        }
        return null;
    }

    // checker.go:17125
    internal Type GetEffectiveTypeArgumentAtIndex(Node node, Type[] typeParameters, int index)
    {
        var typeArguments = node.TypeArguments();
        if (index < typeArguments.Len())
        {
            return GetTypeFromTypeNode(typeArguments![index]);
        }
        return GetEffectiveTypeArguments(node, typeParameters)[index];
    }

    // checker.go:17133
    internal Type? GetConstraintOfIndexedAccess(Type t)
    {
        if (HasNonCircularBaseConstraint(t))
        {
            return GetConstraintFromIndexedAccess(t);
        }
        return null;
    }

    // checker.go:17140
    internal Type? GetConstraintFromIndexedAccess(Type t)
    {
        var d = t.AsIndexedAccessType();
        if (IsMappedTypeGenericIndexedAccess(t))
        {
            // For { [P in K]: E }[X], where K is non-generic and X is generic, substitute E with P->X.
            return SubstituteIndexedMappedType(d.objectType, d.indexType);
        }
        var indexConstraint = GetSimplifiedTypeOrConstraint(d.indexType);
        if (indexConstraint != null && indexConstraint != d.indexType)
        {
            var indexedAccess = GetIndexedAccessTypeOrUndefined(d.objectType, indexConstraint, d.accessFlags, null, null);
            if (indexedAccess != null)
            {
                return indexedAccess;
            }
        }
        var objectConstraint = GetSimplifiedTypeOrConstraint(d.objectType);
        if (objectConstraint != null && objectConstraint != d.objectType)
        {
            return GetIndexedAccessTypeOrUndefined(objectConstraint, d.indexType, d.accessFlags, null, null);
        }
        return null;
    }

    // checker.go:17161
    internal Type? GetConstraintOfConditionalType(Type t)
    {
        if (HasNonCircularBaseConstraint(t))
        {
            return GetConstraintFromConditionalType(t);
        }
        return null;
    }

    // checker.go:17168
    internal Type GetConstraintFromConditionalType(Type t)
    {
        var constraint = GetConstraintOfDistributiveConditionalType(t);
        if (constraint != null)
        {
            return constraint;
        }
        return GetDefaultConstraintOfConditionalType(t);
    }

    // checker.go:17176
    internal Type GetDefaultConstraintOfConditionalType(Type t)
    {
        var d = t.AsConditionalType();
        if (d.resolvedDefaultConstraint == null)
        {
            // Elide an `any` branch from the constraint type — treat `any` like `never` here.
            var trueConstraint = GetInferredTrueTypeFromConditionalType(t);
            var falseConstraint = GetFalseTypeFromConditionalType(t);
            if (IsTypeAny(trueConstraint))
            {
                d.resolvedDefaultConstraint = falseConstraint;
            }
            else if (IsTypeAny(falseConstraint))
            {
                d.resolvedDefaultConstraint = trueConstraint;
            }
            else
            {
                d.resolvedDefaultConstraint = GetUnionType([trueConstraint, falseConstraint]);
            }
        }
        return d.resolvedDefaultConstraint;
    }

    // checker.go:17197 — distributive conditional-type constraint.
    internal Type? GetConstraintOfDistributiveConditionalType(Type t)
    {
        var d = t.AsConditionalType();
        if (d.resolvedConstraintOfDistributive == null)
        {
            // If T (a constrained type parameter) extends U ? X : Y, instantiate replacing T with its
            // constraint, so a union constraint distributes and possibly reduces.
            if (d.root.isDistributive && cachedTypes.GetValueOrDefault(new CachedTypeKey(CachedTypeKind.RestrictiveInstantiation, t.id)) != t)
            {
                var constraint = GetSimplifiedType(d.checkType!, false /*writing*/);
                if (constraint == d.checkType)
                {
                    constraint = GetConstraintOfType(constraint);
                }
                if (constraint != null && constraint != d.checkType)
                {
                    var instantiated = GetConditionalTypeInstantiation(t, TypeMapper.PrependTypeMapping(d.root.checkType!, constraint, d.mapper), true /*forConstraint*/, null);
                    if ((instantiated.flags & TypeFlags.Never) == 0)
                    {
                        d.resolvedConstraintOfDistributive = instantiated;
                        return instantiated;
                    }
                }
            }
            d.resolvedConstraintOfDistributive = noConstraintType;
        }
        if (d.resolvedConstraintOfDistributive != noConstraintType)
        {
            return d.resolvedConstraintOfDistributive;
        }
        return null;
    }

    // checker.go:17232
    internal Type GetDeclaredTypeOfClassOrInterface(Symbol symbol)
    {
        var links = declaredTypeLinks.Get(symbol);
        if (links.declaredType == null)
        {
            var kind = CoreUtil.IfElse((symbol.Flags & SymbolFlags.Class) != 0, ObjectFlags.Class, ObjectFlags.Interface);
            var t = NewObjectType(kind, symbol);
            links.declaredType = t;
            var outerTypeParameters = GetOuterTypeParametersOfClassOrInterface(symbol);
            var typeParameters = AppendLocalTypeParametersOfClassOrInterfaceOrTypeAlias(outerTypeParameters, symbol);
            // A class or interface is generic if it has type parameters or a "this" type. Classes always
            // get a "this" type; interfaces get one unless provably thisless.
            if (typeParameters != null || kind == ObjectFlags.Class || !IsThislessInterface(symbol))
            {
                t.objectFlags |= ObjectFlags.Reference;
                var d = t.AsInterfaceType()!;
                d.thisType = NewTypeParameter(symbol);
                d.thisType.AsTypeParameter().isThisType = true;
                d.thisType.AsTypeParameter().constraint = t;
                d.allTypeParameters = AppendType(typeParameters, d.thisType);
                d.outerTypeParameterCount = outerTypeParameters.Len();
                d.resolvedTypeArguments = d.TypeParameters();
                d.instantiations = new Dictionary<CacheHashKey, Type>();
                d.instantiations[GetTypeListKey(d.resolvedTypeArguments)] = t;
                d.target = t;
            }
        }
        return links.declaredType;
    }

    // checker.go:17269 — true if the interface given by the symbol is free of "this" references.
    internal bool IsThislessInterface(Symbol symbol)
    {
        foreach (var declaration in symbol.Declarations.OrEmpty())
        {
            if (IsInterfaceDeclaration(declaration))
            {
                if ((declaration.Flags & NodeFlags.ContainsThis) != 0)
                {
                    return false;
                }
                var baseTypeNodes = GetExtendsHeritageClauseElements(declaration);
                foreach (var node in baseTypeNodes)
                {
                    if (IsEntityNameExpression(node.Expression()!))
                    {
                        var baseSymbol = ResolveEntityName(node.Expression()!, SymbolFlags.Type, true /*ignoreErrors*/, false, null);
                        if (baseSymbol == null || (baseSymbol.Flags & SymbolFlags.Interface) == 0 || GetDeclaredTypeOfClassOrInterface(baseSymbol).AsInterfaceType()!.thisType != null)
                        {
                            return false;
                        }
                    }
                }
            }
        }
        return true;
    }

    // ===================================================================================
    // Cross-cluster stubs (methods OUTSIDE lines 16406-17288 that the ports above call).
    // Shared helpers (GetMergedSymbol/PushTypeResolution/PopTypeResolution/SymbolToString/
    // ResolveExternalModuleSymbol/CreateDiagnosticForNode/IsTypeUsableAsPropertyName/etc.) are
    // declared once in Checker.SymbolResolution.cs — not repeated here.
    // ===================================================================================

    // #11 dispatch targets in other type-of clusters.
    // getTypeOfMappedSymbol (20889) implemented in Checker.Instantiation.cs (#14).
    // getTypeOfReverseMappedSymbol (inference.go:1111) implemented in Checker.Inference.cs (inference wave).







    // checker.go — removeMissingType(t, isOptional)
    internal Type RemoveMissingType(Type t, bool isOptional)
    {
        if (exactOptionalPropertyTypes && isOptional)
        {
            return RemoveType(t, missingType!);
        }
        return t;
    }

    // Return the inferred type for a variable, parameter, or property declaration
    // checker.go:16565
    internal Type? GetTypeForVariableLikeDeclaration(Node declaration, bool includeOptionality, CheckMode checkMode)
    {
        // A variable declared in a for..in statement is of type string, or of type keyof T when the
        // right hand expression is of a type parameter type.
        if (NodeTests.IsVariableDeclaration(declaration))
        {
            var grandParent = declaration.Parent!.Parent!;
            switch (grandParent.Kind)
            {
                case SyntaxKind.ForInStatement:
                    var indexType = GetIndexType(GetNonNullableTypeIfNeeded(CheckExpressionEx(grandParent.Expression()!, checkMode /*checkMode*/)));
                    if ((indexType.flags & (TypeFlags.TypeParameter | TypeFlags.Index)) != 0)
                    {
                        return GetExtractStringType(indexType);
                    }
                    return stringType;
                case SyntaxKind.ForOfStatement:
                    // checkRightHandSideOfForOf will return undefined if the for-of expression type was
                    // missing properties/signatures required to get its iteratedType (like
                    // [Symbol.iterator] or next). This may be because we accessed properties from anyType,
                    // or it may have led to an error inside getElementTypeOfIterable.
                    return CheckRightHandSideOfForOf(grandParent);
            }
        }
        else if (NodeTests.IsBindingElement(declaration))
        {
            return GetTypeForBindingElement(declaration);
        }
        var isProperty = NodeTests.IsPropertyDeclaration(declaration) && !HasAccessorModifier(declaration) || NodeTests.IsPropertySignatureDeclaration(declaration);
        var isOptional = includeOptionality && IsOptionalDeclaration(declaration);
        // Use type from type annotation if one is present
        var declaredType = TryGetTypeFromTypeNode(declaration);
        if (AstUtilities.IsCatchClauseVariableDeclarationOrBindingElement(declaration))
        {
            if (declaredType != null)
            {
                // If the catch clause is explicitly annotated with any or unknown, accept it, otherwise error.
                if ((declaredType.flags & TypeFlags.AnyOrUnknown) != 0)
                {
                    return declaredType;
                }
                return errorType;
            }
            // If the catch clause is not explicitly annotated, treat it as though it were explicitly
            // annotated with unknown or any, depending on useUnknownInCatchVariables.
            if (useUnknownInCatchVariables)
            {
                return unknownType;
            }
            else
            {
                return anyType;
            }
        }
        if (declaredType != null)
        {
            return AddOptionalityEx(declaredType, isProperty, isOptional);
        }
        if (noImplicitAny && NodeTests.IsVariableDeclaration(declaration) && !AstUtilities.IsBindingPattern(declaration.Name()) &&
            (GetCombinedModifierFlagsCached(declaration) & ModifierFlags.Export) == 0 && (declaration.Flags & NodeFlags.Ambient) == 0)
        {
            // If --noImplicitAny is on or the declaration is in a Javascript file,
            // use control flow tracked 'any' type for non-ambient, non-exported var or let variables with no
            // initializer or a 'null' or 'undefined' initializer.
            var initializer = declaration.Initializer();
            if ((GetCombinedNodeFlagsCached(declaration) & NodeFlags.Constant) == 0 && (initializer == null || IsNullOrUndefined(initializer)))
            {
                return autoType;
            }
            // Use control flow tracked 'any[]' type for non-ambient, non-exported variables with an empty array
            // literal initializer.
            if (initializer != null && AstUtilities.IsEmptyArrayLiteral(initializer))
            {
                return autoArrayType;
            }
        }
        if (NodeTests.IsParameterDeclaration(declaration))
        {
            if (declaration.Symbol() == null)
            {
                // parameters of function types defined in JSDoc in TS files don't have symbols
                return null;
            }
            var fn = declaration.Parent!;
            // For a parameter of a set accessor, use the type of the get accessor if one is present
            if (NodeTests.IsSetAccessorDeclaration(fn) && HasBindableName(fn))
            {
                var getter = AstUtilities.GetDeclarationOfKind(GetSymbolOfDeclaration(declaration.Parent!)!, SyntaxKind.GetAccessor);
                if (getter != null)
                {
                    var getterSignature = GetSignatureFromDeclaration(getter);
                    var thisParameter = GetAccessorThisParameter(fn);
                    if (thisParameter != null && declaration == thisParameter)
                    {
                        // Use the type from the *getter*
                        global::Ts.Core.Debug.Assert(thisParameter.Type() == null);
                        return GetTypeOfSymbol(getterSignature.thisParameter!);
                    }
                    return GetReturnTypeOfSignature(getterSignature);
                }
            }
            {
                var t0 = GetParameterTypeOfFullSignature(fn, declaration);
                if (t0 != null)
                {
                    return t0;
                }
            }
            // Use contextual parameter type if one is available
            Type? t;
            if (declaration.Symbol()!.Name == InternalSymbolName.This)
            {
                t = GetContextualThisParameterType(fn);
            }
            else
            {
                t = GetContextuallyTypedParameterType(declaration);
            }
            if (t != null)
            {
                return AddOptionalityEx(t, false /*isProperty*/, isOptional);
            }
        }
        // Use the type of the initializer expression if one is present and the declaration is
        // not a parameter of a contextually typed function
        if (declaration.Initializer() != null)
        {
            var t = WidenTypeInferredFromInitializer(declaration, CheckDeclarationInitializer(declaration, checkMode, null /*contextualType*/));
            return AddOptionalityEx(t, isProperty, isOptional);
        }
        if (noImplicitAny && NodeTests.IsPropertyDeclaration(declaration))
        {
            // We have a property declaration with no type annotation or initializer, in noImplicitAny mode or a .js file.
            // Use control flow analysis of this.xxx assignments in the constructor or static block to determine the type of the property.
            if (!HasStaticModifier(declaration))
            {
                var constructor = AstUtilities.FindConstructorDeclaration(declaration.Parent!);
                Type? t = null;
                if (constructor != null)
                {
                    t = GetFlowTypeInConstructor(declaration.Symbol()!, constructor);
                }
                else if ((declaration.ModifierFlags() & ModifierFlags.Ambient) != 0)
                {
                    t = GetTypeOfPropertyInBaseClass(declaration.Symbol()!);
                }
                if (t == null)
                {
                    return null;
                }
                return AddOptionalityEx(t, true /*isProperty*/, isOptional);
            }
            else
            {
                var staticBlocks = global::Ts.Core.CoreUtil.Filter(declaration.Parent!.Members(), NodeTests.IsClassStaticBlockDeclaration);
                Type? t = null;
                if (staticBlocks.Len() != 0)
                {
                    t = GetFlowTypeInStaticBlocks(declaration.Symbol()!, staticBlocks!);
                }
                else if ((declaration.ModifierFlags() & ModifierFlags.Ambient) != 0)
                {
                    t = GetTypeOfPropertyInBaseClass(declaration.Symbol()!);
                }
                if (t == null)
                {
                    return null;
                }
                return AddOptionalityEx(t, true /*isProperty*/, isOptional);
            }
        }
        if (NodeTests.IsJsxAttribute(declaration))
        {
            // if JSX attribute doesn't have initializer, by default the attribute will have boolean value of true.
            // I.e <Elem attr /> is sugar for <Elem attr={true} />
            return trueType;
        }
        // If the declaration specifies a binding pattern and is not a parameter of a contextually
        // typed function, use the type implied by the binding pattern
        if (AstUtilities.IsBindingPattern(declaration.Name()))
        {
            return GetTypeFromBindingPattern(declaration.Name()!, false /*includePatternInType*/, true /*reportErrors*/);
        }
        // No type specified and nothing can be inferred
        return null;
    }




    // checker.go:11990
    internal Type? GetContextualThisParameterType(Node fn)
    {
        if (NodeTests.IsArrowFunction(fn))
        {
            return null;
        }
        if (IsContextSensitiveFunctionOrObjectLiteralMethod(fn))
        {
            var contextualSignature = GetContextualSignature(fn);
            if (contextualSignature != null)
            {
                var thisParameter = contextualSignature.thisParameter;
                if (thisParameter != null)
                {
                    return GetTypeOfSymbol(thisParameter);
                }
            }
        }
        var inJs = AstUtilities.IsInJSFile(fn);
        if (noImplicitThis || inJs)
        {
            var containingLiteral = GetContainingObjectLiteral(fn);
            if (containingLiteral != null)
            {
                // We have an object literal method. Check if the containing object literal has a contextual type
                // that includes a ThisType<T>. If so, T is the contextual type for 'this'. We continue looking in
                // any directly enclosing object literals.
                var contextualType = GetApparentTypeOfContextualType(containingLiteral, ContextFlags.None);
                var thisType = GetThisTypeOfObjectLiteralFromContextualType(containingLiteral, contextualType);
                if (thisType != null)
                {
                    return InstantiateType(thisType, GetMapperFromContext(GetInferenceContext(containingLiteral)));
                }
                // There was no contextual ThisType<T> for the containing object literal, so the contextual type
                // for 'this' is the non-null form of the contextual type for the containing object literal or
                // the type of the object literal itself.
                if (contextualType != null)
                {
                    thisType = GetNonNullableType(contextualType);
                }
                else
                {
                    thisType = CheckExpressionCached(containingLiteral);
                }
                return GetWidenedType(thisType);
            }
            // In an assignment of the form 'obj.xxx = function(...)' or 'obj[xxx] = function(...)', the
            // contextual type for 'this' is 'obj'.
            var parent = AstUtilities.WalkUpParenthesizedExpressions(fn.Parent)!;
            if (AstUtilities.IsAssignmentExpression(parent, false))
            {
                var target = parent.AsBinaryExpression().Left!;
                if (AstUtilities.IsAccessExpression(target))
                {
                    var expression = target.Expression()!;
                    // Don't contextually type `this` as `exports` in `exports.Point = function(x, y) { this.x = x; this.y = y; }`
                    if (inJs && NodeTests.IsIdentifier(expression))
                    {
                        var sourceFile = AstUtilities.GetSourceFileOfNode(parent);
                        if (sourceFile!.CommonJSModuleIndicator != null && (GetResolvedSymbol(expression).Flags & SymbolFlags.ModuleExports) != 0)
                        {
                            return null;
                        }
                    }
                    return GetWidenedType(CheckExpressionCached(expression));
                }
            }
        }
        return null;
    }

    // utilities.go:1097
    internal static Node? GetContainingObjectLiteral(Node f)
    {
        if ((f.Kind == SyntaxKind.MethodDeclaration ||
            f.Kind == SyntaxKind.GetAccessor ||
            f.Kind == SyntaxKind.SetAccessor) && f.Parent!.Kind == SyntaxKind.ObjectLiteralExpression)
        {
            return f.Parent;
        }
        else if (f.Kind == SyntaxKind.FunctionExpression && f.Parent!.Kind == SyntaxKind.PropertyAssignment)
        {
            return f.Parent.Parent;
        }
        return null;
    }
    // checker.go:29215
    internal Type? GetContextuallyTypedParameterType(Node parameter)
    {
        var fn = parameter.Parent!;
        if (!IsContextSensitiveFunctionOrObjectLiteralMethod(fn))
        {
            return null;
        }
        var iife = AstUtilities.GetImmediatelyInvokedFunctionExpression(fn);
        if (iife is not null)
        {
            var args = GetEffectiveCallArguments(iife);
            var indexOfParameter = Array.IndexOf(fn.Parameters()!, parameter);
            if (HasDotDotDotToken(parameter))
            {
                return GetSpreadArgumentType(args, indexOfParameter, args.Length, anyType!, null /*context*/, CheckMode.Normal);
            }
            var links = signatureLinks.Get(iife);
            var cached = links.resolvedSignature;
            links.resolvedSignature = anySignature;
            Type? t;
            if (indexOfParameter < args.Length)
            {
                t = GetWidenedLiteralType(CheckExpression(args[indexOfParameter]));
            }
            else if (parameter.Initializer() is not null)
            {
                t = null;
            }
            else
            {
                t = undefinedWideningType;
            }
            links.resolvedSignature = cached;
            return t;
        }
        var contextualSignature = GetContextualSignature(fn);
        if (contextualSignature is not null)
        {
            var index = Array.IndexOf(fn.Parameters()!, parameter) - CoreUtil.IfElse(AstUtilities.GetThisParameter(fn) is not null, 1, 0);
            if (HasDotDotDotToken(parameter) && CoreUtil.LastOrNil(fn.Parameters()) == parameter)
            {
                return GetRestTypeAtPosition(contextualSignature, index, false);
            }
            return TryGetTypeAtPosition(contextualSignature, index);
        }
        return null;
    }

    // GetWidenedLiteralType is now REAL in Checker.Widening.cs (checker.go:25395).

    // checker.go:13303-13317
    internal Type CheckPropertyAssignment(Node node, CheckMode checkMode)
    {
        // Do not use hasDynamicName here, because that returns false for well known symbols.
        // We want to perform checkComputedPropertyName for all computed properties, including
        // well known symbols.
        if (NodeTests.IsComputedPropertyName(node.Name()!))
        {
            CheckComputedPropertyName(node.Name()!);
        }
        var initializerType = CheckExpressionForMutableLocation(node.Initializer()!, checkMode);
        if (node.Type() != null)
        {
            var t = GetTypeFromTypeNode(node.Type()!);
            CheckTypeAssignableToAndOptionallyElaborate(initializerType, t, node, node.Initializer(), null /*headMessage*/, null);
            return t;
        }
        return initializerType;
    }

    // checker.go:13621
    internal Type CheckShorthandPropertyAssignment(Node node, bool inDestructuringPattern, CheckMode checkMode)
    {
        Node? expr = null;
        if (!inDestructuringPattern)
        {
            expr = node.AsShorthandPropertyAssignment().ObjectAssignmentInitializer;
        }
        if (expr == null)
        {
            expr = node.Name();
        }
        var expressionType = CheckExpressionForMutableLocation(expr!, checkMode);
        if (node.Type() != null)
        {
            var t = GetTypeFromTypeNode(node.Type()!);
            CheckTypeAssignableToAndOptionallyElaborate(expressionType, t, node, expr, null /*headMessage*/, null);
            return t;
        }
        return expressionType;
    }

    // checker.go:13789
    internal Type CheckObjectLiteralMethod(Node node, CheckMode checkMode)
    {
        // Grammar checking
        CheckGrammarMethod(node);
        // Do not use hasDynamicName here, because that returns false for well known symbols.
        // We want to perform checkComputedPropertyName for all computed properties, including
        // well known symbols.
        if (NodeTests.IsComputedPropertyName(node.Name()!))
        {
            CheckComputedPropertyName(node.Name()!);
        }
        var uninstantiatedType = CheckFunctionExpressionOrObjectLiteralMethod(node, checkMode);
        return InstantiateTypeWithSingleGenericCallSignature(node, uninstantiatedType, checkMode);
    }

    // checkJsxAttribute: real body landed in Checker.Jsx.cs (jsx wave).

    // IsContextSensitiveFunctionOrObjectLiteralMethod is now REAL in Checker.ContextSensitive.cs (checker.go:29253).




    // checker.go:173 (TypeToString) / printer.go typeToStringEx.
    // The faithful implementation routes through the NodeBuilder + Printer (phase-6 type serialization),
    // which is not yet ported. For the primitive/intrinsic/literal cases the NodeBuilder emits a
    // keyword- or literal-type node whose printed text is exactly the intrinsic name (for intrinsics) or
    // the escaped literal (matching Ts.Printer's literal emit). Those cases — the ones reachable from
    // intrinsic-type assignability diagnostics — are handled directly here so the diagnostic can be
    // produced; all other type kinds defer to TypeToStringEx (still the NodeBuilder-backed stub).
    // PORT-NOTE: replace with the real typeToStringEx once the NodeBuilder/Printer type-serialization
    // surface (phase 6) lands.
    // printer.go:177 — typeToString delegates straight to typeToStringEx (no literal fast-paths;
    // the nodebuilder leaf renders string/number/boolean/enum literals faithfully, and enum-literal
    // types must NOT be intercepted here — they carry NumberLiteral/StringLiteral flags but need the
    // EnumLike rendering path to print the enum name instead of the raw value).
    internal string TypeToString(Type type)
    {
        return TypeToStringEx(type, null, TypeFormatFlags.AllowUniqueESSymbolType | TypeFormatFlags.UseAliasDefinedOutsideCurrentScope, null);
    }

    // checker.go:28889
    internal Node? GetConstraintDeclaration(Type t)
    {
        if (t.symbol != null)
        {
            foreach (var d in t.symbol.Declarations ?? [])
            {
                if (NodeTests.IsTypeParameterDeclaration(d))
                {
                    var constraint = d.AsTypeParameterDeclaration().Constraint;
                    if (constraint != null)
                    {
                        return constraint;
                    }
                }
            }
        }
        return null;
    }

    // checker.go:17027
    internal Type? GetInferredTypeParameterConstraint(Type t, bool omitTypeReferences)
    {
        System.Collections.Generic.List<Type>? inferences = null;
        if (t.symbol != null && (t.symbol.Declarations?.Length ?? 0) != 0)
        {
            foreach (var declaration in t.symbol.Declarations!)
            {
                if (declaration.Parent != null && NodeTests.IsInferTypeNode(declaration.Parent))
                {
                    // When an 'infer T' declaration is immediately contained in a type reference node
                    // (such as 'Foo<infer T>'), T's constraint is inferred from the constraint of the
                    // corresponding type parameter in 'Foo'. When multiple 'infer T' declarations are
                    // present, we form an intersection of the inferred constraint types.
                    var child = declaration.Parent;
                    var parent = child.Parent;
                    while (parent != null && NodeTests.IsParenthesizedTypeNode(parent))
                    {
                        child = parent;
                        parent = child.Parent;
                    }
                    if (parent is null)
                    {
                        continue;
                    }
                    if (NodeTests.IsTypeReferenceNode(parent) && !omitTypeReferences)
                    {
                        var typeParameters = GetTypeParametersForTypeReferenceOrImport(parent);
                        if (typeParameters != null)
                        {
                            var index = System.Array.IndexOf(parent.TypeArguments() ?? [], child);
                            if (index >= 0 && index < typeParameters.Length)
                            {
                                var declaredConstraint = GetConstraintOfTypeParameter(typeParameters[index]);
                                if (declaredConstraint != null)
                                {
                                    // Type parameter constraints can reference other type parameters so
                                    // constraints need to be instantiated. If instantiation produces the
                                    // type parameter itself, we discard that inference.
                                    var tps = typeParameters;
                                    var refParent = parent;
                                    var mapper = TypeMapper.NewDeferredTypeMapper(
                                        tps,
                                        CoreUtil.MapIndex(tps, (_, i) => (System.Func<Type>)(() => GetEffectiveTypeArgumentAtIndex(refParent, tps, i)))!);
                                    var constraint = InstantiateType(declaredConstraint, mapper);
                                    if (constraint != t)
                                    {
                                        (inferences ??= new()).Add(constraint);
                                    }
                                }
                            }
                        }
                    }
                    else if ((NodeTests.IsParameterDeclaration(parent) && parent.AsParameterDeclaration().DotDotDotToken != null)
                        || NodeTests.IsRestTypeNode(parent)
                        || (NodeTests.IsNamedTupleMember(parent) && parent.AsNamedTupleMember().DotDotDotToken != null))
                    {
                        (inferences ??= new()).Add(CreateArrayType(unknownType!));
                    }
                    else if (NodeTests.IsTemplateLiteralTypeSpan(parent))
                    {
                        (inferences ??= new()).Add(stringType!);
                    }
                    else if (NodeTests.IsTypeParameterDeclaration(parent) && parent.Parent != null && NodeTests.IsMappedTypeNode(parent.Parent))
                    {
                        (inferences ??= new()).Add(stringNumberSymbolType!);
                    }
                    else if (NodeTests.IsMappedTypeNode(parent) && parent.Type() != null
                        && AstUtilities.SkipParentheses(parent.Type()!) == declaration.Parent
                        && parent.Parent != null && NodeTests.IsConditionalTypeNode(parent.Parent)
                        && parent.Parent.AsConditionalTypeNode().ExtendsType == parent
                        && parent.Parent.AsConditionalTypeNode().CheckType != null && NodeTests.IsMappedTypeNode(parent.Parent.AsConditionalTypeNode().CheckType!)
                        && parent.Parent.AsConditionalTypeNode().CheckType!.Type() != null)
                    {
                        var checkMappedType = parent.Parent.AsConditionalTypeNode().CheckType!;
                        var nodeType = GetTypeFromTypeNode(checkMappedType.Type()!);
                        var checkMappedTypeParameter = checkMappedType.AsMappedTypeNode().TypeParameter!;
                        var mapper = TypeMapper.NewSimpleTypeMapper(
                            GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(checkMappedTypeParameter)!),
                            checkMappedTypeParameter.AsTypeParameterDeclaration().Constraint != null
                                ? GetTypeFromTypeNode(checkMappedTypeParameter.AsTypeParameterDeclaration().Constraint!)
                                : stringNumberSymbolType!);
                        (inferences ??= new()).Add(InstantiateType(nodeType, mapper));
                    }
                }
            }
        }
        if (inferences != null && inferences.Count != 0)
        {
            return GetIntersectionType(inferences.ToArray());
        }
        return null;
    }

    // getEffectiveTypeArguments / isMappedTypeGenericIndexedAccess / getConditionalTypeInstantiation
    // implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // getResolvedBaseConstraint (27313) / getSimplifiedType (27731) / getSimplifiedTypeOrConstraint (27862)
    // implemented in Checker.Constraints.cs (#19); getIndexedAccessTypeOrUndefined (26801) in Checker.IndexedAccess.cs (#18).

    // checker.go:29048
    internal Type SubstituteIndexedMappedType(Type objectType, Type index)
    {
        var mapper = TypeMapper.NewSimpleTypeMapper(GetTypeParameterFromMappedType(objectType), index);
        var templateMapper = CombineTypeMappers(objectType.AsMappedType().mapper, mapper);
        var instantiatedTemplateType = InstantiateType(GetTemplateTypeFromMappedType(objectType.AsMappedType().target ?? objectType), templateMapper);
        var isOptional = GetMappedTypeOptionality(objectType) > 0;
        if (!isOptional)
        {
            if (IsGenericType(objectType))
            {
                isOptional = GetCombinedMappedTypeOptionality(GetModifiersTypeFromMappedType(objectType)) > 0;
            }
            else
            {
                isOptional = CouldAccessOptionalProperty(objectType, index);
            }
        }
        return AddOptionalityEx(instantiatedTemplateType, true /*isProperty*/, isOptional);
    }

    // checker.go:29062 — Return true if an indexed access with the given object and index types could
    // access an optional property.
    internal bool CouldAccessOptionalProperty(Type objectType, Type indexType)
    {
        var indexConstraint = GetBaseConstraintOfType(indexType);
        return indexConstraint != null && global::Ts.Core.CoreUtil.Some(GetPropertiesOfType(objectType), p =>
            (p.Flags & SymbolFlags.Optional) != 0 && IsTypeAssignableTo(GetLiteralTypeFromProperty(p, TypeFlags.StringOrNumberLiteralOrUnique, false), indexConstraint));
    }



    // isErrorType (26509) implemented in Checker.UnionIntersection.cs (#17).

    // getTypeOfSymbolAtLocation (checker.go:16367) — leaf public-ish query, large surface — remaining.
    // checker.go:16367 — GetTypeOfSymbolAtLocation.
    public Type GetTypeOfSymbolAtLocation(Symbol symbol, Node? location)
    {
        symbol = GetExportSymbolOfValueSymbolIfExported(symbol)!;
        if (location != null)
        {
            // If we have an identifier or a property access at the given location, if the location is
            // a dotted name expression, and if the location is not an assignment target, obtain the type
            // of the expression (which will reflect control flow analysis). If the expression indeed
            // resolved to the given symbol, return the narrowed type.
            if ((NodeTests.IsIdentifier(location) || NodeTests.IsPrivateIdentifier(location)) &&
                !(AstUtilities.IsJsxTagName(location) || NodeTests.IsJsxAttribute(location.Parent!) || NodeTests.IsJsxNamespacedName(location.Parent!)))
            {
                if (AstUtilities.IsRightSideOfQualifiedNameOrPropertyAccess(location))
                {
                    location = location.Parent;
                }
                if (AstUtilities.IsExpressionNode(location!) && (!AstUtilities.IsAssignmentTarget(location!) || IsWriteAccess(location!)))
                {
                    Type t;
                    if (IsWriteAccess(location!) && location!.Kind == SyntaxKind.PropertyAccessExpression)
                    {
                        t = CheckPropertyAccessExpression(location, CheckMode.Normal, true /*writeOnly*/);
                    }
                    else
                    {
                        t = GetTypeOfExpression(location!);
                    }
                    if (GetExportSymbolOfValueSymbolIfExported(symbolNodeLinks.Get(location!).resolvedSymbol) == symbol)
                    {
                        return RemoveOptionalTypeMarker(t);
                    }
                }
            }
            if (AstUtilities.IsDeclarationName(location!) && NodeTests.IsSetAccessorDeclaration(location!.Parent!) && GetAnnotatedAccessorTypeNode(location.Parent) != null)
            {
                return GetWriteTypeOfAccessors(location.Parent!.Symbol()!);
            }
            // The location isn't a reference to the given symbol, meaning we're being asked
            // a hypothetical question of what type the symbol would have if there was a reference
            // to it at the given location. Since we have no control flow information for the
            // hypothetical reference (control flow information is created and attached by the
            // binder), we simply return the declared type of the symbol.
            if (IsRightSideOfAccessExpression(location!) && IsWriteAccess(location!.Parent!))
            {
                return GetWriteTypeOfSymbol(symbol);
            }
        }
        return GetNonMissingTypeOfSymbol(symbol);
    }

    // utilities.go:218 — isRightSideOfAccessExpression.
    private static bool IsRightSideOfAccessExpression(Node node) =>
        node.Parent != null && (
            (NodeTests.IsPropertyAccessExpression(node.Parent) && node.Parent.Name() == node) ||
            (NodeTests.IsElementAccessExpression(node.Parent) && node.Parent.AsElementAccessExpression().ArgumentExpression == node));

    // ===================================================================================
    // Free-function helpers (Go package-level). In-range ones real; others minimal stubs.
    // ===================================================================================

    // utilities.go:175-177
    private static bool IsShorthandAmbientModuleSymbol(Symbol moduleSymbol) => AstUtilities.IsShorthandAmbientModule(moduleSymbol.ValueDeclaration);


    // utilities.go:264-266
    private static bool IsTypeAny(Type? t) => t != null && (t.flags & TypeFlags.Any) != 0;

    // utilities.go:276-278
    internal static bool IsOptionalDeclaration(Node declaration) => AstUtilities.HasQuestionToken(declaration);

    // ast/utilities.go WalkUpBindingElementsAndPatterns — implemented by the EmitResolver wave;
    // internal so the EmitResolver class (separate type) can reach it.
    internal static Node WalkUpBindingElementsAndPatterns(Node binding)
    {
        var node = binding.Parent!;
        while (NodeTests.IsBindingElement(node.Parent!))
        {
            node = node.Parent!.Parent!;
        }
        return node.Parent!;
    }

    // ast.IsJsonSourceFile: real body in AstUtilities (Utilities.Binder.cs, utilities.go:1876).
    private static bool IsJsonSourceFile(SourceFile file) => AstUtilities.IsJsonSourceFile(file);

    // ast.GetExtendsHeritageClauseElements (utilities.go:1701) — delegates to the ast utility.
    private static Node[] GetExtendsHeritageClauseElements(Node node) => AstUtilities.GetExtendsHeritageClauseElements(node);

    // append(typeParameters, x) on Type[]? (Go slice append semantics).
    private static Type[] AppendType(Type[]? types, Type extra)
    {
        var list = new List<Type>(types.Len() + 1);
        list.AddRange(types.OrEmpty());
        list.Add(extra);
        return list.ToArray();
    }
}
