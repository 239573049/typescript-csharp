// Ported from tsgo internal/modulespecifiers/preferences.go — module-specifier ending/relative
// preference inference.

using System;

using Ts.Ast;
using Ts.Core;
using Ts.TsPath;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class Specifiers
{
    // preferences.go:14 — shouldAllowImportingTsExtension.
    internal static bool ShouldAllowImportingTsExtension(CompilerOptions compilerOptions, string fromFileName) =>
        compilerOptions.GetAllowImportingTsExtensions() || (fromFileName.Length > 0 && TsPathApi.IsDeclarationFileName(fromFileName));

    // preferences.go:18 — usesExtensionsOnImports.
    internal static bool UsesExtensionsOnImports(SourceFileForSpecifierGeneration file)
    {
        foreach (Node reference in file.Imports() ?? [])
        {
            string text = reference.Text();
            if (TsPathApi.PathIsRelative(text) && !TsPathApi.FileExtensionIsOneOf(text, TsPathApi.ExtensionsNotSupportingExtensionlessResolution))
            {
                return TsPathApi.HasTSFileExtension(text) || TsPathApi.HasJSFileExtension(text);
            }
        }
        return false;
    }

    // preferences.go:28 — inferPreference.
    internal static ModuleSpecifierEnding InferPreference(ResolutionMode resolutionMode, SourceFileForSpecifierGeneration? sourceFile, bool moduleResolutionIsNodeNext)
    {
        bool usesJsExtensions = false;
        Node[] specifiers = [];
        if (sourceFile != null && (sourceFile.Imports()?.Length ?? 0) > 0)
        {
            specifiers = sourceFile.Imports()!;
        }
        else if (sourceFile != null && sourceFile.IsJS())
        {
            // !!! TODO: JS support (requires at top of file)
        }

        foreach (Node specifier in specifiers)
        {
            string path = specifier.Text();
            if (TsPathApi.PathIsRelative(path))
            {
                // !!! TODO: proper resolutionMode support
                if (moduleResolutionIsNodeNext && resolutionMode == global::Ts.Core.Core.ResolutionModeCommonJS)
                {
                    // Deciding a preference for a CommonJS specifier but looking at an ESM import.
                    continue;
                }
                if (TsPathApi.FileExtensionIsOneOf(path, TsPathApi.ExtensionsNotSupportingExtensionlessResolution))
                {
                    // These extensions are not optional, so do not indicate a preference.
                    continue;
                }
                if (TsPathApi.HasTSFileExtension(path))
                {
                    return ModuleSpecifierEnding.TsExtension;
                }
                if (TsPathApi.HasJSFileExtension(path))
                {
                    usesJsExtensions = true;
                }
            }
        }

        return usesJsExtensions ? ModuleSpecifierEnding.JsExtension : ModuleSpecifierEnding.Minimal;
    }

    // preferences.go:69 — getModuleSpecifierEndingPreference.
    internal static ModuleSpecifierEnding GetModuleSpecifierEndingPreference(
        ImportModuleSpecifierEndingPreference pref,
        ResolutionMode resolutionMode,
        CompilerOptions compilerOptions,
        SourceFileForSpecifierGeneration sourceFile)
    {
        ModuleResolutionKind moduleResolution = compilerOptions.GetModuleResolutionKind();
        bool moduleResolutionIsNodeNext = ModuleResolutionKind.Node16 <= moduleResolution && moduleResolution <= ModuleResolutionKind.NodeNext;

        if (pref == ImportModuleSpecifierEndingPreference.Js || (resolutionMode == global::Ts.Core.Core.ResolutionModeESM && moduleResolutionIsNodeNext))
        {
            // Extensions are explicitly requested or required. Choose between .js and .ts.
            if (!ShouldAllowImportingTsExtension(compilerOptions, ""))
            {
                return ModuleSpecifierEnding.JsExtension;
            }
            // `allowImportingTsExtensions` is a strong signal; use .ts unless the file already uses .js
            // extensions and no .ts extensions.
            if (InferPreference(resolutionMode, sourceFile, moduleResolutionIsNodeNext) != ModuleSpecifierEnding.JsExtension)
            {
                return ModuleSpecifierEnding.TsExtension;
            }
            return ModuleSpecifierEnding.JsExtension;
        }

        if (pref == ImportModuleSpecifierEndingPreference.Minimal)
        {
            return ModuleSpecifierEnding.Minimal;
        }
        if (pref == ImportModuleSpecifierEndingPreference.Index)
        {
            return ModuleSpecifierEnding.Index;
        }

        // No preference specified. Guess from imports/requires.
        if (!ShouldAllowImportingTsExtension(compilerOptions, ""))
        {
            if (sourceFile != null && UsesExtensionsOnImports(sourceFile))
            {
                return ModuleSpecifierEnding.JsExtension;
            }
            return ModuleSpecifierEnding.Minimal;
        }

        return InferPreference(resolutionMode, sourceFile, moduleResolutionIsNodeNext);
    }

    // preferences.go:114 — getPreferredEnding.
    internal static ModuleSpecifierEnding GetPreferredEnding(
        UserPreferences prefs,
        ModuleSpecifierGenerationHost host,
        CompilerOptions compilerOptions,
        SourceFileForSpecifierGeneration importingSourceFile,
        string oldImportSpecifier,
        ResolutionMode resolutionMode)
    {
        if (oldImportSpecifier.Length > 0)
        {
            if (TsPathApi.HasJSFileExtension(oldImportSpecifier))
            {
                return ModuleSpecifierEnding.JsExtension;
            }
            if (oldImportSpecifier.EndsWith("/index", StringComparison.Ordinal))
            {
                return ModuleSpecifierEnding.Index;
            }
        }
        if (resolutionMode == global::Ts.Core.Core.ResolutionModeNone)
        {
            resolutionMode = host.GetDefaultResolutionModeForFile(importingSourceFile);
        }
        return GetModuleSpecifierEndingPreference(prefs.ImportModuleSpecifierEnding, resolutionMode, compilerOptions, importingSourceFile);
    }

    // preferences.go:141 — ModuleSpecifierPreferences.
    internal struct ModuleSpecifierPreferences
    {
        public RelativePreferenceKind RelativePreference;
        public Func<ResolutionMode, ModuleSpecifierEnding[]> GetAllowedEndingsInPreferredOrder;
        public string[]? ExcludeRegexes;
    }

    // preferences.go:147 — GetAllowedEndingsInPreferredOrder.
    public static ModuleSpecifierEnding[] GetAllowedEndingsInPreferredOrder(
        UserPreferences prefs,
        ModuleSpecifierGenerationHost host,
        CompilerOptions compilerOptions,
        SourceFileForSpecifierGeneration importingSourceFile,
        string oldImportSpecifier,
        ResolutionMode syntaxImpliedNodeFormat)
    {
        ModuleSpecifierEnding preferredEnding = GetPreferredEnding(prefs, host, compilerOptions, importingSourceFile, oldImportSpecifier, global::Ts.Core.Core.ResolutionModeNone);
        ResolutionMode resolutionMode = host.GetDefaultResolutionModeForFile(importingSourceFile);
        if (resolutionMode != syntaxImpliedNodeFormat)
        {
            preferredEnding = GetPreferredEnding(prefs, host, compilerOptions, importingSourceFile, oldImportSpecifier, syntaxImpliedNodeFormat);
        }
        ModuleResolutionKind moduleResolution = compilerOptions.GetModuleResolutionKind();
        bool moduleResolutionIsNodeNext = ModuleResolutionKind.Node16 <= moduleResolution && moduleResolution <= ModuleResolutionKind.NodeNext;
        bool allowImportingTsExtension = ShouldAllowImportingTsExtension(compilerOptions, importingSourceFile.FileName());
        if (syntaxImpliedNodeFormat == global::Ts.Core.Core.ResolutionModeESM && moduleResolutionIsNodeNext)
        {
            return allowImportingTsExtension
                ? [ModuleSpecifierEnding.TsExtension, ModuleSpecifierEnding.JsExtension]
                : [ModuleSpecifierEnding.JsExtension];
        }
        switch (preferredEnding)
        {
            case ModuleSpecifierEnding.JsExtension:
                return allowImportingTsExtension
                    ? [ModuleSpecifierEnding.JsExtension, ModuleSpecifierEnding.TsExtension, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index]
                    : [ModuleSpecifierEnding.JsExtension, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index];
            case ModuleSpecifierEnding.TsExtension:
                return [ModuleSpecifierEnding.TsExtension, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.JsExtension, ModuleSpecifierEnding.Index];
            case ModuleSpecifierEnding.Index:
                return allowImportingTsExtension
                    ? [ModuleSpecifierEnding.Index, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.TsExtension, ModuleSpecifierEnding.JsExtension]
                    : [ModuleSpecifierEnding.Index, ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.JsExtension];
            case ModuleSpecifierEnding.Minimal:
                return allowImportingTsExtension
                    ? [ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index, ModuleSpecifierEnding.TsExtension, ModuleSpecifierEnding.JsExtension]
                    : [ModuleSpecifierEnding.Minimal, ModuleSpecifierEnding.Index, ModuleSpecifierEnding.JsExtension];
            default:
                global::Ts.Core.Debug.AssertNever(preferredEnding);
                break;
        }
        return [ModuleSpecifierEnding.Minimal];
    }

    // preferences.go:207 — getModuleSpecifierPreferences.
    internal static ModuleSpecifierPreferences GetModuleSpecifierPreferences(
        UserPreferences prefs,
        ModuleSpecifierGenerationHost host,
        CompilerOptions compilerOptions,
        SourceFileForSpecifierGeneration importingSourceFile,
        string oldImportSpecifier)
    {
        string[]? excludes = prefs.AutoImportSpecifierExcludeRegexes;
        RelativePreferenceKind relativePreference = RelativePreferenceKind.Shortest;
        if (oldImportSpecifier.Length > 0)
        {
            relativePreference = TsPathApi.IsExternalModuleNameRelative(oldImportSpecifier)
                ? RelativePreferenceKind.Relative
                : RelativePreferenceKind.NonRelative;
        }
        else
        {
            if (prefs.ImportModuleSpecifierPreference == ImportModuleSpecifierPreference.Relative)
            {
                relativePreference = RelativePreferenceKind.Relative;
            }
            else if (prefs.ImportModuleSpecifierPreference == ImportModuleSpecifierPreference.NonRelative)
            {
                relativePreference = RelativePreferenceKind.NonRelative;
            }
            else if (prefs.ImportModuleSpecifierPreference == ImportModuleSpecifierPreference.ProjectRelative)
            {
                relativePreference = RelativePreferenceKind.ExternalNonRelative;
                // all others are shortest
            }
        }

        return new ModuleSpecifierPreferences
        {
            ExcludeRegexes = excludes,
            RelativePreference = relativePreference,
            GetAllowedEndingsInPreferredOrder = syntaxImpliedNodeFormat =>
                GetAllowedEndingsInPreferredOrder(prefs, host, compilerOptions, importingSourceFile, oldImportSpecifier, syntaxImpliedNodeFormat),
        };
    }
}
