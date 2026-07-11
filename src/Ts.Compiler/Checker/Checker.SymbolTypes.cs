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
    internal Type GetTypeOfSymbol(Symbol symbol)
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
    internal Type GetBaseConstructorTypeOfClass(Type t)
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
    internal Type? GetConstraintOfTypeParameter(Type typeParameter)
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
    internal Type GetTypeOfMappedSymbol(Symbol symbol) => throw new NotImplementedException("getTypeOfMappedSymbol");

    internal Type GetTypeOfReverseMappedSymbol(Symbol symbol) => throw new NotImplementedException("getTypeOfReverseMappedSymbol");







    internal Type RemoveMissingType(Type type, bool isOptional) => throw new NotImplementedException("removeMissingType");

    // The variable-typing deep worker + widening helpers (#11 tail, very large surface) — remaining.
    internal Type GetTypeForVariableLikeDeclaration(Node declaration, bool includeOptionality, CheckMode checkMode) => throw new NotImplementedException("getTypeForVariableLikeDeclaration");




    internal Type GetWidenedLiteralType(Type type) => throw new NotImplementedException("getWidenedLiteralType");

    internal Type CheckPropertyAssignment(Node node, CheckMode checkMode) => throw new NotImplementedException("checkPropertyAssignment");

    internal Type CheckShorthandPropertyAssignment(Node node, bool inDestructuringPattern, CheckMode checkMode) => throw new NotImplementedException("checkShorthandPropertyAssignment");

    internal Type CheckObjectLiteralMethod(Node node, CheckMode checkMode) => throw new NotImplementedException("checkObjectLiteralMethod");

    internal Type CheckJsxAttribute(Node node, CheckMode checkMode) => throw new NotImplementedException("checkJsxAttribute");

    internal bool IsContextSensitiveFunctionOrObjectLiteralMethod(Node node) => throw new NotImplementedException("isContextSensitiveFunctionOrObjectLiteralMethod");




    internal string TypeToString(Type type) => throw new NotImplementedException("TypeToString");

    internal Node? GetConstraintDeclaration(Type t) => throw new NotImplementedException("getConstraintDeclaration");

    internal Type? GetInferredTypeParameterConstraint(Type t, bool omitTypeReferences) => throw new NotImplementedException("getInferredTypeParameterConstraint");

    internal Type[] GetEffectiveTypeArguments(Node node, Type[] typeParameters) => throw new NotImplementedException("getEffectiveTypeArguments");

    internal Type GetResolvedBaseConstraint(Type t, Node? stack) => throw new NotImplementedException("getResolvedBaseConstraint");

    internal Type? GetSimplifiedTypeOrConstraint(Type type) => throw new NotImplementedException("getSimplifiedTypeOrConstraint");

    internal Type? GetIndexedAccessTypeOrUndefined(Type objectType, Type indexType, AccessFlags accessFlags, Node? accessNode, Symbol? aliasSymbol) => throw new NotImplementedException("getIndexedAccessTypeOrUndefined");

    internal bool IsMappedTypeGenericIndexedAccess(Type t) => throw new NotImplementedException("isMappedTypeGenericIndexedAccess");

    internal Type SubstituteIndexedMappedType(Type objectType, Type index) => throw new NotImplementedException("substituteIndexedMappedType");

    internal Type GetSimplifiedType(Type type, bool writing) => throw new NotImplementedException("getSimplifiedType");

    internal Type GetConditionalTypeInstantiation(Type t, TypeMapper? mapper, bool forConstraint, TypeAlias? alias) => throw new NotImplementedException("getConditionalTypeInstantiation");






    internal bool IsErrorType(Type type) => throw new NotImplementedException("isErrorType");

    // getTypeOfSymbolAtLocation (checker.go:16367) — leaf public-ish query, large surface — remaining.
    internal Type GetTypeOfSymbolAtLocation(Symbol symbol, Node? location) => throw new NotImplementedException("getTypeOfSymbolAtLocation");

    // ===================================================================================
    // Free-function helpers (Go package-level). In-range ones real; others minimal stubs.
    // ===================================================================================

    private static bool IsShorthandAmbientModuleSymbol(Symbol moduleSymbol) => throw new NotImplementedException("isShorthandAmbientModuleSymbol");


    private static bool IsTypeAny(Type? t) => throw new NotImplementedException("IsTypeAny");

    private static bool IsOptionalDeclaration(Node declaration) => throw new NotImplementedException("isOptionalDeclaration");

    // Missing ast helpers (absent from Ts.Ast) — minimal local stubs (report to add to ast).
    private static Node WalkUpBindingElementsAndPatterns(Node binding) => throw new NotImplementedException("ast.WalkUpBindingElementsAndPatterns");

    private static bool IsJsonSourceFile(SourceFile file) => throw new NotImplementedException("ast.IsJsonSourceFile");

    private static Node[] GetExtendsHeritageClauseElements(Node node) => throw new NotImplementedException("ast.GetExtendsHeritageClauseElements");

    // append(typeParameters, x) on Type[]? (Go slice append semantics).
    private static Type[] AppendType(Type[]? types, Type extra)
    {
        var list = new List<Type>(types.Len() + 1);
        list.AddRange(types.OrEmpty());
        list.Add(extra);
        return list.ToArray();
    }
}
