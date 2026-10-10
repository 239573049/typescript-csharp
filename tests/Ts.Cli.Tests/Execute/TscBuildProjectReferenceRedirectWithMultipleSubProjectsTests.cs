// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildProjectReferenceRedirectWithMultipleSubProjects.

using System.Collections.Generic;
using Ts.TestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildProjectReferenceRedirectWithMultipleSubProjectsTests
{
    // tscbuild_test.go:4486-4578 — TestBuildProjectReferenceRedirectWithMultipleSubProjects.
    [Fact]
    public void BuildProjectReferenceRedirectWithMultipleSubProjects()
    {
        var testCases = new List<TscInput>
        {
            new TscInput
            {
                subScenario = "uses correct project reference redirect when file belongs to multiple sub-projects",
                files = new Dictionary<string, object>
                {
                    // Consumer tsconfig - uses customConditions and moduleSuffixes for react-native
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"module": "esnext",
							"moduleResolution": "bundler",
							"customConditions": ["react-native"],
							"moduleSuffixes": [".native", ""],
							"strict": true,
							"noEmit": true
						},
						"include": ["app.ts"],
						"references": [
							{ "path": "./pkg" }
						]
					}
"""),
                    // Consumer app - imports from pkg, expects native platform
                    ["/home/src/workspaces/project/app.ts"] = StringTestUtil.Dedent("""

					import { platform } from "pkg";
					const check: "native" = platform;
"""),
                    // Package - web tsconfig (includes all files via **/*)
                    ["/home/src/workspaces/project/pkg/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"module": "esnext",
							"moduleResolution": "bundler",
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"outDir": "./dist",
							"strict": true
						},
						"include": ["**/*"],
						"exclude": ["dist"],
						"references": [
							{ "path": "./tsconfig.native.json" }
						]
					}
"""),
                    // Package - native tsconfig (includes specific files, has customConditions)
                    ["/home/src/workspaces/project/pkg/tsconfig.native.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"module": "esnext",
							"moduleResolution": "bundler",
							"composite": true,
							"declaration": true,
							"emitDeclarationOnly": true,
							"outDir": "./dist",
							"strict": true,
							"customConditions": ["react-native"],
							"moduleSuffixes": [".native", ""]
						},
						"include": ["index.native.ts", "src/util.native.ts", "src/util.ts"],
						"exclude": ["dist"]
					}
"""),
                    // Package exports - react-native condition maps to index.native.ts
                    ["/home/src/workspaces/project/pkg/package.json"] = StringTestUtil.Dedent("""

					{
						"name": "pkg",
						"exports": {
							".": {
								"react-native": "./index.native.ts",
								"types": "./index.ts",
								"default": "./index.ts"
							}
						}
					}
"""),
                    // Web entry point
                    ["/home/src/workspaces/project/pkg/index.ts"] = "export { platform } from \"./src/util\";",
                    // Native entry point (same content, but should resolve internal imports using native tsconfig)
                    ["/home/src/workspaces/project/pkg/index.native.ts"] = "export { platform } from \"./src/util\";",
                    // Web util
                    ["/home/src/workspaces/project/pkg/src/util.ts"] = "export const platform = \"web\" as const;",
                    // Native util
                    ["/home/src/workspaces/project/pkg/src/util.native.ts"] = "export const platform = \"native\" as const;",
                    // node_modules symlink
                    ["/home/src/workspaces/project/node_modules/pkg"] = VfsTestUtil.Symlink("/home/src/workspaces/project/pkg"),
                },
                cwd = "/home/src/workspaces/project",
                commandLineArgs = ["--b", "--verbose"],
            },
        };

        foreach (var test in testCases)
        {
            test.RunAndAssert("projectReferenceRedirect");
        }
    }
}
