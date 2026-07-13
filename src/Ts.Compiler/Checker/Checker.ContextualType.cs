// Ported from tsgo internal/checker/checker.go — contextual type family (§4.2 partial #21,
// checker.go:29100-31009). Companion Deps file: Checker.ContextualType.Deps.cs.
//
// Trunk: getContextualType (29100) dispatcher + the common branches: initializer/variable-like (29180/
// 29195), return expression (29378) + getContextualReturnType (29422), argument (29519) +
// getContextualTypeForArgumentAtIndex (29529), object-literal element/method (29677/29721), binary/
// conditional/substitution operands (29566/29779/29787), element expression (29729), apparent-type
// wrapper (30443), getContextualSignature (10240) + getContextualSignatureForFunctionLikeDeclaration
// (29468) + getContextualCallSignature (10281), the contextual-stack helpers push/pop/find (30633/30637/
// 30643), and getTypeOfPropertyOfContextualType (30308).
//
// §1.5: cross-package via global::; host classes direct. PORT-NOTE: deep leaves still stubbed and CALLED
// (bodies deferred to their owning waves): mapType/mapTypeEx, getTypeOfExpression, removeMissingType,
// getContextuallyTypedParameterType, instantiateContextualType, getTypeOfPropertyOfContextualTypeEx (a
// large intersection/mapped-type surface — kept as its existing stub), getContextualTypeForYieldOperand/
// AwaitOperand/Decorator/BindingElement/StaticPropertyDeclaration/AssignmentExpression (generator/JS-
// assignment-declaration surfaces — deferred stubs in the Deps file), discriminateContextualType*.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:29100
    internal Type? GetContextualType(Node node, ContextFlags contextFlags)
    {
        if ((node.Flags & NodeFlags.InWithStatement) != 0)
        {
            // We cannot answer semantic questions within a with block, do not proceed any further
            return null;
        }
        // Cached contextual types are obtained with no ContextFlags, so we can only consult them for
        // requests with no ContextFlags.
        var index = FindContextualNode(node, contextFlags == ContextFlags.None /*includeCaches*/);
        if (index >= 0)
        {
            return contextualInfos[index].t;
        }
        var parent = node.Parent!;
        switch (parent.Kind)
        {
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.Parameter:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
            case SyntaxKind.BindingElement:
                return GetContextualTypeForInitializerExpression(node, contextFlags);
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.ReturnStatement:
                return GetContextualTypeForReturnExpression(node, contextFlags);
            case SyntaxKind.YieldExpression:
                return GetContextualTypeForYieldOperand(parent, contextFlags);
            case SyntaxKind.AwaitExpression:
                return GetContextualTypeForAwaitOperand(parent, contextFlags);
            case SyntaxKind.CallExpression:
            case SyntaxKind.NewExpression:
                return GetContextualTypeForArgument(parent, node);
            case SyntaxKind.Decorator:
                return GetContextualTypeForDecorator(parent);
            case SyntaxKind.TypeAssertionExpression:
            case SyntaxKind.AsExpression:
                if (AstUtilities.IsConstAssertion(parent))
                {
                    return GetContextualType(parent, contextFlags);
                }
                return GetTypeFromTypeNode(parent.Type()!);
            case SyntaxKind.BinaryExpression:
                return GetContextualTypeForBinaryOperand(node, contextFlags);
            case SyntaxKind.PropertyAssignment:
            case SyntaxKind.ShorthandPropertyAssignment:
                return GetContextualTypeForObjectLiteralElement(parent, contextFlags);
            case SyntaxKind.SpreadAssignment:
                return GetContextualType(parent.Parent!, contextFlags);
            case SyntaxKind.ArrayLiteralExpression:
                {
                    var t = GetApparentTypeOfContextualType(parent, contextFlags);
                    var elementIndex = IndexOfNode(parent.Elements()!, node);
                    if (elementIndex < 0)
                    {
                        return null;
                    }
                    var (firstSpreadIndex, lastSpreadIndex) = GetSpreadIndices(parent);
                    return GetContextualTypeForElementExpression(t, elementIndex, parent.Elements()!.Length, firstSpreadIndex, lastSpreadIndex);
                }
            case SyntaxKind.ConditionalExpression:
                return GetContextualTypeForConditionalOperand(node, contextFlags);
            case SyntaxKind.TemplateSpan:
                return GetContextualTypeForSubstitutionExpression(parent.Parent!, node);
            case SyntaxKind.ParenthesizedExpression:
                return GetContextualType(parent, contextFlags);
            case SyntaxKind.NonNullExpression:
                return GetContextualType(parent, contextFlags);
            case SyntaxKind.SatisfiesExpression:
                return GetTypeFromTypeNode(parent.Type()!);
            case SyntaxKind.ExportAssignment:
                return TryGetTypeFromTypeNode(parent);
            case SyntaxKind.JsxExpression:
                return GetContextualTypeForJsxExpression(parent, contextFlags);
            case SyntaxKind.JsxAttribute:
            case SyntaxKind.JsxSpreadAttribute:
                return GetContextualTypeForJsxAttribute(parent, contextFlags);
            case SyntaxKind.JsxOpeningElement:
            case SyntaxKind.JsxSelfClosingElement:
                return GetContextualJsxElementAttributesType(parent, contextFlags);
            case SyntaxKind.ImportAttribute:
                return GetContextualImportAttributeType(parent);
        }
        return null;
    }

    // checker.go:29180
    private Type? GetContextualTypeForInitializerExpression(Node node, ContextFlags contextFlags)
    {
        var declaration = node.Parent!;
        var initializer = declaration.Initializer();
        if (node == initializer)
        {
            var result = GetContextualTypeForVariableLikeDeclaration(declaration, contextFlags);
            if (result != null)
            {
                return result;
            }
            if ((contextFlags & ContextFlags.SkipBindingPatterns) == 0 && AstUtilities.IsBindingPattern(declaration.Name()) && declaration.Name()!.Elements().Len() > 0)
            {
                return GetTypeFromBindingPattern(declaration.Name()!, true /*includePatternInType*/, false /*reportErrors*/);
            }
        }
        return null;
    }

    // checker.go:29195
    private Type? GetContextualTypeForVariableLikeDeclaration(Node declaration, ContextFlags contextFlags)
    {
        var typeNode = declaration.Type();
        if (typeNode != null)
        {
            return GetTypeFromTypeNode(typeNode);
        }
        switch (declaration.Kind)
        {
            case SyntaxKind.Parameter:
                return GetContextuallyTypedParameterType(declaration);
            case SyntaxKind.BindingElement:
                return GetContextualTypeForBindingElement(declaration, contextFlags);
            case SyntaxKind.PropertyDeclaration:
                if (AstUtilities.IsStatic(declaration))
                {
                    return GetContextualTypeForStaticPropertyDeclaration(declaration, contextFlags);
                }
                break;
        }
        // By default, do nothing and return null - only the above cases have context implied by a parent
        return null;
    }

    // checker.go:29378
    private Type? GetContextualTypeForReturnExpression(Node node, ContextFlags contextFlags)
    {
        var fn = GetContainingFunction(node);
        if (fn != null)
        {
            var contextualReturnType = GetContextualReturnType(fn, contextFlags);
            if (contextualReturnType != null)
            {
                var functionFlags = AstUtilities.GetFunctionFlags(fn);
                if ((functionFlags & FunctionFlags.Generator) != 0)
                {
                    var isAsyncGenerator = (functionFlags & FunctionFlags.Async) != 0;
                    if ((contextualReturnType.flags & TypeFlags.Union) != 0)
                    {
                        contextualReturnType = FilterType(contextualReturnType, t => GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Return, t, isAsyncGenerator) != null);
                    }
                    var iterationReturnType = GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Return, contextualReturnType, (functionFlags & FunctionFlags.Async) != 0);
                    if (iterationReturnType == null)
                    {
                        return null;
                    }
                    contextualReturnType = iterationReturnType;
                    // falls through to unwrap Promise for AsyncGenerators
                }
                if ((functionFlags & FunctionFlags.Async) != 0)
                {
                    // Get the awaited type without the `Awaited<T>` alias
                    var contextualAwaitedType = MapType(contextualReturnType, t => GetAwaitedTypeNoAlias(t)!);
                    if (contextualAwaitedType == null)
                    {
                        return null;
                    }
                    return GetUnionType(new[] { contextualAwaitedType, CreatePromiseLikeType(contextualAwaitedType) });
                }
                // Regular function or Generator function
                return contextualReturnType;
            }
        }
        return null;
    }

    // checker.go:29422
    private Type? GetContextualReturnType(Node functionDecl, ContextFlags contextFlags)
    {
        // If the containing function has a return type annotation, is a constructor, or is a get accessor
        // whose set accessor has a type annotation, return statements are contextually typed.
        var returnType = GetReturnTypeFromAnnotation(functionDecl);
        if (returnType != null)
        {
            return returnType;
        }
        // Otherwise, if the containing function is contextually typed by a function type with exactly one
        // non-generic call signature, return statements are contextually typed by its return type.
        var signature = GetContextualSignatureForFunctionLikeDeclaration(functionDecl);
        if (signature != null && !IsResolvingReturnTypeOfSignature(signature))
        {
            var retType = GetReturnTypeOfSignature(signature);
            var functionFlags = AstUtilities.GetFunctionFlags(functionDecl);
            if ((functionFlags & FunctionFlags.Generator) != 0)
            {
                return FilterType(retType, t => (t.flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void | TypeFlags.InstantiableNonPrimitive)) != 0 || CheckGeneratorInstantiationAssignabilityToReturnType(t, functionFlags, null /*errorNode*/));
            }
            if ((functionFlags & FunctionFlags.Async) != 0)
            {
                return FilterType(retType, t => (t.flags & (TypeFlags.AnyOrUnknown | TypeFlags.Void | TypeFlags.InstantiableNonPrimitive)) != 0 || GetAwaitedTypeOfPromise(t) != null);
            }
            return retType;
        }
        var iife = AstUtilities.GetImmediatelyInvokedFunctionExpression(functionDecl);
        if (iife != null)
        {
            return GetContextualType(iife, contextFlags);
        }
        return null;
    }

    // checker.go:29468
    internal Signature? GetContextualSignatureForFunctionLikeDeclaration(Node node)
    {
        // Only function expressions, arrow functions, and object literal methods are contextually typed.
        if (AstUtilities.IsFunctionExpressionOrArrowFunction(node) || AstUtilities.IsObjectLiteralMethod(node))
        {
            return GetContextualSignature(node);
        }
        return null;
    }

    // checker.go:10240
    private Signature? GetContextualSignature(Node node)
    {
        var t = GetApparentTypeOfContextualType(node, ContextFlags.Signature);
        if (t == null)
        {
            return null;
        }
        if ((t.flags & TypeFlags.Union) == 0)
        {
            return GetContextualCallSignature(t, node);
        }
        List<Signature>? signatureList = null;
        var types = t.Types();
        foreach (var current in types)
        {
            var signature = GetContextualCallSignature(current, node);
            if (signature != null)
            {
                if (signatureList != null && signatureList.Count != 0 && CompareSignaturesIdentical(signatureList[0], signature, false /*partialMatch*/, true /*ignoreThisTypes*/, true /*ignoreReturnTypes*/, CompareTypesIdentical) == Ternary.False)
                {
                    // Signatures aren't identical, do not use
                    return null;
                }
                // Use this signature for contextual union signature
                (signatureList ??= new List<Signature>()).Add(signature);
            }
        }
        if (signatureList == null || signatureList.Count == 0)
        {
            return null;
        }
        if (signatureList.Count == 1)
        {
            return signatureList[0];
        }
        // Result is union of signatures collected (return type is union of return types of this set)
        return CreateUnionSignature(signatureList[0], signatureList.ToArray());
    }

    // checker.go:10281
    private Signature? GetContextualCallSignature(Type t, Node node)
    {
        var signatures = GetSignaturesOfType(t, SignatureKind.Call);
        var applicableByArity = CoreUtil.Filter(signatures, s => !IsAritySmaller(s, node)) ?? [];
        if (applicableByArity.Length == 1)
        {
            return applicableByArity[0];
        }
        return GetIntersectedSignatures(applicableByArity);
    }

    // checker.go:10290-10306
    private Signature? GetIntersectedSignatures(Signature[] signatures)
    {
        if (!noImplicitAny)
        {
            return null;
        }
        Signature? combined = null;
        foreach (var sig in signatures)
        {
            if (combined == sig || combined is null)
            {
                combined = sig;
            }
            else if (CompareTypeParametersIdentical(combined.typeParameters, sig.typeParameters))
            {
                combined = CombineUnionOrIntersectionMemberSignatures(combined, sig, false /*isUnion*/);
            }
            else
            {
                return null;
            }
        }
        return combined;
    }

    // checker.go:10311 (isAritySmaller) — If the contextual signature has fewer parameters than the
    // function expression, do not use it.
    private bool IsAritySmaller(Signature signature, Node target)
    {
        var parameters = target.Parameters();
        var targetParameterCount = 0;
        while (targetParameterCount < parameters.Length)
        {
            var param = parameters[targetParameterCount];
            if (param.Initializer() != null || param.QuestionToken() != null || HasDotDotDotToken(param))
            {
                break;
            }
            targetParameterCount++;
        }
        if (parameters.Length != 0 && AstUtilities.IsThisParameter(parameters[0]))
        {
            targetParameterCount--;
        }
        return !HasEffectiveRestParameter(signature) && GetParameterCount(signature) < targetParameterCount;
    }

    // checker.go — createUnionSignature
    private Signature CreateUnionSignature(Signature sig, Signature[] unionSignatures)
    {
        var result = CloneSignature(sig);
        result.composite = new CompositeSignature { isUnion = true, signatures = unionSignatures };
        result.target = null;
        result.mapper = null;
        return result;
    }

    // checker.go:29519 — In a typed function call, an argument or substitution expression is contextually
    // typed by the type of the corresponding parameter.
    private Type? GetContextualTypeForArgument(Node callTarget, Node arg)
    {
        var args = GetEffectiveCallArguments(callTarget);
        var argIndex = global::System.Array.IndexOf(args, arg);
        // -1 for e.g. the expression of a CallExpression, or the tag of a TaggedTemplateExpression
        if (argIndex == -1)
        {
            return null;
        }
        return GetContextualTypeForArgumentAtIndex(callTarget, argIndex);
    }

    // checker.go:29529
    internal Type? GetContextualTypeForArgumentAtIndex(Node callTarget, int argIndex)
    {
        if (IsImportCall(callTarget))
        {
            if (argIndex == 0)
            {
                return stringType;
            }
            if (argIndex == 1)
            {
                return GetGlobalImportCallOptionsType();
            }
            return anyType;
        }
        // If we're already in the process of resolving the given signature, don't resolve again as that
        // could cause infinite recursion. Instead, return the resolving signature.
        Signature signature;
        if (signatureLinks.Get(callTarget).resolvedSignature == resolvingSignature)
        {
            signature = resolvingSignature!;
        }
        else
        {
            signature = GetResolvedSignature(callTarget, null, CheckMode.Normal);
        }
        if (IsJsxOpeningLikeElement(callTarget) && argIndex == 0)
        {
            return GetEffectiveFirstArgumentForJsxSignature(signature, callTarget);
        }
        var restIndex = signature.parameters.Len() - 1;
        if (SignatureHasRestParameter(signature) && argIndex >= restIndex)
        {
            return GetIndexedAccessTypeEx(GetTypeOfSymbol(signature.parameters![restIndex]), GetNumberLiteralType(new global::Ts.JsNum.Number(argIndex - restIndex)), AccessFlags.Contextual, null, null);
        }
        return GetTypeAtPosition(signature, argIndex);
    }

    // checker.go:29566
    private Type? GetContextualTypeForBinaryOperand(Node node, ContextFlags contextFlags)
    {
        var binary = node.Parent!.AsBinaryExpression();
        if (binary.Type != null)
        {
            return GetTypeFromTypeNode(binary.Type);
        }
        switch (binary.OperatorToken!.Kind)
        {
            case SyntaxKind.EqualsToken:
            case SyntaxKind.AmpersandAmpersandEqualsToken:
            case SyntaxKind.BarBarEqualsToken:
            case SyntaxKind.QuestionQuestionEqualsToken:
                // In an assignment expression, the right operand is contextually typed by the type of the
                // left operand unless it's an assignment declaration.
                if (node == binary.Right)
                {
                    var target = Precedence.GetLeftmostExpression(binary.Left!, false);
                    if (!(NodeTests.IsIdentifier(target) && (GetResolvedSymbol(target).Flags & SymbolFlags.ModuleExports) != 0))
                    {
                        return GetContextualTypeForAssignmentExpression(binary);
                    }
                }
                break;
            case SyntaxKind.BarBarToken:
            case SyntaxKind.QuestionQuestionToken:
                {
                    // When an || expression has a contextual type, the operands are contextually typed by
                    // that type, except when it originates in a binding pattern.
                    var t = GetContextualType(binary.AsNode(), contextFlags);
                    if (node == binary.Right && (t == null || patternForType.ContainsKey(t)))
                    {
                        return GetTypeOfExpression(binary.Left!);
                    }
                    return t;
                }
            case SyntaxKind.AmpersandAmpersandToken:
            case SyntaxKind.CommaToken:
                if (node == binary.Right)
                {
                    return GetContextualType(binary.AsNode(), contextFlags);
                }
                break;
        }
        return null;
    }

    // checker.go:29677
    private Type? GetContextualTypeForObjectLiteralElement(Node element, ContextFlags contextFlags)
    {
        var elementType = element.Type();
        if (elementType != null && !AstUtilities.IsObjectLiteralMethod(element))
        {
            return GetTypeFromTypeNode(elementType);
        }
        var objectLiteral = element.Parent!;
        var t = GetApparentTypeOfContextualType(objectLiteral, contextFlags);
        if (t != null)
        {
            if (HasBindableName(element))
            {
                // For a (non-symbol) computed property, there is no reason to look up the name in the type.
                var symbol = GetSymbolOfDeclaration(element)!;
                return GetTypeOfPropertyOfContextualTypeEx(t, symbol.Name, valueSymbolLinks.Get(symbol).nameType);
            }
            if (AstUtilities.HasDynamicName(element))
            {
                var name = AstUtilities.GetNameOfDeclaration(element);
                if (name != null && NodeTests.IsComputedPropertyName(name))
                {
                    var exprType = CheckExpression(name.Expression()!);
                    if (IsTypeUsableAsPropertyName(exprType))
                    {
                        var propType2 = GetTypeOfPropertyOfContextualType(t, GetPropertyNameFromType(exprType));
                        if (propType2 != null)
                        {
                            return propType2;
                        }
                    }
                }
            }
            if (element.Name() != null)
            {
                var nameType = GetLiteralTypeFromPropertyName(element.Name()!);
                // We avoid getApplicableIndexInfo here because it performs expensive intersection reduction.
                return MapTypeEx(t, tt =>
                {
                    var indexInfo = FindApplicableIndexInfo(GetIndexInfosOfStructuredType(tt), nameType);
                    if (indexInfo == null)
                    {
                        return null!;
                    }
                    return indexInfo.valueType;
                }, true /*noReductions*/);
            }
        }
        return null;
    }

    // checker.go:29721 — In an object literal contextually typed by T, the contextual type of a property
    // assignment is the type of the matching property in T, or an index signature type.
    internal Type? GetContextualTypeForObjectLiteralMethod(Node node, ContextFlags contextFlags)
    {
        if ((node.Flags & NodeFlags.InWithStatement) != 0)
        {
            // We cannot answer semantic questions within a with block, do not proceed any further
            return null;
        }
        return GetContextualTypeForObjectLiteralElement(node, contextFlags);
    }

    // checker.go:29779 — In a contextually typed conditional expression, the true/false expressions are
    // contextually typed by the same type.
    private Type? GetContextualTypeForConditionalOperand(Node node, ContextFlags contextFlags)
    {
        var conditional = node.Parent!.AsConditionalExpression();
        if (node == conditional.WhenTrue || node == conditional.WhenFalse)
        {
            return GetContextualType(node.Parent!, contextFlags);
        }
        return null;
    }

    // checker.go:29787
    private Type? GetContextualTypeForSubstitutionExpression(Node template, Node substitutionExpression)
    {
        if (NodeTests.IsTaggedTemplateExpression(template.Parent!))
        {
            return GetContextualTypeForArgument(template.Parent!, substitutionExpression);
        }
        return null;
    }

    // checker.go:29794
    private Type? GetContextualImportAttributeType(Node node)
    {
        return GetTypeOfPropertyOfContextualType(GetGlobalImportAttributesType(), node.Name()!.Text());
    }

    // checker.go:30308
    internal Type? GetTypeOfPropertyOfContextualType(Type t, string name)
    {
        return GetTypeOfPropertyOfContextualTypeEx(t, name, null);
    }

    // checker.go:30312
    internal Type? GetTypeOfPropertyOfContextualTypeEx(Type t, string name, Type? nameType)
    {
        return MapTypeEx(t, tt =>
        {
            if ((tt.flags & TypeFlags.Intersection) != 0)
            {
                List<Type>? types = null;
                List<Type>? indexInfoCandidates = null;
                var ignoreIndexInfos = false;
                foreach (var constituentType in tt.Types())
                {
                    if ((constituentType.flags & TypeFlags.Object) == 0)
                    {
                        continue;
                    }
                    if (IsGenericMappedType(constituentType) && GetMappedTypeNameTypeKind(constituentType) != MappedTypeNameTypeKind.Remapping)
                    {
                        var substitutedType = GetIndexedMappedTypeSubstitutedTypeOfContextualType(constituentType, name, nameType);
                        types = AppendContextualPropertyTypeConstituent(types, substitutedType);
                        continue;
                    }
                    var propertyType = GetTypeOfConcretePropertyOfContextualType(constituentType, name);
                    if (propertyType == null)
                    {
                        if (!ignoreIndexInfos)
                        {
                            (indexInfoCandidates ??= []).Add(constituentType);
                        }
                        continue;
                    }
                    ignoreIndexInfos = true;
                    indexInfoCandidates = null;
                    types = AppendContextualPropertyTypeConstituent(types, propertyType);
                }
                if (indexInfoCandidates != null)
                {
                    foreach (var candidate in indexInfoCandidates)
                    {
                        var indexInfoType = GetTypeFromIndexInfosOfContextualType(candidate, name, nameType);
                        types = AppendContextualPropertyTypeConstituent(types, indexInfoType);
                    }
                }
                if ((types?.Count ?? 0) == 0)
                {
                    return null;
                }
                if (types!.Count == 1)
                {
                    return types[0];
                }
                return GetIntersectionType(types.ToArray());
            }
            if ((tt.flags & TypeFlags.Object) == 0)
            {
                return null;
            }
            if (IsGenericMappedType(tt) && GetMappedTypeNameTypeKind(tt) != MappedTypeNameTypeKind.Remapping)
            {
                return GetIndexedMappedTypeSubstitutedTypeOfContextualType(tt, name, nameType);
            }
            var result = GetTypeOfConcretePropertyOfContextualType(tt, name);
            if (result != null)
            {
                return result;
            }
            return GetTypeFromIndexInfosOfContextualType(tt, name, nameType);
        }, true /*noReductions*/);
    }

    // checker.go:30362
    internal Type? GetIndexedMappedTypeSubstitutedTypeOfContextualType(Type t, string name, Type? nameType)
    {
        var propertyNameType = nameType;
        if (propertyNameType == null)
        {
            propertyNameType = GetStringLiteralType(name);
        }
        var constraint = GetConstraintTypeFromMappedType(t);
        // special case for conditional types pretending to be negated types
        if ((t.AsMappedType().nameType != null && IsExcludedMappedPropertyName(t.AsMappedType().nameType!, propertyNameType)) || IsExcludedMappedPropertyName(constraint, propertyNameType))
        {
            return null;
        }
        var constraintOfConstraint = GetBaseConstraintOrType(constraint);
        if (!IsTypeAssignableTo(propertyNameType, constraintOfConstraint))
        {
            return null;
        }
        return SubstituteIndexedMappedType(t, propertyNameType);
    }

    // checker.go:30379
    internal List<Type>? AppendContextualPropertyTypeConstituent(List<Type>? types, Type? t)
    {
        // any doesn't provide any contextual information but could spoil the overall result by nullifying contextual information
        // provided by other intersection constituents so it gets replaced with `unknown` as `T & unknown` is just `T` and all
        // types computed based on the contextual information provided by other constituens are still assignable to any
        if (t == null)
        {
            return types;
        }
        if ((t.flags & TypeFlags.Any) != 0)
        {
            (types ??= []).Add(unknownType!);
            return types;
        }
        (types ??= []).Add(t);
        return types;
    }

    // checker.go:30391
    internal Type? GetTypeOfConcretePropertyOfContextualType(Type t, string name)
    {
        var prop = GetPropertyOfType(t, name);
        if (prop == null || IsCircularMappedProperty(prop))
        {
            return null;
        }
        return RemoveMissingType(GetTypeOfSymbol(prop), (prop.Flags & SymbolFlags.Optional) != 0);
    }

    // checker.go:30398
    internal Type? GetTypeFromIndexInfosOfContextualType(Type t, string name, Type? nameType)
    {
        if (IsTupleType(t) && IsNumericLiteralName(name) && global::Ts.JsNum.JsNum.FromString(name) >= 0)
        {
            var restType = GetElementTypeOfSliceOfTupleType(t, t.TargetTupleType().fixedLength, 0 /*endSkipCount*/, false /*writing*/, true /*noReductions*/);
            if (restType != null)
            {
                return restType;
            }
        }
        if (nameType == null)
        {
            nameType = GetStringLiteralType(name);
        }
        var indexInfo = FindApplicableIndexInfo(GetIndexInfosOfStructuredType(t), nameType);
        if (indexInfo == null)
        {
            return null;
        }
        return indexInfo.valueType;
    }

    // checker.go:30412
    internal bool IsExcludedMappedPropertyName(Type t, Type propertyNameType)
    {
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            return (GetReducedType(GetTrueTypeFromConditionalType(t)).flags & TypeFlags.Never) != 0 &&
                GetActualTypeVariable(GetFalseTypeFromConditionalType(t)) == GetActualTypeVariable(t.AsConditionalType().checkType!) &&
                IsTypeAssignableTo(propertyNameType, t.AsConditionalType().extendsType!);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            return CoreUtil.Some(t.Types(), tt => IsExcludedMappedPropertyName(tt, propertyNameType));
        }
        return false;
    }

    // checker.go:30425
    internal bool IsCircularMappedProperty(Symbol symbol)
    {
        if ((symbol.CheckFlags & CheckFlags.Mapped) != 0)
        {
            var links = valueSymbolLinks.Get(symbol);
            return links.resolvedType == null && FindResolutionCycleStartIndex(symbol, TypeSystemPropertyName.Type) >= 0;
        }
        return false;
    }

    // checker.go:30443 — Return the contextual type for a given expression node. During overload
    // resolution, a contextual type may temporarily be "pushed" onto a node.
    internal Type? GetApparentTypeOfContextualType(Node node, ContextFlags contextFlags)
    {
        Type? contextualType;
        if (AstUtilities.IsObjectLiteralMethod(node))
        {
            contextualType = GetContextualTypeForObjectLiteralMethod(node, contextFlags);
        }
        else
        {
            contextualType = GetContextualType(node, contextFlags);
        }
        var instantiatedType = InstantiateContextualType(contextualType, node, contextFlags);
        if (instantiatedType != null && !((contextFlags & ContextFlags.NoConstraints) != 0 && (instantiatedType.flags & TypeFlags.TypeVariable) != 0))
        {
            // mapTypeEx with a total mapping function never returns nil (checker.go treats result as non-null).
            var apparentType = MapTypeEx(instantiatedType, t =>
            {
                if ((t.objectFlags & ObjectFlags.Mapped) != 0)
                {
                    return t;
                }
                return GetApparentType(t);
            }, true)!;
            if ((apparentType.flags & TypeFlags.Union) != 0 && NodeTests.IsObjectLiteralExpression(node))
            {
                return DiscriminateContextualTypeByObjectMembers(node, apparentType);
            }
            if ((apparentType.flags & TypeFlags.Union) != 0 && NodeTests.IsJsxAttributes(node))
            {
                return DiscriminateContextualTypeByJSXAttributes(node, apparentType);
            }
            return apparentType;
        }
        return null;
    }

    // ===== contextual stack helpers (checker.go:30633/30637/30643) =====

    // checker.go:30633
    internal void PushContextualType(Node node, Type? t, bool isCache)
    {
        contextualInfos.Add(new ContextualInfo { node = node, t = t, isCache = isCache });
    }

    // checker.go:30637
    internal void PopContextualType()
    {
        var lastIndex = contextualInfos.Count - 1;
        contextualInfos[lastIndex] = new ContextualInfo();
        contextualInfos.RemoveAt(lastIndex);
    }

    // checker.go:30712
    internal void PushInferenceContext(Node node, InferenceContext? context)
    {
        inferenceContextInfos.Add(new InferenceContextInfo { node = node, context = context });
    }

    // checker.go:30716
    internal void PopInferenceContext()
    {
        var lastIndex = inferenceContextInfos.Count - 1;
        inferenceContextInfos[lastIndex] = new InferenceContextInfo();
        inferenceContextInfos.RemoveAt(lastIndex);
    }

    // checker.go:30722
    internal InferenceContext? GetInferenceContext(Node node)
    {
        for (var i = inferenceContextInfos.Count - 1; i >= 0; i--)
        {
            if (AstUtilities.IsNodeDescendantOf(node, inferenceContextInfos[i].node))
            {
                return inferenceContextInfos[i].context;
            }
        }
        return null;
    }

    // checker.go:30643
    internal int FindContextualNode(Node node, bool includeCaches)
    {
        for (var i = 0; i < contextualInfos.Count; i++)
        {
            var info = contextualInfos[i];
            if (node == info.node && (includeCaches || !info.isCache))
            {
                return i;
            }
        }
        return -1;
    }

    // checker.go:29880
    private (int, int) GetSpreadIndices(Node node)
    {
        var links = arrayLiteralLinks.Get(node);
        if (!links.indicesComputed)
        {
            var first = -1;
            var last = -1;
            var elements = node.Elements()!;
            for (var i = 0; i < elements.Length; i++)
            {
                if (NodeTests.IsSpreadElement(elements[i]))
                {
                    if (first < 0)
                    {
                        first = i;
                    }
                    last = i;
                }
            }
            links.firstSpreadIndex = first;
            links.lastSpreadIndex = last;
            links.indicesComputed = true;
        }
        return (links.firstSpreadIndex, links.lastSpreadIndex);
    }

    // checker.go:13547-13553
    internal bool IsConstContext(Node node)
    {
        var parent = node.Parent!;
        return AstUtilities.IsConstAssertion(parent) ||
            IsValidConstAssertionArgument(node) && IsConstTypeVariable(GetContextualType(node, ContextFlags.None), 0) ||
            (NodeTests.IsParenthesizedExpression(parent) || NodeTests.IsArrayLiteralExpression(parent) || NodeTests.IsSpreadElement(parent)) && IsConstContext(parent) ||
            (NodeTests.IsPropertyAssignment(parent) || NodeTests.IsShorthandPropertyAssignment(parent) || NodeTests.IsTemplateSpan(parent)) && IsConstContext(parent.Parent!);
    }

    // checker.go:13555-13575
    internal bool IsValidConstAssertionArgument(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ObjectLiteralExpression:
            case SyntaxKind.TemplateExpression:
                return true;
            case SyntaxKind.ParenthesizedExpression:
                return IsValidConstAssertionArgument(node.Expression()!);
            case SyntaxKind.PrefixUnaryExpression:
                var op = node.AsPrefixUnaryExpression().Operator;
                var arg = node.AsPrefixUnaryExpression().Operand!;
                return op == SyntaxKind.MinusToken && (arg.Kind == SyntaxKind.NumericLiteral || arg.Kind == SyntaxKind.BigIntLiteral) || op == SyntaxKind.PlusToken && arg.Kind == SyntaxKind.NumericLiteral;
            case SyntaxKind.PropertyAccessExpression:
            case SyntaxKind.ElementAccessExpression:
                var expr = AstUtilities.SkipParentheses(node.Expression()!);
                Symbol? symbol = null;
                if (AstUtilities.IsEntityNameExpression(expr))
                {
                    symbol = ResolveEntityName(expr, SymbolFlags.Value, true /*ignoreErrors*/, false, null);
                }
                return symbol != null && (symbol.Flags & SymbolFlags.Enum) != 0;
        }
        return false;
    }

    // checker.go:31795
    internal Type? GetThisTypeOfObjectLiteralFromContextualType(Node containingLiteral, Type? contextualType)
    {
        var literal = containingLiteral;
        var t = contextualType;
        while (t != null)
        {
            var thisType = GetThisTypeFromContextualType(t);
            if (thisType != null)
            {
                return thisType;
            }
            if (literal.Parent!.Kind != SyntaxKind.PropertyAssignment)
            {
                break;
            }
            literal = literal.Parent.Parent!;
            t = GetApparentTypeOfContextualType(literal, ContextFlags.None);
        }
        return null;
    }

    // checker.go:31812
    internal Type? GetThisTypeFromContextualType(Type t)
    {
        return MapTypeEx(t, tt =>
        {
            if ((tt.flags & TypeFlags.Intersection) != 0)
            {
                foreach (var ti in tt.AsIntersectionType().types)
                {
                    var typeArg = GetThisTypeArgument(ti);
                    if (typeArg != null)
                    {
                        return typeArg;
                    }
                }
                return null;
            }
            else
            {
                return GetThisTypeArgument(tt);
            }
        }, false /*noReductions*/);
    }

    // checker.go:31827
    internal Type? GetThisTypeArgument(Type t)
    {
        if ((t.objectFlags & ObjectFlags.Reference) != 0 && t.AsTypeReference()!.target == globalThisType)
        {
            return GetTypeArguments(t)[0];
        }
        return null;
    }
}
