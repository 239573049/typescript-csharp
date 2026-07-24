// Ported from tsgo internal/ls/displaypartswriter.go, plus the lsproto ClassificationTypeName /
// VSClassifiedTextRun pair it captures (lsp_generated.go:9299-9306 / 10570-10611; these two move to
// Protocol.g.cs when the generate-lsp codegen wave lands).

using System.Collections.Generic;
using System.Text;
using Ts.Ast;

namespace Ts.Ls;

// lsp_generated.go:10570-10611 — ClassificationTypeName (Roslyn classification names).
public static class ClassificationTypeName
{
    public const string Keyword = "keyword";
    public const string Punctuation = "punctuation";
    public const string Operator = "operator";
    public const string WhiteSpace = "whitespace";
    public const string Text = "text";
    public const string String = "string";
    public const string Number = "number";
    public const string Comment = "comment";
    public const string ClassName = "class name";
    public const string InterfaceName = "interface name";
    public const string EnumName = "enum name";
    public const string ModuleName = "module name";
    public const string MethodName = "method name";
    public const string ParameterName = "parameter name";
    public const string PropertyName = "property name";
    public const string FieldName = "field name";
    public const string LocalName = "local name";
    public const string TypeParameterName = "type parameter name";
    public const string Identifier = "identifier";
}

// lsp_generated.go:9299-9306 — VSClassifiedTextRun.
public sealed class VSClassifiedTextRun
{
    // The classification type name (e.g. 'keyword', 'class name', 'parameter name').
    public string ClassificationTypeName = "";

    // The text content of this run.
    public string Text = "";
}

// displaypartswriter.go:16-28 — displayPartsWriter implements EmitTextWriter and captures classified
// text runs for VS colorized labels, while also building a plain string.
// When vsCapability is false, only the plain string is built; runs are skipped.
public sealed class DisplayPartsWriter : global::Ts.Printer.EmitTextWriter
{
    private readonly StringBuilder builder = new();
    private List<VSClassifiedTextRun>? runs;
    private readonly bool vsCapability;
    private string lastWritten = "";

    // displaypartswriter.go:26
    public DisplayPartsWriter(bool vsCapability) => this.vsCapability = vsCapability;

    // displaypartswriter.go:30-42
    private void AddRun(string classification, string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        if (vsCapability)
        {
            runs ??= new List<VSClassifiedTextRun>();
            runs.Add(new VSClassifiedTextRun
            {
                ClassificationTypeName = classification,
                Text = text,
            });
        }
        lastWritten = text;
        builder.Append(text);
    }

    // displaypartswriter.go:44-47 — WriteClassified writes text with an explicit classification type.
    public void WriteClassified(string text, string classification) => AddRun(classification, text);

    // displaypartswriter.go:49-58 — WriteFrom copies the accumulated content from another writer.
    public void WriteFrom(DisplayPartsWriter other)
    {
        builder.Append(other.String());
        if (vsCapability && other.runs != null)
        {
            runs ??= new List<VSClassifiedTextRun>();
            runs.AddRange(other.runs);
        }
        if (other.lastWritten.Length > 0)
        {
            lastWritten = other.lastWritten;
        }
    }

    // displaypartswriter.go:60-62
    public VSClassifiedTextRun[] GetRuns() => runs?.ToArray() ?? [];

    // displaypartswriter.go:64-66
    public string String() => builder.ToString();

    // displaypartswriter.go:68-72
    public void Clear()
    {
        lastWritten = "";
        builder.Clear();
        runs = null;
    }

    public void DecreaseIndent()
    {
    }

    public int GetColumn() => 0;

    public int GetIndent() => 0;

    public int GetLine() => 0;

    // displaypartswriter.go:82-84 — Go builder.Len() is a byte count; every appended chunk is also
    // counted by the C# UTF-16 length consistently within this writer (position is only compared
    // against positions produced by the same writer).
    public int GetTextPos() => builder.Length;

    public bool HasTrailingComment() => false;

    // displaypartswriter.go:88-97
    public bool HasTrailingWhitespace()
    {
        if (builder.Length == 0)
        {
            return false;
        }
        if (lastWritten.Length == 0)
        {
            return false;
        }
        var ch = char.ConvertToUtf32(lastWritten, lastWritten.Length >= 2 && char.IsSurrogatePair(lastWritten[^2], lastWritten[^1]) ? lastWritten.Length - 2 : lastWritten.Length - 1);
        return global::Ts.StringUtil.StringUtil.IsWhiteSpaceLike(ch);
    }

    public void IncreaseIndent()
    {
    }

    public bool IsAtStartOfLine() => false;

    public void RawWrite(string s) => AddRun(ClassificationTypeName.Text, s);

    public void Write(string s) => AddRun(ClassificationTypeName.Text, s);

    // Strada's writeComment uses unknownWrite → SymbolDisplayPartKind.text → "text"
    public void WriteComment(string text) => AddRun(ClassificationTypeName.Text, text);

    public void WriteKeyword(string text) => AddRun(ClassificationTypeName.Keyword, text);

    public void WriteLine() => AddRun(ClassificationTypeName.WhiteSpace, " ");

    public void WriteLineForce(bool force) => AddRun(ClassificationTypeName.WhiteSpace, " ");

    // Strada's writeLiteral → SymbolDisplayPartKind.stringLiteral → "string"
    public void WriteLiteral(string s) => AddRun(ClassificationTypeName.String, s);

    public void WriteOperator(string text) => AddRun(ClassificationTypeName.Operator, text);

    public void WriteParameter(string text) => AddRun(ClassificationTypeName.ParameterName, text);

    public void WriteProperty(string text) => AddRun(ClassificationTypeName.PropertyName, text);

    public void WritePunctuation(string text) => AddRun(ClassificationTypeName.Punctuation, text);

    public void WriteSpace(string text) => AddRun(ClassificationTypeName.WhiteSpace, text);

    public void WriteStringLiteral(string text) => AddRun(ClassificationTypeName.String, text);

    public void WriteSymbol(string text, Symbol symbol) => AddRun(ClassificationForSymbol(symbol), text);

    public void WriteTrailingSemicolon(string text) => AddRun(ClassificationTypeName.Punctuation, text);

    // displaypartswriter.go:166-208 — classificationForSymbol determines the Roslyn classification
    // type name based on a symbol's flags. Matches the Strada translation chain:
    // displayPartKind() → GetClassificationName().
    public static string ClassificationForSymbol(Symbol? symbol)
    {
        if (symbol == null)
        {
            return ClassificationTypeName.Text;
        }
        var flags = symbol.Flags;
        if ((flags & SymbolFlags.Variable) != 0)
        {
            return IsFirstDeclarationOfSymbolParameter(symbol)
                ? ClassificationTypeName.ParameterName
                : ClassificationTypeName.LocalName;
        }
        if ((flags & SymbolFlags.Property) != 0)
        {
            return ClassificationTypeName.PropertyName;
        }
        if ((flags & SymbolFlags.GetAccessor) != 0)
        {
            return ClassificationTypeName.PropertyName;
        }
        if ((flags & SymbolFlags.SetAccessor) != 0)
        {
            return ClassificationTypeName.PropertyName;
        }
        if ((flags & SymbolFlags.EnumMember) != 0)
        {
            return ClassificationTypeName.FieldName;
        }
        if ((flags & SymbolFlags.Function) != 0)
        {
            return ClassificationTypeName.MethodName;
        }
        if ((flags & SymbolFlags.Class) != 0)
        {
            return ClassificationTypeName.ClassName;
        }
        if ((flags & SymbolFlags.Interface) != 0)
        {
            return ClassificationTypeName.InterfaceName;
        }
        if ((flags & SymbolFlags.Enum) != 0)
        {
            return ClassificationTypeName.EnumName;
        }
        if ((flags & SymbolFlags.Module) != 0)
        {
            return ClassificationTypeName.ModuleName;
        }
        if ((flags & SymbolFlags.Method) != 0)
        {
            return ClassificationTypeName.MethodName;
        }
        if ((flags & SymbolFlags.TypeParameter) != 0)
        {
            return ClassificationTypeName.TypeParameterName;
        }
        if ((flags & SymbolFlags.TypeAlias) != 0)
        {
            return ClassificationTypeName.Identifier;
        }
        if ((flags & SymbolFlags.Alias) != 0)
        {
            return ClassificationTypeName.Identifier;
        }
        return ClassificationTypeName.Text;
    }

    // displaypartswriter.go:210-217 — isFirstDeclarationOfSymbolParameter checks if the symbol's
    // first declaration is a parameter.
    private static bool IsFirstDeclarationOfSymbolParameter(Symbol symbol)
    {
        var declarations = symbol.Declarations;
        if (declarations is not { Length: > 0 })
        {
            return false;
        }
        return declarations[0].Kind == SyntaxKind.Parameter;
    }
}
