// Ported from tsgo internal/ls/autoimport/index.go (whole file): the word-prefix index over
// named entries used for auto-import candidate lookup.

using System;
using System.Collections.Generic;
using StrUtil = Ts.StringUtil.StringUtil;

namespace Ts.Ls.AutoImport;

// index.go:11-14 — Named is a constraint for types that can provide their name.
public interface Named
{
    string Name();
}

// index.go:16-22 — Index stores entries with an index mapping uppercase letters to entries whose
// name starts with that letter, and lowercase letters to entries whose name contains a word
// starting with that letter. (Go map[rune][]int — rune keys become code-point int keys.)
public sealed class Index<T> where T : class, Named
{
    internal List<T> entries = [];
    internal Dictionary<int, List<int>>? index;

    // index.go:24-48 — Find.
    public List<T> Find(string name, bool caseSensitive)
    {
        if (entries.Count == 0 || name.Length == 0)
        {
            return [];
        }
        var firstRune = DecodeFirstRune(name);
        if (firstRune < 0)
        {
            return [];
        }
        var firstRuneUpper = StrUtil.ToUpperRune(firstRune);
        if (index == null || !index.TryGetValue(firstRuneUpper, out var candidates))
        {
            return [];
        }

        var results = new List<T>();
        foreach (var entryIndex in candidates)
        {
            var entry = entries[entryIndex];
            var entryName = entry.Name();
            if ((caseSensitive && entryName == name) || (!caseSensitive && StrUtil.EqualFold(entryName, name)))
            {
                results.Add(entry);
            }
        }

        return results;
    }

    // index.go:50-95 — SearchWordPrefix: returns each entry whose name contains a word beginning
    // with the first character of 'prefix', and whose name contains all characters of 'prefix' in
    // order (case-insensitive).
    public List<T> SearchWordPrefix(string prefix)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        if (prefix.Length == 0)
        {
            return entries;
        }

        prefix = StrUtil.ToLowerJS(prefix);
        var firstRune = DecodeFirstRune(prefix);
        if (firstRune < 0)
        {
            return [];
        }

        var firstRuneUpper = StrUtil.ToUpperRune(firstRune);
        var firstRuneLower = StrUtil.ToLowerRune(firstRune);

        // Look up entries that have words starting with this letter
        List<int>? nameStarts = null;
        List<int>? wordStarts = null;
        index?.TryGetValue(firstRuneUpper, out nameStarts);
        if (firstRuneUpper != firstRuneLower)
        {
            index?.TryGetValue(firstRuneLower, out wordStarts);
        }
        var count = (nameStarts?.Count ?? 0) + (wordStarts?.Count ?? 0);
        if (count == 0)
        {
            return [];
        }

        // Filter entries by checking if they contain all characters in order
        var results = new List<T>(count);
        foreach (var starts in new[] { nameStarts, wordStarts })
        {
            foreach (var i in starts ?? [])
            {
                var entry = entries[i];
                if (ContainsCharsInOrder(entry.Name(), prefix))
                {
                    results.Add(entry);
                }
            }
        }
        return results;
    }

    // index.go:97-112 — containsCharsInOrder: checks if str contains all characters from pattern
    // in order (case-insensitive).
    internal static bool ContainsCharsInOrder(string str, string pattern)
    {
        str = StrUtil.ToLowerJS(str);
        pattern = StrUtil.ToLowerJS(pattern);

        var patternIdx = 0;
        for (var i = 0; i < str.Length;)
        {
            int ch;
            if (char.IsHighSurrogate(str[i]) && i + 1 < str.Length && char.IsLowSurrogate(str[i + 1]))
            {
                ch = char.ConvertToUtf32(str[i], str[i + 1]);
                i += 2;
            }
            else
            {
                ch = str[i];
                i += 1;
            }
            if (patternIdx < pattern.Length)
            {
                int patternRune;
                int size;
                if (char.IsHighSurrogate(pattern[patternIdx]) && patternIdx + 1 < pattern.Length && char.IsLowSurrogate(pattern[patternIdx + 1]))
                {
                    patternRune = char.ConvertToUtf32(pattern[patternIdx], pattern[patternIdx + 1]);
                    size = 2;
                }
                else
                {
                    patternRune = pattern[patternIdx];
                    size = 1;
                }
                if (ch == patternRune)
                {
                    patternIdx += size;
                }
            }
        }
        return patternIdx == pattern.Length;
    }

    // index.go:114-146 — insertAsWords: adds a value to the index keyed by the first letter of
    // each word in its name.
    internal void InsertAsWords(T value)
    {
        index ??= new Dictionary<int, List<int>>();

        var name = value.Name();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Cannot index entry with empty name");
        }
        var entryIndex = entries.Count;
        entries.Add(value);

        var indices = Util.WordIndices(name);
        var seenRunes = new HashSet<int>();

        for (var i = 0; i < indices.Count; i++)
        {
            var start = indices[i];
            var firstRune = DecodeFirstRune(name[start..]);
            if (firstRune < 0)
            {
                continue;
            }
            if (i == 0)
            {
                // Name start keyed by uppercase
                firstRune = StrUtil.ToUpperRune(firstRune);
                AddToIndex(firstRune, entryIndex);
                seenRunes.Add(firstRune); // (Still set seenRunes in case first character is non-alphabetic)
            }
            else
            {
                // Subsequent word starts keyed by lowercase
                firstRune = StrUtil.ToLowerRune(firstRune);
                if (!seenRunes.Contains(firstRune))
                {
                    AddToIndex(firstRune, entryIndex);
                    seenRunes.Add(firstRune);
                }
            }
        }
    }

    private void AddToIndex(int r, int entryIndex)
    {
        if (!index!.TryGetValue(r, out var list))
        {
            list = [];
            index[r] = list;
        }
        list.Add(entryIndex);
    }

    // index.go:148-186 — Clone: creates a new Index containing only entries for which filter
    // returns true. (Go nil-receiver → null-tolerant static extension shape kept via instance +
    // null-conditional at call sites.)
    public Index<T> Clone(Func<T, bool> filter)
    {
        var newIdx = new Index<T>
        {
            entries = new List<T>(entries.Count),
            index = new Dictionary<int, List<int>>(index?.Count ?? 0),
        };

        // Build mapping from old index to new index for filtered entries
        var oldToNew = new Dictionary<int, int>(entries.Count);
        for (var oldIndex = 0; oldIndex < entries.Count; oldIndex++)
        {
            var entry = entries[oldIndex];
            if (filter(entry))
            {
                var newIndex = newIdx.entries.Count;
                newIdx.entries.Add(entry);
                oldToNew[oldIndex] = newIndex;
            }
        }

        // Rebuild the index with remapped indices
        foreach (var (r, oldIndices) in index ?? []) // map-iter: 序无关 (per-letter buckets are disjoint; bucket-internal order preserved)
        {
            var newIndices = new List<int>(oldIndices.Count);
            foreach (var oldIndex in oldIndices)
            {
                if (oldToNew.TryGetValue(oldIndex, out var newIndex))
                {
                    newIndices.Add(newIndex);
                }
            }
            if (newIndices.Count > 0)
            {
                newIdx.index[r] = newIndices;
            }
        }

        return newIdx;
    }

    private static int DecodeFirstRune(string s)
    {
        if (s.Length == 0)
        {
            return -1; // Go utf8.RuneError path
        }
        if (char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1]))
        {
            return char.ConvertToUtf32(s[0], s[1]);
        }
        if (char.IsSurrogate(s[0]))
        {
            return -1; // lone surrogate = RuneError
        }
        return s[0];
    }
}
