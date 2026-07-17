// Ported from tsgo internal/execute/incremental/referencemap.go.

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Ts.Collections;

namespace Ts.Execute.Incremental;

using PathT = global::Ts.TsPath.Path;

// referencemap.go:13-17
internal sealed class ReferenceMap
{
    private readonly SyncMap<PathT, Set<PathT>> references = new();
    private Dictionary<PathT, Set<PathT>>? referencedBy;
    private readonly Lock referenceByLock = new();

    // referencemap.go:19-21
    internal void StoreReferences(PathT path, Set<PathT> refs) => references.Store(path, refs);

    // referencemap.go:23-26
    internal bool TryGetReferences(PathT path, out Set<PathT>? refs) => references.TryLoad(path, out refs);

    // referencemap.go:28-30
    internal PathT[] GetPathsWithReferences() => references.Keys().ToArray();

    // referencemap.go:32-50 — sync.Once → double-checked lock.
    internal IEnumerable<PathT> GetReferencedBy(PathT path)
    {
        var map = Volatile.Read(ref referencedBy);
        if (map == null)
        {
            lock (referenceByLock)
            {
                map = referencedBy;
                if (map == null)
                {
                    map = new Dictionary<PathT, Set<PathT>>();
                    references.Range((key, value) =>
                    {
                        foreach (var reference in SetExtensions.Keys(value))
                        {
                            if (!map.TryGetValue(reference, out var set))
                            {
                                set = new Set<PathT>();
                                map[reference] = set;
                            }
                            set.Add(key);
                        }
                        return true;
                    });
                    Volatile.Write(ref referencedBy, map);
                }
            }
        }
        if (map.TryGetValue(path, out var refs))
        {
            return SetExtensions.Keys(refs);
        }
        return [];
    }
}
