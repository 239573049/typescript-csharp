// Ported from tsgo internal/core/core.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using StrUtil = Ts.StringUtil.StringUtil;
using TsPathLib = Ts.TsPath.TsPath;

namespace Ts.Core;

// Go: internal/core/core.go — ~70 generic slice/map/seq helpers, ported function by
// function in Go source order onto the CoreUtil host class (plan/04 §2.2 #10; the
// SameMap member lives in the sibling CoreUtil.cs). Positions are UTF-16 code units
// (plan/01 §6, AGENTS.md §4.3): Go's TextPos/UTF16Offset dissolve to int, UTF16Len
// becomes s.Length, and ComputeECMALineStarts iterates UTF-16 code units, not bytes.
//
// Slice null-tolerance follows 05 §6.1: Go's nil slice (len 0, range 0×, append ok)
// maps to C# T[]? with the same "null == nil" semantics; helpers that Go returns nil
// from keep a T[]? return so identity/nil is preserved.
public static partial class CoreUtil
{
    // core.go:24-34 — ApplyDebugStackLimit (rtdebug.SetMaxStack) ✂️ NOT PORTED:
    // LargeStackExecutor (fixed 64 MB stack, plan/01 §4.6) replaces the Go runtime
    // stack-limit knob; there is no supported C# analog to runtime/debug.SetMaxStack.

    public static T[]? Filter<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                if (!f(slice[i]))
                {
                    var result = new List<T>(slice[..i]); // slices.Clone(slice[:i])
                    for (i++; i < slice.Length; i++)
                    {
                        var value = slice[i];
                        if (f(value))
                        {
                            result.Add(value);
                        }
                    }
                    return result.ToArray();
                }
            }
        }
        return slice;
    }

    public static IEnumerable<T> FilterSeq<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice is null)
        {
            yield break;
        }
        foreach (var value in slice)
        {
            if (f(value))
            {
                yield return value;
            }
        }
    }

    public static T[]? FilterIndex<T>(T[]? slice, Func<T, int, T[], bool> f)
    {
        if (slice != null)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                if (!f(slice[i], i, slice))
                {
                    var result = new List<T>(slice[..i]);
                    for (i++; i < slice.Length; i++)
                    {
                        var value = slice[i];
                        if (f(value, i, slice))
                        {
                            result.Add(value);
                        }
                    }
                    return result.ToArray();
                }
            }
        }
        return slice;
    }

    public static U[]? Map<T, U>(T[]? slice, Func<T, U> f)
    {
        if (slice == null)
        {
            return null;
        }
        var result = new U[slice.Length];
        for (int i = 0; i < slice.Length; i++)
        {
            result[i] = f(slice[i]);
        }
        return result;
    }

    // Go returns ([]U, error). Per 05 §4 the fallible callback throws instead of
    // returning an error, so TryMap short-circuits on the first thrown exception
    // exactly as Go returns on the first non-nil error. Empty/nil input → null (Go
    // returns nil,nil for len==0) — the one behavior distinguishing it from Map,
    // which allocates an empty array for empty input.
    public static U[]? TryMap<T, U>(T[]? slice, Func<T, U> f)
    {
        if (slice == null || slice.Length == 0)
        {
            return null;
        }
        var result = new U[slice.Length];
        for (int i = 0; i < slice.Length; i++)
        {
            result[i] = f(slice[i]);
        }
        return result;
    }

    public static U[]? MapIndex<T, U>(T[]? slice, Func<T, int, U> f)
    {
        if (slice == null)
        {
            return null;
        }
        var result = new U[slice.Length];
        for (int i = 0; i < slice.Length; i++)
        {
            result[i] = f(slice[i], i);
        }
        return result;
    }

    // Go: `mapped != *new(U)` filters the zero value of U. PORT-NOTE: C# default(U) is
    // null for strings, whereas Go's string zero is "" — instantiated with U = string
    // the empty-string filtering differs (plan/11 §known-deviations).
    public static U[]? MapNonNil<T, U>(T[]? slice, Func<T, U> f)
    {
        List<U>? result = null;
        if (slice != null)
        {
            foreach (var value in slice)
            {
                var mapped = f(value);
                if (!EqualityComparer<U>.Default.Equals(mapped, default!))
                {
                    (result ??= new List<U>()).Add(mapped);
                }
            }
        }
        return result?.ToArray();
    }

    public static U[]? MapFiltered<T, U>(T[]? slice, Func<T, (U Value, bool Ok)> f)
    {
        List<U>? result = null;
        if (slice != null)
        {
            foreach (var value in slice)
            {
                var (mapped, ok) = f(value);
                if (!ok)
                {
                    continue;
                }
                (result ??= new List<U>()).Add(mapped);
            }
        }
        return result?.ToArray();
    }

    public static U[]? FlatMap<T, U>(T[]? slice, Func<T, U[]?> f)
    {
        List<U>? result = null;
        if (slice != null)
        {
            foreach (var value in slice)
            {
                var mapped = f(value);
                if (mapped != null && mapped.Length != 0)
                {
                    (result ??= new List<U>()).AddRange(mapped);
                }
            }
        }
        return result?.ToArray();
    }

    // SameMap lives in the sibling CoreUtil.cs (phase-0 port).

    public static T[]? SameMapIndex<T>(T[]? slice, Func<T, int, T> f)
    {
        if (slice != null)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                var value = slice[i];
                var mapped = f(value, i);
                if (!EqualityComparer<T>.Default.Equals(mapped, value))
                {
                    var result = new T[slice.Length];
                    Array.Copy(slice, result, i);
                    result[i] = mapped;
                    for (int j = i + 1; j < slice.Length; j++)
                    {
                        result[j] = f(slice[j], j);
                    }
                    return result;
                }
            }
        }
        return slice;
    }

    // PORT-NOTE (05 §6.2): Go compares slice headers (&s1[0]==&s2[0]); C# array identity
    // is reference identity, so ReferenceEquals is exact and strictly stronger (two
    // distinct empty arrays / nil-vs-empty compare false, which Go's len-0 short circuit
    // does not — safe, since Same is only ever an optimization hint).
    public static bool Same<T>(T[]? s1, T[]? s2) => ReferenceEquals(s1, s2);

    public static bool Some<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            foreach (var value in slice)
            {
                if (f(value))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool Every<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            foreach (var value in slice)
            {
                if (!f(value))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static Func<T, bool> Or<T>(params Func<T, bool>[] funcs)
    {
        return input =>
        {
            foreach (var f in funcs)
            {
                if (f(input))
                {
                    return true;
                }
            }
            return false;
        };
    }

    public static T? Find<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            foreach (var value in slice)
            {
                if (f(value))
                {
                    return value;
                }
            }
        }
        return default;
    }

    public static T? FindLast<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            for (int i = slice.Length - 1; i >= 0; i--)
            {
                var value = slice[i];
                if (f(value))
                {
                    return value;
                }
            }
        }
        return default;
    }

    public static int FindIndex<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                if (f(slice[i]))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    public static int FindLastIndex<T>(T[]? slice, Func<T, bool> f)
    {
        if (slice != null)
        {
            for (int i = slice.Length - 1; i >= 0; i--)
            {
                if (f(slice[i]))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    public static T? FirstOrNil<T>(T[]? slice) =>
        slice != null && slice.Length != 0 ? slice[0] : default;

    public static T? LastOrNil<T>(T[]? slice) =>
        slice != null && slice.Length != 0 ? slice[^1] : default;

    public static T? ElementOrNil<T>(T[]? slice, int index)
    {
        // Go only guards index < len(slice); a negative index panics in Go and throws
        // (IndexOutOfRange) here too — behavior preserved.
        if (index < (slice?.Length ?? 0))
        {
            return slice![index];
        }
        return default;
    }

    public static T? FirstOrNilSeq<T>(IEnumerable<T>? seq)
    {
        if (seq != null)
        {
            foreach (var value in seq)
            {
                return value;
            }
        }
        return default;
    }

    public static U? FirstNonNil<T, U>(T[]? slice, Func<T, U> f)
    {
        if (slice != null)
        {
            foreach (var value in slice)
            {
                var mapped = f(value);
                if (!EqualityComparer<U>.Default.Equals(mapped, default!))
                {
                    return mapped;
                }
            }
        }
        return default;
    }

    public static T? FirstNonZero<T>(params T[] values)
    {
        foreach (var value in values)
        {
            if (!EqualityComparer<T>.Default.Equals(value, default!))
            {
                return value;
            }
        }
        return default;
    }

    public static T[]? Concatenate<T>(T[]? s1, T[]? s2)
    {
        if (s2 == null || s2.Length == 0)
        {
            return s1;
        }
        if (s1 == null || s1.Length == 0)
        {
            return s2;
        }
        var result = new T[s1.Length + s2.Length];
        Array.Copy(s1, 0, result, 0, s1.Length);
        Array.Copy(s2, 0, result, s1.Length, s2.Length);
        return result;
    }

    public static T[]? Splice<T>(T[]? s1, int start, int deleteCount, params T[] items)
    {
        int n = s1?.Length ?? 0;
        if (start < 0)
        {
            start = n + start;
        }
        if (start < 0)
        {
            start = 0;
        }
        if (start > n)
        {
            start = n;
        }
        if (deleteCount < 0)
        {
            deleteCount = 0;
        }
        int end = Math.Min(start + Math.Max(deleteCount, 0), n);
        if (start == end && items.Length == 0)
        {
            return s1;
        }
        var src = s1 ?? [];
        var result = new T[start + items.Length + (n - end)];
        Array.Copy(src, 0, result, 0, start);
        Array.Copy(items, 0, result, start, items.Length);
        Array.Copy(src, end, result, start + items.Length, n - end);
        return result;
    }

    public static int CountWhere<T>(T[]? slice, Func<T, bool> f)
    {
        int count = 0;
        if (slice != null)
        {
            foreach (var value in slice)
            {
                if (f(value))
                {
                    count++;
                }
            }
        }
        return count;
    }

    public static T[] ReplaceElement<T>(T[] slice, int i, T t)
    {
        var result = (T[])slice.Clone();
        result[i] = t;
        return result;
    }

    public static T[] InsertSorted<T>(T[]? slice, T element, Func<T, T, int> cmp)
    {
        // slices.BinarySearchFunc: leftmost index i where cmp(slice[i], element) >= 0.
        int n = slice?.Length ?? 0;
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (cmp(slice![mid], element) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        // slices.Insert(slice, lo, element).
        var result = new T[n + 1];
        if (slice != null)
        {
            Array.Copy(slice, 0, result, 0, lo);
            Array.Copy(slice, lo, result, lo + 1, n - lo);
        }
        result[lo] = element;
        return result;
    }

    // MinAllFunc returns all minimum elements from xs according to the comparison function cmp.
    public static T[]? MinAllFunc<T>(T[]? xs, Func<T, T, int> cmp)
    {
        if (xs == null || xs.Length == 0)
        {
            return null;
        }
        var m = xs[0];
        var mins = new List<T> { m };
        for (int k = 1; k < xs.Length; k++)
        {
            var x = xs[k];
            int c = cmp(x, m);
            if (c < 0)
            {
                m = x;
                mins.Clear();
                mins.Add(x);
            }
            else if (c == 0)
            {
                mins.Add(x);
            }
        }
        return mins.ToArray();
    }

    public static T[] AppendIfUnique<T>(T[]? slice, T element)
    {
        if (slice != null)
        {
            foreach (var v in slice)
            {
                if (EqualityComparer<T>.Default.Equals(v, element))
                {
                    return slice;
                }
            }
        }
        int n = slice?.Length ?? 0;
        var result = new T[n + 1];
        if (slice != null)
        {
            Array.Copy(slice, result, n);
        }
        result[n] = element;
        return result;
    }

    public static Func<T> Memoize<T>(Func<T> create)
    {
        T value = default!;
        Func<T>? c = create;
        return () =>
        {
            if (c != null)
            {
                value = c();
                c = null;
            }
            return value;
        };
    }

    // Returns whenTrue if b is true; otherwise, returns whenFalse. IfElse should only be used when branches are either
    // constant or precomputed as both branches will be evaluated regardless as to the value of b.
    public static T IfElse<T>(bool b, T whenTrue, T whenFalse) => b ? whenTrue : whenFalse;

    // Returns value if value is not the zero value of T; Otherwise, returns defaultValue. OrElse should only be used when
    // defaultValue is constant or precomputed as its argument will be evaluated regardless as to the content of value.
    // PORT-NOTE: default(T) is null for strings vs Go's "" (plan/11 §known-deviations).
    public static T OrElse<T>(T value, T defaultValue) =>
        !EqualityComparer<T>.Default.Equals(value, default!) ? value : defaultValue;

    // Returns `a` if `a` is not `nil`; Otherwise, returns `b`. Coalesce is roughly analogous to `??` in JS, except that it
    // is non-shortcutting (both arguments are evaluated at the call site regardless).
    public static T? Coalesce<T>(T? a, T? b)
        where T : class => a ?? b;

    // Go: type ECMALineStarts []TextPos → int[] (TextPos dissolves to int, Text.cs).
    public static int[] ComputeECMALineStarts(string text)
    {
        var result = new List<int>(text.AsSpan().Count('\n') + 1);
        foreach (var pos in ComputeECMALineStartsSeq(text))
        {
            result.Add(pos);
        }
        return result.ToArray();
    }

    // PORT-NOTE: Go iterates UTF-8 bytes and yields byte offsets; C# strings are UTF-16,
    // so this iterates UTF-16 code units and yields code-unit offsets (plan/01 §6,
    // AGENTS.md §4.3). Every ECMAScript line terminator (\n, \r, U+2028, U+2029) is a
    // single UTF-16 code unit, so a code-unit scan is exact; a \r\n pair is consumed as
    // one break, with the next line starting after the \n.
    public static IEnumerable<int> ComputeECMALineStartsSeq(string text)
    {
        int textLen = text.Length;
        int pos = 0;
        int lineStart = 0;
        while (pos < textLen)
        {
            char ch = text[pos];
            if (ch < 128)
            {
                pos++;
                switch (ch)
                {
                    case '\r':
                        if (pos < textLen && text[pos] == '\n')
                        {
                            pos++;
                        }
                        goto case '\n';
                    case '\n':
                        yield return lineStart;
                        lineStart = pos;
                        break;
                }
            }
            else if (StrUtil.IsLineBreak(ch))
            {
                pos++;
                yield return lineStart;
                lineStart = pos;
            }
            else
            {
                pos++;
            }
        }
        yield return lineStart;
    }

    // PositionToLineAndByteOffset returns the 0-based line and offset from the start of
    // that line for the given position, using the provided line starts.
    // PORT-NOTE: "byteOffset" is a UTF-16 code-unit offset in this port (positions are
    // UTF-16 code units, AGENTS.md §4.3); the Go name is kept for grep parity.
    public static (int Line, int Offset) PositionToLineAndByteOffset(int position, int[] lineStarts)
    {
        // Go: sort.Search(len, i => lineStarts[i] > position) - 1, floored at 0.
        int lo = 0, hi = lineStarts.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (lineStarts[mid] > position)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        int line = Math.Max(lo - 1, 0);
        return (line, position - lineStarts[line]);
    }

    // Go: type UTF16Offset int → int (positions are UTF-16 code units). UTF16Len's
    // UTF-8-byte-scanning fast path is unnecessary — a C# string's Length already IS its
    // UTF-16 code-unit count, so UTF16Len(s) == s.Length (plan/10 §2.3.1).
    public static int UTF16Len(string s) => s.Length;

    public static T[]? Flatten<T>(T[][]? array)
    {
        List<T>? result = null;
        if (array != null)
        {
            foreach (var subArray in array)
            {
                if (subArray != null && subArray.Length != 0)
                {
                    (result ??= new List<T>()).AddRange(subArray);
                }
            }
        }
        return result?.ToArray();
    }

    // core.go:510-515 — Must(v, err) ✂️ NOT PORTED: Go's (T, error) unwrap has no C#
    // analog. Under 05 §4 fallible operations throw, so `core.Must(f())` becomes just
    // `f()` (which throws on failure). (plan/11 §known-deviations.)

    // core.go:517-520 — FirstResult(t1, ...) ✂️ NOT PORTED: extracts the first of a Go
    // multi-value return; C# multi-returns are tuples, so `core.FirstResult(f())`
    // becomes `f().Item1` / tuple deconstruction at the call site. (plan/11.)

    // core.go:522-525 — StringifyJson(any, prefix, indent) is DEFERRED (TODO csport):
    // Go's json.MarshalIndent over `any` needs reflection (AOT-banned, plan/07), and
    // byte-exact output must mirror tsgo's internal/json escaping — verifiable only
    // against phase-4+ (.errors.txt) / phase-8 (fourslash) baselines. Per-caller typed
    // Utf8JsonWriter emitters (plan/07 §4.2 face 1) land with those callers, mirroring
    // the OrderedMap JSON-face deferral (OrderedMap.cs). (plan/11 §known-deviations.)

    public static ScriptKind GetScriptKindFromFileName(string fileName)
    {
        int dotPos = fileName.LastIndexOf('.');
        if (dotPos >= 0)
        {
            // Go strings.ToLower on the extension; StrUtil.ToLowerJS is the analyzer-
            // approved, AOT-safe lowercaser (ToLowerInvariant is banned). The matched
            // extensions are ASCII, where ToLowerJS and Go strings.ToLower coincide.
            switch (StrUtil.ToLowerJS(fileName[dotPos..]))
            {
                case TsPathLib.ExtensionJs:
                case TsPathLib.ExtensionCjs:
                case TsPathLib.ExtensionMjs:
                    return ScriptKind.JS;
                case TsPathLib.ExtensionJsx:
                    return ScriptKind.Jsx;
                case TsPathLib.ExtensionTs:
                case TsPathLib.ExtensionCts:
                case TsPathLib.ExtensionMts:
                    return ScriptKind.TS;
                case TsPathLib.ExtensionTsx:
                    return ScriptKind.Tsx;
                case TsPathLib.ExtensionJson:
                    return ScriptKind.Json;
            }
        }
        return ScriptKind.Unknown;
    }

    // Given a name and a list of names that are *not* equal to the name, return a spelling suggestion if there is one that is close enough.
    // Names less than length 3 only check for case-insensitive equality.
    //
    // find the candidate with the smallest Levenshtein distance,
    //
    //	except for candidates:
    //	  * With no name
    //	  * Whose length differs from the target name by more than 0.34 of the length of the name.
    //	  * Whose levenshtein distance is more than 0.4 of the length of the name
    //	    (0.4 allows 1 substitution/transposition for every 5 characters,
    //	     and 1 insertion/deletion at 3 characters)
    public static T? GetSpellingSuggestion<T>(string name, IEnumerable<T> candidates, Func<T, string> getName, Func<T, T, int> compare)
    {
        int[] runeName = ToRunes(name);
        int maximumLengthDifference = Math.Max(2, (int)((double)runeName.Length * 0.34));
        double bestDistance = Math.Floor((double)runeName.Length * 0.4) + 0.9; // If the best result is worse than this, don't bother.
        T? bestCandidate = default;
        bool hasBest = false;
        // PORT-NOTE: Go mixes len(candidateName) (bytes) with len(runeName) (runes); C#
        // uses candidateName.Length (UTF-16 units) with runeName.Length (runes). All
        // three coincide for ASCII identifiers (the common case); negligible on the
        // heuristic length gate for non-ASCII.
        foreach (var candidate in candidates)
        {
            string candidateName = getName(candidate);
            int maxLen = Math.Max(candidateName.Length, runeName.Length);
            int minLen = Math.Min(candidateName.Length, runeName.Length);
            if (candidateName.Length != 0 && maxLen - minLen <= maximumLengthDifference)
            {
                if (candidateName == name)
                {
                    continue;
                }
                // Only consider candidates less than 3 characters long when they differ by case.
                // Otherwise, don't bother, since a user would usually notice differences of a 2-character name.
                if (candidateName.Length < 3 && !StrUtil.EqualFold(candidateName, name))
                {
                    continue;
                }
                double distance = LevenshteinWithMax(runeName, ToRunes(candidateName), bestDistance);
                if (distance < 0)
                {
                    continue;
                }
                Debug.Assert(distance <= bestDistance); // Else `LevenshteinWithMax` should return -1.
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestCandidate = candidate;
                    hasBest = true;
                }
                else if (!hasBest || compare(candidate, bestCandidate!) < 0)
                {
                    bestCandidate = candidate;
                    hasBest = true;
                }
            }
        }
        return bestCandidate;
    }

    // PORT-NOTE: Go's zero value is "" for the string T; here GetSpellingSuggestion<string>
    // returns null for "no suggestion", coalesced to "" to match Go (real candidates are
    // never "" — they are filtered by the candidateName.Length != 0 gate).
    public static string GetSpellingSuggestionForStrings(string name, IEnumerable<string> candidates)
    {
        return GetSpellingSuggestion(name, candidates, Identity, static (a, b) => string.CompareOrdinal(a, b)) ?? "";
    }

    // Go []rune(s): decode a string to its Unicode code points.
    private static int[] ToRunes(string s)
    {
        var runes = new List<int>(s.Length);
        foreach (var rune in s.AsSpan().EnumerateRunes())
        {
            runes.Add(rune.Value);
        }
        return runes.ToArray();
    }

    // Go: levenshteinWithMax — PORT-NOTE: the sync.Pool of growable []float64 buffers
    // (core.go:602-611) becomes ArrayPool<double> rentals (plan/10 §2.3.1); s1/s2 are
    // Unicode code points (Go []rune). unicode.ToLower → Rune.ToLowerInvariant (both
    // culture-independent Unicode lowercase).
    private static double LevenshteinWithMax(int[] s1, int[] s2, double maxValue)
    {
        int bufferSize = s2.Length + 1;
        double[] previous = ArrayPool<double>.Shared.Rent(bufferSize);
        double[] current = ArrayPool<double>.Shared.Rent(bufferSize);
        try
        {
            double big = maxValue + 0.01;
            for (int i = 0; i < bufferSize; i++)
            {
                previous[i] = i;
            }
            for (int i = 1; i <= s1.Length; i++)
            {
                int c1 = s1[i - 1];
                int minJ = Math.Max((int)Math.Ceiling((double)i - maxValue), 1);
                int maxJ = Math.Min((int)Math.Floor(maxValue + i), s2.Length);
                double colMin = i;
                current[0] = colMin;
                for (int j = 1; j < minJ; j++)
                {
                    current[j] = big;
                }
                for (int j = minJ; j <= maxJ; j++)
                {
                    double substitutionDistance;
                    if (Rune.ToLowerInvariant(new Rune(s1[i - 1])) == Rune.ToLowerInvariant(new Rune(s2[j - 1])))
                    {
                        substitutionDistance = previous[j - 1] + 0.1;
                    }
                    else
                    {
                        substitutionDistance = previous[j - 1] + 2;
                    }
                    double dist;
                    if (c1 == s2[j - 1])
                    {
                        dist = previous[j - 1];
                    }
                    else
                    {
                        dist = Math.Min(previous[j] + 1, Math.Min(current[j - 1] + 1, substitutionDistance));
                    }
                    current[j] = dist;
                    colMin = Math.Min(colMin, dist);
                }
                for (int j = maxJ + 1; j <= s2.Length; j++)
                {
                    current[j] = big;
                }
                if (colMin > maxValue)
                {
                    // Give up -- everything in this column is > max and it can't get better in future columns.
                    return -1;
                }
                (previous, current) = (current, previous);
            }
            double res = previous[s2.Length];
            if (res > maxValue)
            {
                return -1;
            }
            return res;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(previous);
            ArrayPool<double>.Shared.Return(current);
        }
    }

    public static T Identity<T>(T t) => t;

    public static S[] CheckEachDefined<S>(S[] s, string msg)
        where S : class
    {
        foreach (var value in s)
        {
            if (value == null)
            {
                throw new InvalidOperationException(msg); // Go: panic(msg)
            }
        }
        return s;
    }

    // PORT-NOTE: Go computes strings.Index(s[startIndex:], pattern)+startIndex; C#
    // IndexOf(pattern, startIndex, Ordinal) already returns the absolute index
    // (05 §8.5), so the two are equivalent.
    public static int IndexAfter(string s, string pattern, int startIndex)
    {
        return s.IndexOf(pattern, startIndex, StringComparison.Ordinal);
    }

    // core.go:687-689 — ShouldRewriteModuleSpecifier is QUARANTINED until CompilerOptions
    // lands (compileroptions.go → CompilerOptions.cs, owned by another agent; not yet in
    // Ts.Core). It reads compilerOptions.RewriteRelativeImportExtensions (Tristate) plus
    // three tspath predicates. Restore verbatim once the type exists:
    //   public static bool ShouldRewriteModuleSpecifier(string specifier, CompilerOptions o) =>
    //       o.RewriteRelativeImportExtensions.IsTrue() && TsPathLib.PathIsRelative(specifier)
    //       && !TsPathLib.IsDeclarationFileName(specifier) && TsPathLib.HasTSFileExtension(specifier);
    // (plan/11 §known-deviations.)

    public static T[]? SingleElementSlice<T>(T? element)
        where T : class
    {
        if (element == null)
        {
            return null;
        }
        return new T[] { element };
    }

    public static IEnumerable<T> ConcatenateSeq<T>(params IEnumerable<T>?[] seqs)
    {
        foreach (var seq in seqs)
        {
            if (seq == null)
            {
                continue;
            }
            foreach (var e in seq)
            {
                yield return e;
            }
        }
    }

    // Enumerate returns a sequence of (index, value) pairs from the input sequence.
    public static IEnumerable<(int Index, T Value)> Enumerate<T>(IEnumerable<T> seq)
    {
        int i = 0;
        foreach (var v in seq)
        {
            yield return (i, v);
            i++;
        }
    }

    // DiffMaps compares two maps m1 and m2 and calls the provided callbacks for added, removed, and changed entries.
    // onAdded is called for each key-value pair that is in m2 but not in m1.
    // onRemoved is called for each key-value pair that is in m1 but not in m2.
    // onChanged is called for each key where the value in m1 differs from the value in m2.
    public static void DiffMaps<K, V>(
        Dictionary<K, V>? m1,
        Dictionary<K, V>? m2,
        Action<K, V>? onAdded,
        Action<K, V>? onRemoved,
        Action<K, V, V>? onChanged)
        where K : notnull
    {
        DiffMapsFunc(m1, m2, static (a, b) => EqualityComparer<V>.Default.Equals(a, b), onAdded, onRemoved, onChanged);
    }

    public static void DiffMapsFunc<K, V1, V2>(
        Dictionary<K, V1>? m1,
        Dictionary<K, V2>? m2,
        Func<V1, V2, bool> equalValues,
        Action<K, V2>? onAdded,
        Action<K, V1>? onRemoved,
        Action<K, V1, V2>? onChanged)
        where K : notnull
    {
        if (onAdded != null && m2 != null)
        {
            // map-iter: 序无关 —— per-key add callbacks; order does not affect the result.
            foreach (var (k, v2) in m2)
            {
                if (m1 == null || !m1.ContainsKey(k))
                {
                    onAdded(k, v2);
                }
            }
        }
        if (onChanged == null && onRemoved == null)
        {
            return;
        }
        if (m1 != null)
        {
            // map-iter: 序无关 —— per-key changed/removed callbacks; order-independent.
            foreach (var (k, v1) in m1)
            {
                if (m2 != null && m2.TryGetValue(k, out var v2))
                {
                    if (onChanged != null && !equalValues(v1, v2))
                    {
                        onChanged(k, v1, v2);
                    }
                }
                else
                {
                    // PORT-NOTE: Go calls onRemoved unconditionally here (would panic if
                    // nil); C# skips when null. The guard above ensures at least one of
                    // onChanged/onRemoved is non-null, and no caller passes a nil
                    // onRemoved while expecting removals.
                    onRemoved?.Invoke(k, v1);
                }
            }
        }
    }

    // CopyMapInto is maps.Copy, unless dst is null, in which case it clones and returns src.
    // Use CopyMapInto anywhere you would use maps.Copy preceded by a null check and map initialization.
    public static Dictionary<K, V>? CopyMapInto<K, V>(Dictionary<K, V>? dst, Dictionary<K, V>? src)
        where K : notnull
    {
        if (dst == null)
        {
            return src == null ? null : new Dictionary<K, V>(src, src.Comparer);
        }
        if (src != null)
        {
            // map-iter: 序无关 —— bulk copy; insertion order is irrelevant.
            foreach (var kv in src)
            {
                dst[kv.Key] = kv.Value;
            }
        }
        return dst;
    }

    // UnorderedEqual returns true if s1 and s2 contain the same elements, regardless of order.
    public static bool UnorderedEqual<T>(T[] s1, T[] s2)
        where T : notnull
    {
        if (s1.Length != s2.Length)
        {
            return false;
        }
        var counts = new Dictionary<T, int>();
        foreach (var v in s1)
        {
            counts[v] = counts.GetValueOrDefault(v) + 1;
        }
        foreach (var v in s2)
        {
            int c = counts.GetValueOrDefault(v) - 1;
            counts[v] = c;
            if (c < 0)
            {
                return false;
            }
        }
        return true;
    }

    public static T[]? Deduplicate<T>(T[]? slice)
    {
        if (slice != null && slice.Length > 1)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                var value = slice[i];
                if (Array.IndexOf(slice, value, 0, i) >= 0)
                {
                    var result = new List<T>(slice[..i]);
                    for (i++; i < slice.Length; i++)
                    {
                        value = slice[i];
                        if (!result.Contains(value))
                        {
                            result.Add(value);
                        }
                    }
                    return result.ToArray();
                }
            }
        }
        return slice;
    }

    // PORT-NOTE: Go builds the result in-place over slice's backing array
    // (deduplicated := slice[:1]; append...), mutating the caller's array tail
    // (05 §6.4 write-through). C# returns a fresh array; contents match, and no tsgo
    // caller relies on the tail mutation (the return value is reassigned).
    public static T[]? DeduplicateSorted<T>(T[]? slice, Func<T, T, bool> isEqual)
    {
        if (slice == null || slice.Length == 0)
        {
            return slice;
        }
        var last = slice[0];
        var deduplicated = new List<T> { last };
        for (int i = 1; i < slice.Length; i++)
        {
            var next = slice[i];
            if (isEqual(last, next))
            {
                continue;
            }
            deduplicated.Add(next);
            last = next;
        }
        return deduplicated.ToArray();
    }

    // CompareBooleans treats true as greater than false.
    public static int CompareBooleans(bool a, bool b)
    {
        if (a && !b)
        {
            return 1;
        }
        if (!a && b)
        {
            return -1;
        }
        return 0;
    }
}
