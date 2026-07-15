// Ported from tsgo internal/checker/nodecopy.go — the AST-reuse recovery-boundary machinery that backs
// tryReuseExistingNodeHelper (node reuse for typeToTypeNodeHelperWithPossibleReusableTypeNode and
// serializeTypeForDeclaration). This file lands the boundary/tracker infrastructure; the large
// getExistingNodeTreeVisitor (the tree transformer this boundary wraps) is the remaining piece.
//
// PORT-NOTE (deviation, AGENTS.md §6): Go wraps the boundary tracker as
// `NewSymbolTrackerImpl(ctx, newWrappingTracker(ctx.tracker, bound))`. The nodebuilder-level
// SymbolTrackerImpl (nodebuilder.go) was not ported (the C# ctx.tracker holds the caller's raw
// ISymbolTracker directly — see NodeBuilderImpl.cs EnterContext), so createRecoveryBoundary sets
// ctx.tracker = WrappingTracker directly. Consequently the tracked-symbol collection and the
// start/endRecoveryScope machinery operate on bound.trackedSymbols (the WrappingTracker's list) rather
// than the SymbolTrackerImpl-written ctx.trackedSymbols; ctx.trackedSymbols is saved/restored for
// faithfulness but stays empty. The observable boundary behavior (defer error reports; on success
// replay tracked symbols to the real tracker; on error bail) is preserved.

using System;
using System.Collections.Generic;
using Ts.Ast;

namespace Ts.Checker;

// nodecopy.go — TrackedSymbolArgs: a deferred TrackSymbol call captured within a recovery boundary.
internal readonly struct TrackedSymbolArgs
{
    internal readonly Symbol Symbol;
    internal readonly Node? EnclosingDeclaration;
    internal readonly SymbolFlags Meaning;

    internal TrackedSymbolArgs(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning)
    {
        Symbol = symbol;
        EnclosingDeclaration = enclosingDeclaration;
        Meaning = meaning;
    }
}

// nodecopy.go:115 — originalRecoveryScopeState.
internal readonly struct OriginalRecoveryScopeState
{
    internal readonly int TrackedSymbolsTop;
    internal readonly int UnreportedErrorsTop;
    internal readonly bool HadError;

    internal OriginalRecoveryScopeState(int trackedSymbolsTop, int unreportedErrorsTop, bool hadError)
    {
        TrackedSymbolsTop = trackedSymbolsTop;
        UnreportedErrorsTop = unreportedErrorsTop;
        HadError = hadError;
    }
}

// nodecopy.go:100 — recoveryBoundary.
internal sealed class RecoveryBoundary
{
    internal readonly NodeBuilderContext ctx;
    internal bool hadError;
    internal readonly List<Action> deferredReports = new();
    internal readonly global::Ts.NodeBuilder.ISymbolTracker? oldTracker;
    internal readonly List<TrackedSymbolArgs> oldTrackedSymbols;
    internal readonly List<TrackedSymbolArgs> trackedSymbols = new();
    internal readonly bool oldEncounteredError;
    internal readonly int oldApproximateLength;

    internal RecoveryBoundary(NodeBuilderContext ctx)
    {
        this.ctx = ctx;
        oldTracker = ctx.tracker;
        oldTrackedSymbols = ctx.trackedSymbols;
        oldEncounteredError = ctx.encounteredError;
        oldApproximateLength = ctx.approximateLength;
    }

    // nodecopy.go:108 — markError.
    internal void MarkError(Action? f)
    {
        hadError = true;
        if (f != null)
        {
            deferredReports.Add(f);
        }
    }

    // nodecopy.go:122 — startRecoveryScope. (Operates on bound.trackedSymbols per the file PORT-NOTE.)
    internal OriginalRecoveryScopeState StartRecoveryScope() =>
        new OriginalRecoveryScopeState(trackedSymbols.Count, deferredReports.Count, hadError);

    // nodecopy.go:128 — endRecoveryScope.
    internal void EndRecoveryScope(OriginalRecoveryScopeState state)
    {
        hadError = state.HadError;
        if (trackedSymbols.Count > state.TrackedSymbolsTop)
        {
            trackedSymbols.RemoveRange(state.TrackedSymbolsTop, trackedSymbols.Count - state.TrackedSymbolsTop);
        }
        if (deferredReports.Count > state.UnreportedErrorsTop)
        {
            deferredReports.RemoveRange(state.UnreportedErrorsTop, deferredReports.Count - state.UnreportedErrorsTop);
        }
    }
}

// nodecopy.go:133 — wrappingTracker: wraps the real tracker, deferring error reports (markError) and
// collecting TrackSymbol calls so the boundary can bail-and-discard or commit-and-replay.
internal sealed class WrappingTracker : global::Ts.NodeBuilder.ISymbolTracker
{
    private readonly global::Ts.NodeBuilder.ISymbolTracker? wrapped;
    private readonly RecoveryBoundary bound;

    internal WrappingTracker(global::Ts.NodeBuilder.ISymbolTracker? wrapped, RecoveryBoundary bound)
    {
        this.wrapped = wrapped;
        this.bound = bound;
    }

    public void PopErrorFallbackNode() => wrapped?.PopErrorFallbackNode();
    public void PushErrorFallbackNode(Node? node) => wrapped?.PushErrorFallbackNode(node);
    public void ReportCyclicStructureError() => bound.MarkError(wrapped == null ? null : wrapped.ReportCyclicStructureError);
    public void ReportInaccessibleThisError() => bound.MarkError(wrapped == null ? null : wrapped.ReportInaccessibleThisError);
    public void ReportInaccessibleUniqueSymbolError() => bound.MarkError(wrapped == null ? null : wrapped.ReportInaccessibleUniqueSymbolError);
    public void ReportInferenceFallback(Node node) => wrapped?.ReportInferenceFallback(node); // Should this also be deferred?
    public void ReportLikelyUnsafeImportRequiredError(string specifier, string symbolName) => bound.MarkError(wrapped == null ? null : () => wrapped.ReportLikelyUnsafeImportRequiredError(specifier, symbolName));
    public void ReportNonSerializableProperty(string propertyName) => bound.MarkError(wrapped == null ? null : () => wrapped.ReportNonSerializableProperty(propertyName));
    public void ReportNonlocalAugmentation(SourceFile containingFile, Symbol parentSymbol, Symbol augmentingSymbol) => wrapped?.ReportNonlocalAugmentation(containingFile, parentSymbol, augmentingSymbol); // Should this also be deferred?
    public void ReportPrivateInBaseOfClassExpression(string propertyName) => bound.MarkError(wrapped == null ? null : () => wrapped.ReportPrivateInBaseOfClassExpression(propertyName));
    public void ReportTruncationError() => wrapped?.ReportTruncationError(); // Should this also be deferred?

    public bool TrackSymbol(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning)
    {
        bound.trackedSymbols.Add(new TrackedSymbolArgs(symbol, enclosingDeclaration, meaning));
        return false;
    }
}

public sealed partial class NodeBuilder
{
    // nodecopy.go:293 — attachSymbolToLeftmostIdentifier (inner closure of getExistingNodeTreeVisitor).
    // Renames the leftmost identifier of an entity name to the symbol's written/typeparameter name.
    private Node AttachSymbolToLeftmostIdentifier(Node leftmost, Node node, Symbol? sym)
    {
        global::Ts.Ast.NodeVisitor vis = null!;
        Node? VisitorFunc(Node n)
        {
            if (n == leftmost)
            {
                Node? name = null;
                if (sym != null)
                {
                    var type_ = ch.GetDeclaredTypeOfSymbol(sym);
                    if ((sym.Flags & SymbolFlags.TypeParameter) != 0)
                    {
                        name = TypeParameterToName(type_);
                    }
                }
                name ??= NewIdentifier(n.Text(), sym);
                name = SetTextRangeLocal(name, n);
                e.AddEmitFlags(name!, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
                return name;
            }
            return SetTextRangeLocal(n.VisitEachChild(vis), n);
        }
        vis = new global::Ts.Ast.NodeVisitor(VisitorFunc, f, default);
        return VisitorFunc(node)!;
    }

    // nodecopy.go:316 — trackExistingEntityName. Returns (introducesError, transformed node, symbol).
    private (bool IntroducesError, Node Node, Symbol? Sym) TrackExistingEntityName(Node node, Node? overrideEnclosing)
    {
        var enclosingDeclaration = ctx!.enclosingDeclaration;
        if (overrideEnclosing != null)
        {
            enclosingDeclaration = overrideEnclosing;
        }
        var introducesError = false;
        var leftmost = global::Ts.Checker.Checker.GetFirstIdentifier(node);
        if (AstUtilities.IsInJSFile(node) && (AstUtilities.IsExportsIdentifier(leftmost) || AstUtilities.IsModuleExportsAccessExpression(leftmost.Parent!) ||
            (NodeTests.IsQualifiedName(leftmost.Parent!) && AstUtilities.IsModuleIdentifier(leftmost.Parent!.AsQualifiedName().Left!) && AstUtilities.IsExportsIdentifier(leftmost.Parent!.AsQualifiedName().Right!))))
        {
            introducesError = true;
            return (introducesError, SetTextRangeLocal(f.DeepCloneNode(node), node)!, null);
        }
        var meaning = global::Ts.Checker.EmitResolver.GetMeaningOfEntityNameReference(node);
        Symbol? sym;
        if (JSDocReparseAstGaps.IsThisIdentifier(leftmost))
        {
            // `this` isn't a bindable identifier - find a relevant `this` symbol directly.
            sym = ch.GetSymbolOfDeclaration(ch.GetThisContainer(leftmost, false, false));
            if (ch.IsSymbolAccessible(sym, leftmost, meaning, false).Accessibility != global::Ts.Printer.SymbolAccessibility.Accessible)
            {
                introducesError = true;
                ctx.tracker?.ReportInaccessibleThisError();
            }
            return (introducesError, AttachSymbolToLeftmostIdentifier(leftmost, node, sym), null);
        }
        sym = ch.ResolveEntityName(leftmost, meaning, true, true, null);
        if (ctx.enclosingDeclaration != null && !(sym != null && (sym.Flags & SymbolFlags.TypeParameter) != 0))
        {
            sym = ch.GetExportSymbolOfValueSymbolIfExported(sym);
            // Some declarations may be transplanted to a new location; ensure the name has the same meaning at both.
            var symAtLocation = ch.ResolveEntityName(leftmost, meaning, true, true, ctx.enclosingDeclaration);
            if (symAtLocation == ch.unknownSymbol ||
                (symAtLocation == null && sym != null) ||
                (symAtLocation != null && sym != null && ch.GetSymbolIfSameReference(ch.GetExportSymbolOfValueSymbolIfExported(symAtLocation)!, sym) == null))
            {
                if (symAtLocation != ch.unknownSymbol)
                {
                    ctx.tracker?.ReportInferenceFallback(node);
                }
                introducesError = true;
                return (introducesError, SetTextRangeLocal(f.DeepCloneNode(node), node)!, sym);
            }
            else
            {
                sym = symAtLocation;
            }
        }
        if (sym != null)
        {
            // If a parameter is resolvable in the current context it is also visible, so no need to go to symbol accessibility.
            if ((sym.Flags & SymbolFlags.FunctionScopedVariable) != 0 && sym.ValueDeclaration != null)
            {
                if (AstUtilities.IsPartOfParameterDeclaration(sym.ValueDeclaration) || NodeTests.IsJSDocParameterTag(sym.ValueDeclaration))
                {
                    return (introducesError, AttachSymbolToLeftmostIdentifier(leftmost, node, sym), null);
                }
            }
            if ((sym.Flags & SymbolFlags.TypeParameter) == 0 && !AstUtilities.IsDeclarationName(node) &&
                ch.IsSymbolAccessible(sym, enclosingDeclaration, meaning, false).Accessibility != global::Ts.Printer.SymbolAccessibility.Accessible)
            {
                ctx.tracker?.ReportInferenceFallback(node);
                introducesError = true;
            }
            else
            {
                ctx.tracker?.TrackSymbol(sym, enclosingDeclaration, meaning);
            }
            return (introducesError, AttachSymbolToLeftmostIdentifier(leftmost, node, sym), null);
        }
        return (introducesError, SetTextRangeLocal(f.DeepCloneNode(node), node)!, null);
    }

    // nodecopy.go:195 — createRecoveryBoundary. (Sets ctx.tracker = WrappingTracker directly; see the
    // file PORT-NOTE on the deferred SymbolTrackerImpl wrap.)
    private RecoveryBoundary CreateRecoveryBoundary()
    {
        ch.CheckNotCanceled();
        var bound = new RecoveryBoundary(ctx!);
        ctx!.tracker = new WrappingTracker(ctx.tracker, bound);
        ctx.trackedSymbols = new List<TrackedSymbolArgs>();
        return bound;
    }

    // nodecopy.go:204 — finalizeBoundary.
    private bool FinalizeBoundary(RecoveryBoundary bound)
    {
        ctx!.tracker = bound.oldTracker;
        ctx.trackedSymbols = bound.oldTrackedSymbols;
        ctx.encounteredError = bound.oldEncounteredError;
        ctx.approximateLength = bound.oldApproximateLength;

        foreach (var f in bound.deferredReports)
        {
            f();
        }
        if (bound.hadError)
        {
            return false;
        }
        foreach (var a in bound.trackedSymbols)
        {
            ctx.tracker?.TrackSymbol(a.Symbol, a.EnclosingDeclaration, a.Meaning);
        }
        return true;
    }
}
