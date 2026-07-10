// Ported from tsgo internal/semver/version_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using Ts.Semver;
using Xunit;

namespace Ts.Semver.Tests;

public class VersionTests
{
    // Go: TestTryParseSemver — the Go table holds Version literals; spread into
    // components so the theory rows stay xUnit-serializable (05 §16.2).
    public static TheoryData<string, uint, uint, uint, string[]?, string[]?> TryParseSemverData() => new()
    {
        { "1.2.3-pre.4+build.5", 1, 2, 3, new[] { "pre", "4" }, new[] { "build", "5" } },
        { "1.2.3-pre.4", 1, 2, 3, new[] { "pre", "4" }, null },
        { "1.2.3+build.4", 1, 2, 3, null, new[] { "build", "4" } },
        { "1.2.3", 1, 2, 3, null, null },
    };

    [Theory]
    [MemberData(nameof(TryParseSemverData))]
    public void TryParseSemver(string input, uint major, uint minor, uint patch, string[]? prerelease, string[]? build)
    {
        Assert.True(Semver.TryParseVersion(input, out Version v)); // Go: assert.NilError(t, err)
        AssertVersion(v, new Version { major = major, minor = minor, patch = patch, prerelease = prerelease, build = build });
    }

    // Go: TestVersionString
    public static TheoryData<uint, uint, uint, string[]?, string[]?, string> VersionStringData() => new()
    {
        { 1, 2, 3, new[] { "pre", "4" }, new[] { "build", "5" }, "1.2.3-pre.4+build.5" },
        { 1, 2, 3, new[] { "pre", "4" }, new[] { "build" }, "1.2.3-pre.4+build" },
        { 1, 2, 3, null, new[] { "build" }, "1.2.3+build" },
        { 1, 2, 3, new[] { "pre", "4" }, null, "1.2.3-pre.4" },
        { 1, 2, 3, null, new[] { "build", "4" }, "1.2.3+build.4" },
        { 1, 2, 3, null, null, "1.2.3" },
    };

    [Theory]
    [MemberData(nameof(VersionStringData))]
    public void VersionString(uint major, uint minor, uint patch, string[]? prerelease, string[]? build, string expected)
    {
        var v = new Version { major = major, minor = minor, patch = patch, prerelease = prerelease, build = build };
        Assert.Equal(expected, v.ToString()); // gotest.tools arg order swapped (05 §16.3)
    }

    // Go: TestVersionCompare
    public static TheoryData<string, string, int> VersionCompareData() => new()
    {
        // https://semver.org/#spec-item-11
        // > Precedence is determined by the first difference when comparing each of these
        // > identifiers from left to right as follows: Major, minor, and patch versions are
        // > always compared numerically.
        { "1.0.0", "2.0.0", Semver.ComparisonLessThan },
        { "1.0.0", "1.1.0", Semver.ComparisonLessThan },
        { "1.0.0", "1.0.1", Semver.ComparisonLessThan },
        { "2.0.0", "1.0.0", Semver.ComparisonGreaterThan },
        { "1.1.0", "1.0.0", Semver.ComparisonGreaterThan },
        { "1.0.1", "1.0.0", Semver.ComparisonGreaterThan },
        { "1.0.0", "1.0.0", Semver.ComparisonEqualTo },

        // https://semver.org/#spec-item-11
        // > When major, minor, and patch are equal, a pre-release version has lower
        // > precedence than a normal version.
        { "1.0.0", "1.0.0-pre", Semver.ComparisonGreaterThan },
        { "1.0.1-pre", "1.0.0", Semver.ComparisonGreaterThan },
        { "1.0.0-pre", "1.0.0", Semver.ComparisonLessThan },

        // https://semver.org/#spec-item-11
        // > identifiers consisting of only digits are compared numerically
        { "1.0.0-0", "1.0.0-1", Semver.ComparisonLessThan },
        { "1.0.0-1", "1.0.0-0", Semver.ComparisonGreaterThan },
        { "1.0.0-2", "1.0.0-10", Semver.ComparisonLessThan },
        { "1.0.0-10", "1.0.0-2", Semver.ComparisonGreaterThan },
        { "1.0.0-0", "1.0.0-0", Semver.ComparisonEqualTo },

        // https://semver.org/#spec-item-11
        // > identifiers with letters or hyphens are compared lexically in ASCII sort order.
        { "1.0.0-a", "1.0.0-b", Semver.ComparisonLessThan },
        { "1.0.0-a-2", "1.0.0-a-10", Semver.ComparisonGreaterThan },
        { "1.0.0-b", "1.0.0-a", Semver.ComparisonGreaterThan },
        { "1.0.0-a", "1.0.0-a", Semver.ComparisonEqualTo },
        { "1.0.0-A", "1.0.0-a", Semver.ComparisonLessThan },

        // https://semver.org/#spec-item-11
        // > Numeric identifiers always have lower precedence than non-numeric identifiers.
        { "1.0.0-0", "1.0.0-alpha", Semver.ComparisonLessThan },
        { "1.0.0-alpha", "1.0.0-0", Semver.ComparisonGreaterThan },
        { "1.0.0-0", "1.0.0-0", Semver.ComparisonEqualTo },
        { "1.0.0-alpha", "1.0.0-alpha", Semver.ComparisonEqualTo },

        // https://semver.org/#spec-item-11
        // > A larger set of pre-release fields has a higher precedence than a smaller set, if all
        // > of the preceding identifiers are equal.
        { "1.0.0-alpha", "1.0.0-alpha.0", Semver.ComparisonLessThan },
        { "1.0.0-alpha.0", "1.0.0-alpha", Semver.ComparisonGreaterThan },

        // https://semver.org/#spec-item-11
        // > Precedence for two pre-release versions with the same major, minor, and patch version
        // > MUST be determined by comparing each dot separated identifier from left to right until
        // > a difference is found [...]
        { "1.0.0-a.0.b.1", "1.0.0-a.0.b.2", Semver.ComparisonLessThan },
        { "1.0.0-a.0.b.1", "1.0.0-b.0.a.1", Semver.ComparisonLessThan },
        { "1.0.0-a.0.b.2", "1.0.0-a.0.b.1", Semver.ComparisonGreaterThan },
        { "1.0.0-b.0.a.1", "1.0.0-a.0.b.1", Semver.ComparisonGreaterThan },

        // https://semver.org/#spec-item-11
        // > Build metadata does not figure into precedence
        { "1.0.0+build", "1.0.0", Semver.ComparisonEqualTo },
        { "1.0.0+build.stuff", "1.0.0", Semver.ComparisonEqualTo },
        { "1.0.0", "1.0.0+build", Semver.ComparisonEqualTo },
        { "1.0.0+build", "1.0.0+stuff", Semver.ComparisonEqualTo },

        // https://semver.org/#spec-item-11
        // Edge cases for numeric and lexical comparison of prerelease identifiers.
        { "1.0.0-alpha.99999", "1.0.0-alpha.100000", Semver.ComparisonLessThan },
        { "1.0.0-alpha.beta", "1.0.0-alpha.alpha", Semver.ComparisonGreaterThan },
    };

    [Theory]
    [MemberData(nameof(VersionCompareData))]
    public void VersionCompare(string v1Text, string v2Text, int want)
    {
        Assert.True(Semver.TryParseVersion(v1Text, out Version v1), v1Text); // Go: assert.NilError(t, err1, test.v1)
        Assert.True(Semver.TryParseVersion(v2Text, out Version v2), v2Text);
        Assert.Equal(want, v1.Compare(v2));
    }

    // Go: assertVersion (gotest.tools arg order swapped: expected first, 05 §16.3)
    private static void AssertVersion(Version a, Version b)
    {
        Assert.Equal(b.major, a.major);
        Assert.Equal(b.minor, a.minor);
        Assert.Equal(b.patch, a.patch);
        Assert.Equal(b.prerelease, a.prerelease); // Go: assert.DeepEqual
        Assert.Equal(b.build, a.build);
    }
}
