// Smoke tests for the name-resolution wave (Checker.NameLookup.cs + the wired resolveName field).
//
// The full resolveName trunk lives in Ts.Binder.NameResolver.Resolve; the checker wires it up in
// CreateNameResolver (Checker.Construction.cs) with GetSymbol as the Lookup hook. These tests exercise
// the genuinely-reachable surface without a bound Program: the GetSymbol meaning-filtered lookup and the
// wired resolveName field's global-scope path (location == null → falls through to the Globals lookup).
// Deeper per-scope walking (locals/module/class chains) needs a real bound AST and is covered end-to-end
// by CheckerEndToEndTests; here we prove the wiring resolves simple names to local/global symbols.

using System.Collections.Generic;
using Ts.Ast;
using Ts.Checker;
using Xunit;

namespace Ts.Checker.Tests;

public class NameLookupTests
{
    private static Checker NewChecker()
    {
        var (checker, _) = Checker.NewChecker(new FakeProgram(), null);
        return checker;
    }

    [Fact]
    public void GetSymbol_MatchingMeaning_ResolvesSymbol()
    {
        var c = NewChecker();
        var value = new Symbol { Flags = SymbolFlags.FunctionScopedVariable, Name = "x" };
        var symbols = new Dictionary<string, Symbol> { ["x"] = value };

        // meaning intersects the symbol's flags -> resolved.
        Assert.Same(value, c.GetSymbol(symbols, "x", SymbolFlags.Value));
    }

    [Fact]
    public void GetSymbol_NonMatchingMeaning_ReturnsNull()
    {
        var c = NewChecker();
        var value = new Symbol { Flags = SymbolFlags.FunctionScopedVariable, Name = "x" };
        var symbols = new Dictionary<string, Symbol> { ["x"] = value };

        // A value symbol is invisible to a type-only lookup (no alias to follow) -> null.
        Assert.Null(c.GetSymbol(symbols, "x", SymbolFlags.Type));
    }

    [Fact]
    public void GetSymbol_AbsentName_ReturnsNull()
    {
        var c = NewChecker();
        var symbols = new Dictionary<string, Symbol>();

        Assert.Null(c.GetSymbol(symbols, "missing", SymbolFlags.Value));
    }

    [Fact]
    public void GetSymbol_ZeroMeaning_ReturnsNull()
    {
        var c = NewChecker();
        var value = new Symbol { Flags = SymbolFlags.Value, Name = "x" };
        var symbols = new Dictionary<string, Symbol> { ["x"] = value };

        // meaning & SymbolFlags.All == 0 -> the outer guard short-circuits without touching the table.
        Assert.Null(c.GetSymbol(symbols, "x", SymbolFlags.None));
    }

    [Fact]
    public void ResolveName_GlobalSymbol_ResolvesFromGlobals()
    {
        var c = NewChecker();
        // A location-less lookup skips the scope-chain walk and (excludeGlobals == false) falls through
        // to the Globals table — the classic "resolve a simple identifier to a global symbol" path.
        var global = new Symbol { Flags = SymbolFlags.FunctionScopedVariable, Name = "GlobalThing" };
        c.globals["GlobalThing"] = global;

        var resolved = c.resolveName(null, "GlobalThing", SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
        Assert.Same(global, resolved);
    }

    [Fact]
    public void ResolveName_ExcludeGlobals_DoesNotResolveGlobal()
    {
        var c = NewChecker();
        var global = new Symbol { Flags = SymbolFlags.FunctionScopedVariable, Name = "GlobalThing2" };
        c.globals["GlobalThing2"] = global;

        // excludeGlobals == true skips the Globals fallback -> unresolved.
        var resolved = c.resolveName(null, "GlobalThing2", SymbolFlags.Value, null /*nameNotFoundMessage*/, false /*isUse*/, true /*excludeGlobals*/);
        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveName_GlobalWrongMeaning_ReturnsNull()
    {
        var c = NewChecker();
        var global = new Symbol { Flags = SymbolFlags.FunctionScopedVariable, Name = "ValueOnly" };
        c.globals["ValueOnly"] = global;

        // A value-only global is invisible to a type lookup.
        var resolved = c.resolveName(null, "ValueOnly", SymbolFlags.Type, null /*nameNotFoundMessage*/, false /*isUse*/, false /*excludeGlobals*/);
        Assert.Null(resolved);
    }
}
