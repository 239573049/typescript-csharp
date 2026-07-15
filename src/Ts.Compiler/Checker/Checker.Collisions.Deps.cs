// Single dedup surface for the deep-leaf callees of the #6 collision/unused cluster (Checker.Collisions.cs)
// that belong to unported subsystems. Each throws NotImplementedException so the trunk call sites compile;
// they run only once the owning wave lands the real body, at which point that wave DELETES the stub here.
//
// Two groups:
//  (1) UNUSED-IDENTIFIER DISPATCH (checker.go:7050-7290) — checkUnusedClassMembers /
//      checkUnusedLocalsAndParameters / checkUnusedTypeParameters / checkUnusedInferTypeParameter. These are
//      the heavy per-scope unused-symbol analyzers dispatched from checkUnusedIdentifiers. They run only when
//      the unused-check pass is enabled (noUnusedLocals/noUnusedParameters or suggestion diagnostics) AND the
//      file is non-declaration. The `const x` e2e probe runs with neither flag set, so checkUnusedIdentifiers
//      is not even invoked. Deferred to the fuller unused-analysis wave.
//  (2) RARE COLLISION LEAVES (checker.go:10524/10558) — checkWeakMapSetCollision / checkReflectCollision.
//      Enqueued as deferred diagnostics ONLY when the declaration name is exactly WeakMap/WeakSet/Reflect at
//      an old script target; ordinary names never reach recordPotentialCollisionWith*'s enqueue. Deferred —
//      they depend on GetEnclosingBlockScopeContainer (an unported AST utility) and the deep
//      NodeCheckFlags.ContainsClassWithPrivateIdentifiers / ContainsSuperPropertyInStaticInitializer flow
//      analysis.
//
// **Do NOT implement whole missing subsystems here.** Every method is a single stub.

using System;
using Ts.Ast;

namespace Ts.Checker;

// checker.go:7054 — UnusedKind
internal enum UnusedKind
{
    Local = 0,
    Parameter,
}

public partial class Checker
{
    // ---- (1) unused-identifier dispatch (checker.go:7050-7290) — real bodies in Checker.Collisions.cs ----

    // ---- (2) rare reserved-name collision leaves (checker.go:10524/10558) ----
    internal void CheckWeakMapSetCollision(Node node) => throw new NotImplementedException("checkWeakMapSetCollision");
    internal void CheckReflectCollision(Node node) => throw new NotImplementedException("checkReflectCollision");
}
