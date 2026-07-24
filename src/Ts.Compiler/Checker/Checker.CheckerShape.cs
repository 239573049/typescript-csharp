// The checker satisfies modulespecifiers.CheckerShape (GetSymbolAtLocation / GetAliasedSymbol) so it
// can drive module-specifier generation (nodeBuilder.getSpecifierForModuleSymbol). GetSymbolAtLocation
// is defined in Checker.NodeSymbols.cs; GetAliasedSymbol is defined here.

using Ts.Ast;

namespace Ts.Checker;

public partial class Checker : global::Ts.ModuleSpecifiers.CheckerShape
{
    // checker.go:31924 — GetAliasedSymbol.
    public Symbol? GetAliasedSymbol(Symbol symbol) => ResolveAlias(symbol).Item1;

    // exports.go:244-246
    public Signature? GetResolvedSignature(Node node) => GetResolvedSignature(node, null, CheckMode.Normal);

    // exports.go:61-63
    public Symbol GetUnknownSymbol() => unknownSymbol;

    // checker/utilities.go:1622 — package-level SkipAlias.
    public static Symbol? SkipAlias(Symbol symbol, Checker checker)
    {
        if ((symbol.Flags & SymbolFlags.Alias) != 0)
        {
            return checker.GetAliasedSymbol(symbol);
        }
        return symbol;
    }
}
