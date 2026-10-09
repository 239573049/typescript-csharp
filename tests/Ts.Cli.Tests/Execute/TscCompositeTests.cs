// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscComposite.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscCompositeTests
{
    // tsc_test.go:303-320
    [Fact]
    public void WhenSettingCompositeFalseOnCommandLine()
    {
        new TscInput
        {
            subScenario = "when setting composite false on command line",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "target": "es5",
                        "module": "commonjs",
                        "composite": true,
                    },
                    "include": [
                        "src/**/*.ts",
                    ],
                }
"""),
            },
            commandLineArgs = ["--composite", "false"],
        }.RunAndAssert("composite");
    }

    // tsc_test.go:321-338
    [Fact]
    public void WhenSettingCompositeNullOnCommandLine()
    {
        new TscInput
        {
            subScenario = "when setting composite null on command line",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "target": "es5",
                        "module": "commonjs",
                        "composite": true,
                    },
                    "include": [
                        "src/**/*.ts",
                    ],
                }
"""),
            },
            commandLineArgs = ["--composite", "null"],
        }.RunAndAssert("composite");
    }

    // tsc_test.go:339-357
    [Fact]
    public void WhenSettingCompositeFalseOnCommandLineButHasTsbuildInfoInConfig()
    {
        new TscInput
        {
            subScenario = "when setting composite false on command line but has tsbuild info in config",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "target": "es5",
                        "module": "commonjs",
                        "composite": true,
                        "tsBuildInfoFile": "tsconfig.json.tsbuildinfo",
                    },
                    "include": [
                        "src/**/*.ts",
                    ],
                }
"""),
            },
            commandLineArgs = ["--composite", "false"],
        }.RunAndAssert("composite");
    }

    // tsc_test.go:358-376
    [Fact]
    public void WhenSettingCompositeFalseAndTsbuildinfoAsNullOnCommandLineButHasTsbuildInfoInConfig()
    {
        new TscInput
        {
            subScenario = "when setting composite false and tsbuildinfo as null on command line but has tsbuild info in config",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "target": "es5",
                        "module": "commonjs",
                        "composite": true,
                        "tsBuildInfoFile": "tsconfig.json.tsbuildinfo",
                    },
                    "include": [
                        "src/**/*.ts",
                    ],
                }
"""),
            },
            commandLineArgs = ["--composite", "false", "--tsBuildInfoFile", "null"],
        }.RunAndAssert("composite");
    }

    // tsc_test.go:377-397
    [Fact]
    public void ConvertingToModules()
    {
        new TscInput
        {
            subScenario = "converting to modules",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "module": "none",
                        "composite": true,
                    },
                }
"""),
            },
            edits =
            [
                new TscEdit
                {
                    caption = "convert to modules",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/tsconfig.json", "none", "es2015"),
                },
            ],
        }.RunAndAssert("composite");
    }

    // tsc_test.go:398-424
    [Fact]
    public void SyntheticJsxImportOfEsmModuleFromCjsModuleNoCrashNoJsxElement()
    {
        new TscInput
        {
            subScenario = "synthetic jsx import of ESM module from CJS module no crash no jsx element",
            files = new Dictionary<string, object>
            {
                ["/home/src/projects/project/src/main.ts"] = "export default 42;",
                ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                        "module": "Node16",
                        "jsx": "react-jsx",
                        "jsxImportSource": "solid-js",
                    },
                }
"""),
                ["/home/src/projects/project/node_modules/solid-js/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "solid-js",
                    "type": "module"
                }
"""),
                ["/home/src/projects/project/node_modules/solid-js/jsx-runtime.d.ts"] = StringTestUtil.Dedent("""
                export namespace JSX {
                    type IntrinsicElements = { div: {}; };
                }
"""),
            },
            cwd = "/home/src/projects/project",
        }.RunAndAssert("composite");
    }

    // tsc_test.go:425-451
    [Fact]
    public void SyntheticJsxImportOfEsmModuleFromCjsModuleErrorOnJsxElement()
    {
        new TscInput
        {
            subScenario = "synthetic jsx import of ESM module from CJS module error on jsx element",
            files = new Dictionary<string, object>
            {
                ["/home/src/projects/project/src/main.tsx"] = "export default <div/>;",
                ["/home/src/projects/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                        "module": "Node16",
                        "jsx": "react-jsx",
                        "jsxImportSource": "solid-js",
                    },
                }
"""),
                ["/home/src/projects/project/node_modules/solid-js/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "solid-js",
                    "type": "module"
                }
"""),
                ["/home/src/projects/project/node_modules/solid-js/jsx-runtime.d.ts"] = StringTestUtil.Dedent("""
                export namespace JSX {
                    type IntrinsicElements = { div: {}; };
                }
"""),
            },
            cwd = "/home/src/projects/project",
        }.RunAndAssert("composite");
    }
}
