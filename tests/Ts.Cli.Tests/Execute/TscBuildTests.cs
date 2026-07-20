// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildClean (the two
// smallest scenarios), driving the full baseline runner against the synced tsgo reference
// baselines under testdata/baselines/reference/tsbuild/clean.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildTests
{
    // tscbuild_test.go:300-341 — TestBuildClean.
    [Fact]
    public void Clean_FileNameAndOutputNameClashing()
    {
        new TscInput
        {
            subScenario = "file name and output name clashing",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/index.js"] = "",
                ["/home/src/workspaces/solution/bar.ts"] = "",
                ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"compilerOptions": { "allowJs": true }
				}
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "--clean"],
        }.RunAndAssert("clean");
    }

    [Fact]
    public void Clean_TsxWithDtsEmit()
    {
        new TscInput
        {
            subScenario = "tsx with dts emit",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/project/src/main.tsx"] = "export const x = 10;",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"compilerOptions": { "declaration": true },
					"include": ["src/**/*.tsx", "src/**/*.ts"]
				}
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "project", "-v", "--explainFiles"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "clean build",
                    commandLineArgs = ["-b", "project", "--clean"],
                },
            ],
        }.RunAndAssert("clean");
    }
}

internal static class TscInputTestExtensions
{
    internal static void RunAndAssert(this TscInput input, string scenario)
    {
        var ctx = new BaselineTestContext();
        input.Run(ctx, scenario);
        Assert.False(ctx.Failed, string.Join("\n", ctx.Errors));
    }
}
