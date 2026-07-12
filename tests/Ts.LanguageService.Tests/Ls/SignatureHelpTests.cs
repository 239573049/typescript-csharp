// Smoke tests for the tsgo internal/ls signaturehelp port.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class SignatureHelpTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void GetSignatureHelpItemsInsideEmptyArgumentList()
    {
        const string source = "function f(x: number) { }\nf()\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        var position = source.IndexOf("f()", StringComparison.Ordinal) + "f(".Length;

        var help = service.GetSignatureHelpItems(
            FileName,
            position,
            TestContext.Current.CancellationToken);

        Assert.NotNull(help);
        var signature = Assert.Single(help.Signatures);
        Assert.Equal("f(x: number)", signature.Label);
        var parameter = Assert.Single(signature.Parameters);
        Assert.Equal("x: number", parameter.Label);
        Assert.False(signature.IsVariadic);
        Assert.Equal(0, help.ActiveSignature);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void GetSignatureHelpItemsReportsSecondActiveParameter()
    {
        const string source = "function add(x: number, y: number): number { return x + y; }\nadd(1, 2)\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        // Position on the second argument.
        var position = source.IndexOf("add(1, 2", StringComparison.Ordinal) + "add(1, ".Length;

        var help = service.GetSignatureHelpItems(
            FileName,
            position,
            TestContext.Current.CancellationToken);

        Assert.NotNull(help);
        var signature = Assert.Single(help.Signatures);
        Assert.Equal("add(x: number, y: number): number", signature.Label);
        Assert.Equal(2, signature.Parameters.Length);
        Assert.Equal("x: number", signature.Parameters[0].Label);
        Assert.Equal("y: number", signature.Parameters[1].Label);
        Assert.Equal(0, help.ActiveSignature);
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public void GetSignatureHelpItemsClampsActiveParameterForTrailingRest()
    {
        const string source = "function v(...args: number[]) { }\nv(1, 2, 3)\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        // Position on the third argument: the rest parameter stays active.
        var position = source.IndexOf("v(1, 2, 3", StringComparison.Ordinal) + "v(1, 2, ".Length;

        var help = service.GetSignatureHelpItems(
            FileName,
            position,
            TestContext.Current.CancellationToken);

        Assert.NotNull(help);
        var signature = Assert.Single(help.Signatures);
        Assert.Equal("v(...args: number[])", signature.Label);
        Assert.True(signature.IsVariadic);
        Assert.Equal(0, help.ActiveSignature);
        Assert.Equal(0, help.ActiveParameter);
    }

    [Fact]
    public void GetSignatureHelpItemsReturnsNullOutsideAnyCall()
    {
        const string source = "const answer = 42;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var help = service.GetSignatureHelpItems(
            FileName,
            source.IndexOf("42", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        Assert.Null(help);
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
