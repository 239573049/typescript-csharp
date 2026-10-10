// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildInferredTypeFromMonorepoReference.
// Baselines: testdata/baselines/reference/tsbuild/inferredTypeFromMonorepoReference.

using System.Collections.Generic;
using Ts.TestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildInferredTypeFromMonorepoReferenceTests
{
    // tscbuild_test.go:1356-1494
    [Fact]
    public void InferredTypeFromReferencedProjectThatReferencesAnotherProjectInMonorepo()
    {
        new TscInput
        {
            subScenario = "inferred type from referenced project that references another project in monorepo",
            files = new Dictionary<string, object>
            {
                // Root package.json and tsconfig.json
                ["/home/src/workspaces/solution/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "tsgo-monorepo-issue",
						"private": true,
						"workspaces": ["packages/*"]
					}
"""),
                ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"files": [],
						"include": [],
						"references": [
							{ "path": "packages/package-a" },
							{ "path": "packages/package-b" },
							{ "path": "packages/package-c" }
						]
					}
"""),
                // package-c: exports MyType interface
                ["/home/src/workspaces/solution/packages/package-c/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "package-c",
						"version": "1.0.0",
						"private": true,
						"type": "module",
						"main": "./src/index.ts",
						"types": "./src/index.ts",
						"exports": {
							".": "./src/index.ts"
						}
					}
"""),
                ["/home/src/workspaces/solution/packages/package-c/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"module": "ESNext",
							"moduleResolution": "Bundler",
							"target": "ES2022",
							"outDir": "./out",
							"rootDir": "./src"
						},
						"include": ["src/**/*"]
					}
"""),
                ["/home/src/workspaces/solution/packages/package-c/src/index.ts"] = StringTestUtil.Dedent("""
					export interface MyType {
						id: string;
						name: string;
						enabled: boolean;
					}
"""),
                // package-b: project reference to package-c, exports createThing() returning MyType
                ["/home/src/workspaces/solution/packages/package-b/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "package-b",
						"version": "1.0.0",
						"private": true,
						"type": "module",
						"main": "./src/index.ts",
						"types": "./src/index.ts",
						"exports": {
							".": "./src/index.ts"
						},
						"dependencies": {
							"package-c": "workspace:*"
						}
					}
"""),
                ["/home/src/workspaces/solution/packages/package-b/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"module": "ESNext",
							"moduleResolution": "Bundler",
							"target": "ES2022",
							"outDir": "./out",
							"rootDir": "./src"
						},
						"include": ["src/**/*"],
						"references": [{ "path": "../package-c" }]
					}
"""),
                ["/home/src/workspaces/solution/packages/package-b/src/index.ts"] = StringTestUtil.Dedent("""
					import type { MyType } from "package-c";

					export function createThing(input: MyType): MyType {
						return { ...input };
					}
"""),
                // package-a: project reference to package-b only (not package-c), uses createThing() without type annotation
                ["/home/src/workspaces/solution/packages/package-a/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "package-a",
						"version": "1.0.0",
						"private": true,
						"type": "module",
						"main": "./src/index.ts",
						"types": "./src/index.ts",
						"exports": {
							".": "./src/index.ts"
						},
						"dependencies": {
							"package-b": "workspace:*"
						}
					}
"""),
                ["/home/src/workspaces/solution/packages/package-a/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"module": "ESNext",
							"moduleResolution": "Bundler",
							"target": "ES2022",
							"outDir": "./out",
							"rootDir": "./src"
						},
						"include": ["src/**/*"],
						"references": [{ "path": "../package-b" }]
					}
"""),
                ["/home/src/workspaces/solution/packages/package-a/src/index.ts"] = StringTestUtil.Dedent("""
					import { createThing } from "package-b";

					class MyClass {
						public thing = createThing({ id: "1", name: "test", enabled: true });
					}

					export { MyClass };
"""),
                // Symlinks for node_modules to simulate pnpm/yarn workspace hoisting
                ["/home/src/workspaces/solution/node_modules/package-a"] = VfsTestUtil.Symlink("/home/src/workspaces/solution/packages/package-a"),
                ["/home/src/workspaces/solution/node_modules/package-b"] = VfsTestUtil.Symlink("/home/src/workspaces/solution/packages/package-b"),
                ["/home/src/workspaces/solution/node_modules/package-c"] = VfsTestUtil.Symlink("/home/src/workspaces/solution/packages/package-c"),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "--verbose"],
        }.RunAndAssert("inferredTypeFromMonorepoReference");
    }
}
