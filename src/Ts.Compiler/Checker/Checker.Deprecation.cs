// Ported from tsgo internal/checker/checker.go (deprecation-suggestion cluster, plan/03 §4.2):
//   checkDeprecatedSignature 8340 / addDeprecatedSuggestionWithSignature 8351 /
//   addDeprecatedSuggestion 13897 / addDeprecatedSuggestionWorker 13905 / addSuggestionDiagnostic 13918;
//   tryGetPropertyAccessOrIdentifierToString (utilities.go:1535).
//
// Faithful per-function translation: same order, same branch structure. Go comments preserved.
//
// §1.5: the cross-package Diagnostics class is shadowed by the sibling Ts.Diagnostics namespace, so it
// is reached through the `Diag` alias; the no-collision hosts (NodeTests/AstUtilities/CoreUtil) are used
// directly. Deep name-formatting deps (entityNameToString / GetPropertyNameForPropertyNameNode) are
// reached only when a signature/entity is actually deprecated and remain on their owning stub surfaces.

using Ts.Ast;
using Ts.Core;

using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:8340
    internal void CheckDeprecatedSignature(Signature sig, Node node)
    {
        if ((sig.flags & SignatureFlags.IsSignatureCandidateForOverloadFailure) != 0)
        {
            return;
        }
        if (sig.declaration != null && IsDeprecatedDeclaration(sig.declaration))
        {
            var suggestionNode = GetDeprecatedSuggestionNode(node);
            var name = TryGetPropertyAccessOrIdentifierToString(AstUtilities.GetInvokedExpression(node)!);
            AddDeprecatedSuggestionWithSignature(suggestionNode, sig.declaration, name, SignatureToString(sig));
        }
    }

    // checker.go:8351
    internal Diagnostic AddDeprecatedSuggestionWithSignature(Node? location, Node declaration, string deprecatedEntity, string signatureString)
    {
        var message = CoreUtil.IfElse(deprecatedEntity != "", Diag.The_signature_0_of_1_is_deprecated, Diag.X_0_is_deprecated);
        var diagnostic = NewDiagnosticForNode(location, message, signatureString, deprecatedEntity);
        return AddDeprecatedSuggestionWorker([declaration], diagnostic);
    }

    // checker.go:13897
    internal Diagnostic AddDeprecatedSuggestion(Node? location, Node[] declarations, string deprecatedEntity)
    {
        var diagnostic = NewDiagnosticForNode(location, Diag.X_0_is_deprecated, deprecatedEntity);
        return AddDeprecatedSuggestionWorker(declarations, diagnostic);
    }

    // checker.go:13905
    internal Diagnostic AddDeprecatedSuggestionWorker(Node[] declarations, Diagnostic diagnostic)
    {
        foreach (var declaration in declarations)
        {
            var deprecatedTag = AstUtilities.GetJSDocDeprecatedTag(declaration);
            if (deprecatedTag != null)
            {
                diagnostic.AddRelatedInfo(NewDiagnosticForNode(deprecatedTag, Diag.The_declaration_was_marked_as_deprecated_here));
                break;
            }
        }
        AddSuggestionDiagnostic(diagnostic);
        return diagnostic;
    }

    // checker.go:13918
    internal void AddSuggestionDiagnostic(Diagnostic diagnostic)
    {
        // Discard diagnostics created while at the maximum number of recursive TypeToString invocations.
        if (serializationLevel < MaxSerializationLevel)
        {
            suggestionDiagnostics.Add(diagnostic);
        }
    }

    // utilities.go:1535
    internal static string TryGetPropertyAccessOrIdentifierToString(Node expr)
    {
        if (NodeTests.IsPropertyAccessExpression(expr))
        {
            var baseStr = TryGetPropertyAccessOrIdentifierToString(expr.Expression()!);
            if (baseStr != "")
            {
                return baseStr + "." + EntityNameToString(expr.Name()!);
            }
        }
        else if (NodeTests.IsElementAccessExpression(expr))
        {
            var baseStr = TryGetPropertyAccessOrIdentifierToString(expr.Expression()!);
            if (baseStr != "" && AstUtilities.IsPropertyName(expr.AsElementAccessExpression().ArgumentExpression!))
            {
                return baseStr + "." + GetPropertyNameForPropertyNameNode(expr.AsElementAccessExpression().ArgumentExpression!);
            }
        }
        else if (NodeTests.IsIdentifier(expr))
        {
            return expr.Text();
        }
        else if (NodeTests.IsJsxNamespacedName(expr))
        {
            return EntityNameToString(expr);
        }
        return "";
    }
}
