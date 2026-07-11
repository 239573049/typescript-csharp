// End-to-end probe: build a bound Program from in-memory source, run the checker over it, and
// collect diagnostics. Proves the parse→bind→check→diagnostics pipeline is wired, and surfaces
// which check* stubs a real program actually hits (drives remaining-checker prioritization).

using System.Collections.Generic;
using System.Threading;
using Ts.Core;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Compiler.Tests;

public sealed class CheckerEndToEndTests
{
    private const string CurrentDirectory = "/src";

    private static Program BuildProgram(IReadOnlyDictionary<string, string> files, string[] roots, CompilerOptions options)
    {
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var host = CompilerHostImpl.NewCompilerHost(CurrentDirectory, fs, "/lib", extendedConfigCache: null, trace: null);
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            roots,
            new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = CurrentDirectory });
        return new Program(new ProgramOptions { Host = host, Config = config, SingleThreaded = Tristate.True });
    }

    // Pipeline probe: CheckGrammarSourceFile (the first thing CheckSourceFile calls) is now REAL
    // (grammar-checks wave landed), so the probe advances past grammar and blocks on the next
    // downstream stub: IsCanceled() (checker.go isCanceled), thrown from CheckSourceFile before the
    // const-assignment not-assignable diagnostic can be produced. Un-skip once IsCanceled and the
    // remaining check* path (checkVariableDeclaration → resolveName → assignability) are real; if a
    // further stub is then hit, re-note the next blocker.
    // Pipeline probe: advances through grammar + isCanceled + the variable-statement check path
    // (CheckVariableStatement → …Declaration → …LikeDeclaration) and now blocks on CheckDecorators
    // (runs even with no decorators; its guard needs the still-stubbed ast decorator predicates
    // HasDecorators/NodeCanBeDecorated). Un-skip once the const-assignment check path (checkDecorators
    // early-return + type-annotation + initializer assignability elaboration) is real.
    [Fact(Skip = "e2e check blocked on checkDecorators stub (variable-decl check path; needs ast decorator predicates)")]
    public void ChecksSimpleAssignmentError_ProducesTypeDiagnostic()
    {
        var files = new Dictionary<string, string>
        {
            // Intrinsic-types-only error: string literal not assignable to number annotation.
            ["/src/only.ts"] = "const x: number = \"hello\";\n",
        };
        var program = BuildProgram(files, ["/src/only.ts"], new CompilerOptions { NoLib = Tristate.True, Types = [] });
        program.BindSourceFiles();
        var sf = program.GetSourceFile("/src/only.ts")!;

        var (checker, release) = program.GetTypeChecker(CancellationToken.None);
        try
        {
            var diags = checker.GetDiagnostics(sf);
            Assert.NotEmpty(diags); // expect the not-assignable error
        }
        finally
        {
            release();
        }
    }
}
