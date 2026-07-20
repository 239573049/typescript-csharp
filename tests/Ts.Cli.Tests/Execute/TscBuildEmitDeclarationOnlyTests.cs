// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildEmitDeclarationOnly.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildEmitDeclarationOnlyTests
{
    // tscbuild_test.go:813-922
    private static Dictionary<string, object> GetImportFileMap(bool declarationMap, bool circularRef)
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/src/a.ts"] = StringTestUtil.Dedent("""
				import { B } from "./b";

				export interface A {
					b: B;
				}
"""),
            ["/home/src/workspaces/project/src/b.ts"] = StringTestUtil.Dedent("""
				import { C } from "./c";

				export interface B {
					b: C;
				}
"""),
            ["/home/src/workspaces/project/src/c.ts"] = StringTestUtil.Dedent("""
				import { A } from "./a";

				export interface C {
					a: A;
				}
"""),
            ["/home/src/workspaces/project/src/index.ts"] = StringTestUtil.Dedent("""
				export { A } from "./a";
				export { B } from "./b";
				export { C } from "./c";
"""),
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
				{
					"compilerOptions": {
						"incremental": true,
						"target": "es5",
						"module": "commonjs",
						"declaration": true,
						"declarationMap": {{(declarationMap ? "true" : "false")}},
						"sourceMap": true,
						"outDir": "./lib",
						"composite": true,
						"strict": true,
						"esModuleInterop": true,
						"alwaysStrict": true,
						"rootDir": "src",
						"emitDeclarationOnly": true,
					},
				}
"""),
        };
        if (!circularRef)
        {
            files.Remove("/home/src/workspaces/project/src/index.ts");
            files["/home/src/workspaces/project/src/a.ts"] = StringTestUtil.Dedent("""
				export class B { prop = "hello"; }

				export interface A {
					b: B;
				}
""");
        }
        return files;
    }

    private static TscInput GetCircularTestCase(bool declarationMap) => new()
    {
        subScenario = "only dts output in circular import project with emitDeclarationOnly" + (declarationMap ? " and declarationMap" : ""),
        files = GetImportFileMap(declarationMap, true),
        commandLineArgs = ["--b", "--verbose"],
        edits =
        [
            new TscEdit
            {
                caption = "incremental-declaration-changes",
                edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/src/a.ts", "b: B;", "b: B; foo: any;"),
            },
        ],
    };

    [Fact]
    public void CircularImport() => GetCircularTestCase(false).RunAndAssert("emitDeclarationOnly");

    [Fact]
    public void CircularImportWithDeclarationMap() => GetCircularTestCase(true).RunAndAssert("emitDeclarationOnly");

    [Fact]
    public void NonCircularImports()
    {
        new TscInput
        {
            subScenario = "only dts output in non circular imports project with emitDeclarationOnly",
            files = GetImportFileMap(true, false),
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "incremental-declaration-doesnt-change",
                    edit = sys => sys.ReplaceFileText(
                        "/home/src/workspaces/project/src/a.ts",
                        "export interface A {",
                        StringTestUtil.Dedent("""
								class C { }
								export interface A {
""")),
                },
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/src/a.ts", "b: B;", "b: B; foo: any;"),
                },
            ],
        }.RunAndAssert("emitDeclarationOnly");
    }
}
