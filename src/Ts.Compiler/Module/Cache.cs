// Ported from tsgo internal/module/cache.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;
using Ts.PackageJson;

namespace Ts.Module;

using static Ts.Module.Module;

// Go: type ModeAwareCache[T any] map[ModeAwareCacheKey]T.
public sealed class ModeAwareCache<T> : Dictionary<ModeAwareCacheKey, T>
{
}

internal readonly record struct ModuleResolutionCacheKey(
    string ContainingDirectory,
    string ModuleName,
    ResolutionMode ResolutionMode,
    string RedirectConfigName);

internal sealed class ModuleResolutionCache
{
    private readonly SyncMap<ModuleResolutionCacheKey, ResolvedModule> cache = new();

    public bool Get(ModuleResolutionCacheKey key, out ResolvedModule? value)
    {
        return cache.TryLoad(key, out value);
    }

    public void Set(ModuleResolutionCacheKey key, ResolvedModule value)
    {
        cache.LoadOrStore(key, value);
    }
}

internal readonly record struct TypeRefDirectiveResolutionCacheKey(
    string ContainingDirectory,
    string TypeReferenceName,
    ResolutionMode ResolutionMode,
    string RedirectConfigName,
    bool FromInferredTypesContainingFile);

internal sealed class TypeRefDirectiveResolutionCache
{
    private readonly SyncMap<TypeRefDirectiveResolutionCacheKey, ResolvedTypeReferenceDirective> cache = new();

    public bool Get(TypeRefDirectiveResolutionCacheKey key, out ResolvedTypeReferenceDirective? value)
    {
        return cache.TryLoad(key, out value);
    }

    public void Set(TypeRefDirectiveResolutionCacheKey key, ResolvedTypeReferenceDirective value)
    {
        cache.Store(key, value);
    }
}

internal sealed class Caches
{
    public InfoCache PackageJsonInfoCache;

    public readonly ModuleResolutionCache ModuleResolutionCache = new();
    public readonly TypeRefDirectiveResolutionCache TypeRefDirectiveResolutionCache = new();

    // Cached representation for `core.CompilerOptions.paths`.
    // Doesn't handle other path patterns like in `typesVersions`.
    private readonly object parsedPatternsGate = new();
    private bool parsedPatternsComputed;
    private ParsedPatterns? parsedPatternsForPaths;

    public Caches(InfoCache packageJsonInfoCache)
    {
        PackageJsonInfoCache = packageJsonInfoCache;
    }

    public ParsedPatterns GetParsedPatternsForPaths(CompilerOptions options)
    {
        lock (parsedPatternsGate)
        {
            if (!parsedPatternsComputed)
            {
                parsedPatternsComputed = true;
                parsedPatternsForPaths = TryParsePatterns(options.Paths);
            }
        }
        return parsedPatternsForPaths!;
    }
}

public static partial class Module
{
    // Go: newCaches. The options argument is accepted for signature fidelity but,
    // as in Go, only feeds the package.json info cache construction.
    internal static Caches NewCaches(string currentDirectory, bool useCaseSensitiveFileNames, CompilerOptions options)
    {
        return new Caches(new InfoCache(currentDirectory, useCaseSensitiveFileNames));
    }

    internal static string GetRedirectConfigName(ResolvedProjectReference? redirect)
    {
        if (redirect == null)
        {
            return "";
        }
        return redirect.ConfigName();
    }
}
