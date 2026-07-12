// Ported from tsgo internal/tsoptions/parsedcommandline.go
//
// The option-carrying structure + the ParsedCommandLine accessors. The output-path / wildcard
// accessors (CommonSourceDirectory / GetOutputFileNames / GetBuildInfoFileName /
// PossiblyMatchesFileName / ParseInputOutputNames / ReloadFileNamesOfParsedCommandLine) are ported
// against outputpaths (Ts.OutputPaths) + glob (Ts.Glob) + vfsmatch; ParsedCommandLine implements
// outputpaths.OutputPathsHost. Only Locale() (locale package, phase 9) remains a PORT-TODO.
// PORT-NOTE: Go's compile-time interface assertions (`_ module.ResolvedProjectReference`,
// `_ outputpaths.OutputPathsHost`) are dropped (05-conventions §1.3).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using Ts.Vfs;
using Diag = Ts.Diagnostics.Diagnostics;
using TsPathApi = Ts.TsPath.TsPath;
using Path = Ts.TsPath.Path;
using OutputPathsApi = Ts.OutputPaths.OutputPaths;
using OutputPathsHost = Ts.OutputPaths.OutputPathsHost;
using GlobApi = Ts.Glob.Glob;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch;

namespace Ts.TsOptions;

public sealed class SourceOutputAndProjectReference
{
    public string Source = "";
    public string OutputDts = "";
    public ParsedCommandLine? Resolved;
}

public sealed class ParsedCommandLine : OutputPathsHost
{
    public ParsedOptions ParsedConfig;

    public TsConfigSourceFile? ConfigFile; // used in Program and ExecuteCommandLine
    public Diagnostic[]? Errors;
    public object? Raw;
    public bool? CompileOnSave;

    internal ComparePathsOptions comparePathsOptions;

    // PORT-NOTE: Go `sync.Once` memoization → single-shot guarded fields. Thread-safety is
    // deferred to when this runs off the single compilation large-stack thread (plan/08).
    private bool wildcardDirectoriesComputed;
    private Dictionary<string, bool>? wildcardDirectories;

    private bool includeGlobsComputed;
    private GlobApi[]? includeGlobs;

    private bool commonSourceDirectoryComputed;
    private string commonSourceDirectory = "";

    private bool sourceAndOutputMapsComputed;
    private Dictionary<Path, SourceOutputAndProjectReference>? sourceToProjectReference;
    private Dictionary<Path, SourceOutputAndProjectReference>? outputDtsToProjectReference;

    internal FileExtensionInfo[]? extraFileExtensions;

    private bool fileNamesByPathComputed;
    private Dictionary<Path, string>? fileNamesByPath;

    private bool resolvedProjectReferencePathsComputed;
    private string[]? resolvedProjectReferencePaths;

    internal int literalFileNamesLen;

    internal ParsedCommandLine(ParsedOptions parsedConfig, ComparePathsOptions comparePathsOptions)
    {
        ParsedConfig = parsedConfig;
        this.comparePathsOptions = comparePathsOptions;
    }

    public static ParsedCommandLine NewParsedCommandLine(
        CompilerOptions compilerOptions,
        string[] rootFileNames,
        ComparePathsOptions comparePathsOptions)
    {
        return new ParsedCommandLine(
            new ParsedOptions
            {
                CompilerOptions = compilerOptions,
                FileNames = rootFileNames,
            },
            comparePathsOptions);
    }

    public string ConfigName()
    {
        if (ConfigFile is null)
        {
            return "";
        }
        return ConfigFile.SourceFile.FileName();
    }

    public string GetCurrentDirectory()
    {
        return comparePathsOptions.CurrentDirectory;
    }

    public bool UseCaseSensitiveFileNames()
    {
        return comparePathsOptions.UseCaseSensitiveFileNames;
    }

    // WildcardDirectories returns the cached wildcard directories, initializing them if needed
    public Dictionary<string, bool>? WildcardDirectories()
    {
        if (!wildcardDirectoriesComputed)
        {
            wildcardDirectoriesComputed = true;
            wildcardDirectories ??= WildcardDirectories_getWildcardDirectories();
        }
        return wildcardDirectories;
    }

    private Dictionary<string, bool>? WildcardDirectories_getWildcardDirectories()
    {
        return TsOptions.WildcardDirectories.getWildcardDirectories(
            ConfigFile!.configFileSpecs!.validatedIncludeSpecs,
            ConfigFile.configFileSpecs.validatedExcludeSpecs,
            comparePathsOptions);
    }

    // Normalized file names explicitly specified in `files`
    public string[]? LiteralFileNames()
    {
        if (ConfigFile != null)
        {
            return FileNames()![0..literalFileNamesLen]; // ConfigFile != null ⇒ fileNames populated (Go invariant)
        }
        return null;
    }

    public void SetParsedOptions(ParsedOptions o)
    {
        ParsedConfig = o;
    }

    public void SetCompilerOptions(CompilerOptions o)
    {
        ParsedConfig.CompilerOptions = o;
    }

    public CompilerOptions? CompilerOptions()
    {
        return ParsedConfig.CompilerOptions;
    }

    public void SetTypeAcquisition(TypeAcquisition o)
    {
        ParsedConfig.TypeAcquisition = o;
    }

    public TypeAcquisition? TypeAcquisition()
    {
        return ParsedConfig.TypeAcquisition;
    }

    // All file names matched by files, include, and exclude patterns
    public string[]? FileNames()
    {
        return ParsedConfig.FileNames;
    }

    public Dictionary<Path, string> FileNamesByPath()
    {
        if (!fileNamesByPathComputed)
        {
            fileNamesByPathComputed = true;
            var map = new Dictionary<Path, string>(ParsedConfig.FileNames.Len());
            foreach (var fileName in ParsedConfig.FileNames.OrEmpty())
            {
                var path = TsPathApi.ToPath(fileName, GetCurrentDirectory(), UseCaseSensitiveFileNames());
                map[path] = fileName;
            }
            fileNamesByPath = map;
        }
        return fileNamesByPath!;
    }

    public ProjectReference[]? ProjectReferences()
    {
        return ParsedConfig.ProjectReferences;
    }

    public string[]? ResolvedProjectReferencePaths()
    {
        if (!resolvedProjectReferencePathsComputed)
        {
            resolvedProjectReferencePathsComputed = true;
            resolvedProjectReferencePaths = CoreUtil.Map(ParsedConfig.ProjectReferences, global::Ts.Core.Core.ResolveProjectReferencePath);
        }
        return resolvedProjectReferencePaths;
    }

    public string[]? ExtendedSourceFiles()
    {
        if (ConfigFile is null)
        {
            return null;
        }
        return ConfigFile.ExtendedSourceFiles;
    }

    public Diagnostic[]? GetConfigFileParsingDiagnostics()
    {
        if (ConfigFile != null)
        {
            // todo: !!! should be ConfigFile.ParseDiagnostics, check if they are the same
            return ConfigFile.SourceFile.Diagnostics().OrEmpty().Concat(Errors.OrEmpty()).ToArray();
        }
        return Errors;
    }

    public string GetMatchedFileSpec(string fileName)
    {
        return ConfigFile!.configFileSpecs!.getMatchedFileSpec(fileName, comparePathsOptions);
    }

    public (string spec, bool ok) GetMatchedIncludeSpec(string fileName)
    {
        if (ConfigFile!.configFileSpecs!.validatedIncludeSpecs.Len() == 0)
        {
            return ("", false);
        }

        if (ConfigFile.configFileSpecs.isDefaultIncludeSpec)
        {
            return (ConfigFile.configFileSpecs.validatedIncludeSpecs![0], true);
        }

        return (ConfigFile.configFileSpecs.getMatchedIncludeSpec(fileName, comparePathsOptions), false);
    }

    public bool PossiblyMatchesDirectoryName(Path directoryPath)
    {
        // map-iter: 序无关 —— 命中任一通配目录即返回，与顺序无关
        foreach (var (wildcardDir, recursive) in WildcardDirectories().OrEmptyDict())
        {
            var wildcardDirPath = TsPathApi.ToPath(wildcardDir, GetCurrentDirectory(), UseCaseSensitiveFileNames());
            if (recursive)
            {
                if (wildcardDirPath.ContainsPath(directoryPath))
                {
                    return true;
                }
            }
            else
            {
                if (wildcardDirPath == directoryPath)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // --- outputpaths / glob backed accessors ---

    public Dictionary<Path, SourceOutputAndProjectReference> SourceToProjectReference()
    {
        ParseInputOutputNames();
        return sourceToProjectReference!;
    }

    public Dictionary<Path, SourceOutputAndProjectReference> OutputDtsToProjectReference()
    {
        ParseInputOutputNames();
        return outputDtsToProjectReference!;
    }

    public void ParseInputOutputNames()
    {
        if (!sourceAndOutputMapsComputed)
        {
            sourceAndOutputMapsComputed = true;
            var sourceToOutput = new Dictionary<Path, SourceOutputAndProjectReference>();
            var outputDtsToSource = new Dictionary<Path, SourceOutputAndProjectReference>();

            foreach (var (outputDts, source) in getOutputDeclarationAndSourceFileNames())
            {
                var path = TsPathApi.ToPath(source, GetCurrentDirectory(), UseCaseSensitiveFileNames());
                var projectReference = new SourceOutputAndProjectReference
                {
                    Source = source,
                    OutputDts = outputDts,
                    Resolved = this,
                };
                if (outputDts != "")
                {
                    outputDtsToSource[TsPathApi.ToPath(outputDts, GetCurrentDirectory(), UseCaseSensitiveFileNames())] = projectReference;
                }
                sourceToOutput[path] = projectReference;
            }
            outputDtsToProjectReference = outputDtsToSource;
            sourceToProjectReference = sourceToOutput;
        }
    }

    public string CommonSourceDirectory()
    {
        if (!commonSourceDirectoryComputed)
        {
            commonSourceDirectoryComputed = true;
            string[] files() => CoreUtil.Filter(ParsedConfig.FileNames, file =>
                !(ParsedConfig.CompilerOptions!.NoEmitForJsFiles.IsTrue() && TsPathApi.HasJSFileExtension(file)) && !TsPathApi.IsDeclarationFileName(file)).OrEmpty();

            commonSourceDirectory = OutputPathsApi.GetCommonSourceDirectory(
                ParsedConfig.CompilerOptions!,
                files,
                GetCurrentDirectory(),
                UseCaseSensitiveFileNames(),
                checkSourceFilesBelongToPath);
        }
        return commonSourceDirectory;
    }

    private bool checkSourceFilesBelongToPath(string[] sourceFiles, string rootDirectory)
    {
        var allFilesBelongToPath = true;
        foreach (var file in sourceFiles)
        {
            var absoluteSourceFilePath = TsPathApi.GetCanonicalFileName(TsPathApi.GetNormalizedAbsolutePath(file, GetCurrentDirectory()), UseCaseSensitiveFileNames());
            if (!TsPathApi.ContainsPath(rootDirectory, file, comparePathsOptions))
            {
                Errors = Errors.OrEmpty().Append(Diagnostic.NewCompilerDiagnostic(Diag.File_0_is_not_under_rootDir_1_rootDir_is_expected_to_contain_all_source_files, absoluteSourceFilePath, rootDirectory)).ToArray();
                allFilesBelongToPath = false;
            }
        }
        return allFilesBelongToPath;
    }

    private IEnumerable<(string dtsName, string inputName)> getOutputDeclarationAndSourceFileNames()
    {
        foreach (var fileName in ParsedConfig.FileNames.OrEmpty())
        {
            var outputDts = "";
            if (!TsPathApi.IsDeclarationFileName(fileName) && !TsPathApi.FileExtensionIs(fileName, TsPathApi.ExtensionJson))
            {
                outputDts = OutputPathsApi.GetOutputDeclarationFileNameWorker(fileName, CompilerOptions()!, this);
            }
            yield return (outputDts, fileName);
        }
    }

    public IEnumerable<string> GetOutputFileNames()
    {
        foreach (var fileName in ParsedConfig.FileNames.OrEmpty())
        {
            if (TsPathApi.IsDeclarationFileName(fileName))
            {
                continue;
            }
            var jsFileName = OutputPathsApi.GetOutputJSFileName(fileName, CompilerOptions()!, this);
            var isJson = TsPathApi.FileExtensionIs(fileName, TsPathApi.ExtensionJson);
            if (jsFileName != "")
            {
                yield return jsFileName;
                if (!isJson)
                {
                    var sourceMap = OutputPathsApi.GetSourceMapFilePath(jsFileName, CompilerOptions()!);
                    if (sourceMap != "")
                    {
                        yield return sourceMap;
                    }
                }
            }
            if (isJson)
            {
                continue;
            }
            if (CompilerOptions()!.GetEmitDeclarations())
            {
                var dtsFileName = OutputPathsApi.GetOutputDeclarationFileNameWorker(fileName, CompilerOptions()!, this);
                if (dtsFileName != "")
                {
                    yield return dtsFileName;
                    if (CompilerOptions()!.GetAreDeclarationMapsEnabled())
                    {
                        var declarationMap = dtsFileName + ".map";
                        yield return declarationMap;
                    }
                }
            }
        }
    }

    public string GetBuildInfoFileName()
    {
        return OutputPathsApi.GetBuildInfoFileName(CompilerOptions()!, comparePathsOptions);
    }

    // WildcardDirectoryGlobs returns the cached wildcard-directory globs, initializing them if needed
    public GlobApi[]? WildcardDirectoryGlobs()
    {
        var wildcardDirectories = WildcardDirectories();
        if (wildcardDirectories is null)
        {
            return null;
        }

        if (!includeGlobsComputed)
        {
            includeGlobsComputed = true;
            if (includeGlobs is null)
            {
                var globs = new List<GlobApi>(wildcardDirectories.Count);
                // map-iter: 序无关 —— glob set membership; PossiblyMatchesFileName ORs over all
                foreach (var (dir, recursive) in wildcardDirectories)
                {
                    // Go: glob.Parse(...); on parse error (err != nil) the entry is skipped.
                    try
                    {
                        var parsed = GlobApi.Parse($"{TsPathApi.NormalizePath(dir)}/{CoreUtil.IfElse(recursive, recursiveFileGlobPattern, fileGlobPattern)}");
                        globs.Add(parsed);
                    }
                    catch (FormatException)
                    {
                        // Go swallows glob.Parse errors (err == nil guard); skip this dir.
                    }
                }
                includeGlobs = globs.ToArray();
            }
        }

        return includeGlobs;
    }

    // PossiblyMatchesFileName is a fast check to see if a file is currently included by a config
    // or would be included if the file were to be created. It may return false positives.
    public bool PossiblyMatchesFileName(string fileName)
    {
        var path = TsPathApi.ToPath(fileName, GetCurrentDirectory(), UseCaseSensitiveFileNames());
        if (FileNamesByPath().ContainsKey(path))
        {
            return true;
        }

        foreach (var include in ConfigFile!.configFileSpecs!.validatedIncludeSpecs.OrEmpty())
        {
            if (!include.Any(c => c == '*' || c == '?') && !VfsMatchApi.IsImplicitGlob(include))
            {
                var includePath = TsPathApi.ToPath(include, GetCurrentDirectory(), UseCaseSensitiveFileNames());
                if (includePath == path)
                {
                    return true;
                }
            }
        }
        var wildcardDirectoryGlobs = WildcardDirectoryGlobs();
        if (wildcardDirectoryGlobs.Len() > 0)
        {
            foreach (var glob in wildcardDirectoryGlobs!)
            {
                if (glob.Match(fileName))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public ParsedCommandLine ReloadFileNamesOfParsedCommandLine(IFileSystem fs)
    {
        // Go: parsedConfig := *p.ParsedConfig — a shallow struct copy. ParsedOptions is a C# class,
        // so replicate the field-by-field shallow copy.
        var parsedConfig = new ParsedOptions
        {
            CompilerOptions = ParsedConfig.CompilerOptions,
            WatchOptions = ParsedConfig.WatchOptions,
            TypeAcquisition = ParsedConfig.TypeAcquisition,
            FileNames = ParsedConfig.FileNames,
            ProjectReferences = ParsedConfig.ProjectReferences,
        };
        var (fileNames, literalFileNamesLen) = TsConfigParsing.getFileNamesFromConfigSpecs(
            ConfigFile!.configFileSpecs!,
            GetCurrentDirectory(),
            CompilerOptions(),
            fs,
            extraFileExtensions);
        parsedConfig.FileNames = fileNames;
        var parsedCommandLine = new ParsedCommandLine(parsedConfig, comparePathsOptions)
        {
            ConfigFile = ConfigFile,
            Errors = Errors,
            Raw = Raw,
            CompileOnSave = CompileOnSave,
            wildcardDirectories = wildcardDirectories,
            wildcardDirectoriesComputed = wildcardDirectories != null,
            includeGlobs = includeGlobs,
            includeGlobsComputed = includeGlobs != null,
            extraFileExtensions = extraFileExtensions,
            literalFileNamesLen = literalFileNamesLen,
        };
        return parsedCommandLine;
    }

    // Go module-level: fileGlobPattern / recursiveFileGlobPattern (parsedcommandline.go:23-24)
    private const string fileGlobPattern = "*.{js,jsx,mjs,cjs,ts,tsx,mts,cts,json}";
    private const string recursiveFileGlobPattern = "**/*.{js,jsx,mjs,cjs,ts,tsx,mts,cts,json}";
}

// Null-tolerant helper for foreach over a possibly-null Dictionary (Go `range nil` = 0 iters).
internal static class ParsedCommandLineExtensions
{
    public static IEnumerable<KeyValuePair<K, V>> OrEmptyDict<K, V>(this Dictionary<K, V>? d) where K : notnull
        => d ?? Enumerable.Empty<KeyValuePair<K, V>>();
}
