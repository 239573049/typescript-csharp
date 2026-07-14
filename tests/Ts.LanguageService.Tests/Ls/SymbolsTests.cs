// Smoke tests for the tsgo internal/ls symbols.go (document symbols) port.

using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class SymbolsTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void ProvideDocumentSymbolsReturnsClassWithMethodAndPropertyChildren()
    {
        const string source =
            "class C {\n" +                    // line 0
            "    prop: number = 1;\n" +        // line 1
            "    method() { }\n" +             // line 2
            "}\n";                              // line 3
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var response = service.ProvideDocumentSymbols(
            FileName,
            hierarchicalDocumentSymbolSupport: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response.DocumentSymbols);
        var classSymbol = Assert.Single(response.DocumentSymbols!);
        Assert.Equal("C", classSymbol.Name);
        Assert.Equal(global::Ts.LsProto.SymbolKind.Class, classSymbol.Kind);
        // Selection range covers just the class name identifier on line 0.
        Assert.Equal(0u, classSymbol.SelectionRange.Start.Line);
        Assert.Equal(6u, classSymbol.SelectionRange.Start.Character);
        Assert.Equal(0u, classSymbol.SelectionRange.End.Line);
        Assert.Equal(7u, classSymbol.SelectionRange.End.Character);
        // Full range covers the whole declaration.
        Assert.Equal(0u, classSymbol.Range.Start.Line);
        Assert.Equal(3u, classSymbol.Range.End.Line);

        Assert.NotNull(classSymbol.Children);
        Assert.Equal(2, classSymbol.Children!.Count);
        Assert.Equal("prop", classSymbol.Children[0].Name);
        Assert.Equal(global::Ts.LsProto.SymbolKind.Property, classSymbol.Children[0].Kind);
        Assert.Equal("method", classSymbol.Children[1].Name);
        Assert.Equal(global::Ts.LsProto.SymbolKind.Method, classSymbol.Children[1].Kind);
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
