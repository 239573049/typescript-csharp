// Minimal Go stdlib io/fs equivalent for the vfs layer (no 1:1 tsgo counterpart file).
//
// Go's vfs/internal and vfs/iovfs are written against stdlib fs.FS (Open-based, with
// fs.Stat/fs.ReadDir/fs.ReadFile/fs.Sub/fs.WalkDir helpers). The BCL has no fs.FS
// analogue, so per plan/06 §2.6 ("fs.FS 适配 → 以 C# 抽象重述") this file restates the
// consumed surface as a minimal provider abstraction:
//   - IFileProvider           ≈ fs.FS + the derived read helpers, Try/nullable-shaped
//   - FileProvider.Sub        ≈ fs.Sub   (Go stdlib io/fs/sub.go)
//   - FileProvider.WalkDir    ≈ fs.WalkDir (Go stdlib io/fs/walk.go, semantics 1:1)
//   - FileProvider.ValidPath  ≈ fs.ValidPath (Go stdlib io/fs/fs.go)
//
// PORT-NOTE: provider failures carry no error detail (nullable results); the WalkDir
// algorithm therefore reports VfsUtil.ErrNotExist to the callback where Go would pass
// the underlying *PathError. Every in-tree walkFn either swallows or propagates that
// error without reading it; revisit if osvfs/fourslash need richer error identity.

using System;
using System.Collections.Generic;

namespace Ts.Vfs;

// Go: fs.FS (plus the fs.Stat/fs.ReadDir/fs.ReadFile helper surface the vfs layer uses).
public interface IFileProvider
{
    // Go: fs.Stat(fsys, name) (FileInfo, error) — error only ever nil-checked → null.
    IFileInfo? Stat(string name);

    // Go: fs.ReadDir(fsys, name) ([]DirEntry, error) — error only ever nil-checked → null.
    // Contract (same as fs.ReadDir): entries are sorted by filename, ordinal.
    IReadOnlyList<IDirEntry>? ReadDir(string name);

    // Go: fs.ReadFile(fsys, name) ([]byte, error) — error only ever nil-checked → null.
    // A zero-length array is an existing empty file; null is a read failure.
    byte[]? ReadFile(string name);
}

public static class FileProvider
{
    // Go: stdlib fs.ValidPath(name) — path must be unrooted, slash-separated, without
    // "." or ".." elements. PORT-NOTE: Go additionally rejects invalid UTF-8; C# strings
    // are UTF-16, so that clause dissolves.
    public static bool ValidPath(string name)
    {
        if (name == ".")
        {
            return true;
        }
        while (true)
        {
            int i = 0;
            while (i < name.Length && name[i] != '/')
            {
                i++;
            }
            string elem = name[..i];
            if (elem.Length == 0 || elem == "." || elem == "..")
            {
                return false;
            }
            if (i == name.Length)
            {
                return true; // reached clean ending
            }
            name = name[(i + 1)..];
        }
    }

    // Go: stdlib fs.Sub(fsys, dir) (FS, error) — error (invalid dir) → null.
    // PORT-NOTE: the Go SubFS fast path (fsys implementing Sub itself) has no C#
    // counterpart yet; nested Sub calls simply chain wrappers.
    public static IFileProvider? Sub(IFileProvider fsys, string dir)
    {
        if (!ValidPath(dir))
        {
            return null;
        }
        if (dir == ".")
        {
            return fsys;
        }
        return new SubProvider(fsys, dir);
    }

    // Go: stdlib fs.WalkDir(fsys, root, fn) — semantics ported 1:1 from io/fs/walk.go,
    // including the SkipDir/SkipAll identity comparisons and the second callback call
    // on ReadDir failure.
    public static Exception? WalkDir(IFileProvider fsys, string root, WalkDirFunc fn)
    {
        Exception? err;
        var info = fsys.Stat(root);
        if (info == null)
        {
            err = fn(root, null, VfsUtil.ErrNotExist); // see PORT-NOTE in file header
        }
        else
        {
            err = WalkDirRecursive(fsys, root, new FileInfoDirEntry(info), fn);
        }
        if (ReferenceEquals(err, VfsUtil.SkipDir) || ReferenceEquals(err, VfsUtil.SkipAll))
        {
            return null;
        }
        return err;
    }

    // Go: stdlib io/fs walkDir. Recursion depth is bounded by directory nesting (not
    // AST-scale); no LargeStackExecutor wrapping needed at this entry.
    private static Exception? WalkDirRecursive(IFileProvider fsys, string name, IDirEntry d, WalkDirFunc walkDirFn)
    {
        var err = walkDirFn(name, d, null);
        if (err != null || !d.IsDir())
        {
            if (ReferenceEquals(err, VfsUtil.SkipDir) && d.IsDir())
            {
                // Successfully skipped directory.
                err = null;
            }
            return err;
        }

        var dirs = fsys.ReadDir(name);
        if (dirs == null)
        {
            // Second call, to report ReadDir error.
            err = walkDirFn(name, d, VfsUtil.ErrNotExist); // see PORT-NOTE in file header
            if (err != null)
            {
                if (ReferenceEquals(err, VfsUtil.SkipDir) && d.IsDir())
                {
                    err = null;
                }
                return err;
            }
            dirs = Array.Empty<IDirEntry>(); // Go: nil slice ranges as empty
        }

        foreach (var d1 in dirs)
        {
            string name1 = Join(name, d1.Name());
            var err1 = WalkDirRecursive(fsys, name1, d1, walkDirFn);
            if (err1 != null)
            {
                if (ReferenceEquals(err1, VfsUtil.SkipDir))
                {
                    break;
                }
                return err1;
            }
        }
        return null;
    }

    // Go: stdlib path.Join as used by walkDir — both inputs are already clean here,
    // so only the "." root needs special-casing.
    private static string Join(string dir, string name) => dir == "." ? name : dir + "/" + name;

    // Go: stdlib fs.FileInfoToDirEntry(info).
    private sealed class FileInfoDirEntry : IDirEntry
    {
        private readonly IFileInfo info;

        public FileInfoDirEntry(IFileInfo info)
        {
            this.info = info;
        }

        public string Name() => info.Name();
        public bool IsDir() => info.IsDir();
        public FileMode Type() => info.Mode().Type();
        public IFileInfo? Info() => info;
    }

    // Go: stdlib io/fs subFS — prefixes dir onto every name.
    private sealed class SubProvider : IFileProvider
    {
        private readonly IFileProvider fsys;
        private readonly string dir;

        public SubProvider(IFileProvider fsys, string dir)
        {
            this.fsys = fsys;
            this.dir = dir;
        }

        // Go: subFS.fullName (io/fs/sub.go) — path.Join(dir, name); inputs are clean.
        private string Full(string name) => name == "." ? dir : dir + "/" + name;

        public IFileInfo? Stat(string name) => fsys.Stat(Full(name));
        public IReadOnlyList<IDirEntry>? ReadDir(string name) => fsys.ReadDir(Full(name));
        public byte[]? ReadFile(string name) => fsys.ReadFile(Full(name));
    }
}
