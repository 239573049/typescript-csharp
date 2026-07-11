// Ported from tsgo internal/vfs/vfstest/vfstest.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// Go layers a wrapper over stdlib testing/fstest.MapFS; the BCL has no fstest.MapFS, so this
// is a hand-written in-memory FS (plan/06 §2.6: "测试内存 FS (Go 基于 fstest.MapFS + unsafe)
// → 手写内存 FS", following the FakeFs precedent). It implements the provider surface
// (IFileProvider + IRealpathFileProvider + IWritableFileProvider) and is wrapped into a
// vfs.FS via IoVfsUtil.From, exactly as Go does.
//
// PORT-NOTES:
//  - fstest.MapFS synthesizes directories on the fly; here convertMapFS materializes every
//    intermediate directory explicitly (mkdirAll), so only the root "." is synthesized.
//  - The provider read surface (Stat/ReadDir/ReadFile/TryRealpath) is nullable/Try-shaped
//    (Ts.Vfs.FileProvider contract); Go's error strings on that surface (e.g. "file does not
//    exist") therefore become null/false. The exact Go error strings survive only on the
//    write family (thrown IOException) and the construction panics (InvalidOperationException),
//    which is where the tests assert them.
//  - unsafe.Slice/unsafe.StringData (zero-copy string↔bytes) has no counterpart; string data
//    round-trips through UTF-8, matching Common.DecodeBytes on read.
//  - getFollowingSymlinks iterates the symlink map in sorted key order (map-iter: 序无关 —
//    at most one symlink is a directory-prefix of a given path in practice; sorting only
//    makes the traversal deterministic).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.Vfs;
using Ts.Vfs.IoVfs;
using TsPathApi = Ts.TsPath.TsPath;

namespace Ts.Vfs.VfsTest;

// vfstest.go:37-52 — Clock.
public interface IClock
{
    DateTime Now();
    TimeSpan SinceStart();
}

internal sealed class ClockImpl : IClock
{
    private readonly DateTime start = DateTime.UtcNow;

    public DateTime Now() => DateTime.UtcNow;
    public TimeSpan SinceStart() => DateTime.UtcNow - start;
}

// Go: testing/fstest.MapFile — a single file/dir/symlink entry.
public sealed class MapFile
{
    public byte[]? Data { get; set; }
    public FileMode Mode { get; set; }
    public DateTime ModTime { get; set; }
    public object? Sys { get; set; }
}

// vfstest.go:59-62 — the sys wrapper attached to every entry (carries realpath + original Sys).
internal sealed class Sys
{
    public object? Original { get; }
    public string Realpath { get; }

    public Sys(object? original, string realpath)
    {
        Original = original;
        Realpath = realpath;
    }
}

// vfstest.go:248-259 — a resolution failure. null = success; Broken distinguishes
// brokenSymlinkError from fs.ErrNotExist (errors.Is(err, ErrNotExist)).
internal sealed class FollowError
{
    public bool Broken { get; }
    public string From { get; }
    public string To { get; }

    private FollowError(bool broken, string from, string to)
    {
        Broken = broken;
        From = from;
        To = to;
    }

    public static readonly FollowError NotExist = new(false, "", "");

    public static FollowError BrokenSymlink(string from, string to) => new(true, from, to);

    public bool IsNotExist => !Broken;

    // vfstest.go:252-254 / fs.ErrNotExist message.
    public string ErrorMessage => Broken ? $"broken symlink \"{From}\" -> \"{To}\"" : "file does not exist";
}

public static class VfsTestUtil
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    internal static byte[] Utf8(string s) => Utf8NoBom.GetBytes(s);

    internal static string Utf8ToString(byte[]? b) => b == null ? "" : Utf8NoBom.GetString(b);

    // vfstest.go:237-242 — Symlink creates a symlink MapFile pointing at target.
    public static MapFile Symlink(string target)
    {
        return new MapFile
        {
            Data = Utf8(target),
            Mode = FileMode.Symlink,
        };
    }

    // vfstest.go:64-72 — FromMap creates a vfs.FS from a map of paths to file contents
    // (string, byte[], or MapFile). Paths must be normalized absolute tspath paths without
    // trailing separators, and all POSIX-style or all Windows-style.
    public static IFileSystem FromMap<TFile>(IReadOnlyDictionary<string, TFile>? m, bool useCaseSensitiveFileNames)
    {
        return FromMapWithClock(m, useCaseSensitiveFileNames, new ClockImpl());
    }

    // vfstest.go:74-141 — FromMapWithClock.
    public static IFileSystem FromMapWithClock<TFile>(IReadOnlyDictionary<string, TFile>? m, bool useCaseSensitiveFileNames, IClock clock)
    {
        bool posix = false;
        bool windows = false;

        void CheckPath(string p)
        {
            if (!TsPathApi.IsRootedDiskPath(p))
            {
                throw new InvalidOperationException($"non-rooted path \"{p}\"");
            }
            if (TsPathApi.RemoveTrailingDirectorySeparator(TsPathApi.NormalizePath(p)) != p)
            {
                throw new InvalidOperationException($"non-normalized path \"{p}\"");
            }
            if (p.StartsWith('/'))
            {
                posix = true;
            }
            else
            {
                windows = true;
            }
        }

        var mfs = new Dictionary<string, MapFile>(StringComparer.Ordinal);
        // Sorted creation to ensure times are always guaranteed to be in order.
        var keys = m == null ? new List<string>() : new List<string>(m.Keys);
        keys.Sort(ComparePathsByParts);
        foreach (var p in keys)
        {
            object? f = m![p];
            CheckPath(p);

            MapFile file;
            switch (f)
            {
                case string s:
                    file = new MapFile { Data = Utf8(s), ModTime = clock.Now() };
                    break;
                case byte[] bytes:
                    file = new MapFile { Data = bytes, ModTime = clock.Now() };
                    break;
                case MapFile mf:
                    file = CloneMapFile(mf);
                    file.ModTime = clock.Now();
                    break;
                default:
                    // Go: fmt.Sprintf("invalid file type %T", f) — %T is "int" etc.; the CLR
                    // type name (e.g. "System.Int32") is used instead (registered deviation).
                    throw new InvalidOperationException($"invalid file type {(f?.GetType()?.ToString() ?? "<null>")}");
            }

            if ((file.Mode & FileMode.Symlink) != 0)
            {
                string target = Utf8ToString(file.Data);
                CheckPath(target);

                target = target.StartsWith('/') ? target[1..] : target;
                var fileCopy = CloneMapFile(file);
                fileCopy.Data = Utf8(target);
                file = fileCopy;
            }

            string key = p.StartsWith('/') ? p[1..] : p;
            mfs[key] = file;
        }

        if (posix && windows)
        {
            throw new InvalidOperationException("mixed posix and windows paths");
        }

        return IoVfsUtil.From(ConvertMapFS(mfs, useCaseSensitiveFileNames, clock), useCaseSensitiveFileNames);
    }

    // vfstest.go:143-185 — convertMapFS.
    public static MapFS ConvertMapFS(IReadOnlyDictionary<string, MapFile> input, bool useCaseSensitiveFileNames, IClock? clock)
    {
        clock ??= new ClockImpl();
        var m = new MapFS(useCaseSensitiveFileNames, clock);

        // Verify that the input is well-formed.
        var canonicalPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var checkKeys = new List<string>(input.Keys);
        checkKeys.Sort(ComparePathsByParts);
        foreach (var path in checkKeys)
        {
            string canonical = m.GetCanonicalPath(path);
            if (canonicalPaths.TryGetValue(canonical, out string? other))
            {
                // Ensure consistent panic messages
                string a = string.CompareOrdinal(path, other) <= 0 ? path : other;
                string b = string.CompareOrdinal(path, other) <= 0 ? other : path;
                throw new InvalidOperationException($"duplicate path: \"{a}\" and \"{b}\" have the same canonical path");
            }
            canonicalPaths[canonical] = path;
        }

        // Sort the input by depth and path so parent dirs are created before their children.
        var inputKeys = new List<string>(input.Keys);
        inputKeys.Sort(ComparePathsByParts);

        foreach (var p in inputKeys)
        {
            var file = input[p];

            // Create all missing intermediate directories so we can attach the realpath to each.
            string dir = DirName(p);
            if (dir != "")
            {
                try
                {
                    m.MkdirAllImpl(dir, (FileMode)0b_111_111_111 /* 0o777 */);
                }
                catch (IOException e)
                {
                    throw new InvalidOperationException($"failed to create intermediate directories for \"{p}\": {e.Message}");
                }
            }
            m.SetEntry(p, m.GetCanonicalPath(p), CloneMapFile(file));
        }

        return m;
    }

    internal static MapFile CloneMapFile(MapFile f)
    {
        return new MapFile { Data = f.Data, Mode = f.Mode, ModTime = f.ModTime, Sys = f.Sys };
    }

    // vfstest.go:187-202 — comparePathsByParts compares path strings component by component.
    internal static int ComparePathsByParts(string a, string b)
    {
        while (true)
        {
            (string aStart, string aEnd, bool aOk) = Cut(a, '/');
            (string bStart, string bEnd, bool bOk) = Cut(b, '/');

            if (!aOk || !bOk)
            {
                return string.CompareOrdinal(a, b);
            }

            int r = string.CompareOrdinal(aStart, bStart);
            if (r != 0)
            {
                return r;
            }

            a = aEnd;
            b = bEnd;
        }
    }

    // Go: strings.Cut(s, "/").
    private static (string Before, string After, bool Found) Cut(string s, char sep)
    {
        int i = s.IndexOf(sep);
        if (i < 0)
        {
            return (s, "", false);
        }
        return (s[..i], s[(i + 1)..], true);
    }

    // vfstest.go:315-318 — dirName.
    internal static string DirName(string p)
    {
        var (dir, _) = PathSplit(p);
        return dir.EndsWith('/') ? dir[..^1] : dir; // Go: strings.TrimSuffix(dir, "/")
    }

    // vfstest.go:320-323 — baseName.
    internal static string BaseName(string p)
    {
        var (_, file) = PathSplit(p);
        return file;
    }

    // Go: path.Split — splits immediately after the final slash.
    private static (string Dir, string File) PathSplit(string p)
    {
        int i = p.LastIndexOf('/');
        return (p[..(i + 1)], p[(i + 1)..]);
    }
}

// vfstest.go:22-35 — MapFS. keys in m are canonicalPaths (no leading slash); realpath is
// carried per-entry in Sys.
public sealed class MapFS : IRealpathFileProvider, IWritableFileProvider
{
    private const uint Umask = 0b_000_010_010; // 0o022

    private readonly object gate = new();
    private readonly Dictionary<string, MapFile> m = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> symlinks = new(StringComparer.Ordinal);
    private readonly bool useCaseSensitiveFileNames;
    private readonly IClock clock;

    internal MapFS(bool useCaseSensitiveFileNames, IClock clock)
    {
        this.useCaseSensitiveFileNames = useCaseSensitiveFileNames;
        this.clock = clock;
    }

    // vfstest.go:206-208 — getCanonicalPath.
    internal string GetCanonicalPath(string p)
    {
        return TsPathApi.GetCanonicalFileName(p, useCaseSensitiveFileNames);
    }

    // vfstest.go:244-282 — getFollowingSymlinks resolves a canonical path through any symlinks.
    private (MapFile? File, string Canonical, FollowError? Err) GetFollowingSymlinks(string p)
    {
        return GetFollowingSymlinksWorker(p, "", "");
    }

    private (MapFile? File, string Canonical, FollowError? Err) GetFollowingSymlinksWorker(string p, string symlinkFrom, string symlinkTo)
    {
        if (m.TryGetValue(p, out var file) && (file.Mode & FileMode.Symlink) == 0)
        {
            return (file, p, null);
        }

        if (symlinks.TryGetValue(p, out var target))
        {
            return GetFollowingSymlinksWorker(target, p, target);
        }

        // This could be a path underneath a symlinked directory.
        // map-iter: 序无关 — sorted for determinism (see file header).
        var keys = new List<string>(symlinks.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var other in keys)
        {
            string tgt = symlinks[other];
            if (other.Length < p.Length && p.AsSpan(0, other.Length).SequenceEqual(other.AsSpan()) && p[other.Length] == '/')
            {
                return GetFollowingSymlinksWorker(tgt + p[other.Length..], other, tgt);
            }
        }

        FollowError err = symlinkFrom != "" ? FollowError.BrokenSymlink(symlinkFrom, symlinkTo) : FollowError.NotExist;
        return (null, p, err);
    }

    // vfstest.go:284-305 — set / setEntry.
    internal void SetEntry(string realpath, string canonical, MapFile file)
    {
        if (realpath == "" || canonical == "")
        {
            throw new InvalidOperationException("empty path");
        }

        file.Sys = new Sys(file.Sys, realpath);
        m[canonical] = file;

        if ((file.Mode & FileMode.Symlink) != 0)
        {
            symlinks[canonical] = GetCanonicalPath(VfsTestUtil.Utf8ToString(file.Data));
        }
    }

    // vfstest.go:307-313 — splitPath. ("Rest" is a reserved tuple-element name in C#, CS8126.)
    private static (string Dir, string RestPath) SplitPath(string s, int offset)
    {
        int idx = s.IndexOf('/', offset);
        if (idx < 0)
        {
            return (s, "");
        }
        return (s[..idx], s[(idx + 1)..]);
    }

    // vfstest.go:325-375 — mkdirAll (no lock). Throws IOException on failure.
    internal void MkdirAllImpl(string p, FileMode perm)
    {
        if (p == "")
        {
            throw new InvalidOperationException("empty path");
        }

        // Fast path; already exists.
        var (other0, _, err0) = GetFollowingSymlinks(GetCanonicalPath(p));
        if (err0 == null)
        {
            if (!other0!.Mode.IsDir())
            {
                throw new IOException($"mkdir \"{p}\": path exists but is not a directory");
            }
            return;
        }

        var toCreate = new List<string>();
        int offset = 0;
        while (true)
        {
            var (dir, rest) = SplitPath(p, offset);
            string canonical = GetCanonicalPath(dir);
            var (other, otherPath, err) = GetFollowingSymlinks(canonical);
            if (err != null)
            {
                if (!err.IsNotExist)
                {
                    throw new IOException(err.ErrorMessage);
                }
                toCreate.Add(dir);
            }
            else
            {
                if (!other!.Mode.IsDir())
                {
                    throw new IOException($"mkdir \"{otherPath}\": path exists but is not a directory");
                }
                if (canonical != otherPath)
                {
                    // We have a symlinked parent, reset and start again.
                    p = ((Sys)other.Sys!).Realpath + "/" + rest;
                    toCreate.Clear();
                    offset = 0;
                    continue;
                }
            }
            if (rest == "")
            {
                break;
            }
            offset = dir.Length + 1;
        }

        foreach (var dir in toCreate)
        {
            SetEntry(dir, GetCanonicalPath(dir), new MapFile
            {
                Mode = FileMode.Dir | (FileMode)((uint)perm & ~Umask),
                ModTime = clock.Now(),
            });
        }
    }

    // ---- IFileProvider (fs.FS read surface) ----

    // vfstest.go:428-468 — Open + convertInfo, expressed directly as Stat (follows symlinks).
    public IFileInfo? Stat(string name)
    {
        lock (gate)
        {
            var (file, _, err) = GetFollowingSymlinks(GetCanonicalPath(name));
            if (err != null)
            {
                if (name == ".")
                {
                    // Synthesized root directory (fstest.MapFS synthesizes "."); realpath ".".
                    return new VfsTestFileInfo(".", 0, FileMode.Dir, clock.Now(), true, null);
                }
                return null;
            }
            var sys = (Sys)file!.Sys!;
            return new VfsTestFileInfo(VfsTestUtil.BaseName(sys.Realpath), file.Data?.Length ?? 0, file.Mode, file.ModTime, file.Mode.IsDir(), sys.Original);
        }
    }

    public IReadOnlyList<IDirEntry>? ReadDir(string name)
    {
        lock (gate)
        {
            var (file, cp, err) = GetFollowingSymlinks(GetCanonicalPath(name));
            string dirCanonical;
            if (err != null)
            {
                if (name == ".")
                {
                    dirCanonical = ".";
                }
                else
                {
                    return null;
                }
            }
            else
            {
                if (!file!.Mode.IsDir())
                {
                    return null; // ReadDir on a non-directory
                }
                dirCanonical = cp;
            }

            string prefix = dirCanonical == "." ? "" : dirCanonical + "/";
            var children = new List<(string CanonBase, MapFile File)>();
            foreach (var (k, f) in m)
            {
                if (dirCanonical == ".")
                {
                    if (k.IndexOf('/') < 0)
                    {
                        children.Add((k, f));
                    }
                }
                else if (k.Length > prefix.Length && k.StartsWith(prefix, StringComparison.Ordinal) && k.IndexOf('/', prefix.Length) < 0)
                {
                    children.Add((k[prefix.Length..], f));
                }
            }
            children.Sort(static (a, b) => string.CompareOrdinal(a.CanonBase, b.CanonBase));

            var result = new List<IDirEntry>(children.Count);
            foreach (var (_, f) in children)
            {
                var sys = (Sys)f.Sys!;
                result.Add(new VfsTestDirEntry(VfsTestUtil.BaseName(sys.Realpath), f.Mode, f.Data?.Length ?? 0, f.ModTime, sys.Original));
            }
            return result;
        }
    }

    public byte[]? ReadFile(string name)
    {
        lock (gate)
        {
            var (file, _, err) = GetFollowingSymlinks(GetCanonicalPath(name));
            if (err != null || !file!.Mode.IsRegular())
            {
                return null;
            }
            return file.Data ?? Array.Empty<byte>();
        }
    }

    // ---- IRealpathFileProvider ----

    // vfstest.go:470-479 — Realpath.
    public bool TryRealpath(string name, out string realpath)
    {
        lock (gate)
        {
            var (file, _, err) = GetFollowingSymlinks(GetCanonicalPath(name));
            if (err != null)
            {
                realpath = "";
                return false;
            }
            realpath = ((Sys)file!.Sys!).Realpath;
            return true;
        }
    }

    // ---- IWritableFileProvider ----

    // vfstest.go:495-500 — MkdirAll.
    public void MkdirAll(string path, FileMode perm)
    {
        lock (gate)
        {
            MkdirAllImpl(path, perm);
        }
    }

    // vfstest.go:502-511 — AddSymlink.
    public void AddSymlink(string path, string target)
    {
        lock (gate)
        {
            string canonical = GetCanonicalPath(path);
            SetEntry(path, canonical, new MapFile
            {
                Data = VfsTestUtil.Utf8(target),
                Mode = FileMode.Symlink,
            });
        }
    }

    // vfstest.go:513-547 — WriteFile.
    public void WriteFile(string path, string data, FileMode perm)
    {
        lock (gate)
        {
            string parent = VfsTestUtil.DirName(path);
            if (parent != "")
            {
                var (parentFile, _, perr) = GetFollowingSymlinks(GetCanonicalPath(parent));
                if (perr != null)
                {
                    throw new IOException($"write \"{path}\": {perr.ErrorMessage}");
                }
                if (!parentFile!.Mode.IsDir())
                {
                    throw new IOException($"write \"{path}\": parent path exists but is not a directory");
                }
            }

            var (file, cp, err) = GetFollowingSymlinks(GetCanonicalPath(path));
            if (err == null && !file!.Mode.IsRegular())
            {
                throw new IOException($"write \"{path}\": path exists but is not a regular file");
            }

            SetEntry(path, cp, new MapFile
            {
                Data = VfsTestUtil.Utf8(data),
                ModTime = clock.Now(),
                Mode = (FileMode)((uint)perm & ~Umask),
            });
        }
    }

    // vfstest.go:549-596 — AppendFile.
    public void AppendFile(string path, string data, FileMode perm)
    {
        lock (gate)
        {
            string parent = VfsTestUtil.DirName(path);
            if (parent != "")
            {
                var (parentFile, _, perr) = GetFollowingSymlinks(GetCanonicalPath(parent));
                if (perr != null)
                {
                    throw new IOException($"append \"{path}\": {perr.ErrorMessage}");
                }
                if (!parentFile!.Mode.IsDir())
                {
                    throw new IOException($"append \"{path}\": parent path exists but is not a directory");
                }
            }

            byte[] existing = Array.Empty<byte>();
            FileMode existingMode = FileMode.None;
            var (file, cp, err) = GetFollowingSymlinks(GetCanonicalPath(path));
            if (err == null)
            {
                if (!file!.Mode.IsRegular())
                {
                    throw new IOException($"append \"{path}\": path exists but is not a regular file");
                }
                existing = file.Data ?? Array.Empty<byte>();
                existingMode = file.Mode;
            }

            byte[] dataBytes = VfsTestUtil.Utf8(data);
            var combined = new byte[existing.Length + dataBytes.Length];
            Array.Copy(existing, 0, combined, 0, existing.Length);
            Array.Copy(dataBytes, 0, combined, existing.Length, dataBytes.Length);

            FileMode mode = existingMode;
            if (mode == FileMode.None)
            {
                mode = (FileMode)((uint)perm & ~Umask);
            }

            SetEntry(path, cp, new MapFile
            {
                Data = combined,
                ModTime = clock.Now(),
                Mode = mode,
            });
        }
    }

    // vfstest.go:214-235 / 598-603 — Remove.
    public void Remove(string path)
    {
        lock (gate)
        {
            string canonical = GetCanonicalPath(path);
            if (!m.TryGetValue(canonical, out var fileInfo))
            {
                // file does not exist
                return;
            }
            m.Remove(canonical);
            symlinks.Remove(canonical);

            if (fileInfo.Mode.IsDir())
            {
                string prefix = canonical + "/";
                var toRemove = new List<string>();
                foreach (var k in m.Keys)
                {
                    if (k.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        toRemove.Add(k);
                    }
                }
                foreach (var k in toRemove)
                {
                    m.Remove(k);
                    symlinks.Remove(k);
                }
            }
        }
    }

    // vfstest.go:605-617 — Chtimes.
    public void Chtimes(string path, DateTime aTime, DateTime mTime)
    {
        lock (gate)
        {
            string canonical = GetCanonicalPath(path);
            if (!m.TryGetValue(canonical, out var fileInfo))
            {
                throw new FileNotFoundException("file does not exist");
            }
            fileInfo.ModTime = mTime;
        }
    }

    // vfstest.go:619-631 — GetTargetOfSymlink.
    public bool GetTargetOfSymlink(string path, out string target)
    {
        lock (gate)
        {
            string p = path.StartsWith('/') ? path[1..] : path;
            string canonical = GetCanonicalPath(p);
            if (m.TryGetValue(canonical, out var fileInfo) && (fileInfo.Mode & FileMode.Symlink) != 0)
            {
                target = "/" + VfsTestUtil.Utf8ToString(fileInfo.Data);
                return true;
            }
            target = "";
            return false;
        }
    }

    // vfstest.go:633-643 — GetModTime.
    public DateTime GetModTime(string path)
    {
        lock (gate)
        {
            string p = path.StartsWith('/') ? path[1..] : path;
            string canonical = GetCanonicalPath(p);
            if (m.TryGetValue(canonical, out var fileInfo))
            {
                return fileInfo.ModTime;
            }
            return default;
        }
    }

    // vfstest.go:645-663 — Entries (realpath, MapFile) in sorted order.
    public IEnumerable<(string Path, MapFile File)> Entries()
    {
        lock (gate)
        {
            var inputKeys = new List<string>(m.Keys);
            inputKeys.Sort(VfsTestUtil.ComparePathsByParts);
            var result = new List<(string, MapFile)>(inputKeys.Count);
            foreach (var p in inputKeys)
            {
                var file = m[p];
                string path = ((Sys)file.Sys!).Realpath;
                if (!TsPathApi.PathIsAbsolute(path))
                {
                    path = "/" + path;
                }
                result.Add((path, file));
            }
            return result;
        }
    }

    // vfstest.go:665-672 — GetFileInfo.
    public MapFile? GetFileInfo(string path)
    {
        lock (gate)
        {
            string p = path.StartsWith('/') ? path[1..] : path;
            string canonical = GetCanonicalPath(p);
            return m.TryGetValue(canonical, out var fileInfo) ? fileInfo : null;
        }
    }
}

// vfstest.go:377-389 — fileInfo (realpath-aware).
internal sealed class VfsTestFileInfo : IFileInfo
{
    private readonly string name;
    private readonly long size;
    private readonly FileMode mode;
    private readonly DateTime modTime;
    private readonly bool isDir;
    private readonly object? sys;

    public VfsTestFileInfo(string name, long size, FileMode mode, DateTime modTime, bool isDir, object? sys)
    {
        this.name = name;
        this.size = size;
        this.mode = mode;
        this.modTime = modTime;
        this.isDir = isDir;
        this.sys = sys;
    }

    public string Name() => name;
    public long Size() => size;
    public FileMode Mode() => mode;
    public DateTime ModTime() => modTime;
    public bool IsDir() => isDir;
    public object? Sys() => sys;
}

// Go: fs.FileInfoToDirEntry(fileInfo) — a DirEntry backed by the realpath-aware info.
internal sealed class VfsTestDirEntry : IDirEntry
{
    private readonly string name;
    private readonly FileMode mode;
    private readonly long size;
    private readonly DateTime modTime;
    private readonly object? sys;

    public VfsTestDirEntry(string name, FileMode mode, long size, DateTime modTime, object? sys)
    {
        this.name = name;
        this.mode = mode;
        this.size = size;
        this.modTime = modTime;
        this.sys = sys;
    }

    public string Name() => name;
    public bool IsDir() => mode.IsDir();
    public FileMode Type() => mode.Type();
    public IFileInfo? Info() => new VfsTestFileInfo(name, size, mode, modTime, mode.IsDir(), sys);
}
