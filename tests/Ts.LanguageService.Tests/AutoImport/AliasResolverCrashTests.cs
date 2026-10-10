// Ported from tsgo internal/ls/autoimport/aliasresolver_crash_test.go (whole file).
//
// PORT-NOTE: Go's `module.NewResolver(host, core.EmptyCompilerOptions, "", "")` maps to
// Ts.Module.Resolver's (ResolutionHost, CompilerOptions, string typingsLocation,
// string projectName) constructor (Resolver.Api.cs:38); the 4-arg shape is verbatim.
//
// PORT-NOTE: Go's `func(f string) tspath.Path { return tspath.Path(f) }` identity closure
// is `static fileName => new Path(fileName)`; AliasResolver's unexported members
// (NewAliasResolver, AliasResolver) are internal and visible via InternalsVisibleTo
// (plan/05-conventions.md §16).
//
// PORT-NOTE: gotest.tools assert.NilError on the NewChecker error slot → Assert.Null;
// the test's real assertion is "GetDiagnostics completes without throwing", which the
// call itself expresses.
//
// PORT-NOTE: Go's `for name := range ...` over a set is a map iteration; the C# port
// iterates the set's key collection (no ordering depended on).

using System.Collections.Generic;
using System.Threading;
using Ts.Ast;
using Ts.Core;
using Ts.Ls.AutoImport;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

using Path = global::Ts.TsPath.Path;
using InfoCacheEntry = global::Ts.PackageJson.InfoCacheEntry;

namespace Ts.Ls.AutoImport.Tests;

// aliasresolver_crash_test.go:20-35 — fakeCloneHost.
internal sealed class FakeCloneHost : RegistryCloneHost
{
    private readonly IFileSystem fs;

    public FakeCloneHost(IFileSystem fs)
    {
        this.fs = fs;
    }

    // aliasresolver_crash_test.go:24 — FS.
    public IFileSystem FS() => fs;

    // aliasresolver_crash_test.go:25 — GetCurrentDirectory.
    public string GetCurrentDirectory() => "/";

    // aliasresolver_crash_test.go:26-28 — GetDefaultProject.
    public (Path Path, global::Ts.Compiler.Program? Program) GetDefaultProject(Path path) => (default, null);

    // aliasresolver_crash_test.go:30 — GetProgramForProject.
    public global::Ts.Compiler.Program? GetProgramForProject(Path projectPath) => null;

    // aliasresolver_crash_test.go:32 — GetPackageJson.
    public InfoCacheEntry? GetPackageJson(string fileName) => null;

    // aliasresolver_crash_test.go:34 — GetSourceFile.
    public SourceFile? GetSourceFile(string fileName, Path path) => null;

    // aliasresolver_crash_test.go:35 — Dispose.
    public void Dispose()
    {
    }
}

public sealed class AliasResolverCrashTests
{
    // aliasresolver_crash_test.go:39-73 — TestAliasResolverGetDiagnosticsDoesNotPanic.
    //
    // Regression test for microsoft/typescript-go#4322.
    //
    // During auto-import export extraction, the checker is built on top of an
    // aliasResolver standing in for a real program. This file has a type error, and
    // extracting exports should still complete without crashing.
    [Fact]
    public void AliasResolverGetDiagnosticsDoesNotPanic()
    {
        const string fileName = "/pkg/index.ts";
        const string text = "declare function f(arg: { a: string }): () => void;\nexport const x = f({ a: 1 });\n";

        IFileSystem fs = VfsTestUtil.FromMap(
            new Dictionary<string, string> { [fileName] = text },
            useCaseSensitiveFileNames: true);
        var host = new FakeCloneHost(fs);

        SourceFile sourceFile = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions { FileName = fileName, Path = new Path(fileName) },
            text,
            ScriptKind.TS);
        global::Ts.Binder.Binder.BindSourceFile(sourceFile);

        var resolver = new global::Ts.Module.Resolver(host, global::Ts.Core.Core.EmptyCompilerOptions, "", "");
        AliasResolver r = AliasResolver.NewAliasResolver(
            [sourceFile],
            symlinks: null,
            host,
            resolver,
            static f => new Path(f),
            onFailedAmbientModuleLookup: static (_, _) => { });

        (global::Ts.Checker.Checker ch, System.Threading.Lock _) = global::Ts.Checker.Checker.NewChecker(r, tracer: null);

        // Type-checking this file's diagnostics must not panic.
        ch.GetDiagnostics(sourceFile, CancellationToken.None);
    }
}
