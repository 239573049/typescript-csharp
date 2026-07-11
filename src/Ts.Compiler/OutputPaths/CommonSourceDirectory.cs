// Ported from tsgo internal/outputpaths/commonsourcedirectory.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using Ts.Core;
using Ts.TsPath;

namespace Ts.OutputPaths;

public sealed partial class OutputPaths
{
    private static string ComputeCommonSourceDirectoryOfFilenames(string[] fileNames, string currentDirectory, bool useCaseSensitiveFileNames)
    {
        List<string>? commonPathComponents = null;
        foreach (string sourceFile in fileNames)
        {
            // Each file contributes into common source file path
            List<string> sourcePathComponents = global::Ts.TsPath.TsPath.GetNormalizedPathComponents(sourceFile, currentDirectory);

            // The base file name is not part of the common directory path
            sourcePathComponents = sourcePathComponents.GetRange(0, sourcePathComponents.Count - 1);

            if (commonPathComponents == null)
            {
                // first file
                commonPathComponents = sourcePathComponents;
                continue;
            }

            int n = Math.Min(commonPathComponents.Count, sourcePathComponents.Count);
            for (int i = 0; i < n; i++)
            {
                if (global::Ts.TsPath.TsPath.GetCanonicalFileName(commonPathComponents[i], useCaseSensitiveFileNames) != global::Ts.TsPath.TsPath.GetCanonicalFileName(sourcePathComponents[i], useCaseSensitiveFileNames))
                {
                    if (i == 0)
                    {
                        // Failed to find any common path component
                        return "";
                    }

                    // New common path found that is 0 -> i-1
                    commonPathComponents = commonPathComponents.GetRange(0, i);
                    break;
                }
            }

            // If the sourcePathComponents was shorter than the commonPathComponents, truncate to the sourcePathComponents
            if (sourcePathComponents.Count < commonPathComponents.Count)
            {
                commonPathComponents = commonPathComponents.GetRange(0, sourcePathComponents.Count);
            }
        }

        if (commonPathComponents == null || commonPathComponents.Count == 0)
        {
            // Can happen when all input files are .d.ts files
            return currentDirectory;
        }

        return global::Ts.TsPath.TsPath.GetPathFromPathComponents(commonPathComponents);
    }

    public static string GetComputedCommonSourceDirectory(string[] emittedFiles, string currentDirectory, bool useCaseSensitiveFileNames)
    {
        string commonSourceDirectory = ComputeCommonSourceDirectoryOfFilenames(emittedFiles, currentDirectory, useCaseSensitiveFileNames);
        if (commonSourceDirectory.Length > 0)
        {
            commonSourceDirectory = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(commonSourceDirectory);
        }
        return commonSourceDirectory;
    }

    public static string GetCommonSourceDirectory(CompilerOptions options, Func<string[]> files, string currentDirectory, bool useCaseSensitiveFileNames, Func<string[], string, bool>? checkSourceFilesBelongToPath)
    {
        string commonSourceDirectory;
        if (options.RootDir != "")
        {
            // If a rootDir is specified use it as the commonSourceDirectory
            commonSourceDirectory = options.RootDir;
            if (checkSourceFilesBelongToPath != null)
            {
                checkSourceFilesBelongToPath(files(), options.RootDir);
            }
        }
        else if (options.ConfigFilePath != "")
        {
            // If the rootDir is not specified, then the common source directory is the directory of the config file.
            commonSourceDirectory = global::Ts.TsPath.TsPath.GetDirectoryPath(options.ConfigFilePath);
            if (checkSourceFilesBelongToPath != null)
            {
                checkSourceFilesBelongToPath(files(), commonSourceDirectory);
            }
        }
        else
        {
            commonSourceDirectory = ComputeCommonSourceDirectoryOfFilenames(files(), currentDirectory, useCaseSensitiveFileNames);
        }

        if (commonSourceDirectory.Length > 0)
        {
            // Make sure directory path ends with directory separator so this string can directly
            // used to replace with "" to get the relative path of the source file and the relative path doesn't
            // start with / making it rooted path
            commonSourceDirectory = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(commonSourceDirectory);
        }

        return commonSourceDirectory;
    }
}
