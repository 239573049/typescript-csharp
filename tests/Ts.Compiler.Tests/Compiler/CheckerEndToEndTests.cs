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

    // Pipeline probe — huge advancement: the checker now runs the FULL check on `const x: number =
    // "hello"` — grammar → isCanceled → variable-statement check → checkDecorators (early-return) →
    // type-annotation + initializer assignability → the relation FAILS (string ⊄ number) and enters
    // error REPORTING (checkTypeRelatedTo → reportRelationError). It blocks only on typeToStringEx
    // (the type-display serializer that renders the type names in "Type 'string' is not assignable to
    // type 'number'") — the deferred TypeToString/nodebuilder wave. Un-skip once typeToStringEx renders
    // intrinsic type names; the probe then produces the diagnostic and Assert.NotEmpty passes.
    [Fact(Skip = "e2e check reaches assignability error reporting; blocked on typeToStringEx (type-display / TypeToString wave)")]
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
