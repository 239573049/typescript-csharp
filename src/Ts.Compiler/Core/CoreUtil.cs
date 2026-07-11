using System;
using System.Collections.Generic;

namespace Ts.Core;

// Go: internal/core/core.go — the SameMap member landed in phase 0 for generated
// VisitEachChild bodies over raw node slices (plan/04 §2.2 #10 maps core.SameMap to
// Ts.Core.CoreUtil.SameMap). The remaining ~70 core.go helpers are ported (in Go
// source order) onto this same partial class in the sibling Core.cs.
public static partial class CoreUtil
{
    // core.go:151-165 — returns the input slice unchanged (same reference) when f
    // maps every element to itself; Go's `mapped != value` on pointers is reference
    // equality, which EqualityComparer<T>.Default preserves for reference types
    // without custom equality.
    public static T[]? SameMap<T>(T[]? slice, Func<T, T> f)
    {
        if (slice != null)
        {
            for (int i = 0; i < slice.Length; i++)
            {
                var value = slice[i];
                var mapped = f(value);
                if (!EqualityComparer<T>.Default.Equals(mapped, value))
                {
                    var result = new T[slice.Length];
                    Array.Copy(slice, result, i);
                    result[i] = mapped;
                    for (int j = i + 1; j < slice.Length; j++)
                    {
                        result[j] = f(slice[j]);
                    }
                    return result;
                }
            }
        }
        return slice;
    }
}
