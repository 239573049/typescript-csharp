// Ported from tsgo internal/ls/completions.go:6084-6296 — typeNodeToExpression /
// entityNameToExpression and the snippet printer (snippet-escaping emit writer over
// printer.ChangeTrackerWriter, with post-format escape reapplication).

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;

namespace Ts.Ls;

public sealed partial class LanguageService
{
    // completions.go:6084-6144 — typeNodeToExpression.
    internal static Node? TypeNodeToExpression(
        Node typeNode,
        ScriptTarget target,
        QuotePreference quotePreference,
        global::Ts.Ast.NodeFactory factory)
    {
        switch (typeNode.Kind)
        {
            case SyntaxKind.TypeReference:
            {
                var typeName = typeNode.AsTypeReferenceNode().TypeName;
                return EntityNameToExpression(typeName!, target, quotePreference, factory);
            }
            case SyntaxKind.IndexedAccessType:
            {
                var objectExpression = TypeNodeToExpression(
                    typeNode.AsIndexedAccessTypeNode().ObjectType!,
                    target,
                    quotePreference,
                    factory);
                var indexExpression = TypeNodeToExpression(
                    typeNode.AsIndexedAccessTypeNode().IndexType!,
                    target,
                    quotePreference,
                    factory);
                if (objectExpression != null && indexExpression != null)
                {
                    return factory.NewElementAccessExpression(objectExpression, null /*questionDotToken*/, indexExpression, NodeFlags.None);
                }
                return null;
            }
            case SyntaxKind.LiteralType:
            {
                var literal = typeNode.AsLiteralTypeNode().Literal;
                switch (literal!.Kind)
                {
                    case SyntaxKind.StringLiteral:
                        return factory.NewStringLiteral(literal.Text(), quotePreference == QuotePreference.Single ? TokenFlags.SingleQuote : TokenFlags.None);
                    case SyntaxKind.NumericLiteral:
                        return factory.NewNumericLiteral(literal.Text(), literal.AsNumericLiteral().TokenFlags);
                    default:
                        return null;
                }
            }
            case SyntaxKind.ParenthesizedType:
            {
                var expr = TypeNodeToExpression(
                    typeNode.AsParenthesizedTypeNode().Type!,
                    target,
                    quotePreference,
                    factory);
                if (expr == null)
                {
                    return null;
                }
                if (NodeTests.IsIdentifier(expr))
                {
                    return expr;
                }
                return factory.NewParenthesizedExpression(expr);
            }
            case SyntaxKind.TypeQuery:
                return EntityNameToExpression(typeNode.AsTypeQueryNode().ExprName!, target, quotePreference, factory);
            case SyntaxKind.ImportType:
                Debug.Fail("We should not get an import type after calling 'typeToAutoImportableTypeNode'.");
                return null;
        }
        return null;
    }

    // completions.go:6146-6161 — entityNameToExpression.
    internal static Node EntityNameToExpression(
        Node entityName,
        ScriptTarget target,
        QuotePreference quotePreference,
        global::Ts.Ast.NodeFactory factory)
    {
        if (NodeTests.IsIdentifier(entityName))
        {
            return entityName;
        }
        return factory.NewPropertyAccessExpression(
            EntityNameToExpression(entityName.AsQualifiedName().Left!, target, quotePreference, factory),
            null, /*questionDotToken*/
            entityName.AsQualifiedName().Right!,
            NodeFlags.None);
    }
}

// completions.go:6163-6168 — snippetPrinter.
internal sealed class SnippetPrinter
{
    internal global::Ts.Printer.ChangeTrackerWriter baseWriter = null!;
    internal global::Ts.Printer.Printer printer = null!;
    internal SnippetEmitTextWriter writer = null!;
    internal global::Ts.Ast.NodeFactory factory = null!;

    // completions.go:6170-6177 — printNode: snippet-escaping version of `printer.printNode`.
    internal string PrintNode(Node node)
    {
        var unescaped = PrintUnescapedNode(node);
        if (writer.escapes.Count > 0)
        {
            return global::Ts.Core.Core.ApplyBulkEdits(unescaped, writer.escapes.ToArray());
        }
        return unescaped;
    }

    // completions.go:6179-6184 — printUnescapedNode.
    internal string PrintUnescapedNode(Node node)
    {
        writer.escapes = new List<TextChange>();
        writer.Clear();
        printer.Write(node, null /*sourceFile*/, writer, null /*sourceMapGenerator*/);
        return writer.String();
    }

    // completions.go:6186-6206 — printAndFormatNode.
    internal string PrintAndFormatNode(global::Ts.Format.FormatContext formatCtx, Node node, SourceFile sourceFile)
    {
        var text = PrintUnescapedNode(node);
        var nodeWithPos = baseWriter.AssignPositionsToNode(node, factory);
        var syntheticFile = CreateSyntheticFile(nodeWithPos, text, sourceFile);
        var changes = global::Ts.Format.Api.FormatNodeGivenIndentation(
            formatCtx,
            nodeWithPos,
            syntheticFile,
            sourceFile.LanguageVariant,
            0, /*initialIndentation*/
            0 /*delta*/);

        var allChanges = changes;
        if (writer.escapes.Count > 0)
        {
            var merged = new List<TextChange>(changes);
            merged.AddRange(writer.escapes);
            merged.Sort((a, b) => TextRange.CompareTextRanges(a.TextRange, b.TextRange));
            allChanges = merged.ToArray();
        }

        return global::Ts.Core.Core.ApplyBulkEdits(syntheticFile.Text(), allChanges);
    }

    // completions.go:6208-6225 — createSyntheticFile: creates a source file containing `node`
    // for formatting purposes. `node` and descendants need to be synthetic nodes with positions
    // assigned. This function also assigns parent pointers.
    internal SourceFile CreateSyntheticFile(Node node, string text, SourceFile targetFile)
    {
        var eof = factory.NewToken(SyntaxKind.EndOfFile);
        eof.Loc = new TextRange(text.Length, text.Length);
        var statements = factory.NewNodeList([node]);
        statements.Loc = new TextRange(node.Pos, node.End);
        var syntheticFile = factory.NewSourceFile(
            targetFile.ParseOptions(),
            text,
            statements,
            eof);
        syntheticFile.Loc = new TextRange(0, text.Length);
        AstUtilities.SetParentInChildren(syntheticFile);
        return syntheticFile.AsSourceFile();
    }

    // completions.go:6227-6239 — createSnippetPrinter.
    internal static SnippetPrinter CreateSnippetPrinter(global::Ts.Printer.PrinterOptions options)
    {
        var baseWriter = new global::Ts.Printer.ChangeTrackerWriter(options.NewLine.GetNewLineCharacter(), -1);
        var printer = global::Ts.Printer.Printer.NewPrinter(options, baseWriter.GetPrintHandlers(), null /*emitContext*/);
        var writer = new SnippetEmitTextWriter
        {
            inner = baseWriter,
        };
        return new SnippetPrinter
        {
            baseWriter = baseWriter,
            printer = printer,
            writer = writer,
            factory = new global::Ts.Ast.NodeFactory(new NodeFactoryHooks()),
        };
    }
}

// completions.go:6241-6295 — snippetEmitTextWriter: overrides base writer methods to perform
// snippet escaping. (Go embeds *ChangeTrackerWriter; C# delegates through `inner` since the
// base writer is sealed.)
internal sealed class SnippetEmitTextWriter : global::Ts.Printer.EmitTextWriter
{
    internal global::Ts.Printer.ChangeTrackerWriter inner = null!;
    internal List<TextChange> escapes = new();

    // completions.go:6247-6249 — nonEscapingWrite.
    internal void NonEscapingWrite(string s)
    {
        inner.Write(s);
    }

    public void Write(string s) => EscapingWrite(s, () => inner.Write(s));

    public void WriteComment(string text) => EscapingWrite(text, () => inner.WriteComment(text));

    public void WriteStringLiteral(string text) => EscapingWrite(text, () => inner.WriteStringLiteral(text));

    public void WriteParameter(string text) => EscapingWrite(text, () => inner.WriteParameter(text));

    public void WriteProperty(string text) => EscapingWrite(text, () => inner.WriteProperty(text));

    public void WriteSymbol(string text, Symbol symbol) => EscapingWrite(text, () => inner.WriteSymbol(text, symbol));

    // completions.go:6275-6295 — escapingWrite: the formatter/scanner will have issues with
    // snippet-escaped text, so instead of writing the escaped text directly to the writer,
    // generate a set of changes that can be applied to the unescaped text to escape it
    // post-formatting.
    private void EscapingWrite(string s, Action write)
    {
        var escaped = LanguageService.EscapeSnippetText(s);
        if (escaped != s)
        {
            var start = inner.GetTextPos();
            write();
            var end = inner.GetTextPos();
            escapes.Add(new TextChange
            {
                NewText = escaped,
                TextRange = new TextRange(start, end),
            });
        }
        else
        {
            write();
        }
    }

    // Delegated members (Go embedding promotion).
    public void WriteTrailingSemicolon(string text) => inner.WriteTrailingSemicolon(text);

    public void WriteKeyword(string text) => inner.WriteKeyword(text);

    public void WriteOperator(string text) => inner.WriteOperator(text);

    public void WritePunctuation(string text) => inner.WritePunctuation(text);

    public void WriteSpace(string text) => inner.WriteSpace(text);

    public void WriteLine() => inner.WriteLine();

    public void WriteLineForce(bool force) => inner.WriteLineForce(force);

    public void IncreaseIndent() => inner.IncreaseIndent();

    public void DecreaseIndent() => inner.DecreaseIndent();

    public void Clear() => inner.Clear();

    public string String() => inner.String();

    public void RawWrite(string s) => inner.RawWrite(s);

    public void WriteLiteral(string s) => inner.WriteLiteral(s);

    public int GetTextPos() => inner.GetTextPos();

    public int GetLine() => inner.GetLine();

    public int GetColumn() => inner.GetColumn();

    public int GetIndent() => inner.GetIndent();

    public bool IsAtStartOfLine() => inner.IsAtStartOfLine();

    public bool HasTrailingComment() => inner.HasTrailingComment();

    public bool HasTrailingWhitespace() => inner.HasTrailingWhitespace();
}
