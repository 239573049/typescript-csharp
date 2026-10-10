// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildSample (the sample1
// core → logic → tests project: -b/-b -w, dry/clean/force, option-change, watch and incremental
// error scenarios) against the synced tsgo reference baselines under
// testdata/baselines/reference/tsbuild/sample and tsbuildWatch/sample.

using System;
using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildSampleTests
{
    // tscbuild_test.go:3413-3426 — getLogicConfig.
    private static string GetLogicConfig()
    {
        return StringTestUtil.Dedent("""

			{
				"compilerOptions": {
					"composite": true,
					"declaration": true,
					"sourceMap": true,
					"skipDefaultLibCheck": true,
				},
				"references": [
					{ "path": "../core" },
				],
			}
""");
    }

    // tscbuild_test.go:3428-3483 — getBuildSampleFileMap.
    private static Dictionary<string, object> GetBuildSampleFileMap(Action<Dictionary<string, object>>? modify)
    {
        var files = new Dictionary<string, object>
        {
            ["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

			{
				"compilerOptions": {
					"composite": true,
					"declaration": true,
					"declarationMap": true,
					"skipDefaultLibCheck": true,
				},
			}
"""),
            ["/user/username/projects/sample1/core/index.ts"] = StringTestUtil.Dedent("""

				export const someString: string = "HELLO WORLD";
				export function leftPad(s: string, n: number) { return s + n; }
				export function multiply(a: number, b: number) { return a * b; }
"""),
            ["/user/username/projects/sample1/core/some_decl.d.ts"] = "declare const dts: any;",
            ["/user/username/projects/sample1/core/anotherModule.ts"] = "export const World = \"hello\";",
            ["/user/username/projects/sample1/logic/tsconfig.json"] = GetLogicConfig(),
            ["/user/username/projects/sample1/logic/index.ts"] = StringTestUtil.Dedent("""

				import * as c from '../core/index';
				export function getSecondsInDay() {
					return c.multiply(10, 15);
				}
				import * as mod from '../core/anotherModule';
				export const m = mod;
"""),
            ["/user/username/projects/sample1/tests/tsconfig.json"] = StringTestUtil.Dedent("""

			{
				"references": [
					{ "path": "../core" },
					{ "path": "../logic" },
				],
				"files": ["index.ts"],
				"compilerOptions": {
					"composite": true,
					"declaration": true,
					"skipDefaultLibCheck": true,
				},
			}
"""),
            ["/user/username/projects/sample1/tests/index.ts"] = StringTestUtil.Dedent("""

				import * as c from '../core/index';
				import * as logic from '../logic/index';

				c.leftPad("", 10);
				logic.getSecondsInDay();

				import * as mod from '../core/anotherModule';
				export const m = mod;
"""),
        };
        modify?.Invoke(files);
        return files;
    }

    // tscbuild_test.go:3484-3540 — getStopBuildOnErrorTests.
    private static TscInput[] GetStopBuildOnErrorTests(string[]? options)
    {
        var noChange = options == null ? TscEdit.NoChangeOnlyEdit : null;
        return
        [
            new TscInput
            {
                subScenario = "skips builds downstream projects if upstream projects have errors with stopBuildOnErrors",
                files = GetBuildSampleFileMap(files =>
                {
                    var text = (string)files["/user/username/projects/sample1/core/index.ts"];
                    files["/user/username/projects/sample1/core/index.ts"] = text + """multiply();""";
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = [.. new[] { "--b", "tests", "--verbose", "--stopBuildOnErrors" }, .. options ?? []],
                edits =
                [
                    .. noChange ?? [],
                    new TscEdit
                    {
                        caption = "fix error",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/index.ts", "multiply();", ""),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "skips builds downstream projects if upstream projects have errors with stopBuildOnErrors when test does not reference core",
                files = GetBuildSampleFileMap(files =>
                {
                    files["/user/username/projects/sample1/tests/tsconfig.json"] = StringTestUtil.Dedent("""

					{
						"references": [
							{ "path": "../logic" },
						],
						"files": ["index.ts"],
						"compilerOptions": {
							"composite": true,
							"declaration": true,
							"skipDefaultLibCheck": true,
						},
					}
""");
                    var text = (string)files["/user/username/projects/sample1/core/index.ts"];
                    files["/user/username/projects/sample1/core/index.ts"] = text + """multiply();""";
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = [.. new[] { "--b", "tests", "--verbose", "--stopBuildOnErrors" }, .. options ?? []],
                edits =
                [
                    .. noChange ?? [],
                    new TscEdit
                    {
                        caption = "fix error",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/index.ts", "multiply();", ""),
                    },
                ],
            },
        ];
    }

    // tscbuild_test.go:3541-3565 — getBuildSampleCoreChangeEdits.
    private static TscEdit[] GetBuildSampleCoreChangeEdits()
    {
        return
        [
            new TscEdit
            {
                caption = "incremental-declaration-changes",
                edit = sys => sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nexport class someClass { }"),
            },
            new TscEdit
            {
                caption = "incremental-declaration-doesnt-change",
                edit = sys => sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nclass someClass2 { }"),
            },
            TscEdit.NoChange,
        ];
    }

    // tscbuild_test.go:3566-3588 — getBuildSampleWatchDtsChangingEdits.
    private static TscEdit[] GetBuildSampleWatchDtsChangingEdits()
    {
        return
        [
            new TscEdit
            {
                caption = "Make change to core",
                edit = sys => sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nexport class someClass { }"),
            },
            new TscEdit
            {
                caption = "Revert core file",
                edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/index.ts", "\nexport class someClass { }", ""),
            },
            new TscEdit
            {
                caption = "Make two changes",
                edit = sys =>
                {
                    sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nexport class someClass { }");
                    sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nexport class someClass2 { }");
                },
            },
        ];
    }

    // tscbuild_test.go:3589-3598 — getBuildSampleWatchNonDtsChangingEdits.
    private static TscEdit[] GetBuildSampleWatchNonDtsChangingEdits()
    {
        return
        [
            new TscEdit
            {
                caption = "Make local change to core",
                edit = sys => sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nfunction foo() { }"),
            },
        ];
    }

    // tscbuild_test.go:3599-3614 — getBuildSampleWatchNewFileEdits.
    private static TscEdit[] GetBuildSampleWatchNewFileEdits()
    {
        return
        [
            new TscEdit
            {
                caption = "Change to new File and build core",
                edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/core/newfile.ts", """export const newFileConst = 30;"""),
            },
            new TscEdit
            {
                caption = "Change to new File and build core",
                edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/core/newfile.ts", "\nexport class someClass2 { }"),
            },
        ];
    }

    // tscbuild_test.go:3615-3626 — makeCircularReferences.
    private static void MakeCircularReferences(Dictionary<string, object> files)
    {
        files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

		{
			"compilerOptions": {
				"composite": true,
				"declaration": true
			},
			"references": [
				{ "path": "../tests", "circular": true }
			],
		}
""");
    }

    // tscbuild_test.go:3627-3662 — getIncrementalErrorTest.
    private static TscInput GetIncrementalErrorTest(string subScenario, string[] options)
    {
        var expectedDiffWithLogicError = "";
        if (Array.IndexOf(options, "--stopBuildOnErrors") >= 0)
        {
            expectedDiffWithLogicError = StringTestUtil.Dedent("""

				Clean build will stop on error in core and will not report error in logic
				Watch build will retain previous errors from logic and report it
""");
        }
        return new TscInput
        {
            subScenario = "reportErrors " + subScenario,
            files = GetBuildSampleFileMap(null),
            cwd = "/user/username/projects/sample1",
            commandLineArgs = [.. new[] { "-b", "-w", "tests" }, .. options],
            edits =
            [
                new TscEdit
                {
                    caption = "change logic",
                    edit = sys => sys.AppendFileText("/user/username/projects/sample1/logic/index.ts", "\nlet y: string = 10;"),
                },
                new TscEdit
                {
                    caption = "change core",
                    edit = sys => sys.AppendFileText("/user/username/projects/sample1/core/index.ts", "\nlet x: string = 10;"),
                    expectedDiff = expectedDiffWithLogicError,
                },
                new TscEdit
                {
                    caption = "fix error in logic",
                    edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/logic/index.ts", "\nlet y: string = 10;", ""),
                },
            ],
        };
    }

    // Go strings.Replace(s, old, new, 1) — replaces only the first occurrence.
    private static string ReplaceFirst(string s, string oldText, string newText)
    {
        var idx = s.IndexOf(oldText, StringComparison.Ordinal);
        return idx < 0 ? s : s[..idx] + newText + s[(idx + oldText.Length)..];
    }

    // `getTestLibPathFor("esnext.full")` and `tscLibPath + "/lib.d.ts"` (sys.go:35, 62-70) — the
    // lib files the "when target option changes" scenario supplies itself.
    private static string EsNextFullLibPath => TscTestDefaults.GetTestLibPathFor("esnext.full");

    private static string LibDtsPath => TscTestDefaults.TscLibPath + "/lib.d.ts";

    // The lib content both overridden lib files get (identical in Go).
    private const string LibContent = "/// <reference no-default-lib=\"true\"/>\n/// <reference lib=\"esnext\" />";

    // tscbuild_test.go:3410-4232 — TestBuildSample.
    [Fact]
    public void BuildSample()
    {
        var testCases = new List<TscInput>
        {
            new TscInput
            {
                subScenario = "builds correctly when outDir is specified",
                files = GetBuildSampleFileMap(files => files["/user/username/projects/sample1/logic/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true,
						"declaration": true,
						"sourceMap": true,
						"outDir": "outDir",
					},
					"references": [
						{ "path": "../core" },
					],
				}
""")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests"],
            },
            new TscInput
            {
                subScenario = "builds correctly when declarationDir is specified",
                files = GetBuildSampleFileMap(files => files["/user/username/projects/sample1/logic/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true,
						"declaration": true,
						"sourceMap": true,
						"declarationDir": "out/decls",
					},
					"references": [
						{ "path": "../core" },
					],
				}
""")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests"],
            },
            new TscInput
            {
                subScenario = "builds correctly when project is not composite or doesnt have any references",
                files = GetBuildSampleFileMap(files =>
                {
                    var text = (string)files["/user/username/projects/sample1/core/tsconfig.json"];
                    files["/user/username/projects/sample1/core/tsconfig.json"] = ReplaceFirst(text, "\"composite\": true,", "");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "core", "--verbose"],
            },
            new TscInput
            {
                subScenario = "does not write any files in a dry build",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--dry"],
            },
            new TscInput
            {
                subScenario = "removes all files it built",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "removes all files it built",
                        commandLineArgs = ["--b", "tests", "--clean"],
                    },
                    new TscEdit
                    {
                        caption = "no change --clean",
                        commandLineArgs = ["--b", "tests", "--clean"],
                    },
                ],
            },
            new TscInput
            {
                subScenario = "cleaning project in not build order doesnt throw error",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "logic2", "--clean"],
            },
            new TscInput
            {
                subScenario = "always builds under with force option",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--force"],
                edits = TscEdit.NoChangeOnlyEdit,
            },
            new TscInput
            {
                subScenario = "can detect when and what to rebuild",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    TscEdit.NoChange,
                    // Update a file in the leaf node (tests), only it should rebuild the last one
                    new TscEdit
                    {
                        caption = "Only builds the leaf node project",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/tests/index.ts", "const m = 10;"),
                    },
                    // Update a file in the parent (without affecting types), should get fast downstream builds
                    new TscEdit
                    {
                        caption = "Detects type-only changes in upstream projects",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/index.ts", "HELLO WORLD", "WELCOME PLANET"),
                    },
                    new TscEdit
                    {
                        caption = "rebuilds when tsconfig changes",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/tests/tsconfig.json", "\"composite\": true", "\"composite\": true, \"target\": \"es2020\""),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "when input file text does not change but its modified time changes",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "upstream project changes without changing file text",
                        edit = sys => sys.FS().Chtimes("/user/username/projects/sample1/core/index.ts", default, sys.Now()),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "when declarationMap changes",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Disable declarationMap",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/tsconfig.json", "\"declarationMap\": true,", "\"declarationMap\": false,"),
                    },
                    new TscEdit
                    {
                        caption = "Enable declarationMap",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/tsconfig.json", "\"declarationMap\": false,", "\"declarationMap\": true,"),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "indicates that it would skip builds during a dry build",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "--dry",
                        commandLineArgs = ["--b", "tests", "--dry"],
                    },
                ],
            },
            new TscInput
            {
                subScenario = "rebuilds from start if force option is set",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "--force build",
                        commandLineArgs = ["--b", "tests", "--verbose", "--force"],
                    },
                ],
            },
            new TscInput
            {
                subScenario = "tsbuildinfo has error",
                files = new Dictionary<string, object>
                {
                    ["/home/src/workspaces/project/main.ts"] = "export const x = 10;",
                    ["/home/src/workspaces/project/tsconfig.json"] = "{}",
                    ["/home/src/workspaces/project/tsconfig.tsbuildinfo"] = "Some random string",
                },
                commandLineArgs = ["--b", "-i", "-v"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "tsbuildinfo written has error",
                        edit = sys =>
                        {
                            // This is to ensure the non incremental doesnt crash - as it wont have tsbuildInfo
                            if (!sys.forIncrementalCorrectness)
                            {
                                sys.PrependFileText("/home/src/workspaces/project/tsconfig.tsbuildinfo", "Some random string");
                                sys.ReplaceFileText(
                                    "/home/src/workspaces/project/tsconfig.tsbuildinfo",
                                    $"\"version\":\"{Ts.Core.TsVersion.Version()}\"",
                                    $"\"version\":\"{Harness.FakeTSVersion}\""); // build info won't parse, need to manually sterilize for baseline
                            }
                        },
                    },
                ],
            },
            new TscInput
            {
                subScenario = "rebuilds completely when version in tsbuildinfo doesnt match ts version",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "convert tsbuildInfo version to something that is say to previous version",
                        edit = sys =>
                        {
                            // This is to ensure the non incremental doesnt crash - as it wont have tsbuildInfo
                            if (!sys.forIncrementalCorrectness)
                            {
                                sys.ReplaceFileText(
                                    "/user/username/projects/sample1/core/tsconfig.tsbuildinfo",
                                    $"\"version\":\"{Harness.FakeTSVersion}\"",
                                    $"\"version\":\"{"FakeTsPreviousVersion"}\"");
                                sys.ReplaceFileText(
                                    "/user/username/projects/sample1/logic/tsconfig.tsbuildinfo",
                                    $"\"version\":\"{Harness.FakeTSVersion}\"",
                                    $"\"version\":\"{"FakeTsPreviousVersion"}\"");
                                sys.ReplaceFileText(
                                    "/user/username/projects/sample1/tests/tsconfig.tsbuildinfo",
                                    $"\"version\":\"{Harness.FakeTSVersion}\"",
                                    $"\"version\":\"{"FakeTsPreviousVersion"}\"");
                            }
                        },
                    },
                ],
            },
            new TscInput
            {
                subScenario = "rebuilds when extended config file changes",
                files = GetBuildSampleFileMap(files =>
                {
                    files["/user/username/projects/sample1/tests/tsconfig.base.json"] = StringTestUtil.Dedent("""

					{
						"compilerOptions": {
							"target": "es5"
						}
					}
""");
                    var text = (string)files["/user/username/projects/sample1/tests/tsconfig.json"];
                    files["/user/username/projects/sample1/tests/tsconfig.json"] = ReplaceFirst(text, "\"references\": [", "\"extends\": \"./tsconfig.base.json\", \"references\": [");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "change extended file",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/tests/tsconfig.base.json", StringTestUtil.Dedent("""

						{
							"compilerOptions": { }
						}
""")),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "building project in not build order doesnt throw error",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "logic2/tsconfig.json", "--verbose"],
            },
            new TscInput
            {
                subScenario = "builds downstream projects even if upstream projects have errors",
                files = GetBuildSampleFileMap(files =>
                {
                    var text = (string)files["/user/username/projects/sample1/logic/index.ts"];
                    files["/user/username/projects/sample1/logic/index.ts"] = ReplaceFirst(text, "c.multiply(10, 15)", """c.muitply()""");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits = TscEdit.NoChangeOnlyEdit,
            },
            new TscInput
            {
                subScenario = "listFiles",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--listFiles"],
                edits = GetBuildSampleCoreChangeEdits(),
            },
            new TscInput
            {
                subScenario = "listEmittedFiles",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--listEmittedFiles"],
                edits = GetBuildSampleCoreChangeEdits(),
            },
            new TscInput
            {
                subScenario = "explainFiles",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--explainFiles", "--v"],
                edits = GetBuildSampleCoreChangeEdits(),
            },
            new TscInput
            {
                subScenario = "sample",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    .. GetBuildSampleCoreChangeEdits(),
                    new TscEdit
                    {
                        caption = "when logic config changes declaration dir",
                        edit = sys => sys.ReplaceFileText(
                            "/user/username/projects/sample1/logic/tsconfig.json",
                            "\"declaration\": true,",
                            "\"declaration\": true,\n        \"declarationDir\": \"decls\","),
                    },
                    TscEdit.NoChange,
                ],
            },
            new TscInput
            {
                subScenario = "when logic specifies tsBuildInfoFile",
                files = GetBuildSampleFileMap(files =>
                {
                    var text = (string)files["/user/username/projects/sample1/logic/tsconfig.json"];
                    files["/user/username/projects/sample1/logic/tsconfig.json"] = ReplaceFirst(text, "\"composite\": true,", "\"composite\": true,\n    \"tsBuildInfoFile\": \"ownFile.tsbuildinfo\",");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
            },
            new TscInput
            {
                subScenario = "when declaration option changes",
                files = GetBuildSampleFileMap(files => files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"incremental": true,
						"skipDefaultLibCheck": true,
					},
				}
""")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "core", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "incremental-declaration-changes",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/tsconfig.json", "\"incremental\": true,", "\"incremental\": true, \"declaration\": true,"),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "when target option changes",
                files = GetBuildSampleFileMap(files =>
                {
                    files[EsNextFullLibPath] = LibContent;
                    files[LibDtsPath] = LibContent;
                    files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"incremental": true,
						"listFiles": true,
						"listEmittedFiles": true,
						"target": "esnext",
					},
				}
""");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "core", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "incremental-declaration-changes",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/tsconfig.json", """esnext""", """es5"""),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "when module option changes",
                files = GetBuildSampleFileMap(files => files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"incremental": true,
						"module": "node18",
					},
				}
""")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "core", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "incremental-declaration-changes",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/core/tsconfig.json", """node18""", """nodenext"""),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "when esModuleInterop option changes",
                files = GetBuildSampleFileMap(files => files["/user/username/projects/sample1/tests/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"references": [
						{ "path": "../core" },
						{ "path": "../logic" },
					],
					"files": ["index.ts"],
					"compilerOptions": {
						"composite": true,
						"declaration": true,
						"skipDefaultLibCheck": true,
						"esModuleInterop": false,
					},
				}
""")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "incremental-declaration-changes",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/tests/tsconfig.json", "\"esModuleInterop\": false", "\"esModuleInterop\": true"),
                    },
                ],
            },
            // !!! sheetal this is not reporting error as file not found is not yet implemented
            new TscInput
            {
                subScenario = "reports error if input file is missing",
                files = GetBuildSampleFileMap(files =>
                {
                    files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					 "compilerOptions": { "composite": true },
					 "files": ["anotherModule.ts", "index.ts", "some_decl.d.ts"],
				}
""");
                    files.Remove("/user/username/projects/sample1/core/anotherModule.ts");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose"],
            },
            // !!! sheetal this is not reporting error as file not found is not yet implemented
            new TscInput
            {
                subScenario = "reports error if input file is missing with force",
                files = GetBuildSampleFileMap(files =>
                {
                    files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					 "compilerOptions": { "composite": true },
					 "files": ["anotherModule.ts", "index.ts", "some_decl.d.ts"],
				}
""");
                    files.Remove("/user/username/projects/sample1/core/anotherModule.ts");
                }),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "tests", "--verbose", "--force"],
            },
            new TscInput
            {
                subScenario = "change builds changes and reports found errors message",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits = GetBuildSampleWatchDtsChangingEdits(),
            },
            new TscInput
            {
                subScenario = "non local change does not start build of referencing projects",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits = GetBuildSampleWatchNonDtsChangingEdits(),
            },
            new TscInput
            {
                subScenario = "builds when new file is added, and its subsequent updates",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits = GetBuildSampleWatchNewFileEdits(),
            },
            new TscInput
            {
                subScenario = "change builds changes and reports found errors message with circular references",
                files = GetBuildSampleFileMap(MakeCircularReferences),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits = GetBuildSampleWatchDtsChangingEdits(),
            },
            new TscInput
            {
                subScenario = "non local change does not start build of referencing projects with circular references",
                files = GetBuildSampleFileMap(MakeCircularReferences),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits = GetBuildSampleWatchNonDtsChangingEdits(),
            },
            new TscInput
            {
                subScenario = "builds when new file is added, and its subsequent updates with circular references",
                files = GetBuildSampleFileMap(MakeCircularReferences),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits = GetBuildSampleWatchNewFileEdits(),
            },
            new TscInput
            {
                subScenario = "watches config files that are not present",
                files = GetBuildSampleFileMap(files => files.Remove("/user/username/projects/sample1/logic/tsconfig.json")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Write logic",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/logic/tsconfig.json", GetLogicConfig()),
                    },
                ],
            },
            GetIncrementalErrorTest("when preserveWatchOutput is not used", []),
            GetIncrementalErrorTest("when preserveWatchOutput is passed on command line", ["--preserveWatchOutput"]),
            GetIncrementalErrorTest("when stopBuildOnErrors is passed on command line", ["--stopBuildOnErrors"]),
            new TscInput
            {
                subScenario = "incremental updates in verbose mode",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "tests", "--verbose"],
                edits =
                [
                    new TscEdit
                    {
                        caption = "Make non dts change",
                        edit = sys => sys.AppendFileText("/user/username/projects/sample1/logic/index.ts", "\nfunction someFn() { }"),
                    },
                    new TscEdit
                    {
                        caption = "Make dts change",
                        edit = sys => sys.ReplaceFileText("/user/username/projects/sample1/logic/index.ts", "\nfunction someFn() { }", "\nexport function someFn() { }"),
                    },
                ],
            },
            new TscInput
            {
                subScenario = "should not trigger recompilation because of program emit",
                files = GetBuildSampleFileMap(null),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "core", "--verbose"],
                edits =
                [
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "Add new file",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/core/file3.ts", """export const y = 10;"""),
                    },
                    TscEdit.NoChange,
                ],
            },
            new TscInput
            {
                subScenario = "should not trigger recompilation because of program emit with outDir specified",
                files = GetBuildSampleFileMap(files => files["/user/username/projects/sample1/core/tsconfig.json"] = StringTestUtil.Dedent("""

				{
					"compilerOptions": {
						"composite": true,
						"outDir": "outDir"
					}
                }
""")),
                cwd = "/user/username/projects/sample1",
                commandLineArgs = ["--b", "-w", "core", "--verbose"],
                edits =
                [
                    TscEdit.NoChange,
                    new TscEdit
                    {
                        caption = "Add new file",
                        edit = sys => sys.WriteFileNoError("/user/username/projects/sample1/core/file3.ts", """export const y = 10;"""),
                    },
                    TscEdit.NoChange,
                ],
            },
        };
        testCases.AddRange(GetStopBuildOnErrorTests(null));
        testCases.AddRange(GetStopBuildOnErrorTests(["--watch"]));

        foreach (var test in testCases)
        {
            test.RunAndAssert("sample");
        }
    }
}
