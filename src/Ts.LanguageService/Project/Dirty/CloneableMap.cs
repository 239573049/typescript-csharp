// Ported from tsgo internal/project/dirty/cloneablemap.go (whole file).

using System.Collections.Generic;

namespace Ts.Project.Dirty;

// cloneablemap.go:5-9 — CloneableMap.
public sealed class CloneableMap<K, V> : Dictionary<K, V>, Cloneable<CloneableMap<K, V>> where K : notnull
{
    public CloneableMap()
    {
    }

    public CloneableMap(int capacity)
        : base(capacity)
    {
    }

    public CloneableMap(IDictionary<K, V> source)
        : base(source)
    {
    }

    // cloneablemap.go:7-9 — Clone (maps.Clone: shallow copy).
    public CloneableMap<K, V> Clone()
    {
        return new CloneableMap<K, V>(this);
    }
}
