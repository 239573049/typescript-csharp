// Ported from tsgo internal/core/stack.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;

namespace Ts.Core;

public sealed class Stack<T>
{
    private readonly List<T?> data = new List<T?>();

    public void Push(T item)
    {
        data.Add(item);
    }

    public T Pop()
    {
        int l = data.Count;
        if (l == 0)
        {
            throw new InvalidOperationException("stack is empty");
        }
        T item = data[l - 1]!;
        data[l - 1] = default; // Go: s.data[l-1] = zero — drop the reference so the GC can collect it
        data.RemoveAt(l - 1);
        return item;
    }

    public T Peek()
    {
        int l = data.Count;
        if (l == 0)
        {
            throw new InvalidOperationException("stack is empty");
        }
        return data[l - 1]!;
    }

    // Go: func (s *Stack[T]) Len() int — simple accessor, surfaced as a property (05 §1.2).
    public int Len => data.Count;
}
