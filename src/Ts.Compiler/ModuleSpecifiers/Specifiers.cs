// Ported from tsgo internal/modulespecifiers/specifiers.go — the module-specifier generation core.
// PORT-NOTE (in progress): leaf/support functions land first; the GetModuleSpecifiers entry chain
// (computeModuleSpecifiers / getLocalModuleSpecifier / package.json exports-imports resolution) and the
// wiring of nodeBuilder.getSpecifierForModuleSymbol follow in subsequent batches.

using System;
using System.Collections.Generic;
using System.Linq;

using Ts.Core;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class Specifiers
{
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
