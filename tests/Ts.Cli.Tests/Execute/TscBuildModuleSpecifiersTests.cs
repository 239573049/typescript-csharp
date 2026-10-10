// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildModuleSpecifiers.
// Baselines: testdata/baselines/reference/tsbuild/moduleSpecifiers.

using System.Collections.Generic;
using Ts.TestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildModuleSpecifiersTests
{
    // tscbuild_test.go:1840-1932
    [Fact]
    public void SynthesizedModuleSpecifiersResolveCorrectly()
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/packages/solution/common/nominal.ts"] = StringTestUtil.Dedent("""
                    export declare type Nominal<T, Name extends string> = T & {
                        [Symbol.species]: Name;
                    };
"""),
            ["/home/src/workspaces/packages/solution/common/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "extends": "../../tsconfig.base.json",
                    "compilerOptions": {
                        "composite": true
                    },
                    "include": ["nominal.ts"]
                }
"""),
            ["/home/src/workspaces/packages/solution/sub-project/index.ts"] = StringTestUtil.Dedent("""
                    import { Nominal } from '../common/nominal';

                    export type MyNominal = Nominal<string, 'MyNominal'>;
"""),
            ["/home/src/workspaces/packages/solution/sub-project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "extends": "../../tsconfig.base.json",
                        "compilerOptions": {
                            "composite": true
                        },
                        "references": [
                            { "path": "../common" }
                        ],
                        "include": ["./index.ts"]
                    }
"""),
            ["/home/src/workspaces/packages/solution/sub-project-2/index.ts"] = StringTestUtil.Dedent("""
                    import { MyNominal } from '../sub-project/index';

                    const variable = {
                        key: 'value' as MyNominal,
                    };

                    export function getVar(): keyof typeof variable {
                        return 'key';
                    }
"""),
            ["/home/src/workspaces/packages/solution/sub-project-2/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "extends": "../../tsconfig.base.json",
                        "compilerOptions": {
                            "composite": true
                        },
                        "references": [
                            { "path": "../sub-project" }
                        ],
                        "include": ["./index.ts"]
                    }
"""),
            ["/home/src/workspaces/packages/solution/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true
                        },
                        "references": [
                            { "path": "./sub-project" },
                            { "path": "./sub-project-2" }
                        ],
                        "include": []
                    }
"""),
            ["/home/src/workspaces/packages/tsconfig.base.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "skipLibCheck": true,
                            "rootDir": "./",
                            "outDir": "lib"
						}
                    }
"""),
            ["/home/src/workspaces/packages/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true
                        },
                        "references": [
                            { "path": "./solution" },
                        ],
                        "include": [],
                    }
"""),
        };
        // tscbuild_test.go:1929
        var libDts = TscTestDefaults.TscLibPath + "/lib.d.ts";
        files[libDts] = TscTestDefaults.TscDefaultLibContent.Replace(
            "interface SymbolConstructor {",
            "interface SymbolConstructor {\n    readonly species: symbol;",
            System.StringComparison.Ordinal);

        new TscInput
        {
            subScenario = "synthesized module specifiers resolve correctly",
            files = files,
            cwd = "/home/src/workspaces/packages",
            commandLineArgs = ["-b", "--verbose"],
        }.RunAndAssert("moduleSpecifiers");
    }

    // tscbuild_test.go:1934-2025
    [Fact]
    public void SynthesizedModuleSpecifiersAcrossProjectsResolveCorrectly()
    {
        new TscInput
        {
            subScenario = "synthesized module specifiers across projects resolve correctly",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/packages/src-types/index.ts"] = StringTestUtil.Dedent("""
                    export * from './dogconfig.js';
"""),
                ["/home/src/workspaces/packages/src-types/dogconfig.ts"] = StringTestUtil.Dedent("""
                    export interface DogConfig {
                        name: string;
					}
"""),
                ["/home/src/workspaces/packages/src-dogs/index.ts"] = StringTestUtil.Dedent("""
                    export * from 'src-types';
                    export * from './lassie/lassiedog.js';
"""),
                ["/home/src/workspaces/packages/src-dogs/dogconfig.ts"] = StringTestUtil.Dedent("""
                    import { DogConfig } from 'src-types';

                    export const DOG_CONFIG: DogConfig = {
                        name: 'Default dog',
                    };
"""),
                ["/home/src/workspaces/packages/src-dogs/dog.ts"] = StringTestUtil.Dedent("""
                    import { DogConfig } from 'src-types';
                    import { DOG_CONFIG } from './dogconfig.js';
                    
                    export abstract class Dog {
                    
                        public static getCapabilities(): DogConfig {
                            return DOG_CONFIG;
                        }
                    }
"""),
                ["/home/src/workspaces/packages/src-dogs/lassie/lassiedog.ts"] = StringTestUtil.Dedent("""
                    import { Dog } from '../dog.js';
                    import { LASSIE_CONFIG } from './lassieconfig.js';
                    
                    export class LassieDog extends Dog {
                        protected static getDogConfig = () => LASSIE_CONFIG;
                    }
"""),
                ["/home/src/workspaces/packages/src-dogs/lassie/lassieconfig.ts"] = StringTestUtil.Dedent("""
                    import { DogConfig } from 'src-types';

                    export const LASSIE_CONFIG: DogConfig = { name: 'Lassie' };
"""),
                ["/home/src/workspaces/packages/tsconfig-base.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "declaration": true,
                            "module": "node16",
                        },
                    }
"""),
                ["/home/src/workspaces/packages/src-types/package.json"] = StringTestUtil.Dedent("""
				{
                    "type": "module",
                    "exports": "./index.js"
                }
"""),
                ["/home/src/workspaces/packages/src-dogs/package.json"] = StringTestUtil.Dedent("""
				{
                    "type": "module",
                    "exports": "./index.js"
                }
"""),
                ["/home/src/workspaces/packages/src-types/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "extends": "../tsconfig-base.json",
                    "compilerOptions": {
                        "composite": true,
                    },
                    "include": [
                        "**/*",
                    ],
                }
"""),
                ["/home/src/workspaces/packages/src-dogs/tsconfig.json"] = StringTestUtil.Dedent("""
				{
                    "extends": "../tsconfig-base.json",
                    "compilerOptions": {
                        "composite": true,
                    },
                    "references": [
                        { "path": "../src-types" },
                    ],
                    "include": [
                        "**/*",
                    ],
                }
"""),
                ["/home/src/workspaces/packages/src-types/node_modules"] = VfsTestUtil.Symlink("/home/src/workspaces/packages"),
                ["/home/src/workspaces/packages/src-dogs/node_modules"] = VfsTestUtil.Symlink("/home/src/workspaces/packages"),
            },
            cwd = "/home/src/workspaces/packages",
            commandLineArgs = ["-b", "src-types", "src-dogs", "--verbose"],
        }.RunAndAssert("moduleSpecifiers");
    }
}
