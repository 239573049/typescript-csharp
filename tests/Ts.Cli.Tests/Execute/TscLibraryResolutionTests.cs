// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscLibraryResolution.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscLibraryResolutionTests
{
    // tsc_test.go:2231-2303 — getTscLibraryResolutionFileMap.
    private static Dictionary<string, object> GetTscLibraryResolutionFileMap(bool libReplacement)
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/workspace/projects/project1/utils.d.ts"] = "export const y = 10;",
            ["/home/src/workspace/projects/project1/file.ts"] = "export const file = 10;",
            ["/home/src/workspace/projects/project1/core.d.ts"] = "export const core = 10;",
            ["/home/src/workspace/projects/project1/index.ts"] = "export const x = \"type1\";",
            ["/home/src/workspace/projects/project1/file2.ts"] = StringTestUtil.Dedent("""
                /// <reference lib="webworker"/>
                /// <reference lib="scripthost"/>
                /// <reference lib="es5"/>
"""),
            ["/home/src/workspace/projects/project1/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "composite": true,
                        "typeRoots": ["./typeroot1"],
                        "lib": ["es5", "dom"],
                        "traceResolution": true,
                        "libReplacement": {{(libReplacement ? "true" : "false")}}
                    }
                }
"""),
            ["/home/src/workspace/projects/project1/typeroot1/sometype/index.d.ts"] = "export type TheNum = \"type1\";",
            ["/home/src/workspace/projects/project2/utils.d.ts"] = "export const y = 10;",
            ["/home/src/workspace/projects/project2/index.ts"] = "export const y = 10",
            ["/home/src/workspace/projects/project2/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "composite": true,
                        "lib": ["es5", "dom"],
                        "traceResolution": true,
                        "libReplacement": {{(libReplacement ? "true" : "false")}}
                    }
                }
"""),
            ["/home/src/workspace/projects/project3/utils.d.ts"] = "export const y = 10;",
            ["/home/src/workspace/projects/project3/index.ts"] = "export const z = 10",
            ["/home/src/workspace/projects/project3/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "composite": true,
                        "lib": ["es5", "dom"],
                        "traceResolution": true,
                        "libReplacement": {{(libReplacement ? "true" : "false")}}
                    }
                }
"""),
            ["/home/src/workspace/projects/project4/utils.d.ts"] = "export const y = 10;",
            ["/home/src/workspace/projects/project4/index.ts"] = "export const z = 10",
            ["/home/src/workspace/projects/project4/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "composite": true,
                        "lib": ["esnext", "dom", "webworker"],
                        "traceResolution": true,
                        "libReplacement": {{(libReplacement ? "true" : "false")}}
                    }
                }
"""),
            [TscTestDefaults.GetTestLibPathFor("dom")] = "interface DOMInterface { }",
            [TscTestDefaults.GetTestLibPathFor("webworker")] = "interface WebWorkerInterface { }",
            [TscTestDefaults.GetTestLibPathFor("scripthost")] = "interface ScriptHostInterface { }",
            ["/home/src/workspace/projects/node_modules/@typescript/unlreated/index.d.ts"] = "export const unrelated = 10;",
        };
        if (libReplacement)
        {
            files["/home/src/workspace/projects/node_modules/@typescript/lib-es5/index.d.ts"] = TscTestDefaults.TscDefaultLibContent;
            files["/home/src/workspace/projects/node_modules/@typescript/lib-esnext/index.d.ts"] = TscTestDefaults.TscDefaultLibContent;
            files["/home/src/workspace/projects/node_modules/@typescript/lib-dom/index.d.ts"] = "interface DOMInterface { }";
            files["/home/src/workspace/projects/node_modules/@typescript/lib-webworker/index.d.ts"] = "interface WebWorkerInterface { }";
            files["/home/src/workspace/projects/node_modules/@typescript/lib-scripthost/index.d.ts"] = "interface ScriptHostInterface { }";
        }
        return files;
    }

    // tsc_test.go:2304-2319 — getTscLibResolutionTestCases.
    private static TscInput[] GetTscLibResolutionTestCases(string[] commandLineArgs) =>
    [
        new TscInput
        {
            subScenario = "with config",
            files = GetTscLibraryResolutionFileMap(false),
            cwd = "/home/src/workspace/projects",
            commandLineArgs = commandLineArgs,
        },
        new TscInput
        {
            subScenario = "with config with libReplacement",
            files = GetTscLibraryResolutionFileMap(true),
            cwd = "/home/src/workspace/projects",
            commandLineArgs = commandLineArgs,
        },
    ];

    // tsc_test.go:2320-2342 — getTscLibraryResolutionUnknown.
    private static Dictionary<string, object> GetTscLibraryResolutionUnknown() => new()
    {
        ["/home/src/workspace/projects/project1/utils.d.ts"] = "export const y = 10;",
        ["/home/src/workspace/projects/project1/file.ts"] = "export const file = 10;",
        ["/home/src/workspace/projects/project1/core.d.ts"] = "export const core = 10;",
        ["/home/src/workspace/projects/project1/index.ts"] = "export const x = \"type1\";",
        ["/home/src/workspace/projects/project1/file2.ts"] = StringTestUtil.Dedent("""
            /// <reference lib="webworker2"/>
            /// <reference lib="unknownlib"/>
            /// <reference lib="scripthost"/>
"""),
        ["/home/src/workspace/projects/project1/tsconfig.json"] = StringTestUtil.Dedent("""
        {
            "compilerOptions": {
                "composite": true,
                "traceResolution": true,
                "libReplacement": true
            }
        }
"""),
        [TscTestDefaults.GetTestLibPathFor("webworker")] = "interface WebWorkerInterface { }",
        [TscTestDefaults.GetTestLibPathFor("scripthost")] = "interface ScriptHostInterface { }",
    };

    // tsc_test.go:2344-2346 + 2379-2381 — the three getTscLibResolutionTestCases groups
    // run against the same "libraryResolution" baseline subfolder.
    [Fact]
    public void BuildProjectsVerboseExplainFiles()
        => RunCases(GetTscLibResolutionTestCases(["-b", "project1", "project2", "project3", "project4", "--verbose", "--explainFiles"]));

    [Fact]
    public void ProjectExplainFiles()
        => RunCases(GetTscLibResolutionTestCases(["-p", "project1", "--explainFiles"]));

    [Fact]
    public void BuildProjectsWatchVerboseExplainFiles()
        => RunCases(GetTscLibResolutionTestCases(["-b", "-w", "project1", "project2", "project3", "project4", "--verbose", "--explainFiles"]));

    // tsc_test.go:2347-2353
    [Fact]
    public void UnknownLib()
    {
        new TscInput
        {
            subScenario = "unknown lib",
            files = GetTscLibraryResolutionUnknown(),
            cwd = "/home/src/workspace/projects",
            commandLineArgs = ["-p", "project1", "--explainFiles"],
        }.RunAndAssert("libraryResolution");
    }

    // tsc_test.go:2354-2375
    [Fact]
    public void WhenNoLibToggles()
    {
        new TscInput
        {
            subScenario = "when noLib toggles",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a.d.ts"] = "declare const a = \"hello\";",
                ["/home/src/workspaces/project/b.ts"] = "const b = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "declaration": true,
                            "incremental": true,
                            "lib": ["es6"],
                        },
                    }
"""),
            },
            edits =
            [
                new TscEdit
                {
                    caption = "with --noLib",
                    commandLineArgs = ["--noLib"],
                },
            ],
        }.RunAndAssert("libraryResolution");
    }

    // tsc_test.go:2379-2381 — `for _, test := range testCases { test.run(t, "libraryResolution") }`.
    private static void RunCases(TscInput[] testCases)
    {
        foreach (var testCase in testCases)
        {
            testCase.RunAndAssert("libraryResolution");
        }
    }
}
