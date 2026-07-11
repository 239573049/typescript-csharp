// Ported from tsgo internal/core/linkstore.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ts.Core;

// Go: internal/core/linkstore.go — LinkStore[K comparable, V any] = map[K]*V + Arena[V].
// Authoritative C# design in plan/03 §5.2: the arena is deleted (arena.New() → new V()),
// keys are reference types compared by identity (every tsgo instantiation keys on
// *ast.Node / *ast.Symbol, checker.go:669-694), and V is a parameterless-constructible
// sealed link class whose default field values equal Go's zero values (plan/03 §5.2).
public sealed class LinkStore<K, V>
    where K : class
    where V : class, new()
{
    // PORT-NOTE: Go lazily allocates entries on first Get (linkstore.go:15-17); C#
    // allocates at construction — externally unobservable (plan/03 §5.2 #4).
    private readonly Dictionary<K, V> entries = new(ReferenceEqualityComparer<K>.Instance);

    // Go: Get (linkstore.go:10-21) — returns the existing link, creating it (Go: arena
    // allocation of the zero value) on first access.
    public V Get(K key)
    {
        if (!entries.TryGetValue(key, out var value))
        {
            value = new V();
            entries.Add(key, value);
        }
        return value;
    }

    // Go: Has (linkstore.go:23-26).
    public bool Has(K key) => entries.ContainsKey(key);

    // Go: TryGet (linkstore.go:28-30) — returns null when absent (Go returns the nil *V).
    public V? TryGet(K key) => entries.TryGetValue(key, out var value) ? value : null;
}

// Generic reference-identity comparer.
//
// PORT-NOTE: plan/03 §5.2 and 05 §7.5 call for "ReferenceEqualityComparer.Instance", but
// the BCL's System.Collections.Generic.ReferenceEqualityComparer is IEqualityComparer<object?>
// and cannot type-check as IEqualityComparer<K> for a generic reference K — the interface
// is invariant. This generic wrapper provides the same identity semantics typed as
// IEqualityComparer<T>. (plan/11 §known-deviations.)
public sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T>
    where T : class
{
    public static readonly ReferenceEqualityComparer<T> Instance = new();

    private ReferenceEqualityComparer()
    {
    }

    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
