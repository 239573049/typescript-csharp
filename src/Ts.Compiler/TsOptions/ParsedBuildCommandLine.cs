// Ported from tsgo internal/tsoptions/parsedbuildcommandline.go
//
// SCOPE (phase 4): Locale() is stubbed — it depends on the locale package (phase 9).

using System;
using Ts.Ast;
using Ts.Core;
using Ts.TsPath;
using TsPathApi = Ts.TsPath.TsPath;

namespace Ts.TsOptions;

public sealed class ParsedBuildCommandLine
{
    public BuildOptions? BuildOptions;
    public CompilerOptions? CompilerOptions;
    public WatchOptions? WatchOptions;
    public string[]? Projects;
    public Diagnostic[]? Errors;
    public object? Raw;

    internal ComparePathsOptions comparePathsOptions;

    // PORT-NOTE: Go `sync.Once` → single-shot guarded field (thread-safety deferred, plan/08).
    private bool resolvedProjectPathsComputed;
    private string[]? resolvedProjectPaths;

    public string[]? ResolvedProjectPaths()
    {
        if (!resolvedProjectPathsComputed)
        {
            resolvedProjectPathsComputed = true;
            resolvedProjectPaths = CoreUtil.Map(Projects, project =>
                global::Ts.Core.Core.ResolveConfigFileNameOfProjectReference(
                    TsPathApi.ResolvePath(comparePathsOptions.CurrentDirectory, project)));
        }
        return resolvedProjectPaths;
    }

    // PORT-TODO: locale.Parse (phase 9). Stubbed until the locale package lands.
    public object Locale()
        => throw new NotSupportedException("ParsedBuildCommandLine.Locale: pending the locale package (phase 9).");
}
