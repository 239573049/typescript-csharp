// Smoke tests for the tsgo internal/ls folding.go port.

using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class FoldingTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void ProvideFoldingRangeFoldsImportGroupAndNestedBlocks()
    {
        const string source =
            "import { a } from \"./a\";\n" +   // line 0
            "import { b } from \"./b\";\n" +   // line 1
            "\n" +                              // line 2
            "function outer() {\n" +            // line 3
            "    if (a) {\n" +                  // line 4
            "        b;\n" +                    // line 5
            "    }\n" +                         // line 6
            "}\n";                              // line 7
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var ranges = service.ProvideFoldingRange(
            FileName,
            cancellationToken: TestContext.Current.CancellationToken);

        // The two-import group folds with the "imports" kind, from the first import
        // keyword to the end of the last import statement.
        Assert.Contains(ranges, r =>
            r.Kind is { } kind && kind == global::Ts.LsProto.FoldingRangeKind.Imports &&
            r.StartLine == 0 && r.EndLine == 1);
        // The function body block folds across its braces.
        Assert.Contains(ranges, r => r.StartLine == 3 && r.EndLine == 7);
        // The nested if-block folds independently.
        Assert.Contains(ranges, r => r.StartLine == 4 && r.EndLine == 6);
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
