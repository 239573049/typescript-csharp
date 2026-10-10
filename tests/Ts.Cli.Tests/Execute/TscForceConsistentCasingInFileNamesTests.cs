// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestForceConsistentCasingInFileNames.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscForceConsistentCasingInFileNamesTests
{
    // tsc_test.go:1162-1176
    [Fact]
    public void WithRelativeAndNonRelativeFileResolutions()
    {
        new TscInput
        {
            subScenario = "with relative and non relative file resolutions",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/src/struct.d.ts"] = StringTestUtil.Dedent("""
                    import * as xs1 from "fp-ts/lib/Struct";
                    import * as xs2 from "fp-ts/lib/struct";
                    import * as xs3 from "./Struct";
                    import * as xs4 from "./struct";
"""),
                ["/user/username/projects/myproject/node_modules/fp-ts/lib/struct.d.ts"] = "export function foo(): void",
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["/user/username/projects/myproject/src/struct.d.ts", "--forceConsistentCasingInFileNames", "--explainFiles"],
            ignoreCase = true,
        }.RunAndAssert("forceConsistentCasingInFileNames");
    }

    // tsc_test.go:1177-1204
    [Fact]
    public void WhenFileIsIncludedFromMultiplePlacesWithDifferentCasing()
    {
        new TscInput
        {
            subScenario = "when file is included from multiple places with different casing",
            files = new Dictionary<string, object>
            {
                ["/home/src/projects/project/src/struct.d.ts"] = StringTestUtil.Dedent("""
                    import * as xs1 from "fp-ts/lib/Struct";
                    import * as xs2 from "fp-ts/lib/struct";
                    import * as xs3 from "./Struct";
                    import * as xs4 from "./struct";
"""),
                ["/home/src/projects/project/src/anotherFile.ts"] = StringTestUtil.Dedent("""
                    import * as xs1 from "fp-ts/lib/Struct";
                    import * as xs2 from "fp-ts/lib/struct";
                    import * as xs3 from "./Struct";
                    import * as xs4 from "./struct";
"""),
                ["/home/src/projects/project/src/oneMore.ts"] = StringTestUtil.Dedent("""
                    import * as xs1 from "fp-ts/lib/Struct";
                    import * as xs2 from "fp-ts/lib/struct";
                    import * as xs3 from "./Struct";
                    import * as xs4 from "./struct";
"""),
                ["/home/src/projects/project/tsconfig.json"] = "{}",
                ["/home/src/projects/project/node_modules/fp-ts/lib/struct.d.ts"] = "export function foo(): void",
            },
            cwd = "/home/src/projects/project",
            commandLineArgs = ["--explainFiles"],
            ignoreCase = true,
        }.RunAndAssert("forceConsistentCasingInFileNames");
    }

    // tsc_test.go:1205-1218
    [Fact]
    public void WithTypeRefFromFile()
    {
        new TscInput
        {
            subScenario = "with type ref from file",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/src/fileOne.d.ts"] = "declare class c { }",
                ["/user/username/projects/myproject/src/file2.d.ts"] = StringTestUtil.Dedent("""
                    /// <reference types="./fileOne.d.ts"/>
                    declare const y: c;
"""),
                ["/user/username/projects/myproject/tsconfig.json"] = "{ }",
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-p", "/user/username/projects/myproject", "--explainFiles", "--traceResolution"],
            ignoreCase = true,
        }.RunAndAssert("forceConsistentCasingInFileNames");
    }

    // tsc_test.go:1219-1227
    [Fact]
    public void WithTripleSlashRefFromFile()
    {
        new TscInput
        {
            subScenario = "with triple slash ref from file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/c.ts"] = "/// <reference path=\"./D.ts\"/>",
                ["/home/src/workspaces/project/src/d.ts"] = "declare class c { }",
                ["/home/src/workspaces/project/tsconfig.json"] = "{ }",
            },
            ignoreCase = true,
        }.RunAndAssert("forceConsistentCasingInFileNames");
    }

    // tsc_test.go:1228-1239
    [Fact]
    public void TwoFilesExistOnDiskThatDiffersOnlyInCasing()
    {
        new TscInput
        {
            subScenario = "two files exist on disk that differs only in casing",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/c.ts"] = "import {x} from \"./D\"",
                ["/home/src/workspaces/project/D.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/d.ts"] = "export const y = 20;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "files": ["c.ts", "d.ts"]
                }
"""),
            },
        }.RunAndAssert("forceConsistentCasingInFileNames");
    }
}
