// Ported from tsgo internal/ls/autoimport/specifiers.go (whole file): module specifier
// computation for auto-import fixes, backed by the registry entrypoints and per-file
// specifier cache.

using System;
using Ts.Collections;

namespace Ts.Ls.AutoImport;

public sealed partial class View
{
    // specifiers.go:9-75 — GetModuleSpecifier.
    public (string Specifier, global::Ts.ModuleSpecifiers.ResultKind Kind) GetModuleSpecifier(
        Export export,
        global::Ts.ModuleSpecifiers.UserPreferences userPreferences)
    {
        // Ambient module
        if (global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.PathIsBareSpecifier(export.ModuleID.Value))
        {
            var ambientSpecifier = export.ModuleID.Value;
            if (global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.IsExcludedByRegex(ambientSpecifier, userPreferences.AutoImportSpecifierExcludeRegexes))
            {
                return ("", global::Ts.ModuleSpecifiers.ResultKind.None);
            }
            return (export.ModuleID.Value, global::Ts.ModuleSpecifiers.ResultKind.Ambient);
        }

        if (export.PackageName.Length != 0)
        {
            if (registry.entrypoints != null && registry.entrypoints.TryGetValue(export.Path, out var entrypoints))
            {
                foreach (var entrypoint in entrypoints)
                {
                    if (entrypoint.IncludeConditions.IsSubsetOf(conditions) && !conditions.Intersects(entrypoint.ExcludeConditions))
                    {
                        var entrypointSpecifier = global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.ProcessEntrypointEnding(
                            entrypoint,
                            userPreferences,
                            program,
                            program.Options(),
                            importingFile,
                            GetAllowedEndings());

                        if (!global::Ts.ModuleSpecifiers.ModuleSpecifierUtil.IsExcludedByRegex(entrypointSpecifier, userPreferences.AutoImportSpecifierExcludeRegexes))
                        {
                            return (entrypointSpecifier, global::Ts.ModuleSpecifiers.ResultKind.NodeModules);
                        }
                    }
                }
                return ("", global::Ts.ModuleSpecifiers.ResultKind.None);
            }
        }

        global::Ts.Collections.SyncMap<global::Ts.TsPath.Path, string>? cache = null;
        registry.specifierCache?.TryGetValue(importingFile.Path(), out cache);
        if (export.PackageName.Length == 0)
        {
            if (cache != null && cache.TryLoad(export.Path, out var cachedSpecifier))
            {
                if (cachedSpecifier.Length == 0)
                {
                    return ("", global::Ts.ModuleSpecifiers.ResultKind.None);
                }
                return (cachedSpecifier, global::Ts.ModuleSpecifiers.ResultKind.Relative);
            }
        }

        var (specifiers, kind) = global::Ts.ModuleSpecifiers.Specifiers.GetModuleSpecifiersForFileWithInfo(
            importingFile,
            export.ModuleFileName,
            program.Options(),
            program,
            userPreferences,
            new global::Ts.ModuleSpecifiers.ModuleSpecifierOptions(),
            true);
        // !!! unsure when this could return multiple specifiers combined with the
        //     new node_modules code. Possibly with local symlinks, which should be
        //     very rare.
        foreach (var specifier in specifiers)
        {
            if (specifier.Contains("/node_modules/", StringComparison.Ordinal))
            {
                continue;
            }
            cache?.Store(export.Path, specifier);
            return (specifier, kind);
        }
        cache?.Store(export.Path, "");
        return ("", global::Ts.ModuleSpecifiers.ResultKind.None);
    }
}
