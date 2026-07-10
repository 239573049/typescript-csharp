// Ported from tsgo internal/tspath/path.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Text;
using StrUtil = Ts.StringUtil.StringUtil; // Go: stringutil.* (a plain `using` would be shadowed by the sibling Ts.StringUtil namespace)

namespace Ts.TsPath;

// Go: `type Path string` — the branded canonical-path key. 05-conventions.md §2.3
// (named string type with methods → readonly record struct wrapper, methods carried
// over); plan/10 path.go row. Equality is ordinal string equality on Value.
// PORT-NOTE: default(Path) has a null Value; Go's zero value is "". Members are
// null-tolerant, but code should construct Paths via ToPath/new Path(...).
public readonly record struct Path(string Value) : IComparable<Path>
{
    // PORT-NOTE: Go sorts []Path in string byte order (= code-point order), which is not
    // UTF-16 unit order when [U+E000, U+FFFF] meets supplementary planes (05 §8.4), so
    // ordering goes through StringUtil.CompareRuneOrdinal.
    public int CompareTo(Path other)
    {
        return StrUtil.CompareRuneOrdinal(Value ?? "", other.Value ?? "");
    }

    public override string ToString()
    {
        return Value ?? "";
    }

    // path.go:265 func (p Path) GetDirectoryPath() Path
    public Path GetDirectoryPath()
    {
        return new Path(TsPath.GetDirectoryPath(Value ?? ""));
    }

    // path.go:704 func (p Path) RemoveTrailingDirectorySeparator() Path
    public Path RemoveTrailingDirectorySeparator()
    {
        return new Path(TsPath.RemoveTrailingDirectorySeparator(Value ?? ""));
    }

    // path.go:723 func (p Path) EnsureTrailingDirectorySeparator() Path
    public Path EnsureTrailingDirectorySeparator()
    {
        return new Path(TsPath.EnsureTrailingDirectorySeparator(Value ?? ""));
    }

    // path.go:1038 — ContainsPath checks whether child is contained within or equal to p.
    // Since Path values are already rooted, reduced, and case-canonicalized,
    // this is a simple string prefix check.
    public bool ContainsPath(Path child)
    {
        if (string.IsNullOrEmpty(Value))
        {
            return false;
        }
        string childValue = child.Value ?? "";
        return this == child ||
            childValue.Length > Value.Length &&
            childValue.StartsWith(Value, StringComparison.Ordinal) &&
            (Value[^1] == '/' || childValue[Value.Length] == '/');
    }
}

public static partial class TsPath
{
    // Internally, we represent paths as strings with '/' as the directory separator.
    // When we make system calls (eg: LanguageServiceHost.getDirectory()),
    // we expect the host to correctly handle paths in our specified format.
    public const char DirectorySeparator = '/';
    private const string UrlSchemeSeparator = "://";

    //// Path Tests

    // Determines whether a byte corresponds to `/` or `\`.
    private static bool IsAnyDirectorySeparator(char ch)
    {
        return ch == '/' || ch == '\\';
    }

    // Determines whether a path starts with a URL scheme (e.g. starts with `http://`, `ftp://`, `file://`, etc.).
    public static bool IsUrl(string path)
    {
        return GetEncodedRootLength(path) < 0;
    }

    // Determines whether a path is an absolute disk path (e.g. starts with `/`, or a dos path
    // like `c:`, `c:\` or `c:/`).
    public static bool IsRootedDiskPath(string path)
    {
        return GetEncodedRootLength(path) > 0;
    }

    // Determines whether a path consists only of a path root.
    public static bool IsDiskPathRoot(string path)
    {
        int rootLength = GetEncodedRootLength(path);
        return rootLength > 0 && rootLength == path.Length;
    }

    // IsDynamicFileName returns true if the file name represents a dynamic/virtual file
    // that doesn't exist on disk (e.g., untitled files with paths like "^/untitled/...").
    public static bool IsDynamicFileName(string fileName)
    {
        return fileName.StartsWith("^/", StringComparison.Ordinal);
    }

    // Determines whether a path starts with an absolute path component (i.e. `/`, `c:/`, `file://`, etc.).
    //
    //	```
    //	// POSIX
    //	PathIsAbsolute("/path/to/file.ext") === true
    //	// DOS
    //	PathIsAbsolute("c:/path/to/file.ext") === true
    //	// URL
    //	PathIsAbsolute("file:///path/to/file.ext") === true
    //	// Non-absolute
    //	PathIsAbsolute("path/to/file.ext") === false
    //	PathIsAbsolute("./path/to/file.ext") === false
    //	```
    public static bool PathIsAbsolute(string path)
    {
        return GetEncodedRootLength(path) != 0;
    }

    public static bool HasTrailingDirectorySeparator(string path)
    {
        return path.Length > 0 && IsAnyDirectorySeparator(path[^1]);
    }

    // Combines paths. If a path is absolute, it replaces any previous path. Relative paths are not simplified.
    //
    //	```
    //	// Non-rooted
    //	CombinePaths("path", "to", "file.ext") === "path/to/file.ext"
    //	CombinePaths("path", "dir", "..", "to", "file.ext") === "path/dir/../to/file.ext"
    //	// POSIX
    //	CombinePaths("/path", "to", "file.ext") === "/path/to/file.ext"
    //	CombinePaths("/path", "/to", "file.ext") === "/to/file.ext"
    //	// DOS
    //	CombinePaths("c:/path", "to", "file.ext") === "c:/path/to/file.ext"
    //	CombinePaths("c:/path", "c:/to", "file.ext") === "c:/to/file.ext"
    //	// URL
    //	CombinePaths("file:///path", "to", "file.ext") === "file:///path/to/file.ext"
    //	CombinePaths("file:///path", "file:///to", "file.ext") === "file:///to/file.ext"
    //	```
    public static string CombinePaths(string firstPath, params string[] paths)
    {
        // TODO (drosen): There is potential for a fast path here.
        // In the case where we find the last absolute path and just path.Join from there.
        firstPath = NormalizeSlashes(firstPath);

        var b = new StringBuilder();
        int size = firstPath.Length + paths.Length;
        foreach (string p in paths)
        {
            size += p.Length;
        }
        b.EnsureCapacity(size); // Go: b.Grow(size)

        b.Append(firstPath);

        // To provide a way to "set" the path, keep track of the start and then slice.
        // This will waste some memory each time we do it, but saving memory is more common.
        // PORT-NOTE: Go's result() closure returns b.String()[start:] for free; C# would
        // copy on every ToString, so the two uses inside the loop are expressed as direct
        // length/last-char checks on the builder. Behavior is identical.
        int start = 0;

        foreach (string path in paths)
        {
            string trailingPath = path;
            if (trailingPath == "")
            {
                continue;
            }
            trailingPath = NormalizeSlashes(trailingPath);
            if (b.Length == start /* result() == "" */ || GetRootLength(trailingPath) != 0)
            {
                // `trailingPath` is absolute.
                // Go setResult(value): start = b.Len(); b.WriteString(value)
                start = b.Length;
                b.Append(trailingPath);
            }
            else
            {
                // Go: !HasTrailingDirectorySeparator(result())
                if (!(b.Length > start && IsAnyDirectorySeparator(b[b.Length - 1])))
                {
                    b.Append(DirectorySeparator);
                }
                b.Append(trailingPath);
            }
        }
        return b.ToString(start, b.Length - start); // Go: result()
    }

    public static List<string> GetPathComponents(string path, string currentDirectory)
    {
        path = CombinePaths(currentDirectory, path);
        return PathComponents(path, GetRootLength(path));
    }

    private static List<string> PathComponents(string path, int rootLength)
    {
        string root = path[..rootLength];
        string[] rest = path[rootLength..].Split('/');
        int restLength = rest.Length;
        if (restLength > 0 && rest[restLength - 1] == "")
        {
            restLength--;
        }
        var components = new List<string>(1 + restLength) { root };
        for (int i = 0; i < restLength; i++)
        {
            components.Add(rest[i]);
        }
        return components;
    }

    public static bool IsVolumeCharacter(char ch)
    {
        return ch >= 'a' && ch <= 'z' || ch >= 'A' && ch <= 'Z';
    }

    private static int GetFileUrlVolumeSeparatorEnd(string url, int start)
    {
        if (url.Length <= start)
        {
            return -1;
        }
        char ch0 = url[start];
        if (ch0 == ':')
        {
            return start + 1;
        }
        if (ch0 == '%' && url.Length > start + 2 && url[start + 1] == '3')
        {
            char ch2 = url[start + 2];
            if (ch2 == 'a' || ch2 == 'A')
            {
                return start + 3;
            }
        }
        return -1;
    }

    public static int GetEncodedRootLength(string path)
    {
        int ln = path.Length;
        if (ln == 0)
        {
            return 0;
        }
        char ch0 = path[0];

        // POSIX or UNC
        if (ch0 == '/' || ch0 == '\\')
        {
            if (ln == 1 || path[1] != ch0)
            {
                return 1; // POSIX: "/" (or non-normalized "\")
            }

            const int offset = 2;
            int p1 = path.IndexOf(ch0, offset); // PORT-NOTE: absolute index (Go slices then adds offset back)
            if (p1 < 0)
            {
                return ln; // UNC: "//server" or "\\server"
            }

            return p1 + 1; // UNC: "//server/" or "\\server\"
        }

        // DOS
        if (IsVolumeCharacter(ch0) && ln > 1 && path[1] == ':')
        {
            if (ln == 2)
            {
                return 2; // DOS: "c:" (but not "c:d")
            }
            char ch2 = path[2];
            if (ch2 == '/' || ch2 == '\\')
            {
                return 3; // DOS: "c:/" or "c:\"
            }
        }

        // Untitled paths (e.g., "^/untitled/ts-nul-authority/Untitled-1")
        if (ch0 == '^' && ln > 1 && path[1] == '/')
        {
            return 2; // Untitled: "^/"
        }

        // URL
        int schemeEnd = path.IndexOf(UrlSchemeSeparator, StringComparison.Ordinal);
        if (schemeEnd != -1)
        {
            int authorityStart = schemeEnd + UrlSchemeSeparator.Length;
            int authorityEnd = path.IndexOf('/', authorityStart); // PORT-NOTE: absolute index (Go computes a relative authorityLength)
            if (authorityEnd != -1)
            { // URL: "file:///", "file://server/", "file://server/path"
                // For local "file" URLs, include the leading DOS volume (if present).
                // Per https://www.ietf.org/rfc/rfc1738.txt, a host of "" or "localhost" is a
                // special case interpreted as "the machine from which the URL is being interpreted".
                string scheme = path[..schemeEnd];
                string authority = path[authorityStart..authorityEnd];
                if (scheme == "file" && (authority == "" || authority == "localhost") && path.Length > authorityEnd + 2 && IsVolumeCharacter(path[authorityEnd + 1]))
                {
                    int volumeSeparatorEnd = GetFileUrlVolumeSeparatorEnd(path, authorityEnd + 2);
                    if (volumeSeparatorEnd != -1)
                    {
                        if (volumeSeparatorEnd == path.Length)
                        {
                            // URL: "file:///c:", "file://localhost/c:", "file:///c$3a", "file://localhost/c%3a"
                            // but not "file:///c:d" or "file:///c%3ad"
                            return ~volumeSeparatorEnd;
                        }
                        if (path[volumeSeparatorEnd] == '/')
                        {
                            // URL: "file:///c:/", "file://localhost/c:/", "file:///c%3a/", "file://localhost/c%3a/"
                            return ~(volumeSeparatorEnd + 1);
                        }
                    }
                }
                return ~(authorityEnd + 1); // URL: "file://server/", "http://server/"
            }
            return ~ln; // URL: "file://server", "http://server"
        }

        // relative
        return 0;
    }

    public static int GetRootLength(string path)
    {
        int rootLength = GetEncodedRootLength(path);
        if (rootLength < 0)
        {
            return ~rootLength;
        }
        return rootLength;
    }

    public static string GetDirectoryPath(string path)
    {
        path = NormalizeSlashes(path);

        // If the path provided is itself a root, then return it.
        int rootLength = GetRootLength(path);
        if (rootLength == path.Length)
        {
            return path;
        }

        // return the leading portion of the path up to the last (non-terminal) directory separator
        // but not including any trailing directory separator.
        path = RemoveTrailingDirectorySeparator(path);
        return path[..Math.Max(rootLength, path.LastIndexOf('/'))];
    }

    public static string GetPathFromPathComponents(List<string> pathComponents)
    {
        if (pathComponents.Count == 0)
        {
            return "";
        }

        string root = pathComponents[0];
        if (root != "")
        {
            root = EnsureTrailingDirectorySeparator(root);
        }

        return root + string.Join("/", pathComponents.GetRange(1, pathComponents.Count - 1));
    }

    public static string NormalizeSlashes(string path)
    {
        return path.Replace('\\', '/');
    }

    internal static List<string> ReducePathComponents(List<string> components)
    {
        if (components.Count == 0)
        {
            return [];
        }
        var reduced = new List<string> { components[0] };
        for (int i = 1; i < components.Count; i++)
        {
            string component = components[i];
            if (component == "")
            {
                continue;
            }
            if (component == ".")
            {
                continue;
            }
            if (component == "..")
            {
                if (reduced.Count > 1)
                {
                    if (reduced[^1] != "..")
                    {
                        reduced.RemoveAt(reduced.Count - 1);
                        continue;
                    }
                }
                else if (reduced[0] != "")
                {
                    continue;
                }
            }
            reduced.Add(component);
        }
        return reduced;
    }

    // Combines and resolves paths. If a path is absolute, it replaces any previous path. Any
    // `.` and `..` path components are resolved. Trailing directory separators are preserved.
    //
    // ```go
    // resolvePath("/path", "to", "file.ext") == "path/to/file.ext"
    // resolvePath("/path", "to", "file.ext/") == "path/to/file.ext/"
    // resolvePath("/path", "dir", "..", "to", "file.ext") == "path/to/file.ext"
    // ```
    public static string ResolvePath(string path, params string[] paths)
    {
        string combinedPath;
        if (paths.Length > 0)
        {
            combinedPath = CombinePaths(path, paths);
        }
        else
        {
            combinedPath = NormalizeSlashes(path);
        }
        return NormalizePath(combinedPath);
    }

    public static string ResolveTripleslashReference(string moduleName, string containingFile)
    {
        string basePath = GetDirectoryPath(containingFile);
        if (IsRootedDiskPath(moduleName))
        {
            return NormalizePath(moduleName);
        }
        return NormalizePath(CombinePaths(basePath, moduleName));
    }

    public static List<string> GetNormalizedPathComponents(string path, string currentDirectory)
    {
        string combined = CombinePaths(currentDirectory, path);
        return GetNormalizedPathComponentsFromCombined(combined);
    }

    private static List<string> GetNormalizedPathComponentsFromCombined(string path)
    {
        int rootLength = GetRootLength(path);
        // Always include the root component (empty string for relative paths).
        var components = new List<string>(8) { path[..rootLength] };

        for (int i = rootLength; i < path.Length;)
        {
            // Skip directory separators (handles consecutive separators and trailing '/').
            while (i < path.Length && path[i] == '/')
            {
                i++;
            }
            if (i >= path.Length)
            {
                break;
            }

            int start = i;
            while (i < path.Length && path[i] != '/')
            {
                i++;
            }
            string component = path[start..i];

            if (component == "" || component == ".")
            {
                continue;
            }
            if (component == "..")
            {
                if (components.Count > 1)
                {
                    if (components[^1] != "..")
                    {
                        components.RemoveAt(components.Count - 1);
                        continue;
                    }
                }
                else if (components[0] != "")
                {
                    // If this is an absolute path, we can't go above the root.
                    continue;
                }
            }

            components.Add(component);
        }

        return components;
    }

    public static string GetNormalizedAbsolutePathWithoutRoot(string fileName, string currentDirectory)
    {
        string absolutePath = GetNormalizedAbsolutePath(fileName, currentDirectory);
        int rootLength = GetRootLength(absolutePath);
        return absolutePath[rootLength..];
    }

    public static string GetNormalizedAbsolutePath(string fileName, string currentDirectory)
    {
        int rootLength = GetRootLength(fileName);
        if (rootLength == 0 && currentDirectory != "")
        {
            fileName = CombinePaths(currentDirectory, fileName);
        }
        else
        {
            // CombinePaths normalizes slashes, so not necessary in other branch
            fileName = NormalizeSlashes(fileName);
        }
        rootLength = GetRootLength(fileName);

        if (SimpleNormalizePath(fileName, out string simpleNormalized))
        {
            int simpleLength = simpleNormalized.Length;
            if (simpleLength > rootLength)
            {
                return RemoveTrailingDirectorySeparator(simpleNormalized);
            }
            if (simpleLength == rootLength && rootLength != 0)
            {
                return EnsureTrailingDirectorySeparator(simpleNormalized);
            }
            return simpleNormalized;
        }

        int length = fileName.Length;
        string root = fileName[..rootLength];
        // `normalized` is only initialized once `fileName` is determined to be non-normalized.
        // `changed` is set at the same time.
        bool changed = false;
        string normalized = "";
        int segmentStart;
        int index = rootLength;
        int normalizedUpTo = index;
        bool seenNonDotDotSegment = rootLength != 0;
        while (index < length)
        {
            // At beginning of segment
            segmentStart = index;
            char ch = fileName[index];
            while (ch == '/')
            {
                index++;
                if (index < length)
                {
                    ch = fileName[index];
                }
                else
                {
                    break;
                }
            }
            if (index > segmentStart)
            {
                // Seen superfluous separator
                if (!changed)
                {
                    normalized = fileName[..Math.Max(rootLength, segmentStart - 1)];
                    changed = true;
                }
                if (index == length)
                {
                    break;
                }
                segmentStart = index;
            }
            // Past any superfluous separators
            int segmentEnd = fileName.IndexOf('/', index + 1); // PORT-NOTE: absolute index (Go searches a slice and adds index+1 back)
            if (segmentEnd == -1)
            {
                segmentEnd = length;
            }
            int segmentLength = segmentEnd - segmentStart;
            if (segmentLength == 1 && fileName[index] == '.')
            {
                // "." segment (skip)
                if (!changed)
                {
                    normalized = fileName[..normalizedUpTo];
                    changed = true;
                }
            }
            else if (segmentLength == 2 && fileName[index] == '.' && fileName[index + 1] == '.')
            {
                // ".." segment
                if (!seenNonDotDotSegment)
                {
                    if (changed)
                    {
                        if (normalized.Length == rootLength)
                        {
                            normalized += "..";
                        }
                        else
                        {
                            normalized += "/..";
                        }
                    }
                    else
                    {
                        normalizedUpTo = index + 2;
                    }
                }
                else if (!changed)
                {
                    if (normalizedUpTo - 1 >= 0)
                    {
                        // Go: strings.LastIndexByte(fileName[:normalizedUpTo-1], '/')
                        int lastSlash = normalizedUpTo - 1 == 0 ? -1 : fileName.LastIndexOf('/', normalizedUpTo - 2);
                        normalized = fileName[..Math.Max(rootLength, lastSlash)];
                    }
                    else
                    {
                        normalized = fileName[..normalizedUpTo];
                    }
                    changed = true;
                    seenNonDotDotSegment = (normalized.Length != rootLength || rootLength != 0) && normalized != ".." && !normalized.EndsWith("/..", StringComparison.Ordinal);
                }
                else
                {
                    int lastSlash = normalized.LastIndexOf('/');
                    if (lastSlash != -1)
                    {
                        normalized = normalized[..Math.Max(rootLength, lastSlash)];
                    }
                    else
                    {
                        normalized = root;
                    }
                    seenNonDotDotSegment = (normalized.Length != rootLength || rootLength != 0) && normalized != ".." && !normalized.EndsWith("/..", StringComparison.Ordinal);
                }
            }
            else if (changed)
            {
                if (normalized.Length != rootLength)
                {
                    normalized += "/";
                }
                seenNonDotDotSegment = true;
                normalized += fileName[segmentStart..segmentEnd];
            }
            else
            {
                seenNonDotDotSegment = true;
                normalizedUpTo = segmentEnd;
            }
            index = segmentEnd + 1;
        }
        if (changed)
        {
            return normalized;
        }
        if (length > rootLength)
        {
            return RemoveTrailingDirectorySeparators(fileName);
        }
        if (length == rootLength)
        {
            return EnsureTrailingDirectorySeparator(fileName);
        }
        return fileName;
    }

    private static bool SimpleNormalizePath(string path, out string result)
    {
        // Most paths don't require normalization
        if (!HasRelativePathSegment(path))
        {
            result = path;
            return true;
        }
        // Some paths only require cleanup of `/./` or leading `./`
        string simplified = path.Replace("/./", "/", StringComparison.Ordinal);
        string trimmed = simplified.StartsWith("./", StringComparison.Ordinal) ? simplified[2..] : simplified; // Go: strings.TrimPrefix
        if (trimmed != path && !HasRelativePathSegment(trimmed) && !(trimmed != simplified && trimmed.StartsWith('/')))
        {
            // If we trimmed a leading "./" and the path now starts with "/", we changed the meaning
            result = trimmed;
            return true;
        }
        result = "";
        return false;
    }

    // HasRelativePathSegment reports whether p contains ".", "..", "./", "../", "/.", "/..", "//", "/./", or "/../".
    private static bool HasRelativePathSegment(string p)
    {
        int n = p.Length;
        if (n == 0)
        {
            return false;
        }

        if (p == "." || p == "..")
        {
            return true;
        }

        // Leading "./" OR "../"
        if (p[0] == '.')
        {
            if (n >= 2 && p[1] == '/')
            {
                return true;
            }
            // Leading "../"
            if (n >= 3 && p[1] == '.' && p[2] == '/')
            {
                return true;
            }
        }
        // Trailing "/." OR "/.."
        if (p[n - 1] == '.')
        {
            if (n >= 2 && p[n - 2] == '/')
            {
                return true;
            }
            if (n >= 3 && p[n - 2] == '.' && p[n - 3] == '/')
            {
                return true;
            }
        }

        // Now look for any `//` or `/./` or `/../`

        bool prevSlash = false;
        int segLen = 0;   // length of current segment since last slash
        int dotCount = 0; // consecutive dots at start of the current segment; -1 => not only dots

        for (int i = 0; i < n; i++)
        {
            char c = p[i];
            if (c == '/')
            {
                // "//"
                if (prevSlash)
                {
                    return true;
                }
                // "/./" or "/../"
                if ((segLen == 1 && dotCount == 1) || (segLen == 2 && dotCount == 2))
                {
                    return true;
                }
                prevSlash = true;
                segLen = 0;
                dotCount = 0;
                continue;
            }

            if (c == '.')
            {
                if (dotCount >= 0)
                {
                    dotCount++;
                }
            }
            else
            {
                dotCount = -1;
            }
            segLen++;
            prevSlash = false;
        }

        // Trailing "/." or "/.."
        return (segLen == 1 && dotCount == 1) || (segLen == 2 && dotCount == 2);
    }

    public static string NormalizePath(string path)
    {
        path = NormalizeSlashes(path);
        if (SimpleNormalizePath(path, out string simpleNormalized))
        {
            return simpleNormalized;
        }
        string normalized = GetNormalizedAbsolutePath(path, "");
        if (normalized != "" && HasTrailingDirectorySeparator(path))
        {
            normalized = EnsureTrailingDirectorySeparator(normalized);
        }
        return normalized;
    }

    public static string GetCanonicalFileName(string fileName, bool useCaseSensitiveFileNames)
    {
        if (useCaseSensitiveFileNames)
        {
            return fileName;
        }
        return ToFileNameLowerCase(fileName);
    }

    // We convert the file names to lower case as key for file name on case insensitive file system
    // While doing so we need to handle special characters (eg İ) to ensure that we dont convert
    // it to lower case, fileName with its lowercase form can exist along side it.
    // Handle special characters and make those case sensitive instead
    //
    // |-#--|-Unicode--|-Char code-|-Desc-------------------------------------------------------------------|
    // | 1. | i        | 105       | Ascii i                                                                |
    // | 2. | I        | 73        | Ascii I                                                                |
    // |-------- Special characters ------------------------------------------------------------------------|
    // | 3. | İ   | 304       | Upper case I with dot above                                            |
    // | 4. | i,̇ | 105,775   | i, followed by 775: Lower case of (3rd item)                           |
    // | 5. | I,̇ | 73,775    | I, followed by 775: Upper case of (4th item), lower case is (4th item) |
    // | 6. | ı   | 305       | Lower case i without dot, upper case is I (2nd item)                   |
    // | 7. | ß   | 223       | Lower case sharp s                                                     |
    //
    // Because item 3 is special where in its lowercase character has its own
    // upper case form we cant convert its case.
    // Rest special characters are either already in lower case format or
    // they have corresponding upper case character so they dont need special handling

    public static string ToFileNameLowerCase(string fileName)
    {
        const int IWithDot = 'İ';

        bool ascii = true;
        bool needsLower = false;
        int fileNameLen = fileName.Length;
        for (int i = 0; i < fileNameLen; i++)
        {
            char c = fileName[i];
            if (c >= 0x80)
            {
                ascii = false;
                break;
            }
            if ('A' <= c && c <= 'Z')
            {
                needsLower = true;
            }
        }
        if (ascii)
        {
            if (!needsLower)
            {
                return fileName;
            }
            // PORT-NOTE: Go builds a []byte and aliases it via unsafe.String (path.go:666-675
            // SAFETY comment); string.Create writes into the final string in place, giving the
            // same zero-extra-copy result with structural safety (05 §15).
            return string.Create(fileNameLen, fileName, static (span, src) =>
            {
                for (int i = 0; i < src.Length; i++)
                {
                    char c = src[i];
                    if ('A' <= c && c <= 'Z')
                    {
                        c += (char)32; // Go: c += 'a' - 'A'
                    }
                    span[i] = c;
                }
            });
        }

        // Go: strings.Map(func(r rune) rune { if r == IWithDot { return r }; return unicode.ToLower(r) }, fileName)
        return StrUtil.MapRunes(static r =>
        {
            if (r == IWithDot)
            {
                return r;
            }
            return StrUtil.ToLowerRune(r);
        }, fileName);
    }

    public static Path ToPath(string fileName, string basePath, bool useCaseSensitiveFileNames)
    {
        string nonCanonicalizedPath;
        if (IsRootedDiskPath(fileName))
        {
            nonCanonicalizedPath = NormalizePath(fileName);
        }
        else
        {
            nonCanonicalizedPath = GetNormalizedAbsolutePath(fileName, basePath);
        }
        return new Path(GetCanonicalFileName(nonCanonicalizedPath, useCaseSensitiveFileNames));
    }

    public static string RemoveTrailingDirectorySeparator(string path)
    {
        if (HasTrailingDirectorySeparator(path))
        {
            return path[..^1];
        }
        return path;
    }

    public static string RemoveTrailingDirectorySeparators(string path)
    {
        while (HasTrailingDirectorySeparator(path))
        {
            path = RemoveTrailingDirectorySeparator(path);
        }
        return path;
    }

    public static string EnsureTrailingDirectorySeparator(string path)
    {
        if (!HasTrailingDirectorySeparator(path))
        {
            return path + "/";
        }

        return path;
    }

    //// Relative Paths

    public static List<string> GetPathComponentsRelativeTo(string from, string to, ComparePathsOptions options)
    {
        var fromComponents = ReducePathComponents(GetPathComponents(from, options.CurrentDirectory));
        var toComponents = ReducePathComponents(GetPathComponents(to, options.CurrentDirectory));

        int start = 0;
        int maxCommonComponents = Math.Min(fromComponents.Count, toComponents.Count);
        var stringEqualer = options.GetEqualityComparer();
        for (; start < maxCommonComponents; start++)
        {
            string fromComponent = fromComponents[start];
            string toComponent = toComponents[start];
            if (start == 0)
            {
                if (!StrUtil.EquateStringCaseInsensitive(fromComponent, toComponent))
                {
                    break;
                }
            }
            else
            {
                if (!stringEqualer(fromComponent, toComponent))
                {
                    break;
                }
            }
        }

        if (start == 0)
        {
            return toComponents;
        }

        int numDotDotSlashes = fromComponents.Count - start;
        var result = new List<string>(1 + numDotDotSlashes + toComponents.Count - start)
        {
            "",
        };
        // Add all the relative components until we hit a common directory.
        for (int i = 0; i < numDotDotSlashes; i++)
        {
            result.Add("..");
        }
        // Now add all the remaining components of the "to" path.
        for (int i = start; i < toComponents.Count; i++)
        {
            result.Add(toComponents[i]);
        }

        return result;
    }

    public static string GetRelativePathFromDirectory(string fromDirectory, string to, ComparePathsOptions options)
    {
        if ((GetRootLength(fromDirectory) > 0) != (GetRootLength(to) > 0))
        {
            throw new InvalidOperationException("paths must either both be absolute or both be relative"); // Go: panic
        }
        var pathComponents = GetPathComponentsRelativeTo(fromDirectory, to, options);
        return GetPathFromPathComponents(pathComponents);
    }

    public static string GetRelativePathFromFile(string from, string to, ComparePathsOptions options)
    {
        return EnsurePathIsNonModuleName(GetRelativePathFromDirectory(GetDirectoryPath(from), to, options));
    }

    public static string ConvertToRelativePath(string absoluteOrRelativePath, ComparePathsOptions options)
    {
        if (!IsRootedDiskPath(absoluteOrRelativePath))
        {
            return absoluteOrRelativePath;
        }

        return GetRelativePathToDirectoryOrUrl(options.CurrentDirectory, absoluteOrRelativePath, false /*isAbsolutePathAnUrl*/, options);
    }

    public static string GetRelativePathToDirectoryOrUrl(string directoryPathOrUrl, string relativeOrAbsolutePath, bool isAbsolutePathAnUrl, ComparePathsOptions options)
    {
        var pathComponents = GetPathComponentsRelativeTo(
            directoryPathOrUrl,
            relativeOrAbsolutePath,
            options);

        string firstComponent = pathComponents[0];
        if (isAbsolutePathAnUrl && IsRootedDiskPath(firstComponent))
        {
            string prefix;
            if (firstComponent[0] == DirectorySeparator)
            {
                prefix = "file://";
            }
            else
            {
                prefix = "file:///";
            }
            pathComponents[0] = prefix + firstComponent;
        }

        return GetPathFromPathComponents(pathComponents);
    }

    // Gets the portion of a path following the last (non-terminal) separator (`/`).
    // Semantics align with NodeJS's `path.basename` except that we support URL's as well.
    // If the base name has any one of the provided extensions, it is removed.
    //
    //	// POSIX
    //	GetBaseFileName("/path/to/file.ext") == "file.ext"
    //	GetBaseFileName("/path/to/") == "to"
    //	GetBaseFileName("/") == ""
    //	// DOS
    //	GetBaseFileName("c:/path/to/file.ext") == "file.ext"
    //	GetBaseFileName("c:/path/to/") == "to"
    //	GetBaseFileName("c:/") == ""
    //	GetBaseFileName("c:") == ""
    //	// URL
    //	GetBaseFileName("http://typescriptlang.org/path/to/file.ext") == "file.ext"
    //	GetBaseFileName("http://typescriptlang.org/path/to/") == "to"
    //	GetBaseFileName("http://typescriptlang.org/") == ""
    //	GetBaseFileName("http://typescriptlang.org") == ""
    //	GetBaseFileName("file://server/path/to/file.ext") == "file.ext"
    //	GetBaseFileName("file://server/path/to/") == "to"
    //	GetBaseFileName("file://server/") == ""
    //	GetBaseFileName("file://server") == ""
    //	GetBaseFileName("file:///path/to/file.ext") == "file.ext"
    //	GetBaseFileName("file:///path/to/") == "to"
    //	GetBaseFileName("file:///") == ""
    //	GetBaseFileName("file://") == ""
    public static string GetBaseFileName(string path)
    {
        path = NormalizeSlashes(path);

        // if the path provided is itself the root, then it has no file name.
        int rootLength = GetRootLength(path);
        if (rootLength == path.Length)
        {
            return "";
        }

        // return the trailing portion of the path starting after the last (non-terminal) directory
        // separator but not including any trailing directory separator.
        path = RemoveTrailingDirectorySeparator(path);
        return path[Math.Max(GetRootLength(path), path.LastIndexOf(DirectorySeparator) + 1)..];
    }

    // Gets the file extension for a path.
    // If extensions are provided, gets the file extension for a path, provided it is one of the provided extensions.
    //
    //	GetAnyExtensionFromPath("/path/to/file.ext", nil, false) == ".ext"
    //	GetAnyExtensionFromPath("/path/to/file.ext/", nil, false) == ".ext"
    //	GetAnyExtensionFromPath("/path/to/file", nil, false) == ""
    //	GetAnyExtensionFromPath("/path/to.ext/file", nil, false) == ""
    //	GetAnyExtensionFromPath("/path/to/file.ext", ".ext", true) === ".ext"
    //	GetAnyExtensionFromPath("/path/to/file.js", ".ext", true) === ""
    //	GetAnyExtensionFromPath("/path/to/file.js", [".ext", ".js"], true) === ".js"
    //	GetAnyExtensionFromPath("/path/to/file.ext", ".EXT", false) === ""
    public static string GetAnyExtensionFromPath(string path, string[]? extensions, bool ignoreCase)
    {
        // Retrieves any string from the final "." onwards from a base file name.
        // Unlike extensionFromPath, which throws an exception on unrecognized extensions.
        if (extensions is { Length: > 0 })
        {
            return GetAnyExtensionFromPathWorker(RemoveTrailingDirectorySeparator(path), extensions, StrUtil.GetStringEqualityComparer(ignoreCase));
        }

        string baseFileName = GetBaseFileName(path);
        int extensionIndex = baseFileName.LastIndexOf('.');
        if (extensionIndex >= 0)
        {
            return baseFileName[extensionIndex..];
        }
        return "";
    }

    private static string GetAnyExtensionFromPathWorker(string path, string[] extensions, Func<string, string, bool> stringEqualityComparer)
    {
        foreach (string extension in extensions)
        {
            string result = TryGetExtensionFromPath(path, extension, stringEqualityComparer);
            if (result != "")
            {
                return result;
            }
        }
        return "";
    }

    private static string TryGetExtensionFromPath(string path, string extension, Func<string, string, bool> stringEqualityComparer)
    {
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }
        if (path.Length >= extension.Length && path[path.Length - extension.Length] == '.')
        {
            string pathExtension = path[(path.Length - extension.Length)..];
            if (stringEqualityComparer(pathExtension, extension))
            {
                return pathExtension;
            }
        }
        return "";
    }

    public static bool PathIsRelative(string path)
    {
        // True if path is ".", "..", or starts with "./", "../", ".\\", or "..\\".

        if (path == "." || path == "..")
        {
            return true;
        }

        if (path.Length >= 2 && path[0] == '.' && (path[1] == '/' || path[1] == '\\'))
        {
            return true;
        }

        if (path.Length >= 3 && path[0] == '.' && path[1] == '.' && (path[2] == '/' || path[2] == '\\'))
        {
            return true;
        }

        return false;
    }

    // EnsurePathIsNonModuleName ensures a path is either absolute (prefixed with `/` or `c:`) or dot-relative (prefixed
    // with `./` or `../`) so as not to be confused with an unprefixed module name.
    public static string EnsurePathIsNonModuleName(string path)
    {
        if (!PathIsAbsolute(path) && !PathIsRelative(path))
        {
            return "./" + path;
        }
        return path;
    }

    public static bool IsExternalModuleNameRelative(string moduleName)
    {
        // TypeScript 1.0 spec (April 2014): 11.2.1
        // An external module name is "relative" if the first term is "." or "..".
        // Update: We also consider a path like `C:\foo.ts` "relative" because we do not search for it in `node_modules` or treat it as an ambient module.
        return PathIsRelative(moduleName) || IsRootedDiskPath(moduleName);
    }

    public static int ComparePaths(string a, string b, ComparePathsOptions options)
    {
        a = CombinePaths(options.CurrentDirectory, a);
        b = CombinePaths(options.CurrentDirectory, b);

        if (a == b)
        {
            return 0;
        }
        if (a == "")
        {
            return -1;
        }
        if (b == "")
        {
            return 1;
        }

        // NOTE: Performance optimization - shortcut if the root segments differ as there would be no
        //       need to perform path reduction.
        string aRoot = a[..GetRootLength(a)];
        string bRoot = b[..GetRootLength(b)];
        int result = StrUtil.CompareStringsCaseInsensitive(aRoot, bRoot);
        if (result != 0)
        {
            return result;
        }

        // NOTE: Performance optimization - shortcut if there are no relative path segments in
        //       the non-root portion of the path
        string aRest = a[aRoot.Length..];
        string bRest = b[bRoot.Length..];
        if (!HasRelativePathSegment(aRest) && !HasRelativePathSegment(bRest))
        {
            return options.GetComparer()(aRest, bRest);
        }

        // The path contains a relative path segment. Normalize the paths and perform a slower component
        // by component comparison.
        var aComponents = ReducePathComponents(GetPathComponents(a, ""));
        var bComponents = ReducePathComponents(GetPathComponents(b, ""));
        int sharedLength = Math.Min(aComponents.Count, bComponents.Count);
        for (int i = 1; i < sharedLength; i++)
        {
            int componentResult = options.GetComparer()(aComponents[i], bComponents[i]);
            if (componentResult != 0)
            {
                return componentResult;
            }
        }
        return CmpCompare(aComponents.Count, bComponents.Count);
    }

    public static int ComparePathsCaseSensitive(string a, string b, string currentDirectory)
    {
        return ComparePaths(a, b, new ComparePathsOptions { UseCaseSensitiveFileNames = true, CurrentDirectory = currentDirectory });
    }

    public static int ComparePathsCaseInsensitive(string a, string b, string currentDirectory)
    {
        return ComparePaths(a, b, new ComparePathsOptions { UseCaseSensitiveFileNames = false, CurrentDirectory = currentDirectory });
    }

    public static bool ContainsPath(string parent, string child, ComparePathsOptions options)
    {
        parent = CombinePaths(options.CurrentDirectory, parent);
        child = CombinePaths(options.CurrentDirectory, child);
        if (parent == "" || child == "")
        {
            return false;
        }
        if (parent == child)
        {
            return true;
        }
        var parentComponents = ReducePathComponents(GetPathComponents(parent, ""));
        var childComponents = ReducePathComponents(GetPathComponents(child, ""));
        if (childComponents.Count < parentComponents.Count)
        {
            return false;
        }

        var componentComparer = options.GetEqualityComparer();
        for (int i = 0; i < parentComponents.Count; i++)
        {
            string parentComponent = parentComponents[i];
            Func<string, string, bool> comparer;
            if (i == 0)
            {
                comparer = StrUtil.EquateStringCaseInsensitive;
            }
            else
            {
                comparer = componentComparer;
            }
            if (!comparer(parentComponent, childComponents[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static bool FileExtensionIs(string path, string extension)
    {
        return path.Length > extension.Length && path.EndsWith(extension, StringComparison.Ordinal);
    }

    // Calls `callback` on `directory` and every ancestor directory it has, returning the first defined result.
    // Stops at global cache location
    public static T ForEachAncestorDirectoryStoppingAtGlobalCache<T>(
        string globalCacheLocation,
        string directory,
        Func<string, (T Result, bool Stop)> callback)
    {
        var (result, _) = ForEachAncestorDirectory(directory, ancestorDirectory =>
        {
            var (innerResult, stop) = callback(ancestorDirectory);
            if (stop || ancestorDirectory == globalCacheLocation)
            {
                return (innerResult, true);
            }
            return (innerResult, false);
        });
        return result;
    }

    public static (T Result, bool Ok) ForEachAncestorDirectory<T>(string directory, Func<string, (T Result, bool Stop)> callback)
    {
        while (true)
        {
            var (result, stop) = callback(directory);
            if (stop)
            {
                return (result, true);
            }

            string parentPath = GetDirectoryPath(directory);
            if (parentPath == directory)
            {
                return (default!, false); // Go: var zero T
            }

            directory = parentPath;
        }
    }

    public static (T Result, bool Ok) ForEachAncestorDirectoryPath<T>(Path directory, Func<Path, (T Result, bool Stop)> callback)
    {
        return ForEachAncestorDirectory(directory.Value, dir => callback(new Path(dir)));
    }

    public static bool HasExtension(string fileName)
    {
        return GetBaseFileName(fileName).Contains('.');
    }

    // Go: func SplitVolumePath(path string) (volume string, rest string, ok bool).
    // PORT-NOTE: "Rest" is a reserved tuple element name in C#, so the multi-return
    // maps to bool + out parameters instead of a named tuple.
    public static bool SplitVolumePath(string path, out string volume, out string rest)
    {
        if (path.Length >= 2 && IsVolumeCharacter(path[0]) && path[1] == ':')
        {
            // Go: strings.ToLower(path[0:2]) — the volume character is ASCII and ':' is
            // case-invariant (char-level lowering also sidesteps analyzer CA1308).
            volume = char.ToLowerInvariant(path[0]) + ":";
            rest = path[2..];
            return true;
        }
        volume = "";
        rest = path;
        return false;
    }

    // GetCommonParents returns the smallest set of directories that are parents of all paths with
    // at least `minComponents` directory components. Any path that has fewer than `minComponents` directory components
    // will be returned in the second return value. Examples:
    //
    //	/a/b/c/d, /a/b/c/e, /a/b/f/g  =>  /a/b
    //	/a/b/c/d, /a/b/c/e, /a/b/f/g, /x/y  =>  /
    //	/a/b/c/d, /a/b/c/e, /a/b/f/g, /x/y  (minComponents: 2)	=>  /a/b, /x/y
    //	c:/a/b/c/d, d:/a/b/c/d =>	c:/a/b/c/d, d:/a/b/c/d
    public static (string[]? Parents, HashSet<string>? Ignored) GetCommonParents(
        string[]? paths,
        int minComponents,
        Func<string, string, List<string>> getPathComponents,
        ComparePathsOptions options)
    {
        if (minComponents < 1)
        {
            throw new InvalidOperationException("minComponents must be at least 1"); // Go: panic
        }
        if (paths == null || paths.Length == 0)
        {
            return (null, null);
        }
        if (paths.Length == 1)
        {
            if (ReducePathComponents(getPathComponents(paths[0], options.CurrentDirectory)).Count < minComponents)
            {
                return (null, new HashSet<string>(StringComparer.Ordinal) { paths[0] });
            }
            return (paths, null);
        }

        var ignored = new HashSet<string>(StringComparer.Ordinal);
        var pathComponents = new List<List<string>>(paths.Length);
        foreach (string path in paths)
        {
            var components = ReducePathComponents(getPathComponents(path, options.CurrentDirectory));
            if (components.Count < minComponents)
            {
                ignored.Add(path);
            }
            else
            {
                pathComponents.Add(components);
            }
        }

        var results = GetCommonParentsWorker(pathComponents, minComponents, options);
        var resultPaths = new string[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            resultPaths[i] = GetPathFromPathComponents(results[i]);
        }

        return (resultPaths, ignored);
    }

    private static List<List<string>> GetCommonParentsWorker(List<List<string>> componentGroups, int minComponents, ComparePathsOptions options)
    {
        if (componentGroups.Count == 0)
        {
            return []; // Go: nil (only ranged/measured by callers)
        }
        // Determine the maximum depth we can consider
        int maxDepth = componentGroups[0].Count;
        for (int gi = 1; gi < componentGroups.Count; gi++)
        {
            int l = componentGroups[gi].Count;
            if (l < maxDepth)
            {
                maxDepth = l;
            }
        }

        var equality = options.GetEqualityComparer();
        for (int lastCommonIndex = 0; lastCommonIndex < maxDepth; lastCommonIndex++)
        {
            string candidate = componentGroups[0][lastCommonIndex];
            for (int j = 1; j < componentGroups.Count; j++)
            {
                var comps = componentGroups[j];
                if (!equality(candidate, comps[lastCommonIndex]))
                { // divergence
                    if (lastCommonIndex < minComponents)
                    {
                        // Not enough components, we need to fan out
                        // Go j ranges over componentGroups[1:], so its capacity hint is len-1-j here;
                        // capacity is a hint only.
                        var orderedGroups = new List<Path>(componentGroups.Count - (j - 1));
                        var newGroups = new Dictionary<Path, (List<string> Head, List<List<string>> Tails)>();
                        foreach (var g in componentGroups)
                        {
                            Path key = ToPath(g[lastCommonIndex], options.CurrentDirectory, options.UseCaseSensitiveFileNames);
                            if (!newGroups.ContainsKey(key))
                            {
                                orderedGroups.Add(key);
                            }
                            newGroups.TryGetValue(key, out var existing); // Go: newGroups[key] zero value on miss
                            var tails = existing.Tails ?? [];
                            tails.Add(g.GetRange(lastCommonIndex + 1, g.Count - (lastCommonIndex + 1)));
                            newGroups[key] = (g.GetRange(0, lastCommonIndex + 1), tails);
                        }
                        orderedGroups.Sort(); // Go: slices.Sort (Path code-point order)
                        var result = new List<List<string>>(newGroups.Count);
                        foreach (Path key in orderedGroups)
                        {
                            var group = newGroups[key];
                            var subResults = GetCommonParentsWorker(group.Tails, minComponents - (lastCommonIndex + 1), options);
                            foreach (var sr in subResults)
                            {
                                // PORT-NOTE: Go's `append(group.head, sr...)` may alias the group's
                                // backing array across iterations; a fresh list per result is the
                                // intended (and observed) semantics.
                                var combined = new List<string>(group.Head.Count + sr.Count);
                                combined.AddRange(group.Head);
                                combined.AddRange(sr);
                                result.Add(combined);
                            }
                        }
                        return result;
                    }
                    return [componentGroups[0].GetRange(0, lastCommonIndex)];
                }
            }
        }

        return [componentGroups[0].GetRange(0, maxDepth)];
    }

    public static bool StartsWithDirectory(string fileName, string directoryName, bool useCaseSensitiveFileNames)
    {
        if (directoryName == "")
        {
            return false;
        }

        string canonicalFileName = GetCanonicalFileName(fileName, useCaseSensitiveFileNames);
        string canonicalDirectoryName = GetCanonicalFileName(directoryName, useCaseSensitiveFileNames);
        canonicalDirectoryName = TrimSuffix(canonicalDirectoryName, "/");
        canonicalDirectoryName = TrimSuffix(canonicalDirectoryName, "\\");

        return canonicalFileName.StartsWith(canonicalDirectoryName + "/", StringComparison.Ordinal) ||
            canonicalFileName.StartsWith(canonicalDirectoryName + "\\", StringComparison.Ordinal);
    }

    public static int CompareNumberOfDirectorySeparators(string path1, string path2)
    {
        return CmpCompare(path1.AsSpan().Count('/'), path2.AsSpan().Count('/'));
    }

    // Go: strings.TrimSuffix
    private static string TrimSuffix(string s, string suffix)
    {
        return s.EndsWith(suffix, StringComparison.Ordinal) ? s[..^suffix.Length] : s;
    }

    // Go: cmp.Compare for int (strictly -1/0/+1)
    private static int CmpCompare(int a, int b)
    {
        if (a < b)
        {
            return -1;
        }
        if (a > b)
        {
            return 1;
        }
        return 0;
    }
}

// path.go:938 — options bag for the path comparison APIs. Zero value must behave like
// Go's ComparePathsOptions{}: CurrentDirectory reads "" (never null).
public readonly struct ComparePathsOptions
{
    private readonly string? currentDirectory;

    public bool UseCaseSensitiveFileNames { get; init; }

    public string CurrentDirectory
    {
        get => currentDirectory ?? "";
        init => currentDirectory = value;
    }

    public Func<string, string, int> GetComparer()
    {
        return Ts.StringUtil.StringUtil.GetStringComparer(!UseCaseSensitiveFileNames);
    }

    // Go: getEqualityComparer (package-private → internal)
    internal Func<string, string, bool> GetEqualityComparer()
    {
        return Ts.StringUtil.StringUtil.GetStringEqualityComparer(!UseCaseSensitiveFileNames);
    }
}
