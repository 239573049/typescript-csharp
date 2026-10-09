// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscNoEmit.

using System;
using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscNoEmitTests
{
    // tsc_test.go:3341-3345
    private sealed record NoEmitScenario(string subScenario, string aText, bool dtsEnabled = false);

    // tsc_test.go:3346-3364
    private static readonly NoEmitScenario[] NoEmitScenarios =
    [
        new("syntax errors", "const a = \"hello"),
        new("semantic errors", "const a: number = \"hello\""),
        new("dts errors", "const a = class { private p = 10; };", dtsEnabled: true),
        new("dts errors without dts enabled", "const a = class { private p = 10; };"),
    ];

    // tsc_test.go:3365-3384 — getTscNoEmitAndErrorsFileMap.
    private static Dictionary<string, object> GetTscNoEmitAndErrorsFileMap(NoEmitScenario scenario, bool incremental, bool asModules, Action<Dictionary<string, object>>? modify = null)
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/projects/project/a.ts"] = (asModules ? "export " : "") + scenario.aText,
            ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "incremental": {{(incremental ? "true" : "false")}},
                        "declaration": {{(scenario.dtsEnabled ? "true" : "false")}}
                    }
                }
"""),
        };
        if (asModules)
        {
            files["/home/src/projects/project/b.ts"] = "export const b = 10;";
        }
        modify?.Invoke(files);
        return files;
    }

    // tsc_test.go:3385-3418 — getTscNoEmitAndErrorsTestCasesWorker.
    private static TscInput[] GetTscNoEmitAndErrorsTestCasesWorker(
        string[] commandLineArgs,
        bool addNoEmitOnCommandLine,
        Action<Dictionary<string, object>>? modify,
        Func<NoEmitScenario, string[], bool, TscEdit[]> edits)
    {
        var testingCases = new List<TscInput>(NoEmitScenarios.Length * 3);
        var commandLineArgsForInput = commandLineArgs;
        if (addNoEmitOnCommandLine)
        {
            commandLineArgsForInput = [.. commandLineArgs, "--noEmit"];
        }
        foreach (var scenario in NoEmitScenarios)
        {
            testingCases.Add(new TscInput
            {
                subScenario = scenario.subScenario,
                commandLineArgs = commandLineArgsForInput,
                files = GetTscNoEmitAndErrorsFileMap(scenario, false, false, modify),
                cwd = "/home/src/projects/project",
                edits = edits(scenario, commandLineArgs, false),
            });
            testingCases.Add(new TscInput
            {
                subScenario = scenario.subScenario + " with incremental",
                commandLineArgs = commandLineArgsForInput,
                files = GetTscNoEmitAndErrorsFileMap(scenario, true, false, modify),
                cwd = "/home/src/projects/project",
                edits = edits(scenario, commandLineArgs, false),
            });
            testingCases.Add(new TscInput
            {
                subScenario = scenario.subScenario + " with incremental as modules",
                commandLineArgs = commandLineArgsForInput,
                files = GetTscNoEmitAndErrorsFileMap(scenario, true, true, modify),
                cwd = "/home/src/projects/project",
                edits = edits(scenario, commandLineArgs, true),
            });
        }
        return [.. testingCases];
    }

    // tsc_test.go:3419-3454 — getTscNoEmitAndErrorsTestCases.
    private static TscInput[] GetTscNoEmitAndErrorsTestCases(string[] commandLineArgs) =>
        GetTscNoEmitAndErrorsTestCasesWorker(
            commandLineArgs,
            true,
            null,
            (scenario, commandLineArgs, asModules) =>
            {
                var fixedATsContent = (asModules ? "export " : "") + "const a = \"hello\";";
                return
                [
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "Fix error",
                        edit = sys => sys.WriteFileNoError("/home/src/projects/project/a.ts", fixedATsContent),
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "Emit after fixing error",
                        commandLineArgs = commandLineArgs,
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "Introduce error",
                        edit = sys => sys.WriteFileNoError("/home/src/projects/project/a.ts", scenario.aText),
                    },
                    new TscEdit
                    {
                        caption = "Emit when error",
                        commandLineArgs = commandLineArgs,
                    },
                    TscEdit.NoChange,
                ];
            });

    // tsc_test.go:3455-3504 — getTscNoEmitAndErrorsWatchTestCases.
    private static TscInput[] GetTscNoEmitAndErrorsWatchTestCases(string[] commandLineArgs) =>
        GetTscNoEmitAndErrorsTestCasesWorker(
            commandLineArgs,
            false,
            files => files["/home/src/projects/project/tsconfig.json"] =
                ReplaceFirst((string)files["/home/src/projects/project/tsconfig.json"], "}", ", \"noEmit\": true }"),
            (scenario, commandLineArgs, asModules) =>
            {
                var fixedATsContent = (asModules ? "export " : "") + "const a = \"hello\";";
                return
                [
                    new TscEdit
                    {
                        caption = "Fix error",
                        edit = sys => sys.WriteFileNoError("/home/src/projects/project/a.ts", fixedATsContent),
                    },
                    new TscEdit
                    {
                        caption = "Emit after fixing error",
                        edit = sys => sys.ReplaceFileText("/home/src/projects/project/tsconfig.json", "\"noEmit\": true", "\"noEmit\": false"),
                    },
                    new TscEdit
                    {
                        caption = "no Emit run after fixing error",
                        edit = sys => sys.ReplaceFileText("/home/src/projects/project/tsconfig.json", "\"noEmit\": false", "\"noEmit\": true"),
                    },
                    new TscEdit
                    {
                        caption = "Introduce error",
                        edit = sys => sys.WriteFileNoError("/home/src/projects/project/a.ts", scenario.aText),
                    },
                    new TscEdit
                    {
                        caption = "Emit when error",
                        edit = sys => sys.ReplaceFileText("/home/src/projects/project/tsconfig.json", "\"noEmit\": true", "\"noEmit\": false"),
                    },
                    new TscEdit
                    {
                        caption = "no Emit run when error",
                        edit = sys => sys.ReplaceFileText("/home/src/projects/project/tsconfig.json", "\"noEmit\": false", "\"noEmit\": true"),
                    },
                ];
            });

    // tsc_test.go:3505-3533 — getTscNoEmitChangesFileMap.
    private static Dictionary<string, object> GetTscNoEmitChangesFileMap(string optionsStr) => new()
    {
        ["/home/src/workspaces/project/src/class.ts"] = StringTestUtil.Dedent("""
            export class classC {
                prop = 1;
            }
"""),
        ["/home/src/workspaces/project/src/indirectClass.ts"] = StringTestUtil.Dedent("""
            import { classC } from './class';
            export class indirectClass {
                classC = new classC();
            }
"""),
        ["/home/src/workspaces/project/src/directUse.ts"] = StringTestUtil.Dedent("""
            import { indirectClass } from './indirectClass';
            new indirectClass().classC.prop;
"""),
        ["/home/src/workspaces/project/src/indirectUse.ts"] = StringTestUtil.Dedent("""
            import { indirectClass } from './indirectClass';
            new indirectClass().classC.prop;
"""),
        ["/home/src/workspaces/project/src/noChangeFile.ts"] = StringTestUtil.Dedent("""
            export function writeLog(s: string) {
            }
"""),
        ["/home/src/workspaces/project/src/noChangeFileWithEmitSpecificError.ts"] = StringTestUtil.Dedent("""
            function someFunc(arguments: boolean, ...rest: any[]) {
            }
"""),
        ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
            {
                "compilerOptions":  { {{optionsStr}} }
            }
"""),
    };

    // tsc_test.go:3535-3538
    private sealed record NoEmitChangesScenario(string subScenario, string optionsString);

    // tsc_test.go:3539-3553
    private static readonly NoEmitChangesScenario[] NoEmitChangesScenarios =
    [
        // !!! sheetal missing initial reporting of Duplicate_identifier_arguments_Compiler_uses_arguments_to_initialize_rest_parameters is absent
        new("composite", "\"composite\": true"),
        new("incremental declaration", "\"incremental\": true, \"declaration\": true"),
        new("incremental", "\"incremental\": true"),
    ];

    // tsc_test.go:3554-3633 — getTscNoEmitChangesTestCases.
    private static TscInput[] GetTscNoEmitChangesTestCases(string[] commandLineArgs)
    {
        var noChangeWithNoEmit = new TscEdit
        {
            caption = "No Change run with noEmit",
            commandLineArgs = [.. commandLineArgs, "--noEmit"],
        };
        var noChangeWithEmit = new TscEdit
        {
            caption = "No Change run with emit",
            commandLineArgs = commandLineArgs,
        };
        Action<TestSys> introduceError = sys => sys.ReplaceFileText("/home/src/workspaces/project/src/class.ts", "prop", "prop1");
        Action<TestSys> fixError = sys => sys.ReplaceFileText("/home/src/workspaces/project/src/class.ts", "prop1", "prop");
        var testCases = new List<TscInput>(NoEmitChangesScenarios.Length);
        foreach (var scenario in NoEmitChangesScenarios)
        {
            testCases.Add(new TscInput
            {
                subScenario = "changes " + scenario.subScenario,
                commandLineArgs = commandLineArgs,
                files = GetTscNoEmitChangesFileMap(scenario.optionsString),
                edits =
                [
                    noChangeWithNoEmit,
                    noChangeWithNoEmit,
                    new TscEdit
                    {
                        caption = "Introduce error but still noEmit",
                        commandLineArgs = noChangeWithNoEmit.commandLineArgs,
                        edit = introduceError,
                    },
                    new TscEdit
                    {
                        caption = "Fix error and emit",
                        edit = fixError,
                    },
                    noChangeWithEmit,
                    noChangeWithNoEmit,
                    noChangeWithNoEmit,
                    noChangeWithEmit,
                    new TscEdit
                    {
                        caption = "Introduce error and emit",
                        edit = introduceError,
                    },
                    noChangeWithEmit,
                    noChangeWithNoEmit,
                    noChangeWithNoEmit,
                    noChangeWithEmit,
                    new TscEdit
                    {
                        caption = "Fix error and no emit",
                        commandLineArgs = noChangeWithNoEmit.commandLineArgs,
                        edit = fixError,
                    },
                    noChangeWithEmit,
                    noChangeWithNoEmit,
                    noChangeWithNoEmit,
                    noChangeWithEmit,
                ],
            });
            testCases.Add(new TscInput
            {
                subScenario = "changes with initial noEmit " + scenario.subScenario,
                commandLineArgs = noChangeWithNoEmit.commandLineArgs,
                files = GetTscNoEmitChangesFileMap(scenario.optionsString),
                edits =
                [
                    noChangeWithEmit,
                    new TscEdit
                    {
                        caption = "Introduce error with emit",
                        commandLineArgs = commandLineArgs,
                        edit = introduceError,
                    },
                    new TscEdit
                    {
                        caption = "Fix error and no emit",
                        edit = fixError,
                    },
                    noChangeWithEmit,
                ],
            });
        }
        return [.. testCases];
    }

    // tsc_test.go:3634-3649 — getTscNoEmitDtsChangesFileMap.
    private static Dictionary<string, object> GetTscNoEmitDtsChangesFileMap(bool incremental, bool asModules)
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/projects/project/a.ts"] = asModules ? "export const a = class { private p = 10; };" : "const a = class { private p = 10; };",
            ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "incremental": {{(incremental ? "true" : "false")}},
                    }
                }
"""),
        };
        if (asModules)
        {
            files["/home/src/projects/project/b.ts"] = "export const b = 10;";
        }
        return files;
    }

    // tsc_test.go:3650-3681 — getTscNoEmitDtsChangesEdits.
    private static TscEdit[] GetTscNoEmitDtsChangesEdits(string[] commandLineArgs) =>
    [
        TscEdit.NoChange,
        new TscEdit
        {
            caption = "With declaration enabled noEmit - Should report errors",
            commandLineArgs = [.. commandLineArgs, "--noEmit", "--declaration"],
        },
        new TscEdit
        {
            caption = "With declaration and declarationMap noEmit - Should report errors",
            commandLineArgs = [.. commandLineArgs, "--noEmit", "--declaration", "--declarationMap"],
        },
        TscEdit.NoChange,
        new TscEdit
        {
            caption = "Dts Emit with error",
            commandLineArgs = [.. commandLineArgs, "--declaration"],
        },
        new TscEdit
        {
            caption = "Fix the error",
            edit = sys => sys.ReplaceFileText("/home/src/projects/project/a.ts", "private", "public"),
        },
        new TscEdit
        {
            caption = "With declaration enabled noEmit",
            commandLineArgs = [.. commandLineArgs, "--noEmit", "--declaration"],
        },
        new TscEdit
        {
            caption = "With declaration and declarationMap noEmit",
            commandLineArgs = [.. commandLineArgs, "--noEmit", "--declaration", "--declarationMap"],
        },
    ];

    // tsc_test.go:3682-3706 — getTscNoEmitDtsChangesTestCases.
    private static TscInput[] GetTscNoEmitDtsChangesTestCases() =>
    [
        new TscInput
        {
            subScenario = "dts errors with declaration enable changes",
            commandLineArgs = ["-b", "-v", "--noEmit"],
            files = GetTscNoEmitDtsChangesFileMap(false, false),
            cwd = "/home/src/projects/project",
            edits = GetTscNoEmitDtsChangesEdits(["-b", "-v"]),
        },
        new TscInput
        {
            subScenario = "dts errors with declaration enable changes with incremental",
            commandLineArgs = ["-b", "-v", "--noEmit"],
            files = GetTscNoEmitDtsChangesFileMap(true, false),
            cwd = "/home/src/projects/project",
            edits = GetTscNoEmitDtsChangesEdits(["-b", "-v"]),
        },
        new TscInput
        {
            subScenario = "dts errors with declaration enable changes with incremental as modules",
            commandLineArgs = ["-b", "-v", "--noEmit"],
            files = GetTscNoEmitDtsChangesFileMap(true, true),
            cwd = "/home/src/projects/project",
            edits = GetTscNoEmitDtsChangesEdits(["-b", "-v"]),
        },
    ];

    // tsc_test.go:3707-3741 — getTscNoEmitDtsChangesMultiFileErrorsTestCases.
    private static TscInput[] GetTscNoEmitDtsChangesMultiFileErrorsTestCases(string[] commandLineArgs)
    {
        var aContent = "export const a = class { private p = 10; };";
        return
        [
            new TscInput
            {
                subScenario = "dts errors with declaration enable changes with multiple files",
                commandLineArgs = [.. commandLineArgs, "--noEmit"],
                files = new Dictionary<string, object>
                {
                    ["/home/src/projects/project/a.ts"] = aContent,
                    ["/home/src/projects/project/b.ts"] = "export const b = 10;",
                    ["/home/src/projects/project/c.ts"] = ReplaceFirst(aContent, "a", "c"),
                    ["/home/src/projects/project/d.ts"] = ReplaceFirst(aContent, "a", "d"),
                    ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""
                        {
                            "compilerOptions": {
                                "incremental": true,
                            }
                        }
"""),
                },
                cwd = "/home/src/projects/project",
                edits =
                [
                    .. GetTscNoEmitDtsChangesEdits(commandLineArgs),
                    new TscEdit
                    {
                        caption = "Fix the another ",
                        edit = sys => sys.ReplaceFileText("/home/src/projects/project/c.ts", "private", "public"),
                        commandLineArgs = [.. commandLineArgs, "--noEmit", "--declaration", "--declarationMap"],
                    },
                ],
            },
        ];
    }

    // tsc_test.go:3742-3773 — getTscNoEmitLoopTestCase.
    private static TscInput GetTscNoEmitLoopTestCase(string suffix, string[] commandLineArgs) => new()
    {
        subScenario = "does not go in loop when watching when no files are emitted" + suffix,
        files = new Dictionary<string, object>
        {
            ["/user/username/projects/myproject/a.js"] = "",
            ["/user/username/projects/myproject/b.ts"] = "",
            ["/user/username/projects/myproject/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "allowJs": true,
                        "noEmit": true,
                    },
                }
"""),
        },
        cwd = "/user/username/projects/myproject",
        commandLineArgs = commandLineArgs,
        edits =
        [
            new TscEdit
            {
                caption = "No change",
                edit = sys => sys.WriteFileNoError("/user/username/projects/myproject/a.js", sys.ReadFileNoError("/user/username/projects/myproject/a.js")),
            },
            new TscEdit
            {
                caption = "change",
                edit = sys => sys.WriteFileNoError("/user/username/projects/myproject/a.js", "const x = 10;"),
            },
        ],
    };

    // tsc_test.go:3774-3802 — the concatenated test case list
    // (`when project has strict true`, the two loop cases, then the five families below);
    // each entry is a separate [Fact] in this class. The Go loop itself is line 3804-3806.
    private static readonly Dictionary<string, object> WhenProjectHasStrictTrueFiles = new()
    {
        ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "compilerOptions": {
                    "incremental": true,
                    "strict": true
                }
            }
"""),
        ["/home/src/workspaces/project/class1.ts"] = "export class class1 {}",
    };

    private static string ReplaceFirst(string text, string oldText, string newText)
    {
        var idx = text.IndexOf(oldText, StringComparison.Ordinal);
        return idx < 0 ? text : text[..idx] + newText + text[(idx + oldText.Length)..];
    }

    // tsc_test.go:3774-3806 — one [Fact] per entry of the concatenated testCases slice.
    [Fact]
    public void WhenProjectHasStrictTrue()
    {
        new TscInput
        {
            subScenario = "when project has strict true",
            files = WhenProjectHasStrictTrueFiles,
            commandLineArgs = ["--noEmit"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("noEmit");
    }

    [Fact]
    public void DoesNotGoInLoopWhenWatchingWhenNoFilesAreEmitted() => GetTscNoEmitLoopTestCase("", ["-b", "-w", "-verbose"]).RunAndAssert("noEmit");
    [Fact]
    public void DoesNotGoInLoopWhenWatchingWhenNoFilesAreEmittedWithIncremental() => GetTscNoEmitLoopTestCase(" with incremental", ["-b", "-w", "-verbose", "--incremental"]).RunAndAssert("noEmit");

    [Fact]
    public void SyntaxErrorsCommandLine() => GetTscNoEmitAndErrorsTestCases([])[0].RunAndAssert("noEmit");
    [Fact]
    public void SyntaxErrorsCommandLineWithIncremental() => GetTscNoEmitAndErrorsTestCases([])[1].RunAndAssert("noEmit");
    [Fact]
    public void SyntaxErrorsCommandLineWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases([])[2].RunAndAssert("noEmit");
    [Fact]
    public void SemanticErrorsCommandLine() => GetTscNoEmitAndErrorsTestCases([])[3].RunAndAssert("noEmit");
    [Fact]
    public void SemanticErrorsCommandLineWithIncremental() => GetTscNoEmitAndErrorsTestCases([])[4].RunAndAssert("noEmit");
    [Fact]
    public void SemanticErrorsCommandLineWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases([])[5].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsCommandLine() => GetTscNoEmitAndErrorsTestCases([])[6].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsCommandLineWithIncremental() => GetTscNoEmitAndErrorsTestCases([])[7].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsCommandLineWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases([])[8].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithoutDtsEnabledCommandLine() => GetTscNoEmitAndErrorsTestCases([])[9].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithoutDtsEnabledCommandLineWithIncremental() => GetTscNoEmitAndErrorsTestCases([])[10].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithoutDtsEnabledCommandLineWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases([])[11].RunAndAssert("noEmit");

    [Fact]
    public void SyntaxErrorsBuild() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[0].RunAndAssert("noEmit");
    [Fact]
    public void SyntaxErrorsBuildWithIncremental() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[1].RunAndAssert("noEmit");
    [Fact]
    public void SyntaxErrorsBuildWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[2].RunAndAssert("noEmit");
    [Fact]
    public void SemanticErrorsBuild() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[3].RunAndAssert("noEmit");
    [Fact]
    public void SemanticErrorsBuildWithIncremental() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[4].RunAndAssert("noEmit");
    [Fact]
    public void SemanticErrorsBuildWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[5].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsBuild() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[6].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsBuildWithIncremental() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[7].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsBuildWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[8].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithoutDtsEnabledBuild() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[9].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithoutDtsEnabledBuildWithIncremental() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[10].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithoutDtsEnabledBuildWithIncrementalAsModules() => GetTscNoEmitAndErrorsTestCases(["-b", "-v"])[11].RunAndAssert("noEmit");

    [Fact]
    public void ChangesComposite() => GetTscNoEmitChangesTestCases([])[0].RunAndAssert("noEmit");
    [Fact]
    public void ChangesIncrementalDeclaration() => GetTscNoEmitChangesTestCases([])[1].RunAndAssert("noEmit");
    [Fact]
    public void ChangesIncremental() => GetTscNoEmitChangesTestCases([])[2].RunAndAssert("noEmit");
    [Fact]
    public void ChangesWithInitialNoEmitComposite() => GetTscNoEmitChangesTestCases([])[3].RunAndAssert("noEmit");
    [Fact]
    public void ChangesWithInitialNoEmitIncrementalDeclaration() => GetTscNoEmitChangesTestCases([])[4].RunAndAssert("noEmit");
    [Fact]
    public void ChangesWithInitialNoEmitIncremental() => GetTscNoEmitChangesTestCases([])[5].RunAndAssert("noEmit");

    [Fact]
    public void BuildChangesComposite() => GetTscNoEmitChangesTestCases(["-b", "-v"])[0].RunAndAssert("noEmit");
    [Fact]
    public void BuildChangesIncrementalDeclaration() => GetTscNoEmitChangesTestCases(["-b", "-v"])[1].RunAndAssert("noEmit");
    [Fact]
    public void BuildChangesIncremental() => GetTscNoEmitChangesTestCases(["-b", "-v"])[2].RunAndAssert("noEmit");
    [Fact]
    public void BuildChangesWithInitialNoEmitComposite() => GetTscNoEmitChangesTestCases(["-b", "-v"])[3].RunAndAssert("noEmit");
    [Fact]
    public void BuildChangesWithInitialNoEmitIncrementalDeclaration() => GetTscNoEmitChangesTestCases(["-b", "-v"])[4].RunAndAssert("noEmit");
    [Fact]
    public void BuildChangesWithInitialNoEmitIncremental() => GetTscNoEmitChangesTestCases(["-b", "-v"])[5].RunAndAssert("noEmit");

    [Fact]
    public void DtsErrorsWithDeclarationEnableChanges() => GetTscNoEmitDtsChangesTestCases()[0].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithDeclarationEnableChangesWithIncremental() => GetTscNoEmitDtsChangesTestCases()[1].RunAndAssert("noEmit");
    [Fact]
    public void DtsErrorsWithDeclarationEnableChangesWithIncrementalAsModules() => GetTscNoEmitDtsChangesTestCases()[2].RunAndAssert("noEmit");

    [Fact]
    public void DtsErrorsWithDeclarationEnableChangesWithMultipleFiles() => GetTscNoEmitDtsChangesMultiFileErrorsTestCases([])[0].RunAndAssert("noEmit");
    [Fact]
    public void BuildDtsErrorsWithDeclarationEnableChangesWithMultipleFiles() => GetTscNoEmitDtsChangesMultiFileErrorsTestCases(["-b", "-v"])[0].RunAndAssert("noEmit");

    [Fact]
    public void WatchSyntaxErrors() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[0].RunAndAssert("noEmit");
    [Fact]
    public void WatchSyntaxErrorsWithIncremental() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[1].RunAndAssert("noEmit");
    [Fact]
    public void WatchSyntaxErrorsWithIncrementalAsModules() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[2].RunAndAssert("noEmit");
    [Fact]
    public void WatchSemanticErrors() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[3].RunAndAssert("noEmit");
    [Fact]
    public void WatchSemanticErrorsWithIncremental() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[4].RunAndAssert("noEmit");
    [Fact]
    public void WatchSemanticErrorsWithIncrementalAsModules() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[5].RunAndAssert("noEmit");
    [Fact]
    public void WatchDtsErrors() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[6].RunAndAssert("noEmit");
    [Fact]
    public void WatchDtsErrorsWithIncremental() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[7].RunAndAssert("noEmit");
    [Fact]
    public void WatchDtsErrorsWithIncrementalAsModules() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[8].RunAndAssert("noEmit");
    [Fact]
    public void WatchDtsErrorsWithoutDtsEnabled() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[9].RunAndAssert("noEmit");
    [Fact]
    public void WatchDtsErrorsWithoutDtsEnabledWithIncremental() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[10].RunAndAssert("noEmit");
    [Fact]
    public void WatchDtsErrorsWithoutDtsEnabledWithIncrementalAsModules() => GetTscNoEmitAndErrorsWatchTestCases(["-b", "-verbose", "-w"])[11].RunAndAssert("noEmit");
}
