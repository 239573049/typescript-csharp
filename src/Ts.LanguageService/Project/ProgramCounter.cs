// Ported from tsgo internal/project/programcounter.go (whole file).

using System;
using System.Collections.Generic;
using System.Threading;

namespace Ts.Project;

// programcounter.go:9-12 — programCounter.
internal sealed class ProgramCounter
{
    private readonly Lock mu = new();
    private Dictionary<global::Ts.Compiler.Program, int> refs = [];

    // Ref increments the reference count for a program. If the program is not
    // yet tracked, it is added with a reference count of 1.
    public void Ref(global::Ts.Compiler.Program program)
    {
        lock (mu)
        {
            refs[program] = refs.GetValueOrDefault(program) + 1;
        }
    }

    public bool Deref(global::Ts.Compiler.Program program)
    {
        lock (mu)
        {
            int count = refs.GetValueOrDefault(program);
            if (!refs.ContainsKey(program))
            {
                return false;
            }
            count--;
            if (count < 0)
            {
                throw new InvalidOperationException("program reference count went below zero");
            }
            if (count == 0)
            {
                refs.Remove(program);
                return true;
            }
            refs[program] = count;
            return false;
        }
    }

    public int Len()
    {
        lock (mu)
        {
            return refs.Count;
        }
    }
}
