// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildJavascriptProjectEmit.
// Baselines: testdata/baselines/reference/tsbuild/javascriptProjectEmit.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildJavascriptProjectEmitTests
{
    // tscbuild_test.go:1596-1686
    [Fact]
    public void LoadsJsBasedProjectsAndEmitsThemCorrectly()
    {
        var files = new Dictionary<string, object>
        {
            // !!! sheetal errors seem different
            ["/home/src/workspaces/solution/common/nominal.js"] = StringTestUtil.Dedent("""
                    /**
                     * @template T, Name
                     * @typedef {T & {[Symbol.species]: Name}} Nominal
                     */
                    module.exports = {};
"""),
            ["/home/src/workspaces/solution/common/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"extends": "../tsconfig.base.json",
						"compilerOptions": {
							"composite": true,
						},
						"include": ["nominal.js"],
					}
"""),
            ["/home/src/workspaces/solution/sub-project/index.js"] = StringTestUtil.Dedent("""
                    import { Nominal } from '../common/nominal';

                    /**
                     * @typedef {Nominal<string, 'MyNominal'>} MyNominal
                     */
"""),
            ["/home/src/workspaces/solution/sub-project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig.base.json",
					"compilerOptions": {
						"composite": true,
					},
					"references": [
						{ "path": "../common" },
					],
					"include": ["./index.js"],
				}
"""),
            ["/home/src/workspaces/solution/sub-project-2/index.js"] = StringTestUtil.Dedent("""
                    import { MyNominal } from '../sub-project/index';

                    const variable = {
                        key: /** @type {MyNominal} */('value'),
                    };

                    /**
                     * @return {keyof typeof variable}
                     */
                    export function getVar() {
                        return 'key';
                    }
"""),
            ["/home/src/workspaces/solution/sub-project-2/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "extends": "../tsconfig.base.json",
                    "compilerOptions": {
                        "composite": true,
                    },
                    "references": [
                        { "path": "../sub-project" },
                    ],
                    "include": ["./index.js"],
                }
"""),
            ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "composite": true,
                    },
                    "references": [
                        { "path": "./sub-project" },
                        { "path": "./sub-project-2" },
                    ],
                    "include": [],
                }
"""),
            ["/home/src/workspaces/solution/tsconfig.base.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "skipLibCheck": true,
                        "rootDir": "./",
                        "outDir": "../lib",
                        "allowJs": true,
                        "checkJs": true,
                        "declaration": true,
                    },
                }
"""),
        };
        // tscbuild_test.go:1682
        var libDts = TscTestDefaults.TscLibPath + "/lib.d.ts";
        files[libDts] = TscTestDefaults.TscDefaultLibContent.Replace(
            "interface SymbolConstructor {",
            "interface SymbolConstructor {\n    readonly species: symbol;",
            System.StringComparison.Ordinal);

        new TscInput
        {
            subScenario = "loads js-based projects and emits them correctly",
            files = files,
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b"],
        }.RunAndAssert("javascriptProjectEmit");
    }

    // tscbuild_test.go:1687-1772
    [Fact]
    public void LoadsJsBasedProjectsWithNonMovedJsonFilesAndEmitsThemCorrectly()
    {
        new TscInput
        {
            subScenario = "loads js-based projects with non-moved json files and emits them correctly",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/common/obj.json"] = StringTestUtil.Dedent("""
				{
                    "val": 42,
                }
"""),
                ["/home/src/workspaces/solution/common/index.ts"] = StringTestUtil.Dedent("""
                    import x = require("./obj.json");
                    export = x;
"""),
                ["/home/src/workspaces/solution/common/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "extends": "../tsconfig.base.json",
                    "compilerOptions": {
                        "outDir": null,
                        "composite": true,
                    },
                    "include": ["index.ts", "obj.json"],
                }
"""),
                ["/home/src/workspaces/solution/sub-project/index.js"] = StringTestUtil.Dedent("""
                    import mod from '../common';

                    export const m = mod;
"""),
                ["/home/src/workspaces/solution/sub-project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "extends": "../tsconfig.base.json",
                    "compilerOptions": {
                        "composite": true,
                    },
                    "references": [
                        { "path": "../common" },
                    ],
                    "include": ["./index.js"],
                }
"""),
                ["/home/src/workspaces/solution/sub-project-2/index.js"] = StringTestUtil.Dedent("""
                    import { m } from '../sub-project/index';

                    const variable = {
                        key: m,
                    };

                    export function getVar() {
                        return variable;
                    }
"""),
                ["/home/src/workspaces/solution/sub-project-2/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"extends": "../tsconfig.base.json",
					"compilerOptions": {
						"composite": true,
					},
                    "references": [
                        { "path": "../sub-project" },
                    ],
                    "include": ["./index.js"],
                }
"""),
                ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
				{
					"compilerOptions": {
						"composite": true,
					},
					"references": [
						{ "path": "./sub-project" },
						{ "path": "./sub-project-2" },
                    ],
                    "include": [],
                }
"""),
                ["/home/src/workspaces/solution/tsconfig.base.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "skipLibCheck": true,
                        "rootDir": "./",
                        "outDir": "../out",
                        "allowJs": true,
                        "checkJs": true,
                        "resolveJsonModule": true,
                        "esModuleInterop": true,
                        "declaration": true,
                    },
                }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["-b"],
        }.RunAndAssert("javascriptProjectEmit");
    }
}
