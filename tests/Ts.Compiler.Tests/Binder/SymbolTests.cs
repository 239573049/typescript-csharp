// Unit tests for the completed Ts.Ast.Symbol / SymbolTable / internal-name surface
// (internal/ast/symbol.go). The binder port (phase 4) completes Symbol; these lock down
// its value-level behaviour independent of parsing/binding.

using System.Collections.Generic;
using Ts.Ast;
using Xunit;

namespace Ts.Binder.Tests;

public class SymbolTests
{
    // ── internal symbol name sentinels (symbol.go:47-70) ──

    [Fact]
    public void InternalSymbolName_Prefix_IsSentinel()
    {
        Assert.Equal("þ", InternalSymbolName.Prefix);
        Assert.Single(InternalSymbolName.Prefix);
    }

    [Fact]
    public void InternalSymbolName_Composites_UsePrefix()
    {
        Assert.Equal("þcall", InternalSymbolName.Call);
        Assert.Equal("þconstructor", InternalSymbolName.Constructor);
        Assert.Equal("þnew", InternalSymbolName.New);
        Assert.Equal("þtype", InternalSymbolName.Type);
        Assert.Equal("þobject", InternalSymbolName.Object);
        Assert.Equal("þclass", InternalSymbolName.Class);
        Assert.Equal("þfunction", InternalSymbolName.Function);
        // These two are deliberately not prefixed (symbol.go:66-67).
        Assert.Equal("export=", InternalSymbolName.ExportEquals);
        Assert.Equal("default", InternalSymbolName.Default);
        Assert.Equal("this", InternalSymbolName.This);
    }

    // ── EscapeSymbolName / EscapeInternalSymbolName / EscapeAllInternalSymbolNames ──

    [Fact]
    public void EscapeSymbolName_InternalNameBecomesUnderscored()
    {
        Assert.Equal("__call", AstUtilities.EscapeSymbolName(InternalSymbolName.Call));
        Assert.Equal("__type", AstUtilities.EscapeSymbolName(InternalSymbolName.Type));
    }

    [Fact]
    public void EscapeSymbolName_UserDoubleUnderscoreGainsOne()
    {
        Assert.Equal("___foo", AstUtilities.EscapeSymbolName("__foo"));
        Assert.Equal("___", AstUtilities.EscapeSymbolName("__"));
    }

    [Fact]
    public void EscapeSymbolName_PlainNameUnchanged()
    {
        Assert.Equal("foo", AstUtilities.EscapeSymbolName("foo"));
        Assert.Equal("_bar", AstUtilities.EscapeSymbolName("_bar"));
    }

    [Fact]
    public void EscapeInternalSymbolName_OnlyPrefixed()
    {
        Assert.Equal("__call", AstUtilities.EscapeInternalSymbolName(InternalSymbolName.Call));
        // Unlike EscapeSymbolName, a user "__foo" is left untouched here.
        Assert.Equal("__foo", AstUtilities.EscapeInternalSymbolName("__foo"));
        Assert.Equal("plain", AstUtilities.EscapeInternalSymbolName("plain"));
    }

    [Fact]
    public void EscapeAllInternalSymbolNames_ReplacesEverySentinel()
    {
        Assert.Equal("____", AstUtilities.EscapeAllInternalSymbolNames(InternalSymbolName.Prefix + InternalSymbolName.Prefix));
        Assert.Equal("a__b", AstUtilities.EscapeAllInternalSymbolNames("a" + InternalSymbolName.Prefix + "b"));
    }

    // ── Symbol value helpers (symbol.go:23-41) ──

    [Fact]
    public void Symbol_IsExternalModule_TrueForQuotedModuleName()
    {
        Symbol s = new() { Flags = SymbolFlags.Module, Name = "\"/a\"" };
        Assert.True(s.IsExternalModule());

        Symbol notModule = new() { Flags = SymbolFlags.Module, Name = "N" };
        Assert.False(notModule.IsExternalModule());

        Symbol notQuoted = new() { Flags = SymbolFlags.ValueModule, Name = "N" };
        Assert.False(notQuoted.IsExternalModule());
    }

    [Fact]
    public void Symbol_CombinedLocalAndExportSymbolFlags()
    {
        Symbol export = new() { Flags = SymbolFlags.Class };
        Symbol local = new() { Flags = SymbolFlags.ExportValue, ExportSymbol = export };
        Assert.Equal(SymbolFlags.ExportValue | SymbolFlags.Class, local.CombinedLocalAndExportSymbolFlags());

        Symbol plain = new() { Flags = SymbolFlags.FunctionScopedVariable };
        Assert.Equal(SymbolFlags.FunctionScopedVariable, plain.CombinedLocalAndExportSymbolFlags());
    }

    [Fact]
    public void SymbolTable_IsOrdinalKeyedDictionary()
    {
        // The SymbolTable alias is Dictionary<string, Symbol> with default (ordinal) comparer.
        System.Collections.Generic.Dictionary<string, Symbol> table = new();
        Symbol a = new() { Name = "a" };
        table["a"] = a;
        Assert.Same(a, table.GetValueOrDefault("a"));
        Assert.Null(table.GetValueOrDefault("A")); // case-sensitive (ordinal)
        Assert.Null(table.GetValueOrDefault("missing"));
    }
}
