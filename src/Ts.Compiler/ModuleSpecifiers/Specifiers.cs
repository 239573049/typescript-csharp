// Ported from tsgo internal/modulespecifiers/specifiers.go — the module-specifier generation core.
// PORT-NOTE (in progress): leaf/support functions land first; the GetModuleSpecifiers entry chain
// (computeModuleSpecifiers / getLocalModuleSpecifier / package.json exports-imports resolution) and the
// wiring of nodeBuilder.getSpecifierForModuleSymbol follow in subsequent batches.

using System;
using System.Collections.Generic;
using System.Linq;

using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Module;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class Specifiers
{
    // specifiers.go — specPair (ending + computed value candidate for a paths mapping).
    private readonly record struct SpecPair(ModuleSpecifierEnding Ending, string Value);

    // specifiers.go:1188 — validateEnding.
    private static bool ValidateEnding(SpecPair c, string relativeToBaseUrl, CompilerOptions compilerOptions, ModuleSpecifierGenerationHost host) =>
        c.Ending != ModuleSpecifierEnding.Minimal || c.Value == ProcessEnding(relativeToBaseUrl, [c.Ending], compilerOptions, host);

    // specifiers.go:1089 — tryGetModuleNameFromPaths.
    internal static string TryGetModuleNameFromPaths(
        string relativeToBaseUrl,
        OrderedMap<string, string[]> paths,
        ModuleSpecifierEnding[] allowedEndings,
        string baseDirectory,
        ModuleSpecifierGenerationHost host,
        CompilerOptions compilerOptions)
    {
        bool caseSensitive = host.UseCaseSensitiveFileNames();
        // map-iter: 已排序 — OrderedMap preserves paths insertion order (first match wins, per Go).
        foreach ((string key, string[] values) in paths.Entries())
        {
            foreach (string patternText in values)
            {
                string normalized = TsPathApi.NormalizePath(patternText);
                string pattern = ModuleSpecifierUtil.GetRelativePathIfInSameVolume(normalized, baseDirectory, caseSensitive);
                if (pattern.Length == 0)
                {
                    pattern = normalized;
                }
                int starIdx = pattern.IndexOf('*');
                string prefix = starIdx < 0 ? pattern : pattern[..starIdx];
                string suffix = starIdx < 0 ? "" : pattern[(starIdx + 1)..];
                bool ok = starIdx >= 0;

                // In module resolution, if `pattern` itself has an extension, a file with that extension
                // is looked up directly; the filename result is a candidate only when `pattern` has an
                // extension. (See moduleNameResolver.ts; long note in the Go source.)
                var candidates = new List<SpecPair>();
                foreach (ModuleSpecifierEnding ending in allowedEndings)
                {
                    string result = ProcessEnding(relativeToBaseUrl, [ending], compilerOptions, host);
                    candidates.Add(new SpecPair(ending, result));
                }
                if (TsPathApi.TryGetExtensionFromPath(pattern).Length > 0)
                {
                    candidates.Add(new SpecPair(ModuleSpecifierEnding.JsExtension, relativeToBaseUrl));
                }

                if (ok)
                {
                    foreach (SpecPair c in candidates)
                    {
                        string value = c.Value;
                        if (value.Length >= prefix.Length + suffix.Length &&
                            global::Ts.StringUtil.StringUtil.HasPrefix(value, prefix, caseSensitive) &&
                            global::Ts.StringUtil.StringUtil.HasSuffix(value, suffix, caseSensitive) &&
                            ValidateEnding(c, relativeToBaseUrl, compilerOptions, host))
                        {
                            string matchedStar = value[prefix.Length..(value.Length - suffix.Length)];
                            if (!TsPathApi.PathIsRelative(matchedStar))
                            {
                                return ModuleSpecifierUtil.ReplaceFirstStar(key, matchedStar);
                            }
                        }
                    }
                }
                else if (candidates.Any(c => c.Ending != ModuleSpecifierEnding.Minimal && pattern == c.Value) ||
                         candidates.Any(c => c.Ending == ModuleSpecifierEnding.Minimal && pattern == c.Value && ValidateEnding(c, relativeToBaseUrl, compilerOptions, host)))
                {
                    return key;
                }
            }
        }
        return "";
    }

    // specifiers.go:107 — tryGetModuleNameFromAmbientModule.
    internal static string TryGetModuleNameFromAmbientModule(Symbol moduleSymbol, CheckerShape checker)
    {
        foreach (Node decl in moduleSymbol.Declarations ?? [])
        {
            if (AstUtilities.IsModuleWithStringLiteralName(decl) &&
                (!AstUtilities.IsModuleAugmentationExternal(decl) || !TsPathApi.IsExternalModuleNameRelative(decl.Name()!.Text())))
            {
                return decl.Name()!.Text();
            }
        }

        // The module could be a namespace exported through "export=" from an ambient module:
        //   declare module "m" { namespace ns { class c {} } export = ns; }
        // `import {c} from "m";` is valid — moduleSymbol is "ns" but the module name should be "m".
        foreach (Node d in moduleSymbol.Declarations ?? [])
        {
            if (!NodeTests.IsModuleDeclaration(d))
            {
                continue;
            }

            Node? possibleContainer = AstUtilities.FindAncestor(d, AstUtilities.IsModuleWithStringLiteralName);
            if (possibleContainer == null || possibleContainer.Parent == null || !NodeTests.IsSourceFile(possibleContainer.Parent))
            {
                continue;
            }

            Symbol? sym = possibleContainer.Symbol()!.Exports?.GetValueOrDefault(InternalSymbolName.ExportEquals);
            if (sym == null)
            {
                continue;
            }
            Node? exportAssignmentDecl = sym.ValueDeclaration;
            if (exportAssignmentDecl == null || exportAssignmentDecl.Kind != SyntaxKind.ExportAssignment)
            {
                continue;
            }
            Symbol? exportSymbol = checker.GetSymbolAtLocation(exportAssignmentDecl.Expression()!);
            if (exportSymbol == null)
            {
                continue;
            }
            if ((exportSymbol.Flags & SymbolFlags.Alias) != 0)
            {
                exportSymbol = checker.GetAliasedSymbol(exportSymbol);
            }
            // TODO: Possible strada bug - isn't this insufficient in the presence of merge symbols?
            if (exportSymbol == d.Symbol())
            {
                return possibleContainer.Name()!.Text();
            }
        }
        return "";
    }

    // specifiers.go:175 — getAllModulePaths. (Module-specifier cache model deferred; delegates to worker.)
    internal static ModulePath[] GetAllModulePaths(
        Info info,
        string importedFileName,
        ModuleSpecifierGenerationHost host,
        CompilerOptions compilerOptions,
        UserPreferences preferences,
        ModuleSpecifierOptions options) =>
        GetAllModulePathsWorker(info, importedFileName, host, compilerOptions, options);

    // specifiers.go:198 — getAllModulePathsWorker.
    internal static ModulePath[] GetAllModulePathsWorker(
        Info info,
        string importedFileName,
        ModuleSpecifierGenerationHost host,
        CompilerOptions compilerOptions,
        ModuleSpecifierOptions options)
    {
        // map-iter: 序无关 — sorted per-directory below before being appended.
        var allFileNames = new Dictionary<string, ModulePath>(StringComparer.Ordinal);
        ModulePath[] paths = GetEachFileNameOfModule(info.ImportingSourceFileName, importedFileName, host, true);
        foreach (ModulePath p in paths)
        {
            allFileNames[p.FileName] = p;
        }

        bool useCaseSensitiveFileNames = info.UseCaseSensitiveFileNames;
        int Compare(ModulePath a, ModulePath b) => ModuleSpecifierUtil.ComparePathsByRedirect(a, b, useCaseSensitiveFileNames);

        // Sort by paths closest to importing file name directory.
        var sortedPaths = new List<ModulePath>(paths.Length);
        string directory = info.SourceDirectory;
        while (allFileNames.Count != 0)
        {
            string directoryStart = TsPathApi.EnsureTrailingDirectorySeparator(directory);
            List<ModulePath>? pathsInDirectory = null;
            List<string>? toRemove = null;
            // map-iter: 序无关 — collected then sorted before append.
            foreach ((string fileName, ModulePath p) in allFileNames)
            {
                if (fileName.StartsWith(directoryStart, StringComparison.Ordinal))
                {
                    (pathsInDirectory ??= []).Add(p);
                    (toRemove ??= []).Add(fileName);
                }
            }
            if (toRemove != null)
            {
                foreach (string k in toRemove)
                {
                    allFileNames.Remove(k);
                }
            }
            if (pathsInDirectory != null)
            {
                pathsInDirectory.Sort(Compare);
                sortedPaths.AddRange(pathsInDirectory);
            }
            string newDirectory = TsPathApi.GetDirectoryPath(directory);
            if (newDirectory == directory)
            {
                break;
            }
            directory = newDirectory;
        }
        if (allFileNames.Count > 0)
        {
            var remainingPaths = allFileNames.Values.ToList();
            remainingPaths.Sort(Compare);
            sortedPaths.AddRange(remainingPaths);
        }
        return sortedPaths.ToArray();
    }

    // specifiers.go:260 — GetEachFileNameOfModule. Returns all possible file paths for a module,
    // including symlink alternatives.
    public static ModulePath[] GetEachFileNameOfModule(
        string importingFileName,
        string importedFileName,
        ModuleSpecifierGenerationHost host,
        bool preferSymlinks)
    {
        string cwd = host.GetCurrentDirectory();
        Path importedPath = TsPathApi.ToPath(importedFileName, cwd, host.UseCaseSensitiveFileNames());
        string referenceRedirect = "";
        var outputAndReference = host.GetProjectReferenceFromSource(importedPath);
        if (outputAndReference != null && outputAndReference.OutputDts != "")
        {
            referenceRedirect = outputAndReference.OutputDts;
        }

        string[] redirects = host.GetRedirectTargets(importedPath);
        var importedFileNames = new List<string>(2 + redirects.Length);
        if (referenceRedirect.Length > 0)
        {
            importedFileNames.Add(referenceRedirect);
        }
        importedFileNames.Add(importedFileName);
        importedFileNames.AddRange(redirects);
        string[] targets = CoreUtil.Map(importedFileNames.ToArray(), f => TsPathApi.GetNormalizedAbsolutePath(f, cwd)) ?? [];
        bool shouldFilterIgnoredPaths = !CoreUtil.Every(targets, ContainsIgnoredPath);

        var results = new List<ModulePath>(2);
        if (!preferSymlinks)
        {
            foreach (string p in targets)
            {
                if (!(shouldFilterIgnoredPaths && ContainsIgnoredPath(p)))
                {
                    results.Add(new ModulePath { FileName = p, IsInNodeModules = ContainsNodeModules(p), IsRedirect = referenceRedirect == p });
                }
            }
        }

        var symlinkCache = host.GetSymlinkCache();
        string fullImportedFileName = TsPathApi.GetNormalizedAbsolutePath(importedFileName, cwd);
        if (symlinkCache != null)
        {
            TsPathApi.ForEachAncestorDirectoryStoppingAtGlobalCache<bool>(
                host.GetGlobalTypingsCacheLocation(),
                TsPathApi.GetDirectoryPath(fullImportedFileName),
                realPathDirectory =>
                {
                    if (!symlinkCache.DirectoriesByRealpath().TryLoad(TsPathApi.ToPath(realPathDirectory, cwd, host.UseCaseSensitiveFileNames()).EnsureTrailingDirectorySeparator(), out var symlinkSet))
                    {
                        return (false, false); // Continue to ancestor directory
                    }

                    // Don't want a package to globally import from itself.
                    if (TsPathApi.StartsWithDirectory(importingFileName, realPathDirectory, host.UseCaseSensitiveFileNames()))
                    {
                        return (false, true); // Stop search, each ancestor directory will also hit this condition
                    }

                    foreach (string target in targets)
                    {
                        if (!TsPathApi.StartsWithDirectory(target, realPathDirectory, host.UseCaseSensitiveFileNames()))
                        {
                            continue;
                        }

                        string relative = TsPathApi.GetRelativePathFromDirectory(realPathDirectory, target, new ComparePathsOptions
                        {
                            UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                            CurrentDirectory = cwd,
                        });
                        symlinkSet!.Range(symlinkDirectory =>
                        {
                            string option = TsPathApi.ResolvePath(symlinkDirectory, relative);
                            results.Add(new ModulePath { FileName = option, IsInNodeModules = ContainsNodeModules(option), IsRedirect = target == referenceRedirect });
                            shouldFilterIgnoredPaths = true; // Found a non-ignored path in symlinks, so we can reject ignored-path realpaths
                            return true;
                        });
                    }

                    return (false, false);
                });
        }

        if (preferSymlinks)
        {
            foreach (string p in targets)
            {
                if (!(shouldFilterIgnoredPaths && ContainsIgnoredPath(p)))
                {
                    results.Add(new ModulePath { FileName = p, IsInNodeModules = ContainsNodeModules(p), IsRedirect = referenceRedirect == p });
                }
            }
        }

        return results.ToArray();
    }

    // specifiers.go:157 — Info.
    internal struct Info
    {
        public bool UseCaseSensitiveFileNames;
        public string ImportingSourceFileName;
        public string SourceDirectory;
    }

    // specifiers.go:163 — getInfo.
    internal static Info GetInfo(string importingSourceFileName, ModuleSpecifierGenerationHost host)
    {
        string sourceDirectory = TsPathApi.GetDirectoryPath(importingSourceFileName);
        return new Info
        {
            ImportingSourceFileName = importingSourceFileName,
            SourceDirectory = sourceDirectory,
            UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
        };
    }

    // specifiers.go:359 — computeModuleSpecifiers.
    internal static (string[] Specifiers, ResultKind Kind) ComputeModuleSpecifiers(
        ModulePath[] modulePaths,
        CompilerOptions compilerOptions,
        SourceFileForSpecifierGeneration importingSourceFile,
        ModuleSpecifierGenerationHost host,
        UserPreferences userPreferences,
        ModuleSpecifierOptions options,
        bool forAutoImport)
    {
        Info info = GetInfo(importingSourceFile.FileName(), host);
        ModuleSpecifierPreferences preferences = GetModuleSpecifierPreferences(userPreferences, host, compilerOptions, importingSourceFile, "");

        string existingSpecifier = "";
        foreach (ModulePath modulePath in modulePaths)
        {
            Path targetPath = TsPathApi.ToPath(modulePath.FileName, host.GetCurrentDirectory(), info.UseCaseSensitiveFileNames);
            Node? existingImport = null;
            foreach (Node importSpecifier in importingSourceFile.Imports() ?? [])
            {
                var resolvedModule = host.GetResolvedModuleFromModuleSpecifier(importingSourceFile, importSpecifier);
                if (resolvedModule.IsResolved() &&
                    TsPathApi.ToPath(resolvedModule!.ResolvedFileName, host.GetCurrentDirectory(), info.UseCaseSensitiveFileNames).Value == targetPath.Value)
                {
                    existingImport = importSpecifier;
                    break;
                }
            }
            if (existingImport != null)
            {
                if (preferences.RelativePreference == RelativePreferenceKind.NonRelative && TsPathApi.PathIsRelative(existingImport.Text()))
                {
                    // If the preference is for non-relative and the specifier is relative, ignore it.
                    continue;
                }
                ResolutionMode existingMode = host.GetModeForUsageLocation(importingSourceFile, existingImport);
                ResolutionMode targetMode = options.OverrideImportMode;
                if (targetMode == global::Ts.Core.Core.ResolutionModeNone)
                {
                    targetMode = host.GetDefaultResolutionModeForFile(importingSourceFile);
                }
                if (existingMode != targetMode && existingMode != global::Ts.Core.Core.ResolutionModeNone && targetMode != global::Ts.Core.Core.ResolutionModeNone)
                {
                    // If the candidate import mode doesn't match what we're generating for, skip it.
                    continue;
                }
                existingSpecifier = existingImport.Text();
                break;
            }
        }

        if (existingSpecifier != "")
        {
            return ([existingSpecifier], ResultKind.None);
        }

        bool importedFileIsInNodeModules = modulePaths.Any(p => p.IsInNodeModules);

        // Priority: 1. bare package specifiers (node_modules→"types"), 2. tsconfig `paths`,
        // 3. non-relative node_modules specifiers, 4. relative paths.
        var pathsSpecifiers = new List<string>();
        var redirectPathsSpecifiers = new List<string>();
        var nodeModulesSpecifiers = new List<string>();
        var relativeSpecifiers = new List<string>();

        foreach (ModulePath modulePath in modulePaths)
        {
            string specifier = "";
            if (modulePath.IsInNodeModules)
            {
                specifier = TryGetModuleNameAsNodeModule(modulePath, info, importingSourceFile, host, compilerOptions, userPreferences, false /*packageNameOnly*/, options.OverrideImportMode);
            }
            if (specifier.Length > 0 && !(forAutoImport && ModuleSpecifierUtil.IsExcludedByRegex(specifier, preferences.ExcludeRegexes)))
            {
                nodeModulesSpecifiers.Add(specifier);
                if (modulePath.IsRedirect)
                {
                    // A specifier for a redirect is a bare package specifier — no other will be this good.
                    return (nodeModulesSpecifiers.ToArray(), ResultKind.NodeModules);
                }
            }

            ResolutionMode importMode = options.OverrideImportMode;
            if (importMode == global::Ts.Core.Core.ResolutionModeNone)
            {
                importMode = host.GetDefaultResolutionModeForFile(importingSourceFile);
            }
            string local = GetLocalModuleSpecifier(modulePath.FileName, info, compilerOptions, host, importMode, preferences, modulePath.IsRedirect || specifier.Length > 0);
            if (local.Length == 0 || (forAutoImport && ModuleSpecifierUtil.IsExcludedByRegex(local, preferences.ExcludeRegexes)))
            {
                continue;
            }
            if (modulePath.IsRedirect)
            {
                redirectPathsSpecifiers.Add(local);
            }
            else if (ModuleSpecifierUtil.PathIsBareSpecifier(local))
            {
                if (ContainsNodeModules(local))
                {
                    // baseUrl-generated specifiers containing /node_modules/ trigger a portability error;
                    // don't prioritize them.
                    relativeSpecifiers.Add(local);
                }
                else
                {
                    pathsSpecifiers.Add(local);
                }
            }
            else if (forAutoImport || !importedFileIsInNodeModules || modulePath.IsInNodeModules)
            {
                relativeSpecifiers.Add(local);
            }
        }

        if (pathsSpecifiers.Count > 0)
        {
            return (pathsSpecifiers.ToArray(), ResultKind.Paths);
        }
        if (redirectPathsSpecifiers.Count > 0)
        {
            return (redirectPathsSpecifiers.ToArray(), ResultKind.Redirect);
        }
        if (nodeModulesSpecifiers.Count > 0)
        {
            return (nodeModulesSpecifiers.ToArray(), ResultKind.NodeModules);
        }
        return (relativeSpecifiers.ToArray(), ResultKind.Relative);
    }

    // specifiers.go:485 — getLocalModuleSpecifier.
    internal static string GetLocalModuleSpecifier(
        string moduleFileName,
        Info info,
        CompilerOptions compilerOptions,
        ModuleSpecifierGenerationHost host,
        ResolutionMode importMode,
        ModuleSpecifierPreferences preferences,
        bool pathsOnly)
    {
        OrderedMap<string, string[]>? paths = compilerOptions.Paths;
        string[]? rootDirs = compilerOptions.RootDirs;

        if (pathsOnly && paths == null)
        {
            return "";
        }

        string sourceDirectory = info.SourceDirectory;

        ModuleSpecifierEnding[] allowedEndings = preferences.GetAllowedEndingsInPreferredOrder(importMode);
        string relativePath = "";
        if ((rootDirs?.Length ?? 0) > 0)
        {
            relativePath = TryGetModuleNameFromRootDirs(rootDirs, moduleFileName, sourceDirectory, allowedEndings, compilerOptions, host);
        }
        if (relativePath.Length == 0)
        {
            relativePath = ProcessEnding(ModuleSpecifierUtil.EnsurePathIsNonModuleName(TsPathApi.GetRelativePathFromDirectory(sourceDirectory, moduleFileName, new ComparePathsOptions
            {
                UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                CurrentDirectory = host.GetCurrentDirectory(),
            })), allowedEndings, compilerOptions, host);
        }

        if ((paths == null && !compilerOptions.GetResolvePackageJsonImports()) || preferences.RelativePreference == RelativePreferenceKind.Relative)
        {
            return pathsOnly ? "" : relativePath;
        }

        string root = compilerOptions.GetPathsBasePath(host.GetCurrentDirectory());
        string baseDirectory = TsPathApi.GetNormalizedAbsolutePath(root, host.GetCurrentDirectory());
        string relativeToBaseUrl = ModuleSpecifierUtil.GetRelativePathIfInSameVolume(moduleFileName, baseDirectory, host.UseCaseSensitiveFileNames());
        if (relativeToBaseUrl.Length == 0)
        {
            return pathsOnly ? "" : relativePath;
        }

        string fromPackageJsonImports = "";
        if (!pathsOnly)
        {
            fromPackageJsonImports = TryGetModuleNameFromPackageJsonImports(moduleFileName, sourceDirectory, compilerOptions, host, importMode, ModuleSpecifierUtil.PrefersTsExtension(allowedEndings));
        }

        string fromPaths = "";
        if ((pathsOnly || fromPackageJsonImports.Length == 0) && paths != null)
        {
            fromPaths = TryGetModuleNameFromPaths(relativeToBaseUrl, paths, allowedEndings, baseDirectory, host, compilerOptions);
        }

        if (pathsOnly)
        {
            return fromPaths;
        }

        string maybeNonRelative = fromPackageJsonImports.Length > 0 ? fromPackageJsonImports : fromPaths;
        if (maybeNonRelative.Length == 0)
        {
            return relativePath;
        }

        bool relativeIsExcluded = ModuleSpecifierUtil.IsExcludedByRegex(relativePath, preferences.ExcludeRegexes);
        bool nonRelativeIsExcluded = ModuleSpecifierUtil.IsExcludedByRegex(maybeNonRelative, preferences.ExcludeRegexes);
        if (!relativeIsExcluded && nonRelativeIsExcluded)
        {
            return relativePath;
        }
        if (relativeIsExcluded && !nonRelativeIsExcluded)
        {
            return maybeNonRelative;
        }

        if (preferences.RelativePreference == RelativePreferenceKind.NonRelative && !TsPathApi.PathIsRelative(maybeNonRelative))
        {
            return maybeNonRelative;
        }

        if (preferences.RelativePreference == RelativePreferenceKind.ExternalNonRelative && !TsPathApi.PathIsRelative(maybeNonRelative))
        {
            Path projectDirectory = compilerOptions.ConfigFilePath.Length > 0
                ? TsPathApi.ToPath(TsPathApi.GetDirectoryPath(compilerOptions.ConfigFilePath), host.GetCurrentDirectory(), host.UseCaseSensitiveFileNames())
                : TsPathApi.ToPath(host.GetCurrentDirectory(), host.GetCurrentDirectory(), host.UseCaseSensitiveFileNames());
            Path canonicalSourceDirectory = TsPathApi.ToPath(sourceDirectory, host.GetCurrentDirectory(), host.UseCaseSensitiveFileNames());
            Path modulePath = TsPathApi.ToPath(moduleFileName, projectDirectory.Value, host.UseCaseSensitiveFileNames());

            bool sourceIsInternal = projectDirectory.ContainsPath(canonicalSourceDirectory);
            bool targetIsInternal = projectDirectory.ContainsPath(modulePath);
            if ((sourceIsInternal && !targetIsInternal) || (!sourceIsInternal && targetIsInternal))
            {
                // The import path crosses the boundary of the tsconfig.json-containing directory.
                return maybeNonRelative;
            }

            string nearestTargetPackageJson = host.GetNearestAncestorDirectoryWithPackageJson(TsPathApi.GetDirectoryPath(modulePath.Value));
            string nearestSourcePackageJson = host.GetNearestAncestorDirectoryWithPackageJson(sourceDirectory);

            if (!ModuleSpecifierUtil.PackageJsonPathsAreEqual(nearestTargetPackageJson, nearestSourcePackageJson, new ComparePathsOptions
            {
                UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                CurrentDirectory = host.GetCurrentDirectory(),
            }))
            {
                // The importing and imported files are part of different packages.
                return maybeNonRelative;
            }

            return relativePath;
        }

        // Prefer a relative import over a baseUrl import if it has fewer components.
        if (ModuleSpecifierUtil.IsPathRelativeToParent(maybeNonRelative) || Compare.CountPathComponents(relativePath) < Compare.CountPathComponents(maybeNonRelative))
        {
            return relativePath;
        }
        return maybeNonRelative;
    }

    // specifiers.go:636 — processEnding.
    internal static string ProcessEnding(string fileName, ModuleSpecifierEnding[] allowedEndings, CompilerOptions options, ModuleSpecifierGenerationHost? host)
    {
        if (TsPathApi.FileExtensionIsOneOf(fileName, [TsPathApi.ExtensionJson, TsPathApi.ExtensionMjs, TsPathApi.ExtensionCjs]))
        {
            return fileName;
        }

        string noExtension = TsPathApi.RemoveFileExtension(fileName);
        if (fileName == noExtension)
        {
            return fileName;
        }

        int jsPriority = Array.IndexOf(allowedEndings, ModuleSpecifierEnding.JsExtension);
        int tsPriority = Array.IndexOf(allowedEndings, ModuleSpecifierEnding.TsExtension);
        if (TsPathApi.FileExtensionIsOneOf(fileName, [TsPathApi.ExtensionMts, TsPathApi.ExtensionCts]) && tsPriority != -1 && tsPriority < jsPriority)
        {
            return fileName;
        }
        if (TsPathApi.FileExtensionIsOneOf(fileName, [TsPathApi.ExtensionDmts, TsPathApi.ExtensionDcts]))
        {
            string inputExt = TsPathApi.GetDeclarationFileExtension(fileName);
            string ext = ModuleSpecifierUtil.GetJSExtensionForDeclarationFileExtension(inputExt);
            return TsPathApi.RemoveExtension(fileName, inputExt) + ext;
        }
        if (TsPathApi.FileExtensionIsOneOf(fileName, [TsPathApi.ExtensionMts, TsPathApi.ExtensionCts]))
        {
            return noExtension + ModuleSpecifierUtil.GetJSExtensionForFile(fileName, options);
        }
        if (!TsPathApi.FileExtensionIsOneOf(fileName, [TsPathApi.ExtensionDts]) && TsPathApi.FileExtensionIsOneOf(fileName, [TsPathApi.ExtensionTs]) && fileName.Contains(".d.", StringComparison.Ordinal))
        {
            // `foo.d.json.ts` and the like - remap back to `foo.json`
            string result = ModuleSpecifierUtil.TryGetRealFileNameForNonJSDeclarationFileName(fileName);
            if (result != "")
            {
                return result;
            }
        }

        switch (allowedEndings[0])
        {
            case ModuleSpecifierEnding.Minimal:
            {
                string withoutIndex = noExtension.EndsWith("/index", StringComparison.Ordinal) ? noExtension[..^"/index".Length] : noExtension;
                if (host != null && withoutIndex != noExtension && ModuleSpecifierUtil.TryGetAnyFileFromPath(host, withoutIndex))
                {
                    // Can't remove index if there's a file by the same name as the directory.
                    return noExtension;
                }
                return withoutIndex;
            }
            case ModuleSpecifierEnding.Index:
                return noExtension;
            case ModuleSpecifierEnding.JsExtension:
                return noExtension + ModuleSpecifierUtil.GetJSExtensionForFile(fileName, options);
            case ModuleSpecifierEnding.TsExtension:
                // For now we don't know if this import is type-only, so use no extension or a .js extension.
                if (TsPathApi.IsDeclarationFileName(fileName))
                {
                    int extensionlessPriority = -1;
                    for (int i = 0; i < allowedEndings.Length; i++)
                    {
                        if (allowedEndings[i] == ModuleSpecifierEnding.Minimal || allowedEndings[i] == ModuleSpecifierEnding.Index)
                        {
                            extensionlessPriority = i;
                            break;
                        }
                    }
                    if (extensionlessPriority != -1 && extensionlessPriority < jsPriority)
                    {
                        return noExtension;
                    }
                    return noExtension + ModuleSpecifierUtil.GetJSExtensionForFile(fileName, options);
                }
                return fileName;
            default:
                global::Ts.Core.Debug.AssertNever(allowedEndings[0]);
                return "";
        }
    }

    // specifiers.go:707 — tryGetModuleNameFromRootDirs.
    internal static string TryGetModuleNameFromRootDirs(
        string[]? rootDirs,
        string moduleFileName,
        string sourceDirectory,
        ModuleSpecifierEnding[] allowedEndings,
        CompilerOptions compilerOptions,
        ModuleSpecifierGenerationHost host)
    {
        string[]? normalizedTargetPaths = ModuleSpecifierUtil.GetPathsRelativeToRootDirs(moduleFileName, rootDirs, host.UseCaseSensitiveFileNames());
        if (normalizedTargetPaths == null || normalizedTargetPaths.Length == 0)
        {
            return "";
        }

        string[]? normalizedSourcePaths = ModuleSpecifierUtil.GetPathsRelativeToRootDirs(sourceDirectory, rootDirs, host.UseCaseSensitiveFileNames());
        string shortest = "";
        int shortestSepCount = 0;
        foreach (string sourcePath in normalizedSourcePaths ?? [])
        {
            foreach (string targetPath in normalizedTargetPaths)
            {
                string candidate = ModuleSpecifierUtil.EnsurePathIsNonModuleName(TsPathApi.GetRelativePathFromDirectory(sourcePath, targetPath, new ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                    CurrentDirectory = host.GetCurrentDirectory(),
                }));
                int candidateSepCount = candidate.Count(ch => ch == '/');
                if (shortest.Length == 0 || candidateSepCount < shortestSepCount)
                {
                    shortest = candidate;
                    shortestSepCount = candidateSepCount;
                }
            }
        }

        if (shortest.Length == 0)
        {
            return "";
        }
        return ProcessEnding(shortest, allowedEndings, compilerOptions, host);
    }

    // specifiers.go:247 — containsIgnoredPath. Local helper duplicating tspath.ContainsIgnoredPath for
    // performance.
    internal static bool ContainsIgnoredPath(string s) =>
        s.Contains("/node_modules/.", StringComparison.Ordinal) ||
        s.Contains("/.git", StringComparison.Ordinal) ||
        s.Contains(".#", StringComparison.Ordinal);

    // specifiers.go:254 — ContainsNodeModules.
    public static bool ContainsNodeModules(string s) =>
        s.Contains("/node_modules/", StringComparison.Ordinal);
}
