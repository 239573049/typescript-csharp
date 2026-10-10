// Ported from tsgo internal/tsoptions/tsconfigparsing.go — the tsconfig.json loading slice:
// the JSON-source-file → object walk (convertConfigFileToObject / convertToJson /
// convertPropertyValueToJson / convert{Object,Array}LiteralExpressionToJson), the config
// orchestration (parseConfig / parseOwnConfigOf{Json,JsonSourceFile} / getExtendedConfig /
// getExtendsConfigPath{,OrArray} / ParseExtendedConfig / readJsonConfigFile), the wildcard/file
// expansion (getFileNamesFromConfigSpecs + priority helpers), the ${configDir} substitution
// (handleOptionConfigDirTemplateSubstitution / getSubstituted*), and the public entry points
// (ParseConfigFileTextToJson / ParseJsonConfigFileContent / ParseJsonSourceFileConfigFileContent /
// GetParsedCommandLineOfConfigFile{,Path} / parseJsonConfigFileContentWorker).
//
// Ported 逐函数直译 in Go source order; Go comments and `// !!!` markers kept verbatim. Guards
// resolve to Ts.Ast.NodeTests. plan/05 §1.5 shadowing rule: sibling-namespace types whose class
// name == a namespace tail (Core/Scanner/Diagnostics/Module/Parser/...) are referenced via
// `global::Ts.<Pkg>.<Class>.Member`; diagnostics via the `Diag` alias.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.Module;
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
using OutputPathsApi = Ts.OutputPaths.OutputPaths;
using JsNumApi = Ts.JsNum.JsNum;
using static Ts.TsOptions.CommandLineOptionKind;
using static Ts.TsOptions.Errors;

namespace Ts.TsOptions;

// Go: type parsedTsconfig struct { raw any; options *CompilerOptions; typeAcquisition *TypeAcquisition; extendedConfigPath any }
internal sealed class parsedTsconfig
{
    public object? raw;
    public CompilerOptions? options;
    public TypeAcquisition? typeAcquisition;
    // Note that the case of the config path has not yet been normalized, as no files have been imported into the project yet
    public object? extendedConfigPath; // string | string[]
}

// Go: type extendsResult struct { options *CompilerOptions; watchOptionsCopied bool; include/exclude/files []any; compileOnSave bool; extendedSourceFiles Set[string] }
internal sealed class extendsResult
{
    public CompilerOptions? options;
    public bool watchOptionsCopied;
    public object?[]? include;
    public object?[]? exclude;
    public object?[]? files;
    public bool compileOnSave;
    public Set<string> extendedSourceFiles = new();
}

// Go: type jsonConversionNotifier struct { rootOptions *CommandLineOption; onPropertySet func(...) (any, []*Diagnostic) }
internal sealed class jsonConversionNotifier
{
    public CommandLineOption rootOptions;
    public Func<string, object?, Node, CommandLineOption?, CommandLineOption?, (object?, Diagnostic[]?)> onPropertySet;

    public jsonConversionNotifier(CommandLineOption rootOptions, Func<string, object?, Node, CommandLineOption?, CommandLineOption?, (object?, Diagnostic[]?)> onPropertySet)
    {
        this.rootOptions = rootOptions;
        this.onPropertySet = onPropertySet;
    }
}

// Go: type propOfRaw struct { sliceValue []any; wrongValue string }
internal struct propOfRaw
{
    public object?[]? sliceValue;
    public string wrongValue;
}

public static partial class TsConfigParsing
{
    // Go module-level: compilerOptionsDeclaration
    internal static readonly CommandLineOption compilerOptionsDeclaration = new()
    {
        Name = "compilerOptions",
        Kind = CommandLineOptionTypeObject,
        ElementOptions = CommandLineCompilerOptionsMap,
    };

    // Go module-level: compileOnSaveCommandLineOption
    internal static readonly CommandLineOption compileOnSaveCommandLineOption = new()
    {
        Name = "compileOnSave",
        Kind = CommandLineOptionTypeBoolean,
        DefaultValueDescription = false,
    };

    // Go module-level: extendsOptionDeclaration
    internal static readonly CommandLineOption extendsOptionDeclaration = new()
    {
        Name = "extends",
        Kind = CommandLineOptionTypeListOrElement,
        Category = Diag.File_Management,
        ElementOptions = commandLineOptionsToMap(new[]
        {
            new CommandLineOption { Name = "extends", Kind = CommandLineOptionTypeString },
        }),
    };

    // Go module-level: tsconfigRootOptionsMap
    internal static readonly CommandLineOption tsconfigRootOptionsMap = new()
    {
        Name = "undefined", // should never be needed since this is root
        Kind = CommandLineOptionTypeObject,
        ElementOptions = commandLineOptionsToMap(new[]
        {
            compilerOptionsDeclaration,
            // watchOptionsDeclaration,
            DeclsTypeAcquisition.typeAcquisitionDeclaration,
            extendsOptionDeclaration,
            new CommandLineOption { Name = "references", Kind = CommandLineOptionTypeList },
            new CommandLineOption { Name = "files", Kind = CommandLineOptionTypeList },
            new CommandLineOption { Name = "include", Kind = CommandLineOptionTypeList },
            new CommandLineOption { Name = "exclude", Kind = CommandLineOptionTypeList },
            compileOnSaveCommandLineOption,
        }),
    };

    // Go: parseOwnConfigOfJsonSourceFile (tsconfigparsing.go:180)
    internal static (parsedTsconfig, Diagnostic[]?) parseOwnConfigOfJsonSourceFile(
        SourceFile sourceFile,
        ParseConfigHost host,
        string basePath,
        string configFileName)
    {
        var compilerOptions = getDefaultCompilerOptions(configFileName);
        var typeAcquisition = getDefaultTypeAcquisition(configFileName);
        object? extendedConfigPath = null;
        var rootCompilerOptions = new List<Node>();
        var errors = new List<Diagnostic>();

        (object?, Diagnostic[]?) onPropertySet(
            string keyText,
            object? value,
            Node propertyAssignment,
            CommandLineOption? parentOption,
            CommandLineOption? option)
        {
            // Ensure value is verified except for extends which is handled in its own way for error reporting
            var propertySetErrors = new List<Diagnostic>();
            if (option != null && !ReferenceEquals(option, extendsOptionDeclaration))
            {
                var (v, e) = convertJsonOption(option, value, basePath, propertyAssignment, propertyAssignment.AsPropertyAssignment().Initializer, sourceFile);
                value = v;
                if (e != null)
                {
                    propertySetErrors.AddRange(e);
                }
            }
            if (parentOption != null && parentOption.Name != "undefined" && value != null)
            {
                if (option != null && option.Name != "")
                {
                    Diagnostic[]? parseDiagnostics = null;
                    switch (parentOption.Name)
                    {
                        case "compilerOptions":
                            parseDiagnostics = ParsingHelpers.ParseCompilerOptions(option.Name, value, compilerOptions);
                            break;
                        case "typeAcquisition":
                            parseDiagnostics = ParsingHelpers.ParseTypeAcquisition(option.Name, value, typeAcquisition);
                            break;
                    }
                    if (parseDiagnostics != null)
                    {
                        propertySetErrors.AddRange(parseDiagnostics);
                    }
                }
                else if (keyText != "" && extraKeyDiagnostics(parentOption.Name) != null)
                {
                    var unknownNameDiag = extraKeyDiagnostics(parentOption.Name);
                    if (parentOption.ElementOptions != null)
                    {
                        var possibleOption = parentOption.ElementOptions.Get(keyText);
                        if (possibleOption != null && possibleOption.Name != keyText)
                        {
                            propertySetErrors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(
                                sourceFile,
                                propertyAssignment.Name(),
                                extraKeyDidYouMeanDiagnostics(parentOption.Name)!,
                                keyText,
                                possibleOption.Name));
                        }
                        else
                        {
                            propertySetErrors.Add(createUnknownOptionError(
                                keyText,
                                unknownNameDiag,
                                "", /*unknownOptionErrorText*/
                                propertyAssignment.Name(),
                                sourceFile,
                                null /*alternateMode*/));
                        }
                    }
                }
            }
            else if (ReferenceEquals(parentOption, tsconfigRootOptionsMap))
            {
                if (ReferenceEquals(option, extendsOptionDeclaration))
                {
                    var (configPath, err) = getExtendsConfigPathOrArray(value, host, basePath, configFileName, propertyAssignment, propertyAssignment.AsPropertyAssignment().Initializer, sourceFile);
                    extendedConfigPath = configPath;
                    if (err != null)
                    {
                        propertySetErrors.AddRange(err);
                    }
                }
                else if (option is null)
                {
                    if (keyText == "excludes")
                    {
                        propertySetErrors.Add(CreateDiagnosticForNodeInSourceFile(sourceFile, propertyAssignment.Name(), Diag.Unknown_option_excludes_Did_you_mean_exclude));
                    }
                    if (CoreUtil.Find(DeclsCompiler.OptionsDeclarations, o => o.Name == keyText) != null)
                    {
                        rootCompilerOptions.Add(propertyAssignment.Name()!);
                    }
                }
            }
            return (value, propertySetErrors.ToArray());
        }

        var (json, err2) = convertConfigFileToObject(
            sourceFile,
            new jsonConversionNotifier(tsconfigRootOptionsMap, onPropertySet));
        if (err2 != null)
        {
            errors.AddRange(err2);
        }
        return (new parsedTsconfig
        {
            raw = json,
            options = compilerOptions,
            typeAcquisition = typeAcquisition,
            extendedConfigPath = extendedConfigPath,
        }, errors.ToArray());
    }

    // Go: tsconfigToSourceFile (tsconfigparsing.go:283)
    internal static SourceFile? tsconfigToSourceFile(TsConfigSourceFile? tsconfigSourceFile)
    {
        if (tsconfigSourceFile is null)
        {
            return null;
        }
        return tsconfigSourceFile.SourceFile;
    }

    // Go: NewTsconfigSourceFileFromFilePath (tsconfigparsing.go:290)
    public static TsConfigSourceFile NewTsconfigSourceFileFromFilePath(string configFileName, Path configPath, string configSourceText)
    {
        var sourceFile = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions { FileName = configFileName, Path = configPath },
            configSourceText,
            ScriptKind.Json);
        return new TsConfigSourceFile
        {
            SourceFile = sourceFile,
        };
    }

    // Go: convertConfigFileToObject (tsconfigparsing.go:305)
    internal static (object?, Diagnostic[]?) convertConfigFileToObject(
        SourceFile sourceFile,
        jsonConversionNotifier? jsonConversionNotifier)
    {
        Node? rootExpression = null;
        if (sourceFile.Statements?.Nodes.Length > 0)
        {
            rootExpression = sourceFile.Statements.Nodes[0].Expression();
        }
        if (rootExpression != null && rootExpression.Kind != SyntaxKind.ObjectLiteralExpression)
        {
            var baseFileName = "tsconfig.json";
            if (TsPathApi.GetBaseFileName(sourceFile.FileName()) == "jsconfig.json")
            {
                baseFileName = "jsconfig.json";
            }
            var errors = new Diagnostic[] { Diagnostic.NewCompilerDiagnostic(Diag.The_root_value_of_a_0_file_must_be_an_object, baseFileName) };
            // Last-ditch error recovery. Somewhat useful because the JSON parser will recover from some parse errors by
            // synthesizing a top-level array literal expression. There's a reasonable chance the first element of that
            // array is a well-formed configuration object, made into an array element by stray characters.
            if (NodeTests.IsArrayLiteralExpression(rootExpression))
            {
                var firstObject = CoreUtil.Find(rootExpression.Elements(), NodeTests.IsObjectLiteralExpression);
                if (firstObject != null)
                {
                    return convertToJson(sourceFile, firstObject, true /*returnValue*/, jsonConversionNotifier);
                }
            }
            return (new OrderedMap<string, object?>(), errors);
        }
        return convertToJson(sourceFile, rootExpression, true, jsonConversionNotifier);
    }

    // Go: getExtendsConfigPathOrArray (tsconfigparsing.go:498)
    internal static (string[], Diagnostic[]?) getExtendsConfigPathOrArray(
        object? value,
        ParseConfigHost host,
        string basePath,
        string configFileName,
        Node? propertyAssignment,
        Node? valueExpression,
        SourceFile? sourceFile)
    {
        var extendedConfigPathArray = new List<string>();
        var newBase = basePath;
        if (configFileName != "")
        {
            newBase = directoryOfCombinedPath(configFileName, basePath);
        }
        if (value is null)
        {
            var (_, errors) = convertJsonOption(extendsOptionDeclaration, value, basePath, propertyAssignment, valueExpression, sourceFile);
            return (extendedConfigPathArray.ToArray(), errors);
        }
        if (value is string strValue)
        {
            var (val, err) = getExtendsConfigPath(strValue, host, newBase, valueExpression, sourceFile);
            if (val != "")
            {
                extendedConfigPathArray.Add(val);
            }
            return (extendedConfigPathArray.ToArray(), err);
        }
        var errorsList = new List<Diagnostic>();
        if (value is object?[] valueArr)
        {
            for (int index = 0; index < valueArr.Length; index++)
            {
                var fileName = valueArr[index];
                Node? expression = null;
                if (valueExpression != null)
                {
                    expression = valueExpression.Elements()![index];
                }
                if (fileName is string fileNameStr)
                {
                    var (val, err) = getExtendsConfigPath(fileNameStr, host, newBase, expression, sourceFile);
                    if (val != "")
                    {
                        extendedConfigPathArray.Add(val);
                    }
                    if (err != null)
                    {
                        errorsList.AddRange(err);
                    }
                }
                else
                {
                    var (_, err) = convertJsonOption(extendsOptionDeclaration.Elements()!, value, basePath, propertyAssignment, expression, sourceFile);
                    if (err != null)
                    {
                        errorsList.AddRange(err);
                    }
                }
            }
        }
        else
        {
            var (_, err) = convertJsonOption(extendsOptionDeclaration, value, basePath, propertyAssignment, valueExpression, sourceFile);
            if (err != null)
            {
                errorsList.AddRange(err);
            }
        }
        return (extendedConfigPathArray.ToArray(), errorsList.ToArray());
    }

    // Go: getExtendsConfigPath (tsconfigparsing.go:547)
    internal static (string, Diagnostic[]?) getExtendsConfigPath(
        string extendedConfig,
        ParseConfigHost host,
        string basePath,
        Node? valueExpression,
        SourceFile? sourceFile)
    {
        extendedConfig = TsPathApi.NormalizeSlashes(extendedConfig);
        var errors = new List<Diagnostic>();
        SourceFile? errorFile = null;
        if (sourceFile != null)
        {
            errorFile = sourceFile;
        }
        if (TsPathApi.IsRootedDiskPath(extendedConfig) || extendedConfig.StartsWith("./", StringComparison.Ordinal) || extendedConfig.StartsWith("../", StringComparison.Ordinal))
        {
            var extendedConfigPath = TsPathApi.GetNormalizedAbsolutePath(extendedConfig, basePath);
            if (!host.FS().FileExists(extendedConfigPath) && !extendedConfigPath.EndsWith(TsPathApi.ExtensionJson, StringComparison.Ordinal))
            {
                extendedConfigPath = extendedConfigPath + TsPathApi.ExtensionJson;
                if (!host.FS().FileExists(extendedConfigPath))
                {
                    errors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(errorFile, valueExpression, Diag.File_0_not_found, extendedConfig));
                    return ("", errors.ToArray());
                }
            }
            return (extendedConfigPath, errors.ToArray());
        }
        // If the path isn't a rooted or relative path, resolve like a module
        var resolverHost = new ResolverHost(host);
        // §1.5: `Module` == namespace tail → fully-qualify the package-level static class.
        var resolved = global::Ts.Module.Module.ResolveConfig(extendedConfig, TsPathApi.CombinePaths(basePath, "tsconfig.json"), resolverHost);
        if (resolved.IsResolved())
        {
            return (resolved.ResolvedFileName, errors.ToArray());
        }
        if (extendedConfig == "")
        {
            errors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(errorFile, valueExpression, Diag.Compiler_option_0_cannot_be_given_an_empty_string, "extends"));
        }
        else
        {
            errors.Add(CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(errorFile, valueExpression, Diag.File_0_not_found, extendedConfig));
        }
        return ("", errors.ToArray());
    }

    // Go: convertArrayLiteralExpressionToJson (tsconfigparsing.go:664)
    internal static (object?, Diagnostic[]?) convertArrayLiteralExpressionToJson(
        SourceFile sourceFile,
        Node[] elements,
        CommandLineOption? elementOption,
        bool returnValue)
    {
        if (!returnValue)
        {
            foreach (var element in elements)
            {
                convertPropertyValueToJson(sourceFile, element, elementOption, returnValue, null);
            }
            return (null, null);
        }
        // Filter out invalid values
        if (elements.Length == 0)
        {
            // Always return an empty array, even if elements is nil.
            // The parser will produce nil slices instead of allocating empty ones.
            return (Array.Empty<object?>(), null);
        }
        var errors = new List<Diagnostic>();
        var value = new List<object?>();
        foreach (var element in elements)
        {
            var (convertedValue, err) = convertPropertyValueToJson(sourceFile, element, elementOption, returnValue, null);
            if (err != null)
            {
                errors.AddRange(err);
            }
            if (convertedValue != null)
            {
                value.Add(convertedValue);
            }
        }
        return (value.ToArray(), errors.ToArray());
    }

    // Go: directoryOfCombinedPath (tsconfigparsing.go:694)
    internal static string directoryOfCombinedPath(string fileName, string basePath)
    {
        // Use the `getNormalizedAbsolutePath` function to avoid canonicalizing the path, as it must remain noncanonical
        // until consistent casing errors are reported
        return TsPathApi.GetDirectoryPath(TsPathApi.GetNormalizedAbsolutePath(fileName, basePath));
    }

    // Go: ParseConfigFileTextToJson (tsconfigparsing.go:703)
    public static (object? config, Diagnostic[]? errors) ParseConfigFileTextToJson(string fileName, Path path, string jsonText)
    {
        var jsonSourceFile = global::Ts.Parser.Parser.ParseSourceFile(
            new SourceFileParseOptions { FileName = fileName, Path = path },
            jsonText,
            ScriptKind.Json);
        var (config, errors) = convertConfigFileToObject(jsonSourceFile /*jsonConversionNotifier*/, null);
        if (jsonSourceFile.Diagnostics().Len() > 0)
        {
            errors = new[] { jsonSourceFile.Diagnostics()![0] };
        }
        return (config, errors);
    }

    // Go: ParseJsonSourceFileConfigFileContent (tsconfigparsing.go:726)
    public static ParsedCommandLine ParseJsonSourceFileConfigFileContent(
        TsConfigSourceFile sourceFile,
        ParseConfigHost host,
        string basePath,
        CompilerOptions? existingOptions,
        OrderedMap<string, object?>? existingOptionsRaw,
        string configFileName,
        Path[]? resolutionStack,
        FileExtensionInfo[]? extraFileExtensions,
        ExtendedConfigCache? extendedConfigCache)
    {
        var result = parseJsonConfigFileContentWorker(null /*json*/, sourceFile, host, basePath, existingOptions, existingOptionsRaw, configFileName, resolutionStack, extraFileExtensions, extendedConfigCache);
        return result;
    }

    // Go: convertObjectLiteralExpressionToJson (tsconfigparsing.go:743)
    internal static (OrderedMap<string, object?>?, Diagnostic[]?) convertObjectLiteralExpressionToJson(
        SourceFile sourceFile,
        bool returnValue,
        Node node,
        CommandLineOption? objectOption,
        jsonConversionNotifier? jsonConversionNotifier)
    {
        OrderedMap<string, object?>? result = null;
        if (returnValue)
        {
            result = new OrderedMap<string, object?>();
        }
        var errors = new List<Diagnostic>();
        foreach (var element in node.AsObjectLiteralExpression().Properties!.Nodes)
        {
            if (element.Kind != SyntaxKind.PropertyAssignment)
            {
                errors.Add(Diagnostic.NewDiagnostic(sourceFile, element.Loc, Diag.Property_assignment_expected));
                continue;
            }

            var token = element.QuestionToken();
            if (token != null)
            {
                errors.Add(Diagnostic.NewDiagnostic(sourceFile, token.Loc, Diag.The_0_modifier_can_only_be_used_in_TypeScript_files, "?"));
            }
            var textOfKey = "";
            if (!AstUtil.IsComputedNonLiteralName(element.Name()!))
            {
                AstUtil.TryGetTextOfPropertyName(element.Name()!, out textOfKey);
            }
            var keyText = textOfKey;
            CommandLineOption? option = null;
            if (keyText != "" && objectOption != null && objectOption.ElementOptions != null)
            {
                option = objectOption.ElementOptions.Get(keyText);
                if (option != null && option.Name != keyText)
                {
                    option = null;
                }
            }
            var (value, err) = convertPropertyValueToJson(sourceFile, element.AsPropertyAssignment().Initializer!, option, returnValue, jsonConversionNotifier);
            if (err != null)
            {
                errors.AddRange(err);
            }
            if (keyText != "")
            {
                if (returnValue)
                {
                    result!.Set(keyText, value);
                }
                // Notify key value set, if user asked for it
                if (jsonConversionNotifier != null)
                {
                    var (_, err2) = jsonConversionNotifier.onPropertySet(keyText, value, element, objectOption, option);
                    if (err2 != null)
                    {
                        errors.AddRange(err2);
                    }
                }
            }
        }
        return (result, errors.ToArray());
    }

    // Go: convertToJson (tsconfigparsing.go:795)
    // convertToJson converts the json syntax tree into the json value and report errors
    internal static (object?, Diagnostic[]?) convertToJson(
        SourceFile sourceFile,
        Node? rootExpression,
        bool returnValue,
        jsonConversionNotifier? jsonConversionNotifier)
    {
        if (rootExpression is null)
        {
            if (returnValue)
            {
                // Go: return struct{}{}, nil — an empty object placeholder.
                return (new OrderedMap<string, object?>(), null);
            }
            else
            {
                return (null, null);
            }
        }
        CommandLineOption? rootOptions = null;
        if (jsonConversionNotifier != null)
        {
            rootOptions = jsonConversionNotifier.rootOptions;
        }
        return convertPropertyValueToJson(sourceFile, rootExpression, rootOptions, returnValue, jsonConversionNotifier);
    }

    // Go: isDoubleQuotedString (tsconfigparsing.go:815)
    internal static bool isDoubleQuotedString(Node node)
    {
        return NodeTests.IsStringLiteral(node);
    }

    // Go: convertPropertyValueToJson (tsconfigparsing.go:819)
    internal static (object?, Diagnostic[]?) convertPropertyValueToJson(SourceFile sourceFile, Node valueExpression, CommandLineOption? option, bool returnValue, jsonConversionNotifier? jsonConversionNotifier)
    {
        switch (valueExpression.Kind)
        {
            case SyntaxKind.TrueKeyword:
                return (true, null);
            case SyntaxKind.FalseKeyword:
                return (false, null);
            case SyntaxKind.NullKeyword: // todo: how to manage null
                return (null, null);

            case SyntaxKind.StringLiteral:
                if (!isDoubleQuotedString(valueExpression))
                {
                    return (valueExpression.Text(), new[] { Diagnostic.NewDiagnostic(sourceFile, valueExpression.Loc, Diag.String_literal_with_double_quotes_expected) });
                }
                return (valueExpression.Text(), null);

            case SyntaxKind.NumericLiteral:
                return ((double)JsNumApi.FromString(valueExpression.Text()), null);
            case SyntaxKind.PrefixUnaryExpression:
                if (valueExpression.AsPrefixUnaryExpression().Operator != SyntaxKind.MinusToken || valueExpression.AsPrefixUnaryExpression().Operand!.Kind != SyntaxKind.NumericLiteral)
                {
                    break; // not valid JSON syntax
                }
                return ((double)(-JsNumApi.FromString(valueExpression.AsPrefixUnaryExpression().Operand!.Text())), null);
            case SyntaxKind.ObjectLiteralExpression:
                // Currently having element option declaration in the tsconfig with type "object"
                // determines if it needs onSetValidOptionKeyValueInParent callback or not
                return convertObjectLiteralExpressionToJson(sourceFile, returnValue, valueExpression, option, jsonConversionNotifier);
            case SyntaxKind.ArrayLiteralExpression:
                {
                    var (result, errors) = convertArrayLiteralExpressionToJson(
                        sourceFile,
                        valueExpression.Elements()!,
                        option,
                        returnValue);
                    return (result, errors);
                }
        }
        // Not in expected format
        Diagnostic[] errs;
        if (option != null)
        {
            errs = new[] { Diagnostic.NewDiagnostic(sourceFile, valueExpression.Loc, Diag.Compiler_option_0_requires_a_value_of_type_1, option.Name, getCompilerOptionValueTypeString(option)) };
        }
        else
        {
            errs = new[] { Diagnostic.NewDiagnostic(sourceFile, valueExpression.Loc, Diag.Property_value_can_only_be_string_literal_numeric_literal_true_false_null_object_literal_or_array_literal) };
        }
        return (null, errs);
    }

    // Go: ParseJsonConfigFileContent (tsconfigparsing.go:873)
    public static ParsedCommandLine ParseJsonConfigFileContent(object? json, ParseConfigHost host, string basePath, CompilerOptions? existingOptions, string configFileName, Path[]? resolutionStack, FileExtensionInfo[]? extraFileExtensions, ExtendedConfigCache? extendedConfigCache)
    {
        var result = parseJsonConfigFileContentWorker(ParsingHelpers.parseJsonToStringKey(json), null /*sourceFile*/, host, basePath, existingOptions, null /*existingOptionsRaw*/, configFileName, resolutionStack, extraFileExtensions, extendedConfigCache);
        return result;
    }

    // Go: convertToObject (tsconfigparsing.go:879)
    internal static (object?, Diagnostic[]?) convertToObject(SourceFile sourceFile)
    {
        Node? rootExpression = null;
        if (sourceFile.Statements?.Nodes.Length != 0 && sourceFile.Statements != null)
        {
            rootExpression = sourceFile.Statements.Nodes[0].Expression();
        }
        return convertToJson(sourceFile, rootExpression, true /*returnValue*/, null /*jsonConversionNotifier*/);
    }

    // Go: parseOwnConfigOfJson (tsconfigparsing.go:924)
    internal static (parsedTsconfig, Diagnostic[]?) parseOwnConfigOfJson(
        OrderedMap<string, object?> json,
        ParseConfigHost host,
        string basePath,
        string configFileName)
    {
        var errors = new List<Diagnostic>();
        if (json.Has("excludes"))
        {
            errors.Add(Diagnostic.NewCompilerDiagnostic(Diag.Unknown_option_excludes_Did_you_mean_exclude));
        }
        var (options, err1) = convertCompilerOptionsFromJsonWorker(json.GetOrZero("compilerOptions"), basePath, configFileName);
        var (typeAcquisition, err2) = convertTypeAcquisitionFromJsonWorker(json.GetOrZero("typeAcquisition"), basePath, configFileName);
        if (err1 != null)
        {
            errors.AddRange(err1);
        }
        if (err2 != null)
        {
            errors.AddRange(err2);
        }
        // json.compileOnSave handled by raw
        // Go: tsconfigparsing.go:939-943 — `var extendedConfigPath []string` (nil slice) plus the
        // guard `extends != nil && extends != ""`. The nil slice prints as `[]string(nil)` in
        // reflect.TypeOf, so the later `ownConfig.extendedConfigPath != nil` check (parseConfig) is
        // FALSE when the config has no "extends" and the inherited-config merge is skipped. C#
        // must keep null here for the same reason (an empty array would be non-null and would run
        // the merge with no extended configs, which can emit spurious errors such as
        // "Compiler option 'extends' requires a value of type string or Array").
        Diagnostic[]? err3 = null;
        object? extendedConfigPathRaw = null;
        object? extendsRaw = json.GetOrZero("extends");
        if (extendsRaw is not null && !(extendsRaw is string extendsString && extendsString == ""))
        {
            (string[] paths, err3) = getExtendsConfigPathOrArray(extendsRaw, host, basePath, configFileName, null, null, null);
            extendedConfigPathRaw = paths;
        }
        if (err3 != null)
        {
            errors.AddRange(err3);
        }
        return (new parsedTsconfig
        {
            raw = json,
            options = options,
            typeAcquisition = typeAcquisition,
            extendedConfigPath = extendedConfigPathRaw,
        }, errors.ToArray());
    }

    // Go: readJsonConfigFile (tsconfigparsing.go:953)
    internal static (TsConfigSourceFile, Diagnostic[]?) readJsonConfigFile(string fileName, Path path, Func<string, (string, bool)> readFile)
    {
        var (text, diagnostic) = tryReadFileList(fileName, readFile);
        if (text != "")
        {
            return (new TsConfigSourceFile
            {
                SourceFile = global::Ts.Parser.Parser.ParseSourceFile(
                    new SourceFileParseOptions { FileName = fileName, Path = path },
                    text,
                    ScriptKind.Json),
            }, diagnostic);
        }
        else
        {
            var factory = new NodeFactory();
            var file = new TsConfigSourceFile
            {
                SourceFile = factory.NewSourceFile(new SourceFileParseOptions { FileName = fileName, Path = path }, "", factory.NewNodeList(Array.Empty<Node>()), factory.NewToken(SyntaxKind.EndOfFile)).AsSourceFile(),
            };
            file.SourceFile.SetDiagnostics(diagnostic);
            return (file, diagnostic);
        }
    }

    // Go: getExtendedConfig (tsconfigparsing.go:972)
    internal static (parsedTsconfig?, Diagnostic[]?) getExtendedConfig(
        TsConfigSourceFile? sourceFile,
        string extendedConfigFileName,
        ParseConfigHost host,
        Path[] resolutionStack,
        ExtendedConfigCache? extendedConfigCache,
        extendsResult result)
    {
        var errors = new List<Diagnostic>();
        var extendedConfigPath = TsPathApi.ToPath(extendedConfigFileName, host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames());

        ExtendedConfigCacheEntry cacheEntry;
        // Bypass the cache when we detect a cycle in the resolution stack.
        if (extendedConfigCache != null && !resolutionStack.Contains(extendedConfigPath))
        {
            cacheEntry = extendedConfigCache.GetExtendedConfig(extendedConfigFileName, extendedConfigPath, resolutionStack, host);
        }
        else
        {
            cacheEntry = ParseExtendedConfig(extendedConfigFileName, extendedConfigPath, resolutionStack, host, extendedConfigCache);
        }

        if (cacheEntry.errors.Len() > 0)
        {
            errors.AddRange(cacheEntry.errors!);
        }

        if (cacheEntry.extendedResult != null)
        {
            if (sourceFile != null)
            {
                result.extendedSourceFiles.Add(cacheEntry.extendedResult.SourceFile.FileName());
                foreach (var extendedSourceFile in cacheEntry.extendedResult.ExtendedSourceFiles)
                {
                    result.extendedSourceFiles.Add(extendedSourceFile);
                }
            }
        }
        return (cacheEntry.extendedConfig, errors.ToArray());
    }

    // Go: ParseExtendedConfig (tsconfigparsing.go:1009)
    public static ExtendedConfigCacheEntry ParseExtendedConfig(
        string fileName,
        Path path,
        Path[] resolutionStack,
        ParseConfigHost host,
        ExtendedConfigCache? extendedConfigCache)
    {
        var (extendedResult, readErrors) = readJsonConfigFile(fileName, path, fn =>
        {
            var ok = host.FS().TryReadFile(fn, out var contents);
            return (contents, ok);
        });
        var entry = new ExtendedConfigCacheEntry
        {
            extendedResult = extendedResult,
        };

        if (readErrors.Len() > 0)
        {
            entry.errors = readErrors;
            return entry;
        }

        var parseDiagnostics = extendedResult.SourceFile.Diagnostics();
        if (parseDiagnostics.Len() > 0)
        {
            entry.errors = parseDiagnostics;
            return entry;
        }

        var (extendedConfig, parseErrors) = parseConfig(null, extendedResult, host, TsPathApi.GetDirectoryPath(fileName), TsPathApi.GetBaseFileName(fileName), resolutionStack, extendedConfigCache);
        entry.extendedConfig = extendedConfig;
        entry.errors = parseErrors;
        return entry;
    }
}
