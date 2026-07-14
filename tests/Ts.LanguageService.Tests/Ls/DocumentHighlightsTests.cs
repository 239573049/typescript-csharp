// Smoke tests for the tsgo internal/ls documenthighlights port.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class DocumentHighlightsTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void ProvideDocumentHighlightsReturnsDeclarationAndUse()
    {
        const string source = "const value = 1; const twice = value + value;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var declarationPosition = source.IndexOf("value = 1", StringComparison.Ordinal);
        var firstUsePosition = source.IndexOf("value + value", StringComparison.Ordinal);
        var secondUsePosition = firstUsePosition + "value + ".Length;

        var highlights = service.ProvideDocumentHighlights(
            FileName,
            firstUsePosition,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, highlights.Length);

        // Declaration site (`const value = 1` initializes the variable → write access).
        Assert.Equal(0u, highlights[0].Range.Start.Line);
        Assert.Equal((uint)declarationPosition, highlights[0].Range.Start.Character);
        Assert.Equal(0u, highlights[0].Range.End.Line);
        Assert.Equal((uint)(declarationPosition + "value".Length), highlights[0].Range.End.Character);
        Assert.Equal(global::Ts.LsProto.DocumentHighlightKind.Write, highlights[0].Kind);

        // First use site (read access).
        Assert.Equal(0u, highlights[1].Range.Start.Line);
        Assert.Equal((uint)firstUsePosition, highlights[1].Range.Start.Character);
        Assert.Equal(0u, highlights[1].Range.End.Line);
        Assert.Equal((uint)(firstUsePosition + "value".Length), highlights[1].Range.End.Character);
        Assert.Equal(global::Ts.LsProto.DocumentHighlightKind.Read, highlights[1].Kind);

        // Second use site (read access).
        Assert.Equal((uint)secondUsePosition, highlights[2].Range.Start.Character);
        Assert.Equal((uint)(secondUsePosition + "value".Length), highlights[2].Range.End.Character);
        Assert.Equal(global::Ts.LsProto.DocumentHighlightKind.Read, highlights[2].Kind);
    }

    [Fact]
    public void ProvideDocumentHighlightsFallsBackToSyntacticKeywordHighlights()
    {
        const string source = "function f(n: number) { if (n > 0) { return 1; } else { return 2; } }\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var ifPosition = source.IndexOf("if (", StringComparison.Ordinal);
        var elsePosition = source.IndexOf("else", StringComparison.Ordinal);

        var highlights = service.ProvideDocumentHighlights(
            FileName,
            ifPosition,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, highlights.Length);

        // `if` keyword.
        Assert.Equal((uint)ifPosition, highlights[0].Range.Start.Character);
        Assert.Equal((uint)(ifPosition + "if".Length), highlights[0].Range.End.Character);
        Assert.Equal(global::Ts.LsProto.DocumentHighlightKind.Read, highlights[0].Kind);

        // `else` keyword.
        Assert.Equal((uint)elsePosition, highlights[1].Range.Start.Character);
        Assert.Equal((uint)(elsePosition + "else".Length), highlights[1].Range.End.Character);
        Assert.Equal(global::Ts.LsProto.DocumentHighlightKind.Read, highlights[1].Kind);
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
