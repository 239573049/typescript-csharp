// Ported from tsgo internal/execute/tsc/help.go and the colors helper from tsc/diagnostics.go.

using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Core;
using Ts.Diagnostics;
using Diag = global::Ts.Diagnostics.Diagnostics;

namespace Ts.Execute;

// diagnostics.go:63-128
internal sealed class Colors
{
    internal bool showColors;

    internal bool isWindows;
    internal bool isWindowsTerminal;
    internal bool isVSCode;
    internal bool supportsRicherColors;

    // diagnostics.go:72-91
    internal static Colors CreateColors(ISystem sys)
    {
        if (!DefaultIsPretty(sys))
        {
            return new Colors { showColors = false };
        }

        var os = sys.GetEnvironmentVariable("OS");
        var isWindows = os.Contains("windows", StringComparison.OrdinalIgnoreCase);
        var isWindowsTerminal = sys.GetEnvironmentVariable("WT_SESSION").Length > 0;
        var isVSCode = sys.GetEnvironmentVariable("TERM_PROGRAM") == "vscode";
        var supportsRicherColors = sys.GetEnvironmentVariable("COLORTERM") == "truecolor" || sys.GetEnvironmentVariable("TERM") == "xterm-256color";

        return new Colors
        {
            showColors = true,
            isWindows = isWindows,
            isWindowsTerminal = isWindowsTerminal,
            isVSCode = isVSCode,
            supportsRicherColors = supportsRicherColors,
        };
    }

    // diagnostics.go:46-54
    internal static bool DefaultIsPretty(ISystem sys)
    {
        if (sys.GetEnvironmentVariable("NO_COLOR").Length > 0)
        {
            return false;
        }
        if (sys.GetEnvironmentVariable("FORCE_COLOR").Length > 0)
        {
            return true;
        }
        return sys.WriteOutputIsTTY();
    }

    internal string Bold(string str) => showColors ? "\x1b[1m" + str + "\x1b[22m" : str;

    internal string Blue(string str)
    {
        if (!showColors)
        {
            return str;
        }
        // Effectively Powershell and Command prompt users use cyan instead
        // of blue because the default theme doesn't show blue with enough contrast.
        if (isWindows && !isWindowsTerminal && !isVSCode)
        {
            return BrightWhite(str);
        }
        return "\x1b[94m" + str + "\x1b[39m";
    }

    internal string BlueBackground(string str)
    {
        if (!showColors)
        {
            return str;
        }
        if (supportsRicherColors)
        {
            return "\x1B[48;5;68m" + str + "\x1B[39;49m";
        }
        return "\x1b[44m" + str + "\x1B[39;49m";
    }

    internal string BrightWhite(string str) => showColors ? "\x1b[97m" + str + "\x1b[39m" : str;
}

public static class Help
{
    // help.go:15-17
    public static void PrintVersion(ISystem sys, global::Ts.Locale.Locale locale)
    {
        sys.Writer().Write(Diag.Version_0.Localize(locale, global::Ts.Core.TsVersion.Version()) + "\n");
    }

    // help.go:19-25
    public static void PrintHelp(ISystem sys, global::Ts.Locale.Locale locale, global::Ts.TsOptions.ParsedCommandLine commandLine)
    {
        if (commandLine.CompilerOptions()!.All.IsFalseOrUnknown())
        {
            PrintEasyHelp(sys, locale, GetOptionsForHelp(commandLine));
        }
        else
        {
            PrintAllHelp(sys, locale, GetOptionsForHelp(commandLine));
        }
    }

    // help.go:27-42
    private static global::Ts.TsOptions.CommandLineOption[] GetOptionsForHelp(global::Ts.TsOptions.ParsedCommandLine commandLine)
    {
        // Sort our options by their names, (e.g. "--noImplicitAny" comes before "--watch")
        var opts = new List<global::Ts.TsOptions.CommandLineOption>(global::Ts.TsOptions.DeclsCompiler.OptionsDeclarations);
        opts.Add(global::Ts.TsOptions.DeclsBuild.TscBuildOption);

        if (commandLine.CompilerOptions()!.All.IsTrue())
        {
            opts.Sort((a, b) => string.CompareOrdinal(global::Ts.StringUtil.StringUtil.ToLowerJS(a.Name), global::Ts.StringUtil.StringUtil.ToLowerJS(b.Name)));
            return opts.ToArray();
        }
        return opts.Where(opt => opt.ShowInSimplifiedHelpView).ToArray();
    }

    // help.go:44-65
    internal static List<string> GetHeader(ISystem sys, string message)
    {
        var colors = Colors.CreateColors(sys);
        var header = new List<string>(3);
        var terminalWidth = sys.GetWidthOfTerminal();
        const string tsIcon = "     ";
        const string tsIconTS = "  TS ";
        var tsIconLength = tsIcon.Length;

        var tsIconFirstLine = colors.BlueBackground(tsIcon);
        var tsIconSecondLine = colors.BlueBackground(colors.BrightWhite(tsIconTS));
        // If we have enough space, print TS icon.
        if (terminalWidth >= message.Length + tsIconLength)
        {
            // right align of the icon is 120 at most.
            var rightAlign = terminalWidth > 120 ? 120 : terminalWidth;
            var leftAlign = rightAlign - tsIconLength;
            header.Add(message.PadRight(leftAlign));
            header.Add(tsIconFirstLine);
            header.Add("\n");
            header.Add(new string(' ', leftAlign));
            header.Add(tsIconSecondLine);
            header.Add("\n");
        }
        else
        {
            header.Add(message);
            header.Add("\n");
            header.Add("\n");
        }
        return header;
    }

    // help.go:67-108
    private static void PrintEasyHelp(ISystem sys, global::Ts.Locale.Locale locale, global::Ts.TsOptions.CommandLineOption[] simpleOptions)
    {
        var colors = Colors.CreateColors(sys);
        var output = new List<string>();
        void Example(string[] examples, global::Ts.Diagnostics.Message desc)
        {
            foreach (var example in examples)
            {
                output.Add("  ");
                output.Add(colors.Blue(example));
                output.Add("\n");
            }
            output.Add("  ");
            output.Add(desc.Localize(locale));
            output.Add("\n");
            output.Add("\n");
        }

        var msg = Diag.X_tsc_Colon_The_TypeScript_Compiler.Localize(locale) + " - " + Diag.Version_0.Localize(locale, global::Ts.Core.TsVersion.Version());
        output.AddRange(GetHeader(sys, msg));

        output.Add(colors.Bold(Diag.COMMON_COMMANDS.Localize(locale)));
        output.Add("\n");
        output.Add("\n");

        Example(["tsc"], Diag.Compiles_the_current_project_tsconfig_json_in_the_working_directory);
        Example(["tsc app.ts util.ts"], Diag.Ignoring_tsconfig_json_compiles_the_specified_files_with_default_compiler_options);
        Example(["tsc -b"], Diag.Build_a_composite_project_in_the_working_directory);
        Example(["tsc --init"], Diag.Creates_a_tsconfig_json_with_the_recommended_settings_in_the_working_directory);
        Example(["tsc -p ./path/to/tsconfig.json"], Diag.Compiles_the_TypeScript_project_located_at_the_specified_path);
        Example(["tsc --help --all"], Diag.An_expanded_version_of_this_information_showing_all_possible_compiler_options);
        Example(["tsc --noEmit", "tsc --target esnext"], Diag.Compiles_the_current_project_with_additional_settings);

        var cliCommands = new List<global::Ts.TsOptions.CommandLineOption>();
        var configOpts = new List<global::Ts.TsOptions.CommandLineOption>();
        foreach (var opt in simpleOptions)
        {
            if (opt.IsCommandLineOnly || ReferenceEquals(opt.Category, Diag.Command_line_Options))
            {
                cliCommands.Add(opt);
            }
            else
            {
                configOpts.Add(opt);
            }
        }

        output.AddRange(GenerateSectionOptionsOutput(sys, locale, Diag.COMMAND_LINE_FLAGS.Localize(locale), cliCommands, /*subCategory*/ false, /*beforeOptionsDescription*/ null, /*afterOptionsDescription*/ null));

        var after = Diag.You_can_learn_about_all_of_the_compiler_options_at_0.Localize(locale, "https://aka.ms/tsc");
        output.AddRange(GenerateSectionOptionsOutput(sys, locale, Diag.COMMON_COMPILER_OPTIONS.Localize(locale), configOpts, /*subCategory*/ false, null, after));

        foreach (var chunk in output)
        {
            sys.Writer().Write(chunk);
        }
    }

    // help.go:110-133
    private static void PrintAllHelp(ISystem sys, global::Ts.Locale.Locale locale, global::Ts.TsOptions.CommandLineOption[] options)
    {
        var output = new List<string>();
        var msg = Diag.X_tsc_Colon_The_TypeScript_Compiler.Localize(locale) + " - " + Diag.Version_0.Localize(locale, global::Ts.Core.TsVersion.Version());
        output.AddRange(GetHeader(sys, msg));

        // ALL COMPILER OPTIONS section
        var afterCompilerOptions = Diag.You_can_learn_about_all_of_the_compiler_options_at_0.Localize(locale, "https://aka.ms/tsc");
        output.AddRange(GenerateSectionOptionsOutput(sys, locale, Diag.ALL_COMPILER_OPTIONS.Localize(locale), options, true, null, afterCompilerOptions));

        // WATCH OPTIONS section
        var beforeWatchOptions = Diag.Including_watch_w_will_start_watching_the_current_project_for_the_file_changes_Once_set_you_can_config_watch_mode_with_Colon.Localize(locale);
        output.AddRange(GenerateSectionOptionsOutput(sys, locale, Diag.WATCH_OPTIONS.Localize(locale), global::Ts.TsOptions.DeclsWatch.OptionsForWatch, false, beforeWatchOptions, null));

        // BUILD OPTIONS section
        var beforeBuildOptions = Diag.Using_build_b_will_make_tsc_behave_more_like_a_build_orchestrator_than_a_compiler_This_is_used_to_trigger_building_composite_projects_which_you_can_learn_more_about_at_0.Localize(locale, "https://aka.ms/tsc-composite-builds");
        var buildOptions = global::Ts.TsOptions.DeclsBuild.OptionsForBuild.Where(option => !ReferenceEquals(option, global::Ts.TsOptions.DeclsBuild.TscBuildOption)).ToArray();
        output.AddRange(GenerateSectionOptionsOutput(sys, locale, Diag.BUILD_OPTIONS.Localize(locale), buildOptions, false, beforeBuildOptions, null));

        foreach (var chunk in output)
        {
            sys.Writer().Write(chunk);
        }
    }

    // help.go:135-147
    public static void PrintBuildHelp(ISystem sys, global::Ts.Locale.Locale locale, global::Ts.TsOptions.CommandLineOption[] buildOptions)
    {
        var output = new List<string>();
        output.AddRange(GetHeader(sys, Diag.X_tsc_Colon_The_TypeScript_Compiler.Localize(locale) + " - " + Diag.Version_0.Localize(locale, global::Ts.Core.TsVersion.Version())));
        var before = Diag.Using_build_b_will_make_tsc_behave_more_like_a_build_orchestrator_than_a_compiler_This_is_used_to_trigger_building_composite_projects_which_you_can_learn_more_about_at_0.Localize(locale, "https://aka.ms/tsc-composite-builds");
        var options = buildOptions.Where(option => !ReferenceEquals(option, global::Ts.TsOptions.DeclsBuild.TscBuildOption)).ToArray();
        output.AddRange(GenerateSectionOptionsOutput(sys, locale, Diag.BUILD_OPTIONS.Localize(locale), options, false, before, null));

        foreach (var chunk in output)
        {
            sys.Writer().Write(chunk);
        }
    }

    // help.go:149-192
    private static List<string> GenerateSectionOptionsOutput(
        ISystem sys,
        global::Ts.Locale.Locale locale,
        string sectionName,
        IReadOnlyList<global::Ts.TsOptions.CommandLineOption> options,
        bool subCategory,
        string? beforeOptionsDescription,
        string? afterOptionsDescription)
    {
        var output = new List<string> { Colors.CreateColors(sys).Bold(sectionName), "\n", "\n" };

        if (beforeOptionsDescription != null)
        {
            output.Add(beforeOptionsDescription);
            output.Add("\n");
            output.Add("\n");
        }
        if (!subCategory)
        {
            output.AddRange(GenerateGroupOptionOutput(sys, locale, options));
            if (afterOptionsDescription != null)
            {
                output.Add(afterOptionsDescription);
                output.Add("\n");
                output.Add("\n");
            }
            return output;
        }
        var categoryMap = new Dictionary<string, List<global::Ts.TsOptions.CommandLineOption>>(StringComparer.Ordinal);
        var categoryOrder = new List<string>();
        foreach (var option in options)
        {
            if (option.Category == null)
            {
                continue;
            }
            var curCategory = option.Category.Localize(locale);
            if (!categoryMap.TryGetValue(curCategory, out var list))
            {
                categoryOrder.Add(curCategory);
                list = new List<global::Ts.TsOptions.CommandLineOption>();
                categoryMap[curCategory] = list;
            }
            list.Add(option);
        }
        foreach (var key in categoryOrder)
        {
            var value = categoryMap[key];
            output.Add("### ");
            output.Add(key);
            output.Add("\n");
            output.Add("\n");
            output.AddRange(GenerateGroupOptionOutput(sys, locale, value));
        }
        if (afterOptionsDescription != null)
        {
            output.Add(afterOptionsDescription);
            output.Add("\n");
            output.Add("\n");
        }

        return output;
    }

    // help.go:194-220
    private static List<string> GenerateGroupOptionOutput(ISystem sys, global::Ts.Locale.Locale locale, IReadOnlyList<global::Ts.TsOptions.CommandLineOption> optionsList)
    {
        var maxLength = 0;
        foreach (var option in optionsList)
        {
            maxLength = Math.Max(GetDisplayNameTextOfOption(option).Length, maxLength);
        }

        // left part should be right align, right part should be left align

        // assume 2 space between left margin and left part.
        var rightAlignOfLeftPart = maxLength + 2;
        // assume 2 space between left and right part
        var leftAlignOfRightPart = rightAlignOfLeftPart + 2;

        var lines = new List<string>();
        foreach (var option in optionsList)
        {
            lines.AddRange(GenerateOptionOutput(sys, locale, option, rightAlignOfLeftPart, leftAlignOfRightPart));
        }

        // make sure always a blank line in the end.
        if (lines.Count < 2 || lines[^2] != "\n")
        {
            lines.Add("\n");
        }

        return lines;
    }

    // help.go:222-293
    private static List<string> GenerateOptionOutput(
        ISystem sys,
        global::Ts.Locale.Locale locale,
        global::Ts.TsOptions.CommandLineOption option,
        int rightAlignOfLeft,
        int leftAlignOfRight)
    {
        var text = new List<string>();
        var colors = Colors.CreateColors(sys);

        // name and description
        var name = GetDisplayNameTextOfOption(option);

        // value type and possible value
        var valueCandidates = GetValueCandidate(sys, locale, option);

        string defaultValueDescription;
        if (option.DefaultValueDescription is global::Ts.Diagnostics.Message msg)
        {
            defaultValueDescription = msg.Localize(locale);
        }
        else
        {
            defaultValueDescription = FormatDefaultValue(
                option.DefaultValueDescription,
                option.Kind == "list" || option.Kind == "listOrElement" ? option.Elements()! : option);
        }

        var terminalWidth = sys.GetWidthOfTerminal();

        if (terminalWidth >= 80)
        {
            var description = option.Description?.Localize(locale) ?? "";
            text.AddRange(GetPrettyOutput(colors, name, description, rightAlignOfLeft, leftAlignOfRight, terminalWidth, /*colorLeft*/ true));
            text.Add("\n");
            if (ShowAdditionalInfoOutput(valueCandidates, option))
            {
                if (valueCandidates != null)
                {
                    text.AddRange(GetPrettyOutput(colors, valueCandidates.valueType, valueCandidates.possibleValues, rightAlignOfLeft, leftAlignOfRight, terminalWidth, /*colorLeft*/ false));
                    text.Add("\n");
                }
                if (defaultValueDescription.Length > 0)
                {
                    text.AddRange(GetPrettyOutput(colors, Diag.X_default_Colon.Localize(locale), defaultValueDescription, rightAlignOfLeft, leftAlignOfRight, terminalWidth, /*colorLeft*/ false));
                    text.Add("\n");
                }
            }
            text.Add("\n");
        }
        else
        {
            text.Add(colors.Blue(name));
            text.Add("\n");
            if (option.Description != null)
            {
                text.Add(option.Description.Localize(locale));
            }
            text.Add("\n");
            if (ShowAdditionalInfoOutput(valueCandidates, option))
            {
                if (valueCandidates != null)
                {
                    text.Add(valueCandidates.valueType);
                    text.Add(" ");
                    text.Add(valueCandidates.possibleValues);
                }
                if (defaultValueDescription.Length > 0)
                {
                    if (valueCandidates != null)
                    {
                        text.Add("\n");
                    }
                    text.Add(Diag.X_default_Colon.Localize(locale));
                    text.Add(" ");
                    text.Add(defaultValueDescription);
                }

                text.Add("\n");
            }
            text.Add("\n");
        }

        return text;
    }

    // help.go:295-311
    private static string FormatDefaultValue(object? defaultValue, global::Ts.TsOptions.CommandLineOption option)
    {
        if (defaultValue == null || (defaultValue is Tristate t && t == Tristate.Unknown))
        {
            return "undefined";
        }

        if (option.Kind == "enum")
        {
            // e.g. ScriptTarget.ES2015 -> "es6/es2015"
            var names = new List<string>();
            foreach (var (name, value) in global::Ts.Collections.OrderedMapExtensions.Entries(option.EnumMap()))
            {
                if (Equals(value, defaultValue))
                {
                    names.Add(name);
                }
            }
            return string.Join("/", names);
        }
        return defaultValue switch
        {
            string s => s,
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => defaultValue.ToString() ?? "",
        };
    }

    // help.go:313-317
    private sealed class ValueCandidate
    {
        // "one or more" or "any of"
        internal string valueType = "";
        internal string possibleValues = "";
    }

    // help.go:319-330
    private static bool ShowAdditionalInfoOutput(ValueCandidate? valueCandidates, global::Ts.TsOptions.CommandLineOption option)
    {
        if (ReferenceEquals(option.Category, Diag.Command_line_Options))
        {
            return false;
        }
        if (valueCandidates != null && valueCandidates.possibleValues == "string" &&
            (option.DefaultValueDescription == null ||
                (option.DefaultValueDescription as string) == "false" ||
                (option.DefaultValueDescription as string) == "n/a"))
        {
            return false;
        }
        return true;
    }

    // help.go:332-364
    private static ValueCandidate? GetValueCandidate(ISystem sys, global::Ts.Locale.Locale locale, global::Ts.TsOptions.CommandLineOption option)
    {
        // option.type might be "string" | "number" | "boolean" | "object" | "list" | Map<string, number | string>
        if (option.Kind == "object")
        {
            return null;
        }

        var res = new ValueCandidate();
        if (option.Kind == "listOrElement")
        {
            throw new InvalidOperationException("no value candidate for list or element");
        }

        switch (option.Kind)
        {
            case "string":
            case "number":
            case "boolean":
                res.valueType = Diag.X_type_Colon.Localize(locale);
                break;
            case "list":
                res.valueType = Diag.X_one_or_more_Colon.Localize(locale);
                break;
            default:
                res.valueType = Diag.X_one_of_Colon.Localize(locale);
                break;
        }

        res.possibleValues = GetPossibleValues(option);

        return res;
    }

    // help.go:366-395
    private static string GetPossibleValues(global::Ts.TsOptions.CommandLineOption option)
    {
        switch (option.Kind)
        {
            case "string":
            case "number":
            case "boolean":
                return option.Kind;
            case "list":
            case "listOrElement":
                return GetPossibleValues(option.Elements()!);
            case "object":
                return "";
            default:
            {
                // Map<string, number | string>
                // Group synonyms: es6/es2015
                var enumMap = option.EnumMap();
                var inverted = new global::Ts.Collections.OrderedMap<object, List<string>>();
                var deprecatedKeys = option.DeprecatedKeys();

                foreach (var (name, value) in global::Ts.Collections.OrderedMapExtensions.Entries(enumMap))
                {
                    if (deprecatedKeys == null || !global::Ts.Collections.SetExtensions.Has(deprecatedKeys, name))
                    {
                        var key = value ?? NullEnumValueSentinel;
                        if (!inverted.TryGet(key, out var list))
                        {
                            list = new List<string>();
                            inverted.Set(key, list);
                        }
                        list!.Add(name);
                    }
                }
                var syns = new List<string>();
                foreach (var (_, synonyms) in global::Ts.Collections.OrderedMapExtensions.Entries(inverted))
                {
                    syns.Add(string.Join("/", synonyms));
                }
                return string.Join(", ", syns);
            }
        }
    }

    private static readonly object NullEnumValueSentinel = new();

    // help.go:397-422
    private static List<string> GetPrettyOutput(Colors colors, string left, string right, int rightAlignOfLeft, int leftAlignOfRight, int terminalWidth, bool colorLeft)
    {
        // !!! How does terminalWidth interact with UTF-8 encoding? Strada just assumed UTF-16.
        var res = new List<string>(4);
        var isFirstLine = true;
        var remainRight = right;
        var rightCharacterNumber = terminalWidth - leftAlignOfRight;
        while (remainRight.Length > 0)
        {
            string curLeft;
            if (isFirstLine)
            {
                curLeft = left.PadLeft(rightAlignOfLeft);
                curLeft = curLeft.PadRight(leftAlignOfRight);
                if (colorLeft)
                {
                    curLeft = colors.Blue(curLeft);
                }
            }
            else
            {
                curLeft = new string(' ', leftAlignOfRight);
            }

            var idx = Math.Min(rightCharacterNumber, remainRight.Length);
            var curRight = remainRight[..idx];
            remainRight = remainRight[idx..];
            res.Add(curLeft);
            res.Add(curRight);
            res.Add("\n");
            isFirstLine = false;
        }
        return res;
    }

    // help.go:424-426
    private static string GetDisplayNameTextOfOption(global::Ts.TsOptions.CommandLineOption option) =>
        "--" + option.Name + (option.ShortName.Length > 0 ? ", -" + option.ShortName : "");
}
