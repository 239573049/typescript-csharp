// Ported from tsgo internal/tspath/untitled_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Go file is `package tspath_test` (external test package); irrelevant in C# — the
// tests only touch the public surface.

using Xunit;

namespace Ts.TsPath.Tests;

public class UntitledTests
{
    [Fact]
    public void UntitledPathHandling()
    {
        // Test that untitled paths are treated as rooted
        string untitledPath = "^/untitled/ts-nul-authority/Untitled-2";

        // GetEncodedRootLength should return 2 for "^/"
        int rootLength = TsPath.GetEncodedRootLength(untitledPath);
        Assert.Equal(2, rootLength); // GetEncodedRootLength should return 2 for untitled paths

        // IsRootedDiskPath should return true
        bool isRooted = TsPath.IsRootedDiskPath(untitledPath);
        Assert.True(isRooted, "IsRootedDiskPath should return true for untitled paths");

        // ToPath should not resolve untitled paths against current directory
        string currentDir = "/home/user/project";
        Path path = TsPath.ToPath(untitledPath, currentDir, true);
        // The path should be the original untitled path
        Assert.Equal("^/untitled/ts-nul-authority/Untitled-2", path.Value); // ToPath should not resolve untitled paths against current directory

        // Test GetNormalizedAbsolutePath doesn't resolve untitled paths
        string normalized = TsPath.GetNormalizedAbsolutePath(untitledPath, currentDir);
        Assert.Equal("^/untitled/ts-nul-authority/Untitled-2", normalized); // GetNormalizedAbsolutePath should not resolve untitled paths
    }

    [Theory]
    [InlineData("^/", 2, true)] // Minimal untitled path
    [InlineData("^/untitled/ts-nul-authority/test", 2, true)] // Normal untitled path
    [InlineData("^", 0, false)] // Just ^ is not rooted
    [InlineData("^x", 0, false)] // ^x is not untitled
    [InlineData("^^/", 0, false)] // ^^/ is not untitled
    [InlineData("x^/", 0, false)] // x^/ is not untitled (doesn't start with ^)
    [InlineData("^/untitled/ts-nul-authority/path/with/deeper/structure", 2, true)] // Deeper path
    public void UntitledPathEdgeCases(string path, int expected, bool isRooted)
    {
        int rootLength = TsPath.GetEncodedRootLength(path);
        Assert.Equal(expected, rootLength); // Go msg: "GetEncodedRootLength for path %s"

        bool rooted = TsPath.IsRootedDiskPath(path);
        Assert.Equal(isRooted, rooted); // Go msg: "IsRootedDiskPath for path %s"
    }
}
