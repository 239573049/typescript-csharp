// Ported from tsgo internal/execute/tsc/extendedconfigcache_test.go — the tsc ExtendedConfigCache
// regression tests: an `extends` cycle must produce a circularity diagnostic (code 18000) rather
// than deadlock, and a null `extends` must produce diagnostics instead of panicking.

using System.Collections.Generic;
using Ts.Execute;
using Ts.TsOptions;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Cli.Tests.Execute;

public sealed class TscExtendedConfigCacheTests
{
    // extendedconfigcache_test.go:12-18 — testParseConfigHost.
    private sealed class TestParseConfigHost : ParseConfigHost
    {
        private readonly IFileSystem fs;
        private readonly string cwd;

        internal TestParseConfigHost(IFileSystem fs, string cwd)
        {
            this.fs = fs;
            this.cwd = cwd;
        }

        public IFileSystem FS() => fs;

        public string GetCurrentDirectory() => cwd;
    }

    // extendedconfigcache_test.go:20-87 — TestExtendedConfigCacheExtendsCircularity; the three
    // t.Run subtests below are its bodies.
    // extendedconfigcache_test.go:23-43 — "self-referencing extends".
    [Fact]
    public void ExtendedConfigCacheExtendsCircularity_SelfReferencing()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = "{\"extends\": \"./base.json\"}",
            ["/project/base.json"] = "{\"extends\": \"./base.json\"}",
            ["/project/main.ts"] = "// Hello World!",
        };
        AssertHasCircularityDiagnostic(files);
    }

    // extendedconfigcache_test.go:45-64 — "mutual extends cycle".
    [Fact]
    public void ExtendedConfigCacheExtendsCircularity_MutualCycle()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = "{\"extends\": \"./other.json\"}",
            ["/project/other.json"] = "{\"extends\": \"./tsconfig.json\"}",
            ["/project/main.ts"] = "// Hello World!",
        };
        AssertHasCircularityDiagnostic(files);
    }

    // extendedconfigcache_test.go:66-86 — "case-insensitive self-referencing extends". On a
    // case-insensitive FS, ./Base.json and ./base.json resolve to the same cache entry; the cycle
    // check must use canonical paths to avoid deadlock.
    [Fact]
    public void ExtendedConfigCacheExtendsCircularity_CaseInsensitiveSelfReferencing()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = "{\"extends\": \"./Base.json\"}",
            ["/project/base.json"] = "{\"extends\": \"./base.json\"}",
            ["/project/main.ts"] = "// Hello World!",
        };
        AssertHasCircularityDiagnostic(files);
    }

    // extendedconfigcache_test.go:89-108 — TestExtendedConfigCacheNullExtendsDoesNotPanic.
    [Fact]
    public void ExtendedConfigCacheNullExtendsDoesNotPanic()
    {
        var files = new Dictionary<string, string>
        {
            ["/project/tsconfig.json"] = "{\"extends\": null}",
            ["/project/main.ts"] = "// Hello World!",
        };

        var cmd = ParseWithCache(files);
        Assert.NotNull(cmd);
        Assert.NotEmpty(cmd!.Errors ?? []);
    }

    // extendedconfigcache_test.go:34-42 — fs + host + tsc ExtendedConfigCache, then
    // tsoptions.GetParsedCommandLineOfConfigFile.
    private static ParsedCommandLine? ParseWithCache(Dictionary<string, string> files)
    {
        var fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        var host = new TestParseConfigHost(fs, "/project");
        var cache = new TscExtendedConfigCache();
        var (cmd, _) = TsConfigParsing.GetParsedCommandLineOfConfigFile("/project/tsconfig.json", null, null, host, cache);
        return cmd;
    }

    private static void AssertHasCircularityDiagnostic(Dictionary<string, string> files)
    {
        var cmd = ParseWithCache(files);
        Assert.NotNull(cmd); // extendedconfigcache_test.go:39-41
        // extendedconfigcache_test.go:110-118 — assertHasCircularityDiagnostic.
        foreach (var d in cmd!.Errors ?? [])
        {
            if (d != null && d.Code() == 18000)
            {
                return;
            }
        }
        Assert.Fail("expected circularity diagnostic (code 18000), but none was found");
    }
}
