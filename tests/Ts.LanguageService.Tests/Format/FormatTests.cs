// End-to-end tests for tsgo internal/format/span.go and indent.go.

using Ts.Ast;
using Ts.Core;
using Ts.Format;
using Ts.Parser;
using Ts.TsPath;
using Xunit;

namespace Ts.Format.Tests;

public class FormatTests
{
    private static SourceFile Parse(string text)
    {
        var options = new SourceFileParseOptions
        {
            FileName = "/format.ts",
            Path = new Path("/format.ts"),
        };
        return global::Ts.Parser.Parser.ParseSourceFile(options, text, ScriptKind.TS);
    }

    private static (TextChange[] Edits, string Text) Format(string text)
    {
        SourceFile sourceFile = Parse(text);
        FormatCodeSettings settings = FormatCodeSettings.GetDefaultFormatCodeSettings();
        FormatContext context = FormatContext.WithFormatCodeSettings(settings, "\n");
        TextChange[] edits = Api.FormatDocument(context, sourceFile);
        return (edits, global::Ts.Core.Core.ApplyBulkEdits(text, edits));
    }

    [Fact]
    public void FormatDocument_ProducesCanonicalSpacingEdits()
    {
        const string input = "function f( ){return  1 }";

        (TextChange[] edits, string formatted) = Format(input);

        Assert.NotEmpty(edits);
        Assert.Equal("function f() { return 1 }", formatted);
    }

    [Fact]
    public void FormatDocument_IndentsNestedBlock()
    {
        const string input = "function f(){\nif(true){\nreturn 1;\n}\n}";

        (TextChange[] edits, string formatted) = Format(input);

        Assert.NotEmpty(edits);
        Assert.Equal("function f() {\n    if (true) {\n        return 1;\n    }\n}", formatted);
    }

    // format_test.go:15-58 — TestFormatNoTrailingSpace.
    public static TheoryData<string, string> NoTrailingSpaceData()
    {
        return new TheoryData<string, string>
        {
            { "simple statement without trailing newline", "1;" },
            { "function call without trailing newline", "console.log('hello');" },
            { "if block on single line", "if (true) { }" },
            { "class declaration", "class A {\n    // Class Contents Go Here\n}" },
            { "class declaration with trailing newline", "class A {\n    // Class Contents Go Here\n}\n" },
            { "empty block", "if (true) {}" },
            { "module declaration", "module M { }" },
            { "enum declaration", "enum E { A, B }" },
        };
    }

    // format_test.go:15-58 — TestFormatNoTrailingSpace. Go threads the settings through
    // format.WithFormatCodeSettings; the C# engine takes an explicit FormatContext.
    [Theory]
    [MemberData(nameof(NoTrailingSpaceData))]
    public void FormatNoTrailingSpace(string name, string text)
    {
        _ = name; // Go's t.Run name.
        var settings = new FormatCodeSettings
        {
            TabSize = 4,
            IndentSize = 4,
            NewLineCharacter = "\n",
            ConvertTabsToSpaces = Tristate.True,
            IndentStyle = IndentStyle.Smart,
            TrimTrailingWhitespace = Tristate.True,
        };
        FormatContext context = FormatContext.WithFormatCodeSettings(settings, "\n");
        SourceFile sourceFile = Parse(text);
        TextChange[] edits = Api.FormatDocument(context, sourceFile);
        string newText = global::Ts.Core.Core.ApplyBulkEdits(text, edits);

        // Formatting should not add trailing whitespace at end of file.
        string[] lines = newText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimEnd(' ', '\t');
            Assert.Equal(trimmed, lines[i]);
        }
    }
}
