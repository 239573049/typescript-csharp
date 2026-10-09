// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscModuleResolution.

using System.Collections.Generic;
using Ts.TestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscModuleResolutionTests
{
    // tsc_test.go:2422-2468 — getBuildModuleResolutionInProjectRefTestCase.
    private static TscInput GetBuildModuleResolutionInProjectRefTestCase(bool preserveSymlinks) => new()
    {
        subScenario = "resolves specifier in output declaration file from referenced project correctly" + (preserveSymlinks ? " with preserveSymlinks" : ""),
        files = new Dictionary<string, object>
        {
            ["/user/username/projects/myproject/packages/pkg1/index.ts"] = StringTestUtil.Dedent("""
                import type { TheNum } from 'pkg2'
                export const theNum: TheNum = 42;
"""),
            ["/user/username/projects/myproject/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": { 
                        "outDir": "build",
                        "preserveSymlinks": {{(preserveSymlinks ? "true" : "false")}}
                    },
                    "references": [{ "path": "../pkg2" }]
                }
"""),
            ["/user/username/projects/myproject/packages/pkg2/const.ts"] = StringTestUtil.Dedent("""
                export type TheNum = 42;
"""),
            ["/user/username/projects/myproject/packages/pkg2/index.ts"] = StringTestUtil.Dedent("""
                export type { TheNum } from 'const';
"""),
            ["/user/username/projects/myproject/packages/pkg2/tsconfig.json"] = StringTestUtil.Dedent($$"""
                {
                    "compilerOptions": {
                        "composite": true,
                        "outDir": "build",
                        "paths": {
                            "const": ["./const"]
                        },
                        "preserveSymlinks": {{(preserveSymlinks ? "true" : "false")}},
                    },
                }
"""),
            ["/user/username/projects/myproject/packages/pkg2/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "pkg2",
                    "version": "1.0.0",
                    "main": "build/index.js"
                }
"""),
            ["/user/username/projects/myproject/node_modules/pkg2"] = VfsTestUtil.Symlink("/user/username/projects/myproject/packages/pkg2"),
        },
        cwd = "/user/username/projects/myproject",
        commandLineArgs = ["-b", "packages/pkg1", "--verbose", "--traceResolution"],
    };

    // tsc_test.go:2469-2515 — getTscModuleResolutionSharingFileMap.
    private static Dictionary<string, object> GetTscModuleResolutionSharingFileMap() => new()
    {
        ["/home/src/workspaces/project/packages/a/index.js"] = "export const a = 'a';",
        ["/home/src/workspaces/project/packages/a/test/index.js"] = "import 'a';",
        ["/home/src/workspaces/project/packages/a/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "compilerOptions": {
                    "checkJs": true,
                    "composite": true,
                    "declaration": true,
                    "emitDeclarationOnly": true,
                    "module": "nodenext",
                    "outDir": "types",
                },
            }
"""),
        ["/home/src/workspaces/project/packages/a/package.json"] = StringTestUtil.Dedent("""
            {
                "name": "a",
                "version": "0.0.0",
                "type": "module",
                "exports": {
                    ".": {
                        "types": "./types/index.d.ts",
                        "default": "./index.js"
                    }
                }
            }
"""),
        ["/home/src/workspaces/project/packages/b/index.js"] = "export { a } from 'a';",
        ["/home/src/workspaces/project/packages/b/tsconfig.json"] = StringTestUtil.Dedent("""
            {
            "references": [{ "path": "../a" }],
                "compilerOptions": {
                    "checkJs": true,
                    "module": "nodenext",
                    "noEmit": true,
                    "noImplicitAny": true,
                },
            }
"""),
        ["/home/src/workspaces/project/packages/b/package.json"] = StringTestUtil.Dedent("""
            {
                "name": "b",
                "version": "0.0.0",
                "type": "module"
            }
"""),
        ["/home/src/workspaces/project/node_modules/a"] = VfsTestUtil.Symlink("/home/src/workspaces/project/packages/a"),
    };

    // tsc_test.go:2516-2533 — getTscModuleResolutionAlternateResultAtTypesPackageJson.
    // PORT-NOTE: the raw string literal is indented with tabs, exactly as the Go source, so that
    // Dedent's leading-tab → 4-space conversion reproduces the reference baseline byte-for-byte.
    private static string GetTscModuleResolutionAlternateResultAtTypesPackageJson(string packageName, bool addTypesCondition)
    {
        var typesString = addTypesCondition ? "\"types\": \"./index.d.ts\"," : "";
        return StringTestUtil.Dedent($$"""
		{
			"name": "@types/{{packageName}}",
			"version": "1.0.0",
			"types": "index.d.ts",
			"exports": {
				".": {
					{{typesString}}
					"require": "./index.d.ts"
				}
			}
		}
""");
    }

    // tsc_test.go:2534-2557 — getTscModuleResolutionAlternateResultPackageJson.
    // PORT-NOTE: tab indentation as in the Go source (see above).
    private static string GetTscModuleResolutionAlternateResultPackageJson(string packageName, bool addTypes, bool addTypesCondition)
    {
        var types = addTypes ? "\"types\": \"index.d.ts\"," : "";
        var typesString = addTypesCondition ? "\"types\": \"./index.d.ts\"," : "";
        return StringTestUtil.Dedent($$"""
		{
			"name": "{{packageName}}",
			"version": "1.0.0",
			"main": "index.js",
			{{types}}
			"exports": {
				".": {
					{{typesString}}
					"import": "./index.mjs",
					"require": "./index.js"
				}
			}
		}
""");
    }

    // tsc_test.go:2558-2560 — getTscModuleResolutionAlternateResultDts.
    private static string GetTscModuleResolutionAlternateResultDts(string packageName) =>
        $"export declare const {packageName}: number;";

    // tsc_test.go:2561-2563 — getTscModuleResolutionAlternateResultJs.
    private static string GetTscModuleResolutionAlternateResultJs(string packageName) =>
        $"module.exports = {{ {packageName}: 1 }};";

    // tsc_test.go:2564-2566 — getTscModuleResolutionAlternateResultMjs.
    private static string GetTscModuleResolutionAlternateResultMjs(string packageName) =>
        $"export const {packageName} = 1;";

    // tsc_test.go:2568-2569
    [Fact]
    public void ResolvesSpecifierInOutputDeclarationFileFromReferencedProjectCorrectly()
        => GetBuildModuleResolutionInProjectRefTestCase(false).RunAndAssert("moduleResolution");

    [Fact]
    public void ResolvesSpecifierInOutputDeclarationFileFromReferencedProjectCorrectlyWithPreserveSymlinks()
        => GetBuildModuleResolutionInProjectRefTestCase(true).RunAndAssert("moduleResolution");

    // tsc_test.go:2570-2597
    [Fact]
    public void TypeReferenceResolutionUsesCorrectOptionsForDifferentResolutionOptionsReferencedProject()
    {
        new TscInput
        {
            subScenario = "type reference resolution uses correct options for different resolution options referenced project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/packages/pkg1_index.ts"] = "export const theNum: TheNum = \"type1\";",
                ["/home/src/workspaces/project/packages/pkg1.tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "typeRoots": ["./typeroot1"]
                        },
                        "files": ["./pkg1_index.ts"],
                    }
"""),
                ["/home/src/workspaces/project/packages/typeroot1/sometype/index.d.ts"] = "declare type TheNum = \"type1\";",
                ["/home/src/workspaces/project/packages/pkg2_index.ts"] = "export const theNum: TheNum2 = \"type2\";",
                ["/home/src/workspaces/project/packages/pkg2.tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "typeRoots": ["./typeroot2"]
                        },
                        "files": ["./pkg2_index.ts"],
                    }
"""),
                ["/home/src/workspaces/project/packages/typeroot2/sometype/index.d.ts"] = "declare type TheNum2 = \"type2\";",
            },
            commandLineArgs = ["-b", "packages/pkg1.tsconfig.json", "packages/pkg2.tsconfig.json", "--verbose", "--traceResolution"],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2598-2634
    [Fact]
    public void ImpliedNodeFormatDiffersBetweenProjectsForSharedFile()
    {
        new TscInput
        {
            subScenario = "impliedNodeFormat differs between projects for shared file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/a/src/index.ts"] = "",
                ["/home/src/workspaces/project/a/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true
                        }
                    }
"""),
                ["/home/src/workspaces/project/b/src/index.ts"] = StringTestUtil.Dedent("""
                    import pg from "pg";
                    pg.foo();
"""),
                ["/home/src/workspaces/project/b/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": { 
                            "strict": true,
                            "module": "node16"
                        },
                    }
"""),
                ["/home/src/workspaces/project/b/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "b",
                        "type": "module"
                    }
"""),
                ["/home/src/workspaces/project/node_modules/@types/pg/index.d.ts"] = "export function foo(): void;",
                ["/home/src/workspaces/project/node_modules/@types/pg/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "@types/pg",
                        "types": "index.d.ts"
                    }
"""),
            },
            commandLineArgs = ["-b", "a", "b", "--verbose", "--traceResolution", "--explainFiles"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2635-2639
    [Fact]
    public void SharedResolutionShouldNotReportError()
    {
        new TscInput
        {
            subScenario = "shared resolution should not report error",
            files = GetTscModuleResolutionSharingFileMap(),
            commandLineArgs = ["-b", "packages/b", "--verbose", "--traceResolution", "--explainFiles"],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2640-2650
    [Fact]
    public void WhenResolutionIsNotShared()
    {
        new TscInput
        {
            subScenario = "when resolution is not shared",
            files = GetTscModuleResolutionSharingFileMap(),
            commandLineArgs = ["-b", "packages/a", "--verbose", "--traceResolution", "--explainFiles"],
            edits =
            [
                new TscEdit
                {
                    caption = "build b",
                    commandLineArgs = ["-b", "packages/b", "--verbose", "--traceResolution", "--explainFiles"],
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2651-2795
    [Fact]
    public void PnpmStyleLayout()
    {
        new TscInput
        {
            subScenario = "pnpm style layout",
            files = new Dictionary<string, object>
            {
                // button@0.0.1
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.1/node_modules/@component-type-checker/button/src/index.ts"] = StringTestUtil.Dedent("""
                    export interface Button {
                        a: number;
                        b: number;
                    }
                    export function createButton(): Button {
                        return {
                            a: 0,
                            b: 1,
                        };
                    }
"""),
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.1/node_modules/@component-type-checker/button/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "@component-type-checker/button",
                        "version": "0.0.1",
                        "main": "./src/index.ts"
                    }
"""),

                // button@0.0.2
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.2/node_modules/@component-type-checker/button/src/index.ts"] = StringTestUtil.Dedent("""
                    export interface Button {
                        a: number;
                        c: number;
                    }
                    export function createButton(): Button {
                        return {
                            a: 0,
                            c: 2,
                        };
                    }
"""),
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.2/node_modules/@component-type-checker/button/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "@component-type-checker/button",
                        "version": "0.0.2",
                        "main": "./src/index.ts"
                    }
"""),

                // @component-type-checker+components@0.0.1_@component-type-checker+button@0.0.1
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.1/node_modules/@component-type-checker/button"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.1/node_modules/@component-type-checker/button"),
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.1/node_modules/@component-type-checker/components/src/index.ts"] = StringTestUtil.Dedent("""
                    export { createButton, Button } from "@component-type-checker/button";
"""),
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.1/node_modules/@component-type-checker/components/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "@component-type-checker/components",
                        "version": "0.0.1",
                        "main": "./src/index.ts",
                        "peerDependencies": {
                            "@component-type-checker/button": "*"
                        },
                        "devDependencies": {
                            "@component-type-checker/button": "0.0.2"
                        }
                    }
"""),

                // @component-type-checker+components@0.0.1_@component-type-checker+button@0.0.2
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.2/node_modules/@component-type-checker/button"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.2/node_modules/@component-type-checker/button"),
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.2/node_modules/@component-type-checker/components/src/index.ts"] = StringTestUtil.Dedent("""
                    export { createButton, Button } from "@component-type-checker/button";
"""),
                ["/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.2/node_modules/@component-type-checker/components/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "@component-type-checker/components",
                        "version": "0.0.1",
                        "main": "./src/index.ts",
                        "peerDependencies": {
                            "@component-type-checker/button": "*"
                        },
                        "devDependencies": {
                            "@component-type-checker/button": "0.0.2"
                        }
                    }
"""),

                // sdk => @component-type-checker+components@0.0.1_@component-type-checker+button@0.0.1
                ["/home/src/projects/component-type-checker/packages/sdk/src/index.ts"] = StringTestUtil.Dedent("""
                    export { Button, createButton } from "@component-type-checker/components";
                    export const VERSION = "0.0.2";
"""),
                ["/home/src/projects/component-type-checker/packages/sdk/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "@component-type-checker/sdk1",
                        "version": "0.0.2",
                        "main": "./src/index.ts",
                        "dependencies": {
                            "@component-type-checker/components": "0.0.1",
                            "@component-type-checker/button": "0.0.1"
                        }
                    }
"""),
                ["/home/src/projects/component-type-checker/packages/sdk/node_modules/@component-type-checker/button"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.1/node_modules/@component-type-checker/button"),
                ["/home/src/projects/component-type-checker/packages/sdk/node_modules/@component-type-checker/components"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.1/node_modules/@component-type-checker/components"),

                // app => @component-type-checker+components@0.0.1_@component-type-checker+button@0.0.2
                ["/home/src/projects/component-type-checker/packages/app/src/app.tsx"] = StringTestUtil.Dedent("""
                    import { VERSION } from "@component-type-checker/sdk";
                    import { Button } from "@component-type-checker/components";
                    import { createButton } from "@component-type-checker/button";
                    const button: Button = createButton();
"""),
                ["/home/src/projects/component-type-checker/packages/app/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "app",
                        "version": "1.0.0",
                        "dependencies": {
                            "@component-type-checker/button": "0.0.2",
                            "@component-type-checker/components": "0.0.1",
                            "@component-type-checker/sdk": "0.0.2"
                        }
                    }
"""),
                ["/home/src/projects/component-type-checker/packages/app/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "target": "es5",
                            "module": "esnext",
                            "lib": ["ES5"],
                            "outDir": "dist",
                        },
                        "include": ["src"],
                    }
"""),
                ["/home/src/projects/component-type-checker/packages/app/node_modules/@component-type-checker/button"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+button@0.0.2/node_modules/@component-type-checker/button"),
                ["/home/src/projects/component-type-checker/packages/app/node_modules/@component-type-checker/components"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/node_modules/.pnpm/@component-type-checker+components@0.0.1_@component-type-checker+button@0.0.2/node_modules/@component-type-checker/components"),
                ["/home/src/projects/component-type-checker/packages/app/node_modules/@component-type-checker/sdk"] =
                    VfsTestUtil.Symlink("/home/src/projects/component-type-checker/packages/sdk"),
            },
            cwd = "/home/src/projects/component-type-checker/packages/app",
            commandLineArgs = ["--traceResolution", "--explainFiles"],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2796-2835
    [Fact]
    public void PackageJsonScope()
    {
        new TscInput
        {
            subScenario = "package json scope",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "target": "ES2016",
                            "composite": true,
                            "module": "Node16",
                            "traceResolution": true,
                        },
                        "files": [
                            "main.ts",
                            "fileA.ts",
                            "fileB.mts",
                        ],
                    }
"""),
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/src/fileA.ts"] = StringTestUtil.Dedent("""
                    import { foo } from "./fileB.mjs";
                    foo();
"""),
                ["/home/src/workspaces/project/src/fileB.mts"] = "export function foo() {}",
                ["/home/src/workspaces/project/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "app",
                        "version": "1.0.0"
                    }
"""),
            },
            commandLineArgs = ["-p", "src", "--explainFiles", "--extendedDiagnostics"],
            edits =
            [
                new TscEdit
                {
                    caption = "Delete package.json",
                    edit = sys => sys.RemoveNoError("/home/src/workspaces/project/package.json"),
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2836-2951
    [Fact]
    public void AlternateResult()
    {
        new TscInput
        {
            subScenario = "alternateResult",
            files = new Dictionary<string, object>
            {
                ["/home/src/projects/project/node_modules/@types/bar/package.json"] = GetTscModuleResolutionAlternateResultAtTypesPackageJson("bar" /*addTypesCondition*/, false),
                ["/home/src/projects/project/node_modules/@types/bar/index.d.ts"] = GetTscModuleResolutionAlternateResultDts("bar"),
                ["/home/src/projects/project/node_modules/bar/package.json"] = GetTscModuleResolutionAlternateResultPackageJson("bar" /*addTypes*/, false /*addTypesCondition*/, false),
                ["/home/src/projects/project/node_modules/bar/index.js"] = GetTscModuleResolutionAlternateResultJs("bar"),
                ["/home/src/projects/project/node_modules/bar/index.mjs"] = GetTscModuleResolutionAlternateResultMjs("bar"),
                ["/home/src/projects/project/node_modules/foo/package.json"] = GetTscModuleResolutionAlternateResultPackageJson("foo" /*addTypes*/, true /*addTypesCondition*/, false),
                ["/home/src/projects/project/node_modules/foo/index.js"] = GetTscModuleResolutionAlternateResultJs("foo"),
                ["/home/src/projects/project/node_modules/foo/index.mjs"] = GetTscModuleResolutionAlternateResultMjs("foo"),
                ["/home/src/projects/project/node_modules/foo/index.d.ts"] = GetTscModuleResolutionAlternateResultDts("foo"),
                ["/home/src/projects/project/node_modules/@types/bar2/package.json"] = GetTscModuleResolutionAlternateResultAtTypesPackageJson("bar2" /*addTypesCondition*/, true),
                ["/home/src/projects/project/node_modules/@types/bar2/index.d.ts"] = GetTscModuleResolutionAlternateResultDts("bar2"),
                ["/home/src/projects/project/node_modules/bar2/package.json"] = GetTscModuleResolutionAlternateResultPackageJson("bar2" /*addTypes*/, false /*addTypesCondition*/, false),
                ["/home/src/projects/project/node_modules/bar2/index.js"] = GetTscModuleResolutionAlternateResultJs("bar2"),
                ["/home/src/projects/project/node_modules/bar2/index.mjs"] = GetTscModuleResolutionAlternateResultMjs("bar2"),
                ["/home/src/projects/project/node_modules/foo2/package.json"] = GetTscModuleResolutionAlternateResultPackageJson("foo2" /*addTypes*/, true /*addTypesCondition*/, true),
                ["/home/src/projects/project/node_modules/foo2/index.js"] = GetTscModuleResolutionAlternateResultJs("foo2"),
                ["/home/src/projects/project/node_modules/foo2/index.mjs"] = GetTscModuleResolutionAlternateResultMjs("foo2"),
                ["/home/src/projects/project/node_modules/foo2/index.d.ts"] = GetTscModuleResolutionAlternateResultDts("foo2"),
                ["/home/src/projects/project/index.mts"] = StringTestUtil.Dedent("""
                    import { foo } from "foo";
                    import { bar } from "bar";
                    import { foo2 } from "foo2";
                    import { bar2 } from "bar2";
"""),
                ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "node16",
                            "moduleResolution": "node16",
                            "traceResolution": true,
                            "incremental": true,
                            "strict": true,
                            "types": [],
                        },
                        "files": ["index.mts"],
                    }
"""),
            },
            cwd = "/home/src/projects/project",
            edits =
            [
                new TscEdit
                {
                    caption = "delete the alternateResult in @types",
                    edit = sys => sys.RemoveNoError("/home/src/projects/project/node_modules/@types/bar/index.d.ts"),
                },
                new TscEdit
                {
                    caption = "delete the node10Result in package/types",
                    edit = sys => sys.RemoveNoError("/home/src/projects/project/node_modules/foo/index.d.ts"),
                },
                new TscEdit
                {
                    caption = "add the alternateResult in @types",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/@types/bar/index.d.ts", GetTscModuleResolutionAlternateResultDts("bar")),
                },
                new TscEdit
                {
                    caption = "add the alternateResult in package/types",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/foo/index.d.ts", GetTscModuleResolutionAlternateResultDts("foo")),
                },
                new TscEdit
                {
                    caption = "update package.json from @types so error is fixed",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/@types/bar/package.json", GetTscModuleResolutionAlternateResultAtTypesPackageJson("bar" /*addTypesCondition*/, true)),
                },
                new TscEdit
                {
                    caption = "update package.json so error is fixed",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/foo/package.json", GetTscModuleResolutionAlternateResultPackageJson("foo" /*addTypes*/, true /*addTypesCondition*/, true)),
                },
                new TscEdit
                {
                    caption = "update package.json from @types so error is introduced",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/@types/bar2/package.json", GetTscModuleResolutionAlternateResultAtTypesPackageJson("bar2" /*addTypesCondition*/, false)),
                },
                new TscEdit
                {
                    caption = "update package.json so error is introduced",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/foo2/package.json", GetTscModuleResolutionAlternateResultPackageJson("foo2" /*addTypes*/, true /*addTypesCondition*/, false)),
                },
                new TscEdit
                {
                    caption = "delete the alternateResult in @types",
                    edit = sys => sys.RemoveNoError("/home/src/projects/project/node_modules/@types/bar2/index.d.ts"),
                },
                new TscEdit
                {
                    caption = "delete the node10Result in package/types",
                    edit = sys => sys.RemoveNoError("/home/src/projects/project/node_modules/foo2/index.d.ts"),
                },
                new TscEdit
                {
                    caption = "add the alternateResult in @types",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/@types/bar2/index.d.ts", GetTscModuleResolutionAlternateResultDts("bar2")),
                },
                new TscEdit
                {
                    caption = "add the ndoe10Result in package/types",
                    edit = sys => sys.WriteFileNoError("/home/src/projects/project/node_modules/foo2/index.d.ts", GetTscModuleResolutionAlternateResultDts("foo2")),
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2952-2998
    [Fact]
    public void HandlesTheCacheCorrectlyWhenTwoProjectsUseDifferentModuleResolutionSettings()
    {
        new TscInput
        {
            subScenario = "handles the cache correctly when two projects use different module resolution settings",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/project1/index.ts"] = "import { foo } from \"file\";",
                ["/user/username/projects/myproject/project1/node_modules/file/index.d.ts"] = "export const foo = 10;",
                ["/user/username/projects/myproject/project1/tsconfig.json"] = StringTestUtil.Dedent("""
                       {
                           "compilerOptions": {
                               "composite": true,
                               "types": ["foo", "bar"]
                           },
                           "files": ["index.ts"],
                       }
"""),
                ["/user/username/projects/myproject/project2/index.ts"] = "import { foo } from \"file\";",
                ["/user/username/projects/myproject/project2/node_modules/file/index.d.ts"] = "export const foo = 10;",
                ["/user/username/projects/myproject/project2/tsconfig.json"] = StringTestUtil.Dedent("""
                       {
                           "compilerOptions": {
                               "composite": true,
                               "types": ["foo"],
                               "module": "nodenext",
                               "moduleResolution": "nodenext"
                           },
                           "files": ["index.ts"],
                       }
"""),
                ["/user/username/projects/myproject/node_modules/@types/foo/index.d.ts"] = "export const foo = 10;",
                ["/user/username/projects/myproject/node_modules/@types/bar/index.d.ts"] = "export const bar = 10;",
                // PORT-NOTE: literal indentation copied from the Go source (tabs converted to
                // the same number of spaces as the Go file's tab width) so Dedent matches.
                ["/user/username/projects/myproject/tsconfig.json"] = StringTestUtil.Dedent("""
                      {
                           "files": [],
                           "references": [
                               { "path": "./project1" },
                               { "path": "./project2" },
                           ],
                      }
"""),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["--b", "-w", "-v"],
            edits =
            [
                new TscEdit
                {
                    caption = "Append text",
                    edit = sys => sys.AppendFileText("/user/username/projects/myproject/project1/index.ts", "const bar = 10;"),
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:2999-3069
    [Fact]
    public void ResolvesSpecifierInOutputDeclarationFileFromReferencedProjectCorrectlyWithCtsAndMtsExtensions()
    {
        new TscInput
        {
            // !!! sheetal package.json watches not yet implemented
            subScenario = "resolves specifier in output declaration file from referenced project correctly with cts and mts extensions",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/packages/pkg1/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "pkg1",
                        "version": "1.0.0",
                        "main": "build/index.js",
                        "type": "module"
                    }
"""),
                ["/user/username/projects/myproject/packages/pkg1/index.ts"] = StringTestUtil.Dedent("""
                    import type { TheNum } from 'pkg2'
                    export const theNum: TheNum = 42;
"""),
                ["/user/username/projects/myproject/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "outDir": "build",
                            "module": "node16",
                        },
                        "references": [{ "path": "../pkg2" }],
                    }
"""),
                ["/user/username/projects/myproject/packages/pkg2/const.cts"] = "export type TheNum = 42;",
                ["/user/username/projects/myproject/packages/pkg2/index.ts"] = "export type { TheNum } from './const.cjs';",
                ["/user/username/projects/myproject/packages/pkg2/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "build",
                            "module": "node16",
                        },
                    }
"""),
                ["/user/username/projects/myproject/packages/pkg2/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "pkg2",
                        "version": "1.0.0",
                        "main": "build/index.js",
                        "type": "module"
                    }
"""),
                ["/user/username/projects/myproject/node_modules/pkg2"] = VfsTestUtil.Symlink("/user/username/projects/myproject/packages/pkg2"),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-b", "packages/pkg1", "-w", "--verbose", "--traceResolution"],
            edits =
            [
                new TscEdit
                {
                    caption = "reports import errors after change to package file",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/myproject/packages/pkg1/package.json", "\"module\"", "\"commonjs\""),
                },
                new TscEdit
                {
                    caption = "removes those errors when a package file is changed back",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/myproject/packages/pkg1/package.json", "\"commonjs\"", "\"module\""),
                },
                new TscEdit
                {
                    caption = "reports import errors after change to package file",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/myproject/packages/pkg1/package.json", "\"module\"", "\"commonjs\""),
                },
                new TscEdit
                {
                    caption = "removes those errors when a package file is changed to cjs extensions",
                    edit = sys =>
                    {
                        sys.ReplaceFileText("/user/username/projects/myproject/packages/pkg2/package.json", "\"build/index.js\"", "\"build/index.cjs\"");
                        sys.RenameFileNoError("/user/username/projects/myproject/packages/pkg2/index.ts", "/user/username/projects/myproject/packages/pkg2/index.cts");
                    },
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:3070-3123
    [Fact]
    public void BuildModeWatchesForChangesToPackageJsonMainFields()
    {
        new TscInput
        {
            subScenario = "build mode watches for changes to package-json main fields",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/packages/pkg1/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "pkg1",
                        "version": "1.0.0",
                        "main": "build/index.js"
                    }
"""),
                ["/user/username/projects/myproject/packages/pkg1/index.ts"] = StringTestUtil.Dedent("""
                    import type { TheNum } from 'pkg2'
                    export const theNum: TheNum = 42;
"""),
                ["/user/username/projects/myproject/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "outDir": "build",
                        },
                        "references": [{ "path": "../pkg2" }],
                    }
"""),
                ["/user/username/projects/myproject/packages/pkg2/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "build",
                        },
                    }
"""),
                ["/user/username/projects/myproject/packages/pkg2/const.ts"] = "export type TheNum = 42;",
                ["/user/username/projects/myproject/packages/pkg2/index.ts"] = "export type { TheNum } from './const.js';",
                ["/user/username/projects/myproject/packages/pkg2/other.ts"] = "export type TheStr = string;",
                ["/user/username/projects/myproject/packages/pkg2/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "pkg2",
                        "version": "1.0.0",
                        "main": "build/index.js"
                    }
"""),
                ["/user/username/projects/myproject/node_modules/pkg2"] = VfsTestUtil.Symlink("/user/username/projects/myproject/packages/pkg2"),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-b", "packages/pkg1", "--verbose", "-w", "--traceResolution"],
            edits =
            [
                new TscEdit
                {
                    caption = "reports import errors after change to package file",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/myproject/packages/pkg2/package.json", "index.js", "other.js"),
                },
                new TscEdit
                {
                    caption = "removes those errors when a package file is changed back",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/myproject/packages/pkg2/package.json", "other.js", "index.js"),
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:3124-3160
    [Fact]
    public void BuildModeWatchesMissingPackageJsonLookups()
    {
        new TscInput
        {
            subScenario = "build mode watches missing package-json lookups",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/packages/pkg1/index.ts"] = StringTestUtil.Dedent("""
                    import type { TheNum } from 'pkg2'
                    export const theNum: TheNum = 42;
"""),
                ["/user/username/projects/myproject/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "outDir": "build",
                        },
                    }
"""),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-b", "packages/pkg1", "-w", "--verbose", "--traceResolution"],
            edits =
            [
                new TscEdit
                {
                    caption = "resolves import after package is installed",
                    edit = sys =>
                    {
                        sys.WriteFileNoError("/user/username/projects/myproject/node_modules/pkg2/package.json", StringTestUtil.Dedent("""
                            {
                                "name": "pkg2",
                                "version": "1.0.0",
                                "types": "index.d.ts"
                            }
"""));
                        sys.WriteFileNoError("/user/username/projects/myproject/node_modules/pkg2/index.d.ts", "export type TheNum = 42;");
                    },
                },
                new TscEdit
                {
                    caption = "reports import errors after package is removed",
                    edit = sys =>
                    {
                        sys.RemoveNoError("/user/username/projects/myproject/node_modules/pkg2/package.json");
                        sys.RemoveNoError("/user/username/projects/myproject/node_modules/pkg2/index.d.ts");
                    },
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:3161-3192
    [Fact]
    public void BuildModeWatchesPackageJsonLookupsFromExistingBuildinfo()
    {
        new TscInput
        {
            subScenario = "build mode watches package-json lookups from existing buildinfo",
            files = TestSys.GetFileMapWithBuild(new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/packages/pkg1/index.ts"] = StringTestUtil.Dedent("""
                    import type { TheNum } from 'pkg2'
                    export const theNum: TheNum = 42;
"""),
                ["/user/username/projects/myproject/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "outDir": "zzbuild",
                        },
                    }
"""),
                ["/user/username/projects/myproject/node_modules/pkg2/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "pkg2",
                        "version": "1.0.0",
                        "types": "index.d.ts"
                    }
"""),
                ["/user/username/projects/myproject/node_modules/pkg2/index.d.ts"] = "export type TheNum = 42;",
            }, ["-b", "/user/username/projects/myproject/packages/pkg1", "--verbose", "--traceResolution"]),
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-b", "packages/pkg1", "-w", "--verbose", "--traceResolution"],
            edits =
            [
                new TscEdit
                {
                    caption = "reports import errors after package is removed",
                    edit = sys =>
                    {
                        sys.RemoveNoError("/user/username/projects/myproject/node_modules/pkg2/package.json");
                        sys.RemoveNoError("/user/username/projects/myproject/node_modules/pkg2/index.d.ts");
                    },
                },
            ],
        }.RunAndAssert("moduleResolution");
    }

    // tsc_test.go:3193-3248
    [Fact]
    public void ResolutionFromDtsOfReferencedProject()
    {
        new TscInput
        {
            subScenario = "resolution from d.ts of referenced project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/common.d.ts"] = "export type OnValue = (value: number) => void",
                ["/home/src/workspaces/project/producer/index.ts"] = StringTestUtil.Dedent("""
                    export { ValueProducerDeclaration } from "./in-js"
                    import { OnValue } from "@common"
                    export interface ValueProducerFromTs {
                        onValue: OnValue;
                    }
"""),
                ["/home/src/workspaces/project/producer/in-js.d.ts"] = StringTestUtil.Dedent("""
                    import { OnValue } from "@common"
                    export interface ValueProducerDeclaration {
                        onValue: OnValue;
                    }
"""),
                ["/home/src/workspaces/project/producer/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "composite": true,
                        "module": "nodenext",
                        "moduleResolution": "nodenext",
                        "paths": {
                            "@common": ["../common.d.ts"],
                        },
                    },
                }
"""),
                ["/home/src/workspaces/project/consumer/index.ts"] = StringTestUtil.Dedent("""
                    import { ValueProducerDeclaration, ValueProducerFromTs } from "@producer"
                    declare let v: ValueProducerDeclaration;
                    // n is implicitly any because onValue is actually any (despite what the tooltip says)
                    v.onValue = (n) => {
                    }
                    // n is implicitly number as expected
                    declare let v2: ValueProducerFromTs;
                    v2.onValue = (n) => {
                    }
"""),
                ["/home/src/workspaces/project/consumer/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "module": "nodenext",
                        "moduleResolution": "nodenext",
                        "paths": {
                            "@producer": ["../producer/index"],
                        },
                    },
                    "references": [
                        { "path": "../producer" },
                    ],
                }
"""),
            },
            commandLineArgs = ["--b", "consumer", "--traceResolution", "-v"],
        }.RunAndAssert("moduleResolution");
    }
}
