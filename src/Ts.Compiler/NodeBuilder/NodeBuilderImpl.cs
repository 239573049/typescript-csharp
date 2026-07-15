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
    // nodebuilderimpl.go:70 — remappedSymbolReferences: alias smuggling for reused pseudo-type nodes.
    // Populated only by the deferred reuse path; read by getNameOfSymbolAsWritten. Empty in this wave.
    internal readonly Dictionary<SymbolId, Symbol> remappedSymbolReferences = new();
    // nodebuilderimpl.go:78 — typeParameterSymbolList: guards re-emitting a symbol's type parameters within
    // one qualified name. Read/written by lookup*TypeArgumentNodes (only when WriteTypeParametersInQualifiedName).
    internal readonly HashSet<SymbolId> typeParameterSymbolList = new();
    // nodebuilderimpl.go:85-87 — type-parameter naming scopes. PORT-NOTE (shape): Go declares these as
    // collections.CopyOnWriteMap/-Set consumed via EnterScope; the C# COW Scope is a ref struct (05 §5.2)
    // and cannot be captured by the cleanup closure enterNewScope returns, so plain containers are used
    // and CloneNodeBuilderContext snapshot-restores them — identical restore semantics, eager copy.
    internal readonly Dictionary<TypeId, Node> typeParameterNames = new();
    internal readonly HashSet<string> typeParameterNamesByText = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> typeParameterNamesByTextNextNameCount = new(StringComparer.Ordinal);
    // nodebuilderimpl.go:72 — suppressReportInferenceFallback: quiets ReportInferenceFallback while
    // serializing an inferred return type (the tracker call itself lands with the reuse wave).
    internal bool suppressReportInferenceFallback;

    // ── object-type serialization state (nodebuilderimpl.go:57 NodeBuilderContext) ───────────────────
    // These fields back the anonymous/object type-literal path (createAnonymousTypeNode ->
    // createTypeNodeFromObjectType -> createTypeNodesFromResolvedType -> addPropertyToElementList).
    //
    // nodebuilderimpl.go:60 — approximateLength: running length estimate for truncation. Load-bearing only
    // for checkTruncationLength; kept faithful so the truncation guard fires identically to Go.
    internal int approximateLength;
    // nodebuilderimpl.go:61 — maxTruncationLength: verbosity override (0 = use default). No VerbosityContext
    // in the error-message / .d.ts callers, so this stays 0.
    internal int maxTruncationLength;
    // nodebuilderimpl.go:62 — truncating: sticky "already truncated" latch.
    internal bool truncating;
    // nodebuilderimpl.go:66 — depth: expansion depth (only advanced by the deferred hover-expansion path).
    internal int depth;
    // nodebuilderimpl.go:67 — maxExpansionDepth: -1 = no hover expansion. The error-message / .d.ts callers
    // never set a VerbosityContext, so this is fixed at -1 and shouldExpandType always short-circuits false.
    internal int maxExpansionDepth = -1;
    // nodebuilderimpl.go:68 — typeStack: expansion-cycle stack (only used when maxExpansionDepth >= 0).
    internal readonly List<Type> typeStack = new();
    // nodebuilderimpl.go:73 — inferTypeParameters: infer-type-parameter scope (conditional-type path).
    internal Type[]? inferTypeParameters;
    // nodebuilderimpl.go:75 — visitedTypes: circular-reference guard for named anonymous types.
    internal readonly HashSet<TypeId> visitedTypes = new();
    // nodebuilderimpl.go:76 — symbolDepth: per-symbol recursion depth (visitAndTransformType circular guard).
    internal readonly Dictionary<CompositeSymbolIdentity, int> symbolDepth = new();
    // nodebuilderimpl.go:81 — reverseMappedStack: reverse-mapped-property nesting guard.
    internal readonly List<Symbol> reverseMappedStack = new();
    // nodebuilderimpl.go:64 — reportedDiagnostic: set when a nested diagnostic was reported (gates the
    // serialized-types cache in the full port; here it only feeds the deferred cache write).
    internal bool reportedDiagnostic;
}

// nodebuilderimpl.go:24 — CompositeSymbolIdentity. Composite key that lets visitAndTransformType track a
// type by its (constructor-flavored) symbol identity or node identity rather than by TypeId, so that
// distinct instantiations of the same anonymous type share a recursion-depth counter.
internal readonly record struct CompositeSymbolIdentity(bool IsConstructorNode, SymbolId SymbolId, NodeId NodeId);

// nodebuilder.go:10 + nodebuilderimpl.go:91 — request-scoped builder. In tsgo the public NodeBuilder
// wrapper (enterContext/exitContext) and the NodeBuilderImpl body are separate structs sharing `ctx`;
// this breadth-first port fuses them into one class (the wrapper's ctxStack is single-depth for the
// non-hover .d.ts callers, so a plain enter/exit around one ctx is faithful).
public sealed partial class NodeBuilder
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

    // nodebuilderimpl.go:106 — EmitContext(). Exposes the builder's emit context so callers (typeToStringEx)
    // can drive the Printer over the same context the type nodes were synthesized in (emit-flag lookups —
    // SingleLine / NoAsciiEscaping — resolve against it).
    internal global::Ts.Printer.EmitContext EmitContext() => e;

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

    // nodebuilder.go:224 — SymbolToEntityName.
    internal Node? SymbolToEntityName(Symbol symbol, SymbolFlags meaning, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SymbolToNameImpl(symbol, meaning, false));
    }

    // nodebuilder.go:231 — SymbolToExpression. Used by CreateLiteralConstValue for enum-member initializers.
    internal Node? SymbolToExpression(Symbol symbol, SymbolFlags meaning, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SymbolToExpressionImpl(symbol, meaning));
    }

    // nodebuilder.go:236 — SymbolToNode.
    internal Node? SymbolToNode(Symbol symbol, SymbolFlags meaning, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SymbolToNodeImpl(symbol, meaning));
    }

    // nodebuilder.go — SignatureToSignatureDeclaration.
    internal Node? SignatureToSignatureDeclaration(Signature signature, SyntaxKind kind, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SignatureToSignatureDeclarationHelper(signature, kind, null));
    }

    // nodebuilder.go:267 — TypeToTypeNode.
    internal Node? TypeToTypeNode(Type type, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
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

    // nodebuilderimpl.go:3176 — typeToTypeNode. The authoritative dispatch (TypeToTypeNodeCore, in
    // NodeBuilder.ObjectType.cs) handles the object / reference / anonymous arms on the request-scoped
    // builder (with ctx) and delegates the intrinsic / literal / union / named leaf kinds to the landed
    // context-free core in Checker.TypeToString.cs.
    private Node TypeToTypeNodeImpl(Type t) => TypeToTypeNodeCore(t);

    // nodebuilderimpl.go:3187 — isExpanding (free func: maxExpansionDepth >= 0 && len(typeStack) > 0).
    private static bool IsExpanding(NodeBuilderContext ctx) => ctx.maxExpansionDepth >= 0 && ctx.typeStack.Count > 0;

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

    // ── symbol -> node (symbolToString family) ───────────────────────────────────────────────────────
    //
    // SCOPE (breadth-first, per phase-6 symbol-display wave): faithfully ports the leaf of the symbol
    // serializer that the common assignability / "cannot assign to" error messages need — a single-symbol
    // chain rendered by getNameOfSymbolAsWritten. The full lookupSymbolChain qualification path
    // (getSymbolChain -> getAccessibleSymbolChain / needsQualification / getContainersOfSymbol, plus
    // module-specifier import-type synthesis) is only reachable when an enclosingDeclaration is present or
    // FlagsUseFullyQualifiedType is set; symbolToString(symbol) passes neither, so lookupSymbolChainWorker
    // takes its else branch (chain = [symbol]) exactly as ported below. getSymbolChain is a documented
    // deferred stub that only that path would hit.

    // nodebuilderimpl.go:572 — symbolToNode.
    private Node? SymbolToNodeImpl(Symbol symbol, SymbolFlags meaning)
    {
        if ((ctx!.internalFlags & global::Ts.NodeBuilder.InternalFlags.WriteComputedProps) != 0)
        {
            if (symbol.ValueDeclaration is not null)
            {
                var name = AstUtilities.GetNameOfDeclaration(symbol.ValueDeclaration);
                if (name is not null && NodeTests.IsComputedPropertyName(name))
                {
                    return name;
                }
            }
            if (ch.valueSymbolLinks.Has(symbol))
            {
                var nameType = ch.valueSymbolLinks.Get(symbol).nameType;
                if (nameType is not null && (nameType.flags & (TypeFlags.EnumLiteral | TypeFlags.UniqueESSymbol)) != 0)
                {
                    var oldEnclosing = ctx.enclosingDeclaration;
                    ctx.enclosingDeclaration = nameType.symbol!.ValueDeclaration;
                    var result = f.NewComputedPropertyName(SymbolToExpressionImpl(nameType.symbol, meaning));
                    ctx.enclosingDeclaration = oldEnclosing;
                    return result;
                }
            }
        }
        return SymbolToExpressionImpl(symbol, meaning);
    }

    // nodebuilderimpl.go:594 — symbolToName.
    private Node SymbolToNameImpl(Symbol symbol, SymbolFlags meaning, bool expectsIdentifier)
    {
        var chain = LookupSymbolChain(symbol, meaning, false);
        if (expectsIdentifier && chain.Length != 1 && !ctx!.encounteredError
            && (ctx.flags & global::Ts.NodeBuilder.Flags.AllowQualifiedNameInPlaceOfIdentifier) != 0)
        {
            ctx.encounteredError = true;
        }
        return CreateEntityNameFromSymbolChain(chain, chain.Length - 1);
    }

    // nodebuilderimpl.go:602 — createEntityNameFromSymbolChain.
    private Node CreateEntityNameFromSymbolChain(Symbol[] chain, int index)
    {
        var symbol = chain[index];

        if (index == 0)
        {
            ctx!.flags |= global::Ts.NodeBuilder.Flags.InInitialEntityName;
        }
        var symbolName = GetNameOfSymbolAsWritten(symbol);
        if (index == 0)
        {
            ctx!.flags ^= global::Ts.NodeBuilder.Flags.InInitialEntityName;
        }

        var identifier = NewIdentifier(symbolName, symbol);
        e.AddEmitFlags(identifier, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
        if (index > 0)
        {
            return f.NewQualifiedName(CreateEntityNameFromSymbolChain(chain, index - 1), identifier);
        }
        return identifier;
    }

    // nodebuilderimpl.go:847 — symbolToExpression.
    private Node SymbolToExpressionImpl(Symbol symbol, SymbolFlags mask)
    {
        var chain = LookupSymbolChain(symbol, mask, false);
        return CreateExpressionFromSymbolChain(chain, chain.Length - 1);
    }

    // nodebuilderimpl.go:852 — createExpressionFromSymbolChain.
    private Node CreateExpressionFromSymbolChain(Symbol[] chain, int index)
    {
        var typeParameterNodes = LookupExpressionChainTypeArgumentNodes(chain, index);
        var symbol = chain[index];

        if (index == 0)
        {
            ctx!.flags |= global::Ts.NodeBuilder.Flags.InInitialEntityName;
        }
        var symbolName = GetNameOfSymbolAsWritten(symbol);
        if (index == 0)
        {
            ctx!.flags ^= global::Ts.NodeBuilder.Flags.InInitialEntityName;
        }

        if (StartsWithSingleOrDoubleQuote(symbolName) && CoreUtil.Some(symbol.Declarations, HasNonGlobalAugmentationExternalModuleSymbol))
        {
            // DEFERRED: getSpecifierForModuleSymbol (module-specifier synthesis). Reached only for module
            // symbols, which the symbolToString error paths in this wave do not exercise.
            throw new NotImplementedException("nodeBuilder.getSpecifierForModuleSymbol");
        }

        if (index == 0 || CanUsePropertyAccess(symbolName))
        {
            var identifier = NewIdentifier(symbolName, symbol);
            e.AddEmitFlags(identifier, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
            if (index > 0)
            {
                var result = f.NewPropertyAccessExpression(CreateExpressionFromSymbolChain(chain, index - 1), null, identifier, NodeFlags.None);
                e.AddEmitFlags(result, global::Ts.Printer.EmitFlags.NoIndentation);
                return CreateExpressionWithTypeArguments(result, typeParameterNodes);
            }
            return CreateExpressionWithTypeArguments(identifier, typeParameterNodes);
        }

        if (StartsWithSquareBracket(symbolName))
        {
            symbolName = symbolName[1..^1];
        }

        Node? expression = null;
        if (StartsWithSingleOrDoubleQuote(symbolName) && (symbol.Flags & SymbolFlags.EnumMember) == 0)
        {
            var literalText = global::Ts.StringUtil.StringUtil.UnquoteString(symbolName);
            expression = NewStringLiteralEx(literalText, symbolName[0] == '\'');
        }
        else if (global::Ts.JsNum.JsNum.FromString(symbolName).ToString() == symbolName)
        {
            expression = f.NewNumericLiteral(symbolName, TokenFlags.None);
        }
        if (expression is null)
        {
            expression = NewIdentifier(symbolName, symbol);
            e.AddEmitFlags(expression, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
        }
        return CreateExpressionWithTypeArguments(
            f.NewElementAccessExpression(CreateExpressionFromSymbolChain(chain, index - 1), null, expression, NodeFlags.None),
            typeParameterNodes);
    }

    // nodebuilderimpl.go:906 — canUsePropertyAccess.
    private static bool CanUsePropertyAccess(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }
        if (name.StartsWith("#", StringComparison.Ordinal))
        {
            return name.Length > 1 && global::Ts.Scanner.Scanner.IsIdentifierText(name[1..], LanguageVariant.Standard);
        }
        return global::Ts.Scanner.Scanner.IsIdentifierText(name, LanguageVariant.Standard);
    }

    // nodebuilderimpl.go:918 — startsWithSingleOrDoubleQuote.
    private static bool StartsWithSingleOrDoubleQuote(string str) =>
        str.StartsWith("'", StringComparison.Ordinal) || str.StartsWith("\"", StringComparison.Ordinal);

    // nodebuilderimpl.go:922 — startsWithSquareBracket.
    private static bool StartsWithSquareBracket(string str) => str.StartsWith("[", StringComparison.Ordinal);

    // symbolaccessibility.go:105 — hasNonGlobalAugmentationExternalModuleSymbol.
    private static bool HasNonGlobalAugmentationExternalModuleSymbol(Node declaration) =>
        AstUtilities.IsModuleWithStringLiteralName(declaration)
        || (declaration.Kind == SyntaxKind.SourceFile && AstUtilities.IsExternalOrCommonJSModule(declaration.AsSourceFile()));

    // nodebuilderimpl.go:967 — getNameOfSymbolAsWritten.
    private string GetNameOfSymbolAsWritten(Symbol symbol)
    {
        if (ctx!.remappedSymbolReferences.TryGetValue(AstUtilities.GetSymbolId(symbol), out var remapped))
        {
            symbol = remapped;
        }
        if (symbol.Name == InternalSymbolName.Default && (ctx.flags & global::Ts.NodeBuilder.Flags.UseAliasDefinedOutsideCurrentScope) == 0
            && ((ctx.flags & global::Ts.NodeBuilder.Flags.InInitialEntityName) == 0
                || symbol.Declarations.Len() == 0
                || (ctx.enclosingDeclaration is not null
                    && AstUtilities.FindAncestor(symbol.Declarations![0], IsDefaultBindingContext)
                        != AstUtilities.FindAncestor(ctx.enclosingDeclaration, IsDefaultBindingContext))))
        {
            return "default";
        }
        if (symbol.Declarations.Len() > 0)
        {
            var name = CoreUtil.FirstNonNil(symbol.Declarations, AstUtilities.GetNameOfDeclaration); // Try using a declaration with a name, first
            if (name is not null)
            {
                if (NodeTests.IsComputedPropertyName(name) && (symbol.CheckFlags & CheckFlags.Late) == 0)
                {
                    if (ch.valueSymbolLinks.Has(symbol) && ch.valueSymbolLinks.Get(symbol).nameType is not null
                        && (ch.valueSymbolLinks.Get(symbol).nameType!.flags & TypeFlags.StringOrNumberLiteral) != 0)
                    {
                        var fromNameType = GetNameOfSymbolFromNameType(symbol);
                        if (fromNameType.Length > 0)
                        {
                            return fromNameType;
                        }
                    }
                }
                return global::Ts.Scanner.Scanner.DeclarationNameToString(name);
            }
            var declaration = symbol.Declarations![0]; // Declaration may be nameless, but we'll try anyway
            if (declaration.Parent is not null && declaration.Parent.Kind == SyntaxKind.VariableDeclaration)
            {
                return global::Ts.Scanner.Scanner.DeclarationNameToString(declaration.Parent.AsVariableDeclaration().Name());
            }
            if (NodeTests.IsClassExpression(declaration) || NodeTests.IsFunctionExpression(declaration) || NodeTests.IsArrowFunction(declaration))
            {
                if (!ctx.encounteredError && (ctx.flags & global::Ts.NodeBuilder.Flags.AllowAnonymousIdentifier) == 0)
                {
                    ctx.encounteredError = true;
                }
                switch (declaration.Kind)
                {
                    case SyntaxKind.ClassExpression:
                        return "(Anonymous class)";
                    case SyntaxKind.FunctionExpression:
                    case SyntaxKind.ArrowFunction:
                        return "(Anonymous function)";
                }
            }
        }
        var nameFromType = GetNameOfSymbolFromNameType(symbol);
        if (nameFromType.Length > 0)
        {
            return nameFromType;
        }
        return AstUtilities.EscapeInternalSymbolName(symbol.Name);
    }

    // nodebuilderimpl.go:930 — getNameOfSymbolFromNameType.
    private string GetNameOfSymbolFromNameType(Symbol symbol)
    {
        if (ch.valueSymbolLinks.Has(symbol))
        {
            var nameType = ch.valueSymbolLinks.Get(symbol).nameType;
            if (nameType is null)
            {
                return "";
            }
            if ((nameType.flags & TypeFlags.StringOrNumberLiteral) != 0)
            {
                var name = "";
                var value = nameType.AsLiteralType().value;
                if (value is string s)
                {
                    name = s;
                }
                else if (value is global::Ts.JsNum.Number num)
                {
                    name = num.ToString();
                }
                if (!global::Ts.Scanner.Scanner.IsIdentifierText(name, LanguageVariant.Standard) && !Checker.IsNumericLiteralName(name))
                {
                    return ch.ValueToString(nameType.AsLiteralType().value);
                }
                if (Checker.IsNumericLiteralName(name) && name.StartsWith("-", StringComparison.Ordinal))
                {
                    return "[" + name + "]";
                }
                return name;
            }
            if ((nameType.flags & TypeFlags.UniqueESSymbol) != 0)
            {
                var text = GetNameOfSymbolAsWritten(nameType.AsUniqueESSymbolType().symbol!);
                return "[" + text + "]";
            }
        }
        return "";
    }

    // nodebuilderimpl.go:983 — isDefaultBindingContext.
    private static bool IsDefaultBindingContext(Node location) =>
        location.Kind == SyntaxKind.SourceFile || AstUtilities.IsAmbientModule(location);

    // nodebuilderimpl.go:1055 — lookupSymbolChain.
    private Symbol[] LookupSymbolChain(Symbol symbol, SymbolFlags meaning, bool yieldModuleSymbol)
    {
        ctx!.tracker?.TrackSymbol(symbol, ctx.enclosingDeclaration, meaning);
        return LookupSymbolChainWorker(symbol, meaning, yieldModuleSymbol);
    }

    // nodebuilderimpl.go:1060 — lookupSymbolChainWorker.
    private Symbol[] LookupSymbolChainWorker(Symbol symbol, SymbolFlags meaning, bool yieldModuleSymbol)
    {
        // Try to get qualified name if the symbol is not a type parameter and there is an enclosing declaration.
        var isTypeParameter = (symbol.Flags & SymbolFlags.TypeParameter) != 0;
        if (!isTypeParameter
            && (ctx!.enclosingDeclaration is not null || (ctx.flags & global::Ts.NodeBuilder.Flags.UseFullyQualifiedType) != 0)
            && (ctx.internalFlags & global::Ts.NodeBuilder.InternalFlags.DoNotIncludeSymbolChain) == 0)
        {
            var chain = GetSymbolChain(symbol, meaning, true, yieldModuleSymbol);
            global::Ts.Core.Debug.Assert(chain is not null);
            global::Ts.Core.Debug.Assert(chain!.Length > 0);
            return chain;
        }
        return [symbol];
    }

    // nodebuilderimpl.go:1081 — getSymbolChain.
    // DEFERRED: the qualified-name path needs getAccessibleSymbolChain / needsQualification /
    // getContainersOfSymbol / getSpecifierForModuleSymbol (the symbolaccessibility.go someSymbolTableInScope
    // machinery). Reached only when an enclosingDeclaration is present or FlagsUseFullyQualifiedType is set;
    // the symbolToString error paths in this wave pass neither, so lookupSymbolChainWorker never calls this.
    private Symbol[] GetSymbolChain(Symbol symbol, SymbolFlags meaning, bool endOfChain, bool yieldModuleSymbol) =>
        throw new NotImplementedException("nodeBuilder.getSymbolChain");

    // nodebuilderimpl.go:3563 — lookupExpressionChainTypeArgumentNodes.
    // PORT-NOTE: only WriteTypeParametersInQualifiedName (qualified generic names) exercises the body; the
    // symbolToString error paths do not set that flag, so this returns null (no smuggled type arguments).
    private TypeList? LookupExpressionChainTypeArgumentNodes(Symbol[] chain, int index)
    {
        if ((ctx!.flags & global::Ts.NodeBuilder.Flags.WriteTypeParametersInQualifiedName) != 0 && index < chain.Length - 1)
        {
            // DEFERRED: lookupInstantiatedTypeArgumentNodes / typeParametersToTypeParameterDeclarations.
            throw new NotImplementedException("nodeBuilder.lookupExpressionChainTypeArgumentNodes");
        }
        return null;
    }

    // nodebuilderimpl.go:3533 — createExpressionWithTypeArguments.
    private Node CreateExpressionWithTypeArguments(Node expr, TypeList? typeArguments)
    {
        if (typeArguments is null || typeArguments.Nodes.Length == 0)
        {
            return expr;
        }
        return f.NewExpressionWithTypeArguments(expr, typeArguments);
    }

    // nodebuilderimpl.go:3499 — newStringLiteralEx.
    private Node NewStringLiteralEx(string text, bool isSingleQuote)
    {
        var flags = TokenFlags.None;
        if (isSingleQuote || (ctx!.flags & global::Ts.NodeBuilder.Flags.UseSingleQuotesForStringLiteralType) != 0)
        {
            flags |= TokenFlags.SingleQuote;
        }
        return f.NewStringLiteral(text, flags);
    }

    // nodebuilderimpl.go:3514 — newIdentifier. PORT-NOTE: the idToSymbol side-table (inlay-hint support) is
    // part of the deferred full port; this identifier carries no symbol association.
    private Node NewIdentifier(string text, Symbol? symbol) => f.NewIdentifier(text);
}
