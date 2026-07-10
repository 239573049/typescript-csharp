// Ported from tsgo internal/semver/version_range.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Ts.Semver;

public struct VersionRange
{
    internal VersionComparator[][]? alternatives;

    // Go: String()
    public readonly override string ToString()
    {
        var sb = new StringBuilder();
        Semver.FormatDisjunction(sb, alternatives);
        return sb.ToString();
    }

    public readonly bool Test(Version version)
    {
        return Semver.TestDisjunction(alternatives, version);
    }
}

// Go: versionComparator{operator comparatorOperator; operand Version}.
// PORT-NOTE: `operator` is a C# keyword; spelled `@operator` to keep the Go name greppable.
internal struct VersionComparator
{
    internal string @operator;
    internal Version operand;

    internal VersionComparator(string @operator, Version operand)
    {
        this.@operator = @operator;
        this.operand = operand;
    }
}

// Go: partialVersion
internal struct PartialVersion
{
    internal Version version;
    internal string majorStr;
    internal string minorStr;
    internal string patchStr;
}

public static partial class Semver
{
    // See Version.cs for the regexp translation notes (R08-A3): `(?i)` →
    // IgnoreCase | CultureInvariant, `\d` → `[0-9]`, `$` → `\z`; additionally Go RE2's
    // Perl class `\s` is ASCII `[\t\n\f\r ]` while .NET `\s` is Unicode (and includes
    // `\v`), so `\s` is spelled out as `[\t\n\f\r ]` below.

    // https://github.com/npm/node-semver#range-grammar
    //
    // range-set    ::= range ( logical-or range ) *
    // range        ::= hyphen | simple ( ' ' simple ) * | ''
    // logical-or   ::= ( ' ' ) * '||' ( ' ' ) *
    //
    // Go: `\|\|`
    [GeneratedRegex(@"\|\|")]
    internal static partial Regex LogicalOrRegExp();

    // Go: `\s+`
    [GeneratedRegex(@"[\t\n\f\r ]+")]
    internal static partial Regex WhitespaceRegExp();

    // https://github.com/npm/node-semver#range-grammar
    //
    // partial      ::= xr ( '.' xr ( '.' xr qualifier ? )? )?
    // xr           ::= 'x' | 'X' | '*' | nr
    // nr           ::= '0' | ['1'-'9'] ( ['0'-'9'] ) *
    // qualifier    ::= ( '-' pre )? ( '+' build )?
    // pre          ::= parts
    // build        ::= parts
    // parts        ::= part ( '.' part ) *
    // part         ::= nr | [-0-9A-Za-z]+
    //
    // Go: `(?i)^([x*0]|[1-9]\d*)(?:\.([x*0]|[1-9]\d*)(?:\.([x*0]|[1-9]\d*)(?:-([a-z0-9-.]+))?(?:\+([a-z0-9-.]+))?)?)?$`
    [GeneratedRegex(@"^([x*0]|[1-9][0-9]*)(?:\.([x*0]|[1-9][0-9]*)(?:\.([x*0]|[1-9][0-9]*)(?:-([a-z0-9-.]+))?(?:\+([a-z0-9-.]+))?)?)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PartialRegExp();

    // https://github.com/npm/node-semver#range-grammar
    //
    // hyphen       ::= partial ' - ' partial
    //
    // Go: `(?i)^\s*([a-z0-9-+.*]+)\s+-\s+([a-z0-9-+.*]+)\s*$`
    [GeneratedRegex(@"^[\t\n\f\r ]*([a-z0-9-+.*]+)[\t\n\f\r ]+-[\t\n\f\r ]+([a-z0-9-+.*]+)[\t\n\f\r ]*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex HyphenRegExp();

    // https://github.com/npm/node-semver#range-grammar
    //
    // simple       ::= primitive | partial | tilde | caret
    // primitive    ::= ( '<' | '>' | '>=' | '<=' | '=' ) partial
    // tilde        ::= '~' partial
    // caret        ::= '^' partial
    //
    // Go: `(?i)^([~^<>=]|<=|>=)?\s*([a-z0-9-+.*]+)$`
    [GeneratedRegex(@"^([~^<>=]|<=|>=)?[\t\n\f\r ]*([a-z0-9-+.*]+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex RangeRegExp();

    // Go: type comparatorOperator string — a named string type with no methods, so the
    // type itself disappears (05 §0.4); the constants keep the Go names and values.
    internal const string RangeLessThan = "<";
    internal const string RangeLessThanEqual = "<=";
    internal const string RangeEqual = "=";
    internal const string RangeGreaterThanEqual = ">=";
    internal const string RangeGreaterThan = ">";

    internal static void FormatDisjunction(StringBuilder sb, VersionComparator[][]? alternatives)
    {
        int origLen = sb.Length;

        VersionComparator[][] alts = alternatives ?? []; // Go: range over nil slice
        for (int i = 0; i < alts.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(" || ");
            }
            FormatAlternative(sb, alts[i]);
        }

        if (sb.Length == origLen)
        {
            sb.Append('*');
        }
    }

    private static void FormatAlternative(StringBuilder sb, VersionComparator[] comparators)
    {
        for (int i = 0; i < comparators.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            FormatComparator(sb, comparators[i]);
        }
    }

    private static void FormatComparator(StringBuilder sb, VersionComparator comparator)
    {
        sb.Append(comparator.@operator);
        sb.Append(comparator.operand.ToString());
    }

    internal static bool TestDisjunction(VersionComparator[][]? alternatives, Version version)
    {
        // an empty disjunction is treated as "*" (all versions)
        if (alternatives is null || alternatives.Length == 0)
        {
            return true;
        }

        foreach (VersionComparator[] alternative in alternatives)
        {
            if (TestAlternative(alternative, version))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TestAlternative(VersionComparator[] alternative, Version version)
    {
        foreach (VersionComparator comparator in alternative)
        {
            if (!TestComparator(comparator, version))
            {
                return false;
            }
        }
        return true;
    }

    private static bool TestComparator(VersionComparator comparator, Version version)
    {
        int cmp = version.Compare(comparator.operand);
        switch (comparator.@operator)
        {
            case RangeLessThan:
                return cmp < 0;
            case RangeLessThanEqual:
                return cmp <= 0;
            case RangeEqual:
                return cmp == 0;
            case RangeGreaterThanEqual:
                return cmp >= 0;
            case RangeGreaterThan:
                return cmp > 0;
            default:
                throw new InvalidOperationException("Unexpected operator: " + comparator.@operator);
        }
    }

    public static bool TryParseVersionRange(string text, out VersionRange range)
    {
        bool ok = TryParseAlternatives(text, out VersionComparator[][]? alternatives);
        range = new VersionRange { alternatives = alternatives };
        return ok;
    }

    private static bool TryParseAlternatives(string text, out VersionComparator[][]? alternatives)
    {
        // PORT-NOTE: Go seeds `var alternatives [][]versionComparator` (nil) and appends;
        // consumers only measure length/iterate, so building through List<> and collecting
        // into arrays is equivalent (05 §6.3). Likewise the inner nil comparator slices
        // become empty arrays.
        List<VersionComparator[]> result = [];

        text = text.Trim(); // Go: strings.TrimSpace — trims the same Unicode White_Space set
        string[] ranges = LogicalOrRegExp().Split(text);
        foreach (string rangeText in ranges)
        {
            string r = rangeText.Trim();
            if (r == "")
            {
                continue;
            }

            List<VersionComparator> comparators = [];

            Match hyphenMatch = HyphenRegExp().Match(r);
            if (hyphenMatch.Success)
            {
                if (TryParseHyphen(hyphenMatch.Groups[1].Value, hyphenMatch.Groups[2].Value, out VersionComparator[] parsedComparators))
                {
                    comparators.AddRange(parsedComparators);
                }
                else
                {
                    alternatives = null;
                    return false;
                }
            }
            else
            {
                foreach (string simple in WhitespaceRegExp().Split(r))
                {
                    Match match = RangeRegExp().Match(simple.Trim());
                    if (!match.Success)
                    {
                        alternatives = null;
                        return false;
                    }

                    if (TryParseComparator(match.Groups[1].Value, match.Groups[2].Value, out VersionComparator[] parsedComparators))
                    {
                        comparators.AddRange(parsedComparators);
                    }
                    else
                    {
                        alternatives = null;
                        return false;
                    }
                }
            }

            result.Add(comparators.ToArray());
        }

        alternatives = result.ToArray();
        return true;
    }

    private static bool TryParseHyphen(string left, string right, out VersionComparator[] comparators)
    {
        comparators = [];

        if (!TryParsePartial(left, out PartialVersion leftResult))
        {
            return false;
        }

        if (!TryParsePartial(right, out PartialVersion rightResult))
        {
            return false;
        }

        List<VersionComparator> resultList = [];
        if (!IsWildcard(leftResult.majorStr))
        {
            // `MAJOR.*.*-...` gives us `>=MAJOR.0.0 ...`
            resultList.Add(new VersionComparator(RangeGreaterThanEqual, leftResult.version));
        }

        if (!IsWildcard(rightResult.majorStr))
        {
            string @operator;
            Version operand = rightResult.version;

            // Go: switch { case ...: } — condition chain
            if (IsWildcard(rightResult.minorStr))
            {
                // `...-MAJOR.*.*` gives us `... <(MAJOR+1).0.0`
                operand = operand.IncrementMajor();
                @operator = RangeLessThan;
            }
            else if (IsWildcard(rightResult.patchStr))
            {
                // `...-MAJOR.MINOR.*` gives us `... <MAJOR.(MINOR+1).0`
                operand = operand.IncrementMinor();
                @operator = RangeLessThan;
            }
            else
            {
                // `...-MAJOR.MINOR.PATCH` gives us `... <=MAJOR.MINOR.PATCH`
                @operator = RangeLessThanEqual;
            }

            resultList.Add(new VersionComparator(@operator, operand));
        }

        comparators = resultList.ToArray();
        return true;
    }

    // Produces a "partial" version
    private static bool TryParsePartial(string text, out PartialVersion result)
    {
        result = default; // Go: partialVersion{} (never read by callers on failure)

        Match match = PartialRegExp().Match(text);
        if (!match.Success)
        {
            return false;
        }

        string majorStr = match.Groups[1].Value;
        string minorStr = match.Groups[2].Value; // Go: non-participating group reads ""
        string patchStr = match.Groups[3].Value;
        string prereleaseStr = match.Groups[4].Value;
        string buildStr = match.Groups[5].Value;

        if (minorStr == "")
        {
            minorStr = "*";
        }
        if (patchStr == "")
        {
            patchStr = "*";
        }

        uint majorNumeric = 0, minorNumeric = 0, patchNumeric = 0;

        if (IsWildcard(majorStr))
        {
            majorNumeric = 0;
            minorNumeric = 0;
            patchNumeric = 0;
        }
        else
        {
            if (!TryGetUintComponent(majorStr, out majorNumeric))
            {
                return false;
            }

            if (IsWildcard(minorStr))
            {
                minorNumeric = 0;
                patchNumeric = 0;
            }
            else
            {
                if (!TryGetUintComponent(minorStr, out minorNumeric))
                {
                    return false;
                }

                if (IsWildcard(patchStr))
                {
                    patchNumeric = 0;
                }
                else
                {
                    if (!TryGetUintComponent(patchStr, out patchNumeric))
                    {
                        return false;
                    }
                }
            }
        }

        string[]? prerelease = null;
        if (prereleaseStr != "")
        {
            prerelease = prereleaseStr.Split('.');
        }

        string[]? build = null;
        if (buildStr != "")
        {
            build = buildStr.Split('.');
        }

        result = new PartialVersion
        {
            version = new Version
            {
                major = majorNumeric,
                minor = minorNumeric,
                patch = patchNumeric,
                prerelease = prerelease,
                build = build,
            },
            majorStr = majorStr,
            minorStr = minorStr,
            patchStr = patchStr,
        };

        return true;
    }

    private static bool TryParseComparator(string op, string text, out VersionComparator[] comparatorsResult)
    {
        string @operator = op;
        comparatorsResult = []; // Go: nil result slice — length-only consumption (see PORT-NOTE above)

        if (!TryParsePartial(text, out PartialVersion result))
        {
            return false;
        }

        if (!IsWildcard(result.majorStr))
        {
            switch (@operator)
            {
                case "~":
                {
                    var first = new VersionComparator(RangeGreaterThanEqual, result.version);

                    Version secondVersion;
                    if (IsWildcard(result.minorStr))
                    {
                        secondVersion = result.version.IncrementMajor();
                    }
                    else
                    {
                        secondVersion = result.version.IncrementMinor();
                    }

                    var second = new VersionComparator(RangeLessThan, secondVersion);
                    comparatorsResult = [first, second];
                    break;
                }

                case "^":
                {
                    var first = new VersionComparator(RangeGreaterThanEqual, result.version);

                    Version secondVersion;
                    if (result.version.major > 0 || IsWildcard(result.minorStr))
                    {
                        secondVersion = result.version.IncrementMajor();
                    }
                    else if (result.version.minor > 0 || IsWildcard(result.patchStr))
                    {
                        secondVersion = result.version.IncrementMinor();
                    }
                    else
                    {
                        secondVersion = result.version.IncrementPatch();
                    }
                    var second = new VersionComparator(RangeLessThan, secondVersion);
                    comparatorsResult = [first, second];
                    break;
                }

                case "<":
                case ">=":
                {
                    Version version = result.version;
                    if (IsWildcard(result.minorStr) || IsWildcard(result.patchStr))
                    {
                        version.prerelease = ["0"];
                    }
                    comparatorsResult =
                    [
                        new VersionComparator(@operator, version),
                    ];
                    break;
                }

                case "<=":
                case ">":
                {
                    Version version = result.version;
                    if (IsWildcard(result.minorStr))
                    {
                        if (@operator == RangeLessThanEqual)
                        {
                            @operator = RangeLessThan;
                        }
                        else
                        {
                            @operator = RangeGreaterThanEqual;
                        }

                        version = version.IncrementMajor();
                        version.prerelease = ["0"];
                    }
                    else if (IsWildcard(result.patchStr))
                    {
                        if (@operator == RangeLessThanEqual)
                        {
                            @operator = RangeLessThan;
                        }
                        else
                        {
                            @operator = RangeGreaterThanEqual;
                        }

                        version = version.IncrementMinor();
                        version.prerelease = ["0"];
                    }

                    comparatorsResult =
                    [
                        new VersionComparator(@operator, version),
                    ];
                    break;
                }

                case "=":
                case "":
                {
                    // normalize empty string to `=`
                    @operator = RangeEqual;

                    if (IsWildcard(result.minorStr) || IsWildcard(result.patchStr))
                    {
                        Version originalVersion = result.version;

                        Version firstVersion = originalVersion;
                        firstVersion.prerelease = ["0"];

                        Version secondVersion;
                        if (IsWildcard(result.minorStr))
                        {
                            secondVersion = originalVersion.IncrementMajor();
                        }
                        else
                        {
                            secondVersion = originalVersion.IncrementMinor();
                        }
                        secondVersion.prerelease = ["0"];

                        comparatorsResult =
                        [
                            new VersionComparator(RangeGreaterThanEqual, firstVersion),
                            new VersionComparator(RangeLessThan, secondVersion),
                        ];
                    }
                    else
                    {
                        comparatorsResult =
                        [
                            new VersionComparator(@operator, result.version),
                        ];
                    }
                    break;
                }

                default:
                    throw new InvalidOperationException("Unexpected operator: " + @operator);
            }
        }
        else
        {
            if (@operator == "<" || @operator == ">")
            {
                comparatorsResult =
                [
                    // < 0.0.0-0
                    new VersionComparator(RangeLessThan, versionZero),
                ];
            }
        }

        return true;
    }

    private static bool IsWildcard(string text)
    {
        return text == "*" || text == "x" || text == "X";
    }
}
