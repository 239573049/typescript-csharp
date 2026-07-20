// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildLateBoundSymbol +
// TestBuildReexport (single-scenario groups).

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildMiscTests
{
    // tscbuild_test.go:1780-1835
    [Fact]
    public void LateBoundSymbol_InterfaceIsMergedAndContainsLateBoundMember()
    {
        new TscInput
        {
            subScenario = "interface is merged and contains late bound member",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/globals.d.ts"] = StringTestUtil.Dedent("""
                    interface SymbolConstructor {
                        (description?: string | number): symbol;
                    }
                    declare var Symbol: SymbolConstructor;
"""),
                ["/home/src/workspaces/project/src/hkt.ts"] = "export interface HKT<T> { }",
                ["/home/src/workspaces/project/src/main.ts"] = StringTestUtil.Dedent("""
                    import { HKT } from "./hkt";

                    const sym = Symbol();

                    declare module "./hkt" {
                        interface HKT<T> {
                            [sym]: { a: T }
                        }
                    }
                    const x = 10;
                    type A = HKT<number>[typeof sym];
"""),
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "rootDir": "src",
                        "incremental": true,
                    },
                }
"""),
            },
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "incremental-declaration-doesnt-change",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/src/main.ts", "const x = 10;", ""),
                },
                new TscEdit
                {
                    caption = "incremental-declaration-doesnt-change",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/src/main.ts", "const x = 10;"),
                },
            ],
        }.RunAndAssert("lateBoundSymbol");
    }

    // tscbuild_test.go:2916-2983
    [Fact]
    public void Reexport_ReportsErrorsCorrectly()
    {
        new TscInput
        {
            subScenario = "Reports errors correctly",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/reexport/src/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "files": [],
                    "include": [],
                    "references": [{ "path": "./pure" }, { "path": "./main" }],
                }
"""),
                ["/user/username/projects/reexport/src/main/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "outDir": "../../out",
                        "rootDir": "../",
                    },
                    "include": ["**/*.ts"],
                    "references": [{ "path": "../pure" }],
                }
"""),
                ["/user/username/projects/reexport/src/main/index.ts"] = StringTestUtil.Dedent("""
                    import { Session } from "../pure";

                    export const session: Session = {
                        foo: 1
                    };
"""),
                ["/user/username/projects/reexport/src/pure/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "compilerOptions": {
                        "composite": true,
                        "outDir": "../../out",
                        "rootDir": "../",
                    },
                    "include": ["**/*.ts"],
                }
"""),
                ["/user/username/projects/reexport/src/pure/index.ts"] = "export * from \"./session\";",
                ["/user/username/projects/reexport/src/pure/session.ts"] = StringTestUtil.Dedent("""
                    export interface Session {
                        foo: number;
                        // bar: number;
                    }
"""),
            },
            cwd = "/user/username/projects/reexport",
            commandLineArgs = ["-b", "-w", "-verbose", "src"],
            edits =
            [
                new TscEdit
                {
                    caption = "Introduce error",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/reexport/src/pure/session.ts", "// ", ""),
                },
                new TscEdit
                {
                    caption = "Fix error",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/reexport/src/pure/session.ts", "bar: ", "// bar: "),
                },
            ],
        }.RunAndAssert("reexport");
    }
}
