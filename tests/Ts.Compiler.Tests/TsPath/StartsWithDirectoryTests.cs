// Ported from tsgo internal/tspath/startsWithDirectory_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using Xunit;

namespace Ts.TsPath.Tests;

public class StartsWithDirectoryTests
{
    public static TheoryData<string, string, string, bool, bool> StartsWithDirectoryData()
    {
        return new TheoryData<string, string, string, bool, bool>
        {
            { "exact match case sensitive", "/project/src/file.ts", "/project/src", true, true },
            { "exact match case insensitive", "/project/src/file.ts", "/PROJECT/SRC", false, true },
            { "case sensitive mismatch", "/project/src/file.ts", "/PROJECT/SRC", true, false },
            { "file not in directory", "/project/lib/file.ts", "/project/src", true, false },
            { "file in subdirectory", "/project/src/components/Button.tsx", "/project/src", true, true },
            { "file in parent directory", "/project/file.ts", "/project/src", true, false },
            { "windows style separators", "C:\\project\\src\\file.ts", "C:\\project\\src", true, true },
            { "mixed separators", "/project/src/file.ts", "\\project\\src", true, false },
            { "empty directory name", "/project/src/file.ts", "", true, false },
            { "empty file name", "", "/project/src", true, false },
            { "identical paths", "/project/src", "/project/src", true, false }, // File name doesn't start with directory + separator
            { "directory with trailing separator", "/project/src/file.ts", "/project/src/", true, true },
            { "unicode characters", "/project/测试/file.ts", "/project/测试", true, true },
            { "unicode case insensitive", "/project/测试/file.ts", "/PROJECT/测试", false, true },
        };
    }

    [Theory]
    [MemberData(nameof(StartsWithDirectoryData))]
    public void StartsWithDirectory(string name, string fileName, string directoryName, bool useCaseSensitiveFileNames, bool expected)
    {
        bool result = TsPath.StartsWithDirectory(fileName, directoryName, useCaseSensitiveFileNames);
        // Go: t.Errorf("StartsWithDirectory(%q, %q, %v) = %v, expected %v", ...)
        Assert.True(result == expected, $"{name}: StartsWithDirectory(\"{fileName}\", \"{directoryName}\", {useCaseSensitiveFileNames}) = {result}, expected {expected}");
    }

    public static TheoryData<string, string, string, bool, bool> StartsWithDirectoryEdgeCasesData()
    {
        return new TheoryData<string, string, string, bool, bool>
        {
            { "file name shorter than directory", "/proj", "/project", true, false },
            { "file name starts with directory but no separator", "/projectsrc/file.ts", "/project", true, false },
            { "relative paths", "src/file.ts", "src", true, true },
            { "absolute vs relative", "/project/src/file.ts", "project/src", true, false },
        };
    }

    [Theory]
    [MemberData(nameof(StartsWithDirectoryEdgeCasesData))]
    public void StartsWithDirectoryEdgeCases(string name, string fileName, string directoryName, bool useCaseSensitiveFileNames, bool expected)
    {
        bool result = TsPath.StartsWithDirectory(fileName, directoryName, useCaseSensitiveFileNames);
        // Go: t.Errorf("StartsWithDirectory(%q, %q, %v) = %v, expected %v", ...)
        Assert.True(result == expected, $"{name}: StartsWithDirectory(\"{fileName}\", \"{directoryName}\", {useCaseSensitiveFileNames}) = {result}, expected {expected}");
    }
}
