// Smoke tests for the tsgo internal/ls linkedediting.go port.

using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class LinkedEditingTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.tsx";

    [Fact]
    public void ProvideLinkedEditingRangeLinksJsxOpenAndCloseTagNames()
    {
        // const x = <div>text</div>;\n
        //           ^10 ^11-14      ^21-24
        const string source = "const x = <div>text</div>;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        // Cursor inside the opening tag name `div` (offset 12, between 'd' and 'i').
        var result = service.ProvideLinkedEditingRange(FileName, 12);

        Assert.NotNull(result);
        Assert.Equal(2, result!.Ranges.Length);

        // Opening tag name `div` spans [11, 14) on line 0.
        Assert.Equal(0u, result.Ranges[0].Start.Line);
        Assert.Equal(11u, result.Ranges[0].Start.Character);
        Assert.Equal(0u, result.Ranges[0].End.Line);
        Assert.Equal(14u, result.Ranges[0].End.Character);

        // Closing tag name `div` spans [21, 24) on line 0.
        Assert.Equal(0u, result.Ranges[1].Start.Line);
        Assert.Equal(21u, result.Ranges[1].Start.Character);
        Assert.Equal(0u, result.Ranges[1].End.Line);
        Assert.Equal(24u, result.Ranges[1].End.Character);

        Assert.Equal("[a-zA-Z0-9:\\-\\._$]*", result.WordPattern);
    }

    [Fact]
    public void ProvideLinkedEditingRangeReturnsNullOutsideTagNames()
    {
        const string source = "const x = <div>text</div>;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        // Cursor inside the JSX text child, not a tag name.
        var result = service.ProvideLinkedEditingRange(FileName, 17);

        Assert.Null(result);
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
            Jsx = global::Ts.Core.JsxEmit.Preserve,
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
