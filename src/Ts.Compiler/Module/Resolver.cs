// Ported from tsgo internal/module/resolver.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Generic;
using System.Text;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Ts.PackageJson;
using Ts.Vfs;

namespace Ts.Module;

// PORT-NOTE: aliases inside the file-scoped namespace outrank the enclosing `Ts`
// namespace's sibling sub-namespaces (bare `Core`/`Diagnostics`/etc.).
using CoreUtil = Ts.Core.CoreUtil;
using Core = Ts.Core.Core;
using Diagnostics = Ts.Diagnostics.Diagnostics;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath;
using StringUtilApi = Ts.StringUtil.StringUtil;
using VfsMatchApi = Ts.Vfs.VfsMatch.VfsMatch;
using PackageJson = Ts.PackageJson.PackageJson;

using static Ts.Module.Module;

// Go: type resolved struct (resolver.go:20-26). nil pointer == continueSearching.
internal sealed class Resolved
{
    public string Path = "";
    public string Extension = "";
    public PackageId PackageId;
    public string OriginalPath = "";
    public bool ResolvedUsingTsExtension;
}

internal static class ResolvedExtensions
{
    // Go: func (r *resolved) shouldContinueSearching() bool { return r == nil }.
    public static bool ShouldContinueSearching(this Resolved? r)
    {
        return r is null;
    }

    // Go: func (r *resolved) isResolved() bool { return r != nil && r.path != "" }.
    public static bool IsResolved(this Resolved? r)
    {
        return r != null && r.Path != "";
    }
}

// Go: type DiagAndArgs (resolver.go:50-53).
public readonly struct DiagAndArgs
{
    public readonly Message Message;
    public readonly object?[] Args;

    public DiagAndArgs(Message message, object?[] args)
    {
        Message = message;
        Args = args;
    }
}

// Go: type tracer struct. nil tracer methods are guarded at each call site, matching
// the Go source (which relies on nil-receiver `write`).
internal sealed class Tracer
{
    private readonly List<DiagAndArgs> traces = new();

    public void Write(Message diag, params object?[] args)
    {
        traces.Add(new DiagAndArgs(diag, args));
    }

    public List<DiagAndArgs> GetTraces()
    {
        return traces;
    }

    public void TraceResolutionUsingProjectReference(ResolvedProjectReference? redirectedReference)
    {
        if (redirectedReference != null && redirectedReference.CompilerOptions() != null)
        {
            Write(Diagnostics.Using_compiler_options_of_project_reference_redirect_0, redirectedReference.ConfigName());
        }
    }

    public void TraceTypeReferenceDirectiveResult(string typeReferenceDirectiveName, ResolvedTypeReferenceDirective result)
    {
        if (!result.IsResolved())
        {
            Write(Diagnostics.Type_reference_directive_0_was_not_resolved, typeReferenceDirectiveName);
        }
        else if (!string.IsNullOrEmpty(result.PackageId.Name))
        {
            Write(
                Diagnostics.Type_reference_directive_0_was_successfully_resolved_to_1_with_Package_ID_2_primary_Colon_3,
                typeReferenceDirectiveName,
                result.ResolvedFileName,
                result.PackageId.ToString(),
                result.Primary);
        }
        else
        {
            Write(
                Diagnostics.Type_reference_directive_0_was_successfully_resolved_to_1_primary_Colon_2,
                typeReferenceDirectiveName,
                result.ResolvedFileName,
                result.Primary);
        }
    }
}

// Go: func(extensions extensions, candidate string) *resolved.
internal delegate Resolved? ResolutionKindSpecificLoader(Extensions extensions, string candidate);

internal sealed partial class ResolutionState
{
    public Resolver resolver = null!;
    public Tracer? tracer;

    // request fields
    public string name = "";
    public string containingDirectory = "";
    public bool isConfigLookup;
    public NodeResolutionFeatures features;
    public bool esmMode;
    public string[]? conditions;
    public Extensions extensions;
    public CompilerOptions compilerOptions = null!;
    public bool resolvePackageDirectoryOnly;

    // state fields
    // candidateEndingIsFromConfig is set when the candidate file extension originated from
    // configuration (package.json fields, tsconfig.json paths entries, or wildcard
    // substitutions) rather than from the module specifier written in source code. When true,
    // resolvedUsingTsExtension is suppressed so the checker does not attempt to extract a TS
    // extension from the original specifier.
    public bool candidateEndingIsFromConfig;
    public bool resolvedPackageDirectory;
    public List<Diagnostic>? diagnostics;

    // Similar to whats on resolver but only done if compilerOptions are for project reference
    // redirect. Cached representation for `core.CompilerOptions.paths`.
    private readonly object parsedPatternsGate = new();
    private bool parsedPatternsComputed;
    private ParsedPatterns? parsedPatternsForPaths;

    public ResolutionState()
    {
    }

    // Go: newResolutionState (resolver.go:99-143).
    public static ResolutionState NewResolutionState(
        string name,
        string containingDirectory,
        bool isTypeReferenceDirective,
        ResolutionMode resolutionMode,
        CompilerOptions compilerOptions,
        ResolvedProjectReference? redirectedReference,
        Resolver resolver,
        Tracer? traceBuilder)
    {
        var state = new ResolutionState
        {
            name = name,
            containingDirectory = containingDirectory,
            compilerOptions = GetCompilerOptionsWithRedirect(compilerOptions, redirectedReference),
            resolver = resolver,
            tracer = traceBuilder,
        };

        if (isTypeReferenceDirective)
        {
            state.extensions = Extensions.Declaration;
        }
        else if (compilerOptions.NoDtsResolution == Tristate.True)
        {
            state.extensions = Extensions.ImplementationFiles;
        }
        else
        {
            state.extensions = Extensions.TypeScript | Extensions.JavaScript | Extensions.Declaration;
        }

        if (!isTypeReferenceDirective && compilerOptions.GetResolveJsonModule())
        {
            state.extensions |= Extensions.Json;
        }

        switch (compilerOptions.GetModuleResolutionKind())
        {
            case ModuleResolutionKind.Node16:
                state.features = NodeResolutionFeatures.Node16Default;
                state.esmMode = resolutionMode == ModuleKind.ESNext;
                state.conditions = GetConditions(compilerOptions, resolutionMode);
                break;
            case ModuleResolutionKind.NodeNext:
                state.features = NodeResolutionFeatures.NodeNextDefault;
                state.esmMode = resolutionMode == ModuleKind.ESNext;
                state.conditions = GetConditions(compilerOptions, resolutionMode);
                break;
            case ModuleResolutionKind.Bundler:
                state.features = GetNodeResolutionFeatures(compilerOptions);
                state.conditions = GetConditions(compilerOptions, resolutionMode);
                break;
        }
        return state;
    }

    public InfoCacheEntry? GetPackageScopeForPath(string directory)
    {
        InfoCacheEntry? result = TsPathApi.ForEachAncestorDirectoryStoppingAtGlobalCache<InfoCacheEntry?>(
            resolver.typingsLocation,
            directory,
            dir =>
            {
                InfoCacheEntry? res = GetPackageJsonInfo(dir);
                if (res != null)
                {
                    return (res, true);
                }
                return (null, false);
            });
        return result;
    }

    public ResolvedTypeReferenceDirective ResolveTypeReferenceDirective(string[] typeRoots, bool fromConfig, bool fromInferredTypesContainingFile)
    {
        // Primary lookup
        if (typeRoots.Length > 0)
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Resolving_with_primary_search_path_0, string.Join(", ", typeRoots));
            }
            foreach (string typeRoot in typeRoots)
            {
                string candidate = GetCandidateFromTypeRoot(typeRoot);
                bool directoryExists = resolver.host.FS().DirectoryExists(typeRoot);
                if (!directoryExists)
                {
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.Directory_0_does_not_exist_skipping_all_lookups_in_it, typeRoot);
                    }
                    continue;
                }
                if (fromConfig)
                {
                    // Custom typeRoots resolve as file or directory just like we do modules
                    Resolved? resolvedFromFile = LoadModuleFromFile(Extensions.Declaration, candidate);
                    if (!resolvedFromFile.ShouldContinueSearching())
                    {
                        string packageDirectory = ParseNodeModuleFromPath(resolvedFromFile!.Path, false);
                        if (packageDirectory != "")
                        {
                            resolvedFromFile.PackageId = GetPackageId(resolvedFromFile.Path, GetPackageJsonInfo(packageDirectory));
                        }
                        return CreateResolvedTypeReferenceDirective(resolvedFromFile, true);
                    }
                }
                Resolved? resolvedFromDirectory = LoadNodeModuleFromDirectory(Extensions.Declaration, candidate, true);
                if (!resolvedFromDirectory.ShouldContinueSearching())
                {
                    return CreateResolvedTypeReferenceDirective(resolvedFromDirectory, true);
                }
            }
        }
        else if (tracer != null)
        {
            tracer.Write(Diagnostics.Root_directory_cannot_be_determined_skipping_primary_search_paths);
        }

        // Secondary lookup
        Resolved? resolved = null;
        if (!fromConfig || !fromInferredTypesContainingFile)
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Looking_up_in_node_modules_folder_initial_location_0, containingDirectory);
            }
            if (!TsPathApi.IsExternalModuleNameRelative(name))
            {
                resolved = LoadModuleFromNearestNodeModulesDirectory(false);
            }
            else
            {
                string candidate = NormalizePathForCJSResolution(containingDirectory, name);
                resolved = NodeLoadModuleByRelativeName(Extensions.Declaration, candidate, true);
            }
        }
        else if (tracer != null)
        {
            tracer.Write(Diagnostics.Resolving_type_reference_directive_for_program_that_specifies_custom_typeRoots_skipping_lookup_in_node_modules_folder);
        }
        return CreateResolvedTypeReferenceDirective(resolved, false);
    }

    public string GetCandidateFromTypeRoot(string typeRoot)
    {
        string nameForLookup = name;
        if (typeRoot.EndsWith("/node_modules/@types", StringComparison.Ordinal) || typeRoot.EndsWith("/node_modules/@types/", StringComparison.Ordinal))
        {
            nameForLookup = MangleScopedPackageNameWithTrace(name);
        }
        return TsPathApi.CombinePaths(typeRoot, nameForLookup);
    }

    private string MangleScopedPackageNameWithTrace(string n)
    {
        string mangled = MangleScopedPackageName(n);
        if (tracer != null && mangled != n)
        {
            tracer.Write(Diagnostics.Scoped_package_detected_looking_in_0, mangled);
        }
        return mangled;
    }

    // resolveFromTypeRoot tries to resolve a module name from the configured typeRoots.
    // This is used as a fallback after node_modules resolution fails, for declaration file lookups.
    public Resolved? ResolveFromTypeRoot()
    {
        if (compilerOptions.TypeRoots == null)
        {
            return null;
        }
        foreach (string typeRoot in compilerOptions.TypeRoots)
        {
            string candidate = GetCandidateFromTypeRoot(typeRoot);
            bool directoryExists = resolver.host.FS().DirectoryExists(typeRoot);
            if (!directoryExists)
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Directory_0_does_not_exist_skipping_all_lookups_in_it, typeRoot);
                }
                continue;
            }
            Resolved? resolvedFromFile = LoadModuleFromFile(Extensions.Declaration, candidate);
            if (!resolvedFromFile.ShouldContinueSearching())
            {
                string packageDirectory = ParseNodeModuleFromPath(resolvedFromFile!.Path, false);
                if (packageDirectory != "")
                {
                    resolvedFromFile.PackageId = GetPackageId(resolvedFromFile.Path, GetPackageJsonInfo(packageDirectory));
                }
                return resolvedFromFile;
            }
            Resolved? resolved = LoadNodeModuleFromDirectory(Extensions.Declaration, candidate, true);
            if (!resolved.ShouldContinueSearching())
            {
                return resolved;
            }
        }
        return null;
    }

    public ResolvedModule ResolveNodeLike()
    {
        if (tracer != null)
        {
            string conditionsStr = string.Join(", ", CoreUtil.Map(conditions, c => "'" + c + "'") ?? Array.Empty<string>());
            if (esmMode)
            {
                tracer.Write(Diagnostics.Resolving_in_0_mode_with_conditions_1, "ESM", conditionsStr);
            }
            else
            {
                tracer.Write(Diagnostics.Resolving_in_0_mode_with_conditions_1, "CJS", conditionsStr);
            }
        }
        ResolvedModule result = ResolveNodeLikeWorker();
        if (resolvedPackageDirectory &&
            !isConfigLookup &&
            (features & NodeResolutionFeatures.Exports) != 0 &&
            (extensions & (Extensions.TypeScript | Extensions.Declaration)) != 0 &&
            !TsPathApi.IsExternalModuleNameRelative(name) &&
            result.IsResolved() &&
            result.IsExternalLibraryImport &&
            !ExtensionIsOk(Extensions.TypeScript | Extensions.Declaration, result.Extension) &&
            SliceContains(conditions, "import"))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Resolution_of_non_relative_name_failed_trying_with_modern_Node_resolution_features_disabled_to_see_if_npm_library_needs_configuration_update);
            }
            features &= ~NodeResolutionFeatures.Exports;
            extensions &= Extensions.TypeScript | Extensions.Declaration;
            int diagnosticsCount = diagnostics?.Count ?? 0;
            ResolvedModule diagnosticResult = ResolveNodeLikeWorker();
            if (diagnosticResult.IsResolved() && diagnosticResult.IsExternalLibraryImport)
            {
                result.AlternateResult = diagnosticResult.ResolvedFileName;
            }
            if (diagnostics != null && diagnostics.Count > diagnosticsCount)
            {
                diagnostics.RemoveRange(diagnosticsCount, diagnostics.Count - diagnosticsCount);
            }
        }
        return result;
    }

    public ResolvedModule ResolveNodeLikeWorker()
    {
        Resolved? resolved = TryLoadModuleUsingOptionalResolutionSettings();
        if (!resolved.ShouldContinueSearching())
        {
            return CreateResolvedModuleHandlingSymlink(resolved);
        }

        if (!TsPathApi.IsExternalModuleNameRelative(name))
        {
            if ((features & NodeResolutionFeatures.Imports) != 0 && name.StartsWith("#", StringComparison.Ordinal))
            {
                Resolved? r = LoadModuleFromImports();
                if (!r.ShouldContinueSearching())
                {
                    return CreateResolvedModuleHandlingSymlink(r);
                }
            }
            if ((features & NodeResolutionFeatures.SelfName) != 0)
            {
                Resolved? r = LoadModuleFromSelfNameReference();
                if (!r.ShouldContinueSearching())
                {
                    return CreateResolvedModuleHandlingSymlink(r);
                }
            }
            if (name.Contains(':'))
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Skipping_module_0_that_looks_like_an_absolute_URI_target_file_types_Colon_1, name, extensions.ToDisplayString());
                }
                return CreateResolvedModule(null, false);
            }
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Loading_module_0_from_node_modules_folder_target_file_types_Colon_1, name, extensions.ToDisplayString());
            }
            Resolved? nm = LoadModuleFromNearestNodeModulesDirectory(false);
            if (!nm.ShouldContinueSearching())
            {
                return CreateResolvedModuleHandlingSymlink(nm);
            }
            if ((extensions & Extensions.Declaration) != 0)
            {
                Resolved? tr = ResolveFromTypeRoot();
                if (!tr.ShouldContinueSearching())
                {
                    return CreateResolvedModuleHandlingSymlink(tr);
                }
            }
        }
        else
        {
            string candidate = NormalizePathForCJSResolution(containingDirectory, name);
            Resolved? resolvedRel = NodeLoadModuleByRelativeName(extensions, candidate, true);
            return CreateResolvedModule(
                resolvedRel,
                resolvedRel != null && resolvedRel.Path.Contains("/node_modules/", StringComparison.Ordinal));
        }
        return CreateResolvedModule(null, false);
    }

    public Resolved? LoadModuleFromSelfNameReference()
    {
        string directoryPath = TsPathApi.GetNormalizedAbsolutePath(containingDirectory, resolver.host.GetCurrentDirectory());
        InfoCacheEntry? scope = GetPackageScopeForPath(directoryPath);
        if (!scope.Exists() || scope!.Contents!.Exports.IsFalsy())
        {
            // !!! falsy check seems wrong?
            return ContinueSearching();
        }
        if (!scope.Contents!.Name.GetValue(out string? pkgName))
        {
            return ContinueSearching();
        }
        string nm = pkgName!;
        List<string> parts = TsPathApi.GetPathComponents(name, "");
        List<string> nameParts = TsPathApi.GetPathComponents(nm, "");
        if (parts.Count < nameParts.Count)
        {
            return ContinueSearching();
        }
        for (int i = 0; i < nameParts.Count; i++)
        {
            if (nameParts[i] != parts[i])
            {
                return ContinueSearching();
            }
        }
        // trailingParts = parts[len(nameParts):]
        string subpath;
        if (parts.Count > nameParts.Count)
        {
            string[] trailingParts = parts.GetRange(nameParts.Count, parts.Count - nameParts.Count).ToArray();
            subpath = TsPathApi.CombinePaths(".", trailingParts);
        }
        else
        {
            subpath = ".";
        }
        // Maybe TODO: splitting extensions into two priorities should be unnecessary, except
        // https://github.com/microsoft/TypeScript/issues/50762 makes the behavior different.
        // See the selfNameModuleAugmentation.ts test for why this matters. However, there's an
        // exception when the user has `allowJs` and `declaration`.
        if (compilerOptions.GetAllowJS() && !containingDirectory.Contains("/node_modules/", StringComparison.Ordinal))
        {
            return LoadModuleFromExports(scope, extensions, subpath);
        }
        Extensions priorityExtensions = extensions & (Extensions.TypeScript | Extensions.Declaration);
        Extensions secondaryExtensions = extensions & ~(Extensions.TypeScript | Extensions.Declaration);
        Resolved? resolved = LoadModuleFromExports(scope, priorityExtensions, subpath);
        if (!resolved.ShouldContinueSearching())
        {
            return resolved;
        }
        return LoadModuleFromExports(scope, secondaryExtensions, subpath);
    }

    public Resolved? LoadModuleFromImports()
    {
        if (name == "#" || (name.StartsWith("#/", StringComparison.Ordinal) && (features & NodeResolutionFeatures.ImportsPatternRoot) == 0))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Invalid_import_specifier_0_has_no_possible_resolutions, name);
            }
            return ContinueSearching();
        }
        string directoryPath = TsPathApi.GetNormalizedAbsolutePath(containingDirectory, resolver.host.GetCurrentDirectory());
        InfoCacheEntry? scope = GetPackageScopeForPath(directoryPath);
        if (!scope.Exists())
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Directory_0_has_no_containing_package_json_scope_Imports_will_not_resolve, directoryPath);
            }
            return ContinueSearching();
        }
        if (scope!.Contents!.Imports.Type != JSONValueType.JSONValueTypeObject)
        {
            // !!! Old compiler only checks for undefined, but then assumes `imports` is an object if present.
            if (tracer != null)
            {
                tracer.Write(Diagnostics.X_package_json_scope_0_has_no_imports_defined, scope.PackageDirectory);
            }
            return ContinueSearching();
        }

        Resolved? result = LoadModuleFromExportsOrImports(extensions, name, scope.Contents.Imports.AsObject(), scope, true);
        if (!result.ShouldContinueSearching())
        {
            return result;
        }

        if (tracer != null)
        {
            tracer.Write(Diagnostics.Import_specifier_0_does_not_exist_in_package_json_scope_at_path_1, name, scope.PackageDirectory);
        }
        return ContinueSearching();
    }

    public Resolved? LoadModuleFromExports(InfoCacheEntry? packageInfo, Extensions ext, string subpath)
    {
        // !!! This is ported exactly, but the falsy check seems wrong
        if (!packageInfo.Exists() || packageInfo!.Contents!.Exports.IsFalsy())
        {
            return ContinueSearching();
        }

        if (subpath == ".")
        {
            ExportsOrImports mainExport = default;
            switch (packageInfo.Contents!.Exports.Type)
            {
                case JSONValueType.JSONValueTypeString:
                case JSONValueType.JSONValueTypeArray:
                    mainExport = packageInfo.Contents.Exports;
                    break;
                case JSONValueType.JSONValueTypeObject:
                    if (packageInfo.Contents.Exports.IsConditions())
                    {
                        mainExport = packageInfo.Contents.Exports;
                    }
                    else if (packageInfo.Contents.Exports.AsObject().TryGet(".", out ExportsOrImports dot))
                    {
                        mainExport = dot;
                    }
                    break;
            }
            if (mainExport.Type != JSONValueType.JSONValueTypeNotPresent)
            {
                return LoadModuleFromTargetExportOrImport(ext, subpath, packageInfo, false, mainExport, "", false, ".");
            }
        }
        else if (packageInfo.Contents.Exports.Type == JSONValueType.JSONValueTypeObject && packageInfo.Contents.Exports.IsSubpaths())
        {
            Resolved? result = LoadModuleFromExportsOrImports(ext, subpath, packageInfo.Contents.Exports.AsObject(), packageInfo, false);
            if (!result.ShouldContinueSearching())
            {
                return result;
            }
        }

        if (tracer != null)
        {
            tracer.Write(Diagnostics.Export_specifier_0_does_not_exist_in_package_json_scope_at_path_1, subpath, packageInfo.PackageDirectory);
        }
        return ContinueSearching();
    }

    public Resolved? LoadModuleFromExportsOrImports(
        Extensions extensions,
        string moduleName,
        OrderedMap<string, ExportsOrImports> lookupTable,
        InfoCacheEntry? scope,
        bool isImports)
    {
        if (!moduleName.EndsWith("/", StringComparison.Ordinal) && !moduleName.Contains('*'))
        {
            if (lookupTable.TryGet(moduleName, out ExportsOrImports target))
            {
                return LoadModuleFromTargetExportOrImport(extensions, moduleName, scope, isImports, target, "", false, moduleName);
            }
        }

        var expandingKeys = new List<string>(lookupTable.Size());
        // map-iter: 已排序 (OrderedMap preserves insertion order; keys are re-sorted below).
        foreach (string key in lookupTable.Keys())
        {
            if (CountChar(key, '*') == 1 || key.EndsWith("/", StringComparison.Ordinal))
            {
                expandingKeys.Add(key);
            }
        }
        expandingKeys.Sort(ComparePatternKeys);

        foreach (string potentialTarget in expandingKeys)
        {
            if ((features & NodeResolutionFeatures.ExportsPatternTrailers) != 0 && MatchesPatternWithTrailer(potentialTarget, moduleName))
            {
                lookupTable.TryGet(potentialTarget, out ExportsOrImports target);
                int starPos = potentialTarget.IndexOf('*');
                // subpath = moduleName[starPos : len(moduleName)-(len(potentialTarget)-1-starPos)]
                int start = starPos;
                int end = moduleName.Length - (potentialTarget.Length - 1 - starPos);
                string subpath = moduleName.Substring(start, end - start);
                return LoadModuleFromTargetExportOrImport(extensions, moduleName, scope, isImports, target, subpath, true, potentialTarget);
            }
            else if (potentialTarget.EndsWith("*", StringComparison.Ordinal) && moduleName.StartsWith(potentialTarget.Substring(0, potentialTarget.Length - 1), StringComparison.Ordinal))
            {
                lookupTable.TryGet(potentialTarget, out ExportsOrImports target);
                string subpath = moduleName.Substring(potentialTarget.Length - 1);
                return LoadModuleFromTargetExportOrImport(extensions, moduleName, scope, isImports, target, subpath, true, potentialTarget);
            }
            else if (moduleName.StartsWith(potentialTarget, StringComparison.Ordinal))
            {
                lookupTable.TryGet(potentialTarget, out ExportsOrImports target);
                string subpath = moduleName.Substring(potentialTarget.Length);
                return LoadModuleFromTargetExportOrImport(extensions, moduleName, scope, isImports, target, subpath, false, potentialTarget);
            }
        }

        return ContinueSearching();
    }

    public Resolved? LoadModuleFromTargetExportOrImport(Extensions extensions, string moduleName, InfoCacheEntry? scope, bool isImports, ExportsOrImports target, string subpath, bool isPattern, string key)
    {
        switch (target.Type)
        {
            case JSONValueType.JSONValueTypeString:
            {
                string targetString = target.Value as string ?? "";
                if (!isPattern && subpath.Length > 0 && !targetString.EndsWith("/", StringComparison.Ordinal))
                {
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.X_package_json_scope_0_has_invalid_type_for_target_of_specifier_1, scope!.PackageDirectory, moduleName);
                    }
                    return ContinueSearching();
                }
                if (!targetString.StartsWith("./", StringComparison.Ordinal))
                {
                    if (isImports && !targetString.StartsWith("../", StringComparison.Ordinal) && !targetString.StartsWith("/", StringComparison.Ordinal) && !TsPathApi.IsRootedDiskPath(targetString))
                    {
                        string combinedLookup = targetString + subpath;
                        if (isPattern)
                        {
                            combinedLookup = targetString.Replace("*", subpath, StringComparison.Ordinal);
                        }
                        string scopeContainingDirectory = TsPathApi.EnsureTrailingDirectorySeparator(scope!.PackageDirectory);
                        if (tracer != null)
                        {
                            tracer.Write(Diagnostics.Using_0_subpath_1_with_target_2, "imports", key, combinedLookup);
                            tracer.Write(Diagnostics.Resolving_module_0_from_1, combinedLookup, scopeContainingDirectory);
                        }
                        string savedName = name;
                        string savedDir = containingDirectory;
                        name = combinedLookup;
                        containingDirectory = scopeContainingDirectory;
                        try
                        {
                            ResolvedModule result = ResolveNodeLike();
                            if (result.IsResolved())
                            {
                                return new Resolved
                                {
                                    Path = result.ResolvedFileName,
                                    Extension = result.Extension,
                                    PackageId = result.PackageId,
                                    OriginalPath = result.OriginalPath,
                                    ResolvedUsingTsExtension = result.ResolvedUsingTsExtension,
                                };
                            }
                            return ContinueSearching();
                        }
                        finally
                        {
                            name = savedName;
                            containingDirectory = savedDir;
                        }
                    }
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.X_package_json_scope_0_has_invalid_type_for_target_of_specifier_1, scope!.PackageDirectory, moduleName);
                    }
                    return ContinueSearching();
                }
                List<string> comps = TsPathApi.GetPathComponents(targetString, "");
                List<string> parts = TsPathApi.PathIsRelative(targetString) ? comps.GetRange(1, comps.Count - 1) : comps;
                // partsAfterFirst = parts[1:]
                if (ContainsAny(parts, 1))
                {
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.X_package_json_scope_0_has_invalid_type_for_target_of_specifier_1, scope!.PackageDirectory, moduleName);
                    }
                    return ContinueSearching();
                }
                string resolvedTarget = TsPathApi.CombinePaths(scope!.PackageDirectory, targetString);
                List<string> subpathParts = TsPathApi.GetPathComponents(subpath, "");
                if (ContainsAny(subpathParts, 0))
                {
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.X_package_json_scope_0_has_invalid_type_for_target_of_specifier_1, scope.PackageDirectory, moduleName);
                    }
                    return ContinueSearching();
                }

                if (tracer != null)
                {
                    string messageTarget = isPattern
                        ? targetString.Replace("*", subpath, StringComparison.Ordinal)
                        : targetString + subpath;
                    tracer.Write(Diagnostics.Using_0_subpath_1_with_target_2, CoreUtil.IfElse(isImports, "imports", "exports"), key, messageTarget);
                }
                string finalPath = isPattern
                    ? TsPathApi.GetNormalizedAbsolutePath(resolvedTarget.Replace("*", subpath, StringComparison.Ordinal), resolver.host.GetCurrentDirectory())
                    : TsPathApi.GetNormalizedAbsolutePath(resolvedTarget + subpath, resolver.host.GetCurrentDirectory());
                Resolved? inputLink = TryLoadInputFileForPath(finalPath, subpath, TsPathApi.CombinePaths(scope.PackageDirectory, "package.json"), isImports);
                if (!inputLink.ShouldContinueSearching())
                {
                    inputLink!.PackageId = GetPackageId(inputLink.Path, scope);
                    return inputLink;
                }
                Resolved? loadResult = LoadFileNameFromPackageJSONField(extensions, finalPath, targetString);
                if (!loadResult.ShouldContinueSearching())
                {
                    loadResult!.PackageId = GetPackageId(loadResult.Path, scope);
                    return loadResult;
                }
                return ContinueSearching();
            }

            case JSONValueType.JSONValueTypeObject:
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Entering_conditional_exports);
                }
                // map-iter: 已排序 (conditions evaluated in package.json order).
                foreach (string condition in target.AsObject().Keys())
                {
                    if (ConditionMatches(condition))
                    {
                        if (tracer != null)
                        {
                            tracer.Write(Diagnostics.Matched_0_condition_1, CoreUtil.IfElse(isImports, "imports", "exports"), condition);
                        }
                        target.AsObject().TryGet(condition, out ExportsOrImports subTarget);
                        Resolved? result = LoadModuleFromTargetExportOrImport(extensions, moduleName, scope, isImports, subTarget, subpath, isPattern, key);
                        if (!result.ShouldContinueSearching())
                        {
                            if (result.IsResolved() && tracer != null)
                            {
                                tracer.Write(Diagnostics.Resolved_under_condition_0, condition);
                            }
                            if (tracer != null)
                            {
                                tracer.Write(Diagnostics.Exiting_conditional_exports);
                            }
                            return result;
                        }
                        else if (tracer != null)
                        {
                            tracer.Write(Diagnostics.Failed_to_resolve_under_condition_0, condition);
                        }
                    }
                    else
                    {
                        if (tracer != null)
                        {
                            tracer.Write(Diagnostics.Saw_non_matching_condition_0, condition);
                        }
                    }
                }
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Exiting_conditional_exports);
                }
                return ContinueSearching();
            }

            case JSONValueType.JSONValueTypeArray:
            {
                if (target.AsArray().Count == 0)
                {
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.X_package_json_scope_0_has_invalid_type_for_target_of_specifier_1, scope!.PackageDirectory, moduleName);
                    }
                    return ContinueSearching();
                }
                foreach (ExportsOrImports elem in target.AsArray())
                {
                    Resolved? result = LoadModuleFromTargetExportOrImport(extensions, moduleName, scope, isImports, elem, subpath, isPattern, key);
                    if (!result.ShouldContinueSearching())
                    {
                        return result;
                    }
                }
                break;
            }

            case JSONValueType.JSONValueTypeNull:
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.X_package_json_scope_0_explicitly_maps_specifier_1_to_null, scope!.PackageDirectory, moduleName);
                }
                return Unresolved();
            }
        }

        if (tracer != null)
        {
            tracer.Write(Diagnostics.X_package_json_scope_0_has_invalid_type_for_target_of_specifier_1, scope!.PackageDirectory, moduleName);
        }
        return ContinueSearching();
    }

    public Resolved? TryLoadInputFileForPath(string finalPath, string entry, string packagePath, bool isImports)
    {
        // Replace any references to outputs for files in the program with the input files to
        // support package self-names used with outDir
        if (!isConfigLookup &&
            (compilerOptions.DeclarationDir != "" || compilerOptions.OutDir != "") &&
            !finalPath.Contains("/node_modules/", StringComparison.Ordinal) &&
            (compilerOptions.ConfigFilePath == "" || TsPathApi.ContainsPath(
                TsPathApi.GetDirectoryPath(packagePath),
                compilerOptions.ConfigFilePath,
                new Ts.TsPath.ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = resolver.host.FS().UseCaseSensitiveFileNames(),
                    CurrentDirectory = resolver.host.GetCurrentDirectory(),
                })))
        {
            // Note: this differs from Strada's tryLoadInputFileForPath in that it does not attempt
            // to perform "guesses", instead requiring a clear root indicator.

            string rootDir;
            if (compilerOptions.RootDir != "")
            {
                // A `rootDir` compiler option strongly indicates the root location
                rootDir = compilerOptions.RootDir;
            }
            else if (compilerOptions.ConfigFilePath != "")
            {
                // When no explicit rootDir is set, treat the config file's directory as the project root.
                rootDir = TsPathApi.GetDirectoryPath(compilerOptions.ConfigFilePath);
            }
            else
            {
                Diagnostic diagnostic = Diagnostic.NewDiagnostic(
                    null,
                    default,
                    CoreUtil.IfElse(
                        isImports,
                        Diagnostics.The_project_root_is_ambiguous_but_is_required_to_resolve_import_map_entry_0_in_file_1_Supply_the_rootDir_compiler_option_to_disambiguate,
                        Diagnostics.The_project_root_is_ambiguous_but_is_required_to_resolve_export_map_entry_0_in_file_1_Supply_the_rootDir_compiler_option_to_disambiguate),
                    CoreUtil.IfElse(entry == "", ".", entry),
                    packagePath);
                (diagnostics ??= new()).Add(diagnostic);
                return Unresolved();
            }

            List<string> candidateDirectories = GetOutputDirectoriesForBaseDirectory(rootDir);
            foreach (string candidateDir in candidateDirectories)
            {
                if (TsPathApi.ContainsPath(candidateDir, finalPath, new Ts.TsPath.ComparePathsOptions
                {
                    UseCaseSensitiveFileNames = resolver.host.FS().UseCaseSensitiveFileNames(),
                    CurrentDirectory = resolver.host.GetCurrentDirectory(),
                }))
                {
                    // Map the written path back into the source dir and source extension.
                    string pathFragment = "";
                    if (finalPath.Length > candidateDir.Length)
                    {
                        pathFragment = finalPath.Substring(candidateDir.Length + 1); // +1 to also remove directory separator
                    }
                    string possibleInputBase = TsPathApi.CombinePaths(rootDir, pathFragment);
                    string[] jsAndDtsExtensions = { TsPathApi.ExtensionMjs, TsPathApi.ExtensionCjs, TsPathApi.ExtensionJs, TsPathApi.ExtensionJson, TsPathApi.ExtensionDmts, TsPathApi.ExtensionDcts, TsPathApi.ExtensionDts };
                    foreach (string ext in jsAndDtsExtensions)
                    {
                        if (TsPathApi.FileExtensionIs(possibleInputBase, ext))
                        {
                            string[] inputExts = TsPathApi.GetPossibleOriginalInputExtensionForExtension(possibleInputBase);
                            foreach (string possibleExt in inputExts)
                            {
                                if (!ExtensionIsOk(extensions, possibleExt))
                                {
                                    continue;
                                }
                                string possibleInputWithInputExtension = TsPathApi.ChangeExtension(possibleInputBase, possibleExt);
                                if (resolver.host.FS().FileExists(possibleInputWithInputExtension))
                                {
                                    Resolved? resolved = LoadFileNameFromPackageJSONField(this.extensions, possibleInputWithInputExtension, "");
                                    if (!resolved.ShouldContinueSearching())
                                    {
                                        return resolved;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return ContinueSearching();
    }

    public List<string> GetOutputDirectoriesForBaseDirectory(string commonSourceDirGuess)
    {
        // Config file output paths are processed to be relative to the host's current directory,
        // while otherwise the paths are resolved relative to the common source dir.
        string currentDir = CoreUtil.IfElse(compilerOptions.ConfigFilePath != "", resolver.host.GetCurrentDirectory(), commonSourceDirGuess);
        var candidateDirectories = new List<string>();
        if (compilerOptions.DeclarationDir != "")
        {
            candidateDirectories.Add(TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(currentDir, compilerOptions.DeclarationDir), resolver.host.GetCurrentDirectory()));
        }
        if (compilerOptions.OutDir != "" && compilerOptions.OutDir != compilerOptions.DeclarationDir)
        {
            candidateDirectories.Add(TsPathApi.GetNormalizedAbsolutePath(TsPathApi.CombinePaths(currentDir, compilerOptions.OutDir), resolver.host.GetCurrentDirectory()));
        }
        return candidateDirectories;
    }

    public Resolved? LoadModuleFromNearestNodeModulesDirectory(bool typesScopeOnly)
    {
        ResolutionMode mode = Core.ResolutionModeCommonJS;
        if (esmMode || ConditionMatches("import"))
        {
            mode = Core.ResolutionModeESM;
        }
        Extensions priorityExtensions = extensions & (Extensions.TypeScript | Extensions.Declaration);
        Extensions secondaryExtensions = extensions & ~(Extensions.TypeScript | Extensions.Declaration);
        // (1)
        if (priorityExtensions != 0)
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Searching_all_ancestor_node_modules_directories_for_preferred_extensions_Colon_0, priorityExtensions.ToDisplayString());
            }
            Resolved? result = LoadModuleFromNearestNodeModulesDirectoryWorker(priorityExtensions, mode, typesScopeOnly);
            if (!result.ShouldContinueSearching())
            {
                return result;
            }
        }
        // (2)
        if (secondaryExtensions != 0 && !typesScopeOnly)
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Searching_all_ancestor_node_modules_directories_for_fallback_extensions_Colon_0, secondaryExtensions.ToDisplayString());
            }
            return LoadModuleFromNearestNodeModulesDirectoryWorker(secondaryExtensions, mode, typesScopeOnly);
        }
        return ContinueSearching();
    }

    public Resolved? LoadModuleFromNearestNodeModulesDirectoryWorker(Extensions ext, ResolutionMode mode, bool typesScopeOnly)
    {
        var (result, _) = TsPathApi.ForEachAncestorDirectory<Resolved?>(
            containingDirectory,
            directory =>
            {
                // !!! stop at global cache
                if (TsPathApi.GetBaseFileName(directory) != "node_modules")
                {
                    Resolved? res = LoadModuleFromImmediateNodeModulesDirectory(ext, directory, typesScopeOnly);
                    return (res, !res.ShouldContinueSearching());
                }
                return (ContinueSearching(), false);
            });
        return result;
    }

    public Resolved? LoadModuleFromImmediateNodeModulesDirectory(Extensions extensions, string directory, bool typesScopeOnly)
    {
        string nodeModulesFolder = TsPathApi.CombinePaths(directory, "node_modules");
        if (!resolver.host.FS().DirectoryExists(nodeModulesFolder))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Directory_0_does_not_exist_skipping_all_lookups_in_it, nodeModulesFolder);
            }
            return ContinueSearching();
        }

        if (!typesScopeOnly)
        {
            Resolved? packageResult = LoadModuleFromSpecificNodeModulesDirectory(extensions, name, nodeModulesFolder);
            if (!packageResult.ShouldContinueSearching())
            {
                return packageResult;
            }
        }

        if ((extensions & Extensions.Declaration) != 0)
        {
            string nodeModulesAtTypes = TsPathApi.CombinePaths(nodeModulesFolder, "@types");
            if (!resolver.host.FS().DirectoryExists(nodeModulesAtTypes))
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Directory_0_does_not_exist_skipping_all_lookups_in_it, nodeModulesAtTypes);
                }
                return ContinueSearching();
            }
            return LoadModuleFromSpecificNodeModulesDirectory(Extensions.Declaration, MangleScopedPackageNameWithTrace(name), nodeModulesAtTypes);
        }

        return ContinueSearching();
    }

    public Resolved? LoadModuleFromSpecificNodeModulesDirectory(Extensions ext, string moduleName, string nodeModulesDirectory)
    {
        // Strip any trailing directory separator so that imports like `pkg/` and `pkg` produce
        // identical `candidate` and `packageDirectory` strings.
        // https://github.com/microsoft/typescript-go/issues/3526
        string candidate = TsPathApi.RemoveTrailingDirectorySeparator(TsPathApi.NormalizePath(TsPathApi.CombinePaths(nodeModulesDirectory, moduleName)));
        (string packageName, string rest) = ParsePackageName(moduleName);
        string packageDirectory = TsPathApi.CombinePaths(nodeModulesDirectory, packageName);
        if (packageName == "")
        {
            packageDirectory = candidate;
        }

        if (resolvePackageDirectoryOnly)
        {
            if (resolver.host.FS().DirectoryExists(packageDirectory))
            {
                return new Resolved { Path = packageDirectory };
            }
            return ContinueSearching();
        }

        InfoCacheEntry? rootPackageInfo = null;
        // First look for a nested package.json, as in `node_modules/foo/bar/package.json`
        InfoCacheEntry? packageInfo = GetPackageJsonInfo(candidate);
        // But only if we're not respecting export maps (if we are, we might redirect around this location)
        if (rest != "" && packageInfo.Exists())
        {
            if ((features & NodeResolutionFeatures.Exports) != 0)
            {
                rootPackageInfo = GetPackageJsonInfo(packageDirectory);
            }
            if (!rootPackageInfo.Exists() || rootPackageInfo!.Contents!.Exports.Type == JSONValueType.JSONValueTypeNotPresent)
            {
                Resolved? fromFile = LoadModuleFromFile(ext, candidate);
                if (!fromFile.ShouldContinueSearching())
                {
                    return fromFile;
                }

                Resolved? fromDirectory = LoadNodeModuleFromDirectoryWorker(ext, candidate, packageInfo);
                if (!fromDirectory.ShouldContinueSearching())
                {
                    fromDirectory!.PackageId = GetPackageId(fromDirectory.Path, packageInfo);
                    return fromDirectory;
                }
            }
        }

        ResolutionKindSpecificLoader loader = (extensions, candidate) =>
        {
            if (rest != "" || !esmMode)
            {
                Resolved? fromFile = LoadModuleFromFile(extensions, candidate);
                if (!fromFile.ShouldContinueSearching())
                {
                    fromFile!.PackageId = GetPackageId(fromFile.Path, packageInfo);
                    return fromFile;
                }
            }
            Resolved? fromDirectory = LoadNodeModuleFromDirectoryWorker(extensions, candidate, packageInfo);
            if (!fromDirectory.ShouldContinueSearching())
            {
                fromDirectory!.PackageId = GetPackageId(fromDirectory.Path, packageInfo);
                return fromDirectory;
            }
            if (rest == "" && packageInfo.Exists() &&
                (packageInfo!.Contents!.Exports.Type == JSONValueType.JSONValueTypeNotPresent || packageInfo.Contents.Exports.Type == JSONValueType.JSONValueTypeNull) &&
                esmMode)
            {
                // EsmMode disables index lookup generally, however non-relative package resolutions
                // still assume a default `index.js` entrypoint if no `main` or `exports` are present.
                Resolved? indexResult = LoadModuleFromFile(extensions, TsPathApi.CombinePaths(candidate, "index.js"));
                if (!indexResult.ShouldContinueSearching())
                {
                    indexResult!.PackageId = GetPackageId(indexResult.Path, packageInfo);
                    return indexResult;
                }
            }
            return ContinueSearching();
        };

        if (rest != "")
        {
            packageInfo = rootPackageInfo;
            if (packageInfo == null)
            {
                // Previous `packageInfo` may have been from a nested package.json; ensure we have the root's now.
                packageInfo = GetPackageJsonInfo(packageDirectory);
            }
        }
        if (packageInfo != null)
        {
            resolvedPackageDirectory = true;
            if ((features & NodeResolutionFeatures.Exports) != 0 &&
                packageInfo.Exists() &&
                !packageInfo.Contents!.Exports.IsFalsy())
            {
                // package exports are higher priority than file/directory/typesVersions lookups.
                // *A top-level `"exports": null` does NOT block fallback resolution.
                return LoadModuleFromExports(packageInfo, ext, TsPathApi.CombinePaths(".", rest));
            }
            if (rest != "" && packageInfo.Exists())
            {
                VersionPaths versionPaths = packageInfo.Contents!.GetVersionPaths(GetTraceFunc());
                if (versionPaths.Exists())
                {
                    if (tracer != null)
                    {
                        tracer.Write(Diagnostics.X_package_json_has_a_typesVersions_entry_0_that_matches_compiler_version_1_looking_for_a_pattern_to_match_module_name_2, versionPaths.Version, TsVersion.Version(), rest);
                    }
                    ParsedPatterns pathPatterns = TryParsePatterns(versionPaths.GetPaths());
                    Resolved? fromPaths = TryLoadModuleUsingPaths(ext, rest, packageDirectory, versionPaths.GetPaths(), pathPatterns, loader);
                    if (!fromPaths.ShouldContinueSearching())
                    {
                        return fromPaths;
                    }
                }
            }
        }
        return loader(ext, candidate);
    }

    public ResolvedModule CreateResolvedModuleHandlingSymlink(Resolved? resolved)
    {
        bool isExternalLibraryImport = resolved != null && resolved.Path.Contains("/node_modules/", StringComparison.Ordinal);
        if (compilerOptions.PreserveSymlinks != Tristate.True &&
            isExternalLibraryImport &&
            resolved!.OriginalPath == "" &&
            !TsPathApi.IsExternalModuleNameRelative(name))
        {
            (string originalPath, string resolvedFileName) = GetOriginalAndResolvedFileName(resolved.Path);
            if (originalPath != "")
            {
                resolved.Path = resolvedFileName;
                resolved.OriginalPath = originalPath;
            }
        }
        return CreateResolvedModule(resolved, isExternalLibraryImport);
    }

    public ResolvedModule CreateResolvedModule(Resolved? resolved, bool isExternalLibraryImport)
    {
        var resolvedModule = new ResolvedModule
        {
            ResolutionDiagnostics = diagnostics,
        };

        if (resolved != null)
        {
            resolvedModule.ResolvedFileName = resolved.Path;
            resolvedModule.OriginalPath = resolved.OriginalPath;
            resolvedModule.IsExternalLibraryImport = isExternalLibraryImport;
            resolvedModule.ResolvedUsingTsExtension = resolved.ResolvedUsingTsExtension;
            resolvedModule.Extension = resolved.Extension;
            resolvedModule.PackageId = resolved.PackageId;
        }
        return resolvedModule;
    }

    public ResolvedTypeReferenceDirective CreateResolvedTypeReferenceDirective(Resolved? resolved, bool primary)
    {
        var resolvedTypeReferenceDirective = new ResolvedTypeReferenceDirective
        {
            ResolutionDiagnostics = diagnostics,
        };

        if (resolved.IsResolved())
        {
            if (!TsPathApi.ExtensionIsTs(resolved!.Extension))
            {
                Debug.Fail("expected a TypeScript file extension");
            }
            resolvedTypeReferenceDirective.ResolvedFileName = resolved.Path;
            resolvedTypeReferenceDirective.Primary = primary;
            resolvedTypeReferenceDirective.PackageId = resolved.PackageId;
            resolvedTypeReferenceDirective.IsExternalLibraryImport = resolved.Path.Contains("/node_modules/", StringComparison.Ordinal);

            if (compilerOptions.PreserveSymlinks != Tristate.True)
            {
                (string originalPath, string resolvedFileName) = GetOriginalAndResolvedFileName(resolved.Path);
                if (originalPath != "")
                {
                    resolvedTypeReferenceDirective.ResolvedFileName = resolvedFileName;
                    resolvedTypeReferenceDirective.OriginalPath = originalPath;
                }
            }
        }
        return resolvedTypeReferenceDirective;
    }

    public (string OriginalPath, string ResolvedFileName) GetOriginalAndResolvedFileName(string fileName)
    {
        string resolvedFileName = RealPath(fileName);
        var comparePathsOptions = new Ts.TsPath.ComparePathsOptions
        {
            UseCaseSensitiveFileNames = resolver.host.FS().UseCaseSensitiveFileNames(),
            CurrentDirectory = resolver.host.GetCurrentDirectory(),
        };
        if (TsPathApi.ComparePaths(fileName, resolvedFileName, comparePathsOptions) == 0)
        {
            // If the fileName and realpath differ only in casing, prefer fileName so we can issue
            // correct errors for casing under forceConsistentCasingInFileNames.
            return ("", fileName);
        }
        return (fileName, resolvedFileName);
    }

    public Resolved? TryLoadModuleUsingOptionalResolutionSettings()
    {
        Resolved? resolved = TryLoadModuleUsingPathsIfEligible();
        if (!resolved.ShouldContinueSearching())
        {
            return resolved;
        }

        if (!TsPathApi.IsExternalModuleNameRelative(name))
        {
            // No more tryLoadModuleUsingBaseUrl.
            return ContinueSearching();
        }
        return TryLoadModuleUsingRootDirs();
    }

    private ParsedPatterns GetParsedPatternsForPaths()
    {
        if (ReferenceEquals(compilerOptions, resolver.compilerOptions))
        {
            return resolver.GetParsedPatternsForPaths();
        }
        lock (parsedPatternsGate)
        {
            if (!parsedPatternsComputed)
            {
                parsedPatternsComputed = true;
                parsedPatternsForPaths = TryParsePatterns(compilerOptions.Paths);
            }
        }
        return parsedPatternsForPaths!;
    }

    public Resolved? TryLoadModuleUsingPathsIfEligible()
    {
        if (compilerOptions.Paths.Size() > 0 && !TsPathApi.PathIsRelative(name))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.X_paths_option_is_specified_looking_for_a_pattern_to_match_module_name_0, name);
            }
        }
        else
        {
            return ContinueSearching();
        }
        string baseDirectory = compilerOptions.GetPathsBasePath(resolver.host.GetCurrentDirectory());
        ParsedPatterns pathPatterns = GetParsedPatternsForPaths();
        return TryLoadModuleUsingPaths(
            extensions,
            name,
            baseDirectory,
            compilerOptions.Paths,
            pathPatterns,
            (extensions, candidate) => NodeLoadModuleByRelativeName(extensions, candidate, true));
    }

    public Resolved? TryLoadModuleUsingPaths(Extensions extensions, string moduleName, string containingDirectory, OrderedMap<string, string[]>? paths, ParsedPatterns pathPatterns, ResolutionKindSpecificLoader loader)
    {
        Pattern matchedPattern = MatchPatternOrExact(pathPatterns, moduleName);
        if (matchedPattern.IsValid)
        {
            string matchedStar = matchedPattern.MatchedText(moduleName);
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Module_name_0_matched_pattern_1, moduleName, matchedPattern.Text);
            }
            // paths is non-null whenever a pattern matches: the patterns are derived from it.
            foreach (string subst in paths!.GetOrZero(matchedPattern.Text) ?? Array.Empty<string>())
            {
                string path = ReplaceFirst(subst, "*", matchedStar);
                string candidate = TsPathApi.NormalizePath(TsPathApi.CombinePaths(containingDirectory, path));
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Trying_substitution_0_candidate_module_location_Colon_1, subst, path);
                }
                // A path mapping may have an extension
                string extensionFromSubst = TsPathApi.TryGetExtensionFromPath(subst);
                if (extensionFromSubst != "")
                {
                    if (TryFile(candidate, out string tryPath))
                    {
                        return new Resolved
                        {
                            Path = tryPath,
                            Extension = extensionFromSubst,
                        };
                    }
                }
                // When the substitution path has an explicit extension, the extension came from the
                // paths config, not the module specifier. Suppress resolvedUsingTsExtension.
                bool saveCandidateEndingIsFromConfig = candidateEndingIsFromConfig;
                if (extensionFromSubst != "")
                {
                    candidateEndingIsFromConfig = true;
                }
                Resolved? resolved = loader(extensions, candidate);
                candidateEndingIsFromConfig = saveCandidateEndingIsFromConfig;
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
        }
        return ContinueSearching();
    }

    public Resolved? TryLoadModuleUsingRootDirs()
    {
        if (compilerOptions.RootDirs == null || compilerOptions.RootDirs.Length == 0)
        {
            return ContinueSearching();
        }

        if (tracer != null)
        {
            tracer.Write(Diagnostics.X_rootDirs_option_is_set_using_it_to_resolve_relative_module_name_0, name);
        }

        string candidate = TsPathApi.NormalizePath(TsPathApi.CombinePaths(containingDirectory, name));

        string matchedRootDir = "";
        string matchedNormalizedPrefix = "";
        foreach (string rootDir in compilerOptions.RootDirs)
        {
            // rootDirs are expected to be absolute.
            string normalizedRoot = TsPathApi.NormalizePath(rootDir);
            if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedRoot += "/";
            }
            bool isLongestMatchingPrefix = candidate.StartsWith(normalizedRoot, StringComparison.Ordinal) &&
                (matchedNormalizedPrefix == "" || matchedNormalizedPrefix.Length < normalizedRoot.Length);

            if (tracer != null)
            {
                tracer.Write(Diagnostics.Checking_if_0_is_the_longest_matching_prefix_for_1_2, normalizedRoot, candidate, isLongestMatchingPrefix);
            }

            if (isLongestMatchingPrefix)
            {
                matchedNormalizedPrefix = normalizedRoot;
                matchedRootDir = rootDir;
            }
        }

        if (matchedNormalizedPrefix != "")
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Longest_matching_prefix_for_0_is_1, candidate, matchedNormalizedPrefix);
            }
            string suffix = candidate.Substring(matchedNormalizedPrefix.Length);

            // first - try to load from an initial location
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Loading_0_from_the_root_dir_1_candidate_location_2, suffix, matchedNormalizedPrefix, candidate);
            }
            Resolved? Loader(Extensions extensions, string cand) => NodeLoadModuleByRelativeName(extensions, cand, true);
            Resolved? resolvedFileName = Loader(extensions, candidate);
            if (!resolvedFileName.ShouldContinueSearching())
            {
                return resolvedFileName;
            }

            if (tracer != null)
            {
                tracer.Write(Diagnostics.Trying_other_entries_in_rootDirs);
            }
            // then try to resolve using remaining entries in rootDirs
            foreach (string rootDir in compilerOptions.RootDirs)
            {
                if (rootDir == matchedRootDir)
                {
                    continue;
                }
                string cand = TsPathApi.CombinePaths(TsPathApi.NormalizePath(rootDir), suffix);
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Loading_0_from_the_root_dir_1_candidate_location_2, suffix, rootDir, cand);
                }
                Resolved? r = Loader(extensions, cand);
                if (!r.ShouldContinueSearching())
                {
                    return r;
                }
            }
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Module_resolution_using_rootDirs_has_failed);
            }
        }
        return ContinueSearching();
    }

    public Resolved? NodeLoadModuleByRelativeName(Extensions extensions, string candidate, bool considerPackageJson)
    {
        if (tracer != null)
        {
            tracer.Write(Diagnostics.Loading_module_as_file_Slash_folder_candidate_module_location_0_target_file_types_Colon_1, candidate, extensions.ToDisplayString());
        }
        if (!TsPathApi.HasTrailingDirectorySeparator(candidate))
        {
            string parentOfCandidate = TsPathApi.GetDirectoryPath(candidate);
            if (!resolver.host.FS().DirectoryExists(parentOfCandidate))
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Directory_0_does_not_exist_skipping_all_lookups_in_it, parentOfCandidate);
                }
                return ContinueSearching();
            }
            Resolved? resolvedFromFile = LoadModuleFromFile(extensions, candidate);
            if (resolvedFromFile != null)
            {
                if (considerPackageJson)
                {
                    string packageDirectory = ParseNodeModuleFromPath(resolvedFromFile.Path, false);
                    if (packageDirectory != "")
                    {
                        resolvedFromFile.PackageId = GetPackageId(resolvedFromFile.Path, GetPackageJsonInfo(packageDirectory));
                    }
                }
                return resolvedFromFile;
            }
        }
        if (!resolver.host.FS().DirectoryExists(candidate))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Directory_0_does_not_exist_skipping_all_lookups_in_it, candidate);
            }
            return ContinueSearching();
        }
        // esm mode relative imports shouldn't do any directory lookups.
        if (!esmMode)
        {
            return LoadNodeModuleFromDirectory(extensions, candidate, considerPackageJson);
        }
        return ContinueSearching();
    }

    public Resolved? LoadModuleFromFile(Extensions extensions, string candidate)
    {
        // ./foo.js -> ./foo.ts
        Resolved? resolvedByReplacingExtension = LoadModuleFromFileNoImplicitExtensions(extensions, candidate);
        if (resolvedByReplacingExtension != null)
        {
            return resolvedByReplacingExtension;
        }

        // ./foo -> ./foo.ts
        if (!esmMode)
        {
            return TryAddingExtensions(candidate, extensions, "");
        }

        return ContinueSearching();
    }

    public Resolved? LoadModuleFromFileNoImplicitExtensions(Extensions extensions, string candidate)
    {
        string baseName = TsPathApi.GetBaseFileName(candidate);
        if (!baseName.Contains('.'))
        {
            return ContinueSearching(); // extensionless import, no lookups performed
        }
        string extensionless = TsPathApi.RemoveFileExtension(candidate);
        if (extensionless == candidate)
        {
            extensionless = candidate.Substring(0, candidate.LastIndexOf('.'));
        }

        string extension = candidate.Substring(extensionless.Length);
        if (tracer != null)
        {
            tracer.Write(Diagnostics.File_name_0_has_a_1_extension_stripping_it, candidate, extension);
        }
        return TryAddingExtensions(extensionless, extensions, extension);
    }

    public Resolved? TryAddingExtensions(string extensionless, Extensions extensions, string originalExtension)
    {
        string directory = TsPathApi.GetDirectoryPath(extensionless);
        if (directory != "" && !resolver.host.FS().DirectoryExists(directory))
        {
            return ContinueSearching();
        }

        if (originalExtension == TsPathApi.ExtensionMjs || originalExtension == TsPathApi.ExtensionMts || originalExtension == TsPathApi.ExtensionDmts)
        {
            if ((extensions & Extensions.TypeScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionMts, extensionless, originalExtension == TsPathApi.ExtensionMts || originalExtension == TsPathApi.ExtensionDmts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.Declaration) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionDmts, extensionless, originalExtension == TsPathApi.ExtensionMts || originalExtension == TsPathApi.ExtensionDmts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.JavaScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionMjs, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            return ContinueSearching();
        }
        if (originalExtension == TsPathApi.ExtensionCjs || originalExtension == TsPathApi.ExtensionCts || originalExtension == TsPathApi.ExtensionDcts)
        {
            if ((extensions & Extensions.TypeScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionCts, extensionless, originalExtension == TsPathApi.ExtensionCts || originalExtension == TsPathApi.ExtensionDcts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.Declaration) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionDcts, extensionless, originalExtension == TsPathApi.ExtensionCts || originalExtension == TsPathApi.ExtensionDcts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.JavaScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionCjs, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            return ContinueSearching();
        }
        if (originalExtension == TsPathApi.ExtensionJson)
        {
            if ((extensions & Extensions.Declaration) != 0)
            {
                Resolved? resolved = TryExtension(".d.json.ts", extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.Json) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionJson, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            return ContinueSearching();
        }
        if (originalExtension == TsPathApi.ExtensionTsx || originalExtension == TsPathApi.ExtensionJsx)
        {
            // prefers matching tsx and jsx files exactly before falling back to the ts or js file path.
            if ((extensions & Extensions.TypeScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionTsx, extensionless, originalExtension == TsPathApi.ExtensionTsx);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
                resolved = TryExtension(TsPathApi.ExtensionTs, extensionless, originalExtension == TsPathApi.ExtensionTsx);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.Declaration) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionDts, extensionless, originalExtension == TsPathApi.ExtensionTsx);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.JavaScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionJsx, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
                resolved = TryExtension(TsPathApi.ExtensionJs, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            return ContinueSearching();
        }
        if (originalExtension == TsPathApi.ExtensionTs || originalExtension == TsPathApi.ExtensionDts || originalExtension == TsPathApi.ExtensionJs || originalExtension == "")
        {
            if ((extensions & Extensions.TypeScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionTs, extensionless, originalExtension == TsPathApi.ExtensionTs || originalExtension == TsPathApi.ExtensionDts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
                resolved = TryExtension(TsPathApi.ExtensionTsx, extensionless, originalExtension == TsPathApi.ExtensionTs || originalExtension == TsPathApi.ExtensionDts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.Declaration) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionDts, extensionless, originalExtension == TsPathApi.ExtensionTs || originalExtension == TsPathApi.ExtensionDts);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if ((extensions & Extensions.JavaScript) != 0)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionJs, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
                resolved = TryExtension(TsPathApi.ExtensionJsx, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            if (isConfigLookup)
            {
                Resolved? resolved = TryExtension(TsPathApi.ExtensionJson, extensionless, false);
                if (!resolved.ShouldContinueSearching())
                {
                    return resolved;
                }
            }
            return ContinueSearching();
        }
        // default
        if ((extensions & Extensions.Declaration) != 0 && !TsPathApi.IsDeclarationFileName(extensionless + originalExtension))
        {
            Resolved? resolved = TryExtension(".d" + originalExtension + ".ts", extensionless, false);
            if (!resolved.ShouldContinueSearching())
            {
                return resolved;
            }
        }
        return ContinueSearching();
    }

    public Resolved? TryExtension(string extension, string extensionless, bool resolvedUsingTsExtension)
    {
        string fileName = extensionless + extension;
        if (TryFile(fileName, out string path))
        {
            return new Resolved
            {
                Path = path,
                Extension = extension,
                ResolvedUsingTsExtension = !candidateEndingIsFromConfig && resolvedUsingTsExtension,
            };
        }
        return ContinueSearching();
    }

    public bool TryFile(string fileName, out string result)
    {
        if (compilerOptions.ModuleSuffixes == null || compilerOptions.ModuleSuffixes.Length == 0)
        {
            result = fileName;
            return TryFileLookup(fileName);
        }

        string ext = TsPathApi.TryGetExtensionFromPath(fileName);
        string fileNameNoExtension = TsPathApi.RemoveExtension(fileName, ext);
        foreach (string suffix in compilerOptions.ModuleSuffixes)
        {
            string path = fileNameNoExtension + suffix + ext;
            if (TryFileLookup(path))
            {
                result = path;
                return true;
            }
        }
        result = fileName;
        return false;
    }

    public bool TryFileLookup(string fileName)
    {
        if (resolver.host.FS().FileExists(fileName))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.File_0_exists_use_it_as_a_name_resolution_result, fileName);
            }
            return true;
        }
        else if (tracer != null)
        {
            tracer.Write(Diagnostics.File_0_does_not_exist, fileName);
        }
        return false;
    }

    public Resolved? LoadNodeModuleFromDirectory(Extensions extensions, string candidate, bool considerPackageJson)
    {
        InfoCacheEntry? packageInfo = null;
        if (considerPackageJson)
        {
            packageInfo = GetPackageJsonInfo(candidate);
        }

        return LoadNodeModuleFromDirectoryWorker(extensions, candidate, packageInfo);
    }

    public Resolved? LoadNodeModuleFromDirectoryWorker(Extensions ext, string candidate, InfoCacheEntry? packageInfo)
    {
        string packageFile = "";
        VersionPaths versionPaths = default;
        if (packageInfo.Exists())
        {
            versionPaths = packageInfo!.Contents!.GetVersionPaths(GetTraceFunc());
            if (TsPathApi.ComparePaths(candidate, packageInfo.PackageDirectory, new Ts.TsPath.ComparePathsOptions { UseCaseSensitiveFileNames = resolver.host.FS().UseCaseSensitiveFileNames() }) == 0)
            {
                if (GetPackageFile(ext, packageInfo, out string file))
                {
                    packageFile = file;
                }
            }
        }

        ResolutionKindSpecificLoader loader = (extensions, candidate) =>
        {
            Resolved? fromFile = LoadFileNameFromPackageJSONField(extensions, candidate, packageFile);
            if (!fromFile.ShouldContinueSearching())
            {
                return fromFile;
            }

            // Even if `extensions == extensionsDeclaration`, we can still look up a .ts file as a
            // result of package.json "types".
            Extensions expandedExtensions = extensions;
            if (extensions == Extensions.Declaration)
            {
                expandedExtensions = Extensions.TypeScript | Extensions.Declaration;
            }

            // Disable `esmMode` for the resolution of the package path for CJS-mode packages.
            bool saveESMMode = esmMode;
            bool saveCandidateEndingIsFromConfig = candidateEndingIsFromConfig;
            candidateEndingIsFromConfig = true;
            if (packageInfo.Exists() && packageInfo!.Contents!.Type.Value != "module")
            {
                esmMode = false;
            }
            Resolved? result = NodeLoadModuleByRelativeName(expandedExtensions, candidate, false);
            esmMode = saveESMMode;
            candidateEndingIsFromConfig = saveCandidateEndingIsFromConfig;
            return result;
        };

        string indexPath = isConfigLookup
            ? TsPathApi.CombinePaths(candidate, "tsconfig")
            : TsPathApi.CombinePaths(candidate, "index");

        if (versionPaths.Exists() && (packageFile == "" || TsPathApi.ContainsPath(candidate, packageFile, default)))
        {
            string moduleName = packageFile != ""
                ? TsPathApi.GetRelativePathFromDirectory(candidate, packageFile, default)
                : TsPathApi.GetRelativePathFromDirectory(candidate, indexPath, default);
            if (tracer != null)
            {
                tracer.Write(Diagnostics.X_package_json_has_a_typesVersions_entry_0_that_matches_compiler_version_1_looking_for_a_pattern_to_match_module_name_2, versionPaths.Version, TsVersion.Version(), moduleName);
            }
            ParsedPatterns pathPatterns = TryParsePatterns(versionPaths.GetPaths());
            Resolved? result = TryLoadModuleUsingPaths(ext, moduleName, candidate, versionPaths.GetPaths(), pathPatterns, loader);
            if (!result.ShouldContinueSearching())
            {
                if (!string.IsNullOrEmpty(result!.PackageId.Name))
                {
                    Debug.Fail("expected packageId to be empty");
                }
                return result;
            }
        }

        if (packageFile != "")
        {
            Resolved? packageFileResult = loader(ext, packageFile);
            if (!packageFileResult.ShouldContinueSearching())
            {
                if (!string.IsNullOrEmpty(packageFileResult!.PackageId.Name))
                {
                    Debug.Fail("expected packageId to be empty");
                }
                return packageFileResult;
            }
        }

        // ESM mode resolutions don't do package 'index' lookups
        if (!esmMode)
        {
            if (!resolver.host.FS().DirectoryExists(candidate))
            {
                return ContinueSearching();
            }
            return LoadModuleFromFile(ext, indexPath);
        }
        return ContinueSearching();
    }

    // This function is only ever called with paths written in package.json files - never module
    // specifiers written in source files - and so it always allows the candidate to end with a TS
    // extension (but will also try substituting a JS extension for a TS extension).
    public Resolved? LoadFileNameFromPackageJSONField(Extensions extensions, string candidate, string packageJSONValue)
    {
        if ((extensions & Extensions.TypeScript) != 0 && TsPathApi.HasImplementationTSFileExtension(candidate) ||
            (extensions & Extensions.Declaration) != 0 && TsPathApi.IsDeclarationFileName(candidate))
        {
            if (TryFile(candidate, out string path))
            {
                string extension = TsPathApi.TryExtractTSExtension(path);
                // resolvedUsingTsExtension should be true when the pattern ends with * and the
                // candidate file ends in a TS extension.
                bool resolvedUsingTsExtension = packageJSONValue.EndsWith("*", StringComparison.Ordinal) && extension != "";
                return new Resolved
                {
                    Path = path,
                    Extension = extension,
                    ResolvedUsingTsExtension = resolvedUsingTsExtension,
                };
            }
            return ContinueSearching();
        }

        if (isConfigLookup && (extensions & Extensions.Json) != 0 && TsPathApi.FileExtensionIs(candidate, TsPathApi.ExtensionJson))
        {
            if (TryFile(candidate, out string path))
            {
                return new Resolved
                {
                    Path = path,
                    Extension = TsPathApi.ExtensionJson,
                };
            }
        }

        return LoadModuleFromFileNoImplicitExtensions(extensions, candidate);
    }

    public bool GetPackageFile(Extensions extensions, InfoCacheEntry? packageInfo, out string result)
    {
        result = "";
        if (!packageInfo.Exists())
        {
            return false;
        }
        if (isConfigLookup)
        {
            return GetPackageJSONPathField("tsconfig", packageInfo!.Contents!.TSConfig, packageInfo.PackageDirectory, out result);
        }
        if ((extensions & Extensions.Declaration) != 0)
        {
            if (GetPackageJSONPathField("typings", packageInfo!.Contents!.Typings, packageInfo.PackageDirectory, out result))
            {
                return true;
            }
            if (GetPackageJSONPathField("types", packageInfo.Contents.Types, packageInfo.PackageDirectory, out result))
            {
                return true;
            }
        }
        if ((extensions & (Extensions.ImplementationFiles | Extensions.Declaration)) != 0)
        {
            return GetPackageJSONPathField("main", packageInfo!.Contents!.Main, packageInfo.PackageDirectory, out result);
        }
        return false;
    }

    public InfoCacheEntry? GetPackageJsonInfo(string packageDirectory)
    {
        string packageJsonPath = TsPathApi.CombinePaths(packageDirectory, "package.json");

        InfoCacheEntry? existing = resolver.caches.PackageJsonInfoCache.Get(packageJsonPath);
        if (existing != null)
        {
            if (existing.Contents != null)
            {
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.File_0_exists_according_to_earlier_cached_lookups, packageJsonPath);
                }
                return existing.WithPackageDirectory(packageDirectory);
            }
            else
            {
                if (existing.DirectoryExists && tracer != null)
                {
                    tracer.Write(Diagnostics.File_0_does_not_exist_according_to_earlier_cached_lookups, packageJsonPath);
                }
                return null;
            }
        }

        bool directoryExists = resolver.host.FS().DirectoryExists(packageDirectory);
        if (directoryExists && resolver.host.FS().FileExists(packageJsonPath))
        {
            resolver.host.FS().TryReadFile(packageJsonPath, out string contents);
            (Fields packageJsonContent, bool ok) = PackageJsonParser.Parse(Encoding.UTF8.GetBytes(contents));
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Found_package_json_at_0, packageJsonPath);
            }
            var result = new InfoCacheEntry
            {
                PackageDirectory = packageDirectory,
                DirectoryExists = true,
                Contents = new PackageJson
                {
                    Fields = packageJsonContent,
                    Parseable = ok,
                },
            };
            result = resolver.caches.PackageJsonInfoCache.Set(packageJsonPath, result);
            return result.WithPackageDirectory(packageDirectory);
        }
        else
        {
            if (directoryExists && tracer != null)
            {
                tracer.Write(Diagnostics.File_0_does_not_exist, packageJsonPath);
            }
            resolver.caches.PackageJsonInfoCache.Set(packageJsonPath, new InfoCacheEntry
            {
                PackageDirectory = packageDirectory,
                DirectoryExists = directoryExists,
            });
        }
        return null;
    }

    public PackageId GetPackageId(string resolvedFileName, InfoCacheEntry? packageInfo)
    {
        if (packageInfo.Exists())
        {
            PackageJson packageJsonContent = packageInfo!.Contents!;
            if (packageJsonContent.Name.GetValue(out string? name))
            {
                if (packageJsonContent.Version.GetValue(out string? version))
                {
                    string subModuleName = "";
                    if (resolvedFileName.Length > packageInfo.PackageDirectory.Length)
                    {
                        subModuleName = resolvedFileName.Substring(packageInfo.PackageDirectory.Length + 1);
                    }
                    return new PackageId
                    {
                        Name = name!,
                        Version = version!,
                        SubModuleName = subModuleName,
                        PeerDependencies = ReadPackageJsonPeerDependencies(packageInfo),
                    };
                }
            }
        }
        return new PackageId();
    }

    public string ReadPackageJsonPeerDependencies(InfoCacheEntry packageJsonInfo)
    {
        Expected<Dictionary<string, string>> peerDependencies = packageJsonInfo.Contents!.PeerDependencies;
        bool ok = ValidatePackageJSONField("peerDependencies", peerDependencies);
        if (!ok || peerDependencies.Value == null || peerDependencies.Value.Count == 0)
        {
            return "";
        }
        if (tracer != null)
        {
            tracer.Write(Diagnostics.X_package_json_has_a_peerDependencies_field);
        }
        string packageDirectory = RealPath(packageJsonInfo.PackageDirectory);
        int nodeModulesIndex = packageDirectory.LastIndexOf("/node_modules", StringComparison.Ordinal);
        if (nodeModulesIndex == -1)
        {
            return "";
        }
        string nodeModules = packageDirectory.Substring(0, nodeModulesIndex + "/node_modules".Length) + "/";
        // map-iter: 已排序 (Go collects maps.Keys then slices.Sort; sorted here for the same determinism).
        var names = new List<string>(peerDependencies.Value.Keys);
        names.Sort(StringComparer.Ordinal);
        var builder = new StringBuilder();
        foreach (string name in names)
        {
            InfoCacheEntry? peerPackageJson = GetPackageJsonInfo(nodeModules + name);
            if (peerPackageJson.Exists())
            {
                string version = peerPackageJson!.Contents!.Version.Value ?? "";
                builder.Append('+');
                builder.Append(name);
                builder.Append('@');
                builder.Append(version);
                if (tracer != null)
                {
                    tracer.Write(Diagnostics.Found_peerDependency_0_with_1_version, name, version);
                }
            }
            else if (tracer != null)
            {
                tracer.Write(Diagnostics.Failed_to_find_peerDependency_0, name);
            }
        }
        return builder.ToString();
    }

    public string RealPath(string path)
    {
        string rp = TsPathApi.NormalizePath(resolver.host.FS().Realpath(path));
        if (tracer != null)
        {
            tracer.Write(Diagnostics.Resolving_real_path_for_0_result_1, path, rp);
        }
        return rp;
    }

    public bool ValidatePackageJSONField(string fieldName, TypeValidatedField field)
    {
        if (field.IsPresent())
        {
            if (field.IsValid())
            {
                return true;
            }
            if (tracer != null)
            {
                tracer.Write(Diagnostics.Expected_type_of_0_field_in_package_json_to_be_1_got_2, fieldName, field.ExpectedJSONType(), field.ActualJSONType());
            }
        }
        if (tracer != null)
        {
            tracer.Write(Diagnostics.X_package_json_does_not_have_a_0_field, fieldName);
        }
        return false;
    }

    public bool GetPackageJSONPathField(string fieldName, Expected<string> field, string directory, out string result)
    {
        result = "";
        if (!ValidatePackageJSONField(fieldName, field))
        {
            return false;
        }
        if (string.IsNullOrEmpty(field.Value))
        {
            if (tracer != null)
            {
                tracer.Write(Diagnostics.X_package_json_had_a_falsy_0_field, fieldName);
            }
            return false;
        }
        string path = TsPathApi.NormalizePath(TsPathApi.CombinePaths(directory, field.Value));
        if (tracer != null)
        {
            tracer.Write(Diagnostics.X_package_json_has_0_field_1_that_references_2, fieldName, field.Value, path);
        }
        result = path;
        return true;
    }

    public bool ConditionMatches(string condition)
    {
        if (condition == "default" || SliceContains(conditions, condition))
        {
            return true;
        }
        if (!SliceContains(conditions, "types"))
        {
            return false; // only apply versioned types conditions if the types condition is applied
        }
        return IsApplicableVersionedTypesKey(condition);
    }

    public Ts.PackageJson.TraceFunc? GetTraceFunc()
    {
        if (tracer != null)
        {
            return tracer.Write;
        }
        return null;
    }

    // Go: slices.Contains(partsAfterFirst, "..") || slices.Contains(partsAfterFirst, ".") ||
    // slices.Contains(partsAfterFirst, "node_modules"), scanning parts[startIndex:].
    private static bool ContainsAny(List<string> parts, int startIndex)
    {
        for (int i = startIndex; i < parts.Count; i++)
        {
            string p = parts[i];
            if (p == ".." || p == "." || p == "node_modules")
            {
                return true;
            }
        }
        return false;
    }
}
