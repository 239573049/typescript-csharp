// Ported from tsgo internal/printer/emittextwriter.go

using Ts.Ast;

namespace Ts.Printer;

// Externally opaque interface for printing text
public interface EmitTextWriter
{
    void Write(string s);
    void WriteTrailingSemicolon(string text);
    void WriteComment(string text);
    void WriteKeyword(string text);
    void WriteOperator(string text);
    void WritePunctuation(string text);
    void WriteSpace(string text);
    void WriteStringLiteral(string text);
    void WriteParameter(string text);
    void WriteProperty(string text);
    void WriteSymbol(string text, Symbol symbol);
    void WriteLine();
    void WriteLineForce(bool force);
    void IncreaseIndent();
    void DecreaseIndent();
    void Clear();
    string String();
    void RawWrite(string s);
    void WriteLiteral(string s);
    int GetTextPos();
    int GetLine();
    int GetColumn(); // PORT-NOTE: Go core.UTF16Offset dissolves to int (column measured in UTF-16 code units).
    int GetIndent();
    bool IsAtStartOfLine();
    bool HasTrailingComment();
    bool HasTrailingWhitespace();
}
