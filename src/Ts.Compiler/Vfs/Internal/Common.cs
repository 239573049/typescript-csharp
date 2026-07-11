// Ported from tsgo internal/vfs/internal/internal.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
//
// PORT-NOTE: internal.go:165's unsafe.String zero-copy view has no counterpart — C#
// byte[]→string always transcodes (plan/06 §2.6). DecodeBytes therefore takes the raw
// bytes; its BOM sniffing and UTF-16 decoding replicate Go byte-for-byte (see method
// notes), and the no-BOM path transcodes UTF-8 → UTF-16 at this boundary per the
// repo-wide UTF-16 decision (plan/01).

using System;
using System.Collections.Generic;
using System.Text;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.Vfs.Internal;

public sealed class Common
{
    // Go: RootFor func(root string) fs.FS — nil result means the root is unavailable.
    public required Func<string, IFileProvider?> RootFor { get; init; }

    // Go: IsReparsePoint func(path string) bool — nil on non-Windows implementations.
    public Func<string, bool>? IsReparsePoint { get; init; }

    // internal.go:20-28
    public static int RootLength(string p)
    {
        int l = TsPathApi.GetEncodedRootLength(p);
        if (l == 0)
        {
            // Go: panic(fmt.Sprintf("vfs: path %q is not absolute", p)).
            // PORT-NOTE: %q's escaping of quotes/non-printables inside p is not
            // replicated; plain quoting matches for ordinary paths.
            throw new InvalidOperationException($"vfs: path \"{p}\" is not absolute");
        }
        else if (l < 0)
        {
            return ~l;
        }
        return l;
    }

    // internal.go:30-36
    // PORT-NOTE: "Rest" is a reserved tuple element name in C# (CS8126) → RestPath.
    public static (string RootName, string RestPath) SplitPath(string p)
    {
        p = TsPathApi.NormalizePath(p);
        int l = RootLength(p);
        string rootName = p[..l];
        string rest = TsPathApi.RemoveTrailingDirectorySeparator(p[l..]);
        return (rootName, rest);
    }

    // internal.go:38-44
    public (IFileProvider? Fsys, string RootName, string RestPath) RootAndPath(string path)
    {
        var (rootName, rest) = SplitPath(path);
        if (rest.Length == 0)
        {
            rest = ".";
        }
        return (RootFor(rootName), rootName, rest);
    }

    // internal.go:46-56
    public IFileInfo? Stat(string path)
    {
        var (fsys, _, rest) = RootAndPath(path);
        if (fsys == null)
        {
            return null;
        }
        return fsys.Stat(rest);
    }

    // internal.go:58-61
    public bool FileExists(string path)
    {
        var stat = Stat(path);
        return stat != null && !stat.IsDir();
    }

    // internal.go:63-66
    public bool DirectoryExists(string path)
    {
        var stat = Stat(path);
        return stat != null && stat.IsDir();
    }

    // internal.go:68-115
    public Entries GetAccessibleEntries(string path)
    {
        List<string>? files = null;
        List<string>? directories = null;
        var symlinks = new HashSet<string>(StringComparer.Ordinal);

        bool AddToResult(string name, FileMode mode, bool isLink)
        {
            if (mode.IsDir())
            {
                (directories ??= new List<string>()).Add(name);
            }
            else if (mode.IsRegular())
            {
                (files ??= new List<string>()).Add(name);
            }
            else
            {
                return false;
            }

            if (isLink)
            {
                symlinks.Add(name);
            }
            return true;
        }

        var entries = GetEntries(path);
        if (entries != null)
        {
            foreach (var entry in entries)
            {
                var entryType = entry.Type();

                if (AddToResult(entry.Name(), entryType, false))
                {
                    continue;
                }

                if ((entryType & FileMode.Symlink) != 0)
                {
                    // Easy case; UNIX-like system will clearly mark symlinks.
                    if (Stat(path + "/" + entry.Name()) is { } stat)
                    {
                        AddToResult(entry.Name(), stat.Mode(), true);
                    }
                    continue;
                }

                if ((entryType & FileMode.Irregular) != 0 && IsReparsePoint != null)
                {
                    // Could be a Windows junction or other reparse point.
                    // Check using the OS-specific helper.
                    string fullPath = path + "/" + entry.Name();
                    if (IsReparsePoint(fullPath))
                    {
                        if (Stat(fullPath) is { } stat)
                        {
                            AddToResult(entry.Name(), stat.Mode(), true);
                        }
                    }
                    continue;
                }
            }
        }

        return new Entries
        {
            Files = files?.ToArray(),
            Directories = directories?.ToArray(),
            Symlinks = symlinks,
        };
    }

    // internal.go:117-129
    private IReadOnlyList<IDirEntry>? GetEntries(string path)
    {
        var (fsys, _, rest) = RootAndPath(path);
        if (fsys == null)
        {
            return null;
        }
        return fsys.ReadDir(rest);
    }

    // internal.go:131-142
    public Exception? WalkDir(string root, WalkDirFunc walkFn)
    {
        var (fsys, rootName, rest) = RootAndPath(root);
        if (fsys == null)
        {
            return null;
        }
        return FileProvider.WalkDir(fsys, rest, (path, d, err) =>
        {
            if (path == ".")
            {
                path = "";
            }
            return walkFn(rootName + path, d, err);
        });
    }

    // internal.go:144-168
    public bool TryReadFile(string path, out string contents)
    {
        var (fsys, _, rest) = RootAndPath(path);
        if (fsys == null)
        {
            contents = "";
            return false;
        }

        byte[]? b = fsys.ReadFile(rest);
        if (b == null)
        {
            contents = "";
            return false;
        }

        // PORT-NOTE: Go views the immutable byte slice as a string without copying
        // (internal.go:155-165, unsafe.String); C# strings are UTF-16, so DecodeBytes
        // below performs the (unavoidable) transcode instead.
        if (b.Length == 0)
        {
            contents = "";
            return true;
        }

        contents = DecodeBytes(b);
        return true;
    }

    // internal.go:170-186 — decodeBytes(s) (contents string, ok bool).
    // PORT-NOTE: Go's ok is structurally always true (every branch returns true), so the
    // C# shape collapses to a plain string return.
    public static string DecodeBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2)
        {
            if (b[0] == 0xFF && b[1] == 0xFE)
            {
                return DecodeUtf16(b[2..], bigEndian: false);
            }
            if (b[0] == 0xFE && b[1] == 0xFF)
            {
                return DecodeUtf16(b[2..], bigEndian: true);
            }
        }
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            b = b[3..];
        }

        // Go returns the raw bytes as-is (a Go string is bytes, conventionally UTF-8);
        // here they become UTF-16. PORT-NOTE: for *invalid* UTF-8 input the replacement
        // granularity differs — Go yields one U+FFFD per invalid byte when the string is
        // iterated as runes, Encoding.UTF8 yields one U+FFFD per maximal invalid
        // subsequence (Unicode TR recommendation). Valid UTF-8 is unaffected; registered
        // in plan/11 §known deviations.
        return Encoding.UTF8.GetString(b);
    }

    // internal.go:188-194 — decodeUtf16(s, order). Byte-faithful replication:
    //  - ints := make([]uint16, len(s)/2): an odd trailing byte is silently dropped
    //    (binary.Read fills len/2 values and never errors here, so Go's `return ""`
    //    error branch is unreachable and has no port);
    //  - utf16.Decode: a valid high+low surrogate pair passes through; any unpaired
    //    surrogate becomes U+FFFD.
    public static string DecodeUtf16(ReadOnlySpan<byte> b, bool bigEndian)
    {
        int n = b.Length / 2;
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            char r = ReadUnit(b, i, bigEndian);
            if (r < '\uD800' || r >= '\uE000')
            {
                // normal rune
                sb.Append(r);
            }
            else if (r < '\uDC00' && i + 1 < n && ReadUnit(b, i + 1, bigEndian) is >= '\uDC00' and < '\uE000')
            {
                // valid surrogate sequence
                sb.Append(r);
                i++;
                sb.Append(ReadUnit(b, i, bigEndian));
            }
            else
            {
                // invalid surrogate sequence
                sb.Append('\uFFFD');
            }
        }
        return sb.ToString();

        static char ReadUnit(ReadOnlySpan<byte> b, int index, bool bigEndian)
        {
            byte b0 = b[index * 2];
            byte b1 = b[index * 2 + 1];
            return bigEndian ? (char)((b0 << 8) | b1) : (char)((b1 << 8) | b0);
        }
    }
}
