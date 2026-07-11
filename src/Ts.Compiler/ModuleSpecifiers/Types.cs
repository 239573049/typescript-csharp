// Ported from tsgo internal/modulespecifiers/types.go:42-62 (ModuleSpecifierGenerationHost).
// MINIMAL-BUILD for the checker skeleton: the checker's Host interface embeds this, and the
// full Ts.ModuleSpecifiers package is not yet ported. Interface transcribed faithfully; all
// referenced types already exist (symlinks/tsoptions/packagejson/module/ast/core).

using Ts.Ast;

namespace Ts.ModuleSpecifiers;

// modulespecifiers/types.go:42-62
public interface ModuleSpecifierGenerationHost
{
    global::Ts.Symlinks.KnownSymlinks GetSymlinkCache();

    string CommonSourceDirectory();

    string GetGlobalTypingsCacheLocation();

    bool UseCaseSensitiveFileNames();

    string GetCurrentDirectory();

    global::Ts.TsOptions.SourceOutputAndProjectReference? GetProjectReferenceFromSource(global::Ts.TsPath.Path path);

    string[] GetRedirectTargets(global::Ts.TsPath.Path path);

    string GetSourceOfProjectReferenceIfOutputIncluded(HasFileName file);

    bool FileExists(string path);

    string GetNearestAncestorDirectoryWithPackageJson(string dirname);

    global::Ts.PackageJson.InfoCacheEntry? GetPackageJsonInfo(string pkgJsonPath);

    ResolutionMode GetDefaultResolutionModeForFile(HasFileName file);

    global::Ts.Module.ResolvedModule? GetResolvedModuleFromModuleSpecifier(HasFileName file, StringLiteralLikeNode? moduleSpecifier);

    ResolutionMode GetModeForUsageLocation(HasFileName file, StringLiteralLikeNode? moduleSpecifier);
}
