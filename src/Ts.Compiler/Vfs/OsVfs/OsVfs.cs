// Ported from tsgo internal/vfs/osvfs/os.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
//
// Go's osFS is os.DirFS layered under internal.Common; the C# counterpart restates the
// os.DirFS surface as OsFileProvider (a concrete IFileProvider hitting the real disk;
// FileProvider.cs explains why fs.FS becomes IFileProvider), then wraps it in Common
// exactly as Go does. Semaphore limiting uses the ported Ts.Core.LimitedSemaphore.
//
// PORT-NOTES (platform / dependency divergences, registered for the final report):
//  - nativepath is a separate phase-1 work item and is not yet ported. Per the assignment,
//    Realpath is implemented directly from BCL link resolution (File/Directory.LinkTarget,
//    which is readlink) via a filepath.EvalSymlinks-style component walk (EvalSymlinks
//    below). isReparsePoint likewise uses BCL reparse detection.
//  - core.VersionMajorMinor / os.UserCacheDir have no C# port yet (core.go owned by another
//    work item); GetGlobalTypingsCacheLocation ports both faithfully as private helpers.
//  - Windows junctions: Go reports ModeIrregular so Common routes them through the
//    IsReparsePoint branch; here every reparse point maps to ModeSymlink, so Common's
//    symlink branch (Stat + follow) handles them. The GetAccessibleEntries output is
//    identical (a followed entry added with isLink=true); only the routing differs.
//  - The sync.Pool of limitedWalkDirFunc (os.go:118-135) is a pure allocation optimization;
//    per plan/01 §5.2 only the named parser/binder pools use ObjectPool, so the walker is a
//    plain per-call closure here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Ts.Core;
using Ts.Vfs.Internal;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.Vfs.OsVfs;

// Go: package-level funcs/vars of os.go (naming per the VfsUtil/IoVfsUtil precedent for
// package-level members).
public static class OsVfsUtil
{
    // os.go:21-28 — semaphores for operations that are effectively blocking syscalls.
    internal static readonly LimitedSemaphore BlockingOpSema = new(128);

    // Semaphore for file reads.
    internal static readonly LimitedSemaphore ReadSema = new(128);

    // Semaphore for file writes.
    internal static readonly LimitedSemaphore WriteSema = new(32);

    private static readonly OsFs OsVFS = new();

    // os.go:30-33 — FS creates a new FS from the OS file system.
    public static IFileSystem FS()
    {
        return OsVFS;
    }

    // os.go:46-75 — computed once right at startup to minimize the chance the executable
    // gets moved or deleted.
    internal static readonly bool IsFileSystemCaseSensitive = ComputeIsFileSystemCaseSensitive();

    private static bool ComputeIsFileSystemCaseSensitive()
    {
        // win32/win64 are case insensitive platforms
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        // PORT-NOTE: Go's runtime.GOARCH == "wasm" branch (returns true) has no counterpart
        // in a native-AOT build; the browser-wasm target is out of scope (plan/07).

        // As a proxy for case-insensitivity, we check if the current executable exists under a
        // different case. This is not entirely correct, since different OSs can have differing
        // case sensitivity in different paths, but this is largely good enough for our purposes
        // (and what sys.ts used to do with __filename).
        string? exe = Environment.ProcessPath; // Go: os.Executable()
        if (exe == null)
        {
            // Go: panic(fmt.Sprintf("vfs: failed to get executable path: %v", err))
            throw new InvalidOperationException("vfs: failed to get executable path");
        }

        // If the current executable exists under a different case, we must be case-insensitive.
        string swapped = SwapCase(exe);
        // Go: os.Stat(swapped); os.IsNotExist(err) => true; err (other) => panic; else => false.
        // PORT-NOTE: BCL existence checks fold the non-not-exist error branch into "not found";
        // the panic path is unreachable for ordinary executable paths.
        return !Path.Exists(swapped);
    }

    // os.go:77-87 — convert all lowercase chars to uppercase, and vice-versa.
    internal static string SwapCase(string str)
    {
        // Go: strings.Map(func(r rune) rune { upper := unicode.ToUpper(r); if upper == r { return unicode.ToLower(r) } else { return upper } }, str)
        return Ts.StringUtil.StringUtil.MapRunes(static r =>
        {
            int upper = Ts.StringUtil.StringUtil.ToUpperRune(r);
            if (upper == r)
            {
                return Ts.StringUtil.StringUtil.ToLowerRune(r);
            }
            return upper;
        }, str);
    }

    // os.go:158-172 — osFSRealpath.
    internal static string OsFSRealpath(string path)
    {
        _ = Common.RootLength(path); // Assert path is rooted

        string orig = path;
        path = FromSlash(path); // Go: filepath.FromSlash
        string? resolved = Ts.NativePath.NativePath.Realpath(path); // Go: nativepath.Realpath
        if (resolved == null)
        {
            return orig;
        }
        try
        {
            resolved = Path.GetFullPath(resolved); // Go: filepath.Abs
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return orig;
        }
        return TsPathApi.NormalizeSlashes(resolved);
    }

    // os.go:174-176 — isReparsePoint. PORT-NOTE: nativepath.IsSymlinkOrReparsePoint replaced
    // with a BCL reparse-point probe; only meaningful on Windows (Unix links flow through
    // Common's ModeSymlink branch and never reach the IsReparsePoint hook).
    internal static bool IsReparsePoint(string path)
    {
        string native = FromSlash(path);
        return Ts.NativePath.NativePath.IsSymlinkOrReparsePoint(native);
    }

    // os.go:229-242 — GetGlobalTypingsCacheLocation.
    public static string GetGlobalTypingsCacheLocation()
    {
        string cacheDir = UserCacheDir() ?? Path.GetTempPath(); // Go: os.UserCacheDir() with os.TempDir() fallback

        string subdir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            subdir = "Microsoft/TypeScript";
        }
        else
        {
            subdir = "typescript";
        }
        return TsPathApi.CombinePaths(cacheDir, subdir, VersionMajorMinor());
    }

    // Go: filepath.FromSlash — '/' → OS separator (identity on POSIX).
    private static string FromSlash(string path)
    {
        if (Path.DirectorySeparatorChar == '/')
        {
            return path;
        }
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    // Go: os.UserCacheDir — the OS user cache directory, or null on failure.
    // PORT-NOTE: BCL has no os.UserCacheDir; the platform branches are ported directly.
    private static string? UserCacheDir()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string? dir = Environment.GetEnvironmentVariable("LocalAppData");
            return string.IsNullOrEmpty(dir) ? null : dir;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home) ? null : home + "/Library/Caches";
        }
        // Unix (XDG)
        string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(xdg))
        {
            string? home = Environment.GetEnvironmentVariable("HOME");
            return string.IsNullOrEmpty(home) ? null : home + "/.cache";
        }
        if (!TsPathApi.IsRootedDiskPath(xdg))
        {
            return null; // Go: path in $XDG_CACHE_HOME is relative
        }
        return xdg;
    }

    // Go: core.VersionMajorMinor (internal/core/version.go). PORT-NOTE: core.go is a separate
    // work item; this local port mirrors versionMajorMinor exactly against Ts.Core.TsVersion.
    private static string VersionMajorMinor()
    {
        string version = TsVersion.Value;
        bool seenMajor = false;
        int i = -1;
        for (int k = 0; k < version.Length; k++)
        {
            if (version[k] == '.')
            {
                if (seenMajor)
                {
                    i = k;
                    break;
                }
                seenMajor = true;
            }
        }
        if (i == -1)
        {
            throw new InvalidOperationException("invalid version string: " + version);
        }
        return version[..i];
    }
}

// Go: os.DirFS(root) — a concrete IFileProvider reading the real disk rooted at `root`.
// Stat follows symlinks (os.Stat); ReadDir reports lstat-style entry types (symlinks stay
// symlinks); ReadFile reads bytes.
internal sealed class OsFileProvider : IFileProvider
{
    private readonly string root;

    public OsFileProvider(string root)
    {
        this.root = root;
    }

    // Go: dirFS.join — dir + (endsWithSep ? "" : sep) + name. '/' works with BCL APIs on all
    // platforms, so we join with '/' and let the runtime localize.
    private string Join(string name)
    {
        if (name == ".")
        {
            return root;
        }
        if (root.Length > 0 && (root[^1] == '/' || root[^1] == '\\'))
        {
            return root + name;
        }
        return root + "/" + name;
    }

    // Go: fs.Stat via os.DirFS — follows symlinks.
    public IFileInfo? Stat(string name)
    {
        string full = Join(name);
        var di = new DirectoryInfo(full);
        if (di.Exists)
        {
            return new OsFileInfo(Path.GetFileName(full), 0, FileMode.Dir, di.LastWriteTimeUtc, true);
        }
        var fi = new FileInfo(full);
        if (fi.Exists)
        {
            return new OsFileInfo(Path.GetFileName(full), fi.Length, FileMode.None, fi.LastWriteTimeUtc, false);
        }
        return null;
    }

    // Go: fs.ReadDir via os.DirFS — entries sorted by name (ordinal), lstat-style types.
    public IReadOnlyList<IDirEntry>? ReadDir(string name)
    {
        string full = Join(name);
        FileSystemInfo[] infos;
        try
        {
            infos = new DirectoryInfo(full).GetFileSystemInfos();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        var entries = new List<IDirEntry>(infos.Length);
        foreach (var fsi in infos)
        {
            entries.Add(new OsDirEntry(fsi, LStatMode(fsi)));
        }
        // Go: os.ReadDir sorts by name; FileProvider contract is ordinal (plan/06 §2.6).
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name(), b.Name()));
        return entries;
    }

    // Go: fs.ReadFile via os.DirFS.
    public byte[]? ReadFile(string name)
    {
        string full = Join(name);
        try
        {
            return File.ReadAllBytes(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    // lstat-style mode of a directory entry (symlink not followed). See file-header PORT-NOTE
    // on the Windows junction collapse.
    internal static FileMode LStatMode(FileSystemInfo fsi)
    {
        FileAttributes attrs;
        try
        {
            attrs = fsi.Attributes;
        }
        catch (Exception e) when (e is IOException or FileNotFoundException or DirectoryNotFoundException)
        {
            return FileMode.None;
        }
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            return FileMode.Symlink;
        }
        if ((attrs & FileAttributes.Directory) != 0)
        {
            return FileMode.Dir;
        }
        return FileMode.None; // regular
    }
}

// Go: fs.FileInfo returned by os.DirFS.
internal sealed class OsFileInfo : IFileInfo
{
    private readonly string name;
    private readonly long size;
    private readonly FileMode mode;
    private readonly DateTime modTime;
    private readonly bool isDir;

    public OsFileInfo(string name, long size, FileMode mode, DateTime modTime, bool isDir)
    {
        this.name = name;
        this.size = size;
        this.mode = mode;
        this.modTime = modTime;
        this.isDir = isDir;
    }

    public string Name() => name;
    public long Size() => size;
    public FileMode Mode() => mode;
    public DateTime ModTime() => modTime;
    public bool IsDir() => isDir;
    public object? Sys() => null;
}

// Go: fs.DirEntry returned by os.DirFS.ReadDir — Type()/IsDir() are lstat-style; Info() is
// the entry's own (unfollowed) info.
internal sealed class OsDirEntry : IDirEntry
{
    private readonly FileSystemInfo fsi;
    private readonly FileMode mode;

    public OsDirEntry(FileSystemInfo fsi, FileMode mode)
    {
        this.fsi = fsi;
        this.mode = mode;
    }

    public string Name() => fsi.Name;
    public bool IsDir() => mode.IsDir();
    public FileMode Type() => mode.Type();

    public IFileInfo? Info()
    {
        long size = fsi is FileInfo f ? f.Length : 0;
        return new OsFileInfo(fsi.Name, size, mode, fsi.LastWriteTimeUtc, mode.IsDir());
    }
}

// Go: type osFS struct — the OS-backed vfs.FS, with each op limited by the appropriate
// syscall semaphore.
internal sealed class OsFs : IFileSystem
{
    private readonly Common common;

    public OsFs()
    {
        // os.go:35-40 — common: internal.Common{RootFor: os.DirFS, IsReparsePoint: isReparsePoint}.
        common = new Common
        {
            RootFor = root => new OsFileProvider(root),
            IsReparsePoint = OsVfsUtil.IsReparsePoint,
        };
    }

    public bool UseCaseSensitiveFileNames()
    {
        return OsVfsUtil.IsFileSystemCaseSensitive;
    }

    public bool TryReadFile(string path, out string contents)
    {
        var release = OsVfsUtil.ReadSema.Acquire();
        try
        {
            return common.TryReadFile(path, out contents);
        }
        finally
        {
            release();
        }
    }

    public bool DirectoryExists(string path)
    {
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            return common.DirectoryExists(path);
        }
        finally
        {
            release();
        }
    }

    public bool FileExists(string path)
    {
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            return common.FileExists(path);
        }
        finally
        {
            release();
        }
    }

    public Entries GetAccessibleEntries(string path)
    {
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            return common.GetAccessibleEntries(path);
        }
        finally
        {
            release();
        }
    }

    public IFileInfo? Stat(string path)
    {
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            return common.Stat(path);
        }
        finally
        {
            release();
        }
    }

    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        // os.go:118-151 — wrap walkFn so each callback invocation is limited by blockingOpSema
        // (the sync.Pool is a pure allocation optimization; omitted per file-header PORT-NOTE).
        Exception? Walker(string path, IDirEntry? d, Exception? err)
        {
            var release = OsVfsUtil.BlockingOpSema.Acquire();
            try
            {
                return walkFn(path, d, err);
            }
            finally
            {
                release();
            }
        }
        return common.WalkDir(root, Walker);
    }

    public string Realpath(string path)
    {
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            return OsVfsUtil.OsFSRealpath(path);
        }
        finally
        {
            release();
        }
    }

    public void WriteFile(string path, string content)
    {
        // os.go:210-212 — O_WRONLY|O_CREATE|O_TRUNC.
        WriteFileEnsuringDir(path, content, append: false);
    }

    public void AppendFile(string path, string content)
    {
        // os.go:214-216 — O_WRONLY|O_CREATE|O_APPEND.
        WriteFileEnsuringDir(path, content, append: true);
    }

    public void Remove(string path)
    {
        // os.go:218-222 — os.RemoveAll (no error if the path is absent).
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            release();
        }
    }

    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        // os.go:224-227 — os.Chtimes.
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            if (Directory.Exists(path))
            {
                Directory.SetLastAccessTimeUtc(path, aTime);
                Directory.SetLastWriteTimeUtc(path, mTime);
            }
            else
            {
                File.SetLastAccessTimeUtc(path, aTime);
                File.SetLastWriteTimeUtc(path, mTime);
            }
        }
        finally
        {
            release();
        }
    }

    // os.go:178-192 — writeFileWithFlag.
    private static void WriteFileWithFlag(string path, string content, bool append)
    {
        var release = OsVfsUtil.WriteSema.Acquire();
        try
        {
            using var stream = new FileStream(path, append ? System.IO.FileMode.Append : System.IO.FileMode.Create, FileAccess.Write);
            // Go: file.WriteString(content) writes the string's raw (UTF-8) bytes.
            byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            release();
        }
    }

    // os.go:194-197 — ensureDirectoryExists (os.MkdirAll 0o777).
    private static void EnsureDirectoryExists(string directoryPath)
    {
        var release = OsVfsUtil.BlockingOpSema.Acquire();
        try
        {
            Directory.CreateDirectory(directoryPath);
        }
        finally
        {
            release();
        }
    }

    // os.go:199-208 — writeFileEnsuringDir.
    private static void WriteFileEnsuringDir(string path, string content, bool append)
    {
        _ = Common.RootLength(path); // Assert path is rooted
        try
        {
            WriteFileWithFlag(path, content, append);
            return;
        }
        catch (IOException)
        {
            // Go: any write error falls through to mkdirAll + retry (05-conventions.md §4.2:
            // IOException is the Go-error equivalence class for the write family).
        }
        EnsureDirectoryExists(TsPathApi.GetDirectoryPath(TsPathApi.NormalizePath(path)));
        WriteFileWithFlag(path, content, append);
    }
}
