// Unit tests for tsoptions parsing helpers + the hand-written mergeCompilerOptions
// (internal/tsoptions/parsinghelpers.go). Covers the core value converters and the AOT-safe
// per-field merge that replaces Go's reflection (plan/07 §1.1).

using Ts.Collections;
using Ts.Core;
using Ts.TsOptions;
using Xunit;

namespace Ts.TsOptions.Tests;

public class ParsingHelpersTests
{
    [Fact]
    public void ParseTristate_Cases()
    {
        Assert.Equal(Tristate.Unknown, ParsingHelpers.ParseTristate(null));
        Assert.Equal(Tristate.True, ParsingHelpers.ParseTristate(true));
        Assert.Equal(Tristate.False, ParsingHelpers.ParseTristate(false));
        Assert.Equal(Tristate.True, ParsingHelpers.ParseTristate(Tristate.True));
        Assert.Equal(Tristate.False, ParsingHelpers.ParseTristate(Tristate.False));
        // non-bool, non-tristate → false (Go: value == true is false)
        Assert.Equal(Tristate.False, ParsingHelpers.ParseTristate("x"));
    }

    [Fact]
    public void ParseString_Cases()
    {
        Assert.Equal("hello", ParsingHelpers.ParseString("hello"));
        Assert.Equal("", ParsingHelpers.ParseString(5));
        Assert.Equal("", ParsingHelpers.ParseString(null));
    }

    [Fact]
    public void ParseStringArray_Cases()
    {
        Assert.Equal(new[] { "a", "b" }, ParsingHelpers.ParseStringArray(new object?[] { "a", "b" }));
        // non-strings are filtered out
        Assert.Equal(new[] { "a", "b" }, ParsingHelpers.ParseStringArray(new object?[] { "a", 5, "b" }));
        Assert.Null(ParsingHelpers.ParseStringArray("not an array"));
        Assert.Null(ParsingHelpers.ParseStringArray(null));
    }

    [Fact]
    public void ParseNumber_Cases()
    {
        Assert.Equal(5, ParsingHelpers.parseNumber(5));
        Assert.Equal(5, ParsingHelpers.parseNumber(5.0));
        Assert.Equal(5, ParsingHelpers.parseNumber(5.9)); // int truncation
        Assert.Null(ParsingHelpers.parseNumber("x"));
        Assert.Null(ParsingHelpers.parseNumber(null));
    }

    [Fact]
    public void ParseCompilerOptions_Boolean()
    {
        var co = new CompilerOptions();
        ParsingHelpers.ParseCompilerOptions("allowJs", true, co);
        Assert.Equal(Tristate.True, co.AllowJs);
        ParsingHelpers.ParseCompilerOptions("strict", false, co);
        Assert.Equal(Tristate.False, co.Strict);
    }

    [Fact]
    public void ParseCompilerOptions_EnumFromNumber()
    {
        var co = new CompilerOptions();
        // A JSON number for an enum option (floatOrInt32ToFlag path): 2 → ScriptTarget.ES2015.
        ParsingHelpers.ParseCompilerOptions("target", 2.0, co);
        Assert.Equal(ScriptTarget.ES2015, co.Target);
        // Already-resolved enum value passes through.
        ParsingHelpers.ParseCompilerOptions("module", ModuleKind.CommonJS, co);
        Assert.Equal(ModuleKind.CommonJS, co.Module);
    }

    [Fact]
    public void ParseCompilerOptions_ListAndNumberAndPaths()
    {
        var co = new CompilerOptions();
        ParsingHelpers.ParseCompilerOptions("lib", new object?[] { "es2015", "dom" }, co);
        Assert.Equal(new[] { "es2015", "dom" }, co.Lib);

        ParsingHelpers.ParseCompilerOptions("maxNodeModuleJsDepth", 3.0, co);
        Assert.Equal(3, co.MaxNodeModuleJsDepth);

        var paths = new OrderedMap<string, object?>();
        paths.Set("@app/*", new object?[] { "src/*" });
        ParsingHelpers.ParseCompilerOptions("paths", paths, co);
        Assert.NotNull(co.Paths);
        Assert.True(co.Paths!.TryGet("@app/*", out var mapped));
        Assert.Equal(new[] { "src/*" }, mapped);
    }

    [Fact]
    public void MergeCompilerOptions_CopiesNonZeroSourceFields()
    {
        var target = new CompilerOptions { AllowJs = Tristate.Unknown, Strict = Tristate.True };
        var source = new CompilerOptions { AllowJs = Tristate.True, Strict = Tristate.Unknown, OutDir = "dist" };

        ParsingHelpers.mergeCompilerOptions(target, source, null);

        // Non-zero source field copied.
        Assert.Equal(Tristate.True, target.AllowJs);
        Assert.Equal("dist", target.OutDir);
        // Zero (Unknown) source field does NOT overwrite target.
        Assert.Equal(Tristate.True, target.Strict);
    }

    [Fact]
    public void MergeCompilerOptions_ExplicitNullZeroesTarget()
    {
        var target = new CompilerOptions { Strict = Tristate.True, NoImplicitAny = Tristate.True };
        var source = new CompilerOptions();

        // rawSource: { "compilerOptions": { "strict": null } } — explicit null resets target.Strict.
        var inner = new OrderedMap<string, object?>();
        inner.Set("strict", null);
        var raw = new OrderedMap<string, object?>();
        raw.Set("compilerOptions", inner);

        ParsingHelpers.mergeCompilerOptions(target, source, raw);

        Assert.Equal(Tristate.Unknown, target.Strict); // zeroed by explicit null
        Assert.Equal(Tristate.True, target.NoImplicitAny); // untouched
    }

    [Fact]
    public void MergeCompilerOptions_NullSourceReturnsTarget()
    {
        var target = new CompilerOptions { Strict = Tristate.True };
        var result = ParsingHelpers.mergeCompilerOptions(target, null, null);
        Assert.Same(target, result);
        Assert.Equal(Tristate.True, target.Strict);
    }
}
