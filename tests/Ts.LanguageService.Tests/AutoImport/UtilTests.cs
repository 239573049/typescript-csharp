// Ported from tsgo internal/ls/autoimport/util_test.go (whole file).
//
// PORT-NOTE: Go indexes into strings with byte offsets (wordIndices returns UTF-8 byte
// starts); here strings are UTF-16 and WordIndices returns UTF-16 code-unit indices, so
// the expected words are reproduced by slicing the same string with those indices.
//
// PORT-NOTE: Go's vfstest.FromMap / vfstest.Symlink map to Ts.Vfs.VfsTest.VfsTestUtil
// (tests/Ts.TestUtil/VfsTest/VfsTest.cs). Util.WordIndices and Util.GetPackageRealpathFuncs
// are internal, visible via InternalsVisibleTo (plan/05-conventions.md §16).

using System.Collections.Generic;
using Ts.Ls.AutoImport;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Ls.AutoImport.Tests;

public sealed class UtilTests
{
    // util_test.go:11-93 — TestWordIndices.
    public static TheoryData<string, string[]> WordIndicesData()
    {
        return new TheoryData<string, string[]>
        {
            // Basic camelCase
            { "camelCase", ["camelCase", "Case"] },
            // snake_case
            { "snake_case", ["snake_case", "case"] },
            // ParseURL - uppercase sequence followed by lowercase
            { "ParseURL", ["ParseURL", "URL"] },
            // XMLHttpRequest - multiple uppercase sequences
            { "XMLHttpRequest", ["XMLHttpRequest", "HttpRequest", "Request"] },
            // Single word lowercase
            { "hello", ["hello"] },
            // Single word uppercase
            { "HELLO", ["HELLO"] },
            // Mixed with numbers
            { "parseHTML5Parser", ["parseHTML5Parser", "HTML5Parser", "Parser"] },
            // Underscore variations
            { "__proto__", ["__proto__", "proto__"] },
            { "_private_member", ["_private_member", "member"] },
            // Single character
            { "a", ["a"] },
            { "A", ["A"] },
            // Consecutive underscores
            { "test__double__underscore", ["test__double__underscore", "double__underscore", "underscore"] },
        };
    }

    [Theory]
    [MemberData(nameof(WordIndicesData))]
    public void WordIndices(string input, string[] expectedWords)
    {
        List<int> indices = Util.WordIndices(input);

        // Convert indices to actual word slices for comparison.
        var actualWords = new List<string>();
        foreach (int idx in indices)
        {
            actualWords.Add(input[idx..]);
        }

        Assert.Equal(expectedWords, actualWords);
    }

    // util_test.go:95-147 — TestGetPackageRealpathFuncs_FollowsNodeModulesSymlinks.
    [Fact]
    public void GetPackageRealpathFuncs_FollowsNodeModulesSymlinks()
    {
        // Simulate a layout where the package directory is itself a symlink (e.g. Bazel's
        // convenience symlinks or pnpm's virtual store):
        //   /symlink-bin/pkg/              -> symlink to /real/bin/pkg/
        //   /real/bin/pkg/node_modules/dep -> symlink to /real/dep/
        var files = new Dictionary<string, object?>
        {
            ["/symlink-bin/pkg"] = VfsTestUtil.Symlink("/real/bin/pkg"),
            ["/real/bin/pkg/index.d.ts"] = "export declare const a: number;",
            ["/real/bin/pkg/node_modules/dep"] = VfsTestUtil.Symlink("/real/dep"),
            ["/real/dep/index.d.ts"] = "export declare const b: number;",
            ["/real/dep/src/utils/helper.d.ts"] = "export declare const c: number;",
        };
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);

        (System.Func<string, string> toRealpath, _) = Util.GetPackageRealpathFuncs(fs, "/symlink-bin/pkg");

        // Files inside the package should be converted via string replacement (fast path).
        Assert.Equal("/real/bin/pkg/index.d.ts", toRealpath("/symlink-bin/pkg/index.d.ts"));

        // Files outside the package (e.g. node_modules symlinks) should be resolved via
        // fs.Realpath so the cache key is the canonical realpath, not the symlink path.
        Assert.Equal("/real/dep/index.d.ts", toRealpath("/real/bin/pkg/node_modules/dep/index.d.ts"));

        // Files in subdirectories of an already-resolved external package should use the
        // cached prefix mapping without additional realpath calls.
        Assert.Equal("/real/dep/src/utils/helper.d.ts", toRealpath("/real/bin/pkg/node_modules/dep/src/utils/helper.d.ts"));
    }

    // util_test.go:149-190 — TestGetPackageRealpathFuncs_DuplicateCacheKeys.
    [Fact]
    public void GetPackageRealpathFuncs_DuplicateCacheKeys()
    {
        // Simulate two packages (app-a, app-b) that each have a node_modules symlink to
        // the same shared dependency.
        var files = new Dictionary<string, object?>
        {
            ["/workspace/packages/app-a"] = VfsTestUtil.Symlink("/store/app-a"),
            ["/workspace/packages/app-b"] = VfsTestUtil.Symlink("/store/app-b"),
            ["/store/app-a/index.d.ts"] = "export declare const a: number;",
            ["/store/app-b/index.d.ts"] = "export declare const b: number;",
            ["/store/app-a/node_modules/shared-lib"] = VfsTestUtil.Symlink("/store/shared-lib"),
            ["/store/app-b/node_modules/shared-lib"] = VfsTestUtil.Symlink("/store/shared-lib"),
            ["/store/shared-lib/index.d.ts"] = "export declare const shared: string;",
        };
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);

        (System.Func<string, string> toRealpathA, _) = Util.GetPackageRealpathFuncs(fs, "/workspace/packages/app-a");
        (System.Func<string, string> toRealpathB, _) = Util.GetPackageRealpathFuncs(fs, "/workspace/packages/app-b");

        string resolvedA = toRealpathA("/store/app-a/node_modules/shared-lib/index.d.ts");
        string resolvedB = toRealpathB("/store/app-b/node_modules/shared-lib/index.d.ts");

        // Both should resolve to the same canonical realpath so the module resolver uses a
        // single cache key for the shared dependency, avoiding duplicate loads.
        const string expectedRealpath = "/store/shared-lib/index.d.ts";
        Assert.Equal(expectedRealpath, resolvedA);
        Assert.Equal(expectedRealpath, resolvedB);
    }

    // util_test.go:192-220 — TestGetPackageRealpathFuncs_NonSymlinkedPackageWithSymlinkedDeps.
    [Fact]
    public void GetPackageRealpathFuncs_NonSymlinkedPackageWithSymlinkedDeps()
    {
        var files = new Dictionary<string, object?>
        {
            ["/real/my-pkg/index.d.ts"] = "export declare const a: number;",
            ["/real/my-pkg/node_modules/dep"] = VfsTestUtil.Symlink("/real/dep"),
            ["/real/dep/index.d.ts"] = "export declare const b: number;",
        };
        var fs = VfsTestUtil.FromMap(files, useCaseSensitiveFileNames: true);

        (System.Func<string, string> toRealpath, _) = Util.GetPackageRealpathFuncs(fs, "/real/my-pkg");

        // Files inside the (non-symlinked) package should be returned unchanged.
        Assert.Equal("/real/my-pkg/index.d.ts", toRealpath("/real/my-pkg/index.d.ts"));

        // Files outside the package reached via symlinked node_modules should still be resolved.
        Assert.Equal("/real/dep/index.d.ts", toRealpath("/real/my-pkg/node_modules/dep/index.d.ts"));
    }
}
