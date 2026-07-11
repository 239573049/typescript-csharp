// Local support types/helpers for the inference cluster (inference.go → Checker.Inference.cs).
//   - InferenceKey / InferenceState: ported 1:1 from inference.go:11-31 (InferenceState was a skeleton
//     shell in Checker.Support.cs, removed there and filled in here to keep the cluster cohesive).
//   - Slices* / MinInf: array + priority helpers standing in for Go `slices.*` and `min` over the
//     InferencePriority enum (whose Circularity == -1 makes ordinary int comparison correct).

using System;

namespace Ts.Checker;

// inference.go:11-14
internal readonly record struct InferenceKey(TypeId S, TypeId T);

// inference.go:16-31
internal sealed class InferenceState
{
    internal InferenceInfo[] inferences = [];
    internal Type? originalSource;
    internal Type? originalTarget;
    internal InferencePriority priority;
    internal InferencePriority inferencePriority;
    internal bool contravariant;
    internal bool bivariant;
    internal ExpandingFlags expandingFlags;
    internal Type? propagationType;
    internal Dictionary<InferenceKey, InferencePriority>? visited;
    internal List<Type> sourceStack = [];
    internal List<Type> targetStack = [];
    internal InferenceState? next;
    internal int depth;
}

public partial class Checker
{
    // Go `min` over InferencePriority. The enum is int-backed with Circularity == -1 (less than all
    // other priorities), so signed integer comparison reproduces Go's ordering exactly.
    private static InferencePriority MinInf(InferencePriority a, InferencePriority b) =>
        (int)a <= (int)b ? a : b;

    // slices.Contains — reference equality (Type/int never override Equals; plan/03 §6.4).
    private static bool SlicesContains<T>(T[]? s, T value) =>
        s is not null && Array.IndexOf(s, value) >= 0;

    // slices.Index — -1 when absent (matches Go on nil slices).
    private static int SlicesIndex<T>(T[]? s, T value) =>
        s is null ? -1 : Array.IndexOf(s, value);

    // append(s, value) — grows by one; nil/empty source yields a fresh single-element array.
    private static T[] SlicesAppend<T>(T[]? s, T value)
    {
        var len = s?.Length ?? 0;
        var r = new T[len + 1];
        if (s is not null)
        {
            Array.Copy(s, r, len);
        }
        r[len] = value;
        return r;
    }

    // slices.Delete(s, start, end) — removes [start, end).
    private static T[] SlicesDelete<T>(T[] s, int start, int end)
    {
        var r = new T[s.Length - (end - start)];
        Array.Copy(s, 0, r, 0, start);
        Array.Copy(s, end, r, start, s.Length - end);
        return r;
    }

    // slices.Insert(s, index, value) — inserts value at index (nil source treated as empty).
    private static T[] SlicesInsert<T>(T[]? s, int index, T value)
    {
        var len = s?.Length ?? 0;
        var r = new T[len + 1];
        if (s is not null)
        {
            Array.Copy(s, 0, r, 0, index);
            Array.Copy(s, index, r, index + 1, len - index);
        }
        r[index] = value;
        return r;
    }
}
