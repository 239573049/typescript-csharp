// Ported from tsgo internal/core/compileroptions.go (commit 168e7015edf98244febc8f4ae450b673b5d195d7)

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Ts.Collections;
using TsPathApi = Ts.TsPath.TsPath; // Go: tspath.* (a plain `using` collides with the sibling Ts.TsPath namespace)

namespace Ts.Core;

// Go emits stringers for ModuleKind and ScriptTarget via `//go:generate stringer`
// (compileroptions.go:12-14). Those are codegen family 6 (generate-stringers.ts,
// plan/04 §7) and are deferred until a baseline path needs them, so no C# stringer
// is produced here this wave. The hand-written String() methods on ModuleResolutionKind
// and JsxEmit (which Go deliberately keeps out of the stringer generator) are ported
// below as ToDisplayString().

// Keep in sync with the API's compilerOptions.ts
//
// PORT-NOTE: Field declaration order is load-bearing — it is the .tsbuildinfo key
// order (plan/07 §4.2) and the iteration order of the per-field switch the phase-4
// generator will emit (CompilerOptions.ForEachValue/MergeFrom, plan/07 §1.1). Do not
// reorder. Go's `_ noCopy` vet sentinel is dropped (C# classes are reference types).
// Exported Go fields become public C# fields (not properties) per 05-conventions.md
// §1.1; string fields are initialized to "" to match Go's zero value (getters below
// compare against "", e.g. ConfigFilePath, PathsBasePath). JSON struct tags are not
// carried: this family's wire surface is taken over by the generated tsoptions switch
// and hand-written Utf8JsonWriter emitters (plan/07 §4), not reflection serialization.
public sealed class CompilerOptions
{
    public Tristate AllowJs;
    public Tristate AllowArbitraryExtensions;
    public Tristate AllowImportingTsExtensions;
    public Tristate AllowNonTsExtensions;
    public Tristate AllowUmdGlobalAccess;
    public Tristate AllowUnreachableCode;
    public Tristate AllowUnusedLabels;
    public Tristate AssumeChangesOnlyAffectDirectDependencies;
    public Tristate CheckJs;
    public string[]? CustomConditions;
    public Tristate Composite;
    public Tristate EmitDeclarationOnly;
    public Tristate EmitBOM;
    public Tristate EmitDecoratorMetadata;
    public Tristate Declaration;
    public string DeclarationDir = "";
    public Tristate DeclarationMap;
    public Tristate DeduplicatePackages;
    public Tristate DisableSizeLimit;
    public Tristate DisableSourceOfProjectReferenceRedirect;
    public Tristate DisableSolutionSearching;
    public Tristate DisableReferencedProjectLoad;
    public Tristate ErasableSyntaxOnly;
    public Tristate ExactOptionalPropertyTypes;
    public Tristate ExperimentalDecorators;
    public Tristate ForceConsistentCasingInFileNames;
    public Tristate IsolatedModules;
    public Tristate IsolatedDeclarations;
    public Tristate IgnoreConfig;
    public string IgnoreDeprecations = "";
    public Tristate ImportHelpers;
    public Tristate InlineSourceMap;
    public Tristate InlineSources;
    public Tristate Init;
    public Tristate Incremental;
    public JsxEmit Jsx;
    public string JsxFactory = "";
    public string JsxFragmentFactory = "";
    public string JsxImportSource = "";
    public string[]? Lib;
    public Tristate LibReplacement;
    public string Locale = "";
    public string MapRoot = "";
    public ModuleKind Module;
    public ModuleResolutionKind ModuleResolution;
    public string[]? ModuleSuffixes;
    public ModuleDetectionKind ModuleDetection;
    public NewLineKind NewLine;
    public Tristate NoEmit;
    public Tristate NoCheck;
    public Tristate NoErrorTruncation;
    public Tristate NoFallthroughCasesInSwitch;
    public Tristate NoImplicitAny;
    public Tristate NoImplicitThis;
    public Tristate NoImplicitReturns;
    public Tristate NoEmitHelpers;
    public Tristate NoLib;
    public Tristate NoPropertyAccessFromIndexSignature;
    public Tristate NoUncheckedIndexedAccess;
    public Tristate NoEmitOnError;
    public Tristate NoUnusedLocals;
    public Tristate NoUnusedParameters;
    public Tristate NoResolve;
    public Tristate NoImplicitOverride;
    public Tristate NoUncheckedSideEffectImports;
    public string OutDir = "";
    public OrderedMap<string, string[]>? Paths;
    public Tristate PreserveConstEnums;
    public Tristate PreserveSymlinks;
    public string Project = "";
    public Tristate ResolveJsonModule;
    public Tristate ResolvePackageJsonExports;
    public Tristate ResolvePackageJsonImports;
    public Tristate RemoveComments;
    public Tristate RewriteRelativeImportExtensions;
    public string ReactNamespace = "";
    public string RootDir = "";
    public string[]? RootDirs;
    public Tristate SkipLibCheck;
    public Tristate StableTypeOrdering;
    public Tristate Strict;
    public Tristate StrictBindCallApply;
    public Tristate StrictBuiltinIteratorReturn;
    public Tristate StrictFunctionTypes;
    public Tristate StrictNullChecks;
    public Tristate StrictPropertyInitialization;
    public Tristate StripInternal;
    public Tristate SkipDefaultLibCheck;
    public Tristate SourceMap;
    public string SourceRoot = "";
    public Tristate SuppressOutputPathCheck;
    public ScriptTarget Target;
    public Tristate TraceResolution;
    public string TsBuildInfoFile = "";
    public string[]? TypeRoots;
    public string[]? Types;
    public Tristate UseDefineForClassFields;
    public Tristate UseUnknownInCatchVariables;
    public Tristate VerbatimModuleSyntax;
    public int? MaxNodeModuleJsDepth;

    // Deprecated: Do not use outside of options parsing and validation.
    public Tristate AllowSyntheticDefaultImports;
    // Deprecated: Do not use outside of options parsing and validation.
    public Tristate AlwaysStrict;
    // Deprecated: Do not use outside of options parsing and validation.
    public string BaseUrl = "";
    // Deprecated: Do not use outside of options parsing and validation.
    public Tristate DownlevelIteration;
    // Deprecated: Do not use outside of options parsing and validation.
    public Tristate ESModuleInterop;
    // Deprecated: Do not use outside of options parsing and validation.
    public string OutFile = "";

    // Internal fields
    public string ConfigFilePath = "";
    public Tristate NoDtsResolution;
    public string PathsBasePath = "";
    public Tristate Diagnostics;
    public Tristate ExtendedDiagnostics;
    public string GenerateCpuProfile = "";
    public string GenerateTrace = "";
    public Tristate ListEmittedFiles;
    public Tristate ListFiles;
    public Tristate ExplainFiles;
    public Tristate ListFilesOnly;
    public Tristate NoEmitForJsFiles;
    public Tristate PreserveWatchOutput;
    public Tristate Pretty;
    public Tristate Version;
    public Tristate Watch;
    public Tristate ShowConfig;
    public Tristate Build;
    public Tristate Help;
    public Tristate All;

    public string PprofDir = "";
    public Tristate SingleThreaded;
    public Tristate Quiet;
    public int? Checkers;

    // Clone creates a shallow copy of the CompilerOptions.
    //
    // PORT-NOTE: Go clones every exported field via reflection (compileroptions.go:179-193,
    // whose own comment notes "this could be generated code instead of reflection").
    // Reflection is AOT-banned (plan/07 §1.1), so this is an equivalent shallow bitwise
    // copy: value fields are copied by value; reference fields (string[], OrderedMap) share
    // the source instance, matching Go's field-by-field Set. There are no unexported data
    // fields, so MemberwiseClone reproduces the Go behavior exactly. A generated per-field
    // Clone supersedes this in phase 4.
    public CompilerOptions Clone()
    {
        return (CompilerOptions)MemberwiseClone();
    }

    public ScriptTarget GetEmitScriptTarget()
    {
        if (Target != ScriptTarget.None)
        {
            return Target;
        }
        return ScriptTarget.LatestStandard;
    }

    public ModuleKind GetEmitModuleKind()
    {
        if (Module != ModuleKind.None)
        {
            return Module;
        }

        var target = GetEmitScriptTarget();
        if (target == ScriptTarget.ESNext)
        {
            return ModuleKind.ESNext;
        }
        if (target >= ScriptTarget.ES2022)
        {
            return ModuleKind.ES2022;
        }
        if (target >= ScriptTarget.ES2020)
        {
            return ModuleKind.ES2020;
        }
        if (target >= ScriptTarget.ES2015)
        {
            return ModuleKind.ES2015;
        }
        return ModuleKind.CommonJS;
    }

    public ModuleResolutionKind GetModuleResolutionKind()
    {
        switch (ModuleResolution)
        {
            case ModuleResolutionKind.Unknown:
            case ModuleResolutionKind.Classic:
            case ModuleResolutionKind.Node10:
                switch (GetEmitModuleKind())
                {
                    case ModuleKind.Node16:
                    case ModuleKind.Node18:
                    case ModuleKind.Node20:
                        return ModuleResolutionKind.Node16;
                    case ModuleKind.NodeNext:
                        return ModuleResolutionKind.NodeNext;
                    default:
                        return ModuleResolutionKind.Bundler;
                }
            default:
                return ModuleResolution;
        }
    }

    public ModuleDetectionKind GetEmitModuleDetectionKind()
    {
        if (ModuleDetection != ModuleDetectionKind.None)
        {
            return ModuleDetection;
        }
        var moduleKind = GetEmitModuleKind();
        if (ModuleKind.Node16 <= moduleKind && moduleKind <= ModuleKind.NodeNext)
        {
            return ModuleDetectionKind.Force;
        }
        return ModuleDetectionKind.Auto;
    }

    public bool GetResolvePackageJsonExports()
    {
        return ResolvePackageJsonExports.IsTrueOrUnknown();
    }

    public bool GetResolvePackageJsonImports()
    {
        return ResolvePackageJsonImports.IsTrueOrUnknown();
    }

    public bool GetAllowImportingTsExtensions()
    {
        return AllowImportingTsExtensions.IsTrue() || RewriteRelativeImportExtensions.IsTrue();
    }

    public bool AllowImportingTsExtensionsFrom(string fileName)
    {
        return GetAllowImportingTsExtensions() || TsPathApi.IsDeclarationFileName(fileName);
    }

    public bool GetResolveJsonModule()
    {
        if (ResolveJsonModule != Tristate.Unknown)
        {
            return ResolveJsonModule == Tristate.True;
        }
        switch (GetEmitModuleKind())
        {
            // TODO in 6.0: add Node16/Node18
            case ModuleKind.Node20:
            case ModuleKind.NodeNext:
                return true;
        }
        return GetModuleResolutionKind() == ModuleResolutionKind.Bundler;
    }

    public bool ShouldPreserveConstEnums()
    {
        return PreserveConstEnums == Tristate.True || GetIsolatedModules();
    }

    public bool GetAllowJS()
    {
        if (AllowJs != Tristate.Unknown)
        {
            return AllowJs == Tristate.True;
        }
        return CheckJs == Tristate.True;
    }

    public bool GetJSXTransformEnabled()
    {
        var jsx = Jsx;
        return jsx == JsxEmit.React || jsx == JsxEmit.ReactJSX || jsx == JsxEmit.ReactJSXDev;
    }

    public bool GetStrictOptionValue(Tristate value)
    {
        if (value != Tristate.Unknown)
        {
            return value == Tristate.True;
        }
        return Strict != Tristate.False;
    }

    // Go: func (...) GetEffectiveTypeRoots(currentDirectory string) (result []string, fromConfig bool).
    // Non-ok multi-return → tuple (05-conventions.md §4.1).
    public (string[] Result, bool FromConfig) GetEffectiveTypeRoots(string currentDirectory)
    {
        if (TypeRoots != null)
        {
            return (TypeRoots, true);
        }
        string baseDir;
        if (ConfigFilePath != "")
        {
            baseDir = TsPathApi.GetDirectoryPath(ConfigFilePath);
        }
        else
        {
            baseDir = currentDirectory;
            if (baseDir == "")
            {
                // This was accounted for in the TS codebase, but only for third-party API usage
                // where the module resolution host does not provide a getCurrentDirectory().
                throw new InvalidOperationException("cannot get effective type roots without a config file path or current directory");
            }
        }

        // Go: make([]string, 0, strings.Count(baseDir, "/")) — capacity hint only.
        var typeRoots = new List<string>(baseDir.AsSpan().Count('/'));
        TsPathApi.ForEachAncestorDirectory<object?>(baseDir, dir =>
        {
            typeRoots.Add(TsPathApi.CombinePaths(dir, "node_modules", "@types"));
            return (null, false);
        });
        return (typeRoots.ToArray(), false);
    }

    // UsesWildcardTypes returns true if this option's types array includes "*"
    public bool UsesWildcardTypes()
    {
        // Go: slices.Contains(nil, "*") == false; Array.IndexOf uses ordinal equality for string.
        return Types is not null && Array.IndexOf(Types, "*") >= 0;
    }

    public bool GetIsolatedModules()
    {
        return IsolatedModules == Tristate.True || VerbatimModuleSyntax == Tristate.True;
    }

    public bool IsIncremental()
    {
        return Incremental.IsTrue() || Composite.IsTrue();
    }

    public bool GetEmitStandardClassFields()
    {
        return UseDefineForClassFields != Tristate.False && GetEmitScriptTarget() >= ScriptTarget.ES2022;
    }

    public bool GetUseDefineForClassFields()
    {
        if (UseDefineForClassFields == Tristate.Unknown)
        {
            return GetEmitScriptTarget() >= ScriptTarget.ES2022;
        }
        return UseDefineForClassFields == Tristate.True;
    }

    public bool GetEmitDeclarations()
    {
        return Declaration.IsTrue() || Composite.IsTrue();
    }

    public bool GetAreDeclarationMapsEnabled()
    {
        return DeclarationMap == Tristate.True && GetEmitDeclarations();
    }

    public bool HasJsonModuleEmitEnabled()
    {
        switch (GetEmitModuleKind())
        {
            case ModuleKind.System:
            case ModuleKind.UMD:
                return false;
        }
        return true;
    }

    public string GetPathsBasePath(string currentDirectory)
    {
        // Paths is a nil-tolerant OrderedMap receiver; Size() handles null (Ts.Collections).
        if (Paths.Size() == 0)
        {
            return "";
        }
        if (PathsBasePath != "")
        {
            return PathsBasePath;
        }
        return currentDirectory;
    }
}

// Package-level vars/funcs of compileroptions.go collect onto `Core` (the file-named
// static class convention, 05-conventions.md §2.1/§1.5; matches NodeModules.cs). None of
// these names is defined in core.go, so there is no overlap with that file's owner.
public static partial class Core
{
    public static readonly CompilerOptions EmptyCompilerOptions = new();

    // Go: var ModuleKindToModuleResolutionKind = map[ModuleKind]ModuleResolutionKind{...}
    // A fixed two-entry lookup; FrozenDictionary mirrors the NodeModules.cs FrozenSet choice.
    public static readonly FrozenDictionary<ModuleKind, ModuleResolutionKind> ModuleKindToModuleResolutionKind =
        new Dictionary<ModuleKind, ModuleResolutionKind>
        {
            [ModuleKind.Node16] = ModuleResolutionKind.Node16,
            [ModuleKind.NodeNext] = ModuleResolutionKind.NodeNext,
        }.ToFrozenDictionary();

    // Go: const ( ResolutionModeNone = ModuleKindNone; ... ) — the type alias
    // `type ResolutionMode = ModuleKind` lives in GlobalUsings.cs (05-conventions.md §2.3).
    public const ResolutionMode ResolutionModeNone = ModuleKind.None;
    public const ResolutionMode ResolutionModeCommonJS = ModuleKind.CommonJS;
    public const ResolutionMode ResolutionModeESM = ModuleKind.ESNext;

    public static NewLineKind GetNewLineKind(string s)
    {
        switch (s)
        {
            case "\r\n":
                return NewLineKind.CRLF;
            case "\n":
                return NewLineKind.LF;
            default:
                return NewLineKind.None;
        }
    }
}

public enum ModuleDetectionKind
{
    None = 0,
    Auto = 1,
    Legacy = 2,
    Force = 3,
}

public enum ModuleKind
{
    None = 0,
    CommonJS = 1,
    // Deprecated: Do not use outside of options parsing and validation.
    AMD = 2,
    // Deprecated: Do not use outside of options parsing and validation.
    UMD = 3,
    // Deprecated: Do not use outside of options parsing and validation.
    System = 4,
    // NOTE: ES module kinds should be contiguous to more easily check whether a module kind is *any* ES module kind.
    //       Non-ES module kinds should not come between ES2015 (the earliest ES module kind) and ESNext (the last ES
    //       module kind).
    ES2015 = 5,
    ES2020 = 6,
    ES2022 = 7,
    ESNext = 99,
    // Node16+ is an amalgam of commonjs (albeit updated) and es2022+, and represents a distinct module system from es2020/esnext
    Node16 = 100,
    Node18 = 101,
    Node20 = 102,
    NodeNext = 199,
    // Emit as written
    Preserve = 200,
}

public static class ModuleKindExtensions
{
    public static bool IsNonNodeESM(this ModuleKind moduleKind)
    {
        return moduleKind >= ModuleKind.ES2015 && moduleKind <= ModuleKind.ESNext;
    }

    public static bool SupportsImportAttributes(this ModuleKind moduleKind)
    {
        return (ModuleKind.Node18 <= moduleKind && moduleKind <= ModuleKind.NodeNext) ||
            moduleKind == ModuleKind.Preserve ||
            moduleKind == ModuleKind.ESNext;
    }
}

public enum ModuleResolutionKind
{
    Unknown = 0,
    // Deprecated: Do not use outside of options parsing and validation.
    Classic = 1,
    // Deprecated: Do not use outside of options parsing and validation.
    Node10 = 2,
    // Starting with node16, node's module resolver has significant departures from traditional cjs resolution
    // to better support ECMAScript modules and their use within node - however more features are still being added.
    // TypeScript's Node ESM support was introduced after Node 12 went end-of-life, and Node 14 is the earliest stable
    // version that supports both pattern trailers - *but*, Node 16 is the first version that also supports ECMAScript 2022.
    // In turn, we offer both a `NodeNext` moving resolution target, and a `Node16` version-anchored resolution target
    Node16 = 3,
    NodeNext = 99, // Not simply `Node16` so that compiled code linked against TS can use the `Next` value reliably (same as with `ModuleKind`)
    Bundler = 100,
}

public static class ModuleResolutionKindExtensions
{
    // We don't use stringer on this for now, because these values
    // are user-facing in --traceResolution, and stringer currently
    // lacks the ability to remove the "ModuleResolutionKind" prefix
    // when generating code for multiple types into the same output
    // file. Additionally, since there's no TS equivalent of
    // `ModuleResolutionKindUnknown`, we want to panic on that case,
    // as it probably represents a mistake when porting TS to Go.
    //
    // PORT-NOTE: Go's hand-written String() (compileroptions.go:459-476). Named
    // ToDisplayString() per plan/07 §6; this enum is deliberately not a generated
    // stringer (plan/04 §7.2 does not list it), so this hand port is the permanent home.
    public static string ToDisplayString(this ModuleResolutionKind m)
    {
        switch (m)
        {
            case ModuleResolutionKind.Unknown:
                throw new InvalidOperationException("should not use zero value of ModuleResolutionKind");
            case ModuleResolutionKind.Classic:
                return "Classic";
            case ModuleResolutionKind.Node10:
                return "Node10";
            case ModuleResolutionKind.Node16:
                return "Node16";
            case ModuleResolutionKind.NodeNext:
                return "NodeNext";
            case ModuleResolutionKind.Bundler:
                return "Bundler";
            default:
                throw new InvalidOperationException("unhandled case in ModuleResolutionKind.String");
        }
    }

    // core/modulekind_stringer_generated.go — faithful port of the generated `ModuleKind.String()`
    // (trimprefix ModuleKind). Unlike ModuleResolutionKind, Go generates this stringer, so it returns
    // the `ModuleKind(N)` fallback for unknown values rather than panicking.
    public static string ToDisplayString(this ModuleKind i)
    {
        switch (i)
        {
            case ModuleKind.None: return "None";
            case ModuleKind.CommonJS: return "CommonJS";
            case ModuleKind.AMD: return "AMD";
            case ModuleKind.UMD: return "UMD";
            case ModuleKind.System: return "System";
            case ModuleKind.ES2015: return "ES2015";
            case ModuleKind.ES2020: return "ES2020";
            case ModuleKind.ES2022: return "ES2022";
            case ModuleKind.ESNext: return "ESNext";
            case ModuleKind.Node16: return "Node16";
            case ModuleKind.Node18: return "Node18";
            case ModuleKind.Node20: return "Node20";
            case ModuleKind.NodeNext: return "NodeNext";
            case ModuleKind.Preserve: return "Preserve";
            default: return "ModuleKind(" + ((int)i).ToString(global::System.Globalization.CultureInfo.InvariantCulture) + ")";
        }
    }
}

public enum NewLineKind
{
    None = 0,
    CRLF = 1,
    LF = 2,
}

public static class NewLineKindExtensions
{
    public static string GetNewLineCharacter(this NewLineKind newLine)
    {
        switch (newLine)
        {
            case NewLineKind.CRLF:
                return "\r\n";
            default:
                return "\n";
        }
    }
}

public enum ScriptTarget
{
    None = 0,
    // Deprecated: Do not use outside of options parsing and validation.
    ES5 = 1,
    ES2015 = 2,
    ES2016 = 3,
    ES2017 = 4,
    ES2018 = 5,
    ES2019 = 6,
    ES2020 = 7,
    ES2021 = 8,
    ES2022 = 9,
    ES2023 = 10,
    ES2024 = 11,
    ES2025 = 12,
    ESNext = 99,
    JSON = 100,
    Latest = ESNext,
    LatestStandard = ES2025,
}

public enum JsxEmit
{
    None = 0,
    Preserve = 1,
    ReactNative = 2,
    React = 3,
    ReactJSX = 4,
    ReactJSXDev = 5,
}

public static class JsxEmitExtensions
{
    // PORT-NOTE: Go's hand-written String() (compileroptions.go:540-557). Named
    // ToDisplayString() per plan/07 §6; not a generated stringer (plan/04 §7.2).
    public static string ToDisplayString(this JsxEmit j)
    {
        switch (j)
        {
            case JsxEmit.None:
                throw new InvalidOperationException("should not use zero value of JsxEmit");
            case JsxEmit.Preserve:
                return "preserve";
            case JsxEmit.ReactNative:
                return "react-native";
            case JsxEmit.React:
                return "react";
            case JsxEmit.ReactJSX:
                return "react-jsx";
            case JsxEmit.ReactJSXDev:
                return "react-jsxdev";
            default:
                throw new InvalidOperationException("unhandled case in JsxEmit.String");
        }
    }
}
