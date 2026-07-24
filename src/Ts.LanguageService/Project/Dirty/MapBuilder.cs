// Ported from tsgo internal/project/dirty/mapbuilder.go (whole file).

using System;
using System.Collections.Generic;

namespace Ts.Project.Dirty;

// mapbuilder.go:5-12 — MapBuilder.
public sealed class MapBuilder<K, VBase, VBuilder> where K : notnull
{
    internal Dictionary<K, VBase>? baseMap; // Go: base
    internal Dictionary<K, VBuilder> dirty;
    internal HashSet<K>? deleted;

    internal Func<VBase, VBuilder> toBuilder;
    internal Func<VBuilder, VBase> build;

    // mapbuilder.go:14-25 — NewMapBuilder.
    public MapBuilder(
        Dictionary<K, VBase>? baseMap,
        Func<VBase, VBuilder> toBuilder,
        Func<VBuilder, VBase> build)
    {
        this.baseMap = baseMap;
        dirty = new Dictionary<K, VBuilder>();
        this.toBuilder = toBuilder;
        this.build = build;
    }

    // mapbuilder.go:27-30 — Set.
    public void Set(K key, VBuilder value)
    {
        dirty[key] = value;
        deleted?.Remove(key);
    }

    // mapbuilder.go:32-39 — Delete.
    public void Delete(K key)
    {
        deleted ??= new HashSet<K>();
        deleted.Add(key);
        dirty.Remove(key);
    }

    // mapbuilder.go:41-47 — Clear.
    public void Clear()
    {
        dirty = new Dictionary<K, VBuilder>();
        deleted = new HashSet<K>((baseMap ?? new Dictionary<K, VBase>()).Count);
        // map-iter: 序无关 (仅收集键集)
        foreach (var key in (baseMap ?? new Dictionary<K, VBase>()).Keys)
        {
            deleted.Add(key);
        }
    }

    // mapbuilder.go:49-58 — Has.
    public bool Has(K key)
    {
        if (deleted != null && deleted.Contains(key))
        {
            return false;
        }
        if (dirty.ContainsKey(key))
        {
            return true;
        }
        return baseMap != null && baseMap.ContainsKey(key);
    }

    // mapbuilder.go:60-74 — Build.
    public Dictionary<K, VBase>? Build()
    {
        if (dirty.Count == 0 && (deleted == null || deleted.Count == 0))
        {
            return baseMap;
        }
        var result = baseMap == null ? new Dictionary<K, VBase>() : new Dictionary<K, VBase>(baseMap);
        // map-iter: 序无关 (删除集应用于字典)
        foreach (var key in deleted ?? new HashSet<K>())
        {
            result.Remove(key);
        }
        // map-iter: 序无关 (写入目标为字典)
        foreach (var (key, value) in dirty)
        {
            result[key] = build(value);
        }
        return result;
    }
}
