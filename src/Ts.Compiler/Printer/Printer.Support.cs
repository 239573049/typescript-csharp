// Ported from tsgo internal/printer/printer.go (ListFormat + bracket helpers) and
// internal/printer/utilities.go (lineCharacterCache).

using System;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using SourceMapSource = global::Ts.SourceMap.Source;

// Go: type ListFormat int
[Flags]
public enum ListFormat
{
    None = 0,

    // Line separators
    SingleLine = 0, // Prints the list on a single line (default).
    MultiLine = 1 << 0, // Prints the list on multiple lines.
    PreserveLines = 1 << 1, // Prints the list using line preservation if possible.
    LinesMask = SingleLine | MultiLine | PreserveLines,

    // Delimiters
    NotDelimited = 0, // There is no delimiter between list items (default).
    BarDelimited = 1 << 2, // Each list item is space-and-bar (" |") delimited.
    AmpersandDelimited = 1 << 3, // Each list item is space-and-ampersand (" &") delimited.
    CommaDelimited = 1 << 4, // Each list item is comma (",") delimited.
    AsteriskDelimited = 1 << 5, // Each list item is asterisk ("\n *") delimited, used with JSDoc.
    DelimitersMask = BarDelimited | AmpersandDelimited | CommaDelimited | AsteriskDelimited,

    AllowTrailingComma = 1 << 6, // Write a trailing comma (",") if present.

    // Whitespace
    Indented = 1 << 7, // The list should be indented.
    SpaceBetweenBraces = 1 << 8, // Inserts a space after the opening brace and before the closing brace.
    SpaceBetweenSiblings = 1 << 9, // Inserts a space between each sibling node.

    // Brackets/Braces
    Braces = 1 << 10, // The list is surrounded by "{" and "}".
    Parenthesis = 1 << 11, // The list is surrounded by "(" and ")".
    AngleBrackets = 1 << 12, // The list is surrounded by "<" and ">".
    SquareBrackets = 1 << 13, // The list is surrounded by "[" and "]".
    BracketsMask = Braces | Parenthesis | AngleBrackets | SquareBrackets,

    OptionalIfNil = 1 << 14, // Do not emit brackets if the list is nil.
    OptionalIfEmpty = 1 << 15, // Do not emit brackets if the list is empty.
    Optional = OptionalIfNil | OptionalIfEmpty,

    // Other
    PreferNewLine = 1 << 16, // Prefer adding a LineTerminator between synthesized nodes.
    NoTrailingNewLine = 1 << 17, // Do not emit a trailing NewLine for a MultiLine list.
    NoInterveningComments = 1 << 18, // Do not emit comments between each node
    NoSpaceIfEmpty = 1 << 19, // If the literal is empty, do not add spaces between braces.
    SingleElement = 1 << 20,
    SpaceAfterList = 1 << 21, // Add space after list

    // Precomputed Formats
    Modifiers = SingleLine | SpaceBetweenSiblings | NoInterveningComments | SpaceAfterList,
    HeritageClauses = SingleLine | SpaceBetweenSiblings,
    SingleLineTypeLiteralMembers = SingleLine | SpaceBetweenBraces | SpaceBetweenSiblings,
    MultiLineTypeLiteralMembers = MultiLine | Indented | OptionalIfEmpty,

    SingleLineTupleTypeElements = CommaDelimited | SpaceBetweenSiblings | SingleLine,
    MultiLineTupleTypeElements = CommaDelimited | Indented | SpaceBetweenSiblings | MultiLine,
    UnionTypeConstituents = BarDelimited | SpaceBetweenSiblings | SingleLine,
    IntersectionTypeConstituents = AmpersandDelimited | SpaceBetweenSiblings | SingleLine,
    ObjectBindingPatternElements = SingleLine | AllowTrailingComma | SpaceBetweenBraces | CommaDelimited | SpaceBetweenSiblings | NoSpaceIfEmpty,
    ArrayBindingPatternElements = SingleLine | AllowTrailingComma | CommaDelimited | SpaceBetweenSiblings | NoSpaceIfEmpty,
    ObjectLiteralExpressionProperties = PreserveLines | CommaDelimited | SpaceBetweenSiblings | SpaceBetweenBraces | Indented | Braces | NoSpaceIfEmpty,
    ImportAttributes = PreserveLines | CommaDelimited | SpaceBetweenSiblings | SpaceBetweenBraces | Indented | Braces | NoSpaceIfEmpty,
    ArrayLiteralExpressionElements = PreserveLines | CommaDelimited | SpaceBetweenSiblings | AllowTrailingComma | Indented | SquareBrackets,
    CommaListElements = CommaDelimited | SpaceBetweenSiblings | SingleLine,
    CallExpressionArguments = CommaDelimited | SpaceBetweenSiblings | SingleLine | Parenthesis,
    NewExpressionArguments = CommaDelimited | SpaceBetweenSiblings | SingleLine | Parenthesis | OptionalIfNil,
    TemplateExpressionSpans = SingleLine | NoInterveningComments,
    SingleLineBlockStatements = SpaceBetweenBraces | SpaceBetweenSiblings | SingleLine,
    MultiLineBlockStatements = Indented | MultiLine,
    VariableDeclarationList = CommaDelimited | SpaceBetweenSiblings | SingleLine,
    SingleLineFunctionBodyStatements = SingleLine | SpaceBetweenSiblings | SpaceBetweenBraces,
    MultiLineFunctionBodyStatements = MultiLine,
    ClassHeritageClauses = SingleLine,
    ClassMembers = Indented | MultiLine,
    InterfaceMembers = Indented | MultiLine,
    EnumMembers = CommaDelimited | Indented | MultiLine,
    CaseBlockClauses = Indented | MultiLine,
    NamedImportsOrExportsElements = CommaDelimited | SpaceBetweenSiblings | AllowTrailingComma | SingleLine | SpaceBetweenBraces | NoSpaceIfEmpty,
    JsxElementOrFragmentChildren = SingleLine | NoInterveningComments,
    JsxElementAttributes = SingleLine | SpaceBetweenSiblings | NoInterveningComments,
    CaseOrDefaultClauseStatements = Indented | MultiLine | NoTrailingNewLine | OptionalIfEmpty,
    HeritageClauseTypes = CommaDelimited | SpaceBetweenSiblings | SingleLine,
    SourceFileStatements = MultiLine | NoTrailingNewLine,
    Decorators = MultiLine | Optional | SpaceAfterList,
    TypeArguments = CommaDelimited | SpaceBetweenSiblings | SingleLine | AngleBrackets | Optional,
    TypeParameters = CommaDelimited | SpaceBetweenSiblings | SingleLine | AngleBrackets | Optional,
    Parameters = CommaDelimited | SpaceBetweenSiblings | SingleLine | Parenthesis,
    SingleArrowParameter = CommaDelimited | SpaceBetweenSiblings | SingleLine,
    IndexSignatureParameters = CommaDelimited | SpaceBetweenSiblings | SingleLine | Indented | SquareBrackets,
    JSDocComment = MultiLine | AsteriskDelimited,
    ImportClauseEntries = ImportAttributes, // Deprecated: Use ImportAttributes
}

public sealed partial class Printer
{
    // Go: func getIndentString(indent int, indentSize int) string  (textwriter.go:123; package-level helper
    // duplicated here because the TextWriter version is private).
    private static string GetIndentString(int indent, int indentSize)
    {
        var builder = new StringBuilder(indent * indentSize);
        for (int i = 0; i < indent * indentSize; i++)
        {
            builder.Append(' ');
        }
        return builder.ToString();
    }

    private static string GetOpeningBracket(ListFormat format)
    {
        switch (format & ListFormat.BracketsMask)
        {
            case ListFormat.Braces:
                return "{";
            case ListFormat.Parenthesis:
                return "(";
            case ListFormat.AngleBrackets:
                return "<";
            case ListFormat.SquareBrackets:
                return "[";
            default:
                throw new InvalidOperationException($"Unexpected bracket: {format & ListFormat.BracketsMask}");
        }
    }

    private static string GetClosingBracket(ListFormat format)
    {
        switch (format & ListFormat.BracketsMask)
        {
            case ListFormat.Braces:
                return "}";
            case ListFormat.Parenthesis:
                return ")";
            case ListFormat.AngleBrackets:
                return ">";
            case ListFormat.SquareBrackets:
                return "]";
            default:
                throw new InvalidOperationException($"Unexpected bracket: {format & ListFormat.BracketsMask}");
        }
    }
}

// Go: type lineCharacterCache struct (internal/printer/utilities.go:903)
internal sealed class LineCharacterCache
{
    private readonly int[] lineMap;
    private readonly string text;
    private int cachedLine;
    private int cachedPos;
    private int cachedChar;
    private bool hasCached;

    // Go: func newLineCharacterCache(source sourcemap.Source) *lineCharacterCache
    public LineCharacterCache(SourceMapSource source)
    {
        lineMap = source.ECMALineMap();
        text = source.Text();
    }

    // Go: func (c *lineCharacterCache) getLineAndCharacter(pos int) (line int, character core.UTF16Offset)
    public (int Line, int Character) GetLineAndCharacter(int pos)
    {
        int line = global::Ts.Scanner.Scanner.ComputeLineOfPosition(lineMap, pos);
        int lineStart = lineMap[line];
        int endPos = Math.Min(pos, text.Length);
        int character;
        if (hasCached && line == cachedLine && endPos >= cachedPos)
        {
            character = cachedChar + global::Ts.Core.CoreUtil.UTF16Len(text[cachedPos..endPos]);
        }
        else
        {
            character = global::Ts.Core.CoreUtil.UTF16Len(text[lineStart..endPos]);
        }
        int newCachedChar = character;
        character += pos - endPos;
        cachedLine = line;
        cachedPos = endPos;
        cachedChar = newCachedChar;
        hasCached = true;
        return (line, character);
    }
}

// PORT-NOTE: Go's `*ast.SourceFile` satisfies `sourcemap.Source` structurally. In C# SourceFile does not
// declare the interface (and this wave must not modify the ast package), so we adapt via this thin wrapper.
// It is only used on the source-map emit path (disabled unless a source-map generator is supplied). Reference
// identity of the wrapper is preserved per SourceFile so the printer's `mostRecentSourceMapSource` caching
// still holds.
internal sealed class SourceFileSourceMapSource : SourceMapSource
{
    public SourceFile SourceFile { get; }

    private SourceFileSourceMapSource(SourceFile sourceFile)
    {
        SourceFile = sourceFile;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SourceFile, SourceFileSourceMapSource> Cache = new();

    public static SourceFileSourceMapSource For(SourceFile sourceFile)
    {
        return Cache.GetValue(sourceFile, sf => new SourceFileSourceMapSource(sf));
    }

    public string Text() => SourceFile.Text();

    public string FileName() => SourceFile.FileName();

    public int[] ECMALineMap() => SourceFile.ECMALineMap();
}
