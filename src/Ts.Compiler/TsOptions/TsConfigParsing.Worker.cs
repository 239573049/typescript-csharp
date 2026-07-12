// Ported from tsgo internal/tsoptions/tsconfigparsing.go — parseConfig, the worker
// (parseJsonConfigFileContentWorker), file-name expansion (getFileNamesFromConfigSpecs +
// extension-priority helpers), spec validation, ${configDir} substitution, and the public config
// entry points (GetParsedCommandLineOfConfigFile{,Path}). See TsConfigParsing.Load.cs header for
// the shared conventions (§1.5 global:: / Diag alias).

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.TsPath;
using Ts.Vfs;
using AstUtil = Ts.Ast.AstUtilities;
using Diag = Ts.Diagnostics.Diagnostics;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath;
using Path = Ts.TsPath.Path;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch;
using SpecMatcher = Ts.Vfs.VfsMatch.SpecMatcher;
using Usage = Ts.Vfs.VfsMatch.Usage;
using static Ts.TsOptions.CommandLineOptionKind;
using static Ts.TsOptions.Errors;

namespace Ts.TsOptions;

// Go: type resolverHost struct { ParseConfigHost } with a no-op Trace method.
internal sealed class ResolverHost : global::Ts.Module.ResolutionHost
{
    private readonly ParseConfigHost host;

    public ResolverHost(ParseConfigHost host)
    {
        this.host = host;
    }

    public IFileSystem FS() => host.FS();
    public string GetCurrentDirectory() => host.GetCurrentDirectory();
    public void Trace(string msg) { }
}

public static partial class TsConfigParsing
{
    internal const string defaultIncludeSpec = "**/*";

    // Go: tryReadFile is shared; readJsonConfigFile passes []*ast.Diagnostic{}. This wraps the
    // existing list-mutating CommandLineParser.tryReadFile into the (string, Diagnostic[]?) shape.
    internal static (string, Diagnostic[]?) tryReadFileList(string fileName, Func<string, (string, bool)> readFile)
    {
        var errors = new List<Diagnostic>();
        var text = CommandLineParser.tryReadFile(fileName, fn =>
        {
            var (content, ok) = readFile(fn);
            return (content, ok);
        }, errors);
        return (text, errors.Count > 0 ? errors.ToArray() : null);
    }

    // Go: parseConfig (tsconfigparsing.go:1039)
    // parseConfig just extracts options/include/exclude/files out of a config file.
    internal static (parsedTsconfig, Diagnostic[]?) parseConfig(
        OrderedMap<string, object?>? json,
        TsConfigSourceFile? sourceFile,
        ParseConfigHost host,
        string basePath,
        string configFileName,
        Path[] resolutionStack,
        ExtendedConfigCache? extendedConfigCache)
    {
        basePath = TsPathApi.NormalizeSlashes(basePath);
        var resolvedPath = TsPathApi.ToPath(configFileName, basePath, host.FS().UseCaseSensitiveFileNames());
        var errors = new List<Diagnostic>();
        if (resolutionStack.Contains(resolvedPath))
        {
            parsedTsconfig result;
            errors.Add(Diagnostic.NewCompilerDiagnostic(Diag.Circularity_detected_while_resolving_configuration_Colon_0));
            if (json.Size() == 0)
            {
                result = new parsedTsconfig { raw = json };
            }
            else
            {
                var (rawResult, err) = convertToObject(sourceFile!.SourceFile);
                if (err != null)
                {
                    errors.AddRange(err);
                }
                result = new parsedTsconfig { raw = rawResult };
            }
            return (result, errors.ToArray());
        }

        parsedTsconfig ownConfig;
        Diagnostic[]? err2;
        if (json != null)
        {
            (ownConfig, err2) = parseOwnConfigOfJson(json, host, basePath, configFileName);
        }
        else
        {
            (ownConfig, err2) = parseOwnConfigOfJsonSourceFile(tsconfigToSourceFile(sourceFile)!, host, basePath, configFileName);
        }
        if (err2 != null)
        {
            errors.AddRange(err2);
        }
        if (ownConfig.options != null && ownConfig.options.Paths != null)
        {
            // If we end up needing to resolve relative paths from 'paths' relative to the config
            // file location, we'll need to know where that config file was.
            ownConfig.options.PathsBasePath = basePath;
        }

        void applyExtendedConfig(extendsResult result, string extendedConfigPath)
        {
            var (extendedConfig, extendedErrors) = getExtendedConfig(sourceFile, extendedConfigPath, host, resolutionStack, extendedConfigCache, result);
            if (extendedErrors != null)
            {
                errors.AddRange(extendedErrors);
            }
            if (extendedConfig != null && extendedConfig.options != null)
            {
                var extendsRaw = extendedConfig.raw;
                var relativeDifference = "";
                void setPropertyValue(string propertyName)
                {
                    if (ownConfig.raw is OrderedMap<string, object?> ownRawMap && ownRawMap.Has(propertyName))
                    {
                        return;
                    }
                    if (propertyName == "include" || propertyName == "exclude" || propertyName == "files")
                    {
                        if (extendsRaw is OrderedMap<string, object?> extRawMap && extRawMap.Has(propertyName))
                        {
                            if (extRawMap.GetOrZero(propertyName) is object?[] slice)
                            {
                                var value = CoreUtil.Map(slice, path =>
                                {
                                    if (path is not string pathStr)
                                    {
                                        return path;
                                    }
                                    if (startsWithConfigDirTemplate(path) || TsPathApi.IsRootedDiskPath(pathStr))
                                    {
                                        return (object?)pathStr;
                                    }
                                    else
                                    {
                                        if (relativeDifference == "")
                                        {
                                            var t = new ComparePathsOptions
                                            {
                                                UseCaseSensitiveFileNames = host.FS().UseCaseSensitiveFileNames(),
                                                CurrentDirectory = basePath,
                                            };
                                            relativeDifference = TsPathApi.ConvertToRelativePath(TsPathApi.GetDirectoryPath(extendedConfigPath), t);
                                        }
                                        return TsPathApi.CombinePaths(relativeDifference, pathStr);
                                    }
                                })!;
                                if (propertyName == "include")
                                {
                                    result.include = value;
                                }
                                else if (propertyName == "exclude")
                                {
                                    result.exclude = value;
                                }
                                else if (propertyName == "files")
                                {
                                    result.files = value;
                                }
                            }
                        }
                    }
                }

                setPropertyValue("include");
                setPropertyValue("exclude");
                setPropertyValue("files");
                if (extendsRaw is OrderedMap<string, object?> extendedRawMap && extendedRawMap.Has("compileOnSave"))
                {
                    if (extendedRawMap.GetOrZero("compileOnSave") is bool compileOnSave)
                    {
                        result.compileOnSave = compileOnSave;
                    }
                }
                ParsingHelpers.mergeCompilerOptions(result.options!, extendedConfig.options, extendsRaw);
            }
        }

        if (ownConfig.extendedConfigPath != null)
        {
            // copy the resolution stack so it is never reused between branches in potential diamond-problem scenarios.
            resolutionStack = resolutionStack.Append(resolvedPath).ToArray();
            var result = new extendsResult
            {
                options = new CompilerOptions(),
            };
            if (ownConfig.extendedConfigPath is string configPathStr)
            {
                applyExtendedConfig(result, configPathStr);
            }
            else if (ownConfig.extendedConfigPath is string[] configPath)
            {
                foreach (var extendedConfigPath in configPath)
                {
                    applyExtendedConfig(result, extendedConfigPath);
                }
            }
            var ownRaw = (OrderedMap<string, object?>)ownConfig.raw!;
            if (result.include != null)
            {
                ownRaw.Set("include", result.include);
            }
            if (result.exclude != null)
            {
                ownRaw.Set("exclude", result.exclude);
            }
            if (result.files != null)
            {
                ownRaw.Set("files", result.files);
            }
            if (result.compileOnSave && !ownRaw.Has("compileOnSave"))
            {
                ownRaw.Set("compileOnSave", result.compileOnSave);
            }
            if (sourceFile != null)
            {
                // map-iter: 已排序 —— InsertSorted keeps ExtendedSourceFiles ordered (Go uses Set.Keys + InsertSorted)
                foreach (var extendedSourceFile in result.extendedSourceFiles.Keys())
                {
                    sourceFile.ExtendedSourceFiles = CoreUtil.InsertSorted(sourceFile.ExtendedSourceFiles, extendedSourceFile, string.CompareOrdinal);
                }
            }
            ownConfig.options = ParsingHelpers.mergeCompilerOptions(result.options!, ownConfig.options, ownConfig.raw);
        }
        return (ownConfig, errors.ToArray());
    }

    // Go: parseJsonConfigFileContentWorker (tsconfigparsing.go:1186)
    internal static ParsedCommandLine parseJsonConfigFileContentWorker(
        OrderedMap<string, object?>? json,
        TsConfigSourceFile? sourceFile,
        ParseConfigHost host,
        string basePath,
        CompilerOptions? existingOptions,
        OrderedMap<string, object?>? existingOptionsRaw,
        string configFileName,
        Path[]? resolutionStack,
        FileExtensionInfo[]? extraFileExtensions,
        ExtendedConfigCache? extendedConfigCache)
    {
        Debug.Assert((json == null && sourceFile != null) || (json != null && sourceFile == null));
        resolutionStack ??= Array.Empty<Path>();

        string basePathForFileNames;
        if (configFileName != "")
        {
            basePathForFileNames = TsPathApi.NormalizePath(directoryOfCombinedPath(configFileName, basePath));
        }
        else
        {
            basePathForFileNames = TsPathApi.NormalizePath(basePath);
        }

        var errors = new List<Diagnostic>();
        var (parsedConfig, parseErrors) = parseConfig(json, sourceFile, host, basePath, configFileName, resolutionStack, extendedConfigCache);
        if (parseErrors != null)
        {
            errors.AddRange(parseErrors);
        }
        ParsingHelpers.mergeCompilerOptions(parsedConfig.options!, existingOptions, existingOptionsRaw);
        handleOptionConfigDirTemplateSubstitution(parsedConfig.options, basePathForFileNames);
        var rawConfig = ParsingHelpers.parseJsonToStringKey(parsedConfig.raw);
        if (configFileName != "" && parsedConfig.options != null)
        {
            parsedConfig.options.ConfigFilePath = TsPathApi.NormalizeSlashes(configFileName);
        }
        propOfRaw getPropFromRaw(string prop, Func<object?, bool> validateElement, string elementTypeName)
        {
            if (rawConfig.TryGet(prop, out var value) && value != null)
            {
                if (value is object?[])
                {
                    var result = rawConfig.GetOrZero(prop);
                    if (result is object?[] resultArr)
                    {
                        if (sourceFile == null && !CoreUtil.Every(resultArr, validateElement))
                        {
                            errors.Add(Diagnostic.NewCompilerDiagnostic(Diag.Compiler_option_0_requires_a_value_of_type_1, prop, elementTypeName));
                        }
                        return new propOfRaw { sliceValue = resultArr, wrongValue = "" };
                    }
                    return new propOfRaw { sliceValue = (object?[])result!, wrongValue = "" };
                }
                else if (sourceFile == null)
                {
                    errors.Add(Diagnostic.NewCompilerDiagnostic(Diag.Compiler_option_0_requires_a_value_of_type_1, prop, "Array"));
                    return new propOfRaw { sliceValue = null, wrongValue = "not-array" };
                }
            }
            return new propOfRaw { sliceValue = null, wrongValue = "no-prop" };
        }
        var referencesOfRaw = getPropFromRaw("references", element => element is OrderedMap<string, object?>, "object");
        var fileSpecs = getPropFromRaw("files", element => element is string, "string");
        if (fileSpecs.sliceValue != null || fileSpecs.wrongValue == "")
        {
            var hasZeroOrNoReferences = false;
            if (referencesOfRaw.wrongValue == "no-prop" || referencesOfRaw.wrongValue == "not-array" || referencesOfRaw.sliceValue.Len() == 0)
            {
                hasZeroOrNoReferences = true;
            }
            var hasExtends = rawConfig.GetOrZero("extends");
            if (fileSpecs.sliceValue != null && fileSpecs.sliceValue.Length == 0 && hasZeroOrNoReferences && hasExtends == null)
            {
                if (sourceFile != null)
                {
                    string fileName;
                    if (configFileName != "")
                    {
                        fileName = configFileName;
                    }
                    else
                    {
                        fileName = "tsconfig.json";
                    }
                    var diagnosticMessage = Diag.The_files_list_in_config_file_0_is_empty;
                    var nodeValue = ForEachTsConfigPropArray<Node>(sourceFile.SourceFile, "files", property => property.AsPropertyAssignment().Initializer);
                    errors.Add(CreateDiagnosticForNodeInSourceFile(sourceFile.SourceFile, nodeValue, diagnosticMessage, fileName));
                }
                else
                {
                    errors.Add(Diagnostic.NewCompilerDiagnostic(Diag.The_files_list_in_config_file_0_is_empty, configFileName));
                }
            }
        }
        var includeSpecs = getPropFromRaw("include", element => element is string, "string");
        var excludeSpecs = getPropFromRaw("exclude", element => element is string, "string");
        var isDefaultIncludeSpec = false;
        if (excludeSpecs.wrongValue == "no-prop" && parsedConfig.options != null)
        {
            var outDir = parsedConfig.options.OutDir;
            var declarationDir = parsedConfig.options.DeclarationDir;
            if (outDir != "" || declarationDir != "")
            {
                var values = new List<object?>();
                if (outDir != "")
                {
                    values.Add(outDir);
                }
                if (declarationDir != "")
                {
                    values.Add(declarationDir);
                }
                excludeSpecs = new propOfRaw { sliceValue = values.ToArray(), wrongValue = "" };
            }
        }
        if (fileSpecs.sliceValue == null && includeSpecs.sliceValue == null)
        {
            includeSpecs = new propOfRaw { sliceValue = new object?[] { defaultIncludeSpec }, wrongValue = "" };
            isDefaultIncludeSpec = true;
        }
        string[]? validatedIncludeSpecs = null;
        string[]? validatedIncludeSpecsBeforeSubstitution = null;
        string[]? validatedExcludeSpecs = null;
        string[]? validatedFilesSpec = null;
        string[]? validatedFilesSpecBeforeSubstitution = null;
        // The exclude spec list is converted into a regular expression, which allows us to quickly
        // test whether a file or directory should be excluded before recursively traversing the file system.
        if (includeSpecs.sliceValue != null)
        {
            var (specs, err) = validateSpecs(includeSpecs.sliceValue, true /*disallowTrailingRecursion*/, tsconfigToSourceFile(sourceFile), "include");
            validatedIncludeSpecsBeforeSubstitution = specs;
            if (err != null)
            {
                errors.AddRange(err);
            }
            validatedIncludeSpecs = getSubstitutedStringArrayWithConfigDirTemplate(validatedIncludeSpecsBeforeSubstitution, basePathForFileNames);
            if (validatedIncludeSpecs == null)
            {
                validatedIncludeSpecs = validatedIncludeSpecsBeforeSubstitution;
            }
        }
        if (excludeSpecs.sliceValue != null)
        {
            var (specs, err) = validateSpecs(excludeSpecs.sliceValue, false /*disallowTrailingRecursion*/, tsconfigToSourceFile(sourceFile), "exclude");
            validatedExcludeSpecs = specs;
            if (err != null)
            {
                errors.AddRange(err);
            }
            var validatedExcludeSpecsWithSubstitution = getSubstitutedStringArrayWithConfigDirTemplate(validatedExcludeSpecs, basePathForFileNames);
            if (validatedExcludeSpecsWithSubstitution != null)
            {
                validatedExcludeSpecs = validatedExcludeSpecsWithSubstitution;
            }
        }
        if (fileSpecs.sliceValue != null)
        {
            var filtered = CoreUtil.Filter(fileSpecs.sliceValue, spec => spec is string)!;
            var beforeSubstitution = new List<string>();
            foreach (var spec in filtered)
            {
                if (spec is string specStr)
                {
                    beforeSubstitution.Add(specStr);
                }
            }
            validatedFilesSpecBeforeSubstitution = beforeSubstitution.ToArray();
            validatedFilesSpec = getSubstitutedStringArrayWithConfigDirTemplate(validatedFilesSpecBeforeSubstitution, basePathForFileNames);
            if (validatedFilesSpec == null)
            {
                validatedFilesSpec = validatedFilesSpecBeforeSubstitution;
            }
        }
        var configFileSpecsValue = new configFileSpecs
        {
            filesSpecs = fileSpecs.sliceValue,
            includeSpecs = includeSpecs.sliceValue,
            excludeSpecs = excludeSpecs.sliceValue,
            validatedFilesSpec = validatedFilesSpec,
            validatedIncludeSpecs = validatedIncludeSpecs,
            validatedExcludeSpecs = validatedExcludeSpecs,
            validatedFilesSpecBeforeSubstitution = validatedFilesSpecBeforeSubstitution,
            validatedIncludeSpecsBeforeSubstitution = validatedIncludeSpecsBeforeSubstitution,
            isDefaultIncludeSpec = isDefaultIncludeSpec,
        };

        if (sourceFile != null)
        {
            sourceFile.configFileSpecs = configFileSpecsValue;
        }

        (string[], int) getFileNames(string basePath)
        {
            var parsedConfigOptions = parsedConfig.options;
            var (fileNames, literalFileNamesLen) = getFileNamesFromConfigSpecs(configFileSpecsValue, basePath, parsedConfigOptions, host.FS(), extraFileExtensions);
            if (shouldReportNoInputFiles(fileNames, canJsonReportNoInputFiles(rawConfig), resolutionStack))
            {
                var includeSpecsRaw = configFileSpecsValue.includeSpecs;
                var excludeSpecsRaw = configFileSpecsValue.excludeSpecs;
                var includeArr = includeSpecsRaw as object?[] ?? Array.Empty<object?>();
                var excludeArr = excludeSpecsRaw as object?[] ?? Array.Empty<object?>();
                errors.Add(Diagnostic.NewCompilerDiagnostic(Diag.No_inputs_were_found_in_config_file_0_Specified_include_paths_were_1_and_exclude_paths_were_2, configFileName, stringifyStringArrayJson(includeArr), stringifyStringArrayJson(excludeArr)));
            }
            return (fileNames, literalFileNamesLen);
        }

        ProjectReference[]? getProjectReferences(string basePath)
        {
            ProjectReference[]? projectReferences = null;
            var newReferencesOfRaw = getPropFromRaw("references", element => element is OrderedMap<string, object?>, "object");
            if (newReferencesOfRaw.sliceValue != null)
            {
                var refs = new List<ProjectReference>();
                for (int index = 0; index < newReferencesOfRaw.sliceValue.Length; index++)
                {
                    var reference = newReferencesOfRaw.sliceValue[index];
                    var @ref = ParsingHelpers.parseProjectReference(reference);
                    if (@ref == null)
                    {
                        continue;
                    }
                    if (!@ref.hasPath || !@ref.pathValid)
                    {
                        errors.Add(createDiagnosticAtProjectReferenceProperty(sourceFile, index, "path", Diag.Compiler_option_0_requires_a_value_of_type_1, "reference.path", "string"));
                        continue;
                    }
                    if (@ref.reference.Path == "")
                    {
                        errors.Add(createDiagnosticAtProjectReferenceProperty(sourceFile, index, "path", Diag.Compiler_option_0_cannot_be_given_an_empty_string, "reference.path"));
                        continue;
                    }
                    if (@ref.hasCircular && !@ref.circularValid)
                    {
                        errors.Add(createDiagnosticAtProjectReferenceProperty(sourceFile, index, "circular", Diag.Compiler_option_0_requires_a_value_of_type_1, "reference.circular", "boolean"));
                    }
                    refs.Add(new ProjectReference
                    {
                        Path = TsPathApi.GetNormalizedAbsolutePath(@ref.reference.Path, basePath),
                        OriginalPath = @ref.reference.Path,
                        Circular = @ref.reference.Circular,
                    });
                }
                projectReferences = refs.ToArray();
            }
            return projectReferences;
        }

        var (fileNamesResult, literalLen) = getFileNames(basePathForFileNames);
        return new ParsedCommandLine(
            new ParsedOptions
            {
                CompilerOptions = parsedConfig.options,
                TypeAcquisition = parsedConfig.typeAcquisition,
                FileNames = fileNamesResult,
                ProjectReferences = getProjectReferences(basePathForFileNames),
            },
            new ComparePathsOptions
            {
                UseCaseSensitiveFileNames = host.FS().UseCaseSensitiveFileNames(),
                CurrentDirectory = basePathForFileNames,
            })
        {
            ConfigFile = sourceFile,
            Raw = parsedConfig.raw,
            Errors = errors.ToArray(),
            extraFileExtensions = extraFileExtensions,
            literalFileNamesLen = literalLen,
        };
    }

    // Go: canJsonReportNoInputFiles (tsconfigparsing.go:1399)
    internal static bool canJsonReportNoInputFiles(OrderedMap<string, object?> rawConfig)
    {
        var filesExists = rawConfig.Has("files");
        var referencesExists = rawConfig.Has("references");
        return !filesExists && !referencesExists;
    }

    // Go: shouldReportNoInputFiles (tsconfigparsing.go:1405)
    internal static bool shouldReportNoInputFiles(string[] fileNames, bool canJsonReportNoInputFiles, Path[] resolutionStack)
    {
        return fileNames.Length == 0 && canJsonReportNoInputFiles && resolutionStack.Length == 0;
    }

    // Go: validateSpecs (tsconfigparsing.go:1409)
    internal static (string[]?, Diagnostic[]?) validateSpecs(object? specs, bool disallowTrailingRecursion, SourceFile? jsonSourceFile, string specKey)
    {
        Diagnostic createDiagnostic(Message message, string spec)
        {
            var element = GetTsConfigPropArrayElementValue(jsonSourceFile, specKey, spec);
            Node? node = null;
            if (element != null)
            {
                node = element;
            }
            return CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(jsonSourceFile, node, message, spec);
        }
        var errors = new List<Diagnostic>();
        var finalSpecs = new List<string>();
        foreach (var spec in (object?[])specs!)
        {
            if (spec is not string specStr)
            {
                continue;
            }
            var diag = specToDiagnostic(specStr, disallowTrailingRecursion);
            if (diag != null)
            {
                errors.Add(createDiagnostic(diag, specStr));
            }
            else
            {
                finalSpecs.Add(specStr);
            }
        }
        return (finalSpecs.Count > 0 ? finalSpecs.ToArray() : null, errors.ToArray());
    }

    // Go: GetTsConfigPropArrayElementValue (tsconfigparsing.go:1475)
    internal static Node? GetTsConfigPropArrayElementValue(SourceFile? tsConfigSourceFile, string propKey, string elementValue)
    {
        var callback = GetCallbackForFindingPropertyAssignmentByValue(elementValue);
        return ForEachTsConfigPropArray(tsConfigSourceFile, propKey, property =>
        {
            var value = callback(property);
            if (value != null)
            {
                return value;
            }
            return null;
        });
    }

    // Go: ForEachTsConfigPropArray[T] (tsconfigparsing.go:1485)
    internal static T? ForEachTsConfigPropArray<T>(SourceFile? tsConfigSourceFile, string propKey, Func<Node, T?> callback) where T : class
    {
        if (tsConfigSourceFile != null)
        {
            return ForEachPropertyAssignment(getTsConfigObjectLiteralExpression(tsConfigSourceFile), propKey, callback);
        }
        return null;
    }

    // Go: createDiagnosticAtProjectReferenceProperty (tsconfigparsing.go:1504)
    internal static Diagnostic createDiagnosticAtProjectReferenceProperty(TsConfigSourceFile? sourceFile, int index, string propertyName, Message message, params object?[] args)
    {
        Node? node = null;
        if (sourceFile != null)
        {
            node = ForEachTsConfigPropArray(sourceFile.SourceFile, "references", property =>
            {
                if (NodeTests.IsArrayLiteralExpression(property.AsPropertyAssignment().Initializer!))
                {
                    var elements = property.AsPropertyAssignment().Initializer!.Elements()!;
                    if (elements.Length > index && NodeTests.IsObjectLiteralExpression(elements[index]))
                    {
                        var propertyNode = ForEachPropertyAssignment(elements[index], propertyName, (Node property2) => property2.AsPropertyAssignment().Initializer as Node);
                        if (propertyNode != null)
                        {
                            return propertyNode;
                        }
                        return elements[index];
                    }
                }
                return null;
            });
        }
        return CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(tsconfigToSourceFile(sourceFile), node, message, args);
    }

    // Go: GetCallbackForFindingPropertyAssignmentByValue (tsconfigparsing.go:1525)
    internal static Func<Node, Node?> GetCallbackForFindingPropertyAssignmentByValue(string value)
    {
        return property =>
        {
            if (NodeTests.IsArrayLiteralExpression(property.AsPropertyAssignment().Initializer!))
            {
                return CoreUtil.Find(property.AsPropertyAssignment().Initializer!.Elements(), element =>
                    NodeTests.IsStringLiteral(element) && element.Text() == value);
            }
            return null;
        };
    }

    // Go: ForEachPropertyAssignment[T] (tsconfigparsing.go:1540). key2 is variadic; single-key
    // callers pass null.
    internal static T? ForEachPropertyAssignment<T>(Node? objectLiteral, string key, Func<Node, T?> callback, string? key2 = null) where T : class
    {
        if (objectLiteral != null)
        {
            foreach (var property in objectLiteral.AsObjectLiteralExpression().Properties!.Nodes)
            {
                if (!NodeTests.IsPropertyAssignment(property))
                {
                    continue;
                }
                if (AstUtil.TryGetTextOfPropertyName(property.Name()!, out var propName))
                {
                    if (propName == key || (key2 != null && key2 == propName))
                    {
                        return callback(property);
                    }
                }
            }
        }
        return null;
    }

    // Go: getTsConfigObjectLiteralExpression (tsconfigparsing.go:1556)
    internal static Node? getTsConfigObjectLiteralExpression(SourceFile? tsConfigSourceFile)
    {
        if (tsConfigSourceFile != null && tsConfigSourceFile.Statements != null && tsConfigSourceFile.Statements.Nodes.Length > 0)
        {
            var expression = tsConfigSourceFile.Statements.Nodes[0].Expression();
            if (expression != null && NodeTests.IsObjectLiteralExpression(expression))
            {
                return expression;
            }
        }
        return null;
    }

    // Go: getSubstitutedPathWithConfigDirTemplate (tsconfigparsing.go:1566)
    internal static string getSubstitutedPathWithConfigDirTemplate(string value, string basePath)
    {
        return TsPathApi.GetNormalizedAbsolutePath(ReplaceFirst(value, configDirTemplate, "./"), basePath);
    }

    // Helper: Go strings.Replace(s, old, new, 1) — replace first occurrence only.
    private static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        var idx = s.IndexOf(oldValue, StringComparison.Ordinal);
        if (idx < 0)
        {
            return s;
        }
        return s[..idx] + newValue + s[(idx + oldValue.Length)..];
    }

    // Go: getSubstitutedStringArrayWithConfigDirTemplate (tsconfigparsing.go:1570)
    internal static string[]? getSubstitutedStringArrayWithConfigDirTemplate(string[]? list, string basePath)
    {
        string[]? result = null;
        for (int i = 0; i < list.Len(); i++)
        {
            var element = list![i];
            if (startsWithConfigDirTemplate(element))
            {
                if (result == null)
                {
                    result = (string[])list.Clone();
                }
                result[i] = getSubstitutedPathWithConfigDirTemplate(element, basePath);
            }
        }
        if (result != null)
        {
            return result;
        }
        return null;
    }

    // Go: handleOptionConfigDirTemplateSubstitution (tsconfigparsing.go:1586)
    internal static void handleOptionConfigDirTemplateSubstitution(CompilerOptions? compilerOptions, string basePath)
    {
        if (compilerOptions == null)
        {
            return;
        }

        // !!! don't hardcode this; use options declarations?

        OrderedMap<string, string[]>? paths = null;
        // map-iter: 序无关 —— per-key substitution into a cloned map; order does not affect result
        foreach (var (k, v) in compilerOptions.Paths.Entries())
        {
            var substitution = getSubstitutedStringArrayWithConfigDirTemplate(v, basePath);
            if (substitution != null)
            {
                if (paths == null)
                {
                    paths = compilerOptions.Paths.Clone();
                    compilerOptions.Paths = paths;
                }
                paths!.Set(k, substitution);
            }
        }

        var rootDirs = getSubstitutedStringArrayWithConfigDirTemplate(compilerOptions.RootDirs, basePath);
        if (rootDirs != null)
        {
            compilerOptions.RootDirs = rootDirs;
        }
        var typeRoots = getSubstitutedStringArrayWithConfigDirTemplate(compilerOptions.TypeRoots, basePath);
        if (typeRoots != null)
        {
            compilerOptions.TypeRoots = typeRoots;
        }
        if (startsWithConfigDirTemplate(compilerOptions.GenerateCpuProfile))
        {
            compilerOptions.GenerateCpuProfile = getSubstitutedPathWithConfigDirTemplate(compilerOptions.GenerateCpuProfile, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.GenerateTrace))
        {
            compilerOptions.GenerateTrace = getSubstitutedPathWithConfigDirTemplate(compilerOptions.GenerateTrace, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.OutFile))
        {
            compilerOptions.OutFile = getSubstitutedPathWithConfigDirTemplate(compilerOptions.OutFile, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.OutDir))
        {
            compilerOptions.OutDir = getSubstitutedPathWithConfigDirTemplate(compilerOptions.OutDir, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.RootDir))
        {
            compilerOptions.RootDir = getSubstitutedPathWithConfigDirTemplate(compilerOptions.RootDir, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.TsBuildInfoFile))
        {
            compilerOptions.TsBuildInfoFile = getSubstitutedPathWithConfigDirTemplate(compilerOptions.TsBuildInfoFile, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.BaseUrl))
        {
            compilerOptions.BaseUrl = getSubstitutedPathWithConfigDirTemplate(compilerOptions.BaseUrl, basePath);
        }
        if (startsWithConfigDirTemplate(compilerOptions.DeclarationDir))
        {
            compilerOptions.DeclarationDir = getSubstitutedPathWithConfigDirTemplate(compilerOptions.DeclarationDir, basePath);
        }
    }

    // Go: hasFileWithHigherPriorityExtension (tsconfigparsing.go:1638)
    internal static bool hasFileWithHigherPriorityExtension(string file, string[][] extensions, Func<string, bool> hasFile)
    {
        var extensionGroup = new List<string>();
        foreach (var group in extensions)
        {
            if (TsPathApi.FileExtensionIsOneOf(file, group))
            {
                extensionGroup.AddRange(group);
            }
        }
        if (extensionGroup.Count == 0)
        {
            return false;
        }
        foreach (var ext in extensionGroup)
        {
            // d.ts files match with .ts extension ... So dont match .d.ts files with .ts extension
            if (TsPathApi.FileExtensionIs(file, ext) && (ext != TsPathApi.ExtensionTs || !TsPathApi.FileExtensionIs(file, TsPathApi.ExtensionDts)))
            {
                return false;
            }
            if (hasFile(TsPathApi.ChangeExtension(file, ext)))
            {
                if (ext == TsPathApi.ExtensionDts && (TsPathApi.FileExtensionIs(file, TsPathApi.ExtensionJs) || TsPathApi.FileExtensionIs(file, TsPathApi.ExtensionJsx)))
                {
                    // LEGACY BEHAVIOR: An off-by-one bug ... retain the behavior to prevent breakage.
                    continue;
                }
                return true;
            }
        }
        return false;
    }

    // Go: removeWildcardFilesWithLowerPriorityExtension (tsconfigparsing.go:1670)
    internal static void removeWildcardFilesWithLowerPriorityExtension(string file, OrderedMap<string, string> wildcardFiles, string[][] extensions, Func<string, string> keyMapper)
    {
        var extensionGroup = new List<string>();
        foreach (var group in extensions)
        {
            if (TsPathApi.FileExtensionIsOneOf(file, group))
            {
                extensionGroup.AddRange(group);
            }
        }
        if (extensionGroup.Count == 0)
        {
            return;
        }
        for (int i = extensionGroup.Count - 1; i >= 0; i--)
        {
            var ext = extensionGroup[i];
            if (TsPathApi.FileExtensionIs(file, ext))
            {
                return;
            }
            var lowerPriorityPath = keyMapper(TsPathApi.ChangeExtension(file, ext));
            wildcardFiles.TryDelete(lowerPriorityPath, out _);
        }
    }

    // Go: getFileNamesFromConfigSpecs (tsconfigparsing.go:1697)
    internal static (string[], int) getFileNamesFromConfigSpecs(
        configFileSpecs configFileSpecs,
        string basePath, // considering this is the current directory
        CompilerOptions? options,
        IFileSystem host,
        FileExtensionInfo[]? extraFileExtensions)
    {
        extraFileExtensions = Array.Empty<FileExtensionInfo>();
        basePath = TsPathApi.NormalizePath(basePath);
        string keyMapper(string value) => TsPathApi.GetCanonicalFileName(value, host.UseCaseSensitiveFileNames());
        // Literal file names ... stored in a file map with a possibly case insensitive key.
        var literalFileMap = new OrderedMap<string, string>();
        var wildcardFileMap = new OrderedMap<string, string>();
        var wildCardJsonFileMap = new OrderedMap<string, string>();
        var validatedFilesSpec = configFileSpecs.validatedFilesSpec;
        var validatedIncludeSpecs = configFileSpecs.validatedIncludeSpecs;
        var validatedExcludeSpecs = configFileSpecs.validatedExcludeSpecs;
        // Rather than re-query this for each file and filespec, we query the supported extensions once.
        var supportedExtensions = GetSupportedExtensions(options!, extraFileExtensions);
        var supportedExtensionsWithJsonIfResolveJsonModule = GetSupportedExtensionsWithJsonIfResolveJsonModule(options, supportedExtensions);
        // Literal files are always included verbatim.
        foreach (var fileName in validatedFilesSpec.OrEmpty())
        {
            var file = TsPathApi.GetNormalizedAbsolutePath(fileName, basePath);
            literalFileMap.Set(keyMapper(fileName), file);
        }

        SpecMatcher? jsonOnlyIncludeMatchers = null;
        if (validatedIncludeSpecs.Len() > 0)
        {
            var files = VfsMatchApi.ReadDirectory(host, basePath, basePath, CoreUtil.Flatten(supportedExtensionsWithJsonIfResolveJsonModule), validatedExcludeSpecs, validatedIncludeSpecs, VfsMatchApi.UnlimitedDepth);
            foreach (var file in files)
            {
                if (TsPathApi.FileExtensionIs(file, TsPathApi.ExtensionJson))
                {
                    if (jsonOnlyIncludeMatchers == null)
                    {
                        var includes = CoreUtil.Filter(validatedIncludeSpecs, include => include.EndsWith(TsPathApi.ExtensionJson, StringComparison.Ordinal));
                        jsonOnlyIncludeMatchers = VfsMatchApi.NewSpecMatcher(includes, basePath, Usage.Files, host.UseCaseSensitiveFileNames());
                    }
                    var includeIndex = -1;
                    if (jsonOnlyIncludeMatchers != null)
                    {
                        includeIndex = jsonOnlyIncludeMatchers.MatchIndex(file);
                    }
                    if (includeIndex != -1)
                    {
                        var key = keyMapper(file);
                        if (!literalFileMap.Has(key) && !wildCardJsonFileMap.Has(key))
                        {
                            wildCardJsonFileMap.Set(key, file);
                        }
                    }
                    continue;
                }
                // If we have already included a literal or wildcard path with a higher priority extension, skip.
                if (hasFileWithHigherPriorityExtension(file, supportedExtensions, fileName =>
                {
                    var canonicalFileName = keyMapper(fileName);
                    return literalFileMap.Has(canonicalFileName) || wildcardFileMap.Has(canonicalFileName);
                }))
                {
                    continue;
                }
                // We may have included a wildcard path with a lower priority extension; remove it.
                removeWildcardFilesWithLowerPriorityExtension(file, wildcardFileMap, supportedExtensions, keyMapper);
                var key2 = keyMapper(file);
                if (!literalFileMap.Has(key2) && !wildcardFileMap.Has(key2))
                {
                    wildcardFileMap.Set(key2, file);
                }
            }
        }
        var result = new List<string>(literalFileMap.Size() + wildcardFileMap.Size() + wildCardJsonFileMap.Size());
        // map-iter: 已排序 —— OrderedMap.Values preserves insertion order (承重: file order)
        foreach (var file in literalFileMap.Values())
        {
            result.Add(file);
        }
        foreach (var file in wildcardFileMap.Values())
        {
            result.Add(file);
        }
        foreach (var file in wildCardJsonFileMap.Values())
        {
            result.Add(file);
        }
        return (result.ToArray(), literalFileMap.Size());
    }

    // Go: GetParsedCommandLineOfConfigFile (tsconfigparsing.go:1830)
    public static (ParsedCommandLine? result, Diagnostic[]? errors) GetParsedCommandLineOfConfigFile(
        string configFileName,
        CompilerOptions? options,
        OrderedMap<string, object?>? optionsRaw,
        ParseConfigHost sys,
        ExtendedConfigCache? extendedConfigCache)
    {
        configFileName = TsPathApi.GetNormalizedAbsolutePath(configFileName, sys.GetCurrentDirectory());
        return GetParsedCommandLineOfConfigFilePath(configFileName, TsPathApi.ToPath(configFileName, sys.GetCurrentDirectory(), sys.FS().UseCaseSensitiveFileNames()), options, optionsRaw, sys, extendedConfigCache);
    }

    // Go: GetParsedCommandLineOfConfigFilePath (tsconfigparsing.go:1841)
    public static (ParsedCommandLine? result, Diagnostic[]? errors) GetParsedCommandLineOfConfigFilePath(
        string configFileName,
        Path path,
        CompilerOptions? options,
        OrderedMap<string, object?>? optionsRaw,
        ParseConfigHost sys,
        ExtendedConfigCache? extendedConfigCache)
    {
        var (configFileText, errors) = tryReadFileList(configFileName, fn =>
        {
            var ok = sys.FS().TryReadFile(fn, out var contents);
            return (contents, ok);
        });
        if (errors.Len() > 0)
        {
            // these are unrecoverable errors--exit to report them as diagnostics
            return (null, errors);
        }

        var tsConfigSourceFile = NewTsconfigSourceFileFromFilePath(configFileName, path, configFileText);
        return (ParseJsonSourceFileConfigFileContent(
            tsConfigSourceFile,
            sys,
            TsPathApi.GetDirectoryPath(configFileName),
            options,
            optionsRaw,
            configFileName,
            null,
            null,
            extendedConfigCache), null);
    }

    // Go call site: core.Must(core.StringifyJson([]string, "", "")). StringifyJson over `any` is
    // AOT-deferred (Core.cs); this per-caller emitter reproduces Go json.Marshal of a []string with
    // no prefix/indent — a compact JSON array of double-quoted, escaped strings — byte-for-byte for
    // the "No inputs were found" diagnostic path.
    internal static string stringifyStringArrayJson(object?[] values)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            AppendJsonString(sb, values[i] as string ?? "");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static void AppendJsonString(System.Text.StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
