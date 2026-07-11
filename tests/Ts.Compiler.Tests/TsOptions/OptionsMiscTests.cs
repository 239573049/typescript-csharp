// Unit tests for the tsoptions supporting machinery: NameMap (namemap.go), EnumMaps lib
// resolution (enummaps.go), the CommandLineOptionNameMap field/name lookup (tsconfigparsing.go),
// the spec validators (tsconfigparsing.go), and wildcard directory derivation
// (wildcarddirectories.go).

using Ts.TsOptions;
using Ts.TsPath;
using Xunit;

namespace Ts.TsOptions.Tests;

public class OptionsMiscTests
{
    [Fact]
    public void NameMap_GetIsCaseInsensitive()
    {
        Assert.NotNull(NameMaps.CompilerNameMap.Get("strict"));
        Assert.Same(NameMaps.CompilerNameMap.Get("strict"), NameMaps.CompilerNameMap.Get("Strict"));
        Assert.Same(NameMaps.CompilerNameMap.Get("strict"), NameMaps.CompilerNameMap.Get("STRICT"));
        Assert.Null(NameMaps.CompilerNameMap.Get("definitelyNotAnOption"));
    }

    [Fact]
    public void NameMap_ShortNameResolution()
    {
        // "d" is the short name for "declaration".
        var byShort = NameMaps.CompilerNameMap.GetOptionDeclarationFromName("d", allowShort: true);
        Assert.NotNull(byShort);
        Assert.Equal("declaration", byShort!.Name);
        // Without allowShort, "d" is not an option name.
        Assert.Null(NameMaps.CompilerNameMap.GetOptionDeclarationFromName("d", allowShort: false));
    }

    [Fact]
    public void CommandLineCompilerOptionsMap_ResolvesFieldNameAndJsonName()
    {
        // ForEachCompilerOptionValue looks up by the Go field name ("AllowJs"); the parse switch
        // by the json name ("allowJs"). Both must resolve to the same option (case-insensitive Get).
        var byField = TsConfigParsing.CommandLineCompilerOptionsMap.Get("AllowJs");
        var byJson = TsConfigParsing.CommandLineCompilerOptionsMap.Get("allowJs");
        Assert.NotNull(byField);
        Assert.Same(byField, byJson);
        Assert.Equal("allowJs", byField!.Name);
    }

    [Theory]
    [InlineData("es6", "lib.es2015.d.ts", true)]
    [InlineData("ES2015", "lib.es2015.d.ts", true)] // case-insensitive via ToFileNameLowerCase
    [InlineData("lib.es2015.d.ts", "lib.es2015.d.ts", true)] // already a file name
    [InlineData("bogus.lib", "", false)]
    public void EnumMaps_TryGetLibFileName(string input, string expected, bool ok)
    {
        var got = EnumMaps.TryGetLibFileName(input, out var result);
        Assert.Equal(ok, got);
        if (ok)
        {
            Assert.Equal(expected, result);
        }
    }

    [Theory]
    [InlineData("**", true)]
    [InlineData("a/**", true)]
    [InlineData("a/**/", true)]
    [InlineData("a**b", false)]
    [InlineData("a/b/c", false)]
    public void SpecValidator_InvalidTrailingRecursion(string spec, bool expected)
    {
        Assert.Equal(expected, TsConfigParsing.invalidTrailingRecursion(spec));
    }

    [Theory]
    [InlineData("**/../a", true)]
    [InlineData("a/**/../b", true)]
    [InlineData("../a", false)]
    [InlineData("a/../b", false)]
    [InlineData("**/a", false)]
    public void SpecValidator_InvalidDotDotAfterRecursiveWildcard(string spec, bool expected)
    {
        Assert.Equal(expected, TsConfigParsing.invalidDotDotAfterRecursiveWildcard(spec));
    }

    [Fact]
    public void WildcardDirectories_RecursiveWildcard()
    {
        var cpo = new ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = "/c" };
        var dirs = WildcardDirectories.getWildcardDirectories(new[] { "src/**/*.ts" }, System.Array.Empty<string>(), cpo);
        Assert.NotNull(dirs);
        Assert.True(dirs!.TryGetValue("/c/src", out var recursive));
        Assert.True(recursive);
    }

    [Fact]
    public void WildcardDirectories_NonRecursiveWildcard()
    {
        var cpo = new ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = "/c" };
        var dirs = WildcardDirectories.getWildcardDirectories(new[] { "src/*.ts" }, System.Array.Empty<string>(), cpo);
        Assert.NotNull(dirs);
        Assert.True(dirs!.TryGetValue("/c/src", out var recursive));
        Assert.False(recursive);
    }

    [Fact]
    public void WildcardDirectories_EmptyIncludeReturnsNull()
    {
        var cpo = new ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = "/c" };
        Assert.Null(WildcardDirectories.getWildcardDirectories(System.Array.Empty<string>(), System.Array.Empty<string>(), cpo));
    }
}
