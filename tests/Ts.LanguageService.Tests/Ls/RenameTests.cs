// Smoke tests for the tsgo internal/ls rename port.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class RenameTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";
    private const string FileUri = "file:///src/main.ts";

    [Fact]
    public void PrepareRenameOnIdentifierReturnsTriggerSpan()
    {
        const string source = "const value = 1; value; value;\n";
        var service = BuildService(source);

        var declarationPosition = source.IndexOf("value", StringComparison.Ordinal);

        var placeholder = service.PrepareRename(
            FileName,
            declarationPosition,
            TestContext.Current.CancellationToken);

        Assert.NotNull(placeholder);
        Assert.Equal("value", placeholder.Placeholder);
        Assert.Equal(0u, placeholder.Range.Start.Line);
        Assert.Equal((uint)declarationPosition, placeholder.Range.Start.Character);
        Assert.Equal(0u, placeholder.Range.End.Line);
        Assert.Equal((uint)(declarationPosition + "value".Length), placeholder.Range.End.Character);
    }

    [Fact]
    public void PrepareRenameOnKeywordReturnsNull()
    {
        const string source = "const value = 1; if (value) { value; }\n";
        var service = BuildService(source);

        var keywordPosition = source.IndexOf("if", StringComparison.Ordinal);

        var placeholder = service.PrepareRename(
            FileName,
            keywordPosition,
            TestContext.Current.CancellationToken);

        Assert.Null(placeholder);

        // The faithful RenameInfo channel carries the Go error message.
        var info = service.GetRenameInfo(
            FileName,
            keywordPosition,
            "",
            TestContext.Current.CancellationToken);
        Assert.False(info.CanRename);
        Assert.Equal("You cannot rename this element.", info.LocalizedErrorMessage);
    }

    [Fact]
    public void RenameLocalVariableEditsDeclarationAndAllUses()
    {
        const string source = "const value = 1; const other = value + value;\n";
        var service = BuildService(source);

        var declarationPosition = source.IndexOf("value", StringComparison.Ordinal);
        var firstUsePosition = source.IndexOf("value + value", StringComparison.Ordinal);
        var secondUsePosition = firstUsePosition + "value + ".Length;

        var edit = service.ProvideRename(
            FileName,
            secondUsePosition,
            "renamed",
            TestContext.Current.CancellationToken);

        Assert.NotNull(edit);
        Assert.NotNull(edit.Changes);
        var (uri, textEdits) = Assert.Single(edit.Changes);
        Assert.Equal(FileUri, uri.Value);
        Assert.Equal(3, textEdits.Length);

        int[] expectedStarts = [declarationPosition, firstUsePosition, secondUsePosition];
        for (var i = 0; i < expectedStarts.Length; i++)
        {
            Assert.Equal("renamed", textEdits[i].NewText);
            Assert.Equal(0u, textEdits[i].Range.Start.Line);
            Assert.Equal((uint)expectedStarts[i], textEdits[i].Range.Start.Character);
            Assert.Equal(0u, textEdits[i].Range.End.Line);
            Assert.Equal((uint)(expectedStarts[i] + "value".Length), textEdits[i].Range.End.Character);
        }
    }

    private static global::Ts.Ls.LanguageService BuildService(string source)
    {
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        return global::Ts.Ls.LanguageService.NewLanguageService(program);
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
