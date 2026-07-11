// Ported from tsgo internal/tsoptions/wildcarddirectories.go

using System.Collections.Generic;
using System.Linq;
using Ts.TsPath;
using TsPathApi = Ts.TsPath.TsPath;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch;
using Usage = Ts.Vfs.VfsMatch.Usage;

namespace Ts.TsOptions;

public static class WildcardDirectories
{
    internal static Dictionary<string, bool>? getWildcardDirectories(string[]? include, string[]? exclude, ComparePathsOptions comparePathsOptions)
    {
        // We watch a directory recursively if it contains a wildcard anywhere in a directory segment
        // of the pattern:
        //
        //  /a/b/**/d   - Watch /a/b recursively to catch changes to any d in any subfolder recursively
        //  /a/b/*/d    - Watch /a/b recursively to catch any d in any immediate subfolder, even if a new subfolder is added
        //  /a/b        - Watch /a/b recursively to catch changes to anything in any recursive subfoler
        //
        // We watch a directory without recursion if it contains a wildcard in the file segment of
        // the pattern:
        //
        //  /a/b/*      - Watch /a/b directly to catch any new file
        //  /a/b/a?z    - Watch /a/b directly to catch any new file matching a?z

        if (include is null || include.Length == 0)
        {
            return null;
        }

        var excludeMatcher = VfsMatchApi.NewSpecMatcher(exclude, comparePathsOptions.CurrentDirectory, Usage.Exclude, comparePathsOptions.UseCaseSensitiveFileNames);

        var wildcardDirectories = new Dictionary<string, bool>();
        var wildCardKeyToPath = new Dictionary<string, string>();

        var recursiveKeys = new List<string>();

        foreach (var file in include)
        {
            var spec = TsPathApi.NormalizePath(TsPathApi.CombinePaths(comparePathsOptions.CurrentDirectory, file));
            if (excludeMatcher != null && excludeMatcher.MatchString(spec))
            {
                continue;
            }

            var match = getWildcardDirectoryFromSpec(spec, comparePathsOptions.UseCaseSensitiveFileNames);
            if (match != null)
            {
                var key = match.Key;
                var path = match.Path;
                var recursive = match.Recursive;

                var existsPath = wildCardKeyToPath.TryGetValue(key, out var existingPath);
                var existingRecursive = false;

                if (existsPath)
                {
                    existingRecursive = wildcardDirectories[existingPath!];
                }

                if (!existsPath || (!existingRecursive && recursive))
                {
                    var pathToUse = path;
                    if (existsPath)
                    {
                        pathToUse = existingPath!;
                    }
                    wildcardDirectories[pathToUse] = recursive;

                    if (!existsPath)
                    {
                        wildCardKeyToPath[key] = path;
                    }

                    if (recursive)
                    {
                        recursiveKeys.Add(key);
                    }
                }
            }

            // Remove any subpaths under an existing recursively watched directory
            // map-iter: 序无关 —— 剔除递归目录的子路径，与顺序无关。快照键集合以在删除时安全遍历。
            foreach (var path in wildcardDirectories.Keys.ToArray())
            {
                foreach (var recursiveKey in recursiveKeys)
                {
                    var key = toCanonicalKey(path, comparePathsOptions.UseCaseSensitiveFileNames);
                    if (key != recursiveKey && TsPathApi.ContainsPath(recursiveKey, key, comparePathsOptions))
                    {
                        wildcardDirectories.Remove(path);
                    }
                }
            }
        }

        return wildcardDirectories;
    }

    internal static string toCanonicalKey(string path, bool useCaseSensitiveFileNames)
    {
        if (useCaseSensitiveFileNames)
        {
            return path;
        }
        return OptionsUtil.ToLower(path);
    }

    // wildcardDirectoryMatch represents the result of a wildcard directory match
    internal sealed class wildcardDirectoryMatch
    {
        public string Key = "";
        public string Path = "";
        public bool Recursive;
    }

    internal static wildcardDirectoryMatch? getWildcardDirectoryFromSpec(string spec, bool useCaseSensitiveFileNames)
    {
        // Find the first occurrence of a wildcard character
        var firstWildcard = spec.IndexOfAny(new[] { '*', '?' });
        if (firstWildcard != -1)
        {
            // Find the last directory separator before the wildcard
            var lastSepBeforeWildcard = spec[..firstWildcard].LastIndexOf(TsPathApi.DirectorySeparator);
            if (lastSepBeforeWildcard != -1)
            {
                var path = spec[..lastSepBeforeWildcard];
                var lastDirectorySeparatorIndex = spec.LastIndexOf(TsPathApi.DirectorySeparator);

                // Determine if this should be watched recursively:
                // recursive if the wildcard appears in a directory segment (not just the final file segment)
                var recursive = firstWildcard < lastDirectorySeparatorIndex;

                return new wildcardDirectoryMatch
                {
                    Key = toCanonicalKey(path, useCaseSensitiveFileNames),
                    Path = path,
                    Recursive = recursive,
                };
            }
        }

        var lastSepIndex = spec.LastIndexOf(TsPathApi.DirectorySeparator);
        if (lastSepIndex != -1)
        {
            var lastSegment = spec[(lastSepIndex + 1)..];
            if (VfsMatchApi.IsImplicitGlob(lastSegment))
            {
                var path = TsPathApi.RemoveTrailingDirectorySeparator(spec);
                return new wildcardDirectoryMatch
                {
                    Key = toCanonicalKey(path, useCaseSensitiveFileNames),
                    Path = path,
                    Recursive = true,
                };
            }
        }

        return null;
    }
}
