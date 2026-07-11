// Ported from tsgo internal/symlinks/knownsymlinks_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// PORT-NOTE (05 §16.1): t.Parallel() dropped (assembly-level parallelism);
// BenchmarkSetSymlinksFromResolutions (knownsymlinks_bench_test.go) moves to
// BenchmarkDotNet in phase 10. Go goroutine thread-safety test → Parallel.For.

using System;
using System.Threading.Tasks;
using Ts.Ast;
using Ts.Core;
using Ts.Module;
using Ts.TsPath;
using Xunit;

namespace Ts.Symlinks.Tests;

public class KnownSymlinksTests
{
    [Fact]
    public void NewKnownSymlink()
    {
        var cache = new KnownSymlinks("/test/dir", true);
        Assert.NotNull(cache);
        Assert.Equal("/test/dir", cache.cwd);
        Assert.True(cache.useCaseSensitiveFileNames);
    }

    [Fact]
    public void SetDirectory()
    {
        var cache = new KnownSymlinks("/test/dir", true);
        Path symlinkPath = global::Ts.TsPath.TsPath.ToPath("/test/symlink", "/test/dir", true).EnsureTrailingDirectorySeparator();
        var realDirectory = new KnownDirectoryLink
        {
            Real = "/real/path/",
            RealPath = global::Ts.TsPath.TsPath.ToPath("/real/path", "/test/dir", true).EnsureTrailingDirectorySeparator(),
        };

        cache.SetDirectory("/test/symlink", symlinkPath, realDirectory);

        // Check that directory was stored
        Assert.True(cache.Directories().TryLoad(symlinkPath, out var stored));
        Assert.NotNull(stored);
        Assert.Equal(realDirectory.Real, stored!.Real);
        Assert.Equal(realDirectory.RealPath, stored.RealPath);

        // Check that realpath mapping was created
        Assert.True(cache.DirectoriesByRealpath().TryLoad(realDirectory.RealPath, out var set));
        Assert.True(set!.Size() != 0);
        Assert.True(set.Has("/test/symlink"));
    }

    [Fact]
    public void SetFile()
    {
        var cache = new KnownSymlinks("/test/dir", true);
        string symlink = "/test/symlink/file.ts";
        Path symlinkPath = global::Ts.TsPath.TsPath.ToPath(symlink, "/test/dir", true);
        string realpath = "/real/path/file.ts";

        cache.SetFile(symlink, symlinkPath, realpath);

        Assert.True(cache.Files().TryLoad(symlinkPath, out var stored));
        Assert.Equal(realpath, stored);
    }

    [Fact]
    public void ProcessResolution()
    {
        var cache = new KnownSymlinks("/test/dir", true);

        // Test with empty paths
        cache.ProcessResolution("", "");
        cache.ProcessResolution("original", "");
        cache.ProcessResolution("", "resolved");

        // Test with valid paths
        string originalPath = "/test/original/file.ts";
        string resolvedPath = "/test/resolved/file.ts";
        cache.ProcessResolution(originalPath, resolvedPath);

        // Check that file was stored
        Path symlinkPath = global::Ts.TsPath.TsPath.ToPath(originalPath, "/test/dir", true);
        Assert.True(cache.Files().TryLoad(symlinkPath, out var stored));
        Assert.Equal(resolvedPath, stored);
    }

    [Theory]
    [InlineData("identical paths", "/test/path/file.ts", "/test/path/file.ts", "/", "/")]
    [InlineData("different files same directory", "/test/path/file1.ts", "/test/path/file2.ts", "", "")]
    [InlineData("different directories", "/test/path1/file.ts", "/test/path2/file.ts", "/test/path1", "/test/path2")]
    [InlineData("node_modules paths", "/test/node_modules/pkg/file.ts", "/test/node_modules/pkg/file.ts", "/test/node_modules/pkg", "/test/node_modules/pkg")]
    [InlineData("scoped package paths", "/test/node_modules/@scope/pkg/file.ts", "/test/node_modules/@scope/pkg/file.ts", "/test/node_modules/@scope/pkg", "/test/node_modules/@scope/pkg")]
    public void GuessDirectorySymlink(string name, string a, string b, string expectedResolved, string expectedOriginal)
    {
        _ = name;
        var cache = new KnownSymlinks("/test/dir", true);
        var (commonResolved, commonOriginal) = cache.GuessDirectorySymlink(a, b, "/test/dir");
        Assert.Equal(expectedResolved, commonResolved);
        Assert.Equal(expectedOriginal, commonOriginal);
    }

    [Theory]
    [InlineData("node_modules", true)]
    [InlineData("@scope", true)]
    [InlineData("src", false)]
    [InlineData("", false)]
    [InlineData("NODE_MODULES", false)] // The function is case sensitive
    [InlineData("@SCOPE", true)]
    public void IsNodeModulesOrScopedPackageDirectory(string dir, bool expected)
    {
        var cache = new KnownSymlinks("/test/dir", true);
        Assert.Equal(expected, cache.IsNodeModulesOrScopedPackageDirectory(dir));
    }

    [Fact]
    public void SetSymlinksFromResolutions()
    {
        var cache = new KnownSymlinks("/test/dir", true);

        // Mock resolution data
        var resolvedModules = new[]
        {
            (originalPath: "/test/original/file1.ts", resolvedPath: "/test/resolved/file1.ts"),
            (originalPath: "/test/original/file2.ts", resolvedPath: "/test/resolved/file2.ts"),
        };

        // Mock callbacks. Go's core.ResolutionMode is a type alias for ModuleKind; the
        // C# `ResolutionMode` global-using alias is not shared with the test assembly, so
        // the compiled delegate signature uses the underlying ModuleKind here.
        void ForEachResolvedModule(Action<ResolvedModule, string, ModuleKind, Path> callback, SourceFile? file)
        {
            foreach (var res in resolvedModules)
            {
                var resolution = new ResolvedModule
                {
                    OriginalPath = res.originalPath,
                    ResolvedFileName = res.resolvedPath,
                };
                callback(resolution, "module", ModuleKind.None, global::Ts.TsPath.TsPath.ToPath("/test/source.ts", "/test/dir", true));
            }
        }

        void ForEachResolvedTypeReferenceDirective(Action<ResolvedTypeReferenceDirective, string, ModuleKind, Path> callback, SourceFile? file)
        {
            // No type reference directives for this test
        }

        cache.SetSymlinksFromResolutions(ForEachResolvedModule, ForEachResolvedTypeReferenceDirective);

        // Check that files were stored
        foreach (var res in resolvedModules)
        {
            Path symlinkPath = global::Ts.TsPath.TsPath.ToPath(res.originalPath, "/test/dir", true);
            Assert.True(cache.Files().TryLoad(symlinkPath, out var stored));
            Assert.Equal(res.resolvedPath, stored);
        }
    }

    [Fact]
    public void KnownSymlinksThreadSafety()
    {
        var cache = new KnownSymlinks("/test/dir", true);

        // Test concurrent access
        Parallel.For(0, 10, id =>
        {
            string suffix = ((char)id).ToString();
            Path symlinkPath = global::Ts.TsPath.TsPath.ToPath("/test/symlink" + suffix, "/test/dir", true).EnsureTrailingDirectorySeparator();
            var realDirectory = new KnownDirectoryLink
            {
                Real = "/real/path" + suffix + "/",
                RealPath = global::Ts.TsPath.TsPath.ToPath("/real/path" + suffix, "/test/dir", true).EnsureTrailingDirectorySeparator(),
            };

            cache.SetDirectory("/test/symlink" + suffix, symlinkPath, realDirectory);

            // Read back
            Assert.True(cache.Directories().TryLoad(symlinkPath, out var stored));
            Assert.NotNull(stored);
            Assert.Equal(realDirectory.Real, stored!.Real);
        });

        // Verify all directories were stored
        Assert.Equal(10, cache.Directories().Size());
    }
}
