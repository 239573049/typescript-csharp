// Ported from tsgo internal/checker/nodebuilder.go + internal/checker/nodebuilderimpl.go (the Type ->
// type-AST-node builder that backs typeToStringEx).
//
// PLACEMENT NOTE (phase-5 type-display wave):
//   In tsgo the NodeBuilder / NodeBuilderImpl live in `package checker` (not `package nodebuilder` —
//   that package only holds Flags/InternalFlags/SymbolTracker, which are here in NodeBuilder/Types.cs).
//   Every builder method (`func (b *NodeBuilderImpl) typeToTypeNode(...)`, `mapToTypeNodes`,
//   `symbolToTypeNode`, ...) reaches back into the checker via `b.ch`, so the faithful C# home for the
//   building logic is the Checker itself.
//
//   Accordingly, the breadth-first slice of typeToTypeNode (intrinsics / literals / union / intersection
//   / named type references) lands as private Checker partial methods in
//   src/Ts.Compiler/Checker/Checker.TypeToString.cs, alongside typeToStringEx (from printer.go). That
//   file is the single definition site for:
//     * TypeToStringEx        (printer.go:189)
//     * TypeToTypeNode        (nodebuilderimpl.go:3176 — intrinsic/literal/union/intersection/named slice)
//     * FormatUnionTypes      (printer.go:383)
//     * MapToTypeNodes        (nodebuilderimpl.go:337 — bare-list slice)
//     * SymbolToTypeReferenceNode (nodebuilderimpl.go:638 — bare-identifier slice)
//
//   The `Ts.Checker.NodeBuilder` shell (Checker.Support.cs) and the `getNodeBuilder` pool plumbing
//   (nodebuilder.go:288, `typeToStringNodebuilder` field) remain shells until the full ~150 KB
//   NodeBuilderImpl port lands; the mechanism above short-circuits them by building type nodes directly
//   with the emit-context AST factory (equivalent to `nodeBuilder.TypeToTypeNode(t, ...)`).
//
// This file is intentionally declaration-free: it documents where the nodebuilder type-node build
// currently lives so the full-NodeBuilderImpl wave knows what to relocate. No type is declared here to
// avoid colliding with the existing `Ts.Checker.NodeBuilder` shell.

namespace Ts.NodeBuilder;
