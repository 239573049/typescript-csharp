// Ported from tsgo internal/ls/lsutil/userpreferences.go (whole file).
// PORT-NOTE: Go populates/serializes the struct by walking `raw:` / `config:` /
// `fallbackConfig:` struct tags with reflection (userpreferences.go:286-862). Reflection is
// banned under Native AOT (plan/07), so the tag walk becomes the hand-written FieldInfos table
// below: one entry per tagged field, in Go struct-field order (embedded/untagged structs
// flattened exactly like collectFieldInfos), carrying the same tag strings plus a typed
// getter/setter. Go's decoded `any` values (map[string]any / []any / float64 / bool / string)
// are reproduced by ToAny(JsonElement) so withConfig/setFieldFromValue port line-for-line.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Ts.Core;

namespace Ts.Ls;

// userpreferences.go:45 — UserPreferences represents TypeScript language service preferences.
public sealed class UserPreferences
{
    // userpreferences.go:46 — FormatCodeSettings (Go's first field; carries the format
    // engine settings, consumed by LanguageService.FormatOptions and the LSP formatting
    // entry points).
    public global::Ts.Format.FormatCodeSettings FormatCodeSettings = global::Ts.Format.FormatCodeSettings.GetDefaultFormatCodeSettings();

    // userpreferences.go:148 — UseAliasesForRename
    // (raw:"providePrefixAndSuffixTextForRename" config:"preferences.useAliasesForRenames").
    public Tristate UseAliasesForRename;
    // userpreferences.go:149 — AllowRenameOfImportPath (raw:"allowRenameOfImportPath").
    public Tristate AllowRenameOfImportPath;

    // userpreferences.go:54 — MaximumHoverLength (raw:"maximumHoverLength") // !!!
    public int MaximumHoverLength;
    // userpreferences.go:49 — LazyConfiguredProjectsFromExternalProject
    // (raw:"lazyConfiguredProjectsFromExternalProject") // !!!
    public Tristate LazyConfiguredProjectsFromExternalProject;

    // userpreferences.go:144 — AllowTextChangesInNewFiles (raw:"allowTextChangesInNewFiles") // !!!
    public Tristate AllowTextChangesInNewFiles;
    // userpreferences.go:153 — ProvideRefactorNotApplicableReason
    // (raw:"provideRefactorNotApplicableReason") // !!!
    public Tristate ProvideRefactorNotApplicableReason;
    // userpreferences.go:175 — DisableSuggestions (raw:"disableSuggestions") // !!!
    public Tristate DisableSuggestions;
    // userpreferences.go:176 — DisableLineTextInReferences (raw:"disableLineTextInReferences") // !!!
    public Tristate DisableLineTextInReferences;
    // userpreferences.go:177 — DisplayPartsForJSDoc (raw:"displayPartsForJSDoc") // !!!
    public Tristate DisplayPartsForJSDoc;

    // userpreferences.go:165 — PreferGoToSourceDefinition (raw:"preferGoToSourceDefinition").
    public bool PreferGoToSourceDefinition;
    // userpreferences.go:200 — ExcludeLibrarySymbolsInNavTo
    // (raw:"excludeLibrarySymbolsInNavTo" config:"workspaceSymbols.excludeLibrarySymbols").
    public Tristate ExcludeLibrarySymbolsInNavTo;

    // userpreferences.go:173 — EnableFormatting
    // (raw:"formatEnabled" config:"format.enabled" fallbackConfig:"format.enable").
    public Tristate EnableFormatting;

    // userpreferences.go:48 — QuotePreference (string enum: "" | "auto" | "double" | "single").
    public string QuotePreference = "";
    // userpreferences.go:78 — JsxAttributeCompletionStyle ("auto" | "braces" | "none").
    public string JsxAttributeCompletionStyle = "";
    // If enabled, TypeScript will search through all external modules' exports and add them to the completions list.
    // This affects lone identifier completions but not completions on the right hand side of `obj.`.
    public Tristate IncludeCompletionsForModuleExports;
    // Enables auto-import-style completions on partially-typed import statements. E.g., allows
    // `import write|` to be completed to `import { writeFile } from "fs"`.
    public Tristate IncludeCompletionsForImportStatements;
    // Unless this option is `false`,  member completion lists triggered with `.` will include entries
    // on potentially-null and potentially-undefined values, with insertion text to replace
    // preceding `.` tokens with `?.`.
    public Tristate IncludeAutomaticOptionalChainCompletions;
    // If enabled, completions for class members (e.g. methods and properties) will include
    // a whole declaration for the member.
    public Tristate IncludeCompletionsWithClassMemberSnippets;
    // If enabled, object literal methods will have a method declaration completion entry in addition
    // to the regular completion entry containing just the method name.
    public Tristate IncludeCompletionsWithObjectLiteralMethodSnippets;
    // If enabled, completions will include snippet-style entries (e.g. class member snippets).
    public Tristate IncludeCompletionsWithSnippetText;
    // userpreferences.go:89 — ImportModuleSpecifierEnding: determines whether we import
    // `foo/index.ts` as "foo", "foo/index", or "foo/index.js".
    public global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference ImportModuleSpecifierEnding;
    // userpreferences.go:85 — ImportModuleSpecifierPreference.
    public global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference ImportModuleSpecifierPreference;
    // userpreferences.go:88 — AutoImportSpecifierExcludeRegexes.
    public string[]? AutoImportSpecifierExcludeRegexes;
    // userpreferences.go:89 — AutoImportFileExcludePatterns.
    public string[]? AutoImportFileExcludePatterns;
    // userpreferences.go:90 — AutoImportEntrypointDirectorySearch.
    public Tristate AutoImportEntrypointDirectorySearch;
    // userpreferences.go:91 — PreferTypeOnlyAutoImports.
    public Tristate PreferTypeOnlyAutoImports;

    // ------- InlayHints (userpreferences.go:155-157) -------
    public InlayHintsPreferences InlayHints;

    // ------- CodeLens (userpreferences.go:159-161) -------
    public CodeLensUserPreferences CodeLens;

    // userpreferences.go:79 — EnableAutoClosingTags
    // (raw:"autoClosingTags" config:"autoClosingTags.enabled" fallbackConfig:"autoClosingTags").
    public Tristate EnableAutoClosingTags;
    // userpreferences.go:174 — EnableValidation (`validate.enabled`).
    public Tristate EnableValidation;
    // userpreferences.go:177 — ReportStyleChecksAsWarnings
    // (raw:"reportStyleChecksAsWarnings" config:"reportStyleChecksAsWarnings").
    public Tristate ReportStyleChecksAsWarnings;
    // userpreferences.go:183 — DisableAutomaticTypeAcquisition is the deprecated setting
    // from typescript.disableAutomaticTypeAcquisition.
    public Tristate DisableAutomaticTypeAcquisition;
    // userpreferences.go:186 — AutomaticTypeAcquisitionEnabled is the unified setting from
    // tsserver.automaticTypeAcquisition.enabled under the js/ts section.
    public Tristate AutomaticTypeAcquisitionEnabled;
    // userpreferences.go:192 — CustomConfigFileName specifies a custom config file name to
    // use before defaulting to tsconfig.json/jsconfig.json.
    public string CustomConfigFileName = "";

    // userpreferences.go:198-203 — IsATADisabled returns whether Automatic Type Acquisition
    // is disabled based on user preferences. It checks the unified setting
    // (tsserver.automaticTypeAcquisition.enabled) first, then falls back to the deprecated
    // setting (disableAutomaticTypeAcquisition).
    public bool IsATADisabled()
    {
        if (!AutomaticTypeAcquisitionEnabled.IsUnknown())
        {
            return !AutomaticTypeAcquisitionEnabled.IsTrue();
        }
        return DisableAutomaticTypeAcquisition.IsTrue();
    }

    // ------- OrganizeImports (userpreferences.go:93-141) -------

    // Indicates which deterministic preset should be used to sort imports.
    // "auto" detects the existing ordinal case sensitivity where possible.
    public OrganizeImportsSort OrganizeImportsSort; // !!!
    // Indicates whether imports should be organized in a case-insensitive manner.
    //
    // Default: TSUnknown ("auto" in strada), will perform detection
    public Tristate OrganizeImportsIgnoreCase; // !!!
    // Indicates whether imports should be organized via an "ordinal" (binary) comparison using the numeric value of their
    // code points, or via "unicode" natural sorting. This implementation is locale-agnostic and approximates the practical
    // import-sorting behavior rather than the full Unicode Collation Algorithm.
    //
    // Default: Ordinal
    public OrganizeImportsCollation OrganizeImportsCollation; // !!!
    // Indicates the locale to use for "unicode" collation in legacy clients. This is accepted for compatibility, but
    // currently ignored because organize-import sorting is deterministic and locale-agnostic.
    //
    // This preference is ignored if organizeImportsCollation is not `unicode`.
    //
    // Default: `"en"`
    public string OrganizeImportsLocale = ""; // !!!
    // Indicates whether numeric collation should be used for digit sequences in strings. When `true`, will collate
    // strings such that `a1z < a2z < a100z`. When `false`, will collate strings such that `a1z < a100z < a2z`.
    //
    // This preference is ignored if organizeImportsCollation is not `unicode`.
    //
    // Default: `false`
    public Tristate OrganizeImportsNumericCollation; // !!!
    // Indicates whether accents and other diacritic marks are considered unequal for the purpose of sorting.
    //
    // This preference is ignored if organizeImportsCollation is not `unicode`.
    //
    // Default: `true`
    public Tristate OrganizeImportsAccentCollation; // !!!
    // Indicates whether upper case or lower case should sort first.
    //
    // This permission is ignored if:
    //	- organizeImportsCollation is not `unicode`
    //	- organizeImportsIgnoreCase is `true`
    //	- organizeImportsIgnoreCase is `auto` and the auto-detected case sensitivity is case-insensitive.
    //
    // Default: `false`
    public OrganizeImportsCaseFirst OrganizeImportsCaseFirst; // !!!
    // Indicates where named type-only imports should sort. "inline" sorts named imports without regard to if the import is type-only.
    //
    // Default: `auto`, which defaults to `last`
    public OrganizeImportsTypeOrder OrganizeImportsTypeOrder; // !!!
    // userpreferences.go:80 — EnableJSDocCompletions.
    public Tristate EnableJSDocCompletions;
    // userpreferences.go:81 — GenerateReturnInDocTemplate.
    public Tristate GenerateReturnInDocTemplate;

    // userpreferences.go:866-872 — ModuleSpecifierPreferences.
    public global::Ts.ModuleSpecifiers.UserPreferences ModuleSpecifierPreferences()
    {
        return new global::Ts.ModuleSpecifiers.UserPreferences
        {
            ImportModuleSpecifierPreference = ImportModuleSpecifierPreference,
            ImportModuleSpecifierEnding = ImportModuleSpecifierEnding,
            AutoImportSpecifierExcludeRegexes = AutoImportSpecifierExcludeRegexes,
        };
    }

    // userpreferences.go:874-876 — ParsedAutoImportFileExcludePatterns.
    public global::Ts.Vfs.VfsMatch.SpecMatcher? ParsedAutoImportFileExcludePatterns(bool useCaseSensitiveFileNames)
    {
        return global::Ts.Vfs.VfsMatch.VfsMatch.NewSpecMatcher(AutoImportFileExcludePatterns, "", global::Ts.Vfs.VfsMatch.Usage.Exclude, useCaseSensitiveFileNames);
    }

    // userpreferences.go:878-880 — IsModuleSpecifierExcluded.
    public bool IsModuleSpecifierExcluded(string moduleSpecifier)
    {
        return global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.IsExcludedByRegex(moduleSpecifier, AutoImportSpecifierExcludeRegexes);
    }

    // PORT-NOTE: Go's UserPreferences is a value struct, so assignment/parameter passing/
    // returns copy it; the C# port is a class, so Clone() supplies the struct-copy semantics.
    // Shallow on purpose — it mirrors Go exactly: slices (string[]) share backing arrays,
    // and the nested CodeLens/InlayHints structs copy by value.
    // FormatCodeSettings is a Go value struct too (a C# class here), so it is copied as well.
    public UserPreferences Clone()
    {
        var clone = (UserPreferences)MemberwiseClone();
        clone.FormatCodeSettings = FormatCodeSettings.Clone();
        return clone;
    }

    // userpreferences.go:15-35 — NewDefaultUserPreferences.
    public static UserPreferences NewDefaultUserPreferences() => new()
    {
        FormatCodeSettings = global::Ts.Format.FormatCodeSettings.GetDefaultFormatCodeSettings(),

        IncludeCompletionsForModuleExports = Tristate.True,
        IncludeCompletionsForImportStatements = Tristate.True,
        EnableAutoClosingTags = Tristate.True,
        EnableJSDocCompletions = Tristate.True,
        GenerateReturnInDocTemplate = Tristate.True,

        AllowRenameOfImportPath = Tristate.True,
        ProvideRefactorNotApplicableReason = Tristate.True,
        EnableFormatting = Tristate.True,
        EnableValidation = Tristate.True,
        DisplayPartsForJSDoc = Tristate.True,
        DisableLineTextInReferences = Tristate.True,
        ReportStyleChecksAsWarnings = Tristate.True,

        ExcludeLibrarySymbolsInNavTo = Tristate.True,
    };

    // --- Reflection-based parsing infrastructure (userpreferences.go:286-862) ---

    // PORT-NOTE: stands in for the reflect.Type keys of typeParsers/typeSerializers; plain
    // Bool/Int/String/StringSlice are the reflect.Kind fallbacks of setFieldFromValue/serializeField.
    private enum FieldKind
    {
        Tristate,
        Bool,
        Int,
        String,
        StringSlice,
        IndentStyle,
        SemicolonPreference,
        QuotePreference,
        JsxAttributeCompletionStyle,
        IncludeInlayParameterNameHints,
        OrganizeImportsSort,
        OrganizeImportsCollation,
        OrganizeImportsCaseFirst,
        OrganizeImportsTypeOrder,
        ImportModuleSpecifierPreference,
        ImportModuleSpecifierEndingPreference,
    }

    // userpreferences.go:288-411 — typeParsers.
    private static object ParseTyped(FieldKind kind, object val)
    {
        switch (kind)
        {
            case FieldKind.Tristate:
                if (val is bool b)
                {
                    return b ? Tristate.True : Tristate.False;
                }
                return Tristate.Unknown;
            case FieldKind.IndentStyle:
                return global::Ts.Format.FormatCodeSettings.ParseIndentStyle(val);
            case FieldKind.SemicolonPreference:
                return global::Ts.Format.FormatCodeSettings.ParseSemicolonPreference(val);
            case FieldKind.QuotePreference:
                if (val is string quote)
                {
                    switch (global::Ts.StringUtil.StringUtil.ToLowerJS(quote))
                    {
                        case "auto":
                            return "auto";
                        case "double":
                            return "double";
                        case "single":
                            return "single";
                    }
                }
                return "";
            case FieldKind.JsxAttributeCompletionStyle:
                if (val is string jsx)
                {
                    switch (global::Ts.StringUtil.StringUtil.ToLowerJS(jsx))
                    {
                        case "braces":
                            return "braces";
                        case "none":
                            return "none";
                    }
                }
                return "auto";
            case FieldKind.IncludeInlayParameterNameHints:
                if (val is string hints)
                {
                    switch (hints)
                    {
                        case "all":
                            return IncludeInlayParameterNameHints.All;
                        case "literals":
                            return IncludeInlayParameterNameHints.Literals;
                    }
                }
                return IncludeInlayParameterNameHints.None;
            case FieldKind.OrganizeImportsSort:
                if (val is string sort)
                {
                    switch (global::Ts.StringUtil.StringUtil.ToLowerJS(sort))
                    {
                        case "ordinal":
                            return OrganizeImportsSort.Ordinal;
                        case "ordinalignorecase":
                            return OrganizeImportsSort.OrdinalIgnoreCase;
                        case "natural":
                            return OrganizeImportsSort.Natural;
                        case "naturalignorecase":
                            return OrganizeImportsSort.NaturalIgnoreCase;
                    }
                }
                return OrganizeImportsSort.Auto;
            case FieldKind.OrganizeImportsCollation:
                if (val is string collation && global::Ts.StringUtil.StringUtil.ToLowerJS(collation) == "unicode")
                {
                    return OrganizeImportsCollation.Unicode;
                }
                return OrganizeImportsCollation.Ordinal;
            case FieldKind.OrganizeImportsCaseFirst:
                if (val is string caseFirst)
                {
                    switch (caseFirst)
                    {
                        case "lower":
                            return OrganizeImportsCaseFirst.Lower;
                        case "upper":
                            return OrganizeImportsCaseFirst.Upper;
                    }
                }
                return OrganizeImportsCaseFirst.False;
            case FieldKind.OrganizeImportsTypeOrder:
                if (val is string typeOrder)
                {
                    switch (typeOrder)
                    {
                        case "last":
                            return OrganizeImportsTypeOrder.Last;
                        case "inline":
                            return OrganizeImportsTypeOrder.Inline;
                        case "first":
                            return OrganizeImportsTypeOrder.First;
                    }
                }
                return OrganizeImportsTypeOrder.Auto;
            case FieldKind.ImportModuleSpecifierPreference:
                if (val is string specifier)
                {
                    switch (global::Ts.StringUtil.StringUtil.ToLowerJS(specifier))
                    {
                        case "project-relative":
                            return global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.ProjectRelative;
                        case "relative":
                            return global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.Relative;
                        case "non-relative":
                            return global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.NonRelative;
                    }
                }
                return global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.Shortest;
            case FieldKind.ImportModuleSpecifierEndingPreference:
                if (val is string ending)
                {
                    switch (global::Ts.StringUtil.StringUtil.ToLowerJS(ending))
                    {
                        case "minimal":
                            return global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.Minimal;
                        case "index":
                            return global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.Index;
                        case "js":
                            return global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.Js;
                    }
                }
                return global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.Auto;
            default:
                throw new InvalidOperationException("no type parser for " + kind.ToString());
        }
    }

    // userpreferences.go:413-510 — typeSerializers, merged with serializeField's reflect.Kind
    // fallbacks (userpreferences.go:815-852). Returns null for "omit".
    private static object? SerializeTyped(FieldKind kind, object value)
    {
        switch (kind)
        {
            case FieldKind.Tristate:
                return (Tristate)value switch
                {
                    Tristate.True => true,
                    Tristate.False => false,
                    _ => null,
                };
            case FieldKind.OrganizeImportsSort:
                return (OrganizeImportsSort)value switch
                {
                    OrganizeImportsSort.Ordinal => "ordinal",
                    OrganizeImportsSort.OrdinalIgnoreCase => "ordinalIgnoreCase",
                    OrganizeImportsSort.Natural => "natural",
                    OrganizeImportsSort.NaturalIgnoreCase => "naturalIgnoreCase",
                    _ => "auto",
                };
            case FieldKind.OrganizeImportsCollation:
                return (OrganizeImportsCollation)value == OrganizeImportsCollation.Unicode ? "unicode" : "ordinal";
            case FieldKind.OrganizeImportsCaseFirst:
                return (OrganizeImportsCaseFirst)value switch
                {
                    OrganizeImportsCaseFirst.Lower => "lower",
                    OrganizeImportsCaseFirst.Upper => "upper",
                    _ => "default",
                };
            case FieldKind.OrganizeImportsTypeOrder:
                return (OrganizeImportsTypeOrder)value switch
                {
                    OrganizeImportsTypeOrder.Last => "last",
                    OrganizeImportsTypeOrder.Inline => "inline",
                    OrganizeImportsTypeOrder.First => "first",
                    _ => "auto",
                };
            // These enums distinguish an unset zero value (e.g. "") from their effective
            // default (e.g. "auto"): the parser promotes unset/unknown input to the
            // non-zero default; omit the unset value so it decodes back to the zero value.
            case FieldKind.JsxAttributeCompletionStyle:
            case FieldKind.QuotePreference:
                return (string)value is { Length: > 0 } str ? str : null;
            case FieldKind.ImportModuleSpecifierPreference:
                return ((global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference)value).Value is { Length: > 0 } pref ? pref : null;
            case FieldKind.ImportModuleSpecifierEndingPreference:
                return ((global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference)value).Value is { Length: > 0 } end ? end : null;
            // Go string kind: "" means "unset" and is omitted.
            case FieldKind.IncludeInlayParameterNameHints:
                return (IncludeInlayParameterNameHints)value switch
                {
                    IncludeInlayParameterNameHints.All => "all",
                    IncludeInlayParameterNameHints.Literals => "literals",
                    _ => null,
                };
            case FieldKind.SemicolonPreference:
                return (global::Ts.Format.SemicolonPreference)value switch
                {
                    global::Ts.Format.SemicolonPreference.Insert => "insert",
                    global::Ts.Format.SemicolonPreference.Remove => "remove",
                    global::Ts.Format.SemicolonPreference.Ignore => "ignore",
                    _ => "",
                };
            case FieldKind.Bool:
                return (bool)value;
            // Zero means "unset" for these preference fields. Omit it so a partial
            // config does not clobber defaults with zeros when round-tripped through
            // withConfig.
            case FieldKind.Int:
                return (int)value is not 0 and int i ? i : null;
            case FieldKind.IndentStyle:
                return (int)(global::Ts.Format.IndentStyle)value is not 0 and int style ? style : null;
            case FieldKind.String:
                return (string)value is { Length: > 0 } s ? s : null;
            case FieldKind.StringSlice:
                return value is string[] arr ? arr.Clone() : null;
            default:
                throw new InvalidOperationException("no type serializer for " + kind.ToString());
        }
    }

    // userpreferences.go:536-548 — fieldInfo / configPathInfo.
    private sealed class FieldInfo
    {
        public string RawName = ""; // raw name for unstable section lookup (e.g., "quotePreference")
        public string ConfigPath = ""; // dotted path for config (e.g., "preferences.quoteStyle")
        public List<ConfigPathInfo>? FallbackConfigPaths;
        public bool RawInvert; // whether to invert boolean values for raw name
        public bool ConfigInvert; // whether to invert boolean values for config path
        // PORT-NOTE: replaces fieldPath []int + reflect.Value access.
        public FieldKind Kind;
        public Func<UserPreferences, object?> Get = null!;
        public Action<UserPreferences, object?> Set = null!;
    }

    private readonly record struct ConfigPathInfo(string Path, bool Invert);

    // userpreferences.go:566-615 — collectFieldInfos, for one tagged field.
    private static FieldInfo Field(string rawTag, string configTag, FieldKind kind, Func<UserPreferences, object?> get, Action<UserPreferences, object?> set, string fallbackConfigTag = "")
    {
        var info = new FieldInfo { Kind = kind, Get = get, Set = set };
        // Parse raw tag: "name" or "name,invert"
        if (rawTag != "")
        {
            string[] parts = rawTag.Split(',');
            info.RawName = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] == "invert")
                {
                    info.RawInvert = true;
                }
            }
        }
        // Parse config tag: "path.to.setting" or "path.to.setting,invert"
        if (configTag != "")
        {
            ConfigPathInfo configPath = ParseConfigPathTag(configTag);
            info.ConfigPath = configPath.Path;
            info.ConfigInvert = configPath.Invert;
        }
        if (fallbackConfigTag != "")
        {
            info.FallbackConfigPaths = [];
            foreach (string tag in fallbackConfigTag.Split(';'))
            {
                info.FallbackConfigPaths.Add(ParseConfigPathTag(tag));
            }
        }
        return info;
    }

    // userpreferences.go:617-626 — parseConfigPathTag.
    private static ConfigPathInfo ParseConfigPathTag(string tag)
    {
        string[] parts = tag.Split(',');
        bool invert = false;
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "invert")
            {
                invert = true;
            }
        }
        return new ConfigPathInfo(parts[0], invert);
    }

    // userpreferences.go:550-552 — fieldInfoCache: the struct tags, in Go field order
    // (formatcodeoptions.go:60-92 for the embedded FormatCodeSettings/EditorSettings).
    private static readonly FieldInfo[] fieldInfos =
    [
        // FormatCodeSettings → EditorSettings
        Field("baseIndentSize", "format.baseIndentSize", FieldKind.Int, p => p.FormatCodeSettings.BaseIndentSize, (p, v) => p.FormatCodeSettings.BaseIndentSize = (int)v!),
        Field("indentSize", "format.indentSize", FieldKind.Int, p => p.FormatCodeSettings.IndentSize, (p, v) => p.FormatCodeSettings.IndentSize = (int)v!),
        Field("tabSize", "format.tabSize", FieldKind.Int, p => p.FormatCodeSettings.TabSize, (p, v) => p.FormatCodeSettings.TabSize = (int)v!),
        Field("newLineCharacter", "format.newLineCharacter", FieldKind.String, p => p.FormatCodeSettings.NewLineCharacter, (p, v) => p.FormatCodeSettings.NewLineCharacter = (string)v!),
        Field("convertTabsToSpaces", "format.convertTabsToSpaces", FieldKind.Tristate, p => p.FormatCodeSettings.ConvertTabsToSpaces, (p, v) => p.FormatCodeSettings.ConvertTabsToSpaces = (Tristate)v!),
        Field("indentStyle", "format.indentStyle", FieldKind.IndentStyle, p => p.FormatCodeSettings.IndentStyle, (p, v) => p.FormatCodeSettings.IndentStyle = (global::Ts.Format.IndentStyle)v!),
        Field("trimTrailingWhitespace", "format.trimTrailingWhitespace", FieldKind.Tristate, p => p.FormatCodeSettings.TrimTrailingWhitespace, (p, v) => p.FormatCodeSettings.TrimTrailingWhitespace = (Tristate)v!),
        // FormatCodeSettings
        Field("insertSpaceAfterCommaDelimiter", "format.insertSpaceAfterCommaDelimiter", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterCommaDelimiter, (p, v) => p.FormatCodeSettings.InsertSpaceAfterCommaDelimiter = (Tristate)v!),
        Field("insertSpaceAfterSemicolonInForStatements", "format.insertSpaceAfterSemicolonInForStatements", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterSemicolonInForStatements, (p, v) => p.FormatCodeSettings.InsertSpaceAfterSemicolonInForStatements = (Tristate)v!),
        Field("insertSpaceBeforeAndAfterBinaryOperators", "format.insertSpaceBeforeAndAfterBinaryOperators", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceBeforeAndAfterBinaryOperators, (p, v) => p.FormatCodeSettings.InsertSpaceBeforeAndAfterBinaryOperators = (Tristate)v!),
        Field("insertSpaceAfterConstructor", "format.insertSpaceAfterConstructor", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterConstructor, (p, v) => p.FormatCodeSettings.InsertSpaceAfterConstructor = (Tristate)v!),
        Field("insertSpaceAfterKeywordsInControlFlowStatements", "format.insertSpaceAfterKeywordsInControlFlowStatements", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterKeywordsInControlFlowStatements, (p, v) => p.FormatCodeSettings.InsertSpaceAfterKeywordsInControlFlowStatements = (Tristate)v!),
        Field("insertSpaceAfterFunctionKeywordForAnonymousFunctions", "format.insertSpaceAfterFunctionKeywordForAnonymousFunctions", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterFunctionKeywordForAnonymousFunctions, (p, v) => p.FormatCodeSettings.InsertSpaceAfterFunctionKeywordForAnonymousFunctions = (Tristate)v!),
        Field("insertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis", "format.insertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis, (p, v) => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis = (Tristate)v!),
        Field("insertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets", "format.insertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets, (p, v) => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets = (Tristate)v!),
        Field("insertSpaceAfterOpeningAndBeforeClosingNonemptyBraces", "format.insertSpaceAfterOpeningAndBeforeClosingNonemptyBraces", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces, (p, v) => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces = (Tristate)v!),
        Field("insertSpaceAfterOpeningAndBeforeClosingEmptyBraces", "format.insertSpaceAfterOpeningAndBeforeClosingEmptyBraces", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces, (p, v) => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces = (Tristate)v!),
        Field("insertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces", "format.insertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces, (p, v) => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces = (Tristate)v!),
        Field("insertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces", "format.insertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces, (p, v) => p.FormatCodeSettings.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces = (Tristate)v!),
        Field("insertSpaceAfterTypeAssertion", "format.insertSpaceAfterTypeAssertion", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceAfterTypeAssertion, (p, v) => p.FormatCodeSettings.InsertSpaceAfterTypeAssertion = (Tristate)v!),
        Field("insertSpaceBeforeFunctionParenthesis", "format.insertSpaceBeforeFunctionParenthesis", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceBeforeFunctionParenthesis, (p, v) => p.FormatCodeSettings.InsertSpaceBeforeFunctionParenthesis = (Tristate)v!),
        Field("placeOpenBraceOnNewLineForFunctions", "format.placeOpenBraceOnNewLineForFunctions", FieldKind.Tristate, p => p.FormatCodeSettings.PlaceOpenBraceOnNewLineForFunctions, (p, v) => p.FormatCodeSettings.PlaceOpenBraceOnNewLineForFunctions = (Tristate)v!),
        Field("placeOpenBraceOnNewLineForControlBlocks", "format.placeOpenBraceOnNewLineForControlBlocks", FieldKind.Tristate, p => p.FormatCodeSettings.PlaceOpenBraceOnNewLineForControlBlocks, (p, v) => p.FormatCodeSettings.PlaceOpenBraceOnNewLineForControlBlocks = (Tristate)v!),
        Field("insertSpaceBeforeTypeAnnotation", "format.insertSpaceBeforeTypeAnnotation", FieldKind.Tristate, p => p.FormatCodeSettings.InsertSpaceBeforeTypeAnnotation, (p, v) => p.FormatCodeSettings.InsertSpaceBeforeTypeAnnotation = (Tristate)v!),
        Field("indentMultiLineObjectLiteralBeginningOnBlankLine", "format.indentMultiLineObjectLiteralBeginningOnBlankLine", FieldKind.Tristate, p => p.FormatCodeSettings.IndentMultiLineObjectLiteralBeginningOnBlankLine, (p, v) => p.FormatCodeSettings.IndentMultiLineObjectLiteralBeginningOnBlankLine = (Tristate)v!),
        Field("semicolons", "format.semicolons", FieldKind.SemicolonPreference, p => p.FormatCodeSettings.Semicolons, (p, v) => p.FormatCodeSettings.Semicolons = (global::Ts.Format.SemicolonPreference)v!),
        Field("indentSwitchCase", "format.indentSwitchCase", FieldKind.Tristate, p => p.FormatCodeSettings.IndentSwitchCase, (p, v) => p.FormatCodeSettings.IndentSwitchCase = (Tristate)v!),

        Field("quotePreference", "preferences.quoteStyle", FieldKind.QuotePreference, p => p.QuotePreference, (p, v) => p.QuotePreference = (string)v!),
        Field("lazyConfiguredProjectsFromExternalProject", "", FieldKind.Tristate, p => p.LazyConfiguredProjectsFromExternalProject, (p, v) => p.LazyConfiguredProjectsFromExternalProject = (Tristate)v!),
        Field("maximumHoverLength", "", FieldKind.Int, p => p.MaximumHoverLength, (p, v) => p.MaximumHoverLength = (int)v!),

        // ------- Completions -------
        Field("includeCompletionsForModuleExports", "suggest.autoImports", FieldKind.Tristate, p => p.IncludeCompletionsForModuleExports, (p, v) => p.IncludeCompletionsForModuleExports = (Tristate)v!),
        Field("includeCompletionsForImportStatements", "suggest.includeCompletionsForImportStatements", FieldKind.Tristate, p => p.IncludeCompletionsForImportStatements, (p, v) => p.IncludeCompletionsForImportStatements = (Tristate)v!),
        Field("includeAutomaticOptionalChainCompletions", "suggest.includeAutomaticOptionalChainCompletions", FieldKind.Tristate, p => p.IncludeAutomaticOptionalChainCompletions, (p, v) => p.IncludeAutomaticOptionalChainCompletions = (Tristate)v!),
        Field("includeCompletionsWithClassMemberSnippets", "suggest.classMemberSnippets.enabled", FieldKind.Tristate, p => p.IncludeCompletionsWithClassMemberSnippets, (p, v) => p.IncludeCompletionsWithClassMemberSnippets = (Tristate)v!),
        Field("includeCompletionsWithObjectLiteralMethodSnippets", "suggest.objectLiteralMethodSnippets.enabled", FieldKind.Tristate, p => p.IncludeCompletionsWithObjectLiteralMethodSnippets, (p, v) => p.IncludeCompletionsWithObjectLiteralMethodSnippets = (Tristate)v!),
        Field("jsxAttributeCompletionStyle", "preferences.jsxAttributeCompletionStyle", FieldKind.JsxAttributeCompletionStyle, p => p.JsxAttributeCompletionStyle, (p, v) => p.JsxAttributeCompletionStyle = (string)v!),
        Field("autoClosingTags", "autoClosingTags.enabled", FieldKind.Tristate, p => p.EnableAutoClosingTags, (p, v) => p.EnableAutoClosingTags = (Tristate)v!, fallbackConfigTag: "autoClosingTags"),
        Field("completeJSDocs", "suggest.jsdoc.enabled", FieldKind.Tristate, p => p.EnableJSDocCompletions, (p, v) => p.EnableJSDocCompletions = (Tristate)v!, fallbackConfigTag: "suggest.completeJSDocs"),
        Field("generateReturnInDocTemplate", "suggest.jsdoc.generateReturns", FieldKind.Tristate, p => p.GenerateReturnInDocTemplate, (p, v) => p.GenerateReturnInDocTemplate = (Tristate)v!),

        // ------- AutoImports --------
        Field("importModuleSpecifierPreference", "preferences.importModuleSpecifier", FieldKind.ImportModuleSpecifierPreference, p => p.ImportModuleSpecifierPreference, (p, v) => p.ImportModuleSpecifierPreference = (global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference)v!),
        Field("importModuleSpecifierEnding", "preferences.importModuleSpecifierEnding", FieldKind.ImportModuleSpecifierEndingPreference, p => p.ImportModuleSpecifierEnding, (p, v) => p.ImportModuleSpecifierEnding = (global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference)v!),
        Field("autoImportSpecifierExcludeRegexes", "preferences.autoImportSpecifierExcludeRegexes", FieldKind.StringSlice, p => p.AutoImportSpecifierExcludeRegexes, (p, v) => p.AutoImportSpecifierExcludeRegexes = (string[])v!),
        Field("autoImportFileExcludePatterns", "preferences.autoImportFileExcludePatterns", FieldKind.StringSlice, p => p.AutoImportFileExcludePatterns, (p, v) => p.AutoImportFileExcludePatterns = (string[])v!),
        Field("autoImportEntrypointDirectorySearch", "preferences.autoImportEntrypointDirectorySearch", FieldKind.Tristate, p => p.AutoImportEntrypointDirectorySearch, (p, v) => p.AutoImportEntrypointDirectorySearch = (Tristate)v!),
        Field("preferTypeOnlyAutoImports", "preferences.preferTypeOnlyAutoImports", FieldKind.Tristate, p => p.PreferTypeOnlyAutoImports, (p, v) => p.PreferTypeOnlyAutoImports = (Tristate)v!),

        // ------- OrganizeImports -------
        Field("organizeImportsSort", "preferences.organizeImports.sort", FieldKind.OrganizeImportsSort, p => p.OrganizeImportsSort, (p, v) => p.OrganizeImportsSort = (OrganizeImportsSort)v!),
        Field("organizeImportsIgnoreCase", "preferences.organizeImports.caseSensitivity", FieldKind.Tristate, p => p.OrganizeImportsIgnoreCase, (p, v) => p.OrganizeImportsIgnoreCase = (Tristate)v!),
        Field("organizeImportsCollation", "preferences.organizeImports.unicodeCollation", FieldKind.OrganizeImportsCollation, p => p.OrganizeImportsCollation, (p, v) => p.OrganizeImportsCollation = (OrganizeImportsCollation)v!),
        Field("organizeImportsLocale", "preferences.organizeImports.locale", FieldKind.String, p => p.OrganizeImportsLocale, (p, v) => p.OrganizeImportsLocale = (string)v!),
        Field("organizeImportsNumericCollation", "preferences.organizeImports.numericCollation", FieldKind.Tristate, p => p.OrganizeImportsNumericCollation, (p, v) => p.OrganizeImportsNumericCollation = (Tristate)v!),
        Field("organizeImportsAccentCollation", "preferences.organizeImports.accentCollation", FieldKind.Tristate, p => p.OrganizeImportsAccentCollation, (p, v) => p.OrganizeImportsAccentCollation = (Tristate)v!),
        Field("organizeImportsCaseFirst", "preferences.organizeImports.caseFirst", FieldKind.OrganizeImportsCaseFirst, p => p.OrganizeImportsCaseFirst, (p, v) => p.OrganizeImportsCaseFirst = (OrganizeImportsCaseFirst)v!),
        Field("organizeImportsTypeOrder", "preferences.organizeImports.typeOrder", FieldKind.OrganizeImportsTypeOrder, p => p.OrganizeImportsTypeOrder, (p, v) => p.OrganizeImportsTypeOrder = (OrganizeImportsTypeOrder)v!),

        // ------- MoveToFile -------
        Field("allowTextChangesInNewFiles", "", FieldKind.Tristate, p => p.AllowTextChangesInNewFiles, (p, v) => p.AllowTextChangesInNewFiles = (Tristate)v!),

        // ------- Rename -------
        Field("providePrefixAndSuffixTextForRename", "preferences.useAliasesForRenames", FieldKind.Tristate, p => p.UseAliasesForRename, (p, v) => p.UseAliasesForRename = (Tristate)v!),
        Field("allowRenameOfImportPath", "", FieldKind.Tristate, p => p.AllowRenameOfImportPath, (p, v) => p.AllowRenameOfImportPath = (Tristate)v!),

        // ------- CodeFixes/Refactors -------
        Field("provideRefactorNotApplicableReason", "", FieldKind.Tristate, p => p.ProvideRefactorNotApplicableReason, (p, v) => p.ProvideRefactorNotApplicableReason = (Tristate)v!),

        // ------- InlayHints (InlayHintsPreferences, userpreferences.go:205-214) -------
        Field("includeInlayParameterNameHints", "inlayHints.parameterNames.enabled", FieldKind.IncludeInlayParameterNameHints, p => p.InlayHints.IncludeInlayParameterNameHints, (p, v) => p.InlayHints.IncludeInlayParameterNameHints = (IncludeInlayParameterNameHints)v!),
        Field("includeInlayParameterNameHintsWhenArgumentMatchesName", "inlayHints.parameterNames.suppressWhenArgumentMatchesName,invert", FieldKind.Tristate, p => p.InlayHints.IncludeInlayParameterNameHintsWhenArgumentMatchesName, (p, v) => p.InlayHints.IncludeInlayParameterNameHintsWhenArgumentMatchesName = (Tristate)v!),
        Field("includeInlayFunctionParameterTypeHints", "inlayHints.parameterTypes.enabled", FieldKind.Tristate, p => p.InlayHints.IncludeInlayFunctionParameterTypeHints, (p, v) => p.InlayHints.IncludeInlayFunctionParameterTypeHints = (Tristate)v!),
        Field("includeInlayVariableTypeHints", "inlayHints.variableTypes.enabled", FieldKind.Tristate, p => p.InlayHints.IncludeInlayVariableTypeHints, (p, v) => p.InlayHints.IncludeInlayVariableTypeHints = (Tristate)v!),
        Field("includeInlayVariableTypeHintsWhenTypeMatchesName", "inlayHints.variableTypes.suppressWhenTypeMatchesName,invert", FieldKind.Tristate, p => p.InlayHints.IncludeInlayVariableTypeHintsWhenTypeMatchesName, (p, v) => p.InlayHints.IncludeInlayVariableTypeHintsWhenTypeMatchesName = (Tristate)v!),
        Field("includeInlayPropertyDeclarationTypeHints", "inlayHints.propertyDeclarationTypes.enabled", FieldKind.Tristate, p => p.InlayHints.IncludeInlayPropertyDeclarationTypeHints, (p, v) => p.InlayHints.IncludeInlayPropertyDeclarationTypeHints = (Tristate)v!),
        Field("includeInlayFunctionLikeReturnTypeHints", "inlayHints.functionLikeReturnTypes.enabled", FieldKind.Tristate, p => p.InlayHints.IncludeInlayFunctionLikeReturnTypeHints, (p, v) => p.InlayHints.IncludeInlayFunctionLikeReturnTypeHints = (Tristate)v!),
        Field("includeInlayEnumMemberValueHints", "inlayHints.enumMemberValues.enabled", FieldKind.Tristate, p => p.InlayHints.IncludeInlayEnumMemberValueHints, (p, v) => p.InlayHints.IncludeInlayEnumMemberValueHints = (Tristate)v!),

        // ------- CodeLens (CodeLensUserPreferences, userpreferences.go:216-222) -------
        Field("referencesCodeLensEnabled", "referencesCodeLens.enabled", FieldKind.Tristate, p => p.CodeLens.ReferencesCodeLensEnabled, (p, v) => p.CodeLens.ReferencesCodeLensEnabled = (Tristate)v!),
        Field("implementationsCodeLensEnabled", "implementationsCodeLens.enabled", FieldKind.Tristate, p => p.CodeLens.ImplementationsCodeLensEnabled, (p, v) => p.CodeLens.ImplementationsCodeLensEnabled = (Tristate)v!),
        Field("referencesCodeLensShowOnAllFunctions", "referencesCodeLens.showOnAllFunctions", FieldKind.Tristate, p => p.CodeLens.ReferencesCodeLensShowOnAllFunctions, (p, v) => p.CodeLens.ReferencesCodeLensShowOnAllFunctions = (Tristate)v!),
        Field("implementationsCodeLensShowOnInterfaceMethods", "implementationsCodeLens.showOnInterfaceMethods", FieldKind.Tristate, p => p.CodeLens.ImplementationsCodeLensShowOnInterfaceMethods, (p, v) => p.CodeLens.ImplementationsCodeLensShowOnInterfaceMethods = (Tristate)v!),
        Field("implementationsCodeLensShowOnAllClassMethods", "implementationsCodeLens.showOnAllClassMethods", FieldKind.Tristate, p => p.CodeLens.ImplementationsCodeLensShowOnAllClassMethods, (p, v) => p.CodeLens.ImplementationsCodeLensShowOnAllClassMethods = (Tristate)v!),

        // ------- Definition -------
        Field("preferGoToSourceDefinition", "", FieldKind.Bool, p => p.PreferGoToSourceDefinition, (p, v) => p.PreferGoToSourceDefinition = (bool)v!),

        // ------- Symbols -------
        Field("excludeLibrarySymbolsInNavTo", "workspaceSymbols.excludeLibrarySymbols", FieldKind.Tristate, p => p.ExcludeLibrarySymbolsInNavTo, (p, v) => p.ExcludeLibrarySymbolsInNavTo = (Tristate)v!),

        // ------- Misc -------
        Field("formatEnabled", "format.enabled", FieldKind.Tristate, p => p.EnableFormatting, (p, v) => p.EnableFormatting = (Tristate)v!, fallbackConfigTag: "format.enable"),
        Field("validateEnabled", "validate.enabled", FieldKind.Tristate, p => p.EnableValidation, (p, v) => p.EnableValidation = (Tristate)v!, fallbackConfigTag: "validate.enable"),
        Field("disableSuggestions", "", FieldKind.Tristate, p => p.DisableSuggestions, (p, v) => p.DisableSuggestions = (Tristate)v!),
        Field("disableLineTextInReferences", "", FieldKind.Tristate, p => p.DisableLineTextInReferences, (p, v) => p.DisableLineTextInReferences = (Tristate)v!),
        Field("displayPartsForJSDoc", "", FieldKind.Tristate, p => p.DisplayPartsForJSDoc, (p, v) => p.DisplayPartsForJSDoc = (Tristate)v!),
        Field("reportStyleChecksAsWarnings", "reportStyleChecksAsWarnings", FieldKind.Tristate, p => p.ReportStyleChecksAsWarnings, (p, v) => p.ReportStyleChecksAsWarnings = (Tristate)v!),

        // ------- ATA -------
        Field("disableAutomaticTypeAcquisition", "disableAutomaticTypeAcquisition", FieldKind.Tristate, p => p.DisableAutomaticTypeAcquisition, (p, v) => p.DisableAutomaticTypeAcquisition = (Tristate)v!),
        Field("automaticTypeAcquisitionEnabled", "tsserver.automaticTypeAcquisition.enabled", FieldKind.Tristate, p => p.AutomaticTypeAcquisitionEnabled, (p, v) => p.AutomaticTypeAcquisitionEnabled = (Tristate)v!),

        // ------- Project Configuration -------
        Field("customConfigFileName", "customConfigFileName", FieldKind.String, p => p.CustomConfigFileName, (p, v) => p.CustomConfigFileName = (string)v!),
    ];

    // userpreferences.go:512-534 — configPathParsers provides field-specific config value parsers that
    // override the default type-based parser when the VS Code config value format differs from the field type.
    private static object? ParseConfigPath(string path, object? val, out bool found)
    {
        found = true;
        switch (path)
        {
            // VS Code sends caseSensitivity as a string ("auto"/"caseSensitive"/"caseInsensitive"),
            // but OrganizeImportsIgnoreCase is a core.Tristate.
            case "preferences.organizeImports.caseSensitivity":
                if (val is string s)
                {
                    switch (global::Ts.StringUtil.StringUtil.ToLowerJS(s))
                    {
                        case "caseinsensitive":
                            return Tristate.True;
                        case "casesensitive":
                            return Tristate.False;
                    }
                }
                if (val is bool b)
                {
                    return b ? Tristate.True : Tristate.False;
                }
                return Tristate.Unknown;
        }
        found = false;
        return null;
    }

    // userpreferences.go:554-564 — unstableNameIndex maps raw names to fieldInfo index for unstable section lookup.
    private static readonly Dictionary<string, int> unstableNameIndex = BuildUnstableNameIndex();

    private static Dictionary<string, int> BuildUnstableNameIndex()
    {
        var index = new Dictionary<string, int>(fieldInfos.Length, StringComparer.Ordinal);
        for (int i = 0; i < fieldInfos.Length; i++)
        {
            if (fieldInfos[i].RawName != "")
            {
                index[fieldInfos[i].RawName] = i;
            }
        }
        return index;
    }

    // PORT-NOTE: Go's encoding/json decode of `any` — objects become map[string]any, arrays []any,
    // numbers float64; reproduced here so withConfig sees the same dynamic shapes.
    internal static object? ToAny(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    map[property.Name] = ToAny(property.Value);
                }
                return map;
            case JsonValueKind.Array:
                var list = new List<object?>(element.GetArrayLength());
                foreach (JsonElement item in element.EnumerateArray())
                {
                    list.Add(ToAny(item));
                }
                return list;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    // userpreferences.go:628-642 — getNestedValue.
    private static bool TryGetNestedValue(Dictionary<string, object?> config, string path, out object? value)
    {
        object? current = config;
        foreach (string part in path.Split('.'))
        {
            if (current is not Dictionary<string, object?> m || !m.TryGetValue(part, out current))
            {
                value = null;
                return false;
            }
        }
        value = current;
        return true;
    }

    // userpreferences.go:644-656 — setNestedValue.
    private static void SetNestedValue(Dictionary<string, object?> config, string path, object? value)
    {
        string[] parts = path.Split('.');
        Dictionary<string, object?> current = config;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!current.TryGetValue(parts[i], out object? nextValue) || nextValue is not Dictionary<string, object?> next)
            {
                next = new Dictionary<string, object?>(StringComparer.Ordinal);
                current[parts[i]] = next;
            }
            current = next;
        }
        current[parts[^1]] = value;
    }

    // userpreferences.go:658-672 — setRawFieldsFromConfig.
    private void SetRawFieldsFromConfig(Dictionary<string, object?> settings)
    {
        // map-iter: 序无关 — each raw name addresses a distinct field.
        foreach (var (name, rawValue) in settings)
        {
            if (unstableNameIndex.TryGetValue(name, out int idx))
            {
                FieldInfo info = fieldInfos[idx];
                object? value = rawValue;
                if (info.RawInvert)
                {
                    if (value is bool b)
                    {
                        value = !b;
                    }
                }
                SetFieldFromValue(info, value);
            }
        }
    }

    // userpreferences.go:674-733 — withConfig.
    internal UserPreferences WithConfig(Dictionary<string, object?> config)
    {
        UserPreferences p = Clone();

        // Raw UserPreferences can be provided directly, notably via LSP initializationOptions.
        p.SetRawFieldsFromConfig(config);

        // Process "unstable" section first - allows any field to be set by raw name.
        // This mirrors VS Code's behavior: { ...config.get('unstable'), ...stableOptions }
        // where stable options are spread after and take precedence.
        if (config.TryGetValue("unstable", out object? unstableValue) && unstableValue is Dictionary<string, object?> unstable)
        {
            p.SetRawFieldsFromConfig(unstable);
        }

        // Process path-based config (VS Code style nested paths).
        // These run after unstable, so stable config values take precedence.
        foreach (FieldInfo info in fieldInfos)
        {
            if (info.ConfigPath == "")
            {
                continue;
            }
            var configPath = new ConfigPathInfo(info.ConfigPath, info.ConfigInvert);
            bool ok = TryGetNestedValue(config, configPath.Path, out object? val);
            if (!ok && info.FallbackConfigPaths is { } fallbacks)
            {
                foreach (ConfigPathInfo fallbackConfigPath in fallbacks)
                {
                    ok = TryGetNestedValue(config, fallbackConfigPath.Path, out val);
                    if (ok)
                    {
                        configPath = fallbackConfigPath;
                        break;
                    }
                }
            }
            if (!ok)
            {
                continue;
            }

            if (configPath.Invert)
            {
                if (val is bool b)
                {
                    val = !b;
                }
            }
            object? parsed = ParseConfigPath(configPath.Path, val, out bool hasParser);
            if (hasParser)
            {
                info.Set(p, parsed);
                continue;
            }
            p.SetFieldFromValue(info, val);
        }

        // Validate CustomConfigFileName for path traversal
        if (p.CustomConfigFileName != "")
        {
            string name = p.CustomConfigFileName.Trim();
            if (name.AsSpan().IndexOfAny('/', '\\') >= 0 || name == ".." || name == ".")
            {
                p.CustomConfigFileName = "";
            }
            else
            {
                p.CustomConfigFileName = name;
            }
        }

        return p;
    }

    // userpreferences.go:742-780 — setFieldFromValue.
    private void SetFieldFromValue(FieldInfo info, object? val)
    {
        if (val is null)
        {
            return;
        }

        switch (info.Kind)
        {
            case FieldKind.Bool:
                if (val is bool b)
                {
                    info.Set(this, b);
                }
                break;
            case FieldKind.Int:
                if (val is double d)
                {
                    info.Set(this, (int)(long)d);
                }
                break;
            case FieldKind.String:
                if (val is string s)
                {
                    info.Set(this, s);
                }
                break;
            case FieldKind.StringSlice:
                if (val is List<object?> arr)
                {
                    var result = new List<string>(arr.Count);
                    foreach (object? item in arr)
                    {
                        if (item is string str)
                        {
                            result.Add(str);
                        }
                    }
                    info.Set(this, result.ToArray());
                }
                break;
            default:
                // Custom parsers (for types like Tristate, enums, etc.)
                info.Set(this, ParseTyped(info.Kind, val));
                break;
        }
    }

    // userpreferences.go:782-813 — MarshalJSONTo.
    public void WriteTo(Utf8JsonWriter writer)
    {
        var config = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (FieldInfo info in fieldInfos)
        {
            object? field = info.Get(this);
            object? val = field is null ? null : SerializeTyped(info.Kind, field);
            if (val is null)
            {
                continue;
            }

            // Prefer config path if available, otherwise use unstable section
            if (info.ConfigPath != "")
            {
                if (info.ConfigInvert)
                {
                    if (val is bool b)
                    {
                        val = !b;
                    }
                }
                SetNestedValue(config, info.ConfigPath, val);
            }
            else if (info.RawName != "")
            {
                if (info.RawInvert)
                {
                    if (val is bool b)
                    {
                        val = !b;
                    }
                }
                SetNestedValue(config, "unstable." + info.RawName, val);
            }
        }

        // Go: json.MarshalEncode(enc, config, json.Deterministic(true)) — map keys sorted.
        WriteValue(writer, config);
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case Dictionary<string, object?> map:
                var keys = new List<string>(map.Keys);
                keys.Sort(StringComparer.Ordinal);
                writer.WriteStartObject();
                foreach (string key in keys)
                {
                    writer.WritePropertyName(key);
                    WriteValue(writer, map[key]);
                }
                writer.WriteEndObject();
                break;
            case string[] strings:
                writer.WriteStartArray();
                foreach (string s in strings)
                {
                    writer.WriteStringValue(s);
                }
                writer.WriteEndArray();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    // json.Marshal(&prefs) — the MarshalJSONTo output as a detached JsonElement.
    public JsonElement ToJsonElement()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteTo(writer);
        }
        using JsonDocument doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    // userpreferences.go:854-862 — UnmarshalJSONFrom: start with defaults, then overlay parsed values.
    public static UserPreferences FromJson(JsonElement json)
    {
        var config = ToAny(json) as Dictionary<string, object?>
            ?? throw new JsonException("cannot unmarshal " + json.ValueKind.ToString() + " into UserPreferences");
        return NewDefaultUserPreferences().WithConfig(config);
    }

    // userpreferences.go:882-901 — ParseUserPreferences.
    public static UserPreferences ParseUserPreferences(IReadOnlyDictionary<string, JsonElement> items)
    {
        UserPreferences prefs = NewDefaultUserPreferences();
        // Apply editor settings first (tabSize, indentSize, etc.) as raw-name defaults,
        // then overlay language-specific settings with increasing precedence:
        // editor < javascript < typescript < js/ts
        if (items.TryGetValue("editor", out JsonElement editorItem) && ToAny(editorItem) is Dictionary<string, object?> editorSettings)
        {
            prefs = prefs.WithConfig(new Dictionary<string, object?>(StringComparer.Ordinal) { ["unstable"] = editorSettings });
        }
        // Apply javascript, then typescript, then js/ts (highest precedence).
        foreach (string section in (ReadOnlySpan<string>)["javascript", "typescript", "js/ts"])
        {
            if (items.TryGetValue(section, out JsonElement item) && ToAny(item) is Dictionary<string, object?> settings)
            {
                prefs = prefs.WithConfig(settings);
            }
        }
        return prefs;
    }
}

// userpreferences.go:205-214 — InlayHintsPreferences is owned by InlayHints.Support.cs
// (the typed lsutil port); this file's older string-typed record copy was removed.

// userpreferences.go:216-222 — CodeLensUserPreferences.
public record struct CodeLensUserPreferences(
    Tristate ReferencesCodeLensEnabled,
    Tristate ImplementationsCodeLensEnabled,
    Tristate ReferencesCodeLensShowOnAllFunctions,
    Tristate ImplementationsCodeLensShowOnInterfaceMethods,
    Tristate ImplementationsCodeLensShowOnAllClassMethods);

// userpreferences.go:252-260 — OrganizeImportsSort.
public enum OrganizeImportsSort
{
    Auto = 0,
    Ordinal,
    OrdinalIgnoreCase,
    Natural,
    NaturalIgnoreCase,
}

// userpreferences.go:262-267 — OrganizeImportsCollation (Go bool type: Ordinal=false, Unicode=true).
public enum OrganizeImportsCollation : byte
{
    Ordinal = 0,
    Unicode = 1,
}

// userpreferences.go:269-275 — OrganizeImportsCaseFirst.
public enum OrganizeImportsCaseFirst
{
    False = 0,
    Lower = 1,
    Upper = 2,
}

// userpreferences.go:277-283 — OrganizeImportsTypeOrder.
public enum OrganizeImportsTypeOrder
{
    Auto = 0,
    Last = 1,
    Inline = 2,
    First = 3,
}

