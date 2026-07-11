// Ported from tsgo internal/tsoptions/parsedcommandline.go
//
// SCOPE (phase 4): the option-carrying structure + the accessors reachable from
// ParseCommandLine are ported. Methods that require the not-yet-available packages
// (outputpaths, glob, module, locale) are stubbed with NotSupportedException and a PORT-TODO.
// PORT-NOTE: Go's compile-time interface assertions (`_ module.ResolvedProjectReference`,
// `_ outputpaths.OutputPathsHost`) are dropped (05-conventions §1.3); those interfaces live in
// packages ported by parallel agents.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using TsPathApi = Ts.TsPath.TsPath;
using Path = Ts.TsPath.Path;

namespace Ts.TsOptions;

public sealed class SourceOutputAndProjectReference
{
    public string Source = "";
    public string OutputDts = "";
    public ParsedCommandLine? Resolved;
}

public sealed class ParsedCommandLine
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

    // --- Deferred: require outputpaths / glob / module / locale (parallel waves) ---

    public string CommonSourceDirectory()
        => throw new NotSupportedException("ParsedCommandLine.CommonSourceDirectory: pending outputpaths (phase 4 parallel).");

    public IEnumerable<string> GetOutputFileNames()
        => throw new NotSupportedException("ParsedCommandLine.GetOutputFileNames: pending outputpaths (phase 4 parallel).");

    public string GetBuildInfoFileName()
        => throw new NotSupportedException("ParsedCommandLine.GetBuildInfoFileName: pending outputpaths (phase 4 parallel).");

    public bool PossiblyMatchesFileName(string fileName)
        => throw new NotSupportedException("ParsedCommandLine.PossiblyMatchesFileName: pending glob (phase 4 parallel).");

    public void ParseInputOutputNames()
        => throw new NotSupportedException("ParsedCommandLine.ParseInputOutputNames: pending outputpaths (phase 4 parallel).");

    public ParsedCommandLine ReloadFileNamesOfParsedCommandLine(global::Ts.Vfs.IFileSystem fs)
        => throw new NotSupportedException("ParsedCommandLine.ReloadFileNamesOfParsedCommandLine: pending getFileNamesFromConfigSpecs (phase 4 breadth).");
}

// Null-tolerant helper for foreach over a possibly-null Dictionary (Go `range nil` = 0 iters).
internal static class ParsedCommandLineExtensions
{
    public static IEnumerable<KeyValuePair<K, V>> OrEmptyDict<K, V>(this Dictionary<K, V>? d) where K : notnull
        => d ?? Enumerable.Empty<KeyValuePair<K, V>>();
}
