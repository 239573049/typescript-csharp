// Ported from tsgo internal/fourslash/baselineutil.go (:503-1182) — the LOOSE baseline text
// producers: the location/span grouping (getBaselineForLocations/Spans/GroupedSpansWithFileContents),
// textOfFile, the marker merger (getBaselineContentForFile + textWithContext), and the
// content-with-tooltips annotation (annotateContentWithTooltips).
//
// PORT-NOTES:
//   - documentSpan is a reference type: Go keys spanToContextId and compares `d1.span == d2.span`
//     by pointer, so the C# side uses reference identity (ReferenceEqualityComparer).
//   - Go's `slices.SortStableFunc` → LINQ OrderBy (stable).
//   - collections.OrderedMap → insertion-ordered list of entries.
//   - The marker+item JSON section that Go renders with core.StringifyJson is emitted per call
//     site as a typed Utf8JsonWriter/string builder (AOT, plan/07); the shapes are fixed and small.

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ts.Ls.LsConv;
using Xunit;
using LsProto = global::Ts.LsProto;

namespace Ts.TestUtil.Fourslash;

// baselineutil.go:503-518 — documentSpan.
public sealed class DocumentSpan
{
    public DocumentSpan(LsProto.DocumentUri uri, LsProto.Range textSpan, LsProto.Range? contextSpan = null)
    {
        Uri = uri;
        TextSpan = textSpan;
        ContextSpan = contextSpan;
    }

    public LsProto.DocumentUri Uri { get; }

    public LsProto.Range TextSpan { get; }

    public LsProto.Range? ContextSpan { get; }
}

// baselineutil.go:520-536 — baselineFourslashLocationsOptions.
public sealed class BaselineFourslashLocationsOptions
{
    public Marker? Marker;
    public string MarkerName = "/*MARKER*/";
    public string? EndMarker;
    public Func<DocumentSpan, string?>? StartMarkerPrefix;
    public Func<DocumentSpan, string?>? EndMarkerSuffix;
    public Func<DocumentSpan, string>? GetLocationData;
    public DocumentSpan? AdditionalSpan;
    public bool PreserveResultOrder;
    public LsProto.DocumentUri[] OrderedFiles = [];
}

public sealed partial class FourslashTest
{
    // baselineutil.go:525-536 — getBaselineForLocationsWithFileContents / ForSpansWithFileContents.
    private string GetBaselineForLocationsWithFileContents(
        IReadOnlyList<LsProto.Location> locations,
        BaselineFourslashLocationsOptions options)
    {
        return GetBaselineForSpansWithFileContents(
            [.. locations.Select(static loc => new DocumentSpan(loc.Uri, loc.Range))],
            options);
    }

    private string GetBaselineForSpansWithFileContents(
        List<DocumentSpan> spans,
        BaselineFourslashLocationsOptions options)
    {
        var spansByFile = GroupSpansByFile(spans);
        if (options.PreserveResultOrder)
        {
            options.OrderedFiles = UniqueFilesInSpanOrder(spans);
        }
        return GetBaselineForGroupedSpansWithFileContents(spansByFile, options);
    }

    // collections.GroupBy → insertion-ordered grouping by uri.
    private static List<KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>> GroupSpansByFile(List<DocumentSpan> spans)
    {
        var order = new List<LsProto.DocumentUri>();
        var byFile = new Dictionary<string, List<DocumentSpan>>(StringComparer.Ordinal);
        foreach (DocumentSpan span in spans)
        {
            string key = span.Uri.Value;
            if (!byFile.TryGetValue(key, out List<DocumentSpan>? bucket))
            {
                bucket = [];
                byFile[key] = bucket;
                order.Add(span.Uri);
            }
            bucket.Add(span);
        }
        return [.. order.Select(uri => new KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>(uri, byFile[uri.Value]))];
    }

    // baselineutil.go:638-652 — uniqueFilesInSpanOrder.
    private static LsProto.DocumentUri[] UniqueFilesInSpanOrder(List<DocumentSpan> spans)
    {
        if (spans.Count == 0)
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LsProto.DocumentUri>(spans.Count);
        foreach (DocumentSpan span in spans)
        {
            if (seen.Add(span.Uri.Value))
            {
                result.Add(span.Uri);
            }
        }
        return [.. result];
    }

    // baselineutil.go:550-636 — getBaselineForGroupedSpansWithFileContents.
    private string GetBaselineForGroupedSpansWithFileContents(
        List<KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>> groupedRanges,
        BaselineFourslashLocationsOptions options)
    {
        // We must always print the file containing the marker,
        // but don't want to print it twice at the end if it already
        // found in a file with ranges.
        bool foundMarker = false;
        bool foundAdditionalLocation = false;
        var spanToContextId = NewSpanMap();

        var baselineEntries = new List<string>();
        void AddFileEntry(string path)
        {
            LsProto.DocumentUri fileName = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(path);
            List<DocumentSpan>? ranges = GetGrouped(groupedRanges, fileName);
            if (ranges == null || ranges.Count == 0)
            {
                return;
            }

            // baselineutil.go:654-659 — textOfFile.
            if (!TryTextOfFile(path, out string content))
            {
                return;
            }

            if (options.Marker != null && options.Marker.FileName == path)
            {
                foundMarker = true;
            }

            if (options.AdditionalSpan != null && options.AdditionalSpan.Uri.Value == fileName.Value)
            {
                foundAdditionalLocation = true;
            }

            baselineEntries.Add(GetBaselineContentForFile(path, content, ranges, spanToContextId, options));
        }

        if (options.PreserveResultOrder)
        {
            foreach (LsProto.DocumentUri uri in options.OrderedFiles)
            {
                AddFileEntry(uri.FileName());
            }
        }
        else
        {
            foreach (string path in EnumerateVfsFiles())
            {
                AddFileEntry(path);
            }
        }

        // In Strada, there is a bug where we only ever add additional spans to baselines if we haven't
        // already added the file to the baseline.
        if (options.AdditionalSpan != null && !foundAdditionalLocation)
        {
            string fileName = options.AdditionalSpan.Uri.FileName();
            if (TryTextOfFile(fileName, out string content))
            {
                baselineEntries.Add(GetBaselineContentForFile(
                    fileName,
                    content,
                    [options.AdditionalSpan],
                    spanToContextId,
                    options));
                if (options.Marker != null && options.Marker.FileName == fileName)
                {
                    foundMarker = true;
                }
            }
        }

        if (!foundMarker && options.Marker != null)
        {
            // If we didn't find the marker in any file, we need to add it.
            string markerFileName = options.Marker.FileName;
            if (TryTextOfFile(markerFileName, out string content))
            {
                baselineEntries.Add(GetBaselineContentForFile(
                    markerFileName,
                    content,
                    null,
                    spanToContextId,
                    options));
            }
        }

        // !!! skipDocumentContainingOnlyMarker

        return string.Join("\n\n", baselineEntries);
    }

    private static List<DocumentSpan>? GetGrouped(
        List<KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>>> groupedRanges,
        LsProto.DocumentUri uri)
    {
        foreach (KeyValuePair<LsProto.DocumentUri, List<DocumentSpan>> kv in groupedRanges)
        {
            if (kv.Key.Value == uri.Value)
            {
                return kv.Value;
            }
        }
        return null;
    }

    private static Dictionary<DocumentSpan, int> NewSpanMap() =>
        new(ReferenceEqualityComparer.Instance);

    // baselineutil.go:654-659 — textOfFile. Go walks the vfs; the C# harness owns the file set in
    // scriptInfos, so the walk order is the test-data file order (matching Go's sorted walk of the
    // map-backed in-memory fs closely enough for the marker file to be found).
    private bool TryTextOfFile(string fileName, out string content)
    {
        if (scriptInfos.TryGetValue(fileName, out FourslashScriptInfo? script))
        {
            content = script.Content;
            return true;
        }
        content = "";
        return false;
    }

    private IEnumerable<string> EnumerateVfsFiles()
    {
        foreach (FourslashFile file in testData.Files)
        {
            string path = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(file.VirtualFileName, RootDirectory);
            if (scriptInfos.ContainsKey(path))
            {
                yield return path;
            }
        }
    }

    // baselineutil.go:661-704 — detailKind / baselineDetail.
    private enum DetailKind
    {
        Marker = 0,
        ContextStart = 1,
        TextStart = 2,
        TextEnd = 3,
        ContextEnd = 4,
    }

    private static bool IsEnd(DetailKind kind) => kind is DetailKind.ContextEnd or DetailKind.TextEnd;

    private static bool IsStart(DetailKind kind) => kind is DetailKind.ContextStart or DetailKind.TextStart;

    private sealed class BaselineDetail
    {
        public LsProto.Position Pos = new();
        public string PositionMarker = "";
        public DocumentSpan? Span;
        public DetailKind Kind;

        public LsProto.Range GetRange()
        {
            switch (Kind)
            {
                case DetailKind.ContextStart:
                case DetailKind.ContextEnd:
                    return Span!.ContextSpan!;
                case DetailKind.TextStart:
                case DetailKind.TextEnd:
                    return Span!.TextSpan;
                case DetailKind.Marker:
                    return new LsProto.Range { Start = Pos, End = Pos };
                default:
                    throw new InvalidOperationException("unknown detail kind");
            }
        }
    }

    // baselineutil.go:706-912 — getBaselineContentForFile.
    private string GetBaselineContentForFile(
        string fileName,
        string content,
        List<DocumentSpan>? spansInFile,
        Dictionary<DocumentSpan, int> spanToContextId,
        BaselineFourslashLocationsOptions options)
    {
        var details = new List<BaselineDetail>();
        var detailPrefixes = new Dictionary<BaselineDetail, string>(ReferenceEqualityComparer.Instance);
        var detailSuffixes = new Dictionary<BaselineDetail, string>(ReferenceEqualityComparer.Instance);
        bool canDetermineContextIdInline = true;

        if (options.Marker != null && options.Marker.FileName == fileName)
        {
            details.Add(new BaselineDetail { Pos = options.Marker.LSPos(), PositionMarker = options.MarkerName });
        }

        foreach (DocumentSpan span in spansInFile ?? [])
        {
            int contextSpanIndex = details.Count;

            // Add context span markers if present
            if (span.ContextSpan != null)
            {
                details.Add(new BaselineDetail
                {
                    Pos = span.ContextSpan.Start,
                    PositionMarker = "<|",
                    Span = span,
                    Kind = DetailKind.ContextStart,
                });

                // Check if context span starts after text span
                if (ComparePositions(span.ContextSpan.Start, span.TextSpan.Start) > 0)
                {
                    canDetermineContextIdInline = false;
                }
            }

            int textSpanIndex = details.Count;
            string startMarker = "[|";
            if (options.GetLocationData != null)
            {
                startMarker += options.GetLocationData(span);
            }
            details.Add(new BaselineDetail
            {
                Pos = span.TextSpan.Start,
                PositionMarker = startMarker,
                Span = span,
                Kind = DetailKind.TextStart,
            });
            details.Add(new BaselineDetail
            {
                Pos = span.TextSpan.End,
                PositionMarker = options.EndMarker ?? "|]",
                Span = span,
                Kind = DetailKind.TextEnd,
            });

            if (span.ContextSpan != null)
            {
                details.Add(new BaselineDetail
                {
                    Pos = span.ContextSpan.End,
                    PositionMarker = "|>",
                    Span = span,
                    Kind = DetailKind.ContextEnd,
                });
            }

            if (options.StartMarkerPrefix != null)
            {
                string? startPrefix = options.StartMarkerPrefix(span);
                if (startPrefix != null)
                {
                    // Special case: if this span starts at the same position as the provided marker,
                    // we want the span's prefix to appear before the marker name.
                    // i.e. We want `/*START PREFIX*/A: /*RENAME*/[|ARENAME|]`,
                    // not `/*RENAME*//*START PREFIX*/A: [|ARENAME|]`
                    if (options.Marker != null && fileName == options.Marker.FileName && ComparePositions(span.TextSpan.Start, options.Marker.LSPos()) == 0)
                    {
                        detailPrefixes[details[0]] = startPrefix;
                    }
                    else if (span.ContextSpan != null && ComparePositions(span.ContextSpan.Start, span.TextSpan.Start) == 0)
                    {
                        detailPrefixes[details[contextSpanIndex]] = startPrefix;
                    }
                    else
                    {
                        detailPrefixes[details[textSpanIndex]] = startPrefix;
                    }
                }
            }

            if (options.EndMarkerSuffix != null)
            {
                string? endSuffix = options.EndMarkerSuffix(span);
                if (endSuffix != null)
                {
                    // Same as above for suffixes:
                    if (options.Marker != null && fileName == options.Marker.FileName && ComparePositions(span.TextSpan.End, options.Marker.LSPos()) == 0)
                    {
                        detailSuffixes[details[0]] = endSuffix;
                    }
                    else if (span.ContextSpan != null && ComparePositions(span.ContextSpan.End, span.TextSpan.End) == 0)
                    {
                        detailSuffixes[details[textSpanIndex + 2]] = endSuffix;
                    }
                    else
                    {
                        detailSuffixes[details[textSpanIndex + 1]] = endSuffix;
                    }
                }
            }
        }

        // Our preferred way to write markers is
        // /*MARKER*/[| some text |]
        // [| some /*MARKER*/ text |]
        // [| some text |]/*MARKER*/
        // Go: slices.SortStableFunc → OrderBy (stable).
        List<BaselineDetail> sortedDetails = [.. details.OrderBy(static d => d, DetailComparer.Instance)];

        // !!! if canDetermineContextIdInline
        _ = canDetermineContextIdInline;

        var textWithContext = new TextWithContext(fileName, content);
        for (int index = 0; index < sortedDetails.Count; index++)
        {
            BaselineDetail detail = sortedDetails[index];
            textWithContext.Add(detail);
            textWithContext.Pos = detail.Pos;
            // Prefix
            if (detailPrefixes.TryGetValue(detail, out string? prefix))
            {
                textWithContext.NewContent.Append(prefix);
            }
            textWithContext.NewContent.Append(detail.PositionMarker);
            if (detail.Span != null)
            {
                switch (detail.Kind)
                {
                    case DetailKind.TextStart:
                        string text = "";
                        if (spanToContextId.TryGetValue(detail.Span, out int contextId))
                        {
                            bool isAfterContextStart = false;
                            for (int textStartIndex = index - 1; textStartIndex >= 0; textStartIndex--)
                            {
                                BaselineDetail textStartDetail = sortedDetails[textStartIndex];
                                if (textStartDetail.Kind == DetailKind.ContextStart && ReferenceEquals(textStartDetail.Span, detail.Span))
                                {
                                    isAfterContextStart = true;
                                    break;
                                }
                                // Marker is ok to skip over
                                if (textStartDetail.Span != null)
                                {
                                    break;
                                }
                            }
                            // Skip contextId on span thats surrounded by context span immediately
                            if (!isAfterContextStart)
                            {
                                text = text == ""
                                    ? "contextId: " + contextId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                    : "contextId: " + contextId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", " + text;
                            }
                        }
                        if (text != "")
                        {
                            textWithContext.NewContent.Append("{ ");
                            textWithContext.NewContent.Append(text);
                            textWithContext.NewContent.Append(" |}");
                        }
                        break;
                    case DetailKind.ContextStart:
                        if (canDetermineContextIdInline)
                        {
                            spanToContextId[detail.Span] = spanToContextId.Count;
                        }
                        break;
                }
            }
            if (detailSuffixes.TryGetValue(detail, out string? suffix))
            {
                textWithContext.NewContent.Append(suffix);
            }
        }
        textWithContext.Add(null);
        if (textWithContext.NewContent.Length != 0)
        {
            textWithContext.ReadableContents.Append('\n');
            textWithContext.ReadableJsoncBaseline(textWithContext.NewContent.ToString());
        }
        return textWithContext.ReadableContents.ToString();
    }

    // baselineutil.go:798-852 — the detail sort.
    private sealed class DetailComparer : IComparer<BaselineDetail>
    {
        public static readonly DetailComparer Instance = new();

        public int Compare(BaselineDetail? d1, BaselineDetail? d2)
        {
            if (d1 is null || d2 is null)
            {
                return 0;
            }
            int c = ComparePositions(d1.Pos, d2.Pos);
            if (c != 0 || (d1.Kind == DetailKind.Marker && d2.Kind == DetailKind.Marker))
            {
                return c;
            }

            // /*MARKER*/[| some text |]
            if (d1.Kind == DetailKind.Marker && IsStart(d2.Kind))
            {
                return -1;
            }
            if (d2.Kind == DetailKind.Marker && IsStart(d1.Kind))
            {
                return 1;
            }

            // [| some text |]/*MARKER*/
            if (d1.Kind == DetailKind.Marker && IsEnd(d2.Kind))
            {
                return 1;
            }
            if (d2.Kind == DetailKind.Marker && IsEnd(d1.Kind))
            {
                return -1;
            }

            // [||] or <||>
            if (ReferenceEquals(d1.Span, d2.Span))
            {
                return (int)d1.Kind - (int)d2.Kind;
            }

            // ...|><|...
            if (IsStart(d1.Kind) && IsEnd(d2.Kind))
            {
                return 1;
            }
            if (IsEnd(d1.Kind) && IsStart(d2.Kind))
            {
                return -1;
            }

            // <| ... [| ... |]|>
            if (IsEnd(d1.Kind) && IsEnd(d2.Kind))
            {
                c = ComparePositions(d2.GetRange().Start, d1.GetRange().Start);
                if (c != 0)
                {
                    return c;
                }
                return (int)d1.Kind - (int)d2.Kind;
            }

            // <|[| ... |] ... |>
            if (IsStart(d1.Kind) && IsStart(d2.Kind))
            {
                c = ComparePositions(d2.GetRange().End, d2.GetRange().End);
                if (c != 0)
                {
                    return c;
                }
                return (int)d1.Kind - (int)d2.Kind;
            }

            return 0;
        }
    }

    // lsproto.ComparePositions.
    private static int ComparePositions(LsProto.Position a, LsProto.Position b)
    {
        if (a.Line != b.Line)
        {
            return a.Line < b.Line ? -1 : 1;
        }
        if (a.Character != b.Character)
        {
            return a.Character < b.Character ? -1 : 1;
        }
        return 0;
    }

    // baselineutil.go:916-1042 — textWithContext.
    private sealed class TextWithContext : Script
    {
        private const int LinesContext = 4;

        private readonly List<int> lineStarts;
        private readonly Converters converters;

        public readonly StringBuilder ReadableContents = new();
        public readonly StringBuilder NewContent = new();
        public LsProto.Position Pos = new();
        public readonly bool IsLibFile;
        public readonly string FileNameValue;
        public readonly string Content;

        private LsProto.Position? posInfo;
        private int lineInfo;

        public TextWithContext(string fileName, string content)
        {
            FileNameValue = fileName;
            Content = content;
            IsLibFile = IsLibFileStatic(fileName);
            lineStarts = global::Ts.Ls.LsConv.ComputeLSPLineStarts(content);
            converters = new Converters("utf-16", _ => global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(content));
            ReadableContents.Append("// === ").Append(fileName).Append(" ===");
        }

        public string FileName() => FileNameValue;

        public string Text() => Content;

        private int Index(LsProto.Position pos) => converters.LineAndCharacterToPosition(this, pos);

        // baselineutil.go:967-1032 — add.
        public void Add(BaselineDetail? detail)
        {
            if (NewContent.Length == 0 && detail == null)
            {
                throw new InvalidOperationException("Unsupported");
            }
            if (detail == null || (detail.Kind != DetailKind.TextEnd && detail.Kind != DetailKind.ContextEnd))
            {
                // Calculate pos to location number of lines
                int posLineIndex = lineInfo;
                if (posInfo == null || ComparePositions(posInfo, Pos) != 0)
                {
                    posLineIndex = IndexOfLineStart(Index(Pos));
                }

                int locationLineIndex = lineStarts.Count - 1;
                if (detail != null)
                {
                    locationLineIndex = IndexOfLineStart(Index(detail.Pos));
                    posInfo = detail.Pos;
                    lineInfo = locationLineIndex;
                }

                int nLines = 0;
                if (NewContent.Length != 0)
                {
                    nLines += LinesContext + 1;
                }
                if (detail != null)
                {
                    nLines += LinesContext + 1;
                }
                // first nLinesContext and last nLinesContext
                if (locationLineIndex - posLineIndex > nLines)
                {
                    if (NewContent.Length != 0)
                    {
                        string skippedString = IsLibFile
                            ? "--- (line: --) skipped ---\n"
                            : "--- (line: " + Number(posLineIndex + LinesContext + 1) + ") skipped ---";

                        ReadableContents.Append('\n');
                        ReadableJsoncBaseline(
                            NewContent.ToString()
                            + SliceOfContent(Index(Pos), lineStarts[posLineIndex + LinesContext])
                            + skippedString);

                        if (detail != null)
                        {
                            ReadableContents.Append('\n');
                        }
                        NewContent.Clear();
                    }
                    if (detail != null)
                    {
                        NewContent.Append(IsLibFile
                            ? "--- (line: --) skipped ---\n"
                            : "--- (line: " + Number(locationLineIndex - LinesContext + 1) + ") skipped ---\n");
                        NewContent.Append(SliceOfContent(lineStarts[locationLineIndex - LinesContext + 1], Index(detail.Pos)));
                    }
                    return;
                }
            }
            if (detail == null)
            {
                NewContent.Append(SliceOfContent(Index(Pos), null));
            }
            else
            {
                NewContent.Append(SliceOfContent(Index(Pos), Index(detail.Pos)));
            }
        }

        private static string Number(int value) =>
            value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private int IndexOfLineStart(int pos)
        {
            for (int i = lineStarts.Count - 1; i >= 0; i--)
            {
                if (lineStarts[i] <= pos)
                {
                    return i;
                }
            }
            return 0;
        }

        // baselineutil.go:1142-1156 — sliceOfContent.
        private string SliceOfContent(int start, int? end)
        {
            if (start < 0)
            {
                start = 0;
            }
            int actualEnd = end == null || end.Value > Content.Length ? Content.Length : end.Value;
            if (start > actualEnd)
            {
                return "";
            }
            return Content[start..actualEnd];
        }

        // baselineutil.go:1034-1042 — readableJsoncBaseline.
        public void ReadableJsoncBaseline(string text)
        {
            string[] lines = SplitLines(text);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    ReadableContents.Append('\n');
                }
                ReadableContents.Append("// ").Append(lines[i]);
            }
        }

        // Go: regexp `\r?\n` Split(text, -1).
        private static string[] SplitLines(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                    lines.Add(text[start..end]);
                    start = i + 1;
                }
            }
            lines.Add(text[start..]);
            return [.. lines];
        }
    }

    // fourslash.go:5459-5467 — isLibFile.
    private static bool IsLibFileStatic(string fileName) =>
        fileName.StartsWith("/lib.", StringComparison.Ordinal) ||
        fileName.Contains("/node_modules/typescript/lib/", StringComparison.Ordinal);

    // baselineutil.go:1044-1047 — markerAndItem (Go's generic struct; the JSON field names are
    // `marker`/`item` and are emitted by the typed per-call-site serializer below).
    internal sealed class MarkerAndItem<T>
        where T : class
    {
        public Marker? Marker;
        public T? Item;
    }

    // baselineutil.go:1049-1140 — annotateContentWithTooltips.
    private static readonly string BarWithGutter = "| " + new string('-', 70);

    private string AnnotateContentWithTooltips<T>(
        List<MarkerAndItem<T>> markersAndItems,
        string opName,
        Func<T?, LsProto.Range?> getRange,
        Func<T?, T?, string[]> getTooltipLines)
        where T : class
    {
        // sort by file, then *backwards* by position in the file
        // so we can insert multiple times on a line without counting.
        List<MarkerAndItem<T>> sorted =
        [
            .. markersAndItems
                .OrderBy(static m => m.Marker!.FileName, StringComparer.Ordinal)
                .ThenByDescending(static m => m.Marker!.Position),
        ];

        var filesToLines = new List<KeyValuePair<string, List<string>>>();
        var filesToLinesIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        T? previous = null;
        foreach (MarkerAndItem<T> markerAndItem in sorted)
        {
            Marker marker = markerAndItem.Marker!;
            T? item = markerAndItem.Item;

            LsProto.Range? textRange = getRange(item);
            if (textRange == null)
            {
                LsProto.Position start = marker.LSPosition;
                textRange = new LsProto.Range
                {
                    Start = start,
                    End = new LsProto.Position { Line = start.Line, Character = start.Character + 1 },
                };
            }

            if (textRange.Start.Line != textRange.End.Line)
            {
                Assert.Fail("Expected text range to be on a single line, got " + textRange.Start.Line + ".." + textRange.End.Line);
            }
            string underline = new string(' ', (int)textRange.Start.Character)
                + new string('^', (int)(textRange.End.Character - textRange.Start.Character));

            string fileName = marker.FileName;
            if (!filesToLinesIndex.TryGetValue(fileName, out int linesIndex))
            {
                List<string> newLines = [.. SplitLinesRegex(getScriptInfo(fileName)!.Content)];
                linesIndex = filesToLines.Count;
                filesToLines.Add(new KeyValuePair<string, List<string>>(fileName, newLines));
                filesToLinesIndex[fileName] = linesIndex;
            }
            List<string> lines = filesToLines[linesIndex].Value;

            string[] tooltipLines = [];
            if (item != null)
            {
                tooltipLines = getTooltipLines(item, previous);
            }
            if (tooltipLines.Length == 0)
            {
                tooltipLines = ["No " + opName + " at /*" + marker.GetName() + "*/."];
            }
            tooltipLines = [.. tooltipLines.Select(static line => "| " + line)];

            var linesToInsert = new string[tooltipLines.Length + 3];
            linesToInsert[0] = underline;
            linesToInsert[1] = BarWithGutter;
            Array.Copy(tooltipLines, 0, linesToInsert, 2, tooltipLines.Length);
            linesToInsert[^1] = BarWithGutter;

            lines.InsertRange((int)textRange.Start.Line + 1, linesToInsert);
            filesToLines[linesIndex] = new KeyValuePair<string, List<string>>(fileName, lines);

            previous = item;
        }

        var builder = new StringBuilder();
        bool seenFirst = false;
        foreach (KeyValuePair<string, List<string>> entry in filesToLines)
        {
            builder.Append("=== ").Append(entry.Key).Append(" ===\n");
            foreach (string line in entry.Value)
            {
                builder.Append("// ").Append(line).Append('\n');
            }

            if (seenFirst)
            {
                builder.Append("\n\n");
            }
            else
            {
                seenFirst = true;
            }
        }

        return builder.ToString();
    }

    // Go: regexp `\r?\n` Split(text, -1).
    private static string[] SplitLinesRegex(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(text[start..end]);
                start = i + 1;
            }
        }
        lines.Add(text[start..]);
        return [.. lines];
    }
}

#endif
