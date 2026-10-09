// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscNoCheck.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscNoCheckTests
{
    // tsc_test.go:3258-3261
    private sealed record NoCheckScenario(string subScenario, string aText);

    // tsc_test.go:3262-3319 — getTscNoCheckTestCase.
    private static TscInput GetTscNoCheckTestCase(NoCheckScenario scenario, bool incremental, string[] commandLineArgs)
    {
        var noChangeWithCheck = new TscEdit
        {
            caption = "No Change run with checking",
            commandLineArgs = commandLineArgs,
        };
        var fixErrorNoCheck = new TscEdit
        {
            caption = "Fix `a` error with noCheck",
            edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/a.ts", "export const a = \"hello\";"),
        };
        var addErrorNoCheck = new TscEdit
        {
            caption = "Introduce error with noCheck",
            edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/a.ts", scenario.aText),
        };
        return new TscInput
        {
            subScenario = scenario.subScenario + (incremental ? " with incremental" : ""),
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a.ts"] = scenario.aText,
                ["/home/src/workspaces/project/b.ts"] = "export const b = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "declaration": true,
                        "incremental": {{(incremental ? "true" : "false")}}
                    }
                }
"""),
            },
            commandLineArgs = [.. commandLineArgs, "--noCheck"],
            edits =
            [
                TscEdit.NoChange,
                fixErrorNoCheck,   // Fix error with noCheck
                TscEdit.NoChange,  // Should be no op
                noChangeWithCheck, // Check errors - should not report any errors - update buildInfo
                noChangeWithCheck, // Should be no op
                TscEdit.NoChange,  // Should be no op
                addErrorNoCheck,
                TscEdit.NoChange,  // Should be no op
                noChangeWithCheck, // Should check errors and update buildInfo
                fixErrorNoCheck,   // Fix error with noCheck
                noChangeWithCheck, // Should check errors and update buildInfo
                new TscEdit
                {
                    caption = "Add file with error",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/c.ts", "export const c: number = \"hello\";"),
                    commandLineArgs = commandLineArgs,
                },
                addErrorNoCheck,
                fixErrorNoCheck,
                noChangeWithCheck,
                TscEdit.NoChange,  // Should be no op
                noChangeWithCheck, // Should be no op
            ],
        };
    }

    // tsc_test.go:3321-3325
    private static readonly NoCheckScenario[] Cases =
    [
        new("syntax errors", "export const a = \"hello"),
        new("semantic errors", "export const a: number = \"hello\";"),
        new("dts errors", "export const a = class { private p = 10; };"),
    ];

    // tsc_test.go:3334-3336 — the four getTscNoCheckTestCase groups per case, all run against
    // the "noCheck" scenario (tsc/ for [] and tsbuild/ for -b).
    [Fact]
    public void SyntaxErrors() => GetTscNoCheckTestCase(Cases[0], false, []).RunAndAssert("noCheck");

    [Fact]
    public void SyntaxErrorsWithIncremental() => GetTscNoCheckTestCase(Cases[0], true, []).RunAndAssert("noCheck");

    [Fact]
    public void BuildSyntaxErrors() => GetTscNoCheckTestCase(Cases[0], false, ["-b", "-v"]).RunAndAssert("noCheck");

    [Fact]
    public void BuildSyntaxErrorsWithIncremental() => GetTscNoCheckTestCase(Cases[0], true, ["-b", "-v"]).RunAndAssert("noCheck");

    [Fact]
    public void SemanticErrors() => GetTscNoCheckTestCase(Cases[1], false, []).RunAndAssert("noCheck");

    [Fact]
    public void SemanticErrorsWithIncremental() => GetTscNoCheckTestCase(Cases[1], true, []).RunAndAssert("noCheck");

    [Fact]
    public void BuildSemanticErrors() => GetTscNoCheckTestCase(Cases[1], false, ["-b", "-v"]).RunAndAssert("noCheck");

    [Fact]
    public void BuildSemanticErrorsWithIncremental() => GetTscNoCheckTestCase(Cases[1], true, ["-b", "-v"]).RunAndAssert("noCheck");

    [Fact]
    public void DtsErrors() => GetTscNoCheckTestCase(Cases[2], false, []).RunAndAssert("noCheck");

    [Fact]
    public void DtsErrorsWithIncremental() => GetTscNoCheckTestCase(Cases[2], true, []).RunAndAssert("noCheck");

    [Fact]
    public void BuildDtsErrors() => GetTscNoCheckTestCase(Cases[2], false, ["-b", "-v"]).RunAndAssert("noCheck");

    [Fact]
    public void BuildDtsErrorsWithIncremental() => GetTscNoCheckTestCase(Cases[2], true, ["-b", "-v"]).RunAndAssert("noCheck");
}
