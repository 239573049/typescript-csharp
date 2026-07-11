// Ported/adapted from tsgo internal/tsoptions/commandlineparser_test.go
// (TestParseCommandLineTypeRootsRelativePath, TestResponseFileDoesNotPanic) plus targeted
// coverage of the option-parsing paths (boolean, enum, list, absolute-path conversion, tsc -b
// nonsensical combinations). The baseline-diff TestCommandLineParseResult is deferred to the
// phase-4 batch runner (needs the TypeScript submodule + diagnosticwriter).
//
// PORT-NOTE: types are fully qualified to keep the file's using set minimal (a stray namespace
// import interacted badly with alias resolution in this project's test compilation).

using System.Collections.Generic;
using Ts.Core;
using Ts.TsOptions;
using Xunit;

namespace Ts.TsOptions.Tests;

public class CommandLineParserTests
{
    private sealed class TestConfigHost : ParseConfigHost
    {
        private readonly global::Ts.Vfs.IFileSystem fs;
        private readonly string cwd;

        public TestConfigHost(global::Ts.Vfs.IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public global::Ts.Vfs.IFileSystem FS() => fs;

        public string GetCurrentDirectory() => cwd;
    }

    private static TestConfigHost Host(string cwd, bool caseSensitive = true, Dictionary<string, string>? files = null)
        => new(new global::Ts.Vfs.Tests.FakeFs(files, caseSensitive), cwd);

    [Fact]
    public void TypeRootsRelativePath()
    {
        var host = Host("/home/project", true, new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["/home/project/bug.ts"] = "let x = 1;",
        });

        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "--typeRoots", "t", "bug.ts" }, host);

        var typeRoots = cmdLine.CompilerOptions()!.TypeRoots;
        Assert.NotNull(typeRoots);
        Assert.Single(typeRoots!);
        Assert.Equal("/home/project/t", typeRoots![0]);
    }

    [Fact]
    public void BooleanFlagsAndFileNames()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "--strict", "a.ts", "b.ts" }, Host("/c"));
        Assert.Equal(Tristate.True, cmdLine.CompilerOptions()!.Strict);
        Assert.Equal(new[] { "a.ts", "b.ts" }, cmdLine.FileNames());
    }

    [Fact]
    public void ExplicitBooleanFalse()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "--strictNullChecks", "false", "0.ts" }, Host("/c"));
        Assert.Equal(Tristate.False, cmdLine.CompilerOptions()!.StrictNullChecks);
        Assert.Equal(new[] { "0.ts" }, cmdLine.FileNames());
    }

    [Fact]
    public void EnumOption_Target()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "--target", "es2015", "0.ts" }, Host("/c"));
        Assert.Equal(ScriptTarget.ES2015, cmdLine.CompilerOptions()!.Target);
    }

    [Fact]
    public void ListOption_LibResolvesToFileNames()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "--lib", "es6,es2015.symbol.wellknown", "0.ts" }, Host("/c"));
        var lib = cmdLine.CompilerOptions()!.Lib;
        Assert.NotNull(lib);
        Assert.Equal(new[] { "lib.es2015.d.ts", "lib.es2015.symbol.wellknown.d.ts" }, lib);
    }

    [Fact]
    public void UnknownOptionProducesError()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "--notARealOption", "0.ts" }, Host("/c"));
        Assert.NotEmpty(cmdLine.Errors!);
    }

    [Fact]
    public void ShortNameResolves()
    {
        // -d is the short name for declaration
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "-d", "0.ts" }, Host("/c"));
        Assert.Equal(Tristate.True, cmdLine.CompilerOptions()!.Declaration);
    }

    [Fact]
    public void ResponseFileEmptyNameErrorsNoPanic()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "@" }, Host("/cwd"));
        Assert.NotEmpty(cmdLine.Errors!);
    }

    [Fact]
    public void ResponseFileMissingFileErrorsNoPanic()
    {
        var cmdLine = CommandLineParser.ParseCommandLine(new[] { "@blah" }, Host("/cwd"));
        Assert.NotEmpty(cmdLine.Errors!);
    }

    [Fact]
    public void BuildCommandLine_EmptyProjectsDefaultsToDot()
    {
        var result = CommandLineParser.ParseBuildCommandLine(new[] { "--verbose" }, Host("/c"));
        Assert.Equal(new[] { "." }, result.Projects);
        Assert.Equal(Tristate.True, result.BuildOptions!.Verbose);
    }

    [Fact]
    public void BuildCommandLine_CleanAndForceCannotCombine()
    {
        var result = CommandLineParser.ParseBuildCommandLine(new[] { "--clean", "--force" }, Host("/c"));
        Assert.Equal(Tristate.True, result.BuildOptions!.Clean);
        Assert.Equal(Tristate.True, result.BuildOptions.Force);
        Assert.NotEmpty(result.Errors!);
    }
}
