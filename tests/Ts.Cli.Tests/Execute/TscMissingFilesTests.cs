// Ported from tsgo internal/execute/tsctests/tsc_test.go — TestTscMissingFiles.
//
// The Go file splices these configs in as raw string literals whose first line is flush left
// while the closing lines sit five tabs in; an equivalent C# literal has to keep exactly those
// bytes, because stringtestutil.Dedent only strips the indentation *common* to every line.

using System.Collections.Generic;
using Ts.TestUtil;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscMissingFilesTests
{
    // tsc_test.go:254-264
    [Fact]
    public void FileInTsconfigDoesNotExist()
    {
        new TscInput
        {
            subScenario = "file in tsconfig does not exist",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent(
"""
{
                    "files": ["./src/doesNotExist.ts"]
                    }
"""),
            },
            commandLineArgs = ["-p", "./tsconfig.json"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:265-275
    [Fact]
    public void ExtensionlessFileInTsconfigDoesNotExist()
    {
        new TscInput
        {
            subScenario = "extensionless file in tsconfig does not exist",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/tsconfig.json"] = StringTestUtil.Dedent(
"""
{
                    "files": ["./src/doesNotExist"]
                    }
"""),
            },
            commandLineArgs = ["-p", "./tsconfig.json"],
        }.RunAndAssert("commandLine");
    }

    // tsc_test.go:276-292
    [Fact]
    public void ExtensionlessFileInExtendedTsconfigInDifferentFolderDoesNotExist()
    {
        new TscInput
        {
            subScenario = "extensionless file in extended tsconfig in different folder does not exist",
            files = new Dictionary<string, object>
            {
                ["/home/src/workspaces/project/src/tsconfig.json"] = StringTestUtil.Dedent(
"""
{
                    "extends": "./../tsconfig.base.json",
                    }
"""),
                ["/home/src/workspaces/project/src/oops.ts"] = "export const abc = 10;",
                ["/home/src/workspaces/project/tsconfig.base.json"] = StringTestUtil.Dedent(
"""
{
                    "files": ["./oops"],
                    }
"""),
            },
            commandLineArgs = ["-p", "./src/tsconfig.json"],
        }.RunAndAssert("commandLine");
    }
}
