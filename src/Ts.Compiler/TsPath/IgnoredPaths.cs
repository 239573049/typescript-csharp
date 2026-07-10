// Ported from tsgo internal/tspath/ignoredpaths.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.TsPath;

public static partial class TsPath
{
    private static readonly string[] IgnoredPaths =
    [
        "/node_modules/.",
        "/.git",
        ".#",
    ];

    public static bool ContainsIgnoredPath(string path)
    {
        foreach (string pattern in IgnoredPaths)
        {
            if (path.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
