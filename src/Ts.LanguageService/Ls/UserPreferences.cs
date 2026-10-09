// Ported from tsgo internal/ls/lsutil/userpreferences.go (completion-relevant subset).
// PORT-NOTE: the full UserPreferences surface (format settings, auto-import/organize-imports
// preference fields, and the reflect-tag-driven raw/config population at userpreferences.go:150+)
// lands with the lsp/config wave; Go's reflection walk becomes a generated per-field switch per
// plan/07. This subset carries exactly the fields getCompletionData and its helpers read.

using Ts.Core;

namespace Ts.Ls;

// userpreferences.go:45 — UserPreferences represents TypeScript language service preferences.
public sealed class UserPreferences
{
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

    // userpreferences.go:15-35 — NewDefaultUserPreferences (completion-relevant subset).
    public static UserPreferences NewDefaultUserPreferences() => new()
    {
        // userpreferences.go:21 — EnableAutoClosingTags: core.TSTrue.
        EnableAutoClosingTags = Tristate.True,
        IncludeCompletionsForModuleExports = Tristate.True,
        IncludeCompletionsForImportStatements = Tristate.True,
        EnableJSDocCompletions = Tristate.True,
        GenerateReturnInDocTemplate = Tristate.True,
        // userpreferences.go:28-31 — protocol defaults.
        EnableValidation = Tristate.True,
        ReportStyleChecksAsWarnings = Tristate.True,
    };
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

