// Ported from tsgo internal/checker/checker.go (section: type-node → type resolution,
// lines 22713-24910). plan/03 §4.2 #15.
//
// getTypeFromTypeNode family: type-reference resolution (22909), intersection (24126),
// conditional (24463), enum evaluation (23866), tuple target types (24659), plus the
// type-node→type helpers they call. Faithful per-function translation of the Go range.
//
// Cross-cluster methods (SymbolResolution/SymbolTypes/Members/UnionIntersection/
// Instantiation/IndexedAccess/Constraints/TypeCreation deep members) are called but not
// implemented here; their stubs live in Checker.TypeNodes.Stubs.cs (or the shared
// Checker.Stubs.cs) until their owning waves land.

using System.Text;
using Ts.Ast;
using Ts.Core;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:22713-22715
    internal Type GetTypeFromTypeNode(Node node) => GetConditionalFlowTypeOfType(GetTypeFromTypeNodeWorker(node), node);

    // checker.go:22717-22804
    internal Type GetTypeFromTypeNodeWorker(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.AnyKeyword:
            case SyntaxKind.JSDocAllType:
                return anyType!;
            case SyntaxKind.JSDocNonNullableType:
                return GetTypeFromTypeNode(node.Type()!);
            case SyntaxKind.JSDocNullableType:
                {
                    var t = GetTypeFromTypeNode(node.Type()!);
                    if (strictNullChecks)
                    {
                        return GetNullableType(t, TypeFlags.Null);
                    }
                    return t;
                }
            case SyntaxKind.JSDocVariadicType:
                return CreateArrayType(GetTypeFromTypeNode(node.AsJSDocVariadicType().Type!));
            case SyntaxKind.JSDocOptionalType:
                return AddOptionality(GetTypeFromTypeNode(node.Type()!));
            case SyntaxKind.UnknownKeyword:
                return unknownType!;
            case SyntaxKind.StringKeyword:
                return stringType!;
            case SyntaxKind.NumberKeyword:
                return numberType!;
            case SyntaxKind.BigIntKeyword:
                return bigintType!;
            case SyntaxKind.BooleanKeyword:
                return booleanType!;
            case SyntaxKind.SymbolKeyword:
                return esSymbolType!;
            case SyntaxKind.VoidKeyword:
                return voidType!;
            case SyntaxKind.UndefinedKeyword:
                return undefinedType!;
            case SyntaxKind.NullKeyword:
                return nullType!;
            case SyntaxKind.NeverKeyword:
                return neverType!;
            case SyntaxKind.ObjectKeyword:
                return nonPrimitiveType!;
            case SyntaxKind.IntrinsicKeyword:
                return intrinsicMarkerType!;
            case SyntaxKind.ThisType:
            case SyntaxKind.ThisKeyword:
                return GetTypeFromThisTypeNode(node);
            case SyntaxKind.LiteralType:
                return GetTypeFromLiteralTypeNode(node);
            case SyntaxKind.TypeReference:
            case SyntaxKind.ExpressionWithTypeArguments:
                return GetTypeFromTypeReference(node);
            case SyntaxKind.TypePredicate:
                if (node.AsTypePredicateNode().AssertsModifier is not null)
                {
                    return voidType!;
                }
                return booleanType!;
            case SyntaxKind.TypeQuery:
                return GetTypeFromTypeQueryNode(node);
            case SyntaxKind.ArrayType:
            case SyntaxKind.TupleType:
                return GetTypeFromArrayOrTupleTypeNode(node);
            case SyntaxKind.OptionalType:
                return GetTypeFromOptionalTypeNode(node);
            case SyntaxKind.UnionType:
                return GetTypeFromUnionTypeNode(node);
            case SyntaxKind.IntersectionType:
                return GetTypeFromIntersectionTypeNode(node);
            case SyntaxKind.NamedTupleMember:
                return GetTypeFromNamedTupleTypeNode(node);
            case SyntaxKind.ParenthesizedType:
                return GetTypeFromTypeNode(node.Type()!);
            case SyntaxKind.RestType:
                return GetTypeFromRestTypeNode(node);
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.TypeLiteral:
                return GetTypeFromTypeLiteralOrFunctionOrConstructorTypeNode(node);
            case SyntaxKind.TypeOperator:
                return GetTypeFromTypeOperatorNode(node);
            case SyntaxKind.IndexedAccessType:
                return GetTypeFromIndexedAccessTypeNode(node);
            case SyntaxKind.TemplateLiteralType:
                return GetTypeFromTemplateTypeNode(node);
            case SyntaxKind.MappedType:
                return GetTypeFromMappedTypeNode(node);
            case SyntaxKind.ConditionalType:
                return GetTypeFromConditionalTypeNode(node);
            case SyntaxKind.InferType:
                return GetTypeFromInferTypeNode(node);
            case SyntaxKind.ImportType:
                return GetTypeFromImportTypeNode(node);
            default:
                return errorType!;
        }
    }

    // checker.go:22806-22812
    internal Type GetTypeFromThisTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        links.resolvedType ??= GetThisType(node);
        return links.resolvedType;
    }

    // checker.go:22814-22826
    internal Type GetThisType(Node node)
    {
        var container = AstUtilities.GetThisContainer(node, /*includeArrowFunctions*/ false, /*includeClassComputedPropertyName*/ false);
        if (container is not null)
        {
            var parent = container.Parent;
            if (parent is not null && (AstUtilities.IsClassLike(parent) || NodeTests.IsInterfaceDeclaration(parent)))
            {
                if (!AstUtilities.IsStatic(container) && (!NodeTests.IsConstructorDeclaration(container) || AstUtilities.IsNodeDescendantOf(node, container.Body())))
                {
                    return CoreUtil.Coalesce(GetDeclaredTypeOfClassOrInterface(GetSymbolOfDeclaration(parent)!).AsInterfaceType()!.thisType, errorType)!;
                }
            }
        }
        Error(node, Diag.A_this_type_is_available_only_in_a_non_static_member_of_a_class_or_interface);
        return errorType!;
    }

    // checker.go:22828-22837
    internal Type GetTypeFromLiteralTypeNode(Node node)
    {
        if (node.AsLiteralTypeNode().Literal!.Kind == SyntaxKind.NullKeyword)
        {
            return nullType!;
        }
        var links = typeNodeLinks.Get(node);
        links.resolvedType ??= GetRegularTypeOfLiteralType(CheckExpression(node.AsLiteralTypeNode().Literal!));
        return links.resolvedType;
    }

    // checker.go:22839-22853
    internal Type GetTypeFromTypeLiteralOrFunctionOrConstructorTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            // Deferred resolution of members is handled by resolveObjectTypeMembers
            var alias = GetAliasForTypeNode(node);
            var sym = node.Symbol();
            if (sym is null || (GetMembersOfSymbol(sym).Count == 0 && alias is null))
            {
                links.resolvedType = emptyTypeLiteralType!;
            }
            else
            {
                var t = NewObjectType(ObjectFlags.Anonymous, node.Symbol());
                t.alias = alias;
                links.resolvedType = t;
            }
        }
        return links.resolvedType;
    }

    // checker.go:22855-22864
    internal Type GetTypeFromIndexedAccessTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var objectType = GetTypeFromTypeNode(node.AsIndexedAccessTypeNode().ObjectType!);
            var indexType = GetTypeFromTypeNode(node.AsIndexedAccessTypeNode().IndexType!);
            var potentialAlias = GetAliasForTypeNode(node);
            links.resolvedType = GetIndexedAccessTypeEx(objectType, indexType, AccessFlags.None, node, potentialAlias);
        }
        return links.resolvedType;
    }

    // checker.go:22866-22886
    internal Type GetTypeFromTypeOperatorNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var argType = node.Type()!;
            switch (node.AsTypeOperatorNode().Operator)
            {
                case SyntaxKind.KeyOfKeyword:
                    links.resolvedType = GetIndexType(GetTypeFromTypeNode(argType));
                    break;
                case SyntaxKind.UniqueKeyword:
                    if (argType.Kind == SyntaxKind.SymbolKeyword)
                    {
                        links.resolvedType = GetESSymbolLikeTypeForNode(AstUtilities.WalkUpParenthesizedTypes(node.Parent)!);
                    }
                    else
                    {
                        links.resolvedType = errorType!;
                    }
                    break;
                case SyntaxKind.ReadonlyKeyword:
                    links.resolvedType = GetTypeFromTypeNode(argType);
                    break;
                default:
                    throw new InvalidOperationException("Unhandled case in getTypeFromTypeOperatorNode");
            }
        }
        return links.resolvedType;
    }

    // checker.go:22888-22907
    internal Type GetESSymbolLikeTypeForNode(Node node)
    {
        if (IsValidESSymbolDeclaration(node))
        {
            var symbol = GetSymbolOfNode(node);
            if (symbol is not null)
            {
                var uniqueType = uniqueESSymbolTypes.GetValueOrDefault(symbol);
                if (uniqueType is null)
                {
                    var b = new StringBuilder();
                    b.Append(InternalSymbolName.Prefix);
                    b.Append('@');
                    b.Append(symbol.Name);
                    b.Append('@');
                    b.Append(AstUtilities.GetSymbolId(symbol).Value.ToString());
                    uniqueType = NewUniqueESSymbolType(symbol, b.ToString());
                    uniqueESSymbolTypes[symbol] = uniqueType;
                }
                return uniqueType;
            }
        }
        return esSymbolType!;
    }

    // checker.go:22909-22924
    internal Type GetTypeFromTypeReference(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            // Cache both the resolved symbol and the resolved type. The resolved symbol is needed when we check the
            // type reference in checkTypeReferenceNode.
            // handle LS queries on the `const` in `x as const` by resolving to the type of `x`
            if (AstUtilities.IsConstTypeReference(node) && AstUtilities.IsAssertionExpression(node.Parent!))
            {
                links.resolvedType = CheckExpressionCached(node.Parent!.Expression()!);
            }
            else if (GetIntendedTypeFromJSDocTypeReference(node) is { } t)
            {
                links.resolvedType = t;
            }
            else
            {
                links.resolvedType = GetTypeReferenceType(node, GetSymbolFromTypeReference(node));
            }
        }
        return links.resolvedType;
    }

    // checker.go:22926-22981
    internal Type? GetIntendedTypeFromJSDocTypeReference(Node node)
    {
        if ((node.Flags & NodeFlags.JSDoc) != 0 && NodeTests.IsTypeReferenceNode(node))
        {
            var typeName = node.AsTypeReferenceNode().TypeName!;
            if (NodeTests.IsIdentifier(typeName))
            {
                var typeArgs = node.TypeArguments();
                switch (typeName.Text())
                {
                    case "String":
                        CheckNoTypeArguments(node, null);
                        return stringType!;
                    case "Number":
                        CheckNoTypeArguments(node, null);
                        return numberType!;
                    case "BigInt":
                        CheckNoTypeArguments(node, null);
                        return bigintType!;
                    case "Boolean":
                        CheckNoTypeArguments(node, null);
                        return booleanType!;
                    case "Void":
                        CheckNoTypeArguments(node, null);
                        return voidType!;
                    case "Undefined":
                        CheckNoTypeArguments(node, null);
                        return undefinedType!;
                    case "Null":
                        CheckNoTypeArguments(node, null);
                        return nullType!;
                    case "Function":
                    case "function":
                        CheckNoTypeArguments(node, null);
                        return globalFunctionType!;
                    case "array":
                        if ((typeArgs?.Length ?? 0) == 0 && !noImplicitAny)
                        {
                            return anyArrayType!;
                        }
                        break;
                    case "promise":
                        if ((typeArgs?.Length ?? 0) == 0 && !noImplicitAny)
                        {
                            return CreatePromiseType(anyType!);
                        }
                        break;
                    case "Object":
                        if ((typeArgs?.Length ?? 0) == 2)
                        {
                            if (getGlobalRecordSymbol() is { } recordSymbol)
                            {
                                var indexType = GetTypeFromTypeNode(typeArgs![0]);
                                if (IsValidIndexKeyType(indexType))
                                {
                                    return GetTypeAliasInstantiation(recordSymbol, [indexType, GetTypeFromTypeNode(typeArgs[1])], null);
                                }
                            }
                            return anyType!;
                        }
                        if (!noImplicitAny)
                        {
                            CheckNoTypeArguments(node, null);
                            return anyType!;
                        }
                        break;
                }
            }
        }
        return null;
    }

    // checker.go:22983-22993
    internal Symbol GetSymbolFromTypeReference(Node node)
    {
        var links = symbolNodeLinks.Get(node);
        if (links.resolvedSymbol is null)
        {
            if (AstUtilities.IsConstTypeReference(node) && AstUtilities.IsAssertionExpression(node.Parent!))
            {
                links.resolvedSymbol = unknownSymbol;
            }
            else
            {
                links.resolvedSymbol = ResolveTypeReferenceName(node, SymbolFlags.Type, false /*ignoreErrors*/);
            }
        }
        return links.resolvedSymbol;
    }

    // checker.go:22995-23008
    internal Symbol ResolveTypeReferenceName(Node typeReference, SymbolFlags meaning, bool ignoreErrors)
    {
        var name = GetTypeReferenceName(typeReference);
        if (name is null)
        {
            return unknownSymbol;
        }
        var symbol = ResolveEntityName(name, meaning, ignoreErrors, false /*dontResolveAlias*/, null /*location*/);
        if (symbol is not null && symbol != unknownSymbol)
        {
            return symbol;
        }
        if (ignoreErrors)
        {
            return unknownSymbol;
        }
        return GetUnresolvedSymbolForEntityName(name);
    }

    // checker.go:23010-23045
    internal Symbol GetUnresolvedSymbolForEntityName(Node name)
    {
        Node identifier = name.Kind switch
        {
            SyntaxKind.QualifiedName => name.AsQualifiedName().Right!,
            SyntaxKind.PropertyAccessExpression => name.Name()!,
            _ => name,
        };
        var text = identifier.Text();
        if (text != "")
        {
            Symbol? parentSymbol = name.Kind switch
            {
                SyntaxKind.QualifiedName => GetUnresolvedSymbolForEntityName(name.AsQualifiedName().Left!),
                SyntaxKind.PropertyAccessExpression => GetUnresolvedSymbolForEntityName(name.Expression()!),
                _ => null,
            };
            string path = parentSymbol is not null ? GetSymbolPath(parentSymbol) + "." + text : text;
            var result = unresolvedSymbols.GetValueOrDefault(path);
            if (result is null)
            {
                result = NewSymbolEx(SymbolFlags.TypeAlias, text, CheckFlags.Unresolved);
                unresolvedSymbols[path] = result;
                result.Parent = parentSymbol;
                typeAliasLinks.Get(result).declaredType = unresolvedType;
            }
            return result;
        }
        return unknownSymbol;
    }

    // checker.go:23047-23052
    internal static string GetSymbolPath(Symbol symbol)
    {
        if (symbol.Parent is not null)
        {
            return GetSymbolPath(symbol.Parent) + "." + symbol.Name;
        }
        return symbol.Name;
    }

    // checker.go:23054-23072
    internal Type GetTypeReferenceType(Node node, Symbol symbol)
    {
        if (symbol == unknownSymbol)
        {
            return errorType!;
        }
        if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
        {
            return GetTypeFromClassOrInterfaceReference(node, symbol);
        }
        if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            return GetTypeFromTypeAliasReference(node, symbol);
        }
        // Get type from reference to named type that cannot be generic (enum or type parameter)
        var res = TryGetDeclaredTypeOfSymbol(symbol);
        if (res is not null && CheckNoTypeArguments(node, symbol))
        {
            return GetRegularTypeOfLiteralType(res);
        }

        // !!! Resolving values as types for JS
        return errorType!;
    }

    // Get type from type-reference that reference to class or interface
    // checker.go:23077-23122
    internal Type GetTypeFromClassOrInterfaceReference(Node node, Symbol symbol)
    {
        var t = GetDeclaredTypeOfClassOrInterface(GetMergedSymbol(symbol)!);
        var d = t.AsInterfaceType()!;
        var typeParameters = d.LocalTypeParameters();
        if ((typeParameters?.Length ?? 0) != 0)
        {
            var numTypeArguments = node.TypeArguments()?.Length ?? 0;
            var minTypeArgumentCount = GetMinTypeArgumentCount(typeParameters);
            var isJs = AstUtilities.IsInJSFile(node);
            var isJsImplicitAny = !noImplicitAny && isJs;
            if (!isJsImplicitAny && (numTypeArguments < minTypeArgumentCount || numTypeArguments > typeParameters!.Length))
            {
                global::Ts.Diagnostics.Message message;
                var missingAugmentsTag = isJs && NodeTests.IsExpressionWithTypeArguments(node) && !NodeTests.IsJSDocAugmentsTag(node.Parent!);
                if (missingAugmentsTag)
                {
                    message = Diag.Expected_0_type_arguments_provide_these_with_an_extends_tag;
                    if (minTypeArgumentCount < typeParameters!.Length)
                    {
                        message = Diag.Expected_0_1_type_arguments_provide_these_with_an_extends_tag;
                    }
                }
                else
                {
                    message = Diag.Generic_type_0_requires_1_type_argument_s;
                    if (minTypeArgumentCount < typeParameters!.Length)
                    {
                        message = Diag.Generic_type_0_requires_between_1_and_2_type_arguments;
                    }
                }
                var typeStr = TypeToStringEx(t, null /*enclosingDeclaration*/, TypeFormatFlags.WriteArrayAsGenericType, null);
                Error(node, message, typeStr, minTypeArgumentCount, typeParameters.Length);
                if (!isJs)
                {
                    // TODO: Adopt same permissive behavior in TS as in JS to reduce follow-on editing experience failures (requires editing fillMissingTypeArguments)
                    return errorType!;
                }
            }
            if (node.Kind == SyntaxKind.TypeReference && IsDeferredTypeReferenceNode(node, numTypeArguments != typeParameters!.Length))
            {
                return CreateDeferredTypeReference(t, node, null /*mapper*/, null /*alias*/);
            }
            // In a type reference, the outer type parameters of the referenced class or interface are automatically
            // supplied as type arguments and the type reference only specifies arguments for the local type parameters
            // of the class or interface.
            var localTypeArguments = FillMissingTypeArguments(GetTypeArgumentsFromNode(node), typeParameters, minTypeArgumentCount, isJs);
            Type[] typeArguments = [.. d.OuterTypeParameters() ?? [], .. localTypeArguments ?? []];
            return CreateTypeReferenceEx(t, typeArguments, ObjectFlags.FromTypeNode);
        }
        if (CheckNoTypeArguments(node, symbol))
        {
            return t;
        }
        return errorType!;
    }

    // checker.go:23124-23126
    internal Type[]? GetTypeArgumentsFromNode(Node node) => CoreUtil.Map(node.TypeArguments(), GetTypeFromTypeNode);

    // checker.go:23128-23140
    internal bool CheckNoTypeArguments(Node node, Symbol? symbol)
    {
        if ((node.TypeArguments()?.Length ?? 0) != 0)
        {
            string typeName;
            if (symbol is not null)
            {
                typeName = SymbolToString(symbol);
            }
            else
            {
                typeName = global::Ts.Scanner.Scanner.DeclarationNameToString(node.AsTypeReferenceNode().TypeName);
            }
            Error(node, Diag.Type_0_is_not_generic, typeName);
            return false;
        }
        return true;
    }

    // Return true if the given type reference node is directly aliased or if it needs to be deferred
    // because it is possibly contained in a circular chain of eagerly resolved types.
    // checker.go:23144-23160
    internal bool IsDeferredTypeReferenceNode(Node node, bool hasDefaultTypeArguments)
    {
        if (GetAliasSymbolForTypeNode(node) is not null)
        {
            return true;
        }
        if (IsResolvedByTypeAlias(node))
        {
            switch (node.Kind)
            {
                case SyntaxKind.ArrayType:
                    return MayResolveTypeAlias(node.AsArrayTypeNode().ElementType!);
                case SyntaxKind.TupleType:
                    return CoreUtil.Some(node.Elements(), MayResolveTypeAlias);
                case SyntaxKind.TypeReference:
                    return hasDefaultTypeArguments || CoreUtil.Some(node.TypeArguments(), MayResolveTypeAlias);
            }
            throw new InvalidOperationException("Unhandled case in isDeferredTypeReferenceNode");
        }
        return false;
    }

    // Return true when the given node is transitively contained in type constructs that eagerly
    // resolve their constituent types. We include SyntaxKind.TypeReference because type arguments
    // of type aliases are eagerly resolved.
    // checker.go:23165-23175
    internal bool IsResolvedByTypeAlias(Node node)
    {
        var parent = node.Parent!;
        switch (parent.Kind)
        {
            case SyntaxKind.ParenthesizedType:
            case SyntaxKind.NamedTupleMember:
            case SyntaxKind.TypeReference:
            case SyntaxKind.UnionType:
            case SyntaxKind.IntersectionType:
            case SyntaxKind.IndexedAccessType:
            case SyntaxKind.ConditionalType:
            case SyntaxKind.TypeOperator:
            case SyntaxKind.ArrayType:
            case SyntaxKind.TupleType:
                return IsResolvedByTypeAlias(parent);
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
                return true;
        }
        return false;
    }

    // Return true if resolving the given node (i.e. getTypeFromTypeNode) possibly causes resolution
    // of a type alias.
    // checker.go:23179-23202
    internal bool MayResolveTypeAlias(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TypeReference:
                return (ResolveTypeReferenceName(node, SymbolFlags.Type, false).Flags & SymbolFlags.TypeAlias) != 0;
            case SyntaxKind.TypeQuery:
                return true;
            case SyntaxKind.TypeOperator:
                return node.AsTypeOperatorNode().Operator != SyntaxKind.UniqueKeyword && MayResolveTypeAlias(node.Type()!);
            case SyntaxKind.ParenthesizedType:
            case SyntaxKind.OptionalType:
            case SyntaxKind.NamedTupleMember:
                return MayResolveTypeAlias(node.Type()!);
            case SyntaxKind.RestType:
                return node.Type()!.Kind != SyntaxKind.ArrayType || MayResolveTypeAlias(node.Type()!.AsArrayTypeNode().ElementType!);
            case SyntaxKind.UnionType:
                return CoreUtil.Some(node.AsUnionTypeNode().Types!.Nodes, MayResolveTypeAlias);
            case SyntaxKind.IntersectionType:
                return CoreUtil.Some(node.AsIntersectionTypeNode().Types!.Nodes, MayResolveTypeAlias);
            case SyntaxKind.IndexedAccessType:
                return MayResolveTypeAlias(node.AsIndexedAccessTypeNode().ObjectType!) || MayResolveTypeAlias(node.AsIndexedAccessTypeNode().IndexType!);
            case SyntaxKind.ConditionalType:
                return MayResolveTypeAlias(node.AsConditionalTypeNode().CheckType!) || MayResolveTypeAlias(node.AsConditionalTypeNode().ExtendsType!) ||
                    MayResolveTypeAlias(node.AsConditionalTypeNode().TrueType!) || MayResolveTypeAlias(node.AsConditionalTypeNode().FalseType!);
        }
        return false;
    }

    // checker.go:23204-23209
    internal Type CreateNormalizedTypeReference(Type target, Type[]? typeArguments)
    {
        if ((target.objectFlags & ObjectFlags.Tuple) != 0)
        {
            return CreateNormalizedTupleType(target, typeArguments!);
        }
        return CreateTypeReference(target, typeArguments);
    }

    // checker.go:23211-23257
    internal Type CreateNormalizedTupleTypeEx(Type target, Type[] elementTypes, ObjectFlags objectFlags)
    {
        var d = target.AsTupleType();
        if ((d.combinedFlags & ElementFlags.NonRequired) == 0)
        {
            // No need to normalize when we only have regular required elements
            return CreateTypeReferenceEx(target, elementTypes, objectFlags);
        }
        var elementInfos = d.elementInfos ?? [];
        if ((d.combinedFlags & ElementFlags.Variadic) != 0)
        {
            for (int i = 0; i < elementTypes.Length; i++)
            {
                var e = elementTypes[i];
                if (i < elementInfos.Length && (elementInfos[i].flags & ElementFlags.Variadic) != 0 && (e.flags & (TypeFlags.Never | TypeFlags.Union)) != 0)
                {
                    // Transform [A, ...(X | Y | Z)] into [A, ...X] | [A, ...Y] | [A, ...Z]
                    var checkTypes = CoreUtil.MapIndex(elementTypes, (t, idx) =>
                        idx < elementInfos.Length && (elementInfos[idx].flags & ElementFlags.Variadic) != 0 ? t : unknownType!)!;
                    if (CheckCrossProductUnion(checkTypes))
                    {
                        var iCapture = i;
                        return MapType(e, t => CreateNormalizedTupleTypeEx(target, CoreUtil.ReplaceElement(elementTypes, iCapture, t), objectFlags));
                    }
                }
            }
        }
        // We have optional, rest, or variadic elements that may need normalizing. Normalization ensures that all variadic
        // elements are generic and that the tuple type has one of the following layouts, disregarding variadic elements:
        // (1) Zero or more required elements, followed by zero or more optional elements, followed by zero or one rest element.
        // (2) Zero or more required elements, followed by a rest element, followed by zero or more required elements.
        // In either layout, zero or more generic variadic elements may be present at any location.
        // Note that the element types may contain an extra 'this' type argument that we want to ignore during normalization
        // and then just append to the normalized element types.
        var n = new TupleNormalizer();
        if (!n.Normalize(this, elementTypes[..elementInfos.Length], elementInfos))
        {
            return errorType!;
        }
        if (elementTypes.Length > elementInfos.Length)
        {
            n.types.Add(elementTypes[elementInfos.Length]);
        }
        var tupleTarget = GetTupleTargetType([.. n.infos], d.readonlyElements);
        if (tupleTarget == emptyGenericType)
        {
            return emptyObjectType!;
        }
        if (n.types.Count != 0)
        {
            return CreateTypeReferenceEx(tupleTarget, [.. n.types], objectFlags);
        }
        return tupleTarget;
    }

    // checker.go:23259-23261
    internal Type CreateNormalizedTupleType(Type target, Type[] elementTypes) =>
        CreateNormalizedTupleTypeEx(target, elementTypes, ObjectFlags.None);

    // checker.go:23376-23383
    internal Type[] GetElementTypes(Type t)
    {
        var typeArguments = GetTypeArguments(t);
        var arity = GetTypeReferenceArity(t);
        if (typeArguments.Length == arity)
        {
            return typeArguments;
        }
        return typeArguments[0..arity];
    }

    // checker.go:23385-23387
    internal int GetTypeReferenceArity(Type t) => t.TargetInterfaceType().TypeParameters()?.Length ?? 0;

    // checker.go:23389-23391
    internal bool IsArrayType(Type t) =>
        (t.objectFlags & ObjectFlags.Reference) != 0 && (t.Target() == globalArrayType || t.Target() == globalReadonlyArrayType);

    // checker.go:23393-23395
    internal bool IsReadonlyArrayType(Type t) =>
        (t.objectFlags & ObjectFlags.Reference) != 0 && t.Target() == globalReadonlyArrayType;

    // checker.go:23397-23399
    internal static bool IsTupleType(Type t) =>
        (t.objectFlags & ObjectFlags.Reference) != 0 && (t.Target().objectFlags & ObjectFlags.Tuple) != 0;

    // checker.go:23401-23403
    internal static bool IsMutableTupleType(Type t) => IsTupleType(t) && !t.TargetTupleType().readonlyElements;

    // checker.go:23405-23407 AND 24812-24814 (Go declares both a free function and an identical
    // Checker method; collapsed to a single static — all call sites are inside Checker).
    internal static bool IsGenericTupleType(Type t) => IsTupleType(t) && (t.TargetTupleType().combinedFlags & ElementFlags.Variadic) != 0;

    // checker.go:23409-23411
    internal static bool IsSingleElementGenericTupleType(Type t) =>
        IsGenericTupleType(t) && (t.TargetTupleType().elementInfos?.Length ?? 0) == 1;

    // checker.go:23413-23415
    internal bool IsArrayOrTupleType(Type t) => IsArrayType(t) || IsTupleType(t);

    // checker.go:23417-23419
    internal bool IsMutableArrayOrTuple(Type t) =>
        IsArrayType(t) && !IsReadonlyArrayType(t) || IsTupleType(t) && !t.TargetTupleType().readonlyElements;

    // checker.go:23421-23426
    public Type? GetElementTypeOfArrayType(Type t)
    {
        if (IsArrayType(t))
        {
            return GetTypeArguments(t)[0];
        }
        return null;
    }

    // checker.go:23428-23432
    public bool IsArrayLikeType(Type t) =>
        // A type is array-like if it is a reference to the global Array or global ReadonlyArray type,
        // or if it is not the undefined or null type and if it is assignable to ReadonlyArray<any>
        IsArrayType(t) || (t.flags & TypeFlags.Nullable) == 0 && IsTypeAssignableTo(t, anyReadonlyArrayType!);

    // checker.go:23434-23438
    internal bool IsMutableArrayLikeType(Type t) =>
        // A type is mutable-array-like if it is a reference to the global Array type, or if it is not the
        // any, undefined or null type and if it is assignable to Array<any>
        IsMutableArrayOrTuple(t) || (t.flags & (TypeFlags.Any | TypeFlags.Nullable)) == 0 && IsTypeAssignableTo(t, anyArrayType!);

    // checker.go:23440-23443
    internal bool IsEmptyArrayLiteralType(Type t)
    {
        var elementType = GetElementTypeOfArrayType(t);
        return elementType is not null && IsEmptyLiteralType(elementType);
    }

    // checker.go:23445-23450
    internal bool IsEmptyLiteralType(Type t)
    {
        if (strictNullChecks)
        {
            return t == implicitNeverType;
        }
        return t == undefinedWideningType;
    }

    // checker.go:23452-23462
    internal bool IsTupleLikeType(Type t)
    {
        if (IsTupleType(t) || GetPropertyOfType(t, "0") is not null)
        {
            return true;
        }
        if (IsArrayLikeType(t))
        {
            if (GetTypeOfPropertyOfType(t, "length") is { } lengthType)
            {
                return EveryType(lengthType, u => (u.flags & TypeFlags.NumberLiteral) != 0);
            }
        }
        return false;
    }

    // checker.go:23464-23466
    internal bool IsArrayOrTupleLikeType(Type t) => IsArrayLikeType(t) || IsTupleLikeType(t);

    // checker.go:23468-23470
    internal bool IsArrayOrTupleOrIntersection(Type t) =>
        (t.flags & TypeFlags.Intersection) != 0 && CoreUtil.Every(t.Types(), IsArrayOrTupleType);

    // checker.go:23472-23481
    internal Type? GetTupleElementType(Type t, int index)
    {
        var propType = GetTypeOfPropertyOfType(t, index.ToString());
        if (propType is not null)
        {
            return propType;
        }
        if (EveryType(t, IsTupleType))
        {
            return GetTupleElementTypeOutOfStartCount(t, new Ts.JsNum.Number(index), compilerOptions.NoUncheckedIndexedAccess == Ts.Core.Tristate.True ? undefinedType : null);
        }
        return null;
    }

    // Get type from reference to type alias. When a type alias is generic, the declared type of the type alias may include
    // references to the type parameters of the alias. We replace those with the actual type arguments by instantiating the
    // declared type. Instantiations are cached using the type identities of the type arguments as the key.
    // checker.go:23488-23547
    internal Type GetTypeFromTypeAliasReference(Node node, Symbol symbol)
    {
        var typeArguments = node.TypeArguments();
        if ((symbol.CheckFlags & CheckFlags.Unresolved) != 0)
        {
            var alias0 = new TypeAlias { symbol = symbol, typeArguments = CoreUtil.Map(typeArguments, GetTypeFromTypeNode) };
            var key = GetAliasKey(alias0);
            var errType = errorTypes.GetValueOrDefault(key);
            if (errType is null)
            {
                errType = NewIntrinsicType(TypeFlags.Any, "error");
                errType.alias = alias0;
                errorTypes[key] = errType;
            }
            return errType;
        }
        var t = GetDeclaredTypeOfSymbol(symbol);
        var typeParameters = typeAliasLinks.Get(symbol).typeParameters;
        if ((typeParameters?.Length ?? 0) != 0)
        {
            var numTypeArguments = typeArguments?.Length ?? 0;
            var minTypeArgumentCount = GetMinTypeArgumentCount(typeParameters);
            if (numTypeArguments < minTypeArgumentCount || numTypeArguments > typeParameters!.Length)
            {
                var message = minTypeArgumentCount == typeParameters!.Length
                    ? Diag.Generic_type_0_requires_1_type_argument_s
                    : Diag.Generic_type_0_requires_between_1_and_2_type_arguments;
                Error(node, message, SymbolToString(symbol), minTypeArgumentCount, typeParameters.Length);
                return errorType!;
            }
            // We refrain from associating a local type alias with an instantiation of a top-level type alias
            // because the local alias may end up being referenced in an inferred return type where it is not
            // accessible--which in turn may lead to a large structural expansion of the type when generating
            // a .d.ts file. See #43622 for an example.
            var aliasSymbol = GetAliasSymbolForTypeNode(node);
            Symbol? newAliasSymbol = null;
            if (aliasSymbol is not null && (IsLocalTypeAlias(symbol) || !IsLocalTypeAlias(aliasSymbol)))
            {
                newAliasSymbol = aliasSymbol;
            }
            Type[]? aliasTypeArguments = null;
            if (newAliasSymbol is not null)
            {
                aliasTypeArguments = GetTypeArgumentsForAliasSymbol(newAliasSymbol);
            }
            else if (NodeTests.IsTypeReferenceNode(node) || NodeTests.IsExpressionWithTypeArguments(node))
            {
                var aliasSymbol2 = ResolveTypeReferenceName(node, SymbolFlags.Alias, true /*ignoreErrors*/);
                // refers to an alias import/export/reexport - by making sure we use the target as an aliasSymbol,
                // we ensure the exported symbol is used to refer to the type when it is reserialized later
                if (aliasSymbol2 != unknownSymbol)
                {
                    var (resolved, _) = ResolveAlias(aliasSymbol2);
                    if (resolved is not null && (resolved.Flags & SymbolFlags.TypeAlias) != 0)
                    {
                        newAliasSymbol = resolved;
                        aliasTypeArguments = GetTypeArgumentsFromNode(node);
                    }
                }
            }
            TypeAlias? newAlias = null;
            if (newAliasSymbol is not null)
            {
                newAlias = new TypeAlias { symbol = newAliasSymbol, typeArguments = aliasTypeArguments };
            }
            return GetTypeAliasInstantiation(symbol, GetTypeArgumentsFromNode(node), newAlias);
        }
        if (CheckNoTypeArguments(node, symbol))
        {
            return t;
        }
        return errorType!;
    }

    // checker.go:23549-23571
    internal Type GetTypeAliasInstantiation(Symbol symbol, Type[]? typeArguments, TypeAlias? alias)
    {
        var t = GetDeclaredTypeOfSymbol(symbol);
        if (t == intrinsicMarkerType)
        {
            if (IntrinsicTypeKinds.TryGetValue(symbol.Name, out var typeKind) && (typeArguments?.Length ?? 0) == 1)
            {
                return typeKind switch
                {
                    IntrinsicTypeKind.NoInfer => GetNoInferType(typeArguments![0]),
                    _ => GetStringMappingType(symbol, typeArguments![0]),
                };
            }
        }
        var links = typeAliasLinks.Get(symbol);
        var typeParameters = links.typeParameters;
        var key = GetTypeAliasInstantiationKey(typeArguments, alias);
        var instantiation = links.instantiations?.GetValueOrDefault(key);
        if (instantiation is null)
        {
            var mapper = TypeMapper.NewTypeMapper(typeParameters!, FillMissingTypeArguments(typeArguments, typeParameters, GetMinTypeArgumentCount(typeParameters), AstUtilities.IsInJSFile(symbol.ValueDeclaration))!);
            instantiation = InstantiateTypeWithAlias(t, mapper, alias);
            links.instantiations![key] = instantiation;
        }
        return instantiation;
    }

    // checker.go:23573-23576
    internal static bool IsLocalTypeAlias(Symbol symbol)
    {
        var declaration = CoreUtil.Find(symbol.Declarations, IsTypeAlias);
        return declaration is not null && GetContainingFunction(declaration) is not null;
    }

    // checker.go:23578-23584
    public Type GetDeclaredTypeOfSymbol(Symbol symbol) => TryGetDeclaredTypeOfSymbol(symbol) ?? errorType!;

    // checker.go:23586-23602
    internal Type? TryGetDeclaredTypeOfSymbol(Symbol symbol)
    {
        if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
        {
            return GetDeclaredTypeOfClassOrInterface(symbol);
        }
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0)
        {
            return GetDeclaredTypeOfTypeParameter(symbol);
        }
        if ((symbol.Flags & SymbolFlags.TypeAlias) != 0)
        {
            return GetDeclaredTypeOfTypeAlias(symbol);
        }
        if ((symbol.Flags & SymbolFlags.Enum) != 0)
        {
            return GetDeclaredTypeOfEnum(symbol);
        }
        if ((symbol.Flags & SymbolFlags.EnumMember) != 0)
        {
            return GetDeclaredTypeOfEnumMember(symbol);
        }
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            return GetDeclaredTypeOfAlias(symbol);
        }
        return null;
    }

    // checker.go:23604-23617
    internal static Node? GetTypeReferenceName(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TypeReference:
                return node.AsTypeReferenceNode().TypeName;
            case SyntaxKind.ExpressionWithTypeArguments:
                // We only support expressions that are simple qualified names. For other
                // expressions this produces nil
                var expr = node.Expression()!;
                if (AstUtilities.IsEntityNameExpression(expr))
                {
                    return expr;
                }
                break;
        }
        return null;
    }

    // checker.go:23619-23625
    internal TypeAlias? GetAliasForTypeNode(Node node)
    {
        var symbol = GetAliasSymbolForTypeNode(node);
        if (symbol is not null)
        {
            return new TypeAlias { symbol = symbol, typeArguments = GetTypeArgumentsForAliasSymbol(symbol) };
        }
        return null;
    }

    // checker.go:23627-23636
    internal Symbol? GetAliasSymbolForTypeNode(Node node)
    {
        var host = node.Parent!;
        while (NodeTests.IsParenthesizedTypeNode(host) || NodeTests.IsTypeOperatorNode(host) && host.AsTypeOperatorNode().Operator == SyntaxKind.ReadonlyKeyword)
        {
            host = host.Parent!;
        }
        if (IsTypeAlias(host))
        {
            return GetSymbolOfDeclaration(host);
        }
        return null;
    }

    // checker.go:23638-23643
    internal Type[]? GetTypeArgumentsForAliasSymbol(Symbol? symbol)
    {
        if (symbol is not null)
        {
            return GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(symbol);
        }
        return null;
    }

    // checker.go:23645-23661
    internal Type[]? GetOuterTypeParametersOfClassOrInterface(Symbol symbol)
    {
        var declaration = symbol.ValueDeclaration;
        if ((symbol.Flags & (SymbolFlags.Class | SymbolFlags.Function)) == 0)
        {
            declaration = CoreUtil.Find(symbol.Declarations, d =>
            {
                if (NodeTests.IsInterfaceDeclaration(d))
                {
                    return true;
                }
                if (!NodeTests.IsVariableDeclaration(d))
                {
                    return false;
                }
                var initializer = d.Initializer();
                return initializer is not null && AstUtilities.IsFunctionExpressionOrArrowFunction(initializer);
            });
        }
        Debug.Assert(declaration is not null, "Class was missing valueDeclaration -OR- non-class had no interface declarations");
        return GetOuterTypeParameters(declaration!, false /*includeThisTypes*/);
    }

    // Return the outer type parameters of a node or undefined if the node has no outer type parameters.
    // checker.go:23664-23700
    internal Type[]? GetOuterTypeParameters(Node node, bool includeThisTypes)
    {
        while (true)
        {
            var next = node.Parent;
            if (next is null)
            {
                return null;
            }
            node = next;
            var kind = node.Kind;
            switch (kind)
            {
                case SyntaxKind.ClassDeclaration:
                case SyntaxKind.ClassExpression:
                case SyntaxKind.InterfaceDeclaration:
                case SyntaxKind.CallSignature:
                case SyntaxKind.ConstructSignature:
                case SyntaxKind.MethodSignature:
                case SyntaxKind.FunctionType:
                case SyntaxKind.ConstructorType:
                case SyntaxKind.FunctionDeclaration:
                case SyntaxKind.MethodDeclaration:
                case SyntaxKind.FunctionExpression:
                case SyntaxKind.ArrowFunction:
                case SyntaxKind.TypeAliasDeclaration:
                case SyntaxKind.JSTypeAliasDeclaration:
                case SyntaxKind.MappedType:
                case SyntaxKind.ConditionalType:
                    {
                        var outerTypeParameters = GetOuterTypeParameters(node, includeThisTypes);
                        if ((kind == SyntaxKind.FunctionExpression || kind == SyntaxKind.ArrowFunction || AstUtilities.IsObjectLiteralMethod(node)) && IsContextSensitive(node))
                        {
                            var signature = CoreUtil.FirstOrNil(GetSignaturesOfType(GetTypeOfSymbol(GetSymbolOfDeclaration(node)!), SignatureKind.Call));
                            if (signature is not null && (signature.typeParameters?.Length ?? 0) != 0)
                            {
                                return [.. outerTypeParameters ?? [], .. signature.typeParameters!];
                            }
                        }
                        if (kind == SyntaxKind.MappedType)
                        {
                            return [.. outerTypeParameters ?? [], GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(node.AsMappedTypeNode().TypeParameter!)!)];
                        }
                        if (kind == SyntaxKind.ConditionalType)
                        {
                            return [.. outerTypeParameters ?? [], .. GetInferTypeParameters(node) ?? []];
                        }
                        var outerAndOwnTypeParameters = AppendTypeParameters(outerTypeParameters, node.TypeParameters());
                        Type? thisType = null;
                        if (includeThisTypes && (kind == SyntaxKind.ClassDeclaration || kind == SyntaxKind.ClassExpression || kind == SyntaxKind.InterfaceDeclaration))
                        {
                            thisType = GetDeclaredTypeOfClassOrInterface(GetSymbolOfDeclaration(node)!).AsInterfaceType()!.thisType;
                        }
                        if (thisType is not null)
                        {
                            return [.. outerAndOwnTypeParameters ?? [], thisType];
                        }
                        return outerAndOwnTypeParameters;
                    }
            }
        }
    }

    // checker.go:23702-23710
    internal Type[]? GetInferTypeParameters(Node node)
    {
        Type[]? result = null;
        var locals = node.Locals();
        if (locals is not null)
        {
            // map-iter: 序无关 —— result accumulated via AppendIfUnique semantics; Go ranges a map here,
            // but tsgo relies on the values (declared type params) being order-independent for output.
            foreach (var symbol in locals.Values)
            {
                if ((symbol.Flags & SymbolFlags.TypeParameter) != 0)
                {
                    result = [.. result ?? [], GetDeclaredTypeOfSymbol(symbol)];
                }
            }
        }
        return result;
    }

    // The local type parameters are the combined set of type parameters from all declarations of the class,
    // interface, or type alias.
    // checker.go:23714-23716
    internal Type[]? GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(Symbol symbol) =>
        AppendLocalTypeParametersOfClassOrInterfaceOrTypeAlias(null, symbol);

    // checker.go:23718-23725
    internal Type[]? AppendLocalTypeParametersOfClassOrInterfaceOrTypeAlias(Type[]? types, Symbol symbol)
    {
        foreach (var node in symbol.Declarations ?? [])
        {
            if (AstUtilities.NodeKindIs(node, SyntaxKind.InterfaceDeclaration, SyntaxKind.ClassDeclaration, SyntaxKind.ClassExpression) || IsTypeAlias(node))
            {
                types = AppendTypeParameters(types, node.TypeParameters());
            }
        }
        return types;
    }

    // Appends the type parameters given by a list of declarations to a set of type parameters and returns the resulting set.
    // checker.go:23730-23735
    internal Type[]? AppendTypeParameters(Type[]? typeParameters, Node[]? declarations)
    {
        foreach (var declaration in declarations ?? [])
        {
            typeParameters = CoreUtil.AppendIfUnique(typeParameters, GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(declaration)!));
        }
        return typeParameters;
    }

    // checker.go:23737-23743
    internal Type GetDeclaredTypeOfTypeParameter(Symbol symbol)
    {
        var links = declaredTypeLinks.Get(symbol);
        links.declaredType ??= NewTypeParameter(symbol);
        return links.declaredType;
    }

    // checker.go:23745-23781
    internal Type GetDeclaredTypeOfTypeAlias(Symbol symbol)
    {
        var links = typeAliasLinks.Get(symbol);
        if (links.declaredType is null)
        {
            // Note that we use the links object as the target here because the symbol object is used as the unique
            // identity for resolution of the 'type' property in SymbolLinks.
            if (!PushTypeResolution(symbol, TypeSystemPropertyName.DeclaredType))
            {
                return errorType!;
            }
            var declaration = CoreUtil.Find(symbol.Declarations, IsTypeOrJSTypeAliasDeclaration)!;
            var typeNode = declaration.Type();
            var t = GetTypeFromTypeNode(typeNode!);
            if (PopTypeResolution())
            {
                var typeParameters = GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(symbol);
                if ((typeParameters?.Length ?? 0) != 0)
                {
                    // Initialize the instantiation cache for generic type aliases. The declared type corresponds to
                    // an instantiation of the type alias with the type parameters supplied as type arguments.
                    links.typeParameters = typeParameters;
                    links.instantiations = new Dictionary<CacheHashKey, Type>();
                    links.instantiations[GetTypeListKey(typeParameters)] = t;
                }
                if (t == intrinsicMarkerType && symbol.Name == "BuiltinIteratorReturn")
                {
                    t = GetBuiltinIteratorReturnType();
                }
            }
            else
            {
                var errorNode = declaration.Name();
                errorNode ??= declaration;
                Error(errorNode, Diag.Type_alias_0_circularly_references_itself, SymbolToString(symbol));
                t = errorType!;
            }
            links.declaredType ??= t;
        }
        return links.declaredType;
    }

    // checker.go:23783-23818
    internal Type GetDeclaredTypeOfEnum(Symbol symbol)
    {
        var links = declaredTypeLinks.Get(symbol);
        if (links.declaredType is null)
        {
            Type[]? memberTypeList = null;
            foreach (var declaration in symbol.Declarations ?? [])
            {
                if (declaration.Kind == SyntaxKind.EnumDeclaration)
                {
                    foreach (var member in declaration.Members() ?? [])
                    {
                        if (HasBindableName(member))
                        {
                            var memberSymbol = GetSymbolOfDeclaration(member)!;
                            var value = GetEnumMemberValue(member).Value;
                            Type memberType;
                            if (value is not null)
                            {
                                memberType = GetEnumLiteralType(value, symbol, memberSymbol);
                            }
                            else
                            {
                                memberType = CreateComputedEnumType(memberSymbol);
                            }
                            declaredTypeLinks.Get(memberSymbol).declaredType = GetFreshTypeOfLiteralType(memberType);
                            memberTypeList = [.. memberTypeList ?? [], memberType];
                        }
                    }
                }
            }
            Type enumType;
            if ((memberTypeList?.Length ?? 0) != 0)
            {
                enumType = GetUnionTypeEx(memberTypeList, UnionReduction.Literal, new TypeAlias { symbol = symbol }, null /*origin*/);
            }
            else
            {
                enumType = CreateComputedEnumType(symbol);
            }
            if ((enumType.flags & TypeFlags.Union) != 0)
            {
                enumType.flags |= TypeFlags.EnumLiteral;
                enumType.symbol = symbol;
            }
            links.declaredType = enumType;
        }
        return links.declaredType;
    }

    // checker.go:23820-23823
    internal Ts.Evaluator.Result GetEnumMemberValue(Node node)
    {
        ComputeEnumMemberValues(node.Parent!);
        return enumMemberLinks.Get(node).value;
    }

    // checker.go:23825-23833
    internal Type CreateComputedEnumType(Symbol symbol)
    {
        var regularType = NewLiteralType(TypeFlags.Enum, null, null);
        regularType.symbol = symbol;
        var freshType = NewLiteralType(TypeFlags.Enum, null, regularType);
        freshType.symbol = symbol;
        regularType.AsLiteralType().freshType = freshType;
        freshType.AsLiteralType().freshType = freshType;
        return regularType;
    }

    // checker.go:23835-23844
    internal Type GetDeclaredTypeOfEnumMember(Symbol symbol)
    {
        var links = declaredTypeLinks.Get(symbol);
        if (links.declaredType is null)
        {
            var enumType = GetDeclaredTypeOfEnum(GetParentOfSymbol(symbol)!);
            links.declaredType ??= enumType;
        }
        return links.declaredType;
    }

    // checker.go:23846-23864
    internal void ComputeEnumMemberValues(Node node)
    {
        var nodeLinks0 = nodeLinks.Get(node);
        if ((nodeLinks0.flags & NodeCheckFlags.EnumValuesComputed) == 0)
        {
            nodeLinks0.flags |= NodeCheckFlags.EnumValuesComputed;
            Ts.JsNum.Number? autoValue = new Ts.JsNum.Number(0);
            Node? previous = null;
            foreach (var member in node.Members() ?? [])
            {
                var result = ComputeEnumMemberValue(member, autoValue, previous);
                enumMemberLinks.Get(member).value = result;
                if (result.Value is Ts.JsNum.Number value)
                {
                    autoValue = new Ts.JsNum.Number(value.Value + 1);
                }
                else
                {
                    autoValue = null;
                }
                previous = member;
            }
        }
    }

    // checker.go:23866-23901
    internal Ts.Evaluator.Result ComputeEnumMemberValue(Node member, Ts.JsNum.Number? autoValue, Node? previous)
    {
        if (IsComputedNonLiteralName(member.Name()!))
        {
            Error(member.Name(), Diag.Computed_property_names_are_not_allowed_in_enums);
        }
        else if (NodeTests.IsBigIntLiteral(member.Name()!))
        {
            Error(member.Name(), Diag.An_enum_member_cannot_have_a_numeric_name);
        }
        else
        {
            var text = GetTextOfPropertyName(member.Name()!);
            if (IsNumericLiteralName(text) && !IsInfinityOrNaNString(text))
            {
                Error(member.Name(), Diag.An_enum_member_cannot_have_a_numeric_name);
            }
        }
        if (member.Initializer() is not null)
        {
            return ComputeConstantEnumMemberValue(member);
        }
        // In ambient non-const numeric enum declarations, enum members without initializers are
        // considered computed members (as opposed to having auto-incremented values).
        if ((member.Parent!.Flags & NodeFlags.Ambient) != 0 && !AstUtilities.IsEnumConst(member.Parent!))
        {
            return Ts.Evaluator.Result.NewResult(null, false, false, false);
        }
        // If the member declaration specifies no value, the member is considered a constant enum member.
        // If the member is the first member in the enum declaration, it is assigned the value zero.
        // Otherwise, it is assigned the value of the immediately preceding member plus one, and an error
        // occurs if the immediately preceding member is not a constant enum member.
        if (autoValue is null)
        {
            Error(member.Name(), Diag.Enum_member_must_have_initializer);
            return Ts.Evaluator.Result.NewResult(null, false, false, false);
        }
        if (compilerOptions.GetIsolatedModules() && previous is not null && previous.Initializer() is not null)
        {
            var prevValue = GetEnumMemberValue(previous);
            var prevIsNum = prevValue.Value is Ts.JsNum.Number;
            if (!prevIsNum || prevValue.ResolvedOtherFiles)
            {
                Error(member.Name(), Diag.Enum_member_following_a_non_literal_numeric_member_must_have_an_initializer_when_isolatedModules_is_enabled);
            }
        }
        return Ts.Evaluator.Result.NewResult(autoValue.Value, false, false, false);
    }

    // checker.go:23903-23930
    internal Ts.Evaluator.Result ComputeConstantEnumMemberValue(Node member)
    {
        var isConstEnum = AstUtilities.IsEnumConst(member.Parent!);
        var initializer = member.Initializer()!;
        var result = evaluate(initializer, member);
        if (result.Value is not null)
        {
            if (isConstEnum)
            {
                if (result.Value is Ts.JsNum.Number numValue && (numValue.IsInf() || numValue.IsNaN()))
                {
                    Error(initializer, numValue.IsNaN()
                        ? Diag.X_const_enum_member_initializer_was_evaluated_to_disallowed_value_NaN
                        : Diag.X_const_enum_member_initializer_was_evaluated_to_a_non_finite_value);
                }
            }
            if (compilerOptions.GetIsolatedModules())
            {
                if (result.Value is string && !result.IsSyntacticallyString)
                {
                    var memberName = member.Parent!.Name()!.Text() + "." + member.Name()!.Text();
                    Error(initializer, Diag.X_0_has_a_string_type_but_must_have_syntactically_recognizable_string_syntax_when_isolatedModules_is_enabled, memberName);
                }
            }
        }
        else if (isConstEnum)
        {
            Error(initializer, Diag.X_const_enum_member_initializers_must_be_constant_expressions);
        }
        else if ((member.Parent!.Flags & NodeFlags.Ambient) != 0)
        {
            Error(initializer, Diag.In_ambient_enum_declarations_member_initializer_must_be_constant_expression);
        }
        else
        {
            CheckTypeAssignableTo(CheckExpression(initializer), numberType!, initializer, Diag.Type_0_is_not_assignable_to_type_1_as_required_for_computed_enum_member_values);
        }
        return result;
    }

    // checker.go:23932-23983
    internal Ts.Evaluator.Result EvaluateEntity(Node expr, Node? location)
    {
        switch (expr.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PropertyAccessExpression:
                {
                    var symbol = ResolveEntityName(expr, SymbolFlags.Value, true /*ignoreErrors*/, false, null);
                    if (symbol is null)
                    {
                        return Ts.Evaluator.Result.NewResult(null, false, false, false);
                    }
                    if (expr.Kind == SyntaxKind.Identifier)
                    {
                        if (IsInfinityOrNaNString(expr.Text()) && symbol == GetGlobalSymbol(expr.Text(), SymbolFlags.Value, null /*diagnostic*/))
                        {
                            // Technically we resolved a global lib file here, but the decision to treat this as numeric
                            // is more predicated on the fact that the single-file resolution *didn't* resolve to a
                            // different meaning of `Infinity` or `NaN`. Transpilers handle this no problem.
                            return Ts.Evaluator.Result.NewResult(Ts.JsNum.JsNum.FromString(expr.Text()), false, false, false);
                        }
                    }
                    if ((symbol.Flags & SymbolFlags.EnumMember) != 0)
                    {
                        if (location is not null)
                        {
                            return EvaluateEnumMember(expr, symbol, location);
                        }
                        return GetEnumMemberValue(symbol.ValueDeclaration!);
                    }
                    if (IsConstantVariable(symbol))
                    {
                        var declaration = symbol.ValueDeclaration;
                        if (declaration is not null && NodeTests.IsVariableDeclaration(declaration) && declaration.Type() is null && declaration.Initializer() is not null &&
                            (location is null || declaration != location && IsBlockScopedNameDeclaredBeforeUse(declaration, location)))
                        {
                            var result = evaluate(declaration.Initializer()!, declaration);
                            if (location is not null && AstUtilities.GetSourceFileOfNode(location) != AstUtilities.GetSourceFileOfNode(declaration))
                            {
                                return Ts.Evaluator.Result.NewResult(result.Value, false, true, true);
                            }
                            return Ts.Evaluator.Result.NewResult(result.Value, result.IsSyntacticallyString, result.ResolvedOtherFiles, true /*hasExternalReferences*/);
                        }
                    }
                    return Ts.Evaluator.Result.NewResult(null, false, false, false);
                }
            case SyntaxKind.ElementAccessExpression:
                {
                    var root = expr.Expression()!;
                    if (AstUtilities.IsEntityNameExpression(root) && AstUtilities.IsStringLiteralLike(expr.AsElementAccessExpression().ArgumentExpression!))
                    {
                        var rootSymbol = ResolveEntityName(root, SymbolFlags.Value, true /*ignoreErrors*/, false, null);
                        if (rootSymbol is not null && (rootSymbol.Flags & SymbolFlags.Enum) != 0)
                        {
                            var name = expr.AsElementAccessExpression().ArgumentExpression!.Text();
                            var member = rootSymbol.Exports?.GetValueOrDefault(name);
                            if (member is not null)
                            {
                                if (location is not null)
                                {
                                    return EvaluateEnumMember(expr, member, location);
                                }
                                return GetEnumMemberValue(member.ValueDeclaration!);
                            }
                        }
                    }
                    return Ts.Evaluator.Result.NewResult(null, false, false, false);
                }
        }
        throw new InvalidOperationException("Unhandled case in evaluateEntity");
    }

    // checker.go:23985-24000
    internal Ts.Evaluator.Result EvaluateEnumMember(Node expr, Symbol symbol, Node location)
    {
        var declaration = symbol.ValueDeclaration;
        if (declaration is null || declaration == location)
        {
            Error(expr, Diag.Property_0_is_used_before_being_assigned, SymbolToString(symbol));
            return Ts.Evaluator.Result.NewResult(null, false, false, false);
        }
        if (!IsBlockScopedNameDeclaredBeforeUse(declaration, location))
        {
            Error(expr, Diag.A_member_initializer_in_a_enum_declaration_cannot_reference_members_declared_after_it_including_members_defined_in_other_enums);
            return Ts.Evaluator.Result.NewResult(new Ts.JsNum.Number(0), false, false, false);
        }
        var value = GetEnumMemberValue(declaration);
        if (location.Parent != declaration.Parent)
        {
            return Ts.Evaluator.Result.NewResult(value.Value, value.IsSyntacticallyString, value.ResolvedOtherFiles, true /*hasExternalReferences*/);
        }
        return value;
    }

    // checker.go:24002-24008
    internal Type GetDeclaredTypeOfAlias(Symbol symbol)
    {
        var links = declaredTypeLinks.Get(symbol);
        var (aliasTarget, _) = ResolveAlias(symbol);
        links.declaredType ??= GetDeclaredTypeOfSymbol(aliasTarget!);
        return links.declaredType;
    }

    // checker.go:24010-24021
    internal Type GetTypeFromTypeQueryNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            // TypeScript 1.0 spec (April 2014): 3.6.3
            // The expression is processed as an identifier expression (section 4.3)
            // or property access expression(section 4.10),
            // the widened type(section 3.9) of which becomes the result.
            var t = CheckExpressionWithTypeArguments(node);
            links.resolvedType = GetRegularTypeOfLiteralType(GetWidenedType(t));
        }
        return links.resolvedType;
    }

    // checker.go:24023-24050
    internal Type GetTypeFromArrayOrTupleTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var target = GetArrayOrTupleTargetType(node);
            if (target == emptyGenericType)
            {
                links.resolvedType = emptyObjectType!;
            }
            else if (!(node.Kind == SyntaxKind.TupleType && CoreUtil.Some(node.Elements(), IsVariadicTupleElement)) && IsDeferredTypeReferenceNode(node, false))
            {
                if (node.Kind == SyntaxKind.TupleType && (node.Elements()?.Length ?? 0) == 0)
                {
                    links.resolvedType = target;
                }
                else
                {
                    links.resolvedType = CreateDeferredTypeReference(target, node, null /*mapper*/, null /*alias*/);
                }
            }
            else
            {
                Type[] elementTypes;
                if (node.Kind == SyntaxKind.ArrayType)
                {
                    elementTypes = [GetTypeFromTypeNode(node.AsArrayTypeNode().ElementType!)];
                }
                else
                {
                    elementTypes = CoreUtil.Map(node.Elements(), GetTypeFromTypeNode) ?? [];
                }
                if ((target.objectFlags & ObjectFlags.Tuple) != 0)
                {
                    links.resolvedType = CreateNormalizedTupleTypeEx(target, elementTypes, ObjectFlags.FromTypeNode);
                }
                else
                {
                    links.resolvedType = CreateTypeReferenceEx(target, elementTypes, ObjectFlags.FromTypeNode);
                }
            }
        }
        return links.resolvedType;
    }

    // checker.go:24052-24054
    internal bool IsVariadicTupleElement(Node node) => (GetTupleElementFlags(node) & ElementFlags.Variadic) != 0;

    // checker.go:24056-24066
    internal Type GetArrayOrTupleTargetType(Node node)
    {
        var readonlyElements = IsReadonlyTypeOperator(node.Parent!);
        var elementType = GetArrayElementTypeNode(node);
        if (elementType is not null)
        {
            if (readonlyElements)
            {
                return globalReadonlyArrayType!;
            }
            return globalArrayType!;
        }
        return GetTupleTargetType(CoreUtil.Map(node.Elements(), GetTupleElementInfo) ?? [], readonlyElements);
    }

    // checker.go:24068-24070
    internal bool IsReadonlyTypeOperator(Node node) =>
        NodeTests.IsTypeOperatorNode(node) && node.AsTypeOperatorNode().Operator == SyntaxKind.ReadonlyKeyword;

    // checker.go:24072-24082
    internal Type GetTypeFromNamedTupleTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            if (node.AsNamedTupleMember().DotDotDotToken is not null)
            {
                links.resolvedType = GetTypeFromRestTypeNode(node);
            }
            else
            {
                links.resolvedType = AddOptionalityEx(GetTypeFromTypeNode(node.Type()!), true /*isProperty*/, node.QuestionToken() is not null);
            }
        }
        return links.resolvedType;
    }

    // checker.go:24084-24091
    internal Type GetTypeFromRestTypeNode(Node node)
    {
        var typeNode = node.Type()!;
        var elementTypeNode = GetArrayElementTypeNode(typeNode);
        if (elementTypeNode is not null)
        {
            typeNode = elementTypeNode;
        }
        return GetTypeFromTypeNode(typeNode);
    }

    // checker.go:24093-24111
    internal Node? GetArrayElementTypeNode(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.ParenthesizedType:
                return GetArrayElementTypeNode(node.Type()!);
            case SyntaxKind.TupleType:
                if ((node.Elements()?.Length ?? 0) == 1)
                {
                    node = node.Elements()![0];
                    if (node.Kind == SyntaxKind.RestType)
                    {
                        return GetArrayElementTypeNode(node.Type()!);
                    }
                    if (node.Kind == SyntaxKind.NamedTupleMember && node.AsNamedTupleMember().DotDotDotToken is not null)
                    {
                        return GetArrayElementTypeNode(node.Type()!);
                    }
                }
                break;
            case SyntaxKind.ArrayType:
                return node.AsArrayTypeNode().ElementType;
        }
        return null;
    }

    // checker.go:24113-24115
    internal Type GetTypeFromOptionalTypeNode(Node node) =>
        AddOptionalityEx(GetTypeFromTypeNode(node.Type()!), true /*isProperty*/, true /*isOptional*/);

    // checker.go:24117-24124
    internal Type GetTypeFromUnionTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var alias = GetAliasForTypeNode(node);
            links.resolvedType = GetUnionTypeEx(CoreUtil.Map(node.AsUnionTypeNode().Types!.Nodes, GetTypeFromTypeNode), UnionReduction.Literal, alias, null /*origin*/);
        }
        return links.resolvedType;
    }

    // checker.go:24126-24145
    internal Type GetTypeFromIntersectionTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var alias = GetAliasForTypeNode(node);
            var types = CoreUtil.Map(node.AsIntersectionTypeNode().Types!.Nodes, GetTypeFromTypeNode) ?? [];
            // We perform no supertype reduction for X & {} or {} & X, where X is one of string, number, bigint,
            // or a pattern literal template type. This enables union types like "a" | "b" | string & {} or
            // "aa" | "ab" | `a${string}` which preserve the literal types for purposes of statement completion.
            var noSupertypeReduction = false;
            if (types.Length == 2)
            {
                var emptyIndex = Array.IndexOf(types, emptyTypeLiteralType);
                if (emptyIndex >= 0)
                {
                    var t = types[1 - emptyIndex];
                    noSupertypeReduction = (t.flags & (TypeFlags.String | TypeFlags.Number | TypeFlags.BigInt)) != 0 || (t.flags & TypeFlags.TemplateLiteral) != 0 && IsPatternLiteralType(t);
                }
            }
            links.resolvedType = GetIntersectionTypeEx(types, noSupertypeReduction ? IntersectionFlags.NoSupertypeReduction : 0, alias);
        }
        return links.resolvedType;
    }

    // checker.go:24147-24161
    internal Type GetTypeFromTemplateTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var spans = node.AsTemplateLiteralTypeNode().TemplateSpans!;
            var texts = new string[spans.Nodes.Length + 1];
            var types = new Type[spans.Nodes.Length];
            texts[0] = node.AsTemplateLiteralTypeNode().Head!.Text();
            for (int i = 0; i < spans.Nodes.Length; i++)
            {
                var span = spans.Nodes[i];
                texts[i + 1] = span.AsTemplateLiteralTypeSpan().Literal!.Text();
                types[i] = GetTypeFromTypeNode(span.Type()!);
            }
            links.resolvedType = GetTemplateLiteralType(texts, types);
        }
        return links.resolvedType;
    }

    // checker.go:24163-24175
    internal Type GetTypeFromMappedTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var t = NewObjectType(ObjectFlags.Mapped, node.Symbol());
            t.AsMappedType()!.declaration = node.AsMappedTypeNode();
            t.alias = GetAliasForTypeNode(node);
            links.resolvedType = t;
            // Eagerly resolve the constraint type which forces an error if the constraint type circularly
            // references itself through one or more type aliases.
            GetConstraintTypeFromMappedType(t);
        }
        return links.resolvedType;
    }

    // checker.go:24177-24206
    internal Type GetTypeFromConditionalTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var checkType = GetTypeFromTypeNode(node.AsConditionalTypeNode().CheckType!);
            var alias = GetAliasForTypeNode(node);
            var allOuterTypeParameters = GetOuterTypeParameters(node, true /*includeThisTypes*/);
            Type[]? outerTypeParameters;
            if (alias is not null && (alias.typeArguments?.Length ?? 0) != 0)
            {
                outerTypeParameters = allOuterTypeParameters;
            }
            else
            {
                outerTypeParameters = CoreUtil.Filter(allOuterTypeParameters, tp => IsTypeParameterPossiblyReferenced(tp, node));
            }
            var root = new ConditionalRoot
            {
                node = node.AsConditionalTypeNode(),
                checkType = checkType,
                extendsType = GetTypeFromTypeNode(node.AsConditionalTypeNode().ExtendsType!),
                isDistributive = (checkType.flags & TypeFlags.TypeParameter) != 0,
                inferTypeParameters = GetInferTypeParameters(node),
                outerTypeParameters = outerTypeParameters,
                instantiations = null,
                alias = alias,
            };
            links.resolvedType = GetConditionalType(root, null /*mapper*/, false /*forConstraint*/, null);
            if (outerTypeParameters is not null)
            {
                root.instantiations = new Dictionary<CacheHashKey, Type>();
                root.instantiations[GetConditionalTypeKey(outerTypeParameters, null /*alias*/, false /*forConstraint*/)] = links.resolvedType;
            }
        }
        return links.resolvedType;
    }

    // checker.go:24208-24352
    internal Type GetConditionalType(ConditionalRoot root, TypeMapper? mapper, bool forConstraint, TypeAlias? alias)
    {
        Type? result = null;
        Type[]? extraTypes = null;
        var tailCount = 0;
        // We loop here for an immediately nested conditional type in the false position, effectively treating
        // types of the form 'A extends B ? X : C extends D ? Y : E extends F ? Z : ...' as a single construct for
        // purposes of resolution. We also loop here when resolution of a conditional type ends in resolution of
        // another (or, through recursion, possibly the same) conditional type. In the potentially tail-recursive
        // cases we increment the tail recursion counter and stop after 1000 iterations.
        while (true)
        {
            if (tailCount == 1000)
            {
                Error(currentNode, Diag.Type_instantiation_is_excessively_deep_and_possibly_infinite);
                return errorType!;
            }
            var checkType = InstantiateType(GetActualTypeVariable(root.checkType!), mapper);
            var extendsType = InstantiateType(root.extendsType!, mapper);
            if (checkType == errorType || extendsType == errorType)
            {
                return errorType!;
            }
            if (checkType == wildcardType || extendsType == wildcardType)
            {
                return wildcardType!;
            }
            var checkTypeNode = AstUtilities.SkipTypeParentheses(root.node!.CheckType!);
            var extendsTypeNode = AstUtilities.SkipTypeParentheses(root.node!.ExtendsType!);
            // When the check and extends types are simple tuple types of the same arity, we defer resolution of the
            // conditional type when any tuple elements are generic. This is such that non-distributable conditional
            // types can be written `[X] extends [Y] ? ...` and be deferred similarly to `X extends Y ? ...`.
            var checkTuples = IsSimpleTupleType(checkTypeNode) && IsSimpleTupleType(extendsTypeNode) && (checkTypeNode.Elements()?.Length ?? 0) == (extendsTypeNode.Elements()?.Length ?? 0);
            var checkTypeDeferred = IsDeferredType(checkType, checkTuples);
            TypeMapper? combinedMapper = null;
            if ((root.inferTypeParameters?.Length ?? 0) != 0)
            {
                // When we're looking at making an inference for an infer type, when we get its constraint, it'll automagically be
                // instantiated with the context, so it doesn't need the mapper for the inference context - however the constraint
                // may refer to another _root_, _uncloned_ `infer` type parameter [1], or to something mapped by `mapper` [2].
                var context = NewInferenceContext(root.inferTypeParameters, null /*signature*/, InferenceFlags.None, null);
                if (mapper is not null)
                {
                    context.nonFixingMapper = CombineTypeMappers(context.nonFixingMapper, mapper);
                }
                if (!checkTypeDeferred)
                {
                    // We don't want inferences from constraints as they may cause us to eagerly resolve the
                    // conditional type instead of deferring resolution. Also, we always want strict function
                    // types rules (i.e. proper contravariance) for inferences.
                    InferTypes(context.inferences, checkType, extendsType, InferencePriority.NoConstraints | InferencePriority.AlwaysStrict, false);
                }
                // It's possible for 'infer T' type parameters to be given uninstantiated constraints when the
                // those type parameters are used in type references (see getInferredTypeParameterConstraint). For
                // that reason we need context.mapper to be first in the combined mapper. See #42636 for examples.
                if (mapper is not null)
                {
                    combinedMapper = CombineTypeMappers(context.mapper, mapper);
                }
                else
                {
                    combinedMapper = context.mapper;
                }
            }
            // Instantiate the extends type including inferences for 'infer T' type parameters
            Type inferredExtendsType;
            if (combinedMapper is not null)
            {
                inferredExtendsType = InstantiateType(root.extendsType!, combinedMapper);
            }
            else
            {
                inferredExtendsType = extendsType;
            }
            // We attempt to resolve the conditional type only when the check and extends types are non-generic
            if (!checkTypeDeferred && !IsDeferredType(inferredExtendsType, checkTuples))
            {
                // Return falseType for a definitely false extends check.
                if ((inferredExtendsType.flags & TypeFlags.AnyOrUnknown) == 0 && ((checkType.flags & TypeFlags.Any) != 0 || !IsTypeAssignableTo(GetPermissiveInstantiation(checkType), GetPermissiveInstantiation(inferredExtendsType))))
                {
                    // Return union of trueType and falseType for 'any' since it matches anything. Furthermore, for a
                    // distributive conditional type applied to the constraint of a type variable, include trueType if
                    // there are possible values of the check type that are also possible values of the extends type.
                    if ((checkType.flags & TypeFlags.Any) != 0 || forConstraint && (inferredExtendsType.flags & TypeFlags.Never) == 0 && SomeType(GetPermissiveInstantiation(inferredExtendsType), t => IsTypeAssignableTo(t, GetPermissiveInstantiation(checkType))))
                    {
                        extraTypes = [.. extraTypes ?? [], InstantiateType(GetTypeFromTypeNode(root.node!.TrueType!), CoreUtil.OrElse(combinedMapper, mapper))];
                    }
                    // If falseType is an immediately nested conditional type that isn't distributive or has an
                    // identical checkType, switch to that type and loop.
                    var falseType = GetTypeFromTypeNode(root.node!.FalseType!);
                    if ((falseType.flags & TypeFlags.Conditional) != 0)
                    {
                        var newRoot = falseType.AsConditionalType().root;
                        if (newRoot.node!.Parent == root.node!.AsNode() && (!newRoot.isDistributive || newRoot.checkType == root.checkType))
                        {
                            root = newRoot;
                            continue;
                        }
                        var (newRoot2, newRootMapper) = GetTailRecursionRoot(falseType, mapper);
                        if (newRoot2 is not null)
                        {
                            root = newRoot2;
                            mapper = newRootMapper;
                            alias = null;
                            if (newRoot2.alias is not null)
                            {
                                tailCount++;
                            }
                            continue;
                        }
                    }
                    result = InstantiateType(falseType, mapper);
                    break;
                }
                // Return trueType for a definitely true extends check.
                if ((inferredExtendsType.flags & TypeFlags.AnyOrUnknown) != 0 || IsTypeAssignableTo(GetRestrictiveInstantiation(checkType), GetRestrictiveInstantiation(inferredExtendsType)))
                {
                    var trueType = GetTypeFromTypeNode(root.node!.TrueType!);
                    var trueMapper = CoreUtil.OrElse(combinedMapper, mapper);
                    var (newRoot, newRootMapper) = GetTailRecursionRoot(trueType, trueMapper);
                    if (newRoot is not null)
                    {
                        root = newRoot;
                        mapper = newRootMapper;
                        alias = null;
                        if (newRoot.alias is not null)
                        {
                            tailCount++;
                        }
                        continue;
                    }
                    result = InstantiateType(trueType, trueMapper);
                    break;
                }
            }
            // Return a deferred type for a check that is neither definitely true nor definitely false
            result = NewConditionalType(root, mapper, combinedMapper);
            if (alias is not null)
            {
                result.alias = alias;
            }
            else
            {
                result.alias = InstantiateTypeAlias(root.alias, mapper);
            }
            break;
        }
        if (extraTypes is not null)
        {
            return GetUnionType([.. extraTypes, result!]);
        }
        return result!;
    }

    // We tail-recurse for generic conditional types that (a) have not already been evaluated and cached, and
    // (b) are non distributive, have a check type that is unaffected by instantiation, or have a non-union check
    // type. Note that recursion is possible only through aliased conditional types, so we only increment the tail
    // recursion counter for those.
    // checker.go:24358-24375
    internal (ConditionalRoot?, TypeMapper?) GetTailRecursionRoot(Type newType, TypeMapper? newMapper)
    {
        if ((newType.flags & TypeFlags.Conditional) != 0 && newMapper is not null)
        {
            var newRoot = newType.AsConditionalType().root;
            if ((newRoot.outerTypeParameters?.Length ?? 0) != 0)
            {
                var typeParamMapper = CombineTypeMappers(newType.AsConditionalType().mapper, newMapper);
                var typeArguments = CoreUtil.Map(newRoot.outerTypeParameters, t => typeParamMapper!.Map(t))!;
                var newRootMapper = TypeMapper.NewTypeMapper(newRoot.outerTypeParameters!, typeArguments);
                Type? newCheckType = null;
                if (newRoot.isDistributive)
                {
                    newCheckType = newRootMapper.Map(newRoot.checkType!);
                }
                if (newCheckType is null || newCheckType == newRoot.checkType || (newCheckType.flags & (TypeFlags.Union | TypeFlags.Never)) == 0)
                {
                    return (newRoot, newRootMapper);
                }
            }
        }
        return (null, null);
    }

    // checker.go:24377-24381
    internal bool IsSimpleTupleType(Node node) =>
        NodeTests.IsTupleTypeNode(node) && (node.Elements()?.Length ?? 0) > 0 && !CoreUtil.Some(node.Elements(), e =>
            NodeTests.IsOptionalTypeNode(e) || NodeTests.IsRestTypeNode(e) || NodeTests.IsNamedTupleMember(e) && (e.QuestionToken() is not null || e.AsNamedTupleMember().DotDotDotToken is not null));

    // checker.go:24383-24385
    internal bool IsDeferredType(Type t, bool checkTuples) =>
        IsGenericType(t) || checkTuples && IsTupleType(t) && CoreUtil.Some(GetElementTypes(t), IsGenericType);

    // checker.go:24387-24398
    internal Type GetPermissiveInstantiation(Type t)
    {
        if ((t.flags & (TypeFlags.Primitive | TypeFlags.AnyOrUnknown | TypeFlags.Never)) != 0)
        {
            return t;
        }
        var key = new CachedTypeKey(CachedTypeKind.PermissiveInstantiation, t.id);
        if (cachedTypes.GetValueOrDefault(key) is { } cached)
        {
            return cached;
        }
        var result = InstantiateType(t, permissiveMapper);
        cachedTypes[key] = result;
        return result;
    }

    // checker.go:24400-24417
    internal Type GetRestrictiveInstantiation(Type t)
    {
        if ((t.flags & (TypeFlags.Primitive | TypeFlags.AnyOrUnknown | TypeFlags.Never)) != 0)
        {
            return t;
        }
        var key = new CachedTypeKey(CachedTypeKind.RestrictiveInstantiation, t.id);
        if (cachedTypes.GetValueOrDefault(key) is { } cached)
        {
            return cached;
        }
        var result = InstantiateType(t, restrictiveMapper);
        cachedTypes[key] = result;
        // We set the following so we don't attempt to set the restrictive instance of a restrictive instance
        // which is redundant - we'll produce new type identities, but all type params have already been mapped.
        cachedTypes[new CachedTypeKey(CachedTypeKind.RestrictiveInstantiation, result.id)] = result;
        return result;
    }

    // checker.go:24419-24431
    internal Type GetRestrictiveTypeParameter(Type t)
    {
        if (t.AsTypeParameter().constraint is null && GetConstraintDeclaration(t) is null || t.AsTypeParameter().constraint == noConstraintType)
        {
            return t;
        }
        var key = new CachedTypeKey(CachedTypeKind.RestrictiveTypeParameter, t.id);
        if (cachedTypes.GetValueOrDefault(key) is { } cached)
        {
            return cached;
        }
        var result = NewTypeParameter(t.symbol);
        result.AsTypeParameter().constraint = noConstraintType;
        cachedTypes[key] = result;
        return result;
    }

    // checker.go:24433-24438
    internal Type RestrictiveMapperWorker(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            return GetRestrictiveTypeParameter(t);
        }
        return t;
    }

    // checker.go:24440-24445
    internal Type PermissiveMapperWorker(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            return wildcardType!;
        }
        return t;
    }

    // checker.go:24447-24453
    internal Type GetTrueTypeFromConditionalType(Type t)
    {
        var d = t.AsConditionalType();
        d.resolvedTrueType ??= InstantiateType(GetTypeFromTypeNode(d.root.node!.TrueType!), d.mapper);
        return d.resolvedTrueType;
    }

    // checker.go:24455-24461
    internal Type GetFalseTypeFromConditionalType(Type t)
    {
        var d = t.AsConditionalType();
        d.resolvedFalseType ??= InstantiateType(GetTypeFromTypeNode(d.root.node!.FalseType!), d.mapper);
        return d.resolvedFalseType;
    }

    // checker.go:24463-24473
    internal Type GetInferredTrueTypeFromConditionalType(Type t)
    {
        var d = t.AsConditionalType();
        if (d.resolvedInferredTrueType is null)
        {
            if (d.combinedMapper is not null)
            {
                d.resolvedInferredTrueType = InstantiateType(GetTypeFromTypeNode(d.root.node!.TrueType!), d.combinedMapper);
            }
            else
            {
                d.resolvedInferredTrueType = GetTrueTypeFromConditionalType(t);
            }
        }
        return d.resolvedInferredTrueType;
    }

    // checker.go:24475-24481
    internal Type GetTypeFromInferTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        links.resolvedType ??= GetDeclaredTypeOfTypeParameter(GetSymbolOfDeclaration(node.AsInferTypeNode().TypeParameter!)!);
        return links.resolvedType;
    }

    // checker.go:24483-24556
    internal Type GetTypeFromImportTypeNode(Node node)
    {
        var links = typeNodeLinks.Get(node);
        if (links.resolvedType is null)
        {
            var n = node.AsImportTypeNode();
            if (!IsLiteralImportTypeNode(node))
            {
                Error(n.Argument, Diag.String_literal_expected);
                symbolNodeLinks.Get(node).resolvedSymbol = unknownSymbol;
                links.resolvedType = errorType!;
                return links.resolvedType;
            }
            var targetMeaning = n.IsTypeOf ? SymbolFlags.Value : SymbolFlags.Type;
            // TODO: Future work: support unions/generics/whatever via a deferred import-type
            var innerModuleSymbol = ResolveExternalModuleName(node, n.Argument!.AsLiteralTypeNode().Literal!, false /*ignoreErrors*/);
            if (innerModuleSymbol is null)
            {
                symbolNodeLinks.Get(node).resolvedSymbol = unknownSymbol;
                links.resolvedType = errorType!;
                return links.resolvedType;
            }
            var moduleSymbol = ResolveExternalModuleSymbol(innerModuleSymbol, false /*dontResolveAlias*/);
            if (!AstUtilities.NodeIsMissing(n.Qualifier))
            {
                var nameChain = GetIdentifierChain(n.Qualifier!);
                var currentNamespace = moduleSymbol;
                for (int i = 0; i < nameChain.Length; i++)
                {
                    var current = nameChain[i];
                    var meaning = SymbolFlags.Namespace;
                    if (i == nameChain.Length - 1)
                    {
                        meaning = targetMeaning;
                    }
                    // typeof a.b.c is normally resolved using `checkExpression` which in turn defers to `checkQualifiedName`
                    var mergedResolvedSymbol = GetMergedSymbol(ResolveSymbol(currentNamespace));
                    Symbol? symbolFromVariable = null;
                    Symbol? symbolFromModule = null;
                    if (n.IsTypeOf)
                    {
                        symbolFromVariable = GetPropertyOfTypeEx(GetTypeOfSymbol(mergedResolvedSymbol!), current.Text(), false /*skipObjectFunctionPropertyAugment*/, true /*includeTypeOnlyMembers*/);
                    }
                    else
                    {
                        symbolFromModule = GetSymbol(GetExportsOfSymbol(mergedResolvedSymbol!), current.Text(), meaning);
                        if (symbolFromModule is null)
                        {
                            // a CommonJS module might have typedefs exported alongside an export=
                            // !!!
                            var immediateModuleSymbol = ResolveExternalModuleSymbol(innerModuleSymbol, true /*dontResolveAlias*/);
                            if (immediateModuleSymbol is not null && CoreUtil.Some(immediateModuleSymbol.Declarations, d =>
                                AstUtilities.GetAssignmentDeclarationKind(d) == JSDeclarationKind.ModuleExports))
                            {
                                symbolFromModule = GetSymbol(GetExportsOfSymbol(immediateModuleSymbol.Parent!), current.Text(), meaning);
                            }
                        }
                    }
                    var next = CoreUtil.OrElse(symbolFromModule, symbolFromVariable);
                    if (next is null)
                    {
                        Error(current, Diag.Namespace_0_has_no_exported_member_1, GetFullyQualifiedName(currentNamespace!, null), global::Ts.Scanner.Scanner.DeclarationNameToString(current));
                        links.resolvedType = errorType!;
                        return links.resolvedType;
                    }
                    symbolNodeLinks.Get(current).resolvedSymbol = next;
                    symbolNodeLinks.Get(current.Parent!).resolvedSymbol = next;
                    currentNamespace = next;
                }
                links.resolvedType = ResolveImportSymbolType(node, currentNamespace!, targetMeaning);
            }
            else
            {
                if ((GetSymbolFlags(moduleSymbol!) & targetMeaning) != 0)
                {
                    links.resolvedType = ResolveImportSymbolType(node, moduleSymbol!, targetMeaning);
                }
                else
                {
                    var message = targetMeaning == SymbolFlags.Value
                        ? Diag.Module_0_does_not_refer_to_a_value_but_is_used_as_a_value_here
                        : Diag.Module_0_does_not_refer_to_a_type_but_is_used_as_a_type_here_Did_you_mean_typeof_import_0;
                    Error(node, message, n.Argument!.AsLiteralTypeNode().Literal!.Text());
                    symbolNodeLinks.Get(node).resolvedSymbol = unknownSymbol;
                    links.resolvedType = errorType!;
                }
            }
        }
        return links.resolvedType;
    }

    // checker.go:24558-24563
    internal Node[] GetIdentifierChain(Node node)
    {
        if (NodeTests.IsIdentifier(node))
        {
            return [node];
        }
        return [.. GetIdentifierChain(node.AsQualifiedName().Left!), node.AsQualifiedName().Right!];
    }

    // checker.go:24565-24574
    internal Type ResolveImportSymbolType(Node node, Symbol symbol, SymbolFlags meaning)
    {
        var resolvedSymbol = ResolveSymbol(symbol);
        symbolNodeLinks.Get(node).resolvedSymbol = resolvedSymbol;
        if (meaning == SymbolFlags.Value)
        {
            // intentionally doesn't use resolved symbol so type is cached as expected on the alias
            return GetInstantiationExpressionType(GetTypeOfSymbol(symbol), node);
        }
        // getTypeReferenceType doesn't handle aliases - it must get the resolved symbol
        return GetTypeReferenceType(node, resolvedSymbol!);
    }

    // checker.go:24576-24581
    internal Type CreateTypeFromGenericGlobalType(Type genericGlobalType, Type[]? typeArguments)
    {
        if (genericGlobalType != emptyGenericType)
        {
            return CreateTypeReference(genericGlobalType, typeArguments);
        }
        return emptyObjectType!;
    }

    // checker.go:24583-24588
    internal Type GetGlobalStrictFunctionType(string name)
    {
        if (strictBindCallApply)
        {
            return GetGlobalType(name, 0 /*arity*/, true /*reportErrors*/);
        }
        return globalFunctionType!;
    }

    // checker.go:24590-24603
    internal Type GetGlobalImportMetaExpressionType()
    {
        if (deferredGlobalImportMetaExpressionType is null)
        {
            // Create a synthetic type `ImportMetaExpression { meta: MetaProperty }`
            var symbol = NewSymbol(SymbolFlags.None, "ImportMetaExpression");
            var importMetaType = getGlobalImportMetaType();
            var metaPropertySymbol = NewSymbolEx(SymbolFlags.Property, "meta", CheckFlags.Readonly);
            metaPropertySymbol.Parent = symbol;
            valueSymbolLinks.Get(metaPropertySymbol).resolvedType = importMetaType;
            var members = CreateSymbolTable([metaPropertySymbol]);
            symbol.Members = members;
            deferredGlobalImportMetaExpressionType = NewAnonymousType(symbol, members, null, null, null);
        }
        return deferredGlobalImportMetaExpressionType;
    }

    // checker.go:24605-24607
    internal Type CreateIterableType(Type iteratedType) =>
        CreateTypeFromGenericGlobalType(getGlobalIterableTypeChecked(), [iteratedType, voidType!, undefinedType!]);

    // checker.go:24609-24611
    internal Type CreateArrayType(Type elementType) => CreateArrayTypeEx(elementType, false /*readonly*/);

    // checker.go:24613-24615
    internal Type CreateArrayTypeEx(Type elementType, bool readonlyElements) =>
        CreateTypeFromGenericGlobalType(readonlyElements ? globalReadonlyArrayType! : globalArrayType!, [elementType]);

    // checker.go:24617-24634
    internal ElementFlags GetTupleElementFlags(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.OptionalType:
                return ElementFlags.Optional;
            case SyntaxKind.RestType:
                return GetArrayElementTypeNode(node.Type()!) is not null ? ElementFlags.Rest : ElementFlags.Variadic;
            case SyntaxKind.NamedTupleMember:
                var named = node.AsNamedTupleMember();
                if (named.QuestionToken is not null)
                {
                    return ElementFlags.Optional;
                }
                if (named.DotDotDotToken is not null)
                {
                    return GetArrayElementTypeNode(named.Type!) is not null ? ElementFlags.Rest : ElementFlags.Variadic;
                }
                return ElementFlags.Required;
        }
        return ElementFlags.Required;
    }

    // checker.go:24636-24641
    internal TupleElementInfo GetTupleElementInfo(Node node) =>
        new(GetTupleElementFlags(node), NodeTests.IsNamedTupleMember(node) || NodeTests.IsParameterDeclaration(node) ? node : null);

    // checker.go:24643-24646
    internal Type CreateTupleType(Type[] elementTypes)
    {
        var elementInfos = CoreUtil.Map(elementTypes, _ => new TupleElementInfo(ElementFlags.Required, null)) ?? [];
        return CreateTupleTypeEx(elementTypes, elementInfos, false /*readonly*/);
    }

    // checker.go:24648-24657
    internal Type CreateTupleTypeEx(Type[] elementTypes, TupleElementInfo[] elementInfos, bool readonlyElements)
    {
        var tupleTarget = GetTupleTargetType(elementInfos, readonlyElements);
        if (tupleTarget == emptyGenericType)
        {
            return emptyObjectType!;
        }
        if (elementTypes.Length != 0)
        {
            return CreateNormalizedTypeReference(tupleTarget, elementTypes);
        }
        return tupleTarget;
    }

    // checker.go:24659-24674
    internal Type GetTupleTargetType(TupleElementInfo[] elementInfos, bool readonlyElements)
    {
        if (elementInfos.Length == 1 && (elementInfos[0].flags & ElementFlags.Rest) != 0)
        {
            // [...X[]] is equivalent to just X[]
            if (readonlyElements)
            {
                return globalReadonlyArrayType!;
            }
            return globalArrayType!;
        }
        var key = GetTupleKey(elementInfos, readonlyElements);
        var t = tupleTypes.GetValueOrDefault(key);
        if (t is null)
        {
            t = CreateTupleTargetType(elementInfos, readonlyElements);
            tupleTypes[key] = t;
        }
        return t;
    }

    // We represent tuple types as type references to synthesized generic interface types created by
    // this function. See checker.go for the interface shape.
    // checker.go:24683-24736
    internal Type CreateTupleTargetType(TupleElementInfo[] elementInfos, bool readonlyElements)
    {
        var arity = elementInfos.Length;
        var minLength = CoreUtil.CountWhere(elementInfos, e => (e.flags & (ElementFlags.Required | ElementFlags.Variadic)) != 0);
        Type[]? typeParameters = null;
        var members = new SymbolTable();
        var combinedFlags = ElementFlags.None;
        if (arity != 0)
        {
            var tps = new List<Type>(arity);
            for (int i = 0; i < arity; i++)
            {
                var typeParameter = NewTypeParameter(null);
                tps.Add(typeParameter);
                var flags = elementInfos[i].flags;
                combinedFlags |= flags;
                if ((combinedFlags & ElementFlags.Variable) == 0)
                {
                    var property = NewSymbolEx(SymbolFlags.Property | ((flags & ElementFlags.Optional) != 0 ? SymbolFlags.Optional : 0), i.ToString(), readonlyElements ? CheckFlags.Readonly : 0);
                    valueSymbolLinks.Get(property).resolvedType = typeParameter;
                    // valueSymbolLinks.get(property).tupleLabelDeclaration = elementInfos[i].labeledDeclaration
                    members[property.Name] = property;
                }
            }
            typeParameters = [.. tps];
        }
        var fixedLength = members.Count;
        var lengthSymbol = NewSymbolEx(SymbolFlags.Property, "length", readonlyElements ? CheckFlags.Readonly : 0);
        if ((combinedFlags & ElementFlags.Variable) != 0)
        {
            valueSymbolLinks.Get(lengthSymbol).resolvedType = numberType;
        }
        else
        {
            Type[]? literalTypes = null;
            for (int i = minLength; i <= arity; i++)
            {
                literalTypes = [.. literalTypes ?? [], GetNumberLiteralType(new Ts.JsNum.Number(i))];
            }
            valueSymbolLinks.Get(lengthSymbol).resolvedType = GetUnionType(literalTypes ?? []);
        }
        members[lengthSymbol.Name] = lengthSymbol;
        var t = NewObjectType(ObjectFlags.Tuple | ObjectFlags.Reference, null);
        var d = t.AsTupleType();
        d.thisType = NewTypeParameter(null);
        d.thisType.AsTypeParameter().isThisType = true;
        d.thisType.AsTypeParameter().constraint = t;
        d.allTypeParameters = [.. typeParameters ?? [], d.thisType];
        d.instantiations = new Dictionary<CacheHashKey, Type>();
        d.instantiations[GetTypeListKey(d.TypeParameters())] = t;
        d.target = t;
        d.resolvedTypeArguments = d.TypeParameters();
        d.declaredMembersResolved = true;
        d.declaredMembers = members;
        d.elementInfos = elementInfos;
        d.minLength = minLength;
        d.fixedLength = fixedLength;
        d.combinedFlags = combinedFlags;
        d.readonlyElements = readonlyElements;
        return t;
    }

    // checker.go:24738-24757
    internal Type? GetElementTypeOfSliceOfTupleType(Type t, int index, int endSkipCount, bool writing, bool noReductions)
    {
        var length = GetTypeReferenceArity(t) - endSkipCount;
        var elementInfos = t.TargetTupleType().elementInfos ?? [];
        if (index < length)
        {
            var typeArguments = GetTypeArguments(t);
            Type[]? elementTypes = null;
            for (int i = index; i < length; i++)
            {
                var e = typeArguments[i];
                if ((elementInfos[i].flags & ElementFlags.Variadic) != 0)
                {
                    e = GetIndexedAccessType(e, numberType!);
                }
                elementTypes = [.. elementTypes ?? [], e];
            }
            if (writing)
            {
                return GetIntersectionType(elementTypes ?? []);
            }
            return GetUnionTypeEx(elementTypes, noReductions ? UnionReduction.None : UnionReduction.Literal, null, null);
        }
        return null;
    }

    // checker.go:24759-24761
    internal Type? GetRestTypeOfTupleType(Type t) =>
        GetElementTypeOfSliceOfTupleType(t, t.TargetTupleType().fixedLength, 0, false, false);

    // checker.go:24763-24774
    internal Type GetTupleElementTypeOutOfStartCount(Type t, Ts.JsNum.Number index, Type? undefinedLikeType) =>
        MapType(t, t2 =>
        {
            var restType = GetRestTypeOfTupleType(t2);
            if (restType is null)
            {
                return undefinedType!;
            }
            if (undefinedLikeType is not null && index.Value >= GetTotalFixedElementCount(t2.TargetTupleType()))
            {
                return GetUnionType([restType, undefinedLikeType]);
            }
            return restType;
        });

    // checker.go:24776-24778
    internal bool IsGenericType(Type t) => GetGenericObjectFlags(t) != 0;

    // checker.go:24780-24782
    internal bool IsGenericObjectType(Type t) => (GetGenericObjectFlags(t) & ObjectFlags.IsGenericObjectType) != 0;

    // checker.go:24784-24786
    internal bool IsGenericIndexType(Type t) => (GetGenericObjectFlags(t) & ObjectFlags.IsGenericIndexType) != 0;

    // checker.go:24788-24810
    internal ObjectFlags GetGenericObjectFlags(Type t)
    {
        var combinedFlags = ObjectFlags.None;
        if ((t.flags & (TypeFlags.UnionOrIntersection | TypeFlags.Substitution)) != 0)
        {
            if ((t.objectFlags & ObjectFlags.IsGenericTypeComputed) == 0)
            {
                if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
                {
                    foreach (var u in t.Types())
                    {
                        combinedFlags |= GetGenericObjectFlags(u);
                    }
                }
                else
                {
                    combinedFlags = GetGenericObjectFlags(t.AsSubstitutionType().baseType) | GetGenericObjectFlags(t.AsSubstitutionType().constraint);
                }
                t.objectFlags |= ObjectFlags.IsGenericTypeComputed | combinedFlags;
            }
            return t.objectFlags & ObjectFlags.IsGenericType;
        }
        if ((t.flags & TypeFlags.InstantiableNonPrimitive) != 0 || IsGenericMappedType(t) || IsGenericTupleType(t))
        {
            combinedFlags |= ObjectFlags.IsGenericObjectType;
        }
        if ((t.flags & (TypeFlags.InstantiableNonPrimitive | TypeFlags.Index)) != 0 || IsGenericStringLikeType(t))
        {
            combinedFlags |= ObjectFlags.IsGenericIndexType;
        }
        return combinedFlags;
    }

    // checker.go:24816-24831
    internal bool IsGenericMappedType(Type t)
    {
        if ((t.objectFlags & ObjectFlags.Mapped) != 0)
        {
            var constraint = GetConstraintTypeFromMappedType(t);
            if (IsGenericIndexType(constraint))
            {
                return true;
            }
            // A mapped type is generic if the 'as' clause references generic types other than the iteration type.
            var nameType = GetNameTypeFromMappedType(t);
            if (nameType is not null && IsGenericIndexType(InstantiateType(nameType, TypeMapper.NewSimpleTypeMapper(GetTypeParameterFromMappedType(t), constraint))))
            {
                return true;
            }
        }
        return false;
    }

    // A union type which is reducible upon instantiation must be kept generic; see checker.go comment.
    // checker.go:24840-24843
    internal bool IsGenericReducibleType(Type t) =>
        (t.flags & TypeFlags.Union) != 0 && (t.objectFlags & ObjectFlags.ContainsIntersections) != 0 && CoreUtil.Some(t.Types(), IsGenericReducibleType) ||
        (t.flags & TypeFlags.Intersection) != 0 && IsReducibleIntersection(t);

    // checker.go:24845-24851
    internal bool IsReducibleIntersection(Type t)
    {
        var d = t.AsIntersectionType();
        d.uniqueLiteralFilledInstantiation ??= InstantiateType(t, uniqueLiteralMapper);
        return GetReducedType(d.uniqueLiteralFilledInstantiation) != d.uniqueLiteralFilledInstantiation;
    }

    // checker.go:24853-24858
    internal Type GetUniqueLiteralTypeForTypeParameter(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            return uniqueLiteralType!;
        }
        return t;
    }

    // checker.go:24860-24895
    internal Type GetConditionalFlowTypeOfType(Type t, Node? node)
    {
        Type[]? constraints = null;
        var covariant = true;
        while (node is not null && !AstUtilities.IsStatement(node) && node.Kind != SyntaxKind.JSDoc)
        {
            var parent = node.Parent!;
            // only consider variance flipped by parameter locations
            if (NodeTests.IsParameterDeclaration(parent))
            {
                covariant = !covariant;
            }
            // Always substitute on type parameters, regardless of variance, since even
            // in contravariant positions, they may rely on substituted constraints to be valid
            if ((covariant || (t.flags & TypeFlags.TypeVariable) != 0) && NodeTests.IsConditionalTypeNode(parent) && node == parent.AsConditionalTypeNode().TrueType)
            {
                var constraint = GetImpliedConstraint(t, parent.AsConditionalTypeNode().CheckType!, parent.AsConditionalTypeNode().ExtendsType!);
                if (constraint is not null)
                {
                    constraints = [.. constraints ?? [], constraint];
                }
            }
            else if ((t.flags & TypeFlags.TypeParameter) != 0 && NodeTests.IsMappedTypeNode(parent) && parent.AsMappedTypeNode().NameType is null && node == parent.Type())
            {
                var mappedType = GetTypeFromTypeNode(parent);
                if (GetTypeParameterFromMappedType(mappedType) == GetActualTypeVariable(t))
                {
                    var typeParameter = GetHomomorphicTypeVariable(mappedType);
                    if (typeParameter is not null)
                    {
                        var constraint = GetConstraintOfTypeParameter(typeParameter);
                        if (constraint is not null && EveryType(constraint, IsArrayOrTupleType))
                        {
                            constraints = [.. constraints ?? [], GetUnionType([numberType!, numericStringType!])];
                        }
                    }
                }
            }
            node = parent;
        }
        if ((constraints?.Length ?? 0) != 0)
        {
            return GetSubstitutionType(t, GetIntersectionType(constraints!));
        }
        return t;
    }

    // checker.go:24897-24905
    internal Type? GetImpliedConstraint(Type t, Node checkNode, Node extendsNode)
    {
        if (IsUnaryTupleTypeNode(checkNode) && IsUnaryTupleTypeNode(extendsNode))
        {
            return GetImpliedConstraint(t, checkNode.Elements()![0], extendsNode.Elements()![0]);
        }
        if (GetActualTypeVariable(GetTypeFromTypeNode(checkNode)) == GetActualTypeVariable(t))
        {
            return GetTypeFromTypeNode(extendsNode);
        }
        return null;
    }

    // checker.go:24907-24909
    internal static bool IsUnaryTupleTypeNode(Node node) =>
        NodeTests.IsTupleTypeNode(node) && (node.Elements()?.Length ?? 0) == 1;

    // Return count of starting consecutive tuple elements of the given kind(s)
    // checker.go:23353-23360
    internal static int GetStartElementCount(TupleType t, ElementFlags flags)
    {
        var infos = t.elementInfos ?? [];
        for (int i = 0; i < infos.Length; i++)
        {
            if ((infos[i].flags & flags) == 0)
            {
                return i;
            }
        }
        return infos.Length;
    }

    // Return count of ending consecutive tuple elements of the given kind(s)
    // checker.go:23363-23370
    internal static int GetEndElementCount(TupleType t, ElementFlags flags)
    {
        var infos = t.elementInfos ?? [];
        for (int i = infos.Length; i > 0; i--)
        {
            if ((infos[i - 1].flags & flags) == 0)
            {
                return infos.Length - i;
            }
        }
        return infos.Length;
    }

    // checker.go:23372-23374
    internal static int GetTotalFixedElementCount(TupleType t) => t.fixedLength + GetEndElementCount(t, ElementFlags.Fixed);
}

// checker.go:23263-23350 — TupleNormalizer (translated as a mutable helper class).
internal sealed class TupleNormalizer
{
    internal Checker c = null!;
    internal List<Type> types = [];
    internal List<TupleElementInfo> infos = [];
    private int lastRequiredIndex;
    private int firstRestIndex;
    private int lastOptionalOrRestIndex;

    // checker.go:23272-23336
    internal bool Normalize(Checker c, Type[] elementTypes, TupleElementInfo[] elementInfos)
    {
        this.c = c;
        lastRequiredIndex = -1;
        firstRestIndex = -1;
        lastOptionalOrRestIndex = -1;
        for (int i = 0; i < elementTypes.Length; i++)
        {
            var t = elementTypes[i];
            var info = elementInfos[i];
            if ((info.flags & ElementFlags.Variadic) != 0)
            {
                if ((t.flags & TypeFlags.Any) != 0)
                {
                    Add(t, new TupleElementInfo(ElementFlags.Rest, info.labeledDeclaration));
                }
                else if ((t.flags & TypeFlags.InstantiableNonPrimitive) != 0 || c.IsGenericMappedType(t))
                {
                    // Generic variadic elements stay as they are.
                    Add(t, info);
                }
                else if (Checker.IsTupleType(t))
                {
                    var spreadTypes = c.GetElementTypes(t);
                    if (spreadTypes.Length + types.Count >= 10_000)
                    {
                        var message = Checker.IsPartOfTypeNode(c.currentNode!)
                            ? global::Ts.Diagnostics.Diagnostics.Type_produces_a_tuple_type_that_is_too_large_to_represent
                            : global::Ts.Diagnostics.Diagnostics.Expression_produces_a_tuple_type_that_is_too_large_to_represent;
                        c.Error(c.currentNode, message);
                        return false;
                    }
                    // Spread variadic elements with tuple types into the resulting tuple.
                    var spreadInfos = t.TargetTupleType().elementInfos ?? [];
                    for (int j = 0; j < spreadTypes.Length; j++)
                    {
                        Add(spreadTypes[j], spreadInfos[j]);
                    }
                }
                else
                {
                    // Treat everything else as an array type and create a rest element.
                    Type? s = null;
                    if (c.IsArrayLikeType(t))
                    {
                        s = c.GetIndexTypeOfType(t, c.numberType!);
                    }
                    s ??= c.errorType!;
                    Add(s, new TupleElementInfo(ElementFlags.Rest, info.labeledDeclaration));
                }
            }
            else
            {
                // Copy other element kinds with no change.
                Add(t, info);
            }
        }
        // Turn optional elements preceding the last required element into required elements
        for (int i = 0; i < lastRequiredIndex; i++)
        {
            if ((infos[i].flags & ElementFlags.Optional) != 0)
            {
                infos[i] = new TupleElementInfo(ElementFlags.Required, infos[i].labeledDeclaration);
            }
        }
        if (firstRestIndex >= 0 && firstRestIndex < lastOptionalOrRestIndex)
        {
            // Turn elements between first rest and last optional/rest into a single rest element
            Type[]? typesBetween = null;
            for (int i = firstRestIndex; i <= lastOptionalOrRestIndex; i++)
            {
                var t = types[i];
                if ((infos[i].flags & ElementFlags.Variadic) != 0)
                {
                    t = c.GetIndexedAccessType(t, c.numberType!);
                }
                typesBetween = [.. typesBetween ?? [], t];
            }
            types[firstRestIndex] = c.GetUnionType(typesBetween ?? []);
            types.RemoveRange(firstRestIndex + 1, lastOptionalOrRestIndex - firstRestIndex);
            infos.RemoveRange(firstRestIndex + 1, lastOptionalOrRestIndex - firstRestIndex);
        }
        return true;
    }

    // checker.go:23338-23350
    internal void Add(Type t, TupleElementInfo info)
    {
        if ((info.flags & ElementFlags.Required) != 0)
        {
            lastRequiredIndex = types.Count;
        }
        if ((info.flags & ElementFlags.Rest) != 0 && firstRestIndex < 0)
        {
            firstRestIndex = types.Count;
        }
        if ((info.flags & (ElementFlags.Optional | ElementFlags.Rest)) != 0)
        {
            lastOptionalOrRestIndex = types.Count;
        }
        types.Add(c.AddOptionalityEx(t, true /*isProperty*/, (info.flags & ElementFlags.Optional) != 0));
        infos.Add(info);
    }
}
