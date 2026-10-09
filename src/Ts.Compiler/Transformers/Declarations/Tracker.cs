// Ported from tsgo internal/transformers/declarations/tracker.go.

using System;
using System.Collections.Generic;
using global::Ts.Ast;
using global::Ts.NodeBuilder;
using global::Ts.Printer;

namespace Ts.Transformers.Declarations;

public sealed class SymbolTrackerImpl : ISymbolTracker
{
    private readonly EmitResolver? resolver;
    private readonly SymbolTrackerSharedState state;
    private readonly IDeclarationEmitHost? host;
    private readonly List<Node?> fallbackStack = [];

    // For detecting class expression self-references during member serialization.
    private Symbol? watchedClassSymbol;
    private bool classSymbolTracked;

    // tracker.go:23 — produces the isolatedDeclarations error for a node (diagnostics.go:682).
    private readonly Func<Node, Diagnostic> getIsolatedDeclarationError;

    internal SymbolTrackerImpl(
        IDeclarationEmitHost? host,
        EmitResolver? resolver,
        SymbolTrackerSharedState state)
    {
        this.host = host;
        this.resolver = resolver;
        this.state = state;
        // tracker.go:250 — NewSymbolTracker.
        getIsolatedDeclarationError = DeclarationDiagnostics.CreateGetIsolatedDeclarationErrors(resolver);
    }

    public void PopErrorFallbackNode()
    {
        if (fallbackStack.Count != 0)
        {
            fallbackStack.RemoveAt(fallbackStack.Count - 1);
        }
    }

    public void PushErrorFallbackNode(Node? node) => fallbackStack.Add(node);

    public void ReportCyclicStructureError()
    {
        AddDiagnostic(
            global::Ts.Diagnostics.Diagnostics.The_inferred_type_of_0_references_a_type_with_a_cyclic_structure_which_cannot_be_trivially_serialized_A_type_annotation_is_necessary,
            ErrorDeclarationNameWithFallback());
    }

    public void ReportInaccessibleThisError()
    {
        AddDiagnostic(
            global::Ts.Diagnostics.Diagnostics.The_inferred_type_of_0_references_an_inaccessible_1_type_A_type_annotation_is_necessary,
            ErrorDeclarationNameWithFallback(),
            "this");
    }

    public void ReportInaccessibleUniqueSymbolError()
    {
        AddDiagnostic(
            global::Ts.Diagnostics.Diagnostics.The_inferred_type_of_0_references_an_inaccessible_1_type_A_type_annotation_is_necessary,
            ErrorDeclarationNameWithFallback(),
            "unique symbol");
    }

    // tracker.go:60-69 — isBoundExpando.
    private bool IsBoundExpando(Node node)
    {
        if (!(global::Ts.Checker.Checker.IsExpandoPropertyDeclaration(node) && NodeTests.IsPropertyAccessExpression(node.AsBinaryExpression().Left!)))
        {
            return false;
        }
        var @ref = resolver!.GetReferencedValueDeclarationUnsafe(
            global::Ts.Ast.Precedence.GetLeftmostExpression(node.AsBinaryExpression().Left!, true));
        if (@ref == null)
        {
            return false;
        }
        return resolver!.IsExpandoFunctionDeclarationUnsafe(@ref!);
    }

    // tracker.go:71-78 — isChildOfBoundExpando.
    private bool IsChildOfBoundExpando(Node node)
    {
        return global::Ts.Ast.AstUtilities.FindAncestorOrQuit(node, n =>
        {
            if (NodeTests.IsSourceFile(n) || NodeTests.IsBlock(n))
            {
                return FindAncestorResult.Quit;
            }
            return global::Ts.Ast.AstUtilities.ToFindAncestorResult(IsBoundExpando(n));
        }) != null;
    }

    // tracker.go:80-94 — ReportInferenceFallback.
    public void ReportInferenceFallback(Node node)
    {
        if (!state.IsolatedDeclarations)
        {
            return;
        }
        if (global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node) != state.CurrentSourceFile)
        {
            return; // Nested error on a declaration in another file - ignore, will be reemitted if file is in the output file set
        }
        if (state.Resolver!.IsExpandoFunctionDeclarationUnsafe(node))
        { // within a node builder call that should already lock the checker, use the unsafe call
            state.ReportExpandoFunctionErrors?.Invoke(node);
        }
        if (!IsChildOfBoundExpando(node))
        { // expando props get an error when their host is visited by the above, this prevents a follow-on error on a non-inferrable expression
            state.AddDiagnostic(getIsolatedDeclarationError(node));
        }
    }

    public void ReportLikelyUnsafeImportRequiredError(string specifier, string symbolName)
    {
        if (symbolName.Length != 0)
        {
            AddDiagnostic(
                global::Ts.Diagnostics.Diagnostics.The_inferred_type_of_0_cannot_be_named_without_a_reference_to_2_from_1_This_is_likely_not_portable_A_type_annotation_is_necessary,
                ErrorDeclarationNameWithFallback(),
                specifier,
                symbolName);
        }
        else
        {
            AddDiagnostic(
                global::Ts.Diagnostics.Diagnostics.The_inferred_type_of_0_cannot_be_named_without_a_reference_to_1_This_is_likely_not_portable_A_type_annotation_is_necessary,
                ErrorDeclarationNameWithFallback(),
                specifier);
        }
    }

    public void ReportNonSerializableProperty(string propertyName)
    {
        AddDiagnostic(
            global::Ts.Diagnostics.Diagnostics.The_type_of_this_node_cannot_be_serialized_because_its_property_0_cannot_be_serialized,
            propertyName);
    }

    public void ReportNonlocalAugmentation(
        SourceFile containingFile,
        Symbol parentSymbol,
        Symbol augmentingSymbol)
    {
        Node? primaryDeclaration = null;
        foreach (var declaration in parentSymbol.Declarations ?? [])
        {
            if (global::Ts.Ast.AstUtilities.GetSourceFileOfNode(declaration) == containingFile)
            {
                primaryDeclaration = declaration;
                break;
            }
        }
        if (primaryDeclaration is null)
        {
            return;
        }
        foreach (var augmentation in augmentingSymbol.Declarations ?? [])
        {
            if (global::Ts.Ast.AstUtilities.GetSourceFileOfNode(augmentation) == containingFile)
            {
                continue;
            }
            var diagnostic = DeclarationDiagnostics.CreateDiagnosticForNode(
                augmentation,
                global::Ts.Diagnostics.Diagnostics.Declaration_augments_declaration_in_another_file_This_cannot_be_serialized);
            diagnostic.AddRelatedInfo(DeclarationDiagnostics.CreateDiagnosticForNode(
                primaryDeclaration,
                global::Ts.Diagnostics.Diagnostics.This_is_the_declaration_being_augmented_Consider_moving_the_augmenting_declaration_into_the_same_file));
            state.AddDiagnostic(diagnostic);
        }
    }

    // tracker.go:133-143 — ReportPrivateInBaseOfClassExpression.
    public void ReportPrivateInBaseOfClassExpression(string propertyName)
    {
        var location = ErrorLocation();
        if (location is not null)
        {
            var diag = DeclarationDiagnostics.CreateDiagnosticForNode(
                location,
                global::Ts.Diagnostics.Diagnostics.Property_0_of_exported_anonymous_class_type_may_not_be_private_or_protected,
                propertyName);
            if (NodeTests.IsVariableDeclaration(location.Parent!))
            {
                diag.AddRelatedInfo(DeclarationDiagnostics.CreateDiagnosticForNode(
                    location,
                    global::Ts.Diagnostics.Diagnostics.Add_a_type_annotation_to_the_variable_0,
                    ErrorDeclarationNameWithFallback()));
            }
            state.AddDiagnostic(diag);
        }
    }

    public void ReportTruncationError()
    {
        AddDiagnostic(
            global::Ts.Diagnostics.Diagnostics.The_inferred_type_of_this_node_exceeds_the_maximum_length_the_compiler_will_serialize_An_explicit_type_annotation_is_needed);
    }

    public bool TrackSymbol(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning)
    {
        if ((symbol.Flags & SymbolFlags.TypeParameter) != 0)
        {
            return false;
        }
        if (watchedClassSymbol is not null && ReferenceEquals(symbol, watchedClassSymbol))
        {
            classSymbolTracked = true;
            return false;
        }
        if (resolver is null || enclosingDeclaration is null)
        {
            return false;
        }
        return HandleSymbolAccessibilityError(
            resolver.IsSymbolAccessible(symbol, enclosingDeclaration, meaning, true));
    }

    // tracker.go:198-227 — handleSymbolAccessibilityError.
    internal bool HandleSymbolAccessibilityError(SymbolAccessibilityResult result)
    {
        if (result.Accessibility == SymbolAccessibility.Accessible)
        {
            // Add aliases back onto the possible imports list if they're not there so we can try them again with updated visibility info
            foreach (var alias in result.AliasesToMakeVisible ?? [])
            {
                if (!state.LateMarkedStatements.Contains(alias))
                {
                    state.LateMarkedStatements.Add(alias);
                }
            }
            // TODO: Do all these accessibility checks inside/after the first pass in the checker when declarations are enabled, if possible

            // The checker should issue errors on unresolvable names, skip the declaration emit error for using a private/unreachable name for those
        }
        else if (result.Accessibility != SymbolAccessibility.NotResolved)
        {
            // Report error
            var errorInfo = state.GetSymbolAccessibilityDiagnostic?.Invoke(result);
            if (errorInfo != null)
            {
                var diagNode = result.ErrorNode;
                if (diagNode == null)
                {
                    diagNode = errorInfo.ErrorNode;
                }
                if (errorInfo.TypeName != null)
                {
                    state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(
                        diagNode,
                        errorInfo.DiagnosticMessage!,
                        global::Ts.Scanner.Scanner.GetTextOfNode(errorInfo.TypeName),
                        result.ErrorSymbolName ?? "",
                        result.ErrorModuleName ?? ""));
                }
                else
                {
                    state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(
                        diagNode,
                        errorInfo.DiagnosticMessage!,
                        result.ErrorSymbolName ?? "",
                        result.ErrorModuleName ?? ""));
                }
                return true;
            }
        }
        return false;
    }

    internal void WatchClassSymbol(Symbol? symbol)
    {
        watchedClassSymbol = symbol;
        classSymbolTracked = false;
    }

    internal bool ClassSymbolTracked => classSymbolTracked;

    private Node? ErrorFallbackNode() =>
        fallbackStack.Count == 0 ? null : fallbackStack[fallbackStack.Count - 1];

    private Node? ErrorLocation() => state.ErrorNameNode ?? ErrorFallbackNode();

    private string ErrorDeclarationNameWithFallback()
    {
        if (state.ErrorNameNode is not null)
        {
            return global::Ts.Scanner.Scanner.DeclarationNameToString(state.ErrorNameNode);
        }
        var fallback = ErrorFallbackNode();
        if (fallback?.Name() is { } name)
        {
            return global::Ts.Scanner.Scanner.DeclarationNameToString(name);
        }
        if (fallback?.Kind == SyntaxKind.ExportAssignment)
        {
            return fallback.AsExportAssignment().IsExportEquals ? "export=" : "default";
        }
        return "(Missing)";
    }

    private void AddDiagnostic(global::Ts.Diagnostics.Message message, params object?[] args)
    {
        if (ErrorLocation() is { } location)
        {
            state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(location, message, args));
        }
    }
}

public sealed class SymbolTrackerSharedState
{
    internal List<Node> LateMarkedStatements { get; } = [];
    internal List<Diagnostic> Diagnostics { get; } = [];
    internal GetSymbolAccessibilityDiagnostic? GetSymbolAccessibilityDiagnostic;
    internal Node? ErrorNameNode;
    internal bool IsolatedDeclarations;
    internal bool StripInternal;
    internal SourceFile? CurrentSourceFile;
    internal EmitResolver? Resolver;
    // transform.go:117-130 — installed by the transformer constructor; invoked by the tracker for
    // isolatedDeclarations expando-function errors.
    internal Action<Node>? ReportExpandoFunctionErrors;

    internal void AddDiagnostic(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
}

public static class SymbolTracker
{
    public static SymbolTrackerImpl NewSymbolTracker(
        IDeclarationEmitHost? host,
        EmitResolver? resolver,
        SymbolTrackerSharedState state) => new(host, resolver, state);
}
