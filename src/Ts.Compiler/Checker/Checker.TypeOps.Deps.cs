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
    // ---- relation / subtype (relater.go) ----
    internal bool IsTypeRelatedTo(Type source, Type target, Relation relation) => throw new NotImplementedException("isTypeRelatedTo");
    internal bool IsTypeSubtypeOf(Type source, Type target) => throw new NotImplementedException("isTypeSubtypeOf");
    internal bool IsTypeStrictSubtypeOf(Type source, Type target) => throw new NotImplementedException("isTypeStrictSubtypeOf");
    internal bool IsTypeDerivedFrom(Type source, Type target) => throw new NotImplementedException("isTypeDerivedFrom");

    // ---- template-literal / string-mapping matching (checker.go, later wave) ----
    internal bool IsTypeMatchedByTemplateLiteralType(Type t, TemplateLiteralType template, TypeComparer compareTypes) => throw new NotImplementedException("isTypeMatchedByTemplateLiteralType");
    internal bool IsMemberOfStringMapping(Type t, Type template) => throw new NotImplementedException("isMemberOfStringMapping");

    // ---- misc type predicates (checker.go, later waves) ----
    internal bool IsPrimitiveOrObjectOrEmptyType(Type t) => throw new NotImplementedException("isPrimitiveOrObjectOrEmptyType");
    internal bool ContainsMissingType(Type t) => throw new NotImplementedException("containsMissingType");
    internal bool IsStringIndexSignatureOnlyType(Type t) => throw new NotImplementedException("isStringIndexSignatureOnlyType");
    internal static bool IsObjectOrArrayLiteralType(Type t) => throw new NotImplementedException("isObjectOrArrayLiteralType");

    // forEachMappedTypePropertyKeyTypeAndIndexSignatureKeyType / getLowerBoundOfKeyType provided by
    // a sibling (mapped-type) wave — not duplicated here.

    // ---- symbol / global helpers ----
    internal Symbol GetLateBoundSymbol(Symbol symbol) => throw new NotImplementedException("getLateBoundSymbol");
    internal Symbol? GetGlobalExtractSymbol() => throw new NotImplementedException("getGlobalExtractSymbol");

    // ---- diagnostics ----
    internal Diagnostic NewDiagnosticForNode(Node? node, global::Ts.Diagnostics.Message message, params object?[] args) => throw new NotImplementedException("NewDiagnosticForNode");

    // ---- ast free-function helper ----
    internal static bool IsDeleteTarget(Node node) => throw new NotImplementedException("isDeleteTarget");
}
