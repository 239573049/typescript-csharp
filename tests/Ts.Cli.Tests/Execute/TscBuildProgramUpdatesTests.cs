// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildProgramUpdates.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildProgramUpdatesTests
{
    // tscbuild_test.go:2158-2665 — TestBuildProgramUpdates.
    [Fact]
    public void BuildProgramUpdates()
    {
        var testCases = new List<TscInput>
        {
            // tscbuild_test.go:2161-2210
            new TscInput
            {
                subScenario = "when referenced project change introduces error in the down stream project and then fixes it",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/sample1/Library/tsconfig.json"] = StringTestUtil.Dedent("""

				{ 
					"compilerOptions": {
						"composite": true
					}
				}
"""),
                    ["/user/username/projects/sample1/Library/library.ts"] = StringTestUtil.Dedent("""

					interface SomeObject
					{
						message: string;
					}

					export function createSomeObject(): SomeObject
					{
						return {
							message: "new Object"
						};
					}
				
"""),
                    ["/user/username/projects/sample1/App/tsconfig.json"] = StringTestUtil.Dedent("""

				{ 
					"references": [{ "path": "../Library" }]
				}
"""),
                    ["/user/username/projects/sample1/App/app.ts"] = StringTestUtil.Dedent("""

					import { createSomeObject } from "../Library/library";
					createSomeObject().message;
				
"""),
                },
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["-b", "-w", "App"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Introduce error",
                        // Change message in library to message2
                        edit = sys => sys.ReplaceFileTextAll("/user/username/projects/sample1/Library/library.ts", "message", "message2"),
                    },
                    new TscEdit
                    {
                        caption = "Fix error",
                        // Revert library changes
                        edit = sys => sys.ReplaceFileTextAll("/user/username/projects/sample1/Library/library.ts", "message2", "message"),
                    },
                ],
            },
            // tscbuild_test.go:2211-2238
            new TscInput
            {
                subScenario = "declarationEmitErrors when fixing error files all files are emitted",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/solution/app/fileWithError.ts"] = StringTestUtil.Dedent("""

					export var myClassWithError = class {
						tags() { }
						private p = 12
					};
				
"""),
                    ["/user/username/projects/solution/app/fileWithoutError.ts"] = "export class myClass { }",
                    ["/user/username/projects/solution/app/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true
					}
				}
"""),
                },
                cwd = "/user/username/projects/solution",
                commandLineArgs = ["-b", "-w", "app"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Fix error",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/solution/app/fileWithError.ts", "private p = 12", ""),
                    },
                ],
            },
            // tscbuild_test.go:2239-2266
            new TscInput
            {
                subScenario = "declarationEmitErrors when file with no error changes",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/solution/app/fileWithError.ts"] = StringTestUtil.Dedent("""

					export var myClassWithError = class {
						tags() { }
						private p = 12
					};
				
"""),
                    ["/user/username/projects/solution/app/fileWithoutError.ts"] = "export class myClass { }",
                    ["/user/username/projects/solution/app/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true
					}
				}
"""),
                },
                cwd = "/user/username/projects/solution",
                commandLineArgs = ["-b", "-w", "app"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Change fileWithoutError",
                        edit = sys => sys.ReplaceFileTextAll("/user/username/projects/solution/app/fileWithoutError.ts", "myClass", "myClass2"),
                    },
                ],
            },
            // tscbuild_test.go:2267-2305
            new TscInput
            {
                subScenario = "declarationEmitErrors introduceError when fixing errors only changed file is emitted",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/solution/app/fileWithError.ts"] = StringTestUtil.Dedent("""

					export var myClassWithError = class {
						tags() { }
						
					};
				
"""),
                    ["/user/username/projects/solution/app/fileWithoutError.ts"] = "export class myClass { }",
                    ["/user/username/projects/solution/app/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true
					}
				}
"""),
                },
                cwd = "/user/username/projects/solution",
                commandLineArgs = ["-b", "-w", "app"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Introduce error",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/solution/app/fileWithError.ts", StringTestUtil.Dedent("""
									export var myClassWithError = class {
										tags() { }
										private p = 12
									};
""")),
                    },
                    new TscEdit
                    {
                        caption = "Fix error",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/solution/app/fileWithError.ts", "private p = 12", ""),
                    },
                ],
            },
            // tscbuild_test.go:2306-2344
            new TscInput
            {
                subScenario = "declarationEmitErrors introduceError when file with no error changes",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/solution/app/fileWithError.ts"] = StringTestUtil.Dedent("""

					export var myClassWithError = class {
						tags() { }
						
					};
				
"""),
                    ["/user/username/projects/solution/app/fileWithoutError.ts"] = "export class myClass { }",
                    ["/user/username/projects/solution/app/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true
					}
				}
"""),
                },
                cwd = "/user/username/projects/solution",
                commandLineArgs = ["-b", "-w", "app"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Introduce error",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/solution/app/fileWithError.ts", StringTestUtil.Dedent("""
									export var myClassWithError = class {
										tags() { }
										private p = 12
									};
""")),
                    },
                    new TscEdit
                    {
                        caption = "Change fileWithoutError",
                        edit = sys => sys.ReplaceFileTextAll("/user/username/projects/solution/app/fileWithoutError.ts", "myClass", "myClass2"),
                    },
                ],
            },
            // tscbuild_test.go:2345-2375
            new TscInput
            {
                subScenario = "works when noUnusedParameters changes to false",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/myproject/index.ts"] = "const fn = (a: string, b: string) => b;",
                    ["/user/username/projects/myproject/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"noUnusedParameters": true,
					},
				}
"""),
                },
                cwd = "/user/username/projects/myproject",
                commandLineArgs = ["-b", "-w"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Change tsconfig to set noUnusedParameters to false",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/myproject/tsconfig.json", StringTestUtil.Dedent("""
									{
										"compilerOptions": {
											"noUnusedParameters": false,
										},
									}
""")),
                    },
                ],
            },
            // tscbuild_test.go:2376-2508
            new TscInput
            {
                subScenario = "works with extended source files",
                cwd = "/user/username/projects/project",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/project/commonFile1.ts"] = "let x = 1",
                    ["/user/username/projects/project/commonFile2.ts"] = "let y = 1",
                    ["/user/username/projects/project/alpha.tsconfig.json"] = "{}",
                    ["/user/username/projects/project/project1.tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"extends": "./alpha.tsconfig.json",
						"compilerOptions": {
							"composite": true,
						},
						"files": ["commonFile1.ts", "commonFile2.ts"],
					}
				
"""),
                    ["/user/username/projects/project/bravo.tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"extends": "./alpha.tsconfig.json",
					}
				
"""),
                    ["/user/username/projects/project/other.ts"] = "let z = 0;",
                    ["/user/username/projects/project/project2.tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"extends": "./bravo.tsconfig.json",
						"compilerOptions": {
							"composite": true,
						},
						"files": ["other.ts"],
					}
				
"""),
                    ["/user/username/projects/project/other2.ts"] = "let k = 0;",
                    ["/user/username/projects/project/extendsConfig1.tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"composite": true,
						},
					}
				
"""),
                    ["/user/username/projects/project/extendsConfig2.tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"strictNullChecks": false,
						},
					}
				
"""),
                    ["/user/username/projects/project/extendsConfig3.tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"noImplicitAny": true,
						},
					}
				
"""),
                    ["/user/username/projects/project/project3.tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "extends": [
                        "./extendsConfig1.tsconfig.json",
                        "./extendsConfig2.tsconfig.json",
                        "./extendsConfig3.tsconfig.json",
                    ],
                    "compilerOptions": {
                        "composite": false,
                    },
                    "files": ["other2.ts"],
                }
"""),
                },
                commandLineArgs = ["-b", "-w", "-v", "project1.tsconfig.json", "project2.tsconfig.json", "project3.tsconfig.json"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Modify alpha config",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/alpha.tsconfig.json", StringTestUtil.Dedent("""
								{
									"compilerOptions": {
										"strict": true
									}
								}
""")),
                    },
                    new TscEdit
                    {
                        caption = "change bravo config",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/bravo.tsconfig.json", StringTestUtil.Dedent("""
								{
									"extends": "./alpha.tsconfig.json",
									"compilerOptions": { "strict": false }
								}
""")),
                    },
                    new TscEdit
                    {
                        caption = "project 2 extends alpha",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/project2.tsconfig.json", StringTestUtil.Dedent("""
								{
									"extends": "./alpha.tsconfig.json",
									"files": ["other.ts"]
								}
""")),
                    },
                    new TscEdit
                    {
                        caption = "update aplha config",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/alpha.tsconfig.json", "{}"),
                    },
                    new TscEdit
                    {
                        caption = "Modify extendsConfigFile2",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/extendsConfig2.tsconfig.json", StringTestUtil.Dedent("""
								{
									"compilerOptions": { "strictNullChecks": true }
								}
""")),
                    },
                    new TscEdit
                    {
                        caption = "Modify project 3",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/project3.tsconfig.json", StringTestUtil.Dedent("""
								{
									"extends": ["./extendsConfig1.tsconfig.json", "./extendsConfig2.tsconfig.json"],
									"compilerOptions": { "composite": false },
									"files": ["other2.ts"],
								}
""")),
                    },
                    new TscEdit
                    {
                        caption = "Delete extendedConfigFile2 and report error",
                        edit = sys => sys.RemoveNoError("/user/username/projects/project/extendsConfig2.tsconfig.json"),
                    },
                ],
            },
            // tscbuild_test.go:2509-2574
            new TscInput
            {
                subScenario = "works correctly when project with extended config is removed",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/project/commonFile1.ts"] = "let x = 1",
                    ["/user/username/projects/project/commonFile2.ts"] = "let y = 1",
                    ["/user/username/projects/project/alpha.tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "compilerOptions": {
                        "strict": true,
                    },
                }
"""),
                    ["/user/username/projects/project/project1.tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "extends": "./alpha.tsconfig.json",
                    "compilerOptions": {
                        "composite": true,
                    },
                    "files": ["commonFile1.ts", "commonFile2.ts"],
                }
"""),
                    ["/user/username/projects/project/bravo.tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "compilerOptions": {
                        "strict": true,
                    },
                }
"""),
                    ["/user/username/projects/project/other.ts"] = "let z = 0;",
                    ["/user/username/projects/project/project2.tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "extends": "./bravo.tsconfig.json",
                    "compilerOptions": {
                        "composite": true,
                    },
                    "files": ["other.ts"],
                }
"""),
                    ["/user/username/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""

				{
                    "references": [
                        {
                            "path": "./project1.tsconfig.json",
                        },
                        {
                            "path": "./project2.tsconfig.json",
                        },
                    ],
                    "files": [],
                }
"""),
                },
                cwd = "/user/username/projects/project",
                commandLineArgs = ["-b", "-w", "-v"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Remove project2 from base config",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/project/tsconfig.json", StringTestUtil.Dedent("""
								{
									"references": [
										{
											"path": "./project1.tsconfig.json",
										},
									],
									"files": [],
								}
""")),
                    },
                ],
            },
            // tscbuild_test.go:2575-2584
            new TscInput
            {
                subScenario = "tsbuildinfo has error",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/project/main.ts"] = "export const x = 10;",
                    ["/user/username/projects/project/tsconfig.json"] = "{}",
                    ["/user/username/projects/project/tsconfig.tsbuildinfo"] = "Some random string",
                },
                cwd = "/user/username/projects/project",
                commandLineArgs = ["--b", "-i", "-w"],
            },
            // tscbuild_test.go:2585-2601
            new TscInput
            {
                subScenario = "tsbuildinfo has fewer fileInfos than fileNames",
                files =
                new Dictionary<string, object>
                {
                    ["/user/username/projects/project/src/a.ts"] = "export const a = 1;",
                    ["/user/username/projects/project/src/b.ts"] = "export const b = 2;",
                    ["/user/username/projects/project/tsconfig.json"] = "{\"compilerOptions\":{\"composite\":true,\"outDir\":\"dist\"},\"files\":[\"src/a.ts\",\"src/b.ts\"]}",
                    ["/user/username/projects/project/dist/tsconfig.tsbuildinfo"] = """
{
					"version": "FakeTSVersion",
					"fileNames": ["lib.es2025.full.d.ts", "../src/a.ts", "../src/b.ts"],
					"fileInfos": ["abc123"],
					"options": {"composite": true, "outDir": "./"},
					"root": [2, 3]
				}
""",
                },
                cwd = "/user/username/projects/project",
                commandLineArgs = ["--b", "-v"],
            },
            // tscbuild_test.go:2602-2629
            new TscInput
            {
                subScenario = "when root is source from project reference",
                files =
                new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/lib/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"composite": true,
							"outDir": "./dist"
						}
					}
"""),
                    ["/home/src/workspaces/project/lib/foo.ts"] = "export const FOO: string = 'THEFOOEXPORT';",
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"references": [ { "path": "./lib" } ]
					}
"""),
                    ["/home/src/workspaces/project/index.ts"] = "import { FOO } from \"./lib/foo\";",
                },
                commandLineArgs = ["--b"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "dts doesnt change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/project/lib/foo.ts", "const Bar = 10;"),
                    },
                ],
                cwd = "/home/src/workspaces/project",
            },
            // tscbuild_test.go:2630-2660
            new TscInput
            {
                subScenario = "when root is source from project reference with composite",
                files =
                new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/lib/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"composite": true,
							"outDir": "./dist"
						}
					}
"""),
                    ["/home/src/workspaces/project/lib/foo.ts"] = "export const FOO: string = 'THEFOOEXPORT';",
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"composite": true,
						},
						"references": [ { "path": "./lib" } ]
					}
"""),
                    ["/home/src/workspaces/project/index.ts"] = "import { FOO } from \"./lib/foo\";",
                },
                commandLineArgs = ["--b"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "dts doesnt change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/project/lib/foo.ts", "const Bar = 10;"),
                    },
                ],
                cwd = "/home/src/workspaces/project",
            },
        };

        // tscbuild_test.go:2662-2664
        foreach (var test in testCases)
        {
            test.RunAndAssert("programUpdates");
        }
    }
}
