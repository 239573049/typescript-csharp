// Ported from tsgo internal/execute/tsctests/tscbuild_test.go —
// TestBuildDeclarationEmitForReferencedProjectTypesSubpath.
// Baselines: testdata/baselines/reference/tsbuild/declarationEmitForReferencedProjectTypesSubpath.

using System.Collections.Generic;
using Ts.TestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildDeclarationEmitForReferencedProjectTypesSubpathTests
{
    // tscbuild_test.go:1503-1587
    [Fact]
    public void DeclarationEmitNamesReferencedProjectTypeViaTypesSubpath()
    {
        new TscInput
        {
            // https://github.com/microsoft/typescript-go/issues/3617
            // This package.json exposes the same files under two exports subpaths
            // (`./src/*` and `./types/*`), so both `import("@scope/dep/src/...")` and
            // `import("@scope/dep/types/...")` are technically valid module specifiers for
            // the inferred type. The point of this test is not that one is "correct" and
            // the other "wrong", but that we pick the same one Strada does: when ranking
            // candidate paths, project-reference redirects (here the emitted `./types`
            // `.d.ts`) sort ahead of non-redirect paths (the symlinked `./src` source).
            // A regression in `comparePathsByRedirect` had inverted that ordering, causing
            // the `./src/*` specifier to win instead.
            subScenario = "declaration emit names referenced project type via types subpath",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/packages/dep/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "@scope/dep",
						"version": "1.0.0",
						"exports": {
							"./src/*": "./src/*",
							"./types/*": "./types/*",
							".": { "types": "./types/index.d.ts", "default": "./src/index.ts" }
						}
					}
"""),
                ["/home/src/workspaces/solution/packages/dep/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"module": "esnext",
							"moduleResolution": "bundler",
							"rootDir": "src",
							"outDir": "types",
							"declarationDir": "types",
							"strict": true
						},
						"include": ["src"]
					}
"""),
                ["/home/src/workspaces/solution/packages/dep/src/index.ts"] = StringTestUtil.Dedent("""
					import type { ComponentTypes as NewComponentTypes } from "./themes/componentTypes/index.js"
					export type { NewComponentTypes }
"""),
                ["/home/src/workspaces/solution/packages/dep/src/themes/componentTypes/index.ts"] = StringTestUtil.Dedent("""
					import type FormFieldLayout from "./formFieldLayout.js"
					export type ComponentTypes = {
						formFieldLayout: () => FormFieldLayout
					}
"""),
                ["/home/src/workspaces/solution/packages/dep/src/themes/componentTypes/formFieldLayout.ts"] = StringTestUtil.Dedent("""
					export type FormFieldLayout = {
						textColor: string
						fontSize: string
					}
					export default FormFieldLayout
"""),
                ["/home/src/workspaces/solution/packages/consumer/package.json"] = StringTestUtil.Dedent("""
					{
						"name": "@scope/consumer",
						"version": "1.0.0"
					}
"""),
                ["/home/src/workspaces/solution/packages/consumer/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": {
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"module": "esnext",
							"moduleResolution": "bundler",
							"rootDir": "src",
							"outDir": "types",
							"declarationDir": "types",
							"strict": true
						},
						"include": ["src"],
						"references": [{ "path": "../dep" }]
					}
"""),
                ["/home/src/workspaces/solution/packages/consumer/src/index.ts"] = StringTestUtil.Dedent("""
					import type { NewComponentTypes } from "@scope/dep"
					declare const c: NewComponentTypes
					export const style = c.formFieldLayout()
"""),
                ["/home/src/workspaces/solution/node_modules/@scope/dep"] = VfsTestUtil.Symlink("/home/src/workspaces/solution/packages/dep"),
                ["/home/src/workspaces/solution/node_modules/@scope/consumer"] = VfsTestUtil.Symlink("/home/src/workspaces/solution/packages/consumer"),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "packages/consumer", "--verbose"],
        }.RunAndAssert("declarationEmitForReferencedProjectTypesSubpath");
    }
}
