// Ported from tsgo internal/project/ata/discovertypings_test.go (whole file).
//
// PORT-NOTEs:
//  * Go `assert.Assert(t, cachedTypingPaths == nil)` → Assert.Empty: the C# surface
//    returns string[] (never a nil slice), and every Go nil here corresponds to "no
//    cached paths" (plan/05 §7.1).
//  * `assert.DeepEqual(t, collections.NewSetFromItems(...), ...)` → set equality by
//    membership via Set<string>.Equals (AssertSetDeepEqual helper).
//  * `maps.Clone` + `delete` → Dictionary copy + Remove; `core.VersionMajorMinor()` →
//    Ts.Core.TsVersion.VersionMajorMinor().
//  * The Go files map is `map[string]string`; VfsTestUtil.FromMap accepts string values.

using System.Collections.Generic;
using Ts.Collections;
using Ts.Project.Ata;
using Ts.Project.Logging;
using Ts.TestUtil.ProjectTestUtil;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Project.Tests.Ata;

public sealed class DiscoverTypingsTests
{
    // discovertypings_test.go:17-49 — "should use mappings from safe list".
    [Fact]
    public void TestDiscoverTypings_ShouldUseMappingsFromSafeList()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
            ["/home/src/projects/project/jquery.js"] = "",
            ["/home/src/projects/project/chroma.min.js"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
            },
            ["/home/src/projects/project/app.js", "/home/src/projects/project/jquery.js", "/home/src/projects/project/chroma.min.js"],
            "/home/src/projects/project",
            new SyncMap<string, CachedTyping>(),
            []
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "jquery",
            "chroma-js"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:51-81 — "should return node for core modules".
    [Fact]
    public void TestDiscoverTypings_ShouldReturnNodeForCoreModules()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        Set<string> unresolvedImports = Set.FromItems("assert", "somename");
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
                UnresolvedImports = unresolvedImports,
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            new SyncMap<string, CachedTyping>(),
            []
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "node",
            "somename"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:83-123 — "should use cached locations".
    [Fact]
    public void TestDiscoverTypings_ShouldUseCachedLocations()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
            ["/home/src/projects/project/node.d.ts"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        var cache = new SyncMap<string, CachedTyping>();
        global::Ts.Semver.Version version = global::Ts.Semver.Semver.MustParse("1.3.0");
        cache.Store("node", new CachedTyping
        {
            TypingsLocation = "/home/src/projects/project/node.d.ts",
            Version = version,
        });
        Set<string> unresolvedImports = Set.FromItems("fs", "bar");
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
                UnresolvedImports = unresolvedImports,
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            cache,
            new Dictionary<string, Dictionary<string, string>>
            {
                ["node"] = SessionUtils.TypesRegistryConfig(),
            }
        );
        Assert.Equal(new[]
        {
            "/home/src/projects/project/node.d.ts",
        }, cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "bar"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:125-162 — "should gracefully handle packages that have been
    // removed from the types-registry".
    [Fact]
    public void TestDiscoverTypings_ShouldGracefullyHandlePackagesThatHaveBeenRemovedFromTheTypesRegistry()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
            ["/home/src/projects/project/node.d.ts"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        var cache = new SyncMap<string, CachedTyping>();
        global::Ts.Semver.Version version = global::Ts.Semver.Semver.MustParse("1.3.0");
        cache.Store("node", new CachedTyping
        {
            TypingsLocation = "/home/src/projects/project/node.d.ts",
            Version = version,
        });
        Set<string> unresolvedImports = Set.FromItems("fs", "bar");
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
                UnresolvedImports = unresolvedImports,
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            cache,
            []
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "node",
            "bar"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:164-193 — "should search only 2 levels deep".
    [Fact]
    public void TestDiscoverTypings_ShouldSearchOnly2LevelsDeep()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
            ["/home/src/projects/project/node_modules/a/package.json"] = "{ \"name\": \"a\" }",
            ["/home/src/projects/project/node_modules/a/b/package.json"] = "{ \"name\": \"b\" }",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            new SyncMap<string, CachedTyping>(),
            []
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "a"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:195-223 — "should support scoped packages".
    [Fact]
    public void TestDiscoverTypings_ShouldSupportScopedPackages()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
            ["/home/src/projects/project/node_modules/@a/b/package.json"] = "{ \"name\": \"@a/b\" }",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            new SyncMap<string, CachedTyping>(),
            []
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "@a/b"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:225-270 — "should install expired typings".
    [Fact]
    public void TestDiscoverTypings_ShouldInstallExpiredTypings()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        var cache = new SyncMap<string, CachedTyping>();
        global::Ts.Semver.Version nodeVersion = global::Ts.Semver.Semver.MustParse("1.3.0");
        global::Ts.Semver.Version commanderVersion = global::Ts.Semver.Semver.MustParse("1.0.0");
        cache.Store("node", new CachedTyping
        {
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/node/index.d.ts",
            Version = nodeVersion,
        });
        cache.Store("commander", new CachedTyping
        {
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/commander/index.d.ts",
            Version = commanderVersion,
        });
        Set<string> unresolvedImports = Set.FromItems("http", "commander");
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
                UnresolvedImports = unresolvedImports,
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            cache,
            new Dictionary<string, Dictionary<string, string>>
            {
                ["node"] = SessionUtils.TypesRegistryConfig(),
                ["commander"] = SessionUtils.TypesRegistryConfig(),
            }
        );
        Assert.Equal(new[]
        {
            "/home/src/Library/Caches/typescript/node_modules/@types/node/index.d.ts",
        }, cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "commander"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:272-312 — "should install expired typings with prerelease
    // version of tsserver".
    [Fact]
    public void TestDiscoverTypings_ShouldInstallExpiredTypingsWithPrereleaseVersionOfTsserver()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        var cache = new SyncMap<string, CachedTyping>();
        global::Ts.Semver.Version nodeVersion = global::Ts.Semver.Semver.MustParse("1.0.0");
        cache.Store("node", new CachedTyping
        {
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/node/index.d.ts",
            Version = nodeVersion,
        });
        var config = new Dictionary<string, string>(SessionUtils.TypesRegistryConfig());
        config.Remove("ts" + global::Ts.Core.TsVersion.VersionMajorMinor());

        Set<string> unresolvedImports = Set.FromItems("http");
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
                UnresolvedImports = unresolvedImports,
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            cache,
            new Dictionary<string, Dictionary<string, string>>
            {
                ["node"] = config,
            }
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "node"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // discovertypings_test.go:314-359 — "prerelease typings are properly handled".
    [Fact]
    public void TestDiscoverTypings_PrereleaseTypingsAreProperlyHandled()
    {
        Logger logger = new LogTree("DiscoverTypings");
        var files = new Dictionary<string, string>
        {
            ["/home/src/projects/project/app.js"] = "",
        };
        global::Ts.Vfs.IFileSystem fs = VfsTestUtil.FromMap(files, false /*useCaseSensitiveFileNames*/);
        var cache = new SyncMap<string, CachedTyping>();
        global::Ts.Semver.Version nodeVersion = global::Ts.Semver.Semver.MustParse("1.3.0-next.0");
        global::Ts.Semver.Version commanderVersion = global::Ts.Semver.Semver.MustParse("1.3.0-next.0");
        cache.Store("node", new CachedTyping
        {
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/node/index.d.ts",
            Version = nodeVersion,
        });
        cache.Store("commander", new CachedTyping
        {
            TypingsLocation = ProjectTestUtilConstants.TestTypingsLocation + "/node_modules/@types/commander/index.d.ts",
            Version = commanderVersion,
        });
        var config = new Dictionary<string, string>(SessionUtils.TypesRegistryConfig());
        config["ts" + global::Ts.Core.TsVersion.VersionMajorMinor()] = "1.3.0-next.1";
        Set<string> unresolvedImports = Set.FromItems("http", "commander");
        (string[] cachedTypingPaths, string[] newTypingNames, string[] filesToWatch) = DiscoverTypingsApi.DiscoverTypings(
            fs,
            logger,
            new TypingsInfo
            {
                CompilerOptions = new global::Ts.Core.CompilerOptions(),
                TypeAcquisition = new global::Ts.Core.TypeAcquisition { Enable = global::Ts.Core.Tristate.True },
                UnresolvedImports = unresolvedImports,
            },
            ["/home/src/projects/project/app.js"],
            "/home/src/projects/project",
            cache,
            new Dictionary<string, Dictionary<string, string>>
            {
                ["node"] = config,
                ["commander"] = SessionUtils.TypesRegistryConfig(),
            }
        );
        Assert.Empty(cachedTypingPaths);
        AssertSetDeepEqual(Set.FromItems(
            "node",
            "commander"
        ), Set.FromItems(newTypingNames));
        Assert.Equal(new[]
        {
            "/home/src/projects/project/bower_components",
            "/home/src/projects/project/node_modules",
        }, filesToWatch);
    }

    // assert.DeepEqual(t, collections.NewSetFromItems(a...), collections.NewSetFromItems(b...))
    // — set equality by membership; the message lists both sides' contents for debuggability.
    private static void AssertSetDeepEqual(Set<string> expected, Set<string> actual)
    {
        Assert.True(expected.Equals(actual), $"set mismatch:\n  expected: [{string.Join(" ", expected.Keys())}]\n  actual:   [{string.Join(" ", actual.Keys())}]");
    }
}
