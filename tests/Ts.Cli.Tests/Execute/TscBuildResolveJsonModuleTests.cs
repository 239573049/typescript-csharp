// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildResolveJsonModule.

using System;
using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildResolveJsonModuleTests
{
    // tscbuild_test.go:2987-2994
    private sealed class BuildResolveJsonModuleScenario
    {
        internal string subScenario = "";
        internal string tsconfigFiles = "";
        internal string additionalCompilerOptions = "";
        internal bool skipOutdir;
        internal Action<Dictionary<string, object>>? modifyFiles;
        internal TscEdit[]? edits;
    }

    // tscbuild_test.go:2995-3028
    private static Dictionary<string, object> GetFileMap(bool composite, BuildResolveJsonModuleScenario s)
    {
        var outDirStr = "";
        if (!s.skipOutdir)
        {
            outDirStr = "\"outDir\": \"dist\",";
        }
        var files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/solution/project/src/hello.json"] = StringTestUtil.Dedent("""

			{
				"hello": "world"
			}
"""),
            ["/home/src/workspaces/solution/project/src/index.ts"] = StringTestUtil.Dedent("""

				import hello from "./hello.json"
				export default hello.hello
			
"""),
            ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
				{
					"compilerOptions": {
						"composite": {{(composite ? "true" : "false")}},
						"module": "commonjs",
						"resolveJsonModule": true,
						"esModuleInterop": true,
						"allowSyntheticDefaultImports": true,
						{{outDirStr}}
						"skipDefaultLibCheck": true,
						{{s.additionalCompilerOptions}}
					},
					{{s.tsconfigFiles}}
				}
"""),
        };
        s.modifyFiles?.Invoke(files);
        return files;
    }

    // tscbuild_test.go:3029-3051
    private static List<TscInput> GetTestCases(BuildResolveJsonModuleScenario[] scenarios)
    {
        var testCases = new List<TscInput>(scenarios.Length * 2);
        foreach (var s in scenarios)
        {
            testCases.Add(new TscInput
            {
                subScenario = s.subScenario,
                files = GetFileMap(true, s),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "project", "--v", "--explainFiles", "--listEmittedFiles"],
                edits = s.edits,
            });
            testCases.Add(new TscInput
            {
                subScenario = s.subScenario + " non-composite",
                files = GetFileMap(false, s),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "project", "--v", "--explainFiles", "--listEmittedFiles"],
                edits = s.edits,
            });
        }
        return testCases;
    }

    // tscbuild_test.go:2985-3183 — TestBuildResolveJsonModule.
    [Fact]
    public void BuildResolveJsonModule()
    {
        // tscbuild_test.go:3052-3120
        BuildResolveJsonModuleScenario[] scenarios =
        [
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include only",
                tsconfigFiles = "\"include\": [ \"src/**/*\" ],",
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include only without outDir",
                tsconfigFiles = "\"include\": [ \"src/**/*\" ],",
                skipOutdir = true,
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include only with json not in rootDir",
                tsconfigFiles = "\"include\": [ \"src/**/*\" ],",
                additionalCompilerOptions = "\"rootDir\": \"src\",",
                modifyFiles = files =>
                {
                    var text = files["/home/src/workspaces/solution/project/src/hello.json"];
                    files.Remove("/home/src/workspaces/solution/project/src/hello.json");
                    files["/home/src/workspaces/solution/project/hello.json"] = text;
                    text = files["/home/src/workspaces/solution/project/src/index.ts"];
                    files["/home/src/workspaces/solution/project/src/index.ts"] = ((string)text).Replace("./hello.json", "../hello.json", StringComparison.Ordinal);
                },
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include only with json without rootDir but outside configDirectory",
                tsconfigFiles = "\"include\": [ \"src/**/*\" ],",
                modifyFiles = files =>
                {
                    var text = files["/home/src/workspaces/solution/project/src/hello.json"];
                    files.Remove("/home/src/workspaces/solution/project/src/hello.json");
                    files["/home/src/workspaces/solution/hello.json"] = text;
                    text = files["/home/src/workspaces/solution/project/src/index.ts"];
                    files["/home/src/workspaces/solution/project/src/index.ts"] = ((string)text).Replace("./hello.json", "../../hello.json", StringComparison.Ordinal);
                },
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include of json along with other include",
                tsconfigFiles = "\"include\": [ \"src/**/*\", \"src/**/*.json\" ],",
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include of json along with other include and file name matches ts file",
                tsconfigFiles = "\"include\": [ \"src/**/*\", \"src/**/*.json\" ],",
                modifyFiles = files =>
                {
                    var text = files["/home/src/workspaces/solution/project/src/hello.json"];
                    files.Remove("/home/src/workspaces/solution/project/src/hello.json");
                    files["/home/src/workspaces/solution/project/src/index.json"] = text;
                    text = files["/home/src/workspaces/solution/project/src/index.ts"];
                    files["/home/src/workspaces/solution/project/src/index.ts"] = ((string)text).Replace("./hello.json", "./index.json", StringComparison.Ordinal);
                },
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "files containing json file",
                tsconfigFiles = "\"files\": [ \"src/index.ts\", \"src/hello.json\", ],",
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "include and files",
                tsconfigFiles = "\"files\": [ \"src/hello.json\" ], \"include\": [ \"src/**/*\" ],",
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "sourcemap",
                tsconfigFiles = "\"files\": [ \"src/index.ts\", \"src/hello.json\", ],",
                additionalCompilerOptions = "\"sourceMap\": true,",
                edits = TscEdit.NoChangeOnlyEdit,
            },
            new BuildResolveJsonModuleScenario
            {
                subScenario = "without outDir",
                tsconfigFiles = "\"files\": [ \"src/index.ts\", \"src/hello.json\", ],",
                skipOutdir = true,
                edits = TscEdit.NoChangeOnlyEdit,
            },
        ];

        var testCases = new List<TscInput>();
        testCases.AddRange(GetTestCases(scenarios));
        // tscbuild_test.go:3123-3177
        testCases.Add(new TscInput
        {
            subScenario = "importing json module from project reference",
            files =             new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/project/strings/foo.json"] = StringTestUtil.Dedent("""

						{
							"foo": "bar baz"
						}
					
"""),
                ["/home/src/workspaces/solution/project/strings/tsconfig.json"] = StringTestUtil.Dedent("""

						{
							"extends": "../tsconfig.json",
							"include": ["foo.json"],
							"references": [],
						}
					
"""),
                ["/home/src/workspaces/solution/project/main/index.ts"] = StringTestUtil.Dedent("""

						import { foo } from '../strings/foo.json';
						console.log(foo);
					
"""),
                ["/home/src/workspaces/solution/project/main/tsconfig.json"] = StringTestUtil.Dedent("""

						{
							"extends": "../tsconfig.json",
							"include": [
								"./**/*.ts",
							],
							"references": [{
								"path": "../strings/tsconfig.json",
							}],
						}
					
"""),
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""

						{
							"compilerOptions": {
								"target": "es5",
								"module": "commonjs",
								"rootDir": "./",
								"composite": true,
								"resolveJsonModule": true,
								"strict": true,
								"esModuleInterop": true,
							},
							"references": [
								{ "path": "./strings/tsconfig.json" },
								{ "path": "./main/tsconfig.json" },
							],
							"files": [],
						}
					
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "project", "--verbose", "--explainFiles"],
            edits = TscEdit.NoChangeOnlyEdit,
        });

        // tscbuild_test.go:3180-3182
        foreach (var test in testCases)
        {
            test.RunAndAssert("resolveJsonModule");
        }
    }
}
