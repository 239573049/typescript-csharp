// Smoke tests for the getCompletionData port (Completions.GetData.cs): pin the aggregation
// arms (property access, globals, object literal, import clause, JSDoc tag names) end-to-end
// through checker services. The authoritative gate is the fourslash completions corpus (phase 8).

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Ts.LanguageService.Tests.Ls;

public sealed class CompletionDataTests
{
    private const string CurrentDirectory = "/";
    private const string FileName = "/index.ts";

    [Fact]
    public void PropertyAccessListsMembers()
    {
        var source = "interface Shape { area: number; perimeter: number; }\ndeclare const s: Shape;\ns.;\n";
        var data = GetData(source, source.IndexOf("s.;", StringComparison.Ordinal) + 2);
        var dataData = Assert.IsType<global::Ts.Ls.CompletionDataData>(data);
        var names = dataData.symbols!.Select(s => s.Name).ToArray();
        Assert.Contains("area", names);
        Assert.Contains("perimeter", names);
        Assert.True(dataData.isRightOfDotOrQuestionDot);
    }

    [Fact]
    public void GlobalCompletionListsLocalsAndGlobals()
    {
        var source = "const answer = 42;\nfunction compute(): number { return answer; }\n";
        var data = GetData(source, source.Length);
        var dataData = Assert.IsType<global::Ts.Ls.CompletionDataData>(data);
        var names = dataData.symbols!.Select(s => s.Name).ToArray();
        Assert.Contains("answer", names);
        Assert.Contains("compute", names);
    }

    [Fact]
    public void ObjectLiteralListsContextualProperties()
    {
        var source = "interface Options { depth: number; label: string; }\ndeclare function run(o: Options): void;\nrun({  });\n";
        var data = GetData(source, source.IndexOf("({  })", StringComparison.Ordinal) + 3);
        var dataData = Assert.IsType<global::Ts.Ls.CompletionDataData>(data);
        var names = dataData.symbols!.Select(s => s.Name).ToArray();
        Assert.Contains("depth", names);
        Assert.Contains("label", names);
        Assert.Equal(global::Ts.Ls.CompletionKind.ObjectPropertyDeclaration, dataData.completionKind);
    }

    [Fact]
    public void JsDocAtSignReturnsTagNameData()
    {
        var source = "/** @ */\nfunction f() {}\n";
        var data = GetData(source, source.IndexOf('@') + 1);
        Assert.IsType<global::Ts.Ls.CompletionDataJSDocTagName>(data);
    }

    [Fact]
    public void PlainCommentReturnsNull()
    {
        var source = "// hello world\nconst x = 1;\n";
        var data = GetData(source, source.IndexOf("world", StringComparison.Ordinal));
        Assert.Null(data);
    }

    private static object? GetData(string source, int position)
    {
        var program = BuildProgram(new Dictionary<string, string> { [FileName] = source });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);
        var file = program.GetSourceFile(FileName)!;
        var (checker, done) = program.GetTypeCheckerForFile(TestContext.Current.CancellationToken, file);
        try
        {
            return service.GetCompletionData(
                checker,
                file,
                position,
                global::Ts.Ls.UserPreferences.NewDefaultUserPreferences(),
                false /*forItemResolve*/);
        }
        finally
        {
            done();
        }
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
