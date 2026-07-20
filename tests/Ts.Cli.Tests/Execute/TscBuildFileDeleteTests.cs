// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildFileDelete.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildFileDeleteTests
{
    // tscbuild_test.go:924-1008
    [Fact]
    public void DetectsDeletedFile()
    {
        new TscInput
        {
            subScenario = "detects deleted file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/child/child.ts"] = StringTestUtil.Dedent("""
					import { child2 } from "../child/child2";
					export function child() {
						child2();
					}
"""),
                ["/home/src/workspaces/solution/child/child2.ts"] = StringTestUtil.Dedent("""
					export function child2() {
					}
"""),
                ["/home/src/workspaces/solution/child/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": { "composite": true }
					}
"""),
                ["/home/src/workspaces/solution/main/main.ts"] = StringTestUtil.Dedent("""
                    import { child } from "../child/child";
                    export function main() {
                        child();
                    }
"""),
                ["/home/src/workspaces/solution/main/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": { "composite": true },
						"references": [{ "path": "../child" }],
					}
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "main/tsconfig.json", "-v", "--traceResolution", "--explainFiles"],
            edits =
            [
                new TscEdit
                {
                    caption = "delete child2 file",
                    edit = sys =>
                    {
                        sys.RemoveNoError("/home/src/workspaces/solution/child/child2.ts");
                        sys.RemoveNoError("/home/src/workspaces/solution/child/child2.js");
                        sys.RemoveNoError("/home/src/workspaces/solution/child/child2.d.ts");
                    },
                },
            ],
        }.RunAndAssert("fileDelete");
    }

    [Fact]
    public void DeletedFileWithoutComposite()
    {
        new TscInput
        {
            subScenario = "deleted file without composite",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/child/child.ts"] = StringTestUtil.Dedent("""
					import { child2 } from "../child/child2";
					export function child() {
						child2();
					}
"""),
                ["/home/src/workspaces/solution/child/child2.ts"] = StringTestUtil.Dedent("""
					export function child2() {
					}
"""),
                ["/home/src/workspaces/solution/child/tsconfig.json"] = StringTestUtil.Dedent("""
					{
						"compilerOptions": { }
					}
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "child/tsconfig.json", "-v", "--traceResolution", "--explainFiles"],
            edits =
            [
                new TscEdit
                {
                    caption = "delete child2 file",
                    edit = sys =>
                    {
                        sys.RemoveNoError("/home/src/workspaces/solution/child/child2.ts");
                        sys.RemoveNoError("/home/src/workspaces/solution/child/child2.js");
                    },
                },
            ],
        }.RunAndAssert("fileDelete");
    }
}
