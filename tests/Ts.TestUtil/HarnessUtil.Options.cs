// Ported from tsgo internal/testutil/harnessutil/harnessutil.go — the test-configuration option
// surface: HarnessOptions (harnessutil.go:62-77), SetOptionsFromTestConfig (266-291), the
// harness-extended compiler option list (293-311), harness-only options (313-385),
// getOptionValue (417-466), getValueOfOptionString/tryGetValueOfOptionString/
// getCommandLineOption/getAllValuesForOption (1119-1169), GetConfigNameFromFileName (1197-1203)
// and SkipUnsupportedCompilerOptions (1205-1234).
//
// PORT-NOTEs:
//   - `t.Fatalf` → InvalidOperationException carrying the Go message text; `t.Skipf` →
//     xunit Assert.Skip with the Go message text.
//   - tsoptions.CompilerOptionsValue (Go `any`) → object?; Go `int` option values box as int
//     (ParsingHelpers.parseNumber accepts int), `[]any` list values are object?[].

using System;
using System.Collections.Generic;
using System.Globalization;
using Ts.Collections;
using Ts.Core;
using Ts.TsOptions;
using Xunit;

namespace Ts.TestUtil;

using TsPathApi = global::Ts.TsPath.TsPath;

// harnessutil.go:62-77
public sealed class HarnessOptions
{
    public bool UseCaseSensitiveFileNames;
    public string BaselineFile = "";
    public string IncludeBuiltFile = "";
    public string FileName = "";
    public string[]? LibFiles;
    public bool NoImplicitReferences;
    public string CurrentDirectory = "";
    public string Symlink = "";
    public string Link = "";
    public bool NoTypesAndSymbols;
    public bool FullEmitPaths;
    public bool ReportDiagnostics;
    public bool CaptureSuggestions;
    public string TypescriptVersion = "";
}

public static partial class HarnessUtil
{
    // harnessutil.go:266-291 — SetOptionsFromTestConfig.
    public static void SetOptionsFromTestConfig(IReadOnlyDictionary<string, string> testConfig, CompilerOptions compilerOptions, HarnessOptions harnessOptions, string currentDirectory, bool allowUnknownOptions)
    {
        // map-iter: 序无关 — each entry sets an independent option field.
        foreach (var (name, value) in testConfig)
        {
            if (name == "typescriptversion")
            {
                continue;
            }

            CommandLineOption? commandLineOption = GetCommandLineOption(name);
            if (commandLineOption != null)
            {
                object? parsedValue = GetOptionValue(commandLineOption, value, currentDirectory);
                Ts.Ast.Diagnostic[]? errors = ParsingHelpers.ParseCompilerOptions(commandLineOption.Name, parsedValue, compilerOptions);
                if (errors != null && errors.Length > 0)
                {
                    throw new InvalidOperationException($"Error parsing value '{value}' for compiler option '{commandLineOption.Name}'.");
                }
                continue;
            }
            CommandLineOption? harnessOption = GetHarnessOption(name);
            if (harnessOption != null)
            {
                object? parsedValue = GetOptionValue(harnessOption, value, currentDirectory);
                ParseHarnessOption(harnessOption.Name, parsedValue, harnessOptions);
                continue;
            }
            if (!allowUnknownOptions)
            {
                throw new InvalidOperationException($"Unknown compiler option '{name}'.");
            }
        }
    }

    // harnessutil.go:293-311 — compilerOptions.
    private static readonly CommandLineOption[] compilerOptions = CoreUtil.Concatenate(
        DeclsCompiler.OptionsDeclarations,
        [
            new CommandLineOption
            {
                Name = "allowNonTsExtensions",
                Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
            },
            new CommandLineOption
            {
                Name = "noErrorTruncation",
                Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
            },
            new CommandLineOption
            {
                Name = "suppressOutputPathCheck",
                Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
            },
            new CommandLineOption
            {
                Name = "noCheck",
                Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
            },
        ])!;

    // harnessutil.go:313-371 — harnessCommandLineOptions.
    private static readonly CommandLineOption[] harnessCommandLineOptions =
    [
        new CommandLineOption
        {
            Name = "useCaseSensitiveFileNames",
            Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
        },
        new CommandLineOption
        {
            Name = "baselineFile",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        new CommandLineOption
        {
            Name = "includeBuiltFile",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        new CommandLineOption
        {
            Name = "fileName",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        new CommandLineOption
        {
            Name = "libFiles",
            Kind = CommandLineOptionKind.CommandLineOptionTypeList,
        },
        new CommandLineOption
        {
            Name = "noImplicitReferences",
            Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
        },
        new CommandLineOption
        {
            Name = "currentDirectory",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        new CommandLineOption
        {
            Name = "symlink",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        new CommandLineOption
        {
            Name = "link",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        new CommandLineOption
        {
            Name = "noTypesAndSymbols",
            Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
        },
        // Emitted js baseline will print full paths for every output file
        new CommandLineOption
        {
            Name = "fullEmitPaths",
            Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
        },
        // used to enable error collection in `transpile` baselines
        new CommandLineOption
        {
            Name = "reportDiagnostics",
            Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
        },
        // Adds suggestion diagnostics to error baselines
        new CommandLineOption
        {
            Name = "captureSuggestions",
            Kind = CommandLineOptionKind.CommandLineOptionTypeBoolean,
        },
    ];

    // harnessutil.go:373-377 — getHarnessOption.
    private static CommandLineOption? GetHarnessOption(string name)
    {
        return CoreUtil.Find(harnessCommandLineOptions, option => global::Ts.StringUtil.StringUtil.EqualFold(option.Name, name));
    }

    // harnessutil.go:379-415 — parseHarnessOption.
    private static void ParseHarnessOption(string key, object? value, HarnessOptions harnessOptions)
    {
        switch (key)
        {
            case "useCaseSensitiveFileNames":
                harnessOptions.UseCaseSensitiveFileNames = (bool)value!;
                break;
            case "baselineFile":
                harnessOptions.BaselineFile = (string)value!;
                break;
            case "includeBuiltFile":
                harnessOptions.IncludeBuiltFile = (string)value!;
                break;
            case "fileName":
                harnessOptions.FileName = (string)value!;
                break;
            case "libFiles":
                var values = (object?[])value!;
                var libFiles = new List<string>(values.Length);
                foreach (object? v in values)
                {
                    libFiles.Add((string)v!);
                }
                harnessOptions.LibFiles = libFiles.ToArray();
                break;
            case "noImplicitReferences":
                harnessOptions.NoImplicitReferences = (bool)value!;
                break;
            case "currentDirectory":
                harnessOptions.CurrentDirectory = (string)value!;
                break;
            case "symlink":
                harnessOptions.Symlink = (string)value!;
                break;
            case "link":
                harnessOptions.Link = (string)value!;
                break;
            case "noTypesAndSymbols":
                harnessOptions.NoTypesAndSymbols = (bool)value!;
                break;
            case "fullEmitPaths":
                harnessOptions.FullEmitPaths = (bool)value!;
                break;
            case "reportDiagnostics":
                harnessOptions.ReportDiagnostics = (bool)value!;
                break;
            case "captureSuggestions":
                harnessOptions.CaptureSuggestions = (bool)value!;
                break;
            case "typescriptVersion":
                harnessOptions.TypescriptVersion = (string)value!;
                break;
            default:
                throw new InvalidOperationException($"Unknown harness option '{key}'.");
        }
    }

    // harnessutil.go:417-466 — getOptionValue.
    private static object? GetOptionValue(CommandLineOption option, string value, string cwd)
    {
        switch (option.Kind)
        {
            case CommandLineOptionKind.CommandLineOptionTypeString:
                if (option.IsFilePath)
                {
                    return TsPathApi.GetNormalizedAbsolutePath(value, cwd);
                }
                return value;
            case CommandLineOptionKind.CommandLineOptionTypeNumber:
                if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int numVal))
                {
                    throw new InvalidOperationException($"Value for option '{option.Name}' must be a number, got: {value}");
                }
                return numVal;
            case CommandLineOptionKind.CommandLineOptionTypeBoolean:
                switch (global::Ts.StringUtil.StringUtil.ToLowerJS(value))
                {
                    case "true":
                        return true;
                    case "false":
                        return false;
                    default:
                        throw new InvalidOperationException($"Value for option '{option.Name}' must be a boolean, got: {value}");
                }
            case CommandLineOptionKind.CommandLineOptionTypeEnum:
                if (!option.EnumMap()!.TryGet(global::Ts.StringUtil.StringUtil.ToLowerJS(value), out object? enumVal))
                {
                    throw new InvalidOperationException($"Value for option '{option.Name}' must be one of {string.Join(",", option.EnumMap().Keys())}, got: {value}");
                }
                return enumVal;
            case CommandLineOptionKind.CommandLineOptionTypeList:
            case CommandLineOptionKind.CommandLineOptionTypeListOrElement:
                var (listVal, errors) = CommandLineParser.ParseListTypeOption(option, value);
                if (option.Elements()!.IsFilePath)
                {
                    return CoreUtil.Map(listVal, item => (object?)TsPathApi.GetNormalizedAbsolutePath((string)item!, cwd));
                }
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException($"Unknown value '{value}' for compiler option '{option.Name}'");
                }
                return listVal;
            case CommandLineOptionKind.CommandLineOptionTypeObject:
                throw new InvalidOperationException($"Object type options like '{option.Name}' are not supported");
        }
        return null;
    }

    // harnessutil.go:1119-1125 — getValueOfOptionString.
    internal static object? GetValueOfOptionString(string option, string value)
    {
        var (result, ok) = TryGetValueOfOptionString(option, value);
        if (!ok)
        {
            throw new InvalidOperationException($"Unknown value '{value}' for option '{option}'");
        }
        return result;
    }

    // harnessutil.go:1127-1149 — tryGetValueOfOptionString.
    internal static (object? Value, bool Ok) TryGetValueOfOptionString(string option, string value)
    {
        CommandLineOption? optionDecl = GetCommandLineOption(option);
        if (optionDecl == null)
        {
            return (null, false);
        }
        switch (optionDecl.Kind)
        {
            case CommandLineOptionKind.CommandLineOptionTypeEnum:
                if (!optionDecl.EnumMap()!.TryGet(global::Ts.StringUtil.StringUtil.ToLowerJS(value), out object? enumVal))
                {
                    return (null, false);
                }
                return (enumVal, true);
            case CommandLineOptionKind.CommandLineOptionTypeBoolean:
                switch (global::Ts.StringUtil.StringUtil.ToLowerJS(value))
                {
                    case "true":
                        return (true, true);
                    case "false":
                        return (false, true);
                }
                return (null, false);
        }
        return (value, true);
    }

    // harnessutil.go:1151-1155 — getCommandLineOption.
    internal static CommandLineOption? GetCommandLineOption(string option)
    {
        return CoreUtil.Find(compilerOptions, optionDecl => global::Ts.StringUtil.StringUtil.EqualFold(optionDecl.Name, option));
    }

    // harnessutil.go:1157-1169 — getAllValuesForOption.
    internal static string[]? GetAllValuesForOption(string option)
    {
        CommandLineOption? optionDecl = GetCommandLineOption(option);
        if (optionDecl == null)
        {
            return null;
        }
        switch (optionDecl.Kind)
        {
            case CommandLineOptionKind.CommandLineOptionTypeEnum:
                return [.. optionDecl.EnumMap().Keys()];
            case CommandLineOptionKind.CommandLineOptionTypeBoolean:
                return ["true", "false"];
        }
        return null;
    }

    // harnessutil.go:1197-1203 — GetConfigNameFromFileName.
    public static string GetConfigNameFromFileName(string filename)
    {
        string basenameLower = global::Ts.StringUtil.StringUtil.ToLowerJS(TsPathApi.GetBaseFileName(filename));
        if (basenameLower == "tsconfig.json" || basenameLower == "jsconfig.json")
        {
            return basenameLower;
        }
        return "";
    }

    // harnessutil.go:1205-1234 — SkipUnsupportedCompilerOptions.
    public static void SkipUnsupportedCompilerOptions(CompilerOptions options)
    {
        switch (options.Module)
        {
            case ModuleKind.AMD:
            case ModuleKind.UMD:
            case ModuleKind.System:
                Assert.Skip($"unsupported module kind {options.Module.ToDisplayString()}");
                break;
        }
        switch (options.ModuleResolution)
        {
            case ModuleResolutionKind.Node10:
            case ModuleResolutionKind.Classic:
                Assert.Skip($"unsupported module resolution kind {(int)options.ModuleResolution}");
                break;
        }
        if (options.ESModuleInterop.IsFalse())
        {
            Assert.Skip("esModuleInterop=false is unsupported");
        }
        if (options.AllowSyntheticDefaultImports.IsFalse())
        {
            Assert.Skip("allowSyntheticDefaultImports=false is unsupported");
        }
        if (options.BaseUrl != "")
        {
            Assert.Skip($"unsupported baseUrl {options.BaseUrl}");
        }
        if (options.OutFile != "")
        {
            Assert.Skip($"unsupported outFile {options.OutFile}");
        }
        switch (options.Target)
        {
            case ScriptTarget.ES5:
                // Go formats options.Target through scripttarget_stringer_generated.go, which yields
                // "ES5" for the only value this arm admits.
                Assert.Skip("unsupported target ES5");
                break;
        }
        if (options.AlwaysStrict.IsFalse())
        {
            Assert.Skip("alwaysStrict=false is unsupported");
        }
    }
}
