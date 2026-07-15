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
