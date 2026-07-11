// Ported from tsgo internal/printer/printer.go
//
// Package printer exports a Printer for pretty-printing TS ASTs. printer.go has zero checker coupling
// (it reaches the checker only via the EmitResolver interface, which is ported). This is split by Go
// section into Printer.cs (struct + low-level writing + tokens + literals), Printer.Names.cs,
// Printer.Types.cs, Printer.Expressions.cs, Printer.Statements.cs, Printer.Declarations.cs,
// Printer.Jsx.cs, and Printer.Emit.cs (dispatch + lists + comments + source maps + name generation).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using EF = global::Ts.Printer.EmitFlags;
using StringUtil = global::Ts.StringUtil.StringUtil;
using Scanner = global::Ts.Scanner.Scanner;
using AstUtil = global::Ts.Ast.AstUtilities;
using SourceMapSource = global::Ts.SourceMap.Source;
using SourceMapGenerator = global::Ts.SourceMap.Generator;

public struct PrinterOptions
{
    public bool RemoveComments;
    public NewLineKind NewLine;
    public bool NoEmitHelpers;
    public ScriptTarget Target;
    public bool SourceMap;
    public bool InlineSourceMap;
    public bool InlineSources;
    public bool OmitBraceSourceMapPositions;
    public bool OnlyPrintJSDocStyle;
    public bool NeverAsciiEscape;
    public bool PreserveSourceNewlines;
    public bool TerminateUnterminatedLiterals; // !!!

    public PrinterOptions()
    {
    }
}

public struct PrintHandlers
{
    // A hook used by the Printer when generating unique names to avoid collisions with
    // globally defined names that exist outside of the current source file.
    public Func<string, bool>? HasGlobalName;

    public Action<Node?>? OnBeforeEmitNode;
    public Action<Node?>? OnAfterEmitNode;
    public Action<NodeList?>? OnBeforeEmitNodeList;
    public Action<NodeList?>? OnAfterEmitNodeList;
    public Action<Node?>? OnBeforeEmitToken;
    public Action<Node?>? OnAfterEmitToken;

    public PrintHandlers()
    {
    }
}

// Go: type WriteKind int
public enum WriteKind
{
    None = 0,
    Keyword,
    Operator,
    Punctuation,
    StringLiteral,
    Parameter,
    Property,
    Comment,
    Literal,
}

// Go: type tokenEmitFlags uint32
[Flags]
internal enum TokenEmitFlags : uint
{
    None = 0,
    NoComments = 1 << 0,
    IndentLeadingComments = 1 << 1,
    NoSourceMaps = 1 << 2,
}

// Go: type commentSeparator uint32
internal enum CommentSeparator : uint
{
    None = 0,
    Before,
    After,
}

internal struct DetachedCommentsInfo
{
    public int NodePos;
    public int DetachedCommentEndPos;
}

// Go: type commentState struct. Modeled as a class because printer.go passes it around as *commentState
// (arena.New() → new()).
internal sealed class CommentState
{
    public EmitFlags EmitFlags; // holds the emit flags for the current node
    public TextRange CommentRange; // holds the comment range calculated for the current node
    public int ContainerPos; // captures the value of containerPos prior to entering an node
    public int ContainerEnd; // captures the value of containerEnd prior to entering an node
    public int DeclarationListContainerEnd; // captures the value of declarationListContainerEnd prior to entering an node
}

// Go: type sourceMapState struct. Modeled as a class (arena.New() → new()).
internal sealed class SourceMapState
{
    public EmitFlags EmitFlags; // holds the emit flags for the current node
    public TextRange SourceMapRange; // holds the source map range calculated for the current node
    public bool HasTokenSourceMapRange; // captures whether the source map range was set for the current node
}

internal struct PrinterState
{
    public CommentState? CommentState;
    public SourceMapState? SourceMapState;
}

public sealed partial class Printer
{
    // Go: embedded PrintHandlers
    public PrintHandlers PrintHandlers;
    public PrinterOptions Options;
    private EmitContext emitContext;
    private SourceFile? currentSourceFile;
    private Dictionary<string, IdentifierNode>? uniqueHelperNames;
    private IdentifierNode? externalHelpersModuleName;
    private int nextListElementPos;
    private EmitTextWriter writer = null!;
    private EmitTextWriter? ownWriter;
    private WriteKind writeKind;
    private bool sourceMapsDisabled;
    private SourceMapGenerator? sourceMapGenerator;
    private SourceMapSource? sourceMapSource;
    private int sourceMapSourceIndex;
    private bool sourceMapSourceIsJson;
    private LineCharacterCache? sourceMapLineCharCache;
    private SourceMapSource? mostRecentSourceMapSource;
    private int mostRecentSourceMapSourceIndex;
    private int containerPos;
    private int containerEnd;
    private int declarationListContainerEnd;
    private global::Ts.Core.Stack<DetachedCommentsInfo> detachedCommentsInfo = new();
    private bool commentsDisabled;
    private bool inExtends; // whether we are emitting the `extends` clause of a ConditionalTypeNode or InferTypeNode
    private NameGenerator nameGenerator = new();
    private Func<string, string> makeFileLevelOptimisticUniqueName = null!;
    public Dictionary<IdentifierNode, Symbol>? IdToSymbol;

    // Convenience forwarders for the embedded PrintHandlers fields (Go promoted-field access).
    private Func<string, bool>? HasGlobalName => PrintHandlers.HasGlobalName;
    private Action<Node?>? OnBeforeEmitNode => PrintHandlers.OnBeforeEmitNode;
    private Action<Node?>? OnAfterEmitNode => PrintHandlers.OnAfterEmitNode;
    private Action<NodeList?>? OnBeforeEmitNodeList => PrintHandlers.OnBeforeEmitNodeList;
    private Action<NodeList?>? OnAfterEmitNodeList => PrintHandlers.OnAfterEmitNodeList;
    private Action<Node?>? OnBeforeEmitToken => PrintHandlers.OnBeforeEmitToken;
    private Action<Node?>? OnAfterEmitToken => PrintHandlers.OnAfterEmitToken;

    // Go: func NewPrinter(options, handlers, emitContext) *Printer
    public static Printer NewPrinter(PrinterOptions options, PrintHandlers handlers, EmitContext? emitContext)
    {
        var printer = new Printer(options, handlers, emitContext);
        return printer;
    }

    private Printer(PrinterOptions options, PrintHandlers handlers, EmitContext? emitContext)
    {
        PrintHandlers = handlers;
        Options = options;
        this.emitContext = emitContext ?? EmitContext.NewEmitContext();
        // wire up name generator
        nameGenerator.Context = this.emitContext;
        nameGenerator.GetTextOfNode = node => GetTextOfNode(node, false);
        nameGenerator.IsFileLevelUniqueNameInCurrentFile = IsFileLevelUniqueNameInCurrentFile;
        makeFileLevelOptimisticUniqueName = name => nameGenerator.MakeFileLevelOptimisticUniqueName(name);
        containerPos = -1;
        containerEnd = -1;
        declarationListContainerEnd = -1;
        commentsDisabled = options.RemoveComments;
    }

    private string GetLiteralTextOfNode(Node node, SourceFile? sourceFile, GetLiteralTextFlags flags)
    {
        if (NodeTests.IsStringLiteral(node))
        {
            if (emitContext.textSource is not null && emitContext.textSource.TryGetValue(node, out var textSourceNode) && textSourceNode is not null)
            {
                string text;
                switch (textSourceNode.Kind)
                {
                    default:
                        return GetLiteralTextOfNode(textSourceNode, AstUtil.GetSourceFileOfNode(textSourceNode), flags);
                    case SyntaxKind.NumericLiteral:
                        text = textSourceNode.Text();
                        break;
                    case SyntaxKind.Identifier:
                    case SyntaxKind.PrivateIdentifier:
                    case SyntaxKind.JsxNamespacedName:
                        text = GetTextOfNode(textSourceNode, false);
                        break;
                }

                if ((flags & GetLiteralTextFlags.JsxAttributeEscape) != 0)
                {
                    return "\"" + PrinterUtilities.EscapeJsxAttributeString(text, QuoteChar.DoubleQuote) + "\"";
                }
                else if ((flags & GetLiteralTextFlags.NeverAsciiEscape) != 0 || (emitContext.EmitFlags(node) & EF.NoAsciiEscaping) != 0)
                {
                    return "\"" + PrinterUtilities.EscapeString(text, QuoteChar.DoubleQuote) + "\"";
                }
                else
                {
                    return "\"" + PrinterUtilities.EscapeNonAsciiString(text, QuoteChar.DoubleQuote) + "\"";
                }
            }
        }
        // !!! Printer option to control whether to terminate unterminated literals
        if ((emitContext.EmitFlags(node) & EF.NoAsciiEscaping) != 0)
        {
            flags |= GetLiteralTextFlags.NeverAsciiEscape;
        }
        if (Options.Target >= ScriptTarget.ES2021)
        {
            flags |= GetLiteralTextFlags.AllowNumericSeparator;
        }
        return PrinterUtilities.GetLiteralText(node, sourceFile ?? currentSourceFile, flags);
    }

    // `node` must be one of Identifier | PrivateIdentifier | LiteralExpression | JsxNamespacedName
    private string GetTextOfNode(Node node, bool includeTrivia)
    {
        if (AstUtil.IsMemberName(node) && emitContext.autoGenerate is not null && emitContext.autoGenerate.TryGetValue(node, out var ag) && ag is not null)
        {
            return nameGenerator.GenerateName(node);
        }

        if (NodeTests.IsStringLiteral(node))
        {
            if (emitContext.textSource is not null && emitContext.textSource.TryGetValue(node, out var textSourceNode) && textSourceNode is not null)
            {
                return GetTextOfNode(textSourceNode, includeTrivia);
            }
        }

        bool canUseSourceFile = currentSourceFile is not null && node.Parent is not null && !AstUtil.NodeIsSynthesized(node);

        switch (node.Kind)
        {
            case SyntaxKind.Identifier:
            case SyntaxKind.PrivateIdentifier:
            case SyntaxKind.JsxNamespacedName:
                if (!canUseSourceFile || AstUtil.GetSourceFileOfNode(node) != emitContext.MostOriginal(currentSourceFile!.AsNode())!.AsSourceFile())
                {
                    return node.Text();
                }
                break;
            case SyntaxKind.StringLiteral:
            case SyntaxKind.NumericLiteral:
            case SyntaxKind.BigIntLiteral:
            case SyntaxKind.NoSubstitutionTemplateLiteral:
            case SyntaxKind.TemplateHead:
            case SyntaxKind.TemplateMiddle:
            case SyntaxKind.TemplateTail:
                return GetLiteralTextOfNode(node, null /*sourceFile*/, GetLiteralTextFlags.None);
            default:
                throw new InvalidOperationException($"unexpected node: {node.Kind}");
        }
        return Scanner.GetSourceTextOfNodeFromSourceFile(currentSourceFile!, node, includeTrivia);
    }

    //
    // Low-level writing
    //

    private void WriteAs(string text, WriteKind writeKind)
    {
        switch (writeKind)
        {
            case WriteKind.None:
                writer.Write(text);
                break;
            case WriteKind.Parameter:
                WriteParameter(text);
                break;
            case WriteKind.Keyword:
                WriteKeyword(text);
                break;
            case WriteKind.Operator:
                WriteOperator(text);
                break;
            case WriteKind.Property:
                WriteProperty(text);
                break;
            case WriteKind.Punctuation:
                WritePunctuation(text);
                break;
            case WriteKind.StringLiteral:
                writer.WriteStringLiteral(text);
                break;
            case WriteKind.Comment:
                WriteComment(text);
                break;
            case WriteKind.Literal:
                WriteLiteral(text);
                break;
            default:
                throw new InvalidOperationException($"unexpected printer.WriteKind: {writeKind}");
        }
    }

    private void Write(string text)
    {
        WriteAs(text, writeKind);
    }

    private WriteKind SetWriteKind(WriteKind kind)
    {
        var previous = writeKind;
        writeKind = kind;
        return previous;
    }

    private void WriteSymbol(string text, Symbol? optSymbol)
    {
        if (optSymbol is null)
        {
            Write(text);
        }
        else
        {
            writer.WriteSymbol(text, optSymbol);
        }
    }

    private void WriteLiteral(string text) => writer.WriteLiteral(text);

    private void WritePunctuation(string text) => writer.WritePunctuation(text);

    private void WriteOperator(string text) => writer.WriteOperator(text);

    private void WriteKeyword(string text) => writer.WriteKeyword(text);

    private void WriteProperty(string text) => writer.WriteProperty(text);

    private void WriteParameter(string text) => writer.WriteParameter(text);

    private void WriteComment(string text) => writer.WriteComment(text);

    private void WriteSpace() => writer.WriteSpace(" ");

    private void WriteLine() => writer.WriteLine();

    private void WriteLineRepeat(int count)
    {
        for (int c = 0; c < count; c++)
        {
            WriteLine();
        }
    }

    private void WriteLines(string text)
    {
        var lines = StringUtil.SplitLines(text);
        var indentation = StringUtil.GuessIndentation(lines);
        foreach (var lineOriginal in lines)
        {
            var line = lineOriginal;
            if (indentation > 0)
            {
                line = line[indentation..];
            }
            if (line.Length > 0)
            {
                WriteLine();
                Write(line);
            }
        }
    }

    private void WriteTrailingSemicolon() => writer.WriteTrailingSemicolon(";");

    private void IncreaseIndent() => writer.IncreaseIndent();

    private void DecreaseIndent() => writer.DecreaseIndent();

    private void IncreaseIndentIf(bool indentRequested)
    {
        if (indentRequested)
        {
            IncreaseIndent();
        }
    }

    private void DecreaseIndentIf(bool indentRequested)
    {
        if (indentRequested)
        {
            DecreaseIndent();
        }
    }

    private void WriteLineOrSpace(Node parentNode, Node? prevChildNode, Node? nextChildNode)
    {
        if (ShouldEmitOnSingleLine(parentNode))
        {
            WriteSpace();
        }
        else if (Options.PreserveSourceNewlines)
        {
            var lines = GetLinesBetweenNodes(parentNode, prevChildNode, nextChildNode);
            if (lines > 0)
            {
                WriteLineRepeat(lines);
            }
            else
            {
                WriteSpace();
            }
        }
        else
        {
            WriteLine();
        }
    }

    private void WriteLinesAndIndent(int lineCount, bool writeSpaceIfNotIndenting)
    {
        if (lineCount > 0)
        {
            IncreaseIndent();
            WriteLineRepeat(lineCount);
        }
        else if (writeSpaceIfNotIndenting)
        {
            WriteSpace();
        }
    }

    private bool WriteLineSeparatorsAndIndentBefore(Node node, Node parent)
    {
        if (Options.PreserveSourceNewlines)
        {
            var leadingNewlines = GetLeadingLineTerminatorCount(parent, node, ListFormat.None);
            if (leadingNewlines > 0)
            {
                WriteLinesAndIndent(leadingNewlines, false /*writeSpaceIfNotIndenting*/);
                return true;
            }
        }
        return false;
    }

    private void WriteLineSeparatorsAfter(Node node, Node parent)
    {
        if (Options.PreserveSourceNewlines)
        {
            var trailingNewlines = GetClosingLineTerminatorCount(parent, node, ListFormat.None, new TextRange(-1, -1) /*childrenTextRange*/);
            if (trailingNewlines > 0)
            {
                WriteLineRepeat(trailingNewlines);
            }
        }
    }

    private int GetLinesBetweenNodes(Node parent, Node? node1, Node? node2)
    {
        if (ShouldElideIndentation(parent))
        {
            return 0;
        }

        parent = PrinterUtilities.SkipSynthesizedParentheses(parent);
        node1 = node1 is null ? null : PrinterUtilities.SkipSynthesizedParentheses(node1);
        node2 = node2 is null ? null : PrinterUtilities.SkipSynthesizedParentheses(node2);

        // Always use a newline for synthesized code if the synthesizer desires it.
        if (node2 is not null && ShouldEmitOnNewLine(node2, ListFormat.None))
        {
            return 1;
        }

        if (currentSourceFile is not null && parent is not null && node1 is not null && node2 is not null &&
            !AstUtil.NodeIsSynthesized(parent) && !AstUtil.NodeIsSynthesized(node1) && !AstUtil.NodeIsSynthesized(node2))
        {
            if (Options.PreserveSourceNewlines)
            {
                return GetEffectiveLines(
                    includeComments => PrinterUtilities.GetLinesBetweenRangeEndAndRangeStart(
                        node1.Loc,
                        node2.Loc,
                        currentSourceFile,
                        includeComments));
            }
            return PrinterUtilities.RangeEndIsOnSameLineAsRangeStart(node1.Loc, node2.Loc, currentSourceFile) ? 0 : 1;
        }

        return 0;
    }

    private int GetEffectiveLines(Func<bool, int> getLineDifference)
    {
        // If 'preserveSourceNewlines' is disabled, we should never call this function
        // because it could be more expensive than alternative approximations.
        if (!Options.PreserveSourceNewlines)
        {
            throw new InvalidOperationException("Should not be called when preserveSourceNewlines is false");
        }
        var lines = getLineDifference(true /*includeComments*/);
        if (lines == 0)
        {
            return getLineDifference(false /*includeComments*/);
        }
        return lines;
    }

    private int GetLeadingLineTerminatorCount(Node? parentNode, Node? firstChild, ListFormat format)
    {
        if ((format & ListFormat.PreserveLines) != 0 || Options.PreserveSourceNewlines)
        {
            if ((format & ListFormat.PreferNewLine) != 0)
            {
                return 1;
            }

            if (firstChild is null)
            {
                return (parentNode is null || currentSourceFile is not null && PrinterUtilities.RangeIsOnSingleLine(parentNode.Loc, currentSourceFile)) ? 0 : 1;
            }
            if (nextListElementPos > 0 && firstChild.Pos == nextListElementPos)
            {
                return 0;
            }
            if (firstChild.Kind == SyntaxKind.JsxText)
            {
                // JsxText will be written with its leading whitespace, so don't add more manually.
                return 0;
            }
            if (currentSourceFile is not null && parentNode is not null &&
                !AstUtil.PositionIsSynthesized(parentNode.Pos) &&
                !AstUtil.NodeIsSynthesized(firstChild) &&
                (firstChild.Parent is null))
            {
                if (Options.PreserveSourceNewlines)
                {
                    return GetEffectiveLines(
                        includeComments => PrinterUtilities.GetLinesBetweenPositionAndPrecedingNonWhitespaceCharacter(
                            firstChild.Pos,
                            parentNode.Pos,
                            currentSourceFile,
                            includeComments));
                }
                return PrinterUtilities.RangeStartPositionsAreOnSameLine(parentNode.Loc, firstChild.Loc, currentSourceFile) ? 0 : 1;
            }
            if (ShouldEmitOnNewLine(firstChild, format))
            {
                return 1;
            }
        }
        return (format & ListFormat.MultiLine) != 0 ? 1 : 0;
    }

    private int GetSeparatingLineTerminatorCount(Node? previousNode, Node? nextNode, ListFormat format)
    {
        if ((format & ListFormat.PreserveLines) != 0 || Options.PreserveSourceNewlines)
        {
            if (previousNode is null || nextNode is null)
            {
                return 0;
            }
            if (nextNode.Kind == SyntaxKind.JsxText)
            {
                // JsxText will be written with its leading whitespace, so don't add more manually.
                return 0;
            }
            else if (currentSourceFile is not null && !AstUtil.NodeIsSynthesized(previousNode) && !AstUtil.NodeIsSynthesized(nextNode))
            {
                if (Options.PreserveSourceNewlines && PrinterUtilities.SiblingNodePositionsAreComparable(emitContext, previousNode, nextNode))
                {
                    return GetEffectiveLines(
                        includeComments => PrinterUtilities.GetLinesBetweenRangeEndAndRangeStart(
                            previousNode.Loc,
                            nextNode.Loc,
                            currentSourceFile,
                            includeComments));
                }
                else if (!Options.PreserveSourceNewlines && PrinterUtilities.OriginalNodesHaveSameParent(emitContext, previousNode, nextNode))
                {
                    return PrinterUtilities.RangeEndIsOnSameLineAsRangeStart(previousNode.Loc, nextNode.Loc, currentSourceFile) ? 0 : 1;
                }
                return (format & ListFormat.PreferNewLine) != 0 ? 1 : 0;
            }
            else if (ShouldEmitOnNewLine(previousNode, format) || ShouldEmitOnNewLine(nextNode, format))
            {
                return 1;
            }
        }
        else if (ShouldEmitOnNewLine(nextNode, ListFormat.None))
        {
            return 1;
        }
        return (format & ListFormat.MultiLine) != 0 ? 1 : 0;
    }

    private int GetClosingLineTerminatorCount(Node? parentNode, Node? lastChild, ListFormat format, TextRange childrenTextRange)
    {
        if ((format & ListFormat.PreserveLines) != 0 || Options.PreserveSourceNewlines)
        {
            if ((format & ListFormat.PreferNewLine) != 0)
            {
                return 1;
            }
            if (lastChild is null)
            {
                return (parentNode is null || currentSourceFile is not null && PrinterUtilities.RangeIsOnSingleLine(parentNode.Loc, currentSourceFile)) ? 0 : 1;
            }
            if (currentSourceFile is not null && parentNode is not null && !AstUtil.PositionIsSynthesized(parentNode.Pos) && !AstUtil.NodeIsSynthesized(lastChild) && (lastChild.Parent is null || lastChild.Parent == parentNode))
            {
                if (Options.PreserveSourceNewlines)
                {
                    int end = PrinterUtilities.GreatestEnd(lastChild.End, childrenTextRange);
                    return GetEffectiveLines(
                        includeComments => PrinterUtilities.GetLinesBetweenPositionAndNextNonWhitespaceCharacter(
                            end,
                            parentNode.End,
                            currentSourceFile,
                            includeComments));
                }
                return PrinterUtilities.RangeEndPositionsAreOnSameLine(parentNode.Loc, lastChild.Loc, currentSourceFile) ? 0 : 1;
            }
            if (ShouldEmitOnNewLine(lastChild, format))
            {
                return 1;
            }
        }
        if ((format & ListFormat.MultiLine) != 0 && (format & ListFormat.NoTrailingNewLine) == 0)
        {
            return 1;
        }
        return 0;
    }

    private void WriteCommentRange(CommentRange comment)
    {
        if (currentSourceFile is null)
        {
            return;
        }

        var text = currentSourceFile.Text();
        var lineMap = currentSourceFile.ECMALineMap();
        WriteCommentRangeWorker(text, lineMap, comment.Kind, comment.TextRange);
    }

    private void WriteCommentRangeWorker(string text, int[] lineMap, SyntaxKind kind, TextRange loc)
    {
        if (kind == SyntaxKind.MultiLineCommentTrivia)
        {
            int indentSize = TextWriter.GetDefaultIndentSize();
            int firstLine = Scanner.ComputeLineOfPosition(lineMap, loc.Pos);
            int lineCount = lineMap.Length;
            int firstCommentLineIndent = -1;
            int pos = loc.Pos;
            int currentLine = firstLine;
            for (; pos < loc.End; currentLine++)
            {
                int nextLineStart;
                if (currentLine + 1 == lineCount)
                {
                    nextLineStart = text.Length + 1;
                }
                else
                {
                    nextLineStart = lineMap[currentLine + 1];
                }

                if (pos != loc.Pos)
                {
                    // If we are not emitting first line, we need to write the spaces to adjust the alignment
                    if (firstCommentLineIndent == -1)
                    {
                        firstCommentLineIndent = PrinterUtilities.CalculateIndent(text, lineMap[firstLine], loc.Pos);
                    }

                    int currentWriterIndentSpacing = writer.GetIndent() * indentSize;
                    int spacesToEmit = currentWriterIndentSpacing - firstCommentLineIndent + PrinterUtilities.CalculateIndent(text, pos, nextLineStart);
                    if (spacesToEmit > 0)
                    {
                        int numberOfSingleSpacesToEmit = spacesToEmit % indentSize;
                        string indentSizeSpaceString = GetIndentString((spacesToEmit - numberOfSingleSpacesToEmit) / indentSize, indentSize);

                        writer.RawWrite(indentSizeSpaceString);

                        while (numberOfSingleSpacesToEmit > 0)
                        {
                            writer.RawWrite(" ");
                            numberOfSingleSpacesToEmit--;
                        }
                    }
                    else
                    {
                        writer.RawWrite("");
                    }
                }

                // Write the comment line text
                int end = Math.Min(loc.End, nextLineStart);
                for (int scan = pos; scan < end;)
                {
                    var (ch, size) = StringUtil.DecodeJSStringRune(text, scan);
                    if (size == 0)
                    {
                        break;
                    }
                    if (StringUtil.IsLineBreak(ch))
                    {
                        end = scan;
                        break;
                    }
                    scan += size;
                }
                string currentLineText = text[pos..end].Trim();
                if (currentLineText.Length > 0)
                {
                    WriteComment(currentLineText);
                    if (end != loc.End)
                    {
                        WriteLine();
                    }
                }
                else
                {
                    // Empty string - make sure we write empty line
                    writer.WriteLineForce(true);
                }

                pos = nextLineStart;
            }
        }
        else
        {
            // Single line comment of style //....
            WriteComment(text[loc.Pos..loc.End]);
        }
    }

    //
    // Custom emit behavior stubs (i.e., from `EmitNode`, `EmitFlags`, etc.)
    //

    private bool ShouldEmitComments(Node node)
    {
        return !commentsDisabled &&
            currentSourceFile is not null &&
            !NodeTests.IsSourceFile(node);
    }

    private bool ShouldWriteComment(CommentRange comment)
    {
        return !Options.OnlyPrintJSDocStyle ||
            currentSourceFile is not null && PrinterUtilities.IsJSDocLikeText(currentSourceFile.Text(), comment) ||
            currentSourceFile is not null && PrinterUtilities.IsPinnedComment(currentSourceFile.Text(), comment);
    }

    private bool ShouldEmitIndented(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.Indented) != 0;
    }

    private bool ShouldElideIndentation(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.NoIndentation) != 0;
    }

    private bool ShouldEmitOnSingleLine(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.SingleLine) != 0;
    }

    private bool ShouldEmitOnMultipleLines(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.MultiLine) != 0;
    }

    private bool ShouldEmitBlockFunctionBodyOnSingleLine(Block body)
    {
        if (ShouldEmitOnSingleLine(body.AsNode()))
        {
            return true;
        }

        if (body.MultiLine)
        {
            return false;
        }

        if (!AstUtil.NodeIsSynthesized(body.AsNode()) && currentSourceFile is not null && !PrinterUtilities.RangeIsOnSingleLine(body.Loc, currentSourceFile))
        {
            return false;
        }

        if (GetLeadingLineTerminatorCount(body.AsNode(), global::Ts.Core.CoreUtil.FirstOrNil(body.Statements!.Nodes), ListFormat.PreserveLines) > 0 ||
            GetClosingLineTerminatorCount(body.AsNode(), global::Ts.Core.CoreUtil.LastOrNil(body.Statements!.Nodes), ListFormat.PreserveLines, body.Statements.Loc) > 0)
        {
            return false;
        }

        Node? previousStatement = null;
        foreach (var statement in body.Statements.Nodes)
        {
            if (GetSeparatingLineTerminatorCount(previousStatement, statement, ListFormat.PreserveLines) > 0)
            {
                return false;
            }

            previousStatement = statement;
        }

        return true;
    }

    private bool ShouldEmitOnNewLine(Node? node, ListFormat format)
    {
        // Go passes a possibly-nil *ast.Node here; EmitFlags on a nil node is a zero-value map
        // lookup returning EFNone, so the StartOnNewLine test is simply false. Mirror that faithfully.
        if (node is not null && (emitContext.EmitFlags(node) & EF.StartOnNewLine) != 0)
        {
            return true;
        }
        return (format & ListFormat.PreferNewLine) != 0;
    }

    private bool ShouldEmitSourceMaps(Node node)
    {
        return !sourceMapsDisabled &&
            sourceMapSource is not null &&
            !NodeTests.IsSourceFile(node) &&
            !PrinterUtilities.IsInJsonFile(node);
    }

    private bool ShouldEmitTokenSourceMaps(SyntaxKind token, int pos, Node contextNode, TokenEmitFlags flags)
    {
        return (flags & TokenEmitFlags.NoSourceMaps) == 0 &&
            ShouldEmitSourceMaps(contextNode) &&
            !Options.OmitBraceSourceMapPositions && (token == SyntaxKind.OpenBraceToken || token == SyntaxKind.CloseBraceToken);
    }

    private bool ShouldEmitLeadingComments(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.NoLeadingComments) == 0;
    }

    private bool ShouldEmitTrailingComments(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.NoTrailingComments) == 0;
    }

    private bool ShouldEmitNestedComments(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.NoNestedComments) == 0;
    }

    private bool ShouldEmitDetachedComments(Node node)
    {
        if (!NodeTests.IsSourceFile(node))
        {
            return true;
        }

        var file = node.AsSourceFile();

        return file.Statements!.Nodes.Length == 0 ||
            !AstUtil.IsPrologueDirective(file.Statements.Nodes[0]) ||
            AstUtil.NodeIsSynthesized(file.Statements.Nodes[0]);
    }

    private bool HasCommentsAtPosition(int pos)
    {
        if (currentSourceFile is null)
        {
            return false;
        }

        foreach (var _ in global::Ts.Parser.CommentScanner.GetTrailingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile.Text(), pos + 1))
        {
            return true;
        }
        foreach (var _ in global::Ts.Parser.CommentScanner.GetLeadingCommentRanges(emitContext.Factory.AsNodeFactory(), currentSourceFile.Text(), pos + 1))
        {
            return true;
        }
        return false;
    }

    private bool ShouldEmitIndirectCall(Node node)
    {
        return (emitContext.EmitFlags(node) & EF.IndirectCall) != 0;
    }

    private bool ShouldAllowTrailingComma(Node node, NodeList list)
    {
        if (currentSourceFile is null || currentSourceFile.ScriptKind == ScriptKind.Json)
        {
            return false;
        }

        switch (node.Kind)
        {
            case SyntaxKind.ObjectLiteralExpression:
                return true;
            case SyntaxKind.ArrayLiteralExpression:
            case SyntaxKind.ArrowFunction:
            case SyntaxKind.Constructor:
            case SyntaxKind.GetAccessor:
            case SyntaxKind.SetAccessor:
            case SyntaxKind.TypeAliasDeclaration:
            case SyntaxKind.JSTypeAliasDeclaration:
            case SyntaxKind.FunctionType:
            case SyntaxKind.ConstructorType:
            case SyntaxKind.CallSignature:
            case SyntaxKind.ConstructSignature:
            case SyntaxKind.TaggedTemplateExpression:
            case SyntaxKind.ObjectBindingPattern:
            case SyntaxKind.ArrayBindingPattern:
            case SyntaxKind.NamedImports:
            case SyntaxKind.NamedExports:
            case SyntaxKind.ImportAttributes:
                return true;
            case SyntaxKind.ClassExpression:
            case SyntaxKind.ClassDeclaration:
            case SyntaxKind.InterfaceDeclaration:
                return list == node.TypeParameterList();
            case SyntaxKind.FunctionDeclaration:
            case SyntaxKind.FunctionExpression:
            case SyntaxKind.MethodDeclaration:
                return true;
            case SyntaxKind.CallExpression:
                return true;
            case SyntaxKind.NewExpression:
                return true;
        }

        return false;
    }

    //
    // Tokens/Keywords
    //

    private int WriteTokenText(SyntaxKind token, WriteKind writeKind, int pos)
    {
        // !!! emit leading and trailing comments
        // !!! emit leading and trailing source maps
        var tokenString = Scanner.TokenToString(token);
        WriteAs(tokenString, writeKind);
        if (AstUtil.PositionIsSynthesized(pos))
        {
            return pos;
        }
        else
        {
            return pos + tokenString.Length;
        }
    }

    private int EmitToken(SyntaxKind token, int pos, WriteKind writeKind, Node contextNode)
    {
        return EmitTokenEx(token, pos, writeKind, contextNode, TokenEmitFlags.None);
    }

    private int EmitTokenEx(SyntaxKind token, int pos, WriteKind writeKind, Node contextNode, TokenEmitFlags flags)
    {
        var (state, newPos) = EnterToken(token, pos, contextNode, flags);
        pos = newPos;
        pos = WriteTokenText(token, writeKind, pos);
        ExitToken(token, pos, contextNode, state);
        return pos;
    }

    private void EmitKeywordNode(Node? node)
    {
        EmitKeywordNodeEx(node, TokenEmitFlags.None);
    }

    private void EmitKeywordNodeEx(Node? node, TokenEmitFlags flags)
    {
        if (node is null)
        {
            return;
        }

        var state = EnterTokenNode(node, flags);
        WriteTokenText(node.Kind, WriteKind.Keyword, node.Pos);
        ExitTokenNode(node, state);
    }

    private void EmitPunctuationNode(Node? node)
    {
        EmitPunctuationNodeEx(node, TokenEmitFlags.None);
    }

    private void EmitPunctuationNodeEx(Node? node, TokenEmitFlags flags)
    {
        if (node is null)
        {
            return;
        }

        var state = EnterTokenNode(node, flags);
        WriteTokenText(node.Kind, WriteKind.Punctuation, node.Pos);
        ExitTokenNode(node, state);
    }

    private void EmitTokenNode(Node? node)
    {
        EmitTokenNodeEx(node, TokenEmitFlags.None);
    }

    private void EmitTokenNodeEx(Node? node, TokenEmitFlags flags)
    {
        if (node is null)
        {
            return;
        }

        if (NodeTests.IsKeywordKind(node.Kind))
        {
            EmitKeywordNodeEx(node, flags);
        }
        else if (NodeTests.IsPunctuationKind(node.Kind))
        {
            EmitPunctuationNodeEx(node, flags);
        }
        else
        {
            throw new InvalidOperationException($"unexpected TokenNode: {node.Kind}");
        }
    }

    //
    // Literals
    //

    // Emits literals of the following kinds
    private void EmitLiteral(Node node, GetLiteralTextFlags flags)
    {
        // Add NeverAsciiEscape flag if the printer option is set
        if (Options.NeverAsciiEscape)
        {
            flags |= GetLiteralTextFlags.NeverAsciiEscape;
        }
        if (Options.TerminateUnterminatedLiterals)
        {
            flags |= GetLiteralTextFlags.TerminateUnterminatedLiterals;
        }

        var text = GetLiteralTextOfNode(node, null /*sourceFile*/, flags);

        // Quick info expects all literals to be called with writeStringLiteral, as there's no specific type for
        // numberLiterals
        writer.WriteStringLiteral(text);
    }

    private void EmitNumericLiteral(NumericLiteral node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitBigIntLiteral(BigIntLiteral node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None); // TODO: Preserve numeric literal separators after Strada migration
        ExitNode(node.AsNode(), state);
    }

    private void EmitStringLiteral(StringLiteral node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitNoSubstitutionTemplateLiteral(NoSubstitutionTemplateLiteral node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitRegularExpressionLiteral(RegularExpressionLiteral node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    //
    // Pseudo-literals
    //

    private void EmitTemplateHead(TemplateHead node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateMiddle(TemplateMiddle node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateTail(TemplateTail node)
    {
        var state = EnterNode(node.AsNode());
        EmitLiteral(node.AsNode(), GetLiteralTextFlags.None);
        ExitNode(node.AsNode(), state);
    }

    private void EmitTemplateMiddleTail(Node node)
    {
        switch (node.Kind)
        {
            case SyntaxKind.TemplateMiddle:
                EmitTemplateMiddle(node.AsTemplateMiddle());
                break;
            case SyntaxKind.TemplateTail:
                EmitTemplateTail(node.AsTemplateTail());
                break;
        }
    }
}
