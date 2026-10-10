// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscExtends.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscExtendsTests
{
    // tsc_test.go:954-993
    private static Dictionary<string, object> GetBuildConfigFileExtendsFileMap()
    {
        return new Dictionary<string, object>
        {
            ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "references": [
                    { "path": "./shared/tsconfig.json" },
                    { "path": "./webpack/tsconfig.json" },
                ],
                "files": [],
            }
"""),
            ["/home/src/workspaces/solution/shared/tsconfig-base.json"] = StringTestUtil.Dedent("""
            {
                "include": ["./typings-base/"],
            }
"""),
            ["/home/src/workspaces/solution/shared/typings-base/globals.d.ts"] = "type Unrestricted = any;",
            ["/home/src/workspaces/solution/shared/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "./tsconfig-base.json",
                "compilerOptions": {
                    "composite": true,
                    "outDir": "../target-tsc-build/",
                    "rootDir": "..",
                },
                "files": ["./index.ts"],
            }
"""),
            ["/home/src/workspaces/solution/shared/index.ts"] = "export const a: Unrestricted = 1;",
            ["/home/src/workspaces/solution/webpack/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "../shared/tsconfig-base.json",
                "compilerOptions": {
                    "composite": true,
                    "outDir": "../target-tsc-build/",
                    "rootDir": "..",
                },
                "files": ["./index.ts"],
                "references": [{ "path": "../shared/tsconfig.json" }],
            }
"""),
            ["/home/src/workspaces/solution/webpack/index.ts"] = "export const b: Unrestricted = 1;",
        };
    }

    // tsc_test.go:994-1024
    private static TscInput GetTscExtendsWithSymlinkTestCase(string builtType)
    {
        return new TscInput
        {
            subScenario = "resolves the symlink path",
            files = new Dictionary<string, object>
            {
                ["/users/user/projects/myconfigs/node_modules/@something/tsconfig-node/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "@something/tsconfig-base/tsconfig.json",
                    "compilerOptions": {
                        "removeComments": true
                    }
                }
"""),
                ["/users/user/projects/myconfigs/node_modules/@something/tsconfig-base/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": { "composite": true }
                }
"""),
                ["/users/user/projects/myproject/src/index.ts"] = StringTestUtil.Dedent("""
                // some comment
                export const x = 10;
"""),
                ["/users/user/projects/myproject/src/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "@something/tsconfig-node/tsconfig.json"
                }
"""),
                ["/users/user/projects/myproject/node_modules/@something/tsconfig-node"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/users/user/projects/myconfigs/node_modules/@something/tsconfig-node"),
            },
            cwd = "/users/user/projects/myproject",
            commandLineArgs = [builtType, "src", "--extendedDiagnostics"],
        };
    }

    // tsc_test.go:1025-1073
    private static TscInput GetTscExtendsConfigDirTestCase(string subScenarioSufix, string[] commandLineArgs, TscEdit[]? edits)
    {
        return new TscInput
        {
            subScenario = "configDir template" + subScenarioSufix,
            files = new Dictionary<string, object>
            {
                ["/home/src/projects/configs/first/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "../second/tsconfig.json",
                    "include": ["${configDir}/src"],
                    "compilerOptions": {
                        "typeRoots": ["root1", "${configDir}/root2", "root3"],
                        "types": [],
                    },
                }
"""),
                ["/home/src/projects/configs/second/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "files": ["${configDir}/main.ts"],
                    "compilerOptions": {
                        "declarationDir": "${configDir}/decls",
                        "paths": {
                            "@myscope/*": ["${configDir}/types/*"],
                        },
                    },
                    "watchOptions": {
                        "excludeFiles": ["${configDir}/main.ts"],
                    },
                }
"""),
                ["/home/src/projects/myproject/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "../configs/first/tsconfig.json",
                    "compilerOptions": {
                        "declaration": true,
                        "outDir": "outDir",
                        "traceResolution": true,
                    },
                }
"""),
                ["/home/src/projects/myproject/main.ts"] = StringTestUtil.Dedent("""
                // some comment
                export const y = 10;
                import { x } from "@myscope/sometype";
"""),
                ["/home/src/projects/myproject/types/sometype.ts"] = StringTestUtil.Dedent("""
                export const x = 10;
"""),
            },
            cwd = "/home/src/projects/myproject",
            commandLineArgs = commandLineArgs,
            edits = edits,
        };
    }

    // tsc_test.go:1074-1091
    private static TscInput GetTscExtendsNonStringPathTestCase(string propertyName)
    {
        // Go splices propertyName into the middle of the config text; the placeholder does the
        // same without changing Dedent's indentation guess.
        var baseContents = StringTestUtil.Dedent("""
            {
                "PROPERTY_NAME": [1],
            }
""").Replace("PROPERTY_NAME", propertyName, System.StringComparison.Ordinal);
        return new TscInput
        {
            subScenario = "extends config with non-string " + propertyName,
            files = new Dictionary<string, object>
            {
                ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "./base.json",
                }
"""),
                ["/home/src/projects/project/base.json"] = baseContents,
                ["/home/src/projects/project/main.ts"] = "export const x = 1;",
            },
            cwd = "/home/src/projects/project",
            commandLineArgs = ["-p", "tsconfig.json", "--pretty", "false"],
        };
    }

    // tsc_test.go:1092-1101
    private static Dictionary<string, object> GetTscExtendsBase(string baseContents)
    {
        return new Dictionary<string, object>
        {
            ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "./base.json",
            }
"""),
            ["/home/src/projects/project/base.json"] = StringTestUtil.Dedent(baseContents),
            ["/home/src/projects/project/main.ts"] = "export const x = 1;",
        };
    }

    // tsc_test.go:1103-1108
    [Fact]
    public void WhenBuildingSolutionWithProjectsExtendsConfigWithInclude()
    {
        new TscInput
        {
            subScenario = "when building solution with projects extends config with include",
            files = GetBuildConfigFileExtendsFileMap(),
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "--v", "--listFiles"],
        }.RunAndAssert("extends");
    }

    // tsc_test.go:1109
    [Fact]
    public void ExtendsConfigWithNonStringInclude() => GetTscExtendsNonStringPathTestCase("include").RunAndAssert("extends");

    // tsc_test.go:1110
    [Fact]
    public void ExtendsConfigWithNonStringExclude() => GetTscExtendsNonStringPathTestCase("exclude").RunAndAssert("extends");

    // tsc_test.go:1111
    [Fact]
    public void ExtendsConfigWithNonStringFiles() => GetTscExtendsNonStringPathTestCase("files").RunAndAssert("extends");

    // tsc_test.go:1112-1120
    [Fact]
    public void ExtendsConfigWithMixedValidAndNonStringInclude()
    {
        new TscInput
        {
            subScenario = "extends config with mixed valid and non-string include",
            files = GetTscExtendsBase("""
            {
                "include": ["main.ts", 1],
            }
"""),
            cwd = "/home/src/projects/project",
            commandLineArgs = ["-p", "tsconfig.json", "--pretty", "false"],
        }.RunAndAssert("extends");
    }

    // tsc_test.go:1121-1126
    [Fact]
    public void WhenBuildingProjectUsesReferenceAndBothExtendConfigWithInclude()
    {
        new TscInput
        {
            subScenario = "when building project uses reference and both extend config with include",
            files = GetBuildConfigFileExtendsFileMap(),
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "webpack/tsconfig.json", "--v", "--listFiles"],
        }.RunAndAssert("extends");
    }

    // tsc_test.go:1127
    [Fact]
    public void ResolvesTheSymlinkPathP() => GetTscExtendsWithSymlinkTestCase("-p").RunAndAssert("extends");

    // tsc_test.go:1128
    [Fact]
    public void ResolvesTheSymlinkPathB() => GetTscExtendsWithSymlinkTestCase("-b").RunAndAssert("extends");

    // tsc_test.go:1129
    [Fact]
    public void ConfigDirTemplateExplainFiles() => GetTscExtendsConfigDirTestCase("", ["--explainFiles"], null).RunAndAssert("extends");

    // tsc_test.go:1130
    [Fact]
    public void ConfigDirTemplateShowConfig() => GetTscExtendsConfigDirTestCase(" showConfig", ["--showConfig"], null).RunAndAssert("extends");

    // tsc_test.go:1131
    [Fact]
    public void ConfigDirTemplateWithCommandline() => GetTscExtendsConfigDirTestCase(" with commandline", ["--explainFiles", "--outDir", "${configDir}/outDir"], null).RunAndAssert("extends");

    // tsc_test.go:1132
    [Fact]
    public void ConfigDirTemplateBuild() => GetTscExtendsConfigDirTestCase("", ["--b", "--explainFiles", "--v"], null).RunAndAssert("extends");

    // tsc_test.go:1133-1151
    [Fact]
    public void ConfigDirTemplateBuildWatchEditExtendedConfigFile()
    {
        GetTscExtendsConfigDirTestCase("", ["--b", "-w", "--explainFiles", "--v"],
        [
            new TscEdit
            {
                caption = "edit extended config file",
                edit = sys => sys.WriteFileNoError(
                    "/home/src/projects/configs/first/tsconfig.json",
                    StringTestUtil.Dedent("""
                    {
                        "extends": "../second/tsconfig.json",
                        "include": ["${configDir}/src"],
                        "compilerOptions": {
                            "typeRoots": ["${configDir}/root2"],
                            "types": [],
                        },
                    }
""")),
            },
        ]).RunAndAssert("extends");
    }
}
