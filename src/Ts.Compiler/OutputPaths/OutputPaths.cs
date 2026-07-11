// Ported from tsgo internal/outputpaths/outputpaths.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using Ts.Ast;
using Ts.Core;
using Ts.TsPath;

namespace Ts.OutputPaths;

// PORT-NOTE: package `outputpaths` maps to namespace `Ts.OutputPaths`; the data type
// `OutputPaths` and the package-level functions share one partial class (mirroring the
// `Ts.TsPath`/`TsPath` precedent, 05-conventions.md §1.5). External callers must use
// `global::Ts.OutputPaths.OutputPaths.X` because the class name equals the namespace tail.

public interface OutputPathsHost
{
    string CommonSourceDirectory();
    string GetCurrentDirectory();
    bool UseCaseSensitiveFileNames();
}

public sealed partial class OutputPaths
{
    // Go zero-value strings are ""; keep the same so unset paths read as "" (not null).
    private string jsFilePath = "";
    private string sourceMapFilePath = "";
    private string declarationFilePath = "";
    private string declarationMapPath = "";

    // DeclarationFilePath implements declarations.OutputPaths.
    public string DeclarationFilePath()
    {
        return declarationFilePath;
    }

    // JsFilePath implements declarations.OutputPaths.
    public string JsFilePath()
    {
        return jsFilePath;
    }

    public string SourceMapFilePath()
    {
        return sourceMapFilePath;
    }

    public string DeclarationMapPath()
    {
        return declarationMapPath;
    }

    public static OutputPaths GetOutputPathsFor(SourceFile sourceFile, CompilerOptions options, OutputPathsHost host, bool forceDtsEmit)
    {
        string ownOutputFilePath = GetOwnEmitOutputFilePath(sourceFile.FileName(), options, host, GetOutputExtension(sourceFile.FileName(), options.Jsx));
        bool isJsonFile = AstUtilities.IsJsonSourceFile(sourceFile);
        // If json file emits to the same location skip writing it, if emitDeclarationOnly skip writing it
        bool isJsonEmittedToSameLocation = isJsonFile &&
            global::Ts.TsPath.TsPath.ComparePaths(sourceFile.FileName(), ownOutputFilePath, new ComparePathsOptions
            {
                CurrentDirectory = host.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
            }) == 0;
        var paths = new OutputPaths();
        if (options.EmitDeclarationOnly != Tristate.True && !isJsonEmittedToSameLocation)
        {
            paths.jsFilePath = ownOutputFilePath;
            if (!AstUtilities.IsJsonSourceFile(sourceFile))
            {
                paths.sourceMapFilePath = GetSourceMapFilePath(paths.jsFilePath, options);
            }
        }
        if (forceDtsEmit || options.GetEmitDeclarations() && !isJsonFile)
        {
            paths.declarationFilePath = GetDeclarationEmitOutputFilePath(sourceFile.FileName(), options, host);
            if (options.GetAreDeclarationMapsEnabled())
            {
                paths.declarationMapPath = paths.declarationFilePath + ".map";
            }
        }
        return paths;
    }

    public static bool ForEachEmittedFile(OutputPathsHost host, CompilerOptions options, Func<OutputPaths, SourceFile, bool> action, SourceFile[] sourceFiles, bool forceDtsEmit)
    {
        foreach (SourceFile sourceFile in sourceFiles)
        {
            if (action(GetOutputPathsFor(sourceFile, options, host, forceDtsEmit), sourceFile))
            {
                return true;
            }
        }
        return false;
    }

    public static string GetOutputJSFileName(string inputFileName, CompilerOptions options, OutputPathsHost host)
    {
        if (options.EmitDeclarationOnly.IsTrue())
        {
            return "";
        }
        string outputFileName = GetOutputJSFileNameWorker(inputFileName, options, host);
        if (!global::Ts.TsPath.TsPath.FileExtensionIs(outputFileName, global::Ts.TsPath.TsPath.ExtensionJson) ||
            global::Ts.TsPath.TsPath.ComparePaths(inputFileName, outputFileName, new ComparePathsOptions
            {
                CurrentDirectory = host.GetCurrentDirectory(),
                UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
            }) != 0)
        {
            return outputFileName;
        }
        return "";
    }

    public static string GetOutputJSFileNameWorker(string inputFileName, CompilerOptions options, OutputPathsHost host)
    {
        return global::Ts.TsPath.TsPath.ChangeExtension(
            GetOutputPathWithoutChangingExtension(inputFileName, options.OutDir, host),
            GetOutputExtension(inputFileName, options.Jsx));
    }

    public static string GetOutputDeclarationFileNameWorker(string inputFileName, CompilerOptions options, OutputPathsHost host)
    {
        string dir = options.DeclarationDir;
        if (dir.Length == 0)
        {
            dir = options.OutDir;
        }
        return global::Ts.TsPath.TsPath.ChangeExtension(
            GetOutputPathWithoutChangingExtension(inputFileName, dir, host),
            global::Ts.TsPath.TsPath.GetDeclarationEmitExtensionForPath(inputFileName));
    }

    public static string GetOutputExtension(string fileName, JsxEmit jsx)
    {
        if (global::Ts.TsPath.TsPath.FileExtensionIs(fileName, global::Ts.TsPath.TsPath.ExtensionJson))
        {
            return global::Ts.TsPath.TsPath.ExtensionJson;
        }
        if (jsx == JsxEmit.Preserve && global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, new[] { global::Ts.TsPath.TsPath.ExtensionJsx, global::Ts.TsPath.TsPath.ExtensionTsx }))
        {
            return global::Ts.TsPath.TsPath.ExtensionJsx;
        }
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, new[] { global::Ts.TsPath.TsPath.ExtensionMts, global::Ts.TsPath.TsPath.ExtensionMjs }))
        {
            return global::Ts.TsPath.TsPath.ExtensionMjs;
        }
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, new[] { global::Ts.TsPath.TsPath.ExtensionCts, global::Ts.TsPath.TsPath.ExtensionCjs }))
        {
            return global::Ts.TsPath.TsPath.ExtensionCjs;
        }
        return global::Ts.TsPath.TsPath.ExtensionJs;
    }

    public static string GetDeclarationEmitOutputFilePath(string file, CompilerOptions options, OutputPathsHost host)
    {
        // PORT-NOTE: Go uses `*string` to distinguish "set" from empty; C# uses a nullable
        // string where null means "no output dir" (empty is never assigned, guarded by Length>0).
        string? outputDir = null;
        if (options.DeclarationDir.Length > 0)
        {
            outputDir = options.DeclarationDir;
        }
        else if (options.OutDir.Length > 0)
        {
            outputDir = options.OutDir;
        }

        string path;
        if (outputDir != null)
        {
            path = GetSourceFilePathInNewDirWorker(file, outputDir, host.GetCurrentDirectory(), host.CommonSourceDirectory(), host.UseCaseSensitiveFileNames());
        }
        else
        {
            path = file;
        }
        string declarationExtension = global::Ts.TsPath.TsPath.GetDeclarationEmitExtensionForPath(path);
        return global::Ts.TsPath.TsPath.RemoveFileExtension(path) + declarationExtension;
    }

    public static string GetSourceFilePathInNewDir(string fileName, string newDirPath, string currentDirectory, string commonSourceDirectory, bool useCaseSensitiveFileNames)
    {
        string sourceFilePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, currentDirectory);
        commonSourceDirectory = global::Ts.TsPath.TsPath.EnsureTrailingDirectorySeparator(commonSourceDirectory);
        bool isSourceFileInCommonSourceDirectory = global::Ts.TsPath.TsPath.ContainsPath(commonSourceDirectory, sourceFilePath, new ComparePathsOptions
        {
            UseCaseSensitiveFileNames = useCaseSensitiveFileNames,
            CurrentDirectory = currentDirectory,
        });
        if (isSourceFileInCommonSourceDirectory)
        {
            sourceFilePath = sourceFilePath.Substring(commonSourceDirectory.Length);
        }
        return global::Ts.TsPath.TsPath.CombinePaths(newDirPath, sourceFilePath);
    }

    private static string GetOutputPathWithoutChangingExtension(string inputFileName, string outputDirectory, OutputPathsHost host)
    {
        if (outputDirectory.Length > 0)
        {
            return global::Ts.TsPath.TsPath.ResolvePath(outputDirectory, global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(host.CommonSourceDirectory(), inputFileName, new ComparePathsOptions
            {
                UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
                CurrentDirectory = host.GetCurrentDirectory(),
            }));
        }
        return inputFileName;
    }

    public static string GetSourceFilePathInNewDirWorker(string fileName, string newDirPath, string currentDirectory, string commonSourceDirectory, bool useCaseSensitiveFileNames)
    {
        string sourceFilePath = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, currentDirectory);
        string commonDir = global::Ts.TsPath.TsPath.GetCanonicalFileName(commonSourceDirectory, useCaseSensitiveFileNames);
        string canonFile = global::Ts.TsPath.TsPath.GetCanonicalFileName(sourceFilePath, useCaseSensitiveFileNames);
        bool isSourceFileInCommonSourceDirectory = canonFile.StartsWith(commonDir, StringComparison.Ordinal);
        if (isSourceFileInCommonSourceDirectory)
        {
            sourceFilePath = sourceFilePath.Substring(commonSourceDirectory.Length);
        }
        return global::Ts.TsPath.TsPath.CombinePaths(newDirPath, sourceFilePath);
    }

    private static string GetOwnEmitOutputFilePath(string fileName, CompilerOptions options, OutputPathsHost host, string extension)
    {
        string emitOutputFilePathWithoutExtension;
        if (options.OutDir.Length > 0)
        {
            string currentDirectory = host.GetCurrentDirectory();
            emitOutputFilePathWithoutExtension = global::Ts.TsPath.TsPath.RemoveFileExtension(GetSourceFilePathInNewDir(
                fileName,
                options.OutDir,
                currentDirectory,
                host.CommonSourceDirectory(),
                host.UseCaseSensitiveFileNames()));
        }
        else
        {
            emitOutputFilePathWithoutExtension = global::Ts.TsPath.TsPath.RemoveFileExtension(fileName);
        }
        return emitOutputFilePathWithoutExtension + extension;
    }

    public static string GetSourceMapFilePath(string jsFilePath, CompilerOptions options)
    {
        if (options.SourceMap.IsTrue() && !options.InlineSourceMap.IsTrue())
        {
            return jsFilePath + ".map";
        }
        return "";
    }

    public static string GetBuildInfoFileName(CompilerOptions options, ComparePathsOptions opts)
    {
        if (!options.IsIncremental() && !options.Build.IsTrue())
        {
            return "";
        }
        if (options.TsBuildInfoFile != "")
        {
            return options.TsBuildInfoFile;
        }
        if (options.ConfigFilePath == "")
        {
            return "";
        }
        string configFileExtensionLess = global::Ts.TsPath.TsPath.RemoveFileExtension(options.ConfigFilePath);
        string buildInfoExtensionLess;
        if (options.OutDir != "")
        {
            if (options.RootDir != "")
            {
                buildInfoExtensionLess = global::Ts.TsPath.TsPath.ResolvePath(options.OutDir, global::Ts.TsPath.TsPath.GetRelativePathFromDirectory(options.RootDir, configFileExtensionLess, opts));
            }
            else
            {
                buildInfoExtensionLess = global::Ts.TsPath.TsPath.CombinePaths(options.OutDir, global::Ts.TsPath.TsPath.GetBaseFileName(configFileExtensionLess));
            }
        }
        else
        {
            buildInfoExtensionLess = configFileExtensionLess;
        }
        return buildInfoExtensionLess + global::Ts.TsPath.TsPath.ExtensionTsBuildInfo;
    }
}
