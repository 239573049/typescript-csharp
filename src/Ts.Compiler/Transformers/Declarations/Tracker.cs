// Ported from tsgo internal/transformers/declarations/tracker.go.

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

    internal SymbolTrackerImpl(
        IDeclarationEmitHost? host,
        EmitResolver? resolver,
        SymbolTrackerSharedState state)
    {
        this.host = host;
        this.resolver = resolver;
        this.state = state;
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

    public void ReportInferenceFallback(Node node)
    {
        if (!state.IsolatedDeclarations ||
            (state.CurrentSourceFile is not null &&
             global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node) != state.CurrentSourceFile))
        {
            return;
        }
        state.AddDiagnostic(DeclarationDiagnostics.CreateIsolatedDeclarationError(node));
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

    public void ReportPrivateInBaseOfClassExpression(string propertyName)
    {
        AddDiagnostic(
            global::Ts.Diagnostics.Diagnostics.Property_0_of_exported_anonymous_class_type_may_not_be_private_or_protected,
            propertyName);
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

    internal bool HandleSymbolAccessibilityError(SymbolAccessibilityResult result)
    {
        if (result.Accessibility == SymbolAccessibility.Accessible)
        {
            foreach (var alias in result.AliasesToMakeVisible ?? [])
            {
                if (!state.LateMarkedStatements.Contains(alias))
                {
                    state.LateMarkedStatements.Add(alias);
                }
            }
            return false;
        }
        if (result.Accessibility == SymbolAccessibility.NotResolved)
        {
            return false;
        }
        var info = state.GetSymbolAccessibilityDiagnostic?.Invoke(result);
        if (info?.DiagnosticMessage is null)
        {
            return false;
        }
        var location = result.ErrorNode ?? info.ErrorNode ?? ErrorLocation();
        if (location is null)
        {
            return false;
        }
        state.AddDiagnostic(DeclarationDiagnostics.CreateDiagnosticForNode(
            location,
            info.DiagnosticMessage,
            result.ErrorSymbolName ?? "",
            result.ErrorModuleName ?? ""));
        return true;
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
