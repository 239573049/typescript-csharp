// Ported from tsgo internal/fswatch/walkdir.go (walkDirGeneric — the portable
// implementation, which the Go package also tests on all platforms).
//
// PORT-NOTE: 非 1:1，FileSystemWatcher 重写. Go's platform-specific dirent walkers
// (walkdir_dirent_*.go, walkdir_windows.go, walkdir_unix.go) are collapsed to a
// single BCL traversal — plan/06 §8.5 mandates Directory.EnumerateFileSystemEntries
// (here via DirectoryInfo enumeration to preserve Go's Lstat/ReadDir semantics:
// symlinks are reported but not followed).

using System;
using System.IO;

namespace Ts.FsWatch;

internal static class WalkDir
{
    // walkDirGeneric is the portable walkDir implementation. It visits `dir`
    // and (when recursive) its subtree, calling fn(path, isDir). Symlinked
    // directories are reported as non-directory entries and never descended
    // into — this mirrors Go's use of Lstat + DirEntry.IsDir (which reports the
    // link, not its target).
    //
    // Go signature: walkDirGeneric(dir string, recursive bool,
    //   fn func(path string, isDir bool) error) error.
    // A callback error propagates out (used as a stop sentinel by callers).
    public static Exception? WalkDirGeneric(string dir, bool recursive, Func<string, bool, Exception?>? fn)
    {
        // Go: os.Lstat(dir); if !info.IsDir() return syscall.ENOTDIR.
        DirEntryInfo info;
        try
        {
            info = LstatDir(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return e;
        }
        if (!info.IsDir)
        {
            return new IOException($"fswatch: not a directory: {dir}");
        }
        return WalkDirGenericVisit(dir, recursive, fn);
    }

    private static Exception? WalkDirGenericVisit(string dir, bool recursive, Func<string, bool, Exception?>? fn)
    {
        FileSystemInfo[] entries;
        try
        {
            // os.ReadDir(dir) — enumerate immediate children without following links.
            var di = new DirectoryInfo(dir);
            entries = di.GetFileSystemInfos();
        }
        catch (Exception e) when (e is UnauthorizedAccessException)
        {
            // Go: errors.Is(err, fs.ErrPermission) → return nil (skip silently).
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            // Go: errors.Is(err, fs.ErrNotExist) → return nil.
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException)
        {
            return e;
        }

        if (fn != null)
        {
            Exception? err = fn(dir, true);
            if (err != null)
            {
                return err;
            }
        }

        foreach (FileSystemInfo e in entries)
        {
            string path = dir + Path.DirectorySeparatorChar + e.Name;
            // Go: e.IsDir() reflects the DirEntry type. A symlink to a directory
            // is NOT a directory here (ReadDir returns the link's own type),
            // matching Go's behavior of not descending into symlinked dirs.
            bool isDir = IsDirEntry(e);
            if (isDir)
            {
                if (recursive)
                {
                    Exception? err = WalkDirGenericVisit(path, recursive, fn);
                    if (err != null)
                    {
                        return err;
                    }
                }
                else if (fn != null)
                {
                    Exception? err = fn(path, true);
                    if (err != null)
                    {
                        return err;
                    }
                }
            }
            else if (fn != null)
            {
                Exception? err = fn(path, false);
                if (err != null)
                {
                    return err;
                }
            }
        }
        return null;
    }

    // IsDirEntry replicates Go's DirEntry.IsDir(): true only for a real
    // directory, false for a symlink to a directory (the link's own type is a
    // symlink, which is not a directory). A reparse-point directory is treated
    // as a symlink → non-directory, so the walk does not descend into it.
    private static bool IsDirEntry(FileSystemInfo info)
    {
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }
        return (info.Attributes & FileAttributes.Directory) != 0;
    }

    // LstatDir replicates os.Lstat on a directory path (does not follow a final
    // symlink). Throws for a missing path (Go returns the error); the caller
    // converts a non-directory result to ENOTDIR.
    private static DirEntryInfo LstatDir(string dir)
    {
        var di = new DirectoryInfo(dir);
        if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return new DirEntryInfo(true);
        }
        // Either it does not exist, is a file, or is a symlink. A symlinked
        // "root" dir passed directly to walk is treated as a non-directory,
        // matching Go's Lstat (the link type is not a directory).
        var fi = new FileInfo(dir);
        if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return new DirEntryInfo(false); // symlink → ENOTDIR at root
        }
        if (fi.Exists)
        {
            return new DirEntryInfo(false);
        }
        throw new DirectoryNotFoundException($"fswatch: no such directory: {dir}");
    }

    private readonly struct DirEntryInfo
    {
        public readonly bool IsDir;
        public DirEntryInfo(bool isDir) => IsDir = isDir;
    }
}
