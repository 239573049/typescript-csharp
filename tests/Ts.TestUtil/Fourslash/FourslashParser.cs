// Ported from tsgo internal/fourslash/test_parser.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7):
// ParseTestData (:113-168), parseFileContent (:228-451), getObjectMarker (:453-480), reportError
// (:482-484), chompLeadingSpace (:486-501), plus the config/global-option helpers (:170-185).
// The multi-file split comes from internal/testrunner/test_case_parser.go via Ts.TestUtil's
// TestCaseParser port (plan/08 §4.3: fourslash uses the AllowImplicitFirstFile=true branch).
//
// PORT-NOTES:
//   - Go walks the content rune-by-rune over UTF-8 bytes; this port walks UTF-16 code units one
//     step at a time (plan/01 §UTF-16). Marker delimiters are ASCII, so the state machine is
//     unchanged and every recorded position is already the C# string offset the rest of the
//     codebase calls `position`.
//   - Go computes marker LS positions at parse time with lsconv (UTF-8 converters). The line-map
//     converters live in Ts.LanguageService, which Ts.TestUtil must not reference, so the raw
//     ints stop here and FourslashMarkers.FromProject (runner side) performs the lift.
//   - `t.Fatalf` becomes an exception with the Go message text; `fourslashError` folds into
//     InvalidOperationException carrying the same `file (line,col): message` string.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ts.TestUtil.Fourslash;

public static class FourslashParser
{
    // test_parser.go:218 — emitThisFileOption.
    private const string EmitThisFileOption = "emitthisfile";

    // test_parser.go:113-168 — ParseTestData.
    public static FourslashProject ParseTestData(string contents, string fileName)
    {
        // List of all the subfiles we've parsed out
        var files = new List<FourslashFile>();

        var markerPositions = new Dictionary<string, FourslashMarkerPosition>(StringComparer.Ordinal);
        var markers = new List<FourslashMarker>();
        var rawRanges = new List<RawRange>();

        ParseTestFilesResult<FileWithMarkers> parsed = TestCaseParser.ParseTestFilesAndSymlinksWithOptions(
            contents,
            fileName,
            ParseFileContent,
            new ParseTestFilesOptions { AllowImplicitFirstFile = true });
        if (parsed.Error != null)
        {
            throw new InvalidOperationException("Error parsing fourslash data: " + parsed.Error.Message);
        }

        bool hasTSConfig = false;
        foreach (FileWithMarkers file in parsed.Units)
        {
            files.Add(file.File);
            hasTSConfig = hasTSConfig || IsConfigFile(file.File.VirtualFileName);

            markers.AddRange(file.Markers);
            rawRanges.AddRange(file.Ranges);
            foreach (FourslashMarker marker in file.Markers)
            {
                if (marker.Name == null)
                {
                    if (marker.Data != null)
                    {
                        // The marker is an anonymous object marker, which does not need a name. Markers are only set into markerPositions if they have a name
                        continue;
                    }
                    throw new InvalidOperationException($"Marker at position {marker.Position} is unnamed");
                }
                if (markerPositions.TryGetValue(marker.Name, out FourslashMarkerPosition? existing))
                {
                    throw new InvalidOperationException(
                        $"Duplicate marker name: \"{marker.Name}\" at {marker.Position} and {existing.Position}");
                }
                markerPositions[marker.Name] = new FourslashMarkerPosition(marker.FileName, marker.Position);
            }
        }

        if (hasTSConfig &&
            HasUnsupportedGlobalOptionsWithConfig(parsed.GlobalOptions) &&
            !IsStateBaseliningEnabled(parsed.GlobalOptions))
        {
            throw new InvalidOperationException("It is not allowed to use global options along with config files.");
        }

        var fourslashRanges = new List<FourslashRange>(rawRanges.Count);
        var namedRanges = new Dictionary<string, FourslashRange>(StringComparer.Ordinal);
        for (int index = 0; index < rawRanges.Count; index++)
        {
            RawRange rawRange = rawRanges[index];
            int? markerIndex = null;
            if (rawRange.Marker is not null)
            {
                // Go keeps a *Marker pointer on the range; the raw record carries the marker's
                // list position instead (reference identity — each marker object is created once).
                markerIndex = markers.FindIndex(marker => ReferenceEquals(marker, rawRange.Marker));
                if (markerIndex < 0)
                {
                    throw new InvalidOperationException("Range marker linkage lost during parse.");
                }
            }
            var range = new FourslashRange(
                index,
                rawRange.Marker?.Name,
                rawRange.FileName,
                rawRange.Start,
                rawRange.End,
                markerIndex);
            fourslashRanges.Add(range);
            // Task-specific convenience: named ranges also indexable by their marker name.
            // (Go reaches ranges by slice position / GetRangesByText instead.)
            if (range.Name is not null && !namedRanges.TryAdd(range.Name, range))
            {
                throw new InvalidOperationException("Duplicate fourslash range name: " + range.Name);
            }
        }

        return new FourslashProject(
            files,
            markerPositions,
            markers,
            fourslashRanges,
            namedRanges,
            parsed.GlobalOptions,
            parsed.Symlinks);
    }

    // test_parser.go:109-111 — isStateBaseliningEnabled.
    private static bool IsStateBaseliningEnabled(IReadOnlyDictionary<string, string> globalOptions) =>
        globalOptions.TryGetValue("statebaseline", out string? value) &&
        string.Equals(value, "true", StringComparison.Ordinal);

    // test_parser.go:170-180 — hasUnsupportedGlobalOptionsWithConfig.
    // map-iter: 序无关 — any unrecognized option name fails the check; order cannot matter.
    private static bool HasUnsupportedGlobalOptionsWithConfig(IReadOnlyDictionary<string, string> globalOptions)
    {
        foreach (string option in globalOptions.Keys)
        {
            switch (AsciiToLower(option))
            {
                case "symlink":
                case "link":
                case "usecasesensitivefilenames":
                    continue;
                default:
                    return true;
            }
        }
        return false;
    }

    // test_parser.go:182-185 — isConfigFile.
    private static bool IsConfigFile(string fileName)
    {
        fileName = AsciiToLower(fileName);
        return fileName.EndsWith("tsconfig.json", StringComparison.Ordinal) ||
            fileName.EndsWith("jsconfig.json", StringComparison.Ordinal);
    }

    // test_parser.go:187-197 — locationInformation / rangeLocationInformation.
    private sealed record LocationInformation(int Position, int SourcePosition, int SourceLine, int SourceColumn);

    private sealed class OpenRange
    {
        public OpenRange(LocationInformation location) => Location = location;

        public LocationInformation Location { get; }
        public FourslashMarker? Marker { get; set; }
    }

    private sealed record RawRange(string FileName, int Start, int End, FourslashMarker? Marker);

    private sealed record FileWithMarkers(
        FourslashFile File,
        List<FourslashMarker> Markers,
        List<RawRange> Ranges);

    // test_parser.go:228-451 — parseFileContent.
    private static FileWithMarkers ParseFileContent(
        string fileName,
        string content,
        IReadOnlyDictionary<string, string> fileOptions)
    {
        fileName = global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, "/");
        content = ChompLeadingSpace(content);

        // The file content (minus metacharacters) so far
        var output = new StringBuilder(content.Length);

        var markers = new List<FourslashMarker>();

        /// A stack of the open range markers that are still unclosed
        var openRanges = new List<OpenRange>();
        /// A list of closed ranges we've collected so far
        var rangeMarkers = new List<RawRange>();

        // The total number of metacharacters removed from the file (so far)
        int difference = 0;

        // One-based current position data
        int line = 1;
        int column = 1;

        // The current marker (or maybe multi-line comment?) we're parsing, possibly
        LocationInformation? openMarker = null;

        // The latest position of the start of an unflushed plain text area
        int lastNormalCharPosition = 0;

        void Flush(int lastSafeCharIndex)
        {
            if (lastSafeCharIndex != -1)
            {
                output.Append(content, lastNormalCharPosition, lastSafeCharIndex - lastNormalCharPosition);
            }
            else
            {
                output.Append(content, lastNormalCharPosition, content.Length - lastNormalCharPosition);
            }
        }

        var state = ParserState.None;
        // Go seeds `previousCharacter` with the first rune (RuneError when empty); UTF-16 steps
        // one code unit at a time, so `size` is always 1 and `i` starts at 1.
        char previousCharacter = content.Length > 0 ? content[0] : '\uFFFD';
        int i = content.Length > 0 ? 1 : 0;
        for (; i < content.Length; i += 1)
        {
            char currentCharacter = content[i];
            switch (state)
            {
                case ParserState.None:
                    if (previousCharacter == '[' && currentCharacter == '|')
                    {
                        // found a range start
                        openRanges.Add(new OpenRange(new LocationInformation(
                            Position: (i - 1) - difference,
                            SourcePosition: i - 1,
                            SourceLine: line,
                            SourceColumn: column)));
                        // copy all text up to marker position
                        Flush(i - 1);
                        lastNormalCharPosition = i + 1;
                        difference += 2;
                    }
                    else if (previousCharacter == '|' && currentCharacter == ']')
                    {
                        // found a range end
                        if (openRanges.Count == 0)
                        {
                            throw ReportError(fileName, line, column, "Found range end with no matching start.");
                        }
                        OpenRange rangeStart = openRanges[openRanges.Count - 1];
                        openRanges.RemoveAt(openRanges.Count - 1);

                        rangeMarkers.Add(new RawRange(
                            fileName,
                            rangeStart.Location.Position,
                            (i - 1) - difference,
                            rangeStart.Marker));

                        // copy all text up to range marker position
                        Flush(i - 1);
                        lastNormalCharPosition = i + 1;
                        difference += 2;
                    }
                    else if (previousCharacter == '/' && currentCharacter == '*' &&
                        (i + 1 >= content.Length || content[i + 1] != '/'))
                    {
                        // found a possible marker start
                        state = ParserState.InSlashStarMarker;
                        openMarker = new LocationInformation(
                            Position: (i - 1) - difference,
                            SourcePosition: i - 1,
                            SourceLine: line,
                            SourceColumn: column - 1);
                    }
                    else if (previousCharacter == '{' && currentCharacter == '|')
                    {
                        // found an object marker start
                        state = ParserState.InObjectMarker;
                        openMarker = new LocationInformation(
                            Position: (i - 1) - difference,
                            SourcePosition: i - 1,
                            SourceLine: line,
                            SourceColumn: column);
                        Flush(i - 1);
                    }
                    break;
                case ParserState.InObjectMarker:
                    // Object markers are only ever terminated by |} and have no content restrictions
                    if (previousCharacter == '|' && currentCharacter == '}')
                    {
                        string objectMarkerData = content[(openMarker!.SourcePosition + 2)..(i - 1)].Trim();
                        FourslashMarker marker = GetObjectMarker(fileName, openMarker, objectMarkerData);

                        if (openRanges.Count > 0)
                        {
                            openRanges[openRanges.Count - 1].Marker = marker;
                        }
                        markers.Add(marker);

                        // Set the current start to point to the end of the current marker to ignore its text
                        lastNormalCharPosition = i + 1;
                        difference += i + 1 - openMarker.SourcePosition;

                        // Reset the state
                        openMarker = null;
                        state = ParserState.None;
                    }
                    break;
                case ParserState.InSlashStarMarker:
                    if (previousCharacter == '*' && currentCharacter == '/')
                    {
                        // Record the marker
                        // start + 2 to ignore the */, -1 on the end to ignore the * (/ is next)
                        string markerNameText = content[(openMarker!.SourcePosition + 2)..(i - 1)].Trim();
                        var newMarker = new FourslashMarker(markerNameText, fileName, openMarker.Position, null);
                        if (openRanges.Count > 0)
                        {
                            openRanges[openRanges.Count - 1].Marker = newMarker;
                        }
                        markers.Add(newMarker);

                        // Set the current start to point to the end of the current marker to ignore its text
                        Flush(openMarker.SourcePosition);
                        lastNormalCharPosition = i + 1;
                        difference += i + 1 - openMarker.SourcePosition;

                        // Reset the state
                        openMarker = null;
                        state = ParserState.None;
                    }
                    else if (!(IsDigit(currentCharacter) ||
                        IsAsciiLetter(currentCharacter) ||
                        currentCharacter == '$' ||
                        currentCharacter == '_')) // Invalid marker character
                    {
                        if (currentCharacter == '*' && i < content.Length - 1 && content[i + 1] == '/')
                        {
                            // The marker is about to be closed, ignore the 'invalid' char
                        }
                        else
                        {
                            // We've hit a non-valid marker character, so we were actually in a block comment
                            // Bail out the text we've gathered so far back into the output
                            Flush(i);
                            lastNormalCharPosition = i;
                            openMarker = null;
                            state = ParserState.None;
                        }
                    }
                    break;
            }
            if (currentCharacter == '\n' && previousCharacter == '\r')
            {
                // Ignore trailing \n after \r
                continue;
            }
            else if (currentCharacter == '\n' || currentCharacter == '\r')
            {
                line++;
                column = 1;
                continue;
            }
            column++;
            if (i >= lastNormalCharPosition)
            {
                previousCharacter = currentCharacter;
            }
            else
            {
                // reset to avoid accidentally reusing marker delimiters as part of other markers
                previousCharacter = '\uFFFD';
            }
        }

        // Add the remaining text
        Flush(-1);

        if (openRanges.Count > 0)
        {
            OpenRange openRange = openRanges[0];
            throw ReportError(fileName, openRange.Location.SourceLine, openRange.Location.SourceColumn, "Unterminated range.");
        }

        if (openMarker != null)
        {
            throw ReportError(fileName, openMarker.SourceLine, openMarker.SourceColumn, "Unterminated marker.");
        }

        string outputString = output.ToString();

        // Set LS positions for markers — performed by the runner-side lift (see file header
        // PORT-NOTE); the raw int positions computed above are the parse output currency.

        bool emit = fileOptions.TryGetValue(EmitThisFileOption, out string? emitValue) &&
            string.Equals(emitValue, "true", StringComparison.Ordinal);

        var testFileInfo = new FourslashFile(fileName, outputString, emit);

        // Stable ordering from the upstream parser: start ascending, outer range first for ties.
        // (Go SortStableFunc; LINQ OrderBy is stable, plan/05 §6.4.)
        rangeMarkers = rangeMarkers
            .OrderBy(static r => r.Start, System.Collections.Generic.Comparer<int>.Default)
            .ThenByDescending(static r => r.End)
            .ToList();

        return new FileWithMarkers(testFileInfo, markers, rangeMarkers);
    }

    // test_parser.go:220-226 — parserState.
    private enum ParserState
    {
        None = 0,
        InSlashStarMarker,
        InObjectMarker,
    }

    // test_parser.go:453-480 — getObjectMarker.
    private static FourslashMarker GetObjectMarker(string fileName, LocationInformation location, string text)
    {
        // Attempt to parse the marker value as JSON
        JsonElement v;
        try
        {
            v = JsonDocument.Parse("{ " + text + " }").RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw ReportError(fileName, location.SourceLine, location.SourceColumn, "Unable to parse marker text " + text, e);
        }

        if (v.ValueKind != JsonValueKind.Object || CountProperties(v) == 0)
        {
            throw ReportError(fileName, location.SourceLine, location.SourceColumn, "Object markers can not be empty");
        }

        var markerValue = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in v.EnumerateObject())
        {
            markerValue[property.Name] = property.Value.Clone();
        }

        var marker = new FourslashMarker(null, fileName, location.Position, markerValue);

        // Object markers can be anonymous
        if (markerValue.TryGetValue("name", out JsonElement nameValue) &&
            nameValue.ValueKind == JsonValueKind.String)
        {
            string? name = nameValue.GetString();
            if (name is not null && name.Length != 0)
            {
                marker = marker with { Name = name };
            }
        }

        return marker;
    }

    private static int CountProperties(JsonElement value)
    {
        int count = 0;
        foreach (JsonProperty _ in value.EnumerateObject())
        {
            count++;
        }
        return count;
    }

    // test_parser.go:482-484 — reportError / fourslashError.
    private static InvalidOperationException ReportError(string fileName, int line, int col, string message, Exception? inner = null) =>
        new($"{fileName} ({line},{col}): {message}", inner);

    // test_parser.go:486-501 — chompLeadingSpace.
    private static string ChompLeadingSpace(string content)
    {
        string[] lines = content.Split('\n');
        foreach (string line in lines)
        {
            if (line.Length > 0 && line[0] != ' ')
            {
                return content;
            }
        }

        var result = new string[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0)
            {
                result[i] = lines[i][1..];
            }
            else
            {
                result[i] = lines[i];
            }
        }
        return string.Join("\n", result);
    }

    // stringutil.IsDigit (util.go:71-73) — ASCII only.
    private static bool IsDigit(char ch) => ch >= '0' && ch <= '9';

    // stringutil.IsASCIILetter (util.go:83-85).
    private static bool IsAsciiLetter(char ch) =>
        (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    // Go strings.ToLower over option/file names that are ASCII in every real corpus; use the
    // ASCII fold (String.ToLowerInvariant is banned by the analyzers).
    private static string AsciiToLower(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] is >= 'A' and <= 'Z')
            {
                char[] chars = value.ToCharArray();
                for (int j = i; j < chars.Length; j++)
                {
                    if (chars[j] is >= 'A' and <= 'Z')
                    {
                        chars[j] = (char)(chars[j] + ('a' - 'A'));
                    }
                }
                return new string(chars);
            }
        }
        return value;
    }
}
