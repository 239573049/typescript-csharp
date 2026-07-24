// Ported from tsgo internal/project/dirty/interfaces.go (whole file).
// PORT-NOTE: Go's dirty.Value[T] interface is named DirtyValue<T> here — C# forbids a member
// (Value()) sharing the enclosing type's name; implementations keep the Go member names.

using System;

namespace Ts.Project.Dirty;

// interfaces.go:3-5 — Cloneable.
public interface Cloneable<T>
{
    T Clone();
}

// interfaces.go:7-15 — Value.
public interface DirtyValue<T>
{
    T Value();

    T Original();

    bool Dirty();

    void Change(Action<T> apply);

    bool ChangeIf(Func<T, bool> cond, Action<T> apply);

    void Delete();

    void Locked(Action<DirtyValue<T>> fn);
}
