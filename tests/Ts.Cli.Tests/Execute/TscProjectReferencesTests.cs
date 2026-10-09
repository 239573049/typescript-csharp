// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscProjectReferences.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscProjectReferencesTests
{
    // tsc_test.go:4056-4077
    [Fact]
    public void WhenProjectReferencesCompositeProjectWithNoEmit()
    {
        RunCase(new TscInput
        {
            subScenario = "when project references composite project with noEmit",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/utils/index.ts"] = "export const x = 10;",
                ["/home/src/workspaces/solution/utils/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "noEmit": true
                        }
                    }
"""),
                ["/home/src/workspaces/solution/project/index.ts"] = "import { x } from \"../utils\";",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "references": [
                            { "path": "../utils" },
                        ],
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project"],
        });
    }

    // tsc_test.go:4078-4099
    [Fact]
    public void WhenProjectReferencesComposite()
    {
        RunCase(new TscInput
        {
            subScenario = "when project references composite",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/utils/index.ts"] = "export const x = 10;",
                ["/home/src/workspaces/solution/utils/index.d.ts"] = "export declare const x = 10;",
                ["/home/src/workspaces/solution/utils/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true
                        }
                    }
"""),
                ["/home/src/workspaces/solution/project/index.ts"] = "import { x } from \"../utils\";",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "references": [
                            { "path": "../utils" },
                        ],
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project"],
        });
    }

    // tsc_test.go:4100-4120
    [Fact]
    public void WhenProjectReferenceIsNotBuilt()
    {
        RunCase(new TscInput
        {
            subScenario = "when project reference is not built",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/utils/index.ts"] = "export const x = 10;",
                ["/home/src/workspaces/solution/utils/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true
                        }
                    }
"""),
                ["/home/src/workspaces/solution/project/index.ts"] = "import { x } from \"../utils\";",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "references": [
                            { "path": "../utils" },
                        ],
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project"],
        });
    }

    // tsc_test.go:4121-4134
    [Fact]
    public void WhenProjectContainsInvalidProjectReference()
    {
        RunCase(new TscInput
        {
            subScenario = "when project contains invalid project reference",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/project/index.ts"] = "export const x = 10;",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "references": [
                            { "path": "../utils" },
                        ],
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project"],
        });
    }

    // tsc_test.go:4135-4172
    [Fact]
    public void WhenProjectReferencesHaveInvalidFields()
    {
        RunCase(new TscInput
        {
            subScenario = "when project references have invalid fields",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/project/index.ts"] = "export const x = 10;",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "noEmit": true
                        },
                        "files": ["index.ts"],
                        "references": [
                            { "path": true },
                            { "circular": true },
                            { "path": "../utils", "circular": "yes" },
                            { "path": "" },
                            { "path": "../valid", "circular": true }
                        ]
                    }
"""),
                ["/home/src/workspaces/solution/utils/index.ts"] = "export const y = 10;",
                ["/home/src/workspaces/solution/utils/index.d.ts"] = "export declare const y = 10;",
                ["/home/src/workspaces/solution/utils/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true
                        }
                    }
"""),
                ["/home/src/workspaces/solution/valid/index.ts"] = "export const z = 10;",
                ["/home/src/workspaces/solution/valid/index.d.ts"] = "export declare const z = 10;",
                ["/home/src/workspaces/solution/valid/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true
                        }
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project"],
        });
    }

    // tsc_test.go:4173-4223
    [Fact]
    public void DefaultImportInteropUsesReferencedProjectSettings()
    {
        RunCase(new TscInput
        {
            subScenario = "default import interop uses referenced project settings",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/node_modules/ambiguous-package/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "ambiguous-package"
                    }
"""),
                ["/home/src/workspaces/project/node_modules/ambiguous-package/index.d.ts"] = "export declare const ambiguous: number;",
                ["/home/src/workspaces/project/node_modules/esm-package/package.json"] = StringTestUtil.Dedent("""
                    {
                        "name": "esm-package",
                        "type": "module"
                    }
"""),
                ["/home/src/workspaces/project/node_modules/esm-package/index.d.ts"] = "export declare const esm: number;",
                ["/home/src/workspaces/project/lib/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "rootDir": "src",
                            "outDir": "dist",
                            "module": "esnext",
                            "moduleResolution": "bundler",
                        },
                        "include": ["src"],
                    }
"""),
                ["/home/src/workspaces/project/lib/src/a.ts"] = "export const a = 0;",
                ["/home/src/workspaces/project/lib/dist/a.d.ts"] = "export declare const a = 0;",
                ["/home/src/workspaces/project/app/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "esnext",
                            "moduleResolution": "bundler",
                            "rootDir": "src",
                            "outDir": "dist",
                        },
                        "include": ["src"],
                        "references": [
                            { "path": "../lib" },
                        ],
                    }
"""),
                ["/home/src/workspaces/project/app/src/local.ts"] = "export const local = 0;",
                ["/home/src/workspaces/project/app/src/index.ts"] = StringTestUtil.Dedent("""
                    import local from "./local"; // Error
                    import esm from "esm-package"; // Error
                    import referencedSource from "../../lib/src/a"; // Error
                    import referencedDeclaration from "../../lib/dist/a"; // Error
                    import ambiguous from "ambiguous-package"; // Ok
"""),
            },
            commandLineArgs = ["--p", "app", "--pretty", "false"],
        });
    }

    // tsc_test.go:4224-4258
    [Fact]
    public void ReferencedProjectWithEsmNextModuleDisallowsSyntheticDefaultImports()
    {
        RunCase(new TscInput
        {
            subScenario = "referenced project with esnext module disallows synthetic default imports",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/lib/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "module": "esnext",
                            "moduleResolution": "bundler",
                            "rootDir": "src",
                            "outDir": "dist"
                        },
                        "include": ["src"]
                    }
"""),
                ["/home/src/workspaces/project/lib/src/utils.ts"] = "export const test = () => 'test';",
                ["/home/src/workspaces/project/lib/dist/utils.d.ts"] = "export declare const test: () => string;",
                ["/home/src/workspaces/project/app/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "esnext",
                            "moduleResolution": "bundler"
                        },
                        "references": [
                            { "path": "../lib" }
                        ]
                    }
"""),
                ["/home/src/workspaces/project/app/index.ts"] = StringTestUtil.Dedent("""
                    import TestSrc from '../lib/src/utils'; // Error
                    import TestDecl from '../lib/dist/utils'; // Error
                    console.log(TestSrc.test());
                    console.log(TestDecl.test());
"""),
            },
            commandLineArgs = ["--p", "app", "--pretty", "false"],
        });
    }

    // tsc_test.go:4259-4285
    [Fact]
    public void ReferencingAmbientConstEnumFromReferencedProjectWithPreserveConstEnums()
    {
        RunCase(new TscInput
        {
            subScenario = "referencing ambient const enum from referenced project with preserveConstEnums",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/utils/index.ts"] = "export const enum E { A = 1 }",
                ["/home/src/workspaces/solution/utils/index.d.ts"] = "export declare const enum E { A = 1 }",
                ["/home/src/workspaces/solution/utils/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "preserveConstEnums": true,
                        },
                    }
"""),
                ["/home/src/workspaces/solution/project/index.ts"] = "import { E } from \"../utils\"; E.A;",
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "isolatedModules": true,
                        },
                        "references": [
                            { "path": "../utils" },
                        ],
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project"],
        });
    }

    // tsc_test.go:4286-4328
    [Fact]
    public void ImportingConstEnumFromReferencedProjectWithPreserveConstEnumsAndVerbatimModuleSyntax()
    {
        RunCase(new TscInput
        {
            subScenario = "importing const enum from referenced project with preserveConstEnums and verbatimModuleSyntax",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/preserve/index.ts"] = "export const enum E { A = 1 }",
                ["/home/src/workspaces/solution/preserve/index.d.ts"] = "export declare const enum E { A = 1 }",
                ["/home/src/workspaces/solution/preserve/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "preserveConstEnums": true,
                        },
                    }
"""),
                ["/home/src/workspaces/solution/no-preserve/index.ts"] = "export const enum E { A = 1 }",
                ["/home/src/workspaces/solution/no-preserve/index.d.ts"] = "export declare const enum F { A = 1 }",
                ["/home/src/workspaces/solution/no-preserve/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "declaration": true,
                            "preserveConstEnums": false,
                        },
                    }
"""),
                ["/home/src/workspaces/solution/project/index.ts"] = StringTestUtil.Dedent("""
                    import { E } from "../preserve";
                    import { F } from "../no-preserve";
                    E.A;
                    F.A;
"""),
                ["/home/src/workspaces/solution/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "preserve",
                            "verbatimModuleSyntax": true,
                        },
                        "references": [
                            { "path": "../preserve" },
                            { "path": "../no-preserve" },
                        ],
                    }
"""),
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "project", "--pretty", "false"],
        });
    }

    // tsc_test.go:4329-4375
    [Fact]
    public void RewriteRelativeImportExtensionsProjectReferences1()
    {
        RunCase(new TscInput
        {
            subScenario = "rewriteRelativeImportExtensionsProjectReferences1",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/packages/common/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "rootDir": "src",
                            "outDir": "dist", 
                            "module": "nodenext"
                        }
                    }
"""),
                ["/home/src/workspaces/packages/common/package.json"] = StringTestUtil.Dedent("""
                    {
                            "name": "common",
                            "version": "1.0.0",
                            "type": "module",
                            "exports": {
                                ".": {
                                    "source": "./src/index.ts",
                                    "default": "./dist/index.js"
                                }
                            }
                    }
"""),
                ["/home/src/workspaces/packages/common/src/index.ts"] = "export {};",
                ["/home/src/workspaces/packages/common/dist/index.d.ts"] = "export {};",
                ["/home/src/workspaces/packages/main/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "nodenext",
                            "rewriteRelativeImportExtensions": true,
                            "rootDir": "src",
                            "outDir": "dist"
                        },
                        "references": [
                            { "path": "../common" }
                        ]
                    }
"""),
                ["/home/src/workspaces/packages/main/package.json"] = StringTestUtil.Dedent("""
                    {
                        "type": "module"
                    }
"""),
                ["/home/src/workspaces/packages/main/src/index.ts"] = "import {} from \"../../common/src/index.ts\";",
            },
            cwd = "/home/src/workspaces",
            commandLineArgs = ["-p", "packages/main", "--pretty", "false"],
        });
    }

    // tsc_test.go:4376-4408
    [Fact]
    public void RewriteRelativeImportExtensionsProjectReferences2()
    {
        RunCase(new TscInput
        {
            subScenario = "rewriteRelativeImportExtensionsProjectReferences2",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/src/tsconfig-base.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "module": "nodenext",
                            "composite": true,
                            "rootDir": ".",
                            "outDir": "../dist",
                            "rewriteRelativeImportExtensions": true
                        }
                    }
"""),
                ["/home/src/workspaces/solution/src/compiler/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "extends": "../tsconfig-base.json",
                        "compilerOptions": {}
                    }
"""),
                ["/home/src/workspaces/solution/src/compiler/parser.ts"] = "export {};",
                ["/home/src/workspaces/solution/dist/compiler/parser.d.ts"] = "export {};",
                ["/home/src/workspaces/solution/src/services/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "extends": "../tsconfig-base.json",
                        "compilerOptions": {},
                        "references": [
                            { "path": "../compiler" }
                        ]
                    }
"""),
                ["/home/src/workspaces/solution/src/services/services.ts"] = "import {} from \"../compiler/parser.ts\";",
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "src/services", "--pretty", "false"],
        });
    }

    // tsc_test.go:4409-4445
    [Fact]
    public void RewriteRelativeImportExtensionsProjectReferences3()
    {
        RunCase(new TscInput
        {
            subScenario = "rewriteRelativeImportExtensionsProjectReferences3",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/solution/src/tsconfig-base.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": { 
                            "module": "nodenext",
                            "composite": true,
                            "rewriteRelativeImportExtensions": true
                        }
                    }
"""),
                ["/home/src/workspaces/solution/src/compiler/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "extends": "../tsconfig-base.json",
                        "compilerOptions": {
                            "rootDir": ".",
                            "outDir": "../../dist/compiler"
                        }
                    }
"""),
                ["/home/src/workspaces/solution/src/compiler/parser.ts"] = "export {};",
                ["/home/src/workspaces/solution/dist/compiler/parser.d.ts"] = "export {};",
                ["/home/src/workspaces/solution/src/services/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "extends": "../tsconfig-base.json",
                        "compilerOptions": {
                            "rootDir": ".", 
                            "outDir": "../../dist/services"
                        },
                        "references": [
                            { "path": "../compiler" }
                        ]
                    }
"""),
                ["/home/src/workspaces/solution/src/services/services.ts"] = "import {} from \"../compiler/parser.ts\";",
            },
            cwd = "/home/src/workspaces/solution",
            commandLineArgs = ["--p", "src/services", "--pretty", "false"],
        });
    }

    // tsc_test.go:4446-4470
    [Fact]
    public void DefaultSetupWasCreatedCorrectly()
    {
        RunCase(new TscInput
        {
            subScenario = "default setup was created correctly",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/primary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        }
                    }
"""),
                ["/home/src/workspaces/project/primary/a.ts"] = "export { };",
                ["/home/src/workspaces/project/secondary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": [{
                            "path": "../primary"
                        }]
                    }
"""),
                ["/home/src/workspaces/project/secondary/b.ts"] = "import * as mod_1 from \"../primary/a\";",
            },
            commandLineArgs = ["--p", "primary/tsconfig.json"],
        });
    }

    // tsc_test.go:4471-4485
    [Fact]
    public void ErrorsWhenDeclarationIsFalse()
    {
        RunCase(new TscInput
        {
            subScenario = "errors when declaration = false",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/primary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                            "declaration": false
                        }
                    }
"""),
                ["/home/src/workspaces/project/primary/a.ts"] = "export { };",
            },
            commandLineArgs = ["--p", "primary/tsconfig.json"],
        });
    }

    // tsc_test.go:4486-4509
    [Fact]
    public void ErrorsWhenTheReferencedProjectDoesntHaveComposite()
    {
        RunCase(new TscInput
        {
            subScenario = "errors when the referenced project doesnt have composite",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/primary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": false,
                            "outDir": "bin",
                        }
                    }
"""),
                ["/home/src/workspaces/project/primary/a.ts"] = "export { };",
                ["/home/src/workspaces/project/reference/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "files": [ "b.ts" ],
                        "references": [ { "path": "../primary" } ]
                    }
"""),
                ["/home/src/workspaces/project/reference/b.ts"] = "import * as mod_1 from \"../primary/a\";",
            },
            commandLineArgs = ["--p", "reference/tsconfig.json"],
        });
    }

    // tsc_test.go:4510-4535
    [Fact]
    public void DoesNotErrorWhenTheReferencedProjectDoesntHaveCompositeIfItsAContainerProject()
    {
        RunCase(new TscInput
        {
            subScenario = "does not error when the referenced project doesnt have composite if its a container project",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/primary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": false,
                            "outDir": "bin",
                        }
                    }
"""),
                ["/home/src/workspaces/project/primary/a.ts"] = "export { };",
                ["/home/src/workspaces/project/reference/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "files": [ ],
                        "references": [{
                            "path": "../primary"
                        }]
                    }
"""),
                ["/home/src/workspaces/project/reference/b.ts"] = "import * as mod_1 from \"../primary/a\";",
            },
            commandLineArgs = ["--p", "reference/tsconfig.json"],
        });
    }

    // tsc_test.go:4536-4551
    [Fact]
    public void ErrorsWhenTheFileListIsNotExhaustive()
    {
        RunCase(new TscInput
        {
            subScenario = "errors when the file list is not exhaustive",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/primary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "files": [ "a.ts" ]
                    }
"""),
                ["/home/src/workspaces/project/primary/a.ts"] = "import * as b from './b'",
                ["/home/src/workspaces/project/primary/b.ts"] = "export {}",
            },
            commandLineArgs = ["--p", "primary/tsconfig.json"],
        });
    }

    // tsc_test.go:4552-4568
    [Fact]
    public void ErrorsWhenTheReferencedProjectDoesntExist()
    {
        RunCase(new TscInput
        {
            subScenario = "errors when the referenced project doesnt exist",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/primary/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": [{
                            "path": "../foo"
                        }]
                    }
"""),
                ["/home/src/workspaces/project/primary/a.ts"] = "export { };",
            },
            commandLineArgs = ["--p", "primary/tsconfig.json"],
        });
    }

    // tsc_test.go:4569-4592
    [Fact]
    public void RedirectsToTheOutputDtsFile()
    {
        RunCase(new TscInput
        {
            subScenario = "redirects to the output dts file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/alpha/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        }
                    }
"""),
                ["/home/src/workspaces/project/alpha/a.ts"] = "export const m: number = 3;",
                ["/home/src/workspaces/project/alpha/bin/a.d.ts"] = "export { };",
                ["/home/src/workspaces/project/beta/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": [ { "path": "../alpha" } ]
                    }
"""),
                ["/home/src/workspaces/project/beta/b.ts"] = "import { m } from '../alpha/a'",
            },
            commandLineArgs = ["--p", "beta/tsconfig.json", "--explainFiles"],
        });
    }

    // tsc_test.go:4593-4616
    [Fact]
    public void IssuesANiceErrorWhenTheInputFileIsMissing()
    {
        RunCase(new TscInput
        {
            subScenario = "issues a nice error when the input file is missing",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/alpha/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": []
                    }
"""),
                ["/home/src/workspaces/project/alpha/a.ts"] = "export const m: number = 3;",
                ["/home/src/workspaces/project/beta/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": [ { "path": "../alpha" } ]
                    }
"""),
                ["/home/src/workspaces/project/beta/b.ts"] = "import { m } from '../alpha/a'",
            },
            commandLineArgs = ["--p", "beta/tsconfig.json"],
        });
    }

    // tsc_test.go:4617-4642
    [Fact]
    public void IssuesANiceErrorWhenTheInputFileIsMissingWhenModuleReferenceIsNotRelative()
    {
        RunCase(new TscInput
        {
            subScenario = "issues a nice error when the input file is missing when module reference is not relative",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/alpha/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        }
                    }
"""),
                ["/home/src/workspaces/project/alpha/a.ts"] = "export const m: number = 3;",
                ["/home/src/workspaces/project/beta/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                            "paths": {
                                "@alpha/*": ["../alpha/*"],
                            },
                        },
                        "references": [ { "path": "../alpha" } ]
                    }
"""),
                ["/home/src/workspaces/project/beta/b.ts"] = "import { m } from '@alpha/a'",
            },
            commandLineArgs = ["--p", "beta/tsconfig.json"],
        });
    }

    // tsc_test.go:4643-4657
    [Fact]
    public void DoesntInferTheRootDirFromSourcePaths()
    {
        RunCase(new TscInput
        {
            subScenario = "doesnt infer the rootDir from source paths",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/alpha/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": []
                    }
"""),
                ["/home/src/workspaces/project/alpha/src/a.ts"] = "export const m: number = 3;",
            },
            commandLineArgs = ["--p", "alpha/tsconfig.json"],
        });
    }

    // tsc_test.go:4658-4674
    [Fact]
    public void ErrorsWhenAFileIsOutsideTheRootdir()
    {
        RunCase(new TscInput
        {
            // !!! sheetal rootDir error not reported
            subScenario = "errors when a file is outside the rootdir",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/alpha/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "composite": true,
                            "outDir": "bin",
                        },
                        "references": []
                    }
"""),
                ["/home/src/workspaces/project/alpha/src/a.ts"] = "import * as b from '../../beta/b'",
                ["/home/src/workspaces/project/beta/b.ts"] = "export { }",
            },
            commandLineArgs = ["--p", "alpha/tsconfig.json"],
        });
    }

    // tsc_test.go:4677-4679 — `for _, c := range cases { c.run(t, "projectReferences") }`.
    private static void RunCase(TscInput testCase) => testCase.RunAndAssert("projectReferences");
}
