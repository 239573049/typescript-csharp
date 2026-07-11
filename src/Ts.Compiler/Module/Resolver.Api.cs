// Ported from tsgo internal/module/resolver.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)
// Second partial: public Resolver, package-level functions, and entrypoint discovery.

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.PackageJson;
using Ts.Vfs;

namespace Ts.Module;

using CoreUtil = Ts.Core.CoreUtil;
using Core = Ts.Core.Core;
using Diagnostics = Ts.Diagnostics.Diagnostics;
using TsPathApi = Ts.TsPath.TsPath;
using StringUtilApi = Ts.StringUtil.StringUtil;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch;
using Path = Ts.TsPath.Path;

using static Ts.Module.Module;

public sealed class ResolverOptions
{
    public InfoCache? PackageJsonCache;
}

public sealed class Resolver
{
    internal readonly Caches caches;
    internal readonly ResolutionHost host;
    internal readonly CompilerOptions compilerOptions;
    internal readonly string typingsLocation;
    internal readonly string projectName;

    // Go: NewResolver.
    public Resolver(ResolutionHost host, CompilerOptions options, string typingsLocation, string projectName)
    {
        this.host = host;
        this.caches = NewCaches(host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames(), options);
        this.compilerOptions = options;
        this.typingsLocation = typingsLocation;
        this.projectName = projectName;
    }

    // Go: NewResolverWithOptions.
    public Resolver(ResolutionHost host, CompilerOptions compilerOptions, string typingsLocation, string projectName, ResolverOptions opts)
    {
        this.host = host;
        this.compilerOptions = compilerOptions;
        this.typingsLocation = typingsLocation;
        this.projectName = projectName;
        if (opts.PackageJsonCache != null)
        {
            this.caches = new Caches(opts.PackageJsonCache);
        }
        else
        {
            this.caches = NewCaches(host.GetCurrentDirectory(), host.FS().UseCaseSensitiveFileNames(), compilerOptions);
        }
    }

    internal Tracer? NewTraceBuilder()
    {
        if (compilerOptions.TraceResolution == Tristate.True)
        {
            return new Tracer();
        }
        return null;
    }

    public InfoCacheEntry? GetPackageScopeForPath(string directory)
    {
        return new ResolutionState { compilerOptions = compilerOptions, resolver = this }.GetPackageScopeForPath(directory);
    }

    public void PackageJsonCacheEntries(Func<Path, InfoCacheEntry, bool> f)
    {
        caches.PackageJsonInfoCache.Range(f);
    }

    internal ParsedPatterns GetParsedPatternsForPaths()
    {
        return caches.GetParsedPatternsForPaths(compilerOptions);
    }

    public (ResolvedTypeReferenceDirective Result, List<DiagAndArgs>? Traces) ResolveTypeReferenceDirective(
        string typeReferenceDirectiveName,
        string containingFile,
        ResolutionMode resolutionMode,
        ResolvedProjectReference? redirectedReference)
    {
        string containingDirectory = TsPathApi.GetDirectoryPath(containingFile);
        Tracer? traceBuilder = NewTraceBuilder();

        bool fromInferredTypesContainingFile = containingFile.EndsWith(InferredTypesContainingFile, StringComparison.Ordinal);

        var cacheKey = new TypeRefDirectiveResolutionCacheKey(
            containingDirectory,
            typeReferenceDirectiveName,
            resolutionMode,
            GetRedirectConfigName(redirectedReference),
            fromInferredTypesContainingFile);

        if (traceBuilder == null)
        {
            if (caches.TypeRefDirectiveResolutionCache.Get(cacheKey, out ResolvedTypeReferenceDirective? cached))
            {
                return (cached!, null);
            }
        }

        CompilerOptions options = GetCompilerOptionsWithRedirect(compilerOptions, redirectedReference);

        (string[] typeRoots, bool fromConfig) = options.GetEffectiveTypeRoots(host.GetCurrentDirectory());
        if (traceBuilder != null)
        {
            traceBuilder.Write(Diagnostics.Resolving_type_reference_directive_0_containing_file_1_root_directory_2, typeReferenceDirectiveName, containingFile, string.Join(",", typeRoots));
            traceBuilder.TraceResolutionUsingProjectReference(redirectedReference);
        }

        ResolutionState state = ResolutionState.NewResolutionState(typeReferenceDirectiveName, containingDirectory, true, resolutionMode, options, redirectedReference, this, traceBuilder);
        ResolvedTypeReferenceDirective result = state.ResolveTypeReferenceDirective(typeRoots, fromConfig, fromInferredTypesContainingFile);

        if (traceBuilder != null)
        {
            traceBuilder.TraceTypeReferenceDirectiveResult(typeReferenceDirectiveName, result);
        }

        caches.TypeRefDirectiveResolutionCache.Set(cacheKey, result);

        return (result, traceBuilder?.GetTraces());
    }

    public (ResolvedModule Result, List<DiagAndArgs>? Traces) ResolveModuleName(
        string moduleName,
        string containingFile,
        ResolutionMode resolutionMode,
        ResolvedProjectReference? redirectedReference)
    {
        string containingDirectory = TsPathApi.GetDirectoryPath(containingFile);
        Tracer? traceBuilder = NewTraceBuilder();

        var cacheKey = new ModuleResolutionCacheKey(
            containingDirectory,
            moduleName,
            resolutionMode,
            GetRedirectConfigName(redirectedReference));

        if (traceBuilder == null)
        {
            if (caches.ModuleResolutionCache.Get(cacheKey, out ResolvedModule? cached))
            {
                return (cached!, null);
            }
        }

        CompilerOptions options = GetCompilerOptionsWithRedirect(compilerOptions, redirectedReference);
        if (traceBuilder != null)
        {
            traceBuilder.Write(Diagnostics.Resolving_module_0_from_1, moduleName, containingFile);
            traceBuilder.TraceResolutionUsingProjectReference(redirectedReference);
        }

        ModuleResolutionKind moduleResolution = options.GetModuleResolutionKind();
        if (options.ModuleResolution != moduleResolution)
        {
            if (traceBuilder != null)
            {
                traceBuilder.Write(Diagnostics.Module_resolution_kind_is_not_specified_using_0, moduleResolution.ToDisplayString());
            }
        }
        else
        {
            if (traceBuilder != null)
            {
                traceBuilder.Write(Diagnostics.Explicitly_specified_module_resolution_kind_Colon_0, moduleResolution.ToDisplayString());
            }
        }

        ResolvedModule result;
        switch (moduleResolution)
        {
            case ModuleResolutionKind.Node16:
            case ModuleResolutionKind.NodeNext:
            case ModuleResolutionKind.Bundler:
                ResolutionState state = ResolutionState.NewResolutionState(moduleName, containingDirectory, false, resolutionMode, options, redirectedReference, this, traceBuilder);
                result = state.ResolveNodeLike();
                break;
            default:
                Debug.Fail($"Unexpected moduleResolution: {(int)moduleResolution}");
                throw new InvalidOperationException(); // unreachable
        }

        if (traceBuilder != null)
        {
            if (result.IsResolved())
            {
                if (!string.IsNullOrEmpty(result.PackageId.Name))
                {
                    traceBuilder.Write(Diagnostics.Module_name_0_was_successfully_resolved_to_1_with_Package_ID_2, moduleName, result.ResolvedFileName, result.PackageId.ToString());
                }
                else
                {
                    traceBuilder.Write(Diagnostics.Module_name_0_was_successfully_resolved_to_1, moduleName, result.ResolvedFileName);
                }
            }
            else
            {
                traceBuilder.Write(Diagnostics.Module_name_0_was_not_resolved, moduleName);
            }
        }

        ResolvedModule finalResult = TryResolveFromTypingsLocation(moduleName, containingDirectory, result, traceBuilder);
        caches.ModuleResolutionCache.Set(cacheKey, finalResult);

        return (finalResult, traceBuilder?.GetTraces());
    }

    public ResolvedModule? ResolvePackageDirectory(string moduleName, string containingFile, ResolutionMode resolutionMode, ResolvedProjectReference? redirectedReference)
    {
        CompilerOptions options = GetCompilerOptionsWithRedirect(compilerOptions, redirectedReference);
        string containingDirectory = TsPathApi.GetDirectoryPath(containingFile);
        ResolutionState state = ResolutionState.NewResolutionState(moduleName, containingDirectory, false, resolutionMode, options, redirectedReference, this, null);
        state.resolvePackageDirectoryOnly = true;
        Resolved? result = state.LoadModuleFromNearestNodeModulesDirectory(false);
        if (result != null && result.Path != "")
        {
            return state.CreateResolvedModuleHandlingSymlink(result);
        }
        return null;
    }

    internal ResolvedModule TryResolveFromTypingsLocation(string moduleName, string containingDirectory, ResolvedModule originalResult, Tracer? traceBuilder)
    {
        if (typingsLocation == "" ||
            TsPathApi.IsExternalModuleNameRelative(moduleName) ||
            (originalResult.ResolvedFileName != "" && TsPathApi.ExtensionIsOneOf(originalResult.Extension, TsPathApi.SupportedTSExtensionsWithJsonFlat)))
        {
            return originalResult;
        }

        ResolutionState state = ResolutionState.NewResolutionState(
            moduleName,
            containingDirectory,
            false,
            ModuleKind.None,
            compilerOptions,
            null,
            this,
            traceBuilder);
        if (traceBuilder != null)
        {
            traceBuilder.Write(Diagnostics.Auto_discovery_for_typings_is_enabled_in_project_0_Running_extra_resolution_pass_for_module_1_using_cache_location_2, projectName, moduleName, typingsLocation);
        }
        Resolved? globalResolved = state.LoadModuleFromImmediateNodeModulesDirectory(Extensions.Declaration, typingsLocation, false);
        if (globalResolved == null)
        {
            return originalResult;
        }
        ResolvedModule result = state.CreateResolvedModule(globalResolved, true);
        var combined = new List<Diagnostic>();
        if (originalResult.ResolutionDiagnostics != null)
        {
            combined.AddRange(originalResult.ResolutionDiagnostics);
        }
        if (result.ResolutionDiagnostics != null)
        {
            combined.AddRange(result.ResolutionDiagnostics);
        }
        result.ResolutionDiagnostics = combined;
        return result;
    }

    internal ResolvedModule ResolveConfig(string moduleName, string containingFile)
    {
        string containingDirectory = TsPathApi.GetDirectoryPath(containingFile);
        ResolutionState state = ResolutionState.NewResolutionState(moduleName, containingDirectory, false, ModuleKind.CommonJS, compilerOptions, null, this, null);
        state.isConfigLookup = true;
        state.extensions = Extensions.Json;
        return state.ResolveNodeLike();
    }

    public List<ResolvedEntrypoint>? GetEntrypointsFromPackageJsonInfo(InfoCacheEntry? packageJson, string packageName, bool enableDirectorySearch)
    {
        Extensions extensions = Extensions.TypeScript | Extensions.Declaration;
        NodeResolutionFeatures features = NodeResolutionFeatures.All;
        var state = new ResolutionState { resolver = this, extensions = extensions, features = features, compilerOptions = compilerOptions };
        if (packageJson.Exists() && packageJson!.Contents!.Exports.IsPresent())
        {
            List<ResolvedEntrypoint> entrypoints = state.LoadEntrypointsFromExportMap(packageJson, packageName, packageJson.Contents.Exports);
            return entrypoints;
        }

        var result = new List<ResolvedEntrypoint>();
        Resolved? mainResolution = state.LoadNodeModuleFromDirectoryWorker(
            extensions,
            packageJson!.PackageDirectory,
            packageJson);

        if (mainResolution.IsResolved())
        {
            result.Add(CreateResolvedEntrypointHandlingSymlink(
                mainResolution!.Path,
                packageName,
                null,
                null,
                Ending.Fixed));
        }

        if (enableDirectorySearch)
        {
            List<string> otherFiles = VfsMatchApi.ReadDirectory(
                host.FS(),
                host.GetCurrentDirectory(),
                packageJson.PackageDirectory,
                extensions.ToArray(),
                new[] { "node_modules" },
                new[] { "**/*" },
                VfsMatchApi.UnlimitedDepth);

            var comparePathsOptions = new Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = host.FS().UseCaseSensitiveFileNames() };
            foreach (string file in otherFiles)
            {
                if (mainResolution.IsResolved() && TsPathApi.ComparePaths(file, mainResolution!.Path, comparePathsOptions) == 0)
                {
                    continue;
                }

                result.Add(CreateResolvedEntrypointHandlingSymlink(
                    file,
                    TsPathApi.ResolvePath(packageName, TsPathApi.GetRelativePathFromDirectory(packageJson.PackageDirectory, file, comparePathsOptions)),
                    null,
                    null,
                    Ending.Changeable));
            }
        }

        if (result.Count > 0)
        {
            return result;
        }
        return null;
    }

    internal ResolvedEntrypoint CreateResolvedEntrypointHandlingSymlink(string fileName, string moduleSpecifier, Set<string>? includeConditions, Set<string>? excludeConditions, Ending ending)
    {
        string originalFileName = "";
        string resolvedFileName = fileName;
        string realPath = host.FS().Realpath(fileName);
        if (realPath != fileName)
        {
            originalFileName = fileName;
            resolvedFileName = realPath;
        }
        return new ResolvedEntrypoint
        {
            OriginalFileName = originalFileName,
            ResolvedFileName = resolvedFileName,
            ModuleSpecifier = moduleSpecifier,
            IncludeConditions = includeConditions,
            ExcludeConditions = excludeConditions,
            Ending = ending,
        };
    }
}

public enum Ending
{
    // Fixed indicates that the module specifier cannot be changed without changing its resolution.
    Fixed,
    // ExtensionChangeable indicates the extension portion was inferred from disk and can be swapped.
    ExtensionChangeable,
    // Changeable indicates the file name and extension portion were inferred from disk.
    Changeable,
}

public sealed class ResolvedEntrypoint
{
    // OriginalFileName is the symlink path if the entrypoint was discovered at a symlink. Empty otherwise.
    public string OriginalFileName = "";
    // ResolvedFileName is the real path to the entrypoint file.
    public string ResolvedFileName = "";
    public string ModuleSpecifier = "";
    // Ending indicates whether the file name and extension portion of ModuleSpecifier is fixed or changeable.
    public Ending Ending;
    // IncludeConditions are the conditions that a resolver must have to reach this entrypoint.
    public Set<string>? IncludeConditions;
    // ExcludeConditions are the conditions that a resolver must not have to reach this entrypoint.
    public Set<string>? ExcludeConditions;

    public string SymlinkOrRealpath()
    {
        if (OriginalFileName != "")
        {
            return OriginalFileName;
        }
        return ResolvedFileName;
    }
}

internal sealed partial class ResolutionState
{
    public List<ResolvedEntrypoint> LoadEntrypointsFromExportMap(
        InfoCacheEntry packageJson,
        string packageName,
        ExportsOrImports exports)
    {
        var entrypoints = new List<ResolvedEntrypoint>();

        void LoadEntrypointsFromTargetExports(string subpath, Set<string>? includeConditions, Set<string>? excludeConditions, ExportsOrImports exports)
        {
            if (exports.Type == JSONValueType.JSONValueTypeString && exports.AsString().StartsWith("./", StringComparison.Ordinal))
            {
                if (exports.AsString().Contains('*'))
                {
                    if (exports.AsString().IndexOf('*') != exports.AsString().LastIndexOf('*'))
                    {
                        return;
                    }
                    string patternPath = TsPathApi.ResolvePath(packageJson.PackageDirectory, exports.AsString());
                    (string leadingSlice, string trailingSlice, _) = Cut(patternPath, "*");
                    bool caseSensitive = resolver.host.FS().UseCaseSensitiveFileNames();
                    List<string> files = VfsMatchApi.ReadDirectory(
                        resolver.host.FS(),
                        resolver.host.GetCurrentDirectory(),
                        packageJson.PackageDirectory,
                        extensions.ToArray(),
                        null,
                        new[] { TsPathApi.ChangeFullExtension(ReplaceFirst(exports.AsString(), "*", "**/*"), ".*") },
                        VfsMatchApi.UnlimitedDepth);
                    foreach (string file in files)
                    {
                        (string matchedStar, bool ok) = GetMatchedStarForPatternEntrypoint(file, leadingSlice, trailingSlice, caseSensitive);
                        if (!ok)
                        {
                            continue;
                        }
                        string moduleSpecifier = TsPathApi.ResolvePath(packageName, ReplaceFirst(subpath, "*", matchedStar));
                        entrypoints.Add(resolver.CreateResolvedEntrypointHandlingSymlink(
                            file,
                            moduleSpecifier,
                            includeConditions,
                            excludeConditions,
                            CoreUtil.IfElse(exports.AsString().EndsWith("*", StringComparison.Ordinal), Ending.ExtensionChangeable, Ending.Fixed)));
                    }
                }
                else
                {
                    List<string> comps = TsPathApi.GetPathComponents(exports.AsString(), "");
                    // partsAfterFirst = comps[2:]
                    if (ContainsAny(comps, 2))
                    {
                        return;
                    }
                    string resolvedTarget = TsPathApi.ResolvePath(packageJson.PackageDirectory, exports.AsString());
                    Resolved? result = LoadFileNameFromPackageJSONField(extensions, resolvedTarget, exports.AsString());
                    if (result.IsResolved())
                    {
                        entrypoints.Add(resolver.CreateResolvedEntrypointHandlingSymlink(
                            result!.Path,
                            TsPathApi.ResolvePath(packageName, subpath),
                            includeConditions,
                            excludeConditions,
                            CoreUtil.IfElse(exports.AsString().EndsWith("*", StringComparison.Ordinal), Ending.ExtensionChangeable, Ending.Fixed)));
                    }
                }
            }
            else if (exports.Type == JSONValueType.JSONValueTypeArray)
            {
                foreach (ExportsOrImports element in exports.AsArray())
                {
                    LoadEntrypointsFromTargetExports(subpath, includeConditions, excludeConditions, element);
                }
            }
            else if (exports.Type == JSONValueType.JSONValueTypeObject)
            {
                List<string>? prevConditions = null;
                // map-iter: 已排序 (conditions evaluated in package.json order).
                foreach (KeyValuePair<string, ExportsOrImports> entry in exports.AsObject().Entries())
                {
                    string condition = entry.Key;
                    ExportsOrImports export = entry.Value;
                    if (excludeConditions != null && excludeConditions.Has(condition))
                    {
                        continue;
                    }

                    bool conditionAlwaysMatches = condition == "default" || condition == "types" || IsApplicableVersionedTypesKey(condition);
                    Set<string>? newIncludeConditions = includeConditions;
                    if (!conditionAlwaysMatches)
                    {
                        newIncludeConditions = includeConditions.Clone();
                        excludeConditions = excludeConditions.Clone();
                        if (newIncludeConditions == null)
                        {
                            newIncludeConditions = new Set<string>();
                        }
                        newIncludeConditions.Add(condition);
                        if (prevConditions != null)
                        {
                            foreach (string prevCondition in prevConditions)
                            {
                                if (excludeConditions == null)
                                {
                                    excludeConditions = new Set<string>();
                                }
                                excludeConditions.Add(prevCondition);
                            }
                        }
                    }

                    (prevConditions ??= new List<string>()).Add(condition);
                    LoadEntrypointsFromTargetExports(subpath, newIncludeConditions, excludeConditions, export);
                    if (conditionAlwaysMatches)
                    {
                        break;
                    }
                }
            }
        }

        switch (exports.Type)
        {
            case JSONValueType.JSONValueTypeArray:
                foreach (ExportsOrImports element in exports.AsArray())
                {
                    LoadEntrypointsFromTargetExports(".", null, null, element);
                }
                break;
            case JSONValueType.JSONValueTypeObject:
                if (exports.IsSubpaths())
                {
                    // map-iter: 已排序
                    foreach (KeyValuePair<string, ExportsOrImports> entry in exports.AsObject().Entries())
                    {
                        LoadEntrypointsFromTargetExports(entry.Key, null, null, entry.Value);
                    }
                }
                else
                {
                    LoadEntrypointsFromTargetExports(".", null, null, exports);
                }
                break;
            default:
                LoadEntrypointsFromTargetExports(".", null, null, exports);
                break;
        }

        return entrypoints;
    }

    public (string MatchedStar, bool Ok) GetMatchedStarForPatternEntrypoint(string file, string leadingSlice, string trailingSlice, bool caseSensitive)
    {
        if (StringUtilApi.HasPrefixAndSuffixWithoutOverlap(file, leadingSlice, trailingSlice, caseSensitive))
        {
            return (file.Substring(leadingSlice.Length, file.Length - trailingSlice.Length - leadingSlice.Length), true);
        }

        string jsExtension = TryGetJSExtensionForFile(file, compilerOptions);
        if (jsExtension.Length > 0)
        {
            string swapped = TsPathApi.ChangeFullExtension(file, jsExtension);
            if (StringUtilApi.HasPrefixAndSuffixWithoutOverlap(swapped, leadingSlice, trailingSlice, caseSensitive))
            {
                return (swapped.Substring(leadingSlice.Length, swapped.Length - trailingSlice.Length - leadingSlice.Length), true);
            }
        }

        return ("", false);
    }
}

public static partial class Module
{
    internal static Resolved? ContinueSearching()
    {
        return null;
    }

    internal static Resolved Unresolved()
    {
        return new Resolved();
    }

    internal static bool SliceContains(string[]? s, string v)
    {
        return s != null && Array.IndexOf(s, v) >= 0;
    }

    internal static int CountChar(string s, char c)
    {
        int n = 0;
        foreach (char ch in s)
        {
            if (ch == c)
            {
                n++;
            }
        }
        return n;
    }

    public static CompilerOptions GetCompilerOptionsWithRedirect(CompilerOptions compilerOptions, ResolvedProjectReference? redirectedReference)
    {
        if (redirectedReference == null)
        {
            return compilerOptions;
        }
        CompilerOptions? optionsFromRedirect = redirectedReference.CompilerOptions();
        if (optionsFromRedirect != null)
        {
            return optionsFromRedirect;
        }
        return compilerOptions;
    }

    public static string[] GetConditions(CompilerOptions options, ResolutionMode resolutionMode)
    {
        ModuleResolutionKind moduleResolution = options.GetModuleResolutionKind();
        if (resolutionMode == ModuleKind.None && moduleResolution == ModuleResolutionKind.Bundler)
        {
            resolutionMode = ModuleKind.ESNext;
        }
        var conditions = new List<string>(3 + (options.CustomConditions?.Length ?? 0));
        if (resolutionMode == ModuleKind.ESNext)
        {
            conditions.Add("import");
        }
        else
        {
            conditions.Add("require");
        }

        if (options.NoDtsResolution != Tristate.True)
        {
            conditions.Add("types");
        }
        if (moduleResolution != ModuleResolutionKind.Bundler)
        {
            conditions.Add("node");
        }
        return CoreUtil.Concatenate(conditions.ToArray(), options.CustomConditions) ?? Array.Empty<string>();
    }

    internal static NodeResolutionFeatures GetNodeResolutionFeatures(CompilerOptions options)
    {
        NodeResolutionFeatures features = NodeResolutionFeatures.None;

        switch (options.GetModuleResolutionKind())
        {
            case ModuleResolutionKind.Node16:
                features = NodeResolutionFeatures.Node16Default;
                break;
            case ModuleResolutionKind.NodeNext:
                features = NodeResolutionFeatures.NodeNextDefault;
                break;
            case ModuleResolutionKind.Bundler:
                features = NodeResolutionFeatures.BundlerDefault;
                break;
        }
        if (options.ResolvePackageJsonExports == Tristate.True)
        {
            features |= NodeResolutionFeatures.Exports;
        }
        else if (options.ResolvePackageJsonExports == Tristate.False)
        {
            features &= ~NodeResolutionFeatures.Exports;
        }
        if (options.ResolvePackageJsonImports == Tristate.True)
        {
            features |= NodeResolutionFeatures.Imports;
        }
        else if (options.ResolvePackageJsonImports == Tristate.False)
        {
            features &= ~NodeResolutionFeatures.Imports;
        }
        return features;
    }

    internal static int MoveToNextDirectorySeparatorIfAvailable(string path, int prevSeparatorIndex, bool isFolder)
    {
        int offset = prevSeparatorIndex + 1;
        int nextSeparatorIndex = -1;
        if (offset <= path.Length)
        {
            nextSeparatorIndex = path.AsSpan(offset).IndexOf('/');
        }
        if (nextSeparatorIndex == -1)
        {
            if (isFolder)
            {
                return path.Length;
            }
            return prevSeparatorIndex;
        }
        return nextSeparatorIndex + offset;
    }

    public static ParsedPatterns TryParsePatterns(OrderedMap<string, string[]>? pathMappings)
    {
        int numPatterns = 0;
        // map-iter: 已排序 (OrderedMap preserves insertion order; result is order-independent).
        foreach (string path in pathMappings.Keys())
        {
            Pattern pattern = Core.TryParsePattern(path);
            if (pattern.IsValid && pattern.StarIndex == -1)
            {
                numPatterns++;
            }
        }
        int numMatchables = pathMappings.Size() - numPatterns;

        List<Pattern>? patterns = null;
        Set<string>? matchableStringSet = null;
        if (numPatterns != 0)
        {
            patterns = new List<Pattern>(numPatterns);
        }
        if (numMatchables != 0)
        {
            matchableStringSet = new Set<string>(numMatchables);
        }

        // map-iter: 已排序
        foreach (string path in pathMappings.Keys())
        {
            Pattern pattern = Core.TryParsePattern(path);
            if (pattern.IsValid)
            {
                if (pattern.StarIndex == -1)
                {
                    (matchableStringSet ??= new Set<string>()).Add(path);
                }
                else
                {
                    (patterns ??= new List<Pattern>()).Add(pattern);
                }
            }
        }
        return new ParsedPatterns
        {
            MatchableStringSet = matchableStringSet,
            Patterns = patterns?.ToArray(),
        };
    }

    public static Pattern MatchPatternOrExact(ParsedPatterns patterns, string candidate)
    {
        if (patterns.MatchableStringSet.Has(candidate))
        {
            return new Pattern(candidate, -1);
        }
        if (patterns.Patterns == null || patterns.Patterns.Length == 0)
        {
            return default;
        }
        return Core.FindBestPatternMatch(patterns.Patterns, static (Pattern p) => p, candidate);
    }

    internal static string NormalizePathForCJSResolution(string containingDirectory, string moduleName)
    {
        string combined = TsPathApi.CombinePaths(containingDirectory, moduleName);
        List<string> parts = TsPathApi.GetPathComponents(combined, "");
        string lastPart = parts[parts.Count - 1];
        if (lastPart == "." || lastPart == "..")
        {
            return TsPathApi.EnsureTrailingDirectorySeparator(TsPathApi.NormalizePath(combined));
        }
        return TsPathApi.NormalizePath(combined);
    }

    internal static bool MatchesPatternWithTrailer(string target, string name)
    {
        if (target.EndsWith("*", StringComparison.Ordinal))
        {
            return false;
        }
        (string before, string after, bool ok) = Cut(target, "*");
        if (!ok)
        {
            return false;
        }
        return name.StartsWith(before, StringComparison.Ordinal) && name.EndsWith(after, StringComparison.Ordinal);
    }

    // True if `extension` is one of the supported `extensions`.
    internal static bool ExtensionIsOk(Extensions extensions, string extension)
    {
        return ((extensions & Extensions.JavaScript) != 0 && (extension == TsPathApi.ExtensionJs || extension == TsPathApi.ExtensionJsx || extension == TsPathApi.ExtensionMjs || extension == TsPathApi.ExtensionCjs)) ||
            ((extensions & Extensions.TypeScript) != 0 && (extension == TsPathApi.ExtensionTs || extension == TsPathApi.ExtensionTsx || extension == TsPathApi.ExtensionMts || extension == TsPathApi.ExtensionCts)) ||
            ((extensions & Extensions.Declaration) != 0 && (extension == TsPathApi.ExtensionDts || extension == TsPathApi.ExtensionDmts || extension == TsPathApi.ExtensionDcts)) ||
            ((extensions & Extensions.Json) != 0 && extension == TsPathApi.ExtensionJson);
    }

    public static ResolvedModule ResolveConfig(string moduleName, string containingFile, ResolutionHost host)
    {
        var resolver = new Resolver(host, new CompilerOptions { ModuleResolution = ModuleResolutionKind.NodeNext }, "", "");
        return resolver.ResolveConfig(moduleName, containingFile);
    }

    public static string[] GetAutomaticTypeDirectiveNames(CompilerOptions options, ResolutionHost host)
    {
        if (!options.UsesWildcardTypes())
        {
            if (options.Types != null)
            {
                return options.Types;
            }
            return Array.Empty<string>();
        }

        // Walk the primary type lookup locations
        var wildcardMatches = new List<string>();
        (string[] typeRoots, _) = options.GetEffectiveTypeRoots(host.GetCurrentDirectory());
        foreach (string root in typeRoots)
        {
            if (host.FS().DirectoryExists(root))
            {
                foreach (string typeDirectivePath in host.FS().GetAccessibleEntries(root).Directories ?? Array.Empty<string>())
                {
                    string normalized = TsPathApi.NormalizePath(typeDirectivePath);
                    string packageJsonPath = TsPathApi.CombinePaths(root, normalized, "package.json");
                    bool isNotNeededPackage = false;
                    if (host.FS().FileExists(packageJsonPath))
                    {
                        host.FS().TryReadFile(packageJsonPath, out string contents);
                        (Fields packageJsonContent, _) = PackageJsonParser.Parse(System.Text.Encoding.UTF8.GetBytes(contents));
                        // `types-publisher` sometimes creates packages with `"typings": null` for
                        // packages that don't provide their own types.
                        isNotNeededPackage = packageJsonContent.PathFields.Typings.Null;
                    }
                    if (!isNotNeededPackage)
                    {
                        string baseFileName = TsPathApi.GetBaseFileName(normalized);
                        if (!baseFileName.StartsWith(".", StringComparison.Ordinal))
                        {
                            wildcardMatches.Add(baseFileName);
                        }
                    }
                }
            }
        }

        // Order matters in program construction, so substitute in the wildcard in the position
        // it was specified in the types array.
        var result = new List<string>();
        foreach (string t in options.Types ?? Array.Empty<string>())
        {
            if (t == "*")
            {
                result.AddRange(wildcardMatches);
            }
            else
            {
                result.Add(t);
            }
        }
        return CoreUtil.Deduplicate(result.ToArray()) ?? Array.Empty<string>();
    }
}

public sealed class ParsedPatterns
{
    internal Set<string>? MatchableStringSet;
    internal Pattern[]? Patterns;
}
