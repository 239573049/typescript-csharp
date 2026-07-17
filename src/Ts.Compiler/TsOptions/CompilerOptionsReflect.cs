// PORT-NOTE: this file has no 1:1 Go counterpart. It is the AOT-safe replacement for the
// three reflection consumers of core.CompilerOptions that plan/07 §1.1 calls out:
//   - parsinghelpers.go:585-631  mergeCompilerOptions (reflect Set/SetZero/IsZero + json tag)
//   - declscompiler.go:1203-1240 optionsHaveChanges / ForEachCompilerOptionValue (reflect Field(i))
//   - showconfig.go:169-185      serialize (reflect field walk) — deferred with ShowConfig
// Reflection is banned (plan/07 §1.1); the task authorizes either a tools/generator per-field
// switch or a hand-written per-field table. This is the hand-written table (a single source of
// truth = "统一供给"), living in Ts.TsOptions so that Ts.Core/CompilerOptions.cs stays read-only.
// The descriptor order mirrors the CompilerOptions field declaration order (= Go struct order =
// json key order). JsonName is the Go `json:"...,omitzero"` tag, needed for explicit-null merge.
//
// If/when tools/generator gains a per-field emitter, it should emit exactly this table (same
// Name/JsonName/zero semantics) as a *.g.cs and this file is deleted.

using System;
using System.Collections.Generic;
using Ts.Collections;
using Ts.Core;

namespace Ts.TsOptions;

internal sealed class CompilerOptionField
{
    // Go struct field name (PascalCase); matches reflect.Type.Field(i).Name so that
    // CommandLineCompilerOptionsMap.Get(field.Name) resolves the same option declaration.
    public readonly string Name;

    // Go `json:"<name>,omitzero"` tag name; used by the merge to honor explicit nulls.
    public readonly string JsonName;

    // Boxed field value (reflect.Value.Interface equivalent).
    public readonly Func<CompilerOptions, object?> Get;

    // reflect.Value.IsZero equivalent for this field's type.
    public readonly Func<CompilerOptions, bool> IsZero;

    // reflect.Value.SetZero equivalent.
    public readonly Action<CompilerOptions> SetZero;

    // reflect targetField.Set(sourceField): dst.field = src.field.
    public readonly Action<CompilerOptions, CompilerOptions> Copy;

    public CompilerOptionField(
        string name,
        string jsonName,
        Func<CompilerOptions, object?> get,
        Func<CompilerOptions, bool> isZero,
        Action<CompilerOptions> setZero,
        Action<CompilerOptions, CompilerOptions> copy)
    {
        Name = name;
        JsonName = jsonName;
        Get = get;
        IsZero = isZero;
        SetZero = setZero;
        Copy = copy;
    }
}

internal static class CompilerOptionsFields
{
    private static CompilerOptionField Tri(string name, string json, Func<CompilerOptions, Tristate> g, Action<CompilerOptions, Tristate> s) =>
        new(name, json, o => g(o), o => g(o) == Tristate.Unknown, o => s(o, Tristate.Unknown), (src, dst) => s(dst, g(src)));

    private static CompilerOptionField Str(string name, string json, Func<CompilerOptions, string> g, Action<CompilerOptions, string> s) =>
        new(name, json, o => g(o), o => g(o) == "", o => s(o, ""), (src, dst) => s(dst, g(src)));

    private static CompilerOptionField StrArr(string name, string json, Func<CompilerOptions, string[]?> g, Action<CompilerOptions, string[]?> s) =>
        new(name, json, o => g(o), o => g(o) is null, o => s(o, null), (src, dst) => s(dst, g(src)));

    private static CompilerOptionField IntOpt(string name, string json, Func<CompilerOptions, int?> g, Action<CompilerOptions, int?> s) =>
        new(name, json, o => g(o), o => g(o) is null, o => s(o, null), (src, dst) => s(dst, g(src)));

    private static CompilerOptionField PathsMap(string name, string json, Func<CompilerOptions, OrderedMap<string, string[]>?> g, Action<CompilerOptions, OrderedMap<string, string[]>?> s) =>
        new(name, json, o => g(o), o => g(o) is null, o => s(o, null), (src, dst) => s(dst, g(src)));

    private static CompilerOptionField Enum_<TEnum>(string name, string json, Func<CompilerOptions, TEnum> g, Action<CompilerOptions, TEnum> s) where TEnum : struct, Enum =>
        new(name, json, o => g(o), o => EqualityComparer<TEnum>.Default.Equals(g(o), default), o => s(o, default), (src, dst) => s(dst, g(src)));

    // Order mirrors CompilerOptions field declaration order (= Go struct = json key order).
    internal static readonly CompilerOptionField[] Fields = new CompilerOptionField[]
    {
        Tri("AllowJs", "allowJs", o => o.AllowJs, (o, v) => o.AllowJs = v),
        Tri("AllowArbitraryExtensions", "allowArbitraryExtensions", o => o.AllowArbitraryExtensions, (o, v) => o.AllowArbitraryExtensions = v),
        Tri("AllowImportingTsExtensions", "allowImportingTsExtensions", o => o.AllowImportingTsExtensions, (o, v) => o.AllowImportingTsExtensions = v),
        Tri("AllowNonTsExtensions", "allowNonTsExtensions", o => o.AllowNonTsExtensions, (o, v) => o.AllowNonTsExtensions = v),
        Tri("AllowUmdGlobalAccess", "allowUmdGlobalAccess", o => o.AllowUmdGlobalAccess, (o, v) => o.AllowUmdGlobalAccess = v),
        Tri("AllowUnreachableCode", "allowUnreachableCode", o => o.AllowUnreachableCode, (o, v) => o.AllowUnreachableCode = v),
        Tri("AllowUnusedLabels", "allowUnusedLabels", o => o.AllowUnusedLabels, (o, v) => o.AllowUnusedLabels = v),
        Tri("AssumeChangesOnlyAffectDirectDependencies", "assumeChangesOnlyAffectDirectDependencies", o => o.AssumeChangesOnlyAffectDirectDependencies, (o, v) => o.AssumeChangesOnlyAffectDirectDependencies = v),
        Tri("CheckJs", "checkJs", o => o.CheckJs, (o, v) => o.CheckJs = v),
        StrArr("CustomConditions", "customConditions", o => o.CustomConditions, (o, v) => o.CustomConditions = v),
        Tri("Composite", "composite", o => o.Composite, (o, v) => o.Composite = v),
        Tri("EmitDeclarationOnly", "emitDeclarationOnly", o => o.EmitDeclarationOnly, (o, v) => o.EmitDeclarationOnly = v),
        Tri("EmitBOM", "emitBOM", o => o.EmitBOM, (o, v) => o.EmitBOM = v),
        Tri("EmitDecoratorMetadata", "emitDecoratorMetadata", o => o.EmitDecoratorMetadata, (o, v) => o.EmitDecoratorMetadata = v),
        Tri("Declaration", "declaration", o => o.Declaration, (o, v) => o.Declaration = v),
        Str("DeclarationDir", "declarationDir", o => o.DeclarationDir, (o, v) => o.DeclarationDir = v),
        Tri("DeclarationMap", "declarationMap", o => o.DeclarationMap, (o, v) => o.DeclarationMap = v),
        Tri("DeduplicatePackages", "deduplicatePackages", o => o.DeduplicatePackages, (o, v) => o.DeduplicatePackages = v),
        Tri("DisableSizeLimit", "disableSizeLimit", o => o.DisableSizeLimit, (o, v) => o.DisableSizeLimit = v),
        Tri("DisableSourceOfProjectReferenceRedirect", "disableSourceOfProjectReferenceRedirect", o => o.DisableSourceOfProjectReferenceRedirect, (o, v) => o.DisableSourceOfProjectReferenceRedirect = v),
        Tri("DisableSolutionSearching", "disableSolutionSearching", o => o.DisableSolutionSearching, (o, v) => o.DisableSolutionSearching = v),
        Tri("DisableReferencedProjectLoad", "disableReferencedProjectLoad", o => o.DisableReferencedProjectLoad, (o, v) => o.DisableReferencedProjectLoad = v),
        Tri("ErasableSyntaxOnly", "erasableSyntaxOnly", o => o.ErasableSyntaxOnly, (o, v) => o.ErasableSyntaxOnly = v),
        Tri("ExactOptionalPropertyTypes", "exactOptionalPropertyTypes", o => o.ExactOptionalPropertyTypes, (o, v) => o.ExactOptionalPropertyTypes = v),
        Tri("ExperimentalDecorators", "experimentalDecorators", o => o.ExperimentalDecorators, (o, v) => o.ExperimentalDecorators = v),
        Tri("ForceConsistentCasingInFileNames", "forceConsistentCasingInFileNames", o => o.ForceConsistentCasingInFileNames, (o, v) => o.ForceConsistentCasingInFileNames = v),
        Tri("IsolatedModules", "isolatedModules", o => o.IsolatedModules, (o, v) => o.IsolatedModules = v),
        Tri("IsolatedDeclarations", "isolatedDeclarations", o => o.IsolatedDeclarations, (o, v) => o.IsolatedDeclarations = v),
        Tri("IgnoreConfig", "ignoreConfig", o => o.IgnoreConfig, (o, v) => o.IgnoreConfig = v),
        Str("IgnoreDeprecations", "ignoreDeprecations", o => o.IgnoreDeprecations, (o, v) => o.IgnoreDeprecations = v),
        Tri("ImportHelpers", "importHelpers", o => o.ImportHelpers, (o, v) => o.ImportHelpers = v),
        Tri("InlineSourceMap", "inlineSourceMap", o => o.InlineSourceMap, (o, v) => o.InlineSourceMap = v),
        Tri("InlineSources", "inlineSources", o => o.InlineSources, (o, v) => o.InlineSources = v),
        Tri("Init", "init", o => o.Init, (o, v) => o.Init = v),
        Tri("Incremental", "incremental", o => o.Incremental, (o, v) => o.Incremental = v),
        Enum_<JsxEmit>("Jsx", "jsx", o => o.Jsx, (o, v) => o.Jsx = v),
        Str("JsxFactory", "jsxFactory", o => o.JsxFactory, (o, v) => o.JsxFactory = v),
        Str("JsxFragmentFactory", "jsxFragmentFactory", o => o.JsxFragmentFactory, (o, v) => o.JsxFragmentFactory = v),
        Str("JsxImportSource", "jsxImportSource", o => o.JsxImportSource, (o, v) => o.JsxImportSource = v),
        StrArr("Lib", "lib", o => o.Lib, (o, v) => o.Lib = v),
        Tri("LibReplacement", "libReplacement", o => o.LibReplacement, (o, v) => o.LibReplacement = v),
        Str("Locale", "locale", o => o.Locale, (o, v) => o.Locale = v),
        Str("MapRoot", "mapRoot", o => o.MapRoot, (o, v) => o.MapRoot = v),
        Enum_<ModuleKind>("Module", "module", o => o.Module, (o, v) => o.Module = v),
        Enum_<ModuleResolutionKind>("ModuleResolution", "moduleResolution", o => o.ModuleResolution, (o, v) => o.ModuleResolution = v),
        StrArr("ModuleSuffixes", "moduleSuffixes", o => o.ModuleSuffixes, (o, v) => o.ModuleSuffixes = v),
        Enum_<ModuleDetectionKind>("ModuleDetection", "moduleDetection", o => o.ModuleDetection, (o, v) => o.ModuleDetection = v),
        Enum_<NewLineKind>("NewLine", "newLine", o => o.NewLine, (o, v) => o.NewLine = v),
        Tri("NoEmit", "noEmit", o => o.NoEmit, (o, v) => o.NoEmit = v),
        Tri("NoCheck", "noCheck", o => o.NoCheck, (o, v) => o.NoCheck = v),
        Tri("NoErrorTruncation", "noErrorTruncation", o => o.NoErrorTruncation, (o, v) => o.NoErrorTruncation = v),
        Tri("NoFallthroughCasesInSwitch", "noFallthroughCasesInSwitch", o => o.NoFallthroughCasesInSwitch, (o, v) => o.NoFallthroughCasesInSwitch = v),
        Tri("NoImplicitAny", "noImplicitAny", o => o.NoImplicitAny, (o, v) => o.NoImplicitAny = v),
        Tri("NoImplicitThis", "noImplicitThis", o => o.NoImplicitThis, (o, v) => o.NoImplicitThis = v),
        Tri("NoImplicitReturns", "noImplicitReturns", o => o.NoImplicitReturns, (o, v) => o.NoImplicitReturns = v),
        Tri("NoEmitHelpers", "noEmitHelpers", o => o.NoEmitHelpers, (o, v) => o.NoEmitHelpers = v),
        Tri("NoLib", "noLib", o => o.NoLib, (o, v) => o.NoLib = v),
        Tri("NoPropertyAccessFromIndexSignature", "noPropertyAccessFromIndexSignature", o => o.NoPropertyAccessFromIndexSignature, (o, v) => o.NoPropertyAccessFromIndexSignature = v),
        Tri("NoUncheckedIndexedAccess", "noUncheckedIndexedAccess", o => o.NoUncheckedIndexedAccess, (o, v) => o.NoUncheckedIndexedAccess = v),
        Tri("NoEmitOnError", "noEmitOnError", o => o.NoEmitOnError, (o, v) => o.NoEmitOnError = v),
        Tri("NoUnusedLocals", "noUnusedLocals", o => o.NoUnusedLocals, (o, v) => o.NoUnusedLocals = v),
        Tri("NoUnusedParameters", "noUnusedParameters", o => o.NoUnusedParameters, (o, v) => o.NoUnusedParameters = v),
        Tri("NoResolve", "noResolve", o => o.NoResolve, (o, v) => o.NoResolve = v),
        Tri("NoImplicitOverride", "noImplicitOverride", o => o.NoImplicitOverride, (o, v) => o.NoImplicitOverride = v),
        Tri("NoUncheckedSideEffectImports", "noUncheckedSideEffectImports", o => o.NoUncheckedSideEffectImports, (o, v) => o.NoUncheckedSideEffectImports = v),
        Str("OutDir", "outDir", o => o.OutDir, (o, v) => o.OutDir = v),
        PathsMap("Paths", "paths", o => o.Paths, (o, v) => o.Paths = v),
        Tri("PreserveConstEnums", "preserveConstEnums", o => o.PreserveConstEnums, (o, v) => o.PreserveConstEnums = v),
        Tri("PreserveSymlinks", "preserveSymlinks", o => o.PreserveSymlinks, (o, v) => o.PreserveSymlinks = v),
        Str("Project", "project", o => o.Project, (o, v) => o.Project = v),
        Tri("ResolveJsonModule", "resolveJsonModule", o => o.ResolveJsonModule, (o, v) => o.ResolveJsonModule = v),
        Tri("ResolvePackageJsonExports", "resolvePackageJsonExports", o => o.ResolvePackageJsonExports, (o, v) => o.ResolvePackageJsonExports = v),
        Tri("ResolvePackageJsonImports", "resolvePackageJsonImports", o => o.ResolvePackageJsonImports, (o, v) => o.ResolvePackageJsonImports = v),
        Tri("RemoveComments", "removeComments", o => o.RemoveComments, (o, v) => o.RemoveComments = v),
        Tri("RewriteRelativeImportExtensions", "rewriteRelativeImportExtensions", o => o.RewriteRelativeImportExtensions, (o, v) => o.RewriteRelativeImportExtensions = v),
        Str("ReactNamespace", "reactNamespace", o => o.ReactNamespace, (o, v) => o.ReactNamespace = v),
        Str("RootDir", "rootDir", o => o.RootDir, (o, v) => o.RootDir = v),
        StrArr("RootDirs", "rootDirs", o => o.RootDirs, (o, v) => o.RootDirs = v),
        Tri("SkipLibCheck", "skipLibCheck", o => o.SkipLibCheck, (o, v) => o.SkipLibCheck = v),
        Tri("StableTypeOrdering", "stableTypeOrdering", o => o.StableTypeOrdering, (o, v) => o.StableTypeOrdering = v),
        Tri("Strict", "strict", o => o.Strict, (o, v) => o.Strict = v),
        Tri("StrictBindCallApply", "strictBindCallApply", o => o.StrictBindCallApply, (o, v) => o.StrictBindCallApply = v),
        Tri("StrictBuiltinIteratorReturn", "strictBuiltinIteratorReturn", o => o.StrictBuiltinIteratorReturn, (o, v) => o.StrictBuiltinIteratorReturn = v),
        Tri("StrictFunctionTypes", "strictFunctionTypes", o => o.StrictFunctionTypes, (o, v) => o.StrictFunctionTypes = v),
        Tri("StrictNullChecks", "strictNullChecks", o => o.StrictNullChecks, (o, v) => o.StrictNullChecks = v),
        Tri("StrictPropertyInitialization", "strictPropertyInitialization", o => o.StrictPropertyInitialization, (o, v) => o.StrictPropertyInitialization = v),
        Tri("StripInternal", "stripInternal", o => o.StripInternal, (o, v) => o.StripInternal = v),
        Tri("SkipDefaultLibCheck", "skipDefaultLibCheck", o => o.SkipDefaultLibCheck, (o, v) => o.SkipDefaultLibCheck = v),
        Tri("SourceMap", "sourceMap", o => o.SourceMap, (o, v) => o.SourceMap = v),
        Str("SourceRoot", "sourceRoot", o => o.SourceRoot, (o, v) => o.SourceRoot = v),
        Tri("SuppressOutputPathCheck", "suppressOutputPathCheck", o => o.SuppressOutputPathCheck, (o, v) => o.SuppressOutputPathCheck = v),
        Enum_<ScriptTarget>("Target", "target", o => o.Target, (o, v) => o.Target = v),
        Tri("TraceResolution", "traceResolution", o => o.TraceResolution, (o, v) => o.TraceResolution = v),
        Str("TsBuildInfoFile", "tsBuildInfoFile", o => o.TsBuildInfoFile, (o, v) => o.TsBuildInfoFile = v),
        StrArr("TypeRoots", "typeRoots", o => o.TypeRoots, (o, v) => o.TypeRoots = v),
        StrArr("Types", "types", o => o.Types, (o, v) => o.Types = v),
        Tri("UseDefineForClassFields", "useDefineForClassFields", o => o.UseDefineForClassFields, (o, v) => o.UseDefineForClassFields = v),
        Tri("UseUnknownInCatchVariables", "useUnknownInCatchVariables", o => o.UseUnknownInCatchVariables, (o, v) => o.UseUnknownInCatchVariables = v),
        Tri("VerbatimModuleSyntax", "verbatimModuleSyntax", o => o.VerbatimModuleSyntax, (o, v) => o.VerbatimModuleSyntax = v),
        IntOpt("MaxNodeModuleJsDepth", "maxNodeModuleJsDepth", o => o.MaxNodeModuleJsDepth, (o, v) => o.MaxNodeModuleJsDepth = v),
        Tri("AllowSyntheticDefaultImports", "allowSyntheticDefaultImports", o => o.AllowSyntheticDefaultImports, (o, v) => o.AllowSyntheticDefaultImports = v),
        Tri("AlwaysStrict", "alwaysStrict", o => o.AlwaysStrict, (o, v) => o.AlwaysStrict = v),
        Str("BaseUrl", "baseUrl", o => o.BaseUrl, (o, v) => o.BaseUrl = v),
        Tri("DownlevelIteration", "downlevelIteration", o => o.DownlevelIteration, (o, v) => o.DownlevelIteration = v),
        Tri("ESModuleInterop", "esModuleInterop", o => o.ESModuleInterop, (o, v) => o.ESModuleInterop = v),
        Str("OutFile", "outFile", o => o.OutFile, (o, v) => o.OutFile = v),
        Str("ConfigFilePath", "configFilePath", o => o.ConfigFilePath, (o, v) => o.ConfigFilePath = v),
        Tri("NoDtsResolution", "noDtsResolution", o => o.NoDtsResolution, (o, v) => o.NoDtsResolution = v),
        Str("PathsBasePath", "pathsBasePath", o => o.PathsBasePath, (o, v) => o.PathsBasePath = v),
        Tri("Diagnostics", "diagnostics", o => o.Diagnostics, (o, v) => o.Diagnostics = v),
        Tri("ExtendedDiagnostics", "extendedDiagnostics", o => o.ExtendedDiagnostics, (o, v) => o.ExtendedDiagnostics = v),
        Str("GenerateCpuProfile", "generateCpuProfile", o => o.GenerateCpuProfile, (o, v) => o.GenerateCpuProfile = v),
        Str("GenerateTrace", "generateTrace", o => o.GenerateTrace, (o, v) => o.GenerateTrace = v),
        Tri("ListEmittedFiles", "listEmittedFiles", o => o.ListEmittedFiles, (o, v) => o.ListEmittedFiles = v),
        Tri("ListFiles", "listFiles", o => o.ListFiles, (o, v) => o.ListFiles = v),
        Tri("ExplainFiles", "explainFiles", o => o.ExplainFiles, (o, v) => o.ExplainFiles = v),
        Tri("ListFilesOnly", "listFilesOnly", o => o.ListFilesOnly, (o, v) => o.ListFilesOnly = v),
        Tri("NoEmitForJsFiles", "noEmitForJsFiles", o => o.NoEmitForJsFiles, (o, v) => o.NoEmitForJsFiles = v),
        Tri("PreserveWatchOutput", "preserveWatchOutput", o => o.PreserveWatchOutput, (o, v) => o.PreserveWatchOutput = v),
        Tri("Pretty", "pretty", o => o.Pretty, (o, v) => o.Pretty = v),
        Tri("Version", "version", o => o.Version, (o, v) => o.Version = v),
        Tri("Watch", "watch", o => o.Watch, (o, v) => o.Watch = v),
        Tri("ShowConfig", "showConfig", o => o.ShowConfig, (o, v) => o.ShowConfig = v),
        Tri("Build", "build", o => o.Build, (o, v) => o.Build = v),
        Tri("Help", "help", o => o.Help, (o, v) => o.Help = v),
        Tri("All", "all", o => o.All, (o, v) => o.All = v),
        Str("PprofDir", "pprofDir", o => o.PprofDir, (o, v) => o.PprofDir = v),
        Tri("SingleThreaded", "singleThreaded", o => o.SingleThreaded, (o, v) => o.SingleThreaded = v),
        Tri("Quiet", "quiet", o => o.Quiet, (o, v) => o.Quiet = v),
        IntOpt("Checkers", "checkers", o => o.Checkers, (o, v) => o.Checkers = v),
    };

    // reflect.DeepEqual for the value types that appear on CompilerOptions fields.
    internal static bool DeepEqual(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if (a is string[] sa && b is string[] sb)
        {
            // Go slices.Equal / reflect.DeepEqual over []string → ordinal element compare.
            return sa.AsSpan().SequenceEqual(sb.AsSpan());
        }
        // Tristate, enums, int, string all compare by value here.
        return a.Equals(b);
    }
}

// reflect.DeepEqual(w.config.ParsedConfig, configParseResult.ParsedConfig) — watcher.go:414.
// Hand-written per-field deep equality over core.ParsedOptions, reusing the CompilerOptionsFields
// table for the options struct (the same table that stands in for reflection elsewhere).
public static class ParsedOptionsDeepEqual
{
    public static bool DeepEqual(ParsedOptions? a, ParsedOptions? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null)
        {
            return false;
        }
        return CompilerOptionsDeepEqual(a.CompilerOptions, b.CompilerOptions) &&
            WatchOptionsDeepEqual(a.WatchOptions, b.WatchOptions) &&
            TypeAcquisitionDeepEqual(a.TypeAcquisition, b.TypeAcquisition) &&
            StringsEqual(a.FileNames, b.FileNames) &&
            ProjectReferencesEqual(a.ProjectReferences, b.ProjectReferences);
    }

    private static bool CompilerOptionsDeepEqual(CompilerOptions? a, CompilerOptions? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null)
        {
            return false;
        }
        foreach (var field in CompilerOptionsFields.Fields)
        {
            if (!CompilerOptionsFields.DeepEqual(field.Get(a), field.Get(b)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool WatchOptionsDeepEqual(WatchOptions? a, WatchOptions? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null)
        {
            return false;
        }
        return a.Interval == b.Interval &&
            a.FileKind == b.FileKind &&
            a.DirectoryKind == b.DirectoryKind &&
            a.FallbackPolling == b.FallbackPolling &&
            a.SyncWatchDir == b.SyncWatchDir &&
            StringsEqual(a.ExcludeDir, b.ExcludeDir) &&
            StringsEqual(a.ExcludeFiles, b.ExcludeFiles);
    }

    private static bool TypeAcquisitionDeepEqual(TypeAcquisition? a, TypeAcquisition? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null)
        {
            return false;
        }
        return a.Enable == b.Enable &&
            StringsEqual(a.Include, b.Include) &&
            StringsEqual(a.Exclude, b.Exclude) &&
            a.DisableFilenameBasedTypeAcquisition == b.DisableFilenameBasedTypeAcquisition;
    }

    private static bool StringsEqual(string[]? a, string[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null)
        {
            return false;
        }
        return a.AsSpan().SequenceEqual(b);
    }

    private static bool ProjectReferencesEqual(ProjectReference[]? a, ProjectReference[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }
        if (a == null || b == null || a.Length != b.Length)
        {
            return false;
        }
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i].Path != b[i].Path || a[i].OriginalPath != b[i].OriginalPath || a[i].Circular != b[i].Circular)
            {
                return false;
            }
        }
        return true;
    }
}
