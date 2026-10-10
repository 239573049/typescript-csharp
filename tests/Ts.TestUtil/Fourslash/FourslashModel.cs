// Ported from tsgo internal/fourslash/test_parser.go:90-216 — TestData, TestFileInfo and the
// Marker/RangeMarker raw-position records (commit 168e7015edf98244febc8f4ae450b673b5d195d7).
//
// PORT-NOTE(currency): Go stores byte offsets (UTF-8 world); this port stores UTF-16 code units
// natively (plan/01 §UTF-16) — the whole codebase reads `position` as a C# string offset, so no
// PositionMap is carried. The lift from raw ints to LS positions happens on the runner side
// (FourslashMarkers.FromProject), because the line-map/converters types live in
// Ts.LanguageService and Ts.TestUtil must not depend on it (see Fourslash.cs header).
//
// PORT-NOTE(names): Go's `TestFileInfo`/`TestData` become FourslashFile/FourslashProject to keep
// the C# harness types greppable next to the fourslash session that consumes them; field order
// and meaning follow the Go structs.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ts.TestUtil.Fourslash;

// test_parser.go:199-204 — TestFileInfo: one virtual file after markers are stripped.
// `Emit` carries the `// @emitThisFile: true` file directive (test_parser.go:421).
public sealed record FourslashFile(
    string VirtualFileName,
    string Content,
    bool Emit = false);

// A raw UTF-16 code-unit position of a named marker, as exposed by FourslashProject.MarkerPositions.
public sealed record FourslashMarkerPosition(string FileName, int Position);

// test_parser.go:54-60 — Marker (raw parse output; Name is null for anonymous markers).
public sealed record FourslashMarker(
    string? Name,
    string FileName,
    int Position,
    IReadOnlyDictionary<string, JsonElement>? Data);

// test_parser.go:25-30 — RangeMarker raw half: the stripped-content span plus the marker
// comment captured inside `[| ... |]` (null when the range holds no marker). Go keeps a
// *Marker pointer; the raw record carries the marker's index into FourslashProject.Markers
// (resolved back to the Marker object by the runner-side lift) so anonymous object markers
// keep their range linkage too.
public sealed record FourslashRange(
    int Index,
    string? Name,
    string FileName,
    int Start,
    int End,
    int? MarkerIndex = null);

// test_parser.go:90-97 — TestData.
public sealed class FourslashProject
{
    // PUBLIC because the runner partials are compiled into Ts.Fourslash.Tests, which references
    // this assembly and does not link this file (see the test csproj) — Go's TestData literal
    // construction in NewFourslash (fourslash.go:145) needs the constructor visible there.
    public FourslashProject(
        IReadOnlyList<FourslashFile> files,
        IReadOnlyDictionary<string, FourslashMarkerPosition> markerPositions,
        IReadOnlyList<FourslashMarker> markers,
        IReadOnlyList<FourslashRange> ranges,
        IReadOnlyDictionary<string, FourslashRange> namedRanges,
        IReadOnlyDictionary<string, string> globalOptions,
        IReadOnlyDictionary<string, string> symlinks)
    {
        Files = files;
        MarkerPositions = markerPositions;
        Markers = markers;
        Ranges = ranges;
        NamedRanges = namedRanges;
        GlobalOptions = globalOptions;
        Symlinks = symlinks;
    }

    public IReadOnlyList<FourslashFile> Files { get; }

    // test_parser.go:92 — MarkerPositions (named markers only).
    public IReadOnlyDictionary<string, FourslashMarkerPosition> MarkerPositions { get; }

    public IReadOnlyList<FourslashMarker> Markers { get; }

    // test_parser.go:96 — Ranges, ordered by start ascending / end descending within a file
    // (the parseFileContent SortStableFunc order).
    public IReadOnlyList<FourslashRange> Ranges { get; }

    // Task-specific convenience index over Ranges by marker-derived name (Go reaches ranges by
    // slice position or GetRangesByText instead).
    public IReadOnlyDictionary<string, FourslashRange> NamedRanges { get; }

    // test_parser.go:95 — GlobalOptions.
    public IReadOnlyDictionary<string, string> GlobalOptions { get; }

    // test_parser.go:94 — Symlinks (link target -> current file).
    public IReadOnlyDictionary<string, string> Symlinks { get; }

    // test_parser.go:99-101 — isStateBaseliningEnabled.
    public bool IsStateBaseliningEnabled =>
        GlobalOptions.TryGetValue("statebaseline", out string? value) &&
        string.Equals(value, "true", StringComparison.Ordinal);

    // MarkerPositions lookup with Go's t.Fatalf paths turned into exceptions. The empty name
    // resolves the single anonymous marker, matching classic fourslash `verify.n(r"")` usage.
    public FourslashMarkerPosition GetMarker(string name)
    {
        if (MarkerPositions.TryGetValue(name, out FourslashMarkerPosition? position))
        {
            return position;
        }

        if (name.Length == 0)
        {
            FourslashMarker? anonymous = null;
            foreach (FourslashMarker marker in Markers)
            {
                if (marker.Name is not null)
                {
                    continue;
                }

                if (anonymous is not null)
                {
                    throw new InvalidOperationException("The fourslash project has more than one anonymous marker.");
                }
                anonymous = marker;
            }

            if (anonymous is not null)
            {
                return new FourslashMarkerPosition(anonymous.FileName, anonymous.Position);
            }
        }

        throw new KeyNotFoundException("Fourslash marker was not found: " + name);
    }

    public FourslashRange GetRange(int index)
    {
        if ((uint)index >= (uint)Ranges.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        return Ranges[index];
    }

    public FourslashRange GetRange(string name)
    {
        if (NamedRanges.TryGetValue(name, out FourslashRange? range))
        {
            return range;
        }
        throw new KeyNotFoundException("Fourslash range was not found: " + name);
    }
}
