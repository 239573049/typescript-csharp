// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscDeclarationEmit.
//
// Go's table is split over two baseline subfolders: the command-line cases land in
// testdata/baselines/reference/tsc/declarationEmit, the `-b` cases in .../tsbuild/declarationEmit
// (the runner picks the subfolder from the command line, runner.go:180-202).

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscDeclarationEmitTests
{
    // tsc_test.go:461-530
    private static Dictionary<string, object> GetBuildDeclarationEmitDtsReferenceAsTrippleSlashMap(bool useNoRef)
    {
        var files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/solution/tsconfig.base.json"] = StringTestUtil.Dedent("""
            {
                "compilerOptions": {
                    "rootDir": "./",
                    "outDir": "lib",
                },
            }
"""),
            ["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "compilerOptions": { "composite": true },
                "references": [{ "path": "./src" }],
                "include": [],
            }
"""),
            ["/home/src/workspaces/solution/src/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "compilerOptions": { "composite": true },
                "references": [{ "path": "./subProject" }, { "path": "./subProject2" }],
                "include": [],
            }
"""),
            ["/home/src/workspaces/solution/src/subProject/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "../../tsconfig.base.json",
                "compilerOptions": { "composite": true },
                "references": [{ "path": "../common" }],
                "include": ["./index.ts"],
            }
"""),
            ["/home/src/workspaces/solution/src/subProject/index.ts"] = StringTestUtil.Dedent("""
            import { Nominal } from '../common/nominal';
            export type MyNominal = Nominal<string, 'MyNominal'>;
"""),
            ["/home/src/workspaces/solution/src/subProject2/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "../../tsconfig.base.json",
                "compilerOptions": { "composite": true },
                "references": [{ "path": "../subProject" }],
                "include": ["./index.ts"],
            }
"""),
            ["/home/src/workspaces/solution/src/subProject2/index.ts"] = StringTestUtil.Dedent("""
            import { MyNominal } from '../subProject/index';
            const variable = {
                key: 'value' as MyNominal,
            };
            export function getVar(): keyof typeof variable {
                return 'key';
            }
"""),
            ["/home/src/workspaces/solution/src/common/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "../../tsconfig.base.json",
                "compilerOptions": { "composite": true },
                "include": ["./nominal.ts"],
            }
"""),
            ["/home/src/workspaces/solution/src/common/nominal.ts"] = StringTestUtil.Dedent("""
            /// <reference path="./types.d.ts" preserve="true" />
            export declare type Nominal<T, Name extends string> = MyNominal<T, Name>;
"""),
            ["/home/src/workspaces/solution/src/common/types.d.ts"] = StringTestUtil.Dedent("""
            declare type MyNominal<T, Name extends string> = T & {
                specialKey: Name;
            };
"""),
        };
        if (useNoRef)
        {
            files["/home/src/workspaces/solution/tsconfig.json"] = StringTestUtil.Dedent("""
            {
                "extends": "./tsconfig.base.json",
                "compilerOptions": { "composite": true },
                "include": ["./src/**/*.ts"],
            }
""");
        }
        return files;
    }

    // tsc_test.go:532-569
    private static Dictionary<string, object> GetTscDeclarationEmitDtsErrorsFileMap(bool composite, bool incremental)
    {
        return new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
            {
                "compilerOptions": {
                    "module": "NodeNext",
                    "moduleResolution": "NodeNext",
                    "composite": {{(composite ? "true" : "false")}},
                    "incremental": {{(incremental ? "true" : "false")}},
                    "declaration": true,
                    "skipLibCheck": true,
                    "skipDefaultLibCheck": true,
                },
            }
"""),
            ["/home/src/workspaces/project/index.ts"] = StringTestUtil.Dedent("""
            import ky from 'ky';
            export const api = ky.extend({});
"""),
            ["/home/src/workspaces/project/package.json"] = StringTestUtil.Dedent("""
            {
                "type": "module"
            }
"""),
            ["/home/src/workspaces/project/node_modules/ky/distribution/index.d.ts"] = StringTestUtil.Dedent("""
            type KyInstance = {
                extend(options: Record<string,unknown>): KyInstance;
            }
            declare const ky: KyInstance;
            export default ky;
"""),
            ["/home/src/workspaces/project/node_modules/ky/package.json"] = StringTestUtil.Dedent("""
            {
                "name": "ky",
                "type": "module",
                "main": "./distribution/index.js"
            }
"""),
        };
    }

    // tsc_test.go:571-580
    private static string PluginOneConfig() => StringTestUtil.Dedent("""
        {
            "compilerOptions": {
                "target": "es5",
                "declaration": true,
                "traceResolution": true,
            },
        }
""");

    // tsc_test.go:582-584
    private static string PluginOneIndex() => "import pluginTwo from \"plugin-two\"; // include this to add reference to symlink";

    // tsc_test.go:586-592
    private static string PluginOneAction() => StringTestUtil.Dedent("""
        import { actionCreatorFactory } from "typescript-fsa"; // Include version of shared lib
        const action = actionCreatorFactory("somekey");
        const featureOne = action<{ route: string }>("feature-one");
        export const actions = { featureOne };
""");

    // tsc_test.go:594-617
    private static string PluginTwoDts() => StringTestUtil.Dedent("""
        declare const _default: {
            features: {
                featureOne: {
                    actions: {
                        featureOne: {
                            (payload: {
                                name: string;
                                order: number;
                            }, meta?: {
                                [key: string]: any;
                            }): import("typescript-fsa").Action<{
                                name: string;
                                order: number;
                            }>;
                        };
                    };
                    path: string;
                };
            };
        };
        export default _default;
""");

    // tsc_test.go:619-625
    private static string FsaPackageJson() => StringTestUtil.Dedent("""
        {
            "name": "typescript-fsa",
            "version": "3.0.0-beta-2"
        }
""");

    // tsc_test.go:627-642
    private static string FsaIndex() => StringTestUtil.Dedent("""
        export interface Action<Payload> {
            type: string;
            payload: Payload;
        }
        export declare type ActionCreator<Payload> = {
            type: string;
            (payload: Payload): Action<Payload>;
        }
        export interface ActionCreatorFactory {
            <Payload = void>(type: string): ActionCreator<Payload>;
        }
        export declare function actionCreatorFactory(prefix?: string | null): ActionCreatorFactory;
        export default actionCreatorFactory;
""");

    // tsc_test.go:644-649
    [Fact]
    public void WhenDeclarationFileIsReferencedThroughTripleSlash()
    {
        new TscInput
        {
            subScenario = "when declaration file is referenced through triple slash",
            files = GetBuildDeclarationEmitDtsReferenceAsTrippleSlashMap(false),
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "--verbose"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:650-655
    [Fact]
    public void WhenDeclarationFileIsReferencedThroughTripleSlashButUsesNoReferences()
    {
        new TscInput
        {
            subScenario = "when declaration file is referenced through triple slash but uses no references",
            files = GetBuildDeclarationEmitDtsReferenceAsTrippleSlashMap(true),
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "--verbose"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:656-676
    [Fact]
    public void WhenTsFileIsReferencedThroughTripleSlashFromAnotherProject()
    {
        new TscInput
        {
            subScenario = "when ts file is referenced through triple slash from another project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/include/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": { "composite": true, "declaration": true },
                }
"""),
                ["/home/src/workspaces/solution/include/include.ts"] = StringTestUtil.Dedent("""
                export const include = 1;
"""),
                ["/home/src/workspaces/solution/src/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": { "composite": true, "declaration": true },
                    "references": [{ "path": "../include" }],
                }
"""),
                ["/home/src/workspaces/solution/src/main.ts"] = StringTestUtil.Dedent("""
                /// <reference path="../include/include.ts" preserve="true" />
                export const main = 23;
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--b", "src", "--verbose"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:677-719
    [Fact]
    public void WhenDeclarationFileUsedInferredTypeFromReferencedProject()
    {
        new TscInput
        {
            subScenario = "when declaration file used inferred type from referenced project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                        "paths": { "@fluentui/*": ["./packages/*/src"] },
                    },
                }
"""),
                ["/home/src/workspaces/project/packages/pkg1/src/index.ts"] = StringTestUtil.Dedent("""
                export interface IThing {
                    a: string;
                }
                export interface IThings {
                    thing1: IThing;
                }
"""),
                ["/home/src/workspaces/project/packages/pkg1/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "../../tsconfig",
                    "compilerOptions": { "outDir": "lib" },
                    "include": ["src"],
                }
"""),
                ["/home/src/workspaces/project/packages/pkg2/src/index.ts"] = StringTestUtil.Dedent("""
                import { IThings } from '@fluentui/pkg1';
                export function fn4() {
                    const a: IThings = { thing1: { a: 'b' } };
                    return a.thing1;
                }
"""),
                ["/home/src/workspaces/project/packages/pkg2/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "extends": "../../tsconfig",
                    "compilerOptions": { "outDir": "lib" },
                    "include": ["src"],
                    "references": [{ "path": "../pkg1" }],
                }
"""),
            },
            commandLineArgs = ["--b", "packages/pkg2/tsconfig.json", "--verbose"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:720-760
    [Fact]
    public void WhenInferredExportShouldReuseImportedTypeAliasAcrossAModuleBoundary()
    {
        new TscInput
        {
            subScenario = "when inferred export should reuse imported type alias across a module boundary",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "strict": true,
                        "declaration": true,
                        "emitDeclarationOnly": true,
                        "target": "es2022",
                        "module": "esnext",
                    },
                    "files": ["./a.ts", "./factory.ts", "./state.ts"],
                }
"""),
                [TscTestDefaults.TscLibPath + "/lib.es2022.full.d.ts"] = TscTestDefaults.TscDefaultLibContent + "\n" + StringTestUtil.Dedent("""
                type Partial<T> = {
                    [K in keyof T]?: T[K];
                };
"""),
                ["/home/src/workspaces/project/a.ts"] = StringTestUtil.Dedent("""
                interface ISettings {
                    age: number;
                }

                export type Settings = Partial<ISettings>;
"""),
                ["/home/src/workspaces/project/factory.ts"] = StringTestUtil.Dedent("""
                import type { Settings } from "./a";

                export const makeObj = () => ({
                    fn: (s?: Settings): Settings | undefined => s,
                });
"""),
                ["/home/src/workspaces/project/state.ts"] = StringTestUtil.Dedent("""
                import { makeObj } from "./factory";

                export const obj = makeObj();
"""),
            },
            commandLineArgs = ["--p", "tsconfig.json"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:761-766
    [Fact]
    public void ReportsDtsGenerationErrors()
    {
        new TscInput
        {
            subScenario = "reports dts generation errors",
            files = GetTscDeclarationEmitDtsErrorsFileMap(false, false),
            commandLineArgs = ["-b", "--explainFiles", "--listEmittedFiles", "--v"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:767-772
    [Fact]
    public void ReportsDtsGenerationErrorsWithIncremental()
    {
        new TscInput
        {
            subScenario = "reports dts generation errors with incremental",
            files = GetTscDeclarationEmitDtsErrorsFileMap(false, true),
            commandLineArgs = ["-b", "--explainFiles", "--listEmittedFiles", "--v"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:773-784
    [Fact]
    public void ReportsDtsGenerationErrorsThenBuild()
    {
        new TscInput
        {
            subScenario = "reports dts generation errors",
            files = GetTscDeclarationEmitDtsErrorsFileMap(false, false),
            commandLineArgs = ["--explainFiles", "--listEmittedFiles"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "build -b",
                    commandLineArgs = ["-b", "--explainFiles", "--listEmittedFiles", "--v"],
                },
            ],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:785-796
    [Fact]
    public void ReportsDtsGenerationErrorsWithIncrementalThenBuild()
    {
        new TscInput
        {
            subScenario = "reports dts generation errors with incremental",
            files = GetTscDeclarationEmitDtsErrorsFileMap(true, true),
            commandLineArgs = ["--explainFiles", "--listEmittedFiles"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "build -b",
                    commandLineArgs = ["-b", "--explainFiles", "--listEmittedFiles", "--v"],
                },
            ],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:797-849
    [Fact]
    public void WhenUsingWindowsPathsAndUppercaseLetters()
    {
        new TscInput
        {
            subScenario = "when using Windows paths and uppercase letters",
            files = new Dictionary<string, object>
            {
                ["D:/Work/pkg1/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "ts-specifier-bug",
                    "version": "1.0.0",
                    "main": "index.js"
                }
"""),
                ["D:/Work/pkg1/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "declaration": true,
                        "target": "es2017",
                        "outDir": "./dist",
                    },
                    "include": ["src"],
                }
"""),
                ["D:/Work/pkg1/src/main.ts"] = StringTestUtil.Dedent("""
                import { PartialType } from './utils';

                class Common {}
                
                export class Sub extends PartialType(Common) {
                    id: string;
                }
"""),
                ["D:/Work/pkg1/src/utils/index.ts"] = StringTestUtil.Dedent("""
                import { MyType, MyReturnType } from './type-helpers';

                export function PartialType<T>(classRef: MyType<T>) {
                    abstract class PartialClassType {
                        constructor() {}
                    }
                
                    return PartialClassType as MyReturnType;
                }
"""),
                ["D:/Work/pkg1/src/utils/type-helpers.ts"] = StringTestUtil.Dedent("""
                export type MyReturnType = {	
                    new (...args: any[]): any;
                };
            
                export interface MyType<T = any> extends Function {
                    new (...args: any[]): T;
                }
"""),
            },
            cwd = "D:/Work/pkg1",
            windowsStyleRoot = "D:/",
            ignoreCase = true,
            commandLineArgs = ["-p", "D:\\Work\\pkg1", "--explainFiles"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:850-865
    [Fact]
    public void WhenSameVersionIsReferencedThroughSourceAndAnotherSymlinkedPackage()
    {
        new TscInput
        {
            subScenario = "when same version is referenced through source and another symlinked package",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/plugin-two/index.d.ts"] = PluginTwoDts(),
                ["/user/username/projects/myproject/plugin-two/node_modules/typescript-fsa/package.json"] = FsaPackageJson(),
                ["/user/username/projects/myproject/plugin-two/node_modules/typescript-fsa/index.d.ts"] = FsaIndex(),
                ["/user/username/projects/myproject/plugin-one/tsconfig.json"] = PluginOneConfig(),
                ["/user/username/projects/myproject/plugin-one/index.ts"] = PluginOneIndex(),
                ["/user/username/projects/myproject/plugin-one/action.ts"] = PluginOneAction(),
                ["/user/username/projects/myproject/plugin-one/node_modules/typescript-fsa/package.json"] = FsaPackageJson(),
                ["/user/username/projects/myproject/plugin-one/node_modules/typescript-fsa/index.d.ts"] = FsaIndex(),
                ["/user/username/projects/myproject/plugin-one/node_modules/plugin-two"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/plugin-two"),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-p", "plugin-one", "--explainFiles"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:866-887
    [Fact]
    public void WhenSameVersionIsReferencedThroughSourceAndAnotherSymlinkedPackageWithIndirectLink()
    {
        new TscInput
        {
            subScenario = "when same version is referenced through source and another symlinked package with indirect link",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/plugin-two/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "plugin-two",
                    "version": "0.1.3",
                    "main": "dist/commonjs/index.js"
                }
"""),
                ["/user/username/projects/myproject/plugin-two/dist/commonjs/index.d.ts"] = PluginTwoDts(),
                ["/user/username/projects/myproject/plugin-two/node_modules/typescript-fsa/package.json"] = FsaPackageJson(),
                ["/user/username/projects/myproject/plugin-two/node_modules/typescript-fsa/index.d.ts"] = FsaIndex(),
                ["/user/username/projects/myproject/plugin-one/tsconfig.json"] = PluginOneConfig(),
                ["/user/username/projects/myproject/plugin-one/index.ts"] = PluginOneIndex() + "\n" + PluginOneAction(),
                ["/user/username/projects/myproject/plugin-one/node_modules/typescript-fsa/package.json"] = FsaPackageJson(),
                ["/user/username/projects/myproject/plugin-one/node_modules/typescript-fsa/index.d.ts"] = FsaIndex(),
                ["/temp/yarn/data/link/plugin-two"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/plugin-two"),
                ["/user/username/projects/myproject/plugin-one/node_modules/plugin-two"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/temp/yarn/data/link/plugin-two"),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-p", "plugin-one", "--explainFiles"],
        }.RunAndAssert("declarationEmit");
    }

    // tsc_test.go:888-944
    // !!! sheetal strada has error for d.ts generation in pkg3/src/keys.ts but corsa doesnt have that
    [Fact]
    public void WhenPkgReferencesSiblingPackageThroughIndirectSymlink()
    {
        new TscInput
        {
            subScenario = "when pkg references sibling package through indirect symlink",
            files = new Dictionary<string, object>
            {
                ["/user/username/projects/myproject/pkg1/dist/index.d.ts"] = "export * from './types';",
                ["/user/username/projects/myproject/pkg1/dist/types.d.ts"] = StringTestUtil.Dedent("""
                export declare type A = {
                    id: string;
                };
                export declare type B = {
                    id: number;
                };
                export declare type IdType = A | B;
                export declare class MetadataAccessor<T, D extends IdType = IdType> {
                    readonly key: string;
                    private constructor();
                    toString(): string;
                    static create<T, D extends IdType = IdType>(key: string): MetadataAccessor<T, D>;
                }
"""),
                ["/user/username/projects/myproject/pkg1/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "@raymondfeng/pkg1",
                    "version": "1.0.0",
                    "main": "dist/index.js",
                    "typings": "dist/index.d.ts"
                }
"""),
                ["/user/username/projects/myproject/pkg2/dist/index.d.ts"] = "export * from './types';",
                ["/user/username/projects/myproject/pkg2/dist/types.d.ts"] = "export {MetadataAccessor} from '@raymondfeng/pkg1';",
                ["/user/username/projects/myproject/pkg2/package.json"] = StringTestUtil.Dedent("""
                {
                    "name": "@raymondfeng/pkg2",
                    "version": "1.0.0",
                    "main": "dist/index.js",
                    "typings": "dist/index.d.ts"
                }
"""),
                ["/user/username/projects/myproject/pkg3/src/index.ts"] = "export * from './keys';",
                ["/user/username/projects/myproject/pkg3/src/keys.ts"] = StringTestUtil.Dedent("""
                import {MetadataAccessor} from "@raymondfeng/pkg2";
                export const ADMIN = MetadataAccessor.create<boolean>('1');
"""),
                ["/user/username/projects/myproject/pkg3/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "outDir": "dist",
                            "rootDir": "src",
                            "target": "es5",
                            "module": "commonjs",
                            "strict": true,
                            "esModuleInterop": true,
                            "declaration": true,
                        },
                    }
"""),
                ["/user/username/projects/myproject/pkg2/node_modules/@raymondfeng/pkg1"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/pkg1"),
                ["/user/username/projects/myproject/pkg3/node_modules/@raymondfeng/pkg2"] = global::Ts.Vfs.VfsTest.VfsTestUtil.Symlink("/user/username/projects/myproject/pkg2"),
            },
            cwd = "/user/username/projects/myproject",
            commandLineArgs = ["-p", "pkg3", "--explainFiles"],
        }.RunAndAssert("declarationEmit");
    }
}
