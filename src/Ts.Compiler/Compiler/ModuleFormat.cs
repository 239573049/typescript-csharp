// PORT-NOTE / DEVIATION (registered): compiler-local port of the module-format helpers from
// tsgo internal/ast/parseoptions.go (GetExternalModuleIndicatorOptions,
// isFileForcedToBeModuleByFormat) and internal/ast/utilities.go (GetImpliedNodeFormatForFile,
// GetEmitModuleFormatOfFileWorker, GetImpliedNodeFormatForEmitWorker).
//
// These live in the ast package in Go, but the C# Ast port explicitly deferred them
// (see Ast/ParseOptions.cs header: "they land with the utilities.go port"). The file-loader
// pipeline needs them NOW to compute SourceFileParseOptions + SourceFileMetaData exactly as
// tsgo does. This is a faithful 1:1 port placed here temporarily to unblock the loader; when
// the Ts.Ast port lands these, this file is deleted and callers redirect to Ts.Ast. Behavior
// is identical, so no baseline divergence.
//
// §1.5 (namespace shadowing): the sibling-namespace static classes Core and TsPath are written
// global::Ts.Core.Core / global::Ts.TsPath.TsPath; CoreUtil/EnumMaps are not namespace names.

using System;
using Ts.Ast;
using Ts.Core;

namespace Ts.Compiler;

internal static class ModuleFormat
{
    // Bridges Go's `module.GetCompilerOptionsWithRedirect(options, *ParsedCommandLine)`
    // (resolver.go:145-153): with a redirect, the referenced project's own options win.
    public static CompilerOptions CompilerOptionsForRedirect(CompilerOptions options, global::Ts.TsOptions.ParsedCommandLine? redirect)
    {
        if (redirect is null)
        {
            return options;
        }
        return redirect.CompilerOptions() ?? options;
    }

    // parseoptions.go:44
    private static readonly string[] IsFileForcedToBeModuleByFormatExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionCjs,
        global::Ts.TsPath.TsPath.ExtensionCts,
        global::Ts.TsPath.TsPath.ExtensionMjs,
        global::Ts.TsPath.TsPath.ExtensionMts,
    ];

    private static readonly string[] EsmExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionDmts,
        global::Ts.TsPath.TsPath.ExtensionMts,
        global::Ts.TsPath.TsPath.ExtensionMjs,
    ];

    private static readonly string[] CjsExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionDcts,
        global::Ts.TsPath.TsPath.ExtensionCts,
        global::Ts.TsPath.TsPath.ExtensionCjs,
    ];

    private static readonly string[] AmbiguousExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionDts,
        global::Ts.TsPath.TsPath.ExtensionTs,
        global::Ts.TsPath.TsPath.ExtensionTsx,
        global::Ts.TsPath.TsPath.ExtensionJs,
        global::Ts.TsPath.TsPath.ExtensionJsx,
    ];

    private static readonly string[] CjsOnlyExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionCjs,
        global::Ts.TsPath.TsPath.ExtensionCts,
    ];

    private static readonly string[] MjsOnlyExtensions =
    [
        global::Ts.TsPath.TsPath.ExtensionMjs,
        global::Ts.TsPath.TsPath.ExtensionMts,
    ];

    // parseoptions.go:19-42
    public static ExternalModuleIndicatorOptions GetExternalModuleIndicatorOptions(string fileName, CompilerOptions options, SourceFileMetaData metadata)
    {
        if (global::Ts.TsPath.TsPath.IsDeclarationFileName(fileName))
        {
            return default;
        }

        switch (options.GetEmitModuleDetectionKind())
        {
            case ModuleDetectionKind.Force:
                // All non-declaration files are modules, declaration files still do the usual isFileProbablyExternalModule
                return new ExternalModuleIndicatorOptions { Force = true };
            case ModuleDetectionKind.Legacy:
                // Files are modules if they have imports, exports, or import.meta
                return default;
            case ModuleDetectionKind.Auto:
                // If module is nodenext or node16, all esm format files are modules
                // If jsx is react-jsx or react-jsxdev then jsx tags force module-ness
                // otherwise, the presence of import or export statements (or import.meta) implies module-ness
                return new ExternalModuleIndicatorOptions
                {
                    Jsx = options.Jsx == JsxEmit.ReactJSX || options.Jsx == JsxEmit.ReactJSXDev,
                    Force = IsFileForcedToBeModuleByFormat(fileName, options, metadata),
                };
            default:
                return default;
        }
    }

    // parseoptions.go:46-54
    private static bool IsFileForcedToBeModuleByFormat(string fileName, CompilerOptions options, SourceFileMetaData metadata)
    {
        // Excludes declaration files - they still require an explicit `export {}` or the like
        // for back compat purposes. The only non-declaration files _not_ forced to be a module are `.js` files
        // that aren't esm-mode (meaning not in a `type: module` scope).
        return GetImpliedNodeFormatForEmitWorker(fileName, options.GetEmitModuleKind(), metadata) == ModuleKind.ESNext ||
            global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, IsFileForcedToBeModuleByFormatExtensions);
    }

    // utilities.go:2533-2544
    public static ModuleKind GetImpliedNodeFormatForFile(string path, string packageJsonType)
    {
        var impliedNodeFormat = global::Ts.Core.Core.ResolutionModeNone;
        if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(path, EsmExtensions))
        {
            impliedNodeFormat = global::Ts.Core.Core.ResolutionModeESM;
        }
        else if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(path, CjsExtensions))
        {
            impliedNodeFormat = global::Ts.Core.Core.ResolutionModeCommonJS;
        }
        else if (global::Ts.TsPath.TsPath.FileExtensionIsOneOf(path, AmbiguousExtensions))
        {
            impliedNodeFormat = packageJsonType == "module" ? global::Ts.Core.Core.ResolutionModeESM : global::Ts.Core.Core.ResolutionModeCommonJS;
        }
        return impliedNodeFormat;
    }

    // utilities.go:2546-2552
    public static ModuleKind GetEmitModuleFormatOfFileWorker(string fileName, CompilerOptions options, SourceFileMetaData sourceFileMetaData)
    {
        var result = GetImpliedNodeFormatForEmitWorker(fileName, options.GetEmitModuleKind(), sourceFileMetaData);
        if (result != ModuleKind.None)
        {
            return result;
        }
        return options.GetEmitModuleKind();
    }

    // utilities.go:2554-2569
    public static ResolutionMode GetImpliedNodeFormatForEmitWorker(string fileName, ModuleKind emitModuleKind, SourceFileMetaData sourceFileMetaData)
    {
        if (ModuleKind.Node16 <= emitModuleKind && emitModuleKind <= ModuleKind.NodeNext)
        {
            return sourceFileMetaData.ImpliedNodeFormat;
        }
        if (sourceFileMetaData.ImpliedNodeFormat == ModuleKind.CommonJS &&
            (sourceFileMetaData.PackageJsonType == "commonjs" ||
                global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, CjsOnlyExtensions)))
        {
            return ModuleKind.CommonJS;
        }
        if (sourceFileMetaData.ImpliedNodeFormat == ModuleKind.ESNext &&
            (sourceFileMetaData.PackageJsonType == "module" ||
                global::Ts.TsPath.TsPath.FileExtensionIsOneOf(fileName, MjsOnlyExtensions)))
        {
            return ModuleKind.ESNext;
        }
        return ModuleKind.None;
    }
}
