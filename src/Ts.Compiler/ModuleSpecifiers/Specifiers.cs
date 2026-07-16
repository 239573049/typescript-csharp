// Ported from tsgo internal/modulespecifiers/specifiers.go — the module-specifier generation core.
// PORT-NOTE (in progress): leaf/support functions land first; the GetModuleSpecifiers entry chain
// (computeModuleSpecifiers / getLocalModuleSpecifier / package.json exports-imports resolution) and the
// wiring of nodeBuilder.getSpecifierForModuleSymbol follow in subsequent batches.

using System;

using Ts.Core;

using TsPathApi = global::Ts.TsPath.TsPath;

namespace Ts.ModuleSpecifiers;

public static partial class Specifiers
{
    // specifiers.go:157 — Info.
    internal struct Info
    {
        public bool UseCaseSensitiveFileNames;
        public string ImportingSourceFileName;
        public string SourceDirectory;
    }

    // specifiers.go:163 — getInfo.
    internal static Info GetInfo(string importingSourceFileName, ModuleSpecifierGenerationHost host)
    {
        string sourceDirectory = TsPathApi.GetDirectoryPath(importingSourceFileName);
        return new Info
        {
            ImportingSourceFileName = importingSourceFileName,
            SourceDirectory = sourceDirectory,
            UseCaseSensitiveFileNames = host.UseCaseSensitiveFileNames(),
        };
    }

    // specifiers.go:247 — containsIgnoredPath. Local helper duplicating tspath.ContainsIgnoredPath for
    // performance.
    internal static bool ContainsIgnoredPath(string s) =>
        s.Contains("/node_modules/.", StringComparison.Ordinal) ||
        s.Contains("/.git", StringComparison.Ordinal) ||
        s.Contains(".#", StringComparison.Ordinal);

    // specifiers.go:254 — ContainsNodeModules.
    public static bool ContainsNodeModules(string s) =>
        s.Contains("/node_modules/", StringComparison.Ordinal);
}
