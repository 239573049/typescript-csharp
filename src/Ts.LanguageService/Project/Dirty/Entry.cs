// Ported from tsgo internal/project/dirty/entry.go (whole file). Go embeds mapEntry into
// MapEntry and SyncMapEntry; C# expresses the embedding as a shared base class.

namespace Ts.Project.Dirty;

// entry.go:3-9 — mapEntry.
public abstract class MapEntryBase<K, V>
{
    internal K key = default!;
    internal V original = default!;
    internal V value = default!;
    internal bool dirty;
    internal bool delete;

    // entry.go:11-13 — Key.
    public K Key()
    {
        return key;
    }

    // entry.go:15-17 — Original.
    public V Original()
    {
        return original;
    }

    // entry.go:19-25 — Value.
    public virtual V Value()
    {
        if (delete)
        {
            return default!;
        }
        return value;
    }

    // entry.go:27-29 — Dirty.
    public virtual bool Dirty()
    {
        return dirty;
    }
}
