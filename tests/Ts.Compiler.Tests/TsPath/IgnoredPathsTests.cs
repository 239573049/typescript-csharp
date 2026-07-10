// Ported from tsgo internal/tspath/ignoredpaths_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using Xunit;

namespace Ts.TsPath.Tests;

public class IgnoredPathsTests
{
    public static TheoryData<string, string, bool> ContainsIgnoredPathData()
    {
        return new TheoryData<string, string, bool>
        {
            { "node_modules dot path", "/project/node_modules/.pnpm/file.ts", true },
            { "git directory", "/project/.git/hooks/pre-commit", true },
            { "emacs lock file", "/project/src/file.ts.#", true },
            { "regular file path", "/project/src/file.ts", false },
            { "node_modules without dot", "/project/node_modules/lodash/index.js", false },
            { "empty path", "", false },
            { "path with multiple ignored patterns", "/project/node_modules/.pnpm/.git/.#file.ts", true },
            { "case sensitive test", "/project/NODE_MODULES/.PNPM/file.ts", false }, // Should be case sensitive
            { "path with ignored pattern in middle", "/project/src/node_modules/.pnpm/dist/file.js", true },
            { "path with ignored pattern at end", "/project/src/file.ts.#", true },
        };
    }

    [Theory]
    [MemberData(nameof(ContainsIgnoredPathData))]
    public void ContainsIgnoredPath(string name, string path, bool expected)
    {
        bool result = TsPath.ContainsIgnoredPath(path);
        // Go: t.Errorf("ContainsIgnoredPath(%q) = %v, expected %v", ...)
        Assert.True(result == expected, $"{name}: ContainsIgnoredPath(\"{path}\") = {result}, expected {expected}");
    }

    [Fact]
    public void IgnoredPathsPatterns()
    {
        // Test that all expected patterns are present
        string[] expectedPatterns = ["/node_modules/.", "/.git", ".#"];

        foreach (string pattern in expectedPatterns)
        {
            string testPath = "/test" + pattern + "/file.ts";
            // Go: t.Errorf("Expected pattern '%s' to be detected in path '%s'", ...)
            Assert.True(TsPath.ContainsIgnoredPath(testPath), $"Expected pattern '{pattern}' to be detected in path '{testPath}'");
        }
    }

    public static TheoryData<string, string, bool> IgnoredPathsEdgeCasesData()
    {
        return new TheoryData<string, string, bool>
        {
            { "pattern at start", "/node_modules./file.ts", false }, // Pattern is "/node_modules/." not "/node_modules."
            { "pattern at end", "/project/file.ts.#", true },
            { "multiple occurrences", "/project/.git/node_modules./.git/file.ts", true },
            { "no slashes", "node_modules.file.ts", false },
            { "single slash", "/file.ts", false },
        };
    }

    [Theory]
    [MemberData(nameof(IgnoredPathsEdgeCasesData))]
    public void IgnoredPathsEdgeCases(string name, string path, bool expected)
    {
        bool result = TsPath.ContainsIgnoredPath(path);
        // Go: t.Errorf("ContainsIgnoredPath(%q) = %v, expected %v", ...)
        Assert.True(result == expected, $"{name}: ContainsIgnoredPath(\"{path}\") = {result}, expected {expected}");
    }
}
