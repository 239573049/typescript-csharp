// Ported from tsgo internal/modulespecifiers/util.go — path/extension/comparison helpers.
// PORT-NOTE (in progress): the regex-exclusion (stringToRegex/IsExcludedByRegex), tsoptions-backed
// tryGetAnyFileFromPath, module-backed getJSExtensionForFile, and the NodeModulePathParts family are
// deferred to the next porting batch; getSpecifierForModuleSymbol's core path does not require them.

using System;

using Ts.Core;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class ModuleSpecifierUtil
{
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
}
