// Ported from tsgo internal/project/ata/discovertypings.go (whole file).

using System;
using System.Collections.Generic;
using System.Globalization;
using Ts.Collections;
using Ts.Core;

namespace Ts.Project.Ata;

public static class DiscoverTypingsApi
{
    // discovertypings.go:20-27 — isTypingUpToDate.
    internal static bool IsTypingUpToDate(CachedTyping cachedTyping, Dictionary<string, string> availableTypingVersions)
    {
        if (!availableTypingVersions.TryGetValue("ts" + global::Ts.Core.TsVersion.VersionMajorMinor(), out string? useVersion))
        {
            availableTypingVersions.TryGetValue("latest", out useVersion);
        }
        global::Ts.Semver.Version availableVersion = global::Ts.Semver.Semver.MustParse(useVersion!);
        // Go: availableVersion.Compare(cachedTyping.Version) — a nil *Version panics inside
        // Compare; Nullable<Version>.Value is the throwing analog (the `!` satisfies the
        // nullable analysis for the struct form).
        return availableVersion.Compare(cachedTyping.Version!.Value) <= 0;
    }

    // discovertypings.go:29-104 — DiscoverTypings.
    public static (string[] CachedTypingPaths, string[] NewTypingNames, string[] FilesToWatch) DiscoverTypings(
        global::Ts.Vfs.IFileSystem fs,
        global::Ts.Project.Logging.Logger logger,
        TypingsInfo typingsInfo,
        string[] fileNames,
        string projectRootPath,
        global::Ts.Collections.SyncMap<string, CachedTyping> packageNameToTypingLocation,
        Dictionary<string, Dictionary<string, string>> typesRegistry)
    {
        // A typing name to typing file path mapping
        var inferredTypings = new Dictionary<string, string>();

        // Only infer typings for .js and .jsx files
        fileNames = global::Ts.Core.CoreUtil.Filter(fileNames, fileName => global::Ts.TsPath.TsPath.HasJSFileExtension(fileName)) ?? [];

        string[] filesToWatch = [];
        if (typingsInfo.TypeAcquisition!.Include != null)
        {
            AddInferredTypings(fs, logger, inferredTypings, typingsInfo.TypeAcquisition.Include, "Explicitly included types");
        }
        string[]? exclude = typingsInfo.TypeAcquisition.Exclude;

        // Directories to search for package.json, bower.json and other typing information
        if (typingsInfo.CompilerOptions!.Types == null)
        {
            var possibleSearchDirs = new HashSet<string>();
            foreach (string fileName in fileNames)
            {
                possibleSearchDirs.Add(global::Ts.TsPath.TsPath.GetDirectoryPath(fileName));
            }
            possibleSearchDirs.Add(projectRootPath);
            // map-iter: 序无关 — each search dir accumulates independently into
            // filesToWatch/inferredTypings.
            foreach (string searchDir in possibleSearchDirs)
            {
                filesToWatch = AddTypingNamesAndGetFilesToWatch(fs, logger, inferredTypings, filesToWatch, searchDir, "bower.json", "bower_components");
                filesToWatch = AddTypingNamesAndGetFilesToWatch(fs, logger, inferredTypings, filesToWatch, searchDir, "package.json", "node_modules");
            }
        }

        if (!typingsInfo.TypeAcquisition.DisableFilenameBasedTypeAcquisition.IsTrue())
        {
            GetTypingNamesFromSourceFileNames(fs, logger, inferredTypings, fileNames);
        }

        // add typings for unresolved imports
        string[] modules = [];
        if (typingsInfo.UnresolvedImports != null)
        {
            var moduleList = new List<string>(typingsInfo.UnresolvedImports.Len());
            // map-iter: 已排序 — modules are sorted + deduped below to match Go.
            foreach (string module in typingsInfo.UnresolvedImports.Keys())
            {
                moduleList.Add(global::Ts.Core.Core.NonRelativeModuleNameForTypingCache(module));
            }
            modules = moduleList.ToArray();
            Array.Sort(modules, StringComparer.Ordinal);
            modules = SortedCompact(modules);
        }
        AddInferredTypings(fs, logger, inferredTypings, modules, "Inferred typings from unresolved imports");

        // Remove typings that the user has added to the exclude list
        foreach (string excludeTypingName in exclude ?? [])
        {
            inferredTypings.Remove(excludeTypingName);
            logger.Logf("ATA:: Typing for {0} is in exclude list, will be ignored.", excludeTypingName);
        }

        // Add the cached typing locations for inferred typings that are already installed
        packageNameToTypingLocation.Range((name, typing) =>
        {
            Dictionary<string, string>? registryEntry = typesRegistry.GetValueOrDefault(name);
            // Go: inferredTypings[name] == "" — the zero-value read is true both for names
            // absent from the map and for names inferred earlier with no location yet ("");
            // the cached location must fill the latter (05-conventions.md §7.1).
            if (inferredTypings.GetValueOrDefault(name) == "" && registryEntry != null && IsTypingUpToDate(typing, registryEntry))
            {
                inferredTypings[name] = typing.TypingsLocation;
            }
            return true;
        });

        var cachedTypingPaths = new List<string>();
        var newTypingNames = new List<string>();
        // map-iter: 序无关 — classification per entry; callers sort the results.
        foreach ((string typing, string inferred) in inferredTypings)
        {
            if (inferred != "")
            {
                cachedTypingPaths.Add(inferred);
            }
            else
            {
                newTypingNames.Add(typing);
            }
        }
        logger.Logf("ATA:: Finished typings discovery: cachedTypingsPaths: {0} newTypingNames: {1}, filesToWatch {2}", FormatList(cachedTypingPaths), FormatList(newTypingNames), FormatList(filesToWatch));
        return (cachedTypingPaths.ToArray(), newTypingNames.ToArray(), filesToWatch);
    }

    // discovertypings.go:106-110 — addInferredTyping.
    private static void AddInferredTyping(Dictionary<string, string> inferredTypings, string typingName)
    {
        if (!inferredTypings.ContainsKey(typingName))
        {
            inferredTypings[typingName] = "";
        }
    }

    // discovertypings.go:112-122 — addInferredTypings.
    private static void AddInferredTypings(
        global::Ts.Vfs.IFileSystem fs,
        global::Ts.Project.Logging.Logger logger,
        Dictionary<string, string> inferredTypings,
        string[] typingNames, string message)
    {
        _ = fs;
        logger.Logf("ATA:: {0}: {1}", message, FormatList(typingNames));
        foreach (string typingName in typingNames)
        {
            AddInferredTyping(inferredTypings, typingName);
        }
    }

    /**
     * Infer typing names from given file names. For example, the file name "jquery-min.2.3.4.js"
     * should be inferred to the 'jquery' typing name; and "angular-route.1.2.3.js" should be inferred
     * to the 'angular-route' typing name.
     * @param fileNames are the names for source files in the project
     */
    // discovertypings.go:130-153 — getTypingNamesFromSourceFileNames.
    private static void GetTypingNamesFromSourceFileNames(
        global::Ts.Vfs.IFileSystem fs,
        global::Ts.Project.Logging.Logger logger,
        Dictionary<string, string> inferredTypings,
        string[] fileNames)
    {
        bool hasJsxFile = false;
        var fromFileNames = new List<string>();
        foreach (string fileName in fileNames)
        {
            hasJsxFile = hasJsxFile || global::Ts.TsPath.TsPath.FileExtensionIs(fileName, global::Ts.TsPath.TsPath.ExtensionJsx);
            string inferredTypingName = global::Ts.TsPath.TsPath.RemoveFileExtension(global::Ts.TsPath.TsPath.ToFileNameLowerCase(global::Ts.TsPath.TsPath.GetBaseFileName(fileName)));
            string cleanedTypingName = RemoveMinAndVersionNumbers(inferredTypingName);
            if (TypesMap.SafeFileNameToTypeName.TryGetValue(cleanedTypingName, out string? typeName))
            {
                fromFileNames.Add(typeName);
            }
        }
        if (fromFileNames.Count > 0)
        {
            AddInferredTypings(fs, logger, inferredTypings, fromFileNames.ToArray(), "Inferred typings from file names");
        }
        if (hasJsxFile)
        {
            logger.Log("ATA:: Inferred 'react' typings due to presence of '.jsx' extension");
            AddInferredTyping(inferredTypings, "react");
        }
    }

    /**
     * Adds inferred typings from manifest/module pairs (think package.json + node_modules)
     *
     * @param projectRootPath is the path to the directory where to look for package.json, bower.json and other typing information
     * @param manifestName is the name of the manifest (package.json or bower.json)
     * @param modulesDirName is the directory name for modules (node_modules or bower_components). Should be lowercase!
     * @param filesToWatch are the files to watch for changes. We will push things into this array.
     */
    // discovertypings.go:163-284 — addTypingNamesAndGetFilesToWatch.
    private static string[] AddTypingNamesAndGetFilesToWatch(
        global::Ts.Vfs.IFileSystem fs,
        global::Ts.Project.Logging.Logger logger,
        Dictionary<string, string> inferredTypings,
        string[] filesToWatch,
        string projectRootPath,
        string manifestName,
        string modulesDirName)
    {
        // First, we check the manifests themselves. They're not
        // _required_, but they allow us to do some filtering when dealing
        // with big flat dep directories.
        string manifestPath = global::Ts.TsPath.TsPath.CombinePaths(projectRootPath, manifestName);
        var manifestTypingNames = new List<string>();
        if (fs.TryReadFile(manifestPath, out string manifestContents))
        {
            (global::Ts.PackageJson.Fields manifest, bool parsed) = global::Ts.PackageJson.PackageJsonParser.Parse(System.Text.Encoding.UTF8.GetBytes(manifestContents));
            filesToWatch = [.. filesToWatch, manifestPath];
            if (parsed)
            {
                // map-iter: 序无关 — dependency maps feed a name-accumulation list.
                foreach (Dictionary<string, string>? deps in new Dictionary<string, string>?[] { manifest.DependencyFields.Dependencies.Value, manifest.DependencyFields.DevDependencies.Value, manifest.DependencyFields.OptionalDependencies.Value, manifest.DependencyFields.PeerDependencies.Value })
                {
                    if (deps is not null)
                    {
                        // map-iter: 序无关 — same as above.
                        foreach (string key in deps.Keys)
                        {
                            manifestTypingNames.Add(key);
                        }
                    }
                }
                AddInferredTypings(fs, logger, inferredTypings, manifestTypingNames.ToArray(), "Typing names in '" + manifestPath + "' dependencies");
            }
        }

        // Now we scan the directories for typing information in
        // already-installed dependencies (if present). Note that this
        // step happens regardless of whether a manifest was present,
        // which is certainly a valid configuration, if an unusual one.
        string packagesFolderPath = global::Ts.TsPath.TsPath.CombinePaths(projectRootPath, modulesDirName);
        filesToWatch = [.. filesToWatch, packagesFolderPath];
        if (!fs.DirectoryExists(packagesFolderPath))
        {
            return filesToWatch;
        }

        // There's two cases we have to take into account here:
        // 1. If manifest is undefined, then we're not using a manifest.
        //    That means that we should scan _all_ dependencies at the top
        //    level of the modulesDir.
        // 2. If manifest is defined, then we can do some special
        //    filtering to reduce the amount of scanning we need to do.
        //
        // Previous versions of this algorithm checked for a `_requiredBy`
        // field in the package.json, but that field is only present in
        // `npm@>=3 <7`.

        // Package names that do **not** provide their own typings, so
        // we'll look them up.
        var packageNames = new List<string>();

        var dependencyManifestNames = new List<string>();
        if (manifestTypingNames.Count > 0)
        {
            // This is #1 described above.
            foreach (string typingName in manifestTypingNames)
            {
                dependencyManifestNames.Add(global::Ts.TsPath.TsPath.CombinePaths(packagesFolderPath, typingName, manifestName));
            }
        }
        else
        {
            // And #2. Depth = 3 because scoped packages look like `node_modules/@foo/bar/package.json`
            int depth = 3;
            foreach (string foundManifestPath in global::Ts.Vfs.VfsMatch.VfsMatch.ReadDirectory(fs, projectRootPath, packagesFolderPath, [global::Ts.TsPath.TsPath.ExtensionJson], null, null, depth))
            {
                if (global::Ts.TsPath.TsPath.GetBaseFileName(foundManifestPath) != manifestName)
                {
                    continue;
                }

                // It's ok to treat
                // `node_modules/@foo/bar/package.json` as a manifest,
                // but not `node_modules/jquery/nested/package.json`.
                // We only assume depth 3 is ok for formally scoped
                // packages. So that needs this dance here.

                List<string> pathComponents = global::Ts.TsPath.TsPath.GetPathComponents(foundManifestPath, "");
                int lenPathComponents = pathComponents.Count;
                bool isScoped = pathComponents[lenPathComponents - 3].Length > 0 && pathComponents[lenPathComponents - 3][0] == '@';

                if (isScoped && global::Ts.TsPath.TsPath.ToFileNameLowerCase(pathComponents[lenPathComponents - 4]) == modulesDirName || // `node_modules/@foo/bar`
                    !isScoped && global::Ts.TsPath.TsPath.ToFileNameLowerCase(pathComponents[lenPathComponents - 3]) == modulesDirName)
                {
                    // `node_modules/foo`
                    dependencyManifestNames.Add(foundManifestPath);
                }
            }
        }

        logger.Logf("ATA:: Searching for typing names in {0}; all files: {1}", packagesFolderPath, FormatList(dependencyManifestNames));

        // Once we have the names of things to look up, we iterate over
        // and either collect their included typings, or add them to the
        // list of typings we need to look up separately.
        foreach (string foundManifestPath in dependencyManifestNames)
        {
            if (!fs.TryReadFile(foundManifestPath, out string foundManifestContents))
            {
                continue;
            }
            (global::Ts.PackageJson.Fields manifest, bool parsed) = global::Ts.PackageJson.PackageJsonParser.Parse(System.Text.Encoding.UTF8.GetBytes(foundManifestContents));
            // If the package has its own d.ts typings, those will take precedence. Otherwise the package name will be used
            // to download d.ts files from DefinitelyTyped
            if (!parsed || string.IsNullOrEmpty(manifest.HeaderFields.Name.Value))
            {
                continue;
            }
            string ownTypes = manifest.PathFields.Types.Value ?? "";
            if (ownTypes.Length == 0)
            {
                ownTypes = manifest.PathFields.Typings.Value ?? "";
            }
            if (ownTypes.Length != 0)
            {
                string absolutePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(ownTypes, global::Ts.TsPath.TsPath.GetDirectoryPath(foundManifestPath));
                if (fs.FileExists(absolutePath))
                {
                    logger.Logf("ATA::     Package '{0}' provides its own types.", manifest.HeaderFields.Name.Value);
                    inferredTypings[manifest.HeaderFields.Name.Value] = absolutePath;
                }
                else
                {
                    logger.Logf("ATA::     Package '{0}' provides its own types but they are missing.", manifest.HeaderFields.Name.Value);
                }
            }
            else
            {
                packageNames.Add(manifest.HeaderFields.Name.Value);
            }
        }
        AddInferredTypings(fs, logger, inferredTypings, packageNames.ToArray(), "    Found package names");
        return filesToWatch;
    }

    /**
     * Takes a string like "jquery-min.4.2.3" and returns "jquery"
     *
     * @internal
     */
    // discovertypings.go:291-334 — removeMinAndVersionNumbers.
    internal static string RemoveMinAndVersionNumbers(string fileName)
    {
        // We used to use the regex /[.-]((min)|(\d+(\.\d+)*))$/ and would just .replace it twice.
        // Unfortunately, that regex has O(n^2) performance because v8 doesn't match from the end of the string.
        // Instead, we now essentially scan the filename (backwards) ourselves.
        int end = fileName.Length;
        int pos = end;
        while (pos > 0)
        {
            char ch = fileName[pos - 1];
            int size = 1;
            if (ch >= '0' && ch <= '9')
            {
                // Match a \d+ segment
                while (true)
                {
                    pos -= size;
                    if (pos <= 0)
                    {
                        break;
                    }
                    ch = fileName[pos - 1];
                    size = 1;
                    if (ch < '0' || ch > '9')
                    {
                        break;
                    }
                }
            }
            else if (pos > 4 && (ch == 'n' || ch == 'N'))
            {
                // Looking for "min" or "min"
                // Already matched the 'n'
                pos -= size;
                if (pos <= 0)
                {
                    break;
                }
                ch = fileName[pos - 1];
                size = 1;
                if (ch != 'i' && ch != 'I')
                {
                    break;
                }
                pos -= size;
                if (pos <= 0)
                {
                    break;
                }
                ch = fileName[pos - 1];
                size = 1;
                if (ch != 'm' && ch != 'M')
                {
                    break;
                }
                pos -= size;
                if (pos > 0)
                {
                    ch = fileName[pos - 1];
                    size = 1;
                }
            }
            else
            {
                // This character is not part of either suffix pattern
                break;
            }

            if (ch != '-' && ch != '.')
            {
                break;
            }
            pos -= size;
            end = pos;
        }
        return fileName[0..end];
    }

    // Go %v of string slices — log output only.
    internal static string FormatList(IEnumerable<string> values)
    {
        return "[" + string.Join(" ", values) + "]";
    }

    // slices.Compact — adjacent dedupe of an already-sorted slice.
    private static string[] SortedCompact(string[] sorted)
    {
        if (sorted.Length == 0)
        {
            return sorted;
        }
        var result = new List<string>(sorted.Length) { sorted[0] };
        for (int i = 1; i < sorted.Length; i++)
        {
            if (!string.Equals(sorted[i], result[^1], StringComparison.Ordinal))
            {
                result.Add(sorted[i]);
            }
        }
        return result.ToArray();
    }
}

