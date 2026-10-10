// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildTransitiveReferences.

using System;
using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildTransitiveReferencesTests
{
    // tscbuild_test.go:4237-4291 — getBuildTransitiveReferencesFileMap.
    private static Dictionary<string, object> GetBuildTransitiveReferencesFileMap(Action<Dictionary<string, object>>? modify)
    {
        var files = new Dictionary<string, object>
        {
            ["/user/username/projects/transitiveReferences/refs/a.d.ts"] = StringTestUtil.Dedent("""

				export class X {}
				export class A {}
			
"""),
            ["/user/username/projects/transitiveReferences/a.ts"] = StringTestUtil.Dedent("""

				export class A {}
			
"""),
            ["/user/username/projects/transitiveReferences/b.ts"] = StringTestUtil.Dedent("""

				import {A} from '@ref/a';
				export const b = new A();
			
"""),
            ["/user/username/projects/transitiveReferences/c.ts"] = StringTestUtil.Dedent("""

				import {b} from './b';
				import {X} from "@ref/a";
				b;
				X;
			
"""),
            ["/user/username/projects/transitiveReferences/tsconfig.a.json"] = StringTestUtil.Dedent("""

			{
				"files": ["a.ts"],
				"compilerOptions": {
					"composite": true,
				},
			}
"""),
            ["/user/username/projects/transitiveReferences/tsconfig.b.json"] = StringTestUtil.Dedent("""

			{
				"files": ["b.ts"],
				"compilerOptions": {
					"composite": true,
					"paths": {
						"@ref/*": ["./*"],
					},
				},
				"references": [{ "path": "tsconfig.a.json" }],
			}
"""),
            ["/user/username/projects/transitiveReferences/tsconfig.c.json"] = StringTestUtil.Dedent("""

			{
				"files": ["c.ts"],
				"compilerOptions": {
					"paths": {
						"@ref/*": ["./refs/*"],
					},
				},
				"references": [{ "path": "tsconfig.b.json" }],
			}
"""),
        };
        modify?.Invoke(files);
        return files;
    }

    // tscbuild_test.go:4234-4320 — TestBuildTransitiveReferences.
    [Fact]
    public void BuildTransitiveReferences()
    {
        var testCases = new List<TscInput>
        {
            new TscInput
            {
                subScenario = "builds correctly",
                files = GetBuildTransitiveReferencesFileMap(null),
                cwd = "/user/username/projects/transitiveReferences",
                commandLineArgs = ["--b", "tsconfig.c.json", "--listFiles"],
            },
            new TscInput
            {
                subScenario = "reports error about module not found with node resolution with external module name",
                files = GetBuildTransitiveReferencesFileMap(files =>
                {
                    files["/user/username/projects/transitiveReferences/b.ts"] = "import {A} from 'a';\nexport const b = new A();";
                    files["/user/username/projects/transitiveReferences/tsconfig.b.json"] = StringTestUtil.Dedent("""

				{
					"files": ["b.ts"],
					"compilerOptions": {
						"composite": true,
						"module": "nodenext",
					},
					"references": [{ "path": "tsconfig.a.json" }],
				}
""");
                }),
                cwd = "/user/username/projects/transitiveReferences",
                commandLineArgs = ["--b", "tsconfig.c.json", "--listFiles"],
            },
        };

        foreach (var test in testCases)
        {
            test.RunAndAssert("transitiveReferences");
        }
    }
}
