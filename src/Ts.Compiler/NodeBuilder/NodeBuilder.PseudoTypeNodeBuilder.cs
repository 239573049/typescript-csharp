// Ported from tsgo internal/checker/pseudotypenodebuilder.go — maps the pseudochecker's syntactic
// pseudo-types into AST type nodes (the isolatedDeclarations-style JS/JSDoc declaration-emit path),
// reporting inference-fallback errors the pseudo-type structure implies, and validates pseudo-types
// against checker types (pseudoTypeEquivalentToType) so stale syntactic inferences fall back to
// checker-based serialization.
//
// plan/03 §4.2: pseudotypenodebuilder.go → PseudoTypeNodeBuilder.cs (B11). Physically hosted with the
// other NodeBuilder partials; declares into Ts.Checker like the rest of the builder.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.PseudoChecker;

namespace Ts.Checker;

public sealed partial class NodeBuilder
{
    // pseudotypenodebuilder.go:15 — pseudoTypeToNodeWithCheckerFallback.
    // Like pseudoTypeToNode, but when the top-level pseudo type is PseudoTypeInferred, reports any error
    // nodes and then serializes from the checker's type. This avoids incorrect type output when
    // PseudoTypeInferred would derive the type from the original declaration expression in an
    // instantiated context.
    private Node? PseudoTypeToNodeWithCheckerFallback(PseudoType t, Type checkerType)
    {
        if (t.Kind == PseudoTypeKind.Inferred)
        {
            if (!ctx!.suppressReportInferenceFallback)
            {
                Node[]? errorNodes = t.AsPseudoTypeInferred().ErrorNodes;
                if (errorNodes is { Length: > 0 })
                {
                    foreach (var n in errorNodes)
                    {
                        ctx.tracker?.ReportInferenceFallback(n);
                    }
                }
                else
                {
                    ctx.tracker?.ReportInferenceFallback(t.AsPseudoTypeInferred().Expression);
                }
            }
            bool oldSuppress = ctx.suppressReportInferenceFallback;
            ctx.suppressReportInferenceFallback = true;
            var result = TypeToTypeNodeImpl(checkerType);
            ctx.suppressReportInferenceFallback = oldSuppress;
            return result;
        }
        else if (t.Kind == PseudoTypeKind.Direct)
        {
            Node existing = t.AsPseudoTypeDirect().TypeNode;
            if (!CanReuseExistingJSTypeNode(existing, checkerType))
            {
                if (!ctx!.suppressReportInferenceFallback)
                {
                    ctx.tracker?.ReportInferenceFallback(existing);
                }
                bool oldSuppress = ctx.suppressReportInferenceFallback;
                ctx.suppressReportInferenceFallback = true;
                var result = TypeToTypeNodeImpl(checkerType);
                ctx.suppressReportInferenceFallback = oldSuppress;
                return result;
            }
        }
        return PseudoTypeToNode(t);
    }

    // pseudotypenodebuilder.go:48 — pseudoTypeToNode.
    // Maps a pseudochecker's pseudotypes into ast nodes and reports any inference fallback errors the
    // pseudotype structure implies.
    private Node? PseudoTypeToNode(PseudoType t)
    {
        Debug.Assert(t != null, "Attempted to serialize nil pseudotype");
        switch (t!.Kind)
        {
            case PseudoTypeKind.Direct:
                return ReuseTypeNode(t.AsPseudoTypeDirect().TypeNode);
            case PseudoTypeKind.Inferred:
            {
                var inferred = t.AsPseudoTypeInferred();
                Node node = inferred.Expression;
                Node[]? errorNodes = inferred.ErrorNodes;
                if (errorNodes is { Length: > 0 })
                {
                    foreach (var n in errorNodes)
                    {
                        ctx!.tracker?.ReportInferenceFallback(n);
                    }
                }
                else if (AstUtilities.IsEntityNameExpression(node) && AstUtilities.IsDeclaration(node.Parent!))
                {
                    ctx!.tracker?.ReportInferenceFallback(node.Parent!);
                }
                else
                {
                    ctx!.tracker?.ReportInferenceFallback(node);
                }
                if (inferred.IsSignatureReturn)
                {
                    return SerializeReturnTypeForSignature(ch.GetSignatureFromDeclaration(node), false);
                }
                // use symbol type from parent declaration to automatically handle expression type widening
                // without duplicating logic
                if (NodeTests.IsReturnStatement(node.Parent!))
                {
                    Node enclosing = Checker.GetContainingFunction(node)!;
                    if (AstUtilities.IsAccessor(enclosing))
                    {
                        return SerializeTypeForDeclarationImpl(enclosing, null, null, false);
                    }
                    return SerializeReturnTypeForSignature(ch.GetSignatureFromDeclaration(enclosing), false);
                }
                if (NodeTests.IsArrowFunction(node.Parent!) && node.Parent!.AsArrowFunction().Body == node)
                {
                    return SerializeReturnTypeForSignature(ch.GetSignatureFromDeclaration(node.Parent!), false);
                }
                if (AstUtilities.IsDeclaration(node.Parent!))
                {
                    return SerializeTypeForDeclarationImpl(node.Parent, null, null, false);
                }
                // This might be effectively unreachable. If it's not, it may need more widening rules to
                // mirror checker behavior for whatever expressions are serialized here
                var ty = ch.GetTypeOfExpression(node);
                return TypeToTypeNodeImpl(ty);
            }
            case PseudoTypeKind.NoResult:
            {
                Node node = t.AsPseudoTypeNoResult().Declaration;
                ctx!.tracker?.ReportInferenceFallback(node);
                if (AstUtilities.IsFunctionLike(node) && !AstUtilities.IsAccessor(node))
                {
                    return SerializeReturnTypeForSignature(ch.GetSignatureFromDeclaration(node), false);
                }
                return SerializeTypeForDeclarationImpl(node, null, null, false);
            }
            case PseudoTypeKind.MaybeConstLocation:
            {
                var d = t.AsPseudoTypeMaybeConstLocation();
                // see checkExpressionWithContextualType for general literal widening rules which need to be
                // emulated here, plus checkTemplateLiteralExpression for template literal widening rules if
                // the pseudochecker ever supports literalized templates
                bool isInConstContext = ch.IsConstContext(d.Node);
                if (!isInConstContext && global::Ts.PseudoChecker.PseudoChecker.IsInConstContext(d.Node))
                {
                    // Only consult the contextual type if the pseudochecker's syntactic check also puts us
                    // in a const context. getContextualType returns post-inference results at node-printing
                    // time which may not have existed during initial checking (e.g. when the contextual type
                    // depends on inference), causing incorrect literal type preservation.
                    var contextualType = ch.GetContextualType(d.Node, ContextFlags.None);
                    var tt = PseudoTypeToType(d.ConstType);
                    if (tt != null && ch.IsLiteralOfContextualType(tt, ch.InstantiateContextualType(contextualType, d.Node, ContextFlags.None)))
                    {
                        isInConstContext = true;
                    }
                }
                if (isInConstContext)
                {
                    return PseudoTypeToNode(d.ConstType);
                }
                else
                {
                    return PseudoTypeToNode(d.RegularType);
                }
            }
            case PseudoTypeKind.Union:
            {
                var res = new List<Node>();
                bool hasElidedType = false;
                bool hasUndefined = false;
                PseudoType[] members = t.AsPseudoTypeUnion().Types;
                void AppendTypeNode(Node node)
                {
                    if (NodeTests.IsUnionTypeNode(node))
                    {
                        foreach (var inner in node.AsUnionTypeNode().Types!.Nodes)
                        {
                            AppendTypeNode(inner);
                        }
                        return;
                    }
                    if (node.Kind == SyntaxKind.UndefinedKeyword)
                    {
                        if (hasUndefined)
                        {
                            return;
                        }
                        hasUndefined = true;
                    }
                    res.Add(node);
                }
                foreach (var m in members)
                {
                    if (!ch.strictNullChecks)
                    {
                        if (m.Kind == PseudoTypeKind.Undefined || m.Kind == PseudoTypeKind.Null)
                        {
                            hasElidedType = true;
                            continue;
                        }
                    }
                    AppendTypeNode(PseudoTypeToNode(m)!);
                }
                if (res.Count == 1)
                {
                    return res[0];
                }
                if (res.Count == 0)
                {
                    if (hasElidedType)
                    {
                        return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
                    }
                    return f.NewKeywordTypeNode(SyntaxKind.NeverKeyword);
                }
                return f.NewUnionTypeNode(f.NewNodeList(res.ToArray()));
            }
            case PseudoTypeKind.Undefined:
                if (!ch.strictNullChecks)
                {
                    return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
                }
                return f.NewKeywordTypeNode(SyntaxKind.UndefinedKeyword);
            case PseudoTypeKind.Null:
                if (!ch.strictNullChecks)
                {
                    return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
                }
                return f.NewLiteralTypeNode(f.NewKeywordExpression(SyntaxKind.NullKeyword));
            case PseudoTypeKind.Any:
                return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
            case PseudoTypeKind.String:
                return f.NewKeywordTypeNode(SyntaxKind.StringKeyword);
            case PseudoTypeKind.Number:
                return f.NewKeywordTypeNode(SyntaxKind.NumberKeyword);
            case PseudoTypeKind.BigInt:
                return f.NewKeywordTypeNode(SyntaxKind.BigIntKeyword);
            case PseudoTypeKind.Boolean:
                return f.NewKeywordTypeNode(SyntaxKind.BooleanKeyword);
            case PseudoTypeKind.False:
                return f.NewLiteralTypeNode(f.NewKeywordExpression(SyntaxKind.FalseKeyword));
            case PseudoTypeKind.True:
                return f.NewLiteralTypeNode(f.NewKeywordExpression(SyntaxKind.TrueKeyword));
            case PseudoTypeKind.SingleCallSignature:
            {
                var d = t.AsPseudoTypeSingleCallSignature();
                var signature = ch.GetSignatureFromDeclaration(d.Signature);
                var expandedParams = ch.GetExpandedParameters(signature, true /*skipUnionExpanding*/)[0];
                var cleanup = EnterNewScope(d.Signature, expandedParams, signature.typeParameters, signature.parameters, signature.mapper);
                try
                {
                    NodeList? typeParams = null;
                    if (d.TypeParameters is { Length: > 0 })
                    {
                        var tps = new List<Node>(d.TypeParameters.Length);
                        foreach (var tp in d.TypeParameters)
                        {
                            tps.Add(ReuseNode(tp.AsNode())!);
                        }
                        typeParams = f.NewNodeList(tps.ToArray());
                    }
                    var params_ = PseudoParametersToNodeList(d.Parameters);
                    var returnType = PseudoTypeToNode(d.ReturnType);
                    return f.NewFunctionTypeNode(typeParams, params_, returnType);
                }
                finally
                {
                    cleanup();
                }
            }
            case PseudoTypeKind.Tuple:
            {
                var res = new List<Node>();
                PseudoType[] elements = t.AsPseudoTypeTuple().Elements;
                foreach (var el in elements)
                {
                    res.Add(PseudoTypeToNode(el)!);
                }
                // pseudo-tuples are implicitly `readonly` since they originate from `as const` contexts
                // but strada *sometimes* fails to add the `readonly` modifier to the generated node.
                var result = f.NewTupleTypeNode(f.NewNodeList(res.ToArray()));
                e.AddEmitFlags(result, global::Ts.Printer.EmitFlags.SingleLine);
                return f.NewTypeOperatorNode(SyntaxKind.ReadonlyKeyword, result);
            }
            case PseudoTypeKind.ObjectLiteral:
            {
                PseudoObjectElement[] elements = t.AsPseudoTypeObjectLiteral().Elements ?? [];
                if (elements.Length == 0)
                {
                    var emptyResult = f.NewTypeLiteralNode(f.NewNodeList([]));
                    e.AddEmitFlags(emptyResult, global::Ts.Printer.EmitFlags.SingleLine);
                    return emptyResult;
                }
                // NOTE: using the checker's `isConstContext` instead of the pseudochecker's `isInConstContext`
                // results in different results here. The checker one is more "correct" but means we'll mark
                // objects in parameter positions contextually typed by const type parameters as readonly -
                // something a true syntactic ID emitter couldn't possibly know (since the signature could
                // be from across files). This can't *really* happen in any cases ID doesn't already error on,
                // though. Just something to keep in mind if the ID checker keeps growing.
                bool isConst = ch.IsConstContext(elements[0].Name.Parent!.Parent!);
                var newElements = new List<Node>(elements.Length);

                // Member types are serialized within an object type literal, so set the
                // corresponding flag to mirror createTypeNodeFromObjectType. This ensures
                // inaccessible `this` references inside the members are reported (TS2527).
                var restoreObjectLiteralFlags = SaveRestoreFlags();
                ctx!.flags |= global::Ts.NodeBuilder.Flags.InObjectTypeLiteral;

                foreach (var el in elements)
                {
                    ModifierList? modifiers = null;
                    if (isConst || (el.Kind == PseudoObjectElementKind.PropertyAssignment && el.AsPseudoPropertyAssignment().Readonly))
                    {
                        modifiers = f.NewModifierList([f.NewModifier(SyntaxKind.ReadonlyKeyword)]);
                    }
                    Action? cleanup = null;
                    if (el.Kind != PseudoObjectElementKind.PropertyAssignment)
                    {
                        var signature = ch.GetSignatureFromDeclaration(el.Signature()!);
                        var expandedParams = ch.GetExpandedParameters(signature, true /*skipUnionExpanding*/)[0];
                        cleanup = EnterNewScope(el.Signature(), expandedParams, signature.typeParameters, signature.parameters, signature.mapper);
                    }
                    Node? newProp = null;
                    switch (el.Kind)
                    {
                        case PseudoObjectElementKind.Method:
                        {
                            var d = el.AsPseudoObjectMethod();
                            NodeList? typeParams = null;
                            if (d.TypeParameters is { Length: > 0 })
                            {
                                var tps = new List<Node>(d.TypeParameters.Length);
                                foreach (var tp in d.TypeParameters)
                                {
                                    tps.Add(ReuseNode(tp.AsNode())!);
                                }
                                typeParams = f.NewNodeList(tps.ToArray());
                            }
                            if (isConst)
                            {
                                newProp = f.NewPropertySignatureDeclaration(
                                    modifiers,
                                    ReuseName(el.Name, false /*isMethod*/),
                                    null,
                                    f.NewFunctionTypeNode(
                                        typeParams,
                                        PseudoParametersToNodeList(d.Parameters),
                                        PseudoTypeToNode(d.ReturnType)),
                                    null);
                                break;
                            }
                            newProp = f.NewMethodSignatureDeclaration(
                                modifiers,
                                ReuseName(el.Name, true /*isMethod*/),
                                null,
                                typeParams,
                                PseudoParametersToNodeList(d.Parameters),
                                PseudoTypeToNode(d.ReturnType));
                            break;
                        }
                        case PseudoObjectElementKind.PropertyAssignment:
                        {
                            var d = el.AsPseudoPropertyAssignment();
                            newProp = f.NewPropertySignatureDeclaration(
                                modifiers,
                                ReuseName(el.Name, false /*isMethod*/),
                                null,
                                PseudoTypeToNode(d.Type),
                                null);
                            break;
                        }
                        case PseudoObjectElementKind.SetAccessor:
                        {
                            var d = el.AsPseudoSetAccessor();
                            newProp = f.NewSetAccessorDeclaration(
                                null,
                                ReuseName(el.Name, false /*isMethod*/),
                                null,
                                f.NewNodeList([PseudoParameterToNode(d.Parameter)]),
                                null,
                                null,
                                null);
                            break;
                        }
                        case PseudoObjectElementKind.GetAccessor:
                        {
                            var d = el.AsPseudoGetAccessor();
                            newProp = f.NewGetAccessorDeclaration(
                                null,
                                ReuseName(el.Name, false /*isMethod*/),
                                null,
                                null,
                                PseudoTypeToNode(d.Type),
                                null,
                                null);
                            break;
                        }
                    }
                    if (ctx.enclosingFile == AstUtilities.GetSourceFileOfNode(el.Name))
                    {
                        e.SetCommentRange(newProp!, el.Name.Parent!.Loc);
                    }
                    newElements.Add(newProp!);
                    cleanup?.Invoke();
                }
                restoreObjectLiteralFlags();
                var objResult = f.NewTypeLiteralNode(f.NewNodeList(newElements.ToArray()));
                if ((ctx.flags & global::Ts.NodeBuilder.Flags.MultilineObjectLiterals) == 0)
                {
                    e.AddEmitFlags(objResult, global::Ts.Printer.EmitFlags.SingleLine);
                }
                return objResult;
            }
            case PseudoTypeKind.StringLiteral:
            case PseudoTypeKind.NumericLiteral:
            case PseudoTypeKind.BigIntLiteral:
            {
                Node source = t.AsPseudoTypeLiteral().Node;
                return f.NewLiteralTypeNode(ReuseNode(source));
            }
            default:
                Debug.AssertNever(t.Kind, "Unhandled pseudotype kind in pseudotype node construction");
                return null;
        }
    }

    // pseudotypenodebuilder.go:327 — pseudoParametersToNodeList.
    private NodeList PseudoParametersToNodeList(PseudoParameter[]? params_)
    {
        var res = new List<Node>(params_?.Length ?? 0);
        foreach (var p in params_ ?? [])
        {
            res.Add(PseudoParameterToNode(p));
        }
        return f.NewNodeList(res.ToArray());
    }

    // pseudotypenodebuilder.go:335 — pseudoParameterToNode.
    private Node PseudoParameterToNode(PseudoParameter p)
    {
        Node? dotDotDot = null;
        Node? questionMark = null;
        if (p.Rest)
        {
            dotDotDot = f.NewToken(SyntaxKind.DotDotDotToken);
        }
        if (p.Optional)
        {
            questionMark = f.NewToken(SyntaxKind.QuestionToken);
        }
        var parameter = f.NewParameterDeclaration(
            null,
            dotDotDot,
            // matches strada behavior of always reserializing param names from scratch
            ParameterToParameterDeclarationName(p.Name.Parent!.Symbol()!, p.Name.Parent),
            questionMark,
            PseudoTypeToNode(p.Type),
            null);
        Node original = p.Name.Parent!;
        if (NodeTests.IsParameterDeclaration(original))
        {
            SetCommentRange(parameter, original);
        }
        return parameter;
    }

    // pseudotypenodebuilder.go:362 — pseudoTypeEquivalentToType.
    // see `typeNodeIsEquivalentToType` in strada, but applied more broadly here, so is setup to handle
    // more equivalences - strada only used it via the `canReuseTypeNodeAnnotation` host hook and not the
    // `canReuseTypeNode` hook, which meant locations using the later were reliant on over-invalidation
    // by the ID inference engine to not emit incorrect types.
    private bool PseudoTypeEquivalentToType(PseudoType t, Type? type_, bool isOptionalAnnotated, bool reportErrors)
    {
        // if type_ resolves to an error, we charitably assume equality, since we might be in a
        // single-file checking mode
        if (type_ != null && ch.IsErrorType(type_))
        {
            return true;
        }
        // If we can easily operate on just types, we should
        var typeFromPseudo = PseudoTypeToType(t); // note: cannot convert complex types like objects, which must be validated separately
        if (typeFromPseudo == type_)
        {
            return true;
        }
        var undefinedStripped = type_;
        if (isOptionalAnnotated)
        {
            undefinedStripped = ch.GetTypeWithFacts(type_!, TypeFacts.NEUndefined);
        }
        if (typeFromPseudo != null && type_ != null)
        {
            if (isOptionalAnnotated)
            {
                if (undefinedStripped == typeFromPseudo)
                {
                    return true;
                }
                if ((typeFromPseudo.flags & TypeFlags.Union) != 0 && (undefinedStripped!.flags & TypeFlags.Union) != 0)
                {
                    // does union comparison in general, since the unions may not be `==` identical due to
                    // aliasing and the like
                    if (ch.CompareTypesIdentical(typeFromPseudo, undefinedStripped) == Ternary.True)
                    {
                        return true;
                    }
                }
            }
            // handles freshness mismatches (e.g., fresh true vs regular true in as const)
            if (ch.GetRegularTypeOfLiteralType(typeFromPseudo) == ch.GetRegularTypeOfLiteralType(type_))
            {
                return true;
            }
            if ((typeFromPseudo.flags & TypeFlags.Union) != 0 && (type_.flags & TypeFlags.Union) != 0)
            {
                // handles union comparison in general, since unions may not be `==` identical due to aliasing
                if (ch.CompareTypesIdentical(typeFromPseudo, type_) == Ternary.True)
                {
                    return true;
                }
            }
        }
        // otherwise, fallback to actual pseudo/type cross-comparisons
        switch (t.Kind)
        {
            case PseudoTypeKind.Inferred:
            {
                // PseudoTypeInferred with error nodes identifies specific problematic children.
                // Report fine-grained errors on them, then return false so the parent falls back
                // to checker-based serialization (avoiding issues like reusing raw JSON string
                // literal property names from the pseudochecker's AST).
                Node[]? errorNodes = t.AsPseudoTypeInferred().ErrorNodes;
                if (errorNodes is { Length: > 0 })
                {
                    if (reportErrors)
                    {
                        foreach (var n in errorNodes)
                        {
                            ctx!.tracker?.ReportInferenceFallback(n);
                        }
                    }
                    return false;
                }
                if (reportErrors)
                {
                    ctx!.tracker?.ReportInferenceFallback(t.AsPseudoTypeInferred().Expression);
                }
                return false;
            }
            case PseudoTypeKind.ObjectLiteral:
            {
                var pt = t.AsPseudoTypeObjectLiteral();
                if (type_ == null)
                {
                    return false;
                }
                Symbol[] targetProps = ch.GetPropertiesOfType(undefinedStripped!);
                // Count total declarations across all target prop symbols to handle getter/setter pairs,
                // which are two elements in pt.Elements but only one symbol in targetProps.
                int targetDeclCount = 0;
                foreach (var prop in targetProps)
                {
                    targetDeclCount += prop.Declarations?.Length ?? 0;
                }
                if ((pt.Elements?.Length ?? 0) != targetDeclCount)
                {
                    return false;
                }
                foreach (var el in pt.Elements ?? [])
                {
                    Symbol? targetProp = null;
                    Symbol? elemSymbol = el.Name.Parent!.Symbol();
                    if (elemSymbol != null)
                    {
                        targetProp = ch.GetPropertyOfType(undefinedStripped!, elemSymbol.Name);
                    }
                    if (targetProp == null)
                    {
                        // Name lookup failed or returned no result; search target properties
                        // for one whose declaration name node matches the one we have
                        foreach (var prop in targetProps)
                        {
                            if (prop.ValueDeclaration != null && prop.ValueDeclaration.Name() == el.Name)
                            {
                                targetProp = prop;
                                break;
                            }
                        }
                        if (targetProp == null)
                        {
                            if (reportErrors)
                            {
                                ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                            }
                            return false;
                        }
                    }
                    bool targetIsOptional = (targetProp.Flags & SymbolFlags.Optional) != 0;
                    if (el.Optional != targetIsOptional)
                    {
                        if (reportErrors)
                        {
                            ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                        }
                        return false;
                    }
                    var propType = ch.GetTypeOfSymbol(targetProp);
                    propType = ch.RemoveMissingType(propType, targetIsOptional);
                    switch (el.Kind)
                    {
                        case PseudoObjectElementKind.PropertyAssignment:
                        {
                            var d = el.AsPseudoPropertyAssignment();
                            if (!PseudoTypeEquivalentToType(d.Type, propType, el.Optional, false))
                            {
                                if (reportErrors)
                                {
                                    if (d.Type.Kind == PseudoTypeKind.Inferred && d.Type.AsPseudoTypeInferred().ErrorNodes is { Length: > 0 })
                                    {
                                        // Re-report the fine-grained error nodes; the recursive call used
                                        // reportErrors=false
                                        foreach (var n in d.Type.AsPseudoTypeInferred().ErrorNodes!)
                                        {
                                            ctx!.tracker?.ReportInferenceFallback(n);
                                        }
                                    }
                                    else if (!IsStructuralPseudoType(d.Type))
                                    {
                                        ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                                    }
                                }
                                return false;
                            }
                            break;
                        }
                        case PseudoObjectElementKind.Method:
                        {
                            var d = el.AsPseudoObjectMethod();
                            var targetSig = ch.GetSingleCallSignature(propType);
                            if (targetSig == null)
                            {
                                // Target property type doesn't have a single call signature; can't validate
                                continue;
                            }
                            bool paramEq = PseudoParametersEquivalentToParameters(d.Parameters, targetSig, reportErrors, el.Name.Parent!);
                            if (!paramEq)
                            {
                                return false;
                            }
                            var targetPredicate = ch.GetTypePredicateOfSignature(targetSig);
                            if (targetPredicate != null)
                            {
                                if (!PseudoReturnTypeMatchesPredicate(d.ReturnType, targetPredicate))
                                {
                                    if (reportErrors)
                                    {
                                        ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                                    }
                                    return false;
                                }
                            }
                            else if (!PseudoTypeEquivalentToType(d.ReturnType, ch.GetReturnTypeOfSignature(targetSig), false, false))
                            {
                                if (reportErrors)
                                {
                                    ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                                }
                                return false;
                            }
                            break;
                        }
                        case PseudoObjectElementKind.GetAccessor:
                        {
                            var d = el.AsPseudoGetAccessor();
                            if (!PseudoTypeEquivalentToType(d.Type, propType, false, false))
                            {
                                if (reportErrors)
                                {
                                    ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                                }
                                return false;
                            }
                            break;
                        }
                        case PseudoObjectElementKind.SetAccessor:
                        {
                            var d = el.AsPseudoSetAccessor();
                            var writeType = ch.GetWriteTypeOfSymbol(targetProp);
                            if (!PseudoTypeEquivalentToType(d.Parameter.Type, writeType, false, false))
                            {
                                if (reportErrors)
                                {
                                    ctx!.tracker?.ReportInferenceFallback(el.Name.Parent!);
                                }
                                return false;
                            }
                            break;
                        }
                    }
                }
                return true;
            }
            case PseudoTypeKind.Tuple:
            {
                var pt = t.AsPseudoTypeTuple();
                if (undefinedStripped == null || !Checker.IsTupleType(undefinedStripped))
                {
                    return false;
                }
                var tupleTarget = undefinedStripped.TargetTupleType();
                // Pseudo-tuples come from `as const` array literals, so they only ever have required
                // elements. If the target tuple has optional, rest, or variadic elements, the structures
                // can't match.
                if ((tupleTarget.combinedFlags & ElementFlags.NonRequired) != 0)
                {
                    return false;
                }
                var elementTypes = ch.GetTypeArguments(undefinedStripped);
                if (pt.Elements.Length != elementTypes.Length)
                {
                    return false;
                }
                for (int i = 0; i < pt.Elements.Length; i++)
                {
                    if (!PseudoTypeEquivalentToType(pt.Elements[i], elementTypes[i], false, reportErrors))
                    {
                        return false;
                    }
                }
                return true;
            }
            case PseudoTypeKind.SingleCallSignature:
            {
                var targetSig = ch.GetSingleCallSignature(undefinedStripped!);
                if (targetSig == null)
                {
                    return false;
                }
                var pt = t.AsPseudoTypeSingleCallSignature();
                if ((targetSig.typeParameters?.Length ?? 0) != (pt.TypeParameters?.Length ?? 0))
                {
                    if (reportErrors)
                    {
                        ctx!.tracker?.ReportInferenceFallback(pt.Signature);
                    }
                    return false;
                }
                bool paramEq = PseudoParametersEquivalentToParameters(pt.Parameters, targetSig, reportErrors, pt.Signature);
                if (!paramEq)
                {
                    return false;
                }
                var targetPredicate = ch.GetTypePredicateOfSignature(targetSig);
                if (targetPredicate != null)
                {
                    if (!PseudoReturnTypeMatchesPredicate(pt.ReturnType, targetPredicate))
                    {
                        if (reportErrors)
                        {
                            ctx!.tracker?.ReportInferenceFallback(pt.Signature);
                        }
                        return false;
                    }
                }
                else if (!PseudoTypeEquivalentToType(pt.ReturnType, ch.GetReturnTypeOfSignature(targetSig), false, reportErrors))
                {
                    // error reported within the return type
                    return false;
                }
                return true;
            }
            case PseudoTypeKind.NoResult:
                if (reportErrors)
                {
                    ctx!.tracker?.ReportInferenceFallback(t.AsPseudoTypeNoResult().Declaration);
                }
                return false;
            default:
                return false;
        }
    }

    // pseudotypenodebuilder.go:585 — pseudoParametersEquivalentToParameters.
    private bool PseudoParametersEquivalentToParameters(PseudoParameter[]? params_, Signature targetSig, bool reportErrors, Node nonParamErrorLocation)
    {
        var paramList = params_ ?? [];
        if (targetSig.thisParameter != null && paramList.Length == 0)
        {
            if (reportErrors)
            {
                ctx!.tracker?.ReportInferenceFallback(nonParamErrorLocation); // missing `this` param
            }
            return false;
        }
        else if (targetSig.thisParameter != null && JSDocReparseAstGaps.IsThisIdentifier(paramList[0].Name))
        {
            var targetParam = targetSig.thisParameter;
            var paramType = ch.GetTypeOfParameter(targetParam!);
            if (!PseudoTypeEquivalentToType(paramList[0].Type, paramType, paramList[0].Optional, false))
            {
                if (reportErrors)
                {
                    ctx!.tracker?.ReportInferenceFallback(paramList[0].Name.Parent!);
                }
                return false;
            }
            paramList = paramList[1..];
        }
        else if (targetSig.thisParameter != null)
        {
            if (reportErrors)
            {
                ctx!.tracker?.ReportInferenceFallback(nonParamErrorLocation);
            }
            return false;
        }
        if ((targetSig.parameters?.Length ?? 0) != paramList.Length)
        {
            if (reportErrors)
            {
                ctx!.tracker?.ReportInferenceFallback(nonParamErrorLocation);
            }
            return false; // TODO: spread tuple params may mess with this check
        }
        for (int i = 0; i < paramList.Length; i++)
        {
            var p = paramList[i];
            var targetParam = targetSig.parameters![i];
            if (p.Optional != ch.IsOptionalParameter(targetParam.ValueDeclaration!))
            {
                if (reportErrors)
                {
                    ctx!.tracker?.ReportInferenceFallback(p.Name.Parent!);
                }
                return false;
            }
            var paramType = ch.GetTypeOfParameter(targetParam);
            if (!PseudoTypeEquivalentToType(p.Type, paramType, p.Optional, false))
            {
                if (reportErrors)
                {
                    ctx!.tracker?.ReportInferenceFallback(p.Name.Parent!);
                }
                return false;
            }
        }
        return true;
    }

    // pseudotypenodebuilder.go:632 — isStructuralPseudoType.
    private static bool IsStructuralPseudoType(PseudoType t)
    {
        switch (t.Kind)
        {
            case PseudoTypeKind.ObjectLiteral:
            case PseudoTypeKind.Tuple:
            case PseudoTypeKind.SingleCallSignature:
                return true;
            case PseudoTypeKind.MaybeConstLocation:
            {
                var d = t.AsPseudoTypeMaybeConstLocation();
                return IsStructuralPseudoType(d.ConstType) || IsStructuralPseudoType(d.RegularType);
            }
        }
        return false;
    }

    // pseudotypenodebuilder.go:645 — pseudoReturnTypeMatchesPredicate.
    // Checks if a pseudo return type (which should be a Direct type wrapping a TypePredicate) matches
    // the given type predicate from the checker.
    private bool PseudoReturnTypeMatchesPredicate(PseudoType rt, TypePredicate predicate)
    {
        if (rt.Kind != PseudoTypeKind.Direct)
        {
            return false;
        }
        Node node = rt.AsPseudoTypeDirect().TypeNode;
        if (!NodeTests.IsTypePredicateNode(node))
        {
            return false;
        }
        var tp = node.AsTypePredicateNode();
        // Check asserts modifier matches
        bool isAsserts = tp.AssertsModifier != null;
        bool predicateIsAsserts = predicate.kind == TypePredicateKind.AssertsThis || predicate.kind == TypePredicateKind.AssertsIdentifier;
        if (isAsserts != predicateIsAsserts)
        {
            return false;
        }
        // Check this vs identifier matches
        bool isThis = NodeTests.IsThisTypeNode(tp.ParameterName!);
        bool predicateIsThis = predicate.kind == TypePredicateKind.This || predicate.kind == TypePredicateKind.AssertsThis;
        if (isThis != predicateIsThis)
        {
            return false;
        }
        // For identifier predicates, check parameter name matches
        if (!isThis)
        {
            if (tp.ParameterName!.Text() != predicate.parameterName)
            {
                return false;
            }
        }
        // Check the narrowed type, if any
        if (predicate.t != null)
        {
            if (tp.Type == null)
            {
                return false;
            }
            var predicateTypeFromNode = ch.GetTypeFromTypeNode(tp.Type);
            if (predicateTypeFromNode != predicate.t)
            {
                if (ch.CompareTypesIdentical(predicateTypeFromNode, predicate.t) != Ternary.True)
                {
                    return false;
                }
            }
        }
        else if (tp.Type != null)
        {
            return false;
        }
        return true;
    }

    // pseudotypenodebuilder.go:689 — pseudoTypeToType.
    private Type? PseudoTypeToType(PseudoType t)
    {
        // !!! TODO: only literal types currently mapped because this is only used to determine if literal
        // contextual typing need apply to the pseudotype. If this is used more broadly, the implementation
        // needs to be filled out more to handle the structural pseudotypes - signatures, objects, tuples, etc
        Debug.Assert(t != null, "Attempted to realize nil pseudotype");
        switch (t!.Kind)
        {
            case PseudoTypeKind.Direct:
                return ch.GetTypeFromTypeNode(t.AsPseudoTypeDirect().TypeNode);
            case PseudoTypeKind.Inferred:
            {
                Node node = t.AsPseudoTypeInferred().Expression;
                if (t.AsPseudoTypeInferred().IsSignatureReturn)
                {
                    return ch.GetReturnTypeOfSignature(ch.GetSignatureFromDeclaration(node));
                }
                var ty = ch.GetWidenedType(ch.GetRegularTypeOfExpression(node));
                return ty;
            }
            case PseudoTypeKind.NoResult:
                return null; // TODO: extract type selection logic from `serializeTypeForDeclaration`, not needed for current usecases but needed if completeness becomes required
            case PseudoTypeKind.MaybeConstLocation:
            {
                var d = t.AsPseudoTypeMaybeConstLocation();
                if (ch.IsConstContext(d.Node))
                {
                    return PseudoTypeToType(d.ConstType);
                }
                return PseudoTypeToType(d.RegularType);
            }
            case PseudoTypeKind.Union:
            {
                var res = new List<Type>();
                bool hasElidedType = false;
                PseudoType[] members = t.AsPseudoTypeUnion().Types;
                foreach (var m in members)
                {
                    if (!ch.strictNullChecks)
                    {
                        if (m.Kind == PseudoTypeKind.Undefined || m.Kind == PseudoTypeKind.Null)
                        {
                            hasElidedType = true;
                            continue;
                        }
                    }
                    var tt = PseudoTypeToType(m);
                    if (tt == null)
                    {
                        return null; // propagate failure
                    }
                    res.Add(tt);
                }
                if (res.Count == 1)
                {
                    return res[0];
                }
                if (res.Count == 0)
                {
                    if (hasElidedType)
                    {
                        return ch.anyType;
                    }
                    return ch.neverType;
                }
                return ch.GetUnionType(res.ToArray());
            }
            case PseudoTypeKind.Undefined:
                return ch.undefinedWideningType;
            case PseudoTypeKind.Null:
                return ch.nullWideningType;
            case PseudoTypeKind.Any:
                return ch.anyType;
            case PseudoTypeKind.String:
                return ch.stringType;
            case PseudoTypeKind.Number:
                return ch.numberType;
            case PseudoTypeKind.BigInt:
                return ch.bigintType;
            case PseudoTypeKind.Boolean:
                return ch.booleanType;
            case PseudoTypeKind.False:
                return ch.falseType;
            case PseudoTypeKind.True:
                return ch.trueType;
            case PseudoTypeKind.StringLiteral:
            case PseudoTypeKind.NumericLiteral:
            case PseudoTypeKind.BigIntLiteral:
            {
                Node source = t.AsPseudoTypeLiteral().Node;
                return ch.GetRegularTypeOfExpression(source); // big shortcut, uses cached expression types where possible
            }
            case PseudoTypeKind.ObjectLiteral:
            case PseudoTypeKind.SingleCallSignature:
            case PseudoTypeKind.Tuple:
                return null; // no simple mapping to a type, since these are structural types
            default:
                Debug.Fail("Unhandled pseudochecker.PseudoTypeKind in pseudoTypeToType");
                return null;
        }
    }
}
