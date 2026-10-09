// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildDemoProject.

using System;
using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildDemoTests
{
    // tscbuild_test.go:526-812
    private static Dictionary<string, object> GetDemoFileMap(Action<Dictionary<string, object>>? modify)
    {
        var files = new Dictionary<string, object>
        {
            ["/user/username/projects/demo/animals/animal.ts"] = StringTestUtil.Dedent("""
				export type Size = "small" | "medium" | "large";
				export default interface Animal {
					size: Size;
				}
"""),
            ["/user/username/projects/demo/animals/dog.ts"] = StringTestUtil.Dedent("""
				import Animal from '.';
				import { makeRandomName } from '../core/utilities';

				export interface Dog extends Animal {
					woof(): void;
					name: string;
				}

				export function createDog(): Dog {
					return ({
						size: "medium",
						woof: function(this: Dog) {
							console.log(`${ this.name } says "Woof"!`);
						},
						name: makeRandomName()
					});
				}
"""),
            ["/user/username/projects/demo/animals/index.ts"] = StringTestUtil.Dedent("""
				import Animal from './animal';

				export default Animal;
				import { createDog, Dog } from './dog';
				export { createDog, Dog };
"""),
            ["/user/username/projects/demo/animals/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig-base.json",
					"compilerOptions": {
						"outDir": "../lib/animals",
						"rootDir": "."
					},
					"references": [
						{ "path": "../core" }
					]
				}
"""),
            ["/user/username/projects/demo/core/utilities.ts"] = StringTestUtil.Dedent("""

				export function makeRandomName() {
					return "Bob!?! ";
				}

				export function lastElementOf<T>(arr: T[]): T | undefined {
					if (arr.length === 0) return undefined;
					return arr[arr.length - 1];
				}
"""),
            ["/user/username/projects/demo/core/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig-base.json",
					"compilerOptions": {
						"outDir": "../lib/core",
						"rootDir": "."
					},
				}
"""),
            ["/user/username/projects/demo/zoo/zoo.ts"] = StringTestUtil.Dedent("""
				import { Dog, createDog } from '../animals/index';

				export function createZoo(): Array<Dog> {
					return [
						createDog()
					];
				}
"""),
            ["/user/username/projects/demo/zoo/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig-base.json",
					"compilerOptions": {
						"outDir": "../lib/zoo",
						"rootDir": "."
					},
					"references": [
						{
							"path": "../animals"
						}
					]
				}
"""),
            ["/user/username/projects/demo/tsconfig-base.json"] = StringTestUtil.Dedent("""
				{
					"compilerOptions": {
						"declaration": true,
						"target": "es5",
						"module": "commonjs",
						"strict": true,
						"noUnusedLocals": true,
						"noUnusedParameters": true,
						"noImplicitReturns": true,
						"noFallthroughCasesInSwitch": true,
						"composite": true,
					},
				}
"""),
            ["/user/username/projects/demo/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"files": [],
					"references": [
						{
							"path": "./core"
						},
						{
							"path": "./animals",
						},
						{
							"path": "./zoo",
						},
					],
				}
"""),
        };
        modify?.Invoke(files);
        return files;
    }

    private static readonly string CircularCoreConfig = StringTestUtil.Dedent("""
					{
						"extends": "../tsconfig-base.json",
						"compilerOptions": {
							"outDir": "../lib/core",
							"rootDir": "."
						},
						"references": [
							{
								"path": "../zoo",
							}
						]
					}
""");

    [Fact]
    public void InMasterBranch()
    {
        new TscInput
        {
            subScenario = "in master branch with everything setup correctly and reports no error",
            files = GetDemoFileMap(null),
            cwd = "/user/username/projects/demo",
            commandLineArgs = ["--b", "--verbose"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("demo");
    }

    [Fact]
    public void InCircularBranch()
    {
        new TscInput
        {
            subScenario = "in circular branch reports the error about it by stopping build",
            files = GetDemoFileMap(files => files["/user/username/projects/demo/core/tsconfig.json"] = CircularCoreConfig),
            cwd = "/user/username/projects/demo",
            commandLineArgs = ["--b", "--verbose"],
        }.RunAndAssert("demo");
    }

    // !!! sheetal - this has missing errors from strada about files not in rootDir (3)
    [Fact]
    public void InBadRefBranch()
    {
        new TscInput
        {
            subScenario = "in bad-ref branch reports the error about files not in rootDir at the import location",
            files = GetDemoFileMap(files => files["/user/username/projects/demo/core/utilities.ts"] =
                "import * as A from '../animals'\n" + (string)files["/user/username/projects/demo/core/utilities.ts"]),
            cwd = "/user/username/projects/demo",
            commandLineArgs = ["--b", "--verbose"],
        }.RunAndAssert("demo");
    }

    [Fact]
    public void InCircularIsSetInTheReference()
    {
        new TscInput
        {
            subScenario = "in circular is set in the reference",
            files = GetDemoFileMap(files =>
            {
                files["/user/username/projects/demo/a/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig-base.json",
					"compilerOptions": {
						"outDir": "../lib/a",
						"rootDir": "."
					},
					"references": [
						{
							"path": "../b",
							"circular": true
						}
					]
				}
""");
                files["/user/username/projects/demo/b/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig-base.json",
					"compilerOptions": {
						"outDir": "../lib/b",
						"rootDir": "."
					},
					"references": [
						{
							"path": "../a",
						}
					]
				}
""");
                files["/user/username/projects/demo/a/index.ts"] = "export const a = 10;";
                files["/user/username/projects/demo/b/index.ts"] = "export const b = 10;";
                files["/user/username/projects/demo/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"files": [],
					"references": [
						{
							"path": "./core"
						},
						{
							"path": "./animals",
						},
						{
							"path": "./zoo",
						},
						{
							"path": "./a",
						},
						{
							"path": "./b",
						},
					],
				}
""");
            }),
            cwd = "/user/username/projects/demo",
            commandLineArgs = ["--b", "--verbose"],
        }.RunAndAssert("demo");
    }

    [Fact]
    public void UpdatesWithCircularReference()
    {
        new TscInput
        {
            subScenario = "updates with circular reference",
            files = GetDemoFileMap(files => files["/user/username/projects/demo/core/tsconfig.json"] = CircularCoreConfig),
            cwd = "/user/username/projects/demo",
            commandLineArgs = ["--b", "-w", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "Fix error",
                    edit = sys => sys.WriteFileNoError("/user/username/projects/demo/core/tsconfig.json", StringTestUtil.Dedent("""
							{
								"extends": "../tsconfig-base.json",
								"compilerOptions": {
									"outDir": "../lib/core",
									"rootDir": "."
								},
							}
""")),
                },
            ],
        }.RunAndAssert("demo");
    }

    // !!! sheetal - this has missing errors from strada about files not in rootDir (3)
    [Fact]
    public void UpdatesWithBadReference()
    {
        new TscInput
        {
            subScenario = "updates with bad reference",
            files = GetDemoFileMap(files => files["/user/username/projects/demo/core/utilities.ts"] =
                "import * as A from '../animals'\n" + (string)files["/user/username/projects/demo/core/utilities.ts"]),
            cwd = "/user/username/projects/demo",
            commandLineArgs = ["--b", "-w", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "Prepend a line",
                    edit = sys => sys.PrependFileText("/user/username/projects/demo/core/utilities.ts", "\n"),
                },
            ],
        }.RunAndAssert("demo");
    }
}
