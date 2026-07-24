// Smoke tests for the tsgo internal/ls completions port.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class CompletionsTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void GetCompletionsAtPositionReturnsObjectMembersAfterDot()
    {
        const string source = "var x = {a: 1, b: 2}; x.";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var completions = service.GetCompletionsAtPosition(
            FileName,
            source.Length,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(completions);
        Assert.Contains(completions!.Items!, entry => string.Equals(entry.Item!.Label, "a", StringComparison.Ordinal));
        Assert.Contains(completions.Items!, entry => string.Equals(entry.Item!.Label, "b", StringComparison.Ordinal));
    }

    [Fact]
    public void GetCompletionsAtPositionReturnsIdentifiersInScope()
    {
        const string source = "function f() { var foo = 42; return foo + ; }";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        var position = source.IndexOf("; }", StringComparison.Ordinal);

        var completions = service.GetCompletionsAtPosition(
            FileName,
            position,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(completions);
        Assert.Contains(completions!.Items!, entry => string.Equals(entry.Item!.Label, "foo", StringComparison.Ordinal));
        // Keywords flow in through the keyword filter merge.
        Assert.Contains(completions.Items!, entry => string.Equals(entry.Item!.Label, "return", StringComparison.Ordinal));
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
