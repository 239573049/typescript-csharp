// CLI wave (tsc type-check-and-report): drives the Ts.Execute.Tsc.CommandLine flow through a fake
// ISystem so the parse → dispatch → report → exit-code path is exercised without touching the real OS.
//
// The end-to-end "type-check a legal file and exit 0" test is skipped: the checker's CheckSourceFile
// path is being filled in parallel and currently throws NotImplementedException (checkDecorators) for
// even a trivial legal program. Un-skip once the checker semantic path is real.

using System;
using System.Collections.Generic;
using System.IO;
using Ts.Execute;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests;

public sealed class ExecuteTests
{
    // A test double for ISystem: captures writer output, serves an in-memory FS, and reports a
    // non-TTY, color-free environment so the plain reporter path is used deterministically.
    private sealed class FakeSystem : ISystem
    {
        private readonly StringWriter writer = new();
        private readonly IFileSystem fs;
        private readonly string cwd;
        private readonly DateTime start = DateTime.UtcNow;

        public FakeSystem(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public string Output => writer.ToString();

        public TextWriter Writer() => writer;
        public IFileSystem FS() => fs;
        public string DefaultLibraryPath() => "/lib";
        public string GetCurrentDirectory() => cwd;
        public bool WriteOutputIsTTY() => false;
        public int GetWidthOfTerminal() => 0;
        public string GetEnvironmentVariable(string name) => name == "NO_COLOR" ? "1" : "";
        public DateTime Now() => DateTime.UtcNow;
        public TimeSpan SinceStart() => DateTime.UtcNow - start;
    }

    private static FakeSystem NewSystem(IReadOnlyDictionary<string, string>? files = null)
    {
        var fs = VfsTestUtil.FromMap(files ?? new Dictionary<string, string>(), useCaseSensitiveFileNames: true);
        return new FakeSystem(fs, "/src");
    }

    [Fact]
    public void Version_PrintsVersionAndExitsZero()
    {
        var sys = NewSystem();
        var result = Tsc.CommandLine(sys, ["--version"]);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Contains("Version", sys.Output);
    }

    [Fact]
    public void Help_PrintsUsageAndExitsZero()
    {
        var sys = NewSystem();
        var result = Tsc.CommandLine(sys, ["--help"]);

        Assert.Equal(ExitStatus.Success, result.Status);
        // The 1:1 help tables (help.go) — easy help leads with COMMON COMMANDS.
        Assert.Contains("tsc: The TypeScript Compiler", sys.Output);
        Assert.Contains("COMMON COMMANDS", sys.Output);
    }

    [Fact]
    public void NoArgs_PrintsVersionAndHelp_ReportsNoInputs()
    {
        var sys = NewSystem();
        var result = Tsc.CommandLine(sys, []);

        // tsc's no-inputs behaviour: version + help, non-success status.
        Assert.Equal(ExitStatus.DiagnosticsPresent_OutputsSkipped, result.Status);
        Assert.Contains("Version", sys.Output);
        Assert.Contains("COMMON COMMANDS", sys.Output);
    }

    [Fact(Skip = "checker path WIP: CheckSourceFile throws NotImplementedException (checkDecorators) for legal programs; un-skip when the semantic report path is real")]
    public void LegalFile_TypeChecks_ExitsZero()
    {
        var sys = NewSystem(new Dictionary<string, string> { ["/src/only.ts"] = "export const x = 1;\n" });
        var result = Tsc.CommandLine(sys, ["--noLib", "/src/only.ts"]);

        Assert.Equal(ExitStatus.Success, result.Status);
        Assert.Empty(sys.Output);
    }
}
