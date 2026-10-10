// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildProjectReferenceWithRootDirInParent.

using System;
using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildProjectReferenceWithRootDirInParentTests
{
    // tscbuild_test.go:2766-2808 — getBuildProjectReferenceWithRootDirInParentFileMap.
    private static Dictionary<string, object> GetFileMap(Action<Dictionary<string, object>>? modify)
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/solution/src/main/a.ts"] = StringTestUtil.Dedent("""

				import { b } from './b';
				const a = b;
			
"""),
            ["/home/src/workspaces/solution/src/main/b.ts"] = StringTestUtil.Dedent("""

				export const b = 0;
			
"""),
            ["/home/src/workspaces/solution/src/main/tsconfig.json"] = StringTestUtil.Dedent("""

			{
				"extends": "../../tsconfig.base.json",
				"references": [
					{ "path": "../other" },
				],
			}
"""),
            ["/home/src/workspaces/solution/src/other/other.ts"] = StringTestUtil.Dedent("""

				export const Other = 0;
			
"""),
            ["/home/src/workspaces/solution/src/other/tsconfig.json"] = StringTestUtil.Dedent("""

			{
				"extends": "../../tsconfig.base.json",
			}
			
"""),
            ["/home/src/workspaces/solution/tsconfig.base.json"] = StringTestUtil.Dedent("""

			{
				"compilerOptions": {
					"composite": true,
					"declaration": true,
					"rootDir": "./src/",
					"outDir": "./dist/",
					"skipDefaultLibCheck": true,
				},
				"exclude": [
					"node_modules",
				],
			}
"""),
        };
        modify?.Invoke(files);
        return files;
    }

    // tscbuild_test.go:2764-2914 — TestBuildProjectReferenceWithRootDirInParent.
    [Fact]
    public void BuildProjectReferenceWithRootDirInParent()
    {
        TscInput[] testCases =
        [
            // tscbuild_test.go:2810-2815
            new TscInput
            {
                subScenario = "builds correctly",
                files = GetFileMap(null),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "src/main", "/home/src/workspaces/solution/src/other"],
            },
            // tscbuild_test.go:2816-2826
            new TscInput
            {
                subScenario = "reports error for same tsbuildinfo file because no rootDir in the base",
                files = GetFileMap(files =>
                {
                    var text = (string)files["/home/src/workspaces/solution/tsconfig.base.json"];
                    // Go strings.Replace(..., n: 1); the pattern occurs once.
                    files["/home/src/workspaces/solution/tsconfig.base.json"] = text.Replace("\"rootDir\": \"./src/\",", "", StringComparison.Ordinal);
                }),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "src/main", "--verbose"],
            },
            // tscbuild_test.go:2827-2845
            new TscInput
            {
                subScenario = "reports error for same tsbuildinfo file",
                files = GetFileMap(files =>
                {
                    files["/home/src/workspaces/solution/src/main/tsconfig.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "composite": true, "outDir": "../../dist/" },
								"references": [{ "path": "../other" }]
							}
""");
                    files["/home/src/workspaces/solution/src/other/tsconfig.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "composite": true, "outDir": "../../dist/" },
							}
""");
                }),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "src/main", "--verbose"],
                edits = TscEdit.NoChangeOnlyEdit,
            },
            // tscbuild_test.go:2846-2863
            new TscInput
            {
                subScenario = "reports error for same tsbuildinfo file without incremental",
                files = GetFileMap(files =>
                {
                    files["/home/src/workspaces/solution/src/main/tsconfig.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "outDir": "../../dist/" },
								"references": [{ "path": "../other" }]
							}
""");
                    files["/home/src/workspaces/solution/src/other/tsconfig.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "composite": true, "outDir": "../../dist/" },
							}
""");
                }),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "src/main", "--verbose"],
            },
            // tscbuild_test.go:2864-2887
            new TscInput
            {
                subScenario = "reports error for same tsbuildinfo file without incremental with tsc",
                files = GetFileMap(files =>
                {
                    files["/home/src/workspaces/solution/src/main/tsconfig.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "outDir": "../../dist/" },
								"references": [{ "path": "../other" }]
							}
""");
                    files["/home/src/workspaces/solution/src/other/tsconfig.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "composite": true, "outDir": "../../dist/" },
							}
""");
                }),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "src/other", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Running tsc on main",
                        commandLineArgs = ["-p", "src/main"],
                    },
                ],
            },
            // tscbuild_test.go:2888-2908
            new TscInput
            {
                subScenario = "reports no error when tsbuildinfo differ",
                files = GetFileMap(files =>
                {
                    files.Remove("/home/src/workspaces/solution/src/main/tsconfig.json");
                    files.Remove("/home/src/workspaces/solution/src/other/tsconfig.json");
                    files["/home/src/workspaces/solution/src/main/tsconfig.main.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "composite": true, "outDir": "../../dist/" },
								"references": [{ "path": "../other/tsconfig.other.json" }]
							}
""");
                    files["/home/src/workspaces/solution/src/other/tsconfig.other.json"] = StringTestUtil.Dedent("""
							{
								"compilerOptions": { "composite": true, "outDir": "../../dist/" },
							}
""");
                }),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "src/main/tsconfig.main.json", "--verbose"],
                edits = TscEdit.NoChangeOnlyEdit,
            },
        ];

        // tscbuild_test.go:2911-2913
        foreach (var test in testCases)
        {
            test.RunAndAssert("projectReferenceWithRootDirInParent");
        }
    }
}
