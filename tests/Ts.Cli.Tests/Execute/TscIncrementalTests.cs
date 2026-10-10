// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscIncremental.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscIncrementalTests
{
    // tsc_test.go:1301-1349 — getConstEnumTest.
    private static TscInput GetConstEnumTest(string bdsContents, string changeEnumFile, string testSuffix) => new()
    {
        subScenario = "const enums" + testSuffix,
        files = new Dictionary<string, object>
        {
            ["/home/src/workspaces/project/a.ts"] = StringTestUtil.Dedent("""
                    import {A} from "./c"
                    let a = A.ONE
"""),
            ["/home/src/workspaces/project/b.d.ts"] = StringTestUtil.Dedent(bdsContents),
            ["/home/src/workspaces/project/c.ts"] = StringTestUtil.Dedent("""
                    import {A} from "./b"
                    let b = A.ONE
                    export {A}
"""),
            ["/home/src/workspaces/project/worker.d.ts"] = StringTestUtil.Dedent("""
                    export const enum AWorker {
                        ONE = 1
                    }
"""),
        },
        commandLineArgs = ["-i", "a.ts", "--tsbuildinfofile", "a.tsbuildinfo"],
        edits =
        [
            new TscEdit
            {
                caption = "change enum value",
                edit = sys => sys.ReplaceFileText(changeEnumFile, "1", "2"),
            },
            new TscEdit
            {
                caption = "change enum value again",
                edit = sys => sys.ReplaceFileText(changeEnumFile, "2", "3"),
            },
            new TscEdit
            {
                caption = "something else changes in b.d.ts",
                edit = sys => sys.AppendFileText("/home/src/workspaces/project/b.d.ts", "export const randomThing = 10;"),
            },
            new TscEdit
            {
                caption = "something else changes in b.d.ts again",
                edit = sys => sys.AppendFileText("/home/src/workspaces/project/b.d.ts", "export const randomThing2 = 10;"),
            },
        ],
    };

    // tsc_test.go:1351-1379
    [Fact]
    public void SerializingErrorChain()
    {
        new TscInput
        {
            subScenario = "serializing error chain",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "incremental": true,
                        "strict": true,
                        "jsx": "react",
                        "module": "esnext",
                    },
                }
"""),
                ["/home/src/workspaces/project/index.tsx"] = StringTestUtil.Dedent("""
                    declare namespace JSX {
                        interface ElementChildrenAttribute { children: {}; }
                        interface IntrinsicElements { div: {} }
                    }

                    declare var React: any;

                    declare function Component(props: never): any;
                    declare function Component(props: { children?: number }): any;
                    (<Component>
                        <div />
                        <div />
                    </Component>)
"""),
            },
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1380-1394
    [Fact]
    public void SerializingCompositeProject()
    {
        new TscInput
        {
            subScenario = "serializing composite project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                        "strict": true,
                        "module": "esnext",
                    },
                }
"""),
                ["/home/src/workspaces/project/index.tsx"] = "export const a = 1;",
                ["/home/src/workspaces/project/other.ts"] = "export const b = 2;",
            },
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1395-1441
    [Fact]
    public void ChangeToModifierOfClassExpressionFieldWithDeclarationEmitEnabled()
    {
        new TscInput
        {
            subScenario = "change to modifier of class expression field with declaration emit enabled",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                { 
                    "compilerOptions": {
                        "module": "esnext",
                        "declaration": true
                    }
                }
"""),
                ["/home/src/workspaces/project/main.ts"] = StringTestUtil.Dedent("""
                        import MessageablePerson from './MessageablePerson.js';
                        function logMessage( person: MessageablePerson ) {
                            console.log( person.message );
                        }
"""),
                ["/home/src/workspaces/project/MessageablePerson.ts"] = StringTestUtil.Dedent("""
                        const Messageable = () => {
                            return class MessageableClass {
                                public message = 'hello';
                            }
                        };
                        const wrapper = () => Messageable();
                        type MessageablePerson = InstanceType<ReturnType<typeof wrapper>>;
                        export default MessageablePerson;
"""),
                [TscTestDefaults.TscLibPath + "/lib.d.ts"] = TscTestDefaults.TscDefaultLibContent + "\n" + StringTestUtil.Dedent("""
                    type ReturnType<T extends (...args: any) => any> = T extends (...args: any) => infer R ? R : any;
                    type InstanceType<T extends abstract new (...args: any) => any> = T extends abstract new (...args: any) => infer R ? R : any;
"""),
            },
            commandLineArgs = ["--incremental"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "modify public to protected",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/MessageablePerson.ts", "public", "protected"),
                },
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "modify protected to public",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/MessageablePerson.ts", "protected", "public"),
                },
                TscEdit.NoChange,
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1442-1487
    [Fact]
    public void ChangeToModifierOfClassExpressionField()
    {
        new TscInput
        {
            subScenario = "change to modifier of class expression field",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                { 
                    "compilerOptions": { 
                        "module": "esnext"
                    }
                }
"""),
                ["/home/src/workspaces/project/main.ts"] = StringTestUtil.Dedent("""
                    import MessageablePerson from './MessageablePerson.js';
                    function logMessage( person: MessageablePerson ) {
                        console.log( person.message );
                    }
"""),
                ["/home/src/workspaces/project/MessageablePerson.ts"] = StringTestUtil.Dedent("""
                    const Messageable = () => {
                        return class MessageableClass {
                            public message = 'hello';
                        }
                    };
                    const wrapper = () => Messageable();
                    type MessageablePerson = InstanceType<ReturnType<typeof wrapper>>;
                    export default MessageablePerson;
"""),
                [TscTestDefaults.TscLibPath + "/lib.d.ts"] = TscTestDefaults.TscDefaultLibContent + "\n" + StringTestUtil.Dedent("""
                    type ReturnType<T extends (...args: any) => any> = T extends (...args: any) => infer R ? R : any;
                    type InstanceType<T extends abstract new (...args: any) => any> = T extends abstract new (...args: any) => infer R ? R : any;
"""),
            },
            commandLineArgs = ["--incremental"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "modify public to protected",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/MessageablePerson.ts", "public", "protected"),
                },
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "modify protected to public",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/MessageablePerson.ts", "protected", "public"),
                },
                TscEdit.NoChange,
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1488-1505
    [Fact]
    public void WhenPassingFilenameForBuildinfoOnCommandline()
    {
        new TscInput
        {
            subScenario = "when passing filename for buildinfo on commandline",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "target": "es5",
                        "module": "commonjs"
                    },
                    "include": [
                        "src/**/*.ts"
                    ],
                }
"""),
            },
            commandLineArgs = ["--incremental", "--tsBuildInfoFile", ".tsbuildinfo", "--explainFiles"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1506-1520
    [Fact]
    public void WhenPassingRootDirFromCommandline()
    {
        new TscInput
        {
            subScenario = "when passing rootDir from commandline",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "incremental": true,
                        "outDir": "dist"
                    }
                }
"""),
            },
            commandLineArgs = ["--rootDir", "src"],
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1521-1538
    [Fact]
    public void WithOnlyDtsFiles()
    {
        new TscInput
        {
            subScenario = "with only dts files",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.d.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/src/another.d.ts"] = "export const y = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = "{}",
            },
            commandLineArgs = ["--incremental"],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "modify d.ts file",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/src/main.d.ts", "export const xy = 100;"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1539-1553
    [Fact]
    public void WhenPassingRootDirIsInTheTsconfig()
    {
        new TscInput
        {
            subScenario = "when passing rootDir is in the tsconfig",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "incremental": true,
                        "outDir": "dist",
                        "rootDir": "./"
                    }
                }
"""),
            },
            edits = TscEdit.NoChangeOnlyEdit,
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1554-1570
    [Fact]
    public void TsbuildinfoHasError()
    {
        new TscInput
        {
            subScenario = "tsbuildinfo has error",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/main.ts"] = "export const x = 10;",
                ["/home/src/workspaces/project/tsconfig.json"] = "{}",
                ["/home/src/workspaces/project/tsconfig.tsbuildinfo"] = "Some random string",
            },
            commandLineArgs = ["-i"],
            edits =
            [
                new TscEdit
                {
                    caption = "tsbuildinfo written has error",
                    edit = sys => sys.PrependFileText("/home/src/workspaces/project/tsconfig.tsbuildinfo", "Some random string"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1571-1631
    [Fact]
    public void WhenGlobalFileIsAddedTheSignaturesAreUpdated()
    {
        new TscInput
        {
            subScenario = "when global file is added, the signatures are updated",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/main.ts"] = StringTestUtil.Dedent("""
                    /// <reference path="./filePresent.ts"/>
                    /// <reference path="./fileNotFound.ts"/>
                    function main() { }
"""),
                ["/home/src/workspaces/project/src/anotherFileWithSameReferenes.ts"] = StringTestUtil.Dedent("""
                    /// <reference path="./filePresent.ts"/>
                    /// <reference path="./fileNotFound.ts"/>
                    function anotherFileWithSameReferenes() { }
"""),
                ["/home/src/workspaces/project/src/filePresent.ts"] = "function something() { return 10; }",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": { "composite": true },
                    "include": ["src/**/*.ts"],
                }
"""),
            },
            commandLineArgs = [],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "Modify main file",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/src/main.ts", "something();"),
                },
                new TscEdit
                {
                    caption = "Modify main file again",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/src/main.ts", "something();"),
                },
                new TscEdit
                {
                    caption = "Add new file and update main file",
                    edit = sys =>
                    {
                        sys.WriteFileNoError("/home/src/workspaces/project/src/newFile.ts", "function foo() { return 20; }");
                        sys.PrependFileText(
                            "/home/src/workspaces/project/src/main.ts",
                            "/// <reference path=\"./newFile.ts\"/>\n");
                        sys.AppendFileText("/home/src/workspaces/project/src/main.ts", "foo();");
                    },
                },
                new TscEdit
                {
                    caption = "Write file that could not be resolved",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/src/fileNotFound.ts", "function something2() { return 20; }"),
                },
                new TscEdit
                {
                    caption = "Modify main file",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/src/main.ts", "something();"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1632-1659
    [Fact]
    public void ReactJsxEmitModeWithNoBackingTypesFoundDoesntCrash()
    {
        new TscInput
        {
            subScenario = "react-jsx-emit-mode with no backing types found doesnt crash",
            files = new Dictionary<string, object>
            {
                // js needs to be present so there's a resolution result
                ["/home/src/workspaces/project/node_modules/react/jsx-runtime.js"] = "export {}",
                // doesn't contain a jsx-runtime definition
                ["/home/src/workspaces/project/node_modules/@types/react/index.d.ts"] = StringTestUtil.Dedent("""
                    export {};
                    declare global {
                        namespace JSX {
                            interface Element {}
                            interface IntrinsicElements {
                                div: {
                                    propA?: boolean;
                                };
                            }
                        }
                    }
"""),
                ["/home/src/workspaces/project/src/index.tsx"] = "export const App = () => <div propA={true}></div>;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                { 
                    "compilerOptions": { 
                        "module": "commonjs",
                        "jsx": "react-jsx", 
                        "incremental": true, 
                        "jsxImportSource": "react" 
                    } 
                }
"""),
            },
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1660-1688
    [Fact]
    public void ReactJsxEmitModeWithNoBackingTypesFoundDoesntCrashUnderStrict()
    {
        new TscInput
        {
            subScenario = "react-jsx-emit-mode with no backing types found doesnt crash under --strict",
            files = new Dictionary<string, object>
            {
                // js needs to be present so there's a resolution result
                ["/home/src/workspaces/project/node_modules/react/jsx-runtime.js"] = "export {}",
                // doesn't contain a jsx-runtime definition
                ["/home/src/workspaces/project/node_modules/@types/react/index.d.ts"] = StringTestUtil.Dedent("""
                    export {};
                    declare global {
                        namespace JSX {
                            interface Element {}
                            interface IntrinsicElements {
                                div: {
                                    propA?: boolean;
                                };
                            }
                        }
                    }
"""),
                ["/home/src/workspaces/project/src/index.tsx"] = "export const App = () => <div propA={true}></div>;",
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                { 
                    "compilerOptions": { 
                        "module": "commonjs",
                        "jsx": "react-jsx", 
                        "incremental": true, 
                        "jsxImportSource": "react" 
                    } 
                }
"""),
            },
            commandLineArgs = ["--strict"],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1689-1712
    [Fact]
    public void ChangeToTypeThatGetsUsedAsGlobalThroughExportInAnotherFile()
    {
        new TscInput
        {
            subScenario = "change to type that gets used as global through export in another file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true
                    }
                }
"""),
                ["/home/src/workspaces/project/class1.ts"] = StringTestUtil.Dedent("""
                    const a: MagicNumber = 1;
                    console.log(a);
"""),
                ["/home/src/workspaces/project/constants.ts"] = "export default 1;",
                ["/home/src/workspaces/project/types.d.ts"] = "type MagicNumber = typeof import('./constants').default",
            },
            edits =
            [
                new TscEdit
                {
                    caption = "Modify imports used in global file",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/constants.ts", "export default 2;"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1713-1737
    [Fact]
    public void ChangeToTypeThatGetsUsedAsGlobalThroughExportInAnotherFileThroughIndirectImport()
    {
        new TscInput
        {
            subScenario = "change to type that gets used as global through export in another file through indirect import",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true
                    }
                }
"""),
                ["/home/src/workspaces/project/class1.ts"] = StringTestUtil.Dedent("""
                    const a: MagicNumber = 1;
                    console.log(a);
"""),
                ["/home/src/workspaces/project/constants.ts"] = "export default 1;",
                ["/home/src/workspaces/project/reexport.ts"] = "export { default as ConstantNumber } from \"./constants\"",
                ["/home/src/workspaces/project/types.d.ts"] = "type MagicNumber = typeof import('./reexport').ConstantNumber",
            },
            edits =
            [
                new TscEdit
                {
                    caption = "Modify imports used in global file",
                    edit = sys => sys.WriteFileNoError("/home/src/workspaces/project/constants.ts", "export default 2;"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1738-1758
    [Fact]
    public void WhenFileIsDeleted()
    {
        new TscInput
        {
            subScenario = "when file is deleted",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                        "outDir": "outDir"
                    }
                }
"""),
                ["/home/src/workspaces/project/file1.ts"] = "export class  C { }",
                ["/home/src/workspaces/project/file2.ts"] = "export class D { }",
            },
            edits =
            [
                new TscEdit
                {
                    caption = "delete file with imports",
                    edit = sys => sys.RemoveNoError("/home/src/workspaces/project/file2.ts"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1759-1810
    [Fact]
    public void GeneratesTyperefsCorrectly()
    {
        new TscInput
        {
            subScenario = "generates typerefs correctly",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                        "outDir": "outDir",
                        "checkJs": true
                    },
                    "include": ["src"],
                }
"""),
                ["/home/src/workspaces/project/src/box.ts"] = StringTestUtil.Dedent("""
                    export interface Box<T> {
                        unbox(): T
                    }
"""),
                ["/home/src/workspaces/project/src/bug.js"] = StringTestUtil.Dedent("""
                    import * as B from "./box.js"
                    import * as W from "./wrap.js"

                    /**
                     * @template {object} C
                     * @param {C} source
                     * @returns {W.Wrap<C>}
                     */
                    const wrap = source => {
                    throw source
                    }

                    /**
                     * @returns {B.Box<number>}
                     */
                    const box = (n = 0) => ({ unbox: () => n })

                    export const bug = wrap({ n: box(1) });
"""),
                ["/home/src/workspaces/project/src/wrap.ts"] = StringTestUtil.Dedent("""
                    export type Wrap<C> = {
                        [K in keyof C]: { wrapped: C[K] }
                    }
"""),
            },
            edits =
            [
                new TscEdit
                {
                    caption = "modify js file",
                    edit = sys => sys.AppendFileText("/home/src/workspaces/project/src/bug.js", "export const something = 1;"),
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1812-1816
    [Fact]
    public void ConstEnums() => GetConstEnumTest(StringTestUtil.Dedent("""
            export const enum A {
                ONE = 1
            }
"""), "/home/src/workspaces/project/b.d.ts", "").RunAndAssert("incremental");

    // tsc_test.go:1817-1822
    [Fact]
    public void ConstEnumsAliased() => GetConstEnumTest(StringTestUtil.Dedent("""
            export const enum AWorker {
                ONE = 1
            }
            export { AWorker as A };
"""), "/home/src/workspaces/project/b.d.ts", " aliased").RunAndAssert("incremental");

    // tsc_test.go:1823-1824
    [Fact]
    public void ConstEnumsAliasedInDifferentFile() => GetConstEnumTest(
        "export { AWorker as A } from \"./worker\";",
        "/home/src/workspaces/project/worker.d.ts",
        " aliased in different file").RunAndAssert("incremental");

    // tsc_test.go:1825-1904
    [Fact]
    public void OptionChangesWithComposite()
    {
        new TscInput
        {
            subScenario = "option changes with composite",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                    }
                }
"""),
                ["/home/src/workspaces/project/a.ts"] = "export const a = 10;const aLocal = 10;",
                ["/home/src/workspaces/project/b.ts"] = "export const b = 10;const bLocal = 10;",
                ["/home/src/workspaces/project/c.ts"] = "import { a } from \"./a\";export const c = a;",
                ["/home/src/workspaces/project/d.ts"] = "import { b } from \"./b\";export const d = b;",
            },
            edits =
            [
                new TscEdit
                {
                    caption = "with sourceMap",
                    commandLineArgs = ["--sourceMap"],
                },
                new TscEdit
                {
                    caption = "should re-emit only js so they dont contain sourcemap",
                },
                new TscEdit
                {
                    caption = "with declaration should not emit anything",
                    commandLineArgs = ["--declaration"],
                    // discrepancyExplanation: () => [
                    //     `Clean build tsbuildinfo will have compilerOptions with composite and ${option.replace(/-/g, "")}`,
                    //     `Incremental build will detect that it doesnt need to rebuild so tsbuild info is from before which has option composite only`,
                    // ],
                },
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "with declaration and declarationMap",
                    commandLineArgs = ["--declaration", "--declarationMap"],
                },
                new TscEdit
                {
                    caption = "should re-emit only dts so they dont contain sourcemap",
                },
                new TscEdit
                {
                    caption = "with emitDeclarationOnly should not emit anything",
                    commandLineArgs = ["--emitDeclarationOnly"],
                    // discrepancyExplanation: () => [
                    //     `Clean build tsbuildinfo will have compilerOptions with composite and ${option.replace(/-/g, "")}`,
                    //     `Incremental build will detect that it doesnt need to rebuild so tsbuild info is from before which has option composite only`,
                    // ],
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
                    commandLineArgs = ["--declaration"],
                    // discrepancyExplanation: () => [
                    //     `Clean build tsbuildinfo will have compilerOptions with composite and ${option.replace(/-/g, "")}`,
                    //     `Incremental build will detect that it doesnt need to rebuild so tsbuild info is from before which has option composite only`,
                    // ],
                },
                new TscEdit
                {
                    caption = "with inlineSourceMap",
                    commandLineArgs = ["--inlineSourceMap"],
                },
                new TscEdit
                {
                    caption = "with sourceMap",
                    commandLineArgs = ["--sourceMap"],
                },
                new TscEdit
                {
                    caption = "declarationMap enabling",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/tsconfig.json", "\"composite\": true,", "\"composite\": true,        \"declarationMap\": true"),
                },
                new TscEdit
                {
                    caption = "with sourceMap should not emit d.ts",
                    commandLineArgs = ["--sourceMap"],
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1905-1979
    [Fact]
    public void OptionChangesWithIncremental()
    {
        new TscInput
        {
            subScenario = "option changes with incremental",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "incremental": true,
                    }
                }
"""),
                ["/home/src/workspaces/project/a.ts"] = "export const a = 10;const aLocal = 10;",
                ["/home/src/workspaces/project/b.ts"] = "export const b = 10;const bLocal = 10;",
                ["/home/src/workspaces/project/c.ts"] = "import { a } from \"./a\";export const c = a;",
                ["/home/src/workspaces/project/d.ts"] = "import { b } from \"./b\";export const d = b;",
            },
            edits =
            [
                new TscEdit
                {
                    caption = "with sourceMap",
                    commandLineArgs = ["--sourceMap"],
                },
                new TscEdit
                {
                    caption = "should re-emit only js so they dont contain sourcemap",
                },
                new TscEdit
                {
                    caption = "with declaration, emit Dts and should not emit js",
                    commandLineArgs = ["--declaration"],
                },
                new TscEdit
                {
                    caption = "with declaration and declarationMap",
                    commandLineArgs = ["--declaration", "--declarationMap"],
                },
                new TscEdit
                {
                    caption = "no change",
                    // discrepancyExplanation: () => [
                    //     `Clean build tsbuildinfo will have compilerOptions {}`,
                    //     `Incremental build will detect that it doesnt need to rebuild so tsbuild info is from before which has option declaration and declarationMap`,
                    // ],
                },
                new TscEdit
                {
                    caption = "local change",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/a.ts", "Local = 1", "Local = 10"),
                },
                new TscEdit
                {
                    caption = "with declaration and declarationMap",
                    commandLineArgs = ["--declaration", "--declarationMap"],
                },
                new TscEdit
                {
                    caption = "no change",
                    // discrepancyExplanation: () => [
                    //     `Clean build tsbuildinfo will have compilerOptions {}`,
                    //     `Incremental build will detect that it doesnt need to rebuild so tsbuild info is from before which has option declaration and declarationMap`,
                    // ],
                },
                new TscEdit
                {
                    caption = "with inlineSourceMap",
                    commandLineArgs = ["--inlineSourceMap"],
                },
                new TscEdit
                {
                    caption = "with sourceMap",
                    commandLineArgs = ["--sourceMap"],
                },
                new TscEdit
                {
                    caption = "emit js files",
                },
                new TscEdit
                {
                    caption = "with declaration and declarationMap",
                    commandLineArgs = ["--declaration", "--declarationMap"],
                },
                new TscEdit
                {
                    caption = "with declaration and declarationMap, should not re-emit",
                    commandLineArgs = ["--declaration", "--declarationMap"],
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:1980-2007
    [Fact]
    public void WhenThereIsBindDiagnosticsThatsIgnored()
    {
        new TscInput
        {
            subScenario = "when there is bind diagnostics thats ignored",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "skipLibCheck": true,
                        "incremental": true,
                    }
                }
"""),
                ["/home/src/workspaces/project/a.ts"] = "export const a = 10;",
                ["/home/src/workspaces/project/b.d.ts"] = StringTestUtil.Dedent("""
                    interface NoName {
                        Profiler: new ({ sampleInterval: number, maxBufferSize: number }) => {
                            stop: () => Promise<any>;
                        };
                    }
"""),
            },
            commandLineArgs = [""],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "no change and tsc -b",
                    commandLineArgs = ["-b", "-v"],
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:2008-2055
    [Fact]
    public void CompileIncrementalWithCaseInsensitiveFileNames()
    {
        new TscInput
        {
            subScenario = "Compile incremental with case insensitive file names",
            commandLineArgs = ["-p", "."],
            files = new Dictionary<string, object>
            {
                ["/home/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "incremental": true
                        },
                    }
"""),
                ["/home/project/src/index.ts"] = StringTestUtil.Dedent("""
                    import type { Foo1 } from 'lib1';
                    import type { Foo2 } from 'lib2';
                    export const foo1: Foo1 = { foo: "a" };
                    export const foo2: Foo2 = { foo: "b" };
"""),
                ["/home/node_modules/lib1/index.d.ts"] = StringTestUtil.Dedent("""
                    import type { Foo } from 'someLib';
                    export type { Foo as Foo1 };
"""),
                ["/home/node_modules/lib1/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "lib1"
                    }
"""),
                ["/home/node_modules/lib2/index.d.ts"] = StringTestUtil.Dedent("""
                    import type { Foo } from 'somelib';
                    export type { Foo as Foo2 };
                    export declare const foo2: Foo;
"""),
                ["/home/node_modules/lib2/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "lib2"
                    }
"""),
                ["/home/node_modules/someLib/index.d.ts"] = StringTestUtil.Dedent("""
                    import type { Str } from 'otherLib';
                    export type Foo = { foo: Str; };
"""),
                ["/home/node_modules/someLib/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "somelib"
                    }
"""),
                ["/home/node_modules/otherLib/index.d.ts"] = StringTestUtil.Dedent("""
                    export type Str = string;
"""),
                ["/home/node_modules/otherLib/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "otherlib"
                    }
"""),
            },
            cwd = "/home/project",
            ignoreCase = true,
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:2056-2101
    [Fact]
    public void ConstEnumsWithRefCycle()
    {
        new TscInput
        {
            subScenario = "const enums with refCycle",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/file.ts"] = StringTestUtil.Dedent("""
                    import {A} from "./c"
                    let a = A.ONE
"""),
                ["/home/src/workspaces/project/b.ts"] = StringTestUtil.Dedent("""
                    import { AWorker } from "./aworker"
                    import { A as ACycle } from "./c"
                    export const enum A {
                        ONE = 1
                    }
"""),
                ["/home/src/workspaces/project/c.ts"] = StringTestUtil.Dedent("""
                    import {A} from "./b"
                    let b = A.ONE
                    export {A}
"""),
                ["/home/src/workspaces/project/aworker.ts"] = StringTestUtil.Dedent("""
                    export const AWorker  = 10
"""),
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "composite": true,
                    }
                }
"""),
            },
            commandLineArgs = [],
            edits =
            [
                new TscEdit
                {
                    caption = "change aworker",
                    edit = sys => sys.ReplaceFileText("/home/src/workspaces/project/aworker.ts", "10", "20"),
                },
                new TscEdit
                {
                    caption = "change aworker and enum value",
                    edit = sys =>
                    {
                        sys.ReplaceFileText("/home/src/workspaces/project/aworker.ts", "20", "30");
                        sys.ReplaceFileText("/home/src/workspaces/project/b.ts", "1", "2");
                    },
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:2102-2190
    [Fact]
    public void InternalSymbolnameInTsbuildInfo()
    {
        new TscInput
        {
            subScenario = "internal symbolname in tsbuildInfo",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                {
                    "compilerOptions": {
                        "target": "es2017",
                        "strict": true,
                        "esModuleInterop": true
                    }
                }
"""),
                ["/home/src/workspaces/project/a.ts"] = StringTestUtil.Dedent("""
                    const createFileListFromFiles = (files: File[]): FileList => {
                    const fileList: FileList = {
                        length: files.length,
                        item: (index: number): File | null => files[index] || null,
                        [Symbol.iterator]: function* (): IterableIterator<File> {
                        for (const file of files) yield file;
                        },
                        ...files,
                    } as unknown as FileList;

                    return fileList;
                    };
"""),
                [TscTestDefaults.GetTestLibPathFor("es2015.iterable")] = StringTestUtil.Dedent("""
                    interface SymbolConstructor {
                        readonly iterator: unique symbol;
                    }
                    interface IteratorYieldResult<TYield> {
                        done?: false;
                        value: TYield;
                    }
                    interface IteratorReturnResult<TReturn> {
                        done: true;
                        value: TReturn;
                    }
                    type IteratorResult<T, TReturn = any> = IteratorYieldResult<T> | IteratorReturnResult<TReturn>;
                    interface Iterator<T, TReturn = any, TNext = any> {
                        // NOTE: 'next' is defined using a tuple to ensure we report the correct assignability errors in all places.
                        next(...[value]: [] | [TNext]): IteratorResult<T, TReturn>;
                        return?(value?: TReturn): IteratorResult<T, TReturn>;
                        throw?(e?: any): IteratorResult<T, TReturn>;
                    }
                    interface Iterable<T, TReturn = any, TNext = any> {
                        [Symbol.iterator](): Iterator<T, TReturn, TNext>;
                    }
                    interface IterableIterator<T, TReturn = any, TNext = any> extends Iterator<T, TReturn, TNext> {
                        [Symbol.iterator](): IterableIterator<T, TReturn, TNext>;
                    }
                    interface IteratorObject<T, TReturn = unknown, TNext = unknown> extends Iterator<T, TReturn, TNext> {
                        [Symbol.iterator](): IteratorObject<T, TReturn, TNext>;
                    }
                    type BuiltinIteratorReturn = intrinsic;
                    interface ArrayIterator<T> extends IteratorObject<T, BuiltinIteratorReturn, unknown> {
                        [Symbol.iterator](): ArrayIterator<T>;
                    }
                    interface Array<T> {
                        [Symbol.iterator](): ArrayIterator<T>;
                        entries(): ArrayIterator<[number, T]>;
                        keys(): ArrayIterator<number>;
                        values(): ArrayIterator<T>;
                    }
"""),
                [TscTestDefaults.GetTestLibPathFor("es2017.full")] = StringTestUtil.Dedent("""
                    /// <reference lib="es2015.iterable"/>
                    interface File {
                    }
                    interface FileList {
                        readonly length: number;
                        item(index: number): File | null;
                        [index: number]: File;
                        [Symbol.iterator](): ArrayIterator<File>;
                    }
""") + TscTestDefaults.TscDefaultLibContent,
            },
            commandLineArgs = [""],
            edits =
            [
                TscEdit.NoChange,
                new TscEdit
                {
                    caption = "no change with incremental",
                    commandLineArgs = ["--incremental"],
                },
                new TscEdit
                {
                    caption = "no change with incremental that reads buildInfo",
                    commandLineArgs = ["--incremental"],
                },
            ],
        }.RunAndAssert("incremental");
    }

    // tsc_test.go:2191-2221
    [Fact]
    public void JsFileWithImportInJsdocInCompositeProject()
    {
        new TscInput
        {
            subScenario = "js file with import in jsdoc in composite project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = "{\"compilerOptions\": {\"allowJs\": true, \"composite\": true}}",
                ["/home/src/workspaces/project/index.js"] = StringTestUtil.Dedent("""
                    test("", async function () {
                      ;(/** @type {typeof import("a")} */ ({}))
                    })

                    test("", async function () {
                      ;(/** @type {typeof import("a")} */ a)
                    })

                    test("", async function () {
                      (/** @type {typeof import("a")} */ ({}))
                      ;(/** @type {typeof import("a")} */ ({}))
                    })

                    test("", async function () {
                      (/** @type {typeof import("a")} */ a)
                      ;(/** @type {typeof import("a")} */ a)
                    })

                    test("", async function () {
                      (/** @type {typeof import("a")} */ ({}))
                      ;(/** @type {typeof import("a")} */ ({}))
                    })
"""),
            },
            commandLineArgs = ["--noEmit"],
        }.RunAndAssert("incremental");
    }
}
