// Smoke tests for the tsgo internal/ls diagnostics port (compiler Diagnostic → LSP Diagnostic).

using System.Collections.Generic;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class DiagnosticsTests
{
    private const string CurrentDirectory = "/src";
    private const string FileName = "/src/main.ts";

    [Fact]
    public void ProvideDiagnosticsMapsTs2322ToLspDiagnostic()
    {
        //                    0         1
        //                    0123456789012345678901
        const string source = "const x: string = 1;\n";
        var program = BuildProgram(new Dictionary<string, string>
        {
            [FileName] = source,
        });
        var service = global::Ts.Ls.LanguageService.NewLanguageService(program);

        var diagnostics = service.ProvideDiagnostics(
            FileName,
            reportStyleChecksAsWarnings: false,
            TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics);

        // Range: the error is reported on the declaration name `x` (offset 6..7 on line 0).
        Assert.Equal(0u, diagnostic.Range.Start.Line);
        Assert.Equal(6u, diagnostic.Range.Start.Character);
        Assert.Equal(0u, diagnostic.Range.End.Line);
        Assert.Equal(7u, diagnostic.Range.End.Character);

        // Severity: error category → LSP Error.
        Assert.Equal(global::Ts.LsProto.DiagnosticSeverity.Error, diagnostic.Severity);

        // Code: non-VS clients get the numeric code plus the "ts" source.
        Assert.NotNull(diagnostic.Code);
        Assert.Equal(2322, diagnostic.Code!.Integer);
        Assert.Null(diagnostic.Code.String);
        Assert.Equal("ts", diagnostic.Source);

        Assert.Equal("Type 'number' is not assignable to type 'string'.", diagnostic.Message.String);

        // TS2322 is neither unnecessary nor deprecated and has no related information.
        Assert.Null(diagnostic.Tags);
        Assert.Null(diagnostic.RelatedInformation);
    }

    private static global::Ts.Compiler.Program BuildProgram(IReadOnlyDictionary<string, string> files)
    {
        var fileSystem = global::Ts.Vfs.VfsTest.VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);
        var compilerHost = global::Ts.Compiler.CompilerHosts.NewCompilerHost(
            CurrentDirectory,
            fileSystem,
            "/lib",
            extendedConfigCache: null,
            trace: null);
        var options = new global::Ts.Core.CompilerOptions
        {
            NoLib = global::Ts.Core.Tristate.True,
            Module = global::Ts.Core.ModuleKind.CommonJS,
            Types = [],
        };
        var config = global::Ts.TsOptions.ParsedCommandLine.NewParsedCommandLine(
            options,
            [FileName],
            new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = true,
                CurrentDirectory = CurrentDirectory,
            });

        return new global::Ts.Compiler.Program(new global::Ts.Compiler.ProgramOptions
        {
            Host = compilerHost,
            Config = config,
            SingleThreaded = global::Ts.Core.Tristate.True,
        });
    }
}
