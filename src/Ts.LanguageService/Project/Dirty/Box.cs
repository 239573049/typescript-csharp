// Ported from tsgo internal/project/dirty/box.go (whole file).

using System;

namespace Ts.Project.Dirty;

// box.go:3-8 — Box.
public sealed class Box<T> : DirtyValue<T> where T : Cloneable<T>
{
    internal T original;
    internal T value;
    internal bool dirty;
    internal bool delete;

    // box.go:10-12 — NewBox.
    public Box(T original)
    {
        this.original = original;
        this.value = original;
    }

    // box.go:14-19 — Value.
    public T Value()
    {
        if (delete)
        {
            return default!;
        }
        return value;
    }

    // box.go:21-23 — Original.
    public T Original()
    {
        return original;
    }

    // box.go:25-27 — Dirty.
    public bool Dirty()
    {
        return dirty;
    }

    // box.go:29-33 — Set.
    public void Set(T value)
    {
        this.value = value;
        delete = false;
        dirty = true;
    }

    // box.go:35-41 — Change.
    public void Change(Action<T> apply)
    {
        if (!dirty)
        {
            value = value.Clone();
            dirty = true;
        }
        apply(value);
    }

    // box.go:43-49 — ChangeIf.
    public bool ChangeIf(Func<T, bool> cond, Action<T> apply)
    {
        if (cond(value))
        {
            Change(apply);
            return true;
        }
        return false;
    }

    // box.go:51-53 — Delete.
    public void Delete()
    {
        delete = true;
    }

    // box.go:55-57 — Locked.
    public void Locked(Action<DirtyValue<T>> fn)
    {
        fn(this);
    }

    // box.go:59-61 — Finalize.
    public (T Value, bool Changed) Finalize()
    {
        return (Value(), dirty || delete);
    }
}
