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

// modulespecifiers/types.go:13 — SourceFileForSpecifierGeneration.
public interface SourceFileForSpecifierGeneration
{
    global::Ts.TsPath.Path Path();
    string FileName();
    StringLiteralLikeNode[]? Imports();
    bool IsJS();
}

// modulespecifiers/types.go:20 — CheckerShape.
public interface CheckerShape
{
    Symbol? GetSymbolAtLocation(Node node);
    Symbol? GetAliasedSymbol(Symbol symbol);
}

// modulespecifiers/types.go:25 — ResultKind.
public enum ResultKind : byte
{
    None,
    NodeModules,
    Paths,
    Redirect,
    Relative,
    Ambient,
}

// modulespecifiers/types.go:35 — ModulePath.
public struct ModulePath
{
    public string FileName;
    public bool IsInNodeModules;
    public bool IsRedirect;
}

// modulespecifiers/types.go:64 — ImportModuleSpecifierPreference.
public readonly record struct ImportModuleSpecifierPreference(string Value)
{
    public static readonly ImportModuleSpecifierPreference None = new("");
    public static readonly ImportModuleSpecifierPreference Shortest = new("shortest");
    public static readonly ImportModuleSpecifierPreference ProjectRelative = new("project-relative");
    public static readonly ImportModuleSpecifierPreference Relative = new("relative");
    public static readonly ImportModuleSpecifierPreference NonRelative = new("non-relative");
}

// modulespecifiers/types.go:74 — ImportModuleSpecifierEndingPreference.
public readonly record struct ImportModuleSpecifierEndingPreference(string Value)
{
    public static readonly ImportModuleSpecifierEndingPreference None = new("");
    public static readonly ImportModuleSpecifierEndingPreference Auto = new("auto");
    public static readonly ImportModuleSpecifierEndingPreference Minimal = new("minimal");
    public static readonly ImportModuleSpecifierEndingPreference Index = new("index");
    public static readonly ImportModuleSpecifierEndingPreference Js = new("js");
}

// modulespecifiers/types.go:84 — UserPreferences.
public struct UserPreferences
{
    public ImportModuleSpecifierPreference ImportModuleSpecifierPreference;
    public ImportModuleSpecifierEndingPreference ImportModuleSpecifierEnding;
    public string[]? AutoImportSpecifierExcludeRegexes;
}

// modulespecifiers/types.go:90 — ModuleSpecifierOptions.
public struct ModuleSpecifierOptions
{
    public ResolutionMode OverrideImportMode;
}

// modulespecifiers/types.go:94 — RelativePreferenceKind.
public enum RelativePreferenceKind : byte
{
    Relative,
    NonRelative,
    Shortest,
    ExternalNonRelative,
}

// modulespecifiers/types.go:103 — ModuleSpecifierEnding.
public enum ModuleSpecifierEnding : byte
{
    Minimal,
    Index,
    JsExtension,
    TsExtension,
}

// modulespecifiers/types.go:112 — MatchingMode.
public enum MatchingMode : byte
{
    Exact,
    Directory,
    Pattern,
}
