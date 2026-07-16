// Ported from tsgo internal/checker/nodebuilderimpl.go — the object-type serialization slice of the
// request-scoped NodeBuilder: the path that turns an object type ({ foo: string; } / anonymous / mapped /
// reference / indexed) into a TypeNode. This backs both the error-message serializer (TypeToStringEx in
// Checker.TypeToString.cs) and .d.ts declaration emit.
//
// PLACEMENT (§1.5): NodeBuilderImpl lives in `package checker` in tsgo, so its faithful C# home is
// namespace Ts.Checker; this partial extends the same NodeBuilder class as NodeBuilder/NodeBuilderImpl.cs.
//
// SCOPE (this wave — anonymous object-type serialization):
//   FULLY PORTED (the simple object-literal path the common assignability messages need):
//     * typeToTypeNode              (nodebuilderimpl.go:3176 — the full dispatch, on the builder with ctx)
//     * createAnonymousTypeNode / createAnonymousTypeNodeEx (nodebuilderimpl.go:2770/2785 — object-literal
//       branch, i.e. symbol-less types and the plain-object-literal `visitAndTransformType` path)
//     * shouldEmitTypeOfSymbol / shouldWriteTypeOfFunctionSymbol (nodebuilderimpl.go:2774/2733)
//     * createTypeNodeFromObjectType (nodebuilderimpl.go:2663)
//     * createTypeNodesFromResolvedType (nodebuilderimpl.go:2600 — property + index-info slice)
//     * addPropertyToElementList     (nodebuilderimpl.go:2459 — the property-signature slice)
//     * getPropertyNameNodeForSymbol / *FromNameType / createPropertyNameNodeForIdentifierOrLiteral /
//       classifyPropertyName / isStringNamed / isSingleQuotedStringNamed (name synthesis)
//     * visitAndTransformType        (nodebuilderimpl.go:3089 — circular-reference / symbol-depth guard;
//       the serialized-types cache is deferred, see PORT-NOTE there)
//     * shouldExpandType / isExpandableType / isTypeOnStack / checkTruncationLength /
//       createElidedInformationPlaceholder / mapToTypeNodes (truncation-aware) — the guards the object
//       path threads. maxExpansionDepth is fixed at -1 (no VerbosityContext), so the hover-expansion
//       arms are unreachable and short-circuit faithfully.
//     * typeReferenceToTypeNode      (nodebuilderimpl.go:2947 — Array / ReadonlyArray slice)
//     * getResolvedTypeWithoutAbstractConstructSignatures (nodebuilderimpl.go:552)
//     * getParentSymbolOfTypeParameter (nodebuilderimpl.go:2932)
//   DEFERRED (faithful signatures; documented NotImplementedException — deeper machinery owned by later
//   waves and not exercised by the anonymous-object assignability messages):
//     * signatureToSignatureDeclarationHelper (call/construct/method/getter/setter signature synthesis —
//       needs enterSignatureScope + symbolToParameterDeclaration + serializeReturnTypeForSignature +
//       typeParameterToDeclaration). A `{ foo: string; }`-style object literal has no signatures, so the
//       property path never reaches it.
//     * createMappedTypeNodeFromType (mapped-type `{ [K in T]: ... }` synthesis)
//     * conditionalTypeToTypeNode, template-literal / string-mapping / indexed-access deep arms
//     * the generic tuple / outer-type-parameter arms of typeReferenceToTypeNode
//     * indexInfoToObjectComputedNamesOrSignatureDeclaration computed-name reuse (falls through to the
//       already-ported indexInfoToIndexSignatureDeclarationHelper)
//     * trackComputedName / late-bound-name reporting (needs resolveName + tracker plumbing)
//     * getPropertyNameNodeForSymbolFromNameType unique-symbol arm (symbolToExpression on nameType symbol)

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

using PrinterNs = global::Ts.Printer;

namespace Ts.Checker;

public sealed partial class NodeBuilder
{
    // nodebuilderimpl.go:2273
    private const int MaxReverseMappedNestingInspectionDepth = 3;

    // The context-free leaf arms of typeToTypeNode (nodebuilderimpl.go:3202-3319): every arm up to (but
    // not including) the this-type arm — intrinsics, literals, enum-like — plus `boolean` when unaliased
    // (checked separately because Go also requires t.alias == nil). These flags are disjoint from the
    // reference / type-parameter / union / object arms below, so dispatching them via one mask preserves
    // the Go arm order. (UniqueESSymbol has a ctx-reading arm below in TypeToTypeNodeCore; it
    // falls through to the leaf fallback exactly as before.)
    private const TypeFlags LeafTypeKinds =
        TypeFlags.Any | TypeFlags.Unknown | TypeFlags.String | TypeFlags.Number | TypeFlags.BigInt
        | TypeFlags.EnumLike | TypeFlags.StringLiteral | TypeFlags.NumberLiteral | TypeFlags.BigIntLiteral
        | TypeFlags.BooleanLiteral | TypeFlags.Void | TypeFlags.Undefined | TypeFlags.Null | TypeFlags.Never
        | TypeFlags.ESSymbol | TypeFlags.NonPrimitive;

    // nodebuilderimpl.go:3176 — typeToTypeNode. The single authoritative Type -> TypeNode dispatch on the
    // request-scoped builder with ctx, ported in Go arm order. The context-free leaf arms (intrinsic /
    // literal / enum-like) delegate to the landed core in Checker.TypeToString.cs via TypeToTypeNodeLeaf.
    private Node TypeToTypeNodeCore(Type t)
    {
        // PORT-NOTE: the maxExpansionDepth typeStack push/pop (nodebuilderimpl.go:3178-3183) is a no-op here
        // because maxExpansionDepth is fixed at -1 (no VerbosityContext); kept absent to avoid dead state.

        var inTypeAlias = ctx!.flags & global::Ts.NodeBuilder.Flags.InTypeAlias;
        ctx.flags &= ~global::Ts.NodeBuilder.Flags.InTypeAlias;

        // nodebuilderimpl.go:3198 — reduce unless NoTypeReduction.
        if ((ctx.flags & global::Ts.NodeBuilder.Flags.NoTypeReduction) == 0)
        {
            t = ch.GetReducedType(t);
        }

        // nodebuilderimpl.go:3202-3205 — Any with an alias renders the alias reference unconditionally
        // (the unresolved-name errorType carries the written name as its alias, e.g. `Iterator<T, ...>`
        // keeps `T` even though `T` failed to resolve).
        if ((t.flags & TypeFlags.Any) != 0 && t.alias != null)
        {
            return TypeAliasToTypeReferenceNode(t.alias);
        }

        // nodebuilderimpl.go:3202-3319 — intrinsic / literal / enum-like leaf arms.
        if ((t.flags & LeafTypeKinds) != 0 || ((t.flags & TypeFlags.Boolean) != 0 && t.alias == null))
        {
            return TypeToTypeNodeLeaf(t);
        }

        // nodebuilderimpl.go:3321 — this-type.
        if (Checker.IsThisTypeParameter(t))
        {
            if ((ctx.flags & global::Ts.NodeBuilder.Flags.InObjectTypeLiteral) != 0)
            {
                if (!ctx.encounteredError && (ctx.flags & global::Ts.NodeBuilder.Flags.AllowThisInObjectLiteral) == 0)
                {
                    ctx.encounteredError = true;
                }
                ctx.tracker?.ReportInaccessibleThisError();
            }
            ctx.approximateLength += 4;
            return f.NewThisTypeNode();
        }

        // nodebuilderimpl.go:3332 — type-alias reference arm: render a type by its alias name when the alias
        // symbol is accessible (gated on inTypeAlias == 0). PORT-NOTE: shouldExpandType short-circuits false
        // for error-message / .d.ts callers (maxExpansionDepth < 0), so the alias is always used; and
        // symbolToTypeNode's full symbol chain is approximated by the reduced SymbolToTypeReferenceNodeLocal
        // (correct for accessible top-level aliases — the common case).
        if (inTypeAlias == global::Ts.NodeBuilder.Flags.None && t.alias != null &&
            ((ctx.flags & global::Ts.NodeBuilder.Flags.UseAliasDefinedOutsideCurrentScope) != 0 ||
             ch.IsTypeSymbolAccessible(t.alias.Symbol()!, ctx.enclosingDeclaration)))
        {
            var aliasSym = t.alias.Symbol()!;
            var aliasTypeArgumentNodes = t.alias.TypeArguments() is { } aliasTa ? MapToTypeNodesLocal(aliasTa, false /*isBareList*/) : null;
            if (Checker.IsReservedMemberName(aliasSym.Name) && (aliasSym.Flags & SymbolFlags.Class) == 0)
            {
                return f.NewTypeReferenceNode(f.NewIdentifier(""), aliasTypeArgumentNodes);
            }
            if (aliasTypeArgumentNodes != null && aliasTypeArgumentNodes.Nodes.Length == 1 && aliasSym == ch.globalArrayType?.symbol)
            {
                return f.NewArrayTypeNode(aliasTypeArgumentNodes.Nodes[0]);
            }
            return SymbolToTypeReferenceNodeLocal(aliasSym, aliasTypeArgumentNodes);
        }

        var objectFlags = t.objectFlags;

        // nodebuilderimpl.go:3352 — generic type reference (Array / ReadonlyArray / tuple / interface).
        if ((objectFlags & ObjectFlags.Reference) != 0)
        {
            global::Ts.Core.Debug.Assert((t.flags & TypeFlags.Object) != 0);
            // PORT-NOTE: the shouldExpandType hover arm short-circuits (maxExpansionDepth < 0).
            if (t.AsTypeReference()!.node is not null)
            {
                return VisitAndTransformType(t, static (b, ty) => b.TypeReferenceToTypeNode(ty));
            }
            return TypeReferenceToTypeNode(t);
        }

        // nodebuilderimpl.go:3366 — type parameter / class / interface.
        if ((t.flags & TypeFlags.TypeParameter) != 0 || (objectFlags & ObjectFlags.ClassOrInterface) != 0)
        {
            // PORT-NOTE: the shouldExpandType structural-expansion arm short-circuits (maxExpansionDepth < 0).
            if ((t.flags & TypeFlags.TypeParameter) != 0 && ctx.inferTypeParameters is not null
                && System.Array.IndexOf(ctx.inferTypeParameters, t) >= 0)
            {
                ctx.approximateLength += AstUtilities.SymbolName(t.symbol!).Length + 6;
                Node? constraintNode = null;
                var constraint = ch.GetConstraintOfTypeParameter(t);
                if (constraint is not null)
                {
                    // If the infer type has a constraint that is not the same as the constraint
                    // we would have normally inferred based on context, we emit the constraint
                    // using `infer T extends ?`. We omit inferred constraints from type references
                    // as they may be elided.
                    var inferredConstraint = ch.GetInferredTypeParameterConstraint(t, true /*omitTypeReferences*/);
                    if (!(inferredConstraint is not null && ch.IsTypeIdenticalTo(constraint, inferredConstraint)))
                    {
                        ctx.approximateLength += 9;
                        constraintNode = TypeToTypeNodeCore(constraint);
                    }
                }
                return f.NewInferTypeNode(TypeParameterToDeclarationWithConstraint(t, constraintNode));
            }
            if ((ctx.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0
                && (t.flags & TypeFlags.TypeParameter) != 0)
            {
                var shadowedName = TypeParameterToName(t);
                ctx.approximateLength += shadowedName.Text().Length;
                return f.NewTypeReferenceNode(NewIdentifier(shadowedName.Text(), t.symbol), null /*typeArguments*/);
            }
            // Ignore constraint/default when creating a usage (as opposed to declaration) of a type parameter.
            if (t.symbol is not null)
            {
                return SymbolToTypeReferenceNodeLocal(t.symbol, null /*typeArguments*/);
            }
            string name;
            if ((t == ch.markerSuperTypeForCheck || t == ch.markerSubTypeForCheck)
                && ch.varianceTypeParameter is not null && ch.varianceTypeParameter.symbol is not null)
            {
                name = (t == ch.markerSubTypeForCheck ? "sub-" : "super-") + AstUtilities.SymbolName(ch.varianceTypeParameter.symbol);
            }
            else
            {
                name = "?";
            }
            return f.NewTypeReferenceNode(NewIdentifier(name, null /*symbol*/), null /*typeArguments*/);
        }

        // nodebuilderimpl.go:3409 — union origin unwrap + union/intersection constituent mapping.
        if ((t.flags & TypeFlags.Union) != 0 && t.AsUnionType().origin is not null)
        {
            t = t.AsUnionType().origin!;
        }
        if ((t.flags & (TypeFlags.Union | TypeFlags.Intersection)) != 0)
        {
            Type[] types;
            if ((t.flags & TypeFlags.Union) != 0)
            {
                // expandingEnum is only ever set by the hover-expansion enum arm (maxExpansionDepth < 0 here).
                types = ch.FormatUnionTypes(t.AsUnionType().types, false /*expandingEnum*/);
            }
            else
            {
                types = t.AsIntersectionType().types;
            }
            if (types.Length == 1)
            {
                return TypeToTypeNodeCore(types[0]);
            }
            var typeNodes = MapToTypeNodesLocal(types, true /*isBareList*/);
            if (typeNodes is not null && typeNodes.Nodes.Length > 0)
            {
                if ((t.flags & TypeFlags.Union) != 0)
                {
                    return f.NewUnionTypeNode(typeNodes);
                }
                return f.NewIntersectionTypeNode(typeNodes);
            }
            if (!ctx.encounteredError && (ctx.flags & global::Ts.NodeBuilder.Flags.AllowEmptyUnionOrIntersection) == 0)
            {
                ctx.encounteredError = true;
            }
            // PORT-NOTE: Go returns nil here (TODO GH#18217); encounteredError makes ExitContext drop the
            // result, so a neutral `any` stand-in keeps the C# signature non-nullable with identical output.
            return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }

        // nodebuilderimpl.go:3437 — anonymous / mapped object type literal.
        if ((objectFlags & (ObjectFlags.Anonymous | ObjectFlags.Mapped)) != 0)
        {
            global::Ts.Core.Debug.Assert((t.flags & TypeFlags.Object) != 0);
            return CreateAnonymousTypeNode(t);
        }

        // nodebuilderimpl.go:3442 — index (keyof) type.
        if ((t.flags & TypeFlags.Index) != 0)
        {
            var indexedType = t.Target();
            ctx!.approximateLength += 6;
            var indexTypeNode = TypeToTypeNodeCore(indexedType);
            return f.NewTypeOperatorNode(SyntaxKind.KeyOfKeyword, indexTypeNode);
        }
        // nodebuilderimpl.go:3448 — template literal type.
        if ((t.flags & TypeFlags.TemplateLiteral) != 0)
        {
            var texts = t.AsTemplateLiteralType().texts;
            var types = t.AsTemplateLiteralType().types;
            var templateHead = f.NewTemplateHead(texts[0], "", TokenFlags.None);
            e.AddEmitFlags(templateHead, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
            var templateSpans = f.NewNodeList(global::Ts.Core.CoreUtil.MapIndex(types, (ty, i) =>
            {
                Node res = i < types.Length - 1
                    ? f.NewTemplateMiddle(texts[i + 1], "", TokenFlags.None)
                    : f.NewTemplateTail(texts[i + 1], "", TokenFlags.None);
                e.AddEmitFlags(res, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
                return f.NewTemplateLiteralTypeSpan(TypeToTypeNodeCore(ty), res);
            })!);
            ctx!.approximateLength += 2;
            return f.NewTemplateLiteralTypeNode(templateHead, templateSpans);
        }
        // nodebuilderimpl.go:3466 — string mapping (Uppercase/Lowercase/…) type.
        // PORT-NOTE: Go uses the full symbolToTypeNode; for the intrinsic string-mapping alias symbol the
        // reduced SymbolToTypeReferenceNodeLocal renders the same `Name<Arg>`, matching the reference-arm
        // fallback already used in this file. The full symbol-chain / import-type path lands with that wave.
        if ((t.flags & TypeFlags.StringMapping) != 0)
        {
            var typeNode = TypeToTypeNodeCore(t.Target());
            return SymbolToTypeReferenceNodeLocal(t.symbol!, f.NewNodeList([typeNode]));
        }
        // nodebuilderimpl.go:3470 — indexed access (T[K]) type.
        if ((t.flags & TypeFlags.IndexedAccess) != 0)
        {
            var objectTypeNode = TypeToTypeNodeCore(t.AsIndexedAccessType().objectType);
            var indexTypeNode = TypeToTypeNodeCore(t.AsIndexedAccessType().indexType);
            ctx!.approximateLength += 2;
            return f.NewIndexedAccessTypeNode(objectTypeNode, indexTypeNode);
        }
        // nodebuilderimpl.go:3479 — substitution type (renders base type, or NoInfer<base> for no-infer).
        if ((t.flags & TypeFlags.Substitution) != 0)
        {
            var typeNode = TypeToTypeNodeCore(t.AsSubstitutionType().baseType);
            if (!ch.IsNoInferType(t))
            {
                return typeNode;
            }
            var noInferSymbol = ch.GetGlobalTypeAliasSymbol("NoInfer", 1, false);
            if (noInferSymbol != null)
            {
                return SymbolToTypeReferenceNodeLocal(noInferSymbol, f.NewNodeList([typeNode]));
            }
            return typeNode;
        }

        // nodebuilderimpl.go:3096 — conditional type.
        if ((t.flags & TypeFlags.Conditional) != 0)
        {
            return VisitAndTransformType(t, static (b, ty) => b.ConditionalTypeToTypeNode(ty));
        }
        // nodebuilderimpl.go:3286 — unique ES symbol (renders `typeof x` when the value symbol is
        // accessible, else `unique symbol`).
        if ((t.flags & TypeFlags.UniqueESSymbol) != 0)
        {
            if ((ctx.flags & global::Ts.NodeBuilder.Flags.AllowUniqueESSymbolType) == 0)
            {
                if (ch.IsValueSymbolAccessible(t.symbol!, ctx.enclosingDeclaration))
                {
                    ctx.approximateLength += 6;
                    return SymbolToTypeNode(t.symbol!, SymbolFlags.Value, null)!;
                }
                ctx.tracker?.ReportInaccessibleUniqueSymbolError();
            }
            ctx.approximateLength += 13;
            return f.NewTypeOperatorNode(SyntaxKind.UniqueKeyword, f.NewKeywordTypeNode(SyntaxKind.SymbolKeyword));
        }

        return TypeToTypeNodeLeaf(t);
    }

    // The landed intrinsic / literal / enum-like leaf core (Checker.TypeToString.cs,
    // nodebuilderimpl.go:3202-3319 leaf arms). Context-free; every ctx-reading arm is handled by
    // TypeToTypeNodeCore before reaching here.
    private Node TypeToTypeNodeLeaf(Type t) => ch.NodeBuilderTypeToTypeNode(t, f);

    // nodebuilderimpl.go:140 — checkTruncationLength.
    private bool CheckTruncationLength()
    {
        if (ctx!.truncating)
        {
            return ctx.truncating;
        }
        int maxLength;
        if ((ctx.flags & global::Ts.NodeBuilder.Flags.NoTruncation) != 0)
        {
            maxLength = Checker.NoTruncationMaximumTruncationLength;
        }
        else if (ctx.maxTruncationLength > 0)
        {
            maxLength = ctx.maxTruncationLength;
        }
        else
        {
            maxLength = Checker.DefaultMaximumTruncationLength;
        }
        ctx.truncating = ctx.approximateLength > maxLength;
        return ctx.truncating;
    }

    // nodebuilderimpl.go:169 — isExpandableType.
    // PORT-NOTE: only reached via shouldExpandType, which short-circuits when maxExpansionDepth < 0; kept
    // faithful so a future VerbosityContext port needs no re-derivation. The IsLib*ForHoverVerbosity checks
    // are deferred (hover-only); with maxExpansionDepth < 0 this method is never invoked.
    private bool IsExpandableType(Type t, bool isAlias)
    {
        if (isAlias)
        {
            // DEFERRED: IsLibSymbolForHoverVerbosity (hover-only).
            throw new NotImplementedException("nodeBuilder.isExpandableType(alias)");
        }
        // DEFERRED: IsLibTypeForHoverVerbosity (hover-only).
        throw new NotImplementedException("nodeBuilder.isExpandableType");
    }

    // nodebuilderimpl.go:191 — isTypeOnStack.
    private bool IsTypeOnStack(Type t)
    {
        for (var i = 0; i < ctx!.typeStack.Count - 1; i++)
        {
            if (ctx.typeStack[i] == t)
            {
                return true;
            }
        }
        return false;
    }

    // nodebuilderimpl.go:206 — shouldExpandType. maxExpansionDepth is fixed at -1 in the error-message /
    // .d.ts callers, so this always returns false; the hover-expansion arms below are unreachable until a
    // VerbosityContext port lands.
    private bool ShouldExpandType(Type t, bool isAlias)
    {
        if (ctx!.maxExpansionDepth < 0)
        {
            return false;
        }
        if (!IsExpandableType(t, isAlias))
        {
            return false;
        }
        if (IsTypeOnStack(t))
        {
            return false;
        }
        if (ctx.depth < ctx.maxExpansionDepth)
        {
            return true;
        }
        // DEFERRED: canIncreaseExpansionDepth bookkeeping (hover-only).
        return false;
    }

    // nodebuilderimpl.go:329 — createElidedInformationPlaceholder.
    private Node CreateElidedInformationPlaceholder()
    {
        ctx!.approximateLength += 3;
        if ((ctx.flags & global::Ts.NodeBuilder.Flags.NoTruncation) == 0)
        {
            return f.NewTypeReferenceNode(f.NewIdentifier("..."), null /*typeArguments*/);
        }
        return e.AddSyntheticLeadingComment(f.NewKeywordTypeNode(SyntaxKind.AnyKeyword), SyntaxKind.MultiLineCommentTrivia, "elided", false /*hasTrailingNewLine*/);
    }

    // nodebuilderimpl.go:337 — mapToTypeNodes (truncation-aware; the name-collision seenNames de-dup is
    // deferred, see PORT-NOTE). This local override is used by the object/reference paths; the union path in
    // Checker.TypeToString.cs keeps its own bare mapping.
    // nodebuilderimpl.go:3510 — TypeAlias.ToTypeReferenceNode.
    private Node TypeAliasToTypeReferenceNode(TypeAlias alias) =>
        f.NewTypeReferenceNode(SymbolToEntityNameNode(alias.Symbol()!), alias.TypeArguments() is { } ta ? MapToTypeNodesLocal(ta, false /*isBareList*/) : null);

    // nodebuilderimpl.go:630 — symbolToEntityNameNode.
    private Node SymbolToEntityNameNode(Symbol symbol)
    {
        var identifier = NewIdentifier(symbol.Name, symbol);
        if (symbol.Parent != null)
        {
            return f.NewQualifiedName(SymbolToEntityNameNode(symbol.Parent), identifier);
        }
        return identifier;
    }

    private TypeList? MapToTypeNodesLocal(Type[] list, bool isBareList)
    {
        if (list.Length == 0)
        {
            return null;
        }
        if (CheckTruncationLength())
        {
            if (!isBareList)
            {
                Node node;
                if ((ctx!.flags & global::Ts.NodeBuilder.Flags.NoTruncation) != 0)
                {
                    node = e.AddSyntheticLeadingComment(f.NewKeywordTypeNode(SyntaxKind.AnyKeyword), SyntaxKind.MultiLineCommentTrivia, "elided", false);
                }
                else
                {
                    node = f.NewTypeReferenceNode(f.NewIdentifier("..."), null /*typeArguments*/);
                }
                return f.NewNodeList([node]);
            }
            else if (list.Length > 2)
            {
                var nodes = new Node[3];
                nodes[0] = TypeToTypeNodeCore(list[0]);
                if ((ctx!.flags & global::Ts.NodeBuilder.Flags.NoTruncation) != 0)
                {
                    nodes[1] = e.AddSyntheticLeadingComment(f.NewKeywordTypeNode(SyntaxKind.AnyKeyword), SyntaxKind.MultiLineCommentTrivia, $"... {list.Length - 2} more elided ...", false);
                }
                else
                {
                    nodes[1] = f.NewTypeReferenceNode(f.NewIdentifier($"... {list.Length - 2} more ..."), null /*typeArguments*/);
                }
                nodes[2] = TypeToTypeNodeCore(list[list.Length - 1]);
                return f.NewNodeList(nodes);
            }
        }
        // PORT-NOTE (deferred): the mayHaveNameCollisions seenNames MultiMap de-dup (nodebuilderimpl.go:368+)
        // renames colliding type-argument references; not needed for the object-literal property path.
        var result = new Node[list.Length];
        for (var i = 0; i < list.Length; i++)
        {
            result[i] = TypeToTypeNodeCore(list[i]);
        }
        return f.NewNodeList(result);
    }

    // nodebuilderimpl.go:2932 — getParentSymbolOfTypeParameter. (Used only by the deferred outer-type-
    // parameter arm of typeReferenceToTypeNode.)
    private Symbol? GetParentSymbolOfTypeParameter(TypeParameter typeParameter)
    {
        var tp = AstUtilities.GetDeclarationOfKind(typeParameter.symbol!, SyntaxKind.TypeParameter);
        // !!! JSDoc support (isJSDocTemplateTag) omitted, matching the ported Go arm.
        var host = tp?.Parent;
        if (host is null)
        {
            return null;
        }
        return ch.GetSymbolOfNode(host);
    }

    // nodebuilderimpl.go:2947 — typeReferenceToTypeNode (Array / ReadonlyArray slice).
    // PORT-NOTE (deferred): the tuple arm, the WriteClassExpressionAsTypeLiteral arm, and the interface
    // outer-type-parameter / type-argument-elision arm are owned by the deep-reference wave; a bare
    // reference falls back to a symbol-name reference so it still renders a name instead of `any`.
    private Node TypeReferenceToTypeNode(Type t)
    {
        var typeArguments = ch.GetTypeArguments(t);
        var target = t.Target();
        if (target == ch.globalArrayType || target == ch.globalReadonlyArrayType)
        {
            if ((ctx!.flags & global::Ts.NodeBuilder.Flags.WriteArrayAsGenericType) != 0)
            {
                var typeArgumentNode = TypeToTypeNodeCore(typeArguments[0]);
                return f.NewTypeReferenceNode(
                    NewIdentifier(target == ch.globalArrayType ? "Array" : "ReadonlyArray", target.symbol),
                    f.NewNodeList([typeArgumentNode]));
            }
            var elementType = TypeToTypeNodeCore(typeArguments[0]);
            var arrayType = f.NewArrayTypeNode(elementType);
            if (target == ch.globalArrayType)
            {
                return arrayType;
            }
            return f.NewTypeOperatorNode(SyntaxKind.ReadonlyKeyword, arrayType);
        }
        if ((target.objectFlags & ObjectFlags.Tuple) != 0)
        {
            // nodebuilderimpl.go:2964-3013 — tuple arm (labels, optional/rest/variadic markers).
            var tupleTarget = target.AsTupleType()!;
            var infos = tupleTarget.ElementInfos();
            typeArguments = global::Ts.Core.CoreUtil.SameMapIndex(typeArguments, (arg, i) =>
            {
                bool isOptional = i < infos.Length && (infos[i].flags & ElementFlags.Optional) != 0;
                return ch.RemoveMissingType(arg, isOptional);
            })!; // Go SameMapIndex returns the same (non-nil) slice for non-nil input
            if (typeArguments.Length > 0)
            {
                int arity = ch.GetTypeReferenceArity(t);
                var tupleConstituentNodes = MapToTypeNodesLocal(typeArguments[0..arity], false /*isBareList*/);
                if (tupleConstituentNodes != null)
                {
                    for (int ti = 0; ti < tupleConstituentNodes.Nodes.Length; ti++)
                    {
                        var flags = infos[ti].flags;
                        var labeledElementDeclaration = infos[ti].labeledDeclaration;
                        if (labeledElementDeclaration != null)
                        {
                            tupleConstituentNodes.Nodes[ti] = f.NewNamedTupleMember(
                                (flags & ElementFlags.Variable) != 0 ? f.NewToken(SyntaxKind.DotDotDotToken) : null,
                                f.NewIdentifier(ch.GetTupleElementLabel(infos[ti], null, ti)),
                                (flags & ElementFlags.Optional) != 0 ? f.NewToken(SyntaxKind.QuestionToken) : null,
                                (flags & ElementFlags.Rest) != 0 ? f.NewArrayTypeNode(tupleConstituentNodes.Nodes[ti]) : tupleConstituentNodes.Nodes[ti]);
                        }
                        else if ((flags & ElementFlags.Variable) != 0)
                        {
                            tupleConstituentNodes.Nodes[ti] = f.NewRestTypeNode(
                                (flags & ElementFlags.Rest) != 0 ? f.NewArrayTypeNode(tupleConstituentNodes.Nodes[ti]) : tupleConstituentNodes.Nodes[ti]);
                        }
                        else if ((flags & ElementFlags.Optional) != 0)
                        {
                            tupleConstituentNodes.Nodes[ti] = f.NewOptionalTypeNode(tupleConstituentNodes.Nodes[ti]);
                        }
                    }
                    var tupleTypeNode = f.NewTupleTypeNode(tupleConstituentNodes);
                    e.SetEmitFlags(tupleTypeNode, PrinterNs.EmitFlags.SingleLine);
                    return tupleTarget.readonlyElements
                        ? f.NewTypeOperatorNode(SyntaxKind.ReadonlyKeyword, tupleTypeNode)
                        : tupleTypeNode;
                }
            }
            if (ctx!.encounteredError || (ctx.flags & global::Ts.NodeBuilder.Flags.AllowEmptyTuple) != 0)
            {
                var emptyTupleTypeNode = f.NewTupleTypeNode(f.NewNodeList([]));
                e.SetEmitFlags(emptyTupleTypeNode, PrinterNs.EmitFlags.SingleLine);
                return tupleTarget.readonlyElements
                    ? f.NewTypeOperatorNode(SyntaxKind.ReadonlyKeyword, emptyTupleTypeNode)
                    : emptyTupleTypeNode;
            }
            ctx.encounteredError = true;
            return null!; // TODO: GH#18217 (Go returns nil here)
        }
        if ((ctx!.flags & global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral) != 0
            && t.symbol!.ValueDeclaration is not null && AstUtilities.IsClassLike(t.symbol.ValueDeclaration))
        {
            // DEFERRED: the class-expression-as-type-literal arm needs IsValueSymbolAccessible
            // (symbolaccessibility.go). Only reachable with the WriteClassExpressionAsTypeLiteral flag, which
            // the anonymous-object assignability messages never set.
            throw new NotImplementedException("nodeBuilder.typeReferenceToTypeNode(classExpressionAsTypeLiteral)");
        }
        // nodebuilderimpl.go:3018-3047 — outer-type-parameter grouping: for each group of outer type
        // parameters with the same declaring container, when the group's type arguments differ from the
        // parameters themselves (i.e. an instantiated context), render the container with that argument
        // slice and chain the references (mixin<T>'s anonymous class prints `mixin.(Anonymous class)`,
        // with the outer arguments attached to `mixin` — or elided entirely when they are the defaults).
        var outerTypeParameters = target.AsInterfaceType()?.OuterTypeParameters();
        int i = 0;
        Node? resultType = null;
        if (outerTypeParameters != null)
        {
            int length = outerTypeParameters.Length;
            while (i < length)
            {
                // Find group of type arguments for type parameters with the same declaring container.
                int start = i;
                var parent = GetParentSymbolOfTypeParameter(outerTypeParameters[i].AsTypeParameter());
                do
                {
                    i++;
                }
                while (i < length && GetParentSymbolOfTypeParameter(outerTypeParameters[i].AsTypeParameter()) == parent);
                // When type parameters are their own type arguments for the whole group (i.e. we have
                // the default outer type arguments), we don't show the group.
                bool groupEqual = true;
                for (int k = start; k < i; k++)
                {
                    if (k >= typeArguments.Length || outerTypeParameters[k] != typeArguments[k])
                    {
                        groupEqual = false;
                        break;
                    }
                }
                if (!groupEqual)
                {
                    var typeArgumentSlice = MapToTypeNodesLocal(typeArguments[start..i], false /*isBareList*/);
                    var restoreOuterFlags = SaveRestoreFlags();
                    ctx!.flags |= global::Ts.NodeBuilder.Flags.ForbidIndexedAccessSymbolReferences;
                    var reference = SymbolToTypeNode(parent!, SymbolFlags.Type, typeArgumentSlice);
                    restoreOuterFlags();
                    resultType = resultType == null ? reference : AppendReferenceToType(resultType, reference!);
                }
            }
        }

        // nodebuilderimpl.go:3050-3075 — own-type-argument slice: typeArguments[i:typeParameterCount]
        // with typeParameterCount = min(len(TypeParameters), len(typeArguments)), which drops the
        // trailing this-type argument.
        TypeList? typeArgumentNodes = null;
        if (typeArguments.Length > 0)
        {
            int typeParameterCount = 0;
            var typeParams = target.AsInterfaceType()?.TypeParameters();
            if (typeParams != null)
            {
                typeParameterCount = Math.Min(typeParams.Length, typeArguments.Length);

                // nodebuilderimpl.go:3056-3072 — elide type arguments identical to their type parameters'
                // defaults, but only for `Iterable`, `IterableIterator`, `AsyncIterable`, and
                // `AsyncIterableIterator` (each now has three type parameters instead of one), for
                // backwards-compatible .d.ts emit. Only when the reference has no as-written type-argument
                // node list covering typeParameterCount.
                if (ch.IsReferenceToType(t, ch.GetGlobalIterableType()) || ch.IsReferenceToType(t, ch.GetGlobalIterableIteratorType()) ||
                    ch.IsReferenceToType(t, ch.GetGlobalAsyncIterableType()) || ch.IsReferenceToType(t, ch.GetGlobalAsyncIterableIteratorType()))
                {
                    var refNode = t.AsTypeReference()!.node;
                    if (refNode is null || !NodeTests.IsTypeReferenceNode(refNode) || refNode.TypeArguments() is null || refNode.TypeArguments()!.Length < typeParameterCount)
                    {
                        while (typeParameterCount > 0)
                        {
                            var typeArgument = typeArguments[typeParameterCount - 1];
                            var typeParameter = typeParams[typeParameterCount - 1];
                            var defaultType = ch.GetDefaultFromTypeParameter(typeParameter);
                            if (defaultType is null || !ch.IsTypeIdenticalTo(typeArgument, defaultType))
                            {
                                break;
                            }
                            typeParameterCount--;
                        }
                    }
                }
            }
            typeArgumentNodes = MapToTypeNodesLocal(typeArguments[i..typeParameterCount], false /*isBareList*/);
        }
        // nodebuilderimpl.go:3074 — finalRef := symbolToTypeNode(t.symbol, Type, typeArgumentNodes).
        var restoreFlags = SaveRestoreFlags();
        ctx!.flags |= global::Ts.NodeBuilder.Flags.ForbidIndexedAccessSymbolReferences;
        var finalRef = SymbolToTypeNode(t.symbol!, SymbolFlags.Type, typeArgumentNodes);
        restoreFlags();
        if (resultType == null)
        {
            return finalRef!;
        }
        return AppendReferenceToType(resultType, finalRef!);
    }

    // nodebuilderimpl.go:313 — getAccessStack: the identifiers of a type reference's entity name,
    // leftmost first.
    private static List<Node> GetAccessStack(Node reference)
    {
        Node state = reference.AsTypeReferenceNode().TypeName!;
        var ids = new List<Node>();
        while (!NodeTests.IsIdentifier(state))
        {
            var entity = state.AsQualifiedName();
            ids.Insert(0, entity.Right!);
            state = entity.Left!;
        }
        ids.Insert(0, state);
        return ids;
    }

    // nodebuilderimpl.go:3522 — createAccessExpression.
    private Node CreateAccessExpression(Node node)
    {
        if (NodeTests.IsQualifiedName(node))
        {
            return f.NewPropertyAccessExpression(CreateAccessExpression(node.AsQualifiedName().Left!), null /*questionDotToken*/, f.DeepCloneNode(node.AsQualifiedName().Right!)!, NodeFlags.None);
        }
        if (NodeTests.IsIdentifier(node) || NodeTests.IsPropertyAccessExpression(node) || NodeTests.IsExpressionWithTypeArguments(node))
        {
            return f.DeepCloneNode(node)!;
        }
        throw new InvalidOperationException($"unexpected access node kind: {node.Kind}");
    }

    // nodebuilderimpl.go — appendReferenceToType: append `ref`'s qualifiers/type arguments onto `root`
    // (an import type or type reference), producing the combined qualified reference.
    private Node AppendReferenceToType(Node root, Node reference)
    {
        if (NodeTests.IsImportTypeNode(root))
        {
            // first shift type arguments
            // !!! In the old emitter, an Identifier could have type arguments for use with quickinfo
            // (see the commented-out identifier-type-argument shifting in Go); without it, nested type
            // args are silently elided.
            var import = root.AsImportTypeNode();
            // then move qualifiers
            var ids = GetAccessStack(reference);
            Node? qualifier = import.Qualifier;
            foreach (var id in ids)
            {
                qualifier = qualifier != null ? f.NewQualifiedName(qualifier, id) : id;
            }
            return f.UpdateImportTypeNode(import, import.IsTypeOf, import.Argument, import.Attributes, qualifier, reference.TypeArgumentList());
        }
        else if (NodeTests.IsTypeReferenceNode(root))
        {
            var typeRef = root.AsTypeReferenceNode();
            if ((ctx!.flags & global::Ts.NodeBuilder.Flags.UseInstantiationExpressions) != 0 && typeRef.TypeArguments != null && typeRef.TypeArguments.Nodes.Length != 0)
            {
                Node expr = CreateExpressionWithTypeArguments(CreateAccessExpression(typeRef.TypeName!), typeRef.TypeArguments);
                foreach (var id in GetAccessStack(reference))
                {
                    expr = f.NewPropertyAccessExpression(expr, null, id, NodeFlags.None);
                }
                return expr;
            }
            Node typeName = typeRef.TypeName!;
            foreach (var id in GetAccessStack(reference))
            {
                typeName = f.NewQualifiedName(typeName, id);
            }
            return f.UpdateTypeReferenceNode(typeRef, typeName, reference.TypeArgumentList());
        }
        Node fallbackExpr = CreateAccessExpression(root);
        foreach (var id in GetAccessStack(reference))
        {
            fallbackExpr = f.NewPropertyAccessExpression(fallbackExpr, null, id, NodeFlags.None);
        }
        return fallbackExpr;
    }

    // A bare symbol-name type reference (nodebuilderimpl.go:638 symbolToTypeNode, reduced form) that accepts
    // pre-built type-argument nodes. PORT-NOTE: the full symbol chain (qualified names / import types /
    // accessibility tracking) is deferred with the symbol-chain wave.
    private Node SymbolToTypeReferenceNodeLocal(Symbol symbol, TypeList? typeArguments)
    {
        // getNameOfSymbolAsWritten renders internal names as written ('(Anonymous class)', 'default', …)
        // instead of the raw InternalSymbolName text (e.g. 'þclass').
        var name = GetNameOfSymbolAsWritten(symbol);
        return f.NewTypeReferenceNode(f.NewIdentifier(name), typeArguments);
    }

    // nodebuilderimpl.go:325 — isClassInstanceSide (free func).
    private static bool IsClassInstanceSide(Checker c, Type t) =>
        t.symbol is not null && (t.symbol.Flags & SymbolFlags.Class) != 0
        && (t == c.GetDeclaredTypeOfClassOrInterface(t.symbol)
            || ((t.flags & TypeFlags.Object) != 0 && (t.objectFlags & ObjectFlags.IsClassInstanceClone) != 0));

    // utilities.go:956 — IsPrivateIdentifierSymbol (free func).
    private static bool IsPrivateIdentifierSymbol(Symbol? symbol) =>
        symbol is not null && symbol.Name.StartsWith(InternalSymbolName.Prefix + "#", StringComparison.Ordinal);

    // nodebuilderimpl.go:2770 — createAnonymousTypeNode.
    private Node CreateAnonymousTypeNode(Type t) => CreateAnonymousTypeNodeEx(t, false, false);

    // nodebuilderimpl.go:2733 — shouldWriteTypeOfFunctionSymbol.
    private (bool, Symbol) ShouldWriteTypeOfFunctionSymbol(Symbol symbol, TypeId typeId)
    {
        var isStaticMethodSymbol = (symbol.Flags & SymbolFlags.Method) != 0
            && CoreUtil.Some(symbol.Declarations, declaration =>
                AstUtilities.IsStatic(declaration) && !ch.IsLateBindableIndexSignature(AstUtilities.GetNameOfDeclaration(declaration)!));
        var isNonLocalFunctionSymbol = false;
        var isFunctionExpressionSymbol = false;
        if ((symbol.Flags & SymbolFlags.Function) != 0)
        {
            if (symbol.Parent is not null)
            {
                isNonLocalFunctionSymbol = true;
            }
            else
            {
                foreach (var declaration in symbol.Declarations ?? [])
                {
                    if (declaration.Parent!.Kind == SyntaxKind.SourceFile || declaration.Parent.Kind == SyntaxKind.ModuleBlock)
                    {
                        isNonLocalFunctionSymbol = true;
                        break;
                    }
                    if (AstUtilities.IsFunctionExpressionOrArrowFunction(declaration) && NodeTests.IsVariableDeclaration(declaration.Parent) &&
                        NodeTests.IsVariableDeclarationList(declaration.Parent.Parent!) && NodeTests.IsVariableStatement(declaration.Parent.Parent!.Parent!) &&
                        declaration.Parent.Parent.Parent!.Parent is not null &&
                        (declaration.Parent.Parent.Parent.Parent.Kind == SyntaxKind.SourceFile || declaration.Parent.Parent.Parent.Parent.Kind == SyntaxKind.ModuleBlock))
                    {
                        isNonLocalFunctionSymbol = true;
                        isFunctionExpressionSymbol = true;
                        break;
                    }
                }
            }
        }
        if (isStaticMethodSymbol || isNonLocalFunctionSymbol)
        {
            if (isFunctionExpressionSymbol && symbol.ValueDeclaration is not null && symbol.ValueDeclaration.Parent is not null &&
                symbol.ValueDeclaration.Parent != ctx!.enclosingDeclaration)
            {
                symbol = ch.GetMergedSymbol(symbol.ValueDeclaration.Parent.Symbol())!;
            }
            // typeof is allowed only for static/non local functions
            return (((ctx!.flags & global::Ts.NodeBuilder.Flags.UseTypeOfFunction) != 0 || ctx.visitedTypes.Contains(typeId)) && // it is type of the symbol uses itself recursively
                ((ctx.flags & global::Ts.NodeBuilder.Flags.UseStructuralFallback) == 0 || ch.IsValueSymbolAccessible(symbol, ctx.enclosingDeclaration)), symbol); // And the build is going to succeed without visibility error or there is no structural fallback allowed
        }
        return (false, symbol);
    }

    // nodebuilderimpl.go:2774 — shouldEmitTypeOfSymbol.
    private (bool, Symbol) ShouldEmitTypeOfSymbol(bool forceExpansion, bool forceClassExpansion, SymbolFlags isInstanceType, Symbol symbol, TypeId typeId)
    {
        if (forceExpansion)
        {
            return (false, symbol);
        }
        var nonFunctionResult = ((symbol.Flags & SymbolFlags.Class) != 0 && !forceClassExpansion && ch.GetBaseTypeVariableOfClass(symbol) is null &&
            !(symbol.ValueDeclaration is not null && AstUtilities.IsClassLike(symbol.ValueDeclaration) &&
                (ctx!.flags & global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral) != 0 &&
                (!NodeTests.IsClassDeclaration(symbol.ValueDeclaration) ||
                    ch.IsSymbolAccessible(symbol, ctx.enclosingDeclaration, isInstanceType, false /*shouldComputeAliasesToMakeVisible*/).Accessibility != global::Ts.Printer.SymbolAccessibility.Accessible)))
            || (symbol.Flags & (SymbolFlags.Enum | SymbolFlags.ValueModule)) != 0;
        if (nonFunctionResult)
        {
            return (true, symbol);
        }
        return ShouldWriteTypeOfFunctionSymbol(symbol, typeId);
    }

    // nodebuilderimpl.go:2785 — createAnonymousTypeNodeEx.
    private Node CreateAnonymousTypeNodeEx(Type t, bool forceClassExpansion, bool forceExpansion)
    {
        var typeId = t.id;
        var symbol = t.symbol;
        if (symbol is not null)
        {
            var isInstantiationExpressionType = (t.objectFlags & ObjectFlags.InstantiationExpressionType) != 0;
            if (isInstantiationExpressionType)
            {
                var instantiationExpressionType = t.AsInstantiationExpressionType();
                var existing = instantiationExpressionType.node;
                // instantiationExpressionType.node is unreliable for constituents of unions/intersections.
                if (existing != null && NodeTests.IsTypeQueryNode(existing) && ch.GetTypeFromTypeNode(existing) == t)
                {
                    // Guard against unbounded recursion when the existing typeof node fails to be reused and
                    // the recovery boundary's fallback re-enters typeToTypeNode with the same instantiation type.
                    if (ctx!.visitedTypes.Contains(typeId))
                    {
                        return CreateElidedInformationPlaceholder();
                    }
                    ctx.visitedTypes.Add(typeId);
                    var typeNode = TryReuseExistingNonParameterTypeNode(existing, t, null, null);
                    ctx.visitedTypes.Remove(typeId);
                    if (typeNode != null)
                    {
                        return typeNode;
                    }
                }
                if (ctx!.visitedTypes.Contains(typeId))
                {
                    return CreateElidedInformationPlaceholder();
                }
                return VisitAndTransformType(t, static (b, ty) => b.CreateTypeNodeFromObjectType(ty));
            }
            SymbolFlags isInstanceType;
            if (IsClassInstanceSide(ch, t))
            {
                isInstanceType = SymbolFlags.Type;
            }
            else
            {
                isInstanceType = SymbolFlags.Value;
            }

            // !!! JS support (isJSConstructor) omitted, matching the ported Go arm.
            var (ok, resolvedSymbol) = ShouldEmitTypeOfSymbol(forceExpansion, forceClassExpansion, isInstanceType, symbol, typeId);
            if (ok)
            {
                if (ShouldExpandType(t, false /*isAlias*/))
                {
                    ctx!.depth++;
                }
                else
                {
                    return SymbolToTypeNode(resolvedSymbol, isInstanceType, null)!;
                }
            }
            if (ctx!.visitedTypes.Contains(typeId))
            {
                // If type is an anonymous type literal in a type alias declaration, use type alias name.
                var typeAlias = GetTypeAliasForTypeLiteral(ch, t);
                if (typeAlias is not null)
                {
                    // The specified symbol flags need to be reinterpreted as type flags.
                    return SymbolToTypeNode(typeAlias, SymbolFlags.Type, null)!;
                }
                return CreateElidedInformationPlaceholder();
            }
            return VisitAndTransformType(t, static (b, ty) => b.CreateTypeNodeFromObjectType(ty));
        }
        // Anonymous types without a symbol are never circular.
        return CreateTypeNodeFromObjectType(t);
    }

    // nodebuilderimpl.go:2723 — getTypeAliasForTypeLiteral.
    private static Symbol? GetTypeAliasForTypeLiteral(Checker c, Type t)
    {
        if (t.symbol is not null && (t.symbol.Flags & SymbolFlags.TypeLiteral) != 0 && t.symbol.Declarations is not null)
        {
            var node = AstUtilities.WalkUpParenthesizedTypes(t.symbol.Declarations[0].Parent!);
            if (node is not null && NodeTests.IsTypeAliasDeclaration(node))
            {
                return c.GetSymbolOfDeclaration(node);
            }
        }
        return null;
    }

    // nodebuilderimpl.go:638 — symbolToTypeNode, reduced. PORT-NOTE (reduced): the import-type module
    // root arm and the symbol-chain access with type arguments (createAccessFromSymbolChain) stay with
    // the symbol-chain wave; the diagnostics slice only reaches this with simple bare-name chains. The
    // `typeof X` wrapping for mask == Value (nodebuilderimpl.go:727-736) is load-bearing for
    // "Type 'typeof C' ..." messages and is ported.
    private Node SymbolToNodeImplTypeNode(Symbol symbol, SymbolFlags meaning)
    {
        var isTypeOf = meaning == SymbolFlags.Value;
        var entityName = f.NewIdentifier(AstUtilities.SymbolName(symbol));
        if (isTypeOf)
        {
            return f.NewTypeQueryNode(entityName, null);
        }
        return f.NewTypeReferenceNode(entityName, null);
    }

    // nodebuilderimpl.go:3131 — conditionalTypeToTypeNode.
    // PORT-NOTE: the FlagsGenerateNamesForShadowedTypeParams distributive-rename branch
    // (nodebuilderimpl.go:3138-3165, a .d.ts-emit-only path) is deferred; error-message rendering never
    // sets that flag and takes the simple path below.
    private Node ConditionalTypeToTypeNode(Type conditional)
    {
        if (CheckTruncationLength())
        {
            return CreateElidedInformationPlaceholder();
        }
        var t = conditional.AsConditionalType();
        var checkTypeNode = TypeToTypeNodeCore(t.checkType!);
        ctx!.approximateLength += 15;
        var saveInferTypeParameters = ctx.inferTypeParameters;
        ctx.inferTypeParameters = t.root.inferTypeParameters;
        var extendsTypeNode = TypeToTypeNodeCore(t.extendsType!);
        ctx.inferTypeParameters = saveInferTypeParameters;
        var trueTypeNode = TypeToTypeNodeOrCircularityElision(ch.GetTrueTypeFromConditionalType(conditional));
        var falseTypeNode = TypeToTypeNodeOrCircularityElision(ch.GetFalseTypeFromConditionalType(conditional));
        return f.NewConditionalTypeNode(checkTypeNode, extendsTypeNode, trueTypeNode, falseTypeNode);
    }

    // nodebuilderimpl.go:3170 — typeToTypeNodeOrCircularityElision.
    private Node TypeToTypeNodeOrCircularityElision(Type t)
    {
        if ((t.flags & TypeFlags.Union) != 0)
        {
            if (ctx!.visitedTypes.Contains(t.id))
            {
                if ((ctx.flags & global::Ts.NodeBuilder.Flags.AllowAnonymousIdentifier) == 0)
                {
                    ctx.encounteredError = true;
                    ctx.tracker?.ReportCyclicStructureError();
                }
                return CreateElidedInformationPlaceholder();
            }
            return VisitAndTransformType(t, static (b, ty) => b.TypeToTypeNodeCore(ty));
        }
        return TypeToTypeNodeCore(t);
    }

    // nodebuilderimpl.go:3089 — visitAndTransformType (circular-reference / symbol-depth guard).
    // PORT-NOTE (deferred): the serializedTypes cache (b.links per-enclosing-declaration) and the
    // trackedSymbols capture are omitted — they only affect node identity / repeated-serialization speed,
    // not printed bytes. The visitedTypes + symbolDepth recursion guard IS load-bearing and is ported.
    private Node VisitAndTransformType(Type t, Func<NodeBuilder, Type, Node> transform)
    {
        var typeId = t.id;
        var isConstructorObject = (t.objectFlags & ObjectFlags.Anonymous) != 0
            && t.symbol is not null && (t.symbol.Flags & SymbolFlags.Class) != 0;
        CompositeSymbolIdentity? id;
        if ((t.objectFlags & ObjectFlags.Reference) != 0 && t.AsTypeReference()!.node is not null)
        {
            id = new CompositeSymbolIdentity(false, default, AstUtilities.GetNodeId(t.AsTypeReference()!.node!));
        }
        else if ((t.flags & TypeFlags.Conditional) != 0)
        {
            id = new CompositeSymbolIdentity(false, default, AstUtilities.GetNodeId(t.AsConditionalType().root.node!.AsNode()));
        }
        else if (t.symbol is not null)
        {
            id = new CompositeSymbolIdentity(isConstructorObject, AstUtilities.GetSymbolId(t.symbol), default);
        }
        else
        {
            id = null;
        }
        // Since instantiations of the same anonymous type have the same symbol, tracking symbols instead of
        // types allows us to catch circular references to instantiations of the same anonymous type.

        // PORT-NOTE (deferred): the serializedTypes cache lookup (canUseCache && enclosingDeclaration links)
        // is omitted; it is a pure optimization over identical printed output.

        var depth = 0;
        if (id is not null)
        {
            depth = ctx!.symbolDepth.GetValueOrDefault(id.Value);
            if (depth > 10)
            {
                return CreateElidedInformationPlaceholder();
            }
            ctx.symbolDepth[id.Value] = depth + 1;
        }
        ctx!.visitedTypes.Add(typeId);
        var result = transform(this, t);
        ctx.visitedTypes.Remove(typeId);
        if (id is not null)
        {
            ctx.symbolDepth[id.Value] = depth;
        }
        return result;
    }

    // nodebuilderimpl.go:1444 — isMappedTypeHomomorphic.
    private bool IsMappedTypeHomomorphic(Type mapped) => ch.GetHomomorphicTypeVariable(mapped) != null;

    // nodebuilderimpl.go:1448 — isHomomorphicMappedTypeWithNonHomomorphicInstantiation.
    private bool IsHomomorphicMappedTypeWithNonHomomorphicInstantiation(MappedType mapped) =>
        mapped.target != null && !IsMappedTypeHomomorphic(mapped) && IsMappedTypeHomomorphic(mapped.target);

    // nodebuilderimpl.go:2858 — getTypeFromTypeNode (nodebuilder variant with noMappedTypes).
    private Type? GetTypeFromTypeNodeNb(Node node, bool noMappedTypes)
    {
        // !!! noMappedTypes optional param support
        var t = ch.GetTypeFromTypeNode(node);
        if (ctx!.mapper == null)
        {
            return t;
        }

        var instantiated = ch.InstantiateType(t, ctx.mapper);
        if (noMappedTypes && instantiated != t)
        {
            return null;
        }
        return instantiated;
    }

    // nodebuilderimpl.go:1452 — createMappedTypeNodeFromType.
    private Node CreateMappedTypeNodeFromType(Type t)
    {
        global::Ts.Core.Debug.Assert((t.flags & TypeFlags.Object) != 0);
        var mapped = t.AsMappedType()!;
        Node? readonlyToken = null;
        if (mapped.declaration!.AsMappedTypeNode().ReadonlyToken != null)
        {
            readonlyToken = f.NewToken(mapped.declaration.AsMappedTypeNode().ReadonlyToken!.Kind);
        }
        Node? questionToken = null;
        if (mapped.declaration.AsMappedTypeNode().QuestionToken != null)
        {
            questionToken = f.NewToken(mapped.declaration.AsMappedTypeNode().QuestionToken!.Kind);
        }
        Node? appropriateConstraintTypeNode;
        Node? newTypeVariable = null;
        var templateType = ch.GetTemplateTypeFromMappedType(t);
        var typeParameter = ch.GetTypeParameterFromMappedType(t);

        // If the mapped type isn't `keyof` constraint-declared, _but_ still has modifiers preserved, and its
        // naive instantiation won't preserve modifiers because its constraint isn't `keyof` constrained, we
        // have work to do.
        bool needsModifierPreservingWrapper = !ch.IsMappedTypeWithKeyofConstraintDeclaration(t) &&
            (ch.GetModifiersTypeFromMappedType(t).flags & TypeFlags.Unknown) == 0 &&
            (ctx!.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0 &&
            !((ch.GetConstraintTypeFromMappedType(t).flags & TypeFlags.TypeParameter) != 0 &&
                (ch.GetConstraintOfTypeParameter(ch.GetConstraintTypeFromMappedType(t))!.flags & TypeFlags.Index) != 0);

        if (ch.IsMappedTypeWithKeyofConstraintDeclaration(t))
        {
            // We have a { [P in keyof T]: X }. Retain the toplevel keyof-ness of the type which may be lost
            // due to keyof distribution during getConstraintTypeFromMappedType.
            if ((ctx!.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0 && IsHomomorphicMappedTypeWithNonHomomorphicInstantiation(mapped))
            {
                var newConstraintParam = ch.NewTypeParameter(ch.NewSymbol(SymbolFlags.TypeParameter, "T"));
                var name = TypeParameterToName(newConstraintParam);
                var target = t.Target();
                newTypeVariable = f.NewTypeReferenceNode(name, null);
                templateType = ch.InstantiateType(ch.GetTemplateTypeFromMappedType(target), TypeMapper.NewTypeMapper(
                    [ch.GetTypeParameterFromMappedType(target), ch.GetModifiersTypeFromMappedType(target)], [typeParameter, newConstraintParam]));
            }
            var indexTarget = newTypeVariable ?? TypeToTypeNodeCore(ch.GetModifiersTypeFromMappedType(t));
            appropriateConstraintTypeNode = f.NewTypeOperatorNode(SyntaxKind.KeyOfKeyword, indexTarget);
        }
        else if (needsModifierPreservingWrapper)
        {
            // Step 1: new type variable.
            var newParam = ch.NewTypeParameter(ch.NewSymbol(SymbolFlags.TypeParameter, "T"));
            var name = TypeParameterToName(newParam);
            newTypeVariable = f.NewTypeReferenceNode(name, null);
            // Step 2: make that new type variable itself the constraint node -> `{[K in T_1]: Template}`.
            appropriateConstraintTypeNode = newTypeVariable;
        }
        else
        {
            appropriateConstraintTypeNode = TypeToTypeNodeCore(ch.GetConstraintTypeFromMappedType(t));
        }

        // nameType and templateType nodes have to be in the new scope.
        var cleanup = EnterNewScope(mapped.declaration, null, [ch.GetTypeParameterFromMappedType(t)], null, null);
        var typeParameterDeclarationNode = TypeParameterToDeclarationWithConstraint(typeParameter, appropriateConstraintTypeNode);
        Node? nameTypeNode = null;
        if (mapped.declaration.AsMappedTypeNode().NameType != null)
        {
            nameTypeNode = TypeToTypeNodeCore(ch.GetNameTypeFromMappedType(t)!);
        }
        var templateTypeNode = TypeToTypeNodeCore(ch.RemoveMissingType(templateType,
            (ch.GetMappedTypeModifiers(t) & MappedTypeModifiers.IncludeOptional) != 0));
        cleanup();
        var result = f.NewMappedTypeNode(readonlyToken, typeParameterDeclarationNode, nameTypeNode, questionToken, templateTypeNode, null);
        ctx!.approximateLength += 10;
        e.AddEmitFlags(result, global::Ts.Printer.EmitFlags.SingleLine);

        if ((ctx.flags & global::Ts.NodeBuilder.Flags.GenerateNamesForShadowedTypeParams) != 0 && IsHomomorphicMappedTypeWithNonHomomorphicInstantiation(mapped))
        {
            // Homomorphic mapped type with a non-homomorphic naive inlining: wrap it with a conditional like
            // `SomeModifiersType extends infer U ? {..the mapped type...} : never` to keep it homomorphic.
            var rawConstraintTypeFromDeclaration = GetTypeFromTypeNodeNb(mapped.declaration.AsMappedTypeNode().TypeParameter!.AsTypeParameterDeclaration().Constraint!.Type()!, false);
            if (rawConstraintTypeFromDeclaration != null)
            {
                rawConstraintTypeFromDeclaration = ch.GetConstraintOfTypeParameter(rawConstraintTypeFromDeclaration);
            }
            rawConstraintTypeFromDeclaration ??= ch.unknownType;
            var originalConstraint = ch.InstantiateType(rawConstraintTypeFromDeclaration, mapped.mapper);
            Node? originalConstraintNode = null;
            if ((originalConstraint.flags & TypeFlags.Unknown) == 0)
            {
                originalConstraintNode = TypeToTypeNodeCore(originalConstraint);
            }
            return f.NewConditionalTypeNode(
                TypeToTypeNodeCore(ch.GetModifiersTypeFromMappedType(t)),
                f.NewInferTypeNode(f.NewTypeParameterDeclaration(null, newTypeVariable!.AsTypeReferenceNode().TypeName!.Clone(f), originalConstraintNode, null, null)),
                result,
                f.NewKeywordTypeNode(SyntaxKind.NeverKeyword));
        }
        else if (needsModifierPreservingWrapper)
        {
            // Step 3: `ConstraintType extends infer T_1 extends keyof ModifiersType ? {[K in T_1]: Template} : never`.
            return f.NewConditionalTypeNode(
                TypeToTypeNodeCore(ch.GetConstraintTypeFromMappedType(t)),
                f.NewInferTypeNode(f.NewTypeParameterDeclaration(null, newTypeVariable!.AsTypeReferenceNode().TypeName!.Clone(f),
                    f.NewTypeOperatorNode(SyntaxKind.KeyOfKeyword, TypeToTypeNodeCore(ch.GetModifiersTypeFromMappedType(t))), null, null)),
                result,
                f.NewKeywordTypeNode(SyntaxKind.NeverKeyword));
        }
        return result;
    }

    // nodebuilderimpl.go:2663 — createTypeNodeFromObjectType.
    private Node CreateTypeNodeFromObjectType(Type t)
    {
        if (ch.IsGenericMappedType(t) || ((t.objectFlags & ObjectFlags.Mapped) != 0 && t.AsMappedType()!.containsError))
        {
            return CreateMappedTypeNodeFromType(t);
        }

        var resolved = ch.ResolveStructuredTypeMembers(t);
        var callSigs = resolved.CallSignatures();
        var ctorSigs = resolved.ConstructSignatures();
        if (resolved.Properties().Length == 0 && (resolved.indexInfos is null || resolved.indexInfos.Length == 0))
        {
            if (callSigs.Length == 0 && ctorSigs.Length == 0)
            {
                ctx!.approximateLength += 2;
                var emptyResult = f.NewTypeLiteralNode(f.NewNodeList(System.Array.Empty<Node>()));
                e.SetEmitFlags(emptyResult, PrinterNs.EmitFlags.SingleLine);
                return emptyResult;
            }

            if (callSigs.Length == 1 && ctorSigs.Length == 0)
            {
                var signature = callSigs[0];
                return SignatureToSignatureDeclarationHelper(signature, SyntaxKind.FunctionType, null);
            }

            if (ctorSigs.Length == 1 && callSigs.Length == 0)
            {
                var signature = ctorSigs[0];
                return SignatureToSignatureDeclarationHelper(signature, SyntaxKind.ConstructorType, null);
            }
        }

        var abstractSignatures = CoreUtil.Filter(ctorSigs, signature => (signature.flags & SignatureFlags.Abstract) != 0) ?? [];
        if (abstractSignatures.Length > 0)
        {
            var types = CoreUtil.Map(abstractSignatures, s => ch.GetOrCreateTypeFromSignature(s)) ?? [];
            // count the number of type elements excluding abstract constructors
            var typeElementCount = callSigs.Length + (ctorSigs.Length - abstractSignatures.Length)
                + (resolved.indexInfos?.Length ?? 0)
                + ((ctx!.flags & global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral) != 0
                    ? CoreUtil.CountWhere(resolved.Properties(), p => (p.Flags & SymbolFlags.Prototype) == 0)
                    : resolved.Properties().Length);
            // don't include an empty object literal if there were no other static-side properties to write,
            // i.e. `abstract class C { }` becomes `abstract new () => {}` and not `(abstract new () => {}) & {}`
            if (typeElementCount != 0)
            {
                // create a copy of the object type without any abstract construct signatures.
                types = [.. types, GetResolvedTypeWithoutAbstractConstructSignatures(resolved)];
            }
            return TypeToTypeNodeCore(ch.GetIntersectionType(types));
        }

        var restoreFlags = SaveRestoreFlags();
        ctx!.flags |= global::Ts.NodeBuilder.Flags.InObjectTypeLiteral;
        var members = CreateTypeNodesFromResolvedType(resolved);
        restoreFlags();
        var typeLiteralNode = f.NewTypeLiteralNode(members);
        ctx.approximateLength += 2;
        e.SetEmitFlags(typeLiteralNode, (ctx.flags & global::Ts.NodeBuilder.Flags.MultilineObjectLiterals) != 0 ? PrinterNs.EmitFlags.None : PrinterNs.EmitFlags.SingleLine);
        return typeLiteralNode;
    }

    // nodebuilderimpl.go:552 — getResolvedTypeWithoutAbstractConstructSignatures.
    private Type GetResolvedTypeWithoutAbstractConstructSignatures(StructuredType t)
    {
        if (t.ConstructSignatures().Length == 0)
        {
            return t;
        }
        if (t.objectTypeWithoutAbstractConstructSignatures is not null)
        {
            return t.objectTypeWithoutAbstractConstructSignatures;
        }
        var constructSignatures = CoreUtil.Filter(t.ConstructSignatures(), signature => (signature.flags & SignatureFlags.Abstract) == 0) ?? [];
        if (constructSignatures.Length == t.ConstructSignatures().Length)
        {
            t.objectTypeWithoutAbstractConstructSignatures = t;
            return t;
        }
        var typeCopy = ch.NewAnonymousType(t.symbol, t.members, t.CallSignatures(), constructSignatures.Length > 0 ? constructSignatures : System.Array.Empty<Signature>(), t.indexInfos);
        t.objectTypeWithoutAbstractConstructSignatures = typeCopy;
        ((StructuredType)typeCopy).objectTypeWithoutAbstractConstructSignatures = typeCopy;
        return typeCopy;
    }

    // nodebuilderimpl.go:2600 — createTypeNodesFromResolvedType.
    private TypeList? CreateTypeNodesFromResolvedType(StructuredType resolvedType)
    {
        if (CheckTruncationLength())
        {
            if ((ctx!.flags & global::Ts.NodeBuilder.Flags.NoTruncation) != 0)
            {
                var elem = f.NewNotEmittedTypeElement();
                return f.NewNodeList([e.AddSyntheticTrailingComment(elem, SyntaxKind.MultiLineCommentTrivia, "elided", false /*hasTrailingNewLine*/)]);
            }
            return f.NewNodeList([f.NewPropertySignatureDeclaration(null, f.NewIdentifier("..."), null, null, null)]);
        }
        var typeElements = new List<Node>();
        foreach (var signature in resolvedType.CallSignatures())
        {
            typeElements.Add(SignatureToSignatureDeclarationHelper(signature, SyntaxKind.CallSignature, null));
        }
        foreach (var signature in resolvedType.ConstructSignatures())
        {
            if ((signature.flags & SignatureFlags.Abstract) != 0)
            {
                continue;
            }
            typeElements.Add(SignatureToSignatureDeclarationHelper(signature, SyntaxKind.ConstructSignature, null));
        }
        foreach (var info in resolvedType.indexInfos ?? System.Array.Empty<IndexInfo>())
        {
            typeElements.AddRange(IndexInfoToObjectComputedNamesOrSignatureDeclaration(info,
                (resolvedType.objectFlags & ObjectFlags.ReverseMapped) != 0 ? CreateElidedInformationPlaceholder() : null));
        }

        var properties = resolvedType.Properties();
        if (properties.Length == 0)
        {
            return f.NewNodeList(typeElements.ToArray());
        }

        var i = 0;
        foreach (var propertySymbol in properties)
        {
            if (IsExpanding(ctx!) && (propertySymbol.Flags & SymbolFlags.Prototype) != 0)
            {
                continue;
            }
            i++;
            if ((ctx!.flags & global::Ts.NodeBuilder.Flags.WriteClassExpressionAsTypeLiteral) != 0)
            {
                if ((propertySymbol.Flags & SymbolFlags.Prototype) != 0)
                {
                    continue;
                }
                if ((Checker.GetDeclarationModifierFlagsFromSymbol(propertySymbol) & (ModifierFlags.Private | ModifierFlags.Protected)) != 0)
                {
                    ctx.tracker?.ReportPrivateInBaseOfClassExpression(propertySymbol.Name);
                }
                if (IsPrivateIdentifierSymbol(propertySymbol))
                {
                    ctx.tracker?.ReportPrivateInBaseOfClassExpression(AstUtilities.SymbolName(propertySymbol));
                }
            }
            if (CheckTruncationLength() && (i + 2 < properties.Length - 1))
            {
                if ((ctx.flags & global::Ts.NodeBuilder.Flags.NoTruncation) != 0)
                {
                    typeElements[typeElements.Count - 1] = e.AddSyntheticTrailingComment(typeElements[typeElements.Count - 1], SyntaxKind.MultiLineCommentTrivia, $"... {properties.Length - i} more elided ...", false /*hasTrailingNewLine*/);
                }
                else
                {
                    var text = $"... {properties.Length - i} more ...";
                    typeElements.Add(f.NewPropertySignatureDeclaration(null, f.NewIdentifier(text), null, null, null));
                }
                AddPropertyToElementList(properties[properties.Length - 1], typeElements);
                break;
            }
            AddPropertyToElementList(propertySymbol, typeElements);
        }
        if (typeElements.Count != 0)
        {
            return f.NewNodeList(typeElements.ToArray());
        }
        return null;
    }

    // nodebuilderimpl.go:2459 — addPropertyToElementList. Ported for the property-signature slice; the
    // accessor (getter/setter), method-signature, reverse-mapped-placeholder, and late-bound-name arms are
    // deferred (see the PORT-NOTEs inline) and throw so a stubbed path never silently drops output.
    // nodebuilderimpl.go:2334 — trackComputedName.
    private void TrackComputedName(Node accessExpression, Node? enclosingDeclaration)
    {
        // get symbol of the first identifier of the entityName
        var firstIdentifier = Checker.GetFirstIdentifier(accessExpression);
        var name = ch.ResolveName(enclosingDeclaration, firstIdentifier.Text(), SymbolFlags.Value | SymbolFlags.ExportValue, null /*nameNotFoundMessage*/, true /*isUse*/, false);
        if (name != null)
        {
            ctx!.tracker?.TrackSymbol(name, enclosingDeclaration, SymbolFlags.Value);
        }
        else
        {
            // Name does not resolve at target location, track symbol at dest location (should be inaccessible)
            var fallback = ch.ResolveName(firstIdentifier, firstIdentifier.Text(), SymbolFlags.Value | SymbolFlags.ExportValue, null /*nameNotFoundMessage*/, true /*isUse*/, false);
            if (fallback != null)
            {
                ctx!.tracker?.TrackSymbol(fallback, enclosingDeclaration, SymbolFlags.Value);
            }
        }
    }

    private void AddPropertyToElementList(Symbol propertySymbol, List<Node> typeElements)
    {
        var propertyIsReverseMapped = (propertySymbol.CheckFlags & CheckFlags.ReverseMapped) != 0;
        Type propertyType;
        if (ShouldUsePlaceholderForProperty(propertySymbol))
        {
            propertyType = ch.anyType!;
        }
        else
        {
            propertyType = ch.GetNonMissingTypeOfSymbol(propertySymbol);
        }
        var saveEnclosingDeclaration = ctx!.enclosingDeclaration;
        ctx.enclosingDeclaration = null;
        if (Checker.IsLateBoundName(propertySymbol.Name))
        {
            if (propertySymbol.Declarations is not null && propertySymbol.Declarations.Length > 0)
            {
                var decl = propertySymbol.Declarations[0];
                if (ch.HasLateBindableName(decl))
                {
                    if (NodeTests.IsBinaryExpression(decl))
                    {
                        var name = AstUtilities.GetNameOfDeclaration(decl);
                        if (name != null && NodeTests.IsElementAccessExpression(name) && AstUtilities.IsPropertyAccessEntityNameExpression(name.AsElementAccessExpression().ArgumentExpression!, false /*allowJs*/))
                        {
                            TrackComputedName(name.AsElementAccessExpression().ArgumentExpression!, saveEnclosingDeclaration);
                        }
                    }
                    else
                    {
                        TrackComputedName(decl.Name()!.Expression()!, saveEnclosingDeclaration);
                    }
                }
            }
            else
            {
                ctx.tracker?.ReportNonSerializableProperty(ch.SymbolToString(propertySymbol));
            }
        }
        if (propertySymbol.ValueDeclaration is not null)
        {
            ctx.enclosingDeclaration = propertySymbol.ValueDeclaration;
        }
        else if (propertySymbol.Declarations is not null && propertySymbol.Declarations.Length > 0 && propertySymbol.Declarations[0] is not null)
        {
            ctx.enclosingDeclaration = propertySymbol.Declarations[0];
        }
        else
        {
            ctx.enclosingDeclaration = saveEnclosingDeclaration;
        }
        var propertyName = GetPropertyNameNodeForSymbol(propertySymbol);
        ctx.enclosingDeclaration = saveEnclosingDeclaration;
        ctx.approximateLength += AstUtilities.SymbolName(propertySymbol).Length + 1;

        if ((propertySymbol.Flags & SymbolFlags.Accessor) != 0)
        {
            var writeType = ch.GetWriteTypeOfSymbol(propertySymbol);
            if (!ch.IsErrorType(propertyType) && !ch.IsErrorType(writeType))
            {
                var propDeclaration = AstUtilities.GetDeclarationOfKind(propertySymbol, SyntaxKind.PropertyDeclaration);
                if (propertyType != writeType || (propertySymbol.Parent is not null && (propertySymbol.Parent.Flags & SymbolFlags.Class) != 0 && propDeclaration is null))
                {
                    // DEFERRED: getter/setter signature synthesis for accessor properties. Owned by the
                    // signature-serialization wave; a `{ foo: string; }`-style object literal has no accessors.
                    throw new NotImplementedException("nodeBuilder.addPropertyToElementList(accessor)");
                }
                // else: the accessor-keyword fake-getter/setter arm (nodebuilderimpl.go:2526) is likewise deferred.
                if (propertySymbol.Parent is not null && (propertySymbol.Parent.Flags & SymbolFlags.Class) != 0 && propDeclaration is not null
                    && CoreUtil.Find(propDeclaration.ModifierNodes(), m => m.Kind == SyntaxKind.AccessorKeyword) is not null)
                {
                    throw new NotImplementedException("nodeBuilder.addPropertyToElementList(accessorKeyword)");
                }
            }
        }

        Node? optionalToken;
        if ((propertySymbol.Flags & SymbolFlags.Optional) != 0)
        {
            optionalToken = f.NewToken(SyntaxKind.QuestionToken);
        }
        else
        {
            optionalToken = null;
        }
        if ((propertySymbol.Flags & (SymbolFlags.Function | SymbolFlags.Method)) != 0
            && ch.GetPropertiesOfObjectType(propertyType).Length == 0 && !ch.IsReadonlySymbol(propertySymbol))
        {
            var signatures = ch.GetSignaturesOfType(ch.FilterType(propertyType, static t =>
                (t.flags & TypeFlags.Undefined) == 0), SignatureKind.Call);
            foreach (var signature in signatures)
            {
                var methodDeclaration = SignatureToSignatureDeclarationHelper(signature, SyntaxKind.MethodSignature, new SignatureToSignatureDeclarationOptions
                {
                    name = propertyName,
                    questionToken = optionalToken,
                });
                SetCommentRange(methodDeclaration, signature.declaration ?? propertySymbol.ValueDeclaration);
                typeElements.Add(methodDeclaration);
            }
            if (signatures.Length != 0 || optionalToken is null)
            {
                return;
            }
        }
        Node? propertyTypeNode;
        if (ShouldUsePlaceholderForProperty(propertySymbol))
        {
            propertyTypeNode = CreateElidedInformationPlaceholder();
        }
        else
        {
            if (propertyIsReverseMapped)
            {
                ctx.reverseMappedStack.Add(propertySymbol);
            }
            if (propertyType is not null)
            {
                propertyTypeNode = SerializeTypeForDeclarationImpl(null /*declaration*/, propertyType, propertySymbol, true);
            }
            else
            {
                propertyTypeNode = f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
            }
            if (propertyIsReverseMapped)
            {
                ctx.reverseMappedStack.RemoveAt(ctx.reverseMappedStack.Count - 1);
            }
        }

        ModifierList? modifiers = null;
        if (ch.IsReadonlySymbol(propertySymbol))
        {
            modifiers = f.NewModifierList([f.NewModifier(SyntaxKind.ReadonlyKeyword)]);
            ctx.approximateLength += 9;
        }
        var propertySignature = f.NewPropertySignatureDeclaration(modifiers, propertyName, optionalToken, propertyTypeNode, null);

        SetCommentRange(propertySignature, propertySymbol.ValueDeclaration);
        typeElements.Add(propertySignature);
    }

    // nodebuilderimpl.go:2275 — shouldUsePlaceholderForProperty.
    private bool ShouldUsePlaceholderForProperty(Symbol propertySymbol)
    {
        if ((propertySymbol.CheckFlags & CheckFlags.ReverseMapped) == 0)
        {
            return false;
        }
        // (1)
        if (ctx!.reverseMappedStack.Contains(propertySymbol))
        {
            return true;
        }
        // (2)
        if (ctx.reverseMappedStack.Count > 0)
        {
            var last = ctx.reverseMappedStack[ctx.reverseMappedStack.Count - 1];
            if (ch.reverseMappedSymbolLinks.Has(last))
            {
                var links = ch.reverseMappedSymbolLinks.Get(last);
                var propertyType = links.propertyType;
                if (propertyType is not null && (propertyType.objectFlags & ObjectFlags.Anonymous) == 0)
                {
                    return true;
                }
            }
        }
        // (3)
        if (ctx.reverseMappedStack.Count < MaxReverseMappedNestingInspectionDepth)
        {
            return false;
        }
        if (!ch.reverseMappedSymbolLinks.Has(propertySymbol))
        {
            return false;
        }
        var propertyLinks = ch.reverseMappedSymbolLinks.Get(propertySymbol);
        var propMappedType = propertyLinks.mappedType;
        if (propMappedType is null || propMappedType.symbol is null)
        {
            return false;
        }
        for (var i = 0; i < ctx.reverseMappedStack.Count; i++)
        {
            if (i > MaxReverseMappedNestingInspectionDepth)
            {
                break;
            }
            var prop = ctx.reverseMappedStack[ctx.reverseMappedStack.Count - 1 - i];
            if (ch.reverseMappedSymbolLinks.Has(prop))
            {
                var links = ch.reverseMappedSymbolLinks.Get(prop);
                var mappedType = links.mappedType;
                if (mappedType is not null && mappedType.symbol == propMappedType.symbol)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // nodebuilderimpl.go:2081 — indexInfoToObjectComputedNamesOrSignatureDeclaration.
    // PORT-NOTE (deferred): the computed-name component-reuse fast path (cloning `a.b.c` computed names) is
    // owned by the node-reuse wave; falls through to the already-ported indexInfoToIndexSignatureDeclarationHelper.
    // nodebuilderimpl.go — isTriviallySerializableComputedName.
    private bool IsTriviallySerializableComputedName(Node e)
    {
        var name = e.Name();
        if (name == null || !NodeTests.IsComputedPropertyName(name))
        {
            return false;
        }
        var expr = name.Expression();
        if (expr == null || !AstUtilities.IsEntityNameExpression(expr))
        {
            return false;
        }
        return ch.GetEmitResolver()!.IsEntityNameVisible(expr, ctx!.enclosingDeclaration!).Accessibility == global::Ts.Printer.SymbolAccessibility.Accessible;
    }

    private Node[] IndexInfoToObjectComputedNamesOrSignatureDeclaration(IndexInfo indexInfo, Node? typeNode)
    {
        if (indexInfo.components is not null && indexInfo.components.Length > 0)
        {
            // Index info is derived from object or class computed property names (plus explicit named
            // members) - clone those instead of writing out the resulting computed index signature.
            var allComponentComputedNamesSerializable = ctx!.enclosingDeclaration != null && CoreUtil.Every(indexInfo.components, IsTriviallySerializableComputedName);
            if (allComponentComputedNamesSerializable)
            {
                var newComponents = CoreUtil.Filter(indexInfo.components, c => !ch.HasLateBindableName(c));
                var bailed = false;
                var results = CoreUtil.Map(newComponents, ce =>
                {
                    var name = ReuseNode(ce.Name());
                    if (name != null)
                    {
                        // Still need to track visibility even if we've already checked it to paint references as used.
                        TrackComputedName(ce.Name()!.Expression()!, ctx.enclosingDeclaration);
                        ModifierList? mods = null;
                        if (indexInfo.isReadonly)
                        {
                            mods = f.NewModifierList([f.NewModifier(SyntaxKind.ReadonlyKeyword)]);
                        }
                        Node? postfixToken = ce.PostfixToken() != null ? ce.PostfixToken()!.Clone(f) : null;
                        Node? currentTypeNode = typeNode != null ? f.DeepCloneNode(typeNode) : TypeToTypeNodeImpl(ch.GetTypeOfSymbol(ce.Symbol()!));
                        var sig = f.NewPropertySignatureDeclaration(mods, (PropertyName?)name, (TokenNode?)postfixToken, (TypeNode?)currentTypeNode, null);
                        sig.Loc = ce.Loc;
                        return sig;
                    }
                    bailed = true;
                    return null;
                });
                if (!bailed)
                {
                    // !bailed guarantees every entry was produced (non-null).
                    var final = new Node[results!.Length];
                    for (var i = 0; i < results.Length; i++)
                    {
                        final[i] = results[i]!;
                    }
                    return final;
                }
            }
        }
        return [IndexInfoToIndexSignatureDeclarationHelper(indexInfo, typeNode)];
    }

    // nodebuilderimpl.go:2399 — getPropertyNameNodeForSymbol.
    private Node GetPropertyNameNodeForSymbol(Symbol symbol)
    {
        // For hash-private names, clone the original private identifier from the declaration.
        if (symbol.ValueDeclaration is not null)
        {
            var declName = symbol.ValueDeclaration.Name();
            if (declName is not null && NodeTests.IsPrivateIdentifier(declName))
            {
                return f.DeepCloneNode(declName)!;
            }
        }
        var stringNamed = symbol.Declarations is not null && symbol.Declarations.Length != 0 && CoreUtil.Every(symbol.Declarations, IsStringNamed);
        var singleQuote = symbol.Declarations is not null && symbol.Declarations.Length != 0 && CoreUtil.Every(symbol.Declarations, IsSingleQuotedStringNamed);
        var isMethod = (symbol.Flags & SymbolFlags.Method) != 0;
        var fromNameType = GetPropertyNameNodeForSymbolFromNameType(symbol, singleQuote, stringNamed, isMethod);
        if (fromNameType is not null)
        {
            return fromNameType;
        }

        var name = symbol.Name;
        const string privateNamePrefix = InternalSymbolName.Prefix + "#";
        if (name.StartsWith(privateNamePrefix, StringComparison.Ordinal))
        {
            // symbol IDs are unstable - replace #nnn# with #private#
            name = name.Substring(privateNamePrefix.Length);
            name = TrimLeftDigits(name);
            name = "__#private" + name;
        }

        return CreatePropertyNameNodeForIdentifierOrLiteral(name, singleQuote, stringNamed, isMethod, symbol);
    }

    // strings.TrimLeftFunc(name, stringutil.IsDigit) — leading-digit trim.
    private static string TrimLeftDigits(string name)
    {
        var i = 0;
        while (i < name.Length && global::Ts.StringUtil.StringUtil.IsDigit(name[i]))
        {
            i++;
        }
        return name.Substring(i);
    }

    // nodebuilderimpl.go:2428 — getPropertyNameNodeForSymbolFromNameType.
    private Node? GetPropertyNameNodeForSymbolFromNameType(Symbol symbol, bool singleQuote, bool stringNamed, bool isMethod)
    {
        if (!ch.valueSymbolLinks.Has(symbol))
        {
            return null;
        }
        var nameType = ch.valueSymbolLinks.Get(symbol).nameType;
        if (nameType is null)
        {
            return null;
        }
        if ((nameType.flags & TypeFlags.StringOrNumberLiteral) != 0)
        {
            var name = "";
            var value = nameType.AsLiteralType().value;
            if (value is global::Ts.JsNum.Number num)
            {
                name = num.ToString();
            }
            else if (value is string s)
            {
                name = s;
            }
            if (!global::Ts.Scanner.Scanner.IsIdentifierText(name, LanguageVariant.Standard) && (stringNamed || !Checker.IsNumericLiteralName(name)))
            {
                return f.NewStringLiteral(name, singleQuote ? TokenFlags.SingleQuote : TokenFlags.None);
            }
            if (Checker.IsNumericLiteralName(name) && name.StartsWith("-", StringComparison.Ordinal))
            {
                return f.NewComputedPropertyName(f.NewPrefixUnaryExpression(SyntaxKind.MinusToken, f.NewNumericLiteral(name.Substring(1), TokenFlags.None)));
            }
            return CreatePropertyNameNodeForIdentifierOrLiteral(name, singleQuote, stringNamed, isMethod, symbol);
        }
        if ((nameType.flags & TypeFlags.UniqueESSymbol) != 0)
        {
            return f.NewComputedPropertyName(SymbolToExpressionImpl(nameType.AsUniqueESSymbolType().symbol!, SymbolFlags.Value));
        }
        return null;
    }

    // nodebuilderimpl.go:2357 — classifyPropertyName.
    private enum PropertyNameNodeKind
    {
        Identifier,
        NumericLiteral,
        StringLiteral,
    }

    private static PropertyNameNodeKind ClassifyPropertyName(string name, bool stringNamed, bool isMethod)
    {
        if (isMethod && name == "new")
        {
            return PropertyNameNodeKind.StringLiteral;
        }
        if (global::Ts.Scanner.Scanner.IsIdentifierText(name, LanguageVariant.Standard))
        {
            return PropertyNameNodeKind.Identifier;
        }
        return !stringNamed && Checker.IsNumericLiteralName(name) && global::Ts.JsNum.JsNum.FromString(name) >= new global::Ts.JsNum.Number(0)
            ? PropertyNameNodeKind.NumericLiteral
            : PropertyNameNodeKind.StringLiteral;
    }

    // nodebuilderimpl.go:2367 — createPropertyNameNodeForIdentifierOrLiteral.
    private Node CreatePropertyNameNodeForIdentifierOrLiteral(string name, bool singleQuote, bool stringNamed, bool isMethod, Symbol symbol)
    {
        switch (ClassifyPropertyName(name, stringNamed, isMethod))
        {
            case PropertyNameNodeKind.Identifier:
                return NewIdentifier(name, symbol);
            case PropertyNameNodeKind.NumericLiteral:
                return f.NewNumericLiteral(name, TokenFlags.None);
            default:
                return f.NewStringLiteral(name, singleQuote ? TokenFlags.SingleQuote : TokenFlags.None);
        }
    }

    // nodebuilderimpl.go:2378 — isStringNamed.
    private bool IsStringNamed(Node d)
    {
        var name = AstUtilities.GetNameOfDeclaration(d);
        if (name is null)
        {
            return false;
        }
        if (NodeTests.IsComputedPropertyName(name))
        {
            var t = ch.CheckExpression(name.Expression()!);
            return (t.flags & TypeFlags.StringLike) != 0;
        }
        if (NodeTests.IsElementAccessExpression(name))
        {
            var t = ch.CheckExpression(name.AsElementAccessExpression().ArgumentExpression!);
            return (t.flags & TypeFlags.StringLike) != 0;
        }
        return NodeTests.IsStringLiteral(name);
    }

    // nodebuilderimpl.go:2394 — isSingleQuotedStringNamed.
    private bool IsSingleQuotedStringNamed(Node d)
    {
        var name = AstUtilities.GetNameOfDeclaration(d);
        return name is not null && NodeTests.IsStringLiteral(name) && (name.AsStringLiteral().TokenFlags & TokenFlags.SingleQuote) != 0;
    }

    // signatureToSignatureDeclarationHelper (nodebuilderimpl.go:1786) is now REAL in
    // NodeBuilder.Signatures.cs together with the enterSignatureScope / symbolToParameterDeclaration /
    // serializeReturnTypeForSignature / typeParameterToDeclaration machinery.
}

// nodebuilderimpl.go:1780 — SignatureToSignatureDeclarationOptions (options bag for the deferred signature
// helper). Declared here so the helper signature is faithful; fields are unused until that wave lands.
internal sealed class SignatureToSignatureDeclarationOptions
{
    internal Node? name;
    internal Node? questionToken;
    internal Node[]? modifiers;
}
