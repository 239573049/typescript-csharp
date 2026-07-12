// PLACEHOLDER STUBS for the Instantiation cluster (plan/03 §4.2 #14).
//
// Single dedup surface for cross-cluster helpers that the instantiation family CALLS but does not
// own. Each had no C# definition anywhere at build time, so it is stubbed here (throw) purely to
// compile the cluster. **When the owning cluster lands a real definition, DELETE the corresponding
// stub here.** Signatures match the #14 call sites (nullable-tolerant); the owning cluster's real
// signature is authoritative. §1.5: cross-package names would be `global::Ts.<Pkg>...`; all of
// these are Checker members so no qualification is needed.
//
// NOTE: getUnionType / getIndexType / getBaseConstraintOfType / getUnionTypeEx / getIntersectionTypeEx
// / getIndexedAccessTypeEx / getSubstitutionType / getStringMappingType / getNoInferType /
// createDeferredTypeReference / isTypeAssignableTo / getActualTypeVariable are stubbed in
// Checker.Stubs.cs / Checker.TypeNodes.Stubs.cs (owned by #16/#17/#18/#19/relater) — NOT duplicated here.

using System;
using Ts.Ast;

namespace Ts.Checker;

public partial class Checker
{
    // ---- apparent type: global lib types (#construction / lib-resolution) ----
    // checker.go:21654, 21658 — getGlobalBigIntType() / getGlobalESSymbolType(): the resolver funcs are
    // memoized in the constructor (Checker.Construction.cs); these wrappers invoke them.
    internal Type GetGlobalBigIntType() => getGlobalBigIntType();

    internal Type GetGlobalESSymbolType() => getGlobalESSymbolType();

    // ---- mapped types (#15 type nodes) ----
    // getModifiersTypeFromMappedType (27956) implemented in Checker.Constraints.cs (#19).
    internal MappedTypeModifiers GetMappedTypeModifiers(Type t) => throw new NotImplementedException("getMappedTypeModifiers");

    // ---- substitution / noinfer (#15 type nodes) ----
    // isNoInferType (26688) implemented in Checker.IndexedAccess.cs (#18).

    // ---- union/intersection mapping (#17) ----
    // MapTypeWithAlias is now REAL in Checker.Widening.cs (checker.go:25462).

    // ---- identity relation (relater) ----
    // checker.go — isTypeIdenticalTo(s, t).
    // isTypeIdenticalTo owned by the relater wave (Checker.Relater.cs).
}
