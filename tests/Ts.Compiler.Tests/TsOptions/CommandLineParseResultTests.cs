// Ported from tsgo internal/tsoptions/commandlineparser_test.go — TestParseCommandLineVerifyNull
// (commandlineparser_test.go:170-245).
//
// PORT-NOTES:
//  * Go drives this test through the baseline fixtures
//    `_submodules/TypeScript/tests/baselines/reference/config/commandLineParsing/parseCommandLine/
//    <subtest>.js` (createSubScenario + assertParseResult, commandlineparser_test.go:463-283). The C#
//    runner has no baseline-file reader for this suite (no test under tests/ reads
//    `baselines/reference`), so the expected values are inlined here exactly as the baselines record
//    them: CompilerOptions are compared field-by-field (05-conventions.md §16.3 — no value equality
//    on the options struct) and every diagnostic is compared against the baseline's
//    `error TS####: <message>` line.
//  * `tsoptions.ParseCommandLineTestWorker` (export_test.go:15-42) is
//    `CommandLineParser.parseCommandLineWorker` reached through InternalsVisibleTo
//    (Ts.Compiler → Ts.Compiler.Tests). Go rebuilds `getParseCommandLineWorkerDiagnostics(decls)`
//    when decls is non-empty; neither the option name map nor the diagnostics matter for these
//    scenarios (only `OptionTypeMismatchDiagnostic` consumes workerDiagnostics, and every scenario
//    passes a value), so the plain worker is used.
//  * `createVerifyNullForNonNullIncluded` (commandlineparser_test.go:231-245) concatenates an extra
//    tsconfig-only "optionName" declaration (string / number) with OptionsDeclarations; the C# port
//    reproduces that exactly and pushes the result through a NameMap.
//  * t.Parallel() → deleted (xUnit collection-level parallelism, §16.1); the Go `for` loop over
//    `verifyNullSubScenarios` is unrolled into one [Fact] per scenario, in Go's order.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;
using Ts.TsOptions;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

using Diag = Ts.Diagnostics.Diagnostics;

namespace Ts.TsOptions.Tests;

public class CommandLineParseResultTests
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

    private static Host NewHost(string cwd) =>
        new(VfsTestUtil.FromMap(new Dictionary<string, string>(), true /*useCaseSensitiveFileNames*/), cwd);

    private static Host NewFilesHost(Dictionary<string, string> files, string cwd) =>
        new(VfsTestUtil.FromMap(files, true /*useCaseSensitiveFileNames*/), cwd);

    // export_test.go:15-42 — ParseCommandLineTestWorker.
    private static commandLineParser ParseCommandLineTestWorker(CommandLineOption[]? decls, string[] commandLine, IFileSystem fs, string currentDirectory)
    {
        var parser = new commandLineParser
        {
            fs = fs,
            currentDirectory = currentDirectory,
            workerDiagnostics = ParseCommandLineWorkerDiagnostics.CompilerOptionsDidYouMeanDiagnostics,
            fileNames = new List<string>(),
            options = new OrderedMap<string, object?>(),
            errors = new List<Diagnostic>(),
        };
        if (decls is { Length: > 0 })
        {
            parser.workerDiagnostics = ParseCommandLineWorkerDiagnostics.getParseCommandLineWorkerDiagnostics(decls);
        }
        parser.optionsMap = NameMaps.GetNameMapFromList(parser.OptionsDeclarations());
        parser.parseStrings(commandLine);
        return parser;
    }

    // commandlineparser_test.go:231-245 — createVerifyNullForNonNullIncluded.
    private static CommandLineOption[] WithExtraOption(string kind)
    {
        var decls = new List<CommandLineOption>(DeclsCompiler.OptionsDeclarations)
        {
            new CommandLineOption
            {
                Name = "optionName",
                Kind = kind,
                IsTSConfigOnly = true,
                Category = Diag.Backwards_Compatibility,
                Description = Diag.Enable_project_compilation,
            },
        };
        return decls.ToArray();
    }

    private static CompilerOptions Options(OrderedMap<string, object?> raw) =>
        TsConfigParsing.convertMapToOptions(raw, new compilerOptionsParser(new CompilerOptions())).CompilerOptions;

    // baseline `Errors::` section — one `error TS####: <message>` line per diagnostic.
    private static void AssertErrors(commandLineParser parsed, params string[] expected)
    {
        var written = new List<string>();
        foreach (Diagnostic diagnostic in parsed.errors)
        {
            written.Add($"{diagnostic.Category().Name()} TS{diagnostic.Code()}: {diagnostic}");
        }
        Assert.Equal(expected, written);
    }

    private static void AssertFileNames(commandLineParser parsed, params string[] expected) =>
        Assert.Equal(expected, parsed.fileNames);

    // commandlineparser_test.go:175 — `allows setting option type boolean to false`.
    [Fact]
    public void ParseCommandLineVerifyNull_AllowsSettingBooleanToFalse()
    {
        var parsed = ParseCommandLineTestWorker(null, ["--composite", "false", "0.ts"], NewHost("/").FS(), "/");

        Assert.Equal(Tristate.False, Options(parsed.options).Composite);
        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed);
    }

    // commandlineparser_test.go:177-204 — "option of type boolean".
    [Fact]
    public void ParseCommandLineVerifyNull_Boolean_AllowsSettingItToNull()
    {
        var parsed = ParseCommandLineTestWorker(null, ["--composite", "null", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed);
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Boolean_ErrorsIfNonNullValueIsPassed()
    {
        var parsed = ParseCommandLineTestWorker(null, ["--composite", "true", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6230: Option 'composite' can only be specified in 'tsconfig.json' file or set to 'false' or 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Boolean_ErrorsIfItsFollowedByAnotherOption()
    {
        var parsed = ParseCommandLineTestWorker(null, ["0.ts", "--strictNullChecks", "--composite"], NewHost("/").FS(), "/");

        Assert.Equal(Tristate.True, Options(parsed.options).StrictNullChecks);
        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6230: Option 'composite' can only be specified in 'tsconfig.json' file or set to 'false' or 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Boolean_ErrorsIfItsLastOption()
    {
        var parsed = ParseCommandLineTestWorker(null, ["0.ts", "--composite"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6230: Option 'composite' can only be specified in 'tsconfig.json' file or set to 'false' or 'null' on command line.");
    }

    // commandlineparser_test.go:183-186 — "option of type object".
    [Fact]
    public void ParseCommandLineVerifyNull_Object_AllowsSettingItToNull()
    {
        var parsed = ParseCommandLineTestWorker(null, ["--paths", "null", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed);
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Object_ErrorsIfItsFollowedByAnotherOption()
    {
        var parsed = ParseCommandLineTestWorker(null, ["0.ts", "--strictNullChecks", "--paths"], NewHost("/").FS(), "/");

        Assert.Equal(Tristate.True, Options(parsed.options).StrictNullChecks);
        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'paths' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Object_ErrorsIfItsLastOption()
    {
        var parsed = ParseCommandLineTestWorker(null, ["0.ts", "--paths"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'paths' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    // commandlineparser_test.go:187-191 — "option of type list".
    [Fact]
    public void ParseCommandLineVerifyNull_List_AllowsSettingItToNull()
    {
        var parsed = ParseCommandLineTestWorker(null, ["--rootDirs", "null", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed);
    }

    [Fact]
    public void ParseCommandLineVerifyNull_List_ErrorsIfNonNullValueIsPassed()
    {
        var parsed = ParseCommandLineTestWorker(null, ["--rootDirs", "abc,xyz", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'rootDirs' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_List_ErrorsIfItsFollowedByAnotherOption()
    {
        var parsed = ParseCommandLineTestWorker(null, ["0.ts", "--strictNullChecks", "--rootDirs"], NewHost("/").FS(), "/");

        Assert.Equal(Tristate.True, Options(parsed.options).StrictNullChecks);
        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'rootDirs' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_List_ErrorsIfItsLastOption()
    {
        var parsed = ParseCommandLineTestWorker(null, ["0.ts", "--rootDirs"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'rootDirs' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    // commandlineparser_test.go:192 — createVerifyNullForNonNullIncluded("option of type string", …, "hello").
    [Fact]
    public void ParseCommandLineVerifyNull_String_AllowsSettingItToNull()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("string"), ["--optionName", "null", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed);
    }

    [Fact]
    public void ParseCommandLineVerifyNull_String_ErrorsIfNonNullValueIsPassed()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("string"), ["--optionName", "hello", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'optionName' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_String_ErrorsIfItsFollowedByAnotherOption()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("string"), ["0.ts", "--strictNullChecks", "--optionName"], NewHost("/").FS(), "/");

        Assert.Equal(Tristate.True, Options(parsed.options).StrictNullChecks);
        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'optionName' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_String_ErrorsIfItsLastOption()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("string"), ["0.ts", "--optionName"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'optionName' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    // commandlineparser_test.go:193 — createVerifyNullForNonNullIncluded("option of type number", …, "10").
    [Fact]
    public void ParseCommandLineVerifyNull_Number_AllowsSettingItToNull()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("number"), ["--optionName", "null", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed);
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Number_ErrorsIfNonNullValueIsPassed()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("number"), ["--optionName", "10", "0.ts"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'optionName' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Number_ErrorsIfItsFollowedByAnotherOption()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("number"), ["0.ts", "--strictNullChecks", "--optionName"], NewHost("/").FS(), "/");

        Assert.Equal(Tristate.True, Options(parsed.options).StrictNullChecks);
        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'optionName' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    [Fact]
    public void ParseCommandLineVerifyNull_Number_ErrorsIfItsLastOption()
    {
        var parsed = ParseCommandLineTestWorker(WithExtraOption("number"), ["0.ts", "--optionName"], NewHost("/").FS(), "/");

        AssertFileNames(parsed, "0.ts");
        AssertErrors(parsed,
            "error TS6064: Option 'optionName' can only be specified in 'tsconfig.json' file or set to 'null' on command line.");
    }

    // commandlineparser_test.go:125-168 — TestCustomConditionsNullOverride.
    [Fact]
    public void CustomConditionsNullOverride()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = """
{
  "compilerOptions": {
    "customConditions": ["condition1", "condition2"]
  }
}
""",
            ["/project/index.ts"] = "console.log(\"Hello, World!\");",
        };

        Host host = NewFilesHost(files, "/project");

        // Parse command line with --customConditions null
        ParsedCommandLine cmdLine = CommandLineParser.ParseCommandLine(["--project", "/project", "--customConditions", "null"], host);

        // Check that the raw options contain null for customConditions
        var rawMap = (OrderedMap<string, object?>)cmdLine.Raw!;
        Assert.True(rawMap.TryGet("customConditions", out object? customConditionsRaw), "customConditions should exist in raw options");
        Assert.Null(customConditionsRaw);

        // Now parse the config file with the command line options.
        // Wrap command line options in "compilerOptions" key to match tsconfig.json structure.
        var wrappedRaw = new OrderedMap<string, object?>();
        wrappedRaw.Set("compilerOptions", cmdLine.Raw);
        (ParsedCommandLine? parsedConfig, Diagnostic[]? errors) = TsConfigParsing.GetParsedCommandLineOfConfigFile(
            "/project/tsconfig.json",
            cmdLine.CompilerOptions(),
            wrappedRaw,
            host,
            null /*extendedConfigCache*/);

        Assert.True(errors is null || errors.Length == 0, $"Should not have errors: {string.Join(" | ", errors ?? [])}");

        // Check that customConditions is nil (overridden by command line)
        Assert.Null(parsedConfig!.CompilerOptions()!.CustomConditions);
    }
}
