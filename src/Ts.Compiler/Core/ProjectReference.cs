// Ported from tsgo internal/core/projectreference.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.Core;

public sealed class ProjectReference
{
    // PORT-NOTE: Go string zero value is "", so both string fields default to "".
    public string Path = "";
    public string OriginalPath = "";
    public bool Circular;
}

// Package-level functions of projectreference.go collect onto `Core`
// (05-conventions.md §2.1; matches NodeModules.cs). `ref` is a C# keyword → `@ref`.
public static partial class Core
{
    public static string ResolveProjectReferencePath(ProjectReference @ref)
    {
        return ResolveConfigFileNameOfProjectReference(@ref.Path);
    }

    public static string ResolveConfigFileNameOfProjectReference(string path)
    {
        if (TsPathApi.FileExtensionIs(path, TsPathApi.ExtensionJson))
        {
            return path;
        }
        return TsPathApi.CombinePaths(path, "tsconfig.json");
    }
}
