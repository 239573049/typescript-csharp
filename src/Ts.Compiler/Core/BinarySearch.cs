// Ported from tsgo internal/core/binarysearch.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;

namespace Ts.Core;

public static partial class Core
{
    // BinarySearchUniqueFunc works like [slices.BinarySearchFunc], but avoids extra
    // invocations of the comparison function by assuming that only one element
    // in the slice could match the target. Also, unlike [slices.BinarySearchFunc],
    // the comparison function is passed the current index of the element being
    // compared, instead of the target element.
    //
    // Not-found result is the insertion point (Go sort.Search semantics) — do not
    // replace with Array.BinarySearch, whose not-found result is a complement.
    public static (int Index, bool Found) BinarySearchUniqueFunc<T>(ReadOnlySpan<T> x, Func<int, T, int> cmp)
    {
        int n = x.Length;
        if (n == 0)
        {
            return (0, false);
        }
        int low = 0, high = n - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            int value = cmp(middle, x[middle]);
            if (value < 0)
            {
                low = middle + 1;
            }
            else if (value > 0)
            {
                high = middle - 1;
            }
            else
            {
                return (middle, true);
            }
        }
        return (low, false);
    }
}
