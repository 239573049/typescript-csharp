// Ported from tsgo internal/modulespecifiers/specifiers.go — the package.json exports/imports
// resolution chain.

using System;
using System.Collections.Generic;
using System.Linq;

using Ts.Collections;
using Ts.Core;
using Ts.PackageJson;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class Specifiers
{
    // specifiers.go:976 — tryGetModuleNameFromExports.
    internal static string TryGetModuleNameFromExports(
        CompilerOptions options,
        ModuleSpecifierGenerationHost host,
        string targetFilePath,
        string packageDirectory,
        string packageName,
        ExportsOrImports exports,
        string[] conditions)
    {
        if (exports.IsSubpaths())
        {
            // sub-mappings: directory (key ends with /), pattern (contains *), or exact.
            // map-iter: 已排序 — first match over the exports object's insertion order.
            foreach ((string k, ExportsOrImports subk) in exports.AsObject().Entries())
            {
                string subPackageName = TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(packageName, k), "");
                MatchingMode mode = MatchingMode.Exact;
                if (k.EndsWith("/", StringComparison.Ordinal))
                {
                    mode = MatchingMode.Directory;
                }
                else if (k.Contains("*", StringComparison.Ordinal))
                {
                    mode = MatchingMode.Pattern;
                }
                string result = TryGetModuleNameFromExportsOrImports(options, host, targetFilePath, packageDirectory, subPackageName, subk, conditions, mode, false /*isImports*/, false /*preferTsExtension*/);
                if (result.Length > 0)
                {
                    return result;
                }
            }
        }
        return TryGetModuleNameFromExportsOrImports(options, host, targetFilePath, packageDirectory, packageName, exports, conditions, MatchingMode.Exact, false /*isImports*/, false /*preferTsExtension*/);
    }

    // specifiers.go:1019 — tryGetModuleNameFromPackageJsonImports.
    internal static string TryGetModuleNameFromPackageJsonImports(
        string moduleFileName,
        string sourceDirectory,
        CompilerOptions options,
        ModuleSpecifierGenerationHost host,
        ResolutionMode importMode,
        bool preferTsExtension)
    {
        if (!options.GetResolvePackageJsonImports())
        {
            return "";
        }

        string ancestorDirectoryWithPackageJson = host.GetNearestAncestorDirectoryWithPackageJson(sourceDirectory);
        if (ancestorDirectoryWithPackageJson.Length == 0)
        {
            return "";
        }
        string packageJsonPath = TsPathApi.CombinePaths(ancestorDirectoryWithPackageJson, "package.json");

        var info = host.GetPackageJsonInfo(packageJsonPath);
        if (info == null)
        {
            return "";
        }

        var contents = info.GetContents();
        if (contents == null)
        {
            return "";
        }
        ExportsOrImports imports = contents.Imports;
        switch (imports.Type)
        {
            case JSONValueType.JSONValueTypeNotPresent:
            case JSONValueType.JSONValueTypeArray:
            case JSONValueType.JSONValueTypeString:
                return ""; // not present or invalid for imports
            case JSONValueType.JSONValueTypeObject:
            {
                string[] conditions = global::Ts.Module.Module.GetConditions(options, importMode);
                OrderedMap<string, ExportsOrImports> top = imports.AsObject();
                // map-iter: 已排序 — first match over the imports object's insertion order.
                foreach ((string k, ExportsOrImports value) in top.Entries())
                {
                    if (k == "#" || k == "#/" || !k.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue; // invalid imports entry
                    }
                    if (k.StartsWith("#/", StringComparison.Ordinal) &&
                        options.GetModuleResolutionKind() != ModuleResolutionKind.NodeNext &&
                        options.GetModuleResolutionKind() != ModuleResolutionKind.Bundler)
                    {
                        continue; // "#/" imports keys are only valid in nodenext/bundler
                    }
                    MatchingMode mode = MatchingMode.Exact;
                    if (k.EndsWith("/", StringComparison.Ordinal))
                    {
                        mode = MatchingMode.Directory;
                    }
                    else if (k.Contains("*", StringComparison.Ordinal))
                    {
                        mode = MatchingMode.Pattern;
                    }
                    string result = TryGetModuleNameFromExportsOrImports(options, host, moduleFileName, ancestorDirectoryWithPackageJson, k, value, conditions, mode, true /*isImports*/, preferTsExtension);
                    if (result.Length > 0)
                    {
                        return result;
                    }
                }
                break;
            }
        }
        return "";
    }

    // specifiers.go:1199 — tryGetModuleNameFromExportsOrImports.
    internal static string TryGetModuleNameFromExportsOrImports(
        CompilerOptions options,
        ModuleSpecifierGenerationHost host,
        string targetFilePath,
        string packageDirectory,
        string packageName,
        ExportsOrImports exports,
        string[] conditions,
        MatchingMode mode,
        bool isImports,
        bool preferTsExtension)
    {
        switch (exports.Type)
        {
            case JSONValueType.JSONValueTypeNotPresent:
                return "";
            case JSONValueType.JSONValueTypeString:
            {
                string strValue = (string)exports.Value!;

                // PORT-NOTE (matches Go): always uses the host project's compilerOptions, not those of
                // the targeted package.json.
                string outputFile = "";
                string declarationFile = "";
                if (isImports)
                {
                    outputFile = global::Ts.OutputPaths.OutputPaths.GetOutputJSFileNameWorker(targetFilePath, options, host);
                    declarationFile = global::Ts.OutputPaths.OutputPaths.GetOutputDeclarationFileNameWorker(targetFilePath, options, host);
                }

                string pathOrPattern = TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(packageDirectory, strValue), "");
                string extensionSwappedTarget = "";
                if (TsPathApi.HasTSFileExtension(targetFilePath))
                {
                    extensionSwappedTarget = TsPathApi.RemoveFileExtension(targetFilePath) + global::Ts.Module.Module.TryGetJSExtensionForFile(targetFilePath, options);
                }
                bool canTryTsExtension = preferTsExtension && TsPathApi.HasImplementationTSFileExtension(targetFilePath);

                var compareOpts = new ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                    CurrentDirectory = host.GetCurrentDirectory(),
                };

                switch (mode)
                {
                    case MatchingMode.Exact:
                        if ((extensionSwappedTarget.Length > 0 && TsPathApi.ComparePaths(extensionSwappedTarget, pathOrPattern, compareOpts) == 0) ||
                            TsPathApi.ComparePaths(targetFilePath, pathOrPattern, compareOpts) == 0 ||
                            (outputFile.Length > 0 && TsPathApi.ComparePaths(outputFile, pathOrPattern, compareOpts) == 0) ||
                            (declarationFile.Length > 0 && TsPathApi.ComparePaths(declarationFile, pathOrPattern, compareOpts) == 0))
                        {
                            return packageName;
                        }
                        break;
                    case MatchingMode.Directory:
                        if (canTryTsExtension && TsPathApi.ContainsPath(targetFilePath, pathOrPattern, compareOpts))
                        {
                            string fragment = TsPathApi.GetRelativePathFromDirectory(pathOrPattern, targetFilePath, compareOpts);
                            return TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(TsPathApi.CombinePaths(packageName, strValue), fragment), "");
                        }
                        if (extensionSwappedTarget.Length > 0 && TsPathApi.ContainsPath(pathOrPattern, extensionSwappedTarget, compareOpts))
                        {
                            string fragment = TsPathApi.GetRelativePathFromDirectory(pathOrPattern, extensionSwappedTarget, compareOpts);
                            return TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(TsPathApi.CombinePaths(packageName, strValue), fragment), "");
                        }
                        if (!canTryTsExtension && TsPathApi.ContainsPath(pathOrPattern, targetFilePath, compareOpts))
                        {
                            string fragment = TsPathApi.GetRelativePathFromDirectory(pathOrPattern, targetFilePath, compareOpts);
                            return TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(TsPathApi.CombinePaths(packageName, strValue), fragment), "");
                        }
                        if (outputFile.Length > 0 && TsPathApi.ContainsPath(pathOrPattern, outputFile, compareOpts))
                        {
                            string fragment = TsPathApi.GetRelativePathFromDirectory(pathOrPattern, outputFile, compareOpts);
                            return TsPathApi.CombinePaths(packageName, fragment);
                        }
                        if (declarationFile.Length > 0 && TsPathApi.ContainsPath(pathOrPattern, declarationFile, compareOpts))
                        {
                            string fragment = TsPathApi.GetRelativePathFromDirectory(pathOrPattern, declarationFile, compareOpts);
                            string jsExtension = ModuleSpecifierUtil.GetJSExtensionForFile(declarationFile, options);
                            string fragmentWithJsExtension = TsPathApi.ChangeExtension(fragment, jsExtension);
                            return TsPathApi.CombinePaths(packageName, fragmentWithJsExtension);
                        }
                        break;
                    case MatchingMode.Pattern:
                    {
                        int starIdx = pathOrPattern.IndexOf('*');
                        string leadingSlice = starIdx < 0 ? pathOrPattern : pathOrPattern[..starIdx];
                        string trailingSlice = starIdx < 0 ? "" : pathOrPattern[(starIdx + 1)..];
                        bool caseSensitive = host.UseCaseSensitiveFileNames();
                        if (canTryTsExtension && global::Ts.StringUtil.StringUtil.HasPrefixAndSuffixWithoutOverlap(targetFilePath, leadingSlice, trailingSlice, caseSensitive))
                        {
                            string starReplacement = targetFilePath[leadingSlice.Length..(targetFilePath.Length - trailingSlice.Length)];
                            return ModuleSpecifierUtil.ReplaceFirstStar(packageName, starReplacement);
                        }
                        if (extensionSwappedTarget.Length > 0 && global::Ts.StringUtil.StringUtil.HasPrefixAndSuffixWithoutOverlap(extensionSwappedTarget, leadingSlice, trailingSlice, caseSensitive))
                        {
                            string starReplacement = extensionSwappedTarget[leadingSlice.Length..(extensionSwappedTarget.Length - trailingSlice.Length)];
                            return ModuleSpecifierUtil.ReplaceFirstStar(packageName, starReplacement);
                        }
                        if (!canTryTsExtension && global::Ts.StringUtil.StringUtil.HasPrefixAndSuffixWithoutOverlap(targetFilePath, leadingSlice, trailingSlice, caseSensitive))
                        {
                            string starReplacement = targetFilePath[leadingSlice.Length..(targetFilePath.Length - trailingSlice.Length)];
                            return ModuleSpecifierUtil.ReplaceFirstStar(packageName, starReplacement);
                        }
                        if (outputFile.Length > 0 && global::Ts.StringUtil.StringUtil.HasPrefixAndSuffixWithoutOverlap(outputFile, leadingSlice, trailingSlice, caseSensitive))
                        {
                            string starReplacement = outputFile[leadingSlice.Length..(outputFile.Length - trailingSlice.Length)];
                            return ModuleSpecifierUtil.ReplaceFirstStar(packageName, starReplacement);
                        }
                        if (declarationFile.Length > 0 && global::Ts.StringUtil.StringUtil.HasPrefixAndSuffixWithoutOverlap(declarationFile, leadingSlice, trailingSlice, caseSensitive))
                        {
                            string starReplacement = declarationFile[leadingSlice.Length..(declarationFile.Length - trailingSlice.Length)];
                            string substituted = ModuleSpecifierUtil.ReplaceFirstStar(packageName, starReplacement);
                            string jsExtension = global::Ts.Module.Module.TryGetJSExtensionForFile(declarationFile, options);
                            if (jsExtension.Length > 0)
                            {
                                return TsPathApi.ChangeFullExtension(substituted, jsExtension);
                            }
                        }
                        break;
                    }
                }
                return "";
            }
            case JSONValueType.JSONValueTypeArray:
            {
                List<ExportsOrImports> arr = exports.AsArray();
                foreach (ExportsOrImports e in arr)
                {
                    string result = TryGetModuleNameFromExportsOrImports(options, host, targetFilePath, packageDirectory, packageName, e, conditions, mode, isImports, preferTsExtension);
                    if (result.Length > 0)
                    {
                        return result;
                    }
                }
                break;
            }
            case JSONValueType.JSONValueTypeObject:
            {
                // conditional mapping
                OrderedMap<string, ExportsOrImports> obj = exports.AsObject();
                // map-iter: 已排序 — first match over the conditional object's insertion order.
                foreach ((string key, ExportsOrImports value) in obj.Entries())
                {
                    if (key == "default" || conditions.Contains(key) || (conditions.Contains("types") && global::Ts.Module.Module.IsApplicableVersionedTypesKey(key)))
                    {
                        string result = TryGetModuleNameFromExportsOrImports(options, host, targetFilePath, packageDirectory, packageName, value, conditions, mode, isImports, preferTsExtension);
                        if (result.Length > 0)
                        {
                            return result;
                        }
                    }
                }
                break;
            }
            case JSONValueType.JSONValueTypeNull:
                return "";
        }
        return "";
    }
}
