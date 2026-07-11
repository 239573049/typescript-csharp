using System;
using System.Collections.Generic;
using System.Linq;
using Ts.Core;
using Xunit;

namespace Ts.Core.Tests;

public class CoreTests
{
    // Reference type with NO Equals override — its equality is object identity, exactly
    // like a ported AST Node (05 §6.2: AST/Type classes must not override Equals).
    private sealed class Box
    {
    }

    // ---- SameMap / SameMapIndex: "no change ⇒ same reference" is load-bearing ----

    [Fact]
    public void SameMap_ReturnsSameReferenceWhenUnchanged()
    {
        var input = new[] { "a", "b", "c" };
        var result = CoreUtil.SameMap(input, x => x);
        Assert.Same(input, result);
    }

    [Fact]
    public void SameMap_ReturnsNewArrayWhenChanged()
    {
        var input = new[] { "a", "b", "c" };
        var result = CoreUtil.SameMap(input, x => x == "b" ? "B" : x);
        Assert.NotSame(input, result);
        Assert.Equal(new[] { "a", "B", "c" }, result);
    }

    [Fact]
    public void SameMap_UsesReferenceIdentityForClassesWithoutEquals()
    {
        var a = new Box();
        var b = new Box();
        var input = new[] { a, b };

        // Identity map: every element maps to itself ⇒ original reference back.
        Assert.Same(input, CoreUtil.SameMap(input, x => x));

        // Replace the first element with a *different* instance ⇒ fresh array, tail kept.
        var c = new Box();
        var changed = CoreUtil.SameMap(input, x => ReferenceEquals(x, a) ? c : x);
        Assert.NotSame(input, changed);
        Assert.Same(c, changed![0]);
        Assert.Same(b, changed[1]);
    }

    [Fact]
    public void SameMap_NullStaysNull()
    {
        Assert.Null(CoreUtil.SameMap<string>(null, x => x));
    }

    [Fact]
    public void SameMapIndex_UsesIndexAndPreservesIdentity()
    {
        var input = new[] { 10, 20, 30 };
        Assert.Same(input, CoreUtil.SameMapIndex(input, (v, _) => v));

        var changed = CoreUtil.SameMapIndex(input, (v, i) => i == 1 ? v + i : v);
        Assert.NotSame(input, changed);
        Assert.Equal(new[] { 10, 21, 30 }, changed);
    }

    // ---- ComputeECMALineStarts: \r\n / \r / \n / U+2028 / U+2029, UTF-16 offsets ----

    [Theory]
    [InlineData("", new[] { 0 })]
    [InlineData("abc", new[] { 0 })]
    [InlineData("a\nb", new[] { 0, 2 })]
    [InlineData("a\r\nb", new[] { 0, 3 })] // CRLF consumed together; next line after \n
    [InlineData("a\rb", new[] { 0, 2 })] // lone CR is a line break
    [InlineData("\n\n", new[] { 0, 1, 2 })]
    [InlineData("a\r\n", new[] { 0, 3 })] // trailing CRLF, empty final line at 3
    [InlineData("line1\r\nline2\nline3", new[] { 0, 7, 13 })]
    public void ComputeECMALineStarts_HandlesLineTerminators(string text, int[] expected)
    {
        Assert.Equal(expected, CoreUtil.ComputeECMALineStarts(text));
    }

    [Fact]
    public void ComputeECMALineStarts_UnicodeLineSeparatorsAreSingleUtf16Units()
    {
        // U+2028 (LS) and U+2029 (PS) are BMP ⇒ one UTF-16 code unit each; offsets are
        // UTF-16 code units, not UTF-8 bytes (Go would report byte offsets here).
        Assert.Equal(new[] { 0, 2 }, CoreUtil.ComputeECMALineStarts("a\u2028b"));
        Assert.Equal(new[] { 0, 2 }, CoreUtil.ComputeECMALineStarts("a\u2029b"));
    }

    [Fact]
    public void ComputeECMALineStarts_SupplementaryCharactersAdvanceByTwoUnits()
    {
        // U+1F600 is a surrogate pair (2 UTF-16 code units); the line after the newline
        // starts at code-unit offset 3 (emoji=2 + '\n'=1).
        Assert.Equal(new[] { 0, 3 }, CoreUtil.ComputeECMALineStarts("\U0001F600\nx"));
    }

    [Fact]
    public void PositionToLineAndByteOffset_MapsPositionToLineAndOffset()
    {
        int[] starts = CoreUtil.ComputeECMALineStarts("ab\ncd\nef");
        Assert.Equal(new[] { 0, 3, 6 }, starts);
        Assert.Equal((0, 0), CoreUtil.PositionToLineAndByteOffset(0, starts));
        Assert.Equal((0, 2), CoreUtil.PositionToLineAndByteOffset(2, starts));
        Assert.Equal((1, 0), CoreUtil.PositionToLineAndByteOffset(3, starts));
        Assert.Equal((2, 1), CoreUtil.PositionToLineAndByteOffset(7, starts));
    }

    // ---- Levenshtein-based spelling suggestion ----

    [Fact]
    public void GetSpellingSuggestion_ReturnsClosestCandidate()
    {
        Assert.Equal("color", CoreUtil.GetSpellingSuggestionForStrings("colour", new[] { "color", "other" }));
    }

    [Fact]
    public void GetSpellingSuggestion_ReturnsEmptyWhenNothingCloseEnough()
    {
        Assert.Equal("", CoreUtil.GetSpellingSuggestionForStrings("xyzabc", new[] { "color", "height" }));
    }

    [Fact]
    public void GetSpellingSuggestion_SkipsExactMatch()
    {
        // An exact match is not a "did you mean" suggestion.
        Assert.Equal("", CoreUtil.GetSpellingSuggestionForStrings("width", new[] { "width" }));
    }

    [Fact]
    public void GetSpellingSuggestion_ShortNamesOnlyMatchByCase()
    {
        // Length < 3 candidates only qualify if they differ by case (EqualFold).
        Assert.Equal("ID", CoreUtil.GetSpellingSuggestionForStrings("id", new[] { "ID" }));
        Assert.Equal("", CoreUtil.GetSpellingSuggestionForStrings("id", new[] { "of" }));
    }

    // ---- Slice helpers ----

    [Fact]
    public void Filter_ReturnsSameReferenceWhenNothingRemoved()
    {
        var input = new[] { 1, 2, 3 };
        Assert.Same(input, CoreUtil.Filter(input, _ => true));
    }

    [Fact]
    public void Filter_RemovesNonMatching()
    {
        var input = new[] { 1, 2, 3, 4 };
        Assert.Equal(new[] { 2, 4 }, CoreUtil.Filter(input, x => x % 2 == 0));
    }

    [Fact]
    public void Map_NullInputReturnsNull()
    {
        Assert.Null(CoreUtil.Map<int, int>(null, x => x));
        Assert.Equal(new[] { 2, 4 }, CoreUtil.Map(new[] { 1, 2 }, x => x * 2));
    }

    [Fact]
    public void MapNonNil_DropsZeroValues()
    {
        // Reference elements: null is dropped (matches Go nil).
        var input = new[] { "keep", "drop", "also" };
        var result = CoreUtil.MapNonNil(input, x => x == "drop" ? null! : x);
        Assert.Equal(new[] { "keep", "also" }, result);
    }

    [Fact]
    public void Concatenate_PreservesIdentityWhenOneSideEmpty()
    {
        var s1 = new[] { 1, 2 };
        var s2 = new[] { 3, 4 };
        Assert.Same(s1, CoreUtil.Concatenate(s1, Array.Empty<int>()));
        Assert.Same(s2, CoreUtil.Concatenate(Array.Empty<int>(), s2));
        Assert.Equal(new[] { 1, 2, 3, 4 }, CoreUtil.Concatenate(s1, s2));
    }

    [Fact]
    public void Splice_ReturnsInputWhenNoOp()
    {
        var s = new[] { 1, 2, 3 };
        Assert.Same(s, CoreUtil.Splice(s, 1, 0));
    }

    [Fact]
    public void Splice_RemovesAndInserts()
    {
        var s = new[] { 1, 2, 3, 4 };
        Assert.Equal(new[] { 1, 9, 4 }, CoreUtil.Splice(s, 1, 2, 9));
        Assert.Equal(new[] { 1, 2, 3, 4 }, s); // input untouched
    }

    [Fact]
    public void Splice_NegativeStartCountsFromEnd()
    {
        Assert.Equal(new[] { 1, 2, 9 }, CoreUtil.Splice(new[] { 1, 2, 3 }, -1, 1, 9));
    }

    [Fact]
    public void InsertSorted_InsertsAtSortedPosition()
    {
        var s = new[] { 1, 3, 5 };
        Assert.Equal(new[] { 1, 3, 4, 5 }, CoreUtil.InsertSorted(s, 4, (a, b) => a - b));
        Assert.Equal(new[] { 0, 1, 3, 5 }, CoreUtil.InsertSorted(s, 0, (a, b) => a - b));
        Assert.Equal(new[] { 1, 3, 5, 6 }, CoreUtil.InsertSorted(s, 6, (a, b) => a - b));
    }

    [Fact]
    public void Deduplicate_ReturnsInputWhenNoDuplicates()
    {
        var s = new[] { 1, 2, 3 };
        Assert.Same(s, CoreUtil.Deduplicate(s));
        Assert.Equal(new[] { 1, 2, 3 }, CoreUtil.Deduplicate(new[] { 1, 2, 1, 3, 2 }));
    }

    [Fact]
    public void DeduplicateSorted_CollapsesRuns()
    {
        Assert.Equal(new[] { 1, 2, 3 }, CoreUtil.DeduplicateSorted(new[] { 1, 1, 2, 3, 3, 3 }, (a, b) => a == b));
    }

    [Fact]
    public void MinAllFunc_ReturnsAllMinima()
    {
        var result = CoreUtil.MinAllFunc(new[] { 3, 1, 2, 1, 4, 1 }, (a, b) => a - b);
        Assert.Equal(new[] { 1, 1, 1 }, result);
        Assert.Null(CoreUtil.MinAllFunc(Array.Empty<int>(), (a, b) => a - b));
    }

    [Fact]
    public void AppendIfUnique_SkipsExistingElement()
    {
        var s = new[] { 1, 2, 3 };
        Assert.Same(s, CoreUtil.AppendIfUnique(s, 2));
        Assert.Equal(new[] { 1, 2, 3, 4 }, CoreUtil.AppendIfUnique(s, 4));
    }

    [Fact]
    public void FindFamily_BehavesLikeGo()
    {
        var s = new[] { 1, 2, 3, 4 };
        Assert.Equal(2, CoreUtil.Find(s, x => x % 2 == 0));
        Assert.Equal(4, CoreUtil.FindLast(s, x => x % 2 == 0));
        Assert.Equal(1, CoreUtil.FindIndex(s, x => x % 2 == 0));
        Assert.Equal(3, CoreUtil.FindLastIndex(s, x => x % 2 == 0));
        Assert.Equal(0, CoreUtil.Find(s, x => x > 100)); // zero value
        Assert.Equal(-1, CoreUtil.FindIndex(s, x => x > 100));
    }

    [Fact]
    public void FirstLastElementOrNil()
    {
        var s = new[] { 5, 6, 7 };
        Assert.Equal(5, CoreUtil.FirstOrNil(s));
        Assert.Equal(7, CoreUtil.LastOrNil(s));
        Assert.Equal(6, CoreUtil.ElementOrNil(s, 1));
        Assert.Equal(0, CoreUtil.ElementOrNil(s, 9));
        Assert.Equal(0, CoreUtil.FirstOrNil(Array.Empty<int>()));
    }

    [Fact]
    public void SomeEveryCountWhere()
    {
        var s = new[] { 1, 2, 3 };
        Assert.True(CoreUtil.Some(s, x => x == 2));
        Assert.False(CoreUtil.Some(s, x => x == 9));
        Assert.True(CoreUtil.Every(s, x => x > 0));
        Assert.False(CoreUtil.Every(s, x => x > 1));
        Assert.Equal(2, CoreUtil.CountWhere(s, x => x >= 2));
    }

    [Fact]
    public void OrCombinesPredicates()
    {
        var pred = CoreUtil.Or<int>(x => x == 1, x => x == 2);
        Assert.True(pred(1));
        Assert.True(pred(2));
        Assert.False(pred(3));
    }

    [Fact]
    public void IfElse_OrElse_Coalesce()
    {
        Assert.Equal("yes", CoreUtil.IfElse(true, "yes", "no"));
        Assert.Equal(5, CoreUtil.OrElse(0, 5));
        Assert.Equal(3, CoreUtil.OrElse(3, 5));
        var a = new Box();
        var b = new Box();
        Assert.Same(a, CoreUtil.Coalesce(a, b));
        Assert.Same(b, CoreUtil.Coalesce<Box>(null, b));
    }

    [Fact]
    public void Memoize_EvaluatesOnceAndCaches()
    {
        int calls = 0;
        var memo = CoreUtil.Memoize(() =>
        {
            calls++;
            return 42;
        });
        Assert.Equal(42, memo());
        Assert.Equal(42, memo());
        Assert.Equal(1, calls);
    }

    [Fact]
    public void GetScriptKindFromFileName_MapsExtensions()
    {
        Assert.Equal(ScriptKind.TS, CoreUtil.GetScriptKindFromFileName("a.ts"));
        Assert.Equal(ScriptKind.Tsx, CoreUtil.GetScriptKindFromFileName("a.TSX"));
        Assert.Equal(ScriptKind.JS, CoreUtil.GetScriptKindFromFileName("a.mjs"));
        Assert.Equal(ScriptKind.Jsx, CoreUtil.GetScriptKindFromFileName("a.jsx"));
        Assert.Equal(ScriptKind.Json, CoreUtil.GetScriptKindFromFileName("tsconfig.json"));
        Assert.Equal(ScriptKind.Unknown, CoreUtil.GetScriptKindFromFileName("a.txt"));
        Assert.Equal(ScriptKind.Unknown, CoreUtil.GetScriptKindFromFileName("noext"));
    }

    [Fact]
    public void UTF16Len_IsStringLength()
    {
        Assert.Equal(3, CoreUtil.UTF16Len("abc"));
        Assert.Equal(2, CoreUtil.UTF16Len("\U0001F600")); // one code point, two UTF-16 units
    }

    [Fact]
    public void IndexAfter_FindsPatternFromStart()
    {
        Assert.Equal(3, CoreUtil.IndexAfter("aXbXcX", "X", 2)); // first "X" at/after index 2
        Assert.Equal(5, CoreUtil.IndexAfter("aXbXcX", "X", 4)); // absolute index preserved
        Assert.Equal(-1, CoreUtil.IndexAfter("abc", "X", 0));
    }

    [Fact]
    public void CompareBooleans_TrueGreaterThanFalse()
    {
        Assert.Equal(1, CoreUtil.CompareBooleans(true, false));
        Assert.Equal(-1, CoreUtil.CompareBooleans(false, true));
        Assert.Equal(0, CoreUtil.CompareBooleans(true, true));
    }

    [Fact]
    public void Enumerate_YieldsIndexValuePairs()
    {
        var result = CoreUtil.Enumerate(new[] { "a", "b", "c" }).ToArray();
        Assert.Equal(new[] { (0, "a"), (1, "b"), (2, "c") }, result);
    }

    [Fact]
    public void ConcatenateSeq_ConcatenatesSkippingNulls()
    {
        var result = CoreUtil.ConcatenateSeq(new[] { 1, 2 }, null, new[] { 3 }).ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void UnorderedEqual_IgnoresOrder()
    {
        Assert.True(CoreUtil.UnorderedEqual(new[] { 1, 2, 2, 3 }, new[] { 3, 2, 1, 2 }));
        Assert.False(CoreUtil.UnorderedEqual(new[] { 1, 2, 2 }, new[] { 1, 2, 3 }));
        Assert.False(CoreUtil.UnorderedEqual(new[] { 1 }, new[] { 1, 2 }));
    }

    [Fact]
    public void DiffMaps_ReportsAddedRemovedChanged()
    {
        var m1 = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
        var m2 = new Dictionary<string, int> { ["b"] = 2, ["c"] = 30, ["d"] = 4 };
        var added = new List<string>();
        var removed = new List<string>();
        var changed = new List<string>();
        CoreUtil.DiffMaps(m1, m2,
            (k, _) => added.Add(k),
            (k, _) => removed.Add(k),
            (k, _, _) => changed.Add(k));
        Assert.Equal(new[] { "d" }, added);
        Assert.Equal(new[] { "a" }, removed);
        Assert.Equal(new[] { "c" }, changed);
    }

    [Fact]
    public void CopyMapInto_ClonesWhenDestinationNull()
    {
        var src = new Dictionary<string, int> { ["a"] = 1 };
        var cloned = CoreUtil.CopyMapInto(null, src);
        Assert.NotSame(src, cloned);
        Assert.Equal(1, cloned!["a"]);

        var dst = new Dictionary<string, int> { ["x"] = 9 };
        var merged = CoreUtil.CopyMapInto(dst, src);
        Assert.Same(dst, merged);
        Assert.Equal(2, merged!.Count);
    }

    [Fact]
    public void FlatMap_And_Flatten()
    {
        Assert.Equal(new[] { 1, 1, 2, 2 }, CoreUtil.FlatMap(new[] { 1, 2 }, x => new[] { x, x }));
        Assert.Equal(new[] { 1, 2, 3, 4 }, CoreUtil.Flatten(new[] { new[] { 1, 2 }, Array.Empty<int>(), new[] { 3, 4 } }));
    }
}
