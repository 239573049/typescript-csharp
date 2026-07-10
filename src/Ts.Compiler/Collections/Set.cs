// Ported from tsgo internal/collections/set.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;

namespace Ts.Collections;

public sealed class Set<T>
{
    // Go: M map[T]struct{} — the exported backing map, nil until first write
    // (map[T]struct{} → HashSet<T>, 05-conventions.md §7.5; lazy-init shape kept
    // because the nil/non-nil distinction is observable through Keys/Clone).
    public HashSet<T>? M;

    public Set()
    {
    }

    // NewSetWithSizeHint creates a new Set with a hint for the number of elements it will contain.
    public Set(int sizeHint)
    {
        M = new HashSet<T>(sizeHint);
    }

    public void Add(T key)
    {
        M ??= new HashSet<T>();
        M.Add(key);
    }

    public void Delete(T key)
    {
        // Go: delete on a nil map is a no-op.
        M?.Remove(key);
    }

    // Returns true if the key was not already present in the set.
    public bool AddIfAbsent(T key)
    {
        if (this.Has(key))
        {
            return false;
        }
        Add(key);
        return true;
    }

    // PORT-NOTE: instance method rather than an extension — object.Equals(object)
    // would always win overload resolution over an extension named Equals, silently
    // degrading to reference equality. Unlike Go, a null receiver throws here
    // (Go: nil.Equals(nil) == true, nil.Equals(x) == false); call sites ported later
    // with nullable receivers must handle null explicitly. Registered in the phase
    // report as a known deviation.
    public bool Equals(Set<T>? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }
        if (other is null)
        {
            return false;
        }
        // maps.Equal treats nil and empty maps as equal.
        int len1 = M?.Count ?? 0;
        int len2 = other.M?.Count ?? 0;
        if (len1 != len2)
        {
            return false;
        }
        if (len1 == 0)
        {
            return true;
        }
        return M!.SetEquals(other.M!);
    }
}

// Nil-tolerant receivers (Go methods that begin with `if s == nil`) become extension
// methods on Set<T>? per 05-conventions.md §10.3 — the adjudicated choice over an
// empty-instance sentinel (a shared sentinel could not reproduce Go's "reads succeed,
// Union into nil panics" split).
public static class SetExtensions
{
    public static bool Has<T>(this Set<T>? s, T key)
    {
        if (s is null)
        {
            return false;
        }
        return s.M is not null && s.M.Contains(key);
    }

    public static int Len<T>(this Set<T>? s)
    {
        if (s is null)
        {
            return 0;
        }
        return s.M?.Count ?? 0;
    }

    // PORT-NOTE: Go returns the raw (possibly nil) backing map and callers `range`
    // it; `for range nil` is zero iterations in Go, while a C# foreach over null
    // throws — so a nil receiver/backing map yields an empty sequence instead,
    // which is observably identical at every Go call site.
    public static IEnumerable<T> Keys<T>(this Set<T>? s)
    {
        if (s is null)
        {
            return [];
        }
        return (IEnumerable<T>?)s.M ?? [];
    }

    public static void Clear<T>(this Set<T>? s)
    {
        if (s is null)
        {
            return;
        }
        // Go: clear on a nil map is a no-op.
        s.M?.Clear();
    }

    public static Set<T>? Clone<T>(this Set<T>? s)
    {
        if (s is null)
        {
            return null;
        }
        // maps.Clone(nil) == nil: a never-written set clones to a never-written set.
        var clone = new Set<T> { M = s.M is null ? null : new HashSet<T>(s.M) };
        return clone;
    }

    public static void Union<T>(this Set<T>? s, Set<T>? other)
    {
        if (s.Len() == 0 && other.Len() == 0)
        {
            return;
        }
        if (s is null)
        {
            throw new InvalidOperationException("cannot modify nil Set");
        }
        if (s.M is null)
        {
            // maps.Clone(other.M); Go dereferences `other` here and panics when it is
            // nil — the null-forgiving access throws the equivalent NRE. other.M is
            // non-null on this path (other.Len() != 0 because s is empty).
            s.M = new HashSet<T>(other!.M!);
            return;
        }
        // maps.Copy(s.M, other.M) — copying from a nil map is a no-op; a nil `other`
        // panics in Go and throws NRE here.
        if (other!.M is not null)
        {
            s.M.UnionWith(other.M);
        }
    }

    public static Set<T>? UnionedWith<T>(this Set<T>? s, Set<T>? other)
    {
        if (s is null && other is null)
        {
            return null;
        }
        var result = s.Clone();
        if (other is not null)
        {
            result ??= new Set<T>();
            result.M ??= new HashSet<T>(other.M?.Count ?? 0);
            // maps.Copy — copying from a nil map is a no-op.
            if (other.M is not null)
            {
                result.M.UnionWith(other.M);
            }
        }
        return result;
    }

    public static bool IsSubsetOf<T>(this Set<T>? s, Set<T>? other)
    {
        if (s is null)
        {
            return true;
        }
        if (s.M is not null)
        {
            // map-iter: 序无关 —— 任意序下命中第一个缺失键即 false，结果与顺序无关
            foreach (var key in s.M)
            {
                if (!other.Has(key))
                {
                    return false;
                }
            }
        }
        return true;
    }

    public static bool Intersects<T>(this Set<T>? s, Set<T>? other)
    {
        if (s is null || other is null)
        {
            return false;
        }
        if (s.M is not null)
        {
            // map-iter: 序无关 —— 任意序下命中第一个交集键即 true，结果与顺序无关
            foreach (var key in s.M)
            {
                if (other.Has(key))
                {
                    return true;
                }
            }
        }
        return false;
    }
}

// Package-level functions of set.go (05-conventions.md §2.1).
public static class Set
{
    // Go: NewSetFromItems (Go-style New prefix dropped, 05-conventions.md §0.3).
    public static Set<T> FromItems<T>(params T[] items)
    {
        var s = new Set<T>();
        foreach (var item in items)
        {
            s.Add(item);
        }
        return s;
    }
}
