// Ported from tsgo internal/execute/tsctests/tscbuild_test.go — TestBuildInferredTypeFromTransitiveModule.
// Baselines: testdata/baselines/reference/tsbuild/inferredTypeFromTransitiveModule.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscBuildInferredTypeFromTransitiveModuleTests
{
    // tscbuild_test.go:1218-1273
    private static Dictionary<string, object> GetBuildInferredTypeFromTransitiveModuleMap(bool isolatedModules, string lazyExtraContents)
    {
        return new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/bar.ts"] = StringTestUtil.Dedent("""
				interface RawAction {
					(...args: any[]): Promise<any> | void;
				}
				interface ActionFactory {
					<T extends RawAction>(target: T): T;
				}
				declare function foo<U extends any[] = any[]>(): ActionFactory;
				export default foo()(function foobar(param: string): void {
				});
"""),
            ["/home/src/workspaces/project/bundling.ts"] = StringTestUtil.Dedent("""
				export class LazyModule<TModule> {
					constructor(private importCallback: () => Promise<TModule>) {}
				}

				export class LazyAction<
					TAction extends (...args: any[]) => any,
					TModule
				>  {
					constructor(_lazyModule: LazyModule<TModule>, _getter: (module: TModule) => TAction) {
					}
				}
"""),
            ["/home/src/workspaces/project/global.d.ts"] = StringTestUtil.Dedent("""
				interface PromiseConstructor {
					new <T>(): Promise<T>;
				}
				declare var Promise: PromiseConstructor;
				interface Promise<T> {
				}
"""),
            ["/home/src/workspaces/project/index.ts"] = StringTestUtil.Dedent("""
				import { LazyAction, LazyModule } from './bundling';
				const lazyModule = new LazyModule(() =>
					import('./lazyIndex')
				);
				export const lazyBar = new LazyAction(lazyModule, m => m.bar);
"""),
            ["/home/src/workspaces/project/lazyIndex.ts"] = StringTestUtil.Dedent("""
				export { default as bar } from './bar';
""") + lazyExtraContents,
            ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent($$"""
				{
					"compilerOptions": {
						"target": "es5",
						"declaration": true,
						"outDir": "obj",
						"incremental": true,
						"isolatedModules": {{(isolatedModules ? "true" : "false")}},
					},
				}
"""),
        };
    }

    // tscbuild_test.go:1274-1293
    [Fact]
    public void InferredTypeFromTransitiveModule()
    {
        new TscInput
        {
            subScenario = "inferred type from transitive module",
            files = GetBuildInferredTypeFromTransitiveModuleMap(false, ""),
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "param: string", ""),
                },
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "foobar()", "foobar(param: string)"),
                },
            ],
        }.RunAndAssert("inferredTypeFromTransitiveModule");
    }

    // tscbuild_test.go:1294-1312
    [Fact]
    public void InferredTypeFromTransitiveModuleWithIsolatedModules()
    {
        new TscInput
        {
            subScenario = "inferred type from transitive module with isolatedModules",
            files = GetBuildInferredTypeFromTransitiveModuleMap(true, ""),
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "param: string", ""),
                },
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "foobar()", "foobar(param: string)"),
                },
            ],
        }.RunAndAssert("inferredTypeFromTransitiveModule");
    }

    // tscbuild_test.go:1313-1346
    [Fact]
    public void ReportsErrorsInFilesAffectedByChangeInSignatureWithIsolatedModules()
    {
        new TscInput
        {
            subScenario = "reports errors in files affected by change in signature with isolatedModules",
            files = GetBuildInferredTypeFromTransitiveModuleMap(true, StringTestUtil.Dedent("""
				import { default as bar } from './bar';
				bar("hello");
""")),
            commandLineArgs = ["--b", "--verbose"],
            edits =
            [
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "param: string", ""),
                },
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "foobar()", "foobar(param: string)"),
                },
                new TscEdit
                {
                    caption = "incremental-declaration-changes",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/bar.ts", "param: string", ""),
                },
                new TscEdit
                {
                    caption = "Fix Error",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/lazyIndex.ts", """bar("hello")""", "bar()"),
                },
            ],
        }.RunAndAssert("inferredTypeFromTransitiveModule");
    }
}
