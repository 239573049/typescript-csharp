// Smoke tests for the hover.go port (QuickInfo.cs): display-string shapes mirror tsgo hover output
// (plaintext path). The authoritative gate is the fourslash quickinfo corpus (phase 8); these pin
// the load-bearing display prefixes end-to-end through checker + node builder.

using System;
using System.Collections.Generic;
using Xunit;

namespace Ts.LanguageService.Tests.Ls;

public sealed class QuickInfoTests
{
    private const string CurrentDirectory = "/";
    private const string FileName = "/index.ts";

    [Fact]
    public void ConstVariableShowsConstPrefixAndType()
    {
        var service = MakeService("const answer = 42;\n");
        var info = service.GetQuickInfoAtPosition(FileName, "const an".Length, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("const answer: 42", info!.DisplayString);
    }

    [Fact]
    public void FunctionShowsSignature()
    {
        var service = MakeService("function greet(name: string): string { return name; }\ngreet(\"x\");\n");
        var info = service.GetQuickInfoAtPosition(FileName, "function gr".Length, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("function greet(name: string): string", info!.DisplayString);
    }

    [Fact]
    public void InterfaceShowsInterfacePrefix()
    {
        var service = MakeService("interface Shape { area(): number; }\nlet s: Shape;\n");
        var info = service.GetQuickInfoAtPosition(FileName, "interface Sh".Length, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("interface Shape", info!.DisplayString);
    }

    [Fact]
    public void PropertyShowsPropertyPrefix()
    {
        var source = "interface Shape { area: number; }\ndeclare const s: Shape;\ns.area;\n";
        var service = MakeService(source);
        var position = source.IndexOf("s.area", StringComparison.Ordinal) + "s.ar".Length;
        var info = service.GetQuickInfoAtPosition(FileName, position, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("(property) Shape.area: number", info!.DisplayString);
    }

    [Fact]
    public void JsDocDocumentationIsExtracted()
    {
        var source = "/** Adds two numbers. */\nfunction add(a: number, b: number): number { return a + b; }\nadd(1, 2);\n";
        var service = MakeService(source);
        var position = source.IndexOf("add(1", StringComparison.Ordinal) + 1;
        var info = service.GetQuickInfoAtPosition(FileName, position, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("function add(a: number, b: number): number", info!.DisplayString);
        Assert.Equal("Adds two numbers.", info.Documentation);
    }

    [Fact]
    public void TypeAliasShowsAliasedType()
    {
        var service = MakeService("type Point = { x: number; y: number; };\nlet p: Point;\n");
        var info = service.GetQuickInfoAtPosition(FileName, "type Po".Length, TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.StartsWith("type Point = {", info!.DisplayString, StringComparison.Ordinal);
    }

    private static global::Ts.Ls.LanguageService MakeService(string source)
    {
        var program = BuildProgram(new Dictionary<string, string> { [FileName] = source });
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
