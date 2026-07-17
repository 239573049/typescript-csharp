// Ported from tsgo internal/testutil/tsbaseline/error_baseline.go and util.go.
//
// This is the byte-load-bearing .errors.txt writer used by the conformance runner. The
// diagnostic list at the top is produced by Ts.DiagnosticWriter, then every input file is
// reproduced with Go-harness-compatible inline squiggles and `!!!` annotations. Harness line
// endings are always CRLF. Diagnostic positions and line starts are UTF-16 code-unit offsets;
// the one intentional compatibility shim is that squiggle padding and width collapse surrogate
// pairs to one Unicode code point, matching Go regexp/rune counting (plan/08 §5.1).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ts.Ast;
using Ts.Core;

namespace Ts.TestUtil;

public sealed record BaselineTestFile(string UnitName, string Content);

public static partial class ErrorBaseline
{
    public const string HarnessNewLine = "\r\n";

    private static readonly global::Ts.DiagnosticWriter.FormattingOptions s_formatOptions = new()
    {
        NewLine = HarnessNewLine,
        ComparePathsOptions = new global::Ts.TsPath.ComparePathsOptions(),
        Locale = global::Ts.Locale.Locale.Default,
    };

    public static void Run(
        IBaselineTestContext testContext,
        string baselinePath,
        IReadOnlyList<BaselineTestFile> inputFiles,
        IReadOnlyList<Diagnostic> diagnostics,
        Baseline.Options options)
    {
        const string errorsSuffix = ".errors.txt";
        string fileName = Path.GetFileName(baselinePath);
        string outputName = fileName.EndsWith(errorsSuffix, StringComparison.Ordinal)
            ? fileName
            : Path.GetFileNameWithoutExtension(fileName) + errorsSuffix;
        string actual = diagnostics.Count == 0
            ? Baseline.NoContent
            : GetErrorBaseline(inputFiles, diagnostics);
        Baseline.Run(testContext, outputName, actual, options);
    }

    public static string GetErrorBaseline(
        IReadOnlyList<BaselineTestFile> inputFiles,
        IReadOnlyList<Diagnostic> inputDiagnostics)
    {
        Diagnostic[] diagnostics = inputDiagnostics.ToArray();
        Array.Sort(diagnostics, DiagnosticsCollection.CompareDiagnostics);

        var result = new StringBuilder();
        using (var topDiagnostics = new StringWriter())
        {
            global::Ts.DiagnosticWriter.DiagnosticWriter.WriteFormatDiagnostics(
                topDiagnostics,
                diagnostics,
                s_formatOptions);
            string top = RemoveTestPathPrefixes(topDiagnostics.ToString(), retainTrailingDirectorySeparator: false);
            top = DiagnosticsLocationPrefixRegex().Replace(top, "$1(--,--)");
            result.Append(top);
            result.Append(HarnessNewLine);
            result.Append(HarnessNewLine);
        }

        var output = new StringBuilder();
        bool firstLine = true;
        int totalErrorsReportedInNonLibraryNonTsconfigFiles = 0;

        void StartLine()
        {
            if (firstLine)
            {
                firstLine = false;
            }
            else
            {
                output.Append(HarnessNewLine);
            }
        }

        int OutputErrorText(Diagnostic diagnostic)
        {
            string message = global::Ts.DiagnosticWriter.DiagnosticWriter.FlattenDiagnosticMessage(
                diagnostic,
                HarnessNewLine,
                global::Ts.Locale.Locale.Default);

            foreach (string rawLine in RemoveTestPathPrefixes(message, retainTrailingDirectorySeparator: false).Split('\n'))
            {
                string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
                if (line.Length == 0)
                {
                    continue;
                }
                StartLine();
                output.Append("!!! ");
                output.Append(global::Ts.Diagnostics.CategoryExtensions.Name(diagnostic.Category()));
                output.Append(" TS");
                output.Append(diagnostic.Code());
                output.Append(": ");
                output.Append(line);
            }

            foreach (Diagnostic related in diagnostic.RelatedInformation() ?? Array.Empty<Diagnostic>())
            {
                string location = "";
                if (related.File() is not null)
                {
                    using var locationWriter = new StringWriter();
                    global::Ts.DiagnosticWriter.DiagnosticWriter.WriteLocation(
                        locationWriter,
                        related.File()!,
                        related.Pos(),
                        s_formatOptions,
                        static (writer, text, _) => writer.Write(text));
                    location = " " + RemoveTestPathPrefixes(locationWriter.ToString(), retainTrailingDirectorySeparator: false);
                    if (IsDefaultLibraryFile(related.File()!.FileName()))
                    {
                        location = DiagnosticsLocationPatternRegex().Replace(location, "$1:--:--");
                    }
                }

                StartLine();
                output.Append("!!! related TS");
                output.Append(related.Code());
                output.Append(location);
                output.Append(": ");
                output.Append(global::Ts.DiagnosticWriter.DiagnosticWriter.FlattenDiagnosticMessage(
                    related,
                    HarnessNewLine,
                    global::Ts.Locale.Locale.Default));
            }

            if (diagnostic.File() is null ||
                (!IsDefaultLibraryFile(diagnostic.File()!.FileName()) && !IsTsConfigFile(diagnostic.File()!.FileName())))
            {
                totalErrorsReportedInNonLibraryNonTsconfigFiles++;
            }
            return 1;
        }

        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.File() is null)
            {
                OutputErrorText(diagnostic);
            }
        }
        result.Append(output);
        output.Clear();

        foreach (BaselineTestFile inputFile in inputFiles)
        {
            Diagnostic[] fileErrors = diagnostics
                .Where(diagnostic => diagnostic.File() is not null && PathsEqual(
                    RemoveTestPathPrefixes(diagnostic.File()!.FileName(), retainTrailingDirectorySeparator: false),
                    RemoveTestPathPrefixes(inputFile.UnitName, retainTrailingDirectorySeparator: false)))
                .ToArray();

            StartLine();
            output.Append("==== ");
            output.Append(RemoveTestPathPrefixes(inputFile.UnitName, retainTrailingDirectorySeparator: false));
            output.Append(" (");
            output.Append(fileErrors.Length);
            output.Append(" errors) ====");

            int markedErrorCount = 0;
            int[] lineStarts = CoreUtil.ComputeECMALineStarts(inputFile.Content);
            string[] lines = LineDelimiterRegex().Split(inputFile.Content);

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (line.EndsWith('\r'))
                {
                    line = line[..^1];
                }

                int thisLineStart = lineStarts[lineIndex];
                int nextLineStart = lineIndex == lines.Length - 1
                    ? inputFile.Content.Length
                    : lineStarts[lineIndex + 1];

                StartLine();
                output.Append("    ");
                output.Append(line);

                foreach (Diagnostic diagnostic in fileErrors)
                {
                    int errorStart = diagnostic.Pos();
                    int end = errorStart + diagnostic.Len();
                    if (end >= thisLineStart && (errorStart < nextLineStart || lineIndex == lines.Length - 1))
                    {
                        int relativeOffset = errorStart - thisLineStart;
                        int length = (end - errorStart) - Math.Max(0, thisLineStart - errorStart);
                        int squiggleStart = Math.Max(0, relativeOffset);

                        StartLine();
                        output.Append("    ");
                        output.Append(PadLikeGoNonWhitespace(line.AsSpan(0, Math.Min(squiggleStart, line.Length))));

                        int squiggleEnd = Math.Max(
                            squiggleStart,
                            Math.Min(squiggleStart + length, line.Length));
                        output.Append('~', CountCodePoints(line.AsSpan(
                            Math.Min(squiggleStart, line.Length),
                            Math.Max(0, squiggleEnd - Math.Min(squiggleStart, line.Length)))));

                        if (lineIndex == lines.Length - 1 || nextLineStart > end)
                        {
                            markedErrorCount += OutputErrorText(diagnostic);
                        }
                    }
                }
            }

            if (markedErrorCount != fileErrors.Length)
            {
                throw new InvalidOperationException(
                    $"count of errors in {inputFile.UnitName}: marked {markedErrorCount}, expected {fileErrors.Length}");
            }

            result.Append(output);
            output.Clear();
        }

        int libraryDiagnostics = diagnostics.Count(diagnostic =>
            diagnostic.File() is not null &&
            (IsDefaultLibraryFile(diagnostic.File()!.FileName()) || IsBuiltFile(diagnostic.File()!.FileName())));
        int tsconfigDiagnostics = diagnostics.Count(diagnostic =>
            diagnostic.File() is not null && IsTsConfigFile(diagnostic.File()!.FileName()));
        if (totalErrorsReportedInNonLibraryNonTsconfigFiles + libraryDiagnostics + tsconfigDiagnostics != diagnostics.Length)
        {
            throw new InvalidOperationException(
                $"total number of errors: emitted {totalErrorsReportedInNonLibraryNonTsconfigFiles + libraryDiagnostics + tsconfigDiagnostics}, expected {diagnostics.Length}");
        }

        return result.ToString();
    }

    private static string PadLikeGoNonWhitespace(ReadOnlySpan<char> prefix)
    {
        var output = new StringBuilder(prefix.Length);
        for (int i = 0; i < prefix.Length; i++)
        {
            char c = prefix[i];
            if (c is '\t' or '\n' or '\f' or '\r' or ' ')
            {
                output.Append(c);
                continue;
            }
            if (char.IsHighSurrogate(c) && i + 1 < prefix.Length && char.IsLowSurrogate(prefix[i + 1]))
            {
                i++;
            }
            output.Append(' ');
        }
        return output.ToString();
    }

    private static int CountCodePoints(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    internal static string RemoveTestPathPrefixes(string text, bool retainTrailingDirectorySeparator)
    {
        string replacement = retainTrailingDirectorySeparator ? "/" : "";
        return text
            .Replace("/.ts/", replacement, StringComparison.Ordinal)
            .Replace("/.lib/", replacement, StringComparison.Ordinal)
            .Replace("/.src/", replacement, StringComparison.Ordinal)
            .Replace("bundled:///libs/", replacement, StringComparison.Ordinal)
            .Replace("file:///./ts/", "file:///", StringComparison.Ordinal)
            .Replace("file:///./lib/", "file:///", StringComparison.Ordinal)
            .Replace("file:///./src/", "file:///", StringComparison.Ordinal);
    }

    private static bool IsDefaultLibraryFile(string filePath)
    {
        string fileName = global::Ts.TsPath.TsPath.GetBaseFileName(filePath);
        return fileName.StartsWith("lib.", StringComparison.Ordinal) &&
            fileName.EndsWith(".d.ts", StringComparison.Ordinal);
    }

    private static bool IsBuiltFile(string filePath) =>
        filePath.StartsWith("built/local/", StringComparison.Ordinal) ||
        filePath.StartsWith("/.ts/", StringComparison.Ordinal);

    private static bool IsTsConfigFile(string filePath) =>
        filePath.Contains("tsconfig", StringComparison.Ordinal) &&
        filePath.Contains("json", StringComparison.Ordinal);

    [GeneratedRegex("(?im)^(lib.*\\.d\\.ts)\\([0-9]+,[0-9]+\\)")]
    private static partial Regex DiagnosticsLocationPrefixRegex();

    [GeneratedRegex("(?i)(lib.*\\.d\\.ts):[0-9]+:[0-9]+")]
    private static partial Regex DiagnosticsLocationPatternRegex();

    [GeneratedRegex("\\r?\\n")]
    private static partial Regex LineDelimiterRegex();
}
