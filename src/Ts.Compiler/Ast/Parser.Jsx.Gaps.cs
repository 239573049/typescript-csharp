// AST gaps required by the parser Jsx cluster (phase 3).
//
// These are free functions from tsgo internal/ast that the Jsx parser needs but that had not
// yet been ported into the (parallel-owned) Ast source files at the time this cluster landed.
// They live under a cluster-unique class name so they cannot collide with other clusters' gap
// files. When the owning Ast port lands TagNamesAreEquivalent on AstUtilities (or wherever the
// generator/port places it), delete this file and repoint callers.
#nullable enable
using System;

namespace Ts.Ast;

public static class JsxParserAstGaps
{
    // Ported from tsgo internal/ast/utilities.go:4445 (TagNamesAreEquivalent).
    public static bool TagNamesAreEquivalent(Node lhs, Node rhs)
    {
        if (lhs.Kind != rhs.Kind)
        {
            return false;
        }
        switch (lhs.Kind)
        {
            case SyntaxKind.Identifier:
                return lhs.Text() == rhs.Text();
            case SyntaxKind.ThisKeyword:
                return true;
            case SyntaxKind.JsxNamespacedName:
                return lhs.AsJsxNamespacedName().Namespace!.Text() == rhs.AsJsxNamespacedName().Namespace!.Text() &&
                    lhs.AsJsxNamespacedName().name!.Text() == rhs.AsJsxNamespacedName().name!.Text();
            case SyntaxKind.PropertyAccessExpression:
                return lhs.AsPropertyAccessExpression().name!.Text() == rhs.AsPropertyAccessExpression().name!.Text() &&
                    TagNamesAreEquivalent(lhs.Expression()!, rhs.Expression()!);
        }
        throw new InvalidOperationException("Unhandled case in TagNamesAreEquivalent");
    }
}
