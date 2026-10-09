// Ported from tsgo internal/tsoptions/tsconfigparsing.go
//
// This file carries the self-contained option-conversion + name-map machinery
// (CommandLineOptionNameMap, commandLineOptionsToMap, CommandLineCompilerOptionsMap,
// convertMapToOptions, convertOptionsFromJson, isCompilerOptionsValue, validateJsonOptionValue,
// convertJsonOption(+list/enum), normalizeNonListOptionValue, startsWithConfigDirTemplate,
// specToDiagnostic + spec validators, GetSupportedExtensions(+WithJson), ParseConfigHost,
// FileExtensionInfo, configFileSpecs, ExtendedConfigCache{,Entry}, TsConfigSourceFile).
//
// The tsconfig.json LOADING slice — the JSON-source-file → object walk, parseConfig, the worker,
// extends-chain resolution (getExtendedConfig → module.ResolveConfig), ${configDir} substitution,
// wildcard/file expansion (getFileNamesFromConfigSpecs), and the public entry points
// (ParseConfigFileTextToJson / ParseJsonConfigFileContent / ParseJsonSourceFileConfigFileContent /
// GetParsedCommandLineOfConfigFile{,Path}) — lives in the TsConfigParsing.Load.cs /
// TsConfigParsing.Worker.cs partials of this same partial class. Only the extraValidationLocale
// BCP-47 check inside validateJsonOptionValue remains a PORT-TODO (locale package, phase 9).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.TsPath;
using Ts.Vfs;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath;
using Path = Ts.TsPath.Path;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch;
using Usage = Ts.Vfs.VfsMatch.Usage;
using static Ts.TsOptions.CommandLineOptionKind;
using static Ts.TsOptions.Errors;

namespace Ts.TsOptions;

// Go: `type CommandLineOptionNameMap map[string]*CommandLineOption` with a `Get` receiver.
// PORT-NOTE: wrapped as a class rather than a bare Dictionary so the two-step (exact, then
// lowercased) Get lookup stays a method, matching tsconfigparsing.go:592-598.
public sealed class CommandLineOptionNameMap
{
    private readonly Dictionary<string, CommandLineOption> m;

    internal CommandLineOptionNameMap(Dictionary<string, CommandLineOption> m)
    {
        this.m = m;
    }

    public CommandLineOption? Get(string name)
    {
        if (!m.TryGetValue(name, out var opt))
        {
            m.TryGetValue(OptionsUtil.ToLower(name), out opt);
        }
        return opt;
    }
}

public sealed class FileExtensionInfo
{
    public string Extension = "";
    public bool IsMixedContent;
    public ScriptKind ScriptKind;
}

// Go: type ParseConfigHost interface { FS() vfs.FS; GetCurrentDirectory() string }
public interface ParseConfigHost
{
    IFileSystem FS();
    string GetCurrentDirectory();
}

public static partial class TsConfigParsing
{
    internal static CommandLineOptionNameMap commandLineOptionsToMap(CommandLineOption[] compilerOptions)
    {
        var result = new Dictionary<string, CommandLineOption>(compilerOptions.Length * 2, StringComparer.Ordinal);
        foreach (var opt in compilerOptions)
        {
            result[opt.Name] = opt;
            result[OptionsUtil.ToLower(opt.Name)] = opt;
        }
        return new CommandLineOptionNameMap(result);
    }

    public static readonly CommandLineOptionNameMap CommandLineCompilerOptionsMap = commandLineOptionsToMap(DeclsCompiler.OptionsDeclarations);

    internal static O convertMapToOptions<O>(OrderedMap<string, object?> compilerOptions, O result) where O : optionParser
    {
        // this assumes any `key`, `value` pair in `options` will have `value` already be the correct type. this function should no error handling
        // map-iter: 已排序 —— OrderedMap 保序，逐键 ParseOption
        foreach (var (key, value) in compilerOptions.Entries())
        {
            result.ParseOption(key, value);
        }
        return result;
    }

    internal static (O result, Diagnostic[]? errors) convertOptionsFromJson<O>(CommandLineOptionNameMap optionsNameMap, object? jsonOptions, string basePath, O result) where O : optionParser
    {
        if (jsonOptions is null)
        {
            return (result, null);
        }
        if (jsonOptions is not OrderedMap<string, object?> jsonMap)
        {
            // !!! probably should be an error
            return (result, null);
        }
        var errors = new List<Diagnostic>();
        // map-iter: 已排序 —— OrderedMap 保序
        foreach (var (key, value) in jsonMap.Entries())
        {
            var opt = optionsNameMap.Get(key);
            if (opt != null && opt.Name != key)
            {
                // Case-insensitive match found but exact case doesn't match - provide "did you mean" suggestion
                errors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(null, null, result.UnknownDidYouMeanDiagnostic()!, key, opt.Name));
                continue;
            }
            if (opt is null)
            {
                errors.Add(createUnknownOptionError(key, result.UnknownOptionDiagnostic(), "", null, null, null));
                continue;
            }

            var commandLineOptionEnumMapVal = opt.EnumMap();
            if (commandLineOptionEnumMapVal != null)
            {
                if (value is string strValue)
                {
                    if (commandLineOptionEnumMapVal.TryGet(OptionsUtil.ToLower(strValue), out var val))
                    {
                        var e = result.ParseOption(key, val);
                        if (e != null)
                        {
                            errors.AddRange(e);
                        }
                    }
                }
                else
                {
                    var (convertJson, err) = convertJsonOption(opt, value, basePath, null, null, null);
                    if (err != null)
                    {
                        errors.AddRange(err);
                    }
                    var compilerOptionsErr = result.ParseOption(key, convertJson);
                    if (compilerOptionsErr != null)
                    {
                        errors.AddRange(compilerOptionsErr);
                    }
                }
            }
            else
            {
                var (convertJson, err) = convertJsonOption(opt, value, basePath, null, null, null);
                if (err != null)
                {
                    errors.AddRange(err);
                }
                var compilerOptionsErr = result.ParseOption(key, convertJson);
                if (compilerOptionsErr != null)
                {
                    errors.AddRange(compilerOptionsErr);
                }
            }
        }
        return (result, errors.ToArray());
    }

    internal static bool isCompilerOptionsValue(CommandLineOption? option, object? value)
    {
        if (option != null)
        {
            if (value is null)
            {
                return !option.DisallowNullOrUndefined();
            }
            if (option.Kind == "list")
            {
                return value is object?[] or string[];
            }
            if (option.Kind == "listOrElement")
            {
                if (value is object?[] or string[])
                {
                    return true;
                }
                else
                {
                    return isCompilerOptionsValue(option.Elements(), value);
                }
            }
            if (option.Kind == "string")
            {
                return value is string;
            }
            if (option.Kind == "boolean")
            {
                return value is bool;
            }
            if (option.Kind == "number")
            {
                return value is double;
            }
            if (option.Kind == "object")
            {
                return value is OrderedMap<string, object?>;
            }
            if (option.Kind == "enum" && value is string)
            {
                return true;
            }
        }
        return false;
    }

    internal static (object? value, Diagnostic[]? errors) validateJsonOptionValue(
        CommandLineOption opt,
        object? val,
        Node? valueExpression,
        SourceFile? sourceFile)
    {
        if (val is null)
        {
            return (null, null);
        }

        var errors = new List<Diagnostic>();

        switch (opt.extraValidation)
        {
            case ExtraValidation.extraValidationSpec:
                {
                    var diag = specToDiagnostic((string)val, false);
                    if (diag != null)
                    {
                        errors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, valueExpression, diag));
                    }
                    break;
                }
            case ExtraValidation.extraValidationLocale:
                {
                    // tsconfigparsing.go:386-389
                    if (!global::Ts.Locale.Locale.TryParse((string)val, out _))
                    {
                        errors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, valueExpression, global::Ts.Diagnostics.Diagnostics.Locale_must_be_an_IETF_BCP_47_language_tag_Examples_Colon_0_1, "en", "ja-jp"));
                    }
                    break;
                }
        }

        if (errors.Count > 0)
        {
            return (null, errors.ToArray());
        }
        return (val, null);
    }

    internal static (object?[]? value, Diagnostic[]? errors) convertJsonOptionOfListType(
        CommandLineOption option,
        object? values,
        string basePath,
        Node? propertyAssignment,
        Node? valueExpression,
        SourceFile? sourceFile)
    {
        Node? expression = null;
        var errors = new List<Diagnostic>();
        if (values is object?[] valuesArr)
        {
            var mappedValues = CoreUtil.MapIndex(valuesArr, (v, index) =>
            {
                if (valueExpression != null)
                {
                    expression = valueExpression.Elements()![index];
                }
                var (result, err) = convertJsonOption(option.Elements()!, v, basePath, propertyAssignment, expression, sourceFile);
                if (err != null)
                {
                    errors.AddRange(err);
                }
                return result;
            })!;
            var filteredValues = mappedValues;
            if (!option.listPreserveFalsyValues)
            {
                filteredValues = CoreUtil.Filter(mappedValues, v =>
                    v is not null && !(v is bool b && !b) && !(v is int i && i == 0) && !(v is double d && d == 0) && !(v is string s && s == ""))!;
            }
            return (filteredValues, errors.ToArray());
        }
        return (null, errors.ToArray());
    }

    internal const string configDirTemplate = "${configDir}";

    internal static bool startsWithConfigDirTemplate(object? value)
    {
        if (value is not string str)
        {
            return false;
        }
        return OptionsUtil.ToLower(str).StartsWith(OptionsUtil.ToLower(configDirTemplate), StringComparison.Ordinal);
    }

    internal static object? normalizeNonListOptionValue(CommandLineOption option, string basePath, object? value)
    {
        if (option.IsFilePath)
        {
            value = TsPathApi.NormalizeSlashes((string)value!);
            if (!startsWithConfigDirTemplate(value))
            {
                value = TsPathApi.GetNormalizedAbsolutePath((string)value!, basePath);
            }
            if ((string)value! == "")
            {
                value = ".";
            }
        }
        return value;
    }

    internal static (object? value, Diagnostic[]? errors) convertJsonOption(
        CommandLineOption opt,
        object? value,
        string basePath,
        Node? propertyAssignment,
        Node? valueExpression,
        SourceFile? sourceFile)
    {
        if (opt.IsCommandLineOnly)
        {
            Node? nodeValue = null;
            if (propertyAssignment != null)
            {
                nodeValue = propertyAssignment.Name();
            }
            if (sourceFile is null && nodeValue is null)
            {
                return (null, new[] { Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Option_0_can_only_be_specified_on_command_line, opt.Name) });
            }
            else
            {
                return (null, new[] { CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, nodeValue, global::Ts.Diagnostics.Diagnostics.Option_0_can_only_be_specified_on_command_line, opt.Name) });
            }
        }
        if (isCompilerOptionsValue(opt, value))
        {
            switch (opt.Kind)
            {
                case CommandLineOptionTypeList:
                    return convertJsonOptionOfListType(opt, value, basePath, propertyAssignment, valueExpression, sourceFile); // as ArrayLiteralExpression | undefined
                case CommandLineOptionTypeListOrElement:
                    if (value is object?[] or string[])
                    {
                        return convertJsonOptionOfListType(opt, value, basePath, propertyAssignment, valueExpression, sourceFile);
                    }
                    else
                    {
                        return convertJsonOption(opt.Elements()!, value, basePath, propertyAssignment, valueExpression, sourceFile);
                    }
                case CommandLineOptionTypeEnum:
                    if (value is null)
                    {
                        return (null, null);
                    }
                    return CommandLineParser.convertJsonOptionOfEnumType(opt, (string)value, valueExpression, sourceFile);
            }

            var (validatedValue, errors) = validateJsonOptionValue(opt, value, valueExpression, sourceFile);
            if ((errors != null && errors.Length > 0) || validatedValue is null)
            {
                return (validatedValue, errors);
            }
            else
            {
                return (normalizeNonListOptionValue(opt, basePath, validatedValue), errors);
            }
        }
        else
        {
            return (null, new[] { CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, valueExpression, global::Ts.Diagnostics.Diagnostics.Compiler_option_0_requires_a_value_of_type_1, opt.Name, getCompilerOptionValueTypeString(opt)) });
        }
    }

    internal static Message? specToDiagnostic(string spec, bool disallowTrailingRecursion)
    {
        if (disallowTrailingRecursion && invalidTrailingRecursion(spec))
        {
            return global::Ts.Diagnostics.Diagnostics.File_specification_cannot_end_in_a_recursive_directory_wildcard_Asterisk_Asterisk_Colon_0;
        }
        if (invalidDotDotAfterRecursiveWildcard(spec))
        {
            return global::Ts.Diagnostics.Diagnostics.File_specification_cannot_contain_a_parent_directory_that_appears_after_a_recursive_directory_wildcard_Asterisk_Asterisk_Colon_0;
        }
        return null;
    }

    internal static bool invalidTrailingRecursion(string spec)
    {
        // Matches **, /**, **/, and /**/, but not a**b.
        // Strip optional trailing slash, then check if it ends with /** or is just **
        var s = spec.EndsWith("/", StringComparison.Ordinal) ? spec[..^1] : spec;
        return s == "**" || s.EndsWith("/**", StringComparison.Ordinal);
    }

    internal static bool invalidDotDotAfterRecursiveWildcard(string s)
    {
        // We used to use the regex /(^|\/)\*\*\/(.*\/)?\.\.($|\/)/ to check for this case, but
        // in v8, that has polynomial performance because the recursive wildcard match - **/ -
        // can be matched in many arbitrary positions when multiple are present, resulting
        // in bad backtracking (and we don't care which is matched - just that some /.. segment
        // comes after some **/ segment).
        int wildcardIndex;
        if (s.StartsWith("**/", StringComparison.Ordinal))
        {
            wildcardIndex = 0;
        }
        else
        {
            wildcardIndex = s.IndexOf("/**/", StringComparison.Ordinal);
        }
        if (wildcardIndex == -1)
        {
            return false;
        }
        int lastDotIndex;
        if (s.EndsWith("/..", StringComparison.Ordinal))
        {
            lastDotIndex = s.Length;
        }
        else
        {
            lastDotIndex = s.LastIndexOf("/../", StringComparison.Ordinal);
        }
        return lastDotIndex > wildcardIndex;
    }

    public static string[][] GetSupportedExtensions(CompilerOptions compilerOptions, FileExtensionInfo[]? extraFileExtensions)
    {
        var needJSExtensions = compilerOptions.GetAllowJS();
        if (extraFileExtensions is null || extraFileExtensions.Length == 0)
        {
            if (needJSExtensions)
            {
                return TsPathApi.AllSupportedExtensions;
            }
            else
            {
                return TsPathApi.SupportedTSExtensions;
            }
        }
        string[][] builtins;
        if (needJSExtensions)
        {
            builtins = TsPathApi.AllSupportedExtensions;
        }
        else
        {
            builtins = TsPathApi.SupportedTSExtensions;
        }
        var flatBuiltins = CoreUtil.Flatten(builtins)!;
        var result = new List<string[]>();
        foreach (var x in extraFileExtensions)
        {
            if (x.ScriptKind == ScriptKind.Deferred || (needJSExtensions && (x.ScriptKind == ScriptKind.JS || x.ScriptKind == ScriptKind.Jsx)) && !flatBuiltins.Contains(x.Extension))
            {
                result.Add(new[] { x.Extension });
            }
        }
        return builtins.Concat(result).ToArray();
    }

    public static string[][] GetSupportedExtensionsWithJsonIfResolveJsonModule(CompilerOptions? compilerOptions, string[][] supportedExtensions)
    {
        if (compilerOptions is null || !compilerOptions.GetResolveJsonModule())
        {
            return supportedExtensions;
        }
        if (CoreUtil.Same(supportedExtensions, TsPathApi.AllSupportedExtensions))
        {
            return TsPathApi.AllSupportedExtensionsWithJson;
        }
        if (CoreUtil.Same(supportedExtensions, TsPathApi.SupportedTSExtensions))
        {
            return TsPathApi.SupportedTSExtensionsWithJson;
        }
        return supportedExtensions.Concat(new[] { new[] { TsPathApi.ExtensionJson } }).ToArray();
    }

    internal static CompilerOptions getDefaultCompilerOptions(string configFileName)
    {
        var options = new CompilerOptions();
        if (configFileName != "" && TsPathApi.GetBaseFileName(configFileName) == "jsconfig.json")
        {
            options = new CompilerOptions
            {
                AllowJs = Tristate.True,
                MaxNodeModuleJsDepth = 2,
                SkipLibCheck = Tristate.True,
                NoEmit = Tristate.True,
            };
        }
        return options;
    }

    internal static TypeAcquisition getDefaultTypeAcquisition(string configFileName)
    {
        var options = new TypeAcquisition();
        if (configFileName != "" && TsPathApi.GetBaseFileName(configFileName) == "jsconfig.json")
        {
            options.Enable = Tristate.True;
        }
        return options;
    }

    internal static (CompilerOptions options, Diagnostic[]? errors) convertCompilerOptionsFromJsonWorker(object? jsonOptions, string basePath, string configFileName)
    {
        var options = getDefaultCompilerOptions(configFileName);
        var (_, errors) = convertOptionsFromJson(CommandLineCompilerOptionsMap, jsonOptions, basePath, new compilerOptionsParser(options));
        if (configFileName != "")
        {
            options.ConfigFilePath = TsPathApi.NormalizeSlashes(configFileName);
        }
        return (options, errors);
    }

    internal static (TypeAcquisition options, Diagnostic[]? errors) convertTypeAcquisitionFromJsonWorker(object? jsonOptions, string basePath, string configFileName)
    {
        var options = getDefaultTypeAcquisition(configFileName);
        var (_, errors) = convertOptionsFromJson(DeclsTypeAcquisition.typeAcquisitionDeclaration.ElementOptions!, jsonOptions, basePath, new typeAcquisitionParser(options));
        return (options, errors);
    }

    // ParseConfigFileTextToJson, ParseJsonConfigFileContent, ParseJsonSourceFileConfigFileContent,
    // GetParsedCommandLineOfConfigFile{,Path}, and the extends/wildcard orchestration (parseConfig,
    // parseJsonConfigFileContentWorker, getFileNamesFromConfigSpecs, …) are ported in the
    // TsConfigParsing.Load.cs / TsConfigParsing.Worker.cs partials (same partial class).
}

// Go: type ExtendedConfigCache interface { GetExtendedConfig(...) *ExtendedConfigCacheEntry }
public interface ExtendedConfigCache
{
    ExtendedConfigCacheEntry GetExtendedConfig(string fileName, Path path, Path[] resolutionStack, ParseConfigHost host);
}

// Go: type ExtendedConfigCacheEntry struct { extendedResult *TsConfigSourceFile; extendedConfig *parsedTsconfig; errors []*Diagnostic }
public sealed class ExtendedConfigCacheEntry
{
    internal TsConfigSourceFile? extendedResult;
    internal parsedTsconfig? extendedConfig;
    internal Diagnostic[]? errors;

    public string[]? ExtendedFileNames()
    {
        if (extendedResult != null)
        {
            return extendedResult.ExtendedSourceFiles;
        }
        return null;
    }
}

public sealed class TsConfigSourceFile
{
    public string[] ExtendedSourceFiles = Array.Empty<string>();
    internal configFileSpecs? configFileSpecs;
    public SourceFile SourceFile = null!;
}

// Go: struct configFileSpecs. filesSpecs/includeSpecs/excludeSpecs hold the user-provided
// (possibly non-string) raw values for error reporting; the validated* arrays drive matching.
internal sealed class configFileSpecs
{
    public object? filesSpecs;
    public object? includeSpecs;
    public object? excludeSpecs;
    public string[]? validatedFilesSpec;
    public string[]? validatedIncludeSpecs;
    public string[]? validatedExcludeSpecs;
    public string[]? validatedFilesSpecBeforeSubstitution;
    public string[]? validatedIncludeSpecsBeforeSubstitution;
    public bool isDefaultIncludeSpec;

    public bool matchesExclude(string fileName, ComparePathsOptions comparePathsOptions)
    {
        if (validatedExcludeSpecs.Len() == 0)
        {
            return false;
        }
        var excludeMatcher = VfsMatchApi.NewSpecMatcher(validatedExcludeSpecs, comparePathsOptions.CurrentDirectory, Usage.Exclude, comparePathsOptions.UseCaseSensitiveFileNames);
        if (excludeMatcher is null)
        {
            return false;
        }
        if (excludeMatcher.MatchString(fileName))
        {
            return true;
        }
        if (!TsPathApi.HasExtension(fileName))
        {
            if (excludeMatcher.MatchString(TsPathApi.EnsureTrailingDirectorySeparator(fileName)))
            {
                return true;
            }
        }
        return false;
    }

    public string getMatchedIncludeSpec(string fileName, ComparePathsOptions comparePathsOptions)
    {
        if (validatedIncludeSpecs.Len() == 0)
        {
            return "";
        }
        for (int index = 0; index < validatedIncludeSpecs!.Length; index++)
        {
            var spec = validatedIncludeSpecs[index];
            var includeMatcher = VfsMatchApi.NewSpecMatcher(new[] { spec }, comparePathsOptions.CurrentDirectory, Usage.Files, comparePathsOptions.UseCaseSensitiveFileNames);
            if (includeMatcher != null && includeMatcher.MatchString(fileName))
            {
                return validatedIncludeSpecsBeforeSubstitution![index];
            }
        }
        return "";
    }

    public string getMatchedFileSpec(string fileName, ComparePathsOptions comparePathsOptions)
    {
        if (validatedFilesSpec.Len() == 0)
        {
            return "";
        }
        var filePath = TsPathApi.ToPath(fileName, comparePathsOptions.CurrentDirectory, comparePathsOptions.UseCaseSensitiveFileNames);
        for (int index = 0; index < validatedFilesSpec!.Length; index++)
        {
            var spec = validatedFilesSpec[index];
            if (TsPathApi.ToPath(spec, comparePathsOptions.CurrentDirectory, comparePathsOptions.UseCaseSensitiveFileNames) == filePath)
            {
                return validatedFilesSpecBeforeSubstitution![index];
            }
        }
        return "";
    }
}
