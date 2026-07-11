using System;
using System.Collections.Generic;
using System.IO;

namespace Ts.NativePath;

// Go: internal/nativepath (realpath_*.go, symlink_*.go per platform).
// PORT-NOTE (plan/06 nativepath row): interface preserved, implementation swapped.
// Go's per-OS syscall optimizations (fcntl F_GETPATH on darwin, /proc/self/fd on
// linux, GetFinalPathNameByHandle on windows) all compute the same result as
// path/filepath.EvalSymlinks; here a single BCL implementation covers all
// platforms via File/Directory.LinkTarget (readlink) and FileAttributes.
public static class NativePath
{
    // Go: nativepath.Realpath(path) (string, error) — a fully symlink-free absolute
    // native path, or null on failure (the Go error case). Caller applies FromSlash.
    public static string? Realpath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
        return EvalSymlinks(full);
    }

    // Go: nativepath.IsSymlinkOrReparsePoint(path) — true when path is a symlink
    // (Unix) or any reparse point (Windows). `path` is an OS-native path.
    public static bool IsSymlinkOrReparsePoint(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    // path/filepath.EvalSymlinks, expressed with BCL readlink (LinkTarget). `full`
    // must already be absolute and native-separated (Path.GetFullPath output).
    // Bounded by an ELOOP-style link counter (255, matching typical kernel limits).
    private static string? EvalSymlinks(string full)
    {
        char sep = Path.DirectorySeparatorChar;
        string root = Path.GetPathRoot(full) ?? "";
        if (root.Length == 0)
        {
            return null; // must be absolute
        }

        var comps = new List<string>(full[root.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries));
        string resolved = root;
        int links = 0;
        int i = 0;
        while (i < comps.Count)
        {
            string comp = comps[i];
            i++;
            if (comp == ".")
            {
                continue;
            }
            if (comp == "..")
            {
                resolved = ParentDir(resolved, root, sep);
                continue;
            }

            string cand = AppendComp(resolved, comp, sep);
            string? target = ReadLink(cand);
            if (target == null)
            {
                resolved = cand;
                continue;
            }

            // It's a symlink.
            if (++links > 255)
            {
                return null; // too many links (ELOOP)
            }

            string linkFull;
            try
            {
                linkFull = Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(AppendComp(resolved, target, sep));
            }
            catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
            {
                return null;
            }

            string tRoot = Path.GetPathRoot(linkFull) ?? "";
            if (tRoot.Length == 0)
            {
                return null;
            }
            var rest = comps.GetRange(i, comps.Count - i);
            comps = new List<string>(linkFull[tRoot.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries));
            comps.AddRange(rest);
            resolved = tRoot;
            i = 0;
        }
        return resolved;
    }

    private static string AppendComp(string dir, string comp, char sep)
    {
        if (dir.Length > 0 && (dir[^1] == '/' || dir[^1] == '\\'))
        {
            return dir + comp;
        }
        return dir + sep + comp;
    }

    private static string ParentDir(string resolved, string root, char sep)
    {
        if (resolved.Length <= root.Length)
        {
            return root;
        }
        int idx = resolved.LastIndexOf(sep);
        if (idx < root.Length)
        {
            return root;
        }
        return resolved[..idx];
    }

    // Immediate symlink target of `path` (readlink semantics), or null if `path`
    // is not a link / does not exist.
    private static string? ReadLink(string path)
    {
        try
        {
            string? t = new FileInfo(path).LinkTarget;
            if (t != null)
            {
                return t;
            }
            return new DirectoryInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
