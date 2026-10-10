// Ported from tsgo internal/tsoptions/tsconfigparsing_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTEs:
//  * Only the Go test functions that have no counterpart in the existing self-authored
//    TsConfigParsingTests.cs are ported here (Go file:845-1007, 1210-1381, 1442-1568). The
//    baseline-diff suites (TestParseConfigFileTextToJson / TestParseJsonConfigFileContent /
//    TestParseJsonSourceFileConfigFileContent / TestParseTypeAcquisition) are deferred to the
//    phase-4 batch runner because they call repo.SkipIfNoTypeScriptSubmodule + baseline.Run.
//  * tsoptionstest.{NewVFSParseConfigHost,GetParsedCommandLine} (tsoptionstest/*.go) are
//    reproduced locally as Host()/GetParsedCommandLine() helpers.
//  * The Go `memoCache` (tsconfigparsing_test.go:1424-1440) is ported as MemoCache below; the
//    `var _ tsoptions.ExtendedConfigCache = (*memoCache)(nil)` assertion is dropped (05 §1.3).
//  * Go scan/gotest error assertions → xUnit: assert.Assert(t, cond, msg) → Assert.True(cond, "$...")
//    and assert.NilError/errors-len checks map onto the returned arrays (05-conventions.md §16.3).
//  * `if !bundled.Embedded` guards are absent in Go here; none are needed since all fixtures are
//    in-memory VFS maps.

using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.TsOptions;
using Ts.TsPath;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

using TsOptionsApi = Ts.TsOptions;
using Path = Ts.TsPath.Path;
using TsPathApi = Ts.TsPath.TsPath;

namespace Ts.TsOptions.Tests;

public class TsConfigParsingExtendedTests
{
    // tsoptionstest/vfsparseconfighost.go — VfsParseConfigHost.
    private sealed class Host : ParseConfigHost
    {
        private readonly IFileSystem fs;
        private readonly string cwd;

        public Host(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public IFileSystem FS() => fs;

        public string GetCurrentDirectory() => cwd;
    }

    // tsoptionstest/vfsparseconfighost.go:23-29 — NewVFSParseConfigHost.
    private static Host NewVfsParseConfigHost(Dictionary<string, string> files, string currentDirectory, bool useCaseSensitiveFileNames)
        => new(VfsTestUtil.FromMap(files, useCaseSensitiveFileNames), currentDirectory);

    // tsoptionstest/parsedcommandline.go:10-15 — GetParsedCommandLine.
    private static ParsedCommandLine GetParsedCommandLine(string jsonText, Dictionary<string, string> files, string currentDirectory, bool useCaseSensitiveFileNames)
    {
        Host host = NewVfsParseConfigHost(files, currentDirectory, useCaseSensitiveFileNames);
        string configFileName = TsPathApi.CombinePaths(currentDirectory, "tsconfig.json");
        TsConfigSourceFile tsconfigSourceFile = TsConfigParsing.NewTsconfigSourceFileFromFilePath(
            configFileName, TsPathApi.ToPath(configFileName, currentDirectory, useCaseSensitiveFileNames), jsonText);
        return TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            tsconfigSourceFile, host, currentDirectory, null, null, configFileName, null, null, null);
    }

    // tsconfigparsing_test.go:29-34 — testConfig.
    private sealed class TestConfig
    {
        public string JsonText = "";
        public string ConfigFileName = "";
        public string BasePath = "";
        public Dictionary<string, string> AllFileList = new();
    }

    // tsconfigparsing_test.go:1009-1030 — getParsedWithJsonSourceFileApi.
    private static ParsedCommandLine GetParsedWithJsonSourceFileApi(TestConfig config, ParseConfigHost host, string basePath)
    {
        string configFileName = TsPathApi.GetNormalizedAbsolutePath(config.ConfigFileName, basePath);
        Path path = TsPathApi.ToPath(config.ConfigFileName, basePath, host.FS().UseCaseSensitiveFileNames());
        TsConfigSourceFile tsConfigSourceFile = TsConfigParsing.NewTsconfigSourceFileFromFilePath(configFileName, path, config.JsonText);
        return TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            tsConfigSourceFile, host, host.GetCurrentDirectory(), null, null, configFileName, null, null, null);
    }

    // tsconfigparsing_test.go:845-888 — TestParseJsonSourceFileConfigFileContentReportsInvalidExtendedConfig.
    [Fact]
    public void ParseJsonSourceFileConfigFileContentReportsInvalidExtendedConfig()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = """
{
  "extends": "./bad.json"
}
""",
            // The parser recovers from this as object-like JSON, producing expected-token errors for ':', ',', ',', and '}'.
            ["/project/bad.json"] = "{ this is not json",
            ["/project/main.ts"] = "export const x = 1;",
        };
        Host host = NewVfsParseConfigHost(files, "/project", true /*useCaseSensitiveFileNames*/);
        const string configFileName = "/project/tsconfig.json";
        TsConfigSourceFile configFile = TsConfigParsing.NewTsconfigSourceFileFromFilePath(
            configFileName,
            TsPathApi.ToPath(configFileName, host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames()),
            files[configFileName]);

        ParsedCommandLine parsed = TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            configFile,
            host,
            host.GetCurrentDirectory(),
            null,
            null,
            configFileName,
            null,
            null,
            null);

        Diagnostic[] parseErrors = CoreUtil.Filter(parsed.Errors ?? [], d => d.Code() == global::Ts.Diagnostics.Diagnostics.X_0_expected.Code) ?? [];
        string[] expectedParseErrorMessages = [":", ",", ",", "}"];
        int[] expectedParseErrorPositions = [7, 10, 14, 18];
        Assert.Equal(expectedParseErrorMessages.Length, parseErrors.Length);
        Assert.Equal(expectedParseErrorMessages, CoreUtil.Map(parseErrors, d => d.MessageArgs()![0] as string));
        Assert.Equal(expectedParseErrorPositions, CoreUtil.Map(parseErrors, d => d.Pos()));
        foreach (Diagnostic diagnostic in parseErrors)
        {
            Assert.Equal("/project/bad.json", diagnostic.File()!.FileName());
        }
    }

    // tsconfigparsing_test.go:891-922 — TestParseJsonSourceFileConfigFileContentWithEmptyExtendedConfig.
    [Fact]
    public void ParseJsonSourceFileConfigFileContentWithEmptyExtendedConfig()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = """
{
  "extends": "./base.json"
}
""",
            ["/project/base.json"] = "",
            ["/project/main.ts"] = "export const x = 1;",
        };
        Host host = NewVfsParseConfigHost(files, "/project", true /*useCaseSensitiveFileNames*/);
        const string configFileName = "/project/tsconfig.json";
        TsConfigSourceFile configFile = TsConfigParsing.NewTsconfigSourceFileFromFilePath(
            configFileName,
            TsPathApi.ToPath(configFileName, host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames()),
            files[configFileName]);

        ParsedCommandLine parsed = TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            configFile,
            host,
            host.GetCurrentDirectory(),
            null,
            null,
            configFileName,
            null,
            null,
            null);

        Assert.NotNull(parsed);
        Assert.Equal(new[] { "/project/main.ts" }, parsed.FileNames());
    }

    // tsconfigparsing_test.go:924-947 — TestParseJsonSourceFileConfigFileContentDoesNotDuplicateUnquotedKeyDiagnostics.
    [Fact]
    public void ParseJsonSourceFileConfigFileContentDoesNotDuplicateUnquotedKeyDiagnostics()
    {
        ParsedCommandLine parsed = GetParsedCommandLine("""
{
  compilerOptions: {
    strict: true
  }
}
""", new Dictionary<string, string> { ["/main.ts"] = "export const x = 1;" }, "/", true /*useCaseSensitiveFileNames*/);

        Diagnostic[] diags = parsed.GetConfigFileParsingDiagnostics() ?? [];
        Assert.Equal(2, diags.Length);
        var expectedLocations = new[]
        {
            (Line: 1, Character: 2),
            (Line: 2, Character: 4),
        };
        for (int index = 0; index < diags.Length; index++)
        {
            Assert.Equal(global::Ts.Diagnostics.Diagnostics.String_literal_with_double_quotes_expected.Code, diags[index].Code());
            (int line, int character) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(diags[index].File()!, diags[index].Pos());
            Assert.Equal(expectedLocations[index].Line, line);
            Assert.Equal(expectedLocations[index].Character, character);
        }
    }

    // tsconfigparsing_test.go:949-976 — TestParseJsonSourceFileConfigFileContentReportsQuestionTokenDiagnostics.
    [Fact]
    public void ParseJsonSourceFileConfigFileContentReportsQuestionTokenDiagnostics()
    {
        ParsedCommandLine parsed = GetParsedCommandLine("""
{
  compilerOptions?: {
    strict?: true
  }
}
""", new Dictionary<string, string> { ["/main.ts"] = "export const x = 1;" }, "/", true /*useCaseSensitiveFileNames*/);

        var questionTokenDiagnostics = new List<Diagnostic>();
        foreach (Diagnostic diagnostic in parsed.GetConfigFileParsingDiagnostics() ?? [])
        {
            if (diagnostic.Code() == global::Ts.Diagnostics.Diagnostics.The_0_modifier_can_only_be_used_in_TypeScript_files.Code)
            {
                questionTokenDiagnostics.Add(diagnostic);
            }
        }
        Assert.Equal(2, questionTokenDiagnostics.Count);
        var expectedLocations = new[]
        {
            (Line: 1, Character: 17),
            (Line: 2, Character: 10),
        };
        for (int index = 0; index < questionTokenDiagnostics.Count; index++)
        {
            (int line, int character) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(
                questionTokenDiagnostics[index].File()!, questionTokenDiagnostics[index].Pos());
            Assert.Equal(expectedLocations[index].Line, line);
            Assert.Equal(expectedLocations[index].Character, character);
        }
    }

    // tsconfigparsing_test.go:978-1007 — TestParseNullEnumCompilerOptions.
    [Fact]
    public void ParseNullEnumCompilerOptions()
    {
        var config = new TestConfig
        {
            JsonText = """
{
	"compilerOptions": {
		"target": null,
		"module": null
	}
}
""",
            ConfigFileName = "tsconfig.json",
            BasePath = "/",
            AllFileList = new Dictionary<string, string> { ["/app.ts"] = "" },
        };

        // Go iterates a map[string]func(...) here, so subtest order is random; C# runs the two
        // named sub-scenarios in a fixed order (map-iter: 序无关 — the two parses are independent).
        Dictionary<string, System.Func<TestConfig, ParseConfigHost, string, ParsedCommandLine>> getParsedByName = new()
        {
            ["json api"] = GetParsedWithJsonApi,
            ["jsonSourceFile api"] = GetParsedWithJsonSourceFileApi,
        };

        var allFileLists = new Dictionary<string, string>(config.AllFileList)
        {
            ["/tsconfig.json"] = config.JsonText,
        };
        Host host = NewVfsParseConfigHost(allFileLists, config.BasePath, true /*useCaseSensitiveFileNames*/);

        foreach ((string name, System.Func<TestConfig, ParseConfigHost, string, ParsedCommandLine> getParsed) in getParsedByName)
        {
            ParsedCommandLine parsedConfigFileContent = getParsed(config, host, config.BasePath);
            Assert.True((parsedConfigFileContent.Errors?.Length ?? 0) == 0,
                $"{name}: {string.Join(" | ", (parsedConfigFileContent.Errors ?? []).Select(e => e.Localize(global::Ts.Locale.Locale.Default)))}");
        }
    }

    // tsconfigparsing_test.go:807-832 — getParsedWithJsonApi.
    private static ParsedCommandLine GetParsedWithJsonApi(TestConfig config, ParseConfigHost host, string basePath)
    {
        string configFileName = TsPathApi.GetNormalizedAbsolutePath(config.ConfigFileName, basePath);
        Path path = TsPathApi.ToPath(config.ConfigFileName, basePath, host.FS().UseCaseSensitiveFileNames());
        (object? parsed, Diagnostic[]? _) = TsConfigParsing.ParseConfigFileTextToJson(configFileName, path, config.JsonText);
        return TsConfigParsing.ParseJsonConfigFileContent(
            parsed,
            host,
            basePath,
            null,
            configFileName,
            null,
            null,
            null);
    }

    // tsconfigparsing_test.go:1210-1381 — TestParseSrcCompiler.
    [Fact]
    public void ParseSrcCompiler()
    {
        global::Ts.TestUtil.RepoPaths.SkipIfNoTypeScriptSubmodule();

        string compilerDir = TsPathApi.NormalizeSlashes(System.IO.Path.Combine(
            global::Ts.TestUtil.RepoPaths.TypeScriptSubmodulePath, "src", "compiler"));
        string tsconfigFileName = TsPathApi.CombinePaths(compilerDir, "tsconfig.json");

        IFileSystem fs = global::Ts.Vfs.OsVfs.OsVfsUtil.FS();
        var host = new Host(fs, compilerDir);

        Assert.True(fs.TryReadFile(tsconfigFileName, out string jsonText));
        Path tsconfigPath = TsPathApi.ToPath(tsconfigFileName, compilerDir, fs.UseCaseSensitiveFileNames());
        SourceFile parsed = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions { FileName = tsconfigFileName, Path = tsconfigPath },
            jsonText,
            ScriptKind.Json);

        Assert.True(parsed.Diagnostics() is null || parsed.Diagnostics()!.Length == 0, string.Join("\n", (parsed.Diagnostics() ?? []).Select(d => d.Localize(global::Ts.Locale.Locale.Default))));

        TsConfigSourceFile tsConfigSourceFile = TsConfigParsing.NewTsconfigSourceFileFromFilePath(tsconfigFileName, tsconfigPath, jsonText);

        ParsedCommandLine parseConfigFileContent = TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            tsConfigSourceFile,
            host,
            host.GetCurrentDirectory(),
            null,
            null,
            tsconfigFileName,
            null,
            null,
            null);

        Assert.True(parseConfigFileContent.Errors is null || parseConfigFileContent.Errors.Length == 0,
            string.Join("\n", (parseConfigFileContent.Errors ?? []).Select(e => e.Localize(global::Ts.Locale.Locale.Default))));

        CompilerOptions opts = parseConfigFileContent.CompilerOptions()!;
        string submodule = global::Ts.TestUtil.RepoPaths.TypeScriptSubmodulePath;
        var expected = new CompilerOptions
        {
            Lib = ["lib.es2020.d.ts"],
            Module = ModuleKind.NodeNext,
            ModuleResolution = ModuleResolutionKind.NodeNext,
            NewLine = NewLineKind.LF,
            OutDir = TsPathApi.NormalizeSlashes(System.IO.Path.Combine(submodule, "built", "local")),
            Target = ScriptTarget.ES2020,
            Types = ["node"],
            ConfigFilePath = tsconfigFileName,
            Declaration = Tristate.True,
            DeclarationMap = Tristate.True,
            EmitDeclarationOnly = Tristate.True,
            AlwaysStrict = Tristate.True,
            Composite = Tristate.True,
            IsolatedDeclarations = Tristate.True,
            NoImplicitOverride = Tristate.True,
            PreserveConstEnums = Tristate.True,
            RootDir = TsPathApi.NormalizeSlashes(System.IO.Path.Combine(submodule, "src")),
            SkipLibCheck = Tristate.True,
            Strict = Tristate.True,
            StrictBindCallApply = Tristate.False,
            SourceMap = Tristate.True,
            UseUnknownInCatchVariables = Tristate.False,
            Pretty = Tristate.True,
        };

        string[] fileNames = parseConfigFileContent.ParsedConfig.FileNames ?? [];
        var relativePaths = new List<string>(fileNames.Length);
        foreach (string fileName in fileNames)
        {
            if (fileName.Contains(".generated."))
            {
                continue;
            }

            relativePaths.Add(TsPathApi.ConvertToRelativePath(fileName, new ComparePathsOptions
            {
                CurrentDirectory = compilerDir,
                UseCaseSensitiveFileNames = fs.UseCaseSensitiveFileNames(),
            }));
        }

        // The Go test also DeepEquals the parsed CompilerOptions against a literal; the option
        // struct has no value equality in C# (05-conventions.md §16.3 "带 cmp.Options 的逐案手写"),
        // so each load-bearing field is compared individually.
        AssertOptions(expected, opts);
        Assert.Equal(ParseSrcCompilerRelativePaths, relativePaths);
    }

    private static void AssertOptions(CompilerOptions expected, CompilerOptions actual)
    {
        Assert.Equal(expected.Lib, actual.Lib);
        Assert.Equal(expected.Module, actual.Module);
        Assert.Equal(expected.ModuleResolution, actual.ModuleResolution);
        Assert.Equal(expected.NewLine, actual.NewLine);
        Assert.Equal(expected.OutDir, actual.OutDir);
        Assert.Equal(expected.Target, actual.Target);
        Assert.Equal(expected.Types, actual.Types);
        Assert.Equal(expected.ConfigFilePath, actual.ConfigFilePath);
        Assert.Equal(expected.RootDir, actual.RootDir);
        Assert.Equal(expected.Declaration, actual.Declaration);
        Assert.Equal(expected.DeclarationMap, actual.DeclarationMap);
        Assert.Equal(expected.EmitDeclarationOnly, actual.EmitDeclarationOnly);
        Assert.Equal(expected.AlwaysStrict, actual.AlwaysStrict);
        Assert.Equal(expected.Composite, actual.Composite);
        Assert.Equal(expected.IsolatedDeclarations, actual.IsolatedDeclarations);
        Assert.Equal(expected.NoImplicitOverride, actual.NoImplicitOverride);
        Assert.Equal(expected.PreserveConstEnums, actual.PreserveConstEnums);
        Assert.Equal(expected.SkipLibCheck, actual.SkipLibCheck);
        Assert.Equal(expected.Strict, actual.Strict);
        Assert.Equal(expected.StrictBindCallApply, actual.StrictBindCallApply);
        Assert.Equal(expected.SourceMap, actual.SourceMap);
        Assert.Equal(expected.UseUnknownInCatchVariables, actual.UseUnknownInCatchVariables);
        Assert.Equal(expected.Pretty, actual.Pretty);
    }

    // tsconfigparsing_test.go:1302-1380 — the expected relative file name list.
    private static readonly string[] ParseSrcCompilerRelativePaths =
    [
        "binder.ts",
        "builder.ts",
        "builderPublic.ts",
        "builderState.ts",
        "builderStatePublic.ts",
        "checker.ts",
        "commandLineParser.ts",
        "core.ts",
        "corePublic.ts",
        "debug.ts",
        "emitter.ts",
        "executeCommandLine.ts",
        "expressionToTypeNode.ts",
        "moduleNameResolver.ts",
        "moduleSpecifiers.ts",
        "parser.ts",
        "path.ts",
        "performance.ts",
        "performanceCore.ts",
        "program.ts",
        "programDiagnostics.ts",
        "resolutionCache.ts",
        "scanner.ts",
        "semver.ts",
        "sourcemap.ts",
        "symbolWalker.ts",
        "sys.ts",
        "tracing.ts",
        "transformer.ts",
        "tsbuild.ts",
        "tsbuildPublic.ts",
        "types.ts",
        "utilities.ts",
        "utilitiesPublic.ts",
        "visitorPublic.ts",
        "watch.ts",
        "watchPublic.ts",
        "watchUtilities.ts",
        "_namespaces/ts.moduleSpecifiers.ts",
        "_namespaces/ts.performance.ts",
        "_namespaces/ts.ts",
        "factory/baseNodeFactory.ts",
        "factory/emitHelpers.ts",
        "factory/emitNode.ts",
        "factory/nodeChildren.ts",
        "factory/nodeConverters.ts",
        "factory/nodeFactory.ts",
        "factory/nodeTests.ts",
        "factory/parenthesizerRules.ts",
        "factory/utilities.ts",
        "factory/utilitiesPublic.ts",
        "transformers/classFields.ts",
        "transformers/classThis.ts",
        "transformers/declarations.ts",
        "transformers/destructuring.ts",
        "transformers/es2015.ts",
        "transformers/es2016.ts",
        "transformers/es2017.ts",
        "transformers/es2018.ts",
        "transformers/es2019.ts",
        "transformers/es2020.ts",
        "transformers/es2021.ts",
        "transformers/esDecorators.ts",
        "transformers/esnext.ts",
        "transformers/generators.ts",
        "transformers/jsx.ts",
        "transformers/legacyDecorators.ts",
        "transformers/namedEvaluation.ts",
        "transformers/taggedTemplate.ts",
        "transformers/ts.ts",
        "transformers/typeSerializer.ts",
        "transformers/utilities.ts",
        "transformers/declarations/diagnostics.ts",
        "transformers/module/esnextAnd2015.ts",
        "transformers/module/impliedNodeFormatDependent.ts",
        "transformers/module/module.ts",
        "transformers/module/system.ts",
    ];

    // tsconfigparsing_test.go:1422-1440 — memoCache: a minimal memoizing ExtendedConfigCache.
    private sealed class MemoCache : ExtendedConfigCache
    {
        private readonly Dictionary<Path, ExtendedConfigCacheEntry> m = new();

        public ExtendedConfigCacheEntry GetExtendedConfig(string fileName, Path path, Path[] resolutionStack, ParseConfigHost host)
        {
            if (m.TryGetValue(path, out ExtendedConfigCacheEntry? e))
            {
                return e;
            }
            e = TsConfigParsing.ParseExtendedConfig(fileName, path, resolutionStack, host, this);
            m[path] = e;
            return e;
        }
    }

    // tsconfigparsing_test.go:1444-1533 — TestExtendedConfigErrorsAppearOnCacheHit.
    [Fact]
    public void ExtendedConfigErrorsAppearOnCacheHit_SingleConfigParsedTwice()
    {
        var files = new Dictionary<string, string>
        {
            ["/tsconfig.json"] = """
{
  "extends": "./base.json"
}
""",
            // 'excludes' instead of 'exclude' triggers diagnostic
            ["/base.json"] = """
{
  "excludes": ["**/*.ts"]
}
""",
            ["/app.ts"] = "export {}",
        };

        Host host = NewVfsParseConfigHost(files, "/", true /*useCaseSensitiveFileNames*/);

        var cache = new MemoCache();
        ParsedCommandLine first = ParseConfig("/tsconfig.json", cache, host);
        Assert.True((first.Errors?.Length ?? 0) > 0, "expected diagnostics on first parse, got 0");
        ParsedCommandLine second = ParseConfig("/tsconfig.json", cache, host);
        Assert.True((second.Errors?.Length ?? 0) > 0, "expected diagnostics on second parse (cache hit), got 0");
    }

    [Fact]
    public void ExtendedConfigErrorsAppearOnCacheHit_TwoConfigsShareSameBase()
    {
        var files = new Dictionary<string, string>
        {
            ["/base.json"] = """
{
  "excludes": ["**/*.ts"]
}
""",
            ["/projA/tsconfig.json"] = """
{
  "extends": "../base.json"
}
""",
            ["/projB/tsconfig.json"] = """
{
  "extends": "../base.json"
}
""",
            ["/projA/app.ts"] = "export {}",
            ["/projB/app.ts"] = "export {}",
        };

        Host host = NewVfsParseConfigHost(files, "/", true /*useCaseSensitiveFileNames*/);

        var cache = new MemoCache();
        ParsedCommandLine first = ParseConfig("/projA/tsconfig.json", cache, host);
        Assert.True((first.Errors?.Length ?? 0) > 0, "expected diagnostics for projA parse, got 0");
        ParsedCommandLine second = ParseConfig("/projB/tsconfig.json", cache, host);
        Assert.True((second.Errors?.Length ?? 0) > 0, "expected diagnostics for projB parse (cache hit on base), got 0");
    }

    // tsconfigparsing_test.go:1462-1480 — the local parseConfig closure.
    private static ParsedCommandLine ParseConfig(string configFileName, ExtendedConfigCache cache, ParseConfigHost host)
    {
        Path cfgPath = TsPathApi.ToPath(configFileName, host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames());
        Assert.True(host.FS().TryReadFile(configFileName, out string jsonText), $"missing {configFileName} in test fs");
        TsConfigSourceFile tsConfigSourceFile = TsConfigParsing.NewTsconfigSourceFileFromFilePath(configFileName, cfgPath, jsonText);
        return TsConfigParsing.ParseJsonSourceFileConfigFileContent(
            tsConfigSourceFile,
            host,
            host.GetCurrentDirectory(),
            null,
            null,
            configFileName,
            null,
            null,
            cache);
    }

    // tsconfigparsing_test.go:1535-1568 — TestExtendedConfigConfigDirPathsAreNotCached.
    [Fact]
    public void ExtendedConfigConfigDirPathsAreNotCached()
    {
        var files = new Dictionary<string, string>
        {
            ["/tsconfig.base.json"] = """
{
  "compilerOptions": {
    "paths": {
      "@pkg/*": ["${configDir}/src/*"]
    }
  }
}
""",
            ["/packages/a/tsconfig.json"] = """
{
  "extends": "../../tsconfig.base.json"
}
""",
            ["/packages/b/tsconfig.json"] = """
{
  "extends": "../../tsconfig.base.json"
}
""",
            ["/packages/a/index.ts"] = "export {}",
            ["/packages/b/index.ts"] = "export {}",
        };

        Host host = NewVfsParseConfigHost(files, "/", true /*useCaseSensitiveFileNames*/);
        var cache = new MemoCache();

        ParsedCommandLine ParseConfigFile(string configFileName)
        {
            (ParsedCommandLine? parsed, Diagnostic[]? errors) = TsConfigParsing.GetParsedCommandLineOfConfigFile(configFileName, null, null, host, cache);
            Assert.True(errors is null || errors.Length == 0, $"unexpected errors parsing {configFileName}");
            return parsed!;
        }

        ParseConfigFile("/packages/a/tsconfig.json");
        OrderedMap<string, string[]>? paths = ParseConfigFile("/packages/b/tsconfig.json").CompilerOptions()!.Paths;
        Assert.Equal(new[] { "/packages/b/src/*" }, paths!.GetOrZero("@pkg/*"));
    }
}
