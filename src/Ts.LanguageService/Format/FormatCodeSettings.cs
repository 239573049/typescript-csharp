// Ported from tsgo internal/ls/lsutil/formatcodeoptions.go
//
// PORT-NOTE: The Go `format` package pulls FormatCodeSettings / EditorSettings from
// the `ls/lsutil` package, which is not yet ported (plan/06 §8.2 lists lsutil as a
// prerequisite). The dispatch bootstrap authorizes defining these settings inside
// Ts.Format until lsutil lands: "FormatCodeSettings/EditorSettings 若在 tsoptions/
// core 已落地则用，否则在 Format 内定义." When lsutil is ported these types should
// move there (namespace Ts.Ls.LsUtil) and Format should alias/reference them.
//
// The LSP conversion helpers (FromLSFormatOptions / ToLSFormatOptions) and the
// `parseIndentStyle`/`parseSemicolonPreference` raw-option parsers are omitted here
// because they depend on lsproto (not yet ported); they will be reintroduced with
// lsutil. The struct shape, defaults, and GetDefaultFormatCodeSettings are ported
// 1:1 since the rule engine reads them directly.

using Ts.Core;

namespace Ts.Format;

// Go: type IndentStyle int ; iota order None/Block/Smart.
public enum IndentStyle
{
    None = 0,
    Block = 1,
    Smart = 2,
}

// Go: type SemicolonPreference string.
public enum SemicolonPreference
{
    // Go values are the string literals "ignore"/"insert"/"remove"; the engine only
    // compares them by identity, so an enum is faithful.
    Ignore = 0,
    Insert = 1,
    Remove = 2,
}

// Go: type EditorSettings struct { ... }
// PORT-NOTE: Go embeds EditorSettings into FormatCodeSettings (struct embedding).
// C# has no field embedding; we flatten the EditorSettings fields directly into
// FormatCodeSettings and keep EditorSettings as a standalone type for callers that
// reference it. The format engine only reads via FormatCodeSettings.
public sealed class EditorSettings
{
    // Go: BaseIndentSize int
    public int BaseIndentSize;

    // Go: IndentSize int
    public int IndentSize;

    // Go: TabSize int
    public int TabSize;

    // Go: NewLineCharacter string
    public string NewLineCharacter = "";

    // Go: ConvertTabsToSpaces core.Tristate
    public Tristate ConvertTabsToSpaces;

    // Go: IndentStyle IndentStyle
    public IndentStyle IndentStyle;

    // Go: TrimTrailingWhitespace core.Tristate
    public Tristate TrimTrailingWhitespace;
}

// Go: type FormatCodeSettings struct { EditorSettings; ... }
public sealed class FormatCodeSettings
{
    // --- embedded EditorSettings fields (flattened, see PORT-NOTE above) ---
    public int BaseIndentSize;
    public int IndentSize;
    public int TabSize;
    public string NewLineCharacter = "";
    public Tristate ConvertTabsToSpaces;
    public IndentStyle IndentStyle;
    public Tristate TrimTrailingWhitespace;

    // --- FormatCodeSettings-specific fields ---
    public Tristate InsertSpaceAfterCommaDelimiter;
    public Tristate InsertSpaceAfterSemicolonInForStatements;
    public Tristate InsertSpaceBeforeAndAfterBinaryOperators;
    public Tristate InsertSpaceAfterConstructor;
    public Tristate InsertSpaceAfterKeywordsInControlFlowStatements;
    public Tristate InsertSpaceAfterFunctionKeywordForAnonymousFunctions;
    public Tristate InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis;
    public Tristate InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets;
    public Tristate InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces;
    public Tristate InsertSpaceAfterOpeningAndBeforeClosingEmptyBraces;
    public Tristate InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces;
    public Tristate InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces;
    public Tristate InsertSpaceAfterTypeAssertion;
    public Tristate InsertSpaceBeforeFunctionParenthesis;
    public Tristate PlaceOpenBraceOnNewLineForFunctions;
    public Tristate PlaceOpenBraceOnNewLineForControlBlocks;
    public Tristate InsertSpaceBeforeTypeAnnotation;
    public Tristate IndentMultiLineObjectLiteralBeginningOnBlankLine;
    public SemicolonPreference Semicolons;
    public Tristate IndentSwitchCase;

    // Go: func GetDefaultFormatCodeSettings() FormatCodeSettings
    //
    // PORT-NOTE: Go uses printer.GetDefaultIndentSize() for IndentSize/TabSize.
    // The printer package is not yet ported; its default indent size is 4
    // (tsgo internal/printer default). We inline the literal 4 and note it here.
    public const int DefaultIndentSize = 4; // printer.GetDefaultIndentSize()

    public static FormatCodeSettings GetDefaultFormatCodeSettings()
    {
        return new FormatCodeSettings
        {
            // embedded EditorSettings defaults
            IndentSize = DefaultIndentSize,
            TabSize = DefaultIndentSize,
            NewLineCharacter = "\n",
            ConvertTabsToSpaces = Tristate.True,
            IndentStyle = IndentStyle.Smart,
            TrimTrailingWhitespace = Tristate.True,

            // FormatCodeSettings defaults
            InsertSpaceAfterConstructor = Tristate.False,
            InsertSpaceAfterCommaDelimiter = Tristate.True,
            InsertSpaceAfterSemicolonInForStatements = Tristate.True,
            InsertSpaceBeforeAndAfterBinaryOperators = Tristate.True,
            InsertSpaceAfterKeywordsInControlFlowStatements = Tristate.True,
            InsertSpaceAfterFunctionKeywordForAnonymousFunctions = Tristate.False,
            InsertSpaceAfterOpeningAndBeforeClosingNonemptyParenthesis = Tristate.False,
            InsertSpaceAfterOpeningAndBeforeClosingNonemptyBrackets = Tristate.False,
            InsertSpaceAfterOpeningAndBeforeClosingNonemptyBraces = Tristate.True,
            InsertSpaceAfterOpeningAndBeforeClosingTemplateStringBraces = Tristate.False,
            InsertSpaceAfterOpeningAndBeforeClosingJsxExpressionBraces = Tristate.False,
            InsertSpaceBeforeFunctionParenthesis = Tristate.False,
            PlaceOpenBraceOnNewLineForFunctions = Tristate.False,
            PlaceOpenBraceOnNewLineForControlBlocks = Tristate.False,
            Semicolons = SemicolonPreference.Ignore,
            IndentSwitchCase = Tristate.True,
        };
    }
}
