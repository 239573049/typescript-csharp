// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildSolutionProject.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildSolutionProjectTests
{
    // tscbuild_test.go:4322-4484 — TestBuildSolutionProject.
    [Fact]
    public void BuildSolutionProject()
    {
        var testCases = new List<TscInput>
        {
            new TscInput
            {
                subScenario = "verify that subsequent builds after initial build doesnt build anything",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/solution/src/folder/index.ts"] = "export const x = 10;",
                    ["/home/src/workspaces/solution/src/folder/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "files": ["index.ts"],
                        "compilerOptions": {
                            "composite": true
                        }
                    }
                
"""),
                    ["/home/src/workspaces/solution/src/folder2/index.ts"] = "export const x = 10;",
                    ["/home/src/workspaces/solution/src/folder2/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "files": ["index.ts"],
                        "compilerOptions": {
                            "composite": true
                        }
                    }
                
"""),
                    ["/home/src/workspaces/solution/src/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "files": [],
                        "compilerOptions": {
                            "composite": true
                        },
						"references": [
							{ "path": "./folder" },
							{ "path": "./folder2" },
						]
                }
"""),
                    ["/home/src/workspaces/solution/tests/index.ts"] = "export const x = 10;",
                    ["/home/src/workspaces/solution/tests/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "files": ["index.ts"],
                        "compilerOptions": {
                            "composite": true
                        },
                        "references": [
                            { "path": "../src" }
                        ]
                    }
                
"""),
                    ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "files": [],
                        "compilerOptions": {
                            "composite": true
                        },
                        "references": [
                            { "path": "./src" },
                            { "path": "./tests" }
                        ]
                    }
                
"""),
                },
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "--v"],
                edits = TscEdit.NoChangeOnlyEdit,
            },
            new TscInput
            {
                subScenario = "when solution is referenced indirectly",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/solution/project1/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "compilerOptions": { "composite": true },
                        "references": []
                    }
                
"""),
                    ["/home/src/workspaces/solution/project2/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "compilerOptions": { "composite": true },
                        "references": []
                    }
                
"""),
                    ["/home/src/workspaces/solution/project2/src/b.ts"] = "export const b = 10;",
                    ["/home/src/workspaces/solution/project3/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "compilerOptions": { "composite": true },
                        "references": [
							{ "path": "../project1" },
							{ "path": "../project2" }
						]
                    }
                
"""),
                    ["/home/src/workspaces/solution/project3/src/c.ts"] = "export const c = 10;",
                    ["/home/src/workspaces/solution/project4/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "compilerOptions": { "composite": true },
                        "references": [{ "path": "../project3" }]
                    }
                
"""),
                    ["/home/src/workspaces/solution/project4/src/d.ts"] = "export const d = 10;",
                },
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "project4", "--verbose", "--explainFiles"],
                edits = new TscEdit[]
                {
                    new TscEdit
                    {
                        caption = "modify project3 file",
                        edit = sys =>
                        {
                            sys.ReplaceFileText("/home/src/workspaces/solution/project3/src/c.ts", "c = ", "cc = ");
                        },
                    },
                },
            },
            new TscInput
            {
                subScenario = "has empty files diagnostic when files is empty and no references are provided",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/solution/no-references/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "references": [],
                        "files": [],
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "forceConsistentCasingInFileNames": true,
                            "skipDefaultLibCheck": true,
                        },
                    }
"""),
                },
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "no-references"],
            },
            new TscInput
            {
                subScenario = "does not have empty files diagnostic when files is empty and references are provided",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/solution/core/index.ts"] = "export function multiply(a: number, b: number) { return a * b; }",
                    ["/home/src/workspaces/solution/core/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "declarationMap": true,
                            "skipDefaultLibCheck": true,
                        },
                    }
"""),
                    ["/home/src/workspaces/solution/with-references/tsconfig.json"] = StringTestUtil.Dedent("""

                    {
                        "references": [
                            { "path": "../core" },
                        ],
                        "files": [],
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "forceConsistentCasingInFileNames": true,
                            "skipDefaultLibCheck": true,
                        },
                    }
"""),
                },
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "with-references"],
            },
        };

        foreach (var test in testCases)
        {
            test.RunAndAssert("solution");
        }
    }
}
