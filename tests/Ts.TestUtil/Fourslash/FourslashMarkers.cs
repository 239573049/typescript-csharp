// Ported from tsgo internal/fourslash/test_parser.go:25-107 — the mutable Marker/RangeMarker
// model the fourslash state machine edits in place (fourslash.go:editScriptAndUpdateMarkersWorker
// rewrites Position/LSPosition/Range/LSRange on every edit).
//
// The FourslashProject records (FourslashModel.cs) are the immutable parse output; the session
// lifts them into these handles once, computing LS positions with the UTF-16 converters the C#
// LS foundation speaks (see Fourslash.cs header for the position-currency note).

#if TS_FOURSLASH_RUNNER

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ts.TestUtil.Fourslash;

// test_parser.go:84-88 — MarkerOrRange.
public interface IFourslashMarkerOrRange
{
    string FileName { get; }

    global::Ts.LsProto.Position LSPos();

    string? GetName();
}

// test_parser.go:54-82 — Marker.
public sealed class Marker : IFourslashMarkerOrRange
{
    public Marker(string? name, string fileName, int position, IReadOnlyDictionary<string, JsonElement>? data)
    {
        Name = name;
        FileName = fileName;
        Position = position;
        Data = data;
    }

    public string FileName { get; }

    public int Position { get; set; }

    public global::Ts.LsProto.Position LSPosition { get; set; } = new();

    // `null` for anonymous markers such as `{| "foo": "bar" |}`.
    public string? Name { get; }

    public IReadOnlyDictionary<string, JsonElement>? Data { get; }

    public global::Ts.LsProto.Position LSPos() => LSPosition;

    public string? GetName() => Name;

    // test_parser.go:74-82 — markerWithSymlink (Go's lowercase method name reads as a typo in
    // the source; kept under the PascalCase of the same words).
    public Marker MakerWithSymlink(string fileName)
    {
        return new Marker(Name, fileName, Position, Data)
        {
            LSPosition = LSPosition,
        };
    }
}

// test_parser.go:25-52 — RangeMarker.
public sealed class RangeMarker : IFourslashMarkerOrRange
{
    public RangeMarker(string fileName, global::Ts.Core.TextRange range, Marker? marker)
    {
        FileName = fileName;
        Range = range;
        Marker = marker;
    }

    public string FileName { get; }

    public global::Ts.Core.TextRange Range { get; set; }

    public global::Ts.LsProto.Range LSRange { get; set; } = new();

    public Marker? Marker { get; }

    public global::Ts.LsProto.Position LSPos() => LSRange.Start;

    public string? GetName() => Marker?.Name;

    // test_parser.go:47-52 — LSLocation.
    public global::Ts.LsProto.Location LSLocation() => new()
    {
        Uri = global::Ts.Ls.LsConv.Converters.FileNameToDocumentURI(FileName),
        Range = LSRange,
    };
}

// Lifts the immutable parse output into the mutable session model, computing the LS
// positions/ranges exactly like Go's parseFileContent tail (test_parser.go:414-444):
// line map over the stripped content, then position -> line/character for every marker
// and both ends of every range. `getFileText` resolves the stripped content of a file
// (the session passes its script-info table; the parse output's FourslashFile.Content).
public static class FourslashMarkers
{
    public static (List<Marker> Markers, List<RangeMarker> Ranges, Dictionary<string, Marker> MarkerPositions) FromProject(
        FourslashProject project,
        Func<string, string> getFileText)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(getFileText);

        var lineMaps = new Dictionary<string, global::Ts.Ls.LsConv.LSPLineMap>(StringComparer.Ordinal);
        var converters = new global::Ts.Ls.LsConv.Converters("utf-16", fileName =>
            lineMaps.TryGetValue(fileName, out global::Ts.Ls.LsConv.LSPLineMap? lineMap)
                ? lineMap
                : global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(""));

        var scripts = new Dictionary<string, global::Ts.Ls.LsConv.Script>(StringComparer.Ordinal);
        global::Ts.Ls.LsConv.Script ScriptFor(string fileName)
        {
            if (!scripts.TryGetValue(fileName, out global::Ts.Ls.LsConv.Script? script))
            {
                script = new FourslashScript(fileName, getFileText(fileName));
                scripts[fileName] = script;
            }
            return script;
        }

        foreach (FourslashFile file in project.Files)
        {
            lineMaps[file.VirtualFileName] = global::Ts.Ls.LsConv.LineMap.ComputeLSPLineStarts(getFileText(file.VirtualFileName));
        }

        var markers = new List<Marker>(project.Markers.Count);
        foreach (FourslashMarker parsed in project.Markers)
        {
            var marker = new Marker(parsed.Name, parsed.FileName, parsed.Position, parsed.Data)
            {
                LSPosition = converters.PositionToLineAndCharacter(ScriptFor(parsed.FileName), parsed.Position),
            };
            markers.Add(marker);
        }

        var markersByIndex = new Dictionary<int, Marker>(markers.Count);
        for (int i = 0; i < markers.Count; i++)
        {
            markersByIndex[i] = markers[i];
        }

        var ranges = new List<RangeMarker>(project.Ranges.Count);
        foreach (FourslashRange parsed in project.Ranges)
        {
            var script = ScriptFor(parsed.FileName);
            var rangeMarker = new RangeMarker(
                parsed.FileName,
                new global::Ts.Core.TextRange(parsed.Start, parsed.End),
                parsed.MarkerIndex is { } markerIndex ? markersByIndex.GetValueOrDefault(markerIndex) : null)
            {
                LSRange = new global::Ts.LsProto.Range
                {
                    Start = converters.PositionToLineAndCharacter(script, parsed.Start),
                    End = converters.PositionToLineAndCharacter(script, parsed.End),
                },
            };
            ranges.Add(rangeMarker);
        }

        var markerPositions = new Dictionary<string, Marker>(StringComparer.Ordinal);
        foreach (Marker marker in markers)
        {
            if (marker.Name is not null && !markerPositions.TryAdd(marker.Name, marker))
            {
                throw new InvalidOperationException("Duplicate fourslash marker name: " + marker.Name);
            }
        }

        return (markers, ranges, markerPositions);
    }

    private sealed class FourslashScript(string fileName, string text) : global::Ts.Ls.LsConv.Script
    {
        public string FileName() => fileName;

        public string Text() => text;
    }
}

#endif
