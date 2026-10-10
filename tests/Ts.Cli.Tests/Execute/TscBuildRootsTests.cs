// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildRoots.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildRootsTests
{
    // tscbuild_test.go:3187-3241 — getBuildRootsFromProjectReferencedProjectFileMap.
    private static Dictionary<string, object> GetBuildRootsFromProjectReferencedProjectFileMap(bool serverFirst)
    {
        var include = Ts.Core.CoreUtil.IfElse(serverFirst, "\"src/**/*.ts\", \"../shared/src/**/*.ts\"", "\"../shared/src/**/*.ts\", \"src/**/*.ts\"");
        return new Dictionary<string, object>
        {
            ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""

			{
				"compilerOptions": {
					"composite": true,
				},
				"references": [
					{ "path": "projects/server" },
					{ "path": "projects/shared" },
				],
			}
"""),
            ["/home/src/workspaces/solution/projects/shared/src/myClass.ts"] = "export class MyClass { }",
            ["/home/src/workspaces/solution/projects/shared/src/logging.ts"] = StringTestUtil.Dedent("""

				export function log(str: string) {
					console.log(str);
				}
			
"""),
            ["/home/src/workspaces/solution/projects/shared/src/random.ts"] = StringTestUtil.Dedent("""

				export function randomFn(str: string) {
					console.log(str);
				}
			
"""),
            ["/home/src/workspaces/solution/projects/shared/tsconfig.json"] = StringTestUtil.Dedent("""

			{
				"extends": "../../tsconfig.json",
				"compilerOptions": {
					"outDir": "./dist",
				},
				"include": ["src/**/*.ts"],
			}
"""),
            ["/home/src/workspaces/solution/projects/server/src/server.ts"] = StringTestUtil.Dedent("""

				import { MyClass } from ':shared/myClass.js';
				console.log('Hello, world!');
			
"""),
            ["/home/src/workspaces/solution/projects/server/tsconfig.json"] = StringTestUtil.Dedent($$"""

			{
				"extends": "../../tsconfig.json",
				"compilerOptions": {
					"rootDir": "..",
					"outDir": "./dist",
					"paths": {
						":shared/*": ["./src/../../shared/src/*"],
					},
				},
				"include": [ {{include}} ],
				"references": [
					{ "path": "../shared" },
				],
			}
"""),
        };
    }

    // tscbuild_test.go:3242-3262 — getBuildRootsFromProjectReferencedProjectTestEdits.
    private static TscEdit[] GetBuildRootsFromProjectReferencedProjectTestEdits()
    {
        return new TscEdit[]
        {
            TscEdit.NoChange,
            new TscEdit
            {
                caption = "edit logging file",
                edit = sys =>
                {
                    sys.AppendFileText("/home/src/workspaces/solution/projects/shared/src/logging.ts", "export const x = 10;");
                },
            },
            TscEdit.NoChange,
            new TscEdit
            {
                caption = "delete random file",
                edit = sys =>
                {
                    sys.RemoveNoError("/home/src/workspaces/solution/projects/shared/src/random.ts");
                },
            },
            TscEdit.NoChange,
        };
    }

    // tscbuild_test.go:3185-3408 — TestBuildRoots.
    [Fact]
    public void BuildRoots()
    {
        var testCases = new List<TscInput>
        {
            new TscInput
            {
                subScenario = "when two root files are consecutive",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/file1.ts"] = "export const x = \"hello\";",
                    ["/home/src/workspaces/project/file2.ts"] = "export const y = \"world\";",
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "compilerOptions": { "composite": true },
                    "include": ["*.ts"],
                }
"""),
                },
                commandLineArgs = ["--b", "-v"],
                edits = new TscEdit[]
                {
                    new TscEdit
                    {
                        caption = "delete file1",
                        edit = sys =>
                        {
                            sys.RemoveNoError("/home/src/workspaces/project/file1.ts");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.js");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.d.ts");
                        },
                    },
                },
            },
            new TscInput
            {
                subScenario = "when multiple root files are consecutive",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/file1.ts"] = "export const x = \"hello\";",
                    ["/home/src/workspaces/project/file2.ts"] = "export const y = \"world\";",
                    ["/home/src/workspaces/project/file3.ts"] = "export const y = \"world\";",
                    ["/home/src/workspaces/project/file4.ts"] = "export const y = \"world\";",
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "compilerOptions": { "composite": true },
                    "include": ["*.ts"],
                }
"""),
                },
                commandLineArgs = ["--b", "-v"],
                edits = new TscEdit[]
                {
                    new TscEdit
                    {
                        caption = "delete file1",
                        edit = sys =>
                        {
                            sys.RemoveNoError("/home/src/workspaces/project/file1.ts");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.js");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.d.ts");
                        },
                    },
                },
            },
            new TscInput
            {
                subScenario = "when files are not consecutive",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/file1.ts"] =    "export const x = \"hello\";",
                    ["/home/src/workspaces/project/random.d.ts"] = "export const random = \"world\";",
                    ["/home/src/workspaces/project/file2.ts"] = StringTestUtil.Dedent("""

                    import { random } from "./random";
                    export const y = "world";
                
"""),
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "compilerOptions": { "composite": true },
                    "include": ["file*.ts"],
                }
"""),
                },
                commandLineArgs = ["--b", "-v"],
                edits = new TscEdit[]
                {
                    new TscEdit
                    {
                        caption = "delete file1",
                        edit = sys =>
                        {
                            sys.RemoveNoError("/home/src/workspaces/project/file1.ts");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.js");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.d.ts");
                        },
                    },
                },
            },
            new TscInput
            {
                subScenario = "when consecutive and non consecutive are mixed",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/file1.ts"] =    "export const x = \"hello\";",
                    ["/home/src/workspaces/project/file2.ts"] =    "export const y = \"world\";",
                    ["/home/src/workspaces/project/random.d.ts"] = "export const random = \"hello\";",
                    ["/home/src/workspaces/project/nonconsecutive.ts"] = StringTestUtil.Dedent("""

                import { random } from "./random";
					export const nonConsecutive = "hello";
				
"""),
                    ["/home/src/workspaces/project/random1.d.ts"] = "export const random = \"hello\";",
                    ["/home/src/workspaces/project/asArray1.ts"] = StringTestUtil.Dedent("""

					import { random } from "./random1";
					export const x = "hello";
				
"""),
                    ["/home/src/workspaces/project/asArray2.ts"] =  "export const x = \"hello\";",
                    ["/home/src/workspaces/project/asArray3.ts"] =  "export const x = \"hello\";",
                    ["/home/src/workspaces/project/random2.d.ts"] = "export const random = \"hello\";",
                    ["/home/src/workspaces/project/anotherNonConsecutive.ts"] = StringTestUtil.Dedent("""

					import { random } from "./random2";
					export const nonConsecutive = "hello";
				
"""),
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "compilerOptions": { "composite": true },
                    "include": ["file*.ts", "nonconsecutive*.ts", "asArray*.ts", "anotherNonConsecutive.ts"],
                }
"""),
                },
                commandLineArgs = ["--b", "-v"],
                edits = new TscEdit[]
                {
                    new TscEdit
                    {
                        caption = "delete file1",
                        edit = sys =>
                        {
                            sys.RemoveNoError("/home/src/workspaces/project/file1.ts");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.js");
                            sys.RemoveNoError("/home/src/workspaces/project/file1.d.ts");
                        },
                    },
                },
            },
            new TscInput
            {
                subScenario = "when root file is from referenced project",
                files = GetBuildRootsFromProjectReferencedProjectFileMap(true),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "projects/server", "-v", "--traceResolution", "--explainFiles"],
                edits = GetBuildRootsFromProjectReferencedProjectTestEdits(),
            },
            new TscInput
            {
                subScenario = "when root file is from referenced project and shared is first",
                files = GetBuildRootsFromProjectReferencedProjectFileMap(false),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "projects/server", "-v", "--traceResolution", "--explainFiles"],
                edits = GetBuildRootsFromProjectReferencedProjectTestEdits(),
            },
            new TscInput
            {
                subScenario = "when root file is from referenced project",
                files = GetBuildRootsFromProjectReferencedProjectFileMap(true),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "-w", "projects/server", "-v", "--traceResolution", "--explainFiles"],
                edits = GetBuildRootsFromProjectReferencedProjectTestEdits(),
            },
            new TscInput
            {
                subScenario = "when root file is from referenced project and shared is first",
                files = GetBuildRootsFromProjectReferencedProjectFileMap(false),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "-w", "projects/server", "-v", "--traceResolution", "--explainFiles"],
                edits = GetBuildRootsFromProjectReferencedProjectTestEdits(),
            },
        };

        foreach (var test in testCases)
        {
            test.RunAndAssert("roots");
        }
    }
}
