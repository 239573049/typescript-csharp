// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildCommandLine.

using System.Collections.Generic;
using System.Linq;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildCommandLineTests
{
    // tscbuild_test.go:21-34
    private static Dictionary<string, object> GetBuildCommandLineDifferentOptionsMap(string optionName)
    {
        return new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
				{
					"compilerOptions": {
						"{{optionName}}": true
					}
				}
"""),
            ["/home/src/workspaces/project/a.ts"] = "export const a = 10;const aLocal = 10;",
            ["/home/src/workspaces/project/b.ts"] = "export const b = 10;const bLocal = 10;",
            ["/home/src/workspaces/project/c.ts"] = "import { a } from \"./a\";export const c = a;",
            ["/home/src/workspaces/project/d.ts"] = "import { b } from \"./b\";export const d = b;",
        };
    }

    // tscbuild_test.go:35-57
    private static Dictionary<string, object> GetBuildCommandLineEmitDeclarationOnlyMap(string[] options)
    {
        var compilerOptionsStr = string.Join(", ", options.Select(opt => $"\"{opt}\": true"));
        return new Dictionary<string, object>
        {
            ["/home/src/workspaces/solution/project1/src/tsconfig.json"] = StringTestUtil.Dedent($$"""
				{
					"compilerOptions": { {{compilerOptionsStr}} }
				}
"""),
            ["/home/src/workspaces/solution/project1/src/a.ts"] = "export const a = 10;const aLocal = 10;",
            ["/home/src/workspaces/solution/project1/src/b.ts"] = "export const b = 10;const bLocal = 10;",
            ["/home/src/workspaces/solution/project1/src/c.ts"] = "import { a } from \"./a\";export const c = a;",
            ["/home/src/workspaces/solution/project1/src/d.ts"] = "import { b } from \"./b\";export const d = b;",
            ["/home/src/workspaces/solution/project2/src/tsconfig.json"] = StringTestUtil.Dedent($$"""
				{
					"compilerOptions": { {{compilerOptionsStr}} },
					"references": [{ "path": "../../project1/src" }]
				}
"""),
            ["/home/src/workspaces/solution/project2/src/e.ts"] = "export const e = 10;",
            ["/home/src/workspaces/solution/project2/src/f.ts"] = "import { a } from \"../../project1/src/a\"; export const f = a;",
            ["/home/src/workspaces/solution/project2/src/g.ts"] = "import { b } from \"../../project1/src/b\"; export const g = b;",
        };
    }

    // tscbuild_test.go:58-144
    private static TscInput[] GetBuildCommandLineEmitDeclarationOnlyTestCases(string[] options, string suffix)
    {
        return
        [
            new TscInput
            {
                subScenario = "emitDeclarationOnly on commandline" + suffix,
                files = GetBuildCommandLineEmitDeclarationOnlyMap(options),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "project2/src", "--verbose", "--emitDeclarationOnly"],
                edits =
                [
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "local change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/a.ts", "const aa = 10;"),
                    },
                    new TscEdit
                    {
                        caption = "non local change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/a.ts", "export const aaa = 10;"),
                    },
                    new TscEdit
                    {
                        caption = "emit js files",
                        commandLineArgs = ["--b", "project2/src", "--verbose"],
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "js emit with change without emitDeclarationOnly",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/b.ts", "const alocal = 10;"),
                        commandLineArgs = ["--b", "project2/src", "--verbose"],
                    },
                    new TscEdit
                    {
                        caption = "local change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/b.ts", "const aaaa = 10;"),
                    },
                    new TscEdit
                    {
                        caption = "non local change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/b.ts", "export const aaaaa = 10;"),
                    },
                    new TscEdit
                    {
                        caption = "js emit with change without emitDeclarationOnly",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/b.ts", "export const a2 = 10;"),
                        commandLineArgs = ["--b", "project2/src", "--verbose"],
                    },
                ],
            },
            new TscInput
            {
                subScenario = "emitDeclarationOnly false on commandline" + suffix,
                files = GetBuildCommandLineEmitDeclarationOnlyMap([.. options, "emitDeclarationOnly"]),
                cwd = "/home/src/workspaces/solution",
                commandLineArgs = ["--b", "project2/src", "--verbose"],
                edits =
                [
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/a.ts", "const aa = 10;"),
                    },
                    new TscEdit
                    {
                        caption = "emit js files",
                        commandLineArgs = ["--b", "project2/src", "--verbose", "--emitDeclarationOnly", "false"],
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "no change run with js emit",
                        commandLineArgs = ["--b", "project2/src", "--verbose", "--emitDeclarationOnly", "false"],
                    },
                    new TscEdit
                    {
                        caption = "js emit with change",
                        edit = sys => sys.AppendFileText("/home/src/workspaces/solution/project1/src/b.ts", "const blocal = 10;"),
                        commandLineArgs = ["--b", "project2/src", "--verbose", "--emitDeclarationOnly", "false"],
                    },
                ],
            },
        ];
    }

    // tscbuild_test.go:19-298 — TestBuildCommandLine.
    [Fact]
    public void BuildCommandLine()
    {
        var testCases = new List<TscInput>
        {
            new TscInput
            {
                subScenario = "included tsconfig json can be imported as json input",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/index.ts"] = """
import tsconfig from "./tsconfig.json" with { type: "json" };
declare global {
    interface ImportAttributes {
        type: "json";
    }
}
console.log(tsconfig);
""",
                    ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
						{
							"compilerOptions": {
								"module": "preserve",
								"moduleResolution": "bundler",
								"noEmit": true,
								"resolveJsonModule": true,
								"strict": true,
								"target": "esnext"
							},
							"include": ["index.ts", "tsconfig.json"]
						}
"""),
                },
                commandLineArgs = ["--build"],
            },
            new TscInput
            {
                subScenario = "help",
                files = new Dictionary<string, object>(),
                commandLineArgs = ["--build", "--help"],
            },
            new TscInput
            {
                subScenario = "locale",
                files = new Dictionary<string, object>(),
                commandLineArgs = ["--build", "--help", "--locale", "en"],
            },
            new TscInput
            {
                subScenario = "bad locale",
                files = new Dictionary<string, object>(),
                commandLineArgs = ["--build", "--help", "--locale", "whoops"],
            },
            new TscInput
            {
                subScenario = "different options",
                files = GetBuildCommandLineDifferentOptionsMap("composite"),
                commandLineArgs = ["--build", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "with sourceMap",
                        commandLineArgs = ["--build", "--verbose", "--sourceMap"],
                    },
                    new TscEdit
                    {
                        caption = "should re-emit only js so they dont contain sourcemap",
                    },
                    new TscEdit
                    {
                        caption = "with declaration should not emit anything",
                        commandLineArgs = ["--build", "--verbose", "--declaration"],
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "with declaration and declarationMap",
                        commandLineArgs = ["--build", "--verbose", "--declaration", "--declarationMap"],
                    },
                    new TscEdit
                    {
                        caption = "should re-emit only dts so they dont contain sourcemap",
                    },
                    new TscEdit
                    {
                        caption = "with emitDeclarationOnly should not emit anything",
                        commandLineArgs = ["--build", "--verbose", "--emitDeclarationOnly"],
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "local change",
                        edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/a.ts", "Local = 1", "Local = 10"),
                    },
                    new TscEdit
                    {
                        caption = "with declaration should not emit anything",
                        commandLineArgs = ["--build", "--verbose", "--declaration"],
                    },
                    new TscEdit
                    {
                        caption = "with inlineSourceMap",
                        commandLineArgs = ["--build", "--verbose", "--inlineSourceMap"],
                    },
                    new TscEdit
                    {
                        caption = "with sourceMap",
                        commandLineArgs = ["--build", "--verbose", "--sourceMap"],
                    },
                ],
            },
            new TscInput
            {
                subScenario = "different options with incremental",
                files = GetBuildCommandLineDifferentOptionsMap("incremental"),
                commandLineArgs = ["--build", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "with sourceMap",
                        commandLineArgs = ["--build", "--verbose", "--sourceMap"],
                    },
                    new TscEdit
                    {
                        caption = "should re-emit only js so they dont contain sourcemap",
                    },
                    new TscEdit
                    {
                        caption = "with declaration, emit Dts and should not emit js",
                        commandLineArgs = ["--build", "--verbose", "--declaration"],
                    },
                    new TscEdit
                    {
                        caption = "with declaration and declarationMap",
                        commandLineArgs = ["--build", "--verbose", "--declaration", "--declarationMap"],
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "local change",
                        edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/a.ts", "Local = 1", "Local = 10"),
                    },
                    new TscEdit
                    {
                        caption = "with declaration and declarationMap",
                        commandLineArgs = ["--build", "--verbose", "--declaration", "--declarationMap"],
                    },
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "with inlineSourceMap",
                        commandLineArgs = ["--build", "--verbose", "--inlineSourceMap"],
                    },
                    new TscEdit
                    {
                        caption = "with sourceMap",
                        commandLineArgs = ["--build", "--verbose", "--sourceMap"],
                    },
                    new TscEdit
                    {
                        caption = "emit js files",
                    },
                    new TscEdit
                    {
                        caption = "with declaration and declarationMap",
                        commandLineArgs = ["--build", "--verbose", "--declaration", "--declarationMap"],
                    },
                    new TscEdit
                    {
                        caption = "with declaration and declarationMap, should not re-emit",
                        commandLineArgs = ["--build", "--verbose", "--declaration", "--declarationMap"],
                    },
                ],
            },
        };

        testCases.AddRange(GetBuildCommandLineEmitDeclarationOnlyTestCases(["composite"], ""));
        testCases.AddRange(GetBuildCommandLineEmitDeclarationOnlyTestCases(["incremental", "declaration"], " with declaration and incremental"));
        testCases.AddRange(GetBuildCommandLineEmitDeclarationOnlyTestCases(["declaration"], " with declaration"));

        // tscbuild_test.go:295-297
        foreach (var test in testCases)
        {
            test.RunAndAssert("commandLine");
        }
    }
}
