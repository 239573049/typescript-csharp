// Ported from tsgo internal/format/comment_test.go (whole file).
//
// PORT-NOTE: Go threads lsutil.FormatCodeSettings through the format package and reads it
// back from a context.Context; here the settings live in Ts.Format and the context is an
// explicit FormatContext. Go's `assert.Check` (non-fatal) maps to the corresponding xUnit
// assertion. Non-table t.Run scenario groups become one [Fact] each, named
// <ParentTest>_<SubName> (plan/05-conventions.md §16.1).

using Ts.Ast;
using Ts.Core;
using Ts.Format;
using Ts.Parser;
using Ts.TsPath;
using Xunit;

namespace Ts.Format.Tests;

public sealed class CommentTests
{
    private static SourceFile Parse(string text, ScriptKind kind, string fileName = "/test.ts")
    {
        var options = new SourceFileParseOptions
        {
            FileName = fileName,
            Path = new Path(fileName),
        };
        return global::Ts.Parser.Parser.ParseSourceFile(options, text, kind);
    }

    // comment_test.go:20-31 etc. — the shared lsutil.FormatCodeSettings literal of this file.
    private static FormatContext Ctx(
        bool convertTabsToSpaces,
        int baseIndentSize,
        bool insertSpaceBeforeTypeAnnotation = false,
        bool insertSpaceBeforeAndAfterBinaryOperators = false)
    {
        var settings = new FormatCodeSettings
        {
            TabSize = 4,
            IndentSize = 4,
            BaseIndentSize = baseIndentSize,
            NewLineCharacter = "\n",
            ConvertTabsToSpaces = TristateExtensions.BoolToTristate(convertTabsToSpaces),
            IndentStyle = IndentStyle.Smart,
            TrimTrailingWhitespace = Tristate.True,
        };
        if (insertSpaceBeforeTypeAnnotation)
        {
            settings.InsertSpaceBeforeTypeAnnotation = Tristate.True;
        }
        if (insertSpaceBeforeAndAfterBinaryOperators)
        {
            settings.InsertSpaceBeforeAndAfterBinaryOperators = Tristate.True;
        }
        return FormatContext.WithFormatCodeSettings(settings, "\n");
    }

    // comment_test.go:18-67 — TestCommentFormatting/"format comment issue reproduction".
    [Fact]
    public void CommentFormatting_FormatCommentIssueReproduction()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: true, baseIndentSize: 4, insertSpaceBeforeTypeAnnotation: true);

        // Original code that causes the bug
        const string originalText = "class C {\n    /**\n     *\n    */\n    async x() {}\n}";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        // Apply formatting once
        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string firstFormatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        // Check that the asterisk is not corrupted
        Assert.DoesNotContain("*/\n   /", firstFormatted, System.StringComparison.Ordinal);
        Assert.Contains("*/", firstFormatted, System.StringComparison.Ordinal);
        Assert.Contains("async", firstFormatted, System.StringComparison.Ordinal);

        // Apply formatting a second time to test stability
        SourceFile sourceFile2 = Parse(firstFormatted, ScriptKind.TS);
        TextChange[] edits2 = Api.FormatDocument(ctx, sourceFile2);
        string secondFormatted = global::Ts.Core.Core.ApplyBulkEdits(firstFormatted, edits2);

        // Check that second formatting doesn't introduce corruption
        Assert.DoesNotContain(" sync x()", secondFormatted, System.StringComparison.Ordinal);
        Assert.Contains("async", secondFormatted, System.StringComparison.Ordinal);
    }

    // comment_test.go:69-104 — TestCommentFormatting/"format JSDoc with tab indentation".
    [Fact]
    public void CommentFormatting_FormatJSDocWithTabIndentation()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: false, baseIndentSize: 0, insertSpaceBeforeTypeAnnotation: true);

        // Original code with tab indentation (tabs represented as \t)
        const string originalText = "class Foo {\n\t/**\n\t * @param {string} argument - This is a param description.\n\t */\n\texample(argument) {\nconsole.log(argument);\n\t}\n}";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        // Check that tabs come before spaces (not spaces before tabs).
        Assert.DoesNotContain(" \t*", formatted, System.StringComparison.Ordinal);
        Assert.Contains("\t *", formatted, System.StringComparison.Ordinal);
        Assert.Contains("\t\tconsole.log", formatted, System.StringComparison.Ordinal);
    }

    // comment_test.go:106-137 — TestCommentFormatting/"format comment inside multi-line argument list".
    [Fact]
    public void CommentFormatting_FormatCommentInsideMultiLineArgumentList()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: false, baseIndentSize: 0, insertSpaceBeforeTypeAnnotation: true);

        const string originalText = "console.log(\n\t\"a\",\n\t// the second arg\n\t\"b\"\n);";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        Assert.Contains("\t// the second arg", formatted, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\n// the second arg", formatted, System.StringComparison.Ordinal);
    }

    // comment_test.go:139-170 — TestCommentFormatting/"format comment in chained method calls".
    [Fact]
    public void CommentFormatting_FormatCommentInChainedMethodCalls()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: false, baseIndentSize: 0, insertSpaceBeforeTypeAnnotation: true);

        const string originalText = "foo\n\t.bar()\n\t// A second call\n\t.baz();";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        bool indented = formatted.Contains("\t// A second call", System.StringComparison.Ordinal) ||
            formatted.Contains("   // A second call", System.StringComparison.Ordinal);
        Assert.True(indented, "comment should be indented");
        Assert.DoesNotContain("\n// A second call", formatted, System.StringComparison.Ordinal);
    }

    // comment_test.go:172-204 — TestCommentFormatting/"format chained method call with comment (issue #1928)".
    // Regression test for issue #1928 - panic when formatting chained method call with comment.
    [Fact]
    public void CommentFormatting_FormatChainedMethodCallWithComment_Issue1928()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: false, baseIndentSize: 0, insertSpaceBeforeTypeAnnotation: true);

        // This code previously caused a panic with "strings: negative Repeat count"
        // because tokenIndentation was -1 and was being used directly for indentation
        const string originalText = "foo\n\t.bar()\n\t// A second call\n\t.baz();";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        bool indented = formatted.Contains("\t// A second call", System.StringComparison.Ordinal) ||
            formatted.Contains("   // A second call", System.StringComparison.Ordinal);
        Assert.True(indented, "comment should be indented");
        Assert.DoesNotContain("\n// A second call", formatted, System.StringComparison.Ordinal);
    }

    // comment_test.go:206-233 — TestCommentFormatting/"multiline comment inside block that opens on first line (issue #2649)".
    [Fact]
    public void CommentFormatting_MultilineCommentInsideBlockThatOpensOnFirstLine_Issue2649()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: false, baseIndentSize: 0);

        const string originalText = "document.addEventListener('DOMContentLoaded', () => {\n    /** @type {NodeListOf<HTMLSpanElement>} */\n    const elements = document.querySelectorAll('.test')\n});";

        SourceFile sourceFile = Parse(originalText, ScriptKind.JS, "/test.js");

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);
        Assert.True(formatted.Length > 0, "formatted text should not be empty");
    }

    // comment_test.go:235-262 — TestCommentFormatting/"single-line comment inside block that opens on first line (issue #2649)".
    [Fact]
    public void CommentFormatting_SingleLineCommentInsideBlockThatOpensOnFirstLine_Issue2649()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: false, baseIndentSize: 0);

        const string originalText = "document.addEventListener('DOMContentLoaded', () => {\n    // a comment\n    const x = 1\n});";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);
        Assert.True(formatted.Length > 0, "formatted text should not be empty");
    }

    // comment_test.go:268-301 — TestFormatSelectionPreservesComments/"...selection ends inside comment".
    [Fact]
    public void FormatSelectionPreservesComments_SelectionEndsInsideComment()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: true, baseIndentSize: 0);

        // Reproduce: const test/* comment */=5;
        // When selecting a range that ends inside the comment (before */), format selection
        // should not delete the comment.
        const string originalText = "const test/* comment */=5;";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        // Select a range that starts at the beginning of the line and ends inside the block
        // comment. This covers `const test/* comment`, stopping before the closing `*/`.
        int commentStart = originalText.IndexOf("/*", System.StringComparison.Ordinal);
        int selectionEnd = commentStart + "/* comment".Length;

        TextChange[] edits = Api.FormatSelection(ctx, sourceFile, 0, selectionEnd);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        Assert.Equal(originalText, formatted);
    }

    // comment_test.go:303-333 — TestFormatSelectionPreservesComments/"...selection starts inside comment".
    [Fact]
    public void FormatSelectionPreservesComments_SelectionStartsInsideComment()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: true, baseIndentSize: 0);

        const string originalText = "const test/* comment */=5;";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        // Select from inside the comment to the end
        int commentStart = originalText.IndexOf("/*", System.StringComparison.Ordinal);
        int selectionStart = commentStart + 3;

        TextChange[] edits = Api.FormatSelection(ctx, sourceFile, selectionStart, originalText.Length);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        Assert.Equal(originalText, formatted);
    }

    // comment_test.go:335-362 — TestFormatSelectionPreservesComments/"full document format should preserve block comment and add spaces".
    [Fact]
    public void FormatSelectionPreservesComments_FullDocumentFormatPreservesBlockComment()
    {
        FormatContext ctx = Ctx(
            convertTabsToSpaces: true,
            baseIndentSize: 0,
            insertSpaceBeforeAndAfterBinaryOperators: true);

        const string originalText = "const test/* comment */=5;";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        Assert.Equal("const test/* comment */ = 5;", formatted);
    }

    // comment_test.go:365-402 — TestSliceBoundsPanic/"format code with trailing semicolon should not panic".
    [Fact]
    public void SliceBoundsPanic_FormatCodeWithTrailingSemicolon()
    {
        FormatContext ctx = Ctx(convertTabsToSpaces: true, baseIndentSize: 4, insertSpaceBeforeTypeAnnotation: true);

        // Code from the issue that causes slice bounds panic
        const string originalText = "const _enableDisposeWithListenerWarning = false\n\t// || Boolean(\"TRUE\") // causes a linter warning so that it cannot be pushed\n\t;\n";

        SourceFile sourceFile = Parse(originalText, ScriptKind.TS);

        // This should not panic
        TextChange[] edits = Api.FormatDocument(ctx, sourceFile);
        string formatted = global::Ts.Core.Core.ApplyBulkEdits(originalText, edits);

        Assert.True(formatted.Length > 0, "formatted text should not be empty");
        Assert.Contains("_enableDisposeWithListenerWarning", formatted, System.StringComparison.Ordinal);
    }
}
