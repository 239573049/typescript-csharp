// TODO(tspath): temporary inline helpers — delete this file and switch callers to
// Ts.TsPath once internal/tspath lands (a separate in-flight work item; this unit must
// not create files under src/Ts.Compiler/TsPath/, per assignment).
//
// Every method below is a faithful function-level port of the cited
// tsgo internal/tspath/path.go lines (commit 168e7015edf98244febc8f4ae450b673b5d195d7),
// restricted to the symbols vfs/internal and vfs/iovfs actually consume.
// Positions are UTF-16 code units where Go used bytes; all compared characters are
// ASCII, so root/segment arithmetic is unit-for-unit identical on both sides.

using System;

namespace Ts.Vfs.Internal;

internal static class TsPathShim
{
    // path.go:26-28 — determines whether a char corresponds to `/` or `\`.
    private static bool IsAnyDirectorySeparator(char ch) => ch == '/' || ch == '\\';

    // path.go:31-33 — determines whether a path starts with a URL scheme.
    internal static bool IsUrl(string path) => GetEncodedRootLength(path) < 0;

    // path.go:70-72
    internal static bool HasTrailingDirectorySeparator(string path) =>
        path.Length > 0 && IsAnyDirectorySeparator(path[^1]);

    // path.go:147-149
    private static bool IsVolumeCharacter(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    // path.go:151-166
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

    // path.go:168-240
    internal static int GetEncodedRootLength(string path)
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
            int p1 = path.IndexOf(ch0, offset);
            if (p1 < 0)
            {
                return ln; // UNC: "//server" or "\\server"
            }

            return p1 + 1; // UNC: "//server/" or "\\server\" (Go: relative index + offset + 1)
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
        int schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd != -1)
        {
            int authorityStart = schemeEnd + 3;
            int authoritySlash = path.IndexOf('/', authorityStart);
            if (authoritySlash != -1) // URL: "file:///", "file://server/", "file://server/path"
            {
                int authorityEnd = authoritySlash;

                // For local "file" URLs, include the leading DOS volume (if present).
                // Per https://www.ietf.org/rfc/rfc1738.txt, a host of "" or "localhost" is a
                // special case interpreted as "the machine from which the URL is being interpreted".
                string scheme = path[..schemeEnd];
                string authority = path[authorityStart..authorityEnd];
                if (scheme == "file" && (authority.Length == 0 || authority == "localhost") && path.Length > authorityEnd + 2 && IsVolumeCharacter(path[authorityEnd + 1]))
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

    // path.go:242-248
    internal static int GetRootLength(string path)
    {
        int rootLength = GetEncodedRootLength(path);
        return rootLength < 0 ? ~rootLength : rootLength;
    }

    // path.go:250-263
    internal static string GetDirectoryPath(string path)
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

    // path.go:282-284
    internal static string NormalizeSlashes(string path) => path.Replace('\\', '/');

    // path.go:393-512, specialized to currentDirectory == "" — the only form reachable
    // from NormalizePath below (the CombinePaths branch is therefore not ported).
    private static string GetNormalizedAbsolutePath(string fileName)
    {
        // CombinePaths normalizes slashes, so not necessary in other branch
        fileName = NormalizeSlashes(fileName);
        int rootLength = GetRootLength(fileName);

        if (TrySimpleNormalizePath(fileName, out string simpleNormalized))
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
            int segmentEnd = fileName.IndexOf('/', index + 1);
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

    // path.go:514-528
    private static bool TrySimpleNormalizePath(string path, out string normalized)
    {
        // Most paths don't require normalization
        if (!HasRelativePathSegment(path))
        {
            normalized = path;
            return true;
        }
        // Some paths only require cleanup of `/./` or leading `./`
        string simplified = path.Replace("/./", "/", StringComparison.Ordinal);
        string trimmed = simplified.StartsWith("./", StringComparison.Ordinal) ? simplified[2..] : simplified;
        if (trimmed != path && !HasRelativePathSegment(trimmed) && !(trimmed != simplified && trimmed.StartsWith('/')))
        {
            // If we trimmed a leading "./" and the path now starts with "/", we changed the meaning
            normalized = trimmed;
            return true;
        }
        normalized = "";
        return false;
    }

    // path.go:531-597 — reports whether p contains ".", "..", "./", "../", "/.", "/..",
    // "//", "/./", or "/../".
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

    // path.go:599-609
    internal static string NormalizePath(string path)
    {
        path = NormalizeSlashes(path);
        if (TrySimpleNormalizePath(path, out string simple))
        {
            return simple;
        }
        string normalized = GetNormalizedAbsolutePath(path);
        if (normalized.Length != 0 && HasTrailingDirectorySeparator(path))
        {
            normalized = EnsureTrailingDirectorySeparator(normalized);
        }
        return normalized;
    }

    // path.go:697-702
    internal static string RemoveTrailingDirectorySeparator(string path)
    {
        if (HasTrailingDirectorySeparator(path))
        {
            return path[..^1];
        }
        return path;
    }

    // path.go:708-713
    internal static string RemoveTrailingDirectorySeparators(string path)
    {
        while (HasTrailingDirectorySeparator(path))
        {
            path = RemoveTrailingDirectorySeparator(path);
        }
        return path;
    }

    // path.go:715-720
    internal static string EnsureTrailingDirectorySeparator(string path)
    {
        if (!HasTrailingDirectorySeparator(path))
        {
            return path + "/";
        }
        return path;
    }
}
