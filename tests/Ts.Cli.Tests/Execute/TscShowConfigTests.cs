// Ported from tsgo internal/execute/tsctests/showconfig_test.go — TestShowConfig.
//
// The byte-exact --showConfig baselines live under
// testdata/baselines/reference/tsc/showConfig/. (Ts.Compiler.Tests/TsOptions/ShowConfigTests.cs
// covers ConvertToTSConfig's serialization shape as unit tests; this file runs the same 17
// scenarios through the real CLI + baseline runner.)

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscShowConfigTests
{
    // showconfig_test.go:13
    private static void RunCase(TscInput testCase) => testCase.RunAndAssert("showConfig");

    // showconfig_test.go:14-15
    [Fact]
    public void DefaultInitializedTSConfig()
    {
        RunCase(new TscInput
        {
            subScenario = "Default initialized TSConfig",
            commandLineArgs = ["--showConfig"],
        });
    }

    // showconfig_test.go:16-19
    [Fact]
    public void ShowTSConfigWithFilesOptions()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with files options",
            commandLineArgs = ["--showConfig", "file0.ts", "file1.ts", "file2.ts"],
        });
    }

    // showconfig_test.go:20-23
    [Fact]
    public void ShowTSConfigWithBooleanValueCompilerOptions()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with boolean value compiler options",
            commandLineArgs = ["--showConfig", "--noUnusedLocals"],
        });
    }

    // showconfig_test.go:24-27
    [Fact]
    public void ShowTSConfigWithEnumValueCompilerOptions()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with enum value compiler options",
            commandLineArgs = ["--showConfig", "--target", "es5", "--jsx", "react"],
        });
    }

    // showconfig_test.go:28-31
    [Fact]
    public void ShowTSConfigWithListCompilerOptions()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with list compiler options",
            commandLineArgs = ["--showConfig", "--types", "jquery,mocha"],
        });
    }

    // showconfig_test.go:32-35
    [Fact]
    public void ShowTSConfigWithListCompilerOptionsWithEnumValue()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with list compiler options with enum value",
            commandLineArgs = ["--showConfig", "--lib", "es5,es2015.core"],
        });
    }

    // showconfig_test.go:36-39
    [Fact]
    public void ShowTSConfigWithIncorrectCompilerOption()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with incorrect compiler option",
            commandLineArgs = ["--showConfig", "--someNonExistOption"],
        });
    }

    // showconfig_test.go:40-43
    [Fact]
    public void ShowTSConfigWithIncorrectCompilerOptionValue()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with incorrect compiler option value",
            commandLineArgs = ["--showConfig", "--lib", "nonExistLib,es5,es2015.promise"],
        });
    }

    // showconfig_test.go:44-47
    [Fact]
    public void ShowTSConfigWithAdvancedOptions()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with advanced options",
            commandLineArgs = ["--showConfig", "--declaration", "--declarationDir", "lib", "--skipLibCheck", "--noErrorTruncation"],
        });
    }

    // showconfig_test.go:48-74
    [Fact]
    public void ShowTSConfigWithCompileOnSaveAndMore()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with compileOnSave and more",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/index.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "esModuleInterop": true,
                            "target": "es5",
                            "module": "commonjs",
                            "strict": true
                        },
                        "compileOnSave": true,
                        "exclude": [
                            "dist"
                        ],
                        "files": [],
                        "include": [
                            "src/*"
                        ],
                        "references": [
                            { "path": "./test" }
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:75-109
    [Fact]
    public void ShowTSConfigWithPathsAndMore()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with paths and more",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/index.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "allowJs": true,
                            "outDir": "./lib",
                            "esModuleInterop": true,
                            "module": "commonjs",
                            "moduleResolution": "node",
                            "target": "ES2017",
                            "sourceMap": true,
                            "baseUrl": ".",
                            "paths": {
                                "@root/*": ["./*"],
                                "@configs/*": ["src/configs/*"],
                                "@common/*": ["src/common/*"],
                                "*": [
                                    "node_modules/*",
                                    "src/types/*"
                                ]
                            },
                            "experimentalDecorators": true,
                            "emitDecoratorMetadata": true,
                            "resolveJsonModule": true
                        },
                        "include": [
                            "./src/**/*"
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:110-127
    [Fact]
    public void ShowTSConfigWithIncludeFilteringFiles()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with include filtering files",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/src/util.ts"] = "export const b = 2;",
                ["/home/src/workspaces/project/extra.ts"] = "export const c = 3;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true
                        },
                        "include": [
                            "src/**/*"
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:128-145
    [Fact]
    public void ShowTSConfigWithReferences()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with references",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/index.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "strict": true
                        },
                        "references": [
                            { "path": "./packages/a" },
                            { "path": "./packages/b" }
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:146-162
    [Fact]
    public void ShowTSConfigWithExclude()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with exclude",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/index.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/test/test1.ts"] = "import { a } from \"../src\";",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true
                        },
                        "exclude": [
                            "test"
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:163-182
    [Fact]
    public void ShowTSConfigWithFilesAndInclude()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with files and include",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/extra.ts"] = "export const c = 3;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true
                        },
                        "files": [
                            "extra.ts"
                        ],
                        "include": [
                            "src/**/*"
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:183-195
    [Fact]
    public void ShowTSConfigWithTransitivelyImpliedOptions()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with transitively implied options",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/index.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "nodenext"
                        }
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }

    // showconfig_test.go:196-213
    [Fact]
    public void ShowTSConfigWithExcludeAndOutDir()
    {
        RunCase(new TscInput
        {
            subScenario = "Show TSConfig with exclude and outDir",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/index.ts"] = "export const a = 1;",
                ["/home/src/workspaces/project/src/bin/tool.ts"] = "export const b = 2;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true,
                            "outDir": "./build"
                        },
                        "exclude": [
                            "build"
                        ]
                    }
"""),
            },
            commandLineArgs = ["-p", "tsconfig.json", "--showConfig"],
        });
    }
}
