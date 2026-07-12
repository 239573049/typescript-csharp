// Ported from tsgo internal/checker/printer.go (symbolToString / symbolToStringEx family +
// getTrailingSemicolonDeferringWriter/semicolonRemoverWriter). This is the checker's Symbol ->
// error-message-string serializer:
//   symbolToStringEx  =  nodeBuilder.SymbolToNode / SymbolToEntityName (build entity/expression node)
//                        ->  emit Printer prints the node through a semicolon-deferring writer.
//
// SCOPE (breadth-first, per phase-6 symbol-display wave): the entity/expression synthesis (SymbolToNode /
// SymbolToEntityName / SymbolToExpression + getNameOfSymbolAsWritten) is landed in NodeBuilder/NodeBuilderImpl.cs
// for the single-symbol chain that the common assignability / "cannot assign to X" error messages need.
// The full qualified-name chain (getSymbolChain -> getAccessibleSymbolChain) is only reached when an
// enclosingDeclaration is supplied or FlagsUseFullyQualifiedType is set; symbolToString(symbol) supplies
// neither, so the chain reduces to [symbol] exactly as the Go lookupSymbolChainWorker else branch does.
//
// PLACEMENT (§1.5): symbolToString/symbolToStringEx and semicolonRemoverWriter live in `package checker`
// in tsgo, so their faithful C# home is namespace Ts.Checker.

using System;
using Ts.Ast;

using PrinterNs = global::Ts.Printer;
using NodeBuilderNs = global::Ts.NodeBuilder;

namespace Ts.Checker;

public sealed partial class Checker
{
    // printer.go:254 — symbolToString. (The exported printer.go:250 SymbolToString is the same one-arg
    // entry; C# needs no separate public wrapper — callers use SymbolToString directly.)
    internal string SymbolToString(Symbol? symbol) =>
        SymbolToStringEx(symbol!, null, SymbolFlags.All, SymbolFormatFlags.AllowAnyNodeKind);

    // printer.go:262 — symbolToStringEx.
    internal string SymbolToStringEx(Symbol symbol, Node? enclosingDeclaration, SymbolFlags meaning, SymbolFormatFlags flags)
    {
        var (writer, putWriter) = PrinterNs.SingleLineStringWriter.GetSingleLineStringWriter();
        try
        {
            var nodeFlags = NodeBuilderNs.Flags.IgnoreErrors;
            var internalNodeFlags = NodeBuilderNs.InternalFlags.None;
            if ((flags & SymbolFormatFlags.UseOnlyExternalAliasing) != 0)
            {
                nodeFlags |= NodeBuilderNs.Flags.UseOnlyExternalAliasing;
            }
            if ((flags & SymbolFormatFlags.WriteTypeParametersOrArguments) != 0)
            {
                nodeFlags |= NodeBuilderNs.Flags.WriteTypeParametersInQualifiedName;
            }
            if ((flags & SymbolFormatFlags.UseAliasDefinedOutsideCurrentScope) != 0)
            {
                nodeFlags |= NodeBuilderNs.Flags.UseAliasDefinedOutsideCurrentScope;
            }
            if ((flags & SymbolFormatFlags.DoNotIncludeSymbolChain) != 0)
            {
                internalNodeFlags |= NodeBuilderNs.InternalFlags.DoNotIncludeSymbolChain;
            }
            if ((flags & SymbolFormatFlags.WriteComputedProps) != 0)
            {
                internalNodeFlags |= NodeBuilderNs.InternalFlags.WriteComputedProps;
            }

            var emitContext = PrinterNs.EmitContext.NewEmitContext();
            var nodeBuilder = NewNodeBuilder(emitContext);
            SourceFile? sourceFile = null;
            if (enclosingDeclaration != null)
            {
                sourceFile = AstUtilities.GetSourceFileOfNode(enclosingDeclaration);
            }

            // add neverAsciiEscape for GH#39027
            var printerOptions = enclosingDeclaration != null && enclosingDeclaration.Kind == SyntaxKind.SourceFile
                ? new PrinterNs.PrinterOptions { RemoveComments = true, NeverAsciiEscape = true }
                : new PrinterNs.PrinterOptions { RemoveComments = true };
            var printer = PrinterNs.Printer.NewPrinter(printerOptions, new PrinterNs.PrintHandlers(), emitContext);

            Node? entity = (flags & SymbolFormatFlags.AllowAnyNodeKind) != 0
                ? nodeBuilder.SymbolToNode(symbol, meaning, enclosingDeclaration, nodeFlags, internalNodeFlags, null)
                : nodeBuilder.SymbolToEntityName(symbol, meaning, enclosingDeclaration, nodeFlags, internalNodeFlags, null);
            printer.Write(entity!, sourceFile, GetTrailingSemicolonDeferringWriter(writer), null);
            return writer.String();
        }
        finally
        {
            putWriter();
        }
    }

    // printer.go:169 — getTrailingSemicolonDeferringWriter.
    private static PrinterNs.EmitTextWriter GetTrailingSemicolonDeferringWriter(PrinterNs.EmitTextWriter writer)
    {
        return new SemicolonRemoverWriter(writer);
    }
}

// printer.go:36 — semicolonRemoverWriter. Defers a trailing-semicolon write until the next real write,
// dropping it if the writer is finalized first (single-line symbol names never keep a trailing `;`).
internal sealed class SemicolonRemoverWriter : global::Ts.Printer.EmitTextWriter
{
    private bool hasPendingSemicolon;
    private readonly global::Ts.Printer.EmitTextWriter inner;

    public SemicolonRemoverWriter(global::Ts.Printer.EmitTextWriter inner)
    {
        this.hasPendingSemicolon = false;
        this.inner = inner;
    }

    private void CommitSemicolon()
    {
        if (hasPendingSemicolon)
        {
            inner.WriteTrailingSemicolon(";");
            hasPendingSemicolon = false;
        }
    }

    public void Clear() => inner.Clear();

    public void DecreaseIndent()
    {
        CommitSemicolon();
        inner.DecreaseIndent();
    }

    public int GetColumn() => inner.GetColumn();

    public int GetIndent() => inner.GetIndent();

    public int GetLine() => inner.GetLine();

    public int GetTextPos() => inner.GetTextPos();

    public bool HasTrailingComment() => inner.HasTrailingComment();

    public bool HasTrailingWhitespace() => inner.HasTrailingWhitespace();

    public void IncreaseIndent()
    {
        CommitSemicolon();
        inner.IncreaseIndent();
    }

    public bool IsAtStartOfLine() => inner.IsAtStartOfLine();

    public void RawWrite(string s)
    {
        CommitSemicolon();
        inner.RawWrite(s);
    }

    public string String()
    {
        CommitSemicolon();
        return inner.String();
    }

    public void Write(string s)
    {
        CommitSemicolon();
        inner.Write(s);
    }

    public void WriteComment(string text)
    {
        CommitSemicolon();
        inner.WriteComment(text);
    }

    public void WriteKeyword(string text)
    {
        CommitSemicolon();
        inner.WriteKeyword(text);
    }

    public void WriteLine()
    {
        CommitSemicolon();
        inner.WriteLine();
    }

    public void WriteLineForce(bool force)
    {
        CommitSemicolon();
        inner.WriteLineForce(force);
    }

    public void WriteLiteral(string s)
    {
        CommitSemicolon();
        inner.WriteLiteral(s);
    }

    public void WriteOperator(string text)
    {
        CommitSemicolon();
        inner.WriteOperator(text);
    }

    public void WriteParameter(string text)
    {
        CommitSemicolon();
        inner.WriteParameter(text);
    }

    public void WriteProperty(string text)
    {
        CommitSemicolon();
        inner.WriteProperty(text);
    }

    public void WritePunctuation(string text)
    {
        CommitSemicolon();
        inner.WritePunctuation(text);
    }

    public void WriteSpace(string text)
    {
        CommitSemicolon();
        inner.WriteSpace(text);
    }

    public void WriteStringLiteral(string text)
    {
        CommitSemicolon();
        inner.WriteStringLiteral(text);
    }

    public void WriteSymbol(string text, Symbol symbol)
    {
        CommitSemicolon();
        inner.WriteSymbol(text, symbol);
    }

    public void WriteTrailingSemicolon(string text)
    {
        hasPendingSemicolon = true;
    }
}
