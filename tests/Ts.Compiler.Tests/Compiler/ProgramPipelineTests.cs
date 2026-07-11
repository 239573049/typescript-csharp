// Phase 4/5 milestone smoke test: proves the fileloader → module.Resolver → binder pipeline is
// actually wired end to end. Builds a 3-file in-memory program whose files import each other
// transitively, constructs a Program (load → parse → resolve → bind), and asserts:
//   1. all three files are discovered and parsed from a single root via import recursion,
//   2. each import is resolved by module.Resolver to the correct target file,
//   3. binding runs and produces symbol tables,
//   4. nothing crashes.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Core;
using Ts.Module;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Compiler.Tests;

public sealed class ProgramPipelineTests
{
    private const string CurrentDirectory = "/src";

    private static Program BuildProgram(IReadOnlyDictionary<string, string> files, string[] rootFileNames, CompilerOptions options)
    {
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var host = CompilerHostImpl.NewCompilerHost(CurrentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            rootFileNames,
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = true,
                CurrentDirectory = CurrentDirectory,
            });
        var opts = new ProgramOptions
        {
            Host = host,
            Config = config,
            SingleThreaded = Tristate.True,
        };
        return new Program(opts);
    }

    private static CompilerOptions Options() => new()
    {
        NoLib = Tristate.True,          // keep the test hermetic: no default lib files
        Module = ModuleKind.CommonJS,
        Types = [],                     // no automatic @types directive scan
    };

    [Fact]
    public void LoadsParsesResolvesAndBinds_TransitiveImportGraph()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/a.ts"] = "import { b } from \"./b\";\nexport const a = b + 1;\n",
            ["/src/b.ts"] = "import { c } from \"./c\";\nexport const b = c + 1;\n",
            ["/src/c.ts"] = "export const c = 1;\n",
        };

        // Single root; b.ts and c.ts must be discovered purely by following imports.
        var program = BuildProgram(files, ["/src/a.ts"], Options());

        // 1. All three files discovered + parsed from the one root via import recursion.
        var sourceFiles = program.SourceFiles();
        var fileNames = sourceFiles.Select(f => f.FileName()).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "/src/a.ts", "/src/b.ts", "/src/c.ts" }, fileNames);

        var aFile = program.GetSourceFile("/src/a.ts");
        var bFile = program.GetSourceFile("/src/b.ts");
        var cFile = program.GetSourceFile("/src/c.ts");
        Assert.NotNull(aFile);
        Assert.NotNull(bFile);
        Assert.NotNull(cFile);

        // 2. Imports were resolved by module.Resolver to the correct targets.
        AssertResolvesTo(program, aFile!, "./b", "/src/b.ts");
        AssertResolvesTo(program, bFile!, "./c", "/src/c.ts");

        // 3. Binding runs and produces symbol tables.
        Assert.False(aFile!.IsBound());
        program.BindSourceFiles();
        Assert.True(aFile.IsBound());
        Assert.True(bFile!.IsBound());
        Assert.True(cFile!.IsBound());

        // Each module file must have received a bound symbol table (module symbol or locals).
        foreach (var file in sourceFiles)
        {
            Assert.True(file.Symbol is not null || file.Locals is not null, $"expected bound symbols for {file.FileName()}");
        }
    }

    [Fact]
    public void SingleFileNoImports_LoadsAndBinds()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/only.ts"] = "export const x = 42;\n",
        };

        var program = BuildProgram(files, ["/src/only.ts"], Options());

        var sourceFiles = program.SourceFiles();
        Assert.Single(sourceFiles);
        Assert.Equal("/src/only.ts", sourceFiles[0].FileName());

        program.BindSourceFiles();
        Assert.True(sourceFiles[0].IsBound());
    }

    private static void AssertResolvesTo(Program program, Ts.Ast.SourceFile file, string moduleName, string expectedResolvedFileName)
    {
        var resolutions = program.GetResolvedModules()[file.Path()];
        Assert.NotEmpty(resolutions);
        var resolved = resolutions
            .Where(kv => kv.Key.Name == moduleName)
            .Select(kv => kv.Value)
            .FirstOrDefault();
        Assert.NotNull(resolved);
        Assert.True(resolved!.IsResolved(), $"expected '{moduleName}' to resolve");
        Assert.Equal(expectedResolvedFileName, resolved.ResolvedFileName);
    }
}
