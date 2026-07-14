// Ported from tsgo internal/fourslash/test_parser.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7).

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Ts.TestUtil.Fourslash;

// The source text for one virtual file after fourslash metacharacters have been removed.
public sealed record FourslashFile(string VirtualFileName, string Content);

// A raw UTF-16 code-unit position. C# string offsets already use the position encoding
// required by the language-service APIs and by LSP's negotiated utf-16 encoding.
public sealed record FourslashMarkerPosition(string FileName, int Position);

public sealed record FourslashMarker(
    string? Name,
    string FileName,
    int Position,
    IReadOnlyDictionary<string, JsonElement>? Data);

public sealed record FourslashRange(
    int Index,
    string? Name,
    string FileName,
    int Start,
    int End);

public sealed class FourslashProject
{
    internal FourslashProject(
        IReadOnlyList<FourslashFile> files,
        IReadOnlyDictionary<string, FourslashMarkerPosition> markerPositions,
        IReadOnlyList<FourslashMarker> markers,
        IReadOnlyList<FourslashRange> ranges,
        IReadOnlyDictionary<string, FourslashRange> namedRanges,
        IReadOnlyDictionary<string, string> options)
    {
        Files = files;
        MarkerPositions = markerPositions;
        Markers = markers;
        Ranges = ranges;
        NamedRanges = namedRanges;
        Options = options;
    }

    public IReadOnlyList<FourslashFile> Files { get; }

    // Named slash-star markers and named object markers. Anonymous markers remain available
    // through Markers; GetMarker also accepts "" when exactly one anonymous marker exists.
    public IReadOnlyDictionary<string, FourslashMarkerPosition> MarkerPositions { get; }

    public IReadOnlyList<FourslashMarker> Markers { get; }

    // Ranges are indexed in file order, then by start ascending/end descending within a file.
    public IReadOnlyList<FourslashRange> Ranges { get; }

    public IReadOnlyDictionary<string, FourslashRange> NamedRanges { get; }

    // Directive names are ASCII-lowercased, matching the TypeScript and tsgo harnesses.
    public IReadOnlyDictionary<string, string> Options { get; }

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
