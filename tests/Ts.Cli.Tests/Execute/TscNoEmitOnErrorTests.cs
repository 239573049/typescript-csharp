// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscNoEmitOnError.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscNoEmitOnErrorTests
{
    // tsc_test.go:3811-3815
    private sealed class TscNoEmitOnErrorScenario
    {
        internal string subScenario = "";
        internal string mainErrorContent = "";
        internal string fixedErrorContent = "";
    }

    // tsc_test.go:3816-3838 — getTscNoEmitOnErrorFileMap.
    private static Dictionary<string, object> GetTscNoEmitOnErrorFileMap(TscNoEmitOnErrorScenario scenario, bool declaration, bool incremental) => new()
    {
        ["/user/username/projects/noEmitOnError/tsconfig.json"] = StringTestUtil.Dedent($$"""
            {
                "compilerOptions": {
                    "outDir": "./dev-build",
                    "declaration": {{(declaration ? "true" : "false")}},
                    "incremental": {{(incremental ? "true" : "false")}},
                    "noEmitOnError": true,
                },
            }
"""),
        ["/user/username/projects/noEmitOnError/shared/types/db.ts"] = StringTestUtil.Dedent("""
            export interface A {
                name: string;
            }
"""),
        ["/user/username/projects/noEmitOnError/src/main.ts"] = scenario.mainErrorContent,
        ["/user/username/projects/noEmitOnError/src/other.ts"] = StringTestUtil.Dedent("""
            console.log("hi");
            export { }
"""),
    };

    // tsc_test.go:3839-3885 — getTscNoEmitOnErrorTestCases.
    private static List<TscInput> GetTscNoEmitOnErrorTestCases(TscNoEmitOnErrorScenario[] scenarios, string[] commandLineArgs)
    {
        var testCases = new List<TscInput>(scenarios.Length * 4);
        foreach (var scenario in scenarios)
        {
            TscEdit[] edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "Fix error",
                    edit = sys => sys.WriteFileNoError("/user/username/projects/noEmitOnError/src/main.ts", scenario.fixedErrorContent),
                },
                TscEdit.NoChange,
            ];
            testCases.Add(new TscInput
            {
                subScenario = scenario.subScenario,
                files = GetTscNoEmitOnErrorFileMap(scenario, false, false),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = edits,
            });
            testCases.Add(new TscInput
            {
                subScenario = scenario.subScenario + " with declaration",
                files = GetTscNoEmitOnErrorFileMap(scenario, true, false),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = edits,
            });
            testCases.Add(new TscInput
            {
                subScenario = scenario.subScenario + " with incremental",
                files = GetTscNoEmitOnErrorFileMap(scenario, false, true),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = edits,
            });
            testCases.Add(new TscInput
            {
                subScenario = scenario.subScenario + " with declaration with incremental",
                files = GetTscNoEmitOnErrorFileMap(scenario, true, true),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = edits,
            });
        }
        return testCases;
    }

    // tsc_test.go:3886-3949 — getTscWatchNoEmitOnErrorTestCases.
    private static List<TscInput> GetTscWatchNoEmitOnErrorTestCases(TscNoEmitOnErrorScenario[] scenarios, string[] commandLineArgs)
    {
        var edits = new List<TscEdit>();
        foreach (var scenario in scenarios)
        {
            if (edits.Count > 0)
            {
                edits.Add(new TscEdit
                {
                    caption = scenario.subScenario,
                    edit = sys => sys.WriteFileNoError("/user/username/projects/noEmitOnError/src/main.ts", scenario.mainErrorContent),
                });
            }
            edits.Add(new TscEdit
            {
                caption = "No Change",
                edit = sys => sys.WriteFileNoError(
                    "/user/username/projects/noEmitOnError/src/main.ts",
                    sys.ReadFileNoError("/user/username/projects/noEmitOnError/src/main.ts")),
            });
            edits.Add(new TscEdit
            {
                caption = "Fix " + scenario.subScenario,
                edit = sys => sys.WriteFileNoError("/user/username/projects/noEmitOnError/src/main.ts", scenario.fixedErrorContent),
            });
            edits.Add(new TscEdit
            {
                caption = "No Change",
                edit = sys => sys.WriteFileNoError(
                    "/user/username/projects/noEmitOnError/src/main.ts",
                    sys.ReadFileNoError("/user/username/projects/noEmitOnError/src/main.ts")),
            });
        }
        var editArray = edits.ToArray();
        return
        [
            new TscInput
            {
                subScenario = "noEmitOnError",
                files = GetTscNoEmitOnErrorFileMap(scenarios[0], false, false),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = editArray,
            },
            new TscInput
            {
                subScenario = "noEmitOnError with declaration",
                files = GetTscNoEmitOnErrorFileMap(scenarios[0], true, false),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = editArray,
            },
            new TscInput
            {
                subScenario = "noEmitOnError with incremental",
                files = GetTscNoEmitOnErrorFileMap(scenarios[0], false, true),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = editArray,
            },
            new TscInput
            {
                subScenario = "noEmitOnError with declaration with incremental",
                files = GetTscNoEmitOnErrorFileMap(scenarios[0], true, true),
                cwd = "/user/username/projects/noEmitOnError",
                commandLineArgs = commandLineArgs,
                edits = editArray,
            },
        ];
    }

    // tsc_test.go:3950-3985
    private static TscNoEmitOnErrorScenario[] Scenarios() =>
    [
        new TscNoEmitOnErrorScenario
        {
            subScenario = "syntax errors",
            mainErrorContent = StringTestUtil.Dedent("""
import { A } from "../shared/types/db";
const a = {
    lastName: 'sdsd'
;
"""),
            fixedErrorContent = StringTestUtil.Dedent("""
import { A } from "../shared/types/db";
const a = {
    lastName: 'sdsd'
};
"""),
        },
        new TscNoEmitOnErrorScenario
        {
            subScenario = "semantic errors",
            mainErrorContent = StringTestUtil.Dedent("""
import { A } from "../shared/types/db";
const a: string = 10;
"""),
            fixedErrorContent = StringTestUtil.Dedent("""
import { A } from "../shared/types/db";
const a: string = "hello";
"""),
        },
        new TscNoEmitOnErrorScenario
        {
            subScenario = "dts errors",
            mainErrorContent = StringTestUtil.Dedent("""
import { A } from "../shared/types/db";
export const a = class { private p = 10; };
"""),
            fixedErrorContent = StringTestUtil.Dedent("""
import { A } from "../shared/types/db";
export const a = class { p = 10; };
"""),
        },
    ];

    // tsc_test.go:3886-3949 — the watch group (scenarios all contribute edits; the file maps come
    // from scenarios[0] only).
    [Fact]
    public void BuildWatchVerbose()
    {
        foreach (var testCase in GetTscWatchNoEmitOnErrorTestCases(Scenarios(), ["-b", "-w", "-v"]))
        {
            testCase.RunAndAssert("noEmitOnError");
        }
    }

    // tsc_test.go:4053-4055 / 3986-3988
    [Fact]
    public void TscNoEmitOnError()
    {
        foreach (var testCase in GetTscNoEmitOnErrorTestCases(Scenarios(), []))
        {
            testCase.RunAndAssert("noEmitOnError");
        }
    }

    // tsc_test.go:3987-3988 — the "-b -v" group.
    [Fact]
    public void BuildNoEmitOnError()
    {
        foreach (var testCase in GetTscNoEmitOnErrorTestCases(Scenarios(), ["-b", "-v"]))
        {
            testCase.RunAndAssert("noEmitOnError");
        }
    }

    // tsc_test.go:3989-4020
    [Fact]
    public void WhenDeclarationMapChanges()
    {
        new TscInput
        {
            subScenario = "when declarationMap changes",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "noEmitOnError": true,
                            "declaration": true,
                            "composite": true,
                        },
                    }
"""),
                ["/home/src/workspaces/project/a.ts"] = "const x = 10;",
                ["/home/src/workspaces/project/b.ts"] = "const y = 10;",
            },
            edits =
            [
                new TscEdit
                {
                    caption = "error and enable declarationMap",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/a.ts", "x", "x: 20"),
                    commandLineArgs = ["--declarationMap"],
                },
                new TscEdit
                {
                    caption = "fix error declarationMap",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/a.ts", "x: 20", "x"),
                    commandLineArgs = ["--declarationMap"],
                },
            ],
        }.RunAndAssert("noEmitOnError");
    }

    // tsc_test.go:4021-4043
    [Fact]
    public void FileDeletedBeforeFixingErrorWithNoEmitOnError()
    {
        new TscInput
        {
            subScenario = "file deleted before fixing error with noEmitOnError",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "outDir": "outDir",
                            "noEmitOnError": true,
                        },
                    }
"""),
                ["/home/src/workspaces/project/file1.ts"] = "export const x: 30 = \"hello\";",
                ["/home/src/workspaces/project/file2.ts"] = "export class D { }",
            },
            commandLineArgs = ["-i"],
            edits =
            [
                new TscEdit
                {
                    caption = "delete file without error",
                    edit = sys => sys.RemoveNoError("/home/src/workspaces/project/file2.ts"),
                },
            ],
        }.RunAndAssert("noEmitOnError");
    }
}
