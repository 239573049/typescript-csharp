// Tests for plan/03 §4.2 #10 (SymbolResolution) and #11 (SymbolTypes) clusters.
//
// Deep symbol->type paths bottom out in other clusters' stubs (getTypeFromTypeNode, checkExpression,
// resolveExternalModule, ...); these tests therefore stay on the genuinely reachable surface —
// the alias/flags passthroughs and the getTypeOfSymbol dispatch's error fall-through — which run
// real ported code without touching NotImplementedException stubs. (See CheckerSmokeTests for the
// construction/interning coverage; do not modify that file.)

using System.Collections.Generic;
using Ts.Ast;
using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class SymbolResolutionTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    [Fact]
    public void ResolveSymbolEx_NonAlias_ReturnsSymbolUnchanged()
    {
        var c = NewChecker();
        var symbol = new Symbol { Flags = SymbolFlags.Property | SymbolFlags.Value, Name = "x" };

        // Not an alias -> IsNonLocalAlias is false -> returned verbatim regardless of dontResolveAlias.
        Assert.Same(symbol, c.ResolveSymbolEx(symbol, dontResolveAlias: false));
        Assert.Same(symbol, c.ResolveSymbolEx(symbol, dontResolveAlias: true));
    }

    [Fact]
    public void GetSymbolFlags_NonAlias_ReturnsLocalFlags()
    {
        var c = NewChecker();
        var flags = SymbolFlags.Property | SymbolFlags.Value;
        var symbol = new Symbol { Flags = flags, Name = "x" };

        // The alias-following loop never runs for a non-alias symbol.
        Assert.Equal(flags, c.GetSymbolFlags(symbol));
    }

    [Fact]
    public void GetExportsOfSymbol_PlainSymbol_ReturnsOwnExports()
    {
        var c = NewChecker();
        var exported = new Symbol { Flags = SymbolFlags.Property, Name = "member" };
        var symbol = new Symbol
        {
            Flags = SymbolFlags.Property, // neither Module nor LateBindingContainer
            Name = "ns",
            Exports = new Dictionary<string, Symbol> { ["member"] = exported },
        };

        var exports = c.GetExportsOfSymbol(symbol);
        Assert.Same(exported, exports["member"]);
    }

    [Fact]
    public void GetExportsOfSymbol_NoExports_ReturnsEmptyTable()
    {
        var c = NewChecker();
        var symbol = new Symbol { Flags = SymbolFlags.Property, Name = "x" };

        // Go reads a nil Exports map as empty; the port returns a shared empty table (never null).
        Assert.NotNull(c.GetExportsOfSymbol(symbol));
        Assert.Empty(c.GetExportsOfSymbol(symbol));
    }

    [Fact]
    public void GetTypeOfSymbol_UnhandledFlags_FallsThroughToErrorType()
    {
        var c = NewChecker();
        // A symbol with no value/type/accessor/alias meaning hits the dispatch's final fall-through.
        var symbol = new Symbol { Flags = SymbolFlags.None, Name = "x" };

        Assert.Same(c.errorType, c.GetTypeOfSymbol(symbol));
    }
}
