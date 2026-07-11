// Ported from tsgo internal/printer/singlelinestringwriter.go

using System;
using System.Buffers;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using StringUtil = global::Ts.StringUtil.StringUtil;

internal sealed class SingleLineStringWriter : EmitTextWriter
{
    // Go: var singleLineStringWriterPool sync.Pool ... → Ts.Core.ObjectPool (05-conventions.md §5 / AGENTS §4.5).
    private static readonly ObjectPool<SingleLineStringWriter> Pool = new(() => new SingleLineStringWriter());

    // Go: GetSingleLineStringWriter() (EmitTextWriter, func()) — the second return is the "put back" closure.
    public static (EmitTextWriter Writer, Action Release) GetSingleLineStringWriter()
    {
        var w = Pool.Get();
        w.Clear();
        return (w, () => Pool.Return(w));
    }

    private readonly StringBuilder builder = new();
    private string lastWritten = "";

    public void Clear()
    {
        lastWritten = "";
        builder.Clear();
    }

    public void DecreaseIndent()
    {
        // Do Nothing
    }

    public int GetColumn()
    {
        return 0;
    }

    public int GetIndent()
    {
        return 0;
    }

    public int GetLine()
    {
        return 0;
    }

    public string String()
    {
        return builder.ToString();
    }

    public int GetTextPos()
    {
        return builder.Length;
    }

    public bool HasTrailingComment()
    {
        return false;
    }

    public bool HasTrailingWhitespace()
    {
        if (builder.Length == 0)
        {
            return false;
        }
        if (Rune.DecodeLastFromUtf16(lastWritten, out Rune ch, out _) != OperationStatus.Done)
        {
            return false;
        }
        return StringUtil.IsWhiteSpaceLike(ch.Value);
    }

    public void IncreaseIndent()
    {
        // Do Nothing
    }

    public bool IsAtStartOfLine()
    {
        return false;
    }

    public void RawWrite(string s)
    {
        lastWritten = s;
        builder.Append(s);
    }

    public void Write(string s)
    {
        lastWritten = s;
        builder.Append(s);
    }

    public void WriteComment(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteKeyword(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteLine()
    {
        lastWritten = " ";
        builder.Append(' ');
    }

    public void WriteLineForce(bool force)
    {
        lastWritten = " ";
        builder.Append(' ');
    }

    public void WriteLiteral(string s)
    {
        lastWritten = s;
        builder.Append(s);
    }

    public void WriteOperator(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteParameter(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteProperty(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WritePunctuation(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteSpace(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteStringLiteral(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteSymbol(string text, Symbol symbol)
    {
        lastWritten = text;
        builder.Append(text);
    }

    public void WriteTrailingSemicolon(string text)
    {
        lastWritten = text;
        builder.Append(text);
    }
}
