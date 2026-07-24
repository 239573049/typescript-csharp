// Ported from tsgo internal/project/dirty/util.go (whole file).

using System;
using System.Collections.Generic;

namespace Ts.Project.Dirty;

public static class DirtyUtil
{
    // util.go:5-18 — CloneMapIfNil.
    public static Dictionary<K, V> CloneMapIfNil<K, V, T>(T? dirty, T? original, Func<T, Dictionary<K, V>?> getMap)
        where K : notnull
        where T : class
    {
        var dirtyMap = getMap(dirty!);
        if (dirtyMap == null)
        {
            if (original == null)
            {
                return new Dictionary<K, V>();
            }
            var originalMap = getMap(original);
            if (originalMap == null)
            {
                return new Dictionary<K, V>();
            }
            return new Dictionary<K, V>(originalMap);
        }
        return dirtyMap;
    }
}
