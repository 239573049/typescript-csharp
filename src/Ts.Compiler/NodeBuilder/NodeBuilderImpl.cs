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
using System.Linq;
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
    // nodecopy.go — trackedSymbols: symbols the WrappingTracker deferred within a recovery boundary
    // (see NodeBuilder.NodeCopy.cs). Saved/restored across boundaries; stays empty outside one.
    internal List<TrackedSymbolArgs> trackedSymbols = new();

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

    // nodebuilderimpl.go:106 — lazily-initialized (recursive: the visitor's callback is CloneBindingName).
    private NodeVisitor? cloneBindingNameVisitor;

    // state (nodebuilderimpl.go:103)
    private NodeBuilderContext? ctx;

    // nodebuilderimpl.go:96 — pc: the syntactic pseudochecker backing the JS declaration-emit reuse paths.
    private readonly global::Ts.PseudoChecker.PseudoChecker pc;

    // nodebuilder.go:279 / nodebuilderimpl.go:119 — NewNodeBuilder(ch, e) fused with newNodeBuilderImpl.
    // PORT-NOTE: the idToSymbol map is only used by the deferred symbol-chain paths; omitted here.
    internal NodeBuilder(Checker ch, global::Ts.Printer.EmitContext e)
    {
        this.ch = ch;
        this.e = e;
        this.f = e.Factory.AsNodeFactory();
        this.pc = global::Ts.PseudoChecker.PseudoChecker.NewPseudoChecker(ch.strictNullChecks, ch.exactOptionalPropertyTypes);
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
    internal Node? SerializeReturnTypeForSignature(Node signatureDeclaration, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        var signature = ch.GetSignatureFromDeclaration(signatureDeclaration);
        var (_, cleanup) = EnterSignatureScope(signature);
        var result = SerializeReturnTypeForSignature(signature, true);
        cleanup();
        return ExitContext(result);
    }

    // nodebuilder.go:126 — SerializeTypeParametersForSignature.
    internal Node[] SerializeTypeParametersForSignature(Node signatureDeclaration, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        var symbol = ch.GetSymbolOfDeclaration(signatureDeclaration);
        var typeParams = SymbolToTypeParameterDeclarations(symbol!, enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContextSlice(typeParams);
    }

    // nodebuilder.go — SymbolToTypeParameterDeclarations.
    internal Node[] SymbolToTypeParameterDeclarations(Symbol symbol, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContextSlice(SymbolToTypeParameterDeclarationsImpl(symbol));
    }

    // nodebuilderimpl.go:1614 — symbolToTypeParameterDeclarations.
    private Node[] SymbolToTypeParameterDeclarationsImpl(Symbol symbol) =>
        TypeParametersToTypeParameterDeclarations(symbol);

    // nodebuilderimpl.go:1618 — typeParametersToTypeParameterDeclarations.
    private Node[] TypeParametersToTypeParameterDeclarations(Symbol symbol)
    {
        Symbol targetSymbol = ch.GetTargetSymbol(symbol)!;
        if ((targetSymbol.Flags & (SymbolFlags.Class | SymbolFlags.Interface | SymbolFlags.Alias)) != 0)
        {
            var results = new List<Node>();
            var params_ = ch.GetLocalTypeParametersOfClassOrInterfaceOrTypeAlias(symbol);
            foreach (var param in params_ ?? [])
            {
                results.Add(TypeParameterToDeclaration(param));
            }
            return results.ToArray();
        }
        else if ((targetSymbol.Flags & SymbolFlags.Function) != 0)
        {
            var results = new List<Node>();
            foreach (var param in ch.GetTypeParametersFromDeclaration(symbol.ValueDeclaration!) ?? [])
            {
                results.Add(TypeParameterToDeclaration(param));
            }
            return results.ToArray();
        }
        return [];
    }

    // nodebuilder.go:134 — SerializeTypeForDeclaration.
    internal Node? SerializeTypeForDeclaration(Node? declaration, Symbol? symbol, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SerializeTypeForDeclarationImpl(declaration, null, symbol, true));
    }

    // nodebuilder.go:140 — SerializeTypeForExpression.
    internal Node? SerializeTypeForExpression(Node expr, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(SerializeTypeForExpressionImpl(expr));
    }

    // nodebuilderimpl.go:1733 — serializeTypeForExpression.
    private Node? SerializeTypeForExpressionImpl(Node expr)
    {
        // !!! TODO: shim, add node reuse
        var t = ch.InstantiateType(ch.GetWidenedType(ch.GetRegularTypeOfExpression(expr)), ctx!.mapper);
        return TypeToTypeNodeImpl(t);
    }

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

    // nodebuilder.go — TypePredicateToTypePredicateNode.
    internal Node? TypePredicateToTypePredicateNode(TypePredicate typePredicate, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker? tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(TypePredicateToTypePredicateNodeHelper(typePredicate));
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
    internal Node? TryJSTypeNodeToTypeNode(Node node, Node? enclosingDeclaration,
        global::Ts.NodeBuilder.Flags flags, global::Ts.NodeBuilder.InternalFlags internalFlags,
        global::Ts.NodeBuilder.ISymbolTracker tracker)
    {
        EnterContext(enclosingDeclaration, flags, internalFlags, tracker);
        return ExitContext(TryJSTypeNodeToTypeNodeImpl(node));
    }

    // nodecopy.go:20 — tryJSTypeNodeToTypeNode.
    private Node? TryJSTypeNodeToTypeNodeImpl(Node node) => ReuseNode(node);

    // nodebuilderimpl.go:473 — setCommentRange. Copies comments to node for declaration emit when the
    // range's source file is the enclosing one.
    private void SetCommentRange(Node node, Node? range)
    {
        if (range != null && ctx!.enclosingFile != null && ctx.enclosingFile == AstUtilities.GetSourceFileOfNode(range))
        {
            // Copy comments to node for declaration emit
            e.AssignCommentRange(node, range);
        }
    }

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

        Node? result = null;
        bool reportedInferenceFallback = false;
        // !!! expandable hover support
        if (!IsActivelyExpanding() && tryReuse && ctx!.enclosingDeclaration != null && declaration != null &&
            (AstUtilities.IsAccessor(declaration) || (AstUtilities.HasInferredType(declaration) && !AstUtilities.NodeIsSynthesized(declaration) && (t.ObjectFlags & ObjectFlags.RequiresWidening) == 0)))
        {
            var remove = AddSymbolTypeToContext(symbol!, t);
            global::Ts.PseudoChecker.PseudoType? pt;
            if (AstUtilities.IsAccessor(declaration))
            {
                pt = pc.GetTypeOfAccessor(declaration);
            }
            else
            {
                pt = pc.GetTypeOfDeclaration(declaration);
            }
            if ((pt == null || pt.Kind == global::Ts.PseudoChecker.PseudoTypeKind.NoResult) && NodeTests.IsBinaryExpression(declaration))
            {
                var decl = CoreUtil.Find(symbol!.Declarations, HasTypeAnnotation);
                if (decl != null)
                {
                    // Binary expressions have a first-in-wins type annotation system. The first one with an
                    // annotation supplies the type for the rest.
                    pt = pc.GetTypeOfDeclaration(decl);
                }
            }
            bool reportErrors = !ctx.suppressReportInferenceFallback;
            if (PseudoTypeEquivalentToType(pt!, t, !requiresAddingUndefined && (NodeTests.IsParameterDeclaration(declaration) || NodeTests.IsPropertySignatureDeclaration(declaration) || NodeTests.IsPropertyDeclaration(declaration)) && Checker.IsOptionalDeclaration(declaration), reportErrors))
            {
                // !!! TODO: If annotated type node is a reference with insufficient type arguments, we should
                // still fall back to type serialization — see: canReuseTypeNodeAnnotation in strada for context
                var ptt = PseudoTypeToType(pt!);
                if (ptt != null && requiresAddingUndefined && Checker.ContainsNonMissingUndefinedType(ch, t) && !Checker.ContainsNonMissingUndefinedType(ch, ptt))
                {
                    pt = global::Ts.PseudoChecker.PseudoChecker.NewPseudoTypeUnion([pt!, global::Ts.PseudoChecker.PseudoChecker.PseudoTypeUndefined]);
                }
                result = PseudoTypeToNodeWithCheckerFallback(pt!, t);
            }
            else
            {
                // Equivalence failed; if errors from inferred-with-errors pseudo types were
                // reported, note it so we can suppress nested errors during the fallback
                // typeToTypeNode serialization (mirroring the suppression that
                // pseudoTypeToNodeWithCheckerFallback provides).
                reportedInferenceFallback = reportErrors && pt!.Kind == global::Ts.PseudoChecker.PseudoTypeKind.Inferred && pt.AsPseudoTypeInferred().ErrorNodes is { Length: > 0 };
                bool shouldAddUndefined = false;
                if (requiresAddingUndefined)
                {
                    var ptt = PseudoTypeToType(pt!);
                    if (ptt != null)
                    {
                        shouldAddUndefined = !Checker.ContainsNonMissingUndefinedType(ch, ptt);
                    }
                    else
                    {
                        shouldAddUndefined = !global::Ts.PseudoChecker.PseudoChecker.CouldAlreadyReferToUndefinedType(pt!);
                    }
                }
                if (shouldAddUndefined)
                {
                    pt = global::Ts.PseudoChecker.PseudoChecker.NewPseudoTypeUnion([pt!, global::Ts.PseudoChecker.PseudoChecker.PseudoTypeUndefined]);
                    if (PseudoTypeEquivalentToType(pt, t, false, reportErrors))
                    {
                        result = PseudoTypeToNodeWithCheckerFallback(pt, t);
                        reportedInferenceFallback = false;
                    }
                }
            }
            remove();
        }
        if (result == null)
        {
            if (reportedInferenceFallback)
            {
                bool oldSuppress = ctx!.suppressReportInferenceFallback;
                ctx.suppressReportInferenceFallback = true;
                result = TypeToTypeNodeImpl(t);
                ctx.suppressReportInferenceFallback = oldSuppress;
            }
            else
            {
                result = TypeToTypeNodeImpl(t);
            }
        }
        restoreFlags();
        if (result is null)
        {
            return f.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        return result;
    }

    // nodebuilderimpl.go:2156 — hasTypeAnnotation.
    private static bool HasTypeAnnotation(Node? declaration) =>
        declaration != null && declaration.Type() != null;

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
            var specifier = GetSpecifierForModuleSymbol(symbol, global::Ts.Core.Core.ResolutionModeNone);
            ctx!.approximateLength += 2 + specifier.Length;
            return NewStringLiteralEx(specifier, false);
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

    // nodebuilderimpl.go:1243 — getSpecifierForModuleSymbol.
    private string GetSpecifierForModuleSymbol(Symbol symbol, ResolutionMode overrideImportMode)
    {
        Node? file = AstUtilities.GetDeclarationOfKind(symbol, SyntaxKind.SourceFile);
        if (file == null)
        {
            Symbol? equivalentSymbol = CoreUtil.FirstNonNil(symbol.Declarations, d => ch.GetFileSymbolIfFileSymbolExportEqualsContainer(d, symbol));
            if (equivalentSymbol != null)
            {
                file = AstUtilities.GetDeclarationOfKind(equivalentSymbol, SyntaxKind.SourceFile);
            }
        }

        if (file == null)
        {
            if (AstUtilities.IsAmbientModuleSymbolName(symbol.Name))
            {
                return global::Ts.StringUtil.StringUtil.StripQuotes(symbol.Name);
            }
        }
        if (ctx!.enclosingFile == null)
        {
            if (AstUtilities.IsAmbientModuleSymbolName(symbol.Name))
            {
                return global::Ts.StringUtil.StringUtil.StripQuotes(symbol.Name);
            }
            return AstUtilities.GetSourceFileOfModule(symbol)!.FileName();
        }

        Node? enclosingDeclaration = e.MostOriginal(ctx.enclosingDeclaration);
        Node? originalModuleSpecifier = null;
        if (CanHaveModuleSpecifier(enclosingDeclaration))
        {
            originalModuleSpecifier = Checker.TryGetModuleSpecifierFromDeclaration(enclosingDeclaration!);
        }
        SourceFile contextFile = ctx.enclosingFile;
        ResolutionMode resolutionMode = overrideImportMode;
        if (resolutionMode == global::Ts.Core.Core.ResolutionModeNone && originalModuleSpecifier != null)
        {
            resolutionMode = ch.program.GetModeForUsageLocation(contextFile, originalModuleSpecifier);
        }
        else if (resolutionMode == global::Ts.Core.Core.ResolutionModeNone && contextFile != null)
        {
            resolutionMode = ch.program.GetDefaultResolutionModeForFile(contextFile);
        }
        var cacheKey = new global::Ts.Module.ModeAwareCacheKey(contextFile!.Path().Value, resolutionMode);
        NodeBuilderSymbolLinks symLinks = symbolLinks.Get(symbol);
        symLinks.specifierCache ??= new global::Ts.Module.ModeAwareCache<string>();
        if (symLinks.specifierCache.TryGetValue(cacheKey, out string? cached))
        {
            return cached;
        }
        // For declaration bundles, we need to generate absolute paths relative to the common source dir for
        // imports, just like how the declaration emitter does for the ambient module declarations - we can
        // easily accomplish this using the `baseUrl` compiler option (which we would otherwise never use in
        // declaration emit) and a non-relative specifier preference.
        global::Ts.ModuleSpecifiers.ModuleSpecifierGenerationHost host = ch.program;
        CompilerOptions specifierCompilerOptions = ch.compilerOptions;
        var specifierPref = global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.ProjectRelative;
        var endingPref = global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.None;
        if (resolutionMode == global::Ts.Core.Core.ResolutionModeESM)
        {
            endingPref = global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.Js;
        }

        string[] allSpecifiers = global::Ts.ModuleSpecifiers.Specifiers.GetModuleSpecifiers(
            symbol,
            ch,
            specifierCompilerOptions,
            contextFile,
            host,
            new global::Ts.ModuleSpecifiers.UserPreferences
            {
                ImportModuleSpecifierPreference = specifierPref,
                ImportModuleSpecifierEnding = endingPref,
            },
            new global::Ts.ModuleSpecifiers.ModuleSpecifierOptions
            {
                OverrideImportMode = overrideImportMode,
            },
            false /*forAutoImports*/);
        if (allSpecifiers.Length == 0)
        {
            symLinks.specifierCache[cacheKey] = "";
            return "";
        }
        string specifier = allSpecifiers[0];
        symLinks.specifierCache[cacheKey] = specifier;
        return specifier;
    }

    // nodebuilderimpl.go:1166 — canHaveModuleSpecifier.
    private static bool CanHaveModuleSpecifier(Node? node)
    {
        if (node == null)
        {
            return false;
        }
        switch (node.Kind)
        {
            case SyntaxKind.VariableDeclaration:
            case SyntaxKind.BindingElement:
            case SyntaxKind.ImportDeclaration:
            case SyntaxKind.ExportDeclaration:
            case SyntaxKind.ImportEqualsDeclaration:
            case SyntaxKind.ImportClause:
            case SyntaxKind.NamespaceExport:
            case SyntaxKind.NamespaceImport:
            case SyntaxKind.ExportSpecifier:
            case SyntaxKind.ImportSpecifier:
            case SyntaxKind.ImportType:
                return true;
        }
        return false;
    }

    // nodebuilderimpl.go:430 — serializeTypeName.
    private Node? SerializeTypeName(Node node, bool isTypeOf, TypeList? typeArguments)
    {
        var meaning = isTypeOf ? SymbolFlags.Value : SymbolFlags.Type;
        var symbol = ch.ResolveEntityName(node, meaning, true, false, node);
        if (symbol == null)
        {
            return null;
        }
        var resolvedSymbol = symbol;
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            resolvedSymbol = ch.ResolveAlias(symbol).Item1 ?? symbol;
        }
        if (ch.IsSymbolAccessible(symbol, ctx!.enclosingDeclaration, meaning, false).Accessibility != global::Ts.Printer.SymbolAccessibility.Accessible)
        {
            return null;
        }
        return SymbolToTypeNode(resolvedSymbol, meaning, typeArguments);
    }

    // nodebuilderimpl.go:495 — canReuseExistingJSTypeNode.
    private bool CanReuseExistingJSTypeNode(Node existing, Type t) =>
        ch.GetIntendedTypeFromJSDocTypeReference(existing) == null && ExistingTypeNodeIsNotReferenceOrIsReferenceWithCompatibleTypeArgumentCount(existing, t);

    // nodebuilderimpl.go:499 — tryGetResolvedSymbolFromTypeNode.
    private Symbol? TryGetResolvedSymbolFromTypeNode(Node? node)
    {
        if (node == null)
        {
            return null;
        }
        ch.GetTypeFromTypeNode(node); // call to ensure symbol is resolved
        var links = ch.symbolNodeLinks.TryGet(node);
        if (links == null)
        {
            return null;
        }
        return links.resolvedSymbol;
    }

    // nodebuilderimpl.go:511 — existingTypeNodeIsNotReferenceOrIsReferenceWithCompatibleTypeArgumentCount.
    private bool ExistingTypeNodeIsNotReferenceOrIsReferenceWithCompatibleTypeArgumentCount(Node existing, Type t)
    {
        // In JS, you can say something like `Foo` and get a `Foo<any>` implicitly - we don't want to preserve that original `Foo` in these cases, though.
        if ((t.objectFlags & ObjectFlags.Reference) == 0)
        {
            return true;
        }
        if (!NodeTests.IsTypeReferenceNode(existing))
        {
            return true;
        }
        var symbol = TryGetResolvedSymbolFromTypeNode(existing);
        if (symbol == null)
        {
            return true;
        }
        // `type` is a reference type, and `existing` is a type reference node, but we still need to make sure they refer to the _same_ target type
        // before we go comparing their type argument counts.
        var existingTarget = ch.GetDeclaredTypeOfSymbol(symbol);
        if (existingTarget == null || existingTarget != t.AsTypeReference()!.target)
        {
            return true;
        }
        return (existing.TypeArguments()?.Length ?? 0) >= ch.GetMinTypeArgumentCount(t.AsTypeReference()!.target!.AsInterfaceType()!.TypeParameters());
    }

    // nodebuilderimpl.go:1123 — lookupTypeParameterNodes.
    // The WriteTypeParametersInQualifiedName branch (qualified generic names) is deferred, matching
    // LookupExpressionChainTypeArgumentNodes; the common path returns null after guarding the symbol id.
    private TypeParameterList? LookupTypeParameterNodes(Symbol[] chain, int index)
    {
        global::Ts.Core.Debug.Assert(0 <= index && index < chain.Length);
        var symbol = chain[index];
        var symbolId = AstUtilities.GetSymbolId(symbol);
        if (ctx!.typeParameterSymbolList.Contains(symbolId))
        {
            return null;
        }
        ctx.typeParameterSymbolList.Add(symbolId);

        if ((ctx.flags & global::Ts.NodeBuilder.Flags.WriteTypeParametersInQualifiedName) != 0 && index < chain.Length - 1)
        {
            // DEFERRED: lookupInstantiatedTypeArgumentNodes / typeParametersToTypeParameterDeclarations.
            throw new NotImplementedException("nodeBuilder.lookupTypeParameterNodes(WriteTypeParametersInQualifiedName)");
        }
        return null;
    }

    // nodebuilderimpl.go:751 — createAccessFromSymbolChain.
    private Node CreateAccessFromSymbolChain(Symbol[] chain, int index, int stopper, TypeList? overrideTypeArguments)
    {
        TypeList? typeParameterNodes = overrideTypeArguments;
        if (index != chain.Length - 1)
        {
            typeParameterNodes = LookupTypeParameterNodes(chain, index);
        }
        var symbol = chain[index];
        Symbol? parent = null;
        if (index > 0)
        {
            parent = chain[index - 1];
        }

        string symbolName = "";
        if (index == 0)
        {
            ctx!.flags |= global::Ts.NodeBuilder.Flags.InInitialEntityName;
            symbolName = GetNameOfSymbolAsWritten(symbol);
            ctx.approximateLength += symbolName.Length + 1;
            ctx.flags ^= global::Ts.NodeBuilder.Flags.InInitialEntityName;
        }
        else
        {
            // lookup a ref to symbol within parent to handle export aliases
            if (parent != null)
            {
                var exports = ch.GetExportsOfSymbol(parent);
                if (exports != null)
                {
                    // avoid exhaustive iteration in the common case
                    if (symbol.Name != InternalSymbolName.ExportEquals && !global::Ts.Checker.Checker.IsLateBoundName(symbol.Name) &&
                        exports.TryGetValue(symbol.Name, out var res) && res != null && ch.GetSymbolIfSameReference(res, symbol) != null)
                    {
                        symbolName = symbol.Name;
                    }
                    else
                    {
                        var results = new Dictionary<Symbol, string>();
                        foreach (var (name, ex) in exports) // map-iter: 已排序 — sortSymbols below stabilizes the pick
                        {
                            if (ch.GetSymbolIfSameReference(ex, symbol) != null && !global::Ts.Checker.Checker.IsLateBoundName(name) && name != InternalSymbolName.ExportEquals)
                            {
                                results[ex] = name;
                            }
                        }
                        var resultSymbols = results.Keys.ToList();
                        if (resultSymbols.Count > 0)
                        {
                            ch.SortSymbols(resultSymbols, 0, resultSymbols.Count);
                            symbolName = results[resultSymbols[0]];
                        }
                    }
                }
            }
        }

        if (symbolName.Length == 0)
        {
            Node? name = null;
            foreach (var d in symbol.Declarations ?? [])
            {
                name = AstUtilities.GetNameOfDeclaration(d);
                if (name != null)
                {
                    break;
                }
            }
            if (name != null && NodeTests.IsComputedPropertyName(name) && name.Expression() != null && AstUtilities.IsEntityName(name.Expression()!))
            {
                var lhsComputed = CreateAccessFromSymbolChain(chain, index - 1, stopper, overrideTypeArguments);
                if (AstUtilities.IsEntityName(lhsComputed))
                {
                    return f.NewIndexedAccessTypeNode(
                        f.NewParenthesizedTypeNode(f.NewTypeQueryNode(lhsComputed, null)),
                        f.NewTypeQueryNode(name.Expression()!, null));
                }
                return lhsComputed;
            }
            symbolName = GetNameOfSymbolAsWritten(symbol);
        }
        ctx!.approximateLength += symbolName.Length + 1;

        if ((ctx.flags & global::Ts.NodeBuilder.Flags.ForbidIndexedAccessSymbolReferences) == 0 && parent != null &&
            ch.GetMembersOfSymbol(parent) is { } members && members.TryGetValue(symbol.Name, out var memberSym) && memberSym != null &&
            ch.GetSymbolIfSameReference(memberSym, symbol) != null)
        {
            // Should use an indexed access
            var lhsMember = CreateAccessFromSymbolChain(chain, index - 1, stopper, overrideTypeArguments);
            if (NodeTests.IsIndexedAccessTypeNode(lhsMember))
            {
                return f.NewIndexedAccessTypeNode(lhsMember, f.NewLiteralTypeNode(NewStringLiteralEx(symbolName, false)));
            }
            return f.NewIndexedAccessTypeNode(
                f.NewTypeReferenceNode(lhsMember, typeParameterNodes),
                f.NewLiteralTypeNode(NewStringLiteralEx(symbolName, false)));
        }

        var identifier = NewIdentifier(symbolName, symbol);
        e.AddEmitFlags(identifier, global::Ts.Printer.EmitFlags.NoAsciiEscaping);

        if (index > stopper)
        {
            var lhs = CreateAccessFromSymbolChain(chain, index - 1, stopper, overrideTypeArguments);
            if ((ctx.flags & global::Ts.NodeBuilder.Flags.UseInstantiationExpressions) == 0 || (AstUtilities.IsEntityName(lhs) && (typeParameterNodes == null || typeParameterNodes.Nodes.Length == 0)))
            {
                return f.NewQualifiedName(lhs, identifier);
            }
            // DEFERRED: UseInstantiationExpressions path (createAccessExpression unported); the flag is not set on these paths.
            throw new NotImplementedException("nodeBuilder.createAccessFromSymbolChain(UseInstantiationExpressions)");
        }
        return identifier;
    }

    // nodebuilderimpl.go:638 — symbolToTypeNode.
    // PORT-NOTE: getSpecifierForModuleSymbol is still stubbed, so the external-module (ImportTypeNode)
    // branch throws when chain[0] is a non-global-augmentation external module. Reached only for those
    // symbols; the common qualified-name path below (createAccessFromSymbolChain) is fully real.
    private Node? SymbolToTypeNode(Symbol symbol, SymbolFlags mask, TypeList? typeArguments)
    {
        var chain = LookupSymbolChain(symbol, mask, (ctx!.flags & global::Ts.NodeBuilder.Flags.UseAliasDefinedOutsideCurrentScope) == 0);
        if (chain.Length == 0)
        {
            return null;
        }
        var isTypeOf = mask == SymbolFlags.Value;
        if (CoreUtil.Some(chain[0].Declarations, global::Ts.Checker.Checker.HasNonGlobalAugmentationExternalModuleSymbol))
        {
            // module is root, must use `ImportTypeNode`
            Node? nonRootParts = null;
            if (chain.Length > 1)
            {
                nonRootParts = CreateAccessFromSymbolChain(chain, chain.Length - 1, 1, typeArguments);
            }
            var typeParameterNodes = typeArguments ?? LookupTypeParameterNodes(chain, 0);
            var specifier = GetSpecifierForModuleSymbol(chain[0], global::Ts.Core.Core.ResolutionModeNone);
            var lit = f.NewLiteralTypeNode(NewStringLiteralEx(specifier, false));
            ctx.approximateLength += specifier.Length + 10;
            if (nonRootParts == null || AstUtilities.IsEntityName(nonRootParts))
            {
                return f.NewImportTypeNode(isTypeOf, lit, null, nonRootParts, typeParameterNodes);
            }
            var splitNode = GetTopmostIndexedAccessType(nonRootParts.AsIndexedAccessTypeNode());
            var qualifier = splitNode.ObjectType!.AsTypeReferenceNode().TypeName!;
            return f.NewIndexedAccessTypeNode(
                f.NewImportTypeNode(isTypeOf, lit, null, qualifier, typeParameterNodes),
                splitNode.IndexType!);
        }

        var entityName = CreateAccessFromSymbolChain(chain, chain.Length - 1, 0, typeArguments);
        if (NodeTests.IsIndexedAccessTypeNode(entityName))
        {
            return entityName; // Indexed accesses can never be `typeof`
        }
        if (AstUtilities.IsEntityName(entityName))
        {
            if (isTypeOf)
            {
                return f.NewTypeQueryNode(entityName, null);
            }
            return f.NewTypeReferenceNode(entityName, typeArguments);
        }
        if (isTypeOf && NodeTests.IsExpressionWithTypeArguments(entityName))
        {
            var expr = entityName.AsExpressionWithTypeArguments();
            return f.NewTypeQueryNode(f.DeepCloneNode(expr.Expression!), expr.TypeArguments);
        }
        return entityName;
    }

    // nodebuilderimpl.go:846 — getTopmostIndexedAccessType.
    private static IndexedAccessTypeNode GetTopmostIndexedAccessType(IndexedAccessTypeNode node)
    {
        if (NodeTests.IsIndexedAccessTypeNode(node.ObjectType!))
        {
            return GetTopmostIndexedAccessType(node.ObjectType!.AsIndexedAccessTypeNode());
        }
        return node;
    }

    // nodebuilderimpl.go:1074 — sortedSymbolNamePair
    private readonly struct SortedSymbolNamePair(Symbol sym, string name)
    {
        internal readonly Symbol sym = sym;
        internal readonly string name = name;
    }

    // nodebuilderimpl.go:1081 — getSymbolChain.
    // PORT-NOTE: getSpecifierForModuleSymbol is still stubbed; it is reached only when a parent symbol has
    // a non-global-augmentation external-module declaration, which those paths do not hit — non-module
    // parents take the `{symbol, ""}` branch. External-module-parent qualification remains deferred.
    private Symbol[] GetSymbolChain(Symbol symbol, SymbolFlags meaning, bool endOfChain, bool yieldModuleSymbol)
    {
        var accessibleSymbolChain = ch.GetAccessibleSymbolChain(symbol, ctx!.enclosingDeclaration, meaning, (ctx.flags & global::Ts.NodeBuilder.Flags.UseOnlyExternalAliasing) != 0);
        var qualifierMeaning = meaning;
        if (accessibleSymbolChain.Count > 1)
        {
            qualifierMeaning = global::Ts.Checker.Checker.GetQualifiedLeftMeaning(meaning);
        }
        if (accessibleSymbolChain.Count == 0 ||
            ch.NeedsQualification(accessibleSymbolChain[0], ctx.enclosingDeclaration, qualifierMeaning))
        {
            // Go up and add our parent.
            var root = symbol;
            if (accessibleSymbolChain.Count > 0)
            {
                root = accessibleSymbolChain[0];
            }
            var parents = ch.GetContainersOfSymbol(root, ctx.enclosingDeclaration, meaning);
            if (parents.Count > 0)
            {
                var parentSpecifiers = new List<SortedSymbolNamePair>(parents.Count);
                foreach (var parentSym in parents)
                {
                    if (CoreUtil.Some(parentSym.Declarations, global::Ts.Checker.Checker.HasNonGlobalAugmentationExternalModuleSymbol))
                    {
                        parentSpecifiers.Add(new SortedSymbolNamePair(parentSym, GetSpecifierForModuleSymbol(parentSym, global::Ts.Core.Core.ResolutionModeNone)));
                    }
                    else
                    {
                        parentSpecifiers.Add(new SortedSymbolNamePair(parentSym, ""));
                    }
                }
                // slices.SortStableFunc — OrderBy is a stable sort.
                parentSpecifiers = parentSpecifiers.OrderBy(static p => p, Comparer<SortedSymbolNamePair>.Create(SortByBestName)).ToList();
                foreach (var pair in parentSpecifiers)
                {
                    var parent = pair.sym;
                    var parentChain = GetSymbolChain(parent, global::Ts.Checker.Checker.GetQualifiedLeftMeaning(meaning), false, yieldModuleSymbol);
                    if (parentChain.Length > 0)
                    {
                        if (parent.Exports != null &&
                            parent.Exports.TryGetValue(InternalSymbolName.ExportEquals, out var exported) &&
                            ch.GetSymbolIfSameReference(exported, symbol) != null)
                        {
                            // parentChain root _is_ symbol - symbol is a module export=, so it kinda looks like it's own parent
                            // No need to lookup an alias for the symbol in itself
                            accessibleSymbolChain = new List<Symbol>(parentChain);
                            break;
                        }
                        List<Symbol> nextSyms = accessibleSymbolChain;
                        if (nextSyms.Count == 0)
                        {
                            var fallback = ch.GetAliasForSymbolInContainer(parent, symbol) ?? symbol;
                            nextSyms = new List<Symbol> { fallback };
                        }
                        accessibleSymbolChain = new List<Symbol>(parentChain);
                        accessibleSymbolChain.AddRange(nextSyms);
                        break;
                    }
                }
            }
        }
        if (accessibleSymbolChain.Count > 0)
        {
            return accessibleSymbolChain.ToArray();
        }
        if (
            // If this is the last part of outputting the symbol, always output. The cases apply only to parent symbols.
            endOfChain ||
            // If a parent symbol is an anonymous type, don't write it.
            (symbol.Flags & (SymbolFlags.TypeLiteral | SymbolFlags.ObjectLiteral)) == 0)
        {
            // If a parent symbol is an external module, don't write it. (We prefer just `x` vs `"foo/bar".x`.)
            if (!endOfChain && !yieldModuleSymbol && CoreUtil.Some(symbol.Declarations, global::Ts.Checker.Checker.HasNonGlobalAugmentationExternalModuleSymbol))
            {
                return [];
            }
            return [symbol];
        }
        return [];
    }

    // nodebuilderimpl.go:1181 — sortByBestName
    private int SortByBestName(SortedSymbolNamePair a, SortedSymbolNamePair b)
    {
        var specifierA = a.name;
        var specifierB = b.name;
        if (specifierA.Length > 0 && specifierB.Length > 0)
        {
            var isBRelative = global::Ts.TsPath.TsPath.PathIsRelative(specifierB);
            if (global::Ts.TsPath.TsPath.PathIsRelative(specifierA) == isBRelative)
            {
                // Both relative or both non-relative, sort by number of parts
                return global::Ts.ModuleSpecifiers.Compare.CountPathComponents(specifierA) - global::Ts.ModuleSpecifiers.Compare.CountPathComponents(specifierB);
            }
            if (isBRelative)
            {
                // A is non-relative, B is relative: prefer A
                return -1;
            }
            // A is relative, B is non-relative: prefer B
            return 1;
        }
        return ch.compareSymbols(a.sym, b.sym); // must sort symbols for stable ordering
    }

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
