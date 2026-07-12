// Tests for the tsconfig.json loading slice (TsConfigParsing.Load.cs / TsConfigParsing.Worker.cs):
// the JSON-source-file → object walk, ParseJsonConfigFileContent orchestration (compilerOptions +
// files/include/exclude + references), single-level extends resolution + merge, ${configDir}
// substitution, and GetParsedCommandLineOfConfigFile reading from an in-memory VFS.
//
// The byte-exact baseline套件 (tsgo tsconfigparsing_test.go) is deferred to the phase-4 batch
// runner (needs the TypeScript submodule + diagnosticwriter). These are self-contained assertions
// on the returned ParsedCommandLine.

using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.TsOptions;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.TsOptions.Tests;

public class TsConfigParsingTests
{
    private sealed class TestConfigHost : ParseConfigHost
    {
        private readonly IFileSystem fs;
        private readonly string cwd;

        public TestConfigHost(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public IFileSystem FS() => fs;
        public string GetCurrentDirectory() => cwd;
    }

    private static TestConfigHost Host(string cwd, Dictionary<string, string> files, bool caseSensitive = true)
        => new(VfsTestUtil.FromMap(files, caseSensitive), cwd);

    // Parse a tsconfig.json living at /src via GetParsedCommandLineOfConfigFile.
    private static (ParsedCommandLine? cmd, Diagnostic[]? errors) LoadConfig(Dictionary<string, string> files, string configFileName = "/src/tsconfig.json", string cwd = "/src")
    {
        var host = Host(cwd, files);
        return TsConfigParsing.GetParsedCommandLineOfConfigFile(configFileName, null, null, host, null);
    }

    [Fact]
    public void ParseConfigFileTextToJson_ObjectWalk_ReadsCompilerOptions()
    {
        var text = "{ \"compilerOptions\": { \"strict\": true, \"target\": \"es2020\", \"module\": \"esnext\" }, \"include\": [\"src\"] }";
        var (config, errors) = TsConfigParsing.ParseConfigFileTextToJson("/x/tsconfig.json", new Ts.TsPath.Path("/x/tsconfig.json"), text);

        Assert.True(errors == null || errors.Length == 0);
        var map = Assert.IsType<OrderedMap<string, object?>>(config);
        Assert.True(map.Has("compilerOptions"));
        Assert.True(map.Has("include"));
        var co = Assert.IsType<OrderedMap<string, object?>>(map.GetOrZero("compilerOptions"));
        Assert.Equal(true, co.GetOrZero("strict"));
        Assert.Equal("es2020", co.GetOrZero("target"));
    }

    [Fact]
    public void ParseConfigFileTextToJson_NonObjectRoot_ReportsError()
    {
        var (config, errors) = TsConfigParsing.ParseConfigFileTextToJson("/x/tsconfig.json", new Ts.TsPath.Path("/x/tsconfig.json"), "[1, 2, 3]");
        Assert.NotNull(config);
        Assert.NotNull(errors);
        Assert.NotEmpty(errors!);
    }

    [Fact]
    public void CompilerOptions_AreParsed_AndFilePathsMadeAbsolute()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"strict\": true, \"outDir\": \"dist\", \"target\": \"es2020\" }, \"files\": [\"a.ts\"] }",
            ["/src/a.ts"] = "export const a = 1;",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        Assert.NotNull(cmd);
        var opts = cmd!.CompilerOptions()!;
        Assert.Equal(Tristate.True, opts.Strict);
        // outDir is a file-path option → normalized to an absolute path relative to the config dir.
        Assert.Equal("/src/dist", opts.OutDir);
        Assert.Equal("/src/tsconfig.json", opts.ConfigFilePath);
    }

    [Fact]
    public void Files_ProduceLiteralFileNames_Absolute()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"files\": [\"a.ts\", \"sub/b.ts\"] }",
            ["/src/a.ts"] = "",
            ["/src/sub/b.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var fileNames = cmd!.FileNames()!;
        Assert.Contains("/src/a.ts", fileNames);
        Assert.Contains("/src/sub/b.ts", fileNames);
        Assert.Equal(2, fileNames.Length);
        // LiteralFileNames == the "files" entries (literalFileNamesLen prefix).
        Assert.Equal(2, cmd.LiteralFileNames()!.Length);
    }

    [Fact]
    public void Include_ExpandsWildcards_AndExcludeRemoves()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"include\": [\"**/*\"], \"exclude\": [\"skip\"] }",
            ["/src/a.ts"] = "",
            ["/src/b.tsx"] = "",
            ["/src/skip/c.ts"] = "",
            ["/src/notes.txt"] = "not a source file",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var fileNames = cmd!.FileNames()!;
        Assert.Contains("/src/a.ts", fileNames);
        Assert.Contains("/src/b.tsx", fileNames);
        Assert.DoesNotContain("/src/skip/c.ts", fileNames); // excluded
        Assert.DoesNotContain("/src/notes.txt", fileNames); // unsupported extension
    }

    [Fact]
    public void NoInclude_NoFiles_DefaultsToRecursiveIncludeSpec()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"strict\": true } }",
            ["/src/a.ts"] = "",
            ["/src/nested/b.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var fileNames = cmd!.FileNames()!;
        Assert.Contains("/src/a.ts", fileNames);
        Assert.Contains("/src/nested/b.ts", fileNames);
    }

    [Fact]
    public void EmptyFilesArray_ReportsFilesListEmpty()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"files\": [] }",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.NotNull(cmd);
        // Parse diagnostics land on cmd.Errors (the returned `errors` tuple carries only
        // unrecoverable read errors). "The 'files' list in config file ... is empty."
        Assert.Contains(cmd!.Errors!, d => d.Code() == Ts.Diagnostics.Diagnostics.The_files_list_in_config_file_0_is_empty.Code);
    }

    [Fact]
    public void Extends_RelativeSingleLevel_MergesCompilerOptions()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/base.json"] = "{ \"compilerOptions\": { \"strict\": true, \"target\": \"es2019\" } }",
            ["/src/tsconfig.json"] = "{ \"extends\": \"./base.json\", \"compilerOptions\": { \"module\": \"esnext\" }, \"files\": [\"a.ts\"] }",
            ["/src/a.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var opts = cmd!.CompilerOptions()!;
        // Inherited from base:
        Assert.Equal(Tristate.True, opts.Strict);
        // Own tsconfig has module → must be present; base's target inherited.
        Assert.Equal(ModuleKind.ESNext, opts.Module);
        Assert.Equal(ScriptTarget.ES2019, opts.Target);
    }

    [Fact]
    public void Extends_OwnValueOverridesBase()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/base.json"] = "{ \"compilerOptions\": { \"target\": \"es2019\", \"strict\": true } }",
            ["/src/tsconfig.json"] = "{ \"extends\": \"./base.json\", \"compilerOptions\": { \"target\": \"es2022\" }, \"files\": [\"a.ts\"] }",
            ["/src/a.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var opts = cmd!.CompilerOptions()!;
        // Own tsconfig's target wins over the extended base.
        Assert.Equal(ScriptTarget.ES2022, opts.Target);
        Assert.Equal(Tristate.True, opts.Strict);
    }

    [Fact]
    public void Extends_MissingFile_NoJsonExtension_ReportsFileNotFound()
    {
        // A relative extends without a .json suffix: getExtendsConfigPath appends .json, and when
        // that too is absent reports File_0_not_found directly (Go tsconfigparsing.go:562-567).
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"extends\": \"./does-not-exist\", \"files\": [\"a.ts\"] }",
            ["/src/a.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.NotNull(cmd);
        // The extends resolution error lands on cmd.Errors (not the read-error tuple).
        Assert.Contains(cmd!.Errors!, d => d.Code() == Ts.Diagnostics.Diagnostics.File_0_not_found.Code);
    }

    [Fact]
    public void Extends_MissingJsonFile_ReportsCannotReadFile()
    {
        // A relative extends ending in .json that is absent: getExtendsConfigPath returns the path
        // (no early error), and the subsequent read reports Cannot_read_file (Go readJsonConfigFile).
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"extends\": \"./does-not-exist.json\", \"files\": [\"a.ts\"] }",
            ["/src/a.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.NotNull(cmd);
        Assert.Contains(cmd!.Errors!, d => d.Code() == Ts.Diagnostics.Diagnostics.Cannot_read_file_0.Code);
    }

    [Fact]
    public void References_ParsedAndNormalizedToAbsolute()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"files\": [\"a.ts\"], \"references\": [ { \"path\": \"../other\" } ] }",
            ["/src/a.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var refs = cmd!.ProjectReferences()!;
        Assert.Single(refs);
        Assert.Equal("/other", refs[0].Path);
        Assert.Equal("../other", refs[0].OriginalPath);
    }

    [Fact]
    public void GetOutputFileNames_EmitsJsForEachInput()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"outDir\": \"out\" }, \"files\": [\"a.ts\", \"b.ts\"] }",
            ["/src/a.ts"] = "",
            ["/src/b.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var outputs = cmd!.GetOutputFileNames().ToList();
        Assert.Contains("/src/out/a.js", outputs);
        Assert.Contains("/src/out/b.js", outputs);
    }

    [Fact]
    public void CommonSourceDirectory_IsComputedFromInputs()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"files\": [\"a.ts\", \"sub/b.ts\"] }",
            ["/src/a.ts"] = "",
            ["/src/sub/b.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        var csd = cmd!.CommonSourceDirectory();
        // Go's GetCommonSourceDirectory returns a trailing-separator directory.
        Assert.Equal("/src/", csd);
    }

    [Fact]
    public void ConfigDirTemplate_SubstitutedInFilePaths()
    {
        var files = new Dictionary<string, string>
        {
            ["/src/tsconfig.json"] = "{ \"compilerOptions\": { \"outDir\": \"${configDir}/dist\" }, \"files\": [\"a.ts\"] }",
            ["/src/a.ts"] = "",
        };
        var (cmd, errors) = LoadConfig(files);

        Assert.True(errors == null || errors.Length == 0);
        Assert.Equal("/src/dist", cmd!.CompilerOptions()!.OutDir);
    }
}
