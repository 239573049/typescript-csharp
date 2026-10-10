// Ported from tsgo internal/tsoptions/parsinghelpers.go

using System;
using System.Collections.Generic;
using Ts.Ast;
using Ts.Collections;
using Ts.Core;
using Message = Ts.Diagnostics.Message;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.TsOptions;

public static class ParsingHelpers
{
    public static Tristate ParseTristate(object? value)
    {
        if (value is null)
        {
            return Tristate.Unknown;
        }
        if (value is Tristate v)
        {
            return v;
        }
        if (value is true)
        {
            return Tristate.True;
        }
        else
        {
            return Tristate.False;
        }
    }

    public static string[]? ParseStringArray(object? value)
    {
        if (value is object?[] arr)
        {
            var result = new List<string>(arr.Length);
            foreach (var v in arr)
            {
                if (v is string str)
                {
                    result.Add(str);
                }
            }
            return result.ToArray();
        }
        return null;
    }

    internal static OrderedMap<string, string[]>? parseStringMap(object? value)
    {
        if (value is OrderedMap<string, object?> m)
        {
            var result = new OrderedMap<string, string[]>(m.Size());
            // map-iter: 已排序 —— OrderedMap 保序，逐键复制
            foreach (var (k, v) in m.Entries())
            {
                result.Set(k, ParseStringArray(v)!);
            }
            return result;
        }
        return null;
    }

    public static string ParseString(object? value)
    {
        if (value is string str)
        {
            return str;
        }
        return "";
    }

    internal static int? parseNumber(object? value)
    {
        if (value is int num)
        {
            return num;
        }
        if (value is double d)
        {
            return (int)d;
        }
        return null;
    }

    internal sealed class projectReferenceParseResult
    {
        public ProjectReference reference = new();
        public bool hasPath;
        public bool pathValid;
        public bool hasCircular;
        public bool circularValid;
    }

    internal static projectReferenceParseResult? parseProjectReference(object? json)
    {
        if (json is OrderedMap<string, object?> v)
        {
            var result = new projectReferenceParseResult();
            if (v.TryGet("path", out var value))
            {
                result.hasPath = true;
                if (value is string path)
                {
                    result.reference.Path = path;
                    result.pathValid = true;
                }
            }
            if (v.TryGet("circular", out var value2))
            {
                result.hasCircular = true;
                if (value2 is bool circular)
                {
                    result.reference.Circular = circular;
                    result.circularValid = true;
                }
            }
            return result;
        }
        return null;
    }

    internal static OrderedMap<string, object?> parseJsonToStringKey(object? json)
    {
        var result = new OrderedMap<string, object?>(6);
        if (json is OrderedMap<string, object?> m)
        {
            if (m.TryGet("include", out var v1))
            {
                result.Set("include", v1);
            }
            if (m.TryGet("exclude", out var v2))
            {
                result.Set("exclude", v2);
            }
            if (m.TryGet("files", out var v3))
            {
                result.Set("files", v3);
            }
            if (m.TryGet("references", out var v4))
            {
                result.Set("references", v4);
            }
            if (m.TryGet("extends", out var v5))
            {
                if (v5 is string str)
                {
                    // Go: parsinghelpers.go:118-123 — a scalar extends is wrapped into a one-element
                    // list and then, because the Go code does not `continue`, overwritten by the
                    // scalar itself (Set calls p.optsMap.Delete(key) first). Kept verbatim.
                    result.Set("extends", new object?[] { str });
                }
                result.Set("extends", v5);
            }
            if (m.TryGet("compilerOptions", out var v6))
            {
                result.Set("compilerOptions", v6);
            }
            if (m.TryGet("excludes", out var v7))
            {
                result.Set("excludes", v7);
            }
            if (m.TryGet("typeAcquisition", out var v8))
            {
                result.Set("typeAcquisition", v8);
            }
        }
        return result;
    }

    public static Diagnostic[]? ParseCompilerOptions(string key, object? value, CompilerOptions? allOptions)
    {
        if (value is null)
        {
            return null;
        }
        if (allOptions is null)
        {
            return null;
        }
        parseCompilerOptions(key, value, allOptions);
        return null;
    }

    internal static bool parseCompilerOptions(string key, object? value, CompilerOptions allOptions)
    {
        var option = TsConfigParsing.CommandLineCompilerOptionsMap.Get(key);
        if (option != null)
        {
            key = option.Name;
        }
        switch (key)
        {
            case "allowJs":
                allOptions.AllowJs = ParseTristate(value);
                break;
            case "allowImportingTsExtensions":
                allOptions.AllowImportingTsExtensions = ParseTristate(value);
                break;
            case "allowSyntheticDefaultImports":
                allOptions.AllowSyntheticDefaultImports = ParseTristate(value);
                break;
            case "allowNonTsExtensions":
                allOptions.AllowNonTsExtensions = ParseTristate(value);
                break;
            case "allowUmdGlobalAccess":
                allOptions.AllowUmdGlobalAccess = ParseTristate(value);
                break;
            case "allowUnreachableCode":
                allOptions.AllowUnreachableCode = ParseTristate(value);
                break;
            case "allowUnusedLabels":
                allOptions.AllowUnusedLabels = ParseTristate(value);
                break;
            case "allowArbitraryExtensions":
                allOptions.AllowArbitraryExtensions = ParseTristate(value);
                break;
            case "alwaysStrict":
                allOptions.AlwaysStrict = ParseTristate(value);
                break;
            case "assumeChangesOnlyAffectDirectDependencies":
                allOptions.AssumeChangesOnlyAffectDirectDependencies = ParseTristate(value);
                break;
            case "baseUrl":
                allOptions.BaseUrl = ParseString(value);
                break;
            case "build":
                allOptions.Build = ParseTristate(value);
                break;
            case "checkJs":
                allOptions.CheckJs = ParseTristate(value);
                break;
            case "customConditions":
                allOptions.CustomConditions = ParseStringArray(value);
                break;
            case "composite":
                allOptions.Composite = ParseTristate(value);
                break;
            case "declarationDir":
                allOptions.DeclarationDir = ParseString(value);
                break;
            case "deduplicatePackages":
                allOptions.DeduplicatePackages = ParseTristate(value);
                break;
            case "diagnostics":
                allOptions.Diagnostics = ParseTristate(value);
                break;
            case "disableSizeLimit":
                allOptions.DisableSizeLimit = ParseTristate(value);
                break;
            case "disableSourceOfProjectReferenceRedirect":
                allOptions.DisableSourceOfProjectReferenceRedirect = ParseTristate(value);
                break;
            case "disableSolutionSearching":
                allOptions.DisableSolutionSearching = ParseTristate(value);
                break;
            case "disableReferencedProjectLoad":
                allOptions.DisableReferencedProjectLoad = ParseTristate(value);
                break;
            case "declarationMap":
                allOptions.DeclarationMap = ParseTristate(value);
                break;
            case "declaration":
                allOptions.Declaration = ParseTristate(value);
                break;
            case "downlevelIteration":
                allOptions.DownlevelIteration = ParseTristate(value);
                break;
            case "erasableSyntaxOnly":
                allOptions.ErasableSyntaxOnly = ParseTristate(value);
                break;
            case "emitDeclarationOnly":
                allOptions.EmitDeclarationOnly = ParseTristate(value);
                break;
            case "extendedDiagnostics":
                allOptions.ExtendedDiagnostics = ParseTristate(value);
                break;
            case "emitDecoratorMetadata":
                allOptions.EmitDecoratorMetadata = ParseTristate(value);
                break;
            case "emitBOM":
                allOptions.EmitBOM = ParseTristate(value);
                break;
            case "esModuleInterop":
                allOptions.ESModuleInterop = ParseTristate(value);
                break;
            case "exactOptionalPropertyTypes":
                allOptions.ExactOptionalPropertyTypes = ParseTristate(value);
                break;
            case "explainFiles":
                allOptions.ExplainFiles = ParseTristate(value);
                break;
            case "experimentalDecorators":
                allOptions.ExperimentalDecorators = ParseTristate(value);
                break;
            case "forceConsistentCasingInFileNames":
                allOptions.ForceConsistentCasingInFileNames = ParseTristate(value);
                break;
            case "generateCpuProfile":
                allOptions.GenerateCpuProfile = ParseString(value);
                break;
            case "generateTrace":
                allOptions.GenerateTrace = ParseString(value);
                break;
            case "isolatedModules":
                allOptions.IsolatedModules = ParseTristate(value);
                break;
            case "ignoreConfig":
                allOptions.IgnoreConfig = ParseTristate(value);
                break;
            case "ignoreDeprecations":
                allOptions.IgnoreDeprecations = ParseString(value);
                break;
            case "importHelpers":
                allOptions.ImportHelpers = ParseTristate(value);
                break;
            case "incremental":
                allOptions.Incremental = ParseTristate(value);
                break;
            case "init":
                allOptions.Init = ParseTristate(value);
                break;
            case "inlineSourceMap":
                allOptions.InlineSourceMap = ParseTristate(value);
                break;
            case "inlineSources":
                allOptions.InlineSources = ParseTristate(value);
                break;
            case "isolatedDeclarations":
                allOptions.IsolatedDeclarations = ParseTristate(value);
                break;
            case "jsx":
                allOptions.Jsx = floatOrInt32ToFlag<JsxEmit>(value);
                break;
            case "jsxFactory":
                allOptions.JsxFactory = ParseString(value);
                break;
            case "jsxFragmentFactory":
                allOptions.JsxFragmentFactory = ParseString(value);
                break;
            case "jsxImportSource":
                allOptions.JsxImportSource = ParseString(value);
                break;
            case "lib":
                if (value is string[] libArr)
                {
                    allOptions.Lib = libArr;
                }
                else
                {
                    allOptions.Lib = ParseStringArray(value);
                }
                break;
            case "libReplacement":
                allOptions.LibReplacement = ParseTristate(value);
                break;
            case "listEmittedFiles":
                allOptions.ListEmittedFiles = ParseTristate(value);
                break;
            case "listFiles":
                allOptions.ListFiles = ParseTristate(value);
                break;
            case "listFilesOnly":
                allOptions.ListFilesOnly = ParseTristate(value);
                break;
            case "locale":
                allOptions.Locale = ParseString(value);
                break;
            case "mapRoot":
                allOptions.MapRoot = ParseString(value);
                break;
            case "module":
                allOptions.Module = floatOrInt32ToFlag<ModuleKind>(value);
                break;
            case "moduleDetectionKind":
                allOptions.ModuleDetection = floatOrInt32ToFlag<ModuleDetectionKind>(value);
                break;
            case "moduleResolution":
                allOptions.ModuleResolution = floatOrInt32ToFlag<ModuleResolutionKind>(value);
                break;
            case "moduleSuffixes":
                allOptions.ModuleSuffixes = ParseStringArray(value);
                break;
            case "moduleDetection":
                allOptions.ModuleDetection = floatOrInt32ToFlag<ModuleDetectionKind>(value);
                break;
            case "noCheck":
                allOptions.NoCheck = ParseTristate(value);
                break;
            case "noFallthroughCasesInSwitch":
                allOptions.NoFallthroughCasesInSwitch = ParseTristate(value);
                break;
            case "noEmitForJsFiles":
                allOptions.NoEmitForJsFiles = ParseTristate(value);
                break;
            case "noErrorTruncation":
                allOptions.NoErrorTruncation = ParseTristate(value);
                break;
            case "noImplicitAny":
                allOptions.NoImplicitAny = ParseTristate(value);
                break;
            case "noImplicitThis":
                allOptions.NoImplicitThis = ParseTristate(value);
                break;
            case "noLib":
                allOptions.NoLib = ParseTristate(value);
                break;
            case "noPropertyAccessFromIndexSignature":
                allOptions.NoPropertyAccessFromIndexSignature = ParseTristate(value);
                break;
            case "noUncheckedIndexedAccess":
                allOptions.NoUncheckedIndexedAccess = ParseTristate(value);
                break;
            case "noEmitHelpers":
                allOptions.NoEmitHelpers = ParseTristate(value);
                break;
            case "noEmitOnError":
                allOptions.NoEmitOnError = ParseTristate(value);
                break;
            case "noImplicitReturns":
                allOptions.NoImplicitReturns = ParseTristate(value);
                break;
            case "noUnusedLocals":
                allOptions.NoUnusedLocals = ParseTristate(value);
                break;
            case "noUnusedParameters":
                allOptions.NoUnusedParameters = ParseTristate(value);
                break;
            case "noImplicitOverride":
                allOptions.NoImplicitOverride = ParseTristate(value);
                break;
            case "noUncheckedSideEffectImports":
                allOptions.NoUncheckedSideEffectImports = ParseTristate(value);
                break;
            case "outFile":
                allOptions.OutFile = ParseString(value);
                break;
            case "noResolve":
                allOptions.NoResolve = ParseTristate(value);
                break;
            case "paths":
                allOptions.Paths = parseStringMap(value);
                break;
            case "preserveWatchOutput":
                allOptions.PreserveWatchOutput = ParseTristate(value);
                break;
            case "preserveConstEnums":
                allOptions.PreserveConstEnums = ParseTristate(value);
                break;
            case "preserveSymlinks":
                allOptions.PreserveSymlinks = ParseTristate(value);
                break;
            case "project":
                allOptions.Project = ParseString(value);
                break;
            case "pretty":
                allOptions.Pretty = ParseTristate(value);
                break;
            case "resolveJsonModule":
                allOptions.ResolveJsonModule = ParseTristate(value);
                break;
            case "resolvePackageJsonExports":
                allOptions.ResolvePackageJsonExports = ParseTristate(value);
                break;
            case "resolvePackageJsonImports":
                allOptions.ResolvePackageJsonImports = ParseTristate(value);
                break;
            case "reactNamespace":
                allOptions.ReactNamespace = ParseString(value);
                break;
            case "rewriteRelativeImportExtensions":
                allOptions.RewriteRelativeImportExtensions = ParseTristate(value);
                break;
            case "rootDir":
                allOptions.RootDir = ParseString(value);
                break;
            case "rootDirs":
                allOptions.RootDirs = ParseStringArray(value);
                break;
            case "removeComments":
                allOptions.RemoveComments = ParseTristate(value);
                break;
            case "stableTypeOrdering":
                allOptions.StableTypeOrdering = ParseTristate(value);
                break;
            case "strict":
                allOptions.Strict = ParseTristate(value);
                break;
            case "strictBindCallApply":
                allOptions.StrictBindCallApply = ParseTristate(value);
                break;
            case "strictBuiltinIteratorReturn":
                allOptions.StrictBuiltinIteratorReturn = ParseTristate(value);
                break;
            case "strictFunctionTypes":
                allOptions.StrictFunctionTypes = ParseTristate(value);
                break;
            case "strictNullChecks":
                allOptions.StrictNullChecks = ParseTristate(value);
                break;
            case "strictPropertyInitialization":
                allOptions.StrictPropertyInitialization = ParseTristate(value);
                break;
            case "skipDefaultLibCheck":
                allOptions.SkipDefaultLibCheck = ParseTristate(value);
                break;
            case "sourceMap":
                allOptions.SourceMap = ParseTristate(value);
                break;
            case "sourceRoot":
                allOptions.SourceRoot = ParseString(value);
                break;
            case "stripInternal":
                allOptions.StripInternal = ParseTristate(value);
                break;
            case "suppressOutputPathCheck":
                allOptions.SuppressOutputPathCheck = ParseTristate(value);
                break;
            case "target":
                allOptions.Target = floatOrInt32ToFlag<ScriptTarget>(value);
                break;
            case "traceResolution":
                allOptions.TraceResolution = ParseTristate(value);
                break;
            case "tsBuildInfoFile":
                allOptions.TsBuildInfoFile = ParseString(value);
                break;
            case "typeRoots":
                allOptions.TypeRoots = ParseStringArray(value);
                break;
            case "types":
                allOptions.Types = ParseStringArray(value);
                break;
            case "useDefineForClassFields":
                allOptions.UseDefineForClassFields = ParseTristate(value);
                break;
            case "useUnknownInCatchVariables":
                allOptions.UseUnknownInCatchVariables = ParseTristate(value);
                break;
            case "verbatimModuleSyntax":
                allOptions.VerbatimModuleSyntax = ParseTristate(value);
                break;
            case "version":
                allOptions.Version = ParseTristate(value);
                break;
            case "help":
                allOptions.Help = ParseTristate(value);
                break;
            case "all":
                allOptions.All = ParseTristate(value);
                break;
            case "maxNodeModuleJsDepth":
                allOptions.MaxNodeModuleJsDepth = parseNumber(value);
                break;
            case "skipLibCheck":
                allOptions.SkipLibCheck = ParseTristate(value);
                break;
            case "noEmit":
                allOptions.NoEmit = ParseTristate(value);
                break;
            case "showConfig":
                allOptions.ShowConfig = ParseTristate(value);
                break;
            case "configFilePath":
                allOptions.ConfigFilePath = ParseString(value);
                break;
            case "noDtsResolution":
                allOptions.NoDtsResolution = ParseTristate(value);
                break;
            case "pathsBasePath":
                allOptions.PathsBasePath = ParseString(value);
                break;
            case "outDir":
                allOptions.OutDir = ParseString(value);
                break;
            case "newLine":
                allOptions.NewLine = floatOrInt32ToFlag<NewLineKind>(value);
                break;
            case "watch":
                allOptions.Watch = ParseTristate(value);
                break;
            case "pprofDir":
                allOptions.PprofDir = ParseString(value);
                break;
            case "singleThreaded":
                allOptions.SingleThreaded = ParseTristate(value);
                break;
            case "quiet":
                allOptions.Quiet = ParseTristate(value);
                break;
            case "checkers":
                allOptions.Checkers = parseNumber(value);
                break;
            default:
                // different than any key above
                return false;
        }
        return true;
    }

    // Go: func floatOrInt32ToFlag[T ~int32](value any) T. The value is either the already
    // resolved enum (boxed T, via the enum map) or a JSON number (boxed float64).
    private static T floatOrInt32ToFlag<T>(object? value) where T : struct, Enum
    {
        if (value is T v)
        {
            return v;
        }
        // Go: T(value.(float64)). All these enums are int32-backed, so bit-cast the truncated int.
        return System.Runtime.CompilerServices.Unsafe.BitCast<int, T>((int)(double)value!);
    }

    public static Diagnostic[]? ParseWatchOptions(string key, object? value, WatchOptions? allOptions)
    {
        if (allOptions is null)
        {
            return null;
        }
        switch (key)
        {
            case "watchInterval":
                allOptions.Interval = parseNumber(value);
                break;
            case "watchFile":
                if (value != null)
                {
                    allOptions.FileKind = (WatchFileKind)value;
                }
                break;
            case "watchDirectory":
                if (value != null)
                {
                    allOptions.DirectoryKind = (WatchDirectoryKind)value;
                }
                break;
            case "fallbackPolling":
                if (value != null)
                {
                    allOptions.FallbackPolling = (PollingKind)value;
                }
                break;
            case "synchronousWatchDirectory":
                allOptions.SyncWatchDir = ParseTristate(value);
                break;
            case "excludeDirectories":
                allOptions.ExcludeDir = ParseStringArray(value);
                break;
            case "excludeFiles":
                allOptions.ExcludeFiles = ParseStringArray(value);
                break;
        }
        return null;
    }

    public static Diagnostic[]? ParseTypeAcquisition(string key, object? value, TypeAcquisition? allOptions)
    {
        if (value is null)
        {
            return null;
        }
        if (allOptions is null)
        {
            return null;
        }
        switch (key)
        {
            case "enable":
                allOptions.Enable = ParseTristate(value);
                break;
            case "include":
                allOptions.Include = ParseStringArray(value);
                break;
            case "exclude":
                allOptions.Exclude = ParseStringArray(value);
                break;
            case "disableFilenameBasedTypeAcquisition":
                allOptions.DisableFilenameBasedTypeAcquisition = ParseTristate(value);
                break;
        }
        return null;
    }

    public static Diagnostic[]? ParseBuildOptions(string key, object? value, BuildOptions? allOptions)
    {
        if (value is null)
        {
            return null;
        }
        if (allOptions is null)
        {
            return null;
        }
        var option = NameMaps.BuildNameMap.Get(key);
        if (option != null)
        {
            key = option.Name;
        }
        switch (key)
        {
            case "clean":
                allOptions.Clean = ParseTristate(value);
                break;
            case "dry":
                allOptions.Dry = ParseTristate(value);
                break;
            case "force":
                allOptions.Force = ParseTristate(value);
                break;
            case "builders":
                allOptions.Builders = parseNumber(value);
                break;
            case "stopBuildOnErrors":
                allOptions.StopBuildOnErrors = ParseTristate(value);
                break;
            case "verbose":
                allOptions.Verbose = ParseTristate(value);
                break;
        }
        return null;
    }

    // mergeCompilerOptions merges the source compiler options into the target compiler options
    // with optional awareness of explicitly set null values in the raw JSON.
    //
    // PORT-NOTE: Go walks fields via reflection (parsinghelpers.go:608-628). Reflection is
    // AOT-banned (plan/07 §1.1); the per-field table CompilerOptionsFields is the equivalent.
    // The json tag name for each field is CompilerOptionField.JsonName.
    internal static CompilerOptions mergeCompilerOptions(CompilerOptions targetOptions, CompilerOptions? sourceOptions, object? rawSource)
    {
        if (sourceOptions is null)
        {
            return targetOptions;
        }

        // Collect explicitly null field names from raw JSON
        var explicitNullFields = new HashSet<string>(StringComparer.Ordinal);
        if (rawSource != null)
        {
            // Options are nested under "compilerOptions" in both tsconfig.json and wrapped command line options
            if (rawSource is OrderedMap<string, object?> rawMap && rawMap.TryGet("compilerOptions", out var compilerOptionsRaw))
            {
                if (compilerOptionsRaw is OrderedMap<string, object?> compilerOptionsMap)
                {
                    // map-iter: 序无关 —— 收集 null 值键的集合，与顺序无关
                    foreach (var (key, value) in compilerOptionsMap.Entries())
                    {
                        if (value is null)
                        {
                            explicitNullFields.Add(key);
                        }
                    }
                }
            }
        }

        // Do the merge, handling explicit nulls during the normal merge
        foreach (var field in CompilerOptionsFields.Fields)
        {
            if (explicitNullFields.Contains(field.JsonName))
            {
                field.SetZero(targetOptions);
                continue;
            }

            // Normal merge behavior: copy non-zero fields
            if (!field.IsZero(sourceOptions))
            {
                field.Copy(sourceOptions, targetOptions);
            }
        }

        return targetOptions;
    }

    internal static OrderedMap<string, object?>? convertToOptionsWithAbsolutePaths(OrderedMap<string, object?>? optionsBase, CommandLineOptionNameMap optionMap, string cwd)
    {
        // !!! convert to options with absolute paths was previously done with `CompilerOptions` object, but for ease of implementation, we do it pre-conversion.
        // !!! Revisit this choice if/when refactoring when conversion is done in tsconfig parsing
        if (optionsBase is null)
        {
            return null;
        }
        // map-iter: 序无关 —— 逐键就地替换为绝对路径，与顺序无关（仅更新既有键值）
        foreach (var (o, v) in optionsBase.Entries())
        {
            if (TryConvertOptionToAbsolutePath(o, v, optionMap, cwd, out var result))
            {
                optionsBase.Set(o, result);
            }
        }
        return optionsBase;
    }

    // Go: func ConvertOptionToAbsolutePath(...) (any, bool). (T, ok) → Try (05 §4.1).
    public static bool TryConvertOptionToAbsolutePath(string o, object? v, CommandLineOptionNameMap optionMap, string cwd, out object? result)
    {
        var option = optionMap.Get(o);
        if (option is null)
        {
            result = null;
            return false;
        }
        if (option.Kind == "list")
        {
            if (option.Elements()!.IsFilePath)
            {
                if (v is string[] arr)
                {
                    result = CoreUtil.Map(arr, item => TsPathApi.GetNormalizedAbsolutePath(item, cwd));
                    return true;
                }
                if (v is object?[] arrAny)
                {
                    result = CoreUtil.Map(arrAny, item =>
                    {
                        if (item is string s)
                        {
                            return (object?)TsPathApi.GetNormalizedAbsolutePath(s, cwd);
                        }
                        return item;
                    });
                    return true;
                }
            }
        }
        else if (option.IsFilePath)
        {
            if (v is string value)
            {
                result = TsPathApi.GetNormalizedAbsolutePath(value, cwd);
                return true;
            }
        }
        result = null;
        return false;
    }
}

// optionParser polymorphic interface + the four concrete parsers (parsinghelpers.go).
internal interface optionParser
{
    Diagnostic[]? ParseOption(string key, object? value);
    Message? UnknownOptionDiagnostic();
    Message? UnknownDidYouMeanDiagnostic();
}

internal sealed class compilerOptionsParser : optionParser
{
    public CompilerOptions CompilerOptions;

    public compilerOptionsParser(CompilerOptions o)
    {
        CompilerOptions = o;
    }

    public Diagnostic[]? ParseOption(string key, object? value) => ParsingHelpers.ParseCompilerOptions(key, value, CompilerOptions);

    public Message? UnknownOptionDiagnostic() => Errors.extraKeyDiagnostics("compilerOptions");

    public Message? UnknownDidYouMeanDiagnostic() => Errors.extraKeyDidYouMeanDiagnostics("compilerOptions");
}

internal sealed class watchOptionsParser : optionParser
{
    public WatchOptions WatchOptions;

    public watchOptionsParser(WatchOptions o)
    {
        WatchOptions = o;
    }

    public Diagnostic[]? ParseOption(string key, object? value) => ParsingHelpers.ParseWatchOptions(key, value, WatchOptions);

    public Message? UnknownOptionDiagnostic() => Errors.extraKeyDiagnostics("watchOptions");

    public Message? UnknownDidYouMeanDiagnostic() => Errors.extraKeyDidYouMeanDiagnostics("watchOptions");
}

internal sealed class typeAcquisitionParser : optionParser
{
    public TypeAcquisition TypeAcquisition;

    public typeAcquisitionParser(TypeAcquisition o)
    {
        TypeAcquisition = o;
    }

    public Diagnostic[]? ParseOption(string key, object? value) => ParsingHelpers.ParseTypeAcquisition(key, value, TypeAcquisition);

    public Message? UnknownOptionDiagnostic() => Errors.extraKeyDiagnostics("typeAcquisition");

    public Message? UnknownDidYouMeanDiagnostic() => Errors.extraKeyDidYouMeanDiagnostics("typeAcquisition");
}

internal sealed class buildOptionsParser : optionParser
{
    public BuildOptions BuildOptions;

    public buildOptionsParser(BuildOptions o)
    {
        BuildOptions = o;
    }

    public Diagnostic[]? ParseOption(string key, object? value) => ParsingHelpers.ParseBuildOptions(key, value, BuildOptions);

    public Message? UnknownOptionDiagnostic() => Errors.extraKeyDiagnostics("buildOptions");

    public Message? UnknownDidYouMeanDiagnostic() => Errors.extraKeyDidYouMeanDiagnostics("buildOptions");
}
