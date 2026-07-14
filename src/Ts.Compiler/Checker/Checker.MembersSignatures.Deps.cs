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
    internal static ModifierFlags GetDeclarationModifierFlagsFromSymbol(Symbol s) => GetDeclarationModifierFlagsFromSymbolEx(s, false /*isWrite*/);
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
    internal bool IsKnownSymbol(Symbol symbol) => throw new NotImplementedException("IsKnownSymbol");
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
    internal Type PadObjectLiteralType(Type t, Node pattern) => throw new NotImplementedException("padObjectLiteralType");
    internal Type PadTupleType(Type t, Node pattern) => throw new NotImplementedException("padTupleType");
    // WidenTypeInferredFromInitializer is now REAL in Checker.Widening.cs (checker.go:16795).
    // GetWidenedLiteralTypeForInitializer is now REAL in Checker.Widening.cs (checker.go:16810).
    // GetWidenedLiteralLikeTypeForContextualType is now REAL in Checker.Widening.cs (checker.go:25423).
    internal Type GetNonUndefinedType(Type t) => throw new NotImplementedException("getNonUndefinedType");

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
    // checker.go:30574 — deep return-mapper instantiation; deferred. PORT-NOTE: signature made nullable to
    // match Go (nil in → nil out) so contextual-type callers pass through cleanly.
    internal Type? InstantiateContextualType(Type? contextualType, Node node, ContextFlags contextFlags) => contextualType;

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
