// Smoke tests for the tsgo internal/ls symbols.go workspace-symbol tail
// (getMatchScore / ProvideWorkspaceSymbols) and ast.SourceFile.GetDeclarationMap.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class WorkspaceSymbolsTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    // symbols.go:606-628 — getMatchScore.
    [Fact]
    public void GetMatchScoreMatchesSequenceCaseInsensitivelyAndScoresSkippedCharacters()
    {
        // Exact match scores zero.
        Assert.Equal(0, global::Ts.Ls.LanguageService.GetMatchScore("Foo", "Foo"));
        // A subsequence match counts the characters skipped in `s`.
        Assert.Equal(2, global::Ts.Ls.LanguageService.GetMatchScore("foobar", "fb"));
        // Lower case pattern characters match either case; upper case must match exactly.
        Assert.Equal(0, global::Ts.Ls.LanguageService.GetMatchScore("FOo", "fO"));
        // An exact-case match still scores the characters skipped before it.
        Assert.Equal(3, global::Ts.Ls.LanguageService.GetMatchScore("barFoo", "F"));
        // An upper case pattern character that never matches exactly fails.
        Assert.Equal(-1, global::Ts.Ls.LanguageService.GetMatchScore("Foo", "FO"));
        // Pattern characters must appear in order.
        Assert.Equal(-1, global::Ts.Ls.LanguageService.GetMatchScore("barfoo", "rb"));
        // Empty pattern matches everything with score 0.
        Assert.Equal(0, global::Ts.Ls.LanguageService.GetMatchScore("anything", ""));
    }

    [Fact]
    public void GetDeclarationMapIndexesFunctionsClassesAndMembers()
    {
        const string source =
            "class C {\n" +                    // line 0
            "    prop: number = 1;\n" +        // line 1
            "    method() { }\n" +             // line 2
            "}\n" +                            // line 3
            "function f() { }\n";              // line 4
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var file = program.GetSourceFile(FileName)!;
        var map = file.GetDeclarationMap();

        Assert.Contains("C", map.Keys);
        Assert.Contains("prop", map.Keys);
        Assert.Contains("method", map.Keys);
        Assert.Contains("f", map.Keys);
        Assert.Equal(global::Ts.Ast.SyntaxKind.ClassDeclaration, Assert.Single(map["C"]).Kind);
        Assert.Equal(global::Ts.Ast.SyntaxKind.FunctionDeclaration, Assert.Single(map["f"]).Kind);
    }

    [Fact]
    public void ProvideWorkspaceSymbolsReturnsTopMatchingDeclarations()
    {
        const string source =
            "class C {\n" +                    // line 0
            "    prop: number = 1;\n" +        // line 1
            "    method() { }\n" +             // line 2
            "}\n" +                            // line 3
            "function f() { }\n";              // line 4
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        var preferences = global::Ts.Ls.UserPreferences.NewDefaultUserPreferences();

        var response = global::Ts.Ls.LanguageService.ProvideWorkspaceSymbols(
            global::Ts.Core.RequestContext.Background,
            [program],
            service.Converters(),
            preferences,
            "");

        var symbols = response.SymbolInformations!;
        // Query "" matches every declaration and sorts case-insensitively by name.
        Assert.Equal(new[] { "C", "f", "method", "prop" }, symbols.Select(s => s.Name).ToArray());
        Assert.Equal(global::Ts.LsProto.SymbolKind.Class, symbols[0].Kind);
        Assert.Equal(global::Ts.LsProto.SymbolKind.Function, symbols[1].Kind);
        Assert.Equal(global::Ts.LsProto.SymbolKind.Method, symbols[2].Kind);
        Assert.Equal(global::Ts.LsProto.SymbolKind.Property, symbols[3].Kind);
        // Members are attributed to their containing class; a top-level function's
        // container is the source file, whose declaration name is "" (Go stores a
        // pointer to "" and emits "containerName": "").
        Assert.Equal("C", symbols[2].ContainerName);
        Assert.Equal("C", symbols[3].ContainerName);
        Assert.Equal("", symbols[1].ContainerName);
        // The location names the identifier "C" on line 0 (columns 6..7).
        Assert.Equal("file:///src/main.ts", symbols[0].Location.Uri);
        Assert.Equal(0u, symbols[0].Location.Range.Start.Line);
        Assert.Equal(6u, symbols[0].Location.Range.Start.Character);
        Assert.Equal(7u, symbols[0].Location.Range.End.Character);
    }

    [Fact]
    public void ProvideWorkspaceSymbolsFiltersByQuery()
    {
        const string source =
            "class C {\n" +
            "    method() { }\n" +
            "}\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var response = global::Ts.Ls.LanguageService.ProvideWorkspaceSymbols(
            global::Ts.Core.RequestContext.Background,
            [program],
            service.Converters(),
            global::Ts.Ls.UserPreferences.NewDefaultUserPreferences(),
            "me");

        var symbol = Assert.Single(response.SymbolInformations!);
        Assert.Equal("method", symbol.Name);
        Assert.Equal("C", symbol.ContainerName);
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
