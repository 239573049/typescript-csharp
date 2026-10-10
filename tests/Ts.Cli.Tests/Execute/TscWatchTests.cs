// Ported from tsgo internal/execute/tsctests/tscwatch_test.go — TestTscNoEmitWatch (the
// noEmit watch matrix; its siblings TestWatch/the tscInput runner live in TscRunner.cs and are
// exercised one [Fact] per scenario, driving testdata/baselines/reference/tscWatch/noEmit).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscWatchTests
{
    // tscwatch_test.go:589-606 — listToTsconfig. The tsconfig text and the "optionString" used to
    // rebuild the same config with/without the leading noEmit option.
    private static (string TsconfigText, string OptionString) ListToTsconfig(string @base, params string[] tsconfigOpts)
    {
        var optionString = string.Join(",\n            ", tsconfigOpts);
        var tsconfigText = new StringBuilder("{\n\t\"compilerOptions\": {\n");
        var after = "            ";
        if (@base.Length != 0)
        {
            tsconfigText.Append("            ").Append(@base);
            after = ",\n            ";
        }
        if (tsconfigOpts.Length != 0)
        {
            tsconfigText.Append(after).Append(optionString);
        }
        tsconfigText.Append("\n\t}\n}");
        return (tsconfigText.ToString(), optionString);
    }

    // tscwatch_test.go:608-611 — toTsconfig.
    private static string ToTsconfig(string @base, string compilerOpts)
    {
        var (tsconfigText, _) = ListToTsconfig(@base, compilerOpts);
        return tsconfigText;
    }

    // tscwatch_test.go:651-653 — newTscEdit.
    private static TscEdit NewTscEdit(string name, Action<TestSys> edit) =>
        new() { caption = name, edit = edit };

    // tscwatch_test.go:613-649 — noEmitWatchTestInput.
    private static TscInput NoEmitWatchTestInput(
        string subScenario,
        string[] commandLineArgs,
        string aText,
        string[]? tsconfigOptions)
    {
        const string NoEmitOpt = "\"noEmit\": true";
        var (tsconfigText, optionString) = ListToTsconfig(NoEmitOpt, tsconfigOptions ?? []);
        return new TscInput
        {
            subScenario = subScenario,
            commandLineArgs = commandLineArgs,
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a.ts"] = aText,
                ["/home/src/workspaces/project/tsconfig.json"] = tsconfigText,
            },
            edits =
            [
                NewTscEdit("fix error", sys => sys.WriteFileNoError("/home/src/workspaces/project/a.ts", "const a = \"hello\";")),
                NewTscEdit("emit after fixing error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig("", optionString))),
                NewTscEdit("no emit run after fixing error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig(NoEmitOpt, optionString))),
                NewTscEdit("introduce error", sys => sys.WriteFileNoError("/home/src/workspaces/project/a.ts", aText)),
                NewTscEdit("emit when error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig("", optionString))),
                NewTscEdit("no emit run when error", sys => sys.WriteFileNoError("/home/src/workspaces/project/tsconfig.json", ToTsconfig(NoEmitOpt, optionString))),
            ],
        };
    }

    // tscwatch_test.go:658-683 — the ordered testCase list; one [Fact] per entry (Go loop: 685-687).
    private static TscInput[] NoEmitWatchTestCases() =>
    [
        NoEmitWatchTestInput("syntax errors", ["-w"], "const a = \"hello", null),
        NoEmitWatchTestInput("semantic errors", ["-w"], "const a: number = \"hello\"", null),
        NoEmitWatchTestInput("dts errors without dts enabled", ["-w"], "const a = class { private p = 10; };", null),
        NoEmitWatchTestInput("dts errors", ["-w"], "const a = class { private p = 10; };", ["\"declaration\": true"]),
    ];

    [Fact]
    public void WatchSyntaxErrors() => NoEmitWatchTestCases()[0].RunAndAssert("noEmit");

    [Fact]
    public void WatchSemanticErrors() => NoEmitWatchTestCases()[1].RunAndAssert("noEmit");

    [Fact]
    public void WatchDtsErrorsWithoutDtsEnabled() => NoEmitWatchTestCases()[2].RunAndAssert("noEmit");

    [Fact]
    public void WatchDtsErrors() => NoEmitWatchTestCases()[3].RunAndAssert("noEmit");
}
