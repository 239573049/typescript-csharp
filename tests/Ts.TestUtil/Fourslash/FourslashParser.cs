// Ported from tsgo internal/fourslash/test_parser.go and the pinned TypeScript
// src/harness/fourslashImpl.ts parser (commit 168e7015edf98244febc8f4ae450b673b5d195d7).

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Ts.TestUtil.Fourslash;

public static class FourslashParser
{
    public static FourslashProject Parse(string contents, string fileName)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(fileName);

        var parsedFiles = new List<ParsedFile>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        string currentFileName = NormalizeFileName(fileName);
        var currentFileLines = new List<string>();

        void FlushFile()
        {
            if (currentFileLines.Count == 0)
            {
                return;
            }

            string content = string.Join('\n', currentFileLines);
            parsedFiles.Add(ParseFileContent(currentFileName, ChompLeadingSpace(content)));
            currentFileLines.Clear();
        }

        string[] lines = contents.Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.StartsWith("////", StringComparison.Ordinal))
            {
                currentFileLines.Add(line[4..]);
                continue;
            }

            if (!TryParseDirective(line, out string directiveName, out string directiveValue))
            {
                continue;
            }

            if (string.Equals(directiveName, "filename", StringComparison.Ordinal))
            {
                FlushFile();
                currentFileName = NormalizeFileName(directiveValue);
            }
            else
            {
                options[directiveName] = directiveValue;
            }
        }
        FlushFile();

        var files = new List<FourslashFile>(parsedFiles.Count);
        var markerPositions = new Dictionary<string, FourslashMarkerPosition>(StringComparer.Ordinal);
        var markers = new List<FourslashMarker>();
        var rawRanges = new List<RawRange>();

        foreach (ParsedFile parsedFile in parsedFiles)
        {
            files.Add(parsedFile.File);
            markers.AddRange(parsedFile.Markers);
            rawRanges.AddRange(parsedFile.Ranges);

            foreach (FourslashMarker marker in parsedFile.Markers)
            {
                if (marker.Name is null)
                {
                    continue;
                }

                if (!markerPositions.TryAdd(
                    marker.Name,
                    new FourslashMarkerPosition(marker.FileName, marker.Position)))
                {
                    throw new InvalidOperationException("Duplicate fourslash marker name: " + marker.Name);
                }
            }
        }

        var ranges = new List<FourslashRange>(rawRanges.Count);
        var namedRanges = new Dictionary<string, FourslashRange>(StringComparer.Ordinal);
        for (int index = 0; index < rawRanges.Count; index++)
        {
            RawRange rawRange = rawRanges[index];
            var range = new FourslashRange(
                index,
                rawRange.Name,
                rawRange.FileName,
                rawRange.Start,
                rawRange.End);
            ranges.Add(range);
            if (range.Name is not null && !namedRanges.TryAdd(range.Name, range))
            {
                throw new InvalidOperationException("Duplicate fourslash range name: " + range.Name);
            }
        }

        return new FourslashProject(files, markerPositions, markers, ranges, namedRanges, options);
    }

    private static ParsedFile ParseFileContent(string fileName, string content)
    {
        var output = new StringBuilder(content.Length);
        var markers = new List<FourslashMarker>();
        var ranges = new List<RawRange>();
        var openRanges = new List<OpenRange>();

        int i = 0;
        while (i < content.Length)
        {
            if (StartsWith(content, i, "[|"))
            {
                int afterStart = i + 2;
                string? explicitName = TryReadRangeName(content, ref afterStart);
                openRanges.Add(new OpenRange(output.Length, explicitName));
                i = afterStart;
                continue;
            }

            if (StartsWith(content, i, "|]"))
            {
                if (openRanges.Count == 0)
                {
                    throw ParseError(fileName, content, i, "Found range end with no matching start.");
                }

                int last = openRanges.Count - 1;
                OpenRange start = openRanges[last];
                openRanges.RemoveAt(last);
                ranges.Add(new RawRange(fileName, start.Position, output.Length, start.Name ?? start.MarkerName));
                i += 2;
                continue;
            }

            if (StartsWith(content, i, "{|"))
            {
                int end = content.IndexOf("|}", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw ParseError(fileName, content, i, "Unterminated marker.");
                }

                FourslashMarker marker = ParseObjectMarker(fileName, content, i, end, output.Length);
                markers.Add(marker);
                AttachMarkerToOpenRange(openRanges, marker.Name);
                i = end + 2;
                continue;
            }

            if (StartsWith(content, i, "/*"))
            {
                int end = content.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    string remainder = content[(i + 2)..];
                    if (IsMarkerName(remainder))
                    {
                        throw ParseError(fileName, content, i, "Unterminated marker.");
                    }

                    output.Append(content, i, content.Length - i);
                    break;
                }

                string markerText = content[(i + 2)..end].Trim();
                if (!IsMarkerName(markerText))
                {
                    output.Append(content, i, end + 2 - i);
                    i = end + 2;
                    continue;
                }

                string? markerName = markerText.Length == 0 ? null : markerText;
                var marker = new FourslashMarker(markerName, fileName, output.Length, null);
                markers.Add(marker);
                AttachMarkerToOpenRange(openRanges, markerName);
                i = end + 2;
                continue;
            }

            output.Append(content[i]);
            i++;
        }

        if (openRanges.Count != 0)
        {
            throw ParseError(fileName, content, content.Length, "Unterminated range.");
        }

        // Stable ordering from the upstream parser: start ascending, outer range first for ties.
        ranges.Sort(static (left, right) =>
        {
            int result = left.Start.CompareTo(right.Start);
            return result != 0 ? result : right.End.CompareTo(left.End);
        });

        return new ParsedFile(new FourslashFile(fileName, output.ToString()), markers, ranges);
    }

    private static FourslashMarker ParseObjectMarker(
        string fileName,
        string content,
        int start,
        int end,
        int position)
    {
        string markerText = content[(start + 2)..end].Trim();
        try
        {
            using JsonDocument document = JsonDocument.Parse("{" + markerText + "}");
            JsonElement root = document.RootElement;
            var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                data[property.Name] = property.Value.Clone();
            }

            if (data.Count == 0)
            {
                throw ParseError(fileName, content, start, "Object markers can not be empty.");
            }

            string? name = null;
            if (data.TryGetValue("name", out JsonElement nameValue) &&
                nameValue.ValueKind == JsonValueKind.String)
            {
                string? value = nameValue.GetString();
                if (value is not null && value.Length != 0)
                {
                    name = value;
                }
            }

            return new FourslashMarker(name, fileName, position, data);
        }
        catch (JsonException)
        {
            throw ParseError(fileName, content, start, "Unable to parse marker text " + markerText);
        }
    }

    private static void AttachMarkerToOpenRange(List<OpenRange> openRanges, string? markerName)
    {
        if (openRanges.Count == 0 || markerName is null)
        {
            return;
        }

        openRanges[^1].MarkerName = markerName;
    }

    // Task-specific extension: [|rangeName: selected text|]. Requiring whitespace after the
    // colon prevents ordinary TypeScript properties such as [|name:string|] from being altered.
    private static string? TryReadRangeName(string content, ref int position)
    {
        int nameStart = position;
        if (nameStart >= content.Length || !IsMarkerNameStart(content[nameStart]))
        {
            return null;
        }

        int cursor = nameStart + 1;
        while (cursor < content.Length && IsMarkerNameCharacter(content[cursor]))
        {
            cursor++;
        }

        if (cursor >= content.Length || content[cursor] != ':' ||
            cursor + 1 >= content.Length || (content[cursor + 1] != ' ' && content[cursor + 1] != '\t'))
        {
            return null;
        }

        string name = content[nameStart..cursor];
        cursor++;
        while (cursor < content.Length && (content[cursor] == ' ' || content[cursor] == '\t'))
        {
            cursor++;
        }
        position = cursor;
        return name;
    }

    private static bool TryParseDirective(string line, out string name, out string value)
    {
        string candidate = line.TrimStart();
        if (!candidate.StartsWith("//", StringComparison.Ordinal))
        {
            name = "";
            value = "";
            return false;
        }

        candidate = candidate[2..].TrimStart();
        if (candidate.Length < 3 || candidate[0] != '@')
        {
            name = "";
            value = "";
            return false;
        }

        int colon = candidate.IndexOf(':');
        if (colon <= 1)
        {
            name = "";
            value = "";
            return false;
        }

        string rawName = candidate[1..colon].Trim();
        if (!IsOptionName(rawName))
        {
            name = "";
            value = "";
            return false;
        }

        name = AsciiToLower(rawName);
        value = candidate[(colon + 1)..].Trim();
        return true;
    }

    private static string NormalizeFileName(string fileName)
    {
        return global::Ts.TsPath.TsPath.GetNormalizedAbsolutePath(fileName, "/");
    }

    private static string ChompLeadingSpace(string content)
    {
        string[] lines = content.Split('\n');
        foreach (string line in lines)
        {
            if (line.Length != 0 && line[0] != ' ')
            {
                return content;
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length != 0)
            {
                lines[i] = lines[i][1..];
            }
        }
        return string.Join('\n', lines);
    }

    private static bool StartsWith(string text, int position, string value)
    {
        return position + value.Length <= text.Length &&
            text.AsSpan(position, value.Length).SequenceEqual(value.AsSpan());
    }

    private static bool IsMarkerName(string value)
    {
        foreach (char ch in value)
        {
            if (!IsMarkerNameCharacter(ch))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsMarkerNameStart(char ch)
    {
        return ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '$' or '_';
    }

    private static bool IsMarkerNameCharacter(char ch)
    {
        return IsMarkerNameStart(ch) || ch is >= '0' and <= '9';
    }

    private static bool IsOptionName(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }
        foreach (char ch in value)
        {
            if (!(IsMarkerNameCharacter(ch)))
            {
                return false;
            }
        }
        return true;
    }

    private static string AsciiToLower(string value)
    {
        return string.Create(value.Length, value, static (destination, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char ch = source[i];
                destination[i] = ch is >= 'A' and <= 'Z' ? (char)(ch + ('a' - 'A')) : ch;
            }
        });
    }

    private static InvalidOperationException ParseError(
        string fileName,
        string content,
        int position,
        string message)
    {
        int line = 1;
        int column = 1;
        int limit = Math.Min(position, content.Length);
        for (int i = 0; i < limit; i++)
        {
            if (content[i] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }
        return new InvalidOperationException($"{fileName} ({line},{column}): {message}");
    }

    private sealed record ParsedFile(
        FourslashFile File,
        List<FourslashMarker> Markers,
        List<RawRange> Ranges);

    private sealed class OpenRange(int position, string? name)
    {
        public int Position { get; } = position;
        public string? Name { get; } = name;
        public string? MarkerName { get; set; }
    }

    private sealed record RawRange(string FileName, int Start, int End, string? Name);
}
