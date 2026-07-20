// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildConfigFileErrors.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildConfigFileErrorsTests
{
    // tscbuild_test.go:343-522
    [Fact]
    public void WhenTsconfigExtendsTheMissingFile()
    {
        new TscInput
        {
            subScenario = "when tsconfig extends the missing file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.first.json"] = StringTestUtil.Dedent("""
					{
						"extends": "./foobar.json",
						"compilerOptions": {
							"composite": true
						}
					}
"""),
                ["/home/src/workspaces/project/tsconfig.second.json"] = StringTestUtil.Dedent("""
					{
						"extends": "./foobar.json",
						"compilerOptions": {
							"composite": true
						}
					}
"""),
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true
						},
						"references": [
							{ "path": "./tsconfig.first.json" },
							{ "path": "./tsconfig.second.json" }
						]
					}
"""),
            },
            commandLineArgs = ["--b"],
        }.RunAndAssert("configFileErrors");
    }

    [Fact]
    public void ReportsInvalidProjectReferenceFields()
    {
        new TscInput
        {
            subScenario = "reports invalid project reference fields",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true
						},
						"files": ["index.ts"],
						"references": [
							{ "path": true },
							{ "circular": true },
							{ "path": "./utils", "circular": "yes" },
							{ "path": "" },
							{ "path": "./valid", "circular": true }
						]
					}
"""),
                ["/home/src/workspaces/project/index.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/utils/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true
						},
						"files": ["index.ts"]
					}
"""),
                ["/home/src/workspaces/project/utils/index.ts"] = "export const y = 10;",
                ["/home/src/workspaces/project/valid/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true
						},
						"files": ["index.ts"]
					}
"""),
                ["/home/src/workspaces/project/valid/index.ts"] = "export const z = 10;",
            },
            commandLineArgs = ["--b", "--dry"],
        }.RunAndAssert("configFileErrors");
    }

    private static TscInput ReportsSyntaxErrorsInput(string[] commandLineArgs, TscEdit[] edits) => new()
    {
        subScenario = "reports syntax errors in config file",
        files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/a.ts"] = "export function foo() { }",
            ["/home/src/workspaces/project/b.ts"] = "export function bar() { }",
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
						},
						"files": [
							"a.ts"
							"b.ts"
						]
					}
"""),
        },
        commandLineArgs = commandLineArgs,
        edits = edits,
    };

    private static readonly string FixedConfig = StringTestUtil.Dedent("""
							{
								"compilerOptions": {
									"composite": true, "declaration": true
								},
								"files": [
									"a.ts",
									"b.ts"
								]
							}
""");

    [Fact]
    public void ReportsSyntaxErrorsInConfigFile()
    {
        ReportsSyntaxErrorsInput(
            ["--b"],
            [
                new TscEdit
                {
                    caption = "reports syntax errors after change to config file",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/tsconfig.json", ",", ", \"declaration\": true"),
                },
                new TscEdit
                {
                    caption = "reports syntax errors after change to ts file",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/a.ts", "export function fooBar() { }"),
                },
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "builds after fixing config file errors",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", FixedConfig),
                },
            ]).RunAndAssert("configFileErrors");
    }

    [Fact]
    public void MissingConfigFile()
    {
        new TscInput
        {
            subScenario = "missing config file",
            files = new Dictionary<string, object>(),
            commandLineArgs = ["--b", "bogus.json"],
        }.RunAndAssert("configFileErrors");
    }

    [Fact]
    public void ReportsSyntaxErrorsInConfigFile_Watch()
    {
        ReportsSyntaxErrorsInput(
            ["--b", "-w"],
            [
                new TscEdit
                {
                    caption = "reports syntax errors after change to config file",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/tsconfig.json", ",", ", \"declaration\": true"),
                },
                new TscEdit
                {
                    caption = "reports syntax errors after change to ts file",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/a.ts", "export function fooBar() { }"),
                },
                new TscEdit
                {
                    caption = "reports error when there is no change to tsconfig file",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/tsconfig.json", "", ""),
                },
                new TscEdit
                {
                    caption = "builds after fixing config file errors",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", FixedConfig),
                },
            ]).RunAndAssert("configFileErrors");
    }
}
