// Ported from tsgo internal/tsoptions/commandlineoption.go

using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;
using Ts.Diagnostics;

namespace Ts.TsOptions;

// Go: `type CommandLineOptionKind string` with typed string constants. The underlying
// type is string, so the field stays a plain string (05-conventions.md §2.3) and the
// constants are surfaced via `using static Ts.TsOptions.CommandLineOptionKind` in the
// files that build declaration tables. Comparisons `opt.Kind == "list"` and the
// `string(kind)` conversion (errors.go:35) are therefore identity.
public static class CommandLineOptionKind
{
    public const string CommandLineOptionTypeString = "string";
    public const string CommandLineOptionTypeNumber = "number";
    public const string CommandLineOptionTypeBoolean = "boolean";
    public const string CommandLineOptionTypeObject = "object";
    public const string CommandLineOptionTypeList = "list";
    public const string CommandLineOptionTypeListOrElement = "listOrElement";
    public const string CommandLineOptionTypeEnum = "enum"; // map
}

// Go: `type extraValidation string`.
public static class ExtraValidation
{
    public const string extraValidationNone = "";
    public const string extraValidationSpec = "spec";
    public const string extraValidationLocale = "locale";
}

// PORT-NOTE: Go's CommandLineOption is a struct used almost exclusively via pointer
// (`[]*CommandLineOption`), and identity comparisons like `option == extendsOptionDeclaration`
// and `buildOption == &TscBuildOption` are load-bearing. A sealed reference class preserves
// that reference identity exactly. Exported Go fields → public PascalCase fields; unexported
// fields keep their camelCase spelling as internal (cross-file within the assembly).
public sealed class CommandLineOption
{
    public string Name = "";
    public string ShortName = "";
    public string Kind = "";

    // used in parsing
    public bool IsFilePath;
    public bool IsTSConfigOnly;
    public bool IsCommandLineOnly;

    // used in output
    public Message? Description;
    public object? DefaultValueDescription;
    public bool ShowInSimplifiedHelpView;

    // used in output in serializing and generate tsconfig
    public Message? Category;

    // What kind of extra validation `validateJsonOptionValue` should do
    internal string extraValidation = "";

    // checks that option with number type has value >= minValue
    internal int minValue;

    // true or undefined
    // used for configDirTemplateSubstitutionOptions
    internal bool allowConfigDirTemplateSubstitution;

    // used for filter in compilerrunner
    public bool AffectsDeclarationPath;
    public bool AffectsProgramStructure;
    public bool AffectsSemanticDiagnostics;
    public bool AffectsBuildInfo;
    public bool AffectsBindDiagnostics;
    public bool AffectsSourceFile;
    public bool AffectsModuleResolution;
    public bool AffectsEmit;

    internal bool allowJsFlag;
    internal bool strictFlag;

    // used in transpileoptions worker
    // todo: revisit to see if this can be reduced to boolean
    internal Tristate transpileOptionValue;

    // used for CommandLineOptionTypeList
    internal bool listPreserveFalsyValues;

    // used for compilerOptionsDeclaration
    public CommandLineOptionNameMap? ElementOptions;

    public Set<string>? DeprecatedKeys()
    {
        if (Kind != CommandLineOptionKind.CommandLineOptionTypeEnum)
        {
            return null;
        }
        return commandLineOptionDeprecated.GetValueOrDefault(Name);
    }

    public OrderedMap<string, object?>? EnumMap()
    {
        if (Kind != CommandLineOptionKind.CommandLineOptionTypeEnum)
        {
            return null;
        }
        return commandLineOptionEnumMap.GetValueOrDefault(Name);
    }

    public CommandLineOption? Elements()
    {
        if (Kind != CommandLineOptionKind.CommandLineOptionTypeList && Kind != CommandLineOptionKind.CommandLineOptionTypeListOrElement)
        {
            return null;
        }
        return commandLineOptionElements.GetValueOrDefault(Name);
    }

    public bool DisallowNullOrUndefined()
    {
        return Name == "extends";
    }

    // CommandLineOption.Elements()
    private static readonly Dictionary<string, CommandLineOption> commandLineOptionElements = new()
    {
        ["lib"] = new CommandLineOption
        {
            Name = "lib",
            Kind = CommandLineOptionKind.CommandLineOptionTypeEnum, // libMap,
            DefaultValueDescription = Tristate.Unknown,
        },
        ["rootDirs"] = new CommandLineOption
        {
            Name = "rootDirs",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
            IsFilePath = true,
        },
        ["typeRoots"] = new CommandLineOption
        {
            Name = "typeRoots",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
            IsFilePath = true,
        },
        ["types"] = new CommandLineOption
        {
            Name = "types",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        ["moduleSuffixes"] = new CommandLineOption
        {
            Name = "moduleSuffixes",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        ["customConditions"] = new CommandLineOption
        {
            Name = "condition",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        ["plugins"] = new CommandLineOption
        {
            Name = "plugin",
            Kind = CommandLineOptionKind.CommandLineOptionTypeObject,
        },
        // For tsconfig root options
        ["references"] = new CommandLineOption
        {
            Name = "references",
            Kind = CommandLineOptionKind.CommandLineOptionTypeObject,
        },
        ["files"] = new CommandLineOption
        {
            Name = "files",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        ["include"] = new CommandLineOption
        {
            Name = "include",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        ["exclude"] = new CommandLineOption
        {
            Name = "exclude",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        ["extends"] = new CommandLineOption
        {
            Name = "extends",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
        // For Watch options
        ["excludeDirectories"] = new CommandLineOption
        {
            Name = "excludeDirectory",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
            IsFilePath = true,
            extraValidation = ExtraValidation.extraValidationSpec,
        },
        ["excludeFiles"] = new CommandLineOption
        {
            Name = "excludeFile",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
            IsFilePath = true,
            extraValidation = ExtraValidation.extraValidationSpec,
        },
        // Test infra options
        ["libFiles"] = new CommandLineOption
        {
            Name = "libFiles",
            Kind = CommandLineOptionKind.CommandLineOptionTypeString,
        },
    };

    // CommandLineOption.EnumMap()
    private static readonly Dictionary<string, OrderedMap<string, object?>> commandLineOptionEnumMap = new()
    {
        ["lib"] = EnumMaps.LibMap,
        ["moduleResolution"] = EnumMaps.ModuleResolutionOptionMap,
        ["module"] = EnumMaps.ModuleOptionMap,
        ["target"] = EnumMaps.TargetOptionMap,
        ["moduleDetection"] = EnumMaps.ModuleDetectionOptionMap,
        ["jsx"] = EnumMaps.JsxOptionMap,
        ["newLine"] = EnumMaps.NewLineOptionMap,
        ["watchFile"] = EnumMaps.WatchFileEnumMap,
        ["watchDirectory"] = EnumMaps.WatchDirectoryEnumMap,
        ["fallbackPolling"] = EnumMaps.FallbackEnumMap,
    };

    // CommandLineOption.DeprecatedKeys()
    private static readonly Dictionary<string, Set<string>> commandLineOptionDeprecated = new()
    {
        ["module"] = Set.FromItems("none", "amd", "system", "umd"),
        ["moduleResolution"] = Set.FromItems("node", "classic", "node10"),
        ["target"] = Set.FromItems("es5"),
    };
}
