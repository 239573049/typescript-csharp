// Ported from tsgo internal/checker/checker.go — call/overload resolution family.
// Trunk: checkCallExpression (8298) → getResolvedSignature (8383) → resolveSignature (8429) →
// resolveCallExpression (8447) / resolveNewExpression (8551) → resolveCall (8819) → chooseOverload
// (9001) → hasCorrectArity (9083) / isSignatureApplicable (9232) / inferTypeArguments (9366) /
// getCandidateForOverloadFailure (9474) / reportCallResolutionErrors (9625). Plus the argument
// materialization helpers getEffectiveCallArguments (29799) / getSpreadArgumentType (29257).
//
// §1.5: cross-package classes referenced fully-qualified via global::Ts.<Pkg>.<Class>; host classes
// (NodeTests / AstUtilities) used directly. Diagnostics via the checker's own NewDiagnosticForNode /
// diagnostics.Diagnostics table. Deep-recursion entry is reached through the existing CheckSourceFile
// LargeStackExecutor boundary (§铁律 8); no new public entry is introduced here.
//
// PORT-NOTE: this is the breadth-first trunk. Faithfully-ported paths cover single- and multi-signature
// call/new resolution, arity, applicability (assignability), type-argument inference, and the overload-
// failure diagnostics. Deferred leaves (still stubs in their owning *.Deps files) that this trunk CALLS:
// checkExpressionWithContextualType, checkDeprecatedSignature, getInferenceContext, isContextSensitive,
// getRegularTypeOfObjectLiteral, getAwaitedType/getAwaitedTypeOfPromise, resolveTaggedTemplateExpression,
// resolveDecorator, resolveInstanceofExpression, resolveJsxOpeningLikeElement (real), plus the small
// helpers in Checker.CallResolution.Deps.cs.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:8806-8817
    private struct CallState
    {
        internal Node node;
        internal Node[]? typeArguments;
        internal Node[] args;
        internal Signature[] candidates;
        internal CheckMode argCheckMode;
        internal bool isSingleNonGenericCandidate;
        internal bool signatureHelpTrailingComma;
        internal List<Signature>? candidatesForArgumentError;
        internal Signature? candidateForArgumentArityError;
        internal Signature? candidateForTypeArgumentError;
    }

    // checker.go:8298
    internal Type CheckCallExpression(Node node, CheckMode checkMode)
    {
        CheckGrammarTypeArguments(node, node.TypeArgumentList());
        var signature = GetResolvedSignature(node, null /*candidatesOutArray*/, checkMode);
        if (signature == resolvingSignature)
        {
            // CheckMode.SkipGenericFunctions is enabled and this is a call to a generic function that
            // returns a function type. We defer checking and return silentNeverType.
            return silentNeverType!;
        }
        CheckDeprecatedSignature(signature, node);
        if (node.Expression()!.Kind == SyntaxKind.SuperKeyword)
        {
            return voidType!;
        }
        if (NodeTests.IsNewExpression(node))
        {
            var declaration = signature.declaration;
            if (declaration != null && !NodeTests.IsConstructorDeclaration(declaration) && !NodeTests.IsConstructSignatureDeclaration(declaration) && !NodeTests.IsConstructorTypeNode(declaration))
            {
                // When resolved signature is a call signature (and not a construct signature) the result type is any
                if (noImplicitAny)
                {
                    Error(node, Diag.X_new_expression_whose_target_lacks_a_construct_signature_implicitly_has_an_any_type);
                }
                return anyType!;
            }
        }
        if (AstUtilities.IsInJSFile(node) && IsCommonJSRequire(node))
        {
            return ResolveExternalModuleTypeByLiteral(node.Arguments()![0]);
        }
        var returnType = GetReturnTypeOfSignature(signature);
        // Treat any call to the global 'Symbol' function that is part of a const variable or readonly property
        // as a fresh unique symbol literal type.
        if ((returnType.flags & TypeFlags.ESSymbolLike) != 0 && IsSymbolOrSymbolForCall(node))
        {
            return GetESSymbolLikeTypeForNode(AstUtilities.WalkUpParenthesizedExpressions(node.Parent!)!);
        }
        if (NodeTests.IsCallExpression(node) && node.QuestionDotToken() == null && NodeTests.IsExpressionStatement(node.Parent!) && (returnType.flags & TypeFlags.Void) != 0 && GetTypePredicateOfSignature(signature) != null)
        {
            if (!AstUtilities.IsDottedName(node.Expression()!))
            {
                Error(node.Expression(), Diag.Assertions_require_the_call_target_to_be_an_identifier_or_qualified_name);
            }
            else if (GetEffectsSignature(node) == null)
            {
                var diagnostic = Error(node.Expression(), Diag.Assertions_require_every_name_in_the_call_target_to_be_declared_with_an_explicit_type_annotation);
                GetTypeOfDottedName(node.Expression()!, diagnostic);
            }
        }
        return returnType;
    }

    // checker.go:8357
    private bool IsSymbolOrSymbolForCall(Node node)
    {
        if (!NodeTests.IsCallExpression(node))
        {
            return false;
        }
        var left = node.Expression()!;
        if (NodeTests.IsPropertyAccessExpression(left) && left.Name()!.Text() == "for")
        {
            left = left.Expression()!;
        }
        if (!NodeTests.IsIdentifier(left) || left.Text() != "Symbol")
        {
            return false;
        }
        // make sure `Symbol` is the global symbol
        var globalESSymbol = GetGlobalESSymbolConstructorSymbolOrNil();
        if (globalESSymbol == null)
        {
            return false;
        }
        return globalESSymbol == ResolveName(left, "Symbol", SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, false);
    }

    // checker.go:8383
    // Resolve a signature of a given call-like expression.
    internal Signature GetResolvedSignature(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        var links = signatureLinks.Get(node);
        // If getResolvedSignature has already been called, we will have cached the resolvedSignature.
        // However, it is possible that either candidatesOutArray was not passed in the first time,
        // or that a different candidatesOutArray was passed in. Therefore, we need to redo the work
        // to correctly fill the candidatesOutArray.
        var cached = links.resolvedSignature;
        if (cached != null && cached != resolvingSignature && candidatesOutArray == null)
        {
            return cached;
        }
        var saveResolutionStart = resolutionStart;
        if (cached == null)
        {
            // If we haven't already done so, temporarily reset the resolution stack. This allows us to
            // handle "inverted" situations where, for example, an API client asks for the type of a symbol
            // containined in a function call argument whose contextual type depends on the symbol itself
            // through resolution of the containing function call.
            resolutionStart = typeResolutions.Count;
        }
        links.resolvedSignature = resolvingSignature;
        var result = ResolveSignature(node, candidatesOutArray, checkMode);
        resolutionStart = saveResolutionStart;
        // When CheckMode.SkipGenericFunctions is set we use resolvingSignature to indicate that call
        // resolution should be deferred.
        if (result != resolvingSignature)
        {
            // if the signature resolution originated on a node that itself depends on the contextual type
            // then it's possible that the resolved signature might not be the same as the one that would be
            // computed in source order. In such a case we ignore the local result and reuse the cached one.
            if (links.resolvedSignature != resolvingSignature)
            {
                result = links.resolvedSignature!;
            }
            // If signature resolution originated in control flow type analysis we don't cache the result.
            if (flowLoopStack.Count == 0)
            {
                links.resolvedSignature = result;
            }
            else
            {
                links.resolvedSignature = cached;
            }
        }
        return result;
    }

    // checker.go:8429
    private Signature ResolveSignature(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        switch (node.Kind)
        {
            case SyntaxKind.CallExpression:
                return ResolveCallExpression(node, candidatesOutArray, checkMode);
            case SyntaxKind.NewExpression:
                return ResolveNewExpression(node, candidatesOutArray, checkMode);
            case SyntaxKind.TaggedTemplateExpression:
                return ResolveTaggedTemplateExpression(node, candidatesOutArray, checkMode);
            case SyntaxKind.Decorator:
                return ResolveDecorator(node, candidatesOutArray, checkMode);
            case SyntaxKind.JsxOpeningFragment:
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
                return ResolveJsxOpeningLikeElement(node, candidatesOutArray, checkMode);
            case SyntaxKind.BinaryExpression:
                return ResolveInstanceofExpression(node, candidatesOutArray, checkMode);
        }
        throw new global::System.InvalidOperationException("Unhandled case in resolveSignature");
    }

    // checker.go:8447
    private Signature ResolveCallExpression(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        if (node.Expression()!.Kind == SyntaxKind.SuperKeyword)
        {
            var superType = CheckSuperExpression(node.Expression()!);
            if (IsTypeAny(superType))
            {
                foreach (var arg in node.Arguments()!)
                {
                    // Still visit arguments so they get marked for visibility, etc
                    CheckExpression(arg);
                }
                return anySignature!;
            }
            if (!IsErrorType(superType))
            {
                // In super call, the candidate signatures are the matching arity signatures of the base
                // constructor function instantiated with the type arguments specified in the extends clause.
                var baseTypeNode = GetExtendsHeritageClauseElement(AstUtilities.GetContainingClass(node)!);
                if (baseTypeNode != null)
                {
                    var baseConstructors = GetInstantiatedConstructorsForTypeArguments(superType, baseTypeNode.TypeArguments(), baseTypeNode);
                    return ResolveCall(node, baseConstructors, candidatesOutArray, checkMode, SignatureFlags.None, null);
                }
            }
            return ResolveUntypedCall(node);
        }
        if (IsImportCall(node))
        {
            return ResolveUntypedCall(node);
        }
        SignatureFlags callChainFlags;
        var funcType = CheckExpression(node.Expression()!);
        if (IsCallChain(node))
        {
            var nonOptionalType = GetOptionalExpressionType(funcType, node.Expression()!);
            if (nonOptionalType == funcType)
            {
                callChainFlags = SignatureFlags.None;
            }
            else if (AstUtilities.IsOutermostOptionalChain(node))
            {
                callChainFlags = SignatureFlags.IsOuterCallChain;
            }
            else
            {
                callChainFlags = SignatureFlags.IsInnerCallChain;
            }
            funcType = nonOptionalType;
        }
        else
        {
            callChainFlags = SignatureFlags.None;
        }
        funcType = CheckNonNullTypeWithReporter(funcType, node.Expression()!, ReportCannotInvokePossiblyNullOrUndefinedError);
        if (funcType == silentNeverType)
        {
            return silentNeverSignature!;
        }
        var apparentType = GetApparentType(funcType);
        if (IsErrorType(apparentType))
        {
            // Another error has already been reported
            return ResolveErrorCall(node);
        }
        // Technically, this signatures list may be incomplete. We are taking the apparent type,
        // but we are not including call signatures that may have been added to the Object or
        // Function interface, since they have none by default.
        var callSignatures = GetSignaturesOfType(apparentType, SignatureKind.Call);
        var numConstructSignatures = GetSignaturesOfType(apparentType, SignatureKind.Construct).Length;
        // TS 1.0 Spec: 4.12: in an untyped function call no TypeArgs are permitted and the result is Any.
        if (IsUntypedFunctionCall(funcType, apparentType, callSignatures.Length, numConstructSignatures))
        {
            // The unknownType indicates that an error already occurred (and was reported).
            if (!IsErrorType(funcType) && node.TypeArguments() != null)
            {
                Error(node, Diag.Untyped_function_calls_may_not_accept_type_arguments);
            }
            return ResolveUntypedCall(node);
        }
        // If FuncExpr's apparent type is a function type, the call is a typed function call. TypeScript
        // employs overload resolution in typed function calls to support multiple call signatures.
        if (callSignatures.Length == 0)
        {
            if (numConstructSignatures != 0)
            {
                Error(node, Diag.Value_of_type_0_is_not_callable_Did_you_mean_to_include_new, TypeToString(funcType));
            }
            else
            {
                Diagnostic? relatedInformation = null;
                if (node.Arguments().Len() == 1)
                {
                    var text = AstUtilities.GetSourceFileOfNode(node)!.Text();
                    var options = new global::Ts.Scanner.Scanner.SkipTriviaOptions { StopAfterLineBreak = true };
                    if (global::Ts.StringUtil.StringUtil.IsLineBreak(text[global::Ts.Scanner.Scanner.SkipTriviaEx(text, node.Expression()!.End, options) - 1]))
                    {
                        relatedInformation = CreateDiagnosticForNode(node.Expression()!, Diag.Are_you_missing_a_semicolon);
                    }
                }
                InvocationError(node.Expression()!, apparentType, SignatureKind.Call, relatedInformation);
            }
            return ResolveErrorCall(node);
        }
        // When a call to a generic function is an argument to an outer call to a generic function for which
        // inference is in process, we may choose to defer processing. If no type arguments are specified in
        // the inner call and at least one call signature is generic and returns a function type, we defer.
        if ((checkMode & CheckMode.SkipGenericFunctions) != 0 && node.TypeArguments().Len() == 0 && CoreUtil.Some(callSignatures, IsGenericFunctionReturningFunction))
        {
            SkippedGenericFunction(node, checkMode);
            return resolvingSignature!;
        }
        return ResolveCall(node, callSignatures, candidatesOutArray, checkMode, callChainFlags, null);
    }

    // checker.go:8551
    private Signature ResolveNewExpression(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        var expressionType = CheckNonNullExpression(node.Expression()!);
        if (expressionType == silentNeverType)
        {
            return silentNeverSignature!;
        }
        // If expressionType's apparent type is an object type with one or more construct signatures, the
        // expression is processed like a function call but using the construct signatures.
        expressionType = GetApparentType(expressionType);
        if (IsErrorType(expressionType))
        {
            // Another error has already been reported
            return ResolveErrorCall(node);
        }
        // TS 1.0 spec: 4.11: if expressionType is Any, the result of the operation is Any.
        if (IsTypeAny(expressionType))
        {
            if (node.TypeArguments().Len() != 0)
            {
                Error(node, Diag.Untyped_function_calls_may_not_accept_type_arguments);
            }
            return ResolveUntypedCall(node);
        }
        var constructSignatures = GetSignaturesOfType(expressionType, SignatureKind.Construct);
        if (constructSignatures.Length != 0)
        {
            if (!IsConstructorAccessible(node, constructSignatures[0]))
            {
                return ResolveErrorCall(node);
            }
            // If the expression is a class of abstract type, or an abstract construct signature, it cannot
            // be instantiated.
            if (CoreUtil.Some(constructSignatures, static sig => (sig.flags & SignatureFlags.Abstract) != 0))
            {
                Error(node, Diag.Cannot_create_an_instance_of_an_abstract_class);
                return ResolveErrorCall(node);
            }
            if (expressionType.symbol != null)
            {
                var valueDecl = GetClassLikeDeclarationOfSymbol(expressionType.symbol);
                if (valueDecl != null && HasModifier(valueDecl, ModifierFlags.Abstract))
                {
                    Error(node, Diag.Cannot_create_an_instance_of_an_abstract_class);
                    return ResolveErrorCall(node);
                }
            }
            return ResolveCall(node, constructSignatures, candidatesOutArray, checkMode, SignatureFlags.None, null);
        }
        // If expressionType has no construct signatures but one or more call signatures, the expression is
        // processed as a function call. The type of the result of the operation is Any.
        var callSignatures = GetSignaturesOfType(expressionType, SignatureKind.Call);
        if (callSignatures.Length != 0)
        {
            var signature = ResolveCall(node, callSignatures, candidatesOutArray, checkMode, SignatureFlags.None, null);
            if (!noImplicitAny)
            {
                if (signature.declaration != null && GetReturnTypeOfSignature(signature) != voidType)
                {
                    Error(node, Diag.Only_a_void_function_can_be_called_with_the_new_keyword);
                }
                if (GetThisTypeOfSignature(signature) == voidType)
                {
                    Error(node, Diag.A_function_that_is_called_with_the_new_keyword_cannot_have_a_this_type_that_is_void);
                }
            }
            return signature;
        }
        InvocationError(node.Expression()!, expressionType, SignatureKind.Construct, null);
        return ResolveErrorCall(node);
    }

    // checker.go:8819
    internal Signature ResolveCall(Node node, Signature[] signatures, List<Signature>? candidatesOutArray, CheckMode checkMode, SignatureFlags callChainFlags, global::Ts.Diagnostics.Message? headMessage)
    {
        var isTaggedTemplate = node.Kind == SyntaxKind.TaggedTemplateExpression;
        var isDecorator = node.Kind == SyntaxKind.Decorator;
        var isJsxOpeningOrSelfClosingElement = IsJsxOpeningLikeElement(node);
        var isInstanceof = node.Kind == SyntaxKind.BinaryExpression;
        var reportErrors = !isInferencePartiallyBlocked && candidatesOutArray == null;
        var s = new CallState();
        s.node = node;
        if (!isDecorator && !isInstanceof && !IsSuperCall(node) && !NodeTests.IsJsxOpeningFragment(node))
        {
            s.typeArguments = node.TypeArguments();
            // We already perform checking on the type arguments on the class declaration itself.
            if (isTaggedTemplate || isJsxOpeningOrSelfClosingElement || node.Expression()!.Kind != SyntaxKind.SuperKeyword)
            {
                CheckSourceElements(s.typeArguments!);
            }
        }
        s.candidates = ReorderCandidates(signatures, callChainFlags);
        if (candidatesOutArray != null)
        {
            candidatesOutArray.Clear();
            candidatesOutArray.AddRange(s.candidates);
        }

        if (s.candidates.Length == 0)
        {
            // In Strada we would error here, but no known repro doesn't have at least one other error in
            // this codepath. Just return instead. See #54442
            return unknownSignature!;
        }

        s.args = GetEffectiveCallArguments(node);
        // The idea is that we perform type argument inference & assignability checking once without using
        // the susceptible parameters that are functions, and once more for those parameters, contextually
        // typing each as we go along.
        s.isSingleNonGenericCandidate = s.candidates.Length == 1 && s.candidates[0].typeParameters.Len() == 0;
        if (!isDecorator && !s.isSingleNonGenericCandidate && CoreUtil.Some(s.args, IsContextSensitive))
        {
            s.argCheckMode = CheckMode.SkipContextSensitive;
        }
        else
        {
            s.argCheckMode = CheckMode.Normal;
        }
        // If we are in signature help, a trailing comma indicates that we intend to provide another argument.
        s.signatureHelpTrailingComma = (checkMode & CheckMode.IsForSignatureHelp) != 0 && NodeTests.IsCallExpression(node) && node.ArgumentList()!.HasTrailingComma();
        // Section 4.12.1: if the candidate list contains one or more signatures for which the type of each
        // argument is a subtype of each parameter, the return type of the first of those becomes the return
        // type of the call. Otherwise the return type of the first signature in the list is used.
        Signature? result = null;
        if (s.candidates.Length > 1)
        {
            result = ChooseOverload(ref s, subtypeRelation!);
        }
        if (result == null)
        {
            result = ChooseOverload(ref s, assignableRelation!);
        }
        if (result != null)
        {
            return result;
        }
        result = GetCandidateForOverloadFailure(s.node, s.candidates, s.args, candidatesOutArray != null, checkMode);
        // Preemptively cache the result; getResolvedSignature will do this after we return, but we need to
        // ensure that the result is present for the error checks below to handle the circularity.
        signatureLinks.Get(node).resolvedSignature = result;
        // No signatures were applicable. Now report errors based on the last applicable signature with no
        // arguments excluded from assignability checks.
        if (reportErrors)
        {
            if (headMessage == null && isInstanceof)
            {
                headMessage = Diag.The_left_hand_side_of_an_instanceof_expression_must_be_assignable_to_the_first_argument_of_the_right_hand_side_s_Symbol_hasInstance_method;
            }
            ReportCallResolutionErrors(node, ref s, signatures, headMessage);
        }
        return result;
    }

    // checker.go:8933
    private Signature[] ReorderCandidates(Signature[] signatures, SignatureFlags callChainFlags)
    {
        Node? lastParent = null;
        Symbol? lastSymbol = null;
        var index = 0;
        var cutoffIndex = 0;
        var spliceIndex = 0;
        var specializedIndex = -1;
        var result = new List<Signature>(signatures.Length);
        foreach (var s in signatures)
        {
            var signature = s;
            Symbol? symbol = null;
            Node? parent = null;
            if (signature.declaration != null)
            {
                symbol = GetSymbolOfDeclaration(signature.declaration);
                parent = signature.declaration.Parent;
            }
            if (lastSymbol == null || symbol == lastSymbol)
            {
                if (lastParent != null && parent == lastParent)
                {
                    index = index + 1;
                }
                else
                {
                    lastParent = parent;
                    index = cutoffIndex;
                }
            }
            else
            {
                // current declaration belongs to a different symbol
                index = result.Count;
                cutoffIndex = result.Count;
                lastParent = parent;
            }
            lastSymbol = symbol;
            // specialized signatures always need to be placed before non-specialized signatures; see GH#1133
            if (SignatureHasLiteralTypes(signature))
            {
                specializedIndex++;
                spliceIndex = specializedIndex;
                cutoffIndex++;
            }
            else
            {
                spliceIndex = index;
            }
            if (callChainFlags != 0)
            {
                signature = GetOptionalCallSignature(signature, callChainFlags);
            }
            result.Insert(spliceIndex, signature);
        }
        return result.ToArray();
    }

    // checker.go:8983
    private static bool SignatureHasLiteralTypes(Signature s) => (s.flags & SignatureFlags.HasLiteralTypes) != 0;

    // checker.go:8987
    private Signature GetOptionalCallSignature(Signature signature, SignatureFlags callChainFlags)
    {
        if ((signature.flags & SignatureFlags.CallChainFlags) == callChainFlags)
        {
            return signature;
        }
        var key = new CachedSignatureKey(signature, callChainFlags == SignatureFlags.IsInnerCallChain ? SignatureKeys.Inner : SignatureKeys.Outer);
        if (cachedSignatures.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var result = CloneSignature(signature);
        result.flags |= callChainFlags;
        cachedSignatures[key] = result;
        return result;
    }

    // checker.go:9001
    private Signature? ChooseOverload(ref CallState s, Relation relation)
    {
        s.candidatesForArgumentError = null;
        s.candidateForArgumentArityError = null;
        s.candidateForTypeArgumentError = null;
        if (s.isSingleNonGenericCandidate)
        {
            var candidate = s.candidates[0];
            if (s.typeArguments.Len() != 0 || !HasCorrectArity(s.node, s.args, candidate, s.signatureHelpTrailingComma))
            {
                return null;
            }
            if (!IsSignatureApplicable(s.node, s.args, candidate, relation, CheckMode.Normal, false /*reportErrors*/, null /*diagnosticOutput*/))
            {
                s.candidatesForArgumentError = new List<Signature> { candidate };
                return null;
            }
            return candidate;
        }
        for (var candidateIndex = 0; candidateIndex < s.candidates.Length; candidateIndex++)
        {
            var candidate = s.candidates[candidateIndex];
            if (!HasCorrectTypeArgumentArity(candidate, s.typeArguments) || !HasCorrectArity(s.node, s.args, candidate, s.signatureHelpTrailingComma))
            {
                continue;
            }
            Signature checkCandidate;
            InferenceContext? inferenceContext = null;
            if (candidate.typeParameters.Len() != 0)
            {
                Type[]? typeArgumentTypes;
                if (s.typeArguments.Len() != 0)
                {
                    typeArgumentTypes = CheckTypeArguments(candidate, s.typeArguments!, false /*reportErrors*/, null);
                    if (typeArgumentTypes == null)
                    {
                        s.candidateForTypeArgumentError = candidate;
                        continue;
                    }
                }
                else
                {
                    inferenceContext = NewInferenceContext(candidate.typeParameters, candidate, AstUtilities.IsInJSFile(s.node) ? InferenceFlags.AnyDefault : InferenceFlags.None /*flags*/, null);
                    typeArgumentTypes = InferTypeArguments(s.node, candidate, s.args, s.argCheckMode | CheckMode.SkipGenericFunctions, inferenceContext);
                    if ((inferenceContext.flags & InferenceFlags.SkippedGenericFunction) != 0)
                    {
                        s.argCheckMode |= CheckMode.SkipGenericFunctions;
                    }
                }
                Type[]? inferredTypeParameters = inferenceContext?.inferredTypeParameters;
                checkCandidate = GetSignatureInstantiation(candidate, typeArgumentTypes, AstUtilities.IsInJSFile(candidate.declaration), inferredTypeParameters);
                // If the original signature has a generic rest type, instantiation may produce a signature
                // with different arity and we need to perform another arity check.
                if (GetNonArrayRestType(candidate) != null && !HasCorrectArity(s.node, s.args, checkCandidate, s.signatureHelpTrailingComma))
                {
                    s.candidateForArgumentArityError = checkCandidate;
                    continue;
                }
            }
            else
            {
                checkCandidate = candidate;
            }
            if (!IsSignatureApplicable(s.node, s.args, checkCandidate, relation, s.argCheckMode, false /*reportErrors*/, null /*diagnosticOutput*/))
            {
                (s.candidatesForArgumentError ??= new List<Signature>()).Add(checkCandidate);
                continue;
            }
            if (s.argCheckMode != 0)
            {
                // If one or more context sensitive arguments were excluded, we start including them now and
                // perform a second round of type inference and applicability checking for this candidate.
                s.argCheckMode = CheckMode.Normal;
                if (inferenceContext != null)
                {
                    var typeArgumentTypes = InferTypeArguments(s.node, candidate, s.args, s.argCheckMode, inferenceContext);
                    checkCandidate = GetSignatureInstantiation(candidate, typeArgumentTypes, AstUtilities.IsInJSFile(candidate.declaration), inferenceContext.inferredTypeParameters);
                    if (GetNonArrayRestType(candidate) != null && !HasCorrectArity(s.node, s.args, checkCandidate, s.signatureHelpTrailingComma))
                    {
                        s.candidateForArgumentArityError = checkCandidate;
                        continue;
                    }
                }
                if (!IsSignatureApplicable(s.node, s.args, checkCandidate, relation, s.argCheckMode, false /*reportErrors*/, null /*diagnosticOutput*/))
                {
                    (s.candidatesForArgumentError ??= new List<Signature>()).Add(checkCandidate);
                    continue;
                }
            }
            s.candidates[candidateIndex] = checkCandidate;
            return checkCandidate;
        }
        return null;
    }

    // checker.go:9083
    private bool HasCorrectArity(Node node, Node[] args, Signature signature, bool signatureHelpTrailingComma)
    {
        if (NodeTests.IsJsxOpeningFragment(node))
        {
            return true;
        }
        int argCount;
        var callIsIncomplete = false;
        // In incomplete call we want to be lenient when we have too few arguments
        var effectiveParameterCount = GetParameterCount(signature);
        var effectiveMinimumArguments = GetMinArgumentCount(signature);
        if (NodeTests.IsTaggedTemplateExpression(node))
        {
            argCount = args.Length;
            var template = node.AsTaggedTemplateExpression().Template;
            if (NodeTests.IsTemplateExpression(template!))
            {
                // If a tagged template expression lacks a tail literal, the call is incomplete.
                var lastSpan = CoreUtil.LastOrNil(template!.AsTemplateExpression().TemplateSpans!.Nodes);
                callIsIncomplete = AstUtilities.NodeIsMissing(lastSpan!.AsTemplateSpan().Literal!) || global::Ts.Printer.PrinterUtilities.IsUnterminatedLiteral(lastSpan.AsTemplateSpan().Literal!);
            }
            else
            {
                callIsIncomplete = global::Ts.Printer.PrinterUtilities.IsUnterminatedLiteral(template!);
            }
        }
        else if (NodeTests.IsDecorator(node))
        {
            argCount = GetDecoratorArgumentCount(node, signature);
        }
        else if (NodeTests.IsBinaryExpression(node))
        {
            argCount = 1;
        }
        else if (IsJsxOpeningLikeElement(node))
        {
            callIsIncomplete = node.Attributes()!.End == node.End;
            if (callIsIncomplete)
            {
                return true;
            }
            argCount = effectiveMinimumArguments == 0 ? args.Length : 1;
            effectiveParameterCount = args.Length == 0 ? effectiveParameterCount : 1; // class may have argumentless ctor functions
            effectiveMinimumArguments = global::System.Math.Min(effectiveMinimumArguments, 1); // sfc may specify context argument
        }
        else if (NodeTests.IsNewExpression(node) && node.ArgumentList() == null)
        {
            // This only happens when we have something of the form: 'new C'
            return GetMinArgumentCount(signature) == 0;
        }
        else
        {
            if (signatureHelpTrailingComma)
            {
                argCount = args.Length + 1;
            }
            else
            {
                argCount = args.Length;
            }
            // If we are missing the close parenthesis, the call is incomplete.
            callIsIncomplete = node.ArgumentList()!.End == node.End;
            // If a spread argument is present, check that it corresponds to a rest parameter or at least that
            // it's in the valid range.
            var spreadArgIndex = GetSpreadArgumentIndex(args);
            if (spreadArgIndex >= 0)
            {
                return spreadArgIndex >= GetMinArgumentCount(signature) && (HasEffectiveRestParameter(signature) || spreadArgIndex < GetParameterCount(signature));
            }
        }
        // Too many arguments implies incorrect arity.
        if (!HasEffectiveRestParameter(signature) && argCount > effectiveParameterCount)
        {
            return false;
        }
        // If the call is incomplete, we should skip the lower bound check.
        if (callIsIncomplete || argCount >= effectiveMinimumArguments)
        {
            return true;
        }
        for (var i = argCount; i < effectiveMinimumArguments; i++)
        {
            var t = GetTypeAtPosition(signature, i);
            if ((FilterType(t, AcceptsVoid).flags & TypeFlags.Never) != 0)
            {
                return false;
            }
        }
        return true;
    }

    // checker.go:9155
    private static bool AcceptsVoid(Type t) => (t.flags & TypeFlags.Void) != 0;

    // checker.go:9190
    private bool HasCorrectTypeArgumentArity(Signature signature, Node[]? typeArguments)
    {
        // If the user supplied type arguments, but the number of type arguments does not match the declared
        // number of type parameters, the call has an incorrect arity.
        var numTypeParameters = signature.typeParameters.Len();
        var minTypeArgumentCount = GetMinTypeArgumentCount(signature.typeParameters);
        return typeArguments.Len() == 0 || typeArguments.Len() >= minTypeArgumentCount && typeArguments.Len() <= numTypeParameters;
    }

    // checker.go:9198
    private Type[]? CheckTypeArguments(Signature signature, Node[] typeArgumentNodes, bool reportErrors, global::Ts.Diagnostics.Message? headMessage)
    {
        var isJavaScript = AstUtilities.IsInJSFile(signature.declaration);
        var typeParameters = signature.typeParameters;
        var typeArgumentTypes = FillMissingTypeArguments(CoreUtil.Map(typeArgumentNodes, GetTypeFromTypeNode), typeParameters, GetMinTypeArgumentCount(typeParameters), isJavaScript);
        TypeMapper? mapper = null;
        for (var i = 0; i < typeArgumentNodes.Length; i++)
        {
            global::Ts.Core.Debug.Assert(typeParameters![i] != null, "Should not call checkTypeArguments with too many type arguments");
            var constraint = GetConstraintOfTypeParameter(typeParameters[i]);
            if (constraint != null)
            {
                var typeArgumentHeadMessage = headMessage ?? Diag.Type_0_does_not_satisfy_the_constraint_1;
                mapper ??= TypeMapper.NewTypeMapper(typeParameters!, typeArgumentTypes!);
                var typeArgument = typeArgumentTypes![i];
                Node? errorNode = null;
                if (reportErrors)
                {
                    errorNode = typeArgumentNodes[i];
                }
                var diags = new List<Diagnostic>();
                if (!CheckTypeAssignableToEx(typeArgument, GetTypeWithThisArgument(InstantiateType(constraint, mapper), typeArgument, false), errorNode, typeArgumentHeadMessage, diags))
                {
                    if (diags.Count != 0)
                    {
                        var diagnostic = diags[0];
                        if (headMessage != null)
                        {
                            diagnostic = Diagnostic.NewDiagnosticChain(diagnostic, Diag.Type_0_does_not_satisfy_the_constraint_1);
                        }
                        AddDiagnostic(diagnostic);
                    }
                    return null;
                }
            }
        }
        return typeArgumentTypes;
    }

    // checker.go:9997
    private bool IsGenericFunctionReturningFunction(Signature signature)
    {
        return signature.typeParameters.Len() != 0 && IsFunctionType(GetReturnTypeOfSignature(signature));
    }

    // checker.go:10001
    private void SkippedGenericFunction(Node node, CheckMode checkMode)
    {
        if ((checkMode & CheckMode.Inferential) != 0)
        {
            // We have skipped a generic function during inferential typing. Indicate this so a second pass
            // of inference is needed.
            var context = GetInferenceContext(node);
            context.flags |= InferenceFlags.SkippedGenericFunction;
        }
    }
}
