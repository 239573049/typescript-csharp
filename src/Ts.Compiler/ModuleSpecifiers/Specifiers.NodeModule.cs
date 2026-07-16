// Ported from tsgo internal/modulespecifiers/specifiers.go — node_modules package-name resolution.

using System;

using Ts.Core;
using Ts.PackageJson;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class Specifiers
{
    // specifiers.go:825 — pkgJsonDirAttemptResult.
    private struct PkgJsonDirAttemptResult
    {
        public string ModuleFileToTry;
        public string PackageRootPath;
        public bool BlockedByExports;
        public bool VerbatimFromExports;
    }

    // specifiers.go:743 — tryGetModuleNameAsNodeModule.
    internal static string TryGetModuleNameAsNodeModule(
        ModulePath pathObj,
        Info info,
        SourceFileForSpecifierGeneration importingSourceFile,
        ModuleSpecifierGenerationHost host,
        CompilerOptions options,
        UserPreferences userPreferences,
        bool packageNameOnly,
        ResolutionMode overrideMode)
    {
        NodeModulePathParts? parts = ModuleSpecifierUtil.GetNodeModulePathParts(pathObj.FileName);
        if (parts == null)
        {
            return "";
        }

        // Simplify the full file path to something that can be resolved by Node.
        ModuleSpecifierPreferences preferences = GetModuleSpecifierPreferences(userPreferences, host, options, importingSourceFile, "");
        ModuleSpecifierEnding[] allowedEndings = preferences.GetAllowedEndingsInPreferredOrder(global::Ts.Core.Core.ResolutionModeNone);

        bool caseSensitive = host.UseCaseSensitiveFileNames();
        string moduleSpecifier = pathObj.FileName;
        bool isPackageRootPath = false;
        if (!packageNameOnly)
        {
            int packageRootIndex = parts.PackageRootIndex;
            string moduleFileName = "";
            while (true)
            {
                // If the module could be imported by a directory name, use that directory's name.
                PkgJsonDirAttemptResult pkgJsonResults = TryDirectoryWithPackageJson(parts, pathObj, importingSourceFile, host, overrideMode, options, allowedEndings);
                string moduleFileToTry = pkgJsonResults.ModuleFileToTry ?? "";
                string packageRootPath = pkgJsonResults.PackageRootPath ?? "";
                if (pkgJsonResults.BlockedByExports)
                {
                    return ""; // Under this package.json, but not publicly exported — unnameable via node_modules resolution.
                }
                if (pkgJsonResults.VerbatimFromExports)
                {
                    return moduleFileToTry;
                }
                if (packageRootPath.Length > 0)
                {
                    moduleSpecifier = packageRootPath;
                    isPackageRootPath = true;
                    break;
                }
                if (moduleFileName.Length == 0)
                {
                    moduleFileName = moduleFileToTry;
                }
                // Try with the next level of directory.
                packageRootIndex = global::Ts.Core.CoreUtil.IndexAfter(pathObj.FileName, "/", packageRootIndex + 1);
                if (packageRootIndex == -1)
                {
                    moduleSpecifier = ProcessEnding(moduleFileName, allowedEndings, options, host);
                    break;
                }
            }
        }

        if (pathObj.IsRedirect && !isPackageRootPath)
        {
            return "";
        }

        string globalTypingsCacheLocation = host.GetGlobalTypingsCacheLocation();
        // Get a path relative to node_modules or the importing file's path.
        string pathToTopLevelNodeModules = moduleSpecifier[..parts.TopLevelNodeModulesIndex];

        if (!global::Ts.StringUtil.StringUtil.HasPrefix(info.SourceDirectory, pathToTopLevelNodeModules, caseSensitive) ||
            (globalTypingsCacheLocation.Length > 0 && global::Ts.StringUtil.StringUtil.HasPrefix(globalTypingsCacheLocation, pathToTopLevelNodeModules, caseSensitive)))
        {
            return "";
        }

        // If the module was found in @types, get the actual Node package name.
        string nodeModulesDirectoryName = moduleSpecifier[(parts.TopLevelPackageNameIndex + 1)..];
        return global::Ts.Module.Module.GetPackageNameFromTypesPackageName(nodeModulesDirectoryName);
    }

    // specifiers.go:832 — tryDirectoryWithPackageJson.
    private static PkgJsonDirAttemptResult TryDirectoryWithPackageJson(
        NodeModulePathParts parts,
        ModulePath pathObj,
        SourceFileForSpecifierGeneration importingSourceFile,
        ModuleSpecifierGenerationHost host,
        ResolutionMode overrideMode,
        CompilerOptions options,
        ModuleSpecifierEnding[] allowedEndings)
    {
        int rootIdx = parts.PackageRootIndex;
        if (rootIdx == -1)
        {
            rootIdx = pathObj.FileName.Length;
        }
        string packageRootPath = pathObj.FileName[..rootIdx];
        string packageJsonPath = TsPathApi.CombinePaths(packageRootPath, "package.json");
        string moduleFileToTry = pathObj.FileName;
        bool maybeBlockedByTypesVersions = false;
        var packageJson = host.GetPackageJsonInfo(packageJsonPath);
        if (packageJson == null)
        {
            // No package.json exists; an index.js will still resolve as the package name.
            string fileName = moduleFileToTry[(parts.PackageRootIndex + 1)..];
            if (fileName == "index.d.ts" || fileName == "index.js" || fileName == "index.ts" || fileName == "index.tsx")
            {
                return new PkgJsonDirAttemptResult { ModuleFileToTry = moduleFileToTry, PackageRootPath = packageRootPath };
            }
            return new PkgJsonDirAttemptResult { ModuleFileToTry = moduleFileToTry };
        }

        ResolutionMode importMode = overrideMode;
        if (importMode == global::Ts.Core.Core.ResolutionModeNone)
        {
            importMode = host.GetDefaultResolutionModeForFile(importingSourceFile);
        }

        var packageJsonContent = packageJson.GetContents();
        if (options.GetResolvePackageJsonExports())
        {
            // Use the actual directory name (not packageJsonContent.name).
            string nodeModulesDirectoryName = packageRootPath[(parts.TopLevelPackageNameIndex + 1)..];
            string packageName = global::Ts.Module.Module.GetPackageNameFromTypesPackageName(nodeModulesDirectoryName);

            // Determine resolution mode from the target file's extension (getImpliedNodeFormatForEmitWorker).
            if (TsPathApi.FileExtensionIsOneOf(pathObj.FileName, [TsPathApi.ExtensionCjs, TsPathApi.ExtensionCts, TsPathApi.ExtensionDcts]))
            {
                importMode = global::Ts.Core.Core.ResolutionModeCommonJS;
            }
            else if (TsPathApi.FileExtensionIsOneOf(pathObj.FileName, [TsPathApi.ExtensionMjs, TsPathApi.ExtensionMts, TsPathApi.ExtensionDmts]))
            {
                importMode = global::Ts.Core.Core.ResolutionModeESM;
            }

            string[] conditions = global::Ts.Module.Module.GetConditions(options, importMode);

            string fromExports = "";
            if (packageJsonContent != null && packageJsonContent.Exports.Type != JSONValueType.JSONValueTypeNotPresent)
            {
                fromExports = TryGetModuleNameFromExports(options, host, pathObj.FileName, packageRootPath, packageName, packageJsonContent.Exports, conditions);
            }
            if (fromExports.Length > 0)
            {
                return new PkgJsonDirAttemptResult { ModuleFileToTry = fromExports, VerbatimFromExports = true };
            }
            if (packageJsonContent != null && packageJsonContent.Exports.Type != JSONValueType.JSONValueTypeNotPresent)
            {
                return new PkgJsonDirAttemptResult { ModuleFileToTry = pathObj.FileName, BlockedByExports = true };
            }
        }

        VersionPaths versionPaths = default;
        if (packageJsonContent != null && packageJsonContent.TypesVersions.Type == JSONValueType.JSONValueTypeObject)
        {
            versionPaths = packageJsonContent.GetVersionPaths(null);
        }
        if (versionPaths.GetPaths() != null)
        {
            string subModuleName = pathObj.FileName[(packageRootPath.Length + 1)..];
            string fromPaths = TryGetModuleNameFromPaths(subModuleName, versionPaths.GetPaths()!, allowedEndings, packageRootPath, host, options);
            if (fromPaths.Length == 0)
            {
                maybeBlockedByTypesVersions = true;
            }
            else
            {
                moduleFileToTry = TsPathApi.CombinePaths(packageRootPath, fromPaths);
            }
        }
        // If the file is the main module, it can be imported by the package name.
        string mainFileRelative = "index.js";
        if (packageJsonContent != null)
        {
            if (packageJsonContent.Typings.Valid)
            {
                mainFileRelative = packageJsonContent.Typings.Value!;
            }
            else if (packageJsonContent.Types.Valid)
            {
                mainFileRelative = packageJsonContent.Types.Value!;
            }
            else if (packageJsonContent.Main.Valid)
            {
                mainFileRelative = packageJsonContent.Main.Value!;
            }
        }

        if (mainFileRelative.Length > 0 &&
            !(maybeBlockedByTypesVersions && global::Ts.Module.Module.MatchPatternOrExact(global::Ts.Module.Module.TryParsePatterns(versionPaths.GetPaths()), mainFileRelative).IsValid))
        {
            Path mainExportFile = TsPathApi.ToPath(mainFileRelative, packageRootPath, host.UseCaseSensitiveFileNames());
            var compareOpt = new ComparePathsOptions
            {
                UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                CurrentDirectory = host.GetCurrentDirectory(),
            };
            if (TsPathApi.ComparePaths(TsPathApi.RemoveFileExtension(mainExportFile.Value), TsPathApi.RemoveFileExtension(moduleFileToTry), compareOpt) == 0)
            {
                return new PkgJsonDirAttemptResult { PackageRootPath = packageRootPath, ModuleFileToTry = moduleFileToTry };
            }
            else if ((packageJsonContent == null || packageJsonContent.Type.Value != "module") &&
                !TsPathApi.FileExtensionIsOneOf(moduleFileToTry, TsPathApi.ExtensionsNotSupportingExtensionlessResolution) &&
                global::Ts.StringUtil.StringUtil.HasPrefix(moduleFileToTry, mainExportFile.Value, host.UseCaseSensitiveFileNames()) &&
                TsPathApi.ComparePaths(TsPathApi.GetDirectoryPath(moduleFileToTry), TsPathApi.RemoveTrailingDirectorySeparator(mainExportFile.Value), compareOpt) == 0 &&
                TsPathApi.RemoveFileExtension(TsPathApi.GetBaseFileName(moduleFileToTry)) == "index")
            {
                // mainExportFile is a directory containing moduleFileToTry; use the index file (deprecated
                // for "type": "module" packages, so only for packages without it, and only when the index
                // extension supports omitting the extension).
                return new PkgJsonDirAttemptResult { PackageRootPath = packageRootPath, ModuleFileToTry = moduleFileToTry };
            }
        }

        return new PkgJsonDirAttemptResult { ModuleFileToTry = moduleFileToTry };
    }
}
