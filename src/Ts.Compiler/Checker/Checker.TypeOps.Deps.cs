// Single dedup surface for cross-cluster methods that the type-operations wave (#17/#18/#19) CALLS
// but does not own — they live in not-yet-landed waves (relater, mapped-type resolution, flow,
// diagnostics, globals, tuple/element helpers). All throw NotImplementedException so the call sites
// compile; deep runtime paths run only once the owning wave lands its real implementation, at which
// point that wave DELETES the corresponding stub here.

using Ts.Ast;
using Ts.Core;

namespace Ts.Checker;

public partial class Checker
{
    // relation / subtype (isTypeRelatedTo/isTypeSubtypeOf/isTypeStrictSubtypeOf/isTypeDerivedFrom),
    // template-literal (isTypeMatchedByTemplateLiteralType) and string-mapping
    // (isMemberOfStringMapping) are OWNED by the relater wave — implemented in Checker.Relater.cs.

    // ---- misc type predicates (checker.go, later waves) ----
    // checker.go:1247 (wired closure)
    internal bool IsPrimitiveOrObjectOrEmptyType(Type t) =>
        (t.flags & (TypeFlags.Primitive | TypeFlags.NonPrimitive)) != 0 || IsEmptyAnonymousObjectType(t);
    // checker.go:1250 — containsMissingType (constructor-bound closure in Go; direct method here).
    internal bool ContainsMissingType(Type t) =>
        t == missingType || (t.flags & TypeFlags.Union) != 0 && t.Types()[0] == missingType;
    // isStringIndexSignatureOnlyType is a memoized Func<Type,bool> field (Checker.cs) bound to IsStringIndexSignatureOnlyTypeWorker; callers invoke the field.
    // utilities.go:967
    internal static bool IsObjectOrArrayLiteralType(Type t) =>
        (t.objectFlags & (ObjectFlags.ObjectLiteral | ObjectFlags.ArrayLiteral)) != 0;

    // forEachMappedTypePropertyKeyTypeAndIndexSignatureKeyType / getLowerBoundOfKeyType provided by
    // a sibling (mapped-type) wave — not duplicated here.

    // ---- symbol / global helpers ----
    // GetLateBoundSymbol is now REAL in Checker.SymbolResolution.cs (checker.go:14334).
    internal Symbol? GetGlobalExtractSymbol() => getGlobalExtractSymbol();

    // ---- diagnostics ----
    // utilities.go:22-30
    internal Diagnostic NewDiagnosticForNode(Node? node, global::Ts.Diagnostics.Message message, params object?[] args)
    {
        SourceFile? file = null;
        var loc = default(global::Ts.Core.TextRange);
        if (node != null)
        {
            file = AstUtilities.GetSourceFileOfNode(node);
            loc = global::Ts.Scanner.Scanner.GetErrorRangeForNode(file!, node);
        }
        return Diagnostic.NewDiagnostic(file, loc, message, args);
    }

    // ---- ast free-function helper ----
    // checker/utilities.go:110 — isDeleteTarget.
    internal static bool IsDeleteTarget(Node node)
    {
        if (!AstUtilities.IsAccessExpression(node))
        {
            return false;
        }
        var parent = AstUtilities.WalkUpParenthesizedExpressions(node.Parent);
        return parent != null && parent.Kind == SyntaxKind.DeleteExpression;
    }
}
