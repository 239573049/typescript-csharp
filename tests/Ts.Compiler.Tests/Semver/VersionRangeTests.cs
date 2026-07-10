// Ported from tsgo internal/semver/version_range_test.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using Ts.Semver;
using Xunit;

namespace Ts.Semver.Tests;

public class VersionRangeTests
{
    // Go: TestWildcardsHaveSameString — one [Fact] per named string group (05 §16.1,
    // non-table t.Run subtests become ParentName_SubName facts).
    private static readonly string[] majorWildcardStrings =
    [
        "",
        "*",
        "*.*",
        "*.*.*",
        "x",
        "x.x",
        "x.x.x",
        "X",
        "X.X",
        "X.X.X",
    ];

    private static readonly string[] minorWildcardStrings =
    [
        "1",
        "1.*",
        "1.*.*",
        "1.x",
        "1.x.x",
        "1.X",
        "1.X.X",
    ];

    private static readonly string[] patchWildcardStrings =
    [
        "1.2",
        "1.2.*",
        "1.2.x",
        "1.2.X",
    ];

    private static readonly string[] mixedCaseWildcardStrings =
    [
        "x",
        "X",
        "*",
        "x.X.x",
        "X.x.*",
    ];

    [Fact]
    public void WildcardsHaveSameString_MajorWildcardStrings() => AssertAllVersionRangesHaveIdenticalStrings(majorWildcardStrings);

    [Fact]
    public void WildcardsHaveSameString_MinorWildcardStrings() => AssertAllVersionRangesHaveIdenticalStrings(minorWildcardStrings);

    [Fact]
    public void WildcardsHaveSameString_PatchWildcardStrings() => AssertAllVersionRangesHaveIdenticalStrings(patchWildcardStrings);

    [Fact]
    public void WildcardsHaveSameString_MixedCaseWildcardStrings() => AssertAllVersionRangesHaveIdenticalStrings(mixedCaseWildcardStrings);

    // Go: assertAllVersionRangesHaveIdenticalStrings — the s1/s2 pair context moves from
    // the subtest name into the failure message.
    private static void AssertAllVersionRangesHaveIdenticalStrings(string[] strs)
    {
        foreach (string s1 in strs)
        {
            foreach (string s2 in strs)
            {
                Assert.True(Semver.TryParseVersionRange(s1, out VersionRange v1));
                Assert.True(Semver.TryParseVersionRange(s2, out VersionRange v2));
                string str1 = v1.ToString();
                string str2 = v2.ToString();
                if (str1 != str2) // Go: assert.DeepEqual(t, v1.String(), v2.String())
                {
                    Assert.Fail($"\"{s1}\" == \"{s2}\": range strings differ (\"{str1}\" != \"{str2}\")");
                }
            }
        }
    }

    // Go: TestVersionRanges
    [Fact]
    public void VersionRanges()
    {
        AssertRangesGoodBad("1",
            good: ["1.0.0", "1.9.9", "1.0.0-pre", "1.0.0+build"],
            bad: ["0.0.0", "2.0.0", "0.0.0-pre", "0.0.0+build"]);
        AssertRangesGoodBad("1.2",
            good: ["1.2.0", "1.2.9", "1.2.0-pre", "1.2.0+build"],
            bad: ["1.1.0", "1.3.0", "1.1.0-pre", "1.1.0+build"]);

        AssertRangesGoodBad("1.2.3",
            good: ["1.2.3", "1.2.3+build"],
            bad: ["1.2.2", "1.2.4", "1.2.2-pre", "1.2.2+build", "1.2.3-pre"]);

        AssertRangesGoodBad("1.2.3-pre",
            good: ["1.2.3-pre", "1.2.3-pre+build.stuff"],
            bad: ["1.2.3", "1.2.3-pre.0", "1.2.3-pre.9", "1.2.3-pre.0+build", "1.2.3-pre.9+build", "1.2.3+build", "1.2.4"]);

        AssertRangesGoodBad("<3.8.0",
            good: ["3.6", "3.7"],
            bad: ["3.8", "3.9", "4.0"]);

        AssertRangesGoodBad("<=3.8.0",
            good: ["3.6", "3.7", "3.8"],
            bad: ["3.9", "4.0"]);
        AssertRangesGoodBad(">3.8.0",
            good: ["3.9", "4.0"],
            bad: ["3.6", "3.7", "3.8"]);
        AssertRangesGoodBad(">=3.8.0",
            good: ["3.8", "3.9", "4.0"],
            bad: ["3.6", "3.7"]);

        AssertRangesGoodBad("<3.8.0-0",
            good: ["3.6", "3.7"],
            bad: ["3.8", "3.9", "4.0"]);

        AssertRangesGoodBad("<=3.8.0-0",
            good: ["3.6", "3.7"],
            bad: ["3.8", "3.9", "4.0"]);

        // Big numbers in prerelease strings.
        string lotsaOnes = new('1', 320); // Go: strings.Repeat("1", 320)
        AssertRangesGoodBad(">=1.2.3-1" + lotsaOnes,
            good: ["1.2.3-1" + lotsaOnes, "1.2.3-11" + lotsaOnes + ".1", "1.2.3-1" + lotsaOnes + ".1+build"],
            bad: ["1.2.3-" + lotsaOnes + ".1+build"]);
    }

    // Go: TestComparatorsOfVersionRanges
    public static TheoryData<string, string, bool> ComparatorsData() => new()
    {
        // empty (matches everything)
        { "", "2.0.0", true },
        { "", "2.0.0-0", true },
        { "", "1.1.0", true },
        { "", "1.1.0-0", true },
        { "", "1.0.1", true },
        { "", "1.0.1-0", true },
        { "", "1.0.0", true },
        { "", "1.0.0-0", true },
        { "", "0.0.0", true },
        { "", "0.0.0-0", true },

        // wildcard major (matches everything)
        { "*", "2.0.0", true },
        { "*", "2.0.0-0", true },
        { "*", "1.1.0", true },
        { "*", "1.1.0-0", true },
        { "*", "1.0.1", true },
        { "*", "1.0.1-0", true },
        { "*", "1.0.0", true },
        { "*", "1.0.0-0", true },
        { "*", "0.0.0", true },
        { "*", "0.0.0-0", true },

        // wildcard minor
        { "1", "2.0.0", false },
        { "1", "2.0.0-0", false },
        { "1", "1.1.0", true },
        { "1", "1.1.0-0", true },
        { "1", "1.0.1", true },
        { "1", "1.0.1-0", true },
        { "1", "1.0.0", true },
        { "1", "1.0.0-0", true },
        { "1", "0.0.0", false },
        { "1", "0.0.0-0", false },

        // wildcard patch
        { "1.1", "2.0.0", false },
        { "1.1", "2.0.0-0", false },
        { "1.1", "1.1.0", true },
        { "1.1", "1.1.0-0", true },
        { "1.1", "1.0.1", false },
        { "1.1", "1.0.1-0", false },
        { "1.1", "1.0.0", false },
        { "1.1", "1.0.0-0", false },
        { "1.1", "0.0.0", false },
        { "1.1", "0.0.0-0", false },
        { "1.0", "2.0.0", false },
        { "1.0", "2.0.0-0", false },
        { "1.0", "1.1.0", false },
        { "1.0", "1.1.0-0", false },
        { "1.0", "1.0.1", true },
        { "1.0", "1.0.1-0", true },
        { "1.0", "1.0.0", true },
        { "1.0", "1.0.0-0", true },
        { "1.0", "0.0.0", false },
        { "1.0", "0.0.0-0", false },

        // exact
        { "1.1.0", "2.0.0", false },
        { "1.1.0", "2.0.0-0", false },
        { "1.1.0", "1.1.0", true },
        { "1.1.0", "1.1.0-0", false },
        { "1.1.0", "1.0.1", false },
        { "1.1.0", "1.0.1-0", false },
        { "1.1.0", "1.0.0-0", false },
        { "1.1.0", "1.0.0", false },
        { "1.1.0", "0.0.0", false },
        { "1.1.0", "0.0.0-0", false },
        { "1.1.0-0", "2.0.0", false },
        { "1.1.0-0", "2.0.0-0", false },
        { "1.1.0-0", "1.1.0", false },
        { "1.1.0-0", "1.1.0-0", true },
        { "1.1.0-0", "1.0.1", false },
        { "1.1.0-0", "1.0.1-0", false },
        { "1.1.0-0", "1.0.0-0", false },
        { "1.1.0-0", "1.0.0", false },
        { "1.1.0-0", "0.0.0", false },
        { "1.1.0-0", "0.0.0-0", false },
        { "1.0.1", "2.0.0", false },
        { "1.0.1", "2.0.0-0", false },
        { "1.0.1", "1.1.0", false },
        { "1.0.1", "1.1.0-0", false },
        { "1.0.1", "1.0.1", true },
        { "1.0.1", "1.0.1-0", false },
        { "1.0.1", "1.0.0-0", false },
        { "1.0.1", "1.0.0", false },
        { "1.0.1", "0.0.0", false },
        { "1.0.1", "0.0.0-0", false },
        { "1.0.1-0", "2.0.0", false },
        { "1.0.1-0", "2.0.0-0", false },
        { "1.0.1-0", "1.1.0", false },
        { "1.0.1-0", "1.1.0-0", false },
        { "1.0.1-0", "1.0.1", false },
        { "1.0.1-0", "1.0.1-0", true },
        { "1.0.1-0", "1.0.0-0", false },
        { "1.0.1-0", "1.0.0", false },
        { "1.0.1-0", "0.0.0", false },
        { "1.0.1-0", "0.0.0-0", false },
        { "1.0.0", "2.0.0", false },
        { "1.0.0", "2.0.0-0", false },
        { "1.0.0", "1.1.0", false },
        { "1.0.0", "1.1.0-0", false },
        { "1.0.0", "1.0.1", false },
        { "1.0.0", "1.0.1-0", false },
        { "1.0.0", "1.0.0-0", false },
        { "1.0.0", "1.0.0", true },
        { "1.0.0", "0.0.0", false },
        { "1.0.0", "0.0.0-0", false },
        { "1.0.0-0", "2.0.0", false },
        { "1.0.0-0", "2.0.0-0", false },
        { "1.0.0-0", "1.1.0", false },
        { "1.0.0-0", "1.1.0-0", false },
        { "1.0.0-0", "1.0.1", false },
        { "1.0.0-0", "1.0.1-0", false },
        { "1.0.0-0", "1.0.0", false },
        { "1.0.0-0", "1.0.0-0", true },

        // = wildcard major (matches everything)
        { "=*", "2.0.0", true },
        { "=*", "2.0.0-0", true },
        { "=*", "1.1.0", true },
        { "=*", "1.1.0-0", true },
        { "=*", "1.0.1", true },
        { "=*", "1.0.1-0", true },
        { "=*", "1.0.0", true },
        { "=*", "1.0.0-0", true },
        { "=*", "0.0.0", true },
        { "=*", "0.0.0-0", true },

        // = wildcard minor
        { "=1", "2.0.0", false },
        { "=1", "2.0.0-0", false },
        { "=1", "1.1.0", true },
        { "=1", "1.1.0-0", true },
        { "=1", "1.0.1", true },
        { "=1", "1.0.1-0", true },
        { "=1", "1.0.0", true },
        { "=1", "1.0.0-0", true },
        { "=1", "0.0.0", false },
        { "=1", "0.0.0-0", false },

        // = wildcard patch
        { "=1.1", "2.0.0", false },
        { "=1.1", "2.0.0-0", false },
        { "=1.1", "1.1.0", true },
        { "=1.1", "1.1.0-0", true },
        { "=1.1", "1.0.1", false },
        { "=1.1", "1.0.1-0", false },
        { "=1.1", "1.0.0", false },
        { "=1.1", "1.0.0-0", false },
        { "=1.1", "0.0.0", false },
        { "=1.1", "0.0.0-0", false },
        { "=1.0", "2.0.0", false },
        { "=1.0", "2.0.0-0", false },
        { "=1.0", "1.1.0", false },
        { "=1.0", "1.1.0-0", false },
        { "=1.0", "1.0.1", true },
        { "=1.0", "1.0.1-0", true },
        { "=1.0", "1.0.0", true },
        { "=1.0", "1.0.0-0", true },
        { "=1.0", "0.0.0", false },
        { "=1.0", "0.0.0-0", false },

        // = exact
        { "=1.1.0", "2.0.0", false },
        { "=1.1.0", "2.0.0-0", false },
        { "=1.1.0", "1.1.0", true },
        { "=1.1.0", "1.1.0-0", false },
        { "=1.1.0", "1.0.1", false },
        { "=1.1.0", "1.0.1-0", false },
        { "=1.1.0", "1.0.0-0", false },
        { "=1.1.0", "1.0.0", false },
        { "=1.1.0", "0.0.0", false },
        { "=1.1.0", "0.0.0-0", false },
        { "=1.1.0-0", "2.0.0", false },
        { "=1.1.0-0", "2.0.0-0", false },
        { "=1.1.0-0", "1.1.0", false },
        { "=1.1.0-0", "1.1.0-0", true },
        { "=1.1.0-0", "1.0.1", false },
        { "=1.1.0-0", "1.0.1-0", false },
        { "=1.1.0-0", "1.0.0-0", false },
        { "=1.1.0-0", "1.0.0", false },
        { "=1.1.0-0", "0.0.0", false },
        { "=1.1.0-0", "0.0.0-0", false },
        { "=1.0.1", "2.0.0", false },
        { "=1.0.1", "2.0.0-0", false },
        { "=1.0.1", "1.1.0", false },
        { "=1.0.1", "1.1.0-0", false },
        { "=1.0.1", "1.0.1", true },
        { "=1.0.1", "1.0.1-0", false },
        { "=1.0.1", "1.0.0-0", false },
        { "=1.0.1", "1.0.0", false },
        { "=1.0.1", "0.0.0", false },
        { "=1.0.1", "0.0.0-0", false },
        { "=1.0.1-0", "2.0.0", false },
        { "=1.0.1-0", "2.0.0-0", false },
        { "=1.0.1-0", "1.1.0", false },
        { "=1.0.1-0", "1.1.0-0", false },
        { "=1.0.1-0", "1.0.1", false },
        { "=1.0.1-0", "1.0.1-0", true },
        { "=1.0.1-0", "1.0.0-0", false },
        { "=1.0.1-0", "1.0.0", false },
        { "=1.0.1-0", "0.0.0", false },
        { "=1.0.1-0", "0.0.0-0", false },
        { "=1.0.0", "2.0.0", false },
        { "=1.0.0", "2.0.0-0", false },
        { "=1.0.0", "1.1.0", false },
        { "=1.0.0", "1.1.0-0", false },
        { "=1.0.0", "1.0.1", false },
        { "=1.0.0", "1.0.1-0", false },
        { "=1.0.0", "1.0.0-0", false },
        { "=1.0.0", "1.0.0", true },
        { "=1.0.0", "0.0.0", false },
        { "=1.0.0", "0.0.0-0", false },
        { "=1.0.0-0", "2.0.0", false },
        { "=1.0.0-0", "2.0.0-0", false },
        { "=1.0.0-0", "1.1.0", false },
        { "=1.0.0-0", "1.1.0-0", false },
        { "=1.0.0-0", "1.0.1", false },
        { "=1.0.0-0", "1.0.1-0", false },
        { "=1.0.0-0", "1.0.0", false },
        { "=1.0.0-0", "1.0.0-0", true },

        // > wildcard major (matches nothing)
        { ">*", "2.0.0", false },
        { ">*", "2.0.0-0", false },
        { ">*", "1.1.0", false },
        { ">*", "1.1.0-0", false },
        { ">*", "1.0.1", false },
        { ">*", "1.0.1-0", false },
        { ">*", "1.0.0", false },
        { ">*", "1.0.0-0", false },
        { ">*", "0.0.0", false },
        { ">*", "0.0.0-0", false },

        // > wildcard minor
        { ">1", "2.0.0", true },
        { ">1", "2.0.0-0", true },
        { ">1", "1.1.0", false },
        { ">1", "1.1.0-0", false },
        { ">1", "1.0.1", false },
        { ">1", "1.0.1-0", false },
        { ">1", "1.0.0", false },
        { ">1", "1.0.0-0", false },
        { ">1", "0.0.0", false },
        { ">1", "0.0.0-0", false },

        // > wildcard patch
        { ">1.1", "2.0.0", true },
        { ">1.1", "2.0.0-0", true },
        { ">1.1", "1.1.0", false },
        { ">1.1", "1.1.0-0", false },
        { ">1.1", "1.0.1", false },
        { ">1.1", "1.0.1-0", false },
        { ">1.1", "1.0.0", false },
        { ">1.1", "1.0.0-0", false },
        { ">1.1", "0.0.0", false },
        { ">1.1", "0.0.0-0", false },
        { ">1.0", "2.0.0", true },
        { ">1.0", "2.0.0-0", true },
        { ">1.0", "1.1.0", true },
        { ">1.0", "1.1.0-0", true },
        { ">1.0", "1.0.1", false },
        { ">1.0", "1.0.1-0", false },
        { ">1.0", "1.0.0", false },
        { ">1.0", "1.0.0-0", false },
        { ">1.0", "0.0.0", false },
        { ">1.0", "0.0.0-0", false },

        // > exact
        { ">1.1.0", "2.0.0", true },
        { ">1.1.0", "2.0.0-0", true },
        { ">1.1.0", "1.1.0", false },
        { ">1.1.0", "1.1.0-0", false },
        { ">1.1.0", "1.0.1", false },
        { ">1.1.0", "1.0.1-0", false },
        { ">1.1.0", "1.0.0", false },
        { ">1.1.0", "1.0.0-0", false },
        { ">1.1.0", "0.0.0", false },
        { ">1.1.0", "0.0.0-0", false },
        { ">1.1.0-0", "2.0.0", true },
        { ">1.1.0-0", "2.0.0-0", true },
        { ">1.1.0-0", "1.1.0", true },
        { ">1.1.0-0", "1.1.0-0", false },
        { ">1.1.0-0", "1.0.1", false },
        { ">1.1.0-0", "1.0.1-0", false },
        { ">1.1.0-0", "1.0.0", false },
        { ">1.1.0-0", "1.0.0-0", false },
        { ">1.1.0-0", "0.0.0", false },
        { ">1.1.0-0", "0.0.0-0", false },
        { ">1.0.1", "2.0.0", true },
        { ">1.0.1", "2.0.0-0", true },
        { ">1.0.1", "1.1.0", true },
        { ">1.0.1", "1.1.0-0", true },
        { ">1.0.1", "1.0.1", false },
        { ">1.0.1", "1.0.1-0", false },
        { ">1.0.1", "1.0.0", false },
        { ">1.0.1", "1.0.0-0", false },
        { ">1.0.1", "0.0.0", false },
        { ">1.0.1", "0.0.0-0", false },
        { ">1.0.1-0", "2.0.0", true },
        { ">1.0.1-0", "2.0.0-0", true },
        { ">1.0.1-0", "1.1.0", true },
        { ">1.0.1-0", "1.1.0-0", true },
        { ">1.0.1-0", "1.0.1", true },
        { ">1.0.1-0", "1.0.1-0", false },
        { ">1.0.1-0", "1.0.0", false },
        { ">1.0.1-0", "1.0.0-0", false },
        { ">1.0.1-0", "0.0.0", false },
        { ">1.0.1-0", "0.0.0-0", false },
        { ">1.0.0", "2.0.0", true },
        { ">1.0.0", "2.0.0-0", true },
        { ">1.0.0", "1.1.0", true },
        { ">1.0.0", "1.1.0-0", true },
        { ">1.0.0", "1.0.1", true },
        { ">1.0.0", "1.0.1-0", true },
        { ">1.0.0", "1.0.0", false },
        { ">1.0.0", "1.0.0-0", false },
        { ">1.0.0", "0.0.0", false },
        { ">1.0.0", "0.0.0-0", false },
        { ">1.0.0-0", "2.0.0", true },
        { ">1.0.0-0", "2.0.0-0", true },
        { ">1.0.0-0", "1.1.0", true },
        { ">1.0.0-0", "1.1.0-0", true },
        { ">1.0.0-0", "1.0.1", true },
        { ">1.0.0-0", "1.0.1-0", true },
        { ">1.0.0-0", "1.0.0", true },
        { ">1.0.0-0", "1.0.0-0", false },
        { ">1.0.0-0", "0.0.0", false },
        { ">1.0.0-0", "0.0.0-0", false },

        // >= wildcard major (matches everything)
        { ">=*", "2.0.0", true },
        { ">=*", "2.0.0-0", true },
        { ">=*", "1.1.0", true },
        { ">=*", "1.1.0-0", true },
        { ">=*", "1.0.1", true },
        { ">=*", "1.0.1-0", true },
        { ">=*", "1.0.0", true },
        { ">=*", "1.0.0-0", true },
        { ">=*", "0.0.0", true },
        { ">=*", "0.0.0-0", true },

        // >= wildcard minor
        { ">=1", "2.0.0", true },
        { ">=1", "2.0.0-0", true },
        { ">=1", "1.1.0", true },
        { ">=1", "1.1.0-0", true },
        { ">=1", "1.0.1", true },
        { ">=1", "1.0.1-0", true },
        { ">=1", "1.0.0", true },
        { ">=1", "1.0.0-0", true },
        { ">=1", "0.0.0", false },
        { ">=1", "0.0.0-0", false },

        // >= wildcard patch
        { ">=1.1", "2.0.0", true },
        { ">=1.1", "2.0.0-0", true },
        { ">=1.1", "1.1.0", true },
        { ">=1.1", "1.1.0-0", true },
        { ">=1.1", "1.0.1", false },
        { ">=1.1", "1.0.1-0", false },
        { ">=1.1", "1.0.0", false },
        { ">=1.1", "1.0.0-0", false },
        { ">=1.1", "0.0.0", false },
        { ">=1.1", "0.0.0-0", false },
        { ">=1.0", "2.0.0", true },
        { ">=1.0", "2.0.0-0", true },
        { ">=1.0", "1.1.0", true },
        { ">=1.0", "1.1.0-0", true },
        { ">=1.0", "1.0.1", true },
        { ">=1.0", "1.0.1-0", true },
        { ">=1.0", "1.0.0", true },
        { ">=1.0", "1.0.0-0", true },
        { ">=1.0", "0.0.0", false },
        { ">=1.0", "0.0.0-0", false },

        // >= exact
        { ">=1.1.0", "2.0.0", true },
        { ">=1.1.0", "2.0.0-0", true },
        { ">=1.1.0", "1.1.0", true },
        { ">=1.1.0", "1.1.0-0", false },
        { ">=1.1.0", "1.0.1", false },
        { ">=1.1.0", "1.0.1-0", false },
        { ">=1.1.0", "1.0.0", false },
        { ">=1.1.0", "1.0.0-0", false },
        { ">=1.1.0", "0.0.0", false },
        { ">=1.1.0", "0.0.0-0", false },
        { ">=1.1.0-0", "2.0.0", true },
        { ">=1.1.0-0", "2.0.0-0", true },
        { ">=1.1.0-0", "1.1.0", true },
        { ">=1.1.0-0", "1.1.0-0", true },
        { ">=1.1.0-0", "1.0.1", false },
        { ">=1.1.0-0", "1.0.1-0", false },
        { ">=1.1.0-0", "1.0.0", false },
        { ">=1.1.0-0", "1.0.0-0", false },
        { ">=1.1.0-0", "0.0.0", false },
        { ">=1.1.0-0", "0.0.0-0", false },
        { ">=1.0.1", "2.0.0", true },
        { ">=1.0.1", "2.0.0-0", true },
        { ">=1.0.1", "1.1.0", true },
        { ">=1.0.1", "1.1.0-0", true },
        { ">=1.0.1", "1.0.1", true },
        { ">=1.0.1", "1.0.1-0", false },
        { ">=1.0.1", "1.0.0", false },
        { ">=1.0.1", "1.0.0-0", false },
        { ">=1.0.1", "0.0.0", false },
        { ">=1.0.1", "0.0.0-0", false },
        { ">=1.0.1-0", "2.0.0", true },
        { ">=1.0.1-0", "2.0.0-0", true },
        { ">=1.0.1-0", "1.1.0", true },
        { ">=1.0.1-0", "1.1.0-0", true },
        { ">=1.0.1-0", "1.0.1", true },
        { ">=1.0.1-0", "1.0.1-0", true },
        { ">=1.0.1-0", "1.0.0", false },
        { ">=1.0.1-0", "1.0.0-0", false },
        { ">=1.0.1-0", "0.0.0", false },
        { ">=1.0.1-0", "0.0.0-0", false },
        { ">=1.0.0", "2.0.0", true },
        { ">=1.0.0", "2.0.0-0", true },
        { ">=1.0.0", "1.1.0", true },
        { ">=1.0.0", "1.1.0-0", true },
        { ">=1.0.0", "1.0.1", true },
        { ">=1.0.0", "1.0.1-0", true },
        { ">=1.0.0", "1.0.0", true },
        { ">=1.0.0", "1.0.0-0", false },
        { ">=1.0.0", "0.0.0", false },
        { ">=1.0.0", "0.0.0-0", false },
        { ">=1.0.0-0", "2.0.0", true },
        { ">=1.0.0-0", "2.0.0-0", true },
        { ">=1.0.0-0", "1.1.0", true },
        { ">=1.0.0-0", "1.1.0-0", true },
        { ">=1.0.0-0", "1.0.1", true },
        { ">=1.0.0-0", "1.0.1-0", true },
        { ">=1.0.0-0", "1.0.0", true },
        { ">=1.0.0-0", "1.0.0-0", true },
        { ">=1.0.0-0", "0.0.0", false },
        { ">=1.0.0-0", "0.0.0-0", false },

        // < wildcard major (matches nothing)
        { "<*", "2.0.0", false },
        { "<*", "2.0.0-0", false },
        { "<*", "1.1.0", false },
        { "<*", "1.1.0-0", false },
        { "<*", "1.0.1", false },
        { "<*", "1.0.1-0", false },
        { "<*", "1.0.0", false },
        { "<*", "1.0.0-0", false },
        { "<*", "0.0.0", false },
        { "<*", "0.0.0-0", false },

        // < wildcard minor
        { "<1", "2.0.0", false },
        { "<1", "2.0.0-0", false },
        { "<1", "1.1.0", false },
        { "<1", "1.1.0-0", false },
        { "<1", "1.0.1", false },
        { "<1", "1.0.1-0", false },
        { "<1", "1.0.0", false },
        { "<1", "1.0.0-0", false },
        { "<1", "0.0.0", true },
        { "<1", "0.0.0-0", true },

        // < wildcard patch
        { "<1.1", "2.0.0", false },
        { "<1.1", "2.0.0-0", false },
        { "<1.1", "1.1.0", false },
        { "<1.1", "1.1.0-0", false },
        { "<1.1", "1.0.1", true },
        { "<1.1", "1.0.1-0", true },
        { "<1.1", "1.0.0", true },
        { "<1.1", "1.0.0-0", true },
        { "<1.1", "0.0.0", true },
        { "<1.1", "0.0.0-0", true },
        { "<1.0", "2.0.0", false },
        { "<1.0", "2.0.0-0", false },
        { "<1.0", "1.1.0", false },
        { "<1.0", "1.1.0-0", false },
        { "<1.0", "1.0.1", false },
        { "<1.0", "1.0.1-0", false },
        { "<1.0", "1.0.0", false },
        { "<1.0", "1.0.0-0", false },
        { "<1.0", "0.0.0", true },
        { "<1.0", "0.0.0-0", true },

        // < exact
        { "<1.1.0", "2.0.0", false },
        { "<1.1.0", "2.0.0-0", false },
        { "<1.1.0", "1.1.0", false },
        { "<1.1.0", "1.1.0-0", true },
        { "<1.1.0", "1.0.1", true },
        { "<1.1.0", "1.0.1-0", true },
        { "<1.1.0", "1.0.0", true },
        { "<1.1.0", "1.0.0-0", true },
        { "<1.1.0", "0.0.0", true },
        { "<1.1.0", "0.0.0-0", true },
        { "<1.1.0-0", "2.0.0", false },
        { "<1.1.0-0", "2.0.0-0", false },
        { "<1.1.0-0", "1.1.0", false },
        { "<1.1.0-0", "1.1.0-0", false },
        { "<1.1.0-0", "1.0.1", true },
        { "<1.1.0-0", "1.0.1-0", true },
        { "<1.1.0-0", "1.0.0", true },
        { "<1.1.0-0", "1.0.0-0", true },
        { "<1.1.0-0", "0.0.0", true },
        { "<1.1.0-0", "0.0.0-0", true },
        { "<1.0.1", "2.0.0", false },
        { "<1.0.1", "2.0.0-0", false },
        { "<1.0.1", "1.1.0", false },
        { "<1.0.1", "1.1.0-0", false },
        { "<1.0.1", "1.0.1", false },
        { "<1.0.1", "1.0.1-0", true },
        { "<1.0.1", "1.0.0", true },
        { "<1.0.1", "1.0.0-0", true },
        { "<1.0.1", "0.0.0", true },
        { "<1.0.1", "0.0.0-0", true },
        { "<1.0.1-0", "2.0.0", false },
        { "<1.0.1-0", "2.0.0-0", false },
        { "<1.0.1-0", "1.1.0", false },
        { "<1.0.1-0", "1.1.0-0", false },
        { "<1.0.1-0", "1.0.1", false },
        { "<1.0.1-0", "1.0.1-0", false },
        { "<1.0.1-0", "1.0.0", true },
        { "<1.0.1-0", "1.0.0-0", true },
        { "<1.0.1-0", "0.0.0", true },
        { "<1.0.1-0", "0.0.0-0", true },
        { "<1.0.0", "2.0.0", false },
        { "<1.0.0", "2.0.0-0", false },
        { "<1.0.0", "1.1.0", false },
        { "<1.0.0", "1.1.0-0", false },
        { "<1.0.0", "1.0.1", false },
        { "<1.0.0", "1.0.1-0", false },
        { "<1.0.0", "1.0.0", false },
        { "<1.0.0", "1.0.0-0", true },
        { "<1.0.0", "0.0.0", true },
        { "<1.0.0", "0.0.0-0", true },
        { "<1.0.0-0", "2.0.0", false },
        { "<1.0.0-0", "2.0.0-0", false },
        { "<1.0.0-0", "1.1.0", false },
        { "<1.0.0-0", "1.1.0-0", false },
        { "<1.0.0-0", "1.0.1", false },
        { "<1.0.0-0", "1.0.1-0", false },
        { "<1.0.0-0", "1.0.0", false },
        { "<1.0.0-0", "1.0.0-0", false },
        { "<1.0.0-0", "0.0.0", true },
        { "<1.0.0-0", "0.0.0-0", true },

        // <= wildcard major (matches everything)
        { "<=*", "2.0.0", true },
        { "<=*", "2.0.0-0", true },
        { "<=*", "1.1.0", true },
        { "<=*", "1.1.0-0", true },
        { "<=*", "1.0.1", true },
        { "<=*", "1.0.1-0", true },
        { "<=*", "1.0.0", true },
        { "<=*", "1.0.0-0", true },
        { "<=*", "0.0.0", true },
        { "<=*", "0.0.0-0", true },

        // <= wildcard minor
        { "<=1", "2.0.0", false },
        { "<=1", "2.0.0-0", false },
        { "<=1", "1.1.0", true },
        { "<=1", "1.1.0-0", true },
        { "<=1", "1.0.1", true },
        { "<=1", "1.0.1-0", true },
        { "<=1", "1.0.0", true },
        { "<=1", "1.0.0-0", true },
        { "<=1", "0.0.0", true },
        { "<=1", "0.0.0-0", true },

        // <= wildcard patch
        { "<=1.1", "2.0.0", false },
        { "<=1.1", "2.0.0-0", false },
        { "<=1.1", "1.1.0", true },
        { "<=1.1", "1.1.0-0", true },
        { "<=1.1", "1.0.1", true },
        { "<=1.1", "1.0.1-0", true },
        { "<=1.1", "1.0.0", true },
        { "<=1.1", "1.0.0-0", true },
        { "<=1.1", "0.0.0", true },
        { "<=1.1", "0.0.0-0", true },
        { "<=1.0", "2.0.0", false },
        { "<=1.0", "2.0.0-0", false },
        { "<=1.0", "1.1.0", false },
        { "<=1.0", "1.1.0-0", false },
        { "<=1.0", "1.0.1", true },
        { "<=1.0", "1.0.1-0", true },
        { "<=1.0", "1.0.0", true },
        { "<=1.0", "1.0.0-0", true },
        { "<=1.0", "0.0.0", true },
        { "<=1.0", "0.0.0-0", true },

        // <= exact
        { "<=1.1.0", "2.0.0", false },
        { "<=1.1.0", "2.0.0-0", false },
        { "<=1.1.0", "1.1.0", true },
        { "<=1.1.0", "1.1.0-0", true },
        { "<=1.1.0", "1.0.1", true },
        { "<=1.1.0", "1.0.1-0", true },
        { "<=1.1.0", "1.0.0", true },
        { "<=1.1.0", "1.0.0-0", true },
        { "<=1.1.0", "0.0.0", true },
        { "<=1.1.0", "0.0.0-0", true },
        { "<=1.1.0-0", "2.0.0", false },
        { "<=1.1.0-0", "2.0.0-0", false },
        { "<=1.1.0-0", "1.1.0", false },
        { "<=1.1.0-0", "1.1.0-0", true },
        { "<=1.1.0-0", "1.0.1", true },
        { "<=1.1.0-0", "1.0.1-0", true },
        { "<=1.1.0-0", "1.0.0", true },
        { "<=1.1.0-0", "1.0.0-0", true },
        { "<=1.1.0-0", "0.0.0", true },
        { "<=1.1.0-0", "0.0.0-0", true },
        { "<=1.0.1", "2.0.0", false },
        { "<=1.0.1", "2.0.0-0", false },
        { "<=1.0.1", "1.1.0", false },
        { "<=1.0.1", "1.1.0-0", false },
        { "<=1.0.1", "1.0.1", true },
        { "<=1.0.1", "1.0.1-0", true },
        { "<=1.0.1", "1.0.0", true },
        { "<=1.0.1", "1.0.0-0", true },
        { "<=1.0.1", "0.0.0", true },
        { "<=1.0.1", "0.0.0-0", true },
        { "<=1.0.1-0", "2.0.0", false },
        { "<=1.0.1-0", "2.0.0-0", false },
        { "<=1.0.1-0", "1.1.0", false },
        { "<=1.0.1-0", "1.1.0-0", false },
        { "<=1.0.1-0", "1.0.1", false },
        { "<=1.0.1-0", "1.0.1-0", true },
        { "<=1.0.1-0", "1.0.0", true },
        { "<=1.0.1-0", "1.0.0-0", true },
        { "<=1.0.1-0", "0.0.0", true },
        { "<=1.0.1-0", "0.0.0-0", true },
        { "<=1.0.0", "2.0.0", false },
        { "<=1.0.0", "2.0.0-0", false },
        { "<=1.0.0", "1.1.0", false },
        { "<=1.0.0", "1.1.0-0", false },
        { "<=1.0.0", "1.0.1", false },
        { "<=1.0.0", "1.0.1-0", false },
        { "<=1.0.0", "1.0.0", true },
        { "<=1.0.0", "1.0.0-0", true },
        { "<=1.0.0", "0.0.0", true },
        { "<=1.0.0", "0.0.0-0", true },
        { "<=1.0.0-0", "2.0.0", false },
        { "<=1.0.0-0", "2.0.0-0", false },
        { "<=1.0.0-0", "1.1.0", false },
        { "<=1.0.0-0", "1.1.0-0", false },
        { "<=1.0.0-0", "1.0.1", false },
        { "<=1.0.0-0", "1.0.1-0", false },
        { "<=1.0.0-0", "1.0.0", false },
        { "<=1.0.0-0", "1.0.0-0", true },
        { "<=1.0.0-0", "0.0.0", true },
        { "<=1.0.0-0", "0.0.0-0", true },

        // https://github.com/microsoft/TypeScript/issues/50909
        { ">4.8", "4.9.0-beta", true },
        { ">=4.9", "4.9.0-beta", true },
        { "<4.9", "4.9.0-beta", false },
        { "<=4.8", "4.9.0-beta", false },
    };

    [Theory]
    [MemberData(nameof(ComparatorsData))]
    public void ComparatorsOfVersionRanges(string rangeText, string versionText, bool expected)
        => AssertRangeTest(rangeText, versionText, expected);

    // Go: TestConjunctionsOfVersionRanges
    public static TheoryData<string, string, bool> ConjunctionsData() => new()
    {
        { ">1.0.0 <2.0.0", "1.0.1", true },
        { ">1.0.0 <2.0.0", "2.0.0", false },
        { ">1.0.0 <2.0.0", "1.0.0", false },
        { ">1 >2", "3.0.0", true },
    };

    [Theory]
    [MemberData(nameof(ConjunctionsData))]
    public void ConjunctionsOfVersionRanges(string rangeText, string versionText, bool expected)
        => AssertRangeTest(rangeText, versionText, expected);

    // Go: TestDisjunctionsOfVersionRanges
    public static TheoryData<string, string, bool> DisjunctionsData() => new()
    {
        { ">1.0.0 || <1.0.0", "1.0.1", true },
        { ">1.0.0 || <1.0.0", "0.0.1", true },
        { ">1.0.0 || <1.0.0", "1.0.0", false },
        { ">1.0.0 || <1.0.0", "0.0.0", true },
        { ">=1.0.0 <2.0.0 || >=3.0.0 <4.0.0", "1.0.0", true },
        { ">=1.0.0 <2.0.0 || >=3.0.0 <4.0.0", "2.0.0", false },
        { ">=1.0.0 <2.0.0 || >=3.0.0 <4.0.0", "3.0.0", true },
    };

    [Theory]
    [MemberData(nameof(DisjunctionsData))]
    public void DisjunctionsOfVersionRanges(string rangeText, string versionText, bool expected)
        => AssertRangeTest(rangeText, versionText, expected);

    // Go: TestHyphensOfVersionRanges
    public static TheoryData<string, string, bool> HyphensData() => new()
    {
        { "1.0.0 - 2.0.0", "1.0.0", true },
        { "1.0.0 - 2.0.0", "1.0.1", true },
        { "1.0.0 - 2.0.0", "2.0.0", true },
        { "1.0.0 - 2.0.0", "2.0.1", false },
        { "1.0.0 - 2.0.0", "0.9.9", false },
        { "1.0.0 - 2.0.0", "3.0.0", false },
    };

    [Theory]
    [MemberData(nameof(HyphensData))]
    public void HyphensOfVersionRanges(string rangeText, string versionText, bool expected)
        => AssertRangeTest(rangeText, versionText, expected);

    // Go: TestTildesOfVersionRanges
    public static TheoryData<string, string, bool> TildesData() => new()
    {
        { "~0", "0.0.0", true },
        { "~0", "0.1.0", true },
        { "~0", "0.1.2", true },
        { "~0", "0.1.9", true },
        { "~0", "1.0.0", false },
        { "~0.1", "0.1.0", true },
        { "~0.1", "0.1.2", true },
        { "~0.1", "0.1.9", true },
        { "~0.1", "0.2.0", false },
        { "~0.1.2", "0.1.2", true },
        { "~0.1.2", "0.1.9", true },
        { "~0.1.2", "0.2.0", false },
        { "~1.0.0", "1.0.0", true },
        { "~1.0.0", "1.0.1", true },
        { "~1", "1.0.0", true },
        { "~1", "1.2.0", true },
        { "~1", "1.2.3", true },
        { "~1", "0.0.0", false },
        { "~1", "2.0.0", false },
        { "~1.2", "1.2.0", true },
        { "~1.2", "1.2.3", true },
        { "~1.2", "1.1.0", false },
        { "~1.2", "1.3.0", false },
        { "~1.2.3", "1.2.3", true },
        { "~1.2.3", "1.2.9", true },
        { "~1.2.3", "1.1.0", false },
        { "~1.2.3", "1.3.0", false },
    };

    [Theory]
    [MemberData(nameof(TildesData))]
    public void TildesOfVersionRanges(string rangeText, string versionText, bool expected)
        => AssertRangeTest(rangeText, versionText, expected);

    // Go: TestCaretsOfVersionRanges
    public static TheoryData<string, string, bool> CaretsData() => new()
    {
        { "^0", "0.0.0", true },
        { "^0", "0.1.0", true },
        { "^0", "0.9.0", true },
        { "^0", "0.1.2", true },
        { "^0", "0.1.9", true },
        { "^0", "1.0.0", false },
        { "^0.1", "0.1.0", true },
        { "^0.1", "0.1.2", true },
        { "^0.1", "0.1.9", true },
        { "^0.1.2", "0.1.2", true },
        { "^0.1.2", "0.1.9", true },
        { "^0.1.2", "0.0.0", false },
        { "^0.1.2", "0.2.0", false },
        { "^0.1.2", "1.0.0", false },
        { "^1", "1.0.0", true },
        { "^1", "1.2.0", true },
        { "^1", "1.2.3", true },
        { "^1", "1.9.0", true },
        { "^1", "0.0.0", false },
        { "^1", "2.0.0", false },
        { "^1.2", "1.2.0", true },
        { "^1.2", "1.2.3", true },
        { "^1.2", "1.9.0", true },
        { "^1.2", "1.1.0", false },
        { "^1.2", "2.0.0", false },
        { "^1.2.3", "1.2.3", true },
        { "^1.2.3", "1.9.0", true },
        { "^1.2.3", "1.2.2", false },
        { "^1.2.3", "2.0.0", false },
    };

    [Theory]
    [MemberData(nameof(CaretsData))]
    public void CaretsOfVersionRanges(string rangeText, string versionText, bool expected)
        => AssertRangeTest(rangeText, versionText, expected);

    // Go: assertRangesGoodBad (testGoodBad struct becomes the good/bad parameters)
    private static void AssertRangesGoodBad(string versionRangeString, string[] good, string[] bad)
    {
        Assert.True(Semver.TryParseVersionRange(versionRangeString, out VersionRange versionRange));
        foreach (string g in good)
        {
            Assert.True(Semver.TryParseVersion(g, out Version v));
            Assert.True(versionRange.Test(v), $"{g} should be matched by range {versionRangeString}");
        }

        foreach (string b in bad)
        {
            Assert.True(Semver.TryParseVersion(b, out Version v));
            Assert.False(versionRange.Test(v), $"{b} should not be matched by range {versionRangeString}");
        }
    }

    // Go: assertRangeTest (the name/inRange context lives in the theory display name)
    private static void AssertRangeTest(string rangeText, string versionText, bool inRange)
    {
        Assert.True(Semver.TryParseVersionRange(rangeText, out VersionRange versionRange));
        Assert.True(Semver.TryParseVersion(versionText, out Version version)); // Go: assert.NilError(t, err)
        Assert.Equal(inRange, versionRange.Test(version));
    }
}
