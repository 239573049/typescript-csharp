// Ported from tsgo internal/checker/checker.go (section: signature construction / return types /
// widening / anonymous member resolution, lines 19741-20657). plan/03 §4.2 #13.
//
// getSignatureFromDeclaration(19741), getReturnTypeOfSignature(19906), widening(20328),
// resolveAnonymousTypeMembers(20555).
//
// §1.5: cross-package classes fully qualified; CoreUtil / NodeTests / AstUtilities / InternalSymbolName
// direct; diagnostics via the Diag alias.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:19741-19815
    internal Signature GetSignatureFromDeclaration(Node declaration)
    {
        var links = signatureLinks.Get(declaration);
        if (links.resolvedSignature is not null)
        {
            return links.resolvedSignature;
        }
        List<Symbol> parameters = [];
        var flags = SignatureFlags.None;
        Symbol? thisParameter = null;
        var minArgumentCount = 0;
        var hasThisParameter = false;
        var iife = AstUtilities.GetImmediatelyInvokedFunctionExpression(declaration);
        var isUntypedSignatureInJSFile = iife is null &&
            AstUtilities.IsInJSFile(declaration) &&
            (NodeTests.IsFunctionExpression(declaration) || NodeTests.IsArrowFunction(declaration) || AstUtilities.IsMethodOrAccessor(declaration) || NodeTests.IsFunctionDeclaration(declaration) || NodeTests.IsConstructorDeclaration(declaration)) &&
            CoreUtil.Every(declaration.Parameters(), param => param.Type() is null) &&
            GetContextualType(declaration, ContextFlags.Signature) is null;
        if (isUntypedSignatureInJSFile)
        {
            flags |= SignatureFlags.IsUntypedSignatureInJSFile;
        }
        var declParams = declaration.Parameters();
        for (int i = 0; i < declParams.Length; i++)
        {
            var param = declParams[i];
            var paramSymbol = param.Symbol();
            var typeNode = param.Type();
            // Include parameter symbol instead of property symbol in the signature
            if (paramSymbol is not null && (paramSymbol.Flags & SymbolFlags.Property) != 0 && !AstUtilities.IsBindingPattern(param.Name()))
            {
                var resolvedSymbol = resolveName(param, paramSymbol.Name, SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
                paramSymbol = resolvedSymbol;
            }
            if (i == 0 && paramSymbol!.Name == InternalSymbolName.This)
            {
                hasThisParameter = true;
                thisParameter = param.Symbol();
            }
            else
            {
                parameters.Add(paramSymbol!);
            }
            if (typeNode is not null && typeNode.Kind == SyntaxKind.LiteralType)
            {
                flags |= SignatureFlags.HasLiteralTypes;
            }
            // Record a new minimum argument count if this is not an optional parameter
            var isOptionalParameter = IsOptionalDeclaration(param) ||
                param.Initializer() is not null ||
                IsRestParameter(param) ||
                (iife is not null && parameters.Count > iife.Arguments()!.Length && typeNode is null);
            if (!isOptionalParameter)
            {
                minArgumentCount = parameters.Count;
            }
        }
        // If only one accessor includes a this-type annotation, the other behaves as if it had the same type annotation
        if ((NodeTests.IsGetAccessorDeclaration(declaration) || NodeTests.IsSetAccessorDeclaration(declaration)) && HasBindableName(declaration) && (!hasThisParameter || thisParameter is null))
        {
            var otherKind = CoreUtil.IfElse(NodeTests.IsGetAccessorDeclaration(declaration), SyntaxKind.SetAccessor, SyntaxKind.GetAccessor);
            var other = AstUtilities.GetDeclarationOfKind(GetSymbolOfDeclaration(declaration)!, otherKind);
            if (other is not null)
            {
                thisParameter = GetAnnotatedAccessorThisParameter(other);
            }
        }
        Type? classType = null;
        if (NodeTests.IsConstructorDeclaration(declaration))
        {
            classType = GetDeclaredTypeOfClassOrInterface(GetMergedSymbol(declaration.Parent!.Symbol())!);
        }
        Type[]? typeParameters;
        if (classType is not null)
        {
            typeParameters = classType.AsInterfaceType()!.LocalTypeParameters();
        }
        else
        {
            typeParameters = GetTypeParametersFromDeclaration(declaration);
        }
        if (HasRestParameterFree(declaration))
        {
            flags |= SignatureFlags.HasRestParameter;
        }
        if (NodeTests.IsConstructorTypeNode(declaration) || NodeTests.IsConstructorDeclaration(declaration) || NodeTests.IsConstructSignatureDeclaration(declaration))
        {
            flags |= SignatureFlags.Construct;
        }
        if ((NodeTests.IsConstructorTypeNode(declaration) && AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Abstract)) ||
            (NodeTests.IsConstructorDeclaration(declaration) && AstUtilities.HasSyntacticModifier(declaration.Parent!, ModifierFlags.Abstract)))
        {
            flags |= SignatureFlags.Abstract;
        }
        links.resolvedSignature = NewSignature(flags, declaration, typeParameters, thisParameter, parameters.ToArray(), null /*resolvedReturnType*/, null /*resolvedTypePredicate*/, minArgumentCount);
        return links.resolvedSignature;
    }

    // checker.go:19817-19826
    internal Type[] GetTypeParametersFromDeclaration(Node declaration)
    {
        var sig = GetSignatureOfFullSignatureType(declaration);
        if (sig is not null)
        {
            return sig.TypeParameters() ?? [];
        }
        Type[]? result = null;
        foreach (var node in declaration.TypeParameters() ?? [])
        {
            result = CoreUtil.AppendIfUnique(result, GetDeclaredTypeOfTypeParameter(node.Symbol()!));
        }
        return result ?? [];
    }

    // checker.go:19828-19834
    internal Symbol? GetAnnotatedAccessorThisParameter(Node accessor)
    {
        var parameter = GetAccessorThisParameter(accessor);
        if (parameter is not null)
        {
            return parameter.Symbol();
        }
        return null;
    }

    // checker.go:19836-19841
    internal Node? GetAccessorThisParameter(Node accessor)
    {
        if (accessor.Parameters().Length == CoreUtil.IfElse(NodeTests.IsGetAccessorDeclaration(accessor), 1, 2))
        {
            return AstUtilities.GetThisParameter(accessor);
        }
        return null;
    }

    // Indicates whether a declaration has an early-bound name or a dynamic name that can be late-bound.
    // checker.go:19846-19848
    internal bool HasBindableName(Node node) => !AstUtilities.HasDynamicName(node) || HasLateBindableName(node);

    // Indicates whether a declaration has a late-bindable dynamic name.
    // checker.go:19853-19856
    internal bool HasLateBindableName(Node node)
    {
        var name = AstUtilities.GetNameOfDeclaration(node);
        return name is not null && IsLateBindableName(name);
    }

    // Indicates whether a declaration name is definitely late-bindable.
    // checker.go:19866-19874
    internal bool IsLateBindableName(Node node)
    {
        if (!IsLateBindableAST(node))
        {
            return false;
        }
        if (NodeTests.IsComputedPropertyName(node))
        {
            return IsTypeUsableAsPropertyName(CheckComputedPropertyName(node));
        }
        return IsTypeUsableAsPropertyName(CheckExpressionCached(node.AsElementAccessExpression().ArgumentExpression!));
    }

    // checker.go:19876-19879
    internal bool HasLateBindableIndexSignature(Node node)
    {
        var name = AstUtilities.GetNameOfDeclaration(node);
        return name is not null && IsLateBindableIndexSignature(name);
    }

    // checker.go:19881-19889
    internal bool IsLateBindableIndexSignature(Node node)
    {
        if (!IsLateBindableAST(node))
        {
            return false;
        }
        if (NodeTests.IsComputedPropertyName(node))
        {
            return IsTypeUsableAsIndexSignatureDeclaration(CheckComputedPropertyName(node));
        }
        return IsTypeUsableAsIndexSignatureDeclaration(CheckExpressionCached(node.AsElementAccessExpression().ArgumentExpression!));
    }

    // checker.go:19891-19893
    internal bool IsTypeUsableAsIndexSignatureDeclaration(Type t) => IsTypeAssignableTo(t, stringNumberSymbolType!);

    // checker.go:19895-19904
    internal static bool IsLateBindableAST(Node node)
    {
        Node? expr = null;
        if (NodeTests.IsComputedPropertyName(node))
        {
            expr = node.Expression();
        }
        else if (NodeTests.IsElementAccessExpression(node))
        {
            expr = node.AsElementAccessExpression().ArgumentExpression;
        }
        return expr is not null && AstUtilities.IsEntityNameExpression(expr);
    }

    // checker.go:19906-19954
    internal Type GetReturnTypeOfSignature(Signature sig)
    {
        if (sig.resolvedReturnType is not null)
        {
            return sig.resolvedReturnType;
        }
        if (!PushTypeResolution(sig, TypeSystemPropertyName.ResolvedReturnType))
        {
            return errorType!;
        }
        Type t;
        if (sig.target is not null)
        {
            t = InstantiateType(GetReturnTypeOfSignature(sig.target), sig.mapper);
        }
        else if (sig.composite is not null)
        {
            t = InstantiateType(GetUnionOrIntersectionType(CoreUtil.Map(sig.composite.signatures, GetReturnTypeOfSignature), sig.composite.isUnion, UnionReduction.Subtype), sig.mapper);
        }
        else
        {
            t = GetReturnTypeFromAnnotation(sig.declaration!)!;
            if (t is null)
            {
                if (!AstUtilities.NodeIsMissing(sig.declaration!.Body()))
                {
                    t = GetReturnTypeFromBody(sig.declaration!, CheckMode.Normal);
                }
                else
                {
                    t = anyType!;
                }
            }
        }
        if ((sig.flags & SignatureFlags.IsInnerCallChain) != 0)
        {
            t = AddOptionalTypeMarker(t);
        }
        else if ((sig.flags & SignatureFlags.IsOuterCallChain) != 0)
        {
            t = GetOptionalType(t, false /*isProperty*/);
        }
        if (!PopTypeResolution())
        {
            if (sig.declaration is not null)
            {
                var typeNode = sig.declaration.Type();
                if (typeNode is not null)
                {
                    Error(typeNode, Diag.Return_type_annotation_circularly_references_itself);
                }
                else if (noImplicitAny)
                {
                    var name = AstUtilities.GetNameOfDeclaration(sig.declaration);
                    if (name is not null)
                    {
                        Error(name, Diag.X_0_implicitly_has_return_type_any_because_it_does_not_have_a_return_type_annotation_and_is_referenced_directly_or_indirectly_in_one_of_its_return_expressions, global::Ts.Scanner.Scanner.DeclarationNameToString(name));
                    }
                    else
                    {
                        Error(sig.declaration, Diag.Function_implicitly_has_return_type_any_because_it_does_not_have_a_return_type_annotation_and_is_referenced_directly_or_indirectly_in_one_of_its_return_expressions);
                    }
                }
            }
            t = anyType!;
        }
        sig.resolvedReturnType ??= t;
        return sig.resolvedReturnType;
    }

    // checker.go:19956-19961
    internal Type GetNonCircularReturnTypeOfSignature(Signature sig)
    {
        if (IsResolvingReturnTypeOfSignature(sig))
        {
            return anyType!;
        }
        return GetReturnTypeOfSignature(sig);
    }

    // checker.go:19963-19975
    internal Type? GetReturnTypeFromAnnotation(Node declaration)
    {
        if (NodeTests.IsConstructorDeclaration(declaration))
        {
            return GetDeclaredTypeOfClassOrInterface(GetMergedSymbol(declaration.Parent!.Symbol())!);
        }
        var returnType = declaration.Type();
        if (returnType is not null)
        {
            return GetTypeFromTypeNode(returnType);
        }
        if (NodeTests.IsGetAccessorDeclaration(declaration) && HasBindableName(declaration))
        {
            return GetAnnotatedAccessorType(AstUtilities.GetDeclarationOfKind(GetSymbolOfDeclaration(declaration)!, SyntaxKind.SetAccessor));
        }
        return GetReturnTypeOfFullSignature(declaration);
    }

    // checker.go:19977-19982
    internal Signature? GetSignatureOfFullSignatureType(Node node)
    {
        if (AstUtilities.IsInJSFile(node) && (NodeTests.IsFunctionDeclaration(node) || NodeTests.IsMethodDeclaration(node) || AstUtilities.IsFunctionExpressionOrArrowFunction(node)) && node.FunctionLikeData()?.FullSignature is not null)
        {
            return GetSingleCallSignature(GetTypeFromTypeNode(node.FunctionLikeData()!.FullSignature!));
        }
        return null;
    }

    // checker.go:19984-19994
    internal Type? GetParameterTypeOfFullSignature(Node node, Node parameter)
    {
        var signature = GetSignatureOfFullSignatureType(node);
        if (signature is not null)
        {
            var pos = Array.IndexOf(node.Parameters(), parameter);
            if (parameter.AsParameterDeclaration().DotDotDotToken is not null)
            {
                return GetRestTypeAtPosition(signature, pos, false /*readonly*/);
            }
            else
            {
                return GetTypeAtPosition(signature, pos);
            }
        }
        return null;
    }

    // checker.go:19996-20001
    internal Type? GetReturnTypeOfFullSignature(Node node)
    {
        var signature = GetSignatureOfFullSignatureType(node);
        if (signature is not null)
        {
            return GetReturnTypeOfSignature(signature);
        }
        return null;
    }

    // checker.go:20003-20009
    internal Type? GetAnnotatedAccessorType(Node? accessor)
    {
        var node = GetAnnotatedAccessorTypeNode(accessor);
        if (node is not null)
        {
            return GetTypeFromTypeNode(node);
        }
        return null;
    }

    // checker.go:20011-20021
    internal Node? GetAnnotatedAccessorTypeNode(Node? accessor)
    {
        if (accessor is not null)
        {
            switch (accessor.Kind)
            {
                case SyntaxKind.GetAccessor:
                case SyntaxKind.PropertyDeclaration:
                    return accessor.Type();
                case SyntaxKind.SetAccessor:
                    return GetEffectiveSetAccessorTypeAnnotationNode(accessor);
            }
        }
        return null;
    }

    // checker.go:20023-20029
    internal static Node? GetEffectiveSetAccessorTypeAnnotationNode(Node node)
    {
        var param = GetSetAccessorValueParameter(node);
        if (param is not null)
        {
            return param.Type();
        }
        return null;
    }

    // checker.go:20031-20161
    internal Type GetReturnTypeFromBody(Node fn, CheckMode checkMode)
    {
        var body = fn.Body();
        if (body is null)
        {
            return errorType!;
        }
        var functionFlags = AstUtilities.GetFunctionFlags(fn);
        var isAsync = (functionFlags & FunctionFlags.Async) != 0;
        var isGenerator = (functionFlags & FunctionFlags.Generator) != 0;
        Type? returnType = null;
        Type? yieldType = null;
        Type? nextType = null;
        var fallbackReturnType = voidType!;
        if (!NodeTests.IsBlock(body))
        {
            returnType = CheckExpressionCachedEx(body, checkMode & ~CheckMode.SkipGenericFunctions);
            if (IsConstContext(body))
            {
                returnType = GetRegularTypeOfLiteralType(returnType);
            }
            if (isAsync)
            {
                // From within an async function you can return either a non-promise value or a promise.
                returnType = UnwrapAwaitedType(CheckAwaitedType(returnType, false /*withAlias*/, fn /*errorNode*/, Diag.The_return_type_of_an_async_function_must_either_be_a_valid_promise_or_must_not_contain_a_callable_then_member));
            }
        }
        else if (isGenerator)
        {
            var (returnTypes, isNeverReturning) = CheckAndAggregateReturnExpressionTypes(fn, checkMode);
            if (isNeverReturning)
            {
                fallbackReturnType = neverType!;
            }
            else if (returnTypes.Length != 0)
            {
                returnType = GetUnionTypeEx(returnTypes, UnionReduction.Subtype, null, null);
            }
            var (yieldTypes, nextTypes) = CheckAndAggregateYieldOperandTypes(fn, checkMode);
            if (yieldTypes.Length != 0)
            {
                yieldType = GetUnionTypeEx(yieldTypes, UnionReduction.Subtype, null, null);
            }
            if (nextTypes.Length != 0)
            {
                nextType = GetIntersectionType(nextTypes);
            }
        }
        else
        {
            var (types, isNeverReturning) = CheckAndAggregateReturnExpressionTypes(fn, checkMode);
            if (isNeverReturning)
            {
                // For an async function, the return type will not be never, but rather a Promise for never.
                if ((functionFlags & FunctionFlags.Async) != 0)
                {
                    return CreatePromiseReturnType(fn, neverType!);
                }
                // Normal function
                return neverType!;
            }
            if (types.Length == 0)
            {
                // For an async function, the return type will not be void/undefined, but rather a Promise for void/undefined.
                var contextualReturnType = GetContextualReturnType(fn, ContextFlags.None);
                Type returnType2;
                if (contextualReturnType is not null && SomeType(CoreUtil.OrElse(UnwrapReturnType(contextualReturnType, functionFlags), voidType!), t => (t.flags & TypeFlags.Undefined) != 0))
                {
                    returnType2 = undefinedType!;
                }
                else
                {
                    returnType2 = voidType!;
                }
                if ((functionFlags & FunctionFlags.Async) != 0)
                {
                    return CreatePromiseReturnType(fn, returnType2);
                }
                // Normal function
                return returnType2;
            }
            // Return a union of the return expression types.
            returnType = GetUnionTypeEx(types, UnionReduction.Subtype, null, null);
        }
        if (returnType is not null || yieldType is not null || nextType is not null)
        {
            if (yieldType is not null)
            {
                ReportErrorsFromWidening(fn, yieldType, WideningKind.GeneratorYield);
            }
            if (returnType is not null)
            {
                ReportErrorsFromWidening(fn, returnType, WideningKind.FunctionReturn);
            }
            if (nextType is not null)
            {
                ReportErrorsFromWidening(fn, nextType, WideningKind.GeneratorNext);
            }
            if ((returnType is not null && IsUnitType(returnType)) || (yieldType is not null && IsUnitType(yieldType)) || (nextType is not null && IsUnitType(nextType)))
            {
                var contextualSignature = GetContextualSignatureForFunctionLikeDeclaration(fn);
                Type? contextualType = null;
                if (contextualSignature is null)
                {
                    // No contextual type
                }
                else if (contextualSignature == GetSignatureFromDeclaration(fn))
                {
                    if (!isGenerator)
                    {
                        contextualType = returnType;
                    }
                }
                else
                {
                    contextualType = InstantiateContextualType(GetReturnTypeOfSignature(contextualSignature), fn, ContextFlags.None);
                }
                if (isGenerator)
                {
                    yieldType = GetWidenedLiteralLikeTypeForContextualIterationTypeIfNeeded(yieldType, contextualType, IterationTypeKind.Yield, isAsync);
                    returnType = GetWidenedLiteralLikeTypeForContextualIterationTypeIfNeeded(returnType, contextualType, IterationTypeKind.Return, isAsync);
                    nextType = GetWidenedLiteralLikeTypeForContextualIterationTypeIfNeeded(nextType, contextualType, IterationTypeKind.Next, isAsync);
                }
                else
                {
                    returnType = GetWidenedLiteralLikeTypeForContextualReturnTypeIfNeeded(returnType, contextualType, isAsync);
                }
            }
            if (yieldType is not null)
            {
                yieldType = GetWidenedType(yieldType);
            }
            if (returnType is not null)
            {
                returnType = GetWidenedType(returnType);
            }
            if (nextType is not null)
            {
                nextType = GetWidenedType(nextType);
            }
        }
        returnType ??= fallbackReturnType;
        if (isGenerator)
        {
            yieldType ??= neverType!;
            if (nextType is null)
            {
                nextType = GetContextualIterationType(IterationTypeKind.Next, fn);
                nextType ??= unknownType!;
            }
            return CreateGeneratorType(yieldType, returnType, nextType, isAsync);
        }
        // From within an async function you can return either a non-promise value or a promise.
        if (isAsync)
        {
            return CreatePromiseType(returnType);
        }
        return returnType;
    }

    // Returns the aggregated list of return types, plus a bool indicating a never-returning function.
    // checker.go:20164-20210
    internal (Type[], bool) CheckAndAggregateReturnExpressionTypes(Node fn, CheckMode checkMode)
    {
        var functionFlags = AstUtilities.GetFunctionFlags(fn);
        Type[]? aggregatedTypes = null;
        var hasReturnWithNoExpression = FunctionHasImplicitReturn(fn);
        var hasReturnOfTypeNever = false;
        ForEachReturnStatement(fn.Body()!, returnStatement =>
        {
            var expr = returnStatement.Expression();
            if (expr is null)
            {
                hasReturnWithNoExpression = true;
                return false;
            }
            expr = AstUtilities.SkipParentheses(expr);
            // Bare calls to this same function don't contribute to inference; `return await` is also safe to unwrap here
            if ((functionFlags & FunctionFlags.Async) != 0 && NodeTests.IsAwaitExpression(expr))
            {
                expr = AstUtilities.SkipParentheses(expr.Expression()!);
            }
            if (NodeTests.IsCallExpression(expr) && NodeTests.IsIdentifier(expr.Expression()!) && CheckExpressionCached(expr.Expression()!).symbol == GetMergedSymbol(fn.Symbol()) &&
                (!AstUtilities.IsFunctionExpressionOrArrowFunction(fn.Symbol()!.ValueDeclaration!) || IsConstantReference(expr.Expression()!)))
            {
                hasReturnOfTypeNever = true;
                return false;
            }
            var t = CheckExpressionCachedEx(expr, checkMode & ~CheckMode.SkipGenericFunctions);
            if ((functionFlags & FunctionFlags.Async) != 0)
            {
                // From within an async function you can return either a non-promise value or a promise.
                t = UnwrapAwaitedType(CheckAwaitedType(t, false /*withAlias*/, fn, Diag.The_return_type_of_an_async_function_must_either_be_a_valid_promise_or_must_not_contain_a_callable_then_member));
            }
            if ((t.flags & TypeFlags.Never) != 0)
            {
                hasReturnOfTypeNever = true;
            }
            if (IsConstContext(expr))
            {
                t = GetRegularTypeOfLiteralType(t);
            }
            aggregatedTypes = CoreUtil.AppendIfUnique(aggregatedTypes, t);
            return false;
        });
        if ((aggregatedTypes ?? []).Length == 0 && !hasReturnWithNoExpression && (hasReturnOfTypeNever || MayReturnNever(fn)))
        {
            return ([], true);
        }
        if (strictNullChecks && (aggregatedTypes ?? []).Length != 0 && hasReturnWithNoExpression)
        {
            aggregatedTypes = CoreUtil.AppendIfUnique(aggregatedTypes, undefinedType!);
        }
        return (aggregatedTypes ?? [], false);
    }

    // checker.go:20212-20215
    internal bool FunctionHasImplicitReturn(Node fn)
    {
        var endFlowNode = fn.BodyData()?.EndFlowNode;
        return endFlowNode is not null && IsReachableFlowNode(endFlowNode);
    }

    // checker.go:20217-20225
    internal static bool MayReturnNever(Node fn)
    {
        switch (fn.Kind)
        {
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                return true;
            case SyntaxKind.MethodDeclaration:
                return NodeTests.IsObjectLiteralExpression(fn.Parent!);
        }
        return false;
    }

    // checker.go:20227-20251
    internal (Type[] yieldTypes, Type[] nextTypes) CheckAndAggregateYieldOperandTypes(Node fn, CheckMode checkMode)
    {
        var isAsync = (AstUtilities.GetFunctionFlags(fn) & FunctionFlags.Async) != 0;
        Type[]? yieldTypes = null;
        Type[]? nextTypes = null;
        ForEachYieldExpression(fn.Body()!, yieldExpr =>
        {
            var yieldExprType = undefinedWideningType!;
            if (yieldExpr.Expression() is not null)
            {
                yieldExprType = CheckExpressionEx(yieldExpr.Expression()!, checkMode & ~CheckMode.SkipGenericFunctions);
            }
            if (yieldExpr.Expression() is not null && IsConstContext(yieldExpr.Expression()!))
            {
                yieldExprType = GetRegularTypeOfLiteralType(yieldExprType);
            }
            yieldTypes = CoreUtil.AppendIfUnique(yieldTypes, GetYieldedTypeOfYieldExpression(yieldExpr, yieldExprType, anyType!, isAsync));
            Type? nextType;
            if (yieldExpr.AsYieldExpression().AsteriskToken is not null)
            {
                var iterationTypes = GetIterationTypesOfIterable(yieldExprType, CoreUtil.IfElse(isAsync, IterationUse.AsyncYieldStar, IterationUse.YieldStar), yieldExpr.Expression());
                nextType = iterationTypes.NextType;
            }
            else
            {
                nextType = GetContextualType(yieldExpr, ContextFlags.None);
            }
            if (nextType is not null)
            {
                nextTypes = CoreUtil.AppendIfUnique(nextTypes, nextType);
            }
            return false;
        });
        return (yieldTypes ?? [], nextTypes ?? []);
    }

    // checker.go:20253-20263
    internal Type CreatePromiseType(Type promisedType)
    {
        // creates a `Promise<T>` type where `T` is the promisedType argument
        var globalPromiseType = getGlobalPromiseTypeChecked();
        if (globalPromiseType != emptyGenericType)
        {
            // Unwrap an `Awaited<T>` to `T` to improve inference.
            promisedType = GetAwaitedTypeNoAlias(UnwrapAwaitedType(promisedType)) ?? unknownType!;
            return CreateTypeReference(globalPromiseType, [promisedType]);
        }
        return unknownType!;
    }

    // checker.go:20265-20275
    internal Type CreatePromiseLikeType(Type promisedType)
    {
        // creates a `PromiseLike<T>` type where `T` is the promisedType argument
        var globalPromiseLikeType = getGlobalPromiseLikeType();
        if (globalPromiseLikeType != emptyGenericType)
        {
            promisedType = GetAwaitedTypeNoAlias(UnwrapAwaitedType(promisedType)) ?? unknownType!;
            return CreateTypeReference(globalPromiseLikeType, [promisedType]);
        }
        return unknownType!;
    }

    // checker.go:20277-20291
    internal Type CreatePromiseReturnType(Node fn, Type promisedType)
    {
        var promiseType = CreatePromiseType(promisedType);
        if (promiseType == unknownType)
        {
            Error(fn, CoreUtil.IfElse(IsImportCall(fn),
                Diag.A_dynamic_import_call_returns_a_Promise_Make_sure_you_have_a_declaration_for_Promise_or_include_ES2015_in_your_lib_option,
                Diag.An_async_function_or_method_must_return_a_Promise_Make_sure_you_have_a_declaration_for_Promise_or_include_ES2015_in_your_lib_option));
            return errorType!;
        }
        if (getGlobalPromiseConstructorSymbol() is null)
        {
            Error(fn, CoreUtil.IfElse(IsImportCall(fn),
                Diag.A_dynamic_import_call_in_ES5_requires_the_Promise_constructor_Make_sure_you_have_a_declaration_for_the_Promise_constructor_or_include_ES2015_in_your_lib_option,
                Diag.An_async_function_or_method_in_ES5_requires_the_Promise_constructor_Make_sure_you_have_a_declaration_for_the_Promise_constructor_or_include_ES2015_in_your_lib_option));
        }
        return promiseType;
    }

    // checker.go:20293-20310
    internal Type UnwrapReturnType(Type returnType, FunctionFlags functionFlags)
    {
        var isGenerator = (functionFlags & FunctionFlags.Generator) != 0;
        var isAsync = (functionFlags & FunctionFlags.Async) != 0;
        if (isGenerator)
        {
            var returnIterationType = GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Return, returnType, isAsync);
            if (returnIterationType is null)
            {
                return errorType!;
            }
            if (isAsync)
            {
                return GetAwaitedTypeNoAlias(UnwrapAwaitedType(returnIterationType))!;
            }
            return returnIterationType;
        }
        if (isAsync)
        {
            return GetAwaitedTypeNoAlias(returnType) ?? errorType!;
        }
        return returnType;
    }

    // checker.go:20312-20326
    internal Type? GetWidenedLiteralLikeTypeForContextualReturnTypeIfNeeded(Type? t, Type? contextualSignatureReturnType, bool isAsync)
    {
        if (t is not null && IsUnitType(t))
        {
            Type? contextualType = null;
            if (contextualSignatureReturnType is null)
            {
                // No contextual type
            }
            else if (isAsync)
            {
                contextualType = GetPromisedTypeOfPromise(contextualSignatureReturnType);
            }
            else
            {
                contextualType = contextualSignatureReturnType;
            }
            t = GetWidenedLiteralLikeTypeForContextualType(t, contextualType);
        }
        return t;
    }

    // checker.go:20328-20337
    internal Type? GetWidenedLiteralLikeTypeForContextualIterationTypeIfNeeded(Type? t, Type? contextualSignatureReturnType, IterationTypeKind kind, bool isAsyncGenerator)
    {
        if (t is not null && IsUnitType(t))
        {
            Type? contextualType = null;
            if (contextualSignatureReturnType is not null)
            {
                contextualType = GetIterationTypeOfGeneratorFunctionReturnType(kind, contextualSignatureReturnType, isAsyncGenerator);
            }
            t = GetWidenedLiteralLikeTypeForContextualType(t, contextualType);
        }
        return t;
    }

    // checker.go:20339-20355
    internal Type CreateGeneratorType(Type yieldType, Type returnType, Type nextType, bool isAsyncGenerator)
    {
        var resolver = CoreUtil.IfElse(isAsyncGenerator, asyncIterationTypesResolver!, syncIterationTypesResolver!);
        var globalGeneratorType = resolver.getGlobalGeneratorType();
        yieldType = CoreUtil.OrElse(resolver.resolveIterationType(yieldType, null /*errorNode*/), unknownType!);
        returnType = CoreUtil.OrElse(resolver.resolveIterationType(returnType, null /*errorNode*/), unknownType!);
        if (globalGeneratorType == emptyGenericType)
        {
            // Fall back to the global IterableIterator type.
            var globalIterableIteratorType = resolver.getGlobalIterableIteratorType();
            if (globalIterableIteratorType != emptyGenericType)
            {
                return CreateTypeFromGenericGlobalType(globalIterableIteratorType, [yieldType, returnType, nextType]);
            }
            // The global Generator type doesn't exist, so report an error
            resolver.getGlobalIterableIteratorTypeChecked();
            return emptyObjectType!;
        }
        return CreateTypeFromGenericGlobalType(globalGeneratorType, [yieldType, returnType, nextType]);
    }

    // checker.go:20357-20366
    internal void ReportErrorsFromWidening(Node declaration, Type t, WideningKind wideningKind)
    {
        if (noImplicitAny && (t.objectFlags & ObjectFlags.ContainsWideningType) != 0)
        {
            if (wideningKind == WideningKind.Normal || (AstUtilities.IsFunctionLikeDeclaration(declaration) && ShouldReportErrorsFromWideningWithContextualSignature(declaration, wideningKind)))
            {
                // Report implicit any error within type if possible, otherwise report error on declaration
                if (!ReportWideningErrorsInType(t))
                {
                    ReportImplicitAny(declaration, t, wideningKind);
                }
            }
        }
    }

    // checker.go:20368-20391
    internal bool ShouldReportErrorsFromWideningWithContextualSignature(Node declaration, WideningKind wideningKind)
    {
        var signature = GetContextualSignatureForFunctionLikeDeclaration(declaration);
        if (signature is null)
        {
            return true;
        }
        var returnType = GetReturnTypeOfSignature(signature);
        var flags = AstUtilities.GetFunctionFlags(declaration);
        switch (wideningKind)
        {
            case WideningKind.FunctionReturn:
                if ((flags & FunctionFlags.Generator) != 0)
                {
                    returnType = GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Return, returnType, (flags & FunctionFlags.Async) != 0) ?? returnType;
                }
                else if ((flags & FunctionFlags.Async) != 0)
                {
                    returnType = GetAwaitedTypeNoAlias(returnType) ?? returnType;
                }
                return IsGenericType(returnType);
            case WideningKind.GeneratorYield:
                var yieldType = GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Yield, returnType, (flags & FunctionFlags.Async) != 0);
                return yieldType is not null && IsGenericType(yieldType);
            case WideningKind.GeneratorNext:
                var nextType = GetIterationTypeOfGeneratorFunctionReturnType(IterationTypeKind.Next, returnType, (flags & FunctionFlags.Async) != 0);
                return nextType is not null && IsGenericType(nextType);
        }
        return false;
    }

    // Reports implicit any errors that occur as a result of widening 'null' and 'undefined' to 'any'.
    // checker.go:20402-20438
    internal bool ReportWideningErrorsInType(Type t)
    {
        var errorReported = false;
        if ((t.objectFlags & ObjectFlags.ContainsWideningType) != 0)
        {
            if ((t.flags & TypeFlags.Union) != 0)
            {
                if (CoreUtil.Some(t.Types(), IsEmptyObjectType))
                {
                    errorReported = true;
                }
                else
                {
                    foreach (var s in t.Types())
                    {
                        errorReported = errorReported || ReportWideningErrorsInType(s);
                    }
                }
            }
            else if (IsArrayOrTupleType(t))
            {
                foreach (var s in GetTypeArguments(t))
                {
                    errorReported = errorReported || ReportWideningErrorsInType(s);
                }
            }
            else if (IsObjectLiteralType(t))
            {
                foreach (var p in GetPropertiesOfObjectType(t))
                {
                    var s = GetTypeOfSymbol(p);
                    if ((s.objectFlags & ObjectFlags.ContainsWideningType) != 0)
                    {
                        errorReported = ReportWideningErrorsInType(s);
                        if (!errorReported)
                        {
                            // we need to account for property types coming from object literal type normalization in unions
                            var valueDeclaration = CoreUtil.Find(p.Declarations, d =>
                            {
                                var valueDeclaration = d.Symbol()!.ValueDeclaration;
                                return valueDeclaration is not null && valueDeclaration.Parent == t.symbol!.ValueDeclaration;
                            });
                            if (valueDeclaration is not null)
                            {
                                Error(valueDeclaration, Diag.Object_literal_s_property_0_implicitly_has_an_1_type, SymbolToString(p), TypeToString(GetWidenedType(s)));
                                errorReported = true;
                            }
                        }
                    }
                }
            }
        }
        return errorReported;
    }

    // checker.go:20440-20468
    internal TypePredicate? GetTypePredicateFromBody(Node fn)
    {
        switch (fn.Kind)
        {
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return null;
        }
        var functionFlags = AstUtilities.GetFunctionFlags(fn);
        if (functionFlags != FunctionFlags.Normal)
        {
            return null;
        }
        // Only attempt to infer a type predicate if there's exactly one return.
        Node? singleReturn = null;
        var body = fn.Body();
        if (body is not null && !NodeTests.IsBlock(body))
        {
            // arrow function
            singleReturn = body;
        }
        else
        {
            var bailedEarly = ForEachReturnStatement(body!, returnStatement =>
            {
                if (singleReturn is not null || returnStatement.Expression() is null)
                {
                    return true;
                }
                singleReturn = returnStatement.Expression();
                return false;
            });
            if (bailedEarly || singleReturn is null || FunctionHasImplicitReturn(fn))
            {
                return null;
            }
        }
        return CheckIfExpressionRefinesAnyParameter(fn, singleReturn);
    }

    // checker.go:20470-20488
    internal TypePredicate? CheckIfExpressionRefinesAnyParameter(Node fn, Node expr)
    {
        expr = AstUtilities.SkipParentheses(expr);
        var returnType = CheckExpressionCached(expr);
        if ((returnType.flags & TypeFlags.Boolean) == 0)
        {
            return null;
        }
        var parameters = fn.Parameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var initType = GetTypeOfSymbol(param.Symbol()!);
            if (initType is null || (initType.flags & TypeFlags.Boolean) != 0 || !NodeTests.IsIdentifier(param.Name()!) || IsSymbolAssigned(param.Symbol()!) || IsRestParameter(param))
            {
                // Refining "x: boolean" to "x is true" or "x is false" isn't useful.
                continue;
            }
            var trueType = CheckIfExpressionRefinesParameter(fn, expr, param, initType);
            if (trueType is not null)
            {
                return NewTypePredicate(TypePredicateKind.Identifier, param.Name()!.Text(), i, trueType);
            }
        }
        return null;
    }

    // checker.go:20490-20511
    internal Type? CheckIfExpressionRefinesParameter(Node fn, Node expr, Node param, Type initType)
    {
        var antecedent = GetFlowNodeOfNode(expr);
        if (antecedent is null && NodeTests.IsReturnStatement(expr.Parent!))
        {
            antecedent = GetFlowNodeOfNode(expr.Parent!);
        }
        antecedent ??= new FlowNode { Flags = FlowFlags.Start };
        var trueCondition = new FlowNode { Flags = FlowFlags.TrueCondition, Node = expr, Antecedent = antecedent };
        var trueType = GetFlowTypeOfReferenceEx(param.Name()!, initType, initType, fn, trueCondition);
        if (trueType == initType)
        {
            return null;
        }
        // "x is T" means that x is T if and only if it returns true.
        var falseCondition = new FlowNode { Flags = FlowFlags.FalseCondition, Node = expr, Antecedent = antecedent };
        var falseSubtype = GetReducedType(GetFlowTypeOfReferenceEx(param.Name()!, initType, trueType, fn, falseCondition));
        if ((falseSubtype.flags & TypeFlags.Never) != 0)
        {
            return trueType;
        }
        return null;
    }

    // checker.go:20513-20518
    internal Type AddOptionalTypeMarker(Type t)
    {
        if (strictNullChecks)
        {
            return GetUnionType([t, optionalType!]);
        }
        return t;
    }

    // checker.go:20520-20522
    internal Signature InstantiateSignature(Signature sig, TypeMapper m) =>
        InstantiateSignatureEx(sig, m, m == permissiveMapper /*eraseTypeParameters*/);

    // checker.go:20524-20545
    internal Signature InstantiateSignatureEx(Signature sig, TypeMapper m, bool eraseTypeParameters)
    {
        Type[]? freshTypeParameters = null;
        if ((sig.typeParameters ?? []).Length != 0 && !eraseTypeParameters)
        {
            // First create a fresh set of type parameters, then include a mapping from the old to the
            // new type parameters in the mapper function.
            freshTypeParameters = CoreUtil.Map(sig.typeParameters, CloneTypeParameter);
            m = CombineTypeMappers(TypeMapper.NewTypeMapper(sig.typeParameters!, freshTypeParameters!), m)!;
            foreach (var tp in freshTypeParameters!)
            {
                tp.AsTypeParameter().mapper = m;
            }
        }
        // Don't compute resolvedReturnType and resolvedTypePredicate now (GH#17600).
        var result = NewSignature(sig.flags & SignatureFlags.PropagatingFlags, sig.declaration, freshTypeParameters,
            InstantiateSymbol(sig.thisParameter!, m!), InstantiateSymbols(sig.parameters, m!),
            null /*resolvedReturnType*/, null /*resolvedTypePredicate*/, sig.minArgumentCount);
        result.target = sig;
        result.mapper = m;
        return result;
    }

    // checker.go:20547-20553
    internal IndexInfo InstantiateIndexInfo(IndexInfo info, TypeMapper m)
    {
        var newValueType = InstantiateType(info.valueType, m);
        if (newValueType == info.valueType)
        {
            return info;
        }
        return NewIndexInfo(info.keyType, newValueType, info.isReadonly, info.declaration, info.components);
    }

    // checker.go:20555-20632
    internal void ResolveAnonymousTypeMembers(Type t)
    {
        var d = t.AsObjectType()!;
        if (d.target is not null)
        {
            SetStructuredTypeMembers(t, null, null, null, null);
            var members2 = CreateInstantiatedSymbolTable(GetPropertiesOfObjectType(d.target), d.mapper!);
            var callSignatures2 = InstantiateSignatures(GetSignaturesOfType(d.target, SignatureKind.Call), d.mapper!);
            var constructSignatures2 = InstantiateSignatures(GetSignaturesOfType(d.target, SignatureKind.Construct), d.mapper!);
            var indexInfos2 = InstantiateIndexInfos(GetIndexInfosOfType(d.target), d.mapper!);
            SetStructuredTypeMembers(t, members2, callSignatures2, constructSignatures2, indexInfos2);
            return;
        }
        var symbol = GetMergedSymbol(t.symbol)!;
        if ((symbol.Flags & SymbolFlags.TypeLiteral) != 0)
        {
            SetStructuredTypeMembers(t, null, null, null, null);
            var members3 = GetMembersOfSymbol(symbol);
            var callSignatures3 = GetSignaturesOfSymbol(members3?.GetValueOrDefault(InternalSymbolName.Call));
            var constructSignatures3 = GetSignaturesOfSymbol(members3?.GetValueOrDefault(InternalSymbolName.New));
            var indexInfos3 = GetIndexInfosOfSymbol(symbol);
            SetStructuredTypeMembers(t, members3, callSignatures3, constructSignatures3, indexInfos3);
            return;
        }
        // Combinations of function, class, enum and module
        var members = GetExportsOfSymbol(symbol);
        IndexInfo[]? indexInfos = null;
        if (symbol == globalThisSymbol)
        {
            var varsOnly = new SymbolTable();
            // map-iter: 序无关 — filtering into another table; order does not affect result.
            foreach (var p in members.Values)
            {
                if ((p.Flags & SymbolFlags.BlockScoped) == 0 && !((p.Flags & SymbolFlags.ValueModule) != 0 && (p.Declarations ?? []).Length != 0 && CoreUtil.Every(p.Declarations, AstUtilities.IsAmbientModule)))
                {
                    varsOnly[p.Name] = p;
                }
            }
            members = varsOnly;
        }
        IndexInfo? baseConstructorIndexInfo = null;
        SetStructuredTypeMembers(t, members, null, null, null);
        if ((symbol.Flags & SymbolFlags.Class) != 0)
        {
            var classType = GetDeclaredTypeOfClassOrInterface(symbol);
            var baseConstructorType = GetBaseConstructorTypeOfClass(classType);
            if ((baseConstructorType.flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.TypeVariable)) != 0)
            {
                members = new SymbolTable(members);
                AddInheritedMembers(members, GetPropertiesOfType(baseConstructorType));
                SetStructuredTypeMembers(t, members, null, null, null);
            }
            else if (baseConstructorType == anyType)
            {
                baseConstructorIndexInfo = anyBaseTypeIndexInfo;
            }
        }
        var indexSymbol = members.GetValueOrDefault(InternalSymbolName.Index);
        if (indexSymbol is not null)
        {
            // map-iter: 序无关 — collect members; getIndexInfosOfIndexSymbol reduces order-independently.
            indexInfos = GetIndexInfosOfIndexSymbol(indexSymbol, members.Values.ToArray());
        }
        else
        {
            if (baseConstructorIndexInfo is not null)
            {
                indexInfos = [.. indexInfos ?? [], baseConstructorIndexInfo];
            }
            if ((symbol.Flags & SymbolFlags.Enum) != 0 && ((GetDeclaredTypeOfSymbol(symbol).flags & TypeFlags.Enum) != 0 || CoreUtil.Some(d.properties, prop => (GetTypeOfSymbol(prop).flags & TypeFlags.NumberLike) != 0)))
            {
                indexInfos = [.. indexInfos ?? [], enumNumberIndexInfo!];
            }
        }
        d.indexInfos = indexInfos;
        // We resolve the members before computing the signatures (see issue #6072).
        if ((symbol.Flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0)
        {
            d.signatures = GetSignaturesOfSymbol(symbol);
            d.callSignatureCount = d.signatures.Length;
        }
        // And likewise for construct signatures for classes
        if ((symbol.Flags & SymbolFlags.Class) != 0)
        {
            var classType = GetDeclaredTypeOfClassOrInterface(symbol);
            var constructSignatures = GetSignaturesOfSymbol(symbol.Members?.GetValueOrDefault(InternalSymbolName.Constructor));
            if (constructSignatures.Length == 0)
            {
                constructSignatures = GetDefaultConstructSignatures(classType);
            }
            d.signatures = [.. d.signatures ?? [], .. constructSignatures];
        }
    }

    // checker.go:20634-20643
    internal SymbolTable? CreateInstantiatedSymbolTable(Symbol[] symbols, TypeMapper m)
    {
        if (symbols.Length == 0)
        {
            return null;
        }
        var result = new SymbolTable();
        foreach (var symbol in symbols)
        {
            result[symbol.Name] = InstantiateSymbol(symbol, m);
        }
        return result;
    }

    // checker.go:20645-20656
    internal SymbolTable? InstantiateSymbolTable(SymbolTable? symbols, TypeMapper m)
    {
        if (symbols is null || symbols.Count == 0)
        {
            return null;
        }
        var result = new SymbolTable(symbols.Count);
        // map-iter: 序无关 — instantiate each named member into a fresh table keyed by the same name.
        foreach (var (id, symbol) in symbols)
        {
            if (IsNamedMember(symbol, id))
            {
                result[id] = InstantiateSymbol(symbol, m);
            }
        }
        return result;
    }
}
