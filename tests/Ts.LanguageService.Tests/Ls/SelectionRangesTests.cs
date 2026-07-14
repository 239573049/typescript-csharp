// Smoke tests for the tsgo internal/ls selectionranges.go port.

using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class SelectionRangesTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void ProvideSelectionRangesExpandsFromArgumentToFullFile()
    {
        // call(arg1, arg2);\n
        // 0123456789012345678
        const string source = "call(arg1, arg2);\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        // Cursor inside `arg1` (offset 6, between 'a' and 'r').
        var results = service.ProvideSelectionRanges(FileName, [6]);

        var range = Assert.Single(results);

        // Innermost: the `arg1` identifier [5, 9).
        AssertRange(range, 0, 5, 0, 9);
        // Next: the argument list `arg1, arg2` [5, 15).
        Assert.NotNull(range.Parent);
        range = range.Parent!;
        AssertRange(range, 0, 5, 0, 15);
        // Next: the call expression `call(arg1, arg2)` [0, 16).
        Assert.NotNull(range.Parent);
        range = range.Parent!;
        AssertRange(range, 0, 0, 0, 16);
        // Next: the expression statement `call(arg1, arg2);` [0, 17).
        Assert.NotNull(range.Parent);
        range = range.Parent!;
        AssertRange(range, 0, 0, 0, 17);
        // Outermost: the full source file [0, 18) — the trailing newline puts the
        // end position at the start of line 1.
        Assert.NotNull(range.Parent);
        range = range.Parent!;
        AssertRange(range, 0, 0, 1, 0);
        Assert.Null(range.Parent);
    }

    private static void AssertRange(
        global::Ts.LsProto.SelectionRange range,
        uint startLine,
        uint startCharacter,
        uint endLine,
        uint endCharacter)
    {
        Assert.Equal(startLine, range.Range.Start.Line);
        Assert.Equal(startCharacter, range.Range.Start.Character);
        Assert.Equal(endLine, range.Range.End.Line);
        Assert.Equal(endCharacter, range.Range.End.Character);
    }

    private static global::Ts.Compiler.Program BuildProgram(IReadOnlyDictionary<string, string> files)
    {
        var fileSystem = global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var compilerHost = global::Ts.Compiler.CompilerHosts.NewCompilerHost(
            CurrentDirectory,
            fileSystem,
            "/lib",
            extendedConfigCache: null,
            trace: null);
        var options = new global::Ts.Core.CompilerOptions
        {
            NoLib = global::Ts.Core.Tristate.True,
            Module = global::Ts.Core.ModuleKind.CommonJS,
            Types = [],
        };
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            [FileName],
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = true,
                CurrentDirectory = CurrentDirectory,
            });

        return new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Host = compilerHost,
            Config = config,
            SingleThreaded = global::Ts.Core.Tristate.True,
        });
    }
}
