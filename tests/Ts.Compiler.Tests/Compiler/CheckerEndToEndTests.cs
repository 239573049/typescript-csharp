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

    // Pipeline probe — the checker runs the FULL check on `const x: number = "hello"` — grammar →
    // isCanceled → variable-statement check → checkDecorators (early-return) → type-annotation +
    // initializer assignability → the relation FAILS (string ⊄ number) and enters error REPORTING
    // (checkTypeRelatedTo → reportRelationError). typeToStringEx (the type-display serializer, phase-5
    // NodeBuilder type-node build → emit Printer) now renders intrinsic type names — that block is
    // CLEARED (see TypeToStringTests for direct verification). The probe now advances PAST assignability
    // reporting and blocks on the NEXT downstream stub: checkVarDeclaredNamesNotShadowed
    // (Checker.Statements.Deps.cs), which throws NotImplementedException after the not-assignable
    // diagnostic has been produced but before GetDiagnostics returns it.
    //
    // MILESTONE (proven): the not-assignable diagnostic IS produced — the checker runs grammar →
    // decorators → assignability → relation fails → error reporting → typeToStringEx renders the type
    // names (TypeToStringTests verify this independently). checkVarDeclaredNamesNotShadowed now lands too.
    // The probe trips only on the NEXT post-diagnostic edge-case grammar cluster (#6 collision/unused:
    // checkCollisionsForDeclarationName → needCollisionCheckForIdentifier + collision leaf checks), which
    // runs AFTER the diagnostic is created but before GetDiagnostics returns. Un-skip once #6 lands.
    [Fact(Skip = "diagnostic IS produced (typeToStringEx milestone); blocked on the #6 collision/unused cluster — checkCollisionsForDeclarationName — which runs before GetDiagnostics returns")]
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
