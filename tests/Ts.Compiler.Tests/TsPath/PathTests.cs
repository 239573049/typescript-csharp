// Ported from tsgo internal/tspath/path_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// PORT-NOTE (05 §16.1): BenchmarkCombinePaths/BenchmarkGetNormalizedAbsolutePath/
// BenchmarkToFileNameLowerCase/BenchmarkHasRelativePathSegment/BenchmarkPathIsRelative
// move to BenchmarkDotNet in phase 10; Fuzz* tests are not ported; the bench/fuzz-only
// helpers (shortenName, normalizePath_old, getNormalizedAbsolutePath_old,
// oldToFileNameLowerCase, fileNameLowerCaseRegExp, oldHasRelativePathSegment,
// getNormalizedAbsolutePathTests, toFileNameLowerCaseTests, hasRelativePathSegmentTests)
// are therefore not ported either. t.Parallel() calls are dropped (assembly-level
// parallelism, 05 §16.1).

using System.Collections.Generic;
using Xunit;

namespace Ts.TsPath.Tests;

public class PathTests
{
    // Go: assert.DeepEqual on []string (actual first in gotest.tools; expected first here).
    private static void AssertDeepEqual(string[] expected, List<string> actual)
    {
        Assert.Equal(expected, (IEnumerable<string>)actual);
    }

    [Fact]
    public void NormalizeSlashes()
    {
        Assert.Equal("a", TsPath.NormalizeSlashes("a"));
        Assert.Equal("a/b", TsPath.NormalizeSlashes("a/b"));
        Assert.Equal("a/b", TsPath.NormalizeSlashes("a\\b"));
        Assert.Equal("//server/path", TsPath.NormalizeSlashes("\\\\server\\path"));
    }

    [Fact]
    public void GetRootLength()
    {
        Assert.Equal(0, TsPath.GetRootLength("a"));
        Assert.Equal(1, TsPath.GetRootLength("/"));
        Assert.Equal(1, TsPath.GetRootLength("/path"));
        Assert.Equal(2, TsPath.GetRootLength("c:"));
        Assert.Equal(0, TsPath.GetRootLength("c:d"));
        Assert.Equal(3, TsPath.GetRootLength("c:/"));
        Assert.Equal(3, TsPath.GetRootLength("c:\\"));
        Assert.Equal(8, TsPath.GetRootLength("//server"));
        Assert.Equal(9, TsPath.GetRootLength("//server/share"));
        Assert.Equal(8, TsPath.GetRootLength("\\\\server"));
        Assert.Equal(9, TsPath.GetRootLength("\\\\server\\share"));
        Assert.Equal(8, TsPath.GetRootLength("file:///"));
        Assert.Equal(8, TsPath.GetRootLength("file:///path"));
        Assert.Equal(10, TsPath.GetRootLength("file:///c:"));
        Assert.Equal(8, TsPath.GetRootLength("file:///c:d"));
        Assert.Equal(11, TsPath.GetRootLength("file:///c:/path"));
        Assert.Equal(12, TsPath.GetRootLength("file:///c%3a"));
        Assert.Equal(8, TsPath.GetRootLength("file:///c%3ad"));
        Assert.Equal(13, TsPath.GetRootLength("file:///c%3a/path"));
        Assert.Equal(12, TsPath.GetRootLength("file:///c%3A"));
        Assert.Equal(8, TsPath.GetRootLength("file:///c%3Ad"));
        Assert.Equal(13, TsPath.GetRootLength("file:///c%3A/path"));
        Assert.Equal(16, TsPath.GetRootLength("file://localhost"));
        Assert.Equal(17, TsPath.GetRootLength("file://localhost/"));
        Assert.Equal(17, TsPath.GetRootLength("file://localhost/path"));
        Assert.Equal(19, TsPath.GetRootLength("file://localhost/c:"));
        Assert.Equal(17, TsPath.GetRootLength("file://localhost/c:d"));
        Assert.Equal(20, TsPath.GetRootLength("file://localhost/c:/path"));
        Assert.Equal(21, TsPath.GetRootLength("file://localhost/c%3a"));
        Assert.Equal(17, TsPath.GetRootLength("file://localhost/c%3ad"));
        Assert.Equal(22, TsPath.GetRootLength("file://localhost/c%3a/path"));
        Assert.Equal(21, TsPath.GetRootLength("file://localhost/c%3A"));
        Assert.Equal(17, TsPath.GetRootLength("file://localhost/c%3Ad"));
        Assert.Equal(22, TsPath.GetRootLength("file://localhost/c%3A/path"));
        Assert.Equal(13, TsPath.GetRootLength("file://server"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/path"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c:"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c:d"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c:/d"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c%3a"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c%3ad"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c%3a/d"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c%3A"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c%3Ad"));
        Assert.Equal(14, TsPath.GetRootLength("file://server/c%3A/d"));
        Assert.Equal(13, TsPath.GetRootLength("http://server"));
        Assert.Equal(14, TsPath.GetRootLength("http://server/path"));
    }

    [Fact]
    public void PathIsAbsolute()
    {
        // POSIX
        Assert.True(TsPath.PathIsAbsolute("/path/to/file.ext"));
        // DOS
        Assert.True(TsPath.PathIsAbsolute("c:/path/to/file.ext"));
        // URL
        Assert.True(TsPath.PathIsAbsolute("file:///path/to/file.ext"));
        // Non-absolute
        Assert.False(TsPath.PathIsAbsolute("path/to/file.ext"));
        Assert.False(TsPath.PathIsAbsolute("./path/to/file.ext"));
    }

    [Fact]
    public void IsUrl()
    {
        Assert.False(TsPath.IsUrl("a"));
        Assert.False(TsPath.IsUrl("/"));
        Assert.False(TsPath.IsUrl("c:"));
        Assert.False(TsPath.IsUrl("c:d"));
        Assert.False(TsPath.IsUrl("c:/"));
        Assert.False(TsPath.IsUrl("c:\\"));
        Assert.False(TsPath.IsUrl("//server"));
        Assert.False(TsPath.IsUrl("//server/share"));
        Assert.False(TsPath.IsUrl("\\\\server"));
        Assert.False(TsPath.IsUrl("\\\\server\\share"));

        Assert.True(TsPath.IsUrl("file:///path"));
        Assert.True(TsPath.IsUrl("file:///c:"));
        Assert.True(TsPath.IsUrl("file:///c:d"));
        Assert.True(TsPath.IsUrl("file:///c:/path"));
        Assert.True(TsPath.IsUrl("file://server"));
        Assert.True(TsPath.IsUrl("file://server/path"));
        Assert.True(TsPath.IsUrl("http://server"));
        Assert.True(TsPath.IsUrl("http://server/path"));
    }

    [Fact]
    public void IsRootedDiskPath()
    {
        Assert.False(TsPath.IsRootedDiskPath("a"));
        Assert.True(TsPath.IsRootedDiskPath("/"));
        Assert.True(TsPath.IsRootedDiskPath("c:"));
        Assert.False(TsPath.IsRootedDiskPath("c:d"));
        Assert.True(TsPath.IsRootedDiskPath("c:/"));
        Assert.True(TsPath.IsRootedDiskPath("c:\\"));
        Assert.True(TsPath.IsRootedDiskPath("//server"));
        Assert.True(TsPath.IsRootedDiskPath("//server/share"));
        Assert.True(TsPath.IsRootedDiskPath("\\\\server"));
        Assert.True(TsPath.IsRootedDiskPath("\\\\server\\share"));
        Assert.False(TsPath.IsRootedDiskPath("file:///path"));
        Assert.False(TsPath.IsRootedDiskPath("file:///c:"));
        Assert.False(TsPath.IsRootedDiskPath("file:///c:d"));
        Assert.False(TsPath.IsRootedDiskPath("file:///c:/path"));
        Assert.False(TsPath.IsRootedDiskPath("file://server"));
        Assert.False(TsPath.IsRootedDiskPath("file://server/path"));
        Assert.False(TsPath.IsRootedDiskPath("http://server"));
        Assert.False(TsPath.IsRootedDiskPath("http://server/path"));
    }

    [Fact]
    public void GetDirectoryPath()
    {
        Assert.Equal("", TsPath.GetDirectoryPath(""));
        Assert.Equal("", TsPath.GetDirectoryPath("a"));
        Assert.Equal("a", TsPath.GetDirectoryPath("a/b"));
        Assert.Equal("/", TsPath.GetDirectoryPath("/"));
        Assert.Equal("/", TsPath.GetDirectoryPath("/a"));
        Assert.Equal("/", TsPath.GetDirectoryPath("/a/"));
        Assert.Equal("/a", TsPath.GetDirectoryPath("/a/b"));
        Assert.Equal("/a", TsPath.GetDirectoryPath("/a/b/"));
        Assert.Equal("c:", TsPath.GetDirectoryPath("c:"));
        Assert.Equal("", TsPath.GetDirectoryPath("c:d"));
        Assert.Equal("c:/", TsPath.GetDirectoryPath("c:/"));
        Assert.Equal("c:/", TsPath.GetDirectoryPath("c:/path"));
        Assert.Equal("c:/", TsPath.GetDirectoryPath("c:/path/"));
        Assert.Equal("//server", TsPath.GetDirectoryPath("//server"));
        Assert.Equal("//server/", TsPath.GetDirectoryPath("//server/"));
        Assert.Equal("//server/", TsPath.GetDirectoryPath("//server/share"));
        Assert.Equal("//server/", TsPath.GetDirectoryPath("//server/share/"));
        Assert.Equal("//server", TsPath.GetDirectoryPath("\\\\server"));
        Assert.Equal("//server/", TsPath.GetDirectoryPath("\\\\server\\"));
        Assert.Equal("//server/", TsPath.GetDirectoryPath("\\\\server\\share"));
        Assert.Equal("//server/", TsPath.GetDirectoryPath("\\\\server\\share\\"));
        Assert.Equal("file:///", TsPath.GetDirectoryPath("file:///"));
        Assert.Equal("file:///", TsPath.GetDirectoryPath("file:///path"));
        Assert.Equal("file:///", TsPath.GetDirectoryPath("file:///path/"));
        Assert.Equal("file:///c:", TsPath.GetDirectoryPath("file:///c:"));
        Assert.Equal("file:///", TsPath.GetDirectoryPath("file:///c:d"));
        Assert.Equal("file:///c:/", TsPath.GetDirectoryPath("file:///c:/"));
        Assert.Equal("file:///c:/", TsPath.GetDirectoryPath("file:///c:/path"));
        Assert.Equal("file:///c:/", TsPath.GetDirectoryPath("file:///c:/path/"));
        Assert.Equal("file://server", TsPath.GetDirectoryPath("file://server"));
        Assert.Equal("file://server/", TsPath.GetDirectoryPath("file://server/"));
        Assert.Equal("file://server/", TsPath.GetDirectoryPath("file://server/path"));
        Assert.Equal("file://server/", TsPath.GetDirectoryPath("file://server/path/"));
        Assert.Equal("http://server", TsPath.GetDirectoryPath("http://server"));
        Assert.Equal("http://server/", TsPath.GetDirectoryPath("http://server/"));
        Assert.Equal("http://server/", TsPath.GetDirectoryPath("http://server/path"));
        Assert.Equal("http://server/", TsPath.GetDirectoryPath("http://server/path/"));
    }

    // !!!
    // getBaseFileName
    // getAnyExtensionFromPath

    [Fact]
    public void GetPathComponents()
    {
        AssertDeepEqual([""], TsPath.GetPathComponents("", ""));
        AssertDeepEqual(["", "a"], TsPath.GetPathComponents("a", ""));
        AssertDeepEqual(["", ".", "a"], TsPath.GetPathComponents("./a", ""));
        AssertDeepEqual(["/"], TsPath.GetPathComponents("/", ""));
        AssertDeepEqual(["/", "a"], TsPath.GetPathComponents("/a", ""));
        AssertDeepEqual(["/", "a"], TsPath.GetPathComponents("/a/", ""));
        AssertDeepEqual(["c:"], TsPath.GetPathComponents("c:", ""));
        AssertDeepEqual(["", "c:d"], TsPath.GetPathComponents("c:d", ""));
        AssertDeepEqual(["c:/"], TsPath.GetPathComponents("c:/", ""));
        AssertDeepEqual(["c:/", "path"], TsPath.GetPathComponents("c:/path", ""));
        AssertDeepEqual(["//server"], TsPath.GetPathComponents("//server", ""));
        AssertDeepEqual(["//server/"], TsPath.GetPathComponents("//server/", ""));
        AssertDeepEqual(["//server/", "share"], TsPath.GetPathComponents("//server/share", ""));
        AssertDeepEqual(["file:///"], TsPath.GetPathComponents("file:///", ""));
        AssertDeepEqual(["file:///", "path"], TsPath.GetPathComponents("file:///path", ""));
        AssertDeepEqual(["file:///c:"], TsPath.GetPathComponents("file:///c:", ""));
        AssertDeepEqual(["file:///", "c:d"], TsPath.GetPathComponents("file:///c:d", ""));
        AssertDeepEqual(["file:///c:/"], TsPath.GetPathComponents("file:///c:/", ""));
        AssertDeepEqual(["file:///c:/", "path"], TsPath.GetPathComponents("file:///c:/path", ""));
        AssertDeepEqual(["file://server"], TsPath.GetPathComponents("file://server", ""));
        AssertDeepEqual(["file://server/"], TsPath.GetPathComponents("file://server/", ""));
        AssertDeepEqual(["file://server/", "path"], TsPath.GetPathComponents("file://server/path", ""));
        AssertDeepEqual(["http://server"], TsPath.GetPathComponents("http://server", ""));
        AssertDeepEqual(["http://server/"], TsPath.GetPathComponents("http://server/", ""));
        AssertDeepEqual(["http://server/", "path"], TsPath.GetPathComponents("http://server/path", ""));
    }

    [Fact]
    public void ReducePathComponents()
    {
        AssertDeepEqual([""], TsPath.ReducePathComponents([""]));
        AssertDeepEqual([""], TsPath.ReducePathComponents(["", "."]));
        AssertDeepEqual(["", "a"], TsPath.ReducePathComponents(["", ".", "a"]));
        AssertDeepEqual(["", "a"], TsPath.ReducePathComponents(["", "a", "."]));
        AssertDeepEqual(["", ".."], TsPath.ReducePathComponents(["", ".."]));
        AssertDeepEqual(["", "..", ".."], TsPath.ReducePathComponents(["", "..", ".."]));
        AssertDeepEqual(["", "..", ".."], TsPath.ReducePathComponents(["", "..", ".", ".."]));
        AssertDeepEqual([""], TsPath.ReducePathComponents(["", "a", ".."]));
        AssertDeepEqual(["", "..", "a"], TsPath.ReducePathComponents(["", "..", "a"]));
        AssertDeepEqual(["/"], TsPath.ReducePathComponents(["/"]));
        AssertDeepEqual(["/"], TsPath.ReducePathComponents(["/", "."]));
        AssertDeepEqual(["/"], TsPath.ReducePathComponents(["/", ".."]));
        AssertDeepEqual(["/"], TsPath.ReducePathComponents(["/", "a", ".."]));
    }

    [Fact]
    public void CombinePaths()
    {
        // Non-rooted
        Assert.Equal("path/to/file.ext", TsPath.CombinePaths("path", "to", "file.ext"));
        Assert.Equal("path/dir/../to/file.ext", TsPath.CombinePaths("path", "dir", "..", "to", "file.ext"));
        // POSIX
        Assert.Equal("/path/to/file.ext", TsPath.CombinePaths("/path", "to", "file.ext"));
        Assert.Equal("/to/file.ext", TsPath.CombinePaths("/path", "/to", "file.ext"));
        // DOS
        Assert.Equal("c:/path/to/file.ext", TsPath.CombinePaths("c:/path", "to", "file.ext"));
        Assert.Equal("c:/to/file.ext", TsPath.CombinePaths("c:/path", "c:/to", "file.ext"));
        // URL
        Assert.Equal("file:///path/to/file.ext", TsPath.CombinePaths("file:///path", "to", "file.ext"));
        Assert.Equal("file:///to/file.ext", TsPath.CombinePaths("file:///path", "file:///to", "file.ext"));

        Assert.Equal("/node_modules/@types", TsPath.CombinePaths("/", "/node_modules/@types"));
        Assert.Equal("/a/..", TsPath.CombinePaths("/a/..", ""));
        Assert.Equal("/a/../b", TsPath.CombinePaths("/a/..", "b"));
        Assert.Equal("/a/../b/", TsPath.CombinePaths("/a/..", "b/"));
        Assert.Equal("/", TsPath.CombinePaths("/a/..", "/"));
        Assert.Equal("/b", TsPath.CombinePaths("/a/..", "/b"));
    }

    [Fact]
    public void ResolvePath()
    {
        Assert.Equal("", TsPath.ResolvePath(""));
        Assert.Equal("", TsPath.ResolvePath("."));
        Assert.Equal("", TsPath.ResolvePath("./"));
        Assert.Equal("..", TsPath.ResolvePath(".."));
        Assert.Equal("../", TsPath.ResolvePath("../"));
        Assert.Equal("/", TsPath.ResolvePath("/"));
        Assert.Equal("/", TsPath.ResolvePath("/."));
        Assert.Equal("/", TsPath.ResolvePath("/./"));
        Assert.Equal("/", TsPath.ResolvePath("/../"));
        Assert.Equal("/a", TsPath.ResolvePath("/a"));
        Assert.Equal("/a/", TsPath.ResolvePath("/a/"));
        Assert.Equal("/a", TsPath.ResolvePath("/a/."));
        Assert.Equal("/a/", TsPath.ResolvePath("/a/./"));
        Assert.Equal("/a/b", TsPath.ResolvePath("/a/./b"));
        Assert.Equal("/a/b/", TsPath.ResolvePath("/a/./b/"));
        Assert.Equal("/", TsPath.ResolvePath("/a/.."));
        Assert.Equal("/", TsPath.ResolvePath("/a/../"));
        Assert.Equal("/b", TsPath.ResolvePath("/a/../b"));
        Assert.Equal("/b/", TsPath.ResolvePath("/a/../b/"));
        Assert.Equal("/b", TsPath.ResolvePath("/a/..", "b"));
        Assert.Equal("/", TsPath.ResolvePath("/a/..", "/"));
        Assert.Equal("/b/", TsPath.ResolvePath("/a/..", "b/"));
        Assert.Equal("/b", TsPath.ResolvePath("/a/..", "/b"));
        Assert.Equal("/a/b", TsPath.ResolvePath("/a/.", "b"));
        Assert.Equal("/a", TsPath.ResolvePath("/a/.", "."));
        Assert.Equal("a/b/c", TsPath.ResolvePath("a", "b", "c"));
        Assert.Equal("/c", TsPath.ResolvePath("a", "b", "/c"));
        Assert.Equal("a/c", TsPath.ResolvePath("a", "b", "../c"));
    }

    [Fact]
    public void GetNormalizedAbsolutePath()
    {
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/.", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/./", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/../", ""));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a", ""));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a/", ""));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a/.", ""));
        Assert.Equal("/a/foo.", TsPath.GetNormalizedAbsolutePath("/a/foo.", ""));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a/./", ""));
        Assert.Equal("/a/b", TsPath.GetNormalizedAbsolutePath("/a/./b", ""));
        Assert.Equal("/a/b", TsPath.GetNormalizedAbsolutePath("/a/./b/", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/..", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/../", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/../", ""));
        Assert.Equal("/b", TsPath.GetNormalizedAbsolutePath("/a/../b", ""));
        Assert.Equal("/b", TsPath.GetNormalizedAbsolutePath("/a/../b/", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/..", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/..", "/"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/..", "b/"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/a/..", "/b"));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a/.", "b"));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a/.", "."));

        // Tests as above, but with backslashes.
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\.", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\.\\", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\..\\", ""));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("\\a\\.\\", ""));
        Assert.Equal("/a/b", TsPath.GetNormalizedAbsolutePath("\\a\\.\\b", ""));
        Assert.Equal("/a/b", TsPath.GetNormalizedAbsolutePath("\\a\\.\\b\\", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..\\", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..\\", ""));
        Assert.Equal("/b", TsPath.GetNormalizedAbsolutePath("\\a\\..\\b", ""));
        Assert.Equal("/b", TsPath.GetNormalizedAbsolutePath("\\a\\..\\b\\", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..", "\\"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..", "b\\"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("\\a\\..", "\\b"));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("\\a\\.", "b"));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("\\a\\.", "."));

        // Relative paths on an empty currentDirectory.
        Assert.Equal("", TsPath.GetNormalizedAbsolutePath("", ""));
        Assert.Equal("", TsPath.GetNormalizedAbsolutePath(".", ""));
        Assert.Equal("", TsPath.GetNormalizedAbsolutePath("./", ""));
        // Strangely, these do not normalize to the empty string.
        Assert.Equal("..", TsPath.GetNormalizedAbsolutePath("..", ""));
        Assert.Equal("..", TsPath.GetNormalizedAbsolutePath("../", ""));

        // Interaction between relative paths and currentDirectory.
        Assert.Equal("/home", TsPath.GetNormalizedAbsolutePath("", "/home"));
        Assert.Equal("/home", TsPath.GetNormalizedAbsolutePath(".", "/home"));
        Assert.Equal("/home", TsPath.GetNormalizedAbsolutePath("./", "/home"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("..", "/home"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("../", "/home"));
        Assert.Equal("b/a", TsPath.GetNormalizedAbsolutePath("a", "b"));
        Assert.Equal("b/c/a", TsPath.GetNormalizedAbsolutePath("a", "b/c"));

        // Base names starting or ending with a dot do not affect normalization.
        Assert.Equal(".a", TsPath.GetNormalizedAbsolutePath(".a", ""));
        Assert.Equal("..a", TsPath.GetNormalizedAbsolutePath("..a", ""));
        Assert.Equal("a.", TsPath.GetNormalizedAbsolutePath("a.", ""));
        Assert.Equal("a..", TsPath.GetNormalizedAbsolutePath("a..", ""));

        Assert.Equal("/base/.a", TsPath.GetNormalizedAbsolutePath("/base/./.a", ""));
        Assert.Equal("/.a", TsPath.GetNormalizedAbsolutePath("/base/../.a", ""));
        Assert.Equal("/base/..a", TsPath.GetNormalizedAbsolutePath("/base/./..a", ""));
        Assert.Equal("/..a", TsPath.GetNormalizedAbsolutePath("/base/../..a", ""));
        Assert.Equal("/base/..a/b", TsPath.GetNormalizedAbsolutePath("/base/./..a/b", ""));
        Assert.Equal("/..a/b", TsPath.GetNormalizedAbsolutePath("/base/../..a/b", ""));

        Assert.Equal("/base/a.", TsPath.GetNormalizedAbsolutePath("/base/./a.", ""));
        Assert.Equal("/a.", TsPath.GetNormalizedAbsolutePath("/base/../a.", ""));
        Assert.Equal("/base/a..", TsPath.GetNormalizedAbsolutePath("/base/./a..", ""));
        Assert.Equal("/a..", TsPath.GetNormalizedAbsolutePath("/base/../a..", ""));
        Assert.Equal("/base/a../b", TsPath.GetNormalizedAbsolutePath("/base/./a../b", ""));
        Assert.Equal("/a../b", TsPath.GetNormalizedAbsolutePath("/base/../a../b", ""));

        Assert.Equal("", TsPath.GetNormalizedAbsolutePath("a/..", ""));
        Assert.Equal("/a", TsPath.GetNormalizedAbsolutePath("/a//", ""));
        Assert.Equal("//a/", TsPath.GetNormalizedAbsolutePath("//a", "a"));
        Assert.Equal("//", TsPath.GetNormalizedAbsolutePath("/\\", ""));
        Assert.Equal("a/a", TsPath.GetNormalizedAbsolutePath("a///", "a"));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("/.//", ""));
        Assert.Equal("///", TsPath.GetNormalizedAbsolutePath("//\\\\", ""));
        Assert.Equal("a", TsPath.GetNormalizedAbsolutePath(".//a", "."));
        Assert.Equal("..", TsPath.GetNormalizedAbsolutePath("a/../..", ""));
        Assert.Equal("/", TsPath.GetNormalizedAbsolutePath("../..", "\\a"));
        Assert.Equal("a:/", TsPath.GetNormalizedAbsolutePath("a:", "b"));
        Assert.Equal("../..", TsPath.GetNormalizedAbsolutePath("a/../..", ".."));
        Assert.Equal("", TsPath.GetNormalizedAbsolutePath("a/../..", "b"));
        Assert.Equal("../..", TsPath.GetNormalizedAbsolutePath("a//../..", ".."));

        // Consecutive intermediate slashes are normalized to a single slash.
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a//b", ""));
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a///b", ""));
        Assert.Equal("a/b/c", TsPath.GetNormalizedAbsolutePath("a/b//c", ""));
        Assert.Equal("/a/b/c", TsPath.GetNormalizedAbsolutePath("/a/b//c", ""));
        Assert.Equal("//a/b/c", TsPath.GetNormalizedAbsolutePath("//a/b//c", ""));

        // Backslashes are converted to slashes,
        // and then consecutive intermediate slashes are normalized to a single slash
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a\\\\b", ""));
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a\\\\\\b", ""));
        Assert.Equal("a/b/c", TsPath.GetNormalizedAbsolutePath("a\\b\\\\c", ""));
        Assert.Equal("/a/b/c", TsPath.GetNormalizedAbsolutePath("\\a\\b\\\\c", ""));
        Assert.Equal("//a/b/c", TsPath.GetNormalizedAbsolutePath("\\\\a\\b\\\\c", ""));

        // The same occurs for mixed slashes.
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a/\\b", ""));
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a\\/b", ""));
        Assert.Equal("a/b", TsPath.GetNormalizedAbsolutePath("a\\/\\b", ""));
        Assert.Equal("a/b/c", TsPath.GetNormalizedAbsolutePath("a\\b//c", ""));
        Assert.Equal("/a/b/c", TsPath.GetNormalizedAbsolutePath("\\a\\b\\\\c", ""));
        Assert.Equal("//a/b/c", TsPath.GetNormalizedAbsolutePath("\\\\a\\b\\\\c", ""));
    }

    [Fact]
    public void GetNormalizedAbsolutePathWithoutRoot()
    {
        Assert.Equal("a/b/c.txt", TsPath.GetNormalizedAbsolutePathWithoutRoot("/a/b/c.txt", "/a/b"));
        Assert.Equal("work/hello.txt", TsPath.GetNormalizedAbsolutePathWithoutRoot("c:/work/hello.txt", "c:/work"));
        Assert.Equal("work/hello.txt", TsPath.GetNormalizedAbsolutePathWithoutRoot("c:/work/hello.txt", "d:/worspaces"));
    }

    [Fact]
    public void GetRelativePathToDirectoryOrUrl()
    {
        // !!!
        // Based on tests for `getRelativePathFromDirectory`.

        Assert.Equal("", TsPath.GetRelativePathToDirectoryOrUrl("/", "/", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("", TsPath.GetRelativePathToDirectoryOrUrl("/a", "/a", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("", TsPath.GetRelativePathToDirectoryOrUrl("/a/", "/a", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("..", TsPath.GetRelativePathToDirectoryOrUrl("/a", "/", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../b", TsPath.GetRelativePathToDirectoryOrUrl("/a", "/b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../../b", TsPath.GetRelativePathToDirectoryOrUrl("/a/b", "/b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../../../b", TsPath.GetRelativePathToDirectoryOrUrl("/a/b/c", "/b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../../../b/c", TsPath.GetRelativePathToDirectoryOrUrl("/a/b/c", "/b/c", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("..", TsPath.GetRelativePathToDirectoryOrUrl("/a/b/c", "/a/b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("d:/", TsPath.GetRelativePathToDirectoryOrUrl("c:", "d:", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("", TsPath.GetRelativePathToDirectoryOrUrl("file:///", "file:///", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("", TsPath.GetRelativePathToDirectoryOrUrl("file:///a", "file:///a", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("", TsPath.GetRelativePathToDirectoryOrUrl("file:///a/", "file:///a", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("..", TsPath.GetRelativePathToDirectoryOrUrl("file:///a", "file:///", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../b", TsPath.GetRelativePathToDirectoryOrUrl("file:///a", "file:///b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../../b", TsPath.GetRelativePathToDirectoryOrUrl("file:///a/b", "file:///b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../../../b", TsPath.GetRelativePathToDirectoryOrUrl("file:///a/b/c", "file:///b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("../../../b/c", TsPath.GetRelativePathToDirectoryOrUrl("file:///a/b/c", "file:///b/c", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("..", TsPath.GetRelativePathToDirectoryOrUrl("file:///a/b/c", "file:///a/b", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
        Assert.Equal("file:///d:/", TsPath.GetRelativePathToDirectoryOrUrl("file:///c:", "file:///d:", false /*isAbsolutePathAnUrl*/, new ComparePathsOptions()));
    }

    [Fact]
    public void ToFileNameLowerCase()
    {
        Assert.Equal("/user/username/projects/project/file.ts", TsPath.ToFileNameLowerCase("/user/UserName/projects/Project/file.ts"));
        Assert.Equal("/user/username/projects/projectß/file.ts", TsPath.ToFileNameLowerCase("/user/UserName/projects/projectß/file.ts"));
        Assert.Equal("/user/username/projects/İproject/file.ts", TsPath.ToFileNameLowerCase("/user/UserName/projects/İproject/file.ts"));
        Assert.Equal("/user/username/projects/ı/file.ts", TsPath.ToFileNameLowerCase("/user/UserName/projects/ı/file.ts"));
    }

    [Fact]
    public void ToPath()
    {
        Assert.Equal("path/to/file.ext", TsPath.ToPath("file.ext", "path/to", false /*useCaseSensitiveFileNames*/).Value);
        Assert.Equal("/path/to/file.ext", TsPath.ToPath("file.ext", "/path/to", true /*useCaseSensitiveFileNames*/).Value);
        Assert.Equal("/path/file.ext", TsPath.ToPath("/path/to/../file.ext", "path/to", true /*useCaseSensitiveFileNames*/).Value);
    }

    // Go: pathIsRelativeTests + the init() at path_test.go:649, which doubles the table
    // with backslash variants (05 §16.1: init-time doubling happens inside the data method).
    // The `benchmark` column only feeds BenchmarkPathIsRelative and is dropped.
    public static TheoryData<string, bool> PathIsRelativeData()
    {
        (string P, bool IsRelative)[] tests =
        [
            // relative
            (".", true),
            ("..", true),
            ("./", true),
            ("../", true),
            ("./foo/bar", true),
            ("../foo/bar", true),
            ("../" + Repeat("foo/", 100), true),
            // non-relative
            ("", false),
            ("foo", false),
            ("foo/bar", false),
            ("/foo/bar", false),
            ("c:/foo/bar", false),
        ];
        // Slash-free rows double into identical rows in Go (run twice, harmlessly);
        // xUnit skips duplicate-ID cases, so deduplicate for identical coverage.
        var data = new TheoryData<string, bool>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var (p, isRelative) in tests)
        {
            if (seen.Add(p))
            {
                data.Add(p, isRelative);
            }
        }
        foreach (var (p, isRelative) in tests)
        {
            string bp = p.Replace("/", "\\", System.StringComparison.Ordinal);
            if (seen.Add(bp))
            {
                data.Add(bp, isRelative);
            }
        }
        return data;
    }

    // Go: strings.Repeat
    private static string Repeat(string s, int count)
    {
        var sb = new System.Text.StringBuilder(s.Length * count);
        for (int i = 0; i < count; i++)
        {
            sb.Append(s);
        }
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(PathIsRelativeData))]
    public void PathIsRelative(string p, bool isRelative)
    {
        Assert.Equal(isRelative, TsPath.PathIsRelative(p));
    }

    private static readonly ComparePathsOptions Opts = new(); // Go: opts := ComparePathsOptions{}

    [Fact]
    public void GetCommonParents_EmptyInput()
    {
        string[]? paths = null;
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.Null(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Null(got); // Go: assert.DeepEqual(t, got, ([]string)(nil))
    }

    [Fact]
    public void GetCommonParents_SinglePathReturnsItself()
    {
        string[] paths = ["/a/b/c/d"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.Null(ignored); // Go: assert.Equal(t, len(ignored), 0)
        string[] expected = [paths[0]];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_PathsShorterThanMinComponentsAreIgnored()
    {
        string[] paths = ["/a/b/c/d", "/a/b/c/e", "/a/b/f/g", "/x/y"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 4, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored);
        Assert.True(ignored.SetEquals(["/x/y"])); // Go: assert.DeepEqual(t, ignored, map[string]struct{}{"/x/y": {}})
        string[] expected = ["/a/b/c", "/a/b/f/g"];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_ThreePathsShareAB()
    {
        string[] paths = ["/a/b/c/d", "/a/b/c/e", "/a/b/f/g"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = ["/a/b"];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_MixedWithShortPathCollapsesToRootWhenMinComponents1()
    {
        string[] paths = ["/a/b/c/d", "/a/b/c/e", "/a/b/f/g", "/x/y/z"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = ["/"];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_MixedWithShortPathPreservesBothWhenMinComponents3()
    {
        string[] paths = ["/a/b/c/d", "/a/b/c/e", "/a/b/f/g", "/x/y/z"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 3, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = ["/a/b", "/x/y/z"];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_DifferentVolumesAreReturnedIndividually()
    {
        string[] paths = ["c:/a/b/c/d", "d:/a/b/c/d"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = [paths[0], paths[1]];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_DuplicatePathsDeduplicateResult()
    {
        string[] paths = ["/a/b/c/d", "/a/b/c/d"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = [paths[0]];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_PathsWithFewComponentsAreReturnedAsIsWhenMinComponentsMet()
    {
        string[] paths = ["/a/b/c/d", "/x/y"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 2, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = ["/a/b/c/d", "/x/y"];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_MinComponents2()
    {
        string[] paths = ["/a/b/c/d", "/a/z/c/e", "/a/aaa/f/g", "/x/y/z"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 2, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = ["/a", "/x/y/z"];
        Assert.Equal(expected, got);
    }

    [Fact]
    public void GetCommonParents_TrailingSeparatorsAreHandled()
    {
        string[] paths = ["/a/b/", "/a/b/c"];
        var (got, ignored) = TsPath.GetCommonParents(paths, 1, TsPath.GetPathComponents, Opts);
        Assert.NotNull(ignored); // Go: assert.Equal(t, len(ignored), 0)
        Assert.Empty(ignored);
        string[] expected = ["/a/b"];
        Assert.Equal(expected, got);
    }
}
