// Ported from tsgo internal/ls/lsutil/userpreferences_test.go (whole file).
//
// PORT-NOTE: Go's assert.DeepEqual over the struct becomes AssertDeepEqual, which compares
// the MarshalJSONTo output (UserPreferences.WriteTo). That serialization covers every
// tagged field and is injective over them (zero/unset values are the only omitted ones),
// so equal JSON means equal preference state. fillNonZeroValues' reflection walk becomes
// the explicit FillNonZeroValues below (Go's "1" for int-backed types: Tristate 1 = TSFalse,
// OrganizeImportsSort 1 = Ordinal, IndentStyle 1 = Block, ...; bool-backed collation true =
// Unicode; strings via getValidStringValue).

using System.Collections.Generic;
using System.Text.Json;
using Ts.Core;
using Xunit;

namespace Ts.Ls.Tests;

public sealed class UserPreferencesTests
{
    // userpreferences_test.go:13-58 — fillNonZeroValues / getValidStringValue.
    private static UserPreferences FillNonZeroValues()
    {
        var p = new UserPreferences();
        var f = p.FormatCodeSettings;
        f.BaseIndentSize = 1;
        f.IndentSize = 1;
        f.TabSize = 1;
        f.NewLineCharacter = "test";
        f.ConvertTabsToSpaces = Tristate.False;
        f.IndentStyle = global::Ts.Format.IndentStyle.Block;
        f.TrimTrailingWhitespace = Tristate.False;
        f.InsertSpaceAfterCommaDelimiter = Tristate.False;
        f.InsertSpaceAfterSemicolonInForStatements = Tristate.False;
        f.InsertSpaceBeforeAndAfterBinaryOperators = Tristate.False;
        f.InsertSpaceAfterConstructor = Tristate.False;
        f.InsertSpaceAfterKeywordsInControlFlowStatements = Tristate.False;
        f.InsertSpaceAfterFunctionKeywordForAnonymousFunctions = Tristate.False;
        f.InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis = Tristate.False;
        f.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets = Tristate.False;
        f.InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces = Tristate.False;
        f.InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces = Tristate.False;
        f.InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces = Tristate.False;
        f.InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces = Tristate.False;
        f.InsertSpaceAfterTypeAssertion = Tristate.False;
        f.InsertSpaceBeforeFunctionParenthesis = Tristate.False;
        f.PlaceOpenBraceOnNewLineForFunctions = Tristate.False;
        f.PlaceOpenBraceOnNewLineForControlBlocks = Tristate.False;
        f.InsertSpaceBeforeTypeAnnotation = Tristate.False;
        f.IndentMultiLineObjectLiteralBeginningOnBlankLine = Tristate.False;
        f.Semicolons = global::Ts.Format.SemicolonPreference.Insert;
        f.IndentSwitchCase = Tristate.False;

        p.QuotePreference = "single";
        p.LazyConfiguredProjectsFromExternalProject = Tristate.False;
        p.MaximumHoverLength = 1;
        p.IncludeCompletionsForModuleExports = Tristate.False;
        p.IncludeCompletionsForImportStatements = Tristate.False;
        p.IncludeAutomaticOptionalChainCompletions = Tristate.False;
        p.IncludeCompletionsWithClassMemberSnippets = Tristate.False;
        p.IncludeCompletionsWithObjectLiteralMethodSnippets = Tristate.False;
        p.JsxAttributeCompletionStyle = "braces";
        p.EnableAutoClosingTags = Tristate.False;
        p.EnableJSDocCompletions = Tristate.False;
        p.GenerateReturnInDocTemplate = Tristate.False;
        p.ImportModuleSpecifierPreference = global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.Relative;
        p.ImportModuleSpecifierEnding = global::Ts.ModuleSpecifiers.ImportModuleSpecifierEndingPreference.Js;
        p.AutoImportSpecifierExcludeRegexes = ["test"];
        p.AutoImportFileExcludePatterns = ["test"];
        p.AutoImportEntrypointDirectorySearch = Tristate.False;
        p.PreferTypeOnlyAutoImports = Tristate.False;
        p.OrganizeImportsSort = OrganizeImportsSort.Ordinal;
        p.OrganizeImportsIgnoreCase = Tristate.False;
        p.OrganizeImportsCollation = OrganizeImportsCollation.Unicode;
        p.OrganizeImportsLocale = "test";
        p.OrganizeImportsNumericCollation = Tristate.False;
        p.OrganizeImportsAccentCollation = Tristate.False;
        p.OrganizeImportsCaseFirst = OrganizeImportsCaseFirst.Lower;
        p.OrganizeImportsTypeOrder = OrganizeImportsTypeOrder.Last;
        p.AllowTextChangesInNewFiles = Tristate.False;
        p.UseAliasesForRename = Tristate.False;
        p.AllowRenameOfImportPath = Tristate.False;
        p.ProvideRefactorNotApplicableReason = Tristate.False;
        p.InlayHints = new InlayHintsPreferences
        {
            IncludeInlayParameterNameHints = IncludeInlayParameterNameHints.All,
            IncludeInlayParameterNameHintsWhenArgumentMatchesName = Tristate.False,
            IncludeInlayFunctionParameterTypeHints = Tristate.False,
            IncludeInlayVariableTypeHints = Tristate.False,
            IncludeInlayVariableTypeHintsWhenTypeMatchesName = Tristate.False,
            IncludeInlayPropertyDeclarationTypeHints = Tristate.False,
            IncludeInlayFunctionLikeReturnTypeHints = Tristate.False,
            IncludeInlayEnumMemberValueHints = Tristate.False,
        };
        p.CodeLens = new CodeLensUserPreferences(Tristate.False, Tristate.False, Tristate.False, Tristate.False, Tristate.False);
        p.PreferGoToSourceDefinition = true;
        p.ExcludeLibrarySymbolsInNavTo = Tristate.False;
        p.EnableFormatting = Tristate.False;
        p.EnableValidation = Tristate.False;
        p.DisableSuggestions = Tristate.False;
        p.DisableLineTextInReferences = Tristate.False;
        p.DisplayPartsForJSDoc = Tristate.False;
        p.ReportStyleChecksAsWarnings = Tristate.False;
        p.DisableAutomaticTypeAcquisition = Tristate.False;
        p.AutomaticTypeAcquisitionEnabled = Tristate.False;
        p.CustomConfigFileName = "test";
        return p;
    }

    private static void AssertDeepEqual(UserPreferences expected, UserPreferences actual)
    {
        Assert.Equal(expected.ToJsonElement().GetRawText(), actual.ToJsonElement().GetRawText());
    }

    // Go: json.Unmarshal(bytes, &config) with config map[string]any.
    private static Dictionary<string, object?> Config(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)UserPreferences.ToAny(doc.RootElement)!;
    }

    // Go: ParseUserPreferences(map[string]any{...}) — the outer map as JSON.
    private static UserPreferences Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        var items = new Dictionary<string, JsonElement>(System.StringComparer.Ordinal);
        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            items[property.Name] = property.Value.Clone();
        }
        return UserPreferences.ParseUserPreferences(items);
    }

    // userpreferences_test.go:60-85 — TestUserPreferencesRoundtrip.
    [Fact]
    public void TestUserPreferencesRoundtrip()
    {
        UserPreferences original = FillNonZeroValues();
        JsonElement json = original.ToJsonElement();

        // UnmarshalJSONFrom
        UserPreferences parsed = UserPreferences.FromJson(json);
        AssertDeepEqual(original, parsed);

        // withConfig
        UserPreferences parsed2 = new UserPreferences().WithConfig(Config(json.GetRawText()));
        AssertDeepEqual(original, parsed2);
    }

    // userpreferences_test.go:87-164 — TestUserPreferencesSerialize.
    [Fact]
    public void TestUserPreferencesSerialize()
    {
        // config path field serializes to nested path
        {
            JsonElement actual = new UserPreferences { QuotePreference = "single" }.ToJsonElement();
            Assert.Equal("single", actual.GetProperty("preferences").GetProperty("quoteStyle").GetString());
        }

        // raw-only field serializes to unstable section
        {
            JsonElement actual = new UserPreferences { DisableSuggestions = Tristate.True }.ToJsonElement();
            Assert.True(actual.GetProperty("unstable").GetProperty("disableSuggestions").GetBoolean());
        }

        // inlay hint inversion on serialize
        {
            JsonElement actual = new UserPreferences
            {
                InlayHints = new InlayHintsPreferences
                {
                    IncludeInlayParameterNameHints = IncludeInlayParameterNameHints.All,
                    IncludeInlayParameterNameHintsWhenArgumentMatchesName = Tristate.True,
                },
            }.ToJsonElement();
            JsonElement parameterNames = actual.GetProperty("inlayHints").GetProperty("parameterNames");
            Assert.Equal("all", parameterNames.GetProperty("enabled").GetString());
            Assert.False(parameterNames.GetProperty("suppressWhenArgumentMatchesName").GetBoolean()); // inverted
        }

        // mixed config and unstable fields
        {
            JsonElement actual = new UserPreferences
            {
                QuotePreference = "single",
                DisableSuggestions = Tristate.True,
                DisplayPartsForJSDoc = Tristate.True,
            }.ToJsonElement();
            Assert.Equal("single", actual.GetProperty("preferences").GetProperty("quoteStyle").GetString());
            JsonElement unstable = actual.GetProperty("unstable");
            Assert.True(unstable.GetProperty("disableSuggestions").GetBoolean());
            Assert.True(unstable.GetProperty("displayPartsForJSDoc").GetBoolean());
        }
    }

    public static TheoryData<string, string, UserPreferences> ParseUnstableCases => new()
    {
        {
            "unstable fields with correct casing",
            """{ "unstable": { "disableSuggestions": true, "maximumHoverLength": 100, "allowRenameOfImportPath": true } }""",
            new UserPreferences { DisableSuggestions = Tristate.True, MaximumHoverLength = 100, AllowRenameOfImportPath = Tristate.True }
        },
        {
            "nested preferences path",
            """{ "preferences": { "quoteStyle": "single", "useAliasesForRenames": true } }""",
            new UserPreferences { QuotePreference = "single", UseAliasesForRename = Tristate.True }
        },
        {
            "suggest section",
            """{ "suggest": { "autoImports": false, "includeCompletionsForImportStatements": true } }""",
            new UserPreferences { IncludeCompletionsForModuleExports = Tristate.False, IncludeCompletionsForImportStatements = Tristate.True }
        },
        {
            "inlayHints with invert",
            """{ "inlayHints": { "parameterNames": { "enabled": "all", "suppressWhenArgumentMatchesName": true } } }""",
            new UserPreferences
            {
                InlayHints = new InlayHintsPreferences
                {
                    IncludeInlayParameterNameHints = IncludeInlayParameterNameHints.All,
                    IncludeInlayParameterNameHintsWhenArgumentMatchesName = Tristate.False, // inverted
                },
            }
        },
        {
            "mixed config",
            """{ "unstable": { "displayPartsForJSDoc": true }, "preferences": { "importModuleSpecifier": "relative" }, "workspaceSymbols": { "excludeLibrarySymbols": true } }""",
            new UserPreferences
            {
                DisplayPartsForJSDoc = Tristate.True,
                ImportModuleSpecifierPreference = global::Ts.ModuleSpecifiers.ImportModuleSpecifierPreference.Relative,
                ExcludeLibrarySymbolsInNavTo = Tristate.True,
            }
        },
        {
            "stable config overrides unstable",
            """{ "unstable": { "quotePreference": "double" }, "preferences": { "quoteStyle": "single" } }""",
            new UserPreferences { QuotePreference = "single" } // stable wins
        },
        {
            "unstable sets value when no stable config",
            """{ "unstable": { "includeAutomaticOptionalChainCompletions": false } }""",
            new UserPreferences { IncludeAutomaticOptionalChainCompletions = Tristate.False }
        },
        {
            "any field can be passed via unstable by its raw name",
            """{ "unstable": { "quotePreference": "double", "includeCompletionsForModuleExports": true, "excludeLibrarySymbolsInNavTo": true } }""",
            new UserPreferences { QuotePreference = "double", IncludeCompletionsForModuleExports = Tristate.True, ExcludeLibrarySymbolsInNavTo = Tristate.True }
        },
        {
            "TypeScript raw names work in unstable section",
            """{ "unstable": { "includeCompletionsForModuleExports": true, "quotePreference": "single", "providePrefixAndSuffixTextForRename": true, "includeInlayParameterNameHints": "all", "organizeImportsLocale": "en" } }""",
            new UserPreferences
            {
                IncludeCompletionsForModuleExports = Tristate.True,
                QuotePreference = "single",
                UseAliasesForRename = Tristate.True,
                OrganizeImportsLocale = "en",
                InlayHints = new InlayHintsPreferences { IncludeInlayParameterNameHints = IncludeInlayParameterNameHints.All },
            }
        },
        {
            "old raw organize imports unicode preferences load as raw state",
            """{ "unstable": { "organizeImportsCollation": "unicode", "organizeImportsCaseFirst": "upper", "organizeImportsIgnoreCase": false, "organizeImportsNumericCollation": true } }""",
            new UserPreferences
            {
                OrganizeImportsCollation = OrganizeImportsCollation.Unicode,
                OrganizeImportsCaseFirst = OrganizeImportsCaseFirst.Upper,
                OrganizeImportsIgnoreCase = Tristate.False,
                OrganizeImportsNumericCollation = Tristate.True,
            }
        },
        {
            "old top-level raw organize imports unicode preferences load as raw state",
            """{ "organizeImportsCollation": "unicode", "organizeImportsIgnoreCase": true }""",
            new UserPreferences { OrganizeImportsCollation = OrganizeImportsCollation.Unicode, OrganizeImportsIgnoreCase = Tristate.True }
        },
        {
            "new top-level raw organize imports sort is accepted",
            """{ "organizeImportsSort": "natural" }""",
            new UserPreferences { OrganizeImportsSort = OrganizeImportsSort.Natural }
        },
        {
            "old raw organize imports ignore case loads as raw state",
            """{ "unstable": { "organizeImportsIgnoreCase": true } }""",
            new UserPreferences { OrganizeImportsIgnoreCase = Tristate.True }
        },
        {
            "new raw organize imports sort loads alongside old raw preferences",
            """{ "unstable": { "organizeImportsSort": "ordinal", "organizeImportsCollation": "unicode", "organizeImportsIgnoreCase": true } }""",
            new UserPreferences
            {
                OrganizeImportsSort = OrganizeImportsSort.Ordinal,
                OrganizeImportsCollation = OrganizeImportsCollation.Unicode,
                OrganizeImportsIgnoreCase = Tristate.True,
            }
        },
        {
            "old nested organize imports unicode preferences load as raw state",
            """{ "preferences": { "organizeImports": { "unicodeCollation": "unicode", "caseSensitivity": "caseSensitive", "numericCollation": true, "caseFirst": "upper" } } }""",
            new UserPreferences
            {
                OrganizeImportsCollation = OrganizeImportsCollation.Unicode,
                OrganizeImportsIgnoreCase = Tristate.False,
                OrganizeImportsNumericCollation = Tristate.True,
                OrganizeImportsCaseFirst = OrganizeImportsCaseFirst.Upper,
            }
        },
        {
            "new nested organize imports sort loads alongside old nested preferences",
            """{ "preferences": { "organizeImports": { "sort": "ordinalIgnoreCase", "unicodeCollation": "unicode", "caseSensitivity": "caseSensitive" } } }""",
            new UserPreferences
            {
                OrganizeImportsSort = OrganizeImportsSort.OrdinalIgnoreCase,
                OrganizeImportsCollation = OrganizeImportsCollation.Unicode,
                OrganizeImportsIgnoreCase = Tristate.False,
            }
        },
    };

    // userpreferences_test.go:166-425 — TestUserPreferencesParseUnstable.
    [Theory]
    [MemberData(nameof(ParseUnstableCases))]
    public void TestUserPreferencesParseUnstable(string name, string json, UserPreferences expected)
    {
        _ = name;
        UserPreferences parsed = new UserPreferences().WithConfig(Config(json));
        AssertDeepEqual(expected, parsed);
    }

    // userpreferences_test.go:427-457 — TestUserPreferencesReportStyleChecksAsWarnings.
    [Fact]
    public void TestUserPreferencesReportStyleChecksAsWarnings()
    {
        // reportStyleChecksAsWarnings via config path
        Assert.Equal(Tristate.False, Parse("""{ "js/ts": { "reportStyleChecksAsWarnings": false } }""").ReportStyleChecksAsWarnings);
        // reportStyleChecksAsWarnings defaults to true
        Assert.Equal(Tristate.True, UserPreferences.NewDefaultUserPreferences().ReportStyleChecksAsWarnings);
        // reportStyleChecksAsWarnings via unstable section
        Assert.Equal(Tristate.False, Parse("""{ "js/ts": { "unstable": { "reportStyleChecksAsWarnings": false } } }""").ReportStyleChecksAsWarnings);
    }

    // userpreferences_test.go:459-512 — TestUserPreferencesParseServerFeaturePreferences.
    [Fact]
    public void TestUserPreferencesParseServerFeaturePreferences()
    {
        // preferred server feature settings
        UserPreferences prefs = Parse("""
            { "js/ts": { "validate": { "enabled": false }, "format": { "enabled": false }, "autoClosingTags": { "enabled": false } } }
            """);
        Assert.Equal(Tristate.False, prefs.EnableValidation);
        Assert.Equal(Tristate.False, prefs.EnableFormatting);
        Assert.Equal(Tristate.False, prefs.EnableAutoClosingTags);

        // legacy server feature fallbacks
        prefs = Parse("""
            { "typescript": { "validate": { "enable": false }, "format": { "enable": false }, "autoClosingTags": false } }
            """);
        Assert.Equal(Tristate.False, prefs.EnableValidation);
        Assert.Equal(Tristate.False, prefs.EnableFormatting);
        Assert.Equal(Tristate.False, prefs.EnableAutoClosingTags);

        // preferred settings take precedence over fallbacks
        prefs = Parse("""
            {
                "typescript": { "validate": { "enable": false }, "format": { "enable": false }, "autoClosingTags": false },
                "js/ts": { "validate": { "enabled": true }, "format": { "enabled": true }, "autoClosingTags": { "enabled": true } }
            }
            """);
        Assert.Equal(Tristate.True, prefs.EnableValidation);
        Assert.Equal(Tristate.True, prefs.EnableFormatting);
        Assert.Equal(Tristate.True, prefs.EnableAutoClosingTags);
    }

    // userpreferences_test.go:514-589 — TestUserPreferencesParseJSDocCompletionPreferences.
    [Fact]
    public void TestUserPreferencesParseJSDocCompletionPreferences()
    {
        // unified jsdoc enabled setting
        Assert.Equal(Tristate.False, Parse("""{ "js/ts": { "suggest": { "jsdoc": { "enabled": false } } } }""").EnableJSDocCompletions);
        // language fallback completeJSDocs setting
        Assert.Equal(Tristate.False, Parse("""{ "typescript": { "suggest": { "completeJSDocs": false } } }""").EnableJSDocCompletions);
        // unified jsdoc enabled takes precedence over language fallback
        Assert.Equal(Tristate.True, Parse("""
            { "typescript": { "suggest": { "completeJSDocs": false } }, "js/ts": { "suggest": { "jsdoc": { "enabled": true } } } }
            """).EnableJSDocCompletions);
        // unified jsdoc generateReturns setting
        Assert.Equal(Tristate.False, Parse("""{ "js/ts": { "suggest": { "jsdoc": { "generateReturns": false } } } }""").GenerateReturnInDocTemplate);
        // language jsdoc generateReturns setting
        Assert.Equal(Tristate.False, Parse("""{ "typescript": { "suggest": { "jsdoc": { "generateReturns": false } } } }""").GenerateReturnInDocTemplate);
    }

    // userpreferences_test.go:591-644 — TestUserPreferencesParseATA.
    [Fact]
    public void TestUserPreferencesParseATA()
    {
        // ParseUserPreferences with unified ATA setting in js/ts section
        UserPreferences prefs = Parse("""{ "js/ts": { "tsserver": { "automaticTypeAcquisition": { "enabled": false } } } }""");
        Assert.True(prefs.IsATADisabled());
        Assert.Equal(Tristate.False, prefs.AutomaticTypeAcquisitionEnabled);

        // ParseUserPreferences with deprecated disableAutomaticTypeAcquisition in typescript section
        prefs = Parse("""{ "typescript": { "disableAutomaticTypeAcquisition": true } }""");
        Assert.True(prefs.IsATADisabled());
        Assert.Equal(Tristate.True, prefs.DisableAutomaticTypeAcquisition);

        // unified setting takes precedence over deprecated setting
        prefs = Parse("""
            { "typescript": { "disableAutomaticTypeAcquisition": true }, "js/ts": { "tsserver": { "automaticTypeAcquisition": { "enabled": true } } } }
            """);
        Assert.False(prefs.IsATADisabled());
        Assert.Equal(Tristate.True, prefs.AutomaticTypeAcquisitionEnabled);

        // IsATADisabled returns false when neither setting is configured
        Assert.False(UserPreferences.NewDefaultUserPreferences().IsATADisabled());
    }
}
