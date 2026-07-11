// Ported from github.com/peter-evans/patience v0.3.0 test suite (patience_test.go, lcs_test.go,
// format_test.go, unified_test.go, patience_api_test.go). These are the byte-for-byte validators
// for Ts.TestUtil.Diff.Patience, which must match Go's diff output exactly (plan/08 §4.6).
//
// The two large "canonical" api_test cases (TestPatienceCanonical / TestPatienceUnified) exercise
// the same public paths (Diff -> DiffText, Diff -> UnifiedDiffTextWithOptions) as the README
// examples below; the granular Diff/makeHunks/format cases here cover every branch, so those two
// bulk fixtures are represented by ReadmePlainExample / ReadmeUnifiedExample.

using System.Collections.Generic;
using Ts.TestUtil.Diff;
using Xunit;

namespace Ts.TestUtil.Tests;

public class PatienceTests
{
    private static DiffLine E(string t) => new(t, DiffType.Equal);

    private static DiffLine Ins(string t) => new(t, DiffType.Insert);

    private static DiffLine Del(string t) => new(t, DiffType.Delete);

    // Go: Test_uniqueElements
    [Fact]
    public void UniqueElements()
    {
        (List<string> outEvery, List<int> idxEvery) = Patience.UniqueElements(new[] { "a", "b", "c" });
        Assert.Equal(new[] { "a", "b", "c" }, outEvery);
        Assert.Equal(new[] { 0, 1, 2 }, idxEvery);

        (List<string> outDup, List<int> idxDup) = Patience.UniqueElements(new[] { "a", "b", "a", "c" });
        Assert.Equal(new[] { "b", "c" }, outDup);
        Assert.Equal(new[] { 1, 3 }, idxDup);

        (List<string> outNone, List<int> idxNone) = Patience.UniqueElements(new[] { "a", "b", "a", "c", "c", "b" });
        Assert.Empty(outNone);
        Assert.Empty(idxNone);
    }

    // Go: TestDiff
    [Fact]
    public void Diff()
    {
        Assert.Empty(Patience.Diff(new string[] { }, new string[] { }));

        Assert.Equal(
            new List<DiffLine> { Ins("a") },
            Patience.Diff(new string[] { }, new[] { "a" }));

        Assert.Equal(
            new List<DiffLine> { Del("a") },
            Patience.Diff(new[] { "a" }, new string[] { }));

        Assert.Equal(
            new List<DiffLine> { E("a") },
            Patience.Diff(new[] { "a" }, new[] { "a" }));

        // Equal elements at the head.
        Assert.Equal(
            new List<DiffLine> { E("a"), Del("b"), Ins("c") },
            Patience.Diff(new[] { "a", "b" }, new[] { "a", "c" }));

        // Equal elements at the tail.
        Assert.Equal(
            new List<DiffLine> { Del("a"), Ins("b"), E("c") },
            Patience.Diff(new[] { "a", "c" }, new[] { "b", "c" }));

        // Equal at head and tail.
        Assert.Equal(
            new List<DiffLine> { E("a"), Del("b"), Ins("d"), E("c") },
            Patience.Diff(new[] { "a", "b", "c" }, new[] { "a", "d", "c" }));

        // Diffing the gaps between LCS elements.
        Assert.Equal(
            new List<DiffLine> { E("a"), Del("w"), Ins("y"), E("b"), Del("x"), Ins("z"), E("c") },
            Patience.Diff(new[] { "a", "w", "b", "x", "c" }, new[] { "a", "y", "b", "z", "c" }));
    }

    // Go: TestLCS
    [Fact]
    public void Lcs()
    {
        Assert.Equal(
            new List<(int, int)> { (0, 0), (1, 1), (2, 2) },
            Patience.Lcs(new[] { "a", "b", "c" }, new[] { "a", "b", "c" }));

        Assert.Equal(
            new List<(int, int)> { (0, 0), (2, 1), (3, 4) },
            Patience.Lcs(new[] { "a", "z", "b", "c" }, new[] { "a", "b", "y", "w", "c" }));

        Assert.Equal(
            new List<(int, int)> { (1, 1), (2, 2), (4, 3), (5, 4) },
            Patience.Lcs(new[] { "a", "b", "a", "y", "c", "c" }, new[] { "z", "b", "a", "c", "c", "b" }));
    }

    // Go: TestDiffText
    [Fact]
    public void DiffText()
    {
        var diffs = new List<DiffLine> { E("a"), Ins("b"), E("c"), E(""), Del("d"), E("e") };
        Assert.Equal(" a\n+b\n c\n\n-d\n e", Patience.DiffText(diffs));
    }

    // Go: TestDiffTextA
    [Fact]
    public void DiffTextA()
    {
        var diffs = new List<DiffLine> { E("a"), Ins("b"), E("c"), E(""), Del("d"), E("e") };
        Assert.Equal(" a\n c\n\n-d\n e", Patience.DiffTextA(diffs));
    }

    // Go: TestDiffTextB
    [Fact]
    public void DiffTextB()
    {
        var diffs = new List<DiffLine> { E("a"), Ins("b"), E("c"), E(""), E("e") };
        Assert.Equal(" a\n+b\n c\n\n e", Patience.DiffTextB(diffs));
    }

    // Go: TestUnifiedDiffTextWithOptions
    [Fact]
    public void UnifiedDiffTextWithOptions()
    {
        var multi = new List<DiffLine>
        {
            E("a"), E("b"), Ins("c"), E("d"), E("e"), E("f"),
            Del("g"), Ins("h"), E("i"), Ins("j"), E("k"), E("l"),
        };
        Assert.Equal(
            "@@ -2,2 +2,3 @@\n b\n+c\n d\n@@ -5,4 +6,5 @@\n f\n-g\n+h\n i\n+j\n k",
            Patience.UnifiedDiffTextWithOptions(multi, new UnifiedDiffOptions { Precontext = 1, Postcontext = 1 }));

        var headers = new List<DiffLine> { E("a"), E("b"), Ins("c"), E("") };
        Assert.Equal(
            "--- a.txt\n+++ b.txt\n@@ -2,2 +2,3 @@\n b\n+c\n",
            Patience.UnifiedDiffTextWithOptions(headers, new UnifiedDiffOptions
            {
                Precontext = 1,
                Postcontext = 1,
                SrcHeader = "a.txt",
                DstHeader = "b.txt",
            }));
    }

    // Go: Test_makeHunks
    [Fact]
    public void MakeHunks()
    {
        DiffLine e = E("e"), i = Ins("i"), d = Del("d");

        Assert.Empty(Patience.MakeHunks(new List<DiffLine>(), 2, 2));

        Assert.Empty(Patience.MakeHunks(new List<DiffLine> { e, e, e, e }, 2, 2));

        AssertHunks(
            new[] { H(new[] { i, i, i, i, d, d, d, d }, 1, 4, 1, 4) },
            Patience.MakeHunks(new List<DiffLine> { i, i, i, i, d, d, d, d }, 2, 2));

        AssertHunks(
            new[] { H(new[] { d, d, d, d }, 1, 4, 0, 0) },
            Patience.MakeHunks(new List<DiffLine> { d, d, d, d }, 2, 2));

        AssertHunks(
            new[] { H(new[] { i, i, i, i }, 0, 0, 1, 4) },
            Patience.MakeHunks(new List<DiffLine> { i, i, i, i }, 2, 2));

        AssertHunks(
            new[] { H(new[] { d, d, e, e }, 1, 4, 1, 2) },
            Patience.MakeHunks(new List<DiffLine> { d, d, e, e, e }, 2, 2));

        AssertHunks(
            new[] { H(new[] { i, i, e, e }, 1, 2, 1, 4) },
            Patience.MakeHunks(new List<DiffLine> { i, i, e, e, e }, 2, 2));

        AssertHunks(
            new[]
            {
                H(new[] { d, d, e, e, e }, 1, 5, 1, 3),
                H(new[] { e, e, e, d, d }, 7, 5, 5, 3),
            },
            Patience.MakeHunks(new List<DiffLine> { d, d, e, e, e, e, e, e, e, d, d }, 3, 3));

        AssertHunks(
            new[]
            {
                H(new[] { i, i, e, e, e }, 1, 3, 1, 5),
                H(new[] { e, e, e, i, i }, 5, 3, 7, 5),
            },
            Patience.MakeHunks(new List<DiffLine> { i, i, e, e, e, e, e, e, e, i, i }, 3, 3));

        AssertHunks(
            new[]
            {
                H(new[] { e, e, d, i, i, e, e }, 2, 5, 2, 6),
                H(new[] { e, e, d, i, e, e }, 8, 5, 9, 5),
            },
            Patience.MakeHunks(new List<DiffLine> { e, e, e, d, i, i, e, e, e, e, e, d, i, e, e, e }, 2, 2));

        AssertHunks(
            new[] { H(new[] { e, d, i, e, d, i, e }, 1, 5, 1, 5) },
            Patience.MakeHunks(new List<DiffLine> { e, d, i, e, d, i, e }, 2, 2));

        AssertHunks(
            new[] { H(new[] { d, i, e, e, e, e, d, i }, 1, 6, 1, 6) },
            Patience.MakeHunks(new List<DiffLine> { d, i, e, e, e, e, d, i }, 2, 2));

        AssertHunks(
            new[]
            {
                H(new[] { d, i, e, e, e, e, d, d, i, e, e, e, d, i, e, e }, 1, 13, 1, 12),
                H(new[] { e, e, d, i, e, e }, 15, 5, 14, 5),
            },
            Patience.MakeHunks(
                new List<DiffLine> { d, i, e, e, e, e, d, d, i, e, e, e, d, i, e, e, e, e, e, d, i, e, e, e },
                2, 2));

        AssertHunks(
            new[]
            {
                H(new[] { d, i, e }, 1, 2, 1, 2),
                H(new[] { e, e, d, d, i, e, e, e, d, d, i, e }, 4, 10, 4, 8),
                H(new[] { e, e, d, i, e }, 16, 4, 14, 4),
            },
            Patience.MakeHunks(
                new List<DiffLine> { d, i, e, e, e, e, d, d, i, e, e, e, d, d, i, e, e, e, e, e, d, i, e, e, e },
                2, 1));

        AssertHunks(
            new[]
            {
                H(new[] { d, i }, 1, 1, 1, 1),
                H(new[] { d, i }, 6, 1, 6, 1),
            },
            Patience.MakeHunks(new List<DiffLine> { d, i, e, e, e, e, d, i, e, e, e }, 0, 0));

        AssertHunks(
            new[]
            {
                H(new[] { E("2"), E("3"), Del("4"), Ins("5"), Ins("6"), E("7"), E("8") }, 2, 5, 2, 6),
                H(new[] { E("10"), E("11"), Del("12"), Ins("13"), E("14"), E("15") }, 8, 5, 9, 5),
            },
            Patience.MakeHunks(
                new List<DiffLine>
                {
                    E("1"), E("2"), E("3"), Del("4"), Ins("5"), Ins("6"), E("7"), E("8"),
                    E("9"), E("10"), E("11"), Del("12"), Ins("13"), E("14"), E("15"), E("16"),
                },
                2, 2));
    }

    // Go: TestPlainDiffReadmeExample
    [Fact]
    public void ReadmePlainExample()
    {
        string[] a = "the\nquick\nbrown\nchicken\njumps\nover\nthe\ndog".Split('\n');
        string[] b = "the\nquick\nbrown\nfox\njumps\nover\nthe\nlazy\ndog".Split('\n');

        Assert.Equal(
            " the\n quick\n brown\n-chicken\n+fox\n jumps\n over\n the\n+lazy\n dog",
            Patience.DiffText(Patience.Diff(a, b)));
    }

    // Go: TestUnifiedDiffReadmeExample
    [Fact]
    public void ReadmeUnifiedExample()
    {
        string[] a = "the\nquick\nbrown\nchicken\njumps\nover\nthe\ndog".Split('\n');
        string[] b = "the\nquick\nbrown\nfox\njumps\nover\nthe\nlazy\ndog".Split('\n');

        Assert.Equal(
            "--- a.txt\n+++ b.txt\n@@ -3,3 +3,3 @@\n brown\n-chicken\n+fox\n jumps\n@@ -7,2 +7,3 @@\n the\n+lazy\n dog",
            Patience.UnifiedDiffTextWithOptions(
                Patience.Diff(a, b),
                new UnifiedDiffOptions { Precontext = 1, Postcontext = 1, SrcHeader = "a.txt", DstHeader = "b.txt" }));
    }

    private static Hunk H(IReadOnlyList<DiffLine> diffs, int srcStart, int srcLines, int dstStart, int dstLines) =>
        new()
        {
            Diffs = new List<DiffLine>(diffs),
            SrcStart = srcStart,
            SrcLines = srcLines,
            DstStart = dstStart,
            DstLines = dstLines,
        };

    private static void AssertHunks(IReadOnlyList<Hunk> expected, IReadOnlyList<Hunk> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int k = 0; k < expected.Count; k++)
        {
            Assert.Equal(expected[k].SrcStart, actual[k].SrcStart);
            Assert.Equal(expected[k].SrcLines, actual[k].SrcLines);
            Assert.Equal(expected[k].DstStart, actual[k].DstStart);
            Assert.Equal(expected[k].DstLines, actual[k].DstLines);
            Assert.Equal(expected[k].Diffs, actual[k].Diffs);
        }
    }
}
