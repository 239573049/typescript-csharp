// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscCommandline.
//
// The Go test is a single table driving `test.run(t, "commandLine")`; each subScenario is one
// baseline file under testdata/baselines/reference/{tsc,tsbuild,tscWatch}/commandLine, so each
// entry is a separate [Fact] here.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscCommandlineTests
{
    // tsc_test.go:17-23
    [Fact]
    public void ShowHelpWithExitStatusDiagnosticsPresentOutputsSkipped()
    {
        new TscInput
        {
            subScenario = "show help with ExitStatus.DiagnosticsPresent_OutputsSkipped",
            env = new Dictionary<string, string>
            {
                ["TS_TEST_TERMINAL_WIDTH"] = "120",
            },
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:24-27
    [Fact]
    public void ShowHelpWithExitStatusDiagnosticsPresentOutputsSkippedWhenHostCannotProvideTerminalWidth()
    {
        new TscInput
        {
            subScenario = "show help with ExitStatus.DiagnosticsPresent_OutputsSkipped when host cannot provide terminal width",
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:28-34
    [Fact]
    public void DoesNotAddColorWhenNoColorIsSet()
    {
        new TscInput
        {
            subScenario = "does not add color when NO_COLOR is set",
            env = new Dictionary<string, string>
            {
                ["NO_COLOR"] = "true",
            },
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:35-41
    [Fact]
    public void AddsColorWhenForceColorIsSet()
    {
        new TscInput
        {
            subScenario = "adds color when FORCE_COLOR is set",
            env = new Dictionary<string, string>
            {
                ["FORCE_COLOR"] = "true",
            },
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:42-49
    [Fact]
    public void DoesNotAddColorWhenNoColorIsSetEvenIfForceColorIsSet()
    {
        new TscInput
        {
            subScenario = "does not add color when NO_COLOR is set even if FORCE_COLOR is set",
            env = new Dictionary<string, string>
            {
                ["NO_COLOR"] = "true",
                ["FORCE_COLOR"] = "true",
            },
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:50-53
    [Fact]
    public void WhenBuildNotFirstArgument()
    {
        new TscInput
        {
            subScenario = "when build not first argument",
            commandLineArgs = ["--verbose", "--build"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:54-57
    [Fact]
    public void InitializedTSConfigWithFilesOptions()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with files options",
            commandLineArgs = ["--init", "file0.st", "file1.ts", "file2.ts"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:58-61
    [Fact]
    public void InitializedTSConfigWithBooleanValueCompilerOptions()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with boolean value compiler options",
            commandLineArgs = ["--init", "--noUnusedLocals"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:62-65
    [Fact]
    public void InitializedTSConfigWithEnumValueCompilerOptions()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with enum value compiler options",
            commandLineArgs = ["--init", "--target", "es5", "--jsx", "react"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:66-69
    [Fact]
    public void InitializedTSConfigWithListCompilerOptions()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with list compiler options",
            commandLineArgs = ["--init", "--types", "jquery,mocha"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:70-73
    [Fact]
    public void InitializedTSConfigWithListCompilerOptionsWithEnumValue()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with list compiler options with enum value",
            commandLineArgs = ["--init", "--lib", "es5,es2015.core"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:74-77
    [Fact]
    public void InitializedTSConfigWithIncorrectCompilerOption()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with incorrect compiler option",
            commandLineArgs = ["--init", "--someNonExistOption"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:78-81
    [Fact]
    public void InitializedTSConfigWithIncorrectCompilerOptionValue()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with incorrect compiler option value",
            commandLineArgs = ["--init", "--lib", "nonExistLib,es5,es2015.promise"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:82-85
    [Fact]
    public void InitializedTSConfigWithAdvancedOptions()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with advanced options",
            commandLineArgs = ["--init", "--declaration", "--declarationDir", "lib", "--skipLibCheck", "--noErrorTruncation"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:86-89
    [Fact]
    public void InitializedTSConfigWithHelp()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with --help",
            commandLineArgs = ["--init", "--help"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:90-93
    [Fact]
    public void InitializedTSConfigWithWatch()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with --watch",
            commandLineArgs = ["--init", "--watch"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:94-107
    [Fact]
    public void InitializedTSConfigWithTsconfigJson()
    {
        new TscInput
        {
            subScenario = "Initialized TSConfig with tsconfig.json",
            commandLineArgs = ["--init"],
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "noEmit": true
                    }
                }
"""),
            },
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:108-111
    [Fact]
    public void Help()
    {
        new TscInput
        {
            subScenario = "help",
            commandLineArgs = ["--help"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:112-115
    [Fact]
    public void HelpAll()
    {
        new TscInput
        {
            subScenario = "help all",
            commandLineArgs = ["--help", "--all"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:116-120
    [Fact]
    public void ParseLibOptionWithFileName()
    {
        new TscInput
        {
            subScenario = "Parse --lib option with file name",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const Key = Symbol()",
            },
            commandLineArgs = ["--lib", "es6 ", "first.ts"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:121-125
    [Fact]
    public void OptionDiagnosticsAreSuppressedWhenThereAreSyntacticErrors()
    {
        new TscInput
        {
            subScenario = "option diagnostics are suppressed when there are syntactic errors",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a.ts"] = "const x: = 1;",
            },
            commandLineArgs = ["--strictPropertyInitialization", "--strictNullChecks", "false", "a.ts"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:126-139
    [Fact]
    public void ProjectIsEmptyString()
    {
        new TscInput
        {
            subScenario = "Project is empty string",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "noEmit": true
                    }
                }
"""),
            },
            commandLineArgs = [],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:140-153
    [Fact]
    public void ParseP()
    {
        new TscInput
        {
            subScenario = "Parse -p",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "noEmit": true
                    }
                }
"""),
            },
            commandLineArgs = ["-p", "."],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:154-167
    [Fact]
    public void ParsePWithPathToTsconfigFile()
    {
        new TscInput
        {
            subScenario = "Parse -p with path to tsconfig file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "noEmit": true
                    }
                }
"""),
            },
            commandLineArgs = ["-p", "/home/src/workspaces/project/tsconfig.json"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:168-181
    [Fact]
    public void ParsePWithPathToTsconfigFolder()
    {
        new TscInput
        {
            subScenario = "Parse -p with path to tsconfig folder",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "noEmit": true
                    }
                }
"""),
            },
            commandLineArgs = ["-p", "/home/src/workspaces/project"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:182-189
    [Fact]
    public void ParsePWithEmptyTsconfigFile()
    {
        new TscInput
        {
            subScenario = "Parse -p with empty tsconfig file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = "",
            },
            commandLineArgs = ["-p", "."],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:190-193
    [Fact]
    public void ParseEnumTypeOptions()
    {
        new TscInput
        {
            subScenario = "Parse enum type options",
            commandLineArgs = ["--moduleResolution", "nodenext ", "first.ts", "--module", "nodenext", "--target", "esnext", "--moduleDetection", "auto", "--jsx", "react", "--newLine", "crlf"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:194-207
    [Fact]
    public void ParseWatchIntervalOption()
    {
        new TscInput
        {
            subScenario = "Parse watch interval option",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/first.ts"] = "export const a = 1",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "noEmit": true
                    }
                }
"""),
            },
            commandLineArgs = ["-w", "--watchInterval", "1000"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:208-211
    [Fact]
    public void ParseWatchIntervalOptionWithoutTsconfigJson()
    {
        new TscInput
        {
            subScenario = "Parse watch interval option without tsconfig.json",
            commandLineArgs = ["-w", "--watchInterval", "1000"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:212-235
    [Fact]
    public void ConfigWithReferencesAndEmptyFileAndRefersToConfigWithNoEmit()
    {
        new TscInput
        {
            subScenario = "Config with references and empty file and refers to config with noEmit",
            files = new Dictionary<string, object>
            {
                // Literal indentation kept byte-faithful to tsc_test.go:215-233 (the first line of
                // the snippet is flush left); stringtestutil.Dedent only removes the common
                // indentation, so this survives verbatim in the baseline input.
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent(
"""
{
					"files": [],
					"references": [
						{
							"path": "./packages/pkg1"
						},
					],
				}
"""),
                ["/home/src/workspaces/project/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent(
"""
{
					"compilerOptions": {
						"composite": true,
						"noEmit": true
					},
					"files": [
						"./index.ts",
					],
				}
"""),
                ["/home/src/workspaces/project/packages/pkg1/index.ts"] = "export const a = 1;",
            },
            commandLineArgs = ["-p", "."],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:236-239
    [Fact]
    public void Locale()
    {
        new TscInput
        {
            subScenario = "locale",
            commandLineArgs = ["--locale", "cs", "--version"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:240-243
    [Fact]
    public void BadLocale()
    {
        new TscInput
        {
            subScenario = "bad locale",
            commandLineArgs = ["--locale", "whoops", "--version"],
        }.RunAndAssert("commandLine");
    }
}
