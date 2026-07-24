// Ported from tsgo internal/ls/string_completions.go — the path/module-specifier half
// (:580-1890): module-name completions over relative paths, node_modules, package.json
// exports/imports/typesVersions, paths patterns, typings directories and rootDirs merging.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Collections;
using Ts.PackageJson;
using TsChecker = Ts.Checker.Checker;
using TsPathApi = Ts.TsPath.TsPath;
using ResolutionMode = Ts.Core.ModuleKind;
using GoComparison = int;
using CoreLib = Ts.Core.Core;

namespace Ts.Ls;

// string_completions.go:1202-1208 — moduleCompletionKind.
internal enum ModuleCompletionKind
{
    Directory = 0,
    File,
    ExternalModuleName,
}

// string_completions.go:1210-1214 — moduleCompletionNameAndKind.
internal struct ModuleCompletionNameAndKind
{
    internal string name;
    internal ModuleCompletionKind kind;
    internal string extension;
}

// string_completions.go:1216-1225 — moduleCompletionNameAndKindSet.
internal sealed class ModuleCompletionNameAndKindSet
{
    internal readonly Dictionary<string, ModuleCompletionNameAndKind> names = new(StringComparer.Ordinal);

    internal void Add(ModuleCompletionNameAndKind entry)
    {
        if (!names.TryGetValue(entry.name, out var existing) || existing.kind < entry.kind)
        {
            names[entry.name] = entry;
        }
    }
}

// string_completions.go:1227-1233 — extensionOptions.
internal sealed class ExtensionOptions
{
    internal string[] extensionsToSearch = [];
    internal ReferenceKind referenceKind;
    internal SourceFile importingSourceFile = null!;
    internal global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference endingPreference;
    internal ResolutionMode resolutionMode;
}

// string_completions.go:1235-1241 — referenceKind.
internal enum ReferenceKind
{
    FileName = 0,
    ModuleSpecifier,
}

public sealed partial class LanguageService
{
    // string_completions.go:580-597 — getStringLiteralCompletionsFromModuleNames (real body;
    // replaces the type-side stub).
    internal StringLiteralCompletionsResult GetStringLiteralCompletionsFromModuleNamesWorkerTop(
        SourceFile file,
        Node node,
        TsChecker checker)
    {
        var nameAndKinds = GetStringLiteralCompletionsFromModuleNamesWorker(file, node, checker);
        var textStart = global::Ts.AstNav.Tokens.GetStartOfNode(node, file, false /*includeJSDoc*/) + 1;
        return new StringLiteralCompletionsResult
        {
            fromPaths = AddReplacementSpans(node.Text(), textStart, nameAndKinds),
        };
    }

    // string_completions.go:598-608 — addReplacementSpans.
    internal static List<PathCompletion> AddReplacementSpans(string text, int textStart, List<ModuleCompletionNameAndKind> names)
    {
        var textRange = GetDirectoryFragmentRange(text, textStart);
        var result = new List<PathCompletion>(names.Count);
        foreach (var nameAndKind in names)
        {
            result.Add(new PathCompletion
            {
                name = nameAndKind.name,
                kind = ModuleToScriptElementKind(nameAndKind.kind),
                extension = nameAndKind.extension,
                textRange = textRange,
            });
        }
        return result;
    }

    // string_completions.go:610-620 — moduletToScriptElementKind.
    internal static ScriptElementKind ModuleToScriptElementKind(ModuleCompletionKind kind)
    {
        switch (kind)
        {
            case ModuleCompletionKind.Directory:
                return ScriptElementKind.Directory;
            case ModuleCompletionKind.File:
                return ScriptElementKind.ScriptElement;
            case ModuleCompletionKind.ExternalModuleName:
                return ScriptElementKind.ExternalModuleName;
        }
        throw new InvalidOperationException($"Unknown moduleCompletionKind: {kind}");
    }

    // string_completions.go:622-625 — isAnyDirectorySeparator.
    internal static bool IsAnyDirectorySeparator(char r)
    {
        return r == '/' || r == '\\';
    }

    // string_completions.go:627-638 — getDirectoryFragmentRange: replace everything after the last
    // directory separator that appears.
    internal static global::Ts.Core.TextRange? GetDirectoryFragmentRange(string text, int textStart)
    {
        var index = -1;
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (IsAnyDirectorySeparator(text[i]))
            {
                index = i;
                break;
            }
        }
        var offset = 0;
        if (index != -1)
        {
            offset = index + 1;
        }
        var length = text.Length - offset;
        if (length == 0)
        {
            return null;
        }
        return new global::Ts.Core.TextRange(textStart + offset, textStart + offset + length);
    }

    // string_completions.go:640-682 — getStringLiteralCompletionsFromModuleNamesWorker.
    internal List<ModuleCompletionNameAndKind> GetStringLiteralCompletionsFromModuleNamesWorker(
        SourceFile file,
        Node node,
        TsChecker checker)
    {
        var literalValue = TsPathApi.NormalizeSlashes(node.Text());
        var mode = ResolutionMode.None;
        if (AstUtilities.IsStringLiteralLike(node))
        {
            mode = GetProgram().GetModeForUsageLocation(file, node);
        }

        var scriptPath = file.Path();
        var scriptDirectory = scriptPath.GetDirectoryPath();
        var options = GetProgram().Options();
        var extensionOptions = GetExtensionOptions(options, ReferenceKind.ModuleSpecifier, file, mode, checker);

        if (IsPathRelativeToScript(literalValue) ||
            (options.Paths.Size() == 0 && (TsPathApi.IsRootedDiskPath(literalValue) || TsPathApi.IsUrl(literalValue))))
        {
            return GetCompletionEntriesForRelativeModules(
                literalValue,
                scriptDirectory.Value,
                scriptPath,
                extensionOptions);
        }
        else
        {
            return GetCompletionEntriesForNonRelativeModules(
                literalValue,
                scriptDirectory.Value,
                mode,
                checker,
                extensionOptions);
        }
    }

    // string_completions.go:684-850 — getCompletionEntriesForNonRelativeModules.
    // Check all of the declared modules and those in node modules. Possible sources of modules:
    //
    //	Modules that are found by the type checker
    //	Modules found via patterns from "paths" compiler option
    //	Modules from node_modules (i.e. those listed in package.json)
    //	    This includes all files that are found in node_modules/moduleName/ with acceptable file extensions
    internal List<ModuleCompletionNameAndKind> GetCompletionEntriesForNonRelativeModules(
        string fragment,
        string scriptPath,
        ResolutionMode mode,
        TsChecker typeChecker,
        ExtensionOptions extensionOptions)
    {
        var program = GetProgram();
        var compilerOptions = program.Options();
        var paths = compilerOptions.Paths;

        var result = new ModuleCompletionNameAndKindSet();
        var moduleResolution = compilerOptions.GetModuleResolutionKind();

        if (paths != null && paths.Size() > 0)
        {
            var absolute = compilerOptions.GetPathsBasePath(program.GetCurrentDirectory());
            AddCompletionEntriesFromPaths(result, fragment, absolute, extensionOptions, paths);
        }

        var fragmentDirectory = GetFragmentDirectory(fragment);
        foreach (var ambientName in GetAmbientModuleCompletions(fragment, fragmentDirectory, typeChecker))
        {
            result.Add(new ModuleCompletionNameAndKind
            {
                name = ambientName,
                kind = ModuleCompletionKind.ExternalModuleName,
                extension = "",
            });
        }

        GetCompletionEntriesFromTypings(scriptPath, fragmentDirectory, extensionOptions, result);

        if (ModuleResolutionUsesNodeModules(moduleResolution))
        {
            // If looking for a global package name, don't just include everything in `node_modules` because that includes dependencies' own dependencies.
            // (But do if we didn't find anything, e.g. 'package.json' missing.)
            var foundGlobal = false;
            if (fragmentDirectory.Length == 0)
            {
                foreach (var moduleName in EnumerateNodeModulesVisibleToScript(scriptPath))
                {
                    var moduleResult = new ModuleCompletionNameAndKind
                    {
                        name = moduleName,
                        kind = ModuleCompletionKind.ExternalModuleName,
                        extension = "",
                    };
                    if (!result.names.ContainsKey(moduleResult.name))
                    {
                        foundGlobal = true;
                        result.Add(moduleResult);
                    }
                }
            }
            if (!foundGlobal)
            {
                var resolvePackageJsonExports = compilerOptions.GetResolvePackageJsonExports();
                var resolvePackageJsonImports = compilerOptions.GetResolvePackageJsonImports();
                var seenPackageScope = false;
                var conditions = global::Ts.Module.Module.GetConditions(compilerOptions, mode);

                // Returns true if the search should stop.
                bool ExportsOrImportsLookup(ExportsOrImports? lookupTable, string lookupFragment, string baseDirectory, bool isExports, bool isImports)
                {
                    if (lookupTable == null || lookupTable.Value.Type != JSONValueType.JSONValueTypeObject)
                    {
                        return lookupTable != null && lookupTable.Value.Type != JSONValueType.JSONValueTypeNotPresent;
                    }
                    var table = lookupTable.Value.AsObject();
                    AddCompletionEntriesFromPathsOrExportsOrImports(
                        result,
                        isExports,
                        isImports,
                        lookupFragment,
                        baseDirectory,
                        extensionOptions,
                        table.Keys(),
                        key =>
                        {
                            if (!table.TryGet(key, out var keyValue))
                            {
                                return null;
                            }
                            var pattern = GetPatternFromFirstMatchingCondition(keyValue, conditions);
                            if (pattern.Length == 0)
                            {
                                return null;
                            }
                            if (key.EndsWith('/') && pattern.EndsWith('/'))
                            {
                                return [pattern + "*"];
                            }
                            return [pattern];
                        },
                        (a, b) => global::Ts.Module.Module.ComparePatternKeys(a, b));
                    return true;
                }

                void ImportsLookup(string directory)
                {
                    if (resolvePackageJsonImports && !seenPackageScope)
                    {
                        var packageFile = TsPathApi.CombinePaths(directory, "package.json");
                        var packageJsonInfo = program.GetPackageJsonInfo(packageFile);
                        if (packageJsonInfo.Exists())
                        {
                            seenPackageScope = true;
                            ExportsOrImportsLookup(packageJsonInfo!.Contents!.Imports, fragment, directory, false /*isExports*/, true /*isImports*/);
                        }
                    }
                }

                (object?, bool) AncestorNodeModulesLookup(string ancestor)
                {
                    var nodeModules = TsPathApi.CombinePaths(ancestor, "node_modules");
                    if (host.DirectoryExists(nodeModules))
                    {
                        GetCompletionEntriesForDirectoryFragment(
                            fragment,
                            nodeModules,
                            extensionOptions,
                            false, /* moduleSpecifierIsRelative */
                            "",
                            result);
                    }
                    ImportsLookup(ancestor);
                    return (null, false);
                }

                Func<string, (object?, bool)> ancestorLookup = AncestorNodeModulesLookup;

                if (fragmentDirectory.Length != 0 && resolvePackageJsonExports)
                {
                    var nodeModulesDirectoryOrImportsLookup = ancestorLookup;
                    ancestorLookup = ancestor =>
                    {
                        var components = new List<string>(TsPathApi.GetPathComponents(fragment, ""));
                        components.RemoveAt(0); // shift off empty root
                        if (components.Count == 0)
                        {
                            nodeModulesDirectoryOrImportsLookup(ancestor);
                            return (null, false);
                        }
                        var packagePath = components[0];
                        components.RemoveAt(0);
                        if (packagePath.StartsWith('@'))
                        {
                            if (components.Count == 0)
                            {
                                nodeModulesDirectoryOrImportsLookup(ancestor);
                                return (null, false);
                            }
                            var subName = components[0];
                            components.RemoveAt(0);
                            packagePath = TsPathApi.CombinePaths(packagePath, subName);
                        }
                        if (resolvePackageJsonImports && packagePath.StartsWith('#'))
                        {
                            ImportsLookup(ancestor);
                            return (null, false);
                        }
                        var packageDirectory = TsPathApi.CombinePaths(ancestor, "node_modules", packagePath);
                        var packageFile = TsPathApi.CombinePaths(packageDirectory, "package.json");
                        var packageJsonInfo = program.GetPackageJsonInfo(packageFile);
                        if (packageJsonInfo.Exists())
                        {
                            var fragmentSubpath = string.Join("/", components);
                            if (components.Count > 0 && TsPathApi.HasTrailingDirectorySeparator(fragment))
                            {
                                fragmentSubpath += "/";
                            }
                            if (ExportsOrImportsLookup(
                                packageJsonInfo!.Contents!.Exports,
                                fragmentSubpath,
                                packageDirectory,
                                true, /*isExports*/
                                false /*isImports*/))
                            {
                                return (null, false);
                            }
                        }
                        nodeModulesDirectoryOrImportsLookup(ancestor);
                        return (null, false);
                    };
                }

                var globalCacheLocation = program.GetGlobalTypingsCacheLocation();
                TsPathApi.ForEachAncestorDirectoryStoppingAtGlobalCache(globalCacheLocation, scriptPath, ancestorLookup);
            }
        }

        return new List<ModuleCompletionNameAndKind>(result.names.Values); // map-iter: 序无关 (client sorts entries)
    }

    // string_completions.go:852-860 — getFragmentDirectory.
    internal static string GetFragmentDirectory(string fragment)
    {
        if (!ContainsSlash(fragment))
        {
            return "";
        }
        if (TsPathApi.HasTrailingDirectorySeparator(fragment))
        {
            return fragment;
        }
        return TsPathApi.GetDirectoryPath(fragment);
    }

    // string_completions.go:862-879 — getPatternFromFirstMatchingCondition.
    internal static string GetPatternFromFirstMatchingCondition(ExportsOrImports target, string[] conditions)
    {
        if (target.Type == JSONValueType.JSONValueTypeString)
        {
            return target.AsString();
        }
        if (target.Type == JSONValueType.JSONValueTypeObject)
        {
            var obj = target.AsObject();
            foreach (var condition in obj.Keys())
            {
                if (condition == "default" || Array.IndexOf(conditions, condition) >= 0 ||
                    (Array.IndexOf(conditions, "types") >= 0 && global::Ts.Module.Module.IsApplicableVersionedTypesKey(condition)))
                {
                    if (obj.TryGet(condition, out var pattern))
                    {
                        return GetPatternFromFirstMatchingCondition(pattern, conditions);
                    }
                }
            }
        }
        return "";
    }

    // string_completions.go:881-898 — getAmbientModuleCompletions.
    internal static List<string> GetAmbientModuleCompletions(string fragment, string fragmentDirectory, TsChecker typeChecker)
    {
        var ambientModules = typeChecker.GetAmbientModules();
        var nonRelativeModuleNames = new List<string>();
        foreach (var sym in ambientModules)
        {
            var moduleName = global::Ts.StringUtil.StringUtil.StripQuotes(sym.Name);
            if (moduleName.StartsWith(fragment, StringComparison.Ordinal) && !moduleName.Contains('*'))
            {
                nonRelativeModuleNames.Add(moduleName);
            }
        }

        if (fragmentDirectory.Length != 0)
        {
            var moduleNameWithSeparator = TsPathApi.EnsureTrailingDirectorySeparator(fragmentDirectory);
            for (var i = 0; i < nonRelativeModuleNames.Count; i++)
            {
                if (nonRelativeModuleNames[i].StartsWith(moduleNameWithSeparator, StringComparison.Ordinal))
                {
                    nonRelativeModuleNames[i] = nonRelativeModuleNames[i][moduleNameWithSeparator.Length..];
                }
            }
        }
        return nonRelativeModuleNames;
    }

    // string_completions.go:900-923 — getCompletionEntriesFromTypings.
    internal void GetCompletionEntriesFromTypings(
        string scriptPath,
        string fragmentDirectory,
        ExtensionOptions extensionOptions,
        ModuleCompletionNameAndKindSet result)
    {
        var program = GetProgram();
        var options = program.Options();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var (typeRoots, _) = options.GetEffectiveTypeRoots(program.GetCurrentDirectory());

        foreach (var root in typeRoots ?? [])
        {
            GetCompletionEntriesFromTypingsDirectories(root, options, fragmentDirectory, extensionOptions, seen, result);
        }

        var globalCacheLocation = program.GetGlobalTypingsCacheLocation();
        TsPathApi.ForEachAncestorDirectoryStoppingAtGlobalCache(globalCacheLocation, scriptPath, directory =>
        {
            var typesDir = TsPathApi.CombinePaths(directory, "node_modules/@types");
            GetCompletionEntriesFromTypingsDirectories(typesDir, options, fragmentDirectory, extensionOptions, seen, result);
            return ((object?)null, false);
        });
    }

    // string_completions.go:924-967 — getCompletionEntriesFromTypingsDirectories.
    internal void GetCompletionEntriesFromTypingsDirectories(
        string directory,
        global::Ts.Core.CompilerOptions options,
        string fragmentDirectory,
        ExtensionOptions extensionOptions,
        HashSet<string> seen,
        ModuleCompletionNameAndKindSet result)
    {
        if (!host.DirectoryExists(directory))
        {
            return;
        }

        foreach (var typeDirectoryName in GetDirectories(directory))
        {
            var packageName = global::Ts.Module.Module.UnmangleScopedPackageName(typeDirectoryName);
            if ((options.Types ?? []).Length > 0 && Array.IndexOf(options.Types!, packageName) < 0)
            {
                continue;
            }

            if (fragmentDirectory.Length == 0)
            {
                if (!seen.Contains(packageName))
                {
                    result.Add(new ModuleCompletionNameAndKind
                    {
                        name = packageName,
                        kind = ModuleCompletionKind.ExternalModuleName,
                        extension = "",
                    });
                    seen.Add(packageName);
                }
            }
            else
            {
                var baseDirectory = TsPathApi.CombinePaths(directory, typeDirectoryName);
                var remainingFragment = TryRemoveDirectoryPrefix(fragmentDirectory, packageName, GetProgram().UseCaseSensitiveFileNames());
                if (remainingFragment != null)
                {
                    GetCompletionEntriesForDirectoryFragment(
                        remainingFragment,
                        baseDirectory,
                        extensionOptions,
                        false,
                        "",
                        result);
                }
            }
        }
    }

    // string_completions.go:969-980 — tryRemoveDirectoryPrefix.
    internal static string? TryRemoveDirectoryPrefix(string path, string prefix, bool useCaseSensitiveFileNames)
    {
        var canonicalPath = TsPathApi.GetCanonicalFileName(path, useCaseSensitiveFileNames);
        var canonicalPrefix = TsPathApi.GetCanonicalFileName(prefix, useCaseSensitiveFileNames);
        if (canonicalPath.StartsWith(canonicalPrefix, StringComparison.Ordinal))
        {
            var withoutPrefix = path[prefix.Length..];
            if (withoutPrefix.StartsWith('/') || withoutPrefix.StartsWith('\\'))
            {
                withoutPrefix = withoutPrefix[1..];
            }
            return withoutPrefix;
        }
        return null;
    }

    // string_completions.go:982-1001 — enumerateNodeModulesVisibleToScript.
    internal List<string> EnumerateNodeModulesVisibleToScript(string scriptPath)
    {
        var result = new List<string>();
        var program = GetProgram();
        var globalCacheLocation = program.GetGlobalTypingsCacheLocation();

        TsPathApi.ForEachAncestorDirectoryStoppingAtGlobalCache(globalCacheLocation, scriptPath, directory =>
        {
            var packageJsonPath = TsPathApi.CombinePaths(directory, "package.json");
            var packageJsonInfo = program.GetPackageJsonInfo(packageJsonPath);
            if (packageJsonInfo.Exists() && packageJsonInfo!.Contents != null)
            {
                packageJsonInfo.Contents.Fields.DependencyFields.RangeDependencies((name, version, dependencyField) =>
                {
                    if (!name.StartsWith("@types/", StringComparison.Ordinal))
                    {
                        result.Add(name);
                    }
                    return true;
                });
            }
            return ((object?)null, false);
        });

        return result;
    }

    // string_completions.go:1003-1019 — getExtensionOptions.
    internal ExtensionOptions GetExtensionOptions(
        global::Ts.Core.CompilerOptions options,
        ReferenceKind referenceKind,
        SourceFile file,
        ResolutionMode mode,
        TsChecker? checker)
    {
        var extensionsToSearch = GetSupportedExtensionsForModuleResolution(options, checker);

        return new ExtensionOptions
        {
            extensionsToSearch = extensionsToSearch,
            referenceKind = referenceKind,
            importingSourceFile = file,
            endingPreference = UserPreferences().ImportModuleSpecifierEnding,
            resolutionMode = mode,
        };
    }

    // string_completions.go:1021-1043 — getSupportedExtensionsForModuleResolution.
    internal static string[] GetSupportedExtensionsForModuleResolution(global::Ts.Core.CompilerOptions options, TsChecker? checker)
    {
        // file extensions from ambient modules declarations e.g. *.css
        var extensions = new List<string>();
        if (checker != null)
        {
            var ambientModules = checker.GetAmbientModules();
            foreach (var module in ambientModules)
            {
                var name = global::Ts.StringUtil.StringUtil.StripQuotes(module.Name);
                if (!name.StartsWith("*.", StringComparison.Ordinal) || name.Contains('/'))
                {
                    continue;
                }
                extensions.Add(name[1..]);
            }
        }
        var supportedExtensions = global::Ts.TsOptions.TsConfigParsing.GetSupportedExtensions(options, null /*extraFileExtensions*/);
        foreach (var ext in supportedExtensions)
        {
            extensions.AddRange(ext);
        }
        var moduleResolution = options.GetModuleResolutionKind();
        if (ModuleResolutionUsesNodeModules(moduleResolution))
        {
            return CoreUtil.Flatten(global::Ts.TsOptions.TsConfigParsing.GetSupportedExtensionsWithJsonIfResolveJsonModule(options, [extensions.ToArray()])) ?? [];
        }
        return extensions.ToArray();
    }

    // string_completions.go:1045-1049 — moduleResolutionUsesNodeModules.
    internal static bool ModuleResolutionUsesNodeModules(global::Ts.Core.ModuleResolutionKind moduleResolution)
    {
        return moduleResolution >= global::Ts.Core.ModuleResolutionKind.Node16 && moduleResolution <= global::Ts.Core.ModuleResolutionKind.NodeNext ||
            moduleResolution == global::Ts.Core.ModuleResolutionKind.Bundler;
    }

    // string_completions.go:1051-1053 — isPathRelativeToScript: returns true if the path is
    // explicitly relative (i.e. relative to . or ..).
    internal static bool IsPathRelativeToScript(string path)
    {
        return path.StartsWith("./", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal);
    }

    // string_completions.go:1055-1084 — getCompletionEntriesForRelativeModules.
    internal List<ModuleCompletionNameAndKind> GetCompletionEntriesForRelativeModules(
        string literalValue,
        string scriptDirectory,
        global::Ts.TsPath.Path scriptPath,
        ExtensionOptions extensionOptions)
    {
        var options = GetProgram().Options();
        if ((options.RootDirs ?? []).Length > 0)
        {
            return GetCompletionEntriesForDirectoryFragmentWithRootDirs(
                options.RootDirs!,
                literalValue,
                scriptDirectory,
                scriptPath.Value,
                extensionOptions);
        }
        else
        {
            var result = GetCompletionEntriesForDirectoryFragment(
                literalValue,
                scriptDirectory,
                extensionOptions,
                true, /*moduleSpecifierIsRelative*/
                scriptPath.Value,
                new ModuleCompletionNameAndKindSet());
            return new List<ModuleCompletionNameAndKind>(result.names.Values); // map-iter: 序无关 (client sorts entries)
        }
    }

    // string_completions.go:1086-1122 — getCompletionEntriesForDirectoryFragmentWithRootDirs.
    internal List<ModuleCompletionNameAndKind> GetCompletionEntriesForDirectoryFragmentWithRootDirs(
        string[] rootDirs,
        string fragment,
        string scriptDirectory,
        string exclude,
        ExtensionOptions extensionOptions)
    {
        var program = GetProgram();
        var options = program.Options();
        string basePath;
        if (!string.IsNullOrEmpty(options.Project))
        {
            basePath = options.Project!;
        }
        else
        {
            basePath = program.GetCurrentDirectory();
        }
        var ignoreCase = !program.UseCaseSensitiveFileNames();
        var baseDirectories = GetBaseDirectoriesFromRootDirs(rootDirs, basePath, scriptDirectory, ignoreCase);

        var allCompletions = new List<ModuleCompletionNameAndKind>();
        foreach (var baseDirectory in baseDirectories)
        {
            var result = GetCompletionEntriesForDirectoryFragment(
                fragment,
                baseDirectory,
                extensionOptions,
                true, /*moduleSpecifierIsRelative*/
                exclude,
                new ModuleCompletionNameAndKindSet());
            foreach (var entry in result.names.Values) // map-iter: 序无关 (deduplicated and client-sorted below)
            {
                allCompletions.Add(entry);
            }
        }

        // Deduplicate based on name, kind, and extension
        return DeduplicateModuleCompletions(allCompletions);
    }

    // string_completions.go:1124-1164 — getBaseDirectoriesFromRootDirs: takes a script path and
    // returns paths for all potential folders that could be merged with its containing folder via
    // the "rootDirs" compiler option.
    internal static List<string> GetBaseDirectoriesFromRootDirs(string[] rootDirs, string basePath, string scriptDirectory, bool ignoreCase)
    {
        // Make all paths absolute/normalized if they are not already
        var normalizedRootDirs = new string[rootDirs.Length];
        for (var i = 0; i < rootDirs.Length; i++)
        {
            var rootDirectory = rootDirs[i];
            string normalizedPath;
            if (TsPathApi.IsRootedDiskPath(rootDirectory))
            {
                normalizedPath = rootDirectory;
            }
            else
            {
                normalizedPath = TsPathApi.CombinePaths(basePath, rootDirectory);
            }
            normalizedRootDirs[i] = TsPathApi.EnsureTrailingDirectorySeparator(TsPathApi.NormalizePath(normalizedPath));
        }

        // Determine the path to the directory containing the script relative to the root directory it is contained within
        var relativeDirectory = "";
        var comparePathsOptions = new global::Ts.TsPath.ComparePathsOptions
        {
            UseCaseSensitiveFileNames = !ignoreCase,
            CurrentDirectory = basePath,
        };
        foreach (var rootDirectory in normalizedRootDirs)
        {
            if (TsPathApi.ContainsPath(rootDirectory, scriptDirectory, comparePathsOptions))
            {
                if (rootDirectory.Length > scriptDirectory.Length)
                {
                    relativeDirectory = "";
                }
                else
                {
                    relativeDirectory = scriptDirectory[rootDirectory.Length..];
                }
                break;
            }
        }

        // Now find a path for each potential directory that is to be merged with the one containing the script
        var directories = new List<string>();
        foreach (var rootDirectory in normalizedRootDirs)
        {
            directories.Add(TsPathApi.RemoveTrailingDirectorySeparator(TsPathApi.CombinePaths(rootDirectory, relativeDirectory)));
        }
        directories.Add(TsPathApi.RemoveTrailingDirectorySeparator(scriptDirectory));

        return DeduplicateStrings(directories);
    }

    // string_completions.go:1166-1179 — deduplicateStrings.
    internal static List<string> DeduplicateStrings(List<string> slice)
    {
        if (slice.Count <= 1)
        {
            return slice;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var s in slice)
        {
            if (seen.Add(s))
            {
                result.Add(s);
            }
        }
        return result;
    }

    // string_completions.go:1181-1200 — deduplicateModuleCompletions.
    internal static List<ModuleCompletionNameAndKind> DeduplicateModuleCompletions(List<ModuleCompletionNameAndKind> completions)
    {
        if (completions.Count <= 1)
        {
            return completions;
        }
        var seen = new HashSet<(string, ModuleCompletionKind, string)>();
        var result = new List<ModuleCompletionNameAndKind>();
        foreach (var c in completions)
        {
            if (seen.Add((c.name, c.kind, c.extension)))
            {
                result.Add(c);
            }
        }
        return result;
    }

    // string_completions.go:1243-1342 — getCompletionEntriesForDirectoryFragment: given a path
    // ending at a directory, gets the completions for the path.
    internal ModuleCompletionNameAndKindSet GetCompletionEntriesForDirectoryFragment(
        string fragment,
        string scriptDirectory,
        ExtensionOptions extensionOptions,
        bool moduleSpecifierIsRelative,
        string exclude,
        ModuleCompletionNameAndKindSet result)
    {
        var program = GetProgram();
        fragment = TsPathApi.NormalizeSlashes(fragment);

        // Remove the basename from the path.
        // We don't use the basename to filter completions: the client is responsible for that filtering.
        if (!TsPathApi.HasTrailingDirectorySeparator(fragment))
        {
            fragment = TsPathApi.GetDirectoryPath(fragment);
        }

        if (fragment.Length == 0)
        {
            fragment = ".";
        }

        fragment = TsPathApi.EnsureTrailingDirectorySeparator(fragment);

        var baseDirectory = TsPathApi.ResolvePath(scriptDirectory, fragment);
        if (!moduleSpecifierIsRelative)
        {
            // Check for a version redirect.
            var packageJsonDirectory = program.GetNearestAncestorDirectoryWithPackageJson(baseDirectory);
            if (packageJsonDirectory.Length != 0)
            {
                var packageJsonPath = TsPathApi.CombinePaths(packageJsonDirectory, "package.json");
                var packageJsonInfo = program.GetPackageJsonInfo(packageJsonPath);
                if (packageJsonInfo != null && packageJsonInfo.Contents != null &&
                    packageJsonInfo.Contents.TypesVersions.Type == JSONValueType.JSONValueTypeObject)
                {
                    var versionPaths = packageJsonInfo.Contents.GetVersionPaths(null);
                    var paths = versionPaths.GetPaths();
                    if (paths.Size() > 0)
                    {
                        var pathInPackage = baseDirectory[TsPathApi.EnsureTrailingDirectorySeparator(packageJsonDirectory).Length..];
                        if (AddCompletionEntriesFromPaths(result, pathInPackage, packageJsonDirectory, extensionOptions, paths!))
                        {
                            // One of the `versionPaths` was matched, which will block relative resolution
                            // to files and folders from here.
                            // All reachable paths given the pattern match are already added.
                            return result;
                        }
                    }
                }
            }
        }

        if (!host.DirectoryExists(baseDirectory))
        {
            return result;
        }

        // Enumerate all available files.
        var files = ReadDirectory(
            baseDirectory,
            extensionOptions.extensionsToSearch,
            ["./*"] /*include*/);

        foreach (var filePath in files)
        {
            if (TsPathApi.ComparePaths(exclude, filePath, new global::Ts.TsPath.ComparePathsOptions
            {
                UseCaseSensitiveFileNames = program.UseCaseSensitiveFileNames(),
                CurrentDirectory = program.GetCurrentDirectory(),
            }) == 0)
            {
                continue; // Avoid self-imports
            }

            var (name, extension) = GetFilenameWithExtensionOption(
                TsPathApi.GetBaseFileName(filePath),
                extensionOptions,
                false /*isExportsOrImportsWildcard*/);
            result.Add(new ModuleCompletionNameAndKind
            {
                name = name,
                kind = ModuleCompletionKind.File,
                extension = extension,
            });
        }

        // Get folder completion as well.
        var directories = GetDirectories(baseDirectory);

        foreach (var directory in directories)
        {
            var directoryName = TsPathApi.GetBaseFileName(directory);
            if (directoryName != "@types")
            {
                result.Add(new ModuleCompletionNameAndKind
                {
                    name = directoryName,
                    kind = ModuleCompletionKind.Directory,
                    extension = "",
                });
            }
        }

        return result;
    }

    // string_completions.go:1344-1380 — addCompletionEntriesFromPaths. Returns true if `fragment`
    // was a match for any `paths` (which should indicate whether any other path completions should
    // be offered).
    internal bool AddCompletionEntriesFromPaths(
        ModuleCompletionNameAndKindSet result,
        string fragment,
        string baseDirectory,
        ExtensionOptions extensionOptions,
        global::Ts.Collections.OrderedMap<string, string[]> paths)
    {
        string[]? GetPatternsForKeys(string key) => paths.GetOrZero(key);
        GoComparison ComparePathPatterns(string a, string b)
        {
            var patternA = CoreLib.TryParsePattern(a);
            var patternB = CoreLib.TryParsePattern(b);
            var lengthA = a.Length;
            if (patternA.StarIndex != -1)
            {
                lengthA = patternA.StarIndex;
            }
            var lengthB = b.Length;
            if (patternB.StarIndex != -1)
            {
                lengthB = patternB.StarIndex;
            }
            return lengthB.CompareTo(lengthA);
        }
        return AddCompletionEntriesFromPathsOrExportsOrImports(
            result,
            false, /*isExports*/
            false, /*isImports*/
            fragment,
            baseDirectory,
            extensionOptions,
            paths.Keys(),
            GetPatternsForKeys,
            ComparePathPatterns);
    }

    // string_completions.go:1382-1469 — addCompletionEntriesFromPathsOrExportsOrImports. Returns
    // true if `fragment` was a match for any `paths` (which should indicate whether any other path
    // completions should be offered).
    internal bool AddCompletionEntriesFromPathsOrExportsOrImports(
        ModuleCompletionNameAndKindSet result,
        bool isExports,
        bool isImports,
        string fragment,
        string baseDirectory,
        ExtensionOptions extensionOptions,
        IEnumerable<string> keys,
        Func<string, string[]?> getPatternsForKey,
        Func<string, string, GoComparison> comparePaths)
    {
        var pathResults = new List<(List<ModuleCompletionNameAndKind> Results, bool Matched)>();
        string? matchedPath = null;
        foreach (var key in keys)
        {
            if (key == ".")
            {
                continue;
            }
            var normalizedKey = key.StartsWith("./", StringComparison.Ordinal) ? key[2..] : key; // Remove leading "./"
            if ((isExports || isImports) && key.EndsWith('/')) // Normalize trailing "/" to "/*"
            {
                normalizedKey += "*";
            }
            var patterns = getPatternsForKey(key);
            if (patterns is { Length: > 0 })
            {
                var pathPattern = CoreLib.TryParsePattern(normalizedKey);
                if (!pathPattern.IsValid)
                {
                    continue;
                }
                var isMatch = pathPattern.Matches(fragment);
                var isLongestMatch = false;
                if (isMatch)
                {
                    if (matchedPath == null)
                    {
                        isLongestMatch = true;
                    }
                    else
                    {
                        isLongestMatch = comparePaths(normalizedKey, matchedPath) == global::Ts.StringUtil.StringUtil.ComparisonLessThan;
                    }
                }
                if (isLongestMatch)
                {
                    // If this is a higher priority match than anything we've seen so far, previous results from matches are invalid, e.g.
                    // for `import {} from "some-package/|"` with a typesVersions:
                    // {
                    //   "bar/*": ["bar/*"], // <-- 1. We add 'bar', but 'bar/*' doesn't match yet.
                    //   "*": ["dist/*"],    // <-- 2. We match here and add files from dist. 'bar' is still ok because it didn't come from a match.
                    //   "foo/*": ["foo/*"]  // <-- 3. We matched '*' earlier and added results from dist, but if 'foo/*' also matched,
                    // }                               results in dist would not be visible. 'bar' still stands because it didn't come from a match.
                    //                                 This is especially important if `dist/foo` is a folder, because if we fail to clear results
                    //                                 added by the '*' match, after typing `"some-package/foo/|"` we would get file results from both
                    //                                 ./dist/foo and ./foo, when only the latter will actually be resolvable.
                    //                                 See pathCompletionsTypesVersionsWildcard6.ts.
                    matchedPath = normalizedKey;
                    pathResults = pathResults.FindAll(pr => !pr.Matched);
                }
                if (pathPattern.StarIndex == -1 ||
                    matchedPath == null ||
                    comparePaths(normalizedKey, matchedPath) != global::Ts.StringUtil.StringUtil.ComparisonGreaterThan)
                {
                    pathResults.Add((
                        GetCompletionsForPathMapping(
                            normalizedKey,
                            patterns,
                            fragment,
                            baseDirectory,
                            isExports,
                            isImports,
                            extensionOptions),
                        isMatch));
                }
            }
        }

        foreach (var (results, _) in pathResults)
        {
            foreach (var res in results)
            {
                result.Add(res);
            }
        }

        return matchedPath != null;
    }

    // string_completions.go:1471-1567 — getCompletionsForPathMapping.
    internal List<ModuleCompletionNameAndKind> GetCompletionsForPathMapping(
        string path,
        string[] patterns,
        string fragment,
        string packageDirectory,
        bool isExports,
        bool isImports,
        ExtensionOptions extensionOptions)
    {
        var fragmentDirectory = GetFragmentDirectory(fragment);
        if (fragmentDirectory.Length != 0)
        {
            fragmentDirectory = TsPathApi.EnsureTrailingDirectorySeparator(fragmentDirectory);
        }
        List<ModuleCompletionNameAndKind> JustPathMappingName(string name, ModuleCompletionKind kind, string extension)
        {
            if (name.StartsWith(fragment, StringComparison.Ordinal))
            {
                name = TsPathApi.RemoveTrailingDirectorySeparator(name);
                if (fragmentDirectory.Length != 0 && name.StartsWith(fragmentDirectory, StringComparison.Ordinal))
                {
                    name = name[fragmentDirectory.Length..];
                }
                return [new ModuleCompletionNameAndKind
                {
                    name = name,
                    kind = kind,
                    extension = extension,
                }];
            }
            return [];
        }

        var parsedPath = CoreLib.TryParsePattern(path);
        if (!parsedPath.IsValid)
        {
            return [];
        }
        // No stars in the pattern.
        if (parsedPath.StarIndex == -1)
        {
            // For a path mapping "foo": ["/x/y/z.ts"], add "foo" itself as a completion.
            var pattern = CoreUtil.FirstOrNil(patterns) ?? "";
            var extension = GetFileExtension(pattern);
            return JustPathMappingName(path, ModuleCompletionKind.File, extension);
        }

        var pathPrefix = parsedPath.Text[..parsedPath.StarIndex];
        var pathSuffix = parsedPath.Text[(parsedPath.StarIndex + 1)..];
        if (!fragment.StartsWith(pathPrefix, StringComparison.Ordinal))
        {
            // Fragment doesn't match the path mapping prefix at all:
            // we cannot extend it via this path.
            if (!pathPrefix.StartsWith(fragment, StringComparison.Ordinal))
            {
                return [];
            }
            var starIsFullPathComponent = path.EndsWith("/*", StringComparison.Ordinal);
            if (starIsFullPathComponent)
            {
                return JustPathMappingName(pathPrefix, ModuleCompletionKind.Directory, "" /*extension*/);
            }
            // If path is e.g. `foo/bar/*`, and fragment is `foo/b`, then remaining directory prefix is `bar/`,
            var remainingDirectoryPrefix = pathPrefix[fragmentDirectory.Length..];
            var completions = new List<ModuleCompletionNameAndKind>();
            foreach (var pattern in patterns)
            {
                var modules = GetModulesForPathsPattern(
                    "", /*fragment*/
                    packageDirectory,
                    pattern,
                    isExports,
                    isImports,
                    extensionOptions);
                for (var i = 0; i < modules.Count; i++)
                {
                    var m = modules[i];
                    m.name = remainingDirectoryPrefix + m.name + (m.kind == ModuleCompletionKind.File ? pathSuffix : "");
                    modules[i] = m;
                }
                completions.AddRange(modules);
            }
            return completions;
        }
        var remainingFragment = fragment[pathPrefix.Length..];
        var remainingDirectoryFragment = "";
        if (!fragmentDirectory.StartsWith(pathPrefix, StringComparison.Ordinal))
        {
            remainingDirectoryFragment = pathPrefix[fragmentDirectory.Length..];
        }
        var flat = new List<ModuleCompletionNameAndKind>();
        foreach (var pattern in patterns)
        {
            var modules = GetModulesForPathsPattern(
                remainingFragment,
                packageDirectory,
                pattern,
                isExports,
                isImports,
                extensionOptions);
            for (var i = 0; i < modules.Count; i++)
            {
                var m = modules[i];
                m.name = remainingDirectoryFragment + m.name + (m.kind == ModuleCompletionKind.File ? pathSuffix : "");
                modules[i] = m;
            }
            flat.AddRange(modules);
        }
        return flat;
    }

    // string_completions.go:1569-1576 — getFileExtension.
    internal static string GetFileExtension(string fileName)
    {
        var extension = TsPathApi.TryGetExtensionFromPath(fileName);
        if (extension.Length == 0)
        {
            extension = TsPathApi.GetAnyExtensionFromPath(fileName, null /*extensions*/, false /*ignoreCase*/);
        }
        return extension;
    }

    // string_completions.go:1578-1791 — getModulesForPathsPattern.
    // The input fragment is relative to the path pattern's prefix:
    // e.g. if path = "bar/_*/baz", and fragment = "bar/_dir", then fragment is "dir".
    // The names are relative to the path pattern's prefix and fragment directory :
    // e.g. if path = "bar/_*/baz", and fragment = "bar/_dir/a", and we find result "abd",
    // the result should be interpreted as "bar/_dir/abd".
    internal List<ModuleCompletionNameAndKind> GetModulesForPathsPattern(
        string fragment,
        string packageDirectory,
        string pattern,
        bool isExports,
        bool isImports,
        ExtensionOptions extensionOptions)
    {
        var program = GetProgram();
        var parsed = CoreLib.TryParsePattern(pattern);
        if (!parsed.IsValid || parsed.StarIndex == -1)
        {
            return [];
        }

        var prefix = parsed.Text[..parsed.StarIndex];
        var suffix = parsed.Text[(parsed.StarIndex + 1)..];

        // The prefix has two effective parts: the directory path and the base component after the filepath that is not a
        // full directory component. For example: directory/path/of/prefix/base*
        var normalizedPrefix = TsPathApi.ResolvePath(prefix);
        string normalizedPrefixDirectory;
        string normalizedPrefixBase;
        if (TsPathApi.HasTrailingDirectorySeparator(prefix))
        {
            normalizedPrefixDirectory = normalizedPrefix;
            normalizedPrefixBase = "";
        }
        else
        {
            normalizedPrefixDirectory = TsPathApi.GetDirectoryPath(normalizedPrefix);
            normalizedPrefixBase = TsPathApi.GetBaseFileName(normalizedPrefix);
        }

        var fragmentHasPath = ContainsSlash(fragment);
        var fragmentDirectory = "";
        if (fragmentHasPath)
        {
            if (TsPathApi.HasTrailingDirectorySeparator(fragment))
            {
                fragmentDirectory = fragment;
            }
            else
            {
                fragmentDirectory = TsPathApi.GetDirectoryPath(fragment);
            }
        }

        var options = program.Options();
        var ignoreCase = !program.UseCaseSensitiveFileNames();
        var outDir = options.OutDir;
        var declarationDir = options.DeclarationDir;

        // Try and expand the prefix to include any path from the fragment so that we can limit the readDirectory call
        string expandedPrefixDirectory;
        if (fragmentHasPath)
        {
            expandedPrefixDirectory = TsPathApi.CombinePaths(normalizedPrefixDirectory, normalizedPrefixBase + fragmentDirectory);
        }
        else
        {
            expandedPrefixDirectory = normalizedPrefixDirectory;
        }
        // Need to normalize after combining: If we combinePaths("a", "../b"), we want "b" and not "a/../b".
        var baseDirectory = TsPathApi.NormalizePath(TsPathApi.CombinePaths(packageDirectory, expandedPrefixDirectory));

        var possibleInputBaseDirectoryForOutDir = "";
        var possibleInputBaseDirectoryForDeclarationDir = "";
        if (isImports)
        {
            if (!string.IsNullOrEmpty(outDir))
            {
                possibleInputBaseDirectoryForOutDir = GetPossibleOriginalInputPathWithoutChangingExt(
                    baseDirectory,
                    ignoreCase,
                    outDir!,
                    program.CommonSourceDirectory);
            }
            if (!string.IsNullOrEmpty(declarationDir))
            {
                possibleInputBaseDirectoryForDeclarationDir = GetPossibleOriginalInputPathWithoutChangingExt(
                    baseDirectory,
                    ignoreCase,
                    declarationDir!,
                    program.CommonSourceDirectory);
            }
        }

        var normalizedSuffix = TsPathApi.NormalizePath(suffix);

        var declarationExtension = "";
        string[] inputExtensions = [];
        if (normalizedSuffix.Length != 0)
        {
            declarationExtension = TsPathApi.GetDeclarationEmitExtensionForPath("_" + normalizedSuffix);
            inputExtensions = TsPathApi.GetPossibleOriginalInputExtensionForExtension("_" + normalizedSuffix);
        }

        var matchingSuffixes = new List<string>();
        if (declarationExtension.Length != 0)
        {
            matchingSuffixes.Add(TsPathApi.ChangeExtension(normalizedSuffix, declarationExtension));
        }
        foreach (var ext in inputExtensions)
        {
            matchingSuffixes.Add(TsPathApi.ChangeExtension(normalizedSuffix, ext));
        }
        matchingSuffixes.Add(normalizedSuffix);

        // If we have a suffix, then we read the directory all the way down to avoid returning completions for
        // directories that don't contain files that would match the suffix. A previous comment here was concerned
        // about the case where `normalizedSuffix` includes a `?` character, which should be interpreted literally,
        // but will match any single character as part of the `include` pattern in `tryReadDirectory`. This is not
        // a problem, because (in the extremely unusual circumstance where the suffix has a `?` in it) a `?`
        // interpreted as "any character" can only return *too many* results as compared to the literal
        // interpretation, so we can filter those superfluous results out via `trimPrefixAndSuffix` as we've always
        // done.
        var includeGlobs = new List<string>();
        if (normalizedSuffix.Length != 0)
        {
            foreach (var matchSuffix in matchingSuffixes)
            {
                includeGlobs.Add("**/*" + matchSuffix);
            }
        }
        else
        {
            includeGlobs.Add("./*");
        }

        var isExportsOrImportsWildcard = (isExports || isImports) && pattern.EndsWith("/*", StringComparison.Ordinal);

        string TrimPrefixAndSuffix(string path, string prefixStr)
        {
            foreach (var matchSuffix in matchingSuffixes)
            {
                var inner = WithoutStartAndEnd(TsPathApi.NormalizePath(path), prefixStr, matchSuffix);
                if (inner == null)
                {
                    continue;
                }
                return RemoveLeadingDirectorySeparator(inner);
            }
            return "";
        }

        List<ModuleCompletionNameAndKind> GetMatchesWithPrefix(string directory)
        {
            string completePrefix;
            if (fragmentHasPath)
            {
                completePrefix = directory;
            }
            else
            {
                completePrefix = TsPathApi.EnsureTrailingDirectorySeparator(directory) + normalizedPrefixBase;
            }

            var matches = ReadDirectory(
                directory,
                extensionOptions.extensionsToSearch,
                includeGlobs.ToArray());

            var matchResult = new List<ModuleCompletionNameAndKind>();
            foreach (var match in matches)
            {
                var trimmedWithPattern = TrimPrefixAndSuffix(match, completePrefix);
                if (trimmedWithPattern.Length != 0)
                {
                    if (ContainsSlash(trimmedWithPattern))
                    {
                        var pathComponents = TsPathApi.GetPathComponents(RemoveLeadingDirectorySeparator(trimmedWithPattern), "");
                        if (pathComponents.Count > 1)
                        {
                            matchResult.Add(new ModuleCompletionNameAndKind
                            {
                                name = pathComponents[1],
                                kind = ModuleCompletionKind.Directory,
                                extension = "",
                            });
                        }
                    }
                    else
                    {
                        var (name, extension) = GetFilenameWithExtensionOption(
                            trimmedWithPattern,
                            extensionOptions,
                            isExportsOrImportsWildcard);
                        if (extension.Length == 0)
                        {
                            extension = GetFileExtension(match);
                        }
                        matchResult.Add(new ModuleCompletionNameAndKind
                        {
                            name = name,
                            kind = ModuleCompletionKind.File,
                            extension = extension,
                        });
                    }
                }
            }
            return matchResult;
        }

        List<ModuleCompletionNameAndKind> GetDirectoryMatches(string directoryName)
        {
            var matchDirectories = GetDirectories(directoryName);
            var matchResult = new List<ModuleCompletionNameAndKind>();
            foreach (var dir in matchDirectories)
            {
                if (dir != "node_modules")
                {
                    matchResult.Add(new ModuleCompletionNameAndKind
                    {
                        name = dir,
                        kind = ModuleCompletionKind.Directory,
                        extension = "",
                    });
                }
            }
            return matchResult;
        }

        var resultMatches = new List<ModuleCompletionNameAndKind>();
        resultMatches.AddRange(GetMatchesWithPrefix(baseDirectory));

        if (possibleInputBaseDirectoryForOutDir.Length != 0)
        {
            resultMatches.AddRange(GetMatchesWithPrefix(possibleInputBaseDirectoryForOutDir));
        }
        if (possibleInputBaseDirectoryForDeclarationDir.Length != 0)
        {
            resultMatches.AddRange(GetMatchesWithPrefix(possibleInputBaseDirectoryForDeclarationDir));
        }

        // If we had a suffix, we already recursively searched for all possible files that could match
        // it and returned the directories leading to those files. Otherwise, assume any directory could
        // have something valid to import.
        if (normalizedSuffix.Length == 0)
        {
            resultMatches.AddRange(GetDirectoryMatches(baseDirectory));
            if (possibleInputBaseDirectoryForOutDir.Length != 0)
            {
                resultMatches.AddRange(GetDirectoryMatches(possibleInputBaseDirectoryForOutDir));
            }
            if (possibleInputBaseDirectoryForDeclarationDir.Length != 0)
            {
                resultMatches.AddRange(GetDirectoryMatches(possibleInputBaseDirectoryForDeclarationDir));
            }
        }

        return resultMatches;
    }

    // string_completions.go:1793-1795 — containsSlash.
    internal static bool ContainsSlash(string fragment)
    {
        return fragment.Contains('/');
    }

    // string_completions.go:1797-1803 — withoutStartAndEnd.
    internal static string? WithoutStartAndEnd(string s, string start, string end)
    {
        if (s.StartsWith(start, StringComparison.Ordinal) && s.EndsWith(end, StringComparison.Ordinal) && s.Length >= start.Length + end.Length)
        {
            return s[start.Length..(s.Length - end.Length)];
        }
        return null;
    }

    // string_completions.go:1805-1807 — removeLeadingDirectorySeparator.
    internal static string RemoveLeadingDirectorySeparator(string path)
    {
        return path.StartsWith('/') ? path[1..] : path;
    }

    // string_completions.go:1809-1824 — getPossibleOriginalInputPathWithoutChangingExt.
    internal static string GetPossibleOriginalInputPathWithoutChangingExt(
        string filePath,
        bool ignoreCase,
        string outputDir,
        Func<string> getCommonSourceDirectory)
    {
        if (outputDir.Length != 0)
        {
            return TsPathApi.ResolvePath(
                getCommonSourceDirectory(),
                TsPathApi.GetRelativePathFromDirectory(outputDir, filePath, new global::Ts.TsPath.ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = !ignoreCase,
                }));
        }
        return filePath;
    }

    // string_completions.go:1826-1880 — getFilenameWithExtensionOption.
    internal (string Name, string Extension) GetFilenameWithExtensionOption(
        string name,
        ExtensionOptions extensionOptions,
        bool isExportsOrImportsWildcard)
    {
        var program = GetProgram();
        var nonJSResult = global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.TryGetRealFileNameForNonJSDeclarationFileName(name);
        if (nonJSResult.Length != 0)
        {
            return (nonJSResult, TsPathApi.TryGetExtensionFromPath(nonJSResult));
        }
        if (extensionOptions.referenceKind == ReferenceKind.FileName)
        {
            return (name, TsPathApi.TryGetExtensionFromPath(name));
        }

        var allowedEndings = global::Ts.ModuleSpecifiers.Specifiers.GetAllowedEndingsInPreferredOrder(
            new global::Ts.ModuleSpecifiers.UserPreferences { ImportModuleSpecifierEnding = extensionOptions.endingPreference },
            program,
            program.Options(),
            extensionOptions.importingSourceFile,
            "", /*oldImportSpecifier*/
            extensionOptions.resolutionMode);

        if (isExportsOrImportsWildcard)
        {
            // If we're completing `import {} from "foo/|"` and subpaths are available via `"exports": { "./*": "./src/*" }`,
            // the completion must be a (potentially extension-swapped) file name. Dropping extensions and index files is not allowed.
            allowedEndings = CoreUtil.Filter(allowedEndings, e =>
                e != global::Ts.ModuleSpecifiers.ModuleSpecifierEnding.Minimal && e != global::Ts.ModuleSpecifiers.ModuleSpecifierEnding.Index) ?? [];
        }

        if (allowedEndings.Length > 0 && allowedEndings[0] == global::Ts.ModuleSpecifiers.ModuleSpecifierEnding.TsExtension)
        {
            if (TsPathApi.FileExtensionIsOneOf(name, TsPathApi.SupportedTSImplementationExtensions))
            {
                return (name, TsPathApi.TryGetExtensionFromPath(name));
            }
            var tsOutputExtension = global::Ts.Module.Module.TryGetJSExtensionForFile(name, program.Options());
            if (tsOutputExtension.Length != 0)
            {
                return (TsPathApi.ChangeExtension(name, tsOutputExtension), tsOutputExtension);
            }
            return (name, TsPathApi.TryGetExtensionFromPath(name));
        }

        if (!isExportsOrImportsWildcard &&
            allowedEndings.Length > 0 &&
            (allowedEndings[0] == global::Ts.ModuleSpecifiers.ModuleSpecifierEnding.Minimal || allowedEndings[0] == global::Ts.ModuleSpecifiers.ModuleSpecifierEnding.Index) &&
            TsPathApi.FileExtensionIsOneOf(name, [".js", ".jsx", ".ts", ".tsx", ".d.ts"]))
        {
            return (TsPathApi.RemoveFileExtension(name), TsPathApi.TryGetExtensionFromPath(name));
        }

        var outputExtension = global::Ts.Module.Module.TryGetJSExtensionForFile(name, program.Options());
        if (outputExtension.Length != 0)
        {
            return (TsPathApi.ChangeExtension(name, outputExtension), outputExtension);
        }
        return (name, TsPathApi.TryGetExtensionFromPath(name));
    }
}
