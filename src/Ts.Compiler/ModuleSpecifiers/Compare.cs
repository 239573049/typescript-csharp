// Ported from tsgo internal/modulespecifiers/compare.go.

using System;

namespace Ts.ModuleSpecifiers;

public static class Compare
{
    // compare.go:7 — CountPathComponents
    public static int CountPathComponents(string path)
    {
        var initial = 0;
        if (path.StartsWith("./", StringComparison.Ordinal))
        {
            initial = 2;
        }
        var count = 0;
        for (var i = initial; i < path.Length; i++)
        {
            if (path[i] == '/')
            {
                count++;
            }
        }
        return count;
    }
}
