namespace Ts.Core;

// Go: internal/core/version.go — `var version` is ldflags-overridable there;
// here the MSBuild property TsVersion is the single source of truth and this
// constant must match it (checked once version stamping is wired up in CI).
public static class TsVersion
{
    public const string Value = "7.1.0-dev";
}
