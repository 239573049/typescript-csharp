// Ported from tsgo internal/compiler/program_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTEs:
//  * Only the program_test.go function that is not already covered by the self-authored
//    ProgramPipelineTests/CheckerEndToEndTests suite is ported here (Go file:267-310).
//  * Go's panic-recovery wrapper (assert.NilError over a deferred recover, program_test.go:301-309)
//    has no C# equivalent: a null dereference would surface as a NullReferenceException and fail
//    the test directly, which is exactly what the Go wrapper turns the panic into.
//  * vfstest.FromMap[any](nil, true) → VfsTestUtil.FromMap<string>(null, true); the map then
//    receives its single lowercase file via WriteFile, as Go does.
//  * `if !bundled.Embedded { t.Skip(...) }` is omitted: Bundled.Embedded is `const true` in the
//    C# build (Embed.cs:24).
//  * Scheduling detail: the Go test can rely on Go maps sleeping in a fixed iteration order; the
//    C# Program iterates a parsed JSON object, so the order in which the two root files are
//    case-resolved must not be assumed. The casing diagnostic is produced for the *missing*
//    spelling regardless of order, so the assertion below is order-independent (it is derived
//    from the Go expectation that GetProgramDiagnostics must not throw).

using System;
using System.Collections.Generic;
using Ts.Core;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

using TsOptionsApi = global::Ts.TsOptions;

namespace Ts.Compiler.Tests;

public sealed class ProgramCasingTests
{
    // program_test.go:267-310 — TestIncludeProcessorDiagnosticsWithMissingFileCasing.
    [Fact]
    public void IncludeProcessorDiagnosticsWithMissingFileCasing()
    {
        // Use case-sensitive file names so that /src/MyFile.ts and /src/myFile.ts
        // have different canonical paths but the same lower-case path, triggering
        // file casing diagnostics in the include processor.
        IFileSystem fs = VfsTestUtil.FromMap<string>(null, useCaseSensitiveFileNames: true);

        // Only create the lowercase version; /src/MyFile.ts does not exist.
        fs.WriteFile("/src/myFile.ts", "export const y = 2;");

        var opts = new CompilerOptions { SkipDefaultLibCheck = Tristate.True };

        // List both casings as root files. The first one (/src/MyFile.ts) will fail
        // to load because it does not exist on the case-sensitive filesystem.
        var program = new Program(new ProgramOptions
        {
            Config = TsOptionsApi.ParsedCommandLine.NewParsedCommandLine(
                opts,
                new[] { "/src/MyFile.ts", "/src/myFile.ts" },
                new global::Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = "/" }),
            Host = global::Ts.Compiler.CompilerHosts.NewCompilerHost("/", fs, global::Ts.Bundled.Bundled.LibPath(), null, null),
        });

        // GetProgramDiagnostics triggers getDiagnostics which processes all
        // include processor diagnostics including the casing diagnostic whose
        // file path points to the missing /src/MyFile.ts. Before the fix this
        // panicked with a nil pointer dereference.
        global::Ts.Ast.Diagnostic[] diagnostics = program.GetProgramDiagnostics();

        // The Go test only asserts that the call does not panic; assert the state it
        // depends on so a regression cannot pass vacuously: the casing diagnostic
        // (includeprocessor.go:72/82 — Already_included/File_name_0_differs) is reported
        // for the missing spelling, and the file that does exist is loaded.
        Assert.NotNull(diagnostics);
        Assert.Contains(diagnostics, d => d.Code() == global::Ts.Diagnostics.Diagnostics.Already_included_file_name_0_differs_from_file_name_1_only_in_casing.Code
            || d.Code() == global::Ts.Diagnostics.Diagnostics.File_name_0_differs_from_already_included_file_name_1_only_in_casing.Code);
        Assert.NotNull(program.GetSourceFile("/src/myFile.ts"));
    }
}
