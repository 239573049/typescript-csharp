namespace Ts.Ast;

// Go: internal/ast/symbol.go:10-21 — minimal real skeleton (phase 0). The generated
// AST only needs the type to exist (goOnly mixin fields `Symbol?`/`LocalSymbol?`);
// the full field set (Flags/CheckFlags/Name/Declarations/…) and the internal-name
// constants land with the binder port (phase 4) per plan/02 §2.2 and plan/03.
public sealed class Symbol
{
    internal ulong id; // Go atomic.Uint64; accessed via Interlocked in AstUtilities.GetSymbolId
}

// Go: internal/ast/symbol.go:45 — `type SymbolTable map[string]*Symbol`. Minimal real
// skeleton (phase 0): the concrete map representation (ordinal keys, iteration-order
// discipline per plan/05 §7) is decided with the binder port (phase 4).
public sealed class SymbolTable
{
}
