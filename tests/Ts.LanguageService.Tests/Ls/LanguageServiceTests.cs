// Foundation smoke tests for the tsgo internal/ls language-service port.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class LanguageServiceTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void NewLanguageServiceWrapsProgram()
    {
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = "const answer = 42;\n",
        });

        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        Assert.Same(program, service.GetProgram());
    }

    [Fact]
    public void GetDefinitionAtPositionReturnsDeclarationNameSpan()
    {
        const string source = "const answer: number = 42;\nanswer;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        var usePosition = source.LastIndexOf("answer", StringComparison.Ordinal) + 1;

        var definitions = service.GetDefinitionAtPosition(FileName, usePosition, TestContext.Current.CancellationToken);

        var definition = Assert.Single(definitions);
        var declarationPosition = source.IndexOf("answer", StringComparison.Ordinal);
        Assert.Equal(FileName, definition.FileName);
        Assert.Equal(new global::Ts.Core.TextRange(declarationPosition, declarationPosition + "answer".Length), definition.TextSpan);
    }

    [Fact]
    public void GetQuickInfoAtPositionReturnsIdentifierTypeDisplay()
    {
        const string source = "const answer: number = 42;\nanswer;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        var useStart = source.LastIndexOf("answer", StringComparison.Ordinal);
        var usePosition = useStart + 1;

        var quickInfo = service.GetQuickInfoAtPosition(FileName, usePosition, TestContext.Current.CancellationToken);

        Assert.NotNull(quickInfo);
        Assert.Contains("number", quickInfo.DisplayString, StringComparison.Ordinal);
        Assert.Equal(new global::Ts.Core.TextRange(useStart, useStart + "answer".Length), quickInfo.TextSpan);
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
