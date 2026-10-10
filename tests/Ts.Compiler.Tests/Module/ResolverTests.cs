// Ported from tsgo internal/module/resolver_test.go — TestResolveModuleNameTrailingSlash, the three
// concurrency regressions (TestResolveModuleNameTrailingSlashRace, TestResolveSubpathNilContentsRace,
// TestResolvePeerDependencyNilContentsRace — resolver_test.go:81-416), plus self-authored
// node16/nodenext/bundler + package.json exports coverage
// (commit 168e7015edf98244febc8f4ae450b673b5d195d7). The concurrency fix they guard
// (WithPackageDirectory / Exists() guards) is ported verbatim.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

// resolver_test.go:49-66 — blockingFS: forces FileExists for targetPath to block on `gate` until
// released; each caller signals `arrived` when it reaches the gate.
internal sealed class BlockingFs : IFileSystem
{
    private readonly IFileSystem inner;
    private readonly string targetPath;
    private readonly SemaphoreSlim gate = new(0, int.MaxValue);
    private readonly SemaphoreSlim arrived = new(0, int.MaxValue);

    public BlockingFs(IFileSystem inner, string targetPath)
    {
        this.inner = inner;
        this.targetPath = targetPath;
    }

    // Go: `f.arrived <- struct{}{}` with arrived cap 2 — the test only drains two arrivals.
    public Task WaitArrivalAsync() => arrived.WaitAsync(TimeSpan.FromSeconds(30));

    public void Release() => gate.Release(int.MaxValue);

    public bool FileExists(string path)
    {
        if (path == targetPath)
        {
            arrived.Release();
            gate.Wait();
        }
        return inner.FileExists(path);
    }

    public bool UseCaseSensitiveFileNames() => inner.UseCaseSensitiveFileNames();
    public bool TryReadFile(string path, out string contents) => inner.TryReadFile(path, out contents);
    public void WriteFile(string path, string data) => inner.WriteFile(path, data);
    public void AppendFile(string path, string data) => inner.AppendFile(path, data);
    public void Remove(string path) => inner.Remove(path);
    public void Chtimes(string path, DateTime aTime, DateTime mTime) => inner.Chtimes(path, aTime, mTime);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public Entries GetAccessibleEntries(string path) => inner.GetAccessibleEntries(path);
    public IFileInfo? Stat(string path) => inner.Stat(path);
    public Exception? WalkDir(string root, WalkDirFunc walkFn) => inner.WalkDir(root, walkFn);
    public string Realpath(string path) => inner.Realpath(path);
}

// resolver_test.go:68-104 — flipFileExistsFS: returns false for the first FileExists call to
// targetPath, true for the second; both calls signal arrival and block until released. ReadFile
// for the target path also signals arrival then blocks, so the "file doesn't exist" Set completes
// before the "file exists" Set (reproducing the LoadOrStore race).
internal sealed class FlipFileExistsFs : IFileSystem
{
    private readonly IFileSystem inner;
    private readonly string targetPath;
    private int callCount;
    private readonly SemaphoreSlim firstGate = new(0, int.MaxValue);
    private readonly SemaphoreSlim secondGate = new(0, int.MaxValue);
    private readonly SemaphoreSlim readGate = new(0, int.MaxValue);

    public FlipFileExistsFs(IFileSystem inner, string targetPath)
    {
        this.inner = inner;
        this.targetPath = targetPath;
    }

    public SemaphoreSlim FirstArrived { get; } = new(0, 2);
    public SemaphoreSlim SecondArrived { get; } = new(0, 2);
    public SemaphoreSlim ReadArrived { get; } = new(0, 2);

    public void ReleaseFirst() => firstGate.Release(int.MaxValue);
    public void ReleaseSecond() => secondGate.Release(int.MaxValue);
    public void ReleaseRead() => readGate.Release(int.MaxValue);

    public bool FileExists(string path)
    {
        if (path == targetPath)
        {
            int n = Interlocked.Increment(ref callCount);
            if (n == 1)
            {
                FirstArrived.Release();
                firstGate.Wait();
                return false; // first caller: simulate "file not yet visible"
            }
            if (n == 2)
            {
                SecondArrived.Release();
                secondGate.Wait();
                return inner.FileExists(path); // second caller: file is visible
            }
        }
        return inner.FileExists(path);
    }

    public bool TryReadFile(string path, out string contents)
    {
        if (path == targetPath)
        {
            ReadArrived.Release();
            readGate.Wait();
        }
        return inner.TryReadFile(path, out contents);
    }

    public bool UseCaseSensitiveFileNames() => inner.UseCaseSensitiveFileNames();
    public void WriteFile(string path, string data) => inner.WriteFile(path, data);
    public void AppendFile(string path, string data) => inner.AppendFile(path, data);
    public void Remove(string path) => inner.Remove(path);
    public void Chtimes(string path, DateTime aTime, DateTime mTime) => inner.Chtimes(path, aTime, mTime);
    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public Entries GetAccessibleEntries(string path) => inner.GetAccessibleEntries(path);
    public IFileInfo? Stat(string path) => inner.Stat(path);
    public Exception? WalkDir(string root, WalkDirFunc walkFn) => inner.WalkDir(root, walkFn);
    public string Realpath(string path) => inner.Realpath(path);
}

public class ResolverTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    // resolver_test.go:53-57 — waitForSignal. A missing signal (deadlock) becomes a deterministic
    // failure instead of a hung test.
    private static async Task WaitForSignalAsync(Task arrival, string description)
    {
        await Task.WhenAny(arrival, Task.Delay(SignalTimeout));
        Assert.True(arrival.IsCompleted, $"timed out waiting for {description}");
    }

    // Go's `go func() { … resolve … }()` bodies perform deep module-resolution recursion, so each
    // runs inside LargeStackExecutor.Run on its own long-running task (05-conventions.md §16.4,
    // mirroring tests/Ts.Cli.Tests/Execute/WatcherRaceTests.cs).
    private static Task<T> RunResolutionAsync<T>(Func<T> resolve) =>
        LargeStackExecutor.Run(() => Task.Factory.StartNew(
            () => LargeStackExecutor.Run(resolve),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default));

    // Regression test for https://github.com/microsoft/typescript-go/issues/3526.
    // Two threads resolve the same package via specifiers that differ only by a trailing slash
    // (`pkg` and `pkg/`). A blocking FS holds both at the FileExists check for `package.json` —
    // after each has confirmed a package.json info-cache miss but before either has called Set.
    [Fact]
    public async Task TestResolveModuleNameTrailingSlashRace()
    {
        const string pkgJsonPath = "/repo/node_modules/pkg/package.json";
        var files = new Dictionary<string, string>
        {
            // `types` points at a file that is not discoverable through any fallback path: there is
            // no `index.*` and no `main`. The only way to resolve `pkg` (or `pkg/`) is via the
            // package.json `types` field inside loadNodeModuleFromDirectoryWorker, which is exactly
            // the step that the bug skips when `candidate` and `packageInfo.PackageDirectory`
            // mismatch.
            [pkgJsonPath] = """{"name":"pkg","types":"./typings/index.d.ts"}""",
            ["/repo/node_modules/pkg/typings/index.d.ts"] = "export const x: number;",
            // Distinct containing files so each ResolveModuleName call has a unique
            // module-resolution-cache key.
            ["/repo/src/a/file.ts"] = "",
            ["/repo/src/b/file.ts"] = "",
        };
        var fs = new BlockingFs(VfsTestUtil.FromMap(files, true), pkgJsonPath);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        var a = RunResolutionAsync(() =>
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName("pkg", "/repo/src/a/file.ts", ModuleKind.ESNext, null);
            return r.IsResolved();
        });
        var b = RunResolutionAsync(() =>
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName("pkg/", "/repo/src/b/file.ts", ModuleKind.ESNext, null);
            return r.IsResolved();
        });

        // Wait for both threads to reach the FileExists gate, guaranteeing both have observed a
        // package.json info-cache miss.
        await WaitForSignalAsync(fs.WaitArrivalAsync(), "first FileExists gate arrival");
        await WaitForSignalAsync(fs.WaitArrivalAsync(), "second FileExists gate arrival");
        fs.Release();

        Assert.True(await a, "\"pkg\" failed to resolve");
        Assert.True(await b, "\"pkg/\" failed to resolve");
    }

    // resolver_test.go:216-299 — TestResolveSubpathNilContentsRace.
    [Fact]
    public async Task TestResolveSubpathNilContentsRace()
    {
        const string rootPkgJson = "/repo/node_modules/pkg/package.json";
        var files = new Dictionary<string, string>
        {
            [rootPkgJson] = """{"name":"pkg","version":"1.0.0"}""",
            ["/repo/node_modules/pkg/sub/index.d.ts"] = "export declare const sub: number;",
            ["/repo/node_modules/pkg/sub/index.js"] = "exports.sub = 1;",
            ["/repo/src/a/file.ts"] = "",
            ["/repo/src/b/file.ts"] = "",
        };
        var fs = new FlipFileExistsFs(VfsTestUtil.FromMap(files, true), rootPkgJson);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        var a = RunResolutionAsync(() =>
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName("pkg/sub", "/repo/src/a/file.ts", ModuleKind.ESNext, null);
            return r.IsResolved();
        });
        var b = RunResolutionAsync(() =>
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName("pkg/sub", "/repo/src/b/file.ts", ModuleKind.ESNext, null);
            return r.IsResolved();
        });

        // Phase 1: both threads have observed a cache miss.
        await WaitForSignalAsync(fs.FirstArrived.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken), "first root package.json FileExists arrival");
        await WaitForSignalAsync(fs.SecondArrived.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken), "second root package.json FileExists arrival");

        // Phase 2: release the first FileExists caller (returns false); it stores a nil-Contents entry.
        fs.ReleaseFirst();

        // Phase 3: release the second FileExists caller (returns true).
        fs.ReleaseSecond();

        // Phase 4: wait for the second thread to reach ReadFile, then release. By now the first
        // thread has stored its nil-Contents entry and the second thread's Set returns that entry.
        await WaitForSignalAsync(fs.ReadArrived.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken), "root package.json ReadFile arrival");
        fs.ReleaseRead();

        // A nil-Contents dereference in loadModuleFromSpecificNodeModulesDirectory would surface as
        // an exception thrown out of ResolveModuleName; a resolved result proves the Exists() guard.
        Assert.True(await a, "\"/repo/src/a/file.ts\" failed to resolve pkg/sub");
        Assert.True(await b, "\"/repo/src/b/file.ts\" failed to resolve pkg/sub");
    }

    // resolver_test.go:339-416 — TestResolvePeerDependencyNilContentsRace.
    [Fact]
    public async Task TestResolvePeerDependencyNilContentsRace()
    {
        const string peerPkgJson = "/repo/node_modules/peer/package.json";
        var files = new Dictionary<string, string>
        {
            ["/repo/node_modules/pkg/package.json"] = """{"name":"pkg","version":"1.0.0","types":"index.d.ts","peerDependencies":{"peer":"*"}}""",
            ["/repo/node_modules/pkg/index.d.ts"] = "export declare const x: number;",
            [peerPkgJson] = """{"name":"peer","version":"2.0.0"}""",
            ["/repo/src/a/file.ts"] = "",
            ["/repo/src/b/file.ts"] = "",
        };
        var fs = new FlipFileExistsFs(VfsTestUtil.FromMap(files, true), peerPkgJson);
        var host = new ResolutionHostStub(fs, "/repo");
        var opts = new CompilerOptions
        {
            ModuleResolution = ModuleResolutionKind.Bundler,
            Module = ModuleKind.ESNext,
            Target = ScriptTarget.ESNext,
        };
        var resolver = new Resolver(host, opts, "", "");

        var a = RunResolutionAsync(() =>
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName("pkg", "/repo/src/a/file.ts", ModuleKind.ESNext, null);
            return r.IsResolved();
        });
        var b = RunResolutionAsync(() =>
        {
            (ResolvedModule r, _) = resolver.ResolveModuleName("pkg", "/repo/src/b/file.ts", ModuleKind.ESNext, null);
            return r.IsResolved();
        });

        await WaitForSignalAsync(fs.FirstArrived.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken), "first peer package.json FileExists arrival");
        await WaitForSignalAsync(fs.SecondArrived.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken), "second peer package.json FileExists arrival");
        fs.ReleaseFirst();
        bool firstResolved = await a;
        fs.ReleaseSecond();
        await WaitForSignalAsync(fs.ReadArrived.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken), "peer package.json ReadFile arrival");
        fs.ReleaseRead();

        Assert.True(firstResolved, "\"/repo/src/a/file.ts\" failed to resolve pkg");
        Assert.True(await b, "\"/repo/src/b/file.ts\" failed to resolve pkg");
    }
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
