namespace Ts.Ast;

// Go: internal/ast/ast.go:2464-2544 — SourceFile is the only handWritten node in
// ast.json (plan/02 §12); the generator emits just its As*/Is* surface.
//
// Phase-0 minimal real skeleton: the mixin surface and subtree-facts wiring that the
// class hierarchy requires. The full field set (plan/02 §12.1: fileName/text/
// Statements/EndOfFileToken/diagnostics/jsdocCache/binder products/…) and methods
// (§12.2: ForEachChild/VisitEachChild/copyFrom/Clone/computeSubtreeFacts/…) land
// with the parser/ast port (phases 2-3).
public sealed partial class SourceFile : Node, DeclarationBase, LocalsContainerBase, CompositeBase
{
    public Symbol? Symbol { get; set; } // DeclarationBase
    public SymbolTable? Locals { get; set; } // LocalsContainerBase
    public Node? NextContainer { get; set; } // LocalsContainerBase
    private uint facts; // CompositeBase (private; atomic via Volatile in CachedSubtreeFacts)

    public override DeclarationBase? DeclarationData() => this;
    public override LocalsContainerBase? LocalsContainerData() => this;
    internal override SubtreeFacts SubtreeFactsWorker() => CachedSubtreeFacts(ref facts); // CompositeBase (ast.go:1599-1607)
    // computeSubtreeFacts (ast.go:2687-2689) is hand-written in phase 3.
}
