// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildDependencyUpdate.
// Baselines: testdata/baselines/reference/tsbuild/dependencyUpdate and
// testdata/baselines/reference/tsbuildWatch/dependencyUpdate.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildDependencyUpdateTests
{
    // tscbuild_test.go:1012-1069
    [Fact]
    public void RebuildsWhenDependencyInNodeModulesIsUpdated()
    {
        new TscInput
        {
            // https://github.com/microsoft/typescript-go/issues/2666
            subScenario = "rebuilds when dependency in node_modules is updated",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"outDir": "dist",
							"strict": true
						},
						"include": ["src/**/*"]
					}
"""),
                ["/home/src/workspaces/project/src/index.ts"] = StringTestUtil.Dedent("""
					import { myValue } from "my-dep";
					export const value: string = myValue;
"""),
                ["/home/src/workspaces/project/node_modules/my-dep/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "my-dep",
						"version": "1.0.0",
						"types": "index.d.ts"
					}
"""),
                ["/home/src/workspaces/project/node_modules/my-dep/index.d.ts"] = "export declare const myValue: string;",
            },
            cwd = "/home/src/workspaces/project",
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "update dependency d.ts with breaking type change",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/node_modules/my-dep/index.d.ts", "export declare const myValue: number;"),
                },
                new TscEdit
                {
                    caption = "restore dependency d.ts",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/node_modules/my-dep/index.d.ts", "export declare const myValue: string;"),
                },
                new TscEdit
                {
                    caption = "update dependency d.ts timestamp without changing text",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/node_modules/my-dep/index.d.ts", "export declare const myValue: string;"),
                },
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "delete dependency d.ts",
                    edit = sys => sys.RemoveNoError("/home/src/workspaces/project/node_modules/my-dep/index.d.ts"),
                },
            ],
        }.RunAndAssert("dependencyUpdate");
    }

    // tscbuild_test.go:1070-1100
    [Fact]
    public void RebuildsWhenMissingDependencyPackageJsonIsAdded()
    {
        new TscInput
        {
            subScenario = "rebuilds when missing dependency package json is added",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"outDir": "dist",
							"strict": true
						},
						"include": ["src/**/*"]
					}
"""),
                ["/home/src/workspaces/project/src/index.ts"] = StringTestUtil.Dedent("""
					import { myValue } from "my-dep";
					export const value: string = myValue;
"""),
                ["/home/src/workspaces/project/node_modules/my-dep/index.d.ts"] = "export declare const myValue: string;",
                ["/home/src/workspaces/project/node_modules/my-dep/alt.d.ts"] = "export declare const myValue: number;",
            },
            cwd = "/home/src/workspaces/project",
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "add package json redirecting types to a declaration file with a breaking type change",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/node_modules/my-dep/package.json", """{"types":"alt.d.ts"}"""),
                },
            ],
        }.RunAndAssert("dependencyUpdate");
    }

    // tscbuild_test.go:1101-1138
    [Fact]
    public void RebuildsWhenDependencyPackageJsonRedirectsToADifferentDeclarationFile()
    {
        new TscInput
        {
            subScenario = "rebuilds when dependency package json redirects to a different declaration file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"outDir": "dist",
							"strict": true
						},
						"include": ["src/**/*"]
					}
"""),
                ["/home/src/workspaces/project/src/index.ts"] = StringTestUtil.Dedent("""
					import { myValue } from "my-dep";
					export const value: string = myValue;
"""),
                ["/home/src/workspaces/project/node_modules/my-dep/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "my-dep",
						"version": "1.0.0",
						"types": "index.d.ts"
					}
"""),
                ["/home/src/workspaces/project/node_modules/my-dep/index.d.ts"] = "export declare const myValue: string;",
                ["/home/src/workspaces/project/node_modules/my-dep/alt.d.ts"] = "export declare const myValue: number;",
            },
            cwd = "/home/src/workspaces/project",
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "redirect package types to a declaration file with a breaking type change",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/node_modules/my-dep/package.json", "index.d.ts", "alt.d.ts"),
                },
            ],
        }.RunAndAssert("dependencyUpdate");
    }

    // tscbuild_test.go:1139-1173
    [Fact]
    public void RebuildsWhenAbsoluteNonRootDependencyIsUpdated()
    {
        new TscInput
        {
            subScenario = "rebuilds when absolute non-root dependency is updated",
            files = new Dictionary<string, object>
            {
                ["C:/work/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"outDir": "dist",
							"paths": {
								"abs-dep": ["D:/work/deps/dep.d.ts"]
							},
							"strict": true
						},
						"include": ["src/**/*"]
					}
"""),
                ["C:/work/project/src/index.ts"] = StringTestUtil.Dedent("""
					import { myValue } from "abs-dep";
					export const value: string = myValue;
"""),
                ["D:/work/deps/dep.d.ts"] = "export declare const myValue: string;",
            },
            cwd = "C:/work/project",
            windowsStyleRoot = "C:/",
            ignoreCase = true,
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "update absolute non-root dependency with breaking type change",
                    edit = sys => sys.WriteFileNoError("D:/work/deps/dep.d.ts", "export declare const myValue: number;"),
                },
            ],
        }.RunAndAssert("dependencyUpdate");
    }

    // tscbuild_test.go:1174-1208
    [Fact]
    public void WatchesAbsoluteNonRootDependencyUpdates()
    {
        new TscInput
        {
            subScenario = "watches absolute non-root dependency updates",
            files = new Dictionary<string, object>
            {
                ["C:/work/project/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"outDir": "dist",
							"paths": {
								"abs-dep": ["D:/work/deps/dep.d.ts"]
							},
							"strict": true
						},
						"include": ["src/**/*"]
					}
"""),
                ["C:/work/project/src/index.ts"] = StringTestUtil.Dedent("""
					import { myValue } from "abs-dep";
					export const value: string = myValue;
"""),
                ["D:/work/deps/dep.d.ts"] = "export declare const myValue: string;",
            },
            cwd = "C:/work/project",
            windowsStyleRoot = "C:/",
            ignoreCase = true,
            commandLineArgs = ["--b", "--verbose", "--watch"],
            edits =
            [
                new TscEdit
                {
                    caption = "update absolute non-root dependency with breaking type change",
                    edit = sys => sys.WriteFileNoError("D:/work/deps/dep.d.ts", "export declare const myValue: number;"),
                },
            ],
        }.RunAndAssert("dependencyUpdate");
    }
}
