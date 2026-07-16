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
    // The active getExistingNodeTreeVisitor for the in-flight tryReuseExistingNodeHelper (shared by the
    // tryVisit* helpers, mirroring Go's captured `visitor` closure variable). Single-threaded per builder.
    private global::Ts.Ast.NodeVisitor? reuseVisitor;
    // The active recovery boundary + nonLocalNode flag for the in-flight reuse (Go closure captures).
    private RecoveryBoundary? reuseBound;
    private bool reuseNonLocalNode;

    // nodecopy.go:56 — reuseTypeNode. PORT-NOTE: the walkNodeForExpandability probe (nodecopy.go:64-66)
    // is gated on ctx.maxExpansionDepth >= 0, which is fixed at -1 in this wave (no VerbosityContext /
    // hover expansion — an LS-wave feature); the probe is elided with this note per the ObjectType.cs
    // shouldExpandType precedent.
    private Node? ReuseTypeNode(Node? node)
    {
        if (node == null)
        {
            return node;
        }
        var r = ReuseNode(node);
        if (r != null)
        {
            return r;
        }
        ctx!.tracker?.ReportInferenceFallback(node);
        var t = GetTypeFromTypeNodeNb(node, false);
        return TypeToTypeNodeImpl(t!);
    }

    // nodecopy.go:35 — reuseName.
    private Node? ReuseName(Node? node, bool isMethod)
    {
        var res = ReuseNode(node);
        if (res == null)
        {
            return res;
        }

        if (!AstUtilities.TryGetTextOfPropertyName(res, out string text))
        {
            return res;
        }

        var kind = ClassifyPropertyName(text, NodeTests.IsStringLiteral(res), isMethod);
        if (NodeTests.IsIdentifier(res) && kind == PropertyNameNodeKind.Identifier)
        {
            return res;
        }
        if (NodeTests.IsStringLiteral(res) && kind == PropertyNameNodeKind.StringLiteral)
        {
            return res;
        }

        Node renamed;
        switch (kind)
        {
            case PropertyNameNodeKind.Identifier:
                renamed = NewIdentifier(text, null);
                break;
            case PropertyNameNodeKind.StringLiteral:
                renamed = f.NewStringLiteral(text, TokenFlags.None);
                break;
            default:
                return res;
        }
        e.SetOriginal(renamed, res);
        return SetTextRangeLocal(renamed, res);
    }

    // nodecopy.go:12 — reuseNode.
    private Node? ReuseNode(Node? node) => node == null ? node : TryReuseExistingNodeHelper(node);

    // nodebuilderimpl.go:536 — tryReuseExistingNonParameterTypeNode.
    private Node? TryReuseExistingNonParameterTypeNode(Node existing, Type t, Node? host, Type? annotationType)
    {
        host ??= ctx!.enclosingDeclaration;
        annotationType ??= ch.GetTypeFromTypeNode(existing);
        if (annotationType != null && TypeNodeIsEquivalentToType(host, t, annotationType) && CanReuseExistingJSTypeNode(existing, t))
        {
            var result = TryReuseExistingNodeHelper(existing);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    // nodebuilderimpl.go — typeNodeIsEquivalentToType.
    private bool TypeNodeIsEquivalentToType(Node? annotatedDeclaration, Type t, Type? typeFromTypeNode)
    {
        if (typeFromTypeNode == t)
        {
            return true;
        }
        if (annotatedDeclaration == null)
        {
            return false;
        }
        if (global::Ts.Checker.Checker.IsOptionalDeclaration(annotatedDeclaration))
        {
            return ch.GetTypeWithFacts(t, TypeFacts.NEUndefined) == typeFromTypeNode;
        }
        return false;
    }

    // nodecopy.go:222 — tryReuseExistingNodeHelper.
    private Node? TryReuseExistingNodeHelper(Node existing)
    {
        var bound = CreateRecoveryBoundary();
        var savedVisitor = reuseVisitor;
        var savedBound = reuseBound;
        var savedNonLocal = reuseNonLocalNode;
        reuseBound = bound;
        reuseNonLocalNode = true;
        reuseVisitor = new global::Ts.Ast.NodeVisitor(VisitExistingNodeTreeSymbolsOuter, f, new global::Ts.Ast.NodeVisitorHooks
        {
            VisitNodes = (nodes, v) =>
            {
                var res = v.VisitNodes(nodes);
                if (reuseNonLocalNode && res != null)
                {
                    if (res == nodes)
                    {
                        res = nodes!.Clone(f);
                    }
                    res.Loc = new global::Ts.Core.TextRange(-1, -1);
                }
                return res;
            },
            VisitNode = (n, v) =>
            {
                var oldNonLocalNode = reuseNonLocalNode;
                reuseNonLocalNode = ctx!.enclosingFile == null || ctx.enclosingFile != AstUtilities.GetSourceFileOfNode(e.MostOriginal(n));
                var res = v.VisitNode(n);
                reuseNonLocalNode = oldNonLocalNode;
                return res;
            },
        });
        var transformed = reuseVisitor.VisitNode(existing);
        reuseVisitor = savedVisitor;
        reuseBound = savedBound;
        reuseNonLocalNode = savedNonLocal;
        if (!FinalizeBoundary(bound))
        {
            return null;
        }
        ctx!.approximateLength += existing.End - existing.Pos;
        return transformed;
    }

    // nodecopy.go:828 — the outer visitor callback (bail-on-error + new-scope management around the worker).
    private Node? VisitExistingNodeTreeSymbolsOuter(Node node)
    {
        var bound = reuseBound!;
        // If there was an error in a sibling node bail early, the result will be discarded anyway.
        if (bound.hadError)
        {
            return node;
        }
        var recover_ = bound.StartRecoveryScope();
        var introducesNewScope = AstUtilities.IsFunctionLike(node) || NodeTests.IsMappedTypeNode(node);
        Action? exit = null;
        if (introducesNewScope)
        {
            Symbol[]? paramsList = null;
            Type[]? typeParams = null;
            if (AstUtilities.IsFunctionLike(node))
            {
                var sig = ch.GetSignatureFromDeclaration(node);
                paramsList = sig.parameters;
                typeParams = sig.typeParameters;
            }
            else if (NodeTests.IsConditionalTypeNode(node))
            {
                typeParams = ch.GetInferTypeParameters(node);
            }
            else if (NodeTests.IsMappedTypeNode(node))
            {
                typeParams = [ch.GetDeclaredTypeOfTypeParameter(ch.GetSymbolOfDeclaration(node.AsMappedTypeNode().TypeParameter!)!)];
            }
            exit = EnterNewScope(node, paramsList, typeParams, null, null);
        }
        var result = VisitExistingNodeTreeSymbolsWorker(node);
        exit?.Invoke();

        if (result == node && !AstUtilities.NodeIsSynthesized(node))
        {
            result = f.DeepCloneNode(node); // always clone a new node
        }
        result = SetTextRangeLocal(result, node);

        if (bound.hadError)
        {
            if (AstUtilities.IsTypeNode(node) && !NodeTests.IsTypePredicateNode(node))
            {
                bound.EndRecoveryScope(recover_);
                var t = ch.GetTypeFromTypeNode(node);
                return TypeToTypeNodeImpl(t);
            }
            return SetTextRangeLocal(node.Clone(f), node);
        }
        return result;
    }

    // nodecopy.go:386 — tryVisitIndexedAccess.
    private Node? TryVisitIndexedAccess(Node node)
    {
        var iat = node.AsIndexedAccessTypeNode();
        var resultObjectType = TryVisitSimpleTypeNode(iat.ObjectType!);
        if (resultObjectType == null)
        {
            return null;
        }
        return SetTextRangeLocal(f.UpdateIndexedAccessTypeNode(iat, (TypeNode?)resultObjectType, (TypeNode?)reuseVisitor!.VisitNode(iat.IndexType)), node);
    }

    // nodecopy.go:394 — tryVisitKeyOf.
    private Node? TryVisitKeyOf(Node node)
    {
        var to = node.AsTypeOperatorNode();
        var t = TryVisitSimpleTypeNode(to.Type!);
        if (t == null)
        {
            return null;
        }
        return SetTextRangeLocal(f.UpdateTypeOperatorNode(to, to.Operator, (TypeNode?)t), node);
    }

    // nodecopy.go:402 — tryVisitTypeQuery.
    private Node? TryVisitTypeQuery(Node node)
    {
        var tq = node.AsTypeQueryNode();
        var (introducesError, exprName, _) = TrackExistingEntityName(tq.ExprName!, null);
        if (!introducesError)
        {
            return SetTextRangeLocal(f.UpdateTypeQueryNode(tq, (EntityName?)exprName, reuseVisitor!.VisitNodes(tq.TypeArguments)), node);
        }
        var serializedName = SerializeTypeName(tq.ExprName!, true, reuseVisitor!.VisitNodes(tq.TypeArguments));
        if (serializedName != null)
        {
            return SetTextRangeLocal(serializedName, tq.ExprName);
        }
        return null;
    }

    // nodecopy.go:418 — tryVisitTypeReference.
    private Node? TryVisitTypeReference(Node node)
    {
        if (AstUtilities.IsConstTypeReference(node))
        {
            return null;
        }
        var s = TryGetResolvedSymbolFromTypeNode(node);
        if (s == null)
        {
            return null;
        }
        if ((s.Flags & SymbolFlags.TypeParameter) != 0)
        {
            var declaredType = ch.GetDeclaredTypeOfSymbol(s);
            if (ctx!.mapper != null && ctx.mapper.Map(declaredType) != declaredType)
            {
                return null; // refers to a type parameter remapped by context
            }
        }
        if (!CanReuseExistingJSTypeNode(node, ch.GetTypeFromTypeNode(node)))
        {
            return null;
        }
        var trn = node.AsTypeReferenceNode();
        var (introducesError, newName, _) = TrackExistingEntityName(trn.TypeName!, null);
        if (!introducesError)
        {
            var typeArguments = reuseVisitor!.VisitNodes(trn.TypeArguments);
            return SetTextRangeLocal(f.UpdateTypeReferenceNode(trn, (EntityName?)newName, typeArguments), node);
        }
        else
        {
            var serializedName = SerializeTypeName(trn.TypeName!, false, reuseVisitor!.VisitNodes(trn.TypeArguments));
            if (serializedName != null)
            {
                return SetTextRangeLocal(serializedName, trn.TypeName);
            }
            return null;
        }
    }

    // nodecopy.go:452 — tryVisitSimpleTypeNode.
    private Node? TryVisitSimpleTypeNode(Node node)
    {
        var innerNode = AstUtilities.SkipParentheses(node);
        switch (innerNode.Kind)
        {
            case SyntaxKind.TypeReference:
                return TryVisitTypeReference(innerNode);
            case SyntaxKind.TypeQuery:
                return TryVisitTypeQuery(innerNode);
            case SyntaxKind.IndexedAccessType:
                return TryVisitIndexedAccess(innerNode);
            case SyntaxKind.TypeOperator:
                if (innerNode.AsTypeOperatorNode().Operator == SyntaxKind.KeyOfKeyword)
                {
                    return TryVisitKeyOf(innerNode);
                }
                break;
        }
        return reuseVisitor!.VisitNode(node);
    }

    // nodecopy.go:465 — visitExistingNodeTreeSymbolsWorker (the main dispatch of getExistingNodeTreeVisitor).
    private Node? VisitExistingNodeTreeSymbolsWorker(Node node)
    {
        var bound = reuseBound!;
        var visitor = reuseVisitor!;
        var factory = f;
        // ---- JSDoc handling ----
        if (node.Kind == SyntaxKind.JSDocTypeExpression)
        {
            return visitor.VisitNode(node.AsJSDocTypeExpression().Type);
        }
        if (node.Kind == SyntaxKind.JSDocAllType)
        {
            return factory.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
        }
        if (node.Kind == SyntaxKind.JSDocNullableType)
        {
            return factory.NewUnionTypeNode(factory.NewNodeList([
                visitor.VisitNode(node.AsJSDocNullableType().Type)!,
                factory.NewLiteralTypeNode(factory.NewKeywordExpression(SyntaxKind.NullKeyword)),
            ]));
        }
        if (node.Kind == SyntaxKind.JSDocOptionalType)
        {
            return factory.NewUnionTypeNode(factory.NewNodeList([
                visitor.VisitNode(node.AsJSDocOptionalType().Type)!,
                factory.NewKeywordTypeNode(SyntaxKind.UndefinedKeyword),
            ]));
        }
        if (node.Kind == SyntaxKind.JSDocNonNullableType)
        {
            return visitor.VisitNode(node.AsJSDocNonNullableType().Type);
        }
        if (node.Kind == SyntaxKind.JSDocVariadicType)
        {
            return factory.NewArrayTypeNode(visitor.VisitNode(node.AsJSDocVariadicType().Type));
        }
        if (node.Kind == SyntaxKind.JSDocTypeLiteral)
        {
            var members = new List<Node>();
            foreach (var t in node.AsJSDocTypeLiteral().JSDocPropertyTags ?? [])
            {
                if (t.Kind != SyntaxKind.JSDocPropertyTag && t.Kind != SyntaxKind.JSDocParameterTag)
                {
                    continue;
                }
                var nm = t.Name()!;
                Node? targetName = NodeTests.IsIdentifier(nm) ? nm : nm.AsQualifiedName().Right;
                var mname = visitor.VisitNode(targetName);
                var shouldBeOptional = t.AsJSDocParameterOrPropertyTag().IsBracketed || (t.TypeExpression() != null && t.TypeExpression()!.Kind == SyntaxKind.JSDocOptionalType);
                Node? question = shouldBeOptional ? factory.NewToken(SyntaxKind.QuestionToken) : null;
                var ty = visitor.VisitNode(t.TypeExpression());
                members.Add(factory.NewPropertySignatureDeclaration(null, mname, question, (TypeNode?)ty, null));
            }
            return factory.NewTypeLiteralNode(factory.NewNodeList([.. members]));
        }
        // ---- empty type reference → any ----
        if (NodeTests.IsTypeReferenceNode(node) && NodeTests.IsIdentifier(node.AsTypeReferenceNode().TypeName!) && node.AsTypeReferenceNode().TypeName!.AsIdentifier().Text == "")
        {
            var replacement = factory.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
            e.SetOriginal(replacement, node);
            return replacement;
        }
        if (NodeTests.IsThisTypeNode(node))
        {
            return node;
        }
        if (NodeTests.IsTypeParameterDeclaration(node))
        {
            var (_, newName, _) = TrackExistingEntityName(node.Name()!, null);
            return factory.UpdateTypeParameterDeclaration(
                node.AsTypeParameterDeclaration(),
                visitor.VisitModifiers(node.Modifiers()),
                (Node)newName,
                (TypeNode?)visitor.VisitNode(node.AsTypeParameterDeclaration().Constraint),
                (TypeNode?)visitor.VisitNode(node.AsTypeParameterDeclaration().Expression),
                (TypeNode?)visitor.VisitNode(node.AsTypeParameterDeclaration().DefaultType));
        }
        if (NodeTests.IsIndexedAccessTypeNode(node))
        {
            var result = TryVisitIndexedAccess(node);
            if (result != null)
            {
                return result;
            }
            bound.MarkError(null);
            return node;
        }
        if (NodeTests.IsTypeReferenceNode(node))
        {
            var result = TryVisitTypeReference(node);
            if (result != null)
            {
                return result;
            }
            bound.MarkError(null);
            return node;
        }
        if (NodeTests.IsTypeQueryNode(node))
        {
            var result = TryVisitTypeQuery(node);
            if (result != null)
            {
                return result;
            }
            bound.MarkError(null);
            return node;
        }
        if (NodeTests.IsTypeOperatorNode(node))
        {
            if (node.AsTypeOperatorNode().Operator == SyntaxKind.UniqueKeyword && node.AsTypeOperatorNode().Type!.Kind == SyntaxKind.SymbolKeyword)
            {
                var nonFakeEnclosing = GetEnclosingDeclarationIgnoringFakeScope();
                var sameScope = AstUtilities.FindAncestor(node, a => a == nonFakeEnclosing);
                if (sameScope == null)
                {
                    bound.MarkError(null);
                    return node;
                }
            }
            else if (node.AsTypeOperatorNode().Operator == SyntaxKind.KeyOfKeyword)
            {
                var result = TryVisitKeyOf(node);
                if (result != null)
                {
                    return result;
                }
                bound.MarkError(null);
                return node;
            }
        }
        if (global::Ts.Checker.Checker.IsLiteralImportTypeNode(node))
        {
            var itn = node.AsImportTypeNode();
            if (itn.Attributes != null && itn.Attributes.AsImportAttributes().Token == SyntaxKind.AssertKeyword)
            {
                bound.MarkError(null);
                return node;
            }
            var t = ch.GetTypeFromTypeNode(node);
            if (t == null)
            {
                bound.MarkError(null);
                return node;
            }
            var originalSpec = itn.Argument!.AsLiteralTypeNode().Literal;
            var specifier = RewriteModuleSpecifier(node, originalSpec!);
            if (originalSpec == specifier)
            {
                specifier = visitor.VisitNode(specifier); // visit node if not replaced
            }
            var arg = itn.Argument;
            if (specifier != originalSpec)
            {
                arg = factory.NewLiteralTypeNode(specifier!);
            }
            return factory.UpdateImportTypeNode(
                itn,
                itn.IsTypeOf,
                (TypeNode?)arg,
                (Node?)visitor.VisitNode(itn.Attributes),
                (Node?)visitor.VisitNode(itn.Qualifier),
                visitor.VisitNodes(itn.TypeArguments));
        }
        if (node.Name() != null && node.Name()!.Kind == SyntaxKind.ComputedPropertyName && !ch.HasLateBindableName(node))
        {
            if (!AstUtilities.HasDynamicName(node))
            {
                return visitor.VisitEachChild(node);
            }
            var shouldRemoveDeclaration = !((ctx!.internalFlags & global::Ts.NodeBuilder.InternalFlags.AllowUnresolvedNames) != 0
                && AstUtilities.IsEntityNameExpression(node.Name()!.AsComputedPropertyName().Expression!)
                && (ch.CheckComputedPropertyName(node.Name()!).flags & TypeFlags.Any) != 0);
            if (shouldRemoveDeclaration)
            {
                return null;
            }
        }
        if ((AstUtilities.IsFunctionLike(node) && node.Type() == null)
            || (NodeTests.IsPropertyDeclaration(node) && node.Type() == null && node.Initializer() == null)
            || (NodeTests.IsPropertySignatureDeclaration(node) && node.Type() == null && node.Initializer() == null)
            || (NodeTests.IsParameterDeclaration(node) && node.Type() == null && node.Initializer() == null))
        {
            var visited = visitor.VisitEachChild(node)!;
            if (visited == node)
            {
                visited = SetTextRangeLocal(node.Clone(factory), node)!;
            }
            node = visited;
            var newType = factory.NewKeywordTypeNode(SyntaxKind.AnyKeyword);
            switch (node.Kind)
            {
                case SyntaxKind.PropertyDeclaration:
                    return factory.UpdatePropertyDeclaration(node.AsPropertyDeclaration(), node.Modifiers(), node.Name(), node.PostfixToken(), newType, null);
                case SyntaxKind.PropertySignature:
                    return factory.UpdatePropertySignatureDeclaration(node.AsPropertySignatureDeclaration(), node.Modifiers(), node.Name(), node.PostfixToken(), newType, null);
                case SyntaxKind.Parameter:
                    return factory.UpdateParameterDeclaration(node.AsParameterDeclaration(), null, node.AsParameterDeclaration().DotDotDotToken, node.Name(), node.AsParameterDeclaration().QuestionToken, newType, null);
                case SyntaxKind.MethodSignature:
                    return factory.UpdateMethodSignatureDeclaration(node.AsMethodSignatureDeclaration(), node.Modifiers(), node.Name(), node.AsMethodSignatureDeclaration().PostfixToken, node.AsMethodSignatureDeclaration().TypeParameters, node.AsMethodSignatureDeclaration().Parameters, newType);
                case SyntaxKind.CallSignature:
                    return factory.UpdateCallSignatureDeclaration(node.AsCallSignatureDeclaration(), node.AsCallSignatureDeclaration().TypeParameters, node.AsCallSignatureDeclaration().Parameters, newType);
                case SyntaxKind.JSDocSignature:
                    return factory.UpdateJSDocSignature(node.AsJSDocSignature(), node.AsJSDocSignature().TypeParameters, node.AsJSDocSignature().Parameters, newType);
                case SyntaxKind.ConstructSignature:
                    return factory.UpdateConstructSignatureDeclaration(node.AsConstructSignatureDeclaration(), node.AsConstructSignatureDeclaration().TypeParameters, node.AsConstructSignatureDeclaration().Parameters, newType);
                case SyntaxKind.IndexSignature:
                    return factory.UpdateIndexSignatureDeclaration(node.AsIndexSignatureDeclaration(), node.Modifiers(), node.AsIndexSignatureDeclaration().Parameters, newType);
                case SyntaxKind.FunctionType:
                    return factory.UpdateFunctionTypeNode(node.AsFunctionTypeNode(), node.AsFunctionTypeNode().TypeParameters, node.AsFunctionTypeNode().Parameters, newType);
                case SyntaxKind.ConstructorType:
                    return factory.UpdateConstructorTypeNode(node.AsConstructorTypeNode(), node.Modifiers(), node.AsConstructorTypeNode().TypeParameters, node.AsConstructorTypeNode().Parameters, newType);
            }
        }
        if (NodeTests.IsComputedPropertyName(node) && AstUtilities.IsEntityNameExpression(node.AsComputedPropertyName().Expression!))
        {
            var (introducesError, result, _) = TrackExistingEntityName(node.AsComputedPropertyName().Expression!, null);
            if (!introducesError)
            {
                return factory.UpdateComputedPropertyName(node.AsComputedPropertyName(), result);
            }
            else
            {
                bound.MarkError(null);
                return visitor.VisitEachChild(node);
            }
        }
        if (NodeTests.IsTypePredicateNode(node))
        {
            var tpn = node.AsTypePredicateNode();
            Node? parameterName;
            if (NodeTests.IsIdentifier(tpn.ParameterName!))
            {
                var (introducesError, result, _) = TrackExistingEntityName(tpn.ParameterName!, null);
                if (introducesError)
                {
                    bound.MarkError(null);
                }
                parameterName = result;
            }
            else
            {
                parameterName = tpn.ParameterName!.Clone(factory);
            }
            return factory.UpdateTypePredicateNode(tpn, (Node?)visitor.VisitNode(tpn.AssertsModifier), parameterName, (TypeNode?)visitor.VisitNode(tpn.Type));
        }
        if (NodeTests.IsConditionalTypeNode(node))
        {
            var ctn = node.AsConditionalTypeNode();
            var checkType = visitor.VisitNode(ctn.CheckType);
            var dispose = EnterNewScope(node, null, ch.GetInferTypeParameters(node), null, null);
            var extendsType = visitor.VisitNode(ctn.ExtendsType);
            var trueType = visitor.VisitNode(ctn.TrueType);
            dispose();
            var falseType = visitor.VisitNode(ctn.FalseType);
            return factory.UpdateConditionalTypeNode(ctn, (TypeNode?)checkType, (TypeNode?)extendsType, (TypeNode?)trueType, (TypeNode?)falseType);
        }
        // ---- style applications ----
        if (NodeTests.IsTupleTypeNode(node) || ((ctx!.flags & global::Ts.NodeBuilder.Flags.MultilineObjectLiterals) == 0 && NodeTests.IsTypeLiteralNode(node)) || NodeTests.IsMappedTypeNode(node))
        {
            var res = visitor.VisitEachChild(node);
            if (res == node)
            {
                res = SetTextRangeLocal(res!.Clone(factory), node)!;
            }
            e.AddEmitFlags(res!, global::Ts.Printer.EmitFlags.SingleLine);
            return res;
        }
        if (AstUtilities.IsStringLiteralLike(node))
        {
            var c = node.Clone(f)!;
            if (NodeTests.IsStringLiteral(node) && (ctx!.flags & global::Ts.NodeBuilder.Flags.UseSingleQuotesForStringLiteralType) != 0 && (node.AsStringLiteral().TokenFlags & TokenFlags.SingleQuote) == 0)
            {
                c.AsStringLiteral().TokenFlags ^= TokenFlags.SingleQuote;
            }
            e.AddEmitFlags(c, global::Ts.Printer.EmitFlags.NoAsciiEscaping);
            return c;
        }
        return visitor.VisitEachChild(node);
    }

    // nodecopy.go:234 — getModuleSpecifierOverride.
    private string GetModuleSpecifierOverride(Node parent, Node lit)
    {
        if (ctx!.enclosingFile != AstUtilities.GetSourceFileOfNode(lit))
        {
            ResolutionMode mode = global::Ts.Core.Core.ResolutionModeNone;
            if (parent.AsImportTypeNode().Attributes != null)
            {
                mode = ch.GetResolutionModeOverride(parent.AsImportTypeNode().Attributes!.AsImportAttributes(), false);
            }
            string name = lit.Text();
            string originalName = name;
            Symbol? nodeSymbol = TryGetResolvedSymbolFromTypeNode(parent);
            SymbolFlags meaning = SymbolFlags.Type;
            if (parent.AsImportTypeNode().IsTypeOf)
            {
                meaning = SymbolFlags.Value;
            }
            Symbol? parentSymbol = null;
            if (nodeSymbol != null && ch.IsSymbolAccessible(nodeSymbol, ctx.enclosingDeclaration, meaning, false).Accessibility == global::Ts.Printer.SymbolAccessibility.Accessible)
            {
                parentSymbol = LookupSymbolChain(nodeSymbol, meaning, true)[0];
            }
            if (parentSymbol != null && Checker.IsExternalModuleSymbol(parentSymbol))
            {
                name = GetSpecifierForModuleSymbol(parentSymbol, mode);
            }
            else
            {
                SourceFile? targetFile = ch.GetExternalModuleFileFromDeclaration(parent);
                if (targetFile != null)
                {
                    name = GetSpecifierForModuleSymbol(targetFile.Symbol!, mode);
                }
            }
            if (name.Length > 0 && name.Contains("/node_modules/", StringComparison.Ordinal))
            {
                ctx.encounteredError = true;
                ctx.tracker?.ReportLikelyUnsafeImportRequiredError(name, "");
            }
            if (name != originalName)
            {
                return name;
            }
        }
        return "";
    }

    // nodecopy.go:270 — rewriteModuleSpecifier.
    private Node RewriteModuleSpecifier(Node parent, Node lit)
    {
        string newName = GetModuleSpecifierOverride(parent, lit);
        if (newName.Length == 0)
        {
            return lit;
        }
        Node res = f.NewStringLiteral(newName, TokenFlags.None);
        e.SetOriginal(res, lit);
        return res;
    }

    private Node? GetEnclosingDeclarationIgnoringFakeScope()
    {
        var enc = ctx!.enclosingDeclaration;
        while (enc != null && links.Get(enc).fakeScopeForSignatureDeclaration != null)
        {
            enc = enc.Parent;
        }
        return enc;
    }

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
