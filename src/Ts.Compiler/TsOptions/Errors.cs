// Ported from tsgo internal/tsoptions/errors.go

using System.Linq;
using Ts.Ast;
using Ts.Core;
using Message = Ts.Diagnostics.Message;
using Ts.Collections;


namespace Ts.TsOptions;

// Package-level funcs of errors.go. Consumers pull these in via
// `using static Ts.TsOptions.Errors;` to match Go's unqualified call sites.
public static class Errors
{
    internal static Diagnostic createDiagnosticForInvalidEnumType(CommandLineOption opt, SourceFile? sourceFile, Node? node)
    {
        var namesOfType = opt.EnumMap().Keys().ToArray();
        var stringNames = formatEnumTypeKeys(opt, namesOfType);
        var optName = "--" + opt.Name;
        return CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, node, global::Ts.Diagnostics.Diagnostics.Argument_for_0_option_must_be_Colon_1, optName, stringNames);
    }

    internal static string formatEnumTypeKeys(CommandLineOption opt, string[] keys)
    {
        if (opt.DeprecatedKeys() != null)
        {
            keys = CoreUtil.Filter(keys, key => !opt.DeprecatedKeys().Has(key))!;
        }
        return "'" + string.Join("', '", keys) + "'";
    }

    internal static string getCompilerOptionValueTypeString(CommandLineOption option)
    {
        switch (option.Kind)
        {
            case CommandLineOptionKind.CommandLineOptionTypeListOrElement:
                return $"{getCompilerOptionValueTypeString(option.Elements()!)} or Array";
            case CommandLineOptionKind.CommandLineOptionTypeList:
                return "Array";
            default:
                return option.Kind;
        }
    }

    internal static Diagnostic createUnknownOptionError(
        string unknownOption,
        Message? unknownOptionDiagnostic,
        string unknownOptionErrorText, // optional
        Node? node, // optional
        SourceFile? sourceFile, // optional
        AlternateModeDiagnostics? alternateMode) // optional
    {
        if (alternateMode != null && alternateMode.optionsNameMap != null)
        {
            var otherOption = alternateMode.optionsNameMap.Get(OptionsUtil.ToLower(unknownOption));
            if (otherOption != null)
            {
                // tscbuildoption
                var diagnostic = alternateMode.diagnostic;
                if (otherOption.Name == "build")
                {
                    diagnostic = global::Ts.Diagnostics.Diagnostics.Option_build_must_be_the_first_command_line_argument;
                }
                return CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, node, diagnostic!, unknownOption);
            }
        }
        if (unknownOptionErrorText == "")
        {
            unknownOptionErrorText = unknownOption;
        }
        // TODO: possibleOption := spelling suggestion
        return CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(sourceFile, node, unknownOptionDiagnostic!, unknownOptionErrorText);
    }

    public static Diagnostic CreateDiagnosticForNodeInSourceFile(SourceFile? sourceFile, Node? node, Message message, params object?[] args)
    {
        return Diagnostic.NewDiagnostic(sourceFile, new TextRange(global::Ts.Scanner.Scanner.SkipTrivia(sourceFile!.Text(), node!.Loc.Pos), node.End), message, args);
    }

    public static Diagnostic CreateDiagnosticForNodeInSourceFileOrCompilerDiagnostic(SourceFile? sourceFile, Node? node, Message message, params object?[] args)
    {
        if (sourceFile != null && node != null)
        {
            return CreateDiagnosticForNodeInSourceFile(sourceFile, node, message, args);
        }
        return Diagnostic.NewCompilerDiagnostic(message, args);
    }

    internal static Message? extraKeyDiagnostics(string s)
    {
        switch (s)
        {
            case "compilerOptions":
                return global::Ts.Diagnostics.Diagnostics.Unknown_compiler_option_0;
            case "watchOptions":
                return global::Ts.Diagnostics.Diagnostics.Unknown_watch_option_0;
            case "typeAcquisition":
                return global::Ts.Diagnostics.Diagnostics.Unknown_type_acquisition_option_0;
            case "buildOptions":
                return global::Ts.Diagnostics.Diagnostics.Unknown_build_option_0;
            default:
                return null;
        }
    }

    internal static Message? extraKeyDidYouMeanDiagnostics(string s)
    {
        switch (s)
        {
            case "compilerOptions":
                return global::Ts.Diagnostics.Diagnostics.Unknown_compiler_option_0_Did_you_mean_1;
            case "watchOptions":
                return global::Ts.Diagnostics.Diagnostics.Unknown_watch_option_0_Did_you_mean_1;
            case "typeAcquisition":
                return global::Ts.Diagnostics.Diagnostics.Unknown_type_acquisition_option_0_Did_you_mean_1;
            case "buildOptions":
                return global::Ts.Diagnostics.Diagnostics.Unknown_build_option_0_Did_you_mean_1;
            default:
                return null;
        }
    }
}

// commandLineParser.createUnknownOptionError lives on errors.go in Go; kept here as a
// partial-class member (the type is declared in CommandLineParser.cs).
internal sealed partial class commandLineParser
{
    internal Diagnostic createUnknownOptionError(
        string unknownOption,
        string unknownOptionErrorText,
        Node? node,
        SourceFile? sourceFile)
    {
        return Errors.createUnknownOptionError(
            unknownOption,
            UnknownOptionDiagnostic(),
            unknownOptionErrorText,
            node,
            sourceFile,
            AlternateMode());
    }
}
