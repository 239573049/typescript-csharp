// Ported from tsgo internal/bundled/bundled_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTEs:
//  * Go's bundled.TestingLibPath() returns the on-disk source directory of the bundled libs
//    (runtime.Caller-based, bundled.go:41-50). The C# build has no noembed variant, so the
//    shipped lib directory is the repo path (07-aot-constraints.md §5.2; the csproj
//    EmbeddedResource glob is src/Ts.Compiler/Bundled/libs/**/*.d.ts).
//  * Go's fs.WalkDir callback signature (path, d, err) error → C# WalkDirFunc returning
//    Exception? null=continue (Vfs.cs:167-171).
//  * assert.NilError(t, err) → dropped: TryReadFile/throws-as-error conventions make a failed
//    operation fail the test at the same point (05-conventions.md §16.3).

using System;
using System.Collections.Generic;
using System.IO;
using Ts.Bundled;
using Ts.Vfs;
using Ts.Vfs.OsVfs;
using Xunit;

using BundledApi = global::Ts.Bundled.Bundled;
using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.Bundled.Tests;

public class BundledTests
{
    // bundled_test.go:15-27 — TestTestingLibPath.
    [Fact]
    public void TestingLibPath()
    {
        string p = SourceLibPath();

        Assert.True(Directory.Exists(p));

        string libdts = Path.Combine(p, "lib.d.ts");

        Assert.True(File.Exists(libdts));
    }

    // bundled_test.go:29-48 — TestEmbeddedLibs.
    [Fact]
    public void EmbeddedLibs()
    {
        IFileSystem fs = BundledApi.WrapFS(OsVfsUtil.FS());

        var files = new List<string>();

        Exception? err = fs.WalkDir(BundledApi.LibPath(), (path, d, walkErr) =>
        {
            if (walkErr != null)
            {
                return walkErr;
            }
            if (!d!.IsDir())
            {
                files.Add(TsPathApi.GetBaseFileName(path));
            }
            return null;
        });
        Assert.Null(err);

        Assert.Equal(BundledApi.LibNames, files);
    }

    // bundled.go:41-50 — testingLibPath: the source directory of the bundled libs
    // (Go: runtime.Caller of bundled.go → filepath.Join(dir, "libs")).
    private static string SourceLibPath() =>
        TsPathApi.NormalizeSlashes(Path.Combine(TestProjectBundleDir(), "libs"));

    // The test assembly sits at tests/Ts.Compiler.Tests/bin/<config>/<tfm>/; walk up to the
    // repository root and into the shipping project. PORT-NOTE: the root is located by the
    // TypeScript.slnx marker (the same approach as Ts.TestUtil.RepoPaths), kept local so this
    // test project does not need Ts.TestUtil for it.
    private static string TestProjectBundleDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "TypeScript.slnx")))
            {
                return Path.Combine(dir, "src", "Ts.Compiler", "Bundled");
            }
            dir = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir));
        }
        throw new InvalidOperationException($"could not find TypeScript.slnx above {AppContext.BaseDirectory}");
    }
}
