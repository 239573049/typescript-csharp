// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscListFilesOnly.

using System.Collections.Generic;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscListFilesOnlyTests
{
    // tsc_test.go:2384-2419 — TestTscListFilesOnly.
    [Fact]
    public void LooseFile()
    {
        new TscInput
        {
            subScenario = "loose file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/test.ts"] = "export const x = 1;",
            },
            commandLineArgs = ["test.ts", "--listFilesOnly"],
        }.RunAndAssert("listFilesOnly");
    }

    [Fact]
    public void CombinedWithIncremental()
    {
        new TscInput
        {
            subScenario = "combined with incremental",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/test.ts"] = "export const x = 1;",
                ["/home/src/workspaces/project/tsconfig.json"] = "{}",
            },
            commandLineArgs = ["--incremental", "--listFilesOnly"],
            edits =
            [
                new TscEdit
                {
                    caption = "incremental actual build",
                    commandLineArgs = ["--incremental"],
                },
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "incremental should not build",
                    commandLineArgs = ["--incremental"],
                },
            ],
        }.RunAndAssert("listFilesOnly");
    }
}
