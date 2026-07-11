// Ported from tsgo internal/checker/checker.go (section: type reference / member resolution,
// lines 17580-19740). plan/03 §4.2 #12.
//
// Members: getPropertiesOfType(18751), getSignaturesOfType(18864),
// resolveStructuredTypeMembers(18967), resolveTypeReferenceMembers(19000), plus the binding-pattern
// implied-type family, widening, type-resolution stack, and index-info construction.
//
// §1.5: cross-package classes are fully qualified (global::Ts.<Pkg>...); CoreUtil / NodeTests /
// AstUtilities / InternalSymbolName used directly. Diagnostics via the Diag alias (name differs from
// any sibling namespace segment, so no outer-namespace shadowing).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:17580-17589
    internal bool IsNullOrUndefined(Node node)
    {
        var expr = AstUtilities.SkipParentheses(node);
        switch (expr.Kind)
        {
            case SyntaxKind.NullKeyword:
                return true;
            case SyntaxKind.Identifier:
                return GetResolvedSymbol(expr) == undefinedSymbol;
        }
        return false;
    }

    // checker.go:17591-17594
    internal Type CheckRightHandSideOfForOf(Node statement)
    {
        var use = CoreUtil.IfElse(statement.AsForInOrOfStatement().AwaitModifier is not null, IterationUse.ForAwaitOf, IterationUse.ForOf);
        return CheckIteratedTypeOrElementType(use, CheckNonNullExpression(statement.Expression()!), undefinedType!, statement.Expression());
    }

    // Return the inferred type for a binding element
    // checker.go:17597-17604
    internal Type? GetTypeForBindingElement(Node declaration)
    {
        var checkMode = CoreUtil.IfElse(HasDotDotDotToken(declaration), CheckMode.RestBindingElement, CheckMode.Normal);
        var parentType = GetTypeForBindingElementParent(declaration.Parent!.Parent!, checkMode);
        if (parentType is not null)
        {
            return GetBindingElementTypeFromParentType(declaration, parentType, false /*noTupleBoundsCheck*/);
        }
        return null;
    }

    // Return the type of a binding element parent. We check SymbolLinks first to see if a type has been
    // assigned by contextual typing.
    // checker.go:17608-17618
    internal Type? GetTypeForBindingElementParent(Node node, CheckMode checkMode)
    {
        if (checkMode == CheckMode.Normal)
        {
            // We can use a cached resolved type if no optionality was included in that type.
            var symbol = GetSymbolOfDeclaration(node);
            if (symbol is not null)
            {
                var resolvedType = valueSymbolLinks.Get(symbol).resolvedType;
                if (resolvedType is not null && !(strictNullChecks && IsOptionalDeclaration(node)))
                {
                    return resolvedType;
                }
            }
        }
        return GetTypeForVariableLikeDeclaration(node, false /*includeOptionality*/, checkMode);
    }

    // checker.go:17620-17703
    internal Type GetBindingElementTypeFromParentType(Node declaration, Type parentType, bool noTupleBoundsCheck)
    {
        // If an any type was inferred for parent, infer that for the binding element
        if (IsTypeAny(parentType))
        {
            return parentType;
        }
        var pattern = declaration.Parent!;
        // Relax null check on ambient destructuring parameters, since the parameters have no implementation and are just documentation
        if (strictNullChecks && (declaration.Flags & NodeFlags.Ambient) != 0 && AstUtilities.IsPartOfParameterDeclaration(declaration))
        {
            parentType = GetNonNullableType(parentType);
        }
        else if (strictNullChecks && pattern.Parent!.Initializer() is not null && !HasTypeFacts(GetTypeOfInitializer(pattern.Parent!.Initializer()!), TypeFacts.EQUndefined))
        {
            parentType = GetTypeWithFacts(parentType, TypeFacts.NEUndefined);
        }
        var accessFlags = AccessFlags.ExpressionPosition | CoreUtil.IfElse(noTupleBoundsCheck || HasDefaultValue(declaration), AccessFlags.AllowMissing, AccessFlags.None);
        Type? t;
        switch (pattern.Kind)
        {
            case SyntaxKind.ObjectBindingPattern:
                if (HasDotDotDotToken(declaration))
                {
                    parentType = GetReducedType(parentType);
                    if ((parentType.flags & TypeFlags.Unknown) != 0 || !IsValidSpreadType(parentType))
                    {
                        Error(declaration, Diag.Rest_types_may_only_be_created_from_object_types);
                        return errorType!;
                    }
                    var elements = pattern.Elements() ?? [];
                    var literalMembers = new List<Node>(elements.Length);
                    foreach (var element in elements)
                    {
                        if (!HasDotDotDotToken(element))
                        {
                            var name = element.PropertyNameOrName();
                            literalMembers.Add(name!);
                        }
                    }
                    t = GetRestType(parentType, literalMembers.ToArray(), declaration.Symbol());
                }
                else
                {
                    // Use explicitly specified property name ({ p: xxx } form), or otherwise the implied name ({ p } form)
                    var name = declaration.PropertyNameOrName();
                    var indexType = GetLiteralTypeFromPropertyName(name!);
                    var declaredType = GetIndexedAccessTypeEx(parentType, indexType, accessFlags, name, null);
                    t = GetFlowTypeOfDestructuring(declaration, declaredType);
                }
                break;
            case SyntaxKind.ArrayBindingPattern:
                // This elementType will be used if the specific property corresponding to this index is not
                // present (aka the tuple element property). This call also checks that the parentType is in
                // fact an iterable or array (depending on target language).
                var elementType = CheckIteratedTypeOrElementType(IterationUse.Destructuring | CoreUtil.IfElse(HasDotDotDotToken(declaration), (IterationUse)0, IterationUse.PossiblyOutOfBounds), parentType, undefinedType!, pattern);
                var index = Array.IndexOf(pattern.Elements()!, declaration);
                if (HasDotDotDotToken(declaration))
                {
                    // If the parent is a tuple type, the rest element has a tuple type of the
                    // remaining tuple element types. Otherwise, the rest element has an array type with same
                    // element type as the parent type.
                    var baseConstraint = MapType(parentType, t2 =>
                    {
                        if ((t2.flags & TypeFlags.InstantiableNonPrimitive) != 0)
                        {
                            return GetBaseConstraintOrType(t2);
                        }
                        return t2;
                    });
                    if (EveryType(baseConstraint, IsTupleType))
                    {
                        t = MapType(baseConstraint, t2 => SliceTupleType(t2, index, 0));
                    }
                    else
                    {
                        t = CreateArrayType(elementType);
                    }
                }
                else if (IsArrayLikeType(parentType))
                {
                    var indexType = GetNumberLiteralType(new global::Ts.JsNum.Number(index));
                    var declaredType = GetIndexedAccessTypeOrUndefined(parentType, indexType, accessFlags, declaration.Name(), null) ?? errorType!;
                    t = GetFlowTypeOfDestructuring(declaration, declaredType);
                }
                else
                {
                    t = elementType;
                }
                break;
            default:
                throw new InvalidOperationException("Unhandled case in getBindingElementTypeFromParentType");
        }
        if (declaration.Initializer() is null)
        {
            return t!;
        }
        if (WalkUpBindingElementsAndPatterns(declaration).Type() is not null)
        {
            // In strict null checking mode, if a default value of a non-undefined type is specified, remove
            // undefined from the final type.
            if (strictNullChecks && !HasTypeFacts(CheckDeclarationInitializer(declaration, CheckMode.Normal, null), TypeFacts.IsUndefined))
            {
                return GetNonUndefinedType(t!);
            }
            return t!;
        }
        return WidenTypeInferredFromInitializer(declaration, GetUnionTypeEx([GetNonUndefinedType(t!), CheckDeclarationInitializer(declaration, CheckMode.Normal, null)], UnionReduction.Subtype, null, null));
    }

    // checker.go:17705-17749
    internal Type GetRestType(Type source, Node[] properties, Symbol? symbol)
    {
        source = FilterType(source, t => (t.flags & TypeFlags.Nullable) == 0);
        if ((source.flags & TypeFlags.Never) != 0)
        {
            return emptyObjectType!;
        }
        if ((source.flags & TypeFlags.Union) != 0)
        {
            return MapType(source, t => GetRestType(t, properties, symbol));
        }
        var omitKeyType = GetUnionType(CoreUtil.Map(properties, GetLiteralTypeFromPropertyName)!);
        List<Symbol> spreadableProperties = [];
        List<Type> unspreadableToRestKeys = [];
        foreach (var prop in GetPropertiesOfType(source))
        {
            var literalTypeFromProperty = GetLiteralTypeFromProperty(prop, TypeFlags.StringOrNumberLiteralOrUnique, false);
            if (!IsTypeAssignableTo(literalTypeFromProperty, omitKeyType) && (GetDeclarationModifierFlagsFromSymbol(prop) & (ModifierFlags.Private | ModifierFlags.Protected)) == 0 && IsSpreadableProperty(prop))
            {
                spreadableProperties.Add(prop);
            }
            else
            {
                unspreadableToRestKeys.Add(literalTypeFromProperty);
            }
        }
        if (IsGenericObjectType(source) || IsGenericIndexType(omitKeyType))
        {
            if (unspreadableToRestKeys.Count != 0)
            {
                // If the type we're spreading from has properties that cannot
                // be spread into the rest type (e.g. getters, methods), ensure
                // they are explicitly omitted, as they would in the non-generic case.
                omitKeyType = GetUnionType([.. new[] { omitKeyType }, .. unspreadableToRestKeys]);
            }
            if ((omitKeyType.flags & TypeFlags.Never) != 0)
            {
                return source;
            }
            var omitTypeAlias = getGlobalOmitSymbol();
            if (omitTypeAlias is null)
            {
                return errorType!;
            }
            return GetTypeAliasInstantiation(omitTypeAlias, [source, omitKeyType], null);
        }
        var members = new SymbolTable();
        foreach (var prop in spreadableProperties)
        {
            members[prop.Name] = GetSpreadSymbol(prop, false /*readonly*/);
        }
        var result = NewAnonymousType(symbol, members, null, null, GetIndexInfosOfType(source));
        result.objectFlags |= ObjectFlags.ObjectRestType;
        return result;
    }

    // Determine the control flow type associated with a destructuring declaration or assignment.
    // checker.go:17762-17768
    internal Type GetFlowTypeOfDestructuring(Node node, Type declaredType)
    {
        var reference = GetSyntheticElementAccess(node);
        if (reference is not null)
        {
            return GetFlowTypeOfReference(reference, declaredType);
        }
        return declaredType;
    }

    // checker.go:17770-17793
    internal Node? GetSyntheticElementAccess(Node node)
    {
        var parentAccess = GetParentElementAccess(node);
        if (parentAccess is not null && GetFlowNodeOfNode(parentAccess) is not null)
        {
            if (GetDestructuringPropertyName(node, out var propName))
            {
                var literal = factory.NewStringLiteral(propName, TokenFlags.None);
                literal.Loc = node.Loc;
                var lhsExpr = parentAccess;
                if (!AstUtilities.IsLeftHandSideExpression(parentAccess))
                {
                    lhsExpr = factory.NewParenthesizedExpression(parentAccess);
                    lhsExpr.Loc = node.Loc;
                }
                var result = factory.NewElementAccessExpression(lhsExpr, null, literal, NodeFlags.None);
                result.Loc = node.Loc;
                literal.Parent = result;
                result.Parent = node;
                if (lhsExpr != parentAccess)
                {
                    lhsExpr.Parent = result;
                }
                result.FlowNodeData()!.FlowNode = GetFlowNodeOfNode(parentAccess);
                return result;
            }
        }
        return null;
    }

    // checker.go:17795-17808
    internal Node? GetParentElementAccess(Node node)
    {
        var ancestor = node.Parent!.Parent!;
        switch (ancestor.Kind)
        {
            case SyntaxKind.BindingElement:
            case SyntaxKind.PropertyAssignment:
                return GetSyntheticElementAccess(ancestor);
            case SyntaxKind.ArrayLiteralExpression:
                return GetSyntheticElementAccess(node.Parent);
            case SyntaxKind.VariableDeclaration:
                return ancestor.Initializer();
            case SyntaxKind.BinaryExpression:
                return ancestor.AsBinaryExpression().Right;
        }
        return null;
    }

    // Return the type implied by a binding pattern.
    // checker.go:17817-17831
    internal Type GetTypeFromBindingPattern(Node pattern, bool includePatternInType, bool reportErrors)
    {
        if (includePatternInType)
        {
            contextualBindingPatterns = [.. contextualBindingPatterns ?? [], pattern];
        }
        Type result;
        if (NodeTests.IsObjectBindingPattern(pattern))
        {
            result = GetTypeFromObjectBindingPattern(pattern, includePatternInType, reportErrors);
        }
        else
        {
            result = GetTypeFromArrayBindingPattern(pattern, includePatternInType, reportErrors);
        }
        if (includePatternInType)
        {
            contextualBindingPatterns = contextualBindingPatterns![..^1];
        }
        return result;
    }

    // Return the type implied by an object binding pattern
    // checker.go:17834-17867
    internal Type GetTypeFromObjectBindingPattern(Node pattern, bool includePatternInType, bool reportErrors)
    {
        var members = new SymbolTable();
        IndexInfo? stringIndexInfo = null;
        var objectFlags = ObjectFlags.ObjectLiteral | ObjectFlags.ContainsObjectOrArrayLiteral;
        foreach (var e in pattern.Elements() ?? [])
        {
            var name = e.PropertyNameOrName();
            if (HasDotDotDotToken(e))
            {
                stringIndexInfo = NewIndexInfo(stringType!, anyType!, false /*isReadonly*/, null, null);
                continue;
            }
            var exprType = GetLiteralTypeFromPropertyName(name!);
            if (!IsTypeUsableAsPropertyName(exprType))
            {
                // do not include computed properties in the implied type
                objectFlags |= ObjectFlags.ObjectLiteralPatternWithComputedProperties;
                continue;
            }
            var text = GetPropertyNameFromType(exprType);
            var flags = SymbolFlags.Property | CoreUtil.IfElse(e.Initializer() is not null, SymbolFlags.Optional, SymbolFlags.None);
            var symbol = NewSymbol(flags, text);
            valueSymbolLinks.Get(symbol).resolvedType = GetTypeFromBindingElement(e, includePatternInType, reportErrors);
            members[symbol.Name] = symbol;
        }
        IndexInfo[]? indexInfos = null;
        if (stringIndexInfo is not null)
        {
            indexInfos = [stringIndexInfo];
        }
        var result = NewAnonymousType(null, members, null, null, indexInfos);
        result.objectFlags |= objectFlags;
        if (includePatternInType)
        {
            patternForType[result] = pattern;
            result.objectFlags |= ObjectFlags.ContainsObjectOrArrayLiteral;
        }
        return result;
    }

    // Return the type implied by an array binding pattern
    // checker.go:17870-17914
    internal Type GetTypeFromArrayBindingPattern(Node pattern, bool includePatternInType, bool reportErrors)
    {
        var elements = pattern.Elements() ?? [];
        var lastElement = CoreUtil.LastOrNil(elements);
        Node? restElement = null;
        if (lastElement is not null && NodeTests.IsBindingElement(lastElement) && HasDotDotDotToken(lastElement))
        {
            restElement = lastElement;
        }
        if (elements.Length == 0 || (elements.Length == 1 && restElement is not null))
        {
            // TODO: remove ScriptTargetES2015
            if (languageVersion >= ScriptTarget.ES2015)
            {
                return CreateIterableType(anyType!);
            }
            return anyArrayType!;
        }
        var minLength = CoreUtil.FindLastIndex(elements, e => !(e == restElement || e.Name() is null || HasDefaultValue(e))) + 1;
        var elementTypes = new Type[elements.Length];
        var elementInfos = new TupleElementInfo[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            var e = elements[i];
            Type t;
            if (e.Name() is null)
            {
                t = anyType!;
            }
            else
            {
                t = GetTypeFromBindingElement(e, includePatternInType, reportErrors);
            }
            ElementFlags flags;
            if (e == restElement)
            {
                flags = ElementFlags.Rest;
            }
            else if (i >= minLength)
            {
                flags = ElementFlags.Optional;
            }
            else
            {
                flags = ElementFlags.Required;
            }
            elementTypes[i] = t;
            elementInfos[i] = new TupleElementInfo(flags, null);
        }
        var result = CreateTupleTypeEx(elementTypes, elementInfos, false);
        if (includePatternInType)
        {
            result = CloneTypeReference(result);
            patternForType[result] = pattern;
            result.objectFlags |= ObjectFlags.ContainsObjectOrArrayLiteral;
        }
        return result;
    }

    // Return the type implied by a binding pattern element.
    // checker.go:17919-17944
    internal Type GetTypeFromBindingElement(Node element, bool includePatternInType, bool reportErrors)
    {
        if (element.Initializer() is not null)
        {
            // The type implied by a binding pattern is independent of context.
            var contextualType = unknownType!;
            if (AstUtilities.IsBindingPattern(element.Name()))
            {
                contextualType = GetTypeFromBindingPattern(element.Name()!, true /*includePatternInType*/, false /*reportErrors*/);
            }
            return AddOptionality(GetWidenedLiteralTypeForInitializer(element, CheckDeclarationInitializer(element, CheckMode.Normal, contextualType)));
        }
        if (AstUtilities.IsBindingPattern(element.Name()))
        {
            return GetTypeFromBindingPattern(element.Name()!, includePatternInType, reportErrors);
        }
        if (reportErrors && !DeclarationBelongsToPrivateAmbientMember(element))
        {
            ReportImplicitAny(element, anyType!, WideningKind.Normal);
        }
        // When we're including the pattern in the type (an indication we're obtaining a contextual type), we
        // use a non-inferrable any type.
        if (includePatternInType)
        {
            return nonInferrableAnyType!;
        }
        return anyType!;
    }

    // checker.go:17946-17952
    internal bool DeclarationBelongsToPrivateAmbientMember(Node declaration)
    {
        var memberDeclaration = AstUtilities.GetRootDeclaration(declaration);
        if (NodeTests.IsParameterDeclaration(memberDeclaration))
        {
            memberDeclaration = memberDeclaration.Parent!;
        }
        return IsPrivateWithinAmbient(memberDeclaration);
    }

    // checker.go:17954-17965
    internal Type GetTypeOfPrototypeProperty(Symbol prototype)
    {
        // Every class automatically contains a static property member named 'prototype'.
        var classType = GetDeclaredTypeOfSymbol(GetParentOfSymbol(prototype)!);
        var typeParameters = classType.AsInterfaceType()!.TypeParameters();
        if (typeParameters is { Length: > 0 })
        {
            return CreateTypeReference(classType, CoreUtil.Map(typeParameters, _ => anyType!));
        }
        return classType;
    }

    // checker.go:17976-18027
    internal Type GetWidenedTypeForAssignmentDeclaration(Symbol symbol)
    {
        Type? t = null;
        var (kind, location) = IsConstructorDeclaredThisProperty(symbol);
        switch (kind)
        {
            case ThisAssignmentDeclarationKind.Typed:
                if (location is null)
                {
                    throw new InvalidOperationException("location should not be nil when this assignment has a type.");
                }
                t = GetTypeFromTypeNode(location);
                break;
            case ThisAssignmentDeclarationKind.Constructor:
                if (location is null)
                {
                    throw new InvalidOperationException("constructor should not be nil when this assignment is in a constructor.");
                }
                t = GetFlowTypeInConstructor(symbol, location);
                break;
            case ThisAssignmentDeclarationKind.Method:
                t = GetTypeOfPropertyInBaseClass(symbol);
                break;
        }
        if (t is null)
        {
            Type[]? types = null;
            var declarations = symbol.Declarations ?? [];
            for (int i = 0; i < declarations.Length; i++)
            {
                var declaration = declarations[i];
                if (NodeTests.IsBinaryExpression(declaration) && declaration.Type() is not null)
                {
                    t = GetTypeFromTypeNode(declaration.Type()!);
                    break;
                }
                var assignedType = GetAssignmentDeclarationInitializerType(declaration);
                if (assignedType is not null)
                {
                    // We ignore initial assignments of undefined to CommonJS exports when there are multiple assignment declarations
                    if (AstUtilities.GetAssignmentDeclarationKind(declaration) != JSDeclarationKind.ExportsProperty || i != 0 || declarations.Length == 1 || (assignedType.flags & TypeFlags.Undefined) == 0)
                    {
                        types = CoreUtil.AppendIfUnique(types, assignedType);
                    }
                }
            }
            if (kind == ThisAssignmentDeclarationKind.Method && (types ?? []).Length > 0)
            {
                if (strictNullChecks)
                {
                    types = CoreUtil.AppendIfUnique(types, undefinedOrMissingType!);
                }
            }
            if (t is null)
            {
                t = anyType!;
                if ((types ?? []).Length != 0)
                {
                    t = GetUnionType(types!);
                }
            }
        }
        t = GetWidenedType(t);
        // report an all-nullable or empty union as an implicit any in JS files
        if (symbol.ValueDeclaration is not null && AstUtilities.IsInJSFile(symbol.ValueDeclaration) &&
            FilterType(t, ct => (ct.Flags & ~TypeFlags.Nullable) != 0) == neverType)
        {
            ReportImplicitAny(symbol.ValueDeclaration, anyType!, WideningKind.Normal);
            return anyType!;
        }
        return t;
    }

    // checker.go:18029-18053
    internal Type? GetAssignmentDeclarationInitializerType(Node node)
    {
        if (NodeTests.IsBinaryExpression(node))
        {
            Type t;
            switch (AstUtilities.GetAssignmentDeclarationKind(node))
            {
                case JSDeclarationKind.ModuleExports:
                case JSDeclarationKind.ExportsProperty:
                    t = GetRegularTypeOfLiteralType(CheckExpressionCached(AstUtilities.GetRightMostAssignedExpression(node)));
                    break;
                case JSDeclarationKind.ThisProperty:
                    if (ContainsSameNamedThisProperty(node.AsBinaryExpression().Left!, node.AsBinaryExpression().Right!))
                    {
                        return null;
                    }
                    goto default;
                default:
                    t = CheckExpressionForMutableLocation(node.AsBinaryExpression().Right!, CheckMode.Normal);
                    break;
            }
            if (IsEmptyArrayLiteralType(t) && !HasParentWithTypeAnnotation(node.Symbol()!))
            {
                ReportImplicitAny(node, anyArrayType!, WideningKind.Normal);
                return anyArrayType!;
            }
            return t;
        }
        if (NodeTests.IsCallExpression(node))
        {
            return GetTypeFromPropertyDescriptor(node.Arguments()![2]);
        }
        return null;
    }

    // checker.go:18060-18067
    internal bool HasParentWithTypeAnnotation(Symbol symbol)
    {
        if (symbol.Parent is not null && symbol.Parent.ValueDeclaration is not null && AstUtilities.IsFunctionExpressionOrArrowFunction(symbol.Parent.ValueDeclaration))
        {
            var possiblyAnnotatedSymbol = GetSymbolOfNode(symbol.Parent.ValueDeclaration.Parent!);
            if (possiblyAnnotatedSymbol is not null && possiblyAnnotatedSymbol.ValueDeclaration is not null)
            {
                return possiblyAnnotatedSymbol.ValueDeclaration.Type() is not null;
            }
        }
        return false;
    }

    // checker.go:18069-18081
    internal bool ContainsSameNamedThisProperty(Node thisProperty, Node expression)
    {
        bool Visit(Node node)
        {
            if (IsMatchingReference(thisProperty, node))
            {
                return true;
            }
            if (AstUtilities.IsFunctionLike(node))
            {
                return false;
            }
            return node.ForEachChild(Visit);
        }
        return Visit(expression);
    }

    // checker.go:18083-18099
    internal Type GetTypeFromPropertyDescriptor(Node node)
    {
        var objectLiteralType = CheckExpressionCached(node);
        var valueType = GetTypeOfPropertyOfType(objectLiteralType, "value");
        if (valueType is not null)
        {
            return valueType;
        }
        var getFunc = GetTypeOfPropertyOfType(objectLiteralType, "get");
        if (getFunc is not null)
        {
            var getSig = GetSingleCallSignature(getFunc);
            if (getSig is not null)
            {
                return GetReturnTypeOfSignature(getSig);
            }
        }
        var setFunc = GetTypeOfPropertyOfType(objectLiteralType, "set");
        if (setFunc is not null)
        {
            var setSig = GetSingleCallSignature(setFunc);
            if (setSig is not null)
            {
                return GetTypeOfFirstParameterOfSignature(setSig);
            }
        }
        return anyType!;
    }

    // checker.go:18104-18147
    internal (ThisAssignmentDeclarationKind, Node?) IsConstructorDeclaredThisProperty(Symbol symbol)
    {
        if (symbol.ValueDeclaration is null || !NodeTests.IsBinaryExpression(symbol.ValueDeclaration))
        {
            return (ThisAssignmentDeclarationKind.None, null);
        }
        if (thisExpandoKinds.TryGetValue(symbol, out var kind))
        {
            if (!thisExpandoLocations.TryGetValue(symbol, out var loc))
            {
                throw new InvalidOperationException("location should be cached whenever this expando symbol is cached");
            }
            return (kind, loc);
        }
        var allThis = true;
        Node? typeAnnotation = null;
        foreach (var declaration in symbol.Declarations ?? [])
        {
            if (!NodeTests.IsBinaryExpression(declaration))
            {
                allThis = false;
                break;
            }
            var bin = declaration.AsBinaryExpression();
            if (AstUtilities.GetAssignmentDeclarationKind(declaration) == JSDeclarationKind.ThisProperty &&
                (bin.Left!.Kind != SyntaxKind.ElementAccessExpression || AstUtilities.IsStringOrNumericLiteralLike(bin.Left!.AsElementAccessExpression().ArgumentExpression!)))
            {
                if (bin.Type is not null)
                {
                    typeAnnotation = bin.Type;
                }
            }
            else
            {
                allThis = false;
                break;
            }
        }
        Node? location = null;
        var resultKind = ThisAssignmentDeclarationKind.None;
        if (allThis)
        {
            if (typeAnnotation is not null)
            {
                location = typeAnnotation;
                resultKind = ThisAssignmentDeclarationKind.Typed;
            }
            else
            {
                location = GetDeclaringConstructor(symbol);
                resultKind = CoreUtil.IfElse(location is null, ThisAssignmentDeclarationKind.Method, ThisAssignmentDeclarationKind.Constructor);
            }
        }
        thisExpandoKinds[symbol] = resultKind;
        thisExpandoLocations[symbol] = location!;
        return (resultKind, location);
    }

    // checker.go:18149-18153
    internal bool IsGlobalSymbolConstructor(Node node)
    {
        var symbol = GetSymbolOfNode(node);
        var globalSymbol = getGlobalESSymbolConstructorTypeSymbolOrNil();
        return globalSymbol is not null && symbol == globalSymbol;
    }

    // checker.go:18155-18186
    internal Type WidenTypeForVariableLikeDeclaration(Type? t, Node declaration, bool reportErrors)
    {
        if (t is not null)
        {
            // This special case is required for backwards compatibility with libraries that merge a `symbol` property into `SymbolConstructor`.
            if ((t.flags & TypeFlags.ESSymbol) != 0 && IsGlobalSymbolConstructor(declaration.Parent!))
            {
                t = GetESSymbolLikeTypeForNode(declaration);
            }
            if (reportErrors)
            {
                ReportErrorsFromWidening(declaration, t, WideningKind.Normal);
            }
            // always widen a 'unique symbol' type if the type was created for a different declaration.
            if ((t.flags & TypeFlags.UniqueESSymbol) != 0 && (NodeTests.IsBindingElement(declaration) || declaration.Type() is null) && t.symbol != GetSymbolOfDeclaration(declaration))
            {
                t = esSymbolType!;
            }
            return GetWidenedType(t);
        }
        // Rest parameters default to type any[], other parameters default to type any
        if (NodeTests.IsParameterDeclaration(declaration) && declaration.AsParameterDeclaration().DotDotDotToken is not null)
        {
            t = anyArrayType!;
        }
        else
        {
            t = anyType!;
        }
        // Report implicit any errors unless this is a private property within an ambient declaration
        if (reportErrors)
        {
            if (!DeclarationBelongsToPrivateAmbientMember(declaration))
            {
                ReportImplicitAny(declaration, t, WideningKind.Normal);
            }
        }
        return t;
    }

    // checker.go:18188-18266
    internal void ReportImplicitAny(Node declaration, Type t, WideningKind wideningKind)
    {
        if (AstUtilities.IsInJSFile(declaration) && !IsCheckJSEnabledForFile(AstUtilities.GetSourceFileOfNode(declaration)!, compilerOptions))
        {
            // Only report implicit any errors/suggestions in TS and ts-check JS files
            return;
        }
        var typeAsString = TypeToString(GetWidenedType(t));
        global::Ts.Diagnostics.Message? diagnostic;
        switch (declaration.Kind)
        {
            case SyntaxKind.BinaryExpression:
            case SyntaxKind.PropertyDeclaration:
            case SyntaxKind.PropertySignature:
                diagnostic = CoreUtil.IfElse(noImplicitAny,
                    Diag.Member_0_implicitly_has_an_1_type,
                    Diag.Member_0_implicitly_has_an_1_type_but_a_better_type_may_be_inferred_from_usage);
                break;
            case SyntaxKind.Parameter:
                var param = declaration.AsParameterDeclaration();
                if (NodeTests.IsIdentifier(param.Name()!))
                {
                    var name = param.Name()!.AsIdentifier();
                    var originalKeywordKind = global::Ts.Scanner.Scanner.IdentifierToKeywordKind(name);
                    if ((NodeTests.IsCallSignatureDeclaration(declaration.Parent!) || NodeTests.IsMethodSignatureDeclaration(declaration.Parent!) || NodeTests.IsFunctionTypeNode(declaration.Parent!)) &&
                        Array.IndexOf(declaration.Parent!.Parameters(), declaration) >= 0 &&
                        (AstUtilities.IsTypeNodeKind(originalKeywordKind) || resolveName(declaration, name.Text, SymbolFlags.Type, null /*nameNotFoundMessage*/, true /*isUse*/, false /*excludeGlobals*/) is not null))
                    {
                        var newName = $"arg{Array.IndexOf(declaration.Parent!.Parameters(), declaration)}";
                        var typeName = global::Ts.Scanner.Scanner.DeclarationNameToString(param.Name()) + CoreUtil.IfElse(param.DotDotDotToken is not null, "[]", "");
                        ErrorOrSuggestion(noImplicitAny, declaration, Diag.Parameter_has_a_name_but_no_type_Did_you_mean_0_Colon_1, newName, typeName);
                        return;
                    }
                }
                if (param.DotDotDotToken is not null)
                {
                    if (noImplicitAny)
                    {
                        diagnostic = Diag.Rest_parameter_0_implicitly_has_an_any_type;
                    }
                    else
                    {
                        diagnostic = Diag.Rest_parameter_0_implicitly_has_an_any_type_but_a_better_type_may_be_inferred_from_usage;
                    }
                }
                else if (noImplicitAny)
                {
                    diagnostic = Diag.Parameter_0_implicitly_has_an_1_type;
                }
                else
                {
                    diagnostic = Diag.Parameter_0_implicitly_has_an_1_type_but_a_better_type_may_be_inferred_from_usage;
                }
                break;
            case SyntaxKind.BindingElement:
                diagnostic = Diag.Binding_element_0_implicitly_has_an_1_type;
                if (!noImplicitAny)
                {
                    // Don't issue a suggestion for binding elements since the codefix doesn't yet support them.
                    return;
                }
                break;
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.MethodDeclaration:
            case SyntaxKind.MethodSignature:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.ArrowFunction:
                if (noImplicitAny && declaration.Name() is null)
                {
                    if (wideningKind == WideningKind.GeneratorYield)
                    {
                        Error(declaration, Diag.Generator_implicitly_has_yield_type_0_Consider_supplying_a_return_type_annotation, typeAsString);
                    }
                    else
                    {
                        Error(declaration, Diag.Function_expression_which_lacks_return_type_annotation_implicitly_has_an_0_return_type, typeAsString);
                    }
                    return;
                }
                if (!noImplicitAny)
                {
                    diagnostic = Diag.X_0_implicitly_has_an_1_return_type_but_a_better_type_may_be_inferred_from_usage;
                }
                else if ((declaration.Flags & NodeFlags.Reparsed) != 0)
                {
                    Error(declaration, Diag.This_overload_implicitly_returns_the_type_0_because_it_lacks_a_return_type_annotation, typeAsString);
                    return;
                }
                else if (wideningKind == WideningKind.GeneratorYield)
                {
                    diagnostic = Diag.X_0_which_lacks_return_type_annotation_implicitly_has_an_1_yield_type;
                }
                else
                {
                    diagnostic = Diag.X_0_which_lacks_return_type_annotation_implicitly_has_an_1_return_type;
                }
                break;
            case SyntaxKind.MappedType:
                if (noImplicitAny)
                {
                    Error(declaration, Diag.Mapped_object_type_implicitly_has_an_any_template_type);
                }
                return;
            default:
                if (noImplicitAny)
                {
                    diagnostic = Diag.Variable_0_implicitly_has_an_1_type;
                }
                else
                {
                    diagnostic = Diag.Variable_0_implicitly_has_an_1_type_but_a_better_type_may_be_inferred_from_usage;
                }
                break;
        }
        ErrorOrSuggestion(noImplicitAny, declaration, diagnostic, global::Ts.Scanner.Scanner.DeclarationNameToString(AstUtilities.GetNameOfDeclaration(declaration)), typeAsString);
    }

    // checker.go:18268-18270
    internal Type GetWidenedType(Type t) => GetWidenedTypeWithContext(t, null /*context*/);

    // checker.go:18272-18311
    internal Type GetWidenedTypeWithContext(Type t, WideningContext? context)
    {
        if ((t.objectFlags & ObjectFlags.RequiresWidening) != 0)
        {
            if (context is null)
            {
                var cached = cachedTypes.GetValueOrDefault(new CachedTypeKey(CachedTypeKind.Widened, t.id));
                if (cached is not null)
                {
                    return cached;
                }
            }
            Type? result = null;
            if ((t.flags & (TypeFlags.Any | TypeFlags.Nullable)) != 0)
            {
                result = anyType!;
            }
            else if (IsObjectLiteralType(t))
            {
                result = GetWidenedTypeOfObjectLiteral(t, context);
            }
            else if ((t.flags & TypeFlags.Union) != 0)
            {
                var unionContext = context;
                unionContext ??= new WideningContext { siblings = t.Types() };
                var widenedTypes = CoreUtil.SameMap(t.Types(), tt =>
                {
                    if ((tt.flags & TypeFlags.Nullable) != 0)
                    {
                        return tt;
                    }
                    return GetWidenedTypeWithContext(tt, unionContext);
                });
                // Widening an empty object literal transitions from a highly restrictive type to a highly inclusive one.
                result = GetUnionTypeEx(widenedTypes!, CoreUtil.IfElse(CoreUtil.Some(widenedTypes, IsEmptyObjectType), UnionReduction.Subtype, UnionReduction.Literal), null, null);
            }
            else if ((t.flags & TypeFlags.Intersection) != 0)
            {
                result = GetIntersectionType(CoreUtil.SameMap(t.Types(), GetWidenedType)!);
            }
            else if (IsArrayOrTupleType(t))
            {
                result = CreateTypeReference(t.Target(), CoreUtil.SameMap(GetTypeArguments(t), GetWidenedType)!);
            }
            if (result is not null && context is null)
            {
                cachedTypes[new CachedTypeKey(CachedTypeKind.Widened, t.id)] = result;
            }
            return result ?? t;
        }
        return t;
    }

    // checker.go:18313-18343
    internal Type GetWidenedTypeOfObjectLiteral(Type t, WideningContext? context)
    {
        if (context is not null)
        {
            var cachedCtx = context.widenedTypes?.GetValueOrDefault(t);
            if (cachedCtx is not null)
            {
                return cachedCtx;
            }
        }
        var members = new SymbolTable();
        foreach (var prop in GetPropertiesOfObjectType(t))
        {
            members[prop.Name] = GetWidenedProperty(prop, context);
        }
        if (context is not null)
        {
            foreach (var prop in GetPropertiesOfContext(context))
            {
                if (!members.ContainsKey(prop.Name))
                {
                    members[prop.Name] = GetUndefinedProperty(prop);
                }
            }
        }
        var result = NewAnonymousType(t.symbol, members, null, null, CoreUtil.SameMap(GetIndexInfosOfType(t), info =>
            NewIndexInfo(info.keyType, GetWidenedType(info.valueType), info.isReadonly, info.declaration, info.components)));
        // Retain js literal flag through widening
        result.objectFlags |= t.objectFlags & (ObjectFlags.JSLiteral | ObjectFlags.NonInferrableType);
        // Only cache in child contexts since the root context never widens a particular object literal type more than once
        if (context is not null && context.parent is not null)
        {
            context.widenedTypes ??= new Dictionary<Type, Type>(ReferenceEqualityComparer.Instance);
            context.widenedTypes[t] = result;
        }
        return result;
    }

    // checker.go:18345-18361
    internal Symbol GetWidenedProperty(Symbol prop, WideningContext? context)
    {
        if ((prop.Flags & SymbolFlags.Property) == 0)
        {
            // Since get accessors already widen their return value there is no need to widen accessor based properties here.
            return prop;
        }
        var original = GetTypeOfSymbol(prop);
        WideningContext? propContext = null;
        if (context is not null)
        {
            propContext = GetChildContext(context, prop.Name);
        }
        var widened = GetWidenedTypeWithContext(original, propContext);
        if (widened == original)
        {
            return prop;
        }
        return CreateSymbolWithType(prop, widened);
    }

    // checker.go:18363-18373
    internal WideningContext GetChildContext(WideningContext w, string propertyName)
    {
        var cached = w.childContexts?.GetValueOrDefault(propertyName);
        if (cached is not null)
        {
            return cached;
        }
        var result = new WideningContext { parent = w, propertyName = propertyName };
        w.childContexts ??= [];
        w.childContexts[propertyName] = result;
        return result;
    }

    // checker.go:18375-18388
    internal Symbol[] GetPropertiesOfContext(WideningContext context)
    {
        if (context.resolvedProperties is null)
        {
            var names = new OrderedMap<string, Symbol>();
            foreach (var t in GetSiblingsOfContext(context))
            {
                if (IsObjectLiteralType(t) && (t.objectFlags & ObjectFlags.ContainsSpread) == 0)
                {
                    foreach (var prop in GetPropertiesOfType(t))
                    {
                        names.Set(prop.Name, prop);
                    }
                }
            }
            context.resolvedProperties = names.Values().ToArray();
        }
        return context.resolvedProperties;
    }

    // checker.go:18390-18404
    internal Type[] GetSiblingsOfContext(WideningContext context)
    {
        if (context.siblings is null)
        {
            List<Type> siblings = [];
            foreach (var t in GetSiblingsOfContext(context.parent!))
            {
                if (IsObjectLiteralType(t))
                {
                    var prop = GetPropertyOfObjectType(t, context.propertyName);
                    if (prop is not null)
                    {
                        siblings.AddRange(GetTypeOfSymbol(prop).Distributed());
                    }
                }
            }
            context.siblings = siblings.ToArray();
        }
        return context.siblings;
    }

    // checker.go:18406-18414
    internal Symbol GetUndefinedProperty(Symbol prop)
    {
        var cached = undefinedProperties.GetValueOrDefault(prop.Name);
        if (cached is not null)
        {
            return cached;
        }
        var result = CreateSymbolWithType(prop, undefinedOrMissingType!);
        result.Flags |= SymbolFlags.Optional;
        undefinedProperties[prop.Name] = result;
        return result;
    }

    // checker.go:18416-18422
    internal Type GetTypeOfEnumMember(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        links.resolvedType ??= GetDeclaredTypeOfEnumMember(symbol);
        return links.resolvedType;
    }

    // checker.go:18424-18477
    internal Type GetTypeOfAccessors(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType is null)
        {
            if (!PushTypeResolution(symbol, TypeSystemPropertyName.Type))
            {
                return errorType!;
            }
            var getter = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.GetAccessor);
            var setter = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.SetAccessor);
            var accessor = CoreUtil.Find(symbol.Declarations, AstUtilities.IsAutoAccessorPropertyDeclaration);
            // We try to resolve a getter type annotation, a setter type annotation, or a getter function body return type inference, in that order.
            var t = GetAnnotatedAccessorType(getter);
            t ??= GetAnnotatedAccessorType(setter);
            t ??= GetAnnotatedAccessorType(accessor);
            if (t is null && getter is not null)
            {
                if (getter.Body() is not null)
                {
                    t = GetReturnTypeFromBody(getter, CheckMode.Normal);
                }
            }
            if (t is null && accessor is not null)
            {
                t = GetWidenedTypeForVariableLikeDeclaration(accessor, true /*reportErrors*/);
            }
            if (t is null)
            {
                if (setter is not null && !IsPrivateWithinAmbient(setter))
                {
                    ErrorOrSuggestion(noImplicitAny, setter, Diag.Property_0_implicitly_has_type_any_because_its_set_accessor_lacks_a_parameter_type_annotation, SymbolToString(symbol));
                }
                else if (getter is not null && !IsPrivateWithinAmbient(getter))
                {
                    ErrorOrSuggestion(noImplicitAny, getter, Diag.Property_0_implicitly_has_type_any_because_its_get_accessor_lacks_a_return_type_annotation, SymbolToString(symbol));
                }
                else if (accessor is not null && !IsPrivateWithinAmbient(accessor))
                {
                    ErrorOrSuggestion(noImplicitAny, accessor, Diag.Member_0_implicitly_has_an_1_type, SymbolToString(symbol), "any");
                }
                t = anyType!;
            }
            if (!PopTypeResolution())
            {
                if (GetAnnotatedAccessorTypeNode(getter) is not null)
                {
                    Error(getter, Diag.X_0_is_referenced_directly_or_indirectly_in_its_own_type_annotation, SymbolToString(symbol));
                }
                else if (GetAnnotatedAccessorTypeNode(setter) is not null)
                {
                    Error(setter, Diag.X_0_is_referenced_directly_or_indirectly_in_its_own_type_annotation, SymbolToString(symbol));
                }
                else if (GetAnnotatedAccessorTypeNode(accessor) is not null)
                {
                    Error(setter, Diag.X_0_is_referenced_directly_or_indirectly_in_its_own_type_annotation, SymbolToString(symbol));
                }
                else if (getter is not null && noImplicitAny)
                {
                    Error(getter, Diag.X_0_implicitly_has_return_type_any_because_it_does_not_have_a_return_type_annotation_and_is_referenced_directly_or_indirectly_in_one_of_its_return_expressions, SymbolToString(symbol));
                }
                t = anyType!;
            }
            links.resolvedType ??= t;
        }
        return links.resolvedType;
    }

    // checker.go:18479-18509
    internal Type GetWriteTypeOfAccessors(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.writeType is null)
        {
            if (!PushTypeResolution(symbol, TypeSystemPropertyName.WriteType))
            {
                return errorType!;
            }
            var setter = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.SetAccessor);
            if (setter is null)
            {
                var propDeclaration = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.PropertyDeclaration);
                if (propDeclaration is not null && AstUtilities.IsAutoAccessorPropertyDeclaration(propDeclaration))
                {
                    setter = propDeclaration;
                }
            }
            var writeType = GetAnnotatedAccessorType(setter);
            if (!PopTypeResolution())
            {
                if (GetAnnotatedAccessorTypeNode(setter) is not null)
                {
                    Error(setter, Diag.X_0_is_referenced_directly_or_indirectly_in_its_own_type_annotation, SymbolToString(symbol));
                }
                writeType = anyType!;
            }
            // Absent an explicit setter type annotation we use the read type of the accessor.
            if (links.writeType is null)
            {
                if (writeType is not null)
                {
                    links.writeType = writeType;
                }
                else
                {
                    links.writeType = GetTypeOfAccessors(symbol);
                }
            }
        }
        return links.writeType;
    }

    // checker.go:18511-18540
    internal Type GetTypeOfAlias(Symbol symbol)
    {
        var links = valueSymbolLinks.Get(symbol);
        if (links.resolvedType is null)
        {
            if (!PushTypeResolution(symbol, TypeSystemPropertyName.Type))
            {
                return errorType!;
            }
            var (targetSymbol, _) = ResolveAlias(symbol);
            var exportSymbol = GetTargetOfAliasDeclaration(GetDeclarationOfAliasSymbol(symbol));
            // It only makes sense to get the type of a value symbol.
            if (links.resolvedType is null)
            {
                if ((GetSymbolFlags(targetSymbol!) & SymbolFlags.Value) != 0)
                {
                    links.resolvedType = GetTypeOfSymbol(targetSymbol!);
                }
                else
                {
                    links.resolvedType = errorType!;
                }
            }
            if (!PopTypeResolution())
            {
                ReportCircularityError(exportSymbol ?? symbol);
                links.resolvedType ??= errorType!;
                return links.resolvedType;
            }
        }
        return links.resolvedType;
    }

    // checker.go:18542-18543
    internal Type AddOptionality(Type t) => AddOptionalityEx(t, false /*isProperty*/, true /*isOptional*/);

    // checker.go:18546-18551
    internal Type AddOptionalityEx(Type t, bool isProperty, bool isOptional)
    {
        if (strictNullChecks && isOptional)
        {
            return GetOptionalType(t, isProperty);
        }
        return t;
    }

    // checker.go:18553-18560
    internal Type GetOptionalType(Type t, bool isProperty)
    {
        global::Ts.Core.Debug.Assert(strictNullChecks);
        var missingOrUndefined = CoreUtil.IfElse(isProperty, undefinedOrMissingType!, undefinedType!);
        if (t == missingOrUndefined || ((t.flags & TypeFlags.Union) != 0 && t.Types()[0] == missingOrUndefined))
        {
            return t;
        }
        return GetUnionType([t, missingOrUndefined]);
    }

    // Add undefined or null or both to a type if they are missing.
    // checker.go:18563-18574
    internal Type GetNullableType(Type t, TypeFlags flags)
    {
        var missing = (flags & ~t.flags) & (TypeFlags.Undefined | TypeFlags.Null);
        if (missing == 0)
        {
            return t;
        }
        if (missing == TypeFlags.Undefined)
        {
            return GetUnionType([t, undefinedType!]);
        }
        if (missing == TypeFlags.Null)
        {
            return GetUnionType([t, nullType!]);
        }
        return GetUnionType([t, undefinedType!, nullType!]);
    }

    // checker.go:18576-18581
    internal Type GetNonNullableType(Type t)
    {
        if (strictNullChecks)
        {
            return GetAdjustedTypeWithFacts(t, TypeFacts.NEUndefinedOrNull);
        }
        return t;
    }

    // checker.go:18583-18585
    internal bool IsNullableType(Type t) => HasTypeFacts(t, TypeFacts.IsUndefinedOrNull);

    // checker.go:18587-18592
    internal Type GetNonNullableTypeIfNeeded(Type t)
    {
        if (IsNullableType(t))
        {
            return GetNonNullableType(t);
        }
        return t;
    }

    // checker.go:18594-18599
    internal NodeFlags GetDeclarationNodeFlagsFromSymbol(Symbol s)
    {
        if (s.ValueDeclaration is not null)
        {
            return GetCombinedNodeFlagsCached(s.ValueDeclaration);
        }
        return NodeFlags.None;
    }

    // checker.go:18601-18609
    internal NodeFlags GetCombinedNodeFlagsCached(Node node)
    {
        // we hold onto the last node and result to speed up repeated lookups against the same node.
        if (lastGetCombinedNodeFlagsNode == node)
        {
            return lastGetCombinedNodeFlagsResult;
        }
        lastGetCombinedNodeFlagsNode = node;
        lastGetCombinedNodeFlagsResult = AstUtilities.GetCombinedNodeFlags(node);
        return lastGetCombinedNodeFlagsResult;
    }

    // checker.go:18611-18614
    internal bool IsVarConstLike(Node node)
    {
        var blockScopeKind = GetCombinedNodeFlagsCached(node) & NodeFlags.BlockScoped;
        return blockScopeKind == NodeFlags.Const || blockScopeKind == NodeFlags.Using || blockScopeKind == NodeFlags.AwaitUsing;
    }

    // checker.go:18616-18625
    internal (string, bool) GetEffectivePropertyNameForPropertyNameNode(Node node)
    {
        var name = GetPropertyNameForPropertyNameNode(node);
        if (name != InternalSymbolName.Missing)
        {
            return (name, true);
        }
        if (NodeTests.IsComputedPropertyName(node))
        {
            return TryGetNameFromType(GetTypeOfExpression(node.Expression()!));
        }
        return ("", false);
    }

    // checker.go:18627-18640
    internal (string name, bool ok) TryGetNameFromType(Type t)
    {
        if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
        {
            return (t.AsUniqueESSymbolType().name, true);
        }
        if ((t.flags & TypeFlags.StringLiteral) != 0)
        {
            return (GetStringLiteralValue(t), true);
        }
        if ((t.flags & TypeFlags.NumberLiteral) != 0)
        {
            return (GetNumberLiteralValue(t).ToString(), true);
        }
        return ("", false);
    }

    // checker.go:18642-18650
    internal ModifierFlags GetCombinedModifierFlagsCached(Node node)
    {
        if (lastGetCombinedModifierFlagsNode == node)
        {
            return lastGetCombinedModifierFlagsResult;
        }
        lastGetCombinedModifierFlagsNode = node;
        lastGetCombinedModifierFlagsResult = AstUtilities.GetCombinedModifierFlags(node);
        return lastGetCombinedModifierFlagsResult;
    }

    // Push an entry on the type resolution stack.
    // checker.go:18663-18674
    internal bool PushTypeResolution(object target, TypeSystemPropertyName propertyName)
    {
        var resolutionCycleStartIndex = FindResolutionCycleStartIndex(target, propertyName);
        if (resolutionCycleStartIndex >= 0)
        {
            // A cycle was found
            for (int i = resolutionCycleStartIndex; i < typeResolutions.Count; i++)
            {
                var r = typeResolutions[i];
                r.result = false;
                typeResolutions[i] = r;
            }
            return false;
        }
        typeResolutions.Add(new TypeResolution { target = target, propertyName = propertyName, result = true });
        return true;
    }

    // Pop an entry from the type resolution stack.
    // checker.go:18680-18686
    internal bool PopTypeResolution()
    {
        var lastIndex = typeResolutions.Count - 1;
        var result = typeResolutions[lastIndex].result;
        typeResolutions[lastIndex] = default; // Clear the last entry to avoid memory leaks
        typeResolutions.RemoveAt(lastIndex);
        return result;
    }

    // checker.go:18688-18699
    internal int FindResolutionCycleStartIndex(object target, TypeSystemPropertyName propertyName)
    {
        for (int i = typeResolutions.Count - 1; i >= resolutionStart; i--)
        {
            var resolution = typeResolutions[i];
            if (TypeResolutionHasProperty(resolution))
            {
                return -1;
            }
            if (resolution.target == target && resolution.propertyName == propertyName)
            {
                return i;
            }
        }
        return -1;
    }

    // checker.go:18701-18725
    internal bool TypeResolutionHasProperty(TypeResolution r)
    {
        switch (r.propertyName)
        {
            case TypeSystemPropertyName.Type:
                return valueSymbolLinks.Get((Symbol)r.target!).resolvedType is not null;
            case TypeSystemPropertyName.DeclaredType:
                return typeAliasLinks.Get((Symbol)r.target!).declaredType is not null;
            case TypeSystemPropertyName.ResolvedTypeArguments:
                return ((Type)r.target!).AsTypeReference()!.resolvedTypeArguments is not null;
            case TypeSystemPropertyName.ResolvedBaseTypes:
                return ((Type)r.target!).AsInterfaceType()!.baseTypesResolved;
            case TypeSystemPropertyName.ResolvedBaseConstructorType:
                return ((Type)r.target!).AsInterfaceType()!.resolvedBaseConstructorType is not null;
            case TypeSystemPropertyName.ResolvedReturnType:
                return ((Signature)r.target!).resolvedReturnType is not null;
            case TypeSystemPropertyName.ResolvedBaseConstraint:
                return ((Type)r.target!).AsConstrainedType()!.resolvedBaseConstraint is not null;
            case TypeSystemPropertyName.InitializerIsUndefined:
                return (nodeLinks.Get((Node)r.target!).flags & NodeCheckFlags.InitializerIsUndefinedComputed) != 0;
            case TypeSystemPropertyName.WriteType:
                return valueSymbolLinks.Get((Symbol)r.target!).writeType is not null;
            case TypeSystemPropertyName.AliasTarget:
                return aliasSymbolLinks.Get((Symbol)r.target!).aliasTarget is not null;
        }
        throw new InvalidOperationException("Unhandled case in typeResolutionHasProperty");
    }

    // checker.go:18727-18749
    internal Type ReportCircularityError(Symbol symbol)
    {
        var declaration = symbol.ValueDeclaration;
        // Check if variable has type annotation that circularly references the variable itself
        if (declaration is not null)
        {
            if (declaration.Type() is not null)
            {
                Error(symbol.ValueDeclaration, Diag.X_0_is_referenced_directly_or_indirectly_in_its_own_type_annotation, SymbolToString(symbol));
                return errorType!;
            }
            // Check if variable has initializer that circularly references the variable itself
            if (noImplicitAny && (!NodeTests.IsParameterDeclaration(declaration) || declaration.Initializer() is not null))
            {
                Error(symbol.ValueDeclaration, Diag.X_0_implicitly_has_type_any_because_it_does_not_have_a_type_annotation_and_is_referenced_directly_or_indirectly_in_its_own_initializer, SymbolToString(symbol));
            }
        }
        else if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            var node = GetDeclarationOfAliasSymbol(symbol);
            if (node is not null)
            {
                Error(node, Diag.Circular_definition_of_import_alias_0, SymbolToString(symbol));
            }
        }
        return anyType!;
    }

    // checker.go:18751-18757
    internal Symbol[] GetPropertiesOfType(Type t)
    {
        t = GetReducedApparentType(t);
        if ((t.flags & TypeFlags.UnionOrIntersection) != 0)
        {
            return GetPropertiesOfUnionOrIntersectionType(t);
        }
        return GetPropertiesOfObjectType(t);
    }

    // checker.go:18759-18764
    internal Symbol[] GetPropertiesOfObjectType(Type t)
    {
        if ((t.flags & TypeFlags.Object) != 0)
        {
            return ResolveStructuredTypeMembers(t).Properties();
        }
        return [];
    }

    // checker.go:18766-18790
    internal Symbol[] GetPropertiesOfUnionOrIntersectionType(Type t)
    {
        var d = t.AsUnionOrIntersectionType()!;
        if (d.resolvedProperties is null)
        {
            var checked_ = new Set<string>();
            List<Symbol> props = [];
            foreach (var current in d.types)
            {
                foreach (var prop in GetPropertiesOfType(current))
                {
                    if (!checked_.Has(prop.Name))
                    {
                        checked_.Add(prop.Name);
                        var combinedProp = GetPropertyOfUnionOrIntersectionType(t, prop.Name, (t.flags & TypeFlags.Intersection) != 0 /*skipObjectFunctionPropertyAugment*/);
                        if (combinedProp is not null)
                        {
                            props.Add(combinedProp);
                        }
                    }
                }
                // The properties of a union type are those that are present in all constituent types, so
                // we only need to check the properties of the first type without index signature
                if ((t.flags & TypeFlags.Union) != 0 && GetIndexInfosOfType(current).Length == 0)
                {
                    break;
                }
            }
            d.resolvedProperties = props.ToArray();
        }
        return d.resolvedProperties;
    }

    // checker.go:18792-18794
    internal Symbol? GetPropertyOfType(Type t, string name) =>
        GetPropertyOfTypeEx(t, name, false /*skipObjectFunctionPropertyAugment*/, false /*includeTypeOnlyMembers*/);

    // Return the symbol for the property with the given name in the given type.
    // checker.go:18804-18853
    internal Symbol? GetPropertyOfTypeEx(Type t, string name, bool skipObjectFunctionPropertyAugment, bool includeTypeOnlyMembers)
    {
        t = GetReducedApparentType(t);
        if ((t.flags & TypeFlags.Object) != 0)
        {
            var resolved = ResolveStructuredTypeMembers(t);
            var symbol = resolved.members?.GetValueOrDefault(name);
            if (symbol is not null)
            {
                if (!includeTypeOnlyMembers && t.symbol is not null && (t.symbol.Flags & SymbolFlags.ValueModule) != 0 && (moduleSymbolLinks.Get(t.symbol).typeOnlyExportStarMap?.GetValueOrDefault(name)) is not null)
                {
                    // If this is the type of a module, `resolved.members.get(name)` might have effectively skipped over
                    // an `export type * from './foo'`, leaving `symbolIsValue` unable to see that the symbol is being viewed through a type-only export.
                    return null;
                }
                if (SymbolIsValueEx(symbol, includeTypeOnlyMembers))
                {
                    return symbol;
                }
            }
            if (skipObjectFunctionPropertyAugment)
            {
                return null;
            }
            Type? functionType = null;
            if (t == anyFunctionType)
            {
                functionType = globalFunctionType;
            }
            else if (resolved.CallSignatures().Length != 0)
            {
                functionType = globalCallableFunctionType;
            }
            else if (resolved.ConstructSignatures().Length != 0)
            {
                functionType = globalNewableFunctionType;
            }
            if (functionType is not null)
            {
                symbol = GetPropertyOfObjectType(functionType, name);
                if (symbol is not null)
                {
                    return symbol;
                }
            }
            return GetPropertyOfObjectType(globalObjectType!, name);
        }
        if ((t.flags & TypeFlags.Intersection) != 0)
        {
            var prop = GetPropertyOfUnionOrIntersectionType(t, name, true /*skipObjectFunctionPropertyAugment*/);
            if (prop is not null)
            {
                return prop;
            }
            if (!skipObjectFunctionPropertyAugment)
            {
                return GetPropertyOfUnionOrIntersectionType(t, name, skipObjectFunctionPropertyAugment);
            }
            return null;
        }
        if ((t.flags & TypeFlags.Union) != 0)
        {
            return GetPropertyOfUnionOrIntersectionType(t, name, skipObjectFunctionPropertyAugment);
        }
        return null;
    }

    // Return the type of the given property in the given type, or nil if no such property exists
    // checker.go:18856-18862
    internal Type? GetTypeOfPropertyOfType(Type t, string name)
    {
        var prop = GetPropertyOfType(t, name);
        if (prop is not null)
        {
            return GetTypeOfSymbol(prop);
        }
        return null;
    }

    // checker.go:18864-18866
    internal Signature[] GetSignaturesOfType(Type t, SignatureKind kind) =>
        GetSignaturesOfStructuredType(GetReducedApparentType(t), kind);

    // checker.go:18868-18877
    internal Signature[] GetSignaturesOfStructuredType(Type t, SignatureKind kind)
    {
        if ((t.flags & TypeFlags.StructuredType) == 0)
        {
            return [];
        }
        var resolved = ResolveStructuredTypeMembers(t);
        if (kind == SignatureKind.Call)
        {
            return resolved.CallSignatures();
        }
        return resolved.ConstructSignatures();
    }

    // checker.go:18879-18888
    internal IndexInfo[] GetIndexInfosOfType(Type t) => GetIndexInfosOfStructuredType(GetReducedApparentType(t));

    internal IndexInfo[] GetIndexInfosOfStructuredType(Type t)
    {
        if ((t.flags & TypeFlags.StructuredType) != 0)
        {
            return ResolveStructuredTypeMembers(t).indexInfos ?? [];
        }
        return [];
    }

    // Return the indexing info of the given kind in the given type.
    // checker.go:18892-18894
    internal IndexInfo? GetIndexInfoOfType(Type t, Type keyType) => FindIndexInfo(GetIndexInfosOfType(t), keyType);

    // checker.go:18898-18904
    internal Type? GetIndexTypeOfType(Type t, Type keyType)
    {
        var info = GetIndexInfoOfType(t, keyType);
        if (info is not null)
        {
            return info.valueType;
        }
        return null;
    }

    // checker.go:18906-18911
    internal Type GetIndexTypeOfTypeEx(Type t, Type keyType, Type defaultType)
    {
        var result = GetIndexTypeOfType(t, keyType);
        if (result is not null)
        {
            return result;
        }
        return defaultType;
    }

    // checker.go:18913-18915
    internal IndexInfo? GetApplicableIndexInfo(Type t, Type keyType) => FindApplicableIndexInfo(GetIndexInfosOfType(t), keyType);

    // checker.go:18917-18922
    internal IndexInfo? GetApplicableIndexInfoForName(Type t, string name)
    {
        if (IsLateBoundName(name))
        {
            return GetApplicableIndexInfo(t, esSymbolType!);
        }
        return GetApplicableIndexInfo(t, GetStringLiteralType(name));
    }

    // checker.go:18924-18957
    internal IndexInfo? FindApplicableIndexInfo(IndexInfo[] indexInfos, Type keyType)
    {
        // Index signatures for type 'string' are considered only when no other index signatures apply.
        IndexInfo? stringIndexInfo = null;
        List<IndexInfo> applicableInfos = new(8);
        foreach (var info in indexInfos)
        {
            if (info.keyType == stringType)
            {
                stringIndexInfo = info;
            }
            else if (IsApplicableIndexType(keyType, info.keyType))
            {
                applicableInfos.Add(info);
            }
        }
        // When more than one index signature is applicable we create a synthetic IndexInfo.
        switch (applicableInfos.Count)
        {
            case 0:
                if (stringIndexInfo is not null && IsApplicableIndexType(keyType, stringType!))
                {
                    return stringIndexInfo;
                }
                return null;
            case 1:
                return applicableInfos[0];
            default:
                var isReadonly = true;
                var types = new Type[applicableInfos.Count];
                for (int i = 0; i < applicableInfos.Count; i++)
                {
                    var info = applicableInfos[i];
                    types[i] = info.valueType;
                    if (!info.isReadonly)
                    {
                        isReadonly = false;
                    }
                }
                return NewIndexInfo(unknownType!, GetIntersectionType(types), isReadonly, null, null);
        }
    }

    // checker.go:18959-18965
    internal bool IsApplicableIndexType(Type source, Type target)
    {
        // A 'string' index signature applies to types assignable to 'string' or 'number', etc.
        return IsTypeAssignableTo(source, target) ||
            (target == stringType && IsTypeAssignableTo(source, numberType!)) ||
            (target == numberType && (source == numericStringType || ((source.flags & TypeFlags.StringLiteral) != 0 && IsNumericLiteralName(GetStringLiteralValue(source)))));
    }

    // checker.go:18967-18994
    internal StructuredType ResolveStructuredTypeMembers(Type t)
    {
        if ((t.objectFlags & ObjectFlags.MembersResolved) == 0)
        {
            if ((t.flags & TypeFlags.Object) != 0)
            {
                if ((t.objectFlags & ObjectFlags.Reference) != 0)
                {
                    ResolveTypeReferenceMembers(t);
                }
                else if ((t.objectFlags & ObjectFlags.ClassOrInterface) != 0)
                {
                    ResolveClassOrInterfaceMembers(t);
                }
                else if ((t.objectFlags & ObjectFlags.ReverseMapped) != 0)
                {
                    ResolveReverseMappedTypeMembers(t);
                }
                else if ((t.objectFlags & ObjectFlags.Anonymous) != 0)
                {
                    ResolveAnonymousTypeMembers(t);
                }
                else if ((t.objectFlags & ObjectFlags.Mapped) != 0)
                {
                    ResolveMappedTypeMembers(t);
                }
                else
                {
                    throw new InvalidOperationException("Unhandled case in resolveStructuredTypeMembers");
                }
            }
            else if ((t.flags & TypeFlags.Union) != 0)
            {
                ResolveUnionTypeMembers(t);
            }
            else if ((t.flags & TypeFlags.Intersection) != 0)
            {
                ResolveIntersectionTypeMembers(t);
            }
            else
            {
                throw new InvalidOperationException("Unhandled case in resolveStructuredTypeMembers");
            }
        }
        return t.AsStructuredType()!;
    }

    // checker.go:18996-18998
    internal void ResolveClassOrInterfaceMembers(Type t) => ResolveObjectTypeMembers(t, t, null, null);

    // checker.go:19000-19009
    internal void ResolveTypeReferenceMembers(Type t)
    {
        var source = t.Target();
        var typeParameters = source.AsInterfaceType()!.allTypeParameters;
        var typeArguments = GetTypeArguments(t);
        var paddedTypeArguments = typeArguments;
        if (typeParameters is not null && typeArguments.Length == typeParameters.Length - 1)
        {
            paddedTypeArguments = CoreUtil.Concatenate(typeArguments, [t]);
        }
        ResolveObjectTypeMembers(t, source, typeParameters, paddedTypeArguments);
    }

    // checker.go:19011-19061
    internal void ResolveObjectTypeMembers(Type t, Type source, Type[]? typeParameters, Type[]? typeArguments)
    {
        TypeMapper? mapper = null;
        SymbolTable? members;
        Signature[]? callSignatures;
        Signature[]? constructSignatures;
        IndexInfo[]? indexInfos;
        var instantiated = false;
        var resolved = ResolveDeclaredMembers(source);
        if (SlicesEqual(typeParameters, typeArguments))
        {
            members = resolved.declaredMembers;
            callSignatures = resolved.declaredCallSignatures;
            constructSignatures = resolved.declaredConstructSignatures;
            indexInfos = resolved.declaredIndexInfos;
        }
        else
        {
            instantiated = true;
            mapper = TypeMapper.NewTypeMapper(typeParameters!, typeArguments!);
            members = InstantiateSymbolTable(resolved.declaredMembers, mapper);
            callSignatures = InstantiateSignatures(resolved.declaredCallSignatures, mapper);
            constructSignatures = InstantiateSignatures(resolved.declaredConstructSignatures, mapper);
            indexInfos = InstantiateIndexInfos(resolved.declaredIndexInfos, mapper);
        }
        var baseTypes = GetBaseTypes(source);
        if (baseTypes.Length != 0)
        {
            if (!instantiated)
            {
                members = members is null ? new SymbolTable() : new SymbolTable(members);
            }
            SetStructuredTypeMembers(t, members, callSignatures, constructSignatures, indexInfos);
            var thisArgument = CoreUtil.LastOrNil(typeArguments);
            t.objectFlags |= ObjectFlags.UnresolvedMembers;
            foreach (var baseType in baseTypes)
            {
                var instantiatedBaseType = baseType;
                if (thisArgument is not null)
                {
                    instantiatedBaseType = GetTypeWithThisArgument(InstantiateType(baseType, mapper), thisArgument, false /*needsApparentType*/);
                }
                members = AddInheritedMembers(members, GetPropertiesOfType(instantiatedBaseType));
                callSignatures = CoreUtil.Concatenate(callSignatures, GetSignaturesOfType(instantiatedBaseType, SignatureKind.Call));
                constructSignatures = CoreUtil.Concatenate(constructSignatures, GetSignaturesOfType(instantiatedBaseType, SignatureKind.Construct));
                IndexInfo[]? inheritedIndexInfos;
                if (instantiatedBaseType != anyType)
                {
                    inheritedIndexInfos = GetIndexInfosOfType(instantiatedBaseType);
                }
                else
                {
                    inheritedIndexInfos = [anyBaseTypeIndexInfo!];
                }
                var currentIndexInfos = indexInfos;
                indexInfos = CoreUtil.Concatenate(indexInfos, CoreUtil.Filter(inheritedIndexInfos, info => FindIndexInfo(currentIndexInfos, info.keyType) is null));
            }
            t.objectFlags &= ~ObjectFlags.UnresolvedMembers;
        }
        SetStructuredTypeMembers(t, members, callSignatures, constructSignatures, indexInfos);
    }

    // checker.go:19063-19070
    internal IndexInfo? FindIndexInfo(IndexInfo[]? indexInfos, Type keyType)
    {
        foreach (var info in indexInfos ?? [])
        {
            if (info.keyType == keyType)
            {
                return info;
            }
        }
        return null;
    }

    // checker.go:19072-19109
    internal Type[] GetBaseTypes(Type t)
    {
        if ((t.objectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Tuple)) == 0)
        {
            return [];
        }
        var data = t.AsInterfaceType()!;
        if (!data.baseTypesResolved)
        {
            if (!PushTypeResolution(t, TypeSystemPropertyName.ResolvedBaseTypes))
            {
                return data.resolvedBaseTypes ?? [];
            }
            if ((t.objectFlags & ObjectFlags.Tuple) != 0)
            {
                data.resolvedBaseTypes = [GetTupleBaseType(t)];
            }
            else if ((t.symbol!.Flags & (SymbolFlags.Class | SymbolFlags.Interface)) != 0)
            {
                if ((t.symbol.Flags & SymbolFlags.Class) != 0)
                {
                    ResolveBaseTypesOfClass(t);
                }
                if ((t.symbol.Flags & SymbolFlags.Interface) != 0)
                {
                    ResolveBaseTypesOfInterface(t);
                }
            }
            else
            {
                throw new InvalidOperationException("Unhandled case in getBaseTypes");
            }
            if (!PopTypeResolution() && t.symbol!.Declarations is not null)
            {
                foreach (var declaration in t.symbol.Declarations)
                {
                    if (NodeTests.IsClassDeclaration(declaration) || NodeTests.IsInterfaceDeclaration(declaration))
                    {
                        ReportCircularBaseType(declaration, t);
                    }
                }
            }
            // In general, base type resolution always precedes member resolution. See TS#16861.
            t.objectFlags &= ~ObjectFlags.MembersResolved;
            data.baseTypesResolved = true;
        }
        return data.resolvedBaseTypes ?? [];
    }

    // checker.go:19111-19123
    internal Type GetTupleBaseType(Type t)
    {
        var typeParameters = t.AsTupleType().TypeParameters() ?? [];
        var elementInfos = t.AsTupleType().elementInfos ?? [];
        var elementTypes = new Type[typeParameters.Length];
        for (int i = 0; i < typeParameters.Length; i++)
        {
            var tp = typeParameters[i];
            if ((elementInfos[i].flags & ElementFlags.Variadic) != 0)
            {
                elementTypes[i] = GetIndexedAccessType(tp, numberType!);
            }
            else
            {
                elementTypes[i] = tp;
            }
        }
        return CreateArrayTypeEx(GetUnionType(elementTypes), t.AsTupleType().readonlyElements);
    }

    // checker.go:19125-19170
    internal void ResolveBaseTypesOfClass(Type t)
    {
        var baseConstructorType = GetApparentType(GetBaseConstructorTypeOfClass(t));
        if ((baseConstructorType.flags & (TypeFlags.Object | TypeFlags.Intersection | TypeFlags.Any)) == 0)
        {
            return;
        }
        var baseTypeNode = GetBaseTypeNodeOfClass(t);
        Type? baseType;
        Type? originalBaseType = null;
        if (baseConstructorType.symbol is not null)
        {
            originalBaseType = GetDeclaredTypeOfSymbol(baseConstructorType.symbol);
        }
        if (baseConstructorType.symbol is not null && (baseConstructorType.symbol.Flags & SymbolFlags.Class) != 0 && AreAllOuterTypeParametersApplied(originalBaseType!))
        {
            // When base constructor type is a class with no captured type arguments we know that the constructors all have the same type parameters.
            baseType = GetTypeFromClassOrInterfaceReference(baseTypeNode!, baseConstructorType.symbol);
        }
        else if ((baseConstructorType.flags & TypeFlags.Any) != 0)
        {
            baseType = baseConstructorType;
        }
        else
        {
            // The class derives from a "class-like" constructor function.
            var constructors = GetInstantiatedConstructorsForTypeArguments(baseConstructorType, baseTypeNode!.TypeArguments(), baseTypeNode!);
            if (constructors.Length == 0)
            {
                Error(baseTypeNode!.Expression(), Diag.No_base_constructor_has_the_specified_number_of_type_arguments);
                return;
            }
            baseType = GetReturnTypeOfSignature(constructors[0]);
        }
        if (IsErrorType(baseType))
        {
            return;
        }
        var reducedBaseType = GetReducedType(baseType);
        if (!IsValidBaseType(reducedBaseType))
        {
            var errorNode = baseTypeNode!.Expression();
            var diagnostic = ElaborateNeverIntersection(null, errorNode, baseType);
            diagnostic = NewDiagnosticChainForNode(diagnostic, errorNode, Diag.Base_constructor_return_type_0_is_not_an_object_type_or_intersection_of_object_types_with_statically_known_members, TypeToString(reducedBaseType));
            AddDiagnostic(diagnostic!);
            return;
        }
        if (t == reducedBaseType || HasBaseType(reducedBaseType, t))
        {
            Error(t.symbol!.ValueDeclaration, Diag.Type_0_recursively_references_itself_as_a_base_type, TypeToString(t));
            return;
        }
        t.AsInterfaceType()!.resolvedBaseTypes = [reducedBaseType];
    }

    // checker.go:19172-19178
    internal static Node? GetBaseTypeNodeOfClass(Type t)
    {
        var decl = GetClassLikeDeclarationOfSymbol(t.symbol!);
        if (decl is not null)
        {
            return GetExtendsHeritageClauseElement(decl);
        }
        return null;
    }

    // checker.go:19180-19189
    internal Signature[] GetInstantiatedConstructorsForTypeArguments(Type t, Node[]? typeArgumentNodes, Node location)
    {
        var signatures = GetConstructorsForTypeArguments(t, typeArgumentNodes, location);
        var typeArguments = CoreUtil.Map(typeArgumentNodes, GetTypeFromTypeNode);
        return CoreUtil.SameMap(signatures, sig =>
        {
            if (sig.typeParameters is { Length: > 0 })
            {
                return GetSignatureInstantiation(sig, typeArguments, AstUtilities.IsInJSFile(location), null);
            }
            return sig;
        })!;
    }

    // checker.go:19191-19196
    internal Signature[] GetConstructorsForTypeArguments(Type t, Node[]? typeArgumentNodes, Node location)
    {
        var typeArgCount = (typeArgumentNodes ?? []).Length;
        return CoreUtil.Filter(GetSignaturesOfType(t, SignatureKind.Construct), sig =>
            typeArgCount >= GetMinTypeArgumentCount(sig.typeParameters) && typeArgCount <= (sig.typeParameters ?? []).Length) ?? [];
    }

    // checker.go:19198-19213
    internal Signature GetSignatureInstantiation(Signature sig, Type[]? typeArguments, bool isJavaScript, Type[]? inferredTypeParameters)
    {
        var instantiatedSignature = GetSignatureInstantiationWithoutFillingInTypeArguments(sig, FillMissingTypeArguments(typeArguments, sig.typeParameters, GetMinTypeArgumentCount(sig.typeParameters), isJavaScript));
        if (inferredTypeParameters is { Length: > 0 })
        {
            var returnSignature = GetSingleCallOrConstructSignature(GetReturnTypeOfSignature(instantiatedSignature));
            if (returnSignature is not null)
            {
                var newReturnSignature = CloneSignature(returnSignature);
                newReturnSignature.typeParameters = inferredTypeParameters;
                var newReturnType = GetOrCreateTypeFromSignature(newReturnSignature);
                newReturnType.AsObjectType()!.mapper = instantiatedSignature.mapper;
                var newInstantiatedSignature = CloneSignature(instantiatedSignature);
                newInstantiatedSignature.resolvedReturnType = newReturnType;
                return newInstantiatedSignature;
            }
        }
        return instantiatedSignature;
    }

    // checker.go:19215-19221
    internal Signature CloneSignature(Signature sig)
    {
        var result = NewSignature(sig.flags & SignatureFlags.PropagatingFlags, sig.declaration, sig.typeParameters, sig.thisParameter, sig.parameters, null, null, sig.minArgumentCount);
        result.target = sig.target;
        result.mapper = sig.mapper;
        result.composite = sig.composite;
        return result;
    }

    // checker.go:19223-19231
    internal Signature GetSignatureInstantiationWithoutFillingInTypeArguments(Signature sig, Type[]? typeArguments)
    {
        var key = new CachedSignatureKey(sig, GetTypeListKey(typeArguments));
        var instantiation = cachedSignatures.GetValueOrDefault(key);
        if (instantiation is null)
        {
            instantiation = CreateSignatureInstantiation(sig, typeArguments);
            cachedSignatures[key] = instantiation;
        }
        return instantiation;
    }

    // checker.go:19233-19235
    internal Signature CreateSignatureInstantiation(Signature sig, Type[]? typeArguments) =>
        InstantiateSignatureEx(sig, CreateSignatureTypeMapper(sig, typeArguments), true /*eraseTypeParameters*/);

    // checker.go:19237-19239
    internal TypeMapper CreateSignatureTypeMapper(Signature sig, Type[]? typeArguments) =>
        TypeMapper.NewTypeMapper(GetTypeParametersForMapper(sig), typeArguments!);

    // checker.go:19241-19243
    internal Type[] GetTypeParametersForMapper(Signature sig) =>
        CoreUtil.SameMap(sig.typeParameters, tp => InstantiateType(tp, tp.Mapper()))!;

    // If type has a single call signature and no other members, return that signature. Otherwise, return nil.
    // checker.go:19246-19248
    internal Signature? GetSingleCallSignature(Type t) => GetSingleSignature(t, SignatureKind.Call, false /*allowMembers*/);

    // checker.go:19250-19256
    internal Signature? GetSingleCallOrConstructSignature(Type t)
    {
        var callSig = GetSingleSignature(t, SignatureKind.Call, false /*allowMembers*/);
        if (callSig is not null)
        {
            return callSig;
        }
        return GetSingleSignature(t, SignatureKind.Construct, false /*allowMembers*/);
    }

    // checker.go:19258-19271
    internal Signature? GetSingleSignature(Type t, SignatureKind kind, bool allowMembers)
    {
        if ((t.flags & TypeFlags.Object) != 0)
        {
            var resolved = ResolveStructuredTypeMembers(t);
            if (allowMembers || (resolved.Properties().Length == 0 && (resolved.indexInfos ?? []).Length == 0))
            {
                if (kind == SignatureKind.Call && resolved.CallSignatures().Length == 1 && resolved.ConstructSignatures().Length == 0)
                {
                    return resolved.CallSignatures()[0];
                }
                if (kind == SignatureKind.Construct && resolved.ConstructSignatures().Length == 1 && resolved.CallSignatures().Length == 0)
                {
                    return resolved.ConstructSignatures()[0];
                }
            }
        }
        return null;
    }

    // checker.go:19273-19299
    internal Type GetOrCreateTypeFromSignature(Signature sig)
    {
        // There are two ways to declare a construct signature.
        if (sig.isolatedSignatureType is null)
        {
            var kind = SyntaxKind.Unknown;
            if (sig.declaration is not null)
            {
                kind = sig.declaration.Kind;
            }
            // If declaration is undefined, it is likely to be the signature of the default constructor.
            var isConstructor = kind == SyntaxKind.Unknown || kind == SyntaxKind.Constructor || kind == SyntaxKind.ConstructSignature || kind == SyntaxKind.ConstructorType;

            Symbol? symbol = null;
            if (sig.declaration is not null)
            {
                symbol = sig.declaration.Symbol();
            }
            var t = NewObjectType(ObjectFlags.Anonymous | ObjectFlags.SingleSignatureType, symbol);
            if (isConstructor)
            {
                SetStructuredTypeMembers(t, null, null, [sig], null);
            }
            else
            {
                SetStructuredTypeMembers(t, null, [sig], null, null);
            }
            sig.isolatedSignatureType = t;
        }
        return sig.isolatedSignatureType;
    }

    // checker.go:19301-19312
    internal Signature GetErasedSignature(Signature signature)
    {
        if ((signature.typeParameters ?? []).Length == 0)
        {
            return signature;
        }
        var key = new CachedSignatureKey(signature, SignatureKeys.Erased);
        var erased = cachedSignatures.GetValueOrDefault(key);
        if (erased is null)
        {
            erased = InstantiateSignatureEx(signature, TypeMapper.NewArrayToSingleTypeMapper(signature.typeParameters!, anyType!), true /*eraseTypeParameters*/);
            cachedSignatures[key] = erased;
        }
        return erased;
    }

    // checker.go:19314-19325
    internal Signature GetCanonicalSignature(Signature signature)
    {
        if ((signature.typeParameters ?? []).Length == 0)
        {
            return signature;
        }
        var key = new CachedSignatureKey(signature, SignatureKeys.Canonical);
        var canonical = cachedSignatures.GetValueOrDefault(key);
        if (canonical is null)
        {
            canonical = CreateCanonicalSignature(signature);
            cachedSignatures[key] = canonical;
        }
        return canonical;
    }

    // checker.go:19327-19341
    internal Signature CreateCanonicalSignature(Signature signature)
    {
        // Create an instantiation of the signature where each unconstrained type parameter is replaced with its original.
        var typeArguments = CoreUtil.Map(signature.typeParameters, tp =>
        {
            if (tp.Target() is not null && GetConstraintOfTypeParameter(tp.Target()) is null)
            {
                return tp.Target();
            }
            return tp;
        });
        return GetSignatureInstantiation(signature, typeArguments, AstUtilities.IsInJSFile(signature.declaration), null /*inferredTypeParameters*/);
    }

    // checker.go:19343-19368
    internal Signature GetBaseSignature(Signature signature)
    {
        var typeParameters = signature.typeParameters ?? [];
        if (typeParameters.Length == 0)
        {
            return signature;
        }
        var key = new CachedSignatureKey(signature, SignatureKeys.Base);
        var cached = cachedSignatures.GetValueOrDefault(key);
        if (cached is not null)
        {
            return cached;
        }
        var baseConstraintMapper = TypeMapper.NewTypeMapper(typeParameters, CoreUtil.Map(typeParameters, tp => GetConstraintOfTypeParameter(tp) ?? unknownType!)!);
        var baseConstraints = CoreUtil.Map(typeParameters, tp => InstantiateType(tp, baseConstraintMapper));
        // Run the immediate constraint mapper N-1 times.
        for (int i = 0; i < typeParameters.Length - 1; i++)
        {
            baseConstraints = InstantiateTypes(baseConstraints, baseConstraintMapper);
        }
        // and then apply a type eraser to remove any remaining circularly dependent type parameters
        baseConstraints = InstantiateTypes(baseConstraints, TypeMapper.NewArrayToSingleTypeMapper(typeParameters, anyType!));
        var result = InstantiateSignatureEx(signature, TypeMapper.NewTypeMapper(typeParameters, baseConstraints!), true /*eraseTypeParameters*/);
        cachedSignatures[key] = result;
        return result;
    }

    // Instantiate a generic signature in the context of a non-generic signature
    // checker.go:19371-19401
    internal Signature InstantiateSignatureInContextOf(Signature signature, Signature contextualSignature, InferenceContext? inferenceContext, TypeComparer? compareTypes)
    {
        var context = NewInferenceContext(GetTypeParametersForMapper(signature), signature, InferenceFlags.None, compareTypes);
        // We clone the inferenceContext to avoid fixing.
        var restType = GetEffectiveRestType(contextualSignature);
        TypeMapper? mapper = null;
        if (inferenceContext is not null)
        {
            if (restType is not null && (restType.flags & TypeFlags.TypeParameter) != 0)
            {
                mapper = inferenceContext.nonFixingMapper;
            }
            else
            {
                mapper = inferenceContext.mapper;
            }
        }
        Signature sourceSignature;
        if (mapper is not null)
        {
            sourceSignature = InstantiateSignature(contextualSignature, mapper);
        }
        else
        {
            sourceSignature = contextualSignature;
        }
        ApplyToParameterTypes(sourceSignature, signature, (source, target) =>
        {
            // Type parameters from outer context referenced by source type are fixed by instantiation of the source type
            InferTypes(context.inferences, source, target, InferencePriority.None, false);
        });
        if (inferenceContext is null)
        {
            ApplyToReturnTypes(contextualSignature, signature, (source, target) =>
            {
                InferTypes(context.inferences, source, target, InferencePriority.ReturnType, false);
            });
        }
        return GetSignatureInstantiation(signature, GetInferredTypes(context), AstUtilities.IsInJSFile(contextualSignature.declaration), null /*inferredTypeParameters*/);
    }

    // checker.go:19403-19423
    internal void ResolveBaseTypesOfInterface(Type t)
    {
        var data = t.AsInterfaceType()!;
        foreach (var declaration in t.symbol!.Declarations ?? [])
        {
            if (NodeTests.IsInterfaceDeclaration(declaration))
            {
                foreach (var node in GetExtendsHeritageClauseElements(declaration))
                {
                    var baseType = GetReducedType(GetTypeFromTypeNode(node));
                    if (!IsErrorType(baseType))
                    {
                        if (IsValidBaseType(baseType))
                        {
                            if (t != baseType && !HasBaseType(baseType, t))
                            {
                                data.resolvedBaseTypes = [.. data.resolvedBaseTypes ?? [], baseType];
                            }
                            else
                            {
                                ReportCircularBaseType(declaration, t);
                            }
                        }
                        else
                        {
                            Error(node, Diag.An_interface_can_only_extend_an_object_type_or_intersection_of_object_types_with_statically_known_members);
                        }
                    }
                }
            }
        }
    }

    // checker.go:19425-19435
    internal bool AreAllOuterTypeParametersApplied(Type t)
    {
        // An unapplied type parameter has its symbol still the same as the matching argument symbol.
        var outerTypeParameters = t.AsInterfaceType()!.OuterTypeParameters() ?? [];
        if (outerTypeParameters.Length != 0)
        {
            var last = outerTypeParameters.Length - 1;
            var typeArguments = GetTypeArguments(t);
            return outerTypeParameters[last].symbol != typeArguments[last].symbol;
        }
        return true;
    }

    // checker.go:19437-19439
    internal void ReportCircularBaseType(Node node, Type t) =>
        Error(node, Diag.Type_0_recursively_references_itself_as_a_base_type, TypeToStringEx(t, null, TypeFormatFlags.WriteArrayAsGenericType, null));

    // A valid base type is `any`, an object type or intersection of object types.
    // checker.go:19442-19453
    internal bool IsValidBaseType(Type t)
    {
        if ((t.flags & TypeFlags.TypeParameter) != 0)
        {
            var constraint = GetBaseConstraintOfType(t);
            if (constraint is not null)
            {
                return IsValidBaseType(constraint);
            }
        }
        return ((t.flags & (TypeFlags.Object | TypeFlags.NonPrimitive | TypeFlags.Any)) != 0 && !IsGenericMappedType(t)) ||
            ((t.flags & TypeFlags.Intersection) != 0 && CoreUtil.Every(t.Types(), IsValidBaseType));
    }

    // checker.go:19456-19469
    internal bool HasBaseType(Type t, Type? checkBase)
    {
        bool Check(Type t)
        {
            if ((t.objectFlags & (ObjectFlags.ClassOrInterface | ObjectFlags.Reference)) != 0)
            {
                var target = GetTargetType(t);
                return target == checkBase || CoreUtil.Some(GetBaseTypes(target), Check);
            }
            if ((t.flags & TypeFlags.Intersection) != 0)
            {
                return CoreUtil.Some(t.Types(), Check);
            }
            return false;
        }
        return Check(t);
    }

    // checker.go:19471-19476
    internal static Type GetTargetType(Type t)
    {
        if ((t.objectFlags & ObjectFlags.Reference) != 0)
        {
            return t.Target();
        }
        return t;
    }

    // checker.go:19478-19501
    internal Type GetTypeWithThisArgument(Type t, Type? thisArgument, bool needApparentType)
    {
        if ((t.objectFlags & ObjectFlags.Reference) != 0)
        {
            var target = t.Target();
            var typeArguments = GetTypeArguments(t);
            if ((target.AsInterfaceType()!.TypeParameters() ?? []).Length == typeArguments.Length)
            {
                thisArgument ??= target.AsInterfaceType()!.thisType;
                return CreateTypeReference(target, CoreUtil.Concatenate(typeArguments, [thisArgument!]));
            }
            return t;
        }
        else if ((t.flags & TypeFlags.Intersection) != 0)
        {
            var types = t.Types();
            var newTypes = CoreUtil.SameMap(types, tt => GetTypeWithThisArgument(tt, thisArgument, needApparentType));
            if (CoreUtil.Same(newTypes, types))
            {
                return t;
            }
            return GetIntersectionType(newTypes!);
        }
        if (needApparentType)
        {
            return GetApparentType(t);
        }
        return t;
    }

    // checker.go:19503-19515
    internal SymbolTable? AddInheritedMembers(SymbolTable? symbols, Symbol[] baseSymbols)
    {
        foreach (var base_ in baseSymbols)
        {
            if (!IsStaticPrivateIdentifierProperty(base_))
            {
                if (symbols is null || !symbols.TryGetValue(base_.Name, out var s) || (s.Flags & SymbolFlags.Value) == 0)
                {
                    symbols ??= new SymbolTable();
                    symbols[base_.Name] = base_;
                }
            }
        }
        return symbols;
    }

    // checker.go:19517-19528
    internal InterfaceType ResolveDeclaredMembers(Type t)
    {
        var d = t.AsInterfaceType()!;
        if (!d.declaredMembersResolved)
        {
            var members = GetMembersOfSymbol(t.symbol!);
            d.declaredMembersResolved = true;
            d.declaredMembers = members;
            d.declaredCallSignatures = GetSignaturesOfSymbol(members?.GetValueOrDefault(InternalSymbolName.Call));
            d.declaredConstructSignatures = GetSignaturesOfSymbol(members?.GetValueOrDefault(InternalSymbolName.New));
            d.declaredIndexInfos = GetIndexInfosOfSymbol(t.symbol!);
        }
        return d;
    }

    // checker.go:19530-19536
    internal IndexInfo[]? GetIndexInfosOfSymbol(Symbol symbol)
    {
        var indexSymbol = GetIndexSymbol(symbol);
        if (indexSymbol is not null)
        {
            // map-iter: 序无关 — getIndexInfosOfIndexSymbol reduces over the members without positional dependence.
            return GetIndexInfosOfIndexSymbol(indexSymbol, GetMembersOfSymbol(symbol)?.Values.ToArray() ?? []);
        }
        return null;
    }

    // note intentional similarities to index signature building in `checkObjectLiteral` for parity
    // checker.go:19539-19623
    internal IndexInfo[]? GetIndexInfosOfIndexSymbol(Symbol indexSymbol, Symbol[] siblingSymbols)
    {
        List<IndexInfo>? indexInfos = null;
        var hasComputedStringProperty = false;
        var hasComputedNumberProperty = false;
        var hasComputedSymbolProperty = false;
        var readonlyComputedStringProperty = true;
        var readonlyComputedNumberProperty = true;
        var readonlyComputedSymbolProperty = true;
        List<Symbol> propertySymbols = [];
        foreach (var declaration in indexSymbol.Declarations ?? [])
        {
            if (NodeTests.IsIndexSignatureDeclaration(declaration))
            {
                var parameters = declaration.Parameters();
                var returnTypeNode = declaration.Type();
                if (parameters.Length == 1)
                {
                    var typeNode = parameters[0].Type();
                    if (typeNode is not null)
                    {
                        var valueType = anyType!;
                        if (returnTypeNode is not null)
                        {
                            valueType = GetTypeFromTypeNode(returnTypeNode);
                        }
                        var currentInfos = indexInfos;
                        ForEachType(GetTypeFromTypeNode(typeNode), keyType =>
                        {
                            if (IsValidIndexKeyType(keyType) && FindIndexInfo(currentInfos?.ToArray(), keyType) is null)
                            {
                                var indexInfo = NewIndexInfo(keyType, valueType, AstUtilities.HasSyntacticModifier(declaration, ModifierFlags.Readonly), declaration, null);
                                (indexInfos ??= []).Add(indexInfo);
                                currentInfos = indexInfos;
                            }
                        });
                    }
                }
            }
            else if (HasLateBindableIndexSignature(declaration))
            {
                Node? declName;
                if (NodeTests.IsBinaryExpression(declaration))
                {
                    declName = declaration.AsBinaryExpression().Left;
                }
                else
                {
                    declName = declaration.Name();
                }
                Type keyType;
                if (NodeTests.IsElementAccessExpression(declName!))
                {
                    keyType = CheckExpressionCached(declName!.AsElementAccessExpression().ArgumentExpression!);
                }
                else
                {
                    keyType = CheckComputedPropertyName(declName!);
                }
                if (FindIndexInfo(indexInfos?.ToArray(), keyType) is not null)
                {
                    continue;
                    // Explicit index for key type takes priority
                }
                if (IsTypeAssignableTo(keyType, stringNumberSymbolType!))
                {
                    if (IsTypeAssignableTo(keyType, numberType!))
                    {
                        hasComputedNumberProperty = true;
                        if (!HasReadonlyModifier(declaration))
                        {
                            readonlyComputedNumberProperty = false;
                        }
                    }
                    else if (IsTypeAssignableTo(keyType, esSymbolType!))
                    {
                        hasComputedSymbolProperty = true;
                        if (!HasReadonlyModifier(declaration))
                        {
                            readonlyComputedSymbolProperty = false;
                        }
                    }
                    else
                    {
                        hasComputedStringProperty = true;
                        if (!HasReadonlyModifier(declaration))
                        {
                            readonlyComputedStringProperty = false;
                        }
                    }
                    propertySymbols.Add(declaration.Symbol()!);
                }
            }
        }
        if (hasComputedStringProperty || hasComputedNumberProperty || hasComputedSymbolProperty)
        {
            foreach (var sym in siblingSymbols)
            {
                if (sym != indexSymbol)
                {
                    propertySymbols.Add(sym);
                }
            }
            // aggregate similar index infos implied to be the same key to the same combined index info
            if (hasComputedStringProperty && FindIndexInfo(indexInfos?.ToArray(), stringType!) is null)
            {
                (indexInfos ??= []).Add(GetObjectLiteralIndexInfo(readonlyComputedStringProperty, propertySymbols.ToArray(), stringType!));
            }
            if (hasComputedNumberProperty && FindIndexInfo(indexInfos?.ToArray(), numberType!) is null)
            {
                (indexInfos ??= []).Add(GetObjectLiteralIndexInfo(readonlyComputedNumberProperty, propertySymbols.ToArray(), numberType!));
            }
            if (hasComputedSymbolProperty && FindIndexInfo(indexInfos?.ToArray(), esSymbolType!) is null)
            {
                (indexInfos ??= []).Add(GetObjectLiteralIndexInfo(readonlyComputedSymbolProperty, propertySymbols.ToArray(), esSymbolType!));
            }
        }
        return indexInfos?.ToArray();
    }

    // NOTE: currently does not make pattern literal indexers, eg `${number}px`
    // checker.go:19626-19644
    internal IndexInfo GetObjectLiteralIndexInfo(bool isReadonly, Symbol[] properties, Type keyType)
    {
        List<Type> propTypes = [];
        List<Node> components = [];
        foreach (var prop in properties)
        {
            if ((keyType == stringType && !IsSymbolWithSymbolName(prop)) ||
                (keyType == numberType && IsSymbolWithNumericName(prop)) ||
                (keyType == esSymbolType && IsSymbolWithSymbolName(prop)))
            {
                propTypes.Add(GetTypeOfSymbol(prop));
                if (IsSymbolWithComputedName(prop))
                {
                    components.Add(prop.Declarations![0]);
                }
            }
        }
        var unionType = undefinedType!;
        if (propTypes.Count != 0)
        {
            unionType = GetUnionTypeEx(propTypes.ToArray(), UnionReduction.Subtype, null, null);
        }
        return NewIndexInfo(keyType, unionType, isReadonly, null /*declaration*/, components.ToArray());
    }

    // checker.go:19646-19655
    internal bool IsSymbolWithSymbolName(Symbol symbol)
    {
        if (IsKnownSymbol(symbol))
        {
            return true;
        }
        if ((symbol.Declarations ?? []).Length != 0)
        {
            var name = symbol.Declarations![0].Name();
            return name is not null && NodeTests.IsComputedPropertyName(name) && IsTypeAssignableToKind(CheckComputedPropertyName(name), TypeFlags.ESSymbol);
        }
        return false;
    }

    // checker.go:19657-19666
    internal bool IsSymbolWithNumericName(Symbol symbol)
    {
        if (IsNumericLiteralName(symbol.Name))
        {
            return true;
        }
        if ((symbol.Declarations ?? []).Length != 0)
        {
            var name = symbol.Declarations![0].Name();
            return name is not null && IsNumericName(name);
        }
        return false;
    }

    // checker.go:19668-19674
    internal bool IsSymbolWithComputedName(Symbol symbol)
    {
        if ((symbol.Declarations ?? []).Length != 0)
        {
            var name = symbol.Declarations![0].Name();
            return name is not null && NodeTests.IsComputedPropertyName(name);
        }
        return false;
    }

    // checker.go:19676-19684
    internal bool IsNumericName(Node name)
    {
        switch (name.Kind)
        {
            case SyntaxKind.ComputedPropertyName:
                return IsNumericComputedName(name);
            case SyntaxKind.Identifier:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.StringLiteral:
                return IsNumericLiteralName(name.Text());
        }
        return false;
    }

    // checker.go:19686-19690
    internal bool IsNumericComputedName(Node name) =>
        IsTypeAssignableToKind(CheckComputedPropertyName(name), TypeFlags.NumberLike);

    // checker.go:19692-19696
    internal bool IsValidIndexKeyType(Type t) =>
        (t.flags & (TypeFlags.String | TypeFlags.Number | TypeFlags.ESSymbol)) != 0 ||
        IsPatternLiteralType(t) ||
        ((t.flags & TypeFlags.Intersection) != 0 && !IsGenericType(t) && CoreUtil.Some(t.Types(), IsValidIndexKeyType));

    // checker.go:19707-19709
    internal Symbol? GetIndexSymbol(Symbol symbol) => GetMembersOfSymbol(symbol)?.GetValueOrDefault(InternalSymbolName.Index);

    // checker.go:19711-19739
    internal Signature[] GetSignaturesOfSymbol(Symbol? symbol)
    {
        if (symbol is null)
        {
            return [];
        }
        List<Signature> result = [];
        var declarations = symbol.Declarations ?? [];
        for (int i = 0; i < declarations.Length; i++)
        {
            var decl = declarations[i];
            if (!AstUtilities.IsFunctionLike(decl))
            {
                continue;
            }
            // Don't include signature if node is the implementation of an overloaded function.
            if (i > 0 && decl.Body() is not null)
            {
                var previous = declarations[i - 1];
                if (decl.Parent == previous.Parent && decl.Kind == previous.Kind &&
                    (decl.Pos == previous.End || (previous.Flags & NodeFlags.Reparsed) != 0))
                {
                    continue;
                }
            }
            // If this is a function or method declaration, get the signature from the @type tag.
            var sig = GetSignatureOfFullSignatureType(decl);
            sig ??= GetSignatureFromDeclaration(decl);
            result.Add(sig);
        }
        return result.ToArray();
    }
}
