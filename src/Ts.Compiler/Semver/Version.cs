// Ported from tsgo internal/semver/version.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ts.Semver;

public struct Version
{
    internal uint major;
    internal uint minor;
    internal uint patch;
    internal string[]? prerelease;
    internal string[]? build;

    internal readonly Version IncrementMajor()
    {
        return new Version
        {
            major = major + 1,
        };
    }

    internal readonly Version IncrementMinor()
    {
        return new Version
        {
            major = major,
            minor = minor + 1,
        };
    }

    internal readonly Version IncrementPatch()
    {
        return new Version
        {
            major = major,
            minor = minor,
            patch = patch + 1,
        };
    }

    public readonly int Compare(Version b)
    {
        // https://semver.org/#spec-item-11
        // > Precedence is determined by the first difference when comparing each of these
        // > identifiers from left to right as follows: Major, minor, and patch versions are
        // > always compared numerically.
        //
        // https://semver.org/#spec-item-11
        // > Precedence for two pre-release versions with the same major, minor, and patch version
        // > MUST be determined by comparing each dot separated identifier from left to right until
        // > a difference is found [...]
        //
        // https://semver.org/#spec-item-11
        // > Build metadata does not figure into precedence
        //
        // PORT-NOTE: Go's `func (a *Version) Compare(b *Version)` starts with pointer fast
        // paths (a == b, a == nil, b == nil). Version is a value type here, so those
        // branches vanish in the type system (05 §0.4); the single Go call site that holds
        // a *semver.Version (ata CachedTyping.Version) ports as `Version?` and handles
        // null at that site.

        int r = Semver.CmpCompare(major, b.major);
        if (r != 0)
        {
            return r;
        }

        r = Semver.CmpCompare(minor, b.minor);
        if (r != 0)
        {
            return r;
        }

        r = Semver.CmpCompare(patch, b.patch);
        if (r != 0)
        {
            return r;
        }

        return Semver.ComparePreReleaseIdentifiers(prerelease, b.prerelease);
    }

    // Go: String()
    public readonly override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{major}.{minor}.{patch}");
        if (prerelease is { Length: > 0 })
        {
            sb.Append(CultureInfo.InvariantCulture, $"-{string.Join(".", prerelease)}");
        }
        if (build is { Length: > 0 })
        {
            sb.Append(CultureInfo.InvariantCulture, $"+{string.Join(".", build)}");
        }
        return sb.ToString();
    }
}

public static partial class Semver
{
    // PORT-NOTE (12-risks.md R08-A3): every Go regexp below is translated for exact RE2
    // semantics under the .NET engine —
    //   * `(?i)` → RegexOptions.IgnoreCase | RegexOptions.CultureInvariant (07 §regex policy);
    //   * `\d` → `[0-9]` (Go RE2 `\d` is ASCII-only; .NET `\d` matches Unicode Nd);
    //   * `$` → `\z` (without (?m), Go `$` anchors at end of text only; .NET `$` also
    //     matches before a trailing '\n').
    // Alternation preference and greediness need no translation: both engines implement
    // leftmost-first (Perl-style) semantics. Non-participating capture groups read as ""
    // via Group.Value, matching Go's FindStringSubmatch convention.

    // https://semver.org/#spec-item-2
    // > A normal version number MUST take the form X.Y.Z where X, Y, and Z are non-negative
    // > integers, and MUST NOT contain leading zeroes. X is the major version, Y is the minor
    // > version, and Z is the patch version. Each element MUST increase numerically.
    //
    // NOTE: We differ here in that we allow X and X.Y, with missing parts having the default
    // value of `0`.
    //
    // Go: `(?i)^(0|[1-9]\d*)(?:\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*)(?:-([a-z0-9-.]+))?(?:\+([a-z0-9-.]+))?)?)?$`
    [GeneratedRegex(@"^(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)(?:-([a-z0-9-.]+))?(?:\+([a-z0-9-.]+))?)?)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex VersionRegexp();

    // https://semver.org/#spec-item-9
    // > A pre-release version MAY be denoted by appending a hyphen and a series of dot separated
    // > identifiers immediately following the patch version. Identifiers MUST comprise only ASCII
    // > alphanumerics and hyphen [0-9A-Za-z-]. Identifiers MUST NOT be empty. Numeric identifiers
    // > MUST NOT include leading zeroes.
    //
    // Go: `(?i)^(?:0|[1-9]\d*|[a-z-][a-z0-9-]*)(?:\.(?:0|[1-9]\d*|[a-zA-Z-][a-zA-Z0-9-]*))*$`
    [GeneratedRegex(@"^(?:0|[1-9][0-9]*|[a-z-][a-z0-9-]*)(?:\.(?:0|[1-9][0-9]*|[a-zA-Z-][a-zA-Z0-9-]*))*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PrereleaseRegexp();

    // Go: `(?i)^(?:0|[1-9]\d*|[a-z-][a-z0-9-]*)$` (unused in tsgo as well; kept for parity)
    [GeneratedRegex(@"^(?:0|[1-9][0-9]*|[a-z-][a-z0-9-]*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex PrereleasePartRegexp();

    // https://semver.org/#spec-item-10
    // > Build metadata MAY be denoted by appending a plus sign and a series of dot separated
    // > identifiers immediately following the patch or pre-release version. Identifiers MUST
    // > comprise only ASCII alphanumerics and hyphen [0-9A-Za-z-]. Identifiers MUST NOT be empty.
    //
    // Go: `(?i)^[a-z0-9-]+(?:\.[a-z0-9-]+)*$`
    [GeneratedRegex(@"^[a-z0-9-]+(?:\.[a-z0-9-]+)*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex BuildRegExp();

    // Go: `(?i)^[a-z0-9-]+$` (unused in tsgo as well; kept for parity)
    [GeneratedRegex(@"^[a-z0-9-]+\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex BuildPartRegExp();

    // https://semver.org/#spec-item-9
    // > Numeric identifiers MUST NOT include leading zeroes.
    //
    // Go: `^(?:0|[1-9]\d*)$` (no (?i) upstream, so no IgnoreCase here)
    [GeneratedRegex(@"^(?:0|[1-9][0-9]*)\z")]
    internal static partial Regex NumericIdentifierRegExp();

    internal static readonly Version versionZero = new()
    {
        prerelease = ["0"],
    };

    internal const int ComparisonLessThan = -1;
    internal const int ComparisonEqualTo = 0;
    internal const int ComparisonGreaterThan = 1;

    internal static int ComparePreReleaseIdentifiers(string[]? left, string[]? right)
    {
        // https://semver.org/#spec-item-11
        // > When major, minor, and patch are equal, a pre-release version has lower precedence
        // > than a normal version.
        if (left is null || left.Length == 0)
        {
            if (right is null || right.Length == 0)
            {
                return ComparisonEqualTo;
            }
            return ComparisonGreaterThan;
        }
        else if (right is null || right.Length == 0)
        {
            return ComparisonLessThan;
        }

        // https://semver.org/#spec-item-11
        // > Precedence for two pre-release versions with the same major, minor, and patch version
        // > MUST be determined by comparing each dot separated identifier from left to right until
        // > a difference is found [...]
        //
        // Go: slices.CompareFunc(left, right, comparePreReleaseIdentifier) — element-wise
        // compare; if all shared elements are equal, the shorter slice compares less.
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            int c = ComparePreReleaseIdentifier(left[i], right[i]);
            if (c != 0)
            {
                return c;
            }
        }
        return CmpCompare(left.Length, right.Length);
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        // https://semver.org/#spec-item-11
        // > Precedence for two pre-release versions with the same major, minor, and patch version
        // > MUST be determined by comparing each dot separated identifier from left to right until
        // > a difference is found [...]
        //
        // Go: strings.Compare — compares UTF-8 bytes and returns exactly -1/0/+1. Identifiers
        // here are parser-constrained to ASCII, where ordinal UTF-16 comparison agrees;
        // Math.Sign normalizes CompareOrdinal's unconstrained magnitude.
        int compareResult = Math.Sign(string.CompareOrdinal(left, right));
        if (compareResult == 0)
        {
            return compareResult;
        }

        bool leftIsNumeric = NumericIdentifierRegExp().IsMatch(left);
        bool rightIsNumeric = NumericIdentifierRegExp().IsMatch(right);

        if (leftIsNumeric || rightIsNumeric)
        {
            // https://semver.org/#spec-item-11
            // > Numeric identifiers always have lower precedence than non-numeric identifiers.
            if (!rightIsNumeric)
            {
                return ComparisonLessThan;
            }
            if (!leftIsNumeric)
            {
                return ComparisonGreaterThan;
            }

            // https://semver.org/#spec-item-11
            // > identifiers consisting of only digits are compared numerically
            bool leftOk = TryGetUintComponent(left, out uint leftAsNumber);
            bool rightOk = TryGetUintComponent(right, out uint rightAsNumber);
            if (!leftOk || !rightOk)
            {
                // This should only happen in the event of an overflow.
                // If so, use the lengths or fall back to string comparison.
                int leftLen = left.Length;
                int rightLen = right.Length;
                int lenCompare = CmpCompare(leftLen, rightLen);
                if (lenCompare == 0)
                {
                    return compareResult;
                }
                else
                {
                    return lenCompare;
                }
            }
            return CmpCompare(leftAsNumber, rightAsNumber);
        }

        // https://semver.org/#spec-item-11
        // > identifiers with letters or hyphens are compared lexically in ASCII sort order.
        return compareResult;
    }

    // Go: TryParseVersion(text string) (Version, error) with *SemverParseError.
    // PORT-NOTE: every Go caller only nil-checks the error, so this degrades to the Try
    // pattern and the Go error text is unused by callers (05 §4.2 prescribes exactly this
    // signature); MustParse reproduces the Go panic message.
    public static bool TryParseVersion(string text, out Version result)
    {
        result = new Version();

        Match match = VersionRegexp().Match(text);
        if (!match.Success)
        {
            return false;
        }

        string majorStr = match.Groups[1].Value;
        string minorStr = match.Groups[2].Value;
        string patchStr = match.Groups[3].Value;
        string prereleaseStr = match.Groups[4].Value;
        string buildStr = match.Groups[5].Value;

        if (!TryGetUintComponent(majorStr, out result.major))
        {
            return false;
        }

        if (minorStr != "")
        {
            if (!TryGetUintComponent(minorStr, out result.minor))
            {
                return false;
            }
        }

        if (patchStr != "")
        {
            if (!TryGetUintComponent(patchStr, out result.patch))
            {
                return false;
            }
        }

        if (prereleaseStr != "")
        {
            if (!PrereleaseRegexp().IsMatch(prereleaseStr))
            {
                return false;
            }

            result.prerelease = prereleaseStr.Split('.');
        }
        if (buildStr != "")
        {
            if (!BuildRegExp().IsMatch(buildStr))
            {
                return false;
            }

            result.build = buildStr.Split('.');
        }

        return true;
    }

    public static Version MustParse(string text)
    {
        if (!TryParseVersion(text, out Version v))
        {
            // Go: panic(err) where err.Error() is fmt.Sprintf("Could not parse version
            // string from %q", origInput). PORT-NOTE: %q escaping approximated with plain
            // quotes; the message is not baseline-bearing (panic-only).
            throw new InvalidOperationException($"Could not parse version string from \"{text}\"");
        }
        return v;
    }

    // Go: getUintComponent — strconv.ParseUint(text, 10, 32). On error Go also returns the
    // range-clamped value, but no caller consumes it, so the Try pattern drops it.
    internal static bool TryGetUintComponent(string text, out uint result)
    {
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    // Go: cmp.Compare — returns exactly -1/0/+1 (callers surface this value directly).
    internal static int CmpCompare<T>(T x, T y) where T : IComparable<T>
    {
        return Math.Sign(x.CompareTo(y));
    }
}
