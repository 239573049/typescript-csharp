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

    // checker.go:7953
    internal Type CheckTemplateExpression(Node node)
    {
        var expr = node.AsTemplateExpression();
        var length = expr.TemplateSpans!.Nodes!.Length;
        var texts = new string[length + 1];
        var types = new Type[length];
        texts[0] = expr.Head!.Text();
        for (var i = 0; i < length; i++)
        {
            var span = expr.TemplateSpans.Nodes[i];
            var t = CheckExpression(span.Expression()!);
            if (MaybeTypeOfKindConsideringBaseConstraint(t, TypeFlags.ESSymbolLike))
            {
                Error(span.Expression(), Diag.Implicit_conversion_of_a_symbol_to_a_string_will_fail_at_runtime_Consider_wrapping_this_expression_in_String);
            }
            texts[i + 1] = span.AsTemplateSpan().Literal!.Text();
            types[i] = CoreUtil.IfElse(IsTypeAssignableTo(t, templateConstraintType!), t, stringType!);
        }
        object? evaluated = null;
        if (!NodeTests.IsTaggedTemplateExpression(node.Parent!))
        {
            evaluated = evaluate(node, node).Value;
        }
        if (evaluated != null)
        {
            return GetFreshTypeOfLiteralType(GetStringLiteralType((string)evaluated));
        }
        if (IsConstContext(node) || IsTemplateLiteralContext(node) || SomeType(GetContextualType(node, ContextFlags.None) ?? unknownType!, IsTemplateLiteralContextualType))
        {
            return GetTemplateLiteralType(texts, types);
        }
        return stringType!;
    }

    // checker.go:7981
    internal bool IsTemplateLiteralContext(Node node)
    {
        var parent = node.Parent!;
        return NodeTests.IsParenthesizedExpression(parent) && IsTemplateLiteralContext(parent) ||
            NodeTests.IsElementAccessExpression(parent) && parent.AsElementAccessExpression().ArgumentExpression == node;
    }

    // checker.go:7986
    internal bool IsTemplateLiteralContextualType(Type t)
    {
        return (t.flags & (TypeFlags.StringLiteral | TypeFlags.TemplateLiteral)) != 0 ||
            (t.flags & TypeFlags.InstantiableNonPrimitive) != 0 && MaybeTypeOfKind(GetBaseConstraintOfType(t) ?? unknownType!, TypeFlags.StringLike);
    }

    // checker.go:8110
    internal Type CheckIndexedAccess(Node node, CheckMode checkMode)
    {
        if ((node.Flags & NodeFlags.OptionalChain) != 0)
        {
            return CheckElementAccessChain(node, checkMode);
        }
        return CheckElementAccessExpression(node, CheckNonNullExpression(node.Expression()!), checkMode);
    }

    // checker.go:8117
    internal Type CheckElementAccessChain(Node node, CheckMode checkMode)
    {
        var exprType = CheckExpression(node.Expression()!);
        var nonOptionalType = GetOptionalExpressionType(exprType, node.Expression()!);
        return PropagateOptionalTypeMarker(CheckElementAccessExpression(node, CheckNonNullType(nonOptionalType, node.Expression()!), checkMode), node, nonOptionalType != exprType);
    }

    // checker.go:8123
    internal Type CheckElementAccessExpression(Node node, Type exprType, CheckMode checkMode)
    {
        var objectType = exprType;
        if (GetAssignmentTargetKind(node) != AssignmentKind.None || IsMethodAccessForCall(node))
        {
            objectType = GetWidenedType(objectType);
        }
        var indexExpression = node.AsElementAccessExpression().ArgumentExpression!;
        var indexType = CheckExpression(indexExpression);
        if (IsErrorType(objectType) || objectType == silentNeverType)
        {
            return objectType;
        }
        if (IsConstEnumObjectType(objectType) && !AstUtilities.IsStringLiteralLike(indexExpression))
        {
            Error(indexExpression, global::Ts.Diagnostics.Diagnostics.A_const_enum_member_can_only_be_accessed_using_a_string_literal);
            return errorType!;
        }
        var effectiveIndexType = indexType;
        if (IsForInVariableForNumericPropertyNames(indexExpression))
        {
            effectiveIndexType = numberType!;
        }
        var assignmentTargetKind = GetAssignmentTargetKind(node);
        AccessFlags accessFlags;
        if (assignmentTargetKind == AssignmentKind.None)
        {
            accessFlags = AccessFlags.ExpressionPosition;
        }
        else
        {
            accessFlags = AccessFlags.Writing |
                (assignmentTargetKind == AssignmentKind.Compound ? AccessFlags.ExpressionPosition : 0) |
                (IsGenericObjectType(objectType) && !IsThisTypeParameter(objectType) ? AccessFlags.NoIndexSignatures : 0);
        }
        var indexedAccessType = GetIndexedAccessTypeOrUndefined(objectType, effectiveIndexType, accessFlags, node, null) ?? errorType!;
        return CheckIndexedAccessIndexType(GetFlowTypeOfAccessExpression(node, GetResolvedSymbolOrNil(node), indexedAccessType, indexExpression, checkMode), node);
    }

    // Return true if given node is an expression consisting of an identifier (possibly parenthesized)
    // that references a for-in variable for an object with numeric property names.
    // checker.go:8156
    internal bool IsForInVariableForNumericPropertyNames(Node expr)
    {
        var e = AstUtilities.SkipParentheses(expr);
        if (NodeTests.IsIdentifier(e))
        {
            var symbol = GetResolvedSymbol(e);
            if ((symbol.Flags & SymbolFlags.Variable) != 0)
            {
                var child = expr;
                var node = expr.Parent;
                while (node is not null)
                {
                    if (NodeTests.IsForInStatement(node) && child == node.AsForInOrOfStatement().Statement && GetForInVariableSymbol(node) == symbol && HasNumericPropertyNames(GetTypeOfExpression(node.Expression()!)))
                    {
                        return true;
                    }
                    child = node;
                    node = node.Parent;
                }
            }
        }
        return false;
    }

    // Return the symbol of the for-in variable declared or referenced by the given for-in statement.
    // checker.go:8176
    internal Symbol? GetForInVariableSymbol(Node node)
    {
        var initializer = node.Initializer()!;
        if (NodeTests.IsVariableDeclarationList(initializer))
        {
            var declarations = initializer.AsVariableDeclarationList().Declarations!.Nodes;
            if (declarations.Length > 0)
            {
                var variable = declarations[0];
                if (variable is not null && !AstUtilities.IsBindingPattern(variable.Name()!))
                {
                    return GetSymbolOfDeclaration(variable);
                }
            }
        }
        else if (NodeTests.IsIdentifier(initializer))
        {
            return GetResolvedSymbol(initializer);
        }
        return null;
    }

    // Return true if the given type is considered to have numeric property names.
    // checker.go:8193
    internal bool HasNumericPropertyNames(Type t) =>
        GetIndexInfosOfType(t).Length == 1 && GetIndexInfoOfType(t, numberType!) is not null;

    // checker.go:8197
    internal Type CheckIndexedAccessIndexType(Type t, Node accessNode)
    {
        if ((t.flags & TypeFlags.IndexedAccess) == 0)
        {
            return t;
        }
        // Check if the index type is assignable to 'keyof T' for the object type.
        var objectType = t.AsIndexedAccessType().objectType;
        var indexType = t.AsIndexedAccessType().indexType;
        // skip index type deferral on remapping mapped types
        Type objectIndexType;
        if (IsGenericMappedType(objectType) && GetMappedTypeNameTypeKind(objectType) == MappedTypeNameTypeKind.Remapping)
        {
            objectIndexType = GetIndexTypeForMappedType(objectType, IndexFlags.None);
        }
        else
        {
            objectIndexType = GetIndexTypeEx(objectType, IndexFlags.None);
        }
        var hasNumberIndexInfo = GetIndexInfoOfType(objectType, numberType!) is not null;
        if (EveryType(indexType, t2 => IsTypeAssignableTo(t2, objectIndexType) || hasNumberIndexInfo && IsApplicableIndexType(t2, numberType!)))
        {
            if (accessNode.Kind == SyntaxKind.ElementAccessExpression && AstUtilities.IsAssignmentTarget(accessNode) && (objectType.objectFlags & ObjectFlags.Mapped) != 0 && (GetMappedTypeModifiers(objectType) & MappedTypeModifiers.IncludeReadonly) != 0)
            {
                Error(accessNode, global::Ts.Diagnostics.Diagnostics.Index_signature_in_type_0_only_permits_reading, TypeToString(objectType));
            }
            return t;
        }
        if (IsGenericObjectType(objectType))
        {
            var propertyName = GetPropertyNameFromIndex(indexType, accessNode);
            if (propertyName != InternalSymbolName.Missing)
            {
                var propertySymbol = GetConstituentProperty(objectType, propertyName);
                if (propertySymbol is not null && (GetDeclarationModifierFlagsFromSymbol(propertySymbol) & ModifierFlags.NonPublicAccessibilityModifier) != 0)
                {
                    Error(accessNode, global::Ts.Diagnostics.Diagnostics.Private_or_protected_member_0_cannot_be_accessed_on_a_type_parameter, propertyName);
                    return errorType!;
                }
            }
        }
        Error(accessNode, global::Ts.Diagnostics.Diagnostics.Type_0_cannot_be_used_to_index_type_1, TypeToString(indexType), TypeToString(objectType));
        return errorType!;
    }

    // checker.go:8234
    internal Symbol? GetConstituentProperty(Type objectType, string propertyName)
    {
        foreach (var t in GetApparentType(objectType).Distributed())
        {
            var prop = GetPropertyOfType(t, propertyName);
            if (prop is not null)
            {
                return prop;
            }
        }
        return null;
    }

    // checker.go:10829
    internal Type CheckPrefixUnaryExpression(Node node)
    {
        var expr = node.AsPrefixUnaryExpression();
        var operandType = CheckExpression(expr.Operand!);
        if (operandType == silentNeverType)
        {
            return silentNeverType!;
        }
        switch (expr.Operand!.Kind)
        {
            case SyntaxKind.NumericLiteral:
                switch (expr.Operator)
                {
                    case SyntaxKind.MinusToken:
                        return GetFreshTypeOfLiteralType(GetNumberLiteralType(-Ts.JsNum.JsNum.FromString(expr.Operand.Text())));
                    case SyntaxKind.PlusToken:
                        return GetFreshTypeOfLiteralType(GetNumberLiteralType(Ts.JsNum.JsNum.FromString(expr.Operand.Text()) /* unary + is identity */));
                }
                break;
            case SyntaxKind.BigIntLiteral:
                if (expr.Operator == SyntaxKind.MinusToken)
                {
                    return GetFreshTypeOfLiteralType(GetBigIntLiteralType(new Ts.JsNum.PseudoBigInt(Ts.JsNum.JsNum.ParsePseudoBigInt(expr.Operand.Text()), true /*negative*/)));
                }
                break;
        }
        switch (expr.Operator)
        {
            case SyntaxKind.PlusToken:
            case SyntaxKind.MinusToken:
            case SyntaxKind.TildeToken:
                CheckNonNullType(operandType, expr.Operand!);
                if (MaybeTypeOfKindConsideringBaseConstraint(operandType, TypeFlags.ESSymbolLike))
                {
                    Error(expr.Operand, Diag.The_0_operator_cannot_be_applied_to_type_symbol, global::Ts.Scanner.Scanner.TokenToString(expr.Operator));
                }
                if (expr.Operator == SyntaxKind.PlusToken)
                {
                    if (MaybeTypeOfKindConsideringBaseConstraint(operandType, TypeFlags.BigIntLike))
                    {
                        Error(expr.Operand, Diag.Operator_0_cannot_be_applied_to_type_1, global::Ts.Scanner.Scanner.TokenToString(expr.Operator), TypeToString(GetBaseTypeOfLiteralType(operandType)));
                    }
                    return numberType!;
                }
                return GetUnaryResultType(operandType);
            case SyntaxKind.ExclamationToken:
                CheckTruthinessOfType(operandType, expr.Operand!);
                var facts = GetTypeFacts(operandType, TypeFacts.Truthy | TypeFacts.Falsy);
                if (facts == TypeFacts.Truthy)
                {
                    return falseType!;
                }
                if (facts == TypeFacts.Falsy)
                {
                    return trueType!;
                }
                return booleanType!;
            case SyntaxKind.PlusPlusToken:
            case SyntaxKind.MinusMinusToken:
                var ok = CheckArithmeticOperandType(expr.Operand!, CheckNonNullType(operandType, expr.Operand!), Diag.An_arithmetic_operand_must_be_of_type_any_number_bigint_or_an_enum_type, false);
                if (ok)
                {
                    // run check only if former checks succeeded to avoid reporting cascading errors
                    CheckReferenceExpression(expr.Operand!, Diag.The_operand_of_an_increment_or_decrement_operator_must_be_a_variable_or_a_property_access, Diag.The_operand_of_an_increment_or_decrement_operator_may_not_be_an_optional_property_access);
                }
                return GetUnaryResultType(operandType);
        }
        return errorType!;
    }

    // checker.go:10898
    internal Type GetUnaryResultType(Type operandType)
    {
        if (MaybeTypeOfKind(operandType, TypeFlags.BigIntLike))
        {
            if (IsTypeAssignableToKind(operandType, TypeFlags.AnyOrUnknown) || MaybeTypeOfKind(operandType, TypeFlags.NumberLike))
            {
                return numberOrBigIntType!;
            }
            return bigintType!;
        }
        // If it's not a bigint type, implicit coercion will result in a number
        return numberType!;
    }

    // checker.go:10926
    internal Type CheckYieldExpression(Node node)
    {
        CheckGrammarYieldExpression(node);
        var fn = GetContainingFunction(node);
        if (fn == null)
        {
            return anyType!;
        }
        var functionFlags = AstUtilities.GetFunctionFlags(fn);
        if ((functionFlags & FunctionFlags.Generator) == 0)
        {
            // If the user's code is syntactically correct, the func should always have a star. After all, we are in a yield context.
            return anyType!;
        }
        var isAsync = (functionFlags & FunctionFlags.Async) != 0;
        if (node.AsYieldExpression().AsteriskToken != null)
        {
            // Async generator functions prior to ES2018 require the __await, __asyncDelegator,
            // and __asyncValues helpers
            if (isAsync && languageVersion < LanguageFeatureMinimum.LanguageFeatureMinimumTarget.AsyncGenerators)
            {
                CheckExternalEmitHelpers(node, ExternalEmitHelpers.AsyncDelegatorIncludes);
            }
        }
        // There is no point in doing an assignability check if the function
        // has no explicit return type because the return type is directly computed
        // from the yield expressions.
        var returnType = GetReturnTypeFromAnnotation(fn);
        if (returnType != null && (returnType.flags & TypeFlags.Union) != 0)
        {
            returnType = FilterType(returnType, t => CheckGeneratorInstantiationAssignabilityToReturnType(t, functionFlags, null /*errorNode*/));
        }
        var iterationTypes = default(IterationTypes);
        if (returnType != null)
        {
            iterationTypes = GetIterationTypesOfGeneratorFunctionReturnType(returnType, isAsync);
        }
        var signatureYieldType = CoreUtil.OrElse(iterationTypes.YieldType, anyType)!;
        var signatureNextType = CoreUtil.OrElse(iterationTypes.NextType, anyType)!;
        Type yieldExpressionType;
        if (node.Expression() != null)
        {
            yieldExpressionType = CheckExpression(node.Expression()!);
        }
        else
        {
            yieldExpressionType = undefinedWideningType!;
        }
        var yieldedType = GetYieldedTypeOfYieldExpression(node, yieldExpressionType, signatureNextType, isAsync);
        if (returnType != null && yieldedType != null)
        {
            CheckTypeAssignableToAndOptionallyElaborate(yieldedType, signatureYieldType, CoreUtil.OrElse(node.Expression(), node)!, node.Expression(), null, null);
        }
        if (node.AsYieldExpression().AsteriskToken != null)
        {
            var use = CoreUtil.IfElse(isAsync, IterationUse.AsyncYieldStar, IterationUse.YieldStar);
            return CoreUtil.OrElse(GetIterationTypeOfIterable(use, IterationTypeKind.Return, yieldExpressionType, node.Expression()), anyType)!;
        }
        if (returnType != null)
        {
            return CoreUtil.OrElse(GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Next, returnType, isAsync), anyType)!;
        }
        var t = GetContextualIterationType(IterationTypeKind.Next, fn);
        if (t == null)
        {
            t = anyType!;
            if (noImplicitAny && !ExpressionResultIsUnused(node))
            {
                var contextualType = GetContextualType(node, ContextFlags.None);
                if (contextualType == null || IsTypeAny(contextualType))
                {
                    Error(node, Diag.X_yield_expression_implicitly_results_in_an_any_type_because_its_containing_generator_lacks_a_return_type_annotation);
                }
            }
        }
        return t;
    }

    // checker.go:10993
    internal Type? GetYieldedTypeOfYieldExpression(Node node, Type expressionType, Type sentType, bool isAsync)
    {
        var errorNode = CoreUtil.OrElse(node.Expression(), node)!;
        var isYieldStar = node.AsYieldExpression().AsteriskToken != null;
        // A `yield*` expression effectively yields everything that its operand yields
        var yieldedType = expressionType;
        if (isYieldStar)
        {
            yieldedType = CheckIteratedTypeOrElementType(CoreUtil.IfElse(isAsync, IterationUse.AsyncYieldStar, IterationUse.YieldStar), expressionType, sentType, errorNode);
        }
        if (!isAsync)
        {
            return yieldedType;
        }
        return GetAwaitedTypeEx(yieldedType, errorNode, CoreUtil.IfElse(isYieldStar,
            Diag.Type_of_iterated_elements_of_a_yield_Asterisk_operand_must_either_be_a_valid_promise_or_must_not_contain_a_callable_then_member,
            Diag.Type_of_yield_operand_in_an_async_generator_must_either_be_a_valid_promise_or_must_not_contain_a_callable_then_member));
    }

    // Indicates whether the result of an `Expression` will be unused.
    // NOTE: This requires a node with a valid `parent` pointer.
    // internal/checker/utilities.go:1136 (free func expressionResultIsUnused)
    internal static bool ExpressionResultIsUnused(Node node)
    {
        while (true)
        {
            var parent = node.Parent!;
            // walk up parenthesized expressions, but keep a pointer to the top-most parenthesized expression
            if (NodeTests.IsParenthesizedExpression(parent))
            {
                node = parent;
                continue;
            }
            // result is unused in an expression statement, `void` expression, or the initializer or incrementer of a `for` loop
            if (NodeTests.IsExpressionStatement(parent) || NodeTests.IsVoidExpression(parent) || NodeTests.IsForStatement(parent) && (parent.Initializer() == node || parent.AsForStatement().Incrementor == node))
            {
                return true;
            }
            if (NodeTests.IsBinaryExpression(parent) && parent.AsBinaryExpression().OperatorToken!.Kind == SyntaxKind.CommaToken)
            {
                // left side of comma is always unused
                if (node == parent.AsBinaryExpression().Left)
                {
                    return true;
                }
                // right side of comma is unused if parent is unused
                node = parent;
                continue;
            }
            return false;
        }
    }

    // checker.go:12046
    internal Type CheckThisExpression(Node node)
    {
        // Stop at the first arrow function so that we can
        // tell whether 'this' needs to be captured.
        var container = AstUtilities.GetThisContainer(node, true /*includeArrowFunctions*/, true /*includeClassComputedPropertyName*/);
        var capturedByArrowFunction = false;
        var thisInComputedPropertyName = false;
        if (NodeTests.IsConstructorDeclaration(container))
        {
            CheckThisBeforeSuper(node, container, Diag.X_super_must_be_called_before_accessing_this_in_the_constructor_of_a_derived_class);
        }
        while (true)
        {
            // Now skip arrow functions to get the "real" owner of 'this'.
            if (NodeTests.IsArrowFunction(container))
            {
                container = AstUtilities.GetThisContainer(container, false /*includeArrowFunctions*/, !thisInComputedPropertyName);
                capturedByArrowFunction = true;
            }
            if (NodeTests.IsComputedPropertyName(container))
            {
                container = AstUtilities.GetThisContainer(container, !capturedByArrowFunction, false /*includeClassComputedPropertyName*/);
                thisInComputedPropertyName = true;
                continue;
            }
            break;
        }
        CheckThisInStaticClassFieldInitializerInDecoratedClass(node, container);
        if (thisInComputedPropertyName)
        {
            Error(node, Diag.X_this_cannot_be_referenced_in_a_computed_property_name);
        }
        else
        {
            switch (container.Kind)
            {
                case SyntaxKind.ModuleDeclaration:
                    Error(node, Diag.X_this_cannot_be_referenced_in_a_module_or_namespace_body);
                    // do not return here so in case if lexical this is captured - it will be reflected in flags on NodeLinks
                    break;
                case SyntaxKind.EnumDeclaration:
                    Error(node, Diag.X_this_cannot_be_referenced_in_current_location);
                    // do not return here so in case if lexical this is captured - it will be reflected in flags on NodeLinks
                    break;
            }
        }
        var t = TryGetThisTypeAtEx(node, true /*includeGlobalThis*/, container);
        if (noImplicitThis)
        {
            var globalThisType = GetTypeOfSymbol(globalThisSymbol);
            if (t == globalThisType && capturedByArrowFunction)
            {
                Error(node, Diag.The_containing_arrow_function_captures_the_global_value_of_this);
            }
            else if (t == null)
            {
                // With noImplicitThis, functions may not reference 'this' if it has type 'any'
                var diag = Error(node, Diag.X_this_implicitly_has_type_any_because_it_does_not_have_a_type_annotation);
                if (!NodeTests.IsSourceFile(container))
                {
                    var outsideThis = TryGetThisTypeAt(container);
                    if (outsideThis != null && outsideThis != globalThisType)
                    {
                        diag.AddRelatedInfo(CreateDiagnosticForNode(container, Diag.An_outer_value_of_this_is_shadowed_by_this_container));
                    }
                }
            }
        }
        if (t == null)
        {
            return anyType!;
        }
        return t;
    }

    // checker.go:12103
    internal Type? TryGetThisTypeAt(Node node)
    {
        return TryGetThisTypeAtEx(node, true /*includeGlobalThis*/, null /*container*/);
    }

    // checker.go:12108 — the EXPORTED Go wrapper TryGetThisTypeAtEx (JSDoc-reparse handling for the
    // language service) is not ported here: its C# name would collide with the internal
    // tryGetThisTypeAtEx below. The LS wave ports it (with GetReparsedNodeForNode) under its own name.

    // checker.go:12115
    internal Type? TryGetThisTypeAtEx(Node node, bool includeGlobalThis, Node? container)
    {
        if (container == null)
        {
            container = GetThisContainer(node, false /*includeArrowFunctions*/, false /*includeClassComputedPropertyName*/);
        }
        if (AstUtilities.IsFunctionLike(container) && (!IsInParameterInitializerBeforeContainingFunction(node) || AstUtilities.GetThisParameter(container) != null))
        {
            var sig = GetSignatureOfFullSignatureType(container);
            if (sig == null)
            {
                sig = GetSignatureFromDeclaration(container);
            }
            var thisType = GetThisTypeOfSignature(sig);
            // Note: a parameter initializer should refer to class-this unless function-this is explicitly annotated.
            // If this is a function in a JS file, it might be a class method.
            if (thisType == null)
            {
                thisType = GetContextualThisParameterType(container);
            }
            if (thisType != null)
            {
                return GetFlowTypeOfReference(node, thisType);
            }
        }
        if (container.Parent != null && AstUtilities.IsClassLike(container.Parent))
        {
            var symbol = GetSymbolOfDeclaration(container.Parent)!;
            Type t;
            if (AstUtilities.IsStatic(container))
            {
                t = GetTypeOfSymbol(symbol);
            }
            else
            {
                t = GetDeclaredTypeOfSymbol(symbol).AsInterfaceType()!.thisType!;
            }
            return GetFlowTypeOfReference(node, t);
        }
        if (NodeTests.IsSourceFile(container))
        {
            // look up in the source file's locals or exports
            if (container.AsSourceFile().ExternalModuleIndicator != null)
            {
                // TODO: Maybe issue a better error than 'object is possibly undefined'
                return undefinedType;
            }
            if (includeGlobalThis)
            {
                return GetTypeOfSymbol(globalThisSymbol);
            }
        }
        return null;
    }

    // checker.go:12161 — the checker carries its own getThisContainer in Go; its body is behaviorally
    // identical to ast.GetThisContainer (utilities.go:1771, ported as AstUtilities.GetThisContainer),
    // so we delegate.
    internal Node GetThisContainer(Node node, bool includeArrowFunctions, bool includeClassComputedPropertyName)
    {
        return AstUtilities.GetThisContainer(node, includeArrowFunctions, includeClassComputedPropertyName);
    }

    // checker.go:12204
    internal bool IsInParameterInitializerBeforeContainingFunction(Node node)
    {
        var inBindingInitializer = false;
        while (node.Parent != null && !AstUtilities.IsFunctionLike(node.Parent))
        {
            if (NodeTests.IsParameterDeclaration(node.Parent))
            {
                if (inBindingInitializer || node.Parent.Initializer() == node)
                {
                    return true;
                }
            }

            if (NodeTests.IsBindingElement(node.Parent) && node.Parent.Initializer() == node)
            {
                inBindingInitializer = true;
            }

            node = node.Parent;
        }

        return false;
    }

    // checker.go:12227
    internal void CheckThisInStaticClassFieldInitializerInDecoratedClass(Node thisExpression, Node container)
    {
        if (NodeTests.IsPropertyDeclaration(container) && AstUtilities.HasStaticModifier(container) && legacyDecorators)
        {
            var initializer = container.Initializer();
            if (initializer != null && initializer.Loc.ContainsInclusive(thisExpression.Pos) && AstUtilities.HasDecorators(container.Parent!))
            {
                Error(thisExpression, Diag.Cannot_use_this_in_a_static_property_initializer_of_a_decorated_class);
            }
        }
    }

    // checker.go:12237
    internal void CheckThisBeforeSuper(Node node, Node container, global::Ts.Diagnostics.Message diagnosticMessage)
    {
        var containingClassDecl = container.Parent!;
        var baseTypeNode = AstUtilities.GetExtendsHeritageClauseElement(containingClassDecl);
        // If a containing class does not have extends clause or the class extends null
        // skip checking whether super statement is called before "this" accessing.
        if (baseTypeNode != null && !ClassDeclarationExtendsNull(containingClassDecl))
        {
            if (node.FlowNodeData() != null && !IsPostSuperFlowNode(node.FlowNodeData()!.FlowNode!, false /*noCacheCheck*/))
            {
                Error(node, diagnosticMessage);
            }
        }
    }

    /**
     * Check if the given class-declaration extends null then return true.
     * Otherwise, return false
     * @param classDecl a class declaration to check if it extends null
     */
    // checker.go:12253
    internal bool ClassDeclarationExtendsNull(Node classDecl)
    {
        var classSymbol = GetSymbolOfDeclaration(classDecl)!;
        var classInstanceType = GetDeclaredTypeOfSymbol(classSymbol);
        var baseConstructorType = GetBaseConstructorTypeOfClass(classInstanceType);
        return baseConstructorType == nullWideningType;
    }

    // checker.go:12256
    internal Type CheckAssertion(Node node, CheckMode checkMode)
    {
        if (node.Kind == SyntaxKind.TypeAssertionExpression)
        {
            if (ShouldCheckErasableSyntax(node))
            {
                AddDiagnostic(Diagnostic.NewDiagnostic(AstUtilities.GetSourceFileOfNode(node), new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(AstUtilities.GetSourceFileOfNode(node)!.Text(), node.Pos), node.Expression()!.Pos), Diag.This_syntax_is_not_allowed_when_erasableSyntaxOnly_is_enabled));
            }
        }
        var typeNode = node.Type()!;
        var exprType = CheckExpressionEx(node.Expression()!, checkMode);
        if (AstUtilities.IsConstTypeReference(typeNode))
        {
            if (!IsValidConstAssertionArgument(node.Expression()!))
            {
                Error(node.Expression(), Diag.A_const_assertion_can_only_be_applied_to_references_to_enum_members_or_string_number_boolean_array_or_object_literals);
            }
            return GetRegularTypeOfLiteralType(exprType);
        }
        var links = assertionLinks.Get(node);
        links.exprType = exprType;
        CheckSourceElement(typeNode);
        CheckNodeDeferred(node);
        return GetTypeFromTypeNode(typeNode);
    }

    // checker.go:12277
    internal void CheckAssertionDeferred(Node node)
    {
        var typeNode = node.Type()!;
        var exprType = GetRegularTypeOfObjectLiteral(GetBaseTypeOfLiteralType(assertionLinks.Get(node).exprType!));
        var targetType = GetTypeFromTypeNode(typeNode);
        if (!IsErrorType(targetType))
        {
            var widenedType = GetWidenedType(exprType);
            if (!IsTypeComparableTo(targetType, widenedType))
            {
                var errNode = node;
                if ((typeNode.Flags & NodeFlags.Reparsed) != 0)
                {
                    errNode = typeNode;
                }
                CheckTypeComparableTo(exprType, targetType, errNode, Diag.Conversion_of_type_0_to_type_1_may_be_a_mistake_because_neither_type_sufficiently_overlaps_with_the_other_If_this_was_intentional_convert_the_expression_to_unknown_first);
            }
        }
    }
}
