// Ported from tsgo internal/format/indent_getindentation_test.go (whole file).
//
// PORT-NOTE: Go threads lsutil.FormatCodeSettings through the format package; here the
// settings live in Ts.Format (Format/FormatCodeSettings.cs). Indent.GetIndentation and
// Util.GetLineStartPositionForPosition are internal and visible via InternalsVisibleTo
// (plan/05-conventions.md §16).

using Ts.Ast;
using Ts.Core;
using Ts.Format;
using Ts.Parser;
using Ts.TsPath;
using Xunit;

namespace Ts.Format.Tests;

public sealed class IndentGetIndentationTests
{
    // indent_getindentation_test.go:13-39 — TestGetIndentationForNamedImportsPosition.
    [Fact]
    public void GetIndentationForNamedImportsPosition()
    {
        const string text = "import {\n    type SomeInterface,\n} from \"./exports.js\";";
        // Position 9: \n
        // Position 10: first space of "    type SomeInterface"

        var parseOptions = new SourceFileParseOptions
        {
            FileName = "/test.ts",
            Path = new Path("/test.ts"),
        };
        SourceFile sourceFile = global::Ts.Parser.Parser.ParseSourceFile(parseOptions, text, ScriptKind.TS);

        FormatCodeSettings options = FormatCodeSettings.GetDefaultFormatCodeSettings();

        // The line that contains "    type SomeInterface" starts at position 9 (the \n).
        // The getAdjustedStartPosition with LeadingTriviaOptionNone returns line start.
        int lineStart = Util.GetLineStartPositionForPosition(14, sourceFile); // 14 is somewhere in "    type"

        int indent = Indent.GetIndentation(lineStart, sourceFile, options, true);

        Assert.True(indent == 4, $"Expected indentation 4, got {indent}");
    }
}
