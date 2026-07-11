using System;

namespace Ts.Core;

// Go: internal/core/version.go. `var version` is ldflags-overridable there; here
// the MSBuild property TsVersion is the single source of truth and Value must
// match it (checked once version stamping is wired up in CI).
public static class TsVersion
{
    public const string Value = "7.1.0-dev";

    // Go: core.Version().
    public static string Version() => Value;

    // Go: core.VersionMajorMinor() — memoized "major.minor" prefix. Panics on a
    // version string with no '.' (Go behavior).
    private static readonly Lazy<string> s_versionMajorMinor = new(ComputeVersionMajorMinor);

    public static string VersionMajorMinor() => s_versionMajorMinor.Value;

    private static string ComputeVersionMajorMinor()
    {
        bool seenMajor = false;
        int i = -1;
        for (int k = 0; k < Value.Length; k++)
        {
            if (Value[k] == '.')
            {
                if (seenMajor)
                {
                    i = k;
                    break;
                }
                seenMajor = true;
            }
        }
        if (i == -1)
        {
            throw new InvalidOperationException("invalid version string: " + Value);
        }
        return Value[..i];
    }
}
