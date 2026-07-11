// Ported from typescript-go internal/diagnosticwriter/diagnosticwriter.go (506 LOC).
//
// The errors.txt / --pretty diagnostic formatter: file(line,col) headers, category + `TS####`,
// flattened message chains with indentation, code-span snippets with tilde squiggles, related
// information, ANSI colours (foreground escape sequences, turned off downstream when not a tty),
// and the error-summary tabular display. Its output is byte-load-bearing against the conformance
// `.errors.txt` baseline (plan/06 §4.12) — every space, tilde, gutter width and colour code must
// match Go exactly. Positions are UTF-16 code units (plan/01 §UTF-16; the判别性 evidence for the
// whole port). 逐函数直译; Go comments kept verbatim; function order preserved.
//
// PORT-NOTE (§1.5 / AGENTS.md §6): Go abstracts over ast.Diagnostic and LSP diagnostics via the
// `Diagnostic` interface + the `ASTDiagnostic` wrapper (diagnosticwriter.go:20-99). In C# the AST
// `Ts.Ast.Diagnostic` class already exposes the full interface shape (File/Pos/End/Len/Code/
// Category/Localize/MessageChain/RelatedInformation), and the LSP diagnostic type is phase 8 and
// out of scope here, so the writer operates directly on `Ts.Ast.Diagnostic` and its concrete
// `SourceFile`. The Diagnostic interface, ASTDiagnostic adapter and the Wrap*/From*/To*/
// CompareASTDiagnostics conversion helpers collapse away (their sole purpose is the interface
// unification we no longer need). The FileLike interface likewise collapses: SourceFile is its
// only concrete implementer in scope (Diagnostic.File() returns SourceFile), so the writer takes
// SourceFile directly (SourceFile already implements SourceFileLike for the scanner line-map API). Cross-namespace references to the sibling
// `Ts.Scanner` / `Ts.TsPath` / `Ts.Core` / `Ts.Diagnostics` namespaces use global:: fully-qualified
// names (plan/05 §1.5, avoiding the outer-namespace shadowing trap).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ts.Ast;
using Ts.Core;
using Ts.Diagnostics; // for the Message.Localize extension method resolution (plan/05 §1.5:
                      // types are still referenced via global::Ts.Diagnostics.* to avoid
                      // outer-namespace shadowing; the using only enables extension lookup).

namespace Ts.DiagnosticWriter;

// diagnosticwriter.go:101-105 — FormattingOptions embeds tspath.ComparePathsOptions.
// PORT-NOTE: Go embeds the struct (promoted fields CurrentDirectory/UseCaseSensitiveFileNames);
// C# has no field promotion, so it is held as a named member `ComparePathsOptions`, and callers
// read formatOpts.ComparePathsOptions.CurrentDirectory (Go: formatOpts.CurrentDirectory).
public sealed class FormattingOptions
{
    public global::Ts.Locale.Locale Locale;
    public global::Ts.TsPath.ComparePathsOptions ComparePathsOptions;
    public string NewLine = "";
}

public static class DiagnosticWriter
{
    // diagnosticwriter.go:107-113 — foreground colour escape sequences.
    private const string ForegroundColorEscapeGrey = "\u001b[90m";
    private const string ForegroundColorEscapeRed = "\u001b[91m";
    private const string ForegroundColorEscapeYellow = "\u001b[93m";
    private const string ForegroundColorEscapeBlue = "\u001b[94m";
    private const string ForegroundColorEscapeCyan = "\u001b[96m";

    // diagnosticwriter.go:115-120
    private const string GutterStyleSequence = "\u001b[7m";
    private const string GutterSeparator = " ";
    private const string ResetEscapeSequence = "\u001b[0m";
    private const string Ellipsis = "...";

    // diagnosticwriter.go:122-132
    public static void FormatDiagnosticsWithColorAndContext(TextWriter output, IReadOnlyList<Diagnostic> diags, FormattingOptions formatOpts)
    {
        if (diags.Count == 0)
        {
            return;
        }
        for (int i = 0; i < diags.Count; i++)
        {
            if (i > 0)
            {
                output.Write(formatOpts.NewLine);
            }
            FormatDiagnosticWithColorAndContext(output, diags[i], formatOpts);
        }
    }

    // diagnosticwriter.go:134-167
    public static void FormatDiagnosticWithColorAndContext(TextWriter output, Diagnostic diagnostic, FormattingOptions formatOpts)
    {
        if (diagnostic.File() != null)
        {
            var file = diagnostic.File()!;
            var pos = diagnostic.Pos();
            WriteLocation(output, file, pos, formatOpts, WriteWithStyleAndReset);
            output.Write(" - ");
        }

        WriteWithStyleAndReset(output, diagnostic.Category().Name(), GetCategoryFormat(diagnostic.Category()));
        output.Write($"{ForegroundColorEscapeGrey} TS{diagnostic.Code()}: {ResetEscapeSequence}");
        WriteFlattenedDiagnosticMessage(output, diagnostic, formatOpts.NewLine, formatOpts.Locale);

        if (diagnostic.File() != null && diagnostic.Code() != global::Ts.Diagnostics.Diagnostics.File_appears_to_be_binary.Code)
        {
            output.Write(formatOpts.NewLine);
            WriteCodeSnippet(output, diagnostic.File()!, diagnostic.Pos(), diagnostic.Len(), GetCategoryFormat(diagnostic.Category()), "", formatOpts);
            output.Write(formatOpts.NewLine);
        }

        if ((diagnostic.RelatedInformation() != null) && (diagnostic.RelatedInformation()!.Length > 0))
        {
            foreach (var relatedInformation in diagnostic.RelatedInformation()!)
            {
                var file = relatedInformation.File();
                if (file != null)
                {
                    output.Write(formatOpts.NewLine);
                    output.Write("  ");
                    var pos = relatedInformation.Pos();
                    WriteLocation(output, file, pos, formatOpts, WriteWithStyleAndReset);
                    output.Write(" - ");
                    WriteFlattenedDiagnosticMessage(output, relatedInformation, formatOpts.NewLine, formatOpts.Locale);
                    WriteCodeSnippet(output, file, pos, relatedInformation.Len(), ForegroundColorEscapeCyan, "    ", formatOpts);
                }
                output.Write(formatOpts.NewLine);
            }
        }
    }

    // diagnosticwriter.go:169-251
    private static void WriteCodeSnippet(TextWriter writer, SourceFile sourceFile, int start, int length, string squiggleColor, string indent, FormattingOptions formatOpts)
    {
        var (firstLine, firstLineChar) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, start);
        var (lastLine, lastLineChar) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(sourceFile, start + length);
        if (length == 0)
        {
            lastLineChar++; // When length is zero, squiggle the character right after the start position.
        }

        var lastLineOfFile = global::Ts.Scanner.Scanner.GetECMALineOfPosition(sourceFile, sourceFile.Text().Length);

        var hasMoreThanFiveLines = lastLine - firstLine >= 4;
        var gutterWidth = Itoa(lastLine + 1).Length;
        if (hasMoreThanFiveLines)
        {
            gutterWidth = Math.Max(Ellipsis.Length, gutterWidth);
        }

        for (int i = firstLine; i <= lastLine; i++)
        {
            writer.Write(formatOpts.NewLine);

            // If the error spans over 5 lines, we'll only show the first 2 and last 2 lines,
            // so we'll skip ahead to the second-to-last line.
            if (hasMoreThanFiveLines && firstLine + 1 < i && i < lastLine - 1)
            {
                writer.Write(indent);
                writer.Write(GutterStyleSequence);
                writer.Write(PadLeft(Ellipsis, gutterWidth));
                writer.Write(ResetEscapeSequence);
                writer.Write(GutterSeparator);
                writer.Write(formatOpts.NewLine);
                i = lastLine - 1;
            }

            var lineStart = global::Ts.Scanner.Scanner.GetECMAPositionOfLineAndByteOffset(sourceFile, i, 0);
            int lineEnd;
            if (i < lastLineOfFile)
            {
                lineEnd = global::Ts.Scanner.Scanner.GetECMAPositionOfLineAndByteOffset(sourceFile, i + 1, 0);
            }
            else
            {
                lineEnd = sourceFile.Text().Length;
            }

            var lineContent = TrimRightFunc(sourceFile.Text().Substring(lineStart, lineEnd - lineStart), IsSpace); // trim from end
            lineContent = lineContent.Replace("\t", " ", StringComparison.Ordinal); // convert tabs to single spaces

            // Output the gutter and the actual contents of the line.
            writer.Write(indent);
            writer.Write(GutterStyleSequence);
            writer.Write(PadLeft(Itoa(i + 1), gutterWidth));
            writer.Write(ResetEscapeSequence);
            writer.Write(GutterSeparator);
            writer.Write(lineContent);
            writer.Write(formatOpts.NewLine);

            // Output the gutter and the error span for the line using tildes.
            writer.Write(indent);
            writer.Write(GutterStyleSequence);
            writer.Write(PadLeft("", gutterWidth));
            writer.Write(ResetEscapeSequence);
            writer.Write(GutterSeparator);
            writer.Write(squiggleColor);
            if (i == firstLine)
            {
                // If we're on the last line, then limit it to the last character of the last line.
                // Otherwise, we'll just squiggle the rest of the line, giving 'slice' no end position.
                int lastCharForLine;
                if (i == lastLine)
                {
                    lastCharForLine = lastLineChar;
                }
                else
                {
                    lastCharForLine = global::Ts.Core.CoreUtil.UTF16Len(lineContent);
                }

                // Fill with spaces until the first character,
                // then squiggle the remainder of the line.
                writer.Write(Repeat(" ", firstLineChar));
                writer.Write(Repeat("~", lastCharForLine - firstLineChar));
            }
            else if (i == lastLine)
            {
                // Squiggle until the final character.
                writer.Write(Repeat("~", lastLineChar));
            }
            else
            {
                // Squiggle the entire line.
                writer.Write(Repeat("~", global::Ts.Core.CoreUtil.UTF16Len(lineContent)));
            }

            writer.Write(ResetEscapeSequence);
        }
    }

    // diagnosticwriter.go:253-257
    public static string FlattenDiagnosticMessage(Diagnostic d, string newLine, global::Ts.Locale.Locale locale)
    {
        var output = new StringWriter();
        WriteFlattenedDiagnosticMessage(output, d, newLine, locale);
        return output.ToString();
    }

    // diagnosticwriter.go:259-261 — WriteFlattenedASTDiagnosticMessage.
    // PORT-NOTE: the ASTDiagnostic wrap collapses; this is a straight passthrough now.
    public static void WriteFlattenedASTDiagnosticMessage(TextWriter writer, Diagnostic diagnostic, string newline, global::Ts.Locale.Locale locale)
    {
        WriteFlattenedDiagnosticMessage(writer, diagnostic, newline, locale);
    }

    // diagnosticwriter.go:263-269
    public static void WriteFlattenedDiagnosticMessage(TextWriter writer, Diagnostic diagnostic, string newline, global::Ts.Locale.Locale locale)
    {
        writer.Write(diagnostic.Localize(locale));

        foreach (var chain in diagnostic.MessageChain() ?? System.Array.Empty<Diagnostic>())
        {
            FlattenDiagnosticMessageChain(writer, chain, newline, locale, 1 /*level*/);
        }
    }

    // diagnosticwriter.go:271-281
    private static void FlattenDiagnosticMessageChain(TextWriter writer, Diagnostic chain, string newLine, global::Ts.Locale.Locale locale, int level)
    {
        writer.Write(newLine);
        for (int i = 0; i < level; i++)
        {
            writer.Write("  ");
        }

        writer.Write(chain.Localize(locale));
        foreach (var child in chain.MessageChain() ?? System.Array.Empty<Diagnostic>())
        {
            FlattenDiagnosticMessageChain(writer, child, newLine, locale, level + 1);
        }
    }

    // diagnosticwriter.go:283-295
    private static string GetCategoryFormat(global::Ts.Diagnostics.Category category)
    {
        switch (category)
        {
            case global::Ts.Diagnostics.Category.Error:
                return ForegroundColorEscapeRed;
            case global::Ts.Diagnostics.Category.Warning:
                return ForegroundColorEscapeYellow;
            case global::Ts.Diagnostics.Category.Suggestion:
                return ForegroundColorEscapeGrey;
            case global::Ts.Diagnostics.Category.Message:
                return ForegroundColorEscapeBlue;
            default:
                throw new InvalidOperationException("Unhandled diagnostic category");
        }
    }

    // diagnosticwriter.go:297 — type FormattedWriter func(output io.Writer, text string, formatStyle string).
    public delegate void FormattedWriter(TextWriter output, string text, string formatStyle);

    // diagnosticwriter.go:299-303
    private static void WriteWithStyleAndReset(TextWriter output, string text, string formatStyle)
    {
        output.Write(formatStyle);
        output.Write(text);
        output.Write(ResetEscapeSequence);
    }

    // diagnosticwriter.go:305-319
    public static void WriteLocation(TextWriter output, SourceFile file, int pos, FormattingOptions? formatOpts, FormattedWriter writeWithStyleAndReset)
    {
        var (firstLine, firstChar) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(file, pos);
        string relativeFileName;
        if (formatOpts != null)
        {
            relativeFileName = global::Ts.TsPath.TsPath.ConvertToRelativePath(file.FileName(), formatOpts.ComparePathsOptions);
        }
        else
        {
            relativeFileName = file.FileName();
        }

        writeWithStyleAndReset(output, relativeFileName, ForegroundColorEscapeCyan);
        output.Write(":");
        writeWithStyleAndReset(output, Itoa(firstLine + 1), ForegroundColorEscapeYellow);
        output.Write(":");
        writeWithStyleAndReset(output, Itoa(firstChar + 1), ForegroundColorEscapeYellow);
    }

    // Some of these lived in watch.ts, but they're not specific to the watch API.

    // diagnosticwriter.go:323-328
    public sealed class ErrorSummary
    {
        public int TotalErrorCount;
        public List<Diagnostic> GlobalErrors = new();
        public Dictionary<SourceFile, List<Diagnostic>>? ErrorsByFile;
        public List<SourceFile> SortedFiles = new();
    }

    // diagnosticwriter.go:330-375
    public static void WriteErrorSummaryText(TextWriter output, IReadOnlyList<Diagnostic> allDiagnostics, FormattingOptions formatOpts)
    {
        // Roughly corresponds to 'getErrorSummaryText' from watch.ts

        var errorSummary = GetErrorSummary(allDiagnostics);
        var totalErrorCount = errorSummary.TotalErrorCount;
        if (totalErrorCount == 0)
        {
            return;
        }

        SourceFile? firstFile = null;
        if (errorSummary.SortedFiles.Count > 0)
        {
            firstFile = errorSummary.SortedFiles[0];
        }
        var firstFileName = PrettyPathForFileError(firstFile, firstFile != null ? errorSummary.ErrorsByFile?.GetValueOrDefault(firstFile) : null, formatOpts);
        var numErroringFiles = errorSummary.ErrorsByFile?.Count ?? 0;

        string message;
        if (totalErrorCount == 1)
        {
            // Special-case a single error.
            if (errorSummary.GlobalErrors.Count > 0 || firstFileName == "")
            {
                message = global::Ts.Diagnostics.Diagnostics.Found_1_error.Localize(formatOpts.Locale);
            }
            else
            {
                message = global::Ts.Diagnostics.Diagnostics.Found_1_error_in_0.Localize(formatOpts.Locale, firstFileName);
            }
        }
        else
        {
            switch (numErroringFiles)
            {
                case 0:
                    // No file-specific errors.
                    message = global::Ts.Diagnostics.Diagnostics.Found_0_errors.Localize(formatOpts.Locale, totalErrorCount);
                    break;
                case 1:
                    // One file with errors.
                    message = global::Ts.Diagnostics.Diagnostics.Found_0_errors_in_the_same_file_starting_at_Colon_1.Localize(formatOpts.Locale, totalErrorCount, firstFileName);
                    break;
                default:
                    // Multiple files with errors.
                    message = global::Ts.Diagnostics.Diagnostics.Found_0_errors_in_1_files.Localize(formatOpts.Locale, totalErrorCount, numErroringFiles);
                    break;
            }
        }
        output.Write(formatOpts.NewLine);
        output.Write(message);
        output.Write(formatOpts.NewLine);
        output.Write(formatOpts.NewLine);
        if (numErroringFiles > 1)
        {
            WriteTabularErrorsDisplay(output, errorSummary, formatOpts);
            output.Write(formatOpts.NewLine);
        }
    }

    // diagnosticwriter.go:377-410
    private static ErrorSummary GetErrorSummary(IReadOnlyList<Diagnostic> diags)
    {
        var totalErrorCount = 0;
        List<Diagnostic>? globalErrors = null;
        Dictionary<SourceFile, List<Diagnostic>>? errorsByFile = null;

        foreach (var diagnostic in diags)
        {
            if (diagnostic.Category() != global::Ts.Diagnostics.Category.Error)
            {
                continue;
            }

            totalErrorCount++;
            if (diagnostic.File() == null)
            {
                globalErrors ??= new List<Diagnostic>();
                globalErrors.Add(diagnostic);
            }
            else
            {
                errorsByFile ??= new Dictionary<SourceFile, List<Diagnostic>>();
                var file = diagnostic.File()!;
                if (!errorsByFile.TryGetValue(file, out var list))
                {
                    list = new List<Diagnostic>();
                    errorsByFile[file] = list;
                }
                list.Add(diagnostic);
            }
        }

        // !!!
        // Need an ordered map here, but sorting for consistency.
        // map-iter: 序无关 —— keys are collected then explicitly sorted by file name (Go:
        // slices.SortedFunc(maps.Keys(errorsByFile), byFileName)); ordinal string compare (§8.4).
        var sortedFiles = new List<SourceFile>(errorsByFile?.Keys ?? Enumerable_Empty());
        sortedFiles.Sort((a, b) => string.CompareOrdinal(a.FileName(), b.FileName()));

        return new ErrorSummary
        {
            TotalErrorCount = totalErrorCount,
            GlobalErrors = globalErrors ?? new List<Diagnostic>(),
            ErrorsByFile = errorsByFile,
            SortedFiles = sortedFiles,
        };
    }

    // diagnosticwriter.go:412-441
    private static void WriteTabularErrorsDisplay(TextWriter output, ErrorSummary errorSummary, FormattingOptions formatOpts)
    {
        var sortedFiles = errorSummary.SortedFiles;

        var maxErrors = 0;
        // map-iter: 序无关 —— computing a max over all files' error counts (order irrelevant).
        foreach (var errorsForFile in errorSummary.ErrorsByFile?.Values ?? Enumerable_EmptyLists())
        {
            maxErrors = Math.Max(maxErrors, errorsForFile.Count);
        }

        // !!!
        // TODO (drosen): This was never localized.
        // Should make this better.
        var headerRow = global::Ts.Diagnostics.Diagnostics.Errors_Files.Localize(formatOpts.Locale);
        var leftColumnHeadingLength = headerRow.Split(' ')[0].Length;
        var lengthOfBiggestErrorCount = Itoa(maxErrors).Length;
        var leftPaddingGoal = Math.Max(leftColumnHeadingLength, lengthOfBiggestErrorCount);
        var headerPadding = Math.Max(lengthOfBiggestErrorCount - leftColumnHeadingLength, 0);

        output.Write(Repeat(" ", headerPadding));
        output.Write(headerRow);
        output.Write(formatOpts.NewLine);

        foreach (var file in sortedFiles)
        {
            var fileErrors = errorSummary.ErrorsByFile![file];
            var errorCount = fileErrors.Count;

            output.Write(PadLeft(Itoa(errorCount), leftPaddingGoal));
            output.Write("  ");
            output.Write(PrettyPathForFileError(file, fileErrors, formatOpts));
            output.Write(formatOpts.NewLine);
        }
    }

    // diagnosticwriter.go:443-459
    private static string PrettyPathForFileError(SourceFile? file, List<Diagnostic>? fileErrors, FormattingOptions formatOpts)
    {
        if (file == null || fileErrors == null || fileErrors.Count == 0)
        {
            return "";
        }
        var line = global::Ts.Scanner.Scanner.GetECMALineOfPosition((SourceFileLike)file, fileErrors[0].Pos());
        var fileName = file.FileName();
        if (global::Ts.TsPath.TsPath.PathIsAbsolute(fileName) && global::Ts.TsPath.TsPath.PathIsAbsolute(formatOpts.ComparePathsOptions.CurrentDirectory))
        {
            fileName = global::Ts.TsPath.TsPath.ConvertToRelativePath(file.FileName(), formatOpts.ComparePathsOptions);
        }
        return $"{fileName}{ForegroundColorEscapeGrey}:{line + 1}{ResetEscapeSequence}";
    }

    // diagnosticwriter.go:461-465
    public static void WriteFormatDiagnostics(TextWriter output, IReadOnlyList<Diagnostic> diagnostics, FormattingOptions formatOpts)
    {
        foreach (var diagnostic in diagnostics)
        {
            WriteFormatDiagnostic(output, diagnostic, formatOpts);
        }
    }

    // diagnosticwriter.go:467-478
    public static void WriteFormatDiagnostic(TextWriter output, Diagnostic diagnostic, FormattingOptions formatOpts)
    {
        if (diagnostic.File() != null)
        {
            var (line, character) = global::Ts.Scanner.Scanner.GetECMALineAndUTF16CharacterOfPosition(diagnostic.File()!, diagnostic.Pos());
            var fileName = diagnostic.File()!.FileName();
            var relativeFileName = global::Ts.TsPath.TsPath.ConvertToRelativePath(fileName, formatOpts.ComparePathsOptions);
            output.Write($"{relativeFileName}({line + 1},{character + 1}): ");
        }

        output.Write($"{diagnostic.Category().Name()} TS{diagnostic.Code()}: ");
        WriteFlattenedDiagnosticMessage(output, diagnostic, formatOpts.NewLine, formatOpts.Locale);
        output.Write(formatOpts.NewLine);
    }

    // diagnosticwriter.go:480-485
    public static void FormatDiagnosticsStatusWithColorAndTime(TextWriter output, string time, Diagnostic diag, FormattingOptions formatOpts)
    {
        output.Write("[");
        WriteWithStyleAndReset(output, time, ForegroundColorEscapeGrey);
        output.Write("] ");
        WriteFlattenedDiagnosticMessage(output, diag, formatOpts.NewLine, formatOpts.Locale);
    }

    // diagnosticwriter.go:487-490
    public static void FormatDiagnosticsStatusAndTime(TextWriter output, string time, Diagnostic diag, FormattingOptions formatOpts)
    {
        // Go: fmt.Fprint(output, time, " - ") — Fprint with multiple operands inserts a space
        // between operands only when neither is a string; both are strings here, so no separator.
        output.Write(time);
        output.Write(" - ");
        WriteFlattenedDiagnosticMessage(output, diag, formatOpts.NewLine, formatOpts.Locale);
    }

    // diagnosticwriter.go:492-495
    public static readonly int[] ScreenStartingCodes =
    [
        global::Ts.Diagnostics.Diagnostics.Starting_compilation_in_watch_mode.Code,
        global::Ts.Diagnostics.Diagnostics.File_change_detected_Starting_incremental_compilation.Code,
    ];

    // diagnosticwriter.go:497-506
    public static bool TryClearScreen(TextWriter output, Diagnostic diag, global::Ts.Core.CompilerOptions options)
    {
        if (!options.PreserveWatchOutput.IsTrue() &&
            !options.ExtendedDiagnostics.IsTrue() &&
            !options.Diagnostics.IsTrue() &&
            System.Array.IndexOf(ScreenStartingCodes, diag.Code()) >= 0)
        {
            output.Write("\x1B[2J\x1B[3J\x1B[H"); // Clear screen and move cursor to home position
            return true;
        }
        return false;
    }

    // ── Go stdlib helpers used verbatim above (strconv/strings/unicode/fmt shims) ──

    // strconv.Itoa — base-10 integer to string. InvariantCulture is redundant under
    // InvariantGlobalization but explicit per plan/07 §7.3 (CA1305).
    private static string Itoa(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // strings.Repeat(s, count).
    private static string Repeat(string s, int count) => count <= 0 ? "" : string.Concat(System.Linq.Enumerable.Repeat(s, count));

    // fmt.Fprintf("%*s"/"%*d", width, x) right-justifies x in a field of `width` (spaces on the
    // left). Go pads with spaces and never truncates when the value is wider than the field.
    private static string PadLeft(string s, int width) => s.Length >= width ? s : new string(' ', width - s.Length) + s;

    // strings.TrimRightFunc(s, pred) — trim trailing runes for which pred is true. Operates on
    // Unicode code points (runes), matching Go; a lone surrogate is not IsSpace and stops trimming.
    private static string TrimRightFunc(string s, Func<int, bool> pred)
    {
        int end = s.Length;
        while (end > 0)
        {
            int lo = end - 1;
            int cp;
            int lastCharStart;
            if (char.IsLowSurrogate(s[lo]) && lo - 1 >= 0 && char.IsHighSurrogate(s[lo - 1]))
            {
                cp = char.ConvertToUtf32(s[lo - 1], s[lo]);
                lastCharStart = lo - 1;
            }
            else
            {
                cp = s[lo];
                lastCharStart = lo;
            }
            if (!pred(cp))
            {
                break;
            }
            end = lastCharStart;
        }
        return end == s.Length ? s : s.Substring(0, end);
    }

    // unicode.IsSpace — ported verbatim from Go's unicode/graphic.go IsSpace + the White_Space
    // range table (unicode/tables.go _White_Space). Latin-1 special cases plus the non-Latin1
    // White_Space code points; NOT the same as scanner IsWhiteSpaceLike (this includes NEL/NBSP
    // and the Unicode space separators the Go trim uses). plan/一.6 shim: squiggle/trim counts by
    // Unicode code point.
    private static bool IsSpace(int r)
    {
        if ((uint)r <= 0xFF)
        {
            switch (r)
            {
                case '\t':
                case '\n':
                case '\v':
                case '\f':
                case '\r':
                case ' ':
                case 0x85:
                case 0xA0:
                    return true;
                default:
                    return false;
            }
        }
        // isExcludingLatin(White_Space, r) — non-Latin1 White_Space code points.
        return r is 0x1680
            or (>= 0x2000 and <= 0x200A)
            or 0x2028 or 0x2029
            or 0x202F or 0x205F
            or 0x3000;
    }

    private static IEnumerable<SourceFile> Enumerable_Empty() => System.Array.Empty<SourceFile>();

    private static IEnumerable<List<Diagnostic>> Enumerable_EmptyLists() => System.Array.Empty<List<Diagnostic>>();
}
