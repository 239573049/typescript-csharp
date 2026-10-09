// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscIgnoreConfig.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscIgnoreConfigTests
{
    // tsc_test.go:1248-1254
    private static Dictionary<string, object> FilesWithoutConfig() => new()
    {
        ["/home/src/workspaces/project/src/a.ts"] = "export const a = 10;",
        ["/home/src/workspaces/project/src/b.ts"] = "export const b = 10;",
        ["/home/src/workspaces/project/c.ts"] = "export const c = 10;",
    };

    // tsc_test.go:1255-1262
    private static Dictionary<string, object> FilesWithConfig()
    {
        var files = FilesWithoutConfig();
        files["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
        {
            "include": ["src"],
        }
""");
        return files;
    }

    // tsc_test.go:1263-1287
    private static TscInput[] GetScenarios(string subScenario, string[] commandLineArgs)
    {
        var commandLineArgsIgnoreConfig = new string[commandLineArgs.Length + 1];
        commandLineArgs.CopyTo(commandLineArgsIgnoreConfig, 0);
        commandLineArgsIgnoreConfig[^1] = "--ignoreConfig";
        return
        [
            new TscInput
            {
                subScenario = subScenario,
                files = FilesWithConfig(),
                commandLineArgs = commandLineArgs,
            },
            new TscInput
            {
                subScenario = subScenario + " with --ignoreConfig",
                files = FilesWithConfig(),
                commandLineArgs = commandLineArgsIgnoreConfig,
            },
            new TscInput
            {
                subScenario = subScenario + " when config file absent",
                files = FilesWithoutConfig(),
                commandLineArgs = commandLineArgs,
            },
            new TscInput
            {
                subScenario = subScenario + " when config file absent with --ignoreConfig",
                files = FilesWithoutConfig(),
                commandLineArgs = commandLineArgsIgnoreConfig,
            },
        ];
    }

    // tsc_test.go:1288-1296 — slices.Concat of the four getScenarios groups.
    private static TscInput[] AllScenarios() =>
        [.. GetScenarios("without any options", []),
         .. GetScenarios("specifying files", ["src/a.ts"]),
         .. GetScenarios("specifying project", ["-p", "."]),
         .. GetScenarios("mixing project and files", ["-p", ".", "src/a.ts", "c.ts"])];

    // tsc_test.go:1288-1296 — `for _, test := range testCases { test.run(t, "ignoreConfig") }`.
    private static void RunAll()
    {
        foreach (var testCase in AllScenarios())
        {
            testCase.RunAndAssert("ignoreConfig");
        }
    }

    [Fact]
    public void WithoutAnyOptions() => RunAll();

    [Fact]
    public void SpecifyingFiles() => RunAll();

    [Fact]
    public void SpecifyingProject() => RunAll();

    [Fact]
    public void MixingProjectAndFiles() => RunAll();
}
