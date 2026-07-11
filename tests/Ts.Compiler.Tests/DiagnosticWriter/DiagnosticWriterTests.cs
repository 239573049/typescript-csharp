// Tests for Ts.DiagnosticWriter — the errors.txt / --pretty diagnostic formatter.
//
// diagnosticwriter.go has no *_test.go in tsgo; these are fresh checks that pin the
// byte-exact single-line format (`file(line,col): error TS####: message`), UTF-16 column
// numbering, message-chain indentation, related-information layout, and the fact that
// WriteFormatDiagnostic emits NO ANSI colour codes (colours only appear in the
// FormatDiagnosticWithColorAndContext path). Positions are UTF-16 code units (plan/01 §UTF-16).

using System.Collections.Generic;
using System.IO;
using Ts.Ast;
using Ts.Core;
using Xunit;
using Diags = Ts.Diagnostics.Diagnostics;
using DW = Ts.DiagnosticWriter.DiagnosticWriter;

namespace Ts.DiagnosticWriter.Tests;

public class DiagnosticWriterTests
{
    private static SourceFile Parse(string fileName, string text)
    {
        var opts = new SourceFileParseOptions { FileName = fileName, Path = new Ts.TsPath.Path(fileName) };
        return global::Ts.Parser.Parser.ParseSourceFile(opts, text, ScriptKind.TS);
    }

    private static Ts.DiagnosticWriter.FormattingOptions Opts(string newLine = "\n") => new()
    {
        NewLine = newLine,
        ComparePathsOptions = new Ts.TsPath.ComparePathsOptions
        {
            CurrentDirectory = "/",
            UseCaseSensitiveFileNames = true,
        },
    };

    private static string WriteFormat(Diagnostic d, Ts.DiagnosticWriter.FormattingOptions opts)
    {
        var sw = new StringWriter();
        DW.WriteFormatDiagnostic(sw, d, opts);
        return sw.ToString();
    }

    [Fact]
    public void WriteFormatDiagnostic_SingleLine_MatchesTscFormat()
    {
        // `const x: number = "hi";` — the string literal starts at column 19 (0-based pos 18).
        var text = "const x: number = \"hi\";";
        var file = Parse("/t.ts", text);
        int pos = text.IndexOf('"');
        var d = Diagnostic.NewDiagnostic(
            file,
            new TextRange(pos, pos + 4),
            Diags.Type_0_is_not_assignable_to_type_1,
            "string",
            "number");

        var outText = WriteFormat(d, Opts());

        // file(line,col): error TS2322: Type 'string' is not assignable to type 'number'.\n
        Assert.Equal(
            "t.ts(1,19): error TS2322: Type 'string' is not assignable to type 'number'.\n",
            outText);
        // WriteFormatDiagnostic never emits colour escapes.
        Assert.DoesNotContain("\u001b", outText);
    }

    [Fact]
    public void WriteFormatDiagnostic_ColumnIsOneBased_AtFirstColumn()
    {
        var text = "let a = 1;";
        var file = Parse("/t.ts", text);
        // Error at pos 0 → (1,1).
        var d = Diagnostic.NewDiagnostic(file, new TextRange(0, 3), Diags.Cannot_find_name_0, "a");
        var outText = WriteFormat(d, Opts());
        Assert.Equal("t.ts(1,1): error TS2304: Cannot find name 'a'.\n", outText);
    }

    [Fact]
    public void WriteFormatDiagnostic_LineAndColumnFollowNewlines()
    {
        // Two lines; put the error on the second line at column 3 (0-based line-offset 2).
        var text = "const a = 1;\n  bad;";
        var file = Parse("/t.ts", text);
        int lineStart = text.IndexOf('\n') + 1; // start of line 2
        int pos = lineStart + 2;                // column 3 on line 2
        var d = Diagnostic.NewDiagnostic(file, new TextRange(pos, pos + 3), Diags.Cannot_find_name_0, "bad");
        var outText = WriteFormat(d, Opts());
        Assert.Equal("t.ts(2,3): error TS2304: Cannot find name 'bad'.\n", outText);
    }

    [Fact]
    public void WriteFormatDiagnostic_ColumnIsUtf16CodeUnits_PastAstralChar()
    {
        // A non-BMP astral character (U+1D4B3, MATHEMATICAL SCRIPT CAPITAL X) is one code point
        // but TWO UTF-16 code units. Columns count code units (plan/01 §UTF-16): the char after
        // it is at column 3, not 2.
        var astral = "\U0001D4B3"; // surrogate pair, 2 UTF-16 units
        var text = "const " + astral + "x = 1;";
        var file = Parse("/t.ts", text);
        // Position of the 'x' right after the astral char: "const " is 6 units, astral is 2 → 8.
        int pos = 6 + 2;
        var d = Diagnostic.NewDiagnostic(file, new TextRange(pos, pos + 1), Diags.Cannot_find_name_0, "x");
        var outText = WriteFormat(d, Opts());
        // Column = 8 UTF-16 units before → 0-based 8 → 1-based 9.
        Assert.Equal("t.ts(1,9): error TS2304: Cannot find name 'x'.\n", outText);
    }

    [Fact]
    public void WriteFormatDiagnostic_NoFile_OmitsLocationHeader()
    {
        var d = Diagnostic.NewCompilerDiagnostic(Diags.Cannot_find_name_0, "x");
        var outText = WriteFormat(d, Opts());
        Assert.Equal("error TS2304: Cannot find name 'x'.\n", outText);
    }

    [Fact]
    public void WriteFlattenedDiagnosticMessage_MessageChain_IndentsByLevel()
    {
        // Build a two-deep message chain: leaf → parent. flattenDiagnosticMessageChain indents
        // each level by two spaces per level, prefixed by the newline.
        var leaf = Diagnostic.NewCompilerDiagnostic(Diags.Cannot_find_name_0, "inner");
        var parent = Diagnostic.NewCompilerDiagnostic(Diags.Type_0_is_not_assignable_to_type_1, "A", "B")
            .SetMessageChain([leaf]);

        var sw = new StringWriter();
        DW.WriteFlattenedDiagnosticMessage(sw, parent, "\n", Ts.Locale.Locale.Default);

        Assert.Equal(
            "Type 'A' is not assignable to type 'B'.\n  Cannot find name 'inner'.",
            sw.ToString());
    }

    [Fact]
    public void WriteFlattenedDiagnosticMessage_NestedChain_IndentsDeeper()
    {
        var deep = Diagnostic.NewCompilerDiagnostic(Diags.Cannot_find_name_0, "deep");
        var mid = Diagnostic.NewCompilerDiagnostic(Diags.Cannot_find_name_0, "mid").SetMessageChain([deep]);
        var top = Diagnostic.NewCompilerDiagnostic(Diags.Cannot_find_name_0, "top").SetMessageChain([mid]);

        var sw = new StringWriter();
        DW.WriteFlattenedDiagnosticMessage(sw, top, "\n", Ts.Locale.Locale.Default);

        Assert.Equal(
            "Cannot find name 'top'.\n  Cannot find name 'mid'.\n    Cannot find name 'deep'.",
            sw.ToString());
    }

    [Fact]
    public void WriteFormatDiagnostics_MultipleDiagnostics_OneLinePerDiagnostic()
    {
        var text = "let a = 1;";
        var file = Parse("/t.ts", text);
        var d1 = Diagnostic.NewDiagnostic(file, new TextRange(0, 3), Diags.Cannot_find_name_0, "a");
        var d2 = Diagnostic.NewDiagnostic(file, new TextRange(4, 5), Diags.Cannot_find_name_0, "b");

        var sw = new StringWriter();
        DW.WriteFormatDiagnostics(sw, new List<Diagnostic> { d1, d2 }, Opts());

        Assert.Equal(
            "t.ts(1,1): error TS2304: Cannot find name 'a'.\n" +
            "t.ts(1,5): error TS2304: Cannot find name 'b'.\n",
            sw.ToString());
    }

    [Fact]
    public void FormatDiagnosticWithColorAndContext_EmitsSquiggleAndColors()
    {
        // The pretty path: location header, colours, the source line, and a tilde squiggle under
        // the error span. Assert the structural pieces (colour codes present, squiggle length =
        // span width in UTF-16 units).
        var text = "let a = 1;";
        var file = Parse("/t.ts", text);
        // Squiggle the identifier 'a' at pos 4, length 1.
        var d = Diagnostic.NewDiagnostic(file, new TextRange(4, 5), Diags.Cannot_find_name_0, "a");

        var sw = new StringWriter();
        DW.FormatDiagnosticWithColorAndContext(sw, d, Opts());
        var outText = sw.ToString();

        Assert.Contains("\u001b[91m", outText);         // red = error category colour
        Assert.Contains("error", outText);
        Assert.Contains("TS2304", outText);
        Assert.Contains("Cannot find name 'a'.", outText);
        Assert.Contains("let a = 1;", outText);          // the source line
        Assert.Contains("~", outText);                    // squiggle
        // Exactly one tilde for a length-1 span.
        Assert.Equal(1, System.Linq.Enumerable.Count(outText, c => c == '~'));
    }

    [Fact]
    public void Category_Name_IsLowercase()
    {
        Assert.Equal("error", Ts.Diagnostics.CategoryExtensions.Name(Ts.Diagnostics.Category.Error));
        Assert.Equal("warning", Ts.Diagnostics.CategoryExtensions.Name(Ts.Diagnostics.Category.Warning));
    }

    [Fact]
    public void Format_SubstitutesPlaceholders()
    {
        Assert.Equal("Type 'X' is not assignable to type 'Y'.",
            Diags.Format("Type '{0}' is not assignable to type '{1}'.", ["X", "Y"]));
        // No args → template returned verbatim.
        Assert.Equal("no placeholders", Diags.Format("no placeholders", []));
    }

    [Fact]
    public void Localize_EnglishIdentity_ReturnsTemplateWithArgs()
    {
        var d = Diagnostic.NewCompilerDiagnostic(Diags.Cannot_find_name_0, "foo");
        Assert.Equal("Cannot find name 'foo'.", d.Localize(Ts.Locale.Locale.Default));
    }
}
