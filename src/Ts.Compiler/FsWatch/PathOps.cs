// Ported from tsgo internal/fswatch/watcher.go (the path predicates and
// rebasing helpers: isInDirectoryOrSelf, isDirectChild, rebasePath,
// joinPathSuffix) plus the minimal path/filepath surface fswatch uses
// (filepath.Clean/IsAbs/Dir, os.IsPathSeparator).
//
// PORT-NOTE: 非 1:1 backend, but these helpers ARE 1:1 with the Go logic — they
// are pure string operations on OS-native paths. fswatch operates on native
// paths (it drives OS watcher APIs), so it uses path/filepath / os semantics,
// NOT the forward-slash-normalized Ts.TsPath package (which the Go source also
// does not import — plan/06 §8.5: dependency is nativepath only). These map to
// System.IO.Path (OS-native, same separator semantics as Go's path/filepath).

using System;
using System.IO;

namespace Ts.FsWatch;

internal static class PathOps
{
    // Go: os.IsPathSeparator(c). Both '/' and the OS separator count (Windows
    // accepts '/' as well); matches Go's os.IsPathSeparator on each platform.
    public static bool IsPathSeparator(char c)
    {
        return c == '/' || c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
    }

    // Go: isInDirectoryOrSelf(dir, path) — path == dir, or path is under dir at
    // a separator boundary.
    public static bool IsInDirectoryOrSelf(string dir, string path)
    {
        if (dir.Length == 0)
        {
            return false;
        }
        if (path == dir)
        {
            return true;
        }
        if (!path.StartsWith(dir, StringComparison.Ordinal))
        {
            return false;
        }
        string rest = path.Substring(dir.Length);
        if (rest.Length == 0)
        {
            return false;
        }
        if (IsPathSeparator(dir[dir.Length - 1]))
        {
            return true;
        }
        return IsPathSeparator(rest[0]);
    }

    // Go: isDirectChild(dir, path) — path is an immediate child of dir. Both
    // absolute. False for path == dir.
    public static bool IsDirectChild(string dir, string path)
    {
        if (!path.StartsWith(dir, StringComparison.Ordinal))
        {
            return false;
        }
        string rest = path.Substring(dir.Length);
        if (rest.Length == 0)
        {
            return false;
        }
        if (rest[0] != '/' && rest[0] != Path.DirectorySeparatorChar)
        {
            return false;
        }
        rest = rest.Substring(1);
        if (rest.Length == 0)
        {
            return false;
        }
        return rest.IndexOf('/') < 0 && rest.IndexOf(Path.DirectorySeparatorChar) < 0;
    }

    // Go: rebasePath(path, from, to) — replace the `from` root in path with `to`,
    // preserving any child suffix. Prefix matches must end at a separator.
    public static string RebasePath(string path, string from, string to)
    {
        if (from == to)
        {
            return path;
        }
        if (path == from)
        {
            return to;
        }
        if (!path.StartsWith(from, StringComparison.Ordinal))
        {
            return path;
        }
        string suffix = path.Substring(from.Length);
        if (from.Length > 0 && IsPathSeparator(from[from.Length - 1]))
        {
            return JoinPathSuffix(to, suffix);
        }
        if (suffix.Length == 0 || !IsPathSeparator(suffix[0]))
        {
            return path;
        }
        return JoinPathSuffix(to, suffix);
    }

    // Go: joinPathSuffix(root, suffix).
    public static string JoinPathSuffix(string root, string suffix)
    {
        if (suffix.Length == 0)
        {
            return root;
        }
        if (IsPathSeparator(suffix[0]))
        {
            if (root.Length > 0 && IsPathSeparator(root[root.Length - 1]))
            {
                return root + suffix.Substring(1);
            }
            return root + suffix;
        }
        if (root.Length > 0 && IsPathSeparator(root[root.Length - 1]))
        {
            return root + suffix;
        }
        return root + Path.DirectorySeparatorChar + suffix;
    }
}

// PathUtil mirrors the tiny slice of Go's path/filepath that fswatch uses:
// Clean, IsAbs, Dir. OS-native semantics (BCL System.IO.Path).
internal static class PathUtil
{
    // Go: filepath.Clean — lexical cleanup. Path.GetFullPath would resolve
    // against cwd (wrong: Go's Clean is purely lexical and does not require an
    // absolute input). We emulate filepath.Clean's lexical normalization.
    public static string Clean(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        char sep = Path.DirectorySeparatorChar;
        bool rooted = IsAbs(path);
        string volume = GetVolumeName(path);
        string rest = path.Substring(volume.Length);

        var outParts = new System.Collections.Generic.List<string>();
        int i = 0;
        int n = rest.Length;
        while (i < n)
        {
            // skip separators
            if (IsSep(rest[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < n && !IsSep(rest[i]))
            {
                i++;
            }
            string comp = rest.Substring(start, i - start);
            if (comp == ".")
            {
                continue;
            }
            if (comp == "..")
            {
                if (outParts.Count > 0 && outParts[outParts.Count - 1] != "..")
                {
                    outParts.RemoveAt(outParts.Count - 1);
                }
                else if (!rooted)
                {
                    outParts.Add("..");
                }
                // if rooted, .. at root is dropped
                continue;
            }
            outParts.Add(comp);
        }

        string body = string.Join(sep, outParts);
        string prefix = volume;
        if (rooted)
        {
            // Ensure a leading separator after the volume.
            if (volume.Length == 0)
            {
                prefix = sep.ToString();
            }
            else if (volume.Length > 0 && !IsSep(volume[volume.Length - 1]))
            {
                prefix = volume + sep;
            }
        }

        string result = prefix + body;
        if (result.Length == 0)
        {
            return ".";
        }
        return result;
    }

    // Go: filepath.IsAbs.
    public static bool IsAbs(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            // Drive-absolute (C:\...) or UNC (\\server\share).
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && IsSep(path[2]))
            {
                return true;
            }
            if (path.Length >= 2 && IsSep(path[0]) && IsSep(path[1]))
            {
                return true;
            }
            return false;
        }
        return IsSep(path[0]);
    }

    // Go: filepath.Dir — the all-but-last element, cleaned. If the path is empty,
    // returns ".". Trailing separators are removed before taking the parent.
    public static string Dir(string path)
    {
        string volume = GetVolumeName(path);
        int i = path.Length - 1;
        while (i >= volume.Length && !IsSep(path[i]))
        {
            i--;
        }
        string dir = Clean(path.Substring(0, i + 1));
        if (dir == "." && volume.Length > 0)
        {
            return volume;
        }
        return dir;
    }

    private static bool IsSep(char c)
    {
        if (OperatingSystem.IsWindows())
        {
            return c == '\\' || c == '/';
        }
        return c == '/';
    }

    // Windows volume name ("C:", "\\server\share"), empty on Unix.
    private static string GetVolumeName(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "";
        }
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
        {
            return path.Substring(0, 2);
        }
        return "";
    }
}
