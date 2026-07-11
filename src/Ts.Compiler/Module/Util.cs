// Ported from tsgo internal/module/util.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Module;

// PORT-NOTE: aliases inside the file-scoped namespace outrank the enclosing `Ts`
// namespace's sibling sub-namespaces (e.g. bare `Diagnostics`/`Core`).
using Diagnostics = Ts.Diagnostics.Diagnostics;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath;
using Version = Ts.Semver.Version;
using VersionRange = Ts.Semver.VersionRange;
using SemverApi = Ts.Semver.Semver;

public static partial class Module
{
    private static readonly Version TypeScriptVersion = SemverApi.MustParse(TsVersion.Version());

    public const string InferredTypesContainingFile = "__inferred type names__.ts";

    public static bool IsApplicableVersionedTypesKey(string key)
    {
        if (!key.StartsWith("types@", StringComparison.Ordinal))
        {
            return false;
        }
        if (!SemverApi.TryParseVersionRange(key.Substring("types@".Length), out VersionRange range))
        {
            return false;
        }
        return range.Test(TypeScriptVersion);
    }

    public static string ParseNodeModuleFromPath(string resolved, bool isFolder)
    {
        string path = TsPathApi.NormalizePath(resolved);
        int idx = path.LastIndexOf("/node_modules/", StringComparison.Ordinal);
        if (idx == -1)
        {
            return "";
        }

        int indexAfterNodeModules = idx + "/node_modules/".Length;
        int indexAfterPackageName = MoveToNextDirectorySeparatorIfAvailable(path, indexAfterNodeModules, isFolder);
        if (path[indexAfterNodeModules] == '@')
        {
            indexAfterPackageName = MoveToNextDirectorySeparatorIfAvailable(path, indexAfterPackageName, isFolder);
        }
        return path.Substring(0, indexAfterPackageName);
    }

    public static (string PackageName, string RestPath) ParsePackageName(string moduleName)
    {
        int idx = moduleName.IndexOf('/');
        if (moduleName.Length > 0 && moduleName[0] == '@')
        {
            int offset = idx + 1;
            // Go: strings.Index(moduleName[offset:], "/") is relative; IndexOf(char, offset) is
            // already absolute — equivalent to Go's `idx += offset` adjustment.
            idx = moduleName.IndexOf('/', offset);
        }
        if (idx == -1)
        {
            return (moduleName, "");
        }
        return (moduleName.Substring(0, idx), moduleName.Substring(idx + 1));
    }

    public static string MangleScopedPackageName(string packageName)
    {
        if (packageName.Length > 0 && packageName[0] == '@')
        {
            int idx = packageName.IndexOf('/');
            if (idx == -1)
            {
                return packageName;
            }
            return packageName.Substring(1, idx - 1) + "__" + packageName.Substring(idx + 1);
        }
        return packageName;
    }

    public static string UnmangleScopedPackageName(string packageName)
    {
        (string before, string after, bool ok) = Cut(packageName, "__");
        if (ok)
        {
            return "@" + before + "/" + after;
        }
        return packageName;
    }

    public static string GetTypesPackageName(string packageName)
    {
        return "@types/" + MangleScopedPackageName(packageName);
    }

    public static string GetPackageNameFromTypesPackageName(string mangledName)
    {
        string withoutAtTypePrefix = mangledName.StartsWith("@types/", StringComparison.Ordinal)
            ? mangledName.Substring("@types/".Length)
            : mangledName;
        if (withoutAtTypePrefix != mangledName)
        {
            return UnmangleScopedPackageName(withoutAtTypePrefix);
        }
        return mangledName;
    }

    public static int ComparePatternKeys(string a, string b)
    {
        int aPatternIndex = a.IndexOf('*');
        int bPatternIndex = b.IndexOf('*');
        int baseLenA = a.Length;
        if (aPatternIndex != -1)
        {
            baseLenA = aPatternIndex + 1;
        }
        int baseLenB = b.Length;
        if (bPatternIndex != -1)
        {
            baseLenB = bPatternIndex + 1;
        }

        if (baseLenA > baseLenB)
        {
            return -1;
        }
        if (baseLenB > baseLenA)
        {
            return 1;
        }
        if (aPatternIndex == -1)
        {
            return 1;
        }
        if (bPatternIndex == -1)
        {
            return -1;
        }
        if (a.Length > b.Length)
        {
            return -1;
        }
        if (b.Length > a.Length)
        {
            return 1;
        }
        return 0;
    }

    // Returns a DiagnosticMessage if we won't include a resolved module due to its extension.
    // The DiagnosticMessage's parameters are the imported module name, and the filename it
    // resolved to. This returns a diagnostic even if the module will be an untyped module.
    public static Message? GetResolutionDiagnostic(CompilerOptions options, ResolvedModule resolvedModule, SourceFile file)
    {
        Message? NeedJsx()
        {
            if (options.Jsx != JsxEmit.None)
            {
                return null;
            }
            return Diagnostics.Module_0_was_resolved_to_1_but_jsx_is_not_set;
        }

        Message? NeedAllowJs()
        {
            if (options.GetAllowJS() || !options.NoImplicitAny.DefaultIfUnknown(options.Strict).IsTrue())
            {
                return null;
            }
            return Diagnostics.Could_not_find_a_declaration_file_for_module_0_1_implicitly_has_an_any_type;
        }

        Message? NeedResolveJsonModule()
        {
            if (options.GetResolveJsonModule())
            {
                return null;
            }
            return Diagnostics.Module_0_was_resolved_to_1_but_resolveJsonModule_is_not_used;
        }

        Message? NeedAllowArbitraryExtensions()
        {
            if (file.IsDeclarationFile || options.AllowArbitraryExtensions.IsTrue())
            {
                return null;
            }
            return Diagnostics.Module_0_was_resolved_to_1_but_allowArbitraryExtensions_is_not_set;
        }

        string ext = resolvedModule.Extension;
        if (ext == TsPathApi.ExtensionTs || ext == TsPathApi.ExtensionDts ||
            ext == TsPathApi.ExtensionMts || ext == TsPathApi.ExtensionDmts ||
            ext == TsPathApi.ExtensionCts || ext == TsPathApi.ExtensionDcts)
        {
            // These are always allowed.
            return null;
        }
        if (ext == TsPathApi.ExtensionTsx)
        {
            return NeedJsx();
        }
        if (ext == TsPathApi.ExtensionJsx)
        {
            Message? message = NeedJsx();
            if (message != null)
            {
                return message;
            }
            return NeedAllowJs();
        }
        if (ext == TsPathApi.ExtensionJs || ext == TsPathApi.ExtensionMjs || ext == TsPathApi.ExtensionCjs)
        {
            return NeedAllowJs();
        }
        if (ext == TsPathApi.ExtensionJson)
        {
            return NeedResolveJsonModule();
        }
        return NeedAllowArbitraryExtensions();
    }

    // TryGetJSExtensionForFile maps TS/JS/DTS extensions to the output JS-side extension.
    // Returns an empty string if the extension is unsupported.
    public static string TryGetJSExtensionForFile(string fileName, CompilerOptions options)
    {
        string ext = TsPathApi.TryGetExtensionFromPath(fileName);
        if (ext == TsPathApi.ExtensionTs || ext == TsPathApi.ExtensionDts)
        {
            return TsPathApi.ExtensionJs;
        }
        if (ext == TsPathApi.ExtensionTsx)
        {
            if (options.Jsx == JsxEmit.Preserve)
            {
                return TsPathApi.ExtensionJsx;
            }
            return TsPathApi.ExtensionJs;
        }
        if (ext == TsPathApi.ExtensionJs || ext == TsPathApi.ExtensionJsx || ext == TsPathApi.ExtensionJson)
        {
            return ext;
        }
        if (ext == TsPathApi.ExtensionDmts || ext == TsPathApi.ExtensionMts || ext == TsPathApi.ExtensionMjs)
        {
            return TsPathApi.ExtensionMjs;
        }
        if (ext == TsPathApi.ExtensionDcts || ext == TsPathApi.ExtensionCts || ext == TsPathApi.ExtensionCjs)
        {
            return TsPathApi.ExtensionCjs;
        }
        return "";
    }

    // Go: strings.Cut — splits around the first occurrence of sep.
    internal static (string Before, string After, bool Found) Cut(string s, string sep)
    {
        int i = s.IndexOf(sep, StringComparison.Ordinal);
        if (i < 0)
        {
            return (s, "", false);
        }
        return (s.Substring(0, i), s.Substring(i + sep.Length), true);
    }

    // Go: strings.Replace(s, old, new, 1) — replaces only the first occurrence.
    internal static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        int i = s.IndexOf(oldValue, StringComparison.Ordinal);
        if (i < 0)
        {
            return s;
        }
        return string.Concat(s.AsSpan(0, i), newValue, s.AsSpan(i + oldValue.Length));
    }
}
