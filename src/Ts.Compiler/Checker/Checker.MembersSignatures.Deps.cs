// PLACEHOLDER STUBS for the Members + Signatures cluster (plan/03 §4.2 #12/#13).
//
// Every member below is a cross-cluster helper that #12/#13 CALL but do not own (they live in #7/#8
// expressions, #10 symbol resolution, #11 symbol types, #14 instantiation, #16 type creation, #17
// union/intersection, #18 indexed access, #19 constraints, #21 contextual, #22 awaited, flow, etc.).
// They had no C# definition at build time, so they are stubbed here (throw) purely to compile the
// cluster. **When the owning cluster lands a real definition, DELETE the corresponding stub here** —
// this file is the single dedup surface for the Members/Signatures wave. Signatures are chosen to
// match the #12/#13 call sites (nullable-tolerant); the owning cluster's real signature is authoritative.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // ---- expressions (#7/#8) ----
    // checkExpressionCachedEx (7494) / checkExpressionEx (7534): real bodies landed in
    // Checker.Expressions.cs (#7 wave) — stubs removed from here.
    // checker.go:13077-13092
    internal Type CheckExpressionForMutableLocation(Node node, CheckMode checkMode)
    {
        var t = CheckExpressionEx(node, checkMode);
        if (IsConstContext(node))
        {
            return GetRegularTypeOfLiteralType(t);
        }
        if (IsTypeAssertion(node))
        {
            return t;
        }
        return GetWidenedLiteralLikeTypeForContextualType(t, InstantiateContextualType(GetContextualType(node, ContextFlags.None), node, ContextFlags.None));
    }
    // checker.go:29130 — checkNonNullType (checker.go:29134) still stubbed (Checker.Flow.Deps.cs).
    internal Type CheckNonNullExpression(Node node) => CheckNonNullType(CheckExpression(node), node);
    // checkIteratedTypeOrElementType (6074): real body landed in Checker.IterationTypes.cs (iteration wave).
    // checker.go:16710
    internal Type CheckDeclarationInitializer(Node declaration, CheckMode checkMode, Type? contextualType)
    {
        var initializer = declaration.Initializer()!;
        var t = GetQuickTypeOfExpression(initializer);
        if (t == null)
        {
            if (contextualType != null)
            {
                t = CheckExpressionWithContextualType(initializer, contextualType, null /*inferenceContext*/, checkMode);
            }
            else
            {
                t = CheckExpressionCachedEx(initializer, checkMode);
            }
        }
        if (NodeTests.IsParameterDeclaration(AstUtilities.GetRootDeclaration(declaration)))
        {
            var name = declaration.Name()!;
            switch (name.Kind)
            {
                case SyntaxKind.ObjectBindingPattern:
                    if (IsObjectLiteralType(t))
                    {
                        return PadObjectLiteralType(t, name);
                    }
                    break;
                case SyntaxKind.ArrayBindingPattern:
                    if (IsTupleType(t))
                    {
                        return PadTupleType(t, name);
                    }
                    break;
            }
        }
        return t;
    }
    // isConstContext / isValidConstAssertionArgument (checker.go) implemented in Checker.Inference.cs.
    // isConstantReference: owned by flow.go — real body in Checker.Flow.cs.
    // GetTypeOfExpression is now REAL in Checker.QuickType.cs (checker.go:7314).
    // GetYieldedTypeOfYieldExpression is now REAL in Checker.Expressions.cs (checker.go:10993).

    // ---- symbol resolution / symbol types (#10/#11) ----
    // checker.go:14292
    internal Symbol? GetParentOfSymbol(Symbol symbol)
    {
        if (symbol.Parent != null)
        {
            return GetMergedSymbol(GetLateBoundSymbol(symbol.Parent));
        }
        return null;
    }
    // getSymbolOfNode 14326: real body landed in Checker.SymbolResolution.cs.
    // symbolIsValueEx / createSymbolWithType implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // isNamedMember (checker.go:21992) implemented in Checker.TypeCreation.cs alongside getNamedMembers.
    // checker.go:13517-13534
    internal Symbol GetSpreadSymbol(Symbol prop, bool isReadonly)
    {
        var isSetonlyAccessor = (prop.Flags & SymbolFlags.SetAccessor) != 0 && (prop.Flags & SymbolFlags.GetAccessor) == 0;
        if (!isSetonlyAccessor && isReadonly == IsReadonlySymbol(prop))
        {
            return prop;
        }
        var flags = SymbolFlags.Property | (prop.Flags & SymbolFlags.Optional);
        var result = NewSymbolEx(flags, prop.Name, (prop.CheckFlags & CheckFlags.Late) | CoreUtil.IfElse(isReadonly, CheckFlags.Readonly, CheckFlags.None));
        var links = valueSymbolLinks.Get(result);
        if (isSetonlyAccessor)
        {
            links.resolvedType = undefinedType!;
        }
        else
        {
            links.resolvedType = GetTypeOfSymbol(prop);
        }
        result.Declarations = prop.Declarations;
        links.nameType = valueSymbolLinks.Get(prop).nameType;
        mappedSymbolLinks.Get(result).syntheticOrigin = prop;
        return result;
    }
    // utilities.go:690 — delegates to the Ex form landed in Checker.PropertyAccess.cs.
    public static ModifierFlags GetDeclarationModifierFlagsFromSymbol(Symbol s) => GetDeclarationModifierFlagsFromSymbolEx(s, false /*isWrite*/);
    // getFlowTypeInConstructor: owned by flow.go — real body in Checker.Flow.cs.
    // getTypeOfPropertyInBaseClass (11423) implemented in Checker.Flow.Deps.cs (wave O).
    // getTypeOfInitializer: owned by flow.go — real body in Checker.Flow.cs.
    // getDeclaringConstructor 27211: real body landed in Checker.PropertyAccess.cs.
    // We approximate own properties as non-methods plus methods that are inside the object literal
    // checker.go:13512-13515
    internal bool IsSpreadableProperty(Symbol prop) =>
        (!CoreUtil.Some(prop.Declarations, AstUtilities.IsPrivateIdentifierClassElementDeclaration) && (prop.Flags & (SymbolFlags.Method | SymbolFlags.GetAccessor | SymbolFlags.SetAccessor)) == 0) ||
        !CoreUtil.Some(prop.Declarations, d => d.Parent is not null && AstUtilities.IsClassLike(d.Parent));
    // isSymbolAssigned: owned by flow.go — real body in Checker.Flow.cs.
    // utilities.go:952
    internal bool IsKnownSymbol(Symbol symbol) => IsLateBoundName(symbol.Name);
    // getLiteralTypeFromProperty (26617) / getLiteralTypeFromPropertyName (26640) implemented in Checker.IndexedAccess.cs (#18).
    // getPropertyOfObjectType implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // getPropertyOfUnionOrIntersectionType (21319) / getUnionOrIntersectionProperty (21333) /
    // createUnionOrIntersectionProperty (21357): real bodies landed in Checker.Instantiation.cs.

    // ---- instantiation (#14) ----
    // getReducedApparentType / instantiateSignatures / instantiateSymbols / instantiateIndexInfos /
    // instantiateTypes / cloneTypeParameter / fillMissingTypeArguments / getMinTypeArgumentCount
    // implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // getBaseConstraintOrType (27294) implemented in Checker.IndexedAccess.cs (#18).
    // newInferenceContext / applyToParameterTypes / applyToReturnTypes / inferTypes / getInferredTypes
    // implemented in Checker.Inference.cs (inference wave). getEffectiveRestType owned by relater.

    // ---- type creation (#16) ----
    // checker.go:25011-25013
    internal Type CreateTypeReference(Type target, Type[]? typeArguments) =>
        CreateTypeReferenceEx(target, typeArguments, ObjectFlags.None);
    // checker.go — cloneTypeReference(source)
    internal Type CloneTypeReference(Type source)
    {
        var t = NewObjectType(ObjectFlags.Reference, source.symbol);
        t.objectFlags = source.objectFlags & ~ObjectFlags.MembersResolved;
        t.AsTypeReference()!.target = source.AsTypeReference()!.target;
        t.AsTypeReference()!.resolvedTypeArguments = source.AsTypeReference()!.resolvedTypeArguments;
        return t;
    }

    // ---- union / intersection (#17) ----
    // getUnionTypeEx / getUnionOrIntersectionType / filterType / isEmptyObjectType / isPatternLiteralType
    // (25536/25518/26447/26374/26405) implemented in Checker.UnionIntersection.cs (#17).

    // ---- indexed access / mapped (#18) ----
    // getIndexedAccessType (26789) implemented in Checker.IndexedAccess.cs (#18).
    // sliceTupleType owned by the relater wave (Checker.Relater.cs).
    // resolveMappedTypeMembers (20799) implemented in Checker.Instantiation.cs (#14).
    // resolveReverseMappedTypeMembers (inference.go:1065) implemented in Checker.Inference.cs.
    // resolveUnionTypeMembers (20957) / resolveIntersectionTypeMembers (21207) implemented in
    // Checker.Instantiation.cs (#14).

    // ---- widening / initializer inference ----
    // checkDeclarationInitializer (checker.go:16710) landed in this file; its parameter-binding-pattern
    // branch reaches padObjectLiteralType (checker.go:16736) / padTupleType (checker.go:16776), stubbed
    // here (owning wave: getPropertyNameFromBindingElement / getTypeFromBindingElement / newAnonymousType
    // shapes). Only reachable when a parameter's binding pattern has defaulted elements.
    // checker.go:16736
    internal Type PadObjectLiteralType(Type t, Node pattern)
    {
        List<Node>? missingElements = null;
        foreach (var e in pattern.Elements() ?? [])
        {
            if (e.Initializer() != null)
            {
                var name = GetPropertyNameFromBindingElement(e);
                if (name != InternalSymbolName.Missing && GetPropertyOfType(t, name) == null)
                {
                    (missingElements ??= new()).Add(e);
                }
            }
        }
        if (missingElements == null || missingElements.Count == 0)
        {
            return t;
        }
        var members = new SymbolTable();
        foreach (var prop in GetPropertiesOfObjectType(t))
        {
            members[prop.Name] = prop;
        }
        foreach (var e in missingElements)
        {
            var symbol = NewSymbol(SymbolFlags.Property | SymbolFlags.Optional, GetPropertyNameFromBindingElement(e));
            valueSymbolLinks.Get(symbol).resolvedType = GetTypeFromBindingElement(e, false /*includePatternInType*/, false /*reportErrors*/);
            members[symbol.Name] = symbol;
        }
        var result = NewAnonymousType(t.symbol, members, null, null, GetIndexInfosOfType(t));
        result.objectFlags = t.objectFlags;
        return result;
    }

    // checker.go:16763
    internal string GetPropertyNameFromBindingElement(Node e)
    {
        var exprType = GetLiteralTypeFromPropertyName(e.PropertyNameOrName()!);
        if (IsTypeUsableAsPropertyName(exprType))
        {
            return GetPropertyNameFromType(exprType);
        }
        return InternalSymbolName.Missing;
    }
    // checker.go:16771
    internal Type PadTupleType(Type t, Node pattern)
    {
        var patternElements = pattern.Elements()!;
        if ((t.TargetTupleType()!.combinedFlags & ElementFlags.Variable) != 0 || GetTypeReferenceArity(t) >= patternElements.Length)
        {
            return t;
        }
        var elementTypes = new global::System.Collections.Generic.List<Type>(GetElementTypes(t));
        var elementInfos = new global::System.Collections.Generic.List<TupleElementInfo>(t.TargetTupleType()!.elementInfos!);
        for (var i = GetTypeReferenceArity(t); i < patternElements.Length; i++)
        {
            var e = patternElements[i];
            if (i < patternElements.Length - 1 || !(NodeTests.IsBindingElement(e) && HasDotDotDotToken(e)))
            {
                var elementType = anyType!;
                if (!NodeTests.IsOmittedExpression(e) && HasDefaultValue(e))
                {
                    elementType = GetTypeFromBindingElement(e, false /*includePatternInType*/, false /*reportErrors*/);
                }
                elementTypes.Add(elementType);
                elementInfos.Add(new TupleElementInfo(ElementFlags.Optional, null));
                if (!NodeTests.IsOmittedExpression(e) && !HasDefaultValue(e))
                {
                    ReportImplicitAny(e, anyType!, WideningKind.Normal);
                }
            }
        }
        return CreateTupleTypeEx(elementTypes.ToArray(), elementInfos.ToArray(), t.TargetTupleType()!.readonlyElements);
    }
    // WidenTypeInferredFromInitializer is now REAL in Checker.Widening.cs (checker.go:16795).
    // GetWidenedLiteralTypeForInitializer is now REAL in Checker.Widening.cs (checker.go:16810).
    // GetWidenedLiteralLikeTypeForContextualType is now REAL in Checker.Widening.cs (checker.go:25423).
    // checker.go:31305
    internal Type GetNonUndefinedType(Type t)
    {
        var typeOrConstraint = t;
        if (SomeType(t, IsGenericTypeWithUndefinedConstraint))
        {
            typeOrConstraint = MapType(t, sub =>
                (sub.flags & TypeFlags.Instantiable) != 0 ? GetBaseConstraintOrType(sub) : sub);
        }
        return GetTypeWithFacts(typeOrConstraint, TypeFacts.NEUndefined);
    }

    // checker.go: isGenericTypeWithUndefinedConstraint
    private bool IsGenericTypeWithUndefinedConstraint(Type t)
    {
        if ((t.flags & TypeFlags.Instantiable) != 0)
        {
            var constraint = GetBaseConstraintOfType(t);
            if (constraint != null)
            {
                return MaybeTypeOfKind(constraint, TypeFlags.Undefined);
            }
        }
        return false;
    }

    // ---- facts / assignability (flow, relater) ----
    // hasTypeFacts 30735: real body landed in Checker.TypeFacts.cs.
    // getTypeWithFacts 30907 / getAdjustedTypeWithFacts 30916: real bodies landed in Checker.TypeFacts.cs.
    // isTypeAssignableTo owned by the relater wave (Checker.Relater.cs).
    // isTypeAssignableToKind (27507) implemented in Checker.Constraints.cs (#19).
    // checker.go:13436
    internal bool IsValidSpreadType(Type t)
    {
        var s = RemoveDefinitelyFalsyTypes(MapType(t, GetBaseConstraintOrType));
        return (s.flags & (TypeFlags.Any | TypeFlags.NonPrimitive | TypeFlags.Object | TypeFlags.InstantiableNonPrimitive)) != 0 ||
            ((s.flags & TypeFlags.UnionOrIntersection) != 0 && CoreUtil.Every(s.Types(), IsValidSpreadType));
    }
    // isMatchingReference: owned by flow.go — real body in Checker.Flow.cs.
    // checker.go:13540-13545
    internal bool HasDefaultValue(Node node) =>
        NodeTests.IsBindingElement(node) && node.Initializer() != null ||
        NodeTests.IsPropertyAssignment(node) && HasDefaultValue(node.Initializer()!) ||
        NodeTests.IsShorthandPropertyAssignment(node) && node.AsShorthandPropertyAssignment().ObjectAssignmentInitializer != null ||
        NodeTests.IsBinaryExpression(node) && node.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.EqualsToken;

    // ---- flow ----
    // getFlowNodeOfNode / getFlowTypeOfReference / getFlowTypeOfReferenceEx / isReachableFlowNode /
    // getDestructuringPropertyName: owned by flow.go — real bodies in Checker.Flow.cs.

    // ---- contextual (#21) ----
    // getContextualReturnType / getContextualSignatureForFunctionLikeDeclaration: real bodies landed in
    // Checker.ContextualType.cs (#21 wave).
    // checker.go:30574 — instantiateContextualType.
    internal Type? InstantiateContextualType(Type? contextualType, Node node, ContextFlags contextFlags)
    {
        if (contextualType != null && MaybeTypeOfKind(contextualType, TypeFlags.Instantiable))
        {
            var inferenceContext = GetInferenceContext(node);
            // If no inferences have been made, and none of the type parameters for which we are inferring
            // specify default types, nothing is gained from instantiating as type parameters would just be
            // replaced with their constraints similar to the apparent type.
            if (inferenceContext != null)
            {
                if ((contextFlags & ContextFlags.Signature) != 0 && CoreUtil.Some(inferenceContext.inferences, HasInferenceCandidatesOrDefault))
                {
                    // For contextual signatures we incorporate all inferences made so far, e.g. from return
                    // types as well as arguments to the left in a function call.
                    var t = InstantiateInstantiableTypes(contextualType, inferenceContext.nonFixingMapper);
                    if ((t.flags & TypeFlags.AnyOrUnknown) == 0)
                    {
                        return t;
                    }
                }
                if (inferenceContext.returnMapper != null)
                {
                    // For other purposes (e.g. determining whether to produce literal types) we only
                    // incorporate inferences made from the return type in a function call. We remove
                    // the 'boolean' type from the contextual type such that contextually typed boolean
                    // literals actually end up widening to 'boolean' (see #48363).
                    var t = InstantiateInstantiableTypes(contextualType, inferenceContext.returnMapper);
                    if ((t.flags & TypeFlags.AnyOrUnknown) == 0)
                    {
                        if ((t.flags & TypeFlags.Union) != 0 && ContainsType(t.Types(), regularFalseType!) && ContainsType(t.Types(), regularTrueType!))
                        {
                            return FilterType(t, tt => tt != regularFalseType && tt != regularTrueType);
                        }
                        return t;
                    }
                }
            }
        }
        return contextualType;
    }

    // checker.go — instantiateInstantiableTypes.
    internal Type InstantiateInstantiableTypes(Type t, TypeMapper? mapper)
    {
        if ((t.flags & TypeFlags.Instantiable) != 0)
        {
            return InstantiateType(t, mapper);
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return GetUnionTypeEx(CoreUtil.Map(t.Types(), tt => InstantiateInstantiableTypes(tt, mapper)), UnionReduction.None, null, null);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return GetIntersectionType(CoreUtil.Map(t.Types(), tt => InstantiateInstantiableTypes(tt, mapper)));
        }
        return t;
    }

    // ---- awaited / iteration (#22) ----
    // unwrapAwaitedType / checkAwaitedType / getAwaitedTypeNoAlias / getPromisedTypeOfPromise: real
    // bodies landed in Checker.AwaitedTypes.cs (#22 awaited-types wave).
    // checker.go:29413
    internal Type? GetContextualIterationType(IterationTypeKind kind, Node functionDecl)
    {
        var isAsync = (AstUtilities.GetFunctionFlags(functionDecl) & FunctionFlags.Async) != 0;
        var contextualReturnType = GetContextualReturnType(functionDecl, ContextFlags.None);
        if (contextualReturnType != null)
        {
            return GetIterationTypeOfGeneratorFunctionReturnType(kind, contextualReturnType, isAsync);
        }
        return null;
    }
    // getIterationTypeOfGeneratorFunctionReturnType (6195) / getIterationTypesOfIterable (6244):
    // real bodies landed in Checker.IterationTypes.cs (iteration wave).
    // isResolvingReturnTypeOfSignature owned by the relater wave (Checker.Relater.cs).

    // ---- signatures / parameters (helpers in adjacent waves) ----
    // getTypeOfFirstParameterOfSignature: real body landed in Checker.AwaitedTypes.cs (#22).
    // getTypeAtPosition / getRestTypeAtPosition / newTypePredicate owned by the relater wave.
    // GetDefaultConstructSignatures is now REAL in Checker.Signatures.cs (checker.go:20762).

    // ---- diagnostics helpers ----
    // checker.go:13937
    internal void ErrorOrSuggestion(bool isError, Node? location, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        AddErrorOrSuggestion(isError, NewDiagnosticForNode(location, message, args));
    }
    // TypeToStringEx: real implementation in Checker.TypeToString.cs (phase-5 type-display wave).
    // elaborateNeverIntersection implemented by the #14 Instantiation wave (Checker.Instantiation.cs).
    // utilities.go:32-37 — PORT-NOTE: Go's checker-package free function NewDiagnosticChainForNode is
    // hosted on Checker (same as NewDiagnosticForNode); it never returns nil, so non-nullable return.
    internal Diagnostic NewDiagnosticChainForNode(Diagnostic? chain, Node? node, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        if (chain != null)
        {
            return Diagnostic.NewDiagnosticChain(chain, message, args);
        }
        return NewDiagnosticForNode(node, message, args);
    }

    // ---- ast helpers with no C# home yet ----
    // GetClassLikeDeclarationOfSymbol is now REAL in Ast/Utilities.cs (AstUtilities, utilities.go:2926).
    // GetExtendsHeritageClauseElement is now REAL in Ast/Utilities.cs (AstUtilities, utilities.go:1697).
    // GetThisParameter is now REAL in Ts.Ast (AstUtilities, Utilities.Decorators.cs); call sites use
    // AstUtilities.GetThisParameter directly.
    // utilities.go:1837
    internal static Node? GetSetAccessorValueParameter(Node accessor)
    {
        var parameters = accessor.Parameters();
        if ((parameters?.Length ?? 0) > 0)
        {
            var hasThis = parameters!.Length == 2 && AstUtilities.IsThisParameter(parameters[0]);
            return parameters[hasThis ? 1 : 0];
        }
        return null;
    }
    // ast/utilities.go:3141-3161
    internal static string GetPropertyNameForPropertyNameNode(Node name)
    {
        switch (name.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.JsxNamespacedName:
                return name.Text();
            case SyntaxKind.ComputedPropertyName:
                var nameExpression = name.Expression()!;
                if (AstUtilities.IsStringOrNumericLiteralLike(nameExpression))
                {
                    return nameExpression.Text();
                }
                if (AstUtilities.IsSignedNumericLiteral(nameExpression))
                {
                    var text = nameExpression.AsPrefixUnaryExpression().Operand!.Text();
                    if (nameExpression.AsPrefixUnaryExpression().Operator == SyntaxKind.MinusToken)
                    {
                        text = "-" + text;
                    }
                    return text;
                }
                return InternalSymbolName.Missing;
        }
        throw new global::System.InvalidOperationException("Unhandled case in getPropertyNameForPropertyNameNode");
    }
    // ast/utilities.go:2074 — delegates to the ast utility now that it has landed.
    internal static bool IsImportCall(Node node) => AstUtilities.IsImportCall(node);
    // utilities.go:963 — isLateBoundName. PORT-NOTE: '\xfe' is the InternalSymbolName sentinel (U+00FE in
    // UTF-16); late-bound names are `\xfe@...`.
    internal static bool IsLateBoundName(string name) => name.Length >= 2 && name[0] == '\xfe' && name[1] == '@';
    // ast.IsCheckJSEnabledForFile: real body in AstUtilities (Utilities.Binder.cs, utilities.go:2863).
    internal bool IsCheckJSEnabledForFile(SourceFile sourceFile, CompilerOptions options) => AstUtilities.IsCheckJSEnabledForFile(sourceFile, options);
    // ForEachReturnStatement is now REAL in Ast/Utilities.cs (AstUtilities, utilities.go:1138).
    // ForEachYieldExpression is now REAL in Checker.ContextSensitive.cs (internal/checker/utilities.go:1197).
}
