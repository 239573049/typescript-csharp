// Ported from tsgo internal/vfs/vfstest/vfstest_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTES:
//  - fstest.TestFS(...) conformance-tester assertions have no BCL counterpart and are omitted.
//  - The provider read surface is nullable/Try-shaped, so Go's "file does not exist"
//    ErrorContains assertions on Stat/Realpath become null/false checks.
//  - testutil.AssertPanics → Assert.Throws<InvalidOperationException> with an exact message
//    (Go recovers a formatted panic string). The "invalid file type" case reports the CLR
//    type name (System.Int32) rather than Go's %T ("int").

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Ts.Vfs;
using Ts.Vfs.VfsTest;
using Xunit;

namespace Ts.Vfs.Tests;

public class VfsTestTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static List<string> DirEntriesToNames(IReadOnlyList<IDirEntry>? entries)
    {
        var names = new List<string>();
        if (entries != null)
        {
            foreach (var e in entries)
            {
                names.Add(e.Name());
            }
        }
        return names;
    }

    [Fact]
    public void Insensitive()
    {
        byte[] contents = Bytes("bar");

        var vfs = VfsTestUtil.ConvertMapFS(new Dictionary<string, MapFile>
        {
            ["foo/bar/baz"] = new MapFile { Data = contents, Sys = 1234 },
            ["foo/bar2/baz2"] = new MapFile { Data = contents, Sys = 1234 },
            ["foo/bar3/baz3"] = new MapFile { Data = contents, Sys = 1234 },
        }, false /*useCaseSensitiveFileNames*/, null);

        Assert.Equal(contents, vfs.ReadFile("foo/bar/baz"));
        var sensitiveInfo = vfs.Stat("foo/bar/baz");
        Assert.NotNull(sensitiveInfo);
        Assert.Equal(1234, (int)sensitiveInfo!.Sys()!);
        Assert.True(vfs.TryRealpath("foo/bar/baz", out string sensitiveRealPath));
        Assert.Equal("foo/bar/baz", sensitiveRealPath);
        Assert.Equal(new[] { "bar", "bar2", "bar3" }, DirEntriesToNames(vfs.ReadDir("foo")));

        Assert.False(vfs.TryRealpath("does/not/exist", out _));
        Assert.Null(vfs.Stat("does/not/exist"));

        Assert.Equal(contents, vfs.ReadFile("Foo/Bar/Baz"));
        var insensitiveInfo = vfs.Stat("Foo/Bar/Baz");
        Assert.NotNull(insensitiveInfo);
        Assert.Equal(1234, (int)insensitiveInfo!.Sys()!);
        Assert.True(vfs.TryRealpath("Foo/Bar/Baz", out string insensitiveRealPath));
        Assert.Equal("foo/bar/baz", insensitiveRealPath);
        Assert.Equal(new[] { "bar", "bar2", "bar3" }, DirEntriesToNames(vfs.ReadDir("Foo")));

        Assert.False(vfs.TryRealpath("Does/Not/Exist", out _));
        Assert.Null(vfs.Stat("Does/Not/Exist"));
    }

    [Fact]
    public void InsensitiveUpper()
    {
        byte[] contents = Bytes("bar");

        var vfs = VfsTestUtil.ConvertMapFS(new Dictionary<string, MapFile>
        {
            ["Foo/Bar/Baz"] = new MapFile { Data = contents, Sys = 1234 },
            ["Foo/Bar2/Baz2"] = new MapFile { Data = contents, Sys = 1234 },
            ["Foo/Bar3/Baz3"] = new MapFile { Data = contents, Sys = 1234 },
        }, false /*useCaseSensitiveFileNames*/, null);

        Assert.Equal(contents, vfs.ReadFile("foo/bar/baz"));
        var sensitiveInfo = vfs.Stat("foo/bar/baz");
        Assert.NotNull(sensitiveInfo);
        Assert.Equal(1234, (int)sensitiveInfo!.Sys()!);
        Assert.Equal(new[] { "Bar", "Bar2", "Bar3" }, DirEntriesToNames(vfs.ReadDir("foo")));

        Assert.Equal(contents, vfs.ReadFile("Foo/Bar/Baz"));
        var insensitiveInfo = vfs.Stat("Foo/Bar/Baz");
        Assert.NotNull(insensitiveInfo);
        Assert.Equal(1234, (int)insensitiveInfo!.Sys()!);
        Assert.Equal(new[] { "Bar", "Bar2", "Bar3" }, DirEntriesToNames(vfs.ReadDir("Foo")));
    }

    [Fact]
    public void Sensitive()
    {
        byte[] contents = Bytes("bar");

        var vfs = VfsTestUtil.ConvertMapFS(new Dictionary<string, MapFile>
        {
            ["foo/bar/baz"] = new MapFile { Data = contents, Sys = 1234 },
            ["foo/bar2/baz2"] = new MapFile { Data = contents, Sys = 1234 },
            ["foo/bar3/baz3"] = new MapFile { Data = contents, Sys = 1234 },
        }, true /*useCaseSensitiveFileNames*/, null);

        Assert.Equal(contents, vfs.ReadFile("foo/bar/baz"));
        var sensitiveInfo = vfs.Stat("foo/bar/baz");
        Assert.NotNull(sensitiveInfo);
        Assert.Equal(1234, (int)sensitiveInfo!.Sys()!);

        Assert.Null(vfs.ReadFile("Foo/Bar/Baz"));
    }

    [Fact]
    public void SensitiveDuplicatePath()
    {
        var testfs = new Dictionary<string, MapFile>
        {
            ["foo"] = new MapFile { Data = Bytes("bar") },
            ["Foo"] = new MapFile { Data = Bytes("baz") },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.ConvertMapFS(testfs, false /*useCaseSensitiveFileNames*/, null));
        Assert.Equal("duplicate path: \"Foo\" and \"foo\" have the same canonical path", ex.Message);
    }

    [Fact]
    public void InsensitiveDuplicatePath()
    {
        var testfs = new Dictionary<string, MapFile>
        {
            ["foo"] = new MapFile { Data = Bytes("bar") },
            ["Foo"] = new MapFile { Data = Bytes("baz") },
        };

        // No panic when case-sensitive.
        VfsTestUtil.ConvertMapFS(testfs, true /*useCaseSensitiveFileNames*/, null);
    }

    [Fact]
    public void WritableFS()
    {
        var fs = VfsTestUtil.FromMap<object>(null, false);

        fs.WriteFile("/foo/bar/baz", "hello, world");

        Assert.True(fs.TryReadFile("/foo/bar/baz", out string content));
        Assert.Equal("hello, world", content);

        fs.WriteFile("/foo/bar/baz", "goodbye, world");

        Assert.True(fs.TryReadFile("/foo/bar/baz", out content));
        Assert.Equal("goodbye, world", content);

        var ex = Assert.Throws<IOException>(() => fs.WriteFile("/foo/bar/baz/oops", "goodbye, world"));
        Assert.Contains("mkdir \"foo/bar/baz\": path exists but is not a directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritableFSDelete()
    {
        var fs = VfsTestUtil.FromMap<object>(null, false);

        fs.WriteFile("/foo/bar/file.ts", "remove");
        Assert.True(fs.FileExists("/foo/bar/file.ts"));
        fs.Remove("/foo/bar/file.ts");
        Assert.False(fs.FileExists("/foo/bar/file.ts"));

        fs.WriteFile("/foo/bar/test/remove2.ts", "remove2");
        Assert.True(fs.DirectoryExists("/foo/bar/test"));
        fs.Remove("/foo/bar/test");
        Assert.False(fs.FileExists("/foo/bar/test/remove2.ts"));
        Assert.False(fs.DirectoryExists("/foo/bar/test"));

        // no errors when removing file/dir that does not exist
        fs.Remove("/foo/bar/test");
        fs.Remove("/foo/bar/file.ts");

        fs.WriteFile("/foo/barbar", "remove2");
        fs.Remove("/foo/bar");
        Assert.True(fs.FileExists("/foo/barbar"));
    }

    [Fact]
    public void Stress()
    {
        var fs = VfsTestUtil.FromMap<object>(null, false);

        var ops = new Action[]
        {
            () => fs.WriteFile("/foo/bar/baz.txt", "hello, world"),
            () => fs.TryReadFile("/foo/bar/baz.txt", out _),
            () => fs.DirectoryExists("/foo/bar"),
            () => fs.FileExists("/foo/bar"),
            () => fs.FileExists("/foo/bar/baz.txt"),
            () => fs.GetAccessibleEntries("/foo/bar"),
            () => fs.Realpath("/foo/bar/baz.txt"),
            () => fs.WalkDir("/", (path, d, err) =>
            {
                if (err != null)
                {
                    return err;
                }
                d?.Info();
                return null;
            }),
        };

        int threadCount = Math.Max(2, Environment.ProcessorCount);
        var threads = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            threads[t] = new Thread(() =>
            {
                var rng = new Random(Environment.CurrentManagedThreadId);
                for (int i = 0; i < 10000; i++)
                {
                    ops[rng.Next(ops.Length)]();
                }
            });
        }
        foreach (var th in threads)
        {
            th.Start();
        }
        foreach (var th in threads)
        {
            th.Join();
        }
    }

    [Fact]
    public void ParentDirFile()
    {
        var testfs = new Dictionary<string, MapFile>
        {
            ["foo"] = new MapFile { Data = Bytes("bar") },
            ["foo/oops"] = new MapFile { Data = Bytes("baz") },
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.ConvertMapFS(testfs, false /*useCaseSensitiveFileNames*/, null));
        Assert.Equal("failed to create intermediate directories for \"foo/oops\": mkdir \"foo\": path exists but is not a directory", ex.Message);
    }

    [Fact]
    public void FromMapPosix()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["/string"] = "hello, world",
            ["/bytes"] = Bytes("hello, world"),
            ["/mapfile"] = new MapFile { Data = Bytes("hello, world") },
        }, false);

        Assert.True(fs.TryReadFile("/string", out string content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("/bytes", out content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("/mapfile", out content));
        Assert.Equal("hello, world", content);
    }

    [Fact]
    public void FromMapWindows()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["c:/string"] = "hello, world",
            ["d:/bytes"] = Bytes("hello, world"),
            ["e:/mapfile"] = new MapFile { Data = Bytes("hello, world") },
        }, false);

        Assert.True(fs.TryReadFile("c:/string", out string content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("d:/bytes", out content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("e:/mapfile", out content));
        Assert.Equal("hello, world", content);
    }

    [Fact]
    public void FromMapMixed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/string"] = "hello, world",
                ["c:/bytes"] = Bytes("hello, world"),
            }, false));
        Assert.Equal("mixed posix and windows paths", ex.Message);
    }

    [Fact]
    public void FromMapNonRooted()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["string"] = "hello, world",
            }, false));
        Assert.Equal("non-rooted path \"string\"", ex.Message);
    }

    [Fact]
    public void FromMapNonNormalized()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/string/"] = "hello, world",
            }, false));
        Assert.Equal("non-normalized path \"/string/\"", ex.Message);
    }

    [Fact]
    public void FromMapNonNormalized2()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/string/../foo"] = "hello, world",
            }, false));
        Assert.Equal("non-normalized path \"/string/../foo\"", ex.Message);
    }

    [Fact]
    public void FromMapInvalidFile()
    {
        // PORT-NOTE: Go's %T yields "int"; the CLR type name (System.Int32) is used instead.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            VfsTestUtil.FromMap(new Dictionary<string, object>
            {
                ["/string"] = 1234,
            }, false));
        Assert.Equal("invalid file type System.Int32", ex.Message);
    }

    [Fact]
    public void VFSTestMapFS()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["/foo.ts"] = "hello, world",
            ["/dir1/file1.ts"] = "export const foo = 42;",
            ["/dir1/file2.ts"] = "export const foo = 42;",
            ["/dir2/file1.ts"] = "export const foo = 42;",
        }, false /*useCaseSensitiveFileNames*/);

        // ReadFile
        Assert.True(fs.TryReadFile("/foo.ts", out string content));
        Assert.Equal("hello, world", content);
        Assert.False(fs.TryReadFile("/does/not/exist.ts", out content));
        Assert.Equal("", content);

        // Realpath
        Assert.Equal("/foo.ts", fs.Realpath("/foo.ts"));
        Assert.Equal("/foo.ts", fs.Realpath("/Foo.ts"));
        Assert.Equal("/does/not/exist.ts", fs.Realpath("/does/not/exist.ts"));

        // UseCaseSensitiveFileNames
        Assert.False(fs.UseCaseSensitiveFileNames());
    }

    [Fact]
    public void VFSTestMapFSWindows()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, string>
        {
            ["c:/foo.ts"] = "hello, world",
            ["c:/dir1/file1.ts"] = "export const foo = 42;",
            ["c:/dir1/file2.ts"] = "export const foo = 42;",
            ["c:/dir2/file1.ts"] = "export const foo = 42;",
        }, false);

        Assert.True(fs.TryReadFile("c:/foo.ts", out string content));
        Assert.Equal("hello, world", content);
        Assert.False(fs.TryReadFile("c:/does/not/exist.ts", out content));
        Assert.Equal("", content);

        Assert.Equal("c:/foo.ts", fs.Realpath("c:/foo.ts"));
        Assert.Equal("c:/foo.ts", fs.Realpath("c:/Foo.ts"));
        Assert.Equal("c:/does/not/exist.ts", fs.Realpath("c:/does/not/exist.ts"));
    }

    [Theory]
    [InlineData("BigEndian", true, 0xFE, 0xFF)]
    [InlineData("LittleEndian", false, 0xFF, 0xFE)]
    public void BOM(string name, bool bigEndian, int bom0, int bom1)
    {
        _ = name;
        const string expected = "hello, world";

        var buf = new List<byte> { (byte)bom0, (byte)bom1 };
        foreach (char r in expected)
        {
            ushort unit = r;
            if (bigEndian)
            {
                buf.Add((byte)(unit >> 8));
                buf.Add((byte)(unit & 0xFF));
            }
            else
            {
                buf.Add((byte)(unit & 0xFF));
                buf.Add((byte)(unit >> 8));
            }
        }

        var fs = VfsTestUtil.FromMap(new Dictionary<string, byte[]>
        {
            ["/foo.ts"] = buf.ToArray(),
        }, true);

        Assert.True(fs.TryReadFile("/foo.ts", out string content));
        Assert.Equal(expected, content);
    }

    [Fact]
    public void BOMUtf8()
    {
        const string expected = "hello, world";
        // Go: []byte("\xEF\xBB\xBF" + expected) — raw UTF-8 BOM bytes prepended. C#'s \x
        // escapes produce chars, not bytes, so build the byte array directly.
        var withBom = new List<byte> { 0xEF, 0xBB, 0xBF };
        withBom.AddRange(Bytes(expected));
        var fs = VfsTestUtil.FromMap(new Dictionary<string, byte[]>
        {
            ["/foo.ts"] = withBom.ToArray(),
        }, true);

        Assert.True(fs.TryReadFile("/foo.ts", out string content));
        Assert.Equal(expected, content);
    }

    [Fact]
    public void Symlink()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["/foo.ts"] = "hello, world",
            ["/symlink.ts"] = VfsTestUtil.Symlink("/foo.ts"),
            ["/some/dir/file.ts"] = "hello, world",
            ["/some/dirlink"] = VfsTestUtil.Symlink("/some/dir"),
            ["/a"] = VfsTestUtil.Symlink("/b"),
            ["/b"] = VfsTestUtil.Symlink("/c"),
            ["/c"] = VfsTestUtil.Symlink("/d"),
            ["/d/existing.ts"] = "this is existing.ts",
        }, false);

        // ReadFile
        Assert.True(fs.TryReadFile("/symlink.ts", out string content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("/some/dirlink/file.ts", out content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("/a/existing.ts", out content));
        Assert.Equal("this is existing.ts", content);

        // Realpath
        Assert.Equal("/foo.ts", fs.Realpath("/symlink.ts"));
        Assert.Equal("/some/dir", fs.Realpath("/some/dirlink"));
        Assert.Equal("/some/dir/file.ts", fs.Realpath("/some/dirlink/file.ts"));

        // FileExists
        Assert.True(fs.FileExists("/symlink.ts"));
        Assert.True(fs.FileExists("/some/dirlink/file.ts"));
        Assert.True(fs.FileExists("/a/existing.ts"));

        // DirectoryExists
        Assert.True(fs.DirectoryExists("/some/dirlink"));
        Assert.True(fs.DirectoryExists("/d"));
        Assert.True(fs.DirectoryExists("/c"));
        Assert.True(fs.DirectoryExists("/b"));
        Assert.True(fs.DirectoryExists("/a"));
    }

    [Fact]
    public void WritableFSSymlink()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["/some/dir/other.ts"] = "NOTHING",
            ["/other.ts"] = VfsTestUtil.Symlink("/some/dir/other.ts"),
            ["/some/dirlink"] = VfsTestUtil.Symlink("/some/dir"),
            ["/brokenlink"] = VfsTestUtil.Symlink("/does/not/exist"),
            ["/a"] = VfsTestUtil.Symlink("/b"),
            ["/b"] = VfsTestUtil.Symlink("/c"),
            ["/c"] = VfsTestUtil.Symlink("/d"),
            ["/d/existing.ts"] = "hello, world",
        }, false);

        fs.WriteFile("/some/dirlink/file.ts", "hello, world");

        Assert.True(fs.TryReadFile("/some/dirlink/file.ts", out string content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("/some/dir/file.ts", out content));
        Assert.Equal("hello, world", content);

        fs.WriteFile("/some/dirlink/file.ts", "goodbye, world");

        Assert.True(fs.TryReadFile("/some/dirlink/file.ts", out content));
        Assert.Equal("goodbye, world", content);

        fs.WriteFile("/other.ts", "hello, world");

        Assert.True(fs.TryReadFile("/other.ts", out content));
        Assert.Equal("hello, world", content);

        Assert.True(fs.TryReadFile("/some/dir/other.ts", out content));
        Assert.Equal("hello, world", content);

        var ex = Assert.Throws<IOException>(() => fs.WriteFile("/some/dirlink", "hello, world"));
        Assert.Equal("write \"some/dirlink\": path exists but is not a regular file", ex.Message);

        // Can't write inside a broken dir symlink
        ex = Assert.Throws<IOException>(() => fs.WriteFile("/brokenlink/file.ts", "hello, world"));
        Assert.Equal("broken symlink \"brokenlink\" -> \"does/not/exist\"", ex.Message);

        ex = Assert.Throws<IOException>(() => fs.WriteFile("/brokenlink/also/wrong/file.ts", "hello, world"));
        Assert.Equal("broken symlink \"brokenlink\" -> \"does/not/exist\"", ex.Message);

        // But we can write to a broken file symlink
        fs.WriteFile("/brokenlink", "hello, world");
        Assert.True(fs.TryReadFile("/brokenlink", out content));
        Assert.Equal("hello, world", content);
        Assert.True(fs.TryReadFile("/does/not/exist", out content));
        Assert.Equal("hello, world", content);
    }

    [Fact]
    public void WritableFSSymlinkChain()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["/a"] = VfsTestUtil.Symlink("/b"),
            ["/b"] = VfsTestUtil.Symlink("/c"),
            ["/c"] = VfsTestUtil.Symlink("/d"),
            ["/d/existing.ts"] = "hello, world",
        }, false);

        fs.WriteFile("/a/foo/bar/new.ts", "this is new.ts");
        Assert.True(fs.TryReadFile("/a/foo/bar/new.ts", out string content));
        Assert.Equal("this is new.ts", content);
        Assert.True(fs.TryReadFile("/b/foo/bar/new.ts", out content));
        Assert.Equal("this is new.ts", content);
        Assert.True(fs.TryReadFile("/d/foo/bar/new.ts", out content));
        Assert.Equal("this is new.ts", content);
    }

    [Fact]
    public void WritableFSSymlinkChainNotDir()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["/a"] = VfsTestUtil.Symlink("/b"),
            ["/b"] = VfsTestUtil.Symlink("/c"),
            ["/c"] = VfsTestUtil.Symlink("/d"),
            ["/d"] = "hello, world",
        }, false);

        var ex = Assert.Throws<IOException>(() => fs.WriteFile("/a/foo/bar/new.ts", "this is new.ts"));
        Assert.Equal("mkdir \"d\": path exists but is not a directory", ex.Message);
    }

    [Fact]
    public void WritableFSSymlinkDelete()
    {
        var fs = VfsTestUtil.FromMap(new Dictionary<string, object>
        {
            ["/some/dir/other.ts"] = "NOTHING",
            ["/other.ts"] = VfsTestUtil.Symlink("/some/dir/other.ts"),
            ["/some/dirlink"] = VfsTestUtil.Symlink("/some/dir"),
            ["/brokenlink"] = VfsTestUtil.Symlink("/does/not/exist"),
            ["/a"] = VfsTestUtil.Symlink("/b"),
            ["/b"] = VfsTestUtil.Symlink("/c"),
            ["/c"] = VfsTestUtil.Symlink("/d"),
            ["/d/existing.ts"] = "hello, world",
        }, false);

        fs.Remove("/a");
        Assert.False(fs.DirectoryExists("/a"));
        Assert.True(fs.DirectoryExists("/b"));
        Assert.True(fs.DirectoryExists("/c"));
        Assert.True(fs.FileExists("/d/existing.ts"));

        // symlinks should still exist even if underlying file/dir is deleted
        fs.Remove("/d");
        Assert.False(fs.DirectoryExists("/b"));
        Assert.False(fs.DirectoryExists("/c"));
        Assert.False(fs.DirectoryExists("/d"));
        Assert.False(fs.FileExists("/d/again.ts"));
        fs.WriteFile("/d/again.ts", "d exists again");
        Assert.True(fs.DirectoryExists("/b"));
        Assert.True(fs.DirectoryExists("/c"));
        fs.TryReadFile("/b/again.ts", out string content);
        Assert.Equal("d exists again", content);

        Assert.False(fs.FileExists("/brokenlink"));
        Assert.False(fs.DirectoryExists("/brokenlink"));
        fs.Remove("/does/not/exist"); // should do nothing
        Assert.False(fs.FileExists("/brokenlink"));
        Assert.False(fs.DirectoryExists("/brokenlink"));
        fs.WriteFile("/does/not/exist", "hello, world");
        Assert.True(fs.FileExists("/brokenlink"));
    }
}
