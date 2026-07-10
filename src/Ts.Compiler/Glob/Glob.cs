using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Ts.Glob;

// Go: internal/glob/glob.go — an LSP-compliant glob pattern, as defined by the spec:
// https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#documentFilter
//
// Upstream marks this implementation as testing-oriented (production wildcard matching
// lives in vfsmatch). Matching here operates on UTF-16 code units where Go used bytes;
// character classes decode a full code point on both sides, so observable behavior is
// unchanged for valid inputs.
//
// Registered deviations (11-progress.md §8):
//  - Go parity: a '[!…]' range's negate flag is parsed but never consulted in match
//    (glob.go:294-301) — preserved as-is.
//  - glob.go:233 indexes input[0] after consuming trailing slashes and panics when a
//    pattern ending in '/' exactly exhausts the input; the loop here is guarded instead,
//    so "a/" matches "a/" rather than crashing.
public sealed class Glob
{
    private readonly List<Element> _elems = new();

    private Glob()
    {
    }

    // Parse builds a Glob for the given pattern; invalid patterns throw FormatException
    // (Go returned error — recoverable-parse case per 05-conventions.md §4).
    public static Glob Parse(string pattern)
    {
        (Glob g, _) = ParseCore(pattern, nested: false);
        return g;
    }

    private static (Glob Glob, string Remaining) ParseCore(string pattern, bool nested)
    {
        var g = new Glob();
        while (pattern.Length > 0)
        {
            switch (pattern[0])
            {
                case '/':
                    pattern = pattern[1..];
                    g._elems.Add(Slash.Instance);
                    break;

                case '*':
                    if (pattern.Length > 1 && pattern[1] == '*')
                    {
                        if ((g._elems.Count > 0 && g._elems[^1] is not Slash) ||
                            (pattern.Length > 2 && pattern[2] != '/'))
                        {
                            throw new FormatException("** may only be adjacent to '/'");
                        }
                        pattern = pattern[2..];
                        g._elems.Add(StarStar.Instance);
                        break;
                    }
                    pattern = pattern[1..];
                    g._elems.Add(Star.Instance);
                    break;

                case '?':
                    pattern = pattern[1..];
                    g._elems.Add(AnyChar.Instance);
                    break;

                case '{':
                    var members = new List<Glob>();
                    while (pattern[0] != '}')
                    {
                        pattern = pattern[1..];
                        (Glob member, string rest) = ParseCore(pattern, nested: true);
                        if (rest.Length == 0)
                        {
                            throw new FormatException("unmatched '{'");
                        }
                        pattern = rest;
                        members.Add(member);
                    }
                    pattern = pattern[1..];
                    g._elems.Add(new Group(members));
                    break;

                case '}':
                case ',':
                    if (nested)
                    {
                        return (g, pattern);
                    }
                    pattern = g.ParseLiteral(pattern, nested: false);
                    break;

                case '[':
                    pattern = pattern[1..];
                    if (pattern.Length == 0)
                    {
                        throw BadRange();
                    }
                    bool negate = false;
                    if (pattern[0] == '!')
                    {
                        pattern = pattern[1..];
                        negate = true;
                    }
                    (int low, int lowSize) = ReadRangeRune(pattern);
                    pattern = pattern[lowSize..];
                    if (pattern.Length == 0 || pattern[0] != '-')
                    {
                        throw BadRange();
                    }
                    pattern = pattern[1..];
                    (int high, int highSize) = ReadRangeRune(pattern);
                    pattern = pattern[highSize..];
                    if (pattern.Length == 0 || pattern[0] != ']')
                    {
                        throw BadRange();
                    }
                    pattern = pattern[1..];
                    g._elems.Add(new CharRange(negate, low, high));
                    break;

                default:
                    pattern = g.ParseLiteral(pattern, nested);
                    break;
            }
        }
        return (g, "");
    }

    // Helper for decoding a code point in range elements, e.g. [a-z].
    private static (int Rune, int Size) ReadRangeRune(string input)
    {
        if (input.Length == 0)
        {
            throw BadRange();
        }
        if (System.Text.Rune.DecodeFromUtf16(input, out var rune, out int consumed) != System.Buffers.OperationStatus.Done)
        {
            throw new FormatException("invalid UTF-16 encoding");
        }
        return (rune.Value, consumed);
    }

    private static FormatException BadRange() => new("'[' patterns must be of the form [x-y]");

    private string ParseLiteral(string pattern, bool nested)
    {
        string specialChars = nested ? "*?{[/}," : "*?{[/";
        int end = pattern.AsSpan().IndexOfAny(specialChars);
        if (end == -1)
        {
            end = pattern.Length;
        }
        _elems.Add(new Literal(pattern[..end]));
        return pattern[end..];
    }

    public override string ToString()
    {
        var b = new StringBuilder();
        foreach (Element e in _elems)
        {
            b.Append(e);
        }
        return b.ToString();
    }

    // Match reports whether the input string matches the glob pattern.
    public bool Match(string input) => MatchCore(CollectionsMarshal.AsSpan(_elems), input.AsSpan());

    private static bool MatchCore(ReadOnlySpan<Element> elems, ReadOnlySpan<char> input)
    {
        while (elems.Length > 0)
        {
            Element elem = elems[0];
            elems = elems[1..];
            switch (elem)
            {
                case Slash:
                    if (input.Length == 0 || input[0] != '/')
                    {
                        return false;
                    }
                    while (input.Length > 0 && input[0] == '/')
                    {
                        input = input[1..];
                    }
                    break;

                case StarStar:
                    // Special cases:
                    //  - **/a matches "a"
                    //  - **/ matches everything
                    //
                    // Note that if ** is followed by anything, it must be '/' (this is
                    // enforced by Parse).
                    if (elems.Length > 0)
                    {
                        elems = elems[1..];
                    }

                    // A trailing ** matches anything.
                    if (elems.Length == 0)
                    {
                        return true;
                    }

                    // Backtracking: advance pattern segments until the remaining pattern
                    // elements match.
                    while (input.Length != 0)
                    {
                        if (MatchCore(elems, input))
                        {
                            return true;
                        }
                        _ = Split(input, out input);
                    }
                    return false;

                case Literal literal:
                    if (!input.StartsWith(literal.Value.AsSpan(), StringComparison.Ordinal))
                    {
                        return false;
                    }
                    input = input[literal.Value.Length..];
                    break;

                case Star:
                {
                    ReadOnlySpan<char> segInput = Split(input, out ReadOnlySpan<char> restInput);
                    input = restInput;

                    int elemEnd = elems.Length;
                    for (int i = 0; i < elems.Length; i++)
                    {
                        if (elems[i] is Slash)
                        {
                            elemEnd = i;
                            break;
                        }
                    }
                    ReadOnlySpan<Element> segElems = elems[..elemEnd];
                    elems = elems[elemEnd..];

                    // A trailing * matches the entire segment.
                    if (segElems.Length == 0)
                    {
                        break;
                    }

                    // Backtracking: advance characters until remaining subpattern elements
                    // match.
                    bool matched = false;
                    for (int i = 0; i < segInput.Length; i++)
                    {
                        if (MatchCore(segElems, segInput[i..]))
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        return false;
                    }
                    break;
                }

                case AnyChar:
                    if (input.Length == 0 || input[0] == '/')
                    {
                        return false;
                    }
                    input = input[1..];
                    break;

                case Group group:
                {
                    // Append remaining pattern elements to each group member looking for a
                    // match.
                    foreach (Glob m in group.Members)
                    {
                        var branch = new Element[m._elems.Count + elems.Length];
                        m._elems.CopyTo(branch, 0);
                        elems.CopyTo(branch.AsSpan(m._elems.Count));
                        if (MatchCore(branch, input))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                case CharRange range:
                {
                    if (input.Length == 0 || input[0] == '/')
                    {
                        return false;
                    }
                    if (System.Text.Rune.DecodeFromUtf16(input, out var c, out int size) != System.Buffers.OperationStatus.Done)
                    {
                        return false;
                    }
                    // Go parity: range.Negate is parsed but never consulted here (glob.go:294-301).
                    if (c.Value < range.Low || c.Value > range.High)
                    {
                        return false;
                    }
                    input = input[size..];
                    break;
                }

                default:
                    throw new InvalidOperationException($"segment type {elem.GetType()} not implemented");
            }
        }

        return input.Length == 0;
    }

    // Split returns the portion before the first slash (or sequence of consecutive
    // slashes) and, via rest, the portion after it. If there is no slash, rest is empty.
    private static ReadOnlySpan<char> Split(ReadOnlySpan<char> input, out ReadOnlySpan<char> rest)
    {
        int i = input.IndexOf('/');
        if (i < 0)
        {
            rest = ReadOnlySpan<char>.Empty;
            return input;
        }
        ReadOnlySpan<char> first = input[..i];
        for (int j = i; j < input.Length; j++)
        {
            if (input[j] != '/')
            {
                rest = input[j..];
                return first;
            }
        }
        rest = ReadOnlySpan<char>.Empty;
        return first;
    }

    // Element types (Go: element fmt.Stringer union).
    private abstract class Element
    {
    }

    private sealed class Slash : Element // One or more '/' separators
    {
        public static readonly Slash Instance = new();

        public override string ToString() => "/";
    }

    private sealed class Literal(string value) : Element // string literal, not containing /, *, ?, {}, or []
    {
        public string Value { get; } = value;

        public override string ToString() => Value;
    }

    private sealed class Star : Element // *
    {
        public static readonly Star Instance = new();

        public override string ToString() => "*";
    }

    private sealed class AnyChar : Element // ?
    {
        public static readonly AnyChar Instance = new();

        public override string ToString() => "?";
    }

    private sealed class StarStar : Element // **
    {
        public static readonly StarStar Instance = new();

        public override string ToString() => "**";
    }

    private sealed class Group(List<Glob> members) : Element // {foo, bar, ...} grouping
    {
        public List<Glob> Members { get; } = members;

        public override string ToString()
        {
            var parts = new List<string>();
            foreach (Glob g in Members)
            {
                parts.Add(g.ToString());
            }
            return "{" + string.Join(",", parts) + "}";
        }
    }

    private sealed class CharRange(bool negate, int low, int high) : Element // [a-z] character range
    {
        public bool Negate { get; } = negate;

        public int Low { get; } = low;

        public int High { get; } = high;

        public override string ToString() => "[" + char.ConvertFromUtf32(Low) + "-" + char.ConvertFromUtf32(High) + "]";
    }
}
