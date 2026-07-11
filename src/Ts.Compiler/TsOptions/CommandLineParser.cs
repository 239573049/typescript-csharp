// Ported from tsgo internal/tsoptions/commandlineparser.go

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.TsPath;
using Ts.Vfs;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath;
using StringUtilApi = Ts.StringUtil.StringUtil;
using static Ts.TsOptions.Errors;

namespace Ts.TsOptions;

// Go: type commandLineParser struct + its receiver methods. Declared partial because the
// createUnknownOptionError method lives in Errors.cs (errors.go).
internal sealed partial class commandLineParser
{
    internal ParseCommandLineWorkerDiagnostics workerDiagnostics = null!;
    internal NameMap optionsMap = null!;
    internal IFileSystem? fs;
    internal string currentDirectory = "";
    internal OrderedMap<string, object?> options = new();
    internal List<string> fileNames = new();
    internal List<Diagnostic> errors = new();

    internal AlternateModeDiagnostics? AlternateMode() => workerDiagnostics.didYouMean.alternateMode;

    internal CommandLineOption[] OptionsDeclarations() => workerDiagnostics.didYouMean.OptionDeclarations;

    internal Message? UnknownOptionDiagnostic() => workerDiagnostics.didYouMean.UnknownOptionDiagnostic;

    internal Message? UnknownDidYouMeanDiagnostic() => workerDiagnostics.didYouMean.UnknownDidYouMeanDiagnostic;

    internal void parseStrings(string[] args)
    {
        int i = 0;
        while (i < args.Length)
        {
            var s = args[i];
            i++;
            if (s == "")
            {
                continue;
            }
            switch (s[0])
            {
                case '@':
                    parseResponseFile(s[1..]);
                    break;
                case '-':
                    var inputOptionName = CommandLineParser.getInputOptionName(s);
                    var opt = optionsMap.GetOptionDeclarationFromName(inputOptionName, true /*allowShort*/);
                    if (opt != null)
                    {
                        i = parseOptionValue(args, i, opt, workerDiagnostics.OptionTypeMismatchDiagnostic);
                    }
                    else
                    {
                        var watchOpt = NameMaps.WatchNameMap.GetOptionDeclarationFromName(inputOptionName, true /*allowShort*/);
                        if (watchOpt != null)
                        {
                            i = parseOptionValue(args, i, watchOpt, ParseCommandLineWorkerDiagnostics.watchOptionsDidYouMeanDiagnostics.OptionTypeMismatchDiagnostic);
                        }
                        else
                        {
                            errors.Add(createUnknownOptionError(inputOptionName, s, null, null));
                        }
                    }
                    break;
                default:
                    fileNames.Add(s);
                    break;
            }
        }
    }

    internal void parseResponseFile(string fileName)
    {
        fileName = TsPathApi.GetNormalizedAbsolutePath(fileName, currentDirectory);
        var fileContents = CommandLineParser.tryReadFile(fileName, fn =>
        {
            if (fs is null)
            {
                return ("", false);
            }
            var ok = fs.TryReadFile(fn, out var read);
            return (read, ok);
        }, errors);

        if (fileContents == "")
        {
            return;
        }

        var args = new List<string>();
        // PORT-NOTE: Go builds a []rune; command-line response file tokens are effectively
        // BMP, so char indexing preserves the whitespace/quote scanning. (Surrogate splitting
        // inside quoted args is a non-issue for tsc response files.)
        var text = fileContents;
        var textLength = text.Length;
        var pos = 0;
        while (pos < textLength)
        {
            while (pos < textLength && text[pos] <= ' ')
            {
                pos++;
            }
            if (pos >= textLength)
            {
                break;
            }
            var start = pos;
            if (text[pos] == '"')
            {
                pos++;
                while (pos < textLength && text[pos] != '"')
                {
                    pos++;
                }
                if (pos < textLength)
                {
                    args.Add(text[(start + 1)..pos]);
                    pos++;
                }
                else
                {
                    errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Unterminated_quoted_string_in_response_file_0, fileName));
                }
            }
            else
            {
                while (text[pos] > ' ')
                {
                    pos++;
                }
                args.Add(text[start..pos]);
            }
        }
        parseStrings(args.ToArray());
    }

    internal int parseOptionValue(
        string[] args,
        int i,
        CommandLineOption opt,
        Message? diag)
    {
        if (opt.IsTSConfigOnly && i <= args.Length)
        {
            var optValue = "";
            if (i < args.Length)
            {
                optValue = args[i];
            }
            if (optValue == "null")
            {
                options.Set(opt.Name, null);
                i++;
            }
            else if (opt.Kind == "boolean")
            {
                if (optValue == "false")
                {
                    options.Set(opt.Name, false);
                    i++;
                }
                else
                {
                    if (optValue == "true")
                    {
                        i++;
                    }
                    errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_false_or_null_on_command_line, opt.Name));
                }
            }
            else
            {
                errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Option_0_can_only_be_specified_in_tsconfig_json_file_or_set_to_null_on_command_line, opt.Name));
                if (optValue.Length != 0 && !optValue.StartsWith("-", StringComparison.Ordinal))
                {
                    i++;
                }
            }
        }
        else
        {
            // Check to see if no argument was provided (e.g. "--locale" is the last command-line argument).
            if (i >= args.Length)
            {
                if (opt.Kind != "boolean")
                {
                    errors.Add(Diagnostic.NewCompilerDiagnostic(diag!, opt.Name, getCompilerOptionValueTypeString(opt)));
                    if (opt.Kind == "list")
                    {
                        options.Set(opt.Name, Array.Empty<string>());
                    }
                    else if (opt.Kind == "enum")
                    {
                        errors.Add(createDiagnosticForInvalidEnumType(opt, null, null));
                    }
                }
                else
                {
                    options.Set(opt.Name, true);
                }
                return i;
            }
            if (args[i] != "null")
            {
                switch (opt.Kind)
                {
                    case "number":
                        // !!! Make sure this parseInt matches JS parseInt
                        if (int.TryParse(args[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var num))
                        {
                            if (num >= opt.minValue)
                            {
                                options.Set(opt.Name, num);
                            }
                            else
                            {
                                errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Option_0_requires_value_to_be_greater_than_1, opt.Name, opt.minValue.ToString(CultureInfo.InvariantCulture)));
                            }
                        }
                        else
                        {
                            errors.Add(Diagnostic.NewCompilerDiagnostic(diag!, opt.Name, "number"));
                        }
                        i++;
                        break;
                    case "boolean":
                        // boolean flag has optional value true, false, others
                        var optValue = args[i];

                        // check next argument as boolean flag value
                        if (optValue == "false")
                        {
                            options.Set(opt.Name, false);
                        }
                        else
                        {
                            options.Set(opt.Name, true);
                        }
                        // try to consume next argument as value for boolean flag; do not consume argument if it is not "true" or "false"
                        if (optValue == "false" || optValue == "true")
                        {
                            i++;
                        }
                        break;
                    case "string":
                        {
                            var (val, err) = TsConfigParsing.validateJsonOptionValue(opt, args[i], null, null);
                            if (err is null)
                            {
                                options.Set(opt.Name, val);
                            }
                            else
                            {
                                errors.AddRange(err);
                            }
                            i++;
                            break;
                        }
                    case "list":
                        {
                            var (result, err) = parseListTypeOption(opt, args[i]);
                            options.Set(opt.Name, result);
                            errors.AddRange(err);
                            if (result.Length > 0 || err.Count > 0)
                            {
                                i++;
                            }
                            break;
                        }
                    case "listOrElement":
                        // If not a primitive, the possible types are specified in what is effectively a map of options.
                        throw new InvalidOperationException("listOrElement not supported here");
                    default:
                        {
                            var (val, err) = CommandLineParser.convertJsonOptionOfEnumType(opt, TrimWhiteSpaceLike(args[i]), null, null);
                            options.Set(opt.Name, val);
                            if (err != null)
                            {
                                errors.AddRange(err);
                            }
                            i++;
                            break;
                        }
                }
            }
            else
            {
                options.Set(opt.Name, null);
                i++;
            }
        }
        return i;
    }

    internal (object?[] result, List<Diagnostic> errors) parseListTypeOption(CommandLineOption opt, string value)
    {
        return CommandLineParser.ParseListTypeOption(opt, value);
    }

    // Go strings.TrimFunc(s, IsWhiteSpaceLike): trim leading/trailing code points where the
    // predicate holds. Whitespace-like code points are all BMP, so char indexing is exact.
    internal static string TrimWhiteSpaceLike(string s)
    {
        int start = 0;
        int end = s.Length;
        while (start < end && StringUtilApi.IsWhiteSpaceLike(s[start]))
        {
            start++;
        }
        while (end > start && StringUtilApi.IsWhiteSpaceLike(s[end - 1]))
        {
            end--;
        }
        return s[start..end];
    }
}

// Package-level funcs of commandlineparser.go.
public static class CommandLineParser
{
    public static ParsedCommandLine ParseCommandLine(string[]? commandLine, ParseConfigHost host)
    {
        if (commandLine is null)
        {
            commandLine = Array.Empty<string>();
        }
        var parser = parseCommandLineWorker(ParseCommandLineWorkerDiagnostics.CompilerOptionsDidYouMeanDiagnostics, commandLine, host.FS(), host.GetCurrentDirectory());
        var optionsWithAbsolutePaths = ParsingHelpers.convertToOptionsWithAbsolutePaths(parser.options.Clone()!, TsConfigParsing.CommandLineCompilerOptionsMap, host.GetCurrentDirectory());
        var compilerOptions = TsConfigParsing.convertMapToOptions(optionsWithAbsolutePaths!, new compilerOptionsParser(new CompilerOptions())).CompilerOptions;
        var watchOptions = TsConfigParsing.convertMapToOptions(optionsWithAbsolutePaths!, new watchOptionsParser(new WatchOptions())).WatchOptions;
        var result = ParsedCommandLine.NewParsedCommandLine(compilerOptions, parser.fileNames.ToArray(), new ComparePathsOptions
        {
            UseCaseSensitiveFileNames = host.FS().UseCaseSensitiveFileNames(),
            CurrentDirectory = host.GetCurrentDirectory(),
        });
        result.ParsedConfig.WatchOptions = watchOptions;
        result.Errors = parser.errors.ToArray();
        result.Raw = parser.options;
        return result;
    }

    public static ParsedBuildCommandLine ParseBuildCommandLine(string[]? commandLine, ParseConfigHost host)
    {
        if (commandLine is null)
        {
            commandLine = Array.Empty<string>();
        }
        var parser = parseCommandLineWorker(ParseCommandLineWorkerDiagnostics.buildOptionsDidYouMeanDiagnostics, commandLine, host.FS(), host.GetCurrentDirectory());
        var compilerOptions = new CompilerOptions();
        // map-iter: 已排序 —— OrderedMap 保序
        foreach (var (key, value) in parser.options.Entries())
        {
            var buildOption = NameMaps.BuildNameMap.Get(key);
            if (ReferenceEquals(buildOption, DeclsBuild.TscBuildOption) || ReferenceEquals(buildOption, NameMaps.CompilerNameMap.Get(key)))
            {
                ParsingHelpers.ParseCompilerOptions(key, value, compilerOptions);
            }
        }
        var result = new ParsedBuildCommandLine
        {
            BuildOptions = TsConfigParsing.convertMapToOptions(parser.options, new buildOptionsParser(new BuildOptions())).BuildOptions,
            CompilerOptions = compilerOptions,
            WatchOptions = TsConfigParsing.convertMapToOptions(parser.options, new watchOptionsParser(new WatchOptions())).WatchOptions,
            Projects = parser.fileNames.ToArray(),
            Raw = parser.options,
            comparePathsOptions = new ComparePathsOptions
            {
                UseCaseSensitiveFileNames = host.FS().UseCaseSensitiveFileNames(),
                CurrentDirectory = host.GetCurrentDirectory(),
            },
        };

        if (result.Projects.Length == 0)
        {
            // tsc -b invoked with no extra arguments; act as if invoked with "tsc -b ."
            result.Projects = new[] { "." };
        }

        var errors = new List<Diagnostic>(parser.errors);

        // Nonsensical combinations
        if (result.BuildOptions!.Clean.IsTrue() && result.BuildOptions.Force.IsTrue())
        {
            errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Options_0_and_1_cannot_be_combined, "clean", "force"));
        }
        if (result.BuildOptions.Clean.IsTrue() && result.BuildOptions.Verbose.IsTrue())
        {
            errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Options_0_and_1_cannot_be_combined, "clean", "verbose"));
        }
        if (result.BuildOptions.Clean.IsTrue() && result.CompilerOptions.Watch.IsTrue())
        {
            errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Options_0_and_1_cannot_be_combined, "clean", "watch"));
        }
        if (result.CompilerOptions.Watch.IsTrue() && result.BuildOptions.Dry.IsTrue())
        {
            errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Options_0_and_1_cannot_be_combined, "watch", "dry"));
        }
        result.Errors = errors.ToArray();

        return result;
    }

    internal static commandLineParser parseCommandLineWorker(
        ParseCommandLineWorkerDiagnostics parseCommandLineWithDiagnostics,
        string[] commandLine,
        IFileSystem? fs,
        string currentDirectory)
    {
        var parser = new commandLineParser
        {
            fs = fs,
            currentDirectory = currentDirectory,
            workerDiagnostics = parseCommandLineWithDiagnostics,
            fileNames = new List<string>(),
            options = new OrderedMap<string, object?>(),
            errors = new List<Diagnostic>(),
        };
        parser.optionsMap = NameMaps.GetNameMapFromList(parser.OptionsDeclarations());
        parser.parseStrings(commandLine);
        return parser;
    }

    internal static string getInputOptionName(string input)
    {
        // removes at most two leading '-' from the input string
        if (input.StartsWith("-", StringComparison.Ordinal))
        {
            input = input[1..];
        }
        if (input.StartsWith("-", StringComparison.Ordinal))
        {
            input = input[1..];
        }
        return input;
    }

    // Go: tryReadFile(fileName, readFile func(string)(string,bool), errors) (string, []*Diagnostic).
    // PORT-NOTE: the errors list is mutated in place and the text returned (Go returns the grown
    // slice; observably identical since callers keep the same list reference).
    internal static string tryReadFile(string fileName, Func<string, (string content, bool ok)> readFile, List<Diagnostic> errors)
    {
        // this function adds a compiler diagnostic if the file cannot be read
        var (text, e) = readFile(fileName);

        if (!e)
        {
            // !!! Divergence: the returned error will not give a useful message
            text = "";
            errors.Add(Diagnostic.NewCompilerDiagnostic(global::Ts.Diagnostics.Diagnostics.Cannot_read_file_0, fileName));
        }
        return text;
    }

    public static (object?[] result, List<Diagnostic> errors) ParseListTypeOption(CommandLineOption opt, string value)
    {
        value = value.Trim();
        var errors = new List<Diagnostic>();
        if (value.StartsWith("-", StringComparison.Ordinal))
        {
            return (Array.Empty<object?>(), errors);
        }
        if (opt.Kind == "listOrElement" && !value.Contains(','))
        {
            var (val, err) = TsConfigParsing.validateJsonOptionValue(opt, value, null, null);
            if (err != null)
            {
                return (Array.Empty<object?>(), new List<Diagnostic>(err));
            }
            return (new object?[] { (string)val! }, errors);
        }
        if (value == "")
        {
            return (Array.Empty<object?>(), errors);
        }
        var values = value.Split(',');
        switch (opt.Elements()!.Kind)
        {
            case "string":
                {
                    var elements = CoreUtil.MapFiltered(values, v =>
                    {
                        var (val, err) = TsConfigParsing.validateJsonOptionValue(opt.Elements()!, v, null, null);
                        if (val is string s && err.Len() == 0 && s != "")
                        {
                            return ((object?)s, true);
                        }
                        if (err != null)
                        {
                            errors.AddRange(err);
                        }
                        return ((object?)"", false);
                    })!;
                    return (elements, errors);
                }
            case "boolean":
            case "object":
            case "number":
                // do nothing: only string and enum/object types currently allowed as list entries
                //   !!! we don't actually have number list options, so I didn't implement number list parsing
                throw new InvalidOperationException("List of " + opt.Elements()!.Kind + " is not yet supported.");
            default:
                {
                    var result = CoreUtil.MapFiltered(values, v =>
                    {
                        var (val, err) = convertJsonOptionOfEnumType(opt.Elements()!, commandLineParser.TrimWhiteSpaceLike(v), null, null);
                        if (val is string s && err.Len() == 0 && s != "")
                        {
                            return ((object?)s, true);
                        }
                        if (err != null)
                        {
                            errors.AddRange(err);
                        }
                        return ((object?)"", false);
                    })!;
                    return (result, errors);
                }
        }
    }

    internal static (object? value, Diagnostic[]? errors) convertJsonOptionOfEnumType(
        CommandLineOption opt,
        string value,
        Node? valueExpression,
        SourceFile? sourceFile)
    {
        if (value == "")
        {
            return (null, null);
        }
        var key = OptionsUtil.ToLower(value);
        var typeMap = opt.EnumMap();
        if (typeMap is null)
        {
            return (null, null);
        }
        if (typeMap.TryGet(key, out var val))
        {
            return TsConfigParsing.validateJsonOptionValue(opt, val, valueExpression, sourceFile);
        }
        return (null, new[] { createDiagnosticForInvalidEnumType(opt, sourceFile, valueExpression) });
    }
}
