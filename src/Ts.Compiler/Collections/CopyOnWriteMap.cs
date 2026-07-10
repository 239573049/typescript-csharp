// Ported from tsgo internal/collections/cow.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// PORT-NOTE: file name — plan/10 §2.3.2 lists cow.go → Cow.cs; this port was
// dispatched as CopyOnWriteMap.cs (both COW types live here). Registered as a
// spec ambiguity in the phase report.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Ts.Collections;

// CopyOnWriteMap is a map that defers cloning of an inherited backing map
// until the first mutation, and supports nested scopes that share the parent's
// map for reads but get their own clone on write.
//
// The zero value is an empty map ready to use.
// PORT-NOTE: "zero value" — Go uses the type as an embedded value field; the C#
// class equivalent is the parameterless constructor. Containing types must hold
// their own instance (reference aliasing replaces Go struct copying; audit any
// future Go call site that copies a struct embedding a CopyOnWriteMap by value).
public sealed class CopyOnWriteMap<K, V> where K : notnull
{
    private Dictionary<K, V>? m;
    private bool owned;

    // Get returns the value for k and whether it was present.
    public bool TryGet(K k, [MaybeNullWhen(false)] out V v)
    {
        if (m is null)
        {
            v = default;
            return false;
        }
        return m.TryGetValue(k, out v);
    }

    // Has reports whether k is in the map.
    public bool Has(K k)
    {
        return m is not null && m.ContainsKey(k);
    }

    // Set assigns v to k, cloning the inherited backing map first if necessary.
    public void Set(K k, V v)
    {
        EnsureOwned();
        m![k] = v;
    }

    private void EnsureOwned()
    {
        if (owned)
        {
            return;
        }
        if (m is null)
        {
            m = new Dictionary<K, V>();
        }
        else
        {
            m = new Dictionary<K, V>(m);
        }
        owned = true;
    }

    // EnterScope returns a scope that restores this map to its current state when
    // disposed. While the scope is active, the map shares its current backing
    // storage with the parent scope: reads see the inherited entries, and the first
    // mutation transparently clones the storage so the parent's view is not modified.
    //
    // Go returns a restore closure consumed via `defer c.EnterScope()()`
    // (cow.go:49-53); the C# shape is a named Scope ref struct consumed via
    // `using var scope = map.EnterScope();` (05-conventions.md §5.1 form ③ and
    // §5.2: state-carrying restore → named Scope ref struct, no closure allocation).
    public Scope EnterScope()
    {
        // Go: saved := *c — snapshot both fields, then mark the storage shared.
        var scope = new Scope(this, m, owned);
        owned = false;
        return scope;
    }

    // Scope restores the map's saved (m, owned) pair on Dispose. It must be consumed
    // with `using`; discarding it silently drops the restore, exactly like ignoring
    // the closure returned by Go's EnterScope.
    public readonly ref struct Scope
    {
        private readonly CopyOnWriteMap<K, V> map;
        private readonly Dictionary<K, V>? savedM;
        private readonly bool savedOwned;

        internal Scope(CopyOnWriteMap<K, V> map, Dictionary<K, V>? savedM, bool savedOwned)
        {
            this.map = map;
            this.savedM = savedM;
            this.savedOwned = savedOwned;
        }

        public void Dispose()
        {
            // Go: *c = saved
            map.m = savedM;
            map.owned = savedOwned;
        }
    }
}

public sealed class CopyOnWriteSet<K> where K : notnull
{
    private readonly CopyOnWriteMap<K, byte> m = new();

    // Has reports whether k is in the set.
    public bool Has(K k)
    {
        return m.TryGet(k, out _);
    }

    // Set adds k to the set, cloning the inherited backing map first if necessary.
    public void Add(K k)
    {
        m.Set(k, default);
    }

    // EnterScope returns a scope that restores this set to its current state when
    // disposed. While the scope is active, the set shares its current backing
    // storage with the parent scope: reads see the inherited entries, and the first
    // mutation transparently clones the storage so the parent's view is not modified.
    public CopyOnWriteMap<K, byte>.Scope EnterScope()
    {
        return m.EnterScope();
    }
}
