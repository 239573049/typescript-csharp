// Smoke tests for the tsgo internal/ls findallreferences port.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class FindAllReferencesTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";
    private const string SecondFileName = "/src/other.ts";

    [Fact]
    public void GetReferencesAtPositionReturnsDeclarationAndAllUses()
    {
        const string source = "const a = 1; const b = a + a;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var declarationPosition = source.IndexOf("a = 1", StringComparison.Ordinal);
        var firstUsePosition = source.IndexOf("a + a", StringComparison.Ordinal);
        var secondUsePosition = firstUsePosition + "a + ".Length;

        var references = service.GetReferencesAtPosition(
            FileName,
            secondUsePosition,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, references.Length);
        Assert.All(references, reference => Assert.Equal(FileName, reference.FileName));
        Assert.Equal(
            new global::Ts.Core.TextRange(declarationPosition, declarationPosition + 1),
            references[0].TextSpan);
        Assert.Equal(
            new global::Ts.Core.TextRange(firstUsePosition, firstUsePosition + 1),
            references[1].TextSpan);
        Assert.Equal(
            new global::Ts.Core.TextRange(secondUsePosition, secondUsePosition + 1),
            references[2].TextSpan);
    }

    [Fact]
    public void GetReferencesAtPositionExcludesShadowingDeclarations()
    {
        const string source = "const a = 1; { const a = 2; a; } a;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        // Query on the inner (shadowing) declaration's use: only the inner pair matches.
        var innerDeclarationPosition = source.IndexOf("a = 2", StringComparison.Ordinal);
        var innerUsePosition = source.IndexOf("a;", StringComparison.Ordinal);

        var references = service.GetReferencesAtPosition(
            FileName,
            innerUsePosition,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, references.Length);
        Assert.Equal(
            new global::Ts.Core.TextRange(innerDeclarationPosition, innerDeclarationPosition + 1),
            references[0].TextSpan);
        Assert.Equal(
            new global::Ts.Core.TextRange(innerUsePosition, innerUsePosition + 1),
            references[1].TextSpan);
    }

    [Fact]
    public void GetReferencesAtPositionFindsGlobalUsesAcrossFiles()
    {
        // Two non-module scripts share the global scope; the reference scan must cover
        // every source file of the program.
        const string mainSource = "var shared = 1;\n";
        const string otherSource = "shared = 2;\n";
        var program = BuildProgram(
            new Dictionary<string, string>
            {
                [FileName] = mainSource,
                [SecondFileName] = otherSource,
            },
            [FileName, SecondFileName]);
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var declarationPosition = mainSource.IndexOf("shared", StringComparison.Ordinal);

        var references = service.GetReferencesAtPosition(
            FileName,
            declarationPosition,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, references.Length);
        Assert.Equal(FileName, references[0].FileName);
        Assert.Equal(
            new global::Ts.Core.TextRange(declarationPosition, declarationPosition + "shared".Length),
            references[0].TextSpan);
        Assert.Equal(SecondFileName, references[1].FileName);
        Assert.Equal(
            new global::Ts.Core.TextRange(0, "shared".Length),
            references[1].TextSpan);
    }

    private static global::Ts.Compiler.Program BuildProgram(IReadOnlyDictionary<string, string> files)
    {
        return BuildProgram(files, [FileName]);
    }

    private static global::Ts.Compiler.Program BuildProgram(
        IReadOnlyDictionary<string, string> files,
        string[] rootFiles)
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
            rootFiles,
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
