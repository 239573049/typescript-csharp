// The checker satisfies modulespecifiers.CheckerShape (GetSymbolAtLocation / GetAliasedSymbol) so it
// can drive module-specifier generation (nodeBuilder.getSpecifierForModuleSymbol). GetSymbolAtLocation
// is defined in Checker.NodeSymbols.cs; GetAliasedSymbol is defined here.

using Ts.Ast;

namespace Ts.Checker;

public partial class Checker : global::Ts.ModuleSpecifiers.CheckerShape
{
    // checker.go:31924 — GetAliasedSymbol.
    public Symbol? GetAliasedSymbol(Symbol symbol) => ResolveAlias(symbol).Item1;
}
