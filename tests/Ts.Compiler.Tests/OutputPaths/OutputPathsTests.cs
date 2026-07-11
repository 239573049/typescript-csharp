// Unit tests for the Ts.OutputPaths port (internal/outputpaths has no *_test.go upstream;
// these validate the emit-path computation that later phases' emit baselines rely on).

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Xunit;

namespace Ts.OutputPaths.Tests;

public class OutputPathsTests
{
    private sealed class TestHost : OutputPathsHost
    {
        private readonly string commonSourceDirectory;
        private readonly string currentDirectory;
        private readonly bool useCaseSensitiveFileNames;

        public TestHost(string commonSourceDirectory, string currentDirectory, bool useCaseSensitiveFileNames)
        {
            this.commonSourceDirectory = commonSourceDirectory;
            this.currentDirectory = currentDirectory;
            this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
        }

        public string CommonSourceDirectory() => commonSourceDirectory;
        public string GetCurrentDirectory() => currentDirectory;
        public bool UseCaseSensitiveFileNames() => useCaseSensitiveFileNames;
    }

    private static SourceFile Parse(string fileName, string text, ScriptKind scriptKind)
    {
        var opts = new SourceFileParseOptions
        {
            FileName = fileName,
            Path = new Path(fileName),
        };
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, scriptKind);
    }

    [Theory]
    [InlineData("/a/b.ts", JsxEmit.None, ".js")]
    [InlineData("/a/b.tsx", JsxEmit.React, ".js")]
    [InlineData("/a/b.tsx", JsxEmit.Preserve, ".jsx")]
    [InlineData("/a/b.jsx", JsxEmit.Preserve, ".jsx")]
    [InlineData("/a/b.json", JsxEmit.None, ".json")]
    [InlineData("/a/b.mts", JsxEmit.None, ".mjs")]
    [InlineData("/a/b.mjs", JsxEmit.None, ".mjs")]
    [InlineData("/a/b.cts", JsxEmit.None, ".cjs")]
    [InlineData("/a/b.cjs", JsxEmit.None, ".cjs")]
    public void GetOutputExtension(string fileName, JsxEmit jsx, string expected)
    {
        Assert.Equal(expected, OutputPaths.GetOutputExtension(fileName, jsx));
    }

    [Fact]
    public void GetSourceMapFilePath_SourceMapEnabled()
    {
        var options = new CompilerOptions { SourceMap = Tristate.True };
        Assert.Equal("/out/a.js.map", OutputPaths.GetSourceMapFilePath("/out/a.js", options));
    }

    [Fact]
    public void GetSourceMapFilePath_Disabled()
    {
        Assert.Equal("", OutputPaths.GetSourceMapFilePath("/out/a.js", new CompilerOptions()));
    }

    [Fact]
    public void GetSourceMapFilePath_InlineSuppresses()
    {
        var options = new CompilerOptions { SourceMap = Tristate.True, InlineSourceMap = Tristate.True };
        Assert.Equal("", OutputPaths.GetSourceMapFilePath("/out/a.js", options));
    }

    [Fact]
    public void GetOutputJSFileName_NoOutDir()
    {
        var host = new TestHost("/src/", "/", true);
        Assert.Equal("/src/a.js", OutputPaths.GetOutputJSFileName("/src/a.ts", new CompilerOptions(), host));
    }

    [Fact]
    public void GetDeclarationEmitOutputFilePath_NoOutDir()
    {
        var host = new TestHost("/src/", "/", true);
        Assert.Equal("/src/a.d.ts", OutputPaths.GetDeclarationEmitOutputFilePath("/src/a.ts", new CompilerOptions(), host));
    }

    [Fact]
    public void GetOutputPathsFor_WithOutDir()
    {
        var host = new TestHost("/src/", "/", true);
        var options = new CompilerOptions { OutDir = "/out", SourceMap = Tristate.True };
        SourceFile sourceFile = Parse("/src/a.ts", "export const x = 1;", ScriptKind.TS);

        OutputPaths paths = OutputPaths.GetOutputPathsFor(sourceFile, options, host, forceDtsEmit: true);

        Assert.Equal("/out/a.js", paths.JsFilePath());
        Assert.Equal("/out/a.js.map", paths.SourceMapFilePath());
        Assert.Equal("/out/a.d.ts", paths.DeclarationFilePath());
        Assert.Equal("", paths.DeclarationMapPath());
    }

    [Fact]
    public void GetOutputPathsFor_JsonEmittedToSameLocationSkipsJs()
    {
        var host = new TestHost("/src/", "/", true);
        // No outDir: a .json file's own output path equals its input path, so JS emit is skipped.
        SourceFile sourceFile = Parse("/src/data.json", "{}", ScriptKind.Json);

        OutputPaths paths = OutputPaths.GetOutputPathsFor(sourceFile, new CompilerOptions(), host, forceDtsEmit: false);

        Assert.Equal("", paths.JsFilePath());
        Assert.Equal("", paths.SourceMapFilePath());
    }

    [Fact]
    public void GetComputedCommonSourceDirectory_SameDirectory()
    {
        string result = OutputPaths.GetComputedCommonSourceDirectory(
            new[] { "/src/a.ts", "/src/b.ts" },
            "/",
            true);
        Assert.Equal("/src/", result);
    }

    [Fact]
    public void GetBuildInfoFileName_NotIncrementalReturnsEmpty()
    {
        Assert.Equal("", OutputPaths.GetBuildInfoFileName(new CompilerOptions(), default));
    }

    [Fact]
    public void GetBuildInfoFileName_BuildWithExplicitFile()
    {
        var options = new CompilerOptions { Build = Tristate.True, TsBuildInfoFile = "/custom.tsbuildinfo" };
        Assert.Equal("/custom.tsbuildinfo", OutputPaths.GetBuildInfoFileName(options, default));
    }
}
