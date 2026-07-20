// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildOutputPaths.

using System.Collections.Generic;
using System.Linq;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildOutputPathsTests
{
    // tscbuild_test.go:2033-2156
    private static void RunOutputPaths(string subScenario, Dictionary<string, object> files, string[] expectedDtsNames)
    {
        var input = new TscInput
        {
            subScenario = subScenario,
            files = files,
            commandLineArgs = ["-b", "-v"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "Normal build without change, that does not block emit on error to show files that get emitted",
                    commandLineArgs = ["-p", "/home/src/workspaces/project/tsconfig.json"],
                },
            ],
        };
        input.RunAndAssert("outputPaths");

        // GetOutputFileNames sub-assertion.
        var sys = TestSys.NewTestSys(input, false);
        var (config, _) = global::Ts.TsOptions.TsConfigParsing.GetParsedCommandLineOfConfigFile(
            "/home/src/workspaces/project/tsconfig.json", new global::Ts.Core.CompilerOptions(), null, sys, null);
        Assert.Equal(expectedDtsNames, config!.GetOutputFileNames().ToArray());
    }

    [Fact]
    public void WhenRootDirIsNotSpecified() => RunOutputPaths(
        "when rootDir is not specified",
        new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/src/index.ts"] = "export const x = 10;",
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "outDir": "dist",
                    },
                }
"""),
        },
        ["/home/src/workspaces/project/dist/src/index.js"]);

    [Fact]
    public void WhenRootDirIsNotSpecifiedAndIsComposite() => RunOutputPaths(
        "when rootDir is not specified and is composite",
        new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/src/index.ts"] = "export const x = 10;",
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "outDir": "dist",
						"composite": true,
                    },
                }
"""),
        },
        [
            "/home/src/workspaces/project/dist/src/index.js",
            "/home/src/workspaces/project/dist/src/index.d.ts",
        ]);

    [Fact]
    public void WhenRootDirIsSpecified() => RunOutputPaths(
        "when rootDir is specified",
        new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/src/index.ts"] = "export const x = 10;",
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "outDir": "dist",
						"rootDir": "src",
                    },
                }
"""),
        },
        ["/home/src/workspaces/project/dist/index.js"]);

    // !!! sheetal error missing as not yet implemented
    [Fact]
    public void WhenRootDirIsSpecifiedButNotAllFilesBelongToRootDir() => RunOutputPaths(
        "when rootDir is specified but not all files belong to rootDir",
        new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/src/index.ts"] = "export const x = 10;",
            ["/home/src/workspaces/project/types/type.ts"] = "export type t = string;",
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "outDir": "dist",
						"rootDir": "src",
                    },
                }
"""),
        },
        [
            "/home/src/workspaces/project/dist/index.js",
            "/home/src/workspaces/project/types/type.js",
        ]);

    // !!! sheetal error missing as not yet implemented
    [Fact]
    public void WhenRootDirIsSpecifiedButNotAllFilesBelongToRootDirAndIsComposite() => RunOutputPaths(
        "when rootDir is specified but not all files belong to rootDir and is composite",
        new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/src/index.ts"] = "export const x = 10;",
            ["/home/src/workspaces/project/types/type.ts"] = "export type t = string;",
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "outDir": "dist",
						"rootDir": "src",
						"composite": true
                    },
                }
"""),
        },
        [
            "/home/src/workspaces/project/dist/index.js",
            "/home/src/workspaces/project/dist/index.d.ts",
            "/home/src/workspaces/project/types/type.js",
            "/home/src/workspaces/project/types/type.d.ts",
        ]);
}
