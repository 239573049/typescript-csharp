// Ported from tsgo internal/module/resolver_test.go TestResolveModuleNameTrailingSlash, plus
// self-authored node16/nodenext/bundler + package.json exports coverage
// (commit 168e7015edf98244febc8f4ae450b673b5d195d7). The goroutine race regression tests
// (TestResolveModuleNameTrailingSlashRace etc.) are deferred — they exercise Go channel
// scheduling that has no 1:1 xunit shape; the concurrency fix they guard (WithPackageDirectory
// / Exists() guards) is ported verbatim.

using System.Collections.Generic;
using Ts.Core;
using Ts.Module;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.ModuleTests;

internal sealed class ResolutionHostStub : ResolutionHost
{
    private readonly IFileSystem fs;
    private readonly string cwd;

    public ResolutionHostStub(IFileSystem fs, string cwd)
    {
        this.fs = fs;
        this.cwd = cwd;
    }

    public IFileSystem FS() => fs;

    public string GetCurrentDirectory() => cwd;
}

public class ResolverTests
{
    // Regression test for https://github.com/microsoft/typescript-go/issues/3526.
    // Resolving a node_modules import with a trailing slash (e.g. `pkg/`) must produce the
    // same result as without one.
    [Fact]
    public void TestResolveModuleNameTrailingSlash()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/node_modules/pkg/package.json"] = """{"name":"pkg","main":"main.js","types":"main.d.ts"}""",
            ["/repo/node_modules/pkg/main.d.ts"] = "export const x: number;",
            ["/repo/node_modules/pkg/main.js"] = "exports.x = 1;",
            ["/repo/src/file.ts"] = "",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        foreach (string name in new[] { "pkg", "pkg/" })
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName(name, "/repo/src/file.ts", ModuleKind.ESNext, null);
            Assert.True(r.IsResolved(), $"{name} failed to resolve");
        }
    }

    [Fact]
    public void TestNode16RelativeImport()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/src/a.ts"] = "export const a = 1;",
            ["/repo/src/b.ts"] = "import { a } from './a';",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Node16,
            Module = ModuleKind.Node16,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        (ResolvedModule r, _) = resolver.ResolveModuleName("./a", "/repo/src/b.ts", ModuleKind.CommonJS, null);
        Assert.True(r.IsResolved());
        Assert.Equal("/repo/src/a.ts", r.ResolvedFileName);
        Assert.Equal(".ts", r.Extension);
    }

    [Fact]
    public void TestNode16PackageExportsTypesCondition()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/node_modules/pkg/package.json"] = """{"name":"pkg","version":"1.0.0","exports":{".":{"types":"./index.d.ts","default":"./index.js"}}}""",
            ["/repo/node_modules/pkg/index.d.ts"] = "export declare const x: number;",
            ["/repo/node_modules/pkg/index.js"] = "exports.x = 1;",
            ["/repo/src/file.ts"] = "",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Node16,
            Module = ModuleKind.Node16,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        (ResolvedModule r, _) = resolver.ResolveModuleName("pkg", "/repo/src/file.ts", ModuleKind.ESNext, null);
        Assert.True(r.IsResolved());
        Assert.Equal("/repo/node_modules/pkg/index.d.ts", r.ResolvedFileName);
        Assert.Equal(".d.ts", r.Extension);
        Assert.True(r.IsExternalLibraryImport);
        Assert.Equal("pkg", r.PackageId.Name);
        Assert.Equal("1.0.0", r.PackageId.Version);
    }

    [Fact]
    public void TestNode16PackageSubpathExports()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/node_modules/pkg/package.json"] = """{"name":"pkg","version":"2.0.0","exports":{"./sub":{"types":"./sub/index.d.ts"}}}""",
            ["/repo/node_modules/pkg/sub/index.d.ts"] = "export declare const s: number;",
            ["/repo/src/file.ts"] = "",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.NodeNext,
            Module = ModuleKind.NodeNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        (ResolvedModule r, _) = resolver.ResolveModuleName("pkg/sub", "/repo/src/file.ts", ModuleKind.ESNext, null);
        Assert.True(r.IsResolved());
        Assert.Equal("/repo/node_modules/pkg/sub/index.d.ts", r.ResolvedFileName);
        Assert.Equal("sub/index.d.ts", r.PackageId.SubModuleName);
    }

    [Fact]
    public void TestBundlerResolvesMainAndTypes()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/node_modules/pkg/package.json"] = """{"name":"pkg","version":"1.2.3","main":"main.js","types":"types/main.d.ts"}""",
            ["/repo/node_modules/pkg/types/main.d.ts"] = "export const x: number;",
            ["/repo/node_modules/pkg/main.js"] = "exports.x = 1;",
            ["/repo/src/file.ts"] = "",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        (ResolvedModule r, _) = resolver.ResolveModuleName("pkg", "/repo/src/file.ts", ModuleKind.ESNext, null);
        Assert.True(r.IsResolved());
        Assert.Equal("/repo/node_modules/pkg/types/main.d.ts", r.ResolvedFileName);
        Assert.Equal(".d.ts", r.Extension);
    }

    [Fact]
    public void TestUnresolvedModuleReturnsNotResolved()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/src/file.ts"] = "",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        (ResolvedModule r, _) = resolver.ResolveModuleName("does-not-exist", "/repo/src/file.ts", ModuleKind.ESNext, null);
        Assert.False(r.IsResolved());
    }

    [Fact]
    public void TestTraceResolutionEmitsLog()
    {
        IFileSystem fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/repo/src/a.ts"] = "export const a = 1;",
            ["/repo/src/b.ts"] = "",
        }, true);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
            TraceResolution = Tristate.True,
        };
        var resolver = new Resolver(host, opts, "", "");

        (ResolvedModule r, System.Collections.Generic.List<DiagAndArgs>? traces) = resolver.ResolveModuleName("./a", "/repo/src/b.ts", ModuleKind.ESNext, null);
        Assert.True(r.IsResolved());
        Assert.NotNull(traces);
        Assert.NotEmpty(traces!);
    }
}
