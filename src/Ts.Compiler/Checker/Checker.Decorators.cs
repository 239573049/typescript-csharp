// Ported from tsgo internal/checker/checker.go — decorator checking + decorator call-signature synthesis.
// The LEGACY (--experimentalDecorators) path is fully ported; the ES-decorator path
// (getESDecoratorCallSignature — the ClassDecoratorContext/… context-type machinery) is deferred as a
// stub (ES decorators fall through to it and error; legacy decorators, the common conformance case, work).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go — newParameter.
    private Symbol NewParameter(string name, Type t)
    {
        var symbol = NewSymbol(SymbolFlags.FunctionScopedVariable, name);
        valueSymbolLinks.Get(symbol).resolvedType = t;
        return symbol;
    }

    // checker.go — newCallSignature.
    private Signature NewCallSignature(Type[]? typeParameters, Symbol? thisParameter, Symbol[] parameters, Type returnType)
    {
        var decl = factory.NewFunctionTypeNode(null, null, factory.NewKeywordTypeNode(SyntaxKind.AnyKeyword));
        return NewSignature(SignatureFlags.None, decl, typeParameters, thisParameter, parameters, returnType, null, parameters.Length);
    }

    // checker.go — newTypedPropertyDescriptorType.
    private Type NewTypedPropertyDescriptorType(Type propertyType) =>
        CreateTypeFromGenericGlobalType(getGlobalTypedPropertyDescriptorType(), [propertyType]);

    // checker.go — getParentTypeOfClassElement.
    private Type GetParentTypeOfClassElement(Node node)
    {
        var classSymbol = GetSymbolOfNode(node.Parent!);
        if (AstUtilities.IsStatic(node))
        {
            return GetTypeOfSymbol(classSymbol!);
        }
        return GetDeclaredTypeOfSymbol(classSymbol!);
    }

    // checker.go — getClassElementPropertyKeyType.
    private Type GetClassElementPropertyKeyType(Node element)
    {
        var name = element.Name()!;
        switch (name.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.StringLiteral:
                return GetStringLiteralType(name.Text());
            case SyntaxKind.ComputedPropertyName:
                var nameType = CheckComputedPropertyName(name);
                if (IsTypeAssignableToKind(nameType, TypeFlags.ESSymbolLike))
                {
                    return nameType;
                }
                return stringType!;
        }
        return errorType!;
    }

    // checker.go — getLegacyDecoratorCallSignature.
    private Signature? GetLegacyDecoratorCallSignature(Node decorator)
    {
        var node = decorator.Parent!;
        var links = signatureLinks.Get(node);
        if (links.decoratorSignature == null)
        {
            links.decoratorSignature = anySignature;
            switch (node.Kind)
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                {
                    var targetType = GetTypeOfSymbol(GetSymbolOfDeclaration(node)!);
                    var targetParam = NewParameter("target", targetType);
                    links.decoratorSignature = NewCallSignature(null, null, [targetParam], GetUnionType([targetType, voidType!]));
                    break;
                }
                case SyntaxKind.Parameter:
                {
                    if (!NodeTests.IsConstructorDeclaration(node.Parent!) && !(NodeTests.IsMethodDeclaration(node.Parent!) || (NodeTests.IsSetAccessorDeclaration(node.Parent!) && AstUtilities.IsClassLike(node.Parent!.Parent!))))
                    {
                        break;
                    }
                    if (AstUtilities.GetThisParameter(node.Parent!) == node)
                    {
                        break;
                    }
                    var index = global::System.Array.IndexOf(node.Parent!.Parameters() ?? [], node) - (AstUtilities.GetThisParameter(node.Parent!) != null ? 1 : 0);
                    global::Ts.Core.Debug.Assert(index >= 0);
                    Type targetType;
                    Type keyType;
                    if (NodeTests.IsConstructorDeclaration(node.Parent!))
                    {
                        targetType = GetTypeOfSymbol(GetSymbolOfDeclaration(node.Parent!.Parent!)!);
                        keyType = undefinedType!;
                    }
                    else
                    {
                        targetType = GetParentTypeOfClassElement(node.Parent!);
                        keyType = GetClassElementPropertyKeyType(node.Parent!);
                    }
                    var indexType = GetNumberLiteralType(new global::Ts.JsNum.Number(index));
                    var targetParam = NewParameter("target", targetType);
                    var keyParam = NewParameter("propertyKey", keyType);
                    var indexParam = NewParameter("parameterIndex", indexType);
                    links.decoratorSignature = NewCallSignature(null, null, [targetParam, keyParam, indexParam], voidType!);
                    break;
                }
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                case SyntaxKind.PropertyDeclaration:
                {
                    if (!AstUtilities.IsClassLike(node.Parent!))
                    {
                        break;
                    }
                    var targetType = GetParentTypeOfClassElement(node);
                    var targetParam = NewParameter("target", targetType);
                    var keyType = GetClassElementPropertyKeyType(node);
                    var keyParam = NewParameter("propertyKey", keyType);
                    var returnType = voidType!;
                    if (!NodeTests.IsPropertyDeclaration(node))
                    {
                        returnType = NewTypedPropertyDescriptorType(GetTypeOfNode(node));
                    }
                    var hasPropDesc = !NodeTests.IsPropertyDeclaration(node) || AstUtilities.HasAccessorModifier(node);
                    if (hasPropDesc)
                    {
                        var descriptorType = NewTypedPropertyDescriptorType(GetTypeOfNode(node));
                        var descriptorParam = NewParameter("descriptor", descriptorType);
                        links.decoratorSignature = NewCallSignature(null, null, [targetParam, keyParam, descriptorParam], GetUnionType([returnType, voidType!]));
                    }
                    else
                    {
                        links.decoratorSignature = NewCallSignature(null, null, [targetParam, keyParam], GetUnionType([returnType, voidType!]));
                    }
                    break;
                }
            }
        }
        if (links.decoratorSignature == anySignature)
        {
            return null;
        }
        return links.decoratorSignature;
    }

    // checker.go — getESDecoratorCallSignature. The ES-decorator context-type machinery: the signature
    // `(target, context) => output | void` whose target/context/return types are synthesized from the
    // global ClassDecoratorContext/ClassMemberDecoratorContext/... lib types per the decorated element.
    private Signature? GetESDecoratorCallSignature(Node decorator)
    {
        var node = decorator.Parent!;
        var links = signatureLinks.Get(node);
        if (links.decoratorSignature == null)
        {
            links.decoratorSignature = anySignature;
            switch (node.Kind)
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                {
                    // Class decorators have a `context` of `ClassDecoratorContext<Class>`.
                    var targetType = GetTypeOfSymbol(GetSymbolOfDeclaration(node)!);
                    var contextType = NewClassDecoratorContextType(targetType);
                    links.decoratorSignature = NewESDecoratorCallSignature(targetType, contextType, targetType);
                    break;
                }
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.GetAccessor:
                case SyntaxKind.SetAccessor:
                {
                    if (!AstUtilities.IsClassLike(node.Parent!))
                    {
                        break;
                    }
                    Type valueType;
                    if (NodeTests.IsMethodDeclaration(node))
                    {
                        valueType = GetOrCreateTypeFromSignature(GetSignatureFromDeclaration(node));
                    }
                    else
                    {
                        valueType = GetTypeOfNode(node);
                    }
                    Type thisType;
                    if (AstUtilities.HasStaticModifier(node))
                    {
                        thisType = GetTypeOfSymbol(GetSymbolOfDeclaration(node.Parent!)!);
                    }
                    else
                    {
                        thisType = GetDeclaredTypeOfClassOrInterface(GetSymbolOfDeclaration(node.Parent!)!);
                    }
                    // Wrap the "input type" to match the decoration target: getters `() => inputType`,
                    // setters `(value: inputType) => void`, methods just the input type.
                    Type targetType;
                    if (NodeTests.IsGetAccessorDeclaration(node))
                    {
                        targetType = NewGetterFunctionType(valueType);
                    }
                    else if (NodeTests.IsSetAccessorDeclaration(node))
                    {
                        targetType = NewSetterFunctionType(valueType);
                    }
                    else
                    {
                        targetType = valueType;
                    }
                    var contextType = NewClassMemberDecoratorContextTypeForNode(node, thisType, valueType);
                    links.decoratorSignature = NewESDecoratorCallSignature(targetType, contextType, targetType);
                    break;
                }
                case SyntaxKind.PropertyDeclaration:
                {
                    if (!AstUtilities.IsClassLike(node.Parent!))
                    {
                        break;
                    }
                    var valueType = GetTypeOfNode(node);
                    Type thisType;
                    if (AstUtilities.HasStaticModifier(node))
                    {
                        thisType = GetTypeOfSymbol(GetSymbolOfDeclaration(node.Parent!)!);
                    }
                    else
                    {
                        thisType = GetDeclaredTypeOfClassOrInterface(GetSymbolOfDeclaration(node.Parent!)!);
                    }
                    // The `target` of an auto-accessor decorator is a `{ get, set }` object; the `target`
                    // of a regular field decorator is always `undefined`.
                    Type targetType;
                    if (AstUtilities.HasAccessorModifier(node))
                    {
                        targetType = NewClassAccessorDecoratorTargetType(thisType, valueType);
                    }
                    else
                    {
                        targetType = undefinedType!;
                    }
                    // Wrap the "output type": auto-accessors in `ClassAccessorDecoratorResult<This, In, Out>`,
                    // regular fields in an initializer-mutator function.
                    Type returnType;
                    if (AstUtilities.HasAccessorModifier(node))
                    {
                        returnType = NewClassAccessorDecoratorResultType(thisType, valueType);
                    }
                    else
                    {
                        returnType = NewClassFieldDecoratorInitializerMutatorType(thisType, valueType);
                    }
                    var contextType = NewClassMemberDecoratorContextTypeForNode(node, thisType, valueType);
                    links.decoratorSignature = NewESDecoratorCallSignature(targetType, contextType, returnType);
                    break;
                }
            }
        }
        if (links.decoratorSignature == anySignature)
        {
            return null;
        }
        return links.decoratorSignature;
    }

    // checker.go — newProperty.
    private Symbol NewProperty(string name, Type t)
    {
        var symbol = NewSymbol(SymbolFlags.Property, name);
        valueSymbolLinks.Get(symbol).resolvedType = t;
        return symbol;
    }

    // checker.go — newFunctionType.
    private Type NewFunctionType(Type[]? typeParameters, Symbol? thisParameter, Symbol[] parameters, Type returnType) =>
        GetOrCreateTypeFromSignature(NewCallSignature(typeParameters, thisParameter, parameters, returnType));

    // checker.go — tryCreateTypeReference.
    private Type TryCreateTypeReference(Type target, Type[]? typeArguments)
    {
        if ((typeArguments?.Length ?? 0) != 0 && target == emptyGenericType)
        {
            return unknownType!;
        }
        return CreateTypeReference(target, typeArguments);
    }

    // checker.go — newESDecoratorCallSignature.
    private Signature NewESDecoratorCallSignature(Type targetType, Type contextType, Type nonOptionalReturnType)
    {
        var targetParam = NewParameter("target", targetType);
        var contextParam = NewParameter("context", contextType);
        var returnType = GetUnionType([nonOptionalReturnType, voidType!]);
        return NewCallSignature(null, null /*thisParameter*/, [targetParam, contextParam], returnType);
    }

    // checker.go — newClassDecoratorContextType.
    private Type NewClassDecoratorContextType(Type classType) =>
        TryCreateTypeReference(getGlobalClassDecoratorContextType(), [classType]);

    // checker.go — newClassMethodDecoratorContextType.
    private Type NewClassMethodDecoratorContextType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassMethodDecoratorContextType(), [thisType, valueType]);

    // checker.go — newClassGetterDecoratorContextType.
    private Type NewClassGetterDecoratorContextType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassGetterDecoratorContextType(), [thisType, valueType]);

    // checker.go — newClassSetterDecoratorContextType.
    private Type NewClassSetterDecoratorContextType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassSetterDecoratorContextType(), [thisType, valueType]);

    // checker.go — newClassAccessorDecoratorContextType.
    private Type NewClassAccessorDecoratorContextType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassAccessorDecoratorContextType(), [thisType, valueType]);

    // checker.go — newClassFieldDecoratorContextType.
    private Type NewClassFieldDecoratorContextType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassFieldDecoratorContextType(), [thisType, valueType]);

    // checker.go — newClassAccessorDecoratorTargetType.
    private Type NewClassAccessorDecoratorTargetType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassAccessorDecoratorTargetType(), [thisType, valueType]);

    // checker.go — newClassAccessorDecoratorResultType.
    private Type NewClassAccessorDecoratorResultType(Type thisType, Type valueType) =>
        TryCreateTypeReference(getGlobalClassAccessorDecoratorResultType(), [thisType, valueType]);

    // checker.go — newGetterFunctionType.
    private Type NewGetterFunctionType(Type t) =>
        NewFunctionType(null, null /*thisParameter*/, [], t);

    // checker.go — newSetterFunctionType.
    private Type NewSetterFunctionType(Type t)
    {
        var valueParam = NewParameter("value", t);
        return NewFunctionType(null, null /*thisParameter*/, [valueParam], voidType!);
    }

    // checker.go — newClassFieldDecoratorInitializerMutatorType.
    private Type NewClassFieldDecoratorInitializerMutatorType(Type thisType, Type valueType)
    {
        var thisParam = NewParameter("this", thisType);
        var valueParam = NewParameter("value", valueType);
        return NewFunctionType(null, thisParam, [valueParam], valueType);
    }

    // checker.go — getClassMemberDecoratorContextOverrideType.
    private Type GetClassMemberDecoratorContextOverrideType(Type nameType, bool isPrivate, bool isStatic)
    {
        var kind = isPrivate
            ? (isStatic ? CachedTypeKind.DecoratorContextPrivateStatic : CachedTypeKind.DecoratorContextPrivate)
            : (isStatic ? CachedTypeKind.DecoratorContextStatic : CachedTypeKind.DecoratorContext);
        var key = new CachedTypeKey(kind, nameType.id);
        if (cachedTypes.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var members = new SymbolTable
        {
            ["name"] = NewProperty("name", nameType),
            ["private"] = NewProperty("private", isPrivate ? trueType! : falseType!),
            ["static"] = NewProperty("static", isStatic ? trueType! : falseType!),
        };
        var overrideType = NewAnonymousType(null, members, null, null, null);
        cachedTypes[key] = overrideType;
        return overrideType;
    }

    // checker.go — newClassMemberDecoratorContextTypeForNode.
    private Type NewClassMemberDecoratorContextTypeForNode(Node node, Type thisType, Type valueType)
    {
        var isStatic = AstUtilities.HasStaticModifier(node);
        var isPrivate = NodeTests.IsPrivateIdentifier(node.Name()!);
        Type nameType;
        if (isPrivate)
        {
            nameType = GetStringLiteralType(node.Name()!.Text());
        }
        else
        {
            nameType = GetLiteralTypeFromPropertyName(node.Name()!);
        }
        Type contextType;
        if (NodeTests.IsMethodDeclaration(node))
        {
            contextType = NewClassMethodDecoratorContextType(thisType, valueType);
        }
        else if (NodeTests.IsGetAccessorDeclaration(node))
        {
            contextType = NewClassGetterDecoratorContextType(thisType, valueType);
        }
        else if (NodeTests.IsSetAccessorDeclaration(node))
        {
            contextType = NewClassSetterDecoratorContextType(thisType, valueType);
        }
        else if (AstUtilities.IsAutoAccessorPropertyDeclaration(node))
        {
            contextType = NewClassAccessorDecoratorContextType(thisType, valueType);
        }
        else if (NodeTests.IsPropertyDeclaration(node))
        {
            contextType = NewClassFieldDecoratorContextType(thisType, valueType);
        }
        else
        {
            throw new InvalidOperationException("Unhandled case in createClassMemberDecoratorContextTypeForNode");
        }
        var overrideType = GetClassMemberDecoratorContextOverrideType(nameType, isPrivate, isStatic);
        return GetIntersectionType([contextType, overrideType]);
    }

    // checker.go — getDecoratorCallSignature.
    private Signature? GetDecoratorCallSignature(Node decorator)
    {
        if (legacyDecorators)
        {
            return GetLegacyDecoratorCallSignature(decorator);
        }
        return GetESDecoratorCallSignature(decorator);
    }

    // checker.go — getLegacyDecoratorArgumentCount.
    private int GetLegacyDecoratorArgumentCount(Node node, Signature signature)
    {
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return 1;
            case SyntaxKind.PropertyDeclaration:
                return AstUtilities.HasAccessorModifier(node.Parent!) ? 3 : 2;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return signature.parameters.Len() <= 2 ? 2 : 3;
            case SyntaxKind.Parameter:
                return 3;
        }
        throw new InvalidOperationException("Unhandled case in getLegacyDecoratorArgumentCount");
    }

    // checker.go — getDecoratorArgumentCount.
    private int GetDecoratorArgumentCount(Node node, Signature signature)
    {
        if (compilerOptions.ExperimentalDecorators.IsTrue())
        {
            return GetLegacyDecoratorArgumentCount(node, signature);
        }
        return global::System.Math.Min(global::System.Math.Max(GetParameterCount(signature), 1), 2);
    }

    // checker.go — getEffectiveDecoratorArguments.
    private Node[] GetEffectiveDecoratorArguments(Node node)
    {
        var expr = node.Expression()!;
        var signature = GetDecoratorCallSignature(node);
        if (signature != null)
        {
            var args = new Node[signature.parameters.Len()];
            for (var i = 0; i < signature.parameters.Len(); i++)
            {
                args[i] = CreateSyntheticExpression(expr, GetTypeOfSymbol(signature.parameters![i]), false, null);
            }
            return args;
        }
        throw new InvalidOperationException("Decorator signature not found");
    }

    // checker.go — getContextualTypeForDecorator.
    private Type? GetContextualTypeForDecorator(Node decorator)
    {
        var signature = GetDecoratorCallSignature(decorator);
        if (signature != null)
        {
            return GetOrCreateTypeFromSignature(signature);
        }
        return null;
    }

    // checker.go — isPotentiallyUncalledDecorator.
    private bool IsPotentiallyUncalledDecorator(Node decorator, Signature[] signatures) =>
        signatures.Length != 0 && global::Ts.Core.CoreUtil.Every(signatures, sig =>
            sig.minArgumentCount == 0 && !SignatureHasRestParameter(sig) && sig.parameters.Len() < GetDecoratorArgumentCount(decorator, sig));

    // checker.go — getDiagnosticHeadMessageForDecoratorResolution.
    private global::Ts.Diagnostics.Message GetDiagnosticHeadMessageForDecoratorResolution(Node node)
    {
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                return Diag.Unable_to_resolve_signature_of_class_decorator_when_called_as_an_expression;
            case SyntaxKind.Parameter:
                return Diag.Unable_to_resolve_signature_of_parameter_decorator_when_called_as_an_expression;
            case SyntaxKind.PropertyDeclaration:
                return Diag.Unable_to_resolve_signature_of_property_decorator_when_called_as_an_expression;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                return Diag.Unable_to_resolve_signature_of_method_decorator_when_called_as_an_expression;
        }
        throw new InvalidOperationException("Unhandled case in getDiagnosticHeadMessageForDecoratorResolution");
    }

    // checker.go — resolveDecorator.
    private Signature ResolveDecorator(Node node, List<Signature>? candidatesOutArray, CheckMode checkMode)
    {
        if (!AstUtilities.CanHaveDecorators(node.Parent!))
        {
            return ResolveErrorCall(node);
        }
        var funcType = CheckExpression(node.Expression()!);
        var apparentType = GetApparentType(funcType);
        if (IsErrorType(apparentType))
        {
            return ResolveErrorCall(node);
        }
        var callSignatures = GetSignaturesOfType(apparentType, SignatureKind.Call);
        var numConstructSignatures = GetSignaturesOfType(apparentType, SignatureKind.Construct).Length;
        if (IsUntypedFunctionCall(funcType, apparentType, callSignatures.Length, numConstructSignatures))
        {
            return ResolveUntypedCall(node);
        }
        if (IsPotentiallyUncalledDecorator(node, callSignatures) && !NodeTests.IsParenthesizedExpression(node.Expression()!))
        {
            var nodeStr = global::Ts.Scanner.Scanner.GetTextOfNode(node.Expression()!);
            Error(node, Diag.X_0_accepts_too_few_arguments_to_be_used_as_a_decorator_here_Did_you_mean_to_call_it_first_and_write_0, nodeStr);
            return ResolveErrorCall(node);
        }
        var headMessage = GetDiagnosticHeadMessageForDecoratorResolution(node);
        if (callSignatures.Length == 0)
        {
            var diag = Diagnostic.NewDiagnosticChain(InvocationErrorDetails(node.Expression()!, apparentType, SignatureKind.Call), headMessage);
            AddDiagnostic(diag);
            InvocationErrorRecovery(apparentType, SignatureKind.Call, diag);
            return ResolveErrorCall(node);
        }
        var decoratorSignature = GetDecoratorCallSignature(node);
        if (decoratorSignature == null)
        {
            return ResolveErrorCall(node);
        }
        return ResolveCall(node, callSignatures, candidatesOutArray, checkMode, SignatureFlags.None, headMessage);
    }

    // checker.go:6040 — checkDecorator.
    internal void CheckDecorator(Node node)
    {
        CheckGrammarDecorator(node.AsDecorator());
        var signature = GetResolvedSignature(node, null, CheckMode.Normal);
        CheckDeprecatedSignature(signature, node);
        var returnType = GetReturnTypeOfSignature(signature);
        if ((returnType.flags & TypeFlags.Any) != 0)
        {
            return;
        }
        var decoratorSignature = GetDecoratorCallSignature(node);
        if (decoratorSignature == null || decoratorSignature.resolvedReturnType == null)
        {
            return;
        }
        global::Ts.Diagnostics.Message? headMessage;
        var expectedReturnType = decoratorSignature.resolvedReturnType;
        switch (node.Parent!.Kind)
        {
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.ClassExpression:
                headMessage = Diag.Decorator_function_return_type_0_is_not_assignable_to_type_1;
                break;
            case SyntaxKind.PropertyDeclaration:
                if (!legacyDecorators)
                {
                    headMessage = Diag.Decorator_function_return_type_0_is_not_assignable_to_type_1;
                    break;
                }
                goto case SyntaxKind.Parameter;
            case SyntaxKind.Parameter:
                headMessage = Diag.Decorator_function_return_type_is_0_but_is_expected_to_be_void_or_any;
                break;
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
                headMessage = Diag.Decorator_function_return_type_0_is_not_assignable_to_type_1;
                break;
            default:
                throw new InvalidOperationException("Unhandled case in checkDecorator");
        }
        CheckTypeAssignableTo(returnType, expectedReturnType, node.Expression(), headMessage);
    }
}
