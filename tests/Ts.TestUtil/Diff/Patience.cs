// Ported from github.com/peter-evans/patience v0.3.0 (patience.go, lcs.go, unified.go,
// format.go). tsgo pins this module for baseline diffing (internal/testutil/baseline:
// DiffText). plan/08-testing-strategy.md §4.6 mandates a byte-for-byte algorithm port
// (NOT DiffPlex or another library) because reference/submodule*/**.diff baselines are
// synced from tsgo and our .diff output must match Go's patience formatter exactly.
//
// PORT-NOTES:
//   - Go slices of value structs ([]DiffLine) become List<DiffLine> with copy-on-append.
//     Go's slice-header aliasing (e.g. `Hunk{Diffs: block.Diffs}` sharing a backing array)
//     is safe here because `block` is single-use: once passed to updateHunks it is replaced
//     by the caller, so appending into a shared list mutates no other alias. Final contents
//     are therefore identical to Go.
//   - Hunk is a class (not a struct) so `hunks[curHunk].SrcLines += ...` in-place field
//     mutation ports directly (a List<struct> forbids it).
//   - Integer formatting in the unified-diff header uses InvariantCulture explicitly
//     (CA1305), matching Go's fmt.Sprintf which is culture-agnostic.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ts.TestUtil.Diff;

// Go: DiffType defines the type of a diff element (int8).
public enum DiffType : sbyte
{
    // Go: Delete represents a diff delete operation.
    Delete = -1,

    // Go: Insert represents a diff insert operation.
    Insert = 1,

    // Go: Equal represents no diff.
    Equal = 0,
}

// Go: DiffLine represents a single line and its diff type.
public readonly record struct DiffLine(string Text, DiffType Type);

// Go: Hunk represents a subsection of a diff.
public sealed class Hunk
{
    public List<DiffLine> Diffs { get; set; } = new();
    public int SrcStart { get; set; }
    public int SrcLines { get; set; }
    public int DstStart { get; set; }
    public int DstLines { get; set; }
}

// Go: UnifiedDiffOptions represents the options for UnifiedDiffTextWithOptions.
public struct UnifiedDiffOptions
{
    // Go: Precontext is the number of lines of context before each change in a hunk.
    public int Precontext { get; set; }

    // Go: Postcontext is the number of lines of context after each change in a hunk.
    public int Postcontext { get; set; }

    // Go: SrcHeader is the header for the source file.
    public string SrcHeader { get; set; }

    // Go: DstHeader is the header for the destination file.
    public string DstHeader { get; set; }
}

// Package patience implements the Patience Diff algorithm.
public static class Patience
{
    // Go (patience.go): toDiffLines converts a slice of strings to a slice of DiffLines
    // with the specified diff type.
    internal static List<DiffLine> ToDiffLines(IReadOnlyList<string> a, DiffType t)
    {
        var diffs = new List<DiffLine>(a.Count);
        for (int i = 0; i < a.Count; i++)
        {
            diffs.Add(new DiffLine(a[i], t));
        }
        return diffs;
    }

    // Go (patience.go): uniqueElements returns a slice of unique elements from a slice of
    // strings, and a slice of the original indices of each element.
    internal static (List<string> Elements, List<int> Indices) UniqueElements(IReadOnlyList<string> a)
    {
        var m = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string e in a)
        {
            m.TryGetValue(e, out int count);
            m[e] = count + 1;
        }
        var elements = new List<string>();
        var indices = new List<int>();
        for (int i = 0; i < a.Count; i++)
        {
            if (m[a[i]] == 1)
            {
                elements.Add(a[i]);
                indices.Add(i);
            }
        }
        return (elements, indices);
    }

    // Go (patience.go): Diff returns the patience diff of two slices of strings.
    public static List<DiffLine> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return new List<DiffLine>();
        }
        if (a.Count == 0)
        {
            return ToDiffLines(b, DiffType.Insert);
        }
        if (b.Count == 0)
        {
            return ToDiffLines(a, DiffType.Delete);
        }

        // Find equal elements at the head of slices a and b.
        int i = 0;
        while (i < a.Count && i < b.Count && a[i] == b[i])
        {
            i++;
        }
        if (i > 0)
        {
            var result = ToDiffLines(Slice(a, 0, i), DiffType.Equal);
            result.AddRange(Diff(Slice(a, i, a.Count), Slice(b, i, b.Count)));
            return result;
        }

        // Find equal elements at the tail of slices a and b.
        int j = 0;
        while (j < a.Count && j < b.Count && a[a.Count - 1 - j] == b[b.Count - 1 - j])
        {
            j++;
        }
        if (j > 0)
        {
            var result = Diff(Slice(a, 0, a.Count - j), Slice(b, 0, b.Count - j));
            result.AddRange(ToDiffLines(Slice(a, a.Count - j, a.Count), DiffType.Equal));
            return result;
        }

        // Find the longest common subsequence of unique elements in a and b.
        (List<string> ua, List<int> idxa) = UniqueElements(a);
        (List<string> ub, List<int> idxb) = UniqueElements(b);
        List<(int, int)> lcs = Lcs(ua, ub);

        // If the LCS is empty, the diff is all deletions and insertions.
        if (lcs.Count == 0)
        {
            var result = ToDiffLines(a, DiffType.Delete);
            result.AddRange(ToDiffLines(b, DiffType.Insert));
            return result;
        }

        // Lookup the original indices of slices a and b.
        for (int k = 0; k < lcs.Count; k++)
        {
            (int x0, int x1) = lcs[k];
            lcs[k] = (idxa[x0], idxb[x1]);
        }

        var diffs = new List<DiffLine>();
        int ga = 0, gb = 0;
        foreach ((int ip0, int ip1) in lcs)
        {
            // Diff the gaps between the lcs elements.
            diffs.AddRange(Diff(Slice(a, ga, ip0), Slice(b, gb, ip1)));
            // Append the LCS elements to the diff.
            diffs.Add(new DiffLine(a[ip0], DiffType.Equal));
            ga = ip0 + 1;
            gb = ip1 + 1;
        }
        // Diff the remaining elements of a and b after the final LCS element.
        diffs.AddRange(Diff(Slice(a, ga, a.Count), Slice(b, gb, b.Count)));

        return diffs;
    }

    // Go (lcs.go): LCS computes the longest common subsequence of two string slices and
    // returns the index pairs of the LCS.
    public static List<(int, int)> Lcs(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Initialize the LCS table.
        var lcs = new int[a.Count + 1][];
        for (int i = 0; i <= a.Count; i++)
        {
            lcs[i] = new int[b.Count + 1];
        }

        // Populate the LCS table.
        for (int i = 1; i < lcs.Length; i++)
        {
            for (int j = 1; j < lcs[i].Length; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    lcs[i][j] = lcs[i - 1][j - 1] + 1;
                }
                else
                {
                    lcs[i][j] = Math.Max(lcs[i - 1][j], lcs[i][j - 1]);
                }
            }
        }

        // Backtrack to find the LCS.
        int ii = a.Count, jj = b.Count;
        var s = new List<(int, int)>(lcs[ii][jj]);
        while (ii > 0 && jj > 0)
        {
            if (a[ii - 1] == b[jj - 1])
            {
                s.Add((ii - 1, jj - 1));
                ii--;
                jj--;
            }
            else if (lcs[ii - 1][jj] > lcs[ii][jj - 1])
            {
                ii--;
            }
            else
            {
                jj--;
            }
        }

        // Reverse the backtracked LCS.
        s.Reverse();

        return s;
    }

    // Go (unified.go): makeHunks returns the hunks of a diff.
    internal static List<Hunk> MakeHunks(IReadOnlyList<DiffLine> diffs, int precontext, int postcontext)
    {
        if (diffs.Count == 0)
        {
            return new List<Hunk>();
        }

        var hunks = new List<Hunk>();

        // Update hunks with a diff block.
        void UpdateHunks(Hunk block, bool lastBlock)
        {
            int curHunk = hunks.Count - 1;
            if (block.Diffs[0].Type == DiffType.Equal)
            {
                // Unmodified block.
                if (hunks.Count == 0)
                {
                    // Start a new hunk with the tail of the block.
                    int ctxLen = Math.Min(precontext, block.Diffs.Count);
                    hunks.Add(new Hunk
                    {
                        Diffs = block.Diffs.GetRange(block.Diffs.Count - ctxLen, ctxLen),
                        SrcStart = block.Diffs.Count - ctxLen + block.SrcStart,
                        SrcLines = ctxLen,
                        DstStart = block.Diffs.Count - ctxLen + block.DstStart,
                        DstLines = ctxLen,
                    });
                }
                else
                {
                    // Update the current hunk.
                    int maxNonContext = precontext + postcontext;
                    if (lastBlock)
                    {
                        maxNonContext = postcontext;
                    }
                    if (block.Diffs.Count <= maxNonContext)
                    {
                        // Block is small enough to be appended to the current hunk.
                        hunks[curHunk].Diffs.AddRange(block.Diffs);
                        hunks[curHunk].SrcLines += block.Diffs.Count;
                        hunks[curHunk].DstLines += block.Diffs.Count;
                    }
                    else
                    {
                        // Append the head of the block to the current hunk.
                        hunks[curHunk].Diffs.AddRange(block.Diffs.GetRange(0, postcontext));
                        hunks[curHunk].SrcLines += postcontext;
                        hunks[curHunk].DstLines += postcontext;
                        if (!lastBlock)
                        {
                            // Start a new hunk with the tail of the block.
                            hunks.Add(new Hunk
                            {
                                Diffs = block.Diffs.GetRange(block.Diffs.Count - precontext, precontext),
                                SrcStart = block.Diffs.Count - precontext + block.SrcStart,
                                SrcLines = precontext,
                                DstStart = block.Diffs.Count - precontext + block.DstStart,
                                DstLines = precontext,
                            });
                        }
                    }
                    // Update starting line numbers if the current hunk had no source or destination diff.
                    if (hunks[curHunk].SrcStart == 0)
                    {
                        hunks[curHunk].SrcStart = block.SrcStart;
                    }
                    if (hunks[curHunk].DstStart == 0)
                    {
                        hunks[curHunk].DstStart = block.DstStart;
                    }
                }
            }
            else
            {
                // Modified block.
                if (hunks.Count > 0)
                {
                    hunks[curHunk].Diffs.AddRange(block.Diffs);
                    hunks[curHunk].SrcLines += block.SrcLines;
                    hunks[curHunk].DstLines += block.DstLines;
                }
                else
                {
                    hunks.Add(new Hunk
                    {
                        Diffs = block.Diffs,
                        SrcStart = block.SrcStart,
                        SrcLines = block.SrcLines,
                        DstStart = block.DstStart,
                        DstLines = block.DstLines,
                    });
                }
            }
        }

        // Aggregate blocks of modified and unmodified diff lines, creating
        // or updating hunks after each block.
        var block = new Hunk();
        int modifiedLines = 0;
        int srcLineNum = 0, dstLineNum = 0;
        foreach (DiffLine l in diffs)
        {
            if (block.Diffs.Count == 0 ||
                block.Diffs[0].Type == l.Type ||
                (block.Diffs[0].Type != l.Type && block.Diffs[0].Type != DiffType.Equal && l.Type != DiffType.Equal))
            {
                block.Diffs.Add(l);
            }
            else
            {
                UpdateHunks(block, false);
                block = new Hunk { Diffs = new List<DiffLine> { l } };
            }

            switch (l.Type)
            {
                case DiffType.Delete:
                    srcLineNum++;
                    block.SrcLines++;
                    modifiedLines++;
                    break;
                case DiffType.Insert:
                    dstLineNum++;
                    block.DstLines++;
                    modifiedLines++;
                    break;
                case DiffType.Equal:
                    srcLineNum++;
                    dstLineNum++;
                    block.SrcLines++;
                    block.DstLines++;
                    break;
            }

            if (block.SrcStart == 0 && (l.Type == DiffType.Equal || l.Type == DiffType.Delete))
            {
                block.SrcStart = srcLineNum;
            }
            if (block.DstStart == 0 && (l.Type == DiffType.Equal || l.Type == DiffType.Insert))
            {
                block.DstStart = dstLineNum;
            }
        }
        UpdateHunks(block, true);

        // Return no hunks if the diffs contain only equal lines.
        if (modifiedLines == 0)
        {
            return new List<Hunk>();
        }

        return hunks;
    }

    // Go (format.go): typeSymbol returns the associated symbol of a DiffType.
    internal static string TypeSymbol(DiffType t) => t switch
    {
        DiffType.Equal => " ",
        DiffType.Insert => "+",
        DiffType.Delete => "-",
        _ => throw new InvalidOperationException("unknown DiffType"),
    };

    // Go (format.go): DiffText returns the source and destination texts (all equalities,
    // insertions and deletions).
    public static string DiffText(IReadOnlyList<DiffLine> diffs)
    {
        var s = new string[diffs.Count];
        for (int i = 0; i < diffs.Count; i++)
        {
            DiffLine l = diffs[i];
            if (l.Text.Length == 0 && l.Type == DiffType.Equal)
            {
                s[i] = "";
                continue;
            }
            s[i] = $"{TypeSymbol(l.Type)}{l.Text}";
        }
        return string.Join("\n", s);
    }

    // Go (format.go): DiffTextA returns the source text (all equalities and deletions).
    public static string DiffTextA(IReadOnlyList<DiffLine> diffs)
    {
        var s = new List<string>();
        foreach (DiffLine l in diffs)
        {
            if (l.Type == DiffType.Insert)
            {
                continue;
            }
            if (l.Type == DiffType.Equal && l.Text.Length == 0)
            {
                s.Add("");
            }
            else
            {
                s.Add($"{TypeSymbol(l.Type)}{l.Text}");
            }
        }
        return string.Join("\n", s);
    }

    // Go (format.go): DiffTextB returns the destination text (all equalities and insertions).
    public static string DiffTextB(IReadOnlyList<DiffLine> diffs)
    {
        var s = new List<string>();
        foreach (DiffLine l in diffs)
        {
            if (l.Type == DiffType.Delete)
            {
                continue;
            }
            if (l.Type == DiffType.Equal && l.Text.Length == 0)
            {
                s.Add("");
            }
            else
            {
                s.Add($"{TypeSymbol(l.Type)}{l.Text}");
            }
        }
        return string.Join("\n", s);
    }

    // Go (format.go): UnifiedDiffTextWithOptions returns the diff text in unidiff format.
    public static string UnifiedDiffTextWithOptions(IReadOnlyList<DiffLine> diffs, UnifiedDiffOptions opts)
    {
        List<Hunk> hunks = MakeHunks(diffs, opts.Precontext, opts.Postcontext);
        var s = new List<string>();
        if (!string.IsNullOrEmpty(opts.SrcHeader))
        {
            s.Add($"--- {opts.SrcHeader}");
        }
        if (!string.IsNullOrEmpty(opts.DstHeader))
        {
            s.Add($"+++ {opts.DstHeader}");
        }
        foreach (Hunk h in hunks)
        {
            s.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"@@ -{h.SrcStart},{h.SrcLines} +{h.DstStart},{h.DstLines} @@"));
            foreach (DiffLine l in h.Diffs)
            {
                if (l.Type == DiffType.Equal && l.Text.Length == 0)
                {
                    s.Add("");
                }
                else
                {
                    s.Add($"{TypeSymbol(l.Type)}{l.Text}");
                }
            }
        }
        return string.Join("\n", s);
    }

    // Go (format.go): UnifiedDiffText returns the diff text in unidiff format with a context
    // of 3 lines.
    public static string UnifiedDiffText(IReadOnlyList<DiffLine> diffs) =>
        UnifiedDiffTextWithOptions(diffs, new UnifiedDiffOptions { Precontext = 3, Postcontext = 3 });

    // Go slices `a[lo:hi]` are views; here we materialize the sub-range. Callers only read
    // the result, so a copy is behaviorally identical.
    private static List<string> Slice(IReadOnlyList<string> a, int lo, int hi)
    {
        var result = new List<string>(hi - lo);
        for (int i = lo; i < hi; i++)
        {
            result.Add(a[i]);
        }
        return result;
    }
}
