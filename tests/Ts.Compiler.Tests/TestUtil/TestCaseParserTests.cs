// Unit tests for Ts.TestUtil.TestCaseParser (port of internal/testrunner/test_case_parser.go's
// pure-text splitter). Covers the directive set called out in plan/08-testing-strategy.md §4.3:
// multi-file // @filename split, global // @option:, // @currentDirectory, file-scoped
// // @symlink, global // @link, the fourslash emitthisfile file directive, single-file fallback,
// extractCompilerSettings, and the "content before first filename" guard.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.TestUtil.Tests;

public class TestCaseParserTests
{
    private sealed record TestUnit(string Name, string Content, IReadOnlyDictionary<string, string> Options);

    private static readonly ParseFileFunc<TestUnit> s_makeUnit =
        (name, content, options) => new TestUnit(name, content, options);

    private static ParseTestFilesResult<TestUnit> Parse(string code, string fileName = "test.ts") =>
        TestCaseParser.ParseTestFilesAndSymlinks(code, fileName, s_makeUnit);

    [Fact]
    public void SplitsMultipleFilesOnFilenameDirective()
    {
        ParseTestFilesResult<TestUnit> result = Parse(
            "// @filename: a.ts\nvar a = 1;\n// @filename: b.ts\nvar b = 2;");

        Assert.Null(result.Error);
        Assert.Equal(2, result.Units.Count);
        Assert.Equal("a.ts", result.Units[0].Name);
        Assert.Equal("var a = 1;", result.Units[0].Content);
        Assert.Equal("b.ts", result.Units[1].Name);
        Assert.Equal("var b = 2;", result.Units[1].Content);
    }

    [Fact]
    public void RecordsGlobalOptionsAndCurrentDirectory()
    {
        ParseTestFilesResult<TestUnit> result = Parse(
            "// @currentDirectory: /foo\n// @target: esnext\n// @filename: a.ts\nx;");

        Assert.Equal("/foo", result.CurrentDirectory);
        Assert.Equal("esnext", result.GlobalOptions["target"]);
        // currentDirectory is recorded both as the current dir and as a global option.
        Assert.Equal("/foo", result.GlobalOptions["currentdirectory"]);
        Assert.Single(result.Units);
        Assert.Equal("a.ts", result.Units[0].Name);
    }

    [Fact]
    public void OptionNamesAreLowercasedValuesAreNot()
    {
        ParseTestFilesResult<TestUnit> result = Parse("// @Target: ESNext\n// @filename: a.ts\nx;");
        Assert.Equal("ESNext", result.GlobalOptions["target"]);
    }

    [Fact]
    public void FileScopedSymlinkDirectiveMapsToCurrentFile()
    {
        ParseTestFilesResult<TestUnit> result = Parse(
            "// @filename: real.ts\nexport const x = 1;\n// @symlink: link.ts, other.ts");

        Assert.Equal("real.ts", result.Symlinks["link.ts"]);
        Assert.Equal("real.ts", result.Symlinks["other.ts"]);
    }

    [Fact]
    public void GlobalLinkDirectiveMapsDestinationToSource()
    {
        // Pattern is `src -> dst`; symlinks[dst] = src.
        ParseTestFilesResult<TestUnit> result = Parse(
            "// @link: /src/a.ts -> /out/a.ts\n// @filename: a.ts\nx;");

        Assert.Equal("/src/a.ts", result.Symlinks["/out/a.ts"]);
    }

    [Fact]
    public void EmitThisFileIsAFileScopedDirective()
    {
        ParseTestFilesResult<TestUnit> result = Parse(
            "// @filename: a.ts\n// @emitthisfile: true\nx;");

        Assert.Single(result.Units);
        Assert.Equal("true", result.Units[0].Options["emitthisfile"]);
        // A file-scoped directive must not leak into global options.
        Assert.False(result.GlobalOptions.ContainsKey("emitthisfile"));
    }

    [Fact]
    public void SingleFileWithoutDirectivesUsesBaseFileName()
    {
        ParseTestFilesResult<TestUnit> result = Parse("just some content", fileName: "dir/test.ts");

        Assert.Single(result.Units);
        Assert.Equal("test.ts", result.Units[0].Name);
        Assert.Equal("just some content", result.Units[0].Content);
    }

    [Fact]
    public void NonCommentContentBeforeFirstFilenameThrows()
    {
        Assert.Throws<System.InvalidOperationException>(
            () => Parse("var leaked = 1;\n// @filename: a.ts\nx;"));
    }

    [Fact]
    public void CommentContentBeforeFirstFilenameIsAllowed()
    {
        // Leading trivia (a comment) is not "content", so no throw.
        ParseTestFilesResult<TestUnit> result = Parse("// just a comment\n// @filename: a.ts\nx;");
        Assert.Null(result.Error);
        Assert.Single(result.Units);
        Assert.Equal("a.ts", result.Units[0].Name);
    }

    [Fact]
    public void CompilerModeDropsLeadingBlankLines()
    {
        ParseTestFilesResult<TestUnit> result = Parse("\n\ncontent");
        Assert.Single(result.Units);
        Assert.Equal("content", result.Units[0].Content);
    }

    [Fact]
    public void ImplicitFirstFilePreservesLeadingBlankLines()
    {
        ParseTestFilesResult<TestUnit> result = TestCaseParser.ParseTestFilesAndSymlinksWithOptions(
            "\n\ncontent",
            "test.ts",
            s_makeUnit,
            new ParseTestFilesOptions { AllowImplicitFirstFile = true });

        Assert.Single(result.Units);
        Assert.Equal("test.ts", result.Units[0].Name);
        Assert.Equal("\n\ncontent", result.Units[0].Content);
    }

    [Fact]
    public void ExtractCompilerSettingsStripsTrailingSemicolon()
    {
        Dictionary<string, string> opts = TestCaseParser.ExtractCompilerSettings(
            "// @target: ESNext;\n// @strict: true");

        Assert.Equal("ESNext", opts["target"]);
        Assert.Equal("true", opts["strict"]);
    }
}
