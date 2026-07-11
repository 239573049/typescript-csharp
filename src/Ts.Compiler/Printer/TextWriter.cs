// Ported from tsgo internal/printer/textwriter.go

using System.Buffers;
using System.Text;
using Ts.Ast;
using Ts.Core;

namespace Ts.Printer;

using StringUtil = global::Ts.StringUtil.StringUtil;

internal sealed class TextWriter : EmitTextWriter
{
    private string newLine = "";
    private int indentSize;
    private readonly StringBuilder builder = new();
    private string lastWritten = "";
    private int indent;
    private bool lineStart;
    private int lineCount;
    private int linePos;
    private bool hasTrailingCommentState;

    public void Clear()
    {
        // Go: *w = textWriter{newLine: w.newLine, indentSize: w.indentSize, lineStart: true}
        builder.Clear();
        lastWritten = "";
        indent = 0;
        lineStart = true;
        lineCount = 0;
        linePos = 0;
        hasTrailingCommentState = false;
    }

    public void Grow(int n)
    {
        builder.EnsureCapacity(builder.Length + n);
    }

    public void DecreaseIndent()
    {
        indent--;
    }

    // GetColumn returns the column position measured in UTF-16 code units
    // for source map compatibility.
    public int GetColumn()
    {
        if (lineStart)
        {
            return indent * indentSize;
        }
        // Count UTF-16 code units from the last line start.
        // For ASCII-only output (the common case), this equals the byte count.
        return CoreUtil.UTF16Len(builder.ToString()[linePos..]);
    }

    public int GetIndent()
    {
        return indent;
    }

    public int GetLine()
    {
        return lineCount;
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
        return hasTrailingCommentState;
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
        indent++;
    }

    public bool IsAtStartOfLine()
    {
        return lineStart;
    }

    public void RawWrite(string s)
    {
        if (s != "")
        {
            builder.Append(s);
            lastWritten = s;
            hasTrailingCommentState = false;
        }
        UpdateLineCountAndPosFor(s);
    }

    private void UpdateLineCountAndPosFor(string s)
    {
        int count = 0;
        int lastLineStart = 0;

        foreach (int lineStart in CoreUtil.ComputeECMALineStartsSeq(s))
        {
            count++;
            lastLineStart = lineStart;
        }

        if (count > 1)
        {
            lineCount += count - 1;
            int curLen = builder.Length;
            linePos = curLen - s.Length + lastLineStart;
            this.lineStart = (linePos - curLen) == 0;
            return;
        }
        this.lineStart = false;
    }

    private const int DefaultIndentSize = 4;

    // GetDefaultIndentSize returns the default indent size (4 spaces) used when no specific indent size is configured.
    public static int GetDefaultIndentSize()
    {
        return DefaultIndentSize;
    }

    private static string GetIndentString(int indent, int indentSize)
    {
        if (indent == 0)
        {
            return "";
        }
        // TODO: This is cached in tsc - should it be cached here?
        return new string(' ', indent * indentSize);
    }

    private void WriteText(string s)
    {
        if (s != "")
        {
            if (lineStart)
            {
                builder.Append(GetIndentString(indent, indentSize));
                lineStart = false;
            }
            builder.Append(s);
            lastWritten = s;
            UpdateLineCountAndPosFor(s);
        }
    }

    public void Write(string s)
    {
        if (s != "")
        {
            hasTrailingCommentState = false;
        }
        WriteText(s);
    }

    public void WriteComment(string text)
    {
        if (text != "")
        {
            hasTrailingCommentState = true;
        }
        WriteText(text);
    }

    public void WriteKeyword(string text)
    {
        Write(text);
    }

    private void WriteLineRaw()
    {
        builder.Append(newLine);
        lastWritten = newLine;
        lineCount++;
        linePos = builder.Length;
        lineStart = true;
        hasTrailingCommentState = false;
    }

    public void WriteLine()
    {
        if (!lineStart)
        {
            WriteLineRaw();
        }
    }

    public void WriteLineForce(bool force)
    {
        if (!lineStart || force)
        {
            WriteLineRaw();
        }
    }

    public void WriteLiteral(string s)
    {
        Write(s);
    }

    public void WriteOperator(string text)
    {
        Write(text);
    }

    public void WriteParameter(string text)
    {
        Write(text);
    }

    public void WriteProperty(string text)
    {
        Write(text);
    }

    public void WritePunctuation(string text)
    {
        Write(text);
    }

    public void WriteSpace(string text)
    {
        Write(text);
    }

    public void WriteStringLiteral(string text)
    {
        Write(text);
    }

    public void WriteSymbol(string text, Symbol symbol)
    {
        Write(text);
    }

    public void WriteTrailingSemicolon(string text)
    {
        Write(text);
    }

    public static EmitTextWriter NewTextWriter(string newLine, int indentSize)
    {
        if (indentSize <= 0)
        {
            indentSize = 4;
        }
        var w = new TextWriter();
        w.newLine = newLine;
        w.indentSize = indentSize;
        w.Clear();
        return w;
    }
}
