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
    // checker.go:11435 isMethodAccessForCall / 11443 lookupSymbolForPrivateIdentifierDeclaration /
    // 11459 getPrivateIdentifierPropertyOfType: real bodies landed in Checker.PropertyAccess.cs.
}
