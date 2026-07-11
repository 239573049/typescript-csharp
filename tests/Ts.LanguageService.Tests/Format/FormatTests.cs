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
}
