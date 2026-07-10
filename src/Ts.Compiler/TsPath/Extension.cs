// Ported from tsgo internal/tspath/extension.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.TsPath;

public static partial class TsPath
{
    public const string ExtensionTs = ".ts";
    public const string ExtensionTsx = ".tsx";
    public const string ExtensionDts = ".d.ts";
    public const string ExtensionJs = ".js";
    public const string ExtensionJsx = ".jsx";
    public const string ExtensionJson = ".json";
    public const string ExtensionTsBuildInfo = ".tsbuildinfo";
    public const string ExtensionMjs = ".mjs";
    public const string ExtensionMts = ".mts";
    public const string ExtensionDmts = ".d.mts";
    public const string ExtensionCjs = ".cjs";
    public const string ExtensionCts = ".cts";
    public const string ExtensionDcts = ".d.cts";

    public static readonly string[] SupportedDeclarationExtensions = [ExtensionDts, ExtensionDcts, ExtensionDmts];
    public static readonly string[] SupportedTSImplementationExtensions = [ExtensionTs, ExtensionTsx, ExtensionMts, ExtensionCts];
    private static readonly string[] SupportedTSExtensionsForExtractExtension = [ExtensionDts, ExtensionDcts, ExtensionDmts, ExtensionTs, ExtensionTsx, ExtensionMts, ExtensionCts];
    public static readonly string[][] AllSupportedExtensions = [[ExtensionTs, ExtensionTsx, ExtensionDts, ExtensionJs, ExtensionJsx], [ExtensionCts, ExtensionDcts, ExtensionCjs], [ExtensionMts, ExtensionDmts, ExtensionMjs]];
    public static readonly string[][] SupportedTSExtensions = [[ExtensionTs, ExtensionTsx, ExtensionDts], [ExtensionCts, ExtensionDcts], [ExtensionMts, ExtensionDmts]];
    public static readonly string[] SupportedTSExtensionsFlat = [ExtensionTs, ExtensionTsx, ExtensionDts, ExtensionCts, ExtensionDcts, ExtensionMts, ExtensionDmts];
    public static readonly string[][] SupportedJSExtensions = [[ExtensionJs, ExtensionJsx], [ExtensionMjs], [ExtensionCjs]];
    public static readonly string[] SupportedJSExtensionsFlat = [ExtensionJs, ExtensionJsx, ExtensionMjs, ExtensionCjs];
    // Go: slices.Concat(...)
    public static readonly string[][] AllSupportedExtensionsWithJson = [.. AllSupportedExtensions, [ExtensionJson]];
    public static readonly string[][] SupportedTSExtensionsWithJson = [.. SupportedTSExtensions, [ExtensionJson]];
    public static readonly string[] SupportedTSExtensionsWithJsonFlat = [.. SupportedTSExtensionsFlat, ExtensionJson];
    public static readonly string[] ExtensionsNotSupportingExtensionlessResolution = [ExtensionMts, ExtensionDmts, ExtensionMjs, ExtensionCts, ExtensionDcts, ExtensionCjs];

    public static bool ExtensionIsTs(string ext)
    {
        return ext == ExtensionTs || ext == ExtensionTsx || ext == ExtensionDts || ext == ExtensionMts || ext == ExtensionDmts || ext == ExtensionCts || ext == ExtensionDcts ||
            ext.Length >= 7 && ext.StartsWith(".d.", StringComparison.Ordinal) && ext.EndsWith(".ts", StringComparison.Ordinal);
    }

    private static readonly string[] ExtensionsToRemove = [ExtensionDts, ExtensionDmts, ExtensionDcts, ExtensionMjs, ExtensionMts, ExtensionCjs, ExtensionCts, ExtensionTs, ExtensionJs, ExtensionTsx, ExtensionJsx, ExtensionJson];

    public static string RemoveFileExtension(string path)
    {
        // Remove any known extension even if it has more than one dot
        foreach (string ext in ExtensionsToRemove)
        {
            if (path.EndsWith(ext, StringComparison.Ordinal))
            {
                return path[..^ext.Length];
            }
        }

        return path;
    }

    public static string TryGetExtensionFromPath(string p)
    {
        foreach (string ext in ExtensionsToRemove)
        {
            if (FileExtensionIs(p, ext))
            {
                return ext;
            }
        }
        return "";
    }

    public static string RemoveExtension(string path, string extension)
    {
        return path[..^extension.Length];
    }

    public static bool FileExtensionIsOneOf(string path, string[] extensions)
    {
        foreach (string ext in extensions)
        {
            if (FileExtensionIs(path, ext))
            {
                return true;
            }
        }
        return false;
    }

    public static string TryExtractTSExtension(string fileName)
    {
        foreach (string ext in SupportedTSExtensionsForExtractExtension)
        {
            if (FileExtensionIs(fileName, ext))
            {
                return ext;
            }
        }
        return "";
    }

    public static bool HasTSFileExtension(string path)
    {
        return FileExtensionIsOneOf(path, SupportedTSExtensionsFlat);
    }

    public static bool HasImplementationTSFileExtension(string path)
    {
        return FileExtensionIsOneOf(path, SupportedTSImplementationExtensions) && !IsDeclarationFileName(path);
    }

    public static bool HasJSFileExtension(string path)
    {
        return FileExtensionIsOneOf(path, SupportedJSExtensionsFlat);
    }

    public static bool HasJSONFileExtension(string path)
    {
        return FileExtensionIs(path, ExtensionJson);
    }

    public static bool IsDeclarationFileName(string fileName)
    {
        return GetDeclarationFileExtension(fileName) != "";
    }

    public static bool ExtensionIsOneOf(string ext, string[] extensions)
    {
        // Go: slices.Contains
        return Array.IndexOf(extensions, ext) >= 0;
    }

    public static string GetDeclarationFileExtension(string fileName)
    {
        string baseFileName = GetBaseFileName(fileName);
        foreach (string ext in SupportedDeclarationExtensions)
        {
            if (baseFileName.EndsWith(ext, StringComparison.Ordinal))
            {
                return ext;
            }
        }
        if (baseFileName.EndsWith(ExtensionTs, StringComparison.Ordinal))
        {
            int index = baseFileName.IndexOf(".d.", StringComparison.Ordinal);
            if (index >= 0)
            {
                return baseFileName[index..];
            }
        }
        return "";
    }

    // PORT-NOTE: the Go switch's inline slice literals are hoisted to static arrays
    // (analyzer CA1861: avoid repeatedly allocated constant array arguments).
    private static readonly string[] MjsMtsExtensions = [ExtensionMjs, ExtensionMts];
    private static readonly string[] CjsCtsExtensions = [ExtensionCjs, ExtensionCts];
    private static readonly string[] TsTsxJsJsxExtensions = [ExtensionTs, ExtensionTsx, ExtensionJs, ExtensionJsx];
    private static readonly string[] DmtsMjsMtsExtensions = [ExtensionDmts, ExtensionMjs, ExtensionMts];
    private static readonly string[] DctsCjsCtsExtensions = [ExtensionDcts, ExtensionCjs, ExtensionCts];

    public static string GetDeclarationEmitExtensionForPath(string path)
    {
        // Go: switch { case ... }
        if (FileExtensionIsOneOf(path, MjsMtsExtensions))
        {
            return ExtensionDmts;
        }
        if (FileExtensionIsOneOf(path, CjsCtsExtensions))
        {
            return ExtensionDcts;
        }
        if (FileExtensionIsOneOf(path, TsTsxJsJsxExtensions))
        {
            return ExtensionDts;
        }
        string ext = GetAnyExtensionFromPath(path, null, false);
        if (ext != "")
        {
            return ".d" + ext + ".ts";
        }
        return ExtensionDts;
    }

    // ChangeAnyExtension changes the extension of a path to the provided extension if it has one of the provided extensions.
    //
    // ChangeAnyExtension("/path/to/file.ext", ".js", ".ext") === "/path/to/file.js"
    // ChangeAnyExtension("/path/to/file.ext", ".js", ".ts") === "/path/to/file.ext"
    // ChangeAnyExtension("/path/to/file.ext", ".js", [".ext", ".ts"]) === "/path/to/file.js"
    public static string ChangeAnyExtension(string path, string ext, string[]? extensions, bool ignoreCase)
    {
        string pathext = GetAnyExtensionFromPath(path, extensions, ignoreCase);
        if (pathext != "")
        {
            string result = path[..^pathext.Length];
            if (ext == "")
            {
                return result;
            }
            if (ext.StartsWith('.'))
            {
                return result + ext;
            }
            return result + "." + ext;
        }
        return path;
    }

    public static string ChangeExtension(string path, string newExtension)
    {
        return ChangeAnyExtension(path, newExtension, ExtensionsToRemove, false /*ignoreCase*/);
    }

    // Like `changeAnyExtension`, but declaration file extensions are recognized
    // and replaced starting from the `.d`.
    //
    //	changeAnyExtension("file.d.ts", ".js") === "file.d.js"
    //	changeFullExtension("file.d.ts", ".js") === "file.js"
    public static string ChangeFullExtension(string path, string newExtension)
    {
        string declarationExtension = GetDeclarationFileExtension(path);
        if (declarationExtension != "")
        {
            string ext = newExtension;
            if (!ext.StartsWith('.'))
            {
                ext = "." + ext;
            }
            return path[..^declarationExtension.Length] + ext;
        }
        return ChangeExtension(path, newExtension);
    }

    public static string[] GetPossibleOriginalInputExtensionForExtension(string path)
    {
        if (FileExtensionIsOneOf(path, DmtsMjsMtsExtensions))
        {
            return [ExtensionMts, ExtensionMjs];
        }
        if (FileExtensionIsOneOf(path, DctsCjsCtsExtensions))
        {
            return [ExtensionCts, ExtensionCjs];
        }
        // Handle any custom .d.x.ts extension (e.g., .d.json.ts -> .json, .d.css.ts -> .css)
        string ext = GetDeclarationFileExtension(path);
        if (ext != "" && ext != ExtensionDts)
        {
            string inner = ext[".d.".Length..^".ts".Length];
            return ["." + inner];
        }
        return [ExtensionTsx, ExtensionTs, ExtensionJsx, ExtensionJs];
    }
}
