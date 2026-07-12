// Single dedup surface for methods the alias-reference-marking wave (Checker.AliasReferences.cs) CALLS
// but does not own. Each throws NotImplementedException so the call sites compile; the deep alias-
// transitivity paths run only once the owning wave lands its real implementation, at which point that
// wave DELETES the corresponding stub here (one definition per method across the whole assembly, CS0111).
//
// These are reached only on the deep property-access / private-field branch of markPropertyAliasReferenced
// (a property access whose left resolves to a non-any, non-const-enum value type under non-isolated
// modules). markLinkedReferences itself no-ops / marks correctly for plain, import-free programs — the
// phase-6 emit end-to-end probes never reach these.
//
// Other dependencies of this wave are already stubbed on their owning Deps surfaces and are NOT
// re-declared here (one definition each): getExportSymbolOfValueSymbolIfExported / getTypeOnlyAliasDeclaration
// (Checker.SymbolResolution.cs), resolveSymbol (Checker.Stubs.cs), getAssignmentTargetKind
// (Checker.Flow.Deps.cs), IsTypeAny (Checker.SymbolTypes.cs), checkExternalEmitHelpers
// (Checker.Statements.Deps.cs), checkExternalImportOrExportDeclaration (Checker.Declarations.Deps.cs).
//
// §1.5: no cross-package classes referenced here that collide with sibling namespaces.

using System;
using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // checker.go:11435 — whether a property access is the callee of a call/new (peels parentheses).
    // Reached only on the widened-apparent-type branch of markPropertyAliasReferenced.
    internal bool IsMethodAccessForCall(Node node) => throw new NotImplementedException("isMethodAccessForCall");

    // checker.go:11443 — lexical lookup of a `#private` identifier declaration up the class chain
    // (needs binder.GetSymbolNameForPrivateIdentifier + getContainingClassExcludingClassDecorators).
    internal Symbol? LookupSymbolForPrivateIdentifierDeclaration(string propName, Node location) => throw new NotImplementedException("lookupSymbolForPrivateIdentifierDeclaration");

    // checker.go:11459 — resolves a `#private` property on a type from its lexically scoped symbol.
    internal Symbol? GetPrivateIdentifierPropertyOfType(Type leftType, Symbol lexicallyScopedIdentifier) => throw new NotImplementedException("getPrivateIdentifierPropertyOfType");
}
