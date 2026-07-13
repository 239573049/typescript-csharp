// Ported from tsgo internal/checker/checker.go — call-resolution applicability, inference, overload-
// failure recovery, and diagnostics. Companion to Checker.CallResolution.cs (the trunk).
//
// Functions: isSignatureApplicable (9232), maybeAddMissingAwaitInfo (9307), getThisArgumentOfCall
// (9321), getThisArgumentType (9343), getEffectiveCheckNode (9357), inferTypeArguments (9366),
// getCandidateForOverloadFailure (9474) + pickLongestCandidateSignature/getLongestCandidateIndex/
// getTypeArgumentsFromNodes/inferSignatureInstantiationForOverloadFailure/createUnionOfSignatures-
// ForOverloadFailure and the tryGetRestTypeOfSignature helpers, reportCallResolutionErrors (9625) +
// addImplementationSuccessElaboration/getArgumentArityError/getTypeArgumentArityError, resolveUntyped-
// Call (9878) / resolveErrorCall (9899), isUntypedFunctionCall (9909), invocationError family (9916),
// and the argument materialization getEffectiveCallArguments (29799) / getSpreadArgumentType (29257) /
// getSpreadArgumentIndex (29865) / isSpreadArgument (29869) / createSyntheticExpression (29873).
//
// §1.5: cross-package via global::; host classes direct. PORT-NOTE: deep leaves still stubbed and CALLED:
// checkExpressionWithContextualType, getInferenceContext, getRegularTypeOfObjectLiteral,
// getAwaitedType/getAwaitedTypeOfPromise, isConstTypeVariable, getWidenedLiteralType, mapType.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:9232
    private bool IsSignatureApplicable(Node node, Node[] args, Signature signature, Relation relation, CheckMode checkMode, bool reportErrors, List<Diagnostic>? diagnosticOutput)
    {
        if (IsJsxCallLike(node))
        {
            return CheckApplicableSignatureForJsxCallLikeElement(node, signature, relation, checkMode, reportErrors, diagnosticOutput);
        }
        var thisType = GetThisTypeOfSignature(signature);
        if (thisType != null && thisType != voidType && !(NodeTests.IsNewExpression(node) || NodeTests.IsCallExpression(node) && IsSuperProperty(node.Expression()!)))
        {
            // If the called expression is not of the form `x.f` or `x["f"]`, then sourceType = voidType. If
            // the signature's 'this' type is voidType, the check is skipped. For new/super calls it's skipped.
            var thisArgumentNode = GetThisArgumentOfCall(node);
            var thisArgumentType = GetThisArgumentType(thisArgumentNode);
            Node? errorNode = null;
            if (reportErrors)
            {
                errorNode = thisArgumentNode ?? node;
            }
            var thisHeadMessage = Diag.The_this_context_of_type_0_is_not_assignable_to_method_s_this_of_type_1;
            if (!CheckTypeRelatedToEx(thisArgumentType, thisType, relation, errorNode, thisHeadMessage, diagnosticOutput))
            {
                return false;
            }
        }
        var headMessage = Diag.Argument_of_type_0_is_not_assignable_to_parameter_of_type_1;
        var restType = GetNonArrayRestType(signature);
        int argCount;
        if (restType != null)
        {
            argCount = global::System.Math.Min(GetParameterCount(signature) - 1, args.Length);
        }
        else
        {
            argCount = args.Length;
        }
        for (var i = 0; i < argCount; i++)
        {
            var arg = args[i];
            if (!NodeTests.IsOmittedExpression(arg))
            {
                var paramType = GetTypeAtPosition(signature, i);
                var argType = CheckExpressionWithContextualType(arg, paramType, null /*inferenceContext*/, checkMode);
                // If one or more arguments are still excluded (CheckMode.SkipContextSensitive), obtain the
                // regular type of any object literal arguments to avoid false excess-property positives.
                Type checkArgType;
                if ((checkMode & CheckMode.SkipContextSensitive) != 0)
                {
                    checkArgType = GetRegularTypeOfObjectLiteral(argType);
                }
                else
                {
                    checkArgType = argType;
                }
                var effectiveCheckArgumentNode = GetEffectiveCheckNode(arg);
                if (!CheckTypeRelatedToAndOptionallyElaborate(checkArgType, paramType, relation, reportErrors ? effectiveCheckArgumentNode : null, effectiveCheckArgumentNode, headMessage, diagnosticOutput))
                {
                    MaybeAddMissingAwaitInfo(arg, checkArgType, paramType, relation, reportErrors, diagnosticOutput);
                    return false;
                }
            }
        }
        if (restType != null)
        {
            var spreadType = GetSpreadArgumentType(args, argCount, args.Length, restType, null /*context*/, checkMode);
            var restArgCount = args.Length - argCount;
            Node? errorNode = null;
            if (reportErrors)
            {
                switch (restArgCount)
                {
                    case 0:
                        errorNode = node;
                        break;
                    case 1:
                        errorNode = GetEffectiveCheckNode(args[argCount]);
                        break;
                    default:
                        errorNode = CreateSyntheticExpression(node, spreadType, false, null);
                        errorNode.Loc = new TextRange(args[argCount].Pos, args[args.Length - 1].End);
                        break;
                }
            }
            if (!CheckTypeRelatedToEx(spreadType, restType, relation, errorNode, headMessage, diagnosticOutput))
            {
                MaybeAddMissingAwaitInfo(errorNode, spreadType, restType, relation, reportErrors, diagnosticOutput);
                return false;
            }
        }
        return true;
    }

    // checker.go:9307
    private void MaybeAddMissingAwaitInfo(Node? errorNode, Type source, Type target, Relation relation, bool reportErrors, List<Diagnostic>? diagnosticOutput)
    {
        if (errorNode != null && reportErrors && diagnosticOutput != null && diagnosticOutput.Count != 0)
        {
            // Bail if target is Promise-like---something else is wrong
            if (GetAwaitedTypeOfPromise(target) != null)
            {
                return;
            }
            var awaitedTypeOfSource = GetAwaitedTypeOfPromise(source);
            if (awaitedTypeOfSource != null && IsTypeRelatedTo(awaitedTypeOfSource, target, relation))
            {
                diagnosticOutput[0].AddRelatedInfo(NewDiagnosticForNode(errorNode, Diag.Did_you_forget_to_use_await));
            }
        }
    }

    // checker.go:9321 — Returns the `this` argument node in calls like `x.f(...)` and `x[f](...)`. null otherwise.
    private Node? GetThisArgumentOfCall(Node node)
    {
        if (NodeTests.IsBinaryExpression(node))
        {
            return node.AsBinaryExpression().Right;
        }
        Node? expression = null;
        if (NodeTests.IsCallExpression(node))
        {
            expression = node.Expression();
        }
        else if (NodeTests.IsTaggedTemplateExpression(node))
        {
            expression = node.AsTaggedTemplateExpression().Tag;
        }
        else if (NodeTests.IsDecorator(node) && !legacyDecorators)
        {
            expression = node.Expression();
        }
        if (expression != null)
        {
            var callee = AstUtilities.SkipOuterExpressions(expression, OuterExpressionKinds.All);
            if (AstUtilities.IsAccessExpression(callee))
            {
                return callee.Expression();
            }
        }
        return null;
    }

    // checker.go:9343
    private Type GetThisArgumentType(Node? node)
    {
        if (node == null)
        {
            return voidType!;
        }
        var thisArgumentType = CheckExpression(node);
        if (AstUtilities.IsOptionalChainRoot(node.Parent!))
        {
            return GetNonNullableType(thisArgumentType);
        }
        if (AstUtilities.IsOptionalChain(node.Parent!))
        {
            return RemoveOptionalTypeMarker(thisArgumentType);
        }
        return thisArgumentType;
    }

    // checker.go:9357
    private Node GetEffectiveCheckNode(Node argument)
    {
        var flags = AstUtilities.IsInJSFile(argument)
            ? OuterExpressionKinds.Parentheses | OuterExpressionKinds.Satisfies | OuterExpressionKinds.ExcludeJSDocTypeAssertion
            : OuterExpressionKinds.Parentheses | OuterExpressionKinds.Satisfies;
        return AstUtilities.SkipOuterExpressions(argument, flags);
    }

    // checker.go:9366
    private Type[] InferTypeArguments(Node node, Signature signature, Node[] args, CheckMode checkMode, InferenceContext context)
    {
        if (IsJsxOpeningLikeElement(node))
        {
            return InferJsxTypeArguments(node, signature, checkMode, context);
        }
        // If a contextual type is available, infer from that type to the return type of the call expression.
        if (!NodeTests.IsDecorator(node) && !NodeTests.IsBinaryExpression(node))
        {
            var skipBindingPatterns = CoreUtil.Every(signature.typeParameters, p => GetDefaultFromTypeParameter(p) != null);
            var contextualType = GetContextualType(node, skipBindingPatterns ? ContextFlags.SkipBindingPatterns : ContextFlags.None);
            if (contextualType != null)
            {
                var inferenceTargetType = GetReturnTypeOfSignature(signature);
                if (couldContainTypeVariables(inferenceTargetType))
                {
                    var outerContext = GetInferenceContext(node);
                    var isFromBindingPattern = !skipBindingPatterns && GetContextualType(node, ContextFlags.SkipBindingPatterns) != contextualType;
                    // A return type inference from a binding pattern can be used in instantiating the contextual
                    // type of an argument later, but cannot stand on its own as the final return type.
                    if (!isFromBindingPattern)
                    {
                        // We clone the inference context to avoid disturbing a resolution in progress for an
                        // outer call expression.
                        var outerMapper = GetMapperFromContext(CloneInferenceContext(outerContext, InferenceFlags.NoDefault));
                        var instantiatedType = InstantiateType(contextualType, outerMapper);
                        // If the contextual type is a generic function type with a single call signature, we
                        // instantiate the type with its own type parameters and type arguments.
                        var contextualSignature = GetSingleCallSignature(instantiatedType);
                        Type inferenceSourceType;
                        if (contextualSignature != null && contextualSignature.typeParameters.Len() != 0)
                        {
                            inferenceSourceType = GetOrCreateTypeFromSignature(GetSignatureInstantiationWithoutFillingInTypeArguments(contextualSignature, contextualSignature.typeParameters));
                        }
                        else
                        {
                            inferenceSourceType = instantiatedType;
                        }
                        // Inferences made from return types have lower priority than all other inferences.
                        InferTypes(context.inferences, inferenceSourceType, inferenceTargetType, InferencePriority.ReturnType, false);
                    }
                    // Create a type mapper for instantiating generic contextual types using the inferences made
                    // from the return type.
                    var returnContext = NewInferenceContext(signature.typeParameters, signature, context.flags, null);
                    TypeMapper? outerReturnMapper = null;
                    if (outerContext != null)
                    {
                        outerReturnMapper = CreateOuterReturnMapper(outerContext);
                    }
                    var returnSourceType = InstantiateType(contextualType, outerReturnMapper);
                    InferTypes(returnContext.inferences, returnSourceType, inferenceTargetType, InferencePriority.None, false);
                    if (CoreUtil.Some(returnContext.inferences, HasInferenceCandidates))
                    {
                        context.returnMapper = GetMapperFromContext(CloneInferredPartOfContext(returnContext));
                    }
                    else
                    {
                        context.returnMapper = null;
                    }
                }
            }
        }
        var restType = GetNonArrayRestType(signature);
        var argCount = args.Length;
        if (restType != null)
        {
            argCount = global::System.Math.Min(GetParameterCount(signature) - 1, argCount);
        }
        if (restType != null && (restType.flags & TypeFlags.TypeParameter) != 0)
        {
            var info = CoreUtil.Find(context.inferences, info => info.typeParameter == restType);
            if (info != null)
            {
                if (CoreUtil.FindIndex(args[argCount..], IsSpreadArgument) < 0)
                {
                    info.impliedArity = args.Length - argCount;
                }
            }
        }
        var thisType = GetThisTypeOfSignature(signature);
        if (thisType != null && couldContainTypeVariables(thisType))
        {
            var thisArgumentNode = GetThisArgumentOfCall(node);
            InferTypes(context.inferences, GetThisArgumentType(thisArgumentNode), thisType, InferencePriority.None, false);
        }
        for (var i = 0; i < argCount; i++)
        {
            var arg = args[i];
            if (arg.Kind != SyntaxKind.OmittedExpression)
            {
                var paramType = GetTypeAtPosition(signature, i);
                if (couldContainTypeVariables(paramType))
                {
                    var argType = CheckExpressionWithContextualType(arg, paramType, context, checkMode);
                    InferTypes(context.inferences, argType, paramType, InferencePriority.None, false);
                }
            }
        }
        if (restType != null && couldContainTypeVariables(restType))
        {
            var spreadType = GetSpreadArgumentType(args, argCount, args.Length, restType, context, checkMode);
            InferTypes(context.inferences, spreadType, restType, InferencePriority.None, false);
        }
        return GetInferredTypes(context);
    }

    // checker.go:9474 — No signature was applicable. Errors already reported.
    private Signature GetCandidateForOverloadFailure(Node node, Signature[] candidates, Node[] args, bool hasCandidatesOutArray, CheckMode checkMode)
    {
        CheckNodeDeferred(node);
        // Normally we combine overloads. Skip if they have type parameters (hard to combine) or if there is
        // a candidatesOutArray (then we want the chosen best candidate to be one of the overloads).
        if (hasCandidatesOutArray || candidates.Length == 1 || CoreUtil.Some(candidates, static s => s.typeParameters.Len() != 0))
        {
            return PickLongestCandidateSignature(node, candidates, args, checkMode);
        }
        return CreateUnionOfSignaturesForOverloadFailure(candidates);
    }

    // checker.go:9486
    private Signature PickLongestCandidateSignature(Node node, Signature[] candidates, Node[] args, CheckMode checkMode)
    {
        // Pick the longest signature so we can get a contextual type for partial calls. Use supplied type
        // arguments if provided.
        var argCount = args.Length;
        if (apparentArgumentCount != null)
        {
            argCount = apparentArgumentCount.Value;
        }
        var bestIndex = GetLongestCandidateIndex(candidates, argCount);
        var candidate = candidates[bestIndex];
        var typeParameters = candidate.typeParameters;
        if (typeParameters.Len() == 0)
        {
            return candidate;
        }
        Node[]? typeArgumentNodes = null;
        if (CallLikeExpressionMayHaveTypeArguments(node))
        {
            typeArgumentNodes = node.TypeArguments();
        }
        Signature instantiated;
        if (typeArgumentNodes.Len() != 0)
        {
            instantiated = CreateSignatureInstantiation(candidate, GetTypeArgumentsFromNodes(typeArgumentNodes!, typeParameters!));
        }
        else
        {
            instantiated = InferSignatureInstantiationForOverloadFailure(node, typeParameters!, candidate, args, checkMode);
        }
        candidates[bestIndex] = instantiated;
        return instantiated;
    }

    // checker.go:9517
    private int GetLongestCandidateIndex(Signature[] candidates, int argsCount)
    {
        var maxParamsIndex = -1;
        var maxParams = -1;
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            var paramCount = GetParameterCount(candidate);
            if (HasEffectiveRestParameter(candidate) || paramCount >= argsCount)
            {
                return i;
            }
            if (paramCount > maxParams)
            {
                maxParams = paramCount;
                maxParamsIndex = i;
            }
        }
        return maxParamsIndex;
    }

    // checker.go:9533
    private Type[] GetTypeArgumentsFromNodes(Node[] typeArgumentNodes, Type[] typeParameters)
    {
        if (typeArgumentNodes.Length > typeParameters.Length)
        {
            typeArgumentNodes = typeArgumentNodes[..typeParameters.Length];
        }
        var typeArguments = new List<Type>(CoreUtil.Map(typeArgumentNodes, GetTypeFromTypeNode) ?? []);
        while (typeArguments.Count < typeParameters.Length)
        {
            var t = GetDefaultFromTypeParameter(typeParameters[typeArguments.Count]);
            if (t == null)
            {
                t = GetConstraintOfTypeParameter(typeParameters[typeArguments.Count]);
                if (t == null)
                {
                    t = unknownType!;
                }
            }
            typeArguments.Add(t);
        }
        return typeArguments.ToArray();
    }

    // checker.go:9551
    private Signature InferSignatureInstantiationForOverloadFailure(Node node, Type[] typeParameters, Signature candidate, Node[] args, CheckMode checkMode)
    {
        var inferenceContext = NewInferenceContext(typeParameters, candidate, AstUtilities.IsInJSFile(node) ? InferenceFlags.AnyDefault : InferenceFlags.None, null);
        var typeArgumentTypes = InferTypeArguments(node, candidate, args, checkMode | CheckMode.SkipContextSensitive | CheckMode.SkipGenericFunctions, inferenceContext);
        return CreateSignatureInstantiation(candidate, typeArgumentTypes);
    }

    // checker.go:9557
    private Signature CreateUnionOfSignaturesForOverloadFailure(Signature[] candidates)
    {
        var thisParameters = CoreUtil.MapNonNil(candidates, static c => c.thisParameter);
        Symbol? thisParameter = null;
        if (thisParameters.Len() != 0)
        {
            thisParameter = CreateCombinedSymbolFromTypes(thisParameters!, CoreUtil.Map(thisParameters, p => GetTypeOfParameter(p!))!);
        }
        var (minArgumentCount, maxNonRestParam) = MinAndMax(candidates, GetNonRestParameterCount);
        var parameters = new List<Symbol>(maxNonRestParam);
        for (var i = 0; i < maxNonRestParam; i++)
        {
            var idx = i;
            var symbols = CoreUtil.MapNonNil(candidates, s =>
            {
                if (SignatureHasRestParameter(s))
                {
                    if (idx < s.parameters!.Length - 1)
                    {
                        return s.parameters[idx];
                    }
                    return CoreUtil.LastOrNil(s.parameters);
                }
                if (idx < s.parameters.Len())
                {
                    return s.parameters![idx];
                }
                return (Symbol?)null;
            });
            parameters.Add(CreateCombinedSymbolFromTypes(symbols!, CoreUtil.MapNonNil(candidates, s => TryGetTypeAtPosition(s, idx))!));
        }
        var restParameterSymbols = CoreUtil.MapNonNil(candidates, static s =>
        {
            if (SignatureHasRestParameter(s))
            {
                return CoreUtil.LastOrNil(s.parameters);
            }
            return (Symbol?)null;
        });
        var flags = SignatureFlags.IsSignatureCandidateForOverloadFailure;
        if (restParameterSymbols.Len() != 0)
        {
            var t = CreateArrayType(GetUnionTypeEx(CoreUtil.MapNonNil(candidates, TryGetRestTypeOfSignature)!, UnionReduction.Subtype, null, null));
            parameters.Add(CreateCombinedSymbolForOverloadFailure(restParameterSymbols!, t));
            flags |= SignatureFlags.HasRestParameter;
        }
        if (CoreUtil.Some(candidates, SignatureHasLiteralTypes))
        {
            flags |= SignatureFlags.HasLiteralTypes;
        }
        return NewSignature(flags, candidates[0].declaration, null, thisParameter, parameters.ToArray(), GetIntersectionType(CoreUtil.Map(candidates, GetReturnTypeOfSignature)), null, minArgumentCount);
    }

    // checker.go:9598
    private Symbol CreateCombinedSymbolFromTypes(Symbol[] sources, Type[] types)
    {
        return CreateCombinedSymbolForOverloadFailure(sources, GetUnionTypeEx(types, UnionReduction.Subtype, null, null));
    }

    // checker.go:9602
    private Symbol CreateCombinedSymbolForOverloadFailure(Symbol[] sources, Type t)
    {
        // This function is currently only used for erroneous overloads, so it's good enough to just use the first source.
        return CreateSymbolWithType(CoreUtil.FirstOrNil(sources)!, t);
    }

    // checker.go:9611
    private Type? TryGetRestTypeOfSignature(Signature signature)
    {
        if (!SignatureHasRestParameter(signature))
        {
            return null;
        }
        var restType = GetTypeOfSymbol(signature.parameters![signature.parameters.Length - 1]);
        if (IsTupleType(restType))
        {
            restType = GetRestTypeOfTupleType(restType);
            if (restType == null)
            {
                return null;
            }
        }
        return GetIndexTypeOfType(restType, numberType!);
    }

    // checker.go:9625
    private void ReportCallResolutionErrors(Node node, ref CallState s, Signature[] signatures, global::Ts.Diagnostics.Message? headMessage)
    {
        if (s.candidatesForArgumentError != null && s.candidatesForArgumentError.Count != 0)
        {
            var last = s.candidatesForArgumentError[s.candidatesForArgumentError.Count - 1];
            var diags = new List<Diagnostic>();
            IsSignatureApplicable(s.node, s.args, last, assignableRelation!, CheckMode.Normal, true /*reportErrors*/, diags);
            foreach (var d in diags)
            {
                var diagnostic = d;
                if (s.candidatesForArgumentError!.Count > 1)
                {
                    diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, Diag.The_last_overload_gave_the_following_error);
                    diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, Diag.No_overload_matches_this_call);
                }
                if (headMessage != null)
                {
                    diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, headMessage);
                }
                if (last.declaration != null && s.candidatesForArgumentError.Count > 1)
                {
                    diagnostic.AddRelatedInfo(NewDiagnosticForNode(last.declaration, Diag.The_last_overload_is_declared_here));
                }
                AddImplementationSuccessElaboration(ref s, last, diagnostic);
                AddDiagnostic(diagnostic);
            }
        }
        else if (s.candidateForArgumentArityError != null)
        {
            AddDiagnostic(GetArgumentArityError(s.node, new[] { s.candidateForArgumentArityError }, s.args, headMessage));
        }
        else if (s.candidateForTypeArgumentError != null)
        {
            CheckTypeArguments(s.candidateForTypeArgumentError, s.node.TypeArguments()!, true /*reportErrors*/, headMessage);
        }
        else if (!NodeTests.IsJsxOpeningFragment(node))
        {
            var typeArgs = s.typeArguments;
            var signaturesWithCorrectTypeArgumentArity = CoreUtil.Filter(signatures, sig => HasCorrectTypeArgumentArity(sig, typeArgs)) ?? [];
            if (signaturesWithCorrectTypeArgumentArity.Length == 0)
            {
                AddDiagnostic(GetTypeArgumentArityError(s.node, signatures, s.typeArguments, headMessage));
            }
            else
            {
                AddDiagnostic(GetArgumentArityError(s.node, signaturesWithCorrectTypeArgumentArity, s.args, headMessage));
            }
        }
    }

    // checker.go:9661
    private void AddImplementationSuccessElaboration(ref CallState s, Signature failed, Diagnostic diagnostic)
    {
        if (failed.declaration != null && failed.declaration.Symbol() != null)
        {
            var declarations = failed.declaration.Symbol()!.Declarations;
            if (declarations.Len() > 1)
            {
                var implementation = CoreUtil.Find(declarations, static d => AstUtilities.IsFunctionLikeDeclaration(d) && AstUtilities.NodeIsPresent(d.Body()));
                if (implementation != null)
                {
                    var candidate = GetSignatureFromDeclaration(implementation);
                    var localState = s;
                    localState.candidates = new[] { candidate };
                    localState.isSingleNonGenericCandidate = candidate.typeParameters.Len() == 0;
                    if (ChooseOverload(ref localState, assignableRelation!) != null)
                    {
                        diagnostic.AddRelatedInfo(NewDiagnosticForNode(implementation, Diag.The_call_would_have_succeeded_against_this_implementation_but_implementation_signatures_of_overloads_are_not_externally_visible));
                    }
                }
            }
        }
    }

    // checker.go:9681
    private Diagnostic GetArgumentArityError(Node node, Signature[] signatures, Node[] args, global::Ts.Diagnostics.Message? headMessage)
    {
        var spreadIndex = GetSpreadArgumentIndex(args);
        if (spreadIndex > -1)
        {
            return NewDiagnosticForNode(args[spreadIndex], Diag.A_spread_argument_must_either_have_a_tuple_type_or_be_passed_to_a_rest_parameter);
        }
        var minCount = int.MaxValue; // smallest parameter count
        var maxCount = int.MinValue; // largest parameter count
        var maxBelow = int.MinValue; // largest parameter count that is smaller than the number of arguments
        var minAbove = int.MaxValue; // smallest parameter count that is larger than the number of arguments
        Signature? closestSignature = null;
        foreach (var sig in signatures)
        {
            var minParameter = GetMinArgumentCount(sig);
            var maxParameter = GetParameterCount(sig);
            // smallest/largest parameter counts
            if (minParameter < minCount)
            {
                minCount = minParameter;
                closestSignature = sig;
            }
            maxCount = global::System.Math.Max(maxCount, maxParameter);
            // shortest parameter count *longer than the call*/longest parameter count *shorter than the call*
            if (minParameter < args.Length && minParameter > maxBelow)
            {
                maxBelow = minParameter;
            }
            if (args.Length < maxParameter && maxParameter < minAbove)
            {
                minAbove = maxParameter;
            }
        }
        var hasRestParameter = CoreUtil.Some(signatures, HasEffectiveRestParameter);
        string parameterRange;
        if (hasRestParameter)
        {
            parameterRange = minCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (minCount < maxCount)
        {
            parameterRange = minCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture) + "-" + maxCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            parameterRange = minCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
        }
        var isVoidPromiseError = !hasRestParameter && parameterRange == "1" && args.Length == 0 && IsPromiseResolveArityError(node);
        var errorNode = GetErrorNodeForCallNode(node);
        if (isVoidPromiseError && AstUtilities.IsInJSFile(node))
        {
            return NewDiagnosticForNode(errorNode, Diag.Expected_1_argument_but_got_0_new_Promise_needs_a_JSDoc_hint_to_produce_a_resolve_that_can_be_called_without_arguments);
        }
        global::Ts.Diagnostics.Message message;
        if (NodeTests.IsDecorator(node))
        {
            if (hasRestParameter)
            {
                message = Diag.The_runtime_will_invoke_the_decorator_with_1_arguments_but_the_decorator_expects_at_least_0;
            }
            else
            {
                message = Diag.The_runtime_will_invoke_the_decorator_with_1_arguments_but_the_decorator_expects_0;
            }
        }
        else if (hasRestParameter)
        {
            message = Diag.Expected_at_least_0_arguments_but_got_1;
        }
        else if (isVoidPromiseError)
        {
            message = Diag.Expected_0_arguments_but_got_1_Did_you_forget_to_include_void_in_your_type_argument_to_Promise;
        }
        else
        {
            message = Diag.Expected_0_arguments_but_got_1;
        }
        if (minCount < args.Length && args.Length < maxCount)
        {
            // between min and max, but with no matching overload
            var diagnostic = NewDiagnosticForNode(errorNode, Diag.No_overload_expects_0_arguments_but_overloads_do_exist_that_expect_either_1_or_2_arguments, args.Length, maxBelow, minAbove);
            if (headMessage != null)
            {
                diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, headMessage);
            }
            return diagnostic;
        }
        if (args.Length < minCount)
        {
            // too short: put the error span on the call expression, not any of the args
            var diagnostic = NewDiagnosticForNode(errorNode, message, parameterRange, args.Length);
            if (headMessage != null)
            {
                diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, headMessage);
            }
            Node? parameter = null;
            if (closestSignature != null && closestSignature.declaration != null)
            {
                parameter = CoreUtil.ElementOrNil(closestSignature.declaration.Parameters(), args.Length + (closestSignature.thisParameter != null ? 1 : 0));
            }
            if (parameter != null)
            {
                Diagnostic related;
                if (AstUtilities.IsBindingPattern(parameter.Name()))
                {
                    related = NewDiagnosticForNode(parameter, Diag.An_argument_matching_this_binding_pattern_was_not_provided);
                }
                else if (IsRestParameter(parameter))
                {
                    related = NewDiagnosticForNode(parameter, Diag.Arguments_for_the_rest_parameter_0_were_not_provided, parameter.Name()!.Text());
                }
                else
                {
                    related = NewDiagnosticForNode(parameter, Diag.An_argument_for_0_was_not_provided, parameter.Name()!.Text());
                }
                diagnostic.AddRelatedInfo(related);
            }
            return diagnostic;
        }
        // Guard against out-of-bounds access when maxCount >= len(args).
        if (maxCount >= args.Length)
        {
            var diagnostic = NewDiagnosticForNode(errorNode, message, parameterRange, args.Length);
            if (headMessage != null)
            {
                diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, headMessage);
            }
            return diagnostic;
        }
        var sourceFile = AstUtilities.GetSourceFileOfNode(node);
        var pos = args[maxCount].Pos;
        var end = args[args.Length - 1].End;
        if (end == pos)
        {
            end++;
        }
        pos = global::Ts.Scanner.Scanner.SkipTrivia(sourceFile!.Text(), pos);
        if (end < pos)
        {
            end = pos;
        }
        var diag = Diagnostic.NewDiagnostic(sourceFile, new TextRange(pos, end), message, parameterRange, args.Length);
        if (headMessage != null)
        {
            diag = Diagnostic.NewDiagnosticChain(diag, headMessage);
        }
        return diag;
    }

    // checker.go:9799
    private bool IsPromiseResolveArityError(Node node)
    {
        if (!NodeTests.IsCallExpression(node) || !NodeTests.IsIdentifier(node.Expression()!))
        {
            return false;
        }
        var symbol = ResolveName(node.Expression()!, node.Expression()!.Text(), SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, false);
        if (symbol == null)
        {
            return false;
        }
        var decl = symbol.ValueDeclaration;
        if (decl == null || !NodeTests.IsParameterDeclaration(decl) || !AstUtilities.IsFunctionExpressionOrArrowFunction(decl.Parent!) || !NodeTests.IsNewExpression(decl.Parent!.Parent!) || !NodeTests.IsIdentifier(decl.Parent.Parent!.Expression()!))
        {
            return false;
        }
        var globalPromiseSymbol = GetGlobalPromiseConstructorSymbolOrNil();
        if (globalPromiseSymbol == null)
        {
            return false;
        }
        var constructorSymbol = GetResolvedSymbol(decl.Parent!.Parent!.Expression()!);
        return constructorSymbol == globalPromiseSymbol;
    }

    // checker.go:9819
    private static Node GetErrorNodeForCallNode(Node node)
    {
        if (NodeTests.IsCallExpression(node))
        {
            node = node.Expression()!;
            if (NodeTests.IsPropertyAccessExpression(node))
            {
                node = node.Name()!;
            }
        }
        return node;
    }

    // checker.go:9829
    private Diagnostic GetTypeArgumentArityError(Node node, Signature[] signatures, Node[]? typeArguments, global::Ts.Diagnostics.Message? headMessage)
    {
        Diagnostic diagnostic;
        var argCount = typeArguments.Len();
        var sourceFile = AstUtilities.GetSourceFileOfNode(node);
        var typeArgumentList = node.TypeArgumentList()!;
        var loc = new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceFile!.Text(), typeArgumentList.Loc.Pos), typeArgumentList.Loc.End);
        if (signatures.Length == 1)
        {
            // No overloads exist
            var sig = signatures[0];
            var minCount = GetMinTypeArgumentCount(sig.typeParameters);
            var maxCount = sig.typeParameters.Len();
            var expected = minCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            if (minCount < maxCount)
            {
                expected = expected + "-" + maxCount.ToString(global::System.Globalization.CultureInfo.InvariantCulture);
            }
            diagnostic = Diagnostic.NewDiagnostic(sourceFile, loc, Diag.Expected_0_type_arguments_but_got_1, expected, argCount);
        }
        else
        {
            // Overloads exist
            var belowArgCount = int.MinValue;
            var aboveArgCount = int.MaxValue;
            foreach (var sig in signatures)
            {
                var minCount = GetMinTypeArgumentCount(sig.typeParameters);
                var maxCount = sig.typeParameters.Len();
                if (minCount > argCount)
                {
                    aboveArgCount = global::System.Math.Min(aboveArgCount, minCount);
                }
                else if (maxCount < argCount)
                {
                    belowArgCount = global::System.Math.Max(belowArgCount, maxCount);
                }
            }
            if (belowArgCount != int.MinValue && aboveArgCount != int.MaxValue)
            {
                diagnostic = Diagnostic.NewDiagnostic(sourceFile, loc, Diag.No_overload_expects_0_type_arguments_but_overloads_do_exist_that_expect_either_1_or_2_type_arguments, argCount, belowArgCount, aboveArgCount);
            }
            else
            {
                diagnostic = Diagnostic.NewDiagnostic(sourceFile, loc, Diag.Expected_0_type_arguments_but_got_1, belowArgCount == int.MinValue ? aboveArgCount : belowArgCount, argCount);
            }
        }
        if (headMessage != null)
        {
            diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, headMessage);
        }
        return diagnostic;
    }

    // checker.go:9870
    private void ReportCannotInvokePossiblyNullOrUndefinedError(Node node, TypeFacts facts)
    {
        Error(node, (facts & TypeFacts.IsUndefined) != 0
            ? ((facts & TypeFacts.IsNull) != 0
                ? Diag.Cannot_invoke_an_object_which_is_possibly_null_or_undefined
                : Diag.Cannot_invoke_an_object_which_is_possibly_undefined)
            : Diag.Cannot_invoke_an_object_which_is_possibly_null);
    }

    // checker.go:9878
    private Signature ResolveUntypedCall(Node node)
    {
        if (CallLikeExpressionMayHaveTypeArguments(node))
        {
            // Check type arguments even though we will give an error that untyped calls may not accept type
            // arguments. This gets us diagnostics for the type arguments and marks them as referenced.
            CheckSourceElements(node.TypeArguments());
        }
        switch (node.Kind)
        {
            case SyntaxKind.TaggedTemplateExpression:
                CheckExpression(node.AsTaggedTemplateExpression().Template!);
                break;
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
                CheckExpression(node.Attributes()!);
                break;
            case SyntaxKind.BinaryExpression:
                CheckExpression(node.AsBinaryExpression().Left!);
                break;
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
                // Go: for range over node.Arguments() — a nil argument list (e.g. `new C`) is a no-op.
                foreach (var argument in node.Arguments() ?? [])
                {
                    CheckExpression(argument);
                }
                break;
        }
        return anySignature!;
    }

    // checker.go:9899
    private Signature ResolveErrorCall(Node node)
    {
        ResolveUntypedCall(node);
        return unknownSignature!;
    }

    // checker.go:9909
    // TS 1.0 spec 4.12: if FuncExpr is Any, or an object type with no call/construct signatures but a
    // subtype of Function, the call is an untyped function call.
    private bool IsUntypedFunctionCall(Type funcType, Type apparentFuncType, int numCallSignatures, int numConstructSignatures)
    {
        // We exclude union types because we may have a union of function types with no common signatures.
        return IsTypeAny(funcType) || IsTypeAny(apparentFuncType) && (funcType.flags & TypeFlags.TypeParameter) != 0 ||
            numCallSignatures == 0 && numConstructSignatures == 0 && (apparentFuncType.flags & TypeFlags.Union) == 0 &&
                (GetReducedType(apparentFuncType).flags & TypeFlags.Never) == 0 && IsTypeAssignableTo(funcType, globalFunctionType!);
    }

    // checker.go:9916
    private Diagnostic InvocationErrorDetails(Node errorTarget, Type apparentType, SignatureKind kind)
    {
        Diagnostic? diagnostic = null;
        var isCall = kind == SignatureKind.Call;
        var awaitedType = GetAwaitedType(apparentType);
        var maybeMissingAwait = awaitedType != null && GetSignaturesOfType(awaitedType, kind).Length > 0;
        var target = errorTarget;
        if (NodeTests.IsPropertyAccessExpression(errorTarget) && NodeTests.IsCallExpression(errorTarget.Parent!))
        {
            target = errorTarget.Name()!;
        }
        if ((apparentType.flags & TypeFlags.Union) != 0)
        {
            var types = apparentType.Types();
            var hasSignatures = false;
            foreach (var constituent in types)
            {
                var signatures = GetSignaturesOfType(constituent, kind);
                if (signatures.Length != 0)
                {
                    hasSignatures = true;
                    if (diagnostic != null)
                    {
                        break;
                    }
                }
                else
                {
                    if (diagnostic == null)
                    {
                        diagnostic = NewDiagnosticForNode(target, isCall ? Diag.Type_0_has_no_call_signatures : Diag.Type_0_has_no_construct_signatures, TypeToString(constituent));
                        diagnostic = NewDiagnosticChainForNode(diagnostic, target, isCall ? Diag.Not_all_constituents_of_type_0_are_callable : Diag.Not_all_constituents_of_type_0_are_constructable, TypeToString(apparentType));
                    }
                    if (hasSignatures)
                    {
                        break;
                    }
                }
            }
            if (!hasSignatures)
            {
                diagnostic = NewDiagnosticForNode(target, isCall ? Diag.No_constituent_of_type_0_is_callable : Diag.No_constituent_of_type_0_is_constructable, TypeToString(apparentType));
            }
            if (diagnostic == null)
            {
                diagnostic = NewDiagnosticForNode(target, isCall ? Diag.Each_member_of_the_union_type_0_has_signatures_but_none_of_those_signatures_are_compatible_with_each_other : Diag.Each_member_of_the_union_type_0_has_construct_signatures_but_none_of_those_signatures_are_compatible_with_each_other, TypeToString(apparentType));
            }
        }
        else
        {
            diagnostic = NewDiagnosticChainForNode(diagnostic, target, isCall ? Diag.Type_0_has_no_call_signatures : Diag.Type_0_has_no_construct_signatures, TypeToString(apparentType));
        }
        var headMessage = isCall ? Diag.This_expression_is_not_callable : Diag.This_expression_is_not_constructable;
        // Diagnose get accessors incorrectly called as functions
        if (NodeTests.IsCallExpression(errorTarget.Parent!) && errorTarget.Parent!.Arguments().Len() == 0)
        {
            var resolvedSymbol = GetResolvedSymbolOrNil(errorTarget);
            if (resolvedSymbol != null && (resolvedSymbol.Flags & SymbolFlags.GetAccessor) != 0)
            {
                headMessage = Diag.This_expression_is_not_callable_because_it_is_a_get_accessor_Did_you_mean_to_use_it_without;
            }
        }
        diagnostic = NewDiagnosticChainForNode(diagnostic, target, headMessage)!;
        if (maybeMissingAwait)
        {
            diagnostic!.AddRelatedInfo(NewDiagnosticForNode(errorTarget, Diag.Did_you_forget_to_use_await));
        }
        return diagnostic!;
    }

    // checker.go:9972
    private void InvocationError(Node errorTarget, Type apparentType, SignatureKind kind, Diagnostic? relatedInformation)
    {
        var diagnostic = InvocationErrorDetails(errorTarget, apparentType, kind);
        if (relatedInformation != null)
        {
            diagnostic.AddRelatedInfo(relatedInformation);
        }
        AddDiagnostic(diagnostic);
        InvocationErrorRecovery(apparentType, kind, diagnostic);
    }

    // checker.go:9981
    private void InvocationErrorRecovery(Type apparentType, SignatureKind kind, Diagnostic diagnostic)
    {
        if (apparentType.symbol == null)
        {
            return;
        }
        var importNode = exportTypeLinks.Get(apparentType.symbol).originatingImport;
        // Create a diagnostic on the originating import if possible onto which we can attach a quickfix.
        if (importNode != null && !IsImportCall(importNode))
        {
            var sigs = GetSignaturesOfType(GetTypeOfSymbol(exportTypeLinks.Get(apparentType.symbol).target!), kind);
            if (sigs.Length == 0)
            {
                return;
            }
            diagnostic.AddRelatedInfo(NewDiagnosticForNode(importNode, Diag.Type_originates_at_this_import_A_namespace_style_import_cannot_be_called_or_constructed_and_will_cause_a_failure_at_runtime_Consider_using_a_default_import_or_import_require_here_instead));
        }
    }

    // ===== argument materialization (checker.go:29257 / 29799 / 29865) =====

    // checker.go:29257
    private Type GetSpreadArgumentType(Node[] args, int index, int argCount, Type restType, InferenceContext? context, CheckMode checkMode)
    {
        var inConstContext = IsConstTypeVariable(restType, 0);
        if (argCount > 0 && index >= argCount - 1)
        {
            var arg = args[argCount - 1];
            if (IsSpreadArgument(arg))
            {
                // We are inferring from a spread expression in the last argument position, i.e. both the
                // parameter and the argument are ...x forms.
                Type spreadType;
                if (NodeTests.IsSyntheticExpression(arg))
                {
                    spreadType = (Type)arg.AsSyntheticExpression().Type!;
                }
                else
                {
                    spreadType = CheckExpressionWithContextualType(arg.Expression()!, restType, context, checkMode);
                }
                if (IsArrayLikeType(spreadType))
                {
                    return GetMutableArrayOrTupleType(spreadType);
                }
                if (NodeTests.IsSpreadElement(arg))
                {
                    arg = arg.Expression()!;
                }
                return CreateArrayTypeEx(CheckIteratedTypeOrElementType(IterationUse.Spread, spreadType, undefinedType!, arg), inConstContext);
            }
        }
        var types = new List<Type>();
        var infos = new List<TupleElementInfo>();
        for (var i = index; i < argCount; i++)
        {
            var arg = args[i];
            Type t;
            var infoFlags = ElementFlags.Required;
            Node? labeledDeclaration = null;
            if (IsSpreadArgument(arg))
            {
                Type spreadType;
                if (NodeTests.IsSyntheticExpression(arg))
                {
                    spreadType = (Type)arg.AsSyntheticExpression().Type!;
                }
                else
                {
                    spreadType = CheckExpression(arg.Expression()!);
                }
                if (IsArrayLikeType(spreadType))
                {
                    t = spreadType;
                    infoFlags = ElementFlags.Variadic;
                }
                else
                {
                    if (NodeTests.IsSpreadElement(arg))
                    {
                        t = CheckIteratedTypeOrElementType(IterationUse.Spread, spreadType, undefinedType!, arg.Expression()!);
                    }
                    else
                    {
                        t = CheckIteratedTypeOrElementType(IterationUse.Spread, spreadType, undefinedType!, arg);
                    }
                    infoFlags = ElementFlags.Rest;
                }
            }
            else
            {
                Type contextualType;
                if (IsTupleType(restType))
                {
                    contextualType = GetContextualTypeForElementExpression(restType, i - index, argCount - index, -1, -1) ?? unknownType!;
                }
                else
                {
                    contextualType = GetIndexedAccessTypeEx(restType, GetNumberLiteralType(new global::Ts.JsNum.Number(i - index)), AccessFlags.Contextual, null, null);
                }
                var argType = CheckExpressionWithContextualType(arg, contextualType, context, checkMode);
                var hasPrimitiveContextualType = inConstContext || MaybeTypeOfKind(contextualType, TypeFlags.Primitive | TypeFlags.Index | TypeFlags.TemplateLiteral | TypeFlags.StringMapping);
                if (hasPrimitiveContextualType)
                {
                    t = GetRegularTypeOfLiteralType(argType);
                }
                else
                {
                    t = GetWidenedLiteralType(argType);
                }
            }
            if (NodeTests.IsSyntheticExpression(arg) && arg.AsSyntheticExpression().TupleNameSource != null)
            {
                labeledDeclaration = arg.AsSyntheticExpression().TupleNameSource;
            }
            types.Add(t);
            infos.Add(new TupleElementInfo(infoFlags, labeledDeclaration));
        }
        return CreateTupleTypeEx(types.ToArray(), infos.ToArray(), inConstContext && !SomeType(restType, IsMutableArrayLikeType));
    }

    // checker.go:29328
    private Type GetMutableArrayOrTupleType(Type t)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return MapType(t, GetMutableArrayOrTupleType);
        }
        if ((t.flags & TypeFlags.Any) != 0 || IsMutableArrayOrTuple(GetBaseConstraintOrType(t)))
        {
            return t;
        }
        if (IsTupleType(t))
        {
            return CreateTupleTypeEx(GetElementTypes(t), t.TargetTupleType()!.elementInfos!, false /*readonly*/);
        }
        return CreateTupleTypeEx(new[] { t }, new[] { new TupleElementInfo(ElementFlags.Variadic, null) }, false);
    }

    // checker.go:29799
    private Node[] GetEffectiveCallArguments(Node node)
    {
        if (NodeTests.IsJsxOpeningFragment(node))
        {
            // This attributes type does not include a children property yet.
            return new[] { CreateSyntheticExpression(node, emptyFreshJsxObjectType!, false, null) };
        }
        if (NodeTests.IsTaggedTemplateExpression(node))
        {
            var template = node.AsTaggedTemplateExpression().Template;
            var firstArg = CreateSyntheticExpression(template!, GetGlobalTemplateStringsArrayType(), false, null);
            if (!NodeTests.IsTemplateExpression(template!))
            {
                return new[] { firstArg };
            }
            var spans = template!.AsTemplateExpression().TemplateSpans!.Nodes;
            var args = new Node[spans.Length + 1];
            args[0] = firstArg;
            for (var i = 0; i < spans.Length; i++)
            {
                args[i + 1] = spans[i].Expression()!;
            }
            return args;
        }
        if (NodeTests.IsDecorator(node))
        {
            return GetEffectiveDecoratorArguments(node);
        }
        if (NodeTests.IsBinaryExpression(node))
        {
            // Handles instanceof operator
            return new Node[] { node.AsBinaryExpression().Left! };
        }
        if (IsJsxOpeningLikeElement(node))
        {
            if (node.Attributes()!.Properties().Len() != 0 || (NodeTests.IsJsxOpeningElement(node) && node.Parent!.Children()!.Nodes.Length != 0))
            {
                return new[] { node.Attributes()! };
            }
            return [];
        }
        var arguments = node.Arguments();
        var spreadIndex = GetSpreadArgumentIndex(arguments);
        if (spreadIndex >= 0)
        {
            // Create synthetic arguments from spreads of tuple types.
            var effectiveArgs = new List<Node>(arguments![..spreadIndex]);
            for (var i = spreadIndex; i < arguments!.Length; i++)
            {
                var arg = arguments[i];
                Type? spreadType = null;
                // We can call checkExpressionCached because spread expressions never have a contextual type.
                if (NodeTests.IsSpreadElement(arg))
                {
                    if (flowLoopStack.Count != 0)
                    {
                        spreadType = CheckExpression(arg.Expression()!);
                    }
                    else
                    {
                        spreadType = CheckExpressionCached(arg.Expression()!);
                    }
                }
                if (spreadType != null && IsTupleType(spreadType))
                {
                    var elementTypes = GetElementTypes(spreadType);
                    for (var j = 0; j < elementTypes.Length; j++)
                    {
                        var elementInfos = spreadType.TargetTupleType()!.elementInfos!;
                        var flags = elementInfos[j].flags;
                        var syntheticType = elementTypes[j];
                        if ((flags & ElementFlags.Rest) != 0)
                        {
                            syntheticType = CreateArrayType(elementTypes[j]);
                        }
                        var syntheticArg = CreateSyntheticExpression(arg, syntheticType, (flags & ElementFlags.Variable) != 0, elementInfos[j].labeledDeclaration);
                        effectiveArgs.Add(syntheticArg);
                    }
                }
                else
                {
                    effectiveArgs.Add(arg);
                }
            }
            return effectiveArgs.ToArray();
        }
        // PORT-NOTE: Go returns the possibly-nil `args` slice here (`new C` / `x.f` without an
        // argument list yields node.Arguments() == nil). No consumer in checker.go distinguishes
        // nil from empty — all uses are len/range/index — so per plan/05 §6.1 rule 1 the nullable
        // accessor result normalizes to an empty array at this materialization boundary.
        return arguments ?? [];
    }

    // checker.go:29865
    private int GetSpreadArgumentIndex(Node[]? args) => CoreUtil.FindIndex(args, IsSpreadArgument);

    // checker.go:29869
    private static bool IsSpreadArgument(Node arg) => NodeTests.IsSpreadElement(arg) || NodeTests.IsSyntheticExpression(arg) && arg.AsSyntheticExpression().IsSpread;

    // checker.go:29873
    internal Node CreateSyntheticExpression(Node parent, Type t, bool isSpread, Node? tupleNameSource)
    {
        var result = factory.NewSyntheticExpression(t, isSpread, tupleNameSource);
        result.Loc = parent.Loc;
        result.Parent = parent;
        return result;
    }
}
