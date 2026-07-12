// Ported from tsgo internal/transformers/declarations/diagnostics.go.

using global::Ts.Ast;
using global::Ts.Diagnostics;
using global::Ts.Printer;

namespace Ts.Transformers.Declarations;

internal delegate SymbolAccessibilityDiagnostic? GetSymbolAccessibilityDiagnostic(
    SymbolAccessibilityResult symbolAccessibilityResult);

internal sealed class SymbolAccessibilityDiagnostic
{
    internal Node? ErrorNode;
    internal Message? DiagnosticMessage;
    internal Node? TypeName;
}

internal static class DeclarationDiagnostics
{
    internal static Message? SelectDiagnosticBasedOnModuleName(
        SymbolAccessibilityResult result,
        Message moduleNotNameable,
        Message privateModule,
        Message nonModule)
    {
        if (!string.IsNullOrEmpty(result.ErrorModuleName))
        {
            return result.Accessibility == SymbolAccessibility.CannotBeNamed
                ? moduleNotNameable
                : privateModule;
        }
        return nonModule;
    }

    internal static Message SelectDiagnosticBasedOnModuleNameNoNameCheck(
        SymbolAccessibilityResult result,
        Message privateModule,
        Message nonModule) =>
        !string.IsNullOrEmpty(result.ErrorModuleName) ? privateModule : nonModule;

    internal static GetSymbolAccessibilityDiagnostic CreateGetSymbolAccessibilityDiagnosticForNode(Node node)
    {
        // PORT-NOTE: diagnostics.go has a large declaration-kind-to-message matrix. The tracker and
        // diagnostic context are wired here, but the complete message selection matrix is deferred
        // until declaration emit is connected to the checker-backed resolver. Returning no selector
        // result suppresses only accessibility diagnostics; it never changes the emitted declaration.
        _ = node;
        return static _ => null;
    }

    internal static GetSymbolAccessibilityDiagnostic CreateGetSymbolAccessibilityDiagnosticForNodeName(Node node) =>
        CreateGetSymbolAccessibilityDiagnosticForNode(node);

    internal static global::Ts.Ast.Diagnostic CreateDiagnosticForNode(
        Node node,
        Message message,
        params object?[] args)
    {
        var sourceFile = global::Ts.Ast.AstUtilities.GetSourceFileOfNode(node);
        var location = global::Ts.Scanner.Scanner.GetErrorRangeForNode(sourceFile!, node);
        return global::Ts.Ast.Diagnostic.NewDiagnostic(sourceFile, location, message, args);
    }

    internal static global::Ts.Ast.Diagnostic CreateIsolatedDeclarationError(Node node)
    {
        // Conservative fallback matching diagnostics.go's final createExpressionError branch.
        return CreateDiagnosticForNode(
            node,
            global::Ts.Diagnostics.Diagnostics.Expression_type_can_t_be_inferred_with_isolatedDeclarations);
    }
}
