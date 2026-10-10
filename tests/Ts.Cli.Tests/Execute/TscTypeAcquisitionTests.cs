// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTypeAcquisition.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscTypeAcquisitionTests
{
    // tsc_test.go:4682-4703
    [Fact]
    public void ParseTsconfigWithTypeAcquisition()
    {
        new TscInput
        {
            subScenario = "parse tsconfig with typeAcquisition",
            files = new Dictionary<string, object>
            {
                // NOTE: the Go raw-string literal is written at column 0; C# raw strings strip the
                // common indentation, so the content is over-indented to keep the file bytes
                // identical to the Go input (and therefore to the baseline) after dedenting.
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent("""
                                    {
                                        "compilerOptions": {
                                            "composite": true,
                                            "noEmit": true,
                                        },
                                        "typeAcquisition": {
                                            "enable": true,
                                            "include": ["0.d.ts", "1.d.ts"],
                                            "exclude": ["0.js", "1.js"],
                                            "disableFilenameBasedTypeAcquisition": true,
                                        },
                                    }
"""),
            },
            commandLineArgs = [],
        }.RunAndAssert("typeAcquisition");
    }
}
