// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestGenerateTrace.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscGenerateTraceTests
{
    // tsc_test.go:4708-4727
    [Fact]
    public void GenerateTraceGeneratesTypesFile()
    {
        new TscInput
        {
            subScenario = "generateTrace generates types file",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true,
                            "noEmit": true
                        }
                    }
"""),
                ["/home/src/workspaces/project/a.ts"] = StringTestUtil.Dedent("""
                    interface Person {
                        name: string;
                        age: number;
                    }
                    const p: Person = { name: "Alice", age: 30 };
"""),
            },
            commandLineArgs = ["--generateTrace", "/home/src/workspaces/project/trace", "--singleThreaded"],
        }.RunAndAssert("generateTrace");
    }

    // tsc_test.go:4728-4752
    [Fact]
    public void GenerateTraceWithMultipleFilesAndComplexTypes()
    {
        new TscInput
        {
            subScenario = "generateTrace with multiple files and complex types",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                    {
                        "compilerOptions": {
                            "strict": true,
                            "noEmit": true
                        }
                    }
"""),
                ["/home/src/workspaces/project/types.ts"] = StringTestUtil.Dedent("""
                    export interface Container<T> {
                        value: T;
                        map<U>(fn: (x: T) => U): Container<U>;
                    }
                    export type Nullable<T> = T | null | undefined;
"""),
                ["/home/src/workspaces/project/main.ts"] = StringTestUtil.Dedent("""
                    import { Container, Nullable } from "./types";
                    const c: Container<number> = { value: 42, map: (fn) => ({ value: fn(42), map: c.map }) };
                    const n: Nullable<string> = "hello";
"""),
            },
            commandLineArgs = ["--generateTrace", "/home/src/workspaces/project/trace", "--singleThreaded"],
        }.RunAndAssert("generateTrace");
    }
}
