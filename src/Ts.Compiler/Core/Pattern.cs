// Ported from tsgo internal/core/pattern.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.Core;

// PORT-NOTE: fields surfaced as get-only properties so that default(Pattern)
// behaves like Go's zero Pattern{} — Text reads "" (never null), StarIndex 0
// (05 §9.3 zero-value contract; FindBestPatternMatch returns the zero value).
public readonly struct Pattern
{
    private readonly string? text;
    private readonly int starIndex;

    public Pattern(string text, int starIndex)
    {
        this.text = text;
        this.starIndex = starIndex;
    }

    public string Text => text ?? "";

    public int StarIndex => starIndex; // -1 for exact match

    public bool IsValid => StarIndex == -1 || StarIndex < Text.Length;

    public bool Matches(string candidate)
    {
        if (StarIndex == -1)
        {
            return Text == candidate;
        }
        return candidate.Length >= Text.Length - 1 &&
            candidate.AsSpan().StartsWith(Text.AsSpan(0, StarIndex), StringComparison.Ordinal) &&
            candidate.AsSpan().EndsWith(Text.AsSpan(StarIndex + 1), StringComparison.Ordinal);
    }

    public string MatchedText(string candidate)
    {
        if (!Matches(candidate))
        {
            throw new InvalidOperationException("candidate does not match pattern");
        }
        if (StarIndex == -1)
        {
            return "";
        }
        // Go: candidate[p.StarIndex : len(candidate)-len(p.Text)+p.StarIndex+1]
        return candidate.Substring(StarIndex, candidate.Length - Text.Length + 1);
    }
}

public static partial class Core
{
    public static Pattern TryParsePattern(string pattern)
    {
        int starIndex = pattern.IndexOf('*'); // Go: strings.Index(pattern, "*") — char IndexOf is ordinal
        if (starIndex == -1 || !pattern.AsSpan(starIndex + 1).Contains('*'))
        {
            return new Pattern(pattern, starIndex);
        }
        return default; // Go: Pattern{}
    }

    public static T? FindBestPatternMatch<T>(T[]? values, Func<T, Pattern> getPattern, string candidate)
    {
        T? bestPattern = default;
        int longestMatchPrefixLength = -1;
        foreach (T value in values ?? [])
        {
            Pattern pattern = getPattern(value);
            if ((pattern.StarIndex == -1 || pattern.StarIndex > longestMatchPrefixLength) && pattern.Matches(candidate))
            {
                bestPattern = value;
                longestMatchPrefixLength = pattern.StarIndex;
            }
        }
        return bestPattern;
    }
}
