// Ported from tsgo internal/tsoptions/parsedbuildcommandline.go
//
// Locale() now parses the real `--locale` tag (parsedbuildcommandline.go:40-45); see the
// deviation note on Ts.Locale.Locale.TryParse (no localized-message data is wired yet).

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

    // parsedbuildcommandline.go:40-45 — Locale() parses `--locale` lazily (Go `sync.Once` →
    // single-shot guarded field); failure yields Default (Go's zero tag), see Ts.Locale.Locale.
    private global::Ts.Locale.Locale locale;
    private bool localeComputed;

    public global::Ts.Locale.Locale Locale()
    {
        if (!localeComputed)
        {
            localeComputed = true;
            locale = global::Ts.Locale.Locale.TryParse(CompilerOptions!.Locale, out var parsed) ? parsed : global::Ts.Locale.Locale.Default;
        }
        return locale;
    }
}
