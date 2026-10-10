// Ported from tsgo internal/vfs/vfsmatch/vfsmatch_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// The tsconfig include/exclude glob engine is baseline-bearing; these tests are the primary
// correctness gate for it. Table-driven Go subtests become RdCase arrays iterated per Go
// test function; each case's name is attached to any failure for diagnosis.
//
// PORT-NOTES:
//  - runReadDirectoryCase calls the internal matchFiles directly (as Go does), reached via
//    InternalsVisibleTo("Ts.Compiler.Tests").
//  - compileGlobPattern returns (pattern, ok) in Go → nullable GlobPattern? here (null = !ok).
//  - Unicode ?/* cases advance by full runes (UTF-16 code units): '🎉' is a surrogate pair.

using System;
using System.Collections.Generic;
using Ts.Vfs;
using Ts.Vfs.VfsMatch;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch; // class name collides with the sibling namespace
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Vfs.Tests;

public class VfsMatchTests
{
    private static readonly string[] TsExtensions = { ".ts", ".tsx", ".d.ts" };

    private static bool Contains(string s, string sub) => s.Contains(sub, StringComparison.Ordinal);

    private static bool HasSuffix(string s, string suffix) => s.EndsWith(suffix, StringComparison.Ordinal);

    // ---- Hosts (mirroring the Go package-level host factories) ----

    private static IFileSystem CaseInsensitiveHost() => VfsTestUtil.FromMap(new Dictionary<string, string>
    {
        ["/dev/a.ts"] = "",
        ["/dev/a.d.ts"] = "",
        ["/dev/a.js"] = "",
        ["/dev/b.ts"] = "",
        ["/dev/b.js"] = "",
        ["/dev/c.d.ts"] = "",
        ["/dev/z/a.ts"] = "",
        ["/dev/z/abz.ts"] = "",
        ["/dev/z/aba.ts"] = "",
        ["/dev/z/b.ts"] = "",
        ["/dev/z/bbz.ts"] = "",
        ["/dev/z/bba.ts"] = "",
        ["/dev/x/a.ts"] = "",
        ["/dev/x/aa.ts"] = "",
        ["/dev/x/b.ts"] = "",
        ["/dev/x/y/a.ts"] = "",
        ["/dev/x/y/b.ts"] = "",
        ["/dev/js/a.js"] = "",
        ["/dev/js/b.js"] = "",
        ["/dev/js/d.min.js"] = "",
        ["/dev/js/ab.min.js"] = "",
        ["/ext/ext.ts"] = "",
        ["/ext/b/a..b.ts"] = "",
    }, false);

    private static IFileSystem CaseSensitiveHost() => VfsTestUtil.FromMap(new Dictionary<string, string>
    {
        ["/dev/a.ts"] = "",
        ["/dev/a.d.ts"] = "",
        ["/dev/a.js"] = "",
        ["/dev/b.ts"] = "",
        ["/dev/b.js"] = "",
        ["/dev/A.ts"] = "",
        ["/dev/B.ts"] = "",
        ["/dev/c.d.ts"] = "",
        ["/dev/z/a.ts"] = "",
        ["/dev/z/abz.ts"] = "",
        ["/dev/z/aba.ts"] = "",
        ["/dev/z/b.ts"] = "",
        ["/dev/z/bbz.ts"] = "",
        ["/dev/z/bba.ts"] = "",
        ["/dev/x/a.ts"] = "",
        ["/dev/x/b.ts"] = "",
        ["/dev/x/y/a.ts"] = "",
        ["/dev/x/y/b.ts"] = "",
        ["/dev/q/a/c/b/d.ts"] = "",
        ["/dev/js/a.js"] = "",
        ["/dev/js/b.js"] = "",
        ["/dev/js/d.MIN.js"] = "",
    }, true);

    private static IFileSystem CommonFoldersHost() => VfsTestUtil.FromMap(new Dictionary<string, string>
    {
        ["/dev/a.ts"] = "",
        ["/dev/a.d.ts"] = "",
        ["/dev/a.js"] = "",
        ["/dev/b.ts"] = "",
        ["/dev/x/a.ts"] = "",
        ["/dev/node_modules/a.ts"] = "",
        ["/dev/bower_components/a.ts"] = "",
        ["/dev/jspm_packages/a.ts"] = "",
    }, false);

    private static IFileSystem DottedFoldersHost() => VfsTestUtil.FromMap(new Dictionary<string, string>
    {
        ["/dev/x/d.ts"] = "",
        ["/dev/x/y/d.ts"] = "",
        ["/dev/x/y/.e.ts"] = "",
        ["/dev/x/.y/a.ts"] = "",
        ["/dev/.z/.b.ts"] = "",
        ["/dev/.z/c.ts"] = "",
        ["/dev/w/.u/e.ts"] = "",
        ["/dev/g.min.js/.g/g.ts"] = "",
    }, false);

    private static IFileSystem MixedExtensionHost() => VfsTestUtil.FromMap(new Dictionary<string, string>
    {
        ["/dev/a.ts"] = "",
        ["/dev/a.d.ts"] = "",
        ["/dev/a.js"] = "",
        ["/dev/b.tsx"] = "",
        ["/dev/b.d.ts"] = "",
        ["/dev/b.jsx"] = "",
        ["/dev/c.tsx"] = "",
        ["/dev/c.js"] = "",
        ["/dev/d.js"] = "",
        ["/dev/e.jsx"] = "",
        ["/dev/f.other"] = "",
    }, false);

    private static IFileSystem SameNamedDeclarationsHost() => VfsTestUtil.FromMap(new Dictionary<string, string>
    {
        ["/dev/a.tsx"] = "",
        ["/dev/a.d.ts"] = "",
        ["/dev/b.tsx"] = "",
        ["/dev/b.ts"] = "",
        ["/dev/c.tsx"] = "",
        ["/dev/m.ts"] = "",
        ["/dev/m.d.ts"] = "",
        ["/dev/n.tsx"] = "",
        ["/dev/n.ts"] = "",
        ["/dev/n.d.ts"] = "",
        ["/dev/o.ts"] = "",
        ["/dev/x.d.ts"] = "",
    }, false);

    private sealed class RdCase
    {
        public required string Name { get; init; }
        public required Func<IFileSystem> Host { get; init; }
        public string CurrentDir { get; init; } = "";
        public string Path { get; init; } = "";
        public string[]? Extensions { get; init; }
        public string[]? Excludes { get; init; }
        public string[]? Includes { get; init; }
        public int Depth { get; init; }
        public required Action<List<string>> Expect { get; init; }
    }

    private static void RunReadDirectoryCases(RdCase[] cases)
    {
        foreach (var tc in cases)
        {
            try
            {
                string currentDir = tc.CurrentDir == "" ? "/" : tc.CurrentDir;
                string path = tc.Path == "" ? "/dev" : tc.Path;
                int depth = tc.Depth == 0 ? VfsMatchApi.UnlimitedDepth : tc.Depth;
                var host = tc.Host();
                var got = VfsMatchApi.MatchFiles(path, tc.Extensions, tc.Excludes, tc.Includes, host.UseCaseSensitiveFileNames(), currentDir, depth, host);
                tc.Expect(got);
            }
            catch (Exception e) when (e is not Xunit.Sdk.XunitException x || !x.Message.StartsWith("[case", StringComparison.Ordinal))
            {
                throw new Xunit.Sdk.XunitException($"[case '{tc.Name}'] {e.Message}");
            }
        }
    }

    [Fact]
    public void ReadDirectory()
    {
        var cases = new[]
        {
            new RdCase
            {
                Name = "defaults include common package folders",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/b.ts", got);
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/node_modules/a.ts", got);
                    Assert.Contains("/dev/bower_components/a.ts", got);
                    Assert.Contains("/dev/jspm_packages/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "literal includes without exclusions",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "a.ts", "b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts", "/dev/b.ts" }, got),
            },
            new RdCase
            {
                Name = "literal includes with non ts extensions excluded",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "a.js", "b.js" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "literal includes missing files excluded",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "z.ts", "x.ts" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "literal includes with literal excludes",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "b.ts" },
                Includes = new[] { "a.ts", "b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts" }, got),
            },
            new RdCase
            {
                Name = "literal includes with wildcard excludes",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "*.ts", "z/??z.ts", "*/b.ts" },
                Includes = new[] { "a.ts", "b.ts", "z/a.ts", "z/abz.ts", "z/aba.ts", "x/b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/z/a.ts", "/dev/z/aba.ts" }, got),
            },
            new RdCase
            {
                Name = "literal includes with recursive excludes",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**/b.ts" },
                Includes = new[] { "a.ts", "b.ts", "x/a.ts", "x/b.ts", "x/y/a.ts", "x/y/b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts", "/dev/x/a.ts", "/dev/x/y/a.ts" }, got),
            },
            new RdCase
            {
                Name = "case sensitive exclude is respected",
                Host = CaseSensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**/b.ts" },
                Includes = new[] { "B.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/B.ts" }, got),
            },
            new RdCase
            {
                Name = "explicit includes keep common package folders",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "a.ts", "b.ts", "node_modules/a.ts", "bower_components/a.ts", "jspm_packages/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/b.ts", got);
                    Assert.Contains("/dev/node_modules/a.ts", got);
                    Assert.Contains("/dev/bower_components/a.ts", got);
                    Assert.Contains("/dev/jspm_packages/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "wildcard include sorted order",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "z/*.ts", "x/*.ts" },
                Expect = got => Assert.Equal(new[]
                {
                    "/dev/z/a.ts", "/dev/z/aba.ts", "/dev/z/abz.ts", "/dev/z/b.ts", "/dev/z/bba.ts", "/dev/z/bbz.ts",
                    "/dev/x/a.ts", "/dev/x/aa.ts", "/dev/x/b.ts",
                }, got),
            },
            new RdCase
            {
                Name = "wildcard include same named declarations excluded",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "*.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/b.ts", got);
                    Assert.Contains("/dev/a.d.ts", got);
                    Assert.Contains("/dev/c.d.ts", got);
                },
            },
            new RdCase
            {
                Name = "wildcard star matches only ts files",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "*" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(Contains(f, ".ts") || Contains(f, ".tsx") || Contains(f, ".d.ts"), $"unexpected file: {f}");
                    }
                    Assert.DoesNotContain("/dev/a.js", got);
                    Assert.DoesNotContain("/dev/b.js", got);
                },
            },
            new RdCase
            {
                Name = "wildcard question mark single character",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/?.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/x/a.ts", "/dev/x/b.ts" }, got),
            },
            new RdCase
            {
                Name = "wildcard recursive directory",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/z/a.ts", got);
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/x/y/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "double asterisk matches zero-or-more directories",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/**/a.ts" },
                Expect = got =>
                {
                    Assert.Equal(2, got.Count);
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/x/y/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "wildcard multiple recursive directories",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/y/**/a.ts", "x/**/a.ts", "z/**/a.ts" },
                Expect = got => Assert.True(got.Count > 0),
            },
            new RdCase
            {
                Name = "wildcard case sensitive matching",
                Host = CaseSensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/A.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/A.ts" }, got),
            },
            new RdCase
            {
                Name = "wildcard missing files excluded",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "*/z.ts" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "exclude folders with wildcards",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "z", "x" },
                Includes = new[] { "**/*" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(!Contains(f, "/z/") && !Contains(f, "/x/"), $"should not contain z or x: {f}");
                    }
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/b.ts", got);
                },
            },
            new RdCase
            {
                Name = "include paths outside project absolute",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "*", "/ext/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/ext/ext.ts", got);
                },
            },
            new RdCase
            {
                Name = "include paths outside project relative",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**" },
                Includes = new[] { "*", "../ext/*" },
                Expect = got => Assert.Contains("/ext/ext.ts", got),
            },
            new RdCase
            {
                Name = "include files containing double dots",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**" },
                Includes = new[] { "/ext/b/a..b.ts" },
                Expect = got => Assert.Contains("/ext/b/a..b.ts", got),
            },
            new RdCase
            {
                Name = "exclude files containing double dots",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "/ext/b/a..b.ts" },
                Includes = new[] { "/ext/**/*" },
                Expect = got =>
                {
                    Assert.Contains("/ext/ext.ts", got);
                    Assert.DoesNotContain("/ext/b/a..b.ts", got);
                },
            },
            new RdCase
            {
                Name = "common package folders implicitly excluded",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.DoesNotContain("/dev/node_modules/a.ts", got);
                    Assert.DoesNotContain("/dev/bower_components/a.ts", got);
                    Assert.DoesNotContain("/dev/jspm_packages/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "common package folders explicit recursive include",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/a.ts", "**/node_modules/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/node_modules/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "common package folders wildcard include",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "*/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.DoesNotContain("/dev/node_modules/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "common package folders explicit wildcard include",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "*/a.ts", "node_modules/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/node_modules/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "dotted folders not implicitly included",
                Host = DottedFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/**/*", "w/*/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/d.ts", got);
                    Assert.Contains("/dev/x/y/d.ts", got);
                    Assert.DoesNotContain("/dev/x/.y/a.ts", got);
                    Assert.DoesNotContain("/dev/x/y/.e.ts", got);
                    Assert.DoesNotContain("/dev/w/.u/e.ts", got);
                },
            },
            new RdCase
            {
                Name = "dotted folders explicitly included",
                Host = DottedFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/.y/a.ts", "/dev/.z/.b.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/.y/a.ts", got);
                    Assert.Contains("/dev/.z/.b.ts", got);
                },
            },
            new RdCase
            {
                Name = "dotted folders recursive wildcard matches directories",
                Host = DottedFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/.*/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/.y/a.ts", got);
                    Assert.Contains("/dev/.z/c.ts", got);
                    Assert.Contains("/dev/w/.u/e.ts", got);
                },
            },
            new RdCase
            {
                Name = "trailing recursive include returns empty",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "trailing recursive exclude removes everything",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**" },
                Includes = new[] { "**/*" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "multiple recursive directory patterns in includes",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/x/**/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/x/y/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "multiple recursive directory patterns in excludes",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**/x/**" },
                Includes = new[] { "**/a.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/z/a.ts", got);
                    Assert.DoesNotContain("/dev/x/a.ts", got);
                    Assert.DoesNotContain("/dev/x/y/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "implicit globbification expands directory",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "z" },
                Expect = got =>
                {
                    Assert.Contains("/dev/z/a.ts", got);
                    Assert.Contains("/dev/z/aba.ts", got);
                    Assert.Contains("/dev/z/b.ts", got);
                },
            },
            new RdCase
            {
                Name = "exclude patterns starting with starstar",
                Host = CaseSensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**/x" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(!Contains(f, "/x/"), $"should not contain /x/: {f}");
                    }
                },
            },
            new RdCase
            {
                Name = "include patterns starting with starstar",
                Host = CaseSensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/x", "**/a/**/b" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/q/a/c/b/d.ts", got);
                },
            },
            new RdCase
            {
                Name = "depth limit one",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Depth = 1,
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        string suffix = f["/dev/".Length..];
                        Assert.True(!Contains(suffix, "/"), $"depth 1 should not include nested files: {f}");
                    }
                },
            },
            new RdCase
            {
                Name = "depth limit two",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Depth = 2,
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/z/a.ts", got);
                    Assert.DoesNotContain("/dev/x/y/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "mixed extensions only ts",
                Host = MixedExtensionHost,
                Extensions = new[] { ".ts" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(HasSuffix(f, ".ts"), $"should only have .ts files: {f}");
                    }
                },
            },
            new RdCase
            {
                Name = "mixed extensions ts and tsx",
                Host = MixedExtensionHost,
                Extensions = new[] { ".ts", ".tsx" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(HasSuffix(f, ".ts") || HasSuffix(f, ".tsx"), $"should only have .ts or .tsx files: {f}");
                    }
                },
            },
            new RdCase
            {
                Name = "mixed extensions js and jsx",
                Host = MixedExtensionHost,
                Extensions = new[] { ".js", ".jsx" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(HasSuffix(f, ".js") || HasSuffix(f, ".jsx"), $"should only have .js or .jsx files: {f}");
                    }
                },
            },
            new RdCase
            {
                Name = "min js files excluded by wildcard",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".js" },
                Includes = new[] { "js/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/js/a.js", got);
                    Assert.Contains("/dev/js/b.js", got);
                    Assert.DoesNotContain("/dev/js/d.min.js", got);
                    Assert.DoesNotContain("/dev/js/ab.min.js", got);
                },
            },
            new RdCase
            {
                Name = "min js exclusion is case-sensitive on case-sensitive FS",
                Host = CaseSensitiveHost,
                Extensions = new[] { ".js" },
                Includes = new[] { "js/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/js/a.js", got);
                    Assert.Contains("/dev/js/b.js", got);
                    // Legacy behavior: only lowercase ".min.js" is excluded by default when case-sensitive.
                    Assert.Contains("/dev/js/d.MIN.js", got);
                },
            },
            new RdCase
            {
                Name = "min js files explicitly included",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".js" },
                Includes = new[] { "js/*.min.js" },
                Expect = got =>
                {
                    Assert.Contains("/dev/js/d.min.js", got);
                    Assert.Contains("/dev/js/ab.min.js", got);
                },
            },
            new RdCase
            {
                Name = "min js files included when pattern mentions .min.",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".js" },
                Includes = new[] { "js/*.min.*" },
                Expect = got =>
                {
                    Assert.Equal(2, got.Count);
                    Assert.Contains("/dev/js/d.min.js", got);
                    Assert.Contains("/dev/js/ab.min.js", got);
                },
            },
            new RdCase
            {
                Name = "exclude literal node_modules folder",
                Host = CommonFoldersHost,
                Extensions = TsExtensions,
                Excludes = new[] { "node_modules" },
                Includes = new[] { "**/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.DoesNotContain("/dev/node_modules/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "same named declarations include ts",
                Host = SameNamedDeclarationsHost,
                Extensions = TsExtensions,
                Includes = new[] { "*.ts" },
                Expect = got => Assert.True(got.Count > 0),
            },
            new RdCase
            {
                Name = "same named declarations include tsx",
                Host = SameNamedDeclarationsHost,
                Extensions = TsExtensions,
                Includes = new[] { "*.tsx" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(HasSuffix(f, ".tsx"), $"should only have .tsx files: {f}");
                    }
                },
            },
            new RdCase
            {
                Name = "empty includes returns all matching files",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Expect = got =>
                {
                    Assert.True(got.Count > 0);
                    Assert.Contains("/dev/a.ts", got);
                },
            },
            new RdCase
            {
                Name = "nil extensions returns all files",
                Host = CaseInsensitiveHost,
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/a.js", got);
                },
            },
            new RdCase
            {
                Name = "empty extensions slice returns all files",
                Host = CaseInsensitiveHost,
                Extensions = Array.Empty<string>(),
                Expect = got => Assert.True(got.Count > 0, "expected files to be returned"),
            },
        };

        RunReadDirectoryCases(cases);
    }

    [Theory]
    [InlineData("foo", true)]
    [InlineData("src", true)]
    [InlineData("foo.ts", false)]
    [InlineData("foo.", false)]
    [InlineData("*", false)]
    [InlineData("?", false)]
    [InlineData("foo*", false)]
    [InlineData("foo?", false)]
    [InlineData("foo.bar", false)]
    [InlineData("", true)]
    public void IsImplicitGlob(string input, bool expected)
    {
        Assert.Equal(expected, VfsMatchApi.IsImplicitGlob(input));
    }

    [Fact]
    public void ReadDirectoryEdgeCases()
    {
        var cases = new[]
        {
            new RdCase
            {
                Name = "rooted include path",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".ts" },
                Includes = new[] { "/dev/a.ts" },
                Expect = got => Assert.Contains("/dev/a.ts", got),
            },
            new RdCase
            {
                Name = "include with extension in path",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".ts" },
                Includes = new[] { "a.ts" },
                Expect = got => Assert.Contains("/dev/a.ts", got),
            },
            new RdCase
            {
                Name = "special regex characters in path",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/file+test.ts"] = "",
                    ["/dev/file[0].ts"] = "",
                    ["/dev/file(1).ts"] = "",
                    ["/dev/file$money.ts"] = "",
                    ["/dev/file^start.ts"] = "",
                    ["/dev/file|pipe.ts"] = "",
                    ["/dev/file#hash.ts"] = "",
                }, false),
                Extensions = new[] { ".ts" },
                Includes = new[] { "file+test.ts" },
                Expect = got => Assert.Contains("/dev/file+test.ts", got),
            },
            new RdCase
            {
                Name = "include pattern starting with question mark",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".ts" },
                Includes = new[] { "?.ts" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/b.ts", got);
                },
            },
            new RdCase
            {
                Name = "include pattern starting with star",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".ts" },
                Includes = new[] { "*b.ts" },
                Expect = got => Assert.Contains("/dev/b.ts", got),
            },
            new RdCase
            {
                Name = "case insensitive file matching",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/File.ts"] = "",
                    ["/dev/FILE.ts"] = "",
                }, true),
                Extensions = new[] { ".ts" },
                Includes = new[] { "*.ts" },
                Expect = got => Assert.Equal(2, got.Count),
            },
            new RdCase
            {
                Name = "nested subdirectory base path",
                Host = CaseSensitiveHost,
                Extensions = new[] { ".ts" },
                Includes = new[] { "q/a/c/b/d.ts" },
                Expect = got => Assert.Contains("/dev/q/a/c/b/d.ts", got),
            },
            new RdCase
            {
                Name = "current directory differs from path",
                Host = CaseInsensitiveHost,
                Extensions = new[] { ".ts" },
                Includes = new[] { "z/*.ts" },
                Expect = got => Assert.True(got.Count > 0),
            },
        };

        RunReadDirectoryCases(cases);
    }

    [Fact]
    public void ReadDirectoryEmptyIncludes()
    {
        var cases = new[]
        {
            new RdCase
            {
                Name = "empty includes slice behavior",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string> { ["/root/a.ts"] = "" }, true),
                Path = "/root",
                CurrentDir = "/",
                Extensions = new[] { ".ts" },
                Includes = Array.Empty<string>(),
                Expect = got =>
                {
                    if (got.Count == 0)
                    {
                        return;
                    }
                    Assert.Contains("/root/a.ts", got);
                },
            },
        };

        RunReadDirectoryCases(cases);
    }

    [Fact]
    public void ReadDirectorySymlinkCycle()
    {
        var cases = new[]
        {
            new RdCase
            {
                Name = "detects and skips symlink cycles",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, object>
                {
                    ["/root/file.ts"] = "",
                    ["/root/a/file.ts"] = "",
                    ["/root/a/b"] = VfsTestUtil.Symlink("/root/a"),
                }, true),
                Path = "/root",
                CurrentDir = "/",
                Extensions = new[] { ".ts" },
                Includes = new[] { "**/*" },
                Expect = got => Assert.Equal(new[] { "/root/file.ts", "/root/a/file.ts" }, got),
            },
        };

        RunReadDirectoryCases(cases);
    }

    [Fact]
    public void ReadDirectoryMatchesTypeScriptBaselines()
    {
        var cases = new[]
        {
            new RdCase
            {
                Name = "sorted in include order then alphabetical",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/z/a.ts"] = "",
                    ["/dev/z/aba.ts"] = "",
                    ["/dev/z/abz.ts"] = "",
                    ["/dev/z/b.ts"] = "",
                    ["/dev/z/bba.ts"] = "",
                    ["/dev/z/bbz.ts"] = "",
                    ["/dev/x/a.ts"] = "",
                    ["/dev/x/aa.ts"] = "",
                    ["/dev/x/b.ts"] = "",
                }, false),
                Extensions = TsExtensions,
                Includes = new[] { "z/*.ts", "x/*.ts" },
                Expect = got => Assert.Equal(new[]
                {
                    "/dev/z/a.ts", "/dev/z/aba.ts", "/dev/z/abz.ts", "/dev/z/b.ts", "/dev/z/bba.ts", "/dev/z/bbz.ts",
                    "/dev/x/a.ts", "/dev/x/aa.ts", "/dev/x/b.ts",
                }, got),
            },
            new RdCase
            {
                Name = "recursive wildcards match dotted directories",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/x/d.ts"] = "",
                    ["/dev/x/y/d.ts"] = "",
                    ["/dev/x/y/.e.ts"] = "",
                    ["/dev/x/.y/a.ts"] = "",
                    ["/dev/.z/.b.ts"] = "",
                    ["/dev/.z/c.ts"] = "",
                    ["/dev/w/.u/e.ts"] = "",
                    ["/dev/g.min.js/.g/g.ts"] = "",
                }, false),
                Extensions = TsExtensions,
                Includes = new[] { "**/.*/*" },
                Expect = got =>
                {
                    var expected = new[] { "/dev/.z/c.ts", "/dev/g.min.js/.g/g.ts", "/dev/w/.u/e.ts", "/dev/x/.y/a.ts" };
                    Assert.Equal(expected.Length, got.Count);
                    foreach (var want in expected)
                    {
                        Assert.Contains(want, got);
                    }
                },
            },
            new RdCase
            {
                Name = "common package folders implicitly excluded with wildcard",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/a.ts"] = "",
                    ["/dev/a.d.ts"] = "",
                    ["/dev/a.js"] = "",
                    ["/dev/b.ts"] = "",
                    ["/dev/x/a.ts"] = "",
                    ["/dev/node_modules/a.ts"] = "",
                    ["/dev/bower_components/a.ts"] = "",
                    ["/dev/jspm_packages/a.ts"] = "",
                }, false),
                Extensions = TsExtensions,
                Includes = new[] { "**/a.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts", "/dev/x/a.ts" }, got),
            },
            new RdCase
            {
                Name = "js wildcard excludes min js files",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/js/a.js"] = "",
                    ["/dev/js/b.js"] = "",
                    ["/dev/js/d.min.js"] = "",
                    ["/dev/js/ab.min.js"] = "",
                }, false),
                Extensions = new[] { ".js" },
                Includes = new[] { "js/*" },
                Expect = got => Assert.Equal(new[] { "/dev/js/a.js", "/dev/js/b.js" }, got),
            },
            new RdCase
            {
                Name = "explicit min js pattern includes min files",
                Host = () => VfsTestUtil.FromMap(new Dictionary<string, string>
                {
                    ["/dev/js/a.js"] = "",
                    ["/dev/js/b.js"] = "",
                    ["/dev/js/d.min.js"] = "",
                    ["/dev/js/ab.min.js"] = "",
                }, false),
                Extensions = new[] { ".js" },
                Includes = new[] { "js/*.min.js" },
                Expect = got =>
                {
                    var expected = new[] { "/dev/js/ab.min.js", "/dev/js/d.min.js" };
                    Assert.Equal(expected.Length, got.Count);
                    foreach (var want in expected)
                    {
                        Assert.Contains(want, got);
                    }
                },
            },
            new RdCase
            {
                Name = "literal excludes baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "b.ts" },
                Includes = new[] { "a.ts", "b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts" }, got),
            },
            new RdCase
            {
                Name = "wildcard excludes baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "*.ts", "z/??z.ts", "*/b.ts" },
                Includes = new[] { "a.ts", "b.ts", "z/a.ts", "z/abz.ts", "z/aba.ts", "x/b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/z/a.ts", "/dev/z/aba.ts" }, got),
            },
            new RdCase
            {
                Name = "recursive excludes baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**/b.ts" },
                Includes = new[] { "a.ts", "b.ts", "x/a.ts", "x/b.ts", "x/y/a.ts", "x/y/b.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts", "/dev/x/a.ts", "/dev/x/y/a.ts" }, got),
            },
            new RdCase
            {
                Name = "question mark baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/?.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/x/a.ts", "/dev/x/b.ts" }, got),
            },
            new RdCase
            {
                Name = "recursive directory pattern baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/a.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/a.ts", "/dev/x/a.ts", "/dev/x/y/a.ts", "/dev/z/a.ts" }, got),
            },
            new RdCase
            {
                Name = "case sensitive baseline",
                Host = CaseSensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/A.ts" },
                Expect = got => Assert.Equal(new[] { "/dev/A.ts" }, got),
            },
            new RdCase
            {
                Name = "exclude folders baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "z", "x" },
                Includes = new[] { "**/*" },
                Expect = got =>
                {
                    foreach (var f in got)
                    {
                        Assert.True(!Contains(f, "/z/") && !Contains(f, "/x/"), $"should not contain z or x: {f}");
                    }
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/dev/b.ts", got);
                },
            },
            new RdCase
            {
                Name = "implicit glob expansion baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "z" },
                Expect = got => Assert.Equal(new[] { "/dev/z/a.ts", "/dev/z/aba.ts", "/dev/z/abz.ts", "/dev/z/b.ts", "/dev/z/bba.ts", "/dev/z/bbz.ts" }, got),
            },
            new RdCase
            {
                Name = "trailing recursive directory baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "exclude trailing recursive directory baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**" },
                Includes = new[] { "**/*" },
                Expect = got => Assert.Empty(got),
            },
            new RdCase
            {
                Name = "multiple recursive directory patterns baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/x/**/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/x/aa.ts", got);
                    Assert.Contains("/dev/x/b.ts", got);
                    Assert.Contains("/dev/x/y/a.ts", got);
                    Assert.Contains("/dev/x/y/b.ts", got);
                },
            },
            new RdCase
            {
                Name = "include dirs with starstar prefix baseline",
                Host = CaseSensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "**/x", "**/a/**/b" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/a.ts", got);
                    Assert.Contains("/dev/x/b.ts", got);
                    Assert.Contains("/dev/q/a/c/b/d.ts", got);
                },
            },
            new RdCase
            {
                Name = "dotted folders not implicitly included baseline",
                Host = DottedFoldersHost,
                Extensions = TsExtensions,
                Includes = new[] { "x/**/*", "w/*/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/x/d.ts", got);
                    Assert.Contains("/dev/x/y/d.ts", got);
                    Assert.DoesNotContain("/dev/x/.y/a.ts", got);
                    Assert.DoesNotContain("/dev/x/y/.e.ts", got);
                    Assert.DoesNotContain("/dev/w/.u/e.ts", got);
                },
            },
            new RdCase
            {
                Name = "include paths outside project baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Includes = new[] { "*", "/ext/*" },
                Expect = got =>
                {
                    Assert.Contains("/dev/a.ts", got);
                    Assert.Contains("/ext/ext.ts", got);
                },
            },
            new RdCase
            {
                Name = "include files with double dots baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "**" },
                Includes = new[] { "/ext/b/a..b.ts" },
                Expect = got => Assert.Contains("/ext/b/a..b.ts", got),
            },
            new RdCase
            {
                Name = "exclude files with double dots baseline",
                Host = CaseInsensitiveHost,
                Extensions = TsExtensions,
                Excludes = new[] { "/ext/b/a..b.ts" },
                Includes = new[] { "/ext/**/*" },
                Expect = got =>
                {
                    Assert.Contains("/ext/ext.ts", got);
                    Assert.DoesNotContain("/ext/b/a..b.ts", got);
                },
            },
        };

        RunReadDirectoryCases(cases);
    }

    // ---- SpecMatcher API ----

    private sealed class SpecCase
    {
        public required string Name { get; init; }
        public required string[] Specs { get; init; }
        public required string BasePath { get; init; }
        public required Usage Usage { get; init; }
        public required bool UseCaseSensitiveFileNames { get; init; }
        public string[] MatchingPaths { get; init; } = Array.Empty<string>();
        public string[] NonMatchingPaths { get; init; } = Array.Empty<string>();
    }

    [Fact]
    public void SpecMatcher()
    {
        var cases = new[]
        {
            new SpecCase
            {
                Name = "simple wildcard",
                Specs = new[] { "*.ts" },
                BasePath = "/project",
                Usage = Usage.Files,
                UseCaseSensitiveFileNames = true,
                MatchingPaths = new[] { "/project/a.ts", "/project/b.ts", "/project/foo.ts" },
                NonMatchingPaths = new[] { "/project/a.js", "/project/sub/a.ts" },
            },
            new SpecCase
            {
                Name = "recursive wildcard",
                Specs = new[] { "**/*.ts" },
                BasePath = "/project",
                Usage = Usage.Files,
                UseCaseSensitiveFileNames = true,
                MatchingPaths = new[] { "/project/a.ts", "/project/sub/a.ts", "/project/sub/deep/a.ts" },
                NonMatchingPaths = new[] { "/project/a.js" },
            },
            new SpecCase
            {
                Name = "exclude pattern",
                Specs = new[] { "node_modules" },
                BasePath = "/project",
                Usage = Usage.Exclude,
                UseCaseSensitiveFileNames = true,
                MatchingPaths = new[] { "/project/node_modules/foo" },
                NonMatchingPaths = new[] { "/project/node_modules", "/project/src" },
            },
            new SpecCase
            {
                Name = "case insensitive",
                Specs = new[] { "*.ts" },
                BasePath = "/project",
                Usage = Usage.Files,
                UseCaseSensitiveFileNames = false,
                MatchingPaths = new[] { "/project/A.TS", "/project/B.Ts" },
                NonMatchingPaths = new[] { "/project/a.js" },
            },
            new SpecCase
            {
                Name = "multiple specs",
                Specs = new[] { "*.ts", "*.tsx" },
                BasePath = "/project",
                Usage = Usage.Files,
                UseCaseSensitiveFileNames = true,
                MatchingPaths = new[] { "/project/a.ts", "/project/b.tsx" },
                NonMatchingPaths = new[] { "/project/a.js" },
            },
        };

        foreach (var tc in cases)
        {
            var matcher = VfsMatchApi.NewSpecMatcher(tc.Specs, tc.BasePath, tc.Usage, tc.UseCaseSensitiveFileNames);
            Assert.NotNull(matcher);
            foreach (var path in tc.MatchingPaths)
            {
                Assert.True(matcher!.MatchString(path), $"[{tc.Name}] should match: {path}");
            }
            foreach (var path in tc.NonMatchingPaths)
            {
                Assert.False(matcher!.MatchString(path), $"[{tc.Name}] should not match: {path}");
            }
        }
    }

    [Fact]
    public void SpecMatcherMatchString()
    {
        var cases = new[]
        {
            (Name: "simple wildcard files", Specs: new[] { "*.ts" }, Usage: Usage.Files, Cs: true,
                Paths: new[] { "/project/a.ts", "/project/sub/a.ts", "/project/a.js" }, Expected: new[] { true, false, false }),
            (Name: "recursive wildcard files", Specs: new[] { "**/*.ts" }, Usage: Usage.Files, Cs: true,
                Paths: new[] { "/project/a.ts", "/project/sub/a.ts", "/project/a.js" }, Expected: new[] { true, true, false }),
            (Name: "exclude pattern matches prefix", Specs: new[] { "node_modules" }, Usage: Usage.Exclude, Cs: true,
                Paths: new[] { "/project/node_modules", "/project/node_modules/foo", "/project/src" }, Expected: new[] { false, true, false }),
        };

        foreach (var tc in cases)
        {
            var m = VfsMatchApi.NewSpecMatcher(tc.Specs, "/project", tc.Usage, tc.Cs);
            Assert.NotNull(m);
            for (int i = 0; i < tc.Paths.Length; i++)
            {
                Assert.Equal(tc.Expected[i], m!.MatchString(tc.Paths[i]));
            }
        }
    }

    [Fact]
    public void SingleSpecMatcherMatchString()
    {
        var cases = new[]
        {
            (Name: "single spec wildcard", Spec: "*.ts", Usage: Usage.Files, Cs: true,
                Paths: new[] { "/project/a.ts", "/project/sub/a.ts", "/project/a.js" }, Expected: new[] { true, false, false }),
            (Name: "single spec trailing starstar exclude allowed", Spec: "**", Usage: Usage.Exclude, Cs: true,
                Paths: new[] { "/project/a.ts", "/project/sub/a.ts" }, Expected: new[] { true, true }),
        };

        foreach (var tc in cases)
        {
            var m = VfsMatchApi.NewSpecMatcher(new[] { tc.Spec }, "/project", tc.Usage, tc.Cs);
            Assert.NotNull(m);
            for (int i = 0; i < tc.Paths.Length; i++)
            {
                Assert.Equal(tc.Expected[i], m!.MatchString(tc.Paths[i]));
            }
        }
    }

    [Fact]
    public void SpecMatchersMatchIndex()
    {
        var cases = new[]
        {
            (Name: "index lookup prefers first match", Specs: new[] { "*.ts", "*.tsx" }, Usage: Usage.Files,
                Paths: new[] { "/project/a.ts", "/project/a.tsx", "/project/a.js" }, Expected: new[] { 0, 1, -1 }),
            (Name: "exclude index lookup", Specs: new[] { "node_modules", "bower_components" }, Usage: Usage.Exclude,
                Paths: new[] { "/project/node_modules", "/project/node_modules/foo", "/project/bower_components", "/project/bower_components/bar", "/project/src" },
                Expected: new[] { -1, 0, -1, 1, -1 }),
        };

        foreach (var tc in cases)
        {
            var m = VfsMatchApi.NewSpecMatcher(tc.Specs, "/project", tc.Usage, true);
            Assert.NotNull(m);
            for (int i = 0; i < tc.Paths.Length; i++)
            {
                Assert.Equal(tc.Expected[i], m!.MatchIndex(tc.Paths[i]));
            }
        }
    }

    [Fact]
    public void SingleSpecMatcher()
    {
        // simple spec
        var m1 = VfsMatchApi.NewSpecMatcher(new[] { "*.ts" }, "/project", Usage.Files, true);
        Assert.NotNull(m1);
        Assert.True(m1!.MatchString("/project/a.ts"));
        Assert.False(m1.MatchString("/project/a.js"));

        // trailing ** non-exclude returns nil
        Assert.Null(VfsMatchApi.NewSpecMatcher(new[] { "**" }, "/project", Usage.Files, true));

        // trailing ** exclude works
        var m3 = VfsMatchApi.NewSpecMatcher(new[] { "**" }, "/project", Usage.Exclude, true);
        Assert.NotNull(m3);
        Assert.True(m3!.MatchString("/project/anything"));
        Assert.True(m3.MatchString("/project/deep/path"));
    }

    [Fact]
    public void SpecMatchers()
    {
        // multiple specs return correct index
        var m = VfsMatchApi.NewSpecMatcher(new[] { "*.ts", "*.tsx", "*.js" }, "/project", Usage.Files, true);
        Assert.NotNull(m);
        Assert.Equal(0, m!.MatchIndex("/project/a.ts"));
        Assert.Equal(1, m.MatchIndex("/project/b.tsx"));
        Assert.Equal(2, m.MatchIndex("/project/c.js"));
        Assert.Equal(-1, m.MatchIndex("/project/d.css"));

        // empty specs returns nil
        Assert.Null(VfsMatchApi.NewSpecMatcher(Array.Empty<string>(), "/project", Usage.Files, true));
    }

    // ---- White-box internal tests ----

    [Fact]
    public void GlobPatternInternals_NextPathPart()
    {
        // consecutive slashes
        string path = "/dev//foo///bar";
        Assert.True(VfsMatchApi.NextPathPartParts(path, "", 0, out string part, out int offset));
        Assert.Equal("", part);
        Assert.Equal(1, offset);

        Assert.True(VfsMatchApi.NextPathPartParts(path, "", 1, out part, out offset));
        Assert.Equal("dev", part);

        Assert.True(VfsMatchApi.NextPathPartParts(path, "", offset, out part, out offset));
        Assert.Equal("foo", part);

        Assert.True(VfsMatchApi.NextPathPartParts(path, "", offset, out part, out _));
        Assert.Equal("bar", part);

        // path ending with slashes
        path = "/dev/";
        Assert.True(VfsMatchApi.NextPathPartParts(path, "", 0, out _, out offset));
        Assert.True(VfsMatchApi.NextPathPartParts(path, "", offset, out _, out offset));
        Assert.False(VfsMatchApi.NextPathPartParts(path, "", offset, out _, out _));

        // empty prefix
        path = "/dev//foo";
        Assert.True(VfsMatchApi.NextPathPartParts("", path, 0, out part, out offset));
        Assert.Equal("", part);
        Assert.Equal(1, offset);
        Assert.True(VfsMatchApi.NextPathPartParts("", path, offset, out part, out offset));
        Assert.Equal("dev", part);
        Assert.True(VfsMatchApi.NextPathPartParts("", path, offset, out part, out _));
        Assert.Equal("foo", part);

        // only slashes remain (prefix+suffix)
        string prefix = "/dev/";
        string suffix = "foo";
        Assert.True(VfsMatchApi.NextPathPartParts(prefix, suffix, 0, out _, out offset));
        Assert.True(VfsMatchApi.NextPathPartParts(prefix, suffix, offset, out part, out offset));
        Assert.Equal("dev", part);
        Assert.True(VfsMatchApi.NextPathPartParts(prefix, suffix, offset, out part, out offset));
        Assert.Equal("foo", part);
        Assert.Equal(prefix.Length + suffix.Length, offset);
        Assert.False(VfsMatchApi.NextPathPartParts(prefix, suffix, offset, out _, out _));

        // parse from suffix region
        Assert.True(VfsMatchApi.NextPathPartParts("/", "a", 0, out part, out offset));
        Assert.Equal("", part);
        Assert.Equal(1, offset);
        Assert.True(VfsMatchApi.NextPathPartParts("/", "a", offset, out part, out _));
        Assert.Equal("a", part);
    }

    [Fact]
    public void GlobPatternInternals_QuestionAndStar()
    {
        // vfsmatch_test.go:1630-1641 — "question mark segment at end of string".
        // Create pattern with question mark that should fail when string is exhausted
        var p1 = VfsMatchApi.CompileGlobPattern("a?", "/", Usage.Files, true);
        Assert.NotNull(p1);

        // Should match "ab"
        Assert.True(p1!.Matches("/ab"));

        // Should NOT match "a" (question mark requires a character)
        Assert.False(p1.Matches("/a"));

        // vfsmatch_test.go:1714-1722 — "star with no trailing content".
        // Test that star can match to end of string
        var pStar = VfsMatchApi.CompileGlobPattern("a*", "/", Usage.Files, true);
        Assert.NotNull(pStar);
        Assert.True(pStar!.Matches("/a"));
        Assert.True(pStar.Matches("/abc"));
        Assert.True(pStar.Matches("/aXYZ"));

        // vfsmatch_test.go:1643-1660 — "star segment with complex pattern".
        // Pattern like "a*b*c" requires backtracking in star matching
        var p2 = VfsMatchApi.CompileGlobPattern("a*b*c", "/", Usage.Files, true);
        Assert.NotNull(p2);
        Assert.True(p2!.Matches("/abc"));
        Assert.True(p2.Matches("/aXbYc"));
        Assert.True(p2.Matches("/aXXXbYYYc"));
        Assert.False(p2.Matches("/aXbY"));
    }

    [Fact]
    public void GlobPatternInternals_EnsureTrailingSlash()
    {
        Assert.Equal("/dev/", VfsMatchApi.EnsureTrailingSlash("/dev/"));
        Assert.Equal("/", VfsMatchApi.EnsureTrailingSlash("/"));
        Assert.Equal("", VfsMatchApi.EnsureTrailingSlash(""));
    }

    [Fact]
    public void GlobPatternInternals_LiteralPackageFolder()
    {
        var host = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/dev/node_modules/pkg/index.ts"] = "",
        }, false);

        var got = VfsMatchApi.MatchFiles("/dev", new[] { ".ts" }, null, new[] { "node_modules/pkg/index.ts" }, false, "/", VfsMatchApi.UnlimitedDepth, host);
        Assert.Contains("/dev/node_modules/pkg/index.ts", got);
    }

    [Fact]
    public void MatchSegmentsEdgeCases()
    {
        // question mark before slash in string
        var p = VfsMatchApi.CompileGlobPattern("a?b", "/", Usage.Files, true);
        Assert.NotNull(p);
        Assert.True(p!.Matches("/aXb"));
        Assert.False(p.Matches("/ab"));
        Assert.False(p.Matches("/aXYb"));

        // star with no trailing content
        p = VfsMatchApi.CompileGlobPattern("a*", "/", Usage.Files, true);
        Assert.NotNull(p);
        Assert.True(p!.Matches("/a"));
        Assert.True(p.Matches("/abc"));
        Assert.True(p.Matches("/aXYZ"));

        // multiple stars
        p = VfsMatchApi.CompileGlobPattern("*a*", "/", Usage.Files, true);
        Assert.NotNull(p);
        Assert.True(p!.Matches("/a"));
        Assert.True(p.Matches("/Xa"));
        Assert.True(p.Matches("/aX"));
        Assert.True(p.Matches("/XaY"));
        Assert.False(p.Matches("/XYZ"));

        // multiple stars requiring backtracking
        var p1 = VfsMatchApi.CompileGlobPattern("*a*a", "/", Usage.Files, true)!;
        Assert.True(p1.Matches("/aa"));
        Assert.True(p1.Matches("/Xaa"));
        Assert.True(p1.Matches("/aXa"));
        Assert.True(p1.Matches("/XaYa"));
        Assert.True(p1.Matches("/aaaa"));
        Assert.False(p1.Matches("/a"));
        Assert.False(p1.Matches("/Xa"));
        Assert.False(p1.Matches("/aX"));
        Assert.False(p1.Matches("/XaYaZ"));

        var p2 = VfsMatchApi.CompileGlobPattern("*a*b*c", "/", Usage.Files, true)!;
        Assert.True(p2.Matches("/abc"));
        Assert.True(p2.Matches("/XaYbZc"));
        Assert.True(p2.Matches("/aXbYc"));
        Assert.True(p2.Matches("/aaabbbccc"));
        Assert.False(p2.Matches("/ab"));
        Assert.False(p2.Matches("/ac"));
        Assert.False(p2.Matches("/cba"));
        Assert.False(p2.Matches("/abcX"));

        var p3 = VfsMatchApi.CompileGlobPattern("*a*a*a", "/", Usage.Files, true)!;
        Assert.True(p3.Matches("/aaa"));
        Assert.True(p3.Matches("/aXaYa"));
        Assert.True(p3.Matches("/XaYaZa"));
        Assert.False(p3.Matches("/aa"));
        Assert.False(p3.Matches("/aaX"));

        var p4 = VfsMatchApi.CompileGlobPattern("a*b*a", "/", Usage.Files, true)!;
        Assert.True(p4.Matches("/aba"));
        Assert.True(p4.Matches("/aXbYa"));
        Assert.True(p4.Matches("/abba"));
        Assert.False(p4.Matches("/ab"));
        Assert.False(p4.Matches("/aba "));
        Assert.False(p4.Matches("/Xaba"));

        // pathological pattern performance (must not hang)
        var pp = VfsMatchApi.CompileGlobPattern("*a*a*a*a*b", "/", Usage.Files, true)!;
        Assert.False(pp.Matches("/aaaaaaaaaaaaaaaa"));
        Assert.False(pp.Matches("/aaaaaaaaaaaaaaaaX"));
        Assert.True(pp.Matches("/aaaab"));
        Assert.True(pp.Matches("/XaYaZaWab"));

        // literal segment not matching
        var pl = VfsMatchApi.CompileGlobPattern("abcdefgh.ts", "/", Usage.Files, true)!;
        Assert.False(pl.Matches("/abc.ts"));
        Assert.True(pl.Matches("/abcdefgh.ts"));
    }

    [Fact]
    public void MatchSegmentsUnicode()
    {
        // ? matches one full Unicode codepoint (UTF-16 code units: '🎉' is a surrogate pair)
        var p1 = VfsMatchApi.CompileGlobPattern("?.ts", "/", Usage.Files, true)!;
        Assert.True(p1.Matches("/a.ts"));
        Assert.True(p1.Matches("/é.ts"));
        Assert.True(p1.Matches("/中.ts"));
        Assert.True(p1.Matches("/🎉.ts"));
        Assert.False(p1.Matches("/.ts"));
        Assert.False(p1.Matches("/ab.ts"));

        var p2 = VfsMatchApi.CompileGlobPattern("??.ts", "/", Usage.Files, true)!;
        Assert.True(p2.Matches("/ab.ts"));
        Assert.True(p2.Matches("/é中.ts"));
        Assert.True(p2.Matches("/🎉é.ts"));
        Assert.False(p2.Matches("/a.ts"));
        Assert.False(p2.Matches("/abc.ts"));

        var p3 = VfsMatchApi.CompileGlobPattern("*é.ts", "/", Usage.Files, true)!;
        Assert.True(p3.Matches("/é.ts"));
        Assert.True(p3.Matches("/café.ts"));
        Assert.False(p3.Matches("/cafe.ts"));

        var p4 = VfsMatchApi.CompileGlobPattern("*🎉*", "/", Usage.Files, true)!;
        Assert.True(p4.Matches("/🎉"));
        Assert.True(p4.Matches("/a🎉b"));
        Assert.False(p4.Matches("/abc"));
    }

    [Fact]
    public void ReadDirectoryConsecutiveSlashes()
    {
        var host = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/dev/a.ts"] = "",
            ["/dev/x/b.ts"] = "",
        }, false);

        var got = VfsMatchApi.MatchFiles("/dev", new[] { ".ts" }, null, new[] { "**/*.ts" }, false, "/", VfsMatchApi.UnlimitedDepth, host);
        Assert.True(got.Count >= 2, "should find files");
        Assert.Contains("/dev/a.ts", got);
        Assert.Contains("/dev/x/b.ts", got);
    }

    [Fact]
    public void GlobPatternLiteralWithPackageFolders()
    {
        // wildcard skips package folders
        var host1 = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/dev/a.ts"] = "",
            ["/dev/node_modules/b.ts"] = "",
        }, false);
        var got1 = VfsMatchApi.MatchFiles("/dev", new[] { ".ts" }, null, new[] { "*/*.ts" }, false, "/", VfsMatchApi.UnlimitedDepth, host1);
        Assert.DoesNotContain("/dev/node_modules/b.ts", got1);

        // explicit literal includes package folder
        var host2 = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/dev/node_modules/b.ts"] = "",
        }, false);
        var got2 = VfsMatchApi.MatchFiles("/dev", new[] { ".ts" }, null, new[] { "node_modules/b.ts" }, false, "/", VfsMatchApi.UnlimitedDepth, host2);
        Assert.Contains("/dev/node_modules/b.ts", got2);
    }

    [Fact]
    public void GetBasePathsCaseSensitivity()
    {
        // case-sensitive does not dedup differently-cased paths
        var basePaths = VfsMatchApi.GetBasePaths("/root", new[] { "../Other/**/*.ts", "../other/**/*.ts" }, true);
        Assert.Contains("/Other", basePaths);
        Assert.Contains("/other", basePaths);

        // case-insensitive dedups differently-cased paths
        var basePaths2 = VfsMatchApi.GetBasePaths("/root", new[] { "../Other/**/*.ts", "../other/**/*.ts" }, false);
        int count = 0;
        foreach (var bp in basePaths2)
        {
            if (bp == "/Other" || bp == "/other")
            {
                count++;
            }
        }
        Assert.True(count <= 1, $"expected at most one of /Other or /other: {string.Join(", ", basePaths2)}");
    }
}
