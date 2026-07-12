// Ported from tsgo internal/checker/nodebuilder.go + internal/checker/nodebuilderimpl.go — the
// request-scoped Type/Symbol -> AST-node serializer that backs .d.ts declaration emit (the
// EmitResolver's synthesis half delegates here via NewNodeBuilder(checker, emitContext)).
//
// PLACEMENT (§1.5): In tsgo the NodeBuilder / NodeBuilderImpl / NodeBuilderContext all live in
// `package checker`, so their faithful C# home is namespace Ts.Checker. This file physically lives
// under NodeBuilder/ for organization (per the phase-5 relocation note in NodeBuilder/TypeToTypeNode.cs),
// but declares into Ts.Checker. Every builder method reaches back into the Checker via `ch`; the shared
// Type -> TypeNode synthesis core (intrinsic / literal / union / intersection / named reference) is the
// Checker.TypeToTypeNode(Type, NodeFactory) method landed in Checker.TypeToString.cs, which this builder
// reuses rather than duplicating.
//
// SCOPE (breadth-first, per phase-6 NodeBuilder request-builder wave):
//   FULLY PORTED:
//     * newNodeBuilderImpl + NodeBuilderContext (minimal faithful subset — no verbosity/expansion stack,
//       no truncation accounting, no pseudochecker reuse; these callers pass no VerbosityContext and the
//       .d.ts serialize surface used today does not truncate)
//     * enterContext / exitContext / exitContextSlice
//     * TypeToTypeNode           (nodebuilder.go:267 -> impl typeToTypeNode; delegates to the landed core)
//     * SerializeTypeForDeclaration (nodebuilder.go:134 -> impl serializeTypeForDeclaration; the pseudo-type
//       reuse fast path is deferred and its `tryReuse` guard falls through to typeToTypeNode)
//     * IndexInfoToIndexSignatureDeclaration (nodebuilder.go:111 -> impl indexInfoToIndexSignatureDeclarationHelper)
//   DEFERRED (faithful signatures; documented NotImplementedException — deep machinery owned by later waves):
//     * SerializeReturnTypeForSignature / SerializeTypeParametersForSignature (signature scope +
//       serializeInferredReturnTypeForSignature + symbolToTypeParameterDeclarations)
//     * SymbolToExpression (lookupSymbolChain / getNameOfSymbolAsWritten / module-specifier machinery)
//     * SerializeTypeForExpression / TryJSTypeNodeToTypeNode (expression + JSDoc reuse paths)
//
// The `serializeTypeForDeclaration` type-resolution path calls getWidenedLiteralType, which is a
// throwing stub owned by the widening wave; until that lands, SerializeTypeForDeclaration surfaces that
// wave's NotImplementedException for the t==nil symbol case. TypeToTypeNode(t) needs no widening and is
// exercised directly by the wave's verification test.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

// nodebuilderimpl.go:57 — per-request node builder context. Breadth-first subset: only the fields the
// landed serialize surface reads. (Verbosity/expansion/truncation/type-parameter-name scope state and
// the reverse-mapped/symbol-depth caches are part of the deferred full NodeBuilderImpl port.)
internal sealed class NodeBuilderContext
{
    // nodebuilder.go:41-56 (subset)
    internal global::Ts.NodeBuilder.ISymbolTracker? tracker;
    internal global::Ts.NodeBuilder.Flags flags;
    internal global::Ts.NodeBuilder.InternalFlags internalFlags;
    internal bool encounteredError;
    internal Node? enclosingDeclaration;
    internal SourceFile? enclosingFile;
    internal TypeMapper? mapper;
    // nodebuilderimpl.go:80 — declaration-symbol -> type overrides (populated by the deferred reuse path;
    // read by serializeTypeForDeclaration/serializeReturnTypeForSignature). Empty in this wave.
    internal readonly Dictionary<SymbolId, Type> enclosingSymbolTypes = new();
}

// nodebuilder.go:10 + nodebuilderimpl.go:91 — request-scoped builder. In tsgo the public NodeBuilder
// wrapper (enterContext/exitContext) and the NodeBuilderImpl body are separate structs sharing `ctx`;
// this breadth-first port fuses them into one class (the wrapper's ctxStack is single-depth for the
// non-hover .d.ts callers, so a plain enter/exit around one ctx is faithful).
public sealed class NodeBuilder
{
    // host members (nodebuilderimpl.go:92-96)
    private readonly Checker ch;
    private readonly global::Ts.Printer.EmitContext e;
    private readonly global::Ts.Ast.NodeFactory f;

    // state (nodebuilderimpl.go:103)
    private NodeBuilderContext? ctx;

    // nodebuilder.go:279 / nodebuilderimpl.go:119 — NewNodeBuilder(ch, e) fused with newNodeBuilderImpl.
    // PORT-NOTE: the pseudochecker (`pc`) and idToSymbol map are only used by the deferred reuse /
    // symbol-chain paths; omitted from this breadth-first constructor.
    internal NodeBuilder(Checker ch, global::Ts.Printer.EmitContext e)
    {
        this.ch = ch;
        this.e = e;
        this.f = e.Factory.AsNodeFactory();
    }

    // nodebuilder.go:33 — enterContext. Breadth-first: no verbosity plumbing, no SymbolTrackerImpl wrap
    // (the tracker is stored as-is; the wrapping layer is part of the deferred full port).
    private void EnterContext(Node? enclosingDeclaration, global::Ts.NodeBuilder.Flags flags,
        global::Ts.NodeBuilder.InternalFlags internalFlags, global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        ctx = new NodeBuilderContext
        {
            tracker = tracker,
            flags = flags,
            internalFlags = internalFlags,
            enclosingDeclaration = enclosingDeclaration,
            enclosingFile = enclosingDeclaration is not null
                ? AstUtilities.GetSourceFileOfNode(enclosingDeclaration)
                : null,
        };
    }

    // nodebuilder.go:84 — exitContext.
    private Node? ExitContext(Node? result)
    {
        var encounteredError = ctx!.encounteredError;
        ctx = null;
        if (encounteredError)
        {
            return null;
        }
        return result;
    }

    // nodebuilder.go:94 — exitContextSlice.
    private Node[] ExitContextSlice(Node[] result)
    {
        var encounteredError = ctx!.encounteredError;
        ctx = null;
        if (encounteredError)
        {
            return [];
        }
        return result;
    }

    // nodebuilder.go:111 — IndexInfoToIndexSignatureDeclaration.
    internal Node? IndexInfoToIndexSignatureDeclaration(IndexInfo info, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(IndexInfoToIndexSignatureDeclarationHelper(info, null));
    }

    // nodebuilder.go:117 — SerializeReturnTypeForSignature.
    // DEFERRED: needs enterSignatureScope + serializeInferredReturnTypeForSignature + the pseudo-type
    // reuse path. Faithful signature; body throws until the signature-serialization slice lands.
    internal Node? SerializeReturnTypeForSignature(Node signatureDeclaration, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new NotImplementedException("NodeBuilder.SerializeReturnTypeForSignature");

    // nodebuilder.go:126 — SerializeTypeParametersForSignature.
    // DEFERRED: needs symbolToTypeParameterDeclarations (typeParameterToDeclaration + name scope).
    internal Node[] SerializeTypeParametersForSignature(Node signatureDeclaration, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new NotImplementedException("NodeBuilder.SerializeTypeParametersForSignature");

    // nodebuilder.go:134 — SerializeTypeForDeclaration.
    internal Node? SerializeTypeForDeclaration(Node? declaration, Symbol? symbol, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SerializeTypeForDeclarationImpl(declaration, null, symbol, true));
    }

    // nodebuilder.go:140 — SerializeTypeForExpression.
    // DEFERRED: needs serializeTypeForExpression (getWidenedType + expression-position reuse).
    internal Node? SerializeTypeForExpression(Node expr, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new NotImplementedException("NodeBuilder.SerializeTypeForExpression");

    // nodebuilder.go:231 — SymbolToExpression.
    // DEFERRED: needs lookupSymbolChain / createExpressionFromSymbolChain (module specifiers, property
    // access chains, name-as-written). Used by CreateLiteralConstValue for enum-member initializers.
    internal Node? SymbolToExpression(Symbol symbol, SymbolFlags meaning, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new NotImplementedException("NodeBuilder.SymbolToExpression");

    // nodebuilder.go:267 — TypeToTypeNode.
    internal Node? TypeToTypeNode(Type type, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(TypeToTypeNodeImpl(type));
    }

    // nodebuilder.go:272 — TryJSTypeNodeToTypeNode.
    // DEFERRED: needs tryJSTypeNodeToTypeNode (JSDoc type-node reuse).
    internal Node? TryJSTypeNodeToTypeNode(Node node, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker) =>
        throw new NotImplementedException("NodeBuilder.TryJSTypeNodeToTypeNode");

    // ── impl body (nodebuilderimpl.go) ──────────────────────────────────────────────────────────────

    // nodebuilderimpl.go:3176 — typeToTypeNode. Delegates to the shared Checker synthesis core
    // (Checker.TypeToTypeNode(Type, NodeFactory), landed in Checker.TypeToString.cs), which covers the
    // intrinsic / literal / union / intersection / named-reference slice. The remaining kinds fall through
    // to that core's `any` fallback until the full NodeBuilderImpl port lands.
    private Node TypeToTypeNodeImpl(Type t) => ch.NodeBuilderTypeToTypeNode(t, f);

    // nodebuilderimpl.go:2167 — serializeTypeForDeclaration.
    // PORT-NOTE: the pseudo-type reuse fast path (nodebuilderimpl.go:2209-2255, gated on
    // `!isActivelyExpanding() && tryReuse && enclosingDeclaration != nil && declaration != nil && ...`)
    // requires the pseudochecker + pseudoTypeToNodeWithCheckerFallback machinery and is deferred; the
    // guard falls through to `result = typeToTypeNode(t)`, matching what the Go code produces when reuse
    // is disabled. The addUndefinedForParameter widening path is preserved (getOptionalType).
    private Node SerializeTypeForDeclarationImpl(Node? declaration, Type? t, Symbol? symbol, bool tryReuse)
    {
        if (declaration is null)
        {
            if (symbol is not null)
            {
                declaration = symbol.ValueDeclaration;
                if (declaration is null)
                {
                    // TODO: prefer annotated declarations like in strada (but does this ever even matter
                    // in practice? All callers should supply a declaration!)
                    declaration = CoreUtil.FirstOrNil(symbol.Declarations);
                }
            }
        }
        if (symbol is null)
        {
            symbol = ch.GetSymbolOfDeclaration(declaration!);
        }
        if (t is null)
        {
            t = ctx!.enclosingSymbolTypes.GetValueOrDefault(AstUtilities.GetSymbolId(symbol!));
            if (t is null)
            {
                if ((symbol!.Flags & SymbolFlags.Accessor) != 0 && declaration!.Kind == SyntaxKind.SetAccessor)
                {
                    t = ch.InstantiateType(ch.GetWriteTypeOfSymbol(symbol), ctx.mapper);
                }
                else if (symbol is not null && (symbol.Flags & (SymbolFlags.TypeLiteral | SymbolFlags.Signature)) == 0)
                {
                    t = ch.InstantiateType(ch.GetWidenedLiteralType(ch.GetTypeOfSymbol(symbol)), ctx.mapper);
                }
                else
                {
                    t = ch.errorType;
                }
            }
        }

        // !!! TODO: JSDoc, getEmitResolver call is unfortunate layering for the helper - hoist it into checker
        var requiresAddingUndefined = declaration is not null
            && (NodeTests.IsParameterDeclaration(declaration) || NodeTests.IsPropertySignatureDeclaration(declaration) || NodeTests.IsPropertyDeclaration(declaration))
            && ch.GetEmitResolver().RequiresAddingImplicitUndefined(declaration, symbol!, ctx!.enclosingDeclaration!);
        var addUndefinedForParameter = requiresAddingUndefined && NodeTests.IsParameterDeclaration(declaration!);
        if (addUndefinedForParameter)
        {
            t = ch.GetOptionalType(t!, false);
        }

        var restoreFlags = SaveRestoreFlags();
        if ((t!.flags & TypeFlags.UniqueESSymbol) != 0 && t.symbol == symbol
            && (ctx!.enclosingDeclaration is null || CoreUtil.Some(symbol!.Declarations,
                d => AstUtilities.GetSourceFileOfNode(d) == ctx.enclosingFile)))
        {
            ctx.flags |= global::Ts.NodeBuilder.Flags.AllowUniqueESSymbolType;
        }

        // PORT-NOTE (deferred reuse): the tryReuse pseudo-type fast path is not ported; the guard falls
        // through directly to typeToTypeNode, which is what the Go code emits when reuse yields no node.
        Node? result = TypeToTypeNodeImpl(t);

        restoreFlags();
        if (result is null)
        {
            return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        return result;
    }

    // nodebuilderimpl.go:128 — saveRestoreFlags (flags/internalFlags; `depth` field is part of the
    // deferred expansion state and omitted here).
    private Action SaveRestoreFlags()
    {
        var flags = ctx!.flags;
        var internalFlags = ctx.internalFlags;
        return () =>
        {
            ctx.flags = flags;
            ctx.internalFlags = internalFlags;
        };
    }

    // nodebuilderimpl.go:2132 — indexInfoToIndexSignatureDeclarationHelper.
    private Node IndexInfoToIndexSignatureDeclarationHelper(IndexInfo indexInfo, Node? typeNode)
    {
        var name = GetNameFromIndexInfo(indexInfo);
        var indexerTypeNode = TypeToTypeNodeImpl(indexInfo.keyType);

        var indexingParameter = f.NewParameterDeclaration(null, null, NewIdentifier(name), null, indexerTypeNode, null);
        if (typeNode is null)
        {
            if (indexInfo.valueType is null)
            {
                typeNode = f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
            }
            else
            {
                typeNode = TypeToTypeNodeImpl(indexInfo.valueType);
            }
        }
        if (indexInfo.valueType is null && (ctx!.flags & global::Ts.NodeBuilder.Flags.AllowEmptyIndexInfoType) == 0)
        {
            ctx.encounteredError = true;
        }
        ModifierList? modifiers = null;
        if (indexInfo.isReadonly)
        {
            modifiers = f.NewModifierList([f.NewModifier(SyntaxKind.ReadonlyKeyword)]);
        }
        return f.NewIndexSignatureDeclaration(modifiers, f.NewNodeList([indexingParameter]), typeNode);
    }

    // nodebuilderimpl.go:3514 — newIdentifier. PORT-NOTE: the idToSymbol side-table (inlay-hint support)
    // is part of the deferred full port; this breadth-first identifier carries no symbol association.
    private Node NewIdentifier(string text) => f.NewIdentifier(text);

    // checker.go:27662 — getNameFromIndexInfo.
    private static string GetNameFromIndexInfo(IndexInfo info)
    {
        if (info.declaration is not null)
        {
            return global::Ts.Scanner.Scanner.DeclarationNameToString(info.declaration.ParameterList()!.Nodes[0].Name());
        }
        return "x";
    }
}
