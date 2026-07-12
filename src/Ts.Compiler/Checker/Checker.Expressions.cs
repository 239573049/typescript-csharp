// Ported from tsgo internal/checker/checker.go (section: expressions), lines ~7494-12292.
// plan/03 §4.2 #7. Faithful per-function translation: same function order, same branch structure,
// Go comments and `// !!!` markers preserved.
//
// Breadth-first bring-up (plan mandate: dispatch trunk + literals + identifier/property-access core;
// complex leaves stubbed and annotated). This file carries the checkExpression* dispatch trunk
// (checkExpressionCachedEx 7494 → checkExpressionEx → checkExpressionWorker 7708 big switch), the
// inline literal cases (string/number/bigint/true/false/null — the smoke-testable currency), and
// getResolvedSymbol's public entry (CheckExpression). The large leaf checkers (identifier,
// property/element access, call/new overload resolution, object/array literals, templates,
// decorators, JSX, contextual typing) are stubbed in Checker.Expressions.Deps.cs pending their own
// breadth passes; the dispatch switch routes to those stubs so the assembly compiles and simple
// literal expressions type-check for real.
//
// §1.5: cross-package classes shadowed by sibling namespaces are `global::`-qualified; the
// no-collision hosts (NodeTests/AstUtilities/CoreUtil/Scanner) are used directly, as in flow/relater.
//
// R05 (深递归): the expression tree descends via checkExpressionEx with no trampoline (faithful to
// tsgo); deep left-associative chains rely on the 64MB LargeStackExecutor stack (AGENTS.md §4 铁律8).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:7461
    internal Type CheckExpressionWithContextualType(Node node, Type contextualType, InferenceContext? inferenceContext, CheckMode checkMode)
    {
        var contextNode = GetContextNode(node);
        PushContextualType(contextNode, contextualType, false /*isCache*/);
        PushInferenceContext(contextNode, inferenceContext);
        var t = CheckExpressionEx(node, checkMode | CheckMode.Contextual | CoreUtil.IfElse(inferenceContext is not null, CheckMode.Inferential, CheckMode.Normal));
        // In CheckMode.Inferential we collect intra-expression inference sites to process before fixing any type
        // parameters. This information is no longer needed after the call to checkExpression.
        if (inferenceContext is not null && inferenceContext.intraExpressionInferenceSites is not null)
        {
            inferenceContext.intraExpressionInferenceSites = null;
        }
        // We strip literal freshness when an appropriate contextual type is present such that contextually typed
        // literals always preserve their literal types (otherwise they might widen during type inference). An alternative
        // here would be to not mark contextually typed literals as fresh in the first place.
        if (MaybeTypeOfKind(t, TypeFlags.Literal) && IsLiteralOfContextualType(t, InstantiateContextualType(contextualType, node, ContextFlags.None)))
        {
            t = GetRegularTypeOfLiteralType(t);
        }
        PopInferenceContext();
        PopContextualType();
        return t;
    }

    // checker.go:7482
    internal Node GetContextNode(Node node)
    {
        if (NodeTests.IsJsxAttributes(node) && !NodeTests.IsJsxSelfClosingElement(node.Parent!))
        {
            // Needs to be the root JsxElement, so it encompasses the attributes _and_ the children (which are essentially part of the attributes)
            return node.Parent!.Parent!;
        }
        return node;
    }

    // checker.go:7494-7517
    internal Type CheckExpressionCachedEx(Node node, CheckMode checkMode)
    {
        if (checkMode != CheckMode.Normal)
        {
            return CheckExpressionEx(node, checkMode);
        }
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType == null)
        {
            // When computing a type that we're going to cache, we need to ignore any ongoing control flow
            // analysis because variables may have transient types in indeterminable states. Moving flowLoopStart
            // to the top of the stack ensures all transient types are computed from a known point.
            var saveFlowLoopStack = flowLoopStack;
            var saveFlowTypeCache = flowTypeCache;
            flowLoopStack = null!;
            flowTypeCache = null!;
            links.resolvedType = CheckExpressionEx(node, checkMode);
            flowTypeCache = saveFlowTypeCache;
            flowLoopStack = saveFlowLoopStack;
        }
        return links.resolvedType!;
    }

    // Returns the type of an expression. Unlike checkExpression, this function is simply concerned
    // with computing the type and may not fully check all contained sub-expressions for errors.
    // checker.go:7519-7528
    internal Type GetContextFreeTypeOfExpressionCore(Node node)
    {
        if (contextFreeTypes.TryGetValue(node, out var cached) && cached != null)
        {
            return cached;
        }
        PushContextualType(node, anyType!, false /*isCache*/);
        var t = CheckExpressionEx(node, CheckMode.SkipContextSensitive);
        contextFreeTypes[node] = t;
        PopContextualType();
        return t;
    }

    // checker.go:7530-7532
    internal Type CheckExpressionCore(Node node)
    {
        return CheckExpressionEx(node, CheckMode.Normal);
    }

    // checker.go:7534-7548
    internal Type CheckExpressionEx(Node node, CheckMode checkMode)
    {
        // !!! tracing (tr.Push): omitted (tracer wiring lands with the tracing wave).
        var saveCurrentNode = currentNode;
        currentNode = node;
        instantiationCount = 0;
        var uninstantiatedType = CheckExpressionWorker(node, checkMode);
        var t = InstantiateTypeWithSingleGenericCallSignature(node, uninstantiatedType, checkMode);
        if (IsConstEnumObjectType(t))
        {
            CheckConstEnumAccess(node, t);
        }
        currentNode = saveCurrentNode;
        return t;
    }

    // checker.go:10019
    internal Type CheckParenthesizedExpression(Node node, CheckMode checkMode)
    {
        return CheckExpressionEx(node.Expression()!, checkMode);
    }

    // checker.go:7708-7812
    internal Type CheckExpressionWorker(Node node, CheckMode checkMode)
    {
        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
                return CheckIdentifier(node, checkMode);
            case SyntaxKind.PrivateIdentifier:
                return CheckPrivateIdentifierExpression(node);
            case SyntaxKind.ThisKeyword:
                return CheckThisExpression(node);
            case SyntaxKind.SuperKeyword:
                return CheckSuperExpression(node);
            case SyntaxKind.NullKeyword:
                return nullWideningType!;
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
                if (IsSkipDirectInferenceNode(node))
                {
                    return blockedStringType!;
                }
                return GetFreshTypeOfLiteralType(GetStringLiteralType(node.Text()));
            case SyntaxKind.NumericLiteral:
                CheckGrammarNumericLiteral(node.AsNumericLiteral());
                return GetFreshTypeOfLiteralType(GetNumberLiteralType(Ts.JsNum.JsNum.FromString(node.Text())));
            case SyntaxKind.BigIntLiteral:
                CheckGrammarBigIntLiteral(node.AsBigIntLiteral());
                return GetFreshTypeOfLiteralType(GetBigIntLiteralType(new Ts.JsNum.PseudoBigInt(Ts.JsNum.JsNum.ParsePseudoBigInt(node.Text()), false /*negative*/)));
            case SyntaxKind.TrueKeyword:
                return trueType!;
            case SyntaxKind.FalseKeyword:
                return falseType!;
            case SyntaxKind.TemplateExpression:
                return CheckTemplateExpression(node);
            case SyntaxKind.RegularExpressionLiteral:
                return CheckRegularExpressionLiteral(node);
            case SyntaxKind.ArrayLiteralExpression:
                return CheckArrayLiteral(node, checkMode);
            case SyntaxKind.ObjectLiteralExpression:
                return CheckObjectLiteral(node, checkMode);
            case SyntaxKind.PropertyAccessExpression:
                return CheckPropertyAccessExpression(node, checkMode, false /*writeOnly*/);
            case SyntaxKind.QualifiedName:
                return CheckQualifiedName(node, checkMode);
            case SyntaxKind.ElementAccessExpression:
                return CheckIndexedAccess(node, checkMode);
            case SyntaxKind.CallExpression:
                if (IsImportCall(node))
                {
                    return CheckImportCallExpression(node);
                }
                return CheckCallExpression(node, checkMode);
            case SyntaxKind.NewExpression:
                return CheckCallExpression(node, checkMode);
            case SyntaxKind.TaggedTemplateExpression:
                return CheckTaggedTemplateExpression(node);
            case SyntaxKind.ParenthesizedExpression:
                return CheckParenthesizedExpression(node, checkMode);
            case SyntaxKind.ClassExpression:
                return CheckClassExpression(node);
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                return CheckFunctionExpressionOrObjectLiteralMethod(node, checkMode);
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.AsExpression:
                return CheckAssertion(node, checkMode);
            case SyntaxKind.TypeOfExpression:
                return CheckTypeOfExpression(node);
            case SyntaxKind.NonNullExpression:
                return CheckNonNullAssertion(node);
            case SyntaxKind.ExpressionWithTypeArguments:
                return CheckExpressionWithTypeArguments(node);
            case SyntaxKind.SatisfiesExpression:
                return CheckSatisfiesExpression(node);
            case SyntaxKind.MetaProperty:
                return CheckMetaProperty(node);
            case SyntaxKind.DeleteExpression:
                return CheckDeleteExpression(node);
            case SyntaxKind.VoidExpression:
                return CheckVoidExpression(node);
            case SyntaxKind.AwaitExpression:
                return CheckAwaitExpression(node);
            case SyntaxKind.PrefixUnaryExpression:
                return CheckPrefixUnaryExpression(node);
            case SyntaxKind.PostfixUnaryExpression:
                return CheckPostfixUnaryExpression(node);
            case SyntaxKind.BinaryExpression:
                return CheckBinaryExpressionCore(node, checkMode);
            case SyntaxKind.ConditionalExpression:
                return CheckConditionalExpression(node, checkMode);
            case SyntaxKind.SpreadElement:
                return CheckSpreadExpression(node, checkMode);
            case SyntaxKind.OmittedExpression:
                return undefinedWideningType!;
            case SyntaxKind.YieldExpression:
                return CheckYieldExpression(node);
            case SyntaxKind.SyntheticExpression:
                return CheckSyntheticExpression(node);
            case SyntaxKind.JsxExpression:
                return CheckJsxExpression(node, checkMode);
            case SyntaxKind.JsxElement:
                return CheckJsxElement(node, checkMode);
            case SyntaxKind.JsxSelfClosingElement:
                return CheckJsxSelfClosingElement(node, checkMode);
            case SyntaxKind.JsxFragment:
                return CheckJsxFragment(node);
            case SyntaxKind.JsxAttributes:
                return CheckJsxAttributes(node, checkMode);
            case SyntaxKind.JsxOpeningElement:
                throw new InvalidOperationException("Should never directly check a JsxOpeningElement");
        }
        return errorType!;
    }

    // checker.go:10090
    internal Type CheckFunctionExpressionOrObjectLiteralMethod(Node node, CheckMode checkMode)
    {
        CheckNodeDeferred(node);
        if (NodeTests.IsFunctionExpression(node))
        {
            CheckCollisionsForDeclarationName(node, node.Name());
        }
        if ((checkMode & CheckMode.SkipContextSensitive) != 0 && IsContextSensitive(node))
        {
            // Skip parameters, return signature with return type that retains noncontextual parts so inferences can still be drawn in an early stage
            if (node.Type() == null && !AstUtilities.HasContextSensitiveParameters(node))
            {
                // Return plain anyFunctionType if there is no possibility we'll make inferences from the return type
                var contextualSignature = GetContextualSignature(node);
                if (contextualSignature is not null && couldContainTypeVariables(GetReturnTypeOfSignature(contextualSignature)))
                {
                    if (contextFreeTypes.TryGetValue(node, out var cached) && cached is not null)
                    {
                        return cached;
                    }
                    var returnType = GetReturnTypeFromBody(node, checkMode);
                    var returnOnlySignature = NewSignature(SignatureFlags.IsNonInferrable, null, null /*typeParameters*/, null /*thisParameter*/, null, returnType, null /*resolvedTypePredicate*/, 0);
                    var returnOnlyType = NewAnonymousType(node.Symbol(), null, [returnOnlySignature], null, null);
                    returnOnlyType.objectFlags |= ObjectFlags.NonInferrableType;
                    contextFreeTypes[node] = returnOnlyType;
                    return returnOnlyType;
                }
            }
            return anyFunctionType!;
        }
        // Grammar checking
        var hasGrammarError = CheckGrammarFunctionLikeDeclaration(node);
        if (!hasGrammarError && NodeTests.IsFunctionExpression(node))
        {
            CheckGrammarForGenerator(node);
        }
        if (node.FunctionLikeData()!.FullSignature != null)
        {
            if (GetContextualCallSignature(GetTypeFromTypeNode(node.FunctionLikeData()!.FullSignature!), node) == null)
            {
                Error(node.FunctionLikeData()!.FullSignature, Diag.A_JSDoc_type_tag_on_a_function_must_have_a_signature_with_the_correct_number_of_arguments);
            }
        }
        ContextuallyCheckFunctionExpressionOrObjectLiteralMethod(node, checkMode);
        return GetTypeOfSymbol(GetSymbolOfDeclaration(node)!);
    }

    // checker.go:10128
    internal void ContextuallyCheckFunctionExpressionOrObjectLiteralMethod(Node node, CheckMode checkMode)
    {
        var links = nodeLinks.Get(node);
        // Check if function expression is contextually typed and assign parameter types if so.
        if ((links.flags & NodeCheckFlags.ContextChecked) == 0)
        {
            var contextualSignature = GetContextualSignature(node);
            // If a type check is started at a function expression that is an argument of a function call, obtaining the
            // contextual type may recursively get back to here during overload resolution of the call. If so, we will have
            // already assigned contextual types.
            if ((links.flags & NodeCheckFlags.ContextChecked) == 0)
            {
                links.flags |= NodeCheckFlags.ContextChecked;
                var signature = CoreUtil.FirstOrNil(GetSignaturesOfType(GetTypeOfSymbol(GetSymbolOfDeclaration(node)!), SignatureKind.Call));
                if (signature is null)
                {
                    return;
                }
                if (IsContextSensitive(node))
                {
                    if (contextualSignature is not null)
                    {
                        var inferenceContext = GetInferenceContext(node);
                        Signature? instantiatedContextualSignature = null;
                        if ((checkMode & CheckMode.Inferential) != 0)
                        {
                            InferFromAnnotatedParametersAndReturn(signature, contextualSignature, inferenceContext!);
                            var restType = GetEffectiveRestType(contextualSignature);
                            if (restType is not null && (restType.flags & TypeFlags.TypeParameter) != 0)
                            {
                                instantiatedContextualSignature = InstantiateSignature(contextualSignature, inferenceContext!.nonFixingMapper!);
                            }
                        }
                        if (instantiatedContextualSignature is null)
                        {
                            if (inferenceContext is not null)
                            {
                                instantiatedContextualSignature = InstantiateSignature(contextualSignature, inferenceContext.mapper!);
                            }
                            else
                            {
                                instantiatedContextualSignature = contextualSignature;
                            }
                        }
                        AssignContextualParameterTypes(signature, instantiatedContextualSignature);
                    }
                    else
                    {
                        // Force resolution of all parameter types such that the absence of a contextual type is consistently reflected.
                        AssignNonContextualParameterTypes(signature);
                    }
                }
                else if (contextualSignature is not null && node.TypeParameters() == null && (contextualSignature.parameters ?? []).Length > (node.Parameters() ?? []).Length)
                {
                    var inferenceContext = GetInferenceContext(node);
                    if ((checkMode & CheckMode.Inferential) != 0)
                    {
                        InferFromAnnotatedParametersAndReturn(signature, contextualSignature, inferenceContext!);
                    }
                }
                if (contextualSignature is not null && GetReturnTypeFromAnnotation(node) is null && signature.resolvedReturnType is null)
                {
                    var returnType = GetReturnTypeFromBody(node, checkMode);
                    if (signature.resolvedReturnType is null)
                    {
                        signature.resolvedReturnType = returnType;
                    }
                }
                CheckSignatureDeclaration(node);
            }
        }
    }

    // checker.go:10182
    internal void CheckFunctionExpressionOrObjectLiteralMethodDeferred(Node node)
    {
        var functionFlags = AstUtilities.GetFunctionFlags(node);
        var returnType = GetReturnTypeFromAnnotation(node);
        CheckAllCodePathsInNonVoidFunctionReturnOrThrow(node, returnType);
        var body = node.Body();
        if (body is not null)
        {
            if (node.Type() == null)
            {
                // There are some checks that are only performed in getReturnTypeFromBody, that may produce errors
                // we need. An example is the noImplicitAny errors resulting from widening the return expression
                // of a function. Because checking of function expression bodies is deferred, there was never an
                // appropriate time to do this during the main walk of the file (see the comment at the top of
                // checkFunctionExpressionBodies). So it must be done now.
                GetReturnTypeOfSignature(GetSignatureFromDeclaration(node));
            }
            if (NodeTests.IsBlock(body))
            {
                CheckSourceElement(body);
            }
            else
            {
                // From within an async function you can return either a non-promise value or a promise. Any
                // Promise/A+ compatible implementation will always assimilate any foreign promise, so we
                // should not be checking assignability of a promise to the return type. Instead, we need to
                // check assignability of the awaited type of the expression body against the promised type of
                // its return type annotation.
                var exprType = CheckExpression(body);
                if (returnType is not null)
                {
                    var returnOrPromisedType = UnwrapReturnType(returnType, functionFlags);
                    if (returnOrPromisedType is not null)
                    {
                        CheckReturnExpression(node, returnOrPromisedType, body, body, exprType, false);
                    }
                }
            }
        }
    }

    // checker.go:10216
    internal void InferFromAnnotatedParametersAndReturn(Signature sig, Signature context, InferenceContext inferenceContext)
    {
        var length = (sig.parameters ?? []).Length - CoreUtil.IfElse(SignatureHasRestParameter(sig), 1, 0);
        for (var i = 0; i < length; i++)
        {
            var declaration = sig.parameters![i].ValueDeclaration!;
            var typeNode = declaration.Type();
            if (typeNode is not null)
            {
                var source = AddOptionalityEx(GetTypeFromTypeNode(typeNode), false /*isProperty*/, IsOptionalDeclaration(declaration));
                var target = GetTypeAtPosition(context, i);
                InferTypes(inferenceContext.inferences, source, target, InferencePriority.None, false);
            }
        }
        var sigDeclaration = sig.Declaration();
        if (sigDeclaration is not null)
        {
            var returnTypeNode = sigDeclaration.Type();
            if (returnTypeNode is not null)
            {
                var source = GetTypeFromTypeNode(returnTypeNode);
                var target = GetReturnTypeOfSignature(context);
                InferTypes(inferenceContext.inferences, source, target, InferencePriority.None, false);
            }
        }
    }

    // checker.go:10325
    internal void AssignContextualParameterTypes(Signature sig, Signature context)
    {
        if ((context.typeParameters ?? []).Length != 0)
        {
            if ((sig.typeParameters ?? []).Length != 0)
            {
                // This signature has already has a contextual inference performed and cached on it
                return;
            }
            sig.typeParameters = context.typeParameters;
        }
        if (context.thisParameter is not null)
        {
            var parameter = sig.thisParameter;
            if (parameter is null || parameter.ValueDeclaration is not null && parameter.ValueDeclaration.Type() == null)
            {
                if (parameter is null)
                {
                    sig.thisParameter = CreateSymbolWithType(context.thisParameter, null /*type*/);
                }
                AssignParameterType(sig.thisParameter!, GetTypeOfSymbol(context.thisParameter));
            }
        }
        var length = (sig.parameters ?? []).Length - CoreUtil.IfElse(SignatureHasRestParameter(sig), 1, 0);
        for (var i = 0; i < length; i++)
        {
            var parameter = sig.parameters![i];
            var declaration = parameter.ValueDeclaration!;
            if (declaration.Type() == null)
            {
                var t = TryGetTypeAtPosition(context, i);
                if (t is not null && declaration.Initializer() is not null)
                {
                    var initializerType = CheckDeclarationInitializer(declaration, CheckMode.Normal, null);
                    if (!IsTypeAssignableTo(initializerType, t))
                    {
                        initializerType = WidenTypeInferredFromInitializer(declaration, initializerType);
                        if (IsTypeAssignableTo(t, initializerType))
                        {
                            t = initializerType;
                        }
                    }
                }
                AssignParameterType(parameter, t);
            }
        }
        if (SignatureHasRestParameter(sig))
        {
            // parameter might be a transient symbol generated by use of `arguments` in the function body.
            var parameter = CoreUtil.LastOrNil(sig.parameters)!;
            if (parameter.ValueDeclaration is not null && parameter.ValueDeclaration.Type() == null ||
                parameter.ValueDeclaration is null && (parameter.CheckFlags & CheckFlags.DeferredType) != 0)
            {
                var contextualParameterType = GetRestTypeAtPosition(context, length, false);
                AssignParameterType(parameter, contextualParameterType);
            }
        }
    }

    // checker.go:10371
    internal void AssignNonContextualParameterTypes(Signature signature)
    {
        if (signature.thisParameter is not null)
        {
            AssignParameterType(signature.thisParameter, null);
        }
        foreach (var parameter in signature.parameters ?? [])
        {
            AssignParameterType(parameter, null);
        }
    }

    // checker.go:10380
    internal void AssignParameterType(Symbol parameter, Type? contextualType)
    {
        var links = valueSymbolLinks.Get(parameter);
        if (links.resolvedType is not null)
        {
            return;
        }
        var declaration = parameter.ValueDeclaration;
        var t = contextualType;
        if (t is null)
        {
            if (declaration is not null)
            {
                t = GetWidenedTypeForVariableLikeDeclaration(declaration, true /*reportErrors*/);
            }
            else
            {
                t = GetTypeOfSymbol(parameter);
            }
        }
        links.resolvedType = AddOptionalityEx(t, false, declaration is not null && declaration.Initializer() == null && IsOptionalDeclaration(declaration));
        if (declaration is not null && !NodeTests.IsIdentifier(declaration.Name()!))
        {
            // if inference didn't come up with anything but unknown, fall back to the binding pattern if present.
            if (links.resolvedType == unknownType)
            {
                links.resolvedType = GetTypeFromBindingPattern(declaration.Name()!, false, false);
            }
            AssignBindingElementTypes(declaration.Name()!, links.resolvedType!);
        }
    }

    // When contextual typing assigns a type to a parameter that contains a binding pattern, we also need to push
    // the destructured type into the contained binding elements.
    // checker.go:10406
    internal void AssignBindingElementTypes(Node pattern, Type parentType)
    {
        foreach (var element in pattern.Elements() ?? [])
        {
            var name = element.Name();
            if (name is not null)
            {
                var t = GetBindingElementTypeFromParentType(element, parentType, false /*noTupleBoundsCheck*/);
                if (NodeTests.IsIdentifier(name))
                {
                    valueSymbolLinks.Get(GetSymbolOfDeclaration(element)!).resolvedType = t;
                }
                else
                {
                    AssignBindingElementTypes(name, t);
                }
            }
        }
    }

    // checker.go:10611
    internal Type CheckExpressionWithTypeArguments(Node node)
    {
        CheckGrammarExpressionWithTypeArguments(node);
        CheckSourceElements(node.TypeArguments());
        if (NodeTests.IsExpressionWithTypeArguments(node))
        {
            var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent)!;
            if (NodeTests.IsBinaryExpression(parent) && parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.InstanceOfKeyword && AstUtilities.IsNodeDescendantOf(node, parent.AsBinaryExpression().Right!))
            {
                Error(node, Diag.The_right_hand_side_of_an_instanceof_expression_must_not_be_an_instantiation_expression);
            }
        }
        Type exprType;
        if (NodeTests.IsExpressionWithTypeArguments(node))
        {
            exprType = CheckExpression(node.Expression()!);
        }
        else
        {
            var exprName = node.AsTypeQueryNode().ExprName;
            if (JSDocReparseAstGaps.IsThisIdentifier(exprName))
            {
                exprType = CheckThisExpression(node.AsTypeQueryNode().ExprName!);
            }
            else
            {
                exprType = CheckExpression(node.AsTypeQueryNode().ExprName!);
            }
        }
        return GetInstantiationExpressionType(exprType, node);
    }

    // checker.go:10634
    internal Type GetInstantiationExpressionType(Type exprType, Node node)
    {
        var typeArguments = node.TypeArgumentList();
        if (exprType == silentNeverType || IsErrorType(exprType) || typeArguments is null)
        {
            return exprType;
        }
        var key = new InstantiationExpressionKey(AstUtilities.GetNodeId(node), exprType.id);
        if (instantiationExpressionTypes.TryGetValue(key, out var cached) && cached is not null)
        {
            return cached;
        }
        var hasSomeApplicableSignature = false;
        Type? nonApplicableType = null;
        Signature[]? GetInstantiatedSignatures(Signature[]? signatures)
        {
            var applicableSignatures = CoreUtil.Filter(signatures, sig =>
                (sig.typeParameters ?? []).Length != 0 && HasCorrectTypeArgumentArity(sig, typeArguments.Nodes));
            return CoreUtil.SameMap(applicableSignatures, sig =>
            {
                var typeArgumentTypes = CheckTypeArguments(sig, typeArguments.Nodes!, true /*reportErrors*/, null);
                if (typeArgumentTypes is not null)
                {
                    return GetSignatureInstantiation(sig, typeArgumentTypes, AstUtilities.IsInJSFile(sig.declaration), null);
                }
                return sig;
            });
        }
        Type GetInstantiatedType(Type t)
        {
            var hasSignatures = false;
            var hasApplicableSignature = false;
            Type GetInstantiatedTypePart(Type tPart)
            {
                if ((tPart.flags & TypeFlags.Object) != 0)
                {
                    var resolved = ResolveStructuredTypeMembers(tPart);
                    var resolvedCallSignatures = resolved.CallSignatures();
                    var resolvedConstructSignatures = resolved.ConstructSignatures();
                    var callSignatures = GetInstantiatedSignatures(resolvedCallSignatures);
                    var constructSignatures = GetInstantiatedSignatures(resolvedConstructSignatures);
                    hasSignatures = hasSignatures || resolvedCallSignatures.Length != 0 || resolvedConstructSignatures.Length != 0;
                    hasApplicableSignature = hasApplicableSignature || (callSignatures ?? []).Length != 0 || (constructSignatures ?? []).Length != 0;
                    if (!CoreUtil.Same(callSignatures, resolvedCallSignatures) || !CoreUtil.Same(constructSignatures, resolvedConstructSignatures))
                    {
                        var result = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.InstantiationExpressionType, NewSymbol(SymbolFlags.None, InternalSymbolName.InstantiationExpression));
                        SetStructuredTypeMembers(result, resolved.members, callSignatures, constructSignatures, resolved.indexInfos);
                        result.AsInstantiationExpressionType().node = node;
                        return result;
                    }
                }
                else if ((tPart.flags & TypeFlags.InstantiableNonPrimitive) != 0)
                {
                    var constraint = GetBaseConstraintOfType(tPart);
                    if (constraint is not null)
                    {
                        var instantiated = GetInstantiatedTypePart(constraint);
                        if (instantiated != constraint)
                        {
                            return instantiated;
                        }
                    }
                }
                else if ((tPart.flags & TypeFlags.Union) != 0)
                {
                    return MapType(tPart, GetInstantiatedType);
                }
                else if ((tPart.flags & TypeFlags.Intersection) != 0)
                {
                    return GetIntersectionType(CoreUtil.SameMap(tPart.AsIntersectionType().types, GetInstantiatedTypePart));
                }
                return tPart;
            }
            var partResult = GetInstantiatedTypePart(t);
            hasSomeApplicableSignature = hasSomeApplicableSignature || hasApplicableSignature;
            if (hasSignatures && !hasApplicableSignature)
            {
                nonApplicableType ??= t;
            }
            return partResult;
        }
        var instantiatedResult = GetInstantiatedType(exprType);
        instantiationExpressionTypes[key] = instantiatedResult;
        var errorReportType = hasSomeApplicableSignature ? nonApplicableType : exprType;
        if (errorReportType is not null)
        {
            var sourceFile = AstUtilities.GetSourceFileOfNode(node);
            var loc = new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceFile!.Text(), typeArguments.Pos), typeArguments.End);
            AddDiagnostic(Diagnostic.NewDiagnostic(sourceFile, loc, Diag.Type_0_has_no_signatures_for_which_the_type_argument_list_is_applicable, TypeToString(errorReportType)));
        }
        return instantiatedResult;
    }
}
