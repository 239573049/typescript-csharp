// Ported from tsgo internal/modulespecifiers/util.go — path/extension/comparison helpers.
// PORT-NOTE (in progress): the regex-exclusion (stringToRegex/IsExcludedByRegex), tsoptions-backed
// tryGetAnyFileFromPath, module-backed getJSExtensionForFile, and the NodeModulePathParts family are
// deferred to the next porting batch; getSpecifierForModuleSymbol's core path does not require them.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using Ts.Core;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class ModuleSpecifierUtil
{
    // util.go:23 — regexPatternCache (+ its RWMutex). PORT-NOTE: Go compiles RE2 via regexp; .NET has
    // no RE2 engine, so user exclude-patterns compile through System.Text.RegularExpressions (backtracking,
    // CultureInvariant to honor InvariantGlobalization). Only reached for forAutoImports auto-import
    // filtering, which getSpecifierForModuleSymbol never triggers.
    private static readonly object s_regexCacheLock = new();
    private static readonly Dictionary<(string Pattern, bool CaseInsensitive), Regex?> s_regexCache = new();

    // util.go:46 — IsExcludedByRegex.
    internal static bool IsExcludedByRegex(string moduleSpecifier, string[]? excludes)
    {
        foreach (string pattern in excludes ?? [])
        {
            Regex? re = StringToRegex(pattern);
            if (re == null)
            {
                continue;
            }
            if (re.IsMatch(moduleSpecifier))
            {
                return true;
            }
        }
        return false;
    }

    // util.go:59 — stringToRegex.
    private static Regex? StringToRegex(string pattern)
    {
        bool caseInsensitive = false;

        if (pattern.Length > 2 && pattern[0] == '/')
        {
            int lastSlash = pattern.LastIndexOf('/');
            if (lastSlash > 0)
            {
                bool hasUnescapedMiddleSlash = false;
                for (int i = 1; i < lastSlash; i++)
                {
                    if (pattern[i] == '/' && (i == 0 || pattern[i - 1] != '\\'))
                    {
                        hasUnescapedMiddleSlash = true;
                        break;
                    }
                }

                if (!hasUnescapedMiddleSlash)
                {
                    string flags = pattern[(lastSlash + 1)..];
                    pattern = pattern[1..lastSlash];
                    foreach (char flag in flags)
                    {
                        if (flag == 'i')
                        {
                            caseInsensitive = true;
                        }
                    }
                }
            }
        }

        var key = (pattern, caseInsensitive);
        lock (s_regexCacheLock)
        {
            if (s_regexCache.TryGetValue(key, out Regex? cached))
            {
                return cached;
            }
            if (s_regexCache.Count > 1000)
            {
                s_regexCache.Clear();
            }
            Regex? compiled;
            try
            {
                var options = RegexOptions.CultureInvariant | (caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None);
                compiled = new Regex(pattern, options);
            }
            catch (ArgumentException)
            {
                compiled = null;
            }
            s_regexCache[key] = compiled;
            return compiled;
        }
    }

    // util.go:29 — comparePathsByRedirect.
    internal static int ComparePathsByRedirect(ModulePath a, ModulePath b, bool useCaseSensitiveFileNames)
    {
        // Redirects sort first, matching Strada's compareBooleans(b.isRedirect, a.isRedirect).
        int c = global::Ts.Core.CoreUtil.CompareBooleans(b.IsRedirect, a.IsRedirect);
        if (c != 0)
        {
            return c;
        }
        c = TsPathApi.CompareNumberOfDirectorySeparators(a.FileName, b.FileName);
        if (c != 0)
        {
            return c;
        }
        // Strada relies on Map insertion order to break remaining ties deterministically; Go maps are
        // unordered, so compare paths to keep the ordering stable.
        return TsPathApi.ComparePaths(a.FileName, b.FileName, new ComparePathsOptions { UseCaseSensitiveFileNames = useCaseSensitiveFileNames });
    }

    // util.go:42 — PathIsBareSpecifier.
    public static bool PathIsBareSpecifier(string path) =>
        !TsPathApi.PathIsAbsolute(path) && !TsPathApi.PathIsRelative(path);

    // util.go:136 — ensurePathIsNonModuleName. Ensures a path is absolute or dot-relative so it is not
    // confused with an unprefixed module name.
    internal static string EnsurePathIsNonModuleName(string path) =>
        PathIsBareSpecifier(path) ? "./" + path : path;

    // util.go:143 — GetJSExtensionForDeclarationFileExtension.
    public static string GetJSExtensionForDeclarationFileExtension(string ext) => ext switch
    {
        TsPathApi.ExtensionDts => TsPathApi.ExtensionJs,
        TsPathApi.ExtensionDmts => TsPathApi.ExtensionMjs,
        TsPathApi.ExtensionDcts => TsPathApi.ExtensionCjs,
        // .d.json.ts and the like
        _ => ext[".d".Length..(ext.Length - TsPathApi.ExtensionTs.Length)],
    };

    // util.go:159 — TryGetRealFileNameForNonJSDeclarationFileName. Remaps files like `foo.d.json.ts`
    // back to their real non-JS names.
    public static string TryGetRealFileNameForNonJSDeclarationFileName(string fileName)
    {
        string baseName = TsPathApi.GetBaseFileName(fileName);
        // Ends with .ts, contains ".d.", and is NOT a standard .d.ts file
        if (!fileName.EndsWith(TsPathApi.ExtensionTs, StringComparison.Ordinal) ||
            !baseName.Contains(".d.", StringComparison.Ordinal) ||
            baseName.EndsWith(TsPathApi.ExtensionDts, StringComparison.Ordinal))
        {
            return "";
        }
        string noExtension = TsPathApi.RemoveExtension(fileName, TsPathApi.ExtensionTs);
        int lastDotIndex = noExtension.LastIndexOf('.');
        string ext = noExtension[lastDotIndex..];
        int cut = noExtension.IndexOf(".d.", StringComparison.Ordinal);
        string before = cut < 0 ? noExtension : noExtension[..cut];
        return before + ext;
    }

    // util.go:174 — getJSExtensionForFile.
    internal static string GetJSExtensionForFile(string fileName, CompilerOptions options)
    {
        string result = global::Ts.Module.Module.TryGetJSExtensionForFile(fileName, options);
        if (result.Length == 0)
        {
            throw new InvalidOperationException($"Extension {ExtensionFromPath(fileName)} is unsupported:: FileName:: {fileName}");
        }
        return result;
    }

    // util.go:186 — extensionFromPath. Path must have a valid extension.
    internal static string ExtensionFromPath(string path)
    {
        string ext = TsPathApi.TryGetExtensionFromPath(path);
        if (ext.Length == 0)
        {
            throw new InvalidOperationException($"File {path} has unknown extension.");
        }
        return ext;
    }

    // util.go:225 — getPathsRelativeToRootDirs.
    internal static string[]? GetPathsRelativeToRootDirs(string path, string[]? rootDirs, bool useCaseSensitiveFileNames)
    {
        System.Collections.Generic.List<string>? results = null;
        foreach (string rootDir in rootDirs ?? [])
        {
            string relativePath = GetRelativePathIfInSameVolume(path, rootDir, useCaseSensitiveFileNames);
            if (!IsPathRelativeToParent(relativePath))
            {
                (results ??= []).Add(relativePath);
            }
        }
        return results?.ToArray();
    }

    // util.go:236 — isPathRelativeToParent.
    internal static bool IsPathRelativeToParent(string path) =>
        path.StartsWith("..", StringComparison.Ordinal);

    // util.go:240 — getRelativePathIfInSameVolume.
    internal static string GetRelativePathIfInSameVolume(string path, string directoryPath, bool useCaseSensitiveFileNames)
    {
        string relativePath = TsPathApi.GetRelativePathToDirectoryOrUrl(directoryPath, path, false, new ComparePathsOptions
        {
            UseCaseSensitiveFileNames = useCaseSensitiveFileNames,
            CurrentDirectory = directoryPath,
        });
        if (TsPathApi.IsRootedDiskPath(relativePath))
        {
            return "";
        }
        return relativePath;
    }

    // util.go:251 — packageJsonPathsAreEqual.
    internal static bool PackageJsonPathsAreEqual(string a, string b, ComparePathsOptions options)
    {
        if (a == b)
        {
            return true;
        }
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }
        return TsPathApi.ComparePaths(a, b, options) == 0;
    }

    // util.go:261 — prefersTsExtension.
    internal static bool PrefersTsExtension(ModuleSpecifierEnding[] allowedEndings)
    {
        int jsPriority = Array.IndexOf(allowedEndings, ModuleSpecifierEnding.JsExtension);
        int tsPriority = Array.IndexOf(allowedEndings, ModuleSpecifierEnding.TsExtension);
        if (tsPriority > -1)
        {
            return tsPriority < jsPriority;
        }
        return false;
    }

    // util.go:270 — replaceFirstStar.
    internal static string ReplaceFirstStar(string s, string replacement)
    {
        int i = s.IndexOf('*');
        return i < 0 ? s : s[..i] + replacement + s[(i + 1)..];
    }

    // util.go:283 — nodeModulesPathParseState.
    private enum NodeModulesPathParseState : byte
    {
        BeforeNodeModules,
        NodeModules,
        Scope,
        PackageContent,
    }

    // util.go:290 — GetNodeModulePathParts. Returns null when fullPath can't be a valid module file
    // within node_modules. Expected pattern:
    // /base/path/node_modules/[@scope/otherpackage/@otherscope/node_modules/]package/[subdirectory/]file.js
    public static NodeModulePathParts? GetNodeModulePathParts(string fullPath)
    {
        int topLevelNodeModulesIndex = 0;
        int topLevelPackageNameIndex = 0;
        int packageRootIndex = 0;

        int partStart = 0;
        int partEnd = 0;
        var state = NodeModulesPathParseState.BeforeNodeModules;

        while (partEnd >= 0)
        {
            partStart = partEnd;
            partEnd = global::Ts.Core.CoreUtil.IndexAfter(fullPath, "/", partStart + 1);
            switch (state)
            {
                case NodeModulesPathParseState.BeforeNodeModules:
                    if (fullPath.AsSpan(partStart).StartsWith("/node_modules/"))
                    {
                        topLevelNodeModulesIndex = partStart;
                        topLevelPackageNameIndex = partEnd;
                        state = NodeModulesPathParseState.NodeModules;
                    }
                    break;
                case NodeModulesPathParseState.NodeModules:
                case NodeModulesPathParseState.Scope:
                    if (state == NodeModulesPathParseState.NodeModules && fullPath[partStart + 1] == '@')
                    {
                        state = NodeModulesPathParseState.Scope;
                    }
                    else
                    {
                        packageRootIndex = partEnd;
                        state = NodeModulesPathParseState.PackageContent;
                    }
                    break;
                case NodeModulesPathParseState.PackageContent:
                    if (fullPath.AsSpan(partStart).StartsWith("/node_modules/"))
                    {
                        state = NodeModulesPathParseState.NodeModules;
                    }
                    else
                    {
                        state = NodeModulesPathParseState.PackageContent;
                    }
                    break;
            }
        }

        int fileNameIndex = partStart;

        if (state > NodeModulesPathParseState.NodeModules)
        {
            return new NodeModulePathParts
            {
                TopLevelNodeModulesIndex = topLevelNodeModulesIndex,
                TopLevelPackageNameIndex = topLevelPackageNameIndex,
                PackageRootIndex = packageRootIndex,
                FileNameIndex = fileNameIndex,
            };
        }
        return null;
    }

    // util.go:370 — GetPackageNameFromDirectory.
    public static string GetPackageNameFromDirectory(string fileOrDirectoryPath)
    {
        int idx = fileOrDirectoryPath.LastIndexOf("/node_modules/", StringComparison.Ordinal);
        if (idx == -1)
        {
            return "";
        }

        string basename = fileOrDirectoryPath[(idx + "/node_modules/".Length)..];
        if (basename[0] == '.')
        {
            return "";
        }

        int nextSlash = basename.IndexOf('/');
        if (nextSlash == -1)
        {
            return basename;
        }

        if (basename[0] != '@' || nextSlash == basename.Length - 1)
        {
            return basename[..nextSlash];
        }

        int secondSlash = basename[(nextSlash + 1)..].IndexOf('/');
        if (secondSlash == -1)
        {
            return basename;
        }

        return basename[..(nextSlash + 1 + secondSlash)];
    }
}

// util.go:283 — NodeModulePathParts (Go returns *NodeModulePathParts; class preserves the nullable
// pointer semantics).
public sealed class NodeModulePathParts
{
    public int TopLevelNodeModulesIndex;
    public int TopLevelPackageNameIndex;
    public int PackageRootIndex;
    public int FileNameIndex;
}
