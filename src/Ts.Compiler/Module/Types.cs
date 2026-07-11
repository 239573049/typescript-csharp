// Ported from tsgo internal/module/types.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Core;
using Ts.Vfs;

namespace Ts.Module;

// PORT-NOTE: aliases inside the file-scoped namespace so they outrank the enclosing
// `Ts` namespace's sibling sub-namespaces.
using TsPathApi = Ts.TsPath.TsPath;

public interface ResolutionHost
{
    IFileSystem FS();
    string GetCurrentDirectory();
}

public readonly record struct ModeAwareCacheKey(string Name, ResolutionMode Mode);

public interface ResolvedProjectReference
{
    string ConfigName();
    CompilerOptions? CompilerOptions();
}

[Flags]
public enum NodeResolutionFeatures
{
    None = 0,
    Imports = 1 << 0,
    SelfName = 1 << 1,
    Exports = 1 << 2,
    ExportsPatternTrailers = 1 << 3,
    // allowing `#/` root imports in package.json imports field
    // not supported until mass adoption - https://github.com/nodejs/node/pull/60864
    ImportsPatternRoot = 1 << 4,

    All = Imports | SelfName | Exports | ExportsPatternTrailers | ImportsPatternRoot,
    Node16Default = Imports | SelfName | Exports | ExportsPatternTrailers,
    NodeNextDefault = All,
    BundlerDefault = Imports | SelfName | Exports | ExportsPatternTrailers | ImportsPatternRoot,
}

public struct PackageId
{
    public string Name;
    public string SubModuleName;
    public string Version;
    public string PeerDependencies;

    // Go: func (p *PackageId) String() string — fmt.Sprintf("%s@%s%s", ...).
    public override readonly string ToString()
    {
        return (PackageName() ?? "") + "@" + (Version ?? "") + (PeerDependencies ?? "");
    }

    public readonly string PackageName()
    {
        // PORT-NOTE: null coalesced so default(PackageId) behaves like Go's zero PackageId{}.
        if (!string.IsNullOrEmpty(SubModuleName))
        {
            return Name + "/" + SubModuleName;
        }
        return Name ?? "";
    }
}

public sealed class ResolvedModule
{
    public List<Diagnostic>? ResolutionDiagnostics;
    public string ResolvedFileName = "";
    public string OriginalPath = "";
    public string Extension = "";
    public bool ResolvedUsingTsExtension;
    public PackageId PackageId;
    public bool IsExternalLibraryImport;
    public string AlternateResult = "";
}

public sealed class ResolvedTypeReferenceDirective
{
    public List<Diagnostic>? ResolutionDiagnostics;
    public bool Primary;
    public string ResolvedFileName = "";
    public string OriginalPath = "";
    public PackageId PackageId;
    public bool IsExternalLibraryImport;
}

// Go: nil-receiver IsResolved methods become nil-tolerant extension methods.
public static class ResolvedModuleExtensions
{
    // Go: func (r *ResolvedModule) IsResolved() bool { return r != nil && r.ResolvedFileName != "" }.
    public static bool IsResolved(this ResolvedModule? r)
    {
        return r != null && r.ResolvedFileName != "";
    }

    // Go: func (r *ResolvedTypeReferenceDirective) IsResolved() bool { return r.ResolvedFileName != "" }.
    public static bool IsResolved(this ResolvedTypeReferenceDirective? r)
    {
        return r != null && r.ResolvedFileName != "";
    }
}

[Flags]
internal enum Extensions
{
    TypeScript = 1 << 0,
    JavaScript = 1 << 1,
    Declaration = 1 << 2,
    Json = 1 << 3,

    ImplementationFiles = TypeScript | JavaScript,
}

internal static class ExtensionsExtensions
{
    // Go: func (e extensions) String() string.
    public static string ToDisplayString(this Extensions e)
    {
        var result = new List<string>(4);
        if ((e & Extensions.TypeScript) != 0)
        {
            result.Add("TypeScript");
        }
        if ((e & Extensions.JavaScript) != 0)
        {
            result.Add("JavaScript");
        }
        if ((e & Extensions.Declaration) != 0)
        {
            result.Add("Declaration");
        }
        if ((e & Extensions.Json) != 0)
        {
            result.Add("JSON");
        }
        return string.Join(", ", result);
    }

    // Go: func (e extensions) Array() []string.
    public static string[] ToArray(this Extensions e)
    {
        var result = new List<string>();
        if ((e & Extensions.TypeScript) != 0)
        {
            result.AddRange(TsPathApi.SupportedTSImplementationExtensions);
        }
        if ((e & Extensions.JavaScript) != 0)
        {
            result.AddRange(TsPathApi.SupportedJSExtensionsFlat);
        }
        if ((e & Extensions.Declaration) != 0)
        {
            result.AddRange(TsPathApi.SupportedDeclarationExtensions);
        }
        if ((e & Extensions.Json) != 0)
        {
            result.Add(TsPathApi.ExtensionJson);
        }
        return result.ToArray();
    }
}
