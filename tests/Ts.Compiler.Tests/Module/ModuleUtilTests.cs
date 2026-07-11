// Ported from tsgo internal/module/resolver_test.go TestParseNodeModuleFromPath and the
// util.go helpers (commit 168e7015edf98244febc8f4ae450b673b5d195d7).

using Xunit;
using ModuleApi = Ts.Module.Module;

namespace Ts.ModuleTests;

public class ModuleUtilTests
{
    [Theory]
    [InlineData("/a/node_modules/b/lib/index.d.ts", false, "/a/node_modules/b")]
    [InlineData("/a/node_modules/@scope/b/lib/index.d.ts", false, "/a/node_modules/@scope/b")]
    [InlineData("/a/node_modules/b/lib/File", true, "/a/node_modules/b")]
    [InlineData("/a/node_modules/@scope/b/lib/File", true, "/a/node_modules/@scope/b")]
    [InlineData("/a/node_modules/b", true, "/a/node_modules/b")]
    [InlineData("/a/node_modules/@scope/b", true, "/a/node_modules/@scope/b")]
    // A bare scope directory has no package name; must not throw.
    [InlineData("/a/node_modules/@scope", true, "/a/node_modules/@scope")]
    [InlineData("/a/node_modules/@types", true, "/a/node_modules/@types")]
    [InlineData("/a/src/index.ts", false, "")]
    public void TestParseNodeModuleFromPath(string path, bool isFolder, string want)
    {
        Assert.Equal(want, ModuleApi.ParseNodeModuleFromPath(path, isFolder));
    }

    [Theory]
    [InlineData("foo", "foo", "")]
    [InlineData("foo/bar", "foo", "bar")]
    [InlineData("foo/bar/baz", "foo", "bar/baz")]
    [InlineData("@scope/pkg", "@scope/pkg", "")]
    [InlineData("@scope/pkg/sub", "@scope/pkg", "sub")]
    [InlineData("@scope", "@scope", "")]
    public void TestParsePackageName(string moduleName, string wantPackage, string wantRest)
    {
        (string packageName, string rest) = ModuleApi.ParsePackageName(moduleName);
        Assert.Equal(wantPackage, packageName);
        Assert.Equal(wantRest, rest);
    }

    [Theory]
    [InlineData("@scope/pkg", "scope__pkg")]
    [InlineData("pkg", "pkg")]
    [InlineData("@scope", "@scope")]
    public void TestMangleScopedPackageName(string input, string want)
    {
        Assert.Equal(want, ModuleApi.MangleScopedPackageName(input));
    }

    [Theory]
    [InlineData("scope__pkg", "@scope/pkg")]
    [InlineData("pkg", "pkg")]
    public void TestUnmangleScopedPackageName(string input, string want)
    {
        Assert.Equal(want, ModuleApi.UnmangleScopedPackageName(input));
    }

    [Theory]
    [InlineData("pkg", "@types/pkg")]
    [InlineData("@scope/pkg", "@types/scope__pkg")]
    public void TestGetTypesPackageName(string input, string want)
    {
        Assert.Equal(want, ModuleApi.GetTypesPackageName(input));
    }
}
